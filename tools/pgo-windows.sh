#!/bin/bash
# Profile-guided optimisation of the Windows QEMU build (clang PGO):
#   1. clang's profile runtime for x86_64-w64-windows-gnu (Fedora has none),
#      built from LLVM compiler-rt sources matching the image's clang
#   2. instrumented QEMU (src/qemu/build-win-pgo-gen)
#   3. training run under Wine (tools/pgo-train.py, board from NAND_DIR)
#   4. QEMU rebuilt with the profile (src/qemu/build-win-pgo-use), stripped,
#      copied to OUT_DIR/qemu-system-aarch64.exe
# On real Windows this made guest code ~5-17 % faster.
#   tools/pgo-windows.sh NAND_DIR OUT_DIR   (OUT_DIR holds the QEMU DLLs)
set -e
cd "$(dirname "$(readlink -f "$0")")/.."
ROOT=$PWD
NAND=$(readlink -f "$1"); OUT=$(readlink -f "$2")
W=$ROOT/work/pgo; mkdir -p "$W/rt/windows" "$W/train"
SUDO=; docker info >/dev/null 2>&1 || SUDO=sudo
IMG=qemu-win64-clang
LLVM=$($SUDO docker run --rm $IMG clang -dumpversion)       # e.g. 18.1.8
MAJ=${LLVM%%.*}

# 1. profile runtime
if [ ! -f "$W/rt/windows/libclang_rt.profile-x86_64.a" ]; then
    T=compiler-rt-$LLVM.src
    [ -f "$W/rt/$T.tar.xz" ] || wget -q -O "$W/rt/$T.tar.xz" \
        "https://github.com/llvm/llvm-project/releases/download/llvmorg-$LLVM/$T.tar.xz"
    tar -C "$W/rt" -xf "$W/rt/$T.tar.xz"
    cat > "$W/rt/build.sh" <<EOF
set -e
T="--target=x86_64-w64-windows-gnu --sysroot=/usr/x86_64-w64-mingw32/sys-root/mingw"
S=$T/lib/profile; mkdir -p obj
for f in \$S/*.c; do clang \$T -O2 -fno-builtin -I $T/include -DCOMPILER_RT_HAS_ATOMICS=1 -c \$f -o obj/\$(basename \$f).o; done
clang++ \$T -O2 -fno-exceptions -fno-rtti -I $T/include -c \$S/InstrProfilingRuntime.cpp -o obj/InstrProfilingRuntime.o
x86_64-w64-mingw32-ar rcs windows/libclang_rt.profile-x86_64.a obj/*.o
EOF
    $SUDO docker run --rm -u "$(id -u):$(id -g)" -v "$W/rt:/w" -w /w $IMG bash build.sh
fi

# 2./4. QEMU builds (the runtime is mounted where clang looks for it)
qemu_build() {
    $SUDO docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e STAGE=$1 \
        -v "$ROOT/src/qemu:/src" -v "$W:/rt" \
        -v "$W/rt/windows:/usr/lib/clang/$MAJ/lib/windows" $IMG bash -c '
set -e
SYSROOT=/usr/x86_64-w64-mingw32/sys-root/mingw
GCCLIB=/usr/lib/gcc/x86_64-w64-mingw32/$(ls /usr/lib/gcc/x86_64-w64-mingw32/ | head -1)
CLANG="--target=x86_64-w64-windows-gnu --sysroot=$SYSROOT -fuse-ld=lld"
if [ "$STAGE" = gen ]; then
    PF="-fprofile-instr-generate -fprofile-update=atomic"; PL="-fprofile-instr-generate"
else
    PF="-fprofile-instr-use=/rt/qemu.profdata -Wno-profile-instr-unprofiled -Wno-profile-instr-out-of-date"; PL=""
fi
mkdir -p /src/build-win-pgo-$STAGE && cd /src/build-win-pgo-$STAGE
[ -f build.ninja ] || ../configure --cross-prefix=x86_64-w64-mingw32- \
    --cc="clang $CLANG" --cxx="clang++ $CLANG" --extra-cflags="$PF" \
    --extra-ldflags="-L$GCCLIB $PL" \
    --target-list=aarch64-softmmu --enable-slirp --enable-fdt=internal \
    --enable-libusb \
    --disable-docs --disable-werror --disable-gtk --disable-sdl \
    --disable-vnc --disable-spice --disable-opengl --disable-curl \
    --disable-guest-agent --disable-tools >configure.out 2>&1 ||
    { tail -20 configure.out; exit 1; }
grep -q "CONFIG_USB_LIBUSB 1" config-host.h || ./pyvenv/bin/meson configure -Dlibusb=enabled >/dev/null
# re-read meson.build (new source files) even if build.ninja looks fresh
ninja reconfigure >reconfigure.out 2>&1 || { tail -20 reconfigure.out; exit 1; }
ninja >ninja.out 2>&1 || { grep -A10 FAILED ninja.out | head -40; exit 1; }'
}
echo "PGO: instrumented build"
qemu_build gen

# 3. training under Wine (needs the QEMU DLLs next to the exe)
echo "PGO: training run under Wine"
rm -rf "$W/train" "$W/nand"; mkdir -p "$W/train"
cp "$OUT"/*.dll "$W/train/"
cp "$ROOT/src/qemu/build-win-pgo-gen/qemu-system-aarch64.exe" "$W/train/"
cp -r "$NAND" "$W/nand"
export WINEPREFIX=${WINEPREFIX:-$ROOT/work/wineprefix}
timeout 1800 python3 tools/pgo-train.py "$W/train/qemu-system-aarch64.exe" "$W/nand" \
    "$W/train/qemu-%p.profraw"
wineserver -w 2>/dev/null || true
$SUDO docker run --rm -u "$(id -u):$(id -g)" -v "$W:/rt" -w /rt $IMG \
    sh -c 'llvm-profdata merge -o qemu.profdata train/*.profraw'

echo "PGO: optimised build"
# rebuild everything with the fresh profile (ninja does not track the
# profile file); a changed meson.build still regenerates build.ninja
$SUDO docker run --rm -u "$(id -u):$(id -g)" -v "$ROOT/src/qemu:/src" $IMG \
    sh -c 'cd /src/build-win-pgo-use 2>/dev/null && ninja -t clean >/dev/null || true'
qemu_build use
cp "$ROOT/src/qemu/build-win-pgo-use/qemu-system-aarch64.exe" "$OUT/"
$SUDO docker run --rm -u "$(id -u):$(id -g)" -v "$OUT:/q" $IMG \
    x86_64-w64-mingw32-strip --strip-all /q/qemu-system-aarch64.exe
echo "PGO: $OUT/qemu-system-aarch64.exe"

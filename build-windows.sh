#!/bin/bash
# Build the Windows packages dist/Router-Emulator-<version>-win64.zip
# (no flash folders, for publishing) and dist/Router-Emulator-<version>-dumps.zip
# (only the flash folders, Router-Emulator/nand-*, nor-*, emmc-*: unpacked
# over the package they complete it)
#   - QEMU (with qemu-patches/) cross-compiled in QEMU's Fedora MinGW image
#   - emulator.exe launcher (C#, needs mono-mcs)
#   - board presets (presets/*.ini) and, for every preset with an
#     openwrt=PROFILE key, a fresh NAND folder for OpenWrt $VERSION
#     (default 25.12.5)
#   - QEMU rebuilt with profile-guided optimisation (tools/pgo-windows.sh,
#     PGO=0 to skip; it trains on nand-wr3000p)
#   FLASH_PRESETS="cudy-wr3000p-v1 ..." builds only these flash folders
set -e
cd "$(dirname "$(readlink -f "$0")")"
ROOT=$PWD
VERSION=${VERSION:-25.12.5}                 # OpenWrt version of the NAND images
EMU_VERSION=$(cat VERSION)                  # emulator version
[ -d src/qemu/.git ] || ./build.sh
SUDO=; docker info >/dev/null 2>&1 || SUDO=sudo
$SUDO docker image inspect qemu-win64-cross >/dev/null 2>&1 ||
    $SUDO docker build -t qemu-win64-cross \
        -f src/qemu/tests/docker/dockerfiles/fedora-win64-cross.docker \
        src/qemu/tests/docker/dockerfiles
# clang instead of MinGW GCC: native TLS (GCC uses slow emulated TLS,
# about 30% slower guest execution); same MinGW libraries from Fedora,
# plus libusb for USB pass-through (usb-host=VID:PID)
if ! $SUDO docker run --rm qemu-win64-clang test -f \
        /usr/x86_64-w64-mingw32/sys-root/mingw/lib/pkgconfig/libusb-1.0.pc 2>/dev/null; then
    mkdir -p work
    printf 'FROM qemu-win64-cross\nRUN dnf install -y clang lld mingw64-libusb1 && dnf clean all\n' > work/Dockerfile.clang
    $SUDO docker build -t qemu-win64-clang -f work/Dockerfile.clang work/
fi
APP=Router-Emulator
ZIP=$APP-$EMU_VERSION-win64.zip            # for GitHub: no flash folders
ZIPDUMPS=$APP-$EMU_VERSION-dumps.zip       # the flash folders only
PKG=$ROOT/work/winpkg/$APP
rm -rf "$PKG" && mkdir -p "$PKG/qemu" "$PKG/usb"
$SUDO docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp \
    -v "$ROOT/src/qemu:/src" -v "$PKG/qemu:/out" qemu-win64-clang bash -c '
set -e
SYSROOT=/usr/x86_64-w64-mingw32/sys-root/mingw
GCCLIB=/usr/lib/gcc/x86_64-w64-mingw32/$(ls /usr/lib/gcc/x86_64-w64-mingw32/ | head -1)
CLANG="--target=x86_64-w64-windows-gnu --sysroot=$SYSROOT -fuse-ld=lld"
mkdir -p /src/build-win-clang && cd /src/build-win-clang
[ -f build.ninja ] || ../configure --cross-prefix=x86_64-w64-mingw32- \
    --cc="clang $CLANG" --cxx="clang++ $CLANG" --extra-ldflags="-L$GCCLIB" \
    --target-list=aarch64-softmmu --enable-slirp --enable-fdt=internal \
    --enable-libusb \
    --disable-docs --disable-werror --disable-gtk --disable-sdl \
    --disable-vnc --disable-spice --disable-opengl --disable-curl \
    --disable-guest-agent --disable-tools
# build directories configured before libusb was added
grep -q "CONFIG_USB_LIBUSB 1" config-host.h || ./pyvenv/bin/meson configure -Dlibusb=enabled
ninja
SR=/usr/x86_64-w64-mingw32/sys-root/mingw/bin
cp qemu-system-aarch64.exe /out/
cp $(find subprojects -name "*.dll") /out/
todo=$(ls /out/*.exe /out/*.dll)
while [ -n "$todo" ]; do next=""
  for f in $todo; do
    for d in $(x86_64-w64-mingw32-objdump -p $f | awk "/DLL Name/ {print \$3}"); do
      if [ -f $SR/$d ] && [ ! -f /out/$d ]; then cp $SR/$d /out/; next="$next /out/$d"; fi
    done
  done
  todo=$next
done
x86_64-w64-mingw32-strip --strip-all /out/*.exe /out/*.dll'
# compile against the .NET Framework 4.8 reference assemblies so only APIs
# that exist on Windows are used (Mono's own libraries have newer ones)
# the launcher's version (title bar, file properties) comes from VERSION
mkdir -p work
# assembly versions are numbers only: 0.6a -> 0.6 (the text keeps the letter)
NUM_VERSION=$(echo "$EMU_VERSION" | sed 's/[^0-9.].*//; s/\.$//')
cat > work/Version.cs <<EOF
[assembly: System.Reflection.AssemblyVersion("$NUM_VERSION")]
[assembly: System.Reflection.AssemblyFileVersion("$NUM_VERSION")]
[assembly: System.Reflection.AssemblyInformationalVersion("$EMU_VERSION")]
[assembly: System.Reflection.AssemblyProduct("Router Emulator")]
namespace RouterEmulator { static class AppVersion { public const string Text = "$EMU_VERSION"; } }
EOF
API=/usr/lib/mono/4.8-api
mcs -nostdlib -noconfig -target:winexe -platform:anycpu -out:"$PKG/emulator.exe" \
    -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll \
    -r:$API/System.Drawing.dll -r:$API/System.Windows.Forms.dll -r:$API/System.Management.dll \
    windows/Launcher.cs windows/Presets.cs windows/Lang.cs windows/Terminal.cs windows/Leds.cs windows/UsbDevices.cs work/Version.cs
cp windows/README.txt LICENSE "$PKG/"
cp usb/README.txt "$PKG/usb/"
mkdir -p "$PKG/logs"
cp -r presets languages "$PKG/"
# NAND folders: openwrt=PROFILE (OpenWrt U-Boot images) or, with
# openwrt-stock=DIR, the vendor bootloader dumps in DIR + OpenWrt
# FLASH_PRESETS="a b": only these presets (file names without .ini; CI),
# "none": no flash folders; default: every preset
for f in presets/*.ini; do
    case " ${FLASH_PRESETS:-all} " in
    " all ") ;;
    *" $(basename "$f" .ini) "*) ;;
    *) continue ;;
    esac
    get() { awk -F= -v k="$1" '$1 == k { sub(/^[^=]*=/, ""); sub(/\r$/, ""); print; exit }' "$f"; }
    prof=$(get openwrt); dir=$(get nand-dir); stock=$(get openwrt-stock); local=$(get openwrt-local)
    ver=$(get openwrt-version); ver=${ver:-$VERSION}
    [ -n "$prof" ] && [ -n "$dir" ] || continue
    if [ -n "$stock" ]; then
        # BL2 dump: mtd0 (NAND, NOR) or eMMC boot0
        [ -n "$(ls "$stock"/*mtd0*.bin "$stock"/*boot0*.bin 2>/dev/null)" ] ||
            { echo "skip $f: no vendor dumps in $stock/"; continue; }
        set -- --stock "$stock"
    elif [ -n "$local" ]; then
        [ -d "$local" ] || { echo "skip $f: no local images in $local/"; continue; }
        set -- --local "$local"
    else
        set --
    fi
    [ "$(get openwrt-no-bdinfo)" = 1 ] && set -- "$@" --no-bdinfo
    [ -n "$(get soc)" ] && set -- "$@" --soc "$(get soc)"
    [ "$(get openwrt-emmc)" = 1 ] && set -- "$@" --emmc
    [ -n "$(get openwrt-emmc-layout)" ] && set -- "$@" --emmc-layout "$(get openwrt-emmc-layout)"
    [ -n "$(get openwrt-uboot)" ] && set -- "$@" --uboot "$(get openwrt-uboot)"
    [ "$(get openwrt-ubi-fip)" = 1 ] && set -- "$@" --ubi-fip
    [ -n "$(get openwrt-parts)" ] && set -- "$@" --parts "$(get openwrt-parts)"
    [ "$(get openwrt-nor)" = 1 ] && set -- "$@" --nor --nor-mb "$(get nor)"
    # a board without images for this version must not break the package
    tools/prepare-nand.sh "$@" --flash-mb "$(get nand)" "$prof" "$ver" "$PKG/$dir" ||
        { echo "skip $f: no NAND image"; rm -rf "${PKG:?}/$dir"; }
done
# profile-guided optimisation of QEMU (PGO=0 to skip): ~5-17 % faster
# guest code on real Windows; trains under Wine on the WR3000P image
if [ "${PGO:-1}" = 1 ] && [ -d "$PKG/nand-wr3000p" ]; then
    tools/pgo-windows.sh "$PKG/nand-wr3000p" "$PKG/qemu"
fi
mkdir -p dist && rm -f dist/$APP-*win64*.zip dist/$APP-*-dumps.zip
# flash folders only, in the package's folder layout
(cd work/winpkg && dumps=() &&
 for f in $APP/nand-* $APP/nor-* $APP/emmc-*; do [ -e "$f" ] && dumps+=("$f"); done;
 [ ${#dumps[@]} -eq 0 ] || zip -qr9 "$ROOT/dist/$ZIPDUMPS" "${dumps[@]}")
# public package: everything but the flash folders (they hold OpenWrt and
# board data such as factory/ dumps); users build them with prepare-nand.sh
(cd work/winpkg && zip -qr9 "$ROOT/dist/$ZIP" $APP -x "$APP/nand-*" "$APP/nor-*" "$APP/emmc-*")
ls -la "dist/$ZIP"; [ ! -f "dist/$ZIPDUMPS" ] || ls -la "dist/$ZIPDUMPS"

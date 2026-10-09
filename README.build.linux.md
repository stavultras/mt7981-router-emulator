# Building on Linux

**English** · [Русский](README.build.linux.ru.md) · [Overview](README.md) · [Windows build](README.build.windows.md)

Tested on Debian 13, Ubuntu 24.04 / 26.04, Fedora and Arch Linux (x86-64).
The build itself needs no root; packages are only needed once.

## 1. Dependencies

Build (QEMU): a C compiler, git, meson, ninja, pkg-config, Python 3 with
venv, and the glib, pixman and zlib development files. libslirp (user-mode
network) and libfdt are used from the system when installed; otherwise
`build.sh` downloads and builds QEMU's bundled copies (needs network access
to gitlab.freedesktop.org and gitlab.com), so a build without root works on
any machine that has the compiler and those three libraries.

Tools (optional): `ubinize` (mtd-utils), `sgdisk` (gdisk; eMMC images) and `wget` for NAND images
([`tools/prepare-nand.sh`](tools/prepare-nand.sh), [`tools/mknand.py`](tools/mknand.py)),
`socat` for the headless console (`emulator.sh -S`), bridge/iproute/iptables
for host networking ([`tools/host-bridge.sh`](tools/host-bridge.sh)), libpcap
only for the optional `-netdev pcap` backend (loaded at run time).

Debian / Ubuntu:

```bash
sudo apt-get install -y build-essential git ninja-build meson pkg-config \
    python3 python3-venv libglib2.0-dev libpixman-1-dev libslirp-dev \
    libfdt-dev libusb-1.0-0-dev zlib1g-dev \
    mtd-utils gdisk wget u-boot-tools device-tree-compiler socat \
    bridge-utils iproute2 iptables libpcap0.8t64
```

(`libpcap0.8` on releases before Debian 13 / Ubuntu 24.04.)

Fedora:

```bash
sudo dnf install -y gcc make git ninja-build meson pkgconf-pkg-config \
    python3 glib2-devel pixman-devel libslirp-devel libfdt-devel \
    libusb1-devel zlib-ng-compat-devel \
    mtd-utils-ubi gdisk wget uboot-tools dtc socat \
    iproute iptables-nft libpcap
```

Arch Linux:

```bash
sudo pacman -S --needed base-devel git ninja meson python glib2 pixman \
    libslirp dtc libusb zlib \
    mtd-utils gptfdisk wget uboot-tools socat iproute2 iptables libpcap
```

`tools/host-bridge.sh setup` makes the bridge persistent through
`/etc/network/interfaces` (Debian ifupdown); with NetworkManager or
systemd-networkd create `br0` with their tools and use only
`host-bridge.sh taps`. Without any bridge, `-w user|offline -l user` needs
no root at all.

## 2. Build QEMU with the mt7981-router machine

```bash
./build.sh
```

What [`build.sh`](build.sh) does:

1. clones QEMU **v10.1.0** into `src/qemu` (shallow);
2. creates branch `mt7981` and applies [`qemu-patches/*.patch`](qemu-patches/) with `git am`;
   (patches added to the repository later are applied to an existing checkout);
3. configures `--target-list=aarch64-softmmu --enable-slirp --enable-fdt=enabled`
   (system libslirp/libfdt or the bundled ones);
4. builds `qemu-system-aarch64` with `ninja` (`JOBS=N` limits the jobs,
   `CONFIGURE_ARGS` adds configure options).

Result: `src/qemu/build/qemu-system-aarch64`. Check:

```bash
src/qemu/build/qemu-system-aarch64 -M help | grep mt7981
src/qemu/build/qemu-system-aarch64 -M mt7981-router,help    # board options
```

Manual equivalent:

```bash
git clone --depth 1 --branch v10.1.0 https://gitlab.com/qemu-project/qemu.git src/qemu
cd src/qemu && git checkout -b mt7981 && git am ../../qemu-patches/*.patch
mkdir build && cd build
../configure --target-list=aarch64-softmmu --enable-slirp --enable-fdt=enabled --disable-docs
ninja qemu-system-aarch64
```

After changing sources in `src/qemu/hw/arm/mt7981/` just run `ninja` in
`src/qemu/build`. To update the patch series:
`cd src/qemu && git commit ... && git format-patch -o ../../qemu-patches v10.1.0..mt7981`.

## 3. Flash images

```bash
tools/prepare-nand.sh cudy_wr3000p-v1 25.12.5        # -> nand-wr3000p/
tools/prepare-nand.sh cudy_tr3000-v1 25.12.5         # -> nand-tr3000/
tools/prepare-nand.sh cudy_wr3000p-v1 snapshot       # snapshot instead of a release
# vendor bootloader kept (dumps of BL2 = *mtd0*.bin, FIP = *mtd4*.bin in wr3000u/)
tools/prepare-nand.sh --stock wr3000u --flash-mb 256 cudy_wr3000u-v1 25.12.5
# own OpenWrt build (the *-ubootmod-* images in a folder)
tools/prepare-nand.sh --local m3000/<build> cudy_m3000-v1 25.12.5
# boards without a bdinfo partition (FIP 0x380000, ubi 0x580000)
tools/prepare-nand.sh --no-bdinfo netis_nx31 25.12.5
# SPI-NOR board: vendor BL2/FIP dumps in wr3000/ + OpenWrt sysupgrade.bin
tools/prepare-nand.sh --stock wr3000 --nor cudy_wr3000-v1 25.12.5
```

PROFILE is the OpenWrt device profile. The output folder is `nand-NAME`
(profile without vendor prefix and `-v1`), as used by the presets' `nand-dir`.

Put the board's own `*Factory*.bin` (Wi-Fi calibration) and `*bdinfo*.bin`
(MAC address) into `factory/` before; without them Wi-Fi uses defaults and a
random MAC is generated.

## Linux package

```bash
tools/package-linux.sh        # -> dist/Router-Emulator-<version>-linux-x86_64.tar.gz
```

[`tools/package-linux.sh`](tools/package-linux.sh) (needs `patchelf`) packs
`qemu/qemu-system-aarch64` with the shared libraries it uses (everything
but glibc, found through `RUNPATH=$ORIGIN`), `emulator.sh`, presets, tools
and docs; no flash folders. `emulator.sh` and `tests/quick.py` use
`qemu/qemu-system-aarch64` when there is no `src/qemu/build`. The package
runs on distributions with the same or a newer glibc than the build
machine. For a small dependency set build QEMU without UI and audio first:

```bash
CONFIGURE_ARGS="--disable-gtk --disable-sdl --disable-opengl --disable-vnc \
  --disable-spice --disable-curl --audio-drv-list=" ./build.sh
```

The GitHub workflow [`.github/workflows/build.yml`](.github/workflows/build.yml)
builds this package and the Windows zip on every push, boots OpenWrt with
both (the Windows one on a Windows runner) and attaches them to a GitHub
Release when a tag is pushed.

## 4. Host networking (optional)

```bash
sudo tools/host-bridge.sh setup enp0s3 br0   # move the NIC into br0 (keeps IP/MAC, persistent)
sudo tools/host-bridge.sh taps br0 $USER     # wr-wan -> br0, wr-lan1..4 -> isolated br-wrlan
tools/host-bridge.sh status
sudo tools/host-bridge.sh teardown           # undo
```

In a VM (e.g. VirtualBox) set the host adapter's promiscuous mode to
"Allow All", otherwise frames for the router's MAC addresses are not
delivered. `setup` keeps a backup of `/etc/network/interfaces`.

## 5. Run

```bash
./emulator.sh                      # preset cudy-wr3000p-v1, WAN on br0, lan1 on br-wrlan
./emulator.sh -P list              # presets (presets/*.ini)
./emulator.sh -P cudy-tr3000-v1 -w user -l none
./emulator.sh -P cudy-wr3000p-v1 -o usb-port=3,ram=1024   # change the hardware
./emulator.sh -h                   # all options
```

Console: this terminal (Ctrl-A X quits, Ctrl-A C = QEMU monitor). Logs:
`logs/console_*.log`. Fast automated checks: [`tests/quick.py`](tests/quick.py).

Without root and without a terminal (scripts, CI): WAN through QEMU's
user-mode network, cut off from the internet (`-w offline`), LAN1 forwarded
to 127.0.0.1 (`-l user`, ports from the preset, default 8080 → 80,
8443 → 443, 8022 → 22), console on a unix socket:

```bash
./emulator.sh -P cudy-wr3000p-v1 -w offline -l user -S work/console.sock &
socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:work/console.sock   # Ctrl-] detaches
curl -s http://127.0.0.1:8080/ | head                 # LuCI
echo quit | socat - UNIX-CONNECT:work/monitor.sock    # stop
```

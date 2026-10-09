# Router Emulator (MediaTek MT7981B / Filogic 820, MT7986 / Filogic 830, MT7987)

**English** · [Русский](README.ru.md) · Build: [Linux](README.build.linux.md) · [Windows](README.build.windows.md)

A QEMU machine, `mt7981-router`, that emulates an MT7981B router board at
the hardware level. The board hardware (Ethernet PHYs/switch, flash, RAM
type and size, USB) is configurable, so one machine covers many devices;
**board presets** (`presets/*.ini`) describe concrete routers. Firmware
runs **unmodified**, through the same boot chain as on a real device:

```
BootROM (emulated) → BL2 (MediaTek preloader, DDR training) → BL31 (TF-A)
→ U-Boot → OpenWrt (kernel + rootfs from UBI on SPI-NAND)
```

Both OpenWrt's own bootloader ("ubootmod" layout) and vendor bootloaders
(BL2/FIP dumped from real devices, with NMBM) boot. Everything the
firmware needed was adapted on the emulator side; no firmware is patched.

MT7986A/B (Filogic 830) boards run on a second machine, `mt7986-router`
(preset key `soc=mt7986`): the same peripherals, four cores, the MT7986
BL2 with its own DRAM calibration and size detection, see
[MT7986](#mt7986-filogic-830); MT7987A/B boards on `mt7987-router`
(`soc=mt7987`, see [MT7987](#mt7987)).

## Quick start

Linux: [README.build.linux.md](README.build.linux.md) (or the
`Router-Emulator-<version>-linux-x86_64.tar.gz` of a release: QEMU
with its libraries, no build needed), then

```bash
tools/prepare-nand.sh cudy_wr3000p-v1 25.12.5   # official images -> nand-wr3000p/
./emulator.sh -P list                             # board presets
./emulator.sh -P cudy-wr3000p-v1                  # router console in this terminal
```

Windows: download the release zip (or build it, see
[README.build.windows.md](README.build.windows.md)), unpack, run `emulator.exe`.
The release contains no NAND folders: create them with
`tools/prepare-nand.sh` (Linux or WSL) and copy them next to `emulator.exe`,
or use dumps of a real router.

## Board hardware (machine options)

`-M mt7981-router,nand-dir=DIR,<options>` (or `mt7986-router`) and
`-m <RAM size>`:

| Option | Values | Meaning |
|---|---|---|
| `gmac0` | `mt7531` · `rtl8221b` · `yt8821` · `gpy211` · `none` | what GMAC0 (mac@0, SGMII0) is wired to |
| `ports` | e.g. `wan:lan1:lan2:lan3:lan4` | netdev ids of MT7531 ports 0..4, and port 5 with `port5=` (`-` = unused) |
| `port5`, `port5-phy-addr`, `port5-reset-gpio` | `rtl8221b` · `yt8821` · `gpy211` · `none`; MDIO address (5); GPIO | a 2.5G PHY on MT7531 port 5 (SGMII), e.g. Netcore N60 Pro; its netdev is the 6th entry of `ports` |
| `gmac0-port`, `gmac1-port` | netdev id | port of a PHY attached directly to a GMAC |
| `gmac1` | `rtl8221b` · `yt8821` · `gpy211` · `gphy` · `i2p5ge` · `none` | GMAC1 (mac@1); `gphy` = MT7981 built-in 1G PHY; `i2p5ge` = MT7987 internal 2.5G PHY; `gpy211` = MaxLinear GPY211C |
| `gmac0-reset-gpio`, `gmac1-reset-gpio` | GPIO, `-1` (default) | hardware reset line of a 2.5G PHY; not wired by default (a missing line is harmless, a wrong one could hold the PHY in reset) |
| `gmac0-phy-addr`, `gmac1-phy-addr` | MDIO address | address of that PHY (default 1 on gmac0, 6 on gmac1) |
| `flash` | `nand` · `nor` · `emmc` | boot flash: SPI-NAND on SPI0 (default), SPI-NOR on SPI2 (MT7986: SPI0) or eMMC (see below) |
| `nand` | `128` · `256` | W25N01GV / W25N02KV SPI-NAND |
| `nand-spi` | `0` (default) · `1` · `2` | SPI controller of the SPI-NAND (BPI-R4 Lite: 2) |
| `switch-irq-gpio` | GPIO | EINT of the MT7531 interrupt (default 38 / 66 / 41 for MT7981 / MT7986 / MT7987) |
| `flash=emmc`, `emmc-boot` | MB (4) | eMMC on MSDC0 instead: one image (the first `*.img` of the flash folder, or `-drive if=sd`) = boot0 + boot1 (`emmc-boot` MB each) + user area, size a power of 2 |
| `nor`, `nor-id` | MB (16), JEDEC ID (`ef4018`) | SPI-NOR size and ID, e.g. `204018` = XMC XM25QH128C, `c84018` = GD25Q128; `nor=64,nor-id=ef4020` = Winbond W25Q512JV (64 MB, 4-byte addressing; e.g. a WR3000 with a bigger chip) |
| `ddr` | `ddr3` · `ddr4` | soldered DRAM type: a BL2 built for the other type stops the machine, as DRAM init fails on a real board |
| `usb-port` | `none` · `2` · `3` | USB connector (USB 3.0: devices attach at SuperSpeed) |
| `usb-host` | `VID:PID[;VID:PID…]` | pass USB devices of this PC through to the router (USB 2.0 root ports 2, 3, …, also on boards without a connector), e.g. a Wi-Fi dongle, an LTE modem, a USB Ethernet adapter; see "USB Wi-Fi dongle" |
| `pcie-wifi` | `none` · `mt7992` | MT7987 only: card in the PCIe slot. `mt7992` = MediaTek MT7992 Wi-Fi 7 (2.4 + 5 GHz); OpenWrt drives it with `kmod-mt7996e` and the firmware of `kmod-mt7992-firmware`, which official MT7987 images do not include |
| `reset-gpio`, `wps-gpio` | GPIO | buttons (QOM `/machine/pinctrl` `reset-button`, `wps-button`) |
| `reset-active-high`, `wps-active-high` | `on` | button reads 1 when pressed (default: active low) |
| `reset-hold` | ms | power on with reset held (U-Boot TFTP recovery) |
| `poweroff` | `stop` · `reboot` | `stop` (default): the emulator ends when Linux powers off ("reboot: Power down" on UART0); `reboot`: like a real board, whose firmware cannot power down |
| `gpio-log` | `on` | print GPIO output changes and the LEDs' states |
| `led1` … `led32` | `NAME: COLOUR SOURCE [+ COLOUR SOURCE…]` | front panel LEDs (see below); `led-state` (QOM `/machine`) returns their states |
| `efuse` | file | load the eFuse contents from a dump of a real board (up to 4 KiB, as read from `/sys/bus/nvmem/devices/nvmem0/nvmem`), so calibration and chip data match that board |
| `efuse-uid` | 32 hex digits | set the per-chip unique block, so several emulated boards are not identical |
| `nand-uid` | 32 hex digits | SPI-NAND unique ID (default: derived from the flash folder name); stock Cudy firmware checks the NAND unique ID, without it login is disabled |

Front panel LEDs: `led1=Status: red gpio:11:low + white gpio:10:low` is
one lamp with two colours, each on its own GPIO (`:low` = GPIO_ACTIVE_LOW).
Sources: `gpio:N[:low]` (SoC GPIO), `phy:ADDR:N` (LED pin N of the
Ethernet PHY at MDIO address ADDR, as its LED registers program it:
RTL8221B, GPY211; e.g. the WR3000P WAN lamp in current OpenWrt),
`ws2812:BUS:N` (WS2812B RGB chain on SPI bus BUS, colour `rgb`: Redmi
AX6000) and `pwm:N` (PWM channel, pwm-leds: BPi-R4 Lite). QEMU samples them
every 50 ms; `qom-get /machine led-state` gives per LED (`;`) and colour
(`,`) `0`, `1`, `b` (blinking) or `#rrggbb` / `b#rrggbb`. The Windows
launcher shows them as a row of lamps above the console; with `-g`
(`gpio-log=on`) QEMU prints every change ("LED Status: white on").
[`tools/dts-leds.py`](tools/dts-leds.py) fills the `led` keys of the
presets from the OpenWrt device tree (`--write --all`).

Network ports are QEMU netdevs with the ids used above (`wan`, `lan1`, …).
Launcher-only preset keys: `lan-ip` (router LAN address, default
192.168.1.1) and `lan-forwards` (default `8080:80,8443:443,8022:22`, PC
port:router port) for the LAN port "this PC only": a QEMU user-mode network in the
router's /24 with `restrict=on` and these forwards from 127.0.0.1. QEMU's
own DHCP server is off there (`dhcp=off`, needs libslirp ≥ 4.7): OpenWrt's
dnsmasq does not serve DHCP on br-lan while another server answers.

## Board presets

A preset is an INI file: `name`, `description`, `soc` (`mt7981` default,
`mt7986`), `ram` (MB), `nand-dir`,
build-only keys (`openwrt=` OpenWrt profile, `openwrt-local=` own build,
`openwrt-stock=` vendor bootloader dumps) and machine options. Both
launchers use them; the Windows launcher has an editor for them (Ethernet,
switch port labels, RAM type/size, NAND, USB, button GPIOs with an
"active high" tick — unticked = active low, the default and what most
routers use; saving from the editor drops `;` comments).

| Preset | Ethernet | RAM | NAND | USB | Bootloader |
|---|---|---|---|---|---|
| Cudy WR3000P v1 | 2.5G WAN RTL8221B + 4×1G MT7531 | DDR4 512 MB | 128 MB | 2.0 | OpenWrt |
| Cudy WR3000H v1 | 2.5G WAN RTL8221B + 4×1G MT7531 | DDR3 512 MB | 128 MB | – | OpenWrt |
| Cudy WR3000S v1, WR3000E v1 | 5×1G MT7531 (WAN = port 0) | DDR3 256 MB | 128 MB | – | OpenWrt |
| Cudy WBR3000UAX v1 | 5×1G MT7531 (WAN = port 0) | DDR3 256 MB | 128 MB | 3.0 | OpenWrt |
| Cudy WR3000U v1 | 5×1G MT7531 (WAN = port 0) | DDR3 256 MB | 256 MB | – | vendor (NMBM) |
| Cudy TR3000 v1 | 2.5G WAN RTL8221B on GMAC0 + 1G LAN built-in PHY | DDR3 512 MB | 128 MB | 3.0 | OpenWrt |
| Cudy TR3000 256MB v1 | same, no switch | DDR3 512 MB | 256 MB | 3.0 | vendor (NMBM) |
| Cudy M3000 v1 / v2 (RTL8221B) | 2.5G WAN RTL8221B + 1G LAN built-in PHY | DDR3 256 MB | 128 MB | – | OpenWrt (own build) |
| Cudy M3000 v2 (YT8821) | 2.5G WAN Motorcomm YT8821 + 1G LAN built-in PHY | DDR3 256 MB | 128 MB | – | OpenWrt (own build) |
| Netis NX30 V2, NX31 | 1G WAN built-in PHY + 3×1G LAN MT7531 | DDR3 256 MB | 128 MB | – | OpenWrt |
| Netis NX32U | 4×1G MT7531 (WAN = port 0) | DDR3 256 MB | 128 MB | 3.0 | OpenWrt |
| Cudy WR3000 v1 | 4×1G MT7531 (WAN = port 0) | DDR3 256 MB | **SPI-NOR 16 MB** (XM25QH128C) | – | vendor |
| Xiaomi Mi Router AX3000T | 4×1G MT7531 (WAN = port 0) | DDR3 256 MB | 128 MB, Xiaomi layout | – | OpenWrt |
| Huasifei WH3000 Pro NAND | 2.5G LAN RTL8221B + 1G WAN built-in PHY, no switch | DDR4 1 GB | 256 MB, no bdinfo | 3.0 | vendor (NMBM) |
| Huasifei WH3000R NAND | 1G WAN built-in PHY + 3×1G LAN MT7531 | DDR3 512 MB | 256 MB, no bdinfo | 3.0 | vendor (NMBM) |
| Keenetic KN-1012 | 1G WAN built-in PHY + 4×1G LAN MT7531 (SFP not emulated) | DDR4 512 MB | 256 MB | 3.0 | vendor (NMBM) |
| Xiaomi Redmi AX6000 (MT7986A) | 4×1G MT7531 (WAN = port 4) | DDR4 512 MB | 128 MB | – | OpenWrt |
| Netcore N60 (MT7986A) | 2.5G WAN RTL8221B + 4×1G MT7531 | DDR3 256 MB | 128 MB | – | OpenWrt |
| Netcore N60 Pro (MT7986A) | 2.5G WAN GPY211 + 2.5G LAN GPY211 on switch port 5 + 3×1G MT7531 | DDR4 512 MB | 128 MB | 3.0 | OpenWrt |
| Bananapi BPi-R4 Lite (MT7987A) | 2.5G WAN internal PHY + 4×1G MT7531 (SFP cage not emulated); Wi-Fi 7 MT7992 card on PCIe — for Wi-Fi install `kmod-mt7996e kmod-mt7992-firmware` | DDR4 2 GB | 256 MB on SPI2, FIP in UBI | 3.0 | OpenWrt |
| GL.iNet GL-MT6000 (MT7986A) | 2.5G WAN RTL8221B + 2.5G LAN RTL8221B on switch port 5 + 4×1G MT7531 | DDR4 1 GB | **eMMC** | 3.0 | OpenWrt |
| Huasifei WH3000 Pro eMMC | 2.5G LAN RTL8221B + 1G WAN built-in PHY, no switch | DDR4 1 GB | **eMMC**, vendor layout | 3.0 | OpenWrt (RAX3000M eMMC DDR4 build) |

Netis boards have no bdinfo partition (FIP at 0x380000, ubi at 0x580000;
MACs in Factory): presets set `openwrt-no-bdinfo=1`. The MT7986 boards use
the same layout. Other layouts are given as `openwrt-parts=label:size,...`
(`tools/prepare-nand.sh --parts`), e.g. the Xiaomi AX3000T: BL2, Nvram,
Bdata, Factory, FIP, crash, crash_log, ubi at 0x600000, KF. Its firmware
calls the LAN ports lan2..lan4; the preset's `ports` names the emulator's
connections, so the launcher's LAN (`lan1`) is switch port 1.

## MT7986 (Filogic 830)

`mt7986-router` is the same machine with the MT7986 differences: four
Cortex-A53 cores, HW_CODE 0x7986, pinctrl at 0x1001f000 (101 GPIOs),
eFuse at 0x11d00000, two SPI controllers, no built-in GbE PHY, the MT7531
interrupt on EINT 66. Its BL2 (MediaTek's prebuilt DRAM library, DDR3 and
DDR4) calibrates against the same DRAM controller model, and detects the
DRAM size with the controller's test agent (writes beyond `-m` wrap
around, as on the real chip). The Wi-Fi driver reads back one MT7976
a-die (2.4 + 5 GHz); OpenWrt has no default EEPROM for the MT7986, so
`tools/prepare-nand.sh --soc mt7986` writes a minimal one into an empty
Factory (with a Factory dump of a real board its calibration is used).

```bash
tools/prepare-nand.sh --soc mt7986 --no-bdinfo netcore_n60-pro 25.12.5 nand-n60-pro
./emulator.sh -P netcore-n60-pro
```

## MT7987

`mt7987-router`: UARTs at 0x11000000, SPI0..2 at 0x11007800 /
0x11008800 / 0x11009800, TOP_MISC at 0x10021000, eFuse at 0x11d30000,
NETSYS v3 frame engine (PDMA at 0x6800), the internal 2.5G PHY
(`gmac1=i2p5ge`, MDIO 15; firmware loading and link), LVTS thermal
sensor, boot strap register (DDR type for "comb" BL2s, boot media for
U-Boot's rootdisk choice). The MT7987 BL2 calibrates DRAM against the
same controller model. Options for its boards: `nand-spi=2` (SPI-NAND on
SPI2), `switch-irq-gpio=41`, `-m 2G`.

Wi-Fi is on PCIe: the machine has the Gen3 controller of port 0
(`mt7987_pcie.c`: link, configuration TLPs, INTx, MSI capture; QEMU's
root port is the RC) and, with `pcie-wifi=mt7992`, an MT7992 Wi-Fi 7
card in the slot (`mt7992_wifi.c`, radio silent like the built-in Wi-Fi
of the other SoCs: the firmware is downloaded and answered, both bands
come up, hostapd runs, nothing is on the air). The official image has no
driver for it; install it once (WAN must reach the Internet, the
packages stay in the overlay):

```bash
apk update && apk add kmod-mt7996e kmod-mt7992-firmware
reboot
```

Then enable `radio0` / `radio1` and their networks in LuCI (Wi-Fi is off
by default in OpenWrt).

```bash
tools/prepare-nand.sh --soc mt7987 --flash-mb 256 --ubi-fip bananapi_bpi-r4-lite 25.12.5
./emulator.sh -P bananapi-bpi-r4-lite
```

eMMC boards (GL.iNet GL-MT6000) use `flash=emmc`: the MSDC host model
serves TF-A and U-Boot (PIO) and Linux (descriptor DMA); BL2 boots from
eMMC boot partition 0 ("EMMC_BOOT"), the FIP, kernel and rootfs are GPT
partitions. `tools/prepare-nand.sh --soc mt7986 --emmc glinet_gl-mt6000
25.12.5` builds the image with [`tools/mkemmc.py`](tools/mkemmc.py)
(sparse, 1 GB).

The Huasifei WH3000 Pro eMMC (MT7981B) keeps its vendor GPT layout
(u-boot-env, factory, fip, config, kernel, rootfs; `--emmc-layout
wh3000-pro`), and OpenWrt builds only its firmware (`sysupgrade.bin`, a tar
with the kernel FIT and the root squashfs), not its bootloader. The preset
(`openwrt-uboot=`, `--uboot`) takes the OpenWrt BL2 and FIP of the CMCC
RAX3000M eMMC DDR4 (same SoC, boot medium and DRAM type) and writes a
U-Boot environment that boots the FIT in the `kernel` partition; the
kernel finds `root=PARTLABEL=rootfs` through its own device tree, as with
the vendor bootloader. With dumps of the vendor BL2 (`*boot0*.bin`) and FIP
(`*fip*.bin`), `--stock DIR` keeps the vendor bootloader instead:

```bash
tools/prepare-nand.sh --emmc --emmc-layout wh3000-pro --uboot cmcc_rax3000m-emmc-ddr4 huasifei_wh3000-pro-emmc 25.12.5 emmc-wh3000-pro
./emulator.sh -P huasifei-wh3000-pro-emmc
```

## What is emulated

All device models live in `hw/arm/mt7981/` of the QEMU tree
(patches: [`qemu-patches/`](qemu-patches/), base QEMU v10.1.0).

| Block | Model | Notes |
|---|---|---|
| CPU, GIC | 2× (MT7986: 4×) Cortex-A53 (EL3/EL2), GICv3, arch timer 13 MHz | secondary CPUs started by BL31 through TOPMISC SPMC power-on; PSCI is BL31's |
| BootROM | high-level emulation (`mt7981_router.c`) | parses the `SPINAND!` header + GFH `FILE_INFO`, loads BL2 into L2 SRAM, jumps at EL3 |
| DRAM controller | `mt7981_sysctl.c` + generated status table | broadcast mode, RTSWCMD/MRW responses, jitter meter, DQS gating lead/lag, RX data eye: MediaTek's binary DRAM calibration finds real windows, BL2 log is clean (DDR3 and DDR4); `DDRCOMMON0` DDR3EN/DDR4EN checked against `ddr=` |
| Clocks, power, misc | `mt7981_sysctl.c` | sparse register file + special cases (frequency meter, CPU power-on, TRNG v2, eFuse calibration data, IPPC, thermal sensor ≈45 °C, EIP-97 ID, WED reset bits) |
| APXGPT | `mt7981_sysctl.c` | 8 general purpose timers (used by vendor BL2s) |
| TOPRGU | `mt7981_toprgu.c` | watchdog with real timeout, SW reset (reboot), reset status kept across reset |
| UART ×3 | QEMU 16550 + MTK extra registers | |
| SPI (IPM) | `mt7981_spim.c` | FIFO + DMA, half-duplex spi-mem mode used by TF-A/U-Boot/Linux |
| SPI-NAND | `spinand.c` | W25N01GV (2048+64) / W25N02KV (2048+128), on-die ECC, ONFI parameter page, unique ID page; backing store = folder of partition dumps (see below) or one raw image with OOB |
| eMMC | `mt7981_msdc.c` + QEMU eMMC | MSDC host: commands, auto CMD23/CMD12, PIO FIFO (TF-A, U-Boot), GPD/BD DMA (Linux); card with boot0/boot1 and user area; BootROM boots `EMMC_BOOT` images from boot0 |
| SPI-NOR | `spinor.c` | 3-byte addressing (up to 16 MB), selectable JEDEC ID, reads 1-1-1/1-1-2/1-1-4/1-2-2/1-4-4, page program, 4K/32K/64K/chip erase, status registers with QE; BootROM boots `SF_BOOT` images from it; same folder backing store |
| Ethernet | `mt7981_eth.c` | frame engine: QDMA TX (Linux), PDMA RX, PDMA v2 (U-Boot); TSO + checksum offload; LynxI SGMII PCS ×2; any combination of switch / PHYs on the two GMACs |
| Switch | `mt7981_eth.c` | MT7531: paged MDIO access, internal PHY indirect access, MTK special tag (DSA), learning FDB, port matrix, link IRQ → EINT 38 |
| PHYs | `mt7981_eth.c` | MT7531 GPHY ×5; RTL8221B-VB-CG (C45, temperature sensor); Motorcomm YT8821 (extended registers, UTP/SerDes spaces, 2.5G status); MaxLinear GPY211C (C45, firmware version, mailbox, temperature sensor); MT7981 built-in GbE PHY (calibration handshake); a PHY on MT7531 port 5; optional hardware reset GPIOs |
| GPIO / EINT | `mt7981_pinctrl.c` | buttons (reset/WPS, `reset-hold-ms` for timed presses), LED log, pad levels to board devices |
| LEDs | `mt7981_leds.c`, `ws2812b.c` | front panel LEDs from the preset: GPIO, PHY LED registers (RTL8221B, GPY211), WS2812B on SPI, PWM channels; blinking detection; `led-state` |
| USB | QEMU xHCI + MTK IPPC | USB 2.0 / 3.0 connector |
| Wi-Fi | `mt7981_wmac.c` | WFDMA rings + emulated WM/WA firmware command interface: firmware loads, both bands come up, hostapd runs, nothing is on the air (scans are empty) |
| PCIe (MT7987) | `mt7987_pcie.c` | MediaTek Gen3 host: link, CFGNUM configuration TLPs, INTx, MSI capture, with QEMU's PCIe root port |
| Wi-Fi 7 card (MT7987) | `mt7992_wifi.c` | MT7992 for mt7996e: BAR0 remap windows, WFDMA rings, patch/RAM download, UNI command results, TX free events; radio silent |
| Crypto (EIP-97) | ID only | the safexcel driver detects "no packet engine" and disables itself; software crypto is used |
| WED | reset bits only | with `wed_enable=1` mt7915e detects the missing WO MCU and runs without offload |

A new `pcap` netdev (`net/pcap.c`, libpcap / Npcap loaded at run time)
attaches a port to a host adapter like a bridged adapter — used by the
Windows launcher.

## Flash (NAND folder)

A NAND folder contains partition dumps without OOB. **Every file whose name
contains `mtdN` becomes partition N**; files are concatenated in order mtd0,
mtd1, … into the full flash, e.g. `mt7981.mtd0.BL2.bin`,
`mt7981.mtd1.u-boot-env.bin`, `mt7981.mtd2.Factory.bin`,
`mt7981.mtd3.bdinfo.bin`, `mt7981.mtd4.FIP.bin`, `mt7981.mtd5.ubi.bin`.

Everything the router writes (settings, sysupgrade, U-Boot env) is written
back into these files. Dumps from a real router (`cat /dev/mtdN >
name.mtdN.label.bin`) can be used directly. With `flash=nor` the folder is
the SPI-NOR contents (e.g. BL2, u-boot-env, Factory, bdinfo, FIP,
firmware). Files smaller than the flash (a 128 MB dump started with
`nand=256`, say) are completed on the first start: the last file grows
with erased bytes (`0xff`) to the flash size, so what the router writes
there is kept.

- [`tools/prepare-nand.sh`](tools/prepare-nand.sh) `[--stock DIR [--nor] | --local DIR] [--flash-mb N] [--no-bdinfo] PROFILE [VERSION] [OUTDIR]` — builds a NAND folder for an OpenWrt device profile: downloads the official OpenWrt U-Boot images (`PROFILE-ubootmod-*` or `PROFILE-*`, sha256 verified), or uses own builds (`--local`), or keeps a vendor BL2/FIP (`--stock`) with the OpenWrt `sysupgrade.bin` in the vendor layout; `--no-bdinfo` for boards without a bdinfo partition, `--nor` for SPI-NOR boards (vendor BL2/FIP + the OpenWrt `sysupgrade.bin` in the `firmware` partition), `--soc mt7986` for MT7986 boards (partition files `mt7986.mtdN.*`, minimal Wi-Fi EEPROM in an empty Factory), `--ubi-fip` for "spim-nand-ubi" BL2s (BL2 at 0, UBI from 0x200000 with the FIP as volume `fip`). Factory/bdinfo come from `factory/`.
- [`tools/mknand.py`](tools/mknand.py) — create / edit images: `create` (BL2, FIP, Factory, bdinfo, UBI from `.itb` or a `sysupgrade.bin`), `write --part fip`, `read`, `split`, `join`, `--flash-mb 256`, `--no-bdinfo`, `nor` (SPI-NOR folder).
- [`tools/mkemmc.py`](tools/mkemmc.py) — eMMC image (`flash=emmc`): BL2 in boot0, GPT user area with u-boot-env, factory, fip, kernel, rootfs (GL-MT6000 layout, or `--layout wh3000-pro`), OpenWrt `squashfs-factory.bin` in kernel + rootfs or `--sysupgrade` (sysupgrade tar), `--boot-env` (OpenWrt U-Boot environment that boots the `kernel` partition).

## Running

Linux: [`emulator.sh`](emulator.sh) — `-P PRESET` (`-P list`), `-o OPTS`
(override machine options, `ram=`), `-n NANDDIR`, `-w bridge|user|offline|none`
(`offline` = user-mode WAN with `restrict=on`: DHCP works, nothing leaves the PC),
`-l isolated|nic|user|none` (`user` = this PC only, forwards from the
preset), `-p "1 3"` (LAN ports), `-u DIR` (USB stick from a
folder, FAT16), `-L DIR` (console logs), `-g` (GPIO and LED log), `-R` (power on
with reset held 10 s → TFTP recovery), `-d` (unimplemented register log),
`-S SOCK` (headless: console on a unix socket, for scripts and CI;
`socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:SOCK` to attach). `WAN_EXTRA` /
`LAN_EXTRA` in the environment are appended to the user-mode netdevs, e.g.
`WAN_EXTRA=",guestfwd=tcp:10.0.2.100:80-cmd:nc 127.0.0.1 8000"` gives the
router a local test server even with `-w offline`. Headless example, no
root needed (the router's web UI at http://127.0.0.1:8080/):

```bash
./emulator.sh -P cudy-wr3000p-v1 -w offline -l user -S work/console.sock &
socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:work/console.sock   # console, Ctrl-] detaches
echo quit | socat - UNIX-CONNECT:work/monitor.sock    # stop
```
Host networking: [`tools/host-bridge.sh`](tools/host-bridge.sh) (`br0`
with the NIC for WAN, isolated `br-wrlan` for LAN — LAN on the real
network would expose the router's DHCP/RA there).

Windows: `emulator.exe` — board preset (with editor: New / Edit / Save /
Save as / Delete), NAND folder, WAN/LAN (NAT, "this PC only" port forwards
to LuCI/SSH, or bridge to an adapter via Npcap), USB folder, log folder,
Reset/WPS buttons, "Power + Reset: 10 s" (TFTP recovery), power off on
`poweroff`, built-in terminal (VT100, PuTTY-like 80×24 default with cell
snapping, select = copy, right click = paste, Ctrl+Shift+R fits the router
tty to the window), interface language switched on the fly
([`languages/*.ini`](languages/): English, Русский; add a language by
copying `en.ini`; [`tools/gen-lang-en.py`](tools/gen-lang-en.py)
regenerates `en.ini` from the sources, `--check` lists untranslated keys).

## USB Wi-Fi dongle

The emulated Wi-Fi radios are silent. For real Wi-Fi, pass a USB Wi-Fi
dongle of this PC through to the router (`usb-host=VID:PID`, several
`;` separated; Windows launcher: "USB devices" → Choose…); OpenWrt drives it with its own driver as one
more radio (`radio2`, path `…/usb1/1-2/1-2:1.0`), next to the silent
built-in ones, and runs hostapd on it like on real hardware.

1. Linux: `./emulator.sh … -o usb-host=148f:5370` (QEMU built with
   libusb: `libusb-1.0-0-dev`). QEMU opens `/dev/bus/usb/…`, so the user
   needs write access (emulator.sh prints a udev rule otherwise); the
   host's driver releases the dongle while the router uses it.
   Windows: install [UsbDk](https://github.com/daynix/UsbDk/releases)
   (the dongle stays on its Windows driver and is taken over only while
   the router runs) or bind the WinUSB driver to it with Zadig.
2. In OpenWrt install the dongle's driver once (WAN needs Internet), e.g.
   Ralink RT5370 / RT3070: `apk add kmod-rt2800-usb rt2800-usb-firmware`;
   MediaTek MT7601U: `kmod-mt7601u`; MT7612U: `kmod-mt76x2u`; Atheros
   AR9271: `kmod-ath9k-htc`.
3. Network → Wireless: enable `radio2` (the dongle's band: a 2.4 GHz
   dongle gives 2.4 GHz; a dual-band one works in one band at a time —
   choose 5 GHz there), set SSID and key. The built-in `radio0` /
   `radio1` can be disabled, they are not on the air anyway.

Checked with a Ralink RT5370 (148f:5370) on a Cudy WR3000P with OpenWrt
25.12.5: AP on channel 6, WPA2, bridged to br-lan; on Linux and on
Windows 11 with UsbDk.

## Repository layout

```
emulator.sh                 Linux launcher
presets/                  board presets (*.ini)
build.sh                  QEMU build (Linux)      → README.build.linux.md
build-windows.sh          Windows package (cross) → README.build.windows.md
qemu-patches/             patches on top of QEMU v10.1.0
tools/                    prepare-nand.sh, mknand.py, host-bridge.sh,
                          gen_dramc_table.py (DRAMC status defaults)
windows/                  launcher, preset editor, terminal (C#),
                          README.txt for the package
languages/                launcher texts (en.ini, ru.ini)
tests/                    quick.py (console-driven checks), powercut.py
factory/                  Factory (Wi-Fi EEPROM) and bdinfo (MAC) dumps
wr3000u/, tr3000/, wr3000/ vendor BL2/FIP dumps (not in git)
m3000/                    own OpenWrt builds (not in git)
```

## Development notes

- Reference sources used to model the hardware: OpenWrt's Linux tree
  (vanilla + OpenWrt patches), U-Boot, mtk-openwrt TF-A, mt76, coreboot's
  MediaTek DRAMC code (MT8192/MT8195, same DRAMC generation).
- `-d unimp` logs every access to registers without explicit modelling —
  the fastest way to find what new firmware polls.
- [`tests/quick.py`](tests/quick.py) starts QEMU with the console on a
  socket and waits for console patterns with hard time limits, e.g.
  `tests/quick.py -P cudy-tr3000-v1 --qemu="-netdev user,id=wan" 'Starting kernel@60' 'eth0: Link is Up@120'`.
  [`tests/powercut.py`](tests/powercut.py) quits/resets QEMU at random
  moments and checks that BL2 still loads FIP.
- The Windows build uses clang (native TLS): MinGW GCC's emulated TLS made
  guest execution ~30 % slower; it is also PGO-optimised (5–17 % on real
  Windows), and the launcher keeps QEMU out of Windows 11 efficiency mode.
  Profile (`perf`): most time goes to TCG's translation-block lookup, the
  device models are negligible; -O3/LTO gave nothing. QEMU already runs
  each of the two guest CPUs in its own host thread (MTTCG); one guest
  thread cannot be spread over more host cores.
- OpenWrt marks the overlay "ready" only at the end of boot; rebooting
  earlier makes fstools wipe it (same as on hardware).

## Limitations

- Wi-Fi radio is silent (no stations, empty scans); the MCU command
  interface answers generically. WED offload is not emulated.
- PHY interrupts are not generated (link changes after boot are seen only
  by polling drivers).
- No EIP-97 packet engine; PCIe only on MT7987 (the MT7992 card); PWM/I2C
  are stubs.
- Speed: ~2× slower than the real 1.3 GHz SoC on a typical PC (TCG).

## Releases

CI (`.github/workflows/build.yml`) builds the Linux and Windows packages on
every push and boots one preset per distinct hardware combination with each
of them, on Linux and on a real Windows runner: WR3000P (DDR4, RTL8221B on
GMAC1, switch), WR3000H (DDR3 preloader), WR3000S (WAN on a switch port),
TR3000 (no switch, one LAN on the built-in PHY), Netis NX31 (flash layout
without bdinfo) and the MT7986 boards Redmi AX6000 (DDR4, WAN on a switch
port), Netcore N60 (DDR3, RTL8221B), N60 Pro (GPY211 on GMAC1 and on
switch port 5), GL.iNet GL-MT6000 (eMMC), Huasifei WH3000 Pro eMMC (vendor
eMMC layout) and the MT7987 Bananapi BPi-R4
Lite (NAND on SPI2, FIP in UBI, internal 2.5G PHY). Flash folders are built for these tests only, never
packaged.
To release, change [`VERSION`](VERSION) and push to `main`: when the tag
`v<VERSION>` does not exist yet, CI builds with PGO, tests, creates the tag
and the GitHub Release with `...-linux-x86_64.tar.gz` and `...-win64.zip`
(no flash folders). Locally `build-windows.sh` also makes `...-dumps.zip`:
only the flash folders it built (`Router-Emulator/nand-*`, `nor-*`,
`emmc-*`), to unpack over a package.

## License

GPL-2.0-or-later, like QEMU (the emulator is a set of QEMU patches plus
tools around it): see [LICENSE](LICENSE). Firmware images are not part of
this repository; OpenWrt and vendor firmware keep their own licenses.

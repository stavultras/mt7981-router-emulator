Router Emulator (MediaTek MT7981B / Filogic 820, MT7986 / Filogic 830, MT7987)
==============================================================================

Emulates a MediaTek MT7981B, MT7986 or MT7987 router board whose hardware
is described by a board preset. The emulator runs the REAL boot chain,
unmodified: BootROM -> BL2 (DDR training) -> BL31 -> U-Boot -> OpenWrt
from the emulated flash.

Quick start
-----------
1. Unpack this folder anywhere (path without special characters is best).
2. Run emulator.exe, choose a board preset, press "Power on".
3. A terminal window opens: that is the router's serial port (115200 8N1).
   Press Enter there to get the OpenWrt shell. Arrow keys, Home/End etc.
   work (also in the U-Boot menu). Select text with the mouse = copy,
   right click or Shift+Insert = paste, mouse wheel / Shift+PgUp = scroll
   back. Closing the window powers the router off. Ctrl-A C opens the
   QEMU monitor.
   Like PuTTY the window starts with 80 x 24 characters and snaps to whole
   characters when resized. A serial console cannot tell the router its
   size, so Linux assumes 80x24. After resizing press Ctrl+Shift+R (or
   click "Fit router console to window") to run "resize" in the router:
   mc, top, vi then use the whole window.
4. Default network: WAN = NAT through this PC (Internet works),
   LAN port = "This PC only": LuCI at http://127.0.0.1:8080 (https on 8443),
   SSH: ssh -p 8022 root@127.0.0.1. The router's LAN address and these
   port forwards are preset settings (see below).

Board presets
-------------
A preset (presets\*.ini) describes the hardware:
  GMAC0       MT7531 switch (with the labels of its ports 0..4), a 2.5G
              PHY (Realtek RTL8221B or Motorcomm YT8821) or nothing
  GMAC1       a 2.5G PHY (RTL8221B / YT8821), the MT7981 built-in 1G PHY
              or nothing
  RAM         type (DDR3 / DDR4) and size; the type must match the BL2 in
              the flash, otherwise DRAM initialisation fails as on a real
              board and the emulator stops with an error
  Boot flash  SPI-NAND 128 MB (W25N01GV) or 256 MB (W25N02KV), or
              SPI-NOR 16 MB (W25Q128JV / XM25QH128C / GD25Q128) or
              SPI-NOR 64 MB (W25Q512JV)
  USB         none, USB 2.0 or USB 3.0
  PCIe Wi-Fi  MT7987 boards: a card in the PCIe slot, MediaTek MT7992
              (Wi-Fi 7). The official OpenWrt image has no driver for it:
              in the router run once "apk update && apk add kmod-mt7996e
              kmod-mt7992-firmware" and reboot (the BPi-R4 Lite preset has
              the card)
  Buttons     GPIO of the reset and WPS (or mesh) buttons; "active high"
              = the GPIO reads 1 while pressed. Leave it unticked (active
              low, as on most routers) unless the board's U-Boot/Linux
              device tree says GPIO_ACTIVE_HIGH. A wrong polarity looks
              like a button held down: U-Boot may start TFTP recovery or
              an upgrade, OpenWrt may enter failsafe.
  Chip identity
              eFuse dump of a real board and per-chip eFuse / NAND unique
              IDs (32 hex digits, "Random" button); empty = defaults.
              Vendor firmware may check the NAND UID.
  LEDs       front panel lamps shown above the router console: name and
              one or more colours, each driven by a GPIO ("active low" =
              lit while the GPIO is 0), an LED pin of an Ethernet PHY
              (MDIO address + LED number, e.g. the WR3000P WAN lamp in
              current OpenWrt), a WS2812B RGB LED on SPI or a PWM channel.
              A two-colour status lamp (red / white) has two rows. The
              included presets have them from the OpenWrt device tree;
              hover over a lamp to see its source
  On poweroff turn the emulator off (default) or reboot like the hardware
  Access from this PC
              router LAN IP (default 192.168.1.1) and port forwards for
              the LAN port "This PC only" (default 8080:80,8443:443,8022:22 =
              PC port:router port). Change them when the firmware uses
              another LAN address, e.g. 192.168.5.1.
  NAND folder the flash contents to use
"New..." / "Edit..." open the preset editor: change anything, then
"Save", "Save as new..." (keeps the original) or "Delete". Presets are
plain text files, so they can also be copied or edited by hand (saving
from the editor drops ";" comments in the file).
Port names (wan, lan1, ...) name the emulator's connections, not the
firmware's port labels: the launcher connects its WAN choice to "wan" and
its LAN choice ("LAN port", always the router's first LAN / Ethernet port)
to "lan1". E.g. the Xiaomi AX3000T firmware calls that port lan2.

Included presets: Cudy WR3000P, WR3000H, WR3000S, WR3000E, WR3000U,
WBR3000UAX, TR3000, TR3000 256MB, M3000 v1/v2 (RTL8221B), M3000 v2
(YT8821), WR3000 v1 (SPI-NOR); Netis NX30 V2, NX31, NX32U; Xiaomi Mi
Router AX3000T, Redmi AX6000; Netcore N60, N60 Pro; GL.iNet GL-MT6000;
Bananapi BPi-R4 Lite; Huasifei WH3000 Pro NAND, WH3000 Pro eMMC,
WH3000R NAND.

Language
--------
"Language" (the first line of the window) switches the language at once
(no restart). Languages are files in languages\ (en.ini, ru.ini): to add one,
copy en.ini to e.g. de.ini, set name= and translate the values; it shows
up in the list. Missing texts are shown in English. The router console
window itself is not translated.

Connecting router ports to a real network
-----------------------------------------
Install Npcap from https://npcap.com (tick "WinPcap API-compatible mode").
Then emulator.exe lists your network adapters for WAN and the LAN port ("Bridge
to: ..."). The router then appears on that network with its own MAC, like
a VirtualBox bridged adapter. Use a wired adapter - Wi-Fi adapters usually
cannot send frames with foreign MAC addresses.
WARNING: bridging the LAN port to your network puts the router's DHCP server and
IPv6 RA on that network.

Flash (NAND / NOR) folders
--------------------------
The release package contains no flash images (they hold OpenWrt and
board specific data). Create them with tools/prepare-nand.sh from the
source repository (Linux or WSL: needs python3, wget, mtd-utils), e.g.
  tools/prepare-nand.sh cudy_wr3000p-v1 25.12.5 nand-wr3000p
  tools/prepare-nand.sh --no-bdinfo netis_nx31 25.12.5 nand-nx31
  tools/prepare-nand.sh --stock DUMPS --nor cudy_wr3000-v1 25.12.5 nor-wr3000
and copy the folder next to emulator.exe, or use dumps of a real router.

Every preset names its NAND folder (nand-wr3000p\, nand-tr3000\, ...;
OpenWrt 25.12.5). Boards with "stock bootloader" in the description keep
the vendor BL2/U-Boot and run OpenWrt in the vendor flash layout.
All files whose name contains "mtdN" are joined in order mtd0, mtd1, ...
into the full flash image (e.g. mt7981.mtd0.BL2.bin). Changes the router
makes (settings, sysupgrade, U-Boot env) are written back into these
files - keep a copy if you want to return to a clean state. If the files
are smaller than the flash (a 128 MB dump on a 256 MB preset), the last
one is extended with erased bytes (0xff) to the flash size on the first
start.
To use your own dumps (e.g. from "cat /dev/mtdX" on a real device), put
them into a folder and select it as NAND folder.

USB
---
The "USB folder" appears as a USB flash drive (FAT16, <= 500 MB) on the
router's USB port (boards with USB only). In OpenWrt install
kmod-usb-storage and kmod-fs-vfat, then: mount /dev/sda1 /mnt

Console logs
------------
With "Log folder" ticked, every "Power on" writes the complete router
console output to a new file logs\console_YYYY-MM-DD_HH-mm-ss.log
(plain text, escape sequences removed, Windows line endings).

reboot / poweroff
-----------------
"reboot" in OpenWrt restarts the router (BootROM -> BL2 -> ... again).
A real board cannot switch itself off: on "poweroff" its firmware prints
"Power-down unsupported." and reboots. The emulator stops instead, like
pulling the power plug; a preset can choose the hardware behaviour
("On poweroff: Reboot" in the preset editor).

Buttons
-------
"Reset: 10 s" = factory reset, "WPS button", "Power cycle" = cold reset.
"Power + Reset: 10 s (TFTP recovery)" (while the router is off) = like
holding reset while plugging in the power and releasing it after 10 s:
U-Boot loads a recovery image via TFTP. Bridge LAN to an adapter and
run a TFTP server (e.g. Tftpd64):
  OpenWrt U-Boot: server 192.168.1.254, file
    openwrt-mediatek-filogic-<profile>-ubootmod-initramfs-recovery.itb
  some vendor U-Boots (e.g. Cudy): server 192.168.1.88, file recovery.bin

Wi-Fi
-----
Both radios (2.4 / 5 GHz) work from OpenWrt's point of view (hostapd runs,
APs are up), but nothing is on the air: scans return no networks.
OpenWrt has Wi-Fi off by default: enable the radios and their networks in
LuCI (Network -> Wireless). MT7987 boards (BPi-R4 Lite) have no built-in
Wi-Fi; their MT7992 PCIe card needs kmod-mt7996e and kmod-mt7992-firmware
(see "PCIe Wi-Fi" above).

License and source code
-----------------------
GPL-2.0-or-later (see LICENSE); qemu\ is QEMU 10.1.0 with the emulator's
patches. Source code: https://github.com/stavultras/router-emulator

#!/bin/bash
# Router Emulator: run a board described by a preset (presets/*.ini).
# MT7981 and MT7986 boards (preset key soc=mt7986: machine mt7986-router).
#
#   ./emulator.sh [options] [-- extra qemu args]
#
# Options:
#   -P PRESET      board preset: file name in presets/ without .ini, a path
#                  to an .ini file, or the preset's name= (default:
#                  cudy-wr3000p-v1); "-P list" lists the presets
#   -o OPTS        override/add machine options, e.g. "usb-port=3,ddr=ddr3"
#                  ("-o ram=1024" changes the RAM size, "-o lan-ip=10.0.0.1,
#                  lan-forwards=8080:80;8022:22" the "-l user" access)
#   -n DIR         NAND directory with partition dumps (*mtdN*, e.g.
#                  mt7981.mtd0.BL2.bin), concatenated in mtd order
#                  (default: the preset's nand-dir)
#   -w MODE        WAN: bridge (tap wr-wan on br0, default), user (NAT via
#                  QEMU, router WAN gets 10.0.2.15), offline (same, but
#                  restrict=on: DHCP works, nothing leaves the PC - for
#                  tests that must not reach update/VPN servers), none
#   -l MODE        LAN: isolated (taps on br-wrlan, host 192.168.1.2, default)
#                  nic (taps on br0 = the physical network!), none,
#                  user (this PC only: QEMU forwards 127.0.0.1 ports to the
#                  router, preset keys lan-ip / lan-forwards, default
#                  192.168.1.1 and 8080:80,8443:443,8022:22)
#   -p PORTS       LAN ports to connect, e.g. "1" (default) or "1 3".
#                  Connecting several ports to the same host bridge creates
#                  a loop (the router bridges its LAN ports), so use one
#                  unless you add separate host bridges yourself.
#   -u DIR         export DIR as a USB flash drive (FAT16, max 500MB, read-write, QEMU
#                  vvfat) on the router's USB port (default: ./usb if it
#                  exists and the board has USB; "-u none" disables).  Needs
#                  kmod-usb-storage + kmod-fs-vfat in OpenWrt (/dev/sda1)
#   WAN_EXTRA / LAN_EXTRA (environment): appended to the user-mode netdev
#                  of -w user|offline / -l user, e.g. a mock server for the
#                  router: WAN_EXTRA=",guestfwd=tcp:10.0.2.100:80-cmd:nc 127.0.0.1 18555"
#   -S SOCK        headless: console on a unix socket instead of this
#                  terminal (socat -,raw,echo=0,escape=0x1d UNIX-CONNECT:SOCK,
#                  Ctrl-] detaches), for CI and scripted tests; SOCK is
#                  relative to this folder
#   -L DIR         console log folder (default: ./logs, "-L none" disables);
#                  every start writes console_YYYY-MM-DD_HH-MM-SS.log
#   -m MONITOR     QEMU monitor socket path (default: ./work/monitor.sock)
#   -g             print GPIO changes and the front panel LEDs of the preset
#                  ("LED Status: white on"; preset keys led1=...)
#   -R             power on with reset held 10 s (U-Boot TFTP recovery:
#                  OpenWrt U-Boot asks 192.168.1.254, some vendor
#                  bootloaders 192.168.1.88)
#   -d             debug: log unimplemented register accesses to work/qemu.log
#   -V             print the emulator version
#
# Console: serial (UART0) on this terminal.  Exit QEMU with Ctrl-A X.
# Buttons: socat - UNIX-CONNECT:work/monitor.sock, then
#   qom-set /machine/pinctrl reset-button true / false
set -e
cd "$(dirname "$(readlink -f "$0")")"
ROOT=$PWD
# QEMU_BIN, own build (./build.sh) or the binary of the Linux package
QEMU=${QEMU_BIN:-$ROOT/src/qemu/build/qemu-system-aarch64}
[ -n "$QEMU_BIN" ] || [ -x "$QEMU" ] || QEMU=$ROOT/qemu/qemu-system-aarch64
PRESET=cudy-wr3000p-v1
OVERRIDE=
NAND=
WAN=bridge
LAN=isolated
PORTS="1"
USBDIR=$ROOT/usb
LOGDIR=$ROOT/logs
CONSOCK=
MON=$ROOT/work/monitor.sock
EXTRA=()
GPIO=
DEBUG=()

EMU_VERSION=$(cat "$ROOT/VERSION" 2>/dev/null)
while getopts "P:o:n:w:l:p:u:L:m:S:gRdVh" o; do
    case $o in
    P) PRESET=$OPTARG ;;
    o) OVERRIDE=$OPTARG ;;
    n) NAND=$(readlink -f "$OPTARG") ;;
    w) WAN=$OPTARG ;;
    l) LAN=$OPTARG ;;
    p) PORTS=$OPTARG ;;
    u) USBDIR=$OPTARG ;;
    L) LOGDIR=$OPTARG ;;
    S) CONSOCK=$OPTARG ;;
    m) MON=$OPTARG ;;
    g) GPIO="$GPIO,gpio-log=on" ;;
    R) GPIO="$GPIO,reset-hold=10000" ;;
    d) DEBUG=(-d unimp,guest_errors -D "$ROOT/work/qemu.log") ;;
    V) echo "Router Emulator $EMU_VERSION"; exit 0 ;;
    *) sed -n '2,49p' "$0"; exit 1 ;;
    esac
done
shift $((OPTIND - 1))
[ "$1" = "--" ] && shift
EXTRA=("$@")
mkdir -p "$ROOT/work"

if [ ! -x "$QEMU" ]; then
    echo "QEMU not built: run ./build.sh" >&2
    exit 1
fi

# --- preset: [preset] key=value lines ---------------------------------
ini_get() { awk -F= -v k="$2" '$1 == k { sub(/^[^=]*=/, ""); print; exit }' "$1"; }
if [ "$PRESET" = list ]; then
    for f in "$ROOT"/presets/*.ini; do
        printf '%-24s %s\n    %s\n' "$(basename "$f" .ini)" \
            "$(ini_get "$f" name)" "$(ini_get "$f" description)"
    done
    exit 0
fi
PF=
for f in "$PRESET" "$ROOT/presets/$PRESET.ini"; do
    [ -f "$f" ] && { PF=$f; break; }
done
if [ -z "$PF" ]; then
    PF=$(grep -l -x -F "name=$PRESET" "$ROOT"/presets/*.ini 2>/dev/null | head -1)
fi
[ -n "$PF" ] || { echo "preset not found: $PRESET (try -P list)" >&2; exit 1; }
MOPTS=
SOC=mt7981
RAM=512
LANIP=192.168.1.1
LANFWD=8080:80,8443:443,8022:22
PNAND=
while IFS= read -r line; do
    line=${line%$'\r'}
    case $line in ''|\;*|\#*|\[*) continue ;; esac
    k=${line%%=*}; v=${line#*=}
    case $k in
    name|description|openwrt*) ;;
    lan-ip) LANIP=$v ;;
    lan-forwards) LANFWD=$v ;;
    ram) RAM=$v ;;
    soc) SOC=$v ;;
    nand-dir) PNAND=$v ;;
    *) MOPTS="$MOPTS,$k=$v" ;;
    esac
done < "$PF"
IFS=, read -ra ov <<< "$OVERRIDE"
for kv in "${ov[@]}"; do
    case $kv in
    ram=*) RAM=${kv#ram=} ;;
    soc=*) SOC=${kv#soc=} ;;
    lan-ip=*) LANIP=${kv#lan-ip=} ;;
    lan-forwards=*) LANFWD=${kv#lan-forwards=} ;;   # ';' separated here
    ?*) MOPTS="$MOPTS,$kv" ;;      # later options win in QEMU -M
    esac
done
[ -n "$NAND" ] || NAND=$(readlink -f "$ROOT/${PNAND:-nand}")
echo "Router Emulator $EMU_VERSION" >&2
echo "preset: $(ini_get "$PF" name) - $(ini_get "$PF" description)" >&2
case "$MOPTS," in *usb-port=none,*) [ "$USBDIR" = "$ROOT/usb" ] && USBDIR=none ;; esac

NET=()
lan_br=
case $LAN in
isolated) lan_br=br-wrlan ;;
nic) lan_br=br0
     echo "WARNING: router LAN ports are bridged to the physical network;" \
          "its DHCP/RA servers will be visible there." >&2 ;;
none|user) ;;
*) echo "bad -l $LAN" >&2; exit 1 ;;
esac

if [ "$WAN" = bridge ] || [ -n "$lan_br" ]; then
    # (re)create taps; needs root once per boot of the host
    if ! ip link show wr-wan >/dev/null 2>&1 ||
       { [ -n "$lan_br" ] && [ "$(basename "$(readlink -f /sys/class/net/wr-lan1/master 2>/dev/null)")" != "$lan_br" ]; }; then
        sudo "$ROOT/tools/host-bridge.sh" taps br0 "$USER" "${lan_br:-br-wrlan}"
    fi
fi

case $WAN in
bridge) NET+=(-netdev tap,id=wan,ifname=wr-wan,script=no,downscript=no) ;;
user) NET+=(-netdev "user,id=wan$WAN_EXTRA") ;;
offline) NET+=(-netdev "user,id=wan,restrict=on$WAN_EXTRA") ;;
none) ;;
*) echo "bad -w $WAN" >&2; exit 1 ;;
esac
if [ "$LAN" = user ]; then
    # QEMU user-mode network: a virtual PC in the router's /24 (addresses
    # not clashing with the router), no outgoing connections, only the
    # forwards from 127.0.0.1
    net=${LANIP%.*}; r=${LANIP##*.}; free=()
    for i in $(seq 250 -1 1); do [ "$i" != "$r" ] && free+=($i); [ ${#free[@]} = 3 ] && break; done
    # dhcp=off: the router is the DHCP server of its LAN (OpenWrt's dnsmasq
    # does not serve DHCP when it sees another server on br-lan)
    a="user,id=lan1,net=$net.0/24,host=$net.${free[0]},dns=$net.${free[1]},dhcpstart=$net.${free[2]},dhcp=off,restrict=on"
    busy=
    for f in ${LANFWD//[,;]/ }; do
        # QEMU cannot start when a forwarded port is taken: check first
        if (exec 3<>"/dev/tcp/127.0.0.1/${f%%:*}") 2>/dev/null; then
            busy="$busy ${f%%:*}"
        fi
    done
    if [ -n "$busy" ]; then
        echo "port(s)$busy of 127.0.0.1 already in use (another emulator?):" \
             "stop that program or change lan-forwards (-o lan-forwards=...)" >&2
        exit 1
    fi
    for f in ${LANFWD//[,;]/ }; do
        a="$a,hostfwd=tcp:127.0.0.1:${f%%:*}-$LANIP:${f##*:}"
        echo "LAN1: http(s)/ssh 127.0.0.1:${f%%:*} -> $LANIP:${f##*:}" >&2
    done
    NET+=(-netdev "$a$LAN_EXTRA")
fi
if [ -n "$lan_br" ]; then
    for i in $PORTS; do
        NET+=(-netdev tap,id=lan$i,ifname=wr-lan$i,script=no,downscript=no)
    done
fi

USB=()
if [ "$USBDIR" != none ] && [ -d "$USBDIR" ]; then
    d=$(readlink -f "$USBDIR")
    # port=1: QEMU would otherwise insert a full-speed hub on the last port
    USB=(-blockdev "driver=vvfat,node-name=usbstick,dir=${d//,/,,},rw=on,fat-type=16"
         -device usb-storage,drive=usbstick,removable=on,port=1)
fi

CON=(-monitor "unix:$MON,server,nowait")
if [ "$LOGDIR" != none ]; then
    mkdir -p "$LOGDIR"
    LOG=$(readlink -f "$LOGDIR")/console_$(date +%Y-%m-%d_%H-%M-%S).log
    echo "console log: $LOG" >&2
    CON+=(-chardev "stdio,id=con,mux=on,signal=off,logfile=${LOG//,/,,},logappend=off"
          -serial chardev:con -mon chardev=con)
fi
if [ -n "$CONSOCK" ]; then
    # headless: replace the stdio console by a unix socket (keeps the log)
    rm -f "$CONSOCK"
    c="socket,id=con,path=${CONSOCK//,/,,},server=on,wait=off"
    [ -n "$LOG" ] && c="$c,logfile=${LOG//,/,,},logappend=off"
    CON=(-monitor "unix:$MON,server,nowait" -chardev "$c" -serial chardev:con)
fi

rm -f "$MON"
"$QEMU" -M "$SOC-router,nand-dir=${NAND//,/,,}$MOPTS$GPIO" -m "${RAM}M" \
    $([ -n "$CONSOCK" ] && echo "-display none" || echo -nographic) \
    "${CON[@]}" \
    "${NET[@]}" "${USB[@]}" "${DEBUG[@]}" "${EXTRA[@]}"
rc=$?
# make the log readable: no escape sequences, no "\r\r\n" double breaks
if [ -n "$LOG" ] && [ -f "$LOG" ]; then
    sed -i -e 's/\x1b\[[0-9;?]*[A-Za-z]//g' -e 's/\r//g' "$LOG"
fi
exit $rc

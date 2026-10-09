#!/usr/bin/env python3
"""
Front panel LEDs of a board from its OpenWrt device tree, as preset lines
(led1=..., led2=...) for the emulator's LED panel.

  dts-leds.py [--openwrt DIR] PRESET...      print the LED lines
  dts-leds.py --write PRESET...              replace them in the preset
  dts-leds.py --write --all                  all presets with openwrt=

PRESET is a file in presets/ (with or without .ini) or a path; its
openwrt= key names the OpenWrt device whose DEVICE_DTS is read from
target/linux/mediatek/image/filogic.mk (default OpenWrt tree: src/openwrt).

A preset LED line:  ledN=<name>: <colour> <source> [+ <colour> <source>...]
  sources  gpio:N[:low]     SoC GPIO (":low" = GPIO_ACTIVE_LOW)
           phy:ADDR:N       LED pin N of the Ethernet PHY at MDIO address ADDR
           ws2812:BUS:N     LED N of a WS2812B chain on SPI bus BUS (colour "rgb")
           pwm:N            PWM channel N (pwm-leds)
Several colours of one lamp (a bi-colour status LED) are joined with "+":
LEDs with the same function and different colours, and the LEDs the
led-boot / led-running / led-failsafe / led-upgrade aliases point to.
LEDs the emulator cannot drive (GPIO expanders on I2C) are left out.
"""

import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

COLORS = ["white", "red", "green", "blue", "amber", "violet", "yellow", "ir",
          "multi", "rgb", "purple", "orange", "pink", "cyan", "lime"]

# LED_FUNCTION_* -> panel name
NAMES = {
    "power": "Power", "status": "Status", "wan": "WAN", "wan-online": "Internet",
    "lan": "LAN", "wlan": "Wi-Fi", "wlan-2ghz": "Wi-Fi 2.4G", "wlan-5ghz": "Wi-Fi 5G",
    "wlan-6ghz": "Wi-Fi 6G", "usb": "USB", "wps": "WPS", "fault": "Fault",
    "activity": "Activity", "heartbeat": "Heartbeat", "mesh": "Mesh",
    "indicator": "Indicator", "network": "Network", "boot": "Boot",
    "sfp": "SFP", "system": "Status", "run": "Status", "sys": "Status",
    "wifi": "Wi-Fi", "wifi2": "Wi-Fi 2.4G", "wifi5": "Wi-Fi 5G", "wifi2g": "Wi-Fi 2.4G",
    "wifi5g": "Wi-Fi 5G", "wlan2g": "Wi-Fi 2.4G", "wlan5g": "Wi-Fi 5G", "internet": "Internet", "alarm": "Alarm", "signal": "Signal", "mobile": "Mobile",
}


# ---------------------------------------------------------------- parsing

class Node:
    def __init__(self, name, parent=None):
        self.name = name
        self.parent = parent
        self.props = {}         # name -> raw value text (None: boolean)
        self.children = []
        self.labels = []
        self.deleted = False

    def child(self, name):
        for c in self.children:
            if c.name == name and not c.deleted:
                return c
        c = Node(name, self)
        self.children.append(c)
        return c

    def live_children(self):
        return [c for c in self.children if not c.deleted]

    def str(self, k):
        v = self.props.get(k)
        if v is None:
            return None
        m = re.match(r'\s*"([^"]*)"', v)
        return m.group(1) if m else None

    def cells(self, k):
        v = self.props.get(k)
        if v is None:
            return None
        m = re.search(r'<([^>]*)>', v)
        return m.group(1).split() if m else None


class Dts:
    def __init__(self, incdirs, macros):
        self.incdirs = incdirs
        self.macros = macros
        self.root = Node("/")
        self.labels = {}        # label -> Node
        self.orphans = {}       # &label of a node in a file we do not have

    def load(self, path):
        text = self.preprocess(path, set())
        self.parse(text)

    def preprocess(self, path, seen):
        out = []
        base = os.path.dirname(path)
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
        text = re.sub(r'/\*.*?\*/', ' ', text, flags=re.S)
        text = re.sub(r'//[^\n]*', '', text)
        for line in text.split("\n"):
            m = re.match(r'\s*#include\s+([<"])([^>"]+)[>"]', line)
            if m:
                name = m.group(2)
                for d in ([base] if m.group(1) == '"' else []) + self.incdirs:
                    p = os.path.join(d, name)
                    if os.path.isfile(p):
                        if p not in seen:
                            seen.add(p)
                            if p.endswith(".h"):
                                self.read_macros(p)
                            else:
                                out.append(self.preprocess(p, seen))
                        break
                continue
            m = re.match(r'\s*#define\s+(\w+)\s+(.*)', line)
            if m:
                self.macros[m.group(1)] = m.group(2).strip()
                continue
            if re.match(r'\s*#', line):
                continue
            out.append(line)
        return self.expand("\n".join(out))

    def read_macros(self, path):
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = re.match(r'\s*#define\s+(\w+)\s+([^/\n]*)', line)
                if m:
                    self.macros.setdefault(m.group(1), m.group(2).strip())
                m = re.match(r'\s*#include\s+"([^"]+)"', line)
                if m:
                    p = os.path.join(os.path.dirname(path), m.group(1))
                    if os.path.isfile(p):
                        self.read_macros(p)

    def expand(self, text):
        def rep(m):
            w = m.group(0)
            for _ in range(5):
                if w not in self.macros:
                    break
                w = self.macros[w]
            return w
        return re.sub(r'\b[A-Z_][A-Z0-9_]*\b', rep, text)

    def parse(self, text):
        toks = re.findall(r'"(?:[^"\\]|\\[\s\S])*"|<[^>]*>|\[[^\]]*\]|/delete-node/|'
                          r'/delete-property/|[{};=]|[^\s{};="<\[]+', text)
        i = 0
        stack = []
        cur = None
        while i < len(toks):
            t = toks[i]
            if t == "/dts-v1/" or t == "/plugin/":
                i += 2
                continue
            if t == "/delete-node/":
                name = toks[i + 1]
                if cur is None and name.startswith("&"):
                    n = self.labels.get(name[1:])
                    if n:
                        n.deleted = True
                elif cur is not None:
                    for c in cur.children:
                        if c.name == name or c.name.split("@")[0] == name:
                            c.deleted = True
                i += 3
                continue
            if t == "/delete-property/":
                if cur is not None:
                    cur.props.pop(toks[i + 1], None)
                i += 3
                continue
            if t == "}":
                cur = stack.pop() if stack else None
                i += 2 if i + 1 < len(toks) and toks[i + 1] == ";" else 1
                continue
            # node: [label:]* name {    or    property [= value] ;
            labels = []
            j = i
            while j < len(toks) and toks[j].endswith(":") and toks[j] not in "{};=":
                labels.append(toks[j][:-1])
                j += 1
            name = toks[j]
            if j + 1 < len(toks) and toks[j + 1] == "{":
                if cur is None:
                    if name == "/":
                        node = self.root
                    elif name.startswith("&"):
                        lbl = name[1:].strip("{}")
                        node = self.labels.get(lbl)
                        if node is None:
                            node = self.orphans.setdefault(lbl, Node("&" + lbl))
                    else:
                        node = self.root.child(name)
                else:
                    node = cur.child(name)
                for lb in labels:
                    if lb in self.orphans and self.orphans[lb] is not node:
                        self.merge(node, self.orphans.pop(lb))
                    self.labels[lb] = node
                    node.labels.append(lb)
                stack.append(cur)
                cur = node
                i = j + 2
                continue
            # property
            if cur is None:
                i = j + 1
                continue
            if j + 1 < len(toks) and toks[j + 1] == "=":
                k = j + 2
                val = []
                while k < len(toks) and toks[k] != ";":
                    val.append(toks[k])
                    k += 1
                cur.props[name] = " ".join(val)
                i = k + 1
            else:
                cur.props[name] = None
                i = j + 2 if j + 1 < len(toks) and toks[j + 1] == ";" else j + 1

    def merge(self, dst, src):
        dst.props.update(src.props)
        for c in src.children:
            c.parent = dst
            dst.children.append(c)

    def label_of(self, ref):
        """'&pio' -> 'pio' (also for nodes defined in files we do not have)"""
        return ref.lstrip("&")


def num(s):
    """a cell: number or simple expression of numbers ("(1 | 4)")"""
    s = s.strip("()")
    try:
        return int(s, 0)
    except ValueError:
        if re.fullmatch(r'[0-9a-fA-FxX()|&+<> ]+', s):
            try:
                return int(eval(s, {"__builtins__": {}}))
            except Exception:
                pass
    return 0


def enabled(n):
    while n is not None:
        if n.str("status") in ("disabled", "fail"):
            return False
        n = n.parent
    return True


def node_label_name(n):
    return n.labels[0] if n.labels else None


def is_under(n, labels):
    """is n below a node labelled with one of labels (or an orphan &label)?"""
    p = n.parent
    while p is not None:
        if any(lb in labels for lb in p.labels) or p.name.lstrip("&") in labels:
            return True
        if p.name.split("@")[0] in labels:
            return True
        p = p.parent
    return False


def walk(n):
    yield n
    for c in n.live_children():
        yield from walk(c)


# ---------------------------------------------------------------- LEDs

class Led:
    def __init__(self, node, function, color, source):
        self.node = node
        self.function = function
        self.color = color
        self.source = source


def color_of(n):
    c = n.cells("color")
    if c:
        try:
            return COLORS[num(c[0])]
        except (ValueError, IndexError):
            pass
    lbl = n.str("label")
    if lbl and ":" in lbl:
        return lbl.split(":")[0]
    return "white"


def function_of(n):
    f = n.str("function")
    if f is None:
        lbl = n.str("label")
        if lbl:
            f = lbl.split(":")[-1]
        else:
            f = re.sub(r'^led[-_]?', '', n.name.split("@")[0]) or n.name
    e = n.cells("function-enumerator")
    if e:
        f += "-" + str(num(e[0]))
    return f


def find_leds(dts):
    leds, skipped = [], []
    tops = [dts.root] + list(dts.orphans.values())
    for top in tops:
        for n in walk(top):
            if n.deleted or not enabled(n):
                continue
            compat = n.str("compatible") or ""
            if compat == "gpio-leds":
                for c in n.live_children():
                    g = c.cells("gpios")
                    if not g or not enabled(c):
                        continue
                    ctl = g[0].lstrip("&")
                    if ctl != "pio":
                        skipped.append("%s (GPIO of %s)" % (c.name, ctl))
                        continue
                    flags = num(g[2]) if len(g) > 2 else 0
                    src = "gpio:%d%s" % (num(g[1]), ":low" if flags & 1 else "")
                    leds.append(Led(c, function_of(c), color_of(c), src))
            elif compat == "pwm-leds":
                for c in n.live_children():
                    p = c.cells("pwms")
                    if not p or not enabled(c):
                        continue
                    leds.append(Led(c, function_of(c), color_of(c), "pwm:%d" % num(p[1])))
            elif "worldsemi,ws2812b" in compat:
                bus = re.search(r'spi(\d)', " ".join([n.parent.name] + n.parent.labels)) \
                    if n.parent is not None else None
                if not bus:
                    skipped.append("%s (WS2812B on an unknown bus)" % n.name)
                    continue
                for c in n.live_children():
                    r = c.cells("reg")
                    if r is None or not enabled(c):
                        continue
                    leds.append(Led(c, function_of(c), "rgb", "ws2812:%s:%d" % (bus.group(1), num(r[0]))))
            elif n.name == "leds" and n.parent is not None and n.parent.cells("reg") \
                    and "ethernet-phy" in (n.parent.str("compatible") or "ethernet-phy") \
                    and is_under(n.parent, ("mdio", "mdio_bus", "mdio-bus")):
                addr = num(n.parent.cells("reg")[0])
                for c in n.live_children():
                    r = c.cells("reg")
                    if r is None or not enabled(c):
                        continue
                    leds.append(Led(c, function_of(c), color_of(c),
                                    "phy:%d:%d" % (addr, num(r[0]))))
    return leds, skipped


def group(leds, dts):
    """lamps: lists of LEDs (one colour each) shown as one panel lamp"""
    lamps = []
    alias = dts.root.child("aliases") if any(c.name == "aliases" for c in dts.root.children) else None
    status = []
    if alias is not None:
        for k in ("led-boot", "led-running", "led-failsafe", "led-upgrade"):
            v = alias.props.get(k)
            if v:
                n = dts.labels.get(v.strip().lstrip("&"))
                for l in leds:
                    if l.node is n and l not in status:
                        status.append(l)
    if len(status) > 1 and len(set(l.color for l in status)) == len(status):
        lamps.append(status)
    else:
        status = []
    for l in leds:
        if l in status:
            continue
        for lamp in lamps:
            if lamp is not lamps[0] or not status:
                if lamp[0].function == l.function and l.color not in [x.color for x in lamp] \
                        and lamp[0].source.split(":")[0] == l.source.split(":")[0]:
                    lamp.append(l)
                    break
        else:
            lamps.append([l])
    # the bi-colour status lamp first, the others in DTS order
    return lamps


def lamp_name(lamp):
    fs = [l.function for l in lamp]
    if len(set(fs)) > 1:
        # power + status / fault: the board's status lamp
        return "Status" if "status" in fs else NAMES.get(fs[0], fs[0])
    f = fs[0]
    m = re.match(r'(.*?)-(\d+)$', f)
    if f not in NAMES and m and m.group(1) in NAMES:
        return NAMES[m.group(1)] + " " + m.group(2)
    return NAMES.get(f, f.replace("_", " ").replace("-", " ").capitalize())


def lines_for(dts):
    leds, skipped = find_leds(dts)
    out = []
    for i, lamp in enumerate(group(leds, dts), 1):
        out.append("led%d=%s: %s" % (i, lamp_name(lamp),
                   " + ".join("%s %s" % (l.color, l.source) for l in lamp)))
    return out, skipped


# ---------------------------------------------------------------- presets

def ini_get(path, key):
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith(key + "="):
                return line[len(key) + 1:].strip()
    return None


def device_dts(owrt, profile):
    mk = os.path.join(owrt, "target/linux/mediatek/image/filogic.mk")
    with open(mk, encoding="utf-8") as f:
        text = f.read()
    for p in (profile, profile + "-stock", profile + "-ubootmod"):
        m = re.search(r'^define Device/%s\n(.*?)^endef' % re.escape(p), text, re.S | re.M)
        if m:
            d = re.search(r'DEVICE_DTS\s*:=\s*(\S+)', m.group(1))
            if d:
                return d.group(1)
    return None


def load_dts(owrt, name):
    linux = os.path.join(ROOT, "src", "linux")
    incdirs = [os.path.join(owrt, "target/linux/mediatek/dts"),
               os.path.join(linux, "include"),
               os.path.join(linux, "arch/arm64/boot/dts/mediatek")]
    dts = Dts(incdirs, {})
    # the SoC .dtsi (in the kernel tree, patched by OpenWrt) may be missing:
    # the binding constants the board files use come from these headers
    for h in ("dt-bindings/gpio/gpio.h", "dt-bindings/leds/common.h",
              "dt-bindings/input/input.h"):
        dts.read_macros(os.path.join(linux, "include", h))
    dts.load(os.path.join(owrt, "target/linux/mediatek/dts", name + ".dts"))
    return dts


def write_preset(path, lines, src):
    with open(path, encoding="utf-8") as f:
        old = f.read().split("\n")
    keep = [l for l in old if not re.match(r'led\d+=', l)
            and not l.startswith("; LEDs (tools/dts-leds.py")]
    while keep and keep[-1] == "":
        keep.pop()
    if lines:
        keep.append("; LEDs (tools/dts-leds.py from %s)" % src)
        keep += lines
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(keep) + "\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("presets", nargs="*")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--openwrt", default=os.path.join(ROOT, "src", "openwrt"))
    a = ap.parse_args()
    pdir = os.path.join(ROOT, "presets")
    files = []
    if a.all:
        files = sorted(os.path.join(pdir, f) for f in os.listdir(pdir) if f.endswith(".ini"))
    for p in a.presets:
        for c in (p, os.path.join(pdir, p), os.path.join(pdir, p + ".ini")):
            if os.path.isfile(c):
                files.append(c)
                break
        else:
            sys.exit("preset not found: " + p)
    rc = 0
    for f in files:
        prof = ini_get(f, "openwrt")
        if not prof:
            if not a.all:
                print("%s: no openwrt= key" % f, file=sys.stderr)
                rc = 1
            continue
        name = device_dts(a.openwrt, prof)
        if not name:
            print("%s: device %s not in filogic.mk" % (f, prof), file=sys.stderr)
            rc = 1
            continue
        dts = load_dts(a.openwrt, name)
        lines, skipped = lines_for(dts)
        print("# %s (%s.dts)" % (os.path.basename(f), name))
        for l in lines:
            print(l)
        for s in skipped:
            print("# not emulated: " + s)
        if a.write:
            write_preset(f, lines, name + ".dts")
    return rc


if __name__ == "__main__":
    sys.exit(main())

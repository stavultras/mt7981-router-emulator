// Board presets (presets\*.ini) and the preset editor ("constructor").
//
// A preset file:
//   [preset]
//   name=...            shown in the model list
//   description=...     hardware summary
//   ram=512             MB (-m)
//   nand-dir=nand-xxx   default NAND folder (relative to the program folder)
//   openwrt=...         OpenWrt device profile the package's NAND folder is
//                       built from (build-windows.sh; ignored here)
//   soc=mt7981          SoC: mt7981 (default) or mt7986, the machine is
//                       <soc>-router
//   led1=...            front panel LEDs (see Leds.cs), also machine options
//   key=value           every other key is a machine option
//                       (gmac0, gmac1, ports, nand, ddr, usb-port, ...)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RouterEmulator
{
    class Preset
    {
        public string FilePath;
        // ordered key/value pairs as in the file
        public List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();

        public string Get(string k, string def = "")
        {
            foreach (var kv in Values) if (kv.Key == k) return kv.Value;
            return def;
        }

        public void Set(string k, string v)
        {
            for (int i = 0; i < Values.Count; i++) {
                if (Values[i].Key == k) { Values[i] = new KeyValuePair<string, string>(k, v); return; }
            }
            Values.Add(new KeyValuePair<string, string>(k, v));
        }

        public string Name { get { return Get("name", Path.GetFileNameWithoutExtension(FilePath)); } }
        public string Description { get { return Get("description"); } }
        public int RamMB { get { int r; return int.TryParse(Get("ram", "512"), out r) ? r : 512; } }
        public bool HasUsb { get { return Get("usb-port", "2") != "none"; } }
        // QEMU machine: mt7981-router or mt7986-router
        public string Soc
        {
            get { string s = Get("soc", "mt7981"); return s == "mt7986" || s == "mt7987" ? s : "mt7981"; }
        }
        public string Machine { get { return Soc + "-router"; } }

        // LAN1 "This PC only": router LAN address and port forwards
        // "pcport:routerport,..." from 127.0.0.1 to the router
        public const string DefaultLanIp = "192.168.1.1", DefaultForwards = "8080:80,8443:443,8022:22";
        public string LanIp { get { return Get("lan-ip", DefaultLanIp); } }
        public string LanForwards { get { return Get("lan-forwards", DefaultForwards); } }

        public static bool ValidIp(string s)
        {
            var p = s.Trim().Split('.');
            if (p.Length != 4) return false;
            foreach (var x in p) { int v; if (!int.TryParse(x, out v) || v < 0 || v > 255 || x.Length == 0) return false; }
            int last = int.Parse(p[3]);
            return last > 0 && last < 255;
        }

        // null on a syntax error
        public static List<int[]> ParseForwards(string s)
        {
            var list = new List<int[]>();
            foreach (var item in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                var pr = item.Split(':');
                int a, b;
                if (pr.Length != 2 || !int.TryParse(pr[0], out a) || !int.TryParse(pr[1], out b) ||
                    a < 1 || a > 65535 || b < 1 || b > 65535)
                    return null;
                list.Add(new[] { a, b });
            }
            return list;
        }

        // "127.0.0.1:8080/8443/8022 -> 192.168.1.1:80/443/22"
        public string ForwardSummary()
        {
            var pc = new List<string>();
            var rt = new List<string>();
            foreach (var f in ParseForwards(LanForwards) ?? new List<int[]>()) {
                pc.Add(f[0].ToString());
                rt.Add(f[1].ToString());
            }
            if (pc.Count == 0) return "-";
            return "127.0.0.1:" + string.Join("/", pc.ToArray()) + " -> " + LanIp + ":" + string.Join("/", rt.ToArray());
        }

        // QEMU user-mode network on LAN1: a virtual PC in the router's /24
        // (its own addresses chosen not to clash with the router), no
        // outgoing connections, only the forwards from this PC's loopback
        // (dhcp=off: the router is the DHCP server of its LAN)
        public string HostOnlyNetdev(string id)
        {
            string ip = ValidIp(LanIp) ? LanIp.Trim() : DefaultLanIp;
            string net = ip.Substring(0, ip.LastIndexOf('.') + 1);
            int router = int.Parse(ip.Substring(ip.LastIndexOf('.') + 1));
            var free = new List<int>();
            for (int i = 250; i > 0 && free.Count < 3; i--) if (i != router) free.Add(i);
            var sb = new StringBuilder("-netdev user,id=" + id + ",net=" + net + "0/24,host=" + net + free[0]
                + ",dns=" + net + free[1] + ",dhcpstart=" + net + free[2] + ",dhcp=off,restrict=on");
            foreach (var f in ParseForwards(LanForwards) ?? new List<int[]>())
                sb.Append(",hostfwd=tcp:127.0.0.1:" + f[0] + "-" + ip + ":" + f[1]);
            return sb.ToString();
        }

        // -M options: everything but the launcher's own keys
        public string MachineOptions(string baseDir = null)
        {
            var sb = new StringBuilder();
            foreach (var kv in Values) {
                if (kv.Key == "name" || kv.Key == "description" || kv.Key == "ram" || kv.Key == "nand-dir"
                    || kv.Key == "soc"                 // selects the machine
                    || kv.Key.StartsWith("lan-")       // LAN1 "This PC only" network
                    || kv.Key.StartsWith("openwrt"))   // used by the package build only
                    continue;
                string v = kv.Value;
                // a relative eFuse dump path is relative to the program folder
                if (kv.Key == "efuse" && baseDir != null && v.Length > 0 && !Path.IsPathRooted(v))
                    v = Path.Combine(baseDir, v);
                sb.Append(',').Append(kv.Key).Append('=').Append(v.Replace(",", ",,"));
            }
            return sb.ToString();
        }

        public override string ToString() { return Name; }

        public static Preset Load(string path)
        {
            var p = new Preset { FilePath = path };
            foreach (var raw in File.ReadAllLines(path)) {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) p.Set(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
            }
            return p;
        }

        public void Save()
        {
            var lines = new List<string> {
                "; Board preset for the Router Emulator.",
                "; Keys other than name/description/ram/nand-dir are -M machine options.",
                "[preset]",
            };
            foreach (var kv in Values) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(FilePath, lines.ToArray());
        }

        public static List<Preset> LoadAll(string dir)
        {
            var list = new List<Preset>();
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.ini")) {
                try { list.Add(Load(f)); } catch (Exception) { }
            }
            list.Sort(delegate (Preset a, Preset b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static string FileNameFor(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '-');
            string s = sb.ToString().Trim('-');
            while (s.Contains("--")) s = s.Replace("--", "-");
            return (s.Length > 0 ? s : "preset") + ".ini";
        }
    }

    class Choice
    {
        public string Value, Label;
        public Choice(string v, string l) { Value = v; Label = l; }
        public override string ToString() { return Label; }
    }

    // The preset editor
    class PresetForm : Form
    {
        readonly string presetDir, root;
        Preset preset;          // null: new preset
        public Preset Result;   // saved preset (null if deleted / cancelled)
        public bool Deleted;

        TextBox name, desc, nandDir;
        ComboBox soc, gmac0, gmac1, gmac0Port, gmac1Port, port5, port5Port, nandSize, ddr, ram, usbPort, pcieWifi;
        ComboBox[] swPort = new ComboBox[5];
        NumericUpDown gmac0Rst, gmac1Rst, port5Rst, port5Addr, resetGpio, wpsGpio;
        CheckBox resetHigh, wpsHigh;
        TextBox lanIp, lanFwd, efuseFile, efuseUid, nandUid;
        ComboBox poweroff;
        CheckBox autoDesc;
        ListBox ledList;

        static readonly string[] PortIds = { "wan", "lan1", "lan2", "lan3", "lan4", "-" };
        // keys written by the editor (dropped when not applicable)
        static readonly List<string> Known = new List<string> {
            "name", "description", "gmac0", "ports", "gmac0-port", "gmac0-reset-gpio", "gmac1",
            "gmac1-port", "gmac1-reset-gpio", "nand", "ddr", "ram", "usb-port", "reset-gpio",
            "wps-gpio", "reset-active-high", "wps-active-high", "lan-ip", "lan-forwards", "nand-dir",
            "flash", "nor", "nor-id", "poweroff", "efuse", "efuse-uid", "nand-uid", "soc", "port5",
            "port5-phy-addr", "port5-reset-gpio", "pcie-wifi" };

        public PresetForm(string presetDir, string root, Preset p)
        {
            this.presetDir = presetDir;
            this.root = root;
            preset = p;
            Text = p == null ? L.T("ed.title_new", "New board preset") : L.F("ed.title", "Board preset: {0}", p.Name);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 610);
            Font = new Font("Segoe UI", 9f);

            int y = 12;
            name = new TextBox { Left = 150, Top = y, Width = 470 };
            Row(L.T("ed.name", "Name:"), name, ref y);
            desc = new TextBox { Left = 150, Top = y, Width = 470 };
            Row(L.T("ed.description", "Description:"), desc, ref y, 22);
            autoDesc = new CheckBox { Left = 150, Top = y, Width = 470, Text = L.T("ed.auto_desc", "Generate the description from the hardware below") };
            Controls.Add(autoDesc);
            y += 30;

            // hardware on tabs, so the window stays small
            // hardware on tabs, the most used settings on the first one
            var tabs = new TabControl { Left = 10, Top = y, Width = 620, Height = 332 };
            Controls.Add(tabs);
            var mem = Page(tabs, L.T("ed.tab_general", "General"));
            var eth = Page(tabs, "Ethernet");
            int gy = 22;
            gmac0 = Combo(eth, "GMAC0 (mac@0):", ref gy,
                new Choice("mt7531", L.T("ed.mt7531", "MT7531 switch (5 x 1G ports)")),
                new Choice("rtl8221b", L.T("ed.rtl8221b", "RTL8221B 2.5G PHY (Realtek)")),
                new Choice("yt8821", L.T("ed.yt8821", "YT8821 2.5G PHY (Motorcomm)")),
                new Choice("gpy211", L.T("ed.gpy211", "GPY211 2.5G PHY (MaxLinear)")),
                new Choice("none", L.T("ed.not_connected", "Not connected")));
            eth.Controls.Add(new Label { Left = 10, Top = gy + 3, Width = 130, Text = L.T("ed.switch_ports", "Switch ports 0..4:") });
            for (int i = 0; i < 5; i++) {
                swPort[i] = new ComboBox { Left = 140 + i * 94, Top = gy, Width = 88, DropDownStyle = ComboBoxStyle.DropDown };
                swPort[i].Items.AddRange(PortIds);
                eth.Controls.Add(swPort[i]);
            }
            gy += 30;
            // a 2.5G PHY on switch port 5 (SGMII), e.g. MT7986 boards
            port5 = Combo(eth, L.T("ed.port5", "Switch port 5:"), ref gy,
                new Choice("none", L.T("ed.port5_none", "Not used")),
                new Choice("rtl8221b", L.T("ed.rtl8221b", "RTL8221B 2.5G PHY (Realtek)")),
                new Choice("yt8821", L.T("ed.yt8821", "YT8821 2.5G PHY (Motorcomm)")),
                new Choice("gpy211", L.T("ed.gpy211", "GPY211 2.5G PHY (MaxLinear)")));
            port5.Width = 230;
            eth.Controls.Add(new Label { Left = 380, Top = gy - 27, Width = 90, Text = L.T("ed.mdio_addr", "MDIO address:") });
            port5Addr = new NumericUpDown { Left = 470, Top = gy - 30, Width = 60, Minimum = 0, Maximum = 31, Value = 5 };
            eth.Controls.Add(port5Addr);
            port5Port = PortCombo(eth, L.F("ed.phy_port", "{0} PHY port:", "Port 5"), ref gy, out port5Rst);
            gmac0Port = PortCombo(eth, L.F("ed.phy_port", "{0} PHY port:", "GMAC0"), ref gy, out gmac0Rst);
            gy += 6;
            gmac1 = Combo(eth, "GMAC1 (mac@1):", ref gy,
                new Choice("rtl8221b", L.T("ed.rtl8221b", "RTL8221B 2.5G PHY (Realtek)")),
                new Choice("yt8821", L.T("ed.yt8821", "YT8821 2.5G PHY (Motorcomm)")),
                new Choice("gpy211", L.T("ed.gpy211", "GPY211 2.5G PHY (MaxLinear)")),
                new Choice("i2p5ge", L.T("ed.i2p5ge", "MT7987 internal 2.5G PHY")),
                new Choice("gphy", L.T("ed.gphy", "MT7981 built-in 1G PHY")),
                new Choice("none", L.T("ed.not_connected", "Not connected")));
            gmac1Port = PortCombo(eth, L.F("ed.phy_port", "{0} PHY port:", "GMAC1"), ref gy, out gmac1Rst);
            eth.Controls.Add(new Label { Left = 140, Top = gy, Width = 470, Height = 34, ForeColor = Color.DimGray,
                Text = L.T("ed.port_note", "Names of the emulator's connections, not the firmware's labels: the launcher "
                     + "connects its WAN choice to \"wan\" and its LAN choice to \"lan1\".") });

            gy = 22;
            soc = Combo(mem, L.T("ed.soc", "SoC:"), ref gy,
                new Choice("mt7981", "MediaTek MT7981B (Filogic 820)"),
                new Choice("mt7986", "MediaTek MT7986A/B (Filogic 830)"),
                new Choice("mt7987", "MediaTek MT7987A/B"));
            ddr = Combo(mem, L.T("ed.ram_type", "RAM type:"), ref gy,
                new Choice("ddr4", "DDR4"), new Choice("ddr3", "DDR3"));
            string mb = L.T("ed.mb", "MB"), gb = L.T("ed.gb", "GB");
            ram = Combo(mem, L.T("ed.ram_size", "RAM size:"), ref gy,
                new Choice("256", "256 " + mb), new Choice("512", "512 " + mb), new Choice("1024", "1 " + gb),
                new Choice("2048", "2 " + gb));
            // (boot flash list follows)
            // value: "nand:<MB>" or "nor:<MB>:<JEDEC ID>"
            nandSize = Combo(mem, L.T("ed.flash", "Boot flash:"), ref gy,
                new Choice("nand:128", "SPI-NAND 128 " + mb + " (Winbond W25N01GV)"),
                new Choice("nand:256", "SPI-NAND 256 " + mb + " (Winbond W25N02KV)"),
                new Choice("nor:16:ef4018", "SPI-NOR 16 " + mb + " (Winbond W25Q128JV)"),
                new Choice("nor:16:204018", "SPI-NOR 16 " + mb + " (XMC XM25QH128C)"),
                new Choice("nor:16:c84018", "SPI-NOR 16 " + mb + " (GigaDevice GD25Q128)"),
                new Choice("nor:64:ef4020", "SPI-NOR 64 " + mb + " (Winbond W25Q512JV)"),
                new Choice("emmc:0", L.T("ed.emmc", "eMMC (image *.img in the flash folder)")));
            usbPort = Combo(mem, L.T("ed.usb_port", "USB port:"), ref gy,
                new Choice("2", "USB 2.0"), new Choice("3", "USB 3.0"), new Choice("none", L.T("ed.none", "None")));
            // MT7987 boards: a Wi-Fi card in the PCIe slot (the firmware needs its driver packages)
            pcieWifi = Combo(mem, L.T("ed.pcie_wifi", "PCIe Wi-Fi card:"), ref gy,
                new Choice("none", L.T("ed.none", "None")),
                new Choice("mt7992", "MediaTek MT7992 (Wi-Fi 7)"));
            mem.Controls.Add(new Label { Left = 140, Top = gy - 6, Width = 460, Height = 16, ForeColor = Color.DimGray,
                Text = L.T("ed.pcie_wifi_hint", "OpenWrt needs: apk add kmod-mt7996e kmod-mt7992-firmware") });
            gy += 14;
            mem.Controls.Add(new Label { Left = 10, Top = gy + 3, Width = 130, Text = L.T("ed.nand_dir", "Flash folder:") });
            nandDir = new TextBox { Left = 140, Top = gy, Width = 370 };
            mem.Controls.Add(nandDir);
            var browse = new Button { Left = 516, Top = gy - 1, Width = 84, Height = 25, Text = L.T("main.browse", "Browse...") };
            browse.Click += delegate {
                using (var d = new FolderBrowserDialog { SelectedPath = FullDir(nandDir.Text) }) {
                    if (d.ShowDialog(this) == DialogResult.OK) nandDir.Text = RelDir(d.SelectedPath);
                }
            };
            mem.Controls.Add(browse);
            var adv = Page(tabs, L.T("ed.buttons", "Buttons (GPIO numbers)"));
            adv.Controls.Add(new Label { Left = 10, Top = 25, Width = 60, Text = L.T("ed.reset", "Reset:") });
            resetGpio = new NumericUpDown { Left = 70, Top = 22, Width = 55, Minimum = 0, Maximum = 100 };
            adv.Controls.Add(resetGpio);
            resetHigh = new CheckBox { Left = 135, Top = 23, Width = 160, Text = L.T("ed.active_high", "active high") };
            adv.Controls.Add(resetHigh);
            adv.Controls.Add(new Label { Left = 300, Top = 25, Width = 80, Text = L.T("ed.wps", "WPS / mesh:") });
            wpsGpio = new NumericUpDown { Left = 385, Top = 22, Width = 55, Minimum = 0, Maximum = 100 };
            adv.Controls.Add(wpsGpio);
            wpsHigh = new CheckBox { Left = 450, Top = 23, Width = 160, Text = L.T("ed.active_high", "active high") };
            adv.Controls.Add(wpsHigh);
            var tip = new ToolTip();
            string tipText = L.T("ed.active_high_tip", "Ticked: the GPIO reads 1 while the button is pressed.\n"
                + "Default (unticked): active low, the GPIO reads 0 while pressed.");
            tip.SetToolTip(resetHigh, tipText);
            tip.SetToolTip(wpsHigh, tipText);
            int py = 54;
            poweroff = Combo(adv, L.T("ed.poweroff", "On \"poweroff\":"), ref py,
                new Choice("stop", L.T("ed.poweroff_stop", "Turn the emulator off")),
                new Choice("reboot", L.T("ed.poweroff_reboot", "Reboot (like a real board)")));
            poweroff.Left = 140;
            // chip identity: eFuse dump / per-chip block, SPI-NAND unique ID
            var idg = Page(tabs, L.T("ed.tab_identity", "Chip identity"));
            idg.Controls.Add(new Label { Left = 10, Top = 3, Width = 590, Height = 16, ForeColor = Color.DimGray,
                Text = L.T("ed.identity", "Chip identity (empty = default)") });
            idg.Controls.Add(new Label { Left = 10, Top = 25, Width = 130, Text = L.T("ed.efuse", "eFuse dump:") });
            efuseFile = new TextBox { Left = 140, Top = 22, Width = 380 };
            idg.Controls.Add(efuseFile);
            var efBrowse = new Button { Left = 526, Top = 21, Width = 84, Height = 25, Text = L.T("main.browse", "Browse...") };
            efBrowse.Click += delegate {
                using (var d = new OpenFileDialog { Title = L.T("ed.efuse", "eFuse dump:") }) {
                    if (d.ShowDialog(this) == DialogResult.OK) efuseFile.Text = RelDir(d.FileName);
                }
            };
            idg.Controls.Add(efBrowse);
            efuseUid = UidRow(idg, L.T("ed.efuse_uid", "eFuse UID:"), 55);
            nandUid = UidRow(idg, L.T("ed.nand_uid", "NAND UID:"), 85);
            idg.Controls.Add(new Label { Left = 140, Top = 113, Width = 470, Height = 48, ForeColor = Color.DimGray,
                Text = L.T("ed.identity_hint", "eFuse dump of a real board (/sys/bus/nvmem/devices/nvmem0/nvmem); "
                    + "UIDs: 32 hex digits, make several emulated boards different. "
                    + "Vendor firmware may check the NAND UID.") });
            // front panel LEDs (shown above the router console)
            var lp = Page(tabs, L.T("ed.tab_leds", "LEDs"));
            lp.Controls.Add(new Label { Left = 10, Top = 3, Width = 590, Height = 16, ForeColor = Color.DimGray,
                Text = L.T("ed.leds_hint", "Shown above the router console. Presets of OpenWrt boards: "
                    + "tools/dts-leds.py fills them from the device tree.") });
            ledList = new ListBox { Left = 10, Top = 22, Width = 480, Height = 270, IntegralHeight = false,
                HorizontalScrollbar = true };
            lp.Controls.Add(ledList);
            var ledAdd = new Button { Left = 500, Top = 22, Width = 100, Height = 26, Text = L.T("ed.led_add", "Add...") };
            var ledEdit = new Button { Left = 500, Top = 52, Width = 100, Height = 26, Text = L.T("ed.led_edit", "Edit...") };
            var ledDel = new Button { Left = 500, Top = 82, Width = 100, Height = 26, Text = L.T("ed.led_delete", "Delete") };
            var ledUp = new Button { Left = 500, Top = 122, Width = 100, Height = 26, Text = L.T("ed.led_up", "Up") };
            var ledDown = new Button { Left = 500, Top = 152, Width = 100, Height = 26, Text = L.T("ed.led_down", "Down") };
            lp.Controls.AddRange(new Control[] { ledAdd, ledEdit, ledDel, ledUp, ledDown });
            Action edit = () => {
                var cur = ledList.SelectedItem as LedDef;
                if (cur == null) return;
                using (var f = new LedEditForm(cur)) {
                    if (f.ShowDialog(this) == DialogResult.OK) ledList.Items[ledList.SelectedIndex] = f.Result;
                }
            };
            ledAdd.Click += delegate {
                using (var f = new LedEditForm(null)) {
                    if (f.ShowDialog(this) == DialogResult.OK) ledList.SelectedIndex = ledList.Items.Add(f.Result);
                }
            };
            ledEdit.Click += delegate { edit(); };
            ledList.DoubleClick += delegate { edit(); };
            ledDel.Click += delegate {
                int i = ledList.SelectedIndex;
                if (i < 0) return;
                ledList.Items.RemoveAt(i);
                if (ledList.Items.Count > 0) ledList.SelectedIndex = Math.Min(i, ledList.Items.Count - 1);
            };
            EventHandler move = (o, e) => {
                int i = ledList.SelectedIndex, j = i + (o == ledUp ? -1 : 1);
                if (i < 0 || j < 0 || j >= ledList.Items.Count) return;
                var it = ledList.Items[i];
                ledList.Items.RemoveAt(i);
                ledList.Items.Insert(j, it);
                ledList.SelectedIndex = j;
            };
            ledUp.Click += move;
            ledDown.Click += move;
            y += tabs.Height + 8;

            var acc = new GroupBox { Left = 10, Top = y, Width = 620, Height = 90,
                Text = L.T("ed.pc_access", "Access from this PC (LAN port \"This PC only\")") };
            Controls.Add(acc);
            acc.Controls.Add(new Label { Left = 10, Top = 25, Width = 130, Text = L.T("ed.lan_ip", "Router LAN IP:") });
            lanIp = new TextBox { Left = 140, Top = 22, Width = 120 };
            acc.Controls.Add(lanIp);
            acc.Controls.Add(new Label { Left = 270, Top = 25, Width = 125, Text = L.T("ed.forwards", "Port forwards:") });
            lanFwd = new TextBox { Left = 400, Top = 22, Width = 210 };
            acc.Controls.Add(lanFwd);
            acc.Controls.Add(new Label { Left = 140, Top = 50, Width = 470, Height = 34, ForeColor = Color.DimGray,
                Text = L.T("ed.forwards_hint", "PC port:router port, comma separated, e.g. 8080:80,8443:443,8022:22: "
                    + "http://127.0.0.1:8080 opens the router's port 80.") });
            y += acc.Height + 8;

            y += 4;

            var save = new Button { Left = 150, Top = y, Width = 110, Height = 30, Text = L.T("ed.save", "Save") };
            var saveAs = new Button { Left = 266, Top = y, Width = 110, Height = 30, Text = L.T("ed.save_as", "Save as new...") };
            var del = new Button { Left = 382, Top = y, Width = 110, Height = 30, Text = L.T("ed.delete", "Delete"), Enabled = p != null };
            var cancel = new Button { Left = 510, Top = y, Width = 110, Height = 30, Text = L.T("ed.cancel", "Cancel"), DialogResult = DialogResult.Cancel };
            save.Click += delegate { DoSave(preset == null); };
            saveAs.Click += delegate { DoSave(true); };
            del.Click += delegate { DoDelete(); };
            Controls.Add(save); Controls.Add(saveAs); Controls.Add(del); Controls.Add(cancel);
            CancelButton = cancel;
            // taller than the screen: scroll
            int maxH = Screen.PrimaryScreen.WorkingArea.Height - 60;
            AutoScroll = y + 44 > maxH;
            ClientSize = new Size(AutoScroll ? 660 : 640, Math.Min(y + 44, maxH));

            gmac0.SelectedIndexChanged += delegate { UpdateEnabled(); };
            gmac1.SelectedIndexChanged += delegate { UpdateEnabled(); };
            port5.SelectedIndexChanged += delegate { UpdateEnabled(); };
            soc.SelectedIndexChanged += delegate { UpdateEnabled(); };
            autoDesc.CheckedChanged += delegate { desc.ReadOnly = autoDesc.Checked; UpdateDesc(); };
            foreach (Control c in new Control[] { soc, gmac0, gmac1, gmac0Port, gmac1Port, port5, port5Port, ddr, ram, nandSize, usbPort, pcieWifi })
                c.TextChanged += delegate { UpdateDesc(); };
            foreach (var c in swPort) c.TextChanged += delegate { UpdateDesc(); };

            Fill(p ?? Default());
            autoDesc.Checked = p == null;
            UpdateEnabled();
        }

        static Preset Default()
        {
            var p = new Preset();
            p.Set("name", "My board");
            p.Set("gmac0", "mt7531");
            p.Set("ports", "lan1:lan2:lan3:lan4:-");
            p.Set("gmac1", "rtl8221b");
            p.Set("gmac1-port", "wan");
            p.Set("nand", "128");
            p.Set("ddr", "ddr4");
            p.Set("ram", "512");
            p.Set("usb-port", "2");
            return p;
        }

        void Row(string label, Control c, ref int y, int step = 30)
        {
            Controls.Add(new Label { Left = 14, Top = y + 3, Width = 135, Text = label });
            Controls.Add(c);
            y += step;
        }

        static ComboBox Combo(Control parent, string label, ref int y, params Choice[] items)
        {
            parent.Controls.Add(new Label { Left = 10, Top = y + 3, Width = 130, Text = label });
            var c = new ComboBox { Left = 140, Top = y, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(items);
            parent.Controls.Add(c);
            y += 30;
            return c;
        }

        static TabPage Page(TabControl tabs, string title)
        {
            var tp = new TabPage(title) { UseVisualStyleBackColor = true };
            tabs.TabPages.Add(tp);
            return tp;
        }

        TextBox UidRow(Control parent, string label, int top)
        {
            parent.Controls.Add(new Label { Left = 10, Top = top + 3, Width = 130, Text = label });
            var t = new TextBox { Left = 140, Top = top, Width = 280, MaxLength = 32 };
            parent.Controls.Add(t);
            var r = new Button { Left = 426, Top = top - 1, Width = 100, Height = 25, Text = L.T("ed.random", "Random") };
            r.Click += delegate {
                var b = new byte[16];
                new Random().NextBytes(b);
                t.Text = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            };
            parent.Controls.Add(r);
            return t;
        }

        static bool ValidUid(string s)
        {
            if (s.Length == 0) return true;
            if (s.Length != 32) return false;
            foreach (char c in s) if (Uri.IsHexDigit(c) == false) return false;
            return true;
        }

        static ComboBox PortCombo(Control parent, string label, ref int y, out NumericUpDown rst)
        {
            parent.Controls.Add(new Label { Left = 10, Top = y + 3, Width = 130, Text = label });
            var c = new ComboBox { Left = 140, Top = y, Width = 88, DropDownStyle = ComboBoxStyle.DropDown };
            c.Items.AddRange(PortIds);
            parent.Controls.Add(c);
            parent.Controls.Add(new Label { Left = 240, Top = y + 3, Width = 230, Text = L.T("ed.phy_reset", "PHY reset GPIO (-1 = not wired):") });
            rst = new NumericUpDown { Left = 470, Top = y, Width = 60, Minimum = -1, Maximum = 100, Value = -1 };
            parent.Controls.Add(rst);
            y += 30;
            return c;
        }

        static void SelectValue(ComboBox c, string v)
        {
            for (int i = 0; i < c.Items.Count; i++)
                if (((Choice)c.Items[i]).Value == v) { c.SelectedIndex = i; return; }
            c.SelectedIndex = 0;
        }

        // (null while Fill() is still selecting the other lists)
        static string Val(ComboBox c) { var ch = c.SelectedItem as Choice; return ch != null ? ch.Value : ""; }

        static int Int(string s, int def) { int v; return int.TryParse(s, out v) ? v : def; }

        // QEMU bool option values
        static bool IsOn(string v) { v = v.ToLowerInvariant(); return v == "on" || v == "true" || v == "yes" || v == "1"; }

        static decimal Clamp(NumericUpDown n, int v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        void Fill(Preset p)
        {
            name.Text = p.Name;
            desc.Text = p.Description;
            SelectValue(soc, p.Soc);
            SelectValue(gmac0, p.Get("gmac0", "mt7531"));
            SelectValue(gmac1, p.Get("gmac1", "rtl8221b"));
            string[] ports = p.Get("ports", "lan1:lan2:lan3:lan4").Split(':');
            for (int i = 0; i < 5; i++) swPort[i].Text = i < ports.Length ? ports[i] : "-";
            SelectValue(port5, p.Get("port5", "none"));
            port5Port.Text = ports.Length > 5 ? ports[5] : "lan5";
            port5Addr.Value = Clamp(port5Addr, Int(p.Get("port5-phy-addr"), 5));
            port5Rst.Value = Clamp(port5Rst, Int(p.Get("port5-reset-gpio"), -1));
            gmac0Port.Text = p.Get("gmac0-port", "lan1");
            gmac1Port.Text = p.Get("gmac1-port", "wan");
            gmac0Rst.Value = Clamp(gmac0Rst, Int(p.Get("gmac0-reset-gpio"), -1));
            gmac1Rst.Value = Clamp(gmac1Rst, Int(p.Get("gmac1-reset-gpio"), -1));
            SelectValue(ddr, p.Get("ddr", "ddr4"));
            SelectValue(ram, p.Get("ram", "512"));
            SelectValue(nandSize, p.Get("flash", "nand") == "nor"
                ? "nor:" + p.Get("nor", "16") + ":" + p.Get("nor-id", "ef4018").ToLowerInvariant()
                : p.Get("flash", "nand") == "emmc" ? "emmc:0"
                : "nand:" + p.Get("nand", "128"));
            SelectValue(usbPort, p.Get("usb-port", "2"));
            SelectValue(pcieWifi, p.Get("pcie-wifi", "none"));
            resetGpio.Value = Clamp(resetGpio, Int(p.Get("reset-gpio"), 1));
            wpsGpio.Value = Clamp(wpsGpio, Int(p.Get("wps-gpio"), 0));
            resetHigh.Checked = IsOn(p.Get("reset-active-high"));
            wpsHigh.Checked = IsOn(p.Get("wps-active-high"));
            nandDir.Text = p.Get("nand-dir", "nand");
            lanIp.Text = p.LanIp;
            lanFwd.Text = p.LanForwards;
            SelectValue(poweroff, p.Get("poweroff", "stop"));
            efuseFile.Text = p.Get("efuse");
            efuseUid.Text = p.Get("efuse-uid");
            nandUid.Text = p.Get("nand-uid");
            ledList.Items.Clear();
            foreach (var d in LedDef.FromPreset(p)) ledList.Items.Add(d);
        }

        void UpdateEnabled()
        {
            bool sw = Val(gmac0) == "mt7531";
            foreach (var c in swPort) c.Enabled = sw;
            port5.Enabled = sw;
            bool p5 = sw && Val(port5) != "none";
            port5Port.Enabled = port5Addr.Enabled = port5Rst.Enabled = p5;
            gmac0Port.Enabled = ExtPhy(Val(gmac0));
            gmac0Rst.Enabled = ExtPhy(Val(gmac0));
            gmac1Port.Enabled = Val(gmac1) != "none";
            gmac1Rst.Enabled = ExtPhy(Val(gmac1));
            pcieWifi.Enabled = Val(soc) == "mt7987";      // only the MT7987 machine has the slot
            UpdateDesc();
        }

        // a 2.5G PHY chip with its own reset line
        static bool ExtPhy(string v) { return v == "rtl8221b" || v == "yt8821" || v == "gpy211"; }

        // a 2.5G PHY: external chip or the MT7987 internal one
        static bool Phy25(string v) { return ExtPhy(v) || v == "i2p5ge"; }

        static string PhyName(string v)
        {
            return v == "yt8821" ? "Motorcomm YT8821" : v == "gpy211" ? "MaxLinear GPY211"
                 : v == "i2p5ge" ? "internal PHY" : "RTL8221B";
        }

        // e.g. "2.5G WAN RTL8221B, 4x1G LAN MT7531, DDR4 512 MB, NAND 128 MB, USB 2.0"
        string Summary()
        {
            var parts = new List<string>();
            if (Val(soc) != "mt7981") parts.Add(Val(soc).ToUpperInvariant());
            int swn = 0; bool swWan = false;
            if (Val(gmac0) == "mt7531") {
                foreach (var c in swPort) {
                    string t = c.Text.Trim();
                    if (t == "" || t == "-") continue;
                    if (t == "wan") swWan = true; else swn++;
                }
            }
            if (ExtPhy(Val(gmac0))) parts.Add("2.5G " + gmac0Port.Text.Trim().ToUpperInvariant() + " " + PhyName(Val(gmac0)) + " on GMAC0");
            if (Phy25(Val(gmac1))) parts.Add("2.5G " + gmac1Port.Text.Trim().ToUpperInvariant() + " " + PhyName(Val(gmac1)));
            if (Val(gmac0) == "mt7531" && ExtPhy(Val(port5)))
                parts.Add("2.5G " + port5Port.Text.Trim().ToUpperInvariant() + " " + PhyName(Val(port5)) + " on switch port 5");
            if (Val(gmac1) == "gphy") parts.Add("1G " + gmac1Port.Text.Trim().ToUpperInvariant() + " built-in PHY");
            if (Val(gmac0) == "mt7531") {
                if (swWan) parts.Add((swn + 1) + "x1G MT7531 (WAN = port " + WanPort() + ")");
                else parts.Add(swn + "x1G LAN MT7531");
            }
            // (English: the description is stored in the preset file)
            parts.Add(Val(ddr).ToUpperInvariant() + " " + (Val(ram) == "1024" ? "1 GB" : Val(ram) == "2048" ? "2 GB" : Val(ram) + " MB"));
            var fl = Val(nandSize).Split(':');
            parts.Add(fl[0] == "emmc" ? "eMMC" : (fl[0] == "nor" ? "SPI-NOR " : "NAND ") + fl[1] + " MB");
            parts.Add(Val(usbPort) == "none" ? "no USB" : "USB " + Val(usbPort) + ".0");
            if (Val(soc) == "mt7987" && Val(pcieWifi) == "mt7992") parts.Add("Wi-Fi 7 MT7992 on PCIe");
            return string.Join(", ", parts.ToArray());
        }

        int WanPort()
        {
            for (int i = 0; i < 5; i++) if (swPort[i].Text.Trim() == "wan") return i;
            return -1;
        }

        void UpdateDesc()
        {
            if (autoDesc != null && autoDesc.Checked && soc.SelectedItem != null && port5.SelectedItem != null &&
                gmac0.SelectedItem != null && gmac1.SelectedItem != null &&
                ddr.SelectedItem != null && ram.SelectedItem != null && nandSize.SelectedItem != null && usbPort.SelectedItem != null)
                desc.Text = Summary();
        }

        string FullDir(string d) { return Path.IsPathRooted(d) ? d : Path.Combine(root, d); }

        string RelDir(string d)
        {
            string r = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return d.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? d.Substring(r.Length) : d;
        }

        Preset Build()
        {
            var p = new Preset();
            p.Set("name", name.Text.Trim());
            p.Set("description", desc.Text.Trim());
            if (Val(soc) != "mt7981") p.Set("soc", Val(soc));
            p.Set("gmac0", Val(gmac0));
            if (Val(gmac0) == "mt7531") {
                var ports = new List<string>();
                foreach (var c in swPort) ports.Add(c.Text.Trim().Length > 0 ? c.Text.Trim() : "-");
                if (Val(port5) != "none") {
                    ports.Add(port5Port.Text.Trim().Length > 0 ? port5Port.Text.Trim() : "-");
                    p.Set("ports", string.Join(":", ports.ToArray()));
                    p.Set("port5", Val(port5));
                    p.Set("port5-phy-addr", port5Addr.Value.ToString());
                    if (port5Rst.Value >= 0) p.Set("port5-reset-gpio", port5Rst.Value.ToString());
                } else {
                    p.Set("ports", string.Join(":", ports.ToArray()));
                }
            } else if (ExtPhy(Val(gmac0))) {
                p.Set("gmac0-port", gmac0Port.Text.Trim());
                if (gmac0Rst.Value >= 0) p.Set("gmac0-reset-gpio", gmac0Rst.Value.ToString());
            }
            p.Set("gmac1", Val(gmac1));
            if (Val(gmac1) != "none") {
                p.Set("gmac1-port", gmac1Port.Text.Trim());
                if (ExtPhy(Val(gmac1)) && gmac1Rst.Value >= 0)
                    p.Set("gmac1-reset-gpio", gmac1Rst.Value.ToString());
            }
            var flash = Val(nandSize).Split(':');
            if (flash[0] == "nor") {
                p.Set("flash", "nor");
                p.Set("nor", flash[1]);
                p.Set("nor-id", flash[2]);
            } else if (flash[0] == "emmc") {
                p.Set("flash", "emmc");
            } else {
                p.Set("nand", flash[1]);
            }
            p.Set("ddr", Val(ddr));
            p.Set("ram", Val(ram));
            p.Set("usb-port", Val(usbPort));
            if (Val(soc) == "mt7987" && Val(pcieWifi) != "none") p.Set("pcie-wifi", Val(pcieWifi));
            p.Set("reset-gpio", resetGpio.Value.ToString());
            p.Set("wps-gpio", wpsGpio.Value.ToString());
            if (resetHigh.Checked) p.Set("reset-active-high", "on");
            if (wpsHigh.Checked) p.Set("wps-active-high", "on");
            p.Set("lan-ip", lanIp.Text.Trim());
            p.Set("lan-forwards", lanFwd.Text.Trim().Replace(" ", ""));
            p.Set("poweroff", Val(poweroff));
            if (efuseFile.Text.Trim().Length > 0) p.Set("efuse", efuseFile.Text.Trim());
            if (efuseUid.Text.Trim().Length > 0) p.Set("efuse-uid", efuseUid.Text.Trim().ToLowerInvariant());
            if (nandUid.Text.Trim().Length > 0) p.Set("nand-uid", nandUid.Text.Trim().ToLowerInvariant());
            p.Set("nand-dir", nandDir.Text.Trim());
            // keep keys this editor does not know (openwrt=..., new options)
            if (preset != null) {
                foreach (var kv in preset.Values)
                    if (p.Get(kv.Key, null) == null && !Known.Contains(kv.Key) && !LedDef.IsKey(kv.Key))
                        p.Set(kv.Key, kv.Value);
            }
            for (int i = 0; i < ledList.Items.Count; i++) p.Set("led" + (i + 1), ledList.Items[i].ToString());
            return p;
        }

        // the same port name on two ports would leave one of them unconnected
        string CheckPorts(Preset p)
        {
            var seen = new List<string>();
            var names = new List<string>();
            if (p.Get("gmac0") == "mt7531") names.AddRange(p.Get("ports").Split(':'));
            if (ExtPhy(p.Get("gmac0"))) names.Add(p.Get("gmac0-port"));
            if (p.Get("gmac1") != "none") names.Add(p.Get("gmac1-port"));
            foreach (var n in names) {
                if (n == "-" || n == "") continue;
                if (seen.Contains(n)) return L.F("ed.port_twice", "Port name \"{0}\" is used twice.", n);
                seen.Add(n);
            }
            return null;
        }

        void DoSave(bool asNew)
        {
            var p = Build();
            if (p.Name.Length == 0) { MessageBox.Show(this, L.T("ed.enter_name", "Enter a name."), Text); return; }
            string err = CheckPorts(p);
            if (err == null && p.Soc != "mt7981" && p.Get("gmac1") == "gphy")
                err = L.T("ed.no_gphy", "Only the MT7981 has a built-in 1G PHY: choose another GMAC1 PHY.");
            if (err == null && p.Soc != "mt7987" && (p.Get("gmac0") == "i2p5ge" || p.Get("gmac1") == "i2p5ge"))
                err = L.T("ed.no_i2p5ge", "Only the MT7987 has an internal 2.5G PHY.");
            if (err == null && (!ValidUid(efuseUid.Text.Trim()) || !ValidUid(nandUid.Text.Trim())))
                err = L.T("ed.bad_uid", "A UID must be 32 hex digits (or empty).");
            if (err == null && !Preset.ValidIp(p.LanIp))
                err = L.F("ed.bad_ip", "\"{0}\" is not an IPv4 address of a host.", p.LanIp);
            if (err == null && Preset.ParseForwards(p.LanForwards) == null)
                err = L.T("ed.bad_forwards", "Port forwards must look like 8080:80,8443:443,8022:22 (ports 1..65535).");
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (asNew) {
                p.FilePath = Path.Combine(presetDir, Preset.FileNameFor(p.Name));
                if (File.Exists(p.FilePath) &&
                    MessageBox.Show(this, L.F("ed.replace", "A preset file {0} already exists. Replace it?", Path.GetFileName(p.FilePath)),
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            } else {
                p.FilePath = preset.FilePath;
            }
            try {
                Directory.CreateDirectory(presetDir);
                p.Save();
            } catch (Exception e) {
                MessageBox.Show(this, L.F("ed.cannot_save", "Cannot save the preset:\n{0}", e.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Result = p;
            DialogResult = DialogResult.OK;
        }

        void DoDelete()
        {
            if (MessageBox.Show(this, L.F("ed.ask_delete", "Delete the preset \"{0}\" ({1})? The flash folder is not touched.",
                                preset.Name, Path.GetFileName(preset.FilePath)), Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try {
                File.Delete(preset.FilePath);
            } catch (Exception e) {
                MessageBox.Show(this, L.F("ed.cannot_delete", "Cannot delete:\n{0}", e.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Deleted = true;
            DialogResult = DialogResult.OK;
        }
    }
}

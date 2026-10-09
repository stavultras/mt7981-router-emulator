// Router Emulator launcher for Windows.
//
// Starts qemu\qemu-system-aarch64.exe (machine mt7981-router or
// mt7986-router, from the preset) with the
// hardware of the selected board preset (presets\*.ini, editable in the
// preset editor), NAND folder, network attachments (Npcap adapter / NAT /
// host access) and USB folder; the router's serial console opens in its
// own window.  Board buttons (reset, WPS) are sent through QMP.
//
// Build: see build-windows.sh (mcs against the .NET Framework 4.8
// reference assemblies), or with csc.exe on Windows:
//   csc -target:winexe -out:emulator.exe Launcher.cs Presets.cs Lang.cs Terminal.cs Leds.cs UsbDevices.cs Version.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RouterEmulator
{
    class NetChoice
    {
        public string Kind;     // "nat", "host", "none", "pcap"
        public string Device;   // pcap device name
        public string Label;
        public override string ToString() { return Label; }
    }

    class MainForm : Form
    {
        readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        string PresetDir { get { return Path.Combine(root, "presets"); } }
        ComboBox board, wan, lan;
        Label boardDesc;
        Button btnEdit;
        TextBox nand, usb, logs;
        CheckBox useUsb, gpioLog, useLogs;
        Button nandBrowse, usbBrowse, logsBrowse, usbDevRefresh;
        TextBox usbDev;
        // passed through USB devices: "vid:pid" -> name
        List<KeyValuePair<string, string>> usbDevs = new List<KeyValuePair<string, string>>();
        Button start, btnFactory, btnWps, btnPower, btnTftp, btnNew, btnTerm;
        // buttons held for 10 s: locked, counting down on their text
        readonly HashSet<Button> counting = new HashSet<Button>();
        Label status;
        Process qemu;
        TerminalForm term;
        int qmpPort;
        Dictionary<string, string> cfg = new Dictionary<string, string>();
        ComboBox language;
        // re-apply the texts after a language switch
        readonly List<Action> relang = new List<Action>();
        Func<string> statusText;

        string LangDir { get { return Path.Combine(root, "languages"); } }

        // set a control's text now and after every language switch
        void Tr(Control c, string key, string en)
        {
            Action a = () => c.Text = L.T(key, en);
            a();
            relang.Add(a);
        }

        void SetStatus(Func<string> f)
        {
            statusText = f;
            status.Text = f();
        }

        string CfgPath { get { return Path.Combine(root, "emulator.ini"); } }

        MainForm()
        {
            Text = "Router Emulator " + AppVersion.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ClientSize = new Size(620, 538);
            Font = new Font("Segoe UI", 9f);
            LoadCfg();
            var langs = L.Available(LangDir);
            LangInfo lang = null;
            foreach (var li in langs) {
                string f = Path.GetFileName(li.FilePath);
                if (f.Equals(Get("lang"), StringComparison.OrdinalIgnoreCase)) lang = li;
            }
            if (lang == null) {
                // first start: the system language if there is a file for it
                string sys = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName + ".ini";
                foreach (var li in langs)
                    if (Path.GetFileName(li.FilePath).Equals(sys, StringComparison.OrdinalIgnoreCase)) lang = li;
            }
            L.Load(lang != null ? lang.FilePath : null);

            int y = 14;
            // language list (languages\*.ini), switched without a restart
            AddLabel("main.language", "Language:", y);
            language = new ComboBox { Left = 130, Top = y, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var li in langs) language.Items.Add(li);
            if (lang != null) language.SelectedItem = lang;
            language.SelectedIndexChanged += delegate {
                var li = language.SelectedItem as LangInfo;
                L.Load(li != null ? li.FilePath : null);
                foreach (var a in relang) a();
            };
            language.Enabled = langs.Count > 1;
            Controls.Add(language);
            y += 34;

            AddLabel("main.preset", "Board preset:", y);
            board = new ComboBox { Left = 130, Top = y, Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(board);
            btnEdit = new Button { Left = 426, Top = y - 1, Width = 84, Height = 25 };
            Tr(btnEdit, "main.edit", "Edit...");
            btnEdit.Click += delegate { EditPreset((Preset)board.SelectedItem); };
            Controls.Add(btnEdit);
            btnNew = new Button { Left = 516, Top = y - 1, Width = 84, Height = 25 };
            Tr(btnNew, "main.new", "New...");
            btnNew.Click += delegate { EditPreset(null); };
            Controls.Add(btnNew);
            y += 28;
            boardDesc = new Label { Left = 130, Top = y, Width = 470, Height = 32, ForeColor = Color.DimGray };
            Controls.Add(boardDesc);
            y += 38;

            AddLabel("main.nand", "Flash folder:", y);
            nand = new TextBox { Left = 130, Top = y, Width = 380 };
            Controls.Add(nand);
            nandBrowse = AddBrowse(nand, y);
            y += 22;
            var nandHint = new Label { Left = 130, Top = y, Width = 470, Height = 18, ForeColor = Color.DimGray };
            Tr(nandHint, "main.nand_hint", "Files *.mtd0.BL2.bin, *.mtd1.*.bin ... are joined in mtd order into the flash.");
            Controls.Add(nandHint);
            y += 28;

            AddLabel("main.wan", "WAN port:", y);
            wan = new ComboBox { Left = 130, Top = y, Width = 470, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(wan);
            y += 34;

            AddLabel("main.lan", "LAN port:", y);
            lan = new ComboBox { Left = 130, Top = y, Width = 470, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(lan);
            y += 24;
            var lanHint = new Label { Left = 130, Top = y, Width = 470, Height = 18, ForeColor = Color.DimGray };
            Tr(lanHint, "main.lan_hint", "Always connected to the router's first LAN / Ethernet port.");
            Controls.Add(lanHint);
            y += 28;

            useUsb = new CheckBox { Left = 14, Top = y, Width = 115 };
            Tr(useUsb, "main.usb", "USB folder:");
            Controls.Add(useUsb);
            usb = new TextBox { Left = 130, Top = y, Width = 380 };
            Controls.Add(usb);
            usbBrowse = AddBrowse(usb, y);
            y += 34;

            // USB devices of this PC (e.g. a Wi-Fi dongle) passed through to the router
            AddLabel("main.usb_dev", "USB devices:", y);
            usbDev = new TextBox { Left = 130, Top = y, Width = 380, ReadOnly = true };
            Controls.Add(usbDev);
            usbDevRefresh = new Button { Left = 516, Top = y - 1, Width = 84, Height = 25 };
            Tr(usbDevRefresh, "main.choose", "Choose...");
            usbDevRefresh.Click += delegate {
                using (var f = new UsbDevForm(usbDevs)) {
                    if (f.ShowDialog(this) == DialogResult.OK) { usbDevs = f.Result; ShowUsbDevs(); }
                }
            };
            Controls.Add(usbDevRefresh);
            y += 24;
            var usbDevHint = new Label { Left = 130, Top = y, Width = 470, Height = 32, ForeColor = Color.DimGray };
            Tr(usbDevHint, "main.usb_dev_hint", "Passed through to the router (e.g. a Wi-Fi dongle, OpenWrt "
                + "needs its driver). Needs UsbDk (github.com/daynix/UsbDk) or the WinUSB driver.");
            Controls.Add(usbDevHint);
            y += 38;

            useLogs = new CheckBox { Left = 14, Top = y, Width = 115 };
            Tr(useLogs, "main.logs", "Log folder:");
            Controls.Add(useLogs);
            logs = new TextBox { Left = 130, Top = y, Width = 380 };
            Controls.Add(logs);
            logsBrowse = AddBrowse(logs, y);
            y += 34;

            gpioLog = new CheckBox { Left = 130, Top = y, Width = 470 };
            Tr(gpioLog, "main.gpio_log", "Show LED / GPIO changes in the console");
            Controls.Add(gpioLog);
            y += 36;

            start = new Button { Left = 130, Top = y, Width = 150, Height = 30 };
            relang.Add(() => start.Text = Running ? L.T("main.power_off", "Power off") : L.T("main.power_on", "Power on"));
            start.Click += delegate { if (qemu == null || qemu.HasExited) Start(0); else Stop(); };
            Controls.Add(start);
            btnPower = new Button { Left = 290, Top = y, Width = 150, Height = 30, Enabled = false };
            Tr(btnPower, "main.power_cycle", "Power cycle");
            btnPower.Click += delegate { Qmp("{\"execute\":\"system_reset\"}"); };
            Controls.Add(btnPower);
            y += 40;

            btnFactory = new Button { Left = 130, Top = y, Width = 310, Height = 28, Enabled = false };
            Tr(btnFactory, "main.reset_factory", "Reset: 10 s (factory)");
            btnFactory.Click += delegate {
                if (MessageBox.Show(this, L.T("ask.factory", "Holding reset for 10 s makes OpenWrt erase all settings "
                        + "(factory reset). Continue?"), Text, MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) == DialogResult.Yes) {
                    PressButton("reset-button", 10000);
                    Countdown(btnFactory, "main.reset_factory", "Reset: 10 s (factory)", () => Running);
                }
            };
            Controls.Add(btnFactory);
            btnWps = new Button { Left = 450, Top = y, Width = 150, Height = 28, Enabled = false };
            Tr(btnWps, "main.wps", "WPS button");
            btnWps.Click += delegate { PressButton("wps-button", 1000); };
            Controls.Add(btnWps);
            y += 34;

            // like the real board: hold reset, apply power, release after 10 s;
            // U-Boot then loads the recovery image via TFTP (OpenWrt U-Boot: server
            // 192.168.1.254; some vendor U-Boots: 192.168.1.88, file recovery.bin)
            // (only while the router is off: it is "plugging in the power")
            btnTftp = new Button { Left = 130, Top = y, Width = 310, Height = 28 };
            Tr(btnTftp, "main.tftp", "Power + Reset: 10 s (TFTP recovery)");
            btnTftp.Click += delegate {
                if (Running) return;
                Start(10000);
                if (Running)
                    Countdown(btnTftp, "main.tftp", "Power + Reset: 10 s (TFTP recovery)", () => !Running);
            };
            Controls.Add(btnTftp);

            y += 38;

            btnTerm = new Button { Left = 450, Top = start.Top, Width = 150, Height = 30, Enabled = false };
            Tr(btnTerm, "main.show_console", "Show console");
            btnTerm.Click += delegate { if (term != null && !term.IsDisposed) { term.Show(); term.Activate(); } };
            Controls.Add(btnTerm);

            status = new Label { Left = 14, Top = y, Width = 590, Height = 32, ForeColor = Color.DarkBlue, AutoEllipsis = true };
            Controls.Add(status);
            ClientSize = new Size(620, status.Bottom + 6);   // fit the contents
            relang.Add(() => { if (statusText != null) status.Text = statusText(); });
            relang.Add(RefillNetworks);
            relang.Add(() => { if (board.Items.Count == 0) boardDesc.Text = NoPresetsText(); });

            FillNetworks();
            board.SelectedIndexChanged += delegate { OnBoardChanged(); };
            FillPresets(Get("preset"));
            if (Get("nand") != "") nand.Text = Get("nand");
            usb.Text = Get("usb", Path.Combine(root, "usb"));
            useUsb.Checked = Get("useusb", "1") == "1";
            logs.Text = Get("logs", Path.Combine(root, "logs"));
            useLogs.Checked = Get("uselogs", "1") == "1";
            useUsb.CheckedChanged += delegate { UpdateFolders(); };
            useLogs.CheckedChanged += delegate { UpdateFolders(); };
            UpdateFolders();
            gpioLog.Checked = Get("gpiolog", "0") == "1";
            foreach (var d in Get("usbdev", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                int eq = d.IndexOf('=');
                usbDevs.Add(eq > 0 ? new KeyValuePair<string, string>(d.Substring(0, eq), d.Substring(eq + 1))
                                   : new KeyValuePair<string, string>(d, d));
            }
            ShowUsbDevs();
            relang.Add(ShowUsbDevs);
            Select(wan, Get("wan", "nat"));
            Select(lan, Get("lan", "host"));
            FormClosing += (o, e) => {
                // closing the launcher powers the router off: QEMU has no
                // window of its own and would keep running unseen
                if (Running) {
                    var r = MessageBox.Show(this, L.T("ask.power_off", "Power off the router?"), Text,
                                            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) { e.Cancel = true; return; }
                    PowerOff(qemu, 5000);
                }
                if (term != null && !term.IsDisposed) { term.AskClose = null; term.Close(); }
                SaveCfg();
            };
            foreach (var a in relang) a();      // texts depending on the state
            SetStatus(() => NpcapInstalled()
                ? L.T("status.ready", "Ready.")
                : L.T("status.no_npcap", "Npcap is not installed: only NAT / host access are available. "
                  + "Install it from https://npcap.com to attach router ports to a network adapter."));
        }

        void AddLabel(string key, string en, int y)
        {
            var l = new Label { Left = 14, Top = y + 3, Width = 115 };
            Tr(l, key, en);
            Controls.Add(l);
        }

        bool Running { get { return qemu != null && !qemu.HasExited; } }

        string NoPresetsText()
        {
            return L.F("main.no_presets", "No presets in {0}: create one with New...", PresetDir);
        }

        // network lists carry translated labels: rebuild, keep the selection
        void RefillNetworks()
        {
            if (wan.SelectedItem == null) return;
            string w = Key((NetChoice)wan.SelectedItem), l = Key((NetChoice)lan.SelectedItem);
            wan.Items.Clear();
            lan.Items.Clear();
            FillNetworks();
            Select(wan, w);
            Select(lan, l);
        }

        Button AddBrowse(TextBox box, int y)
        {
            var b = new Button { Left = 516, Top = y - 1, Width = 84, Height = 25 };
            Tr(b, "main.browse", "Browse...");
            b.Click += delegate {
                using (var d = new FolderBrowserDialog { SelectedPath = box.Text }) {
                    if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
                }
            };
            Controls.Add(b);
            return b;
        }

        // folder fields are editable only when their box is ticked (and
        // the router is off; USB only on boards with a USB port)
        void UpdateFolders()
        {
            bool off = qemu == null || qemu.HasExited;
            nand.Enabled = nandBrowse.Enabled = off;
            usb.Enabled = usbBrowse.Enabled = off && useUsb.Enabled && useUsb.Checked;
            logs.Enabled = logsBrowse.Enabled = off && useLogs.Checked;
        }

        string NandOf(Preset p)
        {
            string d = p.Get("nand-dir", "nand");
            return Path.IsPathRooted(d) ? d : Path.Combine(root, d);
        }

        // (re)load presets\*.ini and select the given file (name only)
        void FillPresets(string select)
        {
            board.Items.Clear();
            foreach (var p in Preset.LoadAll(PresetDir)) board.Items.Add(p);
            int idx = 0;
            for (int i = 0; i < board.Items.Count; i++)
                if (Path.GetFileName(((Preset)board.Items[i]).FilePath) == select) idx = i;
            if (board.Items.Count > 0) {
                board.SelectedIndex = idx;
            } else {
                boardDesc.Text = NoPresetsText();
                OnBoardChanged();
            }
        }

        void OnBoardChanged()
        {
            var b = board.SelectedItem as Preset;
            btnEdit.Enabled = b != null;
            start.Enabled = b != null || (qemu != null && !qemu.HasExited);
            if (b == null) return;
            boardDesc.Text = b.Description;
            RefillNetworks();       // "This PC only" shows the preset's forwards
            // follow the preset's NAND folder unless the user picked another one
            string cur = nand.Text;
            bool presetDir = cur == "";
            foreach (Preset o in board.Items)
                if (string.Equals(cur, NandOf(o), StringComparison.OrdinalIgnoreCase)) presetDir = true;
            if (presetDir) nand.Text = NandOf(b);
            useUsb.Enabled = b.HasUsb && (qemu == null || qemu.HasExited);
            if (usbBrowse != null) UpdateFolders();
        }

        void EditPreset(Preset p)
        {
            using (var f = new PresetForm(PresetDir, root, p)) {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string sel = f.Deleted ? "" : Path.GetFileName(f.Result.FilePath);
                if (f.Result != null && p != null && string.Equals(nand.Text, NandOf(p), StringComparison.OrdinalIgnoreCase))
                    nand.Text = NandOf(f.Result);
                FillPresets(sel);
            }
        }

        static bool NpcapInstalled()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(sys, "Npcap", "wpcap.dll"))
                || File.Exists(Path.Combine(sys, "wpcap.dll"));
        }

        void FillNetworks()
        {
            var adapters = new List<NetChoice>();
            if (NpcapInstalled()) {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;
                    string wifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                        ? L.T("net.wifi", " - Wi-Fi: usually cannot bridge") : "";
                    adapters.Add(new NetChoice {
                        Kind = "pcap", Device = "\\Device\\NPF_" + ni.Id,
                        Label = L.F("net.bridge", "Bridge to: {0} ({1})", ni.Name, ni.Description) + wifi
                    });
                }
            }
            wan.Items.Add(new NetChoice { Kind = "nat", Label = L.T("net.nat", "NAT through this PC (router WAN gets 10.0.2.15)") });
            foreach (var a in adapters) wan.Items.Add(a);
            wan.Items.Add(new NetChoice { Kind = "none", Label = L.T("net.none", "Not connected") });

            var bp = board.SelectedItem as Preset;
            lan.Items.Add(new NetChoice { Kind = "host",
                Label = L.F("net.host", "This PC only: {0}", bp != null ? bp.ForwardSummary()
                    : "127.0.0.1:8080 -> " + Preset.DefaultLanIp + ":80") });
            foreach (var a in adapters)
                lan.Items.Add(new NetChoice { Kind = a.Kind, Device = a.Device,
                    Label = a.Label + L.T("net.dhcp_warn", "  (router DHCP server becomes visible there!)") });
            lan.Items.Add(new NetChoice { Kind = "none", Label = L.T("net.none", "Not connected") });
        }

        static void Select(ComboBox box, string key)
        {
            for (int i = 0; i < box.Items.Count; i++) {
                var c = (NetChoice)box.Items[i];
                if (c.Kind == key || c.Device == key) { box.SelectedIndex = i; return; }
            }
            box.SelectedIndex = 0;
        }

        static string Key(NetChoice c) { return c.Kind == "pcap" ? c.Device : c.Kind; }

        static string Esc(string s) { return s.Replace(",", ",,"); }

        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char ch in a) {
                if (ch == '\\') { bs++; continue; }
                if (ch == '"') { sb.Append('\\', bs * 2 + 1); bs = 0; sb.Append('"'); continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(ch);
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }

        string NetArgs(string id, NetChoice c, Preset b)
        {
            switch (c.Kind) {
            case "nat":
                return "-netdev user,id=" + id;
            case "host":
                return b.HostOnlyNetdev(id);
            case "pcap":
                return "-netdev pcap,id=" + id + ",ifname=" + Esc(c.Device);
            }
            return null;
        }

        void Start(int resetHoldMs)
        {
            string exe = Path.Combine(root, "qemu", "qemu-system-aarch64.exe");
            if (!File.Exists(exe)) { Error(L.F("err.not_found", "Not found: {0}", exe)); return; }
            if (!Directory.Exists(nand.Text)) { Error(L.F("err.no_nand", "Flash folder does not exist:\n{0}", nand.Text)); return; }
            var b = board.SelectedItem as Preset;
            if (b == null) { Error(L.T("err.no_preset", "Select or create a board preset first.")); return; }
            var w = (NetChoice)wan.SelectedItem;
            var l = (NetChoice)lan.SelectedItem;
            if (w.Kind == "pcap" && l.Kind == "pcap" && w.Device == l.Device &&
                MessageBox.Show(this, L.T("ask.same_adapter", "WAN and LAN are bridged to the same adapter. The router's DHCP "
                    + "server will answer clients of that network. Continue?"), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            // QEMU cannot start when a port of the "This PC only" forwards is
            // taken (e.g. by another emulator): say which, before starting it
            if (l.Kind == "host") {
                var busy = new List<string>();
                foreach (var f in Preset.ParseForwards(b.LanForwards) ?? new List<int[]>())
                    if (!PortFree(f[0])) busy.Add(f[0] + " (" + L.T("err.router_port", "router port") + " " + f[1] + ")");
                if (busy.Count > 0) {
                    Error(L.F("err.ports_busy", "These ports of this PC are already in use by another program:\n{0}\n\n"
                        + "Close that program (e.g. another running emulator) or change the port forwards "
                        + "of the preset (Edit... > Access from this PC).", string.Join("\n", busy.ToArray())));
                    return;
                }
            }

            qmpPort = FreePort();
            int conPort = FreePort();
            // the LED panel polls QEMU on a QMP socket of its own
            var ledDefs = LedDef.FromPreset(b);
            int ledPort = ledDefs.Count > 0 ? FreePort() : 0;
            // serial console (+ QEMU monitor via Ctrl-A C) on a local socket,
            // shown in the built-in terminal; QEMU waits until it connects
            var args = new List<string> {
                "-M", b.Machine + ",nand-dir=" + Esc(nand.Text) + b.MachineOptions(root)
                      + (gpioLog.Checked ? ",gpio-log=on" : "")
                      + (usbDevs.Count > 0 ? ",usb-host=" + string.Join(";", usbDevs.ConvertAll(d => d.Key).ToArray()) : "")
                      + (resetHoldMs > 0 ? ",reset-hold=" + resetHoldMs : ""),
                "-m", b.RamMB + "M",
                "-display", "none",
                "-qmp", "tcp:127.0.0.1:" + qmpPort + ",server=on,wait=off",
                "-chardev", "socket,id=con,mux=on,host=127.0.0.1,port=" + conPort + ",server=on,wait=on",
                "-serial", "chardev:con",
                "-mon", "chardev=con",
            };
            if (ledPort != 0) {
                args.Add("-qmp");
                args.Add("tcp:127.0.0.1:" + ledPort + ",server=on,wait=off");
            }
            ConsoleLog log = null;
            string logPath = null;
            if (useLogs.Checked) {
                try {
                    Directory.CreateDirectory(logs.Text);
                    logPath = Path.Combine(logs.Text, "console_"
                        + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
                    log = new ConsoleLog(logPath);
                } catch (Exception e) {
                    Error(L.F("err.log", "Cannot create the console log:\n{0}", e.Message));
                    return;
                }
            }
            string a;
            if ((a = NetArgs("wan", w, b)) != null) args.AddRange(SplitArg(a));
            if ((a = NetArgs("lan1", l, b)) != null) args.AddRange(SplitArg(a));
            if (useUsb.Checked && b.HasUsb) {
                if (!Directory.Exists(usb.Text)) Directory.CreateDirectory(usb.Text);
                args.Add("-blockdev");
                args.Add("driver=vvfat,node-name=usbstick,dir=" + Esc(usb.Text) + ",rw=on,fat-type=16");
                args.Add("-device");
                args.Add("usb-storage,drive=usbstick,removable=on,port=1");  // port=1: no auto-added full-speed hub
            }
            var sb = new StringBuilder();
            foreach (var s in args) { if (sb.Length > 0) sb.Append(' '); sb.Append(Quote(s)); }

            try {
                File.WriteAllText(Path.Combine(root, "last-command.txt"),
                                  Quote(exe) + " " + sb + Environment.NewLine);
            } catch (Exception) { }

            var psi = new ProcessStartInfo {
                FileName = exe,
                Arguments = sb.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.Combine(root, "qemu"),
            };
            // libusb loads UsbDkHelper.dll from the PATH
            string usbdk = UsbDkDir();
            if (usbdk != null)
                psi.EnvironmentVariables["PATH"] = usbdk + ";" + Environment.GetEnvironmentVariable("PATH");
            var qemuErr = new StringBuilder();
            try {
                qemu = Process.Start(psi);
                KillWithLauncher(qemu);
                KeepFast(qemu);
                qemu.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (qemuErr) qemuErr.AppendLine(e.Data); };
                qemu.OutputDataReceived += (o, e) => { if (e.Data != null) lock (qemuErr) qemuErr.AppendLine(e.Data); };
                qemu.BeginErrorReadLine();
                qemu.BeginOutputReadLine();
            } catch (Exception e) {
                if (log != null) log.Dispose();
                Error(e.Message);
                return;
            }

            if (term != null && !term.IsDisposed) { term.AskClose = null; term.Close(); }
            term = new TerminalForm(b.Name, ledDefs);
            term.Log = log;
            term.AskClose = () => {
                if (qemu == null || qemu.HasExited) return true;
                var r = MessageBox.Show(term, L.T("ask.power_off", "Power off the router?"), Text,
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) Stop();
                return r == DialogResult.Yes;
            };
            term.Show();
            var proc = qemu;
            term.Connect(conPort, () => !proc.HasExited);
            if (ledPort != 0) term.StartLeds(ledPort, () => !proc.HasExited);

            SaveCfg();
            SetRunning(true);
            string shownLog = logPath;
            if (logPath != null && logPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                shownLog = logPath.Substring(root.Length).TrimStart('\\', '/');
            string runName = b.Name;
            SetStatus(() => L.F("status.running", "Running {0}.", runName)
                + (shownLog != null ? "\n" + L.F("status.log", "Log: {0}", shownLog) : ""));
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            var myTerm = term;
            t.Tick += delegate {
                if (proc.HasExited) {
                    t.Stop();
                    SetRunning(false);
                    SetStatus(() => L.T("status.stopped", "Stopped."));
                    string err;
                    lock (qemuErr) err = qemuErr.ToString().Trim();
                    if (!myTerm.IsDisposed)
                        myTerm.Message("\r\n\x1b[0m\x1b[33m[router powered off" +
                            (proc.ExitCode != 0 ? ", QEMU exit code " + proc.ExitCode : "") + "]\x1b[0m\r\n" +
                            // QEMU's stderr only matters when it failed
                            (proc.ExitCode != 0 && err.Length > 0
                                ? "\x1b[31m" + err.Replace("\n", "\r\n") + "\x1b[0m\r\n" : ""));
                }
            };
            t.Start();
        }

        // Windows 11 runs window-less processes in "efficiency mode" (EcoQoS):
        // on hybrid CPUs they end up on the slow E-cores.  QEMU has no window,
        // so opt it out of execution-speed and timer-resolution throttling and
        // raise its priority a little.  Failures (older Windows, Wine) are
        // harmless.
        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr process, int infoClass,
            ref PowerThrottlingState info, int size);

        static void KeepFast(Process p)
        {
            try {
                var st = new PowerThrottlingState {
                    Version = 1,
                    ControlMask = 0x1 | 0x4,    // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION
                    StateMask = 0,              // = throttling off for both
                };
                SetProcessInformation(p.Handle, 4 /* ProcessPowerThrottling */, ref st,
                                      Marshal.SizeOf(typeof(PowerThrottlingState)));
            } catch (Exception) { }
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch (Exception) { }
        }

        // can 127.0.0.1:port be bound (as QEMU's hostfwd will do)?
        static bool PortFree(int port)
        {
            try {
                var l = new TcpListener(System.Net.IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                l.Start();
                l.Stop();
                return true;
            } catch (SocketException) {
                return false;
            }
        }

        static int FreePort()
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        bool QmpAlive()
        {
            try {
                using (var c = new TcpClient()) {
                    var ar = c.BeginConnect("127.0.0.1", qmpPort, null, null);
                    return ar.AsyncWaitHandle.WaitOne(500) && c.Connected;
                }
            } catch (Exception) {
                return false;
            }
        }

        static IEnumerable<string> SplitArg(string a)
        {
            int sp = a.IndexOf(' ');
            return new[] { a.Substring(0, sp), a.Substring(sp + 1) };
        }

        void Stop()
        {
            var p = qemu;
            new Thread(() => PowerOff(p, 5000)) { IsBackground = true }.Start();
        }

        // QMP "quit", and if QEMU has not ended within ms (QMP not answering,
        // QEMU hanging) end the process
        void PowerOff(Process p, int ms)
        {
            if (p == null || p.HasExited) return;
            Qmp("{\"execute\":\"quit\"}");
            try {
                if (!p.WaitForExit(ms)) { p.Kill(); p.WaitForExit(2000); }
            } catch (Exception) { }
        }

        // QEMU runs in a job object that ends its processes when the last
        // handle to it closes, i.e. when the launcher exits in any way (also a
        // crash or Task Manager), so no qemu-system-aarch64.exe is left behind
        [StructLayout(LayoutKind.Sequential)]
        struct JobBasicLimits {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JobExtendedLimits {
            public JobBasicLimits Basic;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                         ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass,
            ref JobExtendedLimits info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr job;      // open until the launcher exits

        static void KillWithLauncher(Process p)
        {
            try {
                if (job == IntPtr.Zero) {
                    var j = CreateJobObject(IntPtr.Zero, null);
                    if (j == IntPtr.Zero) return;
                    var lim = new JobExtendedLimits();
                    lim.Basic.LimitFlags = 0x2000;      // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    if (!SetInformationJobObject(j, 9 /* ExtendedLimitInformation */, ref lim,
                                                 Marshal.SizeOf(typeof(JobExtendedLimits)))) return;
                    job = j;
                }
                AssignProcessToJobObject(job, p.Handle);
            } catch (Exception) { }
        }

        void SetRunning(bool on)
        {
            start.Text = on ? L.T("main.power_off", "Power off") : L.T("main.power_on", "Power on");
            btnWps.Enabled = btnPower.Enabled = btnTerm.Enabled = on;
            btnFactory.Enabled = on && !counting.Contains(btnFactory);
            btnTftp.Enabled = !on && !counting.Contains(btnTftp);
            board.Enabled = wan.Enabled = lan.Enabled = useUsb.Enabled = gpioLog.Enabled = !on;
            usbDev.Enabled = usbDevRefresh.Enabled = !on;
            btnEdit.Enabled = btnNew.Enabled = !on;
            useLogs.Enabled = !on;
            if (!on) OnBoardChanged();
            UpdateFolders();
        }

        // lock b for 10 s with the seconds left on it, then restore its text
        // and enable it again if enabledAfter() still allows
        void Countdown(Button b, string key, string en, Func<bool> enabledAfter)
        {
            int left = 10;
            Action show = () => b.Text = L.F("main.countdown", "{0} - {1} s", L.T(key, en), left);
            counting.Add(b);
            b.Enabled = false;
            show();
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += delegate {
                if (--left > 0) {
                    show();
                    return;
                }
                t.Stop();
                t.Dispose();
                counting.Remove(b);
                b.Text = L.T(key, en);
                b.Enabled = enabledAfter();
            };
            t.Start();
        }

        void PressButton(string prop, int ms)
        {
            new Thread(() => {
                Qmp("{\"execute\":\"qom-set\",\"arguments\":{\"path\":\"/machine/pinctrl\",\"property\":\"" + prop + "\",\"value\":true}}");
                Thread.Sleep(ms);
                Qmp("{\"execute\":\"qom-set\",\"arguments\":{\"path\":\"/machine/pinctrl\",\"property\":\"" + prop + "\",\"value\":false}}");
            }) { IsBackground = true }.Start();
        }

        void Qmp(string cmd)
        {
            try {
                using (var c = new TcpClient("127.0.0.1", qmpPort))
                using (var s = c.GetStream()) {
                    var r = new StreamReader(s);
                    var wr = new StreamWriter(s) { AutoFlush = true, NewLine = "\r\n" };
                    r.ReadLine();                                   // greeting
                    wr.WriteLine("{\"execute\":\"qmp_capabilities\"}");
                    r.ReadLine();
                    wr.WriteLine(cmd);
                    if (!cmd.Contains("\"quit\"")) r.ReadLine();
                }
            } catch (Exception) {
                // QEMU gone or still starting
            }
        }

        void ShowUsbDevs()
        {
            usbDev.Text = usbDevs.Count == 0 ? L.T("main.usb_dev_none", "None")
                : string.Join(", ", usbDevs.ConvertAll(d => d.Value).ToArray());
        }

        // UsbDk's runtime library (installed by its MSI), null if missing
        static string UsbDkDir()
        {
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }) {
                if (string.IsNullOrEmpty(pf)) continue;
                string d = Path.Combine(pf, "UsbDk Runtime Library");
                if (File.Exists(Path.Combine(d, "UsbDkHelper.dll"))) return d;
            }
            return null;
        }

        void Error(string msg)
        {
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        string Get(string k, string def = "")
        {
            string v;
            return cfg.TryGetValue(k, out v) ? v : def;
        }

        void LoadCfg()
        {
            try {
                // settings of versions up to 0.5
                string old = Path.Combine(root, "MT7981.ini");
                if (!File.Exists(CfgPath) && File.Exists(old)) File.Move(old, CfgPath);
            } catch (Exception) { }
            try {
                foreach (var line in File.ReadAllLines(CfgPath)) {
                    int eq = line.IndexOf('=');
                    if (eq > 0) cfg[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            } catch (Exception) { }
        }

        void SaveCfg()
        {
            try {
                File.WriteAllLines(CfgPath, new[] {
                    "preset=" + (board.SelectedItem != null ? Path.GetFileName(((Preset)board.SelectedItem).FilePath) : ""),
                    "nand=" + nand.Text,
                    "usb=" + usb.Text,
                    "useusb=" + (useUsb.Checked ? "1" : "0"),
                    "logs=" + logs.Text,
                    "uselogs=" + (useLogs.Checked ? "1" : "0"),
                    "gpiolog=" + (gpioLog.Checked ? "1" : "0"),
                    "usbdev=" + string.Join(";", usbDevs.ConvertAll(d => d.Key + "=" + d.Value.Replace(";", ",")).ToArray()),
                    "wan=" + Key((NetChoice)wan.SelectedItem),
                    "lan=" + Key((NetChoice)lan.SelectedItem),
                    "lang=" + (language.SelectedItem != null ? Path.GetFileName(((LangInfo)language.SelectedItem).FilePath) : ""),
                });
            } catch (Exception) { }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}

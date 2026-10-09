// Front panel LEDs: the preset's led1..ledN keys, the LED panel above the
// router console and the LED editor of the preset editor.
//
//   ledN=<name>: <colour> <source> [+ <colour> <source>...]
//   sources: gpio:N[:low]  phy:ADDR:N  ws2812:BUS:N  pwm:N
//
// QEMU evaluates the sources (machine property "led-state", see
// mt7981_leds.c); the panel polls it over a QMP connection of its own.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RouterEmulator
{
    class LedChannel
    {
        public string Color = "white", Source = "gpio:0";
    }

    class LedDef
    {
        public string Name = "";
        public List<LedChannel> Channels = new List<LedChannel>();

        public static readonly string[] Colors = {
            "white", "red", "green", "blue", "amber", "yellow", "orange",
            "purple", "violet", "pink", "cyan", "lime", "rgb",
        };

        public static bool IsKey(string k) { return Regex.IsMatch(k, @"^led\d+$"); }

        // null when the text is not "<name>: <colour> <source> [+ ...]"
        public static LedDef Parse(string s)
        {
            int c = s.IndexOf(": ");
            if (c <= 0) return null;
            var d = new LedDef { Name = s.Substring(0, c).Trim() };
            foreach (var part in s.Substring(c + 2).Split('+')) {
                var w = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length != 2 || !ValidSource(w[1])) return null;
                d.Channels.Add(new LedChannel { Color = w[0].ToLowerInvariant(), Source = w[1] });
            }
            return d.Channels.Count > 0 ? d : null;
        }

        public static bool ValidSource(string s)
        {
            return Regex.IsMatch(s, @"^(gpio:\d+(:low)?|phy:\d+:\d+|ws2812:\d+:\d+|pwm:\d+)$");
        }

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (var ch in Channels) parts.Add(ch.Color + " " + ch.Source);
            return Name + ": " + string.Join(" + ", parts.ToArray());
        }

        // the preset's LEDs in key order (led1, led2, ...)
        public static List<LedDef> FromPreset(Preset p)
        {
            var list = new List<KeyValuePair<int, LedDef>>();
            foreach (var kv in p.Values) {
                if (!IsKey(kv.Key)) continue;
                var d = Parse(kv.Value);
                if (d != null) list.Add(new KeyValuePair<int, LedDef>(int.Parse(kv.Key.Substring(3)), d));
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            var r = new List<LedDef>();
            foreach (var kv in list) r.Add(kv.Value);
            return r;
        }

        public static Color ColorOf(string name)
        {
            switch (name) {
            case "red": return System.Drawing.Color.FromArgb(255, 48, 48);
            case "green": return System.Drawing.Color.FromArgb(48, 224, 64);
            case "blue": return System.Drawing.Color.FromArgb(48, 128, 255);
            case "amber": return System.Drawing.Color.FromArgb(255, 176, 0);
            case "yellow": return System.Drawing.Color.FromArgb(255, 224, 48);
            case "orange": return System.Drawing.Color.FromArgb(255, 128, 32);
            case "purple": return System.Drawing.Color.FromArgb(176, 64, 255);
            case "violet": return System.Drawing.Color.FromArgb(144, 80, 255);
            case "pink": return System.Drawing.Color.FromArgb(255, 96, 192);
            case "cyan": return System.Drawing.Color.FromArgb(48, 224, 255);
            case "lime": return System.Drawing.Color.FromArgb(160, 255, 48);
            default: return System.Drawing.Color.FromArgb(240, 240, 240);
            }
        }
    }

    // The row of lamps above the console.  State: per LED, per colour
    // "0", "1", "b" (blinking), "#rrggbb", "b#rrggbb" (WS2812B).
    class LedPanel : Control
    {
        List<LedDef> leds = new List<LedDef>();
        string[][] state = new string[0][];
        readonly List<Rectangle> lamps = new List<Rectangle>();
        readonly System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer { Interval = 250 };
        readonly ToolTip tip = new ToolTip();
        bool phase;
        int tipLed = -1;
        const int Lamp = 12, RowH = 22;

        public LedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = System.Drawing.Color.FromArgb(32, 32, 32);
            ForeColor = System.Drawing.Color.FromArgb(190, 190, 190);
            Font = new Font("Segoe UI", 8.5f);
            blink.Tick += delegate {
                phase = !phase;
                if (Blinking()) Invalidate();
            };
            blink.Start();
            Disposed += delegate { blink.Dispose(); };
        }

        public bool HasLeds { get { return leds.Count > 0; } }

        public void SetLeds(List<LedDef> l)
        {
            leds = l;
            state = new string[l.Count][];
            Relayout();
        }

        // "1,0;b;#ff0000" from QEMU; null: router off
        public void SetState(string s)
        {
            var per = s == null ? new string[0] : s.Split(';');
            for (int i = 0; i < leds.Count; i++)
                state[i] = i < per.Length ? per[i].Split(',') : null;
            Invalidate();
        }

        bool Blinking()
        {
            foreach (var st in state)
                if (st != null) foreach (var v in st) if (v.StartsWith("b")) return true;
            return false;
        }

        // lamps flow left to right, wrapping into more rows when narrow
        void Relayout()
        {
            lamps.Clear();
            int x = 8, y = 0, w = Math.Max(ClientSize.Width, 100);
            using (var g = CreateGraphics()) {
                foreach (var d in leds) {
                    int tw = (int)Math.Ceiling(g.MeasureString(d.Name, Font).Width);
                    int lw = Lamp + 5 + tw + 14;
                    if (x > 8 && x + lw > w) { x = 8; y += RowH; }
                    lamps.Add(new Rectangle(x, y, lw, RowH));
                    x += lw;
                }
            }
            int h = leds.Count == 0 ? 0 : y + RowH + 4;
            if (Height != h) Height = h;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (leds.Count > 0) Relayout();
        }

        // the colour the lamp shows now, null = dark
        Color? Shown(int i)
        {
            var st = state[i];
            if (st == null) return null;
            int r = 0, g = 0, b = 0, n = 0;
            for (int k = 0; k < leds[i].Channels.Count && k < st.Length; k++) {
                string v = st[k];
                bool blinking = v.StartsWith("b");
                if (blinking) {
                    if (!phase) continue;
                    v = v.Length > 1 ? v.Substring(1) : "1";
                }
                Color c;
                if (v.StartsWith("#") && v.Length == 7) {
                    int rgb = int.Parse(v.Substring(1), NumberStyles.HexNumber);
                    if (rgb == 0) continue;
                    c = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                } else if (v == "1") {
                    c = LedDef.ColorOf(leds[i].Channels[k].Color);
                } else {
                    continue;
                }
                r += c.R; g += c.G; b += c.B; n++;
            }
            if (n == 0) return null;
            // two colours on at once mix (red + white = pink, ...)
            return System.Drawing.Color.FromArgb(Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var text = new SolidBrush(ForeColor)) {
                for (int i = 0; i < leds.Count && i < lamps.Count; i++) {
                    var rc = lamps[i];
                    var lamp = new Rectangle(rc.X, rc.Y + (RowH - Lamp) / 2 + 1, Lamp, Lamp);
                    Color? c = Shown(i);
                    if (c.HasValue) {
                        // a soft glow around a lit lamp
                        var glow = Rectangle.Inflate(lamp, 3, 3);
                        using (var gb = new SolidBrush(System.Drawing.Color.FromArgb(70, c.Value)))
                            g.FillEllipse(gb, glow);
                        using (var lb = new SolidBrush(c.Value)) g.FillEllipse(lb, lamp);
                    } else {
                        using (var lb = new SolidBrush(System.Drawing.Color.FromArgb(58, 58, 58)))
                            g.FillEllipse(lb, lamp);
                    }
                    using (var pen = new Pen(System.Drawing.Color.FromArgb(90, 90, 90)))
                        g.DrawEllipse(pen, lamp);
                    g.DrawString(leds[i].Name, Font, text, rc.X + Lamp + 5, rc.Y + 4);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = lamps.FindIndex(r => r.Contains(e.Location));
            if (hit == tipLed) return;
            tipLed = hit;
            tip.SetToolTip(this, hit >= 0 && hit < leds.Count ? leds[hit].ToString() : "");
        }
    }

    // Polls QEMU's "led-state" over a QMP socket while the router runs.
    class LedPoller
    {
        volatile bool stop;

        public void Stop() { stop = true; }

        public void Start(int port, Func<bool> alive, Action<string> update)
        {
            new Thread(() => {
                while (!stop && alive()) {
                    try {
                        using (var c = new TcpClient()) {
                            c.Connect("127.0.0.1", port);
                            c.NoDelay = true;
                            var s = c.GetStream();
                            var r = new StreamReader(s, Encoding.UTF8);
                            var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\r\n" };
                            r.ReadLine();                               // greeting
                            w.WriteLine("{\"execute\":\"qmp_capabilities\"}");
                            Reply(r);
                            while (!stop && alive()) {
                                w.WriteLine("{\"execute\":\"qom-get\",\"arguments\":"
                                            + "{\"path\":\"/machine\",\"property\":\"led-state\"}}");
                                var m = Regex.Match(Reply(r), "\"return\"\\s*:\\s*\"([^\"]*)\"");
                                if (m.Success) update(m.Groups[1].Value);
                                Thread.Sleep(100);
                            }
                        }
                    } catch (Exception) {
                        Thread.Sleep(300);      // QEMU starting or gone
                    }
                }
                update(null);
            }) { IsBackground = true }.Start();
        }

        // the next reply line, skipping asynchronous events
        static string Reply(StreamReader r)
        {
            string l;
            while ((l = r.ReadLine()) != null)
                if (!l.Contains("\"event\"")) return l;
            throw new IOException("QMP closed");
        }
    }

    // One LED in the preset editor: name and up to three colours.
    class LedEditForm : Form
    {
        public LedDef Result;
        readonly TextBox name;
        readonly ComboBox[] color = new ComboBox[3], kind = new ComboBox[3];
        readonly NumericUpDown[] numA = new NumericUpDown[3], numB = new NumericUpDown[3];
        readonly Label[] labA = new Label[3], labB = new Label[3];
        readonly CheckBox[] low = new CheckBox[3];

        static readonly string[] Kinds = { "", "gpio", "phy", "ws2812", "pwm" };

        public LedEditForm(LedDef d)
        {
            Text = L.T("led.title", "LED");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            Controls.Add(new Label { Left = 12, Top = 15, Width = 90, Text = L.T("led.name", "Name:") });
            name = new TextBox { Left = 105, Top = 12, Width = 250, Text = d != null ? d.Name : "" };
            Controls.Add(name);
            var kindNames = new[] {
                L.T("led.unused", "(not used)"), "GPIO", L.T("led.phy", "Ethernet PHY LED"),
                "WS2812B (SPI)", "PWM",
            };
            int y = 48;
            for (int i = 0; i < 3; i++) {
                Controls.Add(new Label { Left = 12, Top = y + 3, Width = 90,
                    Text = L.F("led.colour_n", "Colour {0}:", i + 1) });
                color[i] = new ComboBox { Left = 105, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
                color[i].Items.AddRange(LedDef.Colors);
                kind[i] = new ComboBox { Left = 192, Top = y, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
                kind[i].Items.AddRange(kindNames);
                labA[i] = new Label { Left = 350, Top = y + 3, Width = 60, TextAlign = ContentAlignment.TopRight };
                numA[i] = new NumericUpDown { Left = 412, Top = y, Width = 50, Minimum = 0, Maximum = 127 };
                labB[i] = new Label { Left = 465, Top = y + 3, Width = 40, TextAlign = ContentAlignment.TopRight };
                numB[i] = new NumericUpDown { Left = 507, Top = y, Width = 45, Minimum = 0, Maximum = 15 };
                low[i] = new CheckBox { Left = 465, Top = y + 1, Width = 110, Text = L.T("led.active_low", "active low") };
                int k = i;
                kind[i].SelectedIndexChanged += delegate { UpdateRow(k); };
                Controls.AddRange(new Control[] { color[i], kind[i], labA[i], numA[i], labB[i], numB[i], low[i] });
                var ch = d != null && i < d.Channels.Count ? d.Channels[i] : null;
                color[i].SelectedItem = ch != null && Array.IndexOf(LedDef.Colors, ch.Color) >= 0 ? ch.Color
                    : (i == 0 ? "white" : "red");
                if (ch == null && i == 0) ch = new LedChannel();
                if (ch != null) {
                    var f = ch.Source.Split(':');
                    kind[i].SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, f[0]));
                    numA[i].Value = Math.Min(numA[i].Maximum, decimal.Parse(f[1]));
                    if (f.Length > 2 && f[2] != "low") numB[i].Value = Math.Min(numB[i].Maximum, decimal.Parse(f[2]));
                    low[i].Checked = f.Length > 2 && f[2] == "low";
                } else {
                    kind[i].SelectedIndex = 0;
                }
                UpdateRow(i);
                y += 32;
            }
            Controls.Add(new Label { Left = 12, Top = y, Width = 560, Height = 64, ForeColor = Color.DimGray,
                Text = L.T("led.hint", "A lamp with several colours (e.g. a red / white status LED) has one row "
                    + "per colour, each driven by its own GPIO. \"active low\": lit while the GPIO is 0 "
                    + "(GPIO_ACTIVE_LOW in the device tree). Ethernet PHY LED: MDIO address of the PHY and "
                    + "its LED pin. WS2812B: SPI bus and position in the chain (colour rgb).") });
            y += 70;
            var ok = new Button { Left = 352, Top = y, Width = 100, Height = 28, Text = "OK" };
            var cancel = new Button { Left = 458, Top = y, Width = 100, Height = 28,
                Text = L.T("ed.cancel", "Cancel"), DialogResult = DialogResult.Cancel };
            ok.Click += delegate { Accept(); };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(575, y + 40);
        }

        void UpdateRow(int i)
        {
            string k = Kinds[Math.Max(0, kind[i].SelectedIndex)];
            color[i].Enabled = k != "";
            labA[i].Visible = numA[i].Visible = k != "";
            labB[i].Visible = numB[i].Visible = k == "phy" || k == "ws2812";
            low[i].Visible = k == "gpio";
            labA[i].Text = k == "gpio" ? "GPIO:" : k == "phy" ? "MDIO:" : k == "ws2812" ? "SPI:" : L.T("led.channel", "Channel:");
            labB[i].Text = "LED:";
            if (k == "ws2812") color[i].SelectedItem = "rgb";
        }

        void Accept()
        {
            var d = new LedDef { Name = name.Text.Trim().Replace(":", "") };
            for (int i = 0; i < 3; i++) {
                string k = Kinds[Math.Max(0, kind[i].SelectedIndex)];
                if (k == "") continue;
                string src = k + ":" + numA[i].Value;
                if (k == "phy" || k == "ws2812") src += ":" + numB[i].Value;
                if (k == "gpio" && low[i].Checked) src += ":low";
                d.Channels.Add(new LedChannel { Color = (string)color[i].SelectedItem ?? "white", Source = src });
            }
            if (d.Name.Length == 0 || d.Channels.Count == 0) {
                MessageBox.Show(this, L.T("led.incomplete", "Enter a name and at least one colour."), Text);
                return;
            }
            Result = d;
            DialogResult = DialogResult.OK;
        }
    }
}

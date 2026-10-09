// Minimal VT100/ANSI terminal for the router serial console.
//
// Connects to QEMU's serial chardev over TCP.  Supports what U-Boot and
// the Linux/OpenWrt console use: cursor movement, erase, colours, reverse
// video, scroll regions, UTF-8.  Mouse selection copies to the clipboard
// (like PuTTY), right click / Ctrl+Shift+V pastes, wheel scrolls back.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RouterEmulator
{
    struct Cell
    {
        public char Ch;
        public byte Fg, Bg;     // palette index, 255 = default
        public bool Bold, Inverse;
    }

    class TermControl : Control
    {
        public const int MaxScrollback = 5000;
        // PuTTY's defaults: 80 x 24 (also the router's tty size until "resize")
        public int Cols = 80, Rows = 24;
        public event Action SizeChanged2;

        static readonly Color[] Palette = {
            Color.FromArgb(0, 0, 0), Color.FromArgb(205, 49, 49),
            Color.FromArgb(13, 188, 121), Color.FromArgb(229, 229, 16),
            Color.FromArgb(36, 114, 200), Color.FromArgb(188, 63, 188),
            Color.FromArgb(17, 168, 205), Color.FromArgb(229, 229, 229),
            Color.FromArgb(102, 102, 102), Color.FromArgb(241, 76, 76),
            Color.FromArgb(35, 209, 139), Color.FromArgb(245, 245, 67),
            Color.FromArgb(59, 142, 234), Color.FromArgb(214, 112, 214),
            Color.FromArgb(41, 184, 219), Color.FromArgb(255, 255, 255),
        };
        static readonly Color DefFg = Color.FromArgb(204, 204, 204);
        static readonly Color DefBg = Color.FromArgb(12, 12, 12);

        // screen = last Rows lines of 'lines'
        readonly List<Cell[]> lines = new List<Cell[]>();
        int curX, curY, savedX, savedY;
        int top, bottom = 23;           // scroll region (Rows - 1)
        Cell attr = Blank();
        bool cursorVisible = true;
        int viewOffset;                  // lines scrolled back

        // parser
        enum St { Normal, Esc, Csi, Osc, Charset }
        St state;
        readonly StringBuilder csi = new StringBuilder();
        readonly Decoder utf8 = new UTF8Encoding(false).GetDecoder();

        // selection (absolute line index, column)
        Point selA, selB;
        bool selecting, hasSel;

        Font font;
        int cw, ch;

        public Action<byte[]> Send;

        static Cell Blank()
        {
            return new Cell { Ch = ' ', Fg = 255, Bg = 255 };
        }

        public TermControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            // PuTTY's default font
            font = new Font("Courier New", 10f);
            if (font.Name != "Courier New") font = new Font(FontFamily.GenericMonospace, 10f);
            using (var g = CreateGraphics()) {
                var sz = TextRenderer.MeasureText(g, "MMMMMMMMMM", font, Size.Empty,
                                                  TextFormatFlags.NoPadding);
                cw = Math.Max(1, sz.Width / 10);
                ch = Math.Max(1, sz.Height);
            }
            ClientSize = new Size(Cols * cw, Rows * ch);
            for (int i = 0; i < Rows; i++) lines.Add(NewLine());
            Cursor = Cursors.IBeam;
            BackColor = DefBg;
        }

        public Size TermSize { get { return new Size(Cols * cw, Rows * ch); } }
        public Size CellSize { get { return new Size(cw, ch); } }

        // Change the grid to fit the control: lines are padded/truncated,
        // rows are added at the bottom or scrolled off the top.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (cw == 0 || ch == 0) return;
            int nc = Math.Max(20, ClientSize.Width / cw);
            int nr = Math.Max(5, ClientSize.Height / ch);
            if (nc == Cols && nr == Rows) return;
            for (int i = 0; i < lines.Count; i++) {
                var l = lines[i];
                if (l.Length == nc) continue;
                var n = new Cell[nc];
                for (int x = 0; x < nc; x++) n[x] = x < l.Length ? l[x] : Blank();
                lines[i] = n;
            }
            int oldCols = Cols;
            Cols = nc;
            if (nr > Rows) {
                // grow: pull lines back from scrollback, else add blanks
                int extra = nr - Rows;
                int fromBack = Math.Min(extra, Math.Max(0, lines.Count - Rows));
                curY += fromBack;
                for (int i = fromBack; i < extra; i++) lines.Add(NewLine());
            } else if (nr < Rows) {
                // shrink: drop blank lines below the cursor first
                int drop = Rows - nr;
                while (drop > 0 && curY < Rows - 1) {
                    lines.RemoveAt(lines.Count - 1);
                    Rows--; drop--;
                }
                curY = Math.Max(0, curY - drop);
            }
            Rows = nr;
            while (lines.Count < Rows) lines.Insert(0, NewLine());
            top = 0; bottom = Rows - 1;
            curX = Math.Min(curX, Cols - 1);
            curY = Math.Min(curY, Rows - 1);
            viewOffset = 0;
            Invalidate();
            if (SizeChanged2 != null && oldCols != 0) SizeChanged2();
        }

        Cell[] NewLine()
        {
            var l = new Cell[Cols];
            var b = attr;
            b.Ch = ' ';
            b.Inverse = false;
            b.Bold = false;
            b.Fg = 255;
            for (int i = 0; i < Cols; i++) l[i] = b;
            return l;
        }

        Cell[] Line(int y) { return lines[lines.Count - Rows + y]; }

        // ---------------------------------------------------------------- data

        public void Feed(byte[] data, int len)
        {
            var chars = new char[len + 4];
            int n = utf8.GetChars(data, 0, len, chars, 0);
            for (int i = 0; i < n; i++) Put(chars[i]);
            if (viewOffset == 0) Invalidate();
        }

        public void Write(string s)
        {
            foreach (char c in s) Put(c);
            Invalidate();
        }

        void Put(char c)
        {
            switch (state) {
            case St.Normal:
                if (c == '\x1b') { state = St.Esc; return; }
                Control(c);
                return;
            case St.Esc:
                state = St.Normal;
                switch (c) {
                case '[': csi.Clear(); state = St.Csi; return;
                case ']': state = St.Osc; return;
                case '(': case ')': state = St.Charset; return;
                case '7': savedX = curX; savedY = curY; return;
                case '8': curX = savedX; curY = savedY; return;
                case 'D': Index(); return;
                case 'E': curX = 0; Index(); return;
                case 'M': ReverseIndex(); return;
                case 'c': Reset(); return;
                }
                return;
            case St.Charset:
                state = St.Normal;
                return;
            case St.Osc:
                if (c == '\x07') state = St.Normal;
                else if (c == '\x1b') state = St.Esc;     // ESC \ terminator
                return;
            case St.Csi:
                if (c >= 0x40 && c <= 0x7e) {
                    state = St.Normal;
                    try {
                        DoCsi(c, csi.ToString());
                    } catch (Exception) {
                        // never let one odd sequence break the terminal
                    }
                } else {
                    csi.Append(c);
                }
                return;
            }
        }

        void Control(char c)
        {
            switch (c) {
            case '\r': curX = 0; return;
            case '\n': case '\x0b': case '\x0c': Index(); return;
            case '\b': if (curX > 0) curX--; return;
            case '\t': curX = Math.Min(Cols - 1, (curX / 8 + 1) * 8); return;
            case '\x07': case '\x00': case '\x0e': case '\x0f': return;
            }
            if (c < ' ') return;
            if (curX >= Cols) { curX = 0; Index(); }
            var a = attr;
            a.Ch = c;
            Line(curY)[curX++] = a;
        }

        void Index()
        {
            if (curY == bottom) ScrollUp(1);
            else if (curY < Rows - 1) curY++;
        }

        void ReverseIndex()
        {
            if (curY == top) ScrollDown(1);
            else if (curY > 0) curY--;
        }

        void ScrollUp(int n)
        {
            for (int k = 0; k < n; k++) {
                if (top == 0 && bottom == Rows - 1) {
                    lines.Add(NewLine());
                    if (lines.Count > MaxScrollback + Rows) {
                        lines.RemoveAt(0);
                        selA.Y--; selB.Y--;
                    } else if (viewOffset > 0) {
                        viewOffset++;
                    }
                } else {
                    int b = lines.Count - Rows;
                    lines.RemoveAt(b + top);
                    lines.Insert(b + bottom, NewLine());
                }
            }
        }

        void ScrollDown(int n)
        {
            int b = lines.Count - Rows;
            for (int k = 0; k < n; k++) {
                lines.RemoveAt(b + bottom);
                lines.Insert(b + top, NewLine());
            }
        }

        void Reset()
        {
            attr = Blank();
            top = 0; bottom = Rows - 1;
            for (int y = 0; y < Rows; y++) Line(y).CopyTo(NewLine(), 0);
            for (int y = 0; y < Rows; y++) lines[lines.Count - Rows + y] = NewLine();
            curX = curY = 0;
        }

        void Erase(int y, int x0, int x1)
        {
            var l = Line(y);
            var b = attr;
            b.Ch = ' '; b.Inverse = false;
            for (int x = Math.Max(0, x0); x < Math.Min(Cols, x1); x++) l[x] = b;
        }

        static int P(string[] p, int i, int def)
        {
            int v;
            if (i < p.Length && int.TryParse(p[i], out v) && v > 0) return v;
            return def;
        }

        void DoCsi(char f, string s)
        {
            bool priv = s.StartsWith("?");
            if (priv) s = s.Substring(1);
            var p = s.Split(';');
            int n = P(p, 0, 1);
            switch (f) {
            case 'A': curY = Math.Max(curY - n, 0); break;
            case 'B': case 'e': curY = Math.Min(curY + n, Rows - 1); break;
            case 'C': case 'a': curX = Math.Min(curX + n, Cols - 1); break;
            case 'D': curX = Math.Max(curX - n, 0); break;
            case 'E': curX = 0; curY = Math.Min(curY + n, Rows - 1); break;
            case 'F': curX = 0; curY = Math.Max(curY - n, 0); break;
            case 'G': case '`': curX = Math.Min(n - 1, Cols - 1); break;
            case 'd': curY = Math.Min(n - 1, Rows - 1); break;
            case 'H': case 'f':
                curY = Math.Min(P(p, 0, 1) - 1, Rows - 1);
                curX = Math.Min(P(p, 1, 1) - 1, Cols - 1);
                break;
            case 'J': {
                int m = P(p, 0, 0) == 0 && (p[0] == "" || p[0] == "0") ? 0 : P(p, 0, 0);
                if (m == 0) { Erase(curY, curX, Cols); for (int y = curY + 1; y < Rows; y++) Erase(y, 0, Cols); }
                else if (m == 1) { Erase(curY, 0, curX + 1); for (int y = 0; y < curY; y++) Erase(y, 0, Cols); }
                else for (int y = 0; y < Rows; y++) Erase(y, 0, Cols);
                break;
            }
            case 'K': {
                int m = p[0] == "" ? 0 : P(p, 0, 0);
                if (m == 0) Erase(curY, curX, Cols);
                else if (m == 1) Erase(curY, 0, curX + 1);
                else Erase(curY, 0, Cols);
                break;
            }
            case 'X': Erase(curY, curX, curX + n); break;
            case 'P': {
                var l = Line(curY);
                for (int x = curX; x < Cols; x++) l[x] = x + n < Cols ? l[x + n] : Blank();
                break;
            }
            case '@': {
                var l = Line(curY);
                for (int x = Cols - 1; x >= curX; x--) l[x] = x - n >= curX ? l[x - n] : Blank();
                break;
            }
            case 'L': if (curY >= top && curY <= bottom) { int t = top; top = curY; ScrollDown(n); top = t; } break;
            case 'M': if (curY >= top && curY <= bottom) { int t = top; top = curY; ScrollUp(n); top = t; } break;
            case 'S': ScrollUp(n); break;
            case 'T': ScrollDown(n); break;
            case 'r':
                top = Math.Min(P(p, 0, 1) - 1, Rows - 1);
                bottom = Math.Min(P(p, 1, Rows) - 1, Rows - 1);
                if (bottom <= top) { top = 0; bottom = Rows - 1; }
                curX = 0; curY = 0;
                break;
            case 's': savedX = curX; savedY = curY; break;
            case 'u': curX = savedX; curY = savedY; break;
            case 'h': case 'l':
                if (priv && s == "25") cursorVisible = f == 'h';
                break;
            case 't':
                if (s == "18")
                    Send(Encoding.ASCII.GetBytes("\x1b[8;" + Rows + ";" + Cols + "t"));
                break;
            case 'n':
                if (s == "6") Send(Encoding.ASCII.GetBytes("\x1b[" + (curY + 1) + ";" + (curX + 1) + "R"));
                break;
            case 'm': Sgr(p); break;
            }
        }

        void Sgr(string[] p)
        {
            for (int i = 0; i < p.Length; i++) {
                int v;
                if (!int.TryParse(p[i], out v)) v = 0;
                if (v == 0) attr = Blank();
                else if (v == 1) attr.Bold = true;
                else if (v == 22) attr.Bold = false;
                else if (v == 7) attr.Inverse = true;
                else if (v == 27) attr.Inverse = false;
                else if (v >= 30 && v <= 37) attr.Fg = (byte)(v - 30);
                else if (v == 39) attr.Fg = 255;
                else if (v >= 40 && v <= 47) attr.Bg = (byte)(v - 40);
                else if (v == 49) attr.Bg = 255;
                else if (v >= 90 && v <= 97) attr.Fg = (byte)(v - 90 + 8);
                else if (v >= 100 && v <= 107) attr.Bg = (byte)(v - 100 + 8);
                else if ((v == 38 || v == 48) && i + 1 < p.Length) {
                    // 256 colour / rgb: map 0..15, ignore the rest
                    if (p[i + 1] == "5" && i + 2 < p.Length) {
                        int c; int.TryParse(p[i + 2], out c);
                        if (c < 16) { if (v == 38) attr.Fg = (byte)c; else attr.Bg = (byte)c; }
                        i += 2;
                    } else if (p[i + 1] == "2") {
                        i += 4;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- paint

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DefBg);
            int first = lines.Count - Rows - viewOffset;
            Point s0, s1;
            OrderedSel(out s0, out s1);
            for (int y = 0; y < Rows; y++) {
                int li = first + y;
                if (li < 0 || li >= lines.Count) continue;
                var l = lines[li];
                for (int x = 0; x < Cols; x++) {
                    var c = l[x];
                    Color fg = c.Fg == 255 ? DefFg : Palette[c.Bold && c.Fg < 8 ? c.Fg + 8 : c.Fg];
                    Color bg = c.Bg == 255 ? DefBg : Palette[c.Bg];
                    bool inv = c.Inverse;
                    if (hasSel && InSel(li, x, s0, s1)) inv = !inv;
                    if (viewOffset == 0 && cursorVisible && y == curY && x == curX && Focused) inv = !inv;
                    if (inv) { var t = fg; fg = bg; bg = t; }
                    if (bg != DefBg) {
                        using (var b = new SolidBrush(bg)) g.FillRectangle(b, x * cw, y * ch, cw, ch);
                    }
                    if (c.Ch != ' ')
                        TextRenderer.DrawText(g, c.Ch.ToString(), font, new Point(x * cw, y * ch), fg,
                                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }
        }

        // ---------------------------------------------------------------- input

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode) {
            case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
            case Keys.Tab: case Keys.Home: case Keys.End: case Keys.PageUp:
            case Keys.PageDown: case Keys.Delete: case Keys.Insert: case Keys.Escape:
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            string seq = null;
            if (e.Control && e.Shift && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; return; }
            if (e.Control && e.Shift && e.KeyCode == Keys.V) { Paste(); e.SuppressKeyPress = true; return; }
            if (e.Shift && e.KeyCode == Keys.Insert) { Paste(); e.SuppressKeyPress = true; return; }
            if (e.Shift && e.KeyCode == Keys.PageUp) { ScrollView(Rows / 2); e.SuppressKeyPress = true; return; }
            if (e.Shift && e.KeyCode == Keys.PageDown) { ScrollView(-Rows / 2); e.SuppressKeyPress = true; return; }
            switch (e.KeyCode) {
            case Keys.Up: seq = "\x1b[A"; break;
            case Keys.Down: seq = "\x1b[B"; break;
            case Keys.Right: seq = "\x1b[C"; break;
            case Keys.Left: seq = "\x1b[D"; break;
            case Keys.Home: seq = "\x1b[H"; break;
            case Keys.End: seq = "\x1b[F"; break;
            case Keys.Insert: seq = "\x1b[2~"; break;
            case Keys.Delete: seq = "\x1b[3~"; break;
            case Keys.PageUp: seq = "\x1b[5~"; break;
            case Keys.PageDown: seq = "\x1b[6~"; break;
            case Keys.F1: seq = "\x1bOP"; break;
            case Keys.F2: seq = "\x1bOQ"; break;
            case Keys.F3: seq = "\x1bOR"; break;
            case Keys.F4: seq = "\x1bOS"; break;
            }
            if (seq != null) {
                SendText(seq);
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            char c = e.KeyChar;
            if (c == '\b') c = '\x7f';          // Backspace like PuTTY
            SendText(c.ToString());
            e.Handled = true;
        }

        void SendText(string s)
        {
            if (viewOffset != 0) { viewOffset = 0; Invalidate(); }
            if (Send != null) Send(Encoding.UTF8.GetBytes(s));
        }

        void Paste()
        {
            if (Clipboard.ContainsText())
                SendText(Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r'));
        }

        void ScrollView(int d)
        {
            viewOffset = Math.Max(0, Math.Min(lines.Count - Rows, viewOffset + d));
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollView(e.Delta / 40);
        }

        Point CellAt(Point p)
        {
            int x = Math.Max(0, Math.Min(Cols - 1, p.X / cw));
            int y = Math.Max(0, Math.Min(Rows - 1, p.Y / ch));
            return new Point(x, lines.Count - Rows - viewOffset + y);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Right) { Paste(); return; }
            if (e.Button != MouseButtons.Left) return;
            selA = selB = CellAt(e.Location);
            selecting = true;
            hasSel = false;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!selecting) return;
            selB = CellAt(e.Location);
            hasSel = selA != selB;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!selecting) return;
            selecting = false;
            selB = CellAt(e.Location);
            hasSel = selA != selB;
            if (hasSel) CopySelection();
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        void OrderedSel(out Point a, out Point b)
        {
            if (selA.Y < selB.Y || (selA.Y == selB.Y && selA.X <= selB.X)) { a = selA; b = selB; }
            else { a = selB; b = selA; }
        }

        static bool InSel(int li, int x, Point a, Point b)
        {
            if (li < a.Y || li > b.Y) return false;
            if (li == a.Y && x < a.X) return false;
            if (li == b.Y && x > b.X) return false;
            return true;
        }

        void CopySelection()
        {
            if (!hasSel) return;
            Point a, b;
            OrderedSel(out a, out b);
            var sb = new StringBuilder();
            for (int li = Math.Max(0, a.Y); li <= b.Y && li < lines.Count; li++) {
                int x0 = li == a.Y ? a.X : 0, x1 = li == b.Y ? b.X : Cols - 1;
                var row = new StringBuilder();
                for (int x = x0; x <= x1; x++) row.Append(lines[li][x].Ch);
                sb.Append(row.ToString().TrimEnd());
                if (li != b.Y) sb.Append("\r\n");
            }
            try { Clipboard.SetText(sb.ToString()); } catch (Exception) { }
        }
    }

    // Writes the console output as plain text: escape sequences removed,
    // line endings normalised to CRLF (firmware prints "\r\r\n").
    class ConsoleLog : IDisposable
    {
        readonly System.IO.StreamWriter w;
        readonly Decoder utf8 = new UTF8Encoding(false).GetDecoder();
        int esc;            // 0 none, 1 after ESC, 2 CSI, 3 OSC, 4 charset

        public ConsoleLog(string path)
        {
            w = new System.IO.StreamWriter(path, false, new UTF8Encoding(false));
            w.AutoFlush = true;
        }

        public void Write(byte[] data, int len)
        {
            var chars = new char[len + 4];
            int n = utf8.GetChars(data, 0, len, chars, 0);
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) {
                char c = chars[i];
                switch (esc) {
                case 1:
                    esc = c == '[' ? 2 : c == ']' ? 3 : (c == '(' || c == ')') ? 4 : 0;
                    continue;
                case 2:
                    if (c >= 0x40 && c <= 0x7e) esc = 0;
                    continue;
                case 3:
                    if (c == '\x07') esc = 0; else if (c == '\x1b') esc = 1;
                    continue;
                case 4:
                    esc = 0;
                    continue;
                }
                if (c == '\x1b') { esc = 1; continue; }
                if (c == '\r') continue;
                if (c == '\n') { sb.Append("\r\n"); continue; }
                if (c < ' ' && c != '\t' && c != '\b') continue;
                sb.Append(c);
            }
            try { w.Write(sb.ToString()); } catch (Exception) { }
        }

        public void Dispose() { try { w.Dispose(); } catch (Exception) { } }
    }

    class TerminalForm : Form
    {
        readonly TermControl term = new TermControl();
        TcpClient client;
        NetworkStream stream;
        volatile bool closing;
        public ConsoleLog Log;
        public Func<bool> AskClose;     // return false to keep the window

        readonly StatusStrip bar = new StatusStrip { SizingGrip = true };
        readonly ToolStripStatusLabel sizeLabel = new ToolStripStatusLabel();
        readonly LedPanel leds = new LedPanel();
        readonly LedPoller ledPoller = new LedPoller();

        public TerminalForm(string title, List<LedDef> ledDefs = null)
        {
            Text = title + " - serial console (select = copy, right click = paste)";
            FormBorderStyle = FormBorderStyle.Sizable;
            var fit = new ToolStripStatusLabel("Fit router console (Ctrl+Shift+R)") {
                IsLink = true, Spring = false
            };
            fit.Click += delegate { FitRouter(); term.Focus(); };
            bar.Items.Add(sizeLabel);
            bar.Items.Add(new ToolStripStatusLabel { Spring = true });
            bar.Items.Add(fit);
            // size the window for the terminal grid first (PuTTY: 80 x 24
            // cells), then dock: docking into a smaller form would shrink
            // the grid before the window gets its size
            Size cells = term.TermSize;
            Controls.Add(term);
            Controls.Add(bar);
            // front panel LEDs of the preset above the console
            leds.Width = cells.Width;
            leds.SetLeds(ledDefs ?? new List<LedDef>());
            if (leds.HasLeds) {
                Controls.Add(leds);
                leds.Dock = DockStyle.Top;
            }
            term.Dock = DockStyle.Fill;
            // the status bar's real height is known only once it is on the form
            ClientSize = new Size(cells.Width, cells.Height + bar.GetPreferredSize(Size.Empty).Height
                                  + (leds.HasLeds ? leds.Height : 0));
            MinimumSize = new Size(300, 200);
            UpdateSizeLabel();
            term.SizeChanged2 += UpdateSizeLabel;
            KeyPreview = true;
            KeyDown += (o, e) => {
                if (e.Control && e.Shift && e.KeyCode == Keys.R) {
                    FitRouter();
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            term.Send = data => {
                try { if (stream != null) stream.Write(data, 0, data.Length); } catch (Exception) { }
            };
            FormClosing += (o, e) => {
                if (AskClose != null && !AskClose()) { e.Cancel = true; return; }
                closing = true;
                ledPoller.Stop();
                try { if (client != null) client.Close(); } catch (Exception) { }
                if (Log != null) Log.Dispose();
            };
            Shown += delegate { term.Focus(); };
        }

        // Like PuTTY: while the frame is dragged, snap the window to whole
        // character cells so no partial row/column is left over.
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const int WM_SIZING = 0x0214;
        const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
                  WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SIZING && term.IsHandleCreated) {
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Size cell = term.CellSize;
                // frame + status bar around the terminal grid
                int fw = Width - term.ClientSize.Width, fh = Height - term.ClientSize.Height;
                int w = Math.Max(20, (r.Right - r.Left - fw + cell.Width / 2) / cell.Width) * cell.Width + fw;
                int h = Math.Max(5, (r.Bottom - r.Top - fh + cell.Height / 2) / cell.Height) * cell.Height + fh;
                int edge = m.WParam.ToInt32();
                if (edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT)
                    r.Left = r.Right - w;
                else
                    r.Right = r.Left + w;
                if (edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT)
                    r.Top = r.Bottom - h;
                else
                    r.Bottom = r.Top + h;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        void UpdateSizeLabel()
        {
            sizeLabel.Text = term.Cols + " x " + term.Rows;
            sizeLabel.ToolTipText = "The router assumes 80 x 24 until \"resize\" is run";
        }

        // BusyBox "resize" asks the terminal for its size (ESC[6n) and sets
        // the tty size, so mc/top/vi use the whole window.
        void FitRouter()
        {
            if (term.Send != null) term.Send(Encoding.ASCII.GetBytes("resize\r"));
        }

        public void Message(string s)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() => term.Write(s)));
        }

        // LED states from QEMU's QMP socket "port" while alive()
        public void StartLeds(int port, Func<bool> alive)
        {
            if (!leds.HasLeds) return;
            ledPoller.Start(port, () => alive() && !closing, st => {
                if (IsDisposed || closing) return;
                try { BeginInvoke(new Action(() => leds.SetState(st))); } catch (Exception) { }
            });
        }

        // Connect to QEMU's serial socket (retrying while QEMU starts) and pump data.
        public void Connect(int port, Func<bool> alive)
        {
            new Thread(() => {
                for (int i = 0; i < 300 && !closing && alive(); i++) {
                    try {
                        client = new TcpClient();
                        client.NoDelay = true;
                        client.Connect("127.0.0.1", port);
                        stream = client.GetStream();
                        break;
                    } catch (Exception) {
                        client = null;
                        Thread.Sleep(100);
                    }
                }
                if (stream == null) return;
                var buf = new byte[8192];
                try {
                    int n;
                    while (!closing && (n = stream.Read(buf, 0, buf.Length)) > 0) {
                        var copy = new byte[n];
                        Array.Copy(buf, copy, n);
                        if (Log != null) Log.Write(copy, n);
                        BeginInvoke(new Action(() => term.Feed(copy, copy.Length)));
                    }
                } catch (Exception) { }
            }) { IsBackground = true }.Start();
        }
    }
}

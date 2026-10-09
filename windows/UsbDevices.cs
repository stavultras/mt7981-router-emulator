// USB devices of this PC passed through to the router (machine option
// usb-host=VID:PID;...): the list of present devices from WMI and the
// dialog that ticks them.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RouterEmulator
{
    class UsbDevForm : Form
    {
        public List<KeyValuePair<string, string>> Result;
        readonly CheckedListBox list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        // items: "vid:pid" -> shown name
        readonly List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();

        public UsbDevForm(List<KeyValuePair<string, string>> chosen)
        {
            Text = L.T("usb.title", "USB devices for the router");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(560, 380);
            Controls.Add(new Label { Left = 12, Top = 10, Width = 536, Height = 48, ForeColor = Color.DimGray,
                Text = L.T("usb.hint", "Ticked devices are passed through to the router while it runs (USB 2.0 "
                    + "ports 2, 3, ... of the router). OpenWrt needs their drivers. Windows needs UsbDk "
                    + "(github.com/daynix/UsbDk) or the WinUSB driver on the device.") });
            list.SetBounds(12, 62, 536, 268);
            Controls.Add(list);
            var refresh = new Button { Left = 12, Top = 340, Width = 110, Height = 28, Text = L.T("main.refresh", "Refresh") };
            var ok = new Button { Left = 340, Top = 340, Width = 100, Height = 28, Text = "OK" };
            var cancel = new Button { Left = 448, Top = 340, Width = 100, Height = 28,
                Text = L.T("ed.cancel", "Cancel"), DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { refresh, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            refresh.Click += delegate { Fill(Checked()); };
            ok.Click += delegate { Result = Checked(); DialogResult = DialogResult.OK; };
            Fill(chosen);
        }

        List<KeyValuePair<string, string>> Checked()
        {
            var r = new List<KeyValuePair<string, string>>();
            foreach (int i in list.CheckedIndices) r.Add(items[i]);
            return r;
        }

        void Fill(List<KeyValuePair<string, string>> chosen)
        {
            items.Clear();
            list.Items.Clear();
            foreach (var d in Present()) items.Add(d);
            // ticked devices that are unplugged now stay in the list
            foreach (var c in chosen)
                if (!items.Exists(d => d.Key == c.Key)) items.Add(c);
            foreach (var d in items) {
                int i = list.Items.Add(d.Value + " (" + d.Key + ")");
                if (chosen.Exists(c => c.Key == d.Key)) list.SetItemChecked(i, true);
            }
        }

        // present USB devices (not their interfaces, not hubs): "vid:pid" -> name
        public static List<KeyValuePair<string, string>> Present()
        {
            var r = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>();
            try {
                using (var q = new ManagementObjectSearcher(
                        "SELECT Name, PNPDeviceID, Service FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID[_]%'")) {
                    foreach (ManagementObject o in q.Get()) {
                        string id = (o["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                        string svc = (o["Service"] as string ?? "").ToLowerInvariant();
                        // composite devices list their interfaces as ...&MI_xx
                        var m = Regex.Match(id, @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(\\|$)");
                        if (!m.Success || svc.StartsWith("usbhub")) continue;
                        string vp = (m.Groups[1].Value + ":" + m.Groups[2].Value).ToLowerInvariant();
                        if (seen.Add(vp)) r.Add(new KeyValuePair<string, string>(vp, o["Name"] as string ?? "USB"));
                    }
                }
            } catch (Exception) { }
            r.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
            return r;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using KippixShared;

namespace KippixOverlay
{
    /// <summary>(0.99.72) The overlay's HDR switch (per display) and Bluetooth: on / off and connecting paired headphones and speakers.</summary>
    internal partial class OverlayWindow
    {
        // read once and kept: the Bluetooth radio takes a moment to ask, so the rows show what was last known and update
        private static int btRadio = -2;            // -2 not asked yet, -1 none / unknown, 0 off, 1 on
        private static bool btRadioBusy;
        private static List<BtAudioDevice> btDevices;
        private static bool btListBusy;
        private static readonly HashSet<string> btBusyKeys = new HashSet<string>();
        private static readonly HashSet<string> hdrBusy = new HashSet<string>();

        private void OnUiLater(Action a)
        {
            try { Dispatcher.BeginInvoke(new Action(() => { try { a(); } catch (Exception ex) { plugin.LogError(ex, "Overlay update failed"); } })); } catch (Exception) { }
        }

        private MenuEntry SwitchRow(string glyph, string title, Func<string> sub, Func<string> value, Action toggle)
        {
            var valueText = new TextBlock { FontSize = 18, Foreground = OverlayTheme.TextMain, VerticalAlignment = VerticalAlignment.Center };
            Action refresh = () => valueText.Text = value();
            refresh();
            return new MenuEntry
            {
                Glyph = glyph,
                Title = title,
                Sub = sub,
                Right = valueText,
                Refresh = refresh,
                Activate = () => { toggle(); refresh(); },
                Adjust = d => { toggle(); refresh(); }
            };
        }

        /// <summary>Settings tab: HDR and Bluetooth.</summary>
        private void AddDeviceRows(List<MenuEntry> l)
        {
            l.Add(Header("Display and Bluetooth"));
            AddHdrRows(l, false);
            AddBluetoothRows(l);
            l.Add(Divider());
        }

        // ================= HDR =================

        private void AddHdrRows(List<MenuEntry> l, bool page)
        {
            List<HdrDisplay> displays;
            try { displays = Hdr.Displays(); } catch (Exception) { displays = new List<HdrDisplay>(); }
            var hdr = displays.Where(d => d.Supported).ToList();
            if (hdr.Count == 0)
            {
                l.Add(Note("", "HDR: " + (displays.Count == 0 ? "Windows didn't list your displays" : "your display doesn't support it")));
                return;
            }
            foreach (HdrDisplay d in hdr)
            {
                HdrDisplay disp = d;
                string title = hdr.Count == 1 ? "HDR" : "HDR · " + d.Name;
                l.Add(SwitchRow("", title,
                    () => { lock (hdrBusy) if (hdrBusy.Contains(disp.Key)) return "Switching… (the screen may go dark for a moment)"; return hdr.Count == 1 ? disp.Name + (disp.On ? "" : "  ·  some games only notice after a restart") : null; },
                    () => { lock (hdrBusy) if (hdrBusy.Contains(disp.Key)) return "…"; return disp.On ? "On" : "Off"; },
                    () => ToggleHdr(disp)));
            }
        }

        private void ToggleHdr(HdrDisplay d)
        {
            lock (hdrBusy) { if (!hdrBusy.Add(d.Key)) return; }
            bool want = !d.On;
            KippixShared.SafeWork.Queue(_ =>
            {
                bool ok = false;
                try { ok = Hdr.Set(d, want); } catch (Exception ex) { Hdr.LastError = ex.Message; }
                lock (hdrBusy) hdrBusy.Remove(d.Key);
                plugin.LogInfo("Kippix overlay: HDR " + (want ? "on" : "off") + " for " + d.Name + ": " + (ok ? "done" : "didn't take (" + (Hdr.LastError ?? "Windows kept it as it was") + ")"));
                OnUiLater(() =>
                {
                    if (!ok) plugin.Toast("HDR didn't switch " + (want ? "on" : "off"), d.Name);
                    if (IsOpen) RefreshEntries();
                });
            });
        }

        // ================= Bluetooth =================

        private static string BtRadioText()
        {
            if (btRadioBusy) return "…";
            switch (btRadio)
            {
                case 1: return "On";
                case 0: return "Off";
                case -2: return "…";
                default: return "—";
            }
        }

        private void ReadBtRadio(Action then)
        {
            KippixShared.SafeWork.Queue(_ =>
            {
                int s = Bluetooth.RadioState();
                btRadio = s;
                OnUiLater(() => { if (then != null) then(); else if (IsOpen) RefreshEntries(); });
            });
        }

        private void AddBluetoothRows(List<MenuEntry> l)
        {
            if (!btRadioBusy) ReadBtRadio(null);
            l.Add(SwitchRow("", "Bluetooth",
                () => btRadio == -1 && !btRadioBusy ? "No Bluetooth found (or Windows wouldn't say)" : (btRadio == 0 ? "Off: Bluetooth controllers and headphones can't connect" : null),
                BtRadioText,
                ToggleBtRadio));
            l.Add(new MenuEntry
            {
                Glyph = "",
                Title = "Bluetooth headphones and speakers",
                Sub = () => BtDevicesLine(),
                Chevron = true,
                Activate = OpenBtDevices
            });
        }

        private static string BtDevicesLine()
        {
            if (btDevices == null) return "Connect or disconnect a paired device";
            var on = btDevices.Where(d => d.Connected).Select(d => d.Name).ToList();
            if (on.Count > 0) return "Connected: " + string.Join(", ", on);
            return btDevices.Count == 0 ? "None paired" : btDevices.Count + " paired, none connected";
        }

        private void ToggleBtRadio()
        {
            if (btRadioBusy || btRadio < 0) { if (btRadio == -1) ReadBtRadio(null); return; }
            bool want = btRadio != 1;
            btRadioBusy = true;
            KippixShared.SafeWork.Queue(_ =>
            {
                bool ok = Bluetooth.SetRadio(want);
                int s = Bluetooth.RadioState();
                btRadio = s;
                btRadioBusy = false;
                plugin.LogInfo("Kippix overlay: Bluetooth " + (want ? "on" : "off") + ": " + (ok ? "done" : "didn't take (" + Bluetooth.LastError + ")"));
                OnUiLater(() =>
                {
                    if (!ok) plugin.Toast("Bluetooth didn't switch " + (want ? "on" : "off"), Bluetooth.LastError ?? "");
                    if (IsOpen) RefreshEntries();
                });
            });
        }

        private void LoadBtDevices(Page p)
        {
            if (btListBusy) return;
            btListBusy = true;
            KippixShared.SafeWork.Queue(_ =>
            {
                List<BtAudioDevice> list = null;
                try { list = Bluetooth.AudioDevices(); } catch (Exception) { }
                btListBusy = false;
                if (list != null) btDevices = list;
                OnUiLater(() => { if (p != null) RefreshPage(p); else if (IsOpen) RefreshEntries(); });
            });
        }

        private void OpenBtDevices()
        {
            Page page = null;
            page = Push("Bluetooth devices", () =>
            {
                var l = new List<MenuEntry>();
                l.Add(SwitchRow("", "Bluetooth", null, BtRadioText, ToggleBtRadio));
                l.Add(Header("Headphones and speakers"));
                if (btDevices == null) l.Add(Note("", "Looking…"));
                else if (btDevices.Count == 0) l.Add(Note("", btRadio == 0 ? "Bluetooth is off" : "No Bluetooth headphones or speakers are paired with this PC"));
                else
                    foreach (BtAudioDevice d in btDevices)
                    {
                        BtAudioDevice dev = d;
                        l.Add(new MenuEntry
                        {
                            Glyph = d.Headset ? "" : "",
                            Title = d.Name,
                            Dot = () => dev.Connected,
                            Sub = () =>
                            {
                                lock (btBusyKeys) if (btBusyKeys.Contains(dev.Key)) return dev.Connected ? "Disconnecting…" : "Connecting…";
                                return dev.Connected ? "Connected" : "Not connected";
                            },
                            Activate = () => ConnectBt(dev, page)
                        });
                    }
                l.Add(Note("", "Controllers connect from the controller itself (hold its pairing or Guide button). To pair something new, use Windows' Bluetooth settings."));
                return l;
            });
            LoadBtDevices(page);
        }

        private void ConnectBt(BtAudioDevice d, Page page)
        {
            lock (btBusyKeys) { if (!btBusyKeys.Add(d.Key)) return; }
            bool connect = !d.Connected;
            if (IsOpen) RefreshEntries();
            KippixShared.SafeWork.Queue(_ =>
            {
                bool asked = false, done = false;
                try
                {
                    asked = Bluetooth.Connect(d, connect);
                    done = asked && Bluetooth.WaitFor(d, connect, connect ? 12 : 6);
                }
                catch (Exception ex) { Bluetooth.LastError = ex.Message; }
                lock (btBusyKeys) btBusyKeys.Remove(d.Key);
                plugin.LogInfo("Kippix overlay: Bluetooth " + (connect ? "connect " : "disconnect ") + d.Name + ": " + (done ? "done" : asked ? "asked, not confirmed" : "failed (" + Bluetooth.LastError + ")"));
                try { btDevices = Bluetooth.AudioDevices(); } catch (Exception) { }
                OnUiLater(() =>
                {
                    if (!done) plugin.Toast(connect ? "Couldn't connect" : "Couldn't disconnect", d.Name + (connect ? " (is it on and nearby?)" : ""));
                    else if (connect) plugin.Toast("Connected", d.Name);
                    RefreshPage(page);
                });
            });
        }
    }
}

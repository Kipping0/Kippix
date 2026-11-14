using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using KippixShared;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.72) The Bluetooth page (top bar shortcut, Quick settings, profile menu › This PC): Bluetooth on / off, and connecting
    /// or disconnecting paired headphones and speakers. Plus HDR on / off in Quick settings. The work is Windows' own (SysControls.cs).
    /// </summary>
    public partial class ThemeApi
    {
        private int btPageRadio = -2;              // -2 not known yet, -1 none, 0 off, 1 on
        private bool btPageRadioBusy, btPageListBusy;
        private List<BtAudioDevice> btPageDevices;
        private readonly HashSet<string> btPageBusy = new HashSet<string>();

        private void BuildBluetoothPage()
        {
            PageSub = Loc("LOC_KX_PageBluetoothSub", "Turn Bluetooth on or off, and connect your headphones and speakers");
            BluetoothRows(null);
            ReadBluetoothAsync(true);
        }

        private bool BluetoothPageShown { get { return openPanel == "page" && pageKey == "bluetooth"; } }

        private void ReadBluetoothAsync(bool devices)
        {
            if (btPageRadioBusy || (devices && btPageListBusy)) return;
            btPageRadioBusy = true;
            if (devices) btPageListBusy = true;
            KippixShared.SafeWork.Queue(_ =>
            {
                int radio = Bluetooth.RadioState();
                List<BtAudioDevice> list = null;
                if (devices) { try { list = Bluetooth.AudioDevices(); } catch (Exception) { } }
                ui.BeginInvoke(new Action(() =>
                {
                    btPageRadio = radio;
                    btPageRadioBusy = false;
                    if (devices) { btPageListBusy = false; if (list != null) btPageDevices = list; }
                    if (BluetoothPageShown) BluetoothRows(FocusedRowKey());
                }));
            });
        }

        private string FocusedRowKey()
        {
            var r = FocusedPageItem() as PageRow;
            return r != null ? r.Key : null;
        }

        private string BtStateText()
        {
            if (btPageRadio == -2) return "…";
            return btPageRadio == 1 ? Loc("LOC_KX_On", "On") : btPageRadio == 0 ? Loc("LOC_KX_Off", "Off") : "—";
        }

        private void BluetoothRows(string focusKey)
        {
            PageRows.Clear();
            PageRows.Add(new PageRow
            {
                Key = "radio", Icon = "bluetooth", Text = Loc("LOC_KX_Bluetooth", "Bluetooth"),
                Right = btPageRadioBusy && btPageRadio != -2 ? "…" : BtStateText(),
                Sub = btPageRadio == -1 ? Loc("LOC_KX_BtNone", "This PC has no Bluetooth, or Windows wouldn't say") : btPageRadio == 0 ? Loc("LOC_KX_BtOffSub", "Bluetooth controllers and headphones can't connect while it's off") : "",
                Command = new RelayCommand(_ => ToggleBluetoothRadio())
            });
            AddPageHeader(Loc("LOC_KX_BtDevicesHeader", "HEADPHONES AND SPEAKERS"));
            if (btPageDevices == null)
                PageRows.Add(new PageRow { Key = "looking", Icon = "hourglass", Text = Loc("LOC_KX_BtLooking", "Looking for paired devices…") });
            else if (btPageDevices.Count == 0)
                PageRows.Add(new PageRow { Key = "none", Icon = "info", Text = btPageRadio == 0 ? Loc("LOC_KX_BtIsOff", "Bluetooth is off") : Loc("LOC_KX_BtNonePaired", "No Bluetooth headphones or speakers are paired with this PC") });
            else
                foreach (var d in btPageDevices)
                {
                    BtAudioDevice dev = d;
                    bool busy;
                    lock (btPageBusy) busy = btPageBusy.Contains(d.Key);
                    PageRows.Add(new PageRow
                    {
                        Key = "d:" + d.Key, Icon = "headphones", Text = d.Name,
                        Right = busy ? "…" : d.Connected ? Loc("LOC_KX_BtConnected", "Connected") : "",
                        Sub = busy ? (d.Connected ? Loc("LOC_KX_BtDisconnecting", "Disconnecting…") : Loc("LOC_KX_BtConnecting", "Connecting…")) : "",
                        Command = new RelayCommand(_ => ConnectBluetooth(dev))
                    });
                }
            PageRows.Add(new PageRow { Key = "refresh", Icon = "refresh", Text = Loc("LOC_KX_BtRefresh", "Look again"), Command = new RelayCommand(_ => { PlaySound("select"); ReadBluetoothAsync(true); }) });
            AddPageHeader(Loc("LOC_KX_BtOtherHeader", "CONTROLLERS AND NEW DEVICES"));
            PageRows.Add(new PageRow { Key = "ctrl", Icon = "controller", Text = Loc("LOC_KX_BtControllers", "Controllers connect from the controller"), Sub = Loc("LOC_KX_BtControllersSub", "Turn it on, or hold its pairing button the first time. It connects by itself once it's paired.") });
            PageRows.Add(new PageRow { Key = "pair", Icon = "add", Text = Loc("LOC_KX_BtPair", "Pair a new device"), Sub = Loc("LOC_KX_BtPairSub", "Opens Windows' Bluetooth settings"), Command = new RelayCommand(_ => OpenWindowsSettings("ms-settings:bluetooth")) });
            NotifyPageLists();
            var row = focusKey != null ? PageRows.FirstOrDefault(r => r.Key == focusKey) : null;
            FocusPageSoon(row);
        }

        private void ToggleBluetoothRadio()
        {
            if (btPageRadioBusy) return;
            if (btPageRadio < 0) { ReadBluetoothAsync(false); return; }
            bool want = btPageRadio != 1;
            PlaySound("select");
            btPageRadioBusy = true;
            BluetoothRows("radio");
            KippixShared.SafeWork.Queue(_ =>
            {
                bool ok = Bluetooth.SetRadio(want);
                int now = Bluetooth.RadioState();
                List<BtAudioDevice> list = null;
                try { list = Bluetooth.AudioDevices(); } catch (Exception) { }
                logger.Info("Kippix: Bluetooth " + (want ? "on" : "off") + ": " + (ok ? "done" : "didn't take (" + Bluetooth.LastError + ")"));
                ui.BeginInvoke(new Action(() =>
                {
                    btPageRadio = now;
                    btPageRadioBusy = false;
                    if (list != null) btPageDevices = list;
                    if (!ok) ShowNoticeSafe(Loc("LOC_KX_Bluetooth", "Bluetooth"), Loc("LOC_KX_BtSwitchFailed", "Bluetooth didn't switch"), Bluetooth.LastError ?? "");
                    if (BluetoothPageShown) BluetoothRows(FocusedRowKey() ?? "radio");
                }));
            });
        }

        private void ConnectBluetooth(BtAudioDevice d)
        {
            lock (btPageBusy) { if (!btPageBusy.Add(d.Key)) return; }
            bool connect = !d.Connected;
            PlaySound("select");
            BluetoothRows("d:" + d.Key);
            KippixShared.SafeWork.Queue(_ =>
            {
                bool asked = false, done = false;
                try
                {
                    asked = Bluetooth.Connect(d, connect);
                    done = asked && Bluetooth.WaitFor(d, connect, connect ? 12 : 6);
                }
                catch (Exception ex) { Bluetooth.LastError = ex.Message; }
                List<BtAudioDevice> list = null;
                try { list = Bluetooth.AudioDevices(); } catch (Exception) { }
                logger.Info("Kippix: Bluetooth " + (connect ? "connect " : "disconnect ") + d.Name + ": " + (done ? "done" : asked ? "asked, not confirmed" : "failed (" + Bluetooth.LastError + ")"));
                ui.BeginInvoke(new Action(() =>
                {
                    lock (btPageBusy) btPageBusy.Remove(d.Key);
                    if (list != null) btPageDevices = list;
                    if (!done) ShowNoticeSafe(Loc("LOC_KX_Bluetooth", "Bluetooth"), connect ? Loc("LOC_KX_BtConnectFailed", "Couldn't connect") : Loc("LOC_KX_BtDisconnectFailed", "Couldn't disconnect"),
                                     d.Name + (connect ? "  ·  " + Loc("LOC_KX_BtIsItOn", "is it on and nearby?") : ""));
                    if (BluetoothPageShown) BluetoothRows(FocusedRowKey() ?? "d:" + d.Key);
                }));
            });
        }

        /// <summary>Quick settings' Bluetooth line: what's connected, or on / off (read in the background; the row updates).</summary>
        private void FillQuickBluetooth(PickRow row)
        {
            KippixShared.SafeWork.Queue(_ =>
            {
                int radio = Bluetooth.RadioState();
                List<BtAudioDevice> list = null;
                if (radio == 1) { try { list = Bluetooth.AudioDevices(); } catch (Exception) { } }
                ui.BeginInvoke(new Action(() =>
                {
                    btPageRadio = radio;
                    if (list != null) btPageDevices = list;
                    string sub;
                    if (radio == 0) sub = Loc("LOC_KX_Off", "Off");
                    else if (radio < 0) sub = Loc("LOC_KX_QuickBluetoothSub2", "Connect headphones and speakers, pair new devices");
                    else
                    {
                        var on = list != null ? list.Where(x => x.Connected).Select(x => x.Name).ToList() : new List<string>();
                        sub = Loc("LOC_KX_On", "On") + (on.Count > 0 ? "   ·   " + string.Join(", ", on) : "");
                    }
                    row.SetSub(sub);
                }));
            });
        }

        // ================= HDR (Quick settings) =================

        /// <summary>Quick settings: one HDR switch per display that can show it (none when no display can).</summary>
        private void AddQuickHdrRows()
        {
            List<HdrDisplay> displays;
            try { displays = Hdr.Displays().Where(d => d.Supported).ToList(); } catch (Exception) { return; }
            foreach (var d in displays)
            {
                HdrDisplay disp = d;
                var row = new PickRow
                {
                    Text = displays.Count == 1 ? Loc("LOC_KX_Hdr", "HDR") : Loc("LOC_KX_Hdr", "HDR") + " · " + d.Name,
                    Sub = displays.Count == 1 ? d.Name : null,
                    Icon = "tv", Key = "hdr:" + d.Key, IsSwitch = true
                };
                row.IsSelected = d.On;
                row.Command = new RelayCommand(_ => ToggleHdr(disp, row));
                QuickRows.Add(row);
            }
        }

        private bool hdrSwitching;

        private void ToggleHdr(HdrDisplay d, PickRow row)
        {
            if (hdrSwitching) return;
            hdrSwitching = true;
            bool want = !d.On;
            PlaySound("select");
            row.IsSelected = want;
            row.SetSub(Loc("LOC_KX_HdrSwitching", "Switching… (the screen may go dark for a moment)"));
            KippixShared.SafeWork.Queue(_ =>
            {
                bool ok = false;
                try { ok = Hdr.Set(d, want); } catch (Exception ex) { Hdr.LastError = ex.Message; }
                logger.Info("Kippix: HDR " + (want ? "on" : "off") + " for " + d.Name + ": " + (ok ? "done" : "didn't take (" + (Hdr.LastError ?? "Windows kept it as it was") + ")"));
                ui.BeginInvoke(new Action(() =>
                {
                    hdrSwitching = false;
                    row.IsSelected = d.On;
                    row.SetSub(d.Name);
                    if (!ok) ShowNoticeSafe(Loc("LOC_KX_Hdr", "HDR"), Loc("LOC_KX_HdrFailed", "HDR didn't switch"), d.Name);
                }));
            });
        }
    }
}

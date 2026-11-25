using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>One button on the controller test: lights up while it's held.</summary>
    public class CtrlButton : Observable
    {
        public string Name { get; set; }
        internal ControllerInput Input;
        private bool isOn, wasOn;
        public bool IsOn { get { return isOn; } set { if (Set(ref isOn, value, "IsOn") && value && !wasOn) { wasOn = true; Notify("WasOn"); } } }
        /// <summary>Pressed at least once (so you can see which ones you've tried).</summary>
        public bool WasOn { get { return wasOn; } }
    }

    /// <summary>The controller page (live button and stick test, resting drift, battery) and the Quick settings panel.</summary>
    public partial class ThemeApi
    {
        // =====================================================================
        // Controller test
        // =====================================================================

        public List<CtrlButton> CtrlButtons { get; private set; }
        public string CtrlName { get; private set; }
        public string CtrlBatteryText { get; private set; }
        public Thickness CtrlLeftDot { get; private set; }
        public Thickness CtrlRightDot { get; private set; }
        public double CtrlLeftTrigger { get; private set; }
        public double CtrlRightTrigger { get; private set; }
        public string CtrlLeftText { get; private set; }
        public string CtrlRightText { get; private set; }
        public string CtrlDriftText { get; private set; }

        private DispatcherTimer ctrlTimer;
        private DateTime ctrlBackAt = DateTime.MinValue;
        private readonly Queue<Tuple<DateTime, double, double>> ctrlRest = new Queue<Tuple<DateTime, double, double>>();
        private const double StickBox = 200, StickDot = 22;

        private void InitControllerPage()
        {
            var names = new[]
            {
                Tuple.Create("A", ControllerInput.A), Tuple.Create("B", ControllerInput.B), Tuple.Create("X", ControllerInput.X), Tuple.Create("Y", ControllerInput.Y),
                Tuple.Create("LB", ControllerInput.LeftShoulder), Tuple.Create("RB", ControllerInput.RightShoulder),
                Tuple.Create("LT", ControllerInput.TriggerLeft), Tuple.Create("RT", ControllerInput.TriggerRight),
                Tuple.Create("↑", ControllerInput.DPadUp), Tuple.Create("↓", ControllerInput.DPadDown), Tuple.Create("←", ControllerInput.DPadLeft), Tuple.Create("→", ControllerInput.DPadRight),
                Tuple.Create("L3", ControllerInput.LeftStick), Tuple.Create("R3", ControllerInput.RightStick),
                Tuple.Create("View", ControllerInput.Back), Tuple.Create("Menu", ControllerInput.Start), Tuple.Create("Guide", ControllerInput.Guide)
            };
            CtrlButtons = names.Select(n => new CtrlButton { Name = n.Item1, Input = n.Item2 }).ToList();
            CtrlLeftDot = CtrlRightDot = DotMargin(0, 0);
        }

        private void BuildControllerPage()
        {
            PageSub = Loc("LOC_KX_PageControllerSub", "Button test, stick drift, battery");
            PageHint = Loc("LOC_KX_CtrlHint", "Press B twice to leave (B on its own is being tested)");
            InitControllerPage();
            Notify("CtrlButtons");
            ctrlRest.Clear();
            CtrlDriftText = Loc("LOC_KX_CtrlDriftWait", "Let go of both sticks for a couple of seconds to check for drift.");
            ReadBatteryForPage();
            ctrlTimer = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(33) };
            ctrlTimer.Tick += (s, e) => ControllerTick();
            ctrlTimer.Start();
            ControllerTick();
        }

        private void StopControllerTest()
        {
            if (ctrlTimer == null) return;
            ctrlTimer.Stop();
            ctrlTimer = null;
        }

        private void ReadBatteryForPage()
        {
            string raw = ReadControllerBattery();
            switch (raw)
            {
                case null: CtrlBatteryText = Loc("LOC_KX_CtrlBatteryNone", "Battery: not reported"); break;
                case "Wired": CtrlBatteryText = Loc("LOC_KX_CtrlWired", "Battery: wired"); break;
                case "Full": CtrlBatteryText = Loc("LOC_KX_CtrlFull", "Battery: full"); break;
                case "Medium": CtrlBatteryText = Loc("LOC_KX_CtrlMedium", "Battery: about half"); break;
                case "Low": CtrlBatteryText = Loc("LOC_KX_CtrlLow", "Battery: low"); break;
                case "Empty": CtrlBatteryText = Loc("LOC_KX_CtrlEmpty", "Battery: nearly empty"); break;
                case "Connected": CtrlBatteryText = Loc("LOC_KX_CtrlConnected", "Battery: connected (this controller doesn't report a level)"); break;
                default: CtrlBatteryText = string.Format(Loc("LOC_KX_CtrlBattery", "Battery: {0}"), raw); break;
            }
            Notify("CtrlBatteryText");
            CtrlBatteryLevel(raw);   // (1.0.7) the Console page's bar
        }

        private static Thickness DotMargin(short x, short y)
        {
            double half = (StickBox - StickDot) / 2;
            return new Thickness(half + x / 32768.0 * half, half + y / 32768.0 * half, 0, 0);
        }

        private void ControllerTick()
        {
            IGamepad pad = null;
            try { pad = api.GetConnectedControllers().OfType<IGamepad>().FirstOrDefault(g => g.Enabled); } catch (Exception) { }
            string name = pad != null ? pad.Name : Loc("LOC_KX_CtrlNone", "No controller found. Connect one (and check Playnite's controller support is on).");
            if (name != CtrlName) { CtrlName = name; Notify("CtrlName"); }
            if (pad == null) return;
            short lx = 0, ly = 0, rx = 0, ry = 0, lt = 0, rt = 0;
            var a = pad.AnalogInputState;
            var b = pad.ButtonInputState;
            if (a != null)
            {
                a.TryGetValue(ControllerInput.LeftStickX, out lx); a.TryGetValue(ControllerInput.LeftStickY, out ly);
                a.TryGetValue(ControllerInput.RightStickX, out rx); a.TryGetValue(ControllerInput.RightStickY, out ry);
                a.TryGetValue(ControllerInput.TriggerLeft, out lt); a.TryGetValue(ControllerInput.TriggerRight, out rt);
            }
            CtrlLeftDot = DotMargin(lx, ly);
            CtrlRightDot = DotMargin(rx, ry);
            CtrlLeftTrigger = Math.Max(0, lt / 32767.0) * 200;
            CtrlRightTrigger = Math.Max(0, rt / 32767.0) * 200;
            double lMag = Math.Min(1, Math.Sqrt((double)lx * lx + (double)ly * ly) / 32767.0);
            double rMag = Math.Min(1, Math.Sqrt((double)rx * rx + (double)ry * ry) / 32767.0);
            CtrlLeftText = Math.Round(lMag * 100) + "%";
            CtrlRightText = Math.Round(rMag * 100) + "%";
            bool any = false;
            foreach (var btn in CtrlButtons)
            {
                ControllerInputState st;
                bool on = b != null && b.TryGetValue(btn.Input, out st) && st == ControllerInputState.Pressed;
                if (!on && btn.Input == ControllerInput.TriggerLeft) on = lt > 8000;
                if (!on && btn.Input == ControllerInput.TriggerRight) on = rt > 8000;
                if (on && !btn.IsOn) CtrlPressedNow(btn.Name);   // (1.0.7) "last pressed"
                btn.IsOn = on;
                any |= on;
            }
            // drift: how far the sticks sit from the middle when nothing's being touched (the smallest movement over two seconds)
            var now = DateTime.Now;
            ctrlRest.Enqueue(Tuple.Create(now, lMag, rMag));
            while (ctrlRest.Count > 0 && (now - ctrlRest.Peek().Item1).TotalSeconds > 2) ctrlRest.Dequeue();
            if (!any && ctrlRest.Count > 30)
            {
                double lMax = ctrlRest.Max(t => t.Item2), rMax = ctrlRest.Max(t => t.Item3);
                double lMin = ctrlRest.Min(t => t.Item2), rMin = ctrlRest.Min(t => t.Item3);
                if (lMax - lMin < 0.04 && rMax - rMin < 0.04 && lMax < 0.35 && rMax < 0.35)   // held still (or let go)
                {
                    CtrlDriftText = string.Format(Loc("LOC_KX_CtrlDrift", "Resting: left stick {0}, right stick {1}.  {2}"),
                                                  DriftWord(lMax), DriftWord(rMax),
                                                  Math.Max(lMax, rMax) > 0.12
                                                      ? string.Format(Loc("LOC_KX_CtrlDeadzone", "A deadzone of about {0}% in your games would hide it."), Math.Ceiling(Math.Max(lMax, rMax) * 100 + 3))
                                                      : Loc("LOC_KX_CtrlNoDrift", "No drift worth worrying about."));
                    Notify("CtrlDriftText");
                }
            }
            foreach (string n in new[] { "CtrlLeftDot", "CtrlRightDot", "CtrlLeftTrigger", "CtrlRightTrigger", "CtrlLeftText", "CtrlRightText" }) Notify(n);
        }

        private string DriftWord(double mag)
        {
            int pct = (int)Math.Round(mag * 100);
            return pct + "%" + (mag > 0.12 ? " (" + Loc("LOC_KX_CtrlDriftWord", "drifting") + ")" : "");
        }

        /// <summary>On the controller page every button is being tested: only B twice (or Esc) leaves.</summary>
        private bool ControllerPageKey(KeyEventArgs e, bool back)
        {
            if (back)
            {
                if ((e.Key == Key.Escape && e.GetType().Name != "GameControllerInputEventArgs") || (DateTime.Now - ctrlBackAt).TotalSeconds < 1.5) { ctrlBackAt = DateTime.MinValue; return PageBack(); }
                ctrlBackAt = DateTime.Now;
            }
            e.Handled = true;
            return true;
        }

        // =====================================================================
        // Quick settings (a panel on the right): volume, display, Wi-Fi, Bluetooth, controller battery
        // =====================================================================

        public ObservableCollection<PickRow> QuickRows { get; private set; }
        public bool IsQuickOpen { get { return openPanel == "quick"; } }
        public string QuickVolumeText { get; private set; }
        public double QuickVolumeWidth { get; private set; }
        public bool QuickMuted { get; private set; }
        public ICommand QuickMuteCommand { get; private set; }

        private void InitQuickSettings()
        {
            QuickRows = new ObservableCollection<PickRow>();
            QuickMuteCommand = new RelayCommand(_ => { try { Audio.ToggleMasterMute(); } catch (Exception) { } PlaySound("move"); ReadQuickVolume(); });
        }

        private void OpenQuickSettings()
        {
            OpenQuickSettings(null);
        }

        /// <summary>Opens Quick settings on the volume row, or on the row with <paramref name="focusKey"/> (coming back from the sound lists).</summary>
        private void OpenQuickSettings(string focusKey)
        {
            if (openPanel == "quick") { ClosePanels(); return; }
            BuildQuickRows();
            ReadQuickVolume();
            TogglePanel("quick");
            var row = focusKey != null ? QuickRows.FirstOrDefault(r => r.Key == focusKey) : null;
            ui.BeginInvoke(new Action(() =>
            {
                if (row != null) UiTools.FocusItem("KX_QuickList", row);
                else UiTools.FocusByName("KX_QuickVolume");
            }), DispatcherPriority.ContextIdle);
        }

        private void ReadQuickVolume()
        {
            bool muted = false;
            float level = 0;
            try { level = Audio.GetMaster(out muted); } catch (Exception) { }
            if (level < 0) { level = 0; muted = false; }   // -1: no sound device to read
            QuickMuted = muted;
            QuickVolumeText = muted ? Loc("LOC_KX_Muted", "Muted") : Math.Round(level * 100) + "%";
            QuickVolumeWidth = Math.Max(0, level * 400);
            foreach (string n in new[] { "QuickVolumeText", "QuickVolumeWidth", "QuickMuted" }) Notify(n);
        }

        private void BuildQuickRows()
        {
            QuickRows.Clear();
            // its line: the device the sound plays on now
            string output = null;
            try { var dev = Audio.OutputDevices().FirstOrDefault(d => d.IsDefault); if (dev != null) output = dev.Name; } catch (Exception) { }
            QuickRows.Add(new PickRow { Text = Loc("LOC_KX_SoundOutput", "Sound output"), Sub = string.IsNullOrEmpty(output) ? Loc("LOC_KX_SoundOutputSub", "Choose where the sound plays") : output, Icon = "speaker", Key = "audio", Command = new RelayCommand(_ => OpenAudioFromQuick(false)) });
            QuickRows.Add(new PickRow { Text = Loc("LOC_KX_SoundInput", "Sound input"), Sub = MicText(), Icon = "mic", Key = "mic", Command = new RelayCommand(_ => OpenAudioFromQuick(true)) });
            QuickRows.Add(new PickRow { Text = Loc("LOC_KX_QuickDisplay", "Display"), Sub = Loc("LOC_KX_QuickDisplaySub", "Brightness, night light, resolution (Windows settings)"), Icon = "sun", Key = "display", Command = new RelayCommand(_ => OpenWindowsSettings("ms-settings:display")) });
            string wifi = WifiState();
            QuickRows.Add(new PickRow { Text = Loc("LOC_KX_QuickWifi", "Wi‑Fi"), Sub = wifi, Icon = "wifi", Key = "wifi", Command = new RelayCommand(_ => OpenWindowsSettings("ms-settings:network-wifi")) });
            AddQuickHdrRows();   // (0.99.72) HDR on / off, per display that can show it
            // (0.99.72) Kippix's own Bluetooth page: on / off and connecting headphones and speakers (pairing is still Windows')
            var btRow = new PickRow { Text = Loc("LOC_KX_QuickBluetooth", "Bluetooth"), Sub = Loc("LOC_KX_QuickBluetoothSub2", "Connect headphones and speakers, pair new devices"), Icon = "bluetooth", Key = "bluetooth", Command = new RelayCommand(_ => { ClosePanels(); OpenPageFromShortcut("bluetooth"); }) };
            QuickRows.Add(btRow);
            FillQuickBluetooth(btRow);
            ReadBatteryForPage();
            QuickRows.Add(new PickRow { Text = Loc("LOC_KX_PageController", "Controller"), Sub = CtrlBatteryText + "   ·   " + Loc("LOC_KX_QuickCtrlSub", "test buttons and sticks"), Icon = "controller", Key = "controller", Command = new RelayCommand(_ => { ClosePanels(); OpenPageFromShortcut("controller"); }) });
            QuickRows.Add(new PickRow { Text = Loc("LOC_KX_Power", "Power"), Sub = Loc("LOC_KX_PowerSub", "Desktop mode, exit Playnite, sleep, restart, shut down"), Icon = "power", Key = "power", Command = new RelayCommand(_ => { ClosePanels(); ToggleProfile(); SetProfilePage("power"); }) });
        }

        /// <summary>Sound input's line: the microphone Windows uses now, its level, or Muted.</summary>
        private string MicText()
        {
            try
            {
                var dev = Audio.InputDevices().FirstOrDefault(d => d.IsDefault);
                if (dev == null) return Loc("LOC_KX_MicNone", "No microphone found");
                bool muted;
                float level = Audio.GetInput(out muted);
                string state = muted ? Loc("LOC_KX_Muted", "Muted") : (level >= 0 ? Math.Round(level * 100) + "%" : "");
                return dev.Name + (state.Length > 0 ? "   ·   " + state : "");
            }
            catch (Exception) { return Loc("LOC_KX_SoundInputSub", "Choose the microphone"); }
        }

        /// <summary>Left / Right on Sound input: the previous / next microphone becomes Windows' default.</summary>
        private void CycleMic(int step)
        {
            try
            {
                var list = Audio.InputDevices().OrderBy(d => d.Name).ToList();
                if (list.Count < 2) return;
                int i = list.FindIndex(d => d.IsDefault);
                var next = list[((i < 0 ? 0 : i) + step + list.Count) % list.Count];
                Audio.SetDefault(next.Id);
            }
            catch (Exception) { }
        }

        private string WifiState()
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up);
                if (nic != null) return Loc("LOC_KX_WifiConnected", "Connected");
                bool wired = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.OperationalStatus == OperationalStatus.Up);
                return wired ? Loc("LOC_KX_WiredConnected", "Not in use (you're on a cable)") : Loc("LOC_KX_WifiOff", "Not connected");
            }
            catch (Exception) { return ""; }
        }

        private void OpenWindowsSettings(string uri)
        {
            PlaySound("select");
            ClosePanels();
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { logger.Warn("Kippix: " + uri + ": " + ex.Message); }
        }

        /// <summary>Quick settings: Left / Right on the volume row turns it down / up; on Sound input it changes the microphone.</summary>
        private bool QuickPanelKey(KeyEventArgs e)
        {
            if (openPanel != "quick") return false;
            var fe = Keyboard.FocusedElement as FrameworkElement;
            if (fe == null) return false;
            if (e.Key != Key.Left && e.Key != Key.Right) return false;
            var micRow = fe.DataContext as PickRow;
            if (micRow != null && micRow.Key == "mic" && QuickRows.Contains(micRow))
            {
                CycleMic(e.Key == Key.Right ? 1 : -1);
                PlaySound("move");
                micRow.SetSub(MicText());
                e.Handled = true;
                return true;
            }
            if (fe.Name != "KX_QuickVolume") return false;
            try
            {
                bool muted;
                float level = Audio.GetMaster(out muted);
                if (level < 0) throw new InvalidOperationException("no volume to read");   // don't set it from a made-up 0
                level = (float)Math.Max(0, Math.Min(1, Math.Round((level + (e.Key == Key.Right ? 0.05 : -0.05)) * 20) / 20));
                Audio.SetMaster(level);
                if (muted && e.Key == Key.Right) Audio.ToggleMasterMute();
            }
            catch (Exception) { }
            PlaySound("move");
            ReadQuickVolume();
            e.Handled = true;
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// Removes the second copy of each controller press in Playnite fullscreen (the Steam Controller: a key feature).
    ///
    /// Playnite reads the controller itself: buttons come as its controller events, the D-pad / stick (and PageUp / PageDown)
    /// as keys it sends to its window (SendMessage from its controller thread). Steam, outside games, ALSO turns the controller
    /// into keyboard keys (its Desktop Layout): arrows, Enter for A, Escape for B, and other keys for the other buttons. So every
    /// press arrived twice: lists skipped, Y also "pressed Space", the bumpers also typed keys.
    ///
    /// The copies are told apart by where they come from, not by timing: Playnite's keys are sent messages (InSendMessage is
    /// true while WPF handles them), Steam's are ordinary posted keyboard input. Before WPF routes anything
    /// (InputManager.PreProcessInput), so every window and handler sees one copy (the overlay too):
    ///  - arrows / PageUp / PageDown: each press from one side cancels one matching press from the other (first come, first served);
    ///  - A / B: the controller's own button wins: Steam's Enter / Escape waits a moment and only goes through if no A / B came;
    ///  - any other controller button (X, Y, bumpers, Start, View, triggers, stick clicks): the controller's button wins over
    ///    whatever key(s) Steam makes for it (any key that isn't a typing key), the same way;
    ///  - the typed character such a key makes (Enter's line break, Space) goes with it.
    /// Only one side present (a keyboard, an Xbox pad, Steam with a gamepad layout): nothing pairs, nothing is dropped; keys are
    /// only ever held while Playnite's controller has been used in the last 2 minutes and a Valve controller is connected.
    /// (The shelved stand-alone add-on KippixSteamController started from this file; this copy is the live one.)
    /// </summary>
    internal sealed class SteamCopyFilter
    {
        [DllImport("user32.dll")]
        private static extern bool InSendMessage();

        private static readonly ILogger logger = LogManager.GetLogger();

        private sealed class PressRec { public bool Playnite; public string Meaning; public DateTime At; }
        private sealed class HeldKey
        {
            public Key Key; public bool UpSeen; public string Text; public DateTime At;
            public bool Follower;   // not a copy: a key that came after a held one and waits its turn
            public bool NoChar;     // makes no character of its own (never takes one)
        }

        private readonly Func<bool> enabled, preferPad;
        private readonly Action<string> log;
        private readonly Func<KeyEventArgs, bool> skip;
        private readonly Dispatcher ui;
        private readonly List<PressRec> unpairedPresses = new List<PressRec>();
        private const double PairWindowMs = 350;
        private DateTime lastPlayniteInputAt = DateTime.MinValue;

        // the KeyDown WPF makes from a PreviewKeyDown gets the same answer
        private Key promoKey = Key.None;
        private int promoStamp;
        private bool promoDrop, promoPending;
        private DateTime promoAt;

        // the key-ups / button releases of dropped presses (they expire: a release that never came, e.g. a game took the
        // window, must not swallow a later one)
        private readonly Dictionary<Key, DateTime> dropKeyUps = new Dictionary<Key, DateTime>();
        private readonly Dictionary<ControllerInput, DateTime> dropPadUps = new Dictionary<ControllerInput, DateTime>();
        private const double DropUpMs = 1500;

        // the typed character a dropped key still makes: eaten too, or it lands in a text box
        private DateTime eatTextUntil = DateTime.MinValue;

        // Steam's keys held back a moment (one press: Enter / Escape, or the key(s) Steam makes for another button)
        private readonly List<HeldKey> held = new List<HeldKey>();
        private string heldMeaning;
        private DispatcherTimer heldTimer;

        /// <param name="skip">Events another part of the add-on sends itself (left alone).</param>
        public SteamCopyFilter(Dispatcher ui, Func<bool> enabled, Func<bool> preferPad, Action<string> log, Func<KeyEventArgs, bool> skip)
        {
            // (the theme hands this over before its own dispatcher field is set: the UI thread's own, then)
            this.ui = ui ?? (Application.Current != null ? Application.Current.Dispatcher : Dispatcher.CurrentDispatcher);
            this.enabled = enabled;
            this.preferPad = preferPad;
            this.log = log;
            this.skip = skip;
        }

        public void Start()
        {
            InputManager.Current.PreProcessInput += OnPreProcess;
            // Playnite's window losing the foreground (a game starting): nothing half-done carries over
            if (Application.Current != null) Application.Current.Deactivated += (s, a) => Reset();
            SteamDevice.Refresh();
        }

        /// <summary>Playnite's controller pressed something just now (for the button icons: the keys around it are Steam's).</summary>
        public DateTime LastControllerInput { get { return lastPlayniteInputAt; } }

        private void Log(string text) { if (log != null) log(text); }

        public void Reset()
        {
            dropKeyUps.Clear();
            dropPadUps.Clear();
            unpairedPresses.Clear();
            promoPending = false;
            eatTextUntil = DateTime.MinValue;
            eatChar = null;
            if (heldTimer != null) heldTimer.Stop();
            held.Clear();
            releasing.Clear();
            heldMeaning = null;
        }

        // ---------------- what a press is ----------------

        /// <summary>Playnite's own controller event (GameControllerInputEventArgs lives in Playnite, not the SDK): its button, or None.</summary>
        internal static ControllerInput PadButton(KeyEventArgs e)
        {
            if (e.GetType().Name != "GameControllerInputEventArgs") return ControllerInput.None;
            var b = e.GetType().GetProperty("Button");
            object v = b != null ? b.GetValue(e, null) : null;
            return v is ControllerInput ? (ControllerInput)v : ControllerInput.None;
        }

        /// <summary>The controller's own buttons that Steam also turns into keys (not A / B, the D-pad or the sticks' directions, nor the Steam button).</summary>
        private static bool IsOtherButton(ControllerInput b)
        {
            switch (b)
            {
                case ControllerInput.X: case ControllerInput.Y:
                case ControllerInput.LeftShoulder: case ControllerInput.RightShoulder:
                case ControllerInput.Start: case ControllerInput.Back:
                case ControllerInput.LeftStick: case ControllerInput.RightStick:
                case ControllerInput.TriggerLeft: case ControllerInput.TriggerRight:
                    return true;
            }
            return false;
        }

        /// <summary>Arrows / PageUp / PageDown (Playnite sends these itself), Enter (A), Escape (B); null for anything else.</summary>
        private static string KeyMeaning(Key k)
        {
            switch (k)
            {
                case Key.Up: return "up";
                case Key.Down: return "down";
                case Key.Left: return "left";
                case Key.Right: return "right";
                case Key.PageUp: return "pgup";
                case Key.PageDown: return "pgdn";
                case Key.Enter: return "a";
                case Key.Escape: return "b";
            }
            return null;
        }

        /// <summary>A key you type with (letters, digits, punctuation, the number pad): never held.</summary>
        private static bool IsTypingKey(Key k)
        {
            if (k >= Key.A && k <= Key.Z) return true;
            if (k >= Key.D0 && k <= Key.D9) return true;
            if (k >= Key.NumPad0 && k <= Key.Divide) return true;
            if (k >= Key.Oem1 && k <= Key.OemBackslash) return true;
            if (k == Key.ImeProcessed || k == Key.DeadCharProcessed || k == Key.None) return true;
            return false;
        }

        private static bool FocusInTextBox()
        {
            var f = Keyboard.FocusedElement;
            return f is TextBoxBase || f is PasswordBox;
        }

        private static bool TakeFresh<T>(Dictionary<T, DateTime> d, T key)
        {
            DateTime at;
            if (!d.TryGetValue(key, out at)) return false;
            d.Remove(key);
            return (DateTime.UtcNow - at).TotalMilliseconds < DropUpMs;
        }

        /// <summary>Playnite's controller is in use (any of its controller events or keys in the last 2 minutes).</summary>
        private bool PlayniteControllerActive
        {
            get { return (DateTime.UtcNow - lastPlayniteInputAt).TotalMinutes < 2; }
        }

        private bool MayHold { get { return preferPad() && PlayniteControllerActive && SteamDevice.Connected; } }

        /// <summary>True: this press is the other side's copy of one already handled (cancel it).</summary>
        private bool PairPress(bool playnite, string meaning)
        {
            var now = DateTime.UtcNow;
            unpairedPresses.RemoveAll(r => (now - r.At).TotalMilliseconds > PairWindowMs);
            for (int i = 0; i < unpairedPresses.Count; i++)
            {
                var r = unpairedPresses[i];
                if (r.Playnite != playnite && r.Meaning == meaning)
                {
                    unpairedPresses.RemoveAt(i);
                    return true;
                }
            }
            Remember(playnite, meaning);
            return false;
        }

        private void Remember(bool playnite, string meaning)
        {
            unpairedPresses.Add(new PressRec { Playnite = playnite, Meaning = meaning, At = DateTime.UtcNow });
            if (unpairedPresses.Count > 16) unpairedPresses.RemoveAt(0);
        }

        /// <summary>An unpaired press of the controller's (with this meaning) just now: taken (it's this key's original).</summary>
        private bool TakePlaynitePress(string meaning)
        {
            var now = DateTime.UtcNow;
            unpairedPresses.RemoveAll(r => (now - r.At).TotalMilliseconds > PairWindowMs);
            int i = unpairedPresses.FindIndex(r => r.Playnite && r.Meaning == meaning);
            if (i < 0) return false;
            unpairedPresses.RemoveAt(i);
            return true;
        }

        // ---------------- the filter ----------------

        // what this filter sends itself (re-sent held keys): left alone, by identity (a global "busy" flag would also let
        // through everything pressed in a dialog the re-sent key opened)
        private readonly List<KeyValuePair<Key, int>> injectedKeys = new List<KeyValuePair<Key, int>>();
        private readonly List<TextComposition> injectedText = new List<TextComposition>();
        private DateTime btnAbsorbUntil = DateTime.MinValue;   // right after a controller button: further Steam keys for it go too
        private const double BtnAbsorbMs = 120;

        private bool IsInjected(InputEventArgs input)
        {
            var tc = input as TextCompositionEventArgs;
            if (tc != null) return injectedText.Contains(tc.TextComposition);
            var k = input as KeyEventArgs;
            if (k == null) return false;
            foreach (var p in injectedKeys) if (p.Key == k.Key && p.Value == k.Timestamp) return true;
            return false;
        }

        /// <summary>The character a key types by itself (null: none, or it depends on the keyboard layout).</summary>
        private static string KeyChar(Key k)
        {
            switch (k)
            {
                case Key.Enter: return "\r";
                case Key.Space: return " ";
                case Key.Tab: return "\t";
                case Key.Back: return "\b";
                case Key.Escape: return "\u001b";
            }
            return null;
        }

        private void OnPreProcess(object sender, PreProcessInputEventArgs e)
        {
            try
            {
                var input = e.StagingItem.Input;
                if (input == null || IsInjected(input)) return;
                var now = DateTime.UtcNow;
                var anyKey = input as KeyEventArgs;
                if (anyKey != null && anyKey.RoutedEvent == Keyboard.KeyDownEvent && PadButton(anyKey) != ControllerInput.None)
                {
                    // when the controller was last used (for the button icons)
                    lastPlayniteInputAt = now;
                }
                if (!enabled()) return;

                // ---- the character a held / dropped key types ----
                var tc = input as TextCompositionEventArgs;
                if (tc != null)
                {
                    // a held key's own character goes with it (a key that makes no character never takes one)
                    for (int i = held.Count - 1; i >= 0; i--)
                    {
                        var h = held[i];
                        if (h.Text != null || h.NoChar || (now - h.At).TotalMilliseconds > 200) continue;
                        string want = KeyChar(h.Key);
                        if (want != null && want != tc.Text) continue;
                        h.Text = tc.Text;
                        e.Cancel();
                        return;
                    }
                    if (eatChar != null && tc.Text == eatChar && now < eatTextUntil) { eatChar = null; e.Cancel(); }
                    return;
                }

                var k = anyKey;
                if (k == null || (skip != null && skip(k))) return;
                var re = k.RoutedEvent;
                bool down = re == Keyboard.PreviewKeyDownEvent || re == Keyboard.KeyDownEvent;
                bool up = re == Keyboard.PreviewKeyUpEvent || re == Keyboard.KeyUpEvent;
                if (!down && !up) return;

                // ---- Playnite's controller buttons ----
                var pad = PadButton(k);
                if (pad != ControllerInput.None)
                {
                    if (up)
                    {
                        if (TakeFresh(dropPadUps, pad)) e.Cancel();
                        return;
                    }
                    lastPlayniteInputAt = now;
                    string pm = pad == ControllerInput.A ? "a" : pad == ControllerInput.B ? "b" : IsOtherButton(pad) ? "btn" : null;
                    if (pm == null) return;
                    if (pm == "btn") btnAbsorbUntil = now.AddMilliseconds(BtnAbsorbMs);
                    // Steam's key(s) for this same press were waiting: the controller's button wins (a button also wins over a
                    // held Enter / Escape / PageUp / PageDown: Steam may make those for Start, View or the bumpers)
                    if (held.Exists(x => !x.Follower) && (heldMeaning == pm || pm == "btn"))
                    {
                        Log("controller " + pad + " wins over Steam's " + HeldNames());
                        DropHeld();
                        if (pm == "btn") Remember(true, "btn");
                        return;
                    }
                    if (pm == "btn") { Remember(true, "btn"); return; }   // the controller's button always goes through
                    if (PairPress(true, pm)) { e.Cancel(); dropPadUps[pad] = now; Log("dropped controller " + pad); }
                    return;
                }

                if (k.Key == Key.None) return;

                // ---- keys ----
                if (up)
                {
                    var h = held.Find(x => x.Key == k.Key && !x.UpSeen) ?? releasing.Find(x => x.Key == k.Key && !x.UpSeen);
                    if (h != null) { h.UpSeen = true; e.Cancel(); return; }   // goes with its down when that's passed on
                    if (TakeFresh(dropKeyUps, k.Key)) e.Cancel();   // the up of a dropped copy (a cancelled PreviewKeyUp makes no KeyUp)
                    return;
                }

                if (re == Keyboard.KeyDownEvent && promoPending && k.Key == promoKey && k.Timestamp == promoStamp && (now - promoAt).TotalMilliseconds < 200)
                {
                    promoPending = false;   // the KeyDown made from the PreviewKeyDown just decided
                    if (promoDrop) e.Cancel();
                    return;
                }

                bool fromPlaynite = InSendMessage();   // Playnite's own keys (sent from its controller thread)
                string meaning = KeyMeaning(k.Key);
                bool drop = false, hold = false;
                bool freshBtn = now < btnAbsorbUntil;

                if (fromPlaynite)
                {
                    lastPlayniteInputAt = now;
                    if (meaning == null) return;
                    var same = held.Find(x => x.Key == k.Key && !x.Follower);
                    if (same != null) { Log("Playnite's " + k.Key + " wins over Steam's"); DropHeld(); }
                    else drop = PairPress(true, meaning);
                }
                else if (k.Key == Key.System || k.Key == Key.LWin || k.Key == Key.RWin) return;   // Alt combinations / Windows key: never touched
                else if (meaning == "a" || meaning == "b")
                {
                    drop = PairPress(false, meaning);
                    if (!drop && freshBtn && TakePlaynitePress("btn"))
                    {
                        // Steam's Enter / Escape for Start or View (the button's own press came first)
                        drop = true;
                        unpairedPresses.RemoveAll(r => !r.Playnite && r.Meaning == meaning);
                    }
                    if (!drop && !k.IsRepeat && MayHold)
                    {
                        // Steam's Enter / Escape while Playnite's controller is in use: wait for the controller's own button
                        unpairedPresses.RemoveAll(r => !r.Playnite && r.Meaning == meaning);
                        hold = true;
                    }
                }
                else if (meaning != null)
                {
                    drop = PairPress(false, meaning);   // arrows / PageUp / PageDown: first come, first served
                    if (!drop && (meaning == "pgup" || meaning == "pgdn"))
                    {
                        // PageUp / PageDown may be Steam's key for a bumper / trigger: that button's own press wins
                        if (TakePlaynitePress("btn") || freshBtn) drop = true;
                        else if (!k.IsRepeat && MayHold)
                        {
                            unpairedPresses.RemoveAll(r => !r.Playnite && r.Meaning == meaning);
                            hold = true;
                        }
                        if (drop) unpairedPresses.RemoveAll(r => !r.Playnite && r.Meaning == meaning);
                    }
                }
                else if (!IsTypingKey(k.Key) && !FocusInTextBox())
                {
                    // another key from Steam for a controller button (X, Y, bumpers...): the button's own press wins
                    if (TakePlaynitePress("btn") || freshBtn) drop = true;
                    else if (!k.IsRepeat && MayHold) hold = true;
                }

                if (!drop && !hold && held.Count > 0)
                {
                    // anything else straight after a held key: it waits its turn (the held key goes first, then this, in order)
                    held.Add(new HeldKey { Key = k.Key, At = now, Follower = true, NoChar = !IsTypingKey(k.Key) && KeyChar(k.Key) == null });
                    if (re == Keyboard.PreviewKeyDownEvent) promoPending = false;
                    e.Cancel();
                    ui.BeginInvoke(new Action(ReleaseHeld), DispatcherPriority.Input);
                    return;
                }
                if (!drop && !hold) return;

                if (hold)
                {
                    HoldKey(k.Key, meaning ?? "btn");
                    Log("held the key " + k.Key);
                }
                if (re == Keyboard.PreviewKeyDownEvent)
                {
                    promoKey = k.Key; promoStamp = k.Timestamp; promoAt = now;
                    promoDrop = true;
                    promoPending = false;   // a cancelled Preview makes no KeyDown
                }
                if (drop)
                {
                    Log("dropped " + (fromPlaynite ? "Playnite's " : "the key ") + k.Key);
                    dropKeyUps[k.Key] = now;
                    if (!fromPlaynite && KeyChar(k.Key) != null) { eatChar = KeyChar(k.Key); eatTextUntil = now.AddMilliseconds(200); }   // its typed character too
                }
                e.Cancel();
            }
            catch (Exception ex) { logger.Warn(ex, "Kippix: checking a press"); }
        }

        private string HeldNames()
        {
            var names = new List<string>();
            foreach (var h in held) if (!h.Follower) names.Add(h.Key.ToString());
            return string.Join("+", names.ToArray());
        }

        // ---------------- Steam's keys held back ----------------

        private string eatChar;
        private readonly List<HeldKey> releasing = new List<HeldKey>();   // taken out of the hold, about to be passed on

        private void HoldKey(Key key, string meaning)
        {
            if (heldTimer == null)
            {
                heldTimer = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(PairWindowMs) };
                heldTimer.Tick += (s, a) => ReleaseHeld();
            }
            if (held.Count > 0 && (heldMeaning != meaning || meaning != "btn" || held.Exists(x => x.Follower)))
            {
                // one press at a time: the earlier one goes through (after this input is done)
                var earlier = new List<HeldKey>(held);
                string m0 = heldMeaning;
                held.Clear();
                heldMeaning = null;
                if (heldTimer != null) heldTimer.Stop();
                releasing.AddRange(earlier);   // their ups are still caught until they're passed on
                ui.BeginInvoke(new Action(() => PassOn(earlier, m0)), DispatcherPriority.Input);
            }
            bool first = held.Count == 0;
            held.Add(new HeldKey { Key = key, At = DateTime.UtcNow, NoChar = KeyChar(key) == null });   // (a second key Steam makes for the same button joins it)
            heldMeaning = meaning;
            if (first) { heldTimer.Stop(); heldTimer.Start(); }   // timed from the first key: a hold never runs on and on
        }

        /// <summary>The controller pressed the button itself: Steam's copy is thrown away (its key-up and character too). Keys
        /// that were only waiting their turn go on.</summary>
        private void DropHeld()
        {
            if (heldTimer != null) heldTimer.Stop();
            var now = DateTime.UtcNow;
            var followers = new List<HeldKey>();
            foreach (var h in held)
            {
                if (h.Follower) { followers.Add(h); continue; }
                if (!h.UpSeen) dropKeyUps[h.Key] = now;
                if (h.Text == null && KeyChar(h.Key) != null) { eatChar = KeyChar(h.Key); eatTextUntil = now.AddMilliseconds(200); }
            }
            held.Clear();
            heldMeaning = null;
            if (followers.Count > 0)
            {
                releasing.AddRange(followers);
                ui.BeginInvoke(new Action(() => PassOn(followers, null)), DispatcherPriority.Input);
            }
        }

        /// <summary>No button from the controller came: they were real keys, so they go through now.</summary>
        private void ReleaseHeld()
        {
            if (heldTimer != null) heldTimer.Stop();
            if (held.Count == 0) return;
            var keys = new List<HeldKey>(held);
            string meaning = heldMeaning;
            held.Clear();
            heldMeaning = null;
            releasing.AddRange(keys);
            PassOn(keys, meaning);
        }

        private void PassOn(List<HeldKey> keys, string meaning)
        {
            // a controller A / B arriving late still counts as this press's copy (and is dropped)
            if (meaning == "a" || meaning == "b") PairPress(false, meaning);
            foreach (var h in keys)
            {
                releasing.Remove(h);
                Reinject(h.Key, h.UpSeen, h.Text);
            }
        }

        private void Reinject(Key key, bool upToo, string text)
        {
            try
            {
                var source = Keyboard.PrimaryDevice.ActiveSource;
                if (source == null) return;
                int stamp = Environment.TickCount;
                injectedKeys.Add(new KeyValuePair<Key, int>(key, stamp));
                if (injectedKeys.Count > 32) injectedKeys.RemoveAt(0);
                var d = new KeyEventArgs(Keyboard.PrimaryDevice, source, stamp, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                InputManager.Current.ProcessInput(d);   // (WPF makes its KeyDown with the same key and time stamp)
                if (!string.IsNullOrEmpty(text) && Keyboard.FocusedElement != null)
                {
                    var comp = new TextComposition(InputManager.Current, Keyboard.FocusedElement, text);
                    injectedText.Add(comp);
                    if (injectedText.Count > 16) injectedText.RemoveAt(0);
                    TextCompositionManager.StartComposition(comp);
                }
                if (upToo)
                {
                    var u = new KeyEventArgs(Keyboard.PrimaryDevice, source, stamp, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
                    InputManager.Current.ProcessInput(u);
                }
            }
            catch (Exception ex) { logger.Warn(ex, "Kippix: passing a held key on"); }
        }
    }

    /// <summary>Is a Valve controller (Steam Controller, Steam Deck: USB vendor 28DE) connected? Checked off the UI thread, at most every 15 s.</summary>
    internal static class SteamDevice
    {
        private static volatile bool connected;
        private static DateTime checkedAt = DateTime.MinValue;
        private static int busy;

        public static bool Connected
        {
            get
            {
                if ((DateTime.UtcNow - checkedAt).TotalSeconds > 15) Refresh();
                return connected;
            }
        }

        public static void Refresh()
        {
            checkedAt = DateTime.UtcNow;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            KippixShared.SafeWork.Queue(_ =>
            {
                try { connected = SteamHid.AnyPathContains("vid_28de"); }
                catch (Exception) { connected = false; }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }
    }

    internal static class SteamHid
    {
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);

        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        /// <summary>True if any present HID device's path contains <paramref name="part"/> (vendor ids are in the path, e.g. vid_28de).</summary>
        public static bool AnyPathContains(string part)
        {
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12);   // PRESENT | DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;
            IntPtr buf = Marshal.AllocHGlobal(2048);
            try
            {
                for (uint i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data)) break;
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    uint req;
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, 2048, out req, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    if (path != null && path.ToLowerInvariant().Contains(part)) return true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                SetupDiDestroyDeviceInfoList(set);
            }
            return false;
        }
    }
}

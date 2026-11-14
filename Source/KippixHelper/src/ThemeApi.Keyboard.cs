using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// The theme's own keyboard for typing anything (a name, a note, a Store search…), the same layout as Search.
    /// It slides up over the bottom of the screen, so what's being searched stays visible above it, and the letters
    /// show as they're typed (a Store search updates the games as you type). A real keyboard types into it too.
    /// B (or Esc) cancels, X deletes, Y is a space, Start or Done finishes.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<SearchKeyRow> KbKeyRows { get; private set; }
        private readonly StringBuilder kbText = new StringBuilder();
        private bool kbOpen, kbShift;
        private bool kbFocusTaken;
        private string kbTitle = "";
        private int kbMax = 200;
        private Action<string> kbDone, kbLive;
        private Action kbCancel;
        private IInputElement kbReturnFocus;
        private DispatcherTimer kbLiveTimer;
        private bool kbLiveNow;   // live text goes straight where it's shown (notes), not a moment later (a search)

        public bool IsKeyboardOpen { get { return kbOpen; } }
        public string KeyboardTitle { get { return kbTitle; } }
        public string KeyboardText { get { return kbText.ToString(); } }
        public bool KeyboardHasText { get { return kbText.Length > 0; } }

        private void InitKeyboard()
        {
            KbKeyRows = new ObservableCollection<SearchKeyRow>();
            BuildKbKeys();
        }

        /// <summary>The Search keyboard's layout (the same rows and key sizes everywhere), with Shift for capitals.</summary>
        internal const string KbRow4 = "zxcvbnm-:.";
        internal static string KbShifted(char c)
        {
            switch (c)
            {
                case '1': return "!";
                case '\'': return "?";
                case '-': return "&";
                case ':': return ",";
                case '.': return "@";
                default: return c.ToString().ToUpperInvariant();
            }
        }

        private void BuildKbKeys()
        {
            KbKeyRows.Clear();
            foreach (string row in new[] { "1234567890", "qwertyuiop", "asdfghjkl'", KbRow4 })
            {
                var r = new SearchKeyRow();
                foreach (char c in row)
                {
                    string ch = kbShift ? KbShifted(c) : c.ToString();
                    r.Keys.Add(MakeKbKey(ch, ch));
                }
                KbKeyRows.Add(r);
            }
            var last = new SearchKeyRow();
            last.Keys.Add(MakeKbKey(kbShift ? Loc("LOC_KX_KeyLower", "abc") : Loc("LOC_KX_KeyShift", "Shift"), "shift", true));
            last.Keys.Add(MakeKbKey(Loc("LOC_KX_KeySpace", "Space"), "space", true));
            last.Keys.Add(MakeKbKey(Loc("LOC_KX_KeyBack", "Delete"), "back", true));
            last.Keys.Add(MakeKbKey(Loc("LOC_KX_KeyClear", "Clear"), "clear", true));
            last.Keys.Add(MakeKbKey(Loc("LOC_KX_KeyFinish", "Done"), "done", true));
            KbKeyRows.Add(last);
        }

        private SearchKey MakeKbKey(string label, string key, bool wide = false)
        {
            var k = new SearchKey { Label = label, Key = key, IsWide = wide };   // letters show as they'll be typed (small, or capitals with Shift)
            k.Command = new RelayCommand(_ => PressKbKey(key));
            return k;
        }

        /// <summary>
        /// Opens the keyboard. done gets the text (Done / Start / Enter); live (optional) gets it as it's typed, a moment after
        /// each change; cancel (optional) runs on B. The highlight comes back to where it was afterwards.
        /// </summary>
        internal void OpenKeyboard(string title, string initial, Action<string> done, Action<string> live = null, Action cancel = null, int max = 200)
        {
            if (KbKeyRows == null) InitKeyboard();
            if (kbOpen) CloseKeyboard(false);
            kbTitle = title ?? "";
            kbText.Clear();
            if (!string.IsNullOrEmpty(initial)) kbText.Append(initial);
            kbMax = max;
            kbDone = done;
            kbLive = live;
            kbCancel = cancel;
            kbShift = false;
            kbLiveNow = false;
            BuildKbKeys();
            kbReturnFocus = Keyboard.FocusedElement;
            kbOpen = true;
            HookSearchTyping();
            PlaySound("select");
            NotifyKeyboard();
            ui.BeginInvoke(new Action(() => FocusKbKey(KbKeyRows.Count > 1 ? KbKeyRows[1].Keys.FirstOrDefault() : null)), DispatcherPriority.ContextIdle);
        }

        private void NotifyKeyboard()
        {
            foreach (string n in new[] { "IsKeyboardOpen", "KeyboardTitle", "KeyboardText", "KeyboardHasText", "KbKeyRows" }) Notify(n);
        }

        private void FocusKbKey(SearchKey k)
        {
            if (k == null) { UiTools.FocusInside("KX_KbOverlay"); return; }
            FocusKeyIn("KX_KbKeys", KbKeyRows, k);
        }

        /// <summary>Focuses a key on one of the keyboards (the Search page's or this one).</summary>
        private static void FocusKeyIn(string listName, ObservableCollection<SearchKeyRow> rowsData, SearchKey k)
        {
            var rows = UiTools.FindByName(Application.Current.MainWindow, listName) as ItemsControl;
            if (rows == null) return;
            foreach (var r in rowsData)
            {
                if (!r.Keys.Contains(k)) continue;
                var rc = rows.ItemContainerGenerator.ContainerFromItem(r) as FrameworkElement;
                var keys = rc != null ? UiTools.FindChild<ItemsControl>(rc) : null;
                var c = keys != null ? keys.ItemContainerGenerator.ContainerFromItem(k) as DependencyObject : null;
                var b = c != null ? UiTools.FindChild<ButtonBase>(c) as UIElement : null;
                if (b != null) Keyboard.Focus(b);
            }
        }

        private void PressKbKey(string key)
        {
            if (!kbOpen) return;
            switch (key)
            {
                case "space": if (kbText.Length > 0 && kbText[kbText.Length - 1] != ' ' && kbText.Length < kbMax) kbText.Append(' '); break;
                case "back": if (kbText.Length > 0) kbText.Length--; else { PlaySound("back"); return; } break;
                case "clear": kbText.Clear(); break;
                case "done": FinishKeyboard(); return;
                case "shift":
                    {
                        kbShift = !kbShift;
                        int ri = -1, ki = -1;
                        var fe = Keyboard.FocusedElement as FrameworkElement;
                        var cur = fe != null ? fe.DataContext as SearchKey : null;
                        for (int i = 0; i < KbKeyRows.Count && ri < 0; i++) { ki = cur != null ? KbKeyRows[i].Keys.IndexOf(cur) : -1; if (ki >= 0) ri = i; }
                        BuildKbKeys();
                        PlaySound("move");
                        if (ri >= 0) { var again = KbKeyRows[ri].Keys[ki]; ui.BeginInvoke(new Action(() => FocusKbKey(again)), DispatcherPriority.ContextIdle); }
                        return;
                    }
                default: if (kbText.Length < kbMax) kbText.Append(key); break;
            }
            PlaySound("move");
            KbChanged();
        }

        private void KbChanged()
        {
            Notify("KeyboardText");
            Notify("KeyboardHasText");
            if (kbLive == null) return;
            if (kbLiveNow) { kbLive(kbText.ToString()); return; }   // shown where it goes, letter by letter
            if (kbLiveTimer == null)
            {
                kbLiveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(450) };
                kbLiveTimer.Tick += (s, e) => { kbLiveTimer.Stop(); if (kbOpen && kbLive != null) kbLive(kbText.ToString()); };
            }
            kbLiveTimer.Stop();
            kbLiveTimer.Start();
        }

        /// <summary>Typed on a real keyboard (from the shared TextInput hook).</summary>
        private void KbTyped(string text)
        {
            foreach (char c in text)
            {
                if (char.IsControl(c)) continue;
                if (c == ' ') { if (kbText.Length > 0 && kbText[kbText.Length - 1] != ' ') kbText.Append(' '); }
                else if (kbText.Length < kbMax) kbText.Append(c);
            }
            KbChanged();
        }

        private void FinishKeyboard()
        {
            var done = kbDone;
            string text = kbText.ToString().Trim();
            CloseKeyboard(true);
            PlaySound("select");
            if (done != null) done(text);
        }

        private void CancelKeyboard()
        {
            // letters typed in the last moment haven't been shown yet: show them first (B leaves what you typed on screen)
            if (kbLive != null && kbLiveTimer != null && kbLiveTimer.IsEnabled) { kbLiveTimer.Stop(); kbLive(kbText.ToString()); }
            var cancel = kbCancel;
            CloseKeyboard(true);
            PlaySound("back");
            if (cancel != null) cancel();
        }

        private void CloseKeyboard(bool restoreFocus)
        {
            if (!kbOpen) return;
            kbOpen = false;
            if (kbLiveTimer != null) kbLiveTimer.Stop();
            kbDone = kbLive = null;
            kbCancel = null;
            NotifyKeyboard();
            var back = kbReturnFocus as UIElement;
            kbReturnFocus = null;
            kbFocusTaken = false;   // what Done / B does next may put the highlight somewhere itself (then it isn't put back)
            if (restoreFocus)
                ui.BeginInvoke(new Action(() =>
                {
                    if (kbFocusTaken) { kbFocusTaken = false; return; }
                    if (back != null && back.IsVisible && back.Focusable) { Keyboard.Focus(back); return; }
                    // what had the highlight has gone (the page changed while typing): the panel's first thing, or the games
                    if (openPanel != null) FocusPanelSoon(); else if (detailsOpen) UiTools.FocusByNameIfVisible("PART_ButtonContext"); else FocusSelectedCard();
                }), DispatcherPriority.ContextIdle);
        }

        /// <summary>
        /// Every key while the keyboard is up (it takes them all): B / Esc cancel, X / Backspace delete, Y / Space a space,
        /// Start / Enter finish, Left / Right wrap round the ends of a row.
        /// </summary>
        private bool KeyboardKey(KeyEventArgs e)
        {
            if (!kbOpen) return false;
            bool pad = e.GetType().Name == "GameControllerInputEventArgs";
            if (pad && IsPadRelease(e)) { e.Handled = true; return true; }
            var button = PadButton(e);
            if (IsBackKey(e) && !(e.Key == Key.Back && !pad)) { CancelKeyboard(); e.Handled = true; return true; }
            if (!pad && e.Key == Key.Back) { PressKbKey("back"); e.Handled = true; return true; }
            if (!pad && e.Key == Key.Space) { PressKbKey("space"); e.Handled = true; return true; }
            if (!pad && e.Key == Key.Enter) { FinishKeyboard(); e.Handled = true; return true; }
            if (pad && button == Playnite.SDK.Events.ControllerInput.X) { PressKbKey("back"); e.Handled = true; return true; }
            if (pad && button == Playnite.SDK.Events.ControllerInput.Y) { PressKbKey("space"); e.Handled = true; return true; }
            if (pad && button == Playnite.SDK.Events.ControllerInput.Start) { FinishKeyboard(); e.Handled = true; return true; }
            if (e.Key == Key.Left || e.Key == Key.Right)
            {
                if (WrapKeyRow(KbKeyRows, "KX_KbKeys", e.Key == Key.Right)) { e.Handled = true; return true; }
                return false;   // along the row as usual
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // stay on the keyboard
                var overlay = UiTools.FindByName(Application.Current.MainWindow, "KX_KbOverlay");
                var f = Keyboard.FocusedElement as UIElement;
                var next = f != null ? f.PredictFocus(e.Key == Key.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down) as DependencyObject : null;
                if (overlay == null || next == null || !overlay.IsAncestorOf(next)) { e.Handled = true; return true; }
                return false;
            }
            return false;
        }

        /// <summary>Left on a row's first key goes to its last, Right on the last to the first. True if it wrapped.</summary>
        private bool WrapKeyRow(ObservableCollection<SearchKeyRow> rowsData, string listName, bool right)
        {
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var k = fe != null ? fe.DataContext as SearchKey : null;
            if (k == null || rowsData == null) return false;
            var row = rowsData.FirstOrDefault(r => r.Keys.Contains(k));
            if (row == null || row.Keys.Count < 2) return false;
            int i = row.Keys.IndexOf(k);
            if (right && i == row.Keys.Count - 1) { FocusKeyIn(listName, rowsData, row.Keys[0]); PlaySound("move"); return true; }
            if (!right && i == 0) { FocusKeyIn(listName, rowsData, row.Keys[row.Keys.Count - 1]); PlaySound("move"); return true; }
            return false;
        }
    }
}

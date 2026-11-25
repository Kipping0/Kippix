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

        // (1.0.7) the half keyboard: Keyboard search's Console design (Settings › Layout changes WIP › Keyboard search, 2 of 2) for
        // every other box too (the Store's search, the settings' search, names…). ‹ › move the caret, &#? the symbols.
        private bool kbV2, kbSymbols;
        private int kbCaret;
        public bool KbDesign2 { get { return kbV2; } }
        public bool KbDesignDefault { get { return !kbV2; } }
        private bool KbIsSearch { get { return kbTitle.StartsWith(Loc("LOC_KX_Search", "Search"), StringComparison.CurrentCultureIgnoreCase); } }
        public string KbStartHint { get { return KbIsSearch ? Loc("LOC_KX_Search", "Search") : Loc("LOC_KX_KeyDoneV2", "Done"); } }
        private string KbKeysName { get { return "KX_KbKeys2"; } }   // (the old full keyboard was taken out)
        private string KbOverlayName { get { return "KX_KbOverlay2"; } }
        public string KeyboardTextBefore { get { int c = Math.Min(kbCaret, kbText.Length); return kbText.ToString(0, c); } }
        public string KeyboardTextAfter { get { int c = Math.Min(kbCaret, kbText.Length); return kbText.ToString(c, kbText.Length - c); } }

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
            if (kbV2) { BuildKbKeysV2(); return; }
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

        /// <summary>(1.0.7) The half keyboard's keys: the Console search keyboard's, with Shift (capitals) in the corner.</summary>
        private void BuildKbKeysV2()
        {
            string[] rows = kbSymbols ? new[] { "1234567890", "!@#$%^&*()", "~+=_[]{};'", "\"<>?/|:,.-" } : new[] { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,.-" };
            bool search = KbIsSearch;
            string[][] side =
            {
                new[] { "", "back", "back" },
                new[] { Loc("LOC_KX_KeyClear", "Clear"), "clear", "action" },
                new[] { kbSymbols ? "abc" : "&#?", "sym", "action" },
                new[] { search ? Loc("LOC_KX_Search", "Search") : Loc("LOC_KX_KeyOK", "OK"), "done", search ? "accent" : "accentplain" },
            };
            for (int i = 0; i < 4; i++)
            {
                var r = new SearchKeyRow();
                foreach (char c in rows[i])
                {
                    string ch = kbShift && !kbSymbols ? KbShifted(c) : c.ToString();
                    if (kbShift && !kbSymbols && !char.IsLetter(c)) ch = c.ToString();   // (only the letters change: the symbols have their own keys)
                    r.Keys.Add(MakeKbKeyV2(ch, ch, "char", KeyW));
                }
                r.Keys.Add(MakeKbKeyV2(side[i][0], side[i][1], side[i][2], KeyW * 2 + KeyGap));
                KbKeyRows.Add(r);
            }
            var last = new SearchKeyRow();
            last.Keys.Add(MakeKbKeyV2(kbShift ? Loc("LOC_KX_KeyLower", "abc") : Loc("LOC_KX_KeyShift", "Shift"), "shift", "action", KeyW * 2 + KeyGap));
            last.Keys.Add(MakeKbKeyV2(Loc("LOC_KX_KeySpace", "Space"), "space", "char", KeyW * 6 + KeyGap * 5));
            last.Keys.Add(MakeKbKeyV2("\u2039", "left", "char", KeyW));
            last.Keys.Add(MakeKbKeyV2("\u203A", "right", "char", KeyW));
            last.Keys.Add(MakeKbKeyV2(Loc("LOC_KX_KeyDoneV2", "Done"), "done", "action", KeyW * 2 + KeyGap));
            KbKeyRows.Add(last);
        }

        private SearchKey MakeKbKeyV2(string label, string key, string kind, double width)
        {
            bool plain = kind == "accentplain";
            var k = new SearchKey { Label = label, Key = key, Kind = plain ? "accent" : kind, Width = width, IsWide = width > KeyW, NoIcon = plain };
            k.Command = new RelayCommand(_ => PressKbKey(key));
            return k;
        }

        /// <summary>Typed text goes in where the caret is.</summary>
        private void KbInsert(string s)
        {
            if (kbText.Length + s.Length > kbMax) return;
            int c = Math.Min(kbCaret, kbText.Length);
            kbText.Insert(c, s);
            kbCaret = c + s.Length;
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
            kbV2 = SearchV2;
            kbSymbols = false;
            kbCaret = kbText.Length;
            BuildKbKeys();
            kbReturnFocus = Keyboard.FocusedElement;
            kbOpen = true;
            HookSearchTyping();
            PlaySound("select");
            NotifyKeyboard();
            int first = kbV2 ? 2 : 1;   // Q (the half keyboard: A)
            ui.BeginInvoke(new Action(() => FocusKbKey(KbKeyRows.Count > first ? KbKeyRows[first].Keys.FirstOrDefault() : null)), DispatcherPriority.ContextIdle);
        }

        private void NotifyKeyboard()
        {
            foreach (string n in new[] { "IsKeyboardOpen", "KeyboardTitle", "KeyboardText", "KeyboardHasText", "KbKeyRows", "KbDesign2", "KbDesignDefault", "KbStartHint", "KeyboardTextBefore", "KeyboardTextAfter" }) Notify(n);
        }

        private void FocusKbKey(SearchKey k)
        {
            if (k == null) { UiTools.FocusInside(KbOverlayName); return; }
            FocusKeyIn(KbKeysName, KbKeyRows, k);
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
                case "space": { int at = Math.Min(kbCaret, kbText.Length); if (at > 0 && kbText[at - 1] != ' ') KbInsert(" "); } break;
                case "back":
                    {
                        int at = Math.Min(kbCaret, kbText.Length);
                        if (at > 0) { kbText.Remove(at - 1, 1); kbCaret = at - 1; } else { PlaySound("back"); return; }
                    }
                    break;
                case "clear": kbText.Clear(); kbCaret = 0; break;
                case "done": FinishKeyboard(); return;
                case "left": if (kbCaret > 0) kbCaret--; PlaySound("move"); NotifyKbCaret(); return;
                case "right": if (kbCaret < kbText.Length) kbCaret++; PlaySound("move"); NotifyKbCaret(); return;
                case "sym": kbSymbols = !kbSymbols; RebuildKbKeysKeepFocus(); return;
                case "shift":
                    {
                        kbShift = !kbShift;
                        RebuildKbKeysKeepFocus();
                        return;
                    }
                default: KbInsert(key); break;
            }
            PlaySound("move");
            KbChanged();
        }

        /// <summary>Shift or symbols: the keys change, the highlight stays on the same spot.</summary>
        private void RebuildKbKeysKeepFocus()
        {
            int ri = -1, ki = -1;
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var cur = fe != null ? fe.DataContext as SearchKey : null;
            for (int i = 0; i < KbKeyRows.Count && ri < 0; i++) { ki = cur != null ? KbKeyRows[i].Keys.IndexOf(cur) : -1; if (ki >= 0) ri = i; }
            BuildKbKeys();
            PlaySound("move");
            if (ri >= 0 && ri < KbKeyRows.Count && ki < KbKeyRows[ri].Keys.Count) { var again = KbKeyRows[ri].Keys[ki]; ui.BeginInvoke(new Action(() => FocusKbKey(again)), DispatcherPriority.ContextIdle); }
        }

        private void NotifyKbCaret() { Notify("KeyboardTextBefore"); Notify("KeyboardTextAfter"); }

        private void KbChanged()
        {
            Notify("KeyboardText");
            Notify("KeyboardHasText");
            NotifyKbCaret();
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
                if (c == ' ') { int at = Math.Min(kbCaret, kbText.Length); if (at > 0 && kbText[at - 1] != ' ') KbInsert(" "); }
                else KbInsert(c.ToString());
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
                if (WrapKeyRow(KbKeyRows, KbKeysName, e.Key == Key.Right)) { e.Handled = true; return true; }
                return false;   // along the row as usual
            }
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // stay on the keyboard
                var overlay = UiTools.FindByName(Application.Current.MainWindow, KbOverlayName);
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

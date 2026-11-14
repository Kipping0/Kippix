using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Playnite's own message boxes in fullscreen (for example Ludusavi's "Back up save data for …?" after playing).
    /// Playnite shows them as a separate window with ShowDialog. When that happens while a game (or the overlay) is in
    /// front, the box never becomes the active window: once Playnite comes back, the home screen behind it has the
    /// keyboard / controller and the box can't be answered. So while a box is up:
    /// - as soon as Playnite is in front again, the box is made the active window and its default button highlighted;
    /// - B / Esc picks its "No" / "Cancel" (for Ludusavi: "No", which doesn't remember the answer); A presses the highlighted one;
    /// - A / B hints are added under its buttons;
    /// - the "You just played" card and our other cards wait until it's gone;
    /// - when it closes, the highlight goes back where it was on the screen behind.
    /// </summary>
    public partial class ThemeApi
    {
        private bool dialogFocusHooked;
        /// <summary>Playnite message boxes showing right now (the after-game cards wait for them).</summary>
        private int playniteDialogsOpen;
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> watchedDialogs = new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        private void InitDialogFocus()
        {
            if (dialogFocusHooked) return;
            dialogFocusHooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnAnyWindowLoaded), true);
        }

        private static bool IsPlayniteMessageBox(Window w)
        {
            return w != null && w.GetType().FullName == "Playnite.FullscreenApp.Windows.MessageBoxWindow";
        }

        private void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var w = sender as Window;
                object seen;
                if (!IsPlayniteMessageBox(w) || !lookFullscreen || Application.Current == null) return;
                var main = Application.Current.MainWindow;
                if (main == null || ReferenceEquals(w, main) || watchedDialogs.TryGetValue(w, out seen)) return;
                watchedDialogs.Add(w, new object());
                WatchDialog(w, main);
            }
            catch (Exception ex) { logger.Warn("Kippix: message box: " + ex.Message); }
        }

        private void WatchDialog(Window w, Window main)
        {
            // where the highlight was on the screen behind
            IInputElement saved = null;
            try
            {
                saved = FocusManager.GetFocusedElement(main);
                var kf = Keyboard.FocusedElement as DependencyObject;
                if (kf != null && ReferenceEquals(Window.GetWindow(kf), main) && !(kf is Window)) saved = (IInputElement)kf;
                if (saved is Window) saved = null;
            }
            catch (Exception) { }
            var savedRef = saved != null ? new WeakReference(saved) : null;

            playniteDialogsOpen++;
            AddDialogHints(w);

            w.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
            {
                try
                {
                    if (e.Handled) return;
                    if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
                    {
                        var no = DialogNoButton(w);
                        if (no != null) { e.Handled = true; no.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, no)); }
                    }
                }
                catch (Exception) { }
            }), true);
            // the controller's B arrives as KeyDown at the highlighted button (no Preview) and bubbles up here
            w.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((s, e) =>
            {
                try
                {
                    if (e.Handled || PadButton(e) != Playnite.SDK.Events.ControllerInput.B) return;
                    var no = DialogNoButton(w);
                    if (no != null) { e.Handled = true; no.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, no)); }
                }
                catch (Exception) { }
            }), true);

            // take the keyboard / controller once Playnite is in front again (it may have come up behind a game)
            var wait = new DispatcherTimer(DispatcherPriority.Input, w.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            bool logged = false;
            wait.Tick += (s, e) =>
            {
                try
                {
                    if (!w.IsVisible) return;
                    if (w.IsActive && FocusInside(w)) return;
                    if (!PlayniteInFront(w, main)) return;
                    if (!w.IsActive) w.Activate();
                    FocusDialogButton(w);
                    if (!logged) { logged = true; logger.Info("Kippix: a message box (\"" + DialogText(w) + "\") took the highlight"); }
                }
                catch (Exception) { }
            };
            wait.Start();
            w.Dispatcher.BeginInvoke(new Action(() => { try { if (PlayniteInFront(w, main)) { if (!w.IsActive) w.Activate(); FocusDialogButton(w); } } catch (Exception) { } }), DispatcherPriority.Input);

            w.Closed += (s, e) =>
            {
                wait.Stop();
                if (playniteDialogsOpen > 0) playniteDialogsOpen--;
                Action back = () =>
                {
                    try
                    {
                        if (playniteDialogsOpen > 0) return;   // another box is up
                        var el = savedRef != null ? savedRef.Target as UIElement : null;
                        if (el == null || !el.IsVisible || !el.Focusable || !ReferenceEquals(Window.GetWindow(el), main)) return;
                        var now = Keyboard.FocusedElement as DependencyObject;
                        if (ReferenceEquals(now, el)) return;
                        var nowWin = now != null ? Window.GetWindow(now) : null;
                        if (nowWin != null && !ReferenceEquals(nowWin, main) && nowWin.IsVisible) return;   // another of Playnite's windows has it
                        if (!main.IsActive && PlayniteInFront(null, main)) main.Activate();
                        Keyboard.Focus(el);
                    }
                    catch (Exception) { }
                };
                // r15: a box that came up behind a game hands the activation back to the window that was active when it
                // opened (often one that's gone by now): Playnite is in front but its main window isn't active, so the
                // controller and the "You just played" card waited. Then the main window is made active again (before
                // the highlight goes back, which needs it active).
                main.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        string why;
                        if (playniteDialogsOpen > 0 || main.IsActive || !main.IsVisible || !PlayniteFrontForCard(main, out why)) return;
                        IntPtr fg = DlgForegroundWindow();
                        foreach (Window x in Application.Current.Windows)
                            if (!ReferenceEquals(x, main) && x.IsVisible && new WindowInteropHelper(x).Handle == fg) return;   // another of Playnite's windows is really up
                        main.Activate();
                        logger.Info("Kippix: message box closed: Playnite's window made active again");
                    }
                    catch (Exception) { }
                }), DispatcherPriority.Input);
                main.Dispatcher.BeginInvoke(back, DispatcherPriority.Input);
                main.Dispatcher.BeginInvoke(back, DispatcherPriority.ContextIdle);   // (after Playnite's own "focus something")
            };
        }

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr DlgForegroundWindow();

        /// <summary>The window in front is Playnite's main window, the box itself or the box's owner.</summary>
        private static bool PlayniteInFront(Window box, Window main)
        {
            IntPtr fg = DlgForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            try
            {
                if (main != null && new WindowInteropHelper(main).Handle == fg) return true;
                if (box != null && new WindowInteropHelper(box).Handle == fg) return true;
                if (box != null && box.Owner != null && new WindowInteropHelper(box.Owner).Handle == fg) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static bool FocusInside(Window w)
        {
            var d = Keyboard.FocusedElement as DependencyObject;
            return d != null && !(d is Window) && ReferenceEquals(Window.GetWindow(d), w);
        }

        /// <summary>The box's own highlighted button (the one Playnite chose: Yes / OK / the add-on's default), else its first.</summary>
        private static void FocusDialogButton(Window w)
        {
            if (FocusInside(w)) return;
            var want = FocusManager.GetFocusedElement(w) as UIElement;
            if (want == null || !want.IsVisible || !want.Focusable || want is Window)
            {
                want = null;
                var stack = w.FindName("StackButtons") as Panel;
                if (stack != null)
                    foreach (UIElement c in stack.Children)
                        if (c is ButtonBase && c.IsVisible && c.Focusable) { want = c; break; }
            }
            if (want != null) Keyboard.Focus(want);
        }

        private static string ButtonText(ButtonBase b)
        {
            if (b == null) return "";
            var s = b.Content as string;
            if (s != null) return s.Trim();
            var tb = b.Content as TextBlock;
            return tb != null && tb.Text != null ? tb.Text.Trim() : "";
        }

        /// <summary>What B picks: Cancel, else No, else the only OK. Ludusavi's own boxes: its "No" (not "Never").</summary>
        private static ButtonBase DialogNoButton(Window w)
        {
            foreach (string name in new[] { "ButtonCancel", "ButtonNo" })
            {
                var b = w.FindName(name) as ButtonBase;
                if (b != null && b.IsVisible) return b;
            }
            var stack = w.FindName("StackButtons") as Panel;
            if (stack == null) return null;
            string noText = w.TryFindResource("LOCNoLabel") as string;
            string cancelText = w.TryFindResource("LOCCancelLabel") as string;
            ButtonBase only = null;
            int shown = 0;
            foreach (UIElement c in stack.Children)
            {
                var b = c as ButtonBase;
                if (b == null || !b.IsVisible) continue;
                shown++;
                only = b;
                string t = ButtonText(b);
                if (t.Length == 0) continue;
                if (string.Equals(t, cancelText, StringComparison.CurrentCultureIgnoreCase) || string.Equals(t, noText, StringComparison.CurrentCultureIgnoreCase)
                    || string.Equals(t, "No", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Cancel", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "Not now", StringComparison.OrdinalIgnoreCase))
                    return b;
            }
            var ok = w.FindName("ButtonOK") as ButtonBase;
            if (ok != null && ok.IsVisible && shown == 1) return ok;   // a plain notice: B closes it
            return null;
        }

        private static string DialogText(Window w)
        {
            try
            {
                var p = w.GetType().GetProperty("Text");
                var s = p != null ? p.GetValue(w, null) as string : null;
                if (s == null) return "";
                s = s.Replace("\r", " ").Replace("\n", " ");
                return s.Length > 80 ? s.Substring(0, 80) + "\u2026" : s;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>"(A) Choose   (B) No" under the box's buttons.</summary>
        private void AddDialogHints(Window w)
        {
            try
            {
                var stack = w.FindName("StackButtons") as FrameworkElement;
                var parent = stack != null ? stack.Parent as Panel : null;
                if (parent == null) return;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 22, 0, 0), Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Left, Tag = "bb-hints" };
                foreach (UIElement c in parent.Children) { var fe = c as FrameworkElement; if (fe != null && fe.Tag as string == "bb-hints") return; }
                var dim = w.TryFindResource("KX_TextDim") as Style;
                Action<string, string> add = (prompt, text) =>
                {
                    var t = w.TryFindResource(prompt) as DataTemplate;
                    if (t != null) row.Children.Add(new ContentControl { ContentTemplate = t, Width = 26, Height = 26, Focusable = false, IsTabStop = false });
                    var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 28, 0), FontSize = 20 };
                    if (dim != null) tb.Style = dim; else tb.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
                    row.Children.Add(tb);
                };
                add("ButtonPromptA", Loc("LOC_KX_DlgChoose", "Choose"));
                var no = DialogNoButton(w);
                if (no != null)
                {
                    string t = ButtonText(no);
                    add("ButtonPromptB", t.Length > 0 ? t : Loc("LOC_KX_Back", "Back"));
                }
                int at = parent.Children.IndexOf(stack);
                parent.Children.Insert(at + 1, row);
            }
            catch (Exception) { }
        }
    }
}

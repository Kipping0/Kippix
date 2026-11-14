using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// Touch and the mouse, with one set of handlers on the main window, turned into the same presses as the controller:
    ///   tap = select (a game card: the first tap picks it, a second opens it; a row or button: pressed at once),
    ///   swipe left / right = Left / Right (rows, the carousel, tabs), swipe up / down = scroll the list under the finger
    ///   (or Up / Down where nothing scrolls), swipe in from the left edge = B (back), long press = Game options on a card,
    ///   X elsewhere; the mouse wheel steps through the home row; right-click = B.
    /// Playnite's "Hide mouse cursor" turns the window's mouse input off (no touch either): with a touch screen (Automatic or On)
    /// it's turned back on for the window, the cursor stays hidden. Not used in the browser (it has its own touch) or while
    /// something is being placed with the mouse (Free edit's own drag).
    /// </summary>
    public partial class ThemeApi
    {
        private KeyEventArgs touchKey;   // a key sent here (swipes, the wheel): SteamCopyFilter leaves it alone
        private TouchDevice touchDev;
        private Point touchStart;
        private DateTime touchStartAt;
        private bool touchMoved, touchPanning, touchLong, touchEdge;
        private DependencyObject touchHit;
        private ScrollViewer touchScroller;
        private double touchScrollX0, touchScrollY0;
        private DispatcherTimer touchHoldTimer;
        private static bool? hasTouchScreen;

        private void InitTouch()
        {
            HandheldPausesLive = () => batterySaving;   // (r12 G: live backgrounds and animated art, ThemeApi.LiveBackground.cs, pause while saving battery / safe mode)
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDownG), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnTouchMoveG), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUpG), true);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheelG), false);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUpG), false);
        }

        /// <summary>Is there a touch screen on this PC?</summary>
        internal static bool HasTouchScreen
        {
            get
            {
                if (hasTouchScreen.HasValue) return hasTouchScreen.Value;
                bool any = false;
                try { foreach (TabletDevice d in Tablet.TabletDevices) if (d.Type == TabletDeviceType.Touch) { any = true; break; } } catch (Exception) { }
                hasTouchScreen = any;
                return any;
            }
        }

        /// <summary>Touch is used: On, or Automatic with a touch screen.</summary>
        private bool TouchOn { get { return settings.TouchMode == 1 || (settings.TouchMode == 0 && HasTouchScreen); } }

        private bool touchHitHooked, touchHitLogged;

        /// <summary>From the tick: a touch screen keeps the window's touch / mouse input on (Playnite's hidden cursor turns it off).</summary>
        private void EnsureTouchInput()
        {
            if (!TouchOn || !HasTouchScreen || !lookFullscreen || openPanel == "browser") return;
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            if (w == null) return;
            if (!touchHitHooked)
            {
                touchHitHooked = true;
                w.IsHitTestVisibleChanged += (s, e) => { if (ui != null) ui.BeginInvoke(new Action(EnsureTouchInput), DispatcherPriority.Background); };
            }
            if (w.IsHitTestVisible) return;
            w.IsHitTestVisible = true;
            if (!touchHitLogged) { touchHitLogged = true; logger.Info("Kippix: touch screen: the window's touch input is on (Playnite's hidden mouse cursor had turned it off)"); }
        }

        private bool TouchAllowedNow(object sender)
        {
            return TouchOn && lookFullscreen && IsMainWindow(sender) && openPanel != "browser" && !freeMove && !IsMasterMoving && widgetMoving == null && !gameRunningNow;
        }

        // ---------------- touch ----------------

        private double TouchScale(Window w)
        {
            // Main sits in a Viewbox 1080 high: distances are measured in the theme's own (1080p) units
            double h = w != null && w.ActualHeight > 0 ? w.ActualHeight : 1080;
            return h / DesignHeight;   // (0.99.58: the theme's height, 1080 on 16:9, more on a 16:10 screen)
        }

        private void OnTouchDownG(object sender, TouchEventArgs e)
        {
            try
            {
                if (!TouchAllowedNow(sender)) return;
                var w = (Window)sender;
                e.Handled = true;   // (no mouse made from it: everything a finger does is decided here)
                lastInput = DateTime.Now;
                if (ambientOn) { StopAmbient(); return; }
                if (touchDev != null) return;   // one finger at a time
                TipsAnyKey();
                touchDev = e.TouchDevice;
                touchStart = e.GetTouchPoint(w).Position;
                touchStartAt = DateTime.Now;
                touchMoved = touchPanning = touchLong = false;
                touchEdge = touchStart.X < 28 * TouchScale(w);
                touchHit = w.InputHitTest(touchStart) as DependencyObject;
                touchScroller = FindScroller(touchHit);
                touchScrollX0 = touchScroller != null ? touchScroller.HorizontalOffset : 0;
                touchScrollY0 = touchScroller != null ? touchScroller.VerticalOffset : 0;
                try { e.TouchDevice.Capture(w); } catch (Exception) { }
                if (touchHoldTimer == null)
                {
                    touchHoldTimer = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(550) };
                    touchHoldTimer.Tick += (s, a) =>
                    {
                        touchHoldTimer.Stop();
                        if (touchDev == null || touchMoved) return;
                        touchLong = true;
                        TouchLongPress(touchHit);
                    };
                }
                touchHoldTimer.Stop();
                touchHoldTimer.Start();
            }
            catch (Exception ex) { logger.Warn("Kippix: touch: " + ex.Message); }
        }

        private void OnTouchMoveG(object sender, TouchEventArgs e)
        {
            try
            {
                if (touchDev == null || !ReferenceEquals(e.TouchDevice, touchDev)) return;
                var w = sender as Window;
                if (w == null) return;
                e.Handled = true;
                double k = TouchScale(w);
                var p = e.GetTouchPoint(w).Position;
                double dx = p.X - touchStart.X, dy = p.Y - touchStart.Y;
                if (!touchMoved && Math.Sqrt(dx * dx + dy * dy) > 16 * k) { touchMoved = true; if (touchHoldTimer != null) touchHoldTimer.Stop(); }
                if (!touchMoved || touchEdge || touchScroller == null) return;
                // the list under the finger follows it
                if (Math.Abs(dy) >= Math.Abs(dx) && touchScroller.ScrollableHeight > 0)
                {
                    touchPanning = true;
                    touchScroller.ScrollToVerticalOffset(Math.Max(0, Math.Min(touchScroller.ScrollableHeight, touchScrollY0 - dy / k)));
                }
                else if (Math.Abs(dx) > Math.Abs(dy) && touchScroller.ScrollableWidth > 0)
                {
                    touchPanning = true;
                    touchScroller.ScrollToHorizontalOffset(Math.Max(0, Math.Min(touchScroller.ScrollableWidth, touchScrollX0 - dx / k)));
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: touch: " + ex.Message); }
        }

        private void OnTouchUpG(object sender, TouchEventArgs e)
        {
            if (touchDev == null || !ReferenceEquals(e.TouchDevice, touchDev)) return;
            try
            {
                var w = sender as Window;
                e.Handled = true;
                try { e.TouchDevice.Capture(null); } catch (Exception) { }
                if (touchHoldTimer != null) touchHoldTimer.Stop();
                if (w == null || touchLong) return;   // (a long press already did its thing)
                double k = TouchScale(w);
                var p = e.GetTouchPoint(w).Position;
                double dx = p.X - touchStart.X, dy = p.Y - touchStart.Y;
                if (!touchMoved) { TouchTap(touchHit); return; }
                if (touchPanning) return;
                double ax = Math.Abs(dx), ay = Math.Abs(dy);
                if (touchEdge && dx > 90 * k && ax > 2 * ay) { PadPress(ControllerInput.B); return; }   // in from the left edge: back
                if (ax > ay && ax > 50 * k)
                {
                    int steps = Math.Max(1, Math.Min(6, (int)Math.Round(ax / (220 * k))));
                    for (int i = 0; i < steps; i++) SendKeyG(dx < 0 ? Key.Right : Key.Left);
                }
                else if (ay > 50 * k)
                {
                    int steps = Math.Max(1, Math.Min(6, (int)Math.Round(ay / (200 * k))));
                    for (int i = 0; i < steps; i++) SendKeyG(dy < 0 ? Key.Down : Key.Up);
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: touch: " + ex.Message); }
            finally { touchDev = null; touchHit = null; touchScroller = null; }
        }

        private static ScrollViewer FindScroller(DependencyObject d)
        {
            for (var p = d; p != null; p = Parent(p))
            {
                var sv = p as ScrollViewer;
                if (sv != null && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0)) return sv;
            }
            return null;
        }

        private static DependencyObject Parent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            var fce = d as FrameworkContentElement;
            return fce != null ? fce.Parent : null;
        }

        /// <summary>What a finger on <paramref name="hit"/> means: the card, row or button it's on (or null).</summary>
        private static UIElement TouchTarget(DependencyObject hit)
        {
            for (var p = hit; p != null; p = Parent(p))
            {
                if (p is Window) return null;
                if (p is ListBoxItem || p is ButtonBase) return (UIElement)p;
                var c = p as Control;
                if (c != null && c.Focusable && c.IsEnabled && c.IsVisible && !(c is ScrollViewer) && !(c is ItemsControl) && !(c is UserControl) && c.GetType() != typeof(ContentControl)) return c;
            }
            return null;
        }

        private void TouchTap(DependencyObject hit)
        {
            var el = TouchTarget(hit);
            if (el == null || !el.IsVisible || !el.IsEnabled) return;
            bool wasFocused = el.IsKeyboardFocusWithin;
            if (!el.IsKeyboardFocused) Keyboard.Focus(el);
            var li = el as ListBoxItem;
            if (li != null)
            {
                // a game card: the first tap picks it, a tap on the picked one opens it
                if (!li.IsSelected) li.IsSelected = true;
                if (!wasFocused) { PlaySound("move"); return; }
                PadPress(ControllerInput.A);
                return;
            }
            var b = el as ButtonBase;
            if (b != null && !b.GetType().Name.EndsWith("Ex", StringComparison.Ordinal))
            {
                // a plain WPF button (A from a controller doesn't press those): pressed as a click
                try { typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(b, null); } catch (Exception ex) { logger.Warn("Kippix: touch: " + ex.Message); }
                return;
            }
            PadPress(ControllerInput.A);
        }

        private void TouchLongPress(DependencyObject hit)
        {
            var el = TouchTarget(hit);
            if (el == null) return;
            if (!el.IsKeyboardFocused) Keyboard.Focus(el);
            var li = el as ListBoxItem;
            if (li != null && !li.IsSelected) li.IsSelected = true;
            // a game card on the home screen: Game options (X would start the game); anywhere else: X (its settings, wishlist...)
            var list = Application.Current != null ? UiTools.FindByName(Application.Current.MainWindow, "PART_ListGameItems") : null;
            bool card = li != null && list != null && list.IsAncestorOf(li) && openPanel == null;
            PadPress(card ? ControllerInput.Start : ControllerInput.X);
        }

        /// <summary>A controller button pressed and let go (for touch and the mouse): through every handler, like the real one.</summary>
        private void PadPress(ControllerInput button)
        {
            var t = PadEventType();
            if (t == null)
            {
                // no controller seen yet: the keyboard keys that mean the same
                if (button == ControllerInput.B) SendKeyG(Key.Escape);
                else if (button == ControllerInput.A) SendKeyG(Key.Enter);
                return;
            }
            RaisePad(t, button, true, false);
            RaisePad(t, button, false, false);
        }

        /// <summary>A keyboard key pressed and let go (Left / Right / Up / Down from swipes and the wheel).</summary>
        private void SendKeyG(Key key)
        {
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    var src = Keyboard.PrimaryDevice.ActiveSource ?? (Application.Current != null && Application.Current.MainWindow != null ? PresentationSource.FromVisual(Application.Current.MainWindow) : null);
                    if (src == null) return;
                    int stamp = Environment.TickCount;
                    var d = new KeyEventArgs(Keyboard.PrimaryDevice, src, stamp, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    touchKey = d;
                    InputManager.Current.ProcessInput(d);   // (WPF makes its KeyDown from it)
                    var u = new KeyEventArgs(Keyboard.PrimaryDevice, src, stamp, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
                    touchKey = u;
                    InputManager.Current.ProcessInput(u);
                }
                catch (Exception ex) { logger.Warn("Kippix: touch key: " + ex.Message); }
                finally { touchKey = null; }
            }), DispatcherPriority.Input);
        }

        // ---------------- the mouse ----------------

        private void OnWheelG(object sender, MouseWheelEventArgs e)
        {
            try
            {
                if (!settings.MouseWheelRow || e.Handled || !lookFullscreen || !IsMainWindow(sender) || e.StylusDevice != null) return;
                if (openPanel != null || detailsOpen || freeMove || IsMasterMoving || carActive || kbOpen) return;   // (the carousel has its own wheel)
                if (LayoutNow != 0) return;   // the grid and the list scroll as they always have
                var f = Keyboard.FocusedElement as DependencyObject;
                var list = UiTools.FindByName(Application.Current.MainWindow, "PART_ListGameItems");
                if (list == null || f == null || !list.IsAncestorOf(f)) return;
                e.Handled = true;
                int steps = Math.Max(1, Math.Min(5, Math.Abs(e.Delta) / 120));
                for (int i = 0; i < steps; i++) SendKeyG(e.Delta < 0 ? Key.Right : Key.Left);
            }
            catch (Exception ex) { logger.Warn("Kippix: mouse wheel: " + ex.Message); }
        }

        private void OnMouseUpG(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != System.Windows.Input.MouseButton.Right || e.Handled || !settings.MouseRightBack) return;
                if (!lookFullscreen || !IsMainWindow(sender) || e.StylusDevice != null) return;   // (a long touch made into a right-click: not this)
                if (openPanel == "browser" || freeMove || IsMasterMoving || widgetMoving != null || gameRunningNow) return;   // (Free edit's own right-click)
                e.Handled = true;
                lastInput = DateTime.Now;
                if (ambientOn) { StopAmbient(); return; }
                PadPress(ControllerInput.B);
            }
            catch (Exception ex) { logger.Warn("Kippix: right-click: " + ex.Message); }
        }
    }
}

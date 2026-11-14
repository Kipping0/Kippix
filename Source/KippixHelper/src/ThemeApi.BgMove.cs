using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// Home settings › Background › Move the picture (r15). The home background (KX_BgShift in Main.xaml: around KX_BgLayers,
    /// so the art, its fade layers, your own picture, the live background and the trailer preview all move together, and
    /// Depth / Background zoom / the living picture keep their own transforms inside it) is slid by a TranslateTransform
    /// set here: HomeBgOffsetX / HomeBgOffsetY, a share of the screen (0 centred, -1..1). What it no longer covers shows
    /// PART_MainHost's black. Not over a plain colour.
    /// Positioning: the row hides the settings panel (it stays open, the highlight stays on the row) and takes every key:
    /// D-pad / arrows move, LB / RB (LT / RT, PgUp / PgDn) change the step, Y / R centre it, A / Enter keep, B / Esc cancel.
    /// A dashed cross shows the picture's middle: each line white, yellow when the picture is centred on that axis (moving
    /// across the middle stops there first, so it's easy to land on). A small box shows where it is, the step and the hints.
    /// </summary>
    public partial class ThemeApi
    {
        public ICommand MoveHomeBgCommand { get; private set; }

        private static readonly double[] BgMoveSteps = { 0.001, 0.005, 0.01, 0.025, 0.05, 0.1 };   // a share of the screen
        private static readonly Color BgMoveYellow = Color.FromRgb(0xFF, 0xD4, 0x00);

        private bool bgMoveOn;
        private int bgMoveStep = 2;   // 1 %
        private double bgMoveFromX, bgMoveFromY;
        private DateTime bgMoveStartedAt = DateTime.MinValue, bgMoveEndedAt = DateTime.MinValue;
        private DispatcherTimer bgMoveWatch;
        private FrameworkElement bgMovePanel;
        private double bgMovePanelOpacity = 1;
        private bool bgMovePanelHit = true;

        private Grid bgMoveLayer;
        private Line bgMoveH, bgMoveV, bgMoveHShade, bgMoveVShade;
        private TextBlock bgMoveWhere, bgMoveStepText;
        private ObservableCollection<HintGroup> bgMoveHints;
        private string bgMoveHintsSig;

        private FrameworkElement bgShiftTarget;
        private TranslateTransform bgShift;

        // ---------------- the setting ----------------

        private void NormalizeBgOffset()
        {
            settings.HomeBgOffsetX = CleanOffset(settings.HomeBgOffsetX);
            settings.HomeBgOffsetY = CleanOffset(settings.HomeBgOffsetY);
        }

        private static double CleanOffset(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            v = Math.Round(Math.Max(-1, Math.Min(1, v)), 4);
            return Math.Abs(v) < 0.0005 ? 0 : v;
        }

        /// <summary>The row shows (and the offset applies) for every background but a plain colour.</summary>
        public bool BgMoveShown { get { return settings.HomeBgMode != 1; } }

        /// <summary>"Centred", or "Right 3% · Up 1%".</summary>
        public string HomeBgOffsetText { get { return BgOffsetText(settings.HomeBgOffsetX, settings.HomeBgOffsetY); } }

        private string BgOffsetText(double x, double y)
        {
            if (x == 0 && y == 0) return Loc("LOC_KX_BgMoveCentred", "Centred");
            string t = "";
            if (x != 0) t = string.Format(x > 0 ? Loc("LOC_KX_BgMoveRight", "Right {0}%") : Loc("LOC_KX_BgMoveLeft", "Left {0}%"), Pct(x));
            if (y != 0)
            {
                string s = string.Format(y < 0 ? Loc("LOC_KX_BgMoveUp", "Up {0}%") : Loc("LOC_KX_BgMoveDown", "Down {0}%"), Pct(y));
                t = t.Length > 0 ? t + "  ·  " + s : s;
            }
            return t;
        }

        private static string Pct(double v)
        {
            return Math.Round(Math.Abs(v) * 100, 1).ToString("0.#", CultureInfo.CurrentCulture);
        }

        /// <summary>Puts the offset on the background (none over a plain colour). Cheap: called whenever the background changes.</summary>
        private void ApplyHomeBgOffset()
        {
            if (!IsFullscreen || ui == null) return;
            var target = bgShiftTarget;
            if (target == null || PresentationSource.FromVisual(target) == null)
            {
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                target = win != null ? UiTools.FindByName(win, "KX_BgShift") : null;
                if (target == null) return;
            }
            if (!ReferenceEquals(target, bgShiftTarget))
            {
                bgShiftTarget = target;
                bgShift = null;
                target.SizeChanged += (s, e) => { try { ApplyHomeBgOffset(); if (bgMoveOn) BgMoveDraw(); } catch (Exception) { } };
            }
            double x = BgMoveShown ? settings.HomeBgOffsetX : 0, y = BgMoveShown ? settings.HomeBgOffsetY : 0;
            if (bgShift == null || !ReferenceEquals(target.RenderTransform, bgShift))
            {
                if (x == 0 && y == 0 && bgShift == null) return;   // never moved: nothing to put on it
                bgShift = new TranslateTransform();
                target.RenderTransform = bgShift;
            }
            bgShift.X = Math.Round(x * target.ActualWidth, 1);
            bgShift.Y = Math.Round(y * target.ActualHeight, 1);
        }

        // ---------------- positioning ----------------

        private bool BgMoveJustStarted { get { return (DateTime.Now - bgMoveStartedAt).TotalMilliseconds < 400; } }

        /// <summary>The row: hide the settings, show the cross and the hints, take the keys.</summary>
        private void BgMoveStart()
        {
            if (bgMoveOn || !IsFullscreen || !BgMoveShown) return;
            if ((DateTime.Now - bgMoveEndedAt).TotalMilliseconds < 500) return;   // (the Enter that came with the A that just ended it)
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var host = win != null ? UiTools.FindByName(win, "PART_MainHost") as Panel : null;
            if (host == null) { logger.Info("Kippix: move the picture: the main screen (PART_MainHost) wasn't found"); return; }
            ApplyHomeBgOffset();
            if (bgShiftTarget == null) { logger.Info("Kippix: move the picture: KX_BgShift isn't in the theme"); return; }
            if (!BgMoveBuild(host)) return;

            bgMoveFromX = settings.HomeBgOffsetX;
            bgMoveFromY = settings.HomeBgOffsetY;
            bgMoveOn = true;
            bgMoveStartedAt = DateTime.Now;
            bgMovePanel = UiTools.FindByName(win, "KX_OptionsPanel");
            if (bgMovePanel != null)
            {
                bgMovePanelOpacity = bgMovePanel.Opacity;
                bgMovePanelHit = bgMovePanel.IsHitTestVisible;
                bgMovePanel.Opacity = 0;
                bgMovePanel.IsHitTestVisible = false;
            }
            bgMoveLayer.Visibility = Visibility.Visible;
            BgMoveDraw();
            PlaySound("select");

            // anything that takes the settings away meanwhile (home, a game starting, a box): it's cancelled
            if (bgMoveWatch == null)
            {
                bgMoveWatch = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
                bgMoveWatch.Tick += (s, e) =>
                {
                    try
                    {
                        if (!bgMoveOn) { bgMoveWatch.Stop(); return; }
                        if (openPanel != "options" || optionsPage != "background" || gameRunningNow || confirmOpen || kbOpen || !BgMoveShown) BgMoveEnd(false);
                    }
                    catch (Exception ex) { logger.Warn("Kippix: move the picture: " + ex.Message); }
                };
            }
            bgMoveWatch.Start();
        }

        /// <summary>A: keep where it is. B: back to where it was.</summary>
        private void BgMoveEnd(bool keep)
        {
            if (!bgMoveOn) return;
            bgMoveOn = false;
            bgMoveEndedAt = DateTime.Now;
            if (bgMoveWatch != null) bgMoveWatch.Stop();
            if (bgMoveLayer != null) bgMoveLayer.Visibility = Visibility.Collapsed;
            if (bgMovePanel != null)
            {
                bgMovePanel.Opacity = bgMovePanelOpacity;
                bgMovePanel.IsHitTestVisible = bgMovePanelHit;
                bgMovePanel = null;
            }
            bool changed = settings.HomeBgOffsetX != bgMoveFromX || settings.HomeBgOffsetY != bgMoveFromY;
            if (!keep)
            {
                settings.HomeBgOffsetX = bgMoveFromX;
                settings.HomeBgOffsetY = bgMoveFromY;
            }
            NormalizeBgOffset();
            ApplyHomeBgOffset();
            if (keep && changed) SaveSettings();
            Notify("HomeBgOffsetText");
            PlaySound(keep ? "select" : "back");
            if (openPanel == "options" && optionsPage == "background")
                ui.BeginInvoke(new Action(() =>
                {
                    var f = Keyboard.FocusedElement as DependencyObject;
                    var panel = Application.Current != null && Application.Current.MainWindow != null ? UiTools.FindByName(Application.Current.MainWindow, "KX_OptionsPanel") : null;
                    if (panel != null && f != null && panel.IsAncestorOf(f)) return;   // (the highlight never left the row)
                    if (!UiTools.FocusCommandParameter("KX_OptionsPanel", "homebgmove")) UiTools.FocusInside("KX_OptionsPanel");
                }), DispatcherPriority.ContextIdle);
        }

        private void BgMoveBy(int dx, int dy)
        {
            double step = BgMoveSteps[Math.Max(0, Math.Min(BgMoveSteps.Length - 1, bgMoveStep))];
            double x = BgMoveAxis(settings.HomeBgOffsetX, dx * step), y = BgMoveAxis(settings.HomeBgOffsetY, dy * step);
            bool landed = (x == 0 && settings.HomeBgOffsetX != 0) || (y == 0 && settings.HomeBgOffsetY != 0);
            if (x == settings.HomeBgOffsetX && y == settings.HomeBgOffsetY) return;   // (at the edge of how far it goes)
            settings.HomeBgOffsetX = x;
            settings.HomeBgOffsetY = y;
            ApplyHomeBgOffset();
            BgMoveDraw();
            PlaySound(landed ? "select" : "move");   // (a different sound when it lands in the middle)
        }

        /// <summary>One axis moved by d: crossing the middle stops on it first (easy to land on), and never past a whole screen.</summary>
        private static double BgMoveAxis(double v, double d)
        {
            if (d == 0) return v;
            double n = v + d;
            if (v != 0 && Math.Sign(n) != Math.Sign(v)) n = 0;
            return CleanOffset(n);
        }

        private void BgMoveCentre()
        {
            if (settings.HomeBgOffsetX == 0 && settings.HomeBgOffsetY == 0) return;
            settings.HomeBgOffsetX = settings.HomeBgOffsetY = 0;
            ApplyHomeBgOffset();
            BgMoveDraw();
            PlaySound("select");
        }

        private void BgMoveChangeStep(int dir)
        {
            int s = Math.Max(0, Math.Min(BgMoveSteps.Length - 1, bgMoveStep + dir));
            if (s == bgMoveStep) return;
            bgMoveStep = s;
            BgMoveDraw();
            PlaySound("move");
        }

        /// <summary>Keys while positioning (window PreviewKeyDown; the D-pad arrives as arrows, LT / RT as PgUp / PgDn). Takes every key.</summary>
        private void BgMoveKey(KeyEventArgs e)
        {
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (controller buttons: BgMovePad)
            if (e.Key == Key.System) return;   // (Alt+F4 and the like stay Windows')
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Left: BgMoveBy(-1, 0); break;
                case Key.Right: BgMoveBy(1, 0); break;
                case Key.Up: BgMoveBy(0, -1); break;
                case Key.Down: BgMoveBy(0, 1); break;
                case Key.PageUp: case Key.OemMinus: case Key.Subtract: BgMoveChangeStep(-1); break;
                case Key.PageDown: case Key.OemPlus: case Key.Add: BgMoveChangeStep(1); break;
                case Key.R: case Key.Home: case Key.Delete: if (!e.IsRepeat) BgMoveCentre(); break;
                case Key.Enter: case Key.Space: if (!e.IsRepeat && !BgMoveJustStarted) BgMoveEnd(true); break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat && !BgMoveJustStarted) BgMoveEnd(false); break;
            }
        }

        /// <summary>The controller's buttons while positioning (from OnPadDownEarly; every button is taken).</summary>
        private void BgMovePad(ControllerInput button, KeyEventArgs e)
        {
            if (IsPadRelease(e)) return;
            switch (button)
            {
                case ControllerInput.A: if (!BgMoveJustStarted) BgMoveEnd(true); break;
                case ControllerInput.B: if (!BgMoveJustStarted) BgMoveEnd(false); break;
                case ControllerInput.Y: BgMoveCentre(); break;
                case ControllerInput.LeftShoulder: BgMoveChangeStep(-1); break;
                case ControllerInput.RightShoulder: BgMoveChangeStep(1); break;
                // the D-pad and the sticks come as arrows, LT / RT as PgUp / PgDn (BgMoveKey); the rest does nothing here
            }
        }

        // ---------------- the cross and the box ----------------

        private bool BgMoveBuild(Panel host)
        {
            try
            {
                if (bgMoveLayer != null && ReferenceEquals(VisualTreeHelper.GetParent(bgMoveLayer), host)) return true;
                if (bgMoveLayer != null) { var old = VisualTreeHelper.GetParent(bgMoveLayer) as Panel; if (old != null) old.Children.Remove(bgMoveLayer); }

                var layer = new Grid { IsHitTestVisible = false, Focusable = false, Visibility = Visibility.Collapsed };
                Panel.SetZIndex(layer, 145);   // over the home screen (and Night mode's dim, 140), under the intro (150)
                Grid.SetRowSpan(layer, 99);
                Grid.SetColumnSpan(layer, 99);
                var canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
                layer.Children.Add(canvas);
                // a soft dark line under each dashed line, so it shows on a white picture too
                bgMoveHShade = BgMoveLine(4, new DoubleCollection { 3, 2.5 }, Color.FromArgb(0x73, 0, 0, 0));
                bgMoveVShade = BgMoveLine(4, new DoubleCollection { 3, 2.5 }, Color.FromArgb(0x73, 0, 0, 0));
                bgMoveH = BgMoveLine(2, new DoubleCollection { 6, 5 }, Colors.White);
                bgMoveV = BgMoveLine(2, new DoubleCollection { 6, 5 }, Colors.White);
                foreach (var l in new[] { bgMoveHShade, bgMoveVShade, bgMoveH, bgMoveV }) canvas.Children.Add(l);

                // the box: what this is, where the picture is, the step, and what the buttons do
                var box = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40, 0, 40, 56),
                    Padding = new Thickness(26, 14, 26, 14), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1),
                    Background = Frozen(Color.FromArgb(0xF0, 0x11, 0x13, 0x17)), BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xD5, 0x4F)), IsHitTestVisible = false
                };
                var stack = new StackPanel();
                var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var title = new TextBlock { Text = Loc("LOC_KX_BgMoveTitle", "Move the picture"), VerticalAlignment = VerticalAlignment.Center };
                bgMoveWhere = new TextBlock { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                bgMoveStepText = new TextBlock { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                // (the theme's styles are looked up once it's in the window: below, after it's added)
                top.Children.Add(title);
                top.Children.Add(bgMoveWhere);
                top.Children.Add(bgMoveStepText);
                stack.Children.Add(top);
                bgMoveHints = new ObservableCollection<HintGroup>();
                bgMoveHintsSig = null;
                var hints = new ItemsControl { Focusable = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 1640, ItemsSource = bgMoveHints };
                hints.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
                stack.Children.Add(hints);
                box.Child = stack;
                layer.Children.Add(box);

                host.Children.Add(layer);
                bgMoveLayer = layer;
                layer.SizeChanged += (s, e) => { try { if (bgMoveOn) BgMoveDraw(); } catch (Exception) { } };

                // the theme's text styles and the edit hints' template (PART_MainHost's resources): found now it's in the window
                var sText = layer.TryFindResource("KX_Text") as Style;
                var sSub = layer.TryFindResource("KX_TextSub") as Style ?? sText;
                var sDim = layer.TryFindResource("KX_TextDim") as Style ?? sText;
                if (sText != null) title.Style = sText;
                title.FontFamily = layer.TryFindResource("FontTitilliumWebBold") as FontFamily ?? title.FontFamily;
                if (sSub != null) bgMoveWhere.Style = sSub;
                if (sDim != null) bgMoveStepText.Style = sDim;
                if (sText == null) { title.Foreground = Brushes.White; bgMoveWhere.Foreground = Brushes.White; bgMoveStepText.Foreground = Brushes.Gray; }
                var tpl = layer.TryFindResource("KX_EditHintGroupTemplate") as DataTemplate;
                if (tpl != null) hints.ItemTemplate = tpl;
                else hints.Visibility = Visibility.Collapsed;   // (an older theme: the line of text above still says what it is)
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: move the picture: " + ex.Message);
                return false;
            }
        }

        private static Line BgMoveLine(double thickness, DoubleCollection dashes, Color colour)
        {
            return new Line
            {
                StrokeThickness = thickness, StrokeDashArray = dashes, StrokeDashCap = PenLineCap.Flat,
                Stroke = new SolidColorBrush(colour), IsHitTestVisible = false, SnapsToDevicePixels = true
            };
        }

        /// <summary>The cross through the picture's middle (white, or yellow on an axis where it's centred) and the box's text.</summary>
        private void BgMoveDraw()
        {
            if (bgMoveLayer == null || bgShiftTarget == null) return;
            double w = bgMoveLayer.ActualWidth, h = bgMoveLayer.ActualHeight;
            if (w <= 0 || h <= 0) return;
            Point c;
            try { c = bgShiftTarget.TranslatePoint(new Point(bgShiftTarget.ActualWidth / 2, bgShiftTarget.ActualHeight / 2), bgMoveLayer); }   // (with the offset on it)
            catch (Exception) { c = new Point(w / 2 + settings.HomeBgOffsetX * w, h / 2 + settings.HomeBgOffsetY * h); }
            double x = Math.Round(c.X) + 0.5, y = Math.Round(c.Y) + 0.5;
            foreach (var l in new[] { bgMoveH, bgMoveHShade }) { l.X1 = 0; l.X2 = w; l.Y1 = y; l.Y2 = y; }
            foreach (var l in new[] { bgMoveV, bgMoveVShade }) { l.X1 = x; l.X2 = x; l.Y1 = 0; l.Y2 = h; }
            ((SolidColorBrush)bgMoveH.Stroke).Color = settings.HomeBgOffsetY == 0 ? BgMoveYellow : Colors.White;   // level with the screen's middle
            ((SolidColorBrush)bgMoveV.Stroke).Color = settings.HomeBgOffsetX == 0 ? BgMoveYellow : Colors.White;   // in line with the screen's middle
            if (bgMoveWhere != null) bgMoveWhere.Text = BgOffsetText(settings.HomeBgOffsetX, settings.HomeBgOffsetY);
            if (bgMoveStepText != null)
                bgMoveStepText.Text = string.Format(Loc("LOC_KX_BgMoveStep", "Step {0}%"),
                                                    (BgMoveSteps[Math.Max(0, Math.Min(BgMoveSteps.Length - 1, bgMoveStep))] * 100).ToString("0.#", CultureInfo.CurrentCulture));
            if (bgMoveHints != null)
            {
                var m = new HintMaker(HintsKeyboard);
                m.Add(Loc("LOC_KX_HintMove", "Move"), "Dpad", "Arrows");
                m.Add(Loc("LOC_KX_HintStep", "Step size"), "LB RB", "PgUp PgDn");
                m.Group();
                m.Add(Loc("LOC_KX_HintCentre", "Centre"), "Y", "R");
                m.Group();
                m.Add(Loc("LOC_KX_HintDone", "Done"), "A", "Enter");
                m.Add(Loc("LOC_KX_HintCancel", "Cancel"), "B", "Esc");
                Fill(bgMoveHints, m.Items, ref bgMoveHintsSig);
            }
        }
    }
}

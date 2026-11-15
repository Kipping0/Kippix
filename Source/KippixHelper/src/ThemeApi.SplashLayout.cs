using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Playnite.SDK.Events;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.1) Settings › Game page › Splash screen layout: what the launch screen (Views/GameStatus.xaml) shows over the game's
    /// picture while a game starts, and where. By default nothing but the picture and Playnite's "Starting…" (as before).
    ///  - Game logo (KX_LaunchLogoBox): the game's logo (ExtraMetadata's Logo.png), or its name when it has none. Top middle.
    ///  - Game card (KX_LaunchCard): its cover, in the middle. Not when the game has no background picture (the cover is the
    ///    picture then, big, as before).
    ///  - "Starting…" (KX_LaunchStatus): Playnite's own words and its Close button, bottom left. Always shown; it can be moved.
    /// Each is moved from where it sits by default by a share of the screen (SplashLogoX / Y …: 0 is there) and sized (…Size,
    /// 1 as it comes), so it lands in the same place on any screen. Applied as a RenderTransform when the screen fills in.
    ///
    /// Edit placement: a copy of the launch screen with a game of yours (the one highlighted, else one you played lately) over the
    /// whole screen, the settings hidden behind it. LB / RB pick the part, the D-pad moves it, LT / RT size it, X changes the
    /// step, Y puts that part back, A keeps it all, B puts it all back as it was. Moving across where it sits by default stops
    /// there first (easy to land on), like Move the picture (ThemeApi.BgMove.cs).
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<PickRow> SplashRows { get { return splashRows ?? (splashRows = new ObservableCollection<PickRow>()); } }
        private ObservableCollection<PickRow> splashRows;
        public bool IsOptionsSplash { get { return optionsPage == "splash"; } }

        private static readonly string[] SplashParts = { "logo", "card", "status" };
        private static readonly double[] SplashSteps = { 0.002, 0.005, 0.01, 0.025, 0.05 };

        // ---------------- the settings page ----------------

        private void BuildSplashRows()
        {
            var rows = SplashRows;
            rows.Clear();
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecSplashShows", "WHILE A GAME STARTS") });
            rows.Add(new PickRow { IsInfo = true, Icon = "picture", Key = "splashpic", Text = Loc("LOC_KX_SplashPicture2", "The game's picture"), Sub = Loc("LOC_KX_SplashPictureSub", "Always there") });
            rows.Add(BoardSwitch("note", "splashstatus", Loc("LOC_KX_SplashStatusRow", "\"Starting…\" message"), settings.SplashShowStatus,
                                 Loc("LOC_KX_SplashStatusOn", "Playnite's words while the game starts"), Loc("LOC_KX_Off", "Off"),
                                 () => { settings.SplashShowStatus = !settings.SplashShowStatus; SplashChanged("splashstatus"); }));
            rows.Add(BoardSwitch("tag", "splashlogo", Loc("LOC_KX_SplashLogo", "Game logo"), settings.SplashShowLogo,
                                 Loc("LOC_KX_SplashLogoOn", "Over the picture (its name when it has no logo)"), Loc("LOC_KX_Off", "Off"),
                                 () => { settings.SplashShowLogo = !settings.SplashShowLogo; SplashChanged("splashlogo"); }));
            rows.Add(BoardSwitch("shelf", "splashcard", Loc("LOC_KX_SplashCard", "Game card"), settings.SplashShowCard,
                                 Loc("LOC_KX_SplashCardOn", "Its cover, over the picture"), Loc("LOC_KX_Off", "Off"),
                                 () => { settings.SplashShowCard = !settings.SplashShowCard; SplashChanged("splashcard"); }));
            rows.Add(new PickRow
            {
                Icon = "move", Key = "splashedit", Text = Loc("LOC_KX_SplashEdit", "Edit placement"),
                Sub = SplashMoved() ? Loc("LOC_KX_SplashEditSubMoved", "Moved from where they start. Move and size the logo, the card and \"Starting…\"")
                                    : Loc("LOC_KX_SplashEditSub", "Move and size the logo, the card and \"Starting…\""),
                Command = new RelayCommand(_ => SplashEditStart())
            });
            if (SplashMoved())
                rows.Add(new PickRow
                {
                    Icon = "refresh", Key = "splashreset", Text = Loc("LOC_KX_SplashReset", "Put them back"), Sub = Loc("LOC_KX_SplashResetSub", "Where they start, at their own size"),
                    Command = new RelayCommand(_ => Confirm(Loc("LOC_KX_SplashResetTitle", "Put the splash screen back?"), Loc("LOC_KX_SplashResetText", "The logo, the card and \"Starting…\" go back where they start."),
                                                            Loc("LOC_KX_Reset", "Reset"), () =>
                                                            {
                                                                foreach (var p in SplashParts) SetSplashPlace(p, 0, 0, 1);
                                                                SaveSettings();
                                                                BoardRowsAgain(BuildSplashRows, "splashedit");
                                                            }))
                });
        }

        private void SplashChanged(string key)
        {
            SaveSettings();
            PlaySound("move");
            BoardRowsAgain(BuildSplashRows, key);
        }

        private bool SplashMoved()
        {
            return SplashParts.Any(p => { double x, y, s; GetSplashPlace(p, out x, out y, out s); return x != 0 || y != 0 || s != 1; });
        }

        private void GetSplashPlace(string part, out double x, out double y, out double size)
        {
            switch (part)
            {
                case "logo": x = settings.SplashLogoX; y = settings.SplashLogoY; size = settings.SplashLogoSize; break;
                case "card": x = settings.SplashCardX; y = settings.SplashCardY; size = settings.SplashCardSize; break;
                default: x = settings.SplashStatusX; y = settings.SplashStatusY; size = settings.SplashStatusSize; break;
            }
            x = CleanOffset(x); y = CleanOffset(y);
            size = double.IsNaN(size) || size <= 0 ? 1 : Math.Round(Math.Max(0.3, Math.Min(3, size)), 2);
        }

        private void SetSplashPlace(string part, double x, double y, double size)
        {
            switch (part)
            {
                case "logo": settings.SplashLogoX = x; settings.SplashLogoY = y; settings.SplashLogoSize = size; break;
                case "card": settings.SplashCardX = x; settings.SplashCardY = y; settings.SplashCardSize = size; break;
                default: settings.SplashStatusX = x; settings.SplashStatusY = y; settings.SplashStatusSize = size; break;
            }
        }

        // ---------------- the launch screen itself ----------------

        private bool splashSizeHooked;

        /// <summary>The logo, the card and the "Starting…" on the launch screen: shown or not, and where (FillLaunchArt calls it).</summary>
        private void ApplySplashLayout(FrameworkElement status, Game g, bool hasBackground)
        {
            if (status == null) return;
            if (!splashSizeHooked)
            {
                splashSizeHooked = true;
                status.SizeChanged += (s, e) => { try { ApplySplashLayout(status, launchGame, lastLaunchHadBg); } catch (Exception) { } };
            }
            lastLaunchHadBg = hasBackground;
            var host = UiTools.FindByName(status, "PART_ViewHost") as FrameworkElement ?? status;
            double w = host.ActualWidth > 0 ? host.ActualWidth : DesignWidth, h = host.ActualHeight > 0 ? host.ActualHeight : 1080;

            var logoBox = UiTools.FindByName(status, "KX_LaunchLogoBox") as FrameworkElement;
            var logo = UiTools.FindByName(status, "KX_LaunchLogo") as Image;
            var nameBox = UiTools.FindByName(status, "KX_LaunchNameBox") as FrameworkElement;
            var name = UiTools.FindByName(status, "KX_LaunchName") as TextBlock;
            var card = UiTools.FindByName(status, "KX_LaunchCard") as Border;
            var words = UiTools.FindByName(status, "KX_LaunchStatus") as FrameworkElement;

            if (logoBox != null)
            {
                bool show = settings.SplashShowLogo && g != null;
                ImageSource pic = show ? SplashLogoPicture(g) : null;
                if (logo != null) { logo.Source = pic; logo.Visibility = pic != null ? Visibility.Visible : Visibility.Collapsed; }
                if (name != null) name.Text = g != null ? g.Name : "";
                if (nameBox != null) nameBox.Visibility = pic == null ? Visibility.Visible : Visibility.Collapsed;
                logoBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                PlaceSplashPart(logoBox, "logo", w, h);
            }
            if (card != null)
            {
                ImageSource cover = settings.SplashShowCard && g != null && hasBackground ? SplashCoverPicture(g) : null;
                SetSplashCard(card, cover);
                PlaceSplashPart(card, "card", w, h);
            }
            if (words != null) PlaceSplashPart(words, "status", w, h);
            var said = UiTools.FindByName(status, "PART_TextStatus") as FrameworkElement;   // (Playnite's words; its Close button stays)
            if (said != null) said.Visibility = settings.SplashShowStatus ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool lastLaunchHadBg;

        private void PlaceSplashPart(FrameworkElement el, string part, double w, double h)
        {
            double x, y, size;
            GetSplashPlace(part, out x, out y, out size);
            if (x == 0 && y == 0 && size == 1) { if (el.RenderTransform is TransformGroup) el.RenderTransform = Transform.Identity; return; }
            var g = new TransformGroup();
            g.Children.Add(new ScaleTransform(size, size));
            g.Children.Add(new TranslateTransform(Math.Round(x * w, 1), Math.Round(y * h, 1)));
            el.RenderTransform = g;
        }

        private static void SetSplashCard(Border card, ImageSource cover)
        {
            if (cover == null) { card.Background = null; card.Visibility = Visibility.Collapsed; return; }
            var bs = cover as System.Windows.Media.Imaging.BitmapSource;
            double aspect = bs != null && bs.PixelHeight > 0 ? (double)bs.PixelWidth / bs.PixelHeight : 2.0 / 3.0;
            aspect = Math.Max(0.5, Math.Min(1.8, aspect));   // (a square or wide cover is fine; a strip isn't)
            card.Height = 440;
            card.Width = Math.Round(440 * aspect);
            card.Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            card.Visibility = Visibility.Visible;
        }

        private ImageSource SplashLogoPicture(Game g)
        {
            try
            {
                string lf = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString(), "Logo.png");
                return File.Exists(lf) ? Images.LoadUncached(lf, 900) : null;
            }
            catch (Exception) { return null; }
        }

        private ImageSource SplashCoverPicture(Game g)
        {
            try
            {
                if (string.IsNullOrEmpty(g.CoverImage) || g.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
                string path = api.Database.GetFullFilePath(g.CoverImage);
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? Images.LoadUncached(path, 700) : null;
            }
            catch (Exception) { return null; }
        }

        // =====================================================================
        // Edit placement
        // =====================================================================

        private bool splashEditOn;
        private int splashPart;
        private int splashStep = 2;
        private double[] splashFrom;
        private DateTime splashStartedAt = DateTime.MinValue, splashEndedAt = DateTime.MinValue;
        private DispatcherTimer splashWatch;
        private FrameworkElement splashPanel;
        private double splashPanelOpacity = 1;
        private bool splashPanelHit = true;

        private Grid splashLayer, splashStage;
        private FrameworkElement splashLogoBox, splashStatus;
        private Border splashCard, splashBox;
        private System.Windows.Shapes.Rectangle splashRing;
        private Canvas splashCanvas;
        private System.Windows.Shapes.Line splashGuideH, splashGuideV;
        private TextBlock splashWhat, splashWhere;
        private ObservableCollection<HintGroup> splashHints;
        private string splashHintsSig;

        private bool SplashJustStarted { get { return (DateTime.Now - splashStartedAt).TotalMilliseconds < 400; } }

        private void SplashEditStart()
        {
            if (splashEditOn || !IsFullscreen) return;
            if ((DateTime.Now - splashEndedAt).TotalMilliseconds < 500) return;   // (the Enter that came with the A that just ended it)
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var host = win != null ? UiTools.FindByName(win, "PART_MainHost") as Panel : null;
            if (host == null) { logger.Info("Kippix: splash screen layout: the main screen (PART_MainHost) wasn't found"); return; }
            var g = SplashSampleGame();
            if (!SplashBuild(host, g)) return;
            splashFrom = new double[9];
            for (int i = 0; i < 3; i++) { double x, y, s; GetSplashPlace(SplashParts[i], out x, out y, out s); splashFrom[i * 3] = x; splashFrom[i * 3 + 1] = y; splashFrom[i * 3 + 2] = s; }
            // the first part that's switched on (else "Starting…")
            splashPart = settings.SplashShowLogo ? 0 : settings.SplashShowCard ? 1 : 2;   // (all off: "Starting…", faint)
            splashEditOn = true;
            splashStartedAt = DateTime.Now;
            splashPanel = UiTools.FindByName(win, "KX_OptionsPanel");
            if (splashPanel != null)
            {
                splashPanelOpacity = splashPanel.Opacity;
                splashPanelHit = splashPanel.IsHitTestVisible;
                splashPanel.Opacity = 0;
                splashPanel.IsHitTestVisible = false;
            }
            splashLayer.Visibility = Visibility.Visible;
            PlaySound("select");
            logger.Info("Kippix: splash screen layout: editing (with " + (g != null ? g.Name : "no game") + ")");
            splashLayer.Dispatcher.BeginInvoke(new Action(SplashDraw), DispatcherPriority.Loaded);

            if (splashWatch == null)
            {
                splashWatch = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
                splashWatch.Tick += (s, e) =>
                {
                    try
                    {
                        if (!splashEditOn) { splashWatch.Stop(); return; }
                        if (openPanel != "options" || optionsPage != "splash" || gameRunningNow || confirmOpen || kbOpen) SplashEditEnd(false);
                    }
                    catch (Exception ex) { logger.Warn("Kippix: splash screen layout: " + ex.Message); }
                };
            }
            splashWatch.Start();
        }

        private void SplashEditEnd(bool keep)
        {
            if (!splashEditOn) return;
            splashEditOn = false;
            splashEndedAt = DateTime.Now;
            if (splashWatch != null) splashWatch.Stop();
            if (splashLayer != null)
            {
                var parent = VisualTreeHelper.GetParent(splashLayer) as Panel;
                if (parent != null) parent.Children.Remove(splashLayer);   // (the game's pictures go with it)
                splashLayer = null;
            }
            if (splashPanel != null)
            {
                splashPanel.Opacity = splashPanelOpacity;
                splashPanel.IsHitTestVisible = splashPanelHit;
                splashPanel = null;
            }
            if (!keep && splashFrom != null)
                for (int i = 0; i < 3; i++) SetSplashPlace(SplashParts[i], splashFrom[i * 3], splashFrom[i * 3 + 1], splashFrom[i * 3 + 2]);
            if (keep) SaveSettings();
            PlaySound(keep ? "select" : "back");
            if (openPanel == "options" && optionsPage == "splash") BoardRowsAgain(BuildSplashRows, "splashedit");
        }

        /// <summary>The game shown while editing: the one highlighted, else one you played lately with a picture.</summary>
        private Game SplashSampleGame()
        {
            Game g = null;
            try { g = SelectedGameNow(); } catch (Exception) { }
            if (g != null && !string.IsNullOrEmpty(g.BackgroundImage)) return g;
            try
            {
                var best = api.Database.Games.Where(x => x != null && !string.IsNullOrEmpty(x.BackgroundImage) && !x.Hidden)
                                             .OrderByDescending(x => x.LastActivity ?? DateTime.MinValue).FirstOrDefault();
                if (best != null) return best;
            }
            catch (Exception) { }
            return g;
        }

        private bool SplashBuild(Panel host, Game g)
        {
            try
            {
                var layer = new Grid { Focusable = false, Background = Brushes.Black, Visibility = Visibility.Collapsed, ClipToBounds = true };
                Panel.SetZIndex(layer, 146);   // over everything on the home screen and the settings (they're hidden meanwhile), under the intro (150)
                Grid.SetRowSpan(layer, 99);
                Grid.SetColumnSpan(layer, 99);
                var stage = new Grid();
                layer.Children.Add(stage);

                // the picture, the same as the launch screen: what the game's background is (else its wide art), the shade at the bottom
                ImageSource bg = null;
                if (g != null)
                {
                    bg = LaunchFile(g.BackgroundImage);
                    if (bg == null) { string wide = WideArtPath(g); if (wide != null) bg = Images.LoadUncached(wide, 0); }
                }
                if (bg != null) stage.Children.Add(new Image { Source = bg, Stretch = Stretch.UniformToFill });
                var shade = new Border { Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55), new GradientStop(Color.FromArgb(0xB3, 0, 0, 0), 1) }, 90) };
                stage.Children.Add(shade);

                // the logo (or the name), top middle
                var logoBox = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 90, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
                var lp = g != null ? SplashLogoPicture(g) : null;
                if (lp != null) logoBox.Children.Add(new Image { Source = lp, MaxHeight = 170, MaxWidth = 620, Stretch = Stretch.Uniform });
                else
                {
                    var vb = new Viewbox { StretchDirection = StretchDirection.DownOnly, MaxWidth = 900, MaxHeight = 170 };
                    vb.Child = new TextBlock { Text = g != null ? g.Name : Loc("LOC_KX_SplashLogo", "Game logo"), FontSize = 72, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                                               Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7 } };
                    logoBox.Children.Add(vb);
                }
                stage.Children.Add(logoBox);

                // the card, in the middle
                var card = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(16), RenderTransformOrigin = new Point(0.5, 0.5),
                                        Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.6 } };
                var cover = g != null ? SplashCoverPicture(g) : null;
                if (cover != null) SetSplashCard(card, cover);
                else { card.Width = 293; card.Height = 440; card.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x29)); }
                card.Visibility = Visibility.Visible;
                stage.Children.Add(card);

                // "Starting…" and Playnite's Close button, bottom left (the same size and place as Views/GameStatus.xaml)
                var words = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(64, 0, 64, 56), RenderTransformOrigin = new Point(0, 1) };
                var wt = new TextBlock { Text = string.Format(Loc("LOC_KX_SplashStarting", "Starting {0}…"), g != null ? g.Name : ""), FontSize = 20, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xF4, 0xF6, 0xFA)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
                words.Children.Add(wt);
                var close = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1.5), Padding = new Thickness(18, 8, 18, 8), MinWidth = 110, Opacity = 0.85 };
                var ct = new TextBlock { Text = Loc("LOC_KX_SplashClose", "Close"), FontSize = 18, Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA)), HorizontalAlignment = HorizontalAlignment.Center };
                close.Child = ct;
                words.Children.Add(close);
                stage.Children.Add(words);

                // the guides (where the part sits by default: yellow when it's there) and the ring round the part being moved
                splashCanvas = new Canvas { IsHitTestVisible = false };
                splashGuideH = BgMoveLine(2, new DoubleCollection { 6, 5 }, Colors.White);
                splashGuideV = BgMoveLine(2, new DoubleCollection { 6, 5 }, Colors.White);
                splashRing = new System.Windows.Shapes.Rectangle { StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 10, RadiusY = 10, IsHitTestVisible = false };
                splashCanvas.Children.Add(splashGuideH);
                splashCanvas.Children.Add(splashGuideV);
                splashCanvas.Children.Add(splashRing);
                layer.Children.Add(splashCanvas);

                // the box: which part, where, the step, and the buttons
                var box = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40, 0, 40, 40),
                    Padding = new Thickness(26, 14, 26, 14), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1),
                    Background = Frozen(Color.FromArgb(0xF0, 0x11, 0x13, 0x17)), BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xD5, 0x4F)), IsHitTestVisible = false
                };
                var stack = new StackPanel();
                var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var title = new TextBlock { Text = Loc("LOC_KX_SplashEditTitle", "Splash screen layout"), VerticalAlignment = VerticalAlignment.Center };
                splashWhat = new TextBlock { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                splashWhere = new TextBlock { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                top.Children.Add(title);
                top.Children.Add(splashWhat);
                top.Children.Add(splashWhere);
                stack.Children.Add(top);
                splashHints = new ObservableCollection<HintGroup>();
                splashHintsSig = null;
                var hints = new ItemsControl { Focusable = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 1640, ItemsSource = splashHints };
                hints.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
                stack.Children.Add(hints);
                box.Child = stack;
                layer.Children.Add(box);

                host.Children.Add(layer);
                splashLayer = layer;
                splashStage = stage;
                splashLogoBox = logoBox;
                splashCard = card;
                splashStatus = words;
                splashBox = box;
                layer.SizeChanged += (s, e) => { try { if (splashEditOn) SplashDraw(); } catch (Exception) { } };

                var sText = layer.TryFindResource("KX_Text") as Style;
                var sSub = layer.TryFindResource("KX_TextSub") as Style ?? sText;
                var sDim = layer.TryFindResource("KX_TextDim") as Style ?? sText;
                if (sText != null) { title.Style = sText; splashWhat.Style = sText; }
                title.FontFamily = layer.TryFindResource("FontTitilliumWebBold") as FontFamily ?? title.FontFamily;
                if (sSub != null) splashWhere.Style = sSub;
                if (sText == null) { title.Foreground = Brushes.White; splashWhat.Foreground = Brushes.White; splashWhere.Foreground = Brushes.Gray; }
                var font = layer.TryFindResource("FontTitilliumWeb") as FontFamily;
                if (font != null) { wt.FontFamily = font; ct.FontFamily = font; }
                var bold = layer.TryFindResource("FontTitilliumWebBold") as FontFamily;
                foreach (var tb in UiTools.FindAll<TextBlock>(logoBox)) if (bold != null) tb.FontFamily = bold;
                var tpl = layer.TryFindResource("KX_EditHintGroupTemplate") as DataTemplate;
                if (tpl != null) hints.ItemTemplate = tpl; else hints.Visibility = Visibility.Collapsed;
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: splash screen layout: " + ex.Message);
                return false;
            }
        }

        private string SplashPartName(int i)
        {
            switch (SplashParts[i])
            {
                case "logo": return Loc("LOC_KX_SplashLogo", "Game logo");
                case "card": return Loc("LOC_KX_SplashCard", "Game card");
                default: return Loc("LOC_KX_SplashStatus", "\"Starting…\"");
            }
        }

        private bool SplashPartOn(int i)
        {
            return SplashParts[i] == "logo" ? settings.SplashShowLogo : SplashParts[i] == "card" ? settings.SplashShowCard : settings.SplashShowStatus;
        }

        private FrameworkElement SplashPartElement(int i)
        {
            return i == 0 ? splashLogoBox : i == 1 ? (FrameworkElement)splashCard : splashStatus;
        }

        /// <summary>Every part where it's set, the part being moved ringed, its guides, and the box's words.</summary>
        private void SplashDraw()
        {
            if (splashLayer == null || splashStage == null) return;
            double w = splashLayer.ActualWidth, h = splashLayer.ActualHeight;
            if (w <= 0 || h <= 0) return;
            for (int i = 0; i < 3; i++)
            {
                var el = SplashPartElement(i);
                if (el == null) continue;
                PlaceSplashPart(el, SplashParts[i], w, h);
                el.Opacity = SplashPartOn(i) ? 1 : i == splashPart ? 0.45 : 0.18;   // (switched off: faint, so it's clear it won't show)
            }
            splashLayer.UpdateLayout();
            var sel = SplashPartElement(splashPart);
            double x, y, size;
            GetSplashPlace(SplashParts[splashPart], out x, out y, out size);
            Rect r = Rect.Empty;
            try { if (sel != null && sel.ActualWidth > 0) r = sel.TransformToVisual(splashLayer).TransformBounds(new Rect(0, 0, sel.ActualWidth, sel.ActualHeight)); } catch (Exception) { }
            if (!r.IsEmpty)
            {
                Canvas.SetLeft(splashRing, Math.Round(r.Left - 10));
                Canvas.SetTop(splashRing, Math.Round(r.Top - 10));
                splashRing.Width = r.Width + 20;
                splashRing.Height = r.Height + 20;
                splashRing.Stroke = AccentBrushNow();
                splashRing.Visibility = Visibility.Visible;
                // the guides through the part's middle: yellow on an axis where it's where it starts
                double cx = Math.Round(r.Left + r.Width / 2) + 0.5, cy = Math.Round(r.Top + r.Height / 2) + 0.5;
                splashGuideH.X1 = 0; splashGuideH.X2 = w; splashGuideH.Y1 = cy; splashGuideH.Y2 = cy;
                splashGuideV.X1 = cx; splashGuideV.X2 = cx; splashGuideV.Y1 = 0; splashGuideV.Y2 = h;
                ((SolidColorBrush)splashGuideH.Stroke).Color = y == 0 ? BgMoveYellow : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
                ((SolidColorBrush)splashGuideV.Stroke).Color = x == 0 ? BgMoveYellow : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
                // the box keeps out of its way: at the top when the part is low down
                bool low = r.Top + r.Height / 2 > h * 0.55;
                splashBox.VerticalAlignment = low ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                splashBox.Margin = low ? new Thickness(40, 40, 40, 0) : new Thickness(40, 0, 40, 40);
            }
            else splashRing.Visibility = Visibility.Collapsed;

            splashWhat.Text = SplashPartName(splashPart) + (SplashPartOn(splashPart) ? "" : "  (" + Loc("LOC_KX_SplashOff", "switched off") + ")");
            string where = x == 0 && y == 0 ? Loc("LOC_KX_SplashWhereStart", "Where it starts") : BgOffsetText(x, y);
            splashWhere.Text = where + "  ·  " + string.Format(Loc("LOC_KX_SplashSize", "Size {0}%"), Math.Round(size * 100))
                             + "  ·  " + string.Format(Loc("LOC_KX_BgMoveStep", "Step {0}%"), (SplashSteps[splashStep] * 100).ToString("0.#", CultureInfo.CurrentCulture));
            var m = new HintMaker(HintsKeyboard);
            m.Add(Loc("LOC_KX_HintMove", "Move"), "Dpad", "Arrows");
            m.Add(Loc("LOC_KX_HintSize", "Size"), "LT RT", "PgUp PgDn");
            m.Add(Loc("LOC_KX_HintPart", "Logo / card / \"Starting…\""), "LB RB", "Q E");
            m.Group();
            m.Add(Loc("LOC_KX_HintStep", "Step size"), "X", "S");
            m.Add(Loc("LOC_KX_HintPutBack", "Put this back"), "Y", "R");
            m.Group();
            m.Add(Loc("LOC_KX_HintDone", "Done"), "A", "Enter");
            m.Add(Loc("LOC_KX_HintCancel", "Cancel"), "B", "Esc");
            Fill(splashHints, m.Items, ref splashHintsSig);
        }

        private Brush AccentBrushNow()
        {
            var b = splashLayer != null ? splashLayer.TryFindResource("KX_AccentBrush") as Brush : null;
            return b ?? Frozen(BgMoveYellow);
        }

        private void SplashMoveBy(int dx, int dy)
        {
            string part = SplashParts[splashPart];
            double x, y, size;
            GetSplashPlace(part, out x, out y, out size);
            double step = SplashSteps[splashStep];
            double nx = BgMoveAxis(x, dx * step), ny = BgMoveAxis(y, dy * step);
            if (nx == x && ny == y) return;
            bool landed = (nx == 0 && x != 0) || (ny == 0 && y != 0);
            SetSplashPlace(part, nx, ny, size);
            SplashDraw();
            PlaySound(landed ? "select" : "move");
        }

        private void SplashSizeBy(int dir)
        {
            string part = SplashParts[splashPart];
            double x, y, size;
            GetSplashPlace(part, out x, out y, out size);
            double n = Math.Round(Math.Max(0.3, Math.Min(3, size + dir * 0.05)), 2);
            if (Math.Abs(n - 1) < 0.001) n = 1;
            if (n == size) return;
            SetSplashPlace(part, x, y, n);
            SplashDraw();
            PlaySound(n == 1 ? "select" : "move");
        }

        private void SplashNextPart(int dir)
        {
            splashPart = (splashPart + dir + 3) % 3;
            SplashDraw();
            PlaySound("move");
        }

        private void SplashNextStep()
        {
            splashStep = (splashStep + 1) % SplashSteps.Length;
            SplashDraw();
            PlaySound("move");
        }

        private void SplashPutBack()
        {
            string part = SplashParts[splashPart];
            double x, y, size;
            GetSplashPlace(part, out x, out y, out size);
            if (x == 0 && y == 0 && size == 1) return;
            SetSplashPlace(part, 0, 0, 1);
            SplashDraw();
            PlaySound("select");
        }

        /// <summary>Keys while editing (window PreviewKeyDown; the D-pad arrives as arrows, LT / RT as PgUp / PgDn). Takes every key.</summary>
        private void SplashEditKey(KeyEventArgs e)
        {
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (controller buttons: SplashEditPad)
            if (e.Key == Key.System) return;
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Left: SplashMoveBy(-1, 0); break;
                case Key.Right: SplashMoveBy(1, 0); break;
                case Key.Up: SplashMoveBy(0, -1); break;
                case Key.Down: SplashMoveBy(0, 1); break;
                case Key.PageUp: case Key.OemMinus: case Key.Subtract: SplashSizeBy(-1); break;
                case Key.PageDown: case Key.OemPlus: case Key.Add: SplashSizeBy(1); break;
                case Key.Q: if (!e.IsRepeat) SplashNextPart(-1); break;
                case Key.E: case Key.Tab: if (!e.IsRepeat) SplashNextPart(1); break;
                case Key.S: if (!e.IsRepeat) SplashNextStep(); break;
                case Key.R: case Key.Home: case Key.Delete: if (!e.IsRepeat) SplashPutBack(); break;
                case Key.Enter: case Key.Space: if (!e.IsRepeat && !SplashJustStarted) SplashEditEnd(true); break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat && !SplashJustStarted) SplashEditEnd(false); break;
            }
        }

        private void SplashEditPad(ControllerInput button, KeyEventArgs e)
        {
            if (IsPadRelease(e)) return;
            switch (button)
            {
                case ControllerInput.A: if (!SplashJustStarted) SplashEditEnd(true); break;
                case ControllerInput.B: if (!SplashJustStarted) SplashEditEnd(false); break;
                case ControllerInput.X: SplashNextStep(); break;
                case ControllerInput.Y: SplashPutBack(); break;
                case ControllerInput.LeftShoulder: SplashNextPart(-1); break;
                case ControllerInput.RightShoulder: SplashNextPart(1); break;
            }
        }
    }
}

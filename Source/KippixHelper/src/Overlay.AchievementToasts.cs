using System;
using System.Collections.Generic;
using System.Linq;
using LExpr = System.Linq.Expressions.Expression;
using LParam = System.Linq.Expressions.ParameterExpression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;

namespace KippixOverlay
{
    /// <summary>One unlock, as Kippix shows it.</summary>
    internal class AchToastItem
    {
        public string GameName, Name, Description, IconPath, RarityTier, TrophyType;
        public double? Percent;
        public int Unlocked, Total;
        public bool Completion, Hidden;
        public int? Style;   // (0.99.38) a design to show this one in (the previews), instead of the chosen one
    }

    /// <summary>
    /// (0.99.37) Kippix's own achievement pop-up, in a PlayStation 5 style: a dark rounded card in a corner (top right to start
    /// with), the achievement's picture, Playnite Achievements' rarity badge (bronze triangle common, silver square uncommon,
    /// gold pentagon rare, platinum hexagon ultra rare) with the rarity and % of players, the achievement's name and the game.
    ///
    /// Where they come from: Playnite Achievements' own unlock event (PlayniteAchievementsPlugin.AchievementUnlocked, static,
    /// raised on Playnite's UI thread by its in-game monitor), hooked through reflection so nothing breaks without it. Its own
    /// pop-ups are turned off in memory while this is on (EnableUnlockToasts / EnableProgressToasts in its Persisted settings;
    /// its sounds stay as they are), and back as they were when this is turned off. Nothing of its settings is saved by Kippix.
    /// Settings: OverlayConfig (Overlay settings › Achievement pop-ups, and the desktop Overlay tab).
    /// </summary>
    internal static class AchievementToasts
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static IPlayniteAPI api;
        private static Func<OverlayConfig> config;
        private static Func<IntPtr> anchor;
        private static Dispatcher ui;
        private static bool hooked;
        private static bool? paToastsWere, paProgressWere, paSoundsWere;
        private static readonly Queue<AchToastItem> queue = new Queue<AchToastItem>();
        private static AchToastWindow showing;
        private static AchToastItem current;          // (0.99.47) the one on screen (or that just went): the Guide button opens it in the overlay
        private static DateTime currentGone = DateTime.MinValue;

        /// <summary>
        /// (0.99.47) The overlay is opening: the achievement pop-up on screen (or gone less than 2 s ago), to show in the overlay;
        /// the pop-up goes. Null when there isn't one.
        /// </summary>
        internal static AchToastItem TakeForOverlay()
        {
            var it = current;
            if (it == null) return null;
            if (showing == null && (DateTime.Now - currentGone).TotalSeconds > 2) return null;
            current = null;
            var w = showing;
            if (w != null) { try { w.Close(); } catch (Exception) { } }
            return it;
        }

        public static void Start(IPlayniteAPI playnite, Func<OverlayConfig> cfg, Func<IntPtr> anchorWindow)
        {
            api = playnite;
            config = cfg;
            anchor = anchorWindow;
            ui = Application.Current != null ? Application.Current.Dispatcher : Dispatcher.CurrentDispatcher;
            Hook();
            Apply();
        }

        private static OverlayConfig Cfg { get { try { return config != null ? config() : null; } catch (Exception) { return null; } } }
        private static bool On { get { var c = Cfg; return c != null && c.AchToasts; } }

        /// <summary>Settings changed (or started): Playnite Achievements' own pop-ups off while ours are on, back otherwise.</summary>
        public static void Apply()
        {
            try
            {
                var c = Cfg;
                bool hide = c != null && c.AchToasts && c.AchHideOther;
                var p = PaPersisted();
                if (p == null) return;
                var t = p.GetType();
                var pt = t.GetProperty("EnableUnlockToasts");
                var pp = t.GetProperty("EnableProgressToasts");
                // (0.99.38) your own unlock sound: Playnite Achievements' sound off meanwhile (no two sounds at once)
                var ps = t.GetProperty("EnableUnlockSounds");
                bool mute = c != null && c.AchToasts && HasSound(c);
                if (ps != null)
                {
                    if (mute) { if (paSoundsWere == null) paSoundsWere = (bool)ps.GetValue(p, null); ps.SetValue(p, false, null); }
                    else if (paSoundsWere != null) { ps.SetValue(p, paSoundsWere.Value, null); paSoundsWere = null; }
                }
                if (hide)
                {
                    bool was = pt != null && (bool)pt.GetValue(p, null);
                    if (pt != null) { if (paToastsWere == null) paToastsWere = was; pt.SetValue(p, false, null); }
                    if (pp != null) { if (paProgressWere == null) paProgressWere = (bool)pp.GetValue(p, null); pp.SetValue(p, false, null); }
                    if (was) logger.Info("Kippix: achievement pop-ups: Kippix's own are on; Playnite Achievements' own pop-ups are off while Playnite runs");
                }
                else
                {
                    if (pt != null && paToastsWere != null) { pt.SetValue(p, paToastsWere.Value, null); paToastsWere = null; }
                    if (pp != null && paProgressWere != null) { pp.SetValue(p, paProgressWere.Value, null); paProgressWere = null; }
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: achievement pop-ups (Playnite Achievements' settings): " + ex.Message); }
        }

        private static Type PaPluginType()
        {
            var plugin = api != null ? api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "PlayniteAchievements.PlayniteAchievementsPlugin") : null;
            return plugin != null ? plugin.GetType() : null;
        }

        private static object PaPersisted()
        {
            // (asked each time: Playnite Achievements can swap its settings object when they're saved)
            var plugin = api != null ? api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "PlayniteAchievements.PlayniteAchievementsPlugin") : null;
            if (plugin == null) return null;
            var sp = plugin.GetType().GetProperty("Settings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var settings = sp != null ? sp.GetValue(plugin, null) : null;
            var pp = settings != null ? settings.GetType().GetProperty("Persisted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
            return pp != null ? pp.GetValue(settings, null) : null;
        }

        /// <summary>Subscribes to Playnite Achievements' unlock event (once).</summary>
        private static void Hook()
        {
            if (hooked) return;
            try
            {
                var t = PaPluginType();
                if (t == null) { logger.Info("Kippix: achievement pop-ups: Playnite Achievements isn't installed (they need it)"); return; }
                var ev = t.GetEvent("AchievementUnlocked", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ev == null) { logger.Warn("Kippix: achievement pop-ups: Playnite Achievements has no AchievementUnlocked event"); return; }
                // (sender, args) => OnUnlocked(args), whatever the delegate's own parameter types
                var invoke = ev.EventHandlerType.GetMethod("Invoke");
                var ps = invoke.GetParameters().Select(p => LExpr.Parameter(p.ParameterType, p.Name)).ToArray();
                var target = typeof(AchievementToasts).GetMethod("OnUnlocked", BindingFlags.Static | BindingFlags.NonPublic);
                System.Linq.Expressions.Expression argExpr = ps.Length > 0 ? (System.Linq.Expressions.Expression)LExpr.Convert(ps[ps.Length - 1], typeof(object)) : LExpr.Constant(null);
                var body = LExpr.Call(target, argExpr);
                var del = LExpr.Lambda(ev.EventHandlerType, body, ps).Compile();
                var add = ev.GetAddMethod(true);
                add.Invoke(add.IsStatic ? null : api.Addons.Plugins.First(p => p.GetType() == t), new object[] { del });
                hooked = true;
                logger.Info("Kippix: achievement pop-ups: listening to Playnite Achievements' unlocks");
            }
            catch (Exception ex) { logger.Warn("Kippix: achievement pop-ups: couldn't listen to Playnite Achievements: " + ex.Message); }
        }

        private static object P(object o, string name)
        {
            try { var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); return p != null ? p.GetValue(o, null) : null; }
            catch (Exception) { return null; }
        }

        private static void OnUnlocked(object e)
        {
            try
            {
                var c = Cfg;
                if (e == null || c == null || !c.AchToasts) return;
                bool progress = P(e, "IsProgressUpdate") is bool && (bool)P(e, "IsProgressUpdate");
                if (progress && !c.AchToastProgress) return;
                object pct = P(e, "GlobalPercent");
                var it = new AchToastItem
                {
                    GameName = P(e, "GameName") as string,
                    Name = (P(e, "DisplayName") as string) ?? (P(e, "ApiName") as string) ?? "",
                    Description = P(e, "Description") as string,
                    IconPath = P(e, "IconPath") as string,
                    RarityTier = Convert.ToString(P(e, "RarityTier")),
                    TrophyType = P(e, "TrophyType") as string,
                    Percent = pct is double ? (double?)(double)pct : null,
                    Completion = P(e, "IsCompletionAchievement") is bool && (bool)P(e, "IsCompletionAchievement"),
                    Hidden = P(e, "IsHidden") is bool && (bool)P(e, "IsHidden")
                };
                // the picture: Playnite Achievements keeps its icons under its own data folder (icon_cache\...)
                it.IconPath = FullPath(it.IconPath) ?? FullPath(P(e, "GameIconPath") as string) ?? FullPath(P(e, "GameCoverPath") as string);
                object u = P(e, "UnlockedCount"), tot = P(e, "TotalCount");
                if (u is int) it.Unlocked = (int)u;
                if (tot is int) it.Total = (int)tot;
                if (progress)
                {
                    object pn = P(e, "ProgressNum"), pd = P(e, "ProgressDenom");
                    if (pn is int && pd is int) it.Description = "Progress " + pn + " / " + pd;
                }
                Show(it);
            }
            catch (Exception ex) { logger.Warn("Kippix: achievement pop-up: " + ex.Message); }
        }

        private static string paDataDir;

        /// <summary>A full path (or URL) for one of Playnite Achievements' pictures, null when it isn't there.</summary>
        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim();
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
                if (!System.IO.Path.IsPathRooted(path))
                {
                    if (paDataDir == null)
                    {
                        var plugin = api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "PlayniteAchievements.PlayniteAchievementsPlugin");
                        try { paDataDir = plugin != null ? plugin.GetPluginUserDataPath() : ""; } catch (Exception) { paDataDir = ""; }
                    }
                    if (paDataDir.Length == 0) return null;
                    path = System.IO.Path.Combine(paDataDir, path);
                }
                return System.IO.File.Exists(path) ? path : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A sample for the settings' "Show a test pop-up" (in the chosen design).</summary>
        public static void ShowTest()
        {
            Show(Sample(null, 0));
        }

        /// <summary>(0.99.38) One sample in each of the seven designs, one after another (each named, a different rarity each).</summary>
        public static void ShowAllDesigns()
        {
            for (int i = 0; i < AchToastWindow.StyleNames.Length; i++) Show(Sample(i, i));
        }

        private static AchToastItem Sample(int? style, int n)
        {
            string[] tiers = { "UltraRare", "Rare", "Uncommon", "Common" };
            double[] pcts = { 2.4, 8.7, 31, 64 };
            return new AchToastItem
            {
                GameName = "Kippix",
                Name = style.HasValue ? "Design: " + AchToastWindow.StyleNames[style.Value] : "Trophy hunter",
                Description = "Unlock 12 achievements while playing",
                RarityTier = tiers[n % 4], Percent = pcts[n % 4], Unlocked = 12, Total = 40, Style = style,
                IconPath = "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png"
            };
        }

        // ---------- (0.99.38) your own unlock sound ----------

        private static MediaPlayer player;

        internal static bool HasSound(OverlayConfig c)
        {
            try { return !string.IsNullOrWhiteSpace(c.AchSoundFile) && System.IO.File.Exists(c.AchSoundFile); } catch (Exception) { return false; }
        }

        /// <summary>Plays a sound file (wav, mp3, wma...) at a volume (0-100), on Playnite's UI thread; nothing when it can't.</summary>
        public static void PlaySound(string file, int volume)
        {
            Action go = () =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) return;
                    if (player == null)
                    {
                        player = new MediaPlayer();
                        player.MediaFailed += (s, e) => logger.Warn("Kippix: achievement sound: " + (e.ErrorException != null ? e.ErrorException.Message : "couldn't play it"));
                    }
                    player.Stop();
                    player.Close();
                    player.Volume = Math.Max(0, Math.Min(100, volume)) / 100.0;
                    player.Open(new Uri(System.IO.Path.GetFullPath(file)));
                    player.Play();
                }
                catch (Exception ex) { logger.Warn("Kippix: achievement sound: " + ex.Message); }
            };
            var d = ui ?? (Application.Current != null ? Application.Current.Dispatcher : null);
            if (d == null || d.CheckAccess()) go(); else d.BeginInvoke(go);
        }

        private static void Show(AchToastItem it)
        {
            Action go = () =>
            {
                queue.Enqueue(it);
                if (showing == null) Next();
            };
            if (ui == null || ui.CheckAccess()) go(); else ui.BeginInvoke(go);
        }

        private static void Next()
        {
            if (queue.Count == 0) { showing = null; return; }
            var it = queue.Dequeue();
            var c = Cfg ?? new OverlayConfig();
            IntPtr a = IntPtr.Zero;
            try { a = anchor != null ? anchor() : IntPtr.Zero; } catch (Exception) { }
            if (a == IntPtr.Zero) a = Native.GetForegroundWindow();
            try
            {
                int secs = it.Style.HasValue ? Math.Min(4, Math.Max(3, c.AchToastSeconds)) : Math.Max(3, Math.Min(12, c.AchToastSeconds));   // (the previews go quicker)
                var w = new AchToastWindow(it, a, c.AchToastPosition, secs, it.Style ?? c.AchToastStyle, c.Theme);
                showing = w;
                current = it;
                w.Closed += (s, e) => { if (ReferenceEquals(showing, w)) { showing = null; currentGone = DateTime.Now; Next(); } };
                w.Show();
                if (HasSound(c)) PlaySound(c.AchSoundFile, c.AchSoundVolume);
            }
            catch (Exception ex) { showing = null; logger.Warn("Kippix: achievement pop-up window: " + ex.Message); }
        }

        /// <summary>Playnite Achievements' rarity badge for a tier (its own pictures, from Playnite's resources), or null.</summary>
        internal static ImageSource RarityBadge(string tier, bool completion)
        {
            string t = (tier ?? "").Replace(" ", "").ToLowerInvariant();
            string key = completion ? "BadgeCompletedGame"
                       : t.Contains("ultra") ? "BadgePlatinumHexagon"
                       : t.Contains("uncommon") ? "BadgeSilverSquare"
                       : t.Contains("rare") ? "BadgeGoldPentagon"
                       : "BadgeBronzeTriangle";
            try
            {
                var app = Application.Current;
                var img = app != null ? app.TryFindResource(key) as ImageSource : null;
                if (img == null)
                {
                    if (badges == null)
                    {
                        try { badges = new ResourceDictionary { Source = new Uri("pack://application:,,,/PlayniteAchievements;component/Resources/RarityBadges.xaml", UriKind.Absolute) }; }
                        catch (Exception) { badges = new ResourceDictionary(); }
                    }
                    img = badges.Contains(key) ? badges[key] as ImageSource : null;
                }
                return img;
            }
            catch (Exception) { return null; }
        }
        private static ResourceDictionary badges;

        internal static string RarityName(string tier, bool completion)
        {
            if (completion) return "Every achievement";
            string t = (tier ?? "").Replace(" ", "").ToLowerInvariant();
            if (t.Contains("ultra")) return "Ultra rare";
            if (t.Contains("uncommon")) return "Uncommon";
            if (t.Contains("rare")) return "Rare";
            if (t.Contains("common")) return "Common";
            return "";
        }
    }

    /// <summary>
    /// The pop-up itself: never takes the focus or clicks, left out of screenshots (FloatingWindow), slides in and fades out.
    /// (0.99.38) Seven designs (StyleNames, OverlayConfig.AchToastStyle), and Bottom middle.
    /// </summary>
    internal class AchToastWindow : FloatingWindow
    {
        public static readonly string[] PositionNames = { "Top right", "Top left", "Bottom right", "Bottom left", "Top middle", "Bottom middle" };
        public static readonly string[] StyleNames = { "Classic", "Glass", "Rarity edge", "Pill", "Banner", "Spotlight", "Kippix" };

        private readonly AchToastItem it;
        private readonly Color rare;
        private readonly ImageSource badge, icon;
        private readonly string rarity;

        public AchToastWindow(AchToastItem item, IntPtr anchorHwnd, int position, int seconds, int style, int themeIndex) : base(anchorHwnd, true)
        {
            it = item;
            rare = RarityColor(it.RarityTier, it.Completion);
            badge = AchievementToasts.RarityBadge(it.RarityTier, it.Completion);
            icon = LoadIcon(it.IconPath);
            rarity = AchievementToasts.RarityName(it.RarityTier, it.Completion);
            position = OverlayConfig.Wrap(position, PositionNames.Length);

            FrameworkElement card;
            switch (OverlayConfig.Wrap(style, StyleNames.Length))
            {
                case 1: card = Glass(); break;
                case 2: card = Edge(); break;
                case 3: card = Pill(); break;
                case 4: card = Banner(); break;
                case 5: card = Spotlight(); break;
                case 6: card = Kippix(OverlayTheme.Get(themeIndex)); break;
                default: card = Classic(); break;
            }
            card.Margin = new Thickness(26);
            Content = card;

            Opacity = 0;
            bool left = position == 1 || position == 3, middle = position >= 4, bottom = position == 2 || position == 3 || position == 5;
            var slide = new TranslateTransform(middle ? 0 : left ? -44 : 44, middle ? (bottom ? 34 : -34) : 0);
            var pop = new ScaleTransform(0.94, 0.94);
            var tg = new TransformGroup();
            tg.Children.Add(pop);
            tg.Children.Add(slide);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = tg;
            Loaded += (s, e) =>
            {
                double w = ActualWidth, h = ActualHeight, mw = Monitor.Width / Scale, mh = Monitor.Height / Scale, ml = Monitor.Left / Scale, mt = Monitor.Top / Scale;
                Left = middle ? ml + (mw - w) / 2 : left ? ml + 8 : ml + mw - w - 8;
                Top = bottom ? mt + mh - h - 8 : mt + 8;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slide.X, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slide.Y, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(460)) { EasingFunction = back });
                pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(460)) { EasingFunction = back });
            };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320));
                fade.Completed += (s2, e2) => { try { Close(); } catch (Exception) { } };
                BeginAnimation(OpacityProperty, fade);
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, bottom ? 14 : -14, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            };
            timer.Start();
        }

        // ---------- the seven designs ----------

        /// <summary>0: a calm dark card (the PlayStation feel): picture, a small rarity line, the name, the game.</summary>
        private FrameworkElement Classic()
        {
            var card = Card(440, 14, new LinearGradientBrush(C(0xF4, 0x26, 0x28, 0x2E), C(0xF4, 0x17, 0x18, 0x1C), 90), Solid(0x1F, 0xFF, 0xFF, 0xFF), 1, new Thickness(12));
            card.Effect = Shadow(Colors.Black, 26, 6, 0.55);
            var g = Columns(GridLength.Auto, Star(), GridLength.Auto);
            Add(g, Picture(64, 10, null), 0);
            var t = Stack(new Thickness(14, 0, 8, 0));
            t.Children.Add(Text("Achievement unlocked", 12, Solid(0x9A, 0xFF, 0xFF, 0xFF), FontWeights.Normal));
            t.Children.Add(Text(it.Name, 18, Brushes.White, FontWeights.SemiBold, new Thickness(0, 2, 0, 0)));
            AddDesc(t, 13, Solid(0xC0, 0xFF, 0xFF, 0xFF), new Thickness(0, 2, 0, 0), HorizontalAlignment.Left);
            if (!string.IsNullOrEmpty(it.GameName)) t.Children.Add(Text(it.GameName, 12.5, Solid(0x80, 0xFF, 0xFF, 0xFF), FontWeights.Normal, new Thickness(0, 2, 0, 0)));
            Add(g, t, 1);
            var r = Stack(new Thickness(0)); r.HorizontalAlignment = HorizontalAlignment.Center;
            if (badge != null) r.Children.Add(new Image { Source = badge, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
            string pc = Pct();
            if (pc.Length > 0) r.Children.Add(Text(pc, 12, Solid(0xB0, 0xFF, 0xFF, 0xFF), FontWeights.SemiBold, new Thickness(0, 3, 0, 0), HorizontalAlignment.Center));
            Add(g, r, 2);
            card.Child = g;
            return card;
        }

        /// <summary>1: frosted glass: a light see-through card with a bright top edge, the rarity in its colour.</summary>
        private FrameworkElement Glass()
        {
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bg.GradientStops.Add(new GradientStop(C(0xC8, 0x5A, 0x60, 0x70), 0));
            bg.GradientStops.Add(new GradientStop(C(0xC8, 0x2A, 0x2E, 0x3A), 0.55));
            bg.GradientStops.Add(new GradientStop(C(0xD8, 0x1A, 0x1C, 0x24), 1));
            var edge = new LinearGradientBrush(C(0x70, 0xFF, 0xFF, 0xFF), C(0x14, 0xFF, 0xFF, 0xFF), 90);
            var card = Card(460, 22, bg, edge, 1.2, new Thickness(14));
            card.Effect = Shadow(Colors.Black, 34, 8, 0.45);
            var g = Columns(GridLength.Auto, Star());
            var pic = Picture(70, 16, Solid(0x40, 0xFF, 0xFF, 0xFF));
            Add(g, pic, 0);
            var t = Stack(new Thickness(16, 0, 6, 0));
            var top = Row();
            if (badge != null) top.Children.Add(new Image { Source = badge, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(Text(Head(false), 13, new SolidColorBrush(Lighten(rare, 0.35)), FontWeights.SemiBold));
            t.Children.Add(top);
            t.Children.Add(Text(it.Name, 20, Brushes.White, FontWeights.SemiBold, new Thickness(0, 3, 0, 0)));
            AddDesc(t, 13.5, Solid(0xD8, 0xFF, 0xFF, 0xFF), new Thickness(0, 2, 0, 0), HorizontalAlignment.Left);
            t.Children.Add(Text(Sub(), 13, Solid(0xB8, 0xFF, 0xFF, 0xFF), FontWeights.Normal, new Thickness(0, 2, 0, 0)));
            Add(g, t, 1);
            card.Child = g;
            return card;
        }

        /// <summary>2: a dark card with a rarity coloured edge and glow, a big badge with the % of players.</summary>
        private FrameworkElement Edge()
        {
            var card = Card(460, 10, Solid(0xF5, 0x12, 0x13, 0x16), Solid(0x30, rare.R, rare.G, rare.B), 1, new Thickness(0));
            card.Effect = Shadow(rare, 30, 0, 0.42);
            card.ClipToBounds = false;
            var g = Columns(new GridLength(5), GridLength.Auto, Star(), GridLength.Auto);
            Add(g, new Border { Background = new LinearGradientBrush(rare, Darken(rare, 0.45), 90), CornerRadius = new CornerRadius(10, 0, 0, 10) }, 0);
            var pic = Picture(60, 8, null); pic.Margin = new Thickness(12, 12, 0, 12);
            Add(g, pic, 1);
            var t = Stack(new Thickness(14, 0, 8, 0));
            t.Children.Add(Text(Spaced(rarity.Length > 0 ? rarity.ToUpperInvariant() : "UNLOCKED"), 11, new SolidColorBrush(rare), FontWeights.Bold));
            t.Children.Add(Text(it.Name, 18, Brushes.White, FontWeights.SemiBold, new Thickness(0, 3, 0, 0)));
            AddDesc(t, 13, Solid(0xB8, 0xFF, 0xFF, 0xFF), new Thickness(0, 2, 0, 0), HorizontalAlignment.Left);
            t.Children.Add(Text(Sub(), 12.5, Solid(0x88, 0xFF, 0xFF, 0xFF), FontWeights.Normal, new Thickness(0, 2, 0, 0)));
            Add(g, t, 2);
            var r = Stack(new Thickness(0, 0, 16, 0)); r.MinWidth = 52;
            if (badge != null) r.Children.Add(new Image { Source = badge, Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center });
            string pc = Pct();
            if (pc.Length > 0) r.Children.Add(Text(pc, 12, new SolidColorBrush(Lighten(rare, 0.25)), FontWeights.SemiBold, new Thickness(0, 4, 0, 0), HorizontalAlignment.Center));
            Add(g, r, 3);
            card.Child = g;
            return card;
        }

        /// <summary>3: a small one-line pill: round picture, the name, the rarity and %.</summary>
        private FrameworkElement Pill()
        {
            var card = Card(double.NaN, 30, Solid(0xF2, 0x10, 0x11, 0x14), Solid(0x90, rare.R, rare.G, rare.B), 1.2, new Thickness(7, 7, 18, 7));
            card.MaxWidth = 560;
            card.Effect = Shadow(Colors.Black, 20, 4, 0.5);
            var g = Columns(GridLength.Auto, Star(), GridLength.Auto);
            var circle = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Solid(0x22, 0xFF, 0xFF, 0xFF), BorderBrush = new SolidColorBrush(rare), BorderThickness = new Thickness(1.5) };
            if (icon != null) circle.Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
            else if (badge != null) circle.Child = new Image { Source = badge, Margin = new Thickness(9) };
            Add(g, circle, 0);
            var t = Row(); t.VerticalAlignment = VerticalAlignment.Center;
            var name = Text(it.Name, 15.5, Brushes.White, FontWeights.SemiBold); name.MaxWidth = 300;
            t.Children.Add(name);
            string pc = Pct();
            string tail = rarity + (pc.Length > 0 ? (rarity.Length > 0 ? " · " : "") + pc : "");
            if (tail.Length > 0) t.Children.Add(Text("   " + tail, 13, Solid(0x99, 0xFF, 0xFF, 0xFF), FontWeights.Normal));
            var lines = Stack(new Thickness(12, 0, 10, 0));
            lines.Children.Add(t);
            var d = AddDesc(lines, 12.5, Solid(0xC0, 0xFF, 0xFF, 0xFF), new Thickness(0, 1, 0, 0), HorizontalAlignment.Left);
            if (d != null) { d.MaxWidth = 440; d.TextWrapping = TextWrapping.NoWrap; d.MaxHeight = double.PositiveInfinity; }   // (one line: it's a pill)
            Add(g, lines, 1);
            if (badge != null) Add(g, new Image { Source = badge, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center }, 2);
            card.Child = g;
            return card;
        }

        /// <summary>4: a wide banner that fades out at both ends, the name big in the middle.</summary>
        private FrameworkElement Banner()
        {
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            bg.GradientStops.Add(new GradientStop(C(0x00, 0x0C, 0x0D, 0x10), 0));
            bg.GradientStops.Add(new GradientStop(C(0xF0, 0x0C, 0x0D, 0x10), 0.18));
            bg.GradientStops.Add(new GradientStop(C(0xF0, 0x0C, 0x0D, 0x10), 0.82));
            bg.GradientStops.Add(new GradientStop(C(0x00, 0x0C, 0x0D, 0x10), 1));
            var card = new Border { Width = 680, Background = bg, Padding = new Thickness(90, 14, 90, 0) };
            var all = new StackPanel();
            var g = Columns(GridLength.Auto, Star());
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var pic = Picture(56, 28, null); pic.BorderBrush = new SolidColorBrush(rare); pic.BorderThickness = new Thickness(2);
            Add(g, pic, 0);
            var t = Stack(new Thickness(16, 0, 0, 0));
            t.Children.Add(Text(Spaced("ACHIEVEMENT UNLOCKED"), 11, Solid(0xA0, 0xFF, 0xFF, 0xFF), FontWeights.SemiBold));
            t.Children.Add(Text(it.Name, 24, Brushes.White, FontWeights.Bold, new Thickness(0, 1, 0, 0)));
            var bd = AddDesc(t, 14, Solid(0xD0, 0xFF, 0xFF, 0xFF), new Thickness(0, 2, 0, 0), HorizontalAlignment.Left);
            if (bd != null) bd.MaxWidth = 420;
            var sub = Row(); sub.Margin = new Thickness(0, 2, 0, 0);
            if (badge != null) sub.Children.Add(new Image { Source = badge, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            string pc = Pct();
            string line = rarity + (pc.Length > 0 ? (rarity.Length > 0 ? " · " : "") + pc : "");
            if (!string.IsNullOrEmpty(it.GameName)) line += (line.Length > 0 ? "   ·   " : "") + it.GameName;
            sub.Children.Add(Text(line, 13, Solid(0xA8, 0xFF, 0xFF, 0xFF), FontWeights.Normal));
            t.Children.Add(sub);
            Add(g, t, 1);
            all.Children.Add(g);
            var lineBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            lineBrush.GradientStops.Add(new GradientStop(C(0, rare.R, rare.G, rare.B), 0));
            lineBrush.GradientStops.Add(new GradientStop(rare, 0.5));
            lineBrush.GradientStops.Add(new GradientStop(C(0, rare.R, rare.G, rare.B), 1));
            all.Children.Add(new Border { Height = 2, Background = lineBrush, Margin = new Thickness(-60, 14, -60, 0) });
            card.Child = all;
            return card;
        }

        /// <summary>5: a tall card: the picture in a rarity coloured glow, "Achievement unlocked", the name and what it's for.</summary>
        private FrameworkElement Spotlight()
        {
            var card = Card(340, 24, new LinearGradientBrush(C(0xF6, 0x1E, 0x20, 0x26), C(0xF6, 0x10, 0x11, 0x14), 90), Solid(0x22, 0xFF, 0xFF, 0xFF), 1, new Thickness(22, 18, 22, 20));
            card.Effect = Shadow(Colors.Black, 36, 8, 0.55);
            var all = new StackPanel();
            var stage = new Grid { Height = 132, Margin = new Thickness(0, 0, 0, 6) };
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(C(0xA0, rare.R, rare.G, rare.B), 0));
            glow.GradientStops.Add(new GradientStop(C(0x30, rare.R, rare.G, rare.B), 0.45));
            glow.GradientStops.Add(new GradientStop(C(0, rare.R, rare.G, rare.B), 1));
            stage.Children.Add(new System.Windows.Shapes.Ellipse { Width = 210, Height = 150, Fill = glow, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var pic = Picture(92, 20, null);
            pic.HorizontalAlignment = HorizontalAlignment.Center; pic.VerticalAlignment = VerticalAlignment.Center;
            pic.BorderBrush = Solid(0x60, 0xFF, 0xFF, 0xFF); pic.BorderThickness = new Thickness(1.5);
            stage.Children.Add(pic);
            if (badge != null) stage.Children.Add(new Image { Source = badge, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(80, 0, 0, 6) });
            all.Children.Add(stage);
            all.Children.Add(Text(Spaced("ACHIEVEMENT UNLOCKED"), 11, new SolidColorBrush(Lighten(rare, 0.3)), FontWeights.SemiBold, new Thickness(0), HorizontalAlignment.Center));
            var name = Text(it.Name, 21, Brushes.White, FontWeights.Bold, new Thickness(0, 4, 0, 0), HorizontalAlignment.Center);
            name.TextWrapping = TextWrapping.Wrap; name.TextAlignment = TextAlignment.Center; name.TextTrimming = TextTrimming.None;
            all.Children.Add(name);
            if (!string.IsNullOrWhiteSpace(it.Description))   // (0.99.47: hidden ones too: it's unlocked now)
            {
                var d = Text(it.Description, 13, Solid(0x9C, 0xFF, 0xFF, 0xFF), FontWeights.Normal, new Thickness(0, 4, 0, 0), HorizontalAlignment.Center);
                d.TextWrapping = TextWrapping.Wrap; d.TextAlignment = TextAlignment.Center; d.MaxHeight = 36;
                all.Children.Add(d);
            }
            all.Children.Add(new Border { Height = 1, Background = Solid(0x1E, 0xFF, 0xFF, 0xFF), Margin = new Thickness(10, 12, 10, 10) });
            string pc = Pct();
            string rl = rarity + (pc.Length > 0 ? (rarity.Length > 0 ? " · " : "") + pc + " of players" : "");
            if (rl.Length > 0) all.Children.Add(Text(rl, 13, new SolidColorBrush(Lighten(rare, 0.45)), FontWeights.SemiBold, new Thickness(0), HorizontalAlignment.Center));
            all.Children.Add(Text(Sub(), 12.5, Solid(0x88, 0xFF, 0xFF, 0xFF), FontWeights.Normal, new Thickness(0, 3, 0, 0), HorizontalAlignment.Center));
            card.Child = all;
            return card;
        }

        /// <summary>6: the overlay's own colours: a gradient edge, the rarity in the accent colour and a bar for how far through the game's list you are.</summary>
        private FrameworkElement Kippix(OverlayTheme th)
        {
            var outer = new Border { Width = 464, CornerRadius = new CornerRadius(18), Background = th.Gradient, Padding = new Thickness(1.6), Effect = th.Glow(0.45) };
            var card = Card(double.NaN, 16.5, th.Panel, null, 0, new Thickness(14, 14, 16, 14));
            outer.Child = card;
            var all = new StackPanel();
            var g = Columns(GridLength.Auto, Star());
            Add(g, Picture(70, 12, null), 0);
            var t = Stack(new Thickness(15, 0, 4, 0));
            var top = Row();
            if (badge != null) top.Children.Add(new Image { Source = badge, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(Text(Head(false), 13, th.Gradient, FontWeights.SemiBold));
            t.Children.Add(top);
            t.Children.Add(Text(it.Name, 19.5, OverlayTheme.TextMain, FontWeights.SemiBold, new Thickness(0, 3, 0, 0)));
            AddDesc(t, 13.5, OverlayTheme.TextSub, new Thickness(0, 2, 0, 0), HorizontalAlignment.Left);
            if (!string.IsNullOrEmpty(it.GameName)) t.Children.Add(Text(it.GameName, 13, OverlayTheme.TextDim, FontWeights.Normal, new Thickness(0, 2, 0, 0)));
            Add(g, t, 1);
            all.Children.Add(g);
            if (it.Total > 0)
            {
                double f = Math.Max(0, Math.Min(1, it.Unlocked / (double)it.Total));
                var bar = Columns(Star(), GridLength.Auto);
                bar.Margin = new Thickness(0, 12, 0, 0);
                var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = OverlayTheme.TrackBg });
                var fill = Columns(new GridLength(Math.Max(0.001, f), GridUnitType.Star), new GridLength(Math.Max(0.001, 1 - f), GridUnitType.Star));
                fill.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = th.Gradient });
                track.Children.Add(fill);
                Add(bar, track, 0);
                Add(bar, Text(it.Unlocked + " of " + it.Total + "  ·  " + Math.Round(f * 100) + "%", 12, OverlayTheme.TextSub, FontWeights.Normal, new Thickness(12, 0, 0, 0)), 1);
                all.Children.Add(bar);
            }
            card.Child = all;
            return outer;
        }

        // ---------- pieces ----------

        /// <summary>(0.99.47) What the achievement is for, under its name in every design: up to two lines.</summary>
        private TextBlock AddDesc(Panel into, double size, Brush fg, Thickness margin, HorizontalAlignment align)
        {
            if (string.IsNullOrWhiteSpace(it.Description)) return null;
            var d = Text(it.Description.Trim(), size, fg, FontWeights.Normal, margin, align);
            d.TextWrapping = TextWrapping.Wrap;
            d.MaxHeight = Math.Ceiling(size * 1.36 * 2) + 1;
            into.Children.Add(d);
            return d;
        }

        private string Pct()
        {
            if (!it.Percent.HasValue || it.Completion) return "";
            double p = it.Percent.Value;
            return p.ToString(p < 10 ? "0.0" : "0", System.Globalization.CultureInfo.CurrentCulture) + "%";
        }

        private string Head(bool withPct)
        {
            string h = it.Completion ? "Every achievement unlocked" : (rarity.Length > 0 ? rarity + " achievement" : "Achievement unlocked");
            string pc = Pct();
            if (pc.Length > 0) h += "  ·  " + pc;
            return h;
        }

        private string Sub() { return Sub(false); }

        private string Sub(bool rarityToo)
        {
            string s = it.GameName ?? "";
            if (rarityToo)
            {
                string pc = Pct();
                string r = rarity + (pc.Length > 0 ? (rarity.Length > 0 ? " · " : "") + pc + " of players" : "");
                if (r.Length > 0) s = r + (s.Length > 0 ? "   ·   " + s : "");
            }
            if (it.Total > 0) s += (s.Length > 0 ? "  ·  " : "") + it.Unlocked + " of " + it.Total;
            return s;
        }

        /// <summary>The achievement's picture in a rounded square (its rarity badge when there isn't one).</summary>
        private Border Picture(double size, double radius, Brush border)
        {
            var pic = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Solid(0x22, 0xFF, 0xFF, 0xFF), VerticalAlignment = VerticalAlignment.Center };
            if (border != null) { pic.BorderBrush = border; pic.BorderThickness = new Thickness(1); }
            if (icon != null) pic.Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
            else if (badge != null) pic.Child = new Image { Source = badge, Margin = new Thickness(size / 5) };
            return pic;
        }

        private static Border Card(double width, double radius, Brush bg, Brush border, double borderWidth, Thickness padding)
        {
            var b = new Border { CornerRadius = new CornerRadius(radius), Background = bg, Padding = padding };
            if (!double.IsNaN(width)) b.Width = width;
            if (border != null) { b.BorderBrush = border; b.BorderThickness = new Thickness(borderWidth); }
            return b;
        }

        private static Grid Columns(params GridLength[] widths)
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        private static void Add(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static GridLength Star() { return new GridLength(1, GridUnitType.Star); }

        private static StackPanel Stack(Thickness margin) { return new StackPanel { Margin = margin, VerticalAlignment = VerticalAlignment.Center }; }

        private static StackPanel Row() { return new StackPanel { Orientation = Orientation.Horizontal }; }

        private static TextBlock Text(string text, double size, Brush fg, FontWeight weight)
        {
            return Text(text, size, fg, weight, new Thickness(0));
        }

        private static TextBlock Text(string text, double size, Brush fg, FontWeight weight, Thickness margin)
        {
            return Text(text, size, fg, weight, margin, HorizontalAlignment.Left);
        }

        private static TextBlock Text(string text, double size, Brush fg, FontWeight weight, Thickness margin, HorizontalAlignment align)
        {
            return new TextBlock { Text = text ?? "", FontSize = size, Foreground = fg, FontWeight = weight, Margin = margin, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        /// <summary>Letter-spaced capitals (WPF has no letter spacing): a hair space between letters.</summary>
        private static string Spaced(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s) { if (sb.Length > 0) sb.Append(' '); sb.Append(ch == ' ' ? " " : ch.ToString()); }
            return sb.ToString();
        }

        private static Color C(byte a, byte r, byte g, byte b) { return Color.FromArgb(a, r, g, b); }

        private static Brush Solid(byte a, byte r, byte g, byte b) { return new SolidColorBrush(Color.FromArgb(a, r, g, b)); }

        private static System.Windows.Media.Effects.DropShadowEffect Shadow(Color c, double blur, double depth, double opacity)
        {
            return new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = blur, ShadowDepth = depth, Opacity = opacity, Direction = 270 };
        }

        private static Color Lighten(Color c, double t) { return Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t)); }

        private static Color Darken(Color c, double t) { return Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t))); }

        /// <summary>The badge's colour: bronze, silver, gold, platinum (gold for every achievement).</summary>
        internal static Color RarityColor(string tier, bool completion)
        {
            string t = (tier ?? "").Replace(" ", "").ToLowerInvariant();
            if (completion) return Color.FromRgb(0xFF, 0xD5, 0x4A);
            if (t.Contains("ultra")) return Color.FromRgb(0xA7, 0xE3, 0xFF);
            if (t.Contains("uncommon")) return Color.FromRgb(0xC0, 0xC7, 0xD1);
            if (t.Contains("rare")) return Color.FromRgb(0xE8, 0xB9, 0x31);
            return Color.FromRgb(0xCD, 0x7F, 0x32);
        }

        private static ImageSource LoadIcon(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 192;
                bmp.UriSource = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || path.StartsWith("pack:", StringComparison.OrdinalIgnoreCase) ? new Uri(path) : new Uri(System.IO.Path.GetFullPath(path));
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                return bmp;
            }
            catch (Exception) { return null; }
        }
    }
}

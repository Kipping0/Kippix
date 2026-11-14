using System;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.7) Home settings › Background › Background change speed: how long after you move to another game the home
    /// background changes (0–400 ms, 150 by default). Playnite's FadeImage waits SourceUpdateDelay ms before it swaps the picture,
    /// and Playnite puts it back to 300 whenever it sets the background up again, so the tick puts ours back on.
    /// </summary>
    public partial class ThemeApi
    {
        public const int BgChangeMax = 10000;   // (0.99.18: up to 10 s)
        // (0.99.18) the slider's stops: fine steps while it's short, bigger ones up to 10 seconds
        private static readonly int[] BgChangeStops = { 0, 50, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 3000, 5000, 7500, 10000 };
        private int BgChangeIndex
        {
            get
            {
                int best = 0;
                for (int i = 0; i < BgChangeStops.Length; i++)
                    if (Math.Abs(BgChangeStops[i] - settings.BgChangeDelay) < Math.Abs(BgChangeStops[best] - settings.BgChangeDelay)) best = i;
                return best;
            }
        }
        private const double BgChangeTrack = 480;
        private bool bgDelayLogged, bgFoundLogged;
        private DateTime bgFindAt = DateTime.MinValue;
        private object cfaSettings;        // Custom Fade Animations' own settings (CustomFadeAnimSettings), while it's installed
        private bool cfaLooked, cfaLogged;

        /// <summary>
        /// (0.99.15) The "Custom Fade Animations" add-on sets its own SourceUpdateDelay on the background each time the picture
        /// changes, so ours never lasted. While Kippix runs, its in-memory value follows ours (its saved settings aren't touched:
        /// open its settings and save them and they're as they were, until Kippix puts ours back).
        /// </summary>
        private void CfaSync()
        {
            try
            {
                if (!cfaLooked)
                {
                    cfaLooked = true;
                    foreach (var plug in api.Addons.Plugins)
                    {
                        if (plug == null || plug.GetType().Name != "CustomFadeAnim") continue;
                        var f = plug.GetType().GetField("settings", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        object vm = f != null ? f.GetValue(plug) : null;
                        var sp = vm != null ? vm.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance) : null;
                        cfaSettings = sp != null ? sp.GetValue(vm, null) : null;
                        break;
                    }
                }
                if (cfaSettings == null) return;
                var p = cfaSettings.GetType().GetProperty("SourceUpdateDelay", BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return;
                double want = Math.Max(0, Math.Min(BgChangeMax, settings.BgChangeDelay));
                double was = Convert.ToDouble(p.GetValue(cfaSettings, null));
                if (Math.Abs(was - want) < 0.5) return;
                p.SetValue(cfaSettings, Convert.ChangeType(want, p.PropertyType, System.Globalization.CultureInfo.InvariantCulture), null);
                if (!cfaLogged) { cfaLogged = true; logger.Info("Kippix: background change speed: Custom Fade Animations had " + was + " ms, now " + want + " ms (while Kippix runs)"); }
            }
            catch (Exception ex) { if (!cfaLogged) { cfaLogged = true; logger.Warn("Kippix: background change speed (Custom Fade Animations): " + ex.Message); } }
        }

        public string BgChangeDelayText
        {
            get
            {
                int d = settings.BgChangeDelay;
                if (d == 0) return Loc("LOC_KX_BgChangeNow", "Straight away");
                if (d < 1000) return string.Format(Loc("LOC_KX_Ms", "{0} ms"), d);
                return string.Format(Loc("LOC_KX_SecondsDec", "{0} s"), (d / 1000.0).ToString(d % 1000 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture));
            }
        }

        private double BgChangeFraction { get { return (double)BgChangeIndex / (BgChangeStops.Length - 1); } }
        public double BgChangeFillWidth { get { return BgChangeFraction * BgChangeTrack; } }
        public Thickness BgChangeKnobMargin { get { return new Thickness(BgChangeFraction * BgChangeTrack - 12, 0, 0, 0); } }

        private bool NudgeBgChangeDelay(int dir)
        {
            int v = BgChangeStops[Math.Max(0, Math.Min(BgChangeStops.Length - 1, BgChangeIndex + dir))];
            if (v == settings.BgChangeDelay) return false;
            settings.BgChangeDelay = v;
            SaveSettings();
            foreach (string n in new[] { "BgChangeDelayText", "BgChangeFillWidth", "BgChangeKnobMargin" }) Notify(n);
            bgDelayLogged = false;   // (the next one put on is logged)
            ApplyBgChangeDelay();
            return true;
        }

        /// <summary>The idle screen's picture (Ambient mode) keeps its own timing.</summary>
        private static bool InAmbient(DependencyObject d)
        {
            for (var p = d; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            {
                var fe = p as FrameworkElement;
                if (fe != null && fe.Name == "KX_AmbientArt") return true;
            }
            return false;
        }

        private static bool InGameDetails(DependencyObject d)
        {
            for (var p = d; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
                if (p.GetType().Name.IndexOf("GameDetails", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private readonly System.Collections.Generic.List<WeakReference> bgFades = new System.Collections.Generic.List<WeakReference>();

        // (0.99.21) The home background with a wait. Playnite's own picture (PART_ImageBackground) stays exactly as Playnite runs
        // it (bound to the selected game; never touched now: 0.99.19-0.99.20 moved its binding and that broke it), but out of
        // sight; Kippix's own picture just above it (KX_BgDelayedHost › a FadeImage of its own) shows what Playnite's has, the
        // chosen time after it changed (moving on meanwhile starts the wait again). Playnite's blur and darkening are copied over.
        private FrameworkElement bgPlaynite, bgMine;
        private DependencyProperty bgSourceDp;
        private bool bgMineLogged;
        private EventHandler bgWatchHandler;

        /// <summary>Puts the background change speed on (from the tick, the slider and every move on the home screen).</summary>
        private void ApplyBgChangeDelay(bool search = true)
        {
            if (!IsFullscreen) return;
            try
            {
                CfaSync();
                if (bgPlaynite != null && bgMine != null && bgPlaynite.IsLoaded && bgMine.IsLoaded) { BgCopyLook(); return; }
                if (!search && (DateTime.Now - bgFindAt).TotalSeconds < 3) return;
                bgFindAt = DateTime.Now;
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                var host = win != null ? UiTools.FindByName(win, "KX_BgDelayedHost") : null;
                var layers = win != null ? UiTools.FindByName(win, "KX_BgLayers") : null;
                if (host == null || layers == null) { if (!bgDelayLogged) { bgDelayLogged = true; logger.Info("Kippix: background change speed: the home background wasn't found"); } return; }
                var mine = UiTools.FindAll<FrameworkElement>(host).FirstOrDefault(x => x.GetType().Name == "FadeImage");
                var theirs = UiTools.FindAll<FrameworkElement>(layers).FirstOrDefault(x => x.GetType().Name == "FadeImage" && !host.IsAncestorOf(x));
                if (mine == null || theirs == null) return;
                var spf = theirs.GetType().GetField("SourceProperty", BindingFlags.Public | BindingFlags.Static);
                var sp = spf != null ? spf.GetValue(null) as DependencyProperty : null;
                if (sp == null) return;
                // (left over from 0.99.19-0.99.20 in this session: never; a new start has Playnite's binding as it should be)
                if (bgPlaynite != null && bgWatchHandler != null && bgSourceDp != null)
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(bgSourceDp, bgPlaynite.GetType()).RemoveValueChanged(bgPlaynite, bgWatchHandler);
                bgPlaynite = theirs;
                bgMine = mine;
                bgSourceDp = sp;
                bgWatchHandler = (s, e) => BgPlayniteChanged();
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(sp, theirs.GetType()).AddValueChanged(theirs, bgWatchHandler);
                BgSetDelayZero(mine);
                BgCopyLook();
                bgShownId = Guid.Empty;                     // the selected game's picture goes on straight away (BgDriveTick)
                theirs.Opacity = 0;                         // Playnite's picture keeps going, out of sight
                BgDriveStart();
                BgDriveTick();
                if (!bgMineLogged) { bgMineLogged = true; logger.Info("Kippix: background change speed: Kippix's own background picture is on (" + settings.BgChangeDelay + " ms after the game changes)"); }
            }
            catch (Exception ex) { if (!bgDelayLogged) { bgDelayLogged = true; logger.Warn("Kippix: background change speed: " + ex.Message); } }
        }

        private static void BgSetDelayZero(FrameworkElement fe)
        {
            try
            {
                var dp = fe.GetType().GetProperty("SourceUpdateDelay", BindingFlags.Public | BindingFlags.Instance);
                if (dp != null && dp.CanWrite) dp.SetValue(fe, Convert.ChangeType(0, dp.PropertyType), null);
            }
            catch (Exception) { }
        }

        /// <summary>Playnite's blur, darkening and fade settings on Kippix's picture too (they change with Playnite's own settings).</summary>
        private void BgCopyLook()
        {
            if (bgPlaynite == null || bgMine == null) return;
            foreach (string name in new[] { "IsBlurEnabled", "BlurAmount", "HighQualityBlur", "ImageDarkeningBrush", "ImageOpacityMask", "AnimationEnabled" })
            {
                try
                {
                    var f = bgPlaynite.GetType().GetField(name + "Property", BindingFlags.Public | BindingFlags.Static);
                    var dp = f != null ? f.GetValue(null) as DependencyProperty : null;
                    if (dp == null) continue;
                    object v = bgPlaynite.GetValue(dp);
                    if (!Equals(bgMine.GetValue(dp), v)) bgMine.SetValue(dp, v);
                }
                catch (Exception) { }
            }
        }

        private int bgSurveys;

        /// <summary>
        /// (0.99.22) The first few game changes: every big picture on screen just after (what it is, where it sits, what it
        /// shows), so the log tells which one changes at once / shows the first game's background.
        /// </summary>
        private void BgSurvey()
        {
            if (bgSurveys >= 4) return;
            int n = ++bgSurveys;
            foreach (int ms in new[] { 60, 600 })
            {
                int wait = ms;
                var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
                tm.Tick += (s, e) =>
                {
                    tm.Stop();
                    try
                    {
                        var win = Application.Current != null ? Application.Current.MainWindow : null;
                        if (win == null) return;
                        double big = win.ActualWidth * 0.5;
                        var lines = new System.Collections.Generic.List<string>();
                        foreach (var fe in UiTools.FindAll<FrameworkElement>(win))
                        {
                            string kind = fe.GetType().Name;
                            object src = null;
                            if (fe is System.Windows.Controls.Image) src = ((System.Windows.Controls.Image)fe).Source;
                            else if (kind == "FadeImage") { try { src = fe.GetType().GetProperty("Source").GetValue(fe, null); } catch (Exception) { } }
                            else if (fe is System.Windows.Controls.Border && ((System.Windows.Controls.Border)fe).Background is System.Windows.Media.ImageBrush) src = ((System.Windows.Media.ImageBrush)((System.Windows.Controls.Border)fe).Background).ImageSource;
                            else if (kind == "MediaElement") src = ((System.Windows.Controls.MediaElement)fe).Source;
                            else continue;
                            if (src == null || !fe.IsVisible || fe.ActualWidth < big) continue;
                            double op = 1;
                            for (DependencyObject p = fe; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p)) { var u = p as UIElement; if (u != null) op *= u.Opacity; }
                            if (op < 0.02) continue;
                            string txt = src.ToString();
                            try { var bp = src.GetType().GetProperty("Source"); if (bp != null && !(src is System.Windows.Media.ImageSource)) txt = Convert.ToString(bp.GetValue(src, null)); } catch (Exception) { }
                            if (txt.Length > 90) txt = "…" + txt.Substring(txt.Length - 90);
                            lines.Add(kind + " " + BgFadeWhere(fe) + " opacity " + op.ToString("0.##") + " = " + txt);
                        }
                        var g = SelectedGameNow();
                        logger.Info("Kippix: background survey " + n + " (" + wait + " ms after the game changed to " + (g != null ? g.Name : "?") + "): " + (lines.Count == 0 ? "no big pictures" : string.Join(" | ", lines)) + " (Kippix's picture " + (bgShownId == bgWantId ? "has changed to it" : "is waiting") + ")");
                    }
                    catch (Exception ex) { logger.Warn("Kippix: background survey: " + ex.Message); }
                };
                tm.Start();
            }
        }

        // (0.99.22) Kippix's picture is driven by the selected game itself (Playnite's SelectedGame, read every 50 ms), not by
        // Playnite's picture: a game counts once it has stayed selected for the chosen time, and only then is its background
        // (Playnite's own DisplayBackgroundImageObject, so the same picture, size and fallbacks) put on Kippix's picture.
        // A moment with nothing selected, or a game passed on the way, never shows. Last session's picture (KX_StartupBg)
        // goes once Kippix's picture has something, so it can't show through between two games.
        private System.Windows.Threading.DispatcherTimer bgDriveTimer;
        private Guid bgWantId = Guid.Empty, bgShownId = Guid.Empty;
        private DateTime bgWantSince;
        private Type bgVmType, bgEntryType;
        private PropertyInfo bgSelProp, bgIdProp, bgObjProp;
        private bool bgOpacityLogged, bgDriveLogged;

        private void BgDriveStart()
        {
            if (bgDriveTimer != null) return;
            bgDriveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            bgDriveTimer.Tick += (s, e) => BgDriveTick();
            bgDriveTimer.Start();
        }

        /// <summary>Playnite's selected game (its list entry) from the main view's model, or null.</summary>
        private object BgSelectedEntry()
        {
            object dc = null;
            for (DependencyObject d = bgPlaynite; d != null && dc == null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe == null || fe.DataContext == null) continue;
                var ty = fe.DataContext.GetType();
                if (ty != bgVmType) { var pi = ty.GetProperty("SelectedGame"); if (pi == null) continue; bgVmType = ty; bgSelProp = pi; }
                dc = fe.DataContext;
            }
            if (dc == null || bgSelProp == null) return null;
            var e = bgSelProp.GetValue(dc, null);
            if (e == null) return null;
            if (e.GetType() != bgEntryType)
            {
                bgEntryType = e.GetType();
                bgIdProp = bgEntryType.GetProperty("Id");
                bgObjProp = bgEntryType.GetProperty("DisplayBackgroundImageObject");
            }
            return bgIdProp != null && bgObjProp != null ? e : null;
        }

        private void BgDriveTick()
        {
            try
            {
                if (bgPlaynite == null || bgMine == null || bgSourceDp == null || !bgMine.IsLoaded || gameRunningNow) return;
                // Playnite's picture stays out of sight (if anything brought it back, it goes again)
                if (bgPlaynite.Opacity > 0.001 || bgPlaynite.HasAnimatedProperties)
                {
                    bgPlaynite.BeginAnimation(UIElement.OpacityProperty, null);
                    bgPlaynite.Opacity = 0;
                    if (!bgOpacityLogged) { bgOpacityLogged = true; logger.Info("Kippix: background change speed: Playnite's own picture had come back into sight; hidden again"); }
                }
                var entry = BgSelectedEntry();
                if (entry == null) { BgChangerTick(); return; }   // nothing selected for a moment: what's showing stays
                var id = (Guid)bgIdProp.GetValue(entry, null);
                var now = DateTime.Now;
                if (id != bgWantId) { bgWantId = id; bgWantSince = now; if (bgShownId != Guid.Empty) BgSurvey(); }
                if (id == bgShownId) { BgChangerTick(); return; }
                int ms = Math.Max(0, Math.Min(BgChangeMax, settings.BgChangeDelay));
                if (bgShownId != Guid.Empty && (now - bgWantSince).TotalMilliseconds < ms) { BgChangerTick(); return; }
                bgMine.SetValue(bgSourceDp, bgObjProp.GetValue(entry, null));
                bool first = bgShownId == Guid.Empty;
                bgShownId = id;
                BgStartupHide();
                BgChangerTick();
                if (!bgDriveLogged && !first) { bgDriveLogged = true; logger.Info("Kippix: background change speed: the first change went " + (int)(now - bgWantSince).TotalMilliseconds + " ms after the game was selected (set to " + ms + " ms)"); }
            }
            catch (Exception ex) { if (!bgDelayLogged) { bgDelayLogged = true; logger.Warn("Kippix: background change speed: " + ex.Message); } }
        }

        // (0.99.23) BackgroundChanger puts its own picture over the home background and changes it the moment the game
        // changes (and it shows itself even with Kippix's "Its backgrounds" off: it sets its element's visibility itself).
        // That was the picture changing at once / showing the previous game's first. Off in Kippix: it stays out of sight.
        // On: it fades out while Kippix's picture waits and back in once the wait is over, so it follows the same delay.
        private FrameworkElement bgChanger;
        private bool bgChangerShown = true, bgChangerLogged, bgChangerHid;

        private void BgChangerTick()
        {
            try
            {
                if (bgChanger == null || !bgChanger.IsLoaded)
                {
                    FrameworkElement layers = null;
                    for (DependencyObject d = bgMine; d != null && layers == null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                    { var fe = d as FrameworkElement; if (fe != null && fe.Name == "KX_BgLayers") layers = fe; }
                    bgChanger = layers != null ? UiTools.FindByName(layers, "BackgroundChanger_PluginBackgroundImage") as FrameworkElement : null;
                    bgChangerShown = true;
                    if (bgChanger == null) return;
                }
                bool on = settings.UseBcBackgrounds && BcInstalled;
                bool show = on && bgShownId != Guid.Empty && bgShownId == bgWantId;
                if (!on && bgChanger.Visibility != Visibility.Collapsed && bgChanger.IsVisible)
                {
                    bgChanger.Visibility = Visibility.Collapsed;
                    bgChangerHid = true;
                    if (!bgChangerLogged) { bgChangerLogged = true; logger.Info("Kippix: background change speed: BackgroundChanger's picture was showing over the home background with Kippix's \"Its backgrounds\" off; hidden"); }
                }
                else if (on && bgChangerHid) { bgChangerHid = false; bgChanger.ClearValue(UIElement.VisibilityProperty); }   // (back to the theme's own rule)
                if (show == bgChangerShown) return;
                bgChangerShown = show;
                bool still = settings.ReduceMotion;
                bgChanger.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(still ? 1 : show ? 500 : 150)));
            }
            catch (Exception) { }
        }

        /// <summary>Last session's picture (shown while the library loads) goes for good once Kippix's picture has one.</summary>
        private void BgStartupHide()
        {
            try
            {
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                var sb = win != null ? UiTools.FindByName(win, "KX_StartupBg") : null;
                if (sb != null && sb.Opacity > 0) sb.Opacity = 0;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Playnite's picture changed: when it's for the game Kippix already shows (its art was changed, say), Kippix's follows
        /// at once; moving to another game is BgDriveTick's.
        /// </summary>
        private void BgPlayniteChanged()
        {
            try
            {
                if (bgPlaynite == null || bgMine == null || bgSourceDp == null) return;
                var entry = BgSelectedEntry();
                if (entry == null || (Guid)bgIdProp.GetValue(entry, null) != bgShownId) return;
                var v = bgPlaynite.GetValue(bgSourceDp);
                if (v != null && !Equals(v, bgMine.GetValue(bgSourceDp))) bgMine.SetValue(bgSourceDp, v);
            }
            catch (Exception) { }
        }

        /// <summary>The home background people see (Kippix's own when it's on, else Playnite's): what motion effects move.</summary>
        internal FrameworkElement HomeBgVisual()
        {
            if (bgMine != null && bgMine.IsLoaded) return bgMine;
            return bgPlaynite;
        }

        /// <summary>Where a background picture sits (its parents' names), for the log.</summary>
        private static string BgFadeWhere(FrameworkElement f)
        {
            var names = new System.Collections.Generic.List<string>();
            for (DependencyObject p = System.Windows.Media.VisualTreeHelper.GetParent(f); p != null && names.Count < 4; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            {
                var pe = p as FrameworkElement;
                if (pe != null && !string.IsNullOrEmpty(pe.Name)) names.Add(pe.Name);
            }
            object d = null;
            try { d = f.GetType().GetProperty("SourceUpdateDelay").GetValue(f, null); } catch (Exception) { }
            return string.Join(" < ", names) + " (" + d + " ms, " + (f.IsVisible ? "shown" : "hidden") + ")";
        }
    }
}

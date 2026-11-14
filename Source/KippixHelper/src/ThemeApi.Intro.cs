using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Look and feel › Intro and outro. When fullscreen starts, a short intro plays over the black start-up layer instead of
    /// plain black: Kippix's own animated logo (drawn here, about 2.6 s) or your own video (.mp4 / .wmv / .gif, a copy kept
    /// in ExtensionsData\intro\). When you leave from the Power menu (exit, desktop mode, sleep, hibernate, restart, shut down)
    /// an outro plays first (at most 6 s). Any button skips; a video that fails is skipped at once, so it never holds anything up.
    ///
    /// Also the plumbing for this round's code-built Look and feel pages (intro, nightmode: one list, LookExtraRows), the code-built
    /// part of Home settings › Background (ANIMATED ART and RESET, BgAnimRows: ThemeApi.AnimatedArt.cs), Left / Right on their
    /// rows and on Depth, and NormalizeLiveSettings for all of this round's settings.
    /// </summary>
    public partial class ThemeApi
    {
        // =====================================================================
        // The pages (Look and feel › Intro and outro / Night mode)
        // =====================================================================

        private ObservableCollection<PickRow> lookExtraRows;
        public ObservableCollection<PickRow> LookExtraRows { get { return lookExtraRows ?? (lookExtraRows = new ObservableCollection<PickRow>()); } }
        public bool IsOptionsLookExtra { get { return optionsPage == "intro" || optionsPage == "nightmode" || optionsPage == "detanim"; } }   // (0.99.70: and Game page › Animation)
        public ICommand OpenLookPageCommand { get; private set; }

        public string LookExtraTitle
        {
            get
            {
                if (optionsPage == "nightmode") return Loc("LOC_KX_NightTitle", "Night mode");
                if (optionsPage == "detanim") return Loc("LOC_KX_PgAnimTitle", "Animation");
                return Loc("LOC_KX_IntroTitle", "Intro and outro");
            }
        }

        public string LookExtraHelp
        {
            get
            {
                if (optionsPage == "detanim") return Loc("LOC_KX_PgAnimHelp", "How the game page comes in when you open a game. Pick one to see it on the game page behind.");
                if (optionsPage == "nightmode") return Loc("LOC_KX_NightHelp", "Easier on the eyes late at night: the screen goes darker and warmer, and the sounds quieter. Left / Right change the amounts.");
                return Loc("LOC_KX_IntroHelp", "A short intro when fullscreen starts, and an outro when you leave from the Power menu. Any button skips them.");
            }
        }

        private void InitLookExtras()
        {
            OpenLookPageCommand = new RelayCommand(p =>
            {
                string page = p as string ?? "";
                if (page != "intro" && page != "nightmode") return;
                SetOptionsPage(page);
            });
            CycleDepthCommand = new RelayCommand(_ => { settings.DepthMode = (settings.DepthMode + 1) % 4; DepthChanged(); });
            // controller buttons skip the intro / outro (they only arrive as KeyDown at the highlighted element, never on the way down);
            // registered before the theme's own early pad handler (InitPadInput), so this one runs first
            EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.KeyDownEvent, new KeyEventHandler(OnPadForIntro), true);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                if (introOn && IsMainWindow(s)) { e.Handled = true; SkipIntro(); }
            }), true);
        }

        private void LookExtraOptionsOpened(string page)
        {
            Notify("IsOptionsLookExtra");
            if (page == "intro" || page == "nightmode" || page == "detanim")
            {
                BuildLookExtraRows();
                Notify("LookExtraHelp");
            }
            if (page == "background") BuildBgAnimRows();   // Home settings › Background › ANIMATED ART and RESET (ThemeApi.AnimatedArt.cs)
            if (page == "look") NotifyLookExtras();
        }

        private void BuildLookExtraRows()
        {
            var rows = LookExtraRows;
            rows.Clear();
            if (optionsPage == "nightmode") AddNightRows(rows);
            else if (optionsPage == "detanim") AddDetAnimRows(rows);
            else AddIntroRows(rows);
        }

        /// <summary>A row was changed: saved, the page built again (rows come and go), the highlight back on that row.</summary>
        private void LookExtraChanged(string key)
        {
            SaveSettings();
            PlaySound("move");
            NotifyLookExtras();
            if (openPanel == "options" && IsOptionsLookExtra) BoardRowsAgain(BuildLookExtraRows, key);
        }

        private void NotifyLookExtras()
        {
            foreach (string n in new[] { "IntroSummary", "NightSummary", "AnimArtSummary", "DepthText", "DepthShown", "LiveSlideWeb", "LookSummary", "HomeBgSummary" }) Notify(n);
        }

        /// <summary>Left / Right on this round's rows (the amounts on the code-built pages; Depth and animated art on Home settings › Background). True if it was one.</summary>
        private bool LookExtraKey(KeyEventArgs e)
        {
            if (e.Key != Key.Left && e.Key != Key.Right) return false;
            bool bgPage = optionsPage == "background", wxPage = optionsPage == "weatherfx";
            if (openPanel != "options" || (!bgPage && !wxPage && !IsOptionsLookExtra)) return false;
            var fe = Keyboard.FocusedElement as FrameworkElement;
            if (fe == null) return false;
            int dir = e.Key == Key.Right ? 1 : -1;
            if (fe.Name == "KX_Slider_depth")
            {
                int d = Math.Max(0, Math.Min(3, settings.DepthMode + dir));
                if (d == settings.DepthMode) { PlaySound("back"); return true; }
                settings.DepthMode = d;
                DepthChanged();
                return true;
            }
            var b = fe as ButtonBase;
            string key = b != null ? b.CommandParameter as string : null;
            if (key == null) return false;
            if (bgPage) return BgPageNudge(key, dir);
            if (wxPage) return WeatherNudge(key, dir);
            return NightNudge(key, dir) || IntroNudge(key, dir);
        }

        /// <summary>Settings read from file (or a layout / profile): keep everything in range.</summary>
        private void NormalizeLiveSettings()
        {
            settings.IntroMode = Math.Max(0, Math.Min(3, settings.IntroMode));
            settings.OutroMode = Math.Max(0, Math.Min(3, settings.OutroMode));
            // (0.99.66) no built-in intro or outro any more (the animated logo, mode 1, and Kippix's video, mode 3, are gone):
            // none, or your own video
            if (settings.IntroMode != 2) settings.IntroMode = 0;
            if (settings.OutroMode != 2) settings.OutroMode = 0;
            if (settings.IntroVideo == null) settings.IntroVideo = "";
            if (settings.OutroVideo == null) settings.OutroVideo = "";
            settings.DepthMode = Math.Max(0, Math.Min(3, settings.DepthMode));
            settings.AnimArtMode = Math.Max(0, Math.Min(1, settings.AnimArtMode));
            NormalizeWeatherFx();   // (0.81: 0 off, 1 the zoom; Stream / Download are gone)
            settings.AnimArtNoArt = Math.Max(0, Math.Min(1, settings.AnimArtNoArt));   // (r14: 0 living picture, 1 still picture)
            if (settings.SgdbApiKey == null) settings.SgdbApiKey = "";
            settings.NightMode = Math.Max(0, Math.Min(2, settings.NightMode));
            settings.NightFrom = ((settings.NightFrom % 1440) + 1440) % 1440;
            settings.NightTo = ((settings.NightTo % 1440) + 1440) % 1440;
            settings.NightDim = Math.Max(0, Math.Min(70, settings.NightDim));
            settings.NightWarm = Math.Max(0, Math.Min(100, settings.NightWarm));
            settings.NightQuiet = Math.Max(0, Math.Min(100, settings.NightQuiet));
        }

        // =====================================================================
        // Intro and outro: the settings rows
        // =====================================================================

        private string IntroModeName(int m)
        {
            if (m == 2) return Loc("LOC_KX_IntroOwn", "Your own video");
            return Loc("LOC_KX_IntroNone", "None");
        }

        public string IntroSummary
        {
            get
            {
                if (settings.IntroMode == 0 && settings.OutroMode == 0) return Loc("LOC_KX_Off", "Off");
                return string.Format(Loc("LOC_KX_IntroSummary", "Intro: {0}  ·  Outro: {1}"), IntroModeName(settings.IntroMode), IntroModeName(settings.OutroMode));
            }
        }

        private string IntroFolder { get { return System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "intro"); } }

        private string IntroVideoText(string path)
        {
            if (string.IsNullOrEmpty(path)) return Loc("LOC_KX_IntroVideoNone", "None yet");
            if (!File.Exists(path)) return Loc("LOC_KX_IntroVideoMissing", "The video can't be found");
            string name = System.IO.Path.GetFileName(path);
            int dash = name.IndexOf("--", StringComparison.Ordinal);   // (the copy is named <when>--<original name>)
            return dash > 0 ? name.Substring(dash + 2) : name;
        }

        private void AddIntroRows(ObservableCollection<PickRow> rows)
        {
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecIntro", "WHEN FULLSCREEN STARTS") });
            rows.Add(new PickRow
            {
                Key = "intro", Icon = "play", Text = string.Format(Loc("LOC_KX_IntroRow", "Intro: {0}"), IntroModeName(settings.IntroMode)),
                Sub = Loc("LOC_KX_IntroRowSub4", "None, or a video of your own"),
                Command = new RelayCommand(_ => { settings.IntroMode = NextIntroMode(settings.IntroMode, 1); LookExtraChanged("intro"); })
            });
            if (settings.IntroMode == 2)
                rows.Add(new PickRow
                {
                    Key = "introvideo", Icon = "video", Text = Loc("LOC_KX_IntroVideoRow", "Intro video"), Sub = IntroVideoText(settings.IntroVideo),
                    Command = new RelayCommand(_ => ChooseIntroVideo(false))
                });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecOutro", "WHEN YOU LEAVE") });
            rows.Add(new PickRow
            {
                Key = "outro", Icon = "leave", Text = string.Format(Loc("LOC_KX_OutroRow", "Outro: {0}"), IntroModeName(settings.OutroMode)),
                Sub = Loc("LOC_KX_OutroRowSub", "Before Exit, Desktop mode, Sleep, Hibernate, Restart or Shut down from the Power menu (6 seconds at most)"),
                Command = new RelayCommand(_ => { settings.OutroMode = NextIntroMode(settings.OutroMode, 1); LookExtraChanged("outro"); })
            });
            if (settings.OutroMode == 2)
                rows.Add(new PickRow
                {
                    Key = "outrovideo", Icon = "video", Text = Loc("LOC_KX_OutroVideoRow", "Outro video"), Sub = IntroVideoText(settings.OutroVideo),
                    Command = new RelayCommand(_ => ChooseIntroVideo(true))
                });
            if (settings.IntroMode != 0 || settings.OutroMode != 0)
            {
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecIntroMore", "SOUND AND PREVIEW") });
                rows.Add(BoardSwitch("speaker", "introsound", Loc("LOC_KX_IntroSound", "Sound"), settings.IntroSound,
                                     Loc("LOC_KX_IntroSoundOn2", "The video's own sound (the music waits)"), Loc("LOC_KX_IntroSoundOff", "Silent"),
                                     () => { settings.IntroSound = !settings.IntroSound; LookExtraChanged("introsound"); }));
                if (settings.IntroMode != 0)
                    rows.Add(new PickRow { Key = "introtry", Icon = "play", Text = Loc("LOC_KX_IntroTry", "Watch the intro now"),
                                           Command = new RelayCommand(_ => { PlaySound("select"); ShowIntroOutro(false, null, true); }) });
                if (settings.OutroMode != 0)
                    rows.Add(new PickRow { Key = "outrotry", Icon = "play", Text = Loc("LOC_KX_OutroTry", "Watch the outro now"), Sub = Loc("LOC_KX_OutroTrySub", "Just to see it: nothing closes"),
                                           Command = new RelayCommand(_ => { PlaySound("select"); ShowIntroOutro(true, null, true); }) });
                if (settings.ReduceMotion && (settings.IntroMode == 1 || settings.OutroMode == 1))
                    rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "introrm", Text = Loc("LOC_KX_IntroReduceMotion", "Reduce motion is on: the logo shows still, without moving") });
            }
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecReset", "RESET") });
            rows.Add(new PickRow
            {
                Key = "introreset", Icon = "refresh", Text = Loc("LOC_KX_ResetPage", "Reset this page"), Sub = Loc("LOC_KX_ResetPageSub", "Resets this page to default"),
                Command = new RelayCommand(_ =>
                {
                    settings.IntroMode = 0; settings.OutroMode = 0; settings.IntroSound = true;   // (back to none; the chosen videos stay on disk for next time)
                    LookExtraChanged("introreset");
                })
            });
        }

        private bool IntroNudge(string key, int dir)
        {
            if (key == "intro") { settings.IntroMode = NextIntroMode(settings.IntroMode, dir); LookExtraChanged(key); return true; }
            if (key == "outro") { settings.OutroMode = NextIntroMode(settings.OutroMode, dir); LookExtraChanged(key); return true; }
            return false;
        }

        /// <summary>Intro / Outro video: pick a file on the PC (the theme's own file picker); a copy is kept in ExtensionsData\intro\.</summary>
        private void ChooseIntroVideo(bool outro)
        {
            string cur = outro ? settings.OutroVideo : settings.IntroVideo;
            string start = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            try { if (!string.IsNullOrEmpty(cur) && !cur.StartsWith(IntroFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(System.IO.Path.GetDirectoryName(cur))) start = System.IO.Path.GetDirectoryName(cur); } catch (Exception) { }
            PlaySound("select");
            OpenPicker(outro ? Loc("LOC_KX_OutroChoose", "Choose an outro video") : Loc("LOC_KX_IntroChoose", "Choose an intro video"), false,
                       new[] { ".mp4", ".m4v", ".wmv", ".gif" }, start, "intro", file =>
            {
                string folder = IntroFolder;
                string backKey = outro ? "outrovideo" : "introvideo";
                KippixShared.SafeWork.Queue(_ =>
                {
                    string dest = null, error = null;
                    try
                    {
                        Directory.CreateDirectory(folder);
                        dest = System.IO.Path.Combine(folder, (outro ? "outro-" : "intro-") + DateTime.Now.Ticks + "--" + System.IO.Path.GetFileName(file));
                        File.Copy(file, dest, true);
                    }
                    catch (Exception ex) { error = ex.Message; dest = null; }
                    OnUi(() =>
                    {
                        if (dest == null)
                        {
                            logger.Warn("Kippix: intro video: " + error);
                            try { ShowNotice(Loc("LOC_KX_IntroTitle", "Intro and outro").ToUpperInvariant(), Loc("LOC_KX_IntroVideoFailed", "That video couldn't be used"), System.IO.Path.GetFileName(file), "", null); } catch (Exception) { }
                        }
                        else
                        {
                            string old = outro ? settings.OutroVideo : settings.IntroVideo;
                            if (outro) { settings.OutroVideo = dest; settings.OutroMode = 2; }
                            else { settings.IntroVideo = dest; settings.IntroMode = 2; }
                            SaveSettings();
                            // the previous copy goes, unless the other one (intro / outro) still uses it
                            try
                            {
                                if (!string.IsNullOrEmpty(old) && old.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(old, settings.IntroVideo, StringComparison.OrdinalIgnoreCase) && !string.Equals(old, settings.OutroVideo, StringComparison.OrdinalIgnoreCase))
                                    File.Delete(old);
                            }
                            catch (Exception) { }
                        }
                        NotifyLookExtras();
                        GoBackTo("intro", backKey);
                    });
                });
            });
        }

        // =====================================================================
        // Playing it
        // =====================================================================

        private Grid introLayer;
        private bool introOn, introIsOutro, introIsTry, introSoundOn, introTried, introClosing;
        private Action introAfter;
        private string introAction;
        private MediaElement introVideo;
        private DispatcherTimer introCap, introWatch;
        private int introGen;
        private bool startupScreensWaiting;
        private WavSound introChime;
        // r13: the press that started it (A on "Watch the intro now", or Yes on Exit) goes on up the tree as the same
        // KeyDown: it mustn't count as "any button skips" (that ended it before a frame showed). That press, and anything
        // within a short moment of the start, is let through.
        private KeyEventArgs introLastPadArgs, introStartArgs;
        private int introStartTick;
        private const int IntroGraceMs = 400;
        private IInputElement introFocusBefore;
        private string introEndWhy;

        /// <summary>The intro or outro is on screen (input goes to skipping it; trailers and live backgrounds wait).</summary>
        public bool IntroCovering { get { return introOn; } }
        /// <summary>It plays with sound: the home screen music waits.</summary>
        private bool IntroSoundActive { get { return introOn && introSoundOn; } }

        /// <summary>The black start-up layer is held: the intro (if there is one) starts over it. Once per start.</summary>
        private void StartIntro()
        {
            if (introTried) return;
            introTried = true;
            if (!IsFullscreen) return;
            if (settings.IntroMode == 0) { logger.Info("Kippix: intro skipped because it's set to None (Look and feel › Intro and outro)"); return; }
            try { ShowIntroOutro(false, null, false); }
            catch (Exception ex) { logger.Warn("Kippix: intro: " + ex.Message); EndIntro(true); }
        }

        /// <summary>
        /// Runs <paramref name="act"/> after the outro (Look and feel › Intro and outro), or straight away when there's none.
        /// <paramref name="what"/>: exit, desktop, sleep, hibernate, restart, shutdown.
        /// </summary>
        private void WithOutro(string what, Action act)
        {
            if (act == null) return;
            if (!IsFullscreen || settings.OutroMode == 0 || introOn || ((settings.OutroMode == 2 || settings.OutroMode == 3) && !File.Exists(IntroVideoPath(true) ?? "")))
            {
                if (IsFullscreen)
                    logger.Info("Kippix: outro skipped because " + (settings.OutroMode == 0 ? "it's set to None" : introOn ? "the intro / outro is already on screen"
                                : "its video isn't there (" + (IntroVideoPath(true) ?? "no path") + ")") + "; " + what + " goes ahead");
                act();
                return;
            }
            bool ran = false;
            Action once = () => { if (ran) return; ran = true; act(); };   // (whatever goes wrong, the action runs exactly once)
            try
            {
                introAction = what;
                ShowIntroOutro(true, once, false);
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: outro: " + ex.Message);
                try { EndIntro(true); } catch (Exception) { }
                once();
            }
        }

        /// <summary>Order in the settings row: none, your own video.</summary>
        private static readonly int[] IntroModeOrder = { 0, 2 };   // (0.99.66: none, or your own video)
        private static int NextIntroMode(int m, int dir)
        {
            int i = Array.IndexOf(IntroModeOrder, m);
            if (i < 0) i = 0;
            return IntroModeOrder[(i + dir + IntroModeOrder.Length) % IntroModeOrder.Length];
        }

        /// <summary>The video for the intro / outro: your own copy (0.99.66: Kippix has no video of its own any more).</summary>
        private string IntroVideoPath(bool outro)
        {
            return outro ? settings.OutroVideo : settings.IntroVideo;
        }

        private void ShowIntroOutro(bool outro, Action after, bool justTrying)
        {
            if (introOn) EndIntro(true);
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var host = win != null ? UiTools.FindByName(win, "PART_MainHost") as Panel : null;
            int mode = outro ? settings.OutroMode : settings.IntroMode;
            string video = IntroVideoPath(outro);
            string what = outro ? "outro" : "intro";
            if (host == null || mode == 0 || ((mode == 2 || mode == 3) && (string.IsNullOrEmpty(video) || !File.Exists(video))))
            {
                string why = host == null ? "the theme's main screen (PART_MainHost) wasn't found"
                           : mode == 0 ? "it's set to None"
                           : string.IsNullOrEmpty(video) ? "no video has been chosen"
                           : "its video isn't there: " + video;
                logger.Info("Kippix: " + what + " skipped because " + why + (justTrying ? " (Watch now)" : ""));
                if (justTrying)
                {
                    PlaySound("back");
                    try
                    {
                        ShowCard(Loc("LOC_KX_IntroTitle", "Intro and outro").ToUpperInvariant(),
                                 mode == 2 ? Loc("LOC_KX_IntroTryNoVideo", "That video can't be found: choose it again") : Loc("LOC_KX_IntroTryMissing", "Kippix's video is missing from the theme's Video folder"),
                                 string.IsNullOrEmpty(video) ? "" : System.IO.Path.GetFileName(video), "", null);
                    }
                    catch (Exception) { }
                }
                if (after != null) after();
                return;
            }
            int gen = ++introGen;
            introStartArgs = introLastPadArgs;   // (the press in flight now, if it was a controller button: not a skip)
            introStartTick = Environment.TickCount;
            introEndWhy = null;
            introFocusBefore = justTrying ? Keyboard.FocusedElement : null;
            introOn = true;
            introClosing = false;
            introIsOutro = outro;
            introIsTry = justTrying;
            introAfter = after;
            introSoundOn = false;

            var layer = new Grid { Background = Brushes.Black, Focusable = false, IsHitTestVisible = true, ClipToBounds = true };
            Panel.SetZIndex(layer, 150);
            Grid.SetRowSpan(layer, 99);
            Grid.SetColumnSpan(layer, 99);
            host.Children.Add(layer);
            introLayer = layer;
            if (outro || justTrying)
            {
                // the outro (and a try from the settings) comes in from what's on screen
                layer.Opacity = 0;
                layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(250)));
            }
            StopPreview();
            PauseLiveForIntro();

            // never longer than this, whatever happens (the outro: 6 s; the intro: 10 s; the logo is shorter by itself)
            double cap = outro ? 6 : 10;
            introCap = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(cap) };
            introCap.Tick += (s, e) => { introCap.Stop(); if (gen == introGen) { introEndWhy = "its time was up (" + cap + " s)"; EndIntro(false); } };
            introCap.Start();

            if (mode == 1) PlayLogo(layer, outro, gen);
            else PlayIntroVideo(layer, video, gen);
            UpdateMusicState();
            logger.Info("Kippix: " + what + " playing (" + (mode == 1 ? "Kippix's animated logo" : video) + (justTrying ? ", Watch now" : "")
                        + (settings.IntroSound ? ", with sound" : ", silent") + ")");
        }

        private void PlayIntroVideo(Grid layer, string path, int gen)
        {
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                // a GIF: decoded off the UI thread into frames (the same player as Animated art), played once
                var img = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
                layer.Children.Add(img);
                double w = SystemParameters.PrimaryScreenWidth;
                KippixShared.SafeWork.Queue(_ =>
                {
                    AnimFrames frames = null;
                    try { frames = AnimDecode(File.ReadAllBytes(path), (int)Math.Min(1280, w), 720, 160L * 1024 * 1024); } catch (Exception ex) { logger.Warn("Kippix: intro GIF: " + ex.Message); }
                    OnUi(() =>
                    {
                        if (gen != introGen || !introOn) return;
                        if (frames == null || frames.Frames.Count == 0) { EndIntro(false); return; }
                        AnimPlayOnce(img, frames, () => { if (gen == introGen) EndIntro(false); });
                    });
                });
                return;
            }
            var me = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform,
                IsHitTestVisible = false, Volume = 0.9 * NightVolumeFactor, IsMuted = !settings.IntroSound, ScrubbingEnabled = false
            };
            introVideo = me;
            introSoundOn = settings.IntroSound;
            bool opened = false;
            me.MediaOpened += (s, e) =>
            {
                opened = true;
                if (gen == introGen) logger.Info("Kippix: intro / outro video started after " + unchecked(Environment.TickCount - introStartTick) + " ms"
                                                 + (me.NaturalDuration.HasTimeSpan ? " (" + me.NaturalDuration.TimeSpan.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s long)" : ""));
            };
            me.MediaEnded += (s, e) => { if (gen == introGen) { introEndWhy = "it played to the end"; EndIntro(false); } };
            me.MediaFailed += (s, e) =>
            {
                logger.Warn("Kippix: intro / outro video couldn't play: " + (e.ErrorException != null ? e.ErrorException.Message : "unknown") + " (" + path + ")");
                if (gen == introGen) { introEndWhy = "the video couldn't play"; EndIntro(false); }
            };
            layer.Children.Add(me);
            // a video that hasn't started within 4 s (a codec Windows hasn't got, a broken file, a busy start-up): skipped, never waited for
            introWatch = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(4) };
            introWatch.Tick += (s, e) => { introWatch.Stop(); if (gen == introGen && !opened) { introEndWhy = "the video didn't start within 4 s (" + path + ")"; EndIntro(false); } };
            introWatch.Start();
            try
            {
                me.Source = new Uri(path);
                me.Play();
            }
            catch (Exception ex) { logger.Warn("Kippix: intro video: " + ex.Message); introEndWhy = "the video couldn't be opened"; EndIntro(false); }
        }

        /// <summary>Any button (or a click) while it plays: it ends now.</summary>
        private void SkipIntro()
        {
            if (!introOn || introClosing) return;
            if (unchecked(Environment.TickCount - introStartTick) < IntroGraceMs) return;   // (the press that started it, or one right with it)
            introEndWhy = "a button skipped it";
            EndIntro(false);
        }

        private void OnPadForIntro(object sender, KeyEventArgs e)
        {
            try
            {
                if (PadButton(e) == Playnite.SDK.Events.ControllerInput.None || !InMainWindow(sender)) return;
                if (!introOn) { introLastPadArgs = e; return; }   // (remembered: if this press starts the intro, it isn't a skip)
                if (ReferenceEquals(e, introStartArgs)) return;    // the press that started it, on its way up: the click has happened
                e.Handled = true;
                lastPadEvent = e;   // (the theme's own early pad handler leaves this press alone: Y doesn't open search under the intro)
                padHandled = e;
                if (IsPadRelease(e)) return;   // (letting go of a button isn't a skip: the A held on "Watch now" / Yes on Exit)
                SkipIntro();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// The intro / outro is over (or skipped, or failed). The intro fades away to the home screen (or the black start-up
        /// layer, if the home screen isn't ready yet); the outro runs its action and stays black until that has happened.
        /// </summary>
        private void EndIntro(bool now)
        {
            if (!introOn) return;
            introClosing = true;
            int gen = ++introGen;
            if (introCap != null) introCap.Stop();
            if (introWatch != null) introWatch.Stop();
            var layer = introLayer;
            var video = introVideo;
            introVideo = null;
            if (video != null) { try { video.Stop(); video.Source = null; } catch (Exception) { } }
            AnimStopOnce();
            bool outro = introIsOutro, trying = introIsTry;
            var after = introAfter;
            introAfter = null;
            introSoundOn = false;
            introStartArgs = null;
            logger.Info("Kippix: " + (outro ? "outro" : "intro") + " ended: " + (introEndWhy ?? (now ? "stopped" : "done")) + " after "
                        + unchecked(Environment.TickCount - introStartTick) + " ms" + (trying ? " (Watch now)" : ""));
            introEndWhy = null;
            var focusBack = trying ? introFocusBefore : null;
            introFocusBefore = null;

            Action remove = () =>
            {
                try { var p = layer != null ? VisualTreeHelper.GetParent(layer) as Panel : null; if (p != null) p.Children.Remove(layer); } catch (Exception) { }
                if (ReferenceEquals(introLayer, layer)) introLayer = null;
                if (gen == introGen) { introOn = false; introClosing = false; }
                UpdateMusicState();
                // Watch now: back on the same settings row
                var fb = focusBack as UIElement;
                if (fb != null && fb.IsVisible && !ReferenceEquals(Keyboard.FocusedElement, fb)) { try { Keyboard.Focus(fb); } catch (Exception) { } }
                if (!outro && startupScreensWaiting && gen == introGen)
                {
                    startupScreensWaiting = false;
                    try { StartupScreens(); } catch (Exception ex) { logger.Error(ex, "Kippix: start-up screens"); }
                }
            };

            if (outro && !trying && after != null)
            {
                // the action goes now, behind black. Sleep / hibernate / desktop mode come back to Playnite: the black goes
                // a little after (a timer: after waking up it fires straight away); anything else closes, with a safety net
                if (layer != null) { layer.BeginAnimation(UIElement.OpacityProperty, null); layer.Opacity = 1; foreach (UIElement c in layer.Children) c.Visibility = Visibility.Collapsed; }
                string what = introAction ?? "";
                introAction = null;
                try { after(); } catch (Exception ex) { logger.Error(ex, "Kippix: action after the outro"); }
                var back = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(what == "sleep" || what == "hibernate" || what == "desktop" ? 3 : 8) };
                back.Tick += (s, e) => { back.Stop(); FadeOutIntroLayer(layer, remove, true); };
                back.Start();
                return;
            }
            if (now || layer == null) { remove(); return; }
            FadeOutIntroLayer(layer, remove, false);
        }

        private void FadeOutIntroLayer(Grid layer, Action remove, bool quick)
        {
            if (layer == null || settings.ReduceMotion) { remove(); return; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(quick ? 200 : 420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            bool done = false;
            fade.Completed += (s, e) => { if (!done) { done = true; remove(); } };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
            // (a backstop: a fade that never completes, e.g. the window hidden, still takes the layer away)
            var stop = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(900) };
            stop.Tick += (s, e) => { stop.Stop(); if (!done) { done = true; remove(); } };
            stop.Start();
        }

        // =====================================================================
        // Kippix's animated logo (vector, drawn here): the rounded screen draws itself, the play button pops in,
        // the name rises, a shine runs across; the outro switches the screen off like an old TV.
        // =====================================================================

        private static readonly Color LogoCyan = Color.FromRgb(60, 190, 245);
        private static readonly Color LogoViolet = Color.FromRgb(185, 95, 250);
        private static readonly Color LogoFill = Color.FromRgb(40, 29, 134);

        private static LinearGradientBrush LogoGradient()
        {
            var g = new LinearGradientBrush(LogoCyan, LogoViolet, new Point(0, 0), new Point(1, 1));
            g.Freeze();
            return g;
        }

        private void PlayLogo(Grid layer, bool outro, int gen)
        {
            bool still = settings.ReduceMotion;
            const double size = 240, thick = 12, radius = 52;
            var root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 40), IsHitTestVisible = false };
            var stack = new StackPanel();
            root.Children.Add(stack);

            // the badge: glow, gradient edge, dark fill, the screen, its stand and the play button
            var badge = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
            var badgeScale = new ScaleTransform(1, 1);
            badge.RenderTransform = badgeScale;
            var glow = new Border { CornerRadius = new CornerRadius(radius + 6), Margin = new Thickness(-14), Background = LogoGradient(), Opacity = 0, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 38 } };
            var fill = new Border { CornerRadius = new CornerRadius(radius - 6), Margin = new Thickness(thick - 2), Background = Frozen(LogoFill), Opacity = 0 };
            double inner = size - thick;
            var edgeGeo = new RectangleGeometry(new Rect(thick / 2, thick / 2, inner, inner), radius - thick / 2, radius - thick / 2);
            double perim = 4 * inner - 8 * (radius - thick / 2) + 2 * Math.PI * (radius - thick / 2);
            double dash = perim / thick + 1;
            var edge = new System.Windows.Shapes.Path { Data = edgeGeo, Stroke = LogoGradient(), StrokeThickness = thick, StrokeDashArray = new DoubleCollection { dash, dash }, StrokeDashOffset = still ? 0 : dash, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            // the TV: an outline, a short neck and a foot, scaled for a 240 badge (the icon's 256 grid)
            double k = size / 256.0;
            var screenGroup = new Grid { RenderTransformOrigin = new Point(0.5, 0.45), Opacity = 0 };
            var screenScale = new ScaleTransform(1, 1);
            screenGroup.RenderTransform = screenScale;
            var screen = new System.Windows.Shapes.Path { Data = new RectangleGeometry(new Rect(54 * k, 66 * k, 148 * k, 104 * k), 18 * k, 18 * k), Stroke = Brushes.White, StrokeThickness = 10 * k };
            var neck = new System.Windows.Shapes.Path { Data = new LineGeometry(new Point(128 * k, 170 * k), new Point(128 * k, 184 * k)), Stroke = Brushes.White, StrokeThickness = 10 * k };
            var foot = new System.Windows.Shapes.Path { Data = new RectangleGeometry(new Rect(108 * k, 182 * k, 40 * k, 10 * k), 5 * k, 5 * k), Fill = Brushes.White };
            screenGroup.Children.Add(screen);
            screenGroup.Children.Add(neck);
            screenGroup.Children.Add(foot);
            var play = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(string.Format(System.Globalization.CultureInfo.InvariantCulture, "M{0},{1} L{0},{2} L{3},{4} Z", 112 * k, 92 * k, 144 * k, 152 * k, 118 * k)),
                Fill = LogoGradient(), StrokeLineJoin = PenLineJoin.Round, Stroke = LogoGradient(), StrokeThickness = 4 * k,
                Opacity = 0
            };
            // (the triangle's centre for its pop: around 128,118 of 256)
            var playScale = new ScaleTransform(1, 1, 128 * k, 118 * k);
            play.RenderTransform = playScale;
            // the shine: a soft white band that runs across the badge once
            var shineBand = new Border
            {
                Width = 70, Margin = new Thickness(-140, -40, 0, -40), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(110, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }, new Point(0, 0), new Point(1, 0)),
                Opacity = 0
            };
            var shineMove = new TranslateTransform();
            var shineGroup = new TransformGroup();
            shineGroup.Children.Add(new SkewTransform(-18, 0));
            shineGroup.Children.Add(shineMove);
            shineBand.RenderTransform = shineGroup;
            var shineClip = new Grid { ClipToBounds = true, Margin = new Thickness(thick), Clip = new RectangleGeometry(new Rect(0, 0, size - 2 * thick, size - 2 * thick), radius - thick, radius - thick) };
            shineClip.Children.Add(shineBand);
            badge.Children.Add(glow);
            badge.Children.Add(fill);
            badge.Children.Add(edge);
            badge.Children.Add(screenGroup);
            badge.Children.Add(play);
            badge.Children.Add(shineClip);
            stack.Children.Add(badge);

            // the name
            var name = new TextBlock
            {
                Text = "Kippix", FontSize = 64, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 34, 0, 0), Opacity = 0, RenderTransform = new TranslateTransform(0, 18)
            };
            try { var f = Application.Current.TryFindResource("FontTitilliumWebBold") as FontFamily; if (f != null) name.FontFamily = f; } catch (Exception) { }
            var nameUnder = new Border { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), Background = LogoGradient(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            stack.Children.Add(name);
            stack.Children.Add(nameUnder);
            layer.Children.Add(root);

            if (settings.IntroSound) { introSoundOn = true; PlayChime(outro); UpdateMusicState(); }   // (the music waits for the chime)

            if (still)
            {
                // Reduce motion: the finished logo, still, for a moment
                glow.Opacity = 0.45; fill.Opacity = 1; screenGroup.Opacity = 1; play.Opacity = 1; name.Opacity = 1; nameUnder.Width = 180;
                ((TranslateTransform)name.RenderTransform).Y = 0;
                Later(1600, gen, () => EndIntro(false));
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Func<double, double, double, double, DoubleAnimation> anim = (from, to, startMs, ms) =>
            {
                var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(startMs), EasingFunction = ease };
                return a;
            };
            if (!outro)
            {
                edge.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, anim(dash, 0, 0, 650));
                glow.BeginAnimation(UIElement.OpacityProperty, anim(0, 0.5, 200, 700));
                fill.BeginAnimation(UIElement.OpacityProperty, anim(0, 1, 350, 400));
                screenGroup.BeginAnimation(UIElement.OpacityProperty, anim(0, 1, 500, 350));
                screenScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim(0.82, 1, 500, 450));
                screenScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim(0.82, 1, 500, 450));
                play.BeginAnimation(UIElement.OpacityProperty, anim(0, 1, 820, 120));
                var pop = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(820) };
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), ease));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                playScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                playScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
                name.BeginAnimation(UIElement.OpacityProperty, anim(0, 1, 1050, 450));
                ((TranslateTransform)name.RenderTransform).BeginAnimation(TranslateTransform.YProperty, anim(18, 0, 1050, 500));
                nameUnder.BeginAnimation(FrameworkElement.WidthProperty, anim(0, 180, 1250, 500));
                shineBand.BeginAnimation(UIElement.OpacityProperty, anim(0, 1, 1500, 60));
                shineMove.BeginAnimation(TranslateTransform.XProperty, anim(0, size + 220, 1500, 650));
                // a gentle breath at the end, then it's done (the layer fades to the home screen)
                badgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim(1, 1.04, 2050, 450));
                badgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim(1, 1.04, 2050, 450));
                Later(2550, gen, () => { introEndWhy = "the logo finished"; EndIntro(false); });
            }
            else
            {
                // the outro: the logo is there at once, the play button fades, then the screen switches off like an old TV
                edge.StrokeDashOffset = 0; glow.Opacity = 0.5; fill.Opacity = 1; screenGroup.Opacity = 1; play.Opacity = 1; name.Opacity = 1; nameUnder.Width = 180;
                ((TranslateTransform)name.RenderTransform).Y = 0;
                root.Opacity = 0;
                root.BeginAnimation(UIElement.OpacityProperty, anim(0, 1, 150, 350));
                play.BeginAnimation(UIElement.OpacityProperty, anim(1, 0, 900, 300));
                name.BeginAnimation(UIElement.OpacityProperty, anim(1, 0, 900, 400));
                nameUnder.BeginAnimation(FrameworkElement.WidthProperty, anim(180, 0, 900, 400));
                var offY = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(1300) };
                offY.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                offY.KeyFrames.Add(new EasingDoubleKeyFrame(0.02, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseIn }));
                var offX = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(1300) };
                offX.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                offX.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
                offX.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new CubicEase { EasingMode = EasingMode.EaseIn }));
                badgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, offY);
                badgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, offX);
                glow.BeginAnimation(UIElement.OpacityProperty, anim(0.5, 1, 1300, 260));
                Later(2000, gen, () => EndIntro(false));
            }
        }

        /// <summary>Runs <paramref name="act"/> after a while, if the same intro / outro is still on.</summary>
        private void Later(int ms, int gen, Action act)
        {
            var t = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); if (gen == introGen && introOn && !introClosing) { try { act(); } catch (Exception) { } } };
            t.Start();
        }

        /// <summary>The logo's chime: a soft rising chord (falling for the outro), made here once and kept in ExtensionsData\intro\.</summary>
        private void PlayChime(bool outro)
        {
            try
            {
                string file = System.IO.Path.Combine(IntroFolder, outro ? "chime-out.wav" : "chime-in.wav");
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(IntroFolder);
                    File.WriteAllBytes(file, MakeChime(outro));
                }
                introChime = WavSound.Load(file);
                if (introChime != null) introChime.Play(Math.Max(10, SoundVolumeNow));
            }
            catch (Exception ex) { logger.Warn("Kippix: intro chime: " + ex.Message); }
        }

        private static byte[] MakeChime(bool outro)
        {
            const int rate = 22050;
            double seconds = 2.2;
            int n = (int)(rate * seconds);
            var pcm = new short[n];
            double[] notes = outro ? new[] { 1046.5, 783.99, 659.25, 523.25 } : new[] { 523.25, 659.25, 783.99, 1046.5 };   // C5 E5 G5 C6
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, v = 0;
                for (int j = 0; j < notes.Length; j++)
                {
                    double start = 0.12 + j * 0.16;
                    if (t < start) continue;
                    double tt = t - start;
                    double env = Math.Min(1, tt / 0.012) * Math.Exp(-tt * 2.6);
                    v += env * (Math.Sin(2 * Math.PI * notes[j] * tt) + 0.25 * Math.Sin(2 * Math.PI * notes[j] * 2 * tt));
                }
                // a low, soft pad under it
                double pad = Math.Min(1, t / 0.3) * Math.Exp(-t * 1.4) * 0.35 * Math.Sin(2 * Math.PI * 130.81 * t);
                v = (v * 0.22 + pad) * 0.8;
                pcm[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v * 32767));
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + n * 2);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(n * 2);
                foreach (short s in pcm) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}

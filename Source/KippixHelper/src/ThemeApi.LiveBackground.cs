using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Home settings › Background › Trailer (muted) and Slideshow ("live backgrounds", HomeBgMode 4 and 5).
    ///   Trailer (muted): once the highlight has rested on a game for 1.5 s, its trailer (the same ones as the game page and the
    ///   previews: Extra Metadata Loader's or the cached Steam trailer) fades in over the art, muted and looping. No trailer:
    ///   the art stays.
    ///   Slideshow: the game's background, your screenshots of it (the gallery) and, for Steam games, the store's screenshots,
    ///   one after another every 8 s with a slow crossfade.
    /// Both pause (keeping their place) while a panel or the game page covers the home screen, while a game runs, under the
    /// intro, and on battery when handheld mode asks (HandheldPausesLive). A 250 ms timer runs only while one of them (or
    /// Animated art) is chosen in fullscreen. The pictures / video sit in KX_LiveBgHost, inside KX_BgLayers (so Depth moves them too).
    /// </summary>
    public partial class ThemeApi
    {
        private const double LiveRestSeconds = 1.5;
        private const double SlideSeconds = 8;

        /// <summary>
        /// Handheld mode can set this: true = live backgrounds (and animated art) pause, e.g. on battery. Null: never.
        /// (Kept as a hook so that feature owns its own rule.)
        /// </summary>
        internal Func<bool> HandheldPausesLive;

        private DispatcherTimer liveTimer;
        private Guid liveGame = Guid.Empty;
        private DateTime liveSince;
        private bool liveStarted;
        private int liveRequest, liveFadeGen;
        private bool liveCovered;
        private MediaElement liveVideo;
        private bool liveVideoOn, liveVideoShown, liveVideoPaused;
        private Image slideA, slideB;
        private bool slideOnA;
        private List<string> slideFiles;
        private int slideAt;
        private DateTime slideNextAt;
        private bool slideBusy, slidesOn;
        private static readonly Dictionary<string, List<string>> steamShots = new Dictionary<string, List<string>>();

        public bool IsHomeBgLive { get { return settings.HomeBgMode == 4 || settings.HomeBgMode == 5; } }
        public bool IsHomeBgLiveTrailer { get { return settings.HomeBgMode == 4; } }
        public bool IsHomeBgSlides { get { return settings.HomeBgMode == 5; } }
        public bool LiveSlideWeb
        {
            get { return settings.LiveSlideWeb; }
            set { if (settings.LiveSlideWeb == value) return; settings.LiveSlideWeb = value; SaveSettings(); Notify("LiveSlideWeb"); StopLiveMedia(false); liveGame = Guid.Empty; }
        }

        private bool LiveBatteryPause()
        {
            try { var f = HandheldPausesLive; return f != null && f(); } catch (Exception) { return false; }
        }

        private Grid LiveHost()
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            return win != null ? UiTools.FindByName(win, "KX_LiveBgHost") as Grid : null;
        }

        /// <summary>Starts the timer when a live background or animated art is chosen (fullscreen only), stops it (and them) otherwise.</summary>
        private void EnsureLiveTimer()
        {
            if (!IsFullscreen || ui == null) return;
            bool want = IsHomeBgLive || settings.AnimArtMode != 0;
            if (!want)
            {
                if (liveTimer != null && liveTimer.IsEnabled) liveTimer.Stop();
                StopLiveMedia(true);
                AnimArtStopAll();
                liveGame = Guid.Empty;
                return;
            }
            if (!IsHomeBgLive) StopLiveMedia(true);
            if (liveTimer == null)
            {
                liveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
                liveTimer.Tick += (s, e) => { try { LiveTick(); } catch (Exception ex) { logger.Warn("Kippix: live background: " + ex.Message); } };
            }
            if (!liveTimer.IsEnabled) liveTimer.Start();
        }

        /// <summary>Another game was highlighted: what was playing goes (crossfading back to the art), and the rest starts again.</summary>
        private void LiveSelectionChanged()
        {
            if (!IsFullscreen) return;
            EnsureLiveTimer();
        }

        /// <summary>The intro / outro covers everything: the video holds still.</summary>
        private void PauseLiveForIntro()
        {
            liveCovered = true;
            PauseLiveMedia();
        }

        private void LiveTick()
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            Game game = null;
            try { game = api.MainView.SelectedGames != null ? api.MainView.SelectedGames.FirstOrDefault() : null; } catch (Exception) { }
            bool visible = win != null && win.IsActive && !detailsOpen && openPanel == null && !gameRunningNow && !libraryLoading && !ambientOn
                           && !HomeBgBlack && !IntroCovering && stageOpacity >= 1 && !LiveBatteryPause();
            liveCovered = !visible;

            Guid id = game != null ? game.Id : Guid.Empty;
            if (id != liveGame)
            {
                StopLiveMedia(true);
                liveGame = id;
                liveSince = DateTime.Now;
                liveStarted = false;
            }

            AnimArtTick(game, visible);   // Background › Animated art (cards, hero and the living picture)

            if (!IsHomeBgLive || game == null) { if (liveVideoOn || slidesOn) StopLiveMedia(true); return; }
            // the card preview playing in its card: the background trailer holds still meanwhile
            bool trailerFree = visible && !(previewPlaying && settings.PreviewMode == 1);
            if (!visible) { PauseLiveMedia(); liveSince = liveStarted ? liveSince : DateTime.Now; return; }

            if (!liveStarted)
            {
                if ((DateTime.Now - liveSince).TotalSeconds < LiveRestSeconds) return;
                liveStarted = true;
                if (settings.HomeBgMode == 4) { if (!NightNoAutoplayNow) StartLiveTrailer(game); }
                else StartSlides(game);
                return;
            }
            if (settings.HomeBgMode == 4)
            {
                if (trailerFree) ResumeLiveMedia(); else PauseLiveMedia();
            }
            else if (slidesOn && !slideBusy && DateTime.Now >= slideNextAt) NextSlide();
        }

        // ---------------- the trailer ----------------

        private void StartLiveTrailer(Game game)
        {
            int request = ++liveRequest;
            string appId = game.GameId ?? "";
            string name = game.Name ?? "?";
            ResolveTrailer(game, files =>
            {
                bool none = files == null || string.IsNullOrEmpty(files.Video);
                LogLiveOnce(name + (none ? ": no trailer, the art stays" : ": its trailer (" + Path.GetFileName(files.Video) + ")"));
                if (request != liveRequest || none || settings.HomeBgMode != 4) return;   // none: the art stays
                PlayLiveTrailer(files.Video);
            }, () =>
            {
                // a Steam trailer downloads first (at most two downloads at once, shared with the previews); it plays when it's there
                lock (trailerWaiters) return trailerWaiters.ContainsKey(appId) || trailerWaiters.Count < 2;
            });
        }

        private readonly HashSet<string> liveLogged = new HashSet<string>();

        /// <summary>"Kippix: live background: X: its trailer (…)", once per game and answer a session.</summary>
        private void LogLiveOnce(string text)
        {
            lock (liveLogged) { if (!liveLogged.Add(text)) return; if (liveLogged.Count > 2000) liveLogged.Clear(); }
            logger.Info("Kippix: live background: " + text);
        }

        private void PlayLiveTrailer(string path)
        {
            var host = LiveHost();
            if (host == null) return;
            if (liveVideo == null || !ReferenceEquals(VisualTreeHelper.GetParent(liveVideo), host))
            {
                liveVideo = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    IsMuted = true, Volume = 0, IsHitTestVisible = false, Opacity = 0, ScrubbingEnabled = false
                };
                var me = liveVideo;
                me.MediaOpened += (s, e) =>
                {
                    if (!ReferenceEquals(s, liveVideo) || !liveVideoOn) return;
                    liveVideoShown = true;
                    SetMotionPaused(true);   // the background zoom holds still under it
                    me.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                };
                me.MediaEnded += (s, e) =>
                {
                    if (!ReferenceEquals(s, liveVideo) || !liveVideoOn) return;
                    try { me.Position = TimeSpan.Zero; me.Play(); } catch (Exception) { }   // loops
                };
                me.MediaFailed += (s, e) =>
                {
                    if (!ReferenceEquals(s, liveVideo)) return;
                    logger.Warn("Kippix: live background trailer couldn't play: " + (e.ErrorException != null ? e.ErrorException.Message : "unknown"));
                    StopLiveMedia(true);   // the art stays
                };
                host.Children.Insert(0, me);
            }
            liveFadeGen++;   // (a fade-out still running from the last game mustn't stop this one)
            liveVideo.BeginAnimation(UIElement.OpacityProperty, null);
            liveVideo.Opacity = 0;
            liveVideo.Visibility = Visibility.Visible;
            liveVideoOn = true;
            liveVideoShown = false;
            liveVideoPaused = false;
            try
            {
                liveVideo.Source = new Uri(path);
                liveVideo.Play();
                if (liveCovered) PauseLiveMedia();
            }
            catch (Exception ex) { logger.Warn("Kippix: live background trailer: " + ex.Message); StopLiveMedia(false); }
        }

        private void PauseLiveMedia()
        {
            if (liveVideo != null && liveVideoOn && !liveVideoPaused)
            {
                try { liveVideo.Pause(); liveVideoPaused = true; } catch (Exception) { }
            }
            if (slidesOn) slideNextAt = DateTime.Now.AddSeconds(SlideSeconds);   // (the slideshow waits, then carries on where it was)
        }

        private void ResumeLiveMedia()
        {
            if (liveVideo != null && liveVideoOn && liveVideoPaused)
            {
                try { liveVideo.Play(); liveVideoPaused = false; } catch (Exception) { }
            }
        }

        /// <summary>Stops the trailer / slideshow: a short fade back to the art (or at once).</summary>
        private void StopLiveMedia(bool fade)
        {
            liveRequest++;
            int gen = ++liveFadeGen;
            bool hadVideo = liveVideoOn;
            liveVideoOn = false;
            liveVideoPaused = false;
            if (liveVideo != null && hadVideo)
            {
                var v = liveVideo;
                if (fade && liveVideoShown && !settings.ReduceMotion)
                {
                    var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
                    a.Completed += (s, e) => { if (gen == liveFadeGen) { try { v.Stop(); v.Source = null; v.Visibility = Visibility.Collapsed; } catch (Exception) { } } };
                    v.BeginAnimation(UIElement.OpacityProperty, a);
                }
                else
                {
                    v.BeginAnimation(UIElement.OpacityProperty, null);
                    v.Opacity = 0;
                    try { v.Stop(); v.Source = null; v.Visibility = Visibility.Collapsed; } catch (Exception) { }
                }
            }
            if (liveVideoShown) SetMotionPaused(previewShown);
            liveVideoShown = false;
            StopSlides(fade, gen);
        }

        // ---------------- the slideshow ----------------

        private void StartSlides(Game game)
        {
            var host = LiveHost();
            if (host == null) return;
            if (slideA == null || !ReferenceEquals(VisualTreeHelper.GetParent(slideA), host))
            {
                slideA = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Opacity = 0 };
                slideB = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Opacity = 0 };
                int at = liveVideo != null && ReferenceEquals(VisualTreeHelper.GetParent(liveVideo), host) ? 1 : 0;
                host.Children.Insert(at, slideA);
                host.Children.Insert(at + 1, slideB);
            }
            int request = ++liveRequest;
            // what to show: worked out off the UI thread (the store's screenshots come from the web)
            string bg = null;
            try
            {
                if (!string.IsNullOrEmpty(game.BackgroundImage))
                    bg = game.BackgroundImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? game.BackgroundImage : api.Database.GetFullFilePath(game.BackgroundImage);
            }
            catch (Exception) { }
            var mine = new List<string>();
            try { mine = galleryFiles.Where(f => f.GameId == game.Id && !f.IsVideo).OrderByDescending(f => f.Date).Take(12).Select(f => f.Path).ToList(); } catch (Exception) { }
            string appId = game.PluginId == SteamPluginId ? game.GameId : null;
            bool web = settings.LiveSlideWeb;
            KippixShared.SafeWork.Queue(_ =>
            {
                var list = new List<string>();
                if (!string.IsNullOrEmpty(bg)) list.Add(bg);
                list.AddRange(mine.Where(File.Exists));
                if (web && !string.IsNullOrEmpty(appId)) list.AddRange(SteamScreenshots(appId));
                OnUi(() =>
                {
                    if (request != liveRequest || settings.HomeBgMode != 5) return;
                    slideFiles = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    slideAt = 0;   // (the first is the art already showing)
                    slidesOn = slideFiles.Count > 1;
                    slideNextAt = DateTime.Now.AddSeconds(SlideSeconds);
                });
            });
        }

        /// <summary>The Steam store's screenshots for a game (asked once per session, at most 10).</summary>
        private static List<string> SteamScreenshots(string appId)
        {
            lock (steamShots)
            {
                List<string> hit;
                if (steamShots.TryGetValue(appId, out hit)) return hit;
            }
            var list = new List<string>();
            try
            {
                var root = StoreJson("https://store.steampowered.com/api/appdetails?filters=screenshots&appids=" + Uri.EscapeDataString(appId)) as Dictionary<string, object>;
                var shots = Dig(root, appId, "data", "screenshots") as System.Collections.IEnumerable;
                if (shots != null)
                    foreach (var s in shots.Cast<object>().OfType<Dictionary<string, object>>())
                    {
                        object p;
                        if (s.TryGetValue("path_full", out p) && p is string) list.Add((string)p);
                        if (list.Count >= 10) break;
                    }
            }
            catch (Exception ex) { logger.Info("Kippix: slideshow, Steam screenshots for " + appId + ": " + ex.Message); }
            lock (steamShots) steamShots[appId] = list;
            return list;
        }

        private void NextSlide()
        {
            if (slideFiles == null || slideFiles.Count < 2) return;
            slideBusy = true;
            int next = (slideAt + 1) % slideFiles.Count;
            string path = slideFiles[next];
            int request = liveRequest;
            int width = (int)Math.Min(2560, Math.Max(1280, SystemParameters.PrimaryScreenWidth));
            KippixShared.SafeWork.Queue(_ =>
            {
                ImageSource img = null;
                try { img = Images.LoadUncached(path, width); } catch (Exception) { }
                OnUi(() =>
                {
                    slideBusy = false;
                    if (request != liveRequest || !slidesOn) return;
                    slideAt = next;
                    slideNextAt = DateTime.Now.AddSeconds(SlideSeconds);
                    if (img == null) { slideNextAt = DateTime.Now.AddSeconds(0.5); return; }   // (a picture that won't load: the next one soon)
                    ShowSlide(img);
                });
            });
        }

        private void ShowSlide(ImageSource img)
        {
            var incoming = slideOnA ? slideB : slideA;
            var outgoing = slideOnA ? slideA : slideB;
            if (incoming == null || outgoing == null) return;
            slideOnA = !slideOnA;
            incoming.Source = img;
            Panel.SetZIndex(incoming, 2);
            Panel.SetZIndex(outgoing, 1);
            var time = TimeSpan.FromMilliseconds(settings.ReduceMotion ? 300 : 1400);
            var a = new DoubleAnimation(0, 1, time) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            a.Completed += (s, e) => { if (ReferenceEquals(incoming, slideOnA ? slideA : slideB)) { outgoing.BeginAnimation(UIElement.OpacityProperty, null); outgoing.Opacity = 0; outgoing.Source = null; } };
            incoming.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private void StopSlides(bool fade, int gen)
        {
            bool was = slidesOn || (slideA != null && (slideA.Source != null || slideB.Source != null));
            slidesOn = false;
            slideFiles = null;
            slideBusy = false;
            if (!was || slideA == null) return;
            foreach (var im in new[] { slideA, slideB })
            {
                var i = im;
                if (i.Source == null) continue;
                if (fade && !settings.ReduceMotion)
                {
                    var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
                    a.Completed += (s, e) => { if (gen == liveFadeGen) i.Source = null; };
                    i.BeginAnimation(UIElement.OpacityProperty, a);
                }
                else
                {
                    i.BeginAnimation(UIElement.OpacityProperty, null);
                    i.Opacity = 0;
                    i.Source = null;
                }
            }
        }
    }
}

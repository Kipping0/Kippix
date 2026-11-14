using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Trailer preview on the home screen: when a game card stays selected for a while, its trailer plays
    /// in the card (which widens to fit the video) or behind everything as the background.
    /// Moving to another card stops it and the countdown starts again.
    /// Uses the same trailers as the game page (Extra Metadata Loader's, or the Steam store trailer).
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly int[] PreviewDelays = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15, 20, 25, 30, 40, 50, 60, 75, 90, 105, 120 };
        private const double PreviewTrackWidth = 480;
        private const double PreviewVolume = 0.8;

        private DispatcherTimer previewTimer;
        private Guid previewGame = Guid.Empty;
        private DateTime previewSince;
        private DateTime previewPausedUntil = DateTime.MinValue;
        private bool previewStarted;    // countdown done, trailer asked for
        private bool previewPlaying;    // video running
        private bool previewShown;      // video open and faded in
        private int previewRequest;
        private bool previewHasAudioFile;
        private MediaElement previewVideo;
        private MediaPlayer previewAudio;
        private FrameworkElement previewHost;
        private ListBox previewList;
        private readonly HashSet<MediaElement> previewHooked = new HashSet<MediaElement>();

        public ICommand CyclePreviewModeCommand { get; private set; }

        private void InitPreviewCommands()
        {
            // "In the background" is Background › Trailer preview: one setting, kept in step both ways (SetPreviewMode, ThemeApi.HomeBackground.cs)
            CyclePreviewModeCommand = new RelayCommand(_ => { SetPreviewMode((settings.PreviewMode + 1) % 3); PlaySound("move"); });
        }

        // ---------------- settings ----------------

        public bool PreviewEnabled { get { return settings.PreviewMode != 0; } }

        public string PreviewModeText
        {
            get
            {
                if (settings.PreviewMode == 1) return Loc("LOC_KX_PreviewInCard", "In the card");
                if (settings.PreviewMode == 2) return Loc("LOC_KX_PreviewInBackground", "In the background");
                return Loc("LOC_KX_Off", "Off");
            }
        }

        public bool PreviewSound
        {
            get { return settings.PreviewSound; }
            set { if (SetSetting(ref settings.PreviewSound, value)) { NotifyPreview(); ApplyPreviewSound(); } }
        }

        private int PreviewDelayIndex
        {
            get
            {
                int best = 0;
                for (int i = 0; i < PreviewDelays.Length; i++)
                    if (Math.Abs(PreviewDelays[i] - settings.PreviewDelay) < Math.Abs(PreviewDelays[best] - settings.PreviewDelay)) best = i;
                return best;
            }
        }

        public string PreviewDelayText
        {
            get
            {
                int d = settings.PreviewDelay;
                if (d == 0) return Loc("LOC_KX_PreviewNow", "Straight away");
                if (d < 60) return string.Format(Loc("LOC_KX_Seconds", "{0} s"), d);
                int m = d / 60, sec = d % 60;
                return sec == 0 ? string.Format(Loc("LOC_KX_Minutes", "{0} min"), m)
                                : string.Format(Loc("LOC_KX_MinSec", "{0} min {1} s"), m, sec);
            }
        }

        private double PreviewDelayFraction { get { return (double)PreviewDelayIndex / (PreviewDelays.Length - 1); } }
        public double PreviewDelayFillWidth { get { return PreviewDelayFraction * PreviewTrackWidth; } }
        public Thickness PreviewDelayKnobMargin { get { return new Thickness(PreviewDelayFraction * PreviewTrackWidth - 12, 0, 0, 0); } }

        private void NudgePreviewDelay(int direction)
        {
            int i = Math.Max(0, Math.Min(PreviewDelays.Length - 1, PreviewDelayIndex + direction));
            if (PreviewDelays[i] == settings.PreviewDelay) return;
            settings.PreviewDelay = PreviewDelays[i];
            SaveSettings();
            NotifyPreview();
        }

        private void NotifyPreview()
        {
            foreach (string n in new[] { "PreviewEnabled", "PreviewModeText", "PreviewSound", "PreviewDelayText", "PreviewDelayFillWidth", "PreviewDelayKnobMargin",
                                         "IsPreviewBackground", "PreviewSoundText", "HomeBgSummary" })
                Notify(n);
        }

        /// <summary>True while a preview plays with sound (home music fades out meanwhile).</summary>
        private bool PreviewHasSound { get { return previewPlaying && settings.PreviewSound; } }

        // ---------------- countdown ----------------

        private void EnsurePreviewTimer()
        {
            if (ui == null) return;
            if (settings.PreviewMode == 0)
            {
                if (previewTimer != null && previewTimer.IsEnabled) previewTimer.Stop();
                if (previewStarted) StopPreview();
                return;
            }
            if (previewTimer == null)
            {
                previewTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
                previewTimer.Tick += (s, e) => PreviewTick();
            }
            if (!previewTimer.IsEnabled) previewTimer.Start();
        }

        /// <summary>No preview for a while (e.g. a game is starting).</summary>
        private void PausePreviewFor(int seconds)
        {
            previewPausedUntil = DateTime.Now.AddSeconds(seconds);
            StopPreview();
            previewGame = Guid.Empty;
        }

        /// <summary>Another card was selected: stop the video, the countdown starts again.</summary>
        private void PreviewSelectionChanged()
        {
            if (settings.PreviewMode == 0) return;
            StopPreview();
            previewGame = Guid.Empty;
        }

        private ListBox PreviewList(Window win)
        {
            if (previewList != null && PresentationSource.FromVisual(previewList) != null) return previewList;
            previewList = UiTools.FindByName(win, "PART_ListGameItems") as ListBox;
            return previewList;
        }

        private void PreviewTick()
        {
            try
            {
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                var list = win != null ? PreviewList(win) : null;
                Game game = null;
                try { game = api.MainView.SelectedGames != null ? api.MainView.SelectedGames.FirstOrDefault() : null; } catch (Exception) { }

                bool allowed = settings.PreviewMode != 0 && game != null && list != null && win.IsActive
                               && list.IsKeyboardFocusWithin && !detailsOpen && openPanel == null
                               && !gameRunningNow && !libraryLoading && !ambientOn && DateTime.Now >= previewPausedUntil   // (not under the ambient slideshow)
                               && !NightNoAutoplayNow && !IntroCovering   // (Night mode's "no trailers by themselves"; not under the intro)
                               && !(settings.PreviewMode == 2 && HomeBgBlack);   // (0.90: no trailer behind Row › Full Screen / Folder's tiles)
                if (!allowed)
                {
                    if (previewStarted) StopPreview();
                    previewGame = Guid.Empty;
                    return;
                }
                if (game.Id != previewGame)
                {
                    StopPreview();
                    previewGame = game.Id;
                    previewSince = DateTime.Now;
                    return;
                }
                if (!previewStarted)
                {
                    if ((DateTime.Now - previewSince).TotalSeconds >= Math.Max(0.5, settings.PreviewDelay))
                    {
                        previewStarted = true;
                        StartPreview(game);
                    }
                }
                else if (previewShown && settings.PreviewMode == 1) PlaceCardPreview(false);
            }
            catch (Exception ex) { logger.Warn(ex, "Kippix: trailer preview failed"); }
        }

        // ---------------- playing ----------------

        private void StartPreview(Game game)
        {
            int request = ++previewRequest;
            string appId = game.GameId ?? "";
            ResolveTrailer(game, files =>
            {
                if (request != previewRequest || !previewStarted || files == null) return;   // no trailer: the card just stays as it is
                PlayPreview(files);
            }, () =>
            {
                // a Steam trailer has to download first (it plays next time it's selected if you've moved on);
                // at most two downloads at once, so resting on lots of cards doesn't pile them up
                lock (trailerWaiters) return trailerWaiters.ContainsKey(appId) || trailerWaiters.Count < 2;
            });
        }

        private void PlayPreview(TrailerFiles files)
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            if (win == null) return;
            bool inCard = settings.PreviewMode == 1;
            var host = UiTools.FindByName(win, inCard ? "KX_PreviewCard" : "KX_PreviewBg");
            var video = inCard ? UiTools.FindByName(win, "KX_PreviewCardVideo") as MediaElement : host as MediaElement;
            if (host == null || video == null) return;
            previewHost = host;
            previewVideo = video;
            HookPreview(video);

            previewHasAudioFile = files.Audio != null;
            video.IsMuted = !settings.PreviewSound || previewHasAudioFile;
            video.Volume = PreviewVolume;
            video.Source = new Uri(files.Video);
            if (previewHasAudioFile)
            {
                if (previewAudio == null) previewAudio = new MediaPlayer();
                previewAudio.Open(new Uri(files.Audio));
                previewAudio.Volume = settings.PreviewSound ? PreviewVolume : 0;
            }

            host.BeginAnimation(UIElement.OpacityProperty, null);
            host.Opacity = 0;                       // shown once the video has opened (no black box while it loads)
            if (inCard) PlaceCardPreview(false);
            host.Visibility = Visibility.Visible;
            try
            {
                video.Play();
                if (previewHasAudioFile) previewAudio.Play();
            }
            catch (Exception) { StopPreview(); return; }
            previewPlaying = true;
            UpdateMusicState();
        }

        private void HookPreview(MediaElement video)
        {
            if (previewHooked.Contains(video)) return;
            previewHooked.Add(video);
            video.MediaOpened += (s, e) =>
            {
                if (!ReferenceEquals(s, previewVideo) || !previewPlaying) return;
                if (previewHasAudioFile && previewAudio != null) try { previewAudio.Position = previewVideo.Position; } catch (Exception) { }
                previewShown = true;
                SetMotionPaused(true);   // the background zoom holds still while a trailer plays
                if (settings.PreviewMode == 1) PlaceCardPreview(true);
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                previewHost.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            video.MediaEnded += (s, e) =>
            {
                if (!ReferenceEquals(s, previewVideo) || !previewPlaying) return;
                // previews always loop
                previewVideo.Position = TimeSpan.Zero;
                previewVideo.Play();
                if (previewHasAudioFile && previewAudio != null) { previewAudio.Position = TimeSpan.Zero; previewAudio.Play(); }
            };
            video.MediaFailed += (s, e) =>
            {
                if (!ReferenceEquals(s, previewVideo)) return;
                logger.Warn("Kippix: preview couldn't play: " + (e.ErrorException != null ? e.ErrorException.Message : "unknown"));
                StopPreview();
            };
        }

        /// <summary>Puts the video over the selected card: same height and centre, 16:9 wide (widening from the card's own width).</summary>
        private void PlaceCardPreview(bool animate)
        {
            var host = previewHost;
            var list = previewList;
            if (host == null || list == null || list.SelectedItem == null) return;
            FrameworkElement tile;
            if (carActive) tile = CarouselPreviewTile();   // Row's carousel: over its highlighted card (Playnite's row is out of sight)
            else
            {
                var container = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as FrameworkElement;
                if (container == null) return;
                tile = UiTools.FindByName(container, "Tile") ?? container;
            }
            var parent = VisualTreeHelper.GetParent(host) as FrameworkElement;
            if (tile == null || parent == null || tile.ActualWidth <= 0 || tile.ActualHeight <= 0) return;

            Rect r;
            try { r = tile.TransformToVisual(parent).TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight)); }
            catch (Exception) { return; }   // (a carousel card just taken off screen: no common parent any more)
            double h = Math.Round(r.Height);
            double w = Math.Round(h * 16 / 9);
            double maxW = parent.ActualWidth - 48;
            if (w > maxW) { w = maxW; }
            double top = r.Top;
            if (carActive && CarDesign != 0)
            {
                // Tall / Full Screen: the cards are tall, so the video takes their middle (a little wider than the card)
                w = Math.Round(Math.Min(r.Width * 1.9, maxW));
                h = Math.Round(w * 9 / 16);
                top = r.Top + (r.Height - h) / 2;
            }
            double left = r.Left + r.Width / 2 - w / 2;
            if (LayoutNow == 2) left = r.Left;   // the list: over the picture at the start of the line
            left = Math.Max(24, Math.Min(parent.ActualWidth - w - 24, left));
            var to = new Thickness(Math.Round(left), Math.Round(top), 0, 0);

            // the cards either side slide out of the way (only once the video is showing; in one row only: the grid and the list leave them be)
            if (previewShown && LayoutNow == 0)
            {
                if (!carActive) ShiftNeighbours(list, Math.Max(0, r.Left - to.Left), Math.Max(0, to.Left + w - r.Right), animate);
                else if (CarDesign == 0) CarouselShift(Math.Max(0, r.Left - to.Left), Math.Max(0, to.Left + w - r.Right), animate);
            }

            if (!animate && Math.Abs(host.Width - w) < 2 && Math.Abs(host.Height - h) < 2 && Math.Abs(host.Margin.Left - to.Left) < 2 && Math.Abs(host.Margin.Top - to.Top) < 2)
                return;
            host.Width = w;
            host.Height = h;
            host.Margin = to;
            if (!animate || ReduceMotionNow) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(320);
            host.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(Math.Round(r.Width), w, time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            host.BeginAnimation(FrameworkElement.MarginProperty,
                new ThicknessAnimation(new Thickness(Math.Round(r.Left), to.Top, 0, 0), to, time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

        // ---------------- pushing the other cards aside ----------------

        private double shiftLeft, shiftRight;
        private bool neighboursShifted;

        /// <summary>Moves the cards left of the selected one left, and the ones right of it right, so the wider video doesn't cover them.</summary>
        private void ShiftNeighbours(ListBox list, double left, double right, bool animate)
        {
            if (ReduceMotionNow) { left = right = 0; animate = false; }   // Accessibility › Reduce motion: the other cards stay put
            if (neighboursShifted && Math.Abs(left - shiftLeft) < 2 && Math.Abs(right - shiftRight) < 2) return;
            var panel = UiTools.FindChild<VirtualizingPanel>(list);
            if (panel == null) return;
            shiftLeft = left;
            shiftRight = right;
            neighboursShifted = true;
            int selected = list.SelectedIndex;
            int n = VisualTreeHelper.GetChildrenCount(panel);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(panel, i) as FrameworkElement;
                if (c == null) continue;
                int index = list.ItemContainerGenerator.IndexFromContainer(c);
                if (index < 0) continue;
                double x = index < selected ? -left : index > selected ? right : 0;
                SlideTo(c, x, animate ? 320 : 0);
            }
        }

        private void ResetNeighbours()
        {
            CarouselShift(0, 0, true);   // (Row's carousel)
            if (!neighboursShifted) return;
            neighboursShifted = false;
            shiftLeft = shiftRight = 0;
            var panel = previewList != null ? UiTools.FindChild<VirtualizingPanel>(previewList) : null;
            if (panel == null) return;
            int n = VisualTreeHelper.GetChildrenCount(panel);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(panel, i) as FrameworkElement;
                if (c != null) SlideTo(c, 0, 220);
            }
        }

        private static void SlideTo(FrameworkElement c, double x, int ms)
        {
            var t = c.RenderTransform as TranslateTransform;
            if (t == null || t.IsFrozen)
            {
                if (x == 0 && t == null) return;   // never moved
                t = new TranslateTransform();
                c.RenderTransform = t;
            }
            if (ms <= 0)
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.X = x;
                return;
            }
            t.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, Motion(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void ApplyPreviewSound()
        {
            if (previewVideo != null) previewVideo.IsMuted = !settings.PreviewSound || previewHasAudioFile;
            if (previewAudio != null) previewAudio.Volume = settings.PreviewSound ? PreviewVolume : 0;
            UpdateMusicState();
        }

        private void StopPreview()
        {
            previewRequest++;
            previewStarted = false;
            previewShown = false;
            SetMotionPaused(false);
            if (previewVideo != null)
            {
                try { previewVideo.Stop(); previewVideo.Source = null; } catch (Exception) { }
            }
            if (previewAudio != null)
            {
                try { previewAudio.Stop(); previewAudio.Close(); } catch (Exception) { }
            }
            if (previewHost != null)
            {
                previewHost.BeginAnimation(UIElement.OpacityProperty, null);
                previewHost.BeginAnimation(FrameworkElement.WidthProperty, null);
                previewHost.BeginAnimation(FrameworkElement.MarginProperty, null);
                previewHost.Opacity = 0;
                previewHost.Visibility = Visibility.Collapsed;
            }
            ResetNeighbours();
            bool was = previewPlaying;
            previewPlaying = false;
            if (was) UpdateMusicState();
        }
    }
}

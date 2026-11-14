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
    /// (0.99.17) Getting started › Glow and background: the background choices that move show it in the preview, with the same
    /// game as the card. Trailer (muted): its trailer straight away, looping, no sound. Art then trailer: the art, then after a
    /// moment the trailer. Slideshow: its art, your screenshots of it and (when that's on) its Steam screenshots, one after another.
    /// It all stops when the highlight leaves those rows or the step.
    /// </summary>
    public partial class ThemeApi
    {
        private Game tourCardGame;
        private MediaElement tourBgVideo;
        private Image tourSlideA, tourSlideB;
        private DispatcherTimer tourBgTimer;
        private int tourBgMediaGen;
        private List<string> tourSlides;
        private int tourSlideAt;

        private Grid TourBgMediaHost()
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            return win != null ? UiTools.FindByName(win, "KX_TourBgMedia") as Grid : null;
        }

        /// <summary>The preview's moving part for background choice <paramref name="m"/> (nothing for the still ones).</summary>
        private void TourBgMediaFor(int m)
        {
            TourBgMediaStop();
            if (tourCardGame == null || (m != 4 && m != 2 && m != 5)) return;
            int gen = ++tourBgMediaGen;
            var game = tourCardGame;
            if (m == 5) { TourSlidesStart(game, gen); return; }
            ResolveTrailer(game, files =>
            {
                if (gen != tourBgMediaGen) return;
                if (files == null || string.IsNullOrEmpty(files.Video)) { TourBgBadgeSet(Loc("LOC_KX_TourBgNoTrailer", "No trailer for this game")); return; }
                if (m == 2)
                {
                    // the art first, then the trailer after a moment (as the real one does)
                    TourBgTimerRun(TimeSpan.FromSeconds(2), () => { if (gen == tourBgMediaGen) TourVideoPlay(files.Video, gen); });
                }
                else TourVideoPlay(files.Video, gen);
            }, () => { if (gen == tourBgMediaGen) TourBgBadgeSet(Loc("LOC_KX_TrailerLoading", "Loading the trailer…")); return gen == tourBgMediaGen; });
        }

        private void TourBgBadgeSet(string text)
        {
            TourBgBadge = text;
            Notify("TourBgBadge");
            Notify("HasTourBgBadge");
        }

        private void TourVideoPlay(string file, int gen)
        {
            var host = TourBgMediaHost();
            if (host == null || gen != tourBgMediaGen) return;
            if (tourBgVideo == null || !ReferenceEquals(VisualTreeHelper.GetParent(tourBgVideo), host))
            {
                tourBgVideo = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.UniformToFill,
                    IsMuted = true, Volume = 0, IsHitTestVisible = false, Opacity = 0, ScrubbingEnabled = false
                };
                RenderOptions.SetBitmapScalingMode(tourBgVideo, BitmapScalingMode.HighQuality);
                var me = tourBgVideo;
                me.MediaOpened += (s, e) => me.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(500)));
                me.MediaEnded += (s, e) => { try { me.Position = TimeSpan.Zero; me.Play(); } catch (Exception) { } };   // loops
                me.MediaFailed += (s, e) => { logger.Warn("Kippix: getting started: the trailer couldn't play: " + (e.ErrorException != null ? e.ErrorException.Message : "")); TourBgMediaStop(); };
                host.Children.Add(me);
            }
            try
            {
                tourBgVideo.BeginAnimation(UIElement.OpacityProperty, null);
                tourBgVideo.Opacity = 0;
                tourBgVideo.Visibility = Visibility.Visible;
                tourBgVideo.Source = new Uri(file);
                tourBgVideo.Play();
            }
            catch (Exception ex) { logger.Warn("Kippix: getting started: trailer: " + ex.Message); }
        }

        private void TourSlidesStart(Game game, int gen)
        {
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
                if (web && !string.IsNullOrEmpty(appId)) { try { list.AddRange(SteamScreenshots(appId)); } catch (Exception) { } }
                list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                OnUi(() =>
                {
                    if (gen != tourBgMediaGen) return;
                    if (list.Count < 2) { TourBgBadgeSet(Loc("LOC_KX_TourBgNoSlides", "Only one picture for this game")); return; }
                    tourSlides = list;
                    tourSlideAt = 0;
                    TourSlideNext(gen);
                });
            });
        }

        private void TourSlideNext(int gen)
        {
            if (gen != tourBgMediaGen || tourSlides == null || tourSlides.Count == 0) return;
            tourSlideAt = (tourSlideAt + 1) % tourSlides.Count;
            string file = tourSlides[tourSlideAt];
            KippixShared.SafeWork.Queue(_ =>
            {
                ImageSource img = null;
                try { img = Images.Load(file, 1000); } catch (Exception) { }
                OnUi(() =>
                {
                    if (gen != tourBgMediaGen) return;
                    if (img != null) TourSlideShow(img);
                    TourBgTimerRun(TimeSpan.FromSeconds(2.6), () => TourSlideNext(gen));
                });
            });
        }

        /// <summary>The next picture fades in over the last one.</summary>
        private void TourSlideShow(ImageSource img)
        {
            var host = TourBgMediaHost();
            if (host == null) return;
            if (tourSlideA == null || !ReferenceEquals(VisualTreeHelper.GetParent(tourSlideA), host))
            {
                tourSlideA = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Opacity = 0 };
                tourSlideB = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Opacity = 0 };
                host.Children.Add(tourSlideA);
                host.Children.Add(tourSlideB);
            }
            var top = tourSlideB.Opacity > 0.5 ? tourSlideA : tourSlideB;
            var under = ReferenceEquals(top, tourSlideA) ? tourSlideB : tourSlideA;
            host.Children.Remove(top);
            host.Children.Add(top);   // (on top)
            top.Source = img;
            top.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)));
            under.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(650) });
        }

        private void TourBgTimerRun(TimeSpan after, Action then)
        {
            if (tourBgTimer == null) tourBgTimer = new DispatcherTimer();
            tourBgTimer.Stop();
            tourBgTimer.Interval = after;
            EventHandler h = null;
            h = (s, e) => { tourBgTimer.Stop(); tourBgTimer.Tick -= h; then(); };
            tourBgTimer.Tick += h;
            tourBgTimer.Start();
        }

        /// <summary>Everything moving in the preview stops (the still picture stays).</summary>
        private void TourBgMediaStop()
        {
            tourBgMediaGen++;
            tourSlides = null;
            if (tourBgTimer != null) { tourBgTimer.Stop(); tourBgTimer = new DispatcherTimer(); }
            try
            {
                if (tourBgVideo != null)
                {
                    tourBgVideo.Stop();
                    tourBgVideo.Source = null;
                    tourBgVideo.BeginAnimation(UIElement.OpacityProperty, null);
                    tourBgVideo.Opacity = 0;
                    tourBgVideo.Visibility = Visibility.Collapsed;
                }
                foreach (var im in new[] { tourSlideA, tourSlideB })
                    if (im != null) { im.BeginAnimation(UIElement.OpacityProperty, null); im.Opacity = 0; im.Source = null; }
            }
            catch (Exception) { }
        }

        /// <summary>The card's game has a trailer Extra Metadata Loader downloaded (so the trailer choices have something to show).</summary>
        private bool HasLocalTrailer(Game g)
        {
            try
            {
                string dir = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString());
                return File.Exists(Path.Combine(dir, "VideoTrailer.mp4")) || File.Exists(Path.Combine(dir, "VideoMicrotrailer.mp4"));
            }
            catch (Exception) { return false; }
        }
    }
}

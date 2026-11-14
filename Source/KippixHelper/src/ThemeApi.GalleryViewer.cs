using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// The gallery's full screen viewer. Left / Right (or LB / RB) step through, A plays a video, Down goes to the controls,
    /// LT / RT zoom a photo (or skip 10 s in a video), the D-pad moves around a zoomed photo, X favourites, Y shows the details,
    /// B un-zooms, then closes. The controls fade away while a video or slideshow plays and come back on any button.
    /// </summary>
    public partial class ThemeApi
    {
        private bool galleryViewerOpen;
        private int galleryIndex;
        private double galleryZoom = 1, galleryPanX, galleryPanY;
        private bool galleryPlaying, galleryMuted, galleryDetailsOpen, gallerySlideshow, galleryControlsShown = true;
        private double galleryVolume = 0.8;
        private int galleryViewerGen;
        private DispatcherTimer galleryProgressTimer, gallerySlideTimer, galleryHideTimer;
        private MediaElement galleryVideo;
        private Point galleryMouse;

        public ObservableCollection<WidgetLine> GalleryDetails { get; private set; }

        public bool IsGalleryViewerOpen { get { return galleryViewerOpen; } }
        public ImageSource GalleryViewerImage { get; private set; }
        public Uri GalleryViewerVideo { get; private set; }
        public bool GalleryViewerIsVideo { get { return galleryViewerOpen && Current != null && Current.IsVideo; } }
        public bool GalleryViewerIsPhoto { get { return galleryViewerOpen && Current != null && !Current.IsVideo; } }
        public bool GalleryViewerPlaying { get { return galleryPlaying; } }
        public string GalleryViewerTime { get; private set; }
        public double GalleryViewerProgress { get; private set; }   // 0..1100 (the bar's width)
        private const double GalleryBarWidth = 1100;
        public string GalleryViewerCounter { get { return galleryList.Count == 0 ? "" : (galleryIndex + 1) + " / " + galleryList.Count; } }
        public string GalleryViewerTitle { get { var c = Current; return c == null ? "" : c.GameId == Guid.Empty ? Loc("LOC_KX_OtherCaptures", "Other") : c.GameName; } }
        public string GalleryViewerDate { get { var c = Current; return c == null ? "" : c.Date.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture); } }
        public bool GalleryViewerIsFav { get { var c = Current; return c != null && FavSet().Contains(c.Path); } }
        public bool GalleryViewerHasGame { get { var c = Current; return c != null && c.GameId != Guid.Empty && !c.IsVideo; } }
        public bool GalleryViewerIsBackground { get { return ArtSwapFor(Current, true) != null; } }
        public bool GalleryViewerIsCover { get { return ArtSwapFor(Current, false) != null; } }
        public bool GalleryDetailsOpen { get { return galleryDetailsOpen; } }
        public double GalleryVolume { get { return galleryVolume; } }
        public bool GalleryMuted { get { return galleryMuted; } }
        public string GalleryVolumeText { get { return galleryMuted ? Loc("LOC_KX_Muted", "Muted") : Math.Round(galleryVolume * 100) + "%"; } }
        public bool GalleryControlsShown { get { return galleryControlsShown; } }
        public bool GallerySlideshowOn { get { return gallerySlideshow; } }
        public string GalleryZoomText { get { return Math.Round(galleryZoom * 100) + "%"; } }
        public string GalleryViewerStatus { get { return galleryStatus; } }
        public bool HasGalleryViewerStatus { get { return galleryViewerOpen && !string.IsNullOrEmpty(galleryStatus); } }
        private bool GallerySoundActive { get { return galleryViewerOpen && galleryPlaying && !galleryMuted; } }

        private GalleryFile Current { get { return galleryIndex >= 0 && galleryIndex < galleryList.Count ? galleryList[galleryIndex] : null; } }

        public ICommand GalleryPrevCommand { get; private set; }
        public ICommand GalleryNextCommand { get; private set; }
        public ICommand GalleryPlayCommand { get; private set; }
        public ICommand GalleryBack10Command { get; private set; }
        public ICommand GalleryForward10Command { get; private set; }
        public ICommand GalleryVolDownCommand { get; private set; }
        public ICommand GalleryVolUpCommand { get; private set; }
        public ICommand GalleryMuteCommand { get; private set; }
        public ICommand GalleryZoomInCommand { get; private set; }
        public ICommand GalleryZoomOutCommand { get; private set; }
        public ICommand GalleryFavCommand { get; private set; }
        public ICommand GallerySetBackgroundCommand { get; private set; }
        public ICommand GallerySetCoverCommand { get; private set; }
        public ICommand GalleryDetailsCommand { get; private set; }
        public ICommand GalleryDeleteCommand { get; private set; }
        public ICommand GalleryViewerSlideshowCommand { get; private set; }
        public ICommand GallerySurfaceCommand { get; private set; }

        private void InitGalleryViewer()
        {
            InitGalleryFrame();   // (0.99.54)
            InitGalleryEdit();    // (0.99.59) the More menu and Edit
            GalleryPrevCommand = new RelayCommand(_ => GalleryStep(-1));
            GalleryNextCommand = new RelayCommand(_ => GalleryStep(1));
            GalleryPlayCommand = new RelayCommand(_ => ToggleGalleryVideo());
            GalleryBack10Command = new RelayCommand(_ => GallerySeek(-10));
            GalleryForward10Command = new RelayCommand(_ => GallerySeek(10));
            GalleryVolDownCommand = new RelayCommand(_ => SetGalleryVolume(galleryVolume - 0.1));
            GalleryVolUpCommand = new RelayCommand(_ => SetGalleryVolume(galleryVolume + 0.1));
            GalleryMuteCommand = new RelayCommand(_ => { galleryMuted = !galleryMuted; PlaySound("move"); NotifyGalleryViewer(); UpdateMusicState(); });
            GalleryZoomInCommand = new RelayCommand(_ => SetGalleryZoom(NextZoom(1)));
            GalleryZoomOutCommand = new RelayCommand(_ => SetGalleryZoom(NextZoom(-1)));
            GalleryFavCommand = new RelayCommand(_ => ToggleGalleryFavourite());
            GallerySetBackgroundCommand = new RelayCommand(_ => SetGalleryArt(true));
            GallerySetCoverCommand = new RelayCommand(_ => SetGalleryArt(false));
            GalleryDetailsCommand = new RelayCommand(_ => ToggleGalleryDetails());
            GalleryDeleteCommand = new RelayCommand(_ => DeleteGalleryCurrent());
            GalleryViewerSlideshowCommand = new RelayCommand(_ => SetSlideshow(!gallerySlideshow));
            GallerySurfaceCommand = new RelayCommand(_ => { if (Current != null && Current.IsVideo) ToggleGalleryVideo(); else ShowGalleryControls(true); });
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseMoveEvent, new MouseEventHandler((s, e) =>
            {
                if (!galleryViewerOpen) return;
                var w = s as Window;
                if (w == null) return;
                var p = e.GetPosition(w);
                double d = Math.Abs(p.X - galleryMouse.X) + Math.Abs(p.Y - galleryMouse.Y);   // WPF sends a "move" when what's under the pointer changes
                galleryMouse = p;
                if (galleryControlsShown || d <= 3) return;
                // (0.99.54) a real move of the mouse, not a brush of the Steam Controller's right trackpad (it moves the pointer)
                // while reaching for RB: 40 px within half a second, and not straight after a button
                var now = DateTime.Now;
                if ((now - galleryMouseRunAt).TotalMilliseconds > 500) galleryMouseRun = 0;
                galleryMouseRunAt = now;
                galleryMouseRun += d;
                if (galleryMouseRun < 40 || (now - galleryPadAt).TotalMilliseconds < 900) return;
                galleryMouseRun = 0;
                ShowGalleryControls(false);
            }), true);
        }

        private void NotifyGalleryViewer()
        {
            foreach (string n in new[] { "IsGalleryViewerOpen", "GalleryViewerImage", "GalleryViewerVideo", "GalleryViewerIsVideo", "GalleryViewerIsPhoto", "GalleryViewerPlaying",
                                         "GalleryViewerTime", "GalleryViewerProgress", "GalleryViewerCounter", "GalleryViewerTitle", "GalleryViewerDate", "GalleryViewerIsFav", "GalleryViewerIsBackground", "GalleryViewerIsCover",
                                         "GalleryViewerHasGame", "GalleryDetailsOpen", "GalleryVolume", "GalleryMuted", "GalleryVolumeText", "GalleryControlsShown",
                                         "GallerySlideshowOn", "GalleryZoomText", "GalleryViewerStatus", "HasGalleryViewerStatus" })
                Notify(n);
        }

        // ---------------- open / close / step ----------------

        private void OpenGalleryViewer(int index)
        {
            if (galleryList.Count == 0) return;
            PlaySound("select");
            galleryViewerOpen = true;
            galleryDetailsOpen = false;
            galleryControlsShown = true;
            HookGalleryLabel();
            SetGalleryFocusLabel("");
            ShowGalleryItem(index);
            HideGalleryControlsSoon();   // (0.99.48) they go after 10 s
            ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_GallerySurface")), DispatcherPriority.ContextIdle);
        }

        private void CloseGalleryViewer(bool focusTile)
        {
            if (!galleryViewerOpen) return;
            if (galleryEditOpen) CloseGalleryEditor();
            if (galleryMoreOpen) { galleryMoreOpen = false; Notify("GalleryMoreOpen"); }
            var back = Current;
            galleryViewerOpen = false;
            galleryViewerGen++;
            SetSlideshow(false);
            StopGalleryVideo();
            if (galleryProgressTimer != null) galleryProgressTimer.Stop();
            if (galleryHideTimer != null) galleryHideTimer.Stop();
            GalleryViewerImage = null;
            lock (galleryFull) galleryFull.Clear();   // (0.99.50: the pictures read ahead go)
            GalleryViewerVideo = null;
            ResetGalleryTransform();
            galleryZoom = 1;
            NotifyGalleryViewer();
            if (!focusTile) return;
            // the grid may have changed (deletes): rebuild it, then highlight the one you were looking at
            int idx = back != null ? galleryList.IndexOf(back) : -1;
            bool rebuilt = galleryRebuildOnClose || GalleryItems.Count(i => i.IsTile) != Math.Min(galleryList.Count, galleryShown);
            galleryRebuildOnClose = false;
            if (rebuilt) RebuildGallery(false);
            while (idx >= galleryShown && galleryShown < galleryList.Count) AppendGalleryTiles(GalleryPageSize, FavSet());
            var tile = back != null ? GalleryItems.FirstOrDefault(i => i.File == back) : null;
            ui.BeginInvoke(new Action(() =>
            {
                if (tile != null) UiTools.FocusItem("KX_GalleryGrid", tile);
                else FocusGalleryStart();
            }), DispatcherPriority.ContextIdle);
        }

        private void GalleryStep(int step)
        {
            if (!galleryViewerOpen || galleryList.Count == 0) return;
            PlaySound("move");
            int i = galleryIndex + step;
            if (i < 0) i = galleryList.Count - 1;
            if (i >= galleryList.Count) i = 0;
            ShowGalleryItem(i);
        }

        private void ShowGalleryItem(int index)
        {
            galleryIndex = Math.Max(0, Math.Min(galleryList.Count - 1, index));
            int gen = ++galleryViewerGen;
            var f = Current;
            var shownBefore = GalleryViewerImage;   // (0.99.50: a photo stays up until the next one is ready, never its blurry thumbnail)
            StopGalleryVideo();
            ResetGalleryTransform();
            galleryZoom = 1;
            GalleryViewerImage = null;
            GalleryViewerVideo = null;
            GalleryViewerTime = "";
            GalleryViewerProgress = 0;
            GalleryDetails.Clear();
            if (f == null) { NotifyGalleryViewer(); return; }
            if (f.IsVideo)
            {
                GalleryViewerVideo = new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = f.Path }.Uri;   // "#" in a name stays part of it
                NotifyGalleryViewer();
                ui.BeginInvoke(new Action(() => { if (gen == galleryViewerGen) PlayGalleryVideo(); }), DispatcherPriority.Loaded);
            }
            else
            {
                // (0.99.50) the full picture straight away when it's been read ahead; otherwise the last picture stays up until it's
                // read (the thumbnail only when nothing's showing yet: opening the viewer), so stepping is never blurry
                string path = f.Path;
                var ready = GalleryFullCached(path);
                if (ready != null) GalleryViewerImage = ready;
                else if (shownBefore != null) GalleryViewerImage = shownBefore;
                else
                {
                    ImageSource quick;
                    lock (galleryThumbs) galleryThumbs.TryGetValue(path, out quick);
                    GalleryViewerImage = quick;
                }
                NotifyGalleryViewer();
                if (ready == null)
                    GalleryFullLoad(path, img => { if (gen != galleryViewerGen || img == null) return; GalleryViewerImage = img; Notify("GalleryViewerImage"); });
                GalleryReadAhead();
            }
            FillGalleryDetails(f, gen);
            if (gallerySlideshow) RestartSlideTimer();
            // the video-only / photo-only buttons may have just gone: keep the highlight on something that's there
            ui.BeginInvoke(new Action(() =>
            {
                if (!galleryViewerOpen) return;
                var bar = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryControls");
                var fe = Keyboard.FocusedElement as FrameworkElement;
                bool lost = fe == null || !fe.IsVisible;
                if (lost || (bar != null && bar.IsAncestorOf(fe) && !fe.IsVisible))
                {
                    if (!(bar != null && fe != null && bar.IsAncestorOf(fe)) && !lost) return;
                    if (!UiTools.FocusByNameIfVisible("KX_GalleryFirstControl")) UiTools.FocusByName("KX_GallerySurface");
                }
            }), DispatcherPriority.ContextIdle);
        }

        // ---------------- (0.99.50) full pictures read ahead ----------------

        private readonly Dictionary<string, ImageSource> galleryFull = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action<ImageSource>>> galleryFullWaiting = new Dictionary<string, List<Action<ImageSource>>>(StringComparer.OrdinalIgnoreCase);

        private ImageSource GalleryFullCached(string path)
        {
            lock (galleryFull) { ImageSource s; return galleryFull.TryGetValue(path, out s) ? s : null; }
        }

        /// <summary>Reads a picture at full size off the UI thread (once, however many ask); done() runs on the UI thread.</summary>
        private void GalleryFullLoad(string path, Action<ImageSource> done)
        {
            lock (galleryFull)
            {
                ImageSource s;
                if (galleryFull.TryGetValue(path, out s)) { if (done != null) OnUi(() => done(s)); return; }
                List<Action<ImageSource>> waiting;
                if (galleryFullWaiting.TryGetValue(path, out waiting)) { if (done != null) waiting.Add(done); return; }
                galleryFullWaiting[path] = new List<Action<ImageSource>>();
                if (done != null) galleryFullWaiting[path].Add(done);
            }
            KippixShared.SafeWork.Queue(_ =>
            {
                ImageSource img = null;
                try { img = Images.LoadUncached(path, 0); } catch (Exception) { }   // as it is (no scaling up)
                List<Action<ImageSource>> todo;
                lock (galleryFull)
                {
                    if (img != null) galleryFull[path] = img;
                    galleryFullWaiting.TryGetValue(path, out todo);
                    galleryFullWaiting.Remove(path);
                }
                if (todo != null && todo.Count > 0) OnUi(() => { foreach (var a in todo) { try { a(img); } catch (Exception) { } } });
            });
        }

        /// <summary>The pictures either side are read now, so stepping shows them sharp at once; only those near the current one are kept.</summary>
        private void GalleryReadAhead()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int d in new[] { 0, 1, -1, 2 })
            {
                int i = galleryIndex + d;
                if (i < 0 || i >= galleryList.Count) continue;
                var g = galleryList[i];
                if (g == null || g.IsVideo || string.IsNullOrEmpty(g.Path)) continue;
                keep.Add(g.Path);
                if (d != 0) GalleryFullLoad(g.Path, null);
            }
            lock (galleryFull)
                foreach (var k in galleryFull.Keys.ToList()) if (!keep.Contains(k)) galleryFull.Remove(k);
        }

        private void FillGalleryDetails(GalleryFile f, int gen)
        {
            string path = f.Path;
            bool video = f.IsVideo;
            ShellMedia.Run(() =>
            {
                int w, h;
                TimeSpan len;
                ShellMedia.Details(path, video, out w, out h, out len);
                OnUi(() =>
                {
                    if (gen != galleryViewerGen) return;
                    GalleryDetails.Clear();
                    GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetGame", "Game"), Value = GalleryViewerTitle });
                    GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetTaken", "Taken"), Value = f.Date.ToString("d MMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture) });
                    GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetType", "Type"), Value = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " " + (video ? Loc("LOC_KX_Video", "video") : Loc("LOC_KX_Photo", "photo")) });
                    if (w > 0 && h > 0) GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetSize", "Resolution"), Value = w + " × " + h });
                    if (len > TimeSpan.Zero) GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetLength", "Length"), Value = Clock(len.TotalSeconds) });
                    GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetFileSize", "File size"), Value = KippixOverlay.Capture.DriveSize(f.Size) });
                    GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetFolder", "Folder"), Value = Path.GetDirectoryName(path) });
                    GalleryDetails.Add(new WidgetLine { Label = Loc("LOC_KX_DetFile", "File"), Value = Path.GetFileName(path) });
                });
            });
        }

        // ---------------- video ----------------

        private MediaElement GalleryVideoElement()
        {
            if (galleryVideo != null) return galleryVideo;
            galleryVideo = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryVideo") as MediaElement;
            if (galleryVideo != null)
            {
                galleryVideo.MediaEnded += (s, e) =>
                {
                    galleryPlaying = false;
                    try { galleryVideo.Stop(); } catch (Exception) { }
                    NotifyGalleryViewer();
                    UpdateMusicState();
                    ShowGalleryControls(false);
                    if (gallerySlideshow) GalleryStep(1);
                };
                galleryVideo.MediaFailed += (s, e) =>
                {
                    galleryPlaying = false;
                    ShowGalleryStatus(Loc("LOC_KX_VideoCantPlay", "This video can't be played here (Windows may need a codec for it)."));
                    NotifyGalleryViewer();
                    UpdateMusicState();
                    if (gallerySlideshow && gallerySlideTimer != null) { gallerySlideTimer.Interval = TimeSpan.FromSeconds(3); gallerySlideTimer.Start(); }
                };
            }
            return galleryVideo;
        }

        private void PlayGalleryVideo()
        {
            var v = GalleryVideoElement();
            if (v == null || GalleryViewerVideo == null) return;
            try { v.Play(); galleryPlaying = true; } catch (Exception) { galleryPlaying = false; }
            if (galleryProgressTimer == null)
            {
                galleryProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                galleryProgressTimer.Tick += (s, e) => UpdateGalleryProgress();
            }
            galleryProgressTimer.Start();
            NotifyGalleryViewer();
            UpdateMusicState();
            HideGalleryControlsSoon();
        }

        private void ToggleGalleryVideo()
        {
            var v = GalleryVideoElement();
            if (v == null || GalleryViewerVideo == null) return;
            if (galleryPlaying)
            {
                try { v.Pause(); } catch (Exception) { }
                galleryPlaying = false;
                NotifyGalleryViewer();
                UpdateMusicState();
                ShowGalleryControls(false);
            }
            else PlayGalleryVideo();
        }

        private void StopGalleryVideo()
        {
            if (galleryVideo != null) try { galleryVideo.Stop(); } catch (Exception) { }
            if (galleryPlaying) { galleryPlaying = false; UpdateMusicState(); }
        }

        private void GallerySeek(int seconds)
        {
            var v = GalleryVideoElement();
            if (v == null || GalleryViewerVideo == null) return;
            try
            {
                double len = v.NaturalDuration.HasTimeSpan ? v.NaturalDuration.TimeSpan.TotalSeconds : 0;
                double t = v.Position.TotalSeconds + seconds;
                if (len > 0) t = Math.Min(t, len - 0.5);
                v.Position = TimeSpan.FromSeconds(Math.Max(0, t));
                PlaySound("move");
            }
            catch (Exception) { }
            UpdateGalleryProgress();
        }

        private void UpdateGalleryProgress()
        {
            var v = galleryVideo;
            if (!galleryViewerOpen || v == null || GalleryViewerVideo == null) { if (galleryProgressTimer != null) galleryProgressTimer.Stop(); return; }
            double len = 0, pos = 0;
            try { len = v.NaturalDuration.HasTimeSpan ? v.NaturalDuration.TimeSpan.TotalSeconds : 0; pos = v.Position.TotalSeconds; } catch (Exception) { }
            GalleryViewerTime = len > 0 ? Clock(pos) + " / " + Clock(len) : "";
            GalleryViewerProgress = len > 0 ? Math.Max(0, Math.Min(1, pos / len)) * GalleryBarWidth : 0;
            Notify("GalleryViewerTime");
            Notify("GalleryViewerProgress");
        }

        private void SetGalleryVolume(double v)
        {
            galleryVolume = Math.Max(0, Math.Min(1, Math.Round(v, 1)));
            galleryMuted = galleryVolume <= 0;
            PlaySound("move");
            NotifyGalleryViewer();
            UpdateMusicState();
        }

        // ---------------- zoom ----------------

        private static readonly double[] ZoomSteps = { 1, 1.5, 2, 3, 4 };

        private double NextZoom(int dir)
        {
            int i = 0;
            for (int k = 0; k < ZoomSteps.Length; k++) if (Math.Abs(ZoomSteps[k] - galleryZoom) < 0.01) i = k;
            return ZoomSteps[Math.Max(0, Math.Min(ZoomSteps.Length - 1, i + dir))];
        }

        private void SetGalleryZoom(double z)
        {
            if (Current == null || Current.IsVideo) return;
            if (Math.Abs(z - galleryZoom) < 0.01) return;
            PlaySound("move");
            double ratio = z / galleryZoom;
            galleryZoom = z;
            galleryPanX *= ratio;
            galleryPanY *= ratio;
            if (z <= 1.01) { galleryPanX = galleryPanY = 0; }
            ApplyGalleryTransform(true);
            Notify("GalleryZoomText");
        }

        private void PanGallery(double dx, double dy)
        {
            galleryPanX += dx;
            galleryPanY += dy;
            ApplyGalleryTransform(true);
        }

        private void ResetGalleryTransform()
        {
            galleryPanX = galleryPanY = 0;
            var img = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryImage");
            if (img != null) { img.RenderTransform = Transform.Identity; }
        }

        private void ApplyGalleryTransform(bool animate)
        {
            var img = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryImage");
            if (img == null) return;
            // keep the picture covering the screen: no panning past its edges
            double maxX = Math.Max(0, (galleryZoom - 1) * img.ActualWidth / 2), maxY = Math.Max(0, (galleryZoom - 1) * img.ActualHeight / 2);
            galleryPanX = Math.Max(-maxX, Math.Min(maxX, galleryPanX));
            galleryPanY = Math.Max(-maxY, Math.Min(maxY, galleryPanY));
            var group = img.RenderTransform as TransformGroup;
            ScaleTransform sc;
            TranslateTransform tr;
            if (group == null || group.Children.Count != 2 || group.IsFrozen)
            {
                sc = new ScaleTransform(1, 1);
                tr = new TranslateTransform();
                group = new TransformGroup();
                group.Children.Add(sc);
                group.Children.Add(tr);
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                img.RenderTransform = group;
            }
            sc = (ScaleTransform)group.Children[0];
            tr = (TranslateTransform)group.Children[1];
            var d = new Duration(TimeSpan.FromMilliseconds(animate ? 180 : 0));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(galleryZoom, d) { EasingFunction = ease });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(galleryZoom, d) { EasingFunction = ease });
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(galleryPanX, d) { EasingFunction = ease });
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(galleryPanY, d) { EasingFunction = ease });
        }

        // ---------------- favourite, details, art, delete ----------------

        private void ToggleGalleryFavourite()
        {
            var f = Current;
            if (f == null) return;
            bool on = !FavSet().Contains(f.Path);
            SetFavourite(f.Path, on, true);
            PlaySound("select");
            var tile = GalleryItems.FirstOrDefault(i => i.File == f);
            if (tile != null) tile.IsFavourite = on;
            ShowGalleryStatus(on ? Loc("LOC_KX_FavOn", "Added to favourites") : Loc("LOC_KX_FavOff", "Taken out of favourites"));
            Notify("GalleryViewerIsFav");
        }

        private void ToggleGalleryDetails()
        {
            galleryDetailsOpen = !galleryDetailsOpen;
            PlaySound("move");
            Notify("GalleryDetailsOpen");
        }

        /// <summary>The record of this photo being the game's background / cover right now, or null.</summary>
        private ArtSwap ArtSwapFor(GalleryFile f, bool background)
        {
            if (f == null || f.IsVideo || f.GameId == Guid.Empty || settings.ArtSwaps == null) return null;
            string gid = f.GameId.ToString();
            var rec = settings.ArtSwaps.FirstOrDefault(r => r.GameId == gid && r.Background == background);
            if (rec == null || !string.Equals(rec.Photo, f.Path, StringComparison.OrdinalIgnoreCase)) return null;
            var game = api.Database.Games.Get(f.GameId);
            if (game == null) return null;
            return (background ? game.BackgroundImage : game.CoverImage) == rec.NewId ? rec : null;
        }

        /// <summary>
        /// Uses the photo as the game's background (or cover) in Playnite. Pressed again on the same photo, it puts
        /// back what the game had before. Swapping between several photos still remembers the art from before the first.
        /// </summary>
        private void SetGalleryArt(bool background)
        {
            var f = Current;
            if (f == null || f.IsVideo) return;
            if (f.GameId == Guid.Empty) { ShowGalleryStatus(Loc("LOC_KX_ArtNoGame", "This one isn't linked to a game, so it can't be its art.")); return; }
            try
            {
                var game = api.Database.Games.Get(f.GameId);
                if (game == null) { ShowGalleryStatus(Loc("LOC_KX_ArtNoGame", "This one isn't linked to a game, so it can't be its art.")); return; }
                if (settings.ArtSwaps == null) settings.ArtSwaps = new List<ArtSwap>();
                string gid = game.Id.ToString();
                string current = background ? game.BackgroundImage : game.CoverImage;
                var rec = settings.ArtSwaps.FirstOrDefault(r => r.GameId == gid && r.Background == background);
                if (rec != null && rec.NewId != current) { settings.ArtSwaps.Remove(rec); rec = null; }   // changed somewhere else since: that's the art now

                if (rec != null && string.Equals(rec.Photo, f.Path, StringComparison.OrdinalIgnoreCase))
                {
                    // undo: back to what it had before
                    if (background) game.BackgroundImage = rec.OldId; else game.CoverImage = rec.OldId;
                    api.Database.Games.Update(game);
                    try { if (!string.IsNullOrEmpty(rec.NewId)) api.Database.RemoveFile(rec.NewId); } catch (Exception) { }
                    settings.ArtSwaps.Remove(rec);
                    SaveSettings();
                    PlaySound("back");
                    ShowGalleryStatus(string.Format(background ? Loc("LOC_KX_ArtBackgroundUndone", "{0}'s background is back to how it was") : Loc("LOC_KX_ArtCoverUndone", "{0}'s cover is back to how it was"), game.Name));
                }
                else
                {
                    string id = api.Database.AddFile(f.Path, game.Id);
                    if (string.IsNullOrEmpty(id)) throw new IOException("not added");
                    if (background) game.BackgroundImage = id; else game.CoverImage = id;
                    api.Database.Games.Update(game);
                    string original = current;
                    if (rec != null)
                    {
                        original = rec.OldId;   // keep the art from before the first photo
                        try { if (!string.IsNullOrEmpty(rec.NewId)) api.Database.RemoveFile(rec.NewId); } catch (Exception) { }
                        settings.ArtSwaps.Remove(rec);
                    }
                    settings.ArtSwaps.Add(new ArtSwap { GameId = gid, Background = background, Photo = f.Path, NewId = id, OldId = original });
                    SaveSettings();
                    PlaySound("select");
                    ShowGalleryStatus(string.Format(background ? Loc("LOC_KX_ArtBackgroundDone", "Now {0}'s background") : Loc("LOC_KX_ArtCoverDone", "Now {0}'s cover"), game.Name)
                                      + "  \u00B7  " + Loc("LOC_KX_ArtPressAgain", "press again to undo"));
                }
                Notify("GalleryViewerIsBackground");
                Notify("GalleryViewerIsCover");
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: set art: " + ex.Message);
                ShowGalleryStatus(Loc("LOC_KX_ArtFailed", "Couldn't change the game's art."));
            }
        }

        private void DeleteGalleryCurrent()
        {
            var f = Current;
            if (f == null) return;
            int at = galleryIndex;
            bool video = f.IsVideo;
            StopGalleryVideo();
            if (video && galleryVideo != null) try { galleryVideo.Close(); } catch (Exception) { }
            GalleryViewerVideo = null;
            Notify("GalleryViewerVideo");
            DeleteGalleryFiles(new List<GalleryFile> { f }, () =>
            {
                if (!galleryViewerOpen) return;
                if (galleryList.Count == 0) { CloseGalleryViewer(true); return; }
                ShowGalleryItem(Math.Min(at, galleryList.Count - 1));
            }, video);
        }

        // ---------------- slideshow ----------------

        private void StartSlideshowFromList()
        {
            if (galleryList.Count == 0) return;
            OpenGalleryViewer(0);
            SetSlideshow(true);
        }

        private void SetSlideshow(bool on)
        {
            if (gallerySlideshow == on) return;
            gallerySlideshow = on;
            if (!on) { if (gallerySlideTimer != null) gallerySlideTimer.Stop(); ShowGalleryControls(false); }
            else { PlaySound("select"); RestartSlideTimer(); HideGalleryControlsSoon(); }
            Notify("GallerySlideshowOn");
        }

        private void RestartSlideTimer()
        {
            if (gallerySlideTimer == null)
            {
                gallerySlideTimer = new DispatcherTimer();
                gallerySlideTimer.Tick += (s, e) =>
                {
                    gallerySlideTimer.Stop();
                    if (!galleryViewerOpen || !gallerySlideshow) return;
                    if (Current != null && Current.IsVideo && galleryPlaying) return;   // a video moves on when it ends
                    GalleryStep(1);
                };
            }
            gallerySlideTimer.Stop();
            gallerySlideTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, settings.GallerySlideSeconds));
            if (Current == null || !Current.IsVideo) gallerySlideTimer.Start();
        }

        // ---------------- the controls fading away ----------------

        /// <summary>
        /// (0.99.48) The controls and the picture's details (game, date, counter) go after 10 s with nothing pressed (4 s while a
        /// video plays or the slideshow runs), or straight away with Up. They stay away while you step through pictures; Up or
        /// Down (or the mouse) bring them back.
        /// </summary>
        private void HideGalleryControlsSoon()
        {
            if (galleryHideTimer == null)
            {
                galleryHideTimer = new DispatcherTimer();
                galleryHideTimer.Tick += (s, e) =>
                {
                    galleryHideTimer.Stop();
                    if (!galleryViewerOpen || galleryDetailsOpen) return;
                    HideGalleryControlsNow();
                };
            }
            galleryHideTimer.Stop();
            galleryHideTimer.Interval = TimeSpan.FromSeconds(galleryPlaying || gallerySlideshow ? 4 : 10);
            galleryHideTimer.Start();
        }

        private DateTime galleryHiddenByUpAt = DateTime.MinValue;
        private bool galleryRebuildOnClose;   // (0.99.56) a frame was saved: the grid behind is built again (it shows the new photo)
        private DateTime galleryPadAt = DateTime.MinValue, galleryMouseRunAt = DateTime.MinValue;   // (0.99.54)
        private double galleryMouseRun;

        private void HideGalleryControlsNow()
        {
            if (galleryMoreOpen || galleryEditOpen) return;   // (0.99.59: not while the More menu or Edit is up)
            if (galleryHideTimer != null) galleryHideTimer.Stop();
            if (!galleryControlsShown) return;
            galleryControlsShown = false;
            Notify("GalleryControlsShown");
            // the controls are going: keep the highlight on the picture
            var bar = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryControls");
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (bar != null && focused != null && bar.IsAncestorOf(focused)) UiTools.FocusByName("KX_GallerySurface");
        }

        /// <summary>Shows the controls again (true = it was only to show them, so the key does nothing else); the 10 s start again.</summary>
        private bool ShowGalleryControls(bool fromKey)
        {
            bool wasHidden = !galleryControlsShown;
            if (wasHidden) { galleryControlsShown = true; Notify("GalleryControlsShown"); Dbg("gallery: controls shown (" + (fromKey ? "a button" : "the mouse, a video stopping or the slideshow") + ")"); }
            HideGalleryControlsSoon();
            return wasHidden && fromKey;
        }

        // ---------------- keys ----------------

        /// <summary>Arrow keys in the gallery (viewer only: the grid uses the normal moves). True when handled.</summary>
        private bool GalleryArrowKey(KeyEventArgs e)
        {
            if (!galleryViewerOpen) return false;
            galleryPadAt = DateTime.Now;
            // (0.99.59) the More menu: Up / Down go through it (WPF moves the highlight), Left / Right do nothing there
            if (galleryMoreOpen && FocusInMoreMenu()) { if (galleryHideTimer != null) galleryHideTimer.Stop(); return e.Key == Key.Left || e.Key == Key.Right; }
            bool hidden = !galleryControlsShown;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            bool onSurface = focused != null && focused.Name == "KX_GallerySurface";
            // (0.99.48) Up / Down bring hidden controls back (that's all they do then); Up on the picture hides them
            if (hidden && (e.Key == Key.Up || e.Key == Key.Down))
            {
                // (0.99.51) one Up can arrive twice (the controller's own press and Steam's arrow key for it): the copy that comes
                // straight after the Up that hid them doesn't bring them back
                if ((DateTime.Now - galleryHiddenByUpAt).TotalMilliseconds < 450) return true;
                ShowGalleryControls(true);
                return true;
            }
            if (!hidden) HideGalleryControlsSoon();   // (something pressed: the 10 s start again)
            if (!onSurface)
            {
                // in the controls: Up goes back to the picture and (0.99.51) hides them, as Up on the picture does; Left / Right move along
                if (e.Key == Key.Up) { UiTools.FocusByName("KX_GallerySurface"); HideGalleryControlsNow(); galleryHiddenByUpAt = DateTime.Now; return true; }
                ShowGalleryControls(true);
                if (e.Key == Key.Down) return true;
                return false;
            }
            if (galleryZoom > 1.01)
            {
                double step = 160;
                if (e.Key == Key.Left) PanGallery(step, 0);
                else if (e.Key == Key.Right) PanGallery(-step, 0);
                else if (e.Key == Key.Up) PanGallery(0, step);
                else if (e.Key == Key.Down) PanGallery(0, -step);
                return true;
            }
            if (e.Key == Key.Left) { GalleryStep(-1); return true; }   // (hidden controls stay hidden)
            if (e.Key == Key.Right) { GalleryStep(1); return true; }
            if (e.Key == Key.Up) { HideGalleryControlsNow(); galleryHiddenByUpAt = DateTime.Now; return true; }
            if (e.Key == Key.Down)
            {
                if (hidden) return true;
                var first = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryFirstControl");
                var play = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryPlayButton");
                var target = Current != null && Current.IsVideo && play != null && play.IsVisible ? play : first;
                if (target != null) Keyboard.Focus(target);
                else UiTools.FocusInside("KX_GalleryControls");
                return true;
            }
            return e.Key == Key.Up;
        }

        /// <summary>LB / RB, LT / RT, X and Y in the gallery. True when handled.</summary>
        private bool GalleryPanelKey(KeyEventArgs e, int step, int pageJump, bool xPressed, bool back)
        {
            if (back) return false;
            bool yPressed = false, pressed = true;
            string btn = null;
            if (e.GetType().Name == "GameControllerInputEventArgs")
            {
                PropertyInfo button = e.GetType().GetProperty("Button");
                PropertyInfo state = e.GetType().GetProperty("ButtonState");
                object b = button != null ? button.GetValue(e, null) : null;
                object st = state != null ? state.GetValue(e, null) : null;
                pressed = st == null || st.ToString() == "Pressed";
                btn = b != null ? b.ToString() : null;
                yPressed = pressed && btn == "Y";
            }
            if (galleryViewerOpen)
            {
                galleryPadAt = DateTime.Now;
                // (0.99.48) the controls hidden: stepping, zooming / skipping and favourites work and they stay hidden;
                // Y (the details) brings them back; anything else only brings them back
                bool hiddenNow = !galleryControlsShown;
                // (0.99.52) the D-pad and sticks also come as arrow keys (GalleryArrowKey decides what they do): their own pad
                // event (or any let-go) must not bring the controls back, or Up hid them and its pad event showed them again
                bool direction = btn != null && (btn.StartsWith("DPad", StringComparison.Ordinal) || (btn.StartsWith("LeftStick", StringComparison.Ordinal) && btn.Length > 9) || (btn.StartsWith("RightStick", StringComparison.Ordinal) && btn.Length > 10));   // (not the sticks' clicks)
                if (direction) return false;
                if (hiddenNow && !pressed && !yPressed) return false;
                // (0.99.54) while they're hidden only the buttons that mean "show me" bring them back: A, Y, Start, View (or Enter /
                // Space / Tab on a keyboard). Anything else that arrives (RB sent twice over by Steam, a stick click, Guide...) is left alone.
                bool shows = btn != null ? (btn == "A" || btn == "Y" || btn == "Start" || btn == "Back")
                                         : (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Tab);
                if (hiddenNow && step == 0 && pageJump == 0 && !xPressed && !shows) { Dbg("gallery: hidden controls left hidden (" + (btn ?? e.Key.ToString()) + ")"); return false; }
                if (hiddenNow && (yPressed || (step == 0 && pageJump == 0 && !xPressed))) { if (pressed) ShowGalleryControls(true); if (!yPressed) return pressed; }
                else if (!hiddenNow) HideGalleryControlsSoon();
                if (step == 0 && pageJump == 0 && !xPressed && !yPressed) return false;
                // a shoulder button steps; Page Up / Down (the triggers, or the keyboard) zoom a photo or skip in a video
                bool shoulder = btn != null ? (btn == "LeftShoulder" || btn == "RightShoulder") : (e.Key != Key.PageUp && e.Key != Key.PageDown);
                if (step != 0 && shoulder) { GalleryStep(step); return true; }
                if (pageJump != 0)
                {
                    if (Current != null && Current.IsVideo) GallerySeek(pageJump * 10);
                    else SetGalleryZoom(NextZoom(pageJump));
                    return true;
                }
                if (xPressed) { ToggleGalleryFavourite(); return true; }
                if (yPressed) { ToggleGalleryDetails(); return true; }
                return false;
            }
            if (galleryPage == "storage" || galleryPage == "games") return false;
            // the grid: X favourites the highlighted tile, Y picks several
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var item = fe != null ? fe.DataContext as GalleryItem : null;
            if (xPressed && item != null && item.IsTile)
            {
                bool on = !item.IsFavourite;
                SetFavourite(item.Path, on, true);
                item.IsFavourite = on;
                PlaySound("select");
                ShowGalleryStatus(on ? Loc("LOC_KX_FavOn", "Added to favourites") : Loc("LOC_KX_FavOff", "Taken out of favourites"));
                return true;
            }
            if (yPressed) { SetGallerySelect(!gallerySelect); return true; }
            return false;
        }
    }
}

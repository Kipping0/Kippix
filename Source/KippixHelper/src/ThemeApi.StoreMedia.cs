using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// The Store's pop-ups: the filter picker (sort, genre, price...), the full screen viewer for screenshots and
    /// trailers (Left / Right steps through them, A plays / pauses, LB / RB skip 10 s), and the trailer that can play
    /// at the top of a game's page.
    /// </summary>
    public partial class ThemeApi
    {
        // ---------------- picker ----------------

        private bool storePickerOpen;
        private IInputElement storePickerReturn;

        public bool IsStorePickerOpen { get { return storePickerOpen; } }
        public string StorePickerTitle { get; private set; }

        private List<Tuple<string, string>> pickerAll = new List<Tuple<string, string>>();
        private string pickerCurrent = "", pickerFilter = "";
        private Action<string> pickerDone;
        private bool pickerSearchable;
        private const int PickerShown = 60;   // long lists (Steam has 400+ tags) show the most popular, and search finds the rest

        private void OpenStorePicker(string title, IEnumerable<Tuple<string, string>> options, string current, Action<string> done)
        {
            OpenStorePicker(title, options, current, done, false);
        }

        private void OpenStorePicker(string title, IEnumerable<Tuple<string, string>> options, string current, Action<string> done, bool searchable)
        {
            pickerAll = options.ToList();
            pickerCurrent = current ?? "";
            pickerDone = done;
            pickerSearchable = searchable;
            pickerFilter = "";
            StorePickerSlider = false;    // only the Price picker has it (it turns it on after opening)
            NotifySlider();
            storePickerReturn = Keyboard.FocusedElement;
            StorePickerTitle = title;
            storePickerOpen = true;
            var focus = BuildPickerItems();
            NotifyStoreOverlays();
            ui.BeginInvoke(new Action(() => { if (focus != null) UiTools.FocusItem("KX_StorePickerList", focus); }), DispatcherPriority.ContextIdle);
        }

        /// <summary>Fills the picker (the search row first, then the choices); returns the row to highlight.</summary>
        /// <summary>The search bar across the top of the picker (genres and tags).</summary>
        public bool StorePickerSearchable { get { return pickerSearchable; } }
        public string StorePickerSearchText { get; private set; }
        public ICommand StorePickerSearchCommand { get; private set; }

        private PickOption BuildPickerItems()
        {
            if (StorePickerSearchCommand == null) StorePickerSearchCommand = new RelayCommand(_ => AskPickerSearch());
            StorePickerItems.Clear();
            PickOption selected = null, firstChoice = null;
            var list = pickerFilter.Length == 0 ? pickerAll
                     : pickerAll.Where(o => o.Item1.Length == 0 || o.Item2.IndexOf(pickerFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var shown = pickerSearchable && pickerFilter.Length == 0 ? list.Take(PickerShown).ToList() : list;
            var cur = list.FirstOrDefault(o => o.Item1 == pickerCurrent);
            if (cur != null && !shown.Contains(cur)) shown.Insert(Math.Min(1, shown.Count), cur);   // keep the current choice in view
            foreach (var o in shown)
            {
                string key = o.Item1;
                var done = pickerDone;
                var p = new PickOption { Text = o.Item2, IsSelected = key == pickerCurrent };
                p.Command = new RelayCommand(_ => { CloseStorePicker(false); PlaySound("select"); if (done != null) done(key); });
                if (p.IsSelected) selected = p;
                if (firstChoice == null) firstChoice = p;
                StorePickerItems.Add(p);
            }
            StorePickerSearchText = pickerFilter.Length > 0
                ? string.Format(Loc("LOC_KX_PickerFound", "\"{0}\": {1} found"), pickerFilter, Math.Max(0, shown.Count - 1))
                : string.Format(Loc("LOC_KX_PickerMore", "Search all {0}"), Math.Max(0, pickerAll.Count - 1));
            Notify("StorePickerSearchable");
            Notify("StorePickerSearchText");
            Notify("StorePickerSearchCommand");
            // after a search, the first match (skipping "Any"); otherwise the current choice
            return pickerFilter.Length > 0 ? StorePickerItems.Skip(1).FirstOrDefault() ?? firstChoice : selected ?? firstChoice;
        }

        private void AskPickerSearch()
        {
            OpenKeyboard(Loc("LOC_KX_PickerSearchPrompt", "Search genres and tags"), pickerFilter, text =>
            {
                if (!storePickerOpen) return;
                pickerFilter = (text ?? "").Trim();
                var focus = BuildPickerItems();
                ui.BeginInvoke(new Action(() => { if (focus != null) UiTools.FocusItem("KX_StorePickerList", focus); }), DispatcherPriority.ContextIdle);
            }, text => { if (storePickerOpen) { pickerFilter = (text ?? "").Trim(); BuildPickerItems(); } },
            () =>
            {
                // B: the matches so far stay, and the first one gets the highlight
                if (!storePickerOpen || pickerFilter.Length == 0) return;
                var first = StorePickerItems.Skip(1).FirstOrDefault();
                if (first != null) ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_StorePickerList", first)), DispatcherPriority.ContextIdle);
            }, 40);
        }

        private void CloseStorePicker() { CloseStorePicker(true); }

        private void CloseStorePicker(bool restoreFocus)
        {
            if (!storePickerOpen) return;
            storePickerOpen = false;
            NotifyStoreOverlays();
            var back = storePickerReturn as UIElement;
            storePickerReturn = null;
            if (restoreFocus) ui.BeginInvoke(new Action(() => { if (back != null && back.IsVisible) Keyboard.Focus(back); else FocusPanelSoon(); }), DispatcherPriority.ContextIdle);
        }

        // ---------------- full screen viewer ----------------

        private bool storeViewerOpen;
        private int storeViewerIndex;
        private int storeViewerGen;
        private bool storeViewerPlaying, storeHeroPlaying;
        private StoreMedia storeViewerReturn;
        private MediaElement storeViewerVideo, storeHeroVideo;

        public bool IsStoreViewerOpen { get { return storeViewerOpen; } }
        public ImageSource StoreViewerImage { get; private set; }
        public Uri StoreViewerVideo { get; private set; }
        public bool StoreViewerShowVideo { get { return StoreViewerVideo != null; } }
        public bool StoreViewerShowImage { get { return StoreViewerVideo == null; } }
        public string StoreViewerCounter { get; private set; }
        public string StoreViewerStatus { get; private set; }
        public bool HasStoreViewerStatus { get { return !string.IsNullOrEmpty(StoreViewerStatus); } }
        public string StoreViewerActionText { get; private set; }
        public Uri StoreHeroVideo { get; private set; }
        public bool ShowStoreHeroVideo { get { return StoreHeroVideo != null && storeHeroPlaying; } }
        public bool StoreVideoMuted { get { return !settings.StoreTrailerSound; } }

        /// <summary>A trailer or the browser makes the home screen music fade out.</summary>
        private bool StoreSoundActive
        {
            get { return ((storeViewerPlaying || storeHeroPlaying) && settings.StoreTrailerSound) || (tilePreviewPlaying && settings.StorePreviewSound) || openPanel == "browser"; }
        }

        private void NotifyStoreOverlays()
        {
            foreach (string n in new[] { "IsStorePickerOpen", "StorePickerTitle", "IsStoreViewerOpen", "StoreViewerImage", "StoreViewerVideo", "StoreViewerShowVideo",
                                         "StoreViewerShowImage", "StoreViewerCounter", "StoreViewerStatus", "HasStoreViewerStatus", "StoreViewerActionText",
                                         "StoreHeroVideo", "ShowStoreHeroVideo", "StoreVideoMuted", "StoreViewerIsTrailer" })
                Notify(n);
        }

        private void CloseStoreOverlays()
        {
            storePickerOpen = false;
            storeViewerOpen = false;
            if (storeReaderOpen) { storeReaderOpen = false; readerGen++; NotifyReader(); }
            StopStoreVideos();
            NotifyStoreOverlays();
        }

        private void OpenStoreMedia(StoreMedia m)
        {
            if (m.Kind == "web") { PlaySound("select"); OpenBrowser(m.Key); return; }
            int index = StoreMedia.IndexOf(m);
            if (index < 0) return;
            PauseStoreHero();
            storeViewerReturn = m;
            storeViewerOpen = true;
            StoreViewerImage = null;   // not the last game's picture while this one loads
            StoreViewerVideo = null;
            ShowViewerItem(index);
            StartViewerProgress();
            ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_StoreViewerButton")), DispatcherPriority.ContextIdle);
        }

        private void ShowViewerItem(int index)
        {
            if (StoreMedia.Count == 0) return;
            storeViewerIndex = (index % StoreMedia.Count + StoreMedia.Count) % StoreMedia.Count;
            int gen = ++storeViewerGen;
            StopViewerVideo();
            var m = StoreMedia[storeViewerIndex];
            bool wasVideo = StoreViewerVideo != null;
            StoreViewerVideo = null;
            if (m.Kind == "image")
            {
                // the sharp picture if it's loaded already; otherwise keep the last one up until it is (no blurry flash)
                var ready = ViewerCached(m.Key);
                if (ready != null) StoreViewerImage = ready;
                else if (wasVideo) StoreViewerImage = null;
            }
            else StoreViewerImage = m.Thumb;
            StoreViewerCounter = (storeViewerIndex + 1) + " / " + StoreMedia.Count;
            StoreViewerStatus = "";
            StoreViewerActionText = m.IsVideo ? Loc("LOC_KX_PlayPause", "Play / pause") : Loc("LOC_KX_Next", "Next");
            if (m.Kind == "image")
            {
                string url = m.Key;
                if (ViewerCached(url) == null)
                    LoadViewerImage(url, full => { if (gen == storeViewerGen && full != null) { StoreViewerImage = full; Notify("StoreViewerImage"); } });
                PreloadViewerImages(storeViewerIndex + 1, 1);
                PreloadViewerImages(storeViewerIndex - 1, 1);
            }
            else if (m.Kind == "web")
            {
                StoreViewerStatus = Loc("LOC_KX_StoreWatchInBrowser", "Watch this trailer in the browser");
                StoreViewerActionText = Loc("LOC_KX_Watch", "Watch");
            }
            else if (m.Kind == "steam")
            {
                StoreViewerStatus = Loc("LOC_KX_StoreTrailerLoading", "Getting the trailer…");
                ResolveSteamTrailer(m.Key, files =>
                {
                    if (gen != storeViewerGen || !storeViewerOpen) return;
                    if (files == null) { StoreViewerStatus = Loc("LOC_KX_StoreTrailerNone", "This trailer can't be played here."); NotifyStoreOverlays(); return; }
                    StoreViewerStatus = "";
                    StoreViewerVideo = new Uri(files.Video);
                    OpenViewerAudio(files.Audio);
                    NotifyStoreOverlays();
                    ui.BeginInvoke(new Action(() => { if (gen == storeViewerGen) PlayViewerVideo(); }), DispatcherPriority.Loaded);
                });
            }
            NotifyStoreOverlays();
        }

        // full screen pictures kept ready: the one you're on and its neighbours
        private readonly Dictionary<string, ImageSource> viewerImages = new Dictionary<string, ImageSource>();
        private readonly List<string> viewerOrder = new List<string>();
        private readonly HashSet<string> viewerLoading = new HashSet<string>();

        private ImageSource ViewerCached(string url)
        {
            ImageSource img;
            return url != null && viewerImages.TryGetValue(url, out img) ? img : null;
        }

        private void LoadViewerImage(string url, Action<ImageSource> done)
        {
            if (string.IsNullOrEmpty(url) || viewerLoading.Contains(url)) return;
            viewerLoading.Add(url);
            int width = PxImg(1920);
            KippixShared.SafeWork.Queue(_ =>
            {
                var img = Images.LoadUncached(url, width);
                OnUi(() =>
                {
                    viewerLoading.Remove(url);
                    if (img != null)
                    {
                        viewerImages[url] = img;
                        viewerOrder.Remove(url);
                        viewerOrder.Add(url);
                        while (viewerOrder.Count > 6) { viewerImages.Remove(viewerOrder[0]); viewerOrder.RemoveAt(0); }
                    }
                    if (done != null) done(img);
                });
            });
        }

        /// <summary>Loads the full screen pictures from this position on in the background.</summary>
        private void PreloadViewerImages(int from, int count)
        {
            if (StoreMedia.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                int idx = ((from + i) % StoreMedia.Count + StoreMedia.Count) % StoreMedia.Count;
                var m = StoreMedia[idx];
                if (m.Kind == "image" && ViewerCached(m.Key) == null) LoadViewerImage(m.Key, null);
            }
        }

        /// <summary>Selecting a trailer's thumbnail starts its download, so it's (nearly) ready when you press A.</summary>
        private bool storeMediaHooked;

        private void HookStoreMediaList()
        {
            if (storeMediaHooked) return;
            var list = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreMediaList");
            if (list == null) return;
            storeMediaHooked = true;
            list.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                var fe = e.OriginalSource as FrameworkElement;
                var m = fe != null ? fe.DataContext as StoreMedia : null;
                if (m == null) return;
                if (m.Kind == "steam") ResolveSteamTrailer(m.Key, _ => { });
                else if (m.Kind == "image") LoadViewerImage(m.Key, null);
            }), true);
        }

        private void StoreViewerStep(int step)
        {
            if (!storeViewerOpen || StoreMedia.Count < 2) return;
            PlaySound("move");
            bool fromControls = InViewerControls();
            ShowViewerItem(storeViewerIndex + step);
            // the controls are only there for trailers: leaving one from them puts you back on the picture
            if (fromControls && !StoreViewerIsTrailer) ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_StoreViewerButton")), DispatcherPriority.ContextIdle);
        }

        /// <summary>A in the viewer: play / pause a trailer, open a YouTube trailer, or go to the next picture.</summary>
        private void StoreViewerAction()
        {
            if (!storeViewerOpen || StoreMedia.Count == 0) return;
            var m = StoreMedia[storeViewerIndex];
            if (m.Kind == "web") { OpenBrowser(m.Key); return; }
            if (m.Kind == "image") { StoreViewerStep(1); return; }
            var v = ViewerVideoElement();
            if (v == null || StoreViewerVideo == null) return;
            if (storeViewerPlaying)
            {
                try { v.Pause(); if (storeViewerAudio != null) storeViewerAudio.Pause(); } catch (Exception) { }
                storeViewerPlaying = false;
                Notify("StoreViewerPlaying");
                UpdateMusicState();
            }
            else PlayViewerVideo();
        }

        private void StoreViewerSeek(int seconds)
        {
            var v = ViewerVideoElement();
            if (v == null || StoreViewerVideo == null) return;
            try
            {
                double len = v.NaturalDuration.HasTimeSpan ? v.NaturalDuration.TimeSpan.TotalSeconds : 0;
                double t = v.Position.TotalSeconds + seconds;
                if (len > 0) t = Math.Min(t, len - 0.5);
                v.Position = TimeSpan.FromSeconds(Math.Max(0, t));
                if (storeViewerAudio != null) storeViewerAudio.Position = v.Position;
            }
            catch (Exception) { }
        }

        private void CloseStoreViewer()
        {
            if (!storeViewerOpen) return;
            storeViewerOpen = false;
            storeViewerGen++;
            StopViewerProgress();
            StopViewerVideo();
            StoreViewerVideo = null;
            NotifyStoreOverlays();
            var back = storeViewerReturn;
            ui.BeginInvoke(new Action(() => { if (back != null) UiTools.FocusItem("KX_StoreMediaList", back); else FocusPanelSoon(); }), DispatcherPriority.ContextIdle);
            ResumeStoreHero();
        }

        private MediaElement ViewerVideoElement()
        {
            if (storeViewerVideo != null) return storeViewerVideo;
            storeViewerVideo = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreViewerVideo") as MediaElement;
            if (storeViewerVideo != null)
            {
                storeViewerVideo.MediaEnded += (s, e) =>
                {
                    storeViewerPlaying = false;
                    try { storeViewerVideo.Stop(); if (storeViewerAudio != null) storeViewerAudio.Stop(); } catch (Exception) { }
                    Notify("StoreViewerPlaying");
                    UpdateMusicState();
                };
                storeViewerVideo.MediaFailed += (s, e) =>
                {
                    storeViewerPlaying = false;
                    StoreViewerVideo = null;
                    StoreViewerStatus = Loc("LOC_KX_StoreTrailerNone", "This trailer can't be played here.");
                    NotifyStoreOverlays();
                    UpdateMusicState();
                };
            }
            return storeViewerVideo;
        }

        private void PlayViewerVideo()
        {
            var v = ViewerVideoElement();
            if (v == null) return;
            try
            {
                v.Play();
                storeViewerPlaying = true;
                if (storeViewerAudio != null) { storeViewerAudio.Position = v.Position; storeViewerAudio.Volume = settings.StoreTrailerSound ? 0.8 : 0; storeViewerAudio.Play(); }
            }
            catch (Exception) { storeViewerPlaying = false; }
            Notify("StoreViewerPlaying");
            UpdateMusicState();
        }

        private void StopViewerVideo()
        {
            if (storeViewerVideo != null) try { storeViewerVideo.Stop(); } catch (Exception) { }
            if (storeViewerAudio != null) { try { storeViewerAudio.Stop(); storeViewerAudio.Close(); } catch (Exception) { } storeViewerAudio = null; }
            if (storeViewerPlaying) { storeViewerPlaying = false; Notify("StoreViewerPlaying"); UpdateMusicState(); }
        }

        /// <summary>When a trailer couldn't be joined into one file, its sound is a separate file played alongside.</summary>
        private MediaPlayer storeViewerAudio;

        private void OpenViewerAudio(string audio)
        {
            if (storeViewerAudio != null) { try { storeViewerAudio.Close(); } catch (Exception) { } storeViewerAudio = null; }
            if (string.IsNullOrEmpty(audio) || !File.Exists(audio)) return;
            storeViewerAudio = new MediaPlayer();
            storeViewerAudio.Open(new Uri(audio));
        }

        // ---------------- trailer at the top of the page ----------------

        private void AutoplayStoreTrailer(int gen)
        {
            var m = StoreMedia.FirstOrDefault(x => x.Kind == "steam");
            if (m == null) return;
            ResolveSteamTrailer(m.Key, files =>
            {
                if (files == null || gen != storeGameGen || storePage != "game" || openPanel != "store" || storeViewerOpen) return;
                StoreHeroVideo = new Uri(files.Video);
                NotifyStoreOverlays();
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != storeGameGen || StoreHeroVideo == null) return;
                    var v = HeroVideoElement();
                    if (v == null) return;
                    try { v.Play(); storeHeroPlaying = true; } catch (Exception) { storeHeroPlaying = false; }
                    NotifyStoreOverlays();
                    UpdateMusicState();
                }), DispatcherPriority.Loaded);
            });
        }

        private MediaElement HeroVideoElement()
        {
            if (storeHeroVideo != null) return storeHeroVideo;
            storeHeroVideo = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreHeroVideo") as MediaElement;
            if (storeHeroVideo != null)
            {
                storeHeroVideo.MediaEnded += (s, e) => { try { storeHeroVideo.Position = TimeSpan.Zero; storeHeroVideo.Play(); } catch (Exception) { } };
                storeHeroVideo.MediaFailed += (s, e) => { storeHeroPlaying = false; StoreHeroVideo = null; NotifyStoreOverlays(); UpdateMusicState(); };
            }
            return storeHeroVideo;
        }

        private bool heroPausedForViewer;

        private void PauseStoreHero()
        {
            heroPausedForViewer = storeHeroPlaying;
            if (!storeHeroPlaying) return;
            if (storeHeroVideo != null) try { storeHeroVideo.Pause(); } catch (Exception) { }
            storeHeroPlaying = false;
            UpdateMusicState();
        }

        private void ResumeStoreHero()
        {
            if (!heroPausedForViewer || StoreHeroVideo == null || storePage != "game") return;
            heroPausedForViewer = false;
            var v = HeroVideoElement();
            if (v == null) return;
            try { v.Play(); storeHeroPlaying = true; } catch (Exception) { }
            NotifyStoreOverlays();
            UpdateMusicState();
        }

        private void StopStoreVideos()
        {
            StopTilePreview();
            StopViewerVideo();
            if (storeHeroVideo != null) try { storeHeroVideo.Stop(); } catch (Exception) { }
            bool was = storeHeroPlaying;
            storeHeroPlaying = false;
            heroPausedForViewer = false;
            StoreHeroVideo = null;
            NotifyStoreOverlays();
            if (was) UpdateMusicState();
        }

        private void ApplyStoreVideoSound()
        {
            if (storeViewerAudio != null) storeViewerAudio.Volume = settings.StoreTrailerSound ? 0.8 : 0;
            Notify("StoreVideoMuted");
            UpdateMusicState();
        }

        /// <summary>A Steam game's trailer as a local file (downloaded once, shared with the game page's cache).</summary>
        private void ResolveSteamTrailer(string appId, Action<TrailerFiles> done)
        {
            TrailerFiles cached;
            lock (steamTrailers)
            {
                if (steamTrailers.TryGetValue(appId, out cached))
                {
                    if (cached == null || File.Exists(cached.Video)) { done(cached); return; }
                    steamTrailers.Remove(appId);   // the cache was trimmed since: download it again
                }
            }
            lock (trailerWaiters)
            {
                List<Action<TrailerFiles>> waiting;
                if (trailerWaiters.TryGetValue(appId, out waiting)) { waiting.Add(done); return; }
                trailerWaiters[appId] = new List<Action<TrailerFiles>> { done };
            }
            string cacheDir = Path.Combine(plugin.GetPluginUserDataPath(), "Trailers");
            KippixShared.SafeWork.Queue(_ =>
            {
                TrailerFiles files = null;
                bool failed = false;
                try { files = GetSteamTrailer(appId, cacheDir); }
                catch (Exception ex) { failed = true; logger.Warn("Kippix: store trailer for " + appId + " failed: " + ex.Message); }
                if (!failed) lock (steamTrailers) steamTrailers[appId] = files;   // a dropped connection isn't "no trailer": tried again next time
                List<Action<TrailerFiles>> callbacks;
                lock (trailerWaiters)
                {
                    trailerWaiters.TryGetValue(appId, out callbacks);
                    trailerWaiters.Remove(appId);
                }
                OnUi(() =>
                {
                    if (callbacks == null) return;
                    foreach (var cb in callbacks)
                        try { cb(files); } catch (Exception ex) { logger.Warn(ex, "Kippix: trailer callback failed"); }
                });
            });
        }
    }
}

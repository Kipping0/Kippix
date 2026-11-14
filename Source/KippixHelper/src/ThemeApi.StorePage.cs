using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Store, v0.23: the game page's single rotating review, the editions picker, the viewer's on-screen trailer controls,
    /// the Store settings sub-pages (Available stores, Front page rows) and the trailer switches.
    /// </summary>
    public partial class ThemeApi
    {
        public ICommand StoreReviewOpenCommand { get; private set; }
        public ICommand StoreEditionsCommand { get; private set; }
        public ICommand StoreViewerBackCommand { get; private set; }
        public ICommand StoreViewerForwardCommand { get; private set; }
        public ICommand StoreViewerSoundCommand { get; private set; }

        private void InitStorePage()
        {
            StoreReviewOpenCommand = new RelayCommand(_ => { var r = StoreCurrentReview; if (r != null) OpenReviewReader(r); });
            StoreEditionsCommand = new RelayCommand(_ => PickEdition());
            StoreViewerBackCommand = new RelayCommand(_ => StoreViewerSeek(-10));
            StoreViewerForwardCommand = new RelayCommand(_ => StoreViewerSeek(10));
            StoreViewerSoundCommand = new RelayCommand(_ => StoreTrailerSound = !settings.StoreTrailerSound);
        }

        // =====================================================================
        // Reviews: one at a time under the pictures, the next one every 10 seconds (not while it's highlighted)
        // =====================================================================

        private int storeReviewIndex;
        private DispatcherTimer storeReviewTimer;

        public StoreReview StoreCurrentReview
        {
            get { return StoreReviews != null && StoreReviews.Count > 0 ? StoreReviews[Math.Min(storeReviewIndex, StoreReviews.Count - 1)] : null; }
        }
        public bool HasStoreCurrentReview { get { return StoreCurrentReview != null; } }
        public string StoreReviewCounter
        {
            get { return StoreReviews != null && StoreReviews.Count > 1 ? (Math.Min(storeReviewIndex, StoreReviews.Count - 1) + 1) + " / " + StoreReviews.Count : ""; }
        }

        private void NotifyCurrentReview()
        {
            Notify("StoreCurrentReview");
            Notify("HasStoreCurrentReview");
            Notify("StoreReviewCounter");
        }

        /// <summary>Called when a game page's reviews arrive (or are cleared).</summary>
        private void ResetReviewRotation()
        {
            storeReviewIndex = 0;
            NotifyCurrentReview();
            if (StoreReviews == null || StoreReviews.Count < 2) { if (storeReviewTimer != null) storeReviewTimer.Stop(); return; }
            if (storeReviewTimer == null)
            {
                storeReviewTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(10) };
                storeReviewTimer.Tick += (s, e) => ReviewTick();
            }
            storeReviewTimer.Stop();
            storeReviewTimer.Start();
        }

        private void ReviewTick()
        {
            // only while a game page is up (the browser keeps the page underneath); anything else ends it
            if ((openPanel != "store" && openPanel != "browser") || storePage != "game" || StoreReviews == null || StoreReviews.Count < 2)
            {
                storeReviewTimer.Stop();
                return;
            }
            if (openPanel != "store" || storeReaderOpen || storeViewerOpen || storePickerOpen) return;
            var card = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreReviewCard") as UIElement;
            if (card != null && card.IsKeyboardFocusWithin) return;   // you're reading it
            storeReviewIndex = (storeReviewIndex + 1) % StoreReviews.Count;
            NotifyCurrentReview();
        }

        // =====================================================================
        // Editions: a button that opens the picker (Standard, Deluxe, Gold... with prices)
        // =====================================================================

        public string StoreEditionsText { get { return string.Format(Loc("LOC_KX_EditionsCount", "Editions ({0})"), StoreEditions != null ? StoreEditions.Count : 0); } }

        private void PickEdition()
        {
            if (StoreEditions == null || StoreEditions.Count == 0) return;
            var list = StoreEditions.ToList();
            var options = new List<Tuple<string, string>>();
            for (int i = 0; i < list.Count; i++)
            {
                var ed = list[i];
                // "Assassin's Creed Odyssey - Gold Edition" → "Gold Edition" (the game's name is the picker's title)
                string name = ed.StoreName ?? "";
                string game = storeGame != null ? storeGame.Name ?? "" : "";
                if (game.Length > 0 && name.StartsWith(game, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = name.Substring(game.Length).TrimStart(' ', '-', '\u2013', '\u2014', ':');
                    if (rest.Length > 0) name = rest;
                }
                string text = name + "   ·   " + ed.PriceText + (ed.HasDiscount ? "  (" + ed.DiscountText + ")" : "");
                options.Add(Tuple.Create(i.ToString(), text));
            }
            OpenStorePicker(string.Format(Loc("LOC_KX_EditionsOf", "Editions of {0}"), storeGame != null ? storeGame.Name : ""), options, "", key =>
            {
                int i;
                if (!int.TryParse(key, out i) || i < 0 || i >= list.Count) return;
                var cmd = list[i].OpenCommand;
                if (cmd != null && cmd.CanExecute(null)) cmd.Execute(null);
            });
        }

        // =====================================================================
        // Full screen viewer: on-screen trailer controls (Down from the picture), progress
        // =====================================================================

        public bool StoreViewerIsTrailer
        {
            get { return storeViewerOpen && StoreMedia != null && storeViewerIndex < StoreMedia.Count && StoreMedia[storeViewerIndex].Kind == "steam"; }
        }
        public bool StoreViewerPlaying { get { return storeViewerPlaying; } }
        public string StoreViewerTime { get; private set; }
        public double StoreViewerProgress { get; private set; }   // 0..900 (the bar's width)
        private const double ViewerBarWidth = 900;
        private DispatcherTimer viewerProgressTimer;

        private void NotifyViewerControls()
        {
            foreach (string n in new[] { "StoreViewerIsTrailer", "StoreViewerPlaying", "StoreViewerTime", "StoreViewerProgress" }) Notify(n);
        }

        private void StartViewerProgress()
        {
            if (viewerProgressTimer == null)
            {
                viewerProgressTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
                viewerProgressTimer.Tick += (s, e) => UpdateViewerProgress();
            }
            viewerProgressTimer.Start();
            UpdateViewerProgress();
        }

        private void StopViewerProgress()
        {
            if (viewerProgressTimer != null) viewerProgressTimer.Stop();
        }

        private void UpdateViewerProgress()
        {
            if (!storeViewerOpen) { StopViewerProgress(); return; }
            double pos = 0, len = 0;
            var v = StoreViewerVideo != null ? ViewerVideoElement() : null;
            if (v != null)
            {
                try
                {
                    pos = v.Position.TotalSeconds;
                    if (v.NaturalDuration.HasTimeSpan) len = v.NaturalDuration.TimeSpan.TotalSeconds;
                }
                catch (Exception) { }
            }
            StoreViewerTime = len > 0 ? Clock(pos) + " / " + Clock(len) : "";
            StoreViewerProgress = len > 0 ? Math.Max(0, Math.Min(1, pos / len)) * ViewerBarWidth : 0;
            NotifyViewerControls();
        }

        private bool InViewerControls()
        {
            var f = Keyboard.FocusedElement as DependencyObject;
            var bar = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreViewerControls");
            return f != null && bar != null && bar.IsAncestorOf(f);
        }

        /// <summary>Arrow keys in the viewer: Left / Right change picture (on the picture), Down to the trailer controls, Up back.</summary>
        private bool ViewerArrowKey(Key key)
        {
            if (InViewerControls())
            {
                if (key == Key.Up) { UiTools.FocusByName("KX_StoreViewerButton"); return true; }
                return key == Key.Down;   // Left / Right move between the controls
            }
            if (key == Key.Left || key == Key.Right) StoreViewerStep(key == Key.Right ? 1 : -1);
            else if (key == Key.Down && StoreViewerIsTrailer) UiTools.FocusByName("KX_StoreViewerPlay");
            return true;
        }

        // =====================================================================
        // Store settings: sub-pages and the trailer switches
        // =====================================================================

        public bool IsOptionsStoreTiles { get { return optionsPage == "storetiles"; } }
        public bool IsOptionsStoreRows { get { return optionsPage == "storerows"; } }

        public string StoreTilesSummary
        {
            get
            {
                var on = new HashSet<string>((settings.StoreTiles ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                var names = StoreDefs.Where(d => on.Contains(d[0])).Select(d => d[1]).ToList();
                return names.Count == 0 ? Loc("LOC_KX_StoreTilesNone", "Only All stores") : string.Join(", ", names.ToArray());
            }
        }

        public string StoreRowsSummary
        {
            get
            {
                int n = new[] { settings.StoreRowWish, settings.StoreRowRecent, settings.StoreRowFree, settings.StoreRowPopular, settings.StoreRowSoon, settings.StoreRowDeals }.Count(b => b);
                return string.Format(Loc("LOC_KX_RowsShown", "{0} of 6 shown"), n);
            }
        }

        /// <summary>On: a game's trailer starts by itself at the top of its page. Trailers are always with the pictures.</summary>
        public bool StoreTrailerAutoplay
        {
            get { return settings.StoreTrailers == 2; }
            set
            {
                int v = value ? 2 : 1;
                if (settings.StoreTrailers == v) return;
                settings.StoreTrailers = v;
                SaveSettings();
                Notify("StoreTrailerAutoplay");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>A part of the reader (system requirements: Minimum / Recommended / Your PC).</summary>
    public class ReaderSection
    {
        public string Heading { get; set; }
        public string Body { get; set; }
        public List<DriveBar> Drives { get; set; }
        public bool HasDrives { get { return Drives != null && Drives.Count > 0; } }
        public List<SpecCheck> Checks { get; set; }
        public bool HasChecks { get { return Checks != null && Checks.Count > 0; } }
        public bool HasBody { get { return !string.IsNullOrEmpty(Body); } }
    }

    /// <summary>One drive in "Your PC": size, used / free as a bar, and whether the game fits.</summary>
    public class DriveBar
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public double UsedWidth { get; set; }      // of 640
        public Brush Fill { get; set; }
        public string FitsText { get; set; }
        public bool FitsGood { get; set; }
        public bool HasFits { get { return !string.IsNullOrEmpty(FitsText); } }
    }

    /// <summary>
    /// Store, v0.22: the wishlist page, add-ons (DLC) and editions, the description and requirements readers,
    /// hiding free games, trailer previews on game tiles, the wishlist alert on the top bar, front page row switches.
    /// </summary>
    public partial class ThemeApi
    {
        public ICommand StoreWishlistCommand { get; private set; }
        public ICommand StoreDlcCommand { get; private set; }
        public ICommand StoreDescriptionCommand { get; private set; }
        public ObservableCollection<StorePrice> StoreEditions { get; private set; }
        public ObservableCollection<ReaderSection> ReaderSections { get; private set; }

        private void InitStoreMore()
        {
            InitStorePage();
            InitStoreQol();
            StoreEditions = new ObservableCollection<StorePrice>();
            ReaderSections = new ObservableCollection<ReaderSection>();
            StoreWishlistCommand = new RelayCommand(_ => OpenWishlistPage());
            StoreDlcCommand = new RelayCommand(_ => OpenDlcList());
            StoreDescriptionCommand = new RelayCommand(_ => OpenDescriptionReader());
        }

        // =====================================================================
        // Wishlist page (the button at the bottom of the Store's first page)
        // =====================================================================

        public bool HasStoreWishButton { get { return HasItadKey; } }
        public string StoreWishButtonText
        {
            get
            {
                int n = settings.StoreWishlist != null ? settings.StoreWishlist.Count : 0;
                return n > 0 ? string.Format(Loc("LOC_KX_WishButtonCount", "Wishlist ({0})"), n) : Loc("LOC_KX_WishButton", "Wishlist");
            }
        }

        private void OpenWishlistPage()
        {
            StopTilePreview();
            OpenStoreList("wishlist", null);
        }

        /// <summary>Every wishlist game with today's best price (the list is small, so one page).</summary>
        private StoreResults WishlistPage(StoreQuery q, List<WishItem> wishes)
        {
            var res = new StoreResults();
            Dictionary<string, Tuple<List<ItadDeal>, ItadDeal>> prices = null;
            var ids = wishes.Select(w => w.ItadId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            if (ids.Count > 0 && HasItadKey)
                try { prices = ItadPrices(ids, ItadShops); } catch (Exception ex) { logger.Warn("Kippix: wishlist prices: " + ex.Message); }
            var rows = new List<Tuple<StoreItem, WishItem, decimal, decimal>>();   // item, wish, price now (-1 unknown), drop
            foreach (var w in wishes)
            {
                var item = new StoreItem
                {
                    Store = "", Id = w.ItadId ?? w.Name, ItadId = w.ItadId, SteamAppId = w.SteamAppId, Name = w.Name, Image = w.Image, Link = w.Link
                };
                // brought in from Steam / GOG and not matched on IsThereAnyDeal yet: open it on its own store
                if (string.IsNullOrEmpty(w.ItadId) && !string.IsNullOrEmpty(w.Store) && !string.IsNullOrEmpty(w.Id)) { item.Store = w.Store; item.Id = w.Id; }
                decimal now = -1, drop = 0;
                Tuple<List<ItadDeal>, ItadDeal> p;
                if (prices != null && w.ItadId != null && prices.TryGetValue(w.ItadId, out p) && p.Item1.Count > 0)
                {
                    var best = p.Item1.OrderBy(d => d.Price).First();
                    now = best.Price;
                    item.PriceText = best.Price == 0 ? Loc("LOC_KX_StoreFree", "Free") : Money(best.Price, best.Currency);
                    item.DiscountText = best.Cut > 0 ? "-" + best.Cut + "%" : null;
                    item.OldPriceText = best.Cut > 0 ? Money(best.Regular, best.Currency) : null;
                    bool dropped;
                    item.Note = WishNote(w, best, out dropped);
                    if (dropped && w.AddedPrice > 0 && best.Currency == w.Currency) drop = Math.Max(0, w.AddedPrice - best.Price);
                    item.IsDropped = dropped;
                    item.IsWished = true;
                }
                else item.Note = string.Format(Loc("LOC_KX_WishAdded", "Added {0}"), w.Added.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
                rows.Add(Tuple.Create(item, w, now, drop));
            }
            IEnumerable<Tuple<StoreItem, WishItem, decimal, decimal>> sorted;
            if (q.Sort == "drop") sorted = rows.OrderByDescending(r => r.Item4).ThenByDescending(r => r.Item2.Added);
            else if (q.Sort == "pricelow") sorted = rows.OrderBy(r => r.Item3 < 0 ? decimal.MaxValue : r.Item3);
            else if (q.Sort == "name") sorted = rows.OrderBy(r => r.Item1.Name, StringComparer.CurrentCultureIgnoreCase);
            else sorted = rows.OrderByDescending(r => r.Item2.Added);
            res.Items = sorted.Select(r => r.Item1).ToList();
            res.Total = res.Items.Count;
            return res;
        }

        // =====================================================================
        // Add-ons (DLC): a list of their own, B goes back to the game
        // =====================================================================

        private class SavedStoreList
        {
            public string Id;
            public StoreQuery Query;
            public List<StoreItem> Items;
            public bool HasMore;
            public int PageNo, Total;
        }

        private StoreItem dlcParent;
        private string dlcParentFrom, dlcAppId;
        private SavedStoreList dlcSaved;
        private int storeDlcCount;

        public bool HasStoreDlc { get { return storeDlcCount > 0 && !IsSubList(storeId); } }
        public string StoreDlcText { get { return string.Format(Loc("LOC_KX_AddOnsCount", "Add-ons ({0})"), storeDlcCount); } }
        public bool HasStoreEditions { get { return StoreEditions != null && StoreEditions.Count > 1; } }

        private void NotifyStoreMore()
        {
            foreach (string n in new[] { "HasStoreDlc", "StoreDlcText", "HasStoreEditions", "StoreEditionsText", "HasStoreWishButton", "StoreWishButtonText",
                                         "HasStoreSimilar", "StoreAlertText", "StoreShowWishHint" }) Notify(n);
        }

        private void OpenDlcList()
        {
            if (storeGame == null || string.IsNullOrEmpty(storeReviewAppId) || storeDlcCount == 0 || IsSubList(storeId)) return;
            OpenSubList("dlc");
        }

        /// <summary>A list that belongs to the game you're on (its add-ons, games like it); B goes back to the game.</summary>
        private string subListKind;

        private void OpenSubList(string kind)
        {
            subListKind = kind;
            StopStoreVideos();
            dlcParent = storeGame;
            dlcParentFrom = storeGameFrom;
            dlcAppId = storeReviewAppId;
            dlcSaved = new SavedStoreList
            {
                Id = storeId, Query = storeQuery.Clone(), Items = StoreItems.ToList(), HasMore = storeHasMore, PageNo = storePageNo, Total = storeTotal
            };
            dlcPage = storeGameParts > 0 ? null : SaveGamePage();   // back from the add-ons shows it straight away (if it had finished loading)
            storeGameGen++;
            OpenStoreList(kind, null);
        }

        // ---------------- the game's page, kept while you look at its add-ons ----------------

        private class GamePageSnapshot
        {
            public StoreItem Game;
            public ImageSource Hero;
            public string Description, Facts, Lowest, Highest, Source, ReviewTitle, ReviewLine, Scores, WebLink, ItadId, ReqMin, ReqRec, ReqFrom;
            public string ReviewAppId, ReviewCursor, ReviewSource, ReviewSourceId;
            public bool Owned;
            public int DlcCount;
            public List<StorePrice> Prices, Editions;
            public List<StoreMedia> Media;
            public List<StoreReview> Reviews;
            public List<StoreBadge> Badges;
        }

        private GamePageSnapshot dlcPage;

        private GamePageSnapshot SaveGamePage()
        {
            return new GamePageSnapshot
            {
                Game = storeGame, Hero = StoreHeroImage, Description = StoreGameDescription, Facts = StoreGameFacts, Lowest = StoreLowestText,
                Highest = StoreHighestText, Source = StorePriceSource, ReviewTitle = StoreReviewTitle, ReviewLine = StoreReviewLine, Scores = StoreScores,
                WebLink = storeWebLink, ItadId = storeGameItadId, ReqMin = storeReqMin, ReqRec = storeReqRec, ReqFrom = storeReqFrom,
                ReviewAppId = storeReviewAppId, ReviewCursor = storeReviewCursor, ReviewSource = storeReviewSource, ReviewSourceId = storeReviewSourceId,
                Owned = StoreGameOwned, DlcCount = storeDlcCount,
                Prices = StorePrices.ToList(), Editions = StoreEditions.ToList(), Media = StoreMedia.ToList(), Reviews = StoreReviews.ToList(), Badges = StoreBadges.ToList()
            };
        }

        /// <summary>focusName: what to highlight (null: the first price).</summary>
        private void RestoreGamePage(GamePageSnapshot p, string focusName) { RestoreGamePage(p, focusName, null); }

        /// <summary>current: the tile you just opened (a cached page belongs to an older copy of it); null = the snapshot's own.</summary>
        private void RestoreGamePage(GamePageSnapshot p, string focusName, StoreItem current)
        {
            storeGameParts = 0;
            StopStoreVideos();
            storeRequest++;
            int gen = ++storeGameGen;
            var game = current ?? p.Game;
            storeGame = game;
            storeListFocus = game;
            storePage = "game";
            StoreHeroImage = p.Hero ?? game.Thumb;
            StoreGameDescription = p.Description;
            StoreGameFacts = p.Facts;
            StoreLowestText = p.Lowest;
            StoreHighestText = p.Highest;
            StorePriceSource = p.Source;
            StoreReviewTitle = p.ReviewTitle;
            StoreReviewLine = p.ReviewLine;
            StoreScores = p.Scores;
            storeWebLink = p.WebLink;
            storeGameItadId = p.ItadId;
            storeReqMin = p.ReqMin;
            storeReqFrom = p.ReqFrom;
            storeReqRec = p.ReqRec;
            storeReviewAppId = p.ReviewAppId;
            storeReviewCursor = p.ReviewCursor;
            storeReviewSource = p.ReviewSource;
            storeReviewSourceId = p.ReviewSourceId;
            StoreGameOwned = current != null ? libraryKeys != null && libraryKeys.Contains(NameKey(game.Name)) : p.Owned;
            storeDlcCount = p.DlcCount;
            StorePricesLoading = false;
            StorePrices.Clear(); foreach (var x in p.Prices) StorePrices.Add(x);
            StoreEditions.Clear(); foreach (var x in p.Editions) StoreEditions.Add(x);
            StoreMedia.Clear(); foreach (var x in p.Media) StoreMedia.Add(x);
            StoreReviews.Clear(); foreach (var x in p.Reviews) StoreReviews.Add(x);
            StoreBadges.Clear();
            if (StoreGameOwned) StoreBadges.Add(new StoreBadge { Text = OwnedBadgeText(game), IsGood = true, IsOwned = true });
            foreach (var x in p.Badges.Where(b => !b.IsOwned)) StoreBadges.Add(x);
            foreach (string n in new[] { "HasStoreBadges", "HasStoreRequirements", "StoreHighestText", "HasStoreHighest" }) Notify(n);
            UpdateWishState();
            ResetReviewRotation();
            NotifyStore();
            var firstPrice = StorePrices.Count > 0 ? StorePrices[0] : null;
            ui.BeginInvoke(new Action(() =>
            {
                if (storePage != "game" || gen != storeGameGen) return;
                if (focusName != null && UiTools.FocusByNameIfVisible(focusName)) return;
                if (firstPrice != null) UiTools.FocusItem("KX_StorePriceList", firstPrice); else FocusPanelSoon();
            }), DispatcherPriority.ContextIdle);
            if (settings.StoreTrailers == 2) AutoplayStoreTrailer(gen);
        }

        private StoreResults DlcPage(string appId, StoreQuery q)
        {
            var list = SteamDlc(appId);
            string free = Loc("LOC_KX_StoreFree", "Free");
            if (q.Sort == "pricelow") list = list.OrderBy(i => i.PriceText == free ? 0 : Math.Max(0, ParseAmount(i.PriceText))).ToList();
            else if (q.Sort == "name") list = list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new StoreResults { Items = list, Total = list.Count };
        }

        /// <summary>B on the add-ons list: back to the game they belong to (and the list you found it in).</summary>
        private bool DlcBack()
        {
            if (dlcParent == null) return false;
            var parent = dlcParent;
            string from = dlcParentFrom;
            var saved = dlcSaved;
            dlcParent = null;
            dlcSaved = null;
            storeRequest++;
            storeListGen++;
            if (saved != null)
            {
                storeId = saved.Id;
                storeQuery = saved.Query;
                StoreItems.Clear();
                foreach (var i in saved.Items) StoreItems.Add(i);
                storeHasMore = saved.HasMore;
                storePageNo = saved.PageNo;
                storeTotal = saved.Total;
                // pictures still loading when the add-ons list opened were dropped: ask again
                foreach (var i in StoreItems.Where(x => x.Thumb == null).ToList()) { i.ThumbRequested = false; LoadItemThumb(i, storeListGen); }
                if (!string.IsNullOrEmpty(storeId)) { BuildChips(); UpdateSectionTitle(); }
                SetStoreStatus("");
            }
            storeGameFrom = from;
            var snap = dlcPage;
            dlcPage = null;
            if (snap != null && ReferenceEquals(snap.Game, parent)) RestoreGamePage(snap, subListKind == "similar" ? "KX_StoreSimilarButton" : "KX_StoreDlcButton");
            else OpenStoreGame(parent);
            storeGameFrom = from;
            return true;
        }

        // =====================================================================
        // Readers: the whole description, and system requirements in sections
        // =====================================================================

        public bool HasReaderSections { get { return ReaderSections != null && ReaderSections.Count > 0; } }
        public bool ReaderShowText { get { return !HasReaderSections; } }
        public bool HasStoreDescription { get { return !string.IsNullOrEmpty(StoreGameDescription); } }

        private void OpenDescriptionReader()
        {
            if (string.IsNullOrEmpty(StoreGameDescription)) return;
            PlaySound("select");
            readerMode = "desc";
            readerGen++;          // a review page still loading must not land here
            readerEnd = true;
            ReaderChips.Clear();
            ReaderSections.Clear();
            ReaderTitle = Loc("LOC_KX_AboutGame", "About this game");
            ReaderSub = storeGame != null ? storeGame.Name : "";
            ReaderText = StoreGameDescription;
            ReaderCounter = "";
            ReaderStatus = "";
            OpenReader();
        }

        private void FillRequirementSections()
        {
            ReaderSections.Clear();
            // Your PC first (how it compares), then what the game asks for
            // what the game asks for (the bigger of minimum / recommended), compared with this PC
            double needDisk = Math.Max(ReqAmount(storeReqMin, DiskPattern), ReqAmount(storeReqRec, DiskPattern));
            // memory, processor and graphics each get a row (yours, a bar with the minimum / recommended marks, a verdict);
            // the text keeps what isn't compared (Windows)
            List<SpecCheck> checks = null;
            try { checks = SpecChecks(storeReqMin, storeReqRec); } catch (Exception ex) { logger.Warn("Kippix: spec check: " + ex.Message); }
            string body = PcSpecs();
            if (checks != null && checks.Count > 0)
                body = string.Join("\n", body.Split('\n').Where(l => !l.StartsWith("Processor:") && !l.StartsWith("Graphics:") && !l.StartsWith("Memory:")).ToArray());
            ReaderSections.Add(new ReaderSection { Heading = Loc("LOC_KX_YourPc", "Your PC"), Body = body, Checks = checks, Drives = DriveBars(needDisk) });
            AddRequirementSection(storeReqMin, Loc("LOC_KX_ReqMinimum", "Minimum"));
            AddRequirementSection(storeReqRec, Loc("LOC_KX_ReqRecommended", "Recommended"));
        }

        /// <summary>Steam's text starts with "Minimum:" / "Recommended:" (that's the heading now) and has blank lines between specs (dropped).</summary>
        private const string DiskPattern = @"(?:Storage|Hard Drive|Hard Disk(?: Space)?|Disk Space|HDD|Free (?:Disk )?Space)\s*:\s*([\d.,]+)\s*(TB|GB|MB)";
        private const string RamPattern = @"(?:Memory|RAM)\s*:\s*([\d.,]+)\s*(GB|MB)";

        /// <summary>An amount in GB from requirement text ("Storage: 70 GB available space"), 0 when it isn't there.</summary>
        private static double ReqAmount(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            double v;
            string num = m.Groups[1].Value;
            // "1,500 MB" is thousands; "1,5 GB" is a decimal comma
            num = System.Text.RegularExpressions.Regex.IsMatch(num, @"^\d{1,3}(,\d{3})+$") ? num.Replace(",", "") : num.Replace(",", ".");
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0;
            string unit = m.Groups[2].Value.ToUpperInvariant();
            return unit == "TB" ? v * 1024 : unit == "MB" ? v / 1024 : v;
        }

        private static double TotalRamGb()
        {
            try { var m = new MemoryStatus(); return GlobalMemoryStatusEx(m) ? m.TotalPhys / 1073741824.0 : 0; } catch (Exception) { return 0; }
        }

        private static string SizeText(double gb)
        {
            return gb >= 1000 ? (gb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " TB" : Math.Round(gb).ToString("N0", CultureInfo.CurrentCulture) + " GB";
        }

        private static readonly Brush DriveOk = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80)), DriveWarn = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24)), DriveFull = Frozen(Color.FromRgb(0xF4, 0x3F, 0x5E));

        /// <summary>Every fixed drive: size, used and free, and (when the game says how much it needs) whether it fits.</summary>
        private List<DriveBar> DriveBars(double needGb)
        {
            var list = new List<DriveBar>();
            try
            {
                foreach (var d in System.IO.DriveInfo.GetDrives())
                {
                    if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                    double total = d.TotalSize / 1073741824.0, free = d.AvailableFreeSpace / 1073741824.0, used = total - free;
                    double frac = Math.Max(0, Math.Min(1, used / total));
                    string label = "";
                    try { label = d.VolumeLabel; } catch (Exception) { }
                    var bar = new DriveBar
                    {
                        Name = d.Name.TrimEnd('\\') + (string.IsNullOrEmpty(label) ? "" : "  " + label),
                        Text = string.Format(Loc("LOC_KX_DriveLine", "{0}  ·  {1} used  ·  {2} free"), SizeText(total), SizeText(used), SizeText(free)),
                        UsedWidth = Math.Round(frac * 640),
                        Fill = frac >= 0.9 ? DriveFull : frac >= 0.75 ? DriveWarn : DriveOk
                    };
                    if (needGb > 0)
                    {
                        bar.FitsGood = free >= needGb;
                        bar.FitsText = bar.FitsGood ? "✓ " + Loc("LOC_KX_DriveFits", "Fits")
                                                    : "✗ " + string.Format(Loc("LOC_KX_DriveShort", "{0} short"), SizeText(needGb - free));
                    }
                    list.Add(bar);
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: drives: " + ex.Message); }
            return list;
        }

        private void AddRequirementSection(string text, string heading)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count > 0)
            {
                foreach (var prefix in new[] { "Minimum:", "Recommended:", "Minimum", "Recommended" })
                    if (lines[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = lines[0].Substring(prefix.Length).Trim();
                        if (rest.Length > 0) lines[0] = rest; else lines.RemoveAt(0);
                        break;
                    }
            }
            if (lines.Count == 0) return;
            ReaderSections.Add(new ReaderSection { Heading = heading, Body = string.Join("\n", lines.ToArray()) });
        }

        // =====================================================================
        // Hide free games (Price picker, and Store settings)
        // =====================================================================

        public bool StoreHideFree
        {
            get { return settings.StoreHideFree; }
            set
            {
                if (settings.StoreHideFree == value) return;
                settings.StoreHideFree = value;
                SaveSettings();
                Notify("StoreHideFree");
                // the switch in the Price picker: use it straight away (the Store settings page refreshes the Store when you leave it)
                if (storePickerOpen && storePage == "list") ui.BeginInvoke(new Action(ApplyHideFreeFromPicker), DispatcherPriority.Input);
            }
        }

        private bool HidesFree
        {
            get { return settings.StoreHideFree && storeId != "wishlist" && storeQuery.Sort != "free" && storeQuery.Price != "free"; }
        }

        private void ApplyHideFreeFromPicker()
        {
            if (!storePickerOpen) return;
            CloseStorePicker(false);
            ReloadStoreList(false);
            var chip = StoreChips.FirstOrDefault(c => c.Key == "price");
            if (chip != null) ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_StoreChips", chip)), DispatcherPriority.ContextIdle);
            else FocusChip(0);
        }

        // =====================================================================
        // Store settings: front page rows, trailer preview on game tiles
        // =====================================================================

        public bool StoreRowWish { get { return settings.StoreRowWish; } set { SetRow(ref settings.StoreRowWish, value, "StoreRowWish"); } }
        public bool StoreRowRecent { get { return settings.StoreRowRecent; } set { SetRow(ref settings.StoreRowRecent, value, "StoreRowRecent"); } }
        public bool StoreRowFree { get { return settings.StoreRowFree; } set { SetRow(ref settings.StoreRowFree, value, "StoreRowFree"); } }
        public bool StoreRowPopular { get { return settings.StoreRowPopular; } set { SetRow(ref settings.StoreRowPopular, value, "StoreRowPopular"); } }
        public bool StoreRowSoon { get { return settings.StoreRowSoon; } set { SetRow(ref settings.StoreRowSoon, value, "StoreRowSoon"); } }
        public bool StoreRowDeals { get { return settings.StoreRowDeals; } set { SetRow(ref settings.StoreRowDeals, value, "StoreRowDeals"); } }

        private void SetRow(ref bool field, bool value, string name)
        {
            if (field == value) return;
            field = value;
            SaveSettings();
            Notify(name);
            Notify("StoreRowsSummary");
        }

        public bool StorePreview
        {
            get { return settings.StorePreview; }
            set { if (settings.StorePreview == value) return; settings.StorePreview = value; SaveSettings(); Notify("StorePreview"); if (!value) StopTilePreview(); }
        }
        public bool StorePreviewSound
        {
            get { return settings.StorePreviewSound; }
            set { if (settings.StorePreviewSound == value) return; settings.StorePreviewSound = value; SaveSettings(); Notify("StorePreviewSound"); }
        }

        private static readonly int[] StorePreviewDelays = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 15 };
        private const double StorePreviewTrack = 480;

        private int StorePreviewDelayIndex
        {
            get
            {
                int best = 0;
                for (int i = 0; i < StorePreviewDelays.Length; i++)
                    if (Math.Abs(StorePreviewDelays[i] - settings.StorePreviewDelay) < Math.Abs(StorePreviewDelays[best] - settings.StorePreviewDelay)) best = i;
                return best;
            }
        }
        public string StorePreviewDelayText
        {
            get
            {
                int d = settings.StorePreviewDelay;
                return d <= 0 ? Loc("LOC_KX_PreviewNow", "Straight away") : string.Format(Loc("LOC_KX_Seconds", "{0} s"), d);
            }
        }
        private double StorePreviewFraction { get { return (double)StorePreviewDelayIndex / (StorePreviewDelays.Length - 1); } }
        public double StorePreviewFillWidth { get { return StorePreviewFraction * StorePreviewTrack; } }
        public Thickness StorePreviewKnobMargin { get { return new Thickness(StorePreviewFraction * StorePreviewTrack - 12, 0, 0, 0); } }

        private void NudgeStorePreviewDelay(int direction)
        {
            int i = Math.Max(0, Math.Min(StorePreviewDelays.Length - 1, StorePreviewDelayIndex + direction));
            if (StorePreviewDelays[i] == settings.StorePreviewDelay) return;
            settings.StorePreviewDelay = StorePreviewDelays[i];
            SaveSettings();
            PlaySound("move");
            foreach (string n in new[] { "StorePreviewDelayText", "StorePreviewFillWidth", "StorePreviewKnobMargin" }) Notify(n);
        }

        // =====================================================================
        // Focus on the Store's first page and lists: whole rows scroll into view (with their titles), trailer previews
        // =====================================================================

        private void StoreFocusChanged(KeyboardFocusChangedEventArgs e)
        {
            if (openPanel != "store" || (storePage != "home" && storePage != "list") || storePickerOpen || storeReaderOpen || storeViewerOpen)
            {
                if (tilePreviewItem != null) StopTilePreview();
                return;
            }
            var fe = e.NewFocus as FrameworkElement;
            if (fe == null) { StopTilePreview(); return; }
            var item = fe.DataContext as StoreItem;
            if (storePage == "home")
            {
                if (item != null) BringRowIntoView(fe);
                else if (fe.DataContext is StoreTile) BringIntoViewWithHeader(fe, 60);
            }
            if (item != null) ScheduleTilePreview(item);
            else StopTilePreview();
            if (item != null) SchedulePrefetch(item);
        }

        private DispatcherTimer prefetchTimer;
        private StoreItem prefetchItem;

        /// <summary>A game that stays highlighted a moment: its details are read ahead (one at a time, never while scrolling fast).</summary>
        private void SchedulePrefetch(StoreItem item)
        {
            prefetchItem = item;
            if (prefetchTimer == null)
            {
                prefetchTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(350) };
                prefetchTimer.Tick += (s, e) =>
                {
                    prefetchTimer.Stop();
                    var it = prefetchItem;
                    if (it != null && openPanel == "store") PrefetchSteamDetails(it.SteamAppId);
                };
            }
            prefetchTimer.Stop();
            prefetchTimer.Start();
        }

        /// <summary>The row the highlighted game is in, title included (going up, a tile alone would hide its row's title).</summary>
        private void BringRowIntoView(FrameworkElement fe)
        {
            DependencyObject cur = fe;
            FrameworkElement rowRoot = null;
            StoreRow row = null;
            while (cur != null)
            {
                var f = cur as FrameworkElement;
                if (f != null)
                {
                    var r = f.DataContext as StoreRow;
                    if (r != null && (row == null || ReferenceEquals(r, row))) { row = r; rowRoot = f; }
                    else if (row != null) break;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            if (rowRoot == null) return;
            var target = rowRoot;
            ui.BeginInvoke(new Action(() => { try { if (target.IsVisible) target.BringIntoView(); } catch (Exception) { } }), DispatcherPriority.Loaded);
        }

        private void BringIntoViewWithHeader(FrameworkElement fe, double above)
        {
            var target = fe;
            ui.BeginInvoke(new Action(() =>
            {
                try { if (target.IsVisible) target.BringIntoView(new Rect(0, -above, target.ActualWidth, target.ActualHeight + above)); } catch (Exception) { }
            }), DispatcherPriority.Loaded);
        }

        // ---------------- trailer preview on a game tile ----------------

        private StoreItem tilePreviewItem;
        private string tilePreviewFetching;                 // app id being downloaded for a preview (one at a time)
        private Action tilePreviewNext;                     // the preview waiting for that download to finish
        private MediaPlayer tilePreviewAudio;
        private DispatcherTimer tilePreviewTimer;
        private bool tilePreviewPlaying;
        private int tilePreviewGen;

        private static string PreviewAppId(StoreItem item)
        {
            if (item == null) return null;
            if (!string.IsNullOrEmpty(item.SteamAppId)) return item.SteamAppId;
            if (item.Store == "Steam") return item.Id;
            if (item.Merged != null) return item.Merged.Where(m => m.Store == "Steam").Select(m => m.Id).FirstOrDefault();
            return null;
        }

        private void ScheduleTilePreview(StoreItem item)
        {
            if (ReferenceEquals(item, tilePreviewItem)) return;
            StopTilePreview();
            if (!settings.StorePreview) return;
            string app = PreviewAppId(item);
            if (string.IsNullOrEmpty(app)) return;
            tilePreviewItem = item;
            int gen = ++tilePreviewGen;
            tilePreviewTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(Math.Max(0.3, settings.StorePreviewDelay)) };
            tilePreviewTimer.Tick += (s, e) =>
            {
                var t = s as DispatcherTimer;
                if (t != null) t.Stop();
                if (gen != tilePreviewGen) return;
                StartTilePreview(item, app, gen);
            };
            tilePreviewTimer.Start();
        }

        private bool TrailerCached(string app)
        {
            lock (steamTrailers)
            {
                TrailerFiles f;
                return steamTrailers.TryGetValue(app, out f) && (f == null || System.IO.File.Exists(f.Video));
            }
        }

        private void StartTilePreview(StoreItem item, string app, int gen)
        {
            // walking along a row mustn't start a download for every tile: one at a time, the newest one waits
            if (!TrailerCached(app) && tilePreviewFetching != null && tilePreviewFetching != app)
            {
                tilePreviewNext = () => { if (gen == tilePreviewGen) StartTilePreview(item, app, gen); };
                return;
            }
            bool fetching = !TrailerCached(app);
            if (fetching) tilePreviewFetching = app;
            ResolveSteamTrailer(app, files =>
            {
                if (fetching && tilePreviewFetching == app)
                {
                    tilePreviewFetching = null;
                    var next = tilePreviewNext;
                    tilePreviewNext = null;
                    if (next != null) next();
                }
                if (gen != tilePreviewGen || openPanel != "store" || files == null || string.IsNullOrEmpty(files.Video)) return;
                bool separateSound = !string.IsNullOrEmpty(files.Audio) && System.IO.File.Exists(files.Audio);
                item.PreviewMuted = !settings.StorePreviewSound || separateSound;
                item.PreviewVideo = new Uri(files.Video);
                item.IsPreviewing = true;
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != tilePreviewGen) return;
                    var me = FindPreviewElement(item);
                    if (me == null) return;
                    MediaPlayer audio = null;
                    if (separateSound && settings.StorePreviewSound)
                    {
                        audio = new MediaPlayer();
                        try { audio.Open(new Uri(files.Audio)); audio.Volume = 0.8; } catch (Exception) { audio = null; }
                        tilePreviewAudio = audio;
                    }
                    // the tile's video element stays (and is reused for other tiles): its handlers come off again in StopTilePreview
                    DetachTilePreview();
                    tilePreviewElement = me;
                    tilePreviewEnded = (s, e) =>
                    {
                        try { me.Position = TimeSpan.Zero; me.Play(); } catch (Exception) { }
                        if (audio != null && ReferenceEquals(audio, tilePreviewAudio)) try { audio.Position = TimeSpan.Zero; audio.Play(); } catch (Exception) { }
                    };
                    tilePreviewFailed = (s, e) => { if (gen == tilePreviewGen) StopTilePreview(); };
                    me.MediaEnded += tilePreviewEnded;
                    me.MediaFailed += tilePreviewFailed;
                    try { me.Play(); if (audio != null) audio.Play(); } catch (Exception) { return; }
                    tilePreviewPlaying = !item.PreviewMuted || audio != null;
                    if (tilePreviewPlaying) UpdateMusicState();
                }), DispatcherPriority.Loaded);
            });
        }

        private MediaElement FindPreviewElement(StoreItem item)
        {
            var root = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreView");
            return root != null ? FindMedia(root, item) : null;
        }

        private static MediaElement FindMedia(DependencyObject root, object data)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var me = c as MediaElement;
                if (me != null && me.Name == "KX_TilePreview" && ReferenceEquals(me.DataContext, data)) return me;
                var hit = FindMedia(c, data);
                if (hit != null) return hit;
            }
            return null;
        }

        private MediaElement tilePreviewElement;
        private RoutedEventHandler tilePreviewEnded;
        private EventHandler<ExceptionRoutedEventArgs> tilePreviewFailed;

        private void DetachTilePreview()
        {
            if (tilePreviewElement != null)
            {
                if (tilePreviewEnded != null) tilePreviewElement.MediaEnded -= tilePreviewEnded;
                if (tilePreviewFailed != null) tilePreviewElement.MediaFailed -= tilePreviewFailed;
            }
            tilePreviewElement = null;
            tilePreviewEnded = null;
            tilePreviewFailed = null;
        }

        private void StopTilePreview()
        {
            tilePreviewGen++;
            tilePreviewNext = null;
            DetachTilePreview();
            if (tilePreviewAudio != null) { try { tilePreviewAudio.Stop(); tilePreviewAudio.Close(); } catch (Exception) { } tilePreviewAudio = null; }
            if (tilePreviewTimer != null) { tilePreviewTimer.Stop(); tilePreviewTimer = null; }
            var item = tilePreviewItem;
            tilePreviewItem = null;
            if (item != null && item.IsPreviewing) { item.IsPreviewing = false; item.PreviewVideo = null; }
            if (tilePreviewPlaying) { tilePreviewPlaying = false; UpdateMusicState(); }
        }

        // =====================================================================
        // Wishlist alert: one check, 20 seconds after Playnite starts, for a cheaper wishlist game (green dot on the Store icon)
        // =====================================================================

        private void ScheduleWishlistCheck()
        {
            if (settings.StoreWishlist == null || settings.StoreWishlist.Count == 0) return;
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(20) };
            t.Tick += (s, e) => { t.Stop(); CheckWishlistOnce(); CheckWishReleases(); };   // (0.85: and "Out today")
            t.Start();
        }

        private void CheckWishlistOnce()
        {
            if (!HasItadKey) return;
            var wishes = settings.StoreWishlist.Where(w => !string.IsNullOrEmpty(w.ItadId) && (w.AddedPrice > 0 || w.TargetPrice > 0)).ToList();
            if (wishes.Count == 0) return;
            var ids = wishes.Select(w => w.ItadId).Distinct().ToList();
            KippixShared.SafeWork.Queue(_ =>
            {
                int cheaper = 0;
                try
                {
                    var prices = ItadPrices(ids, ItadShops);
                    foreach (var w in wishes)
                    {
                        Tuple<List<ItadDeal>, ItadDeal> p;
                        if (string.IsNullOrEmpty(w.ItadId) || !prices.TryGetValue(w.ItadId, out p) || p.Item1.Count == 0) continue;
                        var best = p.Item1.OrderBy(d => d.Price).First();
                        bool good;
                        WishNote(w, best, out good);   // alert price reached (or, without one, cheaper than when added)
                        if (good) cheaper++;
                    }
                }
                catch (Exception ex) { logger.Warn("Kippix: wishlist check: " + ex.Message); return; }
                OnUi(() => { if (cheaper > 0 && openPanel != "store") SetStoreDot(true); });
            });
        }

        private void SetStoreDot(bool on)
        {
            if (Shortcuts == null) return;
            var s = Shortcuts.FirstOrDefault(x => x.Key == "store");
            if (s == null) return;
            s.DotBrush = GreenBrush;
            s.ShowDot = on;
        }
    }
}

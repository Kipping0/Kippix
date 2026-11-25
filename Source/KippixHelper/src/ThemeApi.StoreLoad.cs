using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Store, v0.25: a game's page loads in three parts at once (details, reviews, prices) and shows each as soon as it's
    /// there, instead of one long chain of requests. Pages you've opened recently come back straight away from a cache.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>What every part may need, each looked up once (whichever part asks first) and shared.</summary>
        private class GameLookup
        {
            public StoreItem Item, SteamItem, GogFromList;
            public Lazy<ItadGameInfo> InfoFromItem;   // only when the tile came from IsThereAnyDeal
            public Lazy<string> SteamId;
            public Lazy<StoreDetails> Steam;
            public Lazy<StoreItem> Gog;
            public Lazy<string> ItadId;
            public Lazy<ItadGameInfo> Info;
        }

        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch (Exception) { return null; }
        }

        private GameLookup NewLookup(StoreItem item)
        {
            string key = NameKey(item.Name);
            var all = item.Merged ?? new List<StoreItem> { item };
            var L = new GameLookup { Item = item, SteamItem = all.FirstOrDefault(i => i.Store == "Steam"), GogFromList = all.FirstOrDefault(i => i.Store == "GOG") };
            L.InfoFromItem = new Lazy<ItadGameInfo>(() => string.IsNullOrEmpty(item.ItadId) ? null : Safe(() => ItadInfo(item.ItadId)));
            L.SteamId = new Lazy<string>(() =>
            {
                string id = item.SteamAppId ?? (L.SteamItem != null ? L.SteamItem.Id : null);
                if (id == null && L.InfoFromItem.Value != null) id = L.InfoFromItem.Value.SteamAppId;
                if (id == null)   // GOG / Epic games too: Steam's reviews, Deck and controller info when it's there as well
                {
                    var hit = Safe(() => SteamBrowse(new StoreQuery { Text = item.Name }, 0).Items.FirstOrDefault(i => NameKey(i.Name) == key));
                    if (hit != null) { id = hit.Id; if (L.SteamItem == null) L.SteamItem = hit; }
                }
                return id;
            });
            L.Steam = new Lazy<StoreDetails>(() => L.SteamId.Value == null ? null : Safe(() => SteamDetails(L.SteamId.Value)));
            L.Gog = new Lazy<StoreItem>(() => L.GogFromList ?? Safe(() => GogBrowse(new StoreQuery { Text = item.Name }, 0).Items.FirstOrDefault(i => NameKey(i.Name) == key)));
            L.ItadId = new Lazy<string>(() => !HasItadKey ? null : item.ItadId ?? Safe(() => ItadLookup(L.SteamId.Value, item.Name)));
            L.Info = new Lazy<ItadGameInfo>(() => !string.IsNullOrEmpty(item.ItadId) ? L.InfoFromItem.Value
                                                  : L.ItadId.Value != null ? Safe(() => ItadInfo(L.ItadId.Value)) : null);
            return L;
        }

        // badges come from different parts; kept apart and shown together in one order
        private List<StoreBadge> badgesDetails = new List<StoreBadge>(), badgesDeck = new List<StoreBadge>(), badgesSubs = new List<StoreBadge>();

        private void RenderBadges()
        {
            StoreBadges.Clear();
            if (StoreGameOwned) StoreBadges.Add(new StoreBadge { Text = OwnedBadgeText(storeGame), IsGood = true, IsOwned = true });
            foreach (var b in badgesDetails.Concat(badgesDeck).Concat(badgesSubs)) StoreBadges.Add(b);
            Notify("HasStoreBadges");
        }

        private int storeGameParts;   // parts of the open game's page still loading

        private void LoadStoreGame(StoreItem item, int request, int gen)
        {
            var L = NewLookup(item);
            storeGameParts = 3;
            bool failed = false;
            Func<bool> current = () => request == storeRequest && gen == storeGameGen;
            Action<bool> partDone = ok =>
            {
                if (!current()) return;
                if (!ok) failed = true;
                if (--storeGameParts == 0 && !failed) CacheGamePage(item);   // a page that didn't load properly isn't kept
            };
            KippixShared.SafeWork.Queue(_ => LoadDetailsPart(L, gen, current, partDone));
            KippixShared.SafeWork.Queue(_ => LoadReviewsPart(L, gen, current, partDone));
            KippixShared.SafeWork.Queue(_ => LoadPricesPart(L, gen, current, partDone));
        }

        // ---------------- part 1: description, pictures, trailers, requirements, add-ons, editions ----------------

        private void LoadDetailsPart(GameLookup L, int gen, Func<bool> current, Action<bool> done)
        {
            var item = L.Item;
            var page = new StoreGamePage();
            StoreDetails steam = null, gog = null;
            bool detailsOk = false;
            try
            {
                if (item.Store == "GOG" && L.Gog.Value != null) gog = Safe(() => GogDetails(L.Gog.Value.Id));
                steam = L.Steam.Value;
                if (gog == null && steam == null && L.Gog.Value != null) gog = Safe(() => GogDetails(L.Gog.Value.Id));
                var main = item.Store == "GOG" && gog != null ? gog : steam ?? gog;
                if (main != null)
                {
                    page.Description = main.Description;
                    if (!string.IsNullOrEmpty(main.Facts)) page.Facts = main.Facts;
                    if (!string.IsNullOrEmpty(main.Header))
                    {
                        // Steam's header is small (460 px): its wide key art and bigger capsule live in the same folder
                        if (main == steam && main.Header.Contains("/header.jpg"))
                        {
                            page.Headers.Add(main.Header.Replace("/header.jpg", "/library_hero.jpg"));
                            page.Headers.Add(main.Header.Replace("/header.jpg", "/capsule_616x353.jpg"));
                        }
                        page.Headers.Add(main.Header);
                    }
                    page.Screens.AddRange(main.Screens);
                    page.Videos.AddRange(main.Videos);
                    if (main == gog && steam != null && page.Videos.Count == 0) page.Videos.AddRange(steam.Videos);
                }
                else
                {
                    var info = L.Info.Value;
                    if (info != null) { page.Facts = info.Facts; page.Headers.AddRange(info.Pictures); }
                }
                detailsOk = main != null || L.Info.Value != null;
                page.SteamAppId = L.SteamId.Value;
                page.WebLink = item.Link ?? (page.SteamAppId != null ? "https://store.steampowered.com/app/" + page.SteamAppId + "/" : L.Gog.IsValueCreated && L.Gog.Value != null ? L.Gog.Value.Link : null);
                if (steam != null)
                {
                    page.ReqMin = steam.ReqMinimum;
                    page.ReqRec = steam.ReqRecommended;
                    page.DlcCount = steam.DlcCount;
                    page.Editions = steam.Editions;
                    if (!string.IsNullOrEmpty(steam.AgeRating)) page.Badges.Add(new StoreBadge { Text = steam.AgeRating });
                    if (steam.Controller == "full") page.Badges.Add(new StoreBadge { Text = Loc("LOC_KX_BadgeController", "Full controller support"), IsGood = true });
                    else if (steam.Controller == "partial") page.Badges.Add(new StoreBadge { Text = Loc("LOC_KX_BadgeControllerPartial", "Partial controller support") });
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: store details failed: " + ex.Message); }
            // no requirements of its own (an edition or bundle sold as a package, e.g. "Royal Edition", "Trilogy"): the
            // requirements of the game(s) in it, the most demanding one. Looked up after the page shows (not holding it up).
            bool needFallback = string.IsNullOrEmpty(page.ReqMin) && string.IsNullOrEmpty(page.ReqRec);
            if (needFallback)
                KippixShared.SafeWork.Queue(_ =>
                {
                    var fb = Safe(() => SteamReqFallback(item.Name));
                    if (fb == null) return;
                    page.ReqMin = fb.Item1; page.ReqRec = fb.Item2; page.ReqFrom = fb.Item3;
                    OnUi(() =>
                    {
                        if (!current()) return;
                        storeReqMin = page.ReqMin;
                        storeReqRec = page.ReqRec;
                        storeReqFrom = page.ReqFrom;
                        Notify("HasStoreRequirements");
                        if (storeGameParts == 0 && CachedGamePage(item) != null) CacheGamePage(item);   // the cached page gets them too
                    });
                });
            OnUi(() =>
            {
                if (!current()) return;
                StoreGameDescription = page.Description ?? item.Description ?? "";
                if (!string.IsNullOrEmpty(page.Facts)) StoreGameFacts = page.Facts;
                if (!string.IsNullOrEmpty(page.WebLink)) storeWebLink = page.WebLink;
                BuildStoreMedia(page, gen);
                storeReqMin = page.ReqMin;
                storeReqRec = page.ReqRec;
                storeReqFrom = page.ReqFrom;
                storeReviewAppId = page.SteamAppId;
                storeDlcCount = page.DlcCount;
                StoreEditions.Clear();
                if (page.Editions != null && page.Editions.Count > 1) foreach (var ed in page.Editions) StoreEditions.Add(ed);
                badgesDetails = page.Badges;
                RenderBadges();
                Notify("HasStoreRequirements");
                if (page.Headers.Count > 0)
                    EnqueueImage(page.Headers, PxImg(860), 0, img =>
                    {
                        if (img == null) return;
                        UpdateCachedHero(item, img);   // the cached page gets the big picture too
                        if (gen == storeGameGen) { StoreHeroImage = img; Notify("StoreHeroImage"); }
                    });
                PreloadViewerImages(0, 2);
                NotifyStore();
                if (settings.StoreTrailers == 2) AutoplayStoreTrailer(gen);
                done(detailsOk);
            });
        }

        // ---------------- part 2: reviews, scores, Steam Deck ----------------

        private void LoadReviewsPart(GameLookup L, int gen, Func<bool> current, Action<bool> done)
        {
            var item = L.Item;
            StoreReviews reviews = null;
            string scores = "";
            var deckBadges = new List<StoreBadge>();
            bool reviewsOk = true;
            bool reviewsShown = false;
            try
            {
                // (0.99.26) Steam's reviews first, whichever store the game was opened from; then GOG's (Epic has star ratings only);
                // no store reviews at all → the critics' scores (Metacritic / OpenCritic)
                string steamId = L.SteamId.Value;
                Thread deck = null;
                int deckValue = 0;
                if (steamId != null)
                {
                    deck = new Thread(() => { try { deckValue = SteamDeck(steamId); } catch (Exception) { } }) { IsBackground = true };
                    deck.Start();
                }
                if (reviews == null && steamId != null) { var st = SteamReviewsSure(steamId, item.Name); if (HasReviews(st)) reviews = st; }
                else if (reviews == null) logger.Info("Kippix: store reviews: no Steam game found for \"" + item.Name + "\" (critic scores / GOG instead)");
                if (reviews == null && L.Gog.Value != null) { var g = Safe(() => GogReviews(L.Gog.Value.Id, "all", "*", 8)); if (HasReviews(g)) reviews = g; }
                if (reviews == null) logger.Info("Kippix: store reviews: none from Steam or GOG for \"" + item.Name + "\"" + (L.Gog.Value == null ? " (not on GOG)" : ""));
                var info = !string.IsNullOrEmpty(item.ItadId) || reviews == null ? L.Info.Value : null;
                if (info != null && !string.IsNullOrEmpty(info.Scores)) scores = info.Scores;
                else
                {
                    var steam = steamId != null ? L.Steam.Value : null;
                    if (steam != null && !string.IsNullOrEmpty(steam.Metacritic)) scores = "Metascore " + steam.Metacritic;
                }
                if (reviews == null && scores.Length > 0) reviews = new StoreReviews { Source = "critics", Title = Loc("LOC_KX_CriticScores", "Critic scores") };
                // (0.99.27) the reviews show as soon as they're here; the Steam Deck badge follows when it comes
                var shown = reviews;
                string shownScores = scores;
                OnUi(() => { if (current()) ShowStoreReviews(shown, shownScores); });
                reviewsShown = true;
                if (deck != null) deck.Join(15000);
                if (deckValue == 3) deckBadges.Add(new StoreBadge { Text = Loc("LOC_KX_BadgeDeckVerified", "Steam Deck Verified"), IsGood = true });
                else if (deckValue == 2) deckBadges.Add(new StoreBadge { Text = Loc("LOC_KX_BadgeDeckPlayable", "Steam Deck Playable") });
                else if (deckValue == 1) deckBadges.Add(new StoreBadge { Text = Loc("LOC_KX_BadgeDeckUnsupported", "Not for Steam Deck") });
            }
            catch (Exception ex) { reviewsOk = false; logger.Warn("Kippix: store reviews failed: " + ex.Message); }
            OnUi(() =>
            {
                if (!current()) return;
                if (!reviewsShown) ShowStoreReviews(reviews, scores);
                badgesDeck = deckBadges;
                RenderBadges();
                NotifyStore();
                done(reviewsOk);
            });
        }

        private void ShowStoreReviews(StoreReviews reviews, string scores)
        {
            StoreReviewTitle = reviews != null ? reviews.Title ?? "" : "";
            StoreReviewLine = reviews != null ? reviews.Line ?? "" : "";
            StoreScores = scores ?? "";
            StoreReviews.Clear();
            if (reviews != null) foreach (var r in reviews.Items) { var rv = r; rv.OpenCommand = new RelayCommand(o => OpenReviewReader(rv)); StoreReviews.Add(rv); }
            storeReviewCursor = reviews != null ? reviews.Cursor : null;
            storeReviewSource = reviews != null ? reviews.Source : null;
            storeReviewSourceId = reviews != null ? reviews.SourceId : null;
            ResetReviewRotation();
            NotifyStore();
        }

        // ---------------- part 3: prices, lowest / highest ever, subscriptions ----------------

        private void LoadPricesPart(GameLookup L, int gen, Func<bool> current, Action<bool> done)
        {
            var item = L.Item;
            var page = new StoreGamePage();
            var subs = new List<StoreBadge>();
            try
            {
                string itadId = L.ItadId.Value;
                page.ItadId = itadId;
                if (itadId != null)
                {
                    // the four IsThereAnyDeal requests side by side
                    Tuple<List<ItadDeal>, ItadDeal> prices = null;
                    string lowest = null;
                    List<PricePoint> history = null;
                    List<string> subNames = null;
                    var jobs = new List<Thread>
                    {
                        new Thread(() => { try { ItadPrices(new[] { itadId }, ItadShops).TryGetValue(itadId, out prices); } catch (Exception ex) { logger.Warn("Kippix: IsThereAnyDeal failed: " + ex.Message); } }),
                        new Thread(() => { try { lowest = ItadStoreLow(itadId); } catch (Exception) { } }),
                        new Thread(() => { try { history = ItadHistory(itadId); } catch (Exception) { } }),
                        new Thread(() => { try { subNames = ItadSubs(itadId); } catch (Exception) { } }),
                    };
                    foreach (var t in jobs) { t.IsBackground = true; t.Start(); }
                    var deadline = DateTime.Now.AddSeconds(20);
                    foreach (var t in jobs) t.Join(TimeSpan.FromMilliseconds(Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds)));
                    page.History = history;
                    if (subNames != null) foreach (var sub in subNames) subs.Add(new StoreBadge { Text = string.Format(Loc("LOC_KX_BadgeSub", "Included with {0}"), sub), IsGood = true });
                    if (prices != null && prices.Item1.Count > 0)
                    {
                        string steamApp = L.SteamId.Value;
                        foreach (var d in prices.Item1.OrderBy(x => x.Price)) page.Prices.Add(ItadPriceRow(d, item.Name, steamApp));
                        page.Lowest = lowest;
                        page.Source = Loc("LOC_KX_StoreFromItad", "Prices from IsThereAnyDeal");
                    }
                }
                if (page.Prices.Count == 0)
                {
                    // without IsThereAnyDeal (or if it found nothing): the stores themselves, Epic and the lowest ever from CheapShark
                    var steam = L.Steam.Value;
                    if (steam != null && steam.Price != null) page.Prices.Add(steam.Price);
                    else if (L.SteamItem != null) page.Prices.Add(PriceRow("Steam", L.SteamItem));
                    if (L.Gog.Value != null) page.Prices.Add(PriceRow("GOG", L.Gog.Value));
                    if (item.Store == "Epic") page.Prices.Add(PriceRow("Epic Games", item));
                    try
                    {
                        var cs = CheapShark(item.Name);
                        if (cs != null)
                        {
                            if (item.Store != "Epic" && cs.Item1 != null) page.Prices.Add(cs.Item1);
                            page.Lowest = cs.Item2;
                        }
                    }
                    catch (Exception) { }
                    page.Source = Loc("LOC_KX_StoreFromStores", "Prices from the stores · Epic and lowest ever from CheapShark (US$)");
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: store prices failed: " + ex.Message); }
            OnUi(() =>
            {
                if (!current()) return;
                foreach (var p in page.Prices) p.IsBest = false;   // (a remembered Steam price can carry it from last time)
                var mine = page.Prices.Where(p => p.Amount >= 0 && p.Currency == CurrencyCode).OrderBy(p => p.Amount).FirstOrDefault();
                if (mine != null && page.Prices.Count(p => p.Currency == CurrencyCode && p.Amount >= 0) > 1) mine.IsBest = true;
                StorePrices.Clear();
                foreach (var p in page.Prices) StorePrices.Add(p);
                StoreLowestText = page.Lowest ?? "";
                StorePriceSource = page.Source ?? "";
                storeGameItadId = page.ItadId;
                SetPriceHistory(page.History);
                badgesSubs = subs;
                RenderBadges();
                UpdateWishState();
                StorePricesLoading = false;
                NotifyStore();
                // move to the first price only if you're still on this page and haven't moved yet (not in the browser or viewer)
                var firstPrice = StorePrices.Count > 0 ? StorePrices[0] : null;
                ui.BeginInvoke(new Action(() =>
                {
                    if (firstPrice == null || openPanel != "store" || storePage != "game" || gen != storeGameGen) return;
                    if (storeViewerOpen || storeReaderOpen || storePickerOpen || !storeGameFocusUntouched) return;
                    UiTools.FocusItem("KX_StorePriceList", firstPrice);
                }), DispatcherPriority.ContextIdle);
                done(page.Prices.Count > 0);
            });
        }

        /// <summary>True until you move on the game's page (so a part arriving late doesn't pull the highlight away).</summary>
        private bool storeGameFocusUntouched = true;

        private StorePrice ItadPriceRow(ItadDeal d, string title, string steamAppId)
        {
            string link = ShopLink(d.Url, d.Shop, title, d.Shop == "Steam" ? steamAppId : null);   // the store's own page, not a referral redirect
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(d.Voucher)) notes.Add(string.Format(Loc("LOC_KX_StoreVoucher", "with code {0}"), d.Voucher));
            if (d.Cut > 0 && d.Expiry.HasValue) notes.Add(SaleEndsText(d.Expiry.Value));
            return new StorePrice
            {
                StoreName = ShopName(d.Shop),
                PriceText = d.Price == 0 ? Loc("LOC_KX_StoreFree", "Free") : Money(d.Price, d.Currency),
                OldPriceText = d.Cut > 0 ? Money(d.Regular, d.Currency) : null,
                DiscountText = d.Cut > 0 ? "-" + d.Cut + "%" : null,
                Note = notes.Count > 0 ? string.Join(" · ", notes.ToArray()) : null,
                Amount = d.Price, Currency = d.Currency,
                OpenCommand = new RelayCommand(_ => OpenStoreLink(link, null))
            };
        }

        /// <summary>"Sale ends today / tomorrow / in 3 days / on 2 Nov".</summary>
        private string SaleEndsText(DateTime endsUtc)
        {
            var local = endsUtc.ToLocalTime();
            int days = (local.Date - DateTime.Now.Date).Days;
            if (days <= 0) return Loc("LOC_KX_SaleEndsToday", "Sale ends today");
            if (days == 1) return Loc("LOC_KX_SaleEndsTomorrow", "Sale ends tomorrow");
            if (days <= 14) return string.Format(Loc("LOC_KX_SaleEndsDays", "Sale ends in {0} days"), days);
            return string.Format(Loc("LOC_KX_SaleEndsOn", "Sale ends {0}"), local.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture));
        }

        // ---------------- recently opened pages, kept for 10 minutes ----------------

        private readonly Dictionary<string, Tuple<DateTime, GamePageSnapshot>> gamePageCache = new Dictionary<string, Tuple<DateTime, GamePageSnapshot>>();

        private string GameCacheKey(StoreItem item)
        {
            return (item.Store ?? "") + "|" + (item.Id ?? "") + "|" + (item.ItadId ?? "") + "|" + (item.SteamAppId ?? "") + "|" + Country + "|" + HasItadKey + "|" + settings.StoreTrailers;
        }

        private void UpdateCachedHero(StoreItem item, System.Windows.Media.ImageSource img)
        {
            Tuple<DateTime, GamePageSnapshot> hit;
            if (gamePageCache.TryGetValue(GameCacheKey(item), out hit)) hit.Item2.Hero = img;
            if (dlcPage != null && ReferenceEquals(dlcPage.Game, item)) dlcPage.Hero = img;
        }

        private void CacheGamePage(StoreItem item)
        {
            gamePageCache[GameCacheKey(item)] = Tuple.Create(DateTime.Now, SaveGamePage());
            if (gamePageCache.Count > 20)
                foreach (var old in gamePageCache.OrderBy(x => x.Value.Item1).Take(gamePageCache.Count - 20).Select(x => x.Key).ToList()) gamePageCache.Remove(old);
        }

        private GamePageSnapshot CachedGamePage(StoreItem item)
        {
            Tuple<DateTime, GamePageSnapshot> hit;
            if (!gamePageCache.TryGetValue(GameCacheKey(item), out hit)) return null;
            if (DateTime.Now - hit.Item1 > TimeSpan.FromMinutes(10)) { gamePageCache.Remove(GameCacheKey(item)); return null; }
            return hit.Item2;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Store, v0.25: X on a tile for the wishlist, price alerts, "More like this", release dates on Coming soon,
    /// LT / RT a screen at a time, clearing Recently viewed.
    /// </summary>
    public partial class ThemeApi
    {
        public ICommand StoreSimilarCommand { get; private set; }
        public ICommand StoreAlertCommand { get; private set; }
        public ICommand StoreClearRecentCommand { get; private set; }

        private void InitStoreQol()
        {
            StoreSimilarCommand = new RelayCommand(_ => OpenSimilarList());
            StoreAlertCommand = new RelayCommand(_ => PickAlert());
            StoreClearRecentCommand = new RelayCommand(_ => ClearRecent());
        }

        private static bool IsSubList(string id) { return id == "dlc" || id == "similar"; }

        // =====================================================================
        // More like this (Steam's similar games), a list of its own like the add-ons
        // =====================================================================

        public bool HasStoreSimilar { get { return !string.IsNullOrEmpty(storeReviewAppId) && !IsSubList(storeId); } }

        private void OpenSimilarList()
        {
            if (storeGame == null || string.IsNullOrEmpty(storeReviewAppId) || IsSubList(storeId)) return;
            OpenSubList("similar");
        }

        private StoreResults SimilarPage(string appId, StoreQuery q)
        {
            string html;
            using (var web = StoreWeb())
                html = web.DownloadString("https://store.steampowered.com/recommended/morelike/app/" + Uri.EscapeDataString(appId) + "/?l=english&cc=" + Country);
            // only the similar games (the page also has new releases / top sellers further down): their own section,
            // else the run of similar_grid_item blocks; neither → nothing (rather than unrelated games)
            string part = null;
            int start = html.IndexOf("id=\"released\"", StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = html.Length;
                foreach (var marker in new[] { "id=\"newreleases\"", "id=\"topsellers\"", "id=\"freeplay\"", "id=\"upcoming\"" })
                {
                    int i = html.IndexOf(marker, start + 10, StringComparison.Ordinal);
                    if (i > 0 && i < end) end = i;
                }
                part = html.Substring(start, end - start);
            }
            else
            {
                int first = html.IndexOf("similar_grid_item", StringComparison.Ordinal);
                int last = html.LastIndexOf("similar_grid_item", StringComparison.Ordinal);
                if (first >= 0) part = html.Substring(first, Math.Min(html.Length, last + 3000) - first);
            }
            if (part == null) return new StoreResults { Items = new List<StoreItem>(), Total = 0 };
            var ids = new List<string>();
            var slugs = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(part, "(?:data-ds-appid=\"(\\d+)\")|(?:/app/(\\d+)/([^/?\"'#]*))"))
            {
                bool viaData = m.Groups[1].Success;
                string id = viaData ? m.Groups[1].Value : m.Groups[2].Value;
                if (!viaData && !slugs.ContainsKey(id)) slugs[id] = m.Groups[3].Value;
                if (id == appId || ids.Contains(id)) continue;
                ids.Add(id);
            }
            var images = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(part, "(https?://[^\"'\\s]+?/steam/apps/(\\d+)/[^\"'\\s]*?capsule_616x353\\.jpg[^\"'\\s]*)"))
                if (!images.ContainsKey(m.Groups[2].Value)) images[m.Groups[2].Value] = m.Groups[1].Value.Replace("&amp;", "&");
            ids = ids.Take(30).ToList();
            var named = Safe(() => SteamItemsById(ids)) ?? new Dictionary<string, StoreItem>();
            var list = new List<StoreItem>();
            foreach (var id in ids)
            {
                StoreItem item;
                if (!named.TryGetValue(id, out item))
                {
                    string slug;
                    string name = slugs.TryGetValue(id, out slug) ? slug.Replace('_', ' ').Trim() : "";
                    if (name.Length == 0) continue;
                    item = SteamTile(id, name, "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/" + id + "/header.jpg", 0, 0, 0, CurrencyCode);
                }
                string img;
                if (images.TryGetValue(id, out img))
                {
                    // the page's own (hashed) picture first; the item's in case it's missing
                    var rest = new List<string>();
                    if (!string.IsNullOrEmpty(item.Image) && item.Image != img) rest.Add(item.Image);
                    if (item.ImageFallbacks != null) rest.AddRange(item.ImageFallbacks);
                    item.Image = img;
                    item.ImageFallbacks = rest;
                }
                list.Add(item);
            }
            string free = Loc("LOC_KX_StoreFree", "Free");
            if (q.Sort == "pricelow") list = list.OrderBy(i => i.PriceText == free ? 0 : ParseAmount(i.PriceText) < 0 ? decimal.MaxValue : ParseAmount(i.PriceText)).ToList();
            else if (q.Sort == "name") list = list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new StoreResults { Items = list, Total = list.Count };
        }

        /// <summary>Names and prices for a set of Steam app ids at once (Steam's store browse service, no key needed).</summary>
        private Dictionary<string, StoreItem> SteamItemsById(List<string> ids)
        {
            var result = new Dictionary<string, StoreItem>();
            if (ids.Count == 0) return result;
            string json = "{\"ids\":[" + string.Join(",", ids.Select(i => "{\"appid\":" + i + "}").ToArray()) + "],"
                        + "\"context\":{\"language\":\"english\",\"country_code\":\"" + Country + "\",\"steam_realm\":1},"
                        + "\"data_request\":{\"include_assets\":true}}";
            var root = StoreJson("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(json)) as Dictionary<string, object>;
            var items = Dig(root, "response", "store_items") as System.Collections.IEnumerable;
            if (items == null) return result;
            foreach (var si in items.Cast<object>().OfType<Dictionary<string, object>>())
            {
                string id = FStr(si, "appid");
                if (id.Length == 0) id = FStr(si, "id");
                string name = FStr(si, "name");
                if (id.Length == 0 || name.Length == 0) continue;
                string plain = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/" + id + "/header.jpg";
                var item = SteamTile(id, name, plain, 0, 0, 0, CurrencyCode);
                // newer games' pictures sit in a hashed folder: Steam gives the format ("steam/apps/123/abc/${FILENAME}?t=...")
                var assets = Dig(si, "assets") as Dictionary<string, object>;
                string format = assets != null ? FStr(assets, "asset_url_format") : "";
                string file = assets != null ? (FStr(assets, "main_capsule").Length > 0 ? FStr(assets, "main_capsule") : FStr(assets, "header")) : "";
                if (format.Length > 0 && file.Length > 0)
                {
                    item.Image = "https://shared.fastly.steamstatic.com/store_item_assets/" + format.Replace("${FILENAME}", file);
                    item.ImageFallbacks = new List<string> { plain };
                }
                var buy = Dig(si, "best_purchase_option") as Dictionary<string, object>;
                bool isFree = si.ContainsKey("is_free") && si["is_free"] is bool && (bool)si["is_free"];
                if (buy != null && FStr(buy, "formatted_final_price").Length > 0)
                {
                    item.PriceText = FStr(buy, "formatted_final_price");
                    long cut = FNum(buy, "discount_pct");
                    if (cut > 0)
                    {
                        item.DiscountText = "-" + cut + "%";
                        string old = FStr(buy, "formatted_original_price");
                        if (old.Length > 0) item.OldPriceText = old;
                    }
                }
                else if (isFree) item.PriceText = Loc("LOC_KX_StoreFree", "Free");
                result[id] = item;
            }
            return result;
        }

        // =====================================================================
        // X on a game tile: add to / take off the wishlist without opening the game
        // =====================================================================

        public bool StoreShowWishHint { get { return HasItadKey && (storePage == "home" || storePage == "list"); } }

        private WishItem FindWish(StoreItem item)
        {
            if (item == null || settings.StoreWishlist == null) return null;
            string key = NameKey(item.Name);
            return settings.StoreWishlist.FirstOrDefault(w => (!string.IsNullOrEmpty(item.ItadId) && w.ItadId == item.ItadId) || NameKey(w.Name) == key);
        }

        private void MarkWished(StoreItem item)
        {
            if (item != null) item.IsWished = FindWish(item) != null;
        }

        /// <summary>X / Y-less controllers: the key handler calls this; true when it was a game tile.</summary>
        private bool WishFromTile()
        {
            if (openPanel != "store" || (storePage != "home" && storePage != "list") || storePickerOpen || storeReaderOpen || storeViewerOpen || !HasItadKey) return false;
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var item = fe != null ? fe.DataContext as StoreItem : null;
            if (item == null) return false;
            ToggleTileWish(item);
            return true;
        }

        private bool InWishRow(StoreItem item)
        {
            var row = StoreRows != null ? StoreRows.FirstOrDefault(r => r.Key == "wish") : null;
            return row != null && row.Items.Contains(item);
        }

        private void ToggleTileWish(StoreItem item)
        {
            var existing = FindWish(item);
            if (existing != null)
            {
                PlaySound("back");
                WishRemovedHere(existing);
                settings.StoreWishlist.Remove(existing);
                SaveSettings();
                item.IsWished = false;
                if (storePage == "list" && storeId == "wishlist") RemoveFromWishlistPage(item);
                else if (!InWishRow(item)) BuildWishRow();   // in the wishlist row itself the tile stays until next time (keeps the highlight)
                Notify("StoreWishButtonText");
                return;
            }
            PlaySound("select");
            item.IsWished = true;   // shown straight away; taken back if IsThereAnyDeal doesn't know the game
            string steamId = PreviewAppId(item);
            KippixShared.SafeWork.Queue(_ =>
            {
                string itad = item.ItadId ?? Safe(() => ItadLookup(steamId, item.Name));
                OnUi(() =>
                {
                    if (itad == null) { item.IsWished = false; return; }
                    if (FindWish(item) != null) return;
                    string free = Loc("LOC_KX_StoreFree", "Free");
                    decimal amount = item.PriceText == free ? 0 : string.IsNullOrEmpty(item.PriceText) ? -1 : ParseAmount(item.PriceText);
                    settings.StoreWishlist.Add(new WishItem
                    {
                        ItadId = itad, SteamAppId = steamId, Name = item.Name, Image = item.Image, Link = item.Link,
                        AddedPrice = amount, Currency = CurrencyCode, Added = DateTime.Now
                    });
                    WishedHere(steamId, item.Name);
                    SaveSettings();
                    if (!InWishRow(item)) BuildWishRow();   // re-added from the wishlist row itself: the tile is still there
                    RefreshWishPrices();
                    Notify("StoreWishButtonText");
                });
            });
        }

        private void RemoveFromWishlistPage(StoreItem item)
        {
            int at = StoreItems.IndexOf(item);
            if (at < 0) return;
            StoreItems.RemoveAt(at);
            if (storeTotal > 0) storeTotal--;
            UpdateSectionTitle();
            if (StoreItems.Count == 0) { SetStoreStatus(Loc("LOC_KX_WishEmpty", "Your wishlist is empty. Add games with the heart on a game's page.")); FocusPanelSoon(); return; }
            var next = StoreItems[Math.Min(at, StoreItems.Count - 1)];
            ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_StoreList", next)), DispatcherPriority.ContextIdle);
        }

        // =====================================================================
        // Price alerts on wishlist games ("tell me under £20"); the one check at start uses them
        // =====================================================================

        private WishItem CurrentWish()
        {
            return string.IsNullOrEmpty(storeGameItadId) ? null : PageWish();
        }

        public string StoreAlertText
        {
            get
            {
                var w = CurrentWish();
                return w != null && w.TargetPrice > 0
                    ? string.Format(Loc("LOC_KX_AlertUnder", "Alert under {0}"), Money(w.TargetPrice, w.TargetCurrency ?? CurrencyCode))
                    : Loc("LOC_KX_AlertSet", "Price alert");
            }
        }

        private void PickAlert()
        {
            var w = CurrentWish();
            if (w == null) return;
            var best = StorePrices.Where(p => p.Amount >= 0 && p.Currency == CurrencyCode).OrderBy(p => p.Amount).FirstOrDefault();
            decimal now = best != null ? best.Amount : -1;
            var options = new List<Tuple<string, string>> { Tuple.Create("", Loc("LOC_KX_AlertAnyDrop", "Any price drop")) };
            foreach (int n in new[] { 5, 10, 15, 20, 25, 30, 40, 50, 60, 80 })
            {
                decimal v = n * PriceScale;
                if (now >= 0 && v >= now) continue;
                options.Add(Tuple.Create(v.ToString(CultureInfo.InvariantCulture), string.Format(Loc("LOC_KX_AlertUnderOption", "Under {0}"), Money(v, CurrencyCode))));
            }
            string current = w.TargetPrice > 0 ? w.TargetPrice.ToString(CultureInfo.InvariantCulture) : "";
            var back = Keyboard.FocusedElement as UIElement;
            OpenStorePicker(string.Format(Loc("LOC_KX_AlertTitle", "Price alert: {0}"), w.Name), options, current, key =>
            {
                decimal v;
                if (decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v > 0) { w.TargetPrice = v; w.TargetCurrency = CurrencyCode; }
                else { w.TargetPrice = -1; w.TargetCurrency = null; }
                SaveSettings();
                Notify("StoreAlertText");
                ui.BeginInvoke(new Action(() => { if (back != null && back.IsVisible) Keyboard.Focus(back); }), DispatcherPriority.ContextIdle);
            });
        }

        /// <summary>A wishlist game's line under its tile, and whether it's good news (green, counts for the top bar dot).</summary>
        private string WishNote(WishItem w, ItadDeal best, out bool good)
        {
            if (w.TargetPrice > 0)
            {
                string cur = w.TargetCurrency ?? w.Currency;
                good = best.Currency == cur && best.Price <= w.TargetPrice;
                return string.Format(good ? Loc("LOC_KX_AlertHit", "Under your {0} alert · at {1}") : Loc("LOC_KX_AlertWaiting", "Alert under {0} · at {1}"),
                                     Money(w.TargetPrice, cur), ShopName(best.Shop));
            }
            good = w.AddedPrice > 0 && best.Currency == w.Currency && best.Price < w.AddedPrice;
            return good ? string.Format(Loc("LOC_KX_WishDropped", "Down from {0} when you added it · at {1}"), Money(w.AddedPrice, w.Currency), ShopName(best.Shop))
                        : string.Format(Loc("LOC_KX_StoreAtShop", "at {0}"), ShopName(best.Shop));
        }

        // =====================================================================
        // Coming soon: when it's out
        // =====================================================================

        private static readonly string[] ReleaseFormats = { "d MMM, yyyy", "d MMM yyyy", "MMM d, yyyy", "d MMMM, yyyy", "d MMMM yyyy", "MMMM d, yyyy" };

        /// <summary>"Out today" / "Out tomorrow" / "In 9 days · 27 Oct" / "Out 12 Mar 2027" / Steam's own words ("Q1 2027", "Coming soon").</summary>
        private string ReleaseWhen(string released)
        {
            if (string.IsNullOrWhiteSpace(released)) return "";
            DateTime d;
            if (!DateTime.TryParseExact(released.Trim(), ReleaseFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return released.Trim();
            int days = (d.Date - DateTime.Now.Date).Days;
            if (days < 0) return Loc("LOC_KX_OutNow", "Out now");
            if (days == 0) return Loc("LOC_KX_OutToday", "Out today");
            if (days == 1) return Loc("LOC_KX_OutTomorrow", "Out tomorrow");
            if (days <= 30) return string.Format(Loc("LOC_KX_OutInDays", "In {0} days · {1}"), days, d.ToString("d MMM", CultureInfo.CurrentCulture));
            return string.Format(Loc("LOC_KX_OutOn", "Out {0}"), d.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        }

        // =====================================================================
        // LT / RT: a screen at a time in lists, rows and the picker
        // =====================================================================

        private bool StorePageJump(int dir)
        {
            if (openPanel != "store" || storeReaderOpen || storeViewerOpen) return false;
            var fe = Keyboard.FocusedElement as FrameworkElement;
            if (fe == null) return false;
            if (storePickerOpen)
            {
                var opt = fe.DataContext as PickOption;
                int i = opt != null ? StorePickerItems.IndexOf(opt) : -1;
                if (i < 0) return false;
                int to = Math.Max(0, Math.Min(StorePickerItems.Count - 1, i + dir * 15));   // five rows of three
                if (to != i) { PlaySound("move"); UiTools.FocusItem("KX_StorePickerList", StorePickerItems[to]); }
                return true;
            }
            var item = fe.DataContext as StoreItem;
            if (item == null) return false;
            if (storePage == "list")
            {
                int i = StoreItems.IndexOf(item);
                if (i < 0) return false;
                int to = Math.Max(0, Math.Min(StoreItems.Count - 1, i + dir * 8));   // two rows of four
                if (to != i) { PlaySound("move"); UiTools.FocusItem("KX_StoreList", StoreItems[to]); }
                return true;
            }
            if (storePage == "home" && StoreRows != null)
            {
                var row = StoreRows.FirstOrDefault(r => r.Items.Contains(item));
                if (row == null) return false;
                int i = row.Items.IndexOf(item);
                int to = Math.Max(0, Math.Min(row.Items.Count - 1, i + dir * 5));
                if (to == i) return true;
                var rows = UiTools.FindByName(Application.Current.MainWindow, "KX_StoreRows");
                var target = rows != null ? FindFocusableFor(rows, row.Items[to]) : null;
                if (target != null) { PlaySound("move"); Keyboard.Focus(target); }
                return true;
            }
            return false;
        }

        // =====================================================================
        // Store settings: clear Recently viewed
        // =====================================================================

        public bool HasStoreRecent { get { return settings.StoreRecent != null && settings.StoreRecent.Count > 0; } }
        public string StoreClearRecentText
        {
            get
            {
                int n = settings.StoreRecent != null ? settings.StoreRecent.Count : 0;
                return n > 0 ? string.Format(Loc("LOC_KX_ClearRecentCount", "Clear recently viewed ({0})"), n) : Loc("LOC_KX_ClearRecentEmpty", "Recently viewed is empty");
            }
        }

        private void ClearRecent()
        {
            if (!HasStoreRecent) return;
            PlaySound("select");
            settings.StoreRecent.Clear();
            SaveSettings();
            if (StoreRows != null) BuildRecentRow();
            Notify("HasStoreRecent");
            Notify("StoreClearRecentText");
        }
    }
}

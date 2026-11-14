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

namespace KippixHelper
{
    /// <summary>A card on one of the profile's shelves (an achievement, a wishlisted game).</summary>
    public class HubCard : Observable
    {
        public string Text { get; set; }
        public string Sub { get; set; }
        public bool HasSub { get { return !string.IsNullOrEmpty(Sub); } }
        public string Badge { get; set; }
        public bool HasBadge { get { return !string.IsNullOrEmpty(Badge); } }
        public bool BadgeHot { get; set; }      // a sale (green) / a rare achievement (gold)
        public string Right { get; set; }
        public bool HasRight { get { return !string.IsNullOrEmpty(Right); } }
        public string Key { get; set; }
        public ICommand Command { get; set; }
        internal string ImagePath;
        internal int ImageWidth = 96;
        private ImageSource thumb;
        public ImageSource Thumb { get { return thumb; } set { if (Set(ref thumb, value, "Thumb")) Notify("HasThumb"); } }
        public bool HasThumb { get { return thumb != null; } }
    }

    /// <summary>
    /// (0.99.24) The profile's new look: a card on the left (the last game's art, your picture, name, numbers and the tabs as a
    /// list) and the tab on the right as shelves. The Overview's shelves: Jump back in (covers), Recent captures (the gallery's
    /// latest screenshots and clips), Recent achievements and the Wishlist (on sale first). Lists of rows (most played, friends,
    /// the wishlist, the release calendar…) are cards two to a line under headings. The page's lists keep their names and
    /// items (so the highlight code is the same): on the profile they're only given other templates (SetHubTemplates).
    /// Each shelf can be switched off in Profile tabs (ON YOUR PROFILE).
    /// </summary>
    public partial class ThemeApi
    {
        private readonly ObservableCollection<GalleryItem> hubShots = new ObservableCollection<GalleryItem>();
        private readonly ObservableCollection<HubCard> hubAchs = new ObservableCollection<HubCard>();
        private readonly ObservableCollection<HubCard> hubWish = new ObservableCollection<HubCard>();
        public ObservableCollection<GalleryItem> HubShots { get { return hubShots; } }
        public ObservableCollection<HubCard> HubAchs { get { return hubAchs; } }
        public ObservableCollection<HubCard> HubWish { get { return hubWish; } }
        private bool HubOverviewNow { get { return IsProfileHub && hubTab == "overview"; } }
        public bool HasHubShots { get { return HubOverviewNow && hubShots.Count > 0; } }
        public bool HasHubAchs { get { return HubOverviewNow && hubAchs.Count > 0; } }
        public bool HasHubWish { get { return HubOverviewNow && hubWish.Count > 0; } }
        public string HubWishTitle { get; private set; }
        public string HubShotsTitle { get; private set; }

        /// <summary>LT / RT step the month (Overview) or the year (Your year): shown by the tab's heading. Other tabs' hints (Up next's) plain.</summary>
        public bool HasHubStepper { get { return IsProfileHub && (hubTab == "overview" || hubTab == "year"); } }
        public bool HasHubPlainHint { get { return IsProfileHub && !HasHubStepper; } }

        /// <summary>The tab's name over the right-hand side.</summary>
        public string HubTabTitle { get { return hubTab != null ? HubTabName(hubTab) : ""; } }

        // the page's numbers and the covers' heading: the profile has its own look for them
        public bool HasPageStatsPlain { get { return HasPageStats && !IsProfileHub; } }
        public bool HasHubPageStats { get { return HasPageStats && IsProfileHub; } }
        public bool HasPageTilesTitlePlain { get { return HasPageTilesTitle && !IsProfileHub; } }
        public bool HasHubTilesTitle { get { return HasPageTilesTitle && IsProfileHub; } }

        /// <summary>The pages' edges: the profile uses more of the screen (its card takes the left).</summary>
        public Thickness PagesMargin { get { return IsProfileHub ? new Thickness(64, 44, 64, 24) : new Thickness(120, 60, 120, 28); } }

        private static readonly string[] HubShelfNotes = { "HasHubShots", "HasHubAchs", "HasHubWish", "HubWishTitle", "HubShotsTitle", "HubTabTitle", "HasHubStepper", "HasHubPlainHint",
                                                           "HasPageStatsPlain", "HasHubPageStats", "HasPageTilesTitlePlain", "HasHubTilesTitle", "PagesMargin" };

        private void NotifyHubShelves()
        {
            foreach (string n in HubShelfNotes) Notify(n);
        }

        /// <summary>The page changes / closes: the Overview's shelves go.</summary>
        private void ClearHubShelves()
        {
            if (hubShots.Count > 0) hubShots.Clear();
            if (hubAchs.Count > 0) hubAchs.Clear();
            if (hubWish.Count > 0) hubWish.Clear();
            NotifyHubShelves();
        }

        // =====================================================================
        // Filling the Overview's shelves (on the UI thread, from FillProfilePage)
        // =====================================================================

        private void FillHubShelves(List<RecentAch> lately)
        {
            int gen = pageGen;
            // RECENT ACHIEVEMENTS
            hubAchs.Clear();
            if (HubSectionShown("achs") && lately != null)
                foreach (var a in lately)
                {
                    bool rare = a.Item.Rarity.HasValue && a.Item.Rarity.Value < 10;
                    hubAchs.Add(new HubCard
                    {
                        Text = a.Item.Name, Sub = a.Game + "  ·  " + AgoText(a.Item.Date.Value), Key = "ach:" + a.Game + "|" + a.Item.Name,
                        Badge = a.Item.Rarity.HasValue ? string.Format(CultureInfo.CurrentCulture, "{0:0.#}%", a.Item.Rarity.Value) : "", BadgeHot = rare,
                        ImagePath = string.IsNullOrEmpty(a.Item.Icon) ? null : a.Item.Icon, ImageWidth = 96,
                        Command = new RelayCommand(_ => { ClosePanels(); OpenAchievementsView(); })
                    });
                }
            // WISHLIST: on sale first (when today's prices are known), then the ones added last
            FillHubWishShelf();
            // RECENT CAPTURES: the gallery's newest (it's read once if it hasn't been yet)
            hubShots.Clear();
            HubShotsTitle = Loc("LOC_KX_HubShelfCaptures", "RECENT CAPTURES");
            if (HubSectionShown("captures"))
            {
                if (galleryFiles.Count > 0 && (DateTime.Now - galleryScannedAt).TotalMinutes < 10) FillHubShotShelf();
                else if (!galleryScanning)
                {
                    galleryScanning = true;
                    KippixShared.SafeWork.Queue(_ =>
                    {
                        List<GalleryFile> found = null;
                        try { found = FindGalleryFiles(); } catch (Exception) { }
                        OnUi(() =>
                        {
                            galleryScanning = false;
                            if (found != null) { galleryFiles = found; galleryScannedAt = DateTime.Now; }
                            if (gen == pageGen && HubOverviewNow) { FillHubShotShelf(); NotifyHubShelves(); }
                        });
                    });
                }
            }
            NotifyHubShelves();
            LoadHubCardImages(hubAchs.Concat(hubWish).ToList(), gen);
            HubWishPricesSoon();
        }

        private void FillHubShotShelf()
        {
            hubShots.Clear();
            var newest = galleryFiles.OrderByDescending(f => f.Date).Take(10).ToList();
            foreach (var f in newest)
            {
                var file = f;
                var it = new GalleryItem
                {
                    File = f, Caption = !string.IsNullOrEmpty(f.GameName) ? f.GameName : (f.IsVideo ? Loc("LOC_KX_HubClip", "Clip") : Loc("LOC_KX_HubShot", "Screenshot")),
                    SubText = AgoText(f.Date)
                };
                it.Command = new RelayCommand(_ =>
                {
                    ClosePanels();
                    if (file.GameId != Guid.Empty) OpenGalleryForGame(file.GameId, file.GameName, file.Path); else OpenGallery();
                });
                hubShots.Add(it);
            }
            if (hubShots.Count > 0)
            {
                hubShots.Add(new GalleryItem
                {
                    IsMore = true, MoreText = Loc("LOC_KX_HubOpenGallery", "Open the gallery"),
                    Command = new RelayCommand(_ => { ClosePanels(); OpenGallery(); })
                });
                int month = galleryFiles.Count(f => f.Date.Year == DateTime.Now.Year && f.Date.Month == DateTime.Now.Month);
                HubShotsTitle = Loc("LOC_KX_HubShelfCaptures", "RECENT CAPTURES") + (month > 0 ? "   ·   " + string.Format(Loc("LOC_KX_HubShelfCapturesMonth", "{0} this month"), month.ToString("N0", CultureInfo.CurrentCulture)) : "");
                LoadGalleryThumbs(hubShots.Where(i => i.IsTile).ToList(), galleryGen);
            }
        }

        private void FillHubWishShelf()
        {
            hubWish.Clear();
            HubWishTitle = Loc("LOC_KX_HubShelfWish", "WISHLIST");
            if (!HubSectionShown("wish") || !HubWishlistAllowed || !HubTabShown("wishlist")) return;
            var wishes = settings.StoreWishlist != null ? settings.StoreWishlist.ToList() : new List<WishItem>();
            if (wishes.Count == 0) return;
            var today = DateTime.Today;
            var list = new List<Tuple<HubCard, int, DateTime>>();
            int onSale = 0;
            foreach (var w in wishes)
            {
                var wish = w;
                int cut = 0;
                string price = "";
                Tuple<List<ItadDeal>, ItadDeal> p;
                if (hubWishPrices != null && !string.IsNullOrEmpty(w.ItadId) && hubWishPrices.TryGetValue(w.ItadId, out p) && p.Item1 != null && p.Item1.Count > 0)
                {
                    var best = p.Item1.OrderBy(d => d.Price).First();
                    cut = best.Cut;
                    price = best.Price == 0 ? Loc("LOC_KX_StoreFree", "Free") : Money(best.Price, best.Currency);
                    if (cut > 0) onSale++;
                }
                bool soon = w.ComingSoon || (w.ReleaseDate.HasValue && w.ReleaseDate.Value.Date > today);
                string sub = soon
                    ? (!string.IsNullOrEmpty(w.ReleaseText) ? w.ReleaseText : w.ReleaseDate.HasValue ? w.ReleaseDate.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : Loc("LOC_KX_NoDateYet", "No date yet"))
                    : string.Format(Loc("LOC_KX_WishAdded", "Added {0}"), w.Added.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
                var card = new HubCard
                {
                    Text = w.Name, Sub = sub, Key = "wish:" + (w.SteamAppId ?? w.GogId ?? w.ItadId ?? w.Name),
                    Badge = cut > 0 ? "−" + cut + "%" : soon ? Loc("LOC_KX_HubComing", "Coming") : "", BadgeHot = cut > 0,
                    Right = price, ImagePath = w.Image, ImageWidth = 400,
                    Command = new RelayCommand(_ => OpenStoreFromWidget(WishToStoreItem(wish)))
                };
                list.Add(Tuple.Create(card, cut, w.Added));
            }
            foreach (var t in list.OrderByDescending(x => x.Item2).ThenByDescending(x => x.Item3).Take(10)) hubWish.Add(t.Item1);
            HubWishTitle = onSale > 0 ? Loc("LOC_KX_HubShelfWish", "WISHLIST") + "   ·   " + string.Format(Loc("LOC_KX_HubShelfWishSale", "{0} on sale"), onSale)
                                      : Loc("LOC_KX_HubShelfWish", "WISHLIST");
        }

        /// <summary>Today's wishlist prices for the Overview's shelf too (the same 15 minutes as the Wishlist tab).</summary>
        private void HubWishPricesSoon()
        {
            if (hubWish.Count == 0 || !HasItadKey) return;
            if (hubWishPrices != null && (DateTime.Now - hubWishPricesAt).TotalMinutes < 15) return;
            var wishes = settings.StoreWishlist != null ? settings.StoreWishlist.ToList() : new List<WishItem>();
            var ids = wishes.Select(w => w.ItadId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            if (ids.Count == 0) return;
            int gen = pageGen;
            string shops = ItadShops;
            hubWishPricesAt = DateTime.Now;   // (one ask at a time)
            KippixShared.SafeWork.Queue(_ =>
            {
                Dictionary<string, Tuple<List<ItadDeal>, ItadDeal>> prices = null;
                try { prices = ItadPrices(ids, shops); } catch (Exception ex) { logger.Warn("Kippix: profile wishlist prices: " + ex.Message); }
                OnUi(() =>
                {
                    if (prices == null) { hubWishPricesAt = DateTime.MinValue; return; }
                    hubWishPrices = prices;
                    hubWishPricesAt = DateTime.Now;
                    if (gen != pageGen || !HubOverviewNow) return;
                    var focus = FocusedHubCardKey();
                    FillHubWishShelf();
                    NotifyHubShelves();
                    LoadHubCardImages(hubWish.ToList(), gen);
                    if (focus != null) { var again = hubWish.FirstOrDefault(c => c.Key == focus); if (again != null) UiTools.FocusItem("KX_HubWishList", again); }
                });
            });
        }

        private string FocusedHubCardKey()
        {
            var c = FocusedPageItem() as HubCard;
            return c != null ? c.Key : null;
        }

        private void LoadHubCardImages(List<HubCard> cards, int gen)
        {
            var todo = cards.Where(c => !string.IsNullOrEmpty(c.ImagePath) && c.Thumb == null).ToList();
            if (todo.Count == 0) return;
            KippixShared.SafeWork.Queue(_ =>
            {
                foreach (var c in todo)
                {
                    if (gen != pageGen) return;
                    ImageSource img = null;
                    try { img = Images.Load(c.ImagePath, c.ImageWidth); } catch (Exception) { }
                    if (img != null) { var cc = c; OnUi(() => cc.Thumb = img); }
                }
            });
        }

        // =====================================================================
        // The page's lists in the profile's look (the same lists, other templates)
        // =====================================================================

        private readonly Dictionary<string, Tuple<DataTemplate, ItemsPanelTemplate, ControlTemplate>> pageTemplatesWere = new Dictionary<string, Tuple<DataTemplate, ItemsPanelTemplate, ControlTemplate>>();
        private bool hubTemplatesOn;

        /// <summary>On the profile: rows as cards two to a line, covers as a shelf. Elsewhere: the pages' own look.</summary>
        private void SetHubTemplates(bool on)
        {
            if (on == hubTemplatesOn) return;
            try
            {
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                var view = win != null ? UiTools.FindByName(win, "KX_PagesView") : null;
                if (view == null) return;
                bool done = SwapListLook(view, "KX_PageRowList", on, "KX_HubRowItemTemplate", "KX_HubRowPanel", null)
                          & SwapListLook(view, "KX_PageTileList", on, "KX_HubTileItemTemplate", "KX_HubTilePanel", "KX_HubTileShell");
                if (done) hubTemplatesOn = on;
            }
            catch (Exception ex) { logger.Warn("Kippix: profile look: " + ex.Message); }
        }

        private bool SwapListLook(FrameworkElement view, string listName, bool on, string itemKey, string panelKey, string shellKey)
        {
            var list = UiTools.FindByName(view, listName) as ItemsControl;
            if (list == null) return false;
            if (!pageTemplatesWere.ContainsKey(listName)) pageTemplatesWere[listName] = Tuple.Create(list.ItemTemplate, list.ItemsPanel, list.Template);
            var was = pageTemplatesWere[listName];
            if (on)
            {
                var it = view.TryFindResource(itemKey) as DataTemplate;
                var panel = view.TryFindResource(panelKey) as ItemsPanelTemplate;
                var shell = shellKey != null ? view.TryFindResource(shellKey) as ControlTemplate : null;
                if (it == null || panel == null) return false;
                list.ItemTemplate = it;
                list.ItemsPanel = panel;
                if (shell != null) list.Template = shell;
            }
            else
            {
                list.ItemTemplate = was.Item1;
                list.ItemsPanel = was.Item2;
                if (was.Item3 != null) list.Template = was.Item3;
            }
            return true;
        }
    }
}

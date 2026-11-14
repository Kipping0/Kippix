using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// "Out today" for wishlisted games (0.85). Once a day: 20 seconds after Playnite starts (with the wishlist's price check),
    /// the release dates of wishlisted Steam games are brought up to date (RefreshWishReleases: at most daily per game), and
    /// at midnight while Playnite is open the dates already known are looked at again (no going online). A wishlisted game
    /// that came out today (or in the last two days, if Playnite wasn't open) gets a card: "Out today" / "Out now", with its
    /// picture. Each game is told about once (WishReleaseNotices). Not for games already in your library. The card follows
    /// Settings › Notifications › Other notices.
    /// </summary>
    public partial class ThemeApi
    {
        private DateTime wishReleaseDay = DateTime.MinValue;

        /// <summary>From the start-up wishlist check: fresh release dates, then the cards.</summary>
        private void CheckWishReleases()
        {
            if (settings.StoreWishlist == null || settings.StoreWishlist.Count == 0) return;
            wishReleaseDay = DateTime.Today;
            try { RefreshWishReleases(AnnounceWishReleases); }
            catch (Exception ex) { logger.Warn("Kippix: wishlist releases: " + ex.Message); }
        }

        /// <summary>From the 2-second tick: a new day while Playnite is open (only the dates already known; nothing online).</summary>
        private void WishReleaseTick()
        {
            if (wishReleaseDay == DateTime.MinValue || DateTime.Today == wishReleaseDay) return;   // (not before the start-up check)
            wishReleaseDay = DateTime.Today;
            AnnounceWishReleases();
        }

        private void AnnounceWishReleases()
        {
            try
            {
                if (settings.StoreWishlist == null) return;
                if (settings.WishReleaseNotices == null) settings.WishReleaseNotices = new List<string>();
                var today = DateTime.Today;
                var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var g in api.Database.Games)
                        if (g != null && g.PluginId == SteamLibId && !string.IsNullOrEmpty(g.GameId)) owned.Add(g.GameId);
                }
                catch (Exception) { }
                var due = new List<WishItem>();
                foreach (var w in settings.StoreWishlist.ToList())
                {
                    if (w == null || !w.ReleaseDate.HasValue) continue;
                    var d = w.ReleaseDate.Value.Date;
                    if (d > today || d < today.AddDays(-2)) continue;
                    if (!string.IsNullOrEmpty(w.SteamAppId) && owned.Contains(w.SteamAppId)) continue;
                    string key = (w.SteamAppId ?? w.ItadId ?? w.Name ?? "") + "|" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (settings.WishReleaseNotices.Contains(key)) continue;
                    settings.WishReleaseNotices.Add(key);
                    due.Add(w);
                }
                if (due.Count == 0) return;
                while (settings.WishReleaseNotices.Count > 300) settings.WishReleaseNotices.RemoveAt(0);
                SaveSettings();
                logger.Info("Kippix: wishlist: out now: " + string.Join(", ", due.Select(w => w.Name ?? "?").ToArray()));
                foreach (var w in due.Take(3))
                {
                    var wish = w;
                    bool isToday = wish.ReleaseDate.Value.Date == today;
                    string line = isToday ? Loc("LOC_KX_WishOutToday", "Out today") : string.Format(Loc("LOC_KX_WishOutSince", "Out now (since {0})"), wish.ReleaseDate.Value.ToString("d MMM", CultureInfo.CurrentCulture));
                    string store = string.IsNullOrEmpty(wish.Store) ? (string.IsNullOrEmpty(wish.SteamAppId) ? "" : "Steam") : wish.Store;
                    string line2 = store.Length > 0 ? string.Format(Loc("LOC_KX_WishOnStore", "On {0} · on your wishlist"), store) : Loc("LOC_KX_WishOnList", "On your wishlist");
                    string pic = wish.Image;
                    KippixShared.SafeWork.Queue(_ =>
                    {
                        ImageSource img = null;
                        try { if (!string.IsNullOrEmpty(pic)) img = Images.Load(pic, 460); } catch (Exception) { }
                        OnUi(() => ShowNoticeKept(Loc("LOC_KX_CardWishlistUpper", "WISHLIST"), wish.Name ?? "", line, line2, img));
                    });
                }
                if (due.Count > 3)
                    ShowNoticeKept(Loc("LOC_KX_CardWishlistUpper", "WISHLIST"), string.Format(Loc("LOC_KX_WishOutMore", "{0} more games from your wishlist are out"), due.Count - 3), "", "", null);
            }
            catch (Exception ex) { logger.Warn("Kippix: wishlist releases: " + ex.Message); }
        }
    }
}

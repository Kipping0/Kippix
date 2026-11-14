using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.4) A Community item's pictures, nearly full screen on its page under the buttons (Use, Save…): A or ‹ › on the
    /// picture goes to the next one when there's more than one. And the gallery's download counts (from the gallery's
    /// "community" release on GitHub, where every download from Kippix is counted), and sorting by them.
    /// </summary>
    public partial class ThemeApi
    {
        private List<string> communityShots = new List<string>();
        private int communityShotAt;
        private readonly Dictionary<string, ImageSource> communityShotCache = new Dictionary<string, ImageSource>();

        public bool HasCommunityShot { get { return openPanel == "page" && pageKey == "communityitem" && communityShots.Count > 0; } }
        public bool HasCommunityShotNav { get { return HasCommunityShot && communityShots.Count > 1; } }
        public ImageSource CommunityShot
        {
            get
            {
                if (!HasCommunityShot) return null;
                ImageSource img;
                return communityShotCache.TryGetValue(communityShots[communityShotAt], out img) ? img : null;
            }
        }
        public bool CommunityShotLoading { get { return HasCommunityShot && CommunityShot == null; } }
        public string CommunityShotCounter
        {
            get { return HasCommunityShotNav ? string.Format(Loc("LOC_KX_ShotCounter", "{0} / {1}   ·   ‹ › more pictures"), communityShotAt + 1, communityShots.Count) : ""; }
        }
        public ICommand CommunityShotNextCommand { get { return communityShotNextCmd ?? (communityShotNextCmd = new RelayCommand(_ => CommunityShotStep(1))); } }
        private ICommand communityShotNextCmd;

        private void NotifyCommunityShot()
        {
            foreach (string n in new[] { "HasCommunityShot", "HasCommunityShotNav", "CommunityShot", "CommunityShotLoading", "CommunityShotCounter" }) Notify(n);
        }

        private void ShowCommunityShots(CommunityItem c)
        {
            communityShots = c.Previews.Count > 0 ? c.Previews.ToList() : (c.Preview.Length > 0 ? new List<string> { c.Preview } : new List<string>());
            communityShotAt = 0;
            if (communityShotCache.Count > 24) communityShotCache.Clear();
            NotifyCommunityShot();
            if (communityShots.Count == 0) return;
            int gen = pageGen;
            var urls = communityShots.ToList();
            KippixShared.SafeWork.Queue(_ =>
            {
                foreach (string p in urls)
                {
                    if (gen != pageGen) return;
                    if (communityShotCache.ContainsKey(p)) continue;
                    ImageSource img = null;
                    try { img = Images.Load(CommunityRaw(p), 1920); } catch (Exception) { }
                    if (img == null) continue;
                    string key = p;
                    OnUi(() => { communityShotCache[key] = img; if (gen == pageGen) NotifyCommunityShot(); });
                }
            });
        }

        private bool CommunityShotStep(int d)
        {
            if (!HasCommunityShotNav) { if (HasCommunityShot && d != 0) PlaySound("back"); return HasCommunityShot; }
            communityShotAt = (communityShotAt + d + communityShots.Count) % communityShots.Count;
            PlaySound("move");
            NotifyCommunityShot();
            return true;
        }

        // ---------------- downloads ----------------

        private Dictionary<string, int> communityDownloads;   // kind|id → downloads (null: not known)

        private string CommunityDownloadsText(CommunityItem c)
        {
            if (c.Downloads < 0) return "";
            return c.Downloads == 1 ? Loc("LOC_KX_OneDownload", "1 download") : string.Format(Loc("LOC_KX_NDownloads", "{0} downloads"), c.Downloads.ToString("N0", CultureInfo.CurrentCulture));
        }

        /// <summary>The counts (worker thread), put on the items: GitHub's download count of each item's files in the release.</summary>
        private void ApplyCommunityDownloads(Dictionary<string, List<CommunityItem>> lists, Dictionary<string, int> perAsset)
        {
            if (lists == null || perAsset == null) return;
            foreach (var kv in lists)
                foreach (var c in kv.Value)
                {
                    // every version of it counts (its files are named kind--id--<version>.json)
                    string prefix = kv.Key + "--" + c.Id + "--";
                    int n = 0;
                    bool any = false;
                    foreach (var a in perAsset) if (a.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { n += a.Value; any = true; }
                    c.Downloads = any ? n : 0;
                }
        }

        private bool CommunitySortByDownloads { get { return settings.CommunitySort == "downloads"; } }

        private IEnumerable<CommunityItem> CommunitySorted(IEnumerable<CommunityItem> items)
        {
            return CommunitySortByDownloads
                ? items.OrderByDescending(c => c.Downloads).ThenByDescending(c => c.Added ?? "", StringComparer.Ordinal)
                : items.OrderByDescending(c => c.Added ?? "", StringComparer.Ordinal);
        }
    }
}

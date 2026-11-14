using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KippixHelper
{
    /// <summary>One rarity tier in the achievements header: its trophy colour and how many you've unlocked.</summary>
    public class AchTier
    {
        public string Name { get; set; }
        public string Count { get; set; }
        public string Color { get; set; }
    }

    /// <summary>
    /// The achievements page header: unlocked achievements per rarity tier (Common, Uncommon, Rare, Ultra rare), and a score
    /// (rarer is worth more) that can be shown as a level instead, or hidden (the page's settings dropdown).
    /// </summary>
    public partial class ThemeApi
    {
        // the same thresholds everywhere: % of players who have it
        internal const double UltraRarePct = 5, RarePct = 10, UncommonPct = 50;
        private static readonly int[] TierPoints = { 10, 25, 50, 100 };
        private static readonly string[] TierColors = { "#FFB8BEC8", "#FF4FC37A", "#FF3F8CFF", "#FFD9A93A" };

        public List<AchTier> AchTiers { get; private set; }
        public bool HasAchTiers { get { return AchTiers != null && AchTiers.Count > 0; } }
        public string AchScoreText { get; private set; }
        public string AchLevelText { get; private set; }
        public string AchLevelNextText { get; private set; }
        public double AchLevelBarWidth { get; private set; }
        public bool ShowAchScore { get { return HasAchTiers && settings.AchTallyMode == 0; } }
        public bool ShowAchLevel { get { return HasAchTiers && settings.AchTallyMode == 1; } }

        /// <summary>0 common, 1 uncommon, 2 rare, 3 ultra rare.</summary>
        internal static int AchTierOf(AchItemData i)
        {
            string t = (i.RarityTier ?? "").Replace(" ", "").ToLowerInvariant();
            if (t.Contains("ultra")) return 3;
            if (t == "rare") return 2;
            if (t.Contains("uncommon")) return 1;
            if (t == "common") return 0;
            if (i.Rarity.HasValue)
            {
                double p = i.Rarity.Value;
                if (p <= UltraRarePct) return 3;
                if (p <= RarePct) return 2;
                if (p <= UncommonPct) return 1;
            }
            return 0;
        }

        private string AchTallyModeText
        {
            get { return settings.AchTallyMode == 1 ? Loc("LOC_KX_AchLevel", "Level") : settings.AchTallyMode == 2 ? Loc("LOC_KX_Hidden", "Hidden") : Loc("LOC_KX_AchScore", "Score"); }
        }

        /// <summary>The tiers, rarest first (Ultra rare, Rare, Uncommon, Common).</summary>
        private List<AchTier> TierList(int[] counts)
        {
            string[] names = { Loc("LOC_KX_TierCommon", "Common"), Loc("LOC_KX_TierUncommon", "Uncommon"), Loc("LOC_KX_TierRare", "Rare"), Loc("LOC_KX_TierUltra", "Ultra rare") };
            return counts.Sum() == 0 ? new List<AchTier>()
                 : new[] { 3, 2, 1, 0 }.Select(k => new AchTier { Name = names[k], Count = counts[k].ToString("N0", CultureInfo.CurrentCulture), Color = TierColors[k] }).ToList();
        }

        private static int[] TierCounts(IEnumerable<AchGameData> games)
        {
            var counts = new int[4];
            if (games != null)
                foreach (var d in games)
                    if (d != null && d.Items != null)
                        foreach (var i in d.Items)
                            if (i != null && i.Unlocked) counts[AchTierOf(i)]++;
            return counts;
        }

        // one game's page: the same tally, for that game only
        public List<AchTier> AchGameTiers { get; private set; }
        public bool HasAchGameTiers { get { return AchGameTiers != null && AchGameTiers.Count > 0; } }
        public string AchGameScoreText { get; private set; }
        public bool ShowAchGameScore { get { return HasAchGameTiers && settings.AchTallyMode != 2; } }

        private void BuildAchGameTally(AchGameData d)
        {
            var counts = TierCounts(d != null ? new[] { d } : null);
            AchGameTiers = TierList(counts);
            long score = Enumerable.Range(0, 4).Sum(k => (long)counts[k] * TierPoints[k]);
            AchGameScoreText = string.Format(CultureInfo.CurrentCulture, Loc("LOC_KX_AchScoreText", "Score {0:N0}"), score);
            foreach (string n in new[] { "AchGameTiers", "HasAchGameTiers", "AchGameScoreText", "ShowAchGameScore" }) Notify(n);
        }

        private void BuildAchTally()
        {
            var counts = TierCounts(achData);
            AchTiers = TierList(counts);
            long score = Enumerable.Range(0, 4).Sum(k => (long)counts[k] * TierPoints[k]);
            AchScoreText = string.Format(CultureInfo.CurrentCulture, Loc("LOC_KX_AchScoreText", "Score {0:N0}"), score);
            // levels: each one takes a little more than the last (500, 600, 700…)
            int level = 1;
            long need = 500, left = score;
            while (left >= need) { left -= need; level++; need += 100; }
            AchLevelText = string.Format(CultureInfo.CurrentCulture, Loc("LOC_KX_AchLevelText", "Level {0}"), level);
            AchLevelNextText = string.Format(CultureInfo.CurrentCulture, Loc("LOC_KX_AchLevelNext", "{0:N0} / {1:N0} to level {2}"), left, need, level + 1);
            AchLevelBarWidth = Math.Max(4, 180.0 * left / need);
            foreach (string n in new[] { "AchTiers", "HasAchTiers", "AchScoreText", "AchLevelText", "AchLevelNextText", "AchLevelBarWidth", "ShowAchScore", "ShowAchLevel" }) Notify(n);
        }

        private void CycleAchTallyMode()
        {
            settings.AchTallyMode = (settings.AchTallyMode + 1) % 3;
            SaveSettings();
            BuildAchTally();
            Notify("ShowAchGameScore");
        }
    }
}

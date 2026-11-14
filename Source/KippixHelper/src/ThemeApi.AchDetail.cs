using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;

namespace KippixHelper
{
    /// <summary>
    /// One achievement's page: big icon, status, rarity, extra facts, and an AI tip (how to get it,
    /// whether it's missable, when to go for it) from KippixOverlay's achievement guides.
    /// </summary>
    public partial class ThemeApi
    {
        private class GuideInfo
        {
            public string Tip;
            public bool Missable;
            public string When;
        }

        private AchItemRow achItemRow;
        private bool achReveal;
        private bool achGuideWriting;

        public ObservableCollection<string> AchDetailFacts { get; private set; }
        public bool HasAchDetailFacts { get { return AchDetailFacts.Count > 0; } }
        public string AchDetailName { get; private set; }
        public string AchDetailDescription { get; private set; }
        public bool HasAchDetailDescription { get { return !string.IsNullOrEmpty(AchDetailDescription); } }
        public ImageSource AchDetailIcon { get { return achItemRow != null ? achItemRow.Icon : null; } }
        public bool HasAchDetailIcon { get { return AchDetailIcon != null; } }
        public bool AchDetailDimmed { get { return achItemRow != null && achItemRow.IsDimmed; } }
        public bool AchDetailUnlocked { get { return achItemRow != null && achItemRow.IsUnlocked; } }
        public string AchDetailStatus { get; private set; }
        public string AchDetailRarity { get; private set; }
        public bool HasAchDetailRarity { get { return !string.IsNullOrEmpty(AchDetailRarity); } }
        public string AchDetailPosition { get; private set; }
        public bool ShowAchReveal { get; private set; }
        public bool HasAchDetailProgress { get; private set; }
        public string AchDetailProgressText { get; private set; }
        public double AchDetailProgressWidth { get; private set; }

        // AI tip
        public string AchGuideTip { get; private set; }
        public bool HasAchGuideTip { get { return !string.IsNullOrEmpty(AchGuideTip); } }
        public bool AchGuideMissable { get; private set; }
        public string AchGuideWhen { get; private set; }
        public bool HasAchGuideWhen { get { return !string.IsNullOrEmpty(AchGuideWhen); } }
        public string AchGuideNote { get; private set; }
        public bool HasAchGuideNote { get { return !string.IsNullOrEmpty(AchGuideNote); } }
        public bool ShowAchGetTips { get; private set; }
        public bool AchGuideBusy { get { return achGuideWriting; } }

        public ICommand AchPrevCommand { get; private set; }
        public ICommand AchNextCommand { get; private set; }
        public ICommand AchRevealCommand { get; private set; }
        public ICommand AchGetTipsCommand { get; private set; }

        private void InitAchDetailCommands()
        {
            AchPrevCommand = new RelayCommand(_ => StepAchItem(-1));
            AchNextCommand = new RelayCommand(_ => StepAchItem(1));
            AchRevealCommand = new RelayCommand(_ => { achReveal = true; FillAchDetail(); FocusPanelSoon(); });
            AchGetTipsCommand = new RelayCommand(_ => WriteTips());
        }

        private void OpenAchItem(AchItemRow row)
        {
            ClosePicker(false);
            achItemRow = row;
            achReveal = false;
            achPage = "item";
            FillAchDetail();
            NotifyAch();
            FocusPanelSoon();
        }

        /// <summary>LB / RB (or the arrows on screen): previous or next achievement in the current list.</summary>
        private void StepAchItem(int delta)
        {
            if (achPage != "item" || achItemRow == null || AchItems.Count == 0) return;
            int i = AchItems.IndexOf(achItemRow);
            int j = i < 0 ? 0 : (i + delta + AchItems.Count) % AchItems.Count;
            achItemRow = AchItems[j];
            achReveal = false;
            FillAchDetail();
        }

        private void FillAchDetail()
        {
            var row = achItemRow;
            if (row == null || achGame == null) return;
            var i = row.Data;
            bool secret = i.Hidden && !i.Unlocked && !achReveal;

            AchDetailName = secret ? Loc("LOC_KX_HiddenAch", "Hidden achievement") : i.Name;
            AchDetailDescription = secret ? Loc("LOC_KX_HiddenAchReveal", "This one's a secret. Reveal it if you don't mind spoilers.") : i.Description;
            ShowAchReveal = secret;
            AchDetailStatus = i.Unlocked
                ? (i.Date.HasValue
                    ? string.Format(Loc("LOC_KX_UnlockedOn", "Unlocked {0} at {1}"),
                                    i.Date.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture),
                                    i.Date.Value.ToString(settings.Clock24h ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture))
                    : Loc("LOC_KX_Unlocked", "Unlocked"))
                : Loc("LOC_KX_Locked", "Locked");

            string rarity = i.Rarity.HasValue ? string.Format(CultureInfo.CurrentCulture, Loc("LOC_KX_RarityLong", "{0:0.#}% of players have this"), i.Rarity.Value) : null;
            string tier = Tidy(i.RarityTier);
            if (tier != null && !string.Equals(tier, "None", StringComparison.OrdinalIgnoreCase))
                rarity = rarity == null ? tier : rarity + "   ·   " + tier;
            AchDetailRarity = rarity;

            AchDetailFacts.Clear();
            if (!string.IsNullOrEmpty(i.TrophyType)) AchDetailFacts.Add(string.Format(Loc("LOC_KX_TrophyType", "{0} trophy"), Tidy(i.TrophyType)));
            if (i.Points.HasValue && i.Points.Value > 0) AchDetailFacts.Add(string.Format(Loc("LOC_KX_Points", "{0} points"), i.Points.Value));
            if (!string.IsNullOrEmpty(i.Category) && !string.Equals(i.Category, "Default", StringComparison.OrdinalIgnoreCase)) AchDetailFacts.Add(i.Category);
            if (i.Hidden) AchDetailFacts.Add(Loc("LOC_KX_HiddenFact", "Hidden achievement"));

            HasAchDetailProgress = !i.Unlocked && i.ProgressDenom.HasValue && i.ProgressDenom.Value > 1 && i.ProgressNum.HasValue;
            AchDetailProgressText = HasAchDetailProgress ? string.Format(Loc("LOC_KX_Progress", "Progress  {0} / {1}"), i.ProgressNum.Value, i.ProgressDenom.Value) : "";
            AchDetailProgressWidth = HasAchDetailProgress ? Math.Min(1.0, (double)i.ProgressNum.Value / i.ProgressDenom.Value) * 420 : 0;

            int pos = AchItems.IndexOf(row);
            AchDetailPosition = pos >= 0 ? (pos + 1) + " / " + AchItems.Count : "";

            RefreshAchGuide();
            NotifyAchDetail();
        }

        private static string Tidy(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            // UltraRare -> Ultra rare
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (k > 0 && char.IsUpper(c) && char.IsLower(s[k - 1])) { sb.Append(' '); sb.Append(char.ToLowerInvariant(c)); }
                else sb.Append(k == 0 ? char.ToUpperInvariant(c) : c);
            }
            return sb.ToString();
        }

        // ---------------- AI tips (KippixOverlay) ----------------

        private int OverlayVersion { get { return overlay.Available ? overlay.Get<int>("Version", 0) : 0; } }

        private GuideInfo GuideFor(Guid gameId, string name)
        {
            if (OverlayVersion < 4 || string.IsNullOrEmpty(name)) return null;
            var d = overlay.Call("Guide", gameId, name) as Dictionary<string, object>;
            if (d == null) return null;
            object tip, miss, when;
            d.TryGetValue("Tip", out tip);
            d.TryGetValue("Missable", out miss);
            d.TryGetValue("When", out when);
            if (!(tip is string) || ((string)tip).Length == 0) return null;
            return new GuideInfo { Tip = (string)tip, Missable = miss is bool && (bool)miss, When = when as string };
        }

        private void RefreshAchGuide()
        {
            var row = achItemRow;
            AchGuideTip = null;
            AchGuideWhen = null;
            AchGuideMissable = false;
            AchGuideNote = null;
            ShowAchGetTips = false;
            if (row == null || achGame == null) { NotifyAchDetail(); return; }

            bool secret = row.Data.Hidden && !row.Data.Unlocked && !achReveal;
            Guid gameId = achGame.Id;

            if (!overlay.Available)
                AchGuideNote = OldOverlayInstalled ? Loc("LOC_KX_GuideOldOverlay", "The overlay is built into Kippix Helper now: uninstall the separate KippixOverlay add-on and restart Playnite.")
                                                   : Loc("LOC_KX_GuideNeedsOverlay", "Turn on the in-game overlay to get AI tips on how to unlock achievements.");
            else if (OverlayVersion < 4)
                AchGuideNote = Loc("LOC_KX_GuideUpdateOverlay", "Remove the old KippixOverlay add-on (it's built in now) to get AI tips here.");
            else
            {
                var guide = GuideFor(gameId, row.Data.Name);
                achGuideWriting = overlay.Call("IsWritingGuides", gameId) as bool? == true;
                if (guide != null && !secret)
                {
                    AchGuideTip = guide.Tip;
                    AchGuideMissable = guide.Missable;
                    AchGuideWhen = guide.When;
                }
                else if (guide != null)
                    AchGuideNote = Loc("LOC_KX_GuideHidden", "There's a tip for this one. Reveal the achievement to see it.");
                else if (achGuideWriting)
                    AchGuideNote = Loc("LOC_KX_GuideWriting", "Writing tips for this game… this can take a minute.");
                else if (!overlay.Get<bool>("GuidesReady", false))
                    AchGuideNote = Loc("LOC_KX_GuideSetup", "To get tips, set up an AI provider in Playnite desktop › Add-ons › Extension settings › Kippix Helper › Overlay.");
                else
                {
                    string err = overlay.Call("GuideError", gameId) as string;
                    AchGuideNote = !string.IsNullOrEmpty(err) ? err : Loc("LOC_KX_GuideNone", "No tip for this achievement yet.");
                    ShowAchGetTips = true;
                }
            }
            NotifyAchDetail();
        }

        private void WriteTips()
        {
            if (achGame == null || OverlayVersion < 4) return;
            bool started = overlay.Call("WriteGuides", achGame.Id, false) as bool? == true;
            achGuideWriting = started;
            RefreshAchGuide();
            if (started) FocusPanelSoon();
        }

        /// <summary>The overlay said something changed: pick up freshly written tips.</summary>
        private void OnOverlayChangedAch()
        {
            if (openPanel != "achievements" || achGame == null || OverlayVersion < 4) return;
            bool was = achGuideWriting;
            bool now = overlay.Call("IsWritingGuides", achGame.Id) as bool? == true;
            achGuideWriting = now;
            if (was && !now)
            {
                foreach (var r in achItemRows)
                {
                    var g = !r.Data.Unlocked ? GuideFor(achGame.Id, r.Data.Name) : null;
                    r.IsMissable = g != null && g.Missable;
                }
            }
            if (achPage == "item") RefreshAchGuide();
        }

        private void NotifyAchDetail()
        {
            foreach (string n in new[] { "AchDetailName", "AchDetailDescription", "HasAchDetailDescription", "AchDetailIcon", "HasAchDetailIcon",
                                         "AchDetailDimmed", "AchDetailUnlocked", "AchDetailStatus", "AchDetailRarity", "HasAchDetailRarity",
                                         "AchDetailPosition", "ShowAchReveal", "HasAchDetailFacts", "HasAchDetailProgress", "AchDetailProgressText",
                                         "AchDetailProgressWidth", "AchGuideTip", "HasAchGuideTip", "AchGuideMissable", "AchGuideWhen", "HasAchGuideWhen",
                                         "AchGuideNote", "HasAchGuideNote", "ShowAchGetTips", "AchGuideBusy" })
                Notify(n);
            NotifyAchDetailDesign();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>A NEXT UP chip on a game's achievements page (the row it opens is the list's own, so its icon comes from there).</summary>
    public class AchNextRow
    {
        public AchItemRow Row { get; set; }
        public string Name { get; set; }
        public string Sub { get; set; }
        public double Fill { get; set; }          // 0..170 (progress)
        public bool HasFill { get { return Fill > 0; } }
        public ICommand Command { get; set; }
    }

    /// <summary>A NEXT UP line on the game page's achievements card (just for show: the card opens the achievements).</summary>
    public class AchNextLine : Observable
    {
        public string Name { get; set; }
        public string Sub { get; set; }
        public double Fill { get; set; }          // 0..120
        public bool HasFill { get { return Fill > 0; } }
        private ImageSource icon;
        public ImageSource Icon { get { return icon; } set { Set(ref icon, value, "Icon"); } }
    }

    /// <summary>
    /// NEXT UP: the locked achievements you're closest to. With progress from the achievements add-on (Playnite Achievements'
    /// ProgressNum / ProgressDenom, SuccessStory's progression) the furthest along come first; then the most common (global unlock %).
    /// Hidden ones only show when there's progress. On a game's achievements page (chips under the header), the game page's
    /// achievements card, and the profile's Closest achievements (across what you played lately). Switch: the achievements
    /// page's settings › Next up.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>How close a locked achievement is: 1..2 with progress, 0..1 the share of players who have it, -1 nothing known.</summary>
        internal static double NextUpScore(AchItemData i)
        {
            if (i == null || i.Unlocked) return -2;
            if (i.ProgressDenom.HasValue && i.ProgressDenom.Value > 1 && i.ProgressNum.HasValue && i.ProgressNum.Value > 0 && i.ProgressNum.Value < i.ProgressDenom.Value)
                return 1 + (double)i.ProgressNum.Value / i.ProgressDenom.Value;
            if (i.Hidden) return -2;
            if (i.Rarity.HasValue) return Math.Min(1, i.Rarity.Value / 100.0);
            return -1;
        }

        internal static List<AchItemData> NextUpItems(AchGameData d, int max)
        {
            if (d == null || d.Items == null) return new List<AchItemData>();
            return d.Items.Where(i => i != null && !i.Unlocked && NextUpScore(i) > -2)
                          .Select((i, n) => Tuple.Create(i, NextUpScore(i), n))
                          .OrderByDescending(t => t.Item2).ThenBy(t => t.Item3)
                          .Take(max).Select(t => t.Item1).ToList();
        }

        private static bool HasProgress(AchItemData i) { return NextUpScore(i) >= 1; }

        private static double ProgressOf(AchItemData i) { return HasProgress(i) ? (double)i.ProgressNum.Value / i.ProgressDenom.Value : 0; }

        private string NextUpSub(AchItemData i)
        {
            if (HasProgress(i))
                return string.Format(Loc("LOC_KX_NextProgress", "{0} / {1}  ·  {2}% there"), i.ProgressNum.Value.ToString("N0", CultureInfo.CurrentCulture),
                                     i.ProgressDenom.Value.ToString("N0", CultureInfo.CurrentCulture), Math.Floor(100 * ProgressOf(i)));
            if (i.Rarity.HasValue) return string.Format(Loc("LOC_KX_NextCommon", "{0:0.#}% of players have it"), i.Rarity.Value);
            return Loc("LOC_KX_AchNextSub2", "Still to get");
        }

        // ---------------- a game's achievements page ----------------

        private List<AchNextRow> achNextUp = new List<AchNextRow>();
        public List<AchNextRow> AchNextUp { get { return achNextUp; } }
        public bool HasAchNextUp { get { return settings.AchNextUp && achPage != "games" && achNextUp.Count > 0; } }
        private AchNextRow achDetailFromNext;   // the detail page was opened from a chip: B comes back to it

        /// <summary>After BuildAchItems: the chips use the list's own rows (same icons, same detail page).</summary>
        private void FillAchNextUp(AchGameData d)
        {
            var list = new List<AchNextRow>();
            foreach (var i in NextUpItems(d, 5))
            {
                var row = achItemRows.FirstOrDefault(r => ReferenceEquals(r.Data, i));
                if (row == null) continue;
                var chip = new AchNextRow
                {
                    Row = row, Name = row.Name, Sub = NextUpSub(i), Fill = HasProgress(i) ? Math.Max(6, 170 * ProgressOf(i)) : 0
                };
                chip.Command = new RelayCommand(_ => { achDetailFromNext = chip; OpenAchItem(row); });
                list.Add(chip);
            }
            achNextUp = list;
            achDetailFromNext = null;
            Notify("AchNextUp");
            Notify("HasAchNextUp");
        }

        /// <summary>B on a detail page opened from a chip: back on that chip. True when it put the highlight there.</summary>
        private bool BackToAchNextUp()
        {
            var chip = achDetailFromNext;
            achDetailFromNext = null;
            if (chip == null || !achNextUp.Contains(chip) || !HasAchNextUp) return false;
            ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_AchNextList", chip)), DispatcherPriority.ContextIdle);
            return true;
        }

        private void ToggleAchNextUp()
        {
            settings.AchNextUp = !settings.AchNextUp;
            SaveSettings();
            Notify("HasAchNextUp");
            NotifyDetNextUp();
        }

        // ---------------- the game page's achievements card ----------------

        private List<AchNextLine> detAchNextUp = new List<AchNextLine>();
        public List<AchNextLine> DetAchNextUp { get { return detAchNextUp; } }
        public bool HasDetAchNextUp { get { return settings.AchNextUp && detAchNextUp.Count > 0; } }

        /// <summary>From FillAchHighlights (any game, also one with no achievements: then it's emptied).</summary>
        private void FillDetNextUp(AchGameData d)
        {
            var lines = new List<AchNextLine>();
            if (d != null)
                foreach (var i in NextUpItems(d, 5))
                {
                    var line = new AchNextLine { Name = i.Hidden ? Loc("LOC_KX_HiddenAch", "Hidden achievement") : i.Name, Sub = NextUpSub(i), Fill = HasProgress(i) ? Math.Max(4, 120 * ProgressOf(i)) : 0 };
                    lines.Add(line);
                    var l = line;
                    if (!string.IsNullOrEmpty(i.Icon)) LoadAchIcon(i.Icon, img => l.Icon = img);
                }
            detAchNextUp = lines;
            NotifyDetNextUp();
        }

        private void NotifyDetNextUp()
        {
            Notify("DetAchNextUp");
            Notify("HasDetAchNextUp");
        }

        // ---------------- the profile: Closest achievements ----------------

        private class ClosestAch
        {
            public AchItemData Item;
            public string Game;
            public Guid GameId;
            public double Score;
        }

        /// <summary>(from a worker) Across the games played in the last two months (the 12 most recent), the 5 closest.</summary>
        private List<ClosestAch> ClosestAchievements(List<AchGameData> achs)
        {
            var found = new List<ClosestAch>();
            if (achs == null || achs.Count == 0) return found;
            try
            {
                var byId = new Dictionary<Guid, AchGameData>();
                foreach (var d in achs) if (d != null && !byId.ContainsKey(d.Id)) byId[d.Id] = d;
                var since = DateTime.Now.AddDays(-60);
                var recent = api.Database.Games.Where(g => g != null && g.LastActivity.HasValue && g.LastActivity.Value >= since && byId.ContainsKey(g.Id) && HubSees(g, true))
                                               .OrderByDescending(g => g.LastActivity.Value).Take(12).ToList();
                foreach (var g in recent)
                {
                    var d = byId[g.Id];
                    foreach (var i in NextUpItems(d, 2))
                    {
                        double s = NextUpScore(i);
                        if (s < 0) continue;
                        found.Add(new ClosestAch { Item = i, Game = g.Name, GameId = g.Id, Score = s });
                    }
                }
            }
            catch (Exception) { }
            return found.OrderByDescending(c => c.Score).Take(5).ToList();
        }

        private void AddClosestRows(List<ClosestAch> list)
        {
            if (list == null || list.Count == 0) return;
            AddPageHeader(Loc("LOC_KX_ClosestAch", "CLOSEST ACHIEVEMENTS"));
            foreach (var c in list)
            {
                var gid = c.GameId;
                bool prog = HasProgress(c.Item);
                PageRows.Add(new PageRow
                {
                    Text = c.Item.Hidden ? Loc("LOC_KX_HiddenAch", "Hidden achievement") : c.Item.Name, Icon = "trophy",
                    ImagePath = string.IsNullOrEmpty(c.Item.Icon) ? null : c.Item.Icon, Key = "near:" + gid + "|" + c.Item.Name,
                    Sub = c.Game + "   ·   " + NextUpSub(c.Item),
                    Right = prog ? Math.Floor(100 * ProgressOf(c.Item)) + "%" : "",
                    HasBar = prog, BarWidth = prog ? Math.Max(6, 420 * ProgressOf(c.Item)) : 0, BarColor = "#FF8AB4FF",
                    Command = new RelayCommand(_ => OpenAchForGameFromPage(gid))
                });
            }
        }

        /// <summary>That game's achievements page (from the profile); B there goes to the list of games.</summary>
        private void OpenAchForGameFromPage(Guid id)
        {
            PlaySound("select");
            ClosePanels();
            ui.BeginInvoke(new Action(() =>
            {
                pendingAchGame = id;
                achOpenedForGame = false;
                if (openPanel != "achievements") TogglePanel("achievements");
                achPicker = "";
                if (achRows.Count == 0 || achLoading) { achPage = "games"; NotifyAch(); LoadAchievements(); return; }
                OpenPendingAchGame();
            }), DispatcherPriority.ContextIdle);
        }
    }
}

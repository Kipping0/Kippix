using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// The Storage page's SUGGESTIONS: installed games that could go, biggest saving first, with what it would free altogether.
    /// A game is suggested once, for the first of: installed from two stores (the copy played less), finished (Beaten /
    /// Completed), or big (15 GB or more) and not played for 3 months. A uninstalls it through Playnite (the game's own
    /// launcher: the same "Uninstall?" question as the rest of the page). Only games whose size Playnite knows.
    /// </summary>
    public partial class ThemeApi
    {
        private const long StorageTipBigBytes = 15L * 1000 * 1000 * 1000;

        private class StorageTip
        {
            public Game Game;
            public long Bytes;
            public string Why;
        }

        private List<StorageTip> StorageTipsFor(List<Game> installed)
        {
            var tips = new List<StorageTip>();
            var taken = new HashSet<Guid>();
            Func<Game, long> size = g => g.InstallSize.HasValue ? (long)g.InstallSize.Value : 0;
            Func<Game, string> lastText = g => g.LastActivity.HasValue ? string.Format(Loc("LOC_KX_LastPlayedAgo", "Last played {0}"), AgoLong(g.LastActivity.Value)).ToLower(CultureInfo.CurrentCulture) : Loc("LOC_KX_TipNeverPlayed", "never played");

            // installed from two stores: the copy played less (then the one played longer ago)
            foreach (var grp in installed.GroupBy(g => HealthNameKey(g.Name)).Where(x => x.Key.Length > 0 && x.Select(g => g.PluginId).Distinct().Count() > 1))
            {
                var ordered = grp.OrderByDescending(g => g.Playtime).ThenByDescending(g => g.LastActivity ?? DateTime.MinValue).ToList();
                var keep = ordered[0];
                foreach (var g in ordered.Skip(1))
                {
                    if (size(g) <= 0 || taken.Contains(g.Id)) continue;
                    taken.Add(g.Id);
                    string keepSrc = keep.Source != null && !string.IsNullOrEmpty(keep.Source.Name) ? keep.Source.Name : Loc("LOC_KX_TipOtherStore", "another store");
                    string src = g.Source != null && !string.IsNullOrEmpty(g.Source.Name) ? g.Source.Name : "?";
                    tips.Add(new StorageTip { Game = g, Bytes = size(g), Why = string.Format(Loc("LOC_KX_TipDuplicate", "Uses {0} · also installed from {1} (this is the {2} copy)"), SizeText(size(g)), keepSrc, src) });
                }
            }
            // finished
            foreach (var g in installed.Where(g => size(g) > 0 && !taken.Contains(g.Id) && UpNextFinished(g)))
            {
                taken.Add(g.Id);
                string status = g.CompletionStatus != null && !string.IsNullOrEmpty(g.CompletionStatus.Name) ? g.CompletionStatus.Name : Loc("LOC_KX_TipFinished", "Finished");
                tips.Add(new StorageTip { Game = g, Bytes = size(g), Why = string.Format(Loc("LOC_KX_TipFinishedLine", "Uses {0} · {1} · {2}"), SizeText(size(g)), status, lastText(g)) });
            }
            // big and not played for 3 months (or never, and added over 3 months ago)
            var cutoff = DateTime.Now.AddMonths(-3);
            foreach (var g in installed.Where(g => size(g) >= StorageTipBigBytes && !taken.Contains(g.Id)))
            {
                DateTime? last = g.LastActivity ?? (g.Playtime == 0 ? g.Added : null);
                if (!last.HasValue || last.Value > cutoff) continue;
                taken.Add(g.Id);
                tips.Add(new StorageTip { Game = g, Bytes = size(g), Why = string.Format(Loc("LOC_KX_TipOld", "Uses {0} · {1}"), SizeText(size(g)), lastText(g)) });
            }
            return tips.OrderByDescending(t => t.Bytes).ToList();
        }

        /// <summary>"7 months ago", "2 years ago" (longer spans than AgoText's dates).</summary>
        private string AgoLong(DateTime when)
        {
            var local = when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when;
            double days = (DateTime.Now - local).TotalDays;
            if (days < 45) return AgoText(local);
            int months = (int)Math.Round(days / 30.44);
            if (months < 24) return string.Format(Loc("LOC_KX_MonthsAgo", "{0} months ago"), months);
            return string.Format(Loc("LOC_KX_YearsAgo", "{0} years ago"), (int)Math.Floor(days / 365.25));
        }

        /// <summary>From BuildStoragePage: the SUGGESTIONS rows (up to 12) and a "could free" figure.</summary>
        private void AddStorageSuggestions(List<Game> installed)
        {
            List<StorageTip> tips;
            try { tips = StorageTipsFor(installed); } catch (Exception ex) { logger.Warn("Kippix: storage tips: " + ex.Message); return; }
            if (tips.Count == 0) return;
            long total = tips.Sum(t => t.Bytes);
            if (PageStats != null) PageStats.Add(new PageStat { Value = SizeText(total), Label = Loc("LOC_KX_TipStatFree", "you could free up") });
            AddPageHeader(string.Format(Loc("LOC_KX_TipHeader", "SUGGESTIONS · FREE UP {0}"), SizeText(total)).ToUpper(CultureInfo.CurrentCulture));
            foreach (var t in tips.Take(12))
            {
                var game = t.Game;
                PageRows.Add(new PageRow
                {
                    Text = game.Name, ImagePath = CoverOf(game.Id), Key = "tip:" + game.Id, Sub = t.Why,
                    Right = Loc("LOC_KX_TipUninstall", "A  Uninstall"),
                    Command = new RelayCommand(_ => Confirm(string.Format(Loc("LOC_KX_UninstallTitle", "Uninstall {0}?"), game.Name),
                                                            Loc("LOC_KX_UninstallText", "Playnite asks the game's own launcher to remove it. Your saves and play time stay."),
                                                            Loc("LOC_KX_Uninstall", "Uninstall"),
                                                            () => { try { api.UninstallGame(game.Id); } catch (Exception ex) { logger.Warn("Kippix: uninstall: " + ex.Message); } }))
                });
            }
            if (tips.Count > 12)
                PageRows.Add(new PageRow { Text = string.Format(Loc("LOC_KX_TipMore", "{0} more suggestions"), tips.Count - 12), Sub = Loc("LOC_KX_TipMoreSub", "Uninstall some of these and the next ones come up"), Icon = "info", Key = "tipmore" });
        }
    }
}

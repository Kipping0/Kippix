using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>When a game was finished (completion.json in the add-on's data folder; one entry per game).</summary>
    public class CompletionEntry
    {
        public string GameId;
        public string Name;
        public string Kind;        // "beaten", "100" or "dropped"
        public DateTime? At;       // null: it already had that status when Kippix started keeping dates
        public string Status;      // the Playnite status's name then
    }

    /// <summary>
    /// Completion tracker. Playnite keeps one completion status per game (the names are yours: Beaten, Completed, Abandoned…);
    /// this remembers the day a game's status changes to a finished one (Beaten, 100% or Dropped, worked out from the status
    /// names, or the statuses picked in Profile tabs › FINISHED GAMES), from anywhere (the game page's Status and rating,
    /// Playnite's own menus, other add-ons). Games already finished before that get an entry with no date.
    /// Shown on the profile (Finished this year shelf and counts) and in Your year; the game page's Status and rating page has
    /// quick Beaten / 100% / Dropped choices (a missing status is added to Playnite's list the first time it's picked).
    /// </summary>
    public partial class ThemeApi
    {
        internal static readonly string[] CompletionKinds = { "beaten", "100", "dropped" };
        private readonly object completionLock = new object();
        private Dictionary<string, CompletionEntry> completion;
        private bool completionHooked, completionReconciled;

        private string CompletionFile { get { return Path.Combine(plugin.GetPluginUserDataPath(), "completion.json"); } }

        private string CompletionKindName(string kind)
        {
            switch (kind)
            {
                case "100": return Loc("LOC_KX_Done100", "100%");
                case "dropped": return Loc("LOC_KX_DoneDropped", "Dropped");
            }
            return Loc("LOC_KX_DoneBeaten", "Beaten");
        }

        /// <summary>Starts listening for status changes (once the library is in). Nothing runs on its own: it's Playnite's update event.</summary>
        private void HookCompletion()
        {
            if (completionHooked) return;
            completionHooked = true;
            try { api.Database.Games.ItemUpdated += OnGamesUpdatedForCompletion; }
            catch (Exception ex) { logger.Warn("Kippix: completion tracker: " + ex.Message); }
        }

        private void OnGamesUpdatedForCompletion(object sender, ItemUpdatedEventArgs<Game> e)
        {
            try
            {
                if (e == null || e.UpdatedItems == null) return;
                bool changed = false;
                foreach (var u in e.UpdatedItems)
                {
                    if (u == null || u.NewData == null) continue;
                    Guid was = u.OldData != null ? u.OldData.CompletionStatusId : Guid.Empty, now = u.NewData.CompletionStatusId;
                    if (was == now) continue;
                    if (NoteCompletion(u.NewData, KindOfStatus(now), DateTime.Now)) changed = true;
                }
                if (changed) SaveCompletionSoon();
            }
            catch (Exception ex) { logger.Warn("Kippix: completion tracker: " + ex.Message); }
        }

        /// <summary>Records (or takes away) a game's finished state. True when something changed.</summary>
        private bool NoteCompletion(Game g, string kind, DateTime? at)
        {
            if (g == null) return false;
            string id = g.Id.ToString();
            lock (completionLock)
            {
                EnsureCompletionLoaded();
                CompletionEntry had;
                completion.TryGetValue(id, out had);
                if (kind == null)
                {
                    if (had == null) return false;
                    completion.Remove(id);
                    return true;
                }
                if (had != null && had.Kind == kind) return false;
                string status = "";
                try { var cs = g.CompletionStatus; if (cs != null) status = cs.Name ?? ""; } catch (Exception) { }
                completion[id] = new CompletionEntry { GameId = id, Name = g.Name ?? "", Kind = kind, At = at, Status = status };
                return true;
            }
        }

        private void EnsureCompletionLoaded()
        {
            if (completion != null) return;
            completion = new Dictionary<string, CompletionEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CompletionFile)) return;
                var list = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<CompletionEntry>>(File.ReadAllText(CompletionFile));
                if (list == null) return;
                foreach (var c in list)
                {
                    if (c == null || string.IsNullOrEmpty(c.GameId)) continue;
                    if (c.At.HasValue && c.At.Value.Kind == DateTimeKind.Utc) c.At = c.At.Value.ToLocalTime();
                    completion[c.GameId] = c;
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: completion.json unreadable: " + ex.Message);
                try { File.Copy(CompletionFile, CompletionFile + ".unreadable", true); } catch (Exception) { }
            }
        }

        private int completionSaveGen;

        /// <summary>Written off the UI thread (a moment later, so a batch of changes is one write).</summary>
        private void SaveCompletionSoon()
        {
            int gen = Interlocked.Increment(ref completionSaveGen);
            KippixShared.SafeWork.Queue(_ =>
            {
                Thread.Sleep(400);
                if (gen != completionSaveGen) return;
                try
                {
                    string json;
                    lock (completionLock)
                    {
                        if (completion == null) return;
                        json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(completion.Values.ToList());
                    }
                    string file = CompletionFile, tmp = file + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(tmp, json);
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                }
                catch (Exception ex) { logger.Warn("Kippix: saving completion.json: " + ex.Message); }
            });
        }

        /// <summary>
        /// Once a session (off the UI thread): games already finished get an entry with no date; entries whose game isn't finished
        /// any more go; a game whose status changed while Kippix wasn't watching keeps the new kind with no date.
        /// </summary>
        private void ReconcileCompletion()
        {
            if (completionReconciled) return;
            completionReconciled = true;
            try
            {
                bool changed = false;
                var games = api.Database.Games.ToList();
                lock (completionLock)
                {
                    EnsureCompletionLoaded();
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in games)
                    {
                        if (g == null) continue;
                        string id = g.Id.ToString();
                        ids.Add(id);
                        string kind = KindOfStatus(g.CompletionStatusId);
                        CompletionEntry had;
                        completion.TryGetValue(id, out had);
                        if (kind == null) { if (had != null) { completion.Remove(id); changed = true; } continue; }
                        if (had != null && had.Kind == kind) continue;
                        string status = "";
                        try { var cs = g.CompletionStatus; if (cs != null) status = cs.Name ?? ""; } catch (Exception) { }
                        completion[id] = new CompletionEntry { GameId = id, Name = g.Name ?? "", Kind = kind, At = null, Status = status };
                        changed = true;
                    }
                    foreach (var gone in completion.Keys.Where(k => !ids.Contains(k)).ToList()) { completion.Remove(gone); changed = true; }
                }
                if (changed) SaveCompletionSoon();
            }
            catch (Exception ex) { logger.Warn("Kippix: completion back-fill: " + ex.Message); }
        }

        private List<CompletionEntry> CompletionSnapshot()
        {
            lock (completionLock)
            {
                EnsureCompletionLoaded();
                return completion.Values.Select(c => new CompletionEntry { GameId = c.GameId, Name = c.Name, Kind = c.Kind, At = c.At, Status = c.Status }).ToList();
            }
        }

        private CompletionEntry CompletionOf(Guid gameId)
        {
            lock (completionLock)
            {
                EnsureCompletionLoaded();
                CompletionEntry c;
                return completion.TryGetValue(gameId.ToString(), out c) ? c : null;
            }
        }

        // ---------------- which status is which ----------------

        private string CompletionSetting(string kind)
        {
            string v = kind == "100" ? settings.Completion100 : kind == "dropped" ? settings.CompletionDropped : settings.CompletionBeaten;
            return v ?? "";
        }

        /// <summary>A status name's kind: "100" (completed, 100%, perfect…), "beaten" (beaten, finished…), "dropped" (abandoned…), or null.</summary>
        internal static string NameKind(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n.Length == 0 || n.Contains("incomplet") || n.Contains("not ") || n.StartsWith("un") || n.Contains("plan") || n.Contains("to play") || n.Contains("playing")) return null;
            if (n.Contains("100") || n.Contains("complet") || n.Contains("perfect") || n.Contains("master") || n.Contains("platinum") || n.Contains("all achievements")) return "100";
            if (n.Contains("beat") || n.Contains("finish") || n.Contains("clear") || n == "done" || n.Contains("played through") || n.Contains("rolled credits")) return "beaten";
            if (n.Contains("abandon") || n.Contains("drop") || n.Contains("gave up") || n.Contains("quit") || n.Contains("shelved") || n.Contains("retired")) return "dropped";
            return null;
        }

        /// <summary>The finished kind a Playnite status stands for (the status picked for a kind wins over the names), or null.</summary>
        private string KindOfStatus(Guid statusId)
        {
            if (statusId == Guid.Empty) return null;
            string sid = statusId.ToString();
            foreach (string k in CompletionKinds) if (string.Equals(CompletionSetting(k), sid, StringComparison.OrdinalIgnoreCase)) return k;
            string name = null;
            try { var cs = api.Database.CompletionStatuses.Get(statusId); if (cs != null) name = cs.Name; } catch (Exception) { }
            string kind = NameKind(name);
            if (kind != null && CompletionSetting(kind).Length > 0 && StatusExists(CompletionSetting(kind))) return null;   // that kind is another status of yours
            return kind;
        }

        private bool StatusExists(string id)
        {
            Guid g;
            if (!Guid.TryParse(id ?? "", out g)) return false;
            try { return api.Database.CompletionStatuses.Get(g) != null; } catch (Exception) { return false; }
        }

        /// <summary>The status a quick choice sets: the one picked for it, else the first with a fitting name (Playnite's own names first).</summary>
        private CompletionStatus StatusForKind(string kind)
        {
            try
            {
                Guid set;
                if (Guid.TryParse(CompletionSetting(kind), out set))
                {
                    var cs = api.Database.CompletionStatuses.Get(set);
                    if (cs != null) return cs;
                }
                string prefer = kind == "100" ? "completed" : kind == "dropped" ? "abandoned" : "beaten";
                var all = api.Database.CompletionStatuses.ToList();
                return all.FirstOrDefault(c => c != null && string.Equals((c.Name ?? "").Trim(), prefer, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(c => c != null && NameKind(c.Name) == kind);
            }
            catch (Exception) { return null; }
        }

        // ---------------- the game page's Status and rating: quick choices ----------------

        /// <summary>Top of the Status and rating page: Beaten / 100% / Dropped (A again takes it off).</summary>
        private void AddCompletionQuickRows(Game g)
        {
            if (g == null) return;
            GameStatusRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_DoneHeader", "FINISHED IT?") });
            string nowKind = KindOfStatus(g.CompletionStatusId);
            var known = CompletionOf(g.Id);
            foreach (string k in CompletionKinds)
            {
                string kind = k;
                var cs = StatusForKind(kind);
                string sub = cs != null ? string.Format(Loc("LOC_KX_DoneSets", "Sets Playnite's “{0}” status"), cs.Name)
                                        : string.Format(Loc("LOC_KX_DoneAdds", "Adds a “{0}” status to Playnite's list"), NewStatusName(kind));
                if (nowKind == kind && known != null && known.Kind == kind && known.At.HasValue)
                    sub = string.Format(Loc("LOC_KX_DoneSince", "Since {0}"), known.At.Value.ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
                GameStatusRows.Add(new PickRow
                {
                    Text = CompletionKindName(kind), Sub = sub, Key = "q:" + kind, IsSelected = nowKind == kind,
                    Icon = kind == "100" ? "trophy" : kind == "dropped" ? "remove" : "flag",
                    Command = new RelayCommand(_ => SetCompletionQuick(kind))
                });
            }
        }

        private string NewStatusName(string kind)
        {
            return kind == "100" ? "Completed" : kind == "dropped" ? "Dropped" : "Beaten";
        }

        private void SetCompletionQuick(string kind)
        {
            var g = PanelGame();
            if (g == null) return;
            try
            {
                if (KindOfStatus(g.CompletionStatusId) == kind) g.CompletionStatusId = Guid.Empty;   // pressing the one that's set takes it off
                else
                {
                    var cs = StatusForKind(kind);
                    if (cs == null)
                    {
                        // you don't have one: it's added to Playnite's statuses (and remembered as this kind)
                        cs = new CompletionStatus(NewStatusName(kind));
                        api.Database.CompletionStatuses.Add(cs);
                        if (kind == "100") settings.Completion100 = cs.Id.ToString();
                        else if (kind == "dropped") settings.CompletionDropped = cs.Id.ToString();
                        else settings.CompletionBeaten = cs.Id.ToString();
                        SaveSettings();
                        logger.Info("Kippix: added the completion status " + cs.Name);
                    }
                    g.CompletionStatusId = cs.Id;
                }
                api.Database.Games.Update(g);   // (the update event records the date)
                PlaySound("select");
            }
            catch (Exception ex) { logger.Warn("Kippix: quick status: " + ex.Message); }
            RefreshStatusRows("q:" + kind);
        }

        // ---------------- Profile tabs › FINISHED GAMES: which status means what ----------------

        private void AddCompletionSettingRows()
        {
            AddPageHeader(Loc("LOC_KX_DoneSettingsHeader", "FINISHED GAMES"));
            List<CompletionStatus> all;
            try { all = api.Database.CompletionStatuses.OrderBy(c => c.Name).ToList(); } catch (Exception) { all = new List<CompletionStatus>(); }
            foreach (string k in CompletionKinds)
            {
                string kind = k;
                Guid set;
                CompletionStatus chosen = Guid.TryParse(CompletionSetting(kind), out set) ? all.FirstOrDefault(c => c.Id == set) : null;
                string right;
                if (chosen != null) right = chosen.Name;
                else
                {
                    var auto = all.Where(c => NameKind(c.Name) == kind).Select(c => c.Name).ToList();
                    right = auto.Count > 0 ? string.Format(Loc("LOC_KX_DoneAuto", "Automatic: {0}"), string.Join(", ", auto.ToArray()))
                                           : Loc("LOC_KX_DoneAutoNone", "Automatic (none yet)");
                }
                PageRows.Add(new PageRow
                {
                    Key = "done:" + kind, Icon = kind == "100" ? "trophy" : kind == "dropped" ? "remove" : "flag",
                    Text = string.Format(Loc("LOC_KX_DoneMeans", "{0} means"), CompletionKindName(kind)),
                    Sub = Loc("LOC_KX_DoneMeansSub", "Pick another of your Playnite statuses (Automatic goes by the names)"),
                    Right = right,
                    Command = new RelayCommand(_ =>
                    {
                        // Automatic → each status in turn → Automatic
                        var list = all.Select(c => c.Id.ToString()).ToList();
                        string cur = CompletionSetting(kind);
                        int i = list.IndexOf(cur);
                        string next = i < 0 ? (list.Count > 0 ? list[0] : "") : i + 1 < list.Count ? list[i + 1] : "";
                        if (kind == "100") settings.Completion100 = next;
                        else if (kind == "dropped") settings.CompletionDropped = next;
                        else settings.CompletionBeaten = next;
                        SaveSettings();
                        completionReconciled = false;   // (the profile works the finished games out again)
                        PlaySound("move");
                        RebuildHubTabsPage("done:" + kind);
                    })
                });
            }
        }

        // ---------------- on the profile: Finished this year ----------------

        /// <summary>What the finished games shelf shows (worked out off the UI thread).</summary>
        private class FinishedInfo
        {
            public int Beaten, Hundred, Dropped, Undated;
            public List<CompletionEntry> Shelf = new List<CompletionEntry>();
        }

        /// <summary>(from a worker)</summary>
        private FinishedInfo FinishedIn(int year, bool ownerView)
        {
            var info = new FinishedInfo();
            try
            {
                ReconcileCompletion();
                foreach (var c in CompletionSnapshot())
                {
                    Guid id;
                    if (!Guid.TryParse(c.GameId, out id)) continue;
                    Game g = null;
                    try { g = api.Database.Games.Get(id); } catch (Exception) { }
                    if (g == null || !HubSees(g, ownerView)) continue;
                    c.Name = g.Name ?? c.Name;
                    if (!c.At.HasValue) { if (c.Kind != "dropped") info.Undated++; continue; }
                    if (c.At.Value.Year != year) continue;
                    if (c.Kind == "100") info.Hundred++;
                    else if (c.Kind == "dropped") info.Dropped++;
                    else info.Beaten++;
                    if (c.Kind != "dropped") info.Shelf.Add(c);
                }
                info.Shelf = info.Shelf.OrderByDescending(c => c.At.Value).Take(16).ToList();
            }
            catch (Exception ex) { logger.Warn("Kippix: finished games: " + ex.Message); }
            return info;
        }

        public ObservableCollection<PageTile> HubShelfTiles { get; private set; }
        public string HubShelfTitle { get; private set; }
        public string HubShelfLine { get; private set; }
        public bool HasHubShelf { get; private set; }
        public bool HasHubShelfLine { get { return !string.IsNullOrEmpty(HubShelfLine); } }
        public bool HasHubShelfTiles { get { return HubShelfTiles != null && HubShelfTiles.Count > 0; } }

        private void ClearHubShelf()
        {
            if (HubShelfTiles == null) HubShelfTiles = new ObservableCollection<PageTile>();
            if (!HasHubShelf && HubShelfTiles.Count == 0) return;
            HubShelfTiles.Clear();
            HasHubShelf = false;
            HubShelfTitle = HubShelfLine = "";
            NotifyHubShelf();
        }

        private void NotifyHubShelf()
        {
            foreach (string n in new[] { "HubShelfTiles", "HubShelfTitle", "HubShelfLine", "HasHubShelf", "HasHubShelfLine", "HasHubShelfTiles" }) Notify(n);
        }

        /// <summary>Puts the Finished this year (or in that year) shelf on the page.</summary>
        private void ShowFinishedShelf(FinishedInfo f, int year)
        {
            ClearHubShelf();
            if (f == null || (f.Beaten + f.Hundred + f.Dropped == 0 && f.Undated == 0)) return;
            HubShelfTitle = year == DateTime.Now.Year ? Loc("LOC_KX_DoneThisYear", "FINISHED THIS YEAR") : string.Format(Loc("LOC_KX_DoneInYear", "FINISHED IN {0}"), year);
            var bits = new List<string>
            {
                string.Format(Loc("LOC_KX_DoneNBeaten", "{0} beaten"), f.Beaten),
                string.Format(Loc("LOC_KX_DoneN100", "{0} at 100%"), f.Hundred),
                string.Format(Loc("LOC_KX_DoneNDropped", "{0} dropped"), f.Dropped)
            };
            HubShelfLine = string.Join("   ·   ", bits.ToArray());
            if (f.Undated > 0)
                HubShelfLine += "   ·   " + string.Format(f.Undated == 1 ? Loc("LOC_KX_DoneUndatedOne", "1 more finished before Kippix kept dates")
                                                                       : Loc("LOC_KX_DoneUndated", "{0} more finished before Kippix kept dates"), f.Undated);
            int gen = pageGen;
            var tiles = new List<PageTile>();
            foreach (var c in f.Shelf)
            {
                Guid id;
                if (!Guid.TryParse(c.GameId, out id)) continue;
                var gid = id;
                var t = new PageTile
                {
                    Name = c.Name, GameId = id, CoverPath = CoverOf(id),
                    Sub = CompletionKindName(c.Kind) + "   ·   " + c.At.Value.ToString("d MMM", CultureInfo.CurrentCulture),
                    Command = new RelayCommand(_ => OpenGameFromPage(gid))
                };
                HubShelfTiles.Add(t);
                tiles.Add(t);
            }
            HasHubShelf = true;
            NotifyHubShelf();
            if (tiles.Count == 0) return;
            KippixShared.SafeWork.Queue(_ =>
            {
                foreach (var t in tiles)
                {
                    if (gen != pageGen) return;
                    if (string.IsNullOrEmpty(t.CoverPath)) continue;
                    ImageSource img = null;
                    try { img = Images.Load(t.CoverPath, 240); } catch (Exception) { }
                    if (img != null) { var tt = t; OnUi(() => tt.Cover = img); }
                }
            });
        }
    }
}

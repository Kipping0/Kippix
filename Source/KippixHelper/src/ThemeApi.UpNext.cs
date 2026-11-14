using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Events;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Up next: a queue of games you mean to play (each profile has its own: settings.UpNext is personal data). Added from a
    /// game's page (⋯ More › Add to Up next) or from the suggestions; shown as the profile's Up next tab (a page), an extra home
    /// row ("upnext") and a home tile. On the page: A opens the game, X picks a game up to move it (Up / Down, then A, X or B to put
    /// it down), Y takes it off (Undo comes up). Suggestions, worked out off the UI thread when the page opens: "Finish in under
    /// 5 hours" (HowLongToBeat's main story minus what you've played, from the HowLongToBeat add-on's files) and "Started but not
    /// finished" (played, not Beaten / Completed).
    /// </summary>
    public partial class ThemeApi
    {
        private const string HltbAddonId = "e08cd51f-9c9a-4ee3-a094-fde03b55492f";
        private string upNextMoving;                 // the game being moved (X), null: none
        private List<string> upNextUndo;             // the queue before the last game was taken off
        private string upNextUndoName;
        private readonly Dictionary<Guid, double> hltbStoryHours = new Dictionary<Guid, double>();   // main story hours (0: none known), this session

        private List<string> UpNextIds
        {
            get { if (settings.UpNext == null) settings.UpNext = new List<string>(); return settings.UpNext; }
        }

        private bool InUpNext(Guid id) { return UpNextIds.Contains(id.ToString()); }

        /// <summary>Finished: Beaten / Completed (the statuses chosen in the settings, or worked out from the names).</summary>
        private bool UpNextFinished(Game g)
        {
            if (g == null) return false;
            try
            {
                string id = g.CompletionStatusId.ToString();
                if (!string.IsNullOrEmpty(settings.CompletionBeaten) && id == settings.CompletionBeaten) return true;
                if (!string.IsNullOrEmpty(settings.Completion100) && id == settings.Completion100) return true;
                var cs = g.CompletionStatus;
                string n = cs != null && cs.Name != null ? cs.Name.Trim().ToLowerInvariant() : "";
                if (n.Length == 0 || n.StartsWith("not ", StringComparison.Ordinal) || n.StartsWith("un", StringComparison.Ordinal)) return false;
                return n.Contains("beaten") || n.Contains("completed") || n.Contains("finished") || n.Contains("100%") || n.Contains("mastered") || n.Contains("platinum");
            }
            catch (Exception) { return false; }
        }

        /// <summary>Given up on (Abandoned / Dropped): not "started but not finished".</summary>
        private bool UpNextDropped(Game g)
        {
            try
            {
                if (!string.IsNullOrEmpty(settings.CompletionDropped) && g.CompletionStatusId.ToString() == settings.CompletionDropped) return true;
                var cs = g.CompletionStatus;
                string n = cs != null && cs.Name != null ? cs.Name.ToLowerInvariant() : "";
                return n.Contains("abandon") || n.Contains("dropped") || n.Contains("gave up");
            }
            catch (Exception) { return false; }
        }

        // =====================================================================
        // Adding and taking off
        // =====================================================================

        /// <summary>The game's More menu row (before the profile's favourite).</summary>
        private void AddUpNextRow(Game g)
        {
            if (g == null) return;
            var game = g;
            int at = UpNextIds.IndexOf(g.Id.ToString());
            GameMoreRows.Add(new PickRow
            {
                Text = at >= 0 ? Loc("LOC_KX_UpNextRemove", "Take off Up next") : Loc("LOC_KX_UpNextAdd", "Add to Up next"),
                Sub = at >= 0 ? string.Format(Loc("LOC_KX_UpNextPos", "Number {0} in your Up next"), at + 1) : Loc("LOC_KX_UpNextAddSub", "Your queue of games to play next (profile › Up next)"),
                Icon = "forward", Key = "upnext",
                Command = new RelayCommand(_ => { ClosePanels(); ToggleUpNext(game); })
            });
        }

        private void ToggleUpNext(Game g)
        {
            if (g == null) return;
            string id = g.Id.ToString();
            bool on = UpNextIds.Contains(id);
            if (on) UpNextIds.Remove(id); else UpNextIds.Add(id);
            UpNextChanged();
            PlaySound(on ? "back" : "select");
            try { ShowCard(Loc("LOC_KX_PageUpNext", "Up next").ToUpper(CultureInfo.CurrentCulture), g.Name,
                           on ? Loc("LOC_KX_UpNextTakenOff", "Taken off Up next") : string.Format(Loc("LOC_KX_UpNextAddedN", "Added: number {0}"), UpNextIds.Count), "", null); } catch (Exception) { }
        }

        private void UpNextChanged()
        {
            SaveSettings();
            try { RebuildExtraRowsSoon(); } catch (Exception) { }
            var tile = HomeTiles != null ? HomeTiles.FirstOrDefault(t => t.IsUpNext) : null;
            if (tile != null) tile.Sub = UpNextTileSub();
        }

        // =====================================================================
        // The home tile
        // =====================================================================

        private HomeTile UpNextTile()
        {
            return new HomeTile
            {
                Key = "upnext", Name = Loc("LOC_KX_PageUpNext", "Up next"), Sub = UpNextTileSub(),
                Command = new RelayCommand(_ => { LeaveTilesForPanel(); OpenPageFromShortcut("upnext"); })
            };
        }

        private string UpNextTileSub()
        {
            Game first = null;
            foreach (string s in UpNextIds)
            {
                Guid id;
                if (!Guid.TryParse(s, out id)) continue;
                try { first = api.Database.Games.Get(id); } catch (Exception) { }
                if (first != null && ProfileAllowsGame(first)) break;
                first = null;
            }
            return first != null ? first.Name : Loc("LOC_KX_UpNextTileEmpty", "Games to play next");
        }

        // =====================================================================
        // The page (the profile's Up next tab)
        // =====================================================================

        private List<Game> UpNextGames()
        {
            var list = new List<Game>();
            foreach (string s in UpNextIds.ToList())
            {
                Guid id;
                if (!Guid.TryParse(s, out id)) continue;
                Game g = null;
                try { g = api.Database.Games.Get(id); } catch (Exception) { }
                if (g != null && ProfileAllowsGame(g)) list.Add(g);
            }
            return list;
        }

        private void BuildUpNextPage()
        {
            upNextMoving = null;
            PageSub = Loc("LOC_KX_PageUpNextSub", "The games you mean to play next, and ones you could finish soon");
            PageHint = Loc("LOC_KX_UpNextHint", "X  Move      Y  Take off");
            AddPageChip(Loc("LOC_KX_UpNextAddGame", "Add a game"), "add", () => OpenSearchToPick(id =>
            {
                string s = id.ToString();
                if (!UpNextIds.Contains(s)) { UpNextIds.Add(s); UpNextChanged(); }
                ui.BeginInvoke(new Action(() => { pageQuietOpen = true; OpenProfileHub("upnext", null); }), DispatcherPriority.Loaded);
            }));
            if (upNextUndo != null)
                AddPageChip(string.Format(Loc("LOC_KX_UpNextUndo", "Undo: put {0} back"), upNextUndoName ?? ""), "refresh", UndoUpNext);

            var games = UpNextGames();
            if (games.Count > 0) AddPageHeader(Loc("LOC_KX_UpNextHeader", "UP NEXT"));
            int n = 0;
            foreach (var g in games)
            {
                var gid = g.Id;
                n++;
                PageRows.Add(new PageRow
                {
                    Text = g.Name, Key = "q:" + gid, ImagePath = CoverOf(gid), Right = "#" + n,
                    Sub = UpNextFinished(g) ? Loc("LOC_KX_UpNextDone", "Finished: Y takes it off")
                        : g.IsInstalled ? Loc("LOC_KX_UpNextInstalled", "Installed") : Loc("LOC_KX_UpNextNotInstalled", "Not installed"),
                    Command = new RelayCommand(_ => { if (upNextMoving != null) { DropUpNext(); return; } OpenGameFromPage(gid); })
                });
            }
            SetPageStatus(games.Count == 0 ? Loc("LOC_KX_UpNextEmpty", "Nothing up next yet. On a game's page: ⋯ More › Add to Up next, or choose a suggestion below.") : "");
            LoadPageImages();
            FillUpNextSuggestions(games.Select(g => g.Id).ToList());
        }

        /// <summary>HowLongToBeat main story (hours) for these games, from the add-on's own files (only games it has a file for).</summary>
        private Dictionary<Guid, double> HltbStoryFor(IEnumerable<Game> games)
        {
            var result = new Dictionary<Guid, double>();
            string dir;
            try { dir = Path.Combine(Path.Combine(api.Paths.ExtensionsDataPath, HltbAddonId), "HowLongToBeat"); } catch (Exception) { return result; }
            HashSet<string> have;
            try { have = Directory.Exists(dir) ? new HashSet<string>(Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase) : null; }
            catch (Exception) { have = null; }
            if (have == null) return null;   // the add-on isn't there
            string ext = api.Paths.ExtensionsDataPath;
            foreach (var g in games)
            {
                double h;
                lock (hltbStoryHours) if (hltbStoryHours.TryGetValue(g.Id, out h)) { if (h > 0) result[g.Id] = h; continue; }
                h = 0;
                if (have.Contains(g.Id.ToString()))
                {
                    try { var t = KippixOverlay.GameData.LoadHltb(ext, g.Id); if (t != null) h = t.Story > 0 ? t.Story : 0; } catch (Exception) { }
                }
                lock (hltbStoryHours) hltbStoryHours[g.Id] = h;
                if (h > 0) result[g.Id] = h;
            }
            return result;
        }

        /// <summary>What this profile has played of each game (seconds): Playnite's for the main profile, its own sessions for anyone else.</summary>
        private Dictionary<Guid, ulong> PlayedByProfile(List<Game> all, bool owner, string profileId)
        {
            var d = new Dictionary<Guid, ulong>();
            if (owner) { foreach (var g in all) if (g.Playtime > 0) d[g.Id] = g.Playtime; return d; }
            foreach (var s in OwnSessions(3650).Where(x => x.Profile == profileId && !string.IsNullOrEmpty(x.GameId)))
            {
                Guid id;
                if (!Guid.TryParse(s.GameId, out id)) continue;
                ulong was;
                d.TryGetValue(id, out was);
                d[id] = was + (ulong)Math.Max(0, s.Seconds);
            }
            return d;
        }

        private void FillUpNextSuggestions(List<Guid> queued)
        {
            int gen = pageGen;
            bool owner = IsOwnerProfile;
            string pid = CurrentProfileId;
            var queue = new HashSet<Guid>(queued);
            KippixShared.SafeWork.Queue(_ =>
            {
                var shortOnes = new List<Tuple<Game, double, double>>();   // game, hours left, HowLongToBeat hours
                var started = new List<Game>();
                var queueInfo = new Dictionary<Guid, string>();
                bool noHltb = false;
                try
                {
                    var all = api.Database.Games.Where(g => g != null && !g.Hidden && ProfileAllowsGame(g)).ToList();
                    var played = PlayedByProfile(all, owner, pid);
                    var open = all.Where(g => !UpNextFinished(g)).ToList();
                    var hltb = HltbStoryFor(open.Where(g => queue.Contains(g.Id) || !UpNextDropped(g)));
                    noHltb = hltb == null;
                    if (hltb == null) hltb = new Dictionary<Guid, double>();
                    foreach (var g in open)
                    {
                        if (gen != pageGen) return;
                        ulong secs;
                        played.TryGetValue(g.Id, out secs);
                        double h;
                        bool known = hltb.TryGetValue(g.Id, out h);
                        double left = known ? h - secs / 3600.0 : -1;
                        if (queue.Contains(g.Id))
                        {
                            var bits = new List<string>();
                            if (known) bits.Add(left > 0.25 ? string.Format(Loc("LOC_KX_UpNextAboutLeft", "about {0} to beat"), HoursShort(left)) : Loc("LOC_KX_UpNextNearlyDone", "nearly beaten"));
                            if (secs > 0) bits.Add(string.Format(Loc("LOC_KX_UpNextPlayed", "you've played {0}"), HoursShort(secs / 3600.0)));
                            if (bits.Count > 0) queueInfo[g.Id] = string.Join(" · ", bits.ToArray());
                            continue;
                        }
                        if (UpNextDropped(g)) continue;
                        if (known && left > 0.1 && left <= 5) shortOnes.Add(Tuple.Create(g, left, h));
                        else if (secs > 0) started.Add(g);
                    }
                    shortOnes = shortOnes.OrderBy(t => t.Item2).Take(10).ToList();
                    // the ones played last first: Playnite's dates for the main profile, the profile's own sessions for anyone else
                    var last = new Dictionary<string, DateTime>();
                    if (!owner)
                        foreach (var s in OwnSessions(3650).Where(x => x.Profile == pid && !string.IsNullOrEmpty(x.GameId)))
                        {
                            DateTime end = s.Start.AddSeconds(s.Seconds), was;
                            if (!last.TryGetValue(s.GameId, out was) || end > was) last[s.GameId] = end;
                        }
                    started = started.OrderByDescending(g =>
                    {
                        if (owner) return g.LastActivity ?? DateTime.MinValue;
                        DateTime d;
                        return last.TryGetValue(g.Id.ToString(), out d) ? d : DateTime.MinValue;
                    }).Take(10).ToList();
                    var playedCopy = played;
                    OnUi(() =>
                    {
                        if (gen != pageGen || pageKey != "upnext") return;
                        foreach (var r in PageRows.ToList())
                        {
                            if (r.Key == null || !r.Key.StartsWith("q:", StringComparison.Ordinal)) continue;
                            Guid id;
                            string info;
                            if (Guid.TryParse(r.Key.Substring(2), out id) && queueInfo.TryGetValue(id, out info)) r.SetSub(r.Sub + "   ·   " + info);
                        }
                        if (shortOnes.Count > 0)
                        {
                            AddPageHeader(Loc("LOC_KX_UpNextShort", "FINISH IN UNDER 5 HOURS"));
                            foreach (var t in shortOnes)
                            {
                                ulong secs;
                                playedCopy.TryGetValue(t.Item1.Id, out secs);
                                AddSuggestionRow(t.Item1, secs > 0
                                    ? string.Format(Loc("LOC_KX_UpNextShortPlayed", "About {0} left (HowLongToBeat {1}, you've played {2})"), HoursShort(t.Item2), HoursShort(t.Item3), HoursShort(secs / 3600.0))
                                    : string.Format(Loc("LOC_KX_UpNextShortNew", "About {0} (HowLongToBeat main story)"), HoursShort(t.Item3)));
                            }
                        }
                        if (started.Count > 0)
                        {
                            AddPageHeader(Loc("LOC_KX_UpNextStarted", "STARTED BUT NOT FINISHED"));
                            foreach (var g in started)
                            {
                                ulong secs;
                                playedCopy.TryGetValue(g.Id, out secs);
                                AddSuggestionRow(g, string.Format(Loc("LOC_KX_UpNextPlayedCap", "You've played {0}"), HoursShort(secs / 3600.0))
                                                    + (g.CompletionStatus != null && !string.IsNullOrEmpty(g.CompletionStatus.Name) ? "  ·  " + g.CompletionStatus.Name : ""));
                            }
                        }
                        if (noHltb && PageRows.Count(r => r.IsRow) < 3)
                            SetPageStatus((PageStatus.Length > 0 ? PageStatus + "  " : "") + Loc("LOC_KX_UpNextNoHltb", "With the HowLongToBeat add-on, games you could finish in under 5 hours show here too."));
                        NotifyPageLists();
                        LoadPageImages();
                    });
                }
                catch (Exception ex) { logger.Warn("Kippix: up next suggestions: " + ex.Message); }
            });
        }

        private void AddSuggestionRow(Game g, string sub)
        {
            var game = g;
            PageRows.Add(new PageRow
            {
                Text = g.Name, Key = "s:" + g.Id, ImagePath = CoverOf(g.Id), Sub = sub, Right = Loc("LOC_KX_UpNextAddRight", "Add"),
                Command = new RelayCommand(_ =>
                {
                    if (upNextMoving != null) { DropUpNext(); return; }
                    string s = game.Id.ToString();
                    if (!UpNextIds.Contains(s)) UpNextIds.Add(s);
                    UpNextChanged();
                    PlaySound("select");
                    RebuildUpNextPage("q:" + game.Id);
                })
            });
        }

        private void RebuildUpNextPage(string focusKey)
        {
            if (openPanel != "page" || pageKey != "upnext") return;
            pageQuietOpen = true;
            if (IsProfileHub) OpenProfileHub("upnext", null); else OpenPage("upnext", "", false);
            if (focusKey == null) return;
            var row = PageRows.FirstOrDefault(r => r.Key == focusKey);
            int gen = pageGen;
            // (after the hub's own focus on its tab)
            ui.BeginInvoke(new Action(() => { if (gen == pageGen && row != null && PageRows.Contains(row)) UiTools.FocusItem("KX_PageRowList", row); }), DispatcherPriority.ApplicationIdle);
        }

        // ---------------- keys: X move, Y take off ----------------

        private bool UpNextPageKey(KeyEventArgs e, int step, bool xPressed, bool back)
        {
            var row = FocusedPageItem() as PageRow;
            bool queued = row != null && row.Key != null && row.Key.StartsWith("q:", StringComparison.Ordinal);
            bool y = false;
            try { y = PadButton(e) == ControllerInput.Y && !IsPadRelease(e); } catch (Exception) { }
            if (upNextMoving != null)
            {
                if (back || xPressed) { DropUpNext(); return true; }
                if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    MoveUpNext(e.Key == Key.Up ? -1 : 1);
                    return true;
                }
                if (e.Key == Key.Left || e.Key == Key.Right) return true;   // (stays on the row it carries)
                return false;
            }
            if (!queued) return false;
            if (xPressed)
            {
                upNextMoving = row.Key.Substring(2);
                row.SetRight(Loc("LOC_KX_UpNextMoving", "Moving  ↕"));
                PageHint = Loc("LOC_KX_UpNextMoveHint", "Up / Down  Move      A  Put it here");
                Notify("PageHint");
                Notify("HasHubHint");
                PlaySound("select");
                return true;
            }
            if (y || e.Key == Key.Delete)
            {
                TakeOffUpNext(row.Key.Substring(2));
                return true;
            }
            return false;
        }

        private void MoveUpNext(int dir)
        {
            var ids = UpNextIds;
            // the queue's rows on screen (games this profile doesn't see aren't there: they're stepped over)
            var qrows = PageRows.Where(r => r.Key != null && r.Key.StartsWith("q:", StringComparison.Ordinal)).ToList();
            int k = qrows.FindIndex(r => r.Key == "q:" + upNextMoving);
            if (k < 0 || !ids.Contains(upNextMoving)) { DropUpNext(); return; }
            int t = k + dir;
            if (t < 0 || t >= qrows.Count) { PlaySound("back"); return; }
            var row = qrows[k];
            var neighbour = qrows[t];
            string nid = neighbour.Key.Substring(2);
            ids.Remove(upNextMoving);
            int ni = ids.IndexOf(nid);
            if (ni < 0) ni = dir < 0 ? 0 : ids.Count - 1;
            ids.Insert(dir < 0 ? ni : ni + 1, upNextMoving);
            int a = PageRows.IndexOf(row), b = PageRows.IndexOf(neighbour);
            if (a >= 0 && b >= 0 && a != b) PageRows.Move(a, b);   // (it takes the neighbour's place; the neighbour steps round it)
            RenumberUpNextRows();
            PlaySound("move");
            int gen = pageGen;
            ui.BeginInvoke(new Action(() => { if (gen == pageGen && PageRows.Contains(row)) UiTools.FocusItem("KX_PageRowList", row); }), DispatcherPriority.Loaded);
        }

        private void RenumberUpNextRows()
        {
            int n = 0;
            foreach (var r in PageRows)
            {
                if (r.Key == null || !r.Key.StartsWith("q:", StringComparison.Ordinal)) continue;
                n++;
                if (r.Key.Substring(2) != upNextMoving) r.SetRight("#" + n);
            }
        }

        private void DropUpNext()
        {
            if (upNextMoving == null) return;
            upNextMoving = null;
            RenumberUpNextRows();
            var mine = FocusedPageItem() as PageRow;
            PageHint = Loc("LOC_KX_UpNextHint", "X  Move      Y  Take off");
            Notify("PageHint");
            Notify("HasHubHint");
            UpNextChanged();
            PlaySound("select");
            if (mine != null) UiTools.FocusItem("KX_PageRowList", mine);
        }

        private void TakeOffUpNext(string id)
        {
            var ids = UpNextIds;
            int i = ids.IndexOf(id);
            if (i < 0) return;
            upNextUndo = ids.ToList();
            Guid gid;
            Game g = null;
            if (Guid.TryParse(id, out gid)) try { g = api.Database.Games.Get(gid); } catch (Exception) { }
            upNextUndoName = g != null ? g.Name : "";
            // the highlight goes to the next game in the queue (or the one before)
            var rows = PageRows.Where(r => r.Key != null && r.Key.StartsWith("q:", StringComparison.Ordinal)).ToList();
            int at = rows.FindIndex(r => r.Key == "q:" + id);
            string next = at + 1 < rows.Count ? rows[at + 1].Key : at - 1 >= 0 ? rows[at - 1].Key : null;
            ids.RemoveAt(i);
            UpNextChanged();
            PlaySound("back");
            RebuildUpNextPage(next);
        }

        private void UndoUpNext()
        {
            if (upNextUndo == null) return;
            var before = upNextUndo;
            var now = UpNextIds;
            string back = before.FirstOrDefault(s => !now.Contains(s));
            settings.UpNext = before;
            upNextUndo = null;
            UpNextChanged();
            PlaySound("select");
            RebuildUpNextPage(back != null ? "q:" + back : null);
        }
    }
}

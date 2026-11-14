using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.91) Manual board › groups. Tiles can be grouped (a tile's settings › Group it with other tiles: then A on the tiles
    /// to take into the group, B done). A group's settings: Carousel. With it on, the group's game tiles show games from one
    /// list (Games from, which replaces what each tile was set to: those rows are hidden) in turn along the group, and moving
    /// past its end the way it goes (right, left or down) brings the next games in, like Row; back the other way at its start
    /// goes back. Loop: after the last game it starts again.
    /// </summary>
    public partial class ThemeApi
    {
        private string boardGrouping;   // editing: A puts tiles in / takes them out of this group
        private bool boardOrdering;     // (0.92) ... or, with this on, numbers them (Placement order)
        private readonly Dictionary<string, int> groupOffset = new Dictionary<string, int>();
        private Dictionary<string, List<Guid>> groupGames = new Dictionary<string, List<Guid>>();
        private string boardGroupEditId;   // the group whose settings are open
        private BoardGroup boardSourceGroup;   // Games from is open for a group (not a tile)

        private List<BoardGroup> MGroups { get { return settings.ManualGroups ?? (settings.ManualGroups = new List<BoardGroup>()); } }

        private BoardGroup GroupOf(BoardCard c)
        {
            if (c == null || string.IsNullOrEmpty(c.Group) || !IsManualMode) return null;
            return MGroups.FirstOrDefault(g => g != null && g.Id == c.Group);
        }

        /// <summary>A game tile in a group with Carousel on: its game comes from the group.</summary>
        private bool IsCarouselCard(BoardCard c)
        {
            var g = GroupOf(c);
            return g != null && g.Carousel && IsGameKind(c);
        }

        private string GroupName(BoardGroup g)
        {
            if (g == null) return "";
            if (!string.IsNullOrEmpty(g.Name)) return g.Name;
            int i = MGroups.IndexOf(g);
            return string.Format(Loc("LOC_KX_GroupN", "Group {0}"), i + 1);
        }

        private string GroupDirName(int d)
        {
            return d == 1 ? Loc("LOC_KX_DirLeft", "Left") : d == 2 ? Loc("LOC_KX_DirDown", "Down") : Loc("LOC_KX_DirRight", "Right");
        }

        // ---------------- the games ----------------

        /// <summary>The group's game tiles on this page, in the order its games go: tile i shows game (offset + i), so moving on,
        /// each game slides back one tile and the next one comes in on the last tile. (0.93) With a Placement order: tile 1 shows
        /// the first game, 2 the second...; moving on, 1 shows what 2 had, and so on. Tiles without a number come after those.</summary>
        private List<BoardTile> GroupTiles(BoardGroup g)
        {
            var tiles = BoardTiles.Where(t => t.Card != null && t.Card.Group == g.Id && IsGameKind(t.Card));
            List<BoardTile> along;
            if (g.Dir == 2) along = tiles.OrderBy(t => t.Card.Y).ThenBy(t => t.Card.X).ToList();
            else if (g.Dir == 1) along = tiles.OrderByDescending(t => t.Card.X).ThenBy(t => t.Card.Y).ToList();
            else along = tiles.OrderBy(t => t.Card.X).ThenBy(t => t.Card.Y).ToList();
            if (!along.Any(t => t.Card.GroupOrder > 0)) return along;
            var ordered = along.Where(t => t.Card.GroupOrder > 0).OrderBy(t => t.Card.GroupOrder).ToList();
            ordered.AddRange(along.Where(t => t.Card.GroupOrder <= 0));
            return ordered;
        }

        private bool GroupHasOrder(BoardGroup g) { return BCards.Any(c => c.Group == g.Id && c.GroupOrder > 0); }

        private void ClearGroupOrder(BoardGroup g)
        {
            foreach (var list in new[] { settings.ManualCards, settings.FreeCards })
                if (list != null) foreach (var c in list) if (c.Group == g.Id) c.GroupOrder = 0;
        }

        /// <summary>(0.92) Group settings › Placement order: on the board, A on the tiles in order: the first you press shows
        /// the carousel's first game, the next its second... A on a numbered tile takes its number off.</summary>
        private void StartGroupOrdering(BoardGroup g)
        {
            if (g == null) return;
            if (!GroupHasOrder(g)) ClearGroupOrder(g);
            boardOrdering = true;
            StartGrouping(g);
            if (boardGrouping == null) boardOrdering = false;
        }

        private void BoardOrderToggle(BoardTile t)
        {
            var c = t != null ? t.Card : null;
            if (c == null || t.IsSlot || c.Group != boardGrouping || !IsGameKind(c)) { PlaySound("back"); return; }
            if (c.GroupOrder > 0)
            {
                int was = c.GroupOrder;
                c.GroupOrder = 0;
                foreach (var o in BCards.Where(x => x.Group == boardGrouping && x.GroupOrder > was)) o.GroupOrder--;
                PlaySound("back");
            }
            else
            {
                c.GroupOrder = BCards.Where(x => x.Group == boardGrouping).Select(x => x.GroupOrder).DefaultIfEmpty(0).Max() + 1;
                PlaySound("select");
            }
            groupOffset.Remove(boardGrouping);
            BoardChanged(true);
            FocusBoardTile(t);
        }

        private Guid GroupGameFor(BoardTile t)
        {
            var g = GroupOf(t.Card);
            List<Guid> games;
            if (g == null || !groupGames.TryGetValue(g.Id, out games) || games == null || games.Count == 0) return Guid.Empty;
            int i = GroupTiles(g).IndexOf(t);
            if (i < 0) return Guid.Empty;
            int off;
            groupOffset.TryGetValue(g.Id, out off);
            int k = off + i;
            if (g.Loop) k = ((k % games.Count) + games.Count) % games.Count;
            return k >= 0 && k < games.Count ? games[k] : Guid.Empty;
        }

        /// <summary>The lists the carousel groups need (added to the Board's own when the games are worked out).</summary>
        private IEnumerable<string> GroupSources()
        {
            if (!IsManualMode) return Enumerable.Empty<string>();
            return MGroups.Where(g => g != null && g.Carousel).Select(g => g.Source ?? "all").Distinct().ToList();
        }

        /// <summary>From BoardRebuildNow (worker thread's results): each carousel group's games.</summary>
        private Dictionary<string, List<Guid>> GroupGamesFrom(Dictionary<string, List<Guid>> pools)
        {
            var d = new Dictionary<string, List<Guid>>();
            if (pools == null || !IsManualMode) return d;
            foreach (var g in MGroups.Where(x => x != null && x.Carousel))
            {
                List<Guid> pool;
                if (pools.TryGetValue(g.Source ?? "all", out pool) && pool != null) d[g.Id] = pool.Distinct().ToList();
            }
            return d;
        }

        /// <summary>After the games are worked out again: no carousel left past its last game (a filter took most away).</summary>
        private void ClampGroupOffsets()
        {
            foreach (var g in MGroups.Where(x => x != null && x.Carousel).ToList())
            {
                int off;
                if (!groupOffset.TryGetValue(g.Id, out off)) continue;
                List<Guid> games;
                int n = groupGames.TryGetValue(g.Id, out games) && games != null ? games.Count : 0;
                if (n == 0) { groupOffset.Remove(g.Id); continue; }
                int shown = Math.Max(1, BCards.Count(c => c.Group == g.Id && IsGameKind(c)));
                groupOffset[g.Id] = g.Loop ? off % n : Math.Max(0, Math.Min(off, n - shown));
            }
        }

        /// <summary>The arrows on a carousel group's tile (not editing): past its end the way it goes, the next games come in;
        /// back the other way at its start, the ones before. True: handled.</summary>
        private bool GroupCarouselKey(BoardTile cur, Key key)
        {
            if (boardEditing || cur == null || cur.Card == null || !IsCarouselCard(cur.Card)) return false;
            var g = GroupOf(cur.Card);
            List<Guid> games;
            if (!groupGames.TryGetValue(g.Id, out games) || games == null || games.Count == 0) return false;
            Key fwd = g.Dir == 1 ? Key.Left : g.Dir == 2 ? Key.Down : Key.Right;
            Key back = g.Dir == 1 ? Key.Right : g.Dir == 2 ? Key.Up : Key.Left;
            if (key != fwd && key != back) return false;
            var members = GroupTiles(g);
            if (members.Any(t => t.Card.GroupOrder > 0))
            {
                // (0.94) a Placement order: the highlight goes 1, 2, 3... that way (and back the other way), whatever the tiles' places
                int i = members.IndexOf(cur);
                if (i >= 0)
                {
                    int step = key == fwd ? 1 : -1;
                    for (int j = i + step; j >= 0 && j < members.Count; j += step)
                        if (BoardUsable(members[j])) { PlaySound("move"); FocusBoardTile(members[j]); return true; }
                }
            }
            // another of the group's tiles that way: the highlight goes there first
            else if (BoardNearest(cur, key, members.Where(t => !ReferenceEquals(t, cur) && BoardUsable(t)).ToList()) != null) return false;
            int off;
            groupOffset.TryGetValue(g.Id, out off);
            int n = games.Count, shown = members.Count;
            int next = off + (key == fwd ? 1 : -1);
            if (key == back && off <= 0) return false;              // at the first: on past the group (looping too, so you can leave it)
            if (!g.Loop && key == fwd && off + shown >= n) return false;   // the last game is showing: on past the group
            if (g.Loop) next = ((next % n) + n) % n;
            groupOffset[g.Id] = next;
            PlaySound("move");
            foreach (var t in members) { BoardFill(t, GroupGameFor(t)); BoardShowState(t); }
            if (cur.GameId != Guid.Empty) SelectBoardGameSoon(cur.GameId);
            BoardFillPieces(BoardHighlightGame());
            return true;
        }

        // ---------------- grouping (editing) ----------------

        private void StartGrouping(BoardGroup g)
        {
            if (g == null) return;
            if (BoardSizeClash() || boardPicked != null) { PlaySound("back"); return; }   // (a tile made bigger over others: sorted out first)
            PlaySound("select");
            boardCardFromBoard = false;
            boardGrouping = g.Id;
            ClosePanels();
            ui.BeginInvoke(new Action(() =>
            {
                if (!boardEditing) { EditBegin(); boardEditing = true; BoardSyncTiles(); }
                BoardFillPage();
                NotifyBoard();
                var first = BoardTiles.FirstOrDefault(t => t.Card != null && t.Card.Group == g.Id);
                if (first != null) FocusBoardTile(first); else FocusBoard();
            }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>A on a tile while grouping: into the group, or out of it.</summary>
        private void BoardGroupToggle(BoardTile t)
        {
            if (boardOrdering && boardGrouping != null && boardPicked == null) { BoardOrderToggle(t); return; }
            if (t == null || t.IsSlot || t.Card == null || boardGrouping == null || boardPicked != null) { PlaySound("back"); return; }
            var c = t.Card;
            // a group stays on one page (its games go along its tiles there)
            string gid = boardGrouping;
            var other = BCards.FirstOrDefault(x => x.Group == gid && !ReferenceEquals(x, c));
            if (c.Group != gid && other != null && PageOf(other) != PageOf(c)) { PlaySound("back"); return; }
            if (c.Group == boardGrouping) { c.Group = null; c.GroupOrder = 0; PlaySound("back"); }
            else { c.Group = boardGrouping; PlaySound("select"); }
            groupOffset.Remove(boardGrouping);
            BoardChanged(true);
            FocusBoardTile(t);
        }

        /// <summary>B while grouping: done (a group left with no tiles goes).</summary>
        private void EndGrouping()
        {
            string id = boardGrouping;
            boardGrouping = null;
            boardOrdering = false;
            CleanGroups();
            PlaySound("back");
            BoardChanged(true);
            // back to the group's settings, on the tile it was started from
            var g = MGroups.FirstOrDefault(x => x != null && x.Id == id);
            var c = boardEditCard != null && boardEditCard.Group == id && BCards.Contains(boardEditCard) ? boardEditCard : BCards.FirstOrDefault(x => x.Group == id);
            if (g != null && c != null)
            {
                boardEditCard = c;
                boardGroupEditId = g.Id;
                OpenBoardGroupSettings(g, true);
            }
            else FocusBoard();
        }

        private void CleanGroups()
        {
            var used = new HashSet<string>(BCards.Where(c => c.Group != null).Select(c => c.Group));
            if (settings.ManualCards != null) foreach (var c in settings.ManualCards) if (c.Group != null) used.Add(c.Group);
            if (settings.FreeCards != null) foreach (var c in settings.FreeCards) if (c.Group != null) used.Add(c.Group);
            MGroups.RemoveAll(g => g == null || !used.Contains(g.Id));
        }

        /// <summary>The tile's settings › GROUP rows (Manual board).</summary>
        private void AddGroupRows(ObservableCollection<PickRow> rows, BoardCard c, Action again)
        {
            if (!IsManualMode) return;
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecGroup", "GROUP") });
            var g = GroupOf(c);
            if (g == null)
            {
                rows.Add(new PickRow
                {
                    Key = "groupnew", Icon = "grid", Text = Loc("LOC_KX_GroupNew", "Group it with other tiles"), Sub = Loc("LOC_KX_GroupNewSub", "Choose the tiles to join it, then set up a carousel"),
                    Command = new RelayCommand(_ =>
                    {
                        var ng = new BoardGroup();
                        MGroups.Add(ng);
                        c.Group = ng.Id;
                        SaveSettings();
                        StartGrouping(ng);
                    })
                });
                return;
            }
            rows.Add(new PickRow
            {
                Key = "groupsettings", Icon = "sliders", Text = string.Format(Loc("LOC_KX_GroupSettingsOf", "{0} settings"), GroupName(g)), Sub = GroupSummary(g),
                Command = new RelayCommand(_ => OpenBoardGroupSettings(g, false))
            });
            rows.Add(new PickRow
            {
                Key = "groupleave", Icon = "remove", Text = Loc("LOC_KX_GroupLeave", "Take it out of the group"),
                Command = new RelayCommand(_ => { c.Group = null; c.GroupOrder = 0; CleanGroups(); PlaySound("back"); BoardChanged(true); BoardRowsAgain(again, "groupnew"); })
            });
        }

        private string GroupSummary(BoardGroup g)
        {
            int n = BCards.Count(c => c.Group == g.Id);
            string tiles = n == 1 ? Loc("LOC_KX_OneTile", "1 tile") : string.Format(Loc("LOC_KX_NTiles", "{0} tiles"), n);
            if (!g.Carousel) return tiles;
            return tiles + "   ·   " + string.Format(Loc("LOC_KX_GroupCarouselSum", "Carousel {0}, {1}"), GroupDirName(g.Dir).ToLowerInvariant(), BoardSourceName(g.Source ?? "all"))
                   + (g.Loop ? "   ·   " + Loc("LOC_KX_GroupLoops", "loops") : "");
        }

        // ---------------- the group's settings ----------------

        private ObservableCollection<PickRow> boardGroupRows;
        public ObservableCollection<PickRow> BoardGroupRows { get { return boardGroupRows ?? (boardGroupRows = new ObservableCollection<PickRow>()); } }
        public bool IsOptionsBoardGroup { get { return optionsPage == "boardgroup"; } }

        private void OpenBoardGroupSettings(BoardGroup g, bool openPanelToo)
        {
            if (g == null) return;
            PlaySound("select");
            boardGroupEditId = g.Id;
            if (openPanelToo && openPanel != "options") { TogglePanel("options"); boardCardFromBoard = true; }
            SetOptionsPage("boardgroup");
            BuildBoardGroupRows(null);
        }

        private void BuildBoardGroupRows(string focusKey)
        {
            var rows = BoardGroupRows;
            rows.Clear();
            var g = MGroups.FirstOrDefault(x => x != null && x.Id == boardGroupEditId);
            if (g == null) { rows.Add(new PickRow { IsInfo = true, Text = Loc("LOC_KX_GroupGone", "That group isn't there any more") }); Notify("BoardGroupRows"); return; }
            Action<string> again = k => { BuildBoardGroupRows(k); };
            Action changed = () => { groupOffset.Remove(g.Id); SaveSettings(); BoardChanged(true); };
            rows.Add(new PickRow { IsHeader = true, Text = GroupName(g).ToUpperInvariant() });
            rows.Add(new PickRow { IsInfo = true, Icon = "grid", Key = "gcount", Text = GroupSummary(g) });
            rows.Add(BoardSwitch("refresh", "gcarousel", Loc("LOC_KX_GroupCarousel", "Carousel"), g.Carousel,
                                 Loc("LOC_KX_GroupCarouselOn", "Its game tiles show games from one list, and move on as you go"),
                                 Loc("LOC_KX_GroupCarouselOff", "Each tile keeps its own games"),
                                 () => { g.Carousel = !g.Carousel; if (string.IsNullOrEmpty(g.Source)) g.Source = "all"; PlaySound("move"); changed(); again("gcarousel"); }));
            if (g.Carousel)
            {
                rows.Add(new PickRow
                {
                    Key = "gdir", Icon = "forward", Text = Loc("LOC_KX_GroupDir", "Direction"), Sub = g.Dir == 1 ? Loc("LOC_KX_GroupDirLeftSub", "Go left to bring more games in")
                         : g.Dir == 2 ? Loc("LOC_KX_GroupDirDownSub", "Go down to bring more games in") : Loc("LOC_KX_GroupDirRightSub", "Go right to bring more games in"),
                    IsSelected = false,
                    Command = new RelayCommand(_ => { g.Dir = (g.Dir + 1) % 3; PlaySound("move"); changed(); again("gdir"); })
                });
                rows[rows.Count - 1].Text = string.Format(Loc("LOC_KX_GroupDirIs", "Direction: {0}"), GroupDirName(g.Dir));
                rows.Add(BoardSwitch("refresh", "gloop", Loc("LOC_KX_GroupLoop", "Loop"), g.Loop,
                                     Loc("LOC_KX_GroupLoopOn", "After the last game it starts again"), Loc("LOC_KX_GroupLoopOff", "It stops at the last game"),
                                     () => { g.Loop = !g.Loop; PlaySound("move"); changed(); again("gloop"); }));
                rows.Add(new PickRow
                {
                    Key = "gsource", Icon = "shelf", Text = Loc("LOC_KX_BoardSource", "Games from"),
                    Sub = string.Format(Loc("LOC_KX_GroupSourceSub", "{0} (needed: it replaces each tile's own games)"), BoardSourceName(g.Source ?? "all")),
                    Command = new RelayCommand(_ => { boardSourceGroup = g; PlaySound("select"); SetOptionsPage("boardsource"); })
                });
                // (0.92) the order new games slide into the tiles
                bool ordered = GroupHasOrder(g);
                int nGames = BCards.Count(x => x.Group == g.Id && IsGameKind(x)), nSet = BCards.Count(x => x.Group == g.Id && x.GroupOrder > 0);
                rows.Add(new PickRow
                {
                    Key = "gorder", Icon = "sort", Text = Loc("LOC_KX_GroupOrder", "Placement order"),
                    Sub = ordered ? string.Format(Loc("LOC_KX_GroupOrderSet", "Set on {0} of {1} tiles"), nSet, nGames)
                                  : Loc("LOC_KX_GroupOrderAuto2", "Number the tiles: 1 shows the first game, 2 the second..."),
                    Command = new RelayCommand(_ => StartGroupOrdering(g))
                });
                if (ordered)
                    rows.Add(new PickRow
                    {
                        Key = "gorderclear", Icon = "remove", Text = Loc("LOC_KX_GroupOrderClear", "Clear the placement order"),
                        Command = new RelayCommand(_ => { ClearGroupOrder(g); PlaySound("back"); changed(); again("gorder"); })
                    });
            }
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_GroupTiles", "TILES") });
            rows.Add(new PickRow
            {
                Key = "gtiles", Icon = "add", Text = Loc("LOC_KX_GroupPick", "Add or take out tiles"), Sub = Loc("LOC_KX_GroupPickSub", "On the board: choose tiles to add or take out"),
                Command = new RelayCommand(_ => StartGrouping(g))
            });
            rows.Add(new PickRow
            {
                Key = "gungroup", Icon = "remove", Text = Loc("LOC_KX_GroupUngroup", "Ungroup"), Sub = Loc("LOC_KX_GroupUngroupSub", "The tiles stay, each with its own games again"),
                Command = new RelayCommand(_ =>
                {
                    foreach (var list in new[] { settings.ManualCards, settings.FreeCards }) if (list != null) foreach (var c in list) if (c.Group == g.Id) { c.Group = null; c.GroupOrder = 0; }
                    MGroups.Remove(g);
                    groupOffset.Remove(g.Id);
                    PlaySound("back");
                    BoardChanged(true);
                    GoBackTo("boardcard", "groupnew");
                })
            });
            Notify("BoardGroupRows");
            string key = focusKey;
            if (key != null) ui.BeginInvoke(new Action(() => { if (!UiTools.FocusCommandParameter("KX_OptionsPanel", key)) UiTools.FocusInside("KX_OptionsPanel"); }), DispatcherPriority.ContextIdle);
        }
    }
}

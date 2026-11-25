using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>A key on the search keyboard.</summary>
    public class SearchKey
    {
        public string Label { get; set; }
        public string Key { get; set; }      // what it types, or "space", "back", "clear", "done"
        public bool IsWide { get; set; }
        public ICommand Command { get; set; }
        // (1.0.7) the Console design's keyboard (Settings › Layout changes WIP › Keyboard search 2): each key's width, and its
        // look: "char" a letter, "action" a lighter key (Clear, Done…), "accent" the purple Search key, "back" the backspace icon
        public double Width { get; set; }
        public string Kind { get; set; }
        public bool IsAccent { get { return Kind == "accent"; } }
        public bool IsAction { get { return Kind == "action" || Kind == "back"; } }
        public bool IsBackKey { get { return Kind == "back"; } }
        public bool HasLabel { get { return Kind != "back"; } }
        public bool NoIcon { get; set; }   // (the half keyboard's purple OK key: no magnifier)
    }

    public class SearchKeyRow
    {
        public ObservableCollection<SearchKey> Keys { get; private set; }
        public SearchKeyRow() { Keys = new ObservableCollection<SearchKey>(); }
    }

    /// <summary>A game in the search results.</summary>
    public class SearchHit : Observable
    {
        internal Guid GameId;
        internal string CoverFile;
        public string Name { get; set; }
        public string Sub { get; set; }
        private ImageSource cover;
        public ImageSource Cover { get { return cover; } set { if (Set(ref cover, value, "Cover")) Notify("HasCover"); } }
        public bool HasCover { get { return cover != null; } }
        public ICommand Command { get; set; }
        // (1.0.7) the Console design: a wide picture (the game's background), its store on a badge, the typed letters picked out
        // in the name, and a colour with the first letter for a game without a picture
        internal string WideFile;
        private ImageSource wide;
        public ImageSource Wide { get { return wide; } set { if (Set(ref wide, value, "Wide")) Notify("HasWide"); } }
        public bool HasWide { get { return wide != null; } }
        public string Source { get; set; }
        public bool HasSource { get { return !string.IsNullOrEmpty(Source); } }
        public string NamePre { get; set; }
        public string NameMatch { get; set; }
        public string NamePost { get; set; }
        public Brush Tint { get; set; }
        public string Letter { get; set; }
        public bool IsDim { get; set; }   // not installed: shown a little faded
    }

    /// <summary>
    /// Search with the theme's own keyboard: every letter shows in the box as you type it, the matching games come up
    /// straight away above the keyboard, and your recent searches are there to pick from. A game opens its page;
    /// Done shows the matches in the home screen's games (Playnite's own search filter).
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<SearchKeyRow> SearchKeyRows { get; private set; }
        public ObservableCollection<SearchHit> SearchHits { get; private set; }
        public ObservableCollection<PickOption> SearchRecent { get; private set; }
        private readonly StringBuilder searchText = new StringBuilder();
        private List<Tuple<Game, string>> searchIndex;
        private DateTime searchIndexAt = DateTime.MinValue;
        private int searchGen;
        private bool searchHooked;

        public bool IsSearchOpen { get { return openPanel == "search"; } }
        public string SearchText { get { return searchText.ToString(); } }
        // (1.0.7) the Console design (Settings › Layout changes WIP › Keyboard search, 2 of 2)
        private bool SearchV2 { get { return LabLook("search") == 1; } }
        public bool SearchDesign2 { get { return SearchV2; } }
        public bool SearchDesignDefault { get { return !SearchV2; } }
        private int searchKeysFor = -1;
        private bool searchSymbols;
        private int searchCaret;   // where the next letter goes (the Console design's ‹ › move it; otherwise always the end)
        private string searchGhost = "";
        public string SearchTextBefore { get { int c = Math.Min(searchCaret, searchText.Length); return searchText.ToString(0, c); } }
        public string SearchTextAfter { get { int c = Math.Min(searchCaret, searchText.Length); return searchText.ToString(c, searchText.Length - c); } }
        public string SearchGhost { get { return searchGhost; } }
        public ObservableCollection<PickRow> SearchFilters { get; private set; }
        private string searchFilter = "all";
        public bool HasSearchText { get { return searchText.Length > 0; } }
        public bool ShowSearchRecent { get { return settings.SearchShowRecent && searchText.Length == 0 && SearchRecent != null && SearchRecent.Count > 0; } }   // (1.0.7: only when switched on)
        public bool HasSearchHits { get { return SearchHits != null && SearchHits.Count > 0; } }
        public string SearchCountText { get; private set; }

        private void InitSearch()
        {
            SearchKeyRows = new ObservableCollection<SearchKeyRow>();
            SearchHits = new ObservableCollection<SearchHit>();
            SearchRecent = new ObservableCollection<PickOption>();
            SearchFilters = new ObservableCollection<PickRow>();
            BuildSearchKeys();
        }

        /// <summary>The keys: the default board, or the Console design's (numbers, letters, the action keys down the right).</summary>
        private void BuildSearchKeys()
        {
            searchKeysFor = LabLook("search");
            SearchKeyRows.Clear();
            if (SearchV2) { BuildSearchKeysV2(); return; }
            foreach (string row in new[] { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-:." })
            {
                var r = new SearchKeyRow();
                foreach (char c in row) r.Keys.Add(MakeKey(c.ToString().ToUpperInvariant(), c.ToString()));
                SearchKeyRows.Add(r);
            }
            var last = new SearchKeyRow();
            last.Keys.Add(MakeKey(Loc("LOC_KX_KeySpace", "Space"), "space", true));
            last.Keys.Add(MakeKey(Loc("LOC_KX_KeyBack", "Delete"), "back", true));
            last.Keys.Add(MakeKey(Loc("LOC_KX_KeyClear", "Clear"), "clear", true));
            // (1.0.7) no "Show in my games" key any more (the user took it out)
            last.Keys.Add(MakeKey(Loc("LOC_KX_KeyStore", "Find in the Store"), "store", true));
            SearchKeyRows.Add(last);
        }

        private const double KeyW = 110, KeyGap = 13;
        private void BuildSearchKeysV2()
        {
            string[] rows = searchSymbols ? new[] { "1234567890", "!@#$%^&*()", "~+=_[]{};'", "\"<>?/|:,.-" } : new[] { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,.-" };
            string[][] side =
            {
                new[] { "", "back", "back" },
                new[] { Loc("LOC_KX_KeyClear", "Clear"), "clear", "action" },
                new[] { searchSymbols ? "abc" : "&#?", "sym", "action" },
                new[] { Loc("LOC_KX_Search", "Search"), "search", "accent" },
            };
            for (int i = 0; i < 4; i++)
            {
                var r = new SearchKeyRow();
                foreach (char c in rows[i]) r.Keys.Add(MakeKeyV2(c.ToString(), c.ToString(), "char", KeyW));
                r.Keys.Add(MakeKeyV2(side[i][0], side[i][1], side[i][2], KeyW * 2 + KeyGap));
                SearchKeyRows.Add(r);
            }
            var last = new SearchKeyRow();
            last.Keys.Add(MakeKeyV2(Loc("LOC_KX_KeyOpenStore", "Open in store"), "store", "action", KeyW * 2 + KeyGap));
            last.Keys.Add(MakeKeyV2(Loc("LOC_KX_KeySpace", "Space"), "space", "char", KeyW * 6 + KeyGap * 5));
            last.Keys.Add(MakeKeyV2("\u2039", "left", "char", KeyW));
            last.Keys.Add(MakeKeyV2("\u203A", "right", "char", KeyW));
            last.Keys.Add(MakeKeyV2(Loc("LOC_KX_KeyDoneV2", "Done"), "close", "action", KeyW * 2 + KeyGap));
            SearchKeyRows.Add(last);
        }

        private SearchKey MakeKeyV2(string label, string key, string kind, double width)
        {
            var k = new SearchKey { Label = label, Key = key, Kind = kind, Width = width, IsWide = width > KeyW };
            k.Command = new RelayCommand(_ => PressSearchKey(key));
            return k;
        }

        /// <summary>A letter (or a space) where the caret is.</summary>
        private void SearchInsert(string s)
        {
            if (searchText.Length + s.Length > 60) return;
            int c = Math.Min(searchCaret, searchText.Length);
            searchText.Insert(c, s);
            searchCaret = c + s.Length;
        }

        private void SearchCaretEnd() { searchCaret = searchText.Length; }

        // ---------------- (1.0.7) the Console design's filters: All, Installed, Uninstalled ----------------

        private void BuildSearchFilters()
        {
            if (SearchFilters == null) return;
            SearchFilters.Clear();
            var keys = new List<Tuple<string, string>> { Tuple.Create("all", Loc("LOC_KX_All", "All")), Tuple.Create("installed", Loc("LOC_KX_Installed", "Installed")), Tuple.Create("uninstalled", Loc("LOC_KX_Uninstalled", "Uninstalled")) };
            if (!keys.Any(k => k.Item1 == searchFilter)) searchFilter = "all";
            foreach (var k in keys)
            {
                string key = k.Item1;
                SearchFilters.Add(new PickRow { Key = key, Text = k.Item2, IsSelected = key == searchFilter, Command = new RelayCommand(_ => SetSearchFilter(key)) });
            }
        }

        private void SetSearchFilter(string key)
        {
            if (key == searchFilter) return;
            searchFilter = key;
            foreach (var r in SearchFilters) r.IsSelected = r.Key == key;
            PlaySound("move");
            UpdateSearchHits();
        }

        private DateTime searchFilterStepAt = DateTime.MinValue;

        private void StepSearchFilter(int dir)
        {
            if (SearchFilters == null || SearchFilters.Count == 0) return;
            // one press of LT / RT arrives twice (as the trigger and as Page Up / Down): only the first one moves
            if ((DateTime.UtcNow - searchFilterStepAt).TotalMilliseconds < 220) return;
            searchFilterStepAt = DateTime.UtcNow;
            int i = SearchFilters.ToList().FindIndex(r => r.Key == searchFilter);
            i = (Math.Max(0, i) + dir + SearchFilters.Count) % SearchFilters.Count;
            SetSearchFilter(SearchFilters[i].Key);
        }

        private bool SearchFilterLets(Game g)
        {
            if (searchFilter == "all") return true;
            if (searchFilter == "installed") return g.IsInstalled;
            if (searchFilter == "uninstalled") return !g.IsInstalled;
            return true;
        }

        private static readonly Color[] SearchTints = { Color.FromRgb(0x3B, 0x2A, 0x7A), Color.FromRgb(0x1F, 0x5C, 0x55), Color.FromRgb(0x7A, 0x3A, 0x28), Color.FromRgb(0x2A, 0x4E, 0x82), Color.FromRgb(0x5E, 0x2A, 0x70), Color.FromRgb(0x2E, 0x5A, 0x2A) };

        private SearchKey MakeKey(string label, string key, bool wide = false)
        {
            var k = new SearchKey { Label = label, Key = key, IsWide = wide };
            k.Command = new RelayCommand(_ => PressSearchKey(key));
            return k;
        }

        private void OpenSearch()
        {
            searchPick = null;
            if (SearchKeyRows == null) InitSearch();
            if (searchKeysFor != LabLook("search")) { searchSymbols = false; BuildSearchKeys(); }
            Notify("SearchDesign2"); Notify("SearchDesignDefault");
            if (openPanel != "search") TogglePanel("search");
            searchText.Clear();
            searchCaret = 0;
            searchGhost = "";
            searchFilter = "all";
            BuildSearchFilters();
            BuildSearchRecent();
            SearchHits.Clear();
            SearchCountText = "";
            NotifySearch();
            HookSearchTyping();
            if ((DateTime.Now - searchIndexAt).TotalMinutes > 2) BuildSearchIndex();
            ui.BeginInvoke(new Action(() =>
            {
                var first = SearchKeyRows.Count > 2 ? SearchKeyRows[SearchV2 ? 2 : 1].Keys.FirstOrDefault() : null;   // Q (the Console design: A)
                if (first != null) FocusSearchKey(first); else UiTools.FocusInside("KX_SearchView");
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>The keyboard on screen: the default's, or the Console design's.</summary>
        private string SearchKeysName { get { return "KX_SearchKeys2"; } }   // (the old keyboard search was taken out)

        private void FocusSearchKey(SearchKey k)
        {
            var rows = UiTools.FindByName(Application.Current.MainWindow, SearchKeysName) as ItemsControl;
            if (rows == null) return;
            foreach (var r in SearchKeyRows)
            {
                if (!r.Keys.Contains(k)) continue;
                var rc = rows.ItemContainerGenerator.ContainerFromItem(r) as FrameworkElement;
                var keys = rc != null ? UiTools.FindChild<ItemsControl>(rc) : null;
                var c = keys != null ? keys.ItemContainerGenerator.ContainerFromItem(k) as DependencyObject : null;
                var b = c != null ? UiTools.FindChild<ButtonBase>(c) as UIElement : null;
                if (b != null) Keyboard.Focus(b);
            }
        }

        private void NotifySearch()
        {
            foreach (string n in new[] { "SearchText", "HasSearchText", "ShowSearchRecent", "HasSearchHits", "SearchCountText", "SearchTextBefore", "SearchTextAfter", "SearchGhost" }) Notify(n);
        }

        private void PressSearchKey(string key)
        {
            switch (key)
            {
                case "space": { int c = Math.Min(searchCaret, searchText.Length); if (c > 0 && searchText[c - 1] != ' ') SearchInsert(" "); } break;
                case "back": { int c = Math.Min(searchCaret, searchText.Length); if (c > 0) { searchText.Remove(c - 1, 1); searchCaret = c - 1; } else { PlaySound("back"); return; } } break;
                case "clear": searchText.Clear(); searchCaret = 0; break;
                case "left": if (searchCaret > 0) searchCaret--; else { PlaySound("back"); return; } NotifySearch(); PlaySound("move"); return;
                case "right": if (searchCaret < searchText.Length) searchCaret++; else if (searchGhost.Length > 0) { SearchInsert(searchGhost); } else { PlaySound("back"); return; } break;
                case "sym": searchSymbols = !searchSymbols; { var keys = SearchKeyRows; BuildSearchKeys(); PlaySound("move"); ui.BeginInvoke(new Action(() => { var k = SearchKeyRows.Count > 2 ? SearchKeyRows[2].Keys.LastOrDefault() : null; if (k != null) FocusSearchKey(k); }), DispatcherPriority.ContextIdle); } return;
                case "search": { var first = SearchHits.FirstOrDefault(); if (first == null) { PlaySound("back"); return; } PlaySound("move"); ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_SearchHits2", first)), DispatcherPriority.ContextIdle); } return;
                case "close": PlaySound("back"); ClosePanels(); return;
                case "done": if (searchPick != null) { var first = SearchHits.FirstOrDefault(); if (first != null) OpenSearchHit(first.GameId); else PlaySound("back"); return; } SearchDone(); return;
                case "store": if (searchPick != null) { PlaySound("back"); return; } SearchInStore(); return;
                default: SearchInsert(key); break;
            }
            PlaySound("move");
            SearchChanged();
        }

        private void SearchChanged()
        {
            NotifySearch();
            UpdateSearchHits();
        }

        /// <summary>Typing on a real keyboard goes straight into the box too.</summary>
        private void HookSearchTyping()
        {
            if (searchHooked) return;
            searchHooked = true;
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTextInputEvent, new TextCompositionEventHandler((s, e) =>
            {
                if (!IsMainWindow(s) || string.IsNullOrEmpty(e.Text)) return;
                if (kbOpen) { KbTyped(e.Text); e.Handled = true; return; }
                if (openPanel != "search") return;
                foreach (char c in e.Text)
                {
                    if (char.IsControl(c)) continue;
                    if (c == ' ') { int at = Math.Min(searchCaret, searchText.Length); if (at > 0 && searchText[at - 1] != ' ') SearchInsert(" "); }
                    else SearchInsert(char.ToLowerInvariant(c).ToString());
                }
                e.Handled = true;
                SearchChanged();
            }), true);
        }

        /// <summary>Keys on the search page: Backspace / X delete, Y a space, Enter (keyboard) shows the matches in your games.</summary>
        private bool SearchPanelKey(KeyEventArgs e, bool xPressed)
        {
            if (openPanel != "search") return false;
            bool pad = e.GetType().Name == "GameControllerInputEventArgs";
            if (!pad && (e.Key == Key.Left || e.Key == Key.Right) && WrapKeyRow(SearchKeyRows, SearchKeysName, e.Key == Key.Right)) { e.Handled = true; return true; }
            if (SearchV2 && !pad && (e.Key == Key.PageUp || e.Key == Key.PageDown)) { StepSearchFilter(e.Key == Key.PageDown ? 1 : -1); e.Handled = true; return true; }
            if (!pad && e.Key == Key.Back) { PressSearchKey("back"); e.Handled = true; return true; }
            if (!pad && e.Key == Key.Space) { PressSearchKey("space"); e.Handled = true; return true; }   // typed here (handling the key stops the focused on-screen key pressing too)
            if (!pad && e.Key == Key.Enter && !(Keyboard.FocusedElement is ButtonBase && (((FrameworkElement)Keyboard.FocusedElement).DataContext is SearchHit || ((FrameworkElement)Keyboard.FocusedElement).DataContext is PickOption))) { if (searchPick != null) PressSearchKey("done"); else SearchDone(); e.Handled = true; return true; }
            if (pad && xPressed) { PressSearchKey("back"); e.Handled = true; return true; }
            if (pad && !IsPadRelease(e))
            {
                var b = e.GetType().GetProperty("Button");
                object v = b != null ? b.GetValue(e, null) : null;
                if (v != null && v.ToString() == "Y") { PressSearchKey("space"); e.Handled = true; return true; }
                // (1.0.7) the Console design: LT / RT switch the filter, Start (≡) goes to the games
                if (SearchV2 && v != null && (v.ToString() == "TriggerLeft" || v.ToString() == "TriggerRight")) { StepSearchFilter(v.ToString() == "TriggerRight" ? 1 : -1); e.Handled = true; return true; }
                if (SearchV2 && v != null && v.ToString() == "Start") { PressSearchKey("search"); e.Handled = true; return true; }
            }
            return false;
        }

        // ---------------- the results ----------------

        private void BuildSearchIndex()
        {
            KippixShared.SafeWork.Queue(_ =>
            {
                List<Tuple<Game, string>> idx = null;
                try { idx = api.Database.Games.Where(g => g != null && !g.Hidden && !string.IsNullOrEmpty(g.Name) && ProfileAllowsGame(g) && !CopyBehind(g.Id)).Select(g => Tuple.Create(g, SearchNorm(g.Name))).ToList(); }
                catch (Exception) { }
                OnUi(() => { if (idx != null) { searchIndex = idx; searchIndexAt = DateTime.Now; BuildSearchFilters(); UpdateSearchHits(); } });
            });
        }

        /// <summary>Lower case, letters, digits and single spaces ("Hades II: Early" and "hades ii early" match).</summary>
        private static string SearchNorm(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
                else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
            }
            return sb.ToString().Trim();
        }

        private void UpdateSearchHits()
        {
            int gen = ++searchGen;
            string q = SearchNorm(searchText.ToString());
            SearchHits.Clear();
            searchGhost = "";
            if (q.Length == 0 || searchIndex == null) { SearchCountText = ""; NotifySearch(); return; }
            var words = q.Split(' ');
            var matches = searchIndex.Where(t => words.All(w => t.Item2.Contains(w)) && SearchFilterLets(t.Item1))
                                     .OrderBy(t => t.Item2.StartsWith(q, StringComparison.Ordinal) ? 0 : (" " + t.Item2).Contains(" " + q) ? 1 : 2)
                                     .ThenByDescending(t => t.Item1.LastActivity ?? DateTime.MinValue)
                                     .ToList();
            SearchCountText = matches.Count == 0 ? Loc("LOC_KX_SearchNone", "No games match") : matches.Count == 1 ? Loc("LOC_KX_SearchOne", "1 game") : string.Format(Loc("LOC_KX_SearchN", "{0} games"), matches.Count);
            string typed = searchText.ToString();
            bool v2 = SearchV2;
            foreach (var t in matches.Take(24))
            {
                var g = t.Item1;
                var hit = new SearchHit
                {
                    GameId = g.Id, Name = g.Name,
                    Sub = g.IsInstalled ? Loc("LOC_KX_Installed", "Installed") : (g.Source != null ? g.Source.Name : ""),
                    CoverFile = ShuffledCover(g, !string.IsNullOrEmpty(g.CoverImage) && !g.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? SafeFullPath(g.CoverImage) : null)
                };
                if (v2)
                {
                    // the Console design: the game's background (wide), its store, the typed letters picked out in its name
                    hit.WideFile = !string.IsNullOrEmpty(g.BackgroundImage) && !g.BackgroundImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? SafeFullPath(g.BackgroundImage) : hit.CoverFile;
                    hit.Source = g.Source != null ? g.Source.Name.ToUpperInvariant() : "";
                    string needle = typed.Trim();
                    int at = needle.Length > 0 ? g.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) : -1;
                    if (at < 0 && words.Length > 0) { needle = words[0]; at = g.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase); }
                    if (at >= 0) { hit.NamePre = g.Name.Substring(0, at); hit.NameMatch = g.Name.Substring(at, needle.Length); hit.NamePost = g.Name.Substring(at + needle.Length); }
                    else { hit.NamePre = g.Name; hit.NameMatch = ""; hit.NamePost = ""; }
                    var tint = new SolidColorBrush(SearchTints[Math.Abs(g.Name.GetHashCode()) % SearchTints.Length]);
                    tint.Freeze();
                    hit.Tint = tint;
                    hit.Letter = g.Name.Substring(0, 1).ToUpperInvariant();
                    hit.IsDim = !g.IsInstalled;
                }
                var id = g.Id;
                hit.Command = new RelayCommand(_ => OpenSearchHit(id));
                SearchHits.Add(hit);
            }
            // (1.0.7) the rest of the top game's name, grey after the caret, when what's typed is how its name starts
            var top = SearchHits.FirstOrDefault();
            if (v2 && top != null && searchCaret >= typed.Length && typed.Length > 0 && top.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) searchGhost = top.Name.Substring(typed.Length);
            NotifySearch();
            if (v2)
                foreach (var h in SearchHits.ToList())
                {
                    var hit = h;
                    string f = h.WideFile;
                    if (string.IsNullOrEmpty(f)) continue;
                    KippixShared.SafeWork.Queue(_ => { var img = Images.Load(f, 560); if (img != null) OnUi(() => { if (gen == searchGen) hit.Wide = img; }); });
                }
            foreach (var h in SearchHits.ToList())
            {
                if (v2) break;
                var hit = h;
                string f = h.CoverFile;
                if (string.IsNullOrEmpty(f)) continue;
                KippixShared.SafeWork.Queue(_ => { var img = Images.Load(f, 240); if (img != null) OnUi(() => { if (gen == searchGen) hit.Cover = img; }); });
            }
        }

        /// <summary>A result: its game page (the search is remembered).</summary>
        // picking a game for something (a Board tile): the hit is handed over instead of opened
        private Action<Guid> searchPick;
        private void OpenSearchToPick(Action<Guid> pick)
        {
            OpenSearch();
            searchPick = pick;
        }

        private void OpenSearchHit(Guid id)
        {
            if (searchPick != null)
            {
                var cb = searchPick;
                searchPick = null;
                RememberSearch();
                PlaySound("select");
                ClosePanels();
                cb(id);
                return;
            }
            RememberSearch();
            PlaySound("select");
            focusBeforePanel = null;
            ClosePanels();
            var list = UiTools.FindByName(Application.Current.MainWindow, "PART_ListGameItems") as ListBox;
            bool inView = list != null && list.Items.Cast<object>().Any(x => EntryId(x) == id);
            if (!inView) SetPlayniteSearch("");   // the game isn't in the games on screen (a filter hides it): clear Playnite's search first
            try { api.MainView.SelectGame(id); } catch (Exception) { }
            ui.BeginInvoke(new Action(() =>
            {
                FocusSelectedCard();
                if (!settings.SkipGamePage && SelectedGameId() == id) RunMainCommand("ToggleGameDetailsCommand");
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>Done: the home screen's games are filtered to the search (Playnite's own search, so its filters panel shows it too).</summary>
        private void SearchDone()
        {
            RememberSearch();
            PlaySound("select");
            string q = searchText.ToString().Trim();
            focusBeforePanel = null;   // the highlight goes to the games, not back to the Search button
            ClosePanels();
            SetPlayniteSearch(q);
            ui.BeginInvoke(new Action(FocusSelectedCard), DispatcherPriority.ContextIdle);
        }

        /// <summary>The same words in the Store (owned or not): its list of every store's games, so you can look at it there.</summary>
        private void SearchInStore()
        {
            RememberSearch();
            string q = searchText.ToString().Trim();
            PlaySound("select");
            focusBeforePanel = null;
            ClosePanels();
            OpenStore();
            if (openPanel != "store") return;   // not for this profile (or the PIN is being asked for)
            if (q.Length > 0) { storeSearchShown = q; OpenStoreList("all", q); }
        }

        private void SetPlayniteSearch(string text)
        {
            try
            {
                var w = Application.Current.MainWindow;
                object vm = w != null ? w.DataContext : null;
                object app = vm != null ? Prop(vm, "AppSettings") : null;
                object fs = app != null ? Prop(app, "Fullscreen") : null;
                object filter = fs != null ? Prop(fs, "FilterSettings") : null;
                var p = filter != null ? filter.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance) : null;
                // Playnite lets go of the saved filter the moment its filter has a search in it (the games stay that filter's, but its
                // name goes from above them, and it isn't chosen again when the search is cleared): it's kept here for both
                bool on = !string.IsNullOrEmpty(text);
                object preset = vm != null ? Prop(vm, "ActiveFilterPreset") : null;
                if (on && preset != null) searchPreset = preset;
                if (p != null && p.CanWrite) p.SetValue(filter, on ? text : null, null);
                if (!on && searchPreset != null)
                {
                    var ap = vm != null ? vm.GetType().GetProperty("ActiveFilterPreset", BindingFlags.Public | BindingFlags.Instance) : null;
                    if (ap != null && ap.CanWrite && ap.GetValue(vm, null) == null) ap.SetValue(vm, searchPreset, null);
                    searchPreset = null;
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: search filter: " + ex.Message); }
            HomeSearchChanged();
        }

        // ---------------- the search on the home screen (Show in my games, or Playnite's own search) ----------------

        /// <summary>What the home screen's games are searched for right now (Playnite's name filter); "" when nothing is.</summary>
        public string HomeSearchText
        {
            get
            {
                try { var f = PlayniteFilterSettings(); return (f != null ? Prop(f, "Name") as string : null) ?? ""; }
                catch (Exception) { return ""; }
            }
        }

        // shown with the filter name above the games, with how to clear it (not in Folder: a folder shows its own games, Playnite's filter set aside)
        public bool HomeSearchShown { get { return !FolderMode && HomeSearchText.Length > 0; } }

        private object searchPreset;   // the saved filter that was on when the search went on (SetPlayniteSearch)

        /// <summary>While the games are searched: the name of the saved filter they are still from (Playnite shows none then). Null otherwise.</summary>
        private string SearchLabelName
        {
            get
            {
                try
                {
                    if (searchPreset == null || !HomeSearchShown) return null;
                    var w = Application.Current != null ? Application.Current.MainWindow : null;
                    object vm = w != null ? w.DataContext : null;
                    if (vm == null || Prop(vm, "ActiveFilterPreset") != null) return null;   // (another saved filter was chosen since)
                    string name = Prop(searchPreset, "Name") as string;
                    return string.IsNullOrEmpty(name) ? null : name;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>The name filter changed (here, or in Playnite's own filters panel): the line above the games follows.</summary>
        private void HomeSearchChanged()
        {
            Notify("HomeLabelOwn");
            Notify("HomeLabelText");
            Notify("HomeSearchText");
            Notify("HomeSearchShown");
            Notify("FilterAbove");
        }

        /// <summary>B on the home screen while the games are searched: the search is cleared, every game is back. True: there was one.</summary>
        private bool HomeSearchClear()
        {
            if (!HomeSearchShown) return false;
            PlaySound("select");
            SetPlayniteSearch(null);
            ui.BeginInvoke(new Action(FocusSelectedCard), DispatcherPriority.ContextIdle);
            return true;
        }

        /// <summary>Playnite's filter (fullscreen) back to nothing: every game shows. Its own "clear" when it has one, else each part.</summary>
        private void ClearPlayniteFilter()
        {
            try
            {
                var f = PlayniteFilterSettings();
                if (f == null) return;
                foreach (var m in f.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == "ClearFilters"))
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 0) { m.Invoke(f, null); return; }
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { m.Invoke(f, new object[] { true }); return; }
                }
                foreach (var p in f.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanWrite || !p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    if (p.Name == "UseAndFilteringStyle" || p.Name == "SortingOrder" || p.Name == "SortingOrderDirection" || p.Name == "GroupingOrder") continue;
                    try
                    {
                        if (p.PropertyType == typeof(bool)) p.SetValue(f, false, null);
                        else if (!p.PropertyType.IsValueType) p.SetValue(f, null, null);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: clear filter: " + ex.Message); }
        }

        private static object PlayniteFilterSettings()
        {
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            object vm = w != null ? w.DataContext : null;
            object app = vm != null ? Prop(vm, "AppSettings") : null;
            object fs = app != null ? Prop(app, "Fullscreen") : null;
            return fs != null ? Prop(fs, "FilterSettings") : null;
        }

        // ---------------- recent searches ----------------

        private void RememberSearch()
        {
            string q = searchText.ToString().Trim();
            if (q.Length < 2) return;
            if (settings.RecentSearches == null) settings.RecentSearches = new List<string>();
            settings.RecentSearches.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
            settings.RecentSearches.Insert(0, q);
            if (settings.RecentSearches.Count > 8) settings.RecentSearches.RemoveRange(8, settings.RecentSearches.Count - 8);
            SaveSettings();
        }

        private void BuildSearchRecent()
        {
            SearchRecent.Clear();
            if (settings.RecentSearches == null) return;
            foreach (string r in settings.RecentSearches)
            {
                string q = r;
                SearchRecent.Add(new PickOption { Text = q, Command = new RelayCommand(_ => { searchText.Clear(); searchText.Append(q); SearchCaretEnd(); PlaySound("select"); SearchChanged(); }) });
            }
            if (SearchRecent.Count > 0)
                SearchRecent.Add(new PickOption { Text = Loc("LOC_KX_ClearRecent", "Forget these"), Command = new RelayCommand(_ => { settings.RecentSearches.Clear(); SaveSettings(); SearchRecent.Clear(); PlaySound("back"); NotifySearch(); OpenSearchFocusKeys(); }) });
        }

        private void OpenSearchFocusKeys()
        {
            ui.BeginInvoke(new Action(() => { var k = SearchKeyRows.Count > 1 ? SearchKeyRows[1].Keys.FirstOrDefault() : null; if (k != null) FocusSearchKey(k); }), DispatcherPriority.ContextIdle);
        }
    }
}

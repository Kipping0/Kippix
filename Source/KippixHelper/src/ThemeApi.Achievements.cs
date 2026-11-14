using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    public class AchGameRow : Observable
    {
        internal AchGameData Data;
        internal string CoverFile;
        public string Name { get; set; }
        private ImageSource cover;
        public ImageSource Cover { get { return cover; } set { Set(ref cover, value, "Cover"); Notify("HasCover"); } }
        public bool HasCover { get { return cover != null; } }
        public string CountText { get; set; }
        public string SubText { get; set; }
        internal double Fraction;
        private double progressWidth;
        public double ProgressWidth { get { return progressWidth; } set { Set(ref progressWidth, value, "ProgressWidth"); } }
        public bool IsComplete { get; set; }
        public ICommand OpenCommand { get; set; }
        // (0.89) for the Redesign layouts (ThemeApi.AchRedesign.cs)
        public string PercentText { get; set; }
        public string UnlockedText { get; set; }
        public string DateText { get; set; }
        public Geometry Ring { get; set; }
    }

    public class AchItemRow : Observable
    {
        internal AchItemData Data;
        public string Name { get; set; }
        public string Description { get; set; }
        private ImageSource icon;
        public ImageSource Icon { get { return icon; } set { Set(ref icon, value, "Icon"); Notify("HasIcon"); } }
        public bool HasIcon { get { return icon != null; } }
        public bool IsUnlocked { get; set; }
        /// <summary>Locked and drawn with the unlocked picture, so the theme dims it.</summary>
        public bool IsDimmed { get; set; }
        private bool isMissable;
        public bool IsMissable { get { return isMissable; } set { Set(ref isMissable, value, "IsMissable"); } }
        public string StatusText { get; set; }
        public string RarityText { get; set; }
        public bool HasRarity { get { return !string.IsNullOrEmpty(RarityText); } }
        public ICommand OpenCommand { get; set; }
        // (0.89) for the Redesign layouts: the rarity tier's colour and name, and a bar as wide as the % of players who have it
        public Brush TierBrush { get; set; }
        public string TierName { get; set; }
        public bool HasTier { get; set; }
        public double RarityBarWidth { get; set; }
    }

    /// <summary>One choice in a dropdown (sort, filter).</summary>
    public class PickOption
    {
        public string Text { get; set; }
        /// <summary>Current value shown on the right (settings rows), or null.</summary>
        public string Value { get; set; }
        public bool HasValue { get { return !string.IsNullOrEmpty(Value); } }
        public bool IsSelected { get; set; }
        public ICommand Command { get; set; }
        /// <summary>Store filter chips: the search chip (magnifier icon) and on/off chips (tick when on).</summary>
        public bool IsSearch { get; set; }
        public bool IsToggle { get; set; }
        /// <summary>Which chip this is (to put the highlight back on it after the list reloads).</summary>
        public string Key { get; set; }
    }

    /// <summary>
    /// Kippix's own achievements screen: every game with achievements (search, sort, filter),
    /// one game's full list (filter all / unlocked / locked), and one achievement's details with AI tips
    /// from KippixOverlay when it's installed.
    /// </summary>
    public partial class ThemeApi
    {
        // Card and progress-bar widths for the grid and list layouts (must match the theme)
        // 0 grid, 1 list, 2 centred (like the list, but out to the screen edges with the headings centred)
        private int AchLayout { get { return settings.AchLayout < 0 ? 2 : Math.Min(2, settings.AchLayout); } }   // not chosen yet: centred
        // (0.90) the all-games page and a game's page each have their own layout (Layout (this page) in the settings dropdown)
        private int AchItemLayoutNow { get { return settings.AchItemLayout < 0 ? AchLayout : Math.Min(2, settings.AchItemLayout); } }
        private double AchGameBarMax { get { return AchGamesDesign > 0 ? DesignBarMax(AchGamesDesign) : AchLayout == 2 ? 640 : AchLayout == 1 ? 760 : 300; } }
        public double AchGameBarWidth { get { return AchGameBarMax; } }
        public double AchGameCardWidth { get { return AchGamesDesign > 0 ? DesignGameW(AchGamesDesign) : AchLayout == 2 ? 1862 : AchLayout == 1 ? 1750 : 574; } }
        public double AchItemCardWidth { get { return AchItemsDesign > 0 ? DesignItemW(AchItemsDesign) : AchItemLayoutNow == 2 ? 1862 : AchItemLayoutNow == 1 ? 1750 : 866; } }
        public bool IsAchListLayout { get { return AchLayout != 0; } }
        /// <summary>The page's margin: the all-games page by its layout, a game's page (and one achievement) by theirs.</summary>
        public Thickness AchPageMargin
        {
            get
            {
                bool games = achPage == "games";
                int d = games ? AchGamesDesign : AchItemsDesign;
                int l = games ? AchLayout : AchItemLayoutNow;
                return d > 0 ? DesignMargin(d) : l == 2 ? new Thickness(24, 30, 24, 30) : new Thickness(80, 30, 80, 30);
            }
        }
        /// <summary>Classic centred: each card's cover and text sit in the middle of the (full-width) card.</summary>
        public HorizontalAlignment AchCardContentAlignment { get { return AchLayout == 2 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch; } }
        public HorizontalAlignment AchItemCardContentAlignment { get { return AchItemLayoutNow == 2 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch; } }
        // Centred: the same width for every card's contents, so they line up down the middle (Auto otherwise)
        public double AchGameContentWidth { get { return AchLayout == 2 ? 860 : double.NaN; } }
        public double AchItemContentWidth { get { return AchItemLayoutNow == 2 ? 1240 : double.NaN; } }
        /// <summary>(0.90) The top of every achievements page (title, numbers, search, sort, filter) is centred.</summary>
        public HorizontalAlignment AchHeaderAlignment { get { return HorizontalAlignment.Center; } }
        private string AchLayoutText
        {
            get
            {
                return AchLayout == 2 ? Loc("LOC_KX_Centred", "Centred") : AchLayout == 1 ? Loc("LOC_KX_List", "List") : Loc("LOC_KX_Grid", "Grid");
            }
        }
        private static readonly Brush AchGlassBrush = Frozen(Color.FromArgb(0xF0, 0x0A, 0x0B, 0x0E));
        private static readonly Brush AchSolidBrush = Frozen(Color.FromRgb(0x0A, 0x0B, 0x0E));
        // (Game background: the see-through one under the game's picture and its dim layer, ThemeApi.AchBackground.cs)
        public Brush AchBackground { get { return AchBgMode == 2 ? AchSolidBrush : AchGlassBrush; } }
        private const int CoverPx = 200;             // decoded width of game covers on the cards
        private const int IconPx = 192;              // decoded width of achievement icons

        private volatile List<AchGameData> achData = new List<AchGameData>();
        private List<AchGameRow> achRows = new List<AchGameRow>();
        private DateTime achLoadedAt = DateTime.MinValue;
        private bool achLoading;
        private string achSource;
        private string achSearch = "";
        // what you chose is kept in the settings, so it's the same next time (and after Playnite restarts or updates)
        private int achSort { get { return Math.Max(0, Math.Min(3, settings.AchSort)); } set { settings.AchSort = value; } }                  // 0 recently played, 1 name, 2 most complete, 3 least complete
        private int achFilter { get { return Math.Max(0, Math.Min(3, settings.AchFilter)); } set { settings.AchFilter = value; } }            // 0 all, 1 in progress, 2 completed, 3 not started
        private int achItemFilter { get { return Math.Max(0, Math.Min(2, settings.AchItemFilter)); } set { settings.AchItemFilter = value; } } // 0 all, 1 unlocked, 2 locked
        private AchGameData achGame;
        private AchGameRow achGameRow;
        private string achPage = "games";     // games, game, item
        private string achPicker = "";        // sort, filter, items (an open dropdown)
        private int achItemsVersion;

        public ObservableCollection<AchGameRow> AchGames { get; private set; }
        public ObservableCollection<AchItemRow> AchItems { get; private set; }
        public ObservableCollection<PickOption> AchPickerOptions { get; private set; }

        public bool IsAchievementsOpen { get { return openPanel == "achievements"; } }
        public bool IsAchGamesPage { get { return achPage == "games"; } }
        public bool IsAchGamePage { get { return achPage == "game"; } }
        public bool IsAchItemPage { get { return achPage == "item"; } }
        public bool AchLoading { get { return achLoading; } }
        public string AchSummaryText { get; private set; }
        public string AchSearchText { get { return achSearch.Length > 0 ? "“" + achSearch + "”" : Loc("LOC_KX_Search", "Search"); } }
        public bool HasAchSearch { get { return achSearch.Length > 0; } }

        private string[] SortNames
        {
            get
            {
                return new[] { Loc("LOC_KX_SortRecent", "Recently played"), Loc("LOC_KX_SortName", "Name"),
                               Loc("LOC_KX_SortMost", "Most complete"), Loc("LOC_KX_SortLeast", "Least complete") };
            }
        }
        private string[] FilterNames
        {
            get
            {
                return new[] { Loc("LOC_KX_FilterAll", "All games"), Loc("LOC_KX_FilterInProgress", "In progress"),
                               Loc("LOC_KX_FilterCompleted", "Completed"), Loc("LOC_KX_FilterNotStarted", "Not started") };
            }
        }
        private string[] ItemFilterNames
        {
            get { return new[] { Loc("LOC_KX_ItemsAll", "All achievements"), Loc("LOC_KX_ItemsUnlocked", "Unlocked"), Loc("LOC_KX_ItemsLocked", "Locked") }; }
        }

        public string AchSortText { get { return SortNames[achSort]; } }
        public string AchFilterText { get { return FilterNames[achFilter]; } }
        public string AchItemFilterText { get { return ItemFilterNames[achItemFilter]; } }
        public string AchEmptyText { get; private set; }
        public bool HasAchEmptyText { get { return !string.IsNullOrEmpty(AchEmptyText); } }

        // dropdowns
        public bool IsAchPickerOpen { get { return achPicker.Length > 0; } }
        public bool IsAchSortPickerOpen { get { return achPicker == "sort"; } }
        public bool IsAchFilterPickerOpen { get { return achPicker == "filter"; } }
        public bool IsAchItemsPickerOpen { get { return achPicker == "items"; } }
        public bool IsAchSettingsPickerOpen { get { return achPicker == "settings" && achPage == "games"; } }
        public bool IsAchSettingsPicker2Open { get { return achPicker == "settings" && achPage == "game"; } }
        public ICommand AchSettingsCommand { get; private set; }

        // current game page
        public string AchGameName { get { return achGame != null ? achGame.Name : ""; } }
        public ImageSource AchGameCover { get; private set; }
        public string AchGameCountText { get; private set; }
        public double AchGameProgressWidth { get; private set; }
        public string AchItemsEmptyText { get; private set; }
        public bool HasAchItemsEmptyText { get { return !string.IsNullOrEmpty(AchItemsEmptyText); } }

        public ICommand AchSearchCommand { get; private set; }
        public ICommand AchClearSearchCommand { get; private set; }
        public ICommand AchSortCommand { get; private set; }
        public ICommand AchFilterCommand { get; private set; }
        public ICommand AchItemFilterCommand { get; private set; }

        private void InitAchievementCommands()
        {
            AchGames = new ObservableCollection<AchGameRow>();
            AchItems = new ObservableCollection<AchItemRow>();
            AchPickerOptions = new ObservableCollection<PickOption>();
            AchDetailFacts = new ObservableCollection<string>();
            AchSummaryText = "";
            AchSearchCommand = new RelayCommand(_ =>
            {
                ClosePicker(false);
                string before = achSearch;
                OpenKeyboard(Loc("LOC_KX_AchSearchPrompt", "Search games"), achSearch,
                             text => { achSearch = (text ?? "").Trim(); ApplyAchGames(); FocusPanelSoon(); },
                             text => { achSearch = (text ?? "").Trim(); ApplyAchGames(); },     // the games narrow down as you type
                             () => { FocusPanelSoon(); }, 60);   // B: the games found so far stay
            });
            AchClearSearchCommand = new RelayCommand(_ => { achSearch = ""; ApplyAchGames(); FocusPanelSoon(); });
            AchSortCommand = new RelayCommand(_ => TogglePicker("sort"));
            AchFilterCommand = new RelayCommand(_ => TogglePicker("filter"));
            AchItemFilterCommand = new RelayCommand(_ => TogglePicker("items"));
            AchSettingsCommand = new RelayCommand(_ => TogglePicker("settings"));
            InitAchDetailCommands();
        }

        private void OpenAchievementsView()
        {
            if (openPanel != "achievements") TogglePanel("achievements");
            achPage = "games";
            achPicker = "";
            achOpenedForGame = false;
            pendingAchGame = Guid.Empty;
            NotifyAch();
            // The list is preloaded after startup; only re-read it when it's old.
            if (achData.Count == 0 || (DateTime.Now - achLoadedAt).TotalMinutes > 30) LoadAchievements();
            else ApplyAchGames();
        }

        private void LoadAchievements()
        {
            if (achLoading) return;
            achLoading = true;
            AchEmptyText = "";
            NotifyAch();
            KippixShared.SafeWork.Queue(_ =>
            {
                List<AchGameData> data = null;
                string source = null;
                var covers = new Dictionary<Guid, string>();
                try
                {
                    data = AchievementSource.LoadAll(api, out source);
                    foreach (var d in data) covers[d.Id] = CoverOf(d.Id);
                }
                catch (Exception ex) { logger.Error(ex, "Kippix: loading achievements failed"); }

                // Show the list straight away, then fill in the covers as they decode.
                OnUi(() =>
                {
                    achLoading = false;
                    achData = data ?? new List<AchGameData>();
                    achSource = source;
                    achLoadedAt = DateTime.Now;
                    logger.Info("Kippix: achievements for " + achData.Count + " games from " + (source ?? "nothing"));
                    achRows = achData.Select(d => MakeGameRow(d, covers)).ToList();
                    AchGames.Clear();
                    ApplyAchGames();
                    if (openPanel == "achievements" && !OpenPendingAchGame()) FocusPanelSoon();
                    var rows = achRows.ToList();
                    KippixShared.SafeWork.Queue(__ =>
                    {
                        foreach (var row in rows)
                        {
                            var img = Images.Load(row.CoverFile, CoverPx);
                            if (img != null)
                            {
                                var r = row;
                                OnUi(() =>
                                {
                                    r.Cover = img;
                                    // that game's page already open (straight from the game page): its header picks the cover up too
                                    if (ReferenceEquals(r, achGameRow) && AchGameCover == null) { AchGameCover = img; Notify("AchGameCover"); }
                                });
                            }
                        }
                    });
                });
            });
        }

        /// <summary>For the overlay: one game's achievements from what the theme has loaded (any thread; nothing loads here).</summary>
        internal List<KippixOverlay.AchievementInfo> AchievementsForOverlay(Guid id)
        {
            var data = achData;
            var g = data != null ? data.FirstOrDefault(d => d != null && d.Id == id) : null;
            if (g == null || g.Items == null) return null;
            return g.Items.Where(i => i != null && !string.IsNullOrEmpty(i.Name)).Select(i => new KippixOverlay.AchievementInfo
            {
                Name = i.Name, Description = i.Description, Unlocked = i.Unlocked, Hidden = i.Hidden,
                Date = i.Date.HasValue ? (i.Date.Value.Kind == DateTimeKind.Utc ? i.Date.Value.ToLocalTime() : i.Date.Value) : (DateTime?)null,
                Icon = i.Icon, Rarity = i.Rarity
            }).ToList();
        }

        /// <summary>Reads the achievements quietly a few seconds after startup, so the screen opens instantly.</summary>
        private void PreloadAchievementsLater()
        {
            HookCompletion();   // the completion tracker listens for status changes from now on (ThemeApi.Completion.cs)
            try { if (api.ApplicationInfo.Mode != Playnite.SDK.ApplicationMode.Fullscreen) return; } catch (Exception) { }
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (s, e) => { t.Stop(); if (achData.Count == 0 && !achLoading) LoadAchievements(); };
            t.Start();
        }

        private AchGameRow MakeGameRow(AchGameData d, Dictionary<Guid, string> covers)
        {
            double f = Fraction(d);
            string cover;
            covers.TryGetValue(d.Id, out cover);
            var row = new AchGameRow
            {
                Data = d,
                Name = d.Name,
                CoverFile = cover,
                Fraction = f,
                CountText = d.Unlocked + " / " + d.Total + "   ·   " + Math.Floor(f * 100) + "%",
                SubText = d.LastUnlock.HasValue
                    ? string.Format(Loc("LOC_KX_LastUnlocked", "Last unlocked {0}"), d.LastUnlock.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture))
                    : Loc("LOC_KX_NoneUnlocked", "Nothing unlocked yet"),
                ProgressWidth = Math.Max(f > 0 ? 6 : 0, f * AchGameBarMax),
                IsComplete = d.Total > 0 && d.Unlocked == d.Total
            };
            row.OpenCommand = new RelayCommand(_ => OpenAchGame(row));
            DressAchGameRow(row);
            return row;
        }

        private void ApplyAchGames()
        {
            IEnumerable<AchGameRow> q = achRows;
            if (achSearch.Length > 0)
                q = q.Where(r => (r.Name ?? "").IndexOf(achSearch, StringComparison.OrdinalIgnoreCase) >= 0);
            switch (achFilter)
            {
                case 1: q = q.Where(r => r.Data.Unlocked > 0 && r.Data.Unlocked < r.Data.Total); break;
                case 2: q = q.Where(r => r.Data.Total > 0 && r.Data.Unlocked == r.Data.Total); break;
                case 3: q = q.Where(r => r.Data.Unlocked == 0); break;
            }
            switch (achSort)
            {
                case 1: q = q.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase); break;
                case 2: q = q.OrderByDescending(r => Fraction(r.Data)).ThenBy(r => r.Name); break;
                case 3: q = q.OrderBy(r => Fraction(r.Data)).ThenBy(r => r.Name); break;
                default: q = q.OrderByDescending(r => LastPlayed(r.Data)).ThenBy(r => r.Name); break;
            }
            SyncList(AchGames, q.ToList());

            int total = achData.Sum(d => d.Total), unlocked = achData.Sum(d => d.Unlocked);
            int perfect = achData.Count(d => d.Total > 0 && d.Unlocked == d.Total);
            AchSummaryText = achData.Count == 0 ? "" : string.Format(
                Loc("LOC_KX_AchSummary", "{0:N0} of {1:N0} unlocked   ·   {2} games   ·   {3} completed"),
                unlocked, total, achData.Count, perfect);
            BuildAchTally();

            if (achLoading) AchEmptyText = "";
            else if (achData.Count == 0) AchEmptyText = Loc("LOC_KX_AchNone", "No achievement data found. Install Playnite Achievements (or SuccessStory) and let it fetch your games.");
            else if (AchGames.Count == 0) AchEmptyText = Loc("LOC_KX_AchNoMatch", "No games match this search or filter.");
            else AchEmptyText = "";
            NotifyAch();
        }

        /// <summary>
        /// Puts a list into the given order by moving, adding and removing items rather than rebuilding it,
        /// so the cards already on screen are reused (that's what makes sorting instant).
        /// </summary>
        private static void SyncList<T>(ObservableCollection<T> list, List<T> target) where T : class
        {
            var keep = new HashSet<T>(target);
            for (int i = list.Count - 1; i >= 0; i--)
                if (!keep.Contains(list[i])) list.RemoveAt(i);
            for (int i = 0; i < target.Count; i++)
            {
                if (i < list.Count && ReferenceEquals(list[i], target[i])) continue;
                int j = list.IndexOf(target[i]);
                if (j >= 0) list.Move(j, i);
                else list.Insert(i, target[i]);
            }
            while (list.Count > target.Count) list.RemoveAt(list.Count - 1);
        }

        // ---------------- dropdowns ----------------

        private void TogglePicker(string kind)
        {
            if (achPicker == kind) { ClosePicker(true); return; }
            if (kind == "settings") { OpenSettingsPicker(0); return; }
            AchPickerOptions.Clear();
            string[] names = kind == "sort" ? SortNames : kind == "filter" ? FilterNames : ItemFilterNames;
            int current = kind == "sort" ? achSort : kind == "filter" ? achFilter : achItemFilter;
            PickOption selected = null;
            for (int i = 0; i < names.Length; i++)
            {
                int value = i;
                var o = new PickOption { Text = names[i], IsSelected = i == current, Command = new RelayCommand(_ => Pick(kind, value)) };
                if (o.IsSelected) selected = o;
                AchPickerOptions.Add(o);
            }
            achPicker = kind;
            NotifyPicker();
            string list = PickerListName(kind);
            var focus = selected;
            ui.BeginInvoke(new Action(() => UiTools.FocusItem(list, focus)), DispatcherPriority.ContextIdle);
        }

        private string PickerListName(string kind)
        {
            if (kind == "settings") return achPage == "game" ? "KX_AchSettingsPicker2" : "KX_AchSettingsPicker";
            return kind == "sort" ? "KX_AchSortPicker" : kind == "filter" ? "KX_AchFilterPicker" : "KX_AchItemsPicker";
        }

        private string PickerChipName(string kind)
        {
            if (kind == "settings") return achPage == "game" ? "KX_AchSettingsChip2" : "KX_AchSettingsChip";
            return kind == "sort" ? "KX_AchSortChip" : kind == "filter" ? "KX_AchFilterChip" : "KX_AchItemsChip";
        }

        /// <summary>The settings dropdown: background (game background / see-through / solid), layout (grid / list / centred) and
        /// the score next to the tally. Stays open while you change things.</summary>
        private void OpenSettingsPicker(int focusIndex) { OpenSettingsPicker(new[] { "bg", "layout", "tally", "redesign", "nextup" }[Math.Max(0, Math.Min(4, focusIndex))]); }

        private void OpenSettingsPicker(string focusKey)
        {
            achDesignSub = false;
            AchPickerOptions.Clear();
            AchPickerOptions.Add(new PickOption
            {
                Key = "bg",
                Text = Loc("LOC_KX_AchBackground", "Background"),
                Value = AchBgModeText,
                Command = new RelayCommand(_ => { CycleAchBgMode(); OpenSettingsPicker("bg"); })
            });
            AchPickerOptions.Add(new PickOption
            {
                Key = "tally",
                Text = Loc("LOC_KX_AchTallyMode", "Score next to the tally"),
                Value = AchTallyModeText,
                Command = new RelayCommand(_ => { CycleAchTallyMode(); OpenSettingsPicker("tally"); })
            });
            AchPickerOptions.Add(new PickOption
            {
                Key = "redesign",
                Text = Loc("LOC_KX_AchLayoutThisPage", "Layout (this page)"),
                Value = AchOptionName(AchPageOption),
                Command = new RelayCommand(_ => OpenAchDesignPicker())
            });
            AchPickerOptions.Add(new PickOption
            {
                Key = "nextup",
                Text = Loc("LOC_KX_AchNextUpOption", "Next up (the ones you're closest to)"),
                Value = settings.AchNextUp ? Loc("LOC_KX_On", "On") : Loc("LOC_KX_Off", "Off"),
                Command = new RelayCommand(_ => { ToggleAchNextUp(); OpenSettingsPicker("nextup"); })
            });
            achPicker = "settings";
            NotifyPicker();
            string list = PickerListName("settings");
            var focus = AchPickerOptions.FirstOrDefault(o => o.Key == focusKey) ?? AchPickerOptions[0];
            ui.BeginInvoke(new Action(() => UiTools.FocusItem(list, focus)), DispatcherPriority.ContextIdle);
        }

        private void ApplyAchLayout()
        {
            foreach (var r in achRows) r.ProgressWidth = Math.Max(r.Fraction > 0 ? 6 : 0, r.Fraction * AchGameBarMax);
            foreach (string n in new[] { "AchGameBarWidth", "AchGameCardWidth", "AchItemCardWidth", "IsAchListLayout", "AchPageMargin", "AchHeaderAlignment", "AchCardContentAlignment", "AchItemCardContentAlignment", "AchGameContentWidth", "AchItemContentWidth" }) Notify(n);
            NotifyAchDesign();
        }

        private void Pick(string kind, int value)
        {
            if (kind == "sort") achSort = value;
            else if (kind == "filter") achFilter = value;
            else achItemFilter = value;
            SaveSettings();
            ClosePicker(true);
            if (kind == "items") ApplyAchItems();
            else ApplyAchGames();
        }

        private void ClosePicker(bool focusChip)
        {
            if (achPicker.Length == 0) return;
            string chip = PickerChipName(achPicker);
            achPicker = "";
            NotifyPicker();
            if (focusChip) ui.BeginInvoke(new Action(() => UiTools.FocusByName(chip)), DispatcherPriority.ContextIdle);
        }

        private void NotifyPicker()
        {
            foreach (string n in new[] { "IsAchPickerOpen", "IsAchSortPickerOpen", "IsAchFilterPickerOpen", "IsAchItemsPickerOpen", "IsAchSettingsPickerOpen", "IsAchSettingsPicker2Open" })
                Notify(n);
        }

        // ---------------- one game ----------------

        private void OpenAchGame(AchGameRow row)
        {
            ClosePicker(false);
            var d = row.Data;
            achGame = d;
            achGameRow = row;
            achPage = "game";
            WantAchBg(d.Id, true);   // (Game background: this game's)
            double f = Fraction(d);
            AchGameCover = row.Cover;
            AchGameCountText = string.Format(Loc("LOC_KX_AchGameCount", "{0} of {1} unlocked   ·   {2}%"), d.Unlocked, d.Total, Math.Floor(f * 100));
            AchGameProgressWidth = f * 520;
            BuildAchItems(d);
            FillAchNextUp(d);   // NEXT UP under the header (ThemeApi.NextUp.cs)
            ApplyAchItems();
            FocusPanelSoon();
        }

        private List<AchItemRow> achItemRows = new List<AchItemRow>();
        /// <summary>A game's page with no achievements at all (opened from the game page): what it says instead (ThemeApi.Details.cs).</summary>
        private string achGameNoData = "";

        private void BuildAchItems(AchGameData d)
        {
            BuildAchGameTally(d);
            int version = ++achItemsVersion;
            Guid gameId = d.Id;
            // unlocked first (newest first), then locked (most common first, rarest last)
            achItemRows = d.Items
                .OrderByDescending(i => i.Unlocked)
                .ThenByDescending(i => i.Date ?? DateTime.MinValue)
                .ThenByDescending(i => i.Rarity ?? 0)
                .Select(i => MakeItemRow(i, gameId))
                .ToList();
            AchItems.Clear();

            var rows = achItemRows.ToList();
            KippixShared.SafeWork.Queue(_ =>
            {
                foreach (var row in rows)
                {
                    if (version != achItemsVersion) return;   // another game was opened
                    var img = Images.Load(row.Data.Icon, IconPx);
                    if (img != null)
                    {
                        var r = row;
                        OnUi(() =>
                        {
                            r.Icon = img;
                            if (ReferenceEquals(r, achItemRow)) { Notify("AchDetailIcon"); Notify("HasAchDetailIcon"); }
                        });
                    }
                }
            });
        }

        private AchItemRow MakeItemRow(AchItemData i, Guid gameId)
        {
            bool secret = i.Hidden && !i.Unlocked;
            var guide = GuideFor(gameId, i.Name);
            var row = new AchItemRow
            {
                Data = i,
                Name = secret ? Loc("LOC_KX_HiddenAch", "Hidden achievement") : i.Name,
                Description = secret ? Loc("LOC_KX_HiddenAchSub", "Keep playing to reveal it.") : i.Description,
                IsUnlocked = i.Unlocked,
                IsDimmed = !i.Unlocked && !i.LockedArt,
                IsMissable = !i.Unlocked && guide != null && guide.Missable,
                StatusText = i.Unlocked
                    ? (i.Date.HasValue ? i.Date.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : Loc("LOC_KX_Unlocked", "Unlocked"))
                    : Loc("LOC_KX_Locked", "Locked"),
                RarityText = i.Rarity.HasValue ? string.Format(CultureInfo.CurrentCulture, Loc("LOC_KX_Rarity", "{0:0.#}% of players"), i.Rarity.Value) : null
            };
            row.OpenCommand = new RelayCommand(_ => OpenAchItem(row));
            DressAchItemRow(row);
            return row;
        }

        private void ApplyAchItems()
        {
            if (achGame == null) { AchItems.Clear(); NotifyAch(); return; }
            IEnumerable<AchItemRow> q = achItemRows;
            if (achItemFilter == 1) q = q.Where(r => r.IsUnlocked);
            else if (achItemFilter == 2) q = q.Where(r => !r.IsUnlocked);
            if (AchItemsDesign == 3)   // Timeline: newest unlock first, then the locked ones
                q = q.OrderByDescending(r => r.IsUnlocked).ThenByDescending(r => r.Data != null && r.Data.Date.HasValue ? r.Data.Date.Value : DateTime.MinValue);
            SyncList(AchItems, q.ToList());
            AchItemsEmptyText = AchItems.Count == 0
                ? (achGame.Items == null || achGame.Items.Count == 0 ? achGameNoData : Loc("LOC_KX_AchItemsNone", "Nothing here with this filter."))
                : "";
            NotifyAch();
        }

        /// <summary>B on the achievements screen: close a dropdown, then step back a page, then close.</summary>
        private bool AchievementsBack()
        {
            if (achPicker.Length > 0 && achDesignSub) { OpenSettingsPicker("redesign"); return true; }   // Redesign's list: back to the settings
            if (achPicker.Length > 0) { ClosePicker(true); return true; }
            if (achPage == "item")
            {
                var row = achItemRow;
                achPage = "game";
                NotifyAch();
                if (BackToAchNextUp()) return true;   // opened from a NEXT UP chip: back on it
                if (row != null) ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_AchItemList", row)), DispatcherPriority.ContextIdle);
                else FocusPanelSoon();
                return true;
            }
            if (achPage == "game" && achOpenedForGame) return false;   // opened from the game page: B goes straight back there
            if (achPage == "game")
            {
                var row = achGameRow;
                achPage = "games";
                NotifyAch();
                if (row != null) ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_AchGameList", row)), DispatcherPriority.ContextIdle);
                else FocusPanelSoon();
                return true;
            }
            return false;
        }

        private void NotifyAch()
        {
            foreach (string n in new[] { "IsAchGamesPage", "IsAchGamePage", "IsAchItemPage", "AchLoading", "AchSummaryText", "AchSearchText", "HasAchSearch",
                                         "AchSortText", "AchFilterText", "AchItemFilterText", "AchEmptyText", "HasAchEmptyText",
                                         "AchGameName", "AchGameCover", "AchGameCountText", "AchGameProgressWidth",
                                         "AchItemsEmptyText", "HasAchItemsEmptyText", "AchNextUp", "HasAchNextUp",
                                         "AchGamePercentText", "AchGameRing", "AchStatUnlocked", "AchStatPercent", "AchStatPerfect", "AchStatGames", "IsAchHeroStats", "ShowAchSummaryLine" })
                Notify(n);
            NotifyPicker();
            ApplyAchDesignTemplates();
        }

        private static double Fraction(AchGameData d)
        {
            return d.Total > 0 ? (double)d.Unlocked / d.Total : 0;
        }

        private DateTime LastPlayed(AchGameData d)
        {
            try
            {
                var g = api.Database.Games.Get(d.Id);
                if (g != null && g.LastActivity.HasValue) return g.LastActivity.Value;
            }
            catch (Exception) { }
            return d.LastUnlock ?? DateTime.MinValue;
        }

        private string CoverOf(Guid id)
        {
            try
            {
                Game g = api.Database.Games.Get(id);
                if (g == null || string.IsNullOrEmpty(g.CoverImage)) return null;
                if (g.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return g.CoverImage;
                return api.Database.GetFullFilePath(g.CoverImage);
            }
            catch (Exception) { return null; }
        }
    }
}

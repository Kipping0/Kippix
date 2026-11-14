using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Settings › Search settings: every row on every settings page, found with the theme's own keyboard.
    /// The pages are read straight from the settings panel (each page's rows, with their text), so new rows are
    /// found without a list to keep up to date. A on a result opens its page with the highlight on that row.
    /// Rows that are hidden because they don't apply right now aren't offered.
    /// </summary>
    public partial class ThemeApi
    {
        public bool IsOptionsSettingsSearch { get { return optionsPage == "settingssearch"; } }
        private ICommand searchSettingsCmd;
        public ICommand SearchSettingsCommand { get { return searchSettingsCmd ?? (searchSettingsCmd = new RelayCommand(_ => { PlaySound("select"); OpenSettingsSearch(); })); } }
        private ObservableCollection<PickRow> settingsSearchRows;
        public ObservableCollection<PickRow> SettingsSearchRows { get { return settingsSearchRows ?? (settingsSearchRows = new ObservableCollection<PickRow>()); } }
        private string settingsQuery = "";
        private List<SettingHit> settingsIndex;   // read once while the search page is open (typing doesn't read the pages again)

        private class SettingHit
        {
            public string Page, Title, Sub, Path, Key;
            public FrameworkElement Row;
            public Action Run;   // instead of opening a page (e.g. time limits while Profiles are off: where to switch them on)
        }

        // every settings page a row can be on (pages that only make sense from somewhere else are left out)
        private static readonly string[] SearchPages =
        {
            "home", "cards", "cardinfo", "infoitems", "preview", "listinfo", "extrarows", "homerows", "badges", "look", "background", "board", "boardpages",
            "widgets", "widgetadd", "topbar", "shortcuts", "gamepage", "gamelayout", "gamemedia", "gameextras", "gamefriends", "gamesections",
            "audio", "general", "notify", "help", "layouts", "layoutpick", "placehome", "placegame",
            "storeopts", "storecurrency", "storetiles", "storerows", "galleryfolders", "access", "language", "seasonal", "weatherfx", "slotpins", "addons", "ups", "bgc", "folders",
            "community", "profiles"
        };

        /// <summary>Opens the search page and the keyboard straight away.</summary>
        private void OpenSettingsSearch()
        {
            settingsQuery = "";
            settingsIndex = null;
            SetOptionsPage("settingssearch");
            AskSettingsQuery();
        }

        private void AskSettingsQuery()
        {
            OpenKeyboard(Loc("LOC_KX_SettingsSearchPrompt", "Search the settings"), settingsQuery, typed =>
            {
                settingsQuery = (typed ?? "").Trim();
                BuildSettingsSearchRows();
                ui.BeginInvoke(new Action(() =>
                {
                    // the first result (or the search box when nothing matched)
                    var first = SettingsSearchRows.FirstOrDefault(r => r.Key != null && r.Key.StartsWith("hit:", StringComparison.Ordinal));
                    if (first == null || !UiTools.FocusCommandParameter("KX_OptionsPanel", first.Key)) UiTools.FocusInside("KX_OptionsPanel");
                }), DispatcherPriority.ContextIdle);
            }, typed =>
            {
                settingsQuery = (typed ?? "").Trim();
                BuildSettingsSearchRows();
            }, null, 40);
        }

        private void BuildSettingsSearchRows()
        {
            var rows = SettingsSearchRows;
            rows.Clear();
            rows.Add(new PickRow
            {
                Key = "query", Icon = "search",
                Text = settingsQuery.Length == 0 ? Loc("LOC_KX_SettingsSearchEmpty", "Type what you're looking for") : settingsQuery,
                Sub = Loc("LOC_KX_SettingsSearchSub", ""),
                Command = new RelayCommand(_ => { PlaySound("select"); AskSettingsQuery(); })
            });
            if (settingsQuery.Length == 0) return;
            List<SettingHit> hits;
            try { hits = FindSettings(settingsQuery); }
            catch (Exception ex) { logger.Warn("Kippix: settings search: " + ex.Message); hits = new List<SettingHit>(); }
            if (hits.Count == 0)
            {
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "none", Text = Loc("LOC_KX_SettingsSearchNone", "Nothing matches that") });
                return;
            }
            rows.Add(new PickRow { IsHeader = true, Text = hits.Count == 1 ? Loc("LOC_KX_SettingsOneResult", "1 SETTING") : string.Format(Loc("LOC_KX_SettingsNResults", "{0} SETTINGS"), hits.Count) });
            int i = 0;
            foreach (var h in hits.Take(60))
            {
                var hit = h;
                rows.Add(new PickRow
                {
                    Key = "hit:" + (i++), Icon = "search", Text = h.Title,
                    Sub = h.Path,
                    Command = new RelayCommand(_ => JumpToSetting(hit))
                });
            }
        }

        private void JumpToSetting(SettingHit h)
        {
            if (h.Run != null) { h.Run(); return; }
            PlaySound("select");
            SetOptionsPage(h.Page, false);
            var row = h.Row;
            string key = h.Key;
            ui.BeginInvoke(new Action(() =>
            {
                if (row != null && row.IsVisible && row.Focusable)
                {
                    row.BringIntoView();
                    Keyboard.Focus(row);
                    if (row.IsKeyboardFocusWithin) return;
                }
                if (key != null && UiTools.FocusCommandParameter("KX_OptionsPanel", key)) return;
                UiTools.FocusInside("KX_OptionsPanel");
            }), DispatcherPriority.ContextIdle);
        }

        // ---------------- reading the pages ----------------

        private List<SettingHit> FindSettings(string query)
        {
            var all = settingsIndex ?? (settingsIndex = IndexSettings());
            string q = query.ToLowerInvariant();
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Func<SettingHit, int> score = h =>
            {
                string t = (h.Title ?? "").ToLowerInvariant(), s = (h.Sub ?? "").ToLowerInvariant(), p = (h.Path ?? "").ToLowerInvariant();
                if (!words.All(w => t.Contains(w) || s.Contains(w) || p.Contains(w))) return -1;
                int sc = 0;
                if (t.StartsWith(q)) sc += 100;
                if (t.Contains(q)) sc += 50;
                sc += words.Count(w => t.Contains(w)) * 10;
                sc += words.Count(w => s.Contains(w)) * 3;
                return sc;
            };
            return all.Select(h => new { h, s = score(h) }).Where(x => x.s >= 0)
                      .OrderByDescending(x => x.s).ThenBy(x => x.h.Path).Select(x => x.h).ToList();
        }

        /// <summary>Every row on the settings pages, with its page. Read fresh each time (it's quick, and rows come and go).</summary>
        private List<SettingHit> IndexSettings()
        {
            var hits = new List<SettingHit>();
            var panel = UiTools.FindByName(Application.Current.MainWindow, "KX_OptionsPanel");
            if (panel == null) return hits;

            // the pages built in code: fill them first so their rows are there to read
            try { BuildAccessRows(); BuildLanguageRows(); BuildSeasonalRows(); BuildWeatherFxRows(); BuildSlotPinRows(); BuildAddonRows(); BuildFolderRows(); } catch (Exception) { }
            try { BuildProfileOptionRows(); } catch (Exception) { }   // (Profiles: built for the page that's open, the search here; Community is its own page: CommunitySearchExtras)

            // which IsOptions… switch shows which page (asked once per page, with the page set for a moment)
            var props = typeof(ThemeApi).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                        .Where(p => p.Name.StartsWith("IsOptions", StringComparison.Ordinal) && p.PropertyType == typeof(bool) && p.Name != "IsOptionsOpen" && p.Name != "IsOptionsRoot")
                                        .ToList();
            var pageOf = new Dictionary<string, string>();
            var titleOf = new Dictionary<string, string>();
            string keep = optionsPage;
            try
            {
                foreach (string page in SearchPages)
                {
                    optionsPage = page;
                    foreach (var p in props)
                    {
                        if (pageOf.ContainsKey(p.Name)) continue;
                        bool on = false;
                        try { on = (bool)p.GetValue(this, null); } catch (Exception) { }
                        if (on) pageOf[p.Name] = page;
                    }
                    titleOf[page] = OptionsTitle;
                }
            }
            finally { optionsPage = keep; }

            // each page's container (its Visibility is bound to IsOptions…)
            foreach (var host in LogicalAll(panel))
            {
                string prop = VisibilityPath(host);
                string page;
                if (prop == null || !pageOf.TryGetValue(prop, out page)) continue;
                string path = PagePath(page, titleOf);
                ReadRows(host, host, page, path, hits);
            }
            // words people search for that aren't on a row (time limits, the community: ThemeApi.CommunityHub.cs)
            try { CommunitySearchExtras(hits); } catch (Exception ex) { logger.Warn("Kippix: settings search extras: " + ex.Message); }
            // the same row reached twice (a page inside a page): once
            return hits.GroupBy(h => h.Page + "|" + h.Title + "|" + h.Sub).Select(g => g.First()).ToList();
        }

        private string PagePath(string page, Dictionary<string, string> titles)
        {
            var parts = new List<string>();
            string p = page;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(p); i++)
            {
                string t;
                if (!titles.TryGetValue(p, out t))
                {
                    string keep = optionsPage;
                    try { optionsPage = p; t = OptionsTitle; } finally { optionsPage = keep; }
                    titles[p] = t;
                }
                parts.Insert(0, t);
                p = OptionsParent(p);
            }
            return string.Join(" › ", parts.ToArray());
        }

        /// <summary>The IsOptions… name a page container's Visibility is bound to (null if it isn't one).</summary>
        private static string VisibilityPath(DependencyObject d)
        {
            var fe = d as FrameworkElement;
            if (fe == null) return null;
            var be = BindingOperations.GetBindingExpression(fe, UIElement.VisibilityProperty);
            if (be == null || be.ParentBinding == null || be.ParentBinding.Path == null) return null;
            string path = be.ParentBinding.Path.Path ?? "";
            int dot = path.LastIndexOf('.');
            string name = dot >= 0 ? path.Substring(dot + 1) : path;
            return name.StartsWith("IsOptions", StringComparison.Ordinal) ? name : null;
        }

        private static IEnumerable<DependencyObject> LogicalAll(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                yield return d;
                foreach (object c in LogicalTreeHelper.GetChildren(d))
                {
                    var dc = c as DependencyObject;
                    if (dc != null) stack.Push(dc);
                }
            }
        }

        /// <summary>The rows inside one page (buttons and switches, and lists of rows made in code).</summary>
        private void ReadRows(DependencyObject node, DependencyObject host, string page, string path, List<SettingHit> hits)
        {
            foreach (object c in LogicalTreeHelper.GetChildren(node))
            {
                var d = c as DependencyObject;
                if (d == null) continue;
                var fe = d as FrameworkElement;
                if (fe != null && !ReferenceEquals(fe, host) && fe.Visibility != Visibility.Visible)
                {
                    // hidden because it doesn't apply now: not offered (a page inside a page has its own container)
                    continue;
                }
                if (VisibilityPath(d) != null) continue;   // another page
                var ic = d as ItemsControl;
                if (ic != null && !(d is ButtonBase) && ic.ItemsSource != null)
                {
                    // rows made in code (an ItemsControl with its rows written in the XAML is read like any panel below)
                    foreach (object item in ic.ItemsSource)
                    {
                        var pr = item as PickRow;
                        if (pr == null || pr.IsHeader || pr.IsInfo || string.IsNullOrWhiteSpace(pr.Text)) continue;
                        hits.Add(new SettingHit { Page = page, Title = pr.Text, Sub = pr.Sub, Path = path, Key = pr.Key });
                    }
                    continue;
                }
                if (d is ButtonBase)
                {
                    var texts = new List<string>();
                    CollectTexts(d, texts);
                    if (texts.Count > 0)
                    {
                        var bb = (ButtonBase)d;
                        hits.Add(new SettingHit
                        {
                            Page = page, Title = texts[0], Sub = texts.Count > 1 ? texts[1] : null, Path = path, Row = bb,
                            Key = bb.CommandParameter as string
                        });
                    }
                    continue;
                }
                ReadRows(d, host, page, path, hits);
            }
        }

        private static void CollectTexts(DependencyObject d, List<string> into)
        {
            var tb = d as TextBlock;
            if (tb != null)
            {
                if (tb.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(tb.Text)) into.Add(tb.Text.Trim());
                return;
            }
            var fe = d as FrameworkElement;
            if (fe != null && fe.Visibility != Visibility.Visible) return;
            var cc = d as ContentControl;
            if (cc != null && cc.Content is string && !string.IsNullOrWhiteSpace((string)cc.Content)) into.Add(((string)cc.Content).Trim());
            foreach (object c in LogicalTreeHelper.GetChildren(d))
            {
                var dc = c as DependencyObject;
                if (dc != null) CollectTexts(dc, into);
            }
        }

        private void SettingsSearchOpened(string page)
        {
            Notify("IsOptionsSettingsSearch");
            if (page == "settingssearch") BuildSettingsSearchRows();
        }
    }
}

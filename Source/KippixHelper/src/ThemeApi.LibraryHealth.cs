using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Library health (More pages › Library health): a check of the library, off the UI thread with progress, for missing
    /// pictures (cover, background, icon, logo) and info (description, genres, release date), duplicates (the same game in two
    /// stores: shown; the same store and game id twice: true duplicates), installed games whose folder has gone, play actions
    /// that can't work and huge pictures (over 8 MB). Fixes are one press, never asked about, and can be undone (Undo chip):
    /// Playnite's own metadata download for the games missing info (its downloader, by reflection; only empty fields are filled),
    /// hide the extra copies of true duplicates, mark missing installs as not installed, and a tag that finds the games in desktop
    /// mode for what only desktop mode can fix. Nothing is ever deleted. Each category opens its own page (A on its row).
    /// </summary>
    public partial class ThemeApi
    {
        private class HealthIssue
        {
            public Guid GameId;
            public string Name;
            public string Detail;
            public bool Extra;          // a true duplicate's extra copy (the one hidden by "hide the extra copies")
        }

        private static readonly string[] HealthCats = { "cover", "background", "icon", "logo", "desc", "genres", "release", "dupexact", "dupname", "noinstall", "play", "bigimg" };
        private Dictionary<string, List<HealthIssue>> healthResults;
        private int healthChecked;
        private DateTime healthAt = DateTime.MinValue;
        private volatile bool healthScanning;
        private volatile int healthDone, healthTotal;
        private int healthScanGen;
        private DispatcherTimer healthTimer;
        private List<Action> healthUndo;
        private string healthUndoText;
        private volatile bool healthDownloading;
        private string healthDownloadText;
        private const long HugeImageBytes = 8L * 1024 * 1024;
        private const string HealthTagName = "Kippix: fix in desktop mode";

        private string HealthCatName(string c)
        {
            switch (c)
            {
                case "cover": return Loc("LOC_KX_HealthCover", "Missing cover");
                case "background": return Loc("LOC_KX_HealthBackground", "Missing background");
                case "icon": return Loc("LOC_KX_HealthIcon", "Missing icon");
                case "logo": return Loc("LOC_KX_HealthLogo", "Missing logo");
                case "desc": return Loc("LOC_KX_HealthDesc", "Missing description");
                case "genres": return Loc("LOC_KX_HealthGenres", "Missing genres");
                case "release": return Loc("LOC_KX_HealthRelease", "Missing release date");
                case "dupexact": return Loc("LOC_KX_HealthDupExact", "Duplicates");
                case "dupname": return Loc("LOC_KX_HealthDupName", "In more than one store");
                case "noinstall": return Loc("LOC_KX_HealthNoInstall", "Installed, but the folder is gone");
                case "play": return Loc("LOC_KX_HealthPlay", "Play action that can't work");
                case "bigimg": return Loc("LOC_KX_HealthBigImg", "Huge pictures");
            }
            return c;
        }

        private string HealthCatBlurb(string c)
        {
            switch (c)
            {
                case "cover":
                case "background":
                case "icon":
                case "desc":
                case "genres":
                case "release": return Loc("LOC_KX_HealthMetaBlurb", "Playnite's metadata download can fill them in");
                case "logo": return Loc("LOC_KX_HealthLogoBlurb", "Logos come from the Extra Metadata Loader add-on");
                case "dupexact": return Loc("LOC_KX_HealthDupExactBlurb", "The same game from the same store more than once: the extra copies can be hidden");
                case "dupname": return Loc("LOC_KX_HealthDupNameBlurb", "The same name from different stores (just so you know)");
                case "noinstall": return Loc("LOC_KX_HealthNoInstallBlurb", "Playnite thinks they're installed: they can be marked as not installed");
                case "play": return Loc("LOC_KX_HealthPlayBlurb", "The file it starts isn't there (fix in desktop mode)");
                case "bigimg": return Loc("LOC_KX_HealthBigImgBlurb", "Pictures over 8 MB slow the screens down");
            }
            return "";
        }

        private static string HealthCatIcon(string c)
        {
            switch (c)
            {
                case "cover": case "background": case "icon": case "logo": case "bigimg": return "gallery";
                case "desc": case "genres": case "release": return "note";
                case "dupexact": case "dupname": return "copy";
                case "noinstall": return "folder";
                case "play": return "play";
            }
            return "info";
        }

        private static bool HealthIsMeta(string c) { return c == "cover" || c == "background" || c == "icon" || c == "desc" || c == "genres" || c == "release"; }

        // =====================================================================
        // The pages
        // =====================================================================

        private void BuildHealthPage(string cat)
        {
            // it changes Playnite's library: a profile whose settings need the PIN asks for it first
            if (SettingsLockedNow)
            {
                PageSub = Loc("LOC_KX_PageHealthSub", "Missing pictures and info, duplicates, broken installs");
                SetPageStatus(Loc("LOC_KX_HealthLocked", "This changes Playnite's library, so it needs the grown-ups' PIN."));
                PageRows.Add(new PageRow { Text = Loc("LOC_KX_HealthEnterPin", "Enter the PIN"), Icon = "lock", Key = "pin",
                                           Command = new RelayCommand(_ => AskPin(Loc("LOC_KX_PinFor2", "The PIN"), () => { unlockedNow.Add("settings"); pageQuietOpen = true; OpenPage("health", cat, false); })) });
                return;
            }
            if (!string.IsNullOrEmpty(cat)) { BuildHealthCatPage(cat); return; }
            PageSub = Loc("LOC_KX_PageHealthSub", "Missing pictures and info, duplicates, broken installs");
            AddPageChip(Loc("LOC_KX_HealthAgain", "Check again"), "refresh", () => { StartHealthScan(); pageQuietOpen = true; OpenPage("health", "", false); FocusPageSoon(PageChips.FirstOrDefault(c => c.Icon == "refresh")); });
            AddHealthUndoChip();
            if (healthResults == null || (!healthScanning && (DateTime.Now - healthAt).TotalMinutes > 10)) StartHealthScan();
            if (healthScanning) { HealthProgressStatus(); return; }
            if (healthResults == null) return;

            PageStats = new List<PageStat>
            {
                new PageStat { Value = healthChecked.ToString("N0", CultureInfo.CurrentCulture), Label = Loc("LOC_KX_HealthStatChecked", "games checked") },
                new PageStat { Value = healthResults.Count(kv => kv.Value.Count > 0).ToString(CultureInfo.CurrentCulture), Label = Loc("LOC_KX_HealthStatKinds", "kinds of problem") }
            };
            string group = null;
            foreach (string c in HealthCats)
            {
                List<HealthIssue> list;
                if (!healthResults.TryGetValue(c, out list) || list.Count == 0) continue;
                string g = c == "cover" || c == "background" || c == "icon" || c == "logo" || c == "bigimg" ? Loc("LOC_KX_HealthGroupPictures", "PICTURES")
                         : HealthIsMeta(c) ? Loc("LOC_KX_HealthGroupInfo", "INFO")
                         : c.StartsWith("dup", StringComparison.Ordinal) ? Loc("LOC_KX_HealthGroupDup", "DUPLICATES")
                         : Loc("LOC_KX_HealthGroupInstalls", "INSTALLS AND PLAYING");
                if (g != group) { AddPageHeader(g); group = g; }
                string key = c;
                int n = c == "dupexact" ? list.Count(i => i.Extra) : list.Count;
                PageRows.Add(new PageRow
                {
                    Text = HealthCatName(c), Sub = HealthCatBlurb(c), Icon = HealthCatIcon(c), Key = "cat:" + c,
                    Right = n == 1 ? Loc("LOC_KX_OneGame", "1 game") : string.Format(Loc("LOC_KX_NGames", "{0} games"), n),
                    Command = new RelayCommand(_ => OpenPage("health", key, true))
                });
            }
            SetPageStatus((healthResults.All(kv => kv.Value.Count == 0) ? Loc("LOC_KX_HealthAllGood", "All good: nothing to fix.") + "  " : "")
                          + string.Format(Loc("LOC_KX_HealthWhen", "Checked {0}."), AgoTextShort(healthAt))
                          + (healthDownloading ? "  " + healthDownloadText : ""));
        }

        private string AgoTextShort(DateTime when)
        {
            double m = (DateTime.Now - when).TotalMinutes;
            return m < 1 ? Loc("LOC_KX_JustNow", "just now") : string.Format(Loc("LOC_KX_MinutesAgo", "{0} minutes ago"), (int)m);
        }

        private void AddHealthUndoChip()
        {
            if (healthUndo == null || healthUndo.Count == 0) return;
            AddPageChip(string.Format(Loc("LOC_KX_HealthUndo", "Undo: {0}"), healthUndoText ?? ""), "refresh", UndoHealth);
        }

        private void BuildHealthCatPage(string cat)
        {
            PageTitle = HealthCatName(cat);
            PageSub = HealthCatBlurb(cat);
            List<HealthIssue> list = null;
            if (healthResults == null || !healthResults.TryGetValue(cat, out list)) list = new List<HealthIssue>();
            AddHealthUndoChip();
            var ids = list.Select(i => i.GameId).Distinct().ToList();
            string desktopSteps = null;
            if (HealthIsMeta(cat))
            {
                if (ids.Count > 0 && !healthDownloading)
                    AddPageChip(string.Format(Loc("LOC_KX_HealthDownload", "Download what's missing ({0})"), ids.Count), "download", () => HealthDownloadMetadata(ids));
                desktopSteps = Loc("LOC_KX_HealthMetaSteps", "Uses Playnite's metadata sources and only fills in what's empty. Or in desktop mode: select the games, right-click › Edit… › Download Metadata.");
            }
            if (cat == "logo") desktopSteps = Loc("LOC_KX_HealthLogoSteps", "In desktop mode, with the Extra Metadata Loader add-on: select the games, right-click › Extra Metadata Loader › Logos › Download logos.");
            if (cat == "play") desktopSteps = Loc("LOC_KX_HealthPlaySteps", "In desktop mode: right-click the game › Edit… › Actions, and point the play action at the game's file (or remove the game and add it again).");
            if (cat == "bigimg") desktopSteps = Loc("LOC_KX_HealthBigImgSteps", "In desktop mode: right-click the game › Edit… › Media, and choose a smaller picture (Playnite's metadata download picks sensible sizes).");
            if (cat == "dupname") desktopSteps = Loc("LOC_KX_HealthDupNameSteps", "Nothing's wrong: you own it in more than one place. Hide the copy you don't use from its page if you like.");
            if ((cat == "play" || cat == "bigimg" || cat == "logo") && ids.Count > 0)
                AddPageChip(string.Format(Loc("LOC_KX_HealthTag", "Tag them for desktop mode ({0})"), ids.Count), "tag", () => HealthTagGames(ids));
            if (cat == "dupexact" && list.Any(i => i.Extra))
                AddPageChip(string.Format(Loc("LOC_KX_HealthHideExtras", "Hide the extra copies ({0})"), list.Count(i => i.Extra)), "hide", () => HealthHide(list.Where(i => i.Extra).Select(i => i.GameId).ToList(), true));
            if (cat == "noinstall" && ids.Count > 0)
                AddPageChip(string.Format(Loc("LOC_KX_HealthUninstallAll", "Mark all as not installed ({0})"), ids.Count), "remove", () => HealthMarkNotInstalled(ids, true));

            foreach (var i in list.Take(300))
            {
                var issue = i;
                string right = Loc("LOC_KX_HealthOpen", "");
                Action act = () => OpenGameFromPage(issue.GameId);
                if (cat == "dupexact" && issue.Extra) { right = Loc("LOC_KX_HealthHideOne", "A  Hide this copy"); act = () => HealthHide(new List<Guid> { issue.GameId }, false); }
                if (cat == "noinstall") { right = Loc("LOC_KX_HealthMarkOne", "A  Mark as not installed"); act = () => HealthMarkNotInstalled(new List<Guid> { issue.GameId }, false); }
                PageRows.Add(new PageRow
                {
                    Text = issue.Name, Sub = issue.Detail, ImagePath = cat == "cover" ? null : CoverOf(issue.GameId), Icon = cat == "cover" ? "gallery" : null,
                    Key = issue.GameId.ToString(), Right = right, Command = new RelayCommand(_ => act())
                });
            }
            var status = new StringBuilder();
            if (list.Count == 0) status.Append(Loc("LOC_KX_HealthNoneHere", "Nothing here any more."));
            if (list.Count > 300) status.Append(string.Format(Loc("LOC_KX_HealthMore", "The first 300 of {0}."), list.Count) + " ");
            if (healthDownloading) status.Append(healthDownloadText + " ");
            if (desktopSteps != null) status.Append(desktopSteps);
            SetPageStatus(status.ToString().Trim());
            LoadPageImages();
        }

        // =====================================================================
        // The check (a worker thread; the page shows how far it's got)
        // =====================================================================

        private void StartHealthScan()
        {
            int gen = ++healthScanGen;
            healthScanning = true;
            healthDone = 0;
            healthTotal = 0;
            string extraMeta = null, dataPath = null;
            try { extraMeta = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games"); dataPath = api.Paths.ConfigurationPath; } catch (Exception) { }
            if (healthTimer == null)
            {
                healthTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(300) };
                healthTimer.Tick += (s, e) =>
                {
                    if (openPanel != "page" || pageKey != "health") { if (!healthScanning) healthTimer.Stop(); return; }
                    if (healthScanning) { HealthProgressStatus(); return; }
                    healthTimer.Stop();
                    // done: the page again, the highlight where it was
                    var was = FocusedPageItem();
                    pageQuietOpen = true;
                    OpenPage("health", pageArg, false);
                    var row = was as PageRow;
                    var chip = was as PickRow;
                    if (row != null) { var again = PageRows.FirstOrDefault(r => r.Key == row.Key); if (again != null) FocusPageSoon(again); }
                    else if (chip != null) { var again = PageChips.FirstOrDefault(c => c.Icon == chip.Icon); if (again != null) FocusPageSoon(again); }
                };
            }
            healthTimer.Start();
            KippixShared.SafeWork.Queue(_ =>
            {
                var res = HealthCats.ToDictionary(c => c, c => new List<HealthIssue>());
                int checkedCount = 0;
                try
                {
                    var games = api.Database.Games.Where(g => g != null && !g.Hidden).ToList();
                    healthTotal = games.Count;
                    bool logos = !string.IsNullOrEmpty(extraMeta) && Directory.Exists(extraMeta) && Directory.EnumerateDirectories(extraMeta).Any(d => File.Exists(Path.Combine(d, "Logo.png")));
                    var emulators = new HashSet<Guid>();
                    try { foreach (var em in api.Database.Emulators) emulators.Add(em.Id); } catch (Exception) { }
                    foreach (var g in games)
                    {
                        if (gen != healthScanGen) return;
                        healthDone++;
                        checkedCount++;
                        try { HealthCheckGame(g, res, logos, extraMeta, emulators); } catch (Exception) { }
                    }
                    HealthDuplicates(games, res);
                    foreach (var k in res.Keys.ToList()) res[k] = res[k].OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex) { logger.Warn("Kippix: library health: " + ex.Message); }
                OnUi(() =>
                {
                    if (gen != healthScanGen) return;
                    healthResults = res;
                    healthChecked = checkedCount;
                    healthAt = DateTime.Now;
                    healthScanning = false;
                    logger.Info("Kippix: library health: checked " + checkedCount + " games: " + string.Join(", ", res.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key + " " + kv.Value.Count).ToArray()));
                });
            });
        }

        private void HealthProgressStatus()
        {
            int total = healthTotal, done = healthDone;
            SetPageStatus(total > 0 ? string.Format(Loc("LOC_KX_HealthChecking", "Checking your library… {0} of {1}"), done.ToString("N0", CultureInfo.CurrentCulture), total.ToString("N0", CultureInfo.CurrentCulture))
                                    : Loc("LOC_KX_HealthStarting", "Checking your library…"));
        }

        private bool HealthFileMissing(string field, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrEmpty(field)) return true;
            if (field.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            string path = null;
            try { path = api.Database.GetFullFilePath(field); } catch (Exception) { }
            if (string.IsNullOrEmpty(path)) return true;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return true;
                bytes = fi.Length;
            }
            catch (Exception) { }
            return false;
        }

        private void HealthCheckGame(Game g, Dictionary<string, List<HealthIssue>> res, bool logos, string extraMeta, HashSet<Guid> emulators)
        {
            string name = string.IsNullOrEmpty(g.Name) ? "?" : g.Name;
            Action<string, string> add = (c, detail) => res[c].Add(new HealthIssue { GameId = g.Id, Name = name, Detail = detail ?? HealthSourceText(g) });
            long cb, bb, ib;
            if (HealthFileMissing(g.CoverImage, out cb)) add("cover", null);
            if (HealthFileMissing(g.BackgroundImage, out bb)) add("background", null);
            if (HealthFileMissing(g.Icon, out ib)) add("icon", null);
            if (logos && !File.Exists(Path.Combine(extraMeta, g.Id.ToString(), "Logo.png"))) add("logo", null);
            if (string.IsNullOrWhiteSpace(g.Description)) add("desc", null);
            if (g.GenreIds == null || g.GenreIds.Count == 0) add("genres", null);
            if (g.ReleaseDate == null) add("release", null);
            var big = new List<string>();
            if (cb > HugeImageBytes) big.Add(string.Format(Loc("LOC_KX_HealthBigCover", "cover {0}"), SizeText(cb)));
            if (bb > HugeImageBytes) big.Add(string.Format(Loc("LOC_KX_HealthBigBg", "background {0}"), SizeText(bb)));
            if (ib > HugeImageBytes) big.Add(string.Format(Loc("LOC_KX_HealthBigIcon", "icon {0}"), SizeText(ib)));
            if (big.Count > 0) add("bigimg", string.Join(" · ", big.ToArray()));

            if (!g.IsInstalled) return;
            string dir = g.InstallDirectory;
            if (!string.IsNullOrEmpty(dir))
            {
                try { dir = api.ExpandGameVariables(g, dir); } catch (Exception) { }
                bool missing = false;
                try { missing = dir.IndexOf('{') < 0 && !Directory.Exists(dir); } catch (Exception) { }
                if (missing) add("noinstall", dir);
            }
            // play actions: only the ones Playnite starts itself (a store's games start through their store add-on)
            var actions = g.GameActions != null ? g.GameActions.Where(a => a != null && a.IsPlayAction).ToList() : new List<GameAction>();
            if (actions.Count == 0)
            {
                if (g.PluginId == Guid.Empty) add("play", Loc("LOC_KX_HealthNoAction", "No play action"));
                return;
            }
            foreach (var a in actions)
            {
                if (a.Type == GameActionType.Emulator)
                {
                    if (a.EmulatorId != Guid.Empty && !emulators.Contains(a.EmulatorId)) { add("play", string.Format(Loc("LOC_KX_HealthNoEmulator", "“{0}”: its emulator isn't in Playnite any more"), a.Name ?? "")); return; }
                    continue;
                }
                if (a.Type != GameActionType.File || string.IsNullOrEmpty(a.Path)) continue;
                string path = a.Path;
                try { path = api.ExpandGameVariables(g, path); } catch (Exception) { }
                if (path.IndexOf('{') >= 0) continue;   // (a variable Playnite fills in only when it starts)
                try
                {
                    if (!Path.IsPathRooted(path))
                    {
                        string baseDir = !string.IsNullOrEmpty(a.WorkingDir) ? a.WorkingDir : dir;
                        if (string.IsNullOrEmpty(baseDir)) continue;
                        try { baseDir = api.ExpandGameVariables(g, baseDir); } catch (Exception) { }
                        path = Path.Combine(baseDir, path);
                    }
                    if (!File.Exists(path)) { add("play", string.Format(Loc("LOC_KX_HealthNoFile", "“{0}” starts {1}, which isn't there"), a.Name ?? "", path)); return; }
                }
                catch (Exception) { }
            }
        }

        private string HealthSourceText(Game g)
        {
            try
            {
                string src = g.Source != null && !string.IsNullOrEmpty(g.Source.Name) ? g.Source.Name : "";
                string plat = g.Platforms != null && g.Platforms.Count > 0 && g.Platforms[0] != null ? g.Platforms[0].Name : "";
                return string.Join(" · ", new[] { src, plat }.Where(s => !string.IsNullOrEmpty(s)).ToArray());
            }
            catch (Exception) { return ""; }
        }

        private static string HealthNameKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder();
            foreach (char ch in name.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private void HealthDuplicates(List<Game> games, Dictionary<string, List<HealthIssue>> res)
        {
            // the same store and game id more than once: one is kept (the most played, then the oldest), the rest are extra copies
            foreach (var grp in games.Where(g => g.PluginId != Guid.Empty && !string.IsNullOrEmpty(g.GameId)).GroupBy(g => g.PluginId + "|" + g.GameId).Where(x => x.Count() > 1))
            {
                var ordered = grp.OrderByDescending(g => g.Playtime).ThenBy(g => g.Added ?? DateTime.MaxValue).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var g = ordered[i];
                    res["dupexact"].Add(new HealthIssue
                    {
                        GameId = g.Id, Name = g.Name ?? "?", Extra = i > 0,
                        Detail = (i == 0 ? Loc("LOC_KX_HealthKept", "Kept (the most played)") : Loc("LOC_KX_HealthExtraCopy", "An extra copy"))
                                 + "  ·  " + HealthSourceText(g) + (g.Playtime > 0 ? "  ·  " + HoursShort(g.Playtime / 3600.0) : "")
                    });
                }
            }
            // the same name from different stores
            foreach (var grp in games.GroupBy(g => HealthNameKey(g.Name)).Where(x => x.Key.Length > 0 && x.Select(g => g.PluginId).Distinct().Count() > 1))
                foreach (var g in grp)
                {
                    var others = grp.Where(o => o.PluginId != g.PluginId).Select(o => o.Source != null && !string.IsNullOrEmpty(o.Source.Name) ? o.Source.Name : "?").Distinct().ToArray();
                    res["dupname"].Add(new HealthIssue { GameId = g.Id, Name = g.Name ?? "?", Detail = HealthSourceText(g) + "  ·  " + string.Format(Loc("LOC_KX_HealthAlsoOn", "also on {0}"), string.Join(", ", others)) });
                }
        }

        // =====================================================================
        // Fixes (each one can be undone)
        // =====================================================================

        private void SetHealthUndo(string text, List<Action> undo)
        {
            healthUndo = undo;
            healthUndoText = text;
        }

        private void UndoHealth()
        {
            var undo = healthUndo;
            if (undo == null) return;
            healthUndo = null;
            try { using (api.Database.BufferedUpdate()) foreach (var a in undo) try { a(); } catch (Exception) { } }
            catch (Exception ex) { logger.Warn("Kippix: library health undo: " + ex.Message); }
            PlaySound("back");
            StartHealthScan();
            pageQuietOpen = true;
            OpenPage("health", pageArg, false);
        }

        private void HealthHide(List<Guid> ids, bool all)
        {
            var undo = new List<Action>();
            int n = 0;
            try
            {
                using (api.Database.BufferedUpdate())
                    foreach (var id in ids)
                    {
                        var g = api.Database.Games.Get(id);
                        if (g == null || g.Hidden) continue;
                        g.Hidden = true;
                        api.Database.Games.Update(g);
                        n++;
                        var gid = id;
                        undo.Add(() => { var x = api.Database.Games.Get(gid); if (x != null) { x.Hidden = false; api.Database.Games.Update(x); } });
                    }
            }
            catch (Exception ex) { logger.Warn("Kippix: library health: hiding: " + ex.Message); }
            HealthFixed(string.Format(n == 1 ? Loc("LOC_KX_HealthHidOne", "1 copy hidden") : Loc("LOC_KX_HealthHidN", "{0} copies hidden"), n), undo, ids);
        }

        private void HealthMarkNotInstalled(List<Guid> ids, bool all)
        {
            var undo = new List<Action>();
            int n = 0;
            try
            {
                using (api.Database.BufferedUpdate())
                    foreach (var id in ids)
                    {
                        var g = api.Database.Games.Get(id);
                        if (g == null || !g.IsInstalled) continue;
                        g.IsInstalled = false;   // (the folder is kept in the game's details, so putting it back is exact)
                        api.Database.Games.Update(g);
                        n++;
                        var gid = id;
                        undo.Add(() => { var x = api.Database.Games.Get(gid); if (x != null) { x.IsInstalled = true; api.Database.Games.Update(x); } });
                    }
            }
            catch (Exception ex) { logger.Warn("Kippix: library health: not installed: " + ex.Message); }
            HealthFixed(string.Format(n == 1 ? Loc("LOC_KX_HealthMarkedOne", "1 game marked as not installed") : Loc("LOC_KX_HealthMarkedN", "{0} games marked as not installed"), n), undo, ids);
        }

        private void HealthTagGames(List<Guid> ids)
        {
            var undo = new List<Action>();
            int n = 0;
            try
            {
                var tag = api.Database.Tags.FirstOrDefault(t => t != null && t.Name == HealthTagName);
                bool newTag = tag == null;
                if (newTag) { tag = new Tag(HealthTagName); api.Database.Tags.Add(tag); }
                var tagId = tag.Id;
                using (api.Database.BufferedUpdate())
                    foreach (var id in ids)
                    {
                        var g = api.Database.Games.Get(id);
                        if (g == null) continue;
                        if (g.TagIds == null) g.TagIds = new List<Guid>();
                        if (g.TagIds.Contains(tagId)) continue;
                        g.TagIds.Add(tagId);
                        api.Database.Games.Update(g);
                        n++;
                        var gid = id;
                        undo.Add(() => { var x = api.Database.Games.Get(gid); if (x != null && x.TagIds != null && x.TagIds.Remove(tagId)) api.Database.Games.Update(x); });
                    }
                if (newTag) undo.Add(() => { try { if (!api.Database.Games.Any(x => x.TagIds != null && x.TagIds.Contains(tagId))) api.Database.Tags.Remove(tagId); } catch (Exception) { } });
            }
            catch (Exception ex) { logger.Warn("Kippix: library health: tagging: " + ex.Message); }
            HealthFixed(string.Format(Loc("LOC_KX_HealthTagged", "{0} tagged “{1}”: in desktop mode, filter by that tag"), n, HealthTagName), undo, null);
        }

        /// <summary>After a fix: the results follow (fixed games leave the list), Undo comes up, the page is built again.</summary>
        private void HealthFixed(string text, List<Action> undo, List<Guid> gone)
        {
            PlaySound(undo.Count > 0 ? "select" : "back");
            if (undo.Count > 0) SetHealthUndo(text, undo);
            if (gone != null && healthResults != null && pageArg != null && healthResults.ContainsKey(pageArg))
            {
                var list = healthResults[pageArg];
                if (pageArg == "dupexact")
                {
                    // hidden copies leave the list, and a game with no other copy showing isn't a duplicate any more
                    Func<Game, string> key = g => g.PluginId + "|" + g.GameId;
                    var counts = new Dictionary<string, int>();
                    foreach (var i in list)
                    {
                        var g = api.Database.Games.Get(i.GameId);
                        if (g == null || g.Hidden) continue;
                        int c;
                        counts.TryGetValue(key(g), out c);
                        counts[key(g)] = c + 1;
                    }
                    list.RemoveAll(i =>
                    {
                        var g = api.Database.Games.Get(i.GameId);
                        int c;
                        return g == null || g.Hidden || !counts.TryGetValue(key(g), out c) || c < 2;
                    });
                }
                else
                {
                    var set = new HashSet<Guid>(gone);
                    list.RemoveAll(i => set.Contains(i.GameId));
                }
            }
            try { ShowCard(Loc("LOC_KX_PageHealth", "Library health").ToUpper(CultureInfo.CurrentCulture), text, undo.Count > 0 ? Loc("LOC_KX_HealthUndoHint", "Undo is at the top of the page") : "", "", null); } catch (Exception) { }
            var was = FocusedPageItem() as PageRow;
            int at = was != null ? PageRows.IndexOf(was) : -1;
            pageQuietOpen = true;
            OpenPage("health", pageArg, false);
            var rows = PageRows.Where(r => r.IsRow).ToList();
            if (rows.Count > 0 && at >= 0) FocusPageSoon(PageRows[Math.Min(at, PageRows.Count - 1)].IsRow ? PageRows[Math.Min(at, PageRows.Count - 1)] : rows.Last());
            else FocusPageSoon(PageChips.FirstOrDefault());
        }

        // ---------------- Playnite's own metadata download (by reflection) ----------------

        private void HealthDownloadMetadata(List<Guid> ids)
        {
            if (healthDownloading || ids == null || ids.Count == 0) return;
            healthDownloading = true;
            healthDownloadText = string.Format(Loc("LOC_KX_HealthDownloading", "Downloading info: {0} of {1}…"), 0, ids.Count);
            PlaySound("select");
            pageQuietOpen = true;
            OpenPage("health", pageArg, false);
            var cat = pageArg;
            KippixShared.SafeWork.Queue(_ =>
            {
                string error = null;
                int done = 0;
                try
                {
                    Type appType = null;
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        appType = asm.GetType("Playnite.PlayniteApplication", false);
                        if (appType != null) break;
                    }
                    object app = appType != null ? appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) : null;
                    if (app == null) throw new InvalidOperationException("Playnite's application isn't reachable");
                    object db = appType.GetProperty("Database").GetValue(app, null);
                    object ext = appType.GetProperty("Extensions").GetValue(app, null);
                    object appSettings = appType.GetProperty("AppSettings").GetValue(app, null);
                    object metaPlugins = ext.GetType().GetProperty("MetadataPlugins").GetValue(ext, null);
                    object libPlugins = ext.GetType().GetProperty("LibraryPlugins").GetValue(ext, null);
                    object metaSettings = appSettings.GetType().GetProperty("MetadataSettings").GetValue(appSettings, null);
                    // a copy, set to fill in only what's empty (never overwrite what's there)
                    object useSettings = null;
                    try
                    {
                        var clone = typeof(Playnite.SDK.Data.Serialization).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(m => m.Name == "GetClone" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
                        if (clone != null) useSettings = clone.MakeGenericMethod(metaSettings.GetType()).Invoke(null, new[] { metaSettings });
                    }
                    catch (Exception) { useSettings = null; }
                    var skip = metaSettings.GetType().GetProperty("SkipExistingValues");
                    if (useSettings != null && skip != null) skip.SetValue(useSettings, true, null);
                    else if (skip != null && (bool)skip.GetValue(metaSettings, null)) useSettings = metaSettings;   // (already only fills in what's empty)
                    if (useSettings == null) throw new InvalidOperationException("couldn't make settings that only fill in what's empty");

                    var dlType = appType.Assembly.GetType("Playnite.Metadata.MetadataDownloader");
                    var ctor = dlType.GetConstructors().FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == 3 && ps[2].ParameterType.IsGenericType && ps[2].ParameterType.GetGenericTypeDefinition() == typeof(List<>);
                    });
                    if (ctor == null) throw new InvalidOperationException("Playnite's metadata downloader has changed");
                    var games = ids.Select(id => api.Database.Games.Get(id)).Where(g => g != null).ToList();
                    object downloader = ctor.Invoke(new[] { db, metaPlugins, libPlugins });
                    try
                    {
                        var run = dlType.GetMethod("DownloadMetadataAsync");
                        Action<Game, int, int> progress = (g, i, t) =>
                        {
                            done = i + 1;
                            healthDownloadText = string.Format(Loc("LOC_KX_HealthDownloading", "Downloading info: {0} of {1}…"), done, t);
                            OnUi(() => { if (openPanel == "page" && pageKey == "health") SetPageStatus(healthDownloadText); });
                        };
                        var task = run.Invoke(downloader, new object[] { games, useSettings, appSettings, progress, CancellationToken.None }) as Task;
                        if (task != null) task.Wait();
                    }
                    finally { var d = downloader as IDisposable; if (d != null) d.Dispose(); }
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    if (inner is AggregateException && inner.InnerException != null) inner = inner.InnerException;
                    error = inner.Message;
                    logger.Warn("Kippix: library health: metadata download: " + inner);
                }
                OnUi(() =>
                {
                    healthDownloading = false;
                    healthDownloadText = "";
                    string line = error == null ? string.Format(Loc("LOC_KX_HealthDownloaded", "Info downloaded for {0} games (only what was empty)."), done)
                                                : Loc("LOC_KX_HealthDownloadFailed", "Playnite's metadata download couldn't be started from here. Fix in desktop mode: select the games, right-click › Edit… › Download Metadata.");
                    try { ShowCard(Loc("LOC_KX_PageHealth", "Library health").ToUpper(CultureInfo.CurrentCulture), line, "", "", null); } catch (Exception) { }
                    StartHealthScan();
                    if (openPanel == "page" && pageKey == "health") { pageQuietOpen = true; OpenPage("health", pageArg, false); if (error != null) SetPageStatus(line); }
                });
            });
        }
    }
}

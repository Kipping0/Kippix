using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>One installed game's waiting update, as the card tells it.</summary>
    internal class GameUpdateInfo
    {
        public string Store;   // "Steam", "Epic Games", "GOG"
        public string Line;    // "An update is waiting on Steam · 1.2 GB"
        public string Key;     // store | id | the new build (Once: told about once per update)
    }

    /// <summary>
    /// (0.99.39) Game updates: a card when an installed game has an update waiting in its store's launcher, at start-up (the
    /// games that have one) and on its game page. Settings › Notifications › Game updates: on / off, and how often
    /// (GameUpdateWhen: 0 at start-up and every time you open its page, 1 only at start-up, 2 once on its page, per update).
    ///
    /// What counts as an update (read-only, nothing in a launcher is changed):
    ///  - Steam: the game's appmanifest_&lt;id&gt;.acf (StateFlags "update required" / started / paused / downloading, or a target
    ///    build that isn't the installed one): what Steam itself knows, so it's as fresh as Steam's last check.
    ///  - Epic: the launcher's install manifest (AppVersionString) against the newest build from Epic's launcher assets list,
    ///    asked with Playnite's own Epic sign-in (its Epic library add-on's tokens, through that add-on); nothing without it.
    ///  - GOG: goggame-&lt;id&gt;.info's buildId against GOG's public build list (content-system.gog.com), only when the
    ///    installed build is in that list and older than the newest one.
    /// Other stores (Ubisoft, EA, Xbox, Battle.net...) have nothing Kippix can read, so they're never told about.
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly Guid UpdSteamId = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");
        private static readonly Guid UpdEpicId = new Guid("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
        private static readonly Guid UpdGogId = new Guid("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");

        private readonly object updLock = new object();
        private Dictionary<string, string> epicLatest;          // AppName -> newest build (null: not asked yet)
        private DateTime epicLatestAt = DateTime.MinValue;
        private readonly Dictionary<string, Tuple<DateTime, List<string[]>>> gogBuilds = new Dictionary<string, Tuple<DateTime, List<string[]>>>();   // id -> (when, [build id, version])
        private Guid updPageGame = Guid.Empty;                   // the game page already looked at (cleared when the page closes)
        private int updPageGen;

        private static bool UpdStore(Game g)
        {
            return g != null && (g.PluginId == UpdSteamId || g.PluginId == UpdEpicId || g.PluginId == UpdGogId);
        }

        // ---------------- at start-up ----------------

        private void ScheduleGameUpdateCheck()
        {
            if (!IsFullscreen) return;
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(25) };
            t.Tick += (s, e) => { t.Stop(); BootUpdateCheck(); };
            t.Start();
        }

        private void BootUpdateCheck()
        {
            if (!settings.GameUpdateNotify || settings.GameUpdateWhen == 2) return;
            List<Game> games;
            try { games = api.Database.Games.Where(g => g != null && g.IsInstalled && UpdStore(g)).ToList(); }
            catch (Exception) { return; }
            if (games.Count == 0) return;
            KippixShared.SafeWork.Queue(_ =>
            {
                var found = new List<Tuple<Game, GameUpdateInfo>>();
                foreach (var g in games)
                {
                    GameUpdateInfo info = null;
                    try { info = GameUpdateFor(g); } catch (Exception) { }
                    if (info != null) found.Add(Tuple.Create(g, info));
                }
                logger.Info("Kippix: game updates at start-up: " + games.Count + " installed games looked at, " + (found.Count == 0 ? "none waiting" : found.Count + " waiting: " + string.Join(", ", found.Select(f => f.Item1.Name + " (" + f.Item2.Store + ")").ToArray())));
                if (found.Count == 0) return;
                ImageSource cover = found.Count == 1 ? UpdCover(found[0].Item1) : null;
                OnUi(() =>
                {
                    if (!settings.GameUpdateNotify || settings.GameUpdateWhen == 2) return;
                    if (found.Count == 1)
                        ShowCardKept(Loc("LOC_KX_UpdCardHead", "UPDATE AVAILABLE"), found[0].Item1.Name, found[0].Item2.Line, Loc("LOC_KX_UpdCardHint", "Open its launcher to update"), cover);
                    else
                    {
                        var names = found.Select(f => f.Item1.Name).ToList();
                        string list = string.Join(", ", names.Take(3).ToArray());
                        if (names.Count > 3) list = string.Format(Loc("LOC_KX_UpdAndMore", "{0} and {1} more"), list, names.Count - 3);
                        var stores = found.Select(f => f.Item2.Store).Distinct().ToArray();
                        ShowCardKept(Loc("LOC_KX_UpdCardHeadMany", "GAME UPDATES"), string.Format(Loc("LOC_KX_UpdManyTitle", "{0} games have updates"), found.Count), list,
                                     string.Format(Loc("LOC_KX_UpdManyStores", "Waiting on {0}"), string.Join(", ", stores)), null);
                    }
                });
            });
        }

        // ---------------- on the game page ----------------

        /// <summary>The game page opened, or its game changed: a card if this game has an update waiting.</summary>
        private void GamePageUpdateCheck(Game g)
        {
            if (g == null || g.Id == updPageGame) return;
            updPageGame = g.Id;
            if (!settings.GameUpdateNotify || settings.GameUpdateWhen == 1 || !g.IsInstalled || !UpdStore(g)) return;
            int gen = ++updPageGen;
            KippixShared.SafeWork.Queue(_ =>
            {
                GameUpdateInfo info = null;
                try { info = GameUpdateFor(g); } catch (Exception ex) { logger.Warn("Kippix: game update check (" + g.Name + "): " + ex.Message); }
                if (info == null) return;
                ImageSource cover = UpdCover(g);
                OnUi(() =>
                {
                    if (gen != updPageGen || !detailsOpen || updPageGame != g.Id || !settings.GameUpdateNotify) return;
                    if (settings.GameUpdateWhen == 2)
                    {
                        if (settings.GameUpdateSeen == null) settings.GameUpdateSeen = new List<string>();
                        if (settings.GameUpdateSeen.Contains(info.Key)) return;
                        settings.GameUpdateSeen.Add(info.Key);
                        while (settings.GameUpdateSeen.Count > 300) settings.GameUpdateSeen.RemoveAt(0);
                        SaveSettings();
                    }
                    logger.Info("Kippix: game update card: " + g.Name + " (" + info.Store + ")");
                    ShowCard(Loc("LOC_KX_UpdCardHead", "UPDATE AVAILABLE"), g.Name, info.Line, Loc("LOC_KX_UpdCardHint", "Open its launcher to update"), cover);
                });
            });
        }

        private void GamePageUpdateClosed()
        {
            updPageGame = Guid.Empty;
            updPageGen++;
        }

        private ImageSource UpdCover(Game g)
        {
            try
            {
                string path = !string.IsNullOrEmpty(g.CoverImage) ? api.Database.GetFullFilePath(g.CoverImage) : null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return Images.Load(path, 160);
            }
            catch (Exception) { }
            return null;
        }

        // ---------------- what's waiting ----------------

        /// <summary>The game's waiting update, or null (none, or nothing to tell by). Slow (files, maybe online): not on the UI thread.</summary>
        private GameUpdateInfo GameUpdateFor(Game g)
        {
            if (g == null || string.IsNullOrEmpty(g.GameId)) return null;
            if (g.PluginId == UpdSteamId) return SteamUpdate(g.GameId, g.InstallDirectory);
            if (g.PluginId == UpdEpicId) return EpicUpdate(g.GameId);
            if (g.PluginId == UpdGogId) return GogUpdate(g.GameId, g.InstallDirectory);
            return null;
        }

        // Steam appmanifest StateFlags
        private const long SfUpdateRequired = 2, SfUpdateRunning = 256, SfUpdatePaused = 512, SfUpdateStarted = 1024,
            SfBusy = 256 | 262144 | 524288 | 1048576 | 2097152 | 4194304;

        private GameUpdateInfo SteamUpdate(string appId, string installDir)
        {
            string manifest = SteamManifestPath(appId, installDir);
            if (manifest == null) return null;
            string text = File.ReadAllText(manifest);
            var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(text, "^\\s*\"(\\w+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline))
                if (!v.ContainsKey(m.Groups[1].Value)) v[m.Groups[1].Value] = m.Groups[2].Value;
            long flags = UpdNum(v, "StateFlags"), toDl = UpdNum(v, "BytesToDownload"), dl = UpdNum(v, "BytesDownloaded");
            string build, target;
            v.TryGetValue("buildid", out build);
            v.TryGetValue("TargetBuildID", out target);
            bool newer = !string.IsNullOrEmpty(target) && target != "0" && !string.IsNullOrEmpty(build) && build != "0" && target != build;
            bool busy = (flags & SfBusy) != 0, paused = (flags & (SfUpdatePaused | SfUpdateStarted)) != 0, required = (flags & SfUpdateRequired) != 0;
            if (!required && !busy && !paused && !newer) return null;
            long left = toDl > dl ? toDl - dl : 0;
            string size = left > 0 ? " · " + UpdSize(left) : "";
            string line = busy ? Loc("LOC_KX_UpdSteamBusy", "Updating on Steam now")
                        : paused ? Loc("LOC_KX_UpdSteamPaused", "Update paused on Steam") + size
                        : Loc("LOC_KX_UpdSteam", "An update is waiting on Steam") + size;
            return new GameUpdateInfo { Store = "Steam", Line = line, Key = "steam|" + appId + "|" + (newer ? target : (build ?? "") + "+") };
        }

        private static string SteamManifestPath(string appId, string installDir)
        {
            string name = "appmanifest_" + appId + ".acf";
            // ...\steamapps\common\<game>: its own library first
            try
            {
                if (!string.IsNullOrEmpty(installDir))
                {
                    var common = Directory.GetParent(installDir.TrimEnd('\\', '/'));
                    var apps = common != null ? common.Parent : null;
                    if (apps != null)
                    {
                        string f = Path.Combine(apps.FullName, name);
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch (Exception) { }
            string steam = SteamPaths.SteamPath();
            if (string.IsNullOrEmpty(steam)) return null;
            var libs = new List<string> { Path.Combine(steam, "steamapps") };
            try
            {
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps"));
            }
            catch (Exception) { }
            foreach (var lib in libs)
            {
                try { string f = Path.Combine(lib, name); if (File.Exists(f)) return f; } catch (Exception) { }
            }
            return null;
        }

        private static long UpdNum(Dictionary<string, string> v, string key)
        {
            string s;
            long n;
            return v.TryGetValue(key, out s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        private static string UpdSize(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1) return gb.ToString(gb < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " GB";
            double mb = Math.Max(1, bytes / 1048576.0);
            return mb.ToString("0", CultureInfo.CurrentCulture) + " MB";
        }

        // ---- Epic ----

        private GameUpdateInfo EpicUpdate(string appName)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return null;
            var json = new JavaScriptSerializer();
            string installed = null;
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    var d = json.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                    object app, ver;
                    if (d == null || !d.TryGetValue("AppName", out app) || !string.Equals(app as string, appName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (d.TryGetValue("AppVersionString", out ver)) installed = ver as string;
                    break;
                }
                catch (Exception) { }
            }
            if (string.IsNullOrEmpty(installed)) return null;
            var latest = EpicLatestBuilds();
            string newest;
            if (latest == null || !latest.TryGetValue(appName, out newest) || string.IsNullOrEmpty(newest) || newest == installed) return null;
            return new GameUpdateInfo { Store = "Epic Games", Line = Loc("LOC_KX_UpdEpic", "An update is waiting in the Epic Games Launcher"), Key = "epic|" + appName + "|" + newest };
        }

        /// <summary>AppName -> newest build, from Epic's launcher assets list (asked at most every 20 minutes), or null.</summary>
        private Dictionary<string, string> EpicLatestBuilds()
        {
            lock (updLock)
            {
                if (epicLatest != null && (DateTime.Now - epicLatestAt).TotalMinutes < 20) return epicLatest;
                epicLatestAt = DateTime.Now;
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                epicLatest = result;   // (a failed try isn't repeated for 20 minutes)
                try
                {
                    string token = EpicAccessToken();
                    if (string.IsNullOrEmpty(token)) { logger.Info("Kippix: game updates: Epic: not signed in to Epic in Playnite, so Epic updates aren't known"); return result; }
                    string body;
                    using (var web = StoreWeb())
                    {
                        web.Headers["Authorization"] = "bearer " + token;
                        body = web.DownloadString("https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live");
                    }
                    var arr = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(body) as object[];
                    if (arr != null)
                        foreach (var o in arr)
                        {
                            var d = o as Dictionary<string, object>;
                            object app, build;
                            if (d != null && d.TryGetValue("appName", out app) && d.TryGetValue("buildVersion", out build) && app is string && build is string) result[(string)app] = (string)build;
                        }
                    logger.Info("Kippix: game updates: Epic: " + result.Count + " builds known");
                }
                catch (Exception ex) { logger.Warn("Kippix: game updates: Epic: " + ex.Message); }
                return result;
            }
        }

        /// <summary>Playnite's own Epic sign-in (its Epic library add-on), renewed by that add-on when it's old; null when not signed in.</summary>
        private string EpicAccessToken()
        {
            var plugin = api.Addons.Plugins.FirstOrDefault(p => p != null && p.Id == UpdEpicId);
            if (plugin == null) return null;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var tf = plugin.GetType().GetField("TokensPath", all);
            var tp = tf == null ? plugin.GetType().GetProperty("TokensPath", all) : null;
            string tokensPath = tf != null ? tf.GetValue(plugin) as string : tp != null ? tp.GetValue(plugin, null) as string : null;
            if (string.IsNullOrEmpty(tokensPath) || !File.Exists(tokensPath)) return null;
            var ct = plugin.GetType().Assembly.GetType("EpicLibrary.Services.EpicAccountClient");
            if (ct == null) return null;
            object client = Activator.CreateInstance(ct, new object[] { api, tokensPath });
            var logged = ct.GetMethod("GetIsUserLoggedIn", all);
            if (logged == null || !(bool)logged.Invoke(client, null)) return null;   // (renews the sign-in when it's old)
            var load = ct.GetMethod("loadTokens", all);
            object tok = load != null ? load.Invoke(client, null) : null;
            if (tok == null) return null;
            var af = tok.GetType().GetField("access_token", all);
            var ap = af == null ? tok.GetType().GetProperty("access_token", all) : null;
            return af != null ? af.GetValue(tok) as string : ap != null ? ap.GetValue(tok, null) as string : null;
        }

        // ---- GOG ----

        private GameUpdateInfo GogUpdate(string id, string installDir)
        {
            if (string.IsNullOrEmpty(installDir)) return null;
            string info = Path.Combine(installDir, "goggame-" + id + ".info");
            if (!File.Exists(info)) return null;
            var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(info)) as Dictionary<string, object>;
            object b;
            string installed = d != null && d.TryGetValue("buildId", out b) && b != null ? Convert.ToString(b, CultureInfo.InvariantCulture) : null;
            if (string.IsNullOrEmpty(installed)) return null;
            var builds = GogBuilds(id);
            if (builds == null || builds.Count == 0) return null;
            int at = builds.FindIndex(x => x[0] == installed);
            if (at <= 0) return null;   // the newest one, or not a public build (a beta?): nothing to tell
            string ver = builds[0][1];
            string line = Loc("LOC_KX_UpdGog", "An update is waiting in GOG Galaxy") + (string.IsNullOrEmpty(ver) ? "" : " · " + string.Format(Loc("LOC_KX_UpdVersion", "version {0}"), ver));
            return new GameUpdateInfo { Store = "GOG", Line = line, Key = "gog|" + id + "|" + builds[0][0] };
        }

        /// <summary>GOG's public Windows builds of a game, newest first ([build id, version]), asked at most every 20 minutes.</summary>
        private List<string[]> GogBuilds(string id)
        {
            lock (updLock)
            {
                Tuple<DateTime, List<string[]>> c;
                if (gogBuilds.TryGetValue(id, out c) && (DateTime.Now - c.Item1).TotalMinutes < 20) return c.Item2;
                var list = new List<string[]>();
                gogBuilds[id] = Tuple.Create(DateTime.Now, list);
                try
                {
                    string body;
                    using (var web = StoreWeb()) body = web.DownloadString("https://content-system.gog.com/products/" + Uri.EscapeDataString(id) + "/os/windows/builds?generation=2");
                    var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(body) as Dictionary<string, object>;
                    object items;
                    var rows = new List<Tuple<string, string, string>>();   // date, build, version
                    if (root != null && root.TryGetValue("items", out items) && items is object[])
                        foreach (var o in (object[])items)
                        {
                            var it = o as Dictionary<string, object>;
                            if (it == null) continue;
                            object branch, bid, ver, date;
                            if (it.TryGetValue("branch", out branch) && branch != null && Convert.ToString(branch).Length > 0) continue;   // public builds only
                            if (!it.TryGetValue("build_id", out bid) || bid == null) continue;
                            it.TryGetValue("version_name", out ver);
                            it.TryGetValue("date_published", out date);
                            rows.Add(Tuple.Create(Convert.ToString(date) ?? "", Convert.ToString(bid, CultureInfo.InvariantCulture), Convert.ToString(ver) ?? ""));
                        }
                    foreach (var r in rows.OrderByDescending(r => r.Item1, StringComparer.Ordinal)) list.Add(new[] { r.Item2, r.Item3 });
                }
                catch (Exception ex) { logger.Warn("Kippix: game updates: GOG " + id + ": " + ex.Message); }
                return list;
            }
        }

        // ---------------- Settings › Notifications › Game updates ----------------

        private static readonly string[] UpdWhenKeys = { "LOC_KX_UpdWhenAll", "LOC_KX_UpdWhenBoot", "LOC_KX_UpdWhenOnce" };
        private static readonly string[] UpdWhenText_ = { "At start-up and every visit", "Only at start-up", "Once, on its page" };

        public bool GameUpdateNotify
        {
            get { return settings.GameUpdateNotify; }
            set { if (settings.GameUpdateNotify == value) return; settings.GameUpdateNotify = value; SaveSettings(); Notify("GameUpdateNotify"); Notify("NotifyCardsOn"); }
        }

        public string GameUpdateWhenText { get { int i = Clamp(settings.GameUpdateWhen, 0, 2); return Loc(UpdWhenKeys[i], UpdWhenText_[i]); } }

        private ICommand cycUpdWhen;
        public ICommand CycleGameUpdateWhenCommand
        {
            get
            {
                return cycUpdWhen ?? (cycUpdWhen = new RelayCommand(_ =>
                {
                    settings.GameUpdateWhen = (Clamp(settings.GameUpdateWhen, 0, 2) + 1) % 3;
                    PlaySound("move");
                    SaveSettings();
                    Notify("GameUpdateWhenText");
                }));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// The Split screen page (More pages / a top bar shortcut). Two ways to look:
    ///  - Together on this PC (the sofa): games with local multiplayer (split / shared screen, local co-op / versus).
    ///    Online-only multiplayer games are left out.
    ///  - Online: games with online multiplayer, with their own player choice.
    /// A chosen number of players shows the games for exactly that many (the most that can play: 2 is a 2-player game, 3 a
    /// 3-player one...; the bigger steps are ranges, "5–6 players"), or with "Or more" that many or more (r10c).
    ///
    /// r13: everything is matched on this PC, so the page fills at once (it used to ask PCGamingWiki about each game, which
    /// was slow and kept hitting PCGamingWiki's rate limit). What it goes on:
    ///  1. Playnite's features, categories and tags ("Split screen", "Shared/Split Screen", "Local Co-op", "Local Multiplayer",
    ///     "Online Co-op", "Multiplayer"…; the names vary by metadata source, so they're matched by their words).
    ///  2. Steam's own categories for Steam games, where the badges code has them (badges-steam.json: Shared/Split Screen,
    ///     Co-op, Online Co-op / PvP, Multi-player). Steam's are about the PC version, so a Steam game whose Steam categories
    ///     have no split screen isn't shown only because of a feature IGDB took from the console version (Call of Duty).
    ///  3. Player counts: PCGamingWiki's Multiplayer table for EVERY game with local multiplayer, downloaded once in a few big
    ///     pages (500 games a request, one request at a time, at least a second apart, only while this page is open; paused
    ///     and carried on next time if you leave), kept in ExtensionsData\splitscreen-pcgw.json for 30 days. The online counts
    ///     are a second download of the same kind, made only when you look at Online. Matched by Steam app id, then by name.
    ///     The answers the old per-game look-ups left in localplayers.json are still used (never asked for again).
    /// A game whose player count isn't known is only shown under "Any number".
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly Guid SteamLibId = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        // ---------------- the old per-game answers (read only now) ----------------

        private class LocalInfo
        {
            public int V;
            public bool Found;
            public bool Failed;
            public int Players = -1;
            public string Modes = "";
            public string Local = "";
            public string Online = "";
            public int OnlinePlayers = -1;
            public string OnlineModes = "";
            public string Lan = "";
            public DateTime At;
        }

        private Dictionary<string, LocalInfo> localCache;
        private readonly object localLoadLock = new object();
        private Dictionary<Guid, PageTile> splitTiles = new Dictionary<Guid, PageTile>();

        private string LocalCacheFile { get { return Path.Combine(plugin.GetPluginUserDataPath(), "localplayers.json"); } }

        private static readonly int[] LocalPlayerSteps = { 0, 2, 3, 4, 5, 6, 8 };
        private static readonly int[] OnlinePlayerSteps = { 0, 2, 3, 4, 6, 8, 16, 32, 64 };

        /// <summary>Reads localplayers.json: each game's answer from its PCGamingWiki page (0.83 writes it again: ThemeApi.SplitPages.cs; off the UI thread).</summary>
        private void LoadLocalCache()
        {
            lock (localLoadLock)
            {
                if (localCache != null) return;
                var d = new Dictionary<string, LocalInfo>();
                try
                {
                    if (File.Exists(LocalCacheFile))
                    {
                        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        var read = js.Deserialize<Dictionary<string, LocalInfo>>(File.ReadAllText(LocalCacheFile, Encoding.UTF8));
                        if (read != null) d = read;
                        foreach (var li in d.Values)
                            if (li != null && li.V == 0) li.Found = !string.IsNullOrEmpty(li.Local) || li.Players > 0;
                    }
                }
                catch (Exception ex) { logger.Warn("Kippix: local players cache: " + ex.Message); }
                localCache = d;
            }
        }

        // ---------------- PCGamingWiki's lists, downloaded in bulk ----------------

        /// <summary>One PCGamingWiki page from the bulk download (short names: the file holds thousands).</summary>
        private class PcgwRow
        {
            public string P = "";     // page name (the game's title)
            public string S = "";     // Steam app ids, comma separated
            public string Y = "";     // Local / Online: true, limited
            public int N = -1;        // the most players (Local_players / Online_players), -1 not known
            public string M = "";     // modes (Co-op, Versus, Hot seat…)
        }

        private class PcgwSet
        {
            public DateTime At;              // (UTC) when the download started
            public bool Complete;
            public int Offset;               // rows fetched so far (the next page starts here)
            public int Total;                // what PCGamingWiki said there are (0 not known)
            public List<PcgwRow> Rows = new List<PcgwRow>();
        }

        private class PcgwFile
        {
            public int V;
            public PcgwSet Local;
            public PcgwSet Online;
        }

        /// <summary>A set made searchable: by Steam app id, by name, and by name with only its letters and digits.</summary>
        private class PcgwIndex
        {
            public readonly Dictionary<string, PcgwRow> Steam = new Dictionary<string, PcgwRow>();
            public readonly Dictionary<string, PcgwRow> Name = new Dictionary<string, PcgwRow>();
            public readonly Dictionary<string, PcgwRow> Simple = new Dictionary<string, PcgwRow>();
            public bool Complete;
            public int Count;
        }

        private const int PcgwFileVersion = 1;
        private const int PcgwBulkPage = 200;   // (0.82: was 500; smaller pages answer within the time limit when the wiki is slow)
        private const int PcgwBulkDays = 30;
        private const int PcgwBulkSpacingMs = 1100;
        private const int PcgwBulkTimeoutMs = 60000;
        private static volatile bool pcgwNoJoin;   // (0.82) the query with the Steam app ids failed: the plain one (names only) for the rest of the session
        private const string PcgwApi = "https://www.pcgamingwiki.com/w/api.php";

        private PcgwFile pcgwFile;
        private readonly object pcgwFileLock = new object();
        private volatile PcgwIndex pcgwLocalIdx, pcgwOnlineIdx;
        private bool pcgwLoaded;
        private volatile bool pcgwRunning;
        private volatile string pcgwRunningKind;   // "local" / "online" while a first download runs (the progress line)
        private volatile int pcgwPageNow, pcgwPagesAll;
        private volatile bool pcgwBusy, pcgwDown;  // the last run stopped: PCGamingWiki busy (429) / not answering
        private bool splitComputing, splitComputeAgain;
        private string splitLastSummary;

        private string PcgwFilePath { get { return Path.Combine(plugin.GetPluginUserDataPath(), "splitscreen-pcgw.json"); } }

        /// <summary>Reads splitscreen-pcgw.json and builds the indexes (off the UI thread).</summary>
        private void LoadPcgwFile()
        {
            lock (pcgwFileLock)
            {
                if (pcgwLoaded) return;
                pcgwLoaded = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                PcgwFile f = null;
                try
                {
                    if (File.Exists(PcgwFilePath))
                        f = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<PcgwFile>(File.ReadAllText(PcgwFilePath, Encoding.UTF8));
                }
                catch (Exception ex) { logger.Warn("Kippix: split screen: splitscreen-pcgw.json couldn't be read (downloaded again): " + ex.Message); f = null; }
                if (f == null || f.V != PcgwFileVersion) f = new PcgwFile { V = PcgwFileVersion };
                pcgwFile = f;
                pcgwLocalIdx = f.Local != null && f.Local.Rows != null ? BuildPcgwIndex(f.Local) : null;
                pcgwOnlineIdx = f.Online != null && f.Online.Rows != null ? BuildPcgwIndex(f.Online) : null;
                logger.Info("Kippix: split screen: PCGamingWiki lists read in " + watch.ElapsedMilliseconds + " ms (local "
                            + (pcgwLocalIdx != null ? pcgwLocalIdx.Count + (pcgwLocalIdx.Complete ? "" : ", partial") : "none") + ", online "
                            + (pcgwOnlineIdx != null ? pcgwOnlineIdx.Count + (pcgwOnlineIdx.Complete ? "" : ", partial") : "none") + ")");
            }
        }

        private void SavePcgwFile()
        {
            lock (pcgwFileLock)
            {
                try
                {
                    string file = PcgwFilePath, tmp = file + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(tmp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(pcgwFile), Encoding.UTF8);
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                }
                catch (Exception ex) { logger.Warn("Kippix: split screen: splitscreen-pcgw.json couldn't be saved: " + ex.Message); }
            }
        }

        private static PcgwIndex BuildPcgwIndex(PcgwSet set)
        {
            var idx = new PcgwIndex { Complete = set.Complete };
            List<PcgwRow> rows;
            lock (set) rows = set.Rows.ToList();
            foreach (var r in rows)
            {
                if (r == null) continue;
                idx.Count++;
                foreach (var id in SteamIdsIn(r.S)) if (!idx.Steam.ContainsKey(id)) idx.Steam[id] = r;
                string n = NormGameName(r.P);
                if (n.Length > 0 && !idx.Name.ContainsKey(n)) idx.Name[n] = r;
                string s = SimpleName(r.P);
                if (s.Length > 2 && !idx.Simple.ContainsKey(s)) idx.Simple[s] = r;
            }
            return idx;
        }

        private static List<string> SteamIdsIn(string s)
        {
            return (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        private static readonly Regex EditionRx = new Regex(
            @"\s*[-:–]?\s*\(?\b((game of the year|goty|definitive|complete|deluxe|ultimate|gold|anniversary|digital deluxe|standard|premium|special|enhanced)\s+edition|game of the year|goty|director'?s cut)\)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A name to match on: no ™ ® ©, straight apostrophes, "A - B" as "A: B", single spaces, lower case.</summary>
        private static string NormGameName(string name)
        {
            string s = CleanName(name).Replace('’', '\'').Replace('‘', '\'').Replace(" - ", ": ").Replace(" – ", ": ");
            s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
            return s;
        }

        /// <summary>
        /// The PCGamingWiki page for a game: by its Steam app id, else by its name (a page of that name that is for another
        /// Steam app doesn't count), else by its name without an edition ("… Game of the Year Edition").
        /// </summary>
        private static PcgwRow MatchPcgw(PcgwIndex idx, Game g, string steam)
        {
            if (idx == null || g == null) return null;
            PcgwRow r;
            if (steam != null && idx.Steam.TryGetValue(steam, out r)) return r;
            Func<PcgwRow, bool> ok = row => steam == null || string.IsNullOrEmpty(row.S) || SteamIdsIn(row.S).Contains(steam);
            string name = g.Name ?? "";
            string n = NormGameName(name);
            if (n.Length > 0 && idx.Name.TryGetValue(n, out r) && ok(r)) return r;
            string s = SimpleName(name);
            if (s.Length > 2 && idx.Simple.TryGetValue(s, out r) && ok(r)) return r;
            string plain = EditionRx.Replace(CleanName(name), "");
            if (plain.Length > 0 && plain.Length < name.Length)
            {
                n = NormGameName(plain);
                if (n.Length > 0 && idx.Name.TryGetValue(n, out r) && ok(r)) return r;
                s = SimpleName(plain);
                if (s.Length > 2 && idx.Simple.TryGetValue(s, out r) && ok(r)) return r;
            }
            return null;
        }

        /// <summary>The set needs downloading: never done, partly done, or over a month old.</summary>
        private static bool PcgwSetNeeded(PcgwSet set)
        {
            return set == null || !set.Complete || (DateTime.UtcNow - set.At.ToUniversalTime()).TotalDays >= PcgwBulkDays;
        }

        /// <summary>
        /// The one-time download (and the monthly refresh) while the page is open: the local list, then the online one if
        /// you're looking at Online. One request at a time; leaving the page stops it (what's done is kept and carried on).
        /// </summary>
        private void EnsurePcgwBulk() { EnsurePcgwPages(); }   // (0.83: PCGamingWiki refuses Cargo queries now: ThemeApi.SplitPages.cs reads each game's page)

        /// <summary>The Cargo bulk download (r13 – 0.82). PCGamingWiki now answers "You don't have permission to run arbitrary Cargo
        /// queries", so it isn't started; kept for if that changes.</summary>
        private void EnsurePcgwBulkCargo()
        {
            if (pcgwRunning || !pcgwLoaded) return;
            bool wantLocal, wantOnline;
            lock (pcgwFileLock)
            {
                wantLocal = PcgwSetNeeded(pcgwFile.Local);
                wantOnline = settings.SplitOnline && PcgwSetNeeded(pcgwFile.Online);
            }
            if (!wantLocal && !wantOnline) return;
            pcgwRunning = true;
            pcgwBusy = pcgwDown = false;
            int gen = pageGen;
            KippixShared.SafeWork.Queue(_ =>
            {
                try
                {
                    Func<bool> left = () => Volatile.Read(ref pageGen) != gen;
                    bool ok = true;
                    // the online list first when that's what you're looking at (the local one carries on after)
                    if (settings.SplitOnline && wantOnline) ok = PcgwDownload(true, gen, left);
                    if (ok && !left()) { lock (pcgwFileLock) wantLocal = PcgwSetNeeded(pcgwFile.Local); }
                    if (ok && !left() && wantLocal) ok = PcgwDownload(false, gen, left);
                    if (ok && !left() && settings.SplitOnline) { bool more; lock (pcgwFileLock) more = PcgwSetNeeded(pcgwFile.Online); if (more) PcgwDownload(true, gen, left); }
                }
                catch (Exception ex) { logger.Warn("Kippix: split screen: PCGamingWiki download: " + ex.Message); pcgwDown = true; }   // (not started again at once: no retry loop)
                pcgwRunning = false;
                pcgwRunningKind = null;
                OnUi(() =>
                {
                    if (openPanel != "page" || pageKey != "splitscreen") return;
                    ApplySplitResults();
                    if (gen != pageGen) EnsurePcgwBulk();   // (left and came back while a request was out: carry on)
                    else if (settings.SplitOnline && !pcgwBusy && !pcgwDown) { bool more; lock (pcgwFileLock) more = PcgwSetNeeded(pcgwFile.Online); if (more) EnsurePcgwBulk(); }
                });
            });
        }

        /// <summary>One list, page by page. False if it stopped on PCGamingWiki (busy / not answering).</summary>
        private bool PcgwDownload(bool online, int gen, Func<bool> left)
        {
            string kind = online ? "online" : "local";
            PcgwSet set, old;
            bool refresh;
            lock (pcgwFileLock)
            {
                old = online ? pcgwFile.Online : pcgwFile.Local;
                refresh = old != null && old.Complete;   // a month on: downloaded again, the old one used meanwhile
                set = old != null && !old.Complete ? old : new PcgwSet { At = DateTime.UtcNow };
                if (set.Rows == null) set.Rows = new List<PcgwRow>();
                if (!refresh)
                {
                    if (online) pcgwFile.Online = set; else pcgwFile.Local = set;
                    pcgwRunningKind = kind;   // (the progress line: only for the first download)
                }
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string col = online ? "Online" : "Local";
            string tables, joinOn, fields;
            Action plainQuery = () =>
            {
                tables = "Multiplayer"; joinOn = null;
                fields = "Multiplayer._pageName=P,Multiplayer." + col + "=Y,Multiplayer." + col + "_players=N,Multiplayer." + col + "_modes=M";
            };
            tables = "Multiplayer,Infobox_game";
            joinOn = "Multiplayer._pageID=Infobox_game._pageID";
            fields = "Multiplayer._pageName=P,Infobox_game.Steam_AppID=S,Multiplayer." + col + "=Y,Multiplayer." + col + "_players=N,Multiplayer." + col + "_modes=M";
            if (pcgwNoJoin) plainQuery();
            string where = "Multiplayer." + col + "=\"true\" OR Multiplayer." + col + "=\"limited\"";
            logger.Info("Kippix: split screen: PCGamingWiki " + kind + " list: " + (refresh ? "monthly refresh" : set.Offset > 0 ? "carrying on from " + set.Offset : "first download") + " started");

            if (set.Total <= 0)
            {
                // how many there are (for "3 of 14"); not essential, so a refusal is fine
                int status, ra; string problem;
                var cnt = PcgwRequest(tables, joinOn, "COUNT(*)=C", where, null, 0, 1, out status, out problem, out ra);
                int c;
                if (cnt != null && cnt.Count > 0 && int.TryParse(Field(cnt[0], "C"), NumberStyles.Integer, CultureInfo.InvariantCulture, out c) && c > 0) set.Total = c;
                logger.Info("Kippix: split screen: PCGamingWiki " + kind + " count: " + (set.Total > 0 ? set.Total.ToString(CultureInfo.InvariantCulture) : "not given (" + status + " " + (problem ?? "") + ")"));
                PcgwPace();
            }
            pcgwPagesAll = set.Total > 0 ? (int)Math.Ceiling(set.Total / (double)PcgwBulkPage) : 0;

            int tries = 0, pageMax = 0;   // (pageMax: the most rows a page has brought, in case PCGamingWiki gives fewer than asked for)
            bool first = true;
            while (!left())
            {
                if (online && !settings.SplitOnline) return true;   // (back to Together on this PC: the online list waits)
                pcgwPageNow = set.Offset / Math.Max(1, pageMax > 0 ? pageMax : PcgwBulkPage) + 1;
                if (!refresh && first) SplitRefresh(gen);   // ("Getting player counts (1 of 14)…"; after that, each page's refresh says it)
                first = false;
                var pw = System.Diagnostics.Stopwatch.StartNew();
                int status, retryAfter;
                string problem;
                var rows = PcgwRequest(tables, joinOn, fields, where, "Multiplayer._pageID", set.Offset, PcgwBulkPage, out status, out problem, out retryAfter);
                string head = "Kippix: split screen: PCGamingWiki " + kind + " list page " + pcgwPageNow + (pcgwPagesAll > 0 ? " of " + pcgwPagesAll : "") + ", " + pw.ElapsedMilliseconds + " ms, ";
                if (rows == null)
                {
                    if ((status == 429 || status == 503) && tries < 5)
                    {
                        int wait = retryAfter > 0 ? Math.Min(60000, Math.Max(1000, retryAfter)) : Math.Min(30000, 2000 << tries);
                        tries++;
                        logger.Info(head + status + " busy, waiting " + (wait / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s");
                        if (!SleepUnless(wait, left)) return true;
                        continue;
                    }
                    // (0.82) not busy, just no good answer (timed out, a server error, an error in the answer): once, the simpler query
                    // without the join (games are then matched by name only), which the wiki answers much faster
                    if (!pcgwNoJoin && joinOn != null)
                    {
                        pcgwNoJoin = true;
                        plainQuery();
                        tries = 0;
                        logger.Info(head + "no good answer (" + status + " " + (problem ?? "?") + "): trying the simpler query (names only)");
                        if (!SleepUnless(1500, left)) return true;
                        continue;
                    }
                    if (status == 0 && tries < 2) { tries++; logger.Info(head + "no answer (" + (problem ?? "?") + "), trying again"); if (!SleepUnless(3000, left)) return true; continue; }
                    logger.Info(head + "stopped: " + status + " " + (problem ?? "") + " (carries on next time the page is open)");
                    if (status == 429 || status == 503) pcgwBusy = true; else pcgwDown = true;
                    return false;
                }
                tries = 0;
                var got = rows.Select(r => new PcgwRow
                {
                    P = Field(r, "P").Trim(), S = Field(r, "S").Trim(), Y = Field(r, "Y").Trim().ToLowerInvariant(),
                    N = MaxNumber(Field(r, "N")), M = Field(r, "M").Trim()
                }).Where(r => r.P.Length > 0).ToList();
                logger.Info(head + status + " OK, " + rows.Count + " rows");
                lock (set) { set.Rows.AddRange(got); set.Offset += rows.Count; }
                bool shortPage = pageMax > 0 && rows.Count < pageMax;
                pageMax = Math.Max(pageMax, rows.Count);
                if (pageMax > 0 && set.Total > 0) pcgwPagesAll = (int)Math.Ceiling(set.Total / (double)pageMax);
                if (rows.Count == 0 || (set.Total > 0 ? set.Offset >= set.Total : (shortPage || rows.Count < Math.Min(PcgwBulkPage, pageMax)))) set.Complete = true;
                if (refresh)
                {
                    if (set.Complete) lock (pcgwFileLock) { if (online) pcgwFile.Online = set; else pcgwFile.Local = set; }
                }
                pcgwPageNow = set.Offset / Math.Max(1, pageMax) + 1;   // (the next one, for the progress line)
                if (!refresh || set.Complete)
                {
                    SavePcgwFile();   // (what's done is kept: a first download carries on next time)
                    var idx = BuildPcgwIndex(set);
                    if (online) pcgwOnlineIdx = idx; else pcgwLocalIdx = idx;
                    SplitRefresh(gen);   // (the page refines as each page arrives)
                }
                if (set.Complete)
                {
                    logger.Info("Kippix: split screen: PCGamingWiki " + kind + " list done: " + set.Rows.Count + " games in " + watch.ElapsedMilliseconds + " ms");
                    if (pcgwRunningKind == kind) pcgwRunningKind = null;
                    return true;
                }
                PcgwPace();
            }
            return true;
        }

        private static DateTime pcgwLastStart = DateTime.MinValue;

        /// <summary>At least PcgwBulkSpacingMs between requests (polite to PCGamingWiki).</summary>
        private static void PcgwPace()
        {
            double since = (DateTime.UtcNow - pcgwLastStart).TotalMilliseconds;
            if (since < PcgwBulkSpacingMs) Thread.Sleep((int)(PcgwBulkSpacingMs - since));
        }

        private static bool SleepUnless(int ms, Func<bool> stop)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (stop()) return false;
                Thread.Sleep(200);
            }
            return !stop();
        }

        // ---------------- what Playnite (and Steam) say ----------------

        private static List<string> FeatureNames(Game g)
        {
            try { return g.Features != null ? g.Features.Where(f => f != null && f.Name != null).Select(f => f.Name.ToLowerInvariant()).ToList() : new List<string>(); }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>Playnite's features, plus any category or tag that reads like multiplayer ("Local co-op", "Split screen"…).</summary>
        private static List<string> SignalNames(Game g)
        {
            var list = FeatureNames(g);
            try
            {
                if (g.Categories != null)
                    list.AddRange(g.Categories.Where(c => c != null && c.Name != null).Select(c => c.Name.ToLowerInvariant()).Where(IsAnyMultiFeature));
                if (g.Tags != null)
                    list.AddRange(g.Tags.Where(t => t != null && t.Name != null).Select(t => t.Name.ToLowerInvariant()).Where(IsAnyMultiFeature));
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>Playing together on one PC: split / shared screen, local co-op / versus (not LAN: that's more PCs).</summary>
        private static bool IsLocalFeature(string f)
        {
            return f.Contains("split screen") || f.Contains("split-screen") || f.Contains("splitscreen") || f.Contains("shared screen") || f.Contains("shared/split")
                || f.Contains("local multiplayer") || f.Contains("local multi-player") || f.Contains("local co-op") || f.Contains("local coop")
                || f.Contains("local versus") || f.Contains("local pvp") || f.Contains("couch co-op") || f.Contains("couch coop");
        }

        private static bool IsOnlineFeature(string f)
        {
            return f.Contains("online") || f.StartsWith("mmo") || f.Contains(" mmo") || f.Contains("massively") || f.Contains("cross-platform multiplayer") || f.Contains("crossplay")
                || f == "multiplayer" || f == "multi-player" || f == "co-op" || f == "pvp";
        }

        private static bool IsAnyMultiFeature(string f)
        {
            return IsLocalFeature(f) || IsOnlineFeature(f) || f.Contains("multi-player") || f.Contains("multiplayer") || f.Contains("co-op") || f.Contains("pvp");
        }

        private static bool PcgwYes(string v) { return v == "true" || v == "limited"; }

        private static string SteamIdOf(Game g)
        {
            try
            {
                if (g.PluginId == SteamLibId && !string.IsNullOrEmpty(g.GameId)) return g.GameId;
                if (g.Links != null)
                    foreach (var l in g.Links)
                    {
                        if (l == null || string.IsNullOrEmpty(l.Url)) continue;
                        var m = Regex.Match(l.Url, @"store\.steampowered\.com/app/(\d+)", RegexOptions.IgnoreCase);
                        if (m.Success) return m.Groups[1].Value;
                    }
            }
            catch (Exception) { }
            return null;
        }

        private static string LocalKey(Game g)
        {
            string s = SteamIdOf(g);
            return s != null ? "steam:" + s : "name:" + (g.Name ?? "").Trim().ToLowerInvariant();
        }

        // ---------------- the page ----------------

        private void BuildSplitScreenPage()
        {
            splitTiles = new Dictionary<Guid, PageTile>();
            splitLastSummary = null;
            FillSplitScreen(null);   // (shows at once from Playnite's features; the lists below refine it)
            if (pcgwLoaded && localCache != null) { EnsurePcgwBulk(); return; }
            int gen = pageGen;
            KippixShared.SafeWork.Queue(_ =>
            {
                LoadLocalCache();
                LoadPcgwFile();
                OnUi(() =>
                {
                    if (gen != pageGen || openPanel != "page" || pageKey != "splitscreen") return;
                    ApplySplitResults();
                    EnsurePcgwBulk();
                });
            });
        }

        private string SplitKindText()
        {
            switch (settings.SplitScreenKind)
            {
                case 1: return Loc("LOC_KX_SplitKindSplit", "Split screen");
                case 2: return Loc("LOC_KX_SplitKindShared", "Shared screen");
                default: return Loc("LOC_KX_SplitKindAny", "Split or shared screen");
            }
        }

        private string SplitModeText()
        {
            switch (settings.SplitMode)
            {
                case 1: return Loc("LOC_KX_SplitModeCoop", "Co-op");
                case 2: return Loc("LOC_KX_SplitModeVersus", "Versus");
                default: return Loc("LOC_KX_SplitModeAny", "Co-op or versus");
            }
        }

        private static int NextStep(int[] steps, int now)
        {
            int i = Array.IndexOf(steps, now);
            return steps[(i + 1) % steps.Length];
        }

        /// <summary>
        /// The player counts a choice takes in (the most that can play). Exactly: the number itself; past the single numbers a
        /// step takes in the ones since the step before (online 6: 5–6), and the last step everything above. Or more: n and up.
        /// </summary>
        private void SplitRange(bool online, int n, out int lo, out int hi)
        {
            lo = 0; hi = int.MaxValue;
            if (n <= 0) return;
            if (!settings.SplitExact) { lo = n; return; }
            var steps = online ? OnlinePlayerSteps : LocalPlayerSteps;
            int i = Array.IndexOf(steps, n);
            if (i < 0) { lo = hi = n; return; }
            lo = i > 1 ? steps[i - 1] + 1 : n;
            hi = i == steps.Length - 1 ? int.MaxValue : n;
        }

        private string SplitPlayersChipText(bool online, int n)
        {
            if (n <= 0) return Loc("LOC_KX_SplitPlayersAny", "Any number of players");
            int lo, hi;
            SplitRange(online, n, out lo, out hi);
            if (hi == int.MaxValue) return string.Format(Loc("LOC_KX_SplitPlayersAtLeast", "{0}+ players"), lo);
            if (lo == hi) return string.Format(Loc("LOC_KX_SplitPlayersN", "{0} players"), lo);
            return string.Format(Loc("LOC_KX_SplitPlayersRange", "{0}–{1} players"), lo, hi);
        }

        private string SplitPlayersPhrase(int lo, int hi)
        {
            if (hi == int.MaxValue) return string.Format(Loc("LOC_KX_SplitOrMoreN", "{0} or more players"), lo);
            if (lo == hi) return string.Format(Loc("LOC_KX_SplitExactlyN", "exactly {0} players"), lo);
            return string.Format(Loc("LOC_KX_SplitPlayersRange", "{0}–{1} players"), lo, hi);
        }

        private class LocalGame
        {
            public Game Game;
            public int Players;        // -1 not known
            public bool Split, Shared, Coop, Versus, Installed;
            public string From;        // pcgw / old / playnite / steam (the log's summary)
        }

        /// <summary>What Steam's categories say about a Steam game (from badges-steam.json), if they've been read.</summary>
        private bool SteamCatsFor(string appId, out Feat f)
        {
            f = Feat.None;
            if (appId == null) return false;
            long[] v;
            lock (steamFeatsLock)
            {
                if (steamFeats == null || !steamFeats.TryGetValue(appId, out v) || v == null || v.Length < 2) return false;
            }
            f = (Feat)v[1];
            return true;
        }

        /// <summary>The games for one way of playing (on this PC, or online), with what's known about how many can play. Any thread; nothing goes online.</summary>
        private List<LocalGame> MultiGames(bool online)
        {
            var list = new List<LocalGame>();
            List<Game> games;
            // (Profiles: only what whoever's playing can see; a Kids profile isn't offered games above its age)
            try { games = api.Database.Games.Where(g => g != null && !g.Hidden && ProfileAllowsGame(g)).ToList(); } catch (Exception) { return list; }
            try { LoadSteamFeats(); } catch (Exception) { }
            var localIdx = pcgwLocalIdx;
            var onlineIdx = pcgwOnlineIdx;
            var old = localCache;
            foreach (var g in games)
            {
                var fs = SignalNames(g);
                string steam = SteamIdOf(g);
                Feat sf;
                bool steamKnown = SteamCatsFor(steam, out sf);
                LocalInfo info = null;
                if (old != null) lock (old) old.TryGetValue(LocalKey(g), out info);
                bool oldFound = info != null && info.Found && !info.Failed;
                int players = -1;
                string modes = "", from;
                bool splitWord = false, sharedWord = false;
                if (!online)
                {
                    bool feat = fs.Any(IsLocalFeature);
                    bool steamSays = steamKnown && (sf & Feat.Split) != 0;
                    var row = MatchPcgw(localIdx, g, steam);
                    if (row != null) { players = row.N; modes = row.M; from = "pcgw"; }
                    else if (oldFound && PcgwYes(info.Local)) { players = info.Players; modes = info.Modes ?? ""; from = "old"; }
                    else
                    {
                        if (!feat && !steamSays) continue;
                        // PCGamingWiki (an earlier answer) or Steam say it can't be played on one PC: Call of Duty, Battlefield…
                        // have split screen only on consoles, and IGDB's features describe those versions
                        if (oldFound && (info.Local == "false" || info.Local == "hackable" || PcgwYes(info.Online) || PcgwYes(info.Lan))) continue;
                        if (steamKnown && !steamSays) continue;
                        // the local list is complete and it isn't in it, but it is in the online one: online only
                        if (localIdx != null && localIdx.Complete && MatchPcgw(onlineIdx, g, steam) != null) continue;
                        from = feat ? "playnite" : "steam";
                    }
                }
                else
                {
                    bool feat = fs.Any(IsOnlineFeature);
                    bool steamSays = steamKnown && (sf & Feat.Online) != 0;
                    var row = MatchPcgw(onlineIdx, g, steam);
                    if (row != null) { players = row.N; modes = row.M; from = "pcgw"; }
                    else if (oldFound && PcgwYes(info.Online)) { players = info.OnlinePlayers; modes = info.OnlineModes ?? ""; from = "old"; }
                    else
                    {
                        if (!feat && !steamSays) continue;
                        if (oldFound && info.Online == "false") continue;
                        if (steamKnown && !steamSays) continue;
                        from = feat ? "playnite" : "steam";
                    }
                }
                modes = (modes ?? "").ToLowerInvariant();
                // features for this way of playing only (an online co-op game isn't a local co-op one)
                var mine = fs.Where(f => online ? (IsOnlineFeature(f) || (!IsLocalFeature(f) && !f.Contains("lan")))
                                                : (IsLocalFeature(f) || (!IsOnlineFeature(f) && !f.Contains("lan")))).ToList();
                splitWord = fs.Any(f => f.Contains("split"));
                sharedWord = fs.Any(f => f.Contains("shared"));
                if (fs.Any(f => f.Contains("shared/split"))) { splitWord = sharedWord = false; }   // (Steam's "Shared/Split Screen": either)
                list.Add(new LocalGame
                {
                    Game = g, Players = players > 0 ? players : -1, Installed = g.IsInstalled, From = from,
                    Split = splitWord, Shared = sharedWord,
                    Coop = mine.Any(f => f.Contains("co-op") || f.Contains("coop") || f.Contains("cooperative")) || modes.Contains("co-op") || modes.Contains("coop"),
                    Versus = mine.Any(f => f.Contains("pvp") || f.Contains("versus")) || modes.Contains("versus") || modes.Contains("pvp")
                });
            }
            return list;
        }

        /// <summary>A choice changed: kept, and the page built again with the highlight on the same button.</summary>
        private void SplitChanged(string focusKey)
        {
            SaveSettings();
            PlaySound("move");
            FillSplitScreen(focusKey);
            if (focusKey == "where") EnsurePcgwBulk();   // (Online: its list, the first time)
        }

        /// <summary>The buttons, then the games (focusKey: the button to put the highlight back on, null leaves it).</summary>
        private void FillSplitScreen(string focusKey)
        {
            PageChips.Clear();   // (the covers stay: the new list is fitted around them once it's worked out)
            PageTitle = PageName("splitscreen");
            bool online = settings.SplitOnline;
            int n = online ? settings.OnlinePlayers : settings.SplitPlayers;

            var whereChip = AddPageChip(online ? Loc("LOC_KX_SplitWhereOnline", "Online") : Loc("LOC_KX_SplitWhereLocal", "Together on this PC"), online ? "globe" : "tv", () =>
            {
                settings.SplitOnline = !settings.SplitOnline;
                SplitChanged("where");
            });
            whereChip.Key = "where";
            whereChip.IsSelected = true;
            var players = AddPageChip(SplitPlayersChipText(online, n), "friends", () =>
            {
                if (settings.SplitOnline) settings.OnlinePlayers = NextStep(OnlinePlayerSteps, settings.OnlinePlayers);
                else settings.SplitPlayers = NextStep(LocalPlayerSteps, settings.SplitPlayers);
                SplitChanged("players");
            });
            players.Key = "players";
            players.IsSelected = n > 0;
            if (n > 0)
            {
                var match = AddPageChip(settings.SplitExact ? Loc("LOC_KX_SplitMatchExact", "Exactly") : Loc("LOC_KX_SplitMatchOrMore", "Or more"), "filter", () =>
                {
                    settings.SplitExact = !settings.SplitExact;
                    SplitChanged("match");
                });
                match.Key = "match";
                match.IsSelected = settings.SplitExact;
            }
            // (0.85: no split / shared screen or co-op / versus filters: every way of playing together is always included)

            ApplySplitResults();
            if (focusKey != null) FocusPageSoon(PageChips.FirstOrDefault(c => c.Key == focusKey));
        }

        private string SplitTileSub(LocalGame lg, bool online)
        {
            var bits = new List<string>();
            if (lg.Players > 0)
                bits.Add(string.Format(online ? Loc("LOC_KX_SplitUpToOnline", "Up to {0} online") : Loc("LOC_KX_SplitUpToLocal", "Up to {0} on this PC"), lg.Players));
            else bits.Add(Loc("LOC_KX_SplitCountUnknown", "Count not known"));
            if (lg.Coop && !lg.Versus) bits.Add(Loc("LOC_KX_SplitModeCoop", "Co-op"));
            if (lg.Versus && !lg.Coop) bits.Add(Loc("LOC_KX_SplitModeVersus", "Versus"));
            if (!lg.Installed) bits.Add(Loc("LOC_KX_NotInstalled", "Not installed"));
            return string.Join(" · ", bits.ToArray());
        }

        /// <summary>A list page arrived (any thread): the page catches up, if it's still open on the same visit.</summary>
        private void SplitRefresh(int gen)
        {
            OnUi(() => { if (gen == pageGen) ApplySplitResults(); });
        }

        /// <summary>
        /// The games for the choices, from what's known now. Worked out off the UI thread (it reads the whole library, all on
        /// this PC); only putting them on the page happens here. The covers already there stay, so the highlight stays put.
        /// </summary>
        private void ApplySplitResults()
        {
            if (openPanel != "page" || pageKey != "splitscreen") return;
            if (splitComputing) { splitComputeAgain = true; return; }   // (one at a time: the newest choices / answers win)
            splitComputing = true;
            int gen = pageGen;
            bool online = settings.SplitOnline;
            KippixShared.SafeWork.Queue(_ =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                List<LocalGame> all = null;
                try { all = MultiGames(online); }
                catch (Exception ex) { logger.Warn("Kippix: split screen games: " + ex.Message); }
                long ms = watch.ElapsedMilliseconds;
                OnUi(() =>
                {
                    splitComputing = false;
                    bool again = splitComputeAgain;
                    splitComputeAgain = false;
                    if (again) { ApplySplitResults(); return; }
                    if (all == null || gen != pageGen || online != settings.SplitOnline) return;
                    var w2 = System.Diagnostics.Stopwatch.StartNew();
                    ShowSplitResults(all, online);
                    string summary = (online ? "online" : "local") + ": " + all.Count + " games (" + all.Count(x => x.From == "pcgw") + " from PCGamingWiki's list, "
                                     + all.Count(x => x.From == "old") + " from earlier answers, " + all.Count(x => x.From == "playnite") + " from Playnite's features, "
                                     + all.Count(x => x.From == "steam") + " from Steam's categories; " + all.Count(x => x.Players > 0) + " with a player count)";
                    if (summary != splitLastSummary)
                    {
                        splitLastSummary = summary;
                        logger.Info("Kippix: split screen: " + summary + ", worked out in " + ms + " ms, shown in " + w2.ElapsedMilliseconds + " ms");
                    }
                });
            });
        }

        private void ShowSplitResults(List<LocalGame> all, bool online)
        {
            if (openPanel != "page" || pageKey != "splitscreen") return;
            int n = online ? settings.OnlinePlayers : settings.SplitPlayers;
            int lo, hi;
            SplitRange(online, n, out lo, out hi);
            var fits = all.ToList();   // (0.85: split / shared screen and co-op / versus all included, no filters)
            // a chosen number shows only games known to take that many; "Any number" shows the ones not known too
            var known = fits.Where(lg => lg.Players > 0 && lg.Players >= lo && lg.Players <= hi).ToList();
            var unknown = fits.Where(lg => lg.Players <= 0).ToList();
            var shown = (n > 0 ? known : known.Concat(unknown)).OrderByDescending(lg => lg.Installed).ThenBy(lg => lg.Game.Name ?? "", StringComparer.CurrentCultureIgnoreCase).ToList();

            var want = new List<PageTile>();
            foreach (var lg in shown)
            {
                var gid = lg.Game.Id;
                PageTile t;
                if (!splitTiles.TryGetValue(gid, out t))
                {
                    t = new PageTile { Name = lg.Game.Name, GameId = gid, CoverPath = CoverOf(gid), Command = new RelayCommand(_ => OpenGameFromPage(gid)) };
                    splitTiles[gid] = t;
                }
                t.Sub = SplitTileSub(lg, online);
                want.Add(t);
            }
            var focused = FocusedPageItem() as PageTile;
            bool hadFocus = focused != null && PageTiles.Contains(focused);
            SyncPageTiles(want);
            if (hadFocus && !PageTiles.Contains(focused))
                FocusPageSoon(PageTiles.Count > 0 ? (object)PageTiles[0] : PageChips.FirstOrDefault());   // (its game no longer fits)

            string sub;
            if (n > 0)
                sub = string.Format(online ? Loc("LOC_KX_SplitSubOnline2", "{0} online games for {1}") : Loc("LOC_KX_SplitSubLocal2", "{0} games for {1} together on this PC"),
                                    known.Count, SplitPlayersPhrase(lo, hi));
            else
                sub = string.Format(online ? Loc("LOC_KX_SplitSubOnlineAny", "{0} games with online multiplayer") : Loc("LOC_KX_SplitSubLocalAny", "{0} games to play together on this PC"), shown.Count);
            if (n > 0 && unknown.Count > 0)
                sub += "   ·   " + string.Format(Loc("LOC_KX_SplitUnknownHidden2", "{0} more whose player count isn't known (they're under Any number of players)"), unknown.Count);
            PageSub = sub;
            PageTilesTitle = shown.Count > 0 ? Loc("LOC_KX_SplitTilesTitle", "Installed first") : "";

            var lines = new List<string>();
            string running = pcgwRunningKind;
            if (pcgwRunning && running != null && running == (online ? "online" : "local"))
            {
                int page = pcgwPageNow, pages = pcgwPagesAll;
                lines.Add(pages > 0
                    ? string.Format(Loc("LOC_KX_SplitBulkProgress", "Getting player counts ({0} of {1})…"), Math.Min(page, pages), pages)
                    : string.Format(Loc("LOC_KX_SplitBulkProgressN", "Getting player counts ({0})…"), Math.Max(1, page)));
            }
            else if (!pcgwRunning && (pcgwBusy || pcgwDown))
            {
                var idx = online ? pcgwOnlineIdx : pcgwLocalIdx;
                if (idx == null || !idx.Complete)
                    lines.Add(pcgwBusy ? Loc("LOC_KX_SplitBulkBusy", "PCGamingWiki is busy right now: the player counts carry on next time you open this page.")
                                       : Loc("LOC_KX_SplitBulkDown", "PCGamingWiki isn't answering right now: the player counts carry on next time you open this page."));
            }
            if (PageTiles.Count == 0)
                lines.Add(all.Count == 0
                    ? (online ? Loc("LOC_KX_SplitNoneOnline", "None of your games is marked for online multiplayer.")
                              : Loc("LOC_KX_SplitNone", "None of your games is marked for local multiplayer (Playnite's features: Split screen, Shared screen, Local co-op…)."))
                    : Loc("LOC_KX_SplitNoneFit3", "Nothing fits those choices: try another number of players, Or more, or Any number of players."));
            SetPageStatus(string.Join("\n", lines.ToArray()));
            NotifyPage();
            LoadPageImages();
        }

        /// <summary>PageTiles made to match the list, moving as little as possible (tiles that stay keep their place on screen and the highlight).</summary>
        private void SyncPageTiles(List<PageTile> want)
        {
            var keep = new HashSet<PageTile>(want);
            for (int i = PageTiles.Count - 1; i >= 0; i--)
                if (!keep.Contains(PageTiles[i])) PageTiles.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
            {
                if (i < PageTiles.Count && ReferenceEquals(PageTiles[i], want[i])) continue;
                int at = PageTiles.IndexOf(want[i]);
                if (at >= 0) PageTiles.Move(at, i);
                else PageTiles.Insert(i, want[i]);
            }
        }

        // ---------------- PCGamingWiki's api ----------------

        private static string CleanName(string name)
        {
            return Regex.Replace(name ?? "", "[™®©]", "").Trim();
        }

        /// <summary>The biggest number in PCGamingWiki's player field ("4", "2-4", "8+"), or -1.</summary>
        private static int MaxNumber(string s)
        {
            int best = -1;
            foreach (Match m in Regex.Matches(s ?? "", @"\d+"))
            {
                int p;
                if (int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > best && p < 100000) best = p;
            }
            return best > 0 ? best : -1;
        }

        private static string Field(Dictionary<string, object> row, string name)
        {
            if (row == null) return "";
            object v;
            if (row.TryGetValue(name, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return "";
        }

        /// <summary>
        /// One PCGamingWiki Cargo query: the rows' fields (by the names given after "="), or null with status (the HTTP status;
        /// 200 with an error answer; 0 no answer at all), what went wrong, and a Retry-After in ms (0 none).
        /// </summary>
        private static List<Dictionary<string, object>> PcgwRequest(string tables, string joinOn, string fields, string where, string orderBy, int offset, int limit,
                                                                  out int status, out string problem, out int retryAfterMs)
        {
            status = 0;
            problem = null;
            retryAfterMs = 0;
            pcgwLastStart = DateTime.UtcNow;
            string url = PcgwApi + "?action=cargoquery&format=json&limit=" + limit
                       + (offset > 0 ? "&offset=" + offset : "")
                       + "&tables=" + Uri.EscapeDataString(tables)
                       + (joinOn != null ? "&join_on=" + Uri.EscapeDataString(joinOn) : "")
                       + "&fields=" + Uri.EscapeDataString(fields)
                       + "&where=" + Uri.EscapeDataString(where)
                       + (orderBy != null ? "&order_by=" + Uri.EscapeDataString(orderBy) : "");
            string text;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Kippix/1.0 (Playnite add-on; split screen player counts, one bulk download a month)";
                req.Accept = "application/json";
                req.Timeout = PcgwBulkTimeoutMs;
                req.ReadWriteTimeout = PcgwBulkTimeoutMs;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    status = (int)resp.StatusCode;
                    text = rd.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http == null)
                {
                    problem = ex.Status == WebExceptionStatus.Timeout ? "timed out" : ex.Message;
                    return null;
                }
                try
                {
                    status = (int)http.StatusCode;
                    string ra = http.Headers["Retry-After"];
                    int secs;
                    if (!string.IsNullOrEmpty(ra) && int.TryParse(ra.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out secs) && secs > 0) retryAfterMs = secs * 1000;
                    problem = http.StatusDescription + (string.IsNullOrEmpty(ra) ? "" : " [Retry-After " + ra + "]");
                }
                finally { try { http.Close(); } catch (Exception) { } }
                return null;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
            Dictionary<string, object> root = null;
            try { root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception ex) { problem = "not JSON: " + ex.Message; return null; }
            object rows;
            if (root == null || !root.TryGetValue("cargoquery", out rows) || !(rows is object[]))
            {
                object err;
                var e = root != null && root.TryGetValue("error", out err) ? err as Dictionary<string, object> : null;
                object info = null;
                if (e != null && !e.TryGetValue("info", out info)) e.TryGetValue("code", out info);
                problem = info != null ? Convert.ToString(info, CultureInfo.InvariantCulture) : "no cargoquery in the answer";
                if (problem.Length > 200) problem = problem.Substring(0, 200) + "…";
                return null;
            }
            var list = new List<Dictionary<string, object>>();
            foreach (var r in (object[])rows)
            {
                var d = r as Dictionary<string, object>;
                object t;
                if (d != null && d.TryGetValue("title", out t) && t is Dictionary<string, object>) list.Add((Dictionary<string, object>)t);
            }
            return list;
        }
    }
}

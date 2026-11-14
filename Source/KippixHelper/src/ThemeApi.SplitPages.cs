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
    /// Split screen's player counts from PCGamingWiki (0.83). PCGamingWiki stopped answering Cargo queries ("You don't have
    /// permission to run arbitrary Cargo queries"), which the bulk download used, so instead each of YOUR multiplayer games'
    /// own pages is read (the ordinary MediaWiki api: action=query, prop=revisions, the page's wikitext; several pages a request,
    /// redirects followed) and its {{Network/Multiplayer}} box taken apart: local play / players / modes, online play / players /
    /// modes, LAN play. Only games Playnite or Steam already mark as multiplayer are asked about, local ones first; one request
    /// at a time, 1.5 s apart, only while the Split screen page is open (leaving stops it; what's done is kept). The answers go in
    /// localplayers.json (the same file the old per-game look-ups used, so MultiGames reads them as before) and are kept for
    /// 30 days (a game with no page: also 30 days, then asked again). A page whose Steam app id is another game's isn't used.
    /// </summary>
    public partial class ThemeApi
    {
        private const int PcgwPagesPerRequest = 15;
        private const int PcgwPagesSpacingMs = 1500;
        private const int PcgwAnswerDays = 30;
        private const int PcgwAnswerVersion = 2;
        private readonly object localSaveLock = new object();

        /// <summary>A game that's worth asking about: Playnite's features / categories / tags or Steam's categories say multiplayer.</summary>
        private bool WorthAskingPcgw(Game g, out bool local)
        {
            local = false;
            var fs = SignalNames(g);
            Feat sf;
            bool steamKnown = SteamCatsFor(SteamIdOf(g), out sf);
            local = fs.Any(IsLocalFeature) || (steamKnown && (sf & Feat.Split) != 0);
            bool online = fs.Any(IsOnlineFeature) || (steamKnown && (sf & Feat.Online) != 0);
            return local || online || fs.Any(IsAnyMultiFeature);
        }

        /// <summary>Already answered recently (a page found or not), so not asked again.</summary>
        private bool PcgwAnswered(Game g)
        {
            var c = localCache;
            if (c == null) return false;
            LocalInfo li;
            lock (c) c.TryGetValue(LocalKey(g), out li);
            if (li == null || li.Failed) return false;
            if (li.V < PcgwAnswerVersion) return li.Found;   // (an old per-game answer that found its page: still good)
            return (DateTime.UtcNow - li.At.ToUniversalTime()).TotalDays < PcgwAnswerDays;
        }

        /// <summary>While the Split screen page is open: the pages of the games not answered yet (local ones first).</summary>
        private void EnsurePcgwPages()
        {
            if (pcgwRunning || !pcgwLoaded || localCache == null) return;
            int gen = pageGen;
            pcgwRunning = true;
            pcgwBusy = pcgwDown = false;
            KippixShared.SafeWork.Queue(_ =>
            {
                try
                {
                    try { LoadSteamFeats(); } catch (Exception) { }
                    List<Game> games;
                    try { games = api.Database.Games.Where(g => g != null && !g.Hidden).ToList(); } catch (Exception) { games = new List<Game>(); }
                    var localFirst = new List<Game>();
                    var rest = new List<Game>();
                    foreach (var g in games)
                    {
                        bool local;
                        if (!WorthAskingPcgw(g, out local) || PcgwAnswered(g)) continue;
                        if (local) localFirst.Add(g); else rest.Add(g);
                    }
                    var todo = localFirst.Concat(rest).GroupBy(LocalKey).Select(x => x.First()).ToList();
                    if (todo.Count == 0) return;
                    logger.Info("Kippix: split screen: PCGamingWiki pages to read: " + todo.Count + " (" + localFirst.Count + " with local multiplayer first)");
                    pcgwRunningKind = settings.SplitOnline ? "online" : "local";
                    pcgwPagesAll = (int)Math.Ceiling(todo.Count / (double)PcgwPagesPerRequest);
                    pcgwPageNow = 1;
                    SplitRefresh(gen);
                    Func<bool> left = () => Volatile.Read(ref pageGen) != gen;
                    int answered = 0, found = 0;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < todo.Count && !left(); i += PcgwPagesPerRequest)
                    {
                        pcgwPageNow = i / PcgwPagesPerRequest + 1;
                        var batch = todo.Skip(i).Take(PcgwPagesPerRequest).ToList();
                        int f;
                        bool ok = PcgwReadPages(batch, left, out f);
                        if (!ok) break;
                        answered += batch.Count;
                        found += f;
                        SaveLocalCache();
                        SplitRefresh(gen);   // (the page refines as each batch comes in)
                        if (i + PcgwPagesPerRequest < todo.Count && !SleepUnless(PcgwPagesSpacingMs, left)) break;
                    }
                    logger.Info("Kippix: split screen: PCGamingWiki pages read: " + answered + " of " + todo.Count + " games, " + found + " pages found, " + watch.ElapsedMilliseconds + " ms" + (left() ? " (page left)" : ""));
                }
                catch (Exception ex) { logger.Warn("Kippix: split screen: PCGamingWiki pages: " + ex.Message); pcgwDown = true; }
                finally
                {
                    pcgwRunning = false;
                    pcgwRunningKind = null;
                    SplitRefresh(gen);
                }
            });
        }

        /// <summary>One batch: each game's page by its name (then, if there's no page, by its name without an edition). False: stopped (busy / not answering / page left).</summary>
        private bool PcgwReadPages(List<Game> batch, Func<bool> left, out int found)
        {
            found = 0;
            var titleOf = new Dictionary<Game, string>();
            foreach (var g in batch) titleOf[g] = PcgwTitle(g.Name);
            Dictionary<string, string> texts;
            if (!PcgwFetchTexts(titleOf.Values.Where(t => t.Length > 0).Distinct().ToList(), left, out texts)) return false;
            // no page by its full name: once more without "… Game of the Year Edition" and the like
            var second = new Dictionary<Game, string>();
            foreach (var g in batch)
            {
                if (texts.ContainsKey(titleOf[g])) continue;
                string plain = PcgwTitle(EditionRx.Replace(CleanName(g.Name ?? ""), ""));
                if (plain.Length > 0 && plain != titleOf[g]) second[g] = plain;
            }
            if (second.Count > 0 && !left())
            {
                PcgwPace2();
                Dictionary<string, string> more;
                if (PcgwFetchTexts(second.Values.Distinct().ToList(), left, out more))
                    foreach (var kv in second) { string t; if (more.TryGetValue(kv.Value, out t)) texts[titleOf[kv.Key]] = t; }
            }
            var c = localCache;
            foreach (var g in batch)
            {
                string text;
                var li = new LocalInfo { V = PcgwAnswerVersion, At = DateTime.UtcNow };
                if (texts.TryGetValue(titleOf[g], out text) && PcgwPageFits(text, SteamIdOf(g)))
                {
                    PcgwParseMultiplayer(text, li);
                    li.Found = true;
                    found++;
                }
                lock (c) c[LocalKey(g)] = li;
            }
            return true;
        }

        /// <summary>A game's name as a PCGamingWiki page title (no ™ ® ©, straight apostrophes, single spaces).</summary>
        private static string PcgwTitle(string name)
        {
            string s = CleanName(name).Replace('’', '\'').Replace('‘', '\'');
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>The page is for this game: no Steam app id on either side, or the game's is among the page's.</summary>
        private static bool PcgwPageFits(string text, string steam)
        {
            if (steam == null) return true;
            var ids = new List<string>();
            foreach (Match m in Regex.Matches(text, @"\|\s*steam appid(?: side)?\s*=\s*([0-9 ,]+)", RegexOptions.IgnoreCase))
                ids.AddRange(SteamIdsIn(m.Groups[1].Value));
            return ids.Count == 0 || ids.Contains(steam);
        }

        /// <summary>The page's {{Network/Multiplayer}} fields into an answer.</summary>
        private static void PcgwParseMultiplayer(string text, LocalInfo li)
        {
            Func<string, string> field = name =>
            {
                var m = Regex.Match(text, @"\|\s*" + Regex.Escape(name) + @"\s*=([^\n|}]*)", RegexOptions.IgnoreCase);
                if (!m.Success) return "";
                string v = Regex.Replace(m.Groups[1].Value, @"<ref.*$", "", RegexOptions.IgnoreCase).Trim();
                return v;
            };
            Func<string, string> yes = name =>
            {
                var m = Regex.Match(field(name).ToLowerInvariant(), "^[a-z]+");
                return m.Success ? m.Value : "";
            };
            li.Local = yes("local play");
            li.Players = MaxNumber(field("local play players"));
            li.Modes = field("local play modes");
            li.Online = yes("online play");
            li.OnlinePlayers = MaxNumber(field("online play players"));
            li.OnlineModes = field("online play modes");
            li.Lan = yes("lan play");
        }

        /// <summary>The wikitext of these pages (asked-for title → text; titles with no page are left out). False: busy / not answering / page left.</summary>
        private bool PcgwFetchTexts(List<string> titles, Func<bool> left, out Dictionary<string, string> texts)
        {
            texts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (titles.Count == 0) return true;
            string url = PcgwApi + "?action=query&format=json&formatversion=2&prop=revisions&rvprop=content&rvslots=main&redirects=1&titles="
                         + Uri.EscapeDataString(string.Join("|", titles.ToArray()));
            for (int tries = 0; ; tries++)
            {
                if (left()) return false;
                int status, retryAfter;
                string problem;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var root = PcgwGetJson(url, out status, out problem, out retryAfter);
                string head = "Kippix: split screen: PCGamingWiki pages (" + titles.Count + "), " + watch.ElapsedMilliseconds + " ms, ";
                if (root == null)
                {
                    if ((status == 429 || status == 503) && tries < 4)
                    {
                        int wait = retryAfter > 0 ? Math.Min(60000, Math.Max(1000, retryAfter)) : Math.Min(30000, 3000 << tries);
                        logger.Info(head + status + " busy, waiting " + (wait / 1000) + " s");
                        if (!SleepUnless(wait, left)) return false;
                        continue;
                    }
                    if (status == 0 && tries < 2) { logger.Info(head + "no answer (" + (problem ?? "?") + "), trying again"); if (!SleepUnless(3000, left)) return false; continue; }
                    logger.Info(head + "stopped: " + status + " " + (problem ?? ""));
                    if (status == 429 || status == 503) pcgwBusy = true; else pcgwDown = true;
                    return false;
                }
                var q = root.ContainsKey("query") ? root["query"] as Dictionary<string, object> : null;
                if (q == null)
                {
                    var err = root.ContainsKey("error") ? root["error"] as Dictionary<string, object> : null;
                    logger.Info(head + "stopped: " + (err != null && err.ContainsKey("info") ? Convert.ToString(err["info"], CultureInfo.InvariantCulture) : "no query in the answer"));
                    pcgwDown = true;
                    return false;
                }
                // asked-for title → the page's title (normalised, then redirects followed)
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string key in new[] { "normalized", "redirects" })
                {
                    var arr = q.ContainsKey(key) ? q[key] as object[] : null;
                    if (arr == null) continue;
                    foreach (var o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("from") || !d.ContainsKey("to")) continue;
                        map[Convert.ToString(d["from"], CultureInfo.InvariantCulture)] = Convert.ToString(d["to"], CultureInfo.InvariantCulture);
                    }
                }
                var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);
                var pages = q.ContainsKey("pages") ? q["pages"] as object[] : null;
                if (pages != null)
                    foreach (var o in pages)
                    {
                        var p = o as Dictionary<string, object>;
                        if (p == null || p.ContainsKey("missing") || p.ContainsKey("invalid") || !p.ContainsKey("title")) continue;
                        var revs = p.ContainsKey("revisions") ? p["revisions"] as object[] : null;
                        var rev = revs != null && revs.Length > 0 ? revs[0] as Dictionary<string, object> : null;
                        var slots = rev != null && rev.ContainsKey("slots") ? rev["slots"] as Dictionary<string, object> : null;
                        var main = slots != null && slots.ContainsKey("main") ? slots["main"] as Dictionary<string, object> : null;
                        string content = main != null && main.ContainsKey("content") ? Convert.ToString(main["content"], CultureInfo.InvariantCulture)
                                       : rev != null && rev.ContainsKey("content") ? Convert.ToString(rev["content"], CultureInfo.InvariantCulture) : null;
                        if (!string.IsNullOrEmpty(content)) byTitle[Convert.ToString(p["title"], CultureInfo.InvariantCulture)] = content;
                    }
                foreach (var t in titles)
                {
                    string at = t, text;
                    for (int hop = 0; hop < 4 && map.ContainsKey(at); hop++) at = map[at];
                    if (byTitle.TryGetValue(at, out text)) texts[t] = text;
                }
                logger.Info(head + status + " OK, " + texts.Count + " of " + titles.Count + " pages");
                return true;
            }
        }

        private static DateTime pcgwPagesLast = DateTime.MinValue;
        private static void PcgwPace2()
        {
            double since = (DateTime.UtcNow - pcgwPagesLast).TotalMilliseconds;
            if (since < PcgwPagesSpacingMs) Thread.Sleep((int)(PcgwPagesSpacingMs - since));
        }

        /// <summary>A GET to PCGamingWiki's api: the JSON answer, or null with the HTTP status (0: no answer), what went wrong and a Retry-After (ms).</summary>
        private static Dictionary<string, object> PcgwGetJson(string url, out int status, out string problem, out int retryAfterMs)
        {
            status = 0;
            problem = null;
            retryAfterMs = 0;
            pcgwPagesLast = DateTime.UtcNow;
            string text;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Kippix/1.0 (Playnite add-on; split screen player counts for the games in your library)";
                req.Accept = "application/json";
                req.Timeout = 45000;
                req.ReadWriteTimeout = 45000;
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
                if (http == null) { problem = ex.Status == WebExceptionStatus.Timeout ? "timed out" : ex.Message; return null; }
                try
                {
                    status = (int)http.StatusCode;
                    string ra = http.Headers["Retry-After"];
                    int secs;
                    if (!string.IsNullOrEmpty(ra) && int.TryParse(ra.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out secs) && secs > 0) retryAfterMs = secs * 1000;
                    problem = http.StatusDescription;
                }
                finally { try { http.Close(); } catch (Exception) { } }
                return null;
            }
            catch (Exception ex) { problem = ex.Message; return null; }
            try { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception ex) { problem = "not JSON: " + ex.Message; return null; }
        }

        /// <summary>localplayers.json written (a temporary file first, so a crash never leaves half a file).</summary>
        private void SaveLocalCache()
        {
            var c = localCache;
            if (c == null) return;
            lock (localSaveLock)
            {
                try
                {
                    string json;
                    lock (c) json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(c);
                    string file = LocalCacheFile, tmp = file + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
                }
                catch (Exception ex) { logger.Warn("Kippix: split screen answers: " + ex.Message); }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KippixHelper
{
    /// <summary>Totals per game at a moment (to see what was played since, also outside Playnite).</summary>
    public class PlaytimeSnapshot
    {
        public DateTime At;
        public Dictionary<string, long> Totals = new Dictionary<string, long>();
    }

    /// <summary>
    /// Spotlight play time without GameActivity: Playnite only keeps a total per game, so
    ///  - the first time, the last two weeks come from Steam's own record (localconfig.vdf: minutes in the last 2 weeks, last played),
    ///  - after that, a game's total going up since we last looked (played outside Playnite, or Steam's time synced in) counts too,
    ///  - and sessions started from Playnite are recorded as they end (ThemeApi.Widgets.cs).
    /// Filled-in sessions are marked Estimated.
    /// </summary>
    public partial class ThemeApi
    {
        private string SnapshotFile { get { return Path.Combine(plugin.GetPluginUserDataPath(), "playtime-snapshot.json"); } }

        /// <summary>Before showing play time: fill in what was played that we didn't see. Runs on the widget's background thread.</summary>
        private void CatchUpPlaytime()
        {
            if (!settings.RecordSessions || GameActivityInstalled) return;
            try
            {
                lock (sessionLock)
                {
                    var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    List<PlaySession> sessions;
                    if (File.Exists(SessionsFile))
                    {
                        try { sessions = js.Deserialize<List<PlaySession>>(File.ReadAllText(SessionsFile)) ?? new List<PlaySession>(); }
                        catch (Exception) { return; }   // RecordSession keeps an unreadable file aside; don't touch it here
                    }
                    else sessions = new List<PlaySession>();
                    // the serializer gives dates back as UTC; everything here compares local times (an empty entry ("null") is dropped)
                    sessions.RemoveAll(x => x == null);
                    foreach (var x in sessions) if (x.Start.Kind == DateTimeKind.Utc) x.Start = x.Start.ToLocalTime();
                    PlaytimeSnapshot snap = null;
                    try { if (File.Exists(SnapshotFile)) snap = js.Deserialize<PlaytimeSnapshot>(File.ReadAllText(SnapshotFile)); } catch (Exception) { }
                    if (snap != null && snap.At.Kind == DateTimeKind.Utc) snap.At = snap.At.ToLocalTime();
                    var steamTotals = new Dictionary<string, long>();
                    var accounted = new Dictionary<string, long>();

                    var games = api.Database.Games.ToList();
                    var now = DateTime.Now;
                    bool changed = false;

                    if (snap == null || snap.Totals == null)
                    {
                        // first time: the last two weeks from Steam
                        var twoWeeks = now.AddDays(-14);
                        foreach (var s in SteamRecentSessions(games, steamTotals))
                        {
                            // what Steam counted, less what was already recorded from Playnite in those two weeks
                            long recorded = sessions.Where(x => x.GameId == s.GameId && x.Start >= twoWeeks).Sum(x => x.Seconds);
                            s.Seconds -= recorded;
                            if (s.Seconds < 120) continue;
                            sessions.Add(s);
                            changed = true;
                        }
                    }
                    else
                    {
                        // since we last looked: totals that went up by more than the sessions we recorded
                        foreach (var g in games)
                        {
                            string id = g.Id.ToString();
                            long was;
                            if (!snap.Totals.TryGetValue(id, out was)) continue;   // new to the library: its old time isn't new play
                            long recorded = sessions.Where(x => x.GameId == id && x.Start >= snap.At.AddMinutes(-5)).Sum(x => x.Seconds);
                            long delta = (long)g.Playtime - was - recorded;
                            // what's counted so far: Playnite's total may still be catching up with it (Steam's time synced in later)
                            accounted[id] = was + recorded + (delta >= 120 ? delta : 0);
                            if (delta < 120) continue;
                            DateTime when = g.LastActivity.HasValue && g.LastActivity.Value > snap.At ? g.LastActivity.Value : now;
                            if (when.Kind == DateTimeKind.Utc) when = when.ToLocalTime();
                            sessions.Add(new PlaySession { GameId = id, Name = g.Name, Start = when, Seconds = delta, Estimated = true });
                            changed = true;
                        }
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(SessionsFile));
                    if (changed)
                    {
                        var cutoff = now.AddYears(-3);
                        sessions = sessions.Where(s => s.Start >= cutoff).ToList();
                        string tmp = SessionsFile + ".tmp";
                        File.WriteAllText(tmp, js.Serialize(sessions));
                        if (File.Exists(SessionsFile)) File.Replace(tmp, SessionsFile, null); else File.Move(tmp, SessionsFile);
                    }
                    var next = new PlaytimeSnapshot { At = now };
                    foreach (var g in games)
                    {
                        // first time: Steam's own total too, so Playnite catching up with it later isn't counted as new play
                        long steamTotal, counted;
                        string id = g.Id.ToString();
                        long total = (long)g.Playtime;
                        if (steamTotals.TryGetValue(id, out steamTotal)) total = Math.Max(total, steamTotal);
                        if (accounted.TryGetValue(id, out counted)) total = Math.Max(total, counted);
                        next.Totals[id] = total;
                    }
                    File.WriteAllText(SnapshotFile, js.Serialize(next));
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: catching up play time: " + ex.Message); }
        }

        /// <summary>Steam keeps, per game, the minutes played in the last two weeks and when it was last played.</summary>
        private List<PlaySession> SteamRecentSessions(List<Playnite.SDK.Models.Game> games, Dictionary<string, long> totals)
        {
            var list = new List<PlaySession>();
            try
            {
                string steam = null;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    steam = k != null ? Convert.ToString(k.GetValue("SteamPath")) : null;
                if (string.IsNullOrEmpty(steam)) return list;
                string userdata = Path.Combine(steam.Replace('/', '\\'), "userdata");
                if (!Directory.Exists(userdata)) return list;
                // the account used most recently
                var cfg = Directory.GetDirectories(userdata).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
                                   .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (cfg == null) return list;
                var root = Vdf(File.ReadAllText(cfg, Encoding.UTF8));
                var apps = VdfPath(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                if (apps == null) return list;
                var bySteamId = games.Where(g => g.PluginId == SteamPluginId && !string.IsNullOrEmpty(g.GameId))
                                     .GroupBy(g => g.GameId).ToDictionary(x => x.Key, x => x.First());
                var since = DateTime.Now.AddDays(-14);
                foreach (var kv in apps)
                {
                    var app = kv.Value as Dictionary<string, object>;
                    Playnite.SDK.Models.Game g;
                    if (app == null || !bySteamId.TryGetValue(kv.Key, out g)) continue;
                    long all = VdfLong(app, "Playtime");
                    if (all > 0) totals[g.Id.ToString()] = all * 60;
                    long minutes = VdfLong(app, "Playtime2wks");
                    if (minutes <= 0) continue;
                    long last = VdfLong(app, "LastPlayed");
                    DateTime when = last > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(last).ToLocalTime()
                                             : g.LastActivity ?? DateTime.Now;
                    if (when.Kind == DateTimeKind.Utc) when = when.ToLocalTime();
                    if (when < since) continue;
                    list.Add(new PlaySession { GameId = g.Id.ToString(), Name = g.Name, Start = when, Seconds = minutes * 60, Estimated = true });
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: Steam recent play time: " + ex.Message); }
            return list;
        }

        // ---------------- Valve's text format: "key" "value" and "key" { ... } ----------------

        private static Dictionary<string, object> Vdf(string text)
        {
            int i = 0;
            return VdfBlock(text, ref i);
        }

        private static Dictionary<string, object> VdfBlock(string t, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string key = null;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '"')
                {
                    string s = VdfString(t, ref i);
                    if (key == null) key = s;
                    else { d[key] = s; key = null; }
                }
                else if (c == '{')
                {
                    i++;
                    var child = VdfBlock(t, ref i);
                    if (key != null) { d[key] = child; key = null; }
                }
                else if (c == '}') { i++; return d; }
                else if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
                else i++;
            }
            return d;
        }

        private static string VdfString(string t, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // the opening quote
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length) { i++; sb.Append(t[i] == 'n' ? '\n' : t[i] == 't' ? '\t' : t[i]); }
                else sb.Append(t[i]);
                i++;
            }
            i++;   // the closing quote
            return sb.ToString();
        }

        private static Dictionary<string, object> VdfPath(Dictionary<string, object> d, params string[] path)
        {
            foreach (string p in path)
            {
                object o;
                if (d == null || !d.TryGetValue(p, out o)) return null;
                d = o as Dictionary<string, object>;
            }
            return d;
        }

        private static long VdfLong(Dictionary<string, object> d, string key)
        {
            object o;
            long v;
            return d.TryGetValue(key, out o) && long.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}

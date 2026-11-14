using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KippixShared
{
    /// <summary>A Steam download's progress as Steam's own content log tells it.</summary>
    internal class SteamLogProgress
    {
        public string AppId;
        public long Downloaded;       // bytes downloaded so far (as Steam's "download" figure)
        public long ToDownload;
        public double BytesPerSecond; // Steam's own "current download rate" (0 when not recent)
        public bool Active;           // still downloading (no stop / cancel since it started)
    }

    /// <summary>
    /// (0.99.72) Live Steam download progress from logs\content_log.txt. Steam only saves progress to the appmanifest when a
    /// download starts or stops, so a long download looked stuck at whatever it had when it started (e.g. 297 MB of a game
    /// already 63 GB in). The log has what's needed: "update started : download X/Y" when it (re)starts, then every ~5.5 minutes
    /// "stats:" lines with the bytes each server sent in that window (an "(Invalid, 0)" line is their total), and every minute
    /// "Current download rate: N Mbps". Progress = X + the windows since + the rate since the last window. No state is kept,
    /// so it's right after Playnite restarts and when the download was started outside Playnite.
    /// </summary>
    internal static class SteamContentLog
    {
        private static readonly Regex LineRx = new Regex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\] (.*)$", RegexOptions.Compiled);
        private static readonly Regex StartedRx = new Regex(@"^AppID (\d+) update started : download (\d+)/(\d+)", RegexOptions.Compiled);
        private static readonly Regex StatsRx = new Regex(@"^stats: \((\w+), \d+\)[^:]*: (\d+) Bytes, (\d+) sec", RegexOptions.Compiled);
        private static readonly Regex RateRx = new Regex(@"^Current download rate: ([\d.]+) Mbps", RegexOptions.Compiled);
        private static readonly Regex StoppedRx = new Regex(@"^AppID (\d+) (update canceled|App update changed : None|finished update)", RegexOptions.Compiled);

        private class AppRun
        {
            public long Base, Total;
            public DateTime Started, LastCounted;
            public double Counted;
            public bool Active;
        }

        private static readonly object gate = new object();
        private static string cachePath;
        private static long cacheLen = -1;
        private static DateTime cacheTime;
        private static Dictionary<string, AppRun> cacheRuns = new Dictionary<string, AppRun>();
        private static double cacheRate;
        private static DateTime cacheRateAt = DateTime.MinValue;

        /// <summary>The download in progress (or last stopped) per app id. Empty when there's no log.</summary>
        public static Dictionary<string, SteamLogProgress> Read(string logPath)
        {
            var result = new Dictionary<string, SteamLogProgress>();
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(logPath);
                    if (!fi.Exists) return result;
                    if (cachePath != logPath || fi.Length != cacheLen || fi.LastWriteTimeUtc != cacheTime)
                    {
                        Parse(ReadTail(logPath, 2 * 1024 * 1024));
                        cachePath = logPath; cacheLen = fi.Length; cacheTime = fi.LastWriteTimeUtc;
                    }
                }
                catch (Exception) { return result; }

                DateTime now = DateTime.Now;   // (the log's times are local)
                bool rateFresh = (now - cacheRateAt).TotalSeconds < 150;
                foreach (var kv in cacheRuns)
                {
                    AppRun r = kv.Value;
                    double done = r.Base + r.Counted;
                    double bps = 0;
                    if (r.Active && rateFresh)
                    {
                        bps = cacheRate;
                        double since = (now - (r.LastCounted > r.Started ? r.LastCounted : r.Started)).TotalSeconds;
                        if (since > 0) done += bps * Math.Min(since, 360);
                    }
                    result[kv.Key] = new SteamLogProgress
                    {
                        AppId = kv.Key,
                        Downloaded = (long)Math.Min(r.Total, Math.Max(r.Base, done)),
                        ToDownload = r.Total,
                        BytesPerSecond = bps,
                        Active = r.Active
                    };
                }
            }
            return result;
        }

        private static string ReadTail(string path, int bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - bytes);
                fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs, Encoding.UTF8)) return sr.ReadToEnd();
            }
        }

        private static void Parse(string text)
        {
            var runs = new Dictionary<string, AppRun>();
            AppRun current = null;
            double rate = 0;
            DateTime rateAt = DateTime.MinValue;

            // one window's stats lines share a time stamp: their total (the "(Invalid, 0)" line), or the sum when it's missing
            DateTime batchAt = DateTime.MinValue;
            double batchSum = 0, batchTotal = -1;
            int batchSecs = 0, batchTotalSecs = 0;
            Action flush = () =>
            {
                if (batchAt == DateTime.MinValue) return;
                if (current != null)
                {
                    double bytes = batchTotal >= 0 ? batchTotal : batchSum;
                    int secs = batchTotal >= 0 ? batchTotalSecs : batchSecs;
                    // the first window after a start also holds what came before it (another game, or before a pause): only its share
                    double inRun = (batchAt - current.Started).TotalSeconds;
                    if (secs > 0 && inRun < secs) bytes *= Math.Max(0, inRun) / secs;
                    current.Counted += bytes;
                    current.LastCounted = batchAt;
                }
                batchAt = DateTime.MinValue; batchSum = 0; batchTotal = -1; batchSecs = 0; batchTotalSecs = 0;
            };

            foreach (string raw in text.Split('\n'))
            {
                Match lm = LineRx.Match(raw.TrimEnd('\r'));
                if (!lm.Success) continue;
                DateTime at;
                if (!DateTime.TryParseExact(lm.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) continue;
                string body = lm.Groups[2].Value;
                Match m;
                if ((m = StatsRx.Match(body)).Success)
                {
                    if (batchAt != at) flush();
                    batchAt = at;
                    long n = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    int secs = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (m.Groups[1].Value == "Invalid") { batchTotal = n; batchTotalSecs = secs; }
                    else { batchSum += n; batchSecs = Math.Max(batchSecs, secs); }
                    continue;
                }
                flush();
                if ((m = StartedRx.Match(body)).Success)
                {
                    current = new AppRun
                    {
                        Base = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        Total = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                        Started = at,
                        Active = true
                    };
                    runs[m.Groups[1].Value] = current;
                }
                else if ((m = StoppedRx.Match(body)).Success)
                {
                    AppRun r;
                    if (runs.TryGetValue(m.Groups[1].Value, out r))
                    {
                        r.Active = false;
                        if (r == current) current = null;
                    }
                }
                else if ((m = RateRx.Match(body)).Success)
                {
                    double mbps;
                    if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out mbps)) { rate = mbps * 1000000 / 8; rateAt = at; }
                }
            }
            flush();
            cacheRuns = runs;
            cacheRate = rate;
            cacheRateAt = rateAt;
        }
    }
}

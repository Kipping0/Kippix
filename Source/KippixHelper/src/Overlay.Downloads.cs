using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KippixOverlay
{
    internal class DownloadInfo
    {
        public string Key;
        public string Name;
        public string Source;      // "Steam", "Epic", "Playnite"
        public string State;       // "Downloading", "Paused", "Queued", ...
        public long Done;
        public long Total;
        public double Progress = -1;   // 0..1, or -1 when unknown
        public bool Estimated;         // live figures worked out from Steam's disk writes
        public double BytesPerSecond;  // > 0 while downloading

        public string Text()
        {
            string s = State;
            if (Progress >= 0) s += "  \u00B7  " + (Estimated ? "about " : "") + Math.Floor(Progress * 100) + "%";
            if (Total > 0) s += "  \u00B7  " + Size(Done) + " of " + Size(Total);
            if (BytesPerSecond > 512 * 1024) s += "  \u00B7  " + Math.Round(BytesPerSecond / 1048576.0) + " MB/s";
            return s + "  \u00B7  " + Source;
        }

        public static string Size(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return Math.Max(0, Math.Round(bytes / 1048576.0)) + " MB";
        }
    }

    /// <summary>Game downloads and updates in progress, read from the launchers' own files. Cheap enough to call every few seconds.</summary>
    internal static class Downloads
    {
        private static List<DownloadInfo> cache = new List<DownloadInfo>();
        private static DateTime cacheAt = DateTime.MinValue;
        private static readonly object cacheLock = new object();

        /// <summary>Set by the plugin while a game is running (Steam suspends downloads during play by default).</summary>
        public static volatile bool GameRunning;

        public static List<DownloadInfo> Get(IEnumerable<Playnite.SDK.Models.Game> library)
        {
            // One refresh at a time (the plugin samples in the background and the Settings page reads too).
            lock (cacheLock)
            {
                if ((DateTime.UtcNow - cacheAt).TotalSeconds < 2.5) return cache;
                var list = new List<DownloadInfo>();
                try { list.AddRange(Steam(SteamAchievements.SteamPath())); } catch (Exception) { }
                try { list.AddRange(Epic()); } catch (Exception) { }
                try
                {
                    if (library != null)
                    {
                        foreach (var g in library)
                        {
                            if (g == null || !g.IsInstalling) continue;
                            if (list.Any(d => string.Equals(d.Name, g.Name, StringComparison.OrdinalIgnoreCase))) continue;
                            list.Add(new DownloadInfo { Key = "pn:" + g.Id, Name = g.Name, Source = "Playnite", State = "Installing" });
                        }
                    }
                }
                catch (Exception) { }
                cache = list;
                cacheAt = DateTime.UtcNow;
                return list;
            }
        }

        // ---------------- Steam ----------------

        // appmanifest StateFlags
        private const long UpdateRequired = 2, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024,
            Validating = 131072, AddingFiles = 262144, Preallocating = 524288, Downloading = 1048576,
            Staging = 2097152, Committing = 4194304;

        public static List<DownloadInfo> Steam(string steamPath)
        {
            var result = new List<DownloadInfo>();
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) return result;
            ulong writes = SteamDiskWrites();
            UpdateRate(writes);
            Dictionary<string, string> logState = null;
            try { logState = SteamLogStates(Path.Combine(steamPath, @"logs\content_log.txt")); } catch (Exception) { }
            // (0.99.72) live progress from the same log (the manifest's figure is only saved when a download starts or stops)
            Dictionary<string, KippixShared.SteamLogProgress> logProgress = null;
            try { logProgress = KippixShared.SteamContentLog.Read(Path.Combine(steamPath, @"logs\content_log.txt")); } catch (Exception) { }

            var libraries = new List<string> { Path.Combine(steamPath, "steamapps") };
            string vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string lib = Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps");
                    if (!libraries.Any(x => string.Equals(x.TrimEnd('\\'), lib.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libraries.Add(lib);
                }
            }

            foreach (string lib in libraries)
            {
                string[] files;
                try { files = Directory.Exists(lib) ? Directory.GetFiles(lib, "appmanifest_*.acf") : new string[0]; }
                catch (Exception) { continue; }
                foreach (string f in files)
                {
                    DownloadInfo d = null;
                    long staged = 0, toStage = 0, dl = 0, toDl = 0;
                    bool running = false;
                    try { d = ParseSteamManifest(File.ReadAllText(f), out running, out dl, out toDl, out staged, out toStage); } catch (Exception) { }
                    if (d == null) continue;

                    // The manifest's flags are only saved when a download starts or stops, so they can't tell a
                    // running download from a suspended one. Steam's content log can.
                    string st;
                    string appId = d.Key.Substring("steam:".Length);
                    if (logState != null && logState.TryGetValue(appId, out st))
                    {
                        bool installed = d.State == "Updating";
                        switch (st)
                        {
                            case "downloading": running = true; d.State = installed ? "Updating" : "Downloading"; break;
                            case "installing": running = true; d.State = "Installing"; break;
                            case "verifying": running = true; d.State = "Verifying"; break;
                            case "suspended": running = false; d.State = "Paused"; break;
                            case "paused": running = false; d.State = "Paused"; break;
                            case "finished": continue;
                        }
                    }
                    KippixShared.SteamLogProgress lp;
                    if (running && logProgress != null && logProgress.TryGetValue(appId, out lp) && lp.Active && lp.ToDownload > 0 && (toDl <= 0 || lp.ToDownload == toDl || lp.Downloaded >= dl))
                    {
                        d.Total = lp.ToDownload;
                        d.Done = lp.Downloaded;
                        d.Progress = Math.Min(1, (double)lp.Downloaded / lp.ToDownload);
                        d.Estimated = true;
                        d.BytesPerSecond = lp.BytesPerSecond > 0 ? lp.BytesPerSecond : rate;
                    }
                    else if (running) Estimate(d, f, writes, dl, toDl, staged, toStage);
                    result.Add(d);
                }
            }
            return result;
        }

        public static DownloadInfo ParseSteamManifest(string text)
        {
            bool running;
            long a, b, c, e;
            return ParseSteamManifest(text, out running, out a, out b, out c, out e);
        }

        /// <summary>Null when the app is installed and nothing is pending.</summary>
        public static DownloadInfo ParseSteamManifest(string text, out bool running, out long dlOut, out long toDlOut, out long stagedOut, out long toStageOut)
        {
            var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(text, "^\\s*\"(\\w+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline))
                if (!v.ContainsKey(m.Groups[1].Value)) v[m.Groups[1].Value] = m.Groups[2].Value;

            long flags = Num(v, "StateFlags");
            long toDl = Num(v, "BytesToDownload"), dl = Num(v, "BytesDownloaded");
            long toStage = Num(v, "BytesToStage"), staged = Num(v, "BytesStaged");

            dlOut = dl; toDlOut = toDl; stagedOut = staged; toStageOut = toStage;

            // "Update Started" without "Update Running" means Steam stopped it part-way: usually because a game
            // started (Steam suspends downloads during play unless "Allow downloads during gameplay" is on).
            running = (flags & (UpdateRunning | Validating | AddingFiles | Preallocating | Downloading | Staging | Committing)) != 0;
            bool started = (flags & UpdateStarted) != 0;
            bool paused = (flags & UpdatePaused) != 0;
            bool pending = toDl > 0 && dl < toDl;
            bool active = running;
            if (!running && !started && !paused && !((flags & UpdateRequired) != 0 && pending)) return null;

            var d = new DownloadInfo { Source = "Steam" };
            string id;
            v.TryGetValue("appid", out id);
            d.Key = "steam:" + id;
            string name;
            d.Name = v.TryGetValue("name", out name) && name.Length > 0 ? name : "Steam app " + id;

            if (paused) d.State = "Paused";
            else if (!running && started) d.State = "Paused";
            else if ((flags & Validating) != 0) d.State = "Verifying";
            else if ((flags & (Staging | Committing)) != 0 && !pending) d.State = "Installing";
            else if (active) d.State = Num(v, "SizeOnDisk") == 0 || (flags & 4) == 0 ? "Downloading" : "Updating";
            else d.State = "Queued";

            if (toDl > 0) { d.Done = dl; d.Total = toDl; d.Progress = Math.Min(1, (double)dl / toDl); }
            else if (toStage > 0) { d.Done = staged; d.Total = toStage; d.Progress = Math.Min(1, (double)staged / toStage); }
            return d;
        }

        private static long Num(Dictionary<string, string> v, string key)
        {
            string s;
            long n;
            return v.TryGetValue(key, out s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // ---------------- Steam content log ----------------

        private static readonly Regex LogUpdateChanged = new Regex(@"AppID (\d+) App update changed : (.*)$", RegexOptions.Compiled);
        private static readonly Regex LogCanceled = new Regex(@"AppID (\d+) update canceled : (.*)$", RegexOptions.Compiled);
        private static readonly Regex LogStarted = new Regex(@"AppID (\d+) update started", RegexOptions.Compiled);
        private static readonly Regex LogFinished = new Regex(@"AppID (\d+) (update finished|finished update|state changed : Fully Installed,\s*$)", RegexOptions.Compiled);

        /// <summary>The latest download state per app id from the end of Steam's content_log.txt.</summary>
        public static Dictionary<string, string> SteamLogStates(string logPath)
        {
            var result = new Dictionary<string, string>();
            if (!File.Exists(logPath)) return result;
            string text;
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 256 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            }
            ParseLog(text, result);
            return result;
        }

        public static void ParseLog(string text, Dictionary<string, string> result)
        {
            var canceledReason = new Dictionary<string, string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                Match m;
                if ((m = LogCanceled.Match(line)).Success)
                {
                    string why = m.Groups[2].Value;
                    canceledReason[m.Groups[1].Value] = why.IndexOf("Suspended", StringComparison.OrdinalIgnoreCase) >= 0 ? "suspended" : "paused";
                    result[m.Groups[1].Value] = canceledReason[m.Groups[1].Value];
                }
                else if ((m = LogUpdateChanged.Match(line)).Success)
                {
                    string id = m.Groups[1].Value, what = m.Groups[2].Value;
                    if (what.Trim() == "None")
                    {
                        string why;
                        result[id] = canceledReason.TryGetValue(id, out why) ? why : "finished";
                    }
                    else if (what.Contains("Stopping")) { /* winding down; the cancel line before it says why */ }
                    else if (what.Contains("Committing")) result[id] = "installing";
                    else if (what.Contains("Verifying")) result[id] = "verifying";
                    else if (what.Contains("Downloading") || what.Contains("Staging")) { result[id] = "downloading"; canceledReason.Remove(id); }
                }
                else if ((m = LogStarted.Match(line)).Success)
                {
                    result[m.Groups[1].Value] = "downloading";
                    canceledReason.Remove(m.Groups[1].Value);
                }
                else if ((m = LogFinished.Match(line)).Success)
                {
                    result[m.Groups[1].Value] = "finished";
                    canceledReason.Remove(m.Groups[1].Value);
                }
            }
        }

        // ---------------- Live estimate ----------------
        // Steam only saves progress to the manifest when a download starts, pauses or finishes. While it runs,
        // the bytes Steam writes to disk track the staged data closely, so progress = saved figure + writes since.

        private class Track
        {
            public DateTime ManifestTime;
            public ulong BaseWrites;
            public long BaseStaged, BaseDl;
        }

        private static readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();
        private static ulong rateWrites;
        private static DateTime rateAt = DateTime.MinValue;
        private static double rate;

        private static void Estimate(DownloadInfo d, string manifest, ulong writes, long dl, long toDl, long staged, long toStage)
        {
            d.BytesPerSecond = rate;
            if (writes == 0) return;
            DateTime mtime = File.GetLastWriteTimeUtc(manifest);
            Track t;
            lock (tracks)
            {
                if (!tracks.TryGetValue(d.Key, out t) || t.ManifestTime != mtime || writes < t.BaseWrites)
                {
                    t = new Track { ManifestTime = mtime, BaseWrites = writes, BaseStaged = staged, BaseDl = dl };
                    tracks[d.Key] = t;
                }
            }
            long extra = (long)(writes - t.BaseWrites);
            double fraction;
            if (toStage > 0) fraction = Math.Min(1.0, (double)(t.BaseStaged + extra) / toStage);
            else if (toDl > 0) fraction = Math.Min(1.0, (double)(t.BaseDl + extra) / toDl);
            else return;
            d.Progress = fraction;
            d.Estimated = extra > 0;
            if (toDl > 0) { d.Total = toDl; d.Done = (long)(fraction * toDl); }
        }

        private static void UpdateRate(ulong writes)
        {
            DateTime now = DateTime.UtcNow;
            double dt = (now - rateAt).TotalSeconds;
            if (rateAt != DateTime.MinValue && writes >= rateWrites && dt > 0.5 && dt < 30)
            {
                double r = (writes - rateWrites) / dt;
                rate = rate <= 0 ? r : rate * 0.5 + r * 0.5;
            }
            else if (dt >= 30) rate = 0;
            rateWrites = writes;
            rateAt = now;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Total bytes Steam's processes have written to disk since they started.</summary>
        private static ulong SteamDiskWrites()
        {
            ulong total = 0;
            foreach (string name in new[] { "steam", "steamservice" })
            {
                System.Diagnostics.Process[] procs;
                try { procs = System.Diagnostics.Process.GetProcessesByName(name); } catch (Exception) { continue; }
                foreach (var p in procs)
                {
                    int id = p.Id;
                    p.Dispose();   // (sampled every few seconds: don't leave the Process objects to the finaliser)
                    IntPtr h = OpenProcess(0x1000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        IoCounters c;
                        if (GetProcessIoCounters(h, out c)) total += c.WriteTransferCount;
                    }
                    finally { CloseHandle(h); }
                }
            }
            return total;
        }

        // ---------------- Epic ----------------

        /// <summary>Epic only marks unfinished installs (no percentage), so these show as "Not finished".</summary>
        private static List<DownloadInfo> Epic()
        {
            var result = new List<DownloadInfo>();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return result;
            var json = new JavaScriptSerializer();
            foreach (string f in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    var d = json.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (d == null) continue;
                    object inc;
                    if (!d.TryGetValue("bIsIncompleteInstall", out inc) || !(inc is bool) || !(bool)inc) continue;
                    object name;
                    d.TryGetValue("DisplayName", out name);
                    result.Add(new DownloadInfo
                    {
                        Key = "epic:" + Path.GetFileNameWithoutExtension(f),
                        Name = Convert.ToString(name) ?? "Epic game",
                        Source = "Epic",
                        State = "Not finished"
                    });
                }
                catch (Exception) { }
            }
            return result;
        }
    }
}

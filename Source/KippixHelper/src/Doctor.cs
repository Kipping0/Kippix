using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace KippixHelper
{
    /// <summary>One line of Kippix Doctor's health check.</summary>
    internal class DoctorCheck
    {
        public string Title, Detail;
        public string Level = "ok";   // ok, info, warn, bad
        public string Key;
    }

    /// <summary>
    /// Kippix Doctor (fullscreen: Settings › Help › Kippix Doctor; desktop: the helper's settings › Doctor):
    ///   - health checks: Playnite's version, theme and helper versions match, old or clashing add-ons (only reported, never
    ///     removed), the optional add-ons Kippix uses and what each adds, the settings file, disk space, and the errors in
    ///     Playnite's logs over the last hour (counted by message);
    ///   - a bug report: Playnite's two logs (their last 2 MB), Kippix's settings with every key, token, secret and personal
    ///     thing taken out, and the versions, zipped into Documents › Kippix Logs › Kippix-report-(date).zip.
    /// Everything here runs off the UI thread, only when asked.
    /// </summary>
    internal static class KipDoctor
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // add-ons by Id / assembly name
        private static readonly Guid SuccessStoryId = new Guid("cebe6d32-8c46-4459-b993-5a5189d60788");
        private static readonly Guid HltbId = new Guid("e08cd51f-9c9a-4ee3-a094-fde03b55492f");
        private static readonly Guid LudusaviIdD = new Guid("72e2de43-d859-44d8-914e-4277741c8208");
        private static readonly Guid EmlId = new Guid("705fdbca-e1fc-4004-b839-1d040b8b4429");

        internal class AddonInfo
        {
            public string Name, Version, Assembly, Folder;
            public Guid Id;
        }

        /// <summary>Every loaded plugin with its name and version (from its extension.yaml).</summary>
        internal static List<AddonInfo> Addons(IPlayniteAPI api)
        {
            var list = new List<AddonInfo>();
            try
            {
                foreach (var p in api.Addons.Plugins.ToList())
                {
                    if (p == null) continue;
                    var a = new AddonInfo { Id = p.Id };
                    try
                    {
                        var asm = p.GetType().Assembly;
                        a.Assembly = asm.GetName().Name;
                        a.Folder = Path.GetDirectoryName(asm.Location);
                        string yaml = Path.Combine(a.Folder ?? "", "extension.yaml");
                        if (File.Exists(yaml))
                        {
                            string t = File.ReadAllText(yaml);
                            var m = Regex.Match(t, @"(?m)^Name:\s*(.+?)\s*$");
                            if (m.Success) a.Name = m.Groups[1].Value.Trim('\'', '"');
                            m = Regex.Match(t, @"(?m)^Version:\s*(.+?)\s*$");
                            if (m.Success) a.Version = m.Groups[1].Value.Trim('\'', '"');
                        }
                    }
                    catch (Exception) { }
                    if (string.IsNullOrEmpty(a.Name)) a.Name = a.Assembly ?? p.GetType().Name;
                    list.Add(a);
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: Doctor: add-ons: " + ex.Message); }
            return list;
        }

        private static bool Has(List<AddonInfo> addons, Guid id, params string[] names)
        {
            return addons.Any(a => (id != Guid.Empty && a.Id == id)
                                   || names.Any(n => string.Equals(a.Assembly, n, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>The health check. Worker thread.</summary>
        internal static List<DoctorCheck> Run(IPlayniteAPI api, string themeVersion, string helperVersion, string settingsPath, bool oldOverlay)
        {
            var list = new List<DoctorCheck>();
            Func<string, string, string, string, DoctorCheck> add = (key, level, title, detail) =>
            {
                var c = new DoctorCheck { Key = key, Level = level, Title = title, Detail = detail };
                list.Add(c);
                return c;
            };

            // Playnite
            try
            {
                var v = api.ApplicationInfo.ApplicationVersion;
                if (v.Major >= 10) add("playnite", "ok", "Playnite " + v, "Kippix is made for Playnite 10.");
                else add("playnite", "bad", "Playnite " + v, "Kippix needs Playnite 10 or newer: update Playnite (Main menu › Help › Check for updates).");
            }
            catch (Exception) { add("playnite", "info", "Playnite", "Its version couldn't be read."); }

            // theme and helper
            if (string.IsNullOrEmpty(themeVersion) || string.IsNullOrEmpty(helperVersion))
                add("versions", "warn", "Theme " + (themeVersion ?? "?") + " · Helper " + (helperVersion ?? "?"), "One of the two couldn't be found. Install Kippix again (the theme and Kippix Helper together).");
            else if (themeVersion == helperVersion)
                add("versions", "ok", "Theme " + themeVersion + " · Helper " + helperVersion, "The theme and Kippix Helper are the same version.");
            else
                add("versions", "warn", "Theme " + themeVersion + " · Helper " + helperVersion, "They don't match: install both from the same Kippix release, then restart Playnite.");

            // the fullscreen theme in use
            try
            {
                string fc = Path.Combine(api.Paths.ConfigurationPath, "fullscreenConfig.json");
                if (File.Exists(fc))
                {
                    var m = Regex.Match(File.ReadAllText(fc), "\"Theme\"\\s*:\\s*\"([^\"]*)\"");
                    if (m.Success && m.Groups[1].Value.StartsWith("Kippix", StringComparison.OrdinalIgnoreCase)) add("theme", "ok", "Fullscreen theme: Kippix", null);
                    else if (m.Success) add("theme", "info", "Fullscreen theme: " + m.Groups[1].Value, "Kippix isn't the fullscreen theme right now (Playnite's settings › Appearance in fullscreen mode).");
                }
            }
            catch (Exception) { }

            // old and clashing add-ons: only said, never removed
            var addons = Addons(api);
            if (oldOverlay || addons.Any(a => a.Id == KippixOverlay.OverlayPlugin.PluginId && !string.Equals(a.Assembly, "KippixHelper", StringComparison.OrdinalIgnoreCase)))
                add("old.overlay", "warn", "BananaBreadOverlay is still installed", "The overlay is part of Kippix Helper now, so the built-in one stays off while the old add-on is there. You can uninstall BananaBreadOverlay (Add-ons › Extensions).");
            foreach (var a in addons.Where(a => string.Equals(a.Assembly, "KippixSteamController", StringComparison.OrdinalIgnoreCase) || (a.Name ?? "").IndexOf("KippixSteamController", StringComparison.OrdinalIgnoreCase) >= 0))
                add("old.bbsc", "warn", a.Name + " is installed", "Kippix looks after the Steam Controller itself now. With both, presses can be handled twice; you can turn it off in Add-ons › Extensions.");
            try
            {
                string ext = Path.Combine(api.Paths.ConfigurationPath, "Extensions");
                if (Directory.Exists(ext))
                    foreach (var d in Directory.GetDirectories(ext).Where(d => (Path.GetFileName(d).StartsWith("BananaBreadFSHelper", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d).StartsWith("Kip" + "ScreenHelper", StringComparison.OrdinalIgnoreCase))))
                        add("old.helper", "warn", "An old Kippix Helper folder", Path.GetFileName(d) + " is still in Playnite's Extensions folder. Kippix Helper replaced it; it can be moved out.");
            }
            catch (Exception) { }
            foreach (var a in addons.Where(a => (a.Name ?? "").IndexOf("Aniki", StringComparison.OrdinalIgnoreCase) >= 0))
                add("other.aniki", "info", a.Name + (a.Version != null ? " " + a.Version : ""), "A helper for the Aniki fullscreen themes. Kippix doesn't use it; if menus or the controller act strangely in Kippix, try turning it off (Add-ons › Extensions).");
            foreach (var a in addons.Where(a => (a.Name ?? "").IndexOf("Aniki", StringComparison.OrdinalIgnoreCase) < 0
                                               && ((a.Name ?? "").IndexOf("Fullscreen Helper", StringComparison.OrdinalIgnoreCase) >= 0
                                                   || string.Equals(a.Name, "ThemeExtras", StringComparison.OrdinalIgnoreCase)
                                                   || (a.Name ?? "").IndexOf("Theme Modifier", StringComparison.OrdinalIgnoreCase) >= 0
                                                   || (a.Name ?? "").IndexOf("Fade Animations", StringComparison.OrdinalIgnoreCase) >= 0)))
                add("other." + a.Assembly, "info", a.Name + (a.Version != null ? " " + a.Version : ""), "Made for other fullscreen themes; Kippix doesn't need it. Harmless, but if something looks wrong, try turning it off.");

            // optional add-ons the theme uses
            bool ach = Has(addons, SuccessStoryId, "SuccessStory") || Has(addons, Guid.Empty, "PlayniteAchievements", "Playnite Achievements");
            add("opt.ach", ach ? "ok" : "info", ach ? "Achievements: installed" : "Achievements: not installed",
                "SuccessStory or Playnite Achievements: achievements on the cards, the game page, the profile and the in-game overlay.");
            bool hltb = Has(addons, HltbId, "HowLongToBeat");
            add("opt.hltb", hltb ? "ok" : "info", hltb ? "HowLongToBeat: installed" : "HowLongToBeat: not installed", "How long a game takes to beat, on the game page, the cards and the overlay.");
            bool ludu = Has(addons, LudusaviIdD, "LudusaviPlaynite", "Ludusavi");
            add("opt.ludusavi", ludu ? "ok" : "info", ludu ? "Ludusavi: installed" : "Ludusavi: not installed", "Save backups: the game page's Save backups card, and backups before or after playing.");
            bool ups = Has(addons, new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890"), "UniPlaySong");
            add("opt.ups", ups ? "ok" : "info", ups ? "UniPlaySong: installed" : "UniPlaySong: not installed", "Each game's own music on the home screen and game page (Settings › Audio › Music › UniPlaySong).");
            bool bgc = Has(addons, new Guid("3afdd02b-db6c-4b60-8faa-2971d6dfad2a"), "BackgroundChanger");
            add("opt.bgc", bgc ? "ok" : "info", bgc ? "BackgroundChanger: installed" : "BackgroundChanger: not installed", "More backgrounds and covers per game, videos too (Settings › Home settings › Background › BackgroundChanger).");
            bool eml = Has(addons, EmlId, "ExtraMetadataLoader", "Extra Metadata Loader");
            add("opt.eml", eml ? "ok" : "info", eml ? "Extra Metadata Loader: installed" : "Extra Metadata Loader: not installed", "Game logos and the trailers it downloads (Kippix also finds Steam trailers itself).");

            // settings file
            try
            {
                if (!File.Exists(settingsPath)) add("settings", "info", "Settings file: not made yet", "It's made the first time a setting changes.");
                else
                {
                    var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<HelperSettings>(File.ReadAllText(settingsPath));
                    long size = new FileInfo(settingsPath).Length;
                    add("settings", s != null ? "ok" : "bad", s != null ? "Settings file: reads fine (" + Math.Max(1, size / 1024) + " KB)" : "Settings file: empty", null);
                }
                var copies = Directory.GetFiles(Path.GetDirectoryName(settingsPath), "settings.unreadable-*.json");
                if (copies.Length > 0) add("settings.copies", "info", "Unreadable settings kept: " + copies.Length, "Copies of a settings file that couldn't be read once are kept beside it (" + Path.GetFileName(copies.Last()) + ").");
            }
            catch (Exception ex) { add("settings", "bad", "Settings file: can't be read", ex.Message); }

            // disk space
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(api.Paths.ConfigurationPath));
                double gb = drive.AvailableFreeSpace / 1073741824.0;
                string t = "Free space on " + drive.Name.TrimEnd('\\') + ": " + gb.ToString(gb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " GB";
                if (gb < 0.5) add("disk", "bad", t, "Almost full: Playnite and Kippix can't save settings, pictures or trailers. Free some space.");
                else if (gb < 2) add("disk", "warn", t, "Getting low: trailers and pictures are cached on this drive.");
                else add("disk", "ok", t, null);
            }
            catch (Exception) { }

            // errors in the logs over the last hour
            try
            {
                var groups = new Dictionary<string, int>();
                int total = 0, ours = 0;
                foreach (string log in new[] { "extensions.log", "playnite.log" })
                    CountErrors(Path.Combine(api.Paths.ConfigurationPath, log), DateTime.Now.AddHours(-1), groups, ref total, ref ours);
                if (total == 0) add("log", "ok", "Log errors in the last hour: none", null);
                else
                {
                    add("log", ours > 0 ? "warn" : "info", "Log errors in the last hour: " + total + (ours > 0 ? " (" + ours + " from Kippix)" : ""),
                        ours > 0 ? "Make a bug report and send it with what you were doing." : "From other add-ons or Playnite itself; usually harmless (a store that needs signing in again, for example).");
                    foreach (var g in groups.OrderByDescending(g => g.Value).Take(5))
                        add("log.item", "info", g.Value + " ×  " + g.Key, null);
                }
            }
            catch (Exception ex) { add("log", "info", "Log errors: couldn't read the logs", ex.Message); }
            return list;
        }

        private static readonly Regex LogLine = new Regex(@"^(\d\d)-(\d\d) (\d\d):(\d\d):(\d\d)\.\d+\|(\w+)\s*\|([^:|]*):?(.*)$");

        private static void CountErrors(string file, DateTime since, Dictionary<string, int> groups, ref int total, ref int ours)
        {
            if (!File.Exists(file)) return;
            foreach (string line in TailLines(file, 4 * 1024 * 1024))
            {
                var m = LogLine.Match(line);
                if (!m.Success || m.Groups[6].Value != "ERROR") continue;
                DateTime at;
                try
                {
                    int day = int.Parse(m.Groups[1].Value), month = int.Parse(m.Groups[2].Value);
                    at = new DateTime(DateTime.Now.Year, month, day, int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value));
                    if (at > DateTime.Now.AddDays(1)) at = at.AddYears(-1);
                }
                catch (Exception) { continue; }
                if (at < since) continue;
                total++;
                string source = m.Groups[7].Value.Trim();
                string msg = m.Groups[8].Value.Trim();
                if (source.IndexOf("Kippix", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("Kip" + "Screen", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("BananaBread", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.StartsWith("Kippix", StringComparison.OrdinalIgnoreCase)) ours++;
                string key = (source.Length > 0 ? source + ": " : "") + Regex.Replace(msg, @"\d+", "#");
                if (key.Length > 110) key = key.Substring(0, 110) + "…";
                int n;
                groups.TryGetValue(key, out n);
                groups[key] = n + 1;
            }
        }

        /// <summary>The last <paramref name="maxBytes"/> of a text file as lines (it's open in Playnite: shared read).</summary>
        private static List<string> TailLines(string file, long maxBytes)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bool cut = fs.Length > maxBytes;
                if (cut) fs.Seek(-maxBytes, SeekOrigin.End);
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    if (cut) r.ReadLine();   // (a part line)
                    string l;
                    while ((l = r.ReadLine()) != null) lines.Add(l);
                }
            }
            return lines;
        }

        /// <summary>The end of a file (from a line start), at most <paramref name="maxBytes"/>.</summary>
        private static byte[] Tail(string file, long maxBytes)
        {
            if (!File.Exists(file)) return null;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int read = 0;
                while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                int skip = 0;
                if (start > 0) { int nl = Array.IndexOf(buf, (byte)'\n'); if (nl >= 0 && nl < read - 1) skip = nl + 1; }
                var outb = new byte[read - skip];
                Array.Copy(buf, skip, outb, 0, outb.Length);
                return outb;
            }
        }

        // ---------------- settings with nothing private in them ----------------

        private static readonly Regex SecretName = new Regex("key|secret|token|password|passwd|pin|cookie|auth|session|steamid|account|persona", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Personal = new HashSet<string>
        {
            "StoreWishlist", "StoreRecent", "WishDismissed", "WishRemoveFromSteam", "Profiles", "ProfileNameOverride", "ProfilePicturePath", "RecentSearches",
            "GalleryFolders", "GalleryFavourites", "ArtSwaps", "LastBackground", "HomeBgImage", "Collections", "UpNext", "PinnedGames", "SlotPins"
        };

        /// <summary>Kippix's settings for a bug report: every key, token, secret, PIN and personal thing replaced with "(removed)".</summary>
        internal static string CleanSettings(string json)
        {
            try
            {
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var d = js.DeserializeObject(json) as Dictionary<string, object>;
                if (d == null) return "{}";
                Scrub(d, 0);
                return js.Serialize(d);
            }
            catch (Exception) { return "{}"; }
        }

        private static void Scrub(Dictionary<string, object> d, int depth)
        {
            foreach (var k in d.Keys.ToList())
            {
                // (only text under a secret-like name: "PinBattery" is a switch, "ExtraRowKeys" a list of rows)
                object v = d[k];
                bool secretName = SecretName.IsMatch(k) && v is string && !k.StartsWith("Pin", StringComparison.Ordinal);
                if (secretName || Personal.Contains(k) || k.StartsWith("Weather", StringComparison.Ordinal) && !(v is bool))
                {
                    d[k] = "(removed)";
                    continue;
                }
                var sub = v as Dictionary<string, object>;
                if (sub != null && depth < 6) { Scrub(sub, depth + 1); continue; }
                var arr = v as ArrayList;
                if (arr == null) { var oa = v as object[]; if (oa != null) arr = new ArrayList(oa); }
                if (arr != null && depth < 6)
                    foreach (var item in arr) { var dd = item as Dictionary<string, object>; if (dd != null) Scrub(dd, depth + 1); }
                var s = v as string;
                if (s != null && (Regex.IsMatch(s, "^[A-Za-z0-9+/=_-]{32,}$") || s.IndexOf(":\\Users\\", StringComparison.OrdinalIgnoreCase) >= 0)) d[k] = "(removed)";   // anything that looks like a key, or a path with your user name
            }
        }

        // ---------------- the bug report ----------------

        /// <summary>Makes the zip and returns its path. Worker thread.</summary>
        internal static string MakeReport(IPlayniteAPI api, string versionsText, string settingsJson)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kippix Logs");
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "Kippix-report-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".zip");
            using (var z = new ZipMaker(zip))
            {
                z.Add("versions.txt", Encoding.UTF8.GetBytes(versionsText ?? ""));
                z.Add("Kippix settings (no keys).json", Encoding.UTF8.GetBytes(CleanSettings(settingsJson ?? "{}")));
                foreach (string log in new[] { "extensions.log", "playnite.log" })
                {
                    byte[] b = null;
                    try { b = Tail(Path.Combine(api.Paths.ConfigurationPath, log), 2 * 1024 * 1024); } catch (Exception ex) { b = Encoding.UTF8.GetBytes("(couldn't be read: " + ex.Message + ")"); }
                    if (b != null) z.Add(log, b);
                }
            }
            return zip;
        }

        internal static void ShowInExplorer(string file)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { logger.Warn("Kippix: Doctor: opening the folder: " + ex.Message); }
        }

        internal static string ChecksText(List<DoctorCheck> checks)
        {
            var sb = new StringBuilder();
            foreach (var c in checks ?? new List<DoctorCheck>())
                sb.AppendLine("[" + c.Level + "] " + c.Title + (string.IsNullOrEmpty(c.Detail) ? "" : " — " + c.Detail));
            return sb.ToString();
        }
    }

    /// <summary>A small zip file writer (deflate, UTF-8 names), so no extra assembly is needed.</summary>
    internal sealed class ZipMaker : IDisposable
    {
        private readonly FileStream fs;
        private readonly BinaryWriter w;
        private readonly List<Tuple<string, uint, int, int, long, ushort, ushort>> entries = new List<Tuple<string, uint, int, int, long, ushort, ushort>>();
        private static uint[] crcTable;

        public ZipMaker(string path)
        {
            fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            w = new BinaryWriter(fs);
        }

        public void Add(string name, byte[] data)
        {
            data = data ?? new byte[0];
            uint crc = Crc32(data);
            byte[] comp;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(data, 0, data.Length);
                comp = ms.ToArray();
            }
            var now = DateTime.Now;
            ushort time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            ushort date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            long offset = fs.Position;
            w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)8); w.Write(time); w.Write(date);
            w.Write(crc); w.Write(comp.Length); w.Write(data.Length); w.Write((ushort)nameBytes.Length); w.Write((ushort)0);
            w.Write(nameBytes);
            w.Write(comp);
            entries.Add(Tuple.Create(name, crc, comp.Length, data.Length, offset, time, date));
        }

        public void Dispose()
        {
            long cdStart = fs.Position;
            foreach (var e in entries)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(e.Item1);
                w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)8); w.Write(e.Item6); w.Write(e.Item7);
                w.Write(e.Item2); w.Write(e.Item3); w.Write(e.Item4); w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)e.Item5);
                w.Write(nameBytes);
            }
            long cdSize = fs.Position - cdStart;
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries.Count); w.Write((ushort)entries.Count);
            w.Write((uint)cdSize); w.Write((uint)cdStart); w.Write((ushort)0);
            w.Flush();
            w.Dispose();
        }

        private static uint Crc32(byte[] data)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }

    /// <summary>Kippix Doctor in the theme: safe mode (with the "starting" marker), the Doctor page, the bug report.</summary>
    public partial class ThemeApi
    {
        // ---------------- safe mode ----------------

        /// <summary>Safe mode is on: Kippix's extras stay off until it's turned off.</summary>
        public bool SafeModeOn { get { return settings.SafeMode; } }
        internal bool SafeModeOffer { get { return settings.SafeModeOffer; } set { if (settings.SafeModeOffer == value) return; settings.SafeModeOffer = value; SaveSettings(); } }

        /// <summary>What safe mode switches off (each put back when it's turned off, unless you changed it meanwhile).</summary>
        private Dictionary<string, object> SafeModePreset()
        {
            var d = new Dictionary<string, object>();
            d["WidgetsOn"] = false;
            d["HomeLayoutMode"] = 0;          // the plain row: no carousel, no folders, no board
            d["RowDesign"] = 0;
            d["ExtraRows"] = false;
            d["PreviewMode"] = 0;             // no trailer previews
            if (settings.HomeBgMode == 2) d["HomeBgMode"] = 0;   // the trailer background: the game's art
            d["BackgroundMotion"] = false;
            d["AmbientMode"] = false;
            d["SeasonalOn"] = false;
            d["WeatherFxOn"] = false;
            d["ReduceMotion"] = true;         // no animations
            d["DetTrailerAutoplay"] = false;
            d["StorePreview"] = false;
            return d;
        }

        /// <summary>Safe mode on / off (Help › Kippix Doctor, the desktop settings' Doctor tab, or the offer after failed starts).</summary>
        internal void SetSafeMode(bool on)
        {
            if (on == settings.SafeMode) return;
            if (settings.SafeModeSaved == null) settings.SafeModeSaved = new Dictionary<string, string>();
            if (on)
            {
                settings.SafeModeSaved.Clear();
                OverrideFields(SafeModePreset(), settings.SafeModeSaved);
            }
            else RestoreFields(settings.SafeModeSaved);
            settings.SafeMode = on;
            logger.Info("Kippix: safe mode " + (on ? "on" : "off"));
            SaveSettings();
            if (IsFullscreen && ui != null)
            {
                ApplyAllSettings();
                try { ApplyAccessibility(); } catch (Exception) { }
            }
            UpdateBatterySaving();
            Notify("SafeModeOn");
            Notify("SafeModeText");
        }

        public string SafeModeText
        {
            get { return settings.SafeMode ? Loc("LOC_KX_DocSafeOnShort", "Safe mode is on") : Loc("LOC_KX_DocSafeOffShort", "Health checks, safe mode and a bug report"); }
        }

        // the "starting" marker: written when fullscreen starts, removed once it has run a while (or closes properly)
        private int failedStarts;
        private bool markerPending, safeOfferPending;
        private DateTime startedAt = DateTime.Now;

        private string MarkerPath { get { return Path.Combine(plugin.GetPluginUserDataPath(), "starting.marker"); } }

        /// <summary>From HelperPlugin, before the theme starts (fullscreen only): counts the starts that never finished.</summary>
        internal void DoctorStarting()
        {
            if (!IsFullscreen) return;
            try
            {
                int before = 0;
                if (File.Exists(MarkerPath)) { if (!int.TryParse(File.ReadAllText(MarkerPath).Trim(), out before) || before < 1) before = 1; }
                failedStarts = before;
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
                File.WriteAllText(MarkerPath, (before + 1).ToString(CultureInfo.InvariantCulture));
                markerPending = true;
                startedAt = DateTime.Now;
                if (before >= 2) logger.Warn("Kippix: the last " + before + " fullscreen starts didn't finish");
                if (before >= 2 && !settings.SafeMode)
                {
                    // still failing after the offer: safe mode goes on by itself (it says so)
                    if (before >= 4 && settings.SafeModeOffer) { SetSafeMode(true); safeAutoNotice = true; }
                    else if (settings.SafeModeOffer) safeOfferPending = true;
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: start marker: " + ex.Message); }
        }

        private bool safeAutoNotice, safeStartNoticeShown;

        /// <summary>From HelperPlugin when Playnite closes properly.</summary>
        internal void DoctorStopped()
        {
            ClearStartMarker();
        }

        private void ClearStartMarker()
        {
            if (!markerPending) return;
            markerPending = false;
            try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch (Exception) { }
        }

        /// <summary>From the tick: the start counts as finished after 45 s; the safe mode offer once the home screen is up.</summary>
        private void DoctorTick()
        {
            if (markerPending && (DateTime.Now - startedAt).TotalSeconds > 45) ClearStartMarker();
            if (ticks < 3 || openPanel != null || confirmOpen || kbOpen) return;
            if (safeOfferPending)
            {
                safeOfferPending = false;
                Confirm(Loc("LOC_KX_DocOfferTitle", "Start in safe mode?"),
                        string.Format(Loc("LOC_KX_DocOfferText", "Kippix didn't start properly the last {0} times. Safe mode switches off widgets, the carousel and folders, live backgrounds, the in-game overlay and animations until you turn it off again (Settings › Help › Kippix Doctor)."), failedStarts),
                        Loc("LOC_KX_DocOfferYes", "Use safe mode"), () =>
                        {
                            SetSafeMode(true);
                            ShowNotice(Loc("LOC_KX_DocNotice", "KIPPIX DOCTOR"), Loc("LOC_KX_DocSafeOn", "Safe mode is on"), Loc("LOC_KX_DocSafeOnSub", "Restart Playnite so the overlay stays off too."), "", null);
                        });
                return;
            }
            if (settings.SafeMode && !safeStartNoticeShown && IsFullscreen)
            {
                safeStartNoticeShown = true;
                ShowNotice(Loc("LOC_KX_DocNotice", "KIPPIX DOCTOR"), Loc("LOC_KX_DocSafeOn", "Safe mode is on"),
                           safeAutoNotice ? Loc("LOC_KX_DocSafeAuto", "Turned on by itself: Kippix didn't start properly several times.") : Loc("LOC_KX_DocSafeOnLine", "Extras are off."),
                           Loc("LOC_KX_DocSafeWhere", "Turn it off in Settings › Help › Kippix Doctor"), null);
            }
        }

        // ---------------- the Doctor page (fullscreen) ----------------

        private List<DoctorCheck> doctorChecks;
        private bool doctorRunning, doctorReporting, doctorFromHelp;

        private ICommand openDoctorCmd;
        public System.Windows.Input.ICommand OpenDoctorCommand
        {
            get
            {
                return openDoctorCmd ?? (openDoctorCmd = new RelayCommand(_ =>
                {
                    doctorFromHelp = openPanel == "options";
                    pageStack.Clear();
                    OpenPage("doctor", "", false);
                }));
            }
        }

        private void BuildDoctorPage()
        {
            PageSub = Loc("LOC_KX_DocSub", "Health checks, safe mode and a bug report");
            PageHint = Loc("LOC_KX_PageHintBack", "");
            DoctorRows(null);
            if (doctorChecks == null) RunDoctorChecks();
        }

        private void DoctorRows(string focusKey)
        {
            PageRows.Clear();
            AddPageHeader(Loc("LOC_KX_DocSafeHeader", "SAFE MODE"));
            PageRows.Add(new PageRow
            {
                Key = "safe", Icon = "lock", Text = Loc("LOC_KX_DocSafe", "Safe mode"), Right = settings.SafeMode ? Loc("LOC_KX_On", "On") : Loc("LOC_KX_Off", "Off"),
                Sub = Loc("LOC_KX_DocSafeSub", "For when something breaks: widgets, the carousel and folders, live backgrounds, the in-game overlay and animations stay off. Turning it off puts them back."),
                Command = new RelayCommand(_ => { PlaySound("select"); SetSafeMode(!settings.SafeMode); DoctorRows("safe"); })
            });
            PageRows.Add(new PageRow
            {
                Key = "offer", Icon = "flag", Text = Loc("LOC_KX_DocOffer", "Offer safe mode after failed starts"), Right = settings.SafeModeOffer ? Loc("LOC_KX_On", "On") : Loc("LOC_KX_Off", "Off"),
                Sub = Loc("LOC_KX_DocOfferSub", "When Kippix didn't start properly twice in a row, it asks at the next start"),
                Command = new RelayCommand(_ => { PlaySound("move"); SafeModeOffer = !settings.SafeModeOffer; DoctorRows("offer"); })
            });
            AddPageHeader(Loc("LOC_KX_DocChecksHeader", "HEALTH CHECKS"));
            if (doctorChecks == null)
                PageRows.Add(new PageRow { Key = "checking", Icon = "hourglass", Text = Loc("LOC_KX_DocChecking", "Checking…") });
            else
                foreach (var c in doctorChecks)
                    PageRows.Add(new PageRow
                    {
                        Key = "c:" + c.Key + ":" + c.Title, Text = c.Title, Sub = c.Detail,
                        Icon = c.Level == "ok" ? "check" : c.Level == "info" ? "info" : c.Level == "warn" ? "flag" : "close",
                        Right = c.Level == "ok" ? Loc("LOC_KX_DocOk", "OK") : c.Level == "info" ? "" : c.Level == "warn" ? Loc("LOC_KX_DocWarn", "Check this") : Loc("LOC_KX_DocBad", "Problem")
                    });
            PageRows.Add(new PageRow { Key = "again", Icon = "refresh", Text = Loc("LOC_KX_DocAgain", "Check again"), Command = new RelayCommand(_ => { PlaySound("select"); RunDoctorChecks(); }) });
            AddPageHeader(Loc("LOC_KX_DocReportHeader", "BUG REPORT"));
            PageRows.Add(new PageRow
            {
                Key = "report", Icon = "download", Text = Loc("LOC_KX_DocReport", "Make a bug report"),
                Sub = Loc("LOC_KX_DocReportSub", "Playnite's logs, Kippix's settings (keys, passwords and personal things taken out) and the versions, zipped into Documents › Kippix Logs. The folder opens when it's done."),
                Command = new RelayCommand(_ => MakeBugReport(s => { if (openPanel == "page" && pageKey == "doctor") SetPageStatus(s); }))
            });
            NotifyPageLists();
            if (focusKey != null)
            {
                var row = PageRows.FirstOrDefault(r => r.Key == focusKey);
                if (row != null) FocusPageSoon(row);
            }
        }

        private void RunDoctorChecks()
        {
            if (doctorRunning) return;
            doctorRunning = true;
            int gen = pageGen;
            string tv = ThemeVersion(), hv = HelperVersion(), sp = SettingsPath;
            bool oldOv = OldOverlayInstalled;
            if (openPanel == "page" && pageKey == "doctor") SetPageStatus(Loc("LOC_KX_DocChecking", "Checking…"));
            KippixShared.SafeWork.Queue(_ =>
            {
                List<DoctorCheck> res = null;
                try { res = KipDoctor.Run(api, tv, hv, sp, oldOv); }
                catch (Exception ex) { logger.Warn("Kippix: Doctor: " + ex.Message); }
                OnUi(() =>
                {
                    doctorRunning = false;
                    doctorChecks = res ?? new List<DoctorCheck>();
                    if (openPanel != "page" || pageKey != "doctor") return;
                    var fe = Keyboard.FocusedElement as FrameworkElement;
                    var row = fe != null ? fe.DataContext as PageRow : null;
                    DoctorRows(row != null ? row.Key : null);
                    int bad = doctorChecks.Count(c => c.Level == "bad"), warn = doctorChecks.Count(c => c.Level == "warn");
                    SetPageStatus(bad + warn == 0 ? Loc("LOC_KX_DocAllGood", "Everything looks fine.") : string.Format(Loc("LOC_KX_DocFound", "{0} thing(s) to look at."), bad + warn));
                });
            });
        }

        /// <summary>The bug report zip (off the UI thread), then the folder opens. <paramref name="status"/> hears how it went (UI thread).</summary>
        internal void MakeBugReport(Action<string> status)
        {
            if (doctorReporting) return;
            doctorReporting = true;
            if (IsFullscreen) PlaySound("select");
            if (status != null) status(Loc("LOC_KX_DocReporting", "Making the bug report…"));
            string json;
            try { json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(settings); } catch (Exception) { json = "{}"; }
            string versions = VersionsText();
            string tv = ThemeVersion(), hv = HelperVersion(), sp = SettingsPath;
            bool oldOv = OldOverlayInstalled;
            KippixShared.SafeWork.Queue(_ =>
            {
                string zip = null, err = null;
                try
                {
                    var checks = KipDoctor.Run(api, tv, hv, sp, oldOv);
                    var addons = KipDoctor.Addons(api);
                    var sb = new StringBuilder(versions);
                    sb.AppendLine();
                    sb.AppendLine("Health checks:");
                    sb.Append(KipDoctor.ChecksText(checks));
                    sb.AppendLine();
                    sb.AppendLine("Add-ons:");
                    foreach (var a in addons.OrderBy(a => a.Name)) sb.AppendLine("  " + a.Name + (a.Version != null ? " " + a.Version : "") + "  (" + a.Id + ")");
                    zip = KipDoctor.MakeReport(api, sb.ToString(), json);
                    logger.Info("Kippix: bug report saved: " + zip);
                    KipDoctor.ShowInExplorer(zip);
                }
                catch (Exception ex) { err = ex.Message; logger.Warn("Kippix: bug report: " + ex.Message); }
                Action done = () =>
                {
                    doctorReporting = false;
                    if (status != null) status(zip != null ? string.Format(Loc("LOC_KX_DocReportDone", "Saved: Documents › Kippix Logs › {0}"), Path.GetFileName(zip))
                                                           : string.Format(Loc("LOC_KX_DocReportFailed", "Couldn't make the bug report: {0}"), err));
                };
                if (ui != null) OnUi(done);
                else if (Application.Current != null) Application.Current.Dispatcher.BeginInvoke(done);
            });
        }

        private string VersionsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Kippix bug report, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            string playnite = "?";
            try { playnite = api.ApplicationInfo.ApplicationVersion.ToString(); } catch (Exception) { }
            sb.AppendLine("Theme " + (ThemeVersion() ?? "?") + ", helper " + (HelperVersion() ?? "?") + ", Playnite " + playnite + " (" + (IsFullscreen ? "Fullscreen" : "Desktop") + ")");
            sb.AppendLine("Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " 64-bit" : "") + ", .NET " + Environment.Version + ", culture " + CultureInfo.CurrentCulture.Name);
            try { sb.AppendLine("Screen " + SystemParameters.PrimaryScreenWidth + " x " + SystemParameters.PrimaryScreenHeight + (screenInches > 0 ? ", " + screenInches.ToString("0.0", CultureInfo.InvariantCulture) + " in" : "")); } catch (Exception) { }
            sb.AppendLine("Safe mode " + (settings.SafeMode ? "on" : "off") + ", handheld " + (handheldNow ? "on" : "off") + ", battery saving " + (batterySaving ? "on" : "off")
                          + ", touch screen " + (HasTouchScreen ? "yes" : "no") + ", Steam Controller " + (SteamDevice.Connected ? "connected" : "not seen"));
            sb.AppendLine("Failed starts before this one: " + failedStarts + ", controller buttons moved: " + (settings.PadMap != null ? settings.PadMap.Count : 0));
            return sb.ToString();
        }

        /// <summary>The health check for the desktop settings' Doctor tab (run the job off the UI thread; the versions are read here first).</summary>
        internal Func<List<DoctorCheck>> DoctorCheckJob()
        {
            string tv = ThemeVersion(), hv = HelperVersion(), sp = SettingsPath;
            bool oldOv = OldOverlayInstalled;
            return () => KipDoctor.Run(api, tv, hv, sp, oldOv);
        }

        /// <summary>B on the Doctor page opened from Help: back to Help, on its row.</summary>
        private bool DoctorBack()
        {
            if (!doctorFromHelp) return false;
            doctorFromHelp = false;
            PlaySound("back");
            ClosePanels();
            TogglePanel("options");
            SetOptionsPage("help", false);
            ui.BeginInvoke(new Action(() => { if (!UiTools.FocusByNameIfVisible("KX_HelpDoctor")) UiTools.FocusInside("KX_OptionsPanel"); }), DispatcherPriority.ContextIdle);
            return true;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace KippixOverlay
{
    internal class AchievementInfo
    {
        public string ApiName;
        public string Name;
        public string Description;
        public bool Unlocked;
        public bool Hidden;
        public DateTime? Date;
        public string Icon;
        public double? Rarity;
    }

    internal class HltbTimes
    {
        public double Story;          // hours, 0 = unknown
        public double Extras;
        public double Completionist;
    }

    /// <summary>Reads data saved by the SuccessStory and HowLongToBeat add-ons, when installed.</summary>
    internal static class GameData
    {
        private const string SuccessStoryId = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private const string HltbId = "e08cd51f-9c9a-4ee3-a094-fde03b55492f";

        /// <summary>What happened on the last achievement load, for the diagnostics line.</summary>
        public static string LastAchievementInfo = "";

        /// <summary>
        /// Finds "&lt;gameId&gt;.json" written by an add-on. Tries the known folder first, then any
        /// ExtensionsData sub-folder whose name contains <paramref name="hint"/>.
        /// </summary>
        private static string FindFile(string extData, string knownId, string folder, string hint, Guid gameId)
        {
            try
            {
                string name = gameId.ToString() + ".json";
                string direct = Path.Combine(Path.Combine(extData, knownId), Path.Combine(folder, name));
                if (File.Exists(direct)) return direct;
                if (!Directory.Exists(extData)) return null;

                // Anywhere under ExtensionsData, preferring paths that mention the add-on.
                string fallback = null;
                foreach (string f in SafeFind(extData, name, 0))
                {
                    if (f.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return f;
                    if (fallback == null) fallback = f;
                }
                return fallback;
            }
            catch (Exception) { }
            return null;
        }

        private static IEnumerable<string> SafeFind(string dir, string fileName, int depth)
        {
            var found = new List<string>();
            if (depth > 4) return found;
            try
            {
                string f = Path.Combine(dir, fileName);
                if (File.Exists(f)) found.Add(f);
                foreach (string sub in Directory.GetDirectories(dir)) found.AddRange(SafeFind(sub, fileName, depth + 1));
            }
            catch (Exception) { }
            return found;
        }

        private static object ReadJson(string file)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
            return ser.DeserializeObject(File.ReadAllText(file));
        }

        public static List<AchievementInfo> LoadAchievements(string extData, Guid gameId)
        {
            string file = null;
            try
            {
                file = FindFile(extData, SuccessStoryId, "SuccessStory", "success", gameId);
                if (file == null)
                {
                    LastAchievementInfo = "No SuccessStory data for this game in " + extData;
                    return null;
                }

                IEnumerable items = FindList(ReadJson(file), 0);
                if (items == null)
                {
                    LastAchievementInfo = "SuccessStory data has no achievements: " + file;
                    return null;
                }

                var list = new List<AchievementInfo>();
                foreach (object o in items)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var a = new AchievementInfo();
                    a.Name = Str(d, "Name");
                    a.Description = Str(d, "Description");
                    a.Hidden = Bool(d, "IsHidden");

                    DateTime? dt = Date(d, "DateUnlocked");
                    if (dt.HasValue && dt.Value.Year > 1971)
                    {
                        a.Unlocked = true;
                        a.Date = dt.Value;
                    }

                    a.Icon = a.Unlocked ? Str(d, "UrlUnlocked") : Str(d, "UrlLocked");
                    if (string.IsNullOrEmpty(a.Icon)) a.Icon = Str(d, "UrlUnlocked");

                    double p;
                    if (Num(d, "Percent", out p) && p > 0 && p <= 100) a.Rarity = p;

                    if (!string.IsNullOrEmpty(a.Name)) list.Add(a);
                }

                LastAchievementInfo = list.Count + " achievements from " + file;
                return list
                    .OrderByDescending(x => x.Unlocked)
                    .ThenByDescending(x => x.Date ?? DateTime.MinValue)
                    .ToList();
            }
            catch (Exception ex)
            {
                LastAchievementInfo = "Couldn't read " + (file ?? "SuccessStory data") + ": " + ex.Message;
                return null;
            }
        }

        internal static List<AchievementInfo> Sort(List<AchievementInfo> list)
        {
            return list
                .OrderByDescending(x => x.Unlocked)
                .ThenByDescending(x => x.Date ?? DateTime.MinValue)
                .ToList();
        }

        /// <summary>The first list of objects that look like achievements, wherever it sits in the file.</summary>
        private static IEnumerable FindList(object node, int depth)
        {
            if (node == null || depth > 8 || node is string) return null;
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                object items;
                if (d.TryGetValue("Items", out items) && LooksLikeAchievements(items)) return (IEnumerable)items;
                foreach (var v in d.Values)
                {
                    var r = FindList(v, depth + 1);
                    if (r != null) return r;
                }
                return null;
            }
            if (LooksLikeAchievements(node)) return (IEnumerable)node;
            return null;
        }

        private static bool LooksLikeAchievements(object node)
        {
            var e = node as IEnumerable;
            if (e == null || node is string) return false;
            foreach (object o in e)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) return false;
                return d.ContainsKey("Name") || d.ContainsKey("name");
            }
            return false;
        }

        public static HltbTimes LoadHltb(string extData, Guid gameId)
        {
            try
            {
                string file = FindFile(extData, HltbId, "HowLongToBeat", "howlong", gameId);
                if (file == null) return null;
                object json = ReadJson(file);
                var d = FindDictWith(json, "MainStoryClassic", 0) ?? FindDictWith(json, "MainStory", 0) ?? FindDictWith(json, "CompletionistClassic", 0);
                if (d == null) return null;
                var t = new HltbTimes();
                t.Story = Best(d, "MainStory");
                t.Extras = Best(d, "MainExtra");
                t.Completionist = Best(d, "Completionist");
                if (t.Story <= 0 && t.Extras <= 0 && t.Completionist <= 0) return null;
                return t;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, object> FindDictWith(object node, string key, int depth)
        {
            if (node == null || depth > 12 || node is string) return null;
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                double dummy;
                if (Num(d, key, out dummy) && dummy > 0) return d;
                foreach (var v in d.Values)
                {
                    var r = FindDictWith(v, key, depth + 1);
                    if (r != null) return r;
                }
                return null;
            }
            var e = node as IEnumerable;
            if (e != null)
            {
                foreach (object v in e)
                {
                    var r = FindDictWith(v, key, depth + 1);
                    if (r != null) return r;
                }
            }
            return null;
        }

        // Newer HowLongToBeat add-on versions save MainStoryClassic / ...Median / ...Average; older ones just MainStory.
        private static double Best(Dictionary<string, object> d, string key)
        {
            foreach (string suffix in new[] { "Classic", "", "Median", "Average", "Leisure", "Rushed" })
            {
                double h = Hours(d, key + suffix);
                if (h > 0) return h;
            }
            return 0;
        }

        // The HowLongToBeat add-on stores times in seconds.
        private static double Hours(Dictionary<string, object> d, string key)
        {
            double v;
            if (!Num(d, key, out v) || v <= 0) return 0;
            return v / 3600.0;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        private static DateTime? Date(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            if (v is DateTime) return ((DateTime)v).ToLocalTime();
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto)) return dto.LocalDateTime;
            return null;
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        private static bool Num(Dictionary<string, object> d, string key, out double value)
        {
            value = 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null || v is bool) return false;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static string FormatHours(double hours)
        {
            if (hours <= 0) return "";
            if (hours < 1) return Math.Max(1, (int)Math.Round(hours * 60)) + " min";
            return Math.Round(hours) + " h";
        }

        public static string FormatPlaytime(ulong seconds)
        {
            if (seconds < 60) return "0 min";
            if (seconds < 3600) return (seconds / 60) + " min";
            return (seconds / 3600) + " h";
        }

        public static string FormatLastPlayed(DateTime? when)
        {
            if (!when.HasValue) return "Never played";
            DateTime t = when.Value.Kind == DateTimeKind.Utc ? when.Value.ToLocalTime() : when.Value;
            DateTime today = DateTime.Today;
            if (t.Date == today) return "Today " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "Yesterday";
            if (t.Year == today.Year) return t.ToString("d MMM");
            return t.ToString("d MMM yyyy");
        }
    }
}

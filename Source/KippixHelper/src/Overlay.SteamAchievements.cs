using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KippixOverlay
{
    /// <summary>A node in Valve's binary KeyValues format.</summary>
    internal class KvNode
    {
        public string Name;
        public object Value;                       // string, int, float, ulong, long
        public readonly List<KvNode> Children = new List<KvNode>();

        public KvNode this[string name]
        {
            get
            {
                foreach (var c in Children)
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
                return null;
            }
        }

        public string Text
        {
            get { return Value == null ? null : Convert.ToString(Value, CultureInfo.InvariantCulture); }
        }

        public long Number
        {
            get
            {
                if (Value is int) return (int)Value;
                if (Value is long) return (long)Value;
                if (Value is ulong) return (long)(ulong)Value;
                if (Value is float) return (long)(float)Value;
                long n;
                return long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
            }
        }

        public static List<KvNode> Parse(byte[] data)
        {
            int pos = 0;
            var root = new List<KvNode>();
            ReadInto(data, ref pos, root);
            return root;
        }

        private static void ReadInto(byte[] d, ref int pos, List<KvNode> into)
        {
            while (pos < d.Length)
            {
                byte type = d[pos++];
                if (type == 0x08 || type == 0x0B) return; // end of object
                var node = new KvNode { Name = CString(d, ref pos) };
                switch (type)
                {
                    case 0x00: ReadInto(d, ref pos, node.Children); break;
                    case 0x01: node.Value = CString(d, ref pos); break;
                    case 0x02: node.Value = BitConverter.ToInt32(d, pos); pos += 4; break;
                    case 0x03: node.Value = BitConverter.ToSingle(d, pos); pos += 4; break;
                    case 0x04: node.Value = BitConverter.ToInt32(d, pos); pos += 4; break;
                    case 0x06: node.Value = BitConverter.ToInt32(d, pos); pos += 4; break;
                    case 0x07: node.Value = BitConverter.ToUInt64(d, pos); pos += 8; break;
                    case 0x0A: node.Value = BitConverter.ToInt64(d, pos); pos += 8; break;
                    case 0x05:
                        {
                            int start = pos;
                            while (pos + 1 < d.Length && (d[pos] != 0 || d[pos + 1] != 0)) pos += 2;
                            node.Value = Encoding.Unicode.GetString(d, start, pos - start);
                            pos += 2;
                            break;
                        }
                    default:
                        throw new InvalidDataException("Unknown KeyValues type " + type);
                }
                into.Add(node);
            }
        }

        private static string CString(byte[] d, ref int pos)
        {
            int start = pos;
            while (pos < d.Length && d[pos] != 0) pos++;
            string s = Encoding.UTF8.GetString(d, start, pos - start);
            pos++;
            return s;
        }
    }

    /// <summary>Reads achievements from the Steam client's own local cache (appcache\stats). Works offline, no API key.</summary>
    internal static class SteamAchievements
    {
        public static readonly Guid SteamLibraryId = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        private static readonly Dictionary<string, Dictionary<string, double>> rarityCache = new Dictionary<string, Dictionary<string, double>>();
        private static readonly HashSet<string> rarityRequested = new HashSet<string>();

        public static string SteamPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
                }
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("InstallPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                }
            }
            catch (Exception) { }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            return Directory.Exists(def) ? def : null;
        }

        public static List<AchievementInfo> Load(string steamPath, string appId, out string info)
        {
            info = "";
            try
            {
                if (string.IsNullOrEmpty(steamPath)) { info = "Steam isn't installed"; return null; }
                string stats = Path.Combine(Path.Combine(steamPath, "appcache"), "stats");
                string schemaFile = Path.Combine(stats, "UserGameStatsSchema_" + appId + ".bin");
                if (!File.Exists(schemaFile)) { info = "Steam has no achievement list for app " + appId; return null; }

                string userFile = null;
                DateTime newest = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(stats, "UserGameStats_*_" + appId + ".bin"))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) { newest = t; userFile = f; }
                }

                List<KvNode> schemaRoot = KvNode.Parse(File.ReadAllBytes(schemaFile));
                KvNode app = schemaRoot.FirstOrDefault(n => n.Children.Count > 0);
                KvNode statsNode = app != null ? app["stats"] : null;
                if (statsNode == null) { info = "Steam's achievement list for app " + appId + " is empty"; return null; }

                KvNode userCache = null;
                if (userFile != null)
                {
                    List<KvNode> userRoot = KvNode.Parse(File.ReadAllBytes(userFile));
                    userCache = userRoot.FirstOrDefault(n => n.Children.Count > 0);
                }

                Dictionary<string, double> rarity = Rarity(appId);
                var list = new List<AchievementInfo>();
                foreach (KvNode stat in statsNode.Children)
                {
                    KvNode bits = stat["bits"];
                    if (bits == null) continue;
                    KvNode userStat = userCache != null ? userCache[stat.Name] : null;
                    long mask = userStat != null && userStat["data"] != null ? userStat["data"].Number : 0;
                    KvNode times = userStat != null ? userStat["AchievementTimes"] : null;

                    foreach (KvNode bit in bits.Children)
                    {
                        int index;
                        if (!int.TryParse(bit.Name, out index))
                            index = bit["bit"] != null ? (int)bit["bit"].Number : 0;
                        KvNode display = bit["display"];
                        var a = new AchievementInfo
                        {
                            ApiName = bit["name"] != null ? bit["name"].Text : null,
                            Name = Localized(display != null ? display["name"] : null),
                            Description = Localized(display != null ? display["desc"] : null),
                            Hidden = display != null && display["hidden"] != null && display["hidden"].Number != 0
                        };
                        if (string.IsNullOrEmpty(a.Name)) a.Name = a.ApiName;

                        a.Unlocked = index >= 0 && index < 63 && ((mask >> index) & 1) == 1;
                        if (a.Unlocked)
                        {
                            KvNode tn = times != null ? times[index.ToString(CultureInfo.InvariantCulture)] : null;
                            long unix = tn != null ? tn.Number : 0;
                            a.Date = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : DateTime.Now;
                        }

                        KvNode iconNode = null;
                        if (display != null) iconNode = (a.Unlocked ? display["icon"] : display["icon_gray"]) ?? display["icon"];
                        string icon = iconNode != null ? iconNode.Text : null;
                        if (!string.IsNullOrEmpty(icon))
                            a.Icon = "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/" + appId + "/" + icon;

                        double r;
                        if (rarity != null && a.ApiName != null && rarity.TryGetValue(a.ApiName, out r) && r > 0) a.Rarity = r;
                        if (!string.IsNullOrEmpty(a.Name)) list.Add(a);
                    }
                }

                if (userFile == null) info = list.Count + " achievements from Steam (no unlock data yet)";
                else info = list.Count + " achievements from Steam";
                return list.Count > 0 ? GameData.Sort(list) : null;
            }
            catch (Exception ex)
            {
                info = "Reading Steam's achievements failed: " + ex.Message;
                return null;
            }
        }

        private static string Localized(KvNode node)
        {
            if (node == null) return null;
            if (node.Children.Count == 0) return node.Text;
            KvNode en = node["english"];
            if (en != null && !string.IsNullOrEmpty(en.Text)) return en.Text;
            foreach (var c in node.Children)
                if (!string.Equals(c.Name, "token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.Text)) return c.Text;
            return null;
        }

        /// <summary>Global unlock percentages (public Steam API, no key). Fetched in the background and cached.</summary>
        private static Dictionary<string, double> Rarity(string appId)
        {
            lock (rarityCache)
            {
                Dictionary<string, double> r;
                if (rarityCache.TryGetValue(appId, out r)) return r;
                if (!rarityRequested.Add(appId)) return null;
            }
            new Thread(() =>
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
                    string json;
                    using (var wc = new WebClient())
                        json = wc.DownloadString("https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v0002/?format=json&gameid=" + appId);
                    var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    object ap, achs;
                    if (root != null && root.TryGetValue("achievementpercentages", out ap) && ap is Dictionary<string, object> &&
                        ((Dictionary<string, object>)ap).TryGetValue("achievements", out achs) && achs is System.Collections.IEnumerable)
                    {
                        foreach (object o in (System.Collections.IEnumerable)achs)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d == null || !d.ContainsKey("name") || !d.ContainsKey("percent")) continue;
                            double p;
                            if (double.TryParse(Convert.ToString(d["percent"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                                map[Convert.ToString(d["name"], CultureInfo.InvariantCulture)] = p;
                        }
                    }
                    lock (rarityCache) rarityCache[appId] = map;
                }
                catch (Exception)
                {
                    lock (rarityCache) rarityRequested.Remove(appId);
                }
            }) { IsBackground = true }.Start();
            return null;
        }
    }
}

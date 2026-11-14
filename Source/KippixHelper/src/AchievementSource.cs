using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace KippixHelper
{
    internal class AchItemData
    {
        public string Name;
        public string Description;
        public string Icon;         // full path or URL of the picture to show
        public bool LockedArt;      // Icon is a separate "locked" picture (otherwise a locked one is drawn dimmed)
        public bool Unlocked;
        public bool Hidden;
        public DateTime? Date;
        public double? Rarity;      // % of players who have it
        public string RarityTier;   // "Ultra rare"... (Playnite Achievements only)
        public int? Points;
        public string TrophyType;   // bronze / silver / gold / platinum
        public string Category;
        public int? ProgressNum;
        public int? ProgressDenom;
    }

    internal class AchGameData
    {
        public Guid Id;
        public string Name;
        public int Unlocked;
        public int Total;
        public DateTime? LastUnlock;
        public List<AchItemData> Items = new List<AchItemData>();
    }

    /// <summary>
    /// Reads every game's achievements. Playnite Achievements first (its AchievementDataService, through reflection,
    /// so nothing breaks when it isn't installed), then SuccessStory game by game.
    /// </summary>
    internal static class AchievementSource
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static List<AchGameData> LoadAll(IPlayniteAPI api, out string source)
        {
            List<AchGameData> list = null;
            try { list = FromPlayniteAchievements(api); }
            catch (Exception ex) { logger.Error(ex, "Kippix: reading Playnite Achievements failed"); }
            if (list != null && list.Count > 0) { source = "Playnite Achievements"; return list; }

            try { list = FromSuccessStory(api); }
            catch (Exception ex) { logger.Error(ex, "Kippix: reading SuccessStory failed"); }
            if (list != null && list.Count > 0) { source = "SuccessStory"; return list; }

            source = null;
            return new List<AchGameData>();
        }

        /// <summary>
        /// One game on its own (any thread), for a game missing from the list read earlier: Playnite Achievements read again
        /// (it may have fetched the game since), then SuccessStory, then Steam's own cache (appcache\stats). Null when none has any.
        /// </summary>
        public static AchGameData LoadOne(IPlayniteAPI api, Game game, out string source)
        {
            source = null;
            if (game == null) return null;
            try
            {
                var pa = FromPlayniteAchievements(api);
                var d = pa != null ? pa.FirstOrDefault(x => x != null && x.Id == game.Id) : null;
                if (d != null && d.Items.Count > 0) { source = "Playnite Achievements"; return d; }
            }
            catch (Exception ex) { logger.Warn("Kippix: Playnite Achievements for " + game.Name + ": " + ex.Message); }
            try
            {
                if (api.Addons.Plugins.Any(p => p.GetType().Name == "SuccessStory"))
                {
                    string info;
                    var items = SuccessStoryLive.Load(api, game.Id, out info);
                    if (items != null && items.Count > 0)
                    {
                        var d = new AchGameData { Id = game.Id, Name = game.Name, Items = items };
                        Finish(d, api);
                        source = "SuccessStory";
                        return d;
                    }
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: SuccessStory for " + game.Name + ": " + ex.Message); }
            try
            {
                if (game.PluginId == KippixOverlay.SteamAchievements.SteamLibraryId && !string.IsNullOrEmpty(game.GameId))
                {
                    string info;
                    var list = KippixOverlay.SteamAchievements.Load(KippixOverlay.SteamAchievements.SteamPath(), game.GameId, out info);
                    if (list != null && list.Count > 0)
                    {
                        var d = new AchGameData { Id = game.Id, Name = game.Name };
                        foreach (var a in list)
                            d.Items.Add(new AchItemData { Name = a.Name ?? "", Description = a.Description, Unlocked = a.Unlocked, Hidden = a.Hidden, Date = a.Date, Icon = a.Icon, Rarity = a.Rarity });
                        Finish(d, api);
                        source = "Steam";
                        return d;
                    }
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: Steam's achievements for " + game.Name + ": " + ex.Message); }
            return null;
        }

        // ---------------- Playnite Achievements ----------------

        private static List<AchGameData> FromPlayniteAchievements(IPlayniteAPI api)
        {
            Plugin plugin = api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == "PlayniteAchievements.PlayniteAchievementsPlugin");
            if (plugin == null) return null;
            // Icons are saved relative to the add-on's data folder (icon_cache\...)
            string dataDir = null;
            try { dataDir = plugin.GetPluginUserDataPath(); } catch (Exception) { }
            object service = Get(plugin, "AchievementDataService");
            if (service == null) { logger.Warn("Kippix: Playnite Achievements has no AchievementDataService"); return null; }
            MethodInfo all = service.GetType().GetMethod("GetAllGameAchievementData", Type.EmptyTypes);
            var games = all != null ? all.Invoke(service, null) as IEnumerable : null;
            if (games == null) return null;

            var result = new List<AchGameData>();
            foreach (object g in games)
            {
                if (g == null) continue;
                if (Bool(g, "ExcludedByUser")) continue;
                object id = Get(g, "PlayniteGameId");
                if (!(id is Guid)) continue;

                var data = new AchGameData { Id = (Guid)id, Name = Get(g, "GameName") as string };
                var items = Get(g, "Achievements") as IEnumerable;
                if (items != null)
                {
                    foreach (object a in items)
                    {
                        if (a == null) continue;
                        bool unlocked = Bool(a, "Unlocked");
                        var it = new AchItemData
                        {
                            Name = (Get(a, "DisplayName") as string) ?? (Get(a, "Name") as string) ?? "",
                            Description = Get(a, "Description") as string,
                            Unlocked = unlocked,
                            Hidden = Bool(a, "IsHidden") || Bool(a, "Hidden"),
                            Points = Get(a, "Points") as int?,
                            TrophyType = Get(a, "TrophyType") as string,
                            Category = Get(a, "Category") as string,
                            ProgressNum = Get(a, "ProgressNum") as int?,
                            ProgressDenom = Get(a, "ProgressDenom") as int?
                        };
                        object tier = Get(a, "Rarity");
                        if (tier != null) it.RarityTier = tier.ToString();

                        string onIcon = ResolveIcon(Get(a, "UnlockedIconPath") as string, dataDir);
                        string offIcon = ResolveIcon(Get(a, "LockedIconPath") as string, dataDir);
                        if (onIcon == null) onIcon = ResolveIcon(Get(a, "IconDisplay") as string, dataDir);
                        if (unlocked || offIcon == null || string.Equals(offIcon, onIcon, StringComparison.OrdinalIgnoreCase))
                            it.Icon = onIcon ?? offIcon;
                        else
                        {
                            it.Icon = offIcon;
                            it.LockedArt = true;
                        }

                        object t = Get(a, "UnlockTimeUtc") ?? Get(a, "DateUnlocked");
                        if (t is DateTime && ((DateTime)t).Year > 1971)
                        {
                            var dt = (DateTime)t;
                            it.Date = dt.Kind == DateTimeKind.Local ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
                        }
                        object pct = Get(a, "GlobalPercentUnlocked");
                        if (pct is double && (double)pct > 0) it.Rarity = (double)pct;
                        data.Items.Add(it);
                    }
                }
                if (data.Items.Count == 0) continue;
                Finish(data, api);
                result.Add(data);
            }
            return result;
        }

        // ---------------- SuccessStory ----------------

        private static List<AchGameData> FromSuccessStory(IPlayniteAPI api)
        {
            if (!api.Addons.Plugins.Any(p => p.GetType().Name == "SuccessStory")) return null;
            var result = new List<AchGameData>();
            foreach (Game g in api.Database.Games.ToList())
            {
                if (g.Hidden) continue;
                string info;
                var items = SuccessStoryLive.Load(api, g.Id, out info);
                if (items == null || items.Count == 0) continue;
                var data = new AchGameData { Id = g.Id, Name = g.Name, Items = items };
                Finish(data, api);
                result.Add(data);
            }
            return result;
        }

        private static void Finish(AchGameData d, IPlayniteAPI api)
        {
            d.Total = d.Items.Count;
            d.Unlocked = d.Items.Count(i => i.Unlocked);
            d.LastUnlock = d.Items.Where(i => i.Date.HasValue).Select(i => i.Date).OrderByDescending(x => x).FirstOrDefault();
            if (string.IsNullOrEmpty(d.Name))
            {
                var game = api.Database.Games.Get(d.Id);
                d.Name = game != null ? game.Name : "Unknown game";
            }
        }

        /// <summary>Full path (or URL) of an icon, or null when it doesn't exist.</summary>
        internal static string ResolveIcon(string path, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim();
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
            if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
            try
            {
                string full = Path.IsPathRooted(path) ? path : (baseDir != null ? Path.Combine(baseDir, path) : null);
                return full != null && File.Exists(full) ? full : null;
            }
            catch (Exception) { return null; }
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name, Inst);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            catch (AmbiguousMatchException)
            {
                foreach (PropertyInfo p in o.GetType().GetProperties(Inst))
                    if (p.Name == name && p.GetIndexParameters().Length == 0)
                        try { return p.GetValue(o, null); } catch (Exception) { }
            }
            catch (Exception) { }
            return null;
        }

        private static bool Bool(object o, string name)
        {
            object v = Get(o, name);
            return v is bool && (bool)v;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Playnite.SDK;

namespace KippixOverlay
{
    /// <summary>
    /// Reads achievements straight from the running SuccessStory add-on (the same data other themes show),
    /// instead of from its files on disk.
    /// </summary>
    internal static class SuccessStoryLive
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        public static List<AchievementInfo> Load(IPlayniteAPI api, Guid gameId, out string info)
        {
            info = "";
            try
            {
                object plugin = null;
                foreach (object p in api.Addons.Plugins)
                {
                    Type pt = p.GetType();
                    if (string.Equals(pt.Name, "SuccessStory", StringComparison.OrdinalIgnoreCase) ||
                        (pt.Namespace ?? "").StartsWith("SuccessStory", StringComparison.OrdinalIgnoreCase) && pt.Name.EndsWith("SuccessStory"))
                    {
                        plugin = p;
                        break;
                    }
                }
                if (plugin == null) { info = "SuccessStory isn't loaded in Playnite"; return null; }

                object db = GetStatic(plugin.GetType(), "PluginDatabase") ?? GetInstance(plugin, "PluginDatabase");
                if (db == null) { info = "Couldn't reach SuccessStory's database (version " + plugin.GetType().Assembly.GetName().Version + ")"; return null; }

                object game = CallGet(db, gameId, api);
                if (game == null) { info = "SuccessStory has no entry for this game"; return null; }

                var items = GetInstance(game, "Items") as IEnumerable;
                if (items == null) { info = "SuccessStory's entry has no achievement list"; return null; }

                var list = new List<AchievementInfo>();
                foreach (object it in items)
                {
                    if (it == null) continue;
                    var a = new AchievementInfo
                    {
                        Name = GetInstance(it, "Name") as string,
                        Description = GetInstance(it, "Description") as string,
                        Hidden = GetInstance(it, "IsHidden") is bool && (bool)GetInstance(it, "IsHidden")
                    };

                    object date = GetInstance(it, "DateUnlocked");
                    if (date is DateTime)
                    {
                        var dt = (DateTime)date;
                        if (dt.Year > 1971)
                        {
                            a.Unlocked = true;
                            a.Date = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
                        }
                    }

                    string localImg = (a.Unlocked ? GetInstance(it, "ImageUnlocked") : GetInstance(it, "ImageLocked")) as string;
                    string url = (a.Unlocked ? GetInstance(it, "UrlUnlocked") : GetInstance(it, "UrlLocked")) as string;
                    if (string.IsNullOrEmpty(url)) url = GetInstance(it, "UrlUnlocked") as string;
                    a.Icon = !string.IsNullOrEmpty(localImg) && File.Exists(localImg) ? localImg : url;

                    object pct = GetInstance(it, "Percent");
                    if (pct != null)
                    {
                        double p;
                        if (double.TryParse(Convert.ToString(pct, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out p) && p > 0 && p <= 100)
                            a.Rarity = p;
                    }

                    if (!string.IsNullOrEmpty(a.Name)) list.Add(a);
                }

                info = list.Count + " achievements from SuccessStory";
                return list.Count > 0 ? GameData.Sort(list) : null;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                info = "Reading SuccessStory failed: " + inner.Message;
                return null;
            }
        }

        private static object GetStatic(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                var prop = cur.GetProperty(name, AnyStatic);
                if (prop != null) return prop.GetValue(null, null);
                var field = cur.GetField(name, AnyStatic);
                if (field != null) return field.GetValue(null);
            }
            return null;
        }

        private static object GetInstance(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type cur = o.GetType(); cur != null; cur = cur.BaseType)
                {
                    var prop = cur.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
                    if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(o, null);
                    var field = cur.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
                    if (field != null) return field.GetValue(o);
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Calls the database's Get(Guid id, ...) without forcing a refresh, or Get(Game, ...).</summary>
        private static object CallGet(object db, Guid id, IPlayniteAPI api)
        {
            var methods = db.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "Get" && m.GetParameters().Length >= 1)
                .ToList();

            foreach (var m in methods.Where(m => m.GetParameters()[0].ParameterType == typeof(Guid)))
            {
                object r = Invoke(db, m, id);
                if (r != null) return r;
            }

            object game = null;
            foreach (var g in api.Database.Games) if (g.Id == id) { game = g; break; }
            if (game == null) return null;
            foreach (var m in methods.Where(m => m.GetParameters()[0].ParameterType.IsInstanceOfType(game)))
            {
                object r = Invoke(db, m, game);
                if (r != null) return r;
            }
            return null;
        }

        private static object Invoke(object target, MethodInfo m, object first)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            args[0] = first;
            for (int i = 1; i < ps.Length; i++)
            {
                var p = ps[i];
                if (p.ParameterType == typeof(bool))
                    args[i] = p.Name.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0; // OnlyCache = true, Force = false
                else if (p.IsOptional) args[i] = p.DefaultValue;
                else args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }
            try { return m.Invoke(target, args); }
            catch (Exception) { return null; }
        }
    }
}

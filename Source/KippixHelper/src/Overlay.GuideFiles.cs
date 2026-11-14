using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using Playnite.SDK.Models;

namespace KippixOverlay
{
    // The fill-in file. Field names are lower case so the file reads naturally for people and for ChatGPT.
    public class GuideFileAchievement
    {
        public string name { get; set; }
        public string description { get; set; }
        public bool hidden { get; set; }
        public string tip { get; set; }
        public bool missable { get; set; }
        public string when { get; set; }
    }

    public class GuideFileGame
    {
        public string gameId { get; set; }
        public string game { get; set; }
        public string platform { get; set; }
        public List<GuideFileAchievement> achievements { get; set; }
    }

    public class GuideFile
    {
        public string format { get; set; }
        public string instructions { get; set; }
        public List<GuideFileGame> games { get; set; }
    }

    /// <summary>Exports achievements to a file you can fill in (yourself or with ChatGPT), and imports the filled-in file.</summary>
    internal static class GuideFiles
    {
        public const string FormatName = "BananaBreadOverlay achievement guides 1";

        public const string Instructions =
            "Fill in a guide for every achievement in this file. For each achievement: " +
            "'tip' = 1 to 3 sentences on exactly how to unlock it (specific chapters, locations, characters, items or requirements; no filler). " +
            "'missable' = true if it can be permanently missed in a playthrough, otherwise false. " +
            "'when' = the earliest point it can be done, e.g. 'Chapter 3', 'After the first palace', 'Post-game', 'Any time'. " +
            "Look up accurate information for each game rather than guessing. " +
            "Do not change gameId, game or name, and keep every game and achievement. " +
            "Give back the complete file as a downloadable .json file in exactly the same format.";

        public const string ChatPrompt =
            "I've attached a JSON file of video game achievements. For every achievement, fill in 'tip' (1-3 sentences on exactly how to unlock it, " +
            "with specific chapters, locations or requirements), 'missable' (true if it can be permanently missed) and 'when' (the earliest point it can be done, " +
            "e.g. 'Chapter 3' or 'Post-game'). Search the web so the tips are accurate. Don't change gameId, game or name, and don't skip any. " +
            "Give me back the complete file as a downloadable .json file in exactly the same format.";

        public static GuideFile Build(List<KeyValuePair<Game, List<AchievementInfo>>> games, GuideStore store, Func<Game, string> platformOf)
        {
            var file = new GuideFile { format = FormatName, instructions = Instructions, games = new List<GuideFileGame>() };
            foreach (var w in games)
            {
                var fg = new GuideFileGame
                {
                    gameId = w.Key.Id.ToString(),
                    game = w.Key.Name,
                    platform = platformOf(w.Key),
                    achievements = new List<GuideFileAchievement>()
                };
                foreach (var a in w.Value)
                {
                    if (string.IsNullOrEmpty(a.Name)) continue;
                    AchievementGuide saved = store.Find(w.Key.Id, a.Name);
                    fg.achievements.Add(new GuideFileAchievement
                    {
                        name = a.Name,
                        description = a.Description ?? "",
                        hidden = a.Hidden,
                        tip = saved != null ? saved.Tip ?? "" : "",
                        missable = saved != null && saved.Missable,
                        when = saved != null ? saved.When ?? "" : ""
                    });
                }
                if (fg.achievements.Count > 0) file.games.Add(fg);
            }
            return file;
        }

        public static string ToJson(GuideFile file)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            // The serializer escapes ' < > & for web pages; plain characters are easier to read and still valid JSON.
            string text = json.Serialize(file).Replace("\\u0027", "'").Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\u0026", "&");
            return OverlayConfig.PrettyJson(text);
        }

        /// <summary>Reads a filled-in file and saves every tip in it. Lenient about what ChatGPT sends back. Returns a summary.</summary>
        public static string Import(string text, GuideStore store, List<Game> library)
        {
            object root = Parse(text);
            if (root == null) throw new InvalidOperationException("That file isn't readable JSON. Make sure it's the .json file ChatGPT gave back, not a copy of the chat.");

            IEnumerable gameList = null;
            var dict = root as IDictionary<string, object>;
            if (dict != null) gameList = Get(dict, "games") as IEnumerable;
            else gameList = root as IEnumerable;
            if (gameList == null || gameList is string) throw new InvalidOperationException("That file doesn't contain a list of games. Export a file first, then import the filled-in copy.");

            var byId = new Dictionary<Guid, Game>();
            var byName = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
            foreach (Game g in library)
            {
                byId[g.Id] = g;
                if (!string.IsNullOrEmpty(g.Name) && !byName.ContainsKey(g.Name.Trim())) byName[g.Name.Trim()] = g;
            }

            int gamesUpdated = 0, tips = 0, empty = 0;
            var notFound = new List<string>();
            foreach (object o in gameList)
            {
                var gd = o as IDictionary<string, object>;
                if (gd == null) continue;
                string idText = Str(gd, "gameId"), name = Str(gd, "game");
                Game game = null;
                Guid id;
                if (Guid.TryParse(idText, out id)) byId.TryGetValue(id, out game);
                if (game == null && name.Length > 0) byName.TryGetValue(name.Trim(), out game);
                if (game == null) { notFound.Add(name.Length > 0 ? name : idText); continue; }

                var achList = Get(gd, "achievements") as IEnumerable;
                if (achList == null || achList is string) continue;

                GameGuides guides = store.Load(game.Id) ?? new GameGuides { GameId = game.Id, Created = DateTime.Now, Provider = "Imported file", Model = "" };
                if (guides.Items == null) guides.Items = new Dictionary<string, AchievementGuide>();
                if (guides.Skipped == null) guides.Skipped = new List<string>();
                guides.GameName = game.Name;

                int added = 0;
                foreach (object ao in achList)
                {
                    var ad = ao as IDictionary<string, object>;
                    if (ad == null) continue;
                    string an = Str(ad, "name");
                    string tip = Str(ad, "tip");
                    if (an.Length == 0) continue;
                    if (tip.Length == 0) { empty++; continue; }
                    string key = GuideStore.Key(an);
                    guides.Items[key] = new AchievementGuide
                    {
                        Name = an,
                        Tip = tip,
                        Missable = Bool(Get(ad, "missable")),
                        When = Str(ad, "when"),
                        Created = DateTime.Now
                    };
                    guides.Skipped.Remove(key);
                    added++;
                }
                if (added > 0)
                {
                    store.Save(guides);
                    gamesUpdated++;
                    tips += added;
                }
            }

            string summary = "Imported " + Plural(tips, "tip") + " for " + Plural(gamesUpdated, "game") + ".";
            if (empty > 0) summary += "\n" + Plural(empty, "achievement") + " in the file had no tip, so " + (empty == 1 ? "it was" : "they were") + " left as before.";
            if (notFound.Count > 0)
                summary += "\n" + Plural(notFound.Count, "game") + (notFound.Count == 1 ? " wasn't" : " weren't") + " found in your library: " + string.Join(", ", notFound.Take(5).ToArray()) + (notFound.Count > 5 ? "\u2026" : "") + ".";
            return summary;
        }

        private static string Plural(int n, string word)
        {
            return n + " " + word + (n == 1 ? "" : "s");
        }

        private static object Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            // Allow a file wrapped in ```json fences or with a sentence before it.
            int a = text.IndexOfAny(new[] { '{', '[' });
            int b = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
            if (a < 0 || b <= a) return null;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
                return json.DeserializeObject(text.Substring(a, b - a + 1));
            }
            catch (Exception) { return null; }
        }

        private static object Get(IDictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v)) return v;
            foreach (var kv in d)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        private static string Str(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return "";
            if (v is string) return ((string)v).Trim();
            if (v is IEnumerable) return string.Join(" ", ((IEnumerable)v).Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray()).Trim();
            return Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
        }

        private static bool Bool(object v)
        {
            if (v is bool) return (bool)v;
            string s = (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim().ToLowerInvariant();
            return s == "true" || s == "yes" || s == "y" || s == "1";
        }
    }
}

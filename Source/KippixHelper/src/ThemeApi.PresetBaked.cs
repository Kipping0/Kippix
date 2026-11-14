using System;
using System.Collections.Generic;
using System.Linq;

namespace KippixHelper
{
    /// <summary>
    /// (0.96) Which Kippix designs each layout lists (some were taken out), and the designs as the user set them up
    /// (PresetBakedData.cs): a design with one there is that, instead of the one built in ThemeApi.PresetDesigns.cs.
    /// </summary>
    public partial class ThemeApi
    {
        private static int[] PresetDesignNumbers(string slot)
        {
            switch (slot)
            {
                case "row0": return new[] { 1, 3 };   // (Neon arcade taken out)
                case "row1": return new[] { 1, 2 };   // (0.97: Hive taken out)
                case "row2": return new[] { 1, 2 };   // (0.97: Duo taken out)
                case "l1": return new[] { 1, 3 };     // (Honeycomb taken out)
                case "l2": return new[] { 2, 3 };     // (0.97: Spotlight taken out)
                case "l3": return new[] { 1, 3 };     // (Mosaic taken out)
                case "l4": return new[] { 2 };        // (Bento, and in 0.97 Launcher, taken out)
                case "game": return new[] { 1, 2 };
                default: return new[] { 1, 2, 3 };
            }
        }

        private static Dictionary<string, string> bakedPresets;

        /// <summary>The design as it was set up and put in (true), into s / places / bg; false: build it as usual.</summary>
        private bool BakedPreset(string slot, int n, HelperSettings s, ref HomePlaceSet places, ref double[] bg)
        {
            try
            {
                if (bakedPresets == null)
                {
                    var all = PresetJs.DeserializeObject(PresetBakedData.Json) as Dictionary<string, object>;
                    bakedPresets = new Dictionary<string, string>();
                    if (all != null) foreach (var kv in all) bakedPresets[kv.Key] = PresetJs.Serialize(kv.Value);
                }
                string json;
                if (!bakedPresets.TryGetValue(slot + ".kx" + n, out json)) return false;
                var snap = PresetJs.Deserialize<PresetSnap>(json);   // (a fresh copy each time: it's changed below)
                if (snap == null || snap.F == null) return false;
                var has = new HashSet<string>(snap.K ?? new string[0]);
                bool game = slot == "game";
                foreach (var f in PresetFields(game)) if (has.Contains(f.Name)) f.SetValue(s, f.GetValue(snap.F));
                if (s.Widgets == null) s.Widgets = new List<WidgetConfig>();
                // the games on its tiles: @top0, @top1... are your most played (as the design was built from the one who set it up's)
                var top = PresetTopGames(16);
                foreach (var list in new[] { s.ManualCards, s.FreeCards, s.BoardCards })
                    if (list != null) foreach (var c in list) BakedGame(c, top);
                places = snap.Places ?? new HomePlaceSet();
                if (snap.Bg != null && snap.Bg.Length >= 4) bg = snap.Bg;
                return true;
            }
            catch (Exception ex) { logger.Warn("Kippix: a design couldn't be read (" + slot + " " + n + "): " + ex.Message); return false; }
        }

        private static void BakedGame(BoardCard c, List<string> top)
        {
            if (c == null || c.Kind != "pinned" || c.GameId == null || !c.GameId.StartsWith("@top", StringComparison.Ordinal)) return;
            int i;
            if (int.TryParse(c.GameId.Substring(4), out i) && i >= 0 && i < top.Count) { c.GameId = top[i]; return; }
            c.Kind = "filter"; c.Source = "played"; c.Live = true; c.GameId = null;   // (not that many games: one that changes)
        }
    }
}

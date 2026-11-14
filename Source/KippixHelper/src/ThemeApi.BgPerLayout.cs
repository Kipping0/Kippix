using System;
using System.Collections.Generic;

namespace KippixHelper
{
    /// <summary>
    /// (0.87) Background › Move the picture and Fade to black are kept for each layout: Row (each design), Grid, List,
    /// Board, Manual Board and Folder's tiles each remember their own. A layout you haven't set yet starts centred with the
    /// usual fade. The values in use (HomeBgOffsetX/Y, HomeFadeDir/Reach) belong to BgLayoutKey; switching layout puts them
    /// away under that key and brings out the new layout's.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>(0.88) The home screen parts that go with a layout: (0.89) all of them, the profile button and shortcut bar too.</summary>
        private static bool IsLayoutPlaceKey(string key)
        {
            return key != null && key.StartsWith("home.", StringComparison.Ordinal);
        }

        private static void TakeHome<T>(Dictionary<string, T> from, Dictionary<string, T> into)
        {
            if (from == null) return;
            foreach (var k in new List<string>(from.Keys))
                if (IsLayoutPlaceKey(k)) { into[k] = from[k]; from.Remove(k); }
        }

        private static void PutHome<T>(Dictionary<string, T> from, Dictionary<string, T> into)
        {
            if (from == null || into == null) return;
            foreach (var kv in from) into[kv.Key] = kv.Value;
        }

        /// <summary>Puts the home screen's Free move changes away under the layout they were made in, and brings out the new
        /// layout's (none yet: every part where it comes).</summary>
        private void SwapHomePlaces(string from, string to)
        {
            if (settings.HomePlacePerLayout == null) settings.HomePlacePerLayout = new Dictionary<string, HomePlaceSet>();
            if (settings.MasterOffsets == null) settings.MasterOffsets = new Dictionary<string, double[]>();
            if (settings.MasterSizes == null) settings.MasterSizes = new Dictionary<string, double[]>();
            if (settings.MasterRotations == null) settings.MasterRotations = new Dictionary<string, double>();
            if (settings.MasterLayers == null) settings.MasterLayers = new Dictionary<string, int>();
            if (settings.MasterOpacity == null) settings.MasterOpacity = new Dictionary<string, double>();
            var keep = new HomePlaceSet();
            var turned = new List<string>();
            foreach (var k in settings.MasterRotations.Keys) if (IsLayoutPlaceKey(k)) turned.Add(k);
            TakeHome(settings.MasterOffsets, keep.Offsets);
            TakeHome(settings.MasterSizes, keep.Sizes);
            TakeHome(settings.MasterRotations, keep.Rotations);
            TakeHome(settings.MasterLayers, keep.Layers);
            TakeHome(settings.MasterOpacity, keep.Opacity);
            // (0.89) the widgets' places go with the layout too (a layout not set up yet keeps them where they are)
            foreach (var w in Widgets)
            {
                var c = w.Config;
                if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                keep.Widgets[c.Id] = new[] { c.X, c.Y, c.Scale, c.WidthMul, c.HeightMul, c.Rotation, c.Layer, c.Opacity };
            }
            settings.HomePlacePerLayout[from] = keep;
            HomePlaceSet next;
            if (settings.HomePlacePerLayout.TryGetValue(to, out next) && next != null)
            {
                PutHome(next.Offsets, settings.MasterOffsets);
                PutHome(next.Sizes, settings.MasterSizes);
                PutHome(next.Rotations, settings.MasterRotations);
                PutHome(next.Layers, settings.MasterLayers);
                PutHome(next.Opacity, settings.MasterOpacity);
                if (next.Widgets != null)
                    foreach (var w in Widgets)
                    {
                        double[] v;
                        var c = w.Config;
                        if (c == null || c.Id == null || !next.Widgets.TryGetValue(c.Id, out v) || v == null || v.Length < 8) continue;
                        bool reshape = Math.Abs(c.WidthMul - v[3]) > 0.001 || Math.Abs(c.HeightMul - v[4]) > 0.001;
                        c.X = v[0]; c.Y = v[1]; c.Scale = v[2]; c.WidthMul = v[3]; c.HeightMul = v[4]; c.Rotation = v[5]; c.Layer = (int)v[6]; c.Opacity = v[7];
                        try { w.Place(); if (reshape) RefreshWidget(w); } catch (Exception) { }
                    }
            }
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    RefreshMasterItems();
                    ApplyMasterOffsets();
                    ApplyMasterRotations();   // (turns on parts no longer turned come off too)
                    ApplyHomeLayoutSoon();
                    NotifyMoved();
                }
                catch (Exception ex) { logger.Warn("Kippix: home places per layout: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private string BgLayoutKeyNow
        {
            get
            {
                if (FolderTilesShown) return "folders";
                int l = LayoutNow;
                return l == 0 ? "row" + settings.RowDesign : "l" + l;
            }
        }

        /// <summary>From HomeModeChanged and the 2-second tick (a folder opening or closing changes the layout too).</summary>
        private void BgFollowLayout()
        {
            try
            {
                string now = BgLayoutKeyNow;
                if (string.IsNullOrEmpty(settings.BgLayoutKey)) { settings.BgLayoutKey = now; return; }   // (what's set now is this layout's)
                if (now == settings.BgLayoutKey) { PresetSync(now); return; }   // (0.91: same key, but a preset may need to come out)
                string was = settings.BgLayoutKey;
                PresetLeave(was);   // (0.91) a layout with a preset keeps its own settings: taken before anything moves
                if (settings.BgPerLayout == null) settings.BgPerLayout = new Dictionary<string, double[]>();
                settings.BgPerLayout[settings.BgLayoutKey] = new[] { settings.HomeBgOffsetX, settings.HomeBgOffsetY, settings.HomeFadeDir, settings.HomeFadeReach };
                double[] v;
                if (settings.BgPerLayout.TryGetValue(now, out v) && v != null && v.Length >= 4)
                {
                    settings.HomeBgOffsetX = Math.Max(-1, Math.Min(1, v[0]));
                    settings.HomeBgOffsetY = Math.Max(-1, Math.Min(1, v[1]));
                    settings.HomeFadeDir = Math.Max(0, Math.Min(4, (int)v[2]));
                    settings.HomeFadeReach = Math.Max(5, Math.Min(95, (int)v[3]));
                }
                else
                {
                    settings.HomeBgOffsetX = 0;
                    settings.HomeBgOffsetY = 0;
                    settings.HomeFadeDir = 0;
                    settings.HomeFadeReach = 44;
                }
                SwapHomePlaces(settings.BgLayoutKey, now);   // (0.88) and its own Free move places
                Dbg("background: " + settings.BgLayoutKey + " → " + now + " (its own place and fade)");
                settings.BgLayoutKey = now;
                SaveSettings();
                NotifyHomeBackground();
                PresetEnter(was, now);   // (0.91) and the new layout's own (its preset's, or yours)
                NotifyPresets();
            }
            catch (Exception ex) { logger.Warn("Kippix: background per layout: " + ex.Message); }
        }
    }
}

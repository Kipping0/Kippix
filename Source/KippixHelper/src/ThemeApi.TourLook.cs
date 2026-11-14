using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.97) Getting started › Pick a starting look. The layouts (Row, Grid, List, Board, Manual board, Folder); A on one picks it
    /// and shows its rows: Row's type (Lower, Tall, Full Screen), then its presets (Default and Kippix's designs). The highlight on
    /// any of them shows its picture (0.98: photos that come with the theme, Images\Welcome\Looks\&lt;slot&gt;_&lt;preset&gt;.jpg;
    /// one without a photo shows its drawing). Nothing changes behind until one is picked.
    /// </summary>
    public partial class ThemeApi
    {
        private string tourLookOpen;              // the layout whose type / preset rows show (null: all the layouts)
        private int tourCommitMode = -1, tourCommitDesign;
        private readonly Dictionary<string, string> tourPresetWas = new Dictionary<string, string>();   // slot → its preset before a preview
        private string tourPreviewDone;
        private ImageSource tourSnapshot;

        private static readonly string[] TourLooks = { "row", "grid", "list", "board", "manual", "folders" };
        private static int TourLookMode(string key)
        {
            switch (key) { case "grid": return 1; case "list": return 2; case "board": return 3; case "manual": return 4; case "folders": return 5; }
            return 0;
        }

        private string TourLookName(string key)
        {
            switch (key)
            {
                case "grid": return Loc("LOC_KX_LayoutGrid", "Grid");
                case "list": return Loc("LOC_KX_TourList", "List");
                case "board": return Loc("LOC_KX_LayoutBoard", "Board");
                case "manual": return Loc("LOC_KX_TourManual", "Manual board");
                case "folders": return Loc("LOC_KX_LayoutFolder", "Folder (WIP)");
            }
            return Loc("LOC_KX_TourRow", "Row");
        }

        private string TourLookSub(string key)
        {
            switch (key)
            {
                case "grid": return Loc("LOC_KX_TourGridSub", "Rows of covers that scroll down");
                case "list": return Loc("LOC_KX_TourListSub", "Games down one side with their play time");
                case "board": return Loc("LOC_KX_TourBoardSub", "Tiles of any size: games, widgets, pages");
                case "manual": return Loc("LOC_KX_TourManualSub", "Your own tiles, put where you want them, on pages");
                case "folders": return Loc("LOC_KX_TourFolderSub", "Folder tiles, each opening its own games");
            }
            return Loc("LOC_KX_TourRowSub2", "Covers in a row: low down, tall panels or full screen");
        }

        private string TourLookKeyOf(int mode) { return mode == 1 ? "grid" : mode == 2 ? "list" : mode == 3 ? "board" : mode == 4 ? "manual" : mode == 5 ? "folders" : "row"; }

        /// <summary>Coming to the step: what's on now is what's picked.</summary>
        private void TourLookStart()
        {
            tourCommitMode = settings.HomeLayoutMode;
            tourCommitDesign = settings.RowDesign;
            tourPresetWas.Clear();
            tourLookOpen = null;
            TourPickReset();   // (0.99.13: nothing picked when the step opens)
            tourSnapshot = null;
            tourPreviewDone = null;
            TourShotsCleanup();
        }

        /// <summary>Every layout; under the one opened (A), like a dropdown, its type (Row) and its presets.</summary>
        private void BuildTourLookRows(ObservableCollection<PickRow> rows)
        {
            foreach (string k in TourLooks)
            {
                string key = k;
                bool open = key == tourLookOpen;
                rows.Add(new PickRow
                {
                    Key = "look:" + key, Icon = key == "list" ? "menu" : key == "folders" ? "folder" : key == "row" ? "layout" : "grid",
                    Text = TourLookName(key), Sub = TourLookSub(key), IsSelected = TourLookMode(key) == tourCommitMode,
                    Command = new RelayCommand(_ => TourOpenLook(key))
                });
                if (open) TourLookDropdown(rows, key);
            }
        }

        private void TourLookDropdown(ObservableCollection<PickRow> rows, string open)
        {
            const double In = 56;
            if (open == "row")
            {
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_TourSecRowType", "TYPE") });
                string[] names = { Loc("LOC_KX_RowLower", "Lower"), Loc("LOC_KX_RowTall", "Tall"), Loc("LOC_KX_RowFull", "Full Screen") };
                string[] subs = { Loc("LOC_KX_TourRowSub", "Covers in a row near the bottom, the game's art filling the screen"), Loc("LOC_KX_TourTallSub", "Big portrait panels across the screen, the middle one picked"), Loc("LOC_KX_TourFullSub", "One big card in the middle, the others beside it") };
                for (int d = 0; d < 3; d++)
                {
                    int dd = d;
                    rows.Add(new PickRow { Key = "rt" + d, Icon = "layout", Indent = In, Text = names[d], Sub = subs[d], IsSelected = tourCommitMode == 0 && tourCommitDesign == d, Command = new RelayCommand(_ => TourPickRowType(dd)) });
                }
            }
            if (open == "folders") return;
            string slot = TourSlotNow();
            var st = slot != null ? SlotState(slot, false) : null;
            string chosen = st != null ? st.Chosen : "";
            bool looking = slot != null && tourPresetWas.ContainsKey(slot);   // (one only being looked at isn't ticked)
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_TourSecPreset", "PRESET") });
            rows.Add(new PickRow { Key = "ps0", Icon = "profile", Indent = In, Text = Loc("LOC_KX_PsDefault", "Default"), Sub = Loc("LOC_KX_PsDefaultSub", "Your own layout, as you had it"), IsSelected = chosen == "" && !looking, Command = new RelayCommand(_ => TourPickPreset(0)) });
            if (slot == null) return;
            foreach (int i in PresetDesignNumbers(slot))
            {
                int n = i;
                string name, sub;
                PresetInfo(slot, n, out name, out sub);
                rows.Add(new PickRow { Key = "ps" + n, Icon = "star", Indent = In, Text = name, Sub = sub, IsSelected = chosen == "kx" + n && !looking, Command = new RelayCommand(_ => TourPickPreset(n)) });
            }
        }

        /// <summary>The preset slot for what's on the home screen now (null: Folder).</summary>
        private string TourSlotNow()
        {
            int m = settings.HomeLayoutMode;
            if (m == 5) return null;
            if (m == 0) return "row" + Math.Max(0, Math.Min(2, settings.RowDesign));
            return "l" + m;
        }

        private static void TourSlotLayout(string slot, out int mode, out int design)
        {
            design = 0;
            if (slot.StartsWith("row", StringComparison.Ordinal)) { mode = 0; int.TryParse(slot.Substring(3), out design); return; }
            int.TryParse(slot.Substring(1), out mode);
        }

        // ---------------- picking ----------------

        private void TourOpenLook(string key)
        {
            if (tourLookOpen == key) { PlaySound("back"); TourLookBack(); return; }   // (A again: the dropdown closes)
            if (tourLookOpen != null) { tourLookOpen = null; TourRevert(); }
            int m = TourLookMode(key);
            TourApplyLayout(m, m == 0 ? (settings.HomeLayoutMode == 0 ? settings.RowDesign : 0) : 0);
            tourCommitMode = settings.HomeLayoutMode;
            tourCommitDesign = settings.RowDesign;
            PlaySound("select");
            if (key == "folders") { TourRowsAgain("look:folders"); TourPreviewSoon("look:folders"); return; }
            tourLookOpen = key;
            string slot = TourSlotNow();
            var st = slot != null ? SlotState(slot, false) : null;
            string focus = key == "row" ? "rt" + settings.RowDesign : "ps" + (st != null ? PresetNumberOf(st.Chosen) : 0);
            BuildWelcomeStep(focus);
            TourPreviewSoon(focus);
        }

        private void TourPickRowType(int d)
        {
            TourApplyLayout(0, d);
            tourCommitMode = 0;
            tourCommitDesign = d;
            PlaySound("select");
            BuildWelcomeStep("rt" + d);
            TourPreviewSoon("rt" + d);
        }

        private void TourPickPreset(int n)
        {
            TourApplyLayout(tourCommitMode, tourCommitDesign);
            string slot = TourSlotNow();
            if (slot == null) return;
            var st = SlotState(slot, false);
            string chosen = st != null ? st.Chosen : "";
            if (chosen != (n == 0 ? "" : "kx" + n)) UsePreset(slot, n);
            tourPresetWas.Remove(slot);   // (picked: it stays)
            tourCommitMode = settings.HomeLayoutMode;
            tourCommitDesign = settings.RowDesign;
            PlaySound("select");
            TourRowsAgain("ps" + n);
            TourPreviewSoon("ps" + n);
        }

        /// <summary>B on a look's rows (or Other looks): back to all the layouts, what was picked put back. True: handled.</summary>
        private bool TourLookBack()
        {
            if (openPanel != "welcome" || WelcomeSteps[welcomeStep] != "look" || tourLookOpen == null) return false;
            string was = tourLookOpen;
            tourLookOpen = null;
            TourRevert();
            BuildWelcomeStep("look:" + was);
            return true;
        }

        /// <summary>Leaving the step: anything only being looked at goes back to what was picked.</summary>
        private void TourLookLeave()
        {
            if (tourCommitMode < 0) return;
            TourRevert();
            tourLookOpen = null;
            tourCommitMode = -1;
            tourSnapshot = null;
        }

        private void TourRevert()
        {
            try
            {
                foreach (var kv in tourPresetWas.ToList())
                {
                    int m, d;
                    TourSlotLayout(kv.Key, out m, out d);
                    TourApplyLayout(m, d);
                    var st = SlotState(kv.Key, false);
                    int n = PresetNumberOf(kv.Value);
                    if (st != null && st.Chosen != (kv.Value ?? "")) UsePreset(kv.Key, n);
                }
                tourPresetWas.Clear();
                if (tourCommitMode >= 0) TourApplyLayout(tourCommitMode, tourCommitDesign);
                tourPreviewDone = null;
            }
            catch (Exception ex) { logger.Warn("Kippix: getting started (putting the look back): " + ex.Message); }
        }

        private void TourApplyLayout(int mode, int design)
        {
            if (settings.HomeLayoutMode == mode && (mode != 0 || settings.RowDesign == design)) return;
            muteTourSounds++;
            try { TourSetLayout(mode, design); }
            finally { muteTourSounds--; }
            BgFollowLayout();   // (its own settings and preset come out now, not at the next tick)
        }

        // ---------------- previews (0.98: photos) ----------------

        private int muteTourSounds;
        private string tourFocusKey;
        private readonly Dictionary<string, ImageSource> tourPhotos = new Dictionary<string, ImageSource>();

        /// <summary>The highlight came to a row: its photo (or, without one, its drawing). Nothing changes behind.</summary>
        private void TourPreviewSoon(string key)
        {
            tourFocusKey = key;
            if (key == null) return;
            string state = TourStateFor(key);
            var img = state != null ? TourPhoto(state) : null;
            if (img != null) { ShowTourShot(img); return; }
            ShowTourLook(TourDrawingFor(key));
        }

        /// <summary>The theme's photo of a look and preset ("row0|kx1" → Images\Welcome\Looks\row0_kx1.jpg), or null.</summary>
        private ImageSource TourPhoto(string state)
        {
            ImageSource img;
            if (tourPhotos.TryGetValue(state, out img)) return img;
            try
            {
                string f = System.IO.Path.Combine(ThemeDir, "Images", "Welcome", "Looks", state.Replace("|", "_") + ".jpg");
                img = System.IO.File.Exists(f) ? Images.LoadUncached(f, 1280) : null;
            }
            catch (Exception) { img = null; }
            tourPhotos[state] = img;
            return img;
        }

        private void ShowTourShot(ImageSource img)
        {
            tourSnapshot = img;
            tourLookShown = "snapshot";
            TourLookImage = img;
            Notify("TourLookImage");
            Notify("HasTourLookImage");
        }

        /// <summary>The home screen a row would show: its layout's preset slot and the preset ("row0|kx1", "l3|", "folders").</summary>
        private string TourStateFor(string key)
        {
            string slot;
            string chosen = null;
            if (key.StartsWith("look:", StringComparison.Ordinal))
            {
                string k = key.Substring(5);
                if (k == "folders") return "folders";
                int m = TourLookMode(k);
                slot = m == 0 ? "row" + (tourCommitMode == 0 ? tourCommitDesign : 0) : "l" + m;
            }
            else if (key.StartsWith("rt", StringComparison.Ordinal)) slot = "row" + key.Substring(2);
            else if (key.StartsWith("ps", StringComparison.Ordinal))
            {
                if (tourCommitMode == 5) return null;
                slot = tourCommitMode == 0 ? "row" + tourCommitDesign : "l" + tourCommitMode;
                int n;
                if (!int.TryParse(key.Substring(2), out n)) return null;
                chosen = n == 0 ? "" : "kx" + n;
            }
            else return null;
            if (chosen == null)
            {
                string was;
                var st = SlotState(slot, false);
                chosen = tourPresetWas.TryGetValue(slot, out was) ? (was ?? "") : st != null ? st.Chosen ?? "" : "";
            }
            return slot + "|" + chosen;
        }

        /// <summary>A row's drawing (Images\Welcome\look-*.png), for a look without a photo.</summary>
        private string TourDrawingFor(string key)
        {
            string k = key.StartsWith("look:", StringComparison.Ordinal) ? key.Substring(5)
                     : key == "rt1" ? "tall" : key == "rt2" ? "full" : key == "rt0" ? "row"
                     : tourLookOpen ?? "row";
            if (k == "row" && key.StartsWith("ps", StringComparison.Ordinal) && tourCommitMode == 0) k = tourCommitDesign == 1 ? "tall" : tourCommitDesign == 2 ? "full" : "row";
            if (k == "manual") k = "board";
            return "look-" + k;
        }

        /// <summary>(0.98) The pictures 0.97.2–0.97.3 took of your own screen aren't used any more: their folder goes, once.</summary>
        private void TourShotsCleanup()
        {
            try
            {
                string dir = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "tourshots");
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
            catch (Exception) { }
        }
    }
}

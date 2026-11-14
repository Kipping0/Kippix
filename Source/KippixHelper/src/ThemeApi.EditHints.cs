using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>One button (or key) in a hint: the theme's button icon, a key cap, the D-pad, or a "+" between two.</summary>
    public class HintGlyph
    {
        /// <summary>A, B, X, Y, LB, RB, LT, RT, Back, Start: drawn with the theme's ButtonPrompt icons (they follow the last device used).</summary>
        public string Prompt { get; set; }
        /// <summary>A keyboard key's name, drawn as a key cap.</summary>
        public string Cap { get; set; }
        /// <summary>The D-pad: "all", "ud" (up / down), "lr", or one arm "u", "d", "l", "r" lit.</summary>
        public string Dpad { get; set; }
        public bool IsPlus { get; set; }
        public bool IsPrompt { get { return !string.IsNullOrEmpty(Prompt); } }
        public bool IsCap { get { return !string.IsNullOrEmpty(Cap); } }
        public bool IsDpad { get { return !string.IsNullOrEmpty(Dpad); } }
        public bool ArmUp { get { return Dpad == "all" || Dpad == "ud" || Dpad == "u"; } }
        public bool ArmDown { get { return Dpad == "all" || Dpad == "ud" || Dpad == "d"; } }
        public bool ArmLeft { get { return Dpad == "all" || Dpad == "lr" || Dpad == "l"; } }
        public bool ArmRight { get { return Dpad == "all" || Dpad == "lr" || Dpad == "r"; } }
    }

    /// <summary>One hint in the Free edit / board edit bars: its buttons and what they do.</summary>
    public class HintItem
    {
        public List<HintGlyph> Glyphs { get; set; }
        public string Label { get; set; }
        public Thickness ItemMargin { get; set; }
        internal bool GroupStart;
    }

    /// <summary>Hints that belong together (move / size and turn / the rest): they wrap onto the next line together; a thin line before each but the first.</summary>
    public class HintGroup
    {
        public List<HintItem> Items { get; set; }
        public bool ShowDivider { get; set; }
    }

    /// <summary>
    /// The hint bars of Free move / Free edit (home screen and game page: pointing, moving, sizing, widgets, Layer / Opacity) and
    /// of editing the board (tiles, cropping, pasting) (0.79): a tidy row of hints, each the button's icon (Xbox / PlayStation /
    /// Switch / keyboard, the same set as the rest of the theme, following the last device used; on a keyboard the real key as a
    /// key cap) and a short label, evenly spaced (28 px), in groups (move / size and turn / the rest) split by a thin line,
    /// wrapping onto a second line when they don't fit. Rebuilt when what they describe changes (FreeHints, BoardHints).
    /// </summary>
    public partial class ThemeApi
    {
        private ObservableCollection<HintGroup> freeHints, boardHints;
        private string freeHintsSig, boardHintsSig;
        private bool hintsHooked, hintsPending;

        public ObservableCollection<HintGroup> FreeHints { get { HookHints(); if (freeHints == null) { freeHints = new ObservableCollection<HintGroup>(); HintsSoon(); } return freeHints; } }
        public ObservableCollection<HintGroup> BoardHints { get { HookHints(); if (boardHints == null) { boardHints = new ObservableCollection<HintGroup>(); HintsSoon(); } return boardHints; } }

        private static readonly HashSet<string> HintWatch = new HashSet<string>
        {
            "", "MasterMoveText", "BoardHintText", "IsFreeCursor", "IsSizing", "IsMovingNotSizing", "IsPlacing", "IsMasterMoving", "PickedCanDelete",
            "FreeCanDelete", "FreeHistoryText", "BoardEditing", "FreeCursorShown", "FreeCanLayer", "SizeXText", "MasterStepLabel", "PickModeHint", "ButtonIconsText"
        };

        private void HookHints()
        {
            if (hintsHooked) return;
            hintsHooked = true;
            PropertyChanged += OnHintsWatch;
        }

        private void OnHintsWatch(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || e.PropertyName == null || HintWatch.Contains(e.PropertyName)) HintsSoon();
        }

        private void HintsSoon()
        {
            if (hintsPending || ui == null) return;
            hintsPending = true;
            ui.BeginInvoke(new Action(() =>
            {
                hintsPending = false;
                try { RebuildHints(); }
                catch (Exception ex) { logger.Warn("Kippix: edit hints: " + ex.Message); }
            }), DispatcherPriority.DataBind);
        }

        /// <summary>The keyboard's keys are shown (Button icons: Keyboard, or Automatic and the keyboard was used last).</summary>
        private bool HintsKeyboard { get { return (settings.ButtonIcons == 0 ? promptKind : settings.ButtonIcons) == 4; } }

        private void RebuildHints()
        {
            bool kb = HintsKeyboard;
            if (freeHints != null && IsMasterMoving) Fill(freeHints, BuildFreeHints(kb), ref freeHintsSig);
            if (boardHints != null && boardEditing) Fill(boardHints, BuildBoardHints(kb), ref boardHintsSig);
        }

        private static void Fill(ObservableCollection<HintGroup> into, List<HintItem> items, ref string sig)
        {
            var sb = new StringBuilder();
            foreach (var h in items)
            {
                sb.Append(h.GroupStart ? '|' : ',').Append(h.Label).Append(':');
                foreach (var g in h.Glyphs) sb.Append(g.Prompt).Append('/').Append(g.Cap).Append('/').Append(g.Dpad).Append(g.IsPlus ? "+" : "").Append(';');
            }
            string s = sb.ToString();
            if (s == sig) return;
            sig = s;
            var groups = new List<HintGroup>();
            foreach (var h in items)
            {
                if (groups.Count == 0 || h.GroupStart) groups.Add(new HintGroup { Items = new List<HintItem>(), ShowDivider = groups.Count > 0 });
                groups[groups.Count - 1].Items.Add(h);
            }
            foreach (var g in groups)
                for (int i = 0; i < g.Items.Count; i++) g.Items[i].ItemMargin = new Thickness(0, 4, i == g.Items.Count - 1 ? 0 : 28, 4);   // 28 px between hints
            into.Clear();
            foreach (var g in groups) into.Add(g);
        }

        /// <summary>Makes the hints: Add(label, controller buttons, keyboard keys). Buttons: "A", "LB RB", "Dpad", "Dpad:ud", "RB + Dpad".
        /// Keys: "Enter", "Ctrl+Z Ctrl+Y", "Arrows", "Arrows:ud", "Shift + Arrows" (null: the controller's, in the theme's keyboard icons).</summary>
        private class HintMaker
        {
            public readonly List<HintItem> Items = new List<HintItem>();
            private readonly bool kb;
            private bool group;
            public HintMaker(bool keyboard) { kb = keyboard; }
            public void Group() { if (Items.Count > 0) group = true; }
            public void Add(string label, string pad, string key)
            {
                if (string.IsNullOrEmpty(label)) return;
                var glyphs = new List<HintGlyph>();
                bool keys = kb && key != null;
                foreach (string tok in (keys ? key : pad).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (tok == "+") { glyphs.Add(new HintGlyph { IsPlus = true }); continue; }
                    if (tok.StartsWith("Dpad", StringComparison.Ordinal)) { glyphs.Add(new HintGlyph { Dpad = tok.Length > 5 ? tok.Substring(5) : "all" }); continue; }
                    if (keys)
                    {
                        string cap = tok == "Arrows" ? "← ↑ → ↓" : tok == "Arrows:ud" ? "↑ ↓" : tok == "Arrows:lr" ? "← →" : tok;
                        glyphs.Add(new HintGlyph { Cap = cap });
                    }
                    else glyphs.Add(new HintGlyph { Prompt = tok });
                }
                Items.Add(new HintItem { Glyphs = glyphs, Label = label, GroupStart = group });
                group = false;
            }
        }

        // ---------------- Free move / Free edit (home screen, game page) ----------------

        private List<HintItem> BuildFreeHints(bool kb)
        {
            var h = new HintMaker(kb);
            if (PickModeActive) { PickModeHints(h); return h.Items; }
            if (IsFreeCursor)
            {
                // (0.99.1: the same order as the board's editing)
                h.Add(freePrecise ? Loc("LOC_KX_HintPoint", "Point") : Loc("LOC_KX_HintChoose", "Choose"), "Dpad", "Arrows");
                h.Add(Loc("LOC_KX_HintPickUp", "Pick up"), "A", "Enter");
                h.Group();
                h.Add(Loc("LOC_KX_HintSettings", "Settings"), "X", "S");
                h.Add(Loc("LOC_KX_HintResize", "Resize"), "Y", "Z");
                if (FreeCanDelete) h.Add(Loc("LOC_KX_HintDelete", "Delete"), "Back", "Del");
                if (FreeCanLayer) h.Add(Loc("LOC_KX_HintLayerBackFwd", "Layer back / forward"), "LT RT", "PgUp PgDn");   // (0.99.2)
                h.Group();
                h.Add(freePrecise ? (freeFromBoardEdit ? Loc("LOC_KX_HintBackToBoard", "Back to the board") : Loc("LOC_KX_HintSnapEdit", "Normal edit")) : Loc("LOC_KX_HintPrecise", "Precise edit"), "Start", "Tab");
                h.Add(FreeHistoryText, "LB RB", "Ctrl+Z Ctrl+Y");
                h.Add(Loc("LOC_KX_HintDone", "Done"), "B", "Esc");
                return h.Items;
            }
            if (widgetMoving != null)
            {
                h.Add(Loc("LOC_KX_HintMove", "Move"), "Dpad", "Arrows");
                h.Add(Loc("LOC_KX_HintGridFree", "Grid / free"), "X", "G");
                h.Group();
                h.Add(Loc("LOC_KX_HintSmallerBigger", "Smaller / bigger"), "LB RB", "PgUp PgDn");
                h.Add(Loc("LOC_KX_HintRotate", "Rotate"), "LT RT", "Q E");
                h.Add(Loc("LOC_KX_HintOwnSize", "Its own size"), "Y", "R");
                h.Group();
                PickedEndHints(h);
                return h.Items;
            }
            if (masterMoving == null) return h.Items;
            if (sizing)
            {
                h.Add(Loc("LOC_KX_HintSize", "Size"), "Dpad", "Arrows");
                h.Add(Loc("LOC_KX_HintStep", "Step size"), "LB RB", "PgUp PgDn");
                string key = masterMoving.Key;
                if (key == SimilarKey || !(IsScaleKey(key) || (key == "home.cards" && LayoutNow != 2))) h.Add(SizeXText, "X", "Tab");
                h.Group();
                h.Add(Loc("LOC_KX_HintRotate", "Rotate"), "LT RT", "Q E");
                h.Add(Loc("LOC_KX_HintOwnSize", "Its own size"), "Y", "R");
                h.Group();
                PickedEndHints(h);
                return h.Items;
            }
            h.Add(Loc("LOC_KX_HintMove", "Move"), "Dpad", "Arrows");
            h.Add(Loc("LOC_KX_HintStep", "Step size"), "LB RB", "PgUp PgDn");
            h.Add(Loc("LOC_KX_HintGridFree", "Grid / free"), "X", "G");
            h.Group();
            h.Add(Loc("LOC_KX_HintRotate", "Rotate"), "LT RT", "Q E");
            h.Add(Loc("LOC_KX_HintReset", "Reset"), "Y", "R");
            h.Group();
            PickedEndHints(h);
            return h.Items;
        }

        /// <summary>The button that goes Move → Layer → Opacity: Start (Menu), or X on the board (Start crops there, 0.80).</summary>
        private string PickModeButton { get { return boardPicked != null && masterMoving == null && widgetMoving == null ? "X" : "Start"; } }

        /// <summary>The end of the row while something is picked up: Layer / opacity, Crop (Manual board), Delete (when it can be), Put down, Cancel.</summary>
        private void PickedEndHints(HintMaker h)
        {
            h.Add(Loc("LOC_KX_HintLayer", "Layer / opacity"), PickModeButton, "M");
            BoardCropHint(h);
            if (PickedCanDelete || boardPicked != null) h.Add(Loc("LOC_KX_HintDelete", "Delete"), "Back", "Del");
            h.Add(Loc("LOC_KX_HintPutDown", "Put down"), "A", "Enter");
            h.Add(Loc("LOC_KX_HintCancel", "Cancel"), "B", "Esc");
        }

        /// <summary>Menu while something is picked up: Layer or Opacity.</summary>
        private void PickModeHints(HintMaker h)
        {
            if (pickMode == 1)
            {
                h.Add(Loc("LOC_KX_HintForwardBack", "Forward / backward"), "Dpad:ud", "Arrows:ud");
                h.Add(Loc("LOC_KX_HintFrontBack", "To the front / back"), "Dpad:lr", "Arrows:lr");
                h.Group();
                h.Add(Loc("LOC_KX_HintOpacity", "Opacity"), PickModeButton, "M");
            }
            else
            {
                h.Add(Loc("LOC_KX_HintMoreLess", "More / less"), "Dpad", "Arrows");
                h.Group();
                h.Add(Loc("LOC_KX_HintBackToMoving", "Back to moving"), PickModeButton, "M");
            }
            BoardCropHint(h);
            h.Group();
            h.Add(Loc("LOC_KX_HintPutDown", "Put down"), "A", "Enter");
            h.Add(Loc("LOC_KX_HintCancel", "Cancel"), "B", "Esc");
        }

        // ---------------- editing the board ----------------

        /// <summary>Start: Crop (Manual board only, 0.81: only while a tile is picked up; it stays picked up).</summary>
        private void BoardCropHint(HintMaker h)
        {
            if (!boardEditing || boardPicked == null || masterMoving != null || widgetMoving != null || !IsManualMode || !IsBoardMode || boardPasting) return;
            h.Add(Loc("LOC_KX_HintCrop", "Crop"), "Start", "C");
        }

        private List<HintItem> BuildBoardHints(bool kb)
        {
            var h = new HintMaker(kb);
            if (cropTile != null)
            {
                h.Add(Loc("LOC_KX_HintTrim", "Trim that side"), "Dpad", "Arrows");
                h.Add(Loc("LOC_KX_HintGrow", "Hold: grow it back"), "RB + Dpad", "Shift + Arrows");
                h.Group();
                h.Add(Loc("LOC_KX_HintStepSmallerBigger", "Smaller / bigger steps"), "LT RT", "PgUp PgDn");
                h.Group();
                h.Add(Loc("LOC_KX_HintNoCrop", "No crop"), "Y", "R");
                h.Add(Loc("LOC_KX_HintDone", "Done"), "A", "Enter");
                h.Add(Loc("LOC_KX_HintCancel", "Cancel"), "B", "Esc");
                return h.Items;
            }
            if (boardPicked != null && PickModeActive) { PickModeHints(h); return h.Items; }
            if (boardPicked != null)
            {
                // (sizing grows from its top left; a circle / hexagon / triangle both ways at once, keeping its shape)
                h.Add(boardResizing ? Loc("LOC_KX_HintSizeDirs", "Size: → ↓ bigger, ← ↑ smaller") : Loc("LOC_KX_HintMove", "Move"), "Dpad", "Arrows");
                h.Add(Loc("LOC_KX_HintStep", "Step size"), "LB RB", "PgUp PgDn");
                h.Add(boardResizing ? Loc("LOC_KX_HintMoveIt", "Move it") : Loc("LOC_KX_HintResize", "Resize"), "Y", "Z");
                h.Group();
                h.Add(Loc("LOC_KX_HintRotate", "Rotate"), "LT RT", "Q E");
                h.Group();
                PickedEndHints(h);
                return h.Items;
            }
            if (boardGrouping != null)
            {
                h.Add(Loc("LOC_KX_HintChooseTile", "Choose a tile"), "Dpad", "Arrows");
                if (boardOrdering) h.Add(Loc("LOC_KX_HintOrderToggle", "Give it the next number / take it off"), "A", "Enter");
                else h.Add(Loc("LOC_KX_HintGroupToggle", "In / out of the group"), "A", "Enter");
                h.Group();
                h.Add(Loc("LOC_KX_HintDone", "Done"), "B", "Esc");
                return h.Items;
            }
            if (boardPasting)
            {
                h.Add(Loc("LOC_KX_HintChooseTile", "Choose a tile"), "Dpad", "Arrows");
                h.Add(Loc("LOC_KX_HintPasteHere", "Paste here"), "A", "Enter");
                h.Group();
                h.Add(Loc("LOC_KX_HintDone", "Done"), "B", "Esc");
                return h.Items;
            }
            h.Add(Loc("LOC_KX_HintChooseTile", "Choose a tile"), "Dpad", "Arrows");
            h.Add(Loc("LOC_KX_HintAddPick", "Add / pick up"), "A", "Enter");
            h.Group();
            h.Add(Loc("LOC_KX_HintTileSettings", "Tile settings"), "X", "S");
            h.Add(Loc("LOC_KX_HintRemove", "Remove"), "Y", "Del");
            if (IsManualMode) h.Add(Loc("LOC_KX_HintLayerBackFwd", "Layer back / forward"), "LT RT", "PgUp PgDn");   // (0.99.2; the Board: tiles can't overlap)
            h.Group();
            h.Add(Loc("LOC_KX_HintPrecise", "Precise edit"), "Start", "Tab");   // (0.99.1)
            h.Add(FreeHistoryText, "LB RB", "Ctrl+Z Ctrl+Y");
            h.Add(Loc("LOC_KX_HintDone", "Done"), "B", "Esc");
            return h.Items;
        }
    }
}

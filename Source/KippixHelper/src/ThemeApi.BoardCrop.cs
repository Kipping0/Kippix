using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// Manual board: cropping a tile (0.78, redone in 0.79). Tile settings (X) › Crop, or (0.81) Start (C on a keyboard) while a
    /// tile is picked up: it stays picked up, and A / B go back to moving it (0.80 put it down first, and Start worked on the
    /// highlighted tile too; the hint only shows with one picked up now).
    ///   Steps (0.81, LT / RT): 1, 5, 10 and 25 px (each press moves the edge exactly that far), then ¼, ½ and 1 square on a grid
    ///   (the edge lands on the step). Starts at 10 px.
    ///   The picture (the tile's settings › Crop › Picture): fills what's left, centred (the default again since 0.82), or cut
    ///   where you trimmed (it stays at the whole tile's size and place).
    ///   - The D-pad trims that side (Up the top, Down the bottom, Left the left, Right the right); holding RB (or LB, or Shift on a
    ///     keyboard) with the D-pad grows that side back out. LT / RT (PgUp / PgDn; Playnite sends the triggers as those) make the step smaller / bigger
    ///     (down to 1 px, 0.80); on a grid the edges land on the step (so trimming frees whole squares). Y takes the crop off, A keeps it, B puts back what it was.
    ///     While cropping, the whole tile's shape shows faintly with a dashed line round it, and a D-pad badge on each edge.
    ///   - The tile IS the part that's left: since 0.79 X / Y / W / H are the cropped part (what it takes up on the board: other
    ///     tiles can go where the crop freed room; the highlight, the outline and the empty squares follow it) and CropBox
    ///     [left, top, right, bottom] says how much of the whole tile was cut off each side (its shape is the whole shape cut by
    ///     the crop). Since 0.80 its picture fills the part that's left (uniform to fill, centred on it, decoded for that size);
    ///     before, it stayed laid out at the whole tile's size and place. Moving or sizing a cropped tile moves or
    ///     sizes the part that's left. 0.78's Crop (X / Y / W / H were the whole tile) is turned into this
    ///     when settings load (MigrateOldCrop).
    ///   - Copy crop / Paste crop (and to all tiles of the same shape) in the tile's settings; a tile's "Copy this tile's
    ///     settings" carries the crop too when the tile it's pasted on is the same shape. Undo / redo cover all of it.
    ///   Grid placement: growing a side (or taking the crop off) never goes over another tile.
    /// </summary>
    public partial class ThemeApi
    {
        private const double CropMax = 0.95, CropKeep = 0.05;
        private BoardTile cropTile;
        private double[] cropStartBox;     // X, Y, W, H when cropping started (B puts it back)
        private double[] cropStartCrop;
        private Rect cropFull;             // the whole tile in grid squares (fixed while cropping)
        private bool cropGrow;             // RB / LB held: the D-pad grows a side instead of trimming it
        private int cropStepIdx = -1;      // the step (LT / RT); -1: as it comes
        private string cropNote;           // "Another tile is in the way" for a moment
        private DateTime cropNoteAt = DateTime.MinValue;
        private DispatcherTimer cropNoteTimer;
        private DateTime cropEndedAt = DateTime.MinValue;   // A ended cropping: the Enter Playnite sends for that A isn't another press
        private double[] cropClip;         // Copy crop
        private int cropClipShape = -1;
        private bool cropClipFill;
        private bool cropFromPick;         // cropping the tile that's picked up (Start): A / B go back to moving it
        private DateTime cropStartedAt = DateTime.MinValue;

        /// <summary>
        /// 0.82: cropping only ends by a press made after it started. Cropping from Start closed again straight away for you:
        /// something that came with the press (most likely the key Steam's Desktop Layout makes for Start, or an Enter / Escape
        /// sent with it) ended it. For the first moment nothing ends it; what tried is logged.
        /// </summary>
        private bool CropJustStarted(string what)
        {
            if ((DateTime.Now - cropStartedAt).TotalMilliseconds >= 700) return false;   // (Steam's copy of a press can be held back up to 350 ms first)
            logger.Info("Kippix: crop: " + what + " right after cropping started, ignored");
            return true;
        }

        // ---------------- what a crop is ----------------

        /// <summary>A crop that can be used: four numbers in range that leave at least a little of the tile; null otherwise (or none).</summary>
        private static double[] CleanCrop(double[] c)
        {
            if (c == null || c.Length != 4) return null;
            var r = new double[4];
            bool any = false;
            for (int i = 0; i < 4; i++)
            {
                double v = double.IsNaN(c[i]) || double.IsInfinity(c[i]) ? 0 : Math.Max(0, Math.Min(CropMax, Math.Round(c[i], 6)));
                r[i] = v;
                if (v > 0.0005) any = true;
            }
            if (!any || 1 - r[0] - r[2] < CropKeep - 0.0005 || 1 - r[1] - r[3] < CropKeep - 0.0005) return null;
            return r;
        }

        /// <summary>A card's crop (Manual board only).</summary>
        private double[] CardCrop(BoardCard c)
        {
            if (c == null || !IsManualMode) return null;
            return CleanCrop(c.CropBox);
        }

        /// <summary>The tile's crop (Manual board only).</summary>
        private double[] BoardCropOf(BoardTile t)
        {
            if (t == null || t.IsSlot || t.Card == null) return null;
            return CardCrop(t.Card);
        }

        /// <summary>The whole tile (grid squares) from the part that's left and the crop.</summary>
        private static Rect CropFullOf(Rect foot, double[] crop)
        {
            if (crop == null) return foot;
            double fw = foot.Width / Math.Max(CropKeep, 1 - crop[0] - crop[2]), fh = foot.Height / Math.Max(CropKeep, 1 - crop[1] - crop[3]);
            return new Rect(foot.X - crop[0] * fw, foot.Y - crop[1] * fh, fw, fh);
        }

        /// <summary>The crop that leaves foot of the whole tile full (null: nothing cut off).</summary>
        private static double[] CropOfRects(Rect full, Rect foot)
        {
            if (full.Width <= 0 || full.Height <= 0) return null;
            return CleanCrop(new[] { (foot.X - full.X) / full.Width, (foot.Y - full.Y) / full.Height, (full.Right - foot.Right) / full.Width, (full.Bottom - foot.Bottom) / full.Height });
        }

        private static Rect CardBox(BoardCard c) { return new Rect(c.X, c.Y, Math.Max(0.01, c.W), Math.Max(0.01, c.H)); }
        private static void SetCardBox(BoardCard c, Rect r) { c.X = r.X; c.Y = r.Y; c.W = r.Width; c.H = r.Height; }

        /// <summary>A box of grid squares on screen, the cut shapes keeping their own proportions, centred in it (pieces use the whole box).</summary>
        private Rect BoardShapeRectOf(Rect q, BoardCard c)
        {
            var r = BoardRect(q.X, q.Y, q.Width, q.Height);
            if (!CardShapes.IsCut(c.Shape) || IsPieceKind(c) || c.Kind == "shortcut" || c.Kind == "library") return r;
            int aw, ah;
            CardShapes.Aspect(c.Shape, out aw, out ah);
            double k = Math.Min(r.Width / aw, r.Height / ah);
            double w = aw * k, h = ah * k;
            return new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }

        /// <summary>The same in grid squares (where a cut shape's own outline starts, inside the whole tile): an edge cropped to it
        /// lands exactly on the outline on screen (the gap between tiles taken into account).</summary>
        private Rect ShapeCellsOf(Rect f, BoardCard c)
        {
            if (!CardShapes.IsCut(EffShape(c))) return f;
            double cw = BoardAreaW / BCols, ch = BoardAreaH / BRows, g = BoardGap / 2;
            var px = BoardShapeRectOf(f, c);
            var r = new Rect((px.X - g) / cw, (px.Y - g) / ch, (px.Width + 2 * g) / cw, (px.Height + 2 * g) / ch);
            return Rect.Intersect(r, f).IsEmpty ? f : Rect.Intersect(r, f);
        }

        /// <summary>A cropped tile on screen: the whole tile's shape box (where its picture is laid out) and the part that shows. False when it isn't cropped.</summary>
        private bool BoardCropRects(BoardCard c, out Rect full, out Rect vis)
        {
            full = Rect.Empty; vis = Rect.Empty;
            var crop = CardCrop(c);
            if (crop == null) return false;
            var foot = BoardCells(c);
            full = BoardShapeRectOf(CropFullOf(foot, crop), c);
            var box = BoardRect(foot.X, foot.Y, foot.Width, foot.Height);
            vis = Rect.Intersect(full, box);
            if (vis.IsEmpty || vis.Width < 4 || vis.Height < 4) vis = box;
            return true;
        }

        /// <summary>The whole shape at the whole tile's size (0, 0 at its top left).</summary>
        private static Geometry BoardFullShape(int s, double fullW, double fullH)
        {
            try
            {
                var g = CardShapes.IsCut(s) ? CardShapes.Sized(s, fullW, fullH) : CardShapes.Rounded(fullW, fullH, 10);
                if (g != null) return g;
            }
            catch (Exception) { }
            var r = new RectangleGeometry(new Rect(0, 0, Math.Max(1, fullW), Math.Max(1, fullH)));
            r.Freeze();
            return r;
        }

        /// <summary>The whole shape cut to the part that shows, in that part's own space (it starts offL, offT into the whole tile).</summary>
        private static Geometry BoardCroppedShape(int s, double fullW, double fullH, double offL, double offT, double w, double h)
        {
            try
            {
                var g = BoardFullShape(s, fullW, fullH).Clone();
                g.Transform = new TranslateTransform(-offL, -offT);
                var res = Geometry.Combine(g, new RectangleGeometry(new Rect(0, 0, w, h)), GeometryCombineMode.Intersect, null);
                res.Freeze();
                return res;
            }
            catch (Exception) { return new RectangleGeometry(new Rect(0, 0, w, h)); }
        }

        private static int EffShape(BoardCard c)
        {
            return c == null || IsPieceKind(c) || c.Kind == "shortcut" || c.Kind == "library" ? CardShapes.Square : c.Shape;
        }

        private string CropText(double[] c)
        {
            c = CleanCrop(c);
            if (c == null) return Loc("LOC_KX_CropNone", "Not cropped");
            Func<double, string> pc = v => Math.Round(v * 100) + "%";
            return string.Format(Loc("LOC_KX_CropSides", "Top {0}, right {1}, bottom {2}, left {3}"), pc(c[1]), pc(c[2]), pc(c[3]), pc(c[0]));
        }

        private static bool SameCrop(double[] a, double[] b)
        {
            a = CleanCrop(a); b = CleanCrop(b);
            if (a == null || b == null) return a == null && b == null;
            for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - b[i]) > 0.0005) return false;
            return true;
        }

        /// <summary>Settings loaded: a 0.78 crop (X / Y / W / H the whole tile) becomes a 0.79 one (X / Y / W / H the part that's left).</summary>
        private static void MigrateOldCrop(BoardCard c)
        {
            if (c == null) return;
            if (c.Crop != null)
            {
                var full = CardBox(c);
                double[] raw = c.Crop.Length == 4 ? new[] { Math.Min(0.45, c.Crop[0]), Math.Min(0.45, c.Crop[1]), Math.Min(0.45, c.Crop[2]), Math.Min(0.45, c.Crop[3]) } : null;
                if (raw != null)
                {
                    // (the part that's left stays at least ¼ square: NormalizeSettings makes W / H at least that on every load,
                    // which would otherwise grow the whole tile the next time)
                    FitCropKept(raw, 0, 2, full.Width);
                    FitCropKept(raw, 1, 3, full.Height);
                }
                var old = raw != null ? CleanCrop(raw) : null;
                c.Crop = null;
                if (old != null && c.CropBox == null)
                {
                    SetCardBox(c, new Rect(full.X + old[0] * full.Width, full.Y + old[1] * full.Height,
                                           full.Width * (1 - old[0] - old[2]), full.Height * (1 - old[1] - old[3])));
                    c.CropBox = old;
                }
            }
            c.CropBox = CleanCrop(c.CropBox);
        }

        /// <summary>A 0.78 crop's two sides on one axis made smaller (in proportion) so at least ¼ square of the tile is left.</summary>
        private static void FitCropKept(double[] a, int i, int j, double size)
        {
            double cut = a[i] + a[j];
            if (!(cut > 0) || !(size >= 0.25) || size * (1 - cut) >= 0.25 - 1e-9) return;
            double k = Math.Max(0, 1 - 0.25 / size) / cut;
            a[i] *= k;
            a[j] *= k;
        }

        /// <summary>A tile takes another crop (or none): the whole tile stays where it is, the part that's left changes. Grid placement:
        /// refused (false, nothing changed) if that would go over another tile.</summary>
        private bool SetCardCrop(BoardCard c, double[] crop)
        {
            if (c == null) return false;
            var was = CardBox(c);
            var wasCrop = c.CropBox;
            var full = CropFullOf(was, CleanCrop(c.CropBox));
            crop = CleanCrop(crop);
            var foot = crop == null ? full : new Rect(full.X + crop[0] * full.Width, full.Y + crop[1] * full.Height,
                                                      full.Width * (1 - crop[0] - crop[2]), full.Height * (1 - crop[1] - crop[3]));
            // kept on the board (a whole tile past the edge keeps the part that fits)
            foot = Rect.Intersect(foot, new Rect(BMinX, BMinY, BMaxX - BMinX, BMaxY - BMinY));
            if (foot.IsEmpty || foot.Width < 0.2 || foot.Height < 0.2) return false;
            SetCardBox(c, foot);
            c.CropBox = CropOfRects(full, foot);
            if (!IsFreeMode && BCards.Contains(c) && UnderOf(c).Count > 0)
            {
                SetCardBox(c, was);
                c.CropBox = wasCrop;
                return false;
            }
            return true;
        }

        private void NoteInTheWay()
        {
            ShowNotice(Loc("LOC_KX_CropBlockedHead", "Crop"), Loc("LOC_KX_CropBlocked", "Another tile is in the way: move it first"), null, null, null);
        }

        /// <summary>A change made from a tile's settings: saved, shown, and an undo step (also when the board isn't being edited).</summary>
        private bool BoardStep(Func<bool> change)
        {
            EditBegin();   // (the board as it is, before the change; editing already: noted then)
            bool ok = change();
            if (ok) BoardChanged(false);
            EditCheckpointAfterPick();
            return ok;
        }

        // ---------------- the tile's settings ----------------

        private void AddCropRows(ObservableCollection<PickRow> rows, BoardCard c, Action again)
        {
            if (!IsManualMode || c == null) return;
            var crop = CleanCrop(c.CropBox);
            int shape = EffShape(c);
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecCrop", "CROP") });
            rows.Add(new PickRow
            {
                Key = "crop", Icon = "shape", Text = Loc("LOC_KX_Crop", "Crop"), Sub = crop == null ? Loc("LOC_KX_CropStartSub", "Trim its sides with the D-pad") : CropText(crop),
                Command = new RelayCommand(_ => StartCrop(c))
            });
            if (crop != null)
            {
                rows.Add(new PickRow
                {
                    Key = "cropoff", Icon = "remove", Text = Loc("LOC_KX_CropRemove", "Take the crop off"), Sub = Loc("LOC_KX_CropRemoveSub", "The whole tile again"),
                    Command = new RelayCommand(_ =>
                    {
                        if (!BoardStep(() => SetCardCrop(c, null))) { PlaySound("back"); NoteInTheWay(); return; }
                        PlaySound("move");
                        BoardRowsAgain(again, "crop");
                    })
                });
                rows.Add(new PickRow
                {
                    Key = "cropfill", Icon = "picture",
                    Text = c.CropCut ? Loc("LOC_KX_CropPicCut", "Picture: Cut where you trimmed") : Loc("LOC_KX_CropPicFill2", "Picture: Fills what's left, centred"),
                    Sub = c.CropCut ? Loc("LOC_KX_CropPicCutSub", "What you trimmed is gone from the picture; the rest stays where it was")
                                    : Loc("LOC_KX_CropPicFillSub", "The whole picture is fitted into the part that's left, centred on it"),
                    Command = new RelayCommand(_ => { BoardStep(() => { c.CropCut = !c.CropCut; return true; }); PlaySound("move"); BoardRowsAgain(again, "cropfill"); })
                });
                rows.Add(new PickRow
                {
                    Key = "cropcopy", Icon = "copy", Text = Loc("LOC_KX_CropCopy", "Copy crop"),
                    Sub = cropClip != null && SameCrop(cropClip, crop) && cropClipShape == shape ? Loc("LOC_KX_CropCopied", "Copied ✓  Paste it on another tile of the same shape") : Loc("LOC_KX_CropCopySub", "To paste on other tiles of the same shape"),
                    Command = new RelayCommand(_ => { cropClip = (double[])crop.Clone(); cropClipShape = shape; cropClipFill = c.CropCut; PlaySound("select"); BoardRowsAgain(again, "cropcopy"); })
                });
            }
            if (cropClip == null) return;
            if (cropClipShape != shape)
            {
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "cropother", Text = string.Format(Loc("LOC_KX_CropOtherShape", "The copied crop is for {0} tiles"), ShapeName(cropClipShape)) });
                return;
            }
            if (!SameCrop(cropClip, crop))
                rows.Add(new PickRow
                {
                    Key = "croppaste", Icon = "paste", Text = Loc("LOC_KX_CropPaste", "Paste crop"), Sub = CropText(cropClip),
                    Command = new RelayCommand(_ =>
                    {
                        if (!BoardStep(() => { if (!SetCardCrop(c, (double[])cropClip.Clone())) return false; c.CropCut = cropClipFill; return true; })) { PlaySound("back"); NoteInTheWay(); return; }
                        PlaySound("select");
                        BoardRowsAgain(again, "croppaste");
                    })
                });
            int same = BCards.Count(o => o != null && EffShape(o) == shape);
            rows.Add(new PickRow
            {
                Key = "croppasteall", Icon = "paste", Text = string.Format(Loc("LOC_KX_CropPasteAll", "Paste crop to all tiles of this shape ({0})"), same),
                Sub = string.Format(Loc("LOC_KX_CropPasteAllSub", "Every {0} tile on this board: {1}"), ShapeName(shape), CropText(cropClip)),
                Command = new RelayCommand(_ =>
                {
                    int skipped = 0;
                    BoardStep(() =>
                    {
                        foreach (var o in BCards.Where(o => o != null && EffShape(o) == shape).ToList())
                            if (!SetCardCrop(o, (double[])cropClip.Clone())) skipped++; else o.CropCut = cropClipFill;
                        return true;
                    });
                    PlaySound("select");
                    if (skipped > 0)
                        ShowNotice(Loc("LOC_KX_CropBlockedHead", "Crop"), string.Format(Loc("LOC_KX_CropSkipped", "{0} tiles kept their crop: another tile was in the way"), skipped), null, null, null);
                    BoardRowsAgain(again, "croppasteall");
                })
            });
        }

        // ---------------- cropping ----------------

        private void StartCrop(BoardCard c)
        {
            if (!IsManualMode || c == null) { PlaySound("back"); return; }
            PlaySound("select");
            boardCardFromBoard = false;
            ClosePanels();
            ui.BeginInvoke(new Action(() =>
            {
                if (!IsBoardMode || !IsManualMode) return;
                if (!boardEditing) BoardStartEdit(null);
                if (boardPicked != null) BoardCancelPick();
                var t = BoardTiles.FirstOrDefault(x => x.IsCard && ReferenceEquals(x.Card, c));
                if (t == null) { FocusBoard(); return; }
                cropStartBox = new[] { c.X, c.Y, c.W, c.H };
                cropStartCrop = c.CropBox;
                SetCardBox(c, BoardCells(c));   // (where it really is on the grid)
                cropFull = CropFullOf(CardBox(c), CardCrop(c));
                cropGrow = false;
                cropNote = null;
                cropFromPick = false;
                cropStartedAt = DateTime.Now;
                cropTile = t;
                CropShow();
                FocusBoardTile(t);
            }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// Start (C on a keyboard) while a tile is picked up on the Manual board: crop it there and then (it stays picked up; A keeps
        /// the crop, B puts it back, and either way you're moving it again). Nothing picked up: nothing (0.81).
        /// </summary>
        private void BoardCropShortcut()
        {
            var t = boardPicked;
            if (!IsManualMode || boardPasting || cropTile != null || t == null || !t.IsCard || t.Card == null) { PlaySound("back"); return; }
            var c = t.Card;
            PlaySound("select");
            cropStartBox = new[] { c.X, c.Y, c.W, c.H };
            cropStartCrop = c.CropBox;
            SetCardBox(c, BoardCells(c));   // (where it really is on the grid)
            cropFull = CropFullOf(CardBox(c), CardCrop(c));
            cropGrow = false;
            cropNote = null;
            cropFromPick = true;
            cropStartedAt = DateTime.Now;
            cropTile = t;
            UpdateRotTimer();   // (LT / RT are the step now, not turning it)
            CropShow();
            FocusBoardTile(t);
        }

        /// <summary>The steps LT / RT go through (0.81): below 0 that many pixels on screen (each press moves the edge exactly that far);
        /// above 0 grid squares (on a grid: the edge lands on the step).</summary>
        private double[] CropStepChoices { get { return IsFreeMode ? new double[] { -1, -5, -10, -25, -50 } : new double[] { -1, -5, -10, -25, 0.25, 0.5, 1 }; } }
        private const double CropStepDefault = -10;
        private bool CropStepIsPx { get { return CropStepNow < 0; } }
        private double CropStepX { get { double s = CropStepNow; return s < 0 ? -s * BCols / BoardAreaW : s; } }
        private double CropStepY { get { double s = CropStepNow; return s < 0 ? -s * BRows / BoardAreaH : s; } }

        private double CropStepNow
        {
            get
            {
                var ch = CropStepChoices;
                if (cropStepIdx >= 0) return ch[Math.Min(cropStepIdx, ch.Length - 1)];
                return CropStepDefault;
            }
        }

        private void CropChangeStep(int dir)
        {
            var ch = CropStepChoices;
            int i = Array.IndexOf(ch, CropStepNow);
            if (i < 0) i = Array.IndexOf(ch, CropStepDefault);
            int ni = Math.Max(0, Math.Min(ch.Length - 1, i + dir));
            if (ni == i) { PlaySound("back"); return; }
            cropStepIdx = ni;
            PlaySound("move");
            CropNotify();
        }

        private string CropStepText
        {
            get
            {
                double s = CropStepNow;
                if (s < 0) return string.Format(Loc("LOC_KX_CropStepPx", "{0} px"), (int)Math.Round(-s));
                string frac = s >= 1 ? "1" : Math.Abs(s - 0.5) < 0.01 ? "½" : "¼";
                return string.Format(Loc("LOC_KX_CropStepSq2", "{0} square"), frac);
            }
        }

        /// <summary>The line over the hints while cropping.</summary>
        private string CropStatusText()
        {
            var t = cropTile;
            string note = cropNote != null && (DateTime.Now - cropNoteAt).TotalSeconds < 2.5 ? cropNote : null;
            string what = cropGrow ? Loc("LOC_KX_CropGrowing", "Cropping: growing a side back out") : Loc("LOC_KX_Cropping", "Cropping");
            return what + "   ·   " + string.Format(Loc("LOC_KX_CropStepIs", "step {0}"), CropStepText) + "   ·   "
                   + (note ?? CropText(t != null && t.Card != null ? t.Card.CropBox : null));
        }

        private void CropNotify()
        {
            Notify("BoardHintText");
            Notify("BoardHintWarn");
            Notify("HasBoardHintWarn");
        }

        private void CropShow()
        {
            var t = cropTile;
            if (t == null) return;
            t.PickText = Loc("LOC_KX_CropTag2", "Crop");
            BoardSyncTiles();   // (the empty squares the crop frees, or takes; places every tile)
            CropNotify();
        }

        private void CropSetNote(string text)
        {
            cropNote = text;
            cropNoteAt = DateTime.Now;
            CropNotify();
            if (ui == null) return;
            if (cropNoteTimer == null)
            {
                cropNoteTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(2.6) };
                cropNoteTimer.Tick += (s, e) => { cropNoteTimer.Stop(); cropNote = null; CropNotify(); };
            }
            cropNoteTimer.Stop();
            cropNoteTimer.Start();
        }

        /// <summary>The D-pad: Up / Down / Left / Right trims that side (grow: pulls it back out), by the step; the edge lands on the step.</summary>
        private void CropMove(Key k, bool grow)
        {
            var t = cropTile;
            if (t == null || t.Card == null) return;
            var c = t.Card;
            var full = cropFull;
            var foot = CardBox(c);
            double L = foot.Left, T = foot.Top, R = foot.Right, B = foot.Bottom;
            bool across = k == Key.Left || k == Key.Right;
            double s = across ? CropStepX : CropStepY;   // (1 px is a different part of a square each way)
            bool px = CropStepIsPx;
            Func<double, double> next = v => px ? v + s : StepLine(v, s, 1);    // px: exactly the step; squares: the next line right / down
            Func<double, double> prev = v => px ? v - s : StepLine(v, s, -1);   // the same left / up
            var shape = ShapeCellsOf(full, c);   // a cut shape: the empty corners of its box go in one press
            double minW = Math.Max(IsFreeMode ? 2 : 0.25, full.Width * CropKeep), minH = Math.Max(IsFreeMode ? 2 : 0.25, full.Height * CropKeep);
            double near = px ? s * 0.5 : 0.01;   // (a cut shape's own outline: within half a step of it, the edge goes onto it)
            double gridR = BMaxX, gridB = BMaxY, gridL = BMinX, gridT = BMinY;   // (0.82: up to the screen's edges)
            switch (k)
            {
                case Key.Up:
                    if (!grow) { T = T < shape.Top - near ? shape.Top : next(T); T = Math.Min(T, B - minH); }
                    else { double v = prev(T); if (T > shape.Top + near && v < shape.Top - near) v = shape.Top; T = Math.Max(Math.Max(gridT, full.Top), v); }
                    break;
                case Key.Down:
                    if (!grow) { B = B > shape.Bottom + near ? shape.Bottom : prev(B); B = Math.Max(B, T + minH); }
                    else { double v = next(B); if (B < shape.Bottom - near && v > shape.Bottom + near) v = shape.Bottom; B = Math.Min(Math.Min(gridB, full.Bottom), v); }
                    break;
                case Key.Left:
                    if (!grow) { L = L < shape.Left - near ? shape.Left : next(L); L = Math.Min(L, R - minW); }
                    else { double v = prev(L); if (L > shape.Left + near && v < shape.Left - near) v = shape.Left; L = Math.Max(Math.Max(gridL, full.Left), v); }
                    break;
                case Key.Right:
                    if (!grow) { R = R > shape.Right + near ? shape.Right : prev(R); R = Math.Max(R, L + minW); }
                    else { double v = next(R); if (R < shape.Right - near && v > shape.Right + near) v = shape.Right; R = Math.Min(Math.Min(gridR, full.Right), v); }
                    break;
                default: return;
            }
            var now = new Rect(L, T, Math.Max(0.01, R - L), Math.Max(0.01, B - T));
            if (Math.Abs(now.X - foot.X) < 1e-4 && Math.Abs(now.Y - foot.Y) < 1e-4 && Math.Abs(now.Width - foot.Width) < 1e-4 && Math.Abs(now.Height - foot.Height) < 1e-4)
            {
                PlaySound("back");
                CropSetNote(grow ? Loc("LOC_KX_CropAtEdge", "That side is all the way out") : Loc("LOC_KX_CropAtMin", "That's as small as it goes"));
                return;
            }
            var wasCrop = c.CropBox;
            SetCardBox(c, now);
            c.CropBox = CropOfRects(full, now);
            if (c.CropBox == null) SetCardBox(c, Rect.Intersect(full, new Rect(gridL, gridT, gridR - gridL, gridB - gridT)));   // (all the way back out: exactly the whole tile)
            if (grow && !IsFreeMode && !cropFromPick && UnderOf(c).Count > 0)   // (picked up: it can be over tiles until it's put down)
            {
                SetCardBox(c, foot);
                c.CropBox = wasCrop;
                PlaySound("back");
                CropSetNote(Loc("LOC_KX_CropBlocked", "Another tile is in the way: move it first"));
                return;
            }
            PlaySound("move");
            CropShow();
        }

        /// <summary>Y: the crop off (each side as far back out as it goes when another tile is in the way).</summary>
        private void CropReset()
        {
            var t = cropTile;
            if (t == null || t.Card == null) return;
            var c = t.Card;
            if (CleanCrop(c.CropBox) == null) { PlaySound("back"); return; }
            var before = CardBox(c);
            bool blocked = false;
            foreach (var k in new[] { Key.Up, Key.Down, Key.Left, Key.Right })
            {
                var foot = CardBox(c);
                var wasCrop = c.CropBox;
                double L = foot.Left, T = foot.Top, R = foot.Right, B = foot.Bottom;
                if (k == Key.Up) T = Math.Max(BMinY, cropFull.Top);
                else if (k == Key.Down) B = Math.Min(BMaxY, cropFull.Bottom);
                else if (k == Key.Left) L = Math.Max(BMinX, cropFull.Left);
                else R = Math.Min(BMaxX, cropFull.Right);
                var now = new Rect(L, T, Math.Max(0.01, R - L), Math.Max(0.01, B - T));
                SetCardBox(c, now);
                c.CropBox = CropOfRects(cropFull, now);
                if (!IsFreeMode && !cropFromPick && UnderOf(c).Count > 0) { SetCardBox(c, foot); c.CropBox = wasCrop; blocked = true; }
            }
            if (CardBox(c) == before) { PlaySound("back"); CropSetNote(Loc("LOC_KX_CropBlocked", "Another tile is in the way: move it first")); return; }
            PlaySound("move");
            if (blocked) CropSetNote(Loc("LOC_KX_CropPartBlocked", "Some sides stay cropped: another tile is in the way"));
            CropShow();
        }

        /// <summary>A keeps the crop, B puts back what it was.</summary>
        private void CropEnd(bool keep)
        {
            var t = cropTile;
            if (t == null) return;
            logger.Info("Kippix: crop " + (keep ? "kept" : "cancelled") + (cropFromPick ? " (tile picked up)" : ""));
            cropTile = null;
            cropGrow = false;
            if (t.Card != null && !keep && cropStartBox != null)
            {
                t.Card.X = cropStartBox[0]; t.Card.Y = cropStartBox[1]; t.Card.W = cropStartBox[2]; t.Card.H = cropStartBox[3];
                t.Card.CropBox = cropStartCrop;
            }
            cropStartBox = null;
            cropStartCrop = null;
            t.PickText = null;
            PlaySound(keep ? "select" : "back");
            if (cropFromPick)
            {
                // Start while it was picked up: moving it again (put down with A, the crop and all; B puts it all back)
                cropFromPick = false;
                if (ReferenceEquals(boardPicked, t))
                {
                    BoardCheckUnder();
                    BoardSyncTiles();
                    BoardFillPage();   // (its picture for the new size)
                    BoardPlace(t);
                    BoardPlaceWidgetsSoon();
                    UpdateRotTimer();
                    CropNotify();
                    FocusBoardTile(t);
                    return;
                }
            }
            cropFromPick = false;
            BoardChanged(false);   // (saved: an undo step; the picture again for its new size)
            CropNotify();
            ui.BeginInvoke(new Action(() => { var again = BoardTiles.FirstOrDefault(x => x.IsCard && t.Card != null && ReferenceEquals(x.Card, t.Card)); if (again != null) FocusBoardTile(again); else FocusBoard(); }), DispatcherPriority.Loaded);
        }

        /// <summary>The Enter Playnite sends for the controller's A that just ended cropping (it would pick the tile up).</summary>
        private bool CropEnterEcho(KeyEventArgs e)
        {
            return (e.Key == Key.Enter || e.Key == Key.Space) && (DateTime.Now - cropEndedAt).TotalMilliseconds < 400;
        }

        /// <summary>
        /// LB / RB held on a controller right now (0.82): read from Playnite's own controller state at each press. RB let go
        /// never reached cropping, so "grow it back" stayed on: the next side didn't crop (only the "as far as it goes" sound)
        /// until cropping was started again. Null when no controller can be read (then the press / release flag is used).
        /// </summary>
        private bool? ShoulderHeldNow()
        {
            try
            {
                bool any = false;
                foreach (var pad in api.GetConnectedControllers().OfType<IGamepad>())
                {
                    if (pad == null || !pad.Enabled || pad.ButtonInputState == null) continue;
                    any = true;
                    ControllerInputState st;
                    if (pad.ButtonInputState.TryGetValue(ControllerInput.LeftShoulder, out st) && st == ControllerInputState.Pressed) return true;
                    if (pad.ButtonInputState.TryGetValue(ControllerInput.RightShoulder, out st) && st == ControllerInputState.Pressed) return true;
                }
                return any ? (bool?)false : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>LB / RB let go while cropping (from the key-up side, where releases arrive): back to trimming.</summary>
        private bool CropPadUp(ControllerInput button)
        {
            if (cropTile == null || (button != ControllerInput.LeftShoulder && button != ControllerInput.RightShoulder)) return false;
            if (cropGrow) { cropGrow = false; CropNotify(); }
            return true;
        }

        /// <summary>The keyboard while cropping (the D-pad arrives as arrows; LT / RT as PgUp / PgDn). Takes every key.</summary>
        private bool CropKey(KeyEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Up: case Key.Down: case Key.Left: case Key.Right:
                {
                    var held = ShoulderHeldNow();
                    if (held.HasValue && held.Value != cropGrow) { cropGrow = held.Value; CropNotify(); }
                    CropMove(e.Key, cropGrow || shift);
                    break;
                }
                case Key.Escape: case Key.Back: if (!e.IsRepeat && !CropJustStarted("key " + e.Key)) CropEnd(false); break;
                case Key.Enter: case Key.Space: if (!e.IsRepeat && !CropJustStarted("key " + e.Key)) CropEnd(true); break;
                case Key.PageUp: case Key.OemMinus: case Key.Subtract: CropChangeStep(-1); break;
                case Key.PageDown: case Key.OemPlus: case Key.Add: CropChangeStep(1); break;
                case Key.R: case Key.Delete: if (!e.IsRepeat) CropReset(); break;
            }
            return true;
        }

        /// <summary>The controller's buttons while cropping.</summary>
        private bool CropPadKey(ControllerInput button, KeyEventArgs e)
        {
            switch (button)
            {
                case ControllerInput.A: case ControllerInput.B: case ControllerInput.X: case ControllerInput.Y:
                case ControllerInput.LeftShoulder: case ControllerInput.RightShoulder: case ControllerInput.Start: case ControllerInput.Back:
                case ControllerInput.TriggerLeft: case ControllerInput.TriggerRight:
                    break;
                default: return false;   // (the D-pad: as arrows)
            }
            bool release = IsPadRelease(e);
            if (button == ControllerInput.LeftShoulder || button == ControllerInput.RightShoulder)
            {
                // held: the D-pad grows a side back out
                if (cropGrow == release) { cropGrow = !release; CropNotify(); }
                return true;
            }
            if (release) return true;
            switch (button)
            {
                case ControllerInput.A: if (!CropJustStarted("A")) { CropEnd(true); cropEndedAt = DateTime.Now; } break;   // (Playnite sends Enter for it too: not a second A)
                case ControllerInput.B: if (!CropJustStarted("B")) CropEnd(false); break;
                case ControllerInput.Y: CropReset(); break;
                // LT / RT: Playnite sends them as PgUp / PgDn too, which change the step (CropKey), so nothing here
            }
            return true;
        }
    }
}

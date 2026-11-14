using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.92) Game page settings › Layout › Bring a part into view: every part of the game page; pick one, then (for the parts
    /// that can go on either) the Top or Lower page. It's shown if it was hidden, put on that page, and if it isn't on the
    /// screen (moved off it and lost) it goes to the emptiest spot there is; then it's picked up, ready to move.
    /// </summary>
    public partial class ThemeApi
    {
        private ObservableCollection<PickRow> bringRows;
        public ObservableCollection<PickRow> BringPartRows { get { return bringRows ?? (bringRows = new ObservableCollection<PickRow>()); } }
        public bool IsOptionsBringPart { get { return optionsPage == "bringpart"; } }
        private string bringKey;   // the part whose page is being chosen (null: the list of parts)

        private ICommand bringPartCmd;
        public ICommand BringPartCommand
        {
            get
            {
                return bringPartCmd ?? (bringPartCmd = new RelayCommand(_ =>
                {
                    PlaySound("select");
                    bringKey = null;
                    BuildBringRows(null);
                    SetOptionsPage("bringpart");
                }));
            }
        }

        /// <summary>The game page part (Top and lower page) an element is, or null (the buttons, the clock, the hints: always on top).</summary>
        private static string DetPartOf(MasterElement el)
        {
            if (el == null || el.Names == null) return null;
            foreach (string k in DetAllKeys) if (DetNames(k).Any(n => el.Names.Contains(n))) return k;
            return null;
        }

        private bool BringHidden(MasterElement el)
        {
            string part = DetPartOf(el);
            if (part != null) return !DetPartOn(part);
            if (el.Key == "game.clock") return !settings.DetShowDate;
            if (el.Key == "game.hints") return !settings.DetShowHints;
            return false;
        }

        private void BuildBringRows(string focusKey)
        {
            var rows = BringPartRows;
            rows.Clear();
            var pick = bringKey != null ? MasterElements.FirstOrDefault(m => m.Key == bringKey) : null;
            if (pick == null)
            {
                bringKey = null;
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_BringHeader", "GAME PAGE PARTS") });
                foreach (var el in MasterElements.Where(m => m.Screen == "game"))
                {
                    var e = el;
                    string part = DetPartOf(e);
                    var o = MasterOffset(e.Key);
                    bool moved = Math.Abs(o[0]) >= 0.5 || Math.Abs(o[1]) >= 0.5;
                    string sub = part != null && DetIsLower(part) ? Loc("LOC_KX_BringLower", "Lower page") : Loc("LOC_KX_BringTop", "Top page");
                    if (BringHidden(e)) sub += "   ·   " + Loc("LOC_KX_BringHidden", "Hidden");
                    else if (moved) sub += "   ·   " + Loc("LOC_KX_BringMoved", "Moved");
                    rows.Add(new PickRow
                    {
                        Key = "bring:" + e.Key, Icon = part != null ? DetPartIcon(part) : "layout", Text = Loc(e.LocKey, e.Fallback), Sub = sub,
                        Command = new RelayCommand(_ =>
                        {
                            if (part == null) { BringPart(e, false); return; }
                            PlaySound("select");
                            bringKey = e.Key;
                            BuildBringRows("bringtop");
                        })
                    });
                }
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "bringnote", Text = Loc("LOC_KX_BringNote", "Picks it up on the screen, somewhere you can see it, ready to move.") });
            }
            else
            {
                string part = DetPartOf(pick);
                bool lower = part != null && DetIsLower(part);
                rows.Add(new PickRow { IsHeader = true, Text = string.Format(Loc("LOC_KX_BringWhich", "{0}: WHICH PAGE?"), Loc(pick.LocKey, pick.Fallback).ToUpperInvariant()) });
                rows.Add(new PickRow { Key = "bringtop", Icon = "up", Text = Loc("LOC_KX_BringTop", "Top page"), IsSelected = !lower, Command = new RelayCommand(_ => BringPart(pick, false)) });
                rows.Add(new PickRow { Key = "bringlower", Icon = "download", Text = Loc("LOC_KX_BringLower", "Lower page"), IsSelected = lower, Command = new RelayCommand(_ => BringPart(pick, true)) });
            }
            Notify("BringPartRows");
            string key = focusKey;
            if (key != null)
                ui.BeginInvoke(new Action(() =>
                {
                    var row = BringPartRows.FirstOrDefault(r => r.Key == key);
                    if (row != null && optionsPage == "bringpart") UiTools.FocusItem("KX_BringPartList", row);
                }), DispatcherPriority.ContextIdle);
        }

        /// <summary>B on the page choice: back to the list (true: handled).</summary>
        private bool BringPartBack()
        {
            if (optionsPage != "bringpart" || bringKey == null) return false;
            string k = bringKey;
            bringKey = null;
            PlaySound("back");
            BuildBringRows("bring:" + k);
            return true;
        }

        private void BringPart(MasterElement el, bool down)
        {
            if (el == null || IsMasterMoving) { PlaySound("back"); return; }
            PlaySound("select");
            EditBegin();   // (undo: as it was before it was brought back)
            string part = DetPartOf(el);
            if (part != null)
            {
                if (!DetPartOn(part)) SetDetPartOn(part, true);
                if (DetIsLower(part) != down) MoveDetPart(part);
                if (down && !settings.DetMoreSections) DetMoreSections = true;
            }
            else
            {
                if (el.Key == "game.clock" && !settings.DetShowDate) DetShowDate = true;
                if (el.Key == "game.hints" && !settings.DetShowHints) DetShowHints = true;
            }
            bool opened = !GameDetailsVisible() && EnsureGameDetailsOpen();
            BringAfter(opened ? 650 : 60, () =>
            {
                try { if (GameDetailsVisible()) DetailsScrollTo(down && ShowDetMore, false); } catch (Exception) { }
                BringAfter(220, () => BringPlace(el, down, opened));
            });
        }

        private void BringAfter(int ms, Action act)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); try { act(); } catch (Exception ex) { logger.Warn("Kippix: bring a part into view: " + ex.Message); } };
            t.Start();
        }

        private void BringPlace(MasterElement el, bool down, bool opened)
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var canvas = win != null ? UiTools.FindByName(win, "KX_MasterCanvas") : null;
            if (canvas != null && canvas.ActualWidth > 0)
            {
                canvas.UpdateLayout();
                var area = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
                if (down)
                {
                    // the lower page's own box (it's cut to it)
                    var more = UiTools.FindByName(win, "KX_DetMore");
                    if (more != null && more.IsVisible && more.ActualWidth > 0)
                    {
                        try
                        {
                            var b = more.TransformToVisual(canvas).TransformBounds(new Rect(0, 0, more.ActualWidth, more.ActualHeight));
                            b.Intersect(area);
                            if (!b.IsEmpty && b.Width > 100 && b.Height > 100) area = b;
                        }
                        catch (Exception) { }
                    }
                }
                Rect cur = BringRect(el, canvas);
                var inner = new Rect(area.X + 10, area.Y + 10, Math.Max(1, area.Width - 20), Math.Max(1, area.Height - 20));
                if (!cur.IsEmpty && !inner.Contains(cur))
                {
                    Point spot = BringSpot(el, cur.Size, area, canvas);
                    var o = MasterOffset(el.Key);
                    SetMasterOffset(el.Key, o[0] + spot.X - cur.X, o[1] + spot.Y - cur.Y);
                    SaveSettings();
                    ApplyMasterOffsets();
                }
                else if (cur.IsEmpty)
                {
                    // nothing to measure (it has nothing to show for this game): where it comes, so it isn't lost
                    SetMasterOffset(el.Key, 0, 0);
                    SaveSettings();
                    ApplyMasterOffsets();
                }
            }
            bringKey = null;
            BuildBringRows(null);   // (the list again, for when it's put down)
            StartMasterMove(el);
            if (opened) masterOpenedDetails = true;   // (done: the game page opened for it closes again)
        }

        /// <summary>(0.99) A home screen part switched back on (logo, game info, How long to beat, playing now, friends, hints, filter
        /// name): it comes back at the top of the screen, in the middle, in front, wherever it was moved to before. One with nothing
        /// to show yet (no time known for this game, say) is placed when it first shows (looked for over ~10 s).</summary>
        private void HomePartBackOnTop(string key)
        {
            if (!IsFullscreen) return;
            var el = MasterElements.FirstOrDefault(m => m.Key == key);
            if (el == null) return;
            int tries = 0;
            Action place = null;
            place = () =>
            {
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                var root = win != null ? win.Content as FrameworkElement : null;
                if (root == null || root.ActualWidth <= 0) return;
                root.UpdateLayout();
                Rect cur = BringRect(el, root);
                if (cur.IsEmpty || cur.Width < 2)
                {
                    if (++tries < 20) BringAfter(500, place);
                    return;
                }
                double topY = root.ActualHeight * 0.11;   // (under the top bar)
                double x = (root.ActualWidth - cur.Width) / 2;
                var o = MasterOffset(key);
                SetMasterOffset(key, o[0] + x - cur.X, o[1] + topY - cur.Y);
                foreach (string name in el.Names)
                {
                    var fe = UiTools.FindByName(win, name);
                    if (fe != null) System.Windows.Controls.Panel.SetZIndex(fe, 900);   // (in front of what's beside it)
                }
                SaveSettings();
                ApplyMasterOffsets();
                logger.Info("Kippix: " + key + " switched back on: put at the top in the middle");
            };
            BringAfter(250, place);
        }

        /// <summary>Where the part is now on the canvas (all of it, on screen or not).</summary>
        private static Rect BringRect(MasterElement el, FrameworkElement canvas)
        {
            var box = Rect.Empty;
            var win = Application.Current.MainWindow;
            foreach (string name in el.Names)
            {
                var fe = UiTools.FindByName(win, name);
                if (fe == null || !fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) continue;
                var f = TurnFrame.Of(fe, DrawnBounds(fe), canvas, 0);
                if (f != null) box.Union(f.Bounds());
            }
            return box;
        }

        /// <summary>The spot in <paramref name="area"/> where a box this size covers the least of the page's other parts (nearest
        /// the middle when there's more than one).</summary>
        private Point BringSpot(MasterElement el, Size size, Rect area, FrameworkElement canvas)
        {
            double w = Math.Min(size.Width, area.Width), h = Math.Min(size.Height, area.Height);
            var others = new List<Rect>();
            foreach (var m in MasterElements.Where(x => x.Screen == "game" && x.Key != el.Key))
            {
                var f = MasterFrame(m, canvas);
                if (f == null) continue;
                var b = f.Bounds();
                b.Intersect(area);
                if (!b.IsEmpty && b.Width > 1 && b.Height > 1) others.Add(b);
            }
            const double gap = 24, step = 30;
            var centre = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            Point best = new Point(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
            double bestScore = double.MaxValue;
            for (double y = area.Y + gap; y + h <= area.Bottom - gap + 0.5; y += step)
                for (double x = area.X + gap; x + w <= area.Right - gap + 0.5; x += step)
                {
                    var r = new Rect(x, y, w, h);
                    double cover = 0;
                    foreach (var o in others)
                    {
                        var i = Rect.Intersect(r, o);
                        if (!i.IsEmpty) cover += i.Width * i.Height;
                    }
                    double dx = x + w / 2 - centre.X, dy = y + h / 2 - centre.Y;
                    double score = cover * 1000 + Math.Sqrt(dx * dx + dy * dy);
                    if (score < bestScore) { bestScore = score; best = new Point(x, y); }
                }
            return best;
        }
    }
}

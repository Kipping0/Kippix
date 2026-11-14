using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.51) The game page's lower parts keep their place on every game. A part that's switched on but has nothing for
    /// this game (no captures, no HowLongToBeat times, no news...) used to take no room, so the parts after it in the same
    /// line slid over and the page's layout (and Free move's moves, which are from where each part would be) differed from
    /// game to game. Now it keeps its room, empty: invisible, can't be highlighted or pressed, the size it had the last time
    /// it had something (settings.DetRooms). While you edit (Free move), each one is drawn as a dashed "blank" with its name,
    /// so it can be moved like the part itself (it is the part itself: one of each, no copies).
    /// </summary>
    public partial class ThemeApi
    {
        private class BlankWas { public object MinW, MinH, Op, Hit; }

        private readonly Dictionary<FrameworkElement, BlankWas> detBlankEls = new Dictionary<FrameworkElement, BlankWas>();
        private readonly HashSet<string> detBlankKeys = new HashSet<string>();
        private readonly Dictionary<string, Grid> detBlankMarks = new Dictionary<string, Grid>();
        private bool detBlanksHooked, detBlanksQueued;
        private Canvas blankCanvas;
        private readonly Dictionary<string, FrameworkElement> detBlankFind = new Dictionary<string, FrameworkElement>();

        // the parts' own place: switched on (whether or not they have anything for this game)
        public bool SlotDetHistory { get { return settings.DetSecHistory; } }
        public bool SlotDetHltb { get { return settings.DetSecHltb; } }
        public bool SlotDetAch { get { return settings.DetSecAch; } }
        public bool SlotDetSaves { get { return settings.DetSecSaves && LudusaviInstalled; } }
        public bool SlotDetMods { get { return settings.DetSecMods; } }
        public bool SlotDetNews { get { return settings.DetSecNews; } }
        public bool SlotDetCaptures { get { return settings.DetSecCaptures; } }
        public bool SlotDetSimilar { get { return settings.DetSecSimilar; } }

        private bool DetSlot(string key)
        {
            switch (key)
            {
                case "history": return SlotDetHistory;
                case "hltb": return SlotDetHltb;
                case "ach": return SlotDetAch;
                case "saves": return SlotDetSaves;
                case "mods": return SlotDetMods;
                case "news": return SlotDetNews;
                case "captures": return SlotDetCaptures;
                case "similar": return SlotDetSimilar;
            }
            return DetPartShowing(key);   // (status, notes, scores: always something to show)
        }

        private bool DetBlank(string key) { return detBlankKeys.Contains(key); }

        /// <summary>Anything that changes a part's "showing" brings the blanks up to date (once, after the page is laid out).</summary>
        private void HookDetBlanks()
        {
            if (detBlanksHooked) return;
            detBlanksHooked = true;
            PropertyChanged += (s, e) =>
            {
                string n = e.PropertyName ?? "";
                if (n.Length == 0 || n.StartsWith("ShowDet", StringComparison.Ordinal) || n == "DetSimilar" || n == "DetCaptures")
                {
                    if (n.StartsWith("ShowDet", StringComparison.Ordinal)) Notify("SlotDet" + n.Substring(7));   // (no such property: nothing reads it)
                    QueueDetBlanks();
                }
            };
        }

        private void QueueDetBlanks()
        {
            if (detBlanksQueued || ui == null) return;
            detBlanksQueued = true;
            ui.BeginInvoke(new Action(() => { detBlanksQueued = false; try { ApplyDetBlanks(); } catch (Exception ex) { logger.Warn("Kippix: game page blanks: " + ex.Message); } }), DispatcherPriority.Loaded);
        }

        private static bool DetBlankFullWidth(string key) { return key == "captures" || key == "similar"; }

        private void ApplyDetBlanks()
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            if (win == null) return;
            var details = UiTools.FindByName(win, "PART_ElemGameDetails");
            if (details == null) return;
            if (settings.DetRooms == null) settings.DetRooms = new Dictionary<string, double[]>();
            bool changed = false, save = false;
            foreach (string key in DetSectionKeys)
            {
                FrameworkElement fe;
                // (0.99.55) kept between runs (a search of the page each time, for each part, was slow)
                if (!detBlankFind.TryGetValue(key, out fe) || fe == null || !fe.IsLoaded || PresentationSource.FromVisual(fe) == null)
                {
                    fe = UiTools.FindByName(details, DetNames(key)[0]) ?? UiTools.FindByName(win, DetNames(key)[0]);
                    detBlankFind[key] = fe;
                }
                if (fe == null) continue;
                bool blank = detailsOpen && DetSlot(key) && !DetPartShowing(key);
                bool was = detBlankEls.ContainsKey(fe);
                if (!blank)
                {
                    if (was) { UnBlank(fe); changed = true; }
                    detBlankKeys.Remove(key);
                    // the room it takes with something in it: kept for the games it has nothing for
                    if (fe.IsVisible && fe.ActualWidth > 1 && fe.ActualHeight > 1)
                    {
                        double[] r;
                        settings.DetRooms.TryGetValue(key, out r);
                        double w = Math.Round(fe.ActualWidth), h = Math.Round(fe.ActualHeight);
                        if (r == null || r.Length < 2 || Math.Abs(r[0] - w) > 1 || Math.Abs(r[1] - h) > 1)
                        {
                            if (r == null || r.Length < 2 || Math.Abs(r[1] - h) > 24 || Math.Abs(r[0] - w) > 24) save = true;
                            settings.DetRooms[key] = new[] { w, h };
                        }
                    }
                    continue;
                }
                if (!was)
                {
                    detBlankEls[fe] = new BlankWas
                    {
                        MinW = fe.ReadLocalValue(FrameworkElement.MinWidthProperty),
                        MinH = fe.ReadLocalValue(FrameworkElement.MinHeightProperty),
                        Op = fe.ReadLocalValue(UIElement.OpacityProperty),
                        Hit = fe.ReadLocalValue(UIElement.IsHitTestVisibleProperty)
                    };
                    changed = true;
                    if (fe.IsKeyboardFocusWithin)
                    {
                        var sink = UiTools.FindByName(details, "KX_DetMoreSink") as UIElement;
                        if (sink != null) sink.Focus();
                    }
                }
                detBlankKeys.Add(key);
                double[] room;
                settings.DetRooms.TryGetValue(key, out room);
                double rw = room != null && room.Length >= 2 ? room[0] : 0;
                double rh = room != null && room.Length >= 2 ? room[1] : key == "captures" ? 190 : key == "similar" ? DetSimilarTileH + 70 : 200;
                if (!DetBlankFullWidth(key) && rw > 0) fe.MinWidth = rw;
                fe.MinHeight = rh;
                fe.Opacity = 0;
                fe.IsHitTestVisible = false;
            }
            if (changed) { NotifyDetInteract(); SyncFreeGaps(); }
            if (save) SaveSettings();
            UpdateDetBlankMarks();
        }

        private void UnBlank(FrameworkElement fe)
        {
            BlankWas w;
            if (!detBlankEls.TryGetValue(fe, out w)) return;
            detBlankEls.Remove(fe);
            Put(fe, FrameworkElement.MinWidthProperty, w.MinW);
            Put(fe, FrameworkElement.MinHeightProperty, w.MinH);
            Put(fe, UIElement.OpacityProperty, w.Op);
            Put(fe, UIElement.IsHitTestVisibleProperty, w.Hit);
        }

        private static void Put(DependencyObject d, DependencyProperty p, object v)
        {
            if (v == DependencyProperty.UnsetValue || v is System.Windows.Expression) d.ClearValue(p); else d.SetValue(p, v);
        }

        /// <summary>While editing the game page: a dashed outline with its name on each blank (Free move's layer, so only then).</summary>
        private void UpdateDetBlankMarks()
        {
            // (0.99.55) called on every move of the editing: nothing to do (and no search of the whole screen) unless editing or a mark is up
            bool editingNow = IsMasterMoving;
            if (!editingNow && !detBlankMarks.Values.Any(m => m.Visibility == Visibility.Visible)) return;
            try
            {
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                if (blankCanvas == null || !blankCanvas.IsLoaded) blankCanvas = win != null ? UiTools.FindByName(win, "KX_MasterCanvas") as Canvas : null;
                var canvas = blankCanvas;
                if (canvas == null) return;
                bool editing = IsMasterMoving && GameDetailsVisible();
                foreach (string key in DetSectionKeys)
                {
                    Grid mark;
                    detBlankMarks.TryGetValue(key, out mark);
                    TurnFrame f = null;
                    if (editing && DetBlank(key))
                    {
                        var el = MasterElements.FirstOrDefault(m => m.Key == "game.sec." + key);
                        if (el != null) f = MasterFrame(el, canvas);
                    }
                    if (f == null || f.W < 4 || f.H < 4)
                    {
                        if (mark != null && mark.Visibility != Visibility.Collapsed) mark.Visibility = Visibility.Collapsed;
                        continue;
                    }
                    if (mark == null)
                    {
                        mark = NewBlankMark(DetPartName(key));
                        detBlankMarks[key] = mark;
                    }
                    if (!ReferenceEquals(mark.Parent, canvas))
                    {
                        var old = mark.Parent as Panel;
                        if (old != null) old.Children.Remove(mark);
                        canvas.Children.Insert(0, mark);   // (under the highlight's outline and the pointer)
                    }
                    mark.Visibility = Visibility.Visible;
                    mark.Width = f.W;
                    mark.Height = f.H;
                    Canvas.SetLeft(mark, f.C.X - f.W / 2);
                    Canvas.SetTop(mark, f.C.Y - f.H / 2);
                }
            }
            catch (Exception) { }
        }

        private Grid NewBlankMark(string name)
        {
            var g = new Grid { IsHitTestVisible = false, Focusable = false };
            g.Children.Add(new Rectangle
            {
                RadiusX = 18, RadiusY = 18, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 4 },
                Stroke = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
            });
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = name, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Opacity = 0.9, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock
            {
                Text = Loc("LOC_KX_DetBlankSub", "Blank: nothing for this game. Its place is kept for the games that have it"),
                FontSize = 15, Foreground = Brushes.White, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 460
            });
            g.Children.Add(sp);
            return g;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace KippixHelper
{
    /// <summary>One day of real play (timed sessions only, split at midnight, never more than 24 hours).</summary>
    internal class PlayDay
    {
        public DateTime Day;
        public double Seconds;
        /// <summary>The games played that day, most played first: (game id or name, name, seconds).</summary>
        public List<Tuple<string, string, double>> Games = new List<Tuple<string, string, double>>();
    }

    /// <summary>
    /// The play calendar on the profile's Overview and Your year: one small square per day, shaded by how long you played
    /// (five steps of the accent colour), month names along the top. Drawn once when the page opens (one element, ~370 squares);
    /// with the highlight on it the D-pad walks the days (the line under it says what you played), A opens that day's top game,
    /// and moving off an edge goes on to the rest of the page.
    /// Only timed sessions count here (and for the highlights and streaks): filled-in ones (a Steam sync's hours, Steam's last
    /// two weeks, ThemeApi.Playtime.cs) only know a total, not when you played.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>Real sessions spread over the days they cover (a session past midnight goes partly on each day); no day over 24 h.</summary>
        internal static Dictionary<DateTime, PlayDay> RealPlayDays(IEnumerable<PlaySession> sessions, DateTime from, DateTime to)
        {
            var sums = new Dictionary<DateTime, Dictionary<string, double>>();
            var names = new Dictionary<string, string>();
            DateTime first = from.Date, last = to.Date;
            if (sessions != null)
                foreach (var s in sessions)
                {
                    if (s == null || s.Estimated || s.Seconds <= 0) continue;
                    var start = s.Start;
                    var end = start.AddSeconds(Math.Min(s.Seconds, 86400.0 * 30));   // (a broken entry can't fill months)
                    if (end.Date < first || start.Date > last) continue;
                    string key = !string.IsNullOrEmpty(s.GameId) ? s.GameId : (s.Name ?? "");
                    if (!names.ContainsKey(key) || string.IsNullOrEmpty(names[key])) names[key] = s.Name ?? "";
                    var cur = start;
                    int guard = 0;
                    while (cur < end && guard++ < 40)
                    {
                        var midnight = cur.Date.AddDays(1);
                        var stop = end < midnight ? end : midnight;
                        double sec = (stop - cur).TotalSeconds;
                        var day = cur.Date;
                        if (day >= first && day <= last && sec > 0)
                        {
                            Dictionary<string, double> g;
                            if (!sums.TryGetValue(day, out g)) { g = new Dictionary<string, double>(); sums[day] = g; }
                            double had;
                            g.TryGetValue(key, out had);
                            g[key] = had + sec;
                        }
                        cur = stop;
                    }
                }
            var result = new Dictionary<DateTime, PlayDay>();
            foreach (var kv in sums)
            {
                double total = kv.Value.Values.Sum();
                double k = total > 86400 ? 86400 / total : 1;   // two games at once (or overlapping records): the day still has 24 hours
                var d = new PlayDay { Day = kv.Key, Seconds = Math.Min(86400, total) };
                foreach (var g in kv.Value.OrderByDescending(x => x.Value))
                {
                    string n;
                    names.TryGetValue(g.Key, out n);
                    d.Games.Add(Tuple.Create(g.Key, n ?? "", g.Value * k));
                }
                result[kv.Key] = d;
            }
            return result;
        }

        /// <summary>"2 h 10 m", "45 m".</summary>
        internal static string DurText(double seconds)
        {
            long m = (long)Math.Round(seconds / 60.0);
            if (m < 1) return "<1 m";
            if (m < 60) return m + " m";
            return (m / 60) + " h " + (m % 60).ToString("00", CultureInfo.InvariantCulture) + " m";
        }

        // ---------------- on the page ----------------

        private FrameworkElement pageHeatmap;
        public FrameworkElement PageHeatmap { get { return pageHeatmap; } }
        public bool HasPageHeatmap { get { return pageHeatmap != null; } }
        private bool heatHadFocus;

        /// <summary>A section of the profile pages that can be taken off (Profile tabs › ON YOUR PROFILE).</summary>
        private bool HubSectionShown(string key)
        {
            return settings.ProfileHubSectionsHidden == null || !settings.ProfileHubSectionsHidden.Contains(key);
        }

        /// <summary>The calendar and the finished-games shelf go when the page changes (LeavePage).</summary>
        private void ClearHubExtras()
        {
            heatHadFocus = false;
            if (pageHeatmap != null)
            {
                heatHadFocus = pageHeatmap.IsKeyboardFocusWithin;
                pageHeatmap = null;
                Notify("PageHeatmap");
                Notify("HasPageHeatmap");
            }
            ClearHubShelf();
        }

        /// <summary>Puts the calendar on the page (days: real play from RealPlayDays).</summary>
        private void ShowPageHeatmap(DateTime first, DateTime last, Dictionary<DateTime, PlayDay> days, string title)
        {
            if (first > last) return;
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 22), HorizontalAlignment = HorizontalAlignment.Left };
            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 0, 0, 10) };
            var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(legend, Dock.Right);
            Color accent = AccentColorNow();
            var less = new TextBlock { Text = Loc("LOC_KX_HeatLess", "Less"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            StyleText(less, "KX_TextDim", 14);
            legend.Children.Add(less);
            for (int i = 0; i < 5; i++)
                legend.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 4, 0), Background = PlayHeatmap.LevelBrush(i, accent) });
            var more = new TextBlock { Text = Loc("LOC_KX_HeatMore", "More"), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            StyleText(more, "KX_TextDim", 14);
            legend.Children.Add(more);
            head.Children.Add(legend);
            var header = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 40, 0) };
            StyleText(header, "KX_SectionHeader", 0);
            head.Children.Add(header);
            panel.Children.Add(head);

            int played = days.Values.Count(d => d.Day >= first.Date && d.Day <= last.Date && d.Seconds > 0);
            string summary = played == 0
                ? Loc("LOC_KX_HeatNone", "No timed play sessions here yet (play from Playnite and they're recorded)")
                : string.Format(played == 1 ? Loc("LOC_KX_HeatDaysOne", "1 day with play") : Loc("LOC_KX_HeatDays", "{0} days with play"), played)
                  + "   ·   " + Loc("LOC_KX_HeatHow", "Select the calendar to look at a day");
            var info = new TextBlock { Text = summary, Margin = new Thickness(4, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 1500 };
            StyleText(info, "KX_TextSub", 0);
            Func<DateTime, string> describe = day =>
            {
                PlayDay d;
                string when = day.ToString("ddd d MMM", CultureInfo.CurrentCulture);
                if (!days.TryGetValue(day.Date, out d) || d.Seconds <= 0) return when + "   ·   " + Loc("LOC_KX_HeatNoPlay", "No play");
                string games = string.Join(", ", d.Games.Take(4).Select(x => x.Item2).Where(n => !string.IsNullOrEmpty(n)).ToArray());
                if (d.Games.Count > 4) games += string.Format(Loc("LOC_KX_HeatMoreGames", " and {0} more"), d.Games.Count - 4);
                return when + "   ·   " + DurText(d.Seconds) + (games.Length > 0 ? "   ·   " + games : "");
            };
            var map = new PlayHeatmap(first.Date, last.Date, days, accent);
            map.Picked = day =>
            {
                PlayDay d;
                if (!days.TryGetValue(day, out d) || d.Games.Count == 0) { PlaySound("back"); return; }
                Guid id;
                if (Guid.TryParse(d.Games[0].Item1, out id)) OpenGameFromPage(id); else PlaySound("back");
            };
            map.Moved = day => { info.Text = day == DateTime.MinValue ? summary : describe(day); };
            map.MovedSound = () => PlaySound("move");
            panel.Children.Add(map);
            panel.Children.Add(info);
            pageHeatmap = panel;
            Notify("PageHeatmap");
            Notify("HasPageHeatmap");
            if (heatHadFocus)
            {
                heatHadFocus = false;
                int gen = pageGen;
                ui.BeginInvoke(new Action(() => { if (gen == pageGen && map.IsVisible) map.Focus(); }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>The theme's text styles (Main.xaml) on a TextBlock made here.</summary>
        private static void StyleText(TextBlock t, string style, double size)
        {
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                var st = w != null ? w.TryFindResource(style) as Style : null;
                if (st != null && st.TargetType.IsAssignableFrom(typeof(TextBlock))) t.Style = st;
                else t.Foreground = Brushes.White;
            }
            catch (Exception) { t.Foreground = Brushes.White; }
            if (size > 0) t.FontSize = size;
        }

        /// <summary>The accent colour showing now (KX_AccentBrush), or the theme's blue.</summary>
        internal static Color AccentColorNow()
        {
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                var b = (w != null ? w.TryFindResource("KX_AccentBrush") : Application.Current.TryFindResource("KX_AccentBrush")) as SolidColorBrush;
                if (b != null) return b.Color;
            }
            catch (Exception) { }
            return Color.FromRgb(0x8A, 0xB4, 0xFF);
        }
    }

    /// <summary>The calendar itself: squares drawn once into one visual, the highlight in another (moving it redraws only that).</summary>
    internal class PlayHeatmap : FrameworkElement
    {
        private const double Cell = 20, Gap = 4, Step = Cell + Gap, LeftPad = 46, TopPad = 26;
        private readonly DateTime first, last, firstMonday;
        private readonly int weeks;
        private readonly Dictionary<DateTime, PlayDay> days;
        private readonly Color accent;
        private readonly DrawingVisual cells = new DrawingVisual();
        private readonly DrawingVisual ring = new DrawingVisual();
        private DateTime sel;
        private bool drawn;
        public Action<DateTime> Picked;
        public Action<DateTime> Moved;
        public Action MovedSound;

        public PlayHeatmap(DateTime first, DateTime last, Dictionary<DateTime, PlayDay> days, Color accent)
        {
            this.first = first;
            this.last = last;
            this.days = days ?? new Dictionary<DateTime, PlayDay>();
            this.accent = accent;
            firstMonday = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
            weeks = (int)((last - firstMonday).TotalDays / 7) + 1;
            sel = last;
            Focusable = true;
            FocusVisualStyle = null;
            HorizontalAlignment = HorizontalAlignment.Left;
            AddVisualChild(cells);
            AddVisualChild(ring);
        }

        protected override int VisualChildrenCount { get { return 2; } }
        protected override Visual GetVisualChild(int index) { return index == 0 ? (Visual)cells : ring; }

        protected override Size MeasureOverride(Size available)
        {
            return new Size(LeftPad + weeks * Step - Gap + 6, TopPad + 7 * Step - Gap + 6);
        }

        internal static Brush LevelBrush(int level, Color accent)
        {
            Color c;
            switch (level)
            {
                case 1: c = Color.FromArgb(0x55, accent.R, accent.G, accent.B); break;
                case 2: c = Color.FromArgb(0x8C, accent.R, accent.G, accent.B); break;
                case 3: c = Color.FromArgb(0xC8, accent.R, accent.G, accent.B); break;
                case 4: c = Color.FromArgb(0xFF, accent.R, accent.G, accent.B); break;
                default: c = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF); break;
            }
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>Up to 30 min, 1½ h, 3 h, more.</summary>
        private static int LevelOf(double seconds)
        {
            if (seconds <= 0) return 0;
            if (seconds <= 1800) return 1;
            if (seconds <= 5400) return 2;
            if (seconds <= 10800) return 3;
            return 4;
        }

        private Rect CellRect(DateTime d)
        {
            int col = (int)((d.Date - firstMonday).TotalDays / 7);
            int row = ((int)d.DayOfWeek + 6) % 7;
            return new Rect(3 + LeftPad + col * Step, 3 + TopPad + row * Step, Cell, Cell);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!drawn) { drawn = true; DrawCells(); }
            DrawRing();
        }

        private FormattedText Label(string text, double size, Brush brush)
        {
            var tf = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double dip = 1;
            try { dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (Exception) { }
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush, dip);
        }

        private void DrawCells()
        {
            var brushes = new Brush[5];
            for (int i = 0; i < 5; i++) brushes[i] = LevelBrush(i, accent);
            var dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            dim.Freeze();
            using (var dc = cells.RenderOpen())
            {
                // month names over the week each month starts in
                int lastMonthCol = -10;
                for (var d = first; d <= last; d = d.AddDays(1))
                {
                    if (d.Day != 1 && d != first) continue;
                    int col = (int)((d - firstMonday).TotalDays / 7);
                    if (col - lastMonthCol < 3) continue;   // (a month starting near the edge: no squashed labels)
                    lastMonthCol = col;
                    dc.DrawText(Label(d.ToString("MMM", CultureInfo.CurrentCulture), 14, dim), new Point(3 + LeftPad + col * Step, 2));
                }
                var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
                foreach (int r in new[] { 0, 2, 4 })
                    dc.DrawText(Label(names[(r + 1) % 7], 13, dim), new Point(0, 3 + TopPad + r * Step + 2));
                for (var d = first; d <= last; d = d.AddDays(1))
                {
                    PlayDay p;
                    int level = days.TryGetValue(d, out p) ? LevelOf(p.Seconds) : 0;
                    dc.DrawRoundedRectangle(brushes[level], null, CellRect(d), 4, 4);
                }
            }
        }

        private void DrawRing()
        {
            using (var dc = ring.RenderOpen())
            {
                if (!IsKeyboardFocused) return;
                var r = CellRect(sel);
                r.Inflate(3, 3);
                var pen = new Pen(Brushes.White, 2.5);
                pen.Freeze();
                dc.DrawRoundedRectangle(null, pen, r, 6, 6);
            }
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            DrawRing();
            if (Moved != null) Moved(sel);
            BringCellIntoView();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            DrawRing();
            if (Moved != null) Moved(DateTime.MinValue);
        }

        private void BringCellIntoView()
        {
            var r = CellRect(sel);
            r.Inflate(40, 20);
            BringIntoView(r);
        }

        private bool Go(DateTime to)
        {
            if (to < first || to > last) return false;
            sel = to;
            DrawRing();
            if (Moved != null) Moved(sel);
            if (MovedSound != null) MovedSound();
            BringCellIntoView();
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            int row = ((int)sel.DayOfWeek + 6) % 7;
            switch (e.Key)
            {
                case Key.Left: if (Go(sel.AddDays(-7))) e.Handled = true; return;
                case Key.Right: if (Go(sel.AddDays(7))) e.Handled = true; return;
                case Key.Up: if (row > 0 && Go(sel.AddDays(-1))) e.Handled = true; return;
                case Key.Down: if (row < 6 && Go(sel.AddDays(1))) e.Handled = true; return;
                case Key.Enter:
                case Key.Space:
                    if (Picked != null) Picked(sel);
                    e.Handled = true;
                    return;
            }
            // the controller's A (Playnite raises its buttons as key presses with the button on them)
            if (e.GetType().Name == "GameControllerInputEventArgs")
            {
                try
                {
                    var t = e.GetType();
                    PropertyInfo b = t.GetProperty("Button"), st = t.GetProperty("ButtonState");
                    object bv = b != null ? b.GetValue(e, null) : null, sv = st != null ? st.GetValue(e, null) : null;
                    if (bv != null && bv.ToString() == "A")
                    {
                        if ((sv == null || sv.ToString() == "Pressed") && Picked != null) Picked(sel);
                        e.Handled = true;
                    }
                }
                catch (Exception) { }
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var p = e.GetPosition(this);
            int col = (int)Math.Floor((p.X - 3 - LeftPad) / Step), row = (int)Math.Floor((p.Y - 3 - TopPad) / Step);
            if (col < 0 || row < 0 || row > 6) return;
            var d = firstMonday.AddDays(col * 7 + row);
            if (d < first || d > last) return;
            Focus();
            Go(d);
            e.Handled = true;
        }
    }
}

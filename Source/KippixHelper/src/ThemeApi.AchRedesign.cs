using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.89, per page since 0.90) Achievements › settings › Layout (this page): the all-games page and a game's page each
    /// have their own layout. Classic (grid / list / centred) or one of four designs, which swap the cards
    /// (KX_AchGameTpl1-4 / KX_AchItemTpl1-4 in the theme) and set their own widths and margins:
    ///   1 Showcase      posters with a % badge; big icon tiles edged in the rarity colour; big numbers on top
    ///   2 Trophy rings  a progress ring on every game; icons in a rarity-coloured ring
    ///   3 Timeline      one long line with a dot for each unlock, newest first
    ///   4 Compact       slim two-column rows, lots on screen at once
    /// One achievement's page follows its game's page: each design has its own version of it.
    /// Settings: AchRedesign + AchLayout (all games), AchItemRedesign + AchItemLayout (a game's page).
    /// </summary>
    public partial class ThemeApi
    {
        private int AchGamesDesign { get { return Math.Max(0, Math.Min(4, settings.AchRedesign)); } }
        private int AchItemsDesign { get { return settings.AchItemRedesign < 0 ? AchGamesDesign : Math.Max(0, Math.Min(4, settings.AchItemRedesign)); } }
        private bool achDesignSub;     // the Layout (this page) list is open in the settings dropdown

        // the choices in Layout (this page): 0-2 Classic grid / list / centred, 3-6 the designs 1-4
        private bool AchOnGamesPage { get { return achPage == "games"; } }
        private int AchPageOption
        {
            get
            {
                int d = AchOnGamesPage ? AchGamesDesign : AchItemsDesign;
                return d > 0 ? d + 2 : (AchOnGamesPage ? AchLayout : AchItemLayoutNow);
            }
        }

        private string AchOptionName(int o)
        {
            switch (o)
            {
                case 0: return Loc("LOC_KX_AchOptGrid", "Classic grid");
                case 1: return Loc("LOC_KX_AchOptList", "Classic list");
                case 2: return Loc("LOC_KX_AchOptCentred", "Classic centred");
                case 3: return Loc("LOC_KX_AchDesign1", "Showcase");
                case 4: return Loc("LOC_KX_AchDesign2", "Trophy rings");
                case 5: return Loc("LOC_KX_AchDesign3", "Timeline");
                default: return Loc("LOC_KX_AchDesign4", "Compact");
            }
        }

        private string AchOptionBlurb(int o)
        {
            switch (o)
            {
                case 0: return Loc("LOC_KX_AchOptGridSub", "Cards side by side");
                case 1: return Loc("LOC_KX_AchOptListSub", "One wide card per row");
                case 2: return Loc("LOC_KX_AchOptCentredSub", "Wide cards, everything down the middle");
                case 3: return Loc("LOC_KX_AchDesign1Sub", "Posters and big icon tiles");
                case 4: return Loc("LOC_KX_AchDesign2Sub", "A progress ring on every game");
                case 5: return Loc("LOC_KX_AchDesign3Sub", "Your unlocks along a line, newest first");
                default: return Loc("LOC_KX_AchDesign4Sub", "Slim rows, lots on screen");
            }
        }

        // each design's card widths (1920 wide), page margin and the longest progress bar on its game cards
        private static double DesignGameW(int d) { return new double[] { 0, 200, 574, 1680, 888 }[Math.Max(0, Math.Min(4, d))]; }
        private static double DesignItemW(int d) { return new double[] { 0, 200, 868, 1680, 888 }[Math.Max(0, Math.Min(4, d))]; }
        private static double DesignBarMax(int d) { return new double[] { 300, 150, 300, 400, 220 }[Math.Max(0, Math.Min(4, d))]; }
        private static Thickness DesignMargin(int d)
        {
            switch (d)
            {
                case 3: return new Thickness(120, 30, 120, 30);   // (0.96: everything a little higher, so two whole rows fit)
                case 4: return new Thickness(60, 30, 60, 30);
                default: return new Thickness(80, 30, 80, 30);
            }
        }

        // the header of the all-games page
        public bool IsAchHeroStats { get { int d = AchGamesDesign; return (d == 1 || d == 2) && achData != null && achData.Count > 0; } }
        public bool ShowAchSummaryLine { get { return !IsAchHeroStats; } }
        public string AchStatUnlocked { get { return achData == null ? "0" : achData.Sum(d => d.Unlocked).ToString("N0", CultureInfo.CurrentCulture); } }
        public string AchStatPercent
        {
            get
            {
                if (achData == null) return "0%";
                int total = achData.Sum(d => d.Total), got = achData.Sum(d => d.Unlocked);
                return (total > 0 ? Math.Floor(100.0 * got / total) : 0) + "%";
            }
        }
        public string AchStatPerfect { get { return achData == null ? "0" : achData.Count(d => d.Total > 0 && d.Unlocked == d.Total).ToString("N0", CultureInfo.CurrentCulture); } }
        public string AchStatGames { get { return achData == null ? "0" : achData.Count.ToString("N0", CultureInfo.CurrentCulture); } }

        // a game's page header
        public bool IsAchGameRingShown { get { return AchItemsDesign == 2; } }
        public bool IsAchGameBigPercent { get { return AchItemsDesign == 1; } }
        public bool IsAchGameDesignStat { get { return IsAchGameRingShown || IsAchGameBigPercent; } }
        public bool ShowAchGameBar { get { return AchItemsDesign != 2; } }
        public string AchGamePercentText { get { return achGame == null ? "" : Math.Floor(Fraction(achGame) * 100) + "%"; } }
        public Geometry AchGameRing { get { return achGame == null ? null : RingGeometry(Fraction(achGame), 44, 40); } }

        // ---------------- one achievement's page, in its game's page's design ----------------

        // (0.92) Showcase, Timeline and Compact show one achievement the same way as Classic; only Trophy rings has its own.
        public bool IsAchDetailClassic { get { return AchItemsDesign != 2; } }
        public bool IsAchDetailShowcase { get { return false; } }
        public bool IsAchDetailRings { get { return AchItemsDesign == 2; } }
        public bool IsAchDetailTimeline { get { return false; } }
        public bool IsAchDetailCompact { get { return false; } }
        public bool ShowAchDetailTitle { get { return true; } }
        public HorizontalAlignment AchDetailAlign { get { return HorizontalAlignment.Left; } }
        public TextAlignment AchDetailTextAlign { get { return TextAlignment.Left; } }
        public Brush AchDetailTierBrush { get { return achItemRow != null && achItemRow.TierBrush != null ? achItemRow.TierBrush : NoTierBrush; } }
        public string AchDetailTierName { get { return achItemRow != null ? achItemRow.TierName : ""; } }
        public bool HasAchDetailTier { get { return achItemRow != null && achItemRow.HasTier; } }
        public string AchDetailRarityPct
        {
            get
            {
                var d = achItemRow != null ? achItemRow.Data : null;
                return d != null && d.Rarity.HasValue ? d.Rarity.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "—";
            }
        }
        /// <summary>Trophy rings: unlocked = a full green ring with the tick on it; locked = only the faint ring.</summary>
        public Geometry AchDetailRarityRing
        {
            get
            {
                var d = achItemRow != null ? achItemRow.Data : null;
                if (d == null) return null;
                return d.Unlocked ? RingGeometry(1, 130, 122) : null;   // (0.93) locked: just the faint ring under it
            }
        }
        private static readonly Brush UnlockedGreen = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
        public Brush AchDetailRingBrush { get { return achItemRow != null && achItemRow.IsUnlocked ? UnlockedGreen : AchDetailTierBrush; } }
        /// <summary>Timeline: the day it was unlocked (or Locked).</summary>
        public string AchDetailDateShort
        {
            get
            {
                var d = achItemRow != null ? achItemRow.Data : null;
                if (d == null) return "";
                if (!d.Unlocked) return Loc("LOC_KX_Locked", "Locked");
                return d.Date.HasValue ? d.Date.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : Loc("LOC_KX_Unlocked", "Unlocked");
            }
        }
        public bool AchDetailLocked { get { return achItemRow != null && !achItemRow.IsUnlocked; } }

        private static readonly string[] AchDetailDesignProps =
        {
            "IsAchDetailClassic", "IsAchDetailShowcase", "IsAchDetailRings", "IsAchDetailTimeline", "IsAchDetailCompact", "ShowAchDetailTitle",
            "AchDetailAlign", "AchDetailTextAlign", "AchDetailTierBrush", "AchDetailTierName", "HasAchDetailTier", "AchDetailRarityPct",
            "AchDetailRarityRing", "AchDetailRingBrush", "AchDetailDateShort", "AchDetailLocked"
        };

        private void NotifyAchDetailDesign() { foreach (string n in AchDetailDesignProps) Notify(n); }

        private void NotifyAchDesign()
        {
            foreach (string n in new[] { "IsAchHeroStats", "ShowAchSummaryLine", "AchStatUnlocked", "AchStatPercent", "AchStatPerfect", "AchStatGames",
                                         "IsAchGameRingShown", "IsAchGameBigPercent", "IsAchGameDesignStat", "ShowAchGameBar", "AchGamePercentText", "AchGameRing",
                                         "AchPageMargin", "AchHeaderAlignment", "AchGameDesignNow", "AchItemDesignNow" })
                Notify(n);
            NotifyAchDetailDesign();
        }

        /// <summary>The Layout (this page) list (in the settings dropdown): pick one and the page changes straight away; the list
        /// stays open so you can try them all. B goes back to the settings.</summary>
        private void OpenAchDesignPicker()
        {
            AchPickerOptions.Clear();
            PickOption focus = null;
            int now = AchPageOption;
            for (int i = 0; i <= 6; i++)
            {
                int o = i;
                var opt = new PickOption
                {
                    Key = "layout" + i,
                    Text = AchOptionName(i) + "   ·   " + AchOptionBlurb(i),
                    IsSelected = i == now,
                    Command = new RelayCommand(_ => SetAchPageOption(o))
                };
                if (opt.IsSelected) focus = opt;
                AchPickerOptions.Add(opt);
            }
            achPicker = "settings";
            achDesignSub = true;
            NotifyPicker();
            string list = PickerListName("settings");
            if (focus == null) focus = AchPickerOptions[0];
            ui.BeginInvoke(new Action(() => UiTools.FocusItem(list, focus)), DispatcherPriority.ContextIdle);
        }

        private void SetAchPageOption(int o)
        {
            int design = o >= 3 ? o - 2 : 0;
            if (AchOnGamesPage)
            {
                settings.AchRedesign = design;
                if (design == 0) settings.AchLayout = o;
            }
            else
            {
                settings.AchItemRedesign = design;
                if (design == 0) settings.AchItemLayout = o;
            }
            SaveSettings();
            PlaySound("move");
            foreach (var r in achRows) DressAchGameRow(r);
            foreach (var r in achItemRows) DressAchItemRow(r);
            ApplyAchLayout();
            ApplyAchDesignTemplates(true);
            if (achGame != null && !AchOnGamesPage) ApplyAchItems();
            OpenAchDesignPicker();
        }

        // ---------------- the cards ----------------

        private ItemsControl achDesGameList, achDesItemList;

        /// <summary>Puts each page's design's card templates on its list (Classic: the ones written in the theme).</summary>
        private void ApplyAchDesignTemplates() { ApplyAchDesignTemplates(false); }

        /// <summary>(0.94) The cards follow AchGameDesignNow / AchItemDesignNow in the theme (each list's card picks its template
        /// from them), so changing the layout is a notification; here the lists only get their whole-row scrolling.</summary>
        private void ApplyAchDesignTemplates(bool regen)
        {
            try
            {
                if (!IsAchievementsOpen) { NotifyAchDesign(); return; }
                if (achDesGameList == null || achDesItemList == null || !achDesGameList.IsLoaded || !achDesItemList.IsLoaded)
                {
                    var win = Application.Current != null ? Application.Current.MainWindow : null;
                    if (win != null)
                    {
                        achDesGameList = UiTools.FindByName(win, "KX_AchGameList") as ItemsControl;
                        achDesItemList = UiTools.FindByName(win, "KX_AchItemList") as ItemsControl;
                        HookRowScroll(achDesGameList);
                        HookRowScroll(achDesItemList);
                    }
                }
            }
            catch (Exception ex) { logger.Error(ex, "Kippix: achievements layout"); }
            NotifyAchDesign();
            if (regen) ui.BeginInvoke(new Action(() =>
            {
                foreach (var l in new[] { achDesGameList, achDesItemList })
                {
                    try
                    {
                        var sv = l != null ? VisualParentOf<ScrollViewer>(l) : null;
                        if (sv != null) { sv.ScrollToTop(); ClipToRows(l, sv); }
                    }
                    catch (Exception) { }
                }
            }), DispatcherPriority.Loaded);
        }

        public int AchGameDesignNow { get { return AchGamesDesign; } }
        public int AchItemDesignNow { get { return AchItemsDesign; } }

        // ---------------- (0.92) scrolling by whole rows ----------------

        private readonly System.Collections.Generic.HashSet<ItemsControl> rowScrollHooked = new System.Collections.Generic.HashSet<ItemsControl>();

        /// <summary>Moving the highlight up / down a list scrolls by whole rows, and (0.93) the list only ever shows whole rows:
        /// its height is cut to a number of rows, so no row is half off the bottom (or top) of it.</summary>
        private void HookRowScroll(ItemsControl list)
        {
            if (list == null || rowScrollHooked.Contains(list)) return;
            rowScrollHooked.Add(list);
            list.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((s, e) => RowScroll(list, e)));
            var sv = VisualParentOf<ScrollViewer>(list);
            if (sv != null)
                sv.ScrollChanged += (s, e) =>
                {
                    if (Math.Abs(e.ExtentHeightChange) > 0.5 || Math.Abs(e.ViewportHeightChange) > 0.5 || Math.Abs(e.ViewportWidthChange) > 0.5)
                        ClipToRows(list, sv);
                };
        }

        /// <summary>The tops of the list's rows (in the scrolled content), top first.</summary>
        private static List<double> RowTops(ItemsControl list, ScrollViewer sv)
        {
            var tops = new List<double>();
            var gen = list.ItemContainerGenerator;
            double off = sv.VerticalOffset;
            for (int i = 0; i < list.Items.Count; i++)
            {
                var c = gen.ContainerFromIndex(i) as FrameworkElement;
                if (c == null || !c.IsVisible || c.ActualHeight <= 0) continue;
                double t = Math.Round(c.TransformToAncestor(sv).Transform(new Point(0, 0)).Y + off);
                if (!tops.Any(x => Math.Abs(x - t) < 1.5)) tops.Add(t);
            }
            tops.Sort();
            return tops;
        }

        /// <summary>How much of the list shows: as many whole rows as fit (all of it when the rows aren't even).</summary>
        private static double WholeRowsHeight(List<double> tops, double vh)
        {
            if (tops.Count < 2) return vh;
            double step = double.MaxValue;
            for (int i = 1; i < tops.Count; i++) step = Math.Min(step, tops[i] - tops[i - 1]);
            if (step <= 4 || step > vh) return vh;
            for (int i = 1; i < tops.Count; i++) if (Math.Abs(tops[i] - tops[i - 1] - step) > 2) return vh;   // (rows of different heights)
            return Math.Floor((vh + 0.5) / step) * step;
        }

        private static void ClipToRows(ItemsControl list, ScrollViewer sv)
        {
            try
            {
                double vh = sv.ViewportHeight;
                if (vh <= 0 || sv.ActualWidth <= 0) return;
                double h = WholeRowsHeight(RowTops(list, sv), vh);
                if (h >= vh - 1) sv.Clip = null;
                else sv.Clip = new RectangleGeometry(new Rect(-40, 0, sv.ActualWidth + 80, h + 1));
                // room under the last row, so it can scroll up into the part that shows
                double pad = Math.Max(0, Math.Round(vh - h));
                if (Math.Abs(list.Margin.Bottom - pad) > 0.5) list.Margin = new Thickness(list.Margin.Left, list.Margin.Top, list.Margin.Right, pad);
            }
            catch (Exception) { }
        }

        private static void RowScroll(ItemsControl list, RequestBringIntoViewEventArgs e)
        {
            try
            {
                var target = e.TargetObject as FrameworkElement;
                if (target == null || ReferenceEquals(target, list)) return;
                var sv = VisualParentOf<ScrollViewer>(list);
                if (sv == null || sv.ViewportHeight <= 0) return;
                // the card the highlight is in
                var card = ItemsControl.ContainerFromElement(list, target) as FrameworkElement;
                if (card == null) return;
                var tops = RowTops(list, sv);
                double off = sv.VerticalOffset, vh = WholeRowsHeight(tops, sv.ViewportHeight);
                ClipToRows(list, sv);
                Rect r = card.TransformToAncestor(sv).TransformBounds(new Rect(card.RenderSize));
                double top = r.Top + off, bottom = r.Bottom + off;
                e.Handled = true;
                if (top >= off - 0.5 && bottom <= off + vh + 0.5) return;   // already all on screen
                double want;
                if (top < off) want = top;
                else
                {
                    // the highest row top that still shows the whole card
                    double min = bottom - vh;
                    want = top;
                    foreach (double t in tops) if (t >= min - 0.5 && t < want) want = t;
                }
                sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, want)));
            }
            catch (Exception) { }
        }

        private static T VisualParentOf<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                d = VisualTreeHelper.GetParent(d);
                var t = d as T;
                if (t != null) return t;
            }
            return null;
        }

        private void DressAchGameRow(AchGameRow r)
        {
            if (r == null || r.Data == null) return;
            var d = r.Data;
            double f = Fraction(d);
            r.PercentText = Math.Floor(f * 100) + "%";
            r.UnlockedText = d.Unlocked.ToString("N0", CultureInfo.CurrentCulture) + " / " + d.Total.ToString("N0", CultureInfo.CurrentCulture);
            r.DateText = d.LastUnlock.HasValue ? d.LastUnlock.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "—";
            r.Ring = RingGeometry(f, 48, 44);
            r.ProgressWidth = Math.Max(f > 0 ? 6 : 0, f * AchGameBarMax);
        }

        private static Brush[] tierBrushes;   // made on first use (TierColors lives in another file, so not in a static initialiser)
        private static Brush[] TierBrushes
        {
            get { return tierBrushes ?? (tierBrushes = TierColors.Select(c => (Brush)Frozen((Color)ColorConverter.ConvertFromString(c))).ToArray()); }
        }
        private static readonly Brush NoTierBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

        private void DressAchItemRow(AchItemRow r)
        {
            if (r == null || r.Data == null) return;
            var i = r.Data;
            r.HasTier = i.Rarity.HasValue || !string.IsNullOrEmpty(i.RarityTier);
            int k = AchTierOf(i);
            string[] names = { Loc("LOC_KX_TierCommon", "Common"), Loc("LOC_KX_TierUncommon", "Uncommon"), Loc("LOC_KX_TierRare", "Rare"), Loc("LOC_KX_TierUltra", "Ultra rare") };
            r.TierName = r.HasTier ? names[k] : "";
            r.TierBrush = r.HasTier ? TierBrushes[k] : NoTierBrush;
            r.RarityBarWidth = i.Rarity.HasValue ? Math.Max(3, Math.Min(100, i.Rarity.Value) / 100.0 * 160) : 0;
        }

        /// <summary>An arc from the top, clockwise, for f (0-1) of a circle around (c, c) with radius r.</summary>
        private static Geometry RingGeometry(double f, double c, double r)
        {
            if (f <= 0.001) return null;
            if (f >= 0.999) { var e = new EllipseGeometry(new Point(c, c), r, r); e.Freeze(); return e; }
            double a = f * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
    }
}

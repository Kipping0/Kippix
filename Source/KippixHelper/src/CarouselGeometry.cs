using System;
using System.Collections.Generic;

namespace KippixHelper
{
    /// <summary>One card's rectangle in the carousel host (its place along the carousel is V).</summary>
    internal struct CarSlotRect
    {
        public int V;
        public double X, Y, W, H;
        /// <summary>How highlighted it is: 1 on the highlight, 0 a step or more away (the edge and glow follow it).</summary>
        public double Near;
        public bool Selected;
    }

    /// <summary>The sizes worked out for a design and a host size (see CarouselGeometry.Measure).</summary>
    internal sealed class CarouselFrame
    {
        public int Design;                 // 0 Lower, 1 Tall, 2 Full Screen (Folder's tiles are Full Screen)
        public double HostW, HostH, TopY, BottomInset;
        public int Count;                  // Full Screen: panels across the screen
        public double W, H;                // a card's size (not highlighted)
        public double Gap, Step;           // the gap between cards; one card to the next
        public double Top;                 // the cards' top edge
        public double Anchor;              // Lower / Tall: the highlighted card's left edge
        public double Grow;                // the highlighted card's size against the others
        public bool GrowFromBottom;        // Lower grows from its bottom edge (like Playnite's row); Tall from the middle
        public int SpanLeft, SpanRight;    // Lower / Tall: places drawn either side of the highlight (one spare each side for sliding)

        public string Signature
        {
            get
            {
                return Design + "|" + R(HostW) + "x" + R(HostH) + "|top " + R(TopY) + "|n " + Count + "|" + R(W) + "x" + R(H) + "|gap " + R(Gap) + "|y " + R(Top) + "|x " + R(Anchor);
            }
        }
        private static string R(double d) { return Math.Round(d, 1).ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// Row's carousel: where every card goes, as plain numbers (nothing from WPF here, so it can be checked on its own).
    /// Tall: big 2:3 covers a quarter of the screen wide, the highlighted one in the middle (4% bigger), three whole ones and
    /// the next ones cut off at the edges. Full Screen (and Folder's tiles): N equal panels edge to edge with a 3 px gap, from
    /// under the top bar to the bottom of the screen. Lower: Playnite's own row's card size and place.
    /// </summary>
    internal static class CarouselGeometry
    {
        public const double TallWidthPart = 0.25;    // a Tall card: a quarter of the screen's width
        public const double TallShape = 1.5;         // ... and 2:3 (height = width x 1.5)
        public const double TallGapPart = 0.01;      // the gap: 1% of the screen's width
        public const double TallMarginPart = 0.025;  // room above and below the cards: 2.5% of the screen's height
        public const double TallGrow = 1.04;
        public const double LowerGrow = 1.06;
        public const double FullGap = 3;
        public const int FullMin = 1, FullMax = 10;   // (0.89: from 1)

        /// <summary>
        /// The sizes for a design. <paramref name="topY"/>: the bottom of the top bar (and the filter name) in the host;
        /// <paramref name="bottomInset"/>: room kept free at the bottom (Tall with the logo / info / hints); <paramref name="count"/>:
        /// Full Screen's panels across; row*: Lower's card size and place (from Playnite's row). Null while the host has no size.
        /// </summary>
        public static CarouselFrame Measure(int design, double hostW, double hostH, double topY, double bottomInset, int count,
                                            double rowW, double rowH, double rowLeft, double rowTop)
        {
            return Measure(design, hostW, hostH, topY, bottomInset, count, rowW, rowH, rowLeft, rowTop, 1, 1);
        }

        /// <summary>(0.89) ... with Tall's covers <paramref name="scaleW"/> / <paramref name="scaleH"/> times their own width and height
        /// (Free move › Y; <paramref name="count"/> is Tall's whole covers across too), and Full Screen's panels
        /// <paramref name="scaleH"/> of the height from the top.</summary>
        public static CarouselFrame Measure(int design, double hostW, double hostH, double topY, double bottomInset, int count,
                                            double rowW, double rowH, double rowLeft, double rowTop, double scaleW, double scaleH, double tallAspect = 0)
        {
            double tallFill = Ok(scaleH) && scaleH > 0 ? Math.Max(0.2, Math.Min(1, scaleH)) : 0;   // (0.90) Tall: the covers' height, part of the room (0: 2:3, as fits)
            if (!Ok(scaleW) || scaleW <= 0) scaleW = 1;
            if (!Ok(scaleH) || scaleH <= 0) scaleH = 1;
            if (!Ok(hostW) || !Ok(hostH) || hostW < 50 || hostH < 50) return null;
            topY = Ok(topY) ? Math.Max(0, Math.Min(hostH * 0.5, topY)) : 0;
            bottomInset = Ok(bottomInset) ? Math.Max(0, Math.Min(hostH * 0.6, bottomInset)) : 0;
            var f = new CarouselFrame { Design = design, HostW = hostW, HostH = hostH, TopY = topY, BottomInset = bottomInset, Count = 1 };
            if (design == 1)
            {
                double margin = Math.Max(8, hostH * TallMarginPart);
                double regionTop = topY + margin;
                double regionH = Math.Max(60, hostH - bottomInset - margin - regionTop);
                int across = Math.Max(1, Math.Min(10, count));
                double gapNow = hostW * TallGapPart;
                double w = (hostW - across * gapNow) / (across + 1);   // (3 across: a quarter of the screen, as before)
                // (0.90) Card look's shape: height for width (Tall covers 2:3; square, wide, hexagon, triangle their own)
                double aspect = Ok(tallAspect) && tallAspect > 0 ? tallAspect : TallShape;
                bool cover = Math.Abs(aspect - TallShape) < 0.001;
                double h = w * aspect;
                double maxH = regionH / TallGrow;
                if (cover && tallFill > 0) h = maxH * tallFill;   // (0.90) covers: a height of their own (Free move › Up / Down); the width stays as the count makes it
                else
                {
                    // too tall for the room (the highlighted one 4% bigger still inside it), or than the height chosen: as tall as fits, same shape
                    double cap = tallFill > 0 ? maxH * tallFill : maxH;
                    if (h > cap) { w = w * cap / h; h = cap; }
                }
                f.W = w;
                f.H = h;
                f.Gap = hostW * TallGapPart;
                f.Step = w + f.Gap;
                f.Top = regionTop + (regionH - h) / 2;
                f.Anchor = (hostW - w) / 2;
                f.Grow = TallGrow;
                // the cards needed to reach each edge, and one spare for sliding
                int reach = (int)Math.Ceiling(Math.Max(0, f.Anchor) / f.Step);
                f.SpanLeft = f.SpanRight = reach + 1;
            }
            else if (design == 2)
            {
                int n = Math.Max(FullMin, Math.Min(FullMax, count));
                f.Count = n;
                f.Gap = FullGap;
                f.W = (hostW - (n - 1) * FullGap) / n;
                f.Step = f.W + FullGap;
                f.Top = topY;
                f.H = Math.Max(60, (hostH - topY) * Math.Max(0.3, Math.Min(1, scaleH)));
                f.Anchor = 0;
                f.Grow = 1;
                f.SpanLeft = f.SpanRight = 1;
            }
            else
            {
                if (!Ok(rowW) || !Ok(rowH) || rowW < 10 || rowH < 10) return null;
                f.W = rowW;
                f.H = rowH;
                f.Gap = 0;                          // (Playnite's card has its own spacing inside it)
                f.Step = rowW;
                f.Top = Ok(rowTop) ? rowTop : hostH - rowH;
                f.Anchor = (Ok(rowLeft) ? rowLeft : 0) + rowW * 0.25;   // the first card a quarter of a card in, like Playnite's row
                f.Grow = LowerGrow;
                f.GrowFromBottom = true;
                f.SpanLeft = (int)Math.Ceiling(Math.Max(0, f.Anchor) / f.Step) + 1;
                f.SpanRight = (int)Math.Ceiling(Math.Max(0, hostW - f.Anchor) / f.Step) + 1;
            }
            return f;
        }

        /// <summary>
        /// The places to draw: Lower / Tall around the highlight (<paramref name="pos"/>), Full Screen from the strip's left edge
        /// (<paramref name="view"/>), one spare either side.
        /// </summary>
        public static void Range(CarouselFrame f, double pos, double view, out int from, out int to)
        {
            if (f.Design == 2)
            {
                from = (int)Math.Floor(view) - 1;
                to = (int)Math.Ceiling(view) + f.Count;
            }
            else
            {
                from = (int)Math.Floor(pos) - f.SpanLeft;
                to = (int)Math.Ceiling(pos) + f.SpanRight;
            }
        }

        /// <summary>
        /// Place <paramref name="v"/>'s rectangle with the highlight at <paramref name="pos"/> (moving) and, for Full Screen, the
        /// strip's left edge at <paramref name="view"/>. Lower: <paramref name="shiftL"/> / <paramref name="shiftR"/> push the
        /// cards either side aside (the trailer preview).
        /// </summary>
        public static CarSlotRect Slot(CarouselFrame f, int v, double pos, double view, double shiftL, double shiftR)
        {
            return Slot(f, v, pos, view, shiftL, shiftR, 1);
        }

        /// <summary>... with Lower's highlighted card only growing as far as <paramref name="focus"/> (0..1: the row has the highlight).</summary>
        public static CarSlotRect Slot(CarouselFrame f, int v, double pos, double view, double shiftL, double shiftR, double focus)
        {
            double d = v - pos;
            double near = Smooth(d);
            double x;
            if (f.Design == 2) x = (v - view) * f.Step;
            else
            {
                x = f.Anchor + d * f.Step;
                if (f.Design == 0 && (shiftL > 0 || shiftR > 0)) x += d <= -1 ? -shiftL : d >= 1 ? shiftR : (d < 0 ? -shiftL * -d : shiftR * d);
            }
            double s = 1 + (f.Grow - 1) * near * (f.Design == 0 ? Math.Max(0, Math.Min(1, focus)) : 1);
            double w = f.W * s, h = f.H * s;
            var r = new CarSlotRect { V = v, Near = near, Selected = Math.Abs(d) < 0.5, W = w, H = h };
            r.X = x + (f.W - w) / 2;
            r.Y = f.GrowFromBottom ? f.Top + f.H - h : f.Top + (f.H - h) / 2;
            return r;
        }

        /// <summary>Every rectangle to draw (the tests; the carousel places them one by one).</summary>
        public static List<CarSlotRect> Slots(CarouselFrame f, double pos, double view)
        {
            var list = new List<CarSlotRect>();
            if (f == null) return list;
            int from, to;
            Range(f, pos, view, out from, out to);
            for (int v = from; v <= to; v++) list.Add(Slot(f, v, pos, view, 0, 0));
            return list;
        }

        /// <summary>
        /// Full Screen: the strip's left edge with the highlight on <paramref name="v"/>. It stays where it is (<paramref name="now"/>)
        /// while the highlight moves across the panels, and scrolls once the highlight reaches the first / last panel, keeping
        /// one panel of look-ahead. Without a loop it never shows past either end of the <paramref name="n"/> items.
        /// </summary>
        public static int FullViewFor(int v, int now, int count, int n, bool repeats)
        {
            int c = Math.Max(1, count);
            int ahead = c >= 3 ? 1 : 0;
            int vt = now;
            if (v > vt + c - 1 - ahead) vt = v - (c - 1 - ahead);
            if (v < vt + ahead) vt = v - ahead;
            if (!repeats) vt = Math.Max(0, Math.Min(Math.Max(0, n - c), vt));
            return vt;
        }

        /// <summary>
        /// ... with the first <paramref name="hideFirst"/> items (the tiles before the games, on Auto show) kept off to the left
        /// while the highlight is on the games: the strip starts on the first game, Left from it brings the tiles in. Without a
        /// loop, Right back onto the games puts them away again; with one, they only stay out of sight while the first game is
        /// at the left edge (from there Left shows them, and once more goes round to the end).
        /// </summary>
        public static int FullViewFor(int v, int now, int count, int n, bool repeats, int hideFirst)
        {
            int vt = FullViewFor(v, now, count, n, repeats);
            if (hideFirst <= 0 || hideFirst >= n) return vt;
            if (!repeats) return v >= hideFirst ? Math.Max(vt, hideFirst) : vt;
            int r = (v - hideFirst) % n;
            return r == 0 && now == v ? v : vt;
        }

        private static double Smooth(double d)
        {
            double t = Math.Max(0, 1 - Math.Abs(d));
            return t * t * (3 - 2 * t);
        }

        private static bool Ok(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
    }
}

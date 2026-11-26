using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.1.0) A layout made on another screen fits this one. Kippix is drawn 1080 high and as wide as the screen's shape
    /// makes it (1920 on 16:9, 2520 on 21:9, 1920 × 1200 on 16:10: ThemeApi.DesignSize.cs), and parts moved in Free move keep
    /// how far they were moved. So a layout from an ultrawide screen had parts past the right edge of a 16:9 one, and one from
    /// a 16:10 laptop had parts under the bottom. Now:
    /// - a saved or shared layout says what it was made on (LayoutDesignW / LayoutDesignH);
    /// - using one from a different size moves the widgets in proportion (they're placed from the top left);
    /// - and the first time each screen (home, game page, widgets) shows after using a layout, any moved part that's off the
    ///   screen, even partly, is brought back just inside the edge, and that's kept (LayoutFitPending). Parts that are on the
    ///   screen aren't touched, so the layout looks as it was made wherever it fits.
    /// </summary>
    public partial class ThemeApi
    {
        private const double FitMargin = 8;
        private bool layoutFitQueued;

        /// <summary>Saving a layout: what it's made on.</summary>
        private void StampLayoutSize(HelperSettings copy)
        {
            copy.LayoutDesignW = Math.Round(DesignWidth);
            copy.LayoutDesignH = Math.Round(DesignHeight);
        }

        /// <summary>Using a layout (before it's applied): widgets in proportion, then every screen to be checked as it shows.</summary>
        private void StartLayoutFit(HelperSettings loaded)
        {
            try
            {
                double fromW = loaded.LayoutDesignW, fromH = loaded.LayoutDesignH, w = DesignWidth, h = DesignHeight;
                if (fromW > 0 && fromH > 0 && (Math.Abs(fromW - w) > 2 || Math.Abs(fromH - h) > 2))
                {
                    double sx = w / fromW, sy = h / fromH;
                    if (settings.Widgets != null)
                        foreach (var c in settings.Widgets.Where(c => c != null)) { c.X = Math.Round(c.X * sx); c.Y = Math.Round(c.Y * sy); }
                    if (settings.HomePlacePerLayout != null)
                        foreach (var set in settings.HomePlacePerLayout.Values.Where(v => v != null && v.Widgets != null))
                            foreach (var p in set.Widgets.Values.Where(p => p != null && p.Length >= 2)) { p[0] = Math.Round(p[0] * sx); p[1] = Math.Round(p[1] * sy); }
                }
                settings.LayoutFitPending = new List<string> { "home", "game", "widgets" };
            }
            catch (Exception ex) { logger.Warn("Kippix: layout fit: " + ex.Message); }
        }

        /// <summary>After the moved parts are put in place: a screen that's still to be checked, checked once it's laid out.</summary>
        private void LayoutFitSoon()
        {
            if (layoutFitQueued || settings.LayoutFitPending == null || settings.LayoutFitPending.Count == 0 || !IsFullscreen) return;
            layoutFitQueued = true;
            var t = new DispatcherTimer(DispatcherPriority.ApplicationIdle, ui) { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) => { t.Stop(); layoutFitQueued = false; FitLayoutNow(); };
            t.Start();
        }

        private void FitLayoutNow()
        {
            try
            {
                var pending = settings.LayoutFitPending;
                if (pending == null || pending.Count == 0) return;
                if (freeMove || openPanel != null) { LayoutFitSoon(); return; }   // (a menu over it, e.g. the Community page it came from: when it's closed)
                var win = Application.Current != null ? Application.Current.MainWindow : null;
                if (win == null) return;
                FrameworkElement host = designHost ?? (win.Content as FrameworkElement);
                if (host == null || host.ActualWidth <= 0) return;
                double sw = DesignWidth, sh = DesignHeight;
                bool changed = false;

                foreach (string screen in new[] { "home", "game" })
                {
                    if (!pending.Contains(screen)) continue;
                    if (screen == "game" ? !detailsOpen : detailsOpen) continue;   // (checked when it's the one showing)
                    bool measured = false;
                    foreach (var el in MasterElements.Where(m => m.Screen == screen))
                    {
                        if (!MasterOn(el.Screen)) continue;
                        Rect? box = null;
                        foreach (string name in el.Names)
                        {
                            var fe = UiTools.FindByName(win, name) as FrameworkElement;
                            if (fe == null || !fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) continue;
                            Rect r;
                            try { r = fe.TransformToAncestor(host).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight)); }
                            catch (Exception) { continue; }
                            box = box.HasValue ? Rect.Union(box.Value, r) : r;
                        }
                        if (!box.HasValue) continue;
                        measured = true;
                        var o = MasterOffset(el.Key);
                        if (Math.Abs(o[0]) < 0.5 && Math.Abs(o[1]) < 0.5) continue;   // (only parts that were moved)
                        double dx = FitShift(box.Value.Left, box.Value.Right, sw), dy = FitShift(box.Value.Top, box.Value.Bottom, sh);
                        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;
                        SetMasterOffset(el.Key, o[0] + dx, o[1] + dy);
                        changed = true;
                    }
                    if (measured) pending.Remove(screen);
                }

                if (pending.Contains("widgets") && !detailsOpen && Widgets != null && Widgets.Count > 0 && Widgets.All(w => w.Width > 0))
                {
                    var layer = UiTools.FindByName(win, "KX_Widgets") as FrameworkElement;
                    double lw = layer != null && layer.ActualWidth > 0 ? layer.ActualWidth : sw, lh = layer != null && layer.ActualHeight > 0 ? layer.ActualHeight : sh;
                    foreach (var w in Widgets)
                    {
                        if (w.Config == null || w.BoardRect.HasValue) continue;   // (one in a Board tile goes where its tile is)
                        double ww = w.Width, wh = w.Height;   // (its size, scaled)
                        double dx = FitShift(w.Config.X, w.Config.X + ww, lw), dy = FitShift(w.Config.Y, w.Config.Y + wh, lh);
                        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;
                        w.Config.X = Math.Round(w.Config.X + dx);
                        w.Config.Y = Math.Round(w.Config.Y + dy);
                        w.Place();
                        changed = true;
                    }
                    pending.Remove("widgets");
                }
                else if (pending.Contains("widgets") && (Widgets == null || Widgets.Count == 0)) pending.Remove("widgets");

                if (changed)
                {
                    logger.Info("Kippix: a layout from another screen size: parts off the screen brought back on");
                    ApplyMasterOffsets();
                }
                SaveSettings();
            }
            catch (Exception ex) { logger.Warn("Kippix: layout fit: " + ex.Message); }
        }

        /// <summary>How far to move a span [a, b] to have it inside [0, size] (with a little room); 0 when it's already in.</summary>
        private static double FitShift(double a, double b, double size)
        {
            double lo = FitMargin, hi = size - FitMargin;
            if (b - a >= hi - lo) return lo - a;   // bigger than the screen: its start at the edge
            if (b > hi) return hi - b;
            if (a < lo) return lo - a;
            return 0;
        }
    }
}

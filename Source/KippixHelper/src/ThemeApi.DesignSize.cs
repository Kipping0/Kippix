using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.58) Any screen shape. Playnite draws fullscreen themes 1080 units high and as wide as the screen's shape makes
    /// that (a Viewbox scales it to the screen): 1920 wide on a 16:9 screen, but only 1728 on a 16:10 laptop (2880 × 1800),
    /// and Kippix is laid out for at least 1920 across, so on a 16:10 or 4:3 screen parts didn't line up and cards were cut off.
    /// Now the theme is never given less than 1920 × 1080: a screen narrower than 16:9 gets 1920 across and more height
    /// (1920 × 1200 on 16:10); 16:9 and wider screens are left as Playnite makes them (1080 high, wider). Scaled to the screen
    /// as before, so it fills it exactly, sharp at any resolution.
    /// </summary>
    public partial class ThemeApi
    {
        private bool designHooked;
        private FrameworkElement designHost;
        private bool designChanged;
        private int designTries;

        /// <summary>The theme's units across and down right now (1920 × 1080 on a 16:9 screen).</summary>
        internal double DesignWidth { get { return designHost != null && designHost.ActualWidth > 0 ? designHost.ActualWidth : 1920; } }
        internal double DesignHeight { get { return designHost != null && designHost.ActualHeight > 0 ? designHost.ActualHeight : 1080; } }

        private void HookDesignSize()
        {
            if (designHooked || !IsFullscreen) return;
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            if (w == null || !w.IsLoaded)
            {
                if (++designTries < 30 && ui != null)
                {
                    var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(300) };
                    t.Tick += (s, e) => { t.Stop(); HookDesignSize(); };
                    t.Start();
                }
                return;
            }
            designHooked = true;
            try
            {
                // (how Windows lets Playnite draw: 2 = the graphics card does it; 0 = the processor does it all, which is slow)
                int tier = RenderCapability.Tier >> 16;
                var src = PresentationSource.FromVisual(w) as System.Windows.Interop.HwndSource;
                string mode = src != null && src.CompositionTarget != null ? src.CompositionTarget.RenderMode.ToString() : "?";
                logger.Info("Kippix: drawing: render tier " + tier + (tier < 2 ? " (not fully by the graphics card: slow)" : " (graphics card)") + ", " + mode + ", window " + Math.Round(w.ActualWidth) + " × " + Math.Round(w.ActualHeight));
            }
            catch (Exception) { }
            w.SizeChanged += (s, e) => ui.BeginInvoke(new Action(FitDesignSize), DispatcherPriority.Loaded);
            FitDesignSize();
        }

        /// <summary>Playnite's scaled box: the child of the first Viewbox in the window.</summary>
        private static FrameworkElement FindDesignHost(DependencyObject root)
        {
            var q = new Queue<Tuple<DependencyObject, int>>();
            q.Enqueue(Tuple.Create(root, 0));
            while (q.Count > 0)
            {
                var it = q.Dequeue();
                var vb = it.Item1 as Viewbox;
                if (vb != null && vb.Child is FrameworkElement) return (FrameworkElement)vb.Child;
                if (it.Item2 > 12) continue;
                int n = 0;
                try { n = VisualTreeHelper.GetChildrenCount(it.Item1); } catch (Exception) { }
                for (int i = 0; i < n; i++) q.Enqueue(Tuple.Create(VisualTreeHelper.GetChild(it.Item1, i), it.Item2 + 1));
            }
            return null;
        }

        private void FitDesignSize()
        {
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                if (w == null || w.ActualWidth < 100 || w.ActualHeight < 100) return;
                if (designHost == null || PresentationSource.FromVisual(designHost) == null) designHost = FindDesignHost(w);
                var host = designHost;
                if (host == null) { logger.Info("Kippix: screen shape: Playnite's scaled box wasn't found (left as it is)"); return; }
                double aspect = w.ActualWidth / w.ActualHeight;
                if (aspect < 16.0 / 9 - 0.005)
                {
                    double hgt = Math.Round(1920 / aspect);
                    if (!SameSize(host.Width, 1920) || !SameSize(host.Height, hgt))
                    {
                        host.Width = 1920;
                        host.Height = hgt;
                        designChanged = true;
                        logger.Info("Kippix: screen shape " + aspect.ToString("0.###") + " (narrower than 16:9): the theme is laid out 1920 × " + hgt + " (Playnite's would have been " + Math.Round(1080 * aspect) + " × 1080)");
                    }
                }
                else if (designChanged)
                {
                    // back to 16:9 or wider (another screen): as Playnite makes it
                    host.Height = 1080;
                    host.Width = Math.Round(1080 * aspect);
                    designChanged = false;
                    logger.Info("Kippix: screen shape " + aspect.ToString("0.###") + ": the theme is laid out " + host.Width + " × 1080");
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: screen shape: " + ex.Message); }
        }
    }
}

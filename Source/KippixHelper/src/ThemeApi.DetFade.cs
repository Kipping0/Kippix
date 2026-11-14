using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.64) The game page's parts fade in. The page's own entrance fades the logo, info and cover (GameDetails.xaml, no
    /// slide any more); the parts that come in a moment later, once the game's data is in (the trailer, play time and stats,
    /// How long to beat, friends, the DLC and news buttons, the lower page's cards...), used to pop up whole. Now each one
    /// fades in as it appears, to the opacity it would have (a Free move opacity, a blank's nothing), and nothing is left
    /// on it after. Not with Reduce motion.
    /// </summary>
    public partial class ThemeApi
    {
        // (not the parts the page's entrance already fades, or the two pages' own hosts: their fades are their own)
        private static readonly string[] DetFadeNames =
        {
            "KX_DetStats", "KX_DetStatsLine", "DetButtons", "KX_DetDlc", "KX_DetFacts", "KX_DetFriends", "KX_DetDescButton", "KX_DetDesc",
            "KX_DetVideo", "VideoArea", "KX_Trailer", "KX_DetHints", "KX_DetNexus", "KX_DetNewsButton", "KX_DetMoreButton", "DetClock",
            "KX_SecHistory", "KX_SecHltb", "KX_SecAch", "KX_SecStatus", "KX_SecNotes", "KX_SecScores", "KX_SecSaves", "KX_SecMods", "KX_SecNews",
            "KX_SecCaptures", "KX_SecSimilar", "KX_DetCapturesList"
        };

        private readonly HashSet<FrameworkElement> detFadeHooked = new HashSet<FrameworkElement>();
        // (0.99.65) only a part's first coming in, a while after the page opened: what's there as it opens comes in with the page's
        // own fade (fading each of those on top of it, and again each time one was shown again, made the page flicker in)
        private readonly HashSet<FrameworkElement> detFadeSeen = new HashSet<FrameworkElement>();
        private DateTime detFadeOpenedAt = DateTime.MinValue;

        /// <summary>From DetailsOpened, once the page is laid out: every fading part watched (new ones only).</summary>
        private void HookDetFades(FrameworkElement details)
        {
            if (details == null) return;
            detFadeHooked.RemoveWhere(x => PresentationSource.FromVisual(x) == null);   // (a page Playnite made again: the old parts go)
            foreach (string n in DetFadeNames)
            {
                var fe = UiTools.FindByName(details, n);
                if (fe == null) continue;
                if (fe.IsVisible) detFadeSeen.Add(fe);   // (already there: nothing to fade)
                if (detFadeHooked.Contains(fe)) continue;
                detFadeHooked.Add(fe);
                fe.IsVisibleChanged += (s, e) =>
                {
                    try { if ((bool)e.NewValue) DetFadeIn(s as FrameworkElement); } catch (Exception) { }
                };
            }
        }

        private void DetFadeIn(FrameworkElement fe)
        {
            if (fe == null || !detailsOpen || settings.ReduceMotion || freeMove || IsMasterMoving) return;   // (not while you edit the page)
            if (detBlankEls.ContainsKey(fe)) return;   // (an empty blank: nothing to see)
            if (!detFadeSeen.Add(fe)) return;          // (once a visit: shown again after a move or a refresh, it's just there)
            if ((DateTime.UtcNow - detFadeOpenedAt).TotalMilliseconds < 600) return;   // (the page is still fading in: it comes with it)
            if (IsAncestorVisibleLate(fe)) return;
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            double to = fe.Opacity;                     // what it would be (its own, a Free move opacity)
            if (to < 0.05) return;
            var a = new DoubleAnimation(0, to, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop        // (after: its own value again, so nothing is held)
            };
            fe.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // ---------------- (0.99.69) the entrance, started once the page is ready ----------------
        // The page's first frame takes Windows 200 ms or more to draw (its pictures, shadows and text, at 4K). The entrance used
        // to start the moment the page opened, so that first heavy frame ate the start of every fade and rise: the parts
        // seemed to jump in, whatever the animation was. Now the page waits, unseen, until it's drawn and filled in (two
        // quick frames in a row after the fill, at most 0.65 s), and only then does the entrance play, every frame of it.
        private int detEntranceGen;
        private EventHandler detEntranceTick;
        private int detFilledGen = -1;

        private void DetEntranceStart(FrameworkElement details)
        {
            if (details == null) return;
            int gen = ++detEntranceGen;
            DetEntranceUnhook();
            details.BeginAnimation(UIElement.OpacityProperty, null);
            details.Opacity = 0.003;   // (unseen, but drawn: the heavy first frame happens now, not during the entrance)
            bool off = settings.ReduceMotion || DetAnimStyle == 0;   // (0.99.70: Settings › Game page › Animation › Off: straight away)
            DetEntranceHold(details, off);
            if (off) { details.ClearValue(UIElement.OpacityProperty); return; }
            DetEntranceWatch(details);
            var opened = DateTime.UtcNow;
            DateTime last = DateTime.MinValue;
            int quick = 0;
            detEntranceTick = (s, e) =>
            {
                if (gen != detEntranceGen || !detailsOpen) { DetEntranceUnhook(); return; }
                var now = DateTime.UtcNow;
                if (last != DateTime.MinValue) quick = (now - last).TotalMilliseconds < 34 ? quick + 1 : 0;
                last = now;
                bool filled = detFilledGen == detOpenGen;
                if ((filled && quick >= 2) || (now - opened).TotalMilliseconds > 650)
                {
                    DetEntranceUnhook();
                    logger.Info("Kippix: game page entrance after " + Math.Round((now - opened).TotalMilliseconds) + " ms" + (filled ? "" : " (not filled in yet: started anyway)"));
                    try { DetEntrancePlay(details); } catch (Exception) { details.ClearValue(UIElement.OpacityProperty); }
                }
            };
            CompositionTarget.Rendering += detEntranceTick;
        }

        /// <summary>A part of the page's template by name (its template first, else anywhere in the page).</summary>
        private static T DetPart<T>(Control owner, FrameworkElement details, string name) where T : class
        {
            object o = null;
            try { if (owner != null && owner.Template != null) o = owner.Template.FindName(name, owner); } catch (Exception) { }
            if (o == null && details != null) o = UiTools.FindByName(details, name);
            return o as T;
        }

        private TranslateTransform DetShift(Control owner, FrameworkElement details, string shift, string part)
        {
            var t = DetPart<TranslateTransform>(owner, details, shift);
            if (t != null) return t;
            var fe = DetPart<UIElement>(owner, details, part);
            if (fe == null) return null;
            if (fe.RenderTransform is TranslateTransform) return (TranslateTransform)fe.RenderTransform;
            var g = fe.RenderTransform as TransformGroup;
            if (g != null) foreach (var c in g.Children) if (c is TranslateTransform) return (TranslateTransform)c;
            return null;
        }

        private void DetEntranceUnhook()
        {
            if (detEntranceTick != null) { CompositionTarget.Rendering -= detEntranceTick; detEntranceTick = null; }
        }

        /// <summary>Never left unseen: 2.2 s on, anything of the entrance still at nothing is shown.</summary>
        private void DetEntranceWatch(FrameworkElement details)
        {
            int gen = detEntranceGen;
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(2200) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (gen != detEntranceGen || !detailsOpen) return;
                try
                {
                    if (details.Opacity < 0.9) { details.BeginAnimation(UIElement.OpacityProperty, null); details.ClearValue(UIElement.OpacityProperty); }
                    var owner = FindTemplated(details, "LogoShift");
                    bool stuck = false;
                    foreach (string n in new[] { "DetLogo", "InfoColumn", "CoverColumn", "PART_ScrollHtmlDescription" })
                    {
                        var fe = DetPart<UIElement>(owner, details, n);
                        if (fe != null && fe.Opacity < 0.9) stuck = true;
                    }
                    if (stuck) DetEntranceHold(details, true);   // (everything where it ends: shown, in place, no zoom or blur)
                }
                catch (Exception) { }
            };
            t.Start();
        }

        // ---------------- (0.99.68) the entrance, smooth ----------------
        // The logo, the info and the game's art fade and rise as the page opens. Their shadows and rounded masks were drawn
        // again on every frame of that (at 4K, many times over), so it stuttered. For the entrance each is drawn once into a
        // picture at the screen's real sharpness (BitmapCache) and that picture is what fades and rises; after, they're live again.
        private int detCacheGen;

        private void DetEntranceCache(FrameworkElement details)
        {
            if (details == null || settings.ReduceMotion) return;
            int gen = ++detCacheGen;
            double scale = 1;
            try
            {
                var win = Application.Current.MainWindow;
                var src = PresentationSource.FromVisual(win);
                double dpi = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
                double vb = DesignWidth > 0 && win.ActualWidth > 0 ? win.ActualWidth / DesignWidth : 1;
                scale = Math.Max(1, Math.Min(4, dpi * vb));
            }
            catch (Exception) { }
            var els = new List<UIElement>();
            foreach (string n in new[] { "DetLogo", "InfoColumn", "CoverColumn" })
            {
                var fe = UiTools.FindByName(details, n);
                if (fe == null) continue;
                fe.CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = scale, SnapsToDevicePixels = false };
                els.Add(fe);
            }
            if (els.Count == 0) return;
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(1200) };   // (the entrance is over by 1.05 s)
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (gen != detCacheGen) return;
                foreach (var el in els) { try { el.ClearValue(UIElement.CacheModeProperty); } catch (Exception) { } }
            };
            t.Start();
        }

        // ---------------- (0.99.66) the cover on the right ----------------
        // Playnite puts the game's cover into the page a moment after it opens (and for that moment the last game's cover was
        // still there), so it popped in, or flashed from the old one to the new one, inside the column's fade. Now the cover's
        // box waits, unseen, until the new cover is in (or a quarter of a second at most: the same game again, nothing changes),
        // then fades in.
        private Image detCoverImg;
        private FrameworkElement detCoverBox;
        private bool detCoverWaiting;
        private int detCoverGen;

        /// <summary>From DetailsOpened, before the page is drawn.</summary>
        private void DetCoverHold(FrameworkElement details)
        {
            if (details == null) return;
            if (detCoverImg == null || PresentationSource.FromVisual(detCoverImg) == null)
            {
                detCoverImg = UiTools.FindByName(details, "PART_ImageCover") as Image;
                detCoverBox = UiTools.FindByName(details, "CoverClip");
                if (detCoverImg != null)
                {
                    var img = detCoverImg;
                    var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
                    if (dpd != null) dpd.AddValueChanged(img, (s, e) =>
                    {
                        if (!detCoverWaiting || !ReferenceEquals(img, detCoverImg)) return;
                        int g0 = detCoverGen;
                        ui.BeginInvoke(new Action(() => { if (g0 == detCoverGen) DetCoverShow(); }), DispatcherPriority.Loaded);   // (once it's laid out: drawn whole)
                    });
                }
            }
            var box = detCoverBox ?? detCoverImg;
            if (box == null) return;
            int gen = ++detCoverGen;
            if (settings.ReduceMotion) { detCoverWaiting = false; box.BeginAnimation(UIElement.OpacityProperty, null); return; }
            detCoverWaiting = true;
            box.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0, new Duration(TimeSpan.Zero)));   // (held unseen)
            var t = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(260) };
            t.Tick += (s, e) => { t.Stop(); if (gen == detCoverGen) DetCoverShow(); };
            t.Start();
        }

        private void DetCoverShow() { DetCoverShow(false); }

        private void DetCoverShow(bool force)
        {
            if (!detCoverWaiting) return;
            // (a cover still coming in from disk: once it's in)
            var bs = detCoverImg != null ? detCoverImg.Source as System.Windows.Media.Imaging.BitmapSource : null;
            if (!force && bs != null && !bs.IsFrozen && bs.IsDownloading)
            {
                int g0 = detCoverGen;
                EventHandler done = null;
                done = (s, e) => { bs.DownloadCompleted -= done; if (g0 == detCoverGen) DetCoverShow(true); };
                bs.DownloadCompleted += done;
                var t = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(1200) };   // (never held longer)
                t.Tick += (s, e) => { t.Stop(); if (g0 == detCoverGen) DetCoverShow(true); };
                t.Start();
                return;
            }
            detCoverWaiting = false;
            var box = detCoverBox ?? detCoverImg;
            if (box == null) return;
            box.BeginAnimation(UIElement.OpacityProperty, null);
            double to = box.Opacity;
            var a = new DoubleAnimation(0, to, new Duration(TimeSpan.FromMilliseconds(360)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            box.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>The page closed: nothing left held.</summary>
        private void DetCoverRelease()
        {
            detCoverGen++;
            detCoverWaiting = false;
            var box = detCoverBox ?? detCoverImg;
            if (box != null) box.BeginAnimation(UIElement.OpacityProperty, null);
        }

        /// <summary>A part inside another one that's fading in (the stats line in the play info): only the outer one fades.</summary>
        private bool IsAncestorVisibleLate(FrameworkElement fe)
        {
            DependencyObject p = System.Windows.Media.VisualTreeHelper.GetParent(fe);
            while (p != null)
            {
                var pf = p as FrameworkElement;
                if (pf != null && detFadeHooked.Contains(pf) && pf.HasAnimatedProperties) return true;
                if (pf != null && pf.Name == "PART_ElemGameDetails") break;
                p = System.Windows.Media.VisualTreeHelper.GetParent(p);
            }
            return false;
        }
    }
}

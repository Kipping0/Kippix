using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KippixHelper
{
    /// <summary>
    /// Home settings › Background › MOTION › Depth (Look and feel until r14): the home background (KX_BgLayers in Main.xaml: the art, your picture, the live background) sits
    /// a little "behind" the screen. It's zoomed to 1.04 so its edges never show, drifts very slowly by itself, and shifts a few
    /// pixels the other way when you move (D-pad, stick or arrow keys), then eases back. Strength Off / Low / Medium / High.
    /// Cheap: one TransformGroup on one element, animated by WPF (30 fps). Off with Reduce motion and while a game runs.
    /// </summary>
    public partial class ThemeApi
    {
        public ICommand CycleDepthCommand { get; private set; }

        private const double DepthZoom = 1.04;
        private static readonly double[] DepthKickPx = { 0, 6, 11, 16 };    // how far a move pushes it (1080p pixels; the zoom leaves ~21 px spare each way)
        private static readonly double[] DepthDriftPx = { 0, 2, 3.5, 5 };   // the slow drift on its own

        private FrameworkElement depthTarget;
        private TranslateTransform depthKick, depthDrift;
        private bool depthOn;
        private int depthDriftFor = -1;   // the strength the drift was started for (not restarted, so it doesn't jump)

        /// <summary>The Depth row shows unless Reduce motion is on (then the background stays still anyway).</summary>
        public bool DepthShown { get { return !settings.ReduceMotion; } }

        public string DepthText
        {
            get
            {
                switch (settings.DepthMode)
                {
                    case 1: return Loc("LOC_KX_Low", "Low");
                    case 2: return Loc("LOC_KX_Medium", "Medium");
                    case 3: return Loc("LOC_KX_High", "High");
                }
                return Loc("LOC_KX_Off", "Off");
            }
        }

        private void DepthChanged()
        {
            SaveSettings();
            PlaySound("move");
            Notify("DepthText");
            Notify("LookSummary");
            ApplyDepth();
        }

        /// <summary>Puts the transform on the background layer (or takes it off), and starts / stops the slow drift.</summary>
        private void ApplyDepth()
        {
            if (!IsFullscreen) return;
            try { ApplyHomeBgOffset(); } catch (Exception) { }   // (r15: Move the picture sits on KX_BgShift, around these layers: the two add up)
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var target = win != null ? UiTools.FindByName(win, "KX_BgLayers") : null;
            bool on = target != null && settings.DepthMode > 0 && !settings.ReduceMotion && !gameRunningNow && !batterySaving;   // (r12 G: off while saving battery / safe mode, the setting kept)
            if (!on)
            {
                if (depthOn && depthTarget != null)
                {
                    StopDepthAnimations();
                    depthTarget.RenderTransform = Transform.Identity;
                }
                depthOn = false;
                depthDriftFor = -1;
                return;
            }
            if (!ReferenceEquals(target, depthTarget) || !depthOn || depthKick == null)
            {
                StopDepthAnimations();
                depthTarget = target;
                depthKick = new TranslateTransform();
                depthDrift = new TranslateTransform();
                var g = new TransformGroup();
                g.Children.Add(new ScaleTransform(DepthZoom, DepthZoom));
                g.Children.Add(depthDrift);
                g.Children.Add(depthKick);
                target.RenderTransformOrigin = new Point(0.5, 0.5);
                target.RenderTransform = g;
                depthOn = true;
                depthDriftFor = -1;
            }
            if (depthDriftFor == settings.DepthMode) return;
            depthDriftFor = settings.DepthMode;
            // the drift: two slow sines that never quite line up (X every 11 s, Y every 15 s)
            double a = DepthDriftPx[Math.Max(0, Math.Min(3, settings.DepthMode))];
            var dx = new DoubleAnimation(-a, a, TimeSpan.FromSeconds(11)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            var dy = new DoubleAnimation(-a * 0.6, a * 0.6, TimeSpan.FromSeconds(15)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(dx, 30);
            Timeline.SetDesiredFrameRate(dy, 30);
            depthDrift.BeginAnimation(TranslateTransform.XProperty, dx);
            depthDrift.BeginAnimation(TranslateTransform.YProperty, dy);
        }

        private void StopDepthAnimations()
        {
            try
            {
                if (depthDrift != null) { depthDrift.BeginAnimation(TranslateTransform.XProperty, null); depthDrift.BeginAnimation(TranslateTransform.YProperty, null); }
                if (depthKick != null) { depthKick.BeginAnimation(TranslateTransform.XProperty, null); depthKick.BeginAnimation(TranslateTransform.YProperty, null); }
            }
            catch (Exception) { }
        }

        /// <summary>A move on the home screen: the background slides a little the other way, then settles back.</summary>
        private void DepthNudge(Key key)
        {
            if (!depthOn || depthKick == null || openPanel != null || detailsOpen || gameRunningNow || settings.ReduceMotion) return;
            double s = DepthKickPx[Math.Max(0, Math.Min(3, settings.DepthMode))];
            double x = 0, y = 0;
            switch (key)
            {
                case Key.Left: x = s; break;
                case Key.Right: x = -s; break;
                case Key.Up: y = s * 0.7; break;
                case Key.Down: y = -s * 0.7; break;
                default: return;
            }
            if (x != 0) depthKick.BeginAnimation(TranslateTransform.XProperty, KickTo(x), HandoffBehavior.SnapshotAndReplace);
            if (y != 0) depthKick.BeginAnimation(TranslateTransform.YProperty, KickTo(y), HandoffBehavior.SnapshotAndReplace);
        }

        private static DoubleAnimationUsingKeyFrames KickTo(double v)
        {
            // from wherever it is now: out quickly, then home slowly
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            Timeline.SetDesiredFrameRate(a, 60);
            return a;
        }
    }
}

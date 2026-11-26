using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.1.0, issues #79 and #80) No desktop between Kippix and the game. Playnite holds the launch screen in front only until
    /// the game's program has started, then minimizes; with an emulator (its own window first, then the game) or a launcher
    /// (Steam's "Starting game" box) the desktop, the taskbar and those windows showed before the game was up. Now a cover
    /// over the whole screen (above the taskbar, never taking the keyboard or controller from the game) holds the launch
    /// screen exactly as it was (a picture of it) until the game's own window fills the screen, then fades. A game that stays
    /// in a window: when its window has been in front for 10 seconds. At the latest after Settings › Game page › Splash screen ›
    /// Wait for the game (20 seconds to start with). Cancelled from Playnite's launch screen: straight away.
    /// And coming back: when a game closes and Playnite was minimized, the page you started it from covers the desktop until
    /// Playnite is back in front.
    /// </summary>
    public partial class ThemeApi
    {
        private Window launchCoverWin;
        private DispatcherTimer launchCoverTimer;
        private DateTime launchCoverSince, launchCoverFgSince;
        private IntPtr launchCoverFg;
        private bool launchCoverReturn, launchCoverLeaving;
        private DateTime launchCoverFullSince = DateTime.MinValue;
        private BitmapSource launchReturnShot;

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMon, ref LcMonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out LcRect rect);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int LcGetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int LcSetWindowLong(IntPtr hWnd, int index, int value);

        [StructLayout(LayoutKind.Sequential)] private struct LcRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LcMonitorInfo { public int Size; public LcRect Monitor, Work; public uint Flags; }

        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private int LaunchCoverWaitSeconds { get { int s = settings.LaunchCoverWait; return s == 10 || s == 30 || s == 60 ? s : 20; } }

        // ---------------- starting ----------------

        /// <summary>A game is starting (from Kippix's GameStarting): a picture of the page you're on, for coming back.</summary>
        private void LaunchCoverStarting()
        {
            launchReturnShot = null;
            if (!settings.LaunchCover || !IsFullscreen) return;
            try { launchReturnShot = SnapMainWindow(); } catch (Exception) { }
            // the launch screen is drawn and its picture faded in: the cover goes up over it, looking just the same
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(800) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                try
                {
                    if (!settings.LaunchCover || gameRunningNow && launchCoverWin != null) return;
                    var w = Application.Current != null ? Application.Current.MainWindow : null;
                    if (w == null || w.WindowState == WindowState.Minimized || !w.IsVisible) return;
                    var status = UiTools.FindByName(w, "PART_ElemGameStatus") as FrameworkElement;
                    if (status == null || !status.IsVisible) return;   // (cancelled already, or the game's up)
                    ShowLaunchCover(SnapMainWindow(), false);
                }
                catch (Exception ex) { logger.Warn("Kippix: launch cover: " + ex.Message); }
            };
            t.Start();
        }

        /// <summary>A game closed (from GameRunningChanged): if Playnite was minimized, the page you left covers the desktop until it's back.</summary>
        private void LaunchCoverStopped()
        {
            try
            {
                if (launchCoverWin != null && !launchCoverReturn) HideLaunchCover("the game closed");
                if (!settings.LaunchCover || !IsFullscreen || launchReturnShot == null) return;
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                if (w == null || (w.WindowState != WindowState.Minimized && w.IsVisible && w.IsActive)) return;
                ShowLaunchCover(launchReturnShot, true);
            }
            catch (Exception ex) { logger.Warn("Kippix: return cover: " + ex.Message); }
        }

        // ---------------- the cover ----------------

        private void ShowLaunchCover(BitmapSource shot, bool coming)
        {
            var main = Application.Current != null ? Application.Current.MainWindow : null;
            if (main == null) return;
            IntPtr mh = new WindowInteropHelper(main).Handle;
            var mi = new LcMonitorInfo { Size = Marshal.SizeOf(typeof(LcMonitorInfo)) };
            if (mh == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(mh, 2), ref mi)) return;
            CloseLaunchCoverNow();

            var win = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Black,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false, Cursor = Cursors.None,
                Left = -32000, Top = -32000, Width = 10, Height = 10, Title = "Kippix"
            };
            if (shot != null) win.Content = new Image { Source = shot, Stretch = Stretch.Fill };
            win.SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(win).Handle;
                LcSetWindowLong(h, GWL_EXSTYLE, LcGetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                SetWindowPos(h, HWND_TOPMOST, mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, mi.Monitor.Bottom - mi.Monitor.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            };
            launchCoverWin = win;
            launchCoverReturn = coming;
            launchCoverLeaving = false;
            launchCoverSince = DateTime.Now;
            launchCoverFg = IntPtr.Zero;
            launchCoverFullSince = DateTime.MinValue;
            win.Show();
            if (launchCoverTimer == null)
            {
                launchCoverTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(200) };
                launchCoverTimer.Tick += (s, e) => LaunchCoverTick();
            }
            launchCoverTimer.Start();
            logger.Info("Kippix: " + (coming ? "coming back from a game: the page covers the desktop until Playnite's back" : "launch cover up: the launch screen stays until the game fills the screen"));
        }

        private void LaunchCoverTick()
        {
            var win = launchCoverWin;
            if (win == null) { launchCoverTimer.Stop(); return; }
            if (launchCoverLeaving) return;
            try
            {
                IntPtr ch = new WindowInteropHelper(win).Handle;
                if (ch != IntPtr.Zero) SetWindowPos(ch, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);   // (over the taskbar, whatever comes up)
                double secs = (DateTime.Now - launchCoverSince).TotalSeconds;
                var main = Application.Current != null ? Application.Current.MainWindow : null;
                IntPtr mh = main != null ? new WindowInteropHelper(main).Handle : IntPtr.Zero;

                if (launchCoverReturn)
                {
                    // back: Playnite in front again (and a moment to draw)
                    bool back = main != null && main.IsVisible && main.WindowState != WindowState.Minimized && GetForegroundWindow() == mh;
                    if (back && secs > 0.35) { HideLaunchCover("Playnite's back"); return; }
                    if (secs > 6) HideLaunchCover("6 seconds");
                    return;
                }

                if (secs > LaunchCoverWaitSeconds) { HideLaunchCover(LaunchCoverWaitSeconds + " seconds"); return; }
                // cancelled from the launch screen (nothing running, the screen gone, Playnite still there)
                if (!gameRunningNow && secs > 1 && main != null && main.WindowState != WindowState.Minimized)
                {
                    var status = UiTools.FindByName(main, "PART_ElemGameStatus") as FrameworkElement;
                    if (status == null || !status.IsVisible) { HideLaunchCover("the launch was cancelled"); return; }
                }

                IntPtr fg = GetForegroundWindow();
                uint pid = 0;
                if (fg == IntPtr.Zero || fg == mh || fg == ch || GetWindowThreadProcessId(fg, out pid) == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id || IsShellWindow(fg))
                {
                    launchCoverFg = IntPtr.Zero;
                    launchCoverFullSince = DateTime.MinValue;
                    return;
                }
                if (fg != launchCoverFg) { launchCoverFg = fg; launchCoverFgSince = DateTime.Now; launchCoverFullSince = DateTime.MinValue; }

                // the game's window fills the screen: a moment for its first frames, then away
                if (FillsMonitor(fg, mh))
                {
                    if (launchCoverFullSince == DateTime.MinValue) launchCoverFullSince = DateTime.Now;
                    if ((DateTime.Now - launchCoverFullSince).TotalMilliseconds >= 400) HideLaunchCover("the game fills the screen");
                    return;
                }
                launchCoverFullSince = DateTime.MinValue;
                // a game in a window: in front (and big) for 10 seconds
                if (gameRunningNow && (DateTime.Now - launchCoverFgSince).TotalSeconds >= 10 && CoversHalf(fg, mh)) HideLaunchCover("a window in front for 10 seconds");
            }
            catch (Exception ex) { logger.Warn("Kippix: launch cover: " + ex.Message); HideLaunchCover("a problem"); }
        }

        private void HideLaunchCover(string why)
        {
            var win = launchCoverWin;
            if (win == null || launchCoverLeaving) return;
            launchCoverLeaving = true;
            logger.Info("Kippix: launch cover away (" + why + ")");
            var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(250)));
            fade.Completed += (s, e) => { if (ReferenceEquals(launchCoverWin, win)) CloseLaunchCoverNow(); else try { win.Close(); } catch (Exception) { } };
            win.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void CloseLaunchCoverNow()
        {
            var win = launchCoverWin;
            launchCoverWin = null;
            launchCoverLeaving = false;
            if (launchCoverTimer != null) launchCoverTimer.Stop();
            if (win != null) try { win.Close(); } catch (Exception) { }
        }

        // ---------------- helpers ----------------

        /// <summary>A picture of Playnite's window as it is now, at the screen's own resolution.</summary>
        private BitmapSource SnapMainWindow()
        {
            var w = Application.Current != null ? Application.Current.MainWindow : null;
            if (w == null || !w.IsVisible || w.WindowState == WindowState.Minimized || w.ActualWidth <= 0) return null;
            var root = w.Content as FrameworkElement ?? (FrameworkElement)w;
            var src = PresentationSource.FromVisual(w);
            double sx = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            double sy = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M22 : 1;
            int pw = (int)Math.Round(root.ActualWidth * sx), ph = (int)Math.Round(root.ActualHeight * sy);
            if (pw <= 0 || ph <= 0 || pw > 8000 || ph > 8000) return null;
            var bmp = new RenderTargetBitmap(pw, ph, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            bmp.Render(root);
            bmp.Freeze();
            return bmp;
        }

        private static bool IsShellWindow(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            string c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "Progman" || c == "WorkerW" || c == "Windows.UI.Core.CoreWindow" || c == "NotifyIconOverflowWindow";
        }

        private static bool FillsMonitor(IntPtr h, IntPtr onMonitorOf)
        {
            LcRect r;
            var mi = new LcMonitorInfo { Size = Marshal.SizeOf(typeof(LcMonitorInfo)) };
            if (!IsWindowVisible(h) || IsIconic(h) || !GetWindowRect(h, out r)) return false;
            if (!GetMonitorInfo(MonitorFromWindow(onMonitorOf != IntPtr.Zero ? onMonitorOf : h, 2), ref mi)) return false;
            return r.Left <= mi.Monitor.Left + 2 && r.Top <= mi.Monitor.Top + 2 && r.Right >= mi.Monitor.Right - 2 && r.Bottom >= mi.Monitor.Bottom - 2;
        }

        private static bool CoversHalf(IntPtr h, IntPtr onMonitorOf)
        {
            LcRect r;
            var mi = new LcMonitorInfo { Size = Marshal.SizeOf(typeof(LcMonitorInfo)) };
            if (!IsWindowVisible(h) || IsIconic(h) || !GetWindowRect(h, out r)) return false;
            if (!GetMonitorInfo(MonitorFromWindow(onMonitorOf != IntPtr.Zero ? onMonitorOf : h, 2), ref mi)) return false;
            double a = Math.Max(0, r.Right - r.Left) * (double)Math.Max(0, r.Bottom - r.Top);
            double m = (mi.Monitor.Right - mi.Monitor.Left) * (double)(mi.Monitor.Bottom - mi.Monitor.Top);
            return m > 0 && a >= m * 0.5;
        }
    }
}

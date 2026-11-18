using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Playnite.SDK;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.4, issue: Bencey17) Netflix, YouTube, Prime Video and Crunchyroll can open full screen in your own browser, like a TV
    /// app, instead of in a browser window. Profile menu › Media › Opens in:
    ///  0 Automatic (the default): the service's Windows app when it's installed, otherwise full screen in your browser (YouTube too).
    ///    (Before 1.0.4 that was a normal browser window, and YouTube the built-in browser.)
    ///  1 Full screen in your browser, always.
    ///  2 Your browser, in a window (as it was before 1.0.4).
    /// Full screen: your default browser (Edge, Chrome, Brave, Vivaldi, Opera, Firefox and its kin; Edge when the default
    /// isn't one of those) opens the site in a window of its own (Chromium's --app: no tabs or address bar), with your
    /// usual profile so you stay signed in, and F11 makes it fill the screen. The Guide button opens the overlay over it
    /// (Close game closes just that window), and when the window goes Playnite comes back to the front.
    /// </summary>
    public partial class ThemeApi
    {
        private string StreamOpenText(int m)
        {
            switch (m)
            {
                case 1: return Loc("LOC_KX_StreamOpenFull", "Full screen in your browser");
                case 2: return Loc("LOC_KX_StreamOpenWindow", "Your browser, in a window");
            }
            return Loc("LOC_KX_StreamOpenAuto", "Automatic");
        }

        /// <summary>The note under the Media list: how the services open with the setting as it is.</summary>
        public string MediaHelpText
        {
            get
            {
                switch (settings.StreamOpenIn)
                {
                    case 1: return Loc("LOC_KX_MediaHelpFull", "Every service opens full screen in your browser, like a TV app. The controller drives the browser; Guide brings up the overlay to close it.");
                    case 2: return Loc("LOC_KX_MediaHelpWindow", "Every service opens its website in your browser, in a normal window.");
                }
                return Loc("LOC_KX_MediaHelpAuto", "Netflix, Prime Video and Crunchyroll open their Windows apps when they're installed, otherwise full screen in your browser, as YouTube does. The controller drives the browser; Guide brings up the overlay to close it.");
            }
        }

        /// <summary>The last row on Media: Opens in (A steps through them).</summary>
        private void AddStreamOpenRow()
        {
            ProfileRows.Add(new PickRow
            {
                Key = "streamopen", Icon = "tv",
                Text = string.Format(Loc("LOC_KX_StreamOpenRow", "Opens in: {0}"), StreamOpenText(settings.StreamOpenIn)),
                Sub = Loc("LOC_KX_StreamOpenSub", "Automatic, full screen in your browser, or a browser window"),
                Command = new RelayCommand(_ =>
                {
                    settings.StreamOpenIn = (Math.Max(0, Math.Min(2, settings.StreamOpenIn)) + 1) % 3;
                    SaveSettings();
                    PlaySound("move");
                    Notify("MediaHelpText");
                    var row = ProfileRows.FirstOrDefault(r => r.Key == "streamopen");
                    if (row != null)
                    {
                        int i = ProfileRows.IndexOf(row);
                        ProfileRows.RemoveAt(i);
                        AddStreamOpenRow();
                        var fresh = ProfileRows[ProfileRows.Count - 1];
                        ProfileRows.RemoveAt(ProfileRows.Count - 1);
                        ProfileRows.Insert(i, fresh);
                        FocusListItemSure("KX_ProfileList", fresh);
                    }
                })
            });
        }

        /// <summary>How a service opens with the setting: true when it was opened here (full screen or the built-in browser).</summary>
        private bool OpenMediaAsSet(MediaService svc, string app)
        {
            int mode = Math.Max(0, Math.Min(2, settings.StreamOpenIn));
            if (mode == 0)
            {
                // (YouTube too: full screen in your browser, not the built-in one)
                if (app != null) return false;   // its app (the caller opens it)
                ClosePanels();
                OpenFullScreenBrowser(svc);
                return true;
            }
            ClosePanels();
            if (mode == 1) { OpenFullScreenBrowser(svc); return true; }
            try { Process.Start(new ProcessStartInfo(svc.Web) { UseShellExecute = true }); } catch (Exception ex) { logger.Warn("Kippix: media: " + ex.Message); }
            return true;
        }

        // ---------------- the browser, full screen ----------------

        private IntPtr streamWindow = IntPtr.Zero;
        private DispatcherTimer streamWatch;

        /// <summary>The default browser's program, and whether it's Firefox-like (else Chromium: Edge, Chrome, Brave…). Edge when unsure.</summary>
        private static string StreamBrowser(out bool firefox)
        {
            firefox = false;
            string exe = null;
            try
            {
                string prog = null;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                    if (k != null) prog = k.GetValue("ProgId") as string;
                if (!string.IsNullOrEmpty(prog))
                {
                    using (var k = Registry.ClassesRoot.OpenSubKey(prog + @"\shell\open\command"))
                    {
                        string cmd = k != null ? k.GetValue(null) as string : null;
                        if (!string.IsNullOrEmpty(cmd))
                        {
                            cmd = cmd.Trim();
                            exe = cmd.StartsWith("\"") ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1)) : cmd.Split(' ')[0];
                        }
                    }
                }
            }
            catch (Exception) { }
            if (exe != null && File.Exists(exe))
            {
                string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (n == "firefox" || n == "librewolf" || n == "waterfox" || n == "floorp" || n == "zen") { firefox = true; return exe; }
                if (n == "msedge" || n == "chrome" || n == "brave" || n == "vivaldi" || n == "opera" || n == "chromium" || n == "thorium") return exe;
            }
            foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                string edge = Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(edge)) return edge;
            }
            return null;
        }

        private static List<IntPtr> TopWindowsOf(string processName)
        {
            var pids = new HashSet<uint>();
            try { foreach (var p in Process.GetProcessesByName(processName)) { try { pids.Add((uint)p.Id); } catch (Exception) { } finally { p.Dispose(); } } } catch (Exception) { }
            var list = new List<IntPtr>();
            StreamNative.EnumWindows((h, l) =>
            {
                uint pid;
                if (StreamNative.IsWindowVisible(h) && StreamNative.GetWindowThreadProcessId(h, out pid) != 0 && pids.Contains(pid)) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static bool FillsItsScreen(IntPtr h)
        {
            StreamNative.SRect r;
            if (!StreamNative.GetWindowRect(h, out r)) return false;
            var mi = new StreamNative.SMonInfo { Size = Marshal.SizeOf(typeof(StreamNative.SMonInfo)) };
            if (!StreamNative.GetMonitorInfo(StreamNative.MonitorFromWindow(h, 2), ref mi)) return false;
            return r.Left <= mi.Monitor.Left && r.Top <= mi.Monitor.Top && r.Right >= mi.Monitor.Right && r.Bottom >= mi.Monitor.Bottom;
        }

        private void OpenFullScreenBrowser(MediaService svc)
        {
            bool firefox;
            string exe = StreamBrowser(out firefox);
            if (exe == null)
            {
                try { Process.Start(new ProcessStartInfo(svc.Web) { UseShellExecute = true }); } catch (Exception ex) { logger.Warn("Kippix: media: " + ex.Message); }
                return;
            }
            string name = Path.GetFileNameWithoutExtension(exe);
            var before = new HashSet<IntPtr>(TopWindowsOf(name));
            string args = firefox ? "--new-window \"" + svc.Web + "\"" : "--app=\"" + svc.Web + "\" --start-fullscreen";
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
            catch (Exception ex)
            {
                logger.Warn("Kippix: media: " + name + ": " + ex.Message);
                try { Process.Start(new ProcessStartInfo(svc.Web) { UseShellExecute = true }); } catch (Exception) { }
                return;
            }
            logger.Info("Kippix: media: " + svc.Name + " opening full screen in " + name);
            // its new window: in front, then full screen (F11) if the browser didn't make it so itself
            KippixShared.SafeWork.Queue(_ =>
            {
                IntPtr found = IntPtr.Zero;
                for (int i = 0; i < 60 && found == IntPtr.Zero; i++)   // (up to 15 s: a browser starting from cold)
                {
                    Thread.Sleep(250);
                    foreach (var h in TopWindowsOf(name))
                    {
                        if (before.Contains(h)) continue;
                        var sb = new StringBuilder(256);
                        StreamNative.GetWindowText(h, sb, 256);
                        if (sb.Length == 0) continue;
                        found = h;
                        break;
                    }
                }
                if (found == IntPtr.Zero) { logger.Info("Kippix: media: " + name + "'s new window wasn't found (left as the browser opened it)"); return; }
                Thread.Sleep(600);   // (the page under way: F11 too early can be taken by the window before it's ready)
                try { StreamNative.SetForegroundWindow(found); } catch (Exception) { }
                Thread.Sleep(200);
                if (!FillsItsScreen(found))
                {
                    StreamNative.keybd_event(0x7A, 0, 0, UIntPtr.Zero);   // F11
                    StreamNative.keybd_event(0x7A, 0, 2, UIntPtr.Zero);
                }
                IntPtr win = found;
                OnUi(() => WatchStreamWindow(win));
            });
        }

        /// <summary>While the full-screen window is open: the overlay's Close game closes just it; when it's gone, Playnite comes back.</summary>
        private void WatchStreamWindow(IntPtr h)
        {
            streamWindow = h;
            KippixOverlay.OverlayPlugin.MediaWindow = h;
            // the controller drives the browser (the in-game overlay's controller mode: Overlay.BrowserMode.cs)
            try { if (plugin.OverlayRunning) plugin.Overlay.BeginBrowserMode(h); else logger.Info("Kippix: media: the in-game overlay is off, so the controller can't drive the browser"); }
            catch (Exception ex) { logger.Warn("Kippix: media: controller mode: " + ex.Message); }
            if (streamWatch == null)
            {
                streamWatch = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
                streamWatch.Tick += (s, e) =>
                {
                    if (streamWindow != IntPtr.Zero && StreamNative.IsWindow(streamWindow) && StreamNative.IsWindowVisible(streamWindow)) return;
                    streamWatch.Stop();
                    if (KippixOverlay.OverlayPlugin.MediaWindow == streamWindow) KippixOverlay.OverlayPlugin.MediaWindow = IntPtr.Zero;
                    streamWindow = IntPtr.Zero;
                    try
                    {
                        var w = Application.Current != null ? Application.Current.MainWindow : null;
                        if (w != null)
                        {
                            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                            w.Activate();
                            StreamNative.SetForegroundWindow(new WindowInteropHelper(w).Handle);
                        }
                    }
                    catch (Exception) { }
                    logger.Info("Kippix: media: the full-screen window closed, back to Playnite");
                };
            }
            streamWatch.Stop();
            streamWatch.Start();
        }
    }

    /// <summary>(1.0.4) Windows calls for ThemeApi.StreamOpen.cs (kept apart: the ThemeApi parts declare some of these already).</summary>
    internal static class StreamNative
    {
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out SRect r);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref SMonInfo mi);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] public struct SRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct SMonInfo { public int Size; public SRect Monitor, Work; public uint Flags; }

        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    }
}

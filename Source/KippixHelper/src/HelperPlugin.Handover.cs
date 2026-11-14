using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Playnite.SDK.Events;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.54) A game whose launcher hands over to the game (The Witcher 3's REDlauncher, and others like it): Playnite watches
    /// the program it started, sees it close a couple of seconds in, and says the game has stopped while the launcher and then
    /// the game are still to come. When a game "stops" within its first minute, Kippix waits before treating it as stopped:
    /// while Steam still says the game is running (its RunningAppID), or a program from the game's folder runs, it's carried
    /// on: the overlay keeps the game (screenshots go to it, not to "Other"), no "You just played" card, and Playnite isn't left
    /// over the launcher. Once they've all closed (or nothing came within 20 seconds), it ends as usual: the play time Playnite
    /// missed is added to the game, then the session card, the save backup and the rest.
    /// </summary>
    public partial class HelperPlugin
    {
        private Game hoGame;
        private ulong hoElapsed;
        private DateTime hoStoppedAt, hoLastAlive, hoStartedAt;
        private bool hoCarried;
        private int hoFronts;
        private string hoDir, hoAppId;
        private DispatcherTimer hoTimer;

        private static readonly Guid SteamLibraryId = new Guid("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        /// <summary>True when the stop is held back (a launcher may be handing over).</summary>
        private bool HandoverStart(OnGameStoppedEventArgs a)
        {
            var g = a != null ? a.Game : null;
            if (g == null || a.ElapsedSeconds > 60) return false;
            string dir = null;
            try { if (!string.IsNullOrWhiteSpace(g.InstallDirectory) && System.IO.Directory.Exists(g.InstallDirectory)) dir = System.IO.Path.GetFullPath(g.InstallDirectory).TrimEnd('\\') + "\\"; } catch (Exception) { }
            string appId = g.PluginId == SteamLibraryId && !string.IsNullOrEmpty(g.GameId) ? g.GameId : null;
            if (dir == null && appId == null) return false;
            if (dir != null && dir.Length <= 4) dir = null;   // (a whole drive: no)
            if (hoGame != null) HandoverFinish();
            hoGame = g;
            hoElapsed = a.ElapsedSeconds;
            hoStoppedAt = hoLastAlive = DateTime.Now;
            hoStartedAt = DateTime.Now.AddSeconds(-(double)a.ElapsedSeconds - 5);
            hoCarried = false;
            hoFronts = 0;
            hoDir = dir;
            hoAppId = appId;
            if (hoTimer == null)
            {
                hoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                hoTimer.Tick += (s, e) => { try { HandoverTick(); } catch (Exception ex) { logger.Warn("Kippix: launcher hand-over: " + ex.Message); HandoverFinish(); } };
            }
            hoTimer.Start();
            logger.Info("Kippix: " + g.Name + " stopped after " + a.ElapsedSeconds + " s: watching whether a launcher hands over to the game (Steam app " + (appId ?? "-") + ", folder " + (dir ?? "-") + ")");
            return true;
        }

        private bool HandoverAlive()
        {
            if (hoAppId != null)
            {
                try
                {
                    object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0);
                    if (v is int && ((int)v).ToString() == hoAppId) return true;
                    object r = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\Apps\" + hoAppId, "Running", 0);
                    if (r is int && (int)r != 0) return true;
                }
                catch (Exception) { }
            }
            if (hoDir != null)
            {
                Process[] all = null;
                try
                {
                    all = Process.GetProcesses();
                    foreach (var p in all)
                    {
                        string path = null;
                        try { path = Native.GetProcessPath((uint)p.Id); } catch (Exception) { }
                        if (path != null && path.StartsWith(hoDir, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch (Exception) { }
                finally { if (all != null) foreach (var p in all) try { p.Dispose(); } catch (Exception) { } }
            }
            return false;
        }

        private void HandoverTick()
        {
            if (hoGame == null) { hoTimer.Stop(); return; }
            var now = DateTime.Now;
            bool alive = HandoverAlive();
            if (alive) hoLastAlive = now;
            if (!hoCarried)
            {
                if (alive)
                {
                    hoCarried = true;
                    logger.Info("Kippix: " + hoGame.Name + " carries on after its launcher (still running): kept as the game being played");
                }
                else if ((now - hoStoppedAt).TotalSeconds > 20) { HandoverFinish(); return; }
            }
            if (hoCarried)
            {
                // Playnite came back over the launcher when it thought the game had closed: the launcher goes back in front
                if (hoFronts < 4 && (now - hoStoppedAt).TotalSeconds < 15 && PlayniteInFront()) { hoFronts++; LauncherToFront(); }
                if (!alive && (now - hoLastAlive).TotalSeconds >= 6) HandoverFinish();
            }
        }

        /// <summary>Ends the held-back stop: the play time Playnite missed is added, then everything a stopped game does.</summary>
        private void HandoverFinish()
        {
            if (hoTimer != null) hoTimer.Stop();
            var g = hoGame;
            if (g == null) return;
            hoGame = null;
            ulong total = hoElapsed;
            if (hoCarried)
            {
                double extra = Math.Max(0, (hoLastAlive - hoStoppedAt).TotalSeconds);
                total += (ulong)extra;
                try
                {
                    var fresh = PlayniteApi.Database.Games.Get(g.Id);
                    if (fresh != null && extra >= 1)
                    {
                        fresh.Playtime += (ulong)extra;
                        fresh.LastActivity = DateTime.Now;
                        PlayniteApi.Database.Games.Update(fresh);
                        g = fresh;
                    }
                }
                catch (Exception ex) { logger.Warn("Kippix: adding the play time after the launcher: " + ex.Message); }
                logger.Info("Kippix: " + g.Name + " closed after its launcher: " + (ulong)extra + " s added to its play time (Playnite counted " + hoElapsed + " s)");
            }
            FinishStopped(g, total);
        }

        // ---------------- the launcher back in front ----------------

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);

        private static bool PlayniteInFront()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch (Exception) { return false; }
        }

        private static readonly string[] NotLaunchers = { "steam", "steamwebhelper", "explorer", "discord", "nvidia", "nvcontainer", "playnite", "textinputhost", "searchhost", "shellexperiencehost", "applicationframehost", "gamebar", "msedge", "chrome", "firefox" };

        /// <summary>The newest program's window (started since the game was) goes in front of Playnite: the launcher, or the game.</summary>
        private void LauncherToFront()
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            IntPtr best = IntPtr.Zero;
            var started = new Dictionary<uint, bool>();
            EnumWindows((h, l) =>
            {
                if (best != IntPtr.Zero) return false;
                try
                {
                    if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, 4) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;   // (visible, not owned, with a title)
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == 0 || pid == me) return true;
                    bool ok;
                    if (!started.TryGetValue(pid, out ok))
                    {
                        ok = false;
                        try
                        {
                            using (var p = Process.GetProcessById((int)pid))
                            {
                                string n = p.ProcessName.ToLowerInvariant();
                                string path = Native.GetProcessPath(pid) ?? "";
                                bool inDir = hoDir != null && path.StartsWith(hoDir, StringComparison.OrdinalIgnoreCase);
                                ok = inDir || (!NotLaunchers.Any(x => n.Contains(x)) && p.StartTime >= hoStartedAt);
                            }
                        }
                        catch (Exception) { }
                        started[pid] = ok;
                    }
                    if (ok) best = h;   // (top of the z-order first)
                }
                catch (Exception) { }
                return true;
            }, IntPtr.Zero);
            if (best == IntPtr.Zero) return;
            logger.Info("Kippix: Playnite came back over " + (hoGame != null ? hoGame.Name : "the game") + "'s launcher: the launcher put back in front");
            try { KippixOverlay.Native.ForceForeground(best); } catch (Exception) { }
        }
    }
}

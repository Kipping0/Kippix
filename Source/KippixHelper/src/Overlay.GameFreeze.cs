using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace KippixOverlay
{
    /// <summary>
    /// (1.0.4, issue: Bencey17) "Freeze the game while the overlay is open". The controller is read by the overlay and the game
    /// at the same time (Windows can't hand it to one of them), and emulators (Eden, Ryujinx, Dolphin…) keep reading it when
    /// they aren't in front, so moving around the overlay played the game too. With this on, the game's process is paused
    /// (Windows' own suspend, the same as Resource Monitor's "Suspend process") while the overlay is open, and carries on the
    /// moment it closes.
    ///  - Off / Emulators only (the default) / All games. Never a game the overlay lets keep running (Let it keep running, or
    ///    Automatic for an online game), so online games aren't frozen into a disconnect.
    ///  - Always let go: the overlay closing (however it closes), the game being closed from the overlay, the game stopping,
    ///    Playnite closing. The game's process id is written down while it's frozen, so if Playnite is ended mid-way the next
    ///    start lets it go.
    /// </summary>
    internal static class GameFreeze
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object gate = new object();
        private static uint frozenPid;
        private static string frozenName;
        private static string notePath;

        public static readonly string[] ModeNames = { "Off", "Emulators only", "All games" };

        // emulators by their program's name (the start of it: "pcsx2-qtx64-avx2", "xenia_canary"), for ones not started through
        // Playnite's own emulator settings
        private static readonly string[] EmulatorNames =
        {
            "eden", "yuzu", "suyu", "sudachi", "citron", "torzu", "ryujinx", "cemu", "dolphin", "pcsx2", "rpcs3", "duckstation",
            "ppsspp", "retroarch", "xemu", "xenia", "vita3k", "melonds", "mgba", "flycast", "redream", "shadps4", "azahar",
            "lime3ds", "citra", "mame", "snes9x", "bsnes", "mesen", "mednafen", "project64", "simple64", "rmg", "gopher64",
            "bigpemu", "ares", "punes", "nestopia", "fceux", "visualboyadvance", "desmume", "nosgba", "pcsx", "epsxe", "pcsxr",
            "demul", "nulldc", "supermodel", "model2", "m2emulator", "teknoparrot", "openbor", "dosbox", "scummvm", "stella",
            "hatari", "fs-uae", "winuae", "vice", "x64sc", "blastem", "kega", "gens", "picodrive", "kronos", "yabause", "ymir",
            "mupen64", "play", "steam-rom-manager"
        };

        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        private const uint PROCESS_SUSPEND_RESUME = 0x0800;

        public static bool IsFrozen { get { lock (gate) return frozenPid != 0; } }

        /// <summary>Where the frozen game's id is written down (the overlay's data folder). Lets go of one left from last time.</summary>
        public static void Init(string dataDir)
        {
            try
            {
                notePath = Path.Combine(dataDir, "frozen-game.txt");
                if (!File.Exists(notePath)) return;
                uint pid;
                string text = File.ReadAllText(notePath).Trim();
                File.Delete(notePath);
                if (uint.TryParse(text, out pid) && pid != 0)
                {
                    Resume(pid);
                    logger.Info("Kippix overlay: a game left frozen when Playnite closed (process " + pid + ") carries on");
                }
            }
            catch (Exception ex) { logger.Warn("Kippix overlay: frozen game note: " + ex.Message); }
        }

        /// <summary>Is the running game an emulator: Playnite starts it through an emulator, or its program is a known one.</summary>
        public static bool IsEmulator(Game game, uint pid)
        {
            try
            {
                if (game != null && game.GameActions != null && game.GameActions.Any(a => a != null && a.Type == GameActionType.Emulator)) return true;
            }
            catch (Exception) { }
            string name = ProcessName(pid);
            if (string.IsNullOrEmpty(name)) return false;
            name = name.ToLowerInvariant();
            return EmulatorNames.Any(e => name == e || name.StartsWith(e + "-") || name.StartsWith(e + "_") || name.StartsWith(e + ".") || name.StartsWith(e + " ")
                                          || (e.Length >= 5 && name.StartsWith(e)));
        }

        private static string ProcessName(uint pid)
        {
            try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName; }
            catch (Exception) { return null; }
        }

        /// <summary>Freezes the game's process (the overlay has just opened and has the focus). Nothing when the setting says not to.</summary>
        public static void Freeze(int mode, Game game, uint pid, uint ownPid, string why)
        {
            if (mode <= 0 || pid == 0 || pid == ownPid) return;
            try
            {
                string name = ProcessName(pid);
                if (string.IsNullOrEmpty(name)) return;
                string l = name.ToLowerInvariant();
                if (l == "explorer" || l == "playnite.fullscreenapp" || l == "playnite.desktopapp" || l == "discord" || l == "steam" || l == "steamwebhelper") return;
                if (mode == 1 && !IsEmulator(game, pid)) { logger.Info("Kippix overlay: not frozen (" + name + " isn't an emulator; Freeze the game: Emulators only)"); return; }
                lock (gate)
                {
                    if (frozenPid != 0) return;
                    IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                    if (h == IntPtr.Zero) { logger.Info("Kippix overlay: couldn't freeze " + name + " (it runs as administrator?)"); return; }
                    try
                    {
                        int r = NtSuspendProcess(h);
                        if (r != 0) { logger.Info("Kippix overlay: couldn't freeze " + name + " (" + r + ")"); return; }
                    }
                    finally { CloseHandle(h); }
                    frozenPid = pid;
                    frozenName = name;
                }
                try { if (notePath != null) File.WriteAllText(notePath, pid.ToString()); } catch (Exception) { }
                logger.Info("Kippix overlay: " + name + " frozen while the overlay is open (" + why + ")");
            }
            catch (Exception ex) { logger.Warn("Kippix overlay: freeze: " + ex.Message); }
        }

        /// <summary>The frozen game carries on (safe to call any time, as often as you like).</summary>
        public static void Unfreeze()
        {
            uint pid;
            string name;
            lock (gate)
            {
                pid = frozenPid;
                name = frozenName;
                frozenPid = 0;
                frozenName = null;
            }
            if (pid == 0) return;
            Resume(pid);
            try { if (notePath != null && File.Exists(notePath)) File.Delete(notePath); } catch (Exception) { }
            logger.Info("Kippix overlay: " + name + " carries on");
        }

        /// <summary>Before something is done to a game's process (closing it): let it go first if it's the frozen one.</summary>
        public static void UnfreezeIf(uint pid)
        {
            lock (gate) { if (frozenPid == 0 || frozenPid != pid) return; }
            Unfreeze();
        }

        private static void Resume(uint pid)
        {
            try
            {
                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero) return;
                try { NtResumeProcess(h); } finally { CloseHandle(h); }
            }
            catch (Exception) { }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KippixHelper
{
    internal static class Native
    {
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        /// <summary>Full exe path of a process, or null.</summary>
        public static string GetProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static void Sleep()
        {
            try { SetSuspendState(false, false, false); }
            catch (Exception) { }
        }

        public static void PowerOff()
        {
            try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception) { }
        }

        public static void Restart()
        {
            try { Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception) { }
        }

        public static void Hibernate()
        {
            try
            {
                if (SetSuspendState(true, false, false)) return;
            }
            catch (Exception) { }
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/h") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception) { }
        }

        public static void OpenUri(string uri)
        {
            try { Process.Start(uri); } catch (Exception) { }
        }
    }

    internal static class SteamPaths
    {
        public static string SteamPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
                }
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("InstallPath") as string : null;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                }
            }
            catch (Exception) { }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            return Directory.Exists(def) ? def : null;
        }
    }

    /// <summary>Discord without the overlay: is it running, start it in the background, open it.</summary>
    internal static class DiscordApp
    {
        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("Discord").Length > 0; }
            catch (Exception) { return false; }
        }

        public static void StartInBackground()
        {
            if (IsRunning()) return;
            try
            {
                string update = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Discord\Update.exe");
                if (File.Exists(update))
                {
                    Process.Start(new ProcessStartInfo(update, "--processStart Discord.exe --process-start-args \"--start-minimized\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    return;
                }
                Process.Start("discord://");
            }
            catch (Exception) { }
        }

        public static void Open()
        {
            Native.OpenUri("discord://-/channels/@me");
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        /// <summary>The foreground window is Discord's, or there's no foreground window at all (Discord just hid itself).</summary>
        public static bool DiscordHasForeground()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return true;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            try { return Process.GetProcessById((int)pid).ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        public static bool IsForeground(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
        }

        /// <summary>Brings a window back to the front, working around Windows' foreground-lock rules.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd) return;
            uint dummy;
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out dummy) : 0;
            uint cur = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(cur, fgThread, false);
            // (0.99.51) Windows lets a program take the foreground just after it "pressed a key". That was Alt held down across
            // the switch: the window losing the foreground (often a game) got Alt down and never Alt up, so it thought Alt was
            // still held and the next Enter was Alt+Enter, which switches many games to windowed (and they save it). Now a whole
            // tap of a key nothing uses first, then (only if that wasn't enough) a whole tap of Alt; never held across the switch.
            if (GetForegroundWindow() != hwnd)
            {
                keybd_event(0xE8, 0, 0, UIntPtr.Zero);          // (an unassigned key)
                keybd_event(0xE8, 0, 0x2, UIntPtr.Zero);
                SetForegroundWindow(hwnd);
            }
            if (GetForegroundWindow() != hwnd)
            {
                keybd_event(0x12, 0, 0x1, UIntPtr.Zero);
                keybd_event(0x12, 0, 0x1 | 0x2, UIntPtr.Zero);
                SetForegroundWindow(hwnd);
            }
        }

        /// <summary>Hides Discord's visible windows so it keeps running in the system tray. True if one was hidden.</summary>
        public static bool HideToTray()
        {
            var pids = new System.Collections.Generic.HashSet<uint>();
            try { foreach (Process p in Process.GetProcessesByName("Discord")) pids.Add((uint)p.Id); } catch (Exception) { }
            if (pids.Count == 0) return false;
            bool hidden = false;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                var title = new StringBuilder(256);
                GetWindowText(h, title, 256);
                if (cls.ToString().StartsWith("Chrome_WidgetWin") && title.Length > 0)
                {
                    ShowWindow(h, 0); // SW_HIDE
                    hidden = true;
                }
                return true;
            }, IntPtr.Zero);
            return hidden;
        }
    }

    /// <summary>Xbox-style controller battery straight from XInput (four levels only; no percentage).</summary>
    internal static class XInputBattery
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BatteryInformation
        {
            public byte BatteryType;   // 0 disconnected, 1 wired, 2 alkaline, 3 NiMH, 0xFF unknown
            public byte BatteryLevel;  // 0 empty, 1 low, 2 medium, 3 full
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int GetBatteryInformation(int userIndex, byte devType, ref BatteryInformation info);

        private static bool available = true;

        /// <summary>"Wired", "Full", "Medium", "Low", "Empty", or null when no controller is connected.</summary>
        public static string Read()
        {
            if (!available) return null;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var b = new BatteryInformation();
                    if (GetBatteryInformation(i, 0, ref b) != 0) continue;
                    if (b.BatteryType == 0) continue;
                    if (b.BatteryType == 1) return "Wired";
                    if (b.BatteryType == 0xFF) return "Connected";
                    string[] levels = { "Empty", "Low", "Medium", "Full" };
                    return levels[Math.Min(3, (int)b.BatteryLevel)];
                }
            }
            catch (DllNotFoundException) { available = false; }
            catch (EntryPointNotFoundException) { available = false; }
            catch (Exception) { }
            return null;
        }
    }

    /// <summary>
    /// The battery of a controller Playnite reads itself (through SDL), e.g. the Steam Controller outside games, which
    /// Windows never sees as an Xbox pad. Playnite's own controller objects carry the SDL joystick handle.
    /// </summary>
    internal static class SdlBattery
    {
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_JoystickCurrentPowerLevel(IntPtr joystick);

        private static bool available = true;

        /// <summary>"Wired", "Full", "Medium", "Low", "Empty", "Connected" (level not known), or null.</summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public static string Read(object pad)
        {
            if (!available || pad == null) return null;
            try
            {
                var prop = pad.GetType().GetProperty("Joystic", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?? pad.GetType().GetProperty("Joystick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop == null) { available = false; return null; }
                object v = prop.GetValue(pad, null);
                if (!(v is IntPtr) || (IntPtr)v == IntPtr.Zero) return "Connected";
                switch (SDL_JoystickCurrentPowerLevel((IntPtr)v))
                {
                    case 0: return "Empty";
                    case 1: return "Low";
                    case 2: return "Medium";
                    case 3: case 5: return "Full";
                    case 4: return "Wired";
                    default: return "Connected";
                }
            }
            catch (DllNotFoundException) { available = false; }
            catch (EntryPointNotFoundException) { available = false; }
            catch (Exception) { }
            return "Connected";
        }
    }
}

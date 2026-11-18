using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KippixOverlay
{
    internal static class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // (Windows 10 2004 and later: the window is left out of screenshots and recordings)
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("powrprof.dll", SetLastError = true)] public static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        private const uint KEYEVENTF_KEYUP = 0x2;
        private const uint KEYEVENTF_UNICODE = 0x4;

        public const uint MOUSE_MOVE = 0x1, MOUSE_LEFTDOWN = 0x2, MOUSE_LEFTUP = 0x4, MOUSE_RIGHTDOWN = 0x8, MOUSE_RIGHTUP = 0x10, MOUSE_WHEEL = 0x800;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        public static bool GetMemory(out ulong total, out ulong available)
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            bool ok = GlobalMemoryStatusEx(ref m);
            total = m.ullTotalPhys;
            available = m.ullAvailPhys;
            return ok;
        }

        public static uint GetProcessId(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        /// <summary>Full exe path of a process; works across 32/64-bit and for most protected games.</summary>
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

        /// <summary>Brings a window to the front, working around Windows' foreground-lock rules.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            // already in front (e.g. the game, after an overlay that let it keep the focus): nothing to do (no thread-input
            // attaching to it, no simulated Alt)
            if (GetForegroundWindow() == hwnd && !IsIconic(hwnd)) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE

            uint dummy;
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out dummy);
            uint cur = GetCurrentThreadId();
            bool attached = false;
            // (0.99.59) not while the window in front isn't answering (a game busy going back to full screen): joining its input
            // then waited for it, seconds at a time (the overlay took 3-5 s to appear after the capture preview)
            if (fgThread != 0 && fgThread != cur && !IsHungAppWindow(fg)) attached = AttachThreadInput(cur, fgThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            if (attached) AttachThreadInput(cur, fgThread, false);

            // (0.99.51) not Alt held down across the switch any more: the game losing the foreground got Alt down and never Alt up,
            // so its next Enter was Alt+Enter (windowed, and the game saves it). A whole tap of an unassigned key first, then
            // (only if that wasn't enough) a whole tap of Alt.
            if (GetForegroundWindow() != hwnd)
            {
                keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                keybd_event(0xE8, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(hwnd);
            }
            if (GetForegroundWindow() != hwnd)
            {
                keybd_event(0x12, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                keybd_event(0x12, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(hwnd);
            }
        }

        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);

        /// <summary>(0.99.59) ForceForeground off the calling thread: the window is already showing; getting the focus can't hold up the screen.</summary>
        public static void ForceForegroundSoon(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            KippixShared.SafeWork.Queue(_ => { try { ForceForeground(hwnd); } catch (Exception) { } });
        }

        public static void SetTopmost(IntPtr hwnd, bool topmost)
        {
            if (hwnd == IntPtr.Zero) return;
            // SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW (and SWP_NOACTIVATE when removing)
            SetWindowPos(hwnd, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0, topmost ? 0x43u : 0x13u);
        }

        // ---------- Keyboard ----------

        private static readonly Dictionary<string, byte> KeyNames = BuildKeyNames();

        private static Dictionary<string, byte> BuildKeyNames()
        {
            var d = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            d["ctrl"] = 0x11; d["control"] = 0x11;
            d["shift"] = 0x10;
            d["alt"] = 0x12;
            d["win"] = 0x5B; d["windows"] = 0x5B;
            d["tab"] = 0x09; d["enter"] = 0x0D; d["esc"] = 0x1B; d["escape"] = 0x1B; d["space"] = 0x20;
            d["pageup"] = 0x21; d["pagedown"] = 0x22; d["end"] = 0x23; d["home"] = 0x24;
            d["left"] = 0x25; d["up"] = 0x26; d["right"] = 0x27; d["down"] = 0x28;
            d["printscreen"] = 0x2C; d["prtsc"] = 0x2C; d["insert"] = 0x2D; d["delete"] = 0x2E;
            d["`"] = 0xC0; d["tilde"] = 0xC0; d["backtick"] = 0xC0;
            d["-"] = 0xBD; d["="] = 0xBB; d["["] = 0xDB; d["]"] = 0xDD; d[";"] = 0xBA; d["'"] = 0xDE;
            d[","] = 0xBC; d["."] = 0xBE; d["/"] = 0xBF; d["\\"] = 0xDC;
            for (int i = 0; i <= 9; i++) d["numpad" + i] = (byte)(0x60 + i);
            for (int i = 1; i <= 24; i++) d["f" + i] = (byte)(0x6F + i);
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = (byte)c;
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = (byte)c;
            return d;
        }

        public static List<byte> ParseHotkey(string hotkey)
        {
            var keys = new List<byte>();
            if (string.IsNullOrWhiteSpace(hotkey)) return keys;
            foreach (string raw in hotkey.Split('+'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                byte vk;
                if (KeyNames.TryGetValue(part, out vk)) keys.Add(vk);
            }
            return keys;
        }

        private static bool IsExtended(byte vk)
        {
            return vk == 0x5B || vk == 0x5C || (vk >= 0x21 && vk <= 0x28) || vk == 0x2C || vk == 0x2D || vk == 0x2E;
        }

        private static void Key(byte vk, bool up)
        {
            uint flags = 0;
            if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            if (up) flags |= KEYEVENTF_KEYUP;
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), flags, UIntPtr.Zero);
        }

        /// <summary>Presses a key combination like "Ctrl+Shift+M". Blocks ~150 ms; call off the UI thread.</summary>
        public static bool SendHotkey(string hotkey)
        {
            List<byte> keys = ParseHotkey(hotkey);
            if (keys.Count == 0) return false;
            foreach (byte k in keys) { Key(k, false); Thread.Sleep(25); }
            Thread.Sleep(50);
            for (int i = keys.Count - 1; i >= 0; i--) { Key(keys[i], true); Thread.Sleep(15); }
            return true;
        }

        /// <summary>(1.0.4) A key with a modifier held (Shift+Tab, Alt+Left).</summary>
        public static void TapCombo(byte mod, byte vk)
        {
            Key(mod, false);
            Key(vk, false);
            Key(vk, true);
            Key(mod, true);
        }

        public static void TapKey(byte vk)
        {
            Key(vk, false);
            Key(vk, true);
        }

        /// <summary>Types any character into the focused window.</summary>
        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var inputs = new INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[i * 2].type = 1;
                inputs[i * 2].u.ki.wScan = text[i];
                inputs[i * 2].u.ki.dwFlags = KEYEVENTF_UNICODE;
                inputs[i * 2 + 1].type = 1;
                inputs[i * 2 + 1].u.ki.wScan = text[i];
                inputs[i * 2 + 1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        /// <summary>
        /// (0.99.56) Closes a game the way its own close button would: its windows are asked to close (WM_CLOSE), so it can save
        /// what it saves on the way out (its settings: resolution, display mode...); only if it's still running after
        /// <paramref name="waitMs"/> is it forced closed (taskkill /T /F, as before, which lost whatever it hadn't saved yet).
        /// Runs off the calling thread; <paramref name="done"/> gets true when it closed by itself.
        /// </summary>
        /// <summary>(1.0.4) Asks one window to close (its close button: WM_CLOSE).</summary>
        public static bool CloseWindow(IntPtr h) { return h != IntPtr.Zero && PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }

        public static void CloseGracefully(uint pid, int waitMs, Action<bool> done)
        {
            GameFreeze.UnfreezeIf(pid);   // (1.0.4) a frozen game can't answer "close"
            new System.Threading.Thread(() =>
            {
                bool gentle = false;
                try
                {
                    int asked = 0;
                    EnumWindows((h, l) =>
                    {
                        uint wp;
                        GetWindowThreadProcessId(h, out wp);
                        if (wp == pid && IsWindowVisible(h) && PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero)) asked++;   // WM_CLOSE
                        return true;
                    }, IntPtr.Zero);
                    if (asked > 0)
                    {
                        try
                        {
                            using (var p = Process.GetProcessById((int)pid))
                                gentle = p.WaitForExit(waitMs);
                        }
                        catch (ArgumentException) { gentle = true; }   // (already gone)
                    }
                }
                catch (Exception) { }
                if (!gentle) RunHidden("taskkill", "/PID " + pid + " /T /F");
                if (done != null) try { done(gentle); } catch (Exception) { }
            }) { IsBackground = true }.Start();
        }

        public static void RunHidden(string exe, string args)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                })) { }   // (only started: the handle isn't kept)
            }
            catch (Exception) { }
        }
    }
}

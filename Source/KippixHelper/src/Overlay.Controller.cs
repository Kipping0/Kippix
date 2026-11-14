using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace KippixOverlay
{
    /// <summary>Buttons use Xbox names; PlayStation buttons are mapped onto them (Cross = A, Circle = B, PS = Guide...).</summary>
    internal static class Btn
    {
        public const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008;
        public const ushort Start = 0x0010, Back = 0x0020, LThumb = 0x0040, RThumb = 0x0080;
        public const ushort LB = 0x0100, RB = 0x0200, Guide = 0x0400, Mic = 0x0800;
        public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
        public const ushort Directions = Up | Down | Left | Right;
    }

    internal enum PadKind { Xbox, PlayStation }

    internal static class XInput
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Gamepad
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct State
        {
            public uint dwPacketNumber;
            public Gamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Battery
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        // Ordinal 100 is the undocumented XInputGetStateEx, the only XInput call that reports the Guide button.
        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        public static extern int GetStateEx(int userIndex, ref State state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        public static extern int GetState(int userIndex, ref State state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        public static extern int GetBattery(int userIndex, byte devType, ref Battery info);

        [StructLayout(LayoutKind.Sequential)]
        public struct Vibration
        {
            public ushort LeftMotorSpeed;    // the big, low rumble
            public ushort RightMotorSpeed;   // the small, quick one
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        public static extern int SetState(int userIndex, ref Vibration vibration);
    }

    /// <summary>Reads Sony controllers (DualSense, DualSense Edge, DualShock 4) directly over HID.</summary>
    internal class HidPad
    {
        public string Path;
        public bool Bluetooth;
        public bool DualSense;
        public volatile bool Alive = true;
        public volatile ushort Buttons;
        public volatile short LX, LY, RX, RY;
        public volatile int Battery = -1;     // percent
        public volatile bool Charging;
        public DateTime LastInput = DateTime.MinValue;

        private SafeFileHandle handle;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int toRead, out int read, IntPtr overlapped);

        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int toWrite, out int written, IntPtr overlapped);

        private static readonly uint[] crcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            var tab = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; tab[n] = c; }
            return tab;
        }
        private static uint Crc(byte seed, byte[] data, int count)
        {
            uint c = 0xFFFFFFFFu;
            c = crcTable[(c ^ seed) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < count; i++) c = crcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return ~c;
        }

        /// <summary>
        /// (0.99.30) Sets the rumble motors (0-255; 0 and 0 stops them). DualSense and DualShock 4, USB and Bluetooth; only the
        /// rumble is touched (the light bar and the rest are left as they are). Its own handle, so the reading thread isn't held up.
        /// </summary>
        public void Rumble(byte big, byte small)
        {
            byte[] r;
            if (DualSense && !Bluetooth) { r = new byte[48]; r[0] = 0x02; r[1] = 0x03; r[3] = small; r[4] = big; }
            else if (DualSense)
            {
                r = new byte[78]; r[0] = 0x31; r[1] = 0x02; r[2] = 0x03; r[4] = small; r[5] = big;
                uint crc = Crc(0xA2, r, 74);
                r[74] = (byte)crc; r[75] = (byte)(crc >> 8); r[76] = (byte)(crc >> 16); r[77] = (byte)(crc >> 24);
            }
            else if (!Bluetooth) { r = new byte[32]; r[0] = 0x05; r[1] = 0x01; r[4] = small; r[5] = big; }
            else
            {
                r = new byte[78]; r[0] = 0x11; r[1] = 0xC0; r[3] = 0x01; r[6] = small; r[7] = big;
                uint crc = Crc(0xA2, r, 74);
                r[74] = (byte)crc; r[75] = (byte)(crc >> 8); r[76] = (byte)(crc >> 16); r[77] = (byte)(crc >> 24);
            }
            using (var h = CreateFile(Path, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                int w;
                WriteFile(h, r, r.Length, out w, IntPtr.Zero);
            }
        }

        public bool Open()
        {
            // Shared access so Playnite, Steam etc. keep working with the controller.
            handle = CreateFile(Path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) handle = CreateFile(Path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;

            int length = 128;
            IntPtr pp;
            if (HidD_GetPreparsedData(handle, out pp))
            {
                HIDP_CAPS caps;
                if (HidP_GetCaps(pp, out caps) == 0x00110000 && caps.InputReportByteLength > 0) length = caps.InputReportByteLength;
                HidD_FreePreparsedData(pp);
            }

            int len = length;
            var t = new Thread(() => ReadLoop(len)) { IsBackground = true, Name = "Kippix HID" };
            t.Start();
            return true;
        }

        private void ReadLoop(int length)
        {
            var buf = new byte[Math.Max(length, 64)];
            try
            {
                while (Alive)
                {
                    int read;
                    if (!ReadFile(handle, buf, length, out read, IntPtr.Zero) || read <= 0) break;
                    Parse(buf, read);
                }
            }
            catch (Exception) { }
            Alive = false;
            Buttons = 0;
            try { handle.Dispose(); } catch (Exception) { }
        }

        private static short Axis(byte v, bool invert)
        {
            int c = (v - 128) * 256;
            if (invert) c = -c;
            if (c > short.MaxValue) c = short.MaxValue;
            if (c < short.MinValue) c = short.MinValue;
            return (short)c;
        }

        private void Parse(byte[] d, int len)
        {
            int id = d[0];
            int s, b, status = -1;
            bool ds4Battery = false;

            if (DualSense)
            {
                if (id == 0x01 && len >= 64) { s = 1; b = 8; status = 53; }        // USB
                else if (id == 0x31 && len >= 55) { s = 2; b = 9; status = 54; }   // Bluetooth, full report
                else if (id == 0x01 && len >= 8) { s = 1; b = 5; }                // Bluetooth, simple report
                else return;
            }
            else
            {
                if (id == 0x01 && len >= 8) { s = 1; b = 5; if (len >= 31) { status = 30; ds4Battery = true; } }
                else if (id == 0x11 && len >= 33) { s = 3; b = 7; status = 32; ds4Battery = true; }
                else return;
            }

            LX = Axis(d[s], false);
            LY = Axis(d[s + 1], true);
            RX = Axis(d[s + 2], false);
            RY = Axis(d[s + 3], true);

            byte b0 = d[b], b1 = d[b + 1], b2 = d[b + 2];
            ushort o = 0;
            switch (b0 & 0x0F)
            {
                case 0: o |= Btn.Up; break;
                case 1: o |= Btn.Up | Btn.Right; break;
                case 2: o |= Btn.Right; break;
                case 3: o |= Btn.Down | Btn.Right; break;
                case 4: o |= Btn.Down; break;
                case 5: o |= Btn.Down | Btn.Left; break;
                case 6: o |= Btn.Left; break;
                case 7: o |= Btn.Up | Btn.Left; break;
            }
            if ((b0 & 0x10) != 0) o |= Btn.X;   // Square
            if ((b0 & 0x20) != 0) o |= Btn.A;   // Cross
            if ((b0 & 0x40) != 0) o |= Btn.B;   // Circle
            if ((b0 & 0x80) != 0) o |= Btn.Y;   // Triangle
            if ((b1 & 0x01) != 0) o |= Btn.LB;
            if ((b1 & 0x02) != 0) o |= Btn.RB;
            if ((b1 & 0x10) != 0) o |= Btn.Back;   // Create / Share
            if ((b1 & 0x20) != 0) o |= Btn.Start;  // Options
            if ((b1 & 0x40) != 0) o |= Btn.LThumb;
            if ((b1 & 0x80) != 0) o |= Btn.RThumb;
            if ((b2 & 0x01) != 0) o |= Btn.Guide;  // PS
            if (DualSense && (b2 & 0x04) != 0) o |= Btn.Mic;

            if (o != Buttons) LastInput = DateTime.UtcNow;
            Buttons = o;

            if (status >= 0 && status < len)
            {
                byte st = d[status];
                if (ds4Battery)
                {
                    bool cable = (st & 0x10) != 0;
                    int level = st & 0x0F;
                    Battery = Math.Min(100, level * (cable ? 9 : 10) + 5);
                    Charging = cable && level < 11;
                }
                else
                {
                    Battery = Math.Min(100, (st & 0x0F) * 10 + 5);
                    int cs = (st & 0xF0) >> 4;
                    Charging = cs == 1;
                }
            }
        }
    }

    internal static class HidScanner
    {
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);

        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        private static readonly string[] DualSensePids = { "0ce6", "0df2" };
        private static readonly string[] Ds4Pids = { "05c4", "09cc", "0ba0" };

        public static List<string> SonyPaths() { return PathsMatching(IsSony); }

        /// <summary>Every HID device path the picker likes (vendor ids are in the path: vid_054c Sony, vid_057e Nintendo).</summary>
        public static List<string> PathsMatching(Func<string, bool> pick)
        {
            var result = new List<string>();
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
            IntPtr buf = Marshal.AllocHGlobal(2048);
            try
            {
                for (uint i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data)) break;
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    uint req;
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, 2048, out req, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    if (path != null && pick(path)) result.Add(path);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        private static bool IsSony(string path)
        {
            string p = path.ToLowerInvariant();
            if (!p.Contains("054c")) return false;
            return IsDualSense(p) || Contains(p, Ds4Pids);
        }

        public static bool IsDualSense(string path)
        {
            return Contains(path.ToLowerInvariant(), DualSensePids);
        }

        public static bool IsBluetooth(string path)
        {
            string p = path.ToLowerInvariant();
            return p.Contains("00001124-0000-1000-8000-00805f9b34fb") || p.Contains("_vid&0002");
        }

        private static bool Contains(string p, string[] pids)
        {
            foreach (string pid in pids)
                if (p.Contains("pid_" + pid) || p.Contains("pid&" + pid)) return true;
            return false;
        }
    }

    /// <summary>Polls XInput and Sony HID controllers, merges them, and reports presses and releases.</summary>
    /// <summary>
    /// (0.99.31) Valve's pads (Steam Controller, Steam Deck; USB vendor 28DE) don't take XInput rumble outside a game's Steam
    /// Input, so they're given a buzz the way Steam itself does it, as HID feature reports (Linux's hid-steam driver's layout):
    /// a haptic pulse train on both pads (0x8F, the original Steam Controller) and the rumble command (0xEB, Steam Deck and
    /// newer firmware), then the rumble stopped. Every 28DE interface is tried (only the controller's own takes them); never throws.
    /// </summary>
    internal static class SteamPadHaptics
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

        private static List<string> paths;
        private static DateTime pathsAt = DateTime.MinValue;
        private static int logCount;

        public static void Buzz(int ms, double bigStrength, double smallStrength)
        {
            try
            {
                if (paths == null || (DateTime.UtcNow - pathsAt).TotalSeconds > 30)
                {
                    paths = HidScanner.PathsMatching(p => p.ToLowerInvariant().Contains("vid_28de"));
                    pathsAt = DateTime.UtcNow;
                }
                if (paths.Count == 0) return;
                // pulses (the original Steam Controller / Deck): the stronger, the longer each is on; for about ms
                ushort on = (ushort)(300 + 1700 * Math.Max(0, Math.Min(1, smallStrength))), off = 3000, count = (ushort)Math.Max(1, ms * 1000 / (on + off));
                var pulse = Report(0x8F, 8, 2, (byte)on, (byte)(on >> 8), (byte)off, (byte)(off >> 8), (byte)count, (byte)(count >> 8), 0);
                ushort big = (ushort)(65535 * Math.Max(0, Math.Min(1, bigStrength))), small = (ushort)(65535 * Math.Max(0, Math.Min(1, smallStrength)));
                var rumble = Report(0xEB, 9, 0, 0, 0, (byte)big, (byte)(big >> 8), (byte)small, (byte)(small >> 8), 2, 0);
                var stop = Report(0xEB, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                int took = 0;
                var opened = new List<SafeFileHandle>();
                var outStop = new List<SafeFileHandle>();   // (the 2026 controller's: stopped with an output report)
                var notes = new List<string>();   // (0.99.32) what each interface did, once, for the log
                try
                {
                    foreach (string p in paths)
                    {
                        string id = ShortId(p);
                        var h = CreateFile(p, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (h.IsInvalid)
                        {
                            int err = Marshal.GetLastWin32Error();
                            h.Dispose();
                            h = CreateFile(p, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // (write only)
                            if (h.IsInvalid) { int err2 = Marshal.GetLastWin32Error(); h.Dispose(); h = CreateFile(p, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); if (h.IsInvalid) { notes.Add(id + " can't open (" + err + "/" + err2 + ")"); h.Dispose(); continue; } }
                        }
                        // (0.99.33) the 2026 Steam Controller (and its Puck, pid 1302-1304): haptics are output reports,
                        // 0x80 rumble [type, intensity LE16, left speed LE16, left gain, right speed LE16, right gain] and
                        // 0x81 pulse [side, on us LE16, off us LE16, count LE16] (Linux 7.3's hid-steam)
                        int olen = Caps(h, 3);
                        if (olen >= 10)
                        {
                            var r80 = new byte[olen]; r80[0] = 0x80; r80[4] = (byte)big; r80[5] = (byte)(big >> 8); r80[6] = 0; r80[7] = (byte)small; r80[8] = (byte)(small >> 8);   // (0.99.35: no extra gain: soft)
                            var r81 = new byte[olen]; r81[0] = 0x81; r81[1] = 2; r81[2] = (byte)on; r81[3] = (byte)(on >> 8); r81[4] = (byte)off; r81[5] = (byte)(off >> 8); r81[6] = (byte)count; r81[7] = (byte)(count >> 8);
                            // (0.99.34) the control pipe said no (error 31): the interrupt pipe (WriteFile), as hid_hw_output_report does
                            string ids = string.Join(",", ReportIds(h, 1).Select(x => "0x" + x.ToString("X2")).ToArray());
                            int w1, w2;
                            bool o1 = WriteFile(h, r80, olen, out w1, IntPtr.Zero);
                            int e1 = o1 ? 0 : Marshal.GetLastWin32Error();
                            if (!o1) { o1 = HidD_SetOutputReport(h, r80, olen); if (o1) e1 = 0; }
                            bool o2 = false; int e2 = -1;   // (0.99.35: the pulse train felt hard: the rumble alone, softly)
                            w2 = 0;
                            notes.Add(id + " out " + olen + " ids [" + ids + "]: rumble " + (o1 ? "ok" : "err " + e1));
                            if (o1 || o2) { took++; opened.Add(h); outStop.Add(h); continue; }
                        }
                        int len = FeatureLength(h);
                        if (len < 11) { notes.Add(id + " no feature reports (" + len + ")"); h.Dispose(); continue; }   // (the keyboard / mouse parts)
                        bool a = HidD_SetFeature(h, Sized(pulse, len), len);
                        int ea = a ? 0 : Marshal.GetLastWin32Error();
                        bool b = HidD_SetFeature(h, Sized(rumble, len), len);
                        int eb = b ? 0 : Marshal.GetLastWin32Error();
                        notes.Add(id + " len " + len + ": pulse " + (a ? "ok" : "err " + ea) + ", rumble " + (b ? "ok" : "err " + eb));
                        if (a || b) { took++; opened.Add(h); } else h.Dispose();
                    }
                    if (logCount < 3) { logCount++; LogManagerHelper.Info("Kippix overlay: Steam pad buzz: " + took + " of " + paths.Count + " Valve interfaces took it: " + string.Join("; ", notes.ToArray())); }
                    if (opened.Count > 0)
                    {
                        Thread.Sleep(Math.Max(20, ms));
                        foreach (var h in opened)
                            try
                            {
                                if (outStop.Contains(h)) { int ol = Caps(h, 3); var s0 = new byte[ol]; s0[0] = 0x80; int w0; if (!WriteFile(h, s0, ol, out w0, IntPtr.Zero)) HidD_SetOutputReport(h, s0, ol); }
                                else { int len = FeatureLength(h); HidD_SetFeature(h, Sized(stop, len), len); }
                            }
                            catch (Exception) { }
                    }
                }
                finally { foreach (var h in opened) try { h.Dispose(); } catch (Exception) { } }
            }
            catch (Exception) { }
        }

        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, short[] caps);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int toWrite, out int written, IntPtr overlapped);

        [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort length, IntPtr data);

        /// <summary>The report ids an interface declares for a kind of report (0 input, 1 output, 2 feature), for the log.</summary>
        private static List<int> ReportIds(SafeFileHandle h, int type)
        {
            var ids = new List<int>();
            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) return ids;
            try
            {
                foreach (bool buttons in new[] { false, true })
                {
                    ushort n = 64;
                    var buf = new byte[72 * 64];   // HIDP_VALUE_CAPS / HIDP_BUTTON_CAPS: 72 bytes, ReportID at offset 2
                    int st = buttons ? HidP_GetButtonCaps(type, buf, ref n, pp) : HidP_GetValueCaps(type, buf, ref n, pp);
                    if (st != 0x00110000) continue;
                    for (int i = 0; i < n; i++) { int rid = buf[72 * i + 2]; if (!ids.Contains(rid)) ids.Add(rid); }
                }
            }
            catch (Exception) { }
            finally { HidD_FreePreparsedData(pp); }
            return ids;
        }

        /// <summary>One of the interface's report sizes (HIDP_CAPS: 2 input, 3 output, 4 feature), 0 when it has none.</summary>
        private static int Caps(SafeFileHandle h, int which)
        {
            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) return 0;
            try
            {
                var caps = new short[32];
                if (HidP_GetCaps(pp, caps) != 0x00110000) return 0;
                return (ushort)caps[which];
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        /// <summary>The interface's feature report size (with its id byte), 0 when it has none.</summary>
        private static int FeatureLength(SafeFileHandle h)
        {
            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) return 0;
            try
            {
                var caps = new short[32];   // HIDP_CAPS: Usage, UsagePage, Input/Output/Feature report lengths, ...
                if (HidP_GetCaps(pp, caps) != 0x00110000) return 0;
                return (ushort)caps[4];
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        /// <summary>"pid_1142 mi_02 col01" from a HID path, for the log.</summary>
        private static string ShortId(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            var bits = new List<string>();
            foreach (string k in new[] { "pid_", "mi_", "col" })
            {
                int i = p.IndexOf(k, StringComparison.Ordinal);
                if (i < 0) continue;
                int j = i + k.Length;
                while (j < p.Length && char.IsLetterOrDigit(p[j])) j++;
                bits.Add(p.Substring(i, j - i));
            }
            return string.Join(" ", bits.ToArray());
        }

        private static byte[] Sized(byte[] r, int len)
        {
            var o = new byte[len];
            Array.Copy(r, o, Math.Min(len, r.Length));
            return o;
        }

        /// <summary>A 65-byte feature report: report id 0, then the command and its bytes.</summary>
        private static byte[] Report(params byte[] body)
        {
            var r = new byte[65];
            Array.Copy(body, 0, r, 1, Math.Min(body.Length, 64));
            return r;
        }
    }

    internal static class LogManagerHelper
    {
        private static readonly Playnite.SDK.ILogger log = Playnite.SDK.LogManager.GetLogger();
        public static void Info(string s) { try { log.Info(s); } catch (Exception) { } }
    }

    internal class ControllerPoller
    {
        /// <summary>(newly pressed, currently held). Raised on the polling thread whenever anything changes.</summary>
        public Action<ushort, ushort> Changed;

        /// <summary>When true the left stick moves the mouse and the right stick scrolls.</summary>
        public volatile bool MouseMode;

        /// <summary>
        /// A keyboard shortcut that counts as the Home button (opens the overlay). For controllers Windows only sees through
        /// Steam (the Steam Controller): bind a button to it in the game's Steam Input layout. Empty: off.
        /// </summary>
        public volatile string OverlayHotkey = "Ctrl+Shift+O";

        /// <summary>
        /// The Home / Steam button as Playnite itself reads it (Playnite reads controllers Windows doesn't show as Xbox
        /// pads, like the Steam Controller). Null: not used. Asked every loop; the plugin keeps it cheap.
        /// </summary>
        public Func<bool> PlayniteGuideDown;
        private bool hotkeyWasDown;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        private string hotkeyParsedFor;
        private List<byte> hotkeyKeys = new List<byte>();

        private bool HotkeyDown()
        {
            string hk = OverlayHotkey;
            if (string.IsNullOrWhiteSpace(hk)) return false;
            if (hk != hotkeyParsedFor)
            {
                hotkeyParsedFor = hk;
                var parsed = Native.ParseHotkey(hk);
                int parts = hk.Split('+').Count(x => x.Trim().Length > 0);
                hotkeyKeys = parsed.Count == parts ? parsed : new List<byte>();   // a part it doesn't know: off (not Ctrl+Shift alone)
            }
            var keys = hotkeyKeys;
            if (keys.Count == 0) return false;
            foreach (byte vk in keys) if ((GetAsyncKeyState(vk) & 0x8000) == 0) return false;
            return true;
        }

        public PadKind Kind = PadKind.Xbox;

        private readonly object padLock = new object();
        private readonly List<HidPad> hidPads = new List<HidPad>();
        private Thread thread;
        private volatile bool running;
        private bool useEx = true;
        private bool xinputAvailable = true;

        // written by the polling thread, read from the UI and timer threads
        private volatile int xBatteryType = -1, xBatteryLevel = -1;
        private volatile int lastXPad = -1;

        /// <summary>
        /// A controller this poller reads itself is connected (an XInput pad, or a Sony pad over HID). When none is (a Steam
        /// Controller outside games only reaches Windows as keys), the overlay takes keys at once instead of waiting a moment
        /// to see whether they're a copy of a controller press.
        /// </summary>
        public volatile bool SeesPad;

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "Kippix controller" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            lock (padLock) foreach (var p in hidPads) p.Alive = false;
        }

        /// <summary>
        /// (0.99.30) A short buzz on the controller (the overlay opening): the pad in use (an Xbox pad, anything Steam Input /
        /// DS4Windows presents as one, so a Steam Controller in a game too) and any Sony pad read directly. strength 0-1.
        /// Runs on its own thread; never throws.
        /// </summary>
        public void Pulse(int ms, double big, double small)
        {
            var t = new Thread(() =>
            {
                try
                {
                    var sony = new List<HidPad>();
                    lock (padLock) foreach (var p in hidPads) if (p.Alive) sony.Add(p);
                    int x = lastXPad;
                    var on = new XInput.Vibration { LeftMotorSpeed = (ushort)(65535 * Math.Max(0, Math.Min(1, big))), RightMotorSpeed = (ushort)(65535 * Math.Max(0, Math.Min(1, small))) };
                    var off = new XInput.Vibration();
                    var xs = new List<int>();
                    if (xinputAvailable)
                    {
                        if (x >= 0) xs.Add(x);
                        else for (int i = 0; i < 4; i++) xs.Add(i);
                        foreach (int i in xs) try { XInput.SetState(i, ref on); } catch (Exception) { }
                    }
                    foreach (var p in sony) try { p.Rumble((byte)(255 * big), (byte)(255 * small)); } catch (Exception) { }
                    var valve = new Thread(() => SteamPadHaptics.Buzz(ms, big, small)) { IsBackground = true, Name = "Kippix Steam pad buzz" };   // (0.99.31) the Steam Controller
                    valve.Start();
                    Thread.Sleep(Math.Max(20, ms));
                    foreach (int i in xs) try { XInput.SetState(i, ref off); } catch (Exception) { }
                    foreach (var p in sony) try { p.Rumble(0, 0); } catch (Exception) { }
                }
                catch (Exception) { }
            }) { IsBackground = true, Name = "Kippix rumble" };
            t.Start();
        }

        /// <summary>"wired", "Battery 60%", "Charging 40%" or "" when unknown.</summary>
        /// <summary>
        /// 0 fine (or wired/charging/unknown), 1 low, 2 almost empty. Xbox pads only report four levels,
        /// so "low" there is the pad's own Low level.
        /// </summary>
        public int BatteryWarning()
        {
            HidPad pad = null;
            lock (padLock) foreach (var p in hidPads) if (p.Alive) { pad = p; break; }
            if (Kind == PadKind.PlayStation && pad != null)
            {
                int pct = pad.Battery;
                if (!pad.Bluetooth || pad.Charging || pct < 0) return 0;
                return pct <= 5 ? 2 : pct <= 15 ? 1 : 0;
            }
            int type = xBatteryType, level = xBatteryLevel;
            if (lastXPad >= 0 && type >= 2 && level >= 0)
                return level == 0 ? 2 : level == 1 ? 1 : 0;
            return 0;
        }

        /// <summary>Short battery text for the overlay's bottom bar ("Full", "Low", "80%", "Wired"), or null without a controller.</summary>
        public string BatteryShort()
        {
            HidPad pad = null;
            lock (padLock) foreach (var p in hidPads) if (p.Alive) { pad = p; break; }
            if (Kind == PadKind.PlayStation && pad != null)
            {
                if (!pad.Bluetooth) return "Wired";
                if (pad.Battery < 0) return "Wireless";
                return pad.Battery + "%" + (pad.Charging ? " \u26A1" : "");
            }
            if (lastXPad < 0) return null;
            int type = xBatteryType, level = xBatteryLevel;   // (one reading: the polling thread may reset them meanwhile)
            if (type == 1) return "Wired";
            if (type >= 2 && level >= 0)
            {
                string[] levels = { "Empty", "Low", "Medium", "Full" };
                return levels[Math.Min(3, level)];
            }
            return "Connected";
        }

        public string StatusText()
        {
            HidPad pad = null;
            lock (padLock) foreach (var p in hidPads) if (p.Alive) { pad = p; break; }

            if (Kind == PadKind.PlayStation && pad != null)
            {
                if (!pad.Bluetooth) return "wired";
                if (pad.Battery < 0) return "wireless";
                return (pad.Charging ? "charging " : "battery ") + pad.Battery + "%";
            }
            int type = xBatteryType, level = xBatteryLevel;   // (one reading, as in BatteryShort)
            if (type == 1) return "wired";
            if (type >= 2 && level >= 0)
            {
                string[] levels = { "battery empty", "battery low", "battery medium", "battery full" };
                return levels[Math.Min(3, level)];
            }
            return "";
        }

        private int Read(int index, ref XInput.State st)
        {
            if (useEx)
            {
                try { return XInput.GetStateEx(index, ref st); }
                catch (EntryPointNotFoundException) { useEx = false; }
            }
            return XInput.GetState(index, ref st);
        }

        private void ScanHid()
        {
            List<string> paths;
            try { paths = HidScanner.SonyPaths(); }
            catch (Exception) { return; }

            lock (padLock)
            {
                hidPads.RemoveAll(p => !p.Alive);
                foreach (string path in paths)
                {
                    bool known = false;
                    foreach (var p in hidPads) if (string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) known = true;
                    if (known) continue;
                    var pad = new HidPad
                    {
                        Path = path,
                        DualSense = HidScanner.IsDualSense(path),
                        Bluetooth = HidScanner.IsBluetooth(path)
                    };
                    if (pad.Open()) hidPads.Add(pad);
                }
            }
        }

        private void Loop()
        {
            var connected = new bool[4];
            var nextCheck = new DateTime[4];
            ushort prev = 0;
            ushort repeatMask = 0;
            DateTime repeatAt = DateTime.MaxValue;
            DateTime nextBattery = DateTime.MinValue;
            DateTime nextHidScan = DateTime.MinValue;
            double scrollAccum = 0;

            while (running)
            {
                DateTime now = DateTime.UtcNow;
                ushort held = 0;
                int lx = 0, ly = 0, rx = 0, ry = 0;

                if (now >= nextHidScan)
                {
                    nextHidScan = now.AddSeconds(3);
                    ScanHid();
                }

                // --- XInput (Xbox and anything Steam / DS4Windows presents as Xbox) ---
                if (xinputAvailable)
                {
                    try
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            if (!connected[i] && now < nextCheck[i]) continue;
                            var st = new XInput.State();
                            if (Read(i, ref st) == 0)
                            {
                                connected[i] = true;
                                held |= st.Gamepad.wButtons;
                                Biggest(ref lx, ref ly, st.Gamepad.sThumbLX, st.Gamepad.sThumbLY);
                                Biggest(ref rx, ref ry, st.Gamepad.sThumbRX, st.Gamepad.sThumbRY);
                                if (st.Gamepad.wButtons != 0)
                                {
                                    // a different pad in use: read its battery now, not the old pad's for up to 15 s
                                    if (lastXPad != i) nextBattery = DateTime.MinValue;
                                    lastXPad = i;
                                    Kind = PadKind.Xbox;
                                }
                                else if (lastXPad < 0) lastXPad = i;
                            }
                            else
                            {
                                connected[i] = false;
                                nextCheck[i] = now.AddSeconds(2);
                                // that pad went away: its battery reading goes with it
                                if (lastXPad == i) { lastXPad = -1; xBatteryType = -1; xBatteryLevel = -1; nextBattery = DateTime.MinValue; }
                            }
                        }

                        if (now >= nextBattery && lastXPad >= 0)
                        {
                            nextBattery = now.AddSeconds(15);
                            var bat = new XInput.Battery();
                            if (XInput.GetBattery(lastXPad, 0, ref bat) == 0)
                            {
                                xBatteryType = bat.BatteryType;
                                xBatteryLevel = bat.BatteryLevel;
                            }
                        }
                    }
                    catch (DllNotFoundException) { xinputAvailable = false; }
                    catch (Exception) { }
                }

                // --- Sony HID ---
                bool anyHid = false;
                lock (padLock)
                {
                    foreach (var p in hidPads)
                    {
                        if (!p.Alive) continue;
                        anyHid = true;
                        ushort pb = p.Buttons;
                        held |= pb;
                        Biggest(ref lx, ref ly, p.LX, p.LY);
                        Biggest(ref rx, ref ry, p.RX, p.RY);
                        if (pb != 0) Kind = PadKind.PlayStation;
                    }
                }
                if (anyHid && lastXPad < 0) Kind = PadKind.PlayStation;
                SeesPad = anyHid || connected[0] || connected[1] || connected[2] || connected[3];

                // Left stick doubles as a D-pad in menus (but not while it drives the mouse).
                if (!MouseMode) held |= StickToDpad(lx, ly);

                // the Steam Controller's Steam button (and other pads only Playnite reads)
                try { var pg = PlayniteGuideDown; if (pg != null && pg()) held |= Btn.Guide; } catch (Exception) { }

                // the overlay's keyboard shortcut: a press of the Home button (the moment it goes down)
                bool hkDown = false;
                try { hkDown = HotkeyDown(); } catch (Exception) { }
                if (hkDown && !hotkeyWasDown) held |= Btn.Guide;
                hotkeyWasDown = hkDown;

                ushort pressed = (ushort)(held & ~prev);

                if ((pressed & Btn.Directions) != 0)
                {
                    repeatMask = (ushort)(pressed & Btn.Directions);
                    repeatAt = now.AddMilliseconds(400);
                }
                else if ((held & repeatMask) == 0)
                {
                    repeatMask = 0;
                    repeatAt = DateTime.MaxValue;
                }
                else if (now >= repeatAt)
                {
                    pressed |= (ushort)(held & repeatMask);
                    repeatAt = now.AddMilliseconds(110);
                }

                if (pressed != 0 || held != prev)
                {
                    var handler = Changed;
                    if (handler != null)
                    {
                        try { handler(pressed, held); } catch (Exception) { }
                    }
                }
                prev = held;

                if (MouseMode) MoveMouse(lx, ly, ry, ref scrollAccum);

                Thread.Sleep(14);
            }
        }

        private static void Biggest(ref int x, ref int y, int nx, int ny)
        {
            if ((long)nx * nx + (long)ny * ny > (long)x * x + (long)y * y) { x = nx; y = ny; }
        }

        private static ushort StickToDpad(int x, int y)
        {
            const int dead = 16000;
            ushort b = 0;
            if (y > dead) b |= Btn.Up;
            if (y < -dead) b |= Btn.Down;
            if (x < -dead) b |= Btn.Left;
            if (x > dead) b |= Btn.Right;
            return b;
        }

        private static void MoveMouse(int lx, int ly, int ry, ref double scrollAccum)
        {
            const double dead = 7000;
            double mag = Math.Sqrt((double)lx * lx + (double)ly * ly);
            if (mag > dead)
            {
                double n = Math.Min(1.0, (mag - dead) / (32767 - dead));
                double speed = 2 + n * n * 26;
                int dx = (int)Math.Round(lx / mag * speed);
                int dy = (int)Math.Round(-ly / mag * speed);
                if (dx != 0 || dy != 0) Native.mouse_event(Native.MOUSE_MOVE, dx, dy, 0, UIntPtr.Zero);
            }

            if (Math.Abs(ry) > dead)
            {
                double n = (Math.Abs(ry) - dead) / (32767 - dead);
                scrollAccum += Math.Sign(ry) * (8 + n * n * 50);
                if (Math.Abs(scrollAccum) >= 40)
                {
                    int amount = (int)scrollAccum;
                    scrollAccum -= amount;
                    Native.mouse_event(Native.MOUSE_WHEEL, 0, 0, amount, UIntPtr.Zero);
                }
            }
            else scrollAccum = 0;
        }
    }
}

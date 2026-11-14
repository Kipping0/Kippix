using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KippixShared
{
    // =====================================================================================================================
    // (0.99.72) HDR on / off per display, Bluetooth on / off, and connecting / disconnecting paired Bluetooth headphones and
    // speakers. Used by the in-game overlay and by Kippix's Bluetooth page. Everything here is Windows' own: the display
    // settings API (as Settings › Display › Use HDR), Windows.Devices.Radios (as the Bluetooth switch in Quick settings) and the
    // Bluetooth audio driver's own connect / disconnect (as Settings › Bluetooth › Connect). Nothing is installed and nothing runs
    // in the background.
    // =====================================================================================================================

    internal class HdrDisplay
    {
        public string Name;
        public bool Supported;
        public bool On;
        internal uint AdapterLow;
        internal int AdapterHigh;
        internal uint TargetId;
        internal bool NewApi;   // Windows 11 24H2 and later: HDR has its own switch (separate from colour management)
        public string Key { get { return AdapterLow + ":" + AdapterHigh + ":" + TargetId; } }
    }

    internal static class Hdr
    {
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, IntPtr topology);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(IntPtr packet);

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int PathSize = 72, ModeSize = 64;
        private const int GET_TARGET_NAME = 2, GET_ADVANCED_COLOR_INFO = 9, SET_ADVANCED_COLOR_STATE = 10, GET_ADVANCED_COLOR_INFO_2 = 15, SET_HDR_STATE = 16;

        public static string LastError;

        /// <summary>The displays in use (one entry each), with whether they can show HDR and whether it's on.</summary>
        public static List<HdrDisplay> Displays()
        {
            var list = new List<HdrDisplay>();
            IntPtr paths = IntPtr.Zero, modes = IntPtr.Zero;
            try
            {
                uint np = 0, nm = 0;
                int err = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm);
                    if (err != 0) { LastError = "GetDisplayConfigBufferSizes " + err; return list; }
                    if (paths != IntPtr.Zero) Marshal.FreeHGlobal(paths);
                    if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes);
                    paths = Marshal.AllocHGlobal((int)Math.Max(1, np) * PathSize);
                    modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * ModeSize);
                    err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                    if (err != 122) break;   // ERROR_INSUFFICIENT_BUFFER: the displays changed in between, again
                }
                if (err != 0) { LastError = "QueryDisplayConfig " + err; return list; }
                var seen = new HashSet<string>();
                int n = 1;
                for (int i = 0; i < np; i++)
                {
                    int off = i * PathSize + 20;   // the path's target
                    var d = new HdrDisplay
                    {
                        AdapterLow = (uint)Marshal.ReadInt32(paths, off),
                        AdapterHigh = Marshal.ReadInt32(paths, off + 4),
                        TargetId = (uint)Marshal.ReadInt32(paths, off + 8)
                    };
                    if (!seen.Add(d.Key)) continue;   // (a mirrored display's second path)
                    d.Name = TargetName(d) ?? ("Display " + n);
                    n++;
                    ReadState(d);
                    list.Add(d);
                }
            }
            catch (Exception ex) { LastError = ex.Message; }
            finally
            {
                if (paths != IntPtr.Zero) Marshal.FreeHGlobal(paths);
                if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes);
            }
            return list;
        }

        private static IntPtr Packet(int type, int size, HdrDisplay d)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
            Marshal.WriteInt32(p, 0, type);
            Marshal.WriteInt32(p, 4, size);
            Marshal.WriteInt32(p, 8, (int)d.AdapterLow);
            Marshal.WriteInt32(p, 12, d.AdapterHigh);
            Marshal.WriteInt32(p, 16, (int)d.TargetId);
            return p;
        }

        private static string TargetName(HdrDisplay d)
        {
            IntPtr p = Packet(GET_TARGET_NAME, 420, d);
            try
            {
                if (DisplayConfigGetDeviceInfo(p) != 0) return null;
                string name = Marshal.PtrToStringUni(new IntPtr(p.ToInt64() + 36));
                uint tech = (uint)Marshal.ReadInt32(p, 24);
                if (string.IsNullOrWhiteSpace(name)) return tech == 0x80000000 ? "Built-in display" : null;
                return name.Trim();
            }
            catch (Exception) { return null; }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static void ReadState(HdrDisplay d)
        {
            // Windows 11 24H2 and later: its own HDR bits (an SDR display with colour management isn't "HDR")
            IntPtr p = Packet(GET_ADVANCED_COLOR_INFO_2, 36, d);
            try
            {
                if (DisplayConfigGetDeviceInfo(p) == 0)
                {
                    uint v = (uint)Marshal.ReadInt32(p, 20);
                    d.NewApi = true;
                    d.Supported = (v & 0x10) != 0;
                    d.On = (v & 0x20) != 0 || Marshal.ReadInt32(p, 32) == 2;
                    return;
                }
            }
            catch (Exception) { }
            finally { Marshal.FreeHGlobal(p); }

            p = Packet(GET_ADVANCED_COLOR_INFO, 32, d);
            try
            {
                if (DisplayConfigGetDeviceInfo(p) != 0) return;
                uint v = (uint)Marshal.ReadInt32(p, 20);
                bool wideColorEnforced = (v & 4) != 0;   // colour management on an SDR display: not HDR
                d.Supported = (v & 1) != 0 && !wideColorEnforced;
                d.On = d.Supported && (v & 2) != 0;
            }
            catch (Exception) { }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Turns HDR on or off for a display (the screen goes dark for a moment, as when it's switched in Windows). Returns whether it took.</summary>
        public static bool Set(HdrDisplay d, bool on)
        {
            LastError = null;
            int err = -1;
            if (d.NewApi)
            {
                IntPtr p = Packet(SET_HDR_STATE, 24, d);
                try { Marshal.WriteInt32(p, 20, on ? 1 : 0); err = DisplayConfigSetDeviceInfo(p); }
                finally { Marshal.FreeHGlobal(p); }
            }
            if (err != 0)
            {
                IntPtr p = Packet(SET_ADVANCED_COLOR_STATE, 24, d);
                try { Marshal.WriteInt32(p, 20, on ? 1 : 0); err = DisplayConfigSetDeviceInfo(p); }
                finally { Marshal.FreeHGlobal(p); }
            }
            if (err != 0) { LastError = "DisplayConfigSetDeviceInfo " + err; return false; }
            // what it is now (Windows can say yes and still not switch, e.g. a display that's asleep)
            for (int i = 0; i < 10; i++)
            {
                ReadState(d);
                if (d.On == on) return true;
                Thread.Sleep(150);
            }
            return d.On == on;
        }
    }

    // =====================================================================================================================

    internal class BtAudioDevice
    {
        public string Name;
        public bool Connected;
        public bool Headset;   // has a microphone (hands-free)
        internal string Key;
        internal readonly List<string> EndpointIds = new List<string>();
    }

    internal static class Bluetooth
    {
        public static string LastError;

        // ---------------- the radio (Windows.Devices.Radios, reached at run time) ----------------

        private static Type T(string name)
        {
            foreach (string winmd in new[] { "Windows.Devices", "Windows.System.Devices", "Windows" })
            {
                try { var t = Type.GetType(name + ", " + winmd + ", ContentType=WindowsRuntime", false); if (t != null) return t; }
                catch (Exception) { }
            }
            throw new TypeLoadException(name);
        }

        private static MethodInfo asTask1;

        private static object Await(object op, Type result)
        {
            if (asTask1 == null)
            {
                var asm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                var ext = asm.GetType("System.WindowsRuntimeSystemExtensions", true);
                foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "AsTask" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1") { asTask1 = m; break; }
            }
            var task = (Task)asTask1.MakeGenericMethod(result).Invoke(null, new[] { op });
            if (!task.Wait(8000)) throw new TimeoutException("Windows didn't answer");
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        private static readonly object radioLock = new object();

        private static object FindRadio()
        {
            var radioT = T("Windows.Devices.Radios.Radio");
            var accessT = T("Windows.Devices.Radios.RadioAccessStatus");
            try { Await(radioT.GetMethod("RequestAccessAsync").Invoke(null, null), accessT); } catch (Exception) { }
            var listT = typeof(IReadOnlyList<>).MakeGenericType(radioT);
            var radios = Await(radioT.GetMethod("GetRadiosAsync").Invoke(null, null), listT) as System.Collections.IEnumerable;
            if (radios == null) return null;
            foreach (object r in radios)
                if (Convert.ToInt32(radioT.GetProperty("Kind").GetValue(r, null)) == 3) return r;   // RadioKind.Bluetooth
            return null;
        }

        /// <summary>Bluetooth now: 1 on, 0 off, -1 no Bluetooth (or Windows wouldn't say). Takes a moment: not on the screen's thread.</summary>
        public static int RadioState()
        {
            lock (radioLock)
            {
                try
                {
                    object r = FindRadio();
                    if (r == null) { LastError = "no Bluetooth radio"; return -1; }
                    int s = Convert.ToInt32(r.GetType().GetProperty("State").GetValue(r, null));   // Unknown 0, On 1, Off 2, Disabled 3
                    return s == 1 ? 1 : s == 2 || s == 3 ? 0 : -1;
                }
                catch (Exception ex) { LastError = ex.GetBaseException().Message; return -1; }
            }
        }

        /// <summary>Turns Bluetooth on or off (as the switch in Windows' Quick settings). Not on the screen's thread.</summary>
        public static bool SetRadio(bool on)
        {
            lock (radioLock)
            {
                try
                {
                    object r = FindRadio();
                    if (r == null) { LastError = "no Bluetooth radio"; return false; }
                    var stateT = T("Windows.Devices.Radios.RadioState");
                    var accessT = T("Windows.Devices.Radios.RadioAccessStatus");
                    var res = Await(r.GetType().GetMethod("SetStateAsync").Invoke(r, new[] { Enum.ToObject(stateT, on ? 1 : 2) }), accessT);
                    int access = Convert.ToInt32(res);
                    if (access != 1) { LastError = "Windows said no (" + res + ")"; return false; }
                    return true;
                }
                catch (Exception ex) { LastError = ex.GetBaseException().Message; return false; }
            }
        }

        // ---------------- paired headphones and speakers (Core Audio + the Bluetooth audio driver) ----------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropKey key);
            [PreserveSig] int GetValue(ref PropKey key, out PropVar value);
        }

        [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDeviceTopology
        {
            [PreserveSig] int GetConnectorCount(out uint count);
            [PreserveSig] int GetConnector(uint index, out IConnector connector);
            [PreserveSig] int GetSubunitCount(out uint count);
            [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
            [PreserveSig] int GetPartById(uint id, out IntPtr part);
            [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSignalPath(IntPtr from, IntPtr to, int rejectMixedPaths, out IntPtr parts);
        }

        [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IConnector
        {
            [PreserveSig] int GetType(out int type);
            [PreserveSig] int GetDataFlow(out int flow);
            [PreserveSig] int ConnectTo(IConnector to);
            [PreserveSig] int Disconnect();
            [PreserveSig] int IsConnected(out int connected);
            [PreserveSig] int GetConnectedTo(out IConnector to);
            [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPart
        {
            [PreserveSig] int GetName(out IntPtr name);
            [PreserveSig] int GetLocalId(out uint id);
            [PreserveSig] int GetGlobalId(out IntPtr id);
            [PreserveSig] int GetPartType(out int type);
            [PreserveSig] int GetSubType(out Guid subType);
            [PreserveSig] int GetControlInterfaceCount(out uint count);
            [PreserveSig] int GetControlInterface(uint index, out IntPtr control);
            [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
            [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
            [PreserveSig] int GetTopologyObject(out IntPtr topology);
            [PreserveSig] int Activate(int clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IKsControl
        {
            [PreserveSig] int KsProperty(IntPtr property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
            [PreserveSig] int KsMethod(IntPtr method, uint methodLength, IntPtr data, uint dataLength, out uint bytesReturned);
            [PreserveSig] int KsEvent(IntPtr evt, uint eventLength, IntPtr data, uint dataLength, out uint bytesReturned);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropKey
        {
            public Guid fmtid;
            public int pid;
            public PropKey(string g, int p) { fmtid = new Guid(g); pid = p; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVar
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr p;
            public IntPtr p2;
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVar pv);

        private static readonly Guid IID_DeviceTopology = new Guid("2A07407E-6497-4A18-9787-32F79BD0D98F");
        private static readonly Guid IID_KsControl = new Guid("28F54685-06FD-11D2-B27A-00A0C9223196");
        private static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        private const int ONESHOT_RECONNECT = 0, ONESHOT_DISCONNECT = 1;

        private static readonly PropKey PKEY_InterfaceName = new PropKey("026e516e-b814-414b-83cd-856d6fef4822", 2);   // DEVPKEY_DeviceInterface_FriendlyName
        private static readonly PropKey PKEY_FriendlyName = new PropKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        private static readonly PropKey PKEY_ContainerId = new PropKey("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);

        private static string ReadString(IPropertyStore ps, PropKey key)
        {
            PropVar v;
            if (ps.GetValue(ref key, out v) != 0) return null;
            try { return v.vt == 31 && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : null; }
            finally { PropVariantClear(ref v); }
        }

        private static string ReadGuid(IPropertyStore ps, PropKey key)
        {
            PropVar v;
            if (ps.GetValue(ref key, out v) != 0) return null;
            try { return v.vt == 72 && v.p != IntPtr.Zero ? ((Guid)Marshal.PtrToStructure(v.p, typeof(Guid))).ToString() : null; }
            finally { PropVariantClear(ref v); }
        }

        /// <summary>The Bluetooth audio driver's control for an endpoint, when the endpoint is a Bluetooth one (null otherwise).</summary>
        private static IKsControl BtControl(IMMDevice dev)
        {
            object o;
            Guid iid = IID_DeviceTopology;
            if (dev.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0 || o == null) return null;
            var topo = o as IDeviceTopology;
            if (topo == null) return null;
            IConnector conn;
            if (topo.GetConnector(0, out conn) != 0 || conn == null) return null;
            string other;
            if (conn.GetDeviceIdConnectedTo(out other) != 0 || other == null) return null;
            string lo = other.ToLowerInvariant();
            if (lo.IndexOf("bthenum", StringComparison.Ordinal) < 0 && lo.IndexOf("bthhfenum", StringComparison.Ordinal) < 0) return null;
            IConnector to;
            if (conn.GetConnectedTo(out to) != 0 || to == null) return null;
            var part = to as IPart;
            if (part == null) return null;
            object ks;
            Guid kiid = IID_KsControl;
            if (part.Activate(1 /* CLSCTX_INPROC_SERVER */, ref kiid, out ks) != 0) return null;
            return ks as IKsControl;
        }

        /// <summary>Paired Bluetooth headphones, headsets and speakers (one entry each), and whether they're connected. Not on the screen's thread.</summary>
        public static List<BtAudioDevice> AudioDevices()
        {
            var byKey = new Dictionary<string, BtAudioDevice>();
            var order = new List<BtAudioDevice>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                // both directions: a headset's microphone half is its own endpoint (and connecting it matters too)
                foreach (int flow in new[] { 0, 1 })
                {
                    IMMDeviceCollection col;
                    if (en.EnumAudioEndpoints(flow, 0x1 | 0x8 /* active, unplugged */, out col) != 0 || col == null) continue;
                    int count;
                    col.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            IMMDevice dev;
                            if (col.Item(i, out dev) != 0 || dev == null) continue;
                            if (BtControl(dev) == null) continue;
                            string id; dev.GetId(out id);
                            int state; dev.GetState(out state);
                            IPropertyStore ps;
                            if (dev.OpenPropertyStore(0, out ps) != 0 || ps == null) continue;
                            string name = ReadString(ps, PKEY_InterfaceName) ?? ReadString(ps, PKEY_FriendlyName) ?? "Bluetooth device";
                            string key = ReadGuid(ps, PKEY_ContainerId) ?? CleanName(name).ToLowerInvariant();
                            BtAudioDevice d;
                            if (!byKey.TryGetValue(key, out d)) { d = new BtAudioDevice { Key = key, Name = CleanName(name) }; byKey[key] = d; order.Add(d); }
                            else if (CleanName(name).Length < d.Name.Length) d.Name = CleanName(name);
                            if (state == 1) d.Connected = true;
                            if (flow == 1 || name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0) d.Headset = true;
                            d.EndpointIds.Add(id);
                        }
                        catch (Exception) { }
                    }
                }
                LastError = null;
            }
            catch (Exception ex) { LastError = ex.Message; }
            return order.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
        }

        private static string CleanName(string n)
        {
            foreach (string tail in new[] { " Hands-Free AG Audio", " Hands-Free AG", " Hands-Free", " Stereo", " Avrcp Transport" })
                if (n.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return n.Substring(0, n.Length - tail.Length).Trim();
            return n.Trim();
        }

        /// <summary>Connects (or disconnects) paired headphones / a speaker, as Windows' own Connect button. Not on the screen's thread.</summary>
        public static bool Connect(BtAudioDevice d, bool connect)
        {
            int ok = 0;
            IntPtr prop = Marshal.AllocHGlobal(24);
            try
            {
                byte[] g = KSPROPSETID_BtAudio.ToByteArray();
                Marshal.Copy(g, 0, prop, 16);
                Marshal.WriteInt32(prop, 16, connect ? ONESHOT_RECONNECT : ONESHOT_DISCONNECT);
                Marshal.WriteInt32(prop, 20, 1);   // KSPROPERTY_TYPE_GET
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                foreach (string id in d.EndpointIds)
                {
                    try
                    {
                        IMMDevice dev;
                        if (en.GetDevice(id, out dev) != 0 || dev == null) continue;
                        var ks = BtControl(dev);
                        if (ks == null) continue;
                        uint ret;
                        int hr = ks.KsProperty(prop, 24, IntPtr.Zero, 0, out ret);
                        if (hr >= 0) ok++;
                        else LastError = "the Bluetooth driver said 0x" + hr.ToString("X8");
                    }
                    catch (Exception ex) { LastError = ex.Message; }
                }
            }
            finally { Marshal.FreeHGlobal(prop); }
            return ok > 0;
        }

        /// <summary>Waits (up to <paramref name="seconds"/>) for the device to show as connected / disconnected. Not on the screen's thread.</summary>
        public static bool WaitFor(BtAudioDevice d, bool connected, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                var now = AudioDevices().FirstOrDefault(x => x.Key == d.Key);
                if (now != null && now.Connected == connected) { d.Connected = connected; return true; }
                Thread.Sleep(500);
            }
            return false;
        }
    }
}

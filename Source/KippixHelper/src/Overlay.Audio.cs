using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace KippixOverlay
{
    internal class AudioDevice
    {
        public string Id;
        public string Name;
        public string Description;
        public int FormFactor;     // 3/5 headphones/headset, 9 HDMI display, 8 S/PDIF, 1 speakers
        public bool IsDefault;
    }

    /// <summary>Windows Core Audio: output devices, default device switching, master and per-app volume.</summary>
    internal static class Audio
    {
        // ---------------- COM interfaces ----------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

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
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr id, out IntPtr format);
            [PreserveSig] int GetDeviceFormat(IntPtr id, int def, out IntPtr format);
            [PreserveSig] int ResetDeviceFormat(IntPtr id);
            [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(IntPtr id, int def, out long defaultPeriod, out long minPeriod);
            [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
            [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
            [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
            [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
            public PropertyKey(string g, int p) { fmtid = new Guid(g); pid = p; }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr ptr;
            [FieldOffset(8)] public int intValue;
            [FieldOffset(16)] public IntPtr pad;
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

        private static readonly PropertyKey PKEY_FriendlyName = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        private static readonly PropertyKey PKEY_DeviceDesc = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        private static readonly PropertyKey PKEY_InterfaceName = new PropertyKey("026e516e-b814-414b-83cd-856d6fef4822", 2);
        private static readonly PropertyKey PKEY_FormFactor = new PropertyKey("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);

        private static Guid IID_EndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static Guid IID_SessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static Guid Ctx = Guid.Empty;

        private const int eRender = 0;
        private const int DEVICE_STATE_ACTIVE = 1;

        // (0.99.71) one per thread: the overlay's lists are made on its own screen's thread, some look-ups on worker threads,
        // and one Windows sound object shared between them stopped answering on the others (a dead thread's, or one that
        // can't be reached from another kind of thread): "No output devices found", every volume "not playing"
        [ThreadStatic] private static IMMDeviceEnumerator enumerator;
        /// <summary>(0.99.71) What went wrong the last time a look-up failed (for the log), or null.</summary>
        internal static string LastError;

        private static IMMDeviceEnumerator Enumerator
        {
            get
            {
                if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                return enumerator;
            }
        }

        /// <summary>The sound object made again (one that stopped answering: a device came or went, Windows' audio restarted).</summary>
        private static void ResetEnumerator()
        {
            var e = enumerator;
            enumerator = null;
            try { if (e != null) Marshal.ReleaseComObject(e); } catch (Exception) { }
        }

        private static string GetString(IPropertyStore store, PropertyKey key)
        {
            PropVariant v;
            if (store.GetValue(ref key, out v) != 0) return null;
            try { return v.vt == 31 ? Marshal.PtrToStringUni(v.ptr) : null; }
            finally { PropVariantClear(ref v); }
        }

        private static int GetInt(IPropertyStore store, PropertyKey key)
        {
            PropVariant v;
            if (store.GetValue(ref key, out v) != 0) return -1;
            try { return v.vt == 19 || v.vt == 3 ? v.intValue : -1; }
            finally { PropVariantClear(ref v); }
        }

        private static IMMDevice DefaultDevice()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    IMMDevice dev;
                    if (Enumerator.GetDefaultAudioEndpoint(eRender, 1, out dev) == 0 && dev != null) return dev;
                }
                catch (Exception ex) { LastError = "default device: " + ex.GetType().Name + " " + ex.Message; }
                ResetEnumerator();   // (and once more with a new one)
            }
            return null;
        }

        private static string DefaultId()
        {
            IMMDevice dev = DefaultDevice();
            if (dev == null) return null;
            string id;
            return dev.GetId(out id) == 0 ? id : null;
        }

        public static List<AudioDevice> OutputDevices()
        {
            var list = OutputDevicesOnce();
            if (list.Count == 0) { ResetEnumerator(); list = OutputDevicesOnce(); }   // (0.99.71: a sound object that stopped answering: once more)
            return list;
        }

        private static List<AudioDevice> OutputDevicesOnce()
        {
            var list = new List<AudioDevice>();
            try
            {
                string def = DefaultId();
                IMMDeviceCollection col;
                if (Enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col) != 0) return list;
                int count;
                col.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) != 0) continue;
                    var d = new AudioDevice();
                    dev.GetId(out d.Id);
                    IPropertyStore store;
                    if (dev.OpenPropertyStore(0, out store) == 0)
                    {
                        string iface = GetString(store, PKEY_InterfaceName);
                        string desc = GetString(store, PKEY_DeviceDesc);
                        string friendly = GetString(store, PKEY_FriendlyName);
                        d.Name = !string.IsNullOrEmpty(iface) ? iface : (friendly ?? desc ?? "Audio device");
                        d.Description = desc;
                        d.FormFactor = GetInt(store, PKEY_FormFactor);
                    }
                    d.IsDefault = d.Id == def;
                    list.Add(d);
                }

                // Two outputs on one card share a name; tell them apart with the endpoint description.
                foreach (var d in list)
                {
                    int same = 0;
                    foreach (var o in list) if (o.Name == d.Name) same++;
                    if (same > 1 && !string.IsNullOrEmpty(d.Description)) d.Name = d.Description + " (" + d.Name + ")";
                }
            }
            catch (Exception ex) { LastError = "devices: " + ex.GetType().Name + " " + ex.Message; }
            return list;
        }

        public static bool SetDefault(string id)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                bool ok = true;
                for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) == 0;
                Marshal.ReleaseComObject(policy);
                return ok;
            }
            catch (Exception) { return false; }
        }

        // ---------------- Master volume ----------------

        private static IAudioEndpointVolume EndpointVolume()
        {
            IMMDevice dev = DefaultDevice();
            if (dev == null) return null;
            object o;
            if (dev.Activate(ref IID_EndpointVolume, 23, IntPtr.Zero, out o) != 0) return null;
            return o as IAudioEndpointVolume;
        }

        /// <summary>0..1, or -1 if unavailable.</summary>
        public static float GetMaster(out bool muted)
        {
            muted = false;
            try
            {
                var v = EndpointVolume();
                if (v == null) return -1;
                float level;
                v.GetMasterVolumeLevelScalar(out level);
                v.GetMute(out muted);
                return level;
            }
            catch (Exception) { return -1; }
        }

        public static void SetMaster(float level)
        {
            try
            {
                var v = EndpointVolume();
                if (v == null) return;
                v.SetMasterVolumeLevelScalar(Clamp(level), ref Ctx);
                if (level > 0) v.SetMute(false, ref Ctx);
            }
            catch (Exception) { }
        }

        public static void ToggleMasterMute()
        {
            try
            {
                var v = EndpointVolume();
                if (v == null) return;
                bool m;
                v.GetMute(out m);
                v.SetMute(!m, ref Ctx);
            }
            catch (Exception) { }
        }

        // ---------------- Per-app volume ----------------

        private static List<ISimpleAudioVolume> Sessions(Func<uint, bool> match)
        {
            var result = new List<ISimpleAudioVolume>();
            try
            {
                IMMDevice dev = DefaultDevice();
                if (dev == null) return result;
                object o;
                if (dev.Activate(ref IID_SessionManager2, 23, IntPtr.Zero, out o) != 0) return result;
                var mgr = (IAudioSessionManager2)o;
                IAudioSessionEnumerator en;
                if (mgr.GetSessionEnumerator(out en) != 0) return result;
                int count;
                en.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 s;
                    if (en.GetSession(i, out s) != 0 || s == null) continue;
                    uint pid;
                    if (s.GetProcessId(out pid) != 0 || pid == 0) continue;
                    if (!match(pid)) continue;
                    var vol = s as ISimpleAudioVolume;
                    if (vol != null) result.Add(vol);
                }
            }
            catch (Exception) { }
            return result;
        }

        /// <summary>Volume 0..1 of the first matching app, or -1 when it isn't playing sound.</summary>
        public static float GetAppVolume(Func<uint, bool> match, out bool muted)
        {
            muted = false;
            foreach (var v in Sessions(match))
            {
                float level;
                if (v.GetMasterVolume(out level) == 0)
                {
                    v.GetMute(out muted);
                    return level;
                }
            }
            return -1;
        }

        public static void SetAppVolume(Func<uint, bool> match, float level)
        {
            foreach (var v in Sessions(match))
            {
                v.SetMasterVolume(Clamp(level), ref Ctx);
                if (level > 0) v.SetMute(false, ref Ctx);
            }
        }

        public static void ToggleAppMute(Func<uint, bool> match)
        {
            var list = Sessions(match);
            if (list.Count == 0) return;
            bool m;
            list[0].GetMute(out m);
            foreach (var v in list) v.SetMute(!m, ref Ctx);
        }

        private static float Clamp(float v)
        {
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }

        // ---------------- Process matching helpers ----------------

        private static readonly Dictionary<uint, string> pathCache = new Dictionary<uint, string>();

        public static string ProcessPath(uint pid)
        {
            string p;
            lock (pathCache)
            {
                if (pathCache.TryGetValue(pid, out p)) return p;
            }
            p = Native.GetProcessPath(pid) ?? "";
            lock (pathCache)
            {
                if (pathCache.Count > 500) pathCache.Clear();
                pathCache[pid] = p;
            }
            return p;
        }

        public static bool IsExeNamed(uint pid, string exeName)
        {
            string p = ProcessPath(pid);
            return p.Length > 0 && string.Equals(Path.GetFileName(p), exeName, StringComparison.OrdinalIgnoreCase);
        }
    }
}

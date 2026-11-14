using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace KippixHelper
{
    /// <summary>
    /// Windows' own thumbnails (photos and videos, from its thumbnail cache), video length / picture size, and the Recycle Bin.
    /// The shell calls run on one STA thread of our own, which ends when it has had nothing to do for a while.
    /// </summary>
    internal static class ShellMedia
    {
        // ---------------- the worker thread ----------------

        private static readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
        private static Thread worker;
        private static readonly object workerLock = new object();

        /// <summary>Runs <paramref name="job"/> on the shell thread (in order).</summary>
        public static void Run(Action job)
        {
            jobs.Add(job);
            lock (workerLock)
            {
                if (worker != null && worker.IsAlive) return;
                worker = new Thread(Work) { IsBackground = true, Name = "Kippix shell", Priority = ThreadPriority.BelowNormal };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
        }

        private static void Work()
        {
            while (true)
            {
                Action job;
                if (!jobs.TryTake(out job, 30000))
                {
                    lock (workerLock)
                    {
                        if (jobs.Count == 0) { worker = null; return; }   // nothing for 30 s: stop
                    }
                    continue;
                }
                try { job(); } catch (Exception) { }
            }
        }

        // ---------------- thumbnails ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid,
                                                              [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;

        /// <summary>Windows' thumbnail of a photo or video, about <paramref name="width"/> wide (call on the shell thread). Null when there isn't one.</summary>
        public static BitmapSource Thumbnail(string path, int width)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f) != 0 || f == null) return null;
                var size = new SIZE { cx = width, cy = width };
                if (f.GetImage(size, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                BitmapSource result = src;
                if (src.PixelWidth > width * 1.3)
                {
                    double scale = (double)width / src.PixelWidth;
                    result = new WriteableBitmap(new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale)));   // a copy: the big one can go
                }
                result.Freeze();
                return result;
            }
            catch (Exception) { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (f != null) try { Marshal.ReleaseComObject(f); } catch (Exception) { }
            }
        }

        // ---------------- properties: video length and size ----------------

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint ulVal;
            [FieldOffset(8)] public ulong uhVal;
            [FieldOffset(8)] public IntPtr p;
            [FieldOffset(16)] public IntPtr p2;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHGetPropertyStoreFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, int flags, ref Guid riid,
                                                                    [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);

        private static readonly Guid MediaFmt = new Guid("64440490-4C8B-11D1-8B70-080036B11A03");   // PKEY_Media_Duration (3)
        private static readonly Guid VideoFmt = new Guid("64440491-4C8B-11D1-8B70-080036B11A03");   // PKEY_Video_FrameWidth (3) / FrameHeight (4)
        private static readonly Guid ImageFmt = new Guid("6444048F-4C8B-11D1-8B70-080036B11A03");   // PKEY_Image_HorizontalSize (3) / VerticalSize (4)

        /// <summary>A video's length and size, or a picture's size (call on the shell thread). Zero for what Windows doesn't know.</summary>
        public static void Details(string path, bool video, out int width, out int height, out TimeSpan length)
        {
            width = height = 0;
            length = TimeSpan.Zero;
            if (!video)
            {
                // a picture's header is enough (fast, and doesn't need Windows' property handlers)
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                        if (dec.Frames.Count > 0) { width = dec.Frames[0].PixelWidth; height = dec.Frames[0].PixelHeight; }
                    }
                    if (width > 0) return;
                }
                catch (Exception) { }
            }
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store) != 0 || store == null) return;
                if (video)
                {
                    ulong d = ReadNumber(store, MediaFmt, 3);
                    if (d > 0) length = TimeSpan.FromTicks((long)d);
                    width = (int)ReadNumber(store, VideoFmt, 3);
                    height = (int)ReadNumber(store, VideoFmt, 4);
                }
                else
                {
                    width = (int)ReadNumber(store, ImageFmt, 3);
                    height = (int)ReadNumber(store, ImageFmt, 4);
                }
            }
            catch (Exception) { }
            finally { if (store != null) try { Marshal.ReleaseComObject(store); } catch (Exception) { } }
        }

        private static ulong ReadNumber(IPropertyStore store, Guid fmt, uint pid)
        {
            var key = new PROPERTYKEY { fmtid = fmt, pid = pid };
            PROPVARIANT v;
            if (store.GetValue(ref key, out v) != 0) return 0;
            try
            {
                switch (v.vt)
                {
                    case 19: return v.ulVal;       // VT_UI4
                    case 21: return v.uhVal;       // VT_UI8
                    case 3: return v.ulVal;        // VT_I4
                    case 20: return v.uhVal;       // VT_I8
                }
                return 0;
            }
            finally { PropVariantClear(ref v); }
        }

        // ---------------- Recycle Bin ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")] private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")] private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>
        /// Moves files to the Recycle Bin (no questions, no progress window). True when it worked. Files on drives without a Recycle Bin
        /// (USB sticks, network folders) are left alone (<paramref name="skipped"/>); Windows still warns before destroying one too big for the bin.
        /// </summary>
        public static bool Recycle(IEnumerable<string> files, out int skipped)
        {
            skipped = 0;
            var list = new List<string>();
            foreach (var f in files.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)))
            {
                bool fixedDrive = false;
                try { fixedDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(f))).DriveType == DriveType.Fixed; } catch (Exception) { }
                if (fixedDrive) list.Add(f); else skipped++;
            }
            if (list.Count == 0) return skipped == 0;
            string from = string.Join("\0", list) + "\0\0";
            ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING;
            try
            {
                int r;
                if (IntPtr.Size == 8)
                {
                    var op = new SHFILEOPSTRUCT64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                    r = SHFileOperation64(ref op);
                    if (op.fAnyOperationsAborted != 0) return false;
                }
                else
                {
                    var op = new SHFILEOPSTRUCT32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
                    r = SHFileOperation32(ref op);
                    if (op.fAnyOperationsAborted != 0) return false;
                }
                return r == 0 && list.All(f => !File.Exists(f));
            }
            catch (Exception) { return false; }
        }
    }
}

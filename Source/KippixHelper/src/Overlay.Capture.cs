using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace KippixOverlay
{
    internal static class Capture
    {
        /// <summary>Monitor rectangle in physical pixels.</summary>
        public static Rectangle MonitorBounds(IntPtr hwnd)
        {
            Screen s = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.PrimaryScreen;
            return s.Bounds;
        }

        public static double DpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
            }
            catch (Exception) { return 1.0; }
        }

        /// <summary>(0.99.20) The monitor the game is on, as it is this moment (kept in memory; null if it couldn't be read).</summary>
        public static Bitmap GrabMonitor(IntPtr gameHwnd)
        {
            try
            {
                Rectangle b = MonitorBounds(gameHwnd);
                var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch (Exception) { return null; }
        }

        /// <summary>(0.99.20) A picture already taken, saved as "folder\Game name 2026-10-16 09-30-00.png".</summary>
        public static string SaveBitmap(Bitmap bmp, string folder, string gameName, DateTime when)
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, SafeName(gameName) + " " + when.ToString("yyyy-MM-dd HH-mm-ss") + ".png");
            bmp.Save(file, ImageFormat.Png);
            return file;
        }

        /// <summary>Captures the monitor the game is on to "folder\Game name 2026-10-16 09-30-00.png".</summary>
        public static string SaveScreenshot(IntPtr gameHwnd, string folder, string gameName)
        {
            Rectangle b = MonitorBounds(gameHwnd);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, SafeName(gameName) + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png");

            using (var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                }
                bmp.Save(file, ImageFormat.Png);
            }
            return file;
        }

        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Game";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }

        public static string DriveSize(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1000) return (gb / 1024).ToString("0.#") + " TB";
            return Math.Round(gb) + " GB";
        }
    }
}

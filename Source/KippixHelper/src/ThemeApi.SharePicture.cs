using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.6) The picture an upload takes of the home screen. It was WPF drawing the window into a picture, which leaves out
    /// what the graphics card draws itself (a playing video, some effects) and turns anything see-through black, so it could
    /// come out black ("the picture Kippix took of my home screen came out black"). Now it's the screen itself, as you see it
    /// (Playnite is in front and its pages are closed by then), scaled to 1280 wide; only when that fails or comes out black
    /// is it drawn by WPF, over the window's own background colour so nothing see-through goes black.
    /// </summary>
    public partial class ThemeApi
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out ShotRect r);
        [StructLayout(LayoutKind.Sequential)] private struct ShotRect { public int Left, Top, Right, Bottom; }

        private bool SaveHomePicture(string path)
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            if (win == null) return false;
            try { if (SaveScreenPicture(win, path)) return true; }
            catch (Exception ex) { logger.Info("Kippix: upload picture from the screen: " + ex.Message); }
            try { return SaveDrawnPicture(win, path); }
            catch (Exception ex) { logger.Warn("Kippix: upload picture: " + ex.Message); return false; }
        }

        /// <summary>The window's area of the screen, as it looks. False when it can't, or it's (nearly) all black.</summary>
        private bool SaveScreenPicture(Window win, string path)
        {
            IntPtr h = new WindowInteropHelper(win).Handle;
            ShotRect r;
            if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return false;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 64 || ht < 64) return false;
            using (var full = new System.Drawing.Bitmap(w, ht, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(full)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, ht));
                if (MostlyBlack(full)) { logger.Info("Kippix: upload picture: the screen came out black, drawing it instead"); return false; }
                double scale = Math.Min(1.0, 1280.0 / w);
                int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(ht * scale));
                using (var small = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(full, 0, 0, sw, sh);
                    }
                    var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    if (jpeg == null) { small.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg); return true; }
                    var ps = new System.Drawing.Imaging.EncoderParameters(1);
                    ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                    small.Save(path, jpeg, ps);
                }
            }
            return true;
        }

        private static bool MostlyBlack(System.Drawing.Bitmap b)
        {
            long sum = 0; int n = 0;
            for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 40))
                for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 40))
                {
                    var c = b.GetPixel(x, y);
                    sum += c.R + c.G + c.B; n++;
                }
            return n == 0 || sum / (3.0 * n) < 4;
        }

        /// <summary>WPF draws the window (as before), over its background colour.</summary>
        private bool SaveDrawnPicture(Window win, string path)
        {
            var root = win.Content as FrameworkElement;
            if (root == null || root.ActualWidth <= 0) return false;
            double scale = Math.Min(1.0, 1280.0 / root.ActualWidth);
            int pw = (int)(root.ActualWidth * scale), ph = (int)(root.ActualHeight * scale);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var bg = win.Background as SolidColorBrush;
                dc.DrawRectangle(bg != null && bg.Color.A == 255 ? bg : Brushes.Black, null, new Rect(0, 0, pw, ph));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            return true;
        }
    }
}

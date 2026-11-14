using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.54) The gallery viewer's tidied controls: the highlighted button's name under the bar (GalleryFocusLabel), and
    /// "Save this frame" on a video: the picture showing now saved as a PNG next to the video, as a photo of the same game.
    /// </summary>
    public partial class ThemeApi
    {
        private string galleryFocusLabel = "";
        private bool galleryLabelHooked;

        public string GalleryFocusLabel { get { return galleryFocusLabel; } }
        public ICommand GallerySaveFrameCommand { get; private set; }

        private void InitGalleryFrame()
        {
            GallerySaveFrameCommand = new RelayCommand(_ => SaveGalleryFrame());
        }

        /// <summary>Once: the bar's buttons say what they are when highlighted.</summary>
        private void HookGalleryLabel()
        {
            if (galleryLabelHooked) return;
            var bar = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryControls") as UIElement;
            if (bar == null) return;
            galleryLabelHooked = true;
            bar.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                var fe = e.NewFocus as FrameworkElement;
                string t = fe != null ? fe.ToolTip as string : null;
                SetGalleryFocusLabel(t ?? "");
            }), true);
            bar.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                var to = e.NewFocus as DependencyObject;
                if (to == null || !bar.IsAncestorOf(to)) SetGalleryFocusLabel("");
            }), true);
        }

        private void SetGalleryFocusLabel(string t)
        {
            if (t == galleryFocusLabel) return;
            galleryFocusLabel = t;
            Notify("GalleryFocusLabel");
        }

        private void SaveGalleryFrame()
        {
            var f = Current;
            var v = GalleryVideoElement();
            if (f == null || !f.IsVideo || v == null) return;
            try
            {
                int w = v.NaturalVideoWidth, h = v.NaturalVideoHeight;
                if (w <= 0 || h <= 0) { w = (int)v.ActualWidth; h = (int)v.ActualHeight; }
                if (w <= 0 || h <= 0) { PlaySound("back"); return; }
                double pos = 0;
                try { pos = v.Position.TotalSeconds; } catch (Exception) { }
                // the video's own picture (its drawn bounds are the video, without the black bars), at the video's size
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(v) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string dir = Path.GetDirectoryName(f.Path);
                string stem = Path.GetFileNameWithoutExtension(f.Path);
                int sec = (int)pos;
                string file = FreeName(Path.Combine(dir, stem + " frame " + (sec / 60) + "m" + (sec % 60).ToString("00") + "s.png"));
                using (var fs = File.Create(file)) enc.Save(fs);
                // a photo of the same game
                if (f.GameId != Guid.Empty)
                {
                    var g = api.Database.Games.Get(f.GameId);
                    if (g != null) RecordCapture(file, g);
                }
                var gf = new GalleryFile { Path = file, IsVideo = false, Date = DateTime.Now, Size = new FileInfo(file).Length, GameId = f.GameId, GameName = f.GameName };
                galleryFiles.Add(gf);
                int at = galleryList.IndexOf(f);
                galleryList.Insert(at >= 0 ? at + 1 : galleryList.Count, gf);   // (0.99.56: right after the video, so RB reaches it)
                galleryRebuildOnClose = true;
                galleryChanged = true;
                PlaySound("select");
                ShowGalleryStatus(Loc("LOC_KX_FrameSaved", "This frame is saved as a photo"));
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: saving a video frame: " + ex.Message);
                ShowGalleryStatus(Loc("LOC_KX_FrameFailed", "That frame couldn't be saved"));
            }
        }
    }
}

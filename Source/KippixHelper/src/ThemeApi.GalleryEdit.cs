using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.59) The gallery viewer, tidied: its bar is only previous, play / sound (a clip), Edit, favourite, More and next;
    /// the rest (slideshow, save a frame, volume, background, cover, details, delete) is in the More menu.
    ///
    /// Edit, full screen over the viewer (built in code, takes every button while it's up):
    ///   a photo: Crop (a frame to place: D-pad moves it, LB / RB smaller / bigger, Y its shape), Rotate, Filters (None, Vivid,
    ///   Warm, Cool, Black and white, Sepia, Faded, Punchy), Adjust (brightness, contrast, saturation), Save a copy;
    ///   a clip: Trim (its start and end: Up / Down pick which, Left / Right move it a second, LB / RB a tenth), Save a frame,
    ///   Save a trimmed copy (Windows' own video editing, the one Photos uses: nothing to install).
    /// The original is never changed: the edit is saved next to it ("… edit.png", "… trim.mp4") and goes with the same game.
    /// Left / Right choose a tool, A uses it, B goes back (and closes Edit, the changes not saved), X plays / pauses a clip.
    /// </summary>
    public partial class ThemeApi
    {
        // =====================================================================
        // The viewer's More menu
        // =====================================================================

        private bool galleryMoreOpen;
        public bool GalleryMoreOpen { get { return galleryMoreOpen; } }
        public ICommand GalleryMoreCommand { get; private set; }
        public ICommand GalleryEditCommand { get; private set; }

        public string GalleryMoreSlideshowText { get { return gallerySlideshow ? Loc("LOC_KX_SlideshowStop", "Stop the slideshow") : Loc("LOC_KX_Slideshow", "Slideshow"); } }
        public string GalleryMoreBackgroundText { get { return GalleryViewerIsBackground ? Loc("LOC_KX_UndoBackground", "Undo background") : Loc("LOC_KX_SetBackground", "Use as the game's background"); } }
        public string GalleryMoreCoverText { get { return GalleryViewerIsCover ? Loc("LOC_KX_UndoCover", "Undo cover") : Loc("LOC_KX_SetCover", "Use as the game's cover"); } }

        private void InitGalleryEdit()
        {
            GalleryMoreCommand = new RelayCommand(p => GalleryMore(p as string));
            GalleryEditCommand = new RelayCommand(_ => OpenGalleryEditor());
        }

        private void SetGalleryMore(bool open)
        {
            if (galleryMoreOpen == open) return;
            galleryMoreOpen = open;
            foreach (string n in new[] { "GalleryMoreOpen", "GalleryMoreSlideshowText", "GalleryMoreBackgroundText", "GalleryMoreCoverText" }) Notify(n);
            if (open) { ShowGalleryControls(false); ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_GalleryMoreFirst")), DispatcherPriority.ContextIdle); }
            else ui.BeginInvoke(new Action(() => UiTools.FocusByNameIfVisible("KX_GalleryMoreButton")), DispatcherPriority.ContextIdle);
        }

        private void GalleryMore(string what)
        {
            switch (what)
            {
                case "open": PlaySound(galleryMoreOpen ? "back" : "select"); SetGalleryMore(!galleryMoreOpen); return;
                case "slideshow": SetGalleryMore(false); SetSlideshow(!gallerySlideshow); return;
                case "frame": SetGalleryMore(false); SaveGalleryFrame(); return;
                case "volume":
                    {
                        // 100 → 80 → 60 → 40 → 20 → off → 100 (the row stays open)
                        double v = galleryMuted ? 0 : galleryVolume;
                        double next = v >= 0.95 ? 0.8 : v >= 0.75 ? 0.6 : v >= 0.55 ? 0.4 : v >= 0.35 ? 0.2 : v >= 0.15 ? 0 : 1.0;
                        SetGalleryVolume(next);
                        Notify("GalleryVolumeText");
                        return;
                    }
                case "background": SetGalleryMore(false); SetGalleryArt(true); return;
                case "cover": SetGalleryMore(false); SetGalleryArt(false); return;
                case "details": SetGalleryMore(false); ToggleGalleryDetails(); return;
                case "delete": SetGalleryMore(false); DeleteGalleryCurrent(); return;
            }
        }

        private bool FocusInMoreMenu()
        {
            var menu = UiTools.FindByName(Application.Current.MainWindow, "KX_GalleryMoreMenu");
            var f = Keyboard.FocusedElement as DependencyObject;
            return menu != null && f != null && menu.IsAncestorOf(f);
        }

        // =====================================================================
        // Edit
        // =====================================================================

        private bool galleryEditOpen;
        private Grid geLayer, gePreviewHost;
        private Image gePreview;
        private Canvas geCropLayer;
        private readonly Rectangle[] geShade = new Rectangle[4];
        private Rectangle geFrame;
        private MediaElement geVideo;
        private StackPanel geTools, geOptions;
        private TextBlock geTitle, geSub, geHint, geStatus;
        private Border geTrimStart, geTrimEnd, geTrimFill, geTrimHead;
        private Grid geTrimBar;
        private DispatcherTimer geTick;
        private DateTime geOpenedAt, gePadAAt, gePadBAt;
        private bool geBusy;

        private GalleryFile geFile;
        private bool geIsVideo;
        private List<string> geToolKeys = new List<string>();
        private int geTool;
        private string geMode = "tools";   // tools, crop, filter, adjust, trim

        // a photo
        private BitmapSource geSrc;          // read at the screen's size
        private int geRot;                   // quarter turns
        private int geFilter, geAdjustRow;
        private int geBright, geContrast, geSat;
        private double geCx = 0.5, geCy = 0.5, geCs = 1;
        private int geRatio;                 // 0 the picture's own shape
        private byte[] geRotPixels;          // the turned picture, Bgra32
        private int geRotW, geRotH;
        private BitmapSource geFiltered;     // the turned picture with the filter and adjustments
        private static readonly double[] GeRatios = { 0, 16.0 / 9, 21.0 / 9, 4.0 / 3, 1, 9.0 / 16 };
        private static readonly string[] GeRatioNames = { "Original", "16:9", "21:9", "4:3", "Square", "Tall (9:16)" };
        private static readonly string[] GeFilterNames = { "None", "Vivid", "Warm", "Cool", "Black and white", "Sepia", "Faded", "Punchy" };

        // a clip
        private double geLen, geStart, geEnd;
        private bool geEditEnd, gePlaying;

        private void OpenGalleryEditor()
        {
            var f = Current;
            if (f == null || galleryEditOpen) return;
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var host = win != null ? UiTools.FindByName(win, "PART_MainHost") as Panel : null;
            if (host == null) return;
            SetGalleryMore(false);
            SetSlideshow(false);
            StopGalleryVideo();
            if (galleryHideTimer != null) galleryHideTimer.Stop();
            PlaySound("select");
            geFile = f;
            geIsVideo = f.IsVideo;
            geRot = 0; geFilter = 0; geBright = geContrast = geSat = 0; geAdjustRow = 0;
            geCx = geCy = 0.5; geCs = 1; geRatio = 0;
            geMode = "tools";
            geToolKeys = geIsVideo ? new List<string> { "trim", "frame", "savetrim", "close" } : new List<string> { "crop", "rotate", "filter", "adjust", "save", "close" };
            geTool = 0;
            geBusy = false;
            BuildGalleryEditor(host);
            galleryEditOpen = true;
            geOpenedAt = DateTime.Now;
            if (geIsVideo) LoadEditVideo(); else LoadEditPhoto();
            GeUpdate();
            geLayer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            Dbg("gallery: edit " + (geIsVideo ? "clip" : "photo") + " " + f.Path);
        }

        private void CloseGalleryEditor()
        {
            if (!galleryEditOpen) return;
            galleryEditOpen = false;
            PlaySound("back");
            if (geTick != null) geTick.Stop();
            if (geVideo != null) { try { geVideo.Stop(); geVideo.Close(); geVideo.Source = null; } catch (Exception) { } }
            var layer = geLayer;
            geLayer = null;
            geSrc = null; geFiltered = null; geRotPixels = null;
            if (layer != null)
            {
                var p = layer.Parent as Panel;
                if (p != null) p.Children.Remove(layer);
            }
            ShowGalleryControls(false);
            ui.BeginInvoke(new Action(() => { if (!UiTools.FocusByNameIfVisible("KX_GalleryEditButton")) UiTools.FocusByName("KX_GallerySurface"); }), DispatcherPriority.ContextIdle);
        }

        // ---------------- building it ----------------

        private static Brush GeBrush(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        private Brush GeAccent { get { var b = Application.Current != null ? Application.Current.TryFindResource("KX_AccentBrush") as Brush : null; return b ?? GeBrush(0xFF, 0x5C, 0xE1, 0xE6); } }

        private void BuildGalleryEditor(Panel host)
        {
            geLayer = new Grid { Background = GeBrush(0xFF, 0x06, 0x07, 0x09), Focusable = false, Opacity = 0 };
            Panel.SetZIndex(geLayer, 86);
            if (host is Grid) { Grid.SetRowSpan(geLayer, 99); Grid.SetColumnSpan(geLayer, 99); }
            var root = new Grid { Margin = new Thickness(64, 40, 64, 30) };
            for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            geLayer.Children.Add(root);

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            geTitle = new TextBlock { FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            geSub = new TextBlock { FontSize = 17, Foreground = GeBrush(0x99, 0xFF, 0xFF, 0xFF), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            head.Children.Add(geTitle);
            head.Children.Add(geSub);
            root.Children.Add(head);

            gePreviewHost = new Grid { ClipToBounds = true };
            Grid.SetRow(gePreviewHost, 1);
            gePreview = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(gePreview, BitmapScalingMode.HighQuality);
            gePreviewHost.Children.Add(gePreview);
            geCropLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            for (int i = 0; i < 4; i++) { geShade[i] = new Rectangle { Fill = GeBrush(0xB3, 0, 0, 0) }; geCropLayer.Children.Add(geShade[i]); }
            geFrame = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 6, 3 } };
            geCropLayer.Children.Add(geFrame);
            gePreviewHost.Children.Add(geCropLayer);
            gePreviewHost.SizeChanged += (s, e) => GePlaceCrop();
            root.Children.Add(gePreviewHost);

            geOptions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0), MinHeight = 56 };
            Grid.SetRow(geOptions, 2);
            root.Children.Add(geOptions);

            var pill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(34), Padding = new Thickness(8), Margin = new Thickness(0, 14, 0, 0),
                Background = GeBrush(0xE6, 0x15, 0x17, 0x1C), BorderBrush = GeBrush(0x2E, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1)
            };
            geTools = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Child = geTools;
            Grid.SetRow(pill, 3);
            root.Children.Add(pill);

            var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            geStatus = new TextBlock { FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Height = 26 };
            geHint = new TextBlock { FontSize = 16, Foreground = GeBrush(0x99, 0xFF, 0xFF, 0xFF), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            foot.Children.Add(geStatus);
            foot.Children.Add(geHint);
            Grid.SetRow(foot, 4);
            root.Children.Add(foot);

            host.Children.Add(geLayer);
        }

        private static string GeToolName(string k)
        {
            switch (k)
            {
                case "crop": return "Crop";
                case "rotate": return "Rotate";
                case "filter": return "Filters";
                case "adjust": return "Adjust";
                case "save": return "Save a copy";
                case "trim": return "Trim";
                case "frame": return "Save this frame";
                case "savetrim": return "Save the trimmed clip";
                case "close": return "Close";
            }
            return k;
        }

        private Border GePill(string text, bool selected, bool active)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(26), Padding = new Thickness(22, 10, 22, 10), Margin = new Thickness(4, 0, 4, 0),
                Background = selected ? GeAccent : active ? GeBrush(0x40, 0xFF, 0xFF, 0xFF) : GeBrush(0x1C, 0xFF, 0xFF, 0xFF),
                Child = new TextBlock { Text = text, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = selected ? GeBrush(0xFF, 0x0E, 0x10, 0x14) : Brushes.White }
            };
        }

        /// <summary>The tools, the options strip, the title and the hint, as things are now.</summary>
        private void GeUpdate()
        {
            if (geLayer == null) return;
            geTitle.Text = geIsVideo ? "Edit clip" : "Edit photo";
            geSub.Text = (string.IsNullOrEmpty(geFile.GameName) ? "" : geFile.GameName + "  ·  ") + System.IO.Path.GetFileName(geFile.Path);
            geTools.Children.Clear();
            for (int i = 0; i < geToolKeys.Count; i++)
            {
                string k = geToolKeys[i];
                bool on = (k == geMode);
                geTools.Children.Add(GePill(GeToolName(k), i == geTool && geMode == "tools", on));
            }
            geOptions.Children.Clear();
            geCropLayer.Visibility = geMode == "crop" ? Visibility.Visible : Visibility.Collapsed;
            bool ps = false;
            switch (geMode)
            {
                case "crop":
                    geOptions.Children.Add(GePill("Shape: " + GeRatioNames[geRatio], false, true));
                    geHint.Text = "D-pad  Move        LB / RB  Smaller / bigger        Y  Shape        A  Done        B  Undo the crop";
                    break;
                case "filter":
                    for (int i = 0; i < GeFilterNames.Length; i++) geOptions.Children.Add(GePill(GeFilterNames[i], i == geFilter, false));
                    geHint.Text = "◀ ▶  Choose a filter        A  Done        B  Back";
                    break;
                case "adjust":
                    {
                        string[] names = { "Brightness", "Contrast", "Saturation" };
                        int[] vals = { geBright, geContrast, geSat };
                        for (int i = 0; i < 3; i++) geOptions.Children.Add(GePill(names[i] + "  " + (vals[i] > 0 ? "+" : "") + vals[i], i == geAdjustRow, false));
                        geHint.Text = "◀ ▶  Change        LB / RB  Which one        A  Done        B  Back";
                        break;
                    }
                case "trim":
                    GeBuildTrimBar();
                    geHint.Text = "▲ ▼  Start or end        ◀ ▶  A second        LB / RB  A tenth        X  Play        A  Done";
                    break;
                default:
                    if (geIsVideo) GeBuildTrimBar();
                    geHint.Text = "◀ ▶  Choose        A  Select        " + (geIsVideo ? "X  Play / pause        " : "") + "B  Close (not saved)";
                    break;
            }
            if (ps) { }
            GePlaceCrop();
        }

        // ---------------- a photo ----------------

        private void LoadEditPhoto()
        {
            string path = geFile.Path;
            geStatus.Text = "Loading…";
            KippixShared.SafeWork.Queue(_ =>
            {
                BitmapSource bmp = null;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.DecodePixelWidth = 1600;   // (the preview: filters work on this, quickly; the saved copy is made from the full picture)
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                    conv.Freeze();
                    bmp = conv;
                }
                catch (Exception ex) { logger.Warn("Kippix: edit: reading " + path + ": " + ex.Message); }
                OnUi(() =>
                {
                    if (!galleryEditOpen || geFile == null || geFile.Path != path) return;
                    if (bmp == null) { geStatus.Text = "This picture couldn't be read"; return; }
                    geSrc = bmp;
                    geStatus.Text = "";
                    GeTurn();
                });
            });
        }

        /// <summary>The picture turned (its pixels kept for the filters), then filtered and shown.</summary>
        private void GeTurn()
        {
            if (geSrc == null) return;
            BitmapSource rot = geSrc;
            if (geRot % 4 != 0) { var t = new TransformedBitmap(geSrc, new RotateTransform(90 * (geRot % 4))); t.Freeze(); rot = t; }
            geRotW = rot.PixelWidth; geRotH = rot.PixelHeight;
            geRotPixels = new byte[geRotW * geRotH * 4];
            rot.CopyPixels(geRotPixels, geRotW * 4, 0);
            GeFilterNow();
        }

        private void GeFilterNow()
        {
            if (geRotPixels == null) return;
            var m = GeMatrix();
            byte[] px = (byte[])geRotPixels.Clone();
            GeApply(px, m);
            var b = BitmapSource.Create(geRotW, geRotH, 96, 96, PixelFormats.Bgra32, null, px, geRotW * 4);
            b.Freeze();
            geFiltered = b;
            GeShow();
        }

        /// <summary>The whole picture while cropping (the frame over it), the cropped part otherwise.</summary>
        private void GeShow()
        {
            if (geFiltered == null) return;
            if (geMode == "crop" || GeCropIsWhole()) gePreview.Source = geFiltered;
            else
            {
                var r = GeCropRect(geRotW, geRotH);
                try { var c = new CroppedBitmap(geFiltered, r); c.Freeze(); gePreview.Source = c; } catch (Exception) { gePreview.Source = geFiltered; }
            }
            GePlaceCrop();
        }

        private bool GeCropIsWhole() { return geRatio == 0 && geCs >= 0.999; }

        /// <summary>The crop in a picture of this size.</summary>
        private Int32Rect GeCropRect(int W, int H)
        {
            double a = geRatio == 0 ? W / (double)H : GeRatios[geRatio];
            double mw = W / (double)H > a ? H * a : W, mh = W / (double)H > a ? H : W / a;
            double w = Math.Max(8, mw * geCs), h = Math.Max(8, mh * geCs);
            double x = geCx * W - w / 2, y = geCy * H - h / 2;
            x = Math.Max(0, Math.Min(W - w, x)); y = Math.Max(0, Math.Min(H - h, y));
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            return new Int32Rect(ix, iy, Math.Max(1, Math.Min(W - ix, (int)Math.Round(w))), Math.Max(1, Math.Min(H - iy, (int)Math.Round(h))));
        }

        private void GeClampCrop()
        {
            if (geRotW <= 0) return;
            var r = GeCropRect(geRotW, geRotH);
            geCx = (r.X + r.Width / 2.0) / geRotW;
            geCy = (r.Y + r.Height / 2.0) / geRotH;
        }

        private void GePlaceCrop()
        {
            if (geCropLayer == null || geMode != "crop" || geRotW <= 0 || gePreviewHost.ActualWidth <= 0) return;
            double W = geRotW, H = geRotH;
            double k = Math.Min(gePreviewHost.ActualWidth / W, gePreviewHost.ActualHeight / H);
            double ox = (gePreviewHost.ActualWidth - W * k) / 2, oy = (gePreviewHost.ActualHeight - H * k) / 2;
            var r = GeCropRect(geRotW, geRotH);
            double x = ox + r.X * k, y = oy + r.Y * k, w = r.Width * k, h = r.Height * k, iw = W * k, ih = H * k;
            GePut(geShade[0], ox, oy, iw, y - oy);
            GePut(geShade[1], ox, y + h, iw, oy + ih - (y + h));
            GePut(geShade[2], ox, y, x - ox, h);
            GePut(geShade[3], x + w, y, ox + iw - (x + w), h);
            GePut(geFrame, x, y, w, h);
        }

        private static void GePut(FrameworkElement e, double x, double y, double w, double h)
        {
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            e.Width = Math.Max(0, w); e.Height = Math.Max(0, h);
        }

        // colour: a 3 × 4 matrix (rows r, g, b; the last column added)
        private static float[] GeIdentity() { return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }; }

        /// <summary>a after b.</summary>
        private static float[] GeMul(float[] a, float[] b)
        {
            var r = new float[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
                r[i * 4 + 3] = a[i * 4] * b[3] + a[i * 4 + 1] * b[7] + a[i * 4 + 2] * b[11] + a[i * 4 + 3];
            }
            return r;
        }

        private static float[] GeSaturation(float s)
        {
            float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            return new float[]
            {
                lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0,
                lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0,
                lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0
            };
        }

        private static float[] GeContrast(float c, float lift)
        {
            float o = 128 * (1 - c) + lift;
            return new float[] { c, 0, 0, o, 0, c, 0, o, 0, 0, c, o };
        }

        private static float[] GeFilterMatrix(int f)
        {
            switch (f)
            {
                case 1: return GeMul(GeContrast(1.08f, 0), GeSaturation(1.35f));                                   // Vivid
                case 2: return new float[] { 1.08f, 0, 0, 8, 0, 1.02f, 0, 2, 0, 0, 0.88f, 0 };                     // Warm
                case 3: return new float[] { 0.9f, 0, 0, 0, 0, 1.0f, 0, 2, 0, 0, 1.1f, 10 };                        // Cool
                case 4: return GeMul(GeContrast(1.1f, 0), GeSaturation(0));                                        // Black and white
                case 5: return new float[] { 0.393f, 0.769f, 0.189f, 0, 0.349f, 0.686f, 0.168f, 0, 0.272f, 0.534f, 0.131f, 0 };   // Sepia
                case 6: return GeMul(GeContrast(0.82f, 20), GeSaturation(0.8f));                                   // Faded
                case 7: return GeMul(GeContrast(1.25f, 0), GeSaturation(1.18f));                                   // Punchy
            }
            return GeIdentity();
        }

        private float[] GeMatrix()
        {
            var m = GeFilterMatrix(geFilter);
            if (geSat != 0) m = GeMul(GeSaturation(1 + geSat / 100f), m);
            if (geContrast != 0) m = GeMul(GeContrast(1 + geContrast / 100f, 0), m);
            if (geBright != 0) { var b = GeIdentity(); b[3] = b[7] = b[11] = geBright * 1.6f; m = GeMul(b, m); }
            return m;
        }

        private static void GeApply(byte[] px, float[] m)
        {
            bool id = true;
            var ident = GeIdentity();
            for (int i = 0; i < 12; i++) if (Math.Abs(m[i] - ident[i]) > 1e-4) { id = false; break; }
            if (id) return;
            for (int i = 0; i < px.Length; i += 4)
            {
                float b = px[i], g = px[i + 1], r = px[i + 2];
                float nr = m[0] * r + m[1] * g + m[2] * b + m[3];
                float ng = m[4] * r + m[5] * g + m[6] * b + m[7];
                float nb = m[8] * r + m[9] * g + m[10] * b + m[11];
                px[i] = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                px[i + 1] = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng);
                px[i + 2] = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr);
            }
        }

        /// <summary>The edited copy from the full picture, next to it, as a photo of the same game.</summary>
        private void GeSavePhoto()
        {
            if (geBusy || geSrc == null) return;
            geBusy = true;
            geStatus.Text = "Saving…";
            var f = geFile;
            int rot = geRot % 4, ratio = geRatio;
            double cx = geCx, cy = geCy, cs = geCs;
            var m = GeMatrix();
            bool whole = GeCropIsWhole();
            KippixShared.SafeWork.Queue(_ =>
            {
                string file = null, err = null;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bi.UriSource = new Uri(f.Path); bi.EndInit();
                    BitmapSource b = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                    if (rot != 0) b = new TransformedBitmap(b, new RotateTransform(90 * rot));
                    int W = b.PixelWidth, H = b.PixelHeight;
                    Int32Rect r = new Int32Rect(0, 0, W, H);
                    if (!whole)
                    {
                        // the same crop as on screen, worked out at the full size
                        double a = ratio == 0 ? W / (double)H : GeRatios[ratio];
                        double mw = W / (double)H > a ? H * a : W, mh = W / (double)H > a ? H : W / a;
                        double w = Math.Max(8, mw * cs), h = Math.Max(8, mh * cs);
                        double x = Math.Max(0, Math.Min(W - w, cx * W - w / 2)), y = Math.Max(0, Math.Min(H - h, cy * H - h / 2));
                        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
                        r = new Int32Rect(ix, iy, Math.Max(1, Math.Min(W - ix, (int)Math.Round(w))), Math.Max(1, Math.Min(H - iy, (int)Math.Round(h))));
                    }
                    var px = new byte[r.Width * r.Height * 4];
                    b.CopyPixels(r, px, r.Width * 4, 0);
                    GeApply(px, m);
                    var outBmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null, px, r.Width * 4);
                    file = FreeName(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path), System.IO.Path.GetFileNameWithoutExtension(f.Path) + " edit.png"));
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(outBmp));
                    using (var fs = File.Create(file)) enc.Save(fs);
                }
                catch (Exception ex) { err = ex.Message; file = null; logger.Warn("Kippix: edit: saving: " + ex.Message); }
                OnUi(() =>
                {
                    geBusy = false;
                    if (file == null) { if (geStatus != null) geStatus.Text = "It couldn't be saved: " + err; return; }
                    GeAdded(file, false, f);
                    if (geStatus != null) geStatus.Text = "Saved as a new photo: " + System.IO.Path.GetFileName(file);
                    PlaySound("select");
                });
            });
        }

        /// <summary>A new photo or clip from the editor: in the gallery, after the one it came from, and with its game.</summary>
        private void GeAdded(string file, bool video, GalleryFile from)
        {
            try
            {
                if (from.GameId != Guid.Empty) { var g = api.Database.Games.Get(from.GameId); if (g != null) RecordCapture(file, g); }
                var gf = new GalleryFile { Path = file, IsVideo = video, Date = DateTime.Now, Size = new FileInfo(file).Length, GameId = from.GameId, GameName = from.GameName };
                galleryFiles.Add(gf);
                int at = galleryList.IndexOf(from);
                galleryList.Insert(at >= 0 ? at + 1 : galleryList.Count, gf);
                galleryRebuildOnClose = true;
                galleryChanged = true;
                Notify("GalleryViewerCounter");
            }
            catch (Exception) { }
        }

        // ---------------- a clip ----------------

        private void LoadEditVideo()
        {
            geVideo = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = galleryMuted ? 0 : galleryVolume, ScrubbingEnabled = true };
            geVideo.MediaOpened += (s, e) =>
            {
                try
                {
                    geLen = geVideo.NaturalDuration.HasTimeSpan ? geVideo.NaturalDuration.TimeSpan.TotalSeconds : 0;
                    geStart = 0; geEnd = geLen;
                    geVideo.Pause();
                    geVideo.Position = TimeSpan.Zero;
                    GeUpdate();
                }
                catch (Exception) { }
            };
            geVideo.MediaFailed += (s, e) => { if (geStatus != null) geStatus.Text = "This clip can't be played here (Windows may need a codec for it)"; };
            gePreviewHost.Children.Insert(0, geVideo);
            gePreview.Visibility = Visibility.Collapsed;
            geVideo.Source = new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = geFile.Path }.Uri;
            geVideo.Play();   // (opens it; paused once it's open)
            gePlaying = false;
            geTick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(100) };
            geTick.Tick += (s, e) => GeVideoTick();
            geTick.Start();
        }

        private void GeVideoTick()
        {
            if (geVideo == null || geLen <= 0) return;
            double pos = 0;
            try { pos = geVideo.Position.TotalSeconds; } catch (Exception) { }
            if (gePlaying && pos >= geEnd - 0.05) { try { geVideo.Position = TimeSpan.FromSeconds(geStart); } catch (Exception) { } }   // (plays the trimmed part, round again)
            GePlaceTrim(pos);
        }

        private void GeBuildTrimBar()
        {
            const double W = 1100;
            geTrimBar = new Grid { Width = W, Height = 40, VerticalAlignment = VerticalAlignment.Center };
            geTrimBar.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = GeBrush(0x33, 0xFF, 0xFF, 0xFF), VerticalAlignment = VerticalAlignment.Center });
            geTrimFill = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = GeAccent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            geTrimStart = new Border { Width = 10, Height = 36, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            geTrimEnd = new Border { Width = 10, Height = 36, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            geTrimHead = new Border { Width = 3, Height = 24, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            geTrimBar.Children.Add(geTrimFill);
            geTrimBar.Children.Add(geTrimHead);
            geTrimBar.Children.Add(geTrimStart);
            geTrimBar.Children.Add(geTrimEnd);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(geTrimBar);
            var times = new TextBlock { FontSize = 18, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), MinWidth = 260 };
            times.Tag = "times";
            row.Children.Add(times);
            geOptions.Children.Add(row);
            double pos = 0;
            try { if (geVideo != null) pos = geVideo.Position.TotalSeconds; } catch (Exception) { }
            GePlaceTrim(pos);
        }

        private void GePlaceTrim(double pos)
        {
            if (geTrimBar == null || geLen <= 0) return;
            double W = geTrimBar.Width;
            double xs = geStart / geLen * W, xe = geEnd / geLen * W, xp = Math.Max(0, Math.Min(1, pos / geLen)) * W;
            geTrimFill.Margin = new Thickness(xs, 0, 0, 0);
            geTrimFill.Width = Math.Max(0, xe - xs);
            bool trimming = geMode == "trim";
            geTrimStart.Margin = new Thickness(Math.Max(0, xs - 5), 0, 0, 0);
            geTrimEnd.Margin = new Thickness(Math.Max(0, xe - 5), 0, 0, 0);
            geTrimStart.Background = trimming && !geEditEnd ? Brushes.White : GeBrush(0x99, 0xFF, 0xFF, 0xFF);
            geTrimEnd.Background = trimming && geEditEnd ? Brushes.White : GeBrush(0x99, 0xFF, 0xFF, 0xFF);
            geTrimHead.Margin = new Thickness(Math.Max(0, xp - 1.5), 0, 0, 0);
            var row = geTrimBar.Parent as StackPanel;
            var times = row != null ? row.Children.OfType<TextBlock>().FirstOrDefault() : null;
            if (times != null) times.Text = GeClock(geStart) + "  →  " + GeClock(geEnd) + "   (" + GeClock(geEnd - geStart) + ")";
        }

        private static string GeClock(double s)
        {
            if (s < 0) s = 0;
            int t = (int)s, tenth = (int)((s - t) * 10);
            return (t / 60) + ":" + (t % 60).ToString("00") + "." + tenth;
        }

        private void GeTrimMove(double by)
        {
            if (geLen <= 0) return;
            if (geEditEnd) geEnd = Math.Max(geStart + 0.5, Math.Min(geLen, geEnd + by));
            else geStart = Math.Max(0, Math.Min(geEnd - 0.5, geStart + by));
            try { geVideo.Pause(); gePlaying = false; geVideo.Position = TimeSpan.FromSeconds(geEditEnd ? Math.Max(0, geEnd - 0.05) : geStart); } catch (Exception) { }
            GePlaceTrim(geEditEnd ? geEnd : geStart);
        }

        private void GeTogglePlay()
        {
            if (geVideo == null) return;
            try
            {
                if (gePlaying) { geVideo.Pause(); gePlaying = false; }
                else
                {
                    double pos = geVideo.Position.TotalSeconds;
                    if (pos < geStart || pos >= geEnd - 0.1) geVideo.Position = TimeSpan.FromSeconds(geStart);
                    geVideo.Play();
                    gePlaying = true;
                }
            }
            catch (Exception) { }
        }

        private void GeSaveFrame()
        {
            if (geVideo == null || geBusy) return;
            try
            {
                int w = geVideo.NaturalVideoWidth, h = geVideo.NaturalVideoHeight;
                if (w <= 0 || h <= 0) return;
                int sec = (int)geVideo.Position.TotalSeconds;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(geVideo) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string file = FreeName(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(geFile.Path), System.IO.Path.GetFileNameWithoutExtension(geFile.Path) + " frame " + (sec / 60) + "m" + (sec % 60).ToString("00") + "s.png"));
                using (var fs = File.Create(file)) enc.Save(fs);
                GeAdded(file, false, geFile);
                geStatus.Text = "This frame is saved as a photo";
                PlaySound("select");
            }
            catch (Exception ex) { geStatus.Text = "That frame couldn't be saved: " + ex.Message; }
        }

        private void GeSaveTrim()
        {
            if (geBusy || geVideo == null || geLen <= 0) return;
            if (geStart <= 0.05 && geEnd >= geLen - 0.05) { geStatus.Text = "Move its start or end first (Trim)"; PlaySound("back"); return; }
            geBusy = true;
            try { geVideo.Pause(); gePlaying = false; } catch (Exception) { }
            geStatus.Text = "Saving the trimmed clip… (this takes about as long as the clip)";
            var f = geFile;
            double s0 = geStart, e0 = geEnd;
            KippixShared.SafeWork.Queue(_ =>
            {
                string err;
                string file = WinRtTrim.Trim(f.Path, TimeSpan.FromSeconds(s0), TimeSpan.FromSeconds(e0), out err);
                if (file == null) logger.Warn("Kippix: edit: trimming " + f.Path + ": " + err);
                OnUi(() =>
                {
                    geBusy = false;
                    if (file == null) { if (geStatus != null) geStatus.Text = "The clip couldn't be trimmed: " + err; return; }
                    GeAdded(file, true, f);
                    if (geStatus != null) geStatus.Text = "Saved as a new clip: " + System.IO.Path.GetFileName(file);
                    PlaySound("select");
                });
            });
        }

        // ---------------- buttons ----------------

        /// <summary>A controller button while Edit is up (every one is taken).</summary>
        private void GalleryEditPad(ControllerInput button, KeyEventArgs e)
        {
            if (IsPadRelease(e)) return;
            if ((DateTime.Now - geOpenedAt).TotalMilliseconds < 250) return;
            switch (button)
            {
                case ControllerInput.A: gePadAAt = DateTime.Now; GeAct("a"); break;
                case ControllerInput.B: gePadBAt = DateTime.Now; GeAct("b"); break;
                case ControllerInput.X: GeAct("x"); break;
                case ControllerInput.Y: GeAct("y"); break;
                case ControllerInput.LeftShoulder: GeAct("lb"); break;
                case ControllerInput.RightShoulder: GeAct("rb"); break;
            }
        }

        /// <summary>Keys while Edit is up: the arrows (the D-pad and stick come as these), Enter / Esc on a keyboard, Page Up / Down.</summary>
        private void GalleryEditKey(KeyEventArgs e)
        {
            if (e.Key == Key.System) return;
            e.Handled = true;
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (the buttons: GalleryEditPad)
            switch (e.Key)
            {
                case Key.Left: GeAct("left"); break;
                case Key.Right: GeAct("right"); break;
                case Key.Up: GeAct("up"); break;
                case Key.Down: GeAct("down"); break;
                case Key.PageUp: GeAct("lb"); break;
                case Key.PageDown: GeAct("rb"); break;
                case Key.Enter: if (!e.IsRepeat && (DateTime.Now - gePadAAt).TotalMilliseconds > 400) GeAct("a"); break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat && (DateTime.Now - gePadBAt).TotalMilliseconds > 400) GeAct("b"); break;
            }
        }

        private void GeAct(string k)
        {
            if (!galleryEditOpen || geLayer == null) return;
            if (geIsVideo && k == "x") { GeTogglePlay(); return; }
            switch (geMode)
            {
                case "tools":
                    if (k == "left" && geTool > 0) { geTool--; PlaySound("move"); GeUpdate(); }
                    else if (k == "right" && geTool < geToolKeys.Count - 1) { geTool++; PlaySound("move"); GeUpdate(); }
                    else if (k == "a") GeUseTool(geToolKeys[geTool]);
                    else if (k == "b") CloseGalleryEditor();
                    return;
                case "crop":
                    if (k == "left") GeMoveCrop(-1, 0);
                    else if (k == "right") GeMoveCrop(1, 0);
                    else if (k == "up") GeMoveCrop(0, -1);
                    else if (k == "down") GeMoveCrop(0, 1);
                    else if (k == "lb") GeSizeCrop(-1);
                    else if (k == "rb") GeSizeCrop(1);
                    else if (k == "y") { geRatio = (geRatio + 1) % GeRatios.Length; if (geRatio != 0 && geCs >= 0.999) geCs = 0.9; GeClampCrop(); PlaySound("move"); GeUpdate(); }
                    else if (k == "a") { geMode = "tools"; PlaySound("select"); GeUpdate(); GeShow(); }
                    else if (k == "b") { geRatio = 0; geCs = 1; geCx = geCy = 0.5; geMode = "tools"; PlaySound("back"); GeUpdate(); GeShow(); }
                    return;
                case "filter":
                    if (k == "left" && geFilter > 0) { geFilter--; PlaySound("move"); GeUpdate(); GeFilterNow(); }
                    else if (k == "right" && geFilter < GeFilterNames.Length - 1) { geFilter++; PlaySound("move"); GeUpdate(); GeFilterNow(); }
                    else if (k == "a" || k == "b") { geMode = "tools"; PlaySound(k == "a" ? "select" : "back"); GeUpdate(); }
                    return;
                case "adjust":
                    if (k == "lb" || k == "up") { geAdjustRow = (geAdjustRow + 2) % 3; PlaySound("move"); GeUpdate(); }
                    else if (k == "rb" || k == "down") { geAdjustRow = (geAdjustRow + 1) % 3; PlaySound("move"); GeUpdate(); }
                    else if (k == "left" || k == "right")
                    {
                        int d = k == "left" ? -5 : 5;
                        if (geAdjustRow == 0) geBright = Math.Max(-50, Math.Min(50, geBright + d));
                        else if (geAdjustRow == 1) geContrast = Math.Max(-50, Math.Min(50, geContrast + d));
                        else geSat = Math.Max(-100, Math.Min(100, geSat + d));
                        PlaySound("move");
                        GeUpdate();
                        GeFilterNow();
                    }
                    else if (k == "a" || k == "b") { geMode = "tools"; PlaySound(k == "a" ? "select" : "back"); GeUpdate(); }
                    return;
                case "trim":
                    if (k == "up" || k == "down") { geEditEnd = k == "down"; PlaySound("move"); GePlaceTrim(geEditEnd ? geEnd : geStart); }
                    else if (k == "left") GeTrimMove(-1);
                    else if (k == "right") GeTrimMove(1);
                    else if (k == "lb") GeTrimMove(-0.1);
                    else if (k == "rb") GeTrimMove(0.1);
                    else if (k == "a" || k == "b") { geMode = "tools"; PlaySound(k == "a" ? "select" : "back"); GeUpdate(); }
                    return;
            }
        }

        private void GeUseTool(string t)
        {
            if (geBusy) { PlaySound("back"); return; }
            switch (t)
            {
                case "crop": if (geSrc == null) return; geMode = "crop"; if (GeCropIsWhole()) geCs = 0.9; GeClampCrop(); PlaySound("select"); GeUpdate(); GeShow(); break;
                case "rotate": geRot = (geRot + 1) % 4; geCx = geCy = 0.5; PlaySound("select"); GeTurn(); GeUpdate(); break;
                case "filter": geMode = "filter"; PlaySound("select"); GeUpdate(); break;
                case "adjust": geMode = "adjust"; PlaySound("select"); GeUpdate(); break;
                case "save": GeSavePhoto(); break;
                case "trim": geMode = "trim"; geEditEnd = false; PlaySound("select"); GeUpdate(); break;
                case "frame": GeSaveFrame(); break;
                case "savetrim": GeSaveTrim(); break;
                case "close": CloseGalleryEditor(); break;
            }
        }

        private void GeMoveCrop(int dx, int dy)
        {
            if (geRotW <= 0) return;
            geCx += dx * 0.025;
            geCy += dy * 0.025 * geRotW / Math.Max(1.0, geRotH);
            GeClampCrop();
            GePlaceCrop();
        }

        private void GeSizeCrop(int d)
        {
            geCs = Math.Max(0.15, Math.Min(1, geCs + d * 0.05));
            GeClampCrop();
            PlaySound("move");
            GePlaceCrop();
        }
    }

    /// <summary>
    /// (0.99.59) A trimmed copy of a clip with Windows' own video editing (Windows.Media.Editing, as the Photos app uses), reached
    /// at run time so nothing else is needed. Saved next to the clip as "… trim.mp4". Null (and why) when it couldn't be done.
    /// </summary>
    internal static class WinRtTrim
    {
        private static Type T(string name, string winmd) { return Type.GetType(name + ", " + winmd + ", ContentType=WindowsRuntime", true); }

        private static MethodInfo asTask1, asTask2;

        private static object Await(object op, Type result, Type progress)
        {
            if (asTask1 == null)
            {
                var asm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                var ext = asm.GetType("System.WindowsRuntimeSystemExtensions", true);
                foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "AsTask" || m.GetParameters().Length != 1) continue;
                    string pn = m.GetParameters()[0].ParameterType.Name;
                    if (pn == "IAsyncOperation`1") asTask1 = m;
                    else if (pn == "IAsyncOperationWithProgress`2") asTask2 = m;
                }
            }
            var mi = progress == null ? asTask1.MakeGenericMethod(result) : asTask2.MakeGenericMethod(result, progress);
            var task = (Task)mi.Invoke(null, new[] { op });
            task.Wait();
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        public static string Trim(string src, TimeSpan start, TimeSpan end, out string error)
        {
            error = null;
            try
            {
                var fileT = T("Windows.Storage.StorageFile", "Windows.Storage");
                var ifileT = T("Windows.Storage.IStorageFile", "Windows.Storage");
                var folderT = T("Windows.Storage.StorageFolder", "Windows.Storage");
                var collT = T("Windows.Storage.CreationCollisionOption", "Windows.Storage");
                var clipT = T("Windows.Media.Editing.MediaClip", "Windows.Media");
                var compT = T("Windows.Media.Editing.MediaComposition", "Windows.Media");
                var failT = T("Windows.Media.Transcoding.TranscodeFailureReason", "Windows.Media");

                object file = Await(fileT.GetMethod("GetFileFromPathAsync").Invoke(null, new object[] { src }), fileT, null);
                object clip = Await(clipT.GetMethod("CreateFromFileAsync").Invoke(null, new[] { file }), clipT, null);
                var orig = (TimeSpan)clipT.GetProperty("OriginalDuration").GetValue(clip, null);
                if (end > orig) end = orig;
                clipT.GetProperty("TrimTimeFromStart").SetValue(clip, start, null);
                clipT.GetProperty("TrimTimeFromEnd").SetValue(clip, orig - end, null);

                object comp = Activator.CreateInstance(compT);
                object clips = compT.GetProperty("Clips").GetValue(comp, null);
                typeof(ICollection<>).MakeGenericType(clipT).GetMethod("Add").Invoke(clips, new[] { clip });

                string dir = System.IO.Path.GetDirectoryName(src);
                object folder = Await(folderT.GetMethod("GetFolderFromPathAsync").Invoke(null, new object[] { dir }), folderT, null);
                string name = System.IO.Path.GetFileNameWithoutExtension(src) + " trim.mp4";
                object gen = Enum.Parse(collT, "GenerateUniqueName");
                object outFile = Await(folderT.GetMethod("CreateFileAsync", new[] { typeof(string), collT }).Invoke(folder, new[] { name, gen }), fileT, null);

                var render = compT.GetMethod("RenderToFileAsync", new[] { ifileT });
                object why = Await(render.Invoke(comp, new[] { outFile }), failT, typeof(double));
                string outPath = (string)fileT.GetProperty("Path").GetValue(outFile, null);
                if (why != null && why.ToString() != "None")
                {
                    error = "Windows said " + why;
                    try { File.Delete(outPath); } catch (Exception) { }
                    return null;
                }
                return outPath;
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                error = inner.Message;
                return null;
            }
        }
    }
}

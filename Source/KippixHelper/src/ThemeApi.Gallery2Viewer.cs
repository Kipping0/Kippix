using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>A picture in the Console viewer's strip along the bottom.</summary>
    public class GalleryStripItem : Observable
    {
        internal string Path;
        internal int Index;
        private ImageSource thumb;
        public ImageSource Thumb { get { return thumb; } set { Set(ref thumb, value, "Thumb"); } }
        private bool isCurrent;
        public bool IsCurrent { get { return isCurrent; } set { Set(ref isCurrent, value, "IsCurrent"); } }
        public ICommand Command { get; set; }
    }

    /// <summary>
    /// (1.0.7) The gallery's Console viewer (Gallery › Console, the user's mock-up): a bar across the top (back, the game, when
    /// and its size, "4 / 27", FAVOURITE, then Edit / Favourite / Share / Delete / More), the picture between, round previous /
    /// next buttons at the sides, and the pictures either side in a strip along the bottom with the hints. A hides all of it
    /// (and shows it again), so you can go through the pictures with nothing on screen; the controls never hide on their own.
    /// LB / RB or Left / Right previous / next, X favourite, Y edit, ≡ more, Up to the buttons along the top.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<GalleryStripItem> GalleryStrip { get; private set; }
        public ICommand GalleryShareCommand { get; private set; }
        public ICommand GalleryViewerBackCommand { get; private set; }
        public ICommand GalleryHideCommand { get; private set; }
        private int stripFor = -1;
        private string viewer2Size = "";

        /// <summary>The old viewer's bars (Default): only when the Console design isn't on.</summary>
        public bool GalleryControlsShownV1 { get { return galleryControlsShown && !Gallery2; } }
        public bool GalleryViewer2 { get { return galleryViewerOpen && Gallery2; } }
        public bool GalleryViewer2Bars { get { return galleryViewerOpen && Gallery2 && galleryControlsShown; } }
        /// <summary>The picture sits between the bars while they show, and fills the screen when they're hidden.</summary>
        public Thickness GalleryImageMargin { get { return Gallery2 && galleryControlsShown ? new Thickness(0, 112, 0, 190) : new Thickness(0); } }
        public string GalleryViewer2Sub
        {
            get
            {
                var f = Current;
                if (f == null) return "";
                return Gallery2When(f.Date) + (string.IsNullOrEmpty(viewer2Size) ? "" : " · " + viewer2Size);
            }
        }
        public string GalleryViewer2FavText { get { return GalleryViewerIsFav ? Loc("LOC_KX_G2Favourited", "Favourited") : Loc("LOC_KX_G2Favourite", "Favourite"); } }

        private void InitGallery2Viewer()
        {
            GalleryStrip = new ObservableCollection<GalleryStripItem>();
            GalleryShareCommand = new RelayCommand(_ => ShareGalleryCurrent());
            GalleryViewerBackCommand = new RelayCommand(_ => { PlaySound("back"); if (!GalleryBack()) ClosePanels(); });
            GalleryHideCommand = new RelayCommand(_ => ToggleViewer2Controls());
        }

        private void NotifyViewer2()
        {
            foreach (string n in new[] { "GalleryControlsShownV1", "GalleryViewer2", "GalleryViewer2Bars", "GalleryImageMargin", "GalleryViewer2Sub", "GalleryViewer2FavText" }) Notify(n);
            if (galleryViewerOpen && Gallery2 && stripFor != galleryIndex) UpdateGalleryStrip();
        }

        /// <summary>A on the picture: a clip plays / pauses (hiding the controls is the View button); a photo brings them back if they're hidden.</summary>
        private void Viewer2Surface()
        {
            if (Current != null && Current.IsVideo) { ToggleGalleryVideo(); return; }
            if (!galleryControlsShown) ToggleViewer2Controls();
        }

        /// <summary>The View button (or the Hide hint): every control goes, or comes back.</summary>
        private void ToggleViewer2Controls()
        {
            if (!galleryViewerOpen) return;
            if (galleryMoreOpen) SetGalleryMore(false);
            galleryControlsShown = !galleryControlsShown;
            if (galleryHideTimer != null) galleryHideTimer.Stop();
            PlaySound("move");
            Notify("GalleryControlsShown");
            NotifyViewer2();
            if (!galleryControlsShown) UiTools.FocusByName("KX_GallerySurface");
        }

        // ---------------- the strip ----------------

        private void UpdateGalleryStrip()
        {
            stripFor = galleryIndex;
            if (galleryList == null || galleryList.Count == 0) { GalleryStrip.Clear(); return; }
            int n = galleryList.Count, show = Math.Min(9, n);
            int start = Math.Max(0, Math.Min(galleryIndex - 4, n - show));
            var want = Enumerable.Range(start, show).ToList();
            // the same window (only the highlight moved): keep the pictures
            if (GalleryStrip.Count == show && GalleryStrip.Select(s => s.Index).SequenceEqual(want))
            {
                foreach (var s in GalleryStrip) s.IsCurrent = s.Index == galleryIndex;
            }
            else
            {
                GalleryStrip.Clear();
                foreach (int i in want)
                {
                    var f = galleryList[i];
                    int at = i;
                    var item = new GalleryStripItem { Path = f.Path, Index = i, IsCurrent = i == galleryIndex };
                    item.Command = new RelayCommand(_ => { if (at != galleryIndex) { PlaySound("move"); ShowGalleryItem(at); } });
                    ImageSource hit;
                    lock (galleryThumbs) galleryThumbs.TryGetValue(f.Path, out hit);
                    if (hit != null) item.Thumb = hit;
                    else
                    {
                        string path = f.Path;
                        bool video = f.IsVideo;
                        var it = item;
                        ShellMedia.Run(() =>
                        {
                            ImageSource img = ShellMedia.Thumbnail(path, 320);
                            if (img == null && !video) img = Images.LoadUncached(path, 320);
                            if (img != null) OnUi(() => { lock (galleryThumbs) galleryThumbs[path] = img; it.Thumb = img; });
                        });
                    }
                    GalleryStrip.Add(item);
                }
            }
            // its size, for the line under the name
            var cur = galleryList[galleryIndex];
            string size;
            if (g2Sizes.TryGetValue(cur.Path, out size)) { viewer2Size = size; Notify("GalleryViewer2Sub"); }
            else
            {
                viewer2Size = "";
                Notify("GalleryViewer2Sub");
                string path = cur.Path;
                bool video = cur.IsVideo;
                ShellMedia.Run(() =>
                {
                    int w = 0, h = 0;
                    try
                    {
                        if (video) { TimeSpan len; ShellMedia.Details(path, true, out w, out h, out len); }
                        else
                        {
                            var dec = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                            if (dec.Frames.Count > 0) { w = dec.Frames[0].PixelWidth; h = dec.Frames[0].PixelHeight; }
                        }
                    }
                    catch (Exception) { }
                    if (w <= 0 || h <= 0) return;
                    OnUi(() =>
                    {
                        g2Sizes[path] = w + " × " + h;
                        var c = Current;
                        if (c != null && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)) { viewer2Size = g2Sizes[path]; Notify("GalleryViewer2Sub"); }
                    });
                });
            }
        }

        // ---------------- Share: onto the clipboard, ready to paste ----------------

        private void ShareGalleryCurrent()
        {
            var f = Current;
            if (f == null) return;
            try
            {
                var data = new DataObject();
                var files = new StringCollection { f.Path };
                data.SetFileDropList(files);   // (pasted into a folder, a chat or an email: the file itself)
                if (!f.IsVideo)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(f.Path);
                    bmp.EndInit();
                    bmp.Freeze();
                    data.SetImage(bmp);            // (pasted into a picture box: the picture)
                }
                Clipboard.SetDataObject(data, true);
                PlaySound("select");
                ShowGalleryStatus(Loc("LOC_KX_G2Copied", "Copied: paste it into a chat, a post or a folder"));
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: gallery share: " + ex.Message);
                PlaySound("back");
                ShowGalleryStatus(Loc("LOC_KX_G2CopyFailed", "Couldn't copy it. Try again in a moment."));
            }
        }

        // ---------------- keys ----------------

        /// <summary>Arrows in the Console viewer: on the picture, Left / Right go through them and Up goes to the buttons along the top;
        /// on the buttons, Left / Right move along and Down goes back to the picture.</summary>
        private bool Gallery2ArrowKey(KeyEventArgs e)
        {
            if (!galleryViewerOpen || !Gallery2) return false;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            bool onSurface = focused != null && focused.Name == "KX_GallerySurface";
            if (onSurface)
            {
                if (galleryZoom > 1.01)
                {
                    double step = 160;
                    if (e.Key == Key.Left) PanGallery(step, 0);
                    else if (e.Key == Key.Right) PanGallery(-step, 0);
                    else if (e.Key == Key.Up) PanGallery(0, step);
                    else if (e.Key == Key.Down) PanGallery(0, -step);
                    return true;
                }
                if (e.Key == Key.Left) { GalleryStep(-1); return true; }
                if (e.Key == Key.Right) { GalleryStep(1); return true; }
                if (e.Key == Key.Up && galleryControlsShown) { UiTools.FocusByName("KX_GalleryEditButton"); PlaySound("move"); }
                return true;
            }
            if (e.Key == Key.Down) { UiTools.FocusByName("KX_GallerySurface"); PlaySound("move"); return true; }
            if (e.Key == Key.Up) return true;
            return false;   // along the buttons
        }

        /// <summary>The controller in the Console viewer. True if handled.</summary>
        private bool Gallery2ViewerPad(string btn, bool pressed, int step, int pageJump, bool xPressed, bool yPressed, KeyEventArgs e)
        {
            if (!galleryViewerOpen || !Gallery2) return false;
            if (btn == "Back") { if (pressed) ToggleViewer2Controls(); return true; }   // the View button hides / shows the controls
            bool direction = btn != null && (btn.StartsWith("DPad", StringComparison.Ordinal) || (btn.StartsWith("LeftStick", StringComparison.Ordinal) && btn.Length > 9) || (btn.StartsWith("RightStick", StringComparison.Ordinal) && btn.Length > 10));
            if (direction || !pressed) return false;
            bool shoulder = btn != null ? (btn == "LeftShoulder" || btn == "RightShoulder") : (e.Key != Key.PageUp && e.Key != Key.PageDown);
            if (step != 0 && shoulder) { GalleryStep(step); return true; }
            if (pageJump != 0)
            {
                if (Current != null && Current.IsVideo) GallerySeek(pageJump * 10);
                else SetGalleryZoom(NextZoom(pageJump));
                return true;
            }
            if (xPressed) { ToggleGalleryFavourite(); NotifyViewer2(); return true; }
            if (yPressed) { OpenGalleryEditor(); return true; }
            if (btn == "Start") { GalleryMore("open"); return true; }
            return false;
        }
    }
}

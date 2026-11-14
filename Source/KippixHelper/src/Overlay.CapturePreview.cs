using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixOverlay
{
    /// <summary>
    /// (0.99.54) The Guide button while the "Screenshot saved" / "Clip saved" notification is up: the picture or clip nearly
    /// full screen over the game, with its own controls in one bar. A photo: Crop (a frame to place: D-pad / stick moves it,
    /// LB / RB smaller / bigger, Y the shape: 16:9, 21:9, 4:3, square, tall, A saves a cropped copy next to it), Delete (to the
    /// Recycle Bin), Done. A clip: back 10 s, play / pause, forward 10 s (LB / RB too), Save this frame as a photo, Delete, Done.
    /// Left / Right choose, A presses, B or Guide go back to the game. Like the power menu it's a window of its own that takes
    /// the focus (Playnite hands the Steam Controller only to its focused window) and reads the overlay's own controller too.
    /// </summary>
    internal class CapturePreviewWindow : Window
    {
        private readonly OverlayPlugin plugin;
        private readonly OverlayTheme theme;
        private readonly string path;
        private readonly bool video;
        private readonly Game game;
        private readonly IntPtr gameWindow;
        private readonly DateTime openedAt = DateTime.Now;
        private IntPtr handle;
        private bool mouseSeen, mouseMoved;
        private Point mouseAt;
        private bool busy;

        private readonly Grid host;
        private Image image;
        private BitmapSource bitmap;
        private MediaElement media;
        private bool playing;
        private readonly Border progressFill;
        private readonly TextBlock timeText, label, status, hint, title, sub;
        private readonly StackPanel bar;
        private readonly Grid progressRow;
        private readonly List<Tuple<Border, string, Action, Func<string>>> buttons = new List<Tuple<Border, string, Action, Func<string>>>();
        private int index;
        private readonly DispatcherTimer tick;

        // cropping
        private bool cropping;
        private readonly Canvas cropLayer;
        private readonly Rectangle[] shade = new Rectangle[4];
        private readonly Rectangle frame;
        private static readonly double[] Ratios = { 16.0 / 9, 21.0 / 9, 4.0 / 3, 1, 9.0 / 16 };
        private static readonly string[] RatioNames = { "16:9", "21:9", "4:3", "Square", "Tall (9:16)" };
        private int ratio;
        private double cropScale = 0.8, cropCx = 0.5, cropCy = 0.5;
        private ushort held;
        private DateTime heldSince;

        public CapturePreviewWindow(OverlayPlugin plugin, string file, bool isVideo, Game g, IntPtr anchorHwnd)
        {
            this.plugin = plugin;
            path = file;
            video = isVideo;
            game = g;
            gameWindow = anchorHwnd;
            theme = OverlayTheme.Get(plugin.Config != null ? plugin.Config.Theme : 0);
            WindowStyle = WindowStyle.None;
            // (0.99.55) an ordinary (not see-through) window: a see-through one the size of a 4K screen is drawn by the processor,
            // a frame at a time, and made the preview (and the game behind it) stutter
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x08, 0x0B));
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = OverlayWindow.UiFont;
            var mb = Capture.MonitorBounds(anchorHwnd);
            double scale = Capture.DpiScale();
            Left = mb.Left / scale; Top = mb.Top / scale; Width = mb.Width / scale; Height = mb.Height / scale;
            SourceInitialized += (s, e) =>
            {
                handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                try { Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE); } catch (Exception) { }
            };
            PreviewKeyDown += OnKey;
            // (0.99.56) the mouse picks only once it has really moved (not a pointer that was just sitting where a button appeared)
            PreviewMouseMove += (s0, e0) =>
            {
                var pos = e0.GetPosition(this);
                if (!mouseSeen) { mouseSeen = true; mouseAt = pos; return; }
                if (Math.Abs(pos.X - mouseAt.X) + Math.Abs(pos.Y - mouseAt.Y) > 6) mouseMoved = true;
            };
            AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnPadKey), true);
            AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler(OnPadKey), true);

            var root = new Grid { Margin = new Thickness(56, 34, 56, 26) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel { Margin = new Thickness(4, 0, 0, 16) };
            title = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = OverlayTheme.TextMain };
            title.Text = (video ? "Clip" : "Screenshot") + (g != null ? "  ·  " + g.Name : "");
            sub = new TextBlock { FontSize = 16, Foreground = OverlayTheme.TextDim, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            head.Children.Add(title);
            head.Children.Add(sub);
            root.Children.Add(head);

            host = new Grid { ClipToBounds = true };
            Grid.SetRow(host, 1);
            root.Children.Add(host);
            cropLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            for (int i = 0; i < 4; i++) { shade[i] = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)) }; cropLayer.Children.Add(shade[i]); }
            frame = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 6, 3 } };
            cropLayer.Children.Add(frame);

            progressRow = new Grid { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Visibility = video ? Visibility.Visible : Visibility.Collapsed };
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var track = new Grid { Width = 1000, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
            progressFill = new Border { CornerRadius = new CornerRadius(3), Background = theme.Gradient, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Children.Add(progressFill);
            progressRow.Children.Add(track);
            timeText = new TextBlock { FontSize = 16, Foreground = OverlayTheme.TextSub, Margin = new Thickness(16, 0, 0, 0), MinWidth = 110, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(timeText, 1);
            progressRow.Children.Add(timeText);
            Grid.SetRow(progressRow, 2);
            root.Children.Add(progressRow);

            var foot = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var pill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(36), Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x18, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
            };
            bar = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Child = bar;
            foot.Children.Add(pill);
            label = new TextBlock { FontSize = 18, Foreground = OverlayTheme.TextMain, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Height = 26 };
            foot.Children.Add(label);
            status = new TextBlock { FontSize = 17, Foreground = OverlayTheme.TextSub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Height = 24 };
            foot.Children.Add(status);
            hint = new TextBlock { FontSize = 16, Foreground = OverlayTheme.TextDim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            foot.Children.Add(hint);
            Grid.SetRow(foot, 3);
            root.Children.Add(foot);
            Content = root;

            BuildMedia();
            BuildButtons();

            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            tick.Tick += (s, e) => Tick();
            tick.Start();
            Closed += (s, e) => { tick.Stop(); StopMedia(); };

            root.Opacity = 0;
            Loaded += (s, e) => root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        public void ShowOnTop()
        {
            Show();
            Activate();
            try { Native.ForceForegroundSoon(handle); } catch (Exception) { }
            Focus();
        }

        // ---------------- the picture / the clip ----------------

        private void BuildMedia()
        {
            string info = "";
            try { var fi = new FileInfo(path); info = fi.LastWriteTime.ToString("d MMM yyyy, HH:mm") + "  ·  " + Capture.DriveSize(fi.Length) + "  ·  " + fi.Name; } catch (Exception) { }
            if (!video)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;   // (read whole: the file isn't held open, so it can be deleted)
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.UriSource = new Uri(path);
                    bi.DecodePixelWidth = (int)Math.Max(1280, Math.Min(3840, Width * Capture.DpiScale()));   // (the screen's size, not more)
                    bi.EndInit();
                    bi.Freeze();
                    bitmap = bi;
                    info = bi.PixelWidth + " × " + bi.PixelHeight + "  ·  " + info;
                }
                catch (Exception ex) { status.Text = "This picture couldn't be read: " + ex.Message; }
                image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                host.Children.Add(image);
                host.Children.Add(cropLayer);
                host.SizeChanged += (s, e) => PlaceCrop();
            }
            else
            {
                media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = 0.8 };
                media.MediaOpened += (s, e) => { try { media.Play(); playing = true; UpdateButtons(); } catch (Exception) { } };
                media.MediaEnded += (s, e) => { playing = false; try { media.Pause(); media.Position = TimeSpan.Zero; } catch (Exception) { } UpdateButtons(); };
                media.MediaFailed += (s, e) => { status.Text = "This clip can't be played here (Windows may need a codec for it)."; };
                media.Source = new Uri(path);
                host.Children.Add(media);
            }
            sub.Text = info;
        }

        private void StopMedia()
        {
            if (media == null) return;
            try { media.Stop(); media.Close(); media.Source = null; } catch (Exception) { }
        }

        // ---------------- the bar ----------------

        private void BuildButtons()
        {
            buttons.Clear();
            bar.Children.Clear();
            if (cropping)
            {
                Add("\uE73E", "Save the cropped copy", SaveCrop, null);
                Add("\uE799", null, () => { ratio = (ratio + 1) % Ratios.Length; ClampCrop(); PlaceCrop(); UpdateButtons(); }, () => "Shape: " + RatioNames[ratio]);
                Add("\uE711", "Cancel cropping", EndCrop, null);
                bool ps0 = plugin.Poller != null && plugin.Poller.Kind == PadKind.PlayStation;
                hint.Text = "D-pad / stick  Move        LB / RB  Smaller / bigger        " + (ps0 ? "△" : "Y") + "  Shape        " + (ps0 ? "✕" : "A") + "  Save        " + (ps0 ? "○" : "B") + "  Cancel";
            }
            else
            {
                if (video)
                {
                    Add("\uE892", "Back 10 seconds", () => Seek(-10), null);
                    Add("\uE768", null, TogglePlay, () => playing ? "Pause" : "Play");
                    Add("\uE893", "Forward 10 seconds", () => Seek(10), null);
                    Add("\uE722", "Save this frame as a photo", SaveFrame, null);
                }
                else Add("\uE7A8", "Crop", StartCrop, null);
                Add("\uE74D", "Delete (to the Recycle Bin)", Delete, null);
                Add("\uE73E", "Done: back to the game", Done, null);
                bool ps = plugin.Poller != null && plugin.Poller.Kind == PadKind.PlayStation;
                hint.Text = "◀ ▶  Choose        " + (ps ? "✕" : "A") + "  Select        " + (video ? "LB / RB  Back / forward 10 s        " : "") + (ps ? "○" : "B") + "  Back to the game";
            }
            index = Math.Min(index, buttons.Count - 1);
            if (index < 0) index = 0;
            UpdateButtons();
        }

        private void Add(string glyph, string text, Action act, Func<string> dynamicText)
        {
            int i = buttons.Count;
            var g = new TextBlock { Text = glyph, FontFamily = OverlayWindow.IconFont, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(30), Margin = new Thickness(3, 0, 3, 0), Child = g, Cursor = Cursors.Hand, BorderThickness = new Thickness(2) };
            b.MouseEnter += (s, e) => { if (mouseMoved) { index = i; UpdateButtons(); } };
            b.MouseLeftButtonUp += (s, e) => { index = i; Press(); };
            buttons.Add(Tuple.Create(b, text, act, dynamicText));
            bar.Children.Add(b);
        }

        private void UpdateButtons()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                bool sel = i == index;
                var b = buttons[i].Item1;
                b.Background = sel ? theme.Gradient : new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
                b.BorderBrush = sel ? Brushes.White : Brushes.Transparent;
                var tb = (TextBlock)b.Child;
                tb.Foreground = sel ? new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)) : (Brush)OverlayTheme.TextMain;
                if (video && !cropping && i == 1) tb.Text = playing ? "\uE769" : "\uE768";
            }
            if (index >= 0 && index < buttons.Count)
            {
                var t = buttons[index];
                label.Text = t.Item4 != null ? t.Item4() : t.Item2;
            }
        }

        private void Press()
        {
            if (busy || index < 0 || index >= buttons.Count) return;
            try { buttons[index].Item3(); } catch (Exception ex) { status.Text = ex.Message; }
        }

        // ---------------- input ----------------

        private string lastAction;
        private DateTime lastActionAt = DateTime.MinValue;

        private void Act(string a)
        {
            if (busy) return;
            if ((DateTime.Now - openedAt).TotalMilliseconds < 300) return;   // (the Guide press that opened it)
            if (a == lastAction && (DateTime.Now - lastActionAt).TotalMilliseconds < 150) return;   // (the same press, another way)
            lastAction = a;
            lastActionAt = DateTime.Now;
            if (cropping)
            {
                switch (a)
                {
                    case "left": MoveCrop(-1, 0); return;
                    case "right": MoveCrop(1, 0); return;
                    case "up": MoveCrop(0, -1); return;
                    case "down": MoveCrop(0, 1); return;
                    case "lb": SizeCrop(-1); return;
                    case "rb": SizeCrop(1); return;
                    case "y": ratio = (ratio + 1) % Ratios.Length; ClampCrop(); PlaceCrop(); UpdateButtons(); return;
                    case "pick": SaveCrop(); return;
                    case "back": EndCrop(); return;
                }
                return;
            }
            switch (a)
            {
                case "left": if (index > 0) { index--; UpdateButtons(); } break;
                case "right": if (index < buttons.Count - 1) { index++; UpdateButtons(); } break;
                case "pick": Press(); break;
                case "back": Done(); break;
                case "lb": if (video) Seek(-10); break;
                case "rb": if (video) Seek(10); break;
            }
        }

        private void OnPadKey(object sender, KeyEventArgs e)
        {
            if (e.GetType().Name != "GameControllerInputEventArgs") return;
            e.Handled = true;
            var bp = e.GetType().GetProperty("Button");
            var sp = e.GetType().GetProperty("ButtonState");
            object bv = bp != null ? bp.GetValue(e, null) : null, sv = sp != null ? sp.GetValue(e, null) : null;
            bool down = sv == null || sv.ToString() == "Pressed";
            string n = bv != null ? bv.ToString() : "";
            ushort bit = 0;
            if (n == "DPadLeft" || n == "LeftStickLeft") bit = Btn.Left;
            else if (n == "DPadRight" || n == "LeftStickRight") bit = Btn.Right;
            else if (n == "DPadUp" || n == "LeftStickUp") bit = Btn.Up;
            else if (n == "DPadDown" || n == "LeftStickDown") bit = Btn.Down;
            if (bit != 0) { if (down) { if ((held & bit) == 0) heldSince = DateTime.Now; held |= bit; } else held &= (ushort)~bit; }
            if (!down) return;
            switch (n)
            {
                case "DPadLeft": case "LeftStickLeft": Act("left"); break;
                case "DPadRight": case "LeftStickRight": Act("right"); break;
                case "DPadUp": case "LeftStickUp": Act("up"); break;
                case "DPadDown": case "LeftStickDown": Act("down"); break;
                case "LeftShoulder": Act("lb"); break;
                case "RightShoulder": Act("rb"); break;
                case "Y": Act("y"); break;
                case "A": Act("pick"); break;
                case "B": case "Guide": Act("back"); break;
            }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (OnPadKey)
            switch (e.Key)
            {
                case Key.Left: Act("left"); e.Handled = true; break;
                case Key.Right: Act("right"); e.Handled = true; break;
                case Key.Up: Act("up"); e.Handled = true; break;
                case Key.Down: Act("down"); e.Handled = true; break;
                case Key.PageUp: Act("lb"); e.Handled = true; break;
                case Key.PageDown: Act("rb"); e.Handled = true; break;
                case Key.Enter: case Key.Space: if (!e.IsRepeat) Act("pick"); e.Handled = true; break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat) Act("back"); e.Handled = true; break;
            }
        }

        /// <summary>The overlay's own controller reading: presses, and what's held (a held direction keeps moving the crop).</summary>
        public void HandlePad(ushort pressed, ushort nowHeld)
        {
            if ((nowHeld & Btn.Directions) != (held & Btn.Directions) && (nowHeld & Btn.Directions) != 0) heldSince = DateTime.Now;
            held = nowHeld;
            if (busy || pressed == 0) return;
            if ((pressed & Btn.Left) != 0) Act("left");
            else if ((pressed & Btn.Right) != 0) Act("right");
            else if ((pressed & Btn.Up) != 0) Act("up");
            else if ((pressed & Btn.Down) != 0) Act("down");
            else if ((pressed & Btn.LB) != 0) Act("lb");
            else if ((pressed & Btn.RB) != 0) Act("rb");
            else if ((pressed & Btn.Y) != 0) Act("y");
            else if ((pressed & Btn.A) != 0) Act("pick");
            else if ((pressed & (Btn.B | Btn.Guide)) != 0) Act("back");
        }

        private void Tick()
        {
            if (video && media != null)
            {
                try
                {
                    double len = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : 0, pos = media.Position.TotalSeconds;
                    progressFill.Width = len > 0 ? Math.Max(0, Math.Min(1, pos / len)) * 1000 : 0;
                    timeText.Text = len > 0 ? Clock(pos) + " / " + Clock(len) : "";
                }
                catch (Exception) { }
            }
            // a direction held while cropping keeps the frame moving (after a moment)
            if (cropping && (held & Btn.Directions) != 0 && (DateTime.Now - heldSince).TotalMilliseconds > 280)
            {
                int dx = (held & Btn.Left) != 0 ? -1 : (held & Btn.Right) != 0 ? 1 : 0;
                int dy = (held & Btn.Up) != 0 ? -1 : (held & Btn.Down) != 0 ? 1 : 0;
                MoveCrop(dx * 0.4, dy * 0.4);
            }
        }

        private static string Clock(double s)
        {
            int t = (int)Math.Max(0, s);
            return (t / 60) + ":" + (t % 60).ToString("00");
        }

        // ---------------- the clip ----------------

        private void TogglePlay()
        {
            if (media == null) return;
            try { if (playing) media.Pause(); else media.Play(); playing = !playing; } catch (Exception) { }
            UpdateButtons();
        }

        private void Seek(int seconds)
        {
            if (media == null) return;
            try
            {
                double len = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : 0;
                double to = Math.Max(0, media.Position.TotalSeconds + seconds);
                if (len > 0) to = Math.Min(len - 0.2, to);
                media.Position = TimeSpan.FromSeconds(to);
            }
            catch (Exception) { }
        }

        private void SaveFrame()
        {
            if (media == null) return;
            try
            {
                int w = media.NaturalVideoWidth, h = media.NaturalVideoHeight;
                if (w <= 0 || h <= 0) { w = (int)media.ActualWidth; h = (int)media.ActualHeight; }
                if (w <= 0 || h <= 0) return;
                int sec = (int)media.Position.TotalSeconds;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(media) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string file = FreeName(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileNameWithoutExtension(path) + " frame " + (sec / 60) + "m" + (sec % 60).ToString("00") + "s.png"));
                SavePng(rtb, file);
                Saved(file, "This frame is saved as a photo");
            }
            catch (Exception ex) { status.Text = "That frame couldn't be saved: " + ex.Message; }
        }

        // ---------------- cropping ----------------

        private void StartCrop()
        {
            if (bitmap == null) return;
            cropping = true;
            cropScale = 0.8; cropCx = 0.5; cropCy = 0.5;
            ratio = 0;
            double ar = bitmap.PixelWidth / (double)Math.Max(1, bitmap.PixelHeight);
            for (int i = 0; i < Ratios.Length; i++) if (Math.Abs(Ratios[i] - ar) < 0.02) { ratio = i; break; }
            cropLayer.Visibility = Visibility.Visible;
            index = 0;
            BuildButtons();
            ClampCrop();
            PlaceCrop();
            status.Text = "";
        }

        private void EndCrop()
        {
            cropping = false;
            cropLayer.Visibility = Visibility.Collapsed;
            index = 0;
            BuildButtons();
        }

        /// <summary>The crop in the picture's own pixels.</summary>
        private Int32Rect CropRect()
        {
            int W = bitmap.PixelWidth, H = bitmap.PixelHeight;
            double a = Ratios[ratio];
            double mw = W / (double)H > a ? H * a : W, mh = W / (double)H > a ? H : W / a;
            double w = Math.Max(16, mw * cropScale), h = Math.Max(16, mh * cropScale);
            double x = cropCx * W - w / 2, y = cropCy * H - h / 2;
            x = Math.Max(0, Math.Min(W - w, x)); y = Math.Max(0, Math.Min(H - h, y));
            return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(Math.Min(w, W)), (int)Math.Round(Math.Min(h, H)));
        }

        private void ClampCrop()
        {
            if (bitmap == null) return;
            var r = CropRect();
            cropCx = (r.X + r.Width / 2.0) / bitmap.PixelWidth;
            cropCy = (r.Y + r.Height / 2.0) / bitmap.PixelHeight;
        }

        private void MoveCrop(double dx, double dy)
        {
            if (bitmap == null) return;
            cropCx += dx * 0.02;
            cropCy += dy * 0.02 * bitmap.PixelWidth / Math.Max(1.0, bitmap.PixelHeight);
            ClampCrop();
            PlaceCrop();
        }

        private void SizeCrop(int d)
        {
            cropScale = Math.Max(0.15, Math.Min(1, cropScale + d * 0.05));
            ClampCrop();
            PlaceCrop();
        }

        private void PlaceCrop()
        {
            if (!cropping || bitmap == null || host.ActualWidth <= 0) return;
            double W = bitmap.PixelWidth, H = bitmap.PixelHeight;
            double k = Math.Min(host.ActualWidth / W, host.ActualHeight / H);
            double ox = (host.ActualWidth - W * k) / 2, oy = (host.ActualHeight - H * k) / 2;
            var r = CropRect();
            double x = ox + r.X * k, y = oy + r.Y * k, w = r.Width * k, h = r.Height * k;
            double iw = W * k, ih = H * k;
            Put(shade[0], ox, oy, iw, y - oy);                       // above
            Put(shade[1], ox, y + h, iw, oy + ih - (y + h));         // below
            Put(shade[2], ox, y, x - ox, h);                         // left
            Put(shade[3], x + w, y, ox + iw - (x + w), h);           // right
            Put(frame, x, y, w, h);
            status.Text = "Crop  ·  " + RatioNames[ratio] + "  ·  " + r.Width + " × " + r.Height;
        }

        private static void Put(FrameworkElement e, double x, double y, double w, double h)
        {
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            e.Width = Math.Max(0, w); e.Height = Math.Max(0, h);
        }

        private void SaveCrop()
        {
            if (bitmap == null) return;
            try
            {
                var r = CropRect();
                // (0.99.55) the picture on screen is read at the screen's size: the crop is cut from the full picture
                BitmapSource full = bitmap;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bi.UriSource = new Uri(path); bi.EndInit();
                    full = bi;
                }
                catch (Exception) { }
                if (full.PixelWidth != bitmap.PixelWidth)
                {
                    double k = full.PixelWidth / (double)bitmap.PixelWidth;
                    r = new Int32Rect((int)(r.X * k), (int)(r.Y * k), Math.Min(full.PixelWidth - (int)(r.X * k), (int)(r.Width * k)), Math.Min(full.PixelHeight - (int)(r.Y * k), (int)(r.Height * k)));
                }
                var cut = new CroppedBitmap(full, r);
                string file = FreeName(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileNameWithoutExtension(path) + " crop.png"));
                SavePng(cut, file);
                EndCrop();
                Saved(file, "Cropped copy saved (the original is kept)");
            }
            catch (Exception ex) { status.Text = "The crop couldn't be saved: " + ex.Message; }
        }

        // ---------------- saving, deleting, closing ----------------

        private static void SavePng(BitmapSource src, string file)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        private void Saved(string file, string message)
        {
            status.Text = message + ":  " + System.IO.Path.GetFileName(file);
            try { if (OverlayPlugin.CaptureSaved != null) OverlayPlugin.CaptureSaved(file, game); } catch (Exception) { }   // (the gallery puts it with the game)
            try { if (plugin.Config != null && plugin.Config.ScreenshotSound) Chime.Play(); } catch (Exception) { }
        }

        private static string FreeName(string p)
        {
            if (!File.Exists(p)) return p;
            string dir = System.IO.Path.GetDirectoryName(p), stem = System.IO.Path.GetFileNameWithoutExtension(p), ext = System.IO.Path.GetExtension(p);
            for (int i = 2; i < 1000; i++) { string q = System.IO.Path.Combine(dir, stem + " (" + i + ")" + ext); if (!File.Exists(q)) return q; }
            return p;
        }

        private void Delete()
        {
            if (busy) return;
            busy = true;
            StopMedia();
            image = null;
            status.Text = "Moving it to the Recycle Bin…";
            string file = path;
            var d = Dispatcher;
            new Thread(() =>
            {
                Thread.Sleep(video ? 500 : 50);   // (the player lets go of the file a moment after it's closed)
                int skipped;
                bool ok = false;
                try { ok = KippixHelper.ShellMedia.Recycle(new[] { file }, out skipped); } catch (Exception) { }
                if (!ok && File.Exists(file)) { Thread.Sleep(800); try { ok = KippixHelper.ShellMedia.Recycle(new[] { file }, out skipped); } catch (Exception) { } }
                bool gone = !File.Exists(file);
                try
                {
                    d.BeginInvoke(new Action(() =>
                    {
                        busy = false;
                        Close(gone ? "Deleted" : "Couldn't delete it", gone ? "Moved to the Recycle Bin" : "It's in use, or there's no permission");
                    }));
                }
                catch (Exception) { }
            }) { IsBackground = true }.Start();
        }

        private void Done() { Close(null, null); }

        private void Close(string toastTitle, string toastText)
        {
            if (busy) return;
            busy = true;
            StopMedia();
            var content = Content as UIElement;
            var fade = new DoubleAnimation(content != null ? content.Opacity : 1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (s, e) =>
            {
                try { Close(); } catch (Exception) { }
                try { if (gameWindow != IntPtr.Zero) Native.ForceForegroundSoon(gameWindow); } catch (Exception) { }   // (back to the game)
                if (toastTitle != null) plugin.Toast(toastTitle, toastText);
            };
            if (content != null) content.BeginAnimation(OpacityProperty, fade);
            else { try { Close(); } catch (Exception) { } }
        }
    }
}

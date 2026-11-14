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

namespace KippixOverlay
{
    internal enum EntryKind { Row, Card, Header, Divider, Text }

    internal class MenuEntry
    {
        public EntryKind Kind = EntryKind.Row;
        public string Glyph;
        public Func<string> GlyphFunc;
        public string Title;
        public Func<string> TitleFunc;
        public Func<string> Sub;
        public string Label;            // small accent caps line above the title
        public string LabelAlert;       // red text appended to the label (e.g. "Missable!")
        public Action Activate;
        public Action<int> Adjust;      // left / right
        public bool Chevron;
        public bool Danger;
        public bool Dim;
        public bool Selectable = true;
        public string Image;
        public double ImageW = 40, ImageH = 40;
        public bool RoundImage;
        public UIElement Extra;         // under the text
        public UIElement Right;         // right-hand column
        public Func<bool> Dot;          // green dot after the title
        public Func<int> Badge;         // pink count bubble on the right
        public Action Refresh;          // called every second

        public Border Visual;
        public TextBlock TitleText, SubText, GlyphText, BadgeText;
        public FrameworkElement DotVisual, BadgeVisual;
    }

    internal class Page
    {
        public string Title;
        public Func<List<MenuEntry>> Build;
        public int Selected = -1;
        public Action OnLeave;
        public bool Live;               // rebuild when Discord state changes
    }

    /// <summary>A gradient progress bar.</summary>
    internal class Bar
    {
        public readonly Grid Root;
        private readonly Border fill;
        private double value;

        public Bar(OverlayTheme t, double height, double width)
        {
            Root = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
            if (width > 0) Root.Width = width;
            Root.Children.Add(new Border { Background = OverlayTheme.TrackBg, CornerRadius = new CornerRadius(height / 2) });
            fill = new Border { Background = t.Gradient, CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
            Root.Children.Add(fill);
            Root.SizeChanged += (s, e) => Apply();
        }

        public void Set(double v)
        {
            value = Math.Max(0, Math.Min(1, v));
            Apply();
        }

        private void Apply()
        {
            double w = Root.ActualWidth;
            if (w <= 0) return;
            fill.Width = value <= 0 ? 0 : Math.Max(Root.Height, w * value);
        }
    }

    internal partial class OverlayWindow : Window
    {
        public static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private static readonly string[] TabNames = { "Home", "Party", "Audio", "Settings" };
        private static readonly string[] TabGlyphs = { "\uE80F", "\uE716", "\uE767", "\uE713" };

        private readonly OverlayPlugin plugin;
        private OverlayTheme theme;

        private Grid root;
        private Rectangle backdrop;
        private bool mouseSeen, mouseMoved;
        private Point mouseAt;
        private readonly DateTime builtAt = DateTime.UtcNow;
        private double fullWidth;
        private Rect dimBounds;
        private OverlayDimWindow dim;
        private double PanelWidth { get { return Math.Max(560, Math.Min(720, fullWidth * 0.37)); } }

        private void ShowDim()
        {
            try
            {
                if (dim == null) dim = new OverlayDimWindow(dimBounds);
                dim.Show();
            }
            catch (Exception) { dim = null; }
        }

        private void CloseDim()
        {
            var d = dim;
            dim = null;
            if (d != null) try { d.Close(); } catch (Exception) { }
        }
        private Grid panel;
        private TranslateTransform panelShift;
        private StackPanel tabStrip;
        private TextBlock clockText;
        private Border pageHeader;
        private TextBlock pageTitle;
        private StackPanel list;
        private ScrollViewer scroller;
        private TextBlock statusText, sessionText, discordText, audioText;
        private FrameworkElement sessionItem, discordItem, batteryItem, audioItem;
        private StackPanel footer;

        private readonly List<MenuEntry> entries = new List<MenuEntry>();
        private readonly List<Page> stack = new List<Page>();
        private int selected = -1;
        private int tab;
        private DispatcherTimer ticker;
        private PadKind shownKind;
        private DateTime lastLiveRefresh = DateTime.MinValue;
        private bool liveRefreshQueued;

        public bool IsOpen { get; private set; }
        /// <summary>(0.99.72) How long each part of showing took (for the log).</summary>
        internal string ShowTimings { get; private set; }
        private readonly bool keepGameFocused;

        public OverlayWindow(OverlayPlugin plugin, int startTab)
        {
            this.plugin = plugin;
            theme = OverlayTheme.Get(plugin.Config.Theme);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "Kippix overlay";
            FontFamily = UiFont;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            var b = Capture.MonitorBounds(plugin.GameWindow);
            double scale = Capture.DpiScale();
            Left = b.Left / scale;
            Top = b.Top / scale;
            // (0.99.55) the window is only as wide as the panel (and its glowing edge); the screen behind is dimmed by a still
            // window of its own (OverlayDimWindow). A see-through window is redrawn whole, by the processor, every time anything
            // in it moves: the size of a 4K screen, every slide, fade and highlight stuttered (and took time from the game).
            fullWidth = b.Width / scale;
            Width = PanelWidth + 40;
            Height = b.Height / scale;
            dimBounds = new Rect(b.Left / scale, b.Top / scale, b.Width / scale, b.Height / scale);

            keepGameFocused = plugin.LetsGameRun;   // When the overlay opens: Let it keep running (or Automatic, for an online game)
            if (keepGameFocused)
            {
                // Never become the active window, so the game doesn't see itself lose focus and leave fullscreen (or pause): the
                // overlay only draws on top. Nothing is done to the game's process (it never was: no suspending, no injecting).
                ShowActivated = false;
                SourceInitialized += (s, e) =>
                {
                    IntPtr h = Handle;
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                    Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
                };
            }

            // the window's own system menu (Restore / Move / Size / Minimise / Close) never opens here: an Alt tap (used to
            // bring the overlay in front, or from Steam's controller keys) followed by a D-pad press opened it
            SourceInitialized += (s, e) =>
            {
                var src = System.Windows.Interop.HwndSource.FromHwnd(Handle);
                if (src != null) src.AddHook(NoSystemMenu);
            };

            BuildChrome();
            SwitchTab(startTab);
            PreviewKeyDown += OnKey;
            // (0.99.56) the highlight follows the mouse only once it has really moved: the panel sliding in under a pointer
            // that was just sitting there (or the window appearing under it) "entered" a row, and the highlight opened on that
            // row (Achievements) instead of the game at the top
            PreviewMouseMove += (s, e) =>
            {
                var pos = e.GetPosition(this);
                if (!mouseSeen) { mouseSeen = true; mouseAt = pos; return; }
                if (Math.Abs(pos.X - mouseAt.X) + Math.Abs(pos.Y - mouseAt.Y) > 6 && (DateTime.UtcNow - builtAt).TotalMilliseconds > 400) mouseMoved = true;
            };
            AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnPlaynitePadKey), true);
            AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler(OnPlaynitePadKeyUp), true);
            // Steam turns the Steam Controller's triggers into mouse clicks too: a click while a trigger is pressed (or just
            // let go) is that, not you clicking (it closed the overlay when it landed beside the panel)
            AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnTriggerClick), true);
            AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnTriggerClick), true);
            PreviewTextInput += (s, e) =>
            {
                if (plugin.ComposeOpen) { plugin.ComposeType(e.Text); e.Handled = true; }
            };
        }

        internal static IntPtr NoSystemMenu(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112, WM_CONTEXTMENU = 0x007B;
            if (msg == WM_SYSCOMMAND)
            {
                int cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd == 0xF100 || cmd == 0xF090) { handled = true; return IntPtr.Zero; }   // SC_KEYMENU, SC_MOUSEMENU
            }
            if (msg == WM_CONTEXTMENU) { handled = true; return IntPtr.Zero; }
            return IntPtr.Zero;
        }

        public IntPtr Handle
        {
            get { return new System.Windows.Interop.WindowInteropHelper(this).Handle; }
        }

        // ================= Chrome =================

        private void BuildChrome()
        {
            double panelWidth = PanelWidth;

            root = new Grid();
            backdrop = new Rectangle { Fill = Brushes.Transparent, Opacity = 0, Visibility = Visibility.Collapsed };   // (0.99.55: the dimming is OverlayDimWindow)
            backdrop.MouseLeftButtonUp += (s, e) => plugin.Resume();
            root.Children.Add(backdrop);

            panelShift = new TranslateTransform(-50, 0);
            panel = new Grid
            {
                Width = panelWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = theme.Panel,
                RenderTransform = panelShift,
                Opacity = 0
            };
            root.Children.Add(panel);

            var edge = new Rectangle
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Right,
                Fill = theme.GradientV,
                Effect = theme.Glow(0.9)
            };
            panel.Children.Add(edge);

            var dock = new DockPanel { Margin = new Thickness(30, 30, 34, 20), LastChildFill = true };
            panel.Children.Add(dock);

            // Tabs centred across the panel; the clock sits on the right without pushing them off centre.
            var top = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            tabStrip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            top.Children.Add(tabStrip);
            clockText = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = OverlayTheme.TextMain, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            top.Children.Add(clockText);
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);

            var hdr = new StackPanel { Orientation = Orientation.Horizontal };
            hdr.Children.Add(new TextBlock { Text = "\uE72B", FontFamily = IconFont, FontSize = 18, Foreground = OverlayTheme.TextSub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            pageTitle = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = OverlayTheme.TextMain, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = panelWidth - 120 };
            hdr.Children.Add(pageTitle);
            pageHeader = new Border { Child = hdr, Margin = new Thickness(8, 0, 0, 14), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            pageHeader.MouseLeftButtonUp += (s, e) => Back();
            DockPanel.SetDock(pageHeader, Dock.Top);
            dock.Children.Add(pageHeader);

            // Bottom bar: session time on the left; sound output, Discord and controller battery on the right. Docked
            // under the list (the list gets the height that's left, nothing is covered); it only shows status, so it
            // never takes focus or clicks.
            footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Focusable = false, IsHitTestVisible = false };
            footer.Children.Add(new Border { Height = 1.5, Background = theme.Gradient, Opacity = 0.55, Margin = new Thickness(0, 0, 0, 14) });
            var footRow = new DockPanel { LastChildFill = true };
            sessionItem = BarItem("\uE916", out sessionText);
            sessionItem.Margin = new Thickness(0, 0, 18, 0);
            DockPanel.SetDock(sessionItem, Dock.Left);
            footRow.Children.Add(sessionItem);
            batteryItem = BarItem("\uE7FC", out statusText);
            DockPanel.SetDock(batteryItem, Dock.Right);
            footRow.Children.Add(batteryItem);
            discordItem = BarItem("\uE8BD", out discordText);   // (the Party tab's Discord glyph)
            DockPanel.SetDock(discordItem, Dock.Right);
            footRow.Children.Add(discordItem);
            // the sound output takes the room that's left and shortens a long device name with "..."
            audioItem = BarItem("\uE767", out audioText);
            audioItem.HorizontalAlignment = HorizontalAlignment.Right;
            footRow.Children.Add(audioItem);
            footer.Children.Add(footRow);
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            list = new StackPanel { Margin = new Thickness(0, 4, 4, 12) };
            scroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Padding = new Thickness(0, 6, 0, 0)
            };
            dock.Children.Add(scroller);

            Content = root;
        }

        private void BuildTabStrip()
        {
            tabStrip.Children.Clear();
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                bool active = i == tab;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = TabGlyphs[i],
                    FontFamily = IconFont,
                    FontSize = 24,
                    Foreground = active ? theme.Accent : OverlayTheme.TextMain,
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (active)
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = TabNames[i],
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = OverlayTheme.TextMain,
                        Margin = new Thickness(10, -2, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                var pill = new Border
                {
                    Background = active ? theme.Soft : Brushes.Transparent,
                    BorderBrush = active ? theme.Gradient : Brushes.Transparent,
                    BorderThickness = new Thickness(2.5),
                    CornerRadius = new CornerRadius(26),
                    Padding = new Thickness(active ? 18 : 12, 9, active ? 24 : 12, 9),
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, i == TabNames.Length - 1 ? 0 : 4, 0),
                    Cursor = Cursors.Hand,
                    Effect = active ? theme.Glow(0.6) : null,
                    Child = sp
                };
                pill.MouseLeftButtonUp += (s, e) => SwitchTab(index);
                tabStrip.Children.Add(pill);
            }
        }

        private void BuildFooter()
        {
            // Button hints were removed from the bottom bar; it now shows live status (see UpdateClock).
            shownKind = plugin.Poller != null ? plugin.Poller.Kind : PadKind.Xbox;
        }

        /// <summary>(0.99.44) The glyph the overlay uses for Discord (Segoe's speech bubble): drawn as the user's own Discord icon.</summary>
        internal const string DiscordGlyph = "\uE8BD";
        private static Geometry discordGeo;

        /// <summary>
        /// (0.99.44) Puts a glyph in an icon TextBlock; the Discord one becomes the user's Discord icon (a filled shape the
        /// TextBlock's colour, so selection colours still apply), every other one stays a font glyph.
        /// </summary>
        internal static void SetGlyph(TextBlock tb, string glyph)
        {
            if (tb == null) return;
            if (glyph != DiscordGlyph)
            {
                if (tb.Inlines.Count > 0 && tb.Tag as string == "discord") { tb.Inlines.Clear(); tb.Tag = null; }
                tb.Text = glyph ?? "";
                return;
            }
            if (tb.Tag as string == "discord") return;
            if (discordGeo == null)
            {
                discordGeo = Geometry.Parse(DiscordGeometry);
                discordGeo.Freeze();
            }
            double size = tb.FontSize;
            var path = new System.Windows.Shapes.Path { Data = discordGeo, Stretch = Stretch.Uniform, Width = size * 1.05, Height = size * 0.85 };
            path.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = tb });
            tb.Text = "";
            tb.Inlines.Clear();
            tb.Inlines.Add(new System.Windows.Documents.InlineUIContainer(path) { BaselineAlignment = BaselineAlignment.Center });
            tb.Tag = "discord";
        }

        /// <summary>The user's Discord icon, traced from the picture they sent (same as the theme's KX_IconDiscord).</summary>
        private const string DiscordGeometry = "F0 M17.55,17.71 L17.19,17.69 L16.96,17.56 L16.79,17.39 L15.87,16.08 L15.65,15.87 L15.49,15.81 L13.85,16.07 L12.02,16.15 L10.17,16.08 L8.48,15.81 L8.32,15.89 L8.13,16.08 L7.21,17.39 L6.97,17.62 L6.65,17.72 L5.89,17.66 L5.01,17.49 L3.86,17.14 L2.23,16.43 L1.34,15.85 L0.55,15.09 L0.26,14.74 L0.06,14.37 L-0.01,13.85 L0.07,12.08 L0.32,10.18 L0.59,8.87 L1.14,6.71 L1.64,5.17 L2.10,3.96 L2.87,2.42 L3.37,1.88 L4.22,1.31 L5.21,0.84 L5.93,0.57 L7.04,0.25 L8.05,0.06 L8.81,-0.00 L9.14,0.08 L9.35,0.33 L9.59,1.02 L9.79,1.19 L10.97,1.07 L12.02,1.04 L13.06,1.07 L14.14,1.20 L14.28,1.17 L14.39,1.05 L14.63,0.36 L14.74,0.20 L14.96,0.03 L15.26,-0.00 L15.88,0.05 L16.86,0.23 L18.12,0.59 L19.22,1.03 L19.78,1.31 L20.56,1.82 L20.86,2.10 L21.11,2.39 L21.35,2.81 L21.90,3.96 L22.64,5.99 L23.25,8.15 L23.67,10.17 L23.93,12.08 L24.00,13.20 L23.98,14.24 L23.72,14.77 L23.21,15.34 L22.62,15.87 L21.82,16.40 L20.79,16.89 L19.42,17.37 L18.24,17.64 Z M17.97,16.63 L18.76,16.49 L19.89,16.14 L21.02,15.64 L21.84,15.12 L22.18,14.83 L22.70,14.31 L22.92,14.01 L22.97,13.85 L22.95,12.80 L22.80,11.39 L22.65,10.31 L22.24,8.35 L21.74,6.55 L21.18,4.91 L20.56,3.57 L20.33,3.14 L19.99,2.72 L19.12,2.13 L17.91,1.60 L16.34,1.19 L15.75,1.13 L15.62,1.16 L15.54,1.24 L15.54,1.38 L15.62,1.46 L17.48,2.08 L18.24,2.42 L19.18,2.95 L19.30,3.08 L19.38,3.27 L19.38,3.47 L19.29,3.67 L19.06,3.82 L18.70,3.81 L17.45,3.17 L16.76,2.89 L15.91,2.61 L14.57,2.28 L13.20,2.09 L11.98,2.05 L10.74,2.10 L9.07,2.36 L8.05,2.62 L7.24,2.89 L6.55,3.17 L5.30,3.81 L4.94,3.82 L4.71,3.68 L4.62,3.47 L4.62,3.27 L4.70,3.08 L4.82,2.95 L5.76,2.42 L6.52,2.08 L8.41,1.44 L8.47,1.34 L8.44,1.21 L8.19,1.12 L7.11,1.32 L6.08,1.60 L4.88,2.13 L4.01,2.72 L3.67,3.14 L3.44,3.57 L2.82,4.93 L2.12,7.01 L1.58,9.17 L1.34,10.38 L1.11,12.08 L1.01,13.65 L1.08,14.01 L1.57,14.60 L2.10,15.07 L2.46,15.32 L2.98,15.64 L3.60,15.94 L4.93,16.40 L6.29,16.66 L6.45,16.60 L6.74,16.27 L7.17,15.65 L7.18,15.49 L7.04,15.38 L6.25,15.09 L5.21,14.58 L4.22,13.94 L3.99,13.72 L3.90,13.49 L3.96,13.20 L4.13,13.03 L4.39,12.95 L4.68,13.02 L5.86,13.77 L7.01,14.30 L7.86,14.59 L8.86,14.83 L10.05,15.03 L12.02,15.18 L13.56,15.08 L14.70,14.94 L16.11,14.59 L17.62,14.03 L18.60,13.51 L19.32,13.02 L19.58,12.95 L19.81,13.01 L20.01,13.18 L20.06,13.46 L20.01,13.70 L19.81,13.92 L19.29,14.30 L18.79,14.58 L17.75,15.09 L16.96,15.38 L16.82,15.49 L16.85,15.68 L17.52,16.57 L17.68,16.66 Z M8.74,12.48 L7.99,12.41 L7.53,12.21 L7.14,11.95 L6.82,11.62 L6.55,11.18 L6.36,10.67 L6.30,10.31 L6.28,9.69 L6.35,9.20 L6.54,8.64 L6.80,8.22 L7.11,7.88 L7.46,7.63 L7.96,7.41 L8.28,7.34 L8.81,7.34 L9.20,7.40 L9.66,7.59 L10.06,7.86 L10.37,8.19 L10.64,8.63 L10.83,9.17 L10.89,9.53 L10.90,10.15 L10.84,10.61 L10.64,11.17 L10.37,11.62 L10.05,11.95 L9.63,12.23 L9.17,12.42 Z M15.55,12.48 L14.80,12.41 L14.34,12.21 L13.95,11.95 L13.64,11.62 L13.36,11.18 L13.17,10.67 L13.11,10.31 L13.10,9.69 L13.16,9.20 L13.35,8.64 L13.61,8.22 L13.92,7.88 L14.28,7.63 L14.77,7.41 L15.09,7.34 L15.62,7.34 L16.01,7.40 L16.47,7.59 L16.87,7.86 L17.18,8.19 L17.45,8.63 L17.64,9.17 L17.70,9.53 L17.71,10.15 L17.65,10.61 L17.45,11.17 L17.18,11.62 L16.86,11.95 L16.44,12.23 L15.98,12.42 Z M8.77,11.46 L9.00,11.40 L9.30,11.21 L9.63,10.86 L9.86,10.35 L9.89,9.63 L9.82,9.33 L9.66,9.00 L9.36,8.64 L8.93,8.38 L8.41,8.35 L8.19,8.41 L7.89,8.60 L7.56,8.95 L7.33,9.46 L7.31,10.22 L7.37,10.48 L7.55,10.84 L7.83,11.17 L8.26,11.43 Z M15.58,11.46 L15.81,11.40 L16.11,11.21 L16.44,10.86 L16.67,10.35 L16.70,9.63 L16.63,9.33 L16.47,9.00 L16.17,8.64 L15.74,8.38 L15.23,8.35 L15.00,8.41 L14.70,8.60 L14.37,8.95 L14.14,9.46 L14.12,10.22 L14.18,10.48 L14.36,10.84 L14.64,11.17 L15.07,11.43 Z";

        private DockPanel BarItem(string glyph, out TextBlock text)
        {
            // a DockPanel rather than a StackPanel, so the text gets a real width and can shorten with "..."
            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), Visibility = Visibility.Collapsed };
            var icon = new TextBlock { FontFamily = IconFont, FontSize = 18, Foreground = OverlayTheme.TextSub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            SetGlyph(icon, glyph);
            DockPanel.SetDock(icon, Dock.Left);
            dp.Children.Add(icon);
            text = new TextBlock { FontSize = 16, Foreground = OverlayTheme.TextSub, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            dp.Children.Add(text);
            return dp;
        }

        private static void SetBarItem(FrameworkElement item, TextBlock text, string value)
        {
            text.Text = value ?? "";
            item.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================= Button hints (also used by the popups) =================

        private static void ButtonStyle(PadKind kind, ushort button, out string symbol, out Color color, out bool pill)
        {
            pill = false;
            bool ps = kind == PadKind.PlayStation;
            switch (button)
            {
                case Btn.A: symbol = ps ? "\u2715" : "A"; color = ps ? Color.FromRgb(0x7A, 0xA7, 0xFF) : Color.FromRgb(0x5D, 0xD2, 0x3C); break;
                case Btn.B: symbol = ps ? "\u25CB" : "B"; color = ps ? Color.FromRgb(0xFF, 0x6B, 0x8A) : Color.FromRgb(0xFF, 0x5A, 0x5A); break;
                case Btn.X: symbol = ps ? "\u25A1" : "X"; color = ps ? Color.FromRgb(0xF0, 0x8B, 0xFF) : Color.FromRgb(0x4A, 0x9B, 0xFF); break;
                case Btn.Y: symbol = ps ? "\u25B3" : "Y"; color = ps ? Color.FromRgb(0x3E, 0xE6, 0xA7) : Color.FromRgb(0xFF, 0xD0, 0x3A); break;
                case Btn.LB: symbol = ps ? "L1" : "LB"; color = Color.FromRgb(0xB8, 0xB0, 0xFF); pill = true; break;
                case Btn.RB: symbol = ps ? "R1" : "RB"; color = Color.FromRgb(0xB8, 0xB0, 0xFF); pill = true; break;
                case Btn.Start: symbol = ps ? "OPTIONS" : "MENU"; color = Color.FromRgb(0xB8, 0xB0, 0xFF); pill = true; break;
                case Btn.Guide: symbol = ps ? "PS" : "HOME"; color = Color.FromRgb(0xB8, 0xB0, 0xFF); pill = true; break;
                default: symbol = "?"; color = Colors.White; break;
            }
        }

        private static UIElement ButtonGlyph(PadKind kind, ushort button)
        {
            string symbol;
            Color color;
            bool pill;
            ButtonStyle(kind, button, out symbol, out color, out pill);
            return new Border
            {
                Height = 32,
                MinWidth = 32,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(2.5),
                BorderBrush = new SolidColorBrush(color),
                Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
                Padding = new Thickness(pill ? 7 : 0, 0, pill ? 7 : 0, 0),
                Margin = new Thickness(0, 0, 5, 0),
                Child = new TextBlock
                {
                    Text = symbol,
                    FontSize = pill ? 12 : 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, pill ? 0 : -2, 0, 0)
                }
            };
        }

        public static UIElement ButtonHint(PadKind kind, ushort button, string label)
        {
            return ButtonHints(kind, new[] { button }, label);
        }

        public static UIElement ButtonHints(PadKind kind, ushort[] buttons, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (ushort b in buttons) sp.Children.Add(ButtonGlyph(kind, b));
            sp.Children.Add(new TextBlock { Text = label, FontSize = 17, Foreground = OverlayTheme.TextMain, Margin = new Thickness(5, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        public static UIElement TextHint(string control, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(2),
                BorderBrush = OverlayTheme.TextSub,
                Padding = new Thickness(10, 0, 10, 0),
                Child = new TextBlock { Text = control, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = OverlayTheme.TextMain, VerticalAlignment = VerticalAlignment.Center }
            });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 17, Foreground = OverlayTheme.TextMain, Margin = new Thickness(8, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        // ================= Page stack =================

        private void SwitchTab(int newTab)
        {
            tab = OverlayConfig.Wrap(newTab, TabNames.Length);
            LeavePages(0);
            Func<List<MenuEntry>> build;
            switch (tab)
            {
                case 0: build = HomeEntries; break;
                case 1: build = PartyEntries; break;
                case 2: build = AudioEntries; break;
                default: build = SettingsEntries; break;
            }
            stack.Add(new Page { Build = build, Live = tab == 1 });
            BuildTabStrip();
            ShowTop();
        }

        /// <summary>Removes pages above the given depth, running their clean-up.</summary>
        private void LeavePages(int depth)
        {
            while (stack.Count > depth)
            {
                Page p = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (p.OnLeave != null) { try { p.OnLeave(); } catch (Exception) { } }
            }
        }

        private Page Push(string title, Func<List<MenuEntry>> build)
        {
            stack[stack.Count - 1].Selected = selected;
            var p = new Page { Title = title, Build = build };
            stack.Add(p);
            ShowTop();
            return p;
        }

        private void Back()
        {
            if (plugin.ComposeOpen) { plugin.CloseCompose(); return; }
            if (stack.Count > 1)
            {
                LeavePages(stack.Count - 1);
                ShowTop();
            }
            else plugin.Resume();
        }

        private void PopTo(int depth)
        {
            LeavePages(Math.Max(1, depth));
            ShowTop();
        }

        private Page TopPage
        {
            get { return stack.Count > 0 ? stack[stack.Count - 1] : null; }
        }

        private void ShowTop()
        {
            Page p = TopPage;
            if (p == null) return;
            pageHeader.Visibility = stack.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            pageTitle.Text = p.Title ?? "";
            List<MenuEntry> built;
            try { built = p.Build(); }
            catch (Exception ex)
            {
                built = new List<MenuEntry> { new MenuEntry { Kind = EntryKind.Card, Glyph = "\uE783", Title = "Something went wrong", Sub = () => ex.Message, Selectable = false } };
                plugin.LogError(ex, "Building page failed");
            }
            liveSignature = plugin.DiscordSignature();
            Render(built, p.Selected);
        }

        /// <summary>Rebuilds the current page, keeping the selection.</summary>
        private void Rebuild()
        {
            Page p = TopPage;
            if (p == null) return;
            p.Selected = selected;
            double offset = scroller.VerticalOffset;
            ShowTop();
            scroller.ScrollToVerticalOffset(offset);
        }

        /// <summary>Rebuilds a page if it is still on screen (after async loads).</summary>
        private void RefreshPage(Page p)
        {
            if (IsOpen && p != null && TopPage == p) Rebuild();
        }

        private string liveSignature;

        /// <summary>Called by the plugin (UI thread) when Discord state changes.</summary>
        public void OnLiveDataChanged()
        {
            if (!IsOpen) return;
            Page p = TopPage;
            if (p == null || !p.Live) return;

            // Small changes (someone speaking) only refresh text and dots; bigger ones rebuild the page.
            string sig = plugin.DiscordSignature();
            if (sig == liveSignature || liveRefreshQueued)
            {
                RefreshEntries();
                return;
            }
            double wait = 400 - (DateTime.Now - lastLiveRefresh).TotalMilliseconds;
            liveRefreshQueued = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(30, wait)) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                liveRefreshQueued = false;
                lastLiveRefresh = DateTime.Now;
                liveSignature = plugin.DiscordSignature();
                if (TopPage == p) Rebuild();
            };
            t.Start();
        }

        /// <summary>Runs work on a background thread, then continues on the UI thread if the overlay is still open.</summary>
        private void Async<T>(Func<T> work, Action<T> done, Action<Exception> failed)
        {
            new Thread(() =>
            {
                T result = default(T);
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsOpen) return;
                    try
                    {
                        if (error != null) { if (failed != null) failed(error); }
                        else done(result);
                    }
                    catch (Exception ex) { plugin.LogError(ex, "Async page update failed"); }
                }));
            }) { IsBackground = true }.Start();
        }

        private void Render(List<MenuEntry> newEntries, int select)
        {
            entries.Clear();
            entries.AddRange(newEntries);
            list.Children.Clear();
            foreach (MenuEntry e in entries) list.Children.Add(BuildVisual(e));
            scroller.ScrollToTop();

            selected = -1;
            if (select < 0 || select >= entries.Count || !entries[select].Selectable)
                select = entries.FindIndex(x => x.Selectable);
            if (select >= 0) Select(select);
            BuildFooter();
        }

        // ================= Entry visuals =================

        private UIElement BuildVisual(MenuEntry e)
        {
            if (e.Kind == EntryKind.Header)
            {
                return new TextBlock
                {
                    Text = (e.Title ?? "").ToUpperInvariant(),
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = OverlayTheme.TextSub,
                    Margin = new Thickness(18, 16, 0, 8)
                };
            }
            if (e.Kind == EntryKind.Divider)
            {
                return new Border { Height = 1.5, Background = OverlayTheme.TrackBg, Margin = new Thickness(12, 10, 12, 10) };
            }
            if (e.Kind == EntryKind.Text)
            {
                e.TitleText = new TextBlock
                {
                    Text = e.Title ?? "",
                    FontSize = 18,
                    Foreground = OverlayTheme.TextSub,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 26
                };
                var tb = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(18, 6, 18, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = Brushes.Transparent,
                    Child = e.TitleText
                };
                e.Visual = tb;
                if (e.Selectable) tb.MouseEnter += (s, a) => { if (mouseMoved) Select(entries.IndexOf(e)); };
                return tb;
            }

            bool card = e.Kind == EntryKind.Card;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement icon = null;
            if (!string.IsNullOrEmpty(e.Image))
            {
                var img = new Image { Width = e.ImageW, Height = e.ImageH, Stretch = Stretch.UniformToFill, Opacity = e.Dim ? 0.45 : 1, VerticalAlignment = card ? VerticalAlignment.Top : VerticalAlignment.Center };
                img.Source = LoadImage(e.Image, (int)(e.ImageW * 2));
                double r = e.RoundImage ? e.ImageW / 2 : 8;
                img.Clip = new RectangleGeometry(new Rect(0, 0, e.ImageW, e.ImageH), r, r);
                icon = img;
            }
            else if (e.Glyph != null || e.GlyphFunc != null)
            {
                e.GlyphText = new TextBlock
                {
                    FontFamily = IconFont,
                    FontSize = 28,
                    Width = 34,
                    TextAlignment = TextAlignment.Center,
                    Foreground = e.Danger ? OverlayTheme.DangerBrush : OverlayTheme.TextMain,
                    Opacity = e.Dim ? 0.55 : 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                SetGlyph(e.GlyphText, e.GlyphFunc != null ? e.GlyphFunc() : e.Glyph);
                icon = e.GlyphText;
            }
            if (icon != null)
            {
                var holder = new Border { Child = icon, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = card ? VerticalAlignment.Top : VerticalAlignment.Center };
                grid.Children.Add(holder);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(e.Label))
            {
                var labelText = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
                labelText.Inlines.Add(new System.Windows.Documents.Run(e.Label.ToUpperInvariant()) { Foreground = theme.Accent });
                if (!string.IsNullOrEmpty(e.LabelAlert))
                {
                    labelText.Inlines.Add(new System.Windows.Documents.Run("  \u00B7  ") { Foreground = OverlayTheme.TextSub });
                    labelText.Inlines.Add(new System.Windows.Documents.Run(e.LabelAlert.ToUpperInvariant()) { Foreground = OverlayTheme.DangerBrush });
                }
                text.Children.Add(labelText);
            }

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            e.TitleText = new TextBlock
            {
                Text = e.TitleFunc != null ? e.TitleFunc() : e.Title,
                FontSize = card ? 26 : 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = e.Danger ? OverlayTheme.DangerBrush : OverlayTheme.TextMain,
                Opacity = e.Dim ? 0.65 : 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = card ? TextWrapping.Wrap : TextWrapping.NoWrap
            };
            titleRow.Children.Add(e.TitleText);
            if (e.Dot != null)
            {
                e.DotVisual = new Ellipse { Width = 11, Height = 11, Fill = OverlayTheme.GoodBrush, Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                e.DotVisual.Visibility = e.Dot() ? Visibility.Visible : Visibility.Collapsed;
                titleRow.Children.Add(e.DotVisual);
            }
            if (e.TitleFunc == null && string.IsNullOrEmpty(e.Title) && e.Dot == null) titleRow.Visibility = Visibility.Collapsed;
            text.Children.Add(titleRow);

            string sub = e.Sub != null ? e.Sub() : null;
            e.SubText = new TextBlock
            {
                Text = sub ?? "",
                FontSize = 17,
                Foreground = OverlayTheme.TextSub,
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible
            };
            text.Children.Add(e.SubText);
            if (e.Extra != null) text.Children.Add(e.Extra);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (e.Right != null)
            {
                var right = new Border { Child = e.Right, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
            }
            if (e.Badge != null || e.Chevron)
            {
                var end = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 2, 0) };
                if (e.Badge != null)
                {
                    e.BadgeText = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    e.BadgeVisual = new Border
                    {
                        MinWidth = 32,
                        Height = 32,
                        CornerRadius = new CornerRadius(16),
                        Background = OverlayTheme.BadgeBrush,
                        Padding = new Thickness(8, 0, 8, 0),
                        Margin = new Thickness(0, 0, e.Chevron ? 14 : 0, 0),
                        Child = e.BadgeText
                    };
                    UpdateBadge(e);
                    end.Children.Add(e.BadgeVisual);
                }
                if (e.Chevron)
                    end.Children.Add(new TextBlock { Text = "\uE76C", FontFamily = IconFont, FontSize = 18, Foreground = OverlayTheme.TextMain, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(end, 3);
                grid.Children.Add(end);
            }

            var border = new Border
            {
                CornerRadius = new CornerRadius(20),
                Padding = card ? new Thickness(18, 16, 20, 16) : new Thickness(18, 11, 20, 11),
                Margin = new Thickness(0, 0, 0, card ? 10 : 4),
                Background = card ? OverlayTheme.CardBg : Brushes.Transparent,
                BorderThickness = new Thickness(2.5),
                BorderBrush = Brushes.Transparent,
                Child = grid,
                Cursor = e.Selectable ? Cursors.Hand : Cursors.Arrow
            };
            e.Visual = border;

            if (e.Selectable)
            {
                border.MouseEnter += (s, a) => { if (mouseMoved) Select(entries.IndexOf(e)); };
                border.MouseLeftButtonUp += (s, a) => { Select(entries.IndexOf(e)); ActivateSelected(); };
            }
            return border;
        }

        private static void UpdateBadge(MenuEntry e)
        {
            if (e.BadgeVisual == null) return;
            int n = e.Badge();
            e.BadgeVisual.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            e.BadgeText.Text = n > 99 ? "99+" : n.ToString();
        }

        private static ImageSource LoadImage(string pathOrUrl, int decodeWidth)
        {
            try
            {
                Uri uri;
                if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) uri = new Uri(pathOrUrl);
                else if (File.Exists(pathOrUrl)) uri = new Uri(pathOrUrl, UriKind.Absolute);
                else return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = uri;
                bi.DecodePixelWidth = Math.Max(32, decodeWidth);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.EndInit();
                return bi;
            }
            catch (Exception) { return null; }
        }

        private void StyleEntry(MenuEntry e, bool sel)
        {
            if (e.Visual == null) return;
            if (e.Kind == EntryKind.Text)
            {
                e.Visual.Background = sel ? theme.Soft : Brushes.Transparent;
                e.Visual.BorderBrush = sel ? theme.GradientV : Brushes.Transparent;
                e.TitleText.Foreground = sel ? OverlayTheme.TextMain : OverlayTheme.TextSub;
                return;
            }
            bool card = e.Kind == EntryKind.Card;
            e.Visual.Background = sel ? theme.Soft : (card ? OverlayTheme.CardBg : Brushes.Transparent);
            e.Visual.BorderBrush = sel ? (e.Danger ? OverlayTheme.DangerBrush : theme.Gradient) : Brushes.Transparent;
            e.Visual.Effect = sel ? theme.Glow(0.6) : null;
            if (e.GlyphText != null)
                e.GlyphText.Foreground = e.Danger ? OverlayTheme.DangerBrush : (sel ? theme.Accent : OverlayTheme.TextMain);
        }

        private void Select(int index)
        {
            if (index < 0 || index >= entries.Count || !entries[index].Selectable) return;
            if (selected >= 0 && selected < entries.Count) StyleEntry(entries[selected], false);
            selected = index;
            StyleEntry(entries[index], true);

            int first = entries.FindIndex(x => x.Selectable);
            int last = entries.FindLastIndex(x => x.Selectable);
            if (index == first) scroller.ScrollToTop();
            else if (index == last) scroller.ScrollToEnd();
            else entries[index].Visual.BringIntoView();
        }

        private void Move(int dir)
        {
            for (int i = selected + dir; i >= 0 && i < entries.Count; i += dir)
            {
                if (entries[i].Selectable) { Select(i); return; }
            }
        }

        private void RefreshEntries()
        {
            // runs every second from the ticker: one row's live text failing mustn't take Playnite down
            foreach (MenuEntry e in entries.ToList())
            {
                try
                {
                    if (e.Refresh != null) e.Refresh();
                    if (e.TitleFunc != null && e.TitleText != null) e.TitleText.Text = e.TitleFunc();
                    if (e.GlyphFunc != null && e.GlyphText != null) SetGlyph(e.GlyphText, e.GlyphFunc());
                    if (e.Dot != null && e.DotVisual != null) e.DotVisual.Visibility = e.Dot() ? Visibility.Visible : Visibility.Collapsed;
                    if (e.Badge != null) UpdateBadge(e);
                    if (e.Sub != null && e.SubText != null)
                    {
                        string s = e.Sub();
                        e.SubText.Text = s ?? "";
                        e.SubText.Visibility = string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
                    }
                }
                catch (Exception) { }
            }
        }

        private void ActivateSelected()
        {
            if (selected < 0 || selected >= entries.Count) return;
            var e = entries[selected];
            try
            {
                if (e.Activate != null) e.Activate();
                else if (e.Adjust != null) e.Adjust(1);
            }
            catch (Exception ex)
            {
                plugin.LogError(ex, "Overlay action failed");
                plugin.Toast("That didn't work", ex.Message);
            }
            if (IsOpen) RefreshEntries();
        }

        private void AdjustSelected(int dir)
        {
            if (selected < 0 || selected >= entries.Count) return;
            var e = entries[selected];
            if (e.Adjust == null) return;
            try { e.Adjust(dir); }
            catch (Exception ex) { plugin.LogError(ex, "Overlay setting change failed"); }
            if (IsOpen) RefreshEntries();
        }

        // ================= Entry helpers =================

        private static MenuEntry Row(string glyph, string title, string sub, Action activate)
        {
            return new MenuEntry { Glyph = glyph, Title = title, Sub = sub == null ? null : (Func<string>)(() => sub), Activate = activate };
        }

        private static MenuEntry Header(string title)
        {
            return new MenuEntry { Kind = EntryKind.Header, Title = title, Selectable = false };
        }

        private static MenuEntry Divider()
        {
            return new MenuEntry { Kind = EntryKind.Divider, Selectable = false };
        }

        private static MenuEntry Note(string glyph, string text)
        {
            return new MenuEntry { Glyph = glyph, Title = text, Dim = true, Selectable = false };
        }

        private static MenuEntry InfoCard(string glyph, string label, string title, string sub)
        {
            return new MenuEntry { Kind = EntryKind.Card, Glyph = glyph, Label = label, Title = title, Sub = sub == null ? null : (Func<string>)(() => sub), Selectable = false };
        }

        private void PushConfirm(string title, string message, string glyph, string yesText, bool danger, Action yes)
        {
            Push(title, () =>
            {
                var l = new List<MenuEntry>();
                var card = InfoCard(glyph, null, title + "?", message);
                card.Danger = danger;
                l.Add(card);
                var y = Row(glyph, yesText, null, yes);
                y.Danger = danger;
                l.Add(y);
                l.Add(Row("\uE711", "Cancel", null, Back));
                return l;
            });
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1).TrimEnd() + "\u2026";
        }

        // ================= Input =================

        public void HandleController(ushort pressed)
        {
            if (!IsOpen || pressed == 0) return;
            if ((pressed & Btn.Up) != 0) Move(-1);
            if ((pressed & Btn.Down) != 0) Move(1);
            if ((pressed & Btn.Left) != 0) AdjustSelected(-1);
            if ((pressed & Btn.Right) != 0) AdjustSelected(1);
            if ((pressed & Btn.LB) != 0) SwitchTab(tab - 1);
            if ((pressed & Btn.RB) != 0) SwitchTab(tab + 1);
            if ((pressed & Btn.A) != 0) ActivateSelected();
            if ((pressed & Btn.B) != 0) Back();
        }

        /// <summary>
        /// Playnite's own controller events (they come as KeyDown only, no key): used when the overlay doesn't read the
        /// controller itself (a Steam Controller outside games), so A, B, LB / RB work here too. With a pad the overlay reads,
        /// that reading does it instead.
        /// </summary>
        private bool triggerHeld;
        private DateTime triggerUpAt = DateTime.MinValue;

        private void OnTriggerClick(object sender, MouseButtonEventArgs e)
        {
            if (triggerHeld || (DateTime.UtcNow - triggerUpAt).TotalMilliseconds < 400) e.Handled = true;
        }

        private void OnPlaynitePadKeyUp(object sender, KeyEventArgs e)
        {
            var b = KippixHelper.SteamCopyFilter.PadButton(e);
            if (b == Playnite.SDK.Events.ControllerInput.TriggerLeft || b == Playnite.SDK.Events.ControllerInput.TriggerRight)
            {
                triggerHeld = false;
                triggerUpAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Is this button held on a controller the overlay reads itself (an Xbox-style pad through XInput, a PlayStation pad)?
        /// Then that reading handles the press; Playnite's copy of it is left alone.
        /// </summary>
        private bool PollerHasButton(ushort mask)
        {
            if ((plugin.HeldButtons & mask) != 0) return true;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var st = new XInput.State();
                    if (XInput.GetState(i, ref st) == 0 && (st.Gamepad.wButtons & mask) != 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private void OnPlaynitePadKey(object sender, KeyEventArgs e)
        {
            try
            {
                var b = KippixHelper.SteamCopyFilter.PadButton(e);
                if (b == Playnite.SDK.Events.ControllerInput.TriggerLeft || b == Playnite.SDK.Events.ControllerInput.TriggerRight) triggerHeld = true;
                if (!IsOpen || plugin.Poller == null) return;
                if (plugin.ComposeOpen)
                {
                    // the on-screen keyboard, driven by a pad only Playnite reads (the Steam Controller): every button
                    // works as it does on a pad the overlay reads itself (the D-pad comes as arrow keys, see HandleKey)
                    ushort k = PadMask(b);
                    if (k == 0 || PollerHasButton(k)) return;
                    plugin.ComposePress(k);
                    e.Handled = true;
                    return;
                }
                ushort mask = b == Playnite.SDK.Events.ControllerInput.A ? Btn.A : b == Playnite.SDK.Events.ControllerInput.B ? Btn.B
                            : b == Playnite.SDK.Events.ControllerInput.LeftShoulder ? Btn.LB : b == Playnite.SDK.Events.ControllerInput.RightShoulder ? Btn.RB : (ushort)0;
                if (mask == 0 || PollerHasButton(mask)) return;   // (a pad the overlay reads: it does this itself)
                switch (b)
                {
                    case Playnite.SDK.Events.ControllerInput.LeftShoulder: SwitchTab(tab - 1); break;
                    case Playnite.SDK.Events.ControllerInput.RightShoulder: SwitchTab(tab + 1); break;
                    case Playnite.SDK.Events.ControllerInput.A: ActivateSelected(); break;
                    case Playnite.SDK.Events.ControllerInput.B: Back(); break;
                    default: return;
                }
                e.Handled = true;
            }
            catch (Exception) { }
        }

        /// <summary>A Playnite controller button as the overlay's own button bits (not the D-pad: Playnite sends that as arrow keys too).</summary>
        private static ushort PadMask(Playnite.SDK.Events.ControllerInput b)
        {
            switch (b)
            {
                case Playnite.SDK.Events.ControllerInput.A: return Btn.A;
                case Playnite.SDK.Events.ControllerInput.B: return Btn.B;
                case Playnite.SDK.Events.ControllerInput.X: return Btn.X;
                case Playnite.SDK.Events.ControllerInput.Y: return Btn.Y;
                case Playnite.SDK.Events.ControllerInput.LeftShoulder: return Btn.LB;
                case Playnite.SDK.Events.ControllerInput.RightShoulder: return Btn.RB;
                case Playnite.SDK.Events.ControllerInput.Start: return Btn.Start;
                default: return 0;
            }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            // Steam (and other controller mappers) also turn the controller into arrow/Enter/Escape key presses.
            // The overlay already reads the controller directly, so those copies would move the selection twice.
            if (plugin.ControllerRecentlyUsed)
            {
                e.Handled = true;
                return;
            }
            // Playnite (and Steam) can also turn a controller press into an arrow / Enter / Escape key, and that copy
            // may arrive just before our own controller reading sees the press. Wait a moment: if the controller
            // was used meanwhile, it's that copy and the controller already handled it.
            if (!e.IsRepeat && IsNavKey(e.Key) && plugin.Poller != null && plugin.Poller.SeesPad)
            {
                e.Handled = true;
                pendingKeys.Add(e.Key);
                if (keyTimer == null)
                {
                    keyTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
                    keyTimer.Tick += (s2, e2) => FlushKeys();
                }
                if (!keyTimer.IsEnabled) keyTimer.Start();
                return;
            }
            FlushKeys();   // anything still waiting goes first, so keys stay in order
            if (HandleKey(e.Key)) e.Handled = true;
        }

        private readonly List<Key> pendingKeys = new List<Key>();
        private DispatcherTimer keyTimer;

        /// <summary>The keys that were held back a moment: dropped if the controller turned out to have sent them, handled in order otherwise.</summary>
        private void FlushKeys()
        {
            if (keyTimer != null) keyTimer.Stop();
            if (pendingKeys.Count == 0) return;
            var keys = pendingKeys.ToArray();
            pendingKeys.Clear();
            if (!IsOpen || plugin.ControllerRecentlyUsed) return;
            foreach (var k in keys) { if (!IsOpen) break; HandleKey(k); }
        }

        private static bool IsNavKey(Key k)
        {
            return k == Key.Up || k == Key.Down || k == Key.Left || k == Key.Right || k == Key.Enter || k == Key.Space || k == Key.Escape || k == Key.Back;
        }

        private bool HandleKey(Key key)
        {
            if (plugin.ComposeOpen)
            {
                // the on-screen keyboard: arrows move over it (the D-pad / stick of a pad Playnite reads), and a real
                // keyboard's Backspace / Enter / Escape edit, type it / send, and close
                switch (key)
                {
                    case Key.Up: plugin.ComposePress(Btn.Up); break;
                    case Key.Down: plugin.ComposePress(Btn.Down); break;
                    case Key.Left: plugin.ComposePress(Btn.Left); break;
                    case Key.Right: plugin.ComposePress(Btn.Right); break;
                    case Key.Space: plugin.ComposeType(" "); break;   // (held back like the arrows: its typed space never comes)
                    case Key.Back: plugin.ComposeBackspace(); break;
                    case Key.Enter: plugin.ComposeSubmit(); break;
                    case Key.Escape: plugin.CloseCompose(); break;
                    default: return false;
                }
                return true;
            }
            switch (key)
            {
                case Key.Up: Move(-1); break;
                case Key.Down: Move(1); break;
                case Key.Left: AdjustSelected(-1); break;
                case Key.Right: AdjustSelected(1); break;
                case Key.Enter:
                case Key.Space: ActivateSelected(); break;
                case Key.Escape:
                case Key.Back: Back(); break;
                case Key.Q:
                case Key.PageUp: SwitchTab(tab - 1); break;
                case Key.E:
                case Key.PageDown:
                case Key.Tab: SwitchTab(tab + 1); break;
                default: return false;
            }
            return true;
        }

        // ================= Show / hide =================

        public void ShowOverlay()
        {
            IsOpen = true;
            UpdateClock();
            ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            ticker.Tick += (s, e) =>
            {
                UpdateClock();
                RefreshEntries();
                if (plugin.Poller != null && plugin.Poller.Kind != shownKind) BuildFooter();
            };
            ticker.Start();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ShowDim();   // (behind the panel: shown first)
            long tDim = sw.ElapsedMilliseconds;
            // (0.99.72) shown without taking the focus there and then: taking it from a game that's busy (full screen, or
            // changing mode as it loses the focus) held Playnite up for 2-5 seconds, and the overlay with it. The focus is
            // taken on a worker thread instead, and the keyboard focus follows once Windows has made the overlay active.
            ShowActivated = false;
            Show();
            long tShow = sw.ElapsedMilliseconds;
            if (!keepGameFocused)
            {
                EventHandler onActive = null;
                onActive = (s, e) => { Activated -= onActive; try { if (IsOpen) Focus(); } catch (Exception) { } };
                Activated += onActive;
                Native.ForceForegroundSoon(Handle);   // (0.99.59: never holds the overlay up)
            }
            ShowTimings = "dim " + tDim + ", window " + (tShow - tDim) + ", focus " + (sw.ElapsedMilliseconds - tShow);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(150);   // (0.99.56: 220 ms felt slow to open)
            backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            panelShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-50, 0, dur) { EasingFunction = ease });
        }

        /// <summary>Fading out (closed, not gone yet): a new open waits for it rather than replacing it.</summary>
        public bool IsClosing { get { return closingSince != DateTime.MinValue && !closeDone && (DateTime.UtcNow - closingSince).TotalSeconds < 1.5; } }
        private DateTime closingSince = DateTime.MinValue;
        private bool closeDone;

        /// <summary>Left behind (it never finished closing): closed now, and its pending close does nothing later.</summary>
        public void Abandon()
        {
            closeDone = true;
            IsOpen = false;
            StopTimers();
            CloseDim();
            try { Close(); } catch (InvalidOperationException) { }
        }

        private void StopTimers()
        {
            if (ticker != null) ticker.Stop();
            if (keyTimer != null) keyTimer.Stop();
        }

        /// <summary>
        /// However it closes (faded out, abandoned, or Alt+F4): the once-a-second timer stops (it kept the window alive and
        /// refreshing for good) and open pages clean up (a chat page stops watching Discord).
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            IsOpen = false;
            closeDone = true;
            StopTimers();
            CloseDim();
            try { LeavePages(0); } catch (Exception) { }
            base.OnClosed(e);
        }

        public void CloseOverlay(Action after)
        {
            if (!IsOpen) return;
            IsOpen = false;
            closingSince = DateTime.UtcNow;
            if (ticker != null) ticker.Stop();
            CloseDim();
            LeavePages(0);

            var dur = TimeSpan.FromMilliseconds(130);
            var fade = new DoubleAnimation(panel.Opacity, 0, dur);
            fade.Completed += (s, e) =>
            {
                if (closeDone) return;   // (already closed: replaced, or the game ended)
                closeDone = true;
                try { Hide(); Close(); } catch (InvalidOperationException) { }
                if (after != null) after();
            };
            backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(backdrop.Opacity, 0, dur));
            panelShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40, dur));
            panel.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>The overlay's Keyboard: the overlay goes out of sight (the window stays in front, so the controller still
        /// reaches the keyboard) and comes back if the keyboard couldn't open.</summary>
        internal void HideForKeyboard(bool hide)
        {
            var root = Content as UIElement;
            if (root != null) root.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
            if (dim != null) try { if (hide) dim.Hide(); else { dim.Show(); } } catch (Exception) { }
            if (hide) { Focusable = true; Keyboard.Focus(this); }
        }

        public void Refocus()
        {
            if (IsOpen && !keepGameFocused) Native.ForceForeground(Handle);
        }

        private void UpdateClock()
        {
            clockText.Text = DateTime.Now.ToString("HH:mm");
            UpdateBar();
        }

        /// <summary>Fills the bottom bar (every second, from the ticker). Items with nothing to show are hidden, and the bar
        /// with them when all are.</summary>
        private void UpdateBar()
        {
            if (footer == null) return;
            string session = null, discord = null, battery = null, audio = null;
            try
            {
                if (plugin.RunningGame != null)
                    session = "Session " + SessionClock(DateTime.Now - plugin.GameStartedAt);
            }
            catch (Exception) { }
            try { discord = plugin.DiscordBarText(); } catch (Exception) { }
            try { battery = plugin.Poller != null ? plugin.Poller.BatteryShort() : null; } catch (Exception) { }
            try { audio = plugin.AudioOutputName(); } catch (Exception) { }
            SetBarItem(sessionItem, sessionText, session);
            SetBarItem(discordItem, discordText, discord);
            SetBarItem(batteryItem, statusText, battery);
            SetBarItem(audioItem, audioText, audio);
            bool any = session != null || discord != null || !string.IsNullOrEmpty(battery) || !string.IsNullOrEmpty(audio);
            footer.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>"12:05" under an hour, "1:12:05" after.</summary>
        private static string SessionClock(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int h = (int)t.TotalHours;
            return h > 0 ? h + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                         : t.Minutes + ":" + t.Seconds.ToString("00");
        }

        /// <summary>Re-creates the whole panel after a theme change.</summary>
        private void RestyleAll()
        {
            if (!IsOpen || stack.Count == 0) return;
            int keepTab = tab;
            var keep = new List<Page>(stack);
            int keepSel = selected;
            double keepOffset = scroller != null ? scroller.VerticalOffset : 0;
            BuildChrome();
            panel.Opacity = 1;
            backdrop.Opacity = 1;
            panelShift.X = 0;
            tab = keepTab;
            stack.Clear();
            stack.AddRange(keep);
            stack[stack.Count - 1].Selected = keepSel;
            BuildTabStrip();
            ShowTop();
            UpdateClock();
            // stay on the same row, where it was on screen (the new panel lays out first)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (scroller != null) scroller.ScrollToVerticalOffset(keepOffset);
                if (keepSel >= 0 && keepSel < entries.Count && entries[keepSel].Selectable)
                {
                    if (selected != keepSel) Select(keepSel);
                    else if (entries[keepSel].Visual != null) entries[keepSel].Visual.BringIntoView();
                }
            }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// (0.99.55) The dimmed screen behind the overlay's panel: drawn once and then left alone (nothing in it moves), so it costs
    /// nothing while the panel slides and highlights. Never takes the focus; clicks on it do nothing; not in screenshots.
    /// </summary>
    internal class OverlayDimWindow : Window
    {
        public OverlayDimWindow(Rect bounds)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            Title = "Kippix overlay (dim)";
            SourceInitialized += (s, e) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                try
                {
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                    Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
                }
                catch (Exception) { }
                try { Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE); } catch (Exception) { }
            };
        }
    }
}

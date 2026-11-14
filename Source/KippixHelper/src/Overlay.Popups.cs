using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixOverlay
{
    /// <summary>Base for small windows that float over the game without ever taking focus or clicks.</summary>
    internal class FloatingWindow : Window
    {
        protected readonly System.Drawing.Rectangle Monitor;
        protected readonly double Scale;

        public FloatingWindow(IntPtr anchorHwnd, bool clickThrough)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = OverlayWindow.UiFont;

            Monitor = Capture.MonitorBounds(anchorHwnd);
            Scale = Capture.DpiScale();
            Left = Monitor.Left / Scale + 40;
            Top = Monitor.Top / Scale + 40;

            SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                if (clickThrough) ex |= Native.WS_EX_TRANSPARENT;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
                // (0.99.29) notifications and hint strips never end up in a screenshot or a clip (the theme's or anyone's)
                try { Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE); } catch (Exception) { }
            };
        }

        protected void PlaceTopCenter(double margin)
        {
            Left = Monitor.Left / Scale + (Monitor.Width / Scale - ActualWidth) / 2;
            Top = Monitor.Top / Scale + margin;
        }

        protected void PlaceBottomCenter(double margin)
        {
            Left = Monitor.Left / Scale + (Monitor.Width / Scale - ActualWidth) / 2;
            Top = Monitor.Top / Scale + Monitor.Height / Scale - ActualHeight - margin;
        }

        protected static Border Card(OverlayTheme t, UIElement child, Thickness padding)
        {
            return new Border
            {
                Background = t.Panel,
                BorderBrush = t.Gradient,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(18),
                Padding = padding,
                Child = child,
                Effect = t.Glow(0.55),
                Margin = new Thickness(20)
            };
        }
    }

    internal class ToastWindow : FloatingWindow
    {
        private static ToastWindow current;

        /// <summary>(0.99.29) A notification on screen goes at once (before a screenshot is taken). True: one was showing.</summary>
        public static bool CloseCurrent()
        {
            var c = current;
            if (c == null) return false;
            current = null;
            try { c.Close(); } catch (Exception) { }
            return true;
        }

        public static void ShowToast(string title, string message, IntPtr anchor, OverlayTheme theme) { ShowToast(title, message, anchor, theme, 2.8, null); }

        /// <summary>(0.99.54) With how long it stays, and a hint line under it ("Guide: see it").</summary>
        public static void ShowToast(string title, string message, IntPtr anchor, OverlayTheme theme, double seconds, string hint)
        {
            ShowToast(title, message, anchor, theme, seconds, hint, null, false);
        }

        /// <summary>(0.99.55) With a small picture on the left (a screenshot's or a clip's), and a play mark on a clip's.</summary>
        public static void ShowToast(string title, string message, IntPtr anchor, OverlayTheme theme, double seconds, string hint, ImageSource picture, bool clip)
        {
            if (current != null) { try { current.Close(); } catch (Exception) { } }
            current = new ToastWindow(title, message, anchor, theme, seconds, hint, picture, clip);
            current.Show();
        }

        private ToastWindow(string title, string message, IntPtr anchor, OverlayTheme t, double seconds, string hintText, ImageSource picture, bool clip) : base(anchor, true)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = title, Foreground = OverlayTheme.TextMain, FontSize = 19, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(message))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = message,
                    Foreground = OverlayTheme.TextSub,
                    FontSize = 15,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460
                });
            }
            if (!string.IsNullOrEmpty(hintText))
                stack.Children.Add(new TextBlock { Text = hintText, Foreground = OverlayTheme.TextDim, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) });
            UIElement body = stack;
            if (picture != null)
            {
                double ar = picture.Height > 0 ? picture.Width / picture.Height : 16.0 / 9;
                double h = 90, w = Math.Max(70, Math.Min(200, h * ar));
                var pic = new Grid { Width = w, Height = h, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                pic.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } });
                pic.Children.Add(new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
                if (clip)
                    pic.Children.Add(new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = "\uE768", FontFamily = OverlayWindow.IconFont, FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) }
                    });
                var row = new DockPanel();
                DockPanel.SetDock(pic, Dock.Left);
                row.Children.Add(pic);
                row.Children.Add(stack);
                body = row;
            }
            Content = Card(t, body, new Thickness(picture != null ? 14 : 22, 14, 26, 16));

            Opacity = 0;
            Loaded += (s, e) =>
            {
                PlaceTopCenter(30);
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                fade.Completed += (s2, e2) => { try { Close(); } catch (Exception) { } };
                BeginAnimation(OpacityProperty, fade);
            };
            timer.Start();
            Closed += (s, e) => { if (current == this) current = null; };
        }
    }

    /// <summary>The reminder strip shown while the controller drives Discord.</summary>
    internal class HintBar : FloatingWindow
    {
        public HintBar(IntPtr anchor, OverlayTheme t, PadKind kind) : base(anchor, true)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "Controller mode",
                Foreground = t.Accent,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0)
            });
            row.Children.Add(OverlayWindow.TextHint("Left stick", "mouse"));
            row.Children.Add(OverlayWindow.ButtonHint(kind, Btn.A, "click"));
            row.Children.Add(OverlayWindow.ButtonHint(kind, Btn.X, "right-click"));
            row.Children.Add(OverlayWindow.TextHint("Right stick", "scroll"));
            row.Children.Add(OverlayWindow.ButtonHint(kind, Btn.Y, "keyboard"));
            row.Children.Add(OverlayWindow.ButtonHint(kind, Btn.Guide, "back"));
            Content = Card(t, row, new Thickness(20, 10, 8, 10));
            Loaded += (s, e) => PlaceTopCenter(10);
        }
    }

    /// <summary>Controller on-screen keyboard. Types into whichever window has focus (Discord).</summary>
    internal class KeyboardWindow : FloatingWindow
    {
        private class KeyDef
        {
            public string Label;
            public string Text;     // typed text; null for special keys
            public double Width;    // px
            public string Special;  // shift, back, clear, space, send
            public Border Visual;
            public TextBlock LabelBlock;
            public double Center;
        }

        // the theme's keyboard (the same as Search): number row, three letter rows, then Shift / Space / Delete / Clear / Done
        private static readonly string[] LetterRows = { "1234567890", "qwertyuiop", "asdfghjkl'", KippixHelper.ThemeApi.KbRow4 };

        private const double KeyW = 104, KeyH = 74, WideW = 262, Gap = 10;
        private static readonly Brush KeyBg = Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
        private static readonly Brush KeyLine = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        private static readonly Brush KeyOn = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        private static readonly Brush KeyShiftOn = Frozen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private readonly OverlayTheme theme;
        private readonly PadKind kind;
        private readonly StackPanel rowsPanel = new StackPanel();
        private List<List<KeyDef>> rows;
        private int row = 1, col = 0;
        private bool shift;

        public Action OnClose;
        public Action<string> OnSubmit;

        private readonly bool compose;
        private readonly StringBuilder buffer = new StringBuilder();
        private TextBlock bufferText;
        private readonly string sendWord;
        private Border caret;
        private bool closed;

        private static object AppRes(string key)
        {
            try { return Application.Current != null ? Application.Current.TryFindResource(key) : null; } catch (Exception) { return null; }
        }
        private static Brush ResBrush(string key, Brush fallback) { var b = AppRes(key) as Brush; return b ?? fallback; }
        private static Brush TextBrush { get { return ResBrush("KX_TextBrush", OverlayTheme.TextMain); } }
        private static Brush TextSubBrush { get { return ResBrush("KX_TextSubBrush", OverlayTheme.TextDim); } }
        private static Brush TextDimBrush { get { return ResBrush("KX_TextDimBrush", OverlayTheme.TextDim); } }
        private Brush AccentBrush { get { return ResBrush("KX_AccentBrush", theme.Accent); } }

        /// <summary>A button icon and its word, the theme's way (its own icon for the controller in use), else the overlay's.</summary>
        private void AddHint(Panel to, string promptKey, ushort btn, string word)
        {
            var tpl = AppRes(promptKey) as DataTemplate;
            if (tpl == null) { to.Children.Add(OverlayWindow.ButtonHint(kind, btn, word.ToLowerInvariant())); return; }
            to.Children.Add(new ContentControl { ContentTemplate = tpl, Width = 24, Height = 24, Focusable = false, VerticalAlignment = VerticalAlignment.Center });
            to.Children.Add(new TextBlock { Text = word, FontSize = 17, Foreground = TextDimBrush, Margin = new Thickness(8, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        /// <summary>Types straight into the focused window (Discord controller mode).</summary>
        public KeyboardWindow(IntPtr anchor, OverlayTheme t, PadKind kind) : this(anchor, t, kind, null, null) { }

        /// <summary>Compose mode: edits its own text box and hands the result to OnSubmit.</summary>
        public KeyboardWindow(IntPtr anchor, OverlayTheme t, PadKind kind, string title, string initial) : this(anchor, t, kind, title, initial, null) { }

        /// <param name="sendWord">What Start / the Done key does, e.g. "type it" (the game keyboard); null: "send".</param>
        public KeyboardWindow(IntPtr anchor, OverlayTheme t, PadKind kind, string title, string initial, string sendWord) : base(anchor, false)
        {
            this.sendWord = sendWord;
            theme = t;
            this.kind = kind;
            compose = title != null;

            // the home screen's Search keyboard look: the theme's own font, colours, panel and button icons
            // (the theme is loaded in this same Playnite, so its resources are there; the overlay's own if not)
            FontFamily = (AppRes("FontTitilliumWeb") as FontFamily) ?? OverlayWindow.UiFont;
            var outer = new StackPanel();
            if (compose)
            {
                outer.Children.Add(new TextBlock { Text = title, FontSize = 22, Foreground = TextSubBrush, Margin = new Thickness(6, 0, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = 1200 });
                buffer.Append(initial ?? "");
                bufferText = new TextBlock
                {
                    FontSize = 40,
                    Foreground = TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 1150,
                    MinHeight = 50,
                    VerticalAlignment = VerticalAlignment.Center
                };
                caret = new Border { Width = 3, Height = 42, Background = AccentBrush, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var blink = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(550))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(blink, 12);   // (a gentle blink: no full-rate redraws over the game)
                caret.BeginAnimation(OpacityProperty, blink);
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(bufferText);
                line.Children.Add(caret);
                outer.Children.Add(new Border
                {
                    Background = Frozen(Color.FromArgb(0xF0, 0x12, 0x14, 0x18)),
                    BorderBrush = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(20),
                    Padding = new Thickness(26, 14, 26, 14),
                    Margin = new Thickness(5, 0, 5, 18),
                    Child = line
                });
                UpdateBuffer();
            }
            outer.Children.Add(rowsPanel);
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 };
            AddHint(hints, "ButtonPromptY", Btn.Y, "Space");
            AddHint(hints, "ButtonPromptLB", Btn.LB, "Shift");
            AddHint(hints, "ButtonPromptStart", Btn.Start, sendWord != null ? "Type it" : compose ? "Send" : "Enter");
            AddHint(hints, "ButtonPromptB", Btn.B, compose ? "Cancel" : "Close");
            outer.Children.Add(hints);
            // a smaller screen (Steam Deck, 1366 wide, 150 % scaling): the keyboard shrinks to fit
            var fit = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = outer, MaxWidth = Math.Max(600, Monitor.Width / Scale - 120) };
            Content = new Border
            {
                Background = Frozen(Color.FromArgb(0xF5, 0x0A, 0x0B, 0x0E)),
                BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(30, 24, 30, 20),
                Margin = new Thickness(20),
                Child = fit
            };

            Build();
            Closed += (s, e) =>
            {
                closed = true;
                if (caret != null) caret.BeginAnimation(OpacityProperty, null);   // (the blink stops with it)
            };
            Loaded += (s, e) => PlaceBottomCenter(20);
            SizeChanged += (s, e) => PlaceBottomCenter(20);
        }

        private void UpdateBuffer()
        {
            if (bufferText != null) bufferText.Text = buffer.ToString();   // (the blinking bar after it is the caret, like Search)
        }

        // ---- shared editing (compose mode edits the buffer, otherwise types into the focused app) ----

        public new void AddText(string text)
        {
            if (closed || string.IsNullOrEmpty(text)) return;
            if (compose) { buffer.Append(text); UpdateBuffer(); }
            else Native.TypeText(text);
        }

        public void Backspace()
        {
            if (compose) { if (buffer.Length > 0) buffer.Length--; UpdateBuffer(); }
            else Native.TapKey(0x08);
        }

        public void Submit()
        {
            if (closed) return;
            if (compose && sendWord != null)
            {
                // the game keyboard: exactly what's written (spaces too), and Start goes back to the game even with nothing
                if (OnSubmit != null) OnSubmit(buffer.ToString());
            }
            else if (compose)
            {
                string text = buffer.ToString().Trim();
                if (text.Length > 0 && OnSubmit != null) OnSubmit(text);
            }
            else Native.TapKey(0x0D);
        }

        private void Build()
        {
            rows = new List<List<KeyDef>>();
            rowsPanel.Children.Clear();
            var defs = new List<List<KeyDef>>();
            foreach (string r in LetterRows)
            {
                var list = new List<KeyDef>();
                foreach (char c in r) list.Add(new KeyDef { Text = c.ToString(), Label = c.ToString(), Width = KeyW });
                defs.Add(list);
            }
            var last = new List<KeyDef>();
            last.Add(new KeyDef { Special = "shift", Label = "Shift", Width = WideW });
            last.Add(new KeyDef { Special = "space", Label = "Space", Width = WideW });
            last.Add(new KeyDef { Special = "back", Label = "Delete", Width = WideW });
            if (compose) last.Add(new KeyDef { Special = "clear", Label = "Clear", Width = WideW });   // (typing straight into an app can't clear it)
            last.Add(new KeyDef { Special = "send", Label = sendWord != null ? "Type it" : compose ? "Send" : "Enter", Width = WideW });
            defs.Add(last);

            foreach (var list in defs)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                double x = 0;
                foreach (var kd in list)
                {
                    kd.Center = x + kd.Width / 2;
                    x += kd.Width + Gap;
                    kd.LabelBlock = new TextBlock
                    {
                        Text = kd.Label,
                        FontSize = 28,
                        Foreground = TextBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    kd.Visual = new Border
                    {
                        Width = kd.Width,
                        Height = KeyH,
                        Margin = new Thickness(Gap / 2),
                        CornerRadius = new CornerRadius(14),
                        Background = KeyBg,
                        BorderThickness = new Thickness(1.5),
                        BorderBrush = KeyLine,
                        Child = kd.LabelBlock
                    };
                    var captured = kd;
                    kd.Visual.MouseLeftButtonUp += (s, e) => Press(captured);
                    panel.Children.Add(kd.Visual);
                }
                double half = (x - Gap) / 2;
                foreach (var k in list) k.Center -= half;   // rows are centred: measured from each row's middle, Up / Down land on the key above / below
                rows.Add(list);
                rowsPanel.Children.Add(panel);
            }
            if (row >= rows.Count) row = rows.Count - 1;
            if (col >= rows[row].Count) col = rows[row].Count - 1;
            RefreshLabels();
            Highlight();
        }

        private void RefreshLabels()
        {
            foreach (var r in rows)
                foreach (var k in r)
                {
                    if (k.Text != null) k.LabelBlock.Text = shift ? KippixHelper.ThemeApi.KbShifted(k.Text[0]) : k.Text;
                    if (k.Special == "shift") k.LabelBlock.Text = shift ? "abc" : "Shift";
                }
        }

        private void Highlight()
        {
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var k = rows[r][c];
                    bool sel = r == row && c == col;
                    k.Visual.BorderBrush = sel ? AccentBrush : KeyLine;
                    k.Visual.Background = sel ? KeyOn : (k.Special == "shift" && shift ? KeyShiftOn : KeyBg);
                }
        }

        private void MoveRow(int dir)
        {
            int nr = row + dir;
            if (nr < 0 || nr >= rows.Count) return;
            double center = rows[row][col].Center;
            int best = 0;
            double bestDist = double.MaxValue;
            for (int c = 0; c < rows[nr].Count; c++)
            {
                double d = Math.Abs(rows[nr][c].Center - center);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            row = nr;
            col = best;
            Highlight();
        }

        private void Press(KeyDef k)
        {
            if (k.Text != null)
            {
                AddText(shift ? KippixHelper.ThemeApi.KbShifted(k.Text[0]) : k.Text);
                if (shift) { shift = false; RefreshLabels(); Highlight(); }
                return;
            }
            switch (k.Special)
            {
                case "shift": shift = !shift; RefreshLabels(); Highlight(); break;
                case "back": Backspace(); break;
                case "clear": if (compose) { buffer.Clear(); UpdateBuffer(); } break;
                case "space": AddText(" "); break;
                case "send": Submit(); break;
            }
        }

        /// <summary>Handles controller presses while the keyboard is up: Y space, LB shift, Start done, B cancel (Delete is the on-screen key).</summary>
        public void HandlePress(ushort pressed)
        {
            if (closed) return;
            if ((pressed & Btn.Up) != 0) MoveRow(-1);
            if ((pressed & Btn.Down) != 0) MoveRow(1);
            if ((pressed & Btn.Left) != 0) { col = (col - 1 + rows[row].Count) % rows[row].Count; Highlight(); }
            if ((pressed & Btn.Right) != 0) { col = (col + 1) % rows[row].Count; Highlight(); }
            if ((pressed & Btn.A) != 0) Press(rows[row][col]);
            // (no X: the game behind still reads the controller, and X opened its own keyboard; Delete is the on-screen key)
            // A on Send closes the keyboard: Start or B in the same press mustn't send or close a second time
            if (closed) return;
            if ((pressed & (Btn.Y | Btn.RB)) != 0) AddText(" ");
            if ((pressed & Btn.LB) != 0) { shift = !shift; RefreshLabels(); Highlight(); }
            if ((pressed & Btn.Start) != 0) Submit();
            if (closed) return;
            if ((pressed & Btn.B) != 0 && OnClose != null) OnClose();
        }
    }
}

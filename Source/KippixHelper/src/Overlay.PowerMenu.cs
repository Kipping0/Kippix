using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixOverlay
{
    /// <summary>
    /// (0.99.49) Hold the Guide button (1.5 s) while a game runs: a small power menu over the game, Power off and Hibernate.
    /// Either one first closes the game (as the overlay's Close game does), waits for Playnite to see it stop (so its play
    /// time is saved; up to 15 s), then turns the PC off or hibernates it. Left / Right choose, A picks, B or Guide cancel.
    /// The overlay a Guide press opens is closed when the hold turns into this. Controller input comes from the overlay's
    /// own poller (OverlayPlugin.HandleChangedCore), so it works over any game; the mouse works too.
    /// </summary>
    internal class PowerMenuWindow : Window
    {
        private readonly OverlayPlugin plugin;
        private readonly OverlayTheme theme;
        private readonly Border[] tiles = new Border[2];
        private readonly TextBlock status, hint;
        private int index;
        private bool mouseSeen, mouseMoved;
        private Point mouseAt;
        private bool busy;
        private readonly DateTime openedAt = DateTime.Now;

        public bool Busy { get { return busy; } }

        private IntPtr handle;
        private readonly IntPtr gameWindow;

        /// <summary>
        /// (0.99.51) A real window over the whole screen that takes the focus, as the overlay does: as a window that never took
        /// the focus it stayed behind the game until the overlay had been opened once, and the Steam Controller (which Playnite
        /// reads, and hands to Playnite's focused window) couldn't reach it. Buttons come from both the overlay's own reading
        /// (HandlePress) and Playnite's (OnPadKey), and keys; the same move twice within 150 ms counts once.
        /// </summary>
        public PowerMenuWindow(OverlayPlugin plugin, IntPtr anchorHwnd)
        {
            this.plugin = plugin;
            gameWindow = anchorHwnd;
            theme = OverlayTheme.Get(plugin.Config != null ? plugin.Config.Theme : 0);
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = OverlayWindow.UiFont;
            var b = Capture.MonitorBounds(anchorHwnd);
            double scale = Capture.DpiScale();
            Left = b.Left / scale; Top = b.Top / scale; Width = b.Width / scale; Height = b.Height / scale;
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

            var card = new Border
            {
                Width = 640, CornerRadius = new CornerRadius(28), Padding = new Thickness(36, 30, 36, 28), Margin = new Thickness(40),
                Background = theme.Panel, BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 48, ShadowDepth = 10, Opacity = 0.6, Color = Colors.Black },
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var all = new StackPanel();
            all.Children.Add(new TextBlock { Text = "Power", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = OverlayTheme.TextMain, HorizontalAlignment = HorizontalAlignment.Center });
            string game = plugin.GameName;
            all.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(game) ? "The game closes first" : game + " closes first", FontSize = 17, Foreground = OverlayTheme.TextDim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 22) });

            var row = new UniformGrid2();
            tiles[0] = Tile("", "Power off", 0);
            tiles[1] = Tile("", "Hibernate", 1);
            row.Add(tiles[0]);
            row.Add(tiles[1]);
            all.Children.Add(row.Root);

            status = new TextBlock { FontSize = 18, Foreground = OverlayTheme.TextSub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Visibility = Visibility.Collapsed, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            all.Children.Add(status);
            bool ps = plugin.Poller != null && plugin.Poller.Kind == PadKind.PlayStation;
            hint = new TextBlock { Text = (ps ? "✕" : "A") + "  Select        " + (ps ? "○" : "B") + "  Cancel", FontSize = 17, Foreground = OverlayTheme.TextDim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
            all.Children.Add(hint);
            card.Child = all;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            Content = card;
            Update();

            Opacity = 0;
            var pop = new ScaleTransform(0.92, 0.92);
            card.RenderTransform = pop;
            Loaded += (s, e) =>
            {
                var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
                pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            };
        }

        /// <summary>Shows it in front of the game, with the focus (so Playnite hands it the controller).</summary>
        public void ShowOnTop()
        {
            Show();
            Activate();
            try { Native.ForceForegroundSoon(handle); } catch (Exception) { }
            Focus();
        }

        // ---------------- input: one action, from whichever way it came first ----------------

        private string lastAction;
        private DateTime lastActionAt = DateTime.MinValue;

        private void Act(string action)
        {
            if (busy) return;
            if ((DateTime.Now - openedAt).TotalMilliseconds < 300) return;   // (the hold that opened it)
            if (action == lastAction && (DateTime.Now - lastActionAt).TotalMilliseconds < 150) return;   // (the same press, another way)
            lastAction = action;
            lastActionAt = DateTime.Now;
            switch (action)
            {
                case "left": if (index > 0) { index--; Update(); } break;
                case "right": if (index < tiles.Length - 1) { index++; Update(); } break;
                case "pick": Choose(); break;
                case "back": Cancel(); break;
            }
        }

        private void OnPadKey(object sender, KeyEventArgs e)
        {
            if (e.GetType().Name != "GameControllerInputEventArgs") return;
            e.Handled = true;
            var bp = e.GetType().GetProperty("Button");
            var sp = e.GetType().GetProperty("ButtonState");
            object bv = bp != null ? bp.GetValue(e, null) : null, sv = sp != null ? sp.GetValue(e, null) : null;
            if (sv != null && sv.ToString() != "Pressed") return;
            switch (bv != null ? bv.ToString() : "")
            {
                case "DPadLeft": case "LeftStickLeft": Act("left"); break;
                case "DPadRight": case "LeftStickRight": Act("right"); break;
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
                case Key.Enter: case Key.Space: if (!e.IsRepeat) Act("pick"); e.Handled = true; break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat) Act("back"); e.Handled = true; break;
            }
        }

        private Border Tile(string glyph, string text, int i)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = OverlayWindow.IconFont, FontSize = 52, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });
            var b = new Border { Height = 190, CornerRadius = new CornerRadius(22), Margin = new Thickness(10, 0, 10, 0), Child = sp, BorderThickness = new Thickness(2), Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            b.MouseEnter += (s, e) => { if (!busy && mouseMoved) { index = i; Update(); } };
            b.MouseLeftButtonUp += (s, e) => { if (!busy) { index = i; Choose(); } };
            return b;
        }

        private void Update()
        {
            for (int i = 0; i < tiles.Length; i++)
            {
                bool sel = i == index;
                var t = tiles[i];
                t.Background = sel ? theme.Gradient : new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
                t.BorderBrush = sel ? Brushes.White : Brushes.Transparent;
                var fg = sel ? new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)) : (Brush)OverlayTheme.TextMain;
                foreach (var tb in ((StackPanel)t.Child).Children) ((TextBlock)tb).Foreground = fg;
                var sc = (ScaleTransform)t.RenderTransform;
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sel ? 1.04 : 1, TimeSpan.FromMilliseconds(140)));
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sel ? 1.04 : 1, TimeSpan.FromMilliseconds(140)));
            }
        }

        /// <summary>Controller buttons from the overlay's poller (only presses).</summary>
        public void HandlePress(ushort pressed)
        {
            if (busy || pressed == 0) return;
            if ((pressed & Btn.Left) != 0) Act("left");
            else if ((pressed & Btn.Right) != 0) Act("right");
            else if ((pressed & Btn.A) != 0) Act("pick");
            else if ((pressed & (Btn.B | Btn.Guide)) != 0) Act("back");
        }

        public void Cancel()
        {
            if (busy) return;
            busy = true;
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (s, e) =>
            {
                try { Close(); } catch (Exception) { }
                try { if (gameWindow != IntPtr.Zero) Native.ForceForegroundSoon(gameWindow); } catch (Exception) { }   // (back to the game)
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void Choose()
        {
            if (busy) return;
            busy = true;
            bool hibernate = index == 1;
            hint.Visibility = Visibility.Collapsed;
            status.Visibility = Visibility.Visible;
            string game = plugin.GameName;
            status.Text = "Closing " + (string.IsNullOrEmpty(game) ? "the game" : game) + "…";
            plugin.LogInfo("Kippix overlay: power menu: " + (hibernate ? "hibernate" : "power off") + " (closing " + (game ?? "the game") + " first)");
            plugin.CloseGameForPower();
            var d = Dispatcher;
            new Thread(() =>
            {
                // until Playnite sees the game stop (its play time is saved then), at most 15 s, then a moment more
                var until = DateTime.Now.AddSeconds(15);
                while (plugin.RunningGame != null && DateTime.Now < until) Thread.Sleep(250);
                Thread.Sleep(2500);
                try { d.Invoke(new Action(() => status.Text = hibernate ? "Hibernating…" : "Powering off…")); } catch (Exception) { }
                Thread.Sleep(700);
                plugin.LogInfo("Kippix overlay: power menu: " + (hibernate ? "hibernating" : "powering off") + " now");
                if (hibernate)
                {
                    bool ok = false;
                    try { ok = Native.SetSuspendState(true, false, false); } catch (Exception) { }
                    if (!ok) Native.RunHidden("shutdown", "/h");
                    try { d.BeginInvoke(new Action(() => { try { Close(); } catch (Exception) { } })); } catch (Exception) { }
                }
                else Native.RunHidden("shutdown", "/s /t 0");
            }) { IsBackground = true }.Start();
        }

        /// <summary>Two equal columns.</summary>
        private class UniformGrid2
        {
            public readonly Grid Root = new Grid();
            public UniformGrid2() { Root.ColumnDefinitions.Add(new ColumnDefinition()); Root.ColumnDefinitions.Add(new ColumnDefinition()); }
            public void Add(UIElement e) { Grid.SetColumn(e, Root.Children.Count); Root.Children.Add(e); }
        }
    }
}

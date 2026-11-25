using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>(1.1.0) One of the colour picker's ready-made colours.</summary>
    public class ColourSwatch
    {
        public Brush Brush { get; set; }
        public string Hex { get; set; }
        public bool IsCurrent { get; set; }
        public ICommand Command { get; set; }
    }

    /// <summary>
    /// (1.1.0) The colour picker, over everything (the theme colour, a Media tile's background): a colour wheel (round it the
    /// colour, paler towards the middle), how bright, ready-made colours, or type a # number. The D-pad / stick / arrows move
    /// the marker round the wheel, LB / RB darker or lighter, X the next ready-made colour, Y types a # number, A done,
    /// B cancel. The mouse: click or drag on the wheel and the bar, click a ready-made colour or a button.
    /// And the theme colour itself: every purple in full screen (highlights, buttons, bars) apart from the game tiles,
    /// which have their own glow settings.
    /// </summary>
    public partial class ThemeApi
    {
        internal static readonly Color KippixPurple = Color.FromRgb(0x93, 0x00, 0xC5);
        private static readonly string[] ColourPresets = { "#9300C5", "#5B4CFF", "#2F7BFF", "#00B4D8", "#14B8A6", "#1DB954", "#8BC34A", "#F5B800", "#FF7A00", "#E50914", "#FF2D87", "#C026D3" };
        private const double CpR = 170;   // the wheel's radius
        private const double CpBar = 380; // the brightness bar's width

        private bool colourOpen;
        private double cpH, cpS = 1, cpV = 1;
        private Action<Color> cpDone;
        private Action cpCancel;
        private DateTime cpOpenedAt, cpPadAAt, cpPadBAt;
        private IInputElement cpFocusWas;
        private static ImageSource cpWheel;
        private bool cpMouseHooked, cpDragWheel, cpDragBar;

        public bool ColourPickerShown { get { return colourOpen; } }
        public string ColourPickerTitle { get; private set; }
        public ImageSource ColourWheel { get { return cpWheel ?? (cpWheel = MakeColourWheel()); } }
        public Thickness ColourMarkerMargin
        {
            get { double a = cpH * Math.PI / 180; return new Thickness(CpR + Math.Cos(a) * cpS * CpR - 14, CpR + Math.Sin(a) * cpS * CpR - 14, 0, 0); }
        }
        public double ColourWheelShade { get { return 1 - cpV; } }
        public Brush ColourPreview { get { return FrozenSolid(CpColour); } }
        public string ColourHex { get { return ColourToHex(CpColour); } }
        public Thickness ColourBrightnessMargin { get { return new Thickness(Math.Round(cpV * (CpBar - 28)), 0, 0, 0); } }
        public Brush ColourBrightnessBrush
        {
            get
            {
                var b = new LinearGradientBrush(Colors.Black, HsvToColour(cpH, cpS, 1), 0);
                b.Freeze();
                return b;
            }
        }
        public List<ColourSwatch> ColourSwatches { get; private set; }

        private Color CpColour { get { return HsvToColour(cpH, cpS, cpV); } }

        public ICommand ColourDoneCommand { get { return new RelayCommand(_ => CpAct("a")); } }
        public ICommand ColourCancelCommand { get { return new RelayCommand(_ => CpAct("b")); } }
        public ICommand ColourTypeCommand { get { return new RelayCommand(_ => CpAct("y")); } }
        public ICommand ColourNextCommand { get { return new RelayCommand(_ => CpAct("x")); } }
        public ICommand ColourDarkerCommand { get { return new RelayCommand(_ => CpAct("lb")); } }
        public ICommand ColourLighterCommand { get { return new RelayCommand(_ => CpAct("rb")); } }

        private void OpenColourPicker(string title, Color start, Action<Color> done, Action cancel = null)
        {
            if (kbOpen) CloseKeyboard(false);
            ColourPickerTitle = title;
            CpSet(start);
            cpDone = done;
            cpCancel = cancel;
            cpFocusWas = Keyboard.FocusedElement;
            cpOpenedAt = DateTime.Now;
            colourOpen = true;
            PlaySound("select");
            NotifyColour();
            Notify("ColourPickerTitle");
            Notify("ColourPickerShown");
            ui.BeginInvoke(new Action(HookColourMouse), DispatcherPriority.Loaded);
        }

        private void CloseColourPicker(bool ok)
        {
            if (!colourOpen) return;
            colourOpen = false;
            cpDragWheel = cpDragBar = false;
            Notify("ColourPickerShown");
            PlaySound(ok ? "select" : "back");
            var c = CpColour;
            var done = cpDone;
            var cancel = cpCancel;
            var back = cpFocusWas;
            cpDone = null;
            cpCancel = null;
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    var fe = back as FrameworkElement;
                    if (fe != null && fe.IsVisible) Keyboard.Focus(fe);
                }
                catch (Exception) { }
                try
                {
                    if (ok) { if (done != null) done(c); }
                    else if (cancel != null) cancel();
                }
                catch (Exception ex) { logger.Warn("Kippix: colour: " + ex.Message); }
            }), DispatcherPriority.Loaded);
        }

        private void CpSet(Color c)
        {
            double h, s, v;
            ColourToHsv(c, out h, out s, out v);
            if (s > 0.001) cpH = h;   // (grey: the marker stays where it was round the wheel)
            cpS = s;
            cpV = Math.Max(0.1, v);
        }

        private void NotifyColour()
        {
            string now = ColourHex;
            ColourSwatches = ColourPresets.Select(p =>
            {
                string hex = p;
                return new ColourSwatch
                {
                    Hex = hex, Brush = FrozenSolid(ParseColour(hex) ?? KippixPurple), IsCurrent = string.Equals(hex, now, StringComparison.OrdinalIgnoreCase),
                    Command = new RelayCommand(_ => { var c = ParseColour(hex); if (c != null) { CpSet(c.Value); NotifyColour(); } })
                };
            }).ToList();
            foreach (string n in new[] { "ColourMarkerMargin", "ColourWheelShade", "ColourPreview", "ColourHex", "ColourBrightnessMargin", "ColourBrightnessBrush", "ColourSwatches" }) Notify(n);
        }

        // ---------------- buttons and keys ----------------

        private void ColourPad(ControllerInput button, KeyEventArgs e)
        {
            if (IsPadRelease(e)) return;
            if ((DateTime.Now - cpOpenedAt).TotalMilliseconds < 250) return;
            switch (button)
            {
                case ControllerInput.A: cpPadAAt = DateTime.Now; CpAct("a"); break;
                case ControllerInput.B: cpPadBAt = DateTime.Now; CpAct("b"); break;
                case ControllerInput.X: CpAct("x"); break;
                case ControllerInput.Y: CpAct("y"); break;
                case ControllerInput.LeftShoulder: CpAct("lb"); break;
                case ControllerInput.RightShoulder: CpAct("rb"); break;
            }
        }

        /// <summary>Keys while the picker is up: the arrows (the D-pad and stick come as these), Enter / Esc, Page Up / Down, X / Y.</summary>
        private void ColourKey(KeyEventArgs e)
        {
            if (e.Key == Key.System) return;
            e.Handled = true;
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (the buttons: ColourPad)
            if ((DateTime.Now - cpOpenedAt).TotalMilliseconds < 250) return;
            switch (e.Key)
            {
                case Key.Left: CpAct("left"); break;
                case Key.Right: CpAct("right"); break;
                case Key.Up: CpAct("up"); break;
                case Key.Down: CpAct("down"); break;
                case Key.PageUp: CpAct("lb"); break;
                case Key.PageDown: CpAct("rb"); break;
                case Key.X: if (!e.IsRepeat) CpAct("x"); break;
                case Key.Y: if (!e.IsRepeat) CpAct("y"); break;
                case Key.Enter: case Key.Space: if (!e.IsRepeat && (DateTime.Now - cpPadAAt).TotalMilliseconds > 400) CpAct("a"); break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat && (DateTime.Now - cpPadBAt).TotalMilliseconds > 400) CpAct("b"); break;
            }
        }

        private void CpAct(string k)
        {
            if (!colourOpen) return;
            switch (k)
            {
                case "left": CpMove(-10, 0); break;
                case "right": CpMove(10, 0); break;
                case "up": CpMove(0, -10); break;
                case "down": CpMove(0, 10); break;
                case "lb": cpV = Math.Max(0.1, Math.Round(cpV - 0.05, 2)); PlaySound("move"); NotifyColour(); break;
                case "rb": cpV = Math.Min(1, Math.Round(cpV + 0.05, 2)); PlaySound("move"); NotifyColour(); break;
                case "x":
                {
                    int at = Array.FindIndex(ColourPresets, p => string.Equals(p, ColourHex, StringComparison.OrdinalIgnoreCase));
                    var c = ParseColour(ColourPresets[(at + 1) % ColourPresets.Length]);
                    if (c != null) CpSet(c.Value);
                    PlaySound("move");
                    NotifyColour();
                    break;
                }
                case "y":
                    PlaySound("select");
                    OpenKeyboard(Loc("LOC_KX_ColourType", "A colour's # number (like #9300C5)"), ColourHex, typed =>
                    {
                        var c = ParseColour(typed);
                        if (c != null) { CpSet(c.Value); NotifyColour(); }
                        else if (!string.IsNullOrWhiteSpace(typed))
                            try { ShowNotice(Loc("LOC_KX_ColourHeader", "COLOUR"), Loc("LOC_KX_ColourBad", "That isn't a colour's # number"), typed.Trim(), "", null); } catch (Exception) { }
                    }, null, null, 9);
                    break;
                case "a": CloseColourPicker(true); break;
                case "b": CloseColourPicker(false); break;
            }
        }

        /// <summary>The marker moves where it's pushed, kept on the wheel.</summary>
        private void CpMove(double dx, double dy)
        {
            double a = cpH * Math.PI / 180;
            double x = Math.Cos(a) * cpS * CpR + dx, y = Math.Sin(a) * cpS * CpR + dy;
            CpAtWheel(x, y);
        }

        private void CpAtWheel(double x, double y)
        {
            double r = Math.Sqrt(x * x + y * y);
            if (r > CpR) { x *= CpR / r; y *= CpR / r; r = CpR; }
            if (r > 0.5) cpH = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
            cpS = r / CpR;
            NotifyColour();
        }

        private void HookColourMouse()
        {
            if (cpMouseHooked) return;
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var wheel = UiTools.FindByName(win, "KX_ColourWheelHit") as FrameworkElement;
            var bar = UiTools.FindByName(win, "KX_ColourBar") as FrameworkElement;
            if (wheel == null || bar == null) return;
            cpMouseHooked = true;
            MouseButtonEventHandler wheelDown = (s, e) => { cpDragWheel = true; wheel.CaptureMouse(); CpWheelMouse(e.GetPosition(wheel)); e.Handled = true; };
            wheel.MouseLeftButtonDown += wheelDown;
            wheel.MouseMove += (s, e) => { if (cpDragWheel && e.LeftButton == MouseButtonState.Pressed) CpWheelMouse(e.GetPosition(wheel)); };
            wheel.MouseLeftButtonUp += (s, e) => { cpDragWheel = false; wheel.ReleaseMouseCapture(); };
            bar.MouseLeftButtonDown += (s, e) => { cpDragBar = true; bar.CaptureMouse(); CpBarMouse(e.GetPosition(bar), bar.ActualWidth); e.Handled = true; };
            bar.MouseMove += (s, e) => { if (cpDragBar && e.LeftButton == MouseButtonState.Pressed) CpBarMouse(e.GetPosition(bar), bar.ActualWidth); };
            bar.MouseLeftButtonUp += (s, e) => { cpDragBar = false; bar.ReleaseMouseCapture(); };
        }

        private void CpWheelMouse(Point p)
        {
            if (!colourOpen) return;
            CpAtWheel(p.X - CpR, p.Y - CpR);
        }

        private void CpBarMouse(Point p, double width)
        {
            if (!colourOpen || width <= 28) return;
            cpV = Math.Max(0.1, Math.Min(1, (p.X - 14) / (width - 28)));
            NotifyColour();
        }

        // ---------------- colours ----------------

        private static ImageSource MakeColourWheel()
        {
            int size = (int)(CpR * 2);
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5 - CpR, dy = y + 0.5 - CpR, r = Math.Sqrt(dx * dx + dy * dy);
                    double alpha = Math.Max(0, Math.Min(1, CpR - r + 0.5));
                    if (alpha <= 0) continue;
                    double h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                    var c = HsvToColour(h, Math.Min(1, r / CpR), 1);
                    int i = (y * size + x) * 4;
                    px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = (byte)Math.Round(alpha * 255);
                }
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
            bmp.Freeze();
            return bmp;
        }

        internal static Color HsvToColour(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        internal static void ColourToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        /// <summary>#RRGGBB, RRGGBB, #RGB (or with an alpha first: only the colour is used); null when it isn't one.</summary>
        internal static Color? ParseColour(string text)
        {
            string t = (text ?? "").Trim().TrimStart('#');
            if (t.Length == 3) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
            if (t.Length == 8) t = t.Substring(2);
            if (t.Length != 6) return null;
            int n;
            if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return null;
            return Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
        }

        internal static string ColourToHex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        private static SolidColorBrush FrozenSolid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ---------------- the theme colour ----------------

        private Color ThemeColourNow { get { return ParseColour(settings.ThemeColour) ?? KippixPurple; } }

        /// <summary>Every purple in full screen (KX_Theme* in Constants.xaml) becomes the theme colour.</summary>
        private void ApplyThemeColour()
        {
            var app = Application.Current;
            if (app == null) return;
            var c = ThemeColourNow;
            Func<byte, Color> a = alpha => Color.FromArgb(alpha, c.R, c.G, c.B);
            var dark = Color.FromRgb((byte)(c.R * 0.83), (byte)(c.G * 0.83), (byte)(c.B * 0.84));
            app.Resources["KX_ThemeColor"] = c;
            app.Resources["KX_ThemeColor00"] = a(0x00);
            app.Resources["KX_ThemeColor33"] = a(0x33);
            app.Resources["KX_ThemeColor40"] = a(0x40);
            app.Resources["KX_ThemeBrush"] = FrozenSolid(c);
            app.Resources["KX_ThemeBrush26"] = FrozenSolid(a(0x26));
            app.Resources["KX_ThemeBrush40"] = FrozenSolid(a(0x40));
            app.Resources["KX_ThemeBrush44"] = FrozenSolid(a(0x44));
            app.Resources["KX_ThemeDarkBrush"] = FrozenSolid(dark);
            Notify("ThemeColourText");
            Notify("ThemeColourBrush");
        }

        public bool IsThemeColourDefault { get { return ThemeColourNow == KippixPurple; } }

        public string ThemeColourText
        {
            get { return IsThemeColourDefault ? Loc("LOC_KX_ThemeColourPurple", "Kippix purple") : ColourToHex(ThemeColourNow); }
        }

        public Brush ThemeColourBrush { get { return FrozenSolid(ThemeColourNow); } }

        public ICommand ThemeColourCommand { get { return new RelayCommand(_ => PickThemeColour(null)); } }

        /// <summary>Settings › Look and feel › Theme colour, and getting started's Colour step (then <paramref name="after"/>).</summary>
        private void PickThemeColour(Action after)
        {
            OpenColourPicker(Loc("LOC_KX_ThemeColour", "Theme colour"), ThemeColourNow, c =>
            {
                settings.ThemeColour = ColourToHex(c);
                SaveSettings();
                ApplyThemeColour();
                if (after != null) after();
            }, after);
        }

        private void SetThemeColour(Color c)
        {
            settings.ThemeColour = ColourToHex(c);
            SaveSettings();
            ApplyThemeColour();
        }
    }
}

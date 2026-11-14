using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KippixOverlay
{
    internal class OverlayTheme
    {
        public readonly string Name;
        public readonly Color A;        // gradient start (labels, left/top)
        public readonly Color B;        // gradient end (glow, right/bottom)
        public readonly Color BgTop;
        public readonly Color BgBottom;

        public static readonly OverlayTheme[] All =
        {
            // the fullscreen theme's own look (its menus): dark grey glass, white highlights
            new OverlayTheme("Kippix", "#FFFFFF", "#C9D1DC", "#1B1E24", "#0D0F12"),
            new OverlayTheme("Neon",   "#22D3EE", "#E040FB", "#2B1F8C", "#1A1260"),
            new OverlayTheme("Blue",   "#3D8BFF", "#7AD0FF", "#10306E", "#0A1C47"),
            new OverlayTheme("Purple", "#9B6CFF", "#E86CFF", "#3A1A84", "#220E54"),
            new OverlayTheme("Green",  "#2FDB82", "#22C7E0", "#0E4040", "#082828"),
            new OverlayTheme("Orange", "#FFB13D", "#FF4D6A", "#4C1F2C", "#2C0F1C"),
            new OverlayTheme("Pink",   "#FF4D8D", "#B44DFF", "#4A1452", "#280A32"),
            new OverlayTheme("Cyan",   "#22D3EE", "#3D8BFF", "#0E3654", "#082035")
        };

        public static readonly Brush TextMain = Frozen(Color.FromRgb(0xF6, 0xF4, 0xFF));
        public static readonly Brush TextSub = Frozen(Color.FromArgb(0xD0, 0xE2, 0xDE, 0xFF));
        public static readonly Brush TextDim = Frozen(Color.FromArgb(0x90, 0xE2, 0xDE, 0xFF));
        public static readonly Brush CardBg = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        public static readonly Brush TrackBg = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        public static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x8A));
        public static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x2F, 0xDB, 0x82));
        public static readonly Brush BadgeBrush = Frozen(Color.FromRgb(0xFF, 0x2E, 0xB8));

        public Brush Accent { get; private set; }       // solid start colour (labels)
        public Brush Gradient { get; private set; }     // horizontal A -> B
        public Brush GradientV { get; private set; }    // vertical A -> B
        public Brush Soft { get; private set; }         // translucent gradient for selection fill
        public Brush Panel { get; private set; }

        private OverlayTheme(string name, string a, string b, string top, string bottom)
        {
            Name = name;
            A = Parse(a);
            B = Parse(b);
            BgTop = Parse(top);
            BgBottom = Parse(bottom);

            Accent = Frozen(A);
            Gradient = FreezeIt(new LinearGradientBrush(A, B, 0));
            GradientV = FreezeIt(new LinearGradientBrush(A, B, 90));
            Soft = FreezeIt(new LinearGradientBrush(
                Color.FromArgb(0x3A, Mix(BgTop, A, 0.5).R, Mix(BgTop, A, 0.5).G, Mix(BgTop, A, 0.5).B),
                Color.FromArgb(0x3A, Mix(BgTop, B, 0.5).R, Mix(BgTop, B, 0.5).G, Mix(BgTop, B, 0.5).B), 0));
            Panel = FreezeIt(new LinearGradientBrush(
                Color.FromArgb(0xF5, BgTop.R, BgTop.G, BgTop.B),
                Color.FromArgb(0xF5, BgBottom.R, BgBottom.G, BgBottom.B), 90));
        }

        public static OverlayTheme Get(int index)
        {
            return All[OverlayConfig.Wrap(index, All.Length)];
        }

        public Effect Glow(double opacity)
        {
            return new DropShadowEffect { Color = B, BlurRadius = 18, ShadowDepth = 0, Opacity = opacity };
        }

        private static Color Parse(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private static Color Mix(Color x, Color y, double t)
        {
            return Color.FromRgb(
                (byte)(x.R + (y.R - x.R) * t),
                (byte)(x.G + (y.G - x.G) * t),
                (byte)(x.B + (y.B - x.B) * t));
        }

        public static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Brush FreezeIt(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}

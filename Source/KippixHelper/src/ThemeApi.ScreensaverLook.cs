using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KippixHelper
{
    /// <summary>
    /// (0.86) The screensaver's corner: the game's logo with the time above it (the game's name when it has no logo), in any
    /// of nine places on the screen; how long each picture shows; and whether "Press any button to come back" shows under
    /// them. Settings › General settings › Look and feel › Screensaver.
    /// </summary>
    public partial class ThemeApi
    {
        public ImageSource AmbientLogo { get; private set; }
        public bool HasAmbientLogo { get { return AmbientLogo != null; } }
        public bool HasAmbientCaption { get { return AmbientLogo == null && !string.IsNullOrEmpty(AmbientCaption); } }

        private int AmbientPosNow { get { return Clamp(settings.AmbientPos, 0, 8); } }
        public HorizontalAlignment AmbientHAlign { get { int c = AmbientPosNow % 3; return c == 0 ? HorizontalAlignment.Left : c == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right; } }
        public VerticalAlignment AmbientVAlign { get { int r = AmbientPosNow / 3; return r == 0 ? VerticalAlignment.Top : r == 1 ? VerticalAlignment.Center : VerticalAlignment.Bottom; } }
        public TextAlignment AmbientTextAlign { get { int c = AmbientPosNow % 3; return c == 0 ? TextAlignment.Left : c == 1 ? TextAlignment.Center : TextAlignment.Right; } }
        public Thickness AmbientMargin { get { return new Thickness(90, 80, 90, 80); } }
        // the shade behind the corner: at the top, the bottom, or all over (middle row) so the time can be read on any picture
        public bool AmbientShadeTop { get { return AmbientPosNow / 3 == 0; } }
        public bool AmbientShadeMiddle { get { return AmbientPosNow / 3 == 1; } }
        public bool AmbientShadeBottom { get { return AmbientPosNow / 3 == 2; } }
        public bool AmbientWakeText
        {
            get { return settings.AmbientWakeText; }
            set { if (settings.AmbientWakeText == value) return; settings.AmbientWakeText = value; SaveSettings(); NotifyAmbientLook(); }
        }

        private static readonly string[] AmbientPosKeys = { "LOC_KX_PosTopLeft", "LOC_KX_PosTopCentre", "LOC_KX_PosTopRight", "LOC_KX_PosMidLeft", "LOC_KX_PosMidCentre", "LOC_KX_PosMidRight", "LOC_KX_PosBottomLeft", "LOC_KX_PosBottomCentre", "LOC_KX_PosBottomRight" };
        private static readonly string[] AmbientPosText_ = { "Top left", "Top centre", "Top right", "Middle left", "Middle centre", "Middle right", "Bottom left", "Bottom centre", "Bottom right" };
        public string AmbientPosText { get { int i = AmbientPosNow; return Loc(AmbientPosKeys[i], AmbientPosText_[i]); } }
        public string AmbientSecondsText { get { return string.Format(Loc("LOC_KX_Seconds", "{0} seconds"), Math.Max(4, settings.AmbientSeconds)); } }

        private ICommand cycAmbientPos, cycAmbientSecs;
        public ICommand CycleAmbientPosCommand
        {
            get { return cycAmbientPos ?? (cycAmbientPos = new RelayCommand(_ => { settings.AmbientPos = (AmbientPosNow + 1) % 9; SaveSettings(); PlaySound("move"); NotifyAmbientLook(); })); }
        }
        public ICommand CycleAmbientSecondsCommand
        {
            get
            {
                return cycAmbientSecs ?? (cycAmbientSecs = new RelayCommand(_ =>
                {
                    settings.AmbientSeconds = NextIn(new[] { 6, 8, 12, 20, 30, 60 }, settings.AmbientSeconds);
                    SaveSettings();
                    PlaySound("move");
                    if (ambientTimer != null) ambientTimer.Interval = TimeSpan.FromSeconds(Math.Max(4, settings.AmbientSeconds));
                    NotifyAmbientLook();
                }));
            }
        }

        // (0.99.51) Background: the games' pictures, or a plain colour (the time, logo and wake line stay)
        private static readonly string[] AmbientBgKeys = { "LOC_KX_AmbientBgArt", "LOC_KX_AmbientBgBlack", "LOC_KX_AmbientBgGrey", "LOC_KX_AmbientBgNavy", "LOC_KX_AmbientBgPurple", "LOC_KX_AmbientBgGreen", "LOC_KX_AmbientBgRed" };
        private static readonly string[] AmbientBgText_ = { "The games' pictures", "Black", "Dark grey", "Midnight blue", "Deep purple", "Forest green", "Deep red" };
        private static readonly Color[] AmbientBgColors = { Colors.Black, Colors.Black, Color.FromRgb(0x1A, 0x1B, 0x1F), Color.FromRgb(0x0B, 0x14, 0x2E), Color.FromRgb(0x1E, 0x0E, 0x33), Color.FromRgb(0x0C, 0x24, 0x1A), Color.FromRgb(0x2A, 0x0B, 0x10) };
        private int AmbientBgNow { get { return Clamp(settings.AmbientBg, 0, AmbientBgKeys.Length - 1); } }
        public string AmbientBgText { get { int i = AmbientBgNow; return Loc(AmbientBgKeys[i], AmbientBgText_[i]); } }
        public bool AmbientSolidBg { get { return AmbientBgNow > 0; } }
        public Brush AmbientBgBrush { get { var b = new SolidColorBrush(AmbientBgColors[AmbientBgNow]); b.Freeze(); return b; } }
        private ICommand cycAmbientBg;
        public ICommand CycleAmbientBgCommand
        {
            get { return cycAmbientBg ?? (cycAmbientBg = new RelayCommand(_ => { settings.AmbientBg = (AmbientBgNow + 1) % AmbientBgKeys.Length; SaveSettings(); PlaySound("move"); NotifyAmbientLook(); })); }
        }

        private void NotifyAmbientLook()
        {
            foreach (string n in new[] { "AmbientHAlign", "AmbientVAlign", "AmbientTextAlign", "AmbientShadeTop", "AmbientShadeMiddle", "AmbientShadeBottom",
                                         "AmbientWakeText", "AmbientPosText", "AmbientSecondsText", "AmbientBgText", "AmbientSolidBg", "AmbientBgBrush" })
                Notify(n);
        }
    }
}

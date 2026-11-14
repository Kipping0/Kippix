using System;
using System.Windows;
using System.Windows.Input;

namespace KippixHelper
{
    /// <summary>
    /// (0.87) Home settings › Logo, game info &amp; button hints:
    ///   - Logo lines up (LogoAnchor): logos are different widths, so a moved logo needs a point that stays put. Left edge
    ///     (as before): the next game's logo starts where this one starts. Centre: every logo is centred on the same spot.
    ///     Right edge: they all end at the same spot. Centre / right give the logo a fixed-width box (LogoBoxWidth) with the
    ///     logo lined up inside it, so the box (what Free move moves) never changes size.
    ///   - Bring them back: the logo, game info, hints and filter name back where they come (place, size, turn) and shown.
    /// </summary>
    public partial class ThemeApi
    {
        private const double LogoBoxWidth = 560;   // = the logo's own MaxWidth in Main.xaml

        private int LogoAnchorNow { get { return Clamp(settings.LogoAnchor, 0, 2); } }
        public double LogoAreaWidth { get { return LogoAnchorNow == 0 ? double.NaN : LogoBoxWidth; } }
        public HorizontalAlignment LogoInnerAlignment
        {
            get { return LogoAnchorNow == 1 ? HorizontalAlignment.Center : LogoAnchorNow == 2 ? HorizontalAlignment.Right : LogoAlignment; }
        }
        public TextAlignment LogoInnerTextAlignment
        {
            get { return LogoAnchorNow == 1 ? TextAlignment.Center : LogoAnchorNow == 2 ? TextAlignment.Right : LogoTextAlignment; }
        }
        public string LogoAnchorText
        {
            get
            {
                return LogoAnchorNow == 1 ? Loc("LOC_KX_LogoAnchorCentre", "Centre")
                     : LogoAnchorNow == 2 ? Loc("LOC_KX_LogoAnchorRight", "Right edge")
                     : Loc("LOC_KX_LogoAnchorLeft", "Left edge");
            }
        }
        public string LogoAnchorSub
        {
            get
            {
                return LogoAnchorNow == 1 ? Loc("LOC_KX_LogoAnchorCentreSub", "Every game's logo is centred on the same spot, wherever you put it")
                     : LogoAnchorNow == 2 ? Loc("LOC_KX_LogoAnchorRightSub", "Every game's logo ends at the same spot (its right edge)")
                     : Loc("LOC_KX_LogoAnchorLeftSub", "The next game's logo starts where this one starts, so a smaller logo sits further left");
            }
        }

        /// <summary>
        /// (0.88) Free move shows the logo as a line, not a box round it: the line is the point every game's logo lines up on
        /// (its left edge, its middle or its right edge, from "Logo lines up by its"), running a little past the logo's top
        /// and bottom. A turned logo keeps its box.
        /// </summary>
        private TurnFrame LogoLineFrame(TurnFrame f)
        {
            if (f == null || Math.Abs(f.A) >= 0.05) return f;
            double x = LogoAnchorNow == 1 ? f.C.X : LogoAnchorNow == 2 ? f.C.X + f.W / 2 : f.C.X - f.W / 2;
            return new TurnFrame { C = new Point(x, f.C.Y), W = 0, H = f.H + 40, A = 0 };
        }

        private ICommand cycLogoAnchor, bringInfoBack;
        public ICommand CycleLogoAnchorCommand
        {
            get
            {
                return cycLogoAnchor ?? (cycLogoAnchor = new RelayCommand(_ =>
                {
                    settings.LogoAnchor = (LogoAnchorNow + 1) % 3;
                    SaveSettings();
                    PlaySound("move");
                    NotifyLogoAnchor();
                    try { ApplyMasterOffsets(); } catch (Exception) { }
                }));
            }
        }

        private void NotifyLogoAnchor()
        {
            foreach (string n in new[] { "LogoAreaWidth", "LogoInnerAlignment", "LogoInnerTextAlignment", "LogoAnchorText", "LogoAnchorSub" }) Notify(n);
        }

        /// <summary>Logo, game info &amp; button hints › Bring them back.</summary>
        public ICommand BringHomeInfoBackCommand
        {
            get
            {
                return bringInfoBack ?? (bringInfoBack = new RelayCommand(_ =>
                {
                    foreach (string key in new[] { "home.logo", "home.info", "home.hints", "home.filter" })
                    {
                        PutBack(key);
                        if (settings.MasterSizes != null) settings.MasterSizes.Remove(key + "#gap");
                    }
                    settings.ShowLogo = true;
                    settings.LogoAnchor = 0;   // (0.89: lines up by its left edge again, the default)
                    settings.ShowGameInfo = true;
                    settings.ShowButtonHints = true;
                    AfterPutBack();
                    NotifyHome();
                    NotifyLogoAnchor();
                    logger.Info("Kippix: the logo, game info, hints and filter name put back");
                }));
            }
        }
    }
}

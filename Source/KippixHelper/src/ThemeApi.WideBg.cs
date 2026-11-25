using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.7) Home settings › Background › Wide backgrounds. A background much wider than the screen (SteamGridDB's 3840×1240
    /// heroes, say) fills the screen by being zoomed into its middle. With this on, a picture like that is shown whole instead:
    /// as wide as the screen, at the top, fading to black under it. A picture the screen's shape (or taller) is left as it is.
    /// Kippix's own layer does it (KX_WideBg in Main.xaml, on the game page too), over Playnite's picture, from the copy the
    /// helper has already decoded (SelectedBackground).
    /// </summary>
    public partial class ThemeApi
    {
        private bool selectedBgWide;

        public ImageSource WideBgImage { get { return settings.WideBgFit && selectedBgWide ? selectedBackground : null; } }
        public bool WideBgHome { get { return settings.WideBgFit && selectedBgWide && HomeArtShown; } }
        public bool WideBgPage { get { return settings.WideBgFit && selectedBgWide; } }

        /// <summary>The picture just decoded: is it much wider than the screen?</summary>
        private void WideBgCheck(ImageSource img)
        {
            bool wide = false;
            var b = img as BitmapSource;
            if (b != null && b.PixelHeight > 0)
            {
                double screen = 16.0 / 9.0;
                try
                {
                    var win = Application.Current != null ? Application.Current.MainWindow : null;
                    if (win != null && win.ActualHeight > 0 && win.ActualWidth > 0) screen = win.ActualWidth / win.ActualHeight;
                }
                catch (Exception) { }
                wide = (double)b.PixelWidth / b.PixelHeight > screen * 1.15;
            }
            if (wide == selectedBgWide && !wide) return;
            selectedBgWide = wide;
            NotifyWideBg();
        }

        /// <summary>The selected game has no background of its own (or one on the web): nothing wide to show.</summary>
        private void WideBgNone()
        {
            selectedBackgroundPath = null;   // (so coming back to the last game decodes its picture again)
            if (!selectedBgWide) return;
            selectedBgWide = false;
            NotifyWideBg();
        }

        private void NotifyWideBg()
        {
            Notify("WideBgImage");
            Notify("WideBgHome");
            Notify("WideBgPage");
        }

        private PickRow WideBgRow()
        {
            return BoardSwitch("picture", "widebg", Loc("LOC_KX_WideBg", "Show wide backgrounds whole"), settings.WideBgFit,
                               Loc("LOC_KX_WideBgOn", "A wide picture (like SteamGridDB's 3840×1240) sits at the top and fades to black"),
                               Loc("LOC_KX_WideBgOff", "Wide pictures are zoomed in to fill the screen"),
                               () =>
                               {
                                   settings.WideBgFit = !settings.WideBgFit;
                                   SaveSettings();
                                   PlaySound("move");
                                   NotifyWideBg();
                                   if (openPanel == "options" && optionsPage == "background") BoardRowsAgain(BuildBgAnimRows, "widebg");
                               });
        }
    }
}

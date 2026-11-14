using System;

namespace KippixHelper
{
    /// <summary>
    /// The Steam Controller in Playnite fullscreen: Playnite reads it itself, and Steam (outside games) also turns it into
    /// keyboard keys, so every press arrived twice. SteamCopyFilter.cs keeps one copy of each press.
    /// </summary>
    public partial class ThemeApi
    {
        private SteamCopyFilter padCopies;

        private void InitSteamPad()
        {
            InitFocusGuard();
            InitTouch();   // (r12 G: touch and the mouse, one set of handlers on the main window)
            try
            {
                padCopies = new SteamCopyFilter(System.Windows.Application.Current != null ? System.Windows.Application.Current.Dispatcher : null, () => true, () => true, t => Dbg("pad copy: " + t),
                                                 e => ReferenceEquals(e, replayingA) || ReferenceEquals(e, remapRaised) || ReferenceEquals(e, touchKey));   // (presses sent again by Controller buttons, touch and the mouse)
                padCopies.Start();
            }
            catch (Exception ex) { logger.Warn(ex, "Kippix: controller copy check not available"); }
        }

        /// <summary>Playnite's controller pressed something within the last <paramref name="seconds"/> (its events, or keys it sends).</summary>
        private bool ControllerUsedWithin(double seconds)
        {
            return padCopies != null && (DateTime.UtcNow - padCopies.LastControllerInput).TotalSeconds < seconds;
        }
    }
}

using System;

namespace KippixHelper
{
    /// <summary>
    /// Forgotten your PIN? (r15): the helper's desktop settings (Playnite desktop › Add-ons › Kippix Helper) can take the
    /// grown-ups' PIN away. It's the only PIN: locked profiles, the Store / Settings locks and the time limit's "more time" all use
    /// it. Only from desktop mode, where the fullscreen PIN isn't needed to get in. The locks themselves stay set up
    /// (with no PIN they don't ask), so a new PIN set in Settings › Profiles puts them back as they were.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>A PIN is set (the desktop settings' "Forgotten your PIN?").</summary>
        internal bool HasProfilesPin { get { return !string.IsNullOrEmpty(settings.ProfilesPin); } }

        /// <summary>Takes the PIN away (from the desktop settings, when they're saved).</summary>
        internal void RemoveProfilesPin()
        {
            if (string.IsNullOrEmpty(settings.ProfilesPin)) return;
            settings.ProfilesPin = "";
            SaveSettings();
            logger.Info("Kippix: the grown-ups' PIN was removed from the desktop settings");
            OnUi(() => { try { NotifyProfiles(); } catch (Exception) { } });
        }
    }
}

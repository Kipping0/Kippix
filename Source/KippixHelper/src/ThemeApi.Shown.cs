using System;
using System.Linq;

namespace KippixHelper
{
    /// <summary>
    /// Settings rows that one setting makes pointless somewhere else (another page, or the layout you chose) are hidden there,
    /// not greyed. Each of these says whether a row (or a section) still does anything. They're worked out again whenever
    /// anything changes while the settings are open, and only the ones that changed are announced.
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly string[] ShownNames =
        {
            "HomeInfoShown", "RowCardBadgesShown", "FolderDupShown", "NotifyCardsOn", "TopBarKeepClockShown", "BgZoomShown",
            "WeatherSectionShown", "RecordSessionsShown"
        };
        private bool[] shownLast;
        private bool shownBusy, shownQueued;

        /// <summary>Once, from the constructor.</summary>
        private void HookShown()
        {
            PropertyChanged += (s, e) => ShownInputChanged(e.PropertyName);
        }

        private void ShownInputChanged(string name)
        {
            if (shownBusy || (name != null && Array.IndexOf(ShownNames, name) >= 0)) return;
            var d = ui;
            if (d == null) return;
            if (!d.CheckAccess())
            {
                if (shownQueued) return;
                shownQueued = true;
                d.BeginInvoke(new Action(() => { shownQueued = false; RefreshShown(); }));
                return;
            }
            RefreshShown();
        }

        private void RefreshShown()
        {
            // only the settings panel shows these: worked out afresh (all of them announced) each time it opens
            if (openPanel != "options") { shownLast = null; return; }
            bool[] now;
            try
            {
                now = new[] { HomeInfoShown, RowCardBadgesShown, FolderDupShown, NotifyCardsOn, TopBarKeepClockShown, BgZoomShown,
                              WeatherSectionShown, RecordSessionsShown };
            }
            catch (Exception ex) { logger.Warn("Kippix: settings rows: " + ex.Message); return; }
            var last = shownLast;
            shownLast = now;
            shownBusy = true;
            try
            {
                for (int i = 0; i < now.Length; i++)
                    if (last == null || last[i] != now[i]) Notify(ShownNames[i]);
            }
            finally { shownBusy = false; }
        }

        /// <summary>Row › Tall / Full Screen (also a folder's games in a Row) with "Show the logo, game info and hints" off.</summary>
        private bool RowHidesInfo { get { return RowSettingsShown && settings.RowDesign != 0 && !settings.RowShowInfo; } }
        /// <summary>Row › Tall / Full Screen chosen: the theme draws those cards itself, with only the Installed badge.</summary>
        private bool RowOwnCards { get { return RowSettingsShown && settings.RowDesign != 0; } }

        /// <summary>Logo, game info &amp; button hints: the logo, info and hints sections (Tall / Full Screen can hide all three).</summary>
        public bool HomeInfoShown { get { return !RowHidesInfo; } }
        /// <summary>Tile badges: New, Achievements and HowLongToBeat (and Games kept in place › Pin badge) are on Playnite's cards only.</summary>
        public bool RowCardBadgesShown { get { return !RowOwnCards; } }
        /// <summary>Folder › Show duplicates: only the theme's own row (Loop, Tall, Full Screen) can show a game twice.</summary>
        public bool FolderDupShown { get { return settings.FolderGameDesign == 0 && (settings.RowDesign != 0 || settings.RowLoop); } }
        /// <summary>Notifications › CARD LOOK: only while a card can come up (after playing, or other notices).</summary>
        public bool NotifyCardsOn { get { return settings.SessionSummary || settings.NotifyOther || settings.GameUpdateNotify; } }
        /// <summary>Top bar › Keep the clock visible: auto-hide on and the clock in the top bar.</summary>
        public bool TopBarKeepClockShown { get { return settings.AutoHideTopBar && settings.PinClock; } }
        /// <summary>Look and feel › MOTION (background zoom): not with Reduce motion, and only over the game's art (not a plain colour or your own picture).</summary>
        public bool BgZoomShown { get { return !settings.ReduceMotion && settings.HomeBgMode != 1 && !(settings.HomeBgMode == 3 && HomeCustomShown); } }
        /// <summary>Widgets › WEATHER: only with a weather widget (Weather touches set the town on their own page).</summary>
        public bool WeatherSectionShown { get { return Widgets != null && Widgets.Any(w => w != null && w.Config != null && w.Config.Type == "weather"); } }
        /// <summary>Keep a record of my play sessions: GameActivity keeps them instead, except for other profiles.</summary>
        public bool RecordSessionsShown { get { return ProfilesOn || !GameActivityInstalled; } }
    }
}

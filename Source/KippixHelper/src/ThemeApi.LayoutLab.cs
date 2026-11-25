using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.7) The screens' designs. Layout changes WIP was taken out: the Console designs (the user's mock-ups) are the only
    /// ones now, and the old designs were deleted. The Alternative game page has only its own design.
    /// </summary>
    public partial class ThemeApi
    {
        private int LabLook(string area) { return area == null || area == "altgame" ? 0 : 1; }

        // ---------------- where a design will be put on (called as screens open; nothing to do with only Default) ----------------

        private readonly Dictionary<string, string> labState = new Dictionary<string, string>();

        private void LabApplySoon() { }

        // ---------------- (1.0.7) Search: recent searches, off unless switched on (Settings › General) ----------------

        public bool SearchShowsRecent
        {
            get { return settings.SearchShowRecent; }
            set { if (settings.SearchShowRecent == value) return; settings.SearchShowRecent = value; SaveSettings(); Notify("SearchShowsRecent"); Notify("ShowSearchRecent"); }
        }
    }
}

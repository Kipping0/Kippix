using System;

namespace KippixHelper
{
    /// <summary>Hooks for what 0.32 added (the extra pages, quick settings).</summary>
    public partial class ThemeApi
    {
        private void NewPagesOptionsOpened(string page)
        {
        }

        private void InitNewPages()
        {
            InitPages();
            InitQuickSettings();
        }
    }
}

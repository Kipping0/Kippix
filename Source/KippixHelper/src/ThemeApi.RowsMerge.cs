using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.86) A menu page's rows brought up to date while you're on it (something finished checking in the background)
    /// without the highlight jumping: rows that are still the same rows change where they stand; if rows came or went,
    /// the highlight goes back onto the row it was on (by its key), never to the top.
    /// </summary>
    public partial class ThemeApi
    {
        private void MergeRows(ObservableCollection<PickRow> live, ObservableCollection<PickRow> fresh, string listName)
        {
            if (live == null || fresh == null) return;
            bool same = live.Count == fresh.Count;
            for (int i = 0; same && i < live.Count; i++)
                if (live[i].Key != fresh[i].Key || live[i].IsHeader != fresh[i].IsHeader) same = false;
            if (same)
            {
                for (int i = 0; i < live.Count; i++) live[i].BecomeLike(fresh[i]);
                return;
            }
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var was = fe != null ? fe.DataContext as PickRow : null;
            string key = was != null && live.Contains(was) ? was.Key : null;
            int at = was != null ? live.IndexOf(was) : -1;
            live.Clear();
            foreach (var r in fresh) live.Add(r);
            if (at < 0) return;   // (the highlight wasn't on this list)
            ui.BeginInvoke(new Action(() =>
            {
                var row = key != null ? live.FirstOrDefault(r => r.Key == key) : null;
                if (row == null && live.Count > 0) row = live[Math.Min(at, live.Count - 1)];
                if (row != null) UiTools.FocusItem(listName, row);
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>Settings › Save backups, brought up to date where it stands.</summary>
        private void SavesRowsInPlace()
        {
            var live = SavesRows;
            savesRows = new ObservableCollection<PickRow>();
            try { BuildSavesRows(); }
            finally { var fresh = savesRows; savesRows = live; MergeRows(live, fresh, "KX_SavesList"); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.6) Media's order and what's hidden (MediaOrder, MediaHidden). (1.1.0) The old edit mode (the pencil, the list with
    /// the eyes) was taken out: hold A on a tile moves it (ThemeApi.MediaTiles.cs), X on a tile › Hide this tile hides it,
    /// and Media settings lists the hidden ones to show them again. Services added later go at the end, shown.
    /// </summary>
    public partial class ThemeApi
    {

        private IEnumerable<MediaService> MediaServicesInOrder()
        {
            var order = settings.MediaOrder ?? new List<string>();
            return AllMediaServices.Select((m, i) => new { m, i })
                                .OrderBy(x => { int k = order.IndexOf(x.m.Key); return k >= 0 ? k : order.Count + x.i; })
                                .Select(x => x.m);
        }

        private bool IsMediaRowKey(string key)
        {
            return key == "localmedia" || AllMediaServices.Any(m => m.Key == key);
        }

        /// <summary>Only the streaming services move (Shows &amp; movies stays at the top, Opens in at the bottom).</summary>
        private bool ProfileRowMovable(PickRow row)
        {
            if (profilePage != "media") return true;
            return row != null && AllMediaServices.Any(m => m.Key == row.Key);
        }

        private void SaveMediaOrder()
        {
            var old = settings.MediaOrder ?? new List<string>();
            settings.MediaOrder = ProfileRows.Where(r => !r.IsHeader && AllMediaServices.Any(m => m.Key == r.Key)).Select(r => r.Key).ToList();
            int at = old.IndexOf("localmedia");   // (1.1.0) where Shows & movies was moved to on the tiles stays
            if (at >= 0) settings.MediaOrder.Insert(Math.Min(at, settings.MediaOrder.Count), "localmedia");
            SaveSettings();
        }

        /// <summary>Hidden tiles out.</summary>
        private void ApplyMediaEdit()
        {
            var hidden = settings.MediaHidden ?? (settings.MediaHidden = new List<string>());
            foreach (var r in ProfileRows.Where(r => r.Key != null && hidden.Contains(r.Key)).ToList()) ProfileRows.Remove(r);
        }

        private bool IsMediaHidden(string key) { return key != null && settings.MediaHidden != null && settings.MediaHidden.Contains(key); }

        /// <summary>Hide this tile / Show this tile (a tile's page, Media settings' hidden list).</summary>
        private void SetMediaHidden(string key, bool hide)
        {
            if (!IsMediaRowKey(key)) return;
            var hidden = settings.MediaHidden ?? (settings.MediaHidden = new List<string>());
            hidden.Remove(key);
            if (hide) hidden.Add(key);
            SaveSettings();
        }
    }
}

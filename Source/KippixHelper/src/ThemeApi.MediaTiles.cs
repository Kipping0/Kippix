using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// (1.1.0) The Media tiles have their own list (MediaTiles), so Media settings (Y) and a tile's page (X) open in the menu
    /// over them instead of in their place. Hold A on a tile to pick it up: the D-pad moves it round the grid, A (or B) puts
    /// it down. Media settings › Tile names hides the names under the tiles.
    /// </summary>
    public partial class ThemeApi
    {
        private ObservableCollection<PickRow> mediaTiles = new ObservableCollection<PickRow>();
        private string mediaTileMoving;

        public ObservableCollection<PickRow> MediaTiles { get { return mediaTiles; } }

        /// <summary>The tiles take the controller (nothing open over them).</summary>
        public bool MediaConsoleActive { get { return MediaConsoleShown && profilePage == "media"; } }

        public bool MediaTileNames { get { return settings.MediaTileNames; } }

        private static bool IsMediaMenuPage(string page) { return page == "media" || page == "mediaset" || page == "mediasrc"; }

        /// <summary>After Media's rows are built: the tiles are those rows (not in edit mode: that's the list).</summary>
        private void SyncMediaTiles()
        {
            if (IsProfileReorder) return;
            mediaTiles = new ObservableCollection<PickRow>(OrderMediaTiles(ProfileRows));
            Notify("MediaTiles");
        }

        /// <summary>
        /// The tiles in your order: Shows &amp; movies can be moved among them too (held A), so its place is kept in the order
        /// with the services ("localmedia"); first when it's never been moved. (The menu's list keeps its STREAMING heading.)
        /// </summary>
        private List<PickRow> OrderMediaTiles(IEnumerable<PickRow> rows)
        {
            var list = rows.Where(r => !r.IsHeader).ToList();
            var order = settings.MediaOrder ?? new List<string>();
            int at = order.IndexOf("localmedia");
            var local = list.FirstOrDefault(r => r.Key == "localmedia");
            if (local == null || at < 0) return list;
            list.Remove(local);
            int pos = list.Count;
            for (int k = 0; k < list.Count; k++) { int o = order.IndexOf(list[k].Key ?? ""); if (o > at) { pos = k; break; } }
            list.Insert(pos, local);
            return list;
        }

        private bool IsMovableTileKey(string key) { return key != null && (key == "localmedia" || AllMediaServices.Any(m => m.Key == key)); }

        /// <summary>The tiles built again while a page is open over them (a new name or background shows straight away).</summary>
        private void RefreshMediaTiles()
        {
            var real = ProfileRows;
            string page = profilePage;
            bool reorder = profileReorder;
            try
            {
                ProfileRows = new ObservableCollection<PickRow>();
                profilePage = "media";
                profileReorder = false;
                BuildMediaRows();
                mediaTiles = new ObservableCollection<PickRow>(OrderMediaTiles(ProfileRows));
            }
            catch (Exception ex) { logger.Warn("Kippix: media tiles: " + ex.Message); }
            finally
            {
                ProfileRows = real;
                profilePage = page;
                profileReorder = reorder;
            }
            Notify("MediaTiles");
        }

        private void FocusMediaTile(string key)
        {
            ui.BeginInvoke(new Action(() =>
            {
                HookMediaArea();   // (the tiles fill the width)
                var row = (key != null ? mediaTiles.FirstOrDefault(r => r.Key == key) : null) ?? mediaTiles.FirstOrDefault(r => r.IsRow && r.Command != null);
                if (row != null) UiTools.FocusItem("KX_MediaTileList", row);
            }), DispatcherPriority.ContextIdle);
        }

        private void ToggleMediaTileNames()
        {
            settings.MediaTileNames = !settings.MediaTileNames;
            SaveSettings();
            PlaySound("move");
            Notify("MediaTileNames");
            var row = ProfileRows.FirstOrDefault(r => r.Key == "tilenames");
            if (row != null) row.SetSub(MediaTileNamesText);
        }

        private string MediaTileNamesText { get { return settings.MediaTileNames ? Loc("LOC_KX_Shown", "Shown") : Loc("LOC_KX_Hidden", "Hidden"); } }

        // ---------------- hold A: pick a tile up and move it ----------------

        private PickRow FocusedMediaTile()
        {
            var fe = Keyboard.FocusedElement as FrameworkElement;
            return fe != null ? fe.DataContext as PickRow : null;
        }

        /// <summary>Hold A works on every tile (Shows &amp; movies too).</summary>
        private bool MediaTileHoldApplies()
        {
            if (openPanel != "profile" || !MediaConsoleActive || mediaTileMoving != null || confirmOpen || kbOpen || colourOpen) return false;
            var row = FocusedMediaTile();
            return row != null && IsMovableTileKey(row.Key) && mediaTiles.Contains(row);
        }

        private void StartMediaTileMove(IInputElement held)
        {
            var fe = held as FrameworkElement;
            var row = fe != null ? fe.DataContext as PickRow : null;
            if (row == null || !mediaTiles.Contains(row)) return;
            mediaTileMoving = row.Key;
            row.IsSelected = true;
            PlaySound("select");
            FocusMediaTile(row.Key);
        }

        private void DropMediaTile()
        {
            if (mediaTileMoving == null) return;
            var row = mediaTiles.FirstOrDefault(r => r.Key == mediaTileMoving);
            if (row != null) row.IsSelected = false;
            string key = mediaTileMoving;
            mediaTileMoving = null;
            var keys = mediaTiles.Where(r => IsMovableTileKey(r.Key)).Select(r => r.Key).ToList();
            foreach (var k in settings.MediaOrder ?? new List<string>()) if (!keys.Contains(k)) keys.Add(k);   // (hidden ones keep a place)
            settings.MediaOrder = keys;
            SaveSettings();
            PlaySound("select");
            FocusMediaTile(key);
        }

        /// <summary>The controller's buttons while a tile is picked up: A or B puts it down, the rest wait.</summary>
        private void MediaTileMovePad(ControllerInput button, KeyEventArgs e)
        {
            if (IsPadRelease(e)) return;
            if (button == ControllerInput.A || button == ControllerInput.B) DropMediaTile();
        }

        /// <summary>The arrows (the D-pad and stick come as these) move it; Enter / Esc put it down.</summary>
        private void MediaTileMoveKey(KeyEventArgs e)
        {
            if (e.Key == Key.System) return;
            e.Handled = true;
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (the buttons: MediaTileMovePad)
            int cols = MediaTileCols;
            switch (e.Key)
            {
                case Key.Left: MoveMediaTile(-1); break;
                case Key.Right: MoveMediaTile(1); break;
                case Key.Up: MoveMediaTile(-cols); break;
                case Key.Down: MoveMediaTile(cols); break;
                case Key.Enter: case Key.Space: case Key.Escape: case Key.Back: if (!e.IsRepeat) DropMediaTile(); break;
            }
        }

        /// <summary>
        /// The arrows on the tiles: straight to the next tile, scrolled so all of it shows. (The ScrollViewer on its own scrolls a
        /// little first when the next tile is out of sight, and only goes to it on the next press.)
        /// </summary>
        private bool MediaTileArrow(KeyEventArgs e)
        {
            if (openPanel != "profile" || !MediaConsoleActive || mediaTileMoving != null) return false;
            FocusNavigationDirection dir;
            switch (e.Key)
            {
                case Key.Left: dir = FocusNavigationDirection.Left; break;
                case Key.Right: dir = FocusNavigationDirection.Right; break;
                case Key.Up: dir = FocusNavigationDirection.Up; break;
                case Key.Down: dir = FocusNavigationDirection.Down; break;
                default: return false;
            }
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var row = fe != null ? fe.DataContext as PickRow : null;
            if (row == null || !mediaTiles.Contains(row)) return false;
            e.Handled = true;
            FrameworkElement next = null;
            try { next = fe.PredictFocus(dir) as FrameworkElement; } catch (Exception) { }
            var nextRow = next != null ? next.DataContext as PickRow : null;
            if (next == null || nextRow == null || !mediaTiles.Contains(nextRow)) return true;   // (the edge: stays put)
            Keyboard.Focus(next);
            try { next.BringIntoView(new Rect(-24, -24, next.ActualWidth + 48, next.ActualHeight + 48)); } catch (Exception) { }
            return true;
        }

        private void MoveMediaTile(int by)
        {
            int i = -1;
            for (int k = 0; k < mediaTiles.Count; k++) if (mediaTiles[k].Key == mediaTileMoving) { i = k; break; }
            if (i < 0) return;
            int j = Math.Max(0, Math.Min(mediaTiles.Count - 1, i + by));
            while (j != i && !IsMovableTileKey(mediaTiles[j].Key)) j += by > 0 ? -1 : 1;
            if (j == i) { PlaySound("back"); return; }
            mediaTiles.Move(i, j);
            PlaySound("move");
            FocusMediaTile(mediaTileMoving);
        }
    }
}

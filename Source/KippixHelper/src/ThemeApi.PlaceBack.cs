using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace KippixHelper
{
    /// <summary>
    /// Home settings › Put things back, and Game page settings › Put things back: every part of that screen with what's been
    /// done to it (moved / resized), and A on one puts it back where it comes, at its own size. For a part moved off the screen
    /// (it can happen, a bit of clipping is fine) that Free move can't reach any more. "Put everything back" does them all.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<PickRow> PlaceRows { get; private set; }
        public bool IsOptionsPlace { get { return optionsPage == "placehome" || optionsPage == "placegame"; } }
        private ICommand openPlaceCmd;
        public ICommand OpenPlaceCommand
        {
            get
            {
                return openPlaceCmd ?? (openPlaceCmd = new RelayCommand(p =>
                {
                    string screen = p as string == "placegame" ? "game" : "home";
                    PlaySound("select");
                    BuildPlaceRows(screen);
                    SetOptionsPage(screen == "game" ? "placegame" : "placehome");
                }));
            }
        }

        private string PlaceState(string key)
        {
            var o = MasterOffset(key);
            bool moved = Math.Abs(o[0]) >= 0.5 || Math.Abs(o[1]) >= 0.5;
            var s = MasterSize(key);
            bool sized = IsScaleKey(key) ? Math.Abs(MasterScale(key) - 1) > 0.001 : (s[0] > 0 || s[1] > 0);
            if (Math.Abs(MasterRotation(key)) > 0.001)
            {
                if (moved && sized) return Loc("LOC_KX_PlaceMovedSizedTurned", "Moved, resized and rotated   ·   Put it back");
                if (moved) return Loc("LOC_KX_PlaceMovedTurned", "Moved and rotated   ·   Put it back");
                if (sized) return Loc("LOC_KX_PlaceSizedTurned", "Resized and rotated   ·   Put it back");
                return Loc("LOC_KX_PlaceTurned", "Rotated   ·   Put it back");
            }
            if (moved && sized) return Loc("LOC_KX_PlaceMovedSized", "Moved and resized   ·   A: put it back");
            if (moved) return Loc("LOC_KX_PlaceMoved", "Moved   ·   A: put it back");
            if (sized) return Loc("LOC_KX_PlaceSized", "Resized   ·   A: put it back");
            return Loc("LOC_KX_PlaceOwn", "Where it comes");
        }

        private void BuildPlaceRows(string screen)
        {
            if (PlaceRows == null) PlaceRows = new ObservableCollection<PickRow>();
            PlaceRows.Clear();
            PlaceRows.Add(new PickRow { IsHeader = true, Text = screen == "game" ? Loc("LOC_KX_PlaceGameHeader", "THE GAME PAGE") : Loc("LOC_KX_PlaceHomeHeader", "THE HOME SCREEN") });
            foreach (var el in MasterElements.Where(m => m.Screen == screen))
            {
                string key = el.Key;
                PickRow row = null;
                row = new PickRow
                {
                    Text = Loc(el.LocKey, el.Fallback), Sub = PlaceState(key), Icon = "layout", Key = key,
                    Command = new RelayCommand(_ =>
                    {
                        PutBack(key);
                        AfterPutBack();
                        var shown = PlaceRows.FirstOrDefault(r => r.Key == key);
                        if (shown != null) shown.SetSub(PlaceState(key));   // changes where it is: the highlight stays
                    })
                };
                PlaceRows.Add(row);
            }
            // all of them last (the page opens on the first part, so a quick second A can't put everything back)
            PlaceRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_PlaceAllHeader", "EVERYTHING") });
            PlaceRows.Add(new PickRow
            {
                Text = Loc("LOC_KX_PlaceAll", "Put everything back"),
                Sub = Loc("LOC_KX_PlaceAllSub2", "Every part of this screen back where it comes, at its own size and straight"),
                Icon = "refresh", Key = "all",
                Command = new RelayCommand(_ =>
                {
                    foreach (var el in MasterElements.Where(m => m.Screen == screen)) PutBack(el.Key);
                    AfterPutBack();
                    foreach (var r in PlaceRows) if (r.Key != "all" && r.Key != null) r.SetSub(PlaceState(r.Key));
                })
            });
            Notify("PlaceRows");
        }

        /// <summary>One part's place, size and turn back to how it comes (the game cards' own size settings stay as they are).</summary>
        private void PutBack(string key)
        {
            SetMasterOffset(key, 0, 0);
            SetMasterRotation(key, 0);
            if (settings.MasterSizes != null) settings.MasterSizes.Remove(key);
        }

        private void AfterPutBack()
        {
            PlaySound("select");
            SaveSettings();
            RefreshMasterItems();
            ApplyMasterOffsets();
            ApplyHomeLayoutSoon();
            HomeModeChanged();
            NotifyMoved();
        }
    }
}

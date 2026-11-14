using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.11) The profile menu (Profile, Achievements, Store, Media…): the library refresh and an edit button at its top right.
    /// Edit puts the menu in order mode: A picks a row up, Up / Down moves it, A puts it down; Done (the same button) or B
    /// leaves. The order is kept (ProfileMenuOrder); rows that are new since stay where they'd normally be, at the end.
    /// </summary>
    public partial class ThemeApi
    {
        private bool profileReorder;
        private string profileMovingKey;
        private ICommand profileRefreshCmd, profileEditCmd;

        private bool ProfileReorderMoving { get { return openPanel == "profile" && IsProfileReorder && profileMovingKey != null; } }
        public bool ProfileHeadButtons { get { return profilePage == ""; } }
        public bool ProfileRefreshShown { get { return profilePage == "" && !profileReorder; } }
        public bool IsProfileReorder { get { return profileReorder && profilePage == ""; } }
        public bool IsNotProfileReorder { get { return !IsProfileReorder; } }
        public ICommand ProfileRefreshCommand { get { return profileRefreshCmd ?? (profileRefreshCmd = new RelayCommand(_ => RefreshGameLibrary())); } }
        public ICommand ProfileEditCommand { get { return profileEditCmd ?? (profileEditCmd = new RelayCommand(_ => ProfileReorderToggle())); } }

        private void NotifyProfileHead()
        {
            foreach (string n in new[] { "ProfileHeadButtons", "ProfileRefreshShown", "IsProfileReorder", "IsNotProfileReorder", "ProfileMenuTitle" }) Notify(n);
        }

        private void ProfileReorderToggle()
        {
            PlaySound("select");
            profileReorder = !profileReorder;
            profileMovingKey = null;
            BuildProfileRows();
            NotifyProfileHead();
            ui.BeginInvoke(new Action(() =>
            {
                var first = ProfileRows.FirstOrDefault(r => !r.IsHeader && r.IsRow);
                if (first != null) UiTools.FocusItem("KX_ProfileList", first);
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>The main menu's rows in your order; in order mode, each row picks up / puts down.</summary>
        private void ApplyProfileOrder()
        {
            var order = settings.ProfileMenuOrder;
            if (order != null && order.Count > 0)
            {
                var sorted = ProfileRows.Select((r, i) => new { r, i })
                                        .OrderBy(x => { int k = order.IndexOf(x.r.Key ?? ""); return k >= 0 ? k : order.Count + x.i; })
                                        .Select(x => x.r).ToList();
                ProfileRows.Clear();
                foreach (var r in sorted) ProfileRows.Add(r);
            }
            if (!profileReorder) return;
            foreach (var r in ProfileRows)
            {
                if (r.IsHeader) continue;
                string key = r.Key;
                bool moving = key == profileMovingKey;
                r.Sub = moving ? Loc("LOC_KX_ReorderMoving", "Up or down to move it, A to put it down") : null;
                r.IsSelected = moving;
                if (moving) r.Icon = "move";
                r.Command = new RelayCommand(_ => ProfilePickRow(key));
            }
        }

        private void ProfilePickRow(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlaySound("select");
            profileMovingKey = profileMovingKey == key ? null : key;
            if (profileMovingKey == null) SaveProfileOrder();
            BuildProfileRows();
            ProfileFocusKey(key);
        }

        /// <summary>Up / Down while a row is picked up: it moves.</summary>
        private bool ProfileMoveKey(int dir)
        {
            if (openPanel != "profile" || !IsProfileReorder || profileMovingKey == null) return false;
            int i = -1;
            for (int k = 0; k < ProfileRows.Count; k++) if (ProfileRows[k].Key == profileMovingKey) { i = k; break; }
            if (i < 0) return false;
            int j = i + dir;
            while (j >= 0 && j < ProfileRows.Count && (ProfileRows[j].IsHeader || !ProfileRows[j].IsRow)) j += dir;
            if (j < 0 || j >= ProfileRows.Count) { PlaySound("back"); return true; }
            ProfileRows.Move(i, j);
            PlaySound("move");
            SaveProfileOrder();
            ProfileFocusKey(profileMovingKey);
            return true;
        }

        private void SaveProfileOrder()
        {
            settings.ProfileMenuOrder = ProfileRows.Where(r => !r.IsHeader && !string.IsNullOrEmpty(r.Key)).Select(r => r.Key).ToList();
            SaveSettings();
        }

        private void ProfileFocusKey(string key)
        {
            ui.BeginInvoke(new Action(() =>
            {
                var row = ProfileRows.FirstOrDefault(r => r.Key == key);
                if (row != null) UiTools.FocusItem("KX_ProfileList", row);
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>B in order mode: a picked-up row goes down where it is; otherwise order mode ends.</summary>
        private bool ProfileReorderBack()
        {
            if (openPanel != "profile" || !IsProfileReorder) return false;
            if (profileMovingKey != null)
            {
                string key = profileMovingKey;
                profileMovingKey = null;
                PlaySound("back");
                SaveProfileOrder();
                BuildProfileRows();
                ProfileFocusKey(key);
                return true;
            }
            ProfileReorderToggle();
            return true;
        }
    }
}

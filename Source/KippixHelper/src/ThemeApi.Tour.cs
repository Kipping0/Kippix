using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// First-time tips: the first time a screen opens (Free edit, the Store, your profile, folders, the game page…) a small card
    /// says how it works ("Y searches, X adds to your wishlist…"). It never takes the highlight: any button hides it (and still
    /// does what it does), or it goes by itself after a few seconds or when the screen changes. Each shows once; Settings › Help
    /// switches them off or shows them all again (Reset tips). Also this round's tick work (handheld, Doctor, touch).
    /// </summary>
    public partial class ThemeApi
    {
        private string tipKey;
        private DateTime tipAt;
        public bool IsTipShown { get { return tipKey != null; } }
        public string TipTitle { get; private set; }
        public string TipText { get; private set; }
        public string TipHint { get { return Loc("LOC_KX_FtHint", "Any button hides this   ·   Tips can be switched off in Settings › Help"); } }

        public bool TipsOn
        {
            get { return settings.TipsOn; }
            set { if (settings.TipsOn == value) return; settings.TipsOn = value; SaveSettings(); Notify("TipsOn"); if (!value) HideTip(); }
        }

        private ICommand resetTipsCmd;
        /// <summary>Settings › Help › Reset tips: every tip shows again, the next time its screen opens.</summary>
        public ICommand ResetTipsCommand
        {
            get
            {
                return resetTipsCmd ?? (resetTipsCmd = new RelayCommand(_ =>
                {
                    if (settings.TipsSeen != null) settings.TipsSeen.Clear();
                    settings.TipsOn = true;
                    SaveSettings();
                    PlaySound("select");
                    Notify("TipsOn");
                    SetHelpStatus(Loc("LOC_KX_FtReset", "Tips will show again, the next time each screen opens."));
                }));
            }
        }

        // ---------------- this round's tick ----------------

        private bool gStarted;

        /// <summary>Every 2 s with the theme's own tick (nothing of its own runs in the background).</summary>
        private void GTick()
        {
            if (!gStarted)
            {
                gStarted = true;
                if (IsFullscreen) HandheldStart();
            }
            DoctorTick();
            TipsTick();
            EnsureTouchInput();
        }

        // ---------------- tips ----------------

        /// <summary>Which screen is showing now, for its tip (null: none fits, or not now).</summary>
        private string TipScreenNow()
        {
            if (!IsFullscreen || !settings.TipsOn || ui == null) return null;
            if (openPanel == "welcome" || openPanel == "who" || openPanel == "browser" || openPanel == "trailer" || confirmOpen || kbOpen || ambientOn || gameRunningNow) return null;
            if (freeMove || IsMasterMoving) return "freeedit";
            if (boardEditing) return "boardedit";
            switch (openPanel)
            {
                case "store": return "store";
                case "achievements": return "achievements";
                case "gallery": return "gallery";
                case "search": return "search";
                case "options": return "settings";
                case "page": return IsProfileHub ? "hub" : null;
                case null: break;
                default: return null;
            }
            if (detailsOpen) return "gamepage";
            if (IsFolderMode) return "folders";
            if (IsBoardMode) return "board";
            return "home";
        }

        private bool TipFor(string key, out string title, out string text)
        {
            title = text = null;
            switch (key)
            {
                case "home":
                    title = Loc("LOC_KX_FtHome", "Your home screen");
                    text = Loc("LOC_KX_FtHomeText", "Hold A on a game to move or resize anything (Free edit). Y searches; ⋯ at the top has every setting.");
                    break;
                case "gamepage":
                    title = Loc("LOC_KX_FtGame", "The game page");
                    text = Loc("LOC_KX_FtGameText", "Press X on a part for its settings, hold A to move things around, and go down for more about the game.");
                    break;
                case "freeedit":
                    title = Loc("LOC_KX_FtFree", "Free edit");
                    text = Loc("LOC_KX_FtFreeText", "Point at anything with the D-pad. A picks it up to move it, Y sizes it, X opens its settings, View deletes it. B when you're done.");
                    break;
                case "boardedit":
                    title = Loc("LOC_KX_FtBoardEdit", "Editing the board");
                    text = Loc("LOC_KX_FtBoardEditText", "A adds a tile or picks one up, X opens its settings, Y removes it. B when you're done.");
                    break;
                case "board":
                    title = Loc("LOC_KX_FtBoard", "The Board");
                    text = Loc("LOC_KX_FtBoardText", "Hold A on a tile to edit the board: tiles of any size, widgets and pages.");
                    break;
                case "folders":
                    title = Loc("LOC_KX_FtFolders", "Folders");
                    text = Loc("LOC_KX_FtFoldersText", "Folders gather your games. Make more in Settings › Home settings › Game cards and layout › Your folders.");
                    break;
                case "store":
                    title = Loc("LOC_KX_FtStore", "The Store");
                    text = Loc("LOC_KX_FtStoreText", "Y searches, X puts a game on your wishlist, LT / RT jump a screen at a time. Prices from every store are on each game's page.");
                    break;
                case "hub":
                    title = Loc("LOC_KX_FtHub", "Your profile");
                    text = Loc("LOC_KX_FtHubText", "LB / RB switch tabs (library, friends, wishlist, your year…); LT / RT change the month or year.");
                    break;
                case "achievements":
                    title = Loc("LOC_KX_FtAch", "Achievements");
                    text = Loc("LOC_KX_FtAchText", "Y searches; on a game's achievements LB / RB step through them one at a time.");
                    break;
                case "gallery":
                    title = Loc("LOC_KX_FtGallery", "Gallery");
                    text = Loc("LOC_KX_FtGalleryText", "X marks a favourite, Y picks several at once, LB / RB step through them full screen.");
                    break;
                case "search":
                    title = Loc("LOC_KX_FtSearch", "Search");
                    text = Loc("LOC_KX_FtSearchText", "Type with the on-screen keyboard (or a real one): the results show as you type.");
                    break;
                case "settings":
                    title = Loc("LOC_KX_FtSettings", "Settings & Customisation");
                    text = Loc("LOC_KX_FtSettingsText", "Search settings at the top finds any setting. Help has Kippix Doctor if something goes wrong.");
                    break;
                default: return false;
            }
            return !string.IsNullOrEmpty(text);
        }

        private void TipsTick()
        {
            if (tipKey != null)
            {
                string now = TipScreenNow();
                if (now != tipKey || (DateTime.Now - tipAt).TotalSeconds > 12) HideTip();
                return;
            }
            if (ticks < 3) return;   // (not while starting up)
            string key = TipScreenNow();
            if (key == null) return;
            if (settings.TipsSeen == null) settings.TipsSeen = new List<string>();
            if (settings.TipsSeen.Contains(key)) return;
            string title, text;
            if (!TipFor(key, out title, out text)) return;
            settings.TipsSeen.Add(key);   // shown once (seen as soon as it shows)
            SaveSettings();
            tipKey = key;
            tipAt = DateTime.Now;
            TipTitle = title;
            TipText = text;
            Notify("TipTitle");
            Notify("TipText");
            Notify("TipHint");
            Notify("IsTipShown");
            if (ReduceMotionNow) return;
            // a short fade in (none with Reduce motion)
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    var card = UiTools.FindByName(System.Windows.Application.Current.MainWindow, "KX_TipCard") as System.Windows.UIElement;
                    if (card != null) card.BeginAnimation(System.Windows.UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
                }
                catch (Exception) { }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>From NotifyPanels: a panel opened or closed, its tip (or the tip going) without waiting for the tick.</summary>
        private void TipsSoon()
        {
            if (ui == null || !IsFullscreen) return;
            ui.BeginInvoke(new Action(() => { try { TipsTick(); } catch (Exception) { } }), DispatcherPriority.ContextIdle);
        }

        /// <summary>A button or key: the tip goes (once it's been up a moment; the press still does its job).</summary>
        private void TipsAnyKey()
        {
            if (tipKey != null && (DateTime.Now - tipAt).TotalMilliseconds > 1200) HideTip();
        }

        private void HideTip()
        {
            if (tipKey == null) return;
            tipKey = null;
            Notify("IsTipShown");
        }
    }
}

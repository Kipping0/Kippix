using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.4, issue: Bencey17) The Menu (Start) and View (Select) buttons on the home screen and the game page. Playnite's own
    /// menus (Menu: the game's options; View: Playnite's menu) are the default; each can do one of Kippix's jobs instead, or
    /// nothing. Settings › General › Controller buttons. Read after an action is moved (the button it stands for), so moving
    /// "Game options" to LB moves this with it.
    /// With Playnite's menu off View, it's still in Settings › Help › Playnite's menu, and Playnite's options for a game are at
    /// the bottom of Kippix's More menu while Menu does something else.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>A Menu / View press (or its let-go) on the home screen or the game page that Kippix does something else with.</summary>
        private bool StartViewKey(ControllerInput button, bool pressed)
        {
            if (button != ControllerInput.Start && button != ControllerInput.Back) return false;
            int mode = button == ControllerInput.Start ? settings.PadStartDoes : settings.PadViewDoes;
            if (mode <= 0 || mode > 3) return false;
            if (openPanel != null || confirmOpen || freeMove || IsMasterMoving || kbOpen || descReaderOpen || padCapture != null) return false;
            if (!pressed) return true;   // the let-go of one taken: nothing (Playnite's binding doesn't see it either)
            if (mode == 3) return true;  // nothing
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (button == ControllerInput.Start)
                    {
                        if (mode == 1) OpenGamePanel("gamemore");   // Kippix's game options (the More menu)
                        else if (mode == 2) OpenSearch();
                    }
                    else
                    {
                        if (mode == 1) ToggleProfile();              // Kippix's menu
                        else if (mode == 2) { PlaySound("select"); TogglePanel("options"); }   // Settings
                    }
                }
                catch (Exception ex) { logger.Warn("Kippix: Menu / View button: " + ex.Message); }
            }), DispatcherPriority.Input);
            return true;
        }

        private string PadStartDoesText(int m)
        {
            switch (m)
            {
                case 1: return Loc("LOC_KX_PadDoesKippixGame", "Kippix's game options");
                case 2: return Loc("LOC_KX_PadDoesSearch", "Search");
                case 3: return Loc("LOC_KX_PadDoesNothing", "Nothing");
            }
            return Loc("LOC_KX_PadDoesPlayniteGame", "Playnite's game options");
        }

        private string PadViewDoesText(int m)
        {
            switch (m)
            {
                case 1: return Loc("LOC_KX_PadDoesKippixMenu", "Kippix's menu");
                case 2: return Loc("LOC_KX_PadDoesSettings", "Settings");
                case 3: return Loc("LOC_KX_PadDoesNothing", "Nothing");
            }
            return Loc("LOC_KX_PadDoesPlayniteMenu", "Playnite's menu");
        }

        /// <summary>Controller buttons page: the Menu and View rows (A steps through what each does).</summary>
        private void AddStartViewRows(List<PickRow> rows)
        {
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecMenuView", "MENU AND VIEW BUTTONS") });
            rows.Add(new PickRow
            {
                Key = "padstart", Icon = "menu",
                Text = string.Format(Loc("LOC_KX_PadStartRow", "Menu (Start): {0}"), PadStartDoesText(settings.PadStartDoes)),
                Sub = Loc("LOC_KX_PadStartSub", "On the home screen and the game page. Playnite's game options, Kippix's, Search or nothing"),
                Command = new RelayCommand(_ =>
                {
                    settings.PadStartDoes = (Math.Max(0, Math.Min(3, settings.PadStartDoes)) + 1) % 4;
                    SaveSettings();
                    PlaySound("move");
                    GRowsAgain("padstart");
                })
            });
            rows.Add(new PickRow
            {
                Key = "padview", Icon = "menu",
                Text = string.Format(Loc("LOC_KX_PadViewRow", "View (Select): {0}"), PadViewDoesText(settings.PadViewDoes)),
                Sub = settings.PadViewDoes == 0 ? Loc("LOC_KX_PadViewSub", "On the home screen and the game page. Playnite's menu, Kippix's menu, Settings or nothing")
                                                : Loc("LOC_KX_PadViewSubMoved", "Playnite's menu is still in Settings › Help"),
                Command = new RelayCommand(_ =>
                {
                    settings.PadViewDoes = (Math.Max(0, Math.Min(3, settings.PadViewDoes)) + 1) % 4;
                    SaveSettings();
                    PlaySound("move");
                    GRowsAgain("padview");
                })
            });
        }

        // ---------------- Playnite's own menus, from Kippix ----------------

        private ICommand openPlayniteMenuCommand;
        /// <summary>Settings › Help › Playnite's menu: Playnite's own fullscreen menu (its settings, switch to desktop, exit…).</summary>
        public ICommand OpenPlayniteMenuCommand
        {
            get { return openPlayniteMenuCommand ?? (openPlayniteMenuCommand = new RelayCommand(_ => { PlaySound("select"); CloseThenRun("OpenMainMenuCommand"); })); }
        }

        /// <summary>The bottom of Kippix's More menu while Menu does something else: Playnite's own options for the game.</summary>
        private void AddPlayniteGameMenuRow(Game g)
        {
            if (g == null || settings.PadStartDoes == 0) return;
            GameMoreRows.Add(new PickRow
            {
                Key = "playnitegamemenu", Icon = "menu",
                Text = Loc("LOC_KX_PlayniteGameMenu", "Playnite's options for this game"),
                Sub = Loc("LOC_KX_PlayniteGameMenuSub", "Playnite's own game menu (Menu does something else now)"),
                Command = new RelayCommand(_ => CloseThenRun("OpenGameMenuCommand"))
            });
        }
    }
}

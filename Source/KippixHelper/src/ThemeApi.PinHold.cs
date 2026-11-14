using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Hold A on a game pinned to the home screen (the tiles before the games, ThemeApi.Pins.cs): a small choice, Unpin or Cancel
    /// (B cancels). A tap still opens the game. Unpinning is the same as its More menu's "Unpin from the home screen".
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>The pinned-game tile with the highlight (or the one given), else null.</summary>
        private HomeTile HeldPinTile() { return PinTileOf(Keyboard.FocusedElement); }

        private HomeTile PinTileOf(IInputElement el)
        {
            if (el == null) return null;
            HomeTile tile = null;
            if (carActive && carProxy != null && ReferenceEquals(el, carProxy))
            {
                // Row's carousel: its unseen button has the highlight, the tile is the carousel's item under it
                if (carIndex >= 0 && carIndex < carExtras.Count) tile = carExtras[carIndex].Source as HomeTile;
            }
            else
            {
                var fe = el as FrameworkElement;
                tile = fe != null ? fe.DataContext as HomeTile : null;
            }
            return tile != null && tile.IsPinned && tile.GameId != Guid.Empty ? tile : null;
        }

        /// <summary>Held on a pinned game: Unpin / Cancel (Unpin has the highlight). False: not a pinned game.</summary>
        private bool PinHoldMenu(IInputElement held)
        {
            var tile = PinTileOf(held) ?? HeldPinTile();
            if (tile == null) return false;
            Game g = null;
            try { g = api.Database.Games.Get(tile.GameId); } catch (Exception) { }
            if (g == null || !IsPinnedGame(g.Id)) return false;
            var game = g;
            Confirm(g.Name, Loc("LOC_KX_PinHoldText", "Pinned to the home screen"), Loc("LOC_KX_PinHoldUnpin", "Unpin"), () =>
            {
                if (IsPinnedGame(game.Id)) TogglePin(game);   // (its tile goes: the highlight goes back to the games)
                ui.BeginInvoke(new Action(FocusSelectedCard), DispatcherPriority.ContextIdle);
            });
            // Unpin has the highlight (after Confirm puts it on Cancel)
            ui.BeginInvoke(new Action(() => { if (confirmOpen) UiTools.FocusByName("KX_ConfirmYes"); }), DispatcherPriority.ContextIdle);
            return true;
        }
    }
}

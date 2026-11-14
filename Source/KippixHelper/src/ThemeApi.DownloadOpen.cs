using System;
using System.Diagnostics;
using System.Linq;
using Playnite.SDK.Plugins;

namespace KippixHelper
{
    /// <summary>A download row opens the launcher it's downloading in: Steam's downloads, GOG Galaxy on that game, or the launcher itself.</summary>
    public partial class ThemeApi
    {
        private void OpenDownloadLauncher(DownloadInfo d)
        {
            if (d == null) return;
            PlaySound("select");
            ClosePanels();
            try
            {
                if (d.PluginId == global::KippixHelper.Downloads.SteamLibraryId || d.Source == "Steam") { Native.OpenUri("steam://open/downloads"); return; }
                if ((d.PluginId == global::KippixHelper.Downloads.GogLibraryId || d.Source == "GOG") && !string.IsNullOrEmpty(d.LauncherGameId))
                {
                    Native.OpenUri("goggalaxy://openGameView/" + d.LauncherGameId);
                    return;
                }
                // any other launcher: its library add-on knows how to open it
                var lib = d.PluginId != Guid.Empty ? api.Addons.Plugins.OfType<LibraryPlugin>().FirstOrDefault(p => p.Id == d.PluginId) : null;
                if (lib != null && lib.Client != null) { lib.Client.Open(); return; }
                if (d.PluginId == global::KippixHelper.Downloads.EpicLibraryId || d.Source == "Epic") { Native.OpenUri("com.epicgames.launcher://store"); return; }
                logger.Info("Kippix: no launcher to open for " + d.Name);
            }
            catch (Exception ex) { logger.Warn("Kippix: open launcher: " + ex.Message); }
        }
    }
}

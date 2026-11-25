using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.7) The pages' Console design (the user's mock-ups; Settings › Layout changes WIP, 2 of 2 per screen): Library health,
    /// Storage, Split screen, Pick for me, Controller, Game news, the profile's tabs and Community. One more template beside the
    /// pages' own (KX_Pages2 in Main.xaml), showing the same lists (so every page's commands, keys and focus work as they are);
    /// its lists keep the default names (UiTools.FindByName finds the one showing).
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>Which Layout changes WIP screen the open page belongs to (null: none).</summary>
        private string PageArea
        {
            get
            {
                if (openPanel != "page" || string.IsNullOrEmpty(pageKey)) return null;
                if (IsProfileHub) return "profile";
                if (pageKey.StartsWith("community", StringComparison.Ordinal)) return "community";
                switch (pageKey)
                {
                    case "health": case "storage": case "splitscreen": case "pick": case "controller": return pageKey;
                    case "news": case "gamenews": return "news";
                    case "wishlist": case "friends": case "collections": case "year": case "upnext": case "mylibrary": case "profilepage": return "profile";
                }
                return null;
            }
        }

        public bool PagesConsole { get { var a = PageArea; return a != null && LabLook(a) == 1; } }
        public bool PagesDefault { get { return !PagesConsole; } }
        /// <summary>The Console page's kind (for its small differences): profile, community, health, storage, splitscreen, pick, controller, news.</summary>
        public string Page2Kind { get { return PagesConsole ? PageArea : ""; } }
        public bool Page2Hub { get { return PagesConsole && IsProfileHub; } }
        /// <summary>Pick for me shows its game big in the middle; every other page as a wide card.</summary>
        public bool Page2PickHero { get { return PagesConsole && pageKey == "pick" && HasPageHero; } }
        public bool Page2WideHero { get { return PagesConsole && pageKey != "pick" && HasPageHero; } }
        public bool Page2NotHub { get { return PagesConsole && !IsProfileHub; } }

        // ---- the controller test, Console: the last button pressed and a battery bar
        private string ctrlLast = "";
        private double ctrlBattFrac = -1;
        public string CtrlLastPressed { get { return ctrlLast; } }
        public bool HasCtrlLastPressed { get { return ctrlLast.Length > 0; } }
        public double CtrlBatteryBar { get { return ctrlBattFrac < 0 ? 0 : Math.Max(8, 460 * ctrlBattFrac); } }
        public bool HasCtrlBatteryBar { get { return ctrlBattFrac >= 0; } }

        private void CtrlPressedNow(string name)
        {
            if (name == ctrlLast) return;
            ctrlLast = name ?? "";
            Notify("CtrlLastPressed");
            Notify("HasCtrlLastPressed");
        }

        private void CtrlBatteryLevel(string raw)
        {
            switch (raw)
            {
                case "Full": case "Wired": ctrlBattFrac = 1; break;
                case "Medium": ctrlBattFrac = 0.5; break;
                case "Low": ctrlBattFrac = 0.2; break;
                case "Empty": ctrlBattFrac = 0.05; break;
                default: ctrlBattFrac = -1; break;
            }
            Notify("CtrlBatteryBar");
            Notify("HasCtrlBatteryBar");
        }

        /// <summary>The other screens' Console switches (store, media, game page, home) after a change in Layout changes WIP.</summary>
        private void NotifyConsoleAreas()
        {
            foreach (string n in new[] { "StoreConsole", "StoreDefault", "MediaConsole", "AltGameConsole", "AltGameDefault" }) Notify(n);
            NotifyMediaConsole();
        }

        // ---- the other screens' Console switches (they only show while their screen does)
        public bool StoreConsole { get { return LabLook("store") == 1; } }
        public bool StoreDefault { get { return !StoreConsole; } }
        public bool MediaConsole { get { return LabLook("media") == 1; } }
        /// <summary>Media (the streaming services, profile menu › Media) as the Console screen: big cards over everything, the menu hidden.</summary>
        public bool MediaConsoleShown { get { return MediaConsole && openPanel == "profile" && IsMediaMenuPage(profilePage) && !IsProfileReorder; } }
        // (1.1.0) Media settings and a tile's page: the menu, over the tiles
        public bool ProfilePanelShown { get { return IsProfileOpen && (!MediaConsoleShown || profilePage != "media"); } }
        private void NotifyMediaConsole() { Notify("MediaConsoleShown"); Notify("ProfilePanelShown"); Notify("MediaConsoleActive"); }
        public bool AltGameConsole { get { return LabLook("altgame") == 1; } }
        public bool AltGameDefault { get { return !AltGameConsole; } }

        // ---- Pick for me, Console: the game's background big in the middle, its logo (Extra Metadata Loader's) under it
        public ImageSource PageHeroWide { get; private set; }
        public ImageSource PageHeroLogo { get; private set; }
        public bool HasPageHeroLogo { get { return PageHeroLogo != null; } }
        public bool HasNoPageHeroLogo { get { return PageHeroLogo == null; } }

        private void PickHeroArt(Game g)
        {
            try
            {
                string bg = string.IsNullOrEmpty(g.BackgroundImage) || g.BackgroundImage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : api.Database.GetFullFilePath(g.BackgroundImage);
                PageHeroWide = bg != null ? Images.Load(bg, 960) : null;
                if (PageHeroWide == null) PageHeroWide = Images.Load(CoverOf(g.Id), 480);
                string logo = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", g.Id.ToString(), "Logo.png");
                PageHeroLogo = File.Exists(logo) ? Images.Load(logo, 640) : null;
            }
            catch (Exception) { PageHeroWide = null; PageHeroLogo = null; }
            foreach (string n in new[] { "PageHeroWide", "PageHeroLogo", "HasPageHeroLogo", "HasNoPageHeroLogo" }) Notify(n);
        }

        private void NotifyPagesConsole()
        {
            foreach (string n in new[] { "PagesConsole", "PagesDefault", "Page2Kind", "Page2Hub", "Page2NotHub", "Page2PickHero", "Page2WideHero" }) Notify(n);
        }
    }
}

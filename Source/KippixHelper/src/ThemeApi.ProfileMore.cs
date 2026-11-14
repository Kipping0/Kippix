using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// The profile menu's extras: your own name and picture (a file, or one of your screenshots), what the profile button shows,
    /// Settings &amp; Customisation in the menu (so the ⋯ on the home screen can go), and Power (desktop mode, exit, sleep, restart, shut down).
    /// Also the shortcut bar standing down the side.
    /// </summary>
    public partial class ThemeApi
    {
        private void AddProfileExtraRows()
        {
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_MorePages", "More pages"), Sub = Loc("LOC_KX_MorePagesSub3", "Pick for me, split screen, storage, game news, quick settings…"), Icon = "star", Key = "pages", Command = new RelayCommand(_ => SetProfilePage("pages")) });
            ProfileRows.Add(CommunityProfileRow());   // Community: layouts, look packs, widgets (Settings › Community, ThemeApi.CommunityHub.cs)
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_ProfileSettings", "Your name and picture"), Sub = Loc("LOC_KX_ProfileSettingsSub", "Rename yourself, pick a picture, what the button shows"), Icon = "profile", Key = "me", Command = new RelayCommand(_ => SetProfilePage("me")) });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_Power", "Power"), Sub = Loc("LOC_KX_PowerSub", "Desktop mode, exit Playnite, sleep, restart, shut down"), Icon = "power", Key = "power", Command = new RelayCommand(_ => SetProfilePage("power")) });
            // at the bottom, under Power (as its switch on Your name and picture says)
            if (settings.SettingsInProfileMenu)
                ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_Options", "Settings & Customisation"), Sub = Loc("LOC_KX_OptionsMenuSub", "Every setting, the same as the ⋯ on the home screen"), Icon = "gear", Key = "settings", Command = new RelayCommand(_ => OpenSettingsFromProfile()) });
        }

        /// <summary>The menu's Settings &amp; Customisation: the same menu as the ⋯, on the right.</summary>
        private void OpenSettingsFromProfile()
        {
            PlaySound("select");
            ClosePanels();
            ui.BeginInvoke(new Action(() => TogglePanel("options")), DispatcherPriority.Loaded);
        }

        // =====================================================================
        // Your name and picture
        // =====================================================================

        public bool ProfileShowName { get { return settings.ProfileShow != 2; } }
        public bool ProfileShowPicture { get { return settings.ProfileShow != 1; } }
        public bool TopOptionsShown { get { return !settings.HideTopOptions; } }

        private string ProfileShowText
        {
            get { return settings.ProfileShow == 1 ? Loc("LOC_KX_ShowNameOnly", "Name only") : settings.ProfileShow == 2 ? Loc("LOC_KX_ShowPictureOnly", "Picture only") : Loc("LOC_KX_ShowNameAndPicture", "Name and picture"); }
        }

        private void BuildMeRows()
        {
            bool ownName = !string.IsNullOrWhiteSpace(settings.ProfileNameOverride);
            bool ownPic = !string.IsNullOrEmpty(settings.ProfilePicturePath);
            ProfileRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecName", "NAME") });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_YourName", "Name"), Sub = ProfileName + (ownName ? "" : "  ·  " + Loc("LOC_KX_FromWindows", "from Windows")), Icon = "profile", Key = "name", Command = new RelayCommand(_ => RenameProfile()) });
            // (r15: no "Use your Windows name again": a name is required, so it's only ever changed by typing a new one)
            ProfileRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecPicture", "PICTURE") });
            int avatars = AvatarFiles().Count;
            if (avatars > 0) ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_PicAvatar", "Choose an avatar"), Sub = string.Format(Loc("LOC_KX_PicAvatarSub", "{0} ready-made pictures"), avatars), Icon = "profile", Key = "picavatar", Command = new RelayCommand(_ => PickProfileAvatar()) });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_PicFromFile", "Choose a picture file"), Sub = Loc("LOC_KX_PicFromFileSub2", "PNG, JPG, WebP or AVIF from your PC"), Icon = "file", Key = "picfile", Command = new RelayCommand(_ => PickProfilePictureFile()) });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_PicFromGallery", "Choose one of your screenshots"), Sub = Loc("LOC_KX_PicFromGallerySub", "Opens the Gallery: choose the one you want"), Icon = "gallery", Key = "picgallery", Command = new RelayCommand(_ => PickProfilePictureFromGallery()) });
            if (ownPic) ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_ResetPicture", "Reset picture"), Sub = string.Format(Loc("LOC_KX_ResetPictureSub", "Just the first letter of your name: {0}"), ProfileInitial), Icon = "refresh", Key = "resetpic", Command = new RelayCommand(_ => { PlaySound("select"); SetProfilePicture(null); RebuildMe("picfile"); }) });
            ProfileRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecProfileButton", "THE PROFILE BUTTON") });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_ProfileShows", "Shows"), Sub = ProfileShowText, Icon = "layout", Key = "show", Command = new RelayCommand(_ => { settings.ProfileShow = (settings.ProfileShow + 1) % 3; SaveSettings(); PlaySound("move"); NotifyProfile(); RebuildMe("show"); }) });
            ProfileRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecThisMenu", "THIS MENU") });
            ProfileRows.Add(new PickRow
            {
                Text = Loc("LOC_KX_SettingsInMenu", "Settings & Customisation in this menu"), Sub = Loc("LOC_KX_SettingsInMenuSub", "At the bottom of the list, under Power"),
                IsSelected = settings.SettingsInProfileMenu, IsSwitch = true, Icon = "gear", Key = "inmenu",
                Command = new RelayCommand(_ =>
                {
                    settings.SettingsInProfileMenu = !settings.SettingsInProfileMenu;
                    if (!settings.SettingsInProfileMenu) settings.HideTopOptions = false;   // the ⋯ comes back: there's always a way in
                    SaveSettings(); PlaySound("move"); NotifyProfile(); RebuildMe("inmenu");
                })
            });
            ProfileRows.Add(new PickRow
            {
                Text = Loc("LOC_KX_HideDots", "Hide the ⋯ on the home screen"), Sub = Loc("LOC_KX_HideDotsSub", "Settings & Customisation is then here, in this menu"),
                IsSelected = settings.HideTopOptions, IsSwitch = true, Icon = "hide", Key = "hidedots",
                Command = new RelayCommand(_ =>
                {
                    settings.HideTopOptions = !settings.HideTopOptions;
                    if (settings.HideTopOptions) settings.SettingsInProfileMenu = true;
                    SaveSettings(); PlaySound("move"); NotifyProfile(); RebuildMe("hidedots");
                })
            });
        }

        private void RebuildMe(string focusKey)
        {
            if (openPanel != "profile" || profilePage != "me") return;
            ProfileRows.Clear();
            BuildMeRows();
            ui.BeginInvoke(new Action(() => { var r = ProfileRows.FirstOrDefault(x => x.Key == focusKey); if (r != null) UiTools.FocusItem("KX_ProfileList", r); }), DispatcherPriority.ContextIdle);
        }

        private void NotifyProfile()
        {
            // the profile playing now follows its name and picture (Profiles)
            var cp = ProfilesOn && !GuestActive ? CurrentProfile : null;
            if (cp != null)
            {
                // (back to the Windows name: the profile takes that, so switching back to it doesn't bring the old name back)
                cp.Name = !string.IsNullOrWhiteSpace(settings.ProfileNameOverride) ? settings.ProfileNameOverride.Trim() : ProfileName;
                cp.PicturePath = settings.ProfilePicturePath;
            }
            foreach (string n in new[] { "ProfileName", "ProfileInitial", "ProfileShowName", "ProfileShowPicture", "TopOptionsShown", "ProfileMenuTitle", "ProfilePicture", "HasProfilePicture" }) Notify(n);
            // the ⋯ is a top bar item: its tick follows the profile menu's switches
            var dots = Shortcuts != null ? Shortcuts.FirstOrDefault(x => x.IsOptions) : null;
            if (dots != null && dots.IsPinned == settings.HideTopOptions) { dots.SetPinnedSilently(!settings.HideTopOptions); UpdateShortcutStatus(); }
        }

        private void RenameProfile()
        {
            PlaySound("select");
            string now = ProfileName;
            OpenKeyboard(Loc("LOC_KX_RenameAsk2", "Your name"), now, typed =>
            {
                string name = (typed ?? "").Trim();
                if (name.Length > 40) name = name.Substring(0, 40);
                if (name.Length == 0) { PlaySound("back"); RebuildMe("name"); return; }   // a name is required: empty keeps the one you have
                settings.ProfileNameOverride = name == profileName ? "" : name;
                SaveSettings();
                NotifyProfile();
                RebuildMe("name");
            }, null, () => RebuildMe("name"), 40);
        }

        private string ProfilePictureDir { get { return Path.Combine(plugin.GetPluginUserDataPath(), "profile"); } }

        /// <summary>Copies the picture into the add-on's own folder (so it stays even if the original moves), or goes back to Windows'.</summary>
        private void SetProfilePicture(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) settings.ProfilePicturePath = "";
                else
                {
                    Directory.CreateDirectory(ProfilePictureDir);
                    string dest = CopyPicture(file, Path.Combine(ProfilePictureDir, "picture-" + DateTime.Now.Ticks));
                    if (dest == null) return;   // (couldn't be read: the picture you had stays)
                    foreach (string old in Directory.GetFiles(ProfilePictureDir, "picture*"))
                    {
                        // another profile's picture (Profiles: each person's lives here too) stays, and the new one
                        string o = old;
                        if (string.Equals(o, dest, StringComparison.OrdinalIgnoreCase)) continue;
                        if (settings.Profiles != null && settings.Profiles.Any(x => x != null && x.Id != settings.CurrentProfile && string.Equals(x.PicturePath, o, StringComparison.OrdinalIgnoreCase))) continue;
                        try { File.Delete(old); } catch (Exception) { }
                    }
                    settings.ProfilePicturePath = dest;
                }
                // (0.97) the profile it belongs to keeps it too (it's what's put back when Playnite starts)
                if (settings.Profiles != null)
                    foreach (var p in settings.Profiles) if (p != null && p.Id == settings.CurrentProfile) p.PicturePath = settings.ProfilePicturePath;
                SaveSettings();
                profilePicture = null;
                profilePictureTried = false;
                NotifyProfile();
                var unused = ProfilePicture;   // loads it again
            }
            catch (Exception ex) { logger.Warn("Kippix: profile picture: " + ex.Message); }
        }

        // ---------------- (0.99.6) avatars: your own ready-made profile pictures ----------------
        // The pictures in Kippix's Avatars folder (Kippix's data folder › Avatars; your own files, put there by you, never part of
        // the theme) are offered first wherever a profile picture is chosen: Getting started, Your name and picture, Profiles.

        private string AvatarsDir { get { return Path.Combine(plugin.GetPluginUserDataPath(), "Avatars"); } }

        private List<string> AvatarFiles()
        {
            try
            {
                if (!Directory.Exists(AvatarsDir)) return new List<string>();
                return Directory.GetFiles(AvatarsDir).Where(f => ProfilePicExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        private static string AvatarName(string file) { return Path.GetFileNameWithoutExtension(file).Replace('_', ' ').Replace('-', ' '); }

        private void PickProfileAvatar()
        {
            PlaySound("select");
            ClosePanels();
            TogglePanel("options");
            OpenPicker(Loc("LOC_KX_PicAvatarTitle", "Choose an avatar"), false, ProfilePicExts, AvatarsDir, "", file =>
            {
                SetProfilePicture(file);
                ClosePanels();
                ui.BeginInvoke(new Action(() => { ToggleProfile(); SetProfilePage("me"); }), DispatcherPriority.Loaded);
            }, () =>
            {
                ClosePanels();
                ui.BeginInvoke(new Action(() => { ToggleProfile(); SetProfilePage("me"); }), DispatcherPriority.Loaded);
            });
        }

        /// <summary>(0.99.5) Pictures a profile can take: WebP and AVIF too (what sites like the avatar galleries save as).</summary>
        private static readonly string[] ProfilePicExts = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".avif", ".heic" };

        /// <summary>
        /// A picture copied for a profile (<paramref name="destNoExt"/> + its extension). WebP / AVIF / HEIC are turned into a PNG as it's
        /// copied (read with Windows' own picture codecs: the WebP Image Extensions / AV1 Video Extension / HEIF Image Extensions from the
        /// Microsoft Store; Windows 11 usually has them), so it shows everywhere afterwards. Null (with a notice) when it can't be read.
        /// </summary>
        private string CopyPicture(string file, string destNoExt)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".webp" && ext != ".avif" && ext != ".heic")
            {
                string d = destNoExt + ext;
                File.Copy(file, d, true);
                return d;
            }
            try
            {
                System.Windows.Media.Imaging.BitmapSource frame;
                using (var fs = File.OpenRead(file))
                {
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    frame = dec.Frames[0];
                }
                // (a big avatar is kept at most 1024 px across)
                double scale = Math.Min(1.0, 1024.0 / Math.Max(1, Math.Max(frame.PixelWidth, frame.PixelHeight)));
                if (scale < 1) frame = new System.Windows.Media.Imaging.TransformedBitmap(frame, new ScaleTransform(scale, scale));
                string d = destNoExt + ".png";
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                using (var outFs = File.Create(d)) enc.Save(outFs);
                return d;
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: profile picture " + Path.GetFileName(file) + " couldn't be read: " + ex.Message);
                string need = ext == ".avif" ? "AV1 Video Extension" : ext == ".heic" ? "HEIF Image Extensions" : "WebP Image Extensions";
                try { ShowNotice(Loc("LOC_KX_PicCantRead", "CAN'T READ THAT PICTURE"), Path.GetFileName(file), string.Format(Loc("LOC_KX_PicNeedCodec", "Windows needs the free \"{0}\" from the Microsoft Store to open it (or save the picture as PNG)"), need), "", null); } catch (Exception) { }
                PlaySound("back");
                return null;
            }
        }

        private void PickProfilePictureFile()
        {
            PlaySound("select");
            ClosePanels();
            TogglePanel("options");
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            OpenPicker(Loc("LOC_KX_PicFromFile", "Choose a picture file"), false, ProfilePicExts, pics, "", file =>
            {
                SetProfilePicture(file);
                ClosePanels();
                ui.BeginInvoke(new Action(() => { ToggleProfile(); SetProfilePage("me"); }), DispatcherPriority.Loaded);
            }, () =>
            {
                ClosePanels();
                ui.BeginInvoke(new Action(() => { ToggleProfile(); SetProfilePage("me"); }), DispatcherPriority.Loaded);
            });
        }

        // the Gallery opened to pick your picture: A on a photo takes it
        private bool galleryPickProfile;
        private int galleryShowBeforePick;   // your own Show choice, put back once the picture's picked
        public bool IsGalleryPickingProfile { get { return galleryPickProfile; } }

        private void PickProfilePictureFromGallery()
        {
            ClosePanels();
            if (!galleryPickProfile) galleryShowBeforePick = settings.GalleryShow;
            galleryPickProfile = true;
            Notify("IsGalleryPickingProfile");
            galleryView = "all";
            settings.GalleryShow = 1;   // photos only while picking
            OpenGallery();
        }

        /// <summary>A on a photo while picking a profile picture.</summary>
        private bool GalleryPickedProfile(GalleryItem item)
        {
            if (!galleryPickProfile || item == null || item.File == null) return false;
            if (item.File.IsVideo) { PlaySound("back"); return true; }
            galleryPickProfile = false;
            Notify("IsGalleryPickingProfile");
            settings.GalleryShow = galleryShowBeforePick;
            SetProfilePicture(item.File.Path);
            ClosePanels();
            ui.BeginInvoke(new Action(() => { ToggleProfile(); SetProfilePage("me"); }), DispatcherPriority.Loaded);
            return true;
        }

        private void StopGalleryPick()
        {
            if (!galleryPickProfile) return;
            galleryPickProfile = false;
            settings.GalleryShow = galleryShowBeforePick;
            Notify("IsGalleryPickingProfile");
        }

        // =====================================================================
        // Power
        // =====================================================================

        private void BuildPowerRows()
        {
            ProfileRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecPlaynite", "PLAYNITE") });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_DesktopMode", "Switch to desktop mode"), Icon = "desktop", Key = "desktop", Command = new RelayCommand(_ => PowerAction("desktop")) });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_ExitPlaynite", "Exit Playnite"), Icon = "leave", Key = "exit", Command = new RelayCommand(_ => PowerAction("exit")) });
            ProfileRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecThisPc", "THIS PC") });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_Sleep", "Sleep"), Icon = "moon", Key = "sleep", Command = new RelayCommand(_ => PowerAction("sleep")) });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_Restart", "Restart"), Icon = "refresh", Key = "restart", Command = new RelayCommand(_ => PowerAction("restart")) });
            ProfileRows.Add(new PickRow { Text = Loc("LOC_KX_ShutDown", "Shut down"), Icon = "power", Key = "shutdown", Command = new RelayCommand(_ => PowerAction("shutdown")) });
        }

        /// <summary>Each Power choice asks first (the box has Cancel highlighted).</summary>
        private void PowerAction(string what)
        {
            string title, text, yes;
            switch (what)
            {
                case "desktop": title = Loc("LOC_KX_DesktopAsk", "Switch to desktop mode?"); text = Loc("LOC_KX_DesktopAskText", "Playnite leaves full screen and opens its desktop window."); yes = Loc("LOC_KX_DesktopMode", "Switch to desktop mode"); break;
                case "exit": title = Loc("LOC_KX_ExitAsk", "Exit Playnite?"); text = Loc("LOC_KX_ExitAskText", "Playnite closes. Games that are running keep running."); yes = Loc("LOC_KX_ExitPlaynite", "Exit Playnite"); break;
                case "sleep": title = Loc("LOC_KX_SleepAsk2", "Put the PC to sleep?"); text = Loc("LOC_KX_SleepAskText", "Press a button on the controller or the PC to wake it."); yes = Loc("LOC_KX_Sleep", "Sleep"); break;
                case "restart": title = Loc("LOC_KX_RestartAsk2", "Restart the PC?"); text = Loc("LOC_KX_PowerAskText", "Anything that isn't saved will be lost."); yes = Loc("LOC_KX_Restart", "Restart"); break;
                default: title = Loc("LOC_KX_ShutDownAsk", "Shut down the PC?"); text = Loc("LOC_KX_PowerAskText", "Anything that isn't saved will be lost."); yes = Loc("LOC_KX_ShutDown", "Shut down"); break;
            }
            Confirm(title, text, yes, () => WithOutro(what, () => DoPower(what)));   // (Look and feel › Intro and outro: the outro first)
        }

        private void DoPower(string what)
        {
            if (backgroundDirty) { backgroundDirty = false; }
            SaveSettings();
            if (what == "desktop") { ClosePanels(); RunMainCommand("SwitchToDesktopCommand"); return; }
            if (what == "exit") { AskExitPlaynite(); return; }
            ClosePanels();
            KippixShared.SafeWork.Queue(_ =>
            {
                Thread.Sleep(500);
                if (what == "sleep") Native.Sleep();
                else if (what == "restart") Native.Restart();
                else Native.PowerOff();
            });
        }

        // =====================================================================
        // The shortcut bar: across the top, or down the right side
        // =====================================================================

        public bool ShortcutsVertical
        {
            get { return settings.ShortcutsVertical; }
            set
            {
                if (settings.ShortcutsVertical == value) return;
                settings.ShortcutsVertical = value;
                // the clock goes to the top of the side bar, and back by the notifications when the bar runs across again
                var clock = Shortcuts != null ? Shortcuts.FirstOrDefault(x => x.IsClock) : null;
                if (clock != null)
                {
                    int from = Shortcuts.IndexOf(clock), to = value ? 0 : Shortcuts.Count - 1;
                    if (from != to) Shortcuts.Move(from, to);
                }
                SaveSettings();
                NotifyTopBarShape();
            }
        }
        public Orientation ShortcutsOrientation { get { return settings.ShortcutsVertical ? Orientation.Vertical : Orientation.Horizontal; } }
        public int TopBarRowSpan { get { return settings.ShortcutsVertical ? 3 : 1; } }
        public VerticalAlignment TopRightAlignment { get { return settings.ShortcutsVertical ? VerticalAlignment.Top : VerticalAlignment.Center; } }
        /// <summary>The profile button keeps its place at the top whichever way the shortcut bar stands.</summary>
        public VerticalAlignment TopLeftAlignment { get { return settings.ShortcutsVertical ? VerticalAlignment.Top : VerticalAlignment.Center; } }
        public Thickness TopRightItemMargin { get { return settings.ShortcutsVertical ? new Thickness(0, 6, 0, 6) : new Thickness(0); } }

        private void NotifyTopBarShape()
        {
            foreach (string n in new[] { "ShortcutsVertical", "ShortcutsOrientation", "TopBarRowSpan", "TopRightAlignment", "TopRightItemMargin", "TopLeftAlignment" }) Notify(n);
            ApplyHomeLayoutSoon();
        }

        private static void CollectFocusable(DependencyObject root, List<UIElement> into)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var ui = c as UIElement;
                if (ui != null && !ui.IsVisible) continue;
                if (ui != null && ui.Focusable && ui.IsEnabled && c is Control) { into.Add(ui); continue; }
                CollectFocusable(c, into);
            }
        }

        /// <summary>The shortcut bar down the side: Up / Down move along it, Down from the last goes to the games, Left goes to the profile button.</summary>
        private bool VerticalBarKey(KeyEventArgs e)
        {
            if (!settings.ShortcutsVertical) return false;
            var focused = Keyboard.FocusedElement as UIElement;
            var right = UiTools.FindByName(Application.Current.MainWindow, "KX_TopRight");
            if (focused == null || right == null || !right.IsAncestorOf(focused)) return false;
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // every button on the bar, top to bottom, one step at a time
                var all = new List<UIElement>();
                CollectFocusable(right, all);
                int at = all.FindIndex(x => ReferenceEquals(x, focused) || x.IsAncestorOf(focused));
                int to = at + (e.Key == Key.Down ? 1 : -1);
                if (at >= 0 && to >= 0 && to < all.Count) { Keyboard.Focus(all[to]); e.Handled = true; return true; }
                e.Handled = true;
                if (e.Key == Key.Down) FocusSelectedCard();   // the bottom: on to the games
                return true;                                  // the top: stay
            }
            if (e.Key == Key.Right) { e.Handled = true; return true; }       // the screen's edge
            if (e.Key == Key.Left)
            {
                e.Handled = true;
                if (!UiTools.FocusByNameIfVisible("KX_ProfileButton")) FocusSelectedCard();
                return true;
            }
            return false;
        }
    }
}

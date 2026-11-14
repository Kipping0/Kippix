using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (0.89) Getting started › Who plays: a profile's settings in a big box over the tour (not the settings menu): its name,
    /// picture, Kids, the PIN, age ratings, what it can open, and Delete. Opens straight after a new profile is named, or
    /// with A on a profile. B or Done closes it back onto that profile's row. Its Steam account and daily time limit are in
    /// Settings › Profiles.
    /// </summary>
    public partial class ThemeApi
    {
        private string tourProfileId;   // the profile in the box (null: closed); kept while a picture is picked
        private ObservableCollection<PickRow> tourProfileRows;
        public ObservableCollection<PickRow> TourProfileRows { get { return tourProfileRows ?? (tourProfileRows = new ObservableCollection<PickRow>()); } }
        public bool IsTourProfileOpen { get { return (tourProfileId != null || tourPicking) && openPanel == "welcome"; } }
        public string TourProfileTitle { get; private set; }
        // (0.99.7) the picture picker: the pictures as big round tiles, no names (folders and places stay rows under them)
        private ObservableCollection<PickRow> tourPicTiles;
        public ObservableCollection<PickRow> TourPicTiles { get { return tourPicTiles ?? (tourPicTiles = new ObservableCollection<PickRow>()); } }
        public bool HasTourPicTiles { get { return tourPicking && TourPicTiles.Count > 0; } }

        public string TourBoxKicker { get { return tourPicking ? Loc("LOC_KX_TourPickKicker", "CHOOSE A PICTURE") : Loc("LOC_KX_TourProfileBoxKicker", "PROFILE SETTINGS"); } }

        private PlayerProfile TourProfile { get { return tourProfileId == null || settings.Profiles == null ? null : settings.Profiles.FirstOrDefault(p => p.Id == tourProfileId); } }

        private void OpenTourProfileBox(string id)
        {
            tourProfileId = id;
            BuildTourProfileRows(null);
            Notify("IsTourProfileOpen");
        }

        private void CloseTourProfileBox()
        {
            string id = tourProfileId;
            tourProfileId = null;
            Notify("IsTourProfileOpen");
            if (openPanel == "welcome" && WelcomeSteps[welcomeStep] == "profiles") BuildWelcomeStep(id != null && settings.Profiles.Any(p => p.Id == id) ? "p:" + id : "addperson");
        }

        /// <summary>B in the tour: the box closes first (choosing a picture: back to where it was opened from).</summary>
        private bool TourProfileBack()
        {
            if (!IsTourProfileOpen) return false;
            PlaySound("back");
            if (tourPicking) { EndTourPicker(null); return true; }
            CloseTourProfileBox();
            return true;
        }

        private void BuildTourProfileRows(string focusKey)
        {
            var rows = TourProfileRows;
            rows.Clear();
            if (TourPicTiles.Count > 0) { TourPicTiles.Clear(); Notify("HasTourPicTiles"); }
            var p = TourProfile;
            if (p == null) { tourProfileId = null; Notify("IsTourProfileOpen"); return; }
            TourProfileTitle = p.Name;
            Notify("TourProfileTitle");
            Action<string> again = k => BuildTourProfileRows(k);
            Action changed = () => { SaveSettings(); PlaySound("move"); if (p.Id == settings.CurrentProfile) { profileNow = p; ApplyListFilter(true); } };

            rows.Add(new PickRow
            {
                Key = "name", Icon = "note", Text = Loc("LOC_KX_YourName", "Name"), Sub = p.Name,
                Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_ProfileNameAsk", "Their name"), p.Name, typed =>
                {
                    if (!string.IsNullOrWhiteSpace(typed)) { p.Name = typed.Trim().Length > 30 ? typed.Trim().Substring(0, 30) : typed.Trim(); SaveSettings(); }
                    again("name");
                }, null, () => again("name"), 30))
            });
            rows.Add(new PickRow
            {
                Key = "picture", Icon = "gallery", Text = Loc("LOC_KX_Picture", "Picture"),
                Sub = string.IsNullOrEmpty(p.PicturePath) ? Loc("LOC_KX_PictureNone", "None: their initial") : Path.GetFileName(p.PicturePath),
                Command = new RelayCommand(_ =>
                {
                    PlaySound("select");
                    // (0.90) chosen right here in the box, not in the settings
                    StartTourPicker(file =>
                    {
                        try
                        {
                            Directory.CreateDirectory(ProfilesDir);
                            string dest = CopyPicture(file, Path.Combine(ProfilesDir, p.Id + "-" + DateTime.Now.Ticks));
                            if (dest == null) return;
                            p.PicturePath = dest;
                            if (p.Id == settings.CurrentProfile) { settings.ProfilePicturePath = dest; profilePicture = null; profilePictureTried = false; NotifyProfile(); }
                            SaveSettings();
                        }
                        catch (Exception ex) { logger.Warn("Kippix: profile picture: " + ex.Message); }
                    }, () => BuildTourProfileRows("picture"));
                })
            });
            rows.Add(BoardSwitch("star", "kids", Loc("LOC_KX_KidsProfile", "Kids profile"), p.Kind == "kids", Loc("LOC_KX_KidsProfileOn", "Games above an age rating hidden, no Store, settings behind the PIN"), Loc("LOC_KX_Off", "Off"),
                                 () => { SetProfileKids(p, p.Kind != "kids"); changed(); again("kids"); }));
            rows.Add(BoardSwitch("lock", "needspin", Loc("LOC_KX_NeedsPin", "Needs the PIN to choose"), p.NeedsPin,
                                 string.IsNullOrEmpty(settings.ProfilesPin) ? Loc("LOC_KX_SetPinBelow", "On (set the PIN just below)") : Loc("LOC_KX_On", "On"), Loc("LOC_KX_Off", "Off"),
                                 () => { p.NeedsPin = !p.NeedsPin; changed(); again("needspin"); }));
            if (p.NeedsPin || p.Kind == "kids")
                rows.Add(new PickRow
                {
                    Key = "setpin", Icon = "lock", Text = string.IsNullOrEmpty(settings.ProfilesPin) ? Loc("LOC_KX_SetPin", "Set the grown-ups' PIN") : Loc("LOC_KX_ChangePin", "Change the grown-ups' PIN"),
                    Sub = Loc("LOC_KX_SetPinHereSub", "The one PIN for every locked profile, the Store and settings"),
                    Command = new RelayCommand(_ => SetGrownUpsPin(() => again("setpin")))
                });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_AgeHeader", "AGE RATINGS") });
            rows.Add(new PickRow
            {
                Key = "age", Icon = "flag", Text = p.MaxAge > 0 ? string.Format(Loc("LOC_KX_MaxAge", "Games rated up to age {0}"), p.MaxAge) : Loc("LOC_KX_MaxAgeNone", "Any age rating"),
                Sub = Loc("LOC_KX_MaxAgeSub", "Any, 3, 7, 12, 16, 18. From the games' PEGI / ESRB ratings in Playnite"),
                Command = new RelayCommand(_ => { int[] steps = { 0, 3, 7, 12, 16, 18 }; int i = Array.IndexOf(steps, p.MaxAge); p.MaxAge = steps[(i + 1) % steps.Length]; changed(); again("age"); })
            });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_WhatTheyCanDo", "WHAT THEY CAN OPEN") });
            rows.Add(new PickRow { Key = "store", Icon = "store", Text = Loc("LOC_KX_Store", "Store"), Sub = AccessText(p.StoreAccess),
                                   Command = new RelayCommand(_ => { p.StoreAccess = (p.StoreAccess + 1) % 3; changed(); again("store"); }) });
            rows.Add(new PickRow { Key = "settingsaccess", Icon = "gear", Text = Loc("LOC_KX_Options", "Settings & Customisation"), Sub = AccessText(p.SettingsAccess == 2 ? 1 : p.SettingsAccess),
                                   Command = new RelayCommand(_ => { p.SettingsAccess = p.SettingsAccess == 0 ? 1 : 0; changed(); again("settingsaccess"); }) });
            rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "more", Text = Loc("LOC_KX_TourProfileMore", "Their Steam account and a daily time limit: Settings › Profiles") });
            rows.Add(new PickRow { IsHeader = true, Text = "" });
            rows.Add(new PickRow
            {
                Key = "delete", Icon = "trash", Text = Loc("LOC_KX_PageDeleteProfile", "Delete this profile"),
                Command = new RelayCommand(_ => { if (!DeleteProfileKeepData(p)) { PlaySound("back"); return; } PlaySound("back"); CloseTourProfileBox(); })
            });
            rows.Add(new PickRow { Key = "done", Icon = "check", Text = Loc("LOC_KX_Done", "Done"), Command = new RelayCommand(_ => { PlaySound("select"); CloseTourProfileBox(); }) });

            string key = focusKey;
            ui.BeginInvoke(new Action(() =>
            {
                if (!IsTourProfileOpen || kbOpen) return;
                var row = key != null ? TourProfileRows.FirstOrDefault(r => r.Key == key) : TourProfileRows.FirstOrDefault(r => r.IsRow && !r.IsInfo);
                if (row != null) UiTools.FocusItem("KX_TourProfileList", row);
            }), DispatcherPriority.ContextIdle);
        }

        // =====================================================================
        // (0.90) Choosing a picture inside the tour (the same box): folders and pictures, with little previews
        // =====================================================================

        private bool tourPicking;
        private string tourPickFolder;          // null: the places list (Pictures, Desktop, Downloads, the drives)
        private Action<string> tourPickDone;
        private Action tourPickAfter;           // back to what opened it (picked or not)
        private int tourPickGen;
        private static readonly string[] TourPicExts = ProfilePicExts;

        /// <summary>Opens the picture list in the tour's box. <paramref name="done"/> gets the file; then <paramref name="after"/> runs
        /// (also when B leaves it without one).</summary>
        private void StartTourPicker(Action<string> done, Action after)
        {
            tourPicking = true;
            tourPickDone = done;
            tourPickAfter = after;
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            tourPickFolder = AvatarFiles().Count > 0 ? null : Directory.Exists(pics) ? pics : null;   // (0.99.6: the avatars first, when there are some)
            Notify("IsTourProfileOpen");
            Notify("TourBoxKicker");
            BuildTourPickRows(null);
        }

        private void EndTourPicker(string file)
        {
            var done = tourPickDone;
            var after = tourPickAfter;
            tourPicking = false;
            tourPickDone = null;
            tourPickAfter = null;
            tourPickGen++;
            TourPicTiles.Clear();
            Notify("HasTourPicTiles");
            Notify("TourBoxKicker");
            if (file != null && done != null) { PlaySound("select"); done(file); }
            Notify("IsTourProfileOpen");
            if (after != null) after();
        }

        private void BuildTourPickRows(string focusKey)
        {
            var rows = TourProfileRows;
            rows.Clear();
            var tiles = TourPicTiles;
            tiles.Clear();
            int gen = ++tourPickGen;
            string folder = tourPickFolder;
            TourProfileTitle = folder == null ? Loc("LOC_KX_TourPickTitle", "Choose a picture") : (Path.GetFileName(folder.TrimEnd('\\')).Length > 0 ? Path.GetFileName(folder.TrimEnd('\\')) : folder);
            Notify("TourProfileTitle");
            var thumbs = new List<KeyValuePair<PickRow, string>>();
            if (folder == null)
            {
                // (0.99.6) the avatars, then the Kippix mark, then the usual places and the drives
                foreach (string af in AvatarFiles())
                {
                    string file = af;
                    var r = new PickRow { Key = "pk:file:" + file, Text = AvatarName(file), Command = new RelayCommand(_ => EndTourPicker(file)) };
                    tiles.Add(r);
                    thumbs.Add(new KeyValuePair<PickRow, string>(r, file));
                }
                string mark = Path.Combine(ThemeDir, "Images", "kippix-mark.png");
                if (File.Exists(mark))
                {
                    var r = new PickRow { Key = "pk:file:" + mark, Text = Loc("LOC_KX_TourPickKippix", "The Kippix logo"), Command = new RelayCommand(_ => EndTourPicker(mark)) };
                    tiles.Add(r);
                    thumbs.Add(new KeyValuePair<PickRow, string>(r, mark));
                }
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_TourPickFromPc", "FROM YOUR PC") });
                foreach (var place in new[]
                {
                    new KeyValuePair<string, string>(Loc("LOC_KX_PlacePictures", "Pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                    new KeyValuePair<string, string>(Loc("LOC_KX_PlaceDesktop", "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                    new KeyValuePair<string, string>(Loc("LOC_KX_PlaceDownloads", "Downloads"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
                    new KeyValuePair<string, string>(Loc("LOC_KX_PlaceDocuments", "Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                })
                {
                    string path = place.Value;
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                    rows.Add(new PickRow { Key = "pk:dir:" + path, Icon = "folder", Text = place.Key, Command = new RelayCommand(_ => TourPickOpen(path)) });
                }
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_TourPickDrives", "DRIVES") });
                try
                {
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        if (!d.IsReady) continue;
                        string path = d.RootDirectory.FullName;
                        string label = "";
                        try { label = d.VolumeLabel; } catch (Exception) { }
                        rows.Add(new PickRow { Key = "pk:dir:" + path, Icon = "drive", Text = string.IsNullOrEmpty(label) ? path : label + " (" + path.TrimEnd('\\') + ")", Command = new RelayCommand(_ => TourPickOpen(path)) });
                    }
                }
                catch (Exception) { }
            }
            else
            {
                string parent = null;
                try { var pi = Directory.GetParent(folder); parent = pi != null ? pi.FullName : null; } catch (Exception) { }
                string up = parent;
                rows.Add(new PickRow { Key = "pk:up", Icon = "up", Text = up != null ? Loc("LOC_KX_UpFolder", "Up one folder") : Loc("LOC_KX_TourPickOtherPlaces", "Other places"),
                                       Command = new RelayCommand(_ => { PlaySound("back"); tourPickFolder = up; BuildTourPickRows(null); }) });
                string[] dirs = new string[0], files = new string[0];
                try
                {
                    dirs = Directory.GetDirectories(folder).Where(x => { try { return (new DirectoryInfo(x).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0; } catch (Exception) { return false; } })
                                   .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).Take(200).ToArray();
                    files = Directory.GetFiles(folder).Where(x => TourPicExts.Contains(Path.GetExtension(x).ToLowerInvariant()))
                                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).Take(300).ToArray();
                }
                catch (Exception ex) { rows.Add(new PickRow { IsInfo = true, Icon = "info", Text = Loc("LOC_KX_FolderUnreadable", "This folder can't be opened.") }); logger.Warn("Kippix: tour picture folder: " + ex.Message); }
                foreach (string f in files)
                {
                    string file = f;
                    var r = new PickRow { Key = "pk:file:" + file, Text = Path.GetFileName(file), Command = new RelayCommand(_ => EndTourPicker(file)) };
                    tiles.Add(r);
                    thumbs.Add(new KeyValuePair<PickRow, string>(r, file));
                }
                foreach (string d in dirs)
                {
                    string dir = d;
                    rows.Add(new PickRow { Key = "pk:dir:" + dir, Icon = "folder", Text = Path.GetFileName(dir), Command = new RelayCommand(_ => TourPickOpen(dir)) });
                }
                if (files.Length == 0 && dirs.Length == 0) rows.Add(new PickRow { IsInfo = true, Icon = "info", Text = Loc("LOC_KX_TourPickEmpty2", "No pictures here (PNG, JPG, WebP or AVIF).") });
            }
            // little previews, off the UI thread
            if (thumbs.Count > 0)
                KippixShared.SafeWork.Queue(_ =>
                {
                    foreach (var t in thumbs)
                    {
                        if (gen != tourPickGen) return;
                        System.Windows.Media.ImageSource img = null;
                        try { img = Images.Load(t.Value, 360); } catch (Exception) { }
                        var row = t.Key;
                        if (img != null) OnUi(() => { if (gen == tourPickGen) row.Picture = img; });
                    }
                });
            Notify("HasTourPicTiles");
            string key = focusKey;
            ui.BeginInvoke(new Action(() =>
            {
                if (!IsTourProfileOpen || kbOpen || gen != tourPickGen) return;
                if (key == null && TourPicTiles.Count > 0) { UiTools.FocusItem("KX_TourPicGrid", TourPicTiles[0]); return; }   // (0.99.7: the first picture)
                var row = key != null ? TourProfileRows.FirstOrDefault(r => r.Key == key) : null;
                if (row == null) row = TourProfileRows.FirstOrDefault(r => r.IsRow && !r.IsInfo && !r.IsHeader && r.Key != "pk:up") ?? TourProfileRows.FirstOrDefault(r => r.IsRow && !r.IsInfo);
                if (row != null) UiTools.FocusItem("KX_TourProfileList", row);
            }), DispatcherPriority.ContextIdle);
        }

        private void TourPickOpen(string dir)
        {
            PlaySound("select");
            tourPickFolder = dir;
            BuildTourPickRows(null);
        }
    }
}
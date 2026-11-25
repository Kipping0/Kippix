using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>A screenshot or clip found in the gallery's folders.</summary>
    internal class GalleryFile
    {
        public string Path;
        public bool IsVideo;
        public DateTime Date;
        public long Size;
        public Guid GameId;          // Guid.Empty: not linked to a game ("Other")
        public string GameName;
    }

    /// <summary>One tile in the gallery's grid (or a date heading, or the "Show more" tile).</summary>
    public class GalleryItem : Observable
    {
        internal GalleryFile File;
        public string Path { get { return File != null ? File.Path : ""; } }
        public bool IsVideo { get { return File != null && File.IsVideo; } }
        public bool IsHeader { get; set; }
        public bool IsMore { get; set; }
        public bool IsTile { get { return !IsHeader && !IsMore; } }
        public string HeaderText { get; set; }
        public string Caption { get; set; }       // game name, or the date on a game's own page
        public string SubText { get; set; }       // (the profile's Recent captures: when)
        public bool HasSubText { get { return !string.IsNullOrEmpty(SubText); } }
        public string MoreText { get; set; }
        private ImageSource thumb;
        public ImageSource Thumb { get { return thumb; } set { if (Set(ref thumb, value, "Thumb")) Notify("HasThumb"); } }
        public bool HasThumb { get { return thumb != null; } }
        private string lengthText;
        public string LengthText { get { return lengthText; } set { if (Set(ref lengthText, value, "LengthText")) Notify("HasLength"); } }
        public bool HasLength { get { return !string.IsNullOrEmpty(lengthText); } }
        private bool isFavourite;
        public bool IsFavourite { get { return isFavourite; } set { Set(ref isFavourite, value, "IsFavourite"); } }
        private bool isChecked;
        public bool IsChecked { get { return isChecked; } set { Set(ref isChecked, value, "IsChecked"); } }
        private bool selectMode;
        public bool SelectMode { get { return selectMode; } set { Set(ref selectMode, value, "SelectMode"); } }
        public ICommand Command { get; set; }
    }

    /// <summary>A game on the gallery's "By game" page (and a row on the storage page).</summary>
    public class GalleryGameTile : Observable
    {
        internal Guid GameId;
        public string Name { get; set; }
        public string CountText { get; set; }
        public string SizeText { get; set; }
        public double BarWidth { get; set; }
        private ImageSource cover;
        public ImageSource Cover { get { return cover; } set { if (Set(ref cover, value, "Cover")) Notify("HasCover"); } }
        public bool HasCover { get { return cover != null; } }
        internal string CoverFile;
        public ICommand Command { get; set; }
    }

    /// <summary>
    /// Gallery: screenshots and clips from the overlay's folders (always), Steam, and any folders you add.
    /// Show all / By game / By date / Favourites, sort and filter, a full screen viewer (zoom, details, slideshow,
    /// video controls), favourites, picking several, deleting (to the Recycle Bin), using a shot as the game's art,
    /// and a storage page with a clean-up.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<GalleryItem> GalleryItems { get; private set; }
        public ObservableCollection<GalleryGameTile> GalleryGames { get; private set; }
        public ObservableCollection<GalleryGameTile> GalleryStorage { get; private set; }
        public ObservableCollection<PickRow> GalleryFolderRows { get; private set; }

        private List<GalleryFile> galleryFiles = new List<GalleryFile>();
        private DateTime galleryScannedAt = DateTime.MinValue;
        private bool galleryScanning;
        private int galleryGen;
        private string galleryView = "games";   // all, games, dates, favs (the tab you pick is kept: settings.GalleryView, By game to start with)
        private string galleryPage = "grid";    // grid, games, game, storage
        private Guid galleryGameId;
        private string galleryGameName = "";
        private List<GalleryFile> galleryList = new List<GalleryFile>();   // what the grid / viewer step through
        private int galleryShown;                 // how many of galleryList are on screen
        private const int GalleryPageSize = 60;
        private bool gallerySelect;
        private bool gallerySettingsFromGallery;
        private IInputElement gallerySettingsFocus;
        private readonly Dictionary<string, ImageSource> galleryThumbs = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> galleryLengths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string galleryStatus = "";
        private DispatcherTimer galleryStatusTimer;

        private static readonly string[] PhotoExts = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".jxr", ".tif", ".tiff" };
        private static readonly string[] VideoExts = { ".mp4", ".mkv", ".mov", ".m4v", ".wmv", ".avi", ".webm" };

        private void InitGallery()
        {
            GalleryItems = new ObservableCollection<GalleryItem>();
            GalleryGames = new ObservableCollection<GalleryGameTile>();
            GalleryStorage = new ObservableCollection<GalleryGameTile>();
            GalleryFolderRows = new ObservableCollection<PickRow>();
            GalleryDetails = new ObservableCollection<WidgetLine>();
            GalleryViewCommand = new RelayCommand(p => SetGalleryView(p as string ?? "all"));
            GallerySortCommand = new RelayCommand(p => SetGallerySort(p as string));
            GalleryShowCommand = new RelayCommand(p => SetGalleryShow(p as string));
            GalleryMenuCommand = new RelayCommand(p => ToggleGalleryMenu(p as string ?? ""));
            GallerySelectCommand = new RelayCommand(_ => SetGallerySelect(!gallerySelect));
            GalleryStorageCommand = new RelayCommand(_ => { galleryMenu = ""; OpenGalleryStorage(); });
            GallerySettingsCommand = new RelayCommand(_ => { galleryMenu = ""; NotifyGallery(); OpenGallerySettings(); });
            GallerySlideshowCommand = new RelayCommand(_ => { galleryMenu = ""; NotifyGallery(); StartSlideshowFromList(); });
            GallerySelectionFavCommand = new RelayCommand(_ => FavouriteSelection());
            GallerySelectionDeleteCommand = new RelayCommand(_ => DeleteSelection());
            GallerySelectAllCommand = new RelayCommand(_ => SelectAllShown());
            InitGalleryMove();   // (0.99.54)
            GalleryAddFolderCommand = new RelayCommand(_ => AddGalleryFolder());
            GalleryCleanKindCommand = new RelayCommand(_ => { settings.GalleryCleanKind = (settings.GalleryCleanKind + 1) % 3; SaveSettings(); PlaySound("move"); NotifyGalleryClean(); });
            GalleryCleanDaysCommand = new RelayCommand(_ => { settings.GalleryCleanDays = NextCleanDays(settings.GalleryCleanDays); SaveSettings(); PlaySound("move"); NotifyGalleryClean(); });
            GalleryCleanCommand = new RelayCommand(_ => RunGalleryClean());
            InitGalleryViewer();
            InitGallery2();   // (1.0.7) the Console design
            InitGallery2Viewer();
        }

        // =====================================================================
        // Opening / leaving
        // =====================================================================

        public bool IsGalleryOpen { get { return openPanel == "gallery" || (openPanel == "options" && gallerySettingsFromGallery); } }
        public bool IsGalleryGrid { get { return galleryPage == "grid" || galleryPage == "game"; } }
        public bool IsGalleryGames { get { return galleryPage == "games" || galleryPage == "moveto"; } }
        public bool IsGalleryStorage { get { return galleryPage == "storage"; } }
        public bool GalleryViewAll { get { return galleryView == "all" && galleryPage == "grid"; } }
        public bool GalleryViewGames { get { return galleryView == "games" && (galleryPage == "games" || galleryPage == "game"); } }
        public bool GalleryViewDates { get { return galleryView == "dates" && galleryPage == "grid"; } }
        public bool GalleryViewFavs { get { return galleryView == "favs" && galleryPage == "grid"; } }
        public bool IsGalleryGamePage { get { return galleryPage == "game"; } }
        public string GalleryTitle { get { return galleryPage == "moveto" ? MoveTitle : galleryPage == "game" ? galleryGameName : galleryPage == "storage" ? Loc("LOC_KX_GalleryStorage", "Storage") : Loc("LOC_KX_Gallery", "Gallery"); } }
        public string GallerySubText { get; private set; }
        public bool GalleryLoading { get { return galleryScanning && galleryFiles.Count == 0; } }
        public string GalleryEmptyText { get; private set; }
        public bool HasGalleryEmpty { get { return !string.IsNullOrEmpty(GalleryEmptyText) && !GalleryLoading; } }
        public string GallerySortText { get { return settings.GallerySort == 1 ? Loc("LOC_KX_OldestFirst", "Oldest first") : Loc("LOC_KX_NewestFirst", "Newest first"); } }
        public string GalleryShowText
        {
            get { return settings.GalleryShow == 1 ? Loc("LOC_KX_PhotosOnly", "Photos") : settings.GalleryShow == 2 ? Loc("LOC_KX_VideosOnly", "Videos") : Loc("LOC_KX_PhotosAndVideos", "Photos and videos"); }
        }
        // the header's two small menus: "sort" (sort and filter) and "more" (slideshow, storage, folders)
        private string galleryMenu = "";
        public bool IsGalleryMenuSort { get { return galleryMenu == "sort"; } }
        public bool IsGalleryMenuMore { get { return galleryMenu == "more"; } }
        public string GalleryFilterText { get { return GallerySortText + "  \u00B7  " + GalleryShowText; } }
        public bool GallerySortNewest { get { return settings.GallerySort != 1; } }
        public bool GallerySortOldest { get { return settings.GallerySort == 1; } }
        public bool GalleryShowBoth { get { return settings.GalleryShow == 0; } }
        public bool GalleryShowPhotos { get { return settings.GalleryShow == 1; } }
        public bool GalleryShowVideos { get { return settings.GalleryShow == 2; } }
        public bool GallerySelectMode { get { return gallerySelect; } }
        public string GallerySelectedText
        {
            get
            {
                int n = GalleryItems.Count(i => i.IsChecked);
                return n == 0 ? Loc("LOC_KX_SelectSome", "Choose photos and videos to select them") : string.Format(Loc("LOC_KX_NSelected", "{0} selected"), n);
            }
        }
        public string GalleryStatus { get { return galleryStatus; } }
        public bool HasGalleryStatus { get { return !string.IsNullOrEmpty(galleryStatus); } }

        public ICommand GalleryViewCommand { get; private set; }
        public ICommand GalleryMenuCommand { get; private set; }
        public ICommand GallerySortCommand { get; private set; }
        public ICommand GalleryShowCommand { get; private set; }
        public ICommand GallerySelectCommand { get; private set; }
        public ICommand GalleryStorageCommand { get; private set; }
        public ICommand GallerySettingsCommand { get; private set; }
        public ICommand GallerySlideshowCommand { get; private set; }
        public ICommand GallerySelectionFavCommand { get; private set; }
        public ICommand GallerySelectionDeleteCommand { get; private set; }
        public ICommand GallerySelectAllCommand { get; private set; }
        public ICommand GalleryAddFolderCommand { get; private set; }

        private void NotifyGallery()
        {
            foreach (string n in new[] { "IsGalleryOpen", "IsGalleryGrid", "IsGalleryGames", "IsGalleryStorage", "GalleryViewAll", "GalleryViewGames", "GalleryViewDates",
                                         "GalleryViewFavs", "IsGalleryGamePage", "GalleryTitle", "GallerySubText", "GalleryLoading", "GalleryEmptyText", "HasGalleryEmpty",
                                         "GallerySortText", "GalleryShowText", "GalleryFilterText", "GallerySortNewest", "GallerySortOldest", "GalleryShowBoth",
                                         "GalleryShowPhotos", "GalleryShowVideos", "IsGalleryMenuSort", "IsGalleryMenuMore", "GallerySelectMode", "GallerySelectedText", "GalleryStatus", "HasGalleryStatus" })
                Notify(n);
            NotifyGallery2();
        }

        /// <summary>Top bar / home tile: the gallery, on the view you used last.</summary>
        private void OpenGallery()
        {
            if (openPanel != "gallery") TogglePanel("gallery");
            if (!galleryPickProfile) galleryView = settings.GalleryView ?? "games";   // the tab you chose last time (picking a picture: all of them)
            galleryPage = galleryView == "games" ? "games" : "grid";
            gallerySelect = false;
            galleryOpenFile = null;
            g2Game = null;   // (1.0.7) from the top bar: every game
            Gallery2Opening();
            NotifyGallery();
            if (galleryFiles.Count == 0 || (DateTime.Now - galleryScannedAt).TotalSeconds > 20) ScanGallery(true);
            else RebuildGallery(true);
        }

        /// <summary>Opens the gallery straight on one game's captures (the game page's "Your captures").</summary>
        private void OpenGalleryForGame(Guid id, string name, string file)
        {
            if (openPanel != "gallery") TogglePanel("gallery");
            galleryView = "games";
            galleryPage = "game";
            galleryGameId = id;
            galleryGameName = name ?? "";
            gallerySelect = false;
            galleryOpenFile = file;
            galleryViewerFromGamePage = file != null;   // (0.99.56) a picture opened from the game page: B goes back there
            Gallery2Opening();
            NotifyGallery();
            if (galleryFiles.Count == 0 || (DateTime.Now - galleryScannedAt).TotalSeconds > 20) ScanGallery(true);
            else RebuildGallery(true);
        }

        private string galleryOpenFile;   // open the viewer on this one once the grid is built
        private bool galleryViewerFromGamePage;   // (0.99.56) B in the viewer closes the gallery (back to the game page's captures)
        private bool galleryChanged;              // (0.99.56) something saved / deleted / moved: the game page's captures are read again

        /// <summary>The gallery is closing (or another panel opens): stop any video, slideshow or loading.</summary>
        private void LeaveGallery()
        {
            galleryViewerFromGamePage = false;
            if (galleryChanged && detailsOpen)
            {
                // (0.99.56) a frame saved, a picture deleted or moved: the game page's captures row shows it
                galleryChanged = false;
                var dg = SelectedGameNow();
                if (dg != null) ui.BeginInvoke(new Action(() => { try { FillCaptures(dg, detMoreGen); } catch (Exception) { } }), DispatcherPriority.Background);
            }
            CloseGalleryViewer(false);
            StopGalleryPick();
            gallerySelect = false;
            galleryMenu = "";
            galleryOpenFile = null;   // closed before the scan finished: don't pop the viewer open next time
            galleryGen++;
            gallerySettingsFromGallery = false;
            foreach (var i in GalleryItems) i.Thumb = null;   // let the pictures go
            GalleryItems.Clear();
            ForgetGallery2Pictures();
            lock (galleryThumbs) galleryThumbs.Clear();
        }

        /// <summary>B in the gallery: close the viewer, leave picking, go back a page; false = close the gallery.</summary>
        private bool GalleryBack()
        {
            if (galleryEditOpen) { GeAct("b"); return true; }   // (0.99.59)
            if (galleryViewerOpen && galleryMoreOpen) { PlaySound("back"); SetGalleryMore(false); return true; }
            if (galleryViewerOpen && Gallery2 && !galleryControlsShown) { ToggleViewer2Controls(); return true; }   // (1.0.7) the Console viewer: B brings the controls back first
            if (galleryViewerOpen)
            {
                if (galleryZoom > 1.01) { SetGalleryZoom(1); return true; }
                if (galleryViewerFromGamePage)
                {
                    // (0.99.56) opened from the game page's captures: back to that picture there, not into the gallery
                    galleryViewerFromGamePage = false;
                    string path = Current != null ? Current.Path : null;
                    CloseGalleryViewer(false);
                    ui.BeginInvoke(new Action(() => FocusCaptureOnGamePage(path)), DispatcherPriority.ContextIdle);
                    return false;   // (the gallery closes)
                }
                CloseGalleryViewer(true);
                return true;
            }
            if (Gallery2Back()) return true;   // (1.0.7)
            if (gallerySelect) { SetGallerySelect(false); return true; }
            if (GalleryMoveBack()) return true;
            if (CloseGalleryMenu()) return true;
            if (galleryPage == "game")
            {
                galleryPage = "games";
                NotifyGallery();
                var from = galleryGameId;
                RebuildGallery(false);
                FocusGalleryGame(from);
                return true;
            }
            if (galleryPage == "storage")
            {
                galleryPage = galleryView == "games" ? "games" : "grid";
                NotifyGallery();
                RebuildGallery(false);
                ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_GalleryMoreChip")), DispatcherPriority.ContextIdle);
                return true;
            }
            return false;
        }

        private void SetGalleryView(string view)
        {
            PlaySound("select");
            galleryMenu = "";
            galleryView = view;
            if (!galleryPickProfile && settings.GalleryView != view) { settings.GalleryView = view; SaveSettings(); }   // opens on this tab next time too
            galleryPage = view == "games" ? "games" : "grid";
            gallerySelect = false;
            NotifyGallery();
            RebuildGallery(true);
            string chip = view == "games" ? "KX_GalleryTabGames" : view == "dates" ? "KX_GalleryTabDates" : view == "favs" ? "KX_GalleryTabFavs" : "KX_GalleryTabAll";
            ui.BeginInvoke(new Action(() => UiTools.FocusByName(chip)), DispatcherPriority.ContextIdle);
        }

        /// <summary>Opens or closes one of the header's menus (a row of choices under the header); focus goes into it, and back to its button on close.</summary>
        private void ToggleGalleryMenu(string menu)
        {
            string was = galleryMenu;
            galleryMenu = was == menu ? "" : menu;
            PlaySound(galleryMenu == "" ? "back" : "select");
            NotifyGallery();
            string target = galleryMenu == "sort" ? (settings.GallerySort == 1 ? "KX_GallerySortOld" : "KX_GallerySortNew")
                          : galleryMenu == "more" ? "KX_GalleryMoreSlides"
                          : was == "more" ? "KX_GalleryMoreChip" : "KX_GalleryFilterChip";
            ui.BeginInvoke(new Action(() => UiTools.FocusByName(target)), DispatcherPriority.ContextIdle);
        }

        /// <summary>B with a header menu open: close it.</summary>
        private bool CloseGalleryMenu()
        {
            if (galleryMenu == "") return false;
            ToggleGalleryMenu(galleryMenu);
            return true;
        }

        /// <summary>A sort or filter choice: the grid changes underneath, the highlight stays on the choice you pressed.</summary>
        private void SetGallerySort(string p)
        {
            int v;
            if (!int.TryParse(p, out v)) v = (settings.GallerySort + 1) % 2;
            KeepFocusWhile(() => { settings.GallerySort = v; SaveSettings(); PlaySound("select"); RebuildGallery(false); });
        }

        private void SetGalleryShow(string p)
        {
            int v;
            if (!int.TryParse(p, out v)) v = (settings.GalleryShow + 1) % 3;
            KeepFocusWhile(() => { settings.GalleryShow = v; SaveSettings(); PlaySound("select"); RebuildGallery(false); });
        }

        private void KeepFocusWhile(Action change)
        {
            var keep = Keyboard.FocusedElement as UIElement;
            change();
            if (keep != null) ui.BeginInvoke(new Action(() => { if (keep.IsVisible && !keep.IsKeyboardFocused) Keyboard.Focus(keep); }), DispatcherPriority.ContextIdle);
        }

        // =====================================================================
        // Folders
        // =====================================================================

        public bool IsOptionsGalleryFolders { get { return optionsPage == "galleryfolders"; } }
        public bool GallerySteam
        {
            get { return settings.GallerySteam; }
            set { if (settings.GallerySteam == value) return; settings.GallerySteam = value; SaveSettings(); Notify("GallerySteam"); galleryScannedAt = DateTime.MinValue; }
        }

        private KippixOverlay.OverlayConfig OverlayConfigNow()
        {
            try
            {
                if (plugin.Overlay != null && plugin.Overlay.Config != null) return plugin.Overlay.Config;
                string path = Path.Combine(api.Paths.ExtensionsDataPath, KippixOverlay.OverlayPlugin.PluginId.ToString(), "config.json");
                return KippixOverlay.OverlayConfig.Load(path);
            }
            catch (Exception) { return new KippixOverlay.OverlayConfig(); }
        }

        /// <summary>Where the overlay saves screenshots (its setting), and where its recorder saves clips.</summary>
        private void OverlayFolders(out string shots, out List<string> clips, out string clipsText)
        {
            var c = OverlayConfigNow();
            shots = c != null && !string.IsNullOrWhiteSpace(c.ScreenshotFolder) ? c.ScreenshotFolder : KippixOverlay.OverlayConfig.DefaultScreenshotFolder;
            clips = new List<string>();
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            int method = c != null ? c.ReplayMethod : 0;
            if (!string.IsNullOrEmpty(videos))
            {
                clips.Add(Path.Combine(videos, "Captures"));   // Xbox Game Bar (screenshots too)
                if (method != 1) clips.Add(videos);            // NVIDIA: Videos › game name (only folders named after your games are read)
            }
            clipsText = method == 1 ? Loc("LOC_KX_ClipsGameBar", "Xbox Game Bar: Videos › Captures")
                      : method == 3 ? Loc("LOC_KX_ClipsOff", "Videos › Captures (saving gameplay is off in the overlay)")
                      : Loc("LOC_KX_ClipsNvidia", "NVIDIA and Game Bar: Videos › game folders and Captures");
        }

        private void BuildGalleryFolderRows()
        {
            GalleryFolderRows.Clear();
            string shots, clipsText;
            List<string> clips;
            OverlayFolders(out shots, out clips, out clipsText);
            // set in the overlay, so they can't be changed here (the lines say where)
            string inOverlay = "  ·  " + Loc("LOC_KX_ChangeInOverlay", "change it in the overlay");
            GalleryFolderRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecFromOverlay", "FROM THE OVERLAY") });
            GalleryFolderRows.Add(new PickRow { Text = Loc("LOC_KX_OverlayShots", "Screenshots"), Sub = shots + inOverlay, Icon = "lock", IsInfo = true });
            GalleryFolderRows.Add(new PickRow { Text = Loc("LOC_KX_OverlayClips", "Clips"), Sub = clipsText + inOverlay, Icon = "lock", IsInfo = true });
            GalleryFolderRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_YourFolders", "YOUR FOLDERS") });
            foreach (string f in settings.GalleryFolders.ToList())
            {
                string folder = f;
                GalleryFolderRows.Add(new PickRow
                {
                    Text = Path.GetFileName(folder.TrimEnd('\\')).Length > 0 ? Path.GetFileName(folder.TrimEnd('\\')) : folder,
                    Sub = folder + (Directory.Exists(folder) ? "" : "  ·  " + Loc("LOC_KX_FolderMissing", "not found")),
                    Icon = "remove", Key = folder,
                    Command = new RelayCommand(_ => RemoveGalleryFolder(folder))
                });
            }
            GalleryFolderRows.Add(new PickRow { Text = Loc("LOC_KX_AddFolder", "Add a folder"), Sub = Loc("LOC_KX_AddFolderSub", "Photos and videos in it (and two folders down) show in the gallery"),
                                                Icon = "add", Key = "addfolder", Command = new RelayCommand(_ => AddGalleryFolder()) });
        }

        private void AddGalleryFolder()
        {
            OpenPickerFolder(Loc("LOC_KX_AddFolder", "Add a folder"), "galleryfolders", dir =>
            {
                if (!settings.GalleryFolders.Any(f => string.Equals(f.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                {
                    settings.GalleryFolders.Add(dir);
                    SaveSettings();
                    galleryScannedAt = DateTime.MinValue;
                }
                GoBackTo("galleryfolders", "addfolder");
            });
        }

        private void RemoveGalleryFolder(string folder)
        {
            PlaySound("back");
            int at = GalleryFolderRows.ToList().FindIndex(r => r.Key == folder);
            settings.GalleryFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
            galleryScannedAt = DateTime.MinValue;
            BuildGalleryFolderRows();
            // highlight what's now in that spot (the next folder, or Add a folder)
            var next = at >= 0 && at < GalleryFolderRows.Count ? GalleryFolderRows[at] : GalleryFolderRows.LastOrDefault();
            if (next != null && next.IsHeader) next = GalleryFolderRows.LastOrDefault();
            ui.BeginInvoke(new Action(() => { if (next != null) UiTools.FocusItem("KX_GalleryFolderList", next); }), DispatcherPriority.ContextIdle);
        }

        /// <summary>The gear on the gallery: the folders page opens over it.</summary>
        private void OpenGallerySettings()
        {
            gallerySettingsFromGallery = openPanel == "gallery";
            if (gallerySettingsFromGallery)
            {
                gallerySettingsFocus = Keyboard.FocusedElement;
                CloseGalleryViewer(false);
            }
            if (openPanel != "options")
            {
                if (openPanel == null) { focusBeforePanel = Keyboard.FocusedElement; RememberLowerFocus(); }
                openPanel = "options";
                NotifyPanels();
                UpdateTopBarFade();
            }
            SetOptionsPage("galleryfolders");
        }

        private bool GallerySettingsBack()
        {
            if (!gallerySettingsFromGallery) return false;
            gallerySettingsFromGallery = false;
            openPanel = "gallery";
            NotifyPanels();
            UpdateTopBarFade();
            NotifyGallery();
            if ((DateTime.Now - galleryScannedAt).TotalSeconds > 20) ScanGallery(false);
            var back = gallerySettingsFocus as UIElement;
            gallerySettingsFocus = null;
            ui.BeginInvoke(new Action(() =>
            {
                if (back != null && back.IsVisible) Keyboard.Focus(back);
                else UiTools.FocusByName("KX_GalleryMoreChip");
            }), DispatcherPriority.ContextIdle);
            return true;
        }

        // =====================================================================
        // Finding the files
        // =====================================================================

        private string CaptureIndexPath { get { return Path.Combine(plugin.GetPluginUserDataPath(), "captures.json"); } }
        private Dictionary<string, string> captureIndex;   // file → game id, written when the overlay saves something
        private readonly object captureLock = new object();

        private Dictionary<string, string> CaptureIndex()
        {
            lock (captureLock)
            {
                if (captureIndex != null) return captureIndex;
                captureIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(CaptureIndexPath))
                    {
                        var d = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(CaptureIndexPath));
                        if (d != null) foreach (var kv in d) captureIndex[kv.Key] = kv.Value;
                    }
                }
                catch (Exception) { }
                return captureIndex;
            }
        }

        /// <summary>The overlay saved a screenshot or found a new clip: remember which game it's from.</summary>
        internal void RecordCapture(string file, Game game)
        {
            if (string.IsNullOrEmpty(file) || game == null) return;
            KippixShared.SafeWork.Queue(_ =>
            {
                try
                {
                    lock (captureLock)
                    {
                        var idx = CaptureIndex();
                        idx[file] = game.Id.ToString();
                        if (idx.Count > 5000)
                            foreach (var k in idx.Keys.Where(k => !File.Exists(k)).ToList()) idx.Remove(k);   // forget deleted ones
                        Directory.CreateDirectory(Path.GetDirectoryName(CaptureIndexPath));
                        File.WriteAllText(CaptureIndexPath, new JavaScriptSerializer().Serialize(idx));
                    }
                    galleryScannedAt = DateTime.MinValue;   // show it next time the gallery opens
                }
                catch (Exception ex) { logger.Warn("Kippix: capture index: " + ex.Message); }
            });
        }

        private void ScanGallery(bool focusFirst)
        {
            if (galleryScanning) return;
            galleryScanning = true;
            NotifyGallery();
            int rebuilds = galleryRebuilds;
            KippixShared.SafeWork.Queue(_ =>
            {
                List<GalleryFile> found = null;
                try { found = FindGalleryFiles(); }
                catch (Exception ex) { logger.Warn("Kippix: gallery scan: " + ex.Message); }
                OnUi(() =>
                {
                    galleryScanning = false;
                    if (found != null) { galleryFiles = found; galleryScannedAt = DateTime.Now; }
                    // the game page's captures card skipped its own look while this ran: fill it now
                    if (found != null && detailsOpen) { var dg = SelectedGameNow(); if (dg != null && dg.Id == detGameId) FillCaptures(dg, detMoreGen); }
                    // whatever happened meanwhile (closed and opened again, a view changed), an open gallery shows what was found
                    if ((openPanel != "gallery" && !gallerySettingsFromGallery) || galleryViewerOpen) { NotifyGallery(); return; }
                    RebuildGallery(focusFirst && rebuilds == galleryRebuilds);
                });
            });
        }

        /// <summary>All the gallery's photos and videos, each linked to a game where we can tell which (runs on a worker thread).</summary>
        private List<GalleryFile> FindGalleryFiles()
        {
            var files = new Dictionary<string, GalleryFile>(StringComparer.OrdinalIgnoreCase);
            var games = api.Database.Games.Where(g => g != null && !string.IsNullOrEmpty(g.Name)).ToList();
            var byName = new Dictionary<string, Game>();
            foreach (var g in games.OrderByDescending(g => g.LastActivity ?? DateTime.MinValue))
            {
                string n = NormName(g.Name);
                if (n.Length >= 2 && !byName.ContainsKey(n)) byName[n] = g;
            }
            var namesLongestFirst = byName.Keys.OrderByDescending(k => k.Length).ToList();
            var byId = games.GroupBy(g => g.Id).ToDictionary(x => x.Key, x => x.First());
            var steamById = games.Where(g => g.PluginId == SteamPluginId && !string.IsNullOrEmpty(g.GameId)).GroupBy(g => g.GameId).ToDictionary(x => x.Key, x => x.First());
            var index = CaptureIndex();
            Dictionary<string, string> idxCopy;
            lock (captureLock) idxCopy = new Dictionary<string, string>(index, StringComparer.OrdinalIgnoreCase);
            string src;
            List<PlaySession> sessions = new List<PlaySession>();
            try { sessions = LoadSessions(out src).Where(x => x.Seconds > 0).ToList(); } catch (Exception) { }

            Action<string, Guid, bool> addFile = (path, forced, nameFromFolder) =>
            {
                if (files.ContainsKey(path)) return;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool video = VideoExts.Contains(ext);
                if (!video && !PhotoExts.Contains(ext)) return;
                FileInfo fi;
                try { fi = new FileInfo(path); if ((fi.Attributes & FileAttributes.Hidden) != 0 || fi.Length == 0) return; } catch (Exception) { return; }
                var gf = new GalleryFile { Path = path, IsVideo = video, Date = fi.LastWriteTime, Size = fi.Length };
                Game game = null;
                string sid;
                Guid gid;
                if (forced != Guid.Empty) byId.TryGetValue(forced, out game);
                if (game == null && idxCopy.TryGetValue(path, out sid) && Guid.TryParse(sid, out gid)) byId.TryGetValue(gid, out game);
                if (game == null)
                {
                    // taken while a game was running
                    var s = sessions.FirstOrDefault(x => gf.Date >= x.Start.AddSeconds(-30) && gf.Date <= x.Start.AddSeconds(x.Seconds + 120));
                    if (s != null && Guid.TryParse(s.GameId, out gid)) byId.TryGetValue(gid, out game);
                }
                if (game == null)
                {
                    // the name: NVIDIA puts clips in a folder named after the game; the overlay and Game Bar start the file name with it
                    string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                    string fromFile = StripCaptureDate(Path.GetFileNameWithoutExtension(path));
                    game = MatchGame(NormName(fromFile), byName, namesLongestFirst);
                    if (game == null && nameFromFolder) game = MatchGame(NormName(folder), byName, namesLongestFirst);
                }
                if (game != null) { gf.GameId = game.Id; gf.GameName = game.Name; }
                files[path] = gf;
            };

            string shots, clipsText;
            List<string> clips;
            OverlayFolders(out shots, out clips, out clipsText);
            ScanFolder(shots, 2, p => addFile(p, Guid.Empty, false));
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            Dictionary<string, Game> byFolder = null;
            foreach (string c in clips)
            {
                if (string.Equals(c, videos, StringComparison.OrdinalIgnoreCase))
                {
                    // Videos itself: only folders named after one of your games (so your own videos stay out)
                    try
                    {
                        if (!Directory.Exists(c)) continue;
                        // (0.99.53) the NVIDIA app saves to Videos › NVIDIA › game (GeForce Experience saved to Videos › game): both
                        var gameDirs = Directory.GetDirectories(c).ToList();
                        string nv = Path.Combine(c, "NVIDIA");
                        if (Directory.Exists(nv)) { gameDirs.RemoveAll(x => string.Equals(x, nv, StringComparison.OrdinalIgnoreCase)); gameDirs.AddRange(Directory.GetDirectories(nv)); }
                        foreach (string d in gameDirs)
                        {
                            string leaf = Path.GetFileName(d);
                            var g = MatchGame(NormName(leaf), byName, namesLongestFirst);
                            if (g == null)
                            {
                                // (0.99.53) NVIDIA names the folder after the game's program or install folder ("P5R" for
                                // Persona 5 Royal), not its name: matched against your installed games' folders and programs
                                if (byFolder == null) byFolder = GamesByFolder(games);
                                byFolder.TryGetValue(NormName(leaf), out g);
                            }
                            if (g != null)
                            {
                                Guid id = g.Id;
                                ScanFolder(d, 1, p => addFile(p, id, true));
                                continue;
                            }
                            // a folder with a clip the overlay saved in it (it knows which game that was)
                            string pre = d.TrimEnd('\\') + "\\";
                            if (idxCopy.Keys.Any(k => k.StartsWith(pre, StringComparison.OrdinalIgnoreCase)))
                                ScanFolder(d, 1, p => addFile(p, Guid.Empty, false));
                        }
                    }
                    catch (Exception) { }
                }
                else ScanFolder(c, 1, p => addFile(p, Guid.Empty, false));
            }
            if (settings.GallerySteam) ScanSteamScreenshots(steamById, addFile);
            foreach (string f in settings.GalleryFolders.ToList()) ScanFolder(f, 2, p => addFile(p, Guid.Empty, true));
            return files.Values.ToList();
        }

        /// <summary>(0.99.53) Installed games by their install folder's name and their programs' names ("p5r" → Persona 5 Royal).</summary>
        private static Dictionary<string, Game> GamesByFolder(List<Game> games)
        {
            var map = new Dictionary<string, Game>();
            foreach (var g in games.Where(x => x.IsInstalled && !string.IsNullOrEmpty(x.InstallDirectory)).OrderByDescending(x => x.LastActivity ?? DateTime.MinValue))
            {
                try
                {
                    string dir = g.InstallDirectory.TrimEnd('\\', '/');
                    string n = NormName(Path.GetFileName(dir));
                    if (n.Length >= 2 && !map.ContainsKey(n)) map[n] = g;
                    if (!Directory.Exists(dir)) continue;
                    int k = 0;
                    foreach (string exe in Directory.EnumerateFiles(dir, "*.exe"))
                    {
                        if (++k > 12) break;
                        string e = NormName(Path.GetFileNameWithoutExtension(exe));
                        if (e.Length < 2 || e.Contains("unins") || e.Contains("crash") || e.Contains("setup") || e.Contains("launcher") || e.Contains("redist")) continue;
                        if (!map.ContainsKey(e)) map[e] = g;
                    }
                }
                catch (Exception) { }
            }
            return map;
        }

        private static void ScanFolder(string dir, int depth, Action<string> add)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (string f in Directory.EnumerateFiles(dir)) add(f);
                if (depth <= 0) return;
                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    try { if ((new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue; } catch (Exception) { continue; }
                    ScanFolder(d, depth - 1, add);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Steam's screenshots: userdata › you › 760 › remote › app id › screenshots.</summary>
        private static void ScanSteamScreenshots(Dictionary<string, Game> steamById, Action<string, Guid, bool> add)
        {
            try
            {
                string steam = null;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    steam = k != null ? Convert.ToString(k.GetValue("SteamPath")) : null;
                if (string.IsNullOrEmpty(steam)) return;
                string userdata = Path.Combine(steam.Replace('/', '\\'), "userdata");
                if (!Directory.Exists(userdata)) return;
                foreach (string user in Directory.GetDirectories(userdata))
                {
                    string remote = Path.Combine(user, "760", "remote");
                    if (!Directory.Exists(remote)) continue;
                    foreach (string app in Directory.GetDirectories(remote))
                    {
                        string shots = Path.Combine(app, "screenshots");
                        if (!Directory.Exists(shots)) continue;
                        Game g;
                        Guid id = steamById.TryGetValue(Path.GetFileName(app), out g) ? g.Id : Guid.Empty;
                        try { foreach (string f in Directory.EnumerateFiles(shots, "*.jpg")) add(f, id, false); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        private static readonly Regex CaptureDate = new Regex(@"[\s_\-]*(\d{1,4}[._\-]\d{1,2}[._\-]\d{1,4}|\d{8}[_\-]?\d{4,6}|Screenshot|Clip).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string StripCaptureDate(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return CaptureDate.Replace(name, "").Trim();
        }

        /// <summary>Lower case letters and digits only (so "Hades II" matches "hades_ii").</summary>
        private static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static Game MatchGame(string norm, Dictionary<string, Game> byName, List<string> longestFirst)
        {
            if (string.IsNullOrEmpty(norm) || norm.Length < 2) return null;
            Game g;
            if (byName.TryGetValue(norm, out g)) return g;
            // the file name starts with the game's name ("Hades II 2026-...")
            foreach (string n in longestFirst)
                if (n.Length >= 4 && norm.StartsWith(n, StringComparison.Ordinal)) return byName[n];
            return null;
        }

        // =====================================================================
        // Building what's on screen
        // =====================================================================

        private IEnumerable<GalleryFile> Shown(IEnumerable<GalleryFile> all)
        {
            if (settings.GalleryShow == 1) all = all.Where(f => !f.IsVideo);
            if (settings.GalleryShow == 2) all = all.Where(f => f.IsVideo);
            return settings.GallerySort == 1 ? all.OrderBy(f => f.Date) : all.OrderByDescending(f => f.Date);
        }

        private HashSet<string> FavSet() { return new HashSet<string>(settings.GalleryFavourites ?? new List<string>(), StringComparer.OrdinalIgnoreCase); }

        private int galleryRebuilds;

        private void RebuildGallery(bool focusFirst)
        {
            galleryRebuilds++;
            Gallery2Opening();   // (1.0.7) the Console design has no games page
            int gen = ++galleryGen;
            foreach (var i in GalleryItems) i.Thumb = null;
            GalleryItems.Clear();
            GalleryGames.Clear();
            GalleryEmptyText = "";
            var favs = FavSet();

            if (galleryPage == "moveto")
            {
                BuildMoveTargets(gen);
                NotifyGallery();
                if (focusFirst) ui.BeginInvoke(new Action(FocusGalleryStart), DispatcherPriority.ContextIdle);
                return;
            }
            if (galleryPage == "games")
            {
                var groups = Shown(galleryFiles).GroupBy(f => f.GameId).ToList();
                var tiles = new List<GalleryGameTile>();
                foreach (var g in groups)
                {
                    var list = g.ToList();
                    Guid id = g.Key;
                    string name = id == Guid.Empty ? Loc("LOC_KX_OtherCaptures", "Other") : list[0].GameName;
                    Game game = id == Guid.Empty ? null : api.Database.Games.Get(id);
                    var t = new GalleryGameTile
                    {
                        GameId = id, Name = name, CountText = CountText(list),
                        CoverFile = game != null && !string.IsNullOrEmpty(game.CoverImage) ? SafeFullPath(game.CoverImage) : null
                    };
                    string n2 = name;
                    t.Command = new RelayCommand(_ => OpenGalleryGame(id, n2));
                    tiles.Add(t);
                }
                // most recent first (Other last)
                foreach (var t in tiles.OrderBy(t => t.GameId == Guid.Empty ? 1 : 0)
                                       .ThenByDescending(t => galleryFiles.Where(f => f.GameId == t.GameId).Select(f => f.Date).DefaultIfEmpty().Max()))
                    GalleryGames.Add(t);
                galleryList = new List<GalleryFile>();
                if (GalleryGames.Count == 0) GalleryEmptyText = EmptyGalleryText();
                GallerySubText = string.Format(Loc("LOC_KX_GalleryGamesSub", "{0} games  ·  {1}"), GalleryGames.Count(t => t.GameId != Guid.Empty), CountText(Shown(galleryFiles).ToList()));
                LoadGameCovers(gen);
            }
            else
            {
                IEnumerable<GalleryFile> src = galleryFiles;
                if (galleryPage == "game") src = src.Where(f => f.GameId == galleryGameId);
                else if (galleryView == "favs") src = src.Where(f => favs.Contains(f.Path));
                galleryList = Gallery2Grid ? Gallery2List(favs) : Shown(src).ToList();
                galleryShown = 0;
                AppendGalleryTiles(GalleryPageSize, favs);
                if (galleryList.Count == 0)
                    GalleryEmptyText = (Gallery2Grid ? g2Tab == "favs" : galleryView == "favs" && galleryPage == "grid") ? Loc("LOC_KX_NoFavourites", "Nothing here yet. Press X on a photo or video to make it a favourite.") : EmptyGalleryText();
                GallerySubText = CountText(galleryList) + (galleryList.Count > 0 ? "  ·  " + KippixOverlay.Capture.DriveSize(galleryList.Sum(f => f.Size)) : "");
            }
            NotifyGallery();
            Gallery2Built();
            if (galleryOpenFile != null)
            {
                string open = galleryOpenFile;
                galleryOpenFile = null;
                int at = galleryList.FindIndex(f => string.Equals(f.Path, open, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) { OpenGalleryViewer(at); return; }
            }
            if (focusFirst) ui.BeginInvoke(new Action(FocusGalleryStart), DispatcherPriority.ContextIdle);
        }

        private string EmptyGalleryText()
        {
            if (galleryScanning) return "";
            return galleryFiles.Count == 0
                ? Loc("LOC_KX_GalleryNone", "No screenshots or clips yet. Take some with the overlay, Steam, NVIDIA or Xbox Game Bar, or add a folder with the gear.")
                : Loc("LOC_KX_GalleryNoneHere", "Nothing to show with these settings.");
        }

        private void FocusGalleryStart()
        {
            if (openPanel != "gallery") return;
            var first = GalleryItems.FirstOrDefault(i => i.IsTile);
            if ((galleryPage == "games" || galleryPage == "moveto") && GalleryGames.Count > 0) { UiTools.FocusItem("KX_GalleryGameList", GalleryGames[0]); return; }
            if (first != null) { UiTools.FocusItem(GalleryGridName, first); return; }
            if (Gallery2Grid) { UiTools.FocusItem("KX_Gallery2Tabs", Gallery2Tabs.FirstOrDefault(t => t.IsSelected)); return; }
            UiTools.FocusInside("KX_GalleryView");
        }

        private void FocusGalleryGame(Guid id)
        {
            ui.BeginInvoke(new Action(() =>
            {
                var t = GalleryGames.FirstOrDefault(x => x.GameId == id);
                if (t != null) UiTools.FocusItem("KX_GalleryGameList", t);
                else FocusGalleryStart();
            }), DispatcherPriority.ContextIdle);
        }

        private string CountText(List<GalleryFile> list)
        {
            int v = list.Count(f => f.IsVideo), p = list.Count - v;
            var parts = new List<string>();
            if (p > 0 || v == 0) parts.Add(string.Format(p == 1 ? Loc("LOC_KX_OnePhoto", "{0} photo") : Loc("LOC_KX_NPhotos", "{0} photos"), p));
            if (v > 0) parts.Add(string.Format(v == 1 ? Loc("LOC_KX_OneVideo", "{0} video") : Loc("LOC_KX_NVideos", "{0} videos"), v));
            return string.Join("  ·  ", parts);
        }

        private void OpenGalleryGame(Guid id, string name)
        {
            PlaySound("select");
            galleryPage = "game";
            galleryGameId = id;
            galleryGameName = name;
            NotifyGallery();
            RebuildGallery(true);
        }

        /// <summary>The next lot of tiles (with date headings on "By date"), and a "Show more" tile when there are still more.</summary>
        private void AppendGalleryTiles(int count, HashSet<string> favs)
        {
            var more = GalleryItems.LastOrDefault();
            if (more != null && more.IsMore) GalleryItems.Remove(more);
            string lastHeader = null;
            bool days = (galleryView == "dates" && galleryPage == "grid") || Gallery2Grid;
            if (days)
            {
                var h = GalleryItems.LastOrDefault(i => i.IsHeader);
                lastHeader = h != null ? h.HeaderText : null;
            }
            int end = Math.Min(galleryList.Count, galleryShown + count);
            var added = new List<GalleryItem>();
            for (int i = galleryShown; i < end; i++)
            {
                var f = galleryList[i];
                if (days)
                {
                    string head = Gallery2Grid ? Gallery2Heading(f.Date) : DateHeading(f.Date);
                    if (head != lastHeader) { GalleryItems.Add(new GalleryItem { IsHeader = true, HeaderText = head }); lastHeader = head; }
                }
                var item = new GalleryItem
                {
                    File = f,
                    Caption = galleryPage == "game" ? f.Date.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)
                                                    : (f.GameId == Guid.Empty ? Loc("LOC_KX_OtherCaptures", "Other") : f.GameName),
                    IsFavourite = favs.Contains(f.Path),
                    SelectMode = gallerySelect
                };
                string len;
                lock (galleryLengths) if (galleryLengths.TryGetValue(f.Path, out len)) item.LengthText = len;
                var it = item;
                item.Command = new RelayCommand(_ => GalleryTileAction(it));
                GalleryItems.Add(item);
                added.Add(item);
            }
            galleryShown = end;
            if (galleryShown < galleryList.Count)
            {
                var m = new GalleryItem { IsMore = true, MoreText = string.Format(Loc("LOC_KX_ShowMore", "Show more ({0} left)"), galleryList.Count - galleryShown) };
                m.Command = new RelayCommand(_ => ShowMoreGallery(m));
                GalleryItems.Add(m);
            }
            LoadGalleryThumbs(added, galleryGen);
        }

        private void ShowMoreGallery(GalleryItem moreTile)
        {
            int firstNew = GalleryItems.IndexOf(moreTile);
            AppendGalleryTiles(GalleryPageSize, FavSet());
            NotifyGallery();
            ui.BeginInvoke(new Action(() =>
            {
                var next = firstNew >= 0 ? GalleryItems.Skip(firstNew).FirstOrDefault(i => i.IsTile) : null;
                if (next != null) UiTools.FocusItem(GalleryGridName, next);
            }), DispatcherPriority.ContextIdle);
        }

        private string DateHeading(DateTime d)
        {
            var today = DateTime.Now.Date;
            if (d.Date == today) return Loc("LOC_KX_Today", "Today");
            if (d.Date == today.AddDays(-1)) return Loc("LOC_KX_Yesterday", "Yesterday");
            int back = ((int)today.DayOfWeek + 6) % 7;
            if (d.Date >= today.AddDays(-back)) return Loc("LOC_KX_ThisWeek", "This week");
            if (d.Date >= today.AddDays(-back - 7)) return Loc("LOC_KX_LastWeek", "Last week");
            return d.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        }

        private string SafeFullPath(string dbPath)
        {
            try { return api.Database.GetFullFilePath(dbPath); } catch (Exception) { return null; }
        }

        // ---------------- pictures ----------------

        private void LoadGalleryThumbs(List<GalleryItem> items, int gen)
        {
            foreach (var it in items)
            {
                var item = it;
                ImageSource hit;
                lock (galleryThumbs) galleryThumbs.TryGetValue(item.Path, out hit);
                if (hit != null) { item.Thumb = hit; continue; }
                string path = item.Path;
                bool video = item.IsVideo;
                ShellMedia.Run(() =>
                {
                    if (gen != galleryGen) return;
                    ImageSource img = ShellMedia.Thumbnail(path, 400);
                    if (img == null && !video) img = Images.LoadUncached(path, 400);
                    string len = null;
                    if (video)
                    {
                        int w, h;
                        TimeSpan l;
                        ShellMedia.Details(path, true, out w, out h, out l);
                        if (l > TimeSpan.Zero) len = Clock(l.TotalSeconds);
                    }
                    OnUi(() =>
                    {
                        if (img != null) lock (galleryThumbs) { if (galleryThumbs.Count > 800) galleryThumbs.Clear(); galleryThumbs[path] = img; }
                        if (len != null) lock (galleryLengths) galleryLengths[path] = len;
                        if (gen != galleryGen) return;
                        item.Thumb = img;
                        if (len != null) item.LengthText = len;
                    });
                });
            }
        }

        private void LoadGameCovers(int gen)
        {
            foreach (var t in GalleryGames.ToList())
            {
                var tile = t;
                Guid id = t.GameId;
                string cover = t.CoverFile;
                // no cover (or "Other"): the newest photo instead (picked here, on the UI thread, where the list is changed)
                var newest = galleryFiles.Where(f => f.GameId == id && !f.IsVideo).OrderByDescending(f => f.Date).FirstOrDefault();
                string fallback = newest != null ? newest.Path : null;
                KippixShared.SafeWork.Queue(_ =>
                {
                    try
                    {
                        if (gen != galleryGen) return;
                        ImageSource img = !string.IsNullOrEmpty(cover) ? Images.Load(cover, 300) : null;
                        if (img == null && fallback != null) img = Images.LoadUncached(fallback, 360);
                        OnUi(() => { if (gen == galleryGen) tile.Cover = img; });
                    }
                    catch (Exception) { }
                });
            }
        }

        // =====================================================================
        // Tiles: open, pick several, favourites
        // =====================================================================

        private void GalleryTileAction(GalleryItem item)
        {
            if (item == null || item.File == null) return;
            if (GalleryPickedProfile(item)) return;
            if (gallerySelect)
            {
                PlaySound("move");
                item.IsChecked = !item.IsChecked;
                Notify("GallerySelectedText");
                return;
            }
            int at = galleryList.IndexOf(item.File);
            if (at >= 0) OpenGalleryViewer(at);
        }

        private void SetGallerySelect(bool on)
        {
            PlaySound(on ? "select" : "back");
            gallerySelect = on;
            foreach (var i in GalleryItems) { i.SelectMode = on; if (!on) i.IsChecked = false; }
            NotifyGallery();
            if (on) ui.BeginInvoke(new Action(() => { var f = GalleryItems.FirstOrDefault(i => i.IsTile); if (f != null) UiTools.FocusItem(GalleryGridName, f); }), DispatcherPriority.ContextIdle);
            else ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_GallerySelectChip")), DispatcherPriority.ContextIdle);
        }

        private void SelectAllShown()
        {
            PlaySound("select");
            bool all = GalleryItems.Where(i => i.IsTile).All(i => i.IsChecked);
            foreach (var i in GalleryItems.Where(i => i.IsTile)) i.IsChecked = !all;
            Notify("GallerySelectedText");
        }

        private void FavouriteSelection()
        {
            var picked = GalleryItems.Where(i => i.IsTile && i.IsChecked).ToList();
            if (picked.Count == 0) return;
            bool makeFav = picked.Any(i => !i.IsFavourite);
            foreach (var i in picked) SetFavourite(i.File.Path, makeFav, false);
            SaveSettings();
            foreach (var i in picked) i.IsFavourite = makeFav;
            PlaySound("select");
            ShowGalleryStatus(makeFav ? string.Format(Loc("LOC_KX_FavAdded", "{0} added to favourites"), picked.Count)
                                      : string.Format(Loc("LOC_KX_FavRemoved", "{0} taken out of favourites"), picked.Count));
            SetGallerySelect(false);
            if (galleryView == "favs" && !makeFav) RebuildGallery(true);
        }

        private void SetFavourite(string path, bool on, bool save)
        {
            if (settings.GalleryFavourites == null) settings.GalleryFavourites = new List<string>();
            settings.GalleryFavourites.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (on) settings.GalleryFavourites.Add(path);
            if (save) SaveSettings();
        }

        private void DeleteSelection()
        {
            var picked = GalleryItems.Where(i => i.IsTile && i.IsChecked).Select(i => i.File).ToList();
            if (picked.Count == 0) return;
            DeleteGalleryFiles(picked, () => { SetGallerySelect(false); RebuildGallery(true); });
        }

        /// <summary>To the Recycle Bin (no prompt: you can get them back from there).</summary>
        private void DeleteGalleryFiles(List<GalleryFile> list, Action after, bool playerWasOpen = false)
        {
            PlaySound("back");
            var paths = list.Select(f => f.Path).ToList();
            ShellMedia.Run(() =>
            {
                if (playerWasOpen) Thread.Sleep(500);   // the video player lets go of the file a moment after it's closed
                int skipped;
                bool ok = ShellMedia.Recycle(paths, out skipped);
                if (!ok && playerWasOpen) { Thread.Sleep(800); ok = ShellMedia.Recycle(paths.Where(File.Exists).ToList(), out skipped); }
                OnUi(() =>
                {
                    var gone = new HashSet<string>(paths.Where(p => !File.Exists(p)), StringComparer.OrdinalIgnoreCase);
                    // keep the grid as it is on screen in step (paging counts, the tiles themselves)
                    foreach (var f in galleryList.Where(f => gone.Contains(f.Path)).ToList())
                        if (galleryList.IndexOf(f) < galleryShown) galleryShown--;
                    galleryFiles.RemoveAll(f => gone.Contains(f.Path));
                    galleryList.RemoveAll(f => gone.Contains(f.Path));
                    foreach (var it in GalleryItems.Where(i => i.File != null && gone.Contains(i.File.Path)).ToList()) GalleryItems.Remove(it);
                    var more = GalleryItems.LastOrDefault(i => i.IsMore);
                    if (more != null)
                    {
                        int at = GalleryItems.IndexOf(more);
                        var m2 = new GalleryItem { IsMore = true, MoreText = string.Format(Loc("LOC_KX_ShowMore", "Show more ({0} left)"), galleryList.Count - galleryShown) };
                        m2.Command = new RelayCommand(_ => ShowMoreGallery(m2));
                        if (galleryShown >= galleryList.Count) GalleryItems.RemoveAt(at); else GalleryItems[at] = m2;
                    }
                    if (settings.GalleryFavourites != null && settings.GalleryFavourites.RemoveAll(p => gone.Contains(p)) > 0) SaveSettings();
                    if (gone.Count > 0) galleryChanged = true;
                    string msg = gone.Count > 0 ? string.Format(gone.Count == 1 ? Loc("LOC_KX_DeletedOne", "Moved to the Recycle Bin") : Loc("LOC_KX_DeletedN", "{0} moved to the Recycle Bin"), gone.Count) : "";
                    if (skipped > 0) msg = (msg.Length > 0 ? msg + ". " : "") + string.Format(Loc("LOC_KX_DeleteNoBin", "{0} left alone: that drive has no Recycle Bin."), skipped);
                    else if (!ok) msg = (msg.Length > 0 ? msg + ". " : "") + Loc("LOC_KX_DeleteFailed", "Some couldn't be moved to the Recycle Bin (in use, or no permission).");
                    ShowGalleryStatus(msg);
                    if (openPanel != "gallery") return;   // closed meanwhile: nothing to update on screen
                    if (after != null) after();
                });
            });
        }

        private void ShowGalleryStatus(string text)
        {
            galleryStatus = text ?? "";
            Notify("GalleryStatus");
            Notify("HasGalleryStatus");
            Notify("GalleryViewerStatus");
            Notify("HasGalleryViewerStatus");
            if (galleryStatusTimer == null)
            {
                galleryStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                galleryStatusTimer.Tick += (s, e) =>
                {
                    galleryStatusTimer.Stop();
                    galleryStatus = "";
                    Notify("GalleryStatus");
                    Notify("HasGalleryStatus");
                    Notify("GalleryViewerStatus");
                    Notify("HasGalleryViewerStatus");
                };
            }
            galleryStatusTimer.Stop();
            galleryStatusTimer.Start();
        }

        // =====================================================================
        // Storage and clean-up
        // =====================================================================

        public string GalleryTotalText { get; private set; }
        public string GalleryCleanKindText
        {
            get { return settings.GalleryCleanKind == 0 ? Loc("LOC_KX_PhotosAndVideos", "Photos and videos") : settings.GalleryCleanKind == 2 ? Loc("LOC_KX_PhotosOnly", "Photos") : Loc("LOC_KX_VideosOnly", "Videos"); }
        }
        public string GalleryCleanDaysText { get { return string.Format(Loc("LOC_KX_OlderThanDays", "Older than {0} days"), settings.GalleryCleanDays); } }
        public string GalleryCleanPreview { get; private set; }
        public bool GalleryCanClean { get { return CleanCandidates().Count > 0; } }
        public ICommand GalleryCleanKindCommand { get; private set; }
        public ICommand GalleryCleanDaysCommand { get; private set; }
        public ICommand GalleryCleanCommand { get; private set; }

        private static readonly int[] CleanDays = { 7, 14, 30, 60, 90, 180, 365 };
        private static int NextCleanDays(int now)
        {
            int i = Array.IndexOf(CleanDays, now);
            return CleanDays[(i + 1) % CleanDays.Length];
        }

        private List<GalleryFile> CleanCandidates()
        {
            var cutoff = DateTime.Now.AddDays(-settings.GalleryCleanDays);
            var favs = FavSet();
            return galleryFiles.Where(f => f.Date < cutoff && !favs.Contains(f.Path)
                                           && (settings.GalleryCleanKind == 0 || (settings.GalleryCleanKind == 1) == f.IsVideo)).ToList();
        }

        private void NotifyGalleryClean()
        {
            var c = CleanCandidates();
            GalleryCleanPreview = c.Count == 0 ? Loc("LOC_KX_CleanNothing", "Nothing that old (favourites are always kept).")
                                               : string.Format(Loc("LOC_KX_CleanWould", "{0}  ·  {1}  (favourites are kept)"), CountText(c), KippixOverlay.Capture.DriveSize(c.Sum(f => f.Size)));
            foreach (string n in new[] { "GalleryCleanKindText", "GalleryCleanDaysText", "GalleryCleanPreview", "GalleryCanClean", "GalleryTotalText" }) Notify(n);
        }

        private void OpenGalleryStorage()
        {
            PlaySound("select");
            galleryPage = "storage";
            gallerySelect = false;
            GalleryStorage.Clear();
            long total = galleryFiles.Sum(f => f.Size);
            var groups = galleryFiles.GroupBy(f => f.GameId).Select(g => new { Id = g.Key, List = g.ToList(), Size = g.Sum(f => f.Size) }).OrderByDescending(g => g.Size).ToList();
            long biggest = groups.Count > 0 ? Math.Max(1, groups[0].Size) : 1;
            foreach (var g in groups)
            {
                Guid id = g.Id;
                string name = id == Guid.Empty ? Loc("LOC_KX_OtherCaptures", "Other") : g.List[0].GameName;
                var t = new GalleryGameTile { GameId = id, Name = name, CountText = CountText(g.List), SizeText = KippixOverlay.Capture.DriveSize(g.Size), BarWidth = Math.Max(6, 520.0 * g.Size / biggest) };
                string n2 = name;
                t.Command = new RelayCommand(_ => { galleryView = "games"; OpenGalleryGame(id, n2); });
                GalleryStorage.Add(t);
            }
            GalleryTotalText = string.Format(Loc("LOC_KX_StorageTotal", "{0} in all  ·  {1}"), KippixOverlay.Capture.DriveSize(total), CountText(galleryFiles));
            NotifyGallery();
            NotifyGalleryClean();
            ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_GalleryCleanKind")), DispatcherPriority.ContextIdle);
        }

        private void RunGalleryClean()
        {
            var c = CleanCandidates();
            if (c.Count == 0) return;
            DeleteGalleryFiles(c, () => { OpenGalleryStorage(); });
        }
    }
}

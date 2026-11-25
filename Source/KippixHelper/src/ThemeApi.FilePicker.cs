using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>One row on a list page in the options (file picker, layout list, gallery folders, the game's More menu...).</summary>
    public class PickRow : Observable
    {
        public string Text { get; set; }
        public string Sub { get; set; }
        public bool HasSub { get { return !string.IsNullOrEmpty(Sub); } }
        /// <summary>Which icon: folder, file, drive, up, use, place, layout, upload, remove, add, play, link, trash, info, star, check.</summary>
        public string Icon { get; set; }
        /// <summary>A small heading between groups of rows (not focusable).</summary>
        public bool IsHeader { get; set; }
        /// <summary>A row to show: a heading or a note whose text was taken out (an empty text in the language file) isn't shown at all.</summary>
        public bool IsRow { get { return !IsHeader && !(IsInfo && string.IsNullOrEmpty(Text)); } }
        public bool IsHeaderShown { get { return IsHeader && !string.IsNullOrEmpty(Text); } }
        private bool isSelected;
        public bool IsSelected { get { return isSelected; } set { if (Set(ref isSelected, value, "IsSelected")) { Notify("IsSwitchOn"); Notify("IsSwitchOff"); Notify("IsPickedTick"); } } }
        /// <summary>(0.99.9) Getting started's tiles: the badge in the corner (a tick when picked or switched on, a ring when switched off).</summary>
        public bool IsSwitchOn { get { return IsSwitch && isSelected; } }
        public bool IsSwitchOff { get { return IsSwitch && !isSelected; } }
        public bool IsPickedTick { get { return isSelected; } }
        private bool isDimmed;
        /// <summary>(0.99.13) Getting started's look boxes: another one in its column is picked.</summary>
        public bool IsDimmed { get { return isDimmed; } set { Set(ref isDimmed, value, "IsDimmed"); } }
        /// <summary>(0.99.9) The icon as a shape (Getting started's tiles without a picture draw it big).</summary>
        public System.Windows.Media.Geometry Glyph { get; set; }
        /// <summary>Information only (greyed, does nothing).</summary>
        public bool IsInfo { get; set; }
        /// <summary>An on / off switch: drawn with the switch track (IsSelected is on), like the switches on the XAML pages.</summary>
        public bool IsSwitch { get; set; }
        public bool IsNotSwitch { get { return !IsSwitch; } }
        public ICommand Command { get; set; }
        public string Key { get; set; }
        /// <summary>(0.97) Pushed in from the left (a row opened under another, like a dropdown).</summary>
        public double Indent { get; set; }
        public System.Windows.Thickness IndentMargin { get { return new System.Windows.Thickness(Indent, 3, 0, 3); } }
        /// <summary>A picture in place of the line icon (the media services' own icons).</summary>
        private System.Windows.Media.ImageSource picture;
        public System.Windows.Media.ImageSource Picture { get { return picture; } set { if (Set(ref picture, value, "Picture")) { Notify("HasPicture"); Notify("HasNoPicture"); } } }
        private System.Windows.Media.Brush tileBrush;
        public System.Windows.Media.Brush TileBrush { get { return tileBrush; } set { Set(ref tileBrush, value, "TileBrush"); } }   // (1.1.0) a Media tile's background
        public bool HasPicture { get { return picture != null; } }
        public bool HasNoPicture { get { return picture == null; } }

        /// <summary>Changes the line under the text (shown at once).</summary>
        public void SetSub(string sub) { Sub = sub; Notify("Sub"); Notify("HasSub"); }

        /// <summary>(1.0.6) An eye at the end of the row (Media's edit mode): 0 none, 1 shown (open eye), 2 hidden (crossed out).</summary>
        private int eye;
        public int Eye { get { return eye; } set { if (Set(ref eye, value, "Eye")) { Notify("EyeOpenShown"); Notify("EyeOffShown"); Notify("RowOpacity"); } } }
        public bool EyeOpenShown { get { return eye == 1; } }
        public bool EyeOffShown { get { return eye == 2; } }
        /// <summary>(1.0.6) A hidden row in Media's edit mode shows faded (its icon and name at 40%).</summary>
        public double RowOpacity { get { return eye == 2 ? 0.4 : 1.0; } }
        public void SetText(string text) { Text = text; Notify("Text"); Notify("IsRow"); Notify("IsHeaderShown"); }

        /// <summary>A copy (with no one listening to it yet).</summary>
        public PickRow Clone()
        {
            return new PickRow { Text = Text, Sub = Sub, Icon = Icon, IsHeader = IsHeader, IsInfo = IsInfo, IsSwitch = IsSwitch, Command = Command, Key = Key, Indent = Indent, isSelected = isSelected, picture = picture, eye = eye };
        }

        /// <summary>
        /// Turns this row into <paramref name="o"/> where it stands (same row on screen, so a highlight on it stays put).
        /// Used to change a whole menu without ever removing the highlighted row.
        /// </summary>
        public void BecomeLike(PickRow o)
        {
            Text = o.Text; Sub = o.Sub; Icon = o.Icon; IsHeader = o.IsHeader; IsInfo = o.IsInfo; IsSwitch = o.IsSwitch; Command = o.Command; Key = o.Key; Indent = o.Indent;
            isSelected = o.isSelected; picture = o.picture; Glyph = o.Glyph; eye = o.eye;
            Notify(string.Empty);   // every binding on this row reads again
        }
    }

    /// <summary>
    /// A file / folder picker made for the controller (Options panel page "picker"): places and drives first, then folders.
    /// A opens a folder (or picks a file), B goes up a folder, and at the top B goes back to the page that opened it.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<PickRow> PickerRows { get; private set; }
        public bool IsOptionsPicker { get { return optionsPage == "picker"; } }
        public string PickerTitle { get; private set; }
        public string PickerPathText { get { return string.IsNullOrEmpty(pickerPath) ? Loc("LOC_KX_PickerTop", "Choose a place to start") : pickerPath; } }

        private string pickerPath = "";
        private bool pickerFolders;               // picking a folder (else a file)
        private string[] pickerExts = new string[0];
        private string pickerReturn = "";         // the page that opened the picker
        private Action<string> filePickDone;
        private readonly Stack<string> pickerCameFrom = new Stack<string>();   // to put the highlight back on the folder you left

        private void InitPicker()
        {
            if (PickerRows == null) PickerRows = new ObservableCollection<PickRow>();
        }

        /// <summary>Pick a file with one of these extensions (".json"). Starts in <paramref name="start"/> ("" = the places list).</summary>
        private void OpenPicker(string title, bool folders, string[] exts, string start, string returnPage, Action<string> done)
        {
            OpenPicker(title, folders, exts, start, returnPage, done, null);
        }

        private Action pickerOnBack;   // opened from outside the settings (Welcome, the profile menu): B goes back there

        private void OpenPicker(string title, bool folders, string[] exts, string start, string returnPage, Action<string> done, Action onBack)
        {
            InitPicker();
            pickerOnBack = onBack;
            PickerTitle = title;
            pickerFolders = folders;
            pickerExts = exts ?? new string[0];
            pickerReturn = returnPage ?? "";
            filePickDone = done;
            pickerCameFrom.Clear();
            Notify("PickerTitle");
            pickerPath = !string.IsNullOrEmpty(start) && Directory.Exists(start) ? start : "";
            ListPicker(null);
            SetOptionsPage("picker");
        }

        private void OpenPickerFolder(string title, string returnPage, Action<string> done)
        {
            OpenPicker(title, true, null, "", returnPage, done);
        }

        private void ListPicker(string focusPath)
        {
            PickerRows.Clear();
            try
            {
                if (string.IsNullOrEmpty(pickerPath)) ListPickerPlaces();
                else ListPickerFolder();
            }
            catch (Exception ex)
            {
                PickerRows.Add(new PickRow { Text = Loc("LOC_KX_PickerCantOpen", "This folder can't be opened."), Sub = ex.Message, Icon = "info", IsInfo = true });
            }
            Notify("PickerPathText");
            if (ui != null)
                ui.BeginInvoke(new Action(() =>
                {
                    var target = focusPath != null ? PickerRows.FirstOrDefault(r => string.Equals(r.Key, focusPath, StringComparison.OrdinalIgnoreCase)) : null;
                    if (target != null) UiTools.FocusItem("KX_PickerList", target);
                    else UiTools.FocusInside("KX_OptionsPanel");
                }), DispatcherPriority.ContextIdle);
        }

        private void ListPickerPlaces()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var places = new List<Tuple<string, string>>
            {
                Tuple.Create(Loc("LOC_KX_PlaceDesktop", "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                Tuple.Create(Loc("LOC_KX_PlaceDocuments", "Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                Tuple.Create(Loc("LOC_KX_PlaceDownloads", "Downloads"), Path.Combine(profile, "Downloads")),
                Tuple.Create(Loc("LOC_KX_PlacePictures", "Pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                Tuple.Create(Loc("LOC_KX_PlaceVideos", "Videos"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
            };
            PickerRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_PickerPlaces", "PLACES") });
            foreach (var p in places)
            {
                if (string.IsNullOrEmpty(p.Item2) || !Directory.Exists(p.Item2)) continue;
                string path = p.Item2;
                PickerRows.Add(new PickRow { Text = p.Item1, Sub = path, Icon = "place", Key = path, Command = new RelayCommand(_ => PickerOpen(path)) });
            }
            PickerRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_PickerDrives", "DRIVES") });
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string root = d.RootDirectory.FullName;
                    string label = string.IsNullOrEmpty(d.VolumeLabel) ? root : d.VolumeLabel + " (" + root.TrimEnd('\\') + ")";
                    string free = KippixOverlay.Capture.DriveSize(d.AvailableFreeSpace) + " " + Loc("LOC_KX_Free", "free");
                    PickerRows.Add(new PickRow { Text = label, Sub = free, Icon = "drive", Key = root, Command = new RelayCommand(_ => PickerOpen(root)) });
                }
                catch (Exception) { }
            }
        }

        private void ListPickerFolder()
        {
            if (pickerFolders)
            {
                string here = pickerPath;
                PickerRows.Add(new PickRow
                {
                    Text = Loc("LOC_KX_PickerUseFolder", "Use this folder"), Sub = here, Icon = "use",
                    Command = new RelayCommand(_ => PickerChoose(here))
                });
            }
            string parent = null;
            try { var p = Directory.GetParent(pickerPath); parent = p != null ? p.FullName : ""; } catch (Exception) { parent = ""; }
            string up = parent;
            PickerRows.Add(new PickRow { Text = Loc("LOC_KX_PickerUp", "Up a folder"), Sub = string.IsNullOrEmpty(up) ? Loc("LOC_KX_PickerTopShort", "Places and drives") : up, Icon = "up",
                                         Command = new RelayCommand(_ => PickerUp()) });

            var dirs = new DirectoryInfo(pickerPath).GetDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Take(500).ToList();
            List<FileInfo> files = new List<FileInfo>();
            if (!pickerFolders)
                files = new DirectoryInfo(pickerPath).GetFiles()
                    .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && (pickerExts.Length == 0 || pickerExts.Contains(f.Extension.ToLowerInvariant())))
                    .OrderByDescending(f => f.LastWriteTime).Take(300).ToList();
            if (files.Count > 0)
            {
                PickerRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_PickerFiles", "FILES") });
                foreach (var f in files)
                {
                    string path = f.FullName;
                    PickerRows.Add(new PickRow { Text = f.Name, Sub = f.LastWriteTime.ToString("d MMM yyyy, HH:mm"), Icon = "file", Key = path, Command = new RelayCommand(_ => PickerChoose(path)) });
                }
            }
            if (dirs.Count > 0)
            {
                PickerRows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_PickerFolders", "FOLDERS") });
                foreach (var d in dirs)
                {
                    string path = d.FullName;
                    PickerRows.Add(new PickRow { Text = d.Name, Icon = "folder", Key = path, Command = new RelayCommand(_ => PickerOpen(path)) });
                }
            }
            if (dirs.Count == 0 && files.Count == 0)
                PickerRows.Add(new PickRow { Text = pickerFolders ? Loc("LOC_KX_PickerNoFolders", "No folders in here.") : Loc("LOC_KX_PickerNoFiles", "No layout files in here."), Icon = "info", IsInfo = true });
        }

        private void PickerOpen(string path)
        {
            PlaySound("select");
            pickerCameFrom.Push(pickerPath);
            pickerPath = path;
            ListPicker(null);
        }

        /// <summary>Up a folder (B too). False at the top, where B goes back to the page that opened the picker.</summary>
        private bool PickerUp()
        {
            if (string.IsNullOrEmpty(pickerPath)) return false;
            string left = pickerPath;
            string parent = "";
            try { var p = Directory.GetParent(pickerPath); parent = p != null ? p.FullName : ""; } catch (Exception) { }
            pickerPath = parent;
            ListPicker(left);
            return true;
        }

        private void PickerChoose(string path)
        {
            PlaySound("select");
            var done = filePickDone;
            filePickDone = null;
            pickerOnBack = null;
            if (done != null) done(path);
        }

        /// <summary>B on the picker: up a folder, then back to where it was opened from.</summary>
        private bool PickerBack()
        {
            if (optionsPage != "picker") return false;
            if (PickerUp()) return true;
            filePickDone = null;
            var away = pickerOnBack;
            pickerOnBack = null;
            if (away != null) { PlaySound("back"); away(); return true; }
            GoBackTo(pickerReturn, PickerReturnRow());
            return true;
        }

        private string PickerReturnRow()
        {
            if (pickerReturn == "layouts") return PickerTitle == Loc("LOC_KX_UploadLayout", "Upload layout") ? "upload" : "layouts";
            if (pickerReturn == "galleryfolders") return "addfolder";
            if (pickerReturn == "folder") return "fphoto";   // Folder: a folder's photo
            return pickerReturn;
        }
    }
}

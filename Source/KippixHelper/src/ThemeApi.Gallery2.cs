using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.7) The gallery's Console design (Settings › Layout changes WIP › Gallery, 2 of 2), the user's mock-up: the highlighted
    /// capture big on the left with its game, when it was taken and its size, and View / Favourite / Edit / More under it; the
    /// captures down the right grouped by day (a dot on favourites, the length on clips). Across the top: All games (pick one
    /// game), the tabs All / Screenshots / Clips / Favourites (LB / RB), and how many there are with the order (A swaps it).
    /// </summary>
    public partial class ThemeApi
    {
        private bool Gallery2 { get { return LabLook("gallery") == 1; } }
        /// <summary>The Console design's page is showing (the storage, move-to and settings pages stay as they are).</summary>
        public bool Gallery2Grid { get { return Gallery2 && galleryPage == "grid"; } }
        private string GalleryGridName { get { return Gallery2Grid ? "KX_Gallery2Grid" : "KX_GalleryGrid"; } }

        private string g2Tab = "all";          // all, shots, clips, favs
        private Guid? g2Game;                  // null: every game
        private string g2GameName = "";
        private bool g2GameMenu;
        private GalleryItem g2Item;
        private bool g2Hooked;
        // the big pictures, read ahead (the ones next to the highlight) and kept, so a capture shows sharp straight away
        private const int G2Keep = 16;
        private readonly Dictionary<string, ImageSource> g2Big = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> g2BigOrder = new List<string>();
        private readonly Dictionary<string, string> g2Sizes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> g2Loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<PickRow> Gallery2Tabs { get; private set; }
        public ObservableCollection<PickRow> Gallery2GameRows { get; private set; }
        public ICommand Gallery2GameCommand { get; private set; }
        public ICommand Gallery2SortCommand { get; private set; }
        public ICommand Gallery2ViewCommand { get; private set; }
        public ICommand Gallery2FavCommand { get; private set; }
        public ICommand Gallery2EditCommand { get; private set; }
        public ICommand Gallery2MoreCommand { get; private set; }

        public bool Gallery2GameMenuOpen { get { return g2GameMenu; } }
        public string Gallery2GameText { get { return g2Game.HasValue ? g2GameName : Loc("LOC_KX_G2AllGames", "All games"); } }
        public string Gallery2CountText
        {
            get
            {
                int n = galleryList != null ? galleryList.Count : 0;
                string count = n == 1 ? Loc("LOC_KX_G2OneCapture", "1 capture") : string.Format(Loc("LOC_KX_G2Captures", "{0} captures"), n);
                return count + " \u00B7 " + GallerySortText;
            }
        }
        private ImageSource g2Image;
        public ImageSource Gallery2Image { get { return g2Image; } }
        public bool Gallery2HasItem { get { return g2Item != null && g2Item.File != null; } }
        public string Gallery2Kind { get { return !Gallery2HasItem ? "" : (g2Item.IsVideo ? Loc("LOC_KX_G2Clip", "Clip") : Loc("LOC_KX_G2Screenshot", "Screenshot")).ToUpper(CultureInfo.CurrentCulture); } }
        public string Gallery2Name { get { return !Gallery2HasItem ? "" : (g2Item.File.GameId == Guid.Empty ? Loc("LOC_KX_OtherCaptures", "Other") : g2Item.File.GameName); } }
        private string g2Info = "";
        public string Gallery2Info { get { return g2Info; } }
        public string Gallery2FavText { get { return Gallery2HasItem && g2Item.IsFavourite ? Loc("LOC_KX_G2Unfavourite", "Unfavourite") : Loc("LOC_KX_G2Favourite", "Favourite"); } }

        private void InitGallery2()
        {
            Gallery2Tabs = new ObservableCollection<PickRow>();
            Gallery2GameRows = new ObservableCollection<PickRow>();
            foreach (var t in new[] { Tuple.Create("all", Loc("LOC_KX_All", "All")), Tuple.Create("shots", Loc("LOC_KX_G2Screenshots", "Screenshots")),
                                      Tuple.Create("clips", Loc("LOC_KX_G2Clips", "Clips")), Tuple.Create("favs", Loc("LOC_KX_G2Favourites", "Favourites")) })
            {
                string key = t.Item1;
                Gallery2Tabs.Add(new PickRow { Key = key, Text = t.Item2, IsSelected = key == g2Tab, Command = new RelayCommand(_ => SetG2Tab(key, true)) });
            }
            Gallery2GameCommand = new RelayCommand(_ => ToggleG2GameMenu());
            Gallery2SortCommand = new RelayCommand(_ => KeepFocusWhile(() => { settings.GallerySort = settings.GallerySort == 1 ? 0 : 1; SaveSettings(); PlaySound("select"); RebuildGallery(false); }));
            Gallery2ViewCommand = new RelayCommand(_ => { if (Gallery2HasItem) GalleryTileAction(g2Item); });
            Gallery2FavCommand = new RelayCommand(_ => G2Favourite());
            Gallery2EditCommand = new RelayCommand(_ => G2OpenViewerThen(true));
            Gallery2MoreCommand = new RelayCommand(_ => G2OpenViewerThen(false));
        }

        private void NotifyGallery2()
        {
            foreach (string n in new[] { "Gallery2Grid", "Gallery2GameMenuOpen", "Gallery2GameText", "Gallery2CountText", "Gallery2Image", "Gallery2HasItem", "Gallery2Kind", "Gallery2Name", "Gallery2Info", "Gallery2FavText" })
                Notify(n);
        }

        /// <summary>The highlight follows the captures: the one highlighted shows big on the left.</summary>
        private void HookGallery2()
        {
            if (g2Hooked) return;
            g2Hooked = true;
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                try
                {
                    if (openPanel != "gallery" || !Gallery2Grid || galleryViewerOpen) return;
                    var fe = e.NewFocus as FrameworkElement;
                    var it = fe != null ? fe.DataContext as GalleryItem : null;
                    if (it != null && it.IsTile && it.File != null && it != g2Item) SetG2Item(it);
                }
                catch (Exception) { }
            }), true);
        }

        /// <summary>Opening the gallery in this design: always the captures page (a game page opens it on that game).</summary>
        private void Gallery2Opening()
        {
            if (!Gallery2) return;
            HookGallery2();
            if (galleryPage == "game") { g2Game = galleryGameId; g2GameName = galleryGameName ?? ""; }
            if (galleryPage == "games" || galleryPage == "game") galleryPage = "grid";
            g2GameMenu = false;
        }

        /// <summary>What this design shows: the game picked, the tab, newest or oldest first (the old filters don't apply).</summary>
        private List<GalleryFile> Gallery2List(HashSet<string> favs)
        {
            IEnumerable<GalleryFile> src = galleryFiles;
            if (g2Game.HasValue) src = src.Where(f => f.GameId == g2Game.Value);
            if (g2Tab == "shots") src = src.Where(f => !f.IsVideo);
            else if (g2Tab == "clips") src = src.Where(f => f.IsVideo);
            else if (g2Tab == "favs") src = src.Where(f => favs.Contains(f.Path));
            return (settings.GallerySort == 1 ? src.OrderBy(f => f.Date) : src.OrderByDescending(f => f.Date)).ToList();
        }

        /// <summary>The day headings: Today, Yesterday, then the date (12 NOV; the year too when it isn't this year).</summary>
        private string Gallery2Heading(DateTime d)
        {
            var today = DateTime.Now.Date;
            string h = d.Date == today ? Loc("LOC_KX_Today", "Today")
                     : d.Date == today.AddDays(-1) ? Loc("LOC_KX_Yesterday", "Yesterday")
                     : d.ToString(d.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
            return h.ToUpper(CultureInfo.CurrentCulture);
        }

        /// <summary>After the captures are (re)built: the first one shows until the highlight lands somewhere.</summary>
        private void Gallery2Built()
        {
            if (!Gallery2Grid) return;
            var keep = g2Item != null ? GalleryItems.FirstOrDefault(i => i.IsTile && g2Item.File != null && i.File == g2Item.File) : null;
            SetG2Item(keep ?? GalleryItems.FirstOrDefault(i => i.IsTile));
            // the first few read ahead too (where the highlight starts after changing tab or game)
            foreach (var f in galleryList.Take(4)) G2Want(f);
            NotifyGallery2();
        }

        private void SetG2Item(GalleryItem it)
        {
            g2Item = it;
            ImageSource big = null;
            string size = null;
            if (it != null && it.File != null) { g2Big.TryGetValue(it.Path, out big); g2Sizes.TryGetValue(it.Path, out size); }
            // the full picture when it's been read already (it usually has: the ones around the highlight are read ahead).
            // Not yet: the picture showing stays until the sharp one is ready (never the blurry small one), unless that takes
            // more than half a second (then the small one, so it isn't the wrong capture for long)
            if (big != null || it == null) g2Image = big;
            else G2FallbackSoon();
            g2Info = it != null && it.File != null ? Gallery2When(it.File.Date) + (size != null ? " · " + size : "") : "";
            NotifyGallery2();
            if (it == null || it.File == null) return;
            G2Want(it.File);
            // and the ones either side, ready for the next move
            int at = galleryList != null ? galleryList.IndexOf(it.File) : -1;
            if (at >= 0)
                foreach (int d in new[] { 1, -1, 2, -2, 3, -3 })
                    if (at + d >= 0 && at + d < galleryList.Count) G2Want(galleryList[at + d]);
        }

        /// <summary>Reads a capture's full picture and its size in the background (once), and shows it if it's the highlighted one.</summary>
        private void G2Want(GalleryFile f)
        {
            if (f == null) return;
            string path = f.Path;
            bool video = f.IsVideo;
            if (g2Big.ContainsKey(path) || g2Loading.Contains(path)) return;
            g2Loading.Add(path);
            Action job = () =>
            {
                ImageSource img = null;
                int w = 0, h = 0;
                try
                {
                    img = video ? ShellMedia.Thumbnail(path, 1600) : Images.LoadUncached(path, 1600);
                    if (img == null && !video) img = ShellMedia.Thumbnail(path, 1600);
                    if (video) { TimeSpan len; ShellMedia.Details(path, true, out w, out h, out len); }
                    else
                    {
                        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                        if (dec.Frames.Count > 0) { w = dec.Frames[0].PixelWidth; h = dec.Frames[0].PixelHeight; }
                    }
                }
                catch (Exception) { }
                OnUi(() =>
                {
                    g2Loading.Remove(path);
                    if (w > 0 && h > 0) g2Sizes[path] = w + " × " + h;
                    if (img == null) return;
                    g2Big[path] = img;
                    g2BigOrder.Remove(path);
                    g2BigOrder.Add(path);
                    while (g2BigOrder.Count > G2Keep)
                    {
                        string old = g2BigOrder[0];
                        g2BigOrder.RemoveAt(0);
                        if (g2Item == null || !string.Equals(g2Item.Path, old, StringComparison.OrdinalIgnoreCase)) g2Big.Remove(old);
                    }
                    var cur = g2Item;
                    if (cur != null && cur.File != null && string.Equals(cur.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        g2Image = img;
                        g2Info = Gallery2When(cur.File.Date) + (w > 0 && h > 0 ? " · " + w + " × " + h : "");
                        Notify("Gallery2Image");
                        Notify("Gallery2Info");
                    }
                });
            };
            if (video) ShellMedia.Run(job);   // (the shell's video frames: its own thread)
            else KippixShared.SafeWork.Queue(_ => job());
        }

        private DispatcherTimer g2Fallback;

        private void G2FallbackSoon()
        {
            if (g2Fallback == null)
            {
                g2Fallback = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
                g2Fallback.Tick += (s, e) =>
                {
                    g2Fallback.Stop();
                    var cur = g2Item;
                    if (cur == null || cur.File == null || g2Big.ContainsKey(cur.Path) || cur.Thumb == null) return;
                    g2Image = cur.Thumb;
                    Notify("Gallery2Image");
                };
            }
            g2Fallback.Stop();
            g2Fallback.Start();
        }

        /// <summary>The gallery closing: the big pictures go.</summary>
        private void ForgetGallery2Pictures()
        {
            g2Big.Clear();
            g2BigOrder.Clear();
            g2Loading.Clear();
            g2Image = null;
            g2Item = null;
        }

        /// <summary>"Today, 20:58" / "Yesterday, 09:12" / "12 Nov, 18:40".</summary>
        private string Gallery2When(DateTime d)
        {
            var today = DateTime.Now.Date;
            string day = d.Date == today ? Loc("LOC_KX_Today", "Today")
                       : d.Date == today.AddDays(-1) ? Loc("LOC_KX_Yesterday", "Yesterday")
                       : d.ToString(d.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
            if (day.Length > 0) day = char.ToUpper(day[0], CultureInfo.CurrentCulture) + day.Substring(1);
            return day + ", " + d.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        // ---------------- the tabs and the game ----------------

        private void SetG2Tab(string key, bool focusTab)
        {
            if (key == g2Tab) { PlaySound("move"); return; }
            g2Tab = key;
            foreach (var t in Gallery2Tabs) t.IsSelected = t.Key == key;
            PlaySound("select");
            g2Item = null;
            RebuildGallery(!focusTab);
            if (focusTab)
            {
                var row = Gallery2Tabs.FirstOrDefault(t => t.Key == key);
                ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_Gallery2Tabs", row)), DispatcherPriority.ContextIdle);
            }
        }

        private DateTime g2TabStepAt = DateTime.MinValue;

        /// <summary>LB / RB: the tab before / after (the highlight goes to the first capture).</summary>
        private void StepG2Tab(int dir)
        {
            // a press that arrives twice over (a trigger as the trigger and as Page Up / Down) moves once
            if ((DateTime.UtcNow - g2TabStepAt).TotalMilliseconds < 220) return;
            g2TabStepAt = DateTime.UtcNow;
            var keys = Gallery2Tabs.Select(t => t.Key).ToList();
            int i = Math.Max(0, keys.IndexOf(g2Tab));
            SetG2Tab(keys[(i + dir + keys.Count) % keys.Count], false);
        }

        private void ToggleG2GameMenu()
        {
            g2GameMenu = !g2GameMenu;
            PlaySound(g2GameMenu ? "select" : "back");
            if (g2GameMenu)
            {
                Gallery2GameRows.Clear();
                Gallery2GameRows.Add(new PickRow { Key = "", Text = Loc("LOC_KX_G2AllGames", "All games"), IsSelected = !g2Game.HasValue, Command = new RelayCommand(_ => PickG2Game(null, "")) });
                foreach (var g in galleryFiles.GroupBy(f => f.GameId).OrderBy(g => g.Key == Guid.Empty ? 1 : 0).ThenByDescending(g => g.Max(f => f.Date)))
                {
                    Guid id = g.Key;
                    string name = id == Guid.Empty ? Loc("LOC_KX_OtherCaptures", "Other") : g.First().GameName;
                    Gallery2GameRows.Add(new PickRow { Key = id.ToString(), Text = name, Sub = CountText(g.ToList()), IsSelected = g2Game.HasValue && g2Game.Value == id, Command = new RelayCommand(_ => PickG2Game(id, name)) });
                }
            }
            Notify("Gallery2GameMenuOpen");
            var sel = Gallery2GameRows.FirstOrDefault(r => r.IsSelected) ?? Gallery2GameRows.FirstOrDefault();
            if (g2GameMenu) ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_Gallery2GameList", sel)), DispatcherPriority.ContextIdle);
            else ui.BeginInvoke(new Action(() => UiTools.FocusByName("KX_Gallery2GameChip")), DispatcherPriority.ContextIdle);
        }

        private void PickG2Game(Guid? id, string name)
        {
            g2Game = id;
            g2GameName = name ?? "";
            g2GameMenu = false;
            g2Item = null;
            PlaySound("select");
            NotifyGallery2();
            RebuildGallery(true);
        }

        /// <summary>B: the game list closes first.</summary>
        private bool Gallery2Back()
        {
            if (!Gallery2Grid || !g2GameMenu) return false;
            ToggleG2GameMenu();
            return true;
        }

        // ---------------- the buttons ----------------

        private void G2Favourite()
        {
            var it = g2Item;
            if (it == null || it.File == null) return;
            bool on = !it.IsFavourite;
            SetFavourite(it.Path, on, true);
            it.IsFavourite = on;
            PlaySound("select");
            ShowGalleryStatus(on ? Loc("LOC_KX_FavOn", "Added to favourites") : Loc("LOC_KX_FavOff", "Taken out of favourites"));
            Notify("Gallery2FavText");
        }

        /// <summary>Edit and More: the capture opens in the viewer with its Edit, or its More menu, straight away.</summary>
        private void G2OpenViewerThen(bool edit)
        {
            var it = g2Item;
            if (it == null || it.File == null || galleryList == null) return;
            int at = galleryList.IndexOf(it.File);
            if (at < 0) return;
            OpenGalleryViewer(at);
            ui.BeginInvoke(new Action(() =>
            {
                if (!galleryViewerOpen) return;
                if (edit) OpenGalleryEditor(); else SetGalleryMore(true);
            }), DispatcherPriority.ContextIdle);
        }

        /// <summary>The controller on this page: LB / RB the tabs, X favourite, Y edit, Start (≡) more.</summary>
        private bool Gallery2Key(string btn, bool pressed, int step, bool xPressed, bool yPressed)
        {
            if (!Gallery2Grid || galleryViewerOpen) return false;
            if (g2GameMenu) return false;
            if (step != 0) { StepG2Tab(step); return true; }
            if (xPressed) { G2Favourite(); return true; }
            if (yPressed) { G2OpenViewerThen(true); return true; }
            if (btn == "Start" && pressed) { G2OpenViewerThen(false); return true; }
            return false;
        }
    }
}

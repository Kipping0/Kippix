using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.54) The gallery: hold A on a photo or video to start picking several (it's picked first), and "Move to a game"
    /// for the picked ones. Moving: the gallery's wall of games (the ones with captures and the ones played lately, "Other"
    /// last) and A on one. Each file then belongs to that game: a screenshot in the overlay's folder is renamed to start with
    /// the game's name (that folder is one flat folder, the name says the game); a clip in a game folder under Videos (NVIDIA's)
    /// goes into that game's folder there; anything else stays where it is. All of them are linked to the game in the
    /// gallery's own list too, so they show under it whatever their name or folder.
    /// </summary>
    public partial class ThemeApi
    {
        private List<GalleryFile> galleryMovePicked;
        private string galleryMoveFrom;   // the page it was opened from (grid / game)

        public ICommand GallerySelectionMoveCommand { get; private set; }
        public bool IsGalleryMoveTo { get { return galleryPage == "moveto"; } }

        private void InitGalleryMove()
        {
            GallerySelectionMoveCommand = new RelayCommand(_ => StartGalleryMove());
        }

        /// <summary>Hold A on a tile in the grid: picking several starts with that one picked.</summary>
        private bool GalleryHoldApplies()
        {
            if (openPanel != "gallery" || galleryViewerOpen || gallerySelect || confirmOpen) return false;
            if (galleryPage != "grid" && galleryPage != "game") return false;
            var fe = Keyboard.FocusedElement as System.Windows.FrameworkElement;
            var it = fe != null ? fe.DataContext as GalleryItem : null;
            return it != null && it.IsTile && it.File != null;
        }

        private void GalleryHoldSelect(System.Windows.IInputElement held)
        {
            var fe = held as System.Windows.FrameworkElement;
            var it = fe != null ? fe.DataContext as GalleryItem : null;
            PlaySound("select");
            gallerySelect = true;
            foreach (var i in GalleryItems) i.SelectMode = true;
            if (it != null) it.IsChecked = true;
            NotifyGallery();
            if (it != null) ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_GalleryGrid", it)), DispatcherPriority.ContextIdle);
        }

        private void StartGalleryMove()
        {
            var picked = GalleryItems.Where(i => i.IsTile && i.IsChecked && i.File != null).Select(i => i.File).ToList();
            if (picked.Count == 0) { PlaySound("back"); ShowGalleryStatus(Loc("LOC_KX_MovePickFirst", "Pick the photos and videos to move first")); return; }
            PlaySound("select");
            galleryMovePicked = picked;
            galleryMoveFrom = galleryPage;
            gallerySelect = false;
            galleryPage = "moveto";
            NotifyGallery();
            Notify("IsGalleryMoveTo");
            RebuildGallery(true);
        }

        /// <summary>The games to move to: the ones with captures and the ones played lately (most recent first), "Other" last.</summary>
        private void BuildMoveTargets(int gen)
        {
            var seen = new HashSet<Guid>();
            var order = new List<Game>();
            var withShots = galleryFiles.Where(f => f.GameId != Guid.Empty).GroupBy(f => f.GameId).Select(g => g.Key).ToList();
            var games = api.Database.Games.Where(g => g != null && !g.Hidden && !string.IsNullOrEmpty(g.Name)).ToList();
            var byId = games.ToDictionary(g => g.Id);
            foreach (var g in games.Where(g => g.LastActivity.HasValue || withShots.Contains(g.Id))
                                   .OrderByDescending(g => g.LastActivity ?? DateTime.MinValue).Take(80))
                if (seen.Add(g.Id)) order.Add(g);
            foreach (var id in withShots) { Game g; if (!seen.Contains(id) && byId.TryGetValue(id, out g)) { seen.Add(id); order.Add(g); } }
            foreach (var g in order)
            {
                var game = g;
                var t = new GalleryGameTile
                {
                    GameId = g.Id, Name = g.Name,
                    CountText = CountText(galleryFiles.Where(f => f.GameId == g.Id).ToList()),
                    CoverFile = !string.IsNullOrEmpty(g.CoverImage) ? SafeFullPath(g.CoverImage) : null
                };
                t.Command = new RelayCommand(_ => DoGalleryMove(game));
                GalleryGames.Add(t);
            }
            var other = new GalleryGameTile { GameId = Guid.Empty, Name = Loc("LOC_KX_OtherCaptures", "Other"), CountText = Loc("LOC_KX_MoveNoGame", "Not any game") };
            other.Command = new RelayCommand(_ => DoGalleryMove(null));
            GalleryGames.Add(other);
            galleryList = new List<GalleryFile>();
            int n = galleryMovePicked != null ? galleryMovePicked.Count : 0;
            GallerySubText = string.Format(Loc("LOC_KX_MoveSub", "{0}: pick the game they're from"), CountText(galleryMovePicked ?? new List<GalleryFile>()));
            LoadGameCovers(gen);
        }

        private string MoveTitle
        {
            get
            {
                int n = galleryMovePicked != null ? galleryMovePicked.Count : 0;
                return string.Format(n == 1 ? Loc("LOC_KX_MoveTitleOne", "Move 1 to a game") : Loc("LOC_KX_MoveTitleN", "Move {0} to a game"), n);
            }
        }

        /// <summary>B while choosing: back to the photos (picked ones forgotten).</summary>
        private bool GalleryMoveBack()
        {
            if (galleryPage != "moveto") return false;
            galleryPage = galleryMoveFrom ?? "grid";
            galleryMovePicked = null;
            NotifyGallery();
            Notify("IsGalleryMoveTo");
            RebuildGallery(true);
            return true;
        }

        private void DoGalleryMove(Game dest)
        {
            var list = galleryMovePicked ?? new List<GalleryFile>();
            galleryMovePicked = null;
            if (list.Count == 0) { GalleryMoveBack(); return; }
            PlaySound("select");
            string shotsDir, clipsText;
            List<string> clips;
            OverlayFolders(out shotsDir, out clips, out clipsText);
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var games = api.Database.Games.Where(g => g != null && !string.IsNullOrEmpty(g.Name)).ToList();
            Guid destId = dest != null ? dest.Id : Guid.Empty;
            string destName = dest != null ? dest.Name : null;
            KippixShared.SafeWork.Queue(_ =>
            {
                int moved = 0, failed = 0;
                var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Game> byFolder = null;
                foreach (var f in list)
                {
                    try
                    {
                        string to = f.Path;
                        string dir = Path.GetDirectoryName(f.Path) ?? "";
                        string parent = Path.GetDirectoryName(dir) ?? "";
                        string name = Path.GetFileName(f.Path);
                        if (dest != null && SamePath(dir, shotsDir))
                        {
                            // the overlay's folder: the name says the game
                            var m = CaptureDate.Match(Path.GetFileNameWithoutExtension(name));
                            string rest = m.Success ? m.Value.Trim().TrimStart('_', '-').Trim() : Path.GetFileNameWithoutExtension(name);
                            to = Path.Combine(dir, KippixOverlay.Capture.SafeName(destName) + " " + rest + Path.GetExtension(name));
                        }
                        else if (dest != null && !string.IsNullOrEmpty(videos) && (SamePath(parent, videos) || SamePath(parent, Path.Combine(videos, "NVIDIA"))))
                        {
                            // a game folder under Videos (NVIDIA's): into the game's folder there (the one it already has, or a new one)
                            string target = null;
                            try
                            {
                                if (byFolder == null) byFolder = GamesByFolder(games);
                                foreach (string d in Directory.GetDirectories(parent))
                                {
                                    string leaf = NormName(Path.GetFileName(d));
                                    Game g;
                                    if (leaf == NormName(destName) || (byFolder.TryGetValue(leaf, out g) && g.Id == destId)) { target = d; break; }
                                }
                            }
                            catch (Exception) { }
                            if (target == null) target = Path.Combine(parent, KippixOverlay.Capture.SafeName(destName));
                            Directory.CreateDirectory(target);
                            to = Path.Combine(target, name);
                        }
                        if (!SamePath(to, f.Path))
                        {
                            to = FreeName(to);
                            File.Move(f.Path, to);
                            renames[f.Path] = to;
                        }
                        lock (captureLock)
                        {
                            var idx = CaptureIndex();
                            idx.Remove(f.Path);
                            if (destId != Guid.Empty) idx[to] = destId.ToString();
                        }
                        moved++;
                    }
                    catch (Exception ex) { failed++; logger.Warn("Kippix: moving " + f.Path + ": " + ex.Message); }
                }
                try
                {
                    lock (captureLock)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CaptureIndexPath));
                        File.WriteAllText(CaptureIndexPath, new JavaScriptSerializer().Serialize(CaptureIndex()));
                    }
                }
                catch (Exception ex) { logger.Warn("Kippix: capture index: " + ex.Message); }
                OnUi(() =>
                {
                    // favourites follow a renamed file
                    if (renames.Count > 0 && settings.GalleryFavourites != null)
                    {
                        bool any = false;
                        for (int i = 0; i < settings.GalleryFavourites.Count; i++)
                        {
                            string nw;
                            if (renames.TryGetValue(settings.GalleryFavourites[i], out nw)) { settings.GalleryFavourites[i] = nw; any = true; }
                        }
                        if (any) SaveSettings();
                    }
                    if (moved > 0) galleryChanged = true;
                    string where = destName ?? Loc("LOC_KX_OtherCaptures", "Other");
                    string msg = string.Format(moved == 1 ? Loc("LOC_KX_MovedOne", "Moved 1 to {1}") : Loc("LOC_KX_MovedN", "Moved {0} to {1}"), moved, where);
                    if (failed > 0) msg += ". " + string.Format(Loc("LOC_KX_MoveFailed", "{0} couldn't be moved (in use, or no permission)"), failed);
                    if (openPanel != "gallery") return;
                    galleryPage = galleryMoveFrom ?? "grid";
                    NotifyGallery();
                    Notify("IsGalleryMoveTo");
                    galleryScannedAt = DateTime.MinValue;
                    ScanGallery(true);
                    ShowGalleryStatus(msg);
                });
            });
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        /// <summary>The path, or "name (2).ext" and so on when something's already there.</summary>
        private static string FreeName(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return Path.Combine(dir, stem + " " + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
        }
    }
}

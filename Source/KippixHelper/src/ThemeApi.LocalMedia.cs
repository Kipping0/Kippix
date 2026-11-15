using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.1) Media › Shows &amp; movies: your own videos, from folders you add (the cog on the page). Pages (openPanel "page"):
    ///  - "localmedia": every movie and show as a poster (arg: the filter, all / movies / shows), a chip per filter and the cog.
    ///  - "localshow": one show's episodes, by season (arg: its folder). The first row plays the next one you haven't watched.
    ///  - "localmediafolders": your folders, and Add a folder (the controller's folder picker).
    ///  - "localmediafolder": one folder: what's in it (worked out, movies, shows) and Remove (no "are you sure?").
    /// Nothing is copied or changed. A video plays full screen in Kippix's own player (ThemeApi.LocalPlayer.cs), or in Windows' player
    /// for it when that's switched off on the folders page (LocalMediaInKippix) or Windows can't play it inside Kippix.
    /// What's been watched is kept in the add-on's folder (localmedia.json); X on a movie / episode marks it watched or not.
    /// Folders are read when the page opens (off the UI thread, kept for a minute); nothing runs in the background.
    ///
    /// How a folder is read:
    ///  - a video on its own is a movie; a folder with one video (and maybe a sample / trailer / extras) is that movie, named after
    ///    the folder ("The Film (2010)/the.film.2010.1080p.mkv");
    ///  - a folder with season folders ("Season 1", "S01", "Series 2", "Specials"), SxxEyy-named videos, or three or more videos is
    ///    a show; its episodes are every video inside it (two folders down);
    ///  - any other folder is a group of them ("Movies", "TV"), read the same way (three folders down). A group called Movies / Films
    ///    only holds movies, and one called TV / Shows / Series / Anime only shows;
    ///  - a folder set to Movies or Shows on its own page skips the guessing.
    /// The picture: poster / folder / cover .jpg / .png beside it (Kodi, Jellyfin and Plex all save these), or the movie's own name
    /// with .jpg / -poster.jpg; else the name shows on the tile.
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly HashSet<string> LocalVideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".3gp", ".ogv", ".divx", ".vob" };
        private static readonly string[] PosterNames = { "poster", "folder", "cover", "show", "movie" };
        private static readonly string[] PicExts = { ".jpg", ".jpeg", ".png", ".webp" };

        private class LocalItem
        {
            public bool IsShow;
            public string Name, Year, Path, Poster;   // Path: the movie's file, or the show's folder
            public DateTime Added;
            public List<LocalEpisode> Episodes;
        }

        private class LocalEpisode
        {
            public string File, Title;
            public int Season = -1, Number = -1;   // -1: not known
        }

        private List<LocalItem> localItems;
        private DateTime localScannedAt = DateTime.MinValue;
        private string localScannedFor = "";
        private bool localScanning;
        private bool localFromMenu;   // opened from the profile menu's Media: B goes back there

        // ---------------- watched ----------------

        private class LocalMediaState
        {
            public List<string> Watched = new List<string>();
            public Dictionary<string, string> Last = new Dictionary<string, string>();   // show folder → the episode played last
            public List<string> Hidden = new List<string>();                             // (1.0.1) movies / shows hidden with Select (file or folder)
            public Dictionary<string, double> Pos = new Dictionary<string, double>();    // (1.0.1) file → where you stopped (seconds), Kippix's own player
        }

        private LocalMediaState localState;
        private HashSet<string> localWatched;

        private string LocalStatePath { get { return Path.Combine(plugin.GetPluginUserDataPath(), "localmedia.json"); } }

        private void LoadLocalState()
        {
            if (localState != null) return;
            try
            {
                if (File.Exists(LocalStatePath)) localState = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<LocalMediaState>(File.ReadAllText(LocalStatePath));
            }
            catch (Exception ex) { logger.Warn("Kippix: shows & movies: couldn't read what's watched: " + ex.Message); }
            if (localState == null) localState = new LocalMediaState();
            if (localState.Watched == null) localState.Watched = new List<string>();
            if (localState.Last == null) localState.Last = new Dictionary<string, string>();
            if (localState.Pos == null) localState.Pos = new Dictionary<string, double>();
            if (localState.Hidden == null) localState.Hidden = new List<string>();
            localWatched = new HashSet<string>(localState.Watched, StringComparer.OrdinalIgnoreCase);
        }

        private void SaveLocalState()
        {
            if (localState == null) return;
            localState.Watched = localWatched.ToList();
            string json, path = LocalStatePath;
            try { json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(localState); }
            catch (Exception) { return; }
            KippixShared.SafeWork.Queue(_ =>
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, json); }
                catch (Exception ex) { logger.Warn("Kippix: shows & movies: couldn't save what's watched: " + ex.Message); }
            });
        }

        private bool IsWatched(string file) { LoadLocalState(); return file != null && localWatched.Contains(file); }

        // ---------------- opening ----------------

        /// <summary>From the profile menu's Media (B comes back to it), a top bar shortcut or the quick bar.</summary>
        private void OpenLocalMedia(bool fromMenu)
        {
            ClosePanels();
            localFromMenu = fromMenu;
            pageStack.Clear();
            OpenPage("localmedia", localFilter, false);
            if (fromMenu && openPanel == "page") { profileReturnKey = "localmedia"; profileReturnPage = "media"; }
        }

        private string localFilter = "all";

        private void BuildLocalMediaPage(string arg)
        {
            if (arg == "movies" || arg == "shows" || arg == "all") localFilter = arg;
            PageTitle = Loc("LOC_KX_LocalMedia", "Shows & movies");
            PageHint = Loc("LOC_KX_LocalHintMore", "X  Watched / not watched     Select  Hide and more");
            var all = AddPageChip(Loc("LOC_KX_LocalAll", "All"), "media", () => SetLocalFilter("all"));
            var movies = AddPageChip(Loc("LOC_KX_LocalMovies", "Movies"), "filmstrip", () => SetLocalFilter("movies"));
            var shows = AddPageChip(Loc("LOC_KX_LocalShows", "Shows"), "tv", () => SetLocalFilter("shows"));
            all.Key = "all"; movies.Key = "movies"; shows.Key = "shows";
            var sort = AddPageChip(LocalSortText(), "sort", CycleLocalSort);
            sort.Key = "sort";
            var cog = AddPageChip(Loc("LOC_KX_LocalFolders", "Folders"), "gear", () => OpenPage("localmediafolders", "", true));
            cog.Key = "folders";
            MarkLocalChips();

            var folders = LocalFolders();
            if (folders.Count == 0)
            {
                PageSub = Loc("LOC_KX_LocalSub", "Your own videos, from folders on this PC");
                SetPageStatus(Loc("LOC_KX_LocalNone", "Add the folders your shows and movies are in, and they'll show here. Nothing is copied or changed."));
                PageRows.Add(new PageRow { Text = Loc("LOC_KX_AddFolder", "Add a folder"), Sub = Loc("LOC_KX_LocalAddSub", "A folder of movies, of shows, or both"), Icon = "add", Key = "add", Command = new RelayCommand(_ => AddLocalMediaFolder()) });
                return;
            }
            string sig = LocalFolderSig();
            if (localItems != null && sig == localScannedFor && (DateTime.Now - localScannedAt).TotalSeconds < 60) { FillLocalMedia(); return; }
            if (localItems != null && sig == localScannedFor) FillLocalMedia();   // (what it had, while it looks again)
            else SetPageStatus(Loc("LOC_KX_LocalLooking", "Looking through your folders…"));
            ScanLocalMedia(null);
        }

        private void SetLocalFilter(string f)
        {
            if (localFilter == f) return;
            PlaySound("move");
            localFilter = f;
            pageArg = f;
            MarkLocalChips();
            FillLocalMedia();
        }

        private void MarkLocalChips()
        {
            foreach (var c in PageChips) if (c.Key == "all" || c.Key == "movies" || c.Key == "shows") c.IsSelected = c.Key == localFilter;
        }

        private string LocalSortText()
        {
            return settings.LocalMediaSort == "added" ? Loc("LOC_KX_LocalSortAdded", "Newest first") : Loc("LOC_KX_LocalSortName", "A to Z");
        }

        private void CycleLocalSort()
        {
            PlaySound("move");
            settings.LocalMediaSort = settings.LocalMediaSort == "added" ? "name" : "added";
            SaveSettings();
            var chip = PageChips.FirstOrDefault(c => c.Key == "sort");
            if (chip != null) chip.SetText(LocalSortText());
            FillLocalMedia();
        }

        private List<string> LocalFolders()
        {
            if (settings.LocalMediaFolders == null) settings.LocalMediaFolders = new List<string>();
            return settings.LocalMediaFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        }

        private string LocalKind(string folder)
        {
            string k;
            if (settings.LocalMediaKinds != null && folder != null && settings.LocalMediaKinds.TryGetValue(folder, out k) && (k == "movies" || k == "shows")) return k;
            return "auto";
        }

        private string LocalFolderSig()
        {
            return string.Join("|", LocalFolders().Select(f => f + ">" + LocalKind(f)));
        }

        /// <summary>Reads every folder off the UI thread, then fills the page it's on (if it's still open).</summary>
        private void ScanLocalMedia(Action then)
        {
            if (localScanning) return;
            localScanning = true;
            var folders = LocalFolders().Select(f => Tuple.Create(f, LocalKind(f))).ToList();
            string sig = LocalFolderSig();
            KippixShared.SafeWork.Queue(_ =>
            {
                var items = new List<LocalItem>();
                var missing = new List<string>();
                var watch = Stopwatch.StartNew();
                foreach (var f in folders)
                {
                    try
                    {
                        if (!Directory.Exists(f.Item1)) { missing.Add(f.Item1); continue; }
                        string hint = f.Item2 != "auto" ? f.Item2 : GroupHint(Path.GetFileName(f.Item1.TrimEnd('\\', '/')));
                        ScanLocalDir(f.Item1, hint, 0, items, watch);
                    }
                    catch (Exception ex) { logger.Info("Kippix: shows & movies: " + f.Item1 + ": " + ex.Message); }
                }
                // the same video from two folders (one inside the other): once
                items = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                logger.Info("Kippix: shows & movies: " + items.Count(i => !i.IsShow) + " movies, " + items.Count(i => i.IsShow) + " shows in " + folders.Count + " folders ("
                            + watch.ElapsedMilliseconds + " ms)" + (missing.Count > 0 ? ", not found: " + string.Join(", ", missing) : ""));
                OnUi(() =>
                {
                    localScanning = false;
                    localItems = items;
                    localScannedAt = DateTime.Now;
                    localScannedFor = sig;
                    localMissing = missing;
                    if (openPanel == "page" && pageKey == "localmedia") FillLocalMedia();
                    else if (openPanel == "page" && pageKey == "localshow") FillLocalShow();
                    if (then != null) then();
                });
            });
        }

        private List<string> localMissing = new List<string>();

        private static string GroupHint(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (Regex.IsMatch(n, @"^(my )?(movies|films|movie|film)$")) return "movies";
            if (Regex.IsMatch(n, @"^(my )?(tv|tv shows|tv series|shows|series|anime|cartoons)$")) return "shows";
            return "auto";
        }

        private static readonly Regex SeasonDir = new Regex(@"^(season|series|staffel|saison|temporada|stagione)?[\s._-]*\d{1,3}$|^s\d{1,3}$|^specials?$|^season[\s._-]*\d{1,3}\b", RegexOptions.IgnoreCase);
        private static readonly Regex EpisodeName = new Regex(@"(?<![a-z0-9])s(\d{1,3})[\s._-]*e(\d{1,4})|(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![a-z0-9])|(?<![a-z])(?:ep|episode|e)[\s._-]?(\d{1,4})(?![0-9])", RegexOptions.IgnoreCase);

        private static bool IsVideo(string f)
        {
            return LocalVideoExts.Contains(Path.GetExtension(f) ?? "");
        }

        /// <summary>A sample, trailer or extra that comes with a movie (not the movie).</summary>
        private static bool IsExtra(string f)
        {
            string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            return Regex.IsMatch(n, @"(^|[\s._-])(sample|trailer|teaser|featurette|behind the scenes|deleted scene|extras?)($|[\s._-])");
        }

        private static string[] Videos(string dir)
        {
            try { return Directory.GetFiles(dir).Where(IsVideo).Where(f => !IsHidden(f)).ToArray(); }
            catch (Exception) { return new string[0]; }
        }

        private static string[] SubDirs(string dir)
        {
            try
            {
                return Directory.GetDirectories(dir).Where(d =>
                {
                    string n = Path.GetFileName(d);
                    if (n.StartsWith(".") || n.StartsWith("$") || IsHidden(d)) return false;
                    string l = n.ToLowerInvariant();
                    return l != "system volume information" && l != "@eadir" && l != ".actors" && l != "extrafanart";
                }).ToArray();
            }
            catch (Exception) { return new string[0]; }
        }

        private static bool IsHidden(string path)
        {
            try { var a = File.GetAttributes(path); return (a & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch (Exception) { return true; }
        }

        /// <summary>One folder: its own videos, and what each folder in it is (a movie, a show or a group).</summary>
        private void ScanLocalDir(string dir, string hint, int depth, List<LocalItem> items, Stopwatch watch)
        {
            if (watch.ElapsedMilliseconds > 60000 || items.Count > 20000) return;   // (a whole drive with everything on it: enough)
            var files = Videos(dir);
            if (hint == "shows" && depth == 0 && files.Length > 0)
            {
                // episodes straight in a folder of shows: the folder is the show
                var loose = MakeShow(dir, files.ToList());
                if (loose != null) items.Add(loose);
            }
            else
            {
                foreach (var f in files) if (!IsExtra(f)) items.Add(MakeMovie(f, null));
            }
            foreach (var sub in SubDirs(dir))
            {
                string kind = SubKind(sub, hint);
                if (kind == "movie")
                {
                    var vids = Videos(sub);
                    var main = vids.Where(v => !IsExtra(v)).OrderByDescending(v => SafeLength(v)).FirstOrDefault() ?? vids.OrderByDescending(v => SafeLength(v)).FirstOrDefault();
                    if (main != null) items.Add(MakeMovie(main, sub));
                }
                else if (kind == "show")
                {
                    var eps = new List<string>();
                    CollectVideos(sub, 2, eps);
                    var show = MakeShow(sub, eps);
                    if (show != null) items.Add(show);
                }
                else if (kind == "group" && depth < 3)
                {
                    string h = hint != "auto" ? hint : GroupHint(Path.GetFileName(sub));
                    ScanLocalDir(sub, h, depth + 1, items, watch);
                }
            }
        }

        /// <summary>"movie", "show", "group" or "" (nothing in it).</summary>
        private static string SubKind(string sub, string hint)
        {
            var vids = Videos(sub);
            var subs = SubDirs(sub);
            int seasons = subs.Count(s => SeasonDir.IsMatch(Path.GetFileName(s).Trim()));
            int named = vids.Count(v => EpisodeName.IsMatch(Path.GetFileNameWithoutExtension(v)) && !Regex.IsMatch(Path.GetFileNameWithoutExtension(v), @"(19|20)\d\d"));
            int main = vids.Count(v => !IsExtra(v));
            if (hint == "movies")
            {
                if (main > 0 || vids.Length > 0) return "movie";
                return subs.Length > 0 ? "group" : "";
            }
            if (hint == "shows")
            {
                if (vids.Length > 0 || seasons > 0) return "show";
                // a show whose seasons have their own names ("Book One"): any video two folders down
                if (subs.Any(s => Videos(s).Length > 0)) return "show";
                return subs.Length > 0 ? "group" : "";
            }
            if (GroupHint(Path.GetFileName(sub)) != "auto") return subs.Length > 0 || vids.Length > 0 ? "group" : "";   // ("Movies", "TV")
            if (seasons > 0 && seasons >= subs.Length / 2) return "show";
            // folders in it with videos of their own (not seasons, not a movie's extras): a group of movies / shows
            if (seasons == 0 && subs.Any(s => !IsExtrasDir(s) && (Videos(s).Length > 0 || SubDirs(s).Any(s2 => Videos(s2).Length > 0)))) return "group";
            if (named > 0 && (named >= 2 || (main == 1 && SxxEyy(vids)))) return "show";
            if (main >= 3) return "show";
            if (main >= 1) return "movie";
            if (vids.Length > 0) return "movie";
            return subs.Length > 0 ? "group" : "";
        }

        private static bool IsExtrasDir(string d)
        {
            return Regex.IsMatch(Path.GetFileName(d) ?? "", @"^(extras?|featurettes?|behind the scenes|deleted scenes|trailers?|samples?|subs|subtitles|interviews|shorts|other|bonus)$", RegexOptions.IgnoreCase);
        }

        private static bool SxxEyy(string[] vids)
        {
            return vids.Any(v => Regex.IsMatch(Path.GetFileNameWithoutExtension(v), @"(?<![a-z0-9])s\d{1,3}[\s._-]*e\d{1,4}", RegexOptions.IgnoreCase));
        }

        private static void CollectVideos(string dir, int depth, List<string> into)
        {
            foreach (var v in Videos(dir)) if (!IsExtra(v)) into.Add(v);
            if (depth <= 0) return;
            foreach (var s in SubDirs(dir)) CollectVideos(s, depth - 1, into);
        }

        private static long SafeLength(string f) { try { return new FileInfo(f).Length; } catch (Exception) { return 0; } }
        private static DateTime SafeWritten(string f) { try { return File.GetLastWriteTime(f); } catch (Exception) { return DateTime.MinValue; } }

        private LocalItem MakeMovie(string file, string ownFolder)
        {
            string year;
            string name = CleanTitle(ownFolder != null ? Path.GetFileName(ownFolder) : Path.GetFileNameWithoutExtension(file), out year);
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(file);
            string poster = null;
            string stem = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
            foreach (var e in PicExts)
            {
                if (poster != null) break;
                foreach (var cand in new[] { stem + "-poster" + e, stem + ".poster" + e, stem + e })
                    if (File.Exists(cand)) { poster = cand; break; }
            }
            if (poster == null && ownFolder != null) poster = FolderPoster(ownFolder);
            return new LocalItem { IsShow = false, Name = name, Year = year, Path = file, Poster = poster, Added = SafeWritten(file) };
        }

        private LocalItem MakeShow(string folder, List<string> files)
        {
            if (files == null || files.Count == 0) return null;
            string year;
            string showName = CleanTitle(Path.GetFileName(folder.TrimEnd('\\', '/')), out year);
            if (string.IsNullOrEmpty(showName)) showName = Path.GetFileName(folder);
            var eps = new List<LocalEpisode>();
            foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var ep = new LocalEpisode { File = f };
                string n = Path.GetFileNameWithoutExtension(f);
                var m = Regex.Match(n, @"(?<![a-z0-9])s(\d{1,3})[\s._-]*e(\d{1,4})", RegexOptions.IgnoreCase);
                if (!m.Success) m = Regex.Match(n, @"(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![a-z0-9])", RegexOptions.IgnoreCase);
                if (m.Success) { ep.Season = int.Parse(m.Groups[1].Value); ep.Number = int.Parse(m.Groups[2].Value); }
                // the season from its folder ("Season 2", "S02", "Specials" is 0)
                if (ep.Season < 0)
                {
                    string parent = Path.GetFileName(Path.GetDirectoryName(f));
                    if (!string.Equals(Path.GetDirectoryName(f).TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        if (Regex.IsMatch(parent ?? "", @"^specials?$", RegexOptions.IgnoreCase)) ep.Season = 0;
                        else { var sm = Regex.Match(parent ?? "", @"(\d{1,3})"); if (sm.Success) ep.Season = int.Parse(sm.Groups[1].Value); }
                    }
                }
                if (ep.Number < 0)
                {
                    var em = Regex.Match(n, @"(?<![a-z])(?:ep|episode|e)[\s._-]?(\d{1,4})(?![0-9])", RegexOptions.IgnoreCase);
                    if (!em.Success) em = Regex.Match(n, @"^\s*(\d{1,3})(?![0-9])");
                    if (!em.Success) em = Regex.Match(n, @"[\s._-](\d{1,3})[\s._-]*$");
                    if (em.Success) ep.Number = int.Parse(em.Groups[1].Value);
                }
                ep.Title = EpisodeTitle(n, showName);
                eps.Add(ep);
            }
            eps = eps.OrderBy(e => e.Season < 0 ? 9999 : e.Season == 0 ? 9998 : e.Season)   // (Specials after the seasons)
                     .ThenBy(e => e.Number < 0 ? int.MaxValue : e.Number)
                     .ThenBy(e => e.File, NaturalComparer.Instance).ToList();
            return new LocalItem
            {
                IsShow = true, Name = showName, Year = year, Path = folder, Poster = FolderPoster(folder), Episodes = eps,
                Added = eps.Count > 0 ? eps.Max(e => SafeWritten(e.File)) : SafeWritten(folder)
            };
        }

        private static string FolderPoster(string folder)
        {
            foreach (var n in PosterNames)
                foreach (var e in PicExts)
                {
                    string p = Path.Combine(folder, n + e);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        private static readonly Regex JunkWords = new Regex(@"(?<![a-z])(2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr10?\+?|hdr|dv|dolby ?vision|bluray|blu-ray|brrip|bdrip|bdremux|remux|web[ .-]?dl|webrip|web|hdtv|dvdrip|dvd|xvid|divx|x264|x265|h ?264|h ?265|hevc|avc|aac\d?(\.\d)?|ac3|eac3|dts(-hd)?|truehd|atmos|ddp?\d?(\.\d)?|10bit|8bit|proper|repack|extended|unrated|remastered|imax|multi|subbed|dubbed|internal|limited)(?![a-z])", RegexOptions.IgnoreCase);

        /// <summary>"The.Film.2010.1080p.BluRay.x264-GRP" → "The Film" (year 2010).</summary>
        private static string CleanTitle(string raw, out string year)
        {
            year = null;
            string s = raw ?? "";
            s = Regex.Replace(s, @"\[[^\]]*\]|\{[^}]*\}", " ");   // [group] {tmdb-123}
            s = s.Replace('_', ' ');
            if (!s.Contains(" ")) s = s.Replace('.', ' ');
            else s = Regex.Replace(s, @"(?<=[a-z])\.(?=[a-z])", " ", RegexOptions.IgnoreCase);
            // the year: in brackets, or on its own after the title
            var y = Regex.Match(s, @"\((19\d\d|20\d\d)\)");
            if (!y.Success) y = Regex.Match(s, @"(?<=\S\s)(19\d\d|20\d\d)(?![0-9])");
            if (y.Success)
            {
                year = y.Groups[1].Value;
                s = s.Substring(0, y.Index);
            }
            var j = JunkWords.Match(s);
            if (j.Success && j.Index > 0) s = s.Substring(0, j.Index);
            s = Regex.Replace(s, @"[\s(\-–.]+$", "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        /// <summary>An episode's own name, without the show's name, the SxxEyy and the quality bits ("" when that's all there is).</summary>
        private static string EpisodeTitle(string raw, string show)
        {
            string s = raw.Replace('_', ' ');
            if (!s.Contains(" ")) s = s.Replace('.', ' ');
            else s = Regex.Replace(s, @"(?<=[a-z0-9])\.(?=[a-z0-9])", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            var m = Regex.Match(s, @"(?<![a-z0-9])s\d{1,3}\s*e\d{1,4}(\s*-?\s*e\d{1,4})?|(?<![a-z0-9])\d{1,2}x\d{2,3}(?![a-z0-9])", RegexOptions.IgnoreCase);
            if (m.Success) s = s.Substring(m.Index + m.Length);
            else if (!string.IsNullOrEmpty(show) && s.StartsWith(show, StringComparison.OrdinalIgnoreCase)) s = s.Substring(show.Length);
            var j = JunkWords.Match(s);
            if (j.Success) s = s.Substring(0, j.Index);
            s = Regex.Replace(s, @"^[\s\-–.:]+|[\s\-–.(:]+$", "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (Regex.IsMatch(s, @"^(ep|episode|e)?\s*\d{1,4}$", RegexOptions.IgnoreCase)) return "";
            return s;
        }

        // ---------------- the posters ----------------

        private void FillLocalMedia()
        {
            if (pageKey != "localmedia") return;
            LoadLocalState();
            var was = FocusedPageItem() as PageTile;   // (filled again after a fresh look: the highlight stays on the same poster)
            string wasTag = was != null ? was.Tag : null;
            PageTiles.Clear();
            PageRows.Clear();
            var items = LocalVisible().Where(i => localFilter == "all" || (localFilter == "shows") == i.IsShow).ToList();
            items = settings.LocalMediaSort == "added"
                ? items.OrderByDescending(i => i.Added).ToList()
                : items.OrderBy(i => LocalSortName(i.Name), NaturalComparer.Instance).ThenBy(i => i.Year).ToList();
            int movies = LocalVisible().Count(i => !i.IsShow), shows = LocalVisible().Count(i => i.IsShow);
            PageSub = string.Format(Loc("LOC_KX_LocalCount", "{0} movies · {1} shows"), movies, shows);
            Notify("PageSub");
            foreach (var it in items)
            {
                var item = it;
                var tile = new PageTile { Name = it.Name, CoverPath = it.Poster, Tag = it.Path };
                if (it.IsShow)
                {
                    int left = it.Episodes.Count(e => !IsWatched(e.File));
                    tile.Sub = left == 0 ? Loc("LOC_KX_LocalAllWatched", "All watched")
                             : left == it.Episodes.Count ? (it.Episodes.Count == 1 ? Loc("LOC_KX_LocalOneEp", "1 episode") : string.Format(Loc("LOC_KX_LocalEps", "{0} episodes"), it.Episodes.Count))
                             : string.Format(Loc("LOC_KX_LocalLeft", "{0} left to watch"), left);
                    tile.Command = new RelayCommand(_ => OpenPage("localshow", item.Path, true));
                }
                else
                {
                    tile.Sub = (IsWatched(it.Path) ? "✓  " : "") + (it.Year ?? "");
                    tile.Command = new RelayCommand(_ => PlayLocal(item.Path, null));
                }
                PageTiles.Add(tile);
            }
            string status = "";
            if (items.Count == 0)
                status = localScanning ? Loc("LOC_KX_LocalLooking", "Looking through your folders…")
                       : localFilter == "movies" ? Loc("LOC_KX_LocalNoMovies", "No movies found in your folders.")
                       : localFilter == "shows" ? Loc("LOC_KX_LocalNoShows", "No shows found in your folders.")
                       : Loc("LOC_KX_LocalNothing", "No videos found in your folders. The cog (Folders) adds another, or says whether one holds movies or shows.");
            if (localMissing != null && localMissing.Count > 0)
                status = (status.Length > 0 ? status + "\n" : "") + string.Format(Loc("LOC_KX_LocalMissing", "Can't find {0} (a drive that isn't plugged in?)"), string.Join(", ", localMissing));
            SetPageStatus(status);
            NotifyPageLists();
            LoadPageImages();
            // the highlight: still on a chip, or on the first poster
            var again = wasTag != null ? PageTiles.FirstOrDefault(t => string.Equals(t.Tag, wasTag, StringComparison.OrdinalIgnoreCase)) : null;
            if (again != null) { FocusPageSoon(again); return; }
            var fe = Keyboard.FocusedElement as FrameworkElement;
            bool onChip = fe != null && fe.DataContext is PickRow && PageChips.Contains((PickRow)fe.DataContext);
            if (!onChip && PageTiles.Count > 0 && (fe == null || !fe.IsVisible || was != null)) FocusPageSoon(PageTiles.FirstOrDefault());
        }

        private static string LocalSortName(string n)
        {
            return Regex.Replace(n ?? "", @"^(the|a|an)\s+", "", RegexOptions.IgnoreCase);
        }

        // ---------------- a show ----------------

        private void BuildLocalShowPage(string folder)
        {
            PageHint = Loc("LOC_KX_LocalHint", "X  Watched / not watched");
            var show = localItems != null ? localItems.FirstOrDefault(i => i.IsShow && string.Equals(i.Path, folder, StringComparison.OrdinalIgnoreCase)) : null;
            PageTitle = show != null ? show.Name : Path.GetFileName((folder ?? "").TrimEnd('\\'));
            if (show == null)
            {
                SetPageStatus(Loc("LOC_KX_LocalLooking", "Looking through your folders…"));
                ScanLocalMedia(null);
                return;
            }
            FillLocalShow();
        }

        private void FillLocalShow()
        {
            if (pageKey != "localshow") return;
            LoadLocalState();
            var show = localItems != null ? localItems.FirstOrDefault(i => i.IsShow && string.Equals(i.Path, pageArg, StringComparison.OrdinalIgnoreCase)) : null;
            var keep = FocusedPageItem() as PageRow;
            PageRows.Clear();
            PageChips.Clear();
            if (show == null) { SetPageStatus(Loc("LOC_KX_LocalShowGone", "This show isn't in your folders any more.")); NotifyPageLists(); return; }
            SetPageStatus("");
            PageTitle = show.Name;
            int left = show.Episodes.Count(e => !IsWatched(e.File));
            PageSub = (show.Year != null ? show.Year + "  ·  " : "") + (show.Episodes.Count == 1 ? Loc("LOC_KX_LocalOneEp", "1 episode") : string.Format(Loc("LOC_KX_LocalEps", "{0} episodes"), show.Episodes.Count))
                    + (left > 0 && left < show.Episodes.Count ? "  ·  " + string.Format(Loc("LOC_KX_LocalLeft", "{0} left to watch"), left) : "");
            Notify("PageTitle"); Notify("PageSub");

            // the next one: after the one played last, else the first not watched
            var next = NextEpisode(show);
            if (next != null)
            {
                var n = next;
                PageRows.Add(new PageRow
                {
                    Text = (IsWatched(n.File) || show.Episodes.IndexOf(n) > 0 ? Loc("LOC_KX_LocalContinue", "Play next") : Loc("LOC_KX_LocalStart", "Play from the start")) + ":  " + EpisodeLabel(n),
                    Sub = n.Title.Length > 0 ? n.Title : Path.GetFileName(n.File), Icon = "play", Key = "next", ImagePath = show.Poster,
                    Command = new RelayCommand(_ => PlayLocal(n.File, show.Path))
                });
            }
            int season = int.MinValue;
            bool many = show.Episodes.Select(e => e.Season).Distinct().Count() > 1;
            foreach (var e in show.Episodes)
            {
                if (many && e.Season != season)
                {
                    season = e.Season;
                    AddPageHeader(e.Season == 0 ? Loc("LOC_KX_LocalSpecials", "SPECIALS") : e.Season < 0 ? Loc("LOC_KX_LocalOther", "OTHER EPISODES")
                                  : string.Format(Loc("LOC_KX_LocalSeason", "SEASON {0}"), e.Season));
                }
                var ep = e;
                bool seen = IsWatched(e.File);
                PageRows.Add(new PageRow
                {
                    Text = EpisodeLabel(e) + (e.Title.Length > 0 ? "  ·  " + e.Title : ""),
                    Sub = e.Title.Length > 0 ? null : Path.GetFileName(e.File),
                    Icon = seen ? "check" : "video", Key = e.File, Right = seen ? Loc("LOC_KX_LocalWatched", "Watched") : "",
                    Command = new RelayCommand(_ => PlayLocal(ep.File, show.Path))
                });
            }
            NotifyPageLists();
            LoadPageImages();
            object again = keep != null ? PageRows.FirstOrDefault(r => r.Key == keep.Key) : null;
            FocusPageSoon(again ?? PageRows.FirstOrDefault(r => r.IsRow));
        }

        private string EpisodeLabel(LocalEpisode e)
        {
            if (e.Number >= 0 && e.Season > 0) return string.Format(Loc("LOC_KX_LocalSE", "S{0} E{1}"), e.Season, e.Number);
            if (e.Number >= 0) return string.Format(Loc("LOC_KX_LocalE", "Episode {0}"), e.Number);
            return Path.GetFileNameWithoutExtension(e.File);
        }

        private LocalEpisode NextEpisode(LocalItem show)
        {
            if (show == null || show.Episodes == null || show.Episodes.Count == 0) return null;
            LoadLocalState();
            string last;
            if (localState.Last.TryGetValue(show.Path, out last))
            {
                int at = show.Episodes.FindIndex(e => string.Equals(e.File, last, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    for (int i = at + 1; i < show.Episodes.Count; i++) if (!IsWatched(show.Episodes[i].File)) return show.Episodes[i];
                    if (!IsWatched(show.Episodes[at].File)) return show.Episodes[at];
                }
            }
            return show.Episodes.FirstOrDefault(e => !IsWatched(e.File)) ?? show.Episodes[0];
        }

        // ---------------- playing ----------------

        /// <summary>Opens the video in Windows' own player for it, and counts it as watched.</summary>
        private void PlayLocal(string file, string show)
        {
            if (string.IsNullOrEmpty(file)) return;
            if (!File.Exists(file)) { PlaySound("back"); SetPageStatus(string.Format(Loc("LOC_KX_LocalFileGone", "Can't find {0} any more."), Path.GetFileName(file))); return; }
            PlaySound("select");
            LoadLocalState();
            if (show != null) localState.Last[show] = file;
            // (1.0.1) in Kippix's own player, full screen (ThemeApi.LocalPlayer.cs); it counts as watched near the end
            if (settings.LocalMediaInKippix && OpenLocalPlayer(file, show)) { SaveLocalState(); return; }
            PlayLocalOutside(file);
        }

        /// <summary>The video in Windows' own player for it (counted as watched straight away: there's no telling how far you get).</summary>
        private void PlayLocalOutside(string file)
        {
            LoadLocalState();
            localWatched.Add(file);
            SaveLocalState();
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) });
                logger.Info("Kippix: shows & movies: playing " + Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: shows & movies: couldn't play " + file + ": " + ex.Message);
                SetPageStatus(Loc("LOC_KX_LocalNoPlayer", "Windows has no app to play this kind of video. VLC or the Media Player app from the Microsoft Store play nearly everything."));
                return;
            }
            // the page shows it as watched when you come back
            ui.BeginInvoke(new Action(() =>
            {
                if (openPanel != "page") return;
                if (pageKey == "localshow") FillLocalShow();
                else if (pageKey == "localmedia") RefreshLocalTileSubs();
            }), DispatcherPriority.Background);
        }

        private void RefreshLocalTileSubs()
        {
            if (localItems == null) return;
            foreach (var t in PageTiles)
            {
                var it = localItems.FirstOrDefault(i => string.Equals(i.Path, t.Tag, StringComparison.OrdinalIgnoreCase));
                if (it == null) continue;
                if (it.IsShow)
                {
                    int left = it.Episodes.Count(e => !IsWatched(e.File));
                    t.Sub = left == 0 ? Loc("LOC_KX_LocalAllWatched", "All watched")
                          : left == it.Episodes.Count ? (it.Episodes.Count == 1 ? Loc("LOC_KX_LocalOneEp", "1 episode") : string.Format(Loc("LOC_KX_LocalEps", "{0} episodes"), it.Episodes.Count))
                          : string.Format(Loc("LOC_KX_LocalLeft", "{0} left to watch"), left);
                }
                else t.Sub = (IsWatched(it.Path) ? "✓  " : "") + (it.Year ?? "");
            }
        }

        /// <summary>X on a movie or an episode: watched, or not. A show's poster: every episode.</summary>
        private bool LocalMediaX()
        {
            if (pageKey != "localmedia" && pageKey != "localshow") return false;
            LoadLocalState();
            var item = FocusedPageItem();
            var tile = item as PageTile;
            var row = item as PageRow;
            if (pageKey == "localmedia" && tile != null && localItems != null)
            {
                var it = localItems.FirstOrDefault(i => string.Equals(i.Path, tile.Tag, StringComparison.OrdinalIgnoreCase));
                if (it == null) return true;
                if (it.IsShow)
                {
                    bool allSeen = it.Episodes.All(e => IsWatched(e.File));
                    foreach (var e in it.Episodes) { if (allSeen) localWatched.Remove(e.File); else localWatched.Add(e.File); }
                    if (allSeen) localState.Last.Remove(it.Path);
                }
                else if (!localWatched.Remove(it.Path)) localWatched.Add(it.Path);
                SaveLocalState();
                PlaySound("move");
                RefreshLocalTileSubs();
                return true;
            }
            if (pageKey == "localshow" && row != null && row.IsRow && row.Key != null && row.Key != "next")
            {
                if (!localWatched.Remove(row.Key)) localWatched.Add(row.Key);
                SaveLocalState();
                PlaySound("move");
                FillLocalShow();
                return true;
            }
            return pageKey == "localmedia" || pageKey == "localshow";
        }

        // ---------------- hiding (Select on a poster) ----------------

        /// <summary>What's found, less what you've hidden.</summary>
        private IEnumerable<LocalItem> LocalVisible()
        {
            LoadLocalState();
            var hidden = new HashSet<string>(localState.Hidden, StringComparer.OrdinalIgnoreCase);
            return (localItems ?? new List<LocalItem>()).Where(i => !hidden.Contains(i.Path));
        }

        /// <summary>Select (View) on a poster, or the menu key on a keyboard: that movie's / show's own page (Play, watched, Hide).</summary>
        private bool LocalMediaSelectKey(KeyEventArgs e)
        {
            if (pageKey != "localmedia" || IsPadRelease(e)) return false;
            bool select = e.Key == Key.Apps;
            if (!select && e.GetType().Name == "GameControllerInputEventArgs")
            {
                var bp = e.GetType().GetProperty("Button");
                object bv = bp != null ? bp.GetValue(e, null) : null;
                select = bv != null && bv.ToString() == "Back";
            }
            if (!select) return false;
            var tile = FocusedPageItem() as PageTile;
            if (tile == null || string.IsNullOrEmpty(tile.Tag)) return true;   // (Select elsewhere on the page: nothing)
            OpenPage("localitem", tile.Tag, true);
            return true;
        }

        private void BuildLocalItemPage(string path)
        {
            LoadLocalState();
            var it = localItems != null ? localItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) : null;
            if (it == null) { PageTitle = Path.GetFileName((path ?? "").TrimEnd('\\')); SetPageStatus(Loc("LOC_KX_LocalShowGone", "This show isn't in your folders any more.")); return; }
            PageTitle = it.Name;
            PageSub = (it.Year != null ? it.Year + "  ·  " : "") + it.Path;
            var item = it;
            if (it.IsShow)
                PageRows.Add(new PageRow { Text = Loc("LOC_KX_LocalEpisodes", "Episodes"), Icon = "tv", Key = "open", ImagePath = it.Poster,
                                           Sub = it.Episodes.Count == 1 ? Loc("LOC_KX_LocalOneEp", "1 episode") : string.Format(Loc("LOC_KX_LocalEps", "{0} episodes"), it.Episodes.Count),
                                           Command = new RelayCommand(_ => { pageQuietOpen = true; OpenPage("localshow", item.Path, false); }) });
            else
                PageRows.Add(new PageRow { Text = Loc("LOC_KX_PlayerPlay", "Play"), Icon = "play", Key = "open", ImagePath = it.Poster, Sub = Path.GetFileName(it.Path),
                                           Command = new RelayCommand(_ => PlayLocal(item.Path, null)) });
            bool seen = it.IsShow ? it.Episodes.All(e => IsWatched(e.File)) : IsWatched(it.Path);
            PageRows.Add(new PageRow
            {
                Text = seen ? Loc("LOC_KX_LocalMarkUnwatched", "Mark as not watched") : Loc("LOC_KX_LocalMarkWatched", "Mark as watched"), Icon = "check", Key = "watched",
                Sub = it.IsShow ? Loc("LOC_KX_LocalMarkShowSub", "Every episode") : null,
                Command = new RelayCommand(_ =>
                {
                    LoadLocalState();
                    if (item.IsShow) { foreach (var e in item.Episodes) { if (seen) localWatched.Remove(e.File); else localWatched.Add(e.File); } if (seen) localState.Last.Remove(item.Path); }
                    else if (seen) localWatched.Remove(item.Path); else localWatched.Add(item.Path);
                    SaveLocalState();
                    PlaySound("move");
                    pageQuietOpen = true;
                    OpenPage("localitem", item.Path, false);
                    FocusPageSoon(PageRows.FirstOrDefault(r => r.Key == "watched"));
                })
            });
            PageRows.Add(new PageRow
            {
                Text = Loc("LOC_KX_LocalHide", "Hide from Shows & movies"), Icon = "eyeoff", Key = "hide",
                Sub = Loc("LOC_KX_LocalHideSub", "It stays on your PC. Folders › Hidden shows it again"),
                Command = new RelayCommand(_ =>
                {
                    LoadLocalState();
                    if (!localState.Hidden.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) localState.Hidden.Add(item.Path);
                    SaveLocalState();
                    logger.Info("Kippix: shows & movies: hid " + item.Name);
                    PageBack();   // (the posters, without it)
                })
            });
            LoadPageImages();
        }

        private void BuildLocalHiddenPage()
        {
            LoadLocalState();
            PageTitle = Loc("LOC_KX_LocalHiddenTitle", "Hidden shows & movies");
            PageSub = Loc("LOC_KX_LocalHiddenPageSub", "A shows it in Shows & movies again");
            if (localState.Hidden.Count == 0) { SetPageStatus(Loc("LOC_KX_LocalHiddenEmpty", "Nothing is hidden. Select on a poster hides it.")); return; }
            foreach (var h in localState.Hidden.ToList())
            {
                string path = h;
                var it = localItems != null ? localItems.FirstOrDefault(i => string.Equals(i.Path, h, StringComparison.OrdinalIgnoreCase)) : null;
                PageRows.Add(new PageRow
                {
                    Text = it != null ? it.Name : Path.GetFileNameWithoutExtension(h.TrimEnd('\\')), Sub = h, Key = h, ImagePath = it != null ? it.Poster : null,
                    Icon = it != null && it.IsShow ? "tv" : "filmstrip", Right = Loc("LOC_KX_LocalUnhide", "Show again"),
                    Command = new RelayCommand(_ =>
                    {
                        LoadLocalState();
                        int at = PageRows.ToList().FindIndex(r => r.Key == path);
                        localState.Hidden.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                        SaveLocalState();
                        PlaySound("select");
                        pageQuietOpen = true;
                        OpenPage("localhidden", "", false);
                        var rows = PageRows.Where(r => r.IsRow).ToList();
                        if (rows.Count > 0) FocusPageSoon(rows[Math.Min(Math.Max(0, at), rows.Count - 1)]);
                    })
                });
            }
            LoadPageImages();
        }

        // ---------------- the folders (the cog) ----------------

        private void BuildLocalFoldersPage()
        {
            PageTitle = Loc("LOC_KX_LocalFoldersTitle", "Shows & movies folders");
            PageSub = Loc("LOC_KX_LocalFoldersSub", "Where your videos are. Nothing in them is copied or changed.");
            AddPageHeader(Loc("LOC_KX_YourFolders", "YOUR FOLDERS"));
            foreach (var f in LocalFolders())
            {
                string folder = f;
                bool there = Directory.Exists(f);
                PageRows.Add(new PageRow
                {
                    Text = FolderLabel(f),
                    Sub = f + (there ? "" : "  ·  " + Loc("LOC_KX_LocalNotFound", "not found right now")),
                    Right = LocalKindText(LocalKind(f)), Icon = "folder", Key = f,
                    Command = new RelayCommand(_ => OpenPage("localmediafolder", folder, true))
                });
            }
            PageRows.Add(new PageRow { Text = Loc("LOC_KX_AddFolder", "Add a folder"), Sub = Loc("LOC_KX_LocalAddSub", "A folder of movies, of shows, or both"), Icon = "add", Key = "add", Command = new RelayCommand(_ => AddLocalMediaFolder()) });
            // (1.0.1) where videos play
            // (1.0.1) what you've hidden (Select on a poster › Hide)
            LoadLocalState();
            int hidden = localState.Hidden.Count;
            PageRows.Add(new PageRow
            {
                Text = Loc("LOC_KX_LocalHidden", "Hidden"), Icon = "eyeoff", Key = "hidden",
                Sub = hidden == 0 ? Loc("LOC_KX_LocalHiddenNone", "Nothing hidden. Select on a poster hides it") : Loc("LOC_KX_LocalHiddenSub", "Movies and shows you've hidden: show them again"),
                Right = hidden.ToString(CultureInfo.CurrentCulture),
                Command = new RelayCommand(_ => OpenPage("localhidden", "", true))
            });
            AddPageHeader(Loc("LOC_KX_LocalPlaying", "PLAYING"));
            PageRows.Add(new PageRow
            {
                Text = Loc("LOC_KX_LocalInKippix", "Play in Kippix"), Icon = settings.LocalMediaInKippix ? "check" : "video", Key = "inkippix",
                Sub = settings.LocalMediaInKippix ? Loc("LOC_KX_LocalInKippixOn", "Full screen, with the controller. Carries on where you stopped. A video Windows can't play here opens in its own player")
                                                  : Loc("LOC_KX_LocalInKippixOff", "Off: videos open in Windows' own player for them"),
                Right = settings.LocalMediaInKippix ? Loc("LOC_KX_On", "On") : Loc("LOC_KX_Off", "Off"),
                Command = new RelayCommand(_ =>
                {
                    settings.LocalMediaInKippix = !settings.LocalMediaInKippix;
                    SaveSettings();
                    PlaySound("move");
                    pageQuietOpen = true;
                    OpenPage("localmediafolders", "", false);
                    FocusPageSoon(PageRows.FirstOrDefault(r => r.Key == "inkippix"));
                })
            });
            SetPageStatus(Loc("LOC_KX_LocalHow", "A show is a folder of episodes (season folders are fine). A movie is a video on its own, or in a folder of its own. A poster.jpg or folder.jpg beside them is used as the picture."));
        }

        private static string FolderLabel(string f)
        {
            string n = Path.GetFileName((f ?? "").TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(n) ? f : n;   // (a whole drive: "D:\\")
        }

        private string LocalKindText(string kind)
        {
            return kind == "movies" ? Loc("LOC_KX_LocalKindMovies", "Movies") : kind == "shows" ? Loc("LOC_KX_LocalKindShows", "Shows") : Loc("LOC_KX_LocalKindAuto", "Movies and shows");
        }

        private void BuildLocalFolderPage(string folder)
        {
            PageTitle = Path.GetFileName((folder ?? "").TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(PageTitle)) PageTitle = folder;
            PageSub = folder;
            string kind = LocalKind(folder);
            AddPageHeader(Loc("LOC_KX_LocalHolds", "WHAT'S IN IT"));
            foreach (var k in new[] { "auto", "movies", "shows" })
            {
                string kk = k;
                PageRows.Add(new PageRow
                {
                    Text = LocalKindText(k),
                    Sub = k == "auto" ? Loc("LOC_KX_LocalKindAutoSub", "Kippix works out which is which") : k == "movies" ? Loc("LOC_KX_LocalKindMoviesSub", "Every video (or folder with one in it) is a movie")
                        : Loc("LOC_KX_LocalKindShowsSub", "Every folder in it is a show"),
                    Icon = k == kind ? "check" : k == "movies" ? "filmstrip" : k == "shows" ? "tv" : "media", Key = "kind:" + k, Right = k == kind ? Loc("LOC_KX_LocalChosen", "Chosen") : "",
                    Command = new RelayCommand(_ => SetLocalKind(folder, kk))
                });
            }
            AddPageHeader("");
            PageRows.Add(new PageRow { Text = Loc("LOC_KX_LocalRemove", "Remove this folder"), Sub = Loc("LOC_KX_LocalRemoveSub", "Only from Shows & movies: the videos stay where they are"), Icon = "remove", Key = "remove",
                                       Command = new RelayCommand(_ => RemoveLocalFolder(folder)) });
        }

        private void SetLocalKind(string folder, string kind)
        {
            if (settings.LocalMediaKinds == null) settings.LocalMediaKinds = new Dictionary<string, string>();
            if (kind == "auto") settings.LocalMediaKinds.Remove(folder); else settings.LocalMediaKinds[folder] = kind;
            SaveSettings();
            PlaySound("move");
            pageQuietOpen = true;
            OpenPage("localmediafolder", folder, false);
            FocusPageSoon(PageRows.FirstOrDefault(r => r.Key == "kind:" + kind));
        }

        private void RemoveLocalFolder(string folder)
        {
            settings.LocalMediaFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (settings.LocalMediaKinds != null) settings.LocalMediaKinds.Remove(folder);
            SaveSettings();
            logger.Info("Kippix: shows & movies: removed folder " + folder);
            PageBack();   // (the folders, without it)
        }

        /// <summary>The controller's folder picker (in the settings panel), then back to the folders page.</summary>
        private void AddLocalMediaFolder()
        {
            PlaySound("select");
            bool fromMenu = localFromMenu;
            ClosePanels();
            TogglePanel("options");
            OpenPicker(Loc("LOC_KX_AddFolder", "Add a folder"), true, null, "", "", dir =>
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    if (settings.LocalMediaFolders == null) settings.LocalMediaFolders = new List<string>();
                    if (!settings.LocalMediaFolders.Any(f => string.Equals(f.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.LocalMediaFolders.Add(dir);
                        SaveSettings();
                        logger.Info("Kippix: shows & movies: added folder " + dir);
                    }
                }
                ReopenLocalFolders(dir, fromMenu);
            }, () => ReopenLocalFolders(null, fromMenu));
        }

        private void ReopenLocalFolders(string focusKey, bool fromMenu)
        {
            ClosePanels();
            ui.BeginInvoke(new Action(() =>
            {
                localFromMenu = fromMenu;
                pageStack.Clear();
                pageQuietOpen = true;
                OpenPage("localmedia", localFilter, false);
                if (fromMenu && openPanel == "page") { profileReturnKey = "localmedia"; profileReturnPage = "media"; }
                pageQuietOpen = true;
                OpenPage("localmediafolders", "", true);
                var row = PageRows.FirstOrDefault(r => r.Key == (focusKey ?? "add")) ?? PageRows.FirstOrDefault(r => r.Key == "add");
                if (row != null) FocusPageSoon(row);
            }), DispatcherPriority.Loaded);
        }
    }

    /// <summary>"Episode 2" before "Episode 10".</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string a, string b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}

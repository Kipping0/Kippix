using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Input;

namespace KippixHelper
{
    /// <summary>A game page layout on its own (0.89): only the game page's settings and where its parts are (Free move).</summary>
    public class GamePageFile
    {
        public string Format = "kippix-gamepage";
        public int Version = 1;
        public string Name;
        public string Made;        // the theme version it was made with
        public HelperSettings Settings;
    }

    /// <summary>
    /// (0.89) Game page layouts: save yours, use a saved one, add one from a file, upload one (Community › Yours, or Game page
    /// settings › Layout › Upload this game page, which takes the picture of the game page), and the Community page's
    /// Game pages tab (community/gamepages/&lt;id&gt;.json in the gallery). Using one changes only the game page (its
    /// settings and the game.* parts' places, sizes, turns, layers and opacity), and Undo puts yours back.
    /// Kept in Documents › Kippix Layouts › Game pages.
    /// </summary>
    public partial class ThemeApi
    {
        private static string GamePagesFolder { get { return Path.Combine(LayoutsFolder, "Game pages"); } }
        private bool communityUsedGamePage;

        private static bool IsGameKey(string k) { return k != null && k.StartsWith("game.", StringComparison.Ordinal); }

        /// <summary>A copy of the settings with only the game page in it.</summary>
        private HelperSettings GamePageCopy()
        {
            SaveSettings();
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var copy = js.Deserialize<HelperSettings>(js.Serialize(settings));
            var blank = new HelperSettings();
            foreach (var f in typeof(HelperSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!GameLayoutFields.Contains(f.Name) && !f.Name.StartsWith("Master", StringComparison.Ordinal)) f.SetValue(copy, f.GetValue(blank));
            copy.MasterOffsets = (settings.MasterOffsets ?? new Dictionary<string, double[]>()).Where(p => IsGameKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            copy.MasterSizes = (settings.MasterSizes ?? new Dictionary<string, double[]>()).Where(p => IsGameKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            copy.MasterRotations = (settings.MasterRotations ?? new Dictionary<string, double>()).Where(p => IsGameKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            copy.MasterLayers = (settings.MasterLayers ?? new Dictionary<string, int>()).Where(p => IsGameKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            copy.MasterOpacity = (settings.MasterOpacity ?? new Dictionary<string, double>()).Where(p => IsGameKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            copy.EditsMerged = true;
            StampLayoutSize(copy);   // (1.1.0) the screen it's made on (ThemeApi.LayoutFit.cs)
            return copy;
        }

        private string GamePageFileText(string name)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return js.Serialize(new GamePageFile { Name = name, Made = HelperVersionText(), Settings = GamePageCopy() });
        }

        /// <summary>A game page file (or a whole saved layout: its game page). Null when it's neither.</summary>
        private static HelperSettings ReadGamePageText(string text)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = js.DeserializeObject((text ?? "").TrimStart('﻿')) as Dictionary<string, object>;
            if (root == null) return null;
            object fmt;
            if (root.TryGetValue("Format", out fmt) && Convert.ToString(fmt) == "kippix-gamepage")
            {
                var f = js.Deserialize<GamePageFile>(text.TrimStart('﻿'));
                return f != null ? f.Settings : null;
            }
            if (root.ContainsKey("DetLayout") || root.ContainsKey("DetShowLogo")) return js.Deserialize<HelperSettings>(text.TrimStart('﻿'));
            return null;
        }

        /// <summary>Puts a game page layout on (yours goes in the undo file first).</summary>
        private void ApplyGamePage(HelperSettings src)
        {
            if (src == null) throw new InvalidDataException("not a game page layout");
            if (!src.EditsMerged) MergeEdits(src);
            BackupForUndo();
            foreach (var f in typeof(HelperSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (GameLayoutFields.Contains(f.Name)) f.SetValue(settings, f.GetValue(src));
            if (settings.MasterOffsets == null) settings.MasterOffsets = new Dictionary<string, double[]>();
            if (settings.MasterSizes == null) settings.MasterSizes = new Dictionary<string, double[]>();
            if (settings.MasterRotations == null) settings.MasterRotations = new Dictionary<string, double>();
            if (settings.MasterLayers == null) settings.MasterLayers = new Dictionary<string, int>();
            if (settings.MasterOpacity == null) settings.MasterOpacity = new Dictionary<string, double>();
            ReplaceGameKeys(settings.MasterOffsets, src.MasterOffsets);
            ReplaceGameKeys(settings.MasterSizes, src.MasterSizes);
            ReplaceGameKeys(settings.MasterRotations, src.MasterRotations);
            ReplaceGameKeys(settings.MasterLayers, src.MasterLayers);
            ReplaceGameKeys(settings.MasterOpacity, src.MasterOpacity);
            NormalizeSettings();
            settings.LayoutFitPending = new List<string> { "game" };   // (1.1.0) made on another screen: anything off it brought on (ThemeApi.LayoutFit.cs)
            ApplyAllSettings();
            SaveSettings();
            try { ApplyDetPlacement(); } catch (Exception) { }
        }

        private static void ReplaceGameKeys<T>(Dictionary<string, T> mine, Dictionary<string, T> theirs)
        {
            foreach (var k in mine.Keys.Where(IsGameKey).ToList()) mine.Remove(k);
            if (theirs == null) return;
            foreach (var p in theirs) if (IsGameKey(p.Key)) mine[p.Key] = p.Value;
        }

        private static string GamePageSafeName(string typed)
        {
            string name = string.Join("_", (typed ?? "").Trim().Split(Path.GetInvalidFileNameChars()));
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        // ---------------- commands for the settings (Game page › Layout) ----------------

        private ICommand shareGamePageCmd, saveGamePageCmd;
        /// <summary>Game page settings › Layout › Upload this game page (the picture is of the game page under the settings).</summary>
        public ICommand ShareGamePageCommand { get { return shareGamePageCmd ?? (shareGamePageCmd = new RelayCommand(_ => ShareToCommunity("gamepages", null))); } }
        /// <summary>Game page settings › Layout › Save this game page layout.</summary>
        public ICommand SaveGamePageCommand { get { return saveGamePageCmd ?? (saveGamePageCmd = new RelayCommand(_ => SaveGamePageAs(null))); } }

        /// <summary>Asks for a name and saves the game page layout in the Game pages folder. Then: done (a message).</summary>
        private void SaveGamePageAs(Action<string> after)
        {
            PlaySound("select");
            OpenKeyboard(Loc("LOC_KX_GamePageName", "A name for this game page layout"), "", typed =>
            {
                string name = GamePageSafeName(typed);
                if (name.Length == 0) return;
                string msg;
                try
                {
                    Directory.CreateDirectory(GamePagesFolder);
                    File.WriteAllText(Path.Combine(GamePagesFolder, name + ".json"), GamePageFileText(name), Encoding.UTF8);
                    PlaySound("select");
                    msg = string.Format(Loc("LOC_KX_GamePageSaved", "Saved \"{0}\": it's in Community › Yours › Your game page layouts."), name);
                }
                catch (Exception ex) { msg = Loc("LOC_KX_LayoutShareFailed", "Couldn't save a copy there."); logger.Warn("Kippix: save game page: " + ex.Message); }
                if (after != null) after(msg);
                else try { ShowCard(Loc("LOC_KX_GamePageHeader", "GAME PAGE"), Loc("LOC_KX_GamePageSavedTitle", "Game page layout"), msg, "", null); } catch (Exception) { }
            }, null, null, 60);
        }

        // ---------------- Community › Yours › Your game page layouts ----------------

        private void BuildGamePageListPage()
        {
            PageTitle = Loc("LOC_KX_YourGamePages", "Your game page layouts");
            PageSub = string.Format(Loc("LOC_KX_YourGamePagesSub", "Kept in {0}. A uses one (Undo puts yours back)."), GamePagesFolder);
            AddPageChip(Loc("LOC_KX_SaveGamePage", "Save this game page layout"), "add", () => SaveGamePageAs(msg => { communityStatus = msg; ReopenGamePageList(); }));
            string[] files = new string[0];
            try { if (Directory.Exists(GamePagesFolder)) files = Directory.GetFiles(GamePagesFolder, "*.json").OrderByDescending(File.GetLastWriteTime).ToArray(); } catch (Exception) { }
            foreach (string f in files)
            {
                string file = f;
                PageRows.Add(new PageRow
                {
                    Key = "gp:" + Path.GetFileName(file), Icon = "controller", Text = Path.GetFileNameWithoutExtension(file),
                    Sub = File.GetLastWriteTime(file).ToString("d MMM yyyy", CultureInfo.CurrentCulture),
                    Command = new RelayCommand(_ => OpenPage("gamepagefile", file, true))
                });
            }
            string note = communityStatus;
            communityStatus = null;
            SetPageStatus(note ?? (files.Length == 0 ? Loc("LOC_KX_NoGamePages", "None saved yet. Save this game page layout keeps the one you have now, and the Game pages tab has other people's.") : ""));
            NotifyPageLists();
        }

        private void ReopenGamePageList()
        {
            if (openPanel == "page" && pageKey == "gamepagelist") { pageQuietOpen = true; OpenPage("gamepagelist", "", false); }
        }

        /// <summary>One saved game page layout: use it, upload it, or delete it.</summary>
        private void BuildGamePageFilePage(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file ?? "");
            PageTitle = name;
            PageSub = Loc("LOC_KX_CommunityKindGamePage", "Game page layout");
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { SetPageStatus(Loc("LOC_KX_CommunityGone2", "That one isn't in the gallery any more.")); return; }
            AddPageChip(Loc("LOC_KX_UseThisGamePage", "Use this game page"), "use", () =>
            {
                string msg;
                try
                {
                    ApplyGamePage(ReadGamePageText(File.ReadAllText(file)));
                    communityUsedGamePage = true;
                    PlaySound("select");
                    msg = string.Format(Loc("LOC_KX_GamePageUsing", "Using \"{0}\" on the game page. Undo puts yours back."), name);
                    AddCommunityUndoChip("gamepages");
                }
                catch (Exception ex) { PlaySound("back"); msg = Loc("LOC_KX_GamePageBad", "That game page layout couldn't be read."); logger.Warn("Kippix: game page file: " + ex.Message); }
                SetPageStatus(msg);
            });
            AddPageChip(Loc("LOC_KX_Delete", "Delete"), "trash", () =>
            {
                try { File.Delete(file); } catch (Exception) { }
                communityStatus = string.Format(Loc("LOC_KX_GamePageDeleted", "Deleted \"{0}\"."), name);
                PageBack();
            });
            if (CommunityCanUndo("gamepages")) AddCommunityUndoChip("gamepages");
            SetPageStatus(Loc("LOC_KX_GamePageFileNote", "Using it changes only the game page: its design, what's on it and where everything is."));
            NotifyPageLists();
        }

        // ---------------- the gallery ----------------

        private string CommunityTakeGamePage(CommunityItem c, string text, string target, bool use)
        {
            var src = ReadGamePageText(text);
            if (src == null) throw new InvalidDataException("not a game page layout");
            Directory.CreateDirectory(GamePagesFolder);
            File.WriteAllText(target, text, Encoding.UTF8);
            string name = Path.GetFileNameWithoutExtension(target);
            if (!use) return string.Format(Loc("LOC_KX_CommunityGamePageSaved", "Saved as \"{0}\": it's in Community › Yours › Your game page layouts."), name);
            ApplyGamePage(src);
            communityUsedGamePage = true;
            return string.Format(Loc("LOC_KX_CommunityGamePageUsing", "Using \"{0}\" on the game page. Undo puts yours back."), name);
        }

        /// <summary>Community › Yours › Add a game page from a file: copied into the Game pages folder (and used straight away).</summary>
        private void CommunityGamePageFile()
        {
            PlaySound("select");
            string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            OpenPicker(Loc("LOC_KX_CommunityGamePageFile", "Add a game page from a file…"), false, new[] { ".json" }, Directory.Exists(start) ? start : "", "community", file =>
            {
                string msg;
                try
                {
                    if (new FileInfo(file).Length > 40L * 1024 * 1024) throw new InvalidDataException("too big");
                    string text = File.ReadAllText(file);
                    var src = ReadGamePageText(text);
                    if (src == null) throw new InvalidDataException("not a game page layout");
                    Directory.CreateDirectory(GamePagesFolder);
                    string name = Path.GetFileNameWithoutExtension(file);
                    string target = Path.Combine(GamePagesFolder, name + ".json");
                    for (int i = 2; File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase); i++)
                        target = Path.Combine(GamePagesFolder, name + " (" + i + ").json");
                    if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) File.WriteAllText(target, text, Encoding.UTF8);
                    ApplyGamePage(src);
                    communityUsedGamePage = true;
                    PlaySound("select");
                    msg = string.Format(Loc("LOC_KX_CommunityGamePageAdded", "Added \"{0}\" and it's on the game page now. Undo puts yours back."), Path.GetFileNameWithoutExtension(target));
                }
                catch (Exception ex)
                {
                    PlaySound("back");
                    msg = Loc("LOC_KX_GamePageBad", "That game page layout couldn't be read.");
                    logger.Warn("Kippix: community game page file: " + ex.Message);
                }
                communityStatus = msg;
                GoBackTo("community", "community");
            });
        }
    }
}

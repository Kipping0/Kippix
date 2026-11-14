using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// The community gallery: layouts, look packs and widgets other people have made, kept on GitHub (the theme's repository,
    /// community folder):
    ///   community/index.json   { "version": 2, "layouts": [ … ], "looks": [ … ], "widgets": [ … ] }, each entry
    ///                          { "id", "name", "author", "description", "file", "preview", "added", "themeVersion", "tags" }
    ///                          (a widget's entry also has "type": text, image, web, feed, data, clock…)
    ///   community/layouts/&lt;id&gt;.json  a layout file (what Layouts › Save writes)
    ///   community/looks/&lt;id&gt;.json    a look pack (what Look packs › Save this look as a pack writes)
    ///   community/widgets/&lt;id&gt;.json  a widget file (what Share this widget writes)
    ///   community/gamepages/&lt;id&gt;.json  a game page layout (0.89, ThemeApi.GamePageShare.cs)
    ///   community/previews/…        16:9 pictures
    /// The Community page (one page with tabs: ThemeApi.CommunityHub.cs) shows them; A on one opens "communityitem"
    /// (arg: kind|id) with its picture, who made it and what it's like, and one press to use / add it (and Undo).
    /// Upload saves the file, a picture of the home screen and the gallery entry, ready to post on GitHub with the issue
    /// form, where every one is checked before it goes in. Nothing is uploaded from here, and nothing runs in the background:
    /// the index is read when the page opens (kept 30 minutes).
    /// </summary>
    public partial class ThemeApi
    {
        private class CommunityItem
        {
            public string Kind, Id, Name, Author, Description, File, Preview, Added, ThemeVersion, WidgetType;
            public List<string> Tags = new List<string>();
            public List<string> Previews = new List<string>();   // (1.0.4) every picture (the first is Preview)
            public string Asset = "";                            // (1.0.4) its copy in the gallery's "community" release (downloads are counted there)
            public int Downloads = -1;                           // (1.0.4) -1: not known
        }

        internal static readonly string[] CommunityKinds = { "layouts", "gamepages", "looks", "widgets" };

        private Dictionary<string, List<CommunityItem>> communityLists;
        private DateTime communityAt = DateTime.MinValue;
        private string communityError;

        // what the last press here changed (Undo on the item page, the tabs and Yours)
        private bool communityUsedLayout;
        private LookStyle communityLookBefore;
        private string communityLookBeforeId;
        private string communityWidgetAdded;

        /// <summary>owner/repo the gallery comes from (the helper's desktop settings can point it somewhere else for testing).</summary>
        private string CommunityRepo
        {
            get
            {
                string r = (settings.CommunityRepo ?? "").Trim().Trim('/');
                if (r.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) r = r.Substring(19).Trim('/');
                return r.Length > 0 ? r : GitHubRepo;
            }
        }

        private string CommunityRaw(string path)
        {
            return "https://raw.githubusercontent.com/" + CommunityRepo + "/" + (string.IsNullOrWhiteSpace(settings.CommunityBranch) ? "main" : settings.CommunityBranch.Trim()) + "/community/" + path.TrimStart('/');
        }

        /// <summary>The GitHub form for sharing one (null until there's a community page to post on).</summary>
        private string CommunityPostUrl(string kind)
        {
            if (string.IsNullOrEmpty(CommunityRepo)) return null;
            string form = kind == "looks" ? "look.yml" : kind == "widgets" ? "widget.yml" : kind == "layouts" ? "layout.yml" : kind == "gamepages" ? "gamepage.yml" : null;
            return "https://github.com/" + CommunityRepo + "/issues/new" + (form != null ? "?template=" + form : "/choose");
        }

        private static string CommunityKind(string kind) { return Array.IndexOf(CommunityKinds, kind) >= 0 ? kind : "layouts"; }

        private string CommunityKindTitle(string kind)
        {
            switch (kind)
            {
                case "looks": return Loc("LOC_KX_CommunityLooks", "Community look packs");
                case "widgets": return Loc("LOC_KX_CommunityWidgetsTitle", "Community widgets");
                case "gamepages": return Loc("LOC_KX_CommunityGamePagesTitle", "Community game pages");
            }
            return Loc("LOC_KX_CommunityLayouts", "Community layouts");
        }

        private string CommunityKindShort(string kind)
        {
            switch (kind)
            {
                case "looks": return Loc("LOC_KX_LookPacks", "Look packs");
                case "widgets": return Loc("LOC_KX_Widgets", "Widgets");
                case "gamepages": return Loc("LOC_KX_GamePagesShort", "Game pages");
            }
            return Loc("LOC_KX_LayoutsShort", "Layouts");
        }

        /// <summary>One of a kind, for the badge on a picture (All tab) and the item page.</summary>
        private string CommunityKindOne(string kind)
        {
            switch (kind)
            {
                case "looks": return Loc("LOC_KX_CommunityKindLook", "Look pack");
                case "widgets": return Loc("LOC_KX_CommunityKindWidget", "Widget");
                case "gamepages": return Loc("LOC_KX_CommunityKindGamePage", "Game page layout");
            }
            return Loc("LOC_KX_CommunityKindLayout", "Layout");
        }

        private static string CommunityKindIcon(string kind)
        {
            return kind == "looks" ? "palette" : kind == "widgets" ? "grid" : kind == "gamepages" ? "controller" : "layout";
        }

        // ---------------- opening ----------------

        /// <summary>Layouts &amp; reset's old entry point: the layouts in the gallery.</summary>
        private void OpenCommunityLayouts() { OpenCommunity("layouts", null, false); }

        // ---------------- the index ----------------

        /// <summary>The gallery's index (worker thread): every kind, newest first. A version 1 index only has layouts.</summary>
        private static Dictionary<string, List<CommunityItem>> ReadCommunityIndex(string url)
        {
            byte[] bytes = Fetch(url + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute, 2 * 1024 * 1024);
            string text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
            var all = new Dictionary<string, List<CommunityItem>>();
            foreach (string kind in CommunityKinds)
            {
                var list = new List<CommunityItem>();
                all[kind] = list;
                object arr;
                if (root == null || !root.TryGetValue(kind, out arr) || !(arr is object[])) continue;
                foreach (var o in (object[])arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var c = new CommunityItem
                    {
                        Kind = kind, Id = FStr(d, "id"), Name = FStr(d, "name"), Author = FStr(d, "author"), Description = FStr(d, "description"),
                        File = FStr(d, "file"), Preview = FStr(d, "preview"), Added = FStr(d, "added"), ThemeVersion = FStr(d, "themeVersion"), WidgetType = FStr(d, "type")
                    };
                    object tags;
                    if (d.TryGetValue("tags", out tags) && tags is object[]) c.Tags = ((object[])tags).Select(t => Convert.ToString(t)).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    // only files inside the community folder (no addresses of their own, no ..)
                    if (c.Id.Length == 0 || !CommunityPathOk(c.File)) continue;
                    if (!CommunityPathOk(c.Preview)) c.Preview = "";
                    object pics;
                    if (d.TryGetValue("previews", out pics) && pics is object[])
                        c.Previews = ((object[])pics).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).Where(CommunityPathOk).Take(8).ToList();
                    if (c.Previews.Count == 0 && c.Preview.Length > 0) c.Previews.Add(c.Preview);
                    if (c.Preview.Length == 0 && c.Previews.Count > 0) c.Preview = c.Previews[0];
                    c.Asset = FStr(d, "asset");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(c.Asset, "^[A-Za-z0-9._-]{1,150}$")) c.Asset = "";
                    if (c.Name.Length == 0) c.Name = c.Id;
                    list.Add(c);
                }
                list.Sort((a, b) => string.CompareOrdinal(b.Added ?? "", a.Added ?? ""));
            }
            return all;
        }

        private static bool CommunityPathOk(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length > 200) return false;
            if (p.Contains("..") || p.Contains(":") || p.Contains("\\") || p.StartsWith("/", StringComparison.Ordinal)) return false;
            return p.All(ch => char.IsLetterOrDigit(ch) || ch == '/' || ch == '-' || ch == '_' || ch == '.');
        }

        private List<CommunityItem> CommunityListOf(string kind)
        {
            List<CommunityItem> l;
            return communityLists != null && communityLists.TryGetValue(kind, out l) ? l : null;
        }

        private string CommunityWidgetKindName(string type)
        {
            try { return WidgetTypeName(type); } catch (Exception) { return type; }
        }

        // ---------------- where a download is kept ----------------

        private static string CommunitySafeName(CommunityItem c)
        {
            string safe = string.Join("_", (c.Name.Length > 0 ? c.Name : c.Id).Split(Path.GetInvalidFileNameChars())).Trim();
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            return safe.Length > 0 ? safe : "community";
        }

        private string CommunityFileFor(CommunityItem c)
        {
            string folder = c.Kind == "looks" ? LooksFolder : c.Kind == "widgets" ? WidgetFilesFolder : c.Kind == "gamepages" ? GamePagesFolder : UploadedFolder;
            return Path.Combine(folder, CommunitySafeName(c) + ".json");
        }

        private bool CommunityHaveIt(CommunityItem c) { try { return File.Exists(CommunityFileFor(c)); } catch (Exception) { return false; } }

        // ---------------- one of them ----------------

        /// <summary>Layouts &amp; reset's old page key (communitylayout): a layout.</summary>
        private void BuildCommunityLayoutPage(string id) { BuildCommunityItemPage("layouts|" + id); }

        private void BuildCommunityItemPage(string arg)
        {
            string[] parts = (arg ?? "").Split(new[] { '|' }, 2);
            string kind = CommunityKind(parts[0]);
            string id = parts.Length > 1 ? parts[1] : "";
            var list = CommunityListOf(kind);
            var c = list != null ? list.FirstOrDefault(x => x.Id == id) : null;
            if (c == null) { PageTitle = CommunityKindTitle(kind); SetPageStatus(Loc("LOC_KX_CommunityGone2", "That one isn't in the gallery any more.")); return; }
            PageTitle = c.Name;
            PageSub = string.Join("   ·   ", new[]
            {
                CommunityKindOne(kind),
                c.Author.Length > 0 ? string.Format(Loc("LOC_KX_ByAuthor", "by {0}"), c.Author) : "",
                c.Kind == "widgets" && c.WidgetType.Length > 0 ? CommunityWidgetKindName(c.WidgetType) : "",
                c.ThemeVersion.Length > 0 ? string.Format(Loc("LOC_KX_MadeWith", "made with {0}"), c.ThemeVersion) : "",
                c.Added,
                CommunityDownloadsText(c)
            }.Where(x => x.Length > 0).ToArray());
            var item = c;
            if (kind == "looks")
            {
                AddPageChip(Loc("LOC_KX_UseThisLook", "Use this look"), "use", () => UseCommunityItem(item, true));
                AddPageChip(Loc("LOC_KX_SaveToPacks", "Save it to your look packs"), "download", () => UseCommunityItem(item, false));
            }
            else if (kind == "widgets")
            {
                AddPageChip(Loc("LOC_KX_AddToHome2", "Add it to your home screen"), "add", () => UseCommunityItem(item, true));
                AddPageChip(Loc("LOC_KX_SaveWidgetFile", "Save the widget file"), "download", () => UseCommunityItem(item, false));
                if (IsCommunityType(c.WidgetType) && !settings.CommunityWidgetsOn) AddCommunitySwitchChip();
            }
            else if (kind == "gamepages")
            {
                AddPageChip(Loc("LOC_KX_UseThisGamePage", "Use this game page"), "use", () => UseCommunityItem(item, true));
                AddPageChip(Loc("LOC_KX_SaveForLater", "Save it for later"), "download", () => UseCommunityItem(item, false));
            }
            else
            {
                AddPageChip(Loc("LOC_KX_UseThisLayout", "Use this layout"), "use", () => UseCommunityItem(item, true));
                AddPageChip(Loc("LOC_KX_SaveForLater", "Save it for later"), "download", () => UseCommunityItem(item, false));
            }
            if (CommunityCanUndo(kind)) AddCommunityUndoChip(kind);
            if (c.Description.Length > 0) { AddPageHeader(Loc("LOC_KX_AboutIt", "ABOUT IT")); PageRows.Add(new PageRow { Text = c.Description, Icon = "info" }); }
            if (c.Tags.Count > 0) PageRows.Add(new PageRow { Text = string.Join(", ", c.Tags.ToArray()), Icon = "tag" });
            if (kind == "widgets" && IsCommunityType(c.WidgetType))
                PageRows.Add(new PageRow { Text = Loc("LOC_KX_CommunityGoesOnline2", "This one goes online (only while it's on screen): it needs Community widgets switched on (Community › Yours)"), Icon = "globe" });
            // (1.0.4) its pictures, big, under the buttons (‹ › or A for the next one); no small card any more
            HasPageHero = false;
            pageHeroGame = Guid.Empty;
            ShowCommunityShots(c);
            NotifyPage();
            NotifyPageLists();
        }

        /// <summary>A widget that goes online, and Community widgets are off: a chip that switches them on, right here.</summary>
        private void AddCommunitySwitchChip()
        {
            var chip = new PickRow { Key = "c:switch", Icon = "globe", Text = Loc("LOC_KX_CommunitySwitchOn", "Switch on community widgets") };
            chip.Command = new RelayCommand(_ =>
            {
                CommunityWidgetsOn = true;
                PlaySound("move");
                SetPageStatus(Loc("LOC_KX_CommunitySwitchedOn", "Community widgets are on: web snippets, news feeds and data widgets can go online while they're on screen. Now add it."));
                PageChips.Remove(chip);
                NotifyPageLists();
                FocusPageSoon(PageChips.FirstOrDefault());
            });
            PageChips.Add(chip);
        }

        private bool CommunityCanUndo(string kind)
        {
            if (kind == "looks") return communityLookBefore != null;
            if (kind == "widgets") return communityWidgetAdded != null && settings.Widgets != null && settings.Widgets.Any(w => w.Id == communityWidgetAdded);
            if (kind == "gamepages") return communityUsedGamePage && HasUndoReset;
            return communityUsedLayout && HasUndoReset;
        }

        private void AddCommunityUndoChip(string kind)
        {
            string key = "c:undo:" + kind;
            if (PageChips.Any(x => x.Key == key)) return;
            var chip = new PickRow
            {
                Key = key, Icon = "refresh",
                Text = kind == "looks" ? Loc("LOC_KX_UndoLook", "Undo: put your look back") : kind == "widgets" ? Loc("LOC_KX_UndoWidget", "Undo: take it off again")
                     : kind == "gamepages" ? Loc("LOC_KX_UndoGamePage", "Undo: put your game page back") : Loc("LOC_KX_UndoLayout", "Undo: put yours back")
            };
            chip.Command = new RelayCommand(_ =>
            {
                CommunityUndo(kind);
                if (openPanel != "page") return;
                PageChips.Remove(chip);
                NotifyPageLists();
                FocusPageSoon(PageChips.FirstOrDefault());
            });
            PageChips.Add(chip);
            NotifyPageLists();
        }

        /// <summary>Takes back what the last press in the gallery did (a layout, a look, a widget). The message says what happened.</summary>
        private string CommunityUndo(string kind)
        {
            string done = null;
            try
            {
                if (kind == "looks" && communityLookBefore != null)
                {
                    var before = communityLookBefore;
                    string id = communityLookBeforeId;
                    communityLookBefore = null;
                    communityLookBeforeId = null;
                    UseLookPack(before, string.IsNullOrEmpty(id) ? "builtin:kippix" : id);
                    done = Loc("LOC_KX_UndoLookDone", "Your look is back.");
                }
                else if (kind == "widgets" && communityWidgetAdded != null)
                {
                    string id = communityWidgetAdded;
                    communityWidgetAdded = null;
                    var w = Widgets != null ? Widgets.FirstOrDefault(x => x.Config != null && x.Config.Id == id) : null;
                    settings.Widgets.RemoveAll(c => c.Id == id);
                    if (w != null) Widgets.Remove(w);
                    if (editingWidget != null && editingWidget.Config != null && editingWidget.Config.Id == id) editingWidget = null;
                    SaveSettings();
                    Notify("ShowWidgets");
                    Notify("WidgetCountText");
                    PlaySound("back");
                    done = Loc("LOC_KX_UndoWidgetDone", "Taken off your home screen.");
                }
                else if (kind == "gamepages" && communityUsedGamePage)
                {
                    communityUsedGamePage = false;
                    UndoReset();
                    try { ApplyDetPlacement(); } catch (Exception) { }
                    done = Loc("LOC_KX_UndoGamePageDone", "Your own game page is back.");
                }
                else if (kind == "layouts" && communityUsedLayout)
                {
                    communityUsedLayout = false;
                    UndoReset();
                    done = Loc("LOC_KX_UndoLayoutDone", "Your own layout is back.");
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: community undo: " + ex.Message); }
            if (done != null && openPanel == "page") SetPageStatus(done);
            return done;
        }

        /// <summary>Downloads one (off the UI thread) and checks it's what it says; then uses / adds it, or only keeps the file.</summary>
        private void UseCommunityItem(CommunityItem c, bool use)
        {
            PlaySound("select");
            if (c.Kind == "widgets" && use && IsCommunityType(c.WidgetType) && !settings.CommunityWidgetsOn)
            {
                SetPageStatus(Loc("LOC_KX_CommunityNeedsSwitch2", "This widget goes online: switch on community widgets first (the button above, or Community › Yours), then add it."));
                return;
            }
            SetPageStatus(Loc("LOC_KX_Downloading", "Downloading…"));
            string url = CommunityRaw(c.File);
            // (1.0.4) from the gallery's release when it's there: GitHub counts those downloads (the numbers on the gallery)
            string counted = c.Asset.Length > 0 && !string.IsNullOrEmpty(CommunityRepo) ? "https://github.com/" + CommunityRepo + "/releases/download/community/" + c.Asset : null;
            string target = CommunityFileFor(c);
            int gen = pageGen;
            KippixShared.SafeWork.Queue(_ =>
            {
                string text = null;
                try
                {
                    byte[] bytes = null;
                    if (counted != null) try { bytes = Fetch(counted, c.Kind == "looks" ? 1024 * 1024 : 24 * 1024 * 1024); } catch (Exception) { bytes = null; }
                    if (bytes == null) bytes = Fetch(url, c.Kind == "looks" ? 1024 * 1024 : 24 * 1024 * 1024);
                    text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
                }
                catch (Exception ex) { logger.Warn("Kippix: community download: " + ex.Message); }
                OnUi(() =>
                {
                    if (text == null) { if (gen == pageGen) SetPageStatus(Loc("LOC_KX_CommunityDownloadFailed2", "Couldn't download that one. Check the internet connection and try again.")); return; }
                    if (gen != pageGen && c.Kind == "widgets" && use) return;   // (left the page while it came: not placed out of the blue)
                    string msg;
                    try { msg = c.Kind == "looks" ? CommunityTakeLook(c, text, target, use) : c.Kind == "widgets" ? CommunityTakeWidget(c, text, target, use) : c.Kind == "gamepages" ? CommunityTakeGamePage(c, text, target, use) : CommunityTakeLayout(c, text, target, use); }
                    catch (Exception ex) { msg = Loc("LOC_KX_CommunityBadFile", "That file couldn't be read, so nothing was changed."); logger.Warn("Kippix: community " + c.Kind + ": " + ex.Message); }
                    if (gen == pageGen && openPanel == "page")
                    {
                        if (msg != null) SetPageStatus(msg);
                        if (use && CommunityCanUndo(c.Kind)) AddCommunityUndoChip(c.Kind);
                    }
                });
            });
        }

        private string CommunityTakeLayout(CommunityItem c, string text, string target, bool use)
        {
            // it must be a layout: read it as one before keeping it
            var check = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<HelperSettings>(text);
            if (check == null) throw new InvalidDataException("not a layout");
            Directory.CreateDirectory(UploadedFolder);
            File.WriteAllText(target, text, Encoding.UTF8);
            string name = Path.GetFileNameWithoutExtension(target);
            if (use)
            {
                if (!ApplyLayoutFile(target, "uploaded:" + name)) return Loc("LOC_KX_LayoutBad", "That layout file couldn't be read.");
                communityUsedLayout = true;
                RefreshLayoutProfiles();
                return string.Format(Loc("LOC_KX_CommunityUsing3", "Using \"{0}\". Undo brings yours back (also Settings & Customisation › Layouts & reset › Undo the last reset)."), name);
            }
            RefreshLayoutProfiles();
            return string.Format(Loc("LOC_KX_CommunitySaved2", "Saved as \"{0}\": it's in Settings & Customisation › Layouts & reset › Layout."), name);
        }

        private string CommunityTakeLook(CommunityItem c, string text, string target, bool use)
        {
            var s = LookFromText(new JavaScriptSerializer(), text);
            if (s == null) throw new InvalidDataException("not a look pack");
            string name = Path.GetFileNameWithoutExtension(target);
            s.Name = name;
            Directory.CreateDirectory(LooksFolder);
            File.WriteAllText(target, new JavaScriptSerializer().Serialize(s), Encoding.UTF8);
            if (use)
            {
                if (communityLookBefore == null)   // (several tried in a row: Undo goes back to the one you had first)
                {
                    communityLookBefore = LookNow.Copy();
                    communityLookBeforeId = settings.LookPack;
                }
                UseLookPack(s, "user:" + name);
                return string.Format(Loc("LOC_KX_CommunityLookUsing", "Using \"{0}\". Undo puts your look back; it's kept in Look and feel › Look packs."), name);
            }
            return string.Format(Loc("LOC_KX_CommunityLookSaved", "Saved as \"{0}\" in Look and feel › Look packs."), name);
        }

        private string CommunityTakeWidget(CommunityItem c, string text, string target, bool use)
        {
            string problem;
            var cfg = ReadWidgetText(text, use, out problem);
            if (cfg == null) return problem ?? Loc("LOC_KX_WidgetFileBad", "That isn't a Kippix widget file");
            Directory.CreateDirectory(WidgetFilesFolder);
            File.WriteAllText(target, text, Encoding.UTF8);
            if (!use) return string.Format(Loc("LOC_KX_CommunityWidgetSaved3", "Saved in {0}: Yours › Add from a widget file adds it."), target);
            // placed by hand on the home screen, like any new widget; afterwards back to the Community page
            CommunityPlaceWidget(cfg);
            return null;
        }

        // ---------------- sharing yours (Upload) ----------------

        private static string ShareFolder { get { return Path.Combine(LayoutsFolder, "Share"); } }

        private static string ShareFolderFor(string kind)
        {
            return kind == "looks" ? Path.Combine(ShareFolder, "Looks") : kind == "widgets" ? Path.Combine(ShareFolder, "Widgets") : kind == "gamepages" ? Path.Combine(ShareFolder, "Game pages") : ShareFolder;
        }

        /// <summary>Asks for a name, closes the pages for a moment for a picture of the home screen, then saves the files in
        /// Documents › Kippix Layouts › Share (the file, the picture, the gallery entry) and says what to do next, back on
        /// the Community page on that kind's tab (with the GitHub form when there's a community page to post on).</summary>
        private void ShareToCommunity(string kind, WidgetItem widget)
        {
            PlaySound("select");
            if (openPanel == "options")   // (a widget's own Share this widget: B from the Community page goes back to it)
            {
                var b = Keyboard.FocusedElement as System.Windows.Controls.Primitives.ButtonBase;
                communityFrom = "options";
                communityFromPage = optionsPage ?? "";
                communityFromKey = b != null ? b.CommandParameter as string : null;
            }
            string ask = kind == "looks" ? Loc("LOC_KX_ShareLookName", "A name for your look") : kind == "widgets" ? Loc("LOC_KX_ShareWidgetName", "A name for the widget")
                       : kind == "gamepages" ? Loc("LOC_KX_GamePageName", "A name for this game page layout") : Loc("LOC_KX_ShareName", "A name for your layout");
            string start = kind == "widgets" && widget != null && widget.Config != null ? WidgetFullName(widget.Config) : kind == "looks" ? (LookNow.Name ?? "") : "";
            if (start == "Kippix") start = "";
            OpenKeyboard(ask, start, typed =>
            {
                string name = string.Join("_", (typed ?? "").Trim().Split(Path.GetInvalidFileNameChars()));
                if (name.Length > 60) name = name.Substring(0, 60);
                if (name.Length == 0) return;
                // the picture is of the home screen: the pages close for a moment
                ClosePanels();
                var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(900) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    string result;
                    try { result = WriteShare(kind, name, widget); }
                    catch (Exception ex) { result = null; logger.Warn("Kippix: share " + kind + ": " + ex.Message); }
                    // (1.0.1) with the gallery set up, it's sent straight there (signing in to GitHub first, once)
                    if (result != null && !string.IsNullOrEmpty(CommunityRepo) && shareLastId != null)
                    {
                        communityStatus = null;
                        ShowCommunityPage("mine", "u:" + kind);
                        OpenSendPage(kind, ShareFolderFor(kind), shareLastId);   // (1.0.4) pictures and a description first
                        return;
                    }
                    communityStatus = result ?? Loc("LOC_KX_LayoutShareFailed", "Couldn't save a copy there.");
                    communitySharedKind = result != null ? kind : null;
                    ShowCommunityPage("mine", communitySharedKind != null && CommunityPostUrl(kind) != null ? "c:post" : "u:" + kind);   // (0.81: Upload is on Yours)
                };
                t.Start();
            }, null, null, 60);
        }

        private string shareLastId;   // (1.0.1) the last share's file name (id.json / .jpg / .entry.json), for sending it

        private string WriteShare(string kind, string name, WidgetItem widget)
        {
            shareLastId = null;
            string folder = ShareFolderFor(kind);
            Directory.CreateDirectory(folder);
            string id = new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            while (id.Contains("--")) id = id.Replace("--", "-");
            if (id.Length == 0) id = kind == "looks" ? "look" : kind == "widgets" ? "widget" : kind == "gamepages" ? "gamepage" : "layout";
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string widgetType = "";
            if (kind == "looks")
            {
                var copy = LookNow.Copy();
                copy.Name = name;
                File.WriteAllText(Path.Combine(folder, id + ".json"), js.Serialize(copy), Encoding.UTF8);
            }
            else if (kind == "widgets")
            {
                if (widget == null || widget.Config == null) throw new InvalidOperationException("no widget");
                File.WriteAllText(Path.Combine(folder, id + ".json"), js.Serialize(new WidgetFile { Widget = WidgetForFile(widget) }), Encoding.UTF8);
                widgetType = widget.Config.Type ?? "";
            }
            else if (kind == "gamepages")
            {
                File.WriteAllText(Path.Combine(folder, id + ".json"), GamePageFileText(name), Encoding.UTF8);
                // and a copy in your own game page layouts
                try { Directory.CreateDirectory(GamePagesFolder); File.WriteAllText(Path.Combine(GamePagesFolder, name + ".json"), GamePageFileText(name), Encoding.UTF8); } catch (Exception) { }
            }
            else
            {
                // the layout (only layout fields, like Layouts › Save)
                SaveSettings();
                var copy = js.Deserialize<HelperSettings>(js.Serialize(settings));
                var blank = new HelperSettings();
                foreach (var f in typeof(HelperSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    if (!InProfile(FieldGroup(f.Name))) f.SetValue(copy, f.GetValue(blank));
                copy.EditsMerged = true;
                copy.LayoutHasViews = true;   // (the gallery's and achievements' sort / filter / tab are in it)
                try { EmbedTileImages(copy); } catch (Exception) { }   // (your own picture tiles go with it)
                File.WriteAllText(Path.Combine(folder, id + ".json"), js.Serialize(copy), Encoding.UTF8);
            }
            // (1.0.4) extra pictures from an earlier share of the same name go
            for (int i = 2; i <= MaxSharePictures; i++) try { File.Delete(Path.Combine(folder, id + "-" + i + ".jpg")); } catch (Exception) { }
            // a picture of the home screen
            var win = Application.Current.MainWindow;
            var root = win != null ? win.Content as FrameworkElement : null;
            bool pictured = false;
            if (root != null && root.ActualWidth > 0)
            {
                double scale = Math.Min(1.0, 1280.0 / root.ActualWidth);
                var rtb = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(folder, id + ".jpg"))) enc.Save(fs);
                pictured = true;
            }
            // the index entry, filled in (the gallery's folders: layouts/, looks/, widgets/, previews/)
            string sub = kind == "looks" ? "looks" : kind == "widgets" ? "widgets" : kind == "gamepages" ? "gamepages" : "layouts";
            var entry = new Dictionary<string, object>
            {
                { "id", id }, { "name", name }, { "author", ProfileName }, { "description", "" },
                { "file", sub + "/" + id + ".json" }, { "preview", pictured ? (kind == "layouts" ? "previews/" : "previews/" + sub + "/") + id + ".jpg" : "" },
                { "added", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "themeVersion", HelperVersionText() }, { "tags", new string[0] }
            };
            if (kind == "widgets") entry["type"] = widgetType;
            File.WriteAllText(Path.Combine(folder, id + ".entry.json"), js.Serialize(entry), Encoding.UTF8);
            shareLastId = id;
            string tip = kind == "gamepages" && !detailsOpen ? "\n\n" + Loc("LOC_KX_GamePageShareTip", "The picture is of the home screen: for one of the game page, upload it from a game's page (Game page settings › Layout › Upload this game page).") : "";
            if (string.IsNullOrEmpty(CommunityRepo))
                return string.Format(Loc("LOC_KX_ShareSavedNoRepo2", "Saved in {0} ({1}.json, a picture and its gallery entry). Once Kippix's community page is up, you can post them there for the gallery (every one is checked before it goes in). You can already send the .json file to someone: they add it on Community › Yours, from a file."), folder, id) + tip;
            return string.Format(Loc("LOC_KX_ShareSaved2", "Saved in {0} ({1}.json, a picture and its gallery entry). To add it to the gallery, post the three files at {2}: every one is checked before it goes in."), folder, id, "github.com/" + CommunityRepo + "/issues") + tip;
        }

        private string HelperVersionText()
        {
            try { return YamlVersion(Path.Combine(ThemeDir, "theme.yaml")) ?? ""; } catch (Exception) { return ""; }
        }
    }
}

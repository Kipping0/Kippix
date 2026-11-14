using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// What your own tiles and community widgets show (WidgetConfig.Custom). Saved with the widget, so it goes with layouts,
    /// profiles and widget files.
    /// </summary>
    public class CustomWidgetDef
    {
        public string Name = "";           // its title (empty: the kind's own name)
        public string Text = "";           // text tile: the text ("|" starts a new line)
        public double FontSize = 48;       // text tile / data widget: px at 100 %
        public int Align = 1;              // 0 left, 1 centre, 2 right
        public bool Bold;
        public string Image = "";          // picture tile: the picture's file name in the tiles folder (ExtensionsData\...\tiles)
        public int Fit;                    // picture / web snippet: 0 the whole picture, 1 fills the tile (cropped to fit)
        public string Url = "";            // web snippet / news feed / data widget: the address (http / https only)
        public string Template = "";       // data widget: what it shows, fields in braces by dot path, e.g. "{price} – {status.text}"
        public int RefreshMinutes = 15;    // how often it's fetched again (only while it's on screen)
        public int Items = 5;              // news feed: how many headlines
        public int ViewWidth = 1280;       // web snippet: the page's width (its height follows the widget's shape)
        public string ImageData;           // only in layout and widget files: the picture itself (base64), written back to the tiles folder when loaded
        public string ImageName;           // ... and its file name then
    }

    /// <summary>
    /// Your own tiles (0.78): Text tiles (typed with the on-screen keyboard; size, colour, alignment, background) and Picture
    /// tiles (picked with the controller file picker, copied to ExtensionsData\...\tiles), placed, sized, turned, layered and
    /// deleted like any widget, on the home screen and in Board tiles, and carried with layouts (a saved layout file has the
    /// pictures inside it). Plus the rows on a widget's page for them and for the community widgets (ThemeApi.CommunityWidgets.cs).
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly string[] CustomTypes = { "text", "image", "web", "feed", "data" };
        private static readonly string[] TileImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
        private static readonly int[] FontSizes = { 24, 32, 40, 48, 64, 80, 96, 128, 160 };
        private static readonly int[] RefreshChoices = { 1, 5, 10, 15, 30, 60, 180 };

        internal static bool IsCustomType(string type) { return Array.IndexOf(CustomTypes, type) >= 0; }
        internal static bool IsCommunityType(string type) { return type == "web" || type == "feed" || type == "data"; }

        private string TilesFolder { get { return Path.Combine(plugin.GetPluginUserDataPath(), "tiles"); } }

        /// <summary>The picture's file (a file name in the tiles folder, or a full path from before).</summary>
        private string TileImagePath(string image)
        {
            if (string.IsNullOrEmpty(image)) return null;
            try { return Path.IsPathRooted(image) ? image : Path.Combine(TilesFolder, Path.GetFileName(image)); }
            catch (Exception) { return null; }
        }

        private string WidgetTitleOf(WidgetConfig c)
        {
            if (c.Type == "spotlight" || c.Type == "suggest") return SubjectName(c.Type, c.Subject);
            if (IsCustomType(c.Type) && c.Custom != null && !string.IsNullOrWhiteSpace(c.Custom.Name)) return c.Custom.Name.Trim();
            return WidgetTypeName(c.Type);
        }

        private string CustomFullName(WidgetConfig c)
        {
            var d = c.Custom;
            string what = "";
            if (d != null)
            {
                if (!string.IsNullOrWhiteSpace(d.Name)) what = d.Name.Trim();
                else if (c.Type == "text") what = (d.Text ?? "").Replace("|", " ");
                else if (c.Type == "image") what = Path.GetFileNameWithoutExtension(d.Image ?? "");
                else what = HostOf(d.Url);
            }
            if (what.Length > 40) what = what.Substring(0, 39) + "…";
            return what.Length > 0 ? WidgetTypeName(c.Type) + ": " + what : WidgetTypeName(c.Type);
        }

        private static string HostOf(string url)
        {
            Uri u;
            return !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : "";
        }

        /// <summary>A new one's first look: text and pictures have no background or title; community widgets show their title.</summary>
        private void SetupCustomConfig(WidgetConfig c)
        {
            if (!IsCustomType(c.Type)) return;
            if (c.Custom == null) c.Custom = new CustomWidgetDef();
            if (c.Type == "text" || c.Type == "image") { c.Back = 2; c.HideTitle = true; }
            if (c.Type == "text" && string.IsNullOrEmpty(c.Custom.Text)) c.Custom.Text = Loc("LOC_KX_TileTextDefault", "Your text");
            if (c.Type == "data") { c.Custom.FontSize = 40; c.Custom.Align = 0; }
            if (c.Type == "web") c.Custom.RefreshMinutes = 30;
            if (c.Type == "feed") c.Custom.RefreshMinutes = 30;
        }

        /// <summary>Settings (or a file) loaded: the definition in range, and a picture carried in it written to the tiles folder.</summary>
        private void NormalizeCustom(WidgetConfig c)
        {
            if (!IsCustomType(c.Type)) { c.Custom = null; return; }
            if (c.Custom == null) { c.Custom = new CustomWidgetDef(); SetupCustomConfig(c); }
            var d = c.Custom;
            d.Name = Clip(d.Name, 80);
            d.Text = Clip(d.Text, 600);
            d.Template = Clip(d.Template, 400);
            d.Url = SafeUrl(d.Url) ?? "";
            if (double.IsNaN(d.FontSize) || d.FontSize < 12 || d.FontSize > 200) d.FontSize = 48;
            d.Align = Math.Max(0, Math.Min(2, d.Align));
            d.Fit = Math.Max(0, Math.Min(1, d.Fit));
            d.RefreshMinutes = Math.Max(1, Math.Min(1440, d.RefreshMinutes <= 0 ? 15 : d.RefreshMinutes));
            d.Items = Math.Max(1, Math.Min(10, d.Items <= 0 ? 5 : d.Items));
            d.ViewWidth = Math.Max(640, Math.Min(2560, d.ViewWidth <= 0 ? 1280 : d.ViewWidth));
            if (!string.IsNullOrEmpty(d.Image) && !Path.IsPathRooted(d.Image)) d.Image = Path.GetFileName(d.Image);   // (only ever a name in our folder)
            if (d.ImageData != null) { WriteCarriedImage(d); }
        }

        private static string Clip(string s, int max) { s = s ?? ""; return s.Length > max ? s.Substring(0, max) : s; }

        /// <summary>An address a widget may use: http or https only (no files, no scripts). Null when it isn't one.</summary>
        internal static string SafeUrl(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (!s.Contains("://")) s = "https://" + s;
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            if (string.IsNullOrEmpty(u.Host) || s.Length > 2000) return null;
            return u.AbsoluteUri;
        }

        // ---------------- pictures ----------------

        /// <summary>A copy of the picture in the tiles folder: its file name (null if it couldn't be copied).</summary>
        private string CopyTileImage(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length > 40L * 1024 * 1024) return null;
                string ext = fi.Extension.ToLowerInvariant();
                if (Array.IndexOf(TileImageExts, ext) < 0) return null;
                Directory.CreateDirectory(TilesFolder);
                string name = "tile-" + DateTime.Now.Ticks + ext;
                File.Copy(file, Path.Combine(TilesFolder, name), true);
                return name;
            }
            catch (Exception ex) { logger.Warn("Kippix: picture tile: " + ex.Message); return null; }
        }

        /// <summary>A picture carried inside a layout / widget file: written to the tiles folder (once: same picture, same file).</summary>
        private void WriteCarriedImage(CustomWidgetDef d)
        {
            string data = d.ImageData;
            d.ImageData = null;
            try
            {
                if (string.IsNullOrEmpty(data) || data.Length > 16 * 1024 * 1024) return;
                byte[] bytes = Convert.FromBase64String(data);
                string ext = Path.GetExtension(d.ImageName ?? "").ToLowerInvariant();
                if (Array.IndexOf(TileImageExts, ext) < 0) ext = ".png";
                string hash;
                using (var md5 = MD5.Create()) hash = BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                string name = "tile-" + hash + ext;
                Directory.CreateDirectory(TilesFolder);
                string path = Path.Combine(TilesFolder, name);
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                d.Image = name;
            }
            catch (Exception ex) { logger.Warn("Kippix: picture carried in a file: " + ex.Message); }
            finally { d.ImageName = null; }
        }

        /// <summary>A layout being saved to a file: its picture tiles carry their pictures (so a copy of the file works anywhere).</summary>
        private void EmbedTileImages(HelperSettings copy)
        {
            if (copy == null || copy.Widgets == null) return;
            foreach (var c in copy.Widgets)
                EmbedTileImage(c);
        }

        private void EmbedTileImage(WidgetConfig c)
        {
            try
            {
                if (c == null || c.Type != "image" || c.Custom == null) return;
                string path = TileImagePath(c.Custom.Image);
                if (path == null || !File.Exists(path) || new FileInfo(path).Length > 8L * 1024 * 1024) return;   // (a huge one stays out: the file would be too big to share)
                c.Custom.ImageData = Convert.ToBase64String(File.ReadAllBytes(path));
                c.Custom.ImageName = Path.GetFileName(path);
            }
            catch (Exception ex) { logger.Warn("Kippix: carrying a tile's picture: " + ex.Message); }
        }

        /// <summary>A layout loaded: pictures it carries go to the tiles folder.</summary>
        private void RestoreTileImages()
        {
            if (settings.Widgets == null) return;
            foreach (var c in settings.Widgets)
                if (c != null && c.Custom != null && c.Custom.ImageData != null) WriteCarriedImage(c.Custom);
        }

        // ---------------- showing them ----------------

        private static TimeSpan CustomMaxAge(WidgetConfig c)
        {
            if (c.Type == "text" || c.Type == "image") return TimeSpan.FromDays(1);
            int m = c.Custom != null ? c.Custom.RefreshMinutes : 15;
            if (c.Type == "web") m = Math.Max(5, m);
            return TimeSpan.FromMinutes(Math.Max(1, m));
        }

        /// <summary>Your own tiles and the community widgets: filled here. False for the others.</summary>
        private bool RefreshCustomWidget(WidgetItem w, int gen)
        {
            var c = w.Config;
            if (c == null || !IsCustomType(c.Type)) return false;
            if (c.Custom == null) { c.Custom = new CustomWidgetDef(); SetupCustomConfig(c); }
            w.ExtrasChanged();
            switch (c.Type)
            {
                case "text":
                    w.Big = (c.Custom.Text ?? "").Replace("|", "\n");
                    w.Status = w.Big.Length == 0 ? Loc("LOC_KX_TileTextEmpty", "No text yet: Settings › Home settings › Widgets") : "";
                    return true;
                case "image":
                    FillTileImage(w, gen);
                    return true;
            }
            RefreshCommunityWidget(w, gen);
            return true;
        }

        private void FillTileImage(WidgetItem w, int gen)
        {
            string path = TileImagePath(w.Config.Custom.Image);
            if (path == null) { w.Photo = null; w.Status = Loc("LOC_KX_TileImageNone", "No picture yet: choose one on its page (Settings › Home settings › Widgets)"); return; }
            int width = (int)Math.Max(320, Math.Min(2400, Math.Max(w.Width, w.DesignWidth) * 1.5));
            KippixShared.SafeWork.Queue(_ =>
            {
                ImageSource img = null;
                try { if (File.Exists(path)) img = Images.LoadUncached(path, width); } catch (Exception) { }
                OnUi(() =>
                {
                    if (gen != w.Gen) return;
                    w.Photo = img;
                    w.Status = img != null ? "" : Loc("LOC_KX_TileImageMissing", "Picture not found: choose it again on this widget's page");
                });
            });
        }

        // ---------------- adding them ----------------

        /// <summary>Adding one of these: what it shows is asked first (the text, the picture, the address), then it's placed. False for the others.</summary>
        private bool AddCustomWidget(string type)
        {
            if (!IsCustomType(type)) return false;
            if (IsCommunityType(type) && !settings.CommunityWidgetsOn) { PlaySound("back"); return true; }
            PlaySound("select");
            var c = new WidgetConfig { Id = Guid.NewGuid().ToString("N"), Type = type, Scale = 1 };
            SetupCustomConfig(c);
            switch (type)
            {
                case "text":
                    OpenKeyboard(Loc("LOC_KX_TileTextAsk", "The text for the tile (| starts a new line)"), "", typed =>
                    {
                        if (string.IsNullOrWhiteSpace(typed)) return;
                        c.Custom.Text = Clip(typed.Trim(), 600);
                        AddWidgetConfig(type, null, c);
                    }, null, null, 600);
                    return true;
                case "image":
                    OpenPicker(Loc("LOC_KX_TileImageAsk", "Choose a picture for the tile"), false, TileImageExts, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "widgetadd", file =>
                    {
                        string name = CopyTileImage(file);
                        if (name == null) { PlaySound("back"); ShowNoticeSafe(Loc("LOC_KX_WgHead", "WIDGETS"), Loc("LOC_KX_TileImageBad", "That picture couldn't be used"), Path.GetFileName(file)); GoBackTo("widgets", "widgetadd"); return; }
                        c.Custom.Image = name;
                        AddWidgetConfig(type, null, c);
                    });
                    return true;
            }
            AskCommunityAddress(c, () => AddWidgetConfig(type, null, c));
            return true;
        }

        private void ShowNoticeSafe(string head, string title, string line)
        {
            try { ShowNotice(head, title, line ?? "", "", null); } catch (Exception) { }
        }

        /// <summary>The Board's list of new widgets: your own tiles too (and the community ones while they're on).</summary>
        private IEnumerable<string> BoardCustomTypes()
        {
            yield return "text";
            yield return "image";
            if (settings.CommunityWidgetsOn) { yield return "web"; yield return "feed"; yield return "data"; }
        }

        // ---------------- the rows on the widget pages ----------------

        private ObservableCollection<PickRow> editWidgetCustomRows, widgetAddMoreRows, communityWidgetRows;
        /// <summary>A widget's page: what your own tile / community widget shows.</summary>
        public ObservableCollection<PickRow> EditWidgetCustomRows { get { return editWidgetCustomRows ?? (editWidgetCustomRows = new ObservableCollection<PickRow>()); } }
        /// <summary>Add a widget: your own tiles, the community widgets, a widget file.</summary>
        public ObservableCollection<PickRow> WidgetAddMoreRows { get { return widgetAddMoreRows ?? (widgetAddMoreRows = new ObservableCollection<PickRow>()); } }
        /// <summary>The Widgets page: a note that opens Settings › Community › Widgets (the switch, web snippets, widget files).</summary>
        public ObservableCollection<PickRow> CommunityWidgetRows { get { return communityWidgetRows ?? (communityWidgetRows = new ObservableCollection<PickRow>()); } }

        private void BuildWidgetAddMoreRows()
        {
            var rows = WidgetAddMoreRows;
            rows.Clear();
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecOwnTiles", "YOUR OWN TILES") });
            rows.Add(new PickRow { Key = "addtext", Icon = "note", Text = WidgetTypeName("text"), Sub = Loc("LOC_KX_WgTextSub", "Words of your own: type them, then choose the size, colour and background"), Command = new RelayCommand(_ => AddWidget("text")) });
            rows.Add(new PickRow { Key = "addimage", Icon = "picture", Text = WidgetTypeName("image"), Sub = Loc("LOC_KX_WgImageSub", "A picture from your PC"), Command = new RelayCommand(_ => AddWidget("image")) });
            // web snippets, news feeds, data widgets, widget files and the gallery: Settings › Community › Widgets (ThemeApi.CommunityHub.cs)
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecCommunity", "COMMUNITY") });
            rows.Add(CommunityNoteRow("widgets", Loc("LOC_KX_CommunityNoteWidgets", "Web snippets, news feeds, data widgets, widget files and widgets other people made")));
        }

        private void BuildCommunityWidgetRows()
        {
            var rows = CommunityWidgetRows;
            rows.Clear();
            // (the community widgets' switch, web snippets / news feeds / data widgets and widget files live in Settings › Community › Widgets)
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecCommunity", "COMMUNITY") });
            rows.Add(CommunityNoteRow("widgets", Loc("LOC_KX_CommunityNoteWidgets", "Web snippets, news feeds, data widgets, widget files and widgets other people made")));
        }

        /// <summary>Called when the widget pages open (and after a change on them).</summary>
        private void BuildCustomWidgetRows()
        {
            BuildCommunityWidgetRows();
            BuildWidgetAddMoreRows();
            var rows = EditWidgetCustomRows;
            rows.Clear();
            var w = editingWidget;
            if (w == null || w.Config == null || !IsCustomType(w.Config.Type)) return;
            var c = w.Config;
            if (c.Custom == null) { c.Custom = new CustomWidgetDef(); SetupCustomConfig(c); }
            var d = c.Custom;
            Action again = BuildCustomWidgetRows;
            Action<string> changed = key =>
            {
                w.Title = WidgetTitleOf(c);
                w.Name = WidgetFullName(c);
                ApplyWidgetLook(w);
                w.ExtrasChanged();
                SaveSettings();
                RefreshWidget(w);
                Notify("EditWidgetName");
                Notify("OptionsTitle");
                BoardRowsAgain(again, key);
            };
            string head = c.Type == "text" ? Loc("LOC_KX_SecTileText", "TEXT") : c.Type == "image" ? Loc("LOC_KX_SecTilePicture", "PICTURE") : WidgetTypeName(c.Type).ToUpperInvariant();
            rows.Add(new PickRow { IsHeader = true, Text = head });
            if (IsCommunityType(c.Type) && !settings.CommunityWidgetsOn)
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "communityoff", Text = Loc("LOC_KX_CommunityWidgetsOffEdit3", "Community widgets are off (Community › Yours), so this one doesn't go online"),
                                       Command = new RelayCommand(_ => OpenCommunityAt(null, "cw:on", false)) });
            if (c.Type == "text")
                rows.Add(new PickRow
                {
                    Key = "tiletext", Icon = "note", Text = Loc("LOC_KX_TileTextRow", "Text"), Sub = CustomFullName(c).Replace(WidgetTypeName("text") + ": ", ""),
                    Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_TileTextAsk", "The text for the tile (| starts a new line)"), d.Text ?? "", typed =>
                    {
                        if (typed == null) return;
                        d.Text = Clip(typed, 600);
                        changed("tiletext");
                    }, null, null, 600))
                });
            if (c.Type == "image")
            {
                rows.Add(new PickRow
                {
                    Key = "tileimage", Icon = "picture", Text = Loc("LOC_KX_TileImageRow", "Picture"),
                    Sub = string.IsNullOrEmpty(d.Image) ? Loc("LOC_KX_TileImageNoneShort", "None yet") : d.Image,
                    Command = new RelayCommand(_ =>
                    {
                        PlaySound("select");
                        OpenPicker(Loc("LOC_KX_TileImageAsk", "Choose a picture for the tile"), false, TileImageExts, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "widgetedit", file =>
                        {
                            string name = CopyTileImage(file);
                            if (name != null) { d.Image = name; SaveSettings(); RefreshWidget(w); w.Name = WidgetFullName(c); }
                            else ShowNoticeSafe(Loc("LOC_KX_WgHead", "WIDGETS"), Loc("LOC_KX_TileImageBad", "That picture couldn't be used"), Path.GetFileName(file));
                            GoBackTo("widgetedit", "tileimage");
                        });
                    })
                });
            }
            if (IsCommunityType(c.Type))
                rows.Add(new PickRow
                {
                    Key = "wurl", Icon = "link", Text = Loc("LOC_KX_WgAddress", "Address"), Sub = string.IsNullOrEmpty(d.Url) ? Loc("LOC_KX_WgAddressNone", "Not set") : d.Url,
                    Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_WgAddressAsk", "The address (https://…)"), d.Url ?? "", typed =>
                    {
                        string u = SafeUrl(typed);
                        if (u == null) { PlaySound("back"); ShowNoticeSafe(Loc("LOC_KX_WgHead", "WIDGETS"), Loc("LOC_KX_WgAddressBad", "That isn't a web address (http or https)"), typed); return; }
                        d.Url = u;
                        changed("wurl");
                    }, null, null, 2000))
                });
            if (c.Type == "data")
                rows.Add(new PickRow
                {
                    Key = "wtemplate", Icon = "hash", Text = Loc("LOC_KX_WgTemplate", "What it shows"),
                    Sub = string.IsNullOrEmpty(d.Template) ? Loc("LOC_KX_WgTemplateNone", "Fields in braces by their path, e.g. {price} – {status.text}") : d.Template,
                    Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_WgTemplateAsk", "Words and {fields}: e.g. {data.price} – {data.status}"), d.Template ?? "", typed =>
                    {
                        if (typed == null) return;
                        d.Template = Clip(typed, 400);
                        changed("wtemplate");
                    }, null, null, 400))
                });
            if (c.Type == "text" || c.Type == "data")
            {
                rows.Add(new PickRow
                {
                    Key = "tilesize", Icon = "zoomin", Text = Loc("LOC_KX_TileFontSize", "Text size"), Sub = string.Format(Loc("LOC_KX_PxValue", "{0} px"), Math.Round(d.FontSize)),
                    Command = new RelayCommand(_ =>
                    {
                        int i = Array.FindIndex(FontSizes, s => s > d.FontSize + 0.5);
                        d.FontSize = i < 0 ? FontSizes[0] : FontSizes[i];
                        PlaySound("move");
                        changed("tilesize");
                    })
                });
                rows.Add(new PickRow
                {
                    Key = "tilealign", Icon = "sort", Text = Loc("LOC_KX_TileAlign", "Alignment"),
                    Sub = d.Align == 0 ? Loc("LOC_KX_AlignLeft", "Left") : d.Align == 2 ? Loc("LOC_KX_AlignRight", "Right") : Loc("LOC_KX_AlignCentre", "Centre"),
                    Command = new RelayCommand(_ => { d.Align = (d.Align + 1) % 3; PlaySound("move"); changed("tilealign"); })
                });
                rows.Add(BoardSwitch("tag", "tilebold", Loc("LOC_KX_TileBold", "Bold"), d.Bold, Loc("LOC_KX_On", "On"), Loc("LOC_KX_Off", "Off"),
                                     () => { d.Bold = !d.Bold; PlaySound("move"); changed("tilebold"); }));
            }
            if (c.Type == "image" || c.Type == "web")
                rows.Add(new PickRow
                {
                    Key = "tilefit", Icon = "shape", Text = Loc("LOC_KX_TileFit", "Fit"),
                    Sub = d.Fit == 1 ? Loc("LOC_KX_TileFitFill", "Fills the tile (the edges may be cut off)") : Loc("LOC_KX_TileFitWhole", "The whole picture"),
                    Command = new RelayCommand(_ => { d.Fit = 1 - d.Fit; PlaySound("move"); changed("tilefit"); })
                });
            if (c.Type == "web")
                rows.Add(new PickRow
                {
                    Key = "wwidth", Icon = "desktop", Text = Loc("LOC_KX_WgPageWidth", "Page width"), Sub = string.Format(Loc("LOC_KX_PxValue", "{0} px"), d.ViewWidth),
                    Command = new RelayCommand(_ =>
                    {
                        int[] ws = { 800, 1024, 1280, 1600, 1920 };
                        int i = Array.FindIndex(ws, x => x > d.ViewWidth);
                        d.ViewWidth = i < 0 ? ws[0] : ws[i];
                        PlaySound("move");
                        changed("wwidth");
                    })
                });
            if (c.Type == "feed")
                rows.Add(new PickRow
                {
                    Key = "witems", Icon = "news", Text = Loc("LOC_KX_WgHeadlines", "Headlines"), Sub = d.Items.ToString(CultureInfo.CurrentCulture),
                    Command = new RelayCommand(_ => { d.Items = d.Items >= 8 ? 2 : d.Items + 1; PlaySound("move"); changed("witems"); })
                });
            if (IsCommunityType(c.Type))
            {
                rows.Add(new PickRow
                {
                    Key = "wrefresh", Icon = "refresh", Text = Loc("LOC_KX_WgRefreshEvery", "Fetched again every"), Sub = MinutesText(Math.Max(c.Type == "web" ? 5 : 1, d.RefreshMinutes)),
                    Command = new RelayCommand(_ =>
                    {
                        int i = Array.FindIndex(RefreshChoices, x => x > d.RefreshMinutes);
                        d.RefreshMinutes = i < 0 ? RefreshChoices[0] : RefreshChoices[i];
                        if (c.Type == "web" && d.RefreshMinutes < 5) d.RefreshMinutes = 5;
                        PlaySound("move");
                        SaveSettings();
                        BoardRowsAgain(again, "wrefresh");
                    })
                });
                rows.Add(new PickRow
                {
                    Key = "wnow", Icon = "refresh", Text = Loc("LOC_KX_WgRefreshNow", "Fetch it now"), Sub = Loc("LOC_KX_WgRefreshNowSub", "Only goes online while it's on screen"),
                    Command = new RelayCommand(_ => { PlaySound("select"); RefreshWidget(w); })
                });
            }
            rows.Add(new PickRow
            {
                Key = "wname", Icon = "tag", Text = Loc("LOC_KX_WgNameRow", "Title"), Sub = string.IsNullOrWhiteSpace(d.Name) ? WidgetTypeName(c.Type) : d.Name,
                Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_WgNameAsk", "A title for this widget (empty: its kind's name)"), d.Name ?? "", typed =>
                {
                    if (typed == null) return;
                    d.Name = Clip(typed.Trim(), 80);
                    changed("wname");
                }, null, null, 80))
            });
            rows.Add(new PickRow
            {
                Key = "wexport", Icon = "upload", Text = Loc("LOC_KX_WidgetShareRow", "Share this widget"),
                Sub = Loc("LOC_KX_WidgetShareRowSub2", "Saves it with a picture, ready for the community gallery (more in Community)"),
                Command = new RelayCommand(_ => ShareToCommunity("widgets", w))
            });
        }

        private string MinutesText(int m)
        {
            if (m < 60) return m == 1 ? Loc("LOC_KX_OneMinute", "1 minute") : string.Format(Loc("LOC_KX_NMinutes", "{0} minutes"), m);
            int h = m / 60;
            return h == 1 ? Loc("LOC_KX_OneHour", "1 hour") : string.Format(Loc("LOC_KX_NHours", "{0} hours"), h);
        }
    }
}

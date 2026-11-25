using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.7) Media › the edit button › Manually add source: a website of your own (its address, then its name) joins the
    /// streaming services. It has the globe icon until you give it a picture of your own. In edit mode, A on one of your own
    /// opens its page (name, address, icon, move, remove); X shows or hides it like the others. Each one can go in the top bar
    /// (Settings › Top bar), off until you switch it on, and moves there like any other shortcut. Keys: "custom:" + 8 letters.
    /// </summary>
    public partial class ThemeApi
    {
        private string mediaSrcKey;   // the page "mediasrc": which of your own it's about

        internal static bool IsCustomMediaKey(string key) { return key != null && key.StartsWith("custom:", StringComparison.Ordinal); }

        private List<MediaSource> MediaCustom { get { return settings.MediaCustom ?? (settings.MediaCustom = new List<MediaSource>()); } }

        private MediaSource CustomSource(string key) { return MediaCustom.FirstOrDefault(c => c != null && c.Key == key); }

        /// <summary>The built-in services (with your changes: Media › X), then your own.</summary>
        private IEnumerable<MediaService> AllMediaServices
        {
            get
            {
                var looks = settings.MediaLooks ?? new List<MediaSource>();
                var builtIn = MediaServices.Select(m =>
                {
                    var l = looks.FirstOrDefault(x => x != null && x.Key == m.Key);
                    if (l == null) return m;
                    return new MediaService { Key = m.Key, Name = string.IsNullOrEmpty(l.Name) ? m.Name : l.Name, Package = m.Package, InTheme = m.InTheme,
                                              Web = string.IsNullOrEmpty(l.Web) ? m.Web : l.Web, Icon = l.Icon };
                });
                var mine = MediaCustom.Where(c => c != null && IsCustomMediaKey(c.Key) && !string.IsNullOrEmpty(c.Web))
                                      .Select(c => new MediaService { Key = c.Key, Name = string.IsNullOrEmpty(c.Name) ? c.Web : c.Name, Web = c.Web, Custom = true, Icon = c.Icon });
                return builtIn.Concat(mine);
            }
        }

        // ---------------- (1.1.0) each tile's look: name, address, icon and background ----------------

        /// <summary>Your changes to a tile: one of your own sites, or (made on the first change) a built-in one / Shows &amp; movies.</summary>
        private MediaSource MediaLook(string key, bool create)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (IsCustomMediaKey(key)) return CustomSource(key);
            var looks = settings.MediaLooks ?? (settings.MediaLooks = new List<MediaSource>());
            var l = looks.FirstOrDefault(x => x != null && x.Key == key);
            if (l == null && create) { l = new MediaSource { Key = key }; looks.Add(l); }
            return l;
        }

        /// <summary>The name you gave a built-in tile, or null.</summary>
        private string MediaLookName(string key)
        {
            var l = IsCustomMediaKey(key) ? null : MediaLook(key, false);
            return l != null && !string.IsNullOrEmpty(l.Name) ? l.Name : null;
        }

        private bool MediaWebChanged(string key)
        {
            var l = IsCustomMediaKey(key) ? null : MediaLook(key, false);
            return l != null && !string.IsNullOrEmpty(l.Web);
        }

        private static readonly Color[] MediaTileColours =
        {
            Color.FromRgb(0x2D, 0x23, 0x42), Color.FromRgb(0x1F, 0x2A, 0x45), Color.FromRgb(0x1C, 0x35, 0x40), Color.FromRgb(0x3A, 0x2A, 0x1E),
            Color.FromRgb(0x21, 0x28, 0x44), Color.FromRgb(0x30, 0x1F, 0x45), Color.FromRgb(0x1D, 0x3A, 0x2A), Color.FromRgb(0x3A, 0x32, 0x1E)
        };

        /// <summary>
        /// A tile's background: your colour or picture (Media › X), else Kippix's picture for it (Images/MediaTiles: Netflix, Disney+,
        /// Crunchyroll, Paramount+, HBO Max, Shows &amp; movies), else a colour by where it is.
        /// </summary>
        private Brush MediaTileBrush(string key, int index)
        {
            try
            {
                var l = MediaLook(key, false);
                string bg = l != null ? l.Bg : null;
                if (!string.IsNullOrEmpty(bg))
                {
                    var c = bg.StartsWith("#", StringComparison.Ordinal) ? ParseColour(bg) : null;
                    if (c != null) return FrozenSolid(c.Value);
                    var mine = File.Exists(bg) ? MediaTilePicture(bg) : null;
                    if (mine != null) return mine;
                }
                string own = Path.Combine(ThemeDir, "Images", "MediaTiles", key + ".jpg");
                if (!IsCustomMediaKey(key) && File.Exists(own))
                {
                    var b = MediaTilePicture(own);
                    if (b != null) return b;
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: media tile: " + ex.Message); }
            return FrozenSolid(MediaTileColours[Math.Abs(index) % MediaTileColours.Length]);
        }

        private static Brush MediaTilePicture(string file)
        {
            var img = Images.Load(file, 960);
            if (img == null) return null;
            var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            b.Freeze();
            return b;
        }

        /// <summary>Media settings › Tile size: Small, Medium (to start with), Large; 16:9.</summary>
        /// <summary>
        /// (1.1.0) The tiles fill the width on any screen: 5 across (small), 4 (medium) or 3 (large), sized from the width the
        /// tiles have (KX_MediaTileList, followed as it changes), 16:9, with 28 between them.
        /// </summary>
        public int MediaTileCols { get { return settings.MediaTileSize == 0 ? 5 : settings.MediaTileSize == 2 ? 3 : 4; } }
        public double MediaTileWidth
        {
            get
            {
                double w = mediaAreaW > 300 ? mediaAreaW : 1716;
                return Math.Max(160, Math.Floor((w - MediaTileCols * 28 - 1) / MediaTileCols));
            }
        }
        public double MediaTileHeight { get { return Math.Round(MediaTileWidth * 9 / 16); } }

        private double mediaAreaW;
        private FrameworkElement mediaAreaHooked;

        /// <summary>Follows the tiles' width (the first layout, a new window size), so they always fill it.</summary>
        private void HookMediaArea()
        {
            try
            {
                var list = UiTools.FindByName(Application.Current.MainWindow, "KX_MediaTileList") as FrameworkElement;
                if (list == null || ReferenceEquals(list, mediaAreaHooked)) return;
                mediaAreaHooked = list;
                list.SizeChanged += (s, e) =>
                {
                    if (Math.Abs(e.NewSize.Width - mediaAreaW) < 1) return;
                    mediaAreaW = e.NewSize.Width;
                    Notify("MediaTileWidth");
                    Notify("MediaTileHeight");
                };
                if (list.ActualWidth > 300 && Math.Abs(list.ActualWidth - mediaAreaW) >= 1)
                {
                    mediaAreaW = list.ActualWidth;
                    Notify("MediaTileWidth");
                    Notify("MediaTileHeight");
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: media tiles' width: " + ex.Message); }
        }
        private string MediaTileSizeName { get { return settings.MediaTileSize == 0 ? Loc("LOC_KX_SizeSmall", "Small") : settings.MediaTileSize == 2 ? Loc("LOC_KX_SizeLarge", "Large") : Loc("LOC_KX_SizeMedium", "Medium"); } }

        private void CycleMediaTileSize()
        {
            settings.MediaTileSize = (settings.MediaTileSize + 1) % 3;
            SaveSettings();
            PlaySound("move");
            Notify("MediaTileWidth");
            Notify("MediaTileHeight");
            var row = ProfileRows.FirstOrDefault(r => r.Key == "tilesize");
            if (row != null) row.SetSub(MediaTileSizeName);
        }

        private string MediaIconFolder { get { return Path.Combine(plugin.GetPluginUserDataPath(), "MediaIcons"); } }

        /// <summary>An address as typed: https:// added when there's none; null when it isn't a website.</summary>
        private static string CleanMediaAddress(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) text = "https://" + text;
            Uri u;
            if (!Uri.TryCreate(text, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
            if (u.Host.Length == 0 || (u.Host.IndexOf('.') < 0 && u.Host != "localhost")) return null;
            return u.AbsoluteUri;
        }

        /// <summary>A name to start from: the address's site, without www. and the ending (twitch.tv: Twitch).</summary>
        private static string MediaNameFromAddress(string web)
        {
            try
            {
                string host = new Uri(web).Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
                int dot = host.IndexOf('.');
                string n = dot > 0 ? host.Substring(0, dot) : host;
                return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n.Substring(1) : host;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>Media settings › Add your own site.</summary>
        private PickRow MediaAddRow()
        {
            return new PickRow
            {
                Key = "mediaadd", Icon = "add", Text = Loc("LOC_KX_MediaAddSite", "Add your own site"),
                Sub = Loc("LOC_KX_MediaAddSiteSub", "Its address and name, then its icon and background"),
                Command = new RelayCommand(_ => AddMediaSource())
            };
        }

        private void AddMediaSource()
        {
            PlaySound("select");
            OpenKeyboard(Loc("LOC_KX_MediaAskAddress", "The website's address"), "https://", typed =>
            {
                string web = CleanMediaAddress(typed);
                if (web == null)
                {
                    if ((typed ?? "").Trim().Length > 0 && (typed ?? "").Trim() != "https://")
                        try { ShowNotice(Loc("LOC_KX_MediaHeader", "MEDIA"), Loc("LOC_KX_MediaBadAddress", "That isn't a website's address"), (typed ?? "").Trim(), "", null); } catch (Exception) { }
                    ProfileFocusKey("mediaadd");
                    return;
                }
                ui.BeginInvoke(new Action(() =>
                    OpenKeyboard(Loc("LOC_KX_MediaAskName", "Its name"), MediaNameFromAddress(web), named =>
                    {
                        string name = (named ?? "").Trim();
                        if (name.Length == 0) name = MediaNameFromAddress(web);
                        var c = new MediaSource { Key = "custom:" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = name, Web = web, Icon = "", Bg = "" };
                        MediaCustom.Add(c);
                        SaveSettings();
                        BuildShortcuts();   // (it's in Settings › Top bar now, off)
                        PlaySound("select");
                        // (1.1.0) straight on to its page: its icon and background
                        if (openPanel == "profile") OpenMediaTilePage(c.Key, "mediaset");
                    }, null, () => ProfileFocusKey("mediaadd"), 60)), DispatcherPriority.Loaded);
            }, null, () => ProfileFocusKey("mediaadd"), 400);
        }

        // ---------------- a tile's page (X on it, A on one of your own in edit mode) ----------------

        private string mediaSrcFrom = "media";   // where B goes back to: "media" or "mediaset"

        private void OpenMediaTilePage(string key, string from)
        {
            if (!IsMediaRowKey(key)) return;
            mediaSrcKey = key;
            mediaSrcFrom = from;
            SetProfilePage("mediasrc");
        }

        /// <summary>X on a Media tile: its page (Name, Address, Icon, Background, Move it, Hide this tile; Remove it for your own).</summary>
        private bool MediaTileEditKey()
        {
            if (openPanel != "profile" || profilePage != "media" || IsProfileReorder) return false;
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var row = fe != null ? fe.DataContext as PickRow : null;
            if (row == null || !IsMediaRowKey(row.Key)) return false;
            OpenMediaTilePage(row.Key, "media");
            return true;
        }

        private string MediaDefaultName(string key)
        {
            if (key == "localmedia") return Loc("LOC_KX_LocalMedia", "Shows & movies");
            var m = MediaServices.FirstOrDefault(x => x.Key == key);
            return m != null ? m.Name : "";
        }

        private string MediaNameNow(string key)
        {
            if (IsCustomMediaKey(key)) { var c = CustomSource(key); return c != null ? c.Name : ""; }
            return MediaLookName(key) ?? MediaDefaultName(key);
        }

        private string MediaWebNow(string key)
        {
            if (IsCustomMediaKey(key)) { var c = CustomSource(key); return c != null ? c.Web : ""; }
            var l = MediaLook(key, false);
            if (l != null && !string.IsNullOrEmpty(l.Web)) return l.Web;
            var m = MediaServices.FirstOrDefault(x => x.Key == key);
            return m != null ? m.Web : "";
        }

        public string MediaSourceTitle { get { return mediaSrcKey != null ? MediaNameNow(mediaSrcKey) : Loc("LOC_KX_Media", "Media"); } }

        private void BuildMediaSourceRows()
        {
            string key = mediaSrcKey;
            if (key == null || !IsMediaRowKey(key)) return;
            bool custom = IsCustomMediaKey(key);
            var look = MediaLook(key, false);
            ProfileRows.Add(new PickRow
            {
                Key = "srcname", Icon = "keyboard", Text = Loc("LOC_KX_EditName", "Name"), Sub = MediaNameNow(key),
                Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_MediaAskName", "Its name"), MediaNameNow(key), text =>
                {
                    text = (text ?? "").Trim();
                    if (text == MediaNameNow(key) || (custom && text.Length == 0)) { ProfileFocusKey("srcname"); return; }
                    var l = MediaLook(key, true);
                    if (l == null) return;
                    l.Name = !custom && text == MediaDefaultName(key) ? "" : text;   // (empty: back to its own name)
                    MediaSourceChanged("srcname");
                }, null, () => ProfileFocusKey("srcname"), 60))
            });
            if (key != "localmedia")
                ProfileRows.Add(new PickRow
                {
                    Key = "srcweb", Icon = "link", Text = Loc("LOC_KX_MediaAddress", "Address"), Sub = MediaWebNow(key),
                    Command = new RelayCommand(_ => OpenKeyboard(Loc("LOC_KX_MediaAskAddress", "The website's address"), MediaWebNow(key), text =>
                    {
                        string web = CleanMediaAddress(text);
                        if (web == null && !custom && string.IsNullOrWhiteSpace(text)) web = "";   // (empty: back to its own address)
                        if (web == null || web == MediaWebNow(key)) { ProfileFocusKey("srcweb"); return; }
                        var l = MediaLook(key, true);
                        if (l == null) return;
                        var m = MediaServices.FirstOrDefault(x => x.Key == key);
                        l.Web = !custom && m != null && web == m.Web ? "" : web;
                        MediaSourceChanged("srcweb");
                    }, null, () => ProfileFocusKey("srcweb"), 400))
                });
            // the icon: the same picture in the top bar and Settings › Top bar
            bool ownIcon = look != null && !string.IsNullOrEmpty(look.Icon) && File.Exists(look.Icon);
            var iconRow = new PickRow
            {
                Key = "srcicon", Icon = "picture", Text = Loc("LOC_KX_MediaIcon", "Icon"),
                Sub = ownIcon ? Loc("LOC_KX_MediaIconOwn", "Your own picture") : custom ? Loc("LOC_KX_MediaIconDefault", "The globe") : Loc("LOC_KX_MediaIconKippix", "Kippix's icon"),
                Command = new RelayCommand(_ => PickMediaFile(key, false))
            };
            ProfileRows.Add(iconRow);
            if (!ownIcon && !custom)
                try
                {
                    // the icon it has in the top bar now
                    string kip = Path.Combine(ThemeDir, "Images", key == "localmedia" ? "TopBar" : "Streaming", key + ".png");
                    if (File.Exists(kip)) iconRow.Picture = Images.Load(kip, 128);
                }
                catch (Exception) { }
            if (ownIcon)
            {
                try { iconRow.Picture = Images.Load(look.Icon, 64); } catch (Exception) { }
                ProfileRows.Add(new PickRow
                {
                    Key = "srcicondefault", Icon = custom ? "globe" : "refresh", Text = custom ? Loc("LOC_KX_MediaIconUseDefault", "Use the globe instead") : Loc("LOC_KX_MediaIconUseKippix", "Use Kippix's icon"),
                    Command = new RelayCommand(_ =>
                    {
                        var l = MediaLook(key, false);
                        if (l == null) return;
                        DeleteMediaIcon(l.Icon);
                        l.Icon = "";
                        MediaSourceChanged("srcicon");
                    })
                });
            }
            // the background: a colour or a picture of your own, or Kippix's
            string bg = look != null ? look.Bg : null;
            bool bgColour = !string.IsNullOrEmpty(bg) && bg.StartsWith("#", StringComparison.Ordinal);
            bool bgPicture = !string.IsNullOrEmpty(bg) && !bgColour && File.Exists(bg);
            var colourRow = new PickRow
            {
                Key = "srcbgcolour", Icon = "colour", Text = Loc("LOC_KX_MediaBgColour", "Background colour"),
                Sub = bgColour ? bg.ToUpperInvariant() : Loc("LOC_KX_MediaBgColourSub", "Pick a colour for the tile"),
                Command = new RelayCommand(_ =>
                {
                    var start = bgColour ? ParseColour(bg) : null;
                    OpenColourPicker(Loc("LOC_KX_MediaBgColour", "Background colour"), start ?? ThemeColourNow, c =>
                    {
                        var l = MediaLook(key, true);
                        if (l == null) return;
                        DeleteMediaIcon(l.Bg);
                        l.Bg = ColourToHex(c);
                        MediaSourceChanged("srcbgcolour");
                    });
                })
            };
            ProfileRows.Add(colourRow);
            var picRow = new PickRow
            {
                Key = "srcbgpic", Icon = "gallery", Text = Loc("LOC_KX_MediaBgPicture", "Background picture"),
                Sub = bgPicture ? Loc("LOC_KX_MediaIconOwn", "Your own picture") : Loc("LOC_KX_MediaBgPictureSub", "A picture of your own for the tile"),
                Command = new RelayCommand(_ => PickMediaFile(key, true))
            };
            ProfileRows.Add(picRow);
            if (!string.IsNullOrEmpty(bg))
                ProfileRows.Add(new PickRow
                {
                    Key = "srcbgdefault", Icon = "refresh", Text = Loc("LOC_KX_MediaBgDefault", "Use the default background"),
                    Command = new RelayCommand(_ =>
                    {
                        var l = MediaLook(key, false);
                        if (l == null) return;
                        DeleteMediaIcon(l.Bg);
                        l.Bg = "";
                        MediaSourceChanged("srcbgcolour");
                    })
                });
            bool isHidden = IsMediaHidden(key);
            if (!isHidden)
                ProfileRows.Add(new PickRow
                {
                    Key = "srcmove", Icon = "move", Text = Loc("LOC_KX_MediaMoveIt", "Move it"), Sub = Loc("LOC_KX_MediaMoveItSub2", "Picked up on the Media page: the D-pad moves it, A puts it down"),
                    Command = new RelayCommand(_ => MoveTileFromPage(key))
                });
            // (1.1.0) hide it (Media settings lists the hidden ones to show them again)
            ProfileRows.Add(new PickRow
            {
                Key = "srchide", Icon = isHidden ? "check" : "eyeoff",
                Text = isHidden ? Loc("LOC_KX_MediaShowTile", "Show this tile") : Loc("LOC_KX_MediaHideTile", "Hide this tile"),
                Sub = isHidden ? null : Loc("LOC_KX_MediaHideTileSub", "Media settings (Y) lists it to show it again"),
                Command = new RelayCommand(_ =>
                {
                    SetMediaHidden(key, !isHidden);
                    PlaySound(isHidden ? "select" : "back");
                    if (!isHidden) { SetProfilePage("media", "mediasrc"); return; }   // hidden: back to the tiles, without it
                    MediaSourceChanged("srchide");
                })
            });
            if (custom)
            {
                ProfileRows.Add(new PickRow
                {
                    Key = "srcremove", Icon = "trash", Text = Loc("LOC_KX_MediaRemove", "Remove it"),
                    Command = new RelayCommand(_ => RemoveMediaSource(key))
                });
            }
        }

        /// <summary>Saved, the top bar told, the page drawn again on the same row.</summary>
        private void MediaSourceChanged(string focus)
        {
            SaveSettings();
            PlaySound("select");
            BuildShortcuts();
            if (openPanel == "profile" && profilePage == "mediasrc") { BuildProfileRows(); Notify("ProfileMenuTitle"); ProfileFocusKey(focus); }   // (the tiles too)
        }

        /// <summary>The file picker for a tile's icon (<paramref name="background"/> false) or background picture: a copy is kept.</summary>
        private void PickMediaFile(string key, bool background)
        {
            PlaySound("select");
            ClosePanels();
            TogglePanel("options");
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string from = mediaSrcFrom;
            string focus = background ? "srcbgpic" : "srcicon";
            Action back = () =>
            {
                ClosePanels();
                ui.BeginInvoke(new Action(() => { ToggleProfile(); mediaSrcKey = key; mediaSrcFrom = from; SetProfilePage("mediasrc"); ProfileFocusKey(focus); }), DispatcherPriority.Loaded);
            };
            string title = background ? Loc("LOC_KX_MediaBgChoose", "Choose its background") : Loc("LOC_KX_MediaIconChoose", "Choose its icon");
            OpenPicker(title, false, ProfilePicExts, pics, "", file =>
            {
                try
                {
                    if (Images.Load(file, 64) == null) throw new InvalidDataException("not a picture Windows can open");
                    var l = MediaLook(key, true);
                    if (l != null)
                    {
                        Directory.CreateDirectory(MediaIconFolder);
                        string dest = Path.Combine(MediaIconFolder, key.Replace(':', '-') + (background ? "-bg-" : "-") + DateTime.Now.Ticks + Path.GetExtension(file).ToLowerInvariant());
                        File.Copy(file, dest, true);
                        if (background) { DeleteMediaIcon(l.Bg); l.Bg = dest; }
                        else { DeleteMediaIcon(l.Icon); l.Icon = dest; }
                        SaveSettings();
                        BuildShortcuts();
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn("Kippix: media picture: " + ex.Message);
                    try { ShowNotice(Loc("LOC_KX_MediaHeader", "MEDIA"), Loc("LOC_KX_MediaIconFailed", "That picture couldn't be used"), Path.GetFileName(file), "", null); } catch (Exception) { }
                }
                back();
            }, back);
        }

        /// <summary>A copy Kippix made of your picture (only those: never a file of yours elsewhere).</summary>
        private void DeleteMediaIcon(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && path.StartsWith(MediaIconFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }

        private void RemoveMediaSource(string key)
        {
            var s = CustomSource(key);
            if (s == null) return;
            DeleteMediaIcon(s.Icon);
            DeleteMediaIcon(s.Bg);
            MediaCustom.Remove(s);
            if (settings.MediaOrder != null) settings.MediaOrder.Remove(key);
            if (settings.MediaHidden != null) settings.MediaHidden.Remove(key);
            if (settings.ExtraPins != null) settings.ExtraPins.Remove(key);
            if (settings.ShortcutOrder != null) settings.ShortcutOrder.Remove(key);
            if (settings.TopBarStayVisible != null) settings.TopBarStayVisible.Remove(key);
            SaveSettings();
            PlaySound("back");
            BuildShortcuts();
            try { UpdateShortcutStatus(); } catch (Exception) { }
            SetProfilePage("media", "mediasrc");
        }

        /// <summary>Move it (a tile's page): back on the tiles with it picked up, as if A was held on it.</summary>
        private void MoveTileFromPage(string key)
        {
            SetProfilePage("media", "mediasrc");
            ui.BeginInvoke(new Action(() =>
            {
                var row = mediaTiles.FirstOrDefault(r => r.Key == key);
                if (row == null || !MediaConsoleActive) return;
                mediaTileMoving = key;
                row.IsSelected = true;
                FocusMediaTile(key);
            }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>B on a tile's page: back where it came from.</summary>
        private bool MediaSourceBack()
        {
            if (openPanel != "profile" || profilePage != "mediasrc") return false;
            PlaySound("back");
            string key = mediaSrcKey;
            if (mediaSrcFrom == "mediaset") { SetProfilePage("mediaset", "mediasrc"); ProfileFocusKey("mediaadd"); }
            else { SetProfilePage("media", "mediasrc"); ProfileFocusKey(key); }
            return true;
        }
    }
}

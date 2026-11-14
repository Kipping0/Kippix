using System;
using System.Collections.Generic;
using System.Linq;

namespace KippixHelper
{
    /// <summary>
    /// (0.91) The presets' designs: three for each layout and three for the game page (ThemeApi.Presets.cs puts them on).
    /// Each starts from Kippix's own settings and sets what makes it: the cards, what's shown and where, the background, the
    /// look (colours, glow, fonts, corners) and, for some, widgets. Board and Manual board designs lay out their own tiles;
    /// Manual board's pick games from your library (the ones you play most) when the design goes on.
    /// </summary>
    public partial class ThemeApi
    {
        private void PresetInfo(string slot, int n, out string name, out string sub)
        {
            name = ""; sub = "";
            switch (slot + n)
            {
                case "row01": name = Loc("LOC_KX_PsRow1", "Cinema"); sub = Loc("LOC_KX_PsRow1Sub", "Wide art, a huge logo in the middle, nothing in the way"); break;
                case "row02": name = Loc("LOC_KX_PsRow2", "Neon arcade"); sub = Loc("LOC_KX_PsRow2Sub", "Glowing hexagons in Kippix purple and cyan, with a clock"); break;
                case "row03": name = Loc("LOC_KX_PsRow3", "Minimal"); sub = Loc("LOC_KX_PsRow3Sub", "Small square covers on the game's own tile, calm and clean"); break;
                case "row11": name = Loc("LOC_KX_PsTall1", "Gallery wall"); sub = Loc("LOC_KX_PsTall1Sub", "Five slim covers, soft blue light and slow motion"); break;
                case "row12": name = Loc("LOC_KX_PsTall2", "Poster"); sub = Loc("LOC_KX_PsTall2Sub", "Three huge posters with the logo and info over them"); break;
                case "row13": name = Loc("LOC_KX_PsTall3", "Hive"); sub = Loc("LOC_KX_PsTall3Sub", "Seven glowing hexagons across, Kippix colours"); break;
                case "row21": name = Loc("LOC_KX_PsFull1", "Panorama"); sub = Loc("LOC_KX_PsFull1Sub", "Four wide panels edge to edge, the details over them"); break;
                case "row22": name = Loc("LOC_KX_PsFull2", "Strips"); sub = Loc("LOC_KX_PsFull2Sub", "Eight slim covers on top, the logo and info underneath"); break;
                case "row23": name = Loc("LOC_KX_PsFull3", "Duo"); sub = Loc("LOC_KX_PsFull3Sub", "Two big wide panels and nothing else"); break;
                case "l11": name = Loc("LOC_KX_PsGrid1", "Library wall"); sub = Loc("LOC_KX_PsGrid1Sub", "Three rows of eight, badges on, the details tucked small"); break;
                case "l12": name = Loc("LOC_KX_PsGrid2", "Honeycomb"); sub = Loc("LOC_KX_PsGrid2Sub", "Hexagons fitted together in glowing Kippix colours"); break;
                case "l13": name = Loc("LOC_KX_PsGrid3", "Showroom"); sub = Loc("LOC_KX_PsGrid3Sub", "Two rows of wide art, a big logo and a clock"); break;
                case "l21": name = Loc("LOC_KX_PsList1", "Spotlight"); sub = Loc("LOC_KX_PsList1Sub", "Games down the left, the game big on the right"); break;
                case "l22": name = Loc("LOC_KX_PsList2", "Cinema strip"); sub = Loc("LOC_KX_PsList2Sub", "A slim strip of art on the right, the trailer playing behind"); break;
                case "l23": name = Loc("LOC_KX_PsList3", "Magazine"); sub = Loc("LOC_KX_PsList3Sub", "Four big entries, serif titles and warm colours"); break;
                case "l31": name = Loc("LOC_KX_PsBoard1", "Dashboard"); sub = Loc("LOC_KX_PsBoard1Sub", "Your last game big, favourites, new games, a clock and shortcuts"); break;
                case "l32": name = Loc("LOC_KX_PsBoard2", "Mosaic"); sub = Loc("LOC_KX_PsBoard2Sub", "Mixed shapes and sizes, tiles that change by themselves"); break;
                case "l33": name = Loc("LOC_KX_PsBoard3", "Shelves"); sub = Loc("LOC_KX_PsBoard3Sub", "Three tidy shelves: played, favourites, installed"); break;
                case "l41": name = Loc("LOC_KX_PsManual1", "Bento"); sub = Loc("LOC_KX_PsManual1Sub", "A bento box of your top games, a clock and shortcuts"); break;
                case "l42": name = Loc("LOC_KX_PsManual2", "Collage"); sub = Loc("LOC_KX_PsManual2Sub", "Your top games scattered like photos on a table"); break;
                case "l43": name = Loc("LOC_KX_PsManual3", "Launcher"); sub = Loc("LOC_KX_PsManual3Sub", "Six tall posters and a row of shortcuts"); break;
                case "game1": name = Loc("LOC_KX_PsGameD1", "Design 1"); sub = Loc("LOC_KX_PsGameDSub", "Starts as the game page comes: move things and it keeps them"); break;
                case "game2": name = Loc("LOC_KX_PsGameD2", "Design 2"); sub = Loc("LOC_KX_PsGameDSub", "Starts as the game page comes: move things and it keeps them"); break;
            }
        }

        // ---------------- looks ----------------

        private static LookStyle LookKippixNeon()
        {
            var l = new LookStyle
            {
                Name = "Kippix neon", Accent = "#5CE1E6", Text = "#FFFFFF", SubText = "#DCCBF5", Panel = "#160B24", PanelOpacity = 93, PanelCorner = 18,
                BgTint = "#2A0A44", BgTintAmount = 26, TitleFont = "Bahnschrift SemiBold", BodyFont = "Bahnschrift",
                CardCorner = 6, CardBorder = 30, CardBorderColor = "#5CE1E6", GlowStyle = 2, GlowSize = 26, GlowColor = "#9300C5",
                FocusScale = 112, FocusSpeed = 2, TopBarShade = 70, Highlight = 1, ButtonShape = 3
            };
            return l;
        }

        private static LookStyle LookCinema()
        {
            return new LookStyle
            {
                Name = "Cinema", Accent = "#FFFFFF", Text = "#FFFFFF", SubText = "#D6D6D6", Panel = "#0A0A0C", PanelOpacity = 95, PanelCorner = 14,
                BgTint = "#000000", BgTintAmount = 18, TitleFont = "Bahnschrift SemiBold", BodyFont = "Segoe UI",
                CardCorner = 4, CardBorder = 20, GlowStyle = 3, GlowSize = 30, GlowColor = "#000000", FocusScale = 110, FocusSpeed = 0,
                TopBarShade = 80, Highlight = 3, ButtonShape = 2
            };
        }

        private static LookStyle LookMinimal()
        {
            return new LookStyle
            {
                Name = "Minimal", Accent = "#FFFFFF", Text = "#F4F6FA", SubText = "#B9BFCC", Panel = "#0B0D14", PanelOpacity = 97, PanelCorner = 24,
                BgTint = "#0B0D14", BgTintAmount = 0, TitleFont = "tw:light",
                CardCorner = 18, CardBorder = 26, CardBorderColor = "#FFFFFF", GlowStyle = 0, FocusScale = 104, FocusSpeed = 1,
                TopBarShade = 30, Highlight = 2, ButtonShape = 1
            };
        }

        private static void SetLook(HelperSettings s, string builtin, LookStyle custom)
        {
            if (custom != null) { s.Look = custom; s.LookPack = "builtin:kippix"; return; }
            s.Look = BuiltInLook(builtin);
            s.LookPack = "builtin:" + builtin;
        }

        // ---------------- widgets and tiles ----------------

        private static WidgetConfig PWidget(HelperSettings s, string type, string subject, double x, double y, double scale, int back, bool hideTitle)
        {
            var c = new WidgetConfig { Id = Guid.NewGuid().ToString("N"), Type = type, Subject = subject, X = x, Y = y, Scale = scale, Back = back, HideTitle = hideTitle };
            s.Widgets.Add(c);
            s.WidgetsOn = true;
            return c;
        }

        private static BoardCard PCard(string kind, string source, double x, double y, double w, double h, int shape)
        {
            return new BoardCard { Kind = kind, Source = source ?? "all", X = x, Y = y, W = w, H = h, Shape = shape };
        }

        private static BoardCard PShortcut(string target, double x, double y, double w, double h)
        {
            return new BoardCard { Kind = "shortcut", Target = target, X = x, Y = y, W = w, H = h, Shape = CardShapes.Square };
        }

        private static List<string> PInfo(params string[] keys) { return keys.ToList(); }

        /// <summary>Games for Manual board's tiles: the last one played first, then the ones played most.</summary>
        private List<string> PresetTopGames(int count)
        {
            var ids = new List<string>();
            try
            {
                var all = api.Database.Games.Where(g => g != null && !g.Hidden && ProfileAllowsGame(g)).ToList();
                var last = all.Where(g => g.LastActivity.HasValue).OrderByDescending(g => g.LastActivity.Value).FirstOrDefault();
                if (last != null) ids.Add(last.Id.ToString());
                foreach (var g in all.OrderByDescending(g => g.Playtime).ThenByDescending(g => g.LastActivity ?? DateTime.MinValue))
                {
                    if (ids.Count >= count) break;
                    string id = g.Id.ToString();
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: presets, top games: " + ex.Message); }
            return ids;
        }

        /// <summary>A Manual board tile for the i-th top game (a list of games instead when there aren't that many).</summary>
        private static BoardCard PGame(List<string> top, int i, string fallback, double x, double y, double w, double h, int shape)
        {
            if (i < top.Count) return new BoardCard { Kind = "pinned", GameId = top[i], X = x, Y = y, W = w, H = h, Shape = shape };
            return new BoardCard { Kind = "filter", Source = fallback, X = x, Y = y, W = w, H = h, Shape = shape, Live = true };
        }

        // ---------------- the designs ----------------

        private void BuildPreset(string slot, int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            switch (slot)
            {
                case "row0": PresetRow(n, s, places, ref bg); break;
                case "row1": PresetTall(n, s, places, ref bg); break;
                case "row2": PresetFull(n, s, places, ref bg); break;
                case "l1": PresetGrid(n, s, places, ref bg); break;
                case "l2": PresetList(n, s, places, ref bg); break;
                case "l3": PresetBoard(n, s, places, ref bg); break;
                case "l4": PresetManual(n, s, places, ref bg); break;
                case "game": PresetGame(n, s, places); break;
            }
        }

        // Row (the row near the bottom)
        private void PresetRow(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            s.RowDesign = 0;
            if (n == 1)
            {
                // Cinema: wide art, a huge centred logo and info, no hints or filter name, the top bar out of the way
                s.CardShape = CardShapes.Wide; s.CardSize = 3; s.CardSpacing = 14;
                s.LogoSize = 3; s.LogoPosition = 1; s.LogoAnchor = 1; s.InfoPosition = 1; s.InfoSize = 1; s.HintsPosition = 1;
                s.ShowButtonHints = false; s.ShowFilterLabel = false;
                s.HomeBgMode = 0; s.BackgroundMotion = true; s.MotionSpeed = 0; s.MotionAmount = 1;
                s.AccentFromGame = true; s.AccentStrength = 2;
                s.HomeTilesMode = 0;
                s.AutoHideTopBar = true;
                SetLook(s, null, LookCinema());
                bg = new double[] { 0, 0, 0, 64 };
            }
            else if (n == 2)
            {
                // Neon arcade: hexagons with a neon edge, feature and store badges, depth and a clock in the top right
                s.CardShape = CardShapes.Hexagon; s.CardSize = 4; s.CardSpacing = 10;
                s.BadgeFeatures = true; s.BadgeStore = true; s.BadgeInstalled = true;
                s.LogoSize = 2; s.InfoSize = 1;
                s.HomeBgMode = 0; s.BackgroundMotion = true; s.MotionAmount = 1; s.DepthMode = 2;
                s.HomeTilesMode = 1; s.HomeTileStore = true; s.HomeTileAch = true; s.HomeTileRandom = true;
                SetLook(s, null, LookKippixNeon());
                PWidget(s, "clock", null, 1450, 118, 0.85, 2, true);
                places.Opacity["home.hints"] = 0.7;
                bg = new double[] { 0, 0, 0, 52 };
            }
            else
            {
                // Minimal: small square covers, the game's cover softened on dark navy to the right, small text, no hints
                s.CardShape = CardShapes.Square; s.CardSize = 2; s.CardSpacing = 22;
                s.LogoSize = 1; s.InfoSize = 0; s.ShowButtonHints = false;
                s.InfoPlatform = false; s.InfoAchievements = false;
                s.HomeBgMode = 6; s.HomeBgColor = "#0B0D14"; s.HomeTileBgScale = 0.6;
                s.HomeTilesMode = 0;
                SetLook(s, null, LookMinimal());
                bg = new double[] { 0.26, -0.11, 4, 44 };
            }
        }

        // Row › Tall
        private void PresetTall(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            s.RowDesign = 1;
            s.RowLoop = true;
            if (n == 1)
            {
                s.RowTallCount = 5; s.CarTallFill = 0.9; s.CardShape = CardShapes.Tall; s.RowShowInfo = false;
                s.HomeBgMode = 0; s.BackgroundMotion = true; s.MotionSpeed = 0; s.MotionAmount = 0;
                SetLook(s, "midnight", null);
                bg = new double[] { 0, 0, 0, 85 };
            }
            else if (n == 2)
            {
                s.RowTallCount = 3; s.CarTallFill = 1; s.CardShape = CardShapes.Tall; s.RowShowInfo = true;
                s.LogoSize = 2; s.InfoSize = 1; s.HintsSize = 0;
                s.AccentFromGame = true; s.AccentStrength = 1;
                s.HomeBgMode = 0; s.BackgroundMotion = true; s.MotionAmount = 1;
                SetLook(s, "sunset", null);
                bg = new double[] { 0, 0, 0, 60 };
            }
            else
            {
                s.RowTallCount = 7; s.CarTallFill = 0; s.CardShape = CardShapes.Hexagon; s.RowShowInfo = false;
                s.HomeBgMode = 0; s.DepthMode = 2; s.BackgroundMotion = true;
                SetLook(s, null, LookKippixNeon());
                bg = new double[] { 0, 0, 0, 70 };
            }
        }

        // Row › Full Screen (the background behind it is black; its panels are Tall or Wide)
        private void PresetFull(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            s.RowDesign = 2;
            s.RowLoop = true;
            if (n == 1)
            {
                s.RowFullCount = 4; s.CardShape = CardShapes.Wide; s.CarFullH = 1; s.RowShowInfo = true;
                s.LogoSize = 3; s.InfoSize = 1; s.ShowButtonHints = false;
                SetLook(s, "oled", null);
            }
            else if (n == 2)
            {
                s.RowFullCount = 8; s.CardShape = CardShapes.Tall; s.CarFullH = 0.62; s.RowShowInfo = true;
                s.LogoSize = 2; s.InfoSize = 2; s.HintsSize = 1;
                SetLook(s, null, LookKippixNeon());
            }
            else
            {
                s.RowFullCount = 2; s.CardShape = CardShapes.Wide; s.CarFullH = 1; s.RowShowInfo = false;
                s.AutoHideTopBar = true;
                SetLook(s, null, LookCinema());
            }
        }

        // Grid
        private void PresetGrid(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            if (n == 1)
            {
                s.GridRows = 3; s.GridColumns = 8; s.CardShape = CardShapes.Tall; s.CardSpacing = 12;
                s.BadgeInstalled = true; s.BadgeNew = true; s.BadgeAch = true;
                s.LogoSize = 0; s.InfoSize = 0; s.ShowButtonHints = false;
                s.HomeBgMode = 0;
                SetLook(s, "oled", null);
                bg = new double[] { 0, 0, 0, 55 };
            }
            else if (n == 2)
            {
                s.GridRows = 3; s.GridColumns = 7; s.CardShape = CardShapes.Hexagon; s.CardSpacing = 8;
                s.BadgeFeatures = true;
                s.HomeBgMode = 0; s.BackgroundMotion = true; s.MotionAmount = 1; s.DepthMode = 1;
                SetLook(s, null, LookKippixNeon());
                PWidget(s, "clock", null, 1450, 118, 0.75, 2, true);
                bg = new double[] { 0, 0, 0, 50 };
            }
            else
            {
                s.GridRows = 2; s.GridColumns = 5; s.CardShape = CardShapes.Wide; s.CardSpacing = 18;
                s.LogoSize = 2; s.InfoSize = 1;
                s.AccentFromGame = true;
                s.HomeBgMode = 0; s.BackgroundMotion = true;
                SetLook(s, "midnight", null);
                PWidget(s, "clock", null, 1440, 118, 0.8, 1, true);
                bg = new double[] { 0, 0, 0, 58 };
            }
        }

        // List
        private void PresetList(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            if (n == 1)
            {
                s.ListSide = 0; s.ListRows = 6; s.ListShowInfo = true; s.ListShowName = true; s.ListInfoShare = 50;
                s.ListInfoItems = new List<string> { "playtime", "lastplayed", "ach" };
                s.LogoSize = 3; s.InfoSize = 2;
                s.HomeBgMode = 0; s.BackgroundMotion = true; s.MotionAmount = 1;
                s.AccentFromGame = true;
                bg = new double[] { 0, 0, 2, 62 };
            }
            else if (n == 2)
            {
                s.ListSide = 1; s.ListRows = 8; s.ListShowInfo = false; s.CardShape = CardShapes.Wide;
                s.LogoSize = 2; s.InfoSize = 1;
                s.HomeBgMode = 4;   // the game's trailer, muted, behind everything
                SetLook(s, "oled", null);
                bg = new double[] { 0, 0, 3, 45 };
            }
            else
            {
                s.ListSide = 0; s.ListRows = 4; s.ListShowInfo = true; s.ListShowName = true; s.ListInfoShare = 60;
                s.ListInfoItems = new List<string> { "playtime", "lastplayed", "hltb", "genre" };
                s.LogoSize = 2; s.InfoSize = 1;
                s.HomeBgMode = 0;
                SetLook(s, "sunset", null);
                bg = new double[] { 0, 0, 2, 70 };
            }
        }

        // Board (its tiles show games from lists, again on every page)
        private void PresetBoard(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            var cards = new List<BoardCard>();
            if (n == 1)
            {
                s.BoardCols = 6; s.BoardRows = 3;
                var clock = PWidget(s, "clock", null, 0, 0, 1, 1, true);
                cards.Add(new BoardCard { Source = "played", X = 0, Y = 0, W = 3, H = 2, Shape = CardShapes.Square, Art = 4, Info = PInfo("name", "playtime", "lastplayed", "ach") });
                for (int x = 3; x < 6; x++) cards.Add(PCard("filter", "favourites", x, 0, 1, 1, CardShapes.Square));
                cards.Add(new BoardCard { Source = "recent", X = 3, Y = 1, W = 2, H = 1, Shape = CardShapes.Wide, Info = PInfo("name") });
                cards.Add(PShortcut("store", 5, 1, 1, 1));
                cards.Add(new BoardCard { Kind = "logo", X = 0, Y = 2, W = 2, H = 1, Back = 3 });
                cards.Add(PCard("filter", "installed", 2, 2, 1, 1, CardShapes.Square));
                cards.Add(new BoardCard { Kind = "widget", Target = clock.Id, X = 3, Y = 2, W = 2, H = 1 });
                cards.Add(PShortcut("achievements", 5, 2, 1, 1));
                SetLook(s, "midnight", null);
                bg = new double[] { 0, 0, 0, 60 };
            }
            else if (n == 2)
            {
                s.BoardCols = 8; s.BoardRows = 4; s.BoardLiveSeconds = 7;
                cards.Add(new BoardCard { Source = "played", X = 0, Y = 0, W = 2, H = 2, Shape = CardShapes.Square, Info = PInfo("name", "playtime") });
                cards.Add(new BoardCard { Source = "favourites", X = 2, Y = 0, W = 1, H = 1, Shape = CardShapes.Circle, Live = true });
                cards.Add(new BoardCard { Source = "recent", X = 3, Y = 0, W = 2, H = 1, Shape = CardShapes.Wide, Live = true, Info = PInfo("name") });
                cards.Add(new BoardCard { Source = "installed", X = 5, Y = 0, W = 1, H = 1, Shape = CardShapes.Hexagon });
                cards.Add(new BoardCard { Source = "random", X = 6, Y = 0, W = 2, H = 2, Shape = CardShapes.Square, Live = true, Art = 1, Rotation = 3 });
                cards.Add(new BoardCard { Source = "never", X = 2, Y = 1, W = 1, H = 1, Shape = CardShapes.Hexagon, Live = true });
                cards.Add(new BoardCard { Source = "favourites", X = 3, Y = 1, W = 1, H = 1, Shape = CardShapes.Circle });
                cards.Add(new BoardCard { Source = "played", X = 4, Y = 1, W = 1, H = 1, Shape = CardShapes.Square, Rotation = -4 });
                cards.Add(PShortcut("random", 5, 1, 1, 1));
                cards.Add(new BoardCard { Source = "played", X = 0, Y = 2, W = 3, H = 2, Shape = CardShapes.Wide, Art = 2, Info = PInfo("name", "lastplayed", "hltb") });
                cards.Add(new BoardCard { Kind = "info", X = 3, Y = 2, W = 2, H = 2, Back = 2, Info = PInfo("name", "playtime", "lastplayed", "ach") });
                cards.Add(PShortcut("search", 5, 2, 1, 1));
                cards.Add(new BoardCard { Source = "installed", X = 6, Y = 2, W = 2, H = 1, Shape = CardShapes.Wide, Live = true });
                cards.Add(PShortcut("gallery", 5, 3, 1, 1));
                cards.Add(PShortcut("achievements", 6, 3, 1, 1));
                cards.Add(PShortcut("store", 7, 3, 1, 1));
                SetLook(s, "arcade", null);
                s.BackgroundMotion = true; s.DepthMode = 1;
                bg = new double[] { 0, 0, 0, 50 };
            }
            else
            {
                s.BoardCols = 7; s.BoardRows = 3;
                string[] lists = { "played", "favourites", "installed" };
                string[] heads = { "random", "achievements", "store" };
                for (int y = 0; y < 3; y++)
                {
                    cards.Add(PShortcut(heads[y], 0, y, 1, 1));
                    for (int x = 1; x < 7; x++) cards.Add(PCard("filter", lists[y], x, y, 1, 1, CardShapes.Square));
                }
                SetLook(s, null, LookMinimal());
                s.HomeBgMode = 0;
                bg = new double[] { 0, 0, 0, 80 };
            }
            s.BoardCards = cards;
            s.BoardSeeded = true;
        }

        // Manual board (tiles you put down yourself; the designs put your top games on them)
        private void PresetManual(int n, HelperSettings s, HomePlaceSet places, ref double[] bg)
        {
            var top = PresetTopGames(8);
            var cards = new List<BoardCard>();
            s.ManualPages = new List<BoardPage>();
            s.FreePages = new List<BoardPage>();
            if (n == 1)
            {
                s.ManualPlacement = 0; s.ManualCols = 8; s.ManualRows = 4;
                var clock = PWidget(s, "clock", null, 0, 0, 1, 1, true);
                var g0 = PGame(top, 0, "played", 0, 0, 4, 2, CardShapes.Square); g0.Art = 4; g0.Info = PInfo("name", "playtime", "lastplayed"); cards.Add(g0);
                var g1 = PGame(top, 1, "favourites", 4, 0, 2, 2, CardShapes.Square); g1.Art = 1; cards.Add(g1);
                cards.Add(new BoardCard { Kind = "widget", Target = clock.Id, X = 6, Y = 0, W = 2, H = 1 });
                cards.Add(PShortcut("store", 6, 1, 1, 1));
                cards.Add(PShortcut("achievements", 7, 1, 1, 1));
                cards.Add(new BoardCard { Kind = "filter", Source = "favourites", X = 0, Y = 2, W = 2, H = 2, Shape = CardShapes.Square, Live = true, Art = 1 });
                cards.Add(PGame(top, 2, "installed", 2, 2, 1, 1, CardShapes.Circle));
                cards.Add(PGame(top, 3, "installed", 3, 2, 1, 1, CardShapes.Hexagon));
                cards.Add(new BoardCard { Kind = "filter", Source = "recent", X = 2, Y = 3, W = 2, H = 1, Shape = CardShapes.Wide, Live = true, Info = PInfo("name") });
                cards.Add(new BoardCard { Kind = "info", X = 4, Y = 2, W = 2, H = 2, Back = 1, Info = PInfo("name", "playtime", "lastplayed", "ach") });
                cards.Add(new BoardCard { Kind = "filter", Source = "never", X = 6, Y = 2, W = 2, H = 2, Shape = CardShapes.Square, Live = true, Art = 1 });
                s.ManualCards = cards;
                SetLook(s, null, LookKippixNeon());
                bg = new double[] { 0, 0, 0, 55 };
            }
            else if (n == 2)
            {
                // free placement: 96 × 49 fine squares; overlapping, turned, like photos on a table
                s.ManualPlacement = 1;
                double[][] spots =
                {
                    new double[] { 4, 4, 18, 24, -6, 0 }, new double[] { 20, 2, 16, 21, 5, 1 }, new double[] { 34, 7, 19, 25, -3, 0 },
                    new double[] { 52, 3, 16, 21, 8, 1 }, new double[] { 66, 9, 18, 24, -5, 0 }, new double[] { 80, 2, 14, 19, 6, 1 },
                    new double[] { 9, 28, 15, 19, 4, 2 }, new double[] { 25, 30, 14, 18, -7, 1 }
                };
                for (int i = 0; i < spots.Length; i++)
                {
                    var p = spots[i];
                    var c = PGame(top, i, i % 2 == 0 ? "played" : "favourites", p[0], p[1], p[2], p[3], CardShapes.Square);
                    c.Art = 1; c.Rotation = p[4]; c.Layer = (int)p[5];
                    cards.Add(c);
                }
                var clock = PWidget(s, "clock", null, 0, 0, 1, 2, true);
                cards.Add(new BoardCard { Kind = "logo", X = 44, Y = 33, W = 30, H = 10, Back = 3, Layer = 3 });
                cards.Add(new BoardCard { Kind = "widget", Target = clock.Id, X = 76, Y = 31, W = 18, H = 12, Rotation = 3, Layer = 2 });
                cards.Add(new BoardCard { Kind = "info", X = 44, Y = 43, W = 30, H = 5, Back = 3, Layer = 3, Info = PInfo("playtime", "lastplayed") });
                s.FreeCards = cards;
                SetLook(s, "pastel", null);
                s.HomeBgMode = 0; s.BackgroundMotion = true;
                bg = new double[] { 0, 0, 0, 70 };
            }
            else
            {
                s.ManualPlacement = 0; s.ManualCols = 6; s.ManualRows = 3;
                for (int i = 0; i < 6; i++) { var c = PGame(top, i, "played", i, 0, 1, 2, CardShapes.Square); c.Art = 1; c.HideText = true; cards.Add(c); }
                string[] keys = { "store", "achievements", "gallery", "search", "collections", "random" };
                for (int i = 0; i < 6; i++) cards.Add(PShortcut(keys[i], i, 2, 1, 1));
                s.ManualCards = cards;
                SetLook(s, "oled", null);
                s.HomeBgMode = 0;
                bg = new double[] { 0, 0, 0, 75 };
            }
        }

        // the game page
        /// <summary>(0.94) The game page's designs, Design 1 and Design 2: both start as the game page as Kippix comes (the parts
        /// on their own pages as they come too); move things round and each keeps its own.</summary>
        private void PresetGame(int n, HelperSettings s, HomePlaceSet places)
        {
            s.DetLower = null;
            s.DetInfoOrder = null;
        }
    }
}

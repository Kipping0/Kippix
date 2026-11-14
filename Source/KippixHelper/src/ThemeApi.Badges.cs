using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>One feature badge on the game page (controller, co-op, split screen, online, VR, Steam Deck).</summary>
    public class FeatureBadge
    {
        public Geometry Icon { get; set; }
        public string Text { get; set; }
        public string Tip { get; set; }
        public Brush Brush { get; set; }
    }

    /// <summary>
    /// (r12) Feature badges: what a game supports, from Playnite's features (their names vary from library to library, so common
    /// ones are matched by their words) plus, for Steam games, Steam's own categories and Steam Deck rating (looked up when the
    /// game page opens, kept 7 days in badges-steam.json). On the game page they're a line in the play info; on the home cards
    /// (off at first: Game cards and layout › Feature badges on cards) a small row of icons with the other badges, from what's
    /// already known (the cards never go online). Also the cards' store pill (Store logo on cards).
    /// </summary>
    public partial class ThemeApi
    {
        [Flags]
        private enum Feat { None = 0, Controller = 1, Coop = 2, Split = 4, Online = 8, Vr = 16, DeckVerified = 32, DeckPlayable = 64 }

        private static readonly Regex VrWordRx = new Regex(@"\bvr\b", RegexOptions.Compiled);

        /// <summary>What one feature (or Steam category) name says the game has.</summary>
        private static Feat FeatFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return Feat.None;
            string n = name.ToLowerInvariant().Trim();
            var f = Feat.None;
            bool tracked = n.Contains("tracked");   // ("Tracked Controller Support": VR's motion controllers, not a gamepad)
            if ((n.Contains("controller") || n.Contains("gamepad")) && !tracked) f |= Feat.Controller;
            if (n.Contains("co-op") || n.Contains("coop") || n.Contains("co op") || n.Contains("cooperative")) f |= Feat.Coop;
            if (n.Contains("split") || n.Contains("shared screen") || n.Contains("local multi") || n.Contains("local co") || n.Contains("local pvp")
                || n.Contains("local versus") || n.Contains("couch")) f |= Feat.Split;
            if (n.Contains("online") || n.Contains("mmo") || n.Contains("cross-platform multi") || n == "multi-player" || n == "multiplayer"
                || n == "pvp" || n.Contains("massively")) f |= Feat.Online;
            if (VrWordRx.IsMatch(n) || n.Contains("virtual reality") || tracked) f |= Feat.Vr;
            if (n.Contains("deck"))
            {
                if (n.Contains("verified")) f |= Feat.DeckVerified;
                else if (n.Contains("playable")) f |= Feat.DeckPlayable;
            }
            return f;
        }

        /// <summary>From Playnite's features (and a tag naming the Steam Deck rating, which some add-ons write).</summary>
        private static Feat FeaturesFromPlaynite(Game g)
        {
            var f = Feat.None;
            if (g == null) return f;
            try
            {
                if (g.Features != null) foreach (var x in g.Features) if (x != null) f |= FeatFromName(x.Name);
                if (g.Tags != null)
                    foreach (var t in g.Tags)
                        if (t != null && t.Name != null && t.Name.IndexOf("deck", StringComparison.OrdinalIgnoreCase) >= 0) f |= FeatFromName(t.Name) & (Feat.DeckVerified | Feat.DeckPlayable);
            }
            catch (Exception) { }
            if ((f & Feat.DeckVerified) != 0) f &= ~Feat.DeckPlayable;
            return f;
        }

        // ---------------- Steam's data, kept 7 days ----------------

        private static Dictionary<string, long[]> steamFeats;   // app id → [when (UTC ticks), Feat flags]
        private static readonly object steamFeatsLock = new object();
        private static readonly HashSet<string> steamFeatsLoading = new HashSet<string>();
        private string SteamFeatsPath { get { return System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "badges-steam.json"); } }

        private void LoadSteamFeats()
        {
            lock (steamFeatsLock)
            {
                if (steamFeats != null) return;
                steamFeats = new Dictionary<string, long[]>();
                try
                {
                    if (!File.Exists(SteamFeatsPath)) return;
                    var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(SteamFeatsPath)) as Dictionary<string, object>;
                    if (d == null) return;
                    foreach (var kv in d)
                    {
                        var arr = kv.Value as object[];
                        if (arr == null || arr.Length < 2) continue;
                        try { steamFeats[kv.Key] = new[] { Convert.ToInt64(arr[0], CultureInfo.InvariantCulture), Convert.ToInt64(arr[1], CultureInfo.InvariantCulture) }; }
                        catch (Exception) { }
                    }
                }
                catch (Exception ex) { logger.Info("Kippix: badges-steam.json couldn't be read: " + ex.Message); }
            }
        }

        private void SaveSteamFeats()
        {
            try
            {
                string json;
                lock (steamFeatsLock) json = new JavaScriptSerializer().Serialize(steamFeats);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SteamFeatsPath));
                string tmp = SteamFeatsPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(SteamFeatsPath)) File.Delete(SteamFeatsPath);
                File.Move(tmp, SteamFeatsPath);
            }
            catch (Exception ex) { logger.Info("Kippix: badges-steam.json couldn't be saved: " + ex.Message); }
        }

        /// <summary>What Steam said about the game (None if it hasn't been looked up); fresh: looked up in the last 7 days.</summary>
        private Feat SteamFeatsKnown(string appId, out bool fresh)
        {
            fresh = false;
            if (appId == null) return Feat.None;
            LoadSteamFeats();
            long[] v;
            lock (steamFeatsLock) { if (!steamFeats.TryGetValue(appId, out v)) return Feat.None; }
            fresh = (DateTime.UtcNow - new DateTime(v[0], DateTimeKind.Utc)).TotalDays < 7;
            return (Feat)v[1];
        }

        /// <summary>Steam's categories (controller, co-op, split screen, online, VR) and Steam Deck rating for the game (worker thread).</summary>
        private Feat FetchSteamFeats(string appId)
        {
            var f = Feat.None;
            var app = StoreJson("https://store.steampowered.com/api/appdetails?l=english&appids=" + Uri.EscapeDataString(appId)) as Dictionary<string, object>;
            var d = Dig(app, appId, "data") as Dictionary<string, object>;
            if (d != null)
            {
                string ctl = FStr(d, "controller_support");
                if (ctl.Length > 0) f |= Feat.Controller;
                var cats = Dig(d, "categories") as IEnumerable;
                if (cats != null) foreach (var c in cats.Cast<object>().OfType<Dictionary<string, object>>()) f |= FeatFromName(FStr(c, "description"));
            }
            int deck = SteamDeck(appId);   // (the store page's own lookup: 3 verified, 2 playable)
            if (deck == 3) f |= Feat.DeckVerified;
            else if (deck == 2) f |= Feat.DeckPlayable;
            return f;
        }

        // ---------------- the game page ----------------

        private ObservableCollection<FeatureBadge> detFeatureBadges;
        public ObservableCollection<FeatureBadge> DetFeatureBadges { get { return detFeatureBadges ?? (detFeatureBadges = new ObservableCollection<FeatureBadge>()); } }
        public bool DetInfoShowFeatures { get { return DetInfoOn("features") && DetFeatureBadges.Count > 0; } }

        private void FillFeatureBadges(Game g)
        {
            if (g == null || !detailsOpen || !DetInfoOn("features")) { SetFeatureBadges(Feat.None); return; }
            string appId = SteamAppIdOf(g);
            bool fresh;
            var mine = FeaturesFromPlaynite(g);
            SetFeatureBadges(mine | SteamFeatsKnown(appId, out fresh));
            if (appId == null || fresh) return;
            lock (steamFeatsLock) { if (!steamFeatsLoading.Add(appId)) return; }
            var id = g.Id;
            KippixShared.SafeWork.Queue(_ =>
            {
                Feat got = Feat.None;
                bool ok = false;
                try { got = FetchSteamFeats(appId); ok = true; }
                catch (Exception ex) { logger.Info("Kippix: Steam features for " + appId + " couldn't be read: " + ex.Message); }
                lock (steamFeatsLock)
                {
                    steamFeatsLoading.Remove(appId);
                    if (ok) steamFeats[appId] = new[] { DateTime.UtcNow.Ticks, (long)got };
                }
                if (!ok) return;
                SaveSteamFeats();
                OnUi(() =>
                {
                    if (StillShowing(id) && DetInfoOn("features")) SetFeatureBadges(FeaturesFromPlaynite(SelectedGameNow()) | got);   // (the page's game, whichever lookup asked)
                    if (settings.BadgeFeatures) RefreshBadgesSoon();   // (the home cards can show it now too)
                });
            });
        }

        private void SetFeatureBadges(Feat f)
        {
            DetFeatureBadges.Clear();
            foreach (var b in BadgesFor(f)) DetFeatureBadges.Add(b);
            Notify("DetInfoShowFeatures");
        }

        private static readonly Brush FeatBrush = FeatBrushOf(Color.FromRgb(0xE6, 0xE9, 0xEE));
        private static readonly Brush DeckGoodBrush = FeatBrushOf(Color.FromRgb(0x5D, 0xD8, 0x79));
        private static readonly Brush DeckOkBrush = FeatBrushOf(Color.FromRgb(0xFB, 0xBF, 0x24));

        private static Brush FeatBrushOf(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // the icons (24 × 24, drawn as lines like the theme's own)
        private const string FeatIconController = "M7.5,8 L16.5,8 C19.5,8 21.5,10.5 21.5,14 C21.5,16.5 20.3,18 18.8,18 C17.5,18 16.8,17 16,16 L8,16 C7.2,17 6.5,18 5.2,18 C3.7,18 2.5,16.5 2.5,14 C2.5,10.5 4.5,8 7.5,8 Z M6.5,11 L6.5,14 M5,12.5 L8,12.5 M16.5,11.5 L16.5,11.6 M18,13 L18,13.1";
        private const string FeatIconCoop = "M9,11 A3.5,3.5 0 1 1 9,4 A3.5,3.5 0 1 1 9,11 Z M2.5,20 C2.5,16 5.5,14 9,14 C12.5,14 15.5,16 15.5,20 M16,4.3 A3.4,3.4 0 0 1 16,10.7 M18,14.3 C20.2,15 21.5,17 21.5,20";
        private const string FeatIconSplit = "M3,5 L21,5 L21,19 L3,19 Z M12,5 L12,19";
        private const string FeatIconOnline = "M12,3 A9,9 0 1 1 11.99,3 M3.2,12 L20.8,12 M12,3 C8.8,6.2 8.8,17.8 12,21 M12,3 C15.2,6.2 15.2,17.8 12,21";
        private const string FeatIconVr = "M3,8 L21,8 L21,16 L15.5,16 L13.5,13 L10.5,13 L8.5,16 L3,16 Z M7.5,11 L7.51,11 M16.5,11 L16.51,11";
        private const string FeatIconDeck = "M2.5,8 L21.5,8 L21.5,16 L2.5,16 Z M8,8 L8,16 M16,8 L16,16 M4.6,11 L4.61,11 M19.4,13 L19.41,13";

        private static readonly Dictionary<string, Geometry> featGeometry = new Dictionary<string, Geometry>();
        private static Geometry FeatGeometry(string data)
        {
            Geometry g;
            lock (featGeometry)
            {
                if (featGeometry.TryGetValue(data, out g)) return g;
                try { g = Geometry.Parse(data); g.Freeze(); } catch (Exception) { g = Geometry.Empty; }
                featGeometry[data] = g;
            }
            return g;
        }

        /// <summary>The badges for a set of features, in a fixed order.</summary>
        private List<FeatureBadge> BadgesFor(Feat f)
        {
            var r = new List<FeatureBadge>();
            Action<Feat, string, string, string, Brush> add = (bit, icon, text, tip, brush) =>
            {
                if ((f & bit) != 0) r.Add(new FeatureBadge { Icon = FeatGeometry(icon), Text = text, Tip = tip, Brush = brush ?? FeatBrush });
            };
            add(Feat.Controller, FeatIconController, Loc("LOC_KX_FeatController", "Controller"), Loc("LOC_KX_FeatControllerTip", "Controller support"), null);
            add(Feat.Coop, FeatIconCoop, Loc("LOC_KX_FeatCoop", "Co-op"), Loc("LOC_KX_FeatCoopTip", "Co-op"), null);
            add(Feat.Split, FeatIconSplit, Loc("LOC_KX_FeatSplit", "Split screen"), Loc("LOC_KX_FeatSplitTip", "Split screen / local multiplayer"), null);
            add(Feat.Online, FeatIconOnline, Loc("LOC_KX_FeatOnline", "Online"), Loc("LOC_KX_FeatOnlineTip", "Online multiplayer"), null);
            add(Feat.Vr, FeatIconVr, Loc("LOC_KX_FeatVr", "VR"), Loc("LOC_KX_FeatVrTip", "VR support"), null);
            add(Feat.DeckVerified, FeatIconDeck, Loc("LOC_KX_FeatDeckVerified", "Deck Verified"), Loc("LOC_KX_BadgeDeckVerified", "Steam Deck Verified"), DeckGoodBrush);
            if ((f & Feat.DeckVerified) == 0)
                add(Feat.DeckPlayable, FeatIconDeck, Loc("LOC_KX_FeatDeckPlayable", "Deck Playable"), Loc("LOC_KX_BadgeDeckPlayable", "Steam Deck Playable"), DeckOkBrush);
            return r;
        }

        // ---------------- the home cards ----------------

        public bool BadgeFeatures { get { return settings.BadgeFeatures; } set { if (SetSetting(ref settings.BadgeFeatures, value)) { Notify("BadgeFeatures"); Notify("BadgeFeatRowsShown"); RefreshBadgesSoon(); } } }
        // (0.96) each feature icon on its own switch (Tile badges)
        public bool BadgeFeatRowsShown { get { return settings.BadgeFeatures && IsNotBoardMode; } }
        private bool FeatShown(Feat f) { return (settings.BadgeFeatHide & (int)f) == 0; }
        private void SetFeatShown(Feat f, bool on)
        {
            int v = on ? settings.BadgeFeatHide & ~(int)f : settings.BadgeFeatHide | (int)f;
            if (v == settings.BadgeFeatHide) return;
            settings.BadgeFeatHide = v;
            SaveSettings();
            foreach (string n in new[] { "BadgeFeatController", "BadgeFeatCoop", "BadgeFeatSplit", "BadgeFeatOnline", "BadgeFeatVr", "BadgeFeatDeck" }) Notify(n);
            RefreshBadgesSoon();
        }
        public bool BadgeFeatController { get { return FeatShown(Feat.Controller); } set { SetFeatShown(Feat.Controller, value); } }
        public bool BadgeFeatCoop { get { return FeatShown(Feat.Coop); } set { SetFeatShown(Feat.Coop, value); } }
        public bool BadgeFeatSplit { get { return FeatShown(Feat.Split); } set { SetFeatShown(Feat.Split, value); } }
        public bool BadgeFeatOnline { get { return FeatShown(Feat.Online); } set { SetFeatShown(Feat.Online, value); } }
        public bool BadgeFeatVr { get { return FeatShown(Feat.Vr); } set { SetFeatShown(Feat.Vr, value); } }
        public bool BadgeFeatDeck { get { return FeatShown(Feat.DeckVerified); } set { SetFeatShown(Feat.DeckVerified | Feat.DeckPlayable, value); } }

        public bool BadgeStore { get { return settings.BadgeStore; } set { if (SetSetting(ref settings.BadgeStore, value)) { Notify("BadgeStore"); RefreshBadgesSoon(); } } }

        /// <summary>From BadgeCard: the card's feature icons (bottom row) and store pill (top row), from what's already known.</summary>
        private void BadgeCardExtras(FrameworkElement c, Game g)
        {
            try
            {
                var feat = UiTools.FindByName(c, "KX_BadgeFeat");
                var icons = UiTools.FindByName(c, "KX_BadgeFeatIcons") as Panel;
                if (feat != null && icons != null)
                {
                    var f = Feat.None;
                    if (settings.BadgeFeatures && g != null)
                    {
                        bool fresh;
                        f = FeaturesFromPlaynite(g) | SteamFeatsKnown(SteamAppIdOf(g), out fresh);
                        if ((f & Feat.DeckVerified) != 0) f &= ~Feat.DeckPlayable;
                        f &= ~(Feat)settings.BadgeFeatHide;   // (0.96: the ones switched off in Tile badges)
                    }
                    var v = f != Feat.None ? Visibility.Visible : Visibility.Collapsed;
                    if (feat.Visibility != v) feat.Visibility = v;
                    object was = icons.Tag;
                    if (!(was is int) || (int)was != (int)f)
                    {
                        icons.Tag = (int)f;
                        icons.Children.Clear();
                        foreach (var b in BadgesFor(f))
                            icons.Children.Add(new System.Windows.Shapes.Path
                            {
                                Data = b.Icon, Stroke = b.Brush, StrokeThickness = 1.9, Width = 13, Height = 13, Stretch = Stretch.Uniform,
                                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                                Margin = new Thickness(icons.Children.Count == 0 ? 0 : 5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = b.Tip
                            });
                    }
                }
                string store = settings.BadgeStore && g != null ? StoreShortName(g) : null;
                SetBadge(c, "KX_BadgeStore", !string.IsNullOrEmpty(store), "KX_BadgeStoreText", store);
            }
            catch (Exception) { }
        }

        /// <summary>The game's store as a short name for the card's pill (Steam, GOG, Epic, Ubisoft, Xbox, EA...).</summary>
        private static string StoreShortName(Game g)
        {
            string s = g.Source != null ? g.Source.Name : null;
            if (string.IsNullOrEmpty(s)) return null;
            string n = s.ToLowerInvariant();
            if (n.Contains("steam")) return "Steam";
            if (n.Contains("gog")) return "GOG";
            if (n.Contains("epic")) return "Epic";
            if (n.Contains("ubisoft") || n.Contains("uplay")) return "Ubisoft";
            if (n.Contains("xbox") || n.Contains("microsoft") || n.Contains("game pass")) return "Xbox";
            if (n == "ea" || n.Contains("ea app") || n.Contains("origin") || n.StartsWith("ea ", StringComparison.Ordinal)) return "EA";
            if (n.Contains("battle.net") || n.Contains("blizzard")) return "Battle.net";
            if (n.Contains("amazon") || n.Contains("prime")) return "Amazon";
            if (n.Contains("itch")) return "itch.io";
            if (n.Contains("humble")) return "Humble";
            if (n.Contains("rockstar")) return "Rockstar";
            return s.Length > 12 ? s.Substring(0, 11).TrimEnd() + "…" : s;
        }
    }
}

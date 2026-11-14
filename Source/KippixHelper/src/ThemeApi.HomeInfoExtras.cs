using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (r13) Two optional lines on the home screen's game info (under the logo, with play time and last played), both off at first:
    /// "1,234 playing now" (Steam's public player count, Steam games) and "2 friends playing now" / "3 friends play this" (your
    /// Steam friends with the Steam Web API key, and PlayerActivities' friends). Looked up only for the highlighted game, once it
    /// has stayed highlighted for 600 ms (scrolling past games asks nothing), off the UI thread: the count kept 5 minutes, friends'
    /// libraries a day (the same data as the game page's Friends card). Switches: Home settings › Logo, game info and button hints,
    /// and the Game info details list (where their order is set too). The game page has the same two lines in its play info.
    /// </summary>
    public partial class ThemeApi
    {
        private const int HomeExtrasRestMs = 600;
        private DispatcherTimer homeExtrasTimer;
        private Guid homeExtrasGame = Guid.Empty;
        private int homeExtrasGen;
        private string homePlayersText = "", homeFriendsText = "";

        public bool InfoPlayers
        {
            get { return settings.InfoPlayers; }
            set { if (SetSetting(ref settings.InfoPlayers, value)) { HomeExtrasSwitched("players"); if (value) HomePartBackOnTop("home.players"); } }
        }

        public bool InfoFriends
        {
            get { return settings.InfoFriends; }
            set { if (SetSetting(ref settings.InfoFriends, value)) { HomeExtrasSwitched("friends"); if (value) HomePartBackOnTop("home.friends"); } }
        }

        // (0.96) How long to beat on the home screen (its own part)
        private string homeHltbText = "";
        private readonly Dictionary<Guid, string> homeHltbFull = new Dictionary<Guid, string>();
        private readonly HashSet<Guid> homeHltbLoading = new HashSet<Guid>();

        private string HomeHltbFull(Guid id)
        {
            lock (homeHltbFull)
            {
                string t;
                if (homeHltbFull.TryGetValue(id, out t)) return t;
                if (homeHltbLoading.Contains(id)) return null;
                homeHltbLoading.Add(id);
            }
            string ext = api.Paths.ExtensionsDataPath;
            KippixShared.SafeWork.Queue(_ =>
            {
                string text = null;
                try
                {
                    var h = KippixOverlay.GameData.LoadHltb(ext, id);
                    var parts = new List<string>();
                    if (h != null && h.Story > 0) parts.Add(string.Format(Loc("LOC_KX_HomeHltbMain", "Main story {0}"), HltbHours(h.Story)));
                    if (h != null && h.Extras > 0) parts.Add(string.Format(Loc("LOC_KX_HomeHltbExtras", "Main + extras {0}"), HltbHours(h.Extras)));
                    if (h != null && h.Completionist > 0) parts.Add(string.Format(Loc("LOC_KX_HomeHltbComplete", "Completionist {0}"), HltbHours(h.Completionist)));
                    if (parts.Count > 0) text = string.Join("   ·   ", parts);
                }
                catch (Exception) { }
                lock (homeHltbFull) { homeHltbFull[id] = text; homeHltbLoading.Remove(id); }
                if (text != null && settings.HomeHltb) OnUi(HomeHltbRefresh);
            });
            return null;
        }
        public bool HomeHltb { get { return settings.HomeHltb; } set { if (SetSetting(ref settings.HomeHltb, value)) { Notify("HomeHltb"); HomeHltbRefresh(); if (value) HomePartBackOnTop("home.hltb"); } } }
        public bool HomeShowHltb { get { return settings.HomeHltb && homeHltbText.Length > 0; } }
        public string HomeHltbText { get { return homeHltbText; } }

        /// <summary>The highlighted game's How long to beat: main story, main + extras and completionist (0.99: all that are known;
        /// read in the background the first time; this runs again when it's in).</summary>
        private void HomeHltbRefresh()
        {
            string t = "";
            try
            {
                var g = settings.HomeHltb && IsFullscreen ? SelectedGameNow() : null;
                string h = g != null ? HomeHltbFull(g.Id) : null;
                if (!string.IsNullOrEmpty(h)) t = h;
            }
            catch (Exception) { }
            if (t == homeHltbText) return;
            homeHltbText = t;
            Notify("HomeHltbText");
            Notify("HomeShowHltb");
        }

        public string HomePlayersText { get { return homePlayersText; } }
        public string HomeFriendsText { get { return homeFriendsText; } }
        public bool HomeShowPlayers { get { return settings.InfoPlayers && homePlayersText.Length > 0; } }
        public bool HomeShowFriends { get { return settings.InfoFriends && homeFriendsText.Length > 0; } }
        public int InfoColPlayers { get { return InfoCol("players"); } }
        public int InfoColFriends { get { return InfoCol("friends"); } }

        /// <summary>Switched on or off (the Logo page's switch, or the Game info details list): the other place follows, and it's looked up now.</summary>
        private void HomeExtrasSwitched(string key)
        {
            if (InfoItems != null)
            {
                var item = InfoItems.FirstOrDefault(i => i.Key == key);
                if (item != null && item.IsPinned != GetInfo(key)) item.SetPinnedSilently(GetInfo(key));
            }
            Notify(key == "players" ? "InfoPlayers" : "InfoFriends");
            homeExtrasGame = Guid.Empty;   // (looked up again for the game that's highlighted)
            if (GetInfo(key)) HomeExtrasSelected(SelectedGameNow(), true);
            NotifyHomeExtras();
        }

        private void NotifyHomeExtras()
        {
            foreach (string n in new[] { "HomePlayersText", "HomeFriendsText", "HomeShowPlayers", "HomeShowFriends" }) Notify(n);
        }

        private void SetHomeExtras(string players, string friends)
        {
            bool changed = false;
            if (players != null && players != homePlayersText) { homePlayersText = players; changed = true; }
            if (friends != null && friends != homeFriendsText) { homeFriendsText = friends; changed = true; }
            if (changed) NotifyHomeExtras();
        }

        /// <summary>Another game was highlighted (from GameSelected): the last game's lines go at once, this one's come once it rests.</summary>
        internal void HomeExtrasSelected(Game g, bool now)
        {
            HomeHltbRefresh();
            HomeProgressRefresh();   // (0.99.48)
            if (!IsFullscreen || (!settings.InfoPlayers && !settings.InfoFriends)) { SetHomeExtras("", ""); return; }
            Guid id = g != null ? g.Id : Guid.Empty;
            if (id == homeExtrasGame && !now) return;   // (the same game again)
            homeExtrasGame = id;
            homeExtrasGen++;
            if (homeExtrasTimer != null) homeExtrasTimer.Stop();
            // a count already known for this game shows straight away; anything else waits for the rest
            string cached = "";
            string appId = g != null && settings.InfoPlayers ? SteamAppIdOf(g) : null;
            PlayersCacheEntry hit = null;
            if (appId != null) lock (playersCache) playersCache.TryGetValue(appId, out hit);
            // (0.99) a count known for this game shows at once, however old (a fresh one follows); a Steam game without one yet
            // keeps the line up with a dash, so it doesn't vanish and pop back while you move
            if (hit != null) cached = PlayersText(hit.Count);
            else if (appId != null) cached = Loc("LOC_KX_PlayersWait", "– playing now");
            SetHomeExtras(cached, "");
            if (hit == null && appId != null) PlayersDashGuard(homeExtrasGen);
            if (g == null) return;
            if (homeExtrasTimer == null)
            {
                homeExtrasTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(HomeExtrasRestMs) };
                homeExtrasTimer.Tick += (s, e) => { homeExtrasTimer.Stop(); try { HomeExtrasFill(); } catch (Exception ex) { logger.Warn("Kippix: home info extras: " + ex.Message); } };
            }
            homeExtrasTimer.Interval = TimeSpan.FromMilliseconds(now ? 1 : HomeExtrasRestMs);
            homeExtrasTimer.Start();
        }

        /// <summary>The highlighted game has rested: its player count and friends, looked up off the UI thread.</summary>
        private void HomeExtrasFill()
        {
            var g = SelectedGameNow();
            if (g == null)
            {
                // (0.99.35) at start-up Playnite can say nothing is selected for a moment: asked again shortly (the dash stayed for good)
                if (++playersTries <= 6) HomeExtrasRetry(homeExtrasGen, 1000);
                return;
            }
            if (g.Id != homeExtrasGame) { HomeExtrasSelected(g, true); return; }   // (0.99.4: at start-up the highlighted game had moved on: that one's)
            int my = homeExtrasGen;
            if (settings.InfoPlayers)
            {
                string appId = SteamAppIdOf(g);
                if (appId == null) SetHomeExtras("", null);
                else PlayersCountAsync(appId, count =>
                {
                    if (my != homeExtrasGen) return;
                    if (count >= 0) { playersTries = 0; SetHomeExtras(PlayersText(count), null); return; }
                    // (0.99.4) Steam didn't answer (at start-up the network can still be waking): the dash goes, and it's asked again
                    SetHomeExtras("", null);
                    if (++playersTries <= 4) HomeExtrasRetry(my, 5000 * playersTries);
                });
            }
            if (settings.InfoFriends)
            {
                // Steam friends (with the key): who's playing is asked again at most once a minute, libraries once a day (kept on disk)
                string key = SteamKey;
                string me = key != null ? MySteamId() : null;
                if (g.PluginId == SteamPluginId && key != null && me != null) RefreshFriendsAsync(key, me);
                HomeFriendsCount(g, my);
            }
        }

        /// <summary>Counts the friends for the home line (off the UI thread: PlayerActivities' file can be big).</summary>
        private void HomeFriendsCount(Game g, int my)
        {
            var game = g;
            KippixShared.SafeWork.Queue(_ =>
            {
                int now = 0, played = 0;
                try { FriendCounts(game, out now, out played); } catch (Exception) { }
                OnUi(() => { if (my == homeExtrasGen && settings.InfoFriends) SetHomeExtras(null, FriendsLineText(now, played)); });
            });
        }

        /// <summary>Steam friends' data came in (from RebuildFriendsIfOpen): the home line counts again for the highlighted game.</summary>
        private void HomeFriendsRefresh()
        {
            if (!settings.InfoFriends || homeExtrasGame == Guid.Empty || !IsFullscreen) return;
            var g = SelectedGameNow();
            if (g == null || g.Id != homeExtrasGame) return;
            HomeFriendsCount(g, homeExtrasGen);
        }

        /// <summary>How many friends are playing the game now, and how many have played it (PlayerActivities + Steam). Any thread.</summary>
        private void FriendCounts(Game game, out int now, out int played)
        {
            now = played = 0;
            if (game == null) return;
            var rows = new Dictionary<string, FriendRow>();
            AddPlayerActivitiesFriends(game, rows);
            string appId = game.PluginId == SteamPluginId ? game.GameId : null;
            if (!string.IsNullOrEmpty(appId))
            {
                lock (friendLock)
                {
                    foreach (var pair in friendInfo)
                    {
                        bool playing = pair.Value.PlayingAppId == appId;
                        long[] play = null;
                        Dictionary<string, long[]> lib;
                        if (friendLibraries != null && friendLibraries.TryGetValue(pair.Key, out lib)) lib.TryGetValue(appId, out play);
                        long minutes = play != null ? play[0] : 0;
                        FriendRow row;
                        rows.TryGetValue("Steam:" + pair.Key, out row);
                        if (!playing && minutes <= 0 && row == null) continue;
                        if (row == null) rows["Steam:" + pair.Key] = row = new FriendRow { Store = "Steam" };
                        row.Now = playing;
                        row.Minutes = Math.Max(row.Minutes, minutes);
                    }
                }
            }
            now = rows.Values.Count(r => r.Now);
            played = rows.Count;
        }

        /// <summary>"2 friends playing now" (first), else "3 friends play this"; nothing with none.</summary>
        private string FriendsLineText(int now, int played)
        {
            if (now > 0) return now == 1 ? Loc("LOC_KX_FriendsNowOne", "1 friend playing now") : string.Format(Loc("LOC_KX_FriendsNowMany", "{0} friends playing now"), now);
            if (played > 0) return played == 1 ? Loc("LOC_KX_FriendsPlayOne", "1 friend plays this") : string.Format(Loc("LOC_KX_FriendsPlayMany", "{0} friends play this"), played);
            return "";
        }

        private string PlayersText(long count)
        {
            return count > 0 ? string.Format(Loc("LOC_KX_PlayersNow", "{0} playing now"), count.ToString("N0", CultureInfo.CurrentCulture)) : "";
        }

        /// <summary>Steam's player count for a game: from the 5-minute cache, else asked off the UI thread. <paramref name="done"/> runs on the UI thread.</summary>
        private int playersTries;

        /// <summary>
        /// (0.99.35) The "– playing now" dash is only a placeholder: if it's still there 6 s later for the same game (the look-up never
        /// ran or never came back, as at start-up), the count is asked for again; after a few goes the line goes rather than stay a dash.
        /// </summary>
        private int playersBootChecks;
        private DateTime playersBootLast = DateTime.MinValue;

        /// <summary>
        /// (0.99.36) From the tick, after fullscreen starts: the game it opens on kept "– playing now" (its look-up ran while Playnite
        /// was still starting and was lost). Every 2 s, until the count is showing (or 15 goes), the highlighted Steam game is
        /// looked up again; moving to another game ends it.
        /// </summary>
        private void PlayersBootCheck()
        {
            if (playersBootChecks >= 15 || !IsFullscreen || !settings.InfoPlayers) return;
            if ((DateTime.Now - playersBootLast).TotalSeconds < 2) return;
            playersBootLast = DateTime.Now;
            var g = SelectedGameNow();
            if (g == null) return;
            string dash = Loc("LOC_KX_PlayersWait", "– playing now");
            if (SteamAppIdOf(g) == null || (homePlayersText.Length > 0 && homePlayersText != dash))
            {
                if (playersBootChecks > 0) logger.Info("Kippix: players online: the start-up game's count is showing (after " + playersBootChecks + " look-ups)");
                playersBootChecks = 99;
                return;
            }
            playersBootChecks++;
            if (playersBootChecks == 1) logger.Info("Kippix: players online: looking up \"" + g.Name + "\" again after start-up (" + (homePlayersText.Length == 0 ? "nothing" : "the dash") + " showing)");
            HomeExtrasSelected(g, true);
        }

        private void PlayersDashGuard(int gen)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(6) };
            int goes = 0;
            t.Tick += (s, e) =>
            {
                if (gen != homeExtrasGen || homePlayersText != Loc("LOC_KX_PlayersWait", "– playing now")) { t.Stop(); return; }
                if (++goes > 3) { t.Stop(); SetHomeExtras("", null); logger.Info("Kippix: players online: no count came for the highlighted game, the line is hidden"); return; }
                try { HomeExtrasFill(); } catch (Exception) { }
            };
            t.Start();
        }

        private void HomeExtrasRetry(int gen, int ms)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); if (gen == homeExtrasGen) { try { HomeExtrasFill(); } catch (Exception) { } } };
            t.Start();
        }

        /// <summary>The number playing now (from the cache when it's under 5 minutes old); -1 when Steam couldn't be asked.</summary>
        private void PlayersCountAsync(string appId, Action<long> done)
        {
            PlayersCacheEntry hit;
            lock (playersCache) playersCache.TryGetValue(appId, out hit);
            if (hit != null && (DateTime.UtcNow - hit.At).TotalMinutes < 5) { done(hit.Count); return; }
            KippixShared.SafeWork.Queue(_ =>
            {
                long count = -1;
                try
                {
                    var root = StoreJson("https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid=" + Uri.EscapeDataString(appId)) as Dictionary<string, object>;
                    var resp = Dig(root, "response") as Dictionary<string, object>;
                    if (resp != null) count = FNum(resp, "result") == 1 ? FNum(resp, "player_count") : 0;
                }
                catch (Exception ex) { logger.Info("Kippix: players online for " + appId + " couldn't be read: " + ex.Message); }
                if (count < 0) { OnUi(() => done(-1)); return; }
                lock (playersCache) playersCache[appId] = new PlayersCacheEntry { At = DateTime.UtcNow, Count = count };
                OnUi(() => done(count));
            });
        }

        // ---------------- the game page's friends line (play info, off at first) ----------------

        private string detFriendsLine = "";
        public string DetFriendsLineText { get { return detFriendsLine; } }
        public bool DetInfoShowFriends { get { return DetInfoOn("friends") && detFriendsLine.Length > 0; } }

        /// <summary>From BuildDetFriends: the same words as the home line, for the game page's play info.</summary>
        private void SetDetFriendsLine(List<FriendItem> friends)
        {
            int now = friends != null ? friends.Count(f => f.PlayingNow) : 0;
            int played = friends != null ? friends.Count : 0;
            string text = FriendsLineText(now, played);
            if (text == detFriendsLine) return;
            detFriendsLine = text;
            Notify("DetFriendsLineText");
            Notify("DetInfoShowFriends");
        }
    }
}

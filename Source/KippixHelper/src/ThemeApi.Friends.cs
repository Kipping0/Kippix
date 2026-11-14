using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Input;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>One Steam friend on the game page.</summary>
    public class FriendItem
    {
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
        public string Status { get; set; }
        public bool PlayingNow { get; set; }
    }

    /// <summary>
    /// Steam friends on the game page: who is playing the game right now, and who has played it (time played, when).
    /// Uses the Steam Web API with the user's own key (stored encrypted for their Windows account) and the
    /// Steam account from Playnite's Steam library settings. Friends' libraries are fetched once a day in the
    /// background and kept on disk; who's playing what is refreshed every minute while a game page is open.
    /// Friends whose game details are private only show up while they're playing.
    /// </summary>
    public partial class ThemeApi
    {
        private class FriendInfo
        {
            public string Name, Avatar, PlayingAppId, PlayingName;
            public int State;   // Steam: 0 offline, 1 online, 2 busy, 3 away, 4 snooze, 5 looking to trade, 6 looking to play
        }

        private const string SteamApi = "https://api.steampowered.com/";
        private static readonly TimeSpan FriendSummaryAge = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FriendListAge = TimeSpan.FromHours(6);
        private static readonly TimeSpan FriendLibraryAge = TimeSpan.FromHours(24);
        private const int FriendsShown = 6;

        private readonly object friendLock = new object();
        private List<string> friendIds;
        private DateTime friendIdsAt = DateTime.MinValue;
        private Dictionary<string, FriendInfo> friendInfo = new Dictionary<string, FriendInfo>();
        private DateTime friendInfoAt = DateTime.MinValue;
        // friend -> appid -> [minutes played, last played (unix time)]
        private Dictionary<string, Dictionary<string, long[]>> friendLibraries;
        private DateTime friendLibrariesAt = DateTime.MinValue;
        private bool friendsBusy, librariesBusy;
        private string friendsError = "";

        public ICommand SetSteamKeyCommand { get; private set; }

        private void InitFriendCommands()
        {
            SetSteamKeyCommand = new RelayCommand(_ => AskSteamKey());
        }

        // ---------------- settings ----------------

        public bool DetShowFriends
        {
            get { return settings.DetShowFriends; }
            set { if (SetDet(ref settings.DetShowFriends, value)) UpdateDetFriends(); }
        }

        public bool HasSteamKey { get { return !string.IsNullOrEmpty(SteamKey); } }
        public string SteamKeyText
        {
            get
            {
                if (!HasSteamKey) return Loc("LOC_KX_SteamKeyNone", "Not set");
                return friendsError.Length > 0 ? friendsError : Loc("LOC_KX_SteamKeySet", "Set");
            }
        }

        private void AskSteamKey() { AskSteamKey(null); }

        private void AskSteamKey(Action after)
        {
            string prompt = Loc("LOC_KX_SteamKeyPrompt", "Your Steam Web API key (from steamcommunity.com/dev/apikey). Leave it empty to remove it.");
            if (IsFullscreen)
            {
                OpenKeyboard(prompt, "", typed => { SetSteamKey(typed); if (after != null) after(); }, null, null, 64);
                return;
            }
            var r = api.Dialogs.SelectString(prompt, "Steam", "");
            if (r == null || !r.Result) return;
            SetSteamKey(r.SelectedString);
            if (after != null) after();
        }

        /// <summary>Saves (or with an empty value removes) the Steam Web API key. Also used by the desktop settings page.</summary>
        internal void SetSteamKey(string value)
        {
            string key = (value ?? "").Trim();
            settings.SteamApiKey = key.Length == 0 ? "" : Protect(key);
            SaveSettings();
            lock (friendLock)
            {
                friendIds = null;
                friendIdsAt = friendInfoAt = friendLibrariesAt = DateTime.MinValue;
                friendLibraries = null;
            }
            friendsError = "";
            Notify("HasSteamKey");
            Notify("SteamKeyText");
            Notify("FriendsSourceText");
            UpdateDetFriends();
        }

        private string steamKeyFrom, steamKeyPlain;
        private string SteamKey
        {
            get
            {
                if (string.IsNullOrEmpty(settings.SteamApiKey)) return null;
                if (settings.SteamApiKey != steamKeyFrom)
                {
                    string plain;
                    try { plain = Unprotect(settings.SteamApiKey); } catch (Exception) { plain = null; }
                    steamKeyPlain = plain;   // the key first, then what it came from (read from other threads too)
                    steamKeyFrom = settings.SteamApiKey;
                }
                return steamKeyPlain;
            }
        }

        private string mySteamId;
        /// <summary>The user's SteamID64, from Playnite's Steam library settings.</summary>
        private string MySteamId()
        {
            if (!string.IsNullOrEmpty(mySteamId)) return mySteamId;
            try
            {
                string f = Path.Combine(api.Paths.ExtensionsDataPath, SteamPluginId.ToString(), "config.json");
                if (!File.Exists(f)) return null;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                object id;
                if (d != null && d.TryGetValue("UserId", out id) && id != null) mySteamId = id.ToString();
            }
            catch (Exception) { }
            return mySteamId;
        }

        // ---------------- what the game page shows ----------------
        // Friends come from two places, merged:
        //  1. PlayerActivities (Lacro59), when installed with friends turned on: Steam, GOG, Epic and EA friends,
        //     time played and achievements per game. No key needed.
        //  2. Our own Steam Web API calls, only when the user saved a key: adds who is playing right now and when they last played.

        private List<FriendItem> detFriends = new List<FriendItem>();
        private string detFriendsTitle = "";
        public List<FriendItem> DetFriends { get { return detFriends; } }
        public bool HasDetFriends { get { return settings.DetShowFriends && detFriends.Count > 0; } }
        public string DetFriendsTitle { get { return detFriendsTitle; } }

        /// <summary>The game page wants friends: the Friends card, or (r13) the play info's friends line.</summary>
        private bool DetFriendsWanted { get { return settings.DetShowFriends || DetInfoOn("friends"); } }

        private Game FriendsGame()
        {
            try { return api.MainView.SelectedGames != null ? api.MainView.SelectedGames.FirstOrDefault() : null; } catch (Exception) { return null; }
        }

        private string FriendsAppId()
        {
            var game = FriendsGame();
            if (game == null || game.PluginId != SteamPluginId || string.IsNullOrEmpty(game.GameId)) return null;
            return game.GameId;
        }

        /// <summary>Rebuilds the friends row for the selected game, fetching what's missing in the background.</summary>
        private void UpdateDetFriends()
        {
            var game = detailsOpen && DetFriendsWanted ? FriendsGame() : null;
            if (game == null) { SetDetFriends(new List<FriendItem>(), ""); SetDetFriendsLine(null); return; }
            BuildDetFriends(game);
            string appId = FriendsAppId();
            string key = appId != null ? SteamKey : null;
            string me = key != null ? MySteamId() : null;
            if (appId != null && key != null && me != null) RefreshFriendsAsync(key, me);
        }

        private class FriendRow
        {
            public string Name, Avatar, Store;
            public bool Now;
            public long Minutes, Last, Achievements;
        }

        private void BuildDetFriends(Game game)
        {
            var ordered = FriendItemsFor(game);
            SetDetFriendsLine(ordered);   // (r13) the play info's "2 friends playing now" line (ThemeApi.HomeInfoExtras.cs)
            string title = ordered.Count == 0 ? ""
                : ordered.Count == 1 ? Loc("LOC_KX_FriendsOne", "1 FRIEND HAS PLAYED THIS")
                : string.Format(Loc("LOC_KX_FriendsMany", "{0} FRIENDS HAVE PLAYED THIS"), ordered.Count);
            if (ordered.Count > FriendsShown)
            {
                int more = ordered.Count - FriendsShown;
                ordered = ordered.Take(FriendsShown).ToList();
                ordered.Add(new FriendItem { Name = string.Format(Loc("LOC_KX_FriendsMore", "+{0} more"), more), Status = "" });
            }
            SetDetFriends(ordered, title);
        }

        /// <summary>Friends who play / have played a game (PlayerActivities and Steam), playing now first. Also used by the Friends widget.</summary>
        private List<FriendItem> FriendItemsFor(Game game)
        {
            var rows = new Dictionary<string, FriendRow>();
            AddPlayerActivitiesFriends(game, rows);

            string appId = game.PluginId == SteamPluginId ? game.GameId : null;
            if (appId != null)
            {
                lock (friendLock)
                {
                    foreach (var pair in friendInfo)
                    {
                        bool now = pair.Value.PlayingAppId == appId;
                        long[] play = null;
                        Dictionary<string, long[]> lib;
                        if (friendLibraries != null && friendLibraries.TryGetValue(pair.Key, out lib)) lib.TryGetValue(appId, out play);
                        long minutes = play != null ? play[0] : 0, last = play != null ? play[1] : 0;
                        FriendRow row;
                        rows.TryGetValue("Steam:" + pair.Key, out row);
                        if (!now && minutes <= 0 && row == null) continue;
                        if (row == null) rows["Steam:" + pair.Key] = row = new FriendRow { Store = "Steam" };
                        row.Name = pair.Value.Name;
                        if (!string.IsNullOrEmpty(pair.Value.Avatar)) row.Avatar = pair.Value.Avatar;
                        row.Now = now;
                        row.Minutes = Math.Max(row.Minutes, minutes);
                        if (last > 0) row.Last = last;
                    }
                }
            }

            var ordered = rows.Values
                .OrderByDescending(r => r.Now).ThenByDescending(r => r.Last).ThenByDescending(r => r.Minutes)
                .Select(r => new FriendItem
                {
                    Name = r.Name,
                    AvatarUrl = r.Avatar,
                    PlayingNow = r.Now,
                    Status = r.Now ? Loc("LOC_KX_FriendPlayingNow", "Playing now") : FriendStatus(r, game)
                }).ToList();
            return ordered;
        }

        private string FriendStatus(FriendRow r, Game game)
        {
            string text = PlayedText(r.Minutes, r.Last);
            if (r.Achievements > 0) text += "  ·  " + string.Format(Loc("LOC_KX_FriendAch", "{0} achievements"), r.Achievements);
            // say which store when it isn't the one the game is from
            string store = StoreOf(game);
            if (!string.IsNullOrEmpty(r.Store) && r.Store != store) text = r.Store + "  ·  " + text;
            return text;
        }

        private void SetDetFriends(List<FriendItem> items, string title)
        {
            detFriends = items;
            detFriendsTitle = title;
            Notify("DetFriends");
            Notify("HasDetFriends");
            Notify("DetFriendsTitle");
        }

        private string PlayedText(long minutes, long lastUnix)
        {
            string time = minutes < 60 ? string.Format(Loc("LOC_KX_FriendMinutes", "{0} min"), minutes)
                                       : string.Format(Loc("LOC_KX_FriendHours", "{0} h"), (minutes / 60).ToString("N0", CultureInfo.CurrentCulture));
            if (lastUnix <= 0) return time;
            var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(lastUnix).ToLocalTime();
            int days = (int)(DateTime.Now.Date - when.Date).TotalDays;
            string ago = days <= 0 ? Loc("LOC_KX_Today", "today")
                       : days == 1 ? Loc("LOC_KX_Yesterday", "yesterday")
                       : days < 14 ? string.Format(Loc("LOC_KX_DaysAgo", "{0} days ago"), days)
                       : days < 60 ? string.Format(Loc("LOC_KX_WeeksAgo", "{0} weeks ago"), days / 7)
                       : days < 365 ? string.Format(Loc("LOC_KX_MonthsAgo", "{0} months ago"), days / 30)
                       : when.ToString("MMM yyyy", CultureInfo.CurrentCulture);
            return time + "  ·  " + ago;
        }

        // ---------------- PlayerActivities ----------------

        private static readonly Guid PlayerActivitiesId = new Guid("3dbe2ee4-d6c9-4c09-b377-7a086369603e");
        private static readonly Guid GogPluginId = new Guid("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");
        private static readonly Guid EpicPluginId = new Guid("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
        private static readonly Guid EaPluginId = new Guid("85dd7072-2f20-4e76-a007-41035e390724");

        private class PaFriend
        {
            public string Store, Id, Name, Avatar;
            public List<PaGame> Games = new List<PaGame>();
        }
        private class PaGame { public string Id, Key; public long Minutes, Achievements; }

        private List<PaFriend> paFriends;
        private DateTime paFileTime = DateTime.MinValue;

        private string PlayerActivitiesFile
        {
            get { return Path.Combine(api.Paths.ExtensionsDataPath, PlayerActivitiesId.ToString(), "FriendsData.json"); }
        }

        /// <summary>True when PlayerActivities has saved a friends list we can read.</summary>
        public bool HasPlayerActivities { get { try { return File.Exists(PlayerActivitiesFile); } catch (Exception) { return false; } } }

        private static string StoreOf(Game game)
        {
            if (game == null) return "";
            if (game.PluginId == SteamPluginId) return "Steam";
            if (game.PluginId == GogPluginId) return "GOG";
            if (game.PluginId == EpicPluginId) return "Epic";
            if (game.PluginId == EaPluginId) return "EA";
            return "";
        }

        /// <summary>Lower-case letters and digits only, so "DOOM Eternal™" and "Doom Eternal" match.</summary>
        private static string NameKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Reads PlayerActivities' FriendsData.json (again only when the file changes).</summary>
        private List<PaFriend> LoadPlayerActivities()
        {
            try
            {
                string f = PlayerActivitiesFile;
                if (!File.Exists(f)) { paFriends = null; return null; }
                var t = File.GetLastWriteTimeUtc(f);
                if (paFriends != null && t == paFileTime) return paFriends;
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                var list = new List<PaFriend>();
                object pf;
                var friends = root != null && root.TryGetValue("PlayerFriends", out pf) ? pf as IEnumerable : null;
                if (friends != null)
                    foreach (object o in friends)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        object isUser;
                        if (d.TryGetValue("IsUser", out isUser) && isUser is bool && (bool)isUser) continue;   // that's you
                        var friend = new PaFriend { Store = FStr(d, "ClientName"), Id = FStr(d, "FriendId"), Name = FStr(d, "FriendPseudo"), Avatar = FStr(d, "FriendsAvatar") };
                        bool seconds = friend.Store == "Epic" || friend.Store == "EA";   // those stores report seconds, Steam and GOG minutes
                        object games;
                        var gl = d.TryGetValue("Games", out games) ? games as IEnumerable : null;
                        if (gl != null)
                            foreach (object go in gl)
                            {
                                var g = go as Dictionary<string, object>;
                                if (g == null) continue;
                                long play = FNum(g, "Playtime");
                                friend.Games.Add(new PaGame
                                {
                                    Id = FStr(g, "Id"),
                                    Key = NameKey(FStr(g, "Name")),
                                    Minutes = seconds ? play / 60 : play,
                                    Achievements = FNum(g, "Achievements")
                                });
                            }
                        list.Add(friend);
                    }
                paFriends = list;
                paFileTime = t;
                return list;
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: couldn't read PlayerActivities' friends: " + ex.Message);
                return paFriends;
            }
        }

        /// <summary>Friends (any store) who have played this game, matched by store id or by name.</summary>
        private void AddPlayerActivitiesFriends(Game game, Dictionary<string, FriendRow> rows)
        {
            var friends = LoadPlayerActivities();
            if (friends == null) return;
            string store = StoreOf(game);
            string key = NameKey(game.Name);
            foreach (var f in friends)
            {
                PaGame hit = null;
                foreach (var g in f.Games)
                {
                    if ((f.Store == store && !string.IsNullOrEmpty(g.Id) && g.Id == game.GameId) || (key.Length > 0 && g.Key == key)) { hit = g; break; }
                }
                if (hit == null || (hit.Minutes <= 0 && hit.Achievements <= 0)) continue;
                rows[f.Store + ":" + f.Id] = new FriendRow
                {
                    Name = f.Name, Avatar = f.Avatar, Store = f.Store,
                    Minutes = hit.Minutes, Achievements = hit.Achievements
                };
            }
        }

        /// <summary>What the options show about where friends come from.</summary>
        public string FriendsSourceText
        {
            get
            {
                string pa = HasPlayerActivities ? Loc("LOC_KX_PaFound", "PlayerActivities: found") : Loc("LOC_KX_PaMissing", "PlayerActivities: not installed");
                string key = HasSteamKey ? Loc("LOC_KX_KeySaved", "Steam key: saved") : Loc("LOC_KX_KeyNone", "Steam key: not set");
                return pa + "   ·   " + key;
            }
        }

        // ---------------- Steam Web API ----------------

        private void RefreshFriendsAsync(string key, string me)
        {
            bool needSummaries;
            lock (friendLock) needSummaries = DateTime.Now - friendInfoAt > FriendSummaryAge;
            if (needSummaries && !friendsBusy)
            {
                friendsBusy = true;
                KippixShared.SafeWork.Queue(_ =>
                {
                    string error = "";
                    try { FetchSummaries(key, me); }
                    catch (WebException ex)
                    {
                        var resp = ex.Response as HttpWebResponse;
                        error = resp != null && (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
                            ? Loc("LOC_KX_SteamKeyBad", "Key not accepted (or friends list private)") : "";
                        logger.Warn("Kippix: Steam friends failed: " + ex.Message);
                    }
                    catch (Exception ex) { logger.Warn("Kippix: Steam friends failed: " + ex.Message); }
                    lock (friendLock) friendInfoAt = DateTime.Now;   // after a failure too, so it waits a minute before trying again
                    friendsBusy = false;
                    OnUi(() =>
                    {
                        if (error != friendsError) { friendsError = error; Notify("SteamKeyText"); }
                        RebuildFriendsIfOpen();
                        RefreshWidgetsOfType("friends");   // the Friends online widget fills in now, not at its next turn
                        StartLibraries(key);
                    });
                });
            }
            else StartLibraries(key);
        }

        private void FetchSummaries(string key, string me)
        {
            List<string> ids;
            lock (friendLock) ids = DateTime.Now - friendIdsAt < FriendListAge ? friendIds : null;
            if (ids == null)
            {
                var root = GetJson(SteamApi + "ISteamUser/GetFriendList/v1/?relationship=friend&key=" + Uri.EscapeDataString(key) + "&steamid=" + me);
                ids = new List<string>();
                var list = Path3(root, "friendslist", "friends") as IEnumerable;
                if (list != null)
                    foreach (object o in list)
                    {
                        var f = o as Dictionary<string, object>;
                        object id;
                        if (f != null && f.TryGetValue("steamid", out id) && id != null) ids.Add(id.ToString());
                    }
                lock (friendLock) { friendIds = ids; friendIdsAt = DateTime.Now; }
            }
            var info = new Dictionary<string, FriendInfo>();
            for (int i = 0; i < ids.Count; i += 100)
            {
                string batch = string.Join(",", ids.Skip(i).Take(100).ToArray());
                var root = GetJson(SteamApi + "ISteamUser/GetPlayerSummaries/v2/?key=" + Uri.EscapeDataString(key) + "&steamids=" + batch);
                var players = Path3(root, "response", "players") as IEnumerable;
                if (players == null) continue;
                foreach (object o in players)
                {
                    var p = o as Dictionary<string, object>;
                    if (p == null) continue;
                    int state = 0;
                    try { object sv; if (p.TryGetValue("personastate", out sv) && sv != null) state = Convert.ToInt32(sv); } catch (Exception) { }
                    info[FStr(p, "steamid")] = new FriendInfo { Name = FStr(p, "personaname"), Avatar = FStr(p, "avatarmedium"), PlayingAppId = FStr(p, "gameid"), PlayingName = FStr(p, "gameextrainfo"), State = state };
                }
            }
            lock (friendLock) { friendInfo = info; friendInfoAt = DateTime.Now; }
        }

        private string FriendCacheFile { get { return Path.Combine(plugin.GetPluginUserDataPath(), "SteamFriends.json"); } }

        /// <summary>Friends' libraries (time played per game): from disk if fresh, otherwise fetched one friend at a time.</summary>
        private void StartLibraries(string key)
        {
            if (librariesBusy) return;
            lock (friendLock)
            {
                if (friendIds == null) return;
                if (friendLibraries != null && DateTime.Now - friendLibrariesAt < FriendLibraryAge) return;
            }
            librariesBusy = true;
            List<string> ids;
            lock (friendLock) ids = friendIds.ToList();
            KippixShared.SafeWork.Queue(_ =>
            {
                try
                {
                    // 1. the copy on disk, if it's from today
                    if (friendLibraries == null && File.Exists(FriendCacheFile) && DateTime.Now - File.GetLastWriteTime(FriendCacheFile) < FriendLibraryAge)
                    {
                        var cached = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                            .Deserialize<Dictionary<string, Dictionary<string, long[]>>>(File.ReadAllText(FriendCacheFile));
                        if (cached != null)
                        {
                            lock (friendLock) { friendLibraries = cached; friendLibrariesAt = File.GetLastWriteTime(FriendCacheFile); }
                            OnUi(RebuildFriendsIfOpen);
                            return;
                        }
                    }
                    // 2. ask Steam, one friend at a time (shown as they arrive)
                    var libs = new Dictionary<string, Dictionary<string, long[]>>();
                    int n = 0;
                    foreach (string id in ids)
                    {
                        try
                        {
                            var root = GetJson(SteamApi + "IPlayerService/GetOwnedGames/v1/?include_played_free_games=1&key=" + Uri.EscapeDataString(key) + "&steamid=" + id);
                            var games = Path3(root, "response", "games") as IEnumerable;
                            var lib = new Dictionary<string, long[]>();
                            if (games != null)
                                foreach (object o in games)
                                {
                                    var g = o as Dictionary<string, object>;
                                    if (g == null) continue;
                                    long minutes = FNum(g, "playtime_forever");
                                    if (minutes <= 0) continue;
                                    lib[FStr(g, "appid")] = new[] { minutes, FNum(g, "rtime_last_played") };
                                }
                            libs[id] = lib;   // empty when their game details are private
                        }
                        catch (Exception) { }
                        if (++n % 10 == 0)
                        {
                            lock (friendLock) friendLibraries = new Dictionary<string, Dictionary<string, long[]>>(libs);
                            OnUi(RebuildFriendsIfOpen);
                        }
                        Thread.Sleep(150);
                    }
                    lock (friendLock) { friendLibraries = libs; friendLibrariesAt = DateTime.Now; }
                    try { File.WriteAllText(FriendCacheFile, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(libs)); } catch (Exception) { }
                    OnUi(RebuildFriendsIfOpen);
                }
                catch (Exception ex) { logger.Warn("Kippix: friends' libraries failed: " + ex.Message); }
                finally { librariesBusy = false; }
            });
        }

        private void RebuildFriendsIfOpen()
        {
            if (openPanel == "page" && pageKey == "friends") FillFriends();
            var game = detailsOpen && DetFriendsWanted ? FriendsGame() : null;
            if (game != null) BuildDetFriends(game);
            HomeFriendsRefresh();   // (r13) the home info line's friends, when it's on
        }

        private static Dictionary<string, object> GetJson(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Kippix (Playnite add-on)";
                web.Encoding = Encoding.UTF8;
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(web.DownloadString(url)) as Dictionary<string, object>;
            }
        }

        private static object Path3(Dictionary<string, object> root, string a, string b)
        {
            object x, y;
            if (root == null || !root.TryGetValue(a, out x)) return null;
            var d = x as Dictionary<string, object>;
            return d != null && d.TryGetValue(b, out y) ? y : null;
        }

        private static string FStr(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static long FNum(Dictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }

        // ---------------- keeping the key private (Windows DPAPI, this user only) ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private const int CryptUiForbidden = 0x1;

        private static string Protect(string text)
        {
            return Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(text), true));
        }

        private static string Unprotect(string base64)
        {
            return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(base64), false));
        }

        private static byte[] Dpapi(byte[] data, bool protect)
        {
            var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            var output = new DataBlob();
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "Kippix", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptUiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptUiForbidden, ref output);
                if (!ok) throw new InvalidOperationException("DPAPI failed: " + Marshal.GetLastWin32Error());
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }
}

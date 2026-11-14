using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// The profile hub's Overview tab (profile menu › Profile): a monthly recap (time played, sessions, achievements, captures;
    /// LT / RT for other months), the games you played last (Jump back in), most played this month, recent achievements,
    /// a low-key play streak (never a reminder, just a line here, and it can be switched off), highlights, and buttons for
    /// switching profile, your name and picture and which tabs show. Also Compare with a friend: side by side achievements
    /// for a game you both have (Steam Web API key).
    /// </summary>
    public partial class ThemeApi
    {
        private DateTime profileMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

        private void OpenProfilePage()
        {
            OpenProfileHubFromMenu();
        }

        // Name and picture opened from the profile's Overview: B on it goes back there (not to the profile menu)
        private bool hubMeReturn;
        private string hubMeReturnKey, hubMeReturnPage;

        /// <summary>B on Your name and picture, when it was opened from the profile: back to the Overview, on that button.</summary>
        private bool BackFromMeToHub()
        {
            if (!hubMeReturn || openPanel != "profile" || profilePage != "me") { hubMeReturn = false; return false; }
            hubMeReturn = false;
            string rk = hubMeReturnKey, rp = hubMeReturnPage;
            PlaySound("back");
            ClosePanels();
            pageQuietOpen = true;
            OpenProfileHub("overview", null);
            if (openPanel != "page") return true;
            profileReturnKey = rk;   // (B on the profile then goes where it went before: the profile menu, or home)
            profileReturnPage = rp ?? "pages";
            FocusPageSoon(PageChips.FirstOrDefault(c => c.Icon == "profile"));   // (after the tab: the same priority, queued later)
            return true;
        }

        /// <summary>The sessions that belong to whoever is playing (everything when profiles are off, or for the main profile).</summary>
        /// <summary>(from a worker: ownerView and profileId are read on the UI thread first)</summary>
        private List<PlaySession> SessionsForProfile(out string source, int days, bool ownerView, string profileId)
        {
            var all = LoadSessions(out source, days);
            if (ownerView) return all;
            // other profiles: only the sessions the theme recorded for them
            return OwnSessions(days).Where(s => s.Profile == profileId).ToList();
        }

        private class RecentAch
        {
            public AchItemData Item;
            public string Game;
            public Guid GameId;
        }

        /// <summary>What the Overview adds under the month (worked out off the UI thread with the rest).</summary>
        private class HubExtras
        {
            public Dictionary<DateTime, PlayDay> Heat;       // the play calendar (null: switched off)
            public DateTime HeatFrom, HeatTo;
            public Dictionary<DateTime, PlayDay> MonthDays = new Dictionary<DateTime, PlayDay>();   // real play in the month (highlights)
            public HashSet<DateTime> PlayedDays = new HashSet<DateTime>();                         // real play in the last year (streaks)
            public FinishedInfo Finished;
            public List<ClosestAch> Closest;
        }

        private void BuildProfilePage()
        {
            PageTitle = ProfileName;
            var m = profileMonth;
            bool thisMonth = m.Year == DateTime.Now.Year && m.Month == DateTime.Now.Month;
            PageSub = (thisMonth ? Loc("LOC_KX_ThisMonth", "This month") : m.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
            if (ProfilesOn)
                AddPageChip(Loc("LOC_KX_SwitchProfile", "Switch profile"), "friends", () => { PlaySound("select"); ClosePanels(); ui.BeginInvoke(new Action(OpenWho), DispatcherPriority.Loaded); });
            AddPageChip(Loc("LOC_KX_HubEditMe", "Name and picture"), "profile", () =>
            {
                // B there comes back here (on this button), and B here then goes where it went before
                string rk = profileReturnKey, rp = profileReturnPage;
                ClosePanels();
                ui.BeginInvoke(new Action(() =>
                {
                    ToggleProfile();
                    if (openPanel != "profile") return;
                    SetProfilePage("me");
                    hubMeReturn = true;
                    hubMeReturnKey = rk;
                    hubMeReturnPage = rp;
                }), DispatcherPriority.Loaded);
            });
            var streakChip = AddPageChip(settings.ShowStreaks ? Loc("LOC_KX_StreaksOn", "Streaks: on") : Loc("LOC_KX_StreaksOff", "Streaks: off"), "flag", () =>
            {
                settings.ShowStreaks = !settings.ShowStreaks;
                SaveSettings();
                PlaySound("move");
                pageQuietOpen = true;
                OpenPage("profilepage", "", false);
                FocusPageSoon(PageChips.FirstOrDefault(c => c.Icon == "flag"));
            });
            streakChip.IsSelected = settings.ShowStreaks;
            AddPageChip(Loc("LOC_KX_HubTabsTitle", "Profile tabs"), "tune", () => OpenPage("hubtabs", "", true));
            AddShareChip(0);
            SetPageStatus(Loc("LOC_KX_Counting", "Counting…"));
            int gen = pageGen;
            var achs = achData;
            bool ownerView = !ProfilesOn || IsOwnerProfile;
            string profileId = CurrentProfileId;
            var lastMap = profileLastPlayed;
            bool showHeat = HubSectionShown("heat"), showFinished = HubSectionShown("finished"), showClosest = HubSectionShown("closest");
            KippixShared.SafeWork.Queue(_ =>
            {
                string source;
                List<PlaySession> sessions, recent;
                var extras = new HubExtras();
                try
                {
                    // (the calendar reaches a year back from the end of this month)
                    int days = (int)(DateTime.Now - m).TotalDays + 400;
                    var all = SessionsForProfile(out source, Math.Max(days, 400), ownerView, profileId);
                    sessions = all.Where(s => s.Seconds > 0 && s.Start.Year == m.Year && s.Start.Month == m.Month).ToList();
                    recent = all.Where(s => s.Seconds > 0 && s.Start >= DateTime.Today.AddDays(-400)).ToList();
                    // highlights, streaks and the calendar: timed sessions only, split at midnight (ThemeApi.Heatmap.cs)
                    var monthEnd = m.AddMonths(1).AddDays(-1);
                    extras.MonthDays = RealPlayDays(all, m, monthEnd);
                    extras.PlayedDays = new HashSet<DateTime>(RealPlayDays(recent, DateTime.Today.AddDays(-400), DateTime.Today).Where(kv => kv.Value.Seconds > 0).Select(kv => kv.Key));
                    if (showHeat)
                    {
                        extras.HeatTo = monthEnd > DateTime.Today ? DateTime.Today : monthEnd;
                        extras.HeatFrom = extras.HeatTo.AddDays(-364);
                        extras.Heat = RealPlayDays(all, extras.HeatFrom, extras.HeatTo);
                    }
                }
                catch (Exception) { sessions = new List<PlaySession>(); recent = sessions; source = ""; }
                try { if (showFinished) extras.Finished = FinishedIn(DateTime.Now.Year, ownerView); } catch (Exception) { }
                try { if (showClosest && ownerView) extras.Closest = ClosestAchievements(achs); } catch (Exception) { }
                int captures = 0;
                try { captures = CountCaptures(d => d.Year == m.Year && d.Month == m.Month); } catch (Exception) { }
                int unlocked = 0;
                var lately = new List<RecentAch>();
                try
                {
                    if (achs != null && ownerView)
                        foreach (var d in achs.ToList())
                            if (d != null && d.Items != null)
                                foreach (var i in d.Items.ToList())
                                {
                                    if (i == null || !i.Unlocked || !i.Date.HasValue) continue;
                                    if (i.Date.Value.Year == m.Year && i.Date.Value.Month == m.Month) unlocked++;
                                    lately.Add(new RecentAch { Item = i, Game = d.Name, GameId = d.Id });
                                }
                    lately = lately.OrderByDescending(a => a.Item.Date.Value).Take(12).ToList();   // (0.99.24: a shelf of them)
                }
                catch (Exception) { }   // (a worker thread: nothing may escape)
                // jump back in: the games played last (this profile's own when it isn't the main one)
                var jump = new List<Tuple<Game, DateTime>>();
                try
                {
                    if (ownerView)
                        jump = api.Database.Games.Where(g => HubSees(g, true) && g.LastActivity.HasValue).OrderByDescending(g => g.LastActivity.Value)
                                  .Take(8).Select(g => Tuple.Create(g, g.LastActivity.Value)).ToList();
                    else if (lastMap != null)
                        foreach (var kv in lastMap.OrderByDescending(x => x.Value))
                        {
                            Guid id;
                            if (!Guid.TryParse(kv.Key, out id)) continue;
                            var g = api.Database.Games.Get(id);
                            if (g == null || !HubSees(g, false)) continue;
                            jump.Add(Tuple.Create(g, kv.Value));
                            if (jump.Count >= 8) break;
                        }
                }
                catch (Exception) { }
                OnUi(() => { if (gen == pageGen && pageKey == "profilepage") FillProfilePage(m, sessions, extras, captures, unlocked, achs != null && achs.Count > 0 && ownerView, lately, jump); });
            });
        }

        private void FillProfilePage(DateTime m, List<PlaySession> sessions, HubExtras extras, int captures, int unlocked, bool haveAch, List<RecentAch> lately, List<Tuple<Game, DateTime>> jump)
        {
            if (extras.Heat != null) ShowPageHeatmap(extras.HeatFrom, extras.HeatTo, extras.Heat, Loc("LOC_KX_HeatTitle", "PLAY CALENDAR"));
            if (extras.Finished != null) ShowFinishedShelf(extras.Finished, DateTime.Now.Year);
            double hours = sessions.Sum(s => s.Seconds) / 3600.0;
            var byGame = sessions.GroupBy(s => s.GameId ?? s.Name).Select(g => new { Key = g.Key, Name = g.First().Name, Seconds = g.Sum(x => x.Seconds), Count = g.Count() })
                                 .OrderByDescending(g => g.Seconds).ToList();
            PageStats = new List<PageStat>
            {
                new PageStat { Value = HoursShort(hours), Label = Loc("LOC_KX_StatPlayed", "played") },
                new PageStat { Value = byGame.Count.ToString(), Label = Loc("LOC_KX_StatGames", "games") },
                new PageStat { Value = sessions.Count.ToString(CultureInfo.CurrentCulture), Label = Loc("LOC_KX_StatSessions", "sessions") },
                new PageStat { Value = haveAch ? unlocked.ToString("N0", CultureInfo.CurrentCulture) : "—", Label = Loc("LOC_KX_StatAchievements", "achievements") },
                new PageStat { Value = captures.ToString("N0", CultureInfo.CurrentCulture), Label = Loc("LOC_KX_StatCaptures", "captures") }
            };
            // the covers: what you played last (A opens the game)
            PageTiles.Clear();
            PageTilesTitle = jump.Count > 0 ? Loc("LOC_KX_HubJumpBackIn", "JUMP BACK IN") : "";
            foreach (var j in jump)
            {
                var gid = j.Item1.Id;
                PageTiles.Add(new PageTile
                {
                    Name = j.Item1.Name, GameId = gid, CoverPath = CoverOf(gid),
                    Sub = j.Item1.IsRunning ? Loc("LOC_KX_HubRunning", "Playing now") : AgoText(j.Item2),
                    Command = new RelayCommand(_ => OpenGameFromPage(gid))
                });
            }
            PageRows.Clear();
            if (byGame.Count > 0)
            {
                AddPageHeader(Loc("LOC_KX_TopGamesMonth", "MOST PLAYED THIS MONTH"));
                double max = Math.Max(1, byGame[0].Seconds);
                foreach (var g in byGame.Take(5))
                {
                    Guid id;
                    Guid.TryParse(g.Key ?? "", out id);
                    var gid = id;
                    PageRows.Add(new PageRow
                    {
                        Text = g.Name, Key = "top:" + g.Key, ImagePath = id != Guid.Empty ? CoverOf(id) : null, Icon = "play",
                        Sub = string.Format(Loc("LOC_KX_NSessions", "{0} sessions"), g.Count), Right = HoursShort(g.Seconds / 3600.0),
                        HasBar = true, BarWidth = Math.Max(6, 420 * g.Seconds / max), BarColor = "#FF8AB4FF",
                        Command = gid != Guid.Empty ? new RelayCommand(_ => OpenGameFromPage(gid)) : null
                    });
                }
            }
            if (settings.ShowStreaks)
            {
                int now, best;
                Streaks(extras.PlayedDays, out now, out best);
                if (now >= 2 || best >= 3)
                {
                    AddPageHeader(Loc("LOC_KX_Streaks", "PLAY STREAK"));
                    if (now >= 2) PageRows.Add(new PageRow { Icon = "flag", Text = string.Format(Loc("LOC_KX_StreakNow", "{0} days in a row"), now), Sub = Loc("LOC_KX_StreakNowSub", "Days with some play, up to today") });
                    if (best >= 3) PageRows.Add(new PageRow { Icon = "star", Text = string.Format(Loc("LOC_KX_StreakBest", "Longest: {0} days"), best), Sub = Loc("LOC_KX_StreakBestSub", "In the last year") });
                }
            }
            // (0.99.24) recent achievements, your captures and the wishlist are shelves of their own now (ThemeApi.ProfileShelves.cs)
            AddClosestRows(extras.Closest);   // CLOSEST ACHIEVEMENTS (ThemeApi.NextUp.cs)
            AddHighlightRows(sessions, extras.MonthDays, false);
            FillHubShelves(lately);
            SetPageStatus(sessions.Count == 0 && jump.Count == 0 ? Loc("LOC_KX_MonthEmpty", "No play sessions this month yet.") : "");
            NotifyPage();
            NotifyPageLists();
            LoadPageImages();
            FocusPageSoon(null);
        }

        /// <summary>
        /// HIGHLIGHTS (the month here, the year on Your year): only timed sessions, as a filled-in one (a Steam sync bringing in
        /// hours, Steam's last two weeks) is a total put on one day, not a session; a session past midnight counts on both days,
        /// so no day is ever over 24 hours. With no timed sessions there are no highlights (the totals above still count everything).
        /// </summary>
        private void AddHighlightRows(List<PlaySession> sessions, Dictionary<DateTime, PlayDay> days, bool year)
        {
            var real = sessions.Where(s => s != null && !s.Estimated && s.Seconds > 0).ToList();
            var played = days != null ? days.Values.Where(d => d.Seconds > 0).ToList() : new List<PlayDay>();
            if (real.Count == 0 || played.Count == 0) return;
            AddPageHeader(Loc("LOC_KX_Highlights", "HIGHLIGHTS"));
            var longest = real.OrderByDescending(s => s.Seconds).First();
            PageRows.Add(new PageRow { Icon = "hourglass", Text = Loc("LOC_KX_LongestSession", "Longest session"), Sub = longest.Name + "   ·   " + longest.Start.ToString("d MMMM", CultureInfo.CurrentCulture), Right = HoursShort(longest.Seconds / 3600.0) });
            var busiest = played.OrderByDescending(d => d.Seconds).First();
            PageRows.Add(new PageRow { Icon = "calendar", Text = Loc("LOC_KX_BusiestDay", "Busiest day"), Sub = busiest.Day.ToString("dddd d MMMM", CultureInfo.CurrentCulture), Right = HoursShort(busiest.Seconds / 3600.0) });
            if (year)
            {
                var weekday = played.GroupBy(d => d.Day.DayOfWeek).OrderByDescending(g => g.Sum(d => d.Seconds)).First();
                PageRows.Add(new PageRow { Icon = "clock", Text = Loc("LOC_KX_FavouriteDay", "Favourite day to play"), Sub = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(weekday.Key) });
            }
            else PageRows.Add(new PageRow { Icon = "clock", Text = Loc("LOC_KX_DaysPlayed", "Days you played"), Right = played.Count.ToString(CultureInfo.CurrentCulture) });
        }

        /// <summary>Days in a row with some (timed) play (up to today, or yesterday if nothing yet today), and the longest run.</summary>
        private static void Streaks(HashSet<DateTime> days, out int now, out int best)
        {
            if (days == null) days = new HashSet<DateTime>();
            now = 0;
            var d = DateTime.Today;
            if (!days.Contains(d)) d = d.AddDays(-1);
            while (days.Contains(d)) { now++; d = d.AddDays(-1); }
            best = 0;
            int run = 0;
            DateTime prev = DateTime.MinValue;
            foreach (var day in days.OrderBy(x => x))
            {
                run = prev != DateTime.MinValue && (day - prev).TotalDays == 1 ? run + 1 : 1;
                if (run > best) best = run;
                prev = day;
            }
        }

        private void StepProfileMonth(int step)
        {
            var next = profileMonth.AddMonths(step);
            if (next > DateTime.Now) { PlaySound("back"); return; }
            profileMonth = next;
            PlaySound("move");
            pageQuietOpen = true;
            OpenPage("profilepage", "", false);
        }

        /// <summary>Screenshots and clips (the overlay's folders) whose date passes the test.</summary>
        private int CountCaptures(Func<DateTime, bool> when)
        {
            int captures = 0;
            string shots, clipsText;
            List<string> clips;
            OverlayFolders(out shots, out clips, out clipsText);
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d0 in new[] { shots }.Concat(clips ?? new List<string>()).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool isVideos = string.Equals(d0.TrimEnd('\\'), videos.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                string dir = isVideos ? System.IO.Path.Combine(videos, "Captures") : d0;
                if (!System.IO.Directory.Exists(dir)) continue;
                foreach (string f in System.IO.Directory.EnumerateFiles(dir, "*.*", isVideos ? System.IO.SearchOption.TopDirectoryOnly : System.IO.SearchOption.AllDirectories))
                {
                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".mp4" || ext == ".webp") && when(System.IO.File.GetLastWriteTime(f)) && seen.Add(f)) captures++;
                    if (captures > 99999) break;
                }
            }
            return captures;
        }

        // =====================================================================
        // Compare with a friend: a game you both have, achievements side by side
        // =====================================================================

        private class AchCompare
        {
            public string Name, Description, Icon;
            public bool Me, Them;
            public DateTime? MeAt, ThemAt;
        }

        private void BuildComparePage(string arg)
        {
            var parts = (arg ?? "").Split('|');
            string friend = parts.Length > 0 ? parts[0] : "", appId = parts.Length > 1 ? parts[1] : "";
            FriendInfo f;
            lock (friendLock) friendInfo.TryGetValue(friend, out f);
            string friendName = f != null ? f.Name : Loc("LOC_KX_YourFriend", "Your friend");
            Game mine = null;
            var owned = OwnedSteamGames();
            owned.TryGetValue(appId, out mine);
            PageTitle = mine != null ? mine.Name : Loc("LOC_KX_CompareTitle", "Achievements");
            PageSub = string.Format(Loc("LOC_KX_CompareSub", "You and {0}"), friendName);
            string key = SteamKey, me = MySteamId();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(me))
            {
                SetPageStatus(Loc("LOC_KX_FriendsNoKey", "Friends need your own Steam Web API key (free, from steamcommunity.com/dev/apikey). It's kept encrypted for your Windows account."));
                return;
            }
            SetPageStatus(Loc("LOC_KX_Checking", "Checking…"));
            int gen = pageGen;
            string themName = friendName;
            KippixShared.SafeWork.Queue(_ =>
            {
                List<AchCompare> list = null;
                string error = null;
                try { list = CompareAchievements(key, me, friend, appId); }
                catch (Exception ex) { error = ex.Message; }
                OnUi(() =>
                {
                    if (gen != pageGen || pageKey != "compare") return;
                    if (list == null || list.Count == 0)
                    {
                        SetPageStatus(error != null && error.Contains("403") ? string.Format(Loc("LOC_KX_ComparePrivate", "{0}'s achievements for this game are private."), themName)
                                                                              : Loc("LOC_KX_CompareNone", "This game has no Steam achievements to compare."));
                        NotifyPage();
                        return;
                    }
                    FillCompare(list, themName);
                });
            });
        }

        private static List<AchCompare> CompareAchievements(string key, string me, string friend, string appId)
        {
            var schema = SteamGet("ISteamUserStats/GetSchemaForGame/v2/?key=" + Uri.EscapeDataString(key) + "&appid=" + appId + "&l=english");
            object[] defs = null;
            var game = schema != null && schema.ContainsKey("game") ? schema["game"] as Dictionary<string, object> : null;
            var stats = game != null && game.ContainsKey("availableGameStats") ? game["availableGameStats"] as Dictionary<string, object> : null;
            if (stats != null && stats.ContainsKey("achievements")) defs = stats["achievements"] as object[];
            if (defs == null || defs.Length == 0) return new List<AchCompare>();
            var mine = PlayerAchs(key, me, appId);
            var theirs = PlayerAchs(key, friend, appId);
            var list = new List<AchCompare>();
            foreach (var o in defs)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string api = FStr(d, "name");
                DateTime? a, b;
                bool hasA = mine.TryGetValue(api, out a), hasB = theirs.TryGetValue(api, out b);
                list.Add(new AchCompare { Name = FStr(d, "displayName"), Description = FStr(d, "description"), Icon = FStr(d, "icon"), Me = hasA, Them = hasB, MeAt = a, ThemAt = b });
            }
            return list;
        }

        /// <summary>A player's unlocked achievements for a game (api name → when). Throws on a private profile (403).</summary>
        private static Dictionary<string, DateTime?> PlayerAchs(string key, string steamId, string appId)
        {
            var map = new Dictionary<string, DateTime?>();
            var root = SteamGet("ISteamUserStats/GetPlayerAchievements/v1/?key=" + Uri.EscapeDataString(key) + "&steamid=" + steamId + "&appid=" + appId);
            var ps = root != null && root.ContainsKey("playerstats") ? root["playerstats"] as Dictionary<string, object> : null;
            var arr = ps != null && ps.ContainsKey("achievements") ? ps["achievements"] as object[] : null;
            if (arr == null) return map;
            foreach (var o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || FNum(d, "achieved") != 1) continue;
                long t = FNum(d, "unlocktime");
                map[FStr(d, "apiname")] = t > 0 ? (DateTime?)new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime() : null;
            }
            return map;
        }

        private static Dictionary<string, object> SteamGet(string path)
        {
            return GetJson(SteamApi + path);
        }

        private void FillCompare(List<AchCompare> list, string them)
        {
            int me = list.Count(a => a.Me), they = list.Count(a => a.Them);
            PageStats = new List<PageStat>
            {
                new PageStat { Value = me + " / " + list.Count, Label = Loc("LOC_KX_You", "you") },
                new PageStat { Value = they + " / " + list.Count, Label = them },
                new PageStat { Value = list.Count(a => a.Me && a.Them).ToString(CultureInfo.CurrentCulture), Label = Loc("LOC_KX_BothHave", "both") }
            };
            PageRows.Clear();
            Action<string, IEnumerable<AchCompare>> group = (head, items) =>
            {
                var l = items.ToList();
                if (l.Count == 0) return;
                AddPageHeader(head + "  (" + l.Count + ")");
                foreach (var a in l)
                    PageRows.Add(new PageRow
                    {
                        Text = a.Name, Sub = a.Description, ImagePath = a.Icon.Length > 0 ? a.Icon : null,
                        Right = (a.Me ? Loc("LOC_KX_CmpYouYes", "You ✓") : Loc("LOC_KX_CmpYouNo", "You –")) + "     " + (a.Them ? string.Format(Loc("LOC_KX_CmpThemYes", "{0} ✓"), them) : string.Format(Loc("LOC_KX_CmpThemNo", "{0} –"), them))
                    });
            };
            group(string.Format(Loc("LOC_KX_CmpOnlyThem", "ONLY {0} HAS"), them.ToUpperInvariant()), list.Where(a => a.Them && !a.Me).OrderBy(a => a.ThemAt));
            group(Loc("LOC_KX_CmpOnlyYou", "ONLY YOU HAVE"), list.Where(a => a.Me && !a.Them).OrderBy(a => a.MeAt));
            group(Loc("LOC_KX_CmpBoth", "YOU BOTH HAVE"), list.Where(a => a.Me && a.Them));
            group(Loc("LOC_KX_CmpNeither", "NEITHER OF YOU YET"), list.Where(a => !a.Me && !a.Them));
            SetPageStatus("");
            NotifyPage();
            NotifyPageLists();
            LoadPageImages();
            FocusPageSoon(null);
        }
    }
}

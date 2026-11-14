using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace KippixOverlay
{
    /// <summary>
    /// Public hook for other add-ons (Kippix Helper). Everything here uses plain types only, so callers
    /// can reach it by reflection without referencing this DLL, and keep working if the overlay isn't installed.
    /// Bump <see cref="Version"/> when anything here changes in a way callers need to know about.
    /// </summary>
    public static class OverlayBridge
    {
        public const int Version = 5;

        internal static OverlayPlugin Plugin;

        /// <summary>The overlay is running in this Playnite (built into Kippix Helper).</summary>
        public static bool IsRunning { get { return Plugin != null && Plugin.Started; } }

        /// <summary>Raised (on any thread) when Discord state changes: voice members, mute, messages, connection.</summary>
        public static event Action Changed;

        internal static void RaiseChanged()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); } catch (Exception) { }
        }

        // ---------------- Discord ----------------

        public static bool DiscordRunning { get { return Plugin != null && Plugin.DiscordRunning; } }

        /// <summary>Signed in through the overlay's Discord connection.</summary>
        public static bool DiscordReady { get { return Plugin != null && Plugin.Discord != null && Plugin.Discord.Ready; } }

        /// <summary>A Discord app (Client ID and Secret) has been set up in the overlay's config.</summary>
        public static bool DiscordConfigured { get { return Plugin != null && Plugin.Discord != null && Plugin.Discord.Rpc.Configured; } }

        public static string DiscordUserName
        {
            get { return DiscordReady ? Plugin.Discord.Rpc.UserName : null; }
        }

        public static bool Muted { get { return Plugin != null && Plugin.MicMuted; } }
        public static bool Deafened { get { return Plugin != null && Plugin.Deafened; } }

        public static bool InCall { get { return DiscordReady && Plugin.Discord.Voice != null; } }

        public static string CallName
        {
            get
            {
                if (!InCall) return null;
                var v = Plugin.Discord.Voice;
                return string.IsNullOrEmpty(v.GuildName) ? v.Name : v.Name + "  ·  " + v.GuildName;
            }
        }

        /// <summary>Each member: Name, Avatar (url), Speaking, Muted, Deafened.</summary>
        public static List<Dictionary<string, object>> VoiceMembers()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                if (!InCall) return list;
                foreach (var m in Plugin.Discord.MembersOf(Plugin.Discord.Voice))
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "Id", m.UserId },
                        { "Name", m.Name },
                        { "Avatar", m.Avatar },
                        { "Speaking", m.Speaking },
                        { "Muted", m.Muted },
                        { "Deafened", m.Deafened }
                    });
                }
            }
            catch (Exception) { }
            return list;
        }

        public static int UnreadCount
        {
            get
            {
                try { return DiscordReady ? Plugin.Discord.UnreadCount : 0; }
                catch (Exception) { return 0; }
            }
        }

        public static void ToggleMute() { if (Plugin != null) Plugin.ToggleMute(); }
        public static void ToggleDeafen() { if (Plugin != null) Plugin.ToggleDeafen(); }
        public static void LeaveCall() { if (Plugin != null) Plugin.LeaveCall(); }
        public static void StartDiscord() { if (Plugin != null) Plugin.StartDiscord(); }

        /// <summary>First-time Discord login (shows Discord so you can press Authorize).</summary>
        public static void ConnectDiscord() { if (Plugin != null) Plugin.ConnectDiscord(); }

        // ---------------- Discord: chats, servers, joining calls (bridge v2) ----------------
        // These talk to Discord and can take a moment: call them off the UI thread.

        /// <summary>Recent chats the overlay has seen: ChannelId, Title, LastBody, IconUrl, Time, Unread.</summary>
        public static List<Dictionary<string, object>> Conversations()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                if (!DiscordReady) return list;
                foreach (var c in Plugin.Discord.Conversations())
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "ChannelId", c.ChannelId },
                        { "Title", c.Title },
                        { "LastBody", c.LastBody },
                        { "IconUrl", c.IconUrl },
                        { "Time", c.Time },
                        { "Unread", c.Unread }
                    });
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>Latest messages in a channel, newest first: Author, Avatar, Content, Time. Marks the chat as read.</summary>
        public static List<Dictionary<string, object>> Messages(string channelId)
        {
            var list = new List<Dictionary<string, object>>();
            if (!DiscordReady || string.IsNullOrEmpty(channelId)) return list;
            var ch = Plugin.Discord.Channel(channelId);
            Plugin.Discord.MarkRead(channelId);
            foreach (var m in ch.Messages)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "Author", m.Author },
                    { "Avatar", m.Avatar },
                    { "Content", m.Content },
                    { "Time", m.Time }
                });
            }
            RaiseChanged();
            return list;
        }

        /// <summary>Your servers: Id, Name, Icon.</summary>
        public static List<Dictionary<string, object>> Guilds()
        {
            var list = new List<Dictionary<string, object>>();
            if (!DiscordReady) return list;
            foreach (var g in Plugin.Discord.Guilds())
                list.Add(new Dictionary<string, object> { { "Id", g.Id }, { "Name", g.Name }, { "Icon", g.Icon } });
            return list;
        }

        /// <summary>A server's text and voice channels: Id, Name, IsVoice, IsText.</summary>
        public static List<Dictionary<string, object>> Channels(string guildId)
        {
            var list = new List<Dictionary<string, object>>();
            if (!DiscordReady) return list;
            foreach (var c in Plugin.Discord.Channels(guildId))
            {
                if (!c.IsVoice && !c.IsText) continue;
                list.Add(new Dictionary<string, object> { { "Id", c.Id }, { "Name", c.Name }, { "IsVoice", c.IsVoice }, { "IsText", c.IsText } });
            }
            return list;
        }

        /// <summary>Joins a voice channel, or calls a direct message / group chat.</summary>
        public static bool JoinVoice(string channelId)
        {
            try
            {
                if (!DiscordReady) return false;
                Plugin.Discord.Join(channelId);
                RaiseChanged();
                return true;
            }
            catch (Exception) { return false; }
        }

        public static List<string> QuickReplies()
        {
            try { return Plugin != null && Plugin.Config != null ? Plugin.Config.QuickReplies.ToList() : new List<string>(); }
            catch (Exception) { return new List<string>(); }
        }

        /// <summary>
        /// Sends a message the same way the overlay does (Discord doesn't let apps post as you: the chat is opened
        /// in Discord and typed there), then brings the window <paramref name="returnTo"/> back to the front.
        /// Returns true once Discord confirms the message arrived.
        /// </summary>
        public static bool SendMessage(string channelId, string text, long returnTo)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length == 0 || !DiscordReady) return false;
            bool ok = false;
            try
            {
                ok = Plugin.Discord.WaitForOwnMessage(channelId, text, () =>
                {
                    Plugin.Discord.SelectTextChannel(channelId);
                    IntPtr h = DiscordHelper.MainWindow();
                    if (h == IntPtr.Zero) h = DiscordHelper.ShowAndWait(null);
                    if (h == IntPtr.Zero) throw new Exception("Discord's window didn't open");
                    Thread.Sleep(300);
                    Native.ForceForeground(h);
                    Thread.Sleep(350);
                    if (Native.GetForegroundWindow() != h) { Native.ForceForeground(h); Thread.Sleep(300); }
                    Native.TypeText(text);
                    Thread.Sleep(150);
                    Native.TapKey(0x0D);
                }, 6000);
            }
            catch (Exception) { ok = false; }
            finally
            {
                Thread.Sleep(250);
                HideDiscord();
                if (returnTo != 0) Native.ForceForeground(new IntPtr(returnTo));
            }
            RaiseChanged();
            return ok;
        }

        // ---------------- Discord window (bridge v3) ----------------

        /// <summary>
        /// Every chat in Discord's Direct Messages list (read from Discord's window through Windows accessibility):
        /// ChannelId, Name, Group, Unread. Slow: call off the UI thread.
        /// </summary>
        public static List<Dictionary<string, object>> OpenChats()
        {
            var list = new List<Dictionary<string, object>>();
            if (!DiscordRunning) return list;
            bool wasVisible = DiscordHelper.MainWindow() != IntPtr.Zero;
            string info;
            var chats = DiscordHelper.OpenChats(false, out info);
            if (chats.Count == 0)
            {
                chats = DiscordHelper.OpenChats(true, out info);   // may have to switch Discord to its Home view
                if (!wasVisible) HideDiscord();
            }
            foreach (var c in chats)
                list.Add(new Dictionary<string, object> { { "ChannelId", c.ChannelId }, { "Name", c.Name }, { "Group", c.Group }, { "Unread", c.Unread } });
            return list;
        }

        /// <summary>Hides Discord's window so it keeps running in the system tray. Returns true if a window was hidden.</summary>
        public static bool HideDiscord()
        {
            bool hidden = false;
            try
            {
                foreach (IntPtr h in DiscordHelper.Windows())
                {
                    if (!Native.IsWindowVisible(h)) continue;
                    Native.ShowWindow(h, 0); // SW_HIDE: Discord stays in the tray, its tray icon brings it back
                    hidden = true;
                }
            }
            catch (Exception) { }
            return hidden;
        }

        // ---------------- Controller ----------------

        /// <summary>"Full", "Low", "80%", "Wired"... or null when no controller is connected.</summary>
        public static string ControllerBattery
        {
            get
            {
                try { return Plugin != null && Plugin.Poller != null ? Plugin.Poller.BatteryShort() : null; }
                catch (Exception) { return null; }
            }
        }

        /// <summary>0 fine, 1 low, 2 almost empty.</summary>
        public static int ControllerBatteryWarning
        {
            get
            {
                try { return Plugin != null && Plugin.Poller != null ? Plugin.Poller.BatteryWarning() : 0; }
                catch (Exception) { return 0; }
            }
        }

        // ---------------- Achievements ----------------

        /// <summary>The achievement pinned as "next" for a game in the overlay, or null.</summary>
        public static string NextAchievement(Guid gameId)
        {
            try { return Plugin != null ? Plugin.GetNextAchievement(gameId) : null; }
            catch (Exception) { return null; }
        }

        // ---------------- Achievement guides (AI tips) ----------------

        /// <summary>An AI provider is set up in the overlay's settings, so tips can be written.</summary>
        public static bool GuidesReady
        {
            get
            {
                try { return Plugin != null && Plugin.Config != null && Plugin.Config.GuidesReady; }
                catch (Exception) { return false; }
            }
        }

        private static GuideStore Store()
        {
            if (Plugin == null) return null;
            if (Plugin.Guides == null) Plugin.Guides = new GuideStore(System.IO.Path.Combine(Plugin.GetPluginUserDataPath(), "guides"));
            return Plugin.Guides;
        }

        /// <summary>The saved tip for one achievement: { Tip, Missable (bool), When }, or null when there isn't one.</summary>
        public static Dictionary<string, object> Guide(Guid gameId, string achievementName)
        {
            try
            {
                var store = Store();
                var g = store != null ? store.Find(gameId, achievementName) : null;
                if (g == null || string.IsNullOrEmpty(g.Tip)) return null;
                return new Dictionary<string, object> { { "Tip", g.Tip }, { "Missable", g.Missable }, { "When", g.When ?? "" } };
            }
            catch (Exception) { return null; }
        }

        /// <summary>Tips have been written for at least some of this game's achievements.</summary>
        public static bool HasGuides(Guid gameId)
        {
            try { var store = Store(); return store != null && store.Has(gameId); }
            catch (Exception) { return false; }
        }

        public static bool IsWritingGuides(Guid gameId)
        {
            try { return Plugin != null && Plugin.IsDownloadingGuides(gameId); }
            catch (Exception) { return false; }
        }

        private static readonly Dictionary<Guid, string> guideErrors = new Dictionary<Guid, string>();

        /// <summary>Why the last WriteGuides for this game failed, or null.</summary>
        public static string GuideError(Guid gameId)
        {
            lock (guideErrors)
            {
                string e;
                return guideErrors.TryGetValue(gameId, out e) ? e : null;
            }
        }

        /// <summary>
        /// Writes tips (in the background) for this game's achievements that don't have one yet, or all of them with force.
        /// Call from the UI thread. Changed is raised when it finishes; check GuideError afterwards.
        /// </summary>
        public static bool WriteGuides(Guid gameId, bool force)
        {
            try
            {
                if (Plugin == null) return false;
                var game = Playnite.SDK.API.Instance.Database.Games.Get(gameId);
                if (game == null) return false;
                lock (guideErrors) guideErrors.Remove(gameId);
                Store();
                Plugin.DownloadGuidesBackground(game, force, result =>
                {
                    if (!string.IsNullOrEmpty(result)) lock (guideErrors) guideErrors[gameId] = result;
                    RaiseChanged();
                });
                RaiseChanged();
                return true;
            }
            catch (Exception ex)
            {
                lock (guideErrors) guideErrors[gameId] = ex.Message;
                return false;
            }
        }

        /// <summary>Deletes the saved tips for one game (no questions asked). True if there were any.</summary>
        public static bool DeleteGuides(Guid gameId)
        {
            try
            {
                var store = Store();
                bool ok = store != null && store.Delete(gameId);
                if (ok) RaiseChanged();
                return ok;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Opens the overlay on a tab (0 Home, 1 Party, 2 Audio, 3 Settings) while a game is running.</summary>
        public static bool OpenOverlay(int tab)
        {
            try
            {
                if (Plugin == null || Plugin.RunningGame == null) return false;
                Plugin.OpenOverlay(tab);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}

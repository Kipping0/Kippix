using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace KippixOverlay
{
    internal class DiscordConversation
    {
        public string ChannelId;
        public string Title;
        public string LastBody;
        public string IconUrl;
        public DateTime Time;
        public int Unread;
    }

    internal class VoiceMember
    {
        public string UserId;
        public string Name;
        public string Avatar;
        public bool Muted;
        public bool Deafened;
        public bool Speaking;
    }

    internal class VoiceInfo
    {
        public string ChannelId;
        public string Name;
        public string GuildId;
        public string GuildName;
        public readonly List<VoiceMember> Members = new List<VoiceMember>();
    }

    internal class ChatMessage
    {
        public string Id;
        public string AuthorId;
        public string Author;
        public string Avatar;
        public string Content;
        public DateTime? Time;
    }

    internal class ChannelInfo
    {
        public string Id;
        public string Name;
        public int Type;          // 0 text, 1 DM, 2 voice, 3 group DM, 5 news, 13 stage
        public string GuildId;
        public readonly List<ChatMessage> Messages = new List<ChatMessage>();
        public readonly List<VoiceMember> VoiceMembers = new List<VoiceMember>();

        public bool IsDm { get { return Type == 1 || Type == 3; } }
        public bool IsVoice { get { return Type == 2 || Type == 13; } }
        public bool IsText { get { return Type == 0 || Type == 5 || IsDm; } }
    }

    internal class GuildInfo
    {
        public string Id;
        public string Name;
        public string Icon;
    }

    /// <summary>Discord state for the overlay: voice, mute/deafen, conversations. Commands block; call off the UI thread.</summary>
    internal class DiscordService
    {
        public readonly DiscordRpc Rpc;

        /// <summary>Something changed (raised from background threads).</summary>
        public Action Changed;
        public Action<DiscordConversation> Notified;

        public volatile bool Muted;
        public volatile bool Deafened;
        public VoiceInfo Voice;

        private readonly object sync = new object();
        private readonly List<DiscordConversation> conversations = new List<DiscordConversation>();
        private readonly Dictionary<string, string> guildNames = new Dictionary<string, string>();
        private string voiceSubscribed;
        private bool setupDone;

        private string watchChannel;
        private Action<ChatMessage> watchHandler;
        private string confirmChannel, confirmText;
        private volatile ManualResetEvent confirmEvent;

        public DiscordService(string tokenPath)
        {
            Rpc = new DiscordRpc(tokenPath);
            Rpc.EventReceived = OnEvent;
            Rpc.StateChanged = () =>
            {
                if (!Rpc.Ready)
                {
                    setupDone = false;
                    Voice = null;
                    voiceSubscribed = null;
                    watchChannel = null;
                }
                Raise();
            };
        }

        public void Configure(string clientId, string secret)
        {
            clientId = (clientId ?? "").Trim();
            secret = (secret ?? "").Trim();
            if (clientId == (Rpc.ClientId ?? "") && secret == (Rpc.ClientSecret ?? "")) return;
            Rpc.Disconnect();
            Rpc.ClientId = clientId;
            Rpc.ClientSecret = secret;
        }

        public bool Ready { get { return Rpc.Ready && setupDone; } }

        /// <summary>Connects and logs in. Blocks.</summary>
        public bool Connect(bool allowAuthorize)
        {
            if (!Rpc.EnsureReady(allowAuthorize)) return false;
            if (!setupDone)
            {
                setupDone = true;
                Rpc.Subscribe("NOTIFICATION_CREATE", null);
                Rpc.Subscribe("VOICE_SETTINGS_UPDATE", null);
                Rpc.Subscribe("VOICE_CHANNEL_SELECT", null);
                Rpc.Subscribe("VOICE_CONNECTION_STATUS", null);
                RefreshVoiceSettings();
                RefreshVoice();
                Raise();
            }
            return true;
        }

        private void Raise()
        {
            var h = Changed;
            if (h != null) { try { h(); } catch (Exception) { } }
        }

        private static void Background(Action a)
        {
            KippixShared.SafeWork.Queue(_ => { try { a(); } catch (Exception) { } });
        }

        // ================= Events (reader thread: never wait on commands here) =================

        private void OnEvent(string evt, Dictionary<string, object> data)
        {
            switch (evt)
            {
                case "NOTIFICATION_CREATE":
                    AddNotification(data);
                    break;
                case "VOICE_SETTINGS_UPDATE":
                    Muted = DiscordRpc.Bool(data, "mute");
                    Deafened = DiscordRpc.Bool(data, "deaf");
                    break;
                case "VOICE_CHANNEL_SELECT":
                    Background(RefreshVoice);
                    break;
                case "VOICE_STATE_CREATE":
                case "VOICE_STATE_UPDATE":
                    UpsertMember(data);
                    break;
                case "VOICE_STATE_DELETE":
                    RemoveMember(data);
                    break;
                case "SPEAKING_START":
                case "SPEAKING_STOP":
                    SetSpeaking(DiscordRpc.Str(data, "user_id"), evt == "SPEAKING_START");
                    break;
                case "MESSAGE_CREATE":
                    OnMessage(data);
                    break;
            }
            Raise();
        }

        private void AddNotification(Dictionary<string, object> data)
        {
            string channel = DiscordRpc.Str(data, "channel_id");
            if (channel == null) return;
            DiscordConversation c;
            lock (sync)
            {
                c = conversations.FirstOrDefault(x => x.ChannelId == channel);
                if (c == null)
                {
                    c = new DiscordConversation { ChannelId = channel };
                    conversations.Add(c);
                }
                c.Title = DiscordRpc.Str(data, "title") ?? c.Title ?? "Message";
                c.LastBody = DiscordRpc.Str(data, "body") ?? "";
                c.IconUrl = DiscordRpc.Str(data, "icon_url") ?? c.IconUrl;
                c.Time = DateTime.Now;
                if (watchChannel != channel) c.Unread++;
                conversations.Sort((a, b) => b.Time.CompareTo(a.Time));
            }
            var h = Notified;
            if (h != null && watchChannel != channel) { try { h(c); } catch (Exception) { } }
        }

        public List<DiscordConversation> Conversations()
        {
            lock (sync) return conversations.ToList();
        }

        public int UnreadCount
        {
            get { lock (sync) return conversations.Sum(c => c.Unread); }
        }

        public void MarkRead(string channelId)
        {
            lock (sync) foreach (var c in conversations) if (c.ChannelId == channelId) c.Unread = 0;
        }

        // ================= Voice =================

        public void RefreshVoiceSettings()
        {
            try
            {
                var s = Rpc.Command("GET_VOICE_SETTINGS", null);
                if (s != null)
                {
                    Muted = DiscordRpc.Bool(s, "mute");
                    Deafened = DiscordRpc.Bool(s, "deaf");
                }
            }
            catch (Exception) { }
        }

        public void RefreshVoice()
        {
            Dictionary<string, object> ch = null;
            try { ch = Rpc.Command("GET_SELECTED_VOICE_CHANNEL", null); }
            catch (Exception) { }

            string newId = ch != null ? DiscordRpc.Str(ch, "id") : null;
            if (voiceSubscribed != null && voiceSubscribed != newId) SubscribeVoice(voiceSubscribed, false);

            if (newId == null)
            {
                Voice = null;
                voiceSubscribed = null;
                Raise();
                return;
            }

            var v = new VoiceInfo
            {
                ChannelId = newId,
                Name = DiscordRpc.Str(ch, "name"),
                GuildId = DiscordRpc.Str(ch, "guild_id")
            };
            foreach (var vs in DiscordRpc.List(ch, "voice_states")) v.Members.Add(ParseMember(vs));
            if (string.IsNullOrEmpty(v.Name)) v.Name = DiscordRpc.Int(ch, "type") == 1 ? "Private call" : "Group call";
            if (v.GuildId != null) v.GuildName = GuildName(v.GuildId);
            Voice = v;

            if (voiceSubscribed != newId)
            {
                SubscribeVoice(newId, true);
                voiceSubscribed = newId;
            }
            Raise();
        }

        private void SubscribeVoice(string channelId, bool on)
        {
            var args = DiscordRpc.Args("channel_id", channelId);
            foreach (string e in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
            {
                if (on) Rpc.Subscribe(e, args);
                else Rpc.Unsubscribe(e, args);
            }
        }

        public static VoiceMember ParseMember(Dictionary<string, object> vs)
        {
            var user = DiscordRpc.Dict(vs, "user");
            var state = DiscordRpc.Dict(vs, "voice_state");
            var m = new VoiceMember
            {
                UserId = DiscordRpc.Str(user, "id"),
                Name = DiscordRpc.Str(vs, "nick") ?? DiscordRpc.Str(user, "global_name") ?? DiscordRpc.Str(user, "username") ?? "Someone",
                Avatar = AvatarUrl(user)
            };
            m.Muted = DiscordRpc.Bool(vs, "mute") || DiscordRpc.Bool(state, "mute") || DiscordRpc.Bool(state, "self_mute");
            m.Deafened = DiscordRpc.Bool(state, "deaf") || DiscordRpc.Bool(state, "self_deaf");
            return m;
        }

        /// <summary>A copy of a call's members, safe to go through on any thread (Discord's events change the list on the reader thread).</summary>
        public List<VoiceMember> MembersOf(VoiceInfo v)
        {
            if (v == null) return new List<VoiceMember>();
            lock (sync) return v.Members.ToList();
        }

        private void UpsertMember(Dictionary<string, object> data)
        {
            var v = Voice;
            if (v == null) return;
            var m = ParseMember(data);
            lock (sync)
            {
                var old = v.Members.FirstOrDefault(x => x.UserId == m.UserId);
                if (old != null) { m.Speaking = old.Speaking; v.Members[v.Members.IndexOf(old)] = m; }
                else v.Members.Add(m);
            }
        }

        private void RemoveMember(Dictionary<string, object> data)
        {
            var v = Voice;
            if (v == null) return;
            string id = DiscordRpc.Str(DiscordRpc.Dict(data, "user"), "id");
            lock (sync) v.Members.RemoveAll(x => x.UserId == id);
        }

        private void SetSpeaking(string userId, bool speaking)
        {
            var v = Voice;
            if (v == null || userId == null) return;
            lock (sync) foreach (var m in v.Members) if (m.UserId == userId) m.Speaking = speaking;
        }

        public void SetMute(bool mute)
        {
            Muted = mute;
            Rpc.Command("SET_VOICE_SETTINGS", DiscordRpc.Args("mute", mute));
        }

        public void SetDeaf(bool deaf)
        {
            Deafened = deaf;
            Rpc.Command("SET_VOICE_SETTINGS", DiscordRpc.Args("deaf", deaf));
        }

        public void Leave()
        {
            Rpc.Command("SELECT_VOICE_CHANNEL", DiscordRpc.Args("channel_id", null));
            Voice = null;
        }

        public void Join(string channelId)
        {
            Rpc.Command("SELECT_VOICE_CHANNEL", DiscordRpc.Args("channel_id", channelId, "force", true, "timeout", 10), null, 15000);
            RefreshVoice();
        }

        // ================= Servers & channels =================

        public List<GuildInfo> Guilds()
        {
            var list = new List<GuildInfo>();
            var res = Rpc.Command("GET_GUILDS", null);
            foreach (var g in DiscordRpc.List(res, "guilds"))
            {
                var gi = new GuildInfo { Id = DiscordRpc.Str(g, "id"), Name = DiscordRpc.Str(g, "name"), Icon = DiscordRpc.Str(g, "icon_url") };
                list.Add(gi);
                lock (sync) guildNames[gi.Id ?? ""] = gi.Name;
            }
            return list;
        }

        private string GuildName(string id)
        {
            lock (sync)
            {
                string n;
                if (guildNames.TryGetValue(id, out n)) return n;
            }
            try { Guilds(); } catch (Exception) { }
            lock (sync)
            {
                string n;
                return guildNames.TryGetValue(id, out n) ? n : null;
            }
        }

        public List<ChannelInfo> Channels(string guildId)
        {
            var list = new List<ChannelInfo>();
            var res = Rpc.Command("GET_CHANNELS", DiscordRpc.Args("guild_id", guildId));
            foreach (var c in DiscordRpc.List(res, "channels"))
                list.Add(new ChannelInfo { Id = DiscordRpc.Str(c, "id"), Name = DiscordRpc.Str(c, "name"), Type = DiscordRpc.Int(c, "type"), GuildId = guildId });
            return list;
        }

        public ChannelInfo Channel(string channelId)
        {
            var c = Rpc.Command("GET_CHANNEL", DiscordRpc.Args("channel_id", channelId));
            var info = new ChannelInfo
            {
                Id = channelId,
                Name = DiscordRpc.Str(c, "name"),
                Type = DiscordRpc.Int(c, "type"),
                GuildId = DiscordRpc.Str(c, "guild_id")
            };
            foreach (var m in DiscordRpc.List(c, "messages")) info.Messages.Add(ParseMessage(m));
            info.Messages.Reverse(); // newest first
            foreach (var vs in DiscordRpc.List(c, "voice_states")) info.VoiceMembers.Add(ParseMember(vs));
            return info;
        }

        public static ChatMessage ParseMessage(Dictionary<string, object> m)
        {
            var author = DiscordRpc.Dict(m, "author");
            string content = DiscordRpc.Str(m, "content") ?? "";
            if (content.Length == 0)
            {
                if (DiscordRpc.List(m, "attachments").Count > 0) content = "[attachment]";
                else if (DiscordRpc.List(m, "embeds").Count > 0) content = "[embed]";
                else if (DiscordRpc.List(m, "stickers").Count > 0 || DiscordRpc.List(m, "sticker_items").Count > 0) content = "[sticker]";
            }
            DateTimeOffset t;
            DateTime? time = null;
            if (DateTimeOffset.TryParse(DiscordRpc.Str(m, "timestamp") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t))
                time = t.LocalDateTime;
            return new ChatMessage
            {
                Id = DiscordRpc.Str(m, "id"),
                AuthorId = DiscordRpc.Str(author, "id"),
                Author = DiscordRpc.Str(m, "nick") ?? DiscordRpc.Str(author, "global_name") ?? DiscordRpc.Str(author, "username") ?? "Someone",
                Avatar = AvatarUrl(author),
                Content = content,
                Time = time
            };
        }

        public static string AvatarUrl(Dictionary<string, object> user)
        {
            string id = DiscordRpc.Str(user, "id");
            string av = DiscordRpc.Str(user, "avatar");
            if (id == null || string.IsNullOrEmpty(av)) return null;
            if (av.StartsWith("http")) return av;
            return "https://cdn.discordapp.com/avatars/" + id + "/" + av + ".png?size=64";
        }

        // ================= Live messages =================

        public void Watch(string channelId, Action<ChatMessage> onMessage)
        {
            if (watchChannel != null && watchChannel != channelId) StopWatching();
            watchHandler = onMessage;
            if (watchChannel == channelId) return;
            watchChannel = channelId;
            MarkRead(channelId);
            Rpc.Subscribe("MESSAGE_CREATE", DiscordRpc.Args("channel_id", channelId));
        }

        public void StopWatching()
        {
            string ch = watchChannel;
            watchChannel = null;
            watchHandler = null;
            if (ch != null && Rpc.Ready) Rpc.Unsubscribe("MESSAGE_CREATE", DiscordRpc.Args("channel_id", ch));
        }

        private void OnMessage(Dictionary<string, object> data)
        {
            string channel = DiscordRpc.Str(data, "channel_id");
            var msgDict = DiscordRpc.Dict(data, "message");
            if (msgDict == null) return;
            var msg = ParseMessage(msgDict);

            var confirm = confirmEvent;   // (the sender may clear it at any moment)
            if (confirm != null && channel == confirmChannel && msg.AuthorId == Rpc.UserId &&
                (msg.Content ?? "").Trim() == (confirmText ?? "").Trim())
            {
                try { confirm.Set(); } catch (ObjectDisposedException) { }
            }

            var h = watchHandler;
            if (h != null && channel == watchChannel) h(msg);
        }

        /// <summary>Waits until a message we typed shows up in the channel. Blocks.</summary>
        public bool WaitForOwnMessage(string channelId, string text, Action send, int timeoutMs)
        {
            bool subscribed = watchChannel != channelId;
            if (subscribed) Rpc.Subscribe("MESSAGE_CREATE", DiscordRpc.Args("channel_id", channelId));
            confirmChannel = channelId;
            confirmText = text;
            var ev = new ManualResetEvent(false);
            confirmEvent = ev;
            try
            {
                send();
                return ev.WaitOne(timeoutMs);
            }
            finally
            {
                confirmEvent = null;
                ev.Dispose();
                if (subscribed && watchChannel != channelId) Rpc.Unsubscribe("MESSAGE_CREATE", DiscordRpc.Args("channel_id", channelId));
            }
        }

        public void SelectTextChannel(string channelId)
        {
            Rpc.Command("SELECT_TEXT_CHANNEL", DiscordRpc.Args("channel_id", channelId, "timeout", 5), null, 10000);
        }
    }
}

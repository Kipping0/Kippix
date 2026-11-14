using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace KippixHelper
{
    /// <summary>
    /// The Discord panel. With KippixOverlay installed (and its Discord app set up) it works like the
    /// overlay's Party tab: your call, recent chats, reading and replying, quick replies, calling a chat,
    /// and joining any server voice channel. Without the overlay it can only start and open Discord.
    /// Pages: main -> chats -> chat, main -> servers -> channels -> chat.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<VoiceItem> VoiceMembers { get; private set; }
        public ObservableCollection<DiscordRow> DiscordList { get; private set; }       // chats, servers or channels
        public ObservableCollection<MessageItem> DiscordMessages { get; private set; }
        public ObservableCollection<DiscordRow> QuickReplies { get; private set; }

        public ICommand DiscordStartCommand { get; private set; }
        public ICommand DiscordOpenCommand { get; private set; }
        public ICommand DiscordConnectCommand { get; private set; }
        public ICommand DiscordMuteCommand { get; private set; }
        public ICommand DiscordDeafenCommand { get; private set; }
        public ICommand DiscordLeaveCommand { get; private set; }
        public ICommand DiscordChatsCommand { get; private set; }
        public ICommand DiscordServersCommand { get; private set; }
        public ICommand DiscordReplyCommand { get; private set; }
        public ICommand DiscordCallCommand { get; private set; }
        public ICommand DiscordRefreshChatCommand { get; private set; }

        private DateTime discordStarting = DateTime.MinValue;
        private bool dRunning, dReady, dInCall, dMuted, dDeafened, dConfigured;
        private int dUnread;
        private string dStatus = "", dCall = "", membersSignature = "";

        // ---- page state ----
        private string discordPage = "main";
        private string listKind = "";           // "chats", "servers" or "channels"
        private string chatId, chatTitle, chatCameFrom = "chats";
        private bool chatCanCall;
        private string guildId, guildName;
        private bool discordBusy;
        private string discordNote = "";
        private int discordRequest;              // ignores late answers after you've moved on

        public bool IsDiscordMainPage { get { return discordPage == "main"; } }
        public bool IsDiscordListPage { get { return discordPage == "list"; } }
        public bool IsDiscordChatPage { get { return discordPage == "chat"; } }
        public bool DiscordBusy { get { return discordBusy; } }
        public string DiscordNote { get { return discordNote; } }
        public bool HasDiscordNote { get { return !string.IsNullOrEmpty(discordNote); } }
        public bool ChatCanCall { get { return chatCanCall && dReady; } }
        public bool HasQuickReplies { get { return QuickReplies.Count > 0; } }

        public string DiscordTitle
        {
            get
            {
                if (discordPage == "chat") return chatTitle ?? "";
                if (discordPage == "list")
                {
                    if (listKind == "chats") return Loc("LOC_KX_Chats", "Chats");
                    if (listKind == "servers") return Loc("LOC_KX_Servers", "Servers");
                    if (listKind == "channels") return guildName ?? "";
                }
                return "Discord";
            }
        }

        public string DiscordStatusText { get { return dStatus; } }
        public string CallName { get { return dCall; } }
        public int DiscordUnreadCount { get { return dUnread; } }
        public bool HasDiscordUnread { get { return dUnread > 0; } }
        public bool IsDiscordRunning { get { return dRunning; } }
        public Brush DiscordDotBrush { get { return dInCall ? GreenBrush : WhiteBrush; } }
        public bool ShowDiscordStart { get { return !dRunning; } }
        public bool ShowDiscordControls { get { return dRunning && dReady; } }
        public bool IsInCall { get { return dInCall; } }
        public bool ShowDiscordConnect { get { return overlay.Available && dRunning && dConfigured && !dReady; } }
        public string ChatsLabel
        {
            get { return dUnread > 0 ? string.Format(Loc("LOC_KX_ChatsUnread", "Chats  ({0} unread)"), dUnread) : Loc("LOC_KX_Chats", "Chats"); }
        }
        public string MuteLabel { get { return dMuted ? Loc("LOC_BBH_Unmute", "Unmute microphone") : Loc("LOC_BBH_Mute", "Mute microphone"); } }
        public string DeafenLabel { get { return dDeafened ? Loc("LOC_BBH_Undeafen", "Undeafen") : Loc("LOC_BBH_Deafen", "Deafen"); } }

        private void InitDiscordCommands()
        {
            DiscordStartCommand = new RelayCommand(_ =>
            {
                if (overlay.Available) overlay.Call("StartDiscord");
                else DiscordApp.StartInBackground();
                discordStarting = DateTime.Now;
                RefreshDiscord();
                KeepDiscordInTray();
            });
            DiscordOpenCommand = new RelayCommand(_ => { ClosePanels(); DiscordApp.Open(); });
            DiscordConnectCommand = new RelayCommand(_ => { ClosePanels(); overlay.Call("ConnectDiscord"); });
            DiscordMuteCommand = new RelayCommand(_ => { overlay.Call("ToggleMute"); RefreshDiscordSoon(); });
            DiscordDeafenCommand = new RelayCommand(_ => { overlay.Call("ToggleDeafen"); RefreshDiscordSoon(); });
            DiscordLeaveCommand = new RelayCommand(_ => { overlay.Call("LeaveCall"); RefreshDiscordSoon(); });
            DiscordChatsCommand = new RelayCommand(_ => ShowChats());
            DiscordServersCommand = new RelayCommand(_ => ShowServers());
            DiscordReplyCommand = new RelayCommand(_ => Reply(null));
            DiscordCallCommand = new RelayCommand(_ => JoinVoice(chatId, chatTitle));
            DiscordRefreshChatCommand = new RelayCommand(_ => LoadMessages());
        }

        // =====================================================================
        // Status
        // =====================================================================

        private void RefreshDiscordSoon()
        {
            KippixShared.SafeWork.Queue(_ => { Thread.Sleep(700); OnUi(RefreshDiscord); });
        }

        private void RefreshDiscord()
        {
            if (overlay.Available)
            {
                dRunning = overlay.Get("DiscordRunning", false);
                dReady = overlay.Get("DiscordReady", false);
                dConfigured = overlay.Get("DiscordConfigured", false);
                dInCall = overlay.Get("InCall", false);
                dMuted = overlay.Get("Muted", false);
                dDeafened = overlay.Get("Deafened", false);
                dUnread = overlay.Get("UnreadCount", 0);
                dCall = overlay.Get<string>("CallName", null) ?? "";
            }
            else
            {
                dRunning = DiscordApp.IsRunning();
                dReady = dConfigured = dInCall = dMuted = dDeafened = false;
                dUnread = 0;
                dCall = "";
            }

            if (dRunning) discordStarting = DateTime.MinValue;
            if (!dRunning && (DateTime.Now - discordStarting).TotalSeconds < 25) dStatus = Loc("LOC_BBH_DiscordStarting", "Starting Discord...");
            else if (!dRunning) dStatus = Loc("LOC_BBH_DiscordOff", "Discord isn't running.");
            else if (dReady)
            {
                string user = overlay.Get<string>("DiscordUserName", null);
                dStatus = (dInCall ? Loc("LOC_BBH_InCall", "In a call") : Loc("LOC_BBH_Online", "Online"))
                          + (string.IsNullOrEmpty(user) ? "" : "  ·  " + user)
                          + (dMuted ? "  ·  " + Loc("LOC_BBH_Muted", "muted") : "")
                          + (dDeafened ? "  ·  " + Loc("LOC_BBH_Deafened", "deafened") : "");
            }
            else if (!overlay.Available) dStatus = OldOverlayInstalled ? Loc("LOC_KX_GuideOldOverlay", "The overlay is built into Kippix Helper now: uninstall the separate KippixOverlay add-on and restart Playnite.")
                                                                   : Loc("LOC_BBH_DiscordNoOverlay", "Discord is running. Turn on the in-game overlay to see your chats and calls here.");
            else if (!dConfigured) dStatus = Loc("LOC_BBH_DiscordNotSetUp", "Discord is running. Add your Discord app in Kippix Helper's settings (Overlay tab) to use chats and calls here.");
            else dStatus = Loc("LOC_BBH_DiscordNotConnected", "Discord is running but not connected yet. Choose Connect Discord.");

            var members = dInCall ? overlay.VoiceMembers() : new List<Dictionary<string, object>>();
            string sig = string.Join("|", members.Select(m => Str(m, "Name") + Bool(m, "Speaking") + Bool(m, "Muted") + Bool(m, "Deafened")));
            if (sig != membersSignature)
            {
                membersSignature = sig;
                VoiceMembers.Clear();
                foreach (var m in members)
                {
                    bool deaf = Bool(m, "Deafened"), mute = Bool(m, "Muted");
                    VoiceMembers.Add(new VoiceItem
                    {
                        Name = Str(m, "Name"),
                        AvatarUrl = Str(m, "Avatar"),
                        Speaking = Bool(m, "Speaking"),
                        StateText = deaf ? Loc("LOC_BBH_Deafened", "deafened") : mute ? Loc("LOC_BBH_Muted", "muted") : ""
                    });
                }
            }

            foreach (string n in new[] { "DiscordStatusText", "CallName", "DiscordUnreadCount", "HasDiscordUnread", "IsDiscordRunning",
                                         "DiscordDotBrush", "ShowDiscordStart", "ShowDiscordControls", "IsInCall",
                                         "ShowDiscordConnect", "MuteLabel", "DeafenLabel", "ChatsLabel", "ChatCanCall" })
                Notify(n);
            UpdateShortcutStatus();
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            return d.TryGetValue(k, out o) && o != null ? o.ToString() : "";
        }

        private static bool Bool(Dictionary<string, object> d, string k)
        {
            object o;
            return d.TryGetValue(k, out o) && o is bool && (bool)o;
        }

        // =====================================================================
        // Pages
        // =====================================================================

        private void SetDiscordPage(string page)
        {
            discordPage = page;
            discordRequest++;
            if (discordBusy) SetBusy(false);   // what was loading is dropped (its answer is ignored), so "Loading" goes too
            SetNote("");
            foreach (string n in new[] { "IsDiscordMainPage", "IsDiscordListPage", "IsDiscordChatPage", "DiscordTitle", "ChatCanCall" })
                Notify(n);
            FocusPanelSoon();
        }

        private void LeaveDiscordPages()
        {
            discordPage = "main";
            discordRequest++;
            if (discordBusy) SetBusy(false);
            DiscordList.Clear();
            DiscordMessages.Clear();
        }

        /// <summary>B inside the Discord panel. False when already on the first page (so the panel closes).</summary>
        private bool DiscordBack()
        {
            if (discordPage == "chat")
            {
                if (chatCameFrom == "channels") ShowChannels(guildId, guildName);
                else ShowChats();
                return true;
            }
            if (discordPage == "list")
            {
                if (listKind == "channels") ShowServers();
                else SetDiscordPage("main");
                return true;
            }
            return false;
        }

        private void SetBusy(bool busy)
        {
            discordBusy = busy;
            Notify("DiscordBusy");
        }

        private void SetNote(string note)
        {
            discordNote = note ?? "";
            Notify("DiscordNote");
            Notify("HasDiscordNote");
        }

        private bool RequireConnected()
        {
            if (dReady) return true;
            SetNote(dStatus);
            return false;
        }

        /// <summary>Runs Discord work off the UI thread, then applies the result if you're still on that page.</summary>
        private void DiscordWork<T>(Func<T> work, Action<T> done)
        {
            int request = ++discordRequest;
            SetBusy(true);
            KippixShared.SafeWork.Queue(_ =>
            {
                T result = default(T);
                Exception error = null;
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                OnUi(() =>
                {
                    if (request != discordRequest) return;
                    SetBusy(false);
                    if (error != null)
                    {
                        logger.Error(error, "Kippix: Discord request failed");
                        SetNote(Loc("LOC_BBH_DiscordFailed", "Discord didn't answer. Try again in a moment."));
                        return;
                    }
                    done(result);
                    FocusPanelSoon();
                });
            });
        }

        // ---- Chats ----

        private void ShowChats()
        {
            listKind = "chats";
            DiscordList.Clear();
            SetDiscordPage("list");
            if (!RequireConnected()) return;
            DiscordWork(() => overlay.Call("OpenChats") as System.Collections.IEnumerable, open =>
            {
                AfterDiscordUi();
                DiscordList.Clear();

                // Last message and unread count for chats that have had messages while Playnite was open.
                var recent = new Dictionary<string, Dictionary<string, object>>();
                var convs = overlay.Call("Conversations") as System.Collections.IEnumerable;
                if (convs != null)
                    foreach (object o in convs)
                    {
                        var c = o as Dictionary<string, object>;
                        if (c != null && !recent.ContainsKey(Str(c, "ChannelId"))) recent[Str(c, "ChannelId")] = c;
                    }

                var shown = new HashSet<string>();
                if (open != null)
                {
                    foreach (object o in open)
                    {
                        var c = o as Dictionary<string, object>;
                        if (c == null) continue;
                        string id = Str(c, "ChannelId"), name = Str(c, "Name");
                        if (!shown.Add(id)) continue;
                        Dictionary<string, object> last;
                        recent.TryGetValue(id, out last);
                        int unread = UnreadOf(last);
                        DiscordList.Add(new DiscordRow
                        {
                            Title = name,
                            Subtitle = last != null && Str(last, "LastBody").Length > 0 ? Str(last, "LastBody")
                                     : Bool(c, "Group") ? Loc("LOC_KX_GroupChat", "Group chat") : Loc("LOC_KX_DirectMessage", "Direct message"),
                            IconUrl = last != null ? Str(last, "IconUrl") : null,
                            Badge = unread > 0 ? unread.ToString() : Bool(c, "Unread") ? Loc("LOC_KX_New", "new") : null,
                            Command = new RelayCommand(_ => OpenChat(id, name, true, "chats"))
                        });
                    }
                }
                // Anything that messaged you but isn't in the list (for example a server channel mention).
                foreach (var kv in recent)
                {
                    if (!shown.Add(kv.Key)) continue;
                    string id = kv.Key, title = Str(kv.Value, "Title");
                    int unread = UnreadOf(kv.Value);
                    DiscordList.Add(new DiscordRow
                    {
                        Title = title,
                        Subtitle = Str(kv.Value, "LastBody"),
                        IconUrl = Str(kv.Value, "IconUrl"),
                        Badge = unread > 0 ? unread.ToString() : null,
                        Command = new RelayCommand(_ => OpenChat(id, title, true, "chats"))
                    });
                }
                if (DiscordList.Count == 0)
                    SetNote(Loc("LOC_BBH_NoChats", "Couldn't read your chats from Discord. Make sure Discord is running and signed in, then try again."));
            });
        }

        private static int UnreadOf(Dictionary<string, object> c)
        {
            object un;
            return c != null && c.TryGetValue("Unread", out un) && un is int ? (int)un : 0;
        }

        /// <summary>
        /// Discord sometimes shows its window (or grabs the foreground) when it starts or when the overlay talks to it.
        /// For the next while, tuck it back into the tray and hand the controller back to Playnite.
        /// </summary>
        private void KeepDiscordInTray()
        {
            IntPtr me = MainHwnd();
            KippixShared.SafeWork.Queue(_ =>
            {
                for (int i = 0; i < 60; i++)        // 30 seconds
                {
                    Thread.Sleep(500);
                    DiscordApp.HideToTray();
                    ReclaimFocus(me);
                }
            });
        }

        /// <summary>If Discord took the foreground away from Playnite, take it back (safe on any thread).</summary>
        private static void ReclaimFocus(IntPtr me)
        {
            try
            {
                if (me == IntPtr.Zero || DiscordApp.IsForeground(me) || !DiscordApp.DiscordHasForeground()) return;
                DiscordApp.ForceForeground(me);
            }
            catch (Exception) { }
        }

        private IntPtr MainHwnd()
        {
            try
            {
                var w = System.Windows.Application.Current != null ? System.Windows.Application.Current.MainWindow : null;
                return w != null ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        /// <summary>After asking the overlay to read Discord's window: hide it again and get focus back.</summary>
        private void AfterDiscordUi()
        {
            IntPtr me = MainHwnd();
            KippixShared.SafeWork.Queue(_ =>
            {
                for (int i = 0; i < 6; i++)
                {
                    ReclaimFocus(me);
                    Thread.Sleep(250);
                }
            });
        }

        // ---- Servers and channels ----

        private void ShowServers()
        {
            listKind = "servers";
            DiscordList.Clear();
            SetDiscordPage("list");
            if (!RequireConnected()) return;
            DiscordWork(() => overlay.Call("Guilds") as System.Collections.IEnumerable, list =>
            {
                DiscordList.Clear();
                if (list != null)
                {
                    foreach (object o in list)
                    {
                        var g = o as Dictionary<string, object>;
                        if (g == null) continue;
                        string id = Str(g, "Id"), name = Str(g, "Name");
                        DiscordList.Add(new DiscordRow
                        {
                            Title = name,
                            IconUrl = Str(g, "Icon"),
                            Command = new RelayCommand(_ => ShowChannels(id, name))
                        });
                    }
                }
                if (DiscordList.Count == 0) SetNote(Loc("LOC_BBH_NoServers", "No servers found."));
            });
        }

        private void ShowChannels(string id, string name)
        {
            guildId = id;
            guildName = name;
            listKind = "channels";
            DiscordList.Clear();
            SetDiscordPage("list");
            if (!RequireConnected()) return;
            DiscordWork(() => overlay.Call("Channels", id) as System.Collections.IEnumerable, list =>
            {
                DiscordList.Clear();
                var voice = new List<DiscordRow>();
                var text = new List<DiscordRow>();
                if (list != null)
                {
                    foreach (object o in list)
                    {
                        var c = o as Dictionary<string, object>;
                        if (c == null) continue;
                        string cid = Str(c, "Id"), cname = Str(c, "Name");
                        if (Bool(c, "IsVoice"))
                        {
                            voice.Add(new DiscordRow
                            {
                                Title = cname,
                                Subtitle = Loc("LOC_KX_JoinVoice", "Voice channel"),
                                IsVoice = true,
                                Command = new RelayCommand(_ => JoinVoice(cid, cname))
                            });
                        }
                        else
                        {
                            text.Add(new DiscordRow
                            {
                                Title = "# " + cname,
                                Command = new RelayCommand(_ => OpenChat(cid, "# " + cname, false, "channels"))
                            });
                        }
                    }
                }
                foreach (var r in voice) DiscordList.Add(r);
                foreach (var r in text) DiscordList.Add(r);
                if (DiscordList.Count == 0) SetNote(Loc("LOC_BBH_NoChannels", "No channels you can open here."));
            });
        }

        private void JoinVoice(string channelId, string name)
        {
            if (string.IsNullOrEmpty(channelId) || !RequireConnected()) return;
            SetNote(string.Format(Loc("LOC_BBH_Joining", "Joining {0}..."), name));
            DiscordWork(() => { object r = overlay.Call("JoinVoice", channelId); return r is bool && (bool)r; }, ok =>
            {
                AfterDiscordUi();
                if (!ok) { SetNote(Loc("LOC_BBH_JoinFailed", "Couldn't join that call.")); return; }
                RefreshDiscordSoon();
                SetDiscordPage("main");
            });
        }

        // ---- A chat ----

        private void OpenChat(string id, string title, bool canCall, string cameFrom)
        {
            chatId = id;
            chatTitle = title;
            chatCanCall = canCall;
            chatCameFrom = cameFrom;
            DiscordMessages.Clear();
            BuildQuickReplies();
            SetDiscordPage("chat");
            LoadMessages();
        }

        private void LoadMessages()
        {
            if (!RequireConnected() || string.IsNullOrEmpty(chatId)) return;
            string id = chatId;
            DiscordWork(() => overlay.Call("Messages", id) as System.Collections.IEnumerable, list =>
            {
                DiscordMessages.Clear();
                if (list != null)
                {
                    foreach (object o in list)
                    {
                        var m = o as Dictionary<string, object>;
                        if (m == null) continue;
                        object t;
                        string when = m.TryGetValue("Time", out t) && t is DateTime ? FormatWhen((DateTime)t) : "";
                        DiscordMessages.Add(new MessageItem
                        {
                            Author = Str(m, "Author"),
                            Content = Str(m, "Content"),
                            AvatarUrl = Str(m, "Avatar"),
                            TimeText = when
                        });
                    }
                }
                if (DiscordMessages.Count == 0) SetNote(Loc("LOC_BBH_NoMessages", "No messages to show."));
                RefreshDiscord();
            });
        }

        private string FormatWhen(DateTime t)
        {
            string time = settings.Clock24h ? t.ToString("HH:mm") : t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
            if (t.Date == DateTime.Today) return time;
            if (t.Date == DateTime.Today.AddDays(-1)) return Loc("LOC_KX_Yesterday", "Yesterday") + " " + time;
            return t.ToString("d MMM") + " " + time;
        }

        private void BuildQuickReplies()
        {
            QuickReplies.Clear();
            var replies = overlay.Call("QuickReplies") as System.Collections.IEnumerable;
            if (replies != null)
            {
                foreach (object o in replies)
                {
                    string text = o as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    QuickReplies.Add(new DiscordRow { Title = text, Command = new RelayCommand(_ => Send(text)) });
                }
            }
            Notify("HasQuickReplies");
        }

        private void Reply(string preset)
        {
            if (!RequireConnected() || string.IsNullOrEmpty(chatId)) return;
            OpenKeyboard(string.Format(Loc("LOC_BBH_WriteTo", "Message {0}"), chatTitle), preset ?? "", text =>
            {
                if (string.IsNullOrWhiteSpace(text)) { FocusPanelSoon(); return; }
                Send(text);
            }, null, FocusPanelSoon, 1000);
        }

        /// <summary>Discord doesn't let apps post as you, so the overlay types it into Discord and then brings Playnite back.</summary>
        private void Send(string text)
        {
            if (!RequireConnected() || string.IsNullOrEmpty(chatId)) return;
            long hwnd = 0;
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                if (w != null) hwnd = new WindowInteropHelper(w).Handle.ToInt64();
            }
            catch (Exception) { }
            string id = chatId;
            SetNote(Loc("LOC_BBH_Sending", "Sending..."));
            DiscordWork(() => { object r = overlay.Call("SendMessage", id, text, hwnd); return r is bool && (bool)r; }, ok =>
            {
                AfterDiscordUi();
                SetNote(ok ? Loc("LOC_BBH_Sent", "Message sent.") : Loc("LOC_BBH_NotSent", "Couldn't confirm it was sent. Check Discord to be sure."));
                LoadMessages();
            });
        }
    }
}

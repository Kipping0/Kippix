using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KippixOverlay
{
    internal partial class OverlayWindow
    {
        private bool discordConnecting;
        private DateTime discordConnectTried = DateTime.MinValue;

        private DiscordService Discord
        {
            get { return plugin.Discord; }
        }

        // ================= Party tab =================

        private MenuEntry MuteRow()
        {
            return new MenuEntry
            {
                GlyphFunc = () => plugin.MicMuted ? "\uEC54" : "\uE720",
                TitleFunc = () => plugin.MicMuted ? "Unmute microphone" : "Mute microphone",
                Activate = () => plugin.ToggleMute()
            };
        }

        private MenuEntry DeafenRow()
        {
            return new MenuEntry
            {
                Glyph = "\uE7F6",
                TitleFunc = () => plugin.Deafened ? "Undeafen" : "Deafen",
                Sub = () => plugin.Deafened ? "You can't hear anyone" : null,
                Activate = () => plugin.ToggleDeafen()
            };
        }

        private List<MenuEntry> PartyEntries()
        {
            var l = new List<MenuEntry>();
            l.Add(new MenuEntry
            {
                Glyph = "\uE7E8",
                Title = "Start Discord",
                Sub = () => plugin.DiscordStatus(),
                Dot = () => plugin.DiscordRunning,
                Activate = () => plugin.StartDiscord()
            });
            l.Add(MuteRow());
            l.Add(Divider());
            l.Add(new MenuEntry
            {
                Glyph = "\uE8BD",
                Title = "Chats",
                Sub = () => "Messages, replies and quick replies",
                Badge = () => Discord.UnreadCount,
                Chevron = true,
                Activate = OpenChats
            });
            l.Add(new MenuEntry
            {
                Glyph = "\uE717",
                Title = "Voice chat",
                Sub = () =>
                {
                    var v = Discord.Voice;
                    if (v == null) return "See who's in, and join";
                    int n = Discord.MembersOf(v).Count;
                    return "In " + v.Name + "  \u00B7  " + n + (n == 1 ? " person" : " people");
                },
                Dot = () => Discord.Voice != null,
                Chevron = true,
                Activate = OpenVoice
            });
            l.Add(new MenuEntry
            {
                Glyph = "\uE962",
                Title = "Open Discord",
                Sub = () => "The controller works as a mouse",
                Activate = () => plugin.EnterDiscordMode(null)
            });
            return l;
        }

        // ================= Connection gate =================

        /// <summary>True when Discord is connected; otherwise adds status rows and starts connecting.</summary>
        private bool DiscordGate(List<MenuEntry> l)
        {
            if (Discord.Ready) return true;

            if (!Discord.Rpc.Configured)
            {
                l.AddRange(DiscordSetupEntries());
                return false;
            }
            if (!plugin.DiscordRunning)
            {
                l.Add(InfoCard("\uE8BD", "Discord", "Discord isn't running", null));
                l.Add(Row("\uE7E8", "Start Discord", null, () => plugin.StartDiscord()));
                return false;
            }

            if (!discordConnecting && (DateTime.Now - discordConnectTried).TotalSeconds > 6)
            {
                discordConnecting = true;
                discordConnectTried = DateTime.Now;
                Async(() => Discord.Connect(false),
                    ok => { discordConnecting = false; RefreshPage(TopPage); },
                    ex => { discordConnecting = false; RefreshPage(TopPage); });
            }

            if (discordConnecting)
            {
                l.Add(Note("\uE721", "Connecting to Discord\u2026"));
                return false;
            }

            l.Add(InfoCard("\uE8BD", "Discord", "Not connected", Discord.Rpc.LastError));
            l.Add(Row("\uE8D7", "Connect Discord", "Discord will ask you to Authorize once", () => plugin.ConnectDiscord()));
            l.Add(Row("\uE962", "Use the Discord window instead", null, () => plugin.EnterDiscordMode(null)));
            return false;
        }

        private List<MenuEntry> DiscordSetupEntries()
        {
            var l = new List<MenuEntry>();
            if (Discord.Ready)
            {
                l.Add(InfoCard("\uE73E", "Discord", "Connected", "Signed in as " + (Discord.Rpc.UserName ?? "you")));
                l.Add(Row("\uE72C", "Reconnect", null, () =>
                {
                    Discord.Rpc.Disconnect();
                    plugin.ConnectDiscord();
                }));
                var forget = Row("\uE8D8", "Disconnect and forget login", null, () =>
                {
                    Discord.Rpc.Disconnect();
                    Discord.Rpc.Forget();
                    Rebuild();
                });
                forget.Danger = true;
                l.Add(forget);
                return l;
            }

            l.Add(InfoCard("\uE8BD", "Discord", "Set up Discord in the overlay",
                "One-time setup:\n" +
                "1. On discord.com/developers/applications, create a New Application.\n" +
                "2. Under OAuth2, add the redirect http://localhost and save.\n" +
                "3. Copy the Client ID and Client Secret into the config file.\n" +
                "4. Choose Connect Discord and press Authorize in Discord."));
            l.Add(Row("\uE70F", "Edit config file", plugin.Config.DiscordClientId.Length > 0 ? "Client ID added" : null, () => plugin.OpenConfigFile()));
            l.Add(Row("\uE8D7", "Connect Discord", null, () => plugin.ConnectDiscord()));
            l.Add(Row("\uE962", "Use the Discord window instead", null, () => plugin.EnterDiscordMode(null)));
            return l;
        }

        // ================= Chats =================

        private void OpenChats()
        {
            List<OpenChat> chats = null;
            string error = null;
            bool loading = false;
            Page page = null;
            page = Push("Chats", () =>
            {
                var l = new List<MenuEntry>();
                if (!DiscordGate(l)) return l;

                if (chats == null && error == null && !loading)
                {
                    loading = true;
                    Async(() =>
                    {
                        string info;
                        var found = DiscordHelper.OpenChats(true, out info);
                        if (found.Count == 0) throw new Exception(info);
                        return found;
                    },
                    r => { chats = r; loading = false; Refocus(); RefreshPage(page); },
                    ex => { error = ex.Message; loading = false; Refocus(); RefreshPage(page); });
                }

                l.Add(Header("Open chats"));
                if (error != null)
                {
                    l.Add(Note("\uE783", error));
                    l.Add(Row("\uE72C", "Try again", null, () => { error = null; chats = null; Rebuild(); }));
                }
                else if (chats == null) l.Add(Note("\uE721", "Loading your chats\u2026"));
                else foreach (OpenChat c in chats)
                {
                    OpenChat chat = c;
                    l.Add(new MenuEntry
                    {
                        Glyph = c.Group ? "\uE716" : "\uE77B",
                        Title = c.Name,
                        Dot = () => chat.Unread,
                        Chevron = true,
                        Activate = () => OpenChat(chat.ChannelId, chat.Name, true)
                    });
                }

                l.Add(Divider());
                l.Add(new MenuEntry
                {
                    Glyph = "\uE8F1",
                    Title = "Server channels",
                    Chevron = true,
                    Activate = () => OpenServerList(false)
                });
                return l;
            });
        }

        private static string TimeText(DateTime? t)
        {
            if (!t.HasValue) return "";
            DateTime v = t.Value;
            if (v.Date == DateTime.Today) return v.ToString("HH:mm");
            if (v.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            return v.ToString("d MMM");
        }

        private void OpenGuild(GuildInfo g, bool voice)
        {
            List<ChannelInfo> channels = null;
            string error = null;
            Page page = null;
            page = Push(g.Name, () =>
            {
                var l = new List<MenuEntry>();
                if (error != null) { l.Add(Note("\uE783", "Couldn't load channels: " + error)); return l; }
                if (channels == null) { l.Add(Note("\uE721", voice ? "Finding voice channels\u2026" : "Loading channels\u2026")); return l; }

                var shown = channels.Where(c => voice ? c.IsVoice : (c.Type == 0 || c.Type == 5)).ToList();
                if (shown.Count == 0) l.Add(Note("\uE783", voice ? "No voice channels here" : "No text channels here"));
                foreach (ChannelInfo c in shown)
                {
                    ChannelInfo ch = c;
                    if (voice)
                    {
                        string who = c.VoiceMembers.Count == 0 ? "Empty" :
                            c.VoiceMembers.Count + " in call: " + string.Join(", ", c.VoiceMembers.Take(4).Select(m => m.Name)) + (c.VoiceMembers.Count > 4 ? "\u2026" : "");
                        bool here = Discord.Voice != null && Discord.Voice.ChannelId == c.Id;
                        l.Add(new MenuEntry
                        {
                            Glyph = "\uE767",
                            Title = c.Name,
                            Sub = () => who,
                            Dot = () => here,
                            Activate = () => JoinVoice(ch.Id, ch.Name)
                        });
                    }
                    else
                    {
                        l.Add(new MenuEntry
                        {
                            Glyph = "\uE8BD",
                            Title = "#" + c.Name,
                            Chevron = true,
                            Activate = () => OpenChat(ch.Id, "#" + ch.Name, false)
                        });
                    }
                }
                return l;
            });
            Async(() =>
            {
                var list = Discord.Channels(g.Id);
                if (voice)
                {
                    foreach (var c in list.Where(x => x.IsVoice))
                    {
                        try
                        {
                            var full = Discord.Channel(c.Id);
                            c.VoiceMembers.AddRange(full.VoiceMembers);
                        }
                        catch (Exception) { }
                    }
                }
                return list;
            },
            r => { channels = r; RefreshPage(page); },
            ex => { error = ex.Message; RefreshPage(page); });
        }

        // ================= One chat =================

        private void OpenChat(string channelId, string title, bool isDm)
        {
            ChannelInfo info = null;
            string error = null;
            Page page = null;
            page = Push(title, () =>
            {
                var l = new List<MenuEntry>();
                if (isDm || (info != null && info.IsDm))
                    l.Add(Row("\uE717", "Call " + title, null, () => JoinVoice(channelId, title)));
                l.Add(Row("\uE70F", "Write a message", null, () =>
                    plugin.Compose("Message " + title, text => plugin.SendDiscordMessage(channelId, text))));
                l.Add(new MenuEntry
                {
                    Glyph = "\uE8F2",
                    Title = "Quick replies",
                    Chevron = true,
                    Activate = () => OpenQuickReplies(channelId, title)
                });

                l.Add(Header("Messages"));
                if (error != null) l.Add(Note("\uE783", "Couldn't load messages: " + error));
                else if (info == null) l.Add(Note("\uE721", "Loading messages\u2026"));
                else if (info.Messages.Count == 0) l.Add(Note("\uE8BD", "No recent messages to show"));
                else foreach (ChatMessage m in info.Messages.Take(40))
                {
                    bool mine = m.AuthorId != null && m.AuthorId == Discord.Rpc.UserId;
                    l.Add(new MenuEntry
                    {
                        Title = mine ? "You" : m.Author,
                        Sub = () => m.Content,
                        Image = m.Avatar,
                        RoundImage = true,
                        ImageW = 40,
                        ImageH = 40,
                        Glyph = "\uE77B",
                        Right = new TextBlock { Text = TimeText(m.Time), FontSize = 15, Foreground = OverlayTheme.TextDim }
                    });
                }
                return l;
            });
            bool left = false;
            page.OnLeave = () =>
            {
                left = true;
                new System.Threading.Thread(() => { try { Discord.StopWatching(); } catch (Exception) { } }) { IsBackground = true }.Start();
            };

            Async(() =>
            {
                var ch = Discord.Channel(channelId);
                Discord.Watch(channelId, msg => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (info == null || info.Messages.Any(x => x.Id == msg.Id)) return;
                    info.Messages.Insert(0, msg);
                    RefreshPage(page);
                })));
                // left (or the overlay closed) while this loaded: don't keep watching a chat nobody is looking at
                if (left) Discord.StopWatching();
                return ch;
            },
            r => { info = r; RefreshPage(page); },
            ex => { error = ex.Message; RefreshPage(page); });
        }

        private void OpenQuickReplies(string channelId, string title)
        {
            Push("Quick replies", () =>
            {
                var l = new List<MenuEntry>();
                l.Add(Header("Send to " + title));
                foreach (string reply in plugin.Config.QuickReplies)
                {
                    string text = reply;
                    l.Add(Row("\uE8BD", text, null, () =>
                    {
                        plugin.SendDiscordMessage(channelId, text);
                        Back();
                    }));
                }
                if (plugin.Config.QuickReplies.Count == 0) l.Add(Note("\uE8BD", "No quick replies yet"));
                l.Add(Divider());
                l.Add(Row("\uE70F", "Edit quick replies", "In the config file", () => plugin.OpenConfigFile()));
                return l;
            });
        }

        // ================= Voice =================

        private void OpenVoice()
        {
            Page page = Push("Voice chat", () =>
            {
                var l = new List<MenuEntry>();
                if (!DiscordGate(l)) return l;

                VoiceInfo v = Discord.Voice;
                if (v != null)
                {
                    l.Add(InfoCard("\uE717", "In call", v.Name, v.GuildName));
                    foreach (VoiceMember m in Discord.MembersOf(v))
                    {
                        VoiceMember member = m;
                        l.Add(new MenuEntry
                        {
                            Title = m.Name,
                            Image = m.Avatar,
                            RoundImage = true,
                            ImageW = 38,
                            ImageH = 38,
                            Glyph = "\uE77B",
                            Dot = () =>
                            {
                                var now = Discord.Voice;
                                return now != null && Discord.MembersOf(now).Any(x => x.UserId == member.UserId && x.Speaking);
                            },
                            Right = new TextBlock
                            {
                                Text = m.Deafened ? "\uE74F" : (m.Muted ? "\uEC54" : ""),
                                FontFamily = IconFont,
                                FontSize = 20,
                                Foreground = OverlayTheme.DangerBrush
                            },
                            Selectable = false
                        });
                    }
                }
                else l.Add(InfoCard("\uE717", null, "Not in a call", "Pick a voice channel below to join"));

                l.Add(MuteRow());
                l.Add(DeafenRow());
                if (v != null)
                {
                    var leave = Row("\uE778", "Leave call", null, () => plugin.LeaveCall());
                    leave.Danger = true;
                    l.Add(leave);
                }
                l.Add(Divider());
                l.Add(new MenuEntry
                {
                    Glyph = "\uE716",
                    Title = "Join a voice channel",
                    Chevron = true,
                    Activate = OpenVoiceServers
                });
                return l;
            });
            page.Live = true;
        }

        private void OpenVoiceServers()
        {
            OpenServerList(true);
        }

        private void OpenServerList(bool voice)
        {
            List<GuildInfo> guilds = null;
            string error = null;
            Page page = null;
            page = Push(voice ? "Join a voice channel" : "Server channels", () =>
            {
                var l = new List<MenuEntry>();
                if (error != null) l.Add(Note("\uE783", "Couldn't load your servers: " + error));
                else if (guilds == null) l.Add(Note("\uE721", "Loading servers\u2026"));
                else foreach (GuildInfo g in guilds)
                {
                    GuildInfo gi = g;
                    l.Add(new MenuEntry
                    {
                        Title = g.Name,
                        Image = g.Icon,
                        RoundImage = true,
                        ImageW = 44,
                        ImageH = 44,
                        Glyph = "\uE716",
                        Chevron = true,
                        Activate = () => OpenGuild(gi, voice)
                    });
                }
                return l;
            });
            Async(() => Discord.Guilds(),
                g => { guilds = g; RefreshPage(page); },
                ex => { error = ex.Message; RefreshPage(page); });
        }

        private void JoinVoice(string channelId, string name)
        {
            VoiceInfo current = Discord.Voice;
            if (current != null && current.ChannelId == channelId)
            {
                plugin.Toast("You're already in " + name, "");
                return;
            }

            Action join = () =>
            {
                plugin.Toast("Joining " + name + "\u2026", "");
                Async(() => { Discord.Join(channelId); return true; },
                    ok =>
                    {
                        // Back to the Voice chat page.
                        int idx = stack.FindIndex(p => p.Title == "Voice chat");
                        if (idx >= 0) PopTo(idx + 1);
                        else { PopTo(1); OpenVoice(); }
                    },
                    ex => plugin.Toast("Couldn't join " + name, ex.Message));
            };

            if (current != null)
                PushConfirm("Move to " + name, "You'll leave " + current.Name + ".", "\uE717", "Join " + name, false, join);
            else join();
        }
    }
}

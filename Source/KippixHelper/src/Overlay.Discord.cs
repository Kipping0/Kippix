using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace KippixOverlay
{
    /// <summary>A conversation in Discord's Direct Messages list.</summary>
    internal class OpenChat
    {
        public string ChannelId;
        public string Name;
        public bool Group;
        public bool Unread;
    }

    internal static class DiscordHelper
    {
        /// <summary>Discord's main top-level windows (visible or hidden in the tray).</summary>
        public static List<IntPtr> Windows()
        {
            var pids = new HashSet<uint>();
            try { foreach (Process p in Process.GetProcessesByName("Discord")) using (p) pids.Add((uint)p.Id); } catch (Exception) { }
            var result = new List<IntPtr>();
            if (pids.Count == 0) return result;
            Native.EnumWindows((h, l) =>
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                var cls = new StringBuilder(64);
                Native.GetClassName(h, cls, 64);
                var title = new StringBuilder(256);
                Native.GetWindowText(h, title, 256);
                if (cls.ToString().StartsWith("Chrome_WidgetWin") && title.Length > 0) result.Add(h);
                return true;
            }, IntPtr.Zero);
            // Visible windows first.
            result.Sort((a, b) => Native.IsWindowVisible(b).CompareTo(Native.IsWindowVisible(a)));
            return result;
        }

        private static readonly Regex DmLink = new Regex(@"/channels/@me/(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// Reads the Direct Messages list from Discord's window through Windows accessibility.
        /// Discord only shows that list on its Home view, so with navigate=true it switches there if needed. Slow; call off the UI thread.
        /// </summary>
        public static List<OpenChat> OpenChats(bool navigate, out string info)
        {
            info = "";
            var list = ReadOpenChats();
            if (list.Count == 0 && navigate && IsRunning())
            {
                try { using (Process.Start("discord://-/channels/@me")) { } } catch (Exception) { }
                for (int i = 0; i < 6 && list.Count == 0; i++)
                {
                    System.Threading.Thread.Sleep(700);
                    list = ReadOpenChats();
                }
            }
            if (!IsRunning()) info = "Discord isn't running";
            else if (list.Count == 0) info = "Couldn't read your Direct Messages from Discord";
            else info = list.Count + " open chats";
            return list;
        }

        private static List<OpenChat> ReadOpenChats()
        {
            var result = new List<OpenChat>();
            var seen = new HashSet<string>();
            foreach (IntPtr h in Windows())
            {
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(h);
                    var links = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
                    foreach (AutomationElement link in links)
                    {
                        string url = null;
                        object vp;
                        if (link.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) url = ((ValuePattern)vp).Current.Value;
                        if (string.IsNullOrEmpty(url)) continue;
                        Match m = DmLink.Match(url);
                        if (!m.Success || !seen.Add(m.Groups[1].Value)) continue;
                        string raw = link.Current.Name ?? "";
                        result.Add(new OpenChat
                        {
                            ChannelId = m.Groups[1].Value,
                            Group = raw.IndexOf("group message", StringComparison.OrdinalIgnoreCase) >= 0,
                            Unread = raw.StartsWith("unread", StringComparison.OrdinalIgnoreCase),
                            Name = CleanName(raw)
                        });
                    }
                }
                catch (Exception) { }
                if (result.Count > 0) break;
            }
            return result;
        }

        private static string CleanName(string raw)
        {
            string s = raw;
            s = Regex.Replace(s, @"^\s*(unread|\d+ mentions?),?\s*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*\((direct|group) message\)", "", RegexOptions.IgnoreCase);
            int comma = s.IndexOf(", ", StringComparison.Ordinal);
            if (comma > 0) s = s.Substring(0, comma);
            s = s.Trim();
            return s.Length > 0 ? s : "Direct message";
        }

        public static bool IsRunning()
        {
            // asked every couple of seconds: the Process objects are let go straight away
            try
            {
                Process[] all = Process.GetProcessesByName("Discord");
                foreach (Process p in all) p.Dispose();
                return all.Length > 0;
            }
            catch (Exception) { return false; }
        }

        public static bool IsDiscordPid(uint pid)
        {
            return Audio.IsExeNamed(pid, "Discord.exe");
        }

        /// <summary>Discord's visible main window, or zero.</summary>
        public static IntPtr MainWindow()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("Discord"))
                {
                    using (p)
                    {
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && Native.IsWindowVisible(h)) return h;
                    }
                }
            }
            catch (Exception) { }
            return IntPtr.Zero;
        }

        /// <summary>Starts Discord minimised to the tray, so it runs in the background.</summary>
        public static void StartInBackground()
        {
            if (IsRunning()) return;
            try
            {
                string update = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Discord\Update.exe");
                if (File.Exists(update))
                {
                    using (Process.Start(new ProcessStartInfo(update, "--processStart Discord.exe --process-start-args \"--start-minimized\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })) { }
                    return;
                }
                using (Process.Start("discord://")) { }
            }
            catch (Exception) { }
        }

        /// <summary>Shows Discord (from the tray if needed) and waits up to 10 s for its window.</summary>
        public static IntPtr ShowAndWait(string uri)
        {
            IntPtr h = MainWindow();
            if (h == IntPtr.Zero || uri != null)
            {
                try { using (Process.Start(uri ?? "discord://")) { } } catch (Exception) { }
            }
            for (int i = 0; i < 100; i++)
            {
                h = MainWindow();
                if (h != IntPtr.Zero) return h;
                Thread.Sleep(100);
            }
            return IntPtr.Zero;
        }

        /// <summary>Presses Discord's own "Disconnect" button through Windows accessibility. Slow; call off the UI thread.</summary>
        public static bool LeaveCall()
        {
            try
            {
                IntPtr h = MainWindow();
                if (h == IntPtr.Zero)
                {
                    foreach (Process p in Process.GetProcessesByName("Discord"))
                        using (p) if (h == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) h = p.MainWindowHandle;
                }
                if (h == IntPtr.Zero) return false;

                AutomationElement root = AutomationElement.FromHandle(h);
                var cond = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Disconnect"));
                AutomationElement btn = root.FindFirst(TreeScope.Descendants, cond);
                if (btn == null) return false;
                object pattern;
                if (!btn.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
                ((InvokePattern)pattern).Invoke();
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>A key identifying Discord's focused text box, or null if Discord isn't typing-focused.</summary>
        public static string FocusedTextBoxKey()
        {
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null) return null;
                if (el.Current.ControlType != ControlType.Edit) return null;
                if (!IsDiscordPid((uint)el.Current.ProcessId)) return null;
                int[] id = el.GetRuntimeId();
                return id == null ? "edit" : string.Join(".", id);
            }
            catch (Exception) { return null; }
        }
    }
}

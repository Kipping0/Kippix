using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace KippixOverlay
{
    internal class RpcException : Exception
    {
        public readonly int Code;
        public RpcException(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// Talks to the Discord desktop app through its local RPC pipe (the same official interface stream decks use).
    /// Needs a Discord application of your own (client ID + secret) and a one-time "Authorize" in Discord.
    /// </summary>
    internal class DiscordRpc
    {
        public static readonly string[] Scopes = { "rpc", "identify", "messages.read", "rpc.notifications.read", "rpc.voice.read", "rpc.voice.write" };
        public const string RedirectUri = "http://localhost";

        public string ClientId;
        public string ClientSecret;

        public volatile bool Connected;
        public volatile bool Authenticated;
        public string UserName;
        public string UserId;
        public string LastError = "";

        /// <summary>Raised on the reader thread: (event name, data).</summary>
        public Action<string, Dictionary<string, object>> EventReceived;
        public Action StateChanged;

        private readonly string tokenPath;
        private readonly object writeLock = new object();
        private readonly object connectLock = new object();
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        private NamedPipeClientStream pipe;
        private ManualResetEvent ready;
        private string closeReason;

        private class Pending
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public Dictionary<string, object> Data;
            public string Error;
            public int Code;
        }

        public DiscordRpc(string tokenPath)
        {
            this.tokenPath = tokenPath;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
        }

        public bool Configured
        {
            get { return !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret); }
        }

        public bool Ready
        {
            get { return Connected && Authenticated; }
        }

        public bool HasSavedLogin
        {
            get { return File.Exists(tokenPath); }
        }

        // ================= Connection =================

        /// <summary>Connects and logs in. With allowAuthorize, shows Discord's "Authorize" prompt if needed. Blocks.</summary>
        public bool EnsureReady(bool allowAuthorize)
        {
            lock (connectLock)
            {
                if (Ready) return true;
                if (!Configured) { LastError = "Discord isn't set up yet"; return false; }
                try
                {
                    if (!Connected && !Open()) return false;
                    if (!Authenticated && !Login(allowAuthorize)) return false;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        private bool Open()
        {
            NamedPipeClientStream p = null;
            for (int i = 0; i < 10 && p == null; i++)
            {
                try
                {
                    var c = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    c.Connect(250);
                    p = c;
                }
                catch (Exception) { }
            }
            if (p == null) { LastError = "Discord isn't running"; return false; }

            pipe = p;
            closeReason = null;
            ready = new ManualResetEvent(false);
            Connected = true;
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "Kippix Discord" };
            t.Start();

            var hello = new Dictionary<string, object>();
            hello["v"] = 1;
            hello["client_id"] = ClientId.Trim();
            Write(0, hello);

            if (!ready.WaitOne(5000))
            {
                LastError = closeReason ?? "Discord didn't answer";
                Disconnect();
                return false;
            }
            return true;
        }

        public void Disconnect()
        {
            bool was = Connected;
            Connected = false;
            Authenticated = false;
            try { if (pipe != null) pipe.Dispose(); } catch (Exception) { }
            pipe = null;
            lock (pending)
            {
                foreach (var p in pending.Values) { p.Error = "Disconnected from Discord"; p.Done.Set(); }
                pending.Clear();
            }
            if (was && StateChanged != null) { try { StateChanged(); } catch (Exception) { } }
        }

        private void Write(int op, object payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            lock (writeLock)
            {
                var p = pipe;
                if (p == null) throw new RpcException(0, "Not connected to Discord");
                p.Write(frame, 0, frame.Length);
                p.Flush();
            }
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private void ReadLoop()
        {
            var p = pipe;
            var header = new byte[8];
            try
            {
                while (Connected && p == pipe)
                {
                    if (!ReadExact(p, header, 8)) break;
                    int op = BitConverter.ToInt32(header, 0);
                    int len = BitConverter.ToInt32(header, 4);
                    if (len < 0 || len > 64 * 1024 * 1024) break;
                    var body = new byte[len];
                    if (!ReadExact(p, body, len)) break;
                    string text = Encoding.UTF8.GetString(body);

                    if (op == 3) { Write(4, json.DeserializeObject(text)); continue; }
                    var msg = json.DeserializeObject(text) as Dictionary<string, object>;
                    if (op == 2)
                    {
                        closeReason = msg != null ? Str(msg, "message") : "Discord closed the connection";
                        break;
                    }
                    if (op == 1 && msg != null) Handle(msg);
                }
            }
            catch (Exception) { }
            if (p == pipe) Disconnect();
        }

        private void Handle(Dictionary<string, object> msg)
        {
            string cmd = Str(msg, "cmd");
            string evt = Str(msg, "evt");
            string nonce = Str(msg, "nonce");
            var data = Dict(msg, "data");

            if (cmd == "DISPATCH")
            {
                if (evt == "READY") { if (ready != null) ready.Set(); return; }
                var h = EventReceived;
                if (h != null) { try { h(evt, data ?? new Dictionary<string, object>()); } catch (Exception) { } }
                return;
            }

            if (nonce == null) return;
            Pending pend;
            lock (pending)
            {
                if (!pending.TryGetValue(nonce, out pend)) return;
                pending.Remove(nonce);
            }
            if (evt == "ERROR")
            {
                pend.Error = data != null ? (Str(data, "message") ?? "Discord error") : "Discord error";
                int code;
                pend.Code = data != null && int.TryParse(Str(data, "code"), out code) ? code : 0;
            }
            else pend.Data = data;
            pend.Done.Set();
        }

        /// <summary>Sends a command and waits for Discord's reply. Throws RpcException on errors. Blocks.</summary>
        public Dictionary<string, object> Command(string cmd, Dictionary<string, object> args, string evt, int timeoutMs)
        {
            if (!Connected) throw new RpcException(0, "Not connected to Discord");
            string nonce = Guid.NewGuid().ToString();
            var pend = new Pending();
            lock (pending) pending[nonce] = pend;

            var payload = new Dictionary<string, object>();
            payload["cmd"] = cmd;
            payload["args"] = args ?? new Dictionary<string, object>();
            payload["nonce"] = nonce;
            if (evt != null) payload["evt"] = evt;
            Write(1, payload);

            if (!pend.Done.WaitOne(timeoutMs))
            {
                lock (pending) pending.Remove(nonce);
                throw new RpcException(0, "Discord didn't respond");
            }
            if (pend.Error != null) throw new RpcException(pend.Code, pend.Error);
            return pend.Data;
        }

        public Dictionary<string, object> Command(string cmd, Dictionary<string, object> args)
        {
            return Command(cmd, args, null, 8000);
        }

        public void Subscribe(string evt, Dictionary<string, object> args)
        {
            try { Command("SUBSCRIBE", args, evt, 5000); } catch (Exception) { }
        }

        public void Unsubscribe(string evt, Dictionary<string, object> args)
        {
            try { Command("UNSUBSCRIBE", args, evt, 5000); } catch (Exception) { }
        }

        // ================= Login =================

        private class Token
        {
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public long expires_at { get; set; }
        }

        private bool Login(bool allowAuthorize)
        {
            Token token = LoadToken();
            if (token != null && token.expires_at < DateTime.UtcNow.AddHours(1).Ticks && !string.IsNullOrEmpty(token.refresh_token))
            {
                Token fresh = Exchange("refresh_token", "refresh_token", token.refresh_token);
                if (fresh != null) token = fresh;
            }

            if (token != null && Authenticate(token.access_token)) return true;

            if (token != null && !string.IsNullOrEmpty(token.refresh_token))
            {
                Token fresh = Exchange("refresh_token", "refresh_token", token.refresh_token);
                if (fresh != null && Authenticate(fresh.access_token)) return true;
            }

            if (!allowAuthorize)
            {
                if (token == null) LastError = "Not connected yet: choose Connect Discord";
                return false;
            }

            var args = new Dictionary<string, object>();
            args["client_id"] = ClientId.Trim();
            args["scopes"] = Scopes;
            Dictionary<string, object> res;
            try { res = Command("AUTHORIZE", args, null, 180000); }
            catch (RpcException ex)
            {
                LastError = "Authorize failed: " + ex.Message;
                return false;
            }
            string code = res != null ? Str(res, "code") : null;
            if (string.IsNullOrEmpty(code)) { LastError = "Discord didn't return a code"; return false; }

            Token t = Exchange("authorization_code", "code", code);
            return t != null && Authenticate(t.access_token);
        }

        private bool Authenticate(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken)) return false;
            try
            {
                var args = new Dictionary<string, object>();
                args["access_token"] = accessToken;
                var res = Command("AUTHENTICATE", args, null, 10000);
                var user = res != null ? Dict(res, "user") : null;
                if (user != null)
                {
                    UserId = Str(user, "id");
                    UserName = Str(user, "global_name") ?? Str(user, "username");
                }
                Authenticated = true;
                LastError = "";
                if (StateChanged != null) { try { StateChanged(); } catch (Exception) { } }
                return true;
            }
            catch (RpcException ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private Token Exchange(string grantType, string field, string value)
        {
            try
            {
                var form = new NameValueCollection();
                form["client_id"] = ClientId.Trim();
                form["client_secret"] = ClientSecret.Trim();
                form["grant_type"] = grantType;
                form[field] = value;
                form["redirect_uri"] = RedirectUri;

                string body;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Kippix (Playnite add-on)";
                    body = Encoding.UTF8.GetString(wc.UploadValues("https://discord.com/api/oauth2/token", "POST", form));
                }
                var d = json.DeserializeObject(body) as Dictionary<string, object>;
                if (d == null || Str(d, "access_token") == null) { LastError = "Discord login failed"; return null; }

                long seconds;
                long.TryParse(Str(d, "expires_in"), out seconds);
                var t = new Token
                {
                    access_token = Str(d, "access_token"),
                    refresh_token = Str(d, "refresh_token"),
                    expires_at = DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 600000).Ticks
                };
                SaveToken(t);
                return t;
            }
            catch (WebException ex)
            {
                string detail = "";
                try
                {
                    if (ex.Response != null)
                        using (var resp = ex.Response)
                        using (var r = new StreamReader(resp.GetResponseStream())) detail = r.ReadToEnd();
                }
                catch (Exception) { }
                LastError = "Discord login failed" + (detail.Length > 0 ? ": " + detail : "");
                if (detail.Contains("invalid_grant")) Forget();
                return null;
            }
            catch (Exception ex)
            {
                LastError = "Discord login failed: " + ex.Message;
                return null;
            }
        }

        private Token LoadToken()
        {
            try
            {
                if (!File.Exists(tokenPath)) return null;
                return json.Deserialize<Token>(File.ReadAllText(tokenPath));
            }
            catch (Exception) { return null; }
        }

        private void SaveToken(Token t)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tokenPath));
                File.WriteAllText(tokenPath, json.Serialize(t));
            }
            catch (Exception) { }
        }

        public void Forget()
        {
            try { if (File.Exists(tokenPath)) File.Delete(tokenPath); } catch (Exception) { }
        }

        // ================= Helpers =================

        public static Dictionary<string, object> Args(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static int Int(Dictionary<string, object> d, string key)
        {
            int i;
            return int.TryParse(Str(d, key), out i) ? i : -1;
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<Dictionary<string, object>> List(Dictionary<string, object> d, string key)
        {
            var result = new List<Dictionary<string, object>>();
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null || v is string) return result;
            var e = v as IEnumerable;
            if (e == null) return result;
            foreach (object o in e)
            {
                var x = o as Dictionary<string, object>;
                if (x != null) result.Add(x);
            }
            return result;
        }
    }
}

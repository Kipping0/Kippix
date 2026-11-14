using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.1) GitHub, for the Community gallery's Upload: signing in (GitHub's device sign-in: a code you enter at
    /// github.com/login/device, no password typed into Kippix) and sending an upload to the gallery as a pull request that the
    /// gallery's owner approves (merges) or turns down (closes). Everything here runs off the screen's thread.
    ///
    /// Safe by design: the app's Client ID is public (it's in every OAuth app's sign-in page), and the device sign-in needs no
    /// client secret, so there's none in Kippix. The sign-in's token is your own, stays on this PC encrypted for your Windows
    /// account (ThemeApi.Protect), and is only ever sent to api.github.com.
    /// </summary>
    internal static class GitHubApi
    {
        internal const string ClientId = "Ov23liYmXoTyUrSY0cG9";
        // public_repo: needed to make your copy of the gallery (a fork) and propose your upload from it. Nothing private.
        internal const string Scope = "public_repo";
        private const string Api = "https://api.github.com";

        internal class Failure : Exception
        {
            public readonly int Status;
            public Failure(int status, string message) : base(message) { Status = status; }
        }

        private static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static string Send(string method, string url, string token, string contentType, string body, string accept, out int status)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "Kippix (Playnite add-on)";
            req.Accept = accept ?? "application/vnd.github+json";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            if (!string.IsNullOrEmpty(token)) req.Headers["Authorization"] = "Bearer " + token;
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentType = contentType ?? "application/json";
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    status = (int)resp.StatusCode;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) throw;
                status = (int)r.StatusCode;
                try { using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd(); }
                catch (Exception) { return ""; }
            }
        }

        private static Dictionary<string, object> Call(string method, string path, string token, object body, params int[] ok)
        {
            int status;
            string text = Send(method, path.StartsWith("http") ? path : Api + path, token, "application/json", body != null ? Js.Serialize(body) : null, null, out status);
            if (status >= 200 && status < 300 || ok.Contains(status))
            {
                if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object> { { "_status", status } };
                var d = Js.DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
                d["_status"] = status;
                return d;
            }
            string msg = "";
            try { var e = Js.DeserializeObject(text) as Dictionary<string, object>; if (e != null && e.ContainsKey("message")) msg = Convert.ToString(e["message"], CultureInfo.InvariantCulture); } catch (Exception) { }
            throw new Failure(status, "GitHub said " + status + (msg.Length > 0 ? ": " + msg : ""));
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        // ---------------- signing in (device flow) ----------------

        internal class DeviceCode
        {
            public string Device, User, Uri;
            public int Interval = 5, ExpiresIn = 900;
        }

        internal static DeviceCode StartSignIn()
        {
            int status;
            string text = Send("POST", "https://github.com/login/device/code", null, "application/x-www-form-urlencoded",
                               "client_id=" + Uri.EscapeDataString(ClientId) + "&scope=" + Uri.EscapeDataString(Scope), "application/json", out status);
            var d = Js.DeserializeObject(text) as Dictionary<string, object>;
            if (status != 200 || d == null || Str(d, "device_code").Length == 0)
                throw new Failure(status, "GitHub didn't start the sign-in" + (d != null && Str(d, "error_description").Length > 0 ? ": " + Str(d, "error_description") : ""));
            int interval, expires;
            return new DeviceCode
            {
                Device = Str(d, "device_code"), User = Str(d, "user_code"),
                Uri = Str(d, "verification_uri").Length > 0 ? Str(d, "verification_uri") : "https://github.com/login/device",
                Interval = int.TryParse(Str(d, "interval"), out interval) && interval > 0 ? interval : 5,
                ExpiresIn = int.TryParse(Str(d, "expires_in"), out expires) && expires > 0 ? expires : 900
            };
        }

        /// <summary>One look at whether the code was entered: the token, or null while waiting ("slow_down" makes the wait longer;
        /// expired or turned down throws).</summary>
        internal static string PollSignIn(DeviceCode code)
        {
            int status;
            string text = Send("POST", "https://github.com/login/oauth/access_token", null, "application/x-www-form-urlencoded",
                               "client_id=" + Uri.EscapeDataString(ClientId) + "&device_code=" + Uri.EscapeDataString(code.Device) +
                               "&grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:device_code"), "application/json", out status);
            var d = Js.DeserializeObject(text) as Dictionary<string, object>;
            if (d == null) return null;
            string token = Str(d, "access_token");
            if (token.Length > 0) return token;
            switch (Str(d, "error"))
            {
                case "authorization_pending": return null;
                case "slow_down": code.Interval += 5; return null;
                case "expired_token": throw new Failure(410, "expired");
                case "access_denied": throw new Failure(403, "denied");
                default: throw new Failure(status, Str(d, "error_description").Length > 0 ? Str(d, "error_description") : Str(d, "error"));
            }
        }

        internal static string Login(string token)
        {
            return Str(Call("GET", "/user", token, null), "login");
        }

        // ---------------- sending an upload ----------------

        internal class Sent
        {
            public int Number;
            public string Url, Id;
        }

        private static string Slug(string s)
        {
            string id = new string((s ?? "").ToLowerInvariant().Select(ch => ch < 128 && char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            while (id.Contains("--")) id = id.Replace("--", "-");
            return id.Length > 60 ? id.Substring(0, 60).Trim('-') : id;
        }

        private static bool Exists(string repo, string branch, string path, string token)
        {
            try { Call("GET", "/repos/" + repo + "/contents/" + path + "?ref=" + Uri.EscapeDataString(branch), token, null); return true; }
            catch (Failure f) { if (f.Status == 404) return false; throw; }
        }

        private static void PutFile(string repo, string branch, string path, byte[] data, string message, string token, Dictionary<string, object> who)
        {
            string sha = null;
            try
            {
                var have = Call("GET", "/repos/" + repo + "/contents/" + path + "?ref=" + Uri.EscapeDataString(branch), token, null);
                sha = Str(have, "sha");
            }
            catch (Failure f) { if (f.Status != 404) throw; }
            var body = new Dictionary<string, object> { { "message", message }, { "content", Convert.ToBase64String(data) }, { "branch", branch } };
            if (!string.IsNullOrEmpty(sha)) body["sha"] = sha;
            if (who != null) { body["author"] = who; body["committer"] = who; }
            Call("PUT", "/repos/" + repo + "/contents/" + path, token, body);
        }

        /// <summary>
        /// Sends one share (Documents › Kippix Layouts › Share: id.json, id.jpg, id.entry.json) to the gallery: your copy of the
        /// gallery's repository (made the first time), a branch with the three files, and a pull request to the gallery. The
        /// owner's own uploads go straight to a branch of the gallery (you can't fork your own repository).
        /// </summary>
        private static void SetPreviews(Dictionary<string, object> entry, string kind, string sub, string uid, bool main, int extras)
        {
            if (!main) { entry.Remove("previews"); return; }
            string dir = kind == "layouts" ? "previews/" : "previews/" + sub + "/";
            var list = new List<string> { dir + uid + ".jpg" };
            for (int i = 0; i < extras; i++) list.Add(dir + uid + "-" + (i + 2) + ".jpg");
            entry["previews"] = list.ToArray();
        }

        internal static Sent Upload(string token, string login, string repo, string baseBranch, string kind, string folder, string localId, string description, Action<string> progress)
        {
            string step = "start";
            Action<string> say = s => { step = s; if (progress != null) progress(s); };
            try { return UploadSteps(token, login, repo, baseBranch, kind, folder, localId, description, say); }
            catch (Failure f) { throw new Failure(f.Status, f.Message + " (at: " + step + ")"); }
        }

        private static Sent UploadSteps(string token, string login, string repo, string baseBranch, string kind, string folder, string localId, string description, Action<string> progress)
        {
            if (string.IsNullOrEmpty(repo) || !repo.Contains("/")) throw new Failure(0, "no gallery");
            string owner = repo.Split('/')[0];
            string sub = kind == "looks" ? "looks" : kind == "widgets" ? "widgets" : kind == "gamepages" ? "gamepages" : "layouts";
            string jsonPath = Path.Combine(folder, localId + ".json"), jpgPath = Path.Combine(folder, localId + ".jpg"), entryPath = Path.Combine(folder, localId + ".entry.json");
            if (!File.Exists(jsonPath) || !File.Exists(entryPath)) throw new Failure(0, "the shared files are missing");
            byte[] json = File.ReadAllBytes(jsonPath);
            if (json.Length > 8 * 1024 * 1024) throw new Failure(0, "it's too big to upload (over 8 MB)");
            byte[] jpg = File.Exists(jpgPath) ? File.ReadAllBytes(jpgPath) : null;
            // (1.0.4) more pictures: id-2.jpg … (the gallery shows them one after another)
            var extras = new List<byte[]>();
            if (jpg != null)
                for (int i = 2; i <= ThemeApi.MaxSharePictures; i++)
                {
                    string p = Path.Combine(folder, localId + "-" + i + ".jpg");
                    if (File.Exists(p)) extras.Add(File.ReadAllBytes(p));
                }

            // the gallery's id: yours, with your GitHub name (so two people's "My layout" don't clash, and yours again updates it)
            string uid = Slug(localId + "-" + login);
            if (uid.Length == 0) uid = Slug(sub + "-" + login);
            var entry = Js.DeserializeObject(File.ReadAllText(entryPath, Encoding.UTF8)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            entry["id"] = uid;
            entry["author"] = login;
            entry["file"] = sub + "/" + uid + ".json";
            entry["preview"] = jpg != null ? (kind == "layouts" ? "previews/" : "previews/" + sub + "/") + uid + ".jpg" : "";
            SetPreviews(entry, kind, sub, uid, jpg != null, extras.Count);
            if (!string.IsNullOrWhiteSpace(description)) entry["description"] = description.Trim();
            entry["added"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string name = Str(entry, "name");

            // (1.0.2) the files are saved under your GitHub name and GitHub's private address for you (id+name@users.noreply.github.com),
            // never your own email address: commits in a public repository show it to anyone
            Dictionary<string, object> who = null;
            try
            {
                var me = Call("GET", "/user", token, null);
                string uidNum = Str(me, "id");
                if (uidNum.Length > 0) who = new Dictionary<string, object> { { "name", login }, { "email", uidNum + "+" + login + "@users.noreply.github.com" } };
            }
            catch (Failure) { }
            if (who == null) who = new Dictionary<string, object> { { "name", login }, { "email", login + "@users.noreply.github.com" } };

            // where the branch goes: your fork, or (the owner) the gallery itself
            bool own = string.Equals(owner, login, StringComparison.OrdinalIgnoreCase);
            string target = repo;
            if (!own)
            {
                if (progress != null) progress("fork");
                var fork = Call("POST", "/repos/" + repo + "/forks", token, new Dictionary<string, object> { { "default_branch_only", true } }, 202);
                target = Str(fork, "full_name");
                if (target.Length == 0) target = login + "/" + repo.Split('/')[1];
                // a new fork takes a moment to be ready
                for (int i = 0; i < 30; i++)
                {
                    try { Call("GET", "/repos/" + target + "/git/ref/heads/" + baseBranch, token, null); break; }
                    catch (Failure f) { if (f.Status != 404 && f.Status != 409) throw; }
                    Thread.Sleep(2000);
                }
            }

            if (progress != null) progress("branch");
            // (1.0.3) the branch starts from YOUR copy's main, not the gallery's: when the gallery has changed its GitHub
            // automation (.github/workflows) since your copy was made, putting those changes in your copy needs a wider
            // GitHub permission ("workflow") that Kippix doesn't ask for, and GitHub answers 404. The request still only
            // shows your three files, because GitHub compares from where the two last met.
            string baseSha = "";
            if (!own)
                try { baseSha = Str(Call("GET", "/repos/" + target + "/git/ref/heads/" + baseBranch, token, null)["object"] as Dictionary<string, object>, "sha"); }
                catch (Failure f) { if (f.Status != 404 && f.Status != 409) throw; }
            if (baseSha.Length == 0)
            {
                var baseRef = Call("GET", "/repos/" + repo + "/git/ref/heads/" + baseBranch, token, null);
                baseSha = Str(baseRef.ContainsKey("object") ? baseRef["object"] as Dictionary<string, object> : null, "sha");
            }
            if (baseSha.Length == 0) throw new Failure(0, "couldn't read the gallery");
            string branch = "kippix-" + sub + "-" + uid + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            if (branch.Length > 120) branch = branch.Substring(0, 120);
            Call("POST", "/repos/" + target + "/git/refs", token, new Dictionary<string, object> { { "ref", "refs/heads/" + branch }, { "sha", baseSha } });

            // already in the gallery under this name but not in your (older) copy: a new name, so the two don't clash
            if (!own && Exists(repo, baseBranch, "community/" + Str(entry, "file"), token) && !Exists(target, branch, "community/" + Str(entry, "file"), token))
            {
                uid = Slug(localId + "-" + DateTime.UtcNow.ToString("yyMMddHHmm", CultureInfo.InvariantCulture) + "-" + login);
                entry["id"] = uid;
                entry["file"] = sub + "/" + uid + ".json";
                if (jpg != null) entry["preview"] = (kind == "layouts" ? "previews/" : "previews/" + sub + "/") + uid + ".jpg";
                SetPreviews(entry, kind, sub, uid, jpg != null, extras.Count);
            }

            if (progress != null) progress("files");
            string what = kind == "looks" ? "look pack" : kind == "widgets" ? "widget" : kind == "gamepages" ? "game page" : "layout";
            string msg = "Community " + what + ": " + name;
            PutFile(target, branch, "community/" + Str(entry, "file"), json, msg, token, who);
            if (jpg != null) PutFile(target, branch, "community/" + Str(entry, "preview"), jpg, msg + " (picture)", token, who);
            var previews = entry.ContainsKey("previews") ? entry["previews"] as string[] : null;
            for (int i = 0; i < extras.Count && previews != null && i + 1 < previews.Length; i++)
                PutFile(target, branch, "community/" + previews[i + 1], extras[i], msg + " (picture " + (i + 2) + ")", token, who);
            PutFile(target, branch, "community/entries/" + sub + "/" + uid + ".json", Encoding.UTF8.GetBytes(Js.Serialize(entry)), msg + " (gallery entry)", token, who);

            if (progress != null) progress("request");
            string previewUrl = jpg != null ? "https://raw.githubusercontent.com/" + target + "/" + branch + "/community/" + Str(entry, "preview") : null;
            var sb = new StringBuilder();
            sb.AppendLine("A " + what + " for the Community gallery, sent from Kippix " + Str(entry, "themeVersion") + ".");
            sb.AppendLine();
            sb.AppendLine("**Name:** " + name);
            if (Str(entry, "description").Length > 0) sb.AppendLine("**About it:** " + Str(entry, "description"));
            sb.AppendLine("**By:** @" + login);
            if (previewUrl != null)
            {
                sb.AppendLine();
                var all = entry.ContainsKey("previews") ? entry["previews"] as string[] : null;
                foreach (string pv in all ?? new[] { Str(entry, "preview") })
                    sb.AppendLine("![Preview](https://raw.githubusercontent.com/" + target + "/" + branch + "/community/" + pv + ")");
            }
            sb.AppendLine();
            sb.AppendLine("Merge to add it to the gallery (the list updates by itself a minute later), or close to turn it down.");
            var pr = Call("POST", "/repos/" + repo + "/pulls", token, new Dictionary<string, object>
            {
                { "title", "Community " + what + ": " + name },
                { "head", (own ? "" : target.Split('/')[0] + ":") + branch },
                { "base", baseBranch },
                { "body", sb.ToString() },
                { "maintainer_can_modify", !own }
            });
            int number;
            int.TryParse(Str(pr, "number"), out number);
            return new Sent { Number = number, Url = Str(pr, "html_url"), Id = uid };
        }

        /// <summary>What happened to a sent upload: "open" (waiting), "merged" (in the gallery) or "closed" (turned down).</summary>
        internal static string State(string token, string repo, int number)
        {
            var pr = Call("GET", "/repos/" + repo + "/pulls/" + number.ToString(CultureInfo.InvariantCulture), token, null);
            object merged;
            if (pr.TryGetValue("merged", out merged) && merged is bool && (bool)merged) return "merged";
            return Str(pr, "state") == "closed" ? "closed" : "open";
        }

        // ---------------- the gallery's download counts ----------------

        /// <summary>(1.0.4) Every file in one release with how many times it's been downloaded (name → count). Needs no sign-in.</summary>
        internal static Dictionary<string, int> ReleaseDownloads(string repo, string tag)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> rel;
            try { rel = Call("GET", "/repos/" + repo + "/releases/tags/" + Uri.EscapeDataString(tag), null, null); }
            catch (Failure f) { if (f.Status == 404) return result; throw; }
            string id = Str(rel, "id");
            for (int page = 1; page <= 10 && id.Length > 0; page++)
            {
                int status;
                string text = Send("GET", Api + "/repos/" + repo + "/releases/" + id + "/assets?per_page=100&page=" + page, null, null, null, null, out status);
                if (status != 200) break;
                var arr = Js.DeserializeObject(text) as object[];
                if (arr == null || arr.Length == 0) break;
                foreach (var a in arr.OfType<Dictionary<string, object>>())
                {
                    int n;
                    if (int.TryParse(Str(a, "download_count"), out n)) result[Str(a, "name")] = n;
                }
                if (arr.Length < 100) break;
            }
            return result;
        }

        // ---------------- updates ----------------

        internal class Release
        {
            public string Tag, Version, Url;
            public Dictionary<string, string> Assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // name → download address
        }

        internal static Release Latest(string repo)
        {
            var r = Call("GET", "/repos/" + repo + "/releases/latest", null, null);
            var rel = new Release { Tag = Str(r, "tag_name"), Url = Str(r, "html_url") };
            rel.Version = rel.Tag.TrimStart('v', 'V');
            object assets;
            if (r.TryGetValue("assets", out assets) && assets is object[])
                foreach (var a in ((object[])assets).OfType<Dictionary<string, object>>())
                {
                    string n = Str(a, "name"), u = Str(a, "browser_download_url");
                    if (n.Length > 0 && u.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) rel.Assets[n] = u;
                }
            return rel;
        }

        internal static void Download(string url, string file)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Kippix (Playnite add-on)";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 120000;
            req.AllowAutoRedirect = true;
            string tmp = file + ".part";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = File.Create(tmp)) s.CopyTo(fs);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.7) Edit this game › STEAMGRIDDB: Next background / Next cover. Each press swaps the game's picture for the next
    /// one SteamGridDB has for it (round to the first after the last), with your own free key (desktop settings › SteamGridDB).
    /// Only still pictures (PNG / JPEG: WPF can't show WebP without a codec Windows may not have). A picture the same size as
    /// the one the game already has is taken as that one and skipped, so the first press always shows something new. Where
    /// each game is up to is kept (SgdbArtAt), so the next press carries on from there another day.
    /// </summary>
    public partial class ThemeApi
    {
        private readonly Dictionary<string, List<string>> sgdbArtLists = new Dictionary<string, List<string>>();   // "id|hero": the pictures' addresses

        private Dictionary<string, int> SgdbArtAt { get { return settings.SgdbArtAt ?? (settings.SgdbArtAt = new Dictionary<string, int>()); } }

        /// <summary>From GameEditRows: the rows (or, with no key, where to add one).</summary>
        private void AddSgdbArtRows(List<PickRow> rows)
        {
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SgdbHeader", "STEAMGRIDDB") });
            if (!HasSgdbKey)
            {
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Text = Loc("LOC_KX_SgdbNoKey", "Add your free SteamGridDB key in Playnite's add-on settings for Kippix (desktop mode) to swap this game's art here") });
                return;
            }
            rows.Add(new PickRow { Key = "sgdb:hero", Icon = "picture", Text = Loc("LOC_KX_SgdbNextBg", "Next background"), Sub = Loc("LOC_KX_SgdbNextBgSub", "Swaps it for the next one on SteamGridDB"), Command = new RelayCommand(_ => SgdbNextPress("hero")) });
            rows.Add(new PickRow { Key = "sgdb:grid", Icon = "gallery", Text = Loc("LOC_KX_SgdbNextCover", "Next cover"), Sub = Loc("LOC_KX_SgdbNextCoverSub", "Swaps it for the next one on SteamGridDB"), Command = new RelayCommand(_ => SgdbNextPress("grid")) });
        }

        private void SgdbNextPress(string kind)
        {
            if (gameEditBusy || !GameEditPressOnce()) return;
            Guid id = gameEditId;
            string rowKey = "sgdb:" + kind;
            PlaySound("select");
            gameEditBusy = true;
            GameEditSub(rowKey, Loc("LOC_KX_EditMetaBusy", "Downloading…"));
            string key = SgdbKey;
            KippixShared.SafeWork.Queue(_ =>
            {
                string result;
                try { result = SgdbNextArt(id, kind, key); }
                catch (Exception ex)
                {
                    logger.Warn("Kippix: SteamGridDB art: " + ex.Message);
                    result = sgdbRefused ? Loc("LOC_KX_SgdbRefused", "SteamGridDB didn't accept the key") : string.Format(Loc("LOC_KX_EditMetaFailed", "It didn't work: {0}"), ex.Message);
                }
                OnUi(() =>
                {
                    gameEditBusy = false;
                    if (gameEditLevel == "edit" && gameEditId == id) GameEditSub(rowKey, result);
                });
            });
        }

        /// <summary>Worker thread. The line for the row.</summary>
        private string SgdbNextArt(Guid id, string kind, string key)
        {
            if (string.IsNullOrEmpty(key)) return Loc("LOC_KX_SgdbNoKeyShort", "No SteamGridDB key");
            var g = api.Database.Games.Get(id);
            if (g == null) return "";
            int sid = SgdbGameIdFor(g, key);
            if (sgdbRefused) return Loc("LOC_KX_SgdbRefused", "SteamGridDB didn't accept the key");
            if (sid == 0) return Loc("LOC_KX_SgdbNotFound", "SteamGridDB doesn't know this game by its name");

            string listKey = id + "|" + kind;
            List<string> urls;
            lock (sgdbArtLists) sgdbArtLists.TryGetValue(listKey, out urls);
            if (urls == null)
            {
                string q = kind == "hero" ? "heroes/game/" + sid + "?types=static&mimes=image/png,image/jpeg&nsfw=false&humor=false"
                                          : "grids/game/" + sid + "?types=static&mimes=image/png,image/jpeg&nsfw=false&humor=false&dimensions=600x900,342x482,660x930";
                var res = SgdbGet(key, q) as Dictionary<string, object>;
                var items = res != null && res.ContainsKey("data") ? res["data"] as object[] : null;
                urls = (items ?? new object[0]).OfType<Dictionary<string, object>>()
                                               .Select(o => o.ContainsKey("url") ? Convert.ToString(o["url"]) : "")
                                               .Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
                lock (sgdbArtLists) sgdbArtLists[listKey] = urls;
            }
            if (urls.Count == 0) return kind == "hero" ? Loc("LOC_KX_SgdbNoBg", "SteamGridDB has no backgrounds for this game") : Loc("LOC_KX_SgdbNoCover", "SteamGridDB has no covers for this game");

            // the picture the game has now (its size, to tell it apart)
            long nowLen = -1;
            try
            {
                string cur = kind == "hero" ? g.BackgroundImage : g.CoverImage;
                if (!string.IsNullOrEmpty(cur) && !cur.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    string full = api.Database.GetFullFilePath(cur);
                    if (File.Exists(full)) nowLen = new FileInfo(full).Length;
                }
            }
            catch (Exception) { }

            int at;
            lock (sgdbArtLists) { if (!SgdbArtAt.TryGetValue(listKey, out at)) at = -1; }
            byte[] data = null;
            string url = null;
            for (int tries = 0; tries < Math.Min(urls.Count, 3) && data == null; tries++)
            {
                at = (at + 1) % urls.Count;
                url = urls[at];
                byte[] b;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "Kippix";
                    b = web.DownloadData(url);
                }
                if (b == null || b.Length == 0 || b.Length > 40 * 1024 * 1024) continue;
                if (b.Length == nowLen && urls.Count > 1) continue;   // (the one it already has)
                data = b;
            }
            if (data == null) return Loc("LOC_KX_EditMetaNothing", "Nothing new was found");

            string ext = ".png";
            try { string e = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant(); if (e == ".jpg" || e == ".jpeg" || e == ".png") ext = e; } catch (Exception) { }
            string temp = Path.Combine(Path.GetTempPath(), "kippix_" + Guid.NewGuid().ToString("N") + ext);
            string fileId;
            try
            {
                File.WriteAllBytes(temp, data);
                fileId = api.Database.AddFile(temp, id);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { } }
            if (string.IsNullOrEmpty(fileId)) return Loc("LOC_KX_EditMetaNothing", "Nothing new was found");

            int shown = at;
            OnUiSync(() =>
            {
                var game = api.Database.Games.Get(id);
                if (game == null) return;
                string old = kind == "hero" ? game.BackgroundImage : game.CoverImage;
                if (kind == "hero") game.BackgroundImage = fileId; else game.CoverImage = fileId;
                api.Database.Games.Update(game);
                // the picture it replaces (Playnite's own file only, and never one that's still in use)
                if (!string.IsNullOrEmpty(old) && !old.StartsWith("http", StringComparison.OrdinalIgnoreCase) && old != game.CoverImage && old != game.BackgroundImage && old != game.Icon)
                    try { api.Database.RemoveFile(old); } catch (Exception) { }
                lock (sgdbArtLists) SgdbArtAt[listKey] = shown;
                SaveSettings();
                if (kind == "hero")
                    try { PreloadSelectedBackground(api.Database.GetFullFilePath(fileId)); } catch (Exception) { }   // (the game page's own copy, and Show wide backgrounds whole)
            });
            return string.Format(Loc("LOC_KX_SgdbShowing", "{0} of {1} from SteamGridDB"), shown + 1, urls.Count);
        }

        /// <summary>The game's id on SteamGridDB (by its Steam app id, else only a clear match by name): 0 when there's none.</summary>
        private int SgdbGameIdFor(Game game, string key)
        {
            int id;
            lock (animLock) { if (sgdbIds.TryGetValue(game.Id, out id) && id != 0) return id; }
            id = 0;
            string steam = game.PluginId == SteamPluginId ? game.GameId : null;
            if (!string.IsNullOrEmpty(steam))
            {
                var r = SgdbGet(key, "games/steam/" + Uri.EscapeDataString(steam)) as Dictionary<string, object>;
                var d = r != null && r.ContainsKey("data") ? r["data"] as Dictionary<string, object> : null;
                if (d != null && d.ContainsKey("id")) id = Convert.ToInt32(d["id"]);
            }
            string name = game.Name ?? "";
            if (id == 0 && name.Length > 0 && !sgdbRefused)
            {
                var r = SgdbGet(key, "search/autocomplete/" + Uri.EscapeDataString(name)) as Dictionary<string, object>;
                var list = r != null && r.ContainsKey("data") ? r["data"] as object[] : null;
                if (list != null)
                    foreach (var o in list.OfType<Dictionary<string, object>>())
                    {
                        string n = o.ContainsKey("name") ? Convert.ToString(o["name"]) : "";
                        if (string.Equals(SimpleName(n), SimpleName(name), StringComparison.OrdinalIgnoreCase) && o.ContainsKey("id")) { id = Convert.ToInt32(o["id"]); break; }
                    }
            }
            if (id != 0) lock (animLock) sgdbIds[game.Id] = id;
            return id;
        }
    }
}

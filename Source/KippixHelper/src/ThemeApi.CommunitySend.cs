using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.4) Before an upload is sent ("communitysend"): the picture Kippix took, more pictures of your own (screenshots,
    /// up to 5 in all: the gallery shows them one after another), a description, then Send.
    /// Extra pictures are saved beside the share as id-2.jpg … id-5.jpg.
    /// </summary>
    public partial class ThemeApi
    {
        private string sendKind, sendFolder, sendId;
        internal const int MaxSharePictures = 5;

        private void OpenSendPage(string kind, string folder, string id)
        {
            sendKind = kind; sendFolder = folder; sendId = id;
            OpenPage("communitysend", "", true);
        }

        internal static List<string> SharePictures(string folder, string id)
        {
            var list = new List<string>();
            string main = Path.Combine(folder, id + ".jpg");
            if (File.Exists(main)) list.Add(main);
            for (int i = 2; i <= MaxSharePictures; i++)
            {
                string p = Path.Combine(folder, id + "-" + i + ".jpg");
                if (File.Exists(p)) list.Add(p);
            }
            return list;
        }

        private Dictionary<string, object> ReadShareEntry()
        {
            try { return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(sendFolder, sendId + ".entry.json"), Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        private void BuildCommunitySendPage()
        {
            if (sendId == null || !File.Exists(Path.Combine(sendFolder, sendId + ".entry.json"))) { SetPageStatus(Loc("LOC_KX_SendGone", "There's nothing waiting to be sent.")); return; }
            var entry = ReadShareEntry() ?? new Dictionary<string, object>();
            string name = entry.ContainsKey("name") ? Convert.ToString(entry["name"]) : sendId;
            string desc = entry.ContainsKey("description") ? Convert.ToString(entry["description"]) : "";
            PageTitle = string.Format(Loc("LOC_KX_SendTitle", "Upload “{0}”"), name);
            PageSub = Loc("LOC_KX_SendSub", "Add more pictures and a few words if you like, then send it. It shows in the gallery once it's approved.");

            PageRows.Add(new PageRow
            {
                Key = "send:go", Icon = "upload", Text = Loc("LOC_KX_SendGo", "Send it to the gallery"),
                Sub = GitHubSignedIn ? string.Format(Loc("LOC_KX_SendAs", "As {0} on GitHub"), settings.GitHubLogin) : Loc("LOC_KX_SendSignIn", "You'll sign in to GitHub first (once)"),
                Command = new RelayCommand(_ =>
                {
                    PlaySound("select");
                    string k = sendKind, f = sendFolder, i = sendId;
                    if (!GitHubSignedIn) { SendShareToGallery(k, f, i); return; }
                    ShowCommunityPage("mine", "u:" + k);
                    StartUpload(k, f, i);
                })
            });

            var pics = SharePictures(sendFolder, sendId);
            AddPageHeader(string.Format(Loc("LOC_KX_SendPictures", "PICTURES ({0} OF {1})"), pics.Count, MaxSharePictures));
            int n = 0;
            foreach (string p in pics)
            {
                n++;
                string file = p;
                bool first = n == 1;
                var row = new PageRow
                {
                    Key = "pic:" + Path.GetFileName(p), Icon = "picture", Wide = true,
                    Text = first ? Loc("LOC_KX_SendPicKippix", "The picture Kippix took") : string.Format(Loc("LOC_KX_SendPicN", "Picture {0}"), n),
                    Sub = first ? Loc("LOC_KX_SendPicKippixSub", "Shown first in the gallery") : Loc("LOC_KX_SendPicRemove", "Choose it to take it out"),
                    Command = first ? null : new RelayCommand(_ => RemoveSharePicture(file))
                };
                PageRows.Add(row);
                int gen = pageGen;
                KippixShared.SafeWork.Queue(__ =>
                {
                    ImageSource img = null;
                    try { img = Images.Load(file, 320); } catch (Exception) { }
                    if (img != null) OnUi(() => { if (gen == pageGen) row.Thumb = img; });
                });
            }
            if (pics.Count < MaxSharePictures)
                PageRows.Add(new PageRow
                {
                    Key = "send:addpic", Icon = "add", Text = Loc("LOC_KX_SendAddPic", "Add a picture…"),
                    Sub = Loc("LOC_KX_SendAddPicSub", "A screenshot of it (JPG or PNG), e.g. from Pictures › PlayniteGame: the overlay's screenshots go there"),
                    Command = new RelayCommand(_ => AddSharePicture())
                });

            AddPageHeader(Loc("LOC_KX_AboutIt", "ABOUT IT"));
            PageRows.Add(new PageRow
            {
                Key = "send:desc", Icon = "note", Text = desc.Length > 0 ? desc : Loc("LOC_KX_SendDescNone", "Add a description"),
                Sub = desc.Length > 0 ? Loc("LOC_KX_SendDescChange", "Choose it to change it") : Loc("LOC_KX_SendDescSub", "A few words about it: what it's like, what it suits"),
                Command = new RelayCommand(_ =>
                {
                    OpenKeyboard(Loc("LOC_KX_SendDescAsk", "About it (a sentence or two)"), desc, typed =>
                    {
                        var e = ReadShareEntry();
                        if (e != null)
                        {
                            e["description"] = (typed ?? "").Trim();
                            try { File.WriteAllText(Path.Combine(sendFolder, sendId + ".entry.json"), new JavaScriptSerializer().Serialize(e), Encoding.UTF8); } catch (Exception) { }
                        }
                        OpenPage("communitysend", "", false);
                    }, null, () => OpenPage("communitysend", "", false), 300);
                })
            });
            NotifyPageLists();
        }

        private void RemoveSharePicture(string file)
        {
            try { File.Delete(file); } catch (Exception) { }
            // keep them numbered 2, 3, 4… without gaps
            var rest = SharePictures(sendFolder, sendId).Skip(1).ToList();
            for (int i = 0; i < rest.Count; i++)
            {
                string want = Path.Combine(sendFolder, sendId + "-" + (i + 2) + ".jpg");
                if (!string.Equals(rest[i], want, StringComparison.OrdinalIgnoreCase)) try { File.Move(rest[i], want); } catch (Exception) { }
            }
            PlaySound("back");
            OpenPage("communitysend", "", false);
        }

        private void AddSharePicture()
        {
            string kind = sendKind, folder = sendFolder, id = sendId;
            if (!CommunityOptionsBackdrop()) return;
            PlaySound("select");
            string shots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PlayniteGame");
            string start = Directory.Exists(shots) ? shots : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            OpenPicker(Loc("LOC_KX_SendAddPic", "Add a picture…"), false, new[] { ".jpg", ".jpeg", ".png" }, start, "community", file =>
            {
                string error = null;
                try
                {
                    int next = SharePictures(folder, id).Count + 1;
                    if (next < 2) next = 2;
                    if (next > MaxSharePictures) throw new InvalidOperationException("five pictures already");
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file);
                    bmp.DecodePixelWidth = 1920;
                    bmp.EndInit();
                    var enc = new JpegBitmapEncoder { QualityLevel = 86 };
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = File.Create(Path.Combine(folder, id + "-" + next + ".jpg"))) enc.Save(fs);
                }
                catch (Exception ex) { error = ex.Message; logger.Warn("Kippix: upload picture: " + ex.Message); }
                ClosePanels();
                sendKind = kind; sendFolder = folder; sendId = id;
                OpenPage("communitysend", "", false);
                if (error != null) SetPageStatus(string.Format(Loc("LOC_KX_SendPicFailed", "That picture couldn't be added ({0})."), error));
            }, () =>
            {
                ClosePanels();
                sendKind = kind; sendFolder = folder; sendId = id;
                OpenPage("communitysend", "", false);
            });
        }
    }
}

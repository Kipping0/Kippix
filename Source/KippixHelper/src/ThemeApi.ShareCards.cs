using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Share cards: a 1920×1080 picture of your profile, your year or one game (the game's art blurred and dimmed behind,
    /// your picture or the cover, your name, the headline numbers), saved as a PNG in Pictures\Kippix.
    /// From the profile's Overview and Your year (the Share card button) and a game's More menu (Share card).
    /// Everything is read off the UI thread; only the drawing (one quick pass) is on it, and the file is written off it again.
    /// Profile tabs › SHARE CARDS: open the folder afterwards.
    /// </summary>
    public partial class ThemeApi
    {
        private class ShareCardData
        {
            public string Kicker = "", Title = "", Sub = "", ShelfTitle = "", FileName = "card", Footer = "";
            public ImageSource Picture;          // the cover (game) or your picture (profile, year)
            public string PictureLetter = "";    // no picture of you: your initial in a circle
            public bool RoundPicture;
            public ImageSource Background;       // small and already blurred (BlurSmall)
            public List<string[]> Stats = new List<string[]>();   // { value, label }
            public List<Tuple<ImageSource, string, string>> Shelf = new List<Tuple<ImageSource, string, string>>();   // cover, name, line
            public double Progress = -1;         // 0..1: a bar under the numbers (achievements)
            public string ProgressText = "";
        }

        private bool shareBusy;

        private static string CardFolder
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Kippix"); }
        }

        // ---------------- where they're asked for ----------------

        /// <summary>The game More menu's row.</summary>
        private void AddShareCardRow(Game g)
        {
            if (g == null) return;
            var game = g;
            GameMoreRows.Add(new PickRow
            {
                Text = Loc("LOC_KX_SCardCard", "Share card"), Icon = "picture", Key = "share",
                Sub = Loc("LOC_KX_SCardCardGameSub", "A picture of this game and your numbers for it, saved to Pictures\\Kippix"),
                Command = new RelayCommand(_ => { ClosePanels(); ShareGameCard(game.Id); })
            });
        }

        /// <summary>The Share card button on the profile's Overview and Your year (year 0: the profile).</summary>
        private void AddShareChip(int year)
        {
            if (!HubSectionShown("share")) return;
            int y = year;
            AddPageChip(Loc("LOC_KX_SCardCard", "Share card"), "picture", () => { if (y > 0) ShareYearCard(y); else ShareProfileCard(); });
        }

        private bool StartShare()
        {
            if (shareBusy) { PlaySound("back"); return false; }
            shareBusy = true;
            PlaySound("select");
            ShowCard(Loc("LOC_KX_SCardHeader", "SHARE CARD"), Loc("LOC_KX_SCardMaking", "Making your card…"), "", "", null);
            return true;
        }

        // ---------------- the three cards ----------------

        private void ShareGameCard(Guid id)
        {
            Game g = null;
            try { g = api.Database.Games.Get(id); } catch (Exception) { }
            if (g == null || !StartShare()) return;
            var game = g;
            var achs = achData;
            string who = ProfileName;
            var me = ProfilePicture;
            string letter = ProfileInitial;
            KippixShared.SafeWork.Queue(_ =>
            {
                var d = new ShareCardData { Kicker = Loc("LOC_KX_SCardKickerGame", "NOW IN MY LIBRARY"), Title = game.Name ?? "", FileName = game.Name ?? "game" };
                try
                {
                    var bits = new List<string>();
                    if (game.ReleaseDate.HasValue) bits.Add(game.ReleaseDate.Value.Year.ToString(CultureInfo.InvariantCulture));
                    if (game.Source != null && !string.IsNullOrEmpty(game.Source.Name)) bits.Add(game.Source.Name);
                    if (game.Developers != null && game.Developers.Count > 0 && game.Developers[0] != null) bits.Add(game.Developers[0].Name);
                    d.Sub = string.Join("   ·   ", bits.ToArray());
                    d.Picture = Images.LoadUncached(FullArt(game.CoverImage), 600);
                    d.Background = BlurSmall(Images.LoadUncached(FullArt(!string.IsNullOrEmpty(game.BackgroundImage) ? game.BackgroundImage : game.CoverImage), 360));
                    d.Stats.Add(new[] { game.Playtime > 0 ? HoursShort(game.Playtime / 3600.0) : "—", Loc("LOC_KX_SCardPlayed", "played") });
                    if (game.LastActivity.HasValue) d.Stats.Add(new[] { game.LastActivity.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture), Loc("LOC_KX_SCardLastPlayed", "last played") });
                    var a = achs != null ? achs.FirstOrDefault(x => x != null && x.Id == game.Id) : null;
                    if (a != null && a.Items != null && a.Items.Count > 0)
                    {
                        int un = a.Items.Count(i => i != null && i.Unlocked);
                        double f = (double)un / a.Items.Count;
                        d.Stats.Add(new[] { Math.Floor(f * 100) + "%", string.Format(Loc("LOC_KX_SCardAchOf", "achievements · {0} of {1}"), un, a.Items.Count) });
                        d.Progress = f;
                    }
                    var done = CompletionOf(game.Id);
                    string status = null;
                    try { if (game.CompletionStatus != null) status = game.CompletionStatus.Name; } catch (Exception) { }
                    if (done != null)
                        d.Stats.Add(new[] { CompletionKindName(done.Kind), done.At.HasValue ? done.At.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : (status ?? Loc("LOC_KX_SCardStatus", "status")) });
                    else if (!string.IsNullOrEmpty(status)) d.Stats.Add(new[] { status, Loc("LOC_KX_SCardStatus", "status") });
                    var h = KippixOverlay.GameData.LoadHltb(api.Paths.ExtensionsDataPath, game.Id);
                    if (h != null && (h.Story > 0 || h.Extras > 0))
                    {
                        double main = h.Story > 0 ? h.Story : h.Extras;
                        double you = game.Playtime / 3600.0;
                        d.Stats.Add(new[] { HoursShort(main), you > 0 ? string.Format(Loc("LOC_KX_SCardHltbYou", "main story (HowLongToBeat) · you {0}%"), Math.Round(Math.Min(999, 100 * you / main)))
                                                                       : Loc("LOC_KX_SCardHltb", "main story (HowLongToBeat)") });
                    }
                    d.Footer = who;
                }
                catch (Exception ex) { logger.Warn("Kippix: share card (game): " + ex.Message); }
                OnUi(() => FinishShare(d, me, letter));
            });
        }

        private void ShareProfileCard()
        {
            if (!StartShare()) return;
            var stats = (hubStats ?? new List<PageStat>()).ToList();
            var banner = hubBanner as BitmapSource;
            string who = ProfileName, kind = HubKindText ?? "";
            var me = ProfilePicture;
            string letter = ProfileInitial;
            bool ownerView = !ProfilesOn || IsOwnerProfile;
            string profileId = CurrentProfileId;
            int year = DateTime.Now.Year;
            KippixShared.SafeWork.Queue(_ =>
            {
                var d = new ShareCardData { Kicker = Loc("LOC_KX_SCardKickerProfile", "MY GAMING PROFILE"), Title = who, Sub = kind, FileName = who, RoundPicture = true };
                try
                {
                    d.Background = BlurSmall(banner);
                    foreach (var s in stats.Take(5)) d.Stats.Add(new[] { s.Value, s.Label });
                    var f = FinishedIn(year, ownerView);
                    if (f.Beaten + f.Hundred > 0) d.Stats.Add(new[] { (f.Beaten + f.Hundred).ToString(CultureInfo.CurrentCulture), string.Format(Loc("LOC_KX_SCardFinishedIn", "finished in {0}"), year) });
                    string src;
                    var sessions = SessionsForProfile(out src, DateTime.Now.DayOfYear + 2, ownerView, profileId).Where(s => s != null && s.Start.Year == year && s.Seconds > 0).ToList();
                    AddTopGames(d, sessions, string.Format(Loc("LOC_KX_SCardTopYear", "MOST PLAYED IN {0}"), year));
                }
                catch (Exception ex) { logger.Warn("Kippix: share card (profile): " + ex.Message); }
                OnUi(() => FinishShare(d, me, letter));
            });
        }

        private void ShareYearCard(int year)
        {
            if (!StartShare()) return;
            string who = ProfileName;
            var me = ProfilePicture;
            string letter = ProfileInitial;
            bool ownerView = !ProfilesOn || IsOwnerProfile;
            string profileId = CurrentProfileId;
            var achs = ownerView ? achData : null;
            KippixShared.SafeWork.Queue(_ =>
            {
                var d = new ShareCardData { Kicker = Loc("LOC_KX_SCardKickerYear", "MY YEAR IN GAMES"), Title = string.Format(Loc("LOC_KX_YourYear", "Your {0}"), year), Sub = who, FileName = who + " " + year, RoundPicture = true };
                try
                {
                    string src;
                    int days = (int)(DateTime.Now - new DateTime(year, 1, 1)).TotalDays + 2;
                    var sessions = SessionsForProfile(out src, Math.Max(days, 30), ownerView, profileId).Where(s => s != null && s.Start.Year == year && s.Seconds > 0).ToList();
                    d.Stats.Add(new[] { HoursShort(sessions.Sum(s => s.Seconds) / 3600.0), Loc("LOC_KX_StatPlayed", "played") });
                    d.Stats.Add(new[] { sessions.Select(s => s.GameId ?? s.Name).Distinct().Count().ToString(CultureInfo.CurrentCulture), Loc("LOC_KX_StatGames", "games") });
                    var real = RealPlayDays(sessions, new DateTime(year, 1, 1), new DateTime(year, 12, 31));
                    if (real.Count > 0) d.Stats.Add(new[] { real.Count.ToString(CultureInfo.CurrentCulture), Loc("LOC_KX_SCardDaysPlayed", "days played") });
                    if (achs != null && achs.Count > 0)
                    {
                        int unlocked = 0;
                        foreach (var a in achs.ToList())
                            if (a != null && a.Items != null)
                                unlocked += a.Items.Count(i => i != null && i.Unlocked && i.Date.HasValue && i.Date.Value.Year == year);
                        d.Stats.Add(new[] { unlocked.ToString("N0", CultureInfo.CurrentCulture), Loc("LOC_KX_StatAchievements", "achievements") });
                    }
                    var f = FinishedIn(year, ownerView);
                    if (f.Beaten + f.Hundred > 0) d.Stats.Add(new[] { (f.Beaten + f.Hundred).ToString(CultureInfo.CurrentCulture), Loc("LOC_KX_SCardFinished", "games finished") });
                    var top = AddTopGames(d, sessions, Loc("LOC_KX_TopGames", "YOUR TOP GAMES"));
                    if (top != null)
                    {
                        Game g = null;
                        try { g = api.Database.Games.Get(top.Value); } catch (Exception) { }
                        if (g != null) d.Background = BlurSmall(Images.LoadUncached(FullArt(!string.IsNullOrEmpty(g.BackgroundImage) ? g.BackgroundImage : g.CoverImage), 360));
                    }
                }
                catch (Exception ex) { logger.Warn("Kippix: share card (year): " + ex.Message); }
                OnUi(() => FinishShare(d, me, letter));
            });
        }

        /// <summary>(worker) The five most played games of these sessions as the card's shelf. Returns the top game's id.</summary>
        private Guid? AddTopGames(ShareCardData d, List<PlaySession> sessions, string title)
        {
            var top = sessions.GroupBy(s => s.GameId ?? s.Name).Select(x => new { Key = x.Key, Name = x.First().Name, Sec = x.Sum(s => s.Seconds) })
                              .OrderByDescending(x => x.Sec).Take(5).ToList();
            if (top.Count == 0) return null;
            d.ShelfTitle = title;
            Guid? first = null;
            foreach (var t in top)
            {
                Guid id;
                ImageSource cover = null;
                if (Guid.TryParse(t.Key ?? "", out id))
                {
                    if (first == null) first = id;
                    try { cover = Images.LoadUncached(CoverOf(id), 300); } catch (Exception) { }
                }
                d.Shelf.Add(Tuple.Create(cover, t.Name ?? "", HoursShort(t.Sec / 3600.0)));
            }
            return first;
        }

        private string FullArt(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
            try { return api.Database.GetFullFilePath(path); } catch (Exception) { return null; }
        }

        /// <summary>(any thread) A frozen picture made small and blurred (three box passes on ~320 px): it's stretched to the card behind everything.</summary>
        private static BitmapSource BlurSmall(BitmapSource src)
        {
            try
            {
                if (src == null) return null;
                if (!src.IsFrozen) return null;
                double scale = Math.Min(1.0, 320.0 / Math.Max(1, src.PixelWidth));
                BitmapSource small = scale < 1 ? (BitmapSource)new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
                var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                if (w < 2 || h < 2) return null;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);
                var tmp = new byte[px.Length];
                for (int pass = 0; pass < 3; pass++)
                {
                    BoxBlur(px, tmp, w, h, 7, true);
                    BoxBlur(tmp, px, w, h, 7, false);
                }
                var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
                outBmp.Freeze();
                return outBmp;
            }
            catch (Exception) { return null; }
        }

        private static BitmapSource BlurSmall(ImageSource src) { return BlurSmall(src as BitmapSource); }

        private static void BoxBlur(byte[] from, byte[] to, int w, int h, int r, bool horizontal)
        {
            int lines = horizontal ? h : w, len = horizontal ? w : h;
            for (int line = 0; line < lines; line++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, count = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int k = Math.Max(0, Math.Min(len - 1, i));
                        sum += from[Index(horizontal, line, k, w) + c];
                        count++;
                    }
                    for (int i = 0; i < len; i++)
                    {
                        to[Index(horizontal, line, i, w) + c] = (byte)(sum / count);
                        int add = Math.Min(len - 1, i + r + 1), drop = Math.Max(0, i - r);
                        sum += from[Index(horizontal, line, add, w) + c] - from[Index(horizontal, line, drop, w) + c];
                    }
                }
            }
        }

        private static int Index(bool horizontal, int line, int i, int w)
        {
            return horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
        }

        // ---------------- drawing and saving ----------------

        private void FinishShare(ShareCardData d, ImageSource me, string letter)
        {
            BitmapSource bmp = null;
            try
            {
                if (d.RoundPicture) { d.Picture = me; d.PictureLetter = letter ?? ""; }
                var card = BuildShareVisual(d, me, letter);
                card.Measure(new Size(1920, 1080));
                card.Arrange(new Rect(0, 0, 1920, 1080));
                card.UpdateLayout();
                var rtb = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(card);
                if (rtb.CanFreeze) { rtb.Freeze(); bmp = rtb; }
                else { var copy = new WriteableBitmap(rtb); copy.Freeze(); bmp = copy; }   // (handed to a worker to encode)
            }
            catch (Exception ex) { logger.Warn("Kippix: drawing a share card: " + ex.Message); }
            if (bmp == null)
            {
                shareBusy = false;
                ShowCard(Loc("LOC_KX_SCardHeader", "SHARE CARD"), Loc("LOC_KX_SCardFailed", "Couldn't make the card"), "", "", null);
                return;
            }
            string name = CardFileName(d.FileName);
            var thumb = d.RoundPicture ? me : d.Picture;
            KippixShared.SafeWork.Queue(_ =>
            {
                string file = null, error = null;
                try
                {
                    Directory.CreateDirectory(CardFolder);
                    file = System.IO.Path.Combine(CardFolder, "Kippix - " + name + " - " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    string tmp = file + ".tmp";
                    using (var fs = File.Create(tmp)) enc.Save(fs);
                    File.Move(tmp, file);
                    logger.Info("Kippix: share card saved: " + file);
                }
                catch (Exception ex) { error = ex.Message; logger.Warn("Kippix: saving a share card: " + ex.Message); }
                OnUi(() =>
                {
                    shareBusy = false;
                    if (error != null) { ShowCard(Loc("LOC_KX_SCardHeader", "SHARE CARD"), Loc("LOC_KX_SCardFailed", "Couldn't make the card"), error, "", null); return; }
                    ShowCard(Loc("LOC_KX_SCardHeader", "SHARE CARD"), Loc("LOC_KX_SCardSaved", "Saved to Pictures\\Kippix"), System.IO.Path.GetFileName(file), "", thumb);
                    if (settings.ShareOpenFolder)
                        try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception) { }
                });
            });
        }

        private static string CardFileName(string s)
        {
            s = (s ?? "").Trim();
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            return s.Length > 0 ? s : "card";
        }

        private static SolidColorBrush CardBrush(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        private static TextBlock CardText(string text, double size, Brush brush, FontWeight weight, double maxWidth)
        {
            return new TextBlock
            {
                Text = text ?? "", FontSize = size, Foreground = brush, FontWeight = weight, MaxWidth = maxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
            };
        }

        /// <summary>The card, built in code: art behind, then the picture, the name and the numbers, a shelf of covers, and a footer.</summary>
        private FrameworkElement BuildShareVisual(ShareCardData d, ImageSource me, string letter)
        {
            Color accent = AccentColorNow();
            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();
            var white = Brushes.White;
            var soft = CardBrush(0xCC, 0xFF, 0xFF, 0xFF);
            var dim = CardBrush(0x99, 0xFF, 0xFF, 0xFF);

            var root = new Grid { Width = 1920, Height = 1080, Background = CardBrush(0xFF, 0x0A, 0x0B, 0x0E), ClipToBounds = true };
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                if (w != null) TextElement.SetFontFamily(root, w.FontFamily);
            }
            catch (Exception) { }
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
            RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);

            if (d.Background != null)
                root.Children.Add(new Image { Source = d.Background, Stretch = Stretch.UniformToFill, Opacity = 0.75 });
            var side = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            side.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0x0A, 0x0B, 0x0E), 0));
            side.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0x0A, 0x0B, 0x0E), 0.55));
            side.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0x0A, 0x0B, 0x0E), 1));
            side.Freeze();
            root.Children.Add(new Rectangle { Fill = side });
            var low = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            low.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x0A, 0x0B, 0x0E), 0.55));
            low.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, 0x0A, 0x0B, 0x0E), 1));
            low.Freeze();
            root.Children.Add(new Rectangle { Fill = low });
            var glow = new RadialGradientBrush { Center = new Point(0.1, 0.05), GradientOrigin = new Point(0.1, 0.05), RadiusX = 0.6, RadiusY = 0.8 };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, accent.R, accent.G, accent.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));
            glow.Freeze();
            root.Children.Add(new Rectangle { Fill = glow });
            // a thin accent line down the left edge: Kippix's mark
            root.Children.Add(new Rectangle { Width = 10, HorizontalAlignment = HorizontalAlignment.Left, Fill = accentBrush });

            var canvas = new Canvas();
            root.Children.Add(canvas);
            double textX;
            bool round = d.RoundPicture;
            if (round)
            {
                const double size = 240;
                var pic = new Grid { Width = size, Height = size };
                pic.Children.Add(new Ellipse { Fill = CardBrush(0x33, 0xFF, 0xFF, 0xFF) });
                if (d.Picture != null)
                {
                    var b = new ImageBrush(d.Picture) { Stretch = Stretch.UniformToFill };
                    if (b.CanFreeze) b.Freeze();
                    pic.Children.Add(new Ellipse { Fill = b });
                }
                else
                {
                    var l = CardText(d.PictureLetter, 110, white, FontWeights.SemiBold, size);
                    l.HorizontalAlignment = HorizontalAlignment.Center;
                    l.VerticalAlignment = VerticalAlignment.Center;
                    pic.Children.Add(l);
                }
                pic.Children.Add(new Ellipse { Stroke = accentBrush, StrokeThickness = 5 });
                Canvas.SetLeft(pic, 130);
                Canvas.SetTop(pic, 150);
                canvas.Children.Add(pic);
                textX = 130 + size + 56;
            }
            else
            {
                var cover = new Border { Width = 400, Height = 600, CornerRadius = new CornerRadius(18), Background = CardBrush(0xFF, 0x1E, 0x22, 0x29), BorderBrush = CardBrush(0x40, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(2) };
                if (d.Picture != null)
                {
                    var b = new ImageBrush(d.Picture) { Stretch = Stretch.UniformToFill };
                    if (b.CanFreeze) b.Freeze();
                    cover.Background = b;
                }
                else cover.Child = new TextBlock { Text = d.Title, FontSize = 40, Foreground = white, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
                Canvas.SetLeft(cover, 130);
                Canvas.SetTop(cover, 240);
                canvas.Children.Add(cover);
                textX = 130 + 400 + 70;
            }
            double textW = 1920 - textX - 130;

            var kicker = CardText(d.Kicker, 26, accentBrush, FontWeights.Bold, textW);
            Canvas.SetLeft(kicker, textX);
            Canvas.SetTop(kicker, round ? 175 : 240);
            canvas.Children.Add(kicker);
            var title = new TextBlock { Text = d.Title, FontSize = round ? 96 : 84, Foreground = white, FontWeight = FontWeights.SemiBold, MaxWidth = textW, TextWrapping = TextWrapping.Wrap, MaxHeight = round ? 130 : 210, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = round ? 112 : 98 };
            Canvas.SetLeft(title, textX - 4);
            Canvas.SetTop(title, round ? 210 : 280);
            canvas.Children.Add(title);
            title.Measure(new Size(textW, double.PositiveInfinity));
            double y = (round ? 210 : 280) + Math.Min(title.DesiredSize.Height, round ? 130 : 210) + 8;
            if (!string.IsNullOrEmpty(d.Sub))
            {
                var sub = CardText(d.Sub, 30, soft, FontWeights.Normal, textW);
                Canvas.SetLeft(sub, textX);
                Canvas.SetTop(sub, y);
                canvas.Children.Add(sub);
                y += 50;
            }

            // the numbers: tiles in a row (wrapping), from under the picture on the profile cards, beside the cover on a game's
            double statsX = round ? 130 : textX;
            double statsY = round ? Math.Max(450, y + 50) : Math.Max(y + 46, 520);
            double statsW = 1920 - statsX - 130;
            var wrap = new WrapPanel { Width = statsW };
            int n = Math.Max(1, d.Stats.Count);
            double tileW = round ? Math.Min(300, (statsW - 20 * (Math.Min(n, 6) - 1)) / Math.Min(n, 6)) : (statsW - 40) / 3;
            foreach (var s in d.Stats.Take(6))
            {
                var tile = new Border { Width = tileW, Height = 150, CornerRadius = new CornerRadius(18), Background = CardBrush(0x26, 0xFF, 0xFF, 0xFF), Margin = new Thickness(0, 0, 20, 20), Padding = new Thickness(26, 18, 22, 14) };
                var sp = new StackPanel();
                sp.Children.Add(CardText(s[0], 54, white, FontWeights.SemiBold, tileW - 48));
                sp.Children.Add(CardText(s[1], 23, dim, FontWeights.Normal, tileW - 48));
                tile.Child = sp;
                wrap.Children.Add(tile);
            }
            Canvas.SetLeft(wrap, statsX);
            Canvas.SetTop(wrap, statsY);
            canvas.Children.Add(wrap);
            wrap.Measure(new Size(statsW, double.PositiveInfinity));
            double afterStats = statsY + wrap.DesiredSize.Height;

            if (d.Progress >= 0)
            {
                var bar = new Grid { Width = Math.Min(statsW - 20, 900), Height = 12 };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = CardBrush(0x33, 0xFF, 0xFF, 0xFF) });
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = accentBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(12, bar.Width * Math.Min(1, d.Progress)) });
                Canvas.SetLeft(bar, statsX);
                Canvas.SetTop(bar, afterStats + 4);
                canvas.Children.Add(bar);
                afterStats += 40;
            }

            if (d.Shelf.Count > 0)
            {
                double shelfY = Math.Max(afterStats + 20, 640);
                var head = CardText(d.ShelfTitle, 24, dim, FontWeights.Bold, 1600);
                Canvas.SetLeft(head, 130);
                Canvas.SetTop(head, shelfY);
                canvas.Children.Add(head);
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                double coverH = Math.Max(150, Math.Min(260, 1080 - 110 - (shelfY + 44) - 62)), coverW = coverH * 2 / 3;
                foreach (var t in d.Shelf)
                {
                    var item = new StackPanel { Width = coverW + 40, Margin = new Thickness(0, 0, 10, 0) };
                    var c = new Border { Width = coverW, Height = coverH, CornerRadius = new CornerRadius(12), Background = CardBrush(0xFF, 0x1E, 0x22, 0x29), HorizontalAlignment = HorizontalAlignment.Left };
                    if (t.Item1 != null)
                    {
                        var b = new ImageBrush(t.Item1) { Stretch = Stretch.UniformToFill };
                        if (b.CanFreeze) b.Freeze();
                        c.Background = b;
                    }
                    else c.Child = new TextBlock { Text = t.Item2, FontSize = 20, Foreground = white, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
                    item.Children.Add(c);
                    item.Children.Add(CardText(t.Item2, 21, white, FontWeights.Normal, coverW + 30));
                    item.Children.Add(CardText(t.Item3, 19, dim, FontWeights.Normal, coverW + 30));
                    row.Children.Add(item);
                }
                Canvas.SetLeft(row, 130);
                Canvas.SetTop(row, shelfY + 44);
                canvas.Children.Add(row);
            }

            // footer: who made it (a game's card: your picture and name), Kippix and the date
            var foot = new StackPanel { Orientation = Orientation.Horizontal };
            if (!round && !string.IsNullOrEmpty(d.Footer))
            {
                var av = new Grid { Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) };
                av.Children.Add(new Ellipse { Fill = CardBrush(0x33, 0xFF, 0xFF, 0xFF) });
                if (me != null)
                {
                    var b = new ImageBrush(me) { Stretch = Stretch.UniformToFill };
                    if (b.CanFreeze) b.Freeze();
                    av.Children.Add(new Ellipse { Fill = b });
                }
                else
                {
                    var l = CardText(letter, 24, white, FontWeights.SemiBold, 48);
                    l.HorizontalAlignment = HorizontalAlignment.Center;
                    l.VerticalAlignment = VerticalAlignment.Center;
                    av.Children.Add(l);
                }
                foot.Children.Add(av);
                var fn = CardText(d.Footer, 26, soft, FontWeights.SemiBold, 600);
                fn.VerticalAlignment = VerticalAlignment.Center;
                fn.Margin = new Thickness(0, 0, 28, 0);
                foot.Children.Add(fn);
            }
            var mark = CardText("Kippix   ·   " + DateTime.Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture), 22, dim, FontWeights.Normal, 900);
            mark.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(mark);
            Canvas.SetLeft(foot, 130);
            Canvas.SetTop(foot, 1080 - 96);
            canvas.Children.Add(foot);
            return root;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace KippixHelper
{
    /// <summary>
    /// (1.0.1) Shows &amp; movies' own player: the video full screen inside Kippix (a code-built layer over PART_MainHost, the page
    /// stays underneath), with the controller. Takes every key and button while it's up (like Move the picture):
    ///   A / Space play-pause · D-pad left / right 10 s · LT / RT a minute · D-pad up / down volume · LB / RB the episode before / after
    ///   · X subtitles (a .srt beside the video) · Y from the start · B / Esc close.
    /// Carries on where you stopped (localmedia.json Pos; not in the first 30 s or the last 2 minutes), counts as watched at 90 %,
    /// and a show's next episode starts by itself 8 s after one ends (A now, B stay). The screen doesn't go to sleep while it plays.
    /// Uses Windows' own video playback (WPF MediaElement): what Windows can't play here (some MKV / HEVC files without the codec)
    /// opens in Windows' player for it instead, with a line on the page saying so. The theme's music stays quiet meanwhile.
    /// </summary>
    public partial class ThemeApi
    {
        private bool lpOpen;
        private Grid lpLayer;
        private MediaElement lpMedia;
        private string lpFile, lpShow;
        private List<string> lpList;
        private bool lpPlaying, lpReady, lpCountedWatched, lpControlsShown = true;
        private double lpResumeFrom;
        private DateTime lpOpenedAt, lpLastInput, lpPadAAt = DateTime.MinValue, lpPadBAt = DateTime.MinValue, lpNextAt = DateTime.MinValue;
        private DispatcherTimer lpTick;
        private FrameworkElement lpControls;
        private TextBlock lpTitle, lpLine, lpTime, lpCentre, lpSubText, lpNote;
        private Border lpTrack, lpFill, lpSubBox;
        private ObservableCollection<HintGroup> lpHints;
        private string lpHintsSig;
        private List<Tuple<double, double, string>> lpCues;
        private string lpNoteText;
        private DateTime lpNoteUntil = DateTime.MinValue;

        private bool LocalPlayerActive { get { return lpOpen && lpPlaying && lpMedia != null && lpMedia.Volume > 0; } }

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

        /// <summary>Opens the player on <paramref name="file"/> (<paramref name="show"/>: its show's folder, or null). False: it couldn't be put up.</summary>
        private bool OpenLocalPlayer(string file, string show)
        {
            if (!IsFullscreen) return false;
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var host = win != null ? UiTools.FindByName(win, "PART_MainHost") as Panel : null;
            if (host == null) return false;
            if (lpOpen) CloseLocalPlayer(false);
            try
            {
                lpFile = file;
                lpShow = show;
                var item = show != null && localItems != null ? localItems.FirstOrDefault(i => i.IsShow && string.Equals(i.Path, show, StringComparison.OrdinalIgnoreCase)) : null;
                lpList = item != null ? item.Episodes.Select(e => e.File).ToList() : null;
                LpBuild(host);
                LpFill();
                lpOpen = true;
                lpOpenedAt = DateTime.Now;
                lpLastInput = DateTime.Now;
                lpNextAt = DateTime.MinValue;
                try { SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired); } catch (Exception) { }
                if (lpTick == null)
                {
                    lpTick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
                    lpTick.Tick += (s, e) => { try { LpTick(); } catch (Exception ex) { logger.Warn("Kippix: player: " + ex.Message); } };
                }
                lpTick.Start();
                logger.Info("Kippix: player: " + Path.GetFileName(file));
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Kippix: player: couldn't open " + file + ": " + ex.Message);
                LpRemoveLayer();
                lpOpen = false;
                return false;
            }
        }

        /// <summary>Puts the current file in the player (a new episode reuses the layer).</summary>
        private void LpFill()
        {
            lpReady = false;
            lpPlaying = false;
            lpCountedWatched = false;
            lpNextAt = DateTime.MinValue;
            LoadLocalState();
            double saved;
            lpResumeFrom = localState.Pos.TryGetValue(lpFile, out saved) ? saved : 0;
            if (lpShow != null) localState.Last[lpShow] = lpFile;
            lpCues = LpReadSubs(lpFile);

            // the words: the show and episode, or the movie
            string title = Path.GetFileNameWithoutExtension(lpFile), line = "";
            var show = lpShow != null && localItems != null ? localItems.FirstOrDefault(i => i.IsShow && string.Equals(i.Path, lpShow, StringComparison.OrdinalIgnoreCase)) : null;
            var ep = show != null ? show.Episodes.FirstOrDefault(e => string.Equals(e.File, lpFile, StringComparison.OrdinalIgnoreCase)) : null;
            var movie = show == null && localItems != null ? localItems.FirstOrDefault(i => !i.IsShow && string.Equals(i.Path, lpFile, StringComparison.OrdinalIgnoreCase)) : null;
            if (show != null) { title = show.Name; line = ep != null ? EpisodeLabel(ep) + (ep.Title.Length > 0 ? "  ·  " + ep.Title : "") : Path.GetFileNameWithoutExtension(lpFile); }
            else if (movie != null) { title = movie.Name; line = movie.Year ?? ""; }
            lpTitle.Text = title;
            lpLine.Text = line;
            lpLine.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            lpCentre.Text = Loc("LOC_KX_PlayerLoading", "Loading…");
            lpCentre.Visibility = Visibility.Visible;
            lpSubBox.Visibility = Visibility.Collapsed;
            LpShowControls();

            try { lpMedia.Stop(); } catch (Exception) { }
            lpMedia.Source = new Uri(lpFile);
            lpMedia.Play();   // (opens it; MediaOpened seeks to where you stopped)
            LpDraw();
        }

        private void LpBuild(Panel host)
        {
            LpRemoveLayer();
            var layer = new Grid { Background = Brushes.Black, Focusable = false, ClipToBounds = true };
            Panel.SetZIndex(layer, 147);   // over everything (the page stays underneath), under the intro (150)
            Grid.SetRowSpan(layer, 99);
            Grid.SetColumnSpan(layer, 99);

            var media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, ScrubbingEnabled = true,
                Volume = Math.Max(0, Math.Min(1, settings.LocalMediaVolume)), IsHitTestVisible = false
            };
            media.MediaOpened += (s, e) => { try { LpOpened(); } catch (Exception ex) { logger.Warn("Kippix: player: " + ex.Message); } };
            media.MediaEnded += (s, e) => { try { LpEnded(); } catch (Exception ex) { logger.Warn("Kippix: player: " + ex.Message); } };
            media.MediaFailed += (s, e) => { try { LpFailed(e.ErrorException); } catch (Exception ex) { logger.Warn("Kippix: player: " + ex.Message); } };
            layer.Children.Add(media);

            // subtitles
            lpSubText = new TextBlock { FontSize = 40, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 1500,
                                        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 1 } };
            lpSubBox = new Border { Child = lpSubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(60, 0, 60, 80),
                                    Padding = new Thickness(18, 6, 18, 8), CornerRadius = new CornerRadius(8), Background = Frozen(Color.FromArgb(0x66, 0, 0, 0)), Visibility = Visibility.Collapsed };
            layer.Children.Add(lpSubBox);

            // Loading… / Paused, in the middle
            lpCentre = new TextBlock { FontSize = 34, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                                       Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 } };
            layer.Children.Add(lpCentre);

            // a line at the top (carrying on from …, next episode in …)
            lpNote = new TextBlock { FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 48, 0, 0) };
            var noteBox = new Border { Child = lpNote, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), Padding = new Thickness(22, 10, 22, 12),
                                       CornerRadius = new CornerRadius(18), Background = Frozen(Color.FromArgb(0xE6, 0x11, 0x13, 0x17)), Visibility = Visibility.Collapsed };
            lpNote.Margin = new Thickness(0);
            lpNote.Tag = noteBox;
            layer.Children.Add(noteBox);

            // the controls: the title, the bar, the time and the buttons, on a shade at the bottom
            var controls = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            controls.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0xE0, 0, 0, 0), 0.55) }, 90);
            var stack = new StackPanel { Margin = new Thickness(80, 120, 80, 36) };
            lpTitle = new TextBlock { FontSize = 34, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            lpLine = new TextBlock { FontSize = 20, Foreground = Frozen(Color.FromArgb(0xCC, 0xF4, 0xF6, 0xFA)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(lpTitle);
            stack.Children.Add(lpLine);
            var barRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lpTrack = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
            lpFill = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Grid();
            bar.Children.Add(lpTrack);
            bar.Children.Add(lpFill);
            barRow.Children.Add(bar);
            lpTime = new TextBlock { FontSize = 20, Foreground = Brushes.White, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lpTime, 1);
            barRow.Children.Add(lpTime);
            stack.Children.Add(barRow);
            lpHints = new ObservableCollection<HintGroup>();
            lpHintsSig = null;
            var hints = new ItemsControl { Focusable = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), ItemsSource = lpHints };
            hints.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            stack.Children.Add(hints);
            controls.Children.Add(stack);
            layer.Children.Add(controls);

            host.Children.Add(layer);
            lpLayer = layer;
            lpMedia = media;
            lpControls = controls;
            lpControlsShown = true;
            layer.SizeChanged += (s, e) => { try { LpDraw(); } catch (Exception) { } };

            // the theme's fonts, accent and hint buttons (found now it's in the window)
            var bold = layer.TryFindResource("FontTitilliumWebBold") as FontFamily;
            var font = layer.TryFindResource("FontTitilliumWeb") as FontFamily;
            if (bold != null) { lpTitle.FontFamily = bold; lpSubText.FontFamily = bold; lpCentre.FontFamily = bold; }
            if (font != null) { lpLine.FontFamily = font; lpTime.FontFamily = font; lpNote.FontFamily = font; }
            lpFill.Background = layer.TryFindResource("KX_AccentBrush") as Brush ?? Brushes.White;
            var tpl = layer.TryFindResource("KX_EditHintGroupTemplate") as DataTemplate;
            if (tpl != null) hints.ItemTemplate = tpl; else hints.Visibility = Visibility.Collapsed;
        }

        private void LpRemoveLayer()
        {
            if (lpMedia != null) { try { lpMedia.Stop(); lpMedia.Close(); lpMedia.Source = null; } catch (Exception) { } }
            if (lpLayer != null)
            {
                var parent = VisualTreeHelper.GetParent(lpLayer) as Panel;
                if (parent != null) parent.Children.Remove(lpLayer);
            }
            lpLayer = null;
            lpMedia = null;
        }

        private void LpOpened()
        {
            lpReady = true;
            double len = LpLength();
            // carry on where you stopped (not right at the start or the end)
            if (lpResumeFrom > 30 && (len <= 0 || lpResumeFrom < len - 120))
            {
                lpMedia.Position = TimeSpan.FromSeconds(lpResumeFrom);
                LpNote(string.Format(Loc("LOC_KX_PlayerResumed", "Carrying on from {0}  ·  Y from the start"), Clock(lpResumeFrom)), 5);
            }
            lpMedia.Play();
            lpPlaying = true;
            lpCentre.Visibility = Visibility.Collapsed;
            UpdateMusicState();
            LpDraw();
        }

        private void LpEnded()
        {
            lpPlaying = false;
            LpCountWatched();
            LoadLocalState();
            localState.Pos.Remove(lpFile);
            SaveLocalState();
            string next = LpNeighbour(1);
            if (next != null)
            {
                lpNextAt = DateTime.Now.AddSeconds(8);
                lpCentre.Text = Loc("LOC_KX_PlayerNextSoon", "Next episode soon  ·  A now  ·  B stay here");
            }
            else lpCentre.Text = Loc("LOC_KX_PlayerFinished", "The end  ·  B back");
            lpCentre.Visibility = Visibility.Visible;
            LpShowControls();
            UpdateMusicState();
            LpDraw();
        }

        private void LpFailed(Exception ex)
        {
            string file = lpFile;
            logger.Warn("Kippix: player: Windows can't play " + Path.GetFileName(file) + " here (" + (ex != null ? ex.Message : "no reason given") + "): opening it in its own player");
            CloseLocalPlayer(false);
            PlayLocalOutside(file);
            if (openPanel == "page") SetPageStatus(Loc("LOC_KX_PlayerOutside", "That video can't be played inside Kippix (Windows needs a codec for it), so it opened in Windows' own player."));
        }

        private double LpLength()
        {
            return lpMedia != null && lpMedia.NaturalDuration.HasTimeSpan ? lpMedia.NaturalDuration.TimeSpan.TotalSeconds : 0;
        }

        private void LpCountWatched()
        {
            if (lpCountedWatched) return;
            lpCountedWatched = true;
            LoadLocalState();
            localWatched.Add(lpFile);
            SaveLocalState();
        }

        /// <summary>Closes the player (keeping where you got to), back to the page.</summary>
        private void CloseLocalPlayer(bool sound)
        {
            if (!lpOpen) return;
            lpOpen = false;
            if (lpTick != null) lpTick.Stop();
            try
            {
                if (lpMedia != null && lpReady && lpFile != null)
                {
                    double pos = lpMedia.Position.TotalSeconds, len = LpLength();
                    LoadLocalState();
                    if (pos > 30 && (len <= 0 || pos < len - 120)) localState.Pos[lpFile] = Math.Round(pos, 1);
                    else if (len > 0 && pos >= len - 120) localState.Pos.Remove(lpFile);
                    SaveLocalState();
                }
            }
            catch (Exception) { }
            LpRemoveLayer();
            lpPlaying = false;
            try { SetThreadExecutionState(EsContinuous); } catch (Exception) { }
            if (sound) PlaySound("back");
            UpdateMusicState();
            // the page shows what's been watched
            if (openPanel == "page")
            {
                if (pageKey == "localshow") FillLocalShow();
                else if (pageKey == "localmedia") RefreshLocalTileSubs();
            }
        }

        // ---------------- while it plays ----------------

        private void LpTick()
        {
            if (!lpOpen || lpMedia == null) { if (lpTick != null) lpTick.Stop(); return; }
            if (gameRunningNow || openPanel != "page") { CloseLocalPlayer(false); return; }   // (a game started, or the pages went: Home)
            if (lpNextAt != DateTime.MinValue)
            {
                int left = (int)Math.Ceiling((lpNextAt - DateTime.Now).TotalSeconds);
                if (left <= 0) { LpStep(1); return; }
                lpCentre.Text = string.Format(Loc("LOC_KX_PlayerNextIn", "Next episode in {0}  ·  A now  ·  B stay here"), left);
            }
            double pos = lpMedia.Position.TotalSeconds, len = LpLength();
            if (lpReady && len > 0 && pos / len >= 0.9) LpCountWatched();
            // subtitles
            string cue = null;
            if (settings.LocalMediaSubs && lpCues != null)
                foreach (var c in lpCues) { if (pos >= c.Item1 && pos <= c.Item2) { cue = c.Item3; break; } if (c.Item1 > pos) break; }
            if (cue != null) { if (lpSubText.Text != cue) lpSubText.Text = cue; lpSubBox.Visibility = Visibility.Visible; }
            else lpSubBox.Visibility = Visibility.Collapsed;
            // the note goes after a while; the controls fade away while it plays
            var noteBox = lpNote.Tag as FrameworkElement;
            if (noteBox != null && noteBox.Visibility == Visibility.Visible && DateTime.Now > lpNoteUntil) noteBox.Visibility = Visibility.Collapsed;
            if (lpControlsShown && lpPlaying && (DateTime.Now - lpLastInput).TotalSeconds > 3.5)
            {
                lpControlsShown = false;
                Fade(lpControls, 0);
                lpSubBox.Margin = new Thickness(60, 0, 60, 80);
            }
            LpDraw();
        }

        private void LpDraw()
        {
            if (lpLayer == null || lpMedia == null) return;
            double pos = lpReady ? lpMedia.Position.TotalSeconds : 0, len = LpLength();
            string vol = Math.Round(lpMedia.Volume * 100).ToString(CultureInfo.CurrentCulture) + "%";
            lpTime.Text = Clock(pos) + (len > 0 ? "  /  " + Clock(len) : "") + "     " + string.Format(Loc("LOC_KX_PlayerVolume", "Volume {0}"), vol);
            double w = lpTrack.ActualWidth;
            lpFill.Width = len > 0 && w > 0 ? Math.Max(0, Math.Min(1, pos / len)) * w : 0;
            var m = new HintMaker(HintsKeyboard);
            m.Add(lpPlaying ? Loc("LOC_KX_PlayerPause", "Pause") : Loc("LOC_KX_PlayerPlay", "Play"), "A", "Space");
            m.Add(Loc("LOC_KX_Player10", "10 seconds"), "Dpad:lr", "Arrows:lr");
            m.Add(Loc("LOC_KX_Player60", "A minute"), "LT RT", "PgUp PgDn");
            m.Add(Loc("LOC_KX_PlayerVol", "Volume"), "Dpad:ud", "Arrows:ud");
            m.Group();
            if (lpList != null && lpList.Count > 1) m.Add(Loc("LOC_KX_PlayerEpisodes", "Episode before / after"), "LB RB", "Q E");
            if (lpCues != null) m.Add(settings.LocalMediaSubs ? Loc("LOC_KX_PlayerSubsOff", "Subtitles off") : Loc("LOC_KX_PlayerSubsOn", "Subtitles on"), "X", "S");
            m.Add(Loc("LOC_KX_PlayerRestart", "From the start"), "Y", "Home");
            m.Add(Loc("LOC_KX_PlayerClose", "Close"), "B", "Esc");
            Fill(lpHints, m.Items, ref lpHintsSig);
        }

        private void LpShowControls()
        {
            lpLastInput = DateTime.Now;
            if (lpControlsShown || lpControls == null) return;
            lpControlsShown = true;
            Fade(lpControls, 1);
            if (lpSubBox != null) lpSubBox.Margin = new Thickness(60, 0, 60, 250);
        }

        private void LpNote(string text, double seconds)
        {
            var box = lpNote.Tag as FrameworkElement;
            lpNote.Text = text;
            lpNoteUntil = DateTime.Now.AddSeconds(seconds);
            if (box != null) box.Visibility = Visibility.Visible;
        }

        private void LpPlayPause()
        {
            if (lpMedia == null) return;
            if (lpNextAt != DateTime.MinValue) { LpStep(1); return; }   // (the next episode now)
            if (!lpReady) return;
            if (lpPlaying) { lpMedia.Pause(); lpPlaying = false; lpCentre.Text = Loc("LOC_KX_PlayerPaused", "Paused"); lpCentre.Visibility = Visibility.Visible; }
            else
            {
                if (LpLength() > 0 && lpMedia.Position.TotalSeconds >= LpLength() - 0.5) lpMedia.Position = TimeSpan.Zero;   // (finished: again)
                lpMedia.Play(); lpPlaying = true; lpCentre.Visibility = Visibility.Collapsed;
            }
            PlaySound("move");
            UpdateMusicState();
            LpDraw();
        }

        private void LpSeek(double by)
        {
            if (lpMedia == null || !lpReady) return;
            lpNextAt = DateTime.MinValue;
            double len = LpLength(), t = lpMedia.Position.TotalSeconds + by;
            if (len > 0) t = Math.Min(t, len - 1);
            lpMedia.Position = TimeSpan.FromSeconds(Math.Max(0, t));
            if (!lpPlaying && lpCentre.Text != Loc("LOC_KX_PlayerPaused", "Paused")) { lpCentre.Text = Loc("LOC_KX_PlayerPaused", "Paused"); }
            LpDraw();
        }

        private void LpVolume(int dir)
        {
            if (lpMedia == null) return;
            double v = Math.Round(Math.Max(0, Math.Min(1, lpMedia.Volume + dir * 0.1)), 2);
            lpMedia.Volume = v;
            settings.LocalMediaVolume = v;
            SaveSettings();
            UpdateMusicState();
            LpDraw();
        }

        private string LpNeighbour(int dir)
        {
            if (lpList == null) return null;
            int at = lpList.FindIndex(f => string.Equals(f, lpFile, StringComparison.OrdinalIgnoreCase));
            int to = at + dir;
            return at >= 0 && to >= 0 && to < lpList.Count ? lpList[to] : null;
        }

        /// <summary>LB / RB: the episode before / after (where you got to in this one is kept).</summary>
        private void LpStep(int dir)
        {
            string to = LpNeighbour(dir);
            if (to == null) { PlaySound("back"); return; }
            if (!File.Exists(to)) { PlaySound("back"); LpNote(string.Format(Loc("LOC_KX_LocalFileGone", "Can't find {0} any more."), Path.GetFileName(to)), 4); return; }
            try
            {
                if (lpReady && lpNextAt == DateTime.MinValue)
                {
                    double pos = lpMedia.Position.TotalSeconds, len = LpLength();
                    LoadLocalState();
                    if (pos > 30 && (len <= 0 || pos < len - 120)) localState.Pos[lpFile] = Math.Round(pos, 1);
                }
            }
            catch (Exception) { }
            PlaySound("select");
            lpFile = to;
            LpFill();
            SaveLocalState();
        }

        private void LpRestart()
        {
            if (lpMedia == null || !lpReady) return;
            lpNextAt = DateTime.MinValue;
            lpMedia.Position = TimeSpan.Zero;
            if (!lpPlaying) { lpMedia.Play(); lpPlaying = true; lpCentre.Visibility = Visibility.Collapsed; }
            PlaySound("select");
            UpdateMusicState();
            LpDraw();
        }

        private void LpToggleSubs()
        {
            if (lpCues == null) return;
            settings.LocalMediaSubs = !settings.LocalMediaSubs;
            SaveSettings();
            LpNote(settings.LocalMediaSubs ? Loc("LOC_KX_PlayerSubsShown", "Subtitles on") : Loc("LOC_KX_PlayerSubsHidden", "Subtitles off"), 2.5);
            PlaySound("move");
            LpTick();
        }

        private void LpBack()
        {
            if (lpNextAt != DateTime.MinValue) { lpNextAt = DateTime.MinValue; lpCentre.Text = Loc("LOC_KX_PlayerFinished", "The end  ·  B back"); PlaySound("back"); return; }
            CloseLocalPlayer(true);
        }

        // ---------------- keys and buttons ----------------

        /// <summary>Keys while the player is up (window PreviewKeyDown; the D-pad arrives as arrows, LT / RT as PgUp / PgDn). Takes every key.</summary>
        private void LocalPlayerKey(KeyEventArgs e)
        {
            if (e.Key == Key.System) return;
            e.Handled = true;
            if (e.GetType().Name == "GameControllerInputEventArgs") return;   // (the buttons: LocalPlayerPad)
            if ((DateTime.Now - lpOpenedAt).TotalMilliseconds < 350) return;   // (the press that opened it)
            LpShowControls();
            switch (e.Key)
            {
                case Key.Left: LpSeek(-10); break;
                case Key.Right: LpSeek(10); break;
                case Key.Up: LpVolume(1); break;
                case Key.Down: LpVolume(-1); break;
                case Key.PageUp: LpSeek(-60); break;
                case Key.PageDown: LpSeek(60); break;
                case Key.Q: if (!e.IsRepeat) LpStep(-1); break;
                case Key.E: if (!e.IsRepeat) LpStep(1); break;
                case Key.S: if (!e.IsRepeat) LpToggleSubs(); break;
                case Key.Home: if (!e.IsRepeat) LpRestart(); break;
                case Key.Space: case Key.MediaPlayPause: if (!e.IsRepeat) LpPlayPause(); break;
                case Key.Enter: if (!e.IsRepeat && (DateTime.Now - lpPadAAt).TotalMilliseconds > 400) LpPlayPause(); break;
                case Key.Escape: case Key.Back: if (!e.IsRepeat && (DateTime.Now - lpPadBAt).TotalMilliseconds > 400) LpBack(); break;
            }
        }

        private void LocalPlayerPad(ControllerInput button, KeyEventArgs e)
        {
            if (IsPadRelease(e)) return;
            if ((DateTime.Now - lpOpenedAt).TotalMilliseconds < 350) return;
            LpShowControls();
            switch (button)
            {
                case ControllerInput.A: lpPadAAt = DateTime.Now; LpPlayPause(); break;
                case ControllerInput.B: lpPadBAt = DateTime.Now; LpBack(); break;
                case ControllerInput.X: LpToggleSubs(); break;
                case ControllerInput.Y: LpRestart(); break;
                case ControllerInput.LeftShoulder: LpStep(-1); break;
                case ControllerInput.RightShoulder: LpStep(1); break;
                // the D-pad and the sticks come as arrows, LT / RT as PgUp / PgDn (LocalPlayerKey)
            }
        }

        // ---------------- subtitles (.srt beside the video) ----------------

        private List<Tuple<double, double, string>> LpReadSubs(string video)
        {
            try
            {
                string dir = Path.GetDirectoryName(video), stem = Path.GetFileNameWithoutExtension(video);
                string file = null;
                foreach (var c in new[] { stem + ".srt", stem + ".en.srt", stem + ".eng.srt", stem + ".English.srt" })
                    if (File.Exists(Path.Combine(dir, c))) { file = Path.Combine(dir, c); break; }
                if (file == null)
                    file = Directory.GetFiles(dir, stem + ".*.srt").FirstOrDefault();
                if (file == null) return null;
                var cues = new List<Tuple<double, double, string>>();
                string text = File.ReadAllText(file).Replace("\r\n", "\n").Replace('\r', '\n');
                foreach (var block in Regex.Split(text, @"\n\s*\n"))
                {
                    var m = Regex.Match(block, @"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)[^\n]*\n([\s\S]*)");
                    if (!m.Success) continue;
                    double a = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + int.Parse(m.Groups[4].Value) / Math.Pow(10, m.Groups[4].Value.Length);
                    double b = int.Parse(m.Groups[5].Value) * 3600 + int.Parse(m.Groups[6].Value) * 60 + int.Parse(m.Groups[7].Value) + int.Parse(m.Groups[8].Value) / Math.Pow(10, m.Groups[8].Value.Length);
                    string words = Regex.Replace(m.Groups[9].Value, @"<[^>]+>|\{\\[^}]*\}", "").Trim();
                    if (words.Length > 0) cues.Add(Tuple.Create(a, b, words));
                }
                cues.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                return cues.Count > 0 ? cues : null;
            }
            catch (Exception ex) { logger.Info("Kippix: player: subtitles: " + ex.Message); return null; }
        }
    }
}

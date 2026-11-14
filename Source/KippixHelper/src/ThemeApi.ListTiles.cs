using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>One slider row in the settings (Left / Right on it): its words, how full its bar is, where its knob sits.</summary>
    public class SliderModel : Observable
    {
        private string text = "";
        private double fill;
        public string Text { get { return text; } set { Set(ref text, value, "Text"); } }
        /// <summary>The bar is 480 wide.</summary>
        public double FillWidth { get { return fill; } }
        public Thickness KnobMargin { get { return new Thickness(Math.Max(0, fill - 12), 0, 0, 0); } }
        public void SetFill(double fraction)
        {
            fill = Math.Round(480 * Math.Max(0, Math.Min(1, fraction)));
            Notify("FillWidth");
            Notify("KnobMargin");
        }
    }

    /// <summary>
    /// The list down one side: each game's own size (height and width, or worked out from the screen), how much of a game
    /// the play info takes, what it shows (play time, how long to beat, achievements...), and the sliders for those and for
    /// the home background's fade. "Games to display" (1-10) says how many show: the list grows, the games keep their size.
    /// </summary>
    public partial class ThemeApi
    {
        public SliderModel FadeSlider { get; private set; }
        public SliderModel ListInfoSlider { get; private set; }
        public SliderModel ListTileHSlider { get; private set; }
        public SliderModel ListTileWSlider { get; private set; }
        public SliderModel ListCountSlider { get; private set; }
        public ICommand ResetListCountCommand { get; private set; }
        public ObservableCollection<PickRow> ListInfoRows { get; private set; }
        public ICommand ResetListTileCommand { get; private set; }
        public ICommand OpenListInfoCommand { get; private set; }
        public bool IsOptionsListInfo { get { return optionsPage == "listinfo"; } }
        public string ListInfoSummary
        {
            get
            {
                var names = ListInfoChoices.Where(c => ListInfoOn(c.Item1)).Select(c => c.Item2).ToList();
                return names.Count == 0 ? Loc("LOC_KX_Nothing", "Nothing") : string.Join(", ", names.ToArray());
            }
        }
        public string ListInfoFitText { get; private set; }
        public string ListTileAutoText { get { return settings.ListTileH > 0 || settings.ListTileW > 0 ? Loc("LOC_KX_YourSize", "Your size") : Loc("LOC_KX_AutoSize", "automatic"); } }
        public bool HasListInfoFitText { get { return !string.IsNullOrEmpty(ListInfoFitText); } }

        private static readonly string[] DefaultListInfo = { "playtime", "installed", "lastplayed" };

        private List<Tuple<string, string>> ListInfoChoices
        {
            get
            {
                return new List<Tuple<string, string>>
                {
                    Tuple.Create("name", Loc("LOC_KX_LiName", "Game name")),
                    Tuple.Create("playtime", Loc("LOC_KX_LiPlaytime", "Play time")),
                    Tuple.Create("installed", Loc("LOC_KX_LiInstalled", "Not installed")),
                    Tuple.Create("lastplayed", Loc("LOC_KX_LiLastPlayed", "Last played")),
                    Tuple.Create("hltb", Loc("LOC_KX_LiHltb", "How long to beat")),
                    Tuple.Create("ach", Loc("LOC_KX_LiAch", "Achievements")),
                    Tuple.Create("status", Loc("LOC_KX_LiStatus", "Completion status")),
                    Tuple.Create("platform", Loc("LOC_KX_LiPlatform", "Platform")),
                    Tuple.Create("source", Loc("LOC_KX_LiSource", "Store / library")),
                    Tuple.Create("year", Loc("LOC_KX_LiYear", "Release year")),
                    Tuple.Create("genre", Loc("LOC_KX_LiGenre", "Genre"))
                };
            }
        }

        private bool ListInfoOn(string key)
        {
            if (key == "name") return settings.ListShowName;
            return (settings.ListInfoItems ?? DefaultListInfo.ToList()).Contains(key);
        }

        private void InitListTiles()
        {
            FadeSlider = new SliderModel();
            ListInfoSlider = new SliderModel();
            ListTileHSlider = new SliderModel();
            ListTileWSlider = new SliderModel();
            ListCountSlider = new SliderModel();
            ResetListCountCommand = new RelayCommand(_ =>
            {
                settings.ListRows = 5; SaveSettings(); PlaySound("move"); HomeModeChanged(); UpdateSliders();   // A on the slider: back to 5
            });
            ListInfoRows = new ObservableCollection<PickRow>();
            ResetListTileCommand = new RelayCommand(_ =>
            {
                settings.ListTileH = 0; settings.ListTileW = 0;
                SaveSettings(); PlaySound("move"); HomeModeChanged(); UpdateSliders();
            });
            OpenListInfoCommand = new RelayCommand(_ => { PlaySound("select"); BuildListInfoRows(); SetOptionsPage("listinfo"); });
            UpdateSliders();
        }

        /// <summary>Left / Right on a slider row (named KX_Slider_…). True if it was one.</summary>
        private bool SliderKey(FrameworkElement fe, Key key)
        {
            if (fe == null || fe.Name == null || !fe.Name.StartsWith("KX_Slider_", StringComparison.Ordinal) || (key != Key.Left && key != Key.Right)) return false;
            int dir = key == Key.Right ? 1 : -1;
            switch (fe.Name.Substring(10))
            {
                case "fade":
                    settings.HomeFadeReach = Math.Max(5, Math.Min(95, settings.HomeFadeReach + dir * 2));
                    NotifyHomeBackground();
                    break;
                case "listinfo":
                    settings.ListInfoShare = Math.Max(15, Math.Min(85, settings.ListInfoShare + dir * 5));
                    ApplyListInfoShareNow();   // on the games behind the menu straight away
                    RefreshTileArtSoon();
                    break;
                case "tileh":
                    settings.ListTileH = Math.Max(60, Math.Min(400, Math.Round(CurrentListTileH() / 10) * 10 + dir * 10));
                    HomeModeChanged();
                    break;
                case "listcount":
                {
                    // how many games show: the games keep their size, the list gets taller or shorter
                    int n = Math.Max(1, Math.Min(10, settings.ListRows + dir));
                    if (n == settings.ListRows) { PlaySound("back"); return true; }
                    settings.ListRows = n;
                    HomeModeChanged();
                    break;
                }
                case "bgchange":
                    if (!NudgeBgChangeDelay(dir)) { PlaySound("back"); return true; }   // (0.99.7) how soon the background follows the highlighted game
                    break;
                case "bgdelay":
                    NudgePreviewDelay(dir);   // the trailer behind the home screen: how long the game stays highlighted first
                    break;
                case "boardw":
                case "boardh":
                    if (!BoardNudgeSize(fe.Name.EndsWith("w"), dir)) { PlaySound("back"); return true; }
                    break;
                case "trispacing":
                    settings.TriSpacing = Math.Max(0, Math.Min(40, settings.TriSpacing + dir * 2));
                    ApplyCardLayoutSoon();
                    RefreshTileArtSoon();
                    RebuildExtraRowsSoon();
                    break;
                case "spacing":
                    settings.CardSpacing = Math.Max(0, Math.Min(60, (settings.CardSpacing < 0 ? CardSpacingNow : settings.CardSpacing) + dir * 2));
                    ApplyCardSpacing();
                    break;
                case "tilew":
                    settings.ListTileW = Math.Max(200, Math.Min(1400, Math.Round(CurrentListTileW() / 20) * 20 + dir * 20));
                    HomeModeChanged();
                    break;
                default: return false;
            }
            SaveSettings();
            PlaySound("move");
            UpdateSliders();
            return true;
        }

        /// <summary>The play info's share of each game, set on every game showing now (not waiting for the next refresh).</summary>
        private void ApplyListInfoShareNow()
        {
            try
            {
                if (LayoutNow != 2) return;
                var list = UiTools.FindByName(Application.Current.MainWindow, "PART_ListGameItems") as ItemsControl;
                if (list == null) return;
                var art = settings.ListShowInfo ? new GridLength(100 - settings.ListInfoShare, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
                var info = settings.ListShowInfo ? new GridLength(settings.ListInfoShare, GridUnitType.Star) : new GridLength(0);
                for (int i = 0; i < list.Items.Count; i++)
                {
                    var c = list.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                    var holder = c != null ? UiTools.FindByName(c, "KX_WideArt") as Grid : null;
                    if (holder == null || holder.ColumnDefinitions.Count < 2) continue;
                    holder.ColumnDefinitions[0].Width = art;
                    holder.ColumnDefinitions[1].Width = info;
                    holder.InvalidateMeasure();
                    holder.InvalidateArrange();
                }
                list.UpdateLayout();
            }
            catch (Exception) { }
        }

        /// <summary>Game cards and layout › list: the play info beside each game, or the art alone (its settings hide with it).</summary>
        public bool ListShowInfo
        {
            get { return settings.ListShowInfo; }
            set
            {
                if (settings.ListShowInfo == value) return;
                settings.ListShowInfo = value;
                SaveSettings();
                Notify("ListShowInfo");
                ApplyListInfoShareNow();
                RefreshTileArtSoon();
            }
        }

        private double CurrentListTileH() { return settings.ListTileH > 0 ? settings.ListTileH : (sideItemH > 0 && LayoutNow == 2 ? sideItemH : 170); }
        private double CurrentListTileW() { return settings.ListTileW > 0 ? settings.ListTileW : (sideItemW > 0 && LayoutNow == 2 ? sideItemW : CurrentListTileH() * ListAspectW / ListAspectH); }

        private void UpdateSliders()
        {
            if (FadeSlider == null) return;
            FadeSlider.Text = string.Format(Loc("LOC_KX_FadeReach", "{0}% of the screen"), settings.HomeFadeReach);
            FadeSlider.SetFill((settings.HomeFadeReach - 5) / 90.0);
            ListInfoSlider.Text = string.Format(Loc("LOC_KX_ListInfoShareText", "{0}% of each game"), settings.ListInfoShare);
            ListInfoSlider.SetFill((settings.ListInfoShare - 15) / 70.0);
            double th = CurrentListTileH(), tw = CurrentListTileW();
            string auto = "  ·  " + Loc("LOC_KX_AutoSize", "automatic");
            ListTileHSlider.Text = Math.Round(th) + " px" + (settings.ListTileH > 0 ? "" : auto);
            ListTileHSlider.SetFill((th - 60) / 340.0);
            ListTileWSlider.Text = Math.Round(tw) + " px" + (settings.ListTileW > 0 ? "" : auto);
            ListTileWSlider.SetFill((tw - 200) / 1200.0);
            Notify("ListTileAutoText");
            int count = Math.Max(1, Math.Min(10, settings.ListRows));
            ListCountSlider.Text = string.Format(count == 1 ? Loc("LOC_KX_ListCountOne", "{0} game") : Loc("LOC_KX_ListCountMany", "{0} games"), count);
            ListCountSlider.SetFill((count - 1) / 9.0);
            Notify("ListStretchText");
            BoardUpdateSliders();
            UpdateTriSlider();
            if (SpacingSlider != null)
            {
                SpacingSlider.Text = settings.CardSpacing < 0 ? string.Format(Loc("LOC_KX_SpacingAuto", "{0} px · as it comes"), CardSpacingNow) : settings.CardSpacing + " px";
                SpacingSlider.SetFill(CardSpacingNow / 60.0);
            }
        }

        // ---------------- what the play info shows ----------------

        private void BuildListInfoRows()
        {
            ListInfoRows.Clear();
            if (HasListInfoFitText) ListInfoRows.Add(new PickRow { Text = ListInfoFitText, Icon = "info", IsInfo = true, Key = "fit" });
            foreach (var c in ListInfoChoices)
            {
                string k = c.Item1;
                var row = new PickRow { Text = c.Item2, Icon = InfoIcon(k), Key = k, IsSelected = ListInfoOn(k), IsSwitch = true };
                row.Command = new RelayCommand(_ =>
                {
                    bool nowOn;
                    if (k == "name") { settings.ListShowName = !settings.ListShowName; nowOn = settings.ListShowName; }
                    else
                    {
                        var on = settings.ListInfoItems ?? DefaultListInfo.ToList();
                        if (!on.Remove(k)) on.Add(k);
                        settings.ListInfoItems = on;
                        nowOn = on.Contains(k);
                    }
                    var shown = ListInfoRows.FirstOrDefault(r => r.Key == k);
                    if (shown != null) shown.IsSelected = nowOn;   // switched where it is: the highlight stays
                    SaveSettings();
                    PlaySound("move");
                    Notify("ListInfoSummary");
                    RefreshTileArtSoon();
                });
                ListInfoRows.Add(row);
            }
        }

        /// <summary>The play info lines for one game, in the order of the choices.</summary>
        private List<string> ListInfoLines(Game g)
        {
            var lines = new List<string>();
            foreach (var c in ListInfoChoices)
            {
                if (!ListInfoOn(c.Item1)) continue;
                string t = null;
                try
                {
                    switch (c.Item1)
                    {
                        case "name": continue;   // (the name has its own line, above these)
                        case "playtime": t = g.Playtime > 0 ? string.Format(Loc("LOC_KX_ListPlayed", "{0} played"), HoursShort(g.Playtime / 3600.0)) : Loc("LOC_KX_ListNeverPlayed", "Not played yet"); break;
                        case "installed": t = g.IsInstalled ? null : Loc("LOC_KX_NotInstalled", "Not installed"); break;
                        case "lastplayed": t = g.LastActivity.HasValue ? string.Format(Loc("LOC_KX_ListLast", "Last played {0}"), AgoText(g.LastActivity.Value)) : null; break;
                        case "hltb": { string h = HltbBadgeText(g.Id, true); t = string.IsNullOrEmpty(h) ? null : string.Format(Loc("LOC_KX_LiHltbLine", "{0} to beat"), h); break; }
                        case "ach": { string a = AchBadgeText(g.Id); t = string.IsNullOrEmpty(a) ? null : string.Format(Loc("LOC_KX_LiAchLine", "{0} achievements"), a); break; }
                        case "status": t = g.CompletionStatus != null ? g.CompletionStatus.Name : null; break;
                        case "platform": t = g.Platforms != null && g.Platforms.Count > 0 ? g.Platforms[0].Name : null; break;
                        case "source": t = g.Source != null ? g.Source.Name : null; break;
                        case "year": t = g.ReleaseYear.HasValue ? g.ReleaseYear.Value.ToString(CultureInfo.InvariantCulture) : null; break;
                        case "genre": t = g.Genres != null && g.Genres.Count > 0 ? string.Join(", ", g.Genres.Take(2).Select(x => x.Name).ToArray()) : null; break;
                    }
                }
                catch (Exception) { }
                if (!string.IsNullOrEmpty(t)) lines.Add(t);
            }
            return lines;
        }

        /// <summary>After the list is filled in: says (on the settings page) when the play info doesn't fit in the games.</summary>
        private void CheckListInfoFit(ItemsControl list)
        {
            try
            {
                bool over = false;
                for (int i = 0; i < list.Items.Count && !over; i++)
                {
                    var c = list.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                    if (c == null || !c.IsVisible) continue;
                    var info = UiTools.FindByName(c, "KX_ListInfo") as FrameworkElement;
                    var box = UiTools.FindByName(c, "KX_ListInfoFit") as FrameworkElement;
                    if (info == null || box == null || box.ActualWidth <= 0 || info.ActualWidth <= 0) continue;
                    // the box shrinks the text to fit: only say so when it's had to make it quite small
                    var scale = info.LayoutTransform as ScaleTransform;
                    double grown = scale != null ? scale.ScaleX : 1;
                    double shrink = Math.Min(box.ActualWidth / Math.Max(1, info.ActualWidth * grown), box.ActualHeight / Math.Max(1, info.ActualHeight * grown));
                    if (shrink * grown < 0.62) over = true;
                }
                string want = over ? Loc("LOC_KX_ListInfoSmall", "The play info has had to get small to fit: make the games taller or wider, give the info more room, or show fewer things") : "";
                if (want == (ListInfoFitText ?? "")) return;
                ListInfoFitText = want;
                Notify("ListInfoFitText");
                Notify("HasListInfoFitText");
                if (optionsPage != "listinfo") return;
                var fit = ListInfoRows.FirstOrDefault(r => r.Key == "fit");
                if (over && fit == null) ListInfoRows.Insert(0, new PickRow { Text = want, Icon = "info", IsInfo = true, Key = "fit" });
                else if (!over && fit != null && FocusedRow(ListInfoRows) != fit) ListInfoRows.Remove(fit);
            }
            catch (Exception) { }
        }
    }
}

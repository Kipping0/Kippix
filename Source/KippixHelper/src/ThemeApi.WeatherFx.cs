using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KippixHelper
{
    /// <summary>
    /// Weather touches (Home settings › Weather touches): rain, a storm, snow, fog or sunshine on the
    /// home screen, drawn with the seasonal effects. Either it follows the weather where you are (the weather widget's town,
    /// looked up every 20 minutes while it's on), or you choose one: always, or on set days between set hours.
    /// </summary>
    public partial class ThemeApi
    {
        private static readonly string[] WeatherFxKinds = { "rain", "storm", "snow", "fog", "sun" };
        private string wxLiveKind;              // from the live weather (null: dry / not known yet)
        private string wxLiveWord = "";
        private DateTime wxLiveAt = DateTime.MinValue;
        private bool wxLiveBusy;
        private Rectangle stormFlash;
        private double stormWait, stormGlow;

        private string WeatherFxName(string k)
        {
            switch (k)
            {
                case "rain": return Loc("LOC_KX_WxFxRain", "Rain");
                case "storm": return Loc("LOC_KX_WxFxStorm", "Storm (rain and lightning)");
                case "snow": return Loc("LOC_KX_WxFxSnow", "Snow");
                case "fog": return Loc("LOC_KX_WxFxFog", "Fog");
                case "sun": return Loc("LOC_KX_WxFxSun", "Sunshine");
            }
            return k;
        }

        // ---- set by hand (0.82): each effect its own switch, and When: always / each on its own dates / set days and times ----
        private static readonly int[] WxDefaultDates = { 1001, 331, 601, 831, 1201, 228, 1015, 1130, 601, 831 };

        private void NormalizeWeatherFx()
        {
            if (settings.WeatherFxWhen < 0 || settings.WeatherFxWhen > 2) settings.WeatherFxWhen = settings.WeatherFxSchedule ? 2 : 0;
            if (settings.WeatherFxPick < 0)
            {
                int i = Array.IndexOf(WeatherFxKinds, settings.WeatherFxKind);
                settings.WeatherFxPick = 1 << (i < 0 ? 0 : i);
            }
            settings.WeatherFxPick &= 31;
            settings.WeatherFxLivePick &= 31;
            var d = settings.WeatherFxDates;
            if (d == null || d.Count != WxDefaultDates.Length) settings.WeatherFxDates = d = new List<int>(WxDefaultDates);
            for (int i = 0; i < d.Count; i++) d[i] = CleanMonthDay(d[i], WxDefaultDates[i]);
        }

        private static int CleanMonthDay(int md, int fallback)
        {
            int m = md / 100, day = md % 100;
            if (m < 1 || m > 12 || day < 1 || day > DateTime.DaysInMonth(2001, m)) return fallback;
            return md;
        }

        private bool WxPicked(int i) { return (settings.WeatherFxPick & (1 << i)) != 0; }
        private int WxFromOf(int i) { NormalizeWeatherFx(); return settings.WeatherFxDates[i * 2]; }
        private int WxToOf(int i) { NormalizeWeatherFx(); return settings.WeatherFxDates[i * 2 + 1]; }

        private static bool InMonthDays(int from, int to, DateTime d)
        {
            int md = d.Month * 100 + d.Day;
            return from <= to ? md >= from && md <= to : md >= from || md <= to;
        }

        private static string MonthDayText(int md)
        {
            try { return new DateTime(2001, md / 100, md % 100).ToString("d MMMM", CultureInfo.CurrentCulture); } catch (Exception) { return md.ToString(); }
        }

        /// <summary>The weather effects showing now (none: an empty list).</summary>
        private List<string> WeatherKindsNow()
        {
            var list = new List<string>();
            if (!settings.WeatherFxOn) return list;
            if (settings.WeatherFxSync)
            {
                int li = wxLiveKind != null ? Array.IndexOf(WeatherFxKinds, wxLiveKind) : -1;
                if (li >= 0 && (settings.WeatherFxLivePick & (1 << li)) != 0) list.Add(wxLiveKind);   // (0.85: only the kinds switched on)
                return list;
            }
            NormalizeWeatherFx();
            var now = DateTime.Now;
            if (settings.WeatherFxWhen == 2)
            {
                int bit = 1 << (((int)now.DayOfWeek + 6) % 7);   // Monday first
                if ((settings.WeatherFxDays & bit) == 0) return list;
                int from = settings.WeatherFxFrom, to = settings.WeatherFxTo, h = now.Hour;
                bool inHours = from == to || (from < to ? h >= from && h < to : h >= from || h < to);
                if (!inHours) return list;
            }
            for (int i = 0; i < WeatherFxKinds.Length; i++)
                if (WxPicked(i) && (settings.WeatherFxWhen != 1 || InMonthDays(WxFromOf(i), WxToOf(i), now))) list.Add(WeatherFxKinds[i]);
            if (list.Contains("storm")) list.Remove("rain");   // (the storm has its own rain)
            return list;
        }

        /// <summary>Left / Right (and A) on the Weather touches page's rows (0.82).</summary>
        private bool WeatherNudge(string key, int dir)
        {
            if (key == "wxwhen")
            {
                // (0.85) one cycle: follow the weather where you are → always → each on its own dates → set days and times
                NormalizeWeatherFx();
                int now = settings.WeatherFxSync ? 0 : settings.WeatherFxWhen + 1;
                int next = (now + dir + 4) % 4;
                settings.WeatherFxSync = next == 0;
                if (next > 0) settings.WeatherFxWhen = next - 1;
                settings.WeatherFxSchedule = !settings.WeatherFxSync && settings.WeatherFxWhen == 2;
                wxLiveAt = DateTime.MinValue;
                WeatherFxChanged("wxwhen");
                return true;
            }
            bool isFrom = key.StartsWith("wxdfrom:", StringComparison.Ordinal), isTo = key.StartsWith("wxdto:", StringComparison.Ordinal);
            if (!isFrom && !isTo) return false;
            int i = Array.IndexOf(WeatherFxKinds, key.Substring(key.IndexOf(':') + 1));
            if (i < 0) return false;
            NormalizeWeatherFx();
            int slot = i * 2 + (isTo ? 1 : 0), md = settings.WeatherFxDates[slot];
            var d = new DateTime(2001, md / 100, md % 100).AddDays(dir);
            settings.WeatherFxDates[slot] = d.Month * 100 + d.Day;
            WeatherFxChanged(key);
            return true;
        }

        private void WeatherFxChanged(string key)
        {
            SaveSettings();
            PlaySound("move");
            seasonalBuiltFor = null;
            Notify("SeasonalShown"); Notify("SeasonalSummary"); Notify("WeatherFxSummary");
            UpdateSeasonal();
            if (openPanel == "options" && optionsPage == "weatherfx") BoardRowsAgain(BuildWeatherFxRows, key);
        }

        /// <summary>WMO weather code → the effect (clear and cloudy: nothing).</summary>
        private static string KindFromCode(int code)
        {
            if (code >= 95) return "storm";
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return "snow";
            if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return "rain";
            if (code == 45 || code == 48) return "fog";
            if (code == 0) return "sun";
            return null;
        }

        /// <summary>The live weather, at most every 20 minutes (only while Weather touches follow it).</summary>
        private void RefreshLiveWeatherSoon()
        {
            if (wxLiveBusy || (DateTime.Now - wxLiveAt).TotalMinutes < 20) return;
            if (settings.WeatherLat == 0 && settings.WeatherLon == 0) return;
            wxLiveBusy = true;
            wxLiveAt = DateTime.Now;
            double lat = settings.WeatherLat, lon = settings.WeatherLon;
            KippixShared.SafeWork.Queue(_ =>
            {
                int code = -1;
                bool day = true;
                try
                {
                    var root = QuickJson("https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CultureInfo.InvariantCulture)
                                         + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                                         + "&current=weather_code,is_day&timezone=auto&forecast_days=1", 10000) as Dictionary<string, object>;
                    var cur = Dig(root, "current") as Dictionary<string, object>;
                    if (cur != null) { code = (int)FNum(cur, "weather_code"); day = FNum(cur, "is_day") != 0; }
                }
                catch (Exception ex) { logger.Warn("Kippix: weather touches: " + ex.Message); }
                OnUi(() =>
                {
                    wxLiveBusy = false;
                    if (code < 0) { wxLiveAt = DateTime.Now.AddMinutes(-15); return; }   // try again in 5 minutes
                    string kind = KindFromCode(code);
                    if (kind == "sun" && !day) kind = null;   // no sunshine at night
                    wxLiveWord = WeatherWord(code);
                    Notify("WeatherFxSummary");   // the word can change on its own (cloudy to overcast: no effect either way)
                    if (kind != wxLiveKind)
                    {
                        wxLiveKind = kind;
                        seasonalBuiltFor = null;
                        Notify("SeasonalShown");
                        UpdateSeasonal();
                    }
                    // the page is open: new lines in place, so the highlight stays on whichever row it was on
                    if (openPanel == "options" && optionsPage == "weatherfx")
                    {
                        var fresh = new ObservableCollection<PickRow>();
                        AddWeatherFxRows(fresh);
                        foreach (var row in WeatherFxRows)
                        {
                            var n = fresh.FirstOrDefault(x => x.Key == row.Key);
                            if (n != null && n.Sub != row.Sub) row.SetSub(n.Sub);
                        }
                    }
                });
            });
        }

        // ---------------- drawing ----------------

        private void AddWeatherFx(string kind, double w, double h)
        {
            var r = seasonRnd;
            switch (kind)
            {
                case "rain":
                case "storm":
                {
                    int n = kind == "storm" ? 150 : 110;
                    for (int i = 0; i < n; i++)
                    {
                        double len = 14 + r.NextDouble() * 16;
                        var brush = new LinearGradientBrush(Color.FromArgb(0, 200, 220, 255), Color.FromArgb(150, 210, 225, 255), 90);
                        brush.Freeze();
                        var drop = new Rectangle { Width = 1.6, Height = len, Fill = brush, IsHitTestVisible = false, Opacity = 0.5 + r.NextDouble() * 0.4 };
                        var f = new Flake { Kind = "drop", El = drop, Pos = new TranslateTransform(), Turn = new RotateTransform(14), X = r.NextDouble() * w, Y = r.NextDouble() * h,
                                            Vy = (kind == "storm" ? 1100 : 900) + r.NextDouble() * 300, Vx = kind == "storm" ? 300 : 220 };
                        var tg = new TransformGroup();
                        tg.Children.Add(f.Turn);
                        tg.Children.Add(f.Pos);
                        drop.RenderTransform = tg;
                        Place(f);
                        seasonCanvas.Children.Add(drop);
                        flakes.Add(f);
                    }
                    if (kind == "storm")
                    {
                        stormFlash = new Rectangle { Width = w, Height = h, Fill = Frozen(Color.FromRgb(235, 240, 255)), Opacity = 0, IsHitTestVisible = false };
                        seasonCanvas.Children.Insert(0, stormFlash);
                        stormWait = 3 + r.NextDouble() * 6;
                        stormGlow = 0;
                    }
                    break;
                }
                case "snow":
                    for (int i = 0; i < 60; i++) AddFlake("snow", w, h, true);
                    break;
                case "fog":
                    for (int i = 0; i < 7; i++)
                    {
                        double fw = 700 + r.NextDouble() * 500, fh = 220 + r.NextDouble() * 160;
                        var g = new RadialGradientBrush(Color.FromArgb(70, 220, 225, 232), Color.FromArgb(0, 220, 225, 232));
                        g.Freeze();
                        var puff = new Ellipse { Width = fw, Height = fh, Fill = g, IsHitTestVisible = false };
                        puff.CacheMode = new BitmapCache { RenderAtScale = 0.5 };   // (1.0.1) a soft blur of colour: a half-size picture of it, only moved and faded
                        var f = new Flake { Kind = "fog", El = puff, Pos = new TranslateTransform(), Turn = new RotateTransform(), X = r.NextDouble() * w - fw / 2, Y = h * (0.25 + r.NextDouble() * 0.6) - fh / 2,
                                            Vx = 8 + r.NextDouble() * 14, Phase = r.NextDouble() * Math.PI * 2, Size = fw };
                        puff.RenderTransform = f.Pos;
                        Place(f);
                        seasonCanvas.Children.Add(puff);
                        flakes.Add(f);
                    }
                    break;
                case "sun":
                {
                    var g = new RadialGradientBrush(Color.FromArgb(90, 255, 214, 140), Color.FromArgb(0, 255, 214, 140));
                    g.Freeze();
                    var glow = new Ellipse { Width = 1100, Height = 1100, Fill = g, IsHitTestVisible = false };
                    glow.CacheMode = new BitmapCache { RenderAtScale = 0.5 };   // (1.0.1) a soft blur of colour: a half-size picture of it, only moved and faded
                    var f = new Flake { Kind = "sunglow", El = glow, Pos = new TranslateTransform(), Turn = new RotateTransform(), X = w - 700, Y = -600 };
                    glow.RenderTransform = f.Pos;
                    Place(f);
                    seasonCanvas.Children.Add(glow);
                    flakes.Add(f);
                    for (int i = 0; i < 16; i++)
                    {
                        var mote = new Ellipse { Width = 3, Height = 3, Fill = Frozen(Color.FromArgb(200, 255, 236, 190)), IsHitTestVisible = false };
                        var m = new Flake { Kind = "mote", El = mote, Pos = new TranslateTransform(), Turn = new RotateTransform(), X = w * (0.4 + r.NextDouble() * 0.6), Y = r.NextDouble() * h * 0.7,
                                            Vy = -(6 + r.NextDouble() * 10), Vx = -(4 + r.NextDouble() * 8), Phase = r.NextDouble() * Math.PI * 2 };
                        mote.RenderTransform = m.Pos;
                        Place(m);
                        seasonCanvas.Children.Add(mote);
                        flakes.Add(m);
                    }
                    break;
                }
            }
        }

        private void StepWeatherFx(Flake f, double t, double dt, double w, double h)
        {
            switch (f.Kind)
            {
                case "drop":
                    f.X += f.Vx * dt;
                    f.Y += f.Vy * dt;
                    if (f.Y > h + 30) { f.Y = -40; f.X = seasonRnd.NextDouble() * (w + 200) - 200; }
                    if (f.X > w + 20) f.X -= w + 40;
                    break;
                case "fog":
                    f.X += f.Vx * dt;
                    if (f.X > w + 50) f.X = -f.Size - 50;
                    f.El.Opacity = 0.7 + 0.3 * Math.Sin(t * 0.25 + f.Phase);
                    break;
                case "sunglow":
                    f.El.Opacity = 0.8 + 0.2 * Math.Sin(t * 0.4);
                    return;
                case "mote":
                    f.X += (f.Vx + Math.Sin(t * 0.7 + f.Phase) * 6) * dt;
                    f.Y += f.Vy * dt;
                    f.El.Opacity = 0.4 + 0.5 * Math.Abs(Math.Sin(t * 0.8 + f.Phase));
                    if (f.Y < -10 || f.X < -10) { f.Y = h * (0.3 + seasonRnd.NextDouble() * 0.4); f.X = w * (0.5 + seasonRnd.NextDouble() * 0.5); }
                    break;
            }
            Place(f);
        }

        /// <summary>Lightning now and then: a quick flash that fades (a double flicker sometimes).</summary>
        private void StepStorm(double dt)
        {
            stormWait -= dt;
            if (stormWait <= 0)
            {
                stormGlow = 0.35 + seasonRnd.NextDouble() * 0.25;
                stormWait = seasonRnd.Next(3) == 0 ? 0.18 : 6 + seasonRnd.NextDouble() * 9;
            }
            stormGlow = Math.Max(0, stormGlow - dt * 1.8);
            stormFlash.Opacity = stormGlow;
        }

        // ---------------- the settings (on the Seasonal touches page) ----------------

        private static readonly string[] DayShort = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        private string DaysText()
        {
            int d = settings.WeatherFxDays & 127;
            if (d == 127) return Loc("LOC_KX_EveryDay", "Every day");
            if (d == 31) return Loc("LOC_KX_Weekdays", "Weekdays");
            if (d == 96) return Loc("LOC_KX_Weekends", "Weekends");
            if (d == 0) return Loc("LOC_KX_NoDays", "No days");
            var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
            var list = new List<string>();
            for (int i = 0; i < 7; i++) if ((d & (1 << i)) != 0) list.Add(names[(i + 1) % 7]);
            return string.Join(", ", list.ToArray());
        }

        // ---------------- the settings page (Home settings › Weather touches) ----------------

        private ObservableCollection<PickRow> weatherFxRows;
        public ObservableCollection<PickRow> WeatherFxRows { get { return weatherFxRows ?? (weatherFxRows = new ObservableCollection<PickRow>()); } }
        public bool IsOptionsWeatherFx { get { return optionsPage == "weatherfx"; } }

        /// <summary>The Weather touches row's second line in Home settings.</summary>
        public string WeatherFxSummary
        {
            get
            {
                if (!settings.WeatherFxOn) return Loc("LOC_KX_Off", "Off");
                if (settings.WeatherFxSync)
                {
                    if (string.IsNullOrEmpty(settings.WeatherTown)) return Loc("LOC_KX_WxSummaryNoTown", "On: set your town to follow its weather");
                    return string.Format(Loc("LOC_KX_WxSummarySync", "Following the weather in {0}{1}"), settings.WeatherTown,
                                         wxLiveWord.Length > 0 ? " · " + wxLiveWord : "");
                }
                NormalizeWeatherFx();
                var picked = Enumerable.Range(0, WeatherFxKinds.Length).Where(WxPicked).Select(i => WeatherFxName(WeatherFxKinds[i])).ToList();
                if (picked.Count == 0) return Loc("LOC_KX_WxSummaryNone", "On: no weather switched on");
                string when = settings.WeatherFxWhen == 1 ? Loc("LOC_KX_WxSummaryDates", "on their dates") : settings.WeatherFxWhen == 2 ? Loc("LOC_KX_WxSummaryTimes", "on set days and times") : Loc("LOC_KX_WxSummaryAlways2", "always");
                return string.Join(", ", picked.ToArray()) + " · " + when;
            }
        }

        private void BuildWeatherFxRows()
        {
            var rows = WeatherFxRows;
            rows.Clear();
            AddWeatherFxRows(rows);
            if (settings.WeatherFxOn) rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "wxinfo", Text = Loc("LOC_KX_WxInfo", "Weather touches pause while a game runs or a trailer plays, like the seasonal ones") });
        }

        /// <summary>
        /// 0.85, laid out like Seasonal touches: the switch, When (follow the weather where you are / always / each on its own
        /// dates / set days and times), then each weather with its own switch (following your weather: which kinds may show),
        /// then the town, the dates or the days and times for the When chosen.
        /// </summary>
        private void AddWeatherFxRows(ObservableCollection<PickRow> rows)
        {
            Action again = () => BuildWeatherFxRows();
            Action changed = () => { SaveSettings(); PlaySound("move"); seasonalBuiltFor = null; Notify("SeasonalShown"); Notify("SeasonalSummary"); Notify("WeatherFxSummary"); UpdateSeasonal(); };
            rows.Add(BoardSwitch("cloud", "wxon", Loc("LOC_KX_WeatherFxOn", "Weather touches"), settings.WeatherFxOn,
                                 Loc("LOC_KX_WeatherFxOnSub", "Rain, storms, snow, fog or sunshine on the home screen"), Loc("LOC_KX_Off", "Off"),
                                 () => { settings.WeatherFxOn = !settings.WeatherFxOn; wxLiveAt = DateTime.MinValue; changed(); BoardRowsAgain(again, "wxon"); }));
            if (!settings.WeatherFxOn) return;
            NormalizeWeatherFx();
            bool follow = settings.WeatherFxSync;
            int when = settings.WeatherFxWhen;
            rows.Add(new PickRow
            {
                Key = "wxwhen", Icon = "calendar",
                Text = follow ? Loc("LOC_KX_WxWhenFollow", "When: the weather where you are")
                     : when == 1 ? Loc("LOC_KX_WxWhenDates", "When: each on its own dates")
                     : when == 2 ? Loc("LOC_KX_WxWhenSet", "When: on set days and times") : Loc("LOC_KX_WxWhenAlways2", "When: always on"),
                Sub = follow ? (string.IsNullOrEmpty(settings.WeatherTown) ? Loc("LOC_KX_WxNoTown", "Set your town below first")
                               : string.Format(Loc("LOC_KX_WxNow", "{0}: {1} now"), settings.WeatherTown, wxLiveWord.Length > 0 ? wxLiveWord : Loc("LOC_KX_WxChecking", "checking…")))
                     : when == 1 ? Loc("LOC_KX_WxWhenDatesSub2", "Like Seasonal touches: each one shows between its dates")
                     : when == 2 ? string.Format("{0}, {1:00}:00–{2:00}:00", DaysText(), settings.WeatherFxFrom, settings.WeatherFxTo)
                     : Loc("LOC_KX_WxWhenAlwaysSub3", "Every one switched on below shows all the time"),
                Command = new RelayCommand(_ => WeatherNudge("wxwhen", 1))
            });
            if (follow)
                rows.Add(new PickRow
                {
                    Key = "wxtown", Icon = "globe", Text = string.IsNullOrEmpty(settings.WeatherTown) ? Loc("LOC_KX_WelcomeTown", "Set your town") : settings.WeatherTown,
                    Sub = Loc("LOC_KX_WxTownSub2", "The same town as the weather widget"),
                    Command = new RelayCommand(_ => { AskWeatherTown(); wxLiveAt = DateTime.MinValue; })
                });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecWeather", "WEATHER") });
            var today = DateTime.Today;
            for (int i = 0; i < WeatherFxKinds.Length; i++)
            {
                int bit = 1 << i;
                string k = WeatherFxKinds[i];
                string icon = k == "sun" ? "sun" : "cloud";
                bool on = follow ? (settings.WeatherFxLivePick & bit) != 0 : WxPicked(i);
                string sub = follow ? Loc("LOC_KX_WxFollowKindSub", "Shows when it's like this where you are")
                           : when == 1 ? string.Format(Loc("LOC_KX_WxDatesRange", "{0} to {1}"), MonthDayText(WxFromOf(i)), MonthDayText(WxToOf(i)))
                                         + (InMonthDays(WxFromOf(i), WxToOf(i), today) ? Loc("LOC_KX_SeasonNow", "  (now)") : "")
                           : Loc("LOC_KX_On", "On");
                rows.Add(BoardSwitch(icon, "wxfx:" + k, WeatherFxName(k), on, sub, Loc("LOC_KX_Off", "Off"),
                                     () => { if (settings.WeatherFxSync) settings.WeatherFxLivePick ^= bit; else settings.WeatherFxPick ^= bit; WeatherFxChanged("wxfx:" + k); }));
            }
            if (follow) return;
            if (when == 1)
            {
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecDates", "DATES") });
                bool any = false;
                for (int i = 0; i < WeatherFxKinds.Length; i++)
                {
                    if (!WxPicked(i)) continue;
                    any = true;
                    string k = WeatherFxKinds[i];
                    string fromKey = "wxdfrom:" + k, toKey = "wxdto:" + k;
                    rows.Add(new PickRow { Key = fromKey, Icon = "calendar", Text = string.Format(Loc("LOC_KX_WxFromDate", "{0} from {1}"), WeatherFxName(k), MonthDayText(WxFromOf(i))),
                                           Sub = Loc("LOC_KX_WxDateSub", "Left / Right: a day earlier or later"), Command = new RelayCommand(_ => WeatherNudge(fromKey, 1)) });
                    rows.Add(new PickRow { Key = toKey, Icon = "calendar", Text = string.Format(Loc("LOC_KX_WxToDate", "{0} until {1}"), WeatherFxName(k), MonthDayText(WxToOf(i))),
                                           Sub = Loc("LOC_KX_WxDateSub", "Left / Right: a day earlier or later"), Command = new RelayCommand(_ => WeatherNudge(toKey, 1)) });
                }
                if (!any) rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "wxnodates", Text = Loc("LOC_KX_WxNoneOn", "Switch a weather on above to set its dates") });
                rows.Add(new PickRow
                {
                    Key = "wxdreset", Icon = "refresh", Text = Loc("LOC_KX_WxDatesReset", "Dates back to how they came"),
                    Sub = Loc("LOC_KX_WxDatesResetSub", "Rain October to March, storms and sunshine in summer, snow December to February, fog mid-October to November"),
                    Command = new RelayCommand(_ => { settings.WeatherFxDates = new List<int>(WxDefaultDates); WeatherFxChanged("wxdreset"); })
                });
                return;
            }
            if (when != 2) return;
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecDaysTimes", "DAYS AND TIMES") });
            var names = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
            for (int i = 0; i < 7; i++)
            {
                int bit = 1 << i;
                string key = "wxday" + i;
                rows.Add(BoardSwitch("calendar", key, names[(i + 1) % 7], (settings.WeatherFxDays & bit) != 0, Loc("LOC_KX_On", "On"), Loc("LOC_KX_Off", "Off"),
                                     () => { settings.WeatherFxDays ^= bit; changed(); BoardRowsAgain(again, key); }));
            }
            rows.Add(new PickRow { Key = "wxfrom", Icon = "clock", Text = string.Format(Loc("LOC_KX_WxFrom", "From {0:00}:00"), settings.WeatherFxFrom), Sub = Loc("LOC_KX_WxHourSub", ""),
                                   Command = new RelayCommand(_ => { settings.WeatherFxFrom = (settings.WeatherFxFrom + 1) % 24; changed(); BoardRowsAgain(again, "wxfrom"); }) });
            rows.Add(new PickRow { Key = "wxto", Icon = "clock", Text = string.Format(Loc("LOC_KX_WxTo", "Until {0:00}:00"), settings.WeatherFxTo), Sub = Loc("LOC_KX_WxToSub", "The same as From: all day"),
                                   Command = new RelayCommand(_ => { settings.WeatherFxTo = (settings.WeatherFxTo + 1) % 24; changed(); BoardRowsAgain(again, "wxto"); }) });
        }
    }
}

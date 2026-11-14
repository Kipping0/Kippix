using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Look and feel › Night mode: Off / Automatic (from–until, 22:00–07:00 to start with) / Always. While it's on, a dark layer
    /// and a warm tint sit over the whole fullscreen window (both amounts can be set), menu sounds and the home screen music
    /// are quieter, and (if chosen) no trailer starts by itself. It fades in and out over a few seconds. Checked once a minute,
    /// only while fullscreen runs and only when Night mode isn't Off.
    /// </summary>
    public partial class ThemeApi
    {
        private DispatcherTimer nightTimer;
        private bool nightOn;
        private Border nightDim, nightWarm;

        private static readonly Color NightWarmColour = Color.FromRgb(0xFF, 0x8A, 0x2E);

        /// <summary>How loud menu sounds and the music are now (1: as set; lower at night).</summary>
        private double NightVolumeFactor { get { return nightOn ? Math.Max(0, 1 - settings.NightQuiet / 100.0) : 1; } }
        /// <summary>The menu sounds' volume (0-100) right now: Audio's volume, quieter at night.</summary>
        private int SoundVolumeNow { get { return (int)Math.Round(settings.SoundVolume * NightVolumeFactor); } }
        /// <summary>The music's volume (0-100) right now.</summary>
        private double MusicVolumeNow { get { return settings.MusicVolume * NightVolumeFactor; } }
        /// <summary>Night mode is on and holds trailers back from starting by themselves.</summary>
        private bool NightNoAutoplayNow { get { return nightOn && settings.NightNoAutoplay; } }
        /// <summary>For other parts of the theme (Look packs, Theme Studio): Night mode is on right now.</summary>
        public bool IsNightNow { get { return nightOn; } }

        /// <summary>Starts the minute check (once fullscreen is up), or stops it when Night mode is Off.</summary>
        private void StartNightMode()
        {
            if (!IsFullscreen || ui == null) return;
            if (settings.NightMode == 0)
            {
                if (nightTimer != null) nightTimer.Stop();
                UpdateNight();
                return;
            }
            if (nightTimer == null)
            {
                nightTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(1) };
                nightTimer.Tick += (s, e) => { try { UpdateNight(); } catch (Exception ex) { logger.Warn("Kippix: night mode: " + ex.Message); } };
            }
            if (!nightTimer.IsEnabled) nightTimer.Start();
            UpdateNight();
        }

        private static bool InNightWindow(int from, int to, DateTime now)
        {
            int m = now.Hour * 60 + now.Minute;
            if (from == to) return true;                    // the same time both ends: all day
            if (from < to) return m >= from && m < to;      // e.g. 01:00–06:00
            return m >= from || m < to;                     // over midnight, e.g. 22:00–07:00
        }

        private bool NightShould
        {
            get
            {
                if (!IsFullscreen) return false;
                if (settings.NightMode == 2) return true;
                return settings.NightMode == 1 && InNightWindow(settings.NightFrom, settings.NightTo, DateTime.Now);
            }
        }

        /// <summary>On or off as it should be now, with a smooth fade; the overlay's amounts follow the settings.</summary>
        private void UpdateNight()
        {
            bool should = NightShould;
            bool changed = should != nightOn;
            nightOn = should;
            ApplyNightOverlay(changed);
            if (changed)
            {
                logger.Info("Kippix: night mode " + (should ? "on" : "off"));
                ApplyNightVolume();
                Notify("NightSummary");
                Notify("IsNightNow");
            }
        }

        private void ApplyNightVolume()
        {
            try { if (music != null && musicPlaying) music.Volume = musicLevel * MusicVolumeNow / 100.0 * 0.6; } catch (Exception) { }
        }

        private void ApplyNightOverlay(bool fade)
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var host = win != null ? UiTools.FindByName(win, "PART_MainHost") as Panel : null;
            if (host == null) return;
            if (nightDim == null || !ReferenceEquals(VisualTreeHelper.GetParent(nightDim), host))
            {
                if (!nightOn) return;   // nothing to take away
                nightDim = new Border { Background = Brushes.Black, IsHitTestVisible = false, Focusable = false, Opacity = 0 };
                nightWarm = new Border { Background = Frozen(NightWarmColour), IsHitTestVisible = false, Focusable = false, Opacity = 0 };
                foreach (var b in new[] { nightWarm, nightDim })
                {
                    Panel.SetZIndex(b, 140);   // over everything the theme shows, under the intro / outro
                    Grid.SetRowSpan(b, 99);
                    Grid.SetColumnSpan(b, 99);
                    host.Children.Add(b);
                }
            }
            double dim = nightOn ? Math.Min(0.7, settings.NightDim / 100.0) : 0;
            double warm = nightOn ? settings.NightWarm / 100.0 * 0.22 : 0;   // a tint: at most a fifth or so of orange over the picture
            var time = TimeSpan.FromMilliseconds(fade ? 2500 : 300);
            nightDim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(dim, time) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            nightWarm.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(warm, time) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        // ---------------- the page ----------------

        public string NightSummary
        {
            get
            {
                if (settings.NightMode == 0) return Loc("LOC_KX_Off", "Off");
                if (settings.NightMode == 2) return Loc("LOC_KX_NightAlways", "Always on");
                string when = string.Format(Loc("LOC_KX_NightFromTo", "{0} to {1}"), HhMm(settings.NightFrom), HhMm(settings.NightTo));
                return nightOn ? string.Format(Loc("LOC_KX_NightOnNow", "On now ({0})"), when) : string.Format(Loc("LOC_KX_NightAuto", "Automatic: {0}"), when);
            }
        }

        private string HhMm(int minutes)
        {
            var t = DateTime.Today.AddMinutes(minutes);
            return settings.Clock24h ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("h:mm tt", CultureInfo.CurrentCulture);
        }

        private string NightModeName(int m)
        {
            if (m == 1) return Loc("LOC_KX_NightModeAuto", "Automatic");
            if (m == 2) return Loc("LOC_KX_NightAlways", "Always on");
            return Loc("LOC_KX_Off", "Off");
        }

        private void NightSettingChanged(string key)
        {
            StartNightMode();
            ApplyNightOverlay(false);
            ApplyNightVolume();
            LookExtraChanged(key);
        }

        private void AddNightRows(ObservableCollection<PickRow> rows)
        {
            rows.Add(new PickRow
            {
                Key = "nightmode", Icon = "moon", Text = string.Format(Loc("LOC_KX_NightModeRow", "Night mode: {0}"), NightModeName(settings.NightMode)),
                Sub = Loc("LOC_KX_NightModeRowSub", "Left / Right: off, automatic between two times, or always on"),
                Command = new RelayCommand(_ => { settings.NightMode = (settings.NightMode + 1) % 3; NightSettingChanged("nightmode"); })
            });
            if (settings.NightMode == 0) { AddNightReset(rows); return; }
            if (settings.NightMode == 1)
            {
                rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecNightWhen", "WHEN") });
                rows.Add(new PickRow { Key = "nightfrom", Icon = "clock", Text = string.Format(Loc("LOC_KX_NightFrom", "From {0}"), HhMm(settings.NightFrom)),
                                       Sub = Loc("LOC_KX_NightTimeSub", "Left / Right: half an hour earlier or later"),
                                       Command = new RelayCommand(_ => NightNudge("nightfrom", 1)) });
                rows.Add(new PickRow { Key = "nightto", Icon = "clock", Text = string.Format(Loc("LOC_KX_NightTo", "Until {0}"), HhMm(settings.NightTo)),
                                       Sub = Loc("LOC_KX_NightTimeSub", "Left / Right: half an hour earlier or later"),
                                       Command = new RelayCommand(_ => NightNudge("nightto", 1)) });
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "nightnow",
                                       Text = nightOn ? Loc("LOC_KX_NightIsOn", "It's on now") : string.Format(Loc("LOC_KX_NightStartsAt", "It comes on at {0}"), HhMm(settings.NightFrom)) });
            }
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecNightHow", "WHILE IT'S ON") });
            rows.Add(new PickRow { Key = "nightdim", Icon = "sun", Text = string.Format(Loc("LOC_KX_NightDim", "Darker: {0}%"), settings.NightDim),
                                   Sub = Loc("LOC_KX_AmountSub", "Left / Right: less or more"), Command = new RelayCommand(_ => NightNudge("nightdim", 1)) });
            rows.Add(new PickRow { Key = "nightwarm", Icon = "palette", Text = string.Format(Loc("LOC_KX_NightWarm", "Warmer colours: {0}%"), settings.NightWarm),
                                   Sub = Loc("LOC_KX_AmountSub", "Left / Right: less or more"), Command = new RelayCommand(_ => NightNudge("nightwarm", 1)) });
            rows.Add(new PickRow { Key = "nightquiet", Icon = "speaker", Text = string.Format(Loc("LOC_KX_NightQuiet", "Quieter sounds and music: {0}%"), settings.NightQuiet),
                                   Sub = Loc("LOC_KX_NightQuietSub", "Menu sounds and the home screen music (Left / Right: less or more)"), Command = new RelayCommand(_ => NightNudge("nightquiet", 1)) });
            rows.Add(BoardSwitch("video", "nightnoplay", Loc("LOC_KX_NightNoAutoplay", "No trailers by themselves"), settings.NightNoAutoplay,
                                 Loc("LOC_KX_NightNoAutoplayOn", "Trailers, previews and live backgrounds wait for you to press play"), Loc("LOC_KX_NightNoAutoplayOff", "Trailers start as usual"),
                                 () => { settings.NightNoAutoplay = !settings.NightNoAutoplay; NightSettingChanged("nightnoplay"); }));
            AddNightReset(rows);
        }

        private void AddNightReset(ObservableCollection<PickRow> rows)
        {
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecReset", "RESET") });
            rows.Add(new PickRow
            {
                Key = "nightreset", Icon = "refresh", Text = Loc("LOC_KX_ResetPage", "Reset this page"), Sub = Loc("LOC_KX_ResetPageSub", "Resets this page to default"),
                Command = new RelayCommand(_ =>
                {
                    settings.NightMode = 0; settings.NightFrom = 22 * 60; settings.NightTo = 7 * 60;
                    settings.NightDim = 30; settings.NightWarm = 40; settings.NightQuiet = 50; settings.NightNoAutoplay = false;
                    NightSettingChanged("nightreset");
                })
            });
        }

        /// <summary>Left / Right (and A) on Night mode's rows.</summary>
        private bool NightNudge(string key, int dir)
        {
            switch (key)
            {
                case "nightmode": settings.NightMode = (settings.NightMode + dir + 3) % 3; break;
                case "nightfrom": settings.NightFrom = ((settings.NightFrom + dir * 30) % 1440 + 1440) % 1440; break;
                case "nightto": settings.NightTo = ((settings.NightTo + dir * 30) % 1440 + 1440) % 1440; break;
                case "nightdim":
                {
                    int v = settings.NightDim + dir * 5;
                    if (v > 70 && dir > 0) v = settings.NightDim == 70 ? 0 : 70;   // (A past the top starts again)
                    settings.NightDim = Math.Max(0, Math.Min(70, v));
                    break;
                }
                case "nightwarm":
                {
                    int v = settings.NightWarm + dir * 10;
                    if (v > 100 && dir > 0) v = settings.NightWarm == 100 ? 0 : 100;
                    settings.NightWarm = Math.Max(0, Math.Min(100, v));
                    break;
                }
                case "nightquiet":
                {
                    int v = settings.NightQuiet + dir * 10;
                    if (v > 100 && dir > 0) v = settings.NightQuiet == 100 ? 0 : 100;
                    settings.NightQuiet = Math.Max(0, Math.Min(100, v));
                    break;
                }
                default: return false;
            }
            NightSettingChanged(key);
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// The achievements screen's Background › Game background (the default): behind the screen, the background art of the game
    /// that's highlighted (the games page) or open (a game's page), changing as you move, faded in over the last one and dimmed
    /// so the text stays easy to read. One picture at a time (the last one only stays underneath while the new one fades in).
    /// (r13) Faster: a short rest (100 ms, on a normal-priority timer so a busy screen can't hold it back), decoded on the screen's
    /// own picture thread (never queued behind other work in the shared thread pool) at no more than 1920 px (it's dimmed to
    /// under a quarter anyway), the last 8 kept, and the games next to the highlighted one decoded ahead while you rest.
    /// The thread only lives while the screen is being used (it ends after a few seconds with nothing to do, or when it closes).
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>0 game background, 1 see-through, 2 solid.</summary>
        private int AchBgMode { get { int m = settings.AchBgMode; return m < 0 || m > 2 ? 0 : m; } }
        public bool IsAchGameBg { get { return AchBgMode == 0; } }

        private string AchBgModeText
        {
            get
            {
                return AchBgMode == 2 ? Loc("LOC_KX_Solid", "Solid") : AchBgMode == 1 ? Loc("LOC_KX_SeeThrough", "See-through") : Loc("LOC_KX_AchBgGame", "Game background");
            }
        }

        private Guid achBgWant = Guid.Empty;    // the game whose picture should show
        private Guid achBgShown = Guid.Empty;   // the game whose picture is showing (or was looked for and has none)
        private int achBgFadeGen;
        private DispatcherTimer achBgTimer;
        private Image achBgFront, achBgBack;
        private ItemsControl achBgList;
        private readonly Stopwatch achBgClock = new Stopwatch();   // (debug log: highlight landed → picture showing)
        // the last few pictures (moving back and forth doesn't decode them again); emptied when the screen closes. UI thread only.
        private readonly List<KeyValuePair<Guid, ImageSource>> achBgCache = new List<KeyValuePair<Guid, ImageSource>>();
        private const int AchBgCacheSize = 8;
        private const int AchBgRestMs = 100;

        // ---- the picture thread: the wanted picture first, then the neighbours (all under achBgLock) ----
        private readonly object achBgLock = new object();
        private Guid achBgJob = Guid.Empty;                         // the one to decode next (the highlighted game)
        private readonly List<Guid> achBgAhead = new List<Guid>();  // then these (the games around it), dropped when you move on
        private Guid achBgBusy = Guid.Empty;                        // the one the thread is decoding now
        private Thread achBgThread;
        private AutoResetEvent achBgSignal;                          // the running thread's own wake-up
        private int achBgSession;                                    // the screen closing makes the running thread (and its result) stale

        /// <summary>The settings dropdown's Background row: Game background, then See-through, then Solid.</summary>
        private void CycleAchBgMode()
        {
            settings.AchBgMode = (AchBgMode + 1) % 3;
            settings.AchSolidBackground = settings.AchBgMode == 2;
            SaveSettings();
            Notify("AchBackground");
            Notify("IsAchGameBg");
            if (IsAchGameBg)
            {
                // straight away, for what's highlighted now (or the game that's open)
                Guid id = achPage != "games" && achGame != null ? achGame.Id : FocusedAchGameId();
                if (id != Guid.Empty) WantAchBg(id, true);
            }
            else ClearAchBg();
        }

        /// <summary>The highlight moved on the achievements screen (from the window's focus handler). Kept tiny: it runs on every move.</summary>
        private void AchBgFocusChanged(object newFocus)
        {
            if (!IsAchGameBg) return;
            var fe = newFocus as FrameworkElement;
            if (fe == null) return;
            var row = fe.DataContext as AchGameRow;
            if (row != null && row.Data != null) { WantAchBg(row.Data.Id, false); return; }
            // (anything else, the chips, the dropdowns: the picture stays as it is)
        }

        private Guid FocusedAchGameId()
        {
            try
            {
                var fe = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                var row = fe != null ? fe.DataContext as AchGameRow : null;
                if (row != null && row.Data != null) return row.Data.Id;
            }
            catch (Exception) { }
            return Guid.Empty;
        }

        /// <summary>Shows this game's picture: at once when it's open (or already decoded), after a short rest while you scroll.</summary>
        private void WantAchBg(Guid id, bool now)
        {
            if (!IsAchGameBg || !IsFullscreen || id == Guid.Empty) return;
            if (id == achBgWant && !now && achBgTimer != null && achBgTimer.IsEnabled) return;   // (the same card again: the rest goes on)
            achBgWant = id;
            achBgClock.Restart();
            if (id == achBgShown) { StopAchBgTimer(); return; }
            ImageSource hit;
            if (CachedAchBg(id, out hit)) { StopAchBgTimer(); ShowAchBg(id, hit); AchBgLookAhead(id); return; }
            lock (achBgLock) achBgAhead.Clear();   // moving on: the old neighbours aren't wanted any more
            if (now) { StopAchBgTimer(); RequestAchBg(id); return; }
            if (achBgTimer == null)
            {
                // Normal priority (it was Background: a busy screen, e.g. covers arriving or a layout pass, could hold it back)
                achBgTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(AchBgRestMs) };
                achBgTimer.Tick += (s, e) => { achBgTimer.Stop(); RequestAchBg(achBgWant); };
            }
            achBgTimer.Stop();
            achBgTimer.Start();
        }

        private void StopAchBgTimer() { if (achBgTimer != null) achBgTimer.Stop(); }

        /// <summary>Rested on a game: its picture goes to the front of the picture thread (it shows the moment it's decoded).</summary>
        private void RequestAchBg(Guid id)
        {
            if (id == Guid.Empty || id != achBgWant || openPanel != "achievements" || !IsAchGameBg) return;
            if (id == achBgShown) return;
            ImageSource hit;
            if (CachedAchBg(id, out hit)) { ShowAchBg(id, hit); AchBgLookAhead(id); return; }
            lock (achBgLock)
            {
                if (achBgBusy != id) achBgJob = id;   // (already being decoded ahead: it shows when that's done)
                achBgAhead.Remove(id);
            }
            AchBgWake();
            AchBgLookAhead(id);
        }

        /// <summary>The games next to this one (left, right, the row above and below) decoded ahead, after the wanted one.</summary>
        private void AchBgLookAhead(Guid id)
        {
            try
            {
                if (achPage != "games" || AchGames == null || AchGames.Count < 2) return;
                int at = -1;
                for (int i = 0; i < AchGames.Count; i++) if (AchGames[i] != null && AchGames[i].Data != null && AchGames[i].Data.Id == id) { at = i; break; }
                if (at < 0) return;
                int cols = 1;
                if (achBgList == null)
                {
                    var win = Application.Current != null ? Application.Current.MainWindow : null;
                    achBgList = win != null ? UiTools.FindByName(win, "KX_AchGameList") as ItemsControl : null;
                }
                if (achBgList != null && achBgList.ActualWidth > 0) cols = Math.Max(1, (int)(achBgList.ActualWidth / Math.Max(100, AchGameCardWidth + 10)));
                var want = new List<Guid>();
                foreach (int d in new[] { 1, -1, cols, -cols })
                {
                    int j = at + d;
                    if (j < 0 || j >= AchGames.Count || j == at) continue;
                    var r = AchGames[j];
                    if (r == null || r.Data == null) continue;
                    ImageSource hit;
                    if (!want.Contains(r.Data.Id) && !CachedAchBg(r.Data.Id, out hit)) want.Add(r.Data.Id);
                }
                if (want.Count == 0) return;
                lock (achBgLock)
                {
                    achBgAhead.Clear();
                    achBgAhead.AddRange(want);
                }
                AchBgWake();
            }
            catch (Exception) { }
        }

        /// <summary>Wakes the picture thread (starting it when there isn't one).</summary>
        private void AchBgWake()
        {
            lock (achBgLock)
            {
                if (achBgThread == null)
                {
                    int session = achBgSession;
                    var signal = new AutoResetEvent(false);
                    achBgSignal = signal;
                    var t = new Thread(() => AchBgWorker(session, signal)) { IsBackground = true, Name = "Kippix achievements background" };
                    achBgThread = t;
                    t.Start();
                }
                if (achBgSignal != null) achBgSignal.Set();
            }
        }

        /// <summary>The picture thread: the wanted picture first, then the neighbours; ends after a few seconds with nothing to do.</summary>
        private void AchBgWorker(int session, AutoResetEvent signal)
        {
            int px = 1920;
            try { px = (int)Math.Min(1920, Math.Max(1280, SystemParameters.PrimaryScreenWidth)); } catch (Exception) { }
            while (true)
            {
                Guid id = Guid.Empty;
                bool wanted = false;
                lock (achBgLock)
                {
                    if (session != achBgSession) { if (ReferenceEquals(achBgThread, Thread.CurrentThread)) achBgThread = null; return; }
                    if (achBgJob != Guid.Empty) { id = achBgJob; achBgJob = Guid.Empty; wanted = true; }
                    else if (achBgAhead.Count > 0) { id = achBgAhead[0]; achBgAhead.RemoveAt(0); }
                    achBgBusy = id;
                }
                if (id == Guid.Empty)
                {
                    if (signal.WaitOne(6000)) continue;
                    lock (achBgLock)
                    {
                        if (session == achBgSession && (achBgJob != Guid.Empty || achBgAhead.Count > 0)) continue;   // (one came in just as it timed out)
                        if (ReferenceEquals(achBgThread, Thread.CurrentThread)) achBgThread = null;
                    }
                    return;
                }
                ImageSource img = null;
                var took = Stopwatch.StartNew();
                try
                {
                    string path = AchBgPath(id);
                    if (path != null) img = Images.LoadUncached(path, px);
                }
                catch (Exception) { }
                took.Stop();
                lock (achBgLock) if (achBgBusy == id) achBgBusy = Guid.Empty;
                var got = img;
                var gid = id;
                bool first = wanted;
                long ms = took.ElapsedMilliseconds;
                OnUi(() =>
                {
                    if (session != achBgSession || openPanel != "achievements" || !IsAchGameBg) return;
                    RememberAchBg(gid, got);
                    if (achBgWant == gid && achBgShown != gid)
                    {
                        ShowAchBg(gid, got);
                        Dbg("achievements background: showing " + achBgClock.ElapsedMilliseconds + " ms after the highlight landed (decoded in " + ms + " ms" + (first ? "" : ", ahead") + ")");
                        AchBgLookAhead(gid);
                    }
                });
            }
        }

        /// <summary>The game's background art (a file in Playnite's library, or a web address); null when it has none. Any thread.</summary>
        private string AchBgPath(Guid id)
        {
            try
            {
                Game g = api.Database.Games.Get(id);
                if (g == null || string.IsNullOrEmpty(g.BackgroundImage)) return null;
                if (g.BackgroundImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return g.BackgroundImage;
                string f = api.Database.GetFullFilePath(g.BackgroundImage);
                return !string.IsNullOrEmpty(f) && File.Exists(f) ? f : null;
            }
            catch (Exception) { return null; }
        }

        private bool CachedAchBg(Guid id, out ImageSource img)
        {
            for (int i = 0; i < achBgCache.Count; i++)
                if (achBgCache[i].Key == id) { img = achBgCache[i].Value; return true; }
            img = null;
            return false;
        }

        private void RememberAchBg(Guid id, ImageSource img)
        {
            achBgCache.RemoveAll(p => p.Key == id);
            achBgCache.Add(new KeyValuePair<Guid, ImageSource>(id, img));
            while (achBgCache.Count > AchBgCacheSize)
            {
                // the oldest goes, but never the one showing or wanted
                int drop = 0;
                while (drop < achBgCache.Count - 1 && (achBgCache[drop].Key == achBgShown || achBgCache[drop].Key == achBgWant)) drop++;
                achBgCache.RemoveAt(drop);
            }
        }

        private bool FindAchBgImages()
        {
            if (achBgFront != null && achBgBack != null) return true;
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            if (win == null) return false;
            achBgFront = UiTools.FindByName(win, "KX_AchBgImage") as Image;
            achBgBack = UiTools.FindByName(win, "KX_AchBgBack") as Image;
            if (achBgFront == null || achBgBack == null) { achBgFront = achBgBack = null; return false; }
            return true;
        }

        /// <summary>The new picture fades in over the one showing (which goes once it's covered); none: the last one fades away.</summary>
        private void ShowAchBg(Guid id, ImageSource img)
        {
            achBgShown = id;
            if (!FindAchBgImages()) return;
            var front = achBgFront;
            var back = achBgBack;
            int fade = ++achBgFadeGen;
            double was = front.Source != null ? front.Opacity : 0;   // (mid-fade: where it's got to)
            back.BeginAnimation(UIElement.OpacityProperty, null);
            back.Source = front.Source;
            back.Opacity = was;
            front.BeginAnimation(UIElement.OpacityProperty, null);
            front.Source = img;
            front.Opacity = 0;
            if (img != null)
            {
                var a = new DoubleAnimation(0, 1, Motion(360)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
                a.Completed += (s, e) => { if (fade == achBgFadeGen) { back.Source = null; back.Opacity = 0; } };   // covered now: let it go
                front.BeginAnimation(UIElement.OpacityProperty, a);
            }
            else if (back.Source != null)
            {
                var a = new DoubleAnimation(was, 0, Motion(300)) { FillBehavior = FillBehavior.HoldEnd };
                a.Completed += (s, e) => { if (fade == achBgFadeGen) { back.BeginAnimation(UIElement.OpacityProperty, null); back.Source = null; back.Opacity = 0; } };
                back.BeginAnimation(UIElement.OpacityProperty, a);
            }
        }

        /// <summary>No picture (another background chosen, or the screen closing).</summary>
        private void ClearAchBg()
        {
            achBgFadeGen++;
            achBgWant = achBgShown = Guid.Empty;
            StopAchBgTimer();
            lock (achBgLock) { achBgJob = Guid.Empty; achBgAhead.Clear(); }
            if (achBgFront == null && achBgBack == null) return;
            foreach (var im in new[] { achBgFront, achBgBack })
            {
                if (im == null) continue;
                im.BeginAnimation(UIElement.OpacityProperty, null);
                im.Source = null;
                im.Opacity = 0;
            }
        }

        /// <summary>The achievements screen is closing: the pictures go (it starts on the highlighted game's again next time).</summary>
        private void LeaveAchBackground()
        {
            try
            {
                ClearAchBg();
                achBgCache.Clear();
                lock (achBgLock)
                {
                    achBgSession++;   // the picture thread ends at its next turn; anything it's decoding is dropped
                    if (achBgSignal != null) achBgSignal.Set();
                    achBgSignal = null;
                    achBgThread = null;   // (opening the screen again starts a fresh one straight away)
                }
            }
            catch (Exception ex) { logger.Warn("Kippix: achievements background: " + ex.Message); }
        }
    }
}

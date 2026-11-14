using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Events;
using Playnite.SDK.Models;

namespace KippixHelper
{
    /// <summary>
    /// Pinned game tiles: a game kept in an exact spot among the games (One row, Grid and List), e.g. always the first card.
    /// Different from "Pin to the home screen" (a tile before the games). Playnite's list keeps its own sort for everything
    /// else: the helper sorts Playnite's list the same way, with the kept games put in their spots.
    /// Pin / unpin from the game's More menu, or hold X on its card. While a Playnite filter is on, kept games are either
    /// left where the filter puts them, or kept in their spots and always shown (Settings).
    /// </summary>
    public partial class ThemeApi
    {
        public bool IsOptionsSlotPins { get { return optionsPage == "slotpins" || optionsPage == "slotpin"; } }
        private ObservableCollection<PickRow> slotPinRows;
        public ObservableCollection<PickRow> SlotPinRows { get { return slotPinRows ?? (slotPinRows = new ObservableCollection<PickRow>()); } }
        private string slotPinEditing;   // the game id whose spot is being changed (page "slotpin")

        private int SlotOf(Guid id)
        {
            if (settings.SlotPins == null || id == Guid.Empty) return 0;
            string s = id.ToString();
            var p = settings.SlotPins.FirstOrDefault(x => x.GameId == s);
            return p != null ? p.Slot : 0;
        }

        // ---------------- Playnite's list ----------------

        private ListCollectionView slotView;
        private SlotSorter slotSorter;
        private List<SortDescription> slotSavedSorts;
        private IComparer slotSavedCustom;
        private bool slotApplying, slotSoon;
        private string slotSig;

        private class SlotSorter : IComparer
        {
            public Dictionary<object, int> Rank;
            public IComparer Fallback;
            public bool Missed;   // an item the ranking doesn't know (added or replaced since): ranked again on the next tick
            public int Compare(object a, object b)
            {
                // called by WPF inside Playnite's own list updates: must never throw
                if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
                int ra, rb;
                bool ha = Rank.TryGetValue(a, out ra), hb = Rank.TryGetValue(b, out rb);
                if (!ha || !hb) Missed = true;
                if (ha && hb) return ra.CompareTo(rb);
                if (ha) return -1;
                if (hb) return 1;
                return Fallback.Compare(a, b);
            }
        }

        /// <summary>Playnite's own order (its sort settings), for everything that isn't kept in a spot.</summary>
        private class DescComparer : IComparer
        {
            private readonly List<SortDescription> sorts;
            private readonly IComparer custom;
            private readonly Dictionary<string, PropertyInfo> props = new Dictionary<string, PropertyInfo>();
            public DescComparer(List<SortDescription> sorts, IComparer custom) { this.sorts = sorts ?? new List<SortDescription>(); this.custom = custom; }
            private object Get(object o, string name)
            {
                if (o == null || string.IsNullOrEmpty(name)) return null;
                PropertyInfo p;
                string key = o.GetType().FullName + "|" + name;
                if (!props.TryGetValue(key, out p)) { p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); props[key] = p; }
                try { return p != null ? p.GetValue(o, null) : null; } catch (Exception) { return null; }
            }
            public int Compare(object a, object b)
            {
                if (custom != null) return custom.Compare(a, b);
                foreach (var s in sorts)
                {
                    object x = Get(a, s.PropertyName), y = Get(b, s.PropertyName);
                    int c;
                    if (x == null && y == null) c = 0;
                    else if (x == null) c = -1;
                    else if (y == null) c = 1;
                    else if (x is string || y is string) c = string.Compare(x.ToString(), y.ToString(), StringComparison.CurrentCultureIgnoreCase);
                    else
                    {
                        try { c = Comparer.Default.Compare(x, y); } catch (Exception) { c = 0; }
                    }
                    if (c != 0) return s.Direction == ListSortDirection.Descending ? -c : c;
                }
                return 0;
            }
        }

        private ListCollectionView HomeListView()
        {
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var list = win != null ? UiTools.FindByName(win, "PART_ListGameItems") as ListBox : null;
            if (list == null || list.ItemsSource == null) return null;
            return list.ItemsSource as ListCollectionView ?? CollectionViewSource.GetDefaultView(list.ItemsSource) as ListCollectionView;
        }

        private bool PlayniteFilterActive()
        {
            try
            {
                var f = PlayniteFilterSettings();
                object v = f != null ? Prop(f, "IsActive") : null;
                return v is bool && (bool)v;
            }
            catch (Exception) { return false; }
        }

        private void ApplySlotPinsSoon()
        {
            if (slotSoon || ui == null) return;
            slotSoon = true;
            ui.BeginInvoke(new Action(() => { slotSoon = false; ApplySlotPins(false); }), DispatcherPriority.Background);
        }

        /// <summary>Puts the kept games in their spots (from the 2-second tick, and straight away when something changes).</summary>
        private void ApplySlotPins(bool force)
        {
            if (slotApplying || !IsFullscreen) return;
            // nothing kept and nothing of ours on the list: no need to look for it (this runs every 2 seconds)
            if ((settings.SlotPins == null || settings.SlotPins.Count == 0) && slotSorter == null && !slotsAlwaysShow) return;
            try
            {
                slotApplying = true;
                var view = HomeListView();
                if (view == null) return;
                if (!ReferenceEquals(view, slotView))
                {
                    slotView = view;
                    slotSorter = null;
                    slotSig = null;
                    ((System.Collections.Specialized.INotifyCollectionChanged)view).CollectionChanged += (s, e) =>
                    {
                        if (!slotApplying && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) ApplySlotPinsSoon();
                    };
                }
                bool filtered = PlayniteFilterActive() && folderFilter == null;   // (in a folder Playnite's filter is set aside)
                // (an opened folder with its own Sort by: its order, no spots kept; ThemeApi.Folders.cs)
                bool want = settings.SlotPins != null && settings.SlotPins.Count > 0 && !IsBoardMode && !FolderSortWanted && (settings.SlotPinsWhenFiltered || !filtered);
                bool ours = slotSorter != null && ReferenceEquals(view.CustomSort, slotSorter);

                // the filter: kept games always pass it (only when they're kept in place while filtering)
                bool wrap = want && filtered && settings.SlotPinsWhenFiltered;
                if (wrap != slotsAlwaysShow)
                {
                    // one filter on Playnite's list for this and the profile's hidden games (ThemeApi.Profiles.cs)
                    slotsAlwaysShow = wrap;
                    slotSig = null;
                    ApplyListFilter(true);
                }

                if (!want)
                {
                    if (ours)
                    {
                        // back to Playnite's own sort
                        view.CustomSort = slotSavedCustom;
                        if (slotSavedCustom == null && slotSavedSorts != null)
                            using (view.DeferRefresh()) { view.SortDescriptions.Clear(); foreach (var sd in slotSavedSorts) view.SortDescriptions.Add(sd); }
                    }
                    slotSorter = null;
                    slotSig = null;
                    return;
                }
                if (!ours)
                {
                    // Playnite's sort now (it may have changed it since): remembered, and used for everything else
                    slotSavedSorts = view.SortDescriptions.ToList();
                    slotSavedCustom = view.CustomSort is SlotSorter ? null : view.CustomSort;
                    slotSig = null;
                }
                string sig = string.Join(",", settings.SlotPins.Select(p => p.GameId + ":" + p.Slot).ToArray()) + "|" + filtered + "|" + view.Count
                           + "|" + string.Join(",", (slotSavedSorts ?? new List<SortDescription>()).Select(d => d.PropertyName + d.Direction).ToArray());
                if (!force && ours && sig == slotSig && !slotSorter.Missed) return;
                slotSig = sig;

                var fallback = new DescComparer(slotSavedSorts, slotSavedCustom);
                var source = view.SourceCollection.Cast<object>().ToList();
                var filter = view.Filter;
                var shown = source.Where(o => filter == null || filter(o)).ToList();
                var kept = shown.Select(o => new { o, slot = SlotOf(EntryId(o)) }).Where(x => x.slot > 0).OrderBy(x => x.slot).ToList();
                var order = new Dictionary<object, int>();
                for (int i = 0; i < source.Count; i++) order[source[i]] = i;
                // Playnite's order (ties keep the list's own order)
                var rest = shown.Where(o => SlotOf(EntryId(o)) == 0)
                                .OrderBy(o => o, Comparer<object>.Create((a, b) => { int c = fallback.Compare(a, b); return c != 0 ? c : order[a].CompareTo(order[b]); }))
                                .ToList();
                foreach (var k in kept) rest.Insert(Math.Min(rest.Count, k.slot - 1), k.o);
                var rank = new Dictionary<object, int>();
                for (int i = 0; i < rest.Count; i++) rank[rest[i]] = i;
                var sorter = new SlotSorter { Rank = rank, Fallback = fallback };
                slotSorter = sorter;
                view.CustomSort = sorter;
            }
            catch (Exception ex) { logger.Warn("Kippix: pinned tiles: " + ex.Message); }
            finally { slotApplying = false; }
        }

        // ---------------- pinning ----------------

        /// <summary>Where the game's card is now (1 = first), 0 if it isn't in the list.</summary>
        private int CardNumberOf(Guid id)
        {
            var view = HomeListView();
            if (view == null) return 0;
            int i = 0;
            foreach (object o in view) { i++; if (EntryId(o) == id) return i; }
            return 0;
        }

        private void PinToSlot(Game g, int slot)
        {
            if (g == null) return;
            string s = g.Id.ToString();
            settings.SlotPins.RemoveAll(p => p.GameId == s);
            if (slot > 0)
            {
                // another game kept in that spot (and any after it) moves along one
                if (settings.SlotPins.Any(q => q.Slot == slot))
                    foreach (var q in settings.SlotPins.Where(q => q.Slot >= slot)) q.Slot++;
                settings.SlotPins.Add(new SlotPin { GameId = s, Slot = slot });
            }
            SaveSettings();
            ApplySlotPins(true);
            RefreshBadgesSoon();
        }

        /// <summary>Hold X on a card: kept in the spot it's in now (or let go again).</summary>
        private void ToggleSlotPinHere(Guid id)
        {
            Game g = null;
            try { g = api.Database.Games.Get(id); } catch (Exception) { }
            if (g == null) { PlaySound("back"); return; }
            int now = SlotOf(id);
            if (now > 0) { PlaySound("back"); PinToSlot(g, 0); return; }
            int at = Math.Max(1, CardNumberOf(id));
            PlaySound("select");
            PinToSlot(g, at);
            try { ShowNotice(Loc("LOC_KX_SlotPinned", "KEPT IN PLACE"), g.Name, string.Format(Loc("LOC_KX_SlotPinnedLine", "Always card {0}"), at), Loc("LOC_KX_SlotPinnedHow", "Hold X on it again to let it go"), null); } catch (Exception) { }
        }

        /// <summary>The More menu's row for it.</summary>
        private void AddSlotPinRow(Game g)
        {
            if (g == null || IsBoardMode) return;
            var game = g;
            int slot = SlotOf(g.Id);
            int here = CardNumberOf(g.Id);
            GameMoreRows.Add(new PickRow
            {
                Text = slot > 0 ? string.Format(Loc("LOC_KX_SlotUnpin", "Stop keeping it as card {0}"), slot) : Loc("LOC_KX_SlotPin", "Keep it in this spot"),
                Sub = slot > 0 ? Loc("LOC_KX_SlotUnpinSub", "It goes back to where Playnite's sort puts it") : string.Format(Loc("LOC_KX_SlotPinSub", "Always card {0}, however the games are sorted (or hold X on its card)"), Math.Max(1, here)),
                Icon = "pin", Key = "slotpin",
                Command = new RelayCommand(_ => { ClosePanels(); PlaySound(slot > 0 ? "back" : "select"); PinToSlot(game, slot > 0 ? 0 : Math.Max(1, here)); })
            });
        }

        // ---------------- hold X on a card ----------------

        private DispatcherTimer holdXTimer;
        private bool holdXWaiting;
        private IInputElement holdXFocus;

        private bool HoldXApplies()
        {
            if (openPanel != null || confirmOpen || freeMove || IsMasterMoving || descReaderOpen || detailsOpen || IsBoardMode || kbOpen) return false;
            var f = Keyboard.FocusedElement as DependencyObject;
            if (f == null) return false;
            var list = UiTools.FindByName(Application.Current.MainWindow, "PART_ListGameItems");
            return list != null && list.IsAncestorOf(f);
        }

        /// <summary>X pressed: wait to see if it's held (a tap is played back when X is let go, so Playnite's menu opens as usual).</summary>
        private bool HoldXDown(KeyEventArgs e)
        {
            if (!HoldXApplies()) return false;
            e.Handled = true;
            padHandled = e;
            holdXWaiting = true;
            holdXFocus = Keyboard.FocusedElement;
            if (holdXTimer == null)
            {
                holdXTimer = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(800) };
                holdXTimer.Tick += (s, a) =>
                {
                    holdXTimer.Stop();
                    if (!holdXWaiting) return;
                    holdXWaiting = false;
                    try
                    {
                        var fe = holdXFocus as FrameworkElement;
                        Guid id = fe != null ? EntryId(fe.DataContext) : Guid.Empty;
                        if (id == Guid.Empty) id = SelectedGameId();
                        ToggleSlotPinHere(id);
                    }
                    catch (Exception ex) { logger.Warn("Kippix: hold X: " + ex.Message); }
                };
            }
            holdXTimer.Stop();
            holdXTimer.Start();
            return true;
        }

        private bool HoldXUp(KeyEventArgs e)
        {
            if (!holdXWaiting) return false;
            holdXWaiting = false;
            if (holdXTimer != null) holdXTimer.Stop();
            e.Handled = true;
            ReplayButton(e.GetType(), ControllerInput.X, holdXFocus);
            return true;
        }

        // ---------------- settings ----------------

        private void BuildSlotPinRows()
        {
            var rows = SlotPinRows;
            rows.Clear();
            if (optionsPage == "slotpin" && slotPinEditing != null) { BuildOneSlotPinRows(); return; }
            // (Row › Tall / Full Screen draw their own cards, which have no pin badge)
            if (RowCardBadgesShown)
            rows.Add(BoardSwitch("pin", "slotbadge", Loc("LOC_KX_SlotBadge", "Pin badge on the card"), settings.SlotPinBadge,
                                 Loc("LOC_KX_SlotBadgeOn", "A small pin on cards kept in place"), Loc("LOC_KX_Off", "Off"),
                                 () => { settings.SlotPinBadge = !settings.SlotPinBadge; SaveSettings(); PlaySound("move"); RefreshBadgesSoon(); BoardRowsAgain(BuildSlotPinRows, "slotbadge"); }));
            rows.Add(new PickRow
            {
                Key = "slotfilter", Icon = "tag",
                Text = settings.SlotPinsWhenFiltered ? Loc("LOC_KX_SlotFilterKeep", "While a filter is on: keep them in place") : Loc("LOC_KX_SlotFilterSkip", "While a filter is on: skip them"),
                Sub = settings.SlotPinsWhenFiltered ? Loc("LOC_KX_SlotFilterKeepSub", "They stay in their spots and always show, whatever the filter")
                                                    : Loc("LOC_KX_SlotFilterSkipSub", "Playnite's filter and sort decide, as if they weren't kept"),
                Command = new RelayCommand(_ => { settings.SlotPinsWhenFiltered = !settings.SlotPinsWhenFiltered; SaveSettings(); PlaySound("move"); ApplySlotPins(true); BoardRowsAgain(BuildSlotPinRows, "slotfilter"); })
            });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SlotKept", "KEPT IN PLACE") });
            if (settings.SlotPins.Count == 0)
                rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "slotnone", Text = Loc("LOC_KX_SlotNone", "None yet: hold X on a card, or ⋯ More › Keep it in this spot") });
            foreach (var p in settings.SlotPins.OrderBy(x => x.Slot).ToList())
            {
                Guid id;
                Game g = null;
                if (Guid.TryParse(p.GameId, out id)) try { g = api.Database.Games.Get(id); } catch (Exception) { }
                string gid = p.GameId;
                rows.Add(new PickRow
                {
                    Key = "slot:" + gid, Icon = "pin",
                    Text = string.Format(Loc("LOC_KX_SlotRow", "Card {0}: {1}"), p.Slot, g != null ? g.Name : "?"),
                    Sub = Loc("LOC_KX_SlotRowSub", "Move it or let it go"),
                    Command = new RelayCommand(_ => { slotPinEditing = gid; PlaySound("select"); SetOptionsPage("slotpin"); })
                });
            }
        }

        private void BuildOneSlotPinRows()
        {
            var rows = SlotPinRows;
            var p = settings.SlotPins.FirstOrDefault(x => x.GameId == slotPinEditing);
            if (p == null) { rows.Add(new PickRow { IsInfo = true, Text = Loc("LOC_KX_SlotGone", "It isn't kept in place any more") }); return; }
            Action again = () => BuildSlotPinRows();
            rows.Add(new PickRow { IsInfo = true, Icon = "pin", Key = "slotnow", Text = string.Format(Loc("LOC_KX_SlotNow", "Card {0}"), p.Slot) });
            rows.Add(new PickRow
            {
                Key = "slotearlier", Icon = "rewind", Text = Loc("LOC_KX_SlotEarlier", "One card earlier"),
                Command = new RelayCommand(_ => { if (p.Slot <= 1) { PlaySound("back"); return; } MoveSlot(p, -1); BoardRowsAgain(again, "slotearlier"); })
            });
            rows.Add(new PickRow
            {
                Key = "slotlater", Icon = "forward", Text = Loc("LOC_KX_SlotLater", "One card later"),
                Command = new RelayCommand(_ => { MoveSlot(p, 1); BoardRowsAgain(again, "slotlater"); })
            });
            rows.Add(new PickRow
            {
                Key = "slotfirst", Icon = "star", Text = Loc("LOC_KX_SlotFirst", "Make it the first card"),
                Command = new RelayCommand(_ => { MoveSlot(p, 1 - p.Slot); BoardRowsAgain(again, "slotfirst"); })
            });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecRemove", "REMOVE") });
            rows.Add(new PickRow
            {
                Key = "slotremove", Icon = "remove", Text = Loc("LOC_KX_SlotLetGo", "Stop keeping it in place"),
                Command = new RelayCommand(_ =>
                {
                    settings.SlotPins.Remove(p);
                    SaveSettings();
                    PlaySound("back");
                    ApplySlotPins(true);
                    RefreshBadgesSoon();
                    GoBackTo("slotpins", "slotfilter");
                })
            });
        }

        private void MoveSlot(SlotPin p, int by)
        {
            int to = Math.Max(1, p.Slot + by);
            if (to == p.Slot) { PlaySound("back"); return; }
            var other = settings.SlotPins.FirstOrDefault(x => x != p && x.Slot == to);
            if (other != null) other.Slot = p.Slot;   // swaps with a game kept there
            p.Slot = to;
            SaveSettings();
            PlaySound("move");
            ApplySlotPins(true);
        }

        private void SlotPinsOptionsOpened(string page)
        {
            Notify("IsOptionsSlotPins");
            if (page == "slotpins") slotPinEditing = null;
            if (page == "slotpins" || page == "slotpin") BuildSlotPinRows();
        }
    }
}

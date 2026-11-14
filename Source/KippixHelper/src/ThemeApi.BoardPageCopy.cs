using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace KippixHelper
{
    /// <summary>
    /// (0.86) Manual Board › Pages: a copy of Home as another page. "Add a copy of Home" makes a new page (to the right of the
    /// one showing) with Home's tiles on it; "Copy Home onto this page" (a page's own settings) puts Home's tiles on a page in
    /// place of its own. Widget tiles get their own copy of the widget (so the two pages' widgets can differ). Undo in the
    /// board's editing (LB) puts the page back as it was.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>Home's tiles copied onto <paramref name="pageId"/> (its own tiles go first). How many were copied.</summary>
        private int BoardCopyHomeTo(string pageId)
        {
            if (string.IsNullOrEmpty(pageId) || pageId == "home") return 0;
            bool wasEditing = editBase != null;
            EditBegin();
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            // this page's own tiles go (and the widgets that were only in them)
            var gone = BCards.Where(c => PageOf(c) == pageId).ToList();
            foreach (var c in gone)
            {
                BCards.Remove(c);
                if (c.Kind == "widget" && !string.IsNullOrEmpty(c.Target) && !BCards.Any(o => o.Kind == "widget" && o.Target == c.Target))
                {
                    string wid = c.Target;
                    settings.Widgets.RemoveAll(w => w.Id == wid);
                    var item = Widgets.FirstOrDefault(w => w.Config != null && w.Config.Id == wid);
                    if (item != null) Widgets.Remove(item);
                }
            }
            int n = 0;
            foreach (var src in BCards.Where(c => PageOf(c) == "home").ToList())
            {
                BoardCard copy;
                try { copy = js.Deserialize<BoardCard>(js.Serialize(src)); }
                catch (Exception ex) { logger.Warn("Kippix: board: copying a tile: " + ex.Message); continue; }
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Page = pageId;
                if (copy.Kind == "widget" && !string.IsNullOrEmpty(src.Target))
                {
                    var wc = settings.Widgets.FirstOrDefault(w => w.Id == src.Target);
                    if (wc == null) continue;
                    WidgetConfig wcopy;
                    try { wcopy = js.Deserialize<WidgetConfig>(js.Serialize(wc)); }
                    catch (Exception) { continue; }
                    wcopy.Id = Guid.NewGuid().ToString("N");
                    copy.Target = wcopy.Id;
                    var item = MakeWidget(wcopy);
                    settings.Widgets.Add(wcopy);
                    Widgets.Add(item);
                    try { RefreshWidget(item); } catch (Exception) { }
                }
                BCards.Add(copy);
                n++;
            }
            if (wasEditing) EditCheckpoint(); else EditEnd();
            logger.Info("Kippix: board: Home's " + n + " tiles copied onto page " + pageId + " (" + gone.Count + " tiles there before)");
            Notify("ShowWidgets");
            Notify("WidgetCountText");
            return n;
        }

        /// <summary>Pages › Add a copy of Home: a new page to the right of the one showing, with Home's tiles; you're taken to it.</summary>
        private void BoardAddHomeCopy()
        {
            BoardAddPage(1, 0);   // (saves, shows it, rebuilds the Pages rows)
            string id = boardPageId;
            var page = BPages.FirstOrDefault(p => p.Id == id);
            if (page == null || id == "home") return;
            int n = 2;
            string name = Loc("LOC_KX_PageHomeCopy", "Home copy");
            while (BPages.Any(p => p != page && p.Name == name)) name = string.Format(Loc("LOC_KX_PageHomeCopyN", "Home copy {0}"), n++);
            page.Name = name;
            BoardCopyHomeTo(id);
            BoardChanged(true);
            BoardRowsAgain(BuildBoardPagesRows, "page:" + id);
        }
    }
}

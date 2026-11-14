using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KippixHelper
{
    /// <summary>
    /// A widget's own shape (wider / narrower, taller / shorter), its text size, and whether it can be selected.
    /// The inside rearranges to fit; a change that would cut anything off doesn't happen (it stops at the limit).
    /// </summary>
    public partial class ThemeApi
    {
        public ICommand WidgetShapeResetCommand { get; private set; }
        private string widgetShapeStatus = "";

        private void InitWidgetShape()
        {
            WidgetShapeResetCommand = new RelayCommand(p =>
            {
                var w = editingWidget;
                if (w == null) return;
                string what = p as string;
                double was = what == "width" ? w.Config.WidthMul : what == "height" ? w.Config.HeightMul : w.Config.TextScale;
                if (Math.Abs(was - 1) < 0.001) { PlaySound("back"); return; }
                SetShape(w, what, 1);
                if (!ContentFits(w)) { SetShape(w, what, was); ShapeStatus(Loc("LOC_KX_ShapeLimit", "That's the limit: any further and something would be cut off.")); PlaySound("back"); return; }
                PlaySound("move");
                ShapeStatus("");
                SaveSettings();
            });
        }

        public string EditWidgetWidthText { get { return PercentOf(editingWidget != null ? editingWidget.Config.WidthMul : 1); } }
        public string EditWidgetHeightText { get { return PercentOf(editingWidget != null ? editingWidget.Config.HeightMul : 1); } }
        public string EditWidgetTextSizeText { get { return PercentOf(editingWidget != null ? editingWidget.Config.TextScale : 1); } }
        public string EditWidgetShapeStatus { get { return widgetShapeStatus; } }
        public bool HasEditWidgetShapeStatus { get { return widgetShapeStatus.Length > 0; } }
        public bool EditWidgetInteractive
        {
            get { return editingWidget == null || !editingWidget.Config.NotInteractive; }
            set
            {
                var w = editingWidget;
                if (w == null || w.Config.NotInteractive == !value) return;
                w.Config.NotInteractive = !value;
                SaveSettings();
                Notify("EditWidgetInteractive");
                Notify("IsInteractive");
                RefreshWidget(w);   // its pictures get (or lose) their buttons
            }
        }

        private static string PercentOf(double v) { return Math.Round((v <= 0 ? 1 : v) * 100) + "%"; }

        private void NotifyWidgetShape()
        {
            foreach (string n in new[] { "EditWidgetWidthText", "EditWidgetHeightText", "EditWidgetTextSizeText", "EditWidgetShapeStatus", "HasEditWidgetShapeStatus", "EditWidgetInteractive" }) Notify(n);
        }

        private void ShapeStatus(string s)
        {
            widgetShapeStatus = s ?? "";
            NotifyWidgetShape();
        }

        private static void SetShape(WidgetItem w, string what, double v)
        {
            if (what == "width") w.Config.WidthMul = v;
            else if (what == "height") w.Config.HeightMul = v;
            else w.Config.TextScale = v;
            w.Place();
        }

        /// <summary>Left / Right on the Width, Height or Text size rows (10% a step).</summary>
        private bool WidgetShapeKey(FrameworkElement fe, Key key)
        {
            if (fe == null || (key != Key.Left && key != Key.Right)) return false;
            string what = fe.Name == "KX_WgWidth" ? "width" : fe.Name == "KX_WgHeight" ? "height" : fe.Name == "KX_WgText" ? "text" : null;
            if (what == null) return false;
            var w = editingWidget;
            if (w == null) return true;
            int dir = key == Key.Right ? 1 : -1;
            double was = what == "width" ? w.Config.WidthMul : what == "height" ? w.Config.HeightMul : w.Config.TextScale;
            if (was <= 0) was = 1;
            double min = what == "text" ? 0.6 : 0.5, max = what == "text" ? 1.8 : 2.5;
            double v = Math.Round((was + dir * 0.1) * 10) / 10;
            if (v < min - 0.001 || v > max + 0.001) { PlaySound("back"); return true; }
            SetShape(w, what, v);
            // getting smaller (or the text bigger) can squeeze the inside: check it all still fits
            bool squeezes = (what == "text") ? dir > 0 : dir < 0;
            if (squeezes && !ContentFits(w))
            {
                SetShape(w, what, was);
                ShapeStatus(Loc("LOC_KX_ShapeLimit", "That's the limit: any further and something would be cut off."));
                PlaySound("back");
                return true;
            }
            ShapeStatus("");
            PlaySound("move");
            SaveSettings();
            return true;
        }

        /// <summary>Lays the widget out at its new size and checks its inside isn't wider or taller than the room it has.</summary>
        private bool ContentFits(WidgetItem w)
        {
            try
            {
                if (w.IsPhoto || w.IsImage) return true;   // the photo fills whatever shape it's given
                var layer = UiTools.FindByName(Application.Current.MainWindow, "KX_Widgets") as ItemsControl;
                var c = layer != null ? layer.ItemContainerGenerator.ContainerFromItem(w) as FrameworkElement : null;
                if (c == null) return true;
                c.UpdateLayout();
                var box = UiTools.FindChild<Border>(c);
                var inside = box != null ? box.Child as FrameworkElement : null;
                if (box == null || inside == null) return true;
                double availW = w.DesignWidth - box.Padding.Left - box.Padding.Right - 2;
                double availH = w.DesignHeight - box.Padding.Top - box.Padding.Bottom - 2;
                inside.Measure(new Size(availW, double.PositiveInfinity));
                var need = inside.DesiredSize;
                // and the widest single line of text that can't wrap or shorten (the big number, a title)
                double widest = WidestFixedText(inside);
                inside.InvalidateMeasure();
                return need.Height <= availH + 0.5 && need.Width <= availW + 0.5 && widest <= availW + 0.5;
            }
            catch (Exception) { return true; }
        }

        private static double WidestFixedText(DependencyObject root)
        {
            double widest = 0;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                var t = c as TextBlock;
                if (t != null && t.IsVisible && t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None && !string.IsNullOrEmpty(t.Text))
                {
                    var ft = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                               new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.White);
                    widest = Math.Max(widest, ft.WidthIncludingTrailingWhitespace + t.Margin.Left + t.Margin.Right);
                }
                widest = Math.Max(widest, WidestFixedText(c));
            }
            return widest;
        }
    }
}

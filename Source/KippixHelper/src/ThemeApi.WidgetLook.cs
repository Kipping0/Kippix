using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KippixHelper
{
    /// <summary>A row in a choice page: a name, a line under it, a colour swatch (colour pages), ticked when it's the current one.</summary>
    public class ChoiceRow
    {
        public string Text { get; set; }
        public string Sub { get; set; }
        public bool HasSub { get { return !string.IsNullOrEmpty(Sub); } }
        public Brush Swatch { get; set; }
        public bool HasSwatch { get { return Swatch != null; } }
        public bool IsSelected { get; set; }
        public ICommand Command { get; set; }
    }

    /// <summary>
    /// Widgets: how each one looks (see-through / solid colour / no background, the title, the text colour),
    /// and the pages that ask what a Spotlight or a Suggestions widget shows.
    /// </summary>
    public partial class ThemeApi
    {
        public ObservableCollection<ChoiceRow> WidgetChoices { get; private set; }
        public ICommand WidgetBackCommand { get; private set; }
        public ICommand WidgetBackColorCommand { get; private set; }
        public ICommand WidgetTextColorCommand { get; private set; }
        public ICommand WidgetTypeColorCommand { get; private set; }
        private string choicePage = "";          // subject / back / text
        private string subjectAddType;            // adding a widget: which kind (null when changing one)
        private WidgetItem subjectWidget;

        private static readonly string[][] WidgetPalette =
        {
            new[] { "#0E1014", "Dark" }, new[] { "#2B2F36", "Charcoal" }, new[] { "#5B6B7F", "Slate" }, new[] { "#FFFFFF", "White" },
            new[] { "#FFD54F", "Yellow" }, new[] { "#FF8A3D", "Orange" }, new[] { "#E5484D", "Red" }, new[] { "#E86AA6", "Pink" },
            new[] { "#8E6CF0", "Purple" }, new[] { "#3D8BFD", "Blue" }, new[] { "#2EC4D6", "Teal" }, new[] { "#3DBE6E", "Green" },
            new[] { "#A6D84A", "Lime" }, new[] { "#8A5A3C", "Brown" }, new[] { "#000000", "Black" }
        };
        private static readonly Color DefaultWidgetBack = Color.FromRgb(0x0E, 0x10, 0x14);

        private void InitWidgetLookCommands()
        {
            WidgetChoices = new ObservableCollection<ChoiceRow>();
            WidgetBackCommand = new RelayCommand(_ => CycleWidgetBack());
            WidgetBackColorCommand = new RelayCommand(_ => OpenColorChoice("back"));
            WidgetTextColorCommand = new RelayCommand(_ => OpenColorChoice("text"));
            WidgetTypeColorCommand = new RelayCommand(_ => TypeWidgetColor());
        }

        // ---------------- applying the look ----------------

        private static Color WithAlpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        private static Color? ParseColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (Regex.IsMatch(s, "^[0-9a-fA-F]{6}$") || Regex.IsMatch(s, "^[0-9a-fA-F]{3}$")) s = "#" + s;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s);
                return Color.FromRgb(c.R, c.G, c.B);
            }
            catch (Exception) { return null; }
        }

        private static string ColorHex(Color c) { return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

        private static double Luminance(Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; }

        private void ApplyWidgetLook(WidgetItem w)
        {
            var c = w.Config;
            Color back = ParseColor(c.BackColor) ?? DefaultWidgetBack;
            Brush backBrush, edge;
            Effect shadow = null;
            switch (c.Back)
            {
                case 1: backBrush = Frozen(back); edge = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)); break;
                case 2:
                    backBrush = Brushes.Transparent;
                    edge = Brushes.Transparent;
                    var ds = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 12, Opacity = 0.95, Color = Colors.Black };
                    ds.Freeze();
                    shadow = ds;
                    break;
                default: backBrush = Frozen(WithAlpha(back, 0xC7)); edge = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)); break;
            }
            Color? custom = ParseColor(c.TextColor);
            Color text;
            if (custom.HasValue) text = custom.Value;
            else if (c.Back != 2 && Luminance(back) > 0.6) text = Color.FromRgb(0x15, 0x18, 0x1D);   // light background: dark text unless you chose one
            else text = Color.FromRgb(0xF4, 0xF6, 0xFA);
            bool themeText = !custom.HasValue && text.R == 0xF4;
            w.SetLook(backBrush, edge,
                      Frozen(text),
                      themeText ? Frozen(Color.FromArgb(0xCC, 0xE4, 0xE8, 0xEF)) : Frozen(WithAlpha(text, 0xD9)),
                      themeText ? Frozen(Color.FromArgb(0x8C, 0xE4, 0xE8, 0xEF)) : Frozen(WithAlpha(text, 0xA6)),
                      !c.HideTitle, shadow);
        }

        // ---------------- the widget's page ----------------

        private string BackName(int back)
        {
            return back == 1 ? Loc("LOC_KX_WgBackSolid", "Solid colour") : back == 2 ? Loc("LOC_KX_WgBackNone", "No background") : Loc("LOC_KX_WgBackGlass", "See-through");
        }

        private string ColorName(string hex, bool forText)
        {
            var c = ParseColor(hex);
            if (!c.HasValue) return forText ? Loc("LOC_KX_WgTextAuto", "Default") : Loc("LOC_KX_ColDark", "Dark");
            string h = ColorHex(c.Value);
            var p = WidgetPalette.FirstOrDefault(x => x[0].Equals(h, StringComparison.OrdinalIgnoreCase));
            return p != null ? Loc("LOC_KX_Col" + p[1], p[1]) : h;
        }

        public string EditWidgetBackText { get { return editingWidget != null ? BackName(editingWidget.Config.Back) : ""; } }
        public bool EditWidgetHasBackColor { get { return editingWidget != null && editingWidget.Config.Back != 2; } }
        public string EditWidgetBackColorText { get { return editingWidget != null ? ColorName(editingWidget.Config.BackColor, false) : ""; } }
        public Brush EditWidgetBackSwatch { get { return Frozen(editingWidget != null ? ParseColor(editingWidget.Config.BackColor) ?? DefaultWidgetBack : DefaultWidgetBack); } }
        public string EditWidgetTextColorText { get { return editingWidget != null ? ColorName(editingWidget.Config.TextColor, true) : ""; } }
        public Brush EditWidgetTextSwatch { get { return editingWidget != null ? editingWidget.TextBrush : Brushes.White; } }
        public bool EditWidgetHideTitle
        {
            get { return editingWidget != null && editingWidget.Config.HideTitle; }
            set
            {
                var w = editingWidget;
                if (w == null || w.Config.HideTitle == value) return;
                w.Config.HideTitle = value;
                ApplyWidgetLook(w);
                SaveSettings();
                NotifyWidgetLook();
            }
        }

        private void NotifyWidgetLook()
        {
            foreach (string n in new[] { "EditWidgetBackText", "EditWidgetHasBackColor", "EditWidgetBackColorText", "EditWidgetBackSwatch",
                                         "EditWidgetTextColorText", "EditWidgetTextSwatch", "EditWidgetHideTitle" }) Notify(n);
        }

        private void CycleWidgetBack()
        {
            var w = editingWidget;
            if (w == null) return;
            w.Config.Back = (w.Config.Back + 1) % 3;
            ApplyWidgetLook(w);
            SaveSettings();
            NotifyWidgetLook();
        }

        // ---------------- choice pages (colours, what a widget shows) ----------------

        public bool IsOptionsWidgetChoice { get { return optionsPage == "widgetchoice"; } }
        public bool WidgetChoiceIsColor { get { return choicePage == "back" || choicePage == "text"; } }
        public string WidgetChoiceTitle
        {
            get
            {
                if (choicePage == "back") return Loc("LOC_KX_WgBackColor", "Background colour");
                if (choicePage == "text") return Loc("LOC_KX_WgTextColor", "Text colour");
                return Loc("LOC_KX_WgShowsWhat", "What should it show?");
            }
        }
        public string WidgetChoiceHelp
        {
            get
            {
                if (choicePage == "back" && colorTarget != null) return "";
                if (choicePage == "back") return Loc("LOC_KX_WgBackColorHelp", "See-through uses this colour too, a little lighter.");
                if (choicePage == "text") return Loc("LOC_KX_WgTextColorHelp", "Default is white, or dark on a light background.");
                return subjectAddType != null ? Loc("LOC_KX_WgSubjectAddHelp", "Then you put it where you want it.") : "";
            }
        }

        private void OpenChoicePage(string kind)
        {
            choicePage = kind;
            Notify("WidgetChoiceIsColor");
            Notify("WidgetChoiceTitle");
            Notify("WidgetChoiceHelp");
            SetOptionsPage("widgetchoice");
            var current = WidgetChoices.FirstOrDefault(c => c.IsSelected);
            if (current != null)
                ui.BeginInvoke(new Action(() => UiTools.FocusItem("KX_WidgetChoiceList", current)), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void AskWidgetSubject(string type, WidgetItem w)
        {
            subjectAddType = w == null ? type : null;
            subjectWidget = w;
            string current = w != null ? w.Config.Subject : null;
            var subjects = type == "spotlight" ? SpotlightSubjects : SuggestSubjects;
            WidgetChoices.Clear();
            foreach (string s in subjects)
            {
                string subject = s;
                WidgetChoices.Add(new ChoiceRow
                {
                    Text = SubjectName(type, subject),
                    Sub = SubjectHelp(type, subject),
                    IsSelected = subject == current,
                    Command = new RelayCommand(_ => ChooseSubject(type, subject))
                });
            }
            OpenChoicePage("subject");
        }

        private void ChooseSubject(string type, string subject)
        {
            PlaySound("select");
            if (subjectAddType != null)
            {
                subjectAddType = null;
                AddWidget(type, subject);   // straight on to placing it
                return;
            }
            var w = subjectWidget;
            subjectWidget = null;
            SetWidgetSubject(w, subject);
            GoBackTo("widgetedit", "subject");
        }

        private string SubjectHelp(string type, string subject)
        {
            if (type == "spotlight")
                switch (subject)
                {
                    case "achievements": return Loc("LOC_KX_SpAchHelp", "Your latest unlocks");
                    case "photos": return Loc("LOC_KX_SpPhotosHelp", "Screenshots from Steam, Xbox Game Bar and your Pictures, one at a time");
                    case "friends": return Loc("LOC_KX_SpFriendsHelp", "Who's online and what they're playing");
                    default: return Loc("LOC_KX_SpPlaytimeHelp", "Your hours this week and what you played most");
                }
            switch (subject)
            {
                case "old": return Loc("LOC_KX_SgOldHelp", "Games you liked but haven't touched for months");
                case "nearly": return Loc("LOC_KX_SgNearlyHelp", "Most of the achievements done");
                case "like": return Loc("LOC_KX_SgLikeHelp", "Same genres as what you play most");
                case "random": return Loc("LOC_KX_SgRandomHelp", "Three you haven't played, a new pick each time");
                default: return Loc("LOC_KX_SgNeverHelp", "Installed games you haven't started");
            }
        }

        private void OpenColorChoice(string which)
        {
            colorTarget = null;   // a widget's colour, not the theme's
            var w = editingWidget;
            if (w == null) return;
            bool text = which == "text";
            string current = text ? w.Config.TextColor : w.Config.BackColor;
            var cur = ParseColor(current);
            WidgetChoices.Clear();
            if (text)
                WidgetChoices.Add(new ChoiceRow { Text = Loc("LOC_KX_WgTextAuto", "Default"), Swatch = null, IsSelected = !cur.HasValue, Command = new RelayCommand(_ => ChooseColor(which, "")) });
            foreach (var p in WidgetPalette)
            {
                string hex = p[0];
                var col = ParseColor(hex).Value;
                WidgetChoices.Add(new ChoiceRow
                {
                    Text = Loc("LOC_KX_Col" + p[1], p[1]),
                    Swatch = Frozen(col),
                    IsSelected = cur.HasValue ? ColorHex(cur.Value) == hex : (!text && hex == "#0E1014"),
                    Command = new RelayCommand(_ => ChooseColor(which, hex))
                });
            }
            // a colour of your own that isn't in the list: shown first so you can keep it
            if (cur.HasValue && !WidgetPalette.Any(p => p[0] == ColorHex(cur.Value)))
            {
                string hex = ColorHex(cur.Value);
                WidgetChoices.Insert(text ? 1 : 0, new ChoiceRow { Text = hex, Swatch = Frozen(cur.Value), IsSelected = true, Command = new RelayCommand(_ => ChooseColor(which, hex)) });
            }
            OpenChoicePage(which);
        }

        private void ChooseColor(string which, string hex)
        {
            var w = editingWidget;
            if (w == null) return;
            PlaySound("select");
            if (which == "text") w.Config.TextColor = hex;
            else
            {
                w.Config.BackColor = hex;
                if (w.Config.Back == 2) w.Config.Back = 1;   // picked a colour: show it
            }
            ApplyWidgetLook(w);
            SaveSettings();
            NotifyWidgetLook();
            GoBackTo("widgetedit", which == "text" ? "colortext" : "colorback");
        }

        private void TypeWidgetColor()
        {
            if (WidgetChoiceIsColor && TypeThemeColor()) return;
            if (!WidgetChoiceIsColor || editingWidget == null) return;
            string which = choicePage;
            string now = which == "text" ? editingWidget.Config.TextColor : editingWidget.Config.BackColor;
            OpenKeyboard(Loc("LOC_KX_WgTypeColorHint", "A colour code like #3D8BFD (6 digits), or a colour name like teal"), now ?? "", typed =>
            {
                var c = ParseColor(typed);
                if (!c.HasValue) { PlaySound("back"); return; }
                ChooseColor(which, ColorHex(c.Value));
            }, null, null, 30);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Look and feel › Theme Studio: a settings page made for the controller to design the look you're using (colours, fonts,
    /// text size, the game cards, the top bar, panels, buttons). Every change shows at once on the real home screen behind the
    /// settings panel (and on the panel itself). The rows are built here (the page in Main.xaml only holds them):
    ///   Left / Right change a value (LT / RT in bigger steps), A on a colour opens its editor (hue / saturation / brightness,
    ///   quick picks, a colour code typed with the keyboard), X puts the highlighted value back to the pack's, Y types a colour
    ///   code, LB / RB jump between sections, B goes back (out of a colour, then out of the studio).
    /// </summary>
    public partial class ThemeApi
    {
        private class StudioRow
        {
            public string Key;
            public ButtonBase Button;
            public Action<int, bool> Adjust;   // Left / Right (true: a big step, LT / RT)
            public Action Reset;               // X: back to the pack's value
            public Action Refresh;             // shows the value now
            public string HexTarget;           // Y: type a colour code for this colour
        }

        public bool IsOptionsStudio { get { return optionsPage == "studio"; } }
        private StackPanel studioRoot;
        public object StudioView { get { return studioRoot; } }
        private readonly List<StudioRow> studioRows = new List<StudioRow>();
        private readonly List<ButtonBase> studioSections = new List<ButtonBase>();
        private string studioColorKey;               // the colour being edited (null: the studio's main page)
        private double studioH, studioS, studioV;    // that colour as hue / saturation / brightness (kept, so the hue isn't lost at grey)
        private DispatcherTimer studioSaveTimer;
        private static Type buttonExType;
        private Border studioCard;                   // the sample card (CARDS)
        private ScaleTransform studioCardScale;

        // ---------------- building the rows ----------------

        private static ButtonBase NewStudioButton()
        {
            if (buttonExType == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { var t = a.GetType("Playnite.FullscreenApp.Controls.ButtonEx", false); if (t != null) { buttonExType = t; break; } }
                    catch (Exception) { }
                }
                if (buttonExType == null) buttonExType = typeof(Button);
            }
            var b = (ButtonBase)Activator.CreateInstance(buttonExType);
            var win = Application.Current != null ? Application.Current.MainWindow : null;
            var style = win != null ? win.TryFindResource("KX_MenuButton") as Style : null;
            if (style != null && style.TargetType != null && style.TargetType.IsInstanceOfType(b)) b.Style = style;
            if (buttonExType == typeof(Button))
            {
                // (Playnite's ButtonEx clicks on the controller's A by itself; a plain button needs telling)
                b.KeyDown += (s, e) =>
                {
                    if (e.GetType().Name != "GameControllerInputEventArgs" || b.Command == null) return;
                    var bp = e.GetType().GetProperty("Button");
                    object bv = bp != null ? bp.GetValue(e, null) : null;
                    if (bv != null && bv.ToString() == "A") { b.Command.Execute(b.CommandParameter); e.Handled = true; }
                };
            }
            return b;
        }

        private static TextBlock StudioText(string text, string style)
        {
            var t = new TextBlock { Text = text ?? "", VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(FrameworkElement.StyleProperty, style);
            return t;
        }

        private void StudioHeader(string text)
        {
            studioRoot.Children.Add(StudioText(text, "KX_SectionHeader"));
            studioNewSection = true;
        }

        private bool studioNewSection;

        private StudioRow StudioAdd(StudioRow r, FrameworkElement content)
        {
            var b = NewStudioButton();
            b.Content = content;
            b.CommandParameter = r.Key;
            b.Tag = r;
            r.Button = b;
            studioRoot.Children.Add(b);
            studioRows.Add(r);
            if (studioNewSection) { studioSections.Add(b); studioNewSection = false; }
            if (r.Refresh != null) r.Refresh();
            return r;
        }

        /// <summary>A row with a label and a value on the right (choices, actions).</summary>
        private static Grid StudioLine(string label, out TextBlock value, out TextBlock sub)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(StudioText(label, "KX_Text"));
            sub = StudioText("", "KX_TextDim");
            sub.TextWrapping = TextWrapping.Wrap;
            sub.Visibility = Visibility.Collapsed;
            left.Children.Add(sub);
            g.Children.Add(left);
            value = StudioText("", "KX_TextSub");
            value.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(value, 1);
            g.Children.Add(value);
            return g;
        }

        private static void SetSub(TextBlock sub, string text)
        {
            sub.Text = text ?? "";
            sub.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private const double StudioTrack = 480;

        /// <summary>A slider: Left / Right step it (LT / RT five steps), X puts the pack's value back.</summary>
        private StudioRow StudioSlider(string key, string label, int min, int max, int stepBy, Func<LookStyle, int> get, Action<LookStyle, int> set,
                                       Func<int, string> show, string part, Func<string> note = null, Brush track = null, Action<Border> trackHook = null)
        {
            var root = new StackPanel();
            TextBlock value, sub;
            root.Children.Add(StudioLine(label, out value, out sub));
            var bar = new Grid { Width = StudioTrack, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 4) };
            var back = new Border { Height = track != null ? 12 : 6, CornerRadius = new CornerRadius(track != null ? 6 : 3), VerticalAlignment = VerticalAlignment.Center };
            if (track != null) back.Background = track; else back.Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
            if (trackHook != null) trackHook(back);
            bar.Children.Add(back);
            var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, "KX_AccentBrush");
            if (track == null) bar.Children.Add(fill);
            var knob = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            knob.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.6 };
            bar.Children.Add(knob);
            root.Children.Add(bar);
            var r = new StudioRow { Key = "studio:" + key };
            r.Refresh = () =>
            {
                int v = get(LookNow);
                value.Text = show(v);
                double f = max > min ? (double)(v - min) / (max - min) : 0;
                fill.Width = Math.Max(0, f * StudioTrack);
                knob.Margin = new Thickness(Math.Max(0, f * StudioTrack - 12), 0, 0, 0);
                SetSub(sub, note != null ? note() : null);
            };
            r.Adjust = (dir, big) =>
            {
                int v = get(LookNow);
                int n = LookClamp(v + dir * stepBy * (big ? 5 : 1), min, max);
                if (n == v) { PlaySound("back"); return; }
                set(LookNow, n);
                PlaySound("move");
                StudioChanged(part);
            };
            r.Reset = () => { set(LookNow, get(LookBase)); StudioChanged(part); };
            return StudioAdd(r, root);
        }

        /// <summary>A choice: Left / Right (and A) step through it.</summary>
        private StudioRow StudioChoice(string key, string label, Func<string[]> names, Func<LookStyle, int> get, Action<LookStyle, int> set, string part, Func<string> note = null)
        {
            TextBlock value, sub;
            var line = StudioLine(label, out value, out sub);
            var r = new StudioRow { Key = "studio:" + key };
            r.Refresh = () =>
            {
                var n = names();
                int i = get(LookNow);
                value.Text = "‹  " + (i >= 0 && i < n.Length ? n[i] : "") + "  ›";
                SetSub(sub, note != null ? note() : null);
            };
            r.Adjust = (dir, big) =>
            {
                var n = names();
                if (n.Length == 0) return;
                int i = get(LookNow);
                set(LookNow, ((i + dir) % n.Length + n.Length) % n.Length);
                PlaySound("move");
                StudioChanged(part);
            };
            r.Reset = () => { set(LookNow, get(LookBase)); StudioChanged(part); };
            var row = StudioAdd(r, line);
            row.Button.Command = new RelayCommand(_ => row.Adjust(1, false));
            return row;
        }

        /// <summary>A colour: A opens its editor, Y types its code, X puts the pack's back. "" (where allowed) is the accent.</summary>
        private StudioRow StudioColour(string target, string label, Func<string> note = null)
        {
            TextBlock value, sub;
            var line = StudioLine(label, out value, out sub);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Remove(value);
            right.Children.Add(value);
            var chip = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Margin = new Thickness(12, 0, 0, 0), BorderThickness = new Thickness(1.5), BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)) };
            right.Children.Add(chip);
            var chev = new System.Windows.Shapes.Path { Width = 18, Height = 18, Margin = new Thickness(10, 0, 0, 0) };
            chev.SetResourceReference(FrameworkElement.StyleProperty, "KX_IconPath");
            chev.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "KX_IconChevron");
            right.Children.Add(chev);
            Grid.SetColumn(right, 1);
            line.Children.Add(right);
            var r = new StudioRow { Key = "studio:c:" + target, HexTarget = target };
            r.Refresh = () =>
            {
                string hex = StudioGetColour(LookNow, target);
                if (string.IsNullOrEmpty(hex)) { value.Text = Loc("LOC_KX_StudioFollowsAccent", "Accent"); chip.SetResourceReference(Border.BackgroundProperty, "KX_AccentBrush"); }
                else { value.Text = hex.ToUpperInvariant(); chip.Background = new SolidColorBrush(HexOr(hex, Colors.Black)); }
                SetSub(sub, note != null ? note() : null);
            };
            r.Reset = () => { StudioSetColour(LookNow, target, StudioGetColour(LookBase, target)); StudioChanged(StudioColourPart(target)); };
            var row = StudioAdd(r, line);
            row.Button.Command = new RelayCommand(_ => StudioOpenColour(target));
            return row;
        }

        private StudioRow StudioAction(string key, string label, Func<string> note, Action run)
        {
            TextBlock value, sub;
            var line = StudioLine(label, out value, out sub);
            var r = new StudioRow { Key = key };
            r.Refresh = () => SetSub(sub, note != null ? note() : null);
            var row = StudioAdd(r, line);
            row.Button.Command = new RelayCommand(_ => run());
            return row;
        }

        // ---------------- the colours it can edit ----------------

        private static string StudioGetColour(LookStyle l, string target)
        {
            switch (target)
            {
                case "accent": return l.Accent;
                case "text": return l.Text;
                case "sub": return l.SubText;
                case "panel": return l.Panel;
                case "tint": return l.BgTint;
                case "ring": return l.CardBorderColor;
                case "glow": return l.GlowColor;
            }
            return "";
        }

        private static void StudioSetColour(LookStyle l, string target, string hex)
        {
            switch (target)
            {
                case "accent": l.Accent = hex; break;
                case "text": l.Text = hex; break;
                case "sub": l.SubText = hex; break;
                case "panel": l.Panel = hex; break;
                case "tint": l.BgTint = hex; break;
                case "ring": l.CardBorderColor = hex; break;
                case "glow": l.GlowColor = hex; break;
            }
        }

        private static bool StudioCanFollowAccent(string target) { return target == "ring" || target == "glow"; }
        private static string StudioColourPart(string target) { return target == "accent" ? "accent" : target == "panel" ? "panel" : "colour"; }

        private string StudioColourName(string target)
        {
            switch (target)
            {
                case "accent": return Loc("LOC_KX_StudioAccent", "Accent colour");
                case "text": return Loc("LOC_KX_StudioTextColour", "Text");
                case "sub": return Loc("LOC_KX_StudioSubColour", "Second line text");
                case "panel": return Loc("LOC_KX_StudioPanelColour", "Panels");
                case "tint": return Loc("LOC_KX_StudioTintColour", "Background tint");
                case "ring": return Loc("LOC_KX_StudioRingColour", "Highlight edge colour");
                case "glow": return Loc("LOC_KX_StudioGlowColour", "Glow colour");
            }
            return target;
        }

        // ---------------- the pages ----------------

        /// <summary>Builds the studio (or the colour being edited) again; the highlight goes to the row with this key.</summary>
        private void StudioRebuild(string focusKey)
        {
            bool first = studioRoot == null;
            if (first) studioRoot = new StackPanel();
            studioRoot.Children.Clear();
            studioRows.Clear();
            studioSections.Clear();
            studioCard = null;
            studioNewSection = false;
            try
            {
                if (studioColorKey != null) BuildStudioColour(); else BuildStudioMain();
            }
            catch (Exception ex) { logger.Warn("Kippix: Theme Studio: " + ex.Message); }
            if (first) Notify("StudioView");
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                if (optionsPage != "studio") return;
                if (focusKey == null || !UiTools.FocusCommandParameter("KX_OptionsPanel", focusKey)) UiTools.FocusInside("KX_OptionsPanel");
            }), DispatcherPriority.ContextIdle);
        }

        private void BuildStudioMain()
        {
            var help = StudioText(Loc("LOC_KX_StudioHelp", "Changes show straight away on the screen behind. Left / Right change a value, X puts one back as the pack had it, LB / RB jump between sections."), "KX_TextDim");
            help.TextWrapping = TextWrapping.Wrap;
            help.Margin = new Thickness(20, 0, 16, 8);
            studioRoot.Children.Add(help);

            StudioHeader(Loc("LOC_KX_SecLookPack", "LOOK PACK"));
            StudioAction("studio:pack", Loc("LOC_KX_LookPacks", "Look packs"), () => LookPackText, () => { PlaySound("select"); lookStatus = null; SetOptionsPage("lookpacks"); });
            StudioAction("studio:save", Loc("LOC_KX_LookSaveAs", "Save this look as a pack"), () => Loc("LOC_KX_LookSaveAsSub2", "Keep it, share it, or use it again later"), SaveLookAsPack);
            StudioAction("studio:community", Loc("LOC_KX_CommunityNote", "Share or get more in Settings › Community"), () => Loc("LOC_KX_CommunityNoteLooks", "Look packs other people have made, and sharing yours"),
                         () => OpenCommunityAt(null, "ck:browse", false));   // (ThemeApi.CommunityHub.cs)

            StudioHeader(Loc("LOC_KX_SecColours", "COLOURS"));
            StudioColour("accent", StudioColourName("accent"), () => settings.AccentFromGame ? Loc("LOC_KX_StudioAccentGame", "Accent from the game is on: this is used when a game has no colour of its own") : null);
            StudioColour("text", StudioColourName("text"));
            StudioColour("sub", StudioColourName("sub"));
            StudioColour("panel", StudioColourName("panel"));
            StudioColour("tint", StudioColourName("tint"));
            StudioSlider("tintamount", Loc("LOC_KX_StudioTintAmount", "Background tint strength"), 0, 80, 2, l => l.BgTintAmount, (l, v) => l.BgTintAmount = v, v => v == 0 ? Loc("LOC_KX_Off", "Off") : v + " %", "colour");

            StudioHeader(Loc("LOC_KX_SecFontsText", "FONTS AND TEXT"));
            StudioChoice("titlefont", Loc("LOC_KX_StudioTitleFont", "Titles and bold text"), () => LookFontChoices(true).Select(p => p.Value).ToArray(),
                         l => FontIndex(true, l.TitleFont), (l, i) => l.TitleFont = FontAt(true, i), "fonts");
            StudioChoice("bodyfont", Loc("LOC_KX_StudioBodyFont", "Everything else"), () => LookFontChoices(false).Select(p => p.Value).ToArray(),
                         l => FontIndex(false, l.BodyFont), (l, i) => l.BodyFont = FontAt(false, i), "fonts");
            StudioSlider("textscale", Loc("LOC_KX_StudioTextScale", "Text size"), 85, 130, 5, l => l.TextScale, (l, v) => l.TextScale = v, v => v + " %", "text",
                         () => settings.TextSize > 0 ? Loc("LOC_KX_StudioTextScaleNote", "On top of Accessibility › Text size") : null);

            StudioHeader(Loc("LOC_KX_SecStudioCards", "GAME CARDS"));
            BuildStudioCard();
            StudioSlider("cardcorner", Loc("LOC_KX_StudioCorners", "Corners"), 0, 32, 1, l => l.CardCorner, (l, v) => l.CardCorner = v, v => v == 0 ? Loc("LOC_KX_StudioSquare", "Square") : v + " px", "cards");
            StudioSlider("cardborder", Loc("LOC_KX_StudioEdge", "Highlight edge"), 0, 60, 5, l => l.CardBorder, (l, v) => l.CardBorder = v, v => v == 0 ? Loc("LOC_KX_Off", "Off") : (v / 10.0).ToString("0.#", CultureInfo.CurrentCulture) + " px", "cards");
            StudioColour("ring", StudioColourName("ring"));
            StudioChoice("glowstyle", Loc("LOC_KX_StudioGlow", "Glow"), () => new[] { Loc("LOC_KX_StudioGlowNone", "None"), Loc("LOC_KX_StudioGlowSoft", "Soft"), Loc("LOC_KX_StudioGlowNeon", "Neon"), Loc("LOC_KX_StudioGlowShadow", "Dark shadow") },
                         l => l.GlowStyle, (l, i) => l.GlowStyle = i, "cards");
            StudioSlider("glowsize", Loc("LOC_KX_StudioGlowSize", "Glow size"), 0, 48, 2, l => l.GlowSize, (l, v) => l.GlowSize = v, v => v + " px", "cards");
            StudioColour("glow", StudioColourName("glow"), () => LookNow.GlowStyle == 3 ? Loc("LOC_KX_StudioGlowShadowNote", "Dark shadow is always black") : null);
            StudioSlider("focusscale", Loc("LOC_KX_StudioZoom", "Highlighted card grows"), 100, 115, 1, l => l.FocusScale, (l, v) => l.FocusScale = v, v => v == 100 ? Loc("LOC_KX_Off", "Off") : v + " %", "focus",
                         () => settings.ReduceMotion ? Loc("LOC_KX_StudioReduceMotion", "Reduce motion is on (Accessibility): cards don't zoom") : null);
            StudioChoice("focusspeed", Loc("LOC_KX_StudioZoomSpeed", "How quickly"), () => new[] { Loc("LOC_KX_Slow", "Slow"), Loc("LOC_KX_Normal", "Normal"), Loc("LOC_KX_Fast", "Fast"), Loc("LOC_KX_StudioInstant", "At once") },
                         l => l.FocusSpeed, (l, i) => l.FocusSpeed = i, "focus");

            StudioHeader(Loc("LOC_KX_SecStudioTopBar", "TOP BAR"));
            StudioSlider("barplate", Loc("LOC_KX_StudioBarPlate", "Band behind it"), 0, 100, 5, l => l.TopBarPlate, (l, v) => l.TopBarPlate = v, v => v == 0 ? Loc("LOC_KX_Off", "Off") : v + " %", "topbar",
                         () => Loc("LOC_KX_StudioBarPlateNote", "In the panel colour"));
            StudioSlider("barshade", Loc("LOC_KX_StudioBarShade", "Soft shade from the top"), 0, 100, 5, l => l.TopBarShade, (l, v) => l.TopBarShade = v, v => v == 0 ? Loc("LOC_KX_Off", "Off") : v + " %", "topbar");
            StudioSlider("barsize", Loc("LOC_KX_StudioBarSize", "Size"), 80, 130, 5, l => l.TopBarSize, (l, v) => l.TopBarSize = v, v => v + " %", "topbar");

            StudioHeader(Loc("LOC_KX_SecStudioPanels", "PANELS"));
            StudioSlider("panelopacity", Loc("LOC_KX_StudioPanelOpacity", "How solid"), 40, 100, 2, l => l.PanelOpacity, (l, v) => l.PanelOpacity = v, v => v + " %", "panel");
            StudioSlider("panelcorner", Loc("LOC_KX_StudioCorners", "Corners"), 0, 40, 2, l => l.PanelCorner, (l, v) => l.PanelCorner = v, v => v == 0 ? Loc("LOC_KX_StudioSquare", "Square") : v + " px", "panel");

            StudioHeader(Loc("LOC_KX_SecStudioButtons", "BUTTONS AND HIGHLIGHTS"));
            StudioChoice("highlight", Loc("LOC_KX_StudioHighlight", "Highlight"), () => new[] { Loc("LOC_KX_StudioHlSoft", "Soft (Kippix)"), Loc("LOC_KX_StudioHlAccent", "Accent colour"), Loc("LOC_KX_StudioHlBold", "Bold"), Loc("LOC_KX_StudioHlMinimal", "Minimal") },
                         l => l.Highlight, (l, i) => l.Highlight = i, "buttons",
                         () => settings.HighContrast ? Loc("LOC_KX_StudioContrastNote", "High contrast is on (Accessibility): it has its own highlight") : null);
            StudioChoice("buttonshape", Loc("LOC_KX_StudioButtonShape", "Button shape"), () => new[] { Loc("LOC_KX_StudioShapeOwn", "Kippix"), Loc("LOC_KX_StudioShapeRound", "Round"), Loc("LOC_KX_StudioShapeSoft", "Soft square"), Loc("LOC_KX_StudioSquare", "Square") },
                         l => l.ButtonShape, (l, i) => l.ButtonShape = i, "buttons");

            StudioHeader(Loc("LOC_KX_SecReset", "RESET"));
            StudioAction("studio:undo", Loc("LOC_KX_StudioUndo", "Back to the pack as it was"), () => string.Format(Loc("LOC_KX_StudioUndoSub", "Undoes your changes to {0}"), string.IsNullOrEmpty(LookBase.Name) ? "Kippix" : LookBase.Name), () =>
            {
                string name = LookNow.Name;
                settings.Look = LookBase.Copy();
                settings.Look.Name = name;
                PlaySound("select");
                StudioChanged("all");
            });
            StudioAction("studio:reset", Loc("LOC_KX_ResetThisPage", "Reset this page"), () => Loc("LOC_KX_ResetThisPageSub", "Resets this page to default"), AskLookReset);
        }

        private int FontIndex(bool title, string id)
        {
            var list = LookFontChoices(title);
            for (int i = 0; i < list.Count; i++) if (list[i].Key == (id ?? "")) return i;
            return 0;
        }

        private string FontAt(bool title, int i)
        {
            var list = LookFontChoices(title);
            return i >= 0 && i < list.Count ? list[i].Key : "";
        }

        // ---------------- the sample card ----------------

        /// <summary>A card drawn like the game cards (their corners, edge and glow), highlighted, so the card settings show while
        /// the settings have the highlight. It zooms the way a card would when the zoom changes.</summary>
        private void BuildStudioCard()
        {
            var host = new Grid { Height = 236, Margin = new Thickness(0, 6, 0, 10) };
            var card = new Grid { Width = 150, Height = 200, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5) };
            studioCardScale = new ScaleTransform(1, 1);
            card.RenderTransform = studioCardScale;
            var art = new Border { Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x41, 0x52), Color.FromRgb(0x16, 0x19, 0x20), 60) };
            art.SetResourceReference(Border.CornerRadiusProperty, "KX_CardCorner");
            card.Children.Add(art);
            var ring = new Border { Margin = new Thickness(-3), IsHitTestVisible = false };
            ring.SetResourceReference(Border.CornerRadiusProperty, "KX_CardRingCorner");
            ring.SetResourceReference(Border.BorderThicknessProperty, "KX_CardRingThickness");
            ring.SetResourceReference(Border.BorderBrushProperty, "KX_CardRingBrush");
            card.Children.Add(ring);
            host.Children.Add(card);
            studioRoot.Children.Add(host);
            studioCard = ring;
            StudioCardRefresh();
            // the highlighted game's cover on it (decoded off the UI thread, at the card's size)
            string path = lastAccentPath;
            if (!string.IsNullOrEmpty(path))
                KippixShared.SafeWork.Queue(_ =>
                {
                    ImageSource img = null;
                    try { img = Images.Load(path, 300); } catch (Exception) { }
                    OnUi(() => { if (img != null) art.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; });
                });
        }

        private void StudioCardRefresh()
        {
            if (studioCard == null) return;
            var L = LookNow;
            Color glow = L.GlowStyle == 3 ? Colors.Black : string.IsNullOrEmpty(L.GlowColor) ? accentNow : HexOr(L.GlowColor, Colors.White);
            double size = L.GlowStyle == 2 ? L.GlowSize * 1.4 : L.GlowSize;
            double opacity = L.GlowStyle == 0 ? 0 : L.GlowStyle == 2 ? 0.95 : L.GlowStyle == 3 ? 0.75 : 0.5;
            studioCard.Effect = opacity > 0 && size > 0 ? new DropShadowEffect { Color = glow, BlurRadius = size, ShadowDepth = 0, Opacity = opacity } : null;
        }

        private void StudioCardZoom()
        {
            if (studioCardScale == null || ReduceMotionNow) return;
            var L = LookNow;
            double to = L.FocusScale / 100.0;
            int ms = FocusSpeedMs[LookClamp(L.FocusSpeed, 0, 3)];
            var up = new DoubleAnimation(1.0, to, new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var scale = studioCardScale;
            up.Completed += (s, e) =>
            {
                var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    var down = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(Math.Max(ms, 120))));
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
                };
                t.Start();
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, up);
        }

        // ---------------- the colour editor ----------------

        private void StudioOpenColour(string target)
        {
            PlaySound("select");
            studioColorKey = target;
            string hex = StudioGetColour(LookNow, target);
            Color c = string.IsNullOrEmpty(hex) ? accentNow : HexOr(hex, Colors.White);
            ToHsv(c, out studioH, out studioS, out studioV);
            StudioRebuild(null);
        }

        private void BuildStudioColour()
        {
            string target = studioColorKey;
            StudioHeader(StudioColourName(target).ToUpper(CultureInfo.CurrentCulture));
            // the colour, big, with its code
            var preview = new Border { Height = 74, CornerRadius = new CornerRadius(16), Margin = new Thickness(20, 6, 20, 12), BorderThickness = new Thickness(1.5), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) };
            var code = new TextBlock { FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            code.SetResourceReference(TextBlock.FontFamilyProperty, "FontTitilliumWebBold");
            preview.Child = code;
            studioRoot.Children.Add(preview);
            Action show = () =>
            {
                string hex = StudioGetColour(LookNow, target);
                Color c = string.IsNullOrEmpty(hex) ? accentNow : HexOr(hex, Colors.White);
                preview.Background = new SolidColorBrush(c);
                code.Text = string.IsNullOrEmpty(hex) ? Loc("LOC_KX_StudioFollowsAccent", "Accent") + " · " + ToHex(c) : ToHex(c);
                double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                code.Foreground = lum > 140 ? new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x12)) : Brushes.White;
            };
            show();

            Border satTrack = null, valTrack = null;
            Action tracks = () =>
            {
                if (satTrack != null) satTrack.Background = new LinearGradientBrush(FromHsv(studioH, 0, Math.Max(0.35, studioV)), FromHsv(studioH, 1, Math.Max(0.35, studioV)), 0);
                if (valTrack != null) valTrack.Background = new LinearGradientBrush(Colors.Black, FromHsv(studioH, Math.Max(0.15, studioS), 1), 0);
            };
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
            rainbow.Freeze();

            Action<double, double, double> put = (h, s, v) =>
            {
                studioH = h; studioS = s; studioV = v;
                StudioSetColour(LookNow, target, ToHex(FromHsv(h, s, v)));
                tracks();
                show();
                StudioChanged(StudioColourPart(target));
            };
            var hue = StudioHsvSlider("hue", Loc("LOC_KX_StudioHue", "Colour (hue)"), 360, 5, () => studioH, v => put(v % 360, studioS, studioV), v => Math.Round(v) + "°", rainbow, null);
            var sat = StudioHsvSlider("sat", Loc("LOC_KX_StudioSaturation", "Strength (saturation)"), 100, 2, () => studioS * 100, v => put(studioH, v / 100.0, studioV), v => Math.Round(v) + " %", Brushes.Gray, b => satTrack = b);
            var val = StudioHsvSlider("val", Loc("LOC_KX_StudioBrightness", "Brightness"), 100, 2, () => studioV * 100, v => put(studioH, studioS, v / 100.0), v => Math.Round(v) + " %", Brushes.Gray, b => valTrack = b);
            tracks();

            // quick picks
            StudioHeader(Loc("LOC_KX_SecQuickPicks", "QUICK PICKS"));
            var wrap = new WrapPanel { Margin = new Thickness(14, 0, 14, 6) };
            string[] picks = { "#FFFFFF", "#E4E8EF", "#9AA3B2", "#1B1E24", "#000000", "#FF6B6B", "#FFB37A", "#FBBF24", "#7DFF9B", "#4ADE80", "#2DF2FF", "#9DB8FF", "#7C83FF", "#C084FC", "#FF4FB3", "#F6C1D9" };
            ButtonBase firstPick = null;
            foreach (string p in picks)
            {
                string hex = p;
                var b = NewStudioButton();
                b.Width = 62; b.Height = 62; b.MinHeight = 0; b.Padding = new Thickness(0); b.Margin = new Thickness(2); b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Content = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(HexOr(hex, Colors.White)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center };
                b.CommandParameter = "studio:pick:" + hex;
                b.Command = new RelayCommand(_ =>
                {
                    double h, s, v;
                    ToHsv(HexOr(hex, Colors.White), out h, out s, out v);
                    if (s < 0.01) h = studioH;   // (a grey keeps the hue you had)
                    PlaySound("select");
                    put(h, s, v);
                    hue.Refresh(); sat.Refresh(); val.Refresh();
                });
                b.Tag = new StudioRow { Key = "studio:pick:" + hex, HexTarget = target };
                wrap.Children.Add(b);
                if (firstPick == null) firstPick = b;
            }
            studioRoot.Children.Add(wrap);
            if (firstPick != null) studioSections.Add(firstPick);

            StudioHeader(Loc("LOC_KX_SecMore", "MORE"));
            if (StudioCanFollowAccent(target))
                StudioAction("studio:followaccent", Loc("LOC_KX_StudioUseAccent", "Use the accent colour"), () => string.IsNullOrEmpty(StudioGetColour(LookNow, target)) ? Loc("LOC_KX_StudioUsingAccent", "In use: it follows the accent (and Accent from the game)") : Loc("LOC_KX_StudioUseAccentSub", "It follows the accent (and Accent from the game)"), () =>
                {
                    StudioSetColour(LookNow, target, "");
                    PlaySound("select");
                    show();
                    StudioChanged(StudioColourPart(target));
                    foreach (var r in studioRows) if (r.Refresh != null) r.Refresh();
                });
            StudioAction("studio:hex", Loc("LOC_KX_StudioTypeCode", "Type a colour code"), () => Loc("LOC_KX_StudioTypeCodeSub", "#RRGGBB, with the keyboard (Y does this too)"), () => StudioTypeHex(target));
            StudioAction("studio:done", Loc("LOC_KX_Done", "Done"), null, () => { PlaySound("back"); studioColorKey = null; StudioRebuild("studio:c:" + target); });
        }

        /// <summary>A hue / saturation / brightness slider (its own value, not one of the look's).</summary>
        private StudioRow StudioHsvSlider(string key, string label, double max, double stepBy, Func<double> get, Action<double> set, Func<double, string> show, Brush track, Action<Border> trackHook)
        {
            var root = new StackPanel();
            TextBlock value, sub;
            root.Children.Add(StudioLine(label, out value, out sub));
            var bar = new Grid { Width = StudioTrack, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 4) };
            var back = new Border { Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center, Background = track };
            if (trackHook != null) trackHook(back);
            bar.Children.Add(back);
            var knob = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.White, BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x12)), HorizontalAlignment = HorizontalAlignment.Left };
            knob.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.6 };
            bar.Children.Add(knob);
            root.Children.Add(bar);
            var r = new StudioRow { Key = "studio:" + key, HexTarget = studioColorKey };
            r.Refresh = () =>
            {
                double v = get();
                value.Text = show(v);
                double f = Math.Max(0, Math.Min(1, v / max));
                knob.Margin = new Thickness(Math.Max(0, f * StudioTrack - 13), 0, 0, 0);
            };
            r.Adjust = (dir, big) =>
            {
                double v = get();
                double n = v + dir * stepBy * (big ? 5 : 1);
                if (key == "hue") n = ((n % 360) + 360) % 360;   // the hue goes round
                else n = Math.Max(0, Math.Min(max, n));
                if (Math.Abs(n - v) < 0.001) { PlaySound("back"); return; }
                PlaySound("move");
                set(n);
                foreach (var o in studioRows) if (o.Refresh != null) o.Refresh();
            };
            return StudioAdd(r, root);
        }

        private void StudioTypeHex(string target)
        {
            if (target == null) return;
            PlaySound("select");
            string now = StudioGetColour(LookNow, target);
            OpenKeyboard(string.Format(Loc("LOC_KX_StudioCodeFor", "Colour code for {0} (#RRGGBB)"), StudioColourName(target)), string.IsNullOrEmpty(now) ? "#" : now, typed =>
            {
                var c = ParseHex((typed ?? "").Trim());
                if (c == null) { PlaySound("back"); return; }
                StudioSetColour(LookNow, target, ToHex(c.Value));
                ToHsv(c.Value, out studioH, out studioS, out studioV);
                StudioChanged(StudioColourPart(target));
                if (studioColorKey != null) StudioRebuild("studio:hex");
            }, null, null, 9);
        }

        // ---------------- a change ----------------

        /// <summary>A value changed: on screen at once, every row's value shown again, saved a moment later.</summary>
        private void StudioChanged(string part)
        {
            var L = LookNow;
            NormalizeLookStyle(L);
            try
            {
                if (part == "accent" || part == "all") { var a = LookBaseAccent(); if (a != accentNow) PutAccent(a); }
                if (part == "text" || part == "all") ApplyTextSize();
                if (part == "topbar" || part == "panel" || part == "all") LookExtras();
                LookResources(false);
                LookAccentFollow(true);
                if (part == "all") ApplyContrast();
                StudioCardRefresh();
                if (part == "focus") StudioCardZoom();
            }
            catch (Exception ex) { logger.Warn("Kippix: Theme Studio: " + ex.Message); }
            foreach (var r in studioRows) if (r.Refresh != null) r.Refresh();
            Notify("LookPackText");
            if (ui == null) { SaveSettings(); return; }
            if (studioSaveTimer == null)
            {
                studioSaveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(700) };
                studioSaveTimer.Tick += (s, e) => { studioSaveTimer.Stop(); SaveSettings(); };
            }
            studioSaveTimer.Stop();
            studioSaveTimer.Start();
        }

        /// <summary>Leaving the studio: a change still waiting to be saved is saved now.</summary>
        private void StudioFlushSave()
        {
            if (studioSaveTimer != null && studioSaveTimer.IsEnabled) { studioSaveTimer.Stop(); SaveSettings(); }
        }

        // ---------------- keys ----------------

        /// <summary>Left / Right on a studio row (from the window's arrow keys, before they move the highlight). True if it was one.</summary>
        private bool StudioArrowKey(FrameworkElement fe, Key key)
        {
            if (openPanel != "options" || optionsPage != "studio" || fe == null || (key != Key.Left && key != Key.Right)) return false;
            var r = fe.Tag as StudioRow;
            if (r == null || r.Adjust == null) return false;
            r.Adjust(key == Key.Right ? 1 : -1, false);
            return true;
        }

        /// <summary>The buttons in Theme Studio (from HandlePanelKey).</summary>
        private bool StudioPanelKey(KeyEventArgs e, int step, int pageJump, bool x, bool y, bool back)
        {
            if (back)
            {
                if (studioColorKey == null) { StudioFlushSave(); return false; }   // out of the studio, as usual
                PlaySound("back");
                string k = studioColorKey;
                studioColorKey = null;
                StudioRebuild("studio:c:" + k);
                return true;
            }
            var fe = Keyboard.FocusedElement as FrameworkElement;
            var r = fe != null ? fe.Tag as StudioRow : null;
            if (pageJump != 0 && r != null && r.Adjust != null) { r.Adjust(pageJump, true); return true; }
            if (step != 0 && pageJump == 0) { StudioJump(step, fe); return true; }
            if (x && r != null && r.Reset != null) { PlaySound("move"); r.Reset(); return true; }
            if (y && r != null && r.HexTarget != null) { StudioTypeHex(r.HexTarget); return true; }
            return false;
        }

        /// <summary>LB / RB: the first row of the section before / after the highlighted one.</summary>
        private void StudioJump(int dir, FrameworkElement focused)
        {
            if (studioSections.Count == 0) return;
            int at = -1;
            if (focused != null)
            {
                // the section the highlight is in: the last section start at or above it
                var all = new List<UIElement>();
                foreach (UIElement c in studioRoot.Children) all.Add(c);
                int fi = -1;
                for (int i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], focused) || all[i].IsAncestorOf(focused)) { fi = i; break; }
                for (int s = 0; s < studioSections.Count; s++)
                {
                    int si = -1;
                    for (int i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], studioSections[s]) || all[i].IsAncestorOf(studioSections[s])) { si = i; break; }
                    if (si >= 0 && si <= fi) at = s;
                }
            }
            int to = Math.Max(0, Math.Min(studioSections.Count - 1, at + dir));
            if (at < 0 && dir < 0) to = 0;
            if (to == at) { PlaySound("back"); return; }
            PlaySound("move");
            var b = studioSections[to];
            b.BringIntoView();
            Keyboard.Focus(b);
        }

        // ---------------- hue / saturation / brightness ----------------

        private static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
                if (h < 0) h += 360;
            }
        }

        private static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Max(0, Math.Min(1, s));
            v = Math.Max(0, Math.Min(1, v));
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}

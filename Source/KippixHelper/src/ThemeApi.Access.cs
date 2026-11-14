using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Settings › Accessibility (text size, reduce motion, high contrast) and General › Language.
    /// Text size and contrast swap the theme's own resources (KX_Font*, KX_*Brush); reduce motion makes the
    /// helper's slides instant, stops the focus zooms as they start, and holds the game page's entrance still.
    /// </summary>
    public partial class ThemeApi
    {
        /// <summary>Accessibility › Reduce motion (read by static helpers too).</summary>
        internal static bool ReduceMotionNow;

        /// <summary>An animation's length: none at all with Reduce motion on.</summary>
        internal static Duration Motion(int ms)
        {
            return new Duration(TimeSpan.FromMilliseconds(ReduceMotionNow ? 0 : ms));
        }

        public bool IsOptionsAccess { get { return optionsPage == "access"; } }
        public bool IsOptionsLanguage { get { return optionsPage == "language"; } }
        private ObservableCollection<PickRow> accessRows, languageRows;
        public ObservableCollection<PickRow> AccessRows { get { return accessRows ?? (accessRows = new ObservableCollection<PickRow>()); } }
        public ObservableCollection<PickRow> LanguageRows { get { return languageRows ?? (languageRows = new ObservableCollection<PickRow>()); } }

        private static readonly double[] TextSizeScales = { 1.0, 1.15, 1.3, 1.5 };

        private string TextSizeName(int i)
        {
            switch (i)
            {
                case 1: return Loc("LOC_KX_TextLarge", "Large");
                case 2: return Loc("LOC_KX_TextLarger", "Larger");
                case 3: return Loc("LOC_KX_TextLargest", "Largest");
                default: return Loc("LOC_KX_TextNormal", "Normal");
            }
        }

        public string AccessSummary
        {
            get
            {
                var on = new List<string>();
                if (settings.TextSize > 0) on.Add(string.Format(Loc("LOC_KX_TextSizeIs", "Text: {0}"), TextSizeName(settings.TextSize)));
                if (settings.ReduceMotion) on.Add(Loc("LOC_KX_ReduceMotion", "Reduce motion"));
                if (settings.HighContrast) on.Add(Loc("LOC_KX_HighContrast", "High contrast"));
                return on.Count == 0 ? Loc("LOC_KX_CatAccessSub", "Bigger text, less motion, high contrast") : string.Join(" · ", on.ToArray());
            }
        }

        /// <summary>Called once the settings are read (and again whenever one of these changes).</summary>
        private void ApplyAccessibility()
        {
            ReduceMotionNow = settings.ReduceMotion;
            try { ApplyTextSize(); } catch (Exception ex) { logger.Warn("Kippix: text size: " + ex.Message); }
            try { ApplyContrast(); } catch (Exception ex) { logger.Warn("Kippix: high contrast: " + ex.Message); }
            if (ReduceMotionNow) HookStillZooms();
            try { ApplyBackgroundMotion(); } catch (Exception) { }
        }

        private static readonly string[] FontKeys = { "KX_FontText", "KX_FontSub", "KX_FontDim", "KX_FontHeader", "KX_FontTitle", "KX_FontPill", "KX_FontChip" };
        private static readonly double[] FontBase = { 22, 20, 17, 15, 54, 24, 19 };

        private void ApplyTextSize()
        {
            var app = Application.Current;
            if (app == null) return;
            double f = TextSizeScales[Math.Max(0, Math.Min(3, settings.TextSize))] * LookTextScale();   // (times the look's own text size: Theme Studio)
            for (int i = 0; i < FontKeys.Length; i++)
            {
                if (f == 1.0) app.Resources.Remove(FontKeys[i]);
                else app.Resources[FontKeys[i]] = Math.Round(FontBase[i] * f);
            }
        }

        private static readonly string[] ContrastKeys = { "KX_PanelBrush", "KX_HairlineBrush", "KX_TextSubBrush", "KX_TextDimBrush", "KX_FocusFillBrush", "KX_CardBrush", "KX_FocusRingBrush", "KX_FocusRingThickness" };

        private static SolidColorBrush Solid(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        private void ApplyContrast()
        {
            var app = Application.Current;
            if (app == null) return;
            // the look (Look packs / Theme Studio) goes first: High contrast goes over its own keys while it's on
            if (!settings.HighContrast)
            {
                bool was = contrastOn;
                contrastOn = false;
                if (was) foreach (string k in ContrastKeys) app.Resources.Remove(k);
                LookResources(was);
                return;
            }
            LookResources(false);
            contrastOn = true;
            app.Resources["KX_PanelBrush"] = Solid(0xFF0A0B0E);       // solid, nothing shows through
            app.Resources["KX_HairlineBrush"] = Solid(0xB3FFFFFF);
            app.Resources["KX_TextSubBrush"] = Solid(0xFFFFFFFF);
            app.Resources["KX_TextDimBrush"] = Solid(0xF2F2F4F7);
            app.Resources["KX_FocusFillBrush"] = Solid(0x5CFFFFFF);
            app.Resources["KX_CardBrush"] = Solid(0x33FFFFFF);
            app.Resources["KX_FocusRingBrush"] = Solid(0xFFFFFFFF);
            app.Resources["KX_FocusRingThickness"] = new Thickness(3);
        }

        // ---------------- reduce motion: the theme's own zooms ----------------

        private static bool stillZoomsHooked;

        /// <summary>The theme's buttons and cards zoom a little when highlighted (storyboards in the theme): with
        /// Reduce motion on, the zoom is taken off again as soon as it starts.</summary>
        private void HookStillZooms()
        {
            if (stillZoomsHooked) return;
            stillZoomsHooked = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                if (ReduceMotionNow && ReferenceEquals(s, e.NewFocus)) StillZoomSoon(s as UIElement);
            }), true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.MouseEnterEvent, new MouseEventHandler((s, e) =>
            {
                if (ReduceMotionNow) StillZoomSoon(s as UIElement);
            }), true);
        }

        private static void StillZoomSoon(UIElement el)
        {
            if (el == null) return;
            el.Dispatcher.BeginInvoke(new Action(() => StillZoom(el.RenderTransform)), DispatcherPriority.Render);
        }

        private static void StillZoom(Transform t)
        {
            try
            {
                var st = t as ScaleTransform;
                if (st != null && !st.IsFrozen && st.HasAnimatedProperties)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    return;
                }
                var g = t as TransformGroup;
                if (g != null && !g.IsFrozen) foreach (var c in g.Children) StillZoom(c);
            }
            catch (Exception) { }
        }

        /// <summary>The game page slides its parts in and zooms its background as it opens: held at the end instead.</summary>
        private void StillDetailsEntrance(FrameworkElement details)
        {
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    var owner = FindTemplated(details, "LogoShift");
                    if (owner == null) return;
                    var hold = new Duration(TimeSpan.Zero);
                    foreach (string n in new[] { "LogoShift", "InfoShift", "CoverShift" })
                    {
                        var tt = owner.Template.FindName(n, owner) as TranslateTransform;
                        if (tt != null) { tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 0, hold)); tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 0, hold)); }   // (0.99.68: they rise now)
                    }
                    var bg = owner.Template.FindName("BgScale", owner) as ScaleTransform;
                    if (bg != null)
                    {
                        bg.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1, hold));
                        bg.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1, hold));
                    }
                }
                catch (Exception) { }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>The control (somewhere inside <paramref name="root"/>) whose template has a part with this name.</summary>
        private static Control FindTemplated(DependencyObject root, string part)
        {
            if (root == null) return null;
            var c = root as Control;
            if (c != null && c.Template != null)
            {
                try { if (c.Template.FindName(part, c) != null) return c; } catch (Exception) { }
            }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var found = FindTemplated(VisualTreeHelper.GetChild(root, i), part);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Ambient mode's slow drift: held still.</summary>
        private void StillAmbient()
        {
            try
            {
                var art = UiTools.FindByName(Application.Current.MainWindow, "KX_AmbientArt");
                if (art == null) return;
                var st = art.RenderTransform as ScaleTransform;
                if (st == null || st.IsFrozen) return;
                var hold = new Duration(TimeSpan.Zero);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1, hold));
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1, hold));
            }
            catch (Exception) { }
        }

        // ---------------- the Accessibility page ----------------

        private void BuildAccessRows()
        {
            var rows = AccessRows;
            rows.Clear();
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_AccessText", "TEXT") });
            rows.Add(new PickRow
            {
                Key = "textsize", Icon = "note",
                Text = string.Format(Loc("LOC_KX_TextSizeRow", "Text size: {0}"), TextSizeName(settings.TextSize)),
                Sub = Loc("LOC_KX_TextSizeSub", "Normal, Large, Larger, Largest"),
                Command = new RelayCommand(_ =>
                {
                    settings.TextSize = (settings.TextSize + 1) % 4;
                    SaveSettings();
                    PlaySound("move");
                    ApplyAccessibility();
                    AccessChanged("textsize");
                })
            });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_AccessMotion", "MOTION") });
            rows.Add(BoardSwitch("pause", "motion", Loc("LOC_KX_ReduceMotion", "Reduce motion"), settings.ReduceMotion,
                                 Loc("LOC_KX_ReduceMotionOn", "No slides, zooms, background motion or cards pushed aside"),
                                 Loc("LOC_KX_ReduceMotionOff", "Off: things slide and zoom as they move"),
                                 () => { settings.ReduceMotion = !settings.ReduceMotion; SaveSettings(); PlaySound("move"); ApplyAccessibility(); AccessChanged("motion"); }));
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_AccessContrast", "CONTRAST") });
            rows.Add(BoardSwitch("sun", "contrast", Loc("LOC_KX_HighContrast", "High contrast"), settings.HighContrast,
                                 Loc("LOC_KX_HighContrastOn", "Solid backgrounds, brighter text, stronger outlines and highlight rings"),
                                 Loc("LOC_KX_HighContrastOff", "Off"),
                                 () => { settings.HighContrast = !settings.HighContrast; SaveSettings(); PlaySound("move"); ApplyAccessibility(); AccessChanged("contrast"); }));
        }

        private void AccessChanged(string key)
        {
            Notify("AccessSummary");
            BoardRowsAgain(BuildAccessRows, key);
        }

        // ---------------- language ----------------

        private ResourceDictionary languageDict;

        /// <summary>The language files the theme has (Localization\xx_YY.xaml).</summary>
        private List<string> LanguageFiles()
        {
            var list = new List<string>();
            try
            {
                string dir = Path.Combine(ThemeDir, "Localization");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.xaml")) list.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception) { }
            if (!list.Contains("en_US")) list.Insert(0, "en_US");
            return list.OrderBy(x => x == "en_US" ? 0 : 1).ThenBy(LanguageName).ToList();
        }

        private static string LanguageName(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            try
            {
                var ci = CultureInfo.GetCultureInfo(code.Replace('_', '-'));
                string n = ci.NativeName;
                return string.IsNullOrEmpty(n) ? code : char.ToUpper(n[0]) + n.Substring(1);
            }
            catch (Exception) { return code; }
        }

        /// <summary>Playnite's own language (en_US when it can't be read).</summary>
        private string PlayniteLanguage()
        {
            try
            {
                string l = api.ApplicationSettings.Language;
                return string.IsNullOrEmpty(l) ? "en_US" : l;
            }
            catch (Exception) { return "en_US"; }
        }

        public string LanguageText
        {
            get
            {
                string o = settings.LanguageOverride;
                return string.IsNullOrEmpty(o) ? string.Format(Loc("LOC_KX_LanguageFollow", "Same as Playnite ({0})"), LanguageName(PlayniteLanguage())) : LanguageName(o);
            }
        }

        /// <summary>An overridden language: its file goes over the theme's text (Playnite's own choice otherwise).</summary>
        private void ApplyLanguage()
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;
                var merged = app.Resources.MergedDictionaries;
                if (languageDict != null) { merged.Remove(languageDict); languageDict = null; }
                string code = settings.LanguageOverride;
                if (string.IsNullOrEmpty(code)) return;
                string file = Path.Combine(ThemeDir, "Localization", code + ".xaml");
                if (!File.Exists(file)) return;
                // English underneath, so a file missing some lines still shows English for them
                if (code != "en_US")
                {
                    string en = Path.Combine(ThemeDir, "Localization", "en_US.xaml");
                    var baseDict = LoadDict(en);
                    var top = LoadDict(file);
                    if (top == null) return;
                    var both = new ResourceDictionary();
                    if (baseDict != null) both.MergedDictionaries.Add(baseDict);
                    both.MergedDictionaries.Add(top);
                    merged.Add(both);
                    languageDict = both;
                    return;
                }
                var dict = LoadDict(file);
                if (dict == null) return;
                merged.Add(dict);
                languageDict = dict;
            }
            catch (Exception ex) { logger.Warn("Kippix: language: " + ex.Message); }
        }

        private static ResourceDictionary LoadDict(string file)
        {
            if (!File.Exists(file)) return null;
            using (var fs = File.OpenRead(file))
            {
                var ctx = new ParserContext { BaseUri = new Uri(file, UriKind.Absolute) };
                return XamlReader.Load(fs, ctx) as ResourceDictionary;
            }
        }

        private void BuildLanguageRows()
        {
            var rows = LanguageRows;
            rows.Clear();
            string cur = settings.LanguageOverride ?? "";
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_SecFollowPlaynite", "FOLLOW PLAYNITE") });
            rows.Add(new PickRow
            {
                Key = "lang:", Icon = "globe", IsSelected = cur == "",
                Text = Loc("LOC_KX_LanguageAuto", "Same as Playnite"),
                Sub = string.Format(Loc("LOC_KX_LanguageAutoSub", "Playnite is set to {0}"), LanguageName(PlayniteLanguage())),
                Command = new RelayCommand(_ => SetLanguage(""))
            });
            rows.Add(new PickRow { IsHeader = true, Text = Loc("LOC_KX_LanguageAlways", "ALWAYS USE") });
            foreach (string code in LanguageFiles())
            {
                string c = code;
                rows.Add(new PickRow
                {
                    Key = "lang:" + c, Icon = "globe", IsSelected = cur == c, Text = LanguageName(c), Sub = c,
                    Command = new RelayCommand(_ => SetLanguage(c))
                });
            }
            rows.Add(new PickRow { IsInfo = true, Icon = "info", Key = "langmore", Text = Loc("LOC_KX_LanguageMore", "More languages are new files in the theme's Localization folder") });
        }

        private void SetLanguage(string code)
        {
            settings.LanguageOverride = code ?? "";
            SaveSettings();
            PlaySound("select");
            ApplyLanguage();
            // text the helper made itself is made again in the new language
            try { BuildShortcuts(); BuildInfoItems(); } catch (Exception) { }
            Notify("");
            BoardRowsAgain(BuildLanguageRows, "lang:" + settings.LanguageOverride);
        }

        private void AccessOptionsOpened(string page)
        {
            Notify("IsOptionsAccess");
            Notify("IsOptionsLanguage");
            if (page == "access") BuildAccessRows();
            if (page == "language") BuildLanguageRows();
            if (page == "general" || page == "") { Notify("LanguageText"); Notify("AccessSummary"); }
        }
    }
}

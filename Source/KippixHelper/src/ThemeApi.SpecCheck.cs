using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace KippixHelper
{
    /// <summary>
    /// One part of "Your PC" against a game's requirements (memory, processor, graphics): your part, a bar of how strong it is
    /// with marks for the minimum and recommended, and a short verdict. Processors and graphics cards are compared by a
    /// built-in table of rough benchmark scores (well-known parts; a newer or rare one that isn't known says so).
    /// </summary>
    public class SpecCheck
    {
        public string Name { get; set; }
        public string Mine { get; set; }
        public string Summary { get; set; }
        public Brush SummaryBrush { get; set; }
        public string MinLine { get; set; }
        public string RecLine { get; set; }
        public bool HasMinLine { get { return !string.IsNullOrEmpty(MinLine); } }
        public bool HasRecLine { get { return !string.IsNullOrEmpty(RecLine); } }
        public double FillWidth { get; set; }      // of 640
        public Brush Fill { get; set; }
        public Thickness MinMark { get; set; }
        public Thickness RecMark { get; set; }
        public bool HasMinMark { get; set; }
        public bool HasRecMark { get; set; }
        public bool HasBar { get; set; }
    }

    public partial class ThemeApi
    {
        private static readonly Brush SpecGood = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80)), SpecOk = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24)),
                                      SpecBad = Frozen(Color.FromRgb(0xF4, 0x3F, 0x5E)), SpecUnknown = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));

        /// <summary>Memory, processor and graphics checks for the game whose requirements are open.</summary>
        private List<SpecCheck> SpecChecks(string reqMin, string reqRec)
        {
            var list = new List<SpecCheck>();
            // memory
            double ram = Math.Round(TotalRamGb());
            double ramMin = ReqAmount(reqMin, RamPattern), ramRec = ReqAmount(reqRec, RamPattern);
            if (ram > 0)
                list.Add(MakeCheck(Loc("LOC_KX_SpecMemory", "Memory"), ram + " GB", ram,
                                   ramMin, ramMin > 0 ? ramMin.ToString("0.#", CultureInfo.CurrentCulture) + " GB" : null,
                                   ramRec, ramRec > 0 ? ramRec.ToString("0.#", CultureInfo.CurrentCulture) + " GB" : null, true));
            // processor
            string cpu = MyCpuName();
            if (!string.IsNullOrEmpty(cpu))
            {
                string minName, recName;
                double cMin = PartNeed(ReqLine(reqMin, "Processor|CPU"), false, out minName), cRec = PartNeed(ReqLine(reqRec, "Processor|CPU"), false, out recName);
                list.Add(MakeCheck(Loc("LOC_KX_SpecCpu", "Processor"), CleanPartName(cpu), PartScores.Cpu(cpu), cMin, minName ?? ReqShort(reqMin, "Processor|CPU"), cRec, recName ?? ReqShort(reqRec, "Processor|CPU"), false));
            }
            // graphics: the strongest card in this PC
            string gpu = null;
            double gScore = 0;
            foreach (var g in MyGpuNames())
            {
                double sc = PartScores.Gpu(g);
                if (gpu == null || sc > gScore) { gpu = g; gScore = sc; }
            }
            if (!string.IsNullOrEmpty(gpu))
            {
                string minName, recName;
                double gMin = PartNeed(ReqLine(reqMin, "Graphics|Video|GPU|Video Card"), true, out minName), gRec = PartNeed(ReqLine(reqRec, "Graphics|Video|GPU|Video Card"), true, out recName);
                list.Add(MakeCheck(Loc("LOC_KX_SpecGpu", "Graphics"), CleanPartName(gpu), gScore, gMin, minName ?? ReqShort(reqMin, "Graphics|Video|GPU|Video Card"), gRec, recName ?? ReqShort(reqRec, "Graphics|Video|GPU|Video Card"), false));
            }
            return list;
        }

        /// <param name="exact">memory: compared as amounts (a little leeway for "16 GB" showing as 15.9)</param>
        private SpecCheck MakeCheck(string name, string mine, double mineScore, double minScore, string minText, double recScore, string recText, bool exact)
        {
            var c = new SpecCheck { Name = name, Mine = mine };
            double slack = exact ? 0.5 : 0;
            Func<double, bool> meets = need => exact ? mineScore + slack >= need : mineScore >= need * 0.95;
            if (!string.IsNullOrEmpty(minText))
                c.MinLine = string.Format(Loc("LOC_KX_SpecMinLine", "Minimum: {0}"), minText)
                          + (minScore > 0 && mineScore > 0 ? "   " + (meets(minScore) ? "✓" : "✗") : "");
            if (!string.IsNullOrEmpty(recText))
                c.RecLine = string.Format(Loc("LOC_KX_SpecRecLine", "Recommended: {0}"), recText)
                          + (recScore > 0 && mineScore > 0 ? "   " + (meets(recScore) ? "✓" : "✗") : "");

            if (mineScore <= 0)
            {
                c.Summary = Loc("LOC_KX_SpecUnknownMine", "Not in the comparison list");
                c.SummaryBrush = SpecUnknown;
                return c;
            }
            if (minScore <= 0 && recScore <= 0)
            {
                c.Summary = string.IsNullOrEmpty(minText) && string.IsNullOrEmpty(recText) ? "" : Loc("LOC_KX_SpecCantCompare", "Can't compare");
                c.SummaryBrush = SpecUnknown;
                return c;
            }
            if (recScore > 0 && meets(recScore)) { c.Summary = exact ? Loc("LOC_KX_SpecEnoughRec", "Enough for recommended") : Loc("LOC_KX_SpecBetterRec", "Better than recommended"); c.SummaryBrush = SpecGood; c.Fill = SpecGood; }
            else if (minScore > 0 && meets(minScore)) { c.Summary = exact ? Loc("LOC_KX_SpecEnoughMin", "Enough for minimum") : Loc("LOC_KX_SpecBetterMin", "Better than minimum"); c.SummaryBrush = recScore > 0 ? SpecOk : SpecGood; c.Fill = c.SummaryBrush; }
            else if (minScore > 0) { c.Summary = exact ? Loc("LOC_KX_SpecNotEnough", "Not enough") : Loc("LOC_KX_SpecBelowMin", "Below minimum"); c.SummaryBrush = SpecBad; c.Fill = SpecBad; }
            else { c.Summary = exact ? Loc("LOC_KX_SpecBelowRecRam", "Less than recommended") : Loc("LOC_KX_SpecBelowRec", "Below recommended"); c.SummaryBrush = SpecOk; c.Fill = SpecOk; }

            // the bar: yours, with marks where the minimum and recommended are
            double top = Math.Max(mineScore, Math.Max(minScore, recScore)) * 1.15;
            c.HasBar = true;
            c.FillWidth = Math.Round(Math.Min(1, mineScore / top) * 640);
            if (minScore > 0) { c.HasMinMark = true; c.MinMark = new Thickness(Math.Round(minScore / top * 640) - 1.5, 0, 0, 0); }
            if (recScore > 0) { c.HasRecMark = true; c.RecMark = new Thickness(Math.Round(recScore / top * 640) - 1.5, 0, 0, 0); }
            return c;
        }

        /// <summary>The requirement's line for a part ("Graphics: NVIDIA GeForce GTX 970 or AMD Radeon RX 480").</summary>
        private static string ReqLine(string text, string labels)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"(?:^|\n)\s*(?:" + labels + @")\s*:\s*([^\n]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string ReqShort(string text, string labels)
        {
            string l = ReqLine(text, labels);
            if (l == null) return null;
            return l.Length > 70 ? l.Substring(0, 70).TrimEnd() + "…" : l;
        }

        /// <summary>
        /// What a requirement line asks for: the weakest of the parts it names (any one of them will do), with its name.
        /// 0 when none of them is known.
        /// </summary>
        private static double PartNeed(string line, bool gpu, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(line)) return 0;
            double best = 0;
            var names = new List<string>();
            foreach (var part in Regex.Split(line, @"\s+or\s+|\s*/\s*|\s*\|\s*|,\s*(?=(?:nvidia|amd|intel|ati|geforce|radeon|gtx|rtx|rx|arc|core|ryzen|fx)\b)", RegexOptions.IgnoreCase))
            {
                string k;
                double s = gpu ? PartScores.Gpu(part, out k) : PartScores.Cpu(part, out k);
                if (s <= 0) continue;
                names.Add(CleanPartName(part));
                if (best == 0 || s < best) best = s;
            }
            if (names.Count > 0) name = string.Join(" / ", names.Distinct().Take(3).ToArray());
            return best;
        }

        private static string CleanPartName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"\((?:R|TM|tm|r)\)|®|™", "");
            s = Regex.Replace(s, @"\b(\d+-Core|\d+ Cores?|Processor|CPU|@.*$)\b?", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @",?\s*\d+\s*GB.*$", "", RegexOptions.IgnoreCase);   // "GTX 970, 4 GB VRAM"
            s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(',', ';', '.');
            return s;
        }

        private static string myCpu;
        private static List<string> myGpus;

        private static string MyCpuName()
        {
            if (myCpu != null) return myCpu;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    myCpu = k != null ? Convert.ToString(k.GetValue("ProcessorNameString")).Trim() : "";
            }
            catch (Exception) { myCpu = ""; }
            return myCpu;
        }

        private static List<string> MyGpuNames()
        {
            if (myGpus != null) return myGpus;
            var gpus = new List<string>();
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    if (cls != null)
                        foreach (var name in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                            using (var k = cls.OpenSubKey(name))
                            {
                                string desc = k != null ? Convert.ToString(k.GetValue("DriverDesc")) : "";
                                if (desc.Length > 0 && desc.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) < 0 && !gpus.Contains(desc)) gpus.Add(desc);
                            }
            }
            catch (Exception) { }
            myGpus = gpus;
            return myGpus;
        }
    }

    /// <summary>
    /// Rough benchmark scores for processors and graphics cards (in the style of PassMark's CPU Mark / G3D Mark), enough to
    /// tell "better or worse than this game asks for". Unknown models in a known family are estimated from their generation.
    /// </summary>
    internal static class PartScores
    {
        public static double Gpu(string name) { string k; return Gpu(name, out k); }
        public static double Cpu(string name) { string k; return Cpu(name, out k); }

        // ---------------- graphics ----------------

        private static readonly Dictionary<string, double> Gpus = new Dictionary<string, double>
        {
            // NVIDIA
            { "gtx 260", 1100 }, { "gtx 275", 1300 }, { "gtx 280", 1300 }, { "gtx 285", 1400 }, { "gtx 295", 1700 }, { "gts 250", 900 }, { "gts 450", 1300 },
            { "gt 430", 550 }, { "gt 440", 650 }, { "gt 630", 750 }, { "gt 640", 1100 }, { "gtx 460", 1900 }, { "gtx 465", 2100 }, { "gtx 470", 2600 },
            { "gtx 480", 2900 }, { "gtx 550 ti", 1600 }, { "gtx 555", 2000 }, { "gtx 560", 2600 }, { "gtx 560 ti", 3000 }, { "gtx 570", 3500 }, { "gtx 580", 4100 },
            { "gtx 590", 4500 }, { "9800 gt", 800 }, { "9800 gtx", 900 }, { "8800 gt", 700 }, { "8800 gtx", 750 },
            { "hd 4850", 900 }, { "hd 4870", 1100 }, { "hd 5670", 800 }, { "hd 5750", 1300 }, { "hd 5850", 1900 }, { "hd 5870", 2200 }, { "hd 6570", 800 },
            { "hd 6670", 1000 }, { "hd 6770", 1500 }, { "hd 6790", 1800 },
            { "intel hd 2000", 250 }, { "intel hd 3000", 400 }, { "intel hd 4000", 550 }, { "intel hd 4600", 700 }, { "intel hd 5000", 800 },
            { "intel hd 500", 950 }, { "intel hd 600", 1100 },
            { "gt 710", 640 }, { "gt 730", 900 }, { "gt 1030", 2600 }, { "gtx 650", 1800 }, { "gtx 650 ti", 2600 }, { "gtx 660", 4100 }, { "gtx 660 ti", 4600 },
            { "gtx 670", 5500 }, { "gtx 680", 5900 }, { "gtx 690", 7000 }, { "gtx 745", 2700 }, { "gtx 750", 3300 }, { "gtx 750 ti", 3900 }, { "gtx 760", 4900 },
            { "gtx 770", 6000 }, { "gtx 780", 7000 }, { "gtx 780 ti", 8000 }, { "gtx 950", 5300 }, { "gtx 960", 6000 }, { "gtx 970", 9600 }, { "gtx 980", 11000 },
            { "gtx 980 ti", 13700 }, { "gtx titan x", 13000 }, { "gtx 1050", 4900 }, { "gtx 1050 ti", 6300 }, { "gtx 1060", 10000 }, { "gtx 1070", 13500 },
            { "gtx 1070 ti", 14800 }, { "gtx 1080", 15400 }, { "gtx 1080 ti", 18500 }, { "gtx 1630", 4700 }, { "gtx 1650", 7800 }, { "gtx 1650 super", 10000 },
            { "gtx 1660", 11500 }, { "gtx 1660 super", 12700 }, { "gtx 1660 ti", 12800 }, { "rtx 2060", 14000 }, { "rtx 2060 super", 16300 }, { "rtx 2070", 16300 },
            { "rtx 2070 super", 18200 }, { "rtx 2080", 19000 }, { "rtx 2080 super", 20000 }, { "rtx 2080 ti", 21800 }, { "rtx 3050", 12800 }, { "rtx 3060", 17000 },
            { "rtx 3060 ti", 20300 }, { "rtx 3070", 22300 }, { "rtx 3070 ti", 23500 }, { "rtx 3080", 25200 }, { "rtx 3080 ti", 26900 }, { "rtx 3090", 26500 },
            { "rtx 3090 ti", 29500 }, { "rtx 4060", 19600 }, { "rtx 4060 ti", 22500 }, { "rtx 4070", 26800 }, { "rtx 4070 super", 30000 }, { "rtx 4070 ti", 31600 },
            { "rtx 4070 ti super", 32000 }, { "rtx 4080", 34500 }, { "rtx 4080 super", 35000 }, { "rtx 4090", 38500 }, { "rtx 5050", 18000 }, { "rtx 5060", 22000 },
            { "rtx 5060 ti", 25000 }, { "rtx 5070", 29500 }, { "rtx 5070 ti", 33000 }, { "rtx 5080", 36500 }, { "rtx 5090", 44000 },
            // AMD
            { "hd 5770", 1500 }, { "hd 6850", 2000 }, { "hd 6870", 2300 }, { "hd 6950", 2900 }, { "hd 6970", 3100 }, { "hd 7750", 2100 }, { "hd 7770", 2700 },
            { "hd 7790", 3200 }, { "hd 7850", 4000 }, { "hd 7870", 5000 }, { "hd 7950", 5600 }, { "hd 7970", 6600 }, { "r7 260x", 3000 }, { "r7 360", 2800 },
            { "r7 370", 4000 }, { "r9 270", 4400 }, { "r9 270x", 4900 }, { "r9 280", 5700 }, { "r9 280x", 6600 }, { "r9 285", 5800 }, { "r9 290", 8300 },
            { "r9 290x", 8800 }, { "r9 380", 5700 }, { "r9 380x", 6200 }, { "r9 390", 8600 }, { "r9 390x", 8900 }, { "r9 fury", 9800 }, { "r9 fury x", 10000 },
            { "rx 460", 3800 }, { "rx 470", 7200 }, { "rx 480", 8200 }, { "rx 550", 2700 }, { "rx 560", 3800 }, { "rx 570", 7400 }, { "rx 580", 8500 },
            { "rx 590", 9200 }, { "rx vega 56", 12500 }, { "rx vega 64", 13800 }, { "radeon vii", 15800 }, { "rx 5500 xt", 9000 }, { "rx 5600 xt", 13500 },
            { "rx 5700", 15000 }, { "rx 5700 xt", 16500 }, { "rx 6400", 6800 }, { "rx 6500 xt", 7500 }, { "rx 6600", 15000 }, { "rx 6600 xt", 16800 },
            { "rx 6650 xt", 17500 }, { "rx 6700", 18500 }, { "rx 6700 xt", 19800 }, { "rx 6750 xt", 20500 }, { "rx 6800", 23000 }, { "rx 6800 xt", 25500 },
            { "rx 6900 xt", 26800 }, { "rx 6950 xt", 28000 }, { "rx 7600", 17000 }, { "rx 7600 xt", 17500 }, { "rx 7700 xt", 23000 }, { "rx 7800 xt", 26000 },
            { "rx 7900 gre", 27000 }, { "rx 7900 xt", 30000 }, { "rx 7900 xtx", 32500 }, { "rx 9060 xt", 22000 }, { "rx 9070", 29000 }, { "rx 9070 xt", 31500 },
            // Intel
            { "arc a380", 6000 }, { "arc a580", 13000 }, { "arc a750", 14500 }, { "arc a770", 16000 }, { "arc b570", 17000 }, { "arc b580", 18500 },
            { "iris xe", 2800 }, { "uhd", 1300 }, { "hd graphics", 1000 },
        };

        public static double Gpu(string name, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(name)) return 0;
            string s = " " + name.ToLowerInvariant().Replace("-", " ") + " ";
            s = Regex.Replace(s, @"\b(nvidia|geforce|amd|ati|radeon\(tm\)|\(tm\)|\(r\)|graphics card|series)\b", " ");
            s = Regex.Replace(s, @"\s+", " ");
            Match m;
            if ((m = Regex.Match(s, @"\b(gtx|rtx|gt)\s*(\d{3,4})\s*(ti super|ti|super)?\b")).Success)
                key = (m.Groups[1].Value + " " + m.Groups[2].Value + (m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? " " + m.Groups[3].Value : "")).Trim();
            else if ((m = Regex.Match(s, @"\btitan x\b")).Success) key = "gtx titan x";
            else if ((m = Regex.Match(s, @"\brx\s*vega\s*(56|64)\b")).Success) key = "rx vega " + m.Groups[1].Value;
            else if ((m = Regex.Match(s, @"\bradeon vii\b")).Success) key = "radeon vii";
            else if ((m = Regex.Match(s, @"\brx\s*(\d{3,4})\s*(xtx|xt|gre)?\b")).Success) key = ("rx " + m.Groups[1].Value + " " + m.Groups[2].Value).Trim();
            else if ((m = Regex.Match(s, @"\br9\s*fury\s*(x)?\b")).Success) key = "r9 fury" + (m.Groups[1].Success ? " x" : "");
            else if ((m = Regex.Match(s, @"\b(r[79])\s*(\d{3})\s*(x)?\b")).Success) key = m.Groups[1].Value + " " + m.Groups[2].Value + (m.Groups[3].Success ? "x" : "");
            else if ((m = Regex.Match(s, @"\b(9800|8800)\s*(gtx|gt)\b")).Success) key = m.Groups[1].Value + " " + m.Groups[2].Value;
            else if (s.Contains("intel") && (m = Regex.Match(s, @"\b(?:hd\s*(?:graphics\s*)?)?(\d)(\d{2,3}|x{2,3})\b")).Success)
            {
                // Intel HD 4000 / 4600 / 530 / 630, or "Intel 4xxx series"
                string d = m.Groups[1].Value, rest = m.Groups[2].Value;
                if (rest.Length == 3) key = d == "4" && rest.StartsWith("6") ? "intel hd 4600" : "intel hd " + d + "000";
                else key = d == "5" ? "intel hd 500" : d == "6" ? "intel hd 600" : null;
            }
            else if ((m = Regex.Match(s, @"\bhd\s*(\d{4})\b")).Success && s.Contains("intel") == false) key = "hd " + m.Groups[1].Value;
            else if ((m = Regex.Match(s, @"\barc\s*([ab]\d{3})\b")).Success) key = "arc " + m.Groups[1].Value;
            else if (s.Contains("iris xe")) key = "iris xe";
            else if (Regex.IsMatch(s, @"\buhd\b")) key = "uhd";
            else if (s.Contains("hd graphics")) key = "hd graphics";
            if (key == null)
            {
                // no model named: estimate from the video memory it asks for ("DirectX 11 card with 2 GB")
                var vm = Regex.Match(s, @"(\d+(?:[.,]\d+)?)\s*(gb|mb)\s*(?:of\s*)?(?:vram|video|dedicated|graphics|gddr|memory)?", RegexOptions.IgnoreCase);
                if (vm.Success && Regex.IsMatch(s, @"vram|video|dedicated|graphics|gpu|card|gddr"))
                {
                    double amt;
                    if (double.TryParse(vm.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amt))
                    {
                        double gb = vm.Groups[2].Value.ToLowerInvariant() == "mb" ? amt / 1024 : amt;
                        key = "vram " + gb;
                        return gb <= 0.5 ? 700 : gb <= 1 ? 1500 : gb <= 2 ? 4000 : gb <= 3 ? 6000 : gb <= 4 ? 8000 : gb <= 6 ? 11000 : gb <= 8 ? 15000 : 20000;
                    }
                }
                return 0;
            }
            double v;
            if (Gpus.TryGetValue(key, out v)) return v;
            // "rx 6700 xt" known as "rx 6700"? try without the suffix, a bit lower
            string bare = Regex.Replace(key, @"\s+(ti super|ti|super|xtx|xt|gre)$", "");
            if (bare != key && Gpus.TryGetValue(bare, out v)) return v;
            return 0;
        }

        // ---------------- processors ----------------

        private static readonly Dictionary<string, double> Cpus = new Dictionary<string, double>
        {
            // Intel
            { "i3-2100", 2000 }, { "i3-3220", 2300 }, { "i3-4130", 2900 }, { "i3-4160", 3000 }, { "i3-6100", 3600 }, { "i3-7100", 3900 }, { "i3-8100", 6100 },
            { "i3-9100", 6900 }, { "i3-10100", 8800 }, { "i3-12100", 13800 }, { "i3-13100", 14800 },
            { "i5-750", 2500 }, { "i5-760", 2600 }, { "i5-2300", 3300 }, { "i5-2400", 3800 }, { "i5-2500", 4000 }, { "i5-2500k", 4100 }, { "i5-3330", 4300 },
            { "i5-3470", 4700 }, { "i5-3570", 4800 }, { "i5-3570k", 4900 }, { "i5-4430", 4600 }, { "i5-4460", 5000 }, { "i5-4590", 5300 }, { "i5-4670", 5400 },
            { "i5-4670k", 5500 }, { "i5-4690", 5500 }, { "i5-4690k", 5600 }, { "i5-6400", 5300 }, { "i5-6500", 5700 }, { "i5-6600", 6100 }, { "i5-6600k", 6400 },
            { "i5-7400", 5900 }, { "i5-7500", 6400 }, { "i5-7600", 6900 }, { "i5-7600k", 7200 }, { "i5-8400", 9200 }, { "i5-8500", 9600 }, { "i5-8600k", 10200 },
            { "i5-9400", 9500 }, { "i5-9600k", 10700 }, { "i5-10400", 12200 }, { "i5-10600k", 14400 }, { "i5-11400", 16900 }, { "i5-11600k", 19300 },
            { "i5-12400", 19500 }, { "i5-12600k", 27600 }, { "i5-13400", 25500 }, { "i5-13600k", 38000 }, { "i5-14400", 26000 }, { "i5-14600k", 39000 },
            { "i7-920", 2800 }, { "i7-950", 3000 }, { "i7-2600", 5100 }, { "i7-2600k", 5300 }, { "i7-3770", 6300 }, { "i7-3770k", 6500 }, { "i7-4770", 7000 },
            { "i7-4770k", 7200 }, { "i7-4790", 7200 }, { "i7-4790k", 8000 }, { "i7-5820k", 9600 }, { "i7-6700", 8000 }, { "i7-6700k", 8900 }, { "i7-7700", 8600 },
            { "i7-7700k", 9700 }, { "i7-8700", 13000 }, { "i7-8700k", 13900 }, { "i7-9700", 13700 }, { "i7-9700k", 14500 }, { "i7-10700", 17500 },
            { "i7-10700k", 19300 }, { "i7-11700k", 24500 }, { "i7-12700", 31000 }, { "i7-12700k", 34500 }, { "i7-13700k", 46000 }, { "i7-14700k", 53000 },
            { "i9-9900k", 18800 }, { "i9-10900k", 23500 }, { "i9-11900k", 25500 }, { "i9-12900k", 41500 }, { "i9-13900k", 59500 }, { "i9-14900k", 60500 },
            { "ultra 5 245k", 42000 }, { "ultra 7 265k", 58000 }, { "ultra 9 285k", 67000 },
            // AMD
            { "fx-4300", 3100 }, { "fx-6300", 4300 }, { "fx-6350", 4600 }, { "fx-8320", 5900 }, { "fx-8350", 6000 }, { "fx-9590", 6900 },
            { "ryzen 3 1200", 6300 }, { "ryzen 3 1300x", 7000 }, { "ryzen 3 2200g", 6800 }, { "ryzen 3 3100", 11500 }, { "ryzen 3 3200g", 7200 }, { "ryzen 3 3300x", 12700 },
            { "ryzen 5 1400", 7700 }, { "ryzen 5 1500x", 9100 }, { "ryzen 5 1600", 12300 }, { "ryzen 5 1600x", 12900 }, { "ryzen 5 2400g", 8800 }, { "ryzen 5 2600", 13200 },
            { "ryzen 5 2600x", 14100 }, { "ryzen 5 3400g", 9300 }, { "ryzen 5 3500", 12500 }, { "ryzen 5 3600", 17700 }, { "ryzen 5 3600x", 18300 }, { "ryzen 5 4500", 16200 },
            { "ryzen 5 5500", 19500 }, { "ryzen 5 5600", 21500 }, { "ryzen 5 5600x", 21900 }, { "ryzen 5 5600g", 19800 }, { "ryzen 5 7500f", 27500 },
            { "ryzen 5 7600", 27000 }, { "ryzen 5 7600x", 28700 }, { "ryzen 5 9600x", 30000 },
            { "ryzen 7 1700", 14600 }, { "ryzen 7 1700x", 15300 }, { "ryzen 7 1800x", 16400 }, { "ryzen 7 2700", 16500 }, { "ryzen 7 2700x", 17600 },
            { "ryzen 7 3700x", 22700 }, { "ryzen 7 3800x", 23700 }, { "ryzen 7 5700x", 26700 }, { "ryzen 7 5700x3d", 26000 }, { "ryzen 7 5800x", 28000 },
            { "ryzen 7 5800x3d", 28200 }, { "ryzen 7 7700", 34500 }, { "ryzen 7 7700x", 36000 }, { "ryzen 7 7800x3d", 34500 }, { "ryzen 7 9700x", 37000 },
            { "ryzen 7 9800x3d", 40000 }, { "ryzen 9 3900x", 32500 }, { "ryzen 9 3950x", 39000 }, { "ryzen 9 5900x", 39200 }, { "ryzen 9 5950x", 46000 },
            { "ryzen 9 7900x", 51500 }, { "ryzen 9 7950x", 63000 }, { "ryzen 9 7950x3d", 62500 }, { "ryzen 9 9900x", 54000 }, { "ryzen 9 9950x", 66000 },
            { "ryzen 9 9950x3d", 68000 },
        };

        // estimates for models not in the list: an i5 / Ryzen 5 of that generation, scaled by tier
        private static readonly Dictionary<int, double> IntelGen = new Dictionary<int, double> { { 2, 3800 }, { 3, 4600 }, { 4, 5100 }, { 5, 5300 }, { 6, 5700 }, { 7, 6200 }, { 8, 9500 }, { 9, 10000 }, { 10, 12500 }, { 11, 17000 }, { 12, 22000 }, { 13, 27000 }, { 14, 28000 } };
        private static readonly Dictionary<int, double> RyzenGen = new Dictionary<int, double> { { 1, 12300 }, { 2, 13200 }, { 3, 17700 }, { 4, 16200 }, { 5, 21500 }, { 7, 27000 }, { 8, 26000 }, { 9, 30000 } };

        public static double Cpu(string name, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(name)) return 0;
            string s = " " + Regex.Replace(name.ToLowerInvariant(), @"\((?:tm|r)\)|®|™", " ") + " ";
            s = Regex.Replace(s, @"\s+", " ");
            Match m;
            if ((m = Regex.Match(s, @"\bi([3579])\s*-?\s*(\d{3,5})([a-z]{0,2})\b")).Success)
            {
                string suffix = m.Groups[3].Value.Contains("k") ? "k" : "";
                key = "i" + m.Groups[1].Value + "-" + m.Groups[2].Value + suffix;
                double v;
                if (Cpus.TryGetValue(key, out v)) return v;
                if (suffix.Length > 0 && Cpus.TryGetValue("i" + m.Groups[1].Value + "-" + m.Groups[2].Value, out v)) return v * 1.05;
                if (suffix.Length == 0 && Cpus.TryGetValue(key + "k", out v)) return v * 0.95;
                string num = m.Groups[2].Value;
                int gen = num.Length == 5 ? int.Parse(num.Substring(0, 2)) : num.Length == 4 ? int.Parse(num.Substring(0, 1)) : 1;
                double baseScore;
                if (!IntelGen.TryGetValue(gen, out baseScore)) return 0;
                double tier = m.Groups[1].Value == "3" ? 0.7 : m.Groups[1].Value == "5" ? 1 : m.Groups[1].Value == "7" ? 1.35 : 1.7;
                return baseScore * tier * (suffix.Length > 0 ? 1.08 : 1);
            }
            if ((m = Regex.Match(s, @"\bultra\s*([579])\s*(\d{3})([a-z]{0,2})\b")).Success)
            {
                key = "ultra " + m.Groups[1].Value + " " + m.Groups[2].Value + (m.Groups[3].Value.Contains("k") ? "k" : "");
                double v;
                if (Cpus.TryGetValue(key, out v)) return v;
                return m.Groups[1].Value == "5" ? 38000 : m.Groups[1].Value == "7" ? 50000 : 60000;   // desktop Core Ultra 200 class
            }
            if ((m = Regex.Match(s, @"\bryzen\s*([3579])\s*(?:pro\s*)?(\d{4})(x3d|x|g|f|xt|ge)?\b")).Success)
            {
                string suffix = m.Groups[3].Value;
                key = "ryzen " + m.Groups[1].Value + " " + m.Groups[2].Value + suffix;
                double v;
                if (Cpus.TryGetValue(key, out v)) return v;
                if (suffix == "xt" && Cpus.TryGetValue("ryzen " + m.Groups[1].Value + " " + m.Groups[2].Value + "x", out v)) return v * 1.03;
                if (Cpus.TryGetValue("ryzen " + m.Groups[1].Value + " " + m.Groups[2].Value, out v)) return v;
                int gen = int.Parse(m.Groups[2].Value.Substring(0, 1));
                double baseScore;
                if (!RyzenGen.TryGetValue(gen, out baseScore)) return 0;
                double tier = m.Groups[1].Value == "3" ? 0.65 : m.Groups[1].Value == "5" ? 1 : m.Groups[1].Value == "7" ? 1.35 : 2;
                return baseScore * tier;
            }
            if ((m = Regex.Match(s, @"\bfx\s*-?\s*(\d{4})\b")).Success)
            {
                key = "fx-" + m.Groups[1].Value;
                double v;
                return Cpus.TryGetValue(key, out v) ? v : 4500;
            }
            // no model number: a rough guess from what it does say ("Intel i5 2 GHz", "Core 2 Duo", "quad-core")
            if ((m = Regex.Match(s, @"\b(?:core\s*)?i([3579])\b")).Success) { key = "i" + m.Groups[1].Value; return m.Groups[1].Value == "3" ? 2200 : m.Groups[1].Value == "5" ? 3800 : m.Groups[1].Value == "7" ? 5100 : 9000; }
            if ((m = Regex.Match(s, @"\bryzen\s*([3579])\b")).Success) { key = "ryzen " + m.Groups[1].Value; return m.Groups[1].Value == "3" ? 6300 : m.Groups[1].Value == "5" ? 12300 : m.Groups[1].Value == "7" ? 14600 : 30000; }
            if (Regex.IsMatch(s, @"core\s*2\s*quad|q6600|q9550")) { key = "core 2 quad"; return 2000; }
            if (Regex.IsMatch(s, @"core\s*2\s*duo|e8400|e6600|athlon\s*(64\s*)?x2|athlon ii x2")) { key = "core 2 duo"; return 1100; }
            if (Regex.IsMatch(s, @"phenom\s*ii\s*x4|athlon\s*ii\s*x4")) { key = "phenom ii x4"; return 2400; }
            if (Regex.IsMatch(s, @"phenom\s*ii\s*x6")) { key = "phenom ii x6"; return 3800; }
            if (Regex.IsMatch(s, @"\b(8|eight)[- ]?core|octa[- ]?core")) { key = "8 cores"; return 9000; }
            if (Regex.IsMatch(s, @"\b(6|six)[- ]?core|hexa[- ]?core")) { key = "6 cores"; return 6500; }
            if (Regex.IsMatch(s, @"\b(4|quad)[- ]?core")) { key = "4 cores"; return 3500; }
            if (Regex.IsMatch(s, @"\b(2|dual)[- ]?core|pentium|celeron")) { key = "2 cores"; return 1500; }
            return 0;
        }
    }
}

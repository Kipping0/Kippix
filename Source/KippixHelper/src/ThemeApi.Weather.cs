using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Input;
using System.Windows.Threading;

namespace KippixHelper
{
    /// <summary>
    /// Weather location: type a town, a city, a postcode / ZIP, with the country or state if you like
    /// ("Marlow, UK", "Marlow (UK)", "SL7 1HZ", "Austin TX", "90210"). Several matches: you pick one.
    /// Open-Meteo first (fast, no key), postcodes.io for UK postcodes, OpenStreetMap's search when those find nothing.
    /// </summary>
    public partial class ThemeApi
    {
        private class GeoHit
        {
            public string Name, Detail, CountryCode;
            public double Lat, Lon;
            public string Full { get { return Detail.Length > 0 ? Name + ", " + Detail : Name; } }
        }

        public ObservableCollection<PickOption> WeatherChoices { get; private set; }
        public bool IsOptionsWeatherPick { get { return optionsPage == "weatherpick"; } }
        private bool weatherSearching;
        private string weatherStatus = "";
        public string WeatherStatus { get { return weatherStatus; } }
        public bool HasWeatherStatus { get { return weatherStatus.Length > 0; } }

        private void SetWeatherStatus(string s)
        {
            weatherStatus = s ?? "";
            Notify("WeatherStatus");
            Notify("HasWeatherStatus");
        }

        // ---------------- the on-screen keyboard's hint (shown above the text box) ----------------

        private string keyboardHint = "";
        public string KeyboardHint { get { return keyboardHint; } }
        public bool HasKeyboardHint { get { return keyboardHint.Length > 0; } }

        /// <summary>The on-screen keyboard with a line above the box saying what to type.</summary>
        private string AskText(string hint, string title, string current)
        {
            keyboardHint = hint ?? "";
            Notify("KeyboardHint");
            Notify("HasKeyboardHint");
            try
            {
                var r = api.Dialogs.SelectString(hint ?? "", title ?? "", current ?? "");
                return r != null && r.Result ? (r.SelectedString ?? "") : null;
            }
            finally
            {
                keyboardHint = "";
                Notify("KeyboardHint");
                Notify("HasKeyboardHint");
            }
        }

        // ---------------- asking ----------------

        private void AskWeatherTown()
        {
            if (weatherSearching) return;
            OpenKeyboard(Loc("LOC_KX_WeatherAsk", "Type your town or city, or a postcode / ZIP. Add the country or state if there are others with that name, e.g. London, UK · SW1A 1AA · Austin, TX · 90210"),
                         settings.WeatherTown ?? "", WeatherTownTyped);
        }

        private void WeatherTownTyped(string text)
        {
            if (text == null || weatherSearching) return;
            text = text.Trim();
            if (text.Length == 0)
            {
                settings.WeatherTown = "";
                settings.WeatherLat = settings.WeatherLon = 0;
                SaveSettings();
                SetWeatherStatus("");
                NotifyWidgetEdit();
                RefreshWidgetsOfType("weather");
                // no town: Weather touches that follow it stop showing the old town's weather
                wxLiveKind = null;
                wxLiveWord = "";
                seasonalBuiltFor = null;
                Notify("SeasonalShown");
                Notify("WeatherFxSummary");
                UpdateSeasonal();
                if (openPanel == "options" && optionsPage == "weatherfx") BoardRowsAgain(BuildWeatherFxRows, "wxtown");
                return;
            }
            if (text == settings.WeatherTown) return;   // the box starts with what's set: OK without changing it
            weatherSearching = true;
            SetWeatherStatus(string.Format(Loc("LOC_KX_WeatherLooking", "Looking up \"{0}\"…"), text));
            // its own thread: the thread pool can be busy with pictures and the like
            var t = new Thread(() =>
            {
                List<GeoHit> hits = null;
                bool failed = false;
                var watch = Stopwatch.StartNew();
                try { hits = FindPlaces(text); }
                catch (Exception ex) { failed = true; logger.Warn("Kippix: weather place: " + ex.Message); }
                logger.Info("Kippix: weather place lookup took " + watch.ElapsedMilliseconds + " ms");
                OnUi(() =>
                {
                    weatherSearching = false;
                    if (hits == null || hits.Count == 0)
                    {
                        SetWeatherStatus(failed ? Loc("LOC_KX_WeatherNoNet", "Couldn't reach the weather service. Check the connection and try again.")
                                                : string.Format(Loc("LOC_KX_WeatherNotFound", "Couldn't find \"{0}\". Try the town and country, e.g. London, UK, or a postcode."), text));
                        PlaySound("back");
                        return;
                    }
                    if (hits.Count == 1) { UseWeatherPlace(hits[0]); return; }
                    ShowWeatherChoices(hits);
                });
            }) { IsBackground = true, Name = "Kippix weather place" };
            t.Start();
        }

        private void ShowWeatherChoices(List<GeoHit> hits)
        {
            WeatherChoices.Clear();
            foreach (var h in hits)
            {
                var hit = h;
                WeatherChoices.Add(new PickOption { Text = hit.Full, Command = new RelayCommand(_ =>
                {
                    PlaySound("select");
                    UseWeatherPlace(hit);
                    if (weatherPickFrom == "weatherfx") GoBackTo("weatherfx", "wxtown"); else GoBackTo("widgets", "weather");
                }) });
            }
            SetWeatherStatus("");
            if (openPanel == "options" && (optionsPage == "widgets" || optionsPage == "weatherfx" || optionsPage == "weatherpick"))
            {
                if (optionsPage != "weatherpick") weatherPickFrom = optionsPage;
                SetOptionsPage("weatherpick");
            }
            else UseWeatherPlace(hits[0]);   // the menu was closed meanwhile: take the best match
        }

        private string weatherPickFrom;   // the page "Which one?" goes back to (Widgets, or Weather touches)

        private void UseWeatherPlace(GeoHit h)
        {
            settings.WeatherTown = h.Full;
            settings.WeatherLat = Math.Round(h.Lat, 4);
            settings.WeatherLon = Math.Round(h.Lon, 4);
            SaveSettings();
            SetWeatherStatus("");
            NotifyWidgetEdit();
            foreach (var w in Widgets.Where(x => x.Type == "weather")) w.Status = Loc("LOC_KX_WeatherLoading", "Getting the weather…");
            RefreshWidgetsOfType("weather");
            // Weather touches follow the same town
            wxLiveAt = DateTime.MinValue;
            wxLiveWord = "";
            Notify("WeatherFxSummary");
            if (openPanel == "options" && optionsPage == "weatherfx") BoardRowsAgain(BuildWeatherFxRows, "wxtown");
        }

        // ---------------- finding the place ----------------

        private static readonly Regex UkPostcode = new Regex(@"^([A-Z]{1,2}[0-9][A-Z0-9]?)\s*([0-9][A-Z]{2})?$", RegexOptions.IgnoreCase);

        private List<GeoHit> FindPlaces(string text)
        {
            // "Marlow (UK)" / "Marlow, UK" / "Austin TX": the name, then what narrows it down
            string clean = text.Replace("(", ",").Replace(")", ",").Replace(";", ",");
            var parts = clean.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) return new List<GeoHit>();
            string name = parts[0];
            var quals = parts.Skip(1).ToList();
            if (quals.Count == 0)
            {
                // no comma: a known country / state word at the end ("Marlow UK", "Austin TX")
                var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (words.Count > 1 && !Regex.IsMatch(name, @"\d") && IsRegionWord(words[words.Count - 1]))
                {
                    quals.Add(words[words.Count - 1]);
                    name = string.Join(" ", words.Take(words.Count - 1));
                }
            }

            // UK postcode (full or just the first half, like SL7)
            var pc = UkPostcode.Match(text.Trim());
            if (pc.Success && Regex.IsMatch(text, @"\d"))
            {
                var uk = UkPostcodeLookup(pc.Groups[1].Value, pc.Groups[2].Success ? pc.Groups[2].Value : "");
                if (uk != null) return new List<GeoHit> { uk };
            }

            var found = new List<GeoHit>();
            if (!Regex.IsMatch(name, @"^\d"))   // names: Open-Meteo (postcodes / ZIPs go to the next search)
            {
                string cc = quals.Select(CountryCodeFor).FirstOrDefault(c => c != null);
                var root = QuickJson("https://geocoding-api.open-meteo.com/v1/search?count=30&language=en&format=json&name=" + Uri.EscapeDataString(name)
                                     + (cc != null ? "&countryCode=" + cc : ""), 8000) as Dictionary<string, object>;
                foreach (var r in (Dig(root, "results") as IEnumerable ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>())
                {
                    var h = new GeoHit
                    {
                        Name = FStr(r, "name"),
                        CountryCode = FStr(r, "country_code").ToUpperInvariant(),
                        Lat = FDouble(r, "latitude"),
                        Lon = FDouble(r, "longitude")
                    };
                    var fields = new[] { FStr(r, "admin2"), FStr(r, "admin1"), FStr(r, "country") };
                    if (!quals.All(q => QualMatches(q, h.CountryCode, fields))) continue;
                    h.Detail = string.Join(", ", new[] { FStr(r, "admin2"), FStr(r, "admin1"), CountryShort(h.CountryCode, FStr(r, "country")) }
                                              .Where(s => s.Length > 0 && !s.Equals(h.Name, StringComparison.OrdinalIgnoreCase)).Distinct());
                    found.Add(h);
                }
            }
            if (found.Count == 0) found = OsmSearch(text);
            // the same place twice (different spellings): keep the first
            var outList = new List<GeoHit>();
            foreach (var h in found)
                if (!outList.Any(o => o.Full.Equals(h.Full, StringComparison.OrdinalIgnoreCase) || (Math.Abs(o.Lat - h.Lat) < 0.02 && Math.Abs(o.Lon - h.Lon) < 0.02))) outList.Add(h);
            return outList.Take(8).ToList();
        }

        private GeoHit UkPostcodeLookup(string outward, string inward)
        {
            try
            {
                string url = inward.Length > 0 ? "https://api.postcodes.io/postcodes/" + Uri.EscapeDataString(outward + inward)
                                               : "https://api.postcodes.io/outcodes/" + Uri.EscapeDataString(outward);
                var r = Dig(QuickJson(url, 6000), "result") as Dictionary<string, object>;
                if (r == null) return null;
                object district;
                r.TryGetValue("admin_district", out district);
                string place = district is IEnumerable && !(district is string)
                    ? string.Join(" / ", ((IEnumerable)district).Cast<object>().Select(Convert.ToString).Take(2))
                    : Convert.ToString(district ?? "", CultureInfo.InvariantCulture);
                string code = (outward + (inward.Length > 0 ? " " + inward : "")).ToUpperInvariant();
                return new GeoHit
                {
                    Name = place.Length > 0 ? place : code,
                    Detail = (place.Length > 0 ? code + ", " : "") + "UK",
                    CountryCode = "GB",
                    Lat = FDouble(r, "latitude"),
                    Lon = FDouble(r, "longitude")
                };
            }
            catch (Exception ex)
            {
                // not a real postcode (404), or postcodes.io is down: try it as a name instead
                logger.Info("Kippix: postcode lookup: " + ex.Message);
                return null;
            }
        }

        /// <summary>OpenStreetMap's place search (towns, postcodes, ZIPs anywhere). One search when you press OK, as their rules ask.</summary>
        private List<GeoHit> OsmSearch(string text)
        {
            var list = new List<GeoHit>();
            var arr = QuickJson("https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&limit=8&accept-language=en&q=" + Uri.EscapeDataString(text), 8000) as IEnumerable;
            foreach (var r in (arr ?? new object[0]).Cast<object>().OfType<Dictionary<string, object>>())
            {
                var a = Dig(r, "address") as Dictionary<string, object> ?? new Dictionary<string, object>();
                string town = new[] { "city", "town", "village", "hamlet", "suburb", "municipality", "county" }.Select(k => FStr(a, k)).FirstOrDefault(s => s.Length > 0) ?? "";
                string name = town.Length > 0 ? town : FStr(r, "name");
                if (name.Length == 0) name = FStr(r, "display_name").Split(',')[0];
                string cc = FStr(a, "country_code").ToUpperInvariant();
                var detail = new[] { FStr(a, "postcode"), FStr(a, "county"), FStr(a, "state"), CountryShort(cc, FStr(a, "country")) }
                             .Where(s => s.Length > 0 && !s.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct();
                list.Add(new GeoHit { Name = name, Detail = string.Join(", ", detail), CountryCode = cc, Lat = FDouble(r, "lat"), Lon = FDouble(r, "lon") });
            }
            return list;
        }

        // ---------------- countries and states ----------------

        private static readonly Dictionary<string, string> CountryWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "uk", "GB" }, { "u.k.", "GB" }, { "gb", "GB" }, { "great britain", "GB" }, { "britain", "GB" }, { "united kingdom", "GB" },
            { "england", "GB" }, { "scotland", "GB" }, { "wales", "GB" }, { "northern ireland", "GB" },
            { "us", "US" }, { "usa", "US" }, { "u.s.", "US" }, { "u.s.a.", "US" }, { "america", "US" }, { "united states", "US" },
            { "ireland", "IE" }, { "canada", "CA" }, { "australia", "AU" }, { "new zealand", "NZ" }, { "nz", "NZ" },
            { "germany", "DE" }, { "france", "FR" }, { "spain", "ES" }, { "italy", "IT" }, { "netherlands", "NL" }, { "holland", "NL" },
            { "belgium", "BE" }, { "sweden", "SE" }, { "norway", "NO" }, { "denmark", "DK" }, { "finland", "FI" }, { "poland", "PL" },
            { "portugal", "PT" }, { "austria", "AT" }, { "switzerland", "CH" }, { "japan", "JP" }, { "india", "IN" }, { "brazil", "BR" },
            { "mexico", "MX" }, { "south africa", "ZA" }
        };

        // US states, Canadian provinces and Australian states by their short codes
        private static readonly Dictionary<string, string> StateCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AL", "Alabama" }, { "AK", "Alaska" }, { "AZ", "Arizona" }, { "AR", "Arkansas" }, { "CA", "California" }, { "CO", "Colorado" },
            { "CT", "Connecticut" }, { "DE", "Delaware" }, { "FL", "Florida" }, { "GA", "Georgia" }, { "HI", "Hawaii" }, { "ID", "Idaho" },
            { "IL", "Illinois" }, { "IN", "Indiana" }, { "IA", "Iowa" }, { "KS", "Kansas" }, { "KY", "Kentucky" }, { "LA", "Louisiana" },
            { "ME", "Maine" }, { "MD", "Maryland" }, { "MA", "Massachusetts" }, { "MI", "Michigan" }, { "MN", "Minnesota" }, { "MS", "Mississippi" },
            { "MO", "Missouri" }, { "MT", "Montana" }, { "NE", "Nebraska" }, { "NV", "Nevada" }, { "NH", "New Hampshire" }, { "NJ", "New Jersey" },
            { "NM", "New Mexico" }, { "NY", "New York" }, { "NC", "North Carolina" }, { "ND", "North Dakota" }, { "OH", "Ohio" }, { "OK", "Oklahoma" },
            { "OR", "Oregon" }, { "PA", "Pennsylvania" }, { "RI", "Rhode Island" }, { "SC", "South Carolina" }, { "SD", "South Dakota" },
            { "TN", "Tennessee" }, { "TX", "Texas" }, { "UT", "Utah" }, { "VT", "Vermont" }, { "VA", "Virginia" }, { "WA", "Washington" },
            { "WV", "West Virginia" }, { "WI", "Wisconsin" }, { "WY", "Wyoming" }, { "DC", "District of Columbia" },
            { "ON", "Ontario" }, { "QC", "Quebec" }, { "BC", "British Columbia" }, { "AB", "Alberta" }, { "MB", "Manitoba" }, { "SK", "Saskatchewan" },
            { "NS", "Nova Scotia" }, { "NB", "New Brunswick" }, { "NL", "Newfoundland and Labrador" }, { "PE", "Prince Edward Island" },
            { "NSW", "New South Wales" }, { "VIC", "Victoria" }, { "QLD", "Queensland" }, { "SA", "South Australia" },
            { "TAS", "Tasmania" }, { "ACT", "Australian Capital Territory" }, { "NT", "Northern Territory" }
        };

        private static bool IsRegionWord(string w)
        {
            return CountryWords.ContainsKey(w) || (w.Length <= 3 && w.ToUpperInvariant() == w && StateCodes.ContainsKey(w));
        }

        private static string CountryCodeFor(string q)
        {
            string cc;
            return CountryWords.TryGetValue(q.Trim(), out cc) ? cc : null;
        }

        /// <summary>Does "UK" / "England" / "TX" / "Bucks" / "Oklahoma" fit this result?</summary>
        private static bool QualMatches(string q, string countryCode, string[] fields)
        {
            q = q.Trim();
            if (q.Length == 0) return true;
            string cc = CountryCodeFor(q);
            if (cc != null)
            {
                if (cc != countryCode) return false;
                // England / Scotland / Wales / Northern Ireland: the right one of the four
                if (cc == "GB" && new[] { "england", "scotland", "wales", "northern ireland" }.Contains(q.ToLowerInvariant()))
                    return fields.Any(f => f.Equals(q, StringComparison.OrdinalIgnoreCase));
                return true;
            }
            if (q.Equals(countryCode, StringComparison.OrdinalIgnoreCase) && q.Length == 2 && !StateCodes.ContainsKey(q)) return true;
            string state;
            if (StateCodes.TryGetValue(q, out state) && fields.Any(f => f.Equals(state, StringComparison.OrdinalIgnoreCase))) return true;
            if (q.Equals(countryCode, StringComparison.OrdinalIgnoreCase)) return true;   // "CA" can be Canada as well as California
            return fields.Any(f => f.Length > 0 && (f.Equals(q, StringComparison.OrdinalIgnoreCase)
                                                    || (q.Length >= 3 && f.StartsWith(q, StringComparison.OrdinalIgnoreCase))));
        }

        private static string CountryShort(string code, string country)
        {
            if (code == "GB") return "UK";
            if (code == "US") return "USA";
            return country ?? "";
        }

        // ---------------- quick web requests (IPv4, short timeouts) ----------------

        /// <summary>
        /// A JSON GET that answers fast: straight to the site's IPv4 address with a short timeout. On some connections IPv6 is
        /// offered but doesn't work, and Windows' own web requests then wait about 20 seconds before trying IPv4.
        /// If that fails (a proxy, an IPv6-only line), the normal web request is used instead, with a longer wait.
        /// </summary>
        private object QuickJson(string url, int timeoutMs)
        {
            var uri = new Uri(url);
            var watch = Stopwatch.StartNew();
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                object result = js.DeserializeObject(RawGet(uri, DateTime.UtcNow.AddMilliseconds(timeoutMs), 0));
                Dbg(uri.Host + " answered in " + watch.ElapsedMilliseconds + " ms");
                return result;
            }
            catch (HttpStatusException) { throw; }
            catch (Exception ex)
            {
                // a proxy, an IPv6-only line, or a broken answer: the normal way, with what's left of the time (at least 10 s)
                int left = Math.Max(10000, timeoutMs - (int)watch.ElapsedMilliseconds);
                Dbg("quick request to " + uri.Host + " failed after " + watch.ElapsedMilliseconds + " ms (" + ex.Message + "), trying the normal way for up to " + left + " ms");
                return js.DeserializeObject(SlowGet(uri, left));
            }
        }

        private class HttpStatusException : Exception
        {
            public readonly int Status;
            public HttpStatusException(int status) : base("HTTP " + status) { Status = status; }
        }

        private static int Left(DateTime deadline)
        {
            int ms = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (ms <= 0) throw new TimeoutException("timed out");
            return ms;
        }

        /// <summary>One HTTPS GET to the site's IPv4 address, all within <paramref name="deadline"/>.</summary>
        private static string RawGet(Uri uri, DateTime deadline, int redirects)
        {
            if (uri.Scheme != "https") throw new NotSupportedException("https only");
            var v4 = Dns.GetHostAddresses(uri.Host).Where(x => x.AddressFamily == AddressFamily.InterNetwork).Take(2).ToList();
            if (v4.Count == 0) throw new IOException("no IPv4 address");
            for (int i = 0; i < v4.Count; i++)
            {
                var tcp = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    var connect = tcp.ConnectAsync(v4[i], uri.Port);
                    bool connected;
                    try { connected = connect.Wait(Left(deadline)) && tcp.Connected; }
                    catch (AggregateException) { connected = false; }
                    if (!connected)
                    {
                        if (i + 1 < v4.Count && DateTime.UtcNow < deadline) continue;   // only a failed connect tries the next address
                        throw new IOException("couldn't connect");
                    }
                    using (var ssl = new SslStream(tcp.GetStream(), false))
                    {
                        ssl.ReadTimeout = ssl.WriteTimeout = Left(deadline);
                        ssl.AuthenticateAsClient(uri.Host, null, SslProtocols.Tls12, false);
                        string req = "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Host + "\r\nUser-Agent: Kippix (Playnite add-on)\r\n"
                                   + "Accept: application/json\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n";
                        var bytes = Encoding.ASCII.GetBytes(req);
                        ssl.WriteTimeout = Left(deadline);
                        ssl.Write(bytes, 0, bytes.Length);
                        ssl.Flush();
                        var ms = new MemoryStream();
                        var buf = new byte[16384];
                        while (true)
                        {
                            ssl.ReadTimeout = Left(deadline);
                            int n = ssl.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                            if (ms.Length > 8 * 1024 * 1024) throw new IOException("answer too big");
                        }
                        return ParseHttp(uri, ms.ToArray(), deadline, redirects);
                    }
                }
                finally { tcp.Close(); }
            }
            throw new IOException("no answer");
        }

        private static string ParseHttp(Uri uri, byte[] all, DateTime deadline, int redirects)
        {
            int split = -1;
            for (int i = 0; i + 3 < all.Length; i++)
                if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { split = i; break; }
            if (split < 0) throw new IOException("bad answer");
            var head = Encoding.ASCII.GetString(all, 0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var status = head[0].Split(' ');
            int code;
            if (status.Length < 2 || !int.TryParse(status[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) throw new IOException("bad status line");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in head.Skip(1))
            {
                int c = h.IndexOf(':');
                if (c > 0) headers[h.Substring(0, c).Trim()] = h.Substring(c + 1).Trim();
            }
            string location;
            if (code >= 300 && code < 400 && headers.TryGetValue("Location", out location) && redirects < 3)
                return RawGet(new Uri(uri, location), deadline, redirects + 1);
            if (code != 200) throw new HttpStatusException(code);
            byte[] body = new byte[all.Length - split - 4];
            Array.Copy(all, split + 4, body, 0, body.Length);
            string te, cl;
            if (headers.TryGetValue("Transfer-Encoding", out te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) body = Unchunk(body);
            else if (headers.TryGetValue("Content-Length", out cl))
            {
                long len;
                if (long.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out len) && body.Length < len) throw new IOException("answer cut short");
            }
            string ce;
            if (headers.TryGetValue("Content-Encoding", out ce) && ce.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(body), System.IO.Compression.CompressionMode.Decompress))
                using (var outMs = new MemoryStream()) { gz.CopyTo(outMs); body = outMs.ToArray(); }
            return Encoding.UTF8.GetString(body);
        }

        /// <summary>Chunked body back to one piece; anything cut short is an error (not half an answer).</summary>
        private static byte[] Unchunk(byte[] b)
        {
            var outMs = new MemoryStream();
            int i = 0;
            while (true)
            {
                int eol = i;
                while (eol + 1 < b.Length && !(b[eol] == 13 && b[eol + 1] == 10)) eol++;
                if (eol + 1 >= b.Length) throw new IOException("answer cut short");
                string sizeText = Encoding.ASCII.GetString(b, i, eol - i).Split(';')[0].Trim();
                int size;
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) || size < 0) throw new IOException("bad chunk");
                if (size == 0) return outMs.ToArray();   // the last chunk
                int start = eol + 2;
                if (start + size > b.Length) throw new IOException("answer cut short");
                outMs.Write(b, start, size);
                i = start + size + 2;
                if (i > b.Length) throw new IOException("answer cut short");
            }
        }

        private static string SlowGet(Uri uri, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Kippix (Playnite add-on)";
            req.Accept = "application/json";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null) throw new HttpStatusException((int)r.StatusCode);
                throw;
            }
        }
    }
}

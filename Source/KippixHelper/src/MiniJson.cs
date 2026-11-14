using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.26) A forgiving JSON reader, for answers .NET's JavaScriptSerializer turns down ("Invalid JSON primitive"):
    /// anything before the first { or [ (a byte order mark, an anti-JSON-hijacking prefix) is skipped, a missing value counts
    /// as null, unquoted keys and odd numbers (.5, 1e-05) are read. Gives the same shapes as JavaScriptSerializer:
    /// Dictionary&lt;string, object&gt;, object[], string, int / long / decimal / double, bool, null.
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            if (text == null) return null;
            int i = 0;
            while (i < text.Length && text[i] != '{' && text[i] != '[') i++;
            if (i >= text.Length) throw new FormatException("not JSON");
            return Value(text, ref i);
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '﻿')) i++;
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"' || c == '\'') return Str(s, ref i);
            if (c == ',' || c == '}' || c == ']') return null;   // (a missing value)
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            string tok = s.Substring(start, i - start);
            if (tok == "null" || tok == "undefined") return null;
            if (tok == "true") return true;
            if (tok == "false") return false;
            int n;
            if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            long l;
            if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            decimal m;
            if (decimal.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return m;
            double d;
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return tok;
        }

        private static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;   // {
            while (true)
            {
                Skip(s, ref i);
                if (i >= s.Length) return d;
                if (s[i] == '}') { i++; return d; }
                if (s[i] == ',') { i++; continue; }
                string key;
                if (s[i] == '"' || s[i] == '\'') key = Str(s, ref i);
                else
                {
                    int start = i;
                    while (i < s.Length && s[i] != ':' && !char.IsWhiteSpace(s[i]) && s[i] != '}') i++;
                    key = s.Substring(start, i - start);
                }
                Skip(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object v = Value(s, ref i);
                d[key] = v;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
            }
        }

        private static object[] Arr(string s, ref int i)
        {
            var list = new List<object>();
            i++;   // [
            while (true)
            {
                Skip(s, ref i);
                if (i >= s.Length) return list.ToArray();
                if (s[i] == ']') { i++; return list.ToArray(); }
                if (s[i] == ',') { i++; continue; }
                list.Add(Value(s, ref i));
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
            }
        }

        private static string Str(string s, ref int i)
        {
            char q = s[i++];
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == q) return sb.ToString();
                if (c != '\\' || i >= s.Length) { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        int code;
                        if (i + 4 <= s.Length && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) { sb.Append((char)code); i += 4; }
                        else sb.Append('u');
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}

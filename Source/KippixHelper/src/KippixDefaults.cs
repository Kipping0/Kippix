using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

namespace KippixHelper
{
    /// <summary>
    /// (0.99.62) How Kippix comes (KippixDefaultsData: Kipping0's home screen). Every new HelperSettings gets these over the
    /// field defaults in Models.cs: a fresh install, Reset (which copies from a new HelperSettings), a new profile. A settings
    /// file that's read still has the last word on everything it holds, so nobody's own settings change.
    /// </summary>
    internal static class KippixDefaults
    {
        private static List<KeyValuePair<FieldInfo, object>> plain;   // numbers, switches, text: the value itself
        private static List<KeyValuePair<FieldInfo, string>> deep;    // lists and the like: made new each time (never shared)
        private static readonly object gate = new object();

        internal static void Apply(HelperSettings s)
        {
            if (s == null) return;
            Ensure();
            foreach (var kv in plain) kv.Key.SetValue(s, kv.Value);
            if (deep.Count == 0) return;
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (var kv in deep)
            {
                try { kv.Key.SetValue(s, js.Deserialize(kv.Value, kv.Key.FieldType)); } catch (Exception) { }
            }
        }

        private static void Ensure()
        {
            if (plain != null) return;
            lock (gate)
            {
                if (plain != null) return;
                var p = new List<KeyValuePair<FieldInfo, object>>();
                var d = new List<KeyValuePair<FieldInfo, string>>();
                try
                {
                    var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var root = js.DeserializeObject(KippixDefaultsData.Json) as Dictionary<string, object>;
                    if (root != null)
                        foreach (var kv in root)
                        {
                            var f = typeof(HelperSettings).GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                            if (f == null || f.IsInitOnly) continue;
                            var t = f.FieldType;
                            try
                            {
                                if (t.IsValueType || t == typeof(string))
                                {
                                    if (kv.Value == null && t.IsValueType) continue;
                                    p.Add(new KeyValuePair<FieldInfo, object>(f, kv.Value == null ? null : js.ConvertToType(kv.Value, t)));
                                }
                                else
                                {
                                    string json = js.Serialize(kv.Value);
                                    js.Deserialize(json, t);   // (one that doesn't fit its field is left out)
                                    d.Add(new KeyValuePair<FieldInfo, string>(f, json));
                                }
                            }
                            catch (Exception) { }
                        }
                }
                catch (Exception) { }
                deep = d;
                plain = p;
            }
        }
    }
}

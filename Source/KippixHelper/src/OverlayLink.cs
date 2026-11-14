using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace KippixHelper
{
    /// <summary>
    /// Optional link to the KippixOverlay add-on through its public OverlayBridge class.
    /// Everything goes through reflection, so the helper builds and runs fine without the overlay.
    /// </summary>
    internal class OverlayLink
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static readonly Guid OverlayId = new Guid("b4a7d1c2-5e8f-4b9a-9c3d-2f6e1a8b7c55");

        private Type bridge;
        private bool searched;

        /// <summary>True when the overlay is installed and new enough to have the bridge.</summary>
        public bool Available { get { return bridge != null; } }

        public void Connect(IPlayniteAPI api, Action changed)
        {
            if (searched) return;
            searched = true;
            try
            {
                // the overlay built into this add-on (0.29+), when it's running
                if (KippixOverlay.OverlayBridge.IsRunning)
                {
                    bridge = typeof(KippixOverlay.OverlayBridge);
                    if (changed != null) KippixOverlay.OverlayBridge.Changed += changed;
                    logger.Info("Kippix: using the built-in overlay (bridge v" + KippixOverlay.OverlayBridge.Version + ").");
                    return;
                }
                foreach (Plugin p in api.Addons.Plugins)
                {
                    if (p.Id != OverlayId) continue;
                    bridge = p.GetType().Assembly.GetType("BananaBreadOverlay.OverlayBridge");
                    if (bridge == null)
                    {
                        logger.Info("Kippix: KippixOverlay is installed but too old for the bridge (needs 2.7.6+).");
                        return;
                    }
                    EventInfo ev = bridge.GetEvent("Changed");
                    if (ev != null && changed != null) ev.AddEventHandler(null, changed);
                    logger.Info("Kippix: linked to KippixOverlay (bridge v" + Get<int>("Version", 0) + ").");
                    return;
                }
            }
            catch (Exception ex)
            {
                bridge = null;
                logger.Error(ex, "Kippix: couldn't link to KippixOverlay.");
            }
        }

        public T Get<T>(string name, T fallback)
        {
            if (bridge == null) return fallback;
            try
            {
                PropertyInfo prop = bridge.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    object v = prop.GetValue(null, null);
                    return v is T ? (T)v : fallback;
                }
                FieldInfo field = bridge.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                {
                    object v = field.GetValue(null);
                    return v is T ? (T)v : fallback;
                }
            }
            catch (Exception) { }
            return fallback;
        }

        public object Call(string method, params object[] args)
        {
            if (bridge == null) return null;
            try
            {
                MethodInfo m = bridge.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                return m != null ? m.Invoke(null, args) : null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Kippix: overlay call failed: " + method);
                return null;
            }
        }

        public List<Dictionary<string, object>> VoiceMembers()
        {
            var list = Call("VoiceMembers") as IEnumerable;
            var result = new List<Dictionary<string, object>>();
            if (list == null) return result;
            foreach (object o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d != null) result.Add(d);
            }
            return result;
        }
    }
}

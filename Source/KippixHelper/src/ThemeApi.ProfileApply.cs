using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace KippixHelper
{
    /// <summary>
    /// (0.88) A one-off change to your own profile left in the add-on's data folder as apply-profile.json
    /// ({"Name": "…", "Picture": "C:\…png"}): read once at start-up, applied (your name and picture, and the main profile's
    /// with Profiles on) and then deleted. Nothing happens when there's no file. How a name and picture were set from outside
    /// Playnite while it may have been running (its own settings file is written over when it saves).
    /// </summary>
    public partial class ThemeApi
    {
        internal void ApplyPendingProfile()
        {
            string file = null;
            try
            {
                file = Path.Combine(plugin.GetPluginUserDataPath(), "apply-profile.json");
                if (!File.Exists(file)) return;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                string name = d != null && d.ContainsKey("Name") ? Convert.ToString(d["Name"]) : null;
                string pic = d != null && d.ContainsKey("Picture") ? Convert.ToString(d["Picture"]) : null;
                var owner = settings.Profiles != null && settings.Profiles.Count > 0 ? settings.Profiles[0] : null;
                bool ownerNow = owner == null || settings.CurrentProfile == owner.Id || string.IsNullOrEmpty(settings.CurrentProfile);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    name = name.Trim();
                    if (name.Length > 40) name = name.Substring(0, 40);
                    if (ownerNow) settings.ProfileNameOverride = name;
                    if (owner != null) owner.Name = name;
                }
                if (!string.IsNullOrWhiteSpace(pic) && File.Exists(pic))
                {
                    if (ownerNow)
                    {
                        SetProfilePicture(pic);   // (copies it into the profile folder)
                        if (owner != null) owner.PicturePath = settings.ProfilePicturePath;
                    }
                    else if (owner != null)
                    {
                        Directory.CreateDirectory(ProfilesDir);
                        string dest = Path.Combine(ProfilesDir, owner.Id + "-" + DateTime.Now.Ticks + Path.GetExtension(pic).ToLowerInvariant());
                        File.Copy(pic, dest, true);
                        owner.PicturePath = dest;
                    }
                }
                SaveSettings();
                // (0.89) your profile's own copy of its settings too, so switching to it later doesn't bring the old ones back
                try { if (ProfilesOn && ownerNow && owner != null) SaveProfileSnapshot(owner.Id); } catch (Exception) { }
                NotifyProfile();
                logger.Info("Kippix: your profile's name / picture set from apply-profile.json (" + (name ?? "") + ")");
            }
            catch (Exception ex) { logger.Warn("Kippix: apply-profile.json: " + ex.Message); }
            finally
            {
                try { if (file != null && File.Exists(file)) File.Delete(file); } catch (Exception) { }
            }
        }
    }
}

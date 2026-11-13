using System;
using System.IO;
using BepInEx;

namespace NomadModManager
{
    /// <summary>
    /// The two editions of the Mods menu (website and Nexus) share everything: the same plugin, config file and
    /// BepInEx\NomadModManager folder, so mods, profiles, settings and save backups carry over when a player switches.
    /// This remembers which edition ran last and, after a switch, says what carries over and what pauses.
    /// </summary>
    internal static class EditionSwitch
    {
        public static string Notice { get; private set; } = "";

        private static string FilePath => Path.Combine(Pending.Dir(Paths.GameRootPath), "edition.txt");

        public static void Check()
        {
            var now = Edition.Nexus ? "nexus" : "website";
            try
            {
                var before = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : "";
                if (before.Length > 0 && before != now)
                {
                    Notice = Edition.Nexus
                        ? "Switched to the Nexus edition of the Mods menu: your mods, profiles, settings and save backups are all here. Website mods now get their updates from Nexus when they're on it; your website account and subscriptions are kept for if you switch back."
                        : "Switched to the website edition of the Mods menu: your mods, profiles, settings and save backups are all here, and your website subscriptions and account work again.";
                    Activity.Add(Edition.Nexus ? "Switched to the Nexus edition of the Mods menu" : "Switched to the website edition of the Mods menu");
                    ModManagerPlugin.Log.LogMessage(Notice);
                }
                if (before != now) { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, now); }
            }
            catch (Exception e) { ModManagerPlugin.Log.LogDebug("Edition check: " + e.Message); }
        }

        /// <summary>The notice, once (the first time the menu opens after a switch).</summary>
        public static string Take() { var n = Notice; Notice = ""; return n; }
    }
}

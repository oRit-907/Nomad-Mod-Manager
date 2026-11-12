using System;
using System.IO;
using BepInEx;

namespace NomadModManager
{
    /// <summary>
    /// The preloader patchers are named for what they patch (1.7.2): NomadModManager.ModFiles (your mod files),
    /// NomadModFiles (the same, from the BepInEx package) and NomadUnityEngine (Unity's stripped modules). The copies
    /// under the old names (NomadModManager.Patcher, NomadModChanges, NomadUnityCompat) are removed once the new one is
    /// there. While the game runs they're usually loaded and can't be deleted; that's fine: the old mod-files patcher
    /// finds nothing left to do after the new one, and the new Unity one stands aside while the old one is loaded.
    /// Nomad Mod Downloader's Check BepInEx removes them with the game closed.
    /// </summary>
    internal static class PatcherNames
    {
        private static string P(string rel) => Path.Combine(Paths.PatcherPluginPath, rel);

        public static bool ModFilesInstalled => File.Exists(P("NomadModManager.ModFiles.dll")) || File.Exists(P("NomadModManager.Patcher.dll"));

        public static void Tidy()
        {
            void Old(string newRel, string oldRel)
            {
                try
                {
                    if (!File.Exists(P(newRel)) || !File.Exists(P(oldRel))) return;
                    File.Delete(P(oldRel));
                    ModManagerPlugin.Log?.LogInfo($"Removed the old patcher {oldRel} (now {newRel}).");
                }
                catch { }   // in use: Check BepInEx removes it with the game closed
            }
            Old("NomadModManager.ModFiles.dll", "NomadModManager.Patcher.dll");
            Old(Path.Combine("NomadModFiles", "NomadModFiles.dll"), Path.Combine("NomadModChanges", "NomadModChanges.Patcher.dll"));
            Old(Path.Combine("NomadUnityEngine", "NomadUnityEngine.dll"), Path.Combine("NomadUnityCompat", "NomadUnityCompat.dll"));
        }
    }
}

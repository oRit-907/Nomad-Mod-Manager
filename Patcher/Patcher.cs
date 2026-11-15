using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace NomadModManager
{
    /// <summary>
    /// Runs in BepInEx's preloader, before any plugin loads: applies mod updates, removals and
    /// enable/disable changes that were queued while the game was running.
    /// </summary>
    public static class NomadModManagerPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
#if MODCHANGES
            var log = Logger.CreateLogSource("Mod files");
#else
            var log = Logger.CreateLogSource("Mod files (Mods menu)");
#endif
            try
            {
                // 1.3.1: the Mods menu's data folder moves in with its plugin (BepInEx\plugins\NomadModManager\data). Done
                // here, before any plugin loads and while nothing in the folder is in use.
                try { var moved = Pending.Migrate(Paths.GameRootPath); if (moved != null) log.LogMessage(moved); }
                catch (Exception e) { log.LogWarning("The Mods menu's data folder stays where it was for now: " + e.Message); }
                if (!Pending.HasWork(Paths.GameRootPath)) return;
                var n = Pending.ApplyAll(Paths.GameRootPath, m => log.LogInfo(m));
                log.LogMessage($"Applied {n} queued mod change(s).");
            }
            catch (Exception e)
            {
                log.LogError("Couldn't apply queued mod changes: " + e);
            }
            finally
            {
                Logger.Sources.Remove(log);
            }
        }
    }
}

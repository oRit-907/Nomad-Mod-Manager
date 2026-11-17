using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;

namespace NomadModManager
{
    /// <summary>
    /// The mod loader the game was started with, and its folder (1.16.0). The same DLLs run on BepInEx 5 and on Ignite
    /// Loader (nomaddrivemods.online/mod/20), which keeps everything the BepInEx way one folder over: IgniteLoader\plugins,
    /// \patchers, \config, \LogOutput.log. Nothing in the Mods menu names the BepInEx folder itself any more: every path
    /// is built from the folder the loader says it runs in.
    ///
    /// That matters twice over on Ignite Loader, which by default sends file calls on "BepInEx\..." to its own folder
    /// (RedirectBepInExFolder). A mod manager that names BepInEx\plugins there and its own folder next to it is handed
    /// the same folder twice, and moving or deleting "the old copy" removes the live one. So: no path with BepInEx in it
    /// unless that is the running loader's folder, and nothing is ever moved or deleted between the two loaders' folders
    /// (see <see cref="Pending.Migrate"/>).
    ///
    /// Mods, the website and Nomad Mod Downloader describe files the BepInEx way ("BepInEx/plugins/Some Mod/...");
    /// <see cref="Map"/> turns that into the running loader's folder.
    /// </summary>
    internal static class LoaderPaths
    {
        public const string BepInExDir = "BepInEx";
        public const string IgniteDir = "IgniteLoader";

        private static string _dir;
        private static int _ignite = -1;

        /// <summary>The loader's folder in the game folder, by name: "BepInEx" or "IgniteLoader".</summary>
        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                string name = null;
                try
                {
                    var root = Paths.BepInExRootPath;
                    if (!string.IsNullOrEmpty(root)) name = Path.GetFileName(root.TrimEnd('\\', '/'));
                }
                catch { }
                return _dir = string.IsNullOrEmpty(name) ? BepInExDir : name;
            }
        }

        /// <summary>The game was started by Ignite Loader (its own BepInEx-compatible API is what this runs on).</summary>
        public static bool IsIgnite
        {
            get
            {
                if (_ignite >= 0) return _ignite == 1;
                bool found = false;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { if (a.GetName().Name == "IgniteLoader") { found = true; break; } }
                        catch { }
                    }
                }
                catch { }
                // (its folder says so too, should the loader's own library not be loaded under that name)
                if (!found && Dir.Equals(IgniteDir, StringComparison.OrdinalIgnoreCase)) found = true;
                _ignite = found ? 1 : 0;
                return found;
            }
        }

        public static string Name => IsIgnite ? "Ignite Loader" : "BepInEx";

        /// <summary>"v3.3.0" for Ignite Loader, "v5.4.23.2" for BepInEx.</summary>
        public static string Version
        {
            get
            {
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        AssemblyName n = a.GetName();
                        if (n.Name == "IgniteLoader") return "v" + n.Version.ToString(3);
                    }
                    return "v" + typeof(Paths).Assembly.GetName().Version;
                }
                catch { return "Installed"; }
            }
        }

        /// <summary>"BepInEx/plugins" or "IgniteLoader/plugins": the way a file's place in the game folder is written.</summary>
        public static string Plugins => Dir + "/plugins";
        /// <summary>The same for showing to the player.</summary>
        public static string PluginsShown => Dir + "\\plugins";
        public static string LogShown => Dir + "\\LogOutput.log";

        /// <summary>A loader's folder name: BepInEx, Ignite Loader's, or the one Ignite Loader had when it was Nomad Loader.</summary>
        public static bool IsLoaderDirName(string s) =>
            s.Equals(BepInExDir, StringComparison.OrdinalIgnoreCase) || s.Equals(IgniteDir, StringComparison.OrdinalIgnoreCase) || s.Equals("NomadLoader", StringComparison.OrdinalIgnoreCase);

        /// <summary>"IgniteLoader/plugins/X" (a zip laid out for Ignite Loader) -> "BepInEx/plugins/X", the way everything
        /// else here describes a file; <see cref="Map"/> then puts it in the running loader's folder.</summary>
        public static string ToBepInExLayout(string rel)
        {
            int slash = rel.IndexOf('/');
            if (!IsLoaderDirName(slash < 0 ? rel : rel.Substring(0, slash))) return rel;
            return BepInExDir + (slash < 0 ? "" : rel.Substring(slash));
        }

        /// <summary>"BepInEx/plugins/X" -> the running loader's folder ("IgniteLoader/plugins/X" on Ignite Loader; as it is on BepInEx).</summary>
        public static string Map(string rel)
        {
            rel = Pending.Norm(rel);
            if (Dir.Equals(BepInExDir, StringComparison.OrdinalIgnoreCase)) return rel;
            if (rel.Equals(BepInExDir, StringComparison.OrdinalIgnoreCase)) return Dir;
            if (rel.StartsWith(BepInExDir + "/", StringComparison.OrdinalIgnoreCase)) return Dir + rel.Substring(BepInExDir.Length);
            return rel;
        }

        /// <summary>
        /// A file's place as it was written down some time ago (a list of what a mod installed, a profile), on whichever
        /// loader the game ran then: where that is with the running one. "BepInEx/plugins/X" kept from a BepInEx install
        /// is "IgniteLoader/plugins/X" on Ignite Loader, and the other way round.
        /// </summary>
        public static string Here(string rel) => Map(ToBepInExLayout(Pending.Norm(rel ?? "")));

        /// <summary>Inside the running loader's folder.</summary>
        public static bool InLoader(string rel) => Pending.Norm(rel).StartsWith(Dir + "/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The other loader's folder for the Mods menu's data, when this is Ignite Loader: where the menu kept it while
        /// the game ran on BepInEx. Null on BepInEx (there is nothing to bring over: BepInEx came first).
        /// </summary>
        public static string[] OtherLoaderData(string gameRoot)
        {
            if (Dir.Equals(BepInExDir, StringComparison.OrdinalIgnoreCase)) return null;
            return new[] { Path.Combine(Path.Combine(Path.Combine(Path.Combine(gameRoot, BepInExDir), "plugins"), "NomadModManager"), "data") };
        }
    }
}

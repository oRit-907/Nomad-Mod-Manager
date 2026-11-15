using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NomadModManager
{
    /// <summary>
    /// Changes that can't happen while Nomad Drive is running (the DLLs are in use), applied by the
    /// preloader patcher the next time the game starts, before any plugin loads.
    ///
    ///   BepInEx/plugins/NomadModManager/data/pending.txt   one "op|relative/path" per line:
    ///       remove|BepInEx/plugins/Some Mod      delete a mod's folder or DLL
    ///       disable|BepInEx/plugins/Some Mod     rename its *.dll to *.dll.disabled
    ///       enable|BepInEx/plugins/Some Mod      the reverse
    ///   *.nmm-new files under BepInEx/           a newer copy of an in-use file, swapped in at launch
    ///
    /// Shared with Nomad Mod Downloader, which writes the same format when it updates a mod while the game runs.
    /// </summary>
    internal static class Pending
    {
        public const string NewSuffix = ".nmm-new";
        public const string OffSuffix = ".disabled";

        /// <summary>The Mods menu's data folder: BepInEx\plugins\NomadModManager\data, with the plugin (1.13.7). Until then
        /// it was BepInEx\NomadModManager (<see cref="LegacyDir"/>), which Nomad Mod Downloader may still write to.</summary>
        public static string Dir(string gameRoot) =>
            Path.Combine(Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "plugins"), "NomadModManager"), "data");
        public static string LegacyDir(string gameRoot) => Path.Combine(Path.Combine(gameRoot, "BepInEx"), "NomadModManager");
        public static string ListFile(string gameRoot) => Path.Combine(Dir(gameRoot), "pending.txt");
        private static string LegacyListFile(string gameRoot) => Path.Combine(LegacyDir(gameRoot), "pending.txt");

        /// <summary>Inside the data folder. It sits under plugins now, and nothing in it is a mod: not the in-menu
        /// browser's DLLs, not the save backups.</summary>
        public static bool InData(string gameRoot, string path)
        {
            try
            {
                var dir = Path.GetFullPath(Dir(gameRoot)).TrimEnd(Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(path);
                return full.Equals(dir, StringComparison.OrdinalIgnoreCase) || full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Every file under a folder, leaving the data folder out (it is big, and none of it is a mod's).</summary>
        public static List<string> FilesOutsideData(string gameRoot, string dir, string pattern)
        {
            var result = new List<string>();
            var todo = new List<string> { dir };   // (a plain list: the game's trimmed libraries have no Stack)
            while (todo.Count > 0)
            {
                var d = todo[todo.Count - 1];
                todo.RemoveAt(todo.Count - 1);
                if (InData(gameRoot, d)) continue;
                try { result.AddRange(Directory.GetFiles(d, pattern)); } catch { }
                try { todo.AddRange(Directory.GetDirectories(d)); } catch { }
            }
            return result;
        }

        /// <summary>
        /// Brings the old data folder (BepInEx\NomadModManager) to the new place. The first time, the whole folder is
        /// renamed in one go. After that, whatever turns up in the old place again (Nomad Mod Downloader still writes its
        /// queued changes and its own address there) is taken over: queued changes are added to the list, the Downloader's
        /// two files replace the ones here, anything else is moved if it isn't here yet. Returns what was done, or null.
        /// </summary>
        public static string Migrate(string gameRoot)
        {
            var old = LegacyDir(gameRoot);
            if (!Directory.Exists(old)) return null;
            var dir = Dir(gameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(dir));
            if (!Directory.Exists(dir))
            {
                try { Directory.Move(old, dir); return "The Mods menu's data folder is now BepInEx\\plugins\\NomadModManager\\data (moved from BepInEx\\NomadModManager)."; }
                catch { }   // (something in it is in use: taken over piece by piece below, the rest next time)
            }
            Directory.CreateDirectory(dir);
            int n = MoveInto(old, dir, true);
            try { if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old); } catch { }
            return n > 0 ? $"Took {n} file(s) over from BepInEx\\NomadModManager into the Mods menu's data folder." : null;
        }

        private static int MoveInto(string from, string to, bool top)
        {
            int n = 0;
            foreach (var f in Directory.GetFiles(from))
            {
                try
                {
                    var name = Path.GetFileName(f);
                    var target = Path.Combine(to, name);
                    if (top && name.Equals("pending.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        File.AppendAllLines(target, File.ReadAllLines(f));
                        File.Delete(f);
                        n++;
                    }
                    else if (top && (name.Equals("downloader.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("downloader-version.txt", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(f, target);
                        n++;
                    }
                    else if (!File.Exists(target)) { File.Move(f, target); n++; }
                    else if (new FileInfo(target).Length == new FileInfo(f).Length) File.Delete(f);   // (already here: the old copy goes)
                }
                catch { }
            }
            foreach (var d in Directory.GetDirectories(from))
            {
                try
                {
                    var target = Path.Combine(to, Path.GetFileName(d));
                    if (!Directory.Exists(target)) { Directory.Move(d, target); n++; continue; }
                    n += MoveInto(d, target, false);
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { }
            }
            return n;
        }

        public static string Norm(string rel) => rel.Replace('\\', '/').Trim('/');

        /// <summary>Game root + relative path, refusing anything outside BepInEx.</summary>
        public static string Full(string gameRoot, string rel)
        {
            rel = Norm(rel);
            if (rel.Length == 0 || rel.Split('/').Any(s => s == ".." || s.Contains(":")))
                throw new InvalidOperationException("Unsafe path: " + rel);
            var full = Path.GetFullPath(Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            var bep = Path.GetFullPath(Path.Combine(gameRoot, "BepInEx")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(bep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Path is outside BepInEx: " + rel);
            return full;
        }

        public static List<KeyValuePair<string, string>> Read(string gameRoot)
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                // (the list here, and whatever Nomad Mod Downloader has queued in the old place since the game started)
                var lines = new List<string>();
                foreach (var f in new[] { ListFile(gameRoot), LegacyListFile(gameRoot) })
                    if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f));
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    var bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    var op = line.Substring(0, bar).Trim().ToLowerInvariant();
                    var rel = Norm(line.Substring(bar + 1));
                    if (rel.Length == 0 || (op != "remove" && op != "disable" && op != "enable")) continue;
                    list.Add(new KeyValuePair<string, string>(op, rel));
                }
            }
            catch { }
            return list;
        }

        public static void Write(string gameRoot, List<KeyValuePair<string, string>> ops)
        {
            Directory.CreateDirectory(Dir(gameRoot));
            var f = ListFile(gameRoot);
            // (what was read from the old place is in this list now)
            try { var was = LegacyListFile(gameRoot); if (File.Exists(was)) File.Delete(was); } catch { }
            if (ops.Count == 0) { if (File.Exists(f)) File.Delete(f); return; }
            File.WriteAllLines(f, ops.Select(o => o.Key + "|" + o.Value).ToArray());
        }

        /// <summary>Queues an op. disable/enable cancel each other; remove replaces both.</summary>
        public static void Add(string gameRoot, string op, string rel)
        {
            rel = Norm(rel);
            var ops = Read(gameRoot);
            bool Same(KeyValuePair<string, string> o) => o.Value.Equals(rel, StringComparison.OrdinalIgnoreCase);
            if (op == "disable" && ops.RemoveAll(o => Same(o) && o.Key == "enable") > 0) { Write(gameRoot, ops); return; }
            if (op == "enable" && ops.RemoveAll(o => Same(o) && o.Key == "disable") > 0) { Write(gameRoot, ops); return; }
            if (op == "remove") ops.RemoveAll(Same);
            if (!ops.Any(o => Same(o) && o.Key == op)) ops.Add(new KeyValuePair<string, string>(op, rel));
            Write(gameRoot, ops);
        }

        public static void Cancel(string gameRoot, string op, string rel)
        {
            rel = Norm(rel);
            var ops = Read(gameRoot);
            ops.RemoveAll(o => o.Key == op && o.Value.Equals(rel, StringComparison.OrdinalIgnoreCase));
            Write(gameRoot, ops);
        }

        /// <summary>All *.nmm-new files waiting under BepInEx (plugins, patchers, config).</summary>
        public static List<string> StagedFiles(string gameRoot)
        {
            var result = new List<string>();
            foreach (var sub in new[] { "plugins", "patchers", "config" })
            {
                var d = Path.Combine(Path.Combine(gameRoot, "BepInEx"), sub);
                if (!Directory.Exists(d)) continue;
                result.AddRange(FilesOutsideData(gameRoot, d, "*" + NewSuffix));
            }
            // (the two picture-decoding libraries the Mods menu ships are in its data folder: the one part of it an
            // update writes to)
            try
            {
                var native = Path.Combine(Dir(gameRoot), "native");
                if (Directory.Exists(native)) result.AddRange(Directory.GetFiles(native, "*" + NewSuffix));
            }
            catch { }
            return result;
        }

        public static bool HasWork(string gameRoot) => Read(gameRoot).Count > 0 || StagedFiles(gameRoot).Count > 0;

        /// <summary>Applies everything. Ops that fail stay queued for next time. Returns how many changes were made.</summary>
        public static int ApplyAll(string gameRoot, Action<string> log)
        {
            var done = 0;
            var ops = Read(gameRoot);
            var failed = new List<KeyValuePair<string, string>>();

            // 1. removals
            foreach (var o in ops.Where(o => o.Key == "remove"))
            {
                try
                {
                    var full = Full(gameRoot, o.Value);
                    if (Directory.Exists(full))
                    {
                        // (the Mods menu's own folder holds its data folder: the saves' backups and sign-ins in it stay)
                        bool holdsData = Directory.Exists(Dir(gameRoot)) && InData(gameRoot, Path.Combine(full, "data")) && !InData(gameRoot, full);
                        if (!holdsData) Directory.Delete(full, true);
                        else
                        {
                            foreach (var f in Directory.GetFiles(full)) File.Delete(f);
                            foreach (var d in Directory.GetDirectories(full)) if (!InData(gameRoot, d)) Directory.Delete(d, true);
                            log("Kept the Mods menu's data folder (save backups and sign-ins).");
                        }
                    }
                    foreach (var f in new[] { full, full + OffSuffix })
                        if (File.Exists(f)) File.Delete(f);
                    log("Removed " + o.Value);
                    done++;
                }
                catch (InvalidOperationException e) { log("Dropped unsafe entry: " + e.Message); }
                catch (Exception e) { log("Couldn't remove " + o.Value + ": " + e.Message); failed.Add(o); }
            }

            // 2. newer copies of files that were in use
            foreach (var staged in StagedFiles(gameRoot))
            {
                try
                {
                    var target = staged.Substring(0, staged.Length - NewSuffix.Length);
                    // A disabled mod stays disabled after its update.
                    if (!File.Exists(target) && File.Exists(target + OffSuffix)) target += OffSuffix;
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(staged, target);
                    log("Updated " + Path.GetFileName(target));
                    done++;
                }
                catch (Exception e) { log("Couldn't update " + Path.GetFileName(staged) + ": " + e.Message); }
            }

            // 3. enable / disable
            foreach (var o in ops.Where(o => o.Key == "disable" || o.Key == "enable"))
            {
                try
                {
                    SetEnabled(Full(gameRoot, o.Value), o.Key == "enable");
                    log((o.Key == "enable" ? "Enabled " : "Disabled ") + o.Value);
                    done++;
                }
                catch (InvalidOperationException e) { log("Dropped unsafe entry: " + e.Message); }
                catch (Exception e) { log("Couldn't " + o.Key + " " + o.Value + ": " + e.Message); failed.Add(o); }
            }

            try { Write(gameRoot, failed); } catch { }
            return done;
        }

        /// <summary>In the "data" folder of the Mods menu's own plugin folder (worked out from the mod folder itself: this
        /// method isn't told where the game is).</summary>
        private static bool InDataOf(string modFolder, string file)
        {
            if (!Path.GetFileName(modFolder.TrimEnd(Path.DirectorySeparatorChar, '/')).Equals("NomadModManager", StringComparison.OrdinalIgnoreCase)) return false;
            var data = Path.Combine(Path.GetFullPath(modFolder), "data") + Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).StartsWith(data, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Renames the DLLs of a mod folder (or a single DLL) between *.dll and *.dll.disabled.</summary>
        public static void SetEnabled(string full, bool enabled)
        {
            IEnumerable<string> files;
            if (Directory.Exists(full)) files = Directory.GetFiles(full, "*", SearchOption.AllDirectories).Where(f => !InDataOf(full, f));
            else files = new[] { full, full + OffSuffix };
            foreach (var f in files.ToList())
            {
                if (!enabled && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                {
                    if (File.Exists(f + OffSuffix)) File.Delete(f + OffSuffix);
                    File.Move(f, f + OffSuffix);
                }
                else if (enabled && f.EndsWith(".dll" + OffSuffix, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                {
                    var on = f.Substring(0, f.Length - OffSuffix.Length);
                    if (File.Exists(on)) File.Delete(on);
                    File.Move(f, on);
                }
            }
        }
    }
}

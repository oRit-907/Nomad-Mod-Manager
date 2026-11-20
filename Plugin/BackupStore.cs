using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace NomadModManager
{
    /// <summary>
    /// 1.16.1: where the save backups are kept, and how little room they take.
    ///
    /// Until now each backup was a whole copy of the saves folder, and the last ten automatic ones were kept: with
    /// saves of a few hundred MB that is several GB (a player's had grown to 4.7 GB in three days). Now:
    ///   - a backup holds only the files that changed since the backup before it. Each backup folder has a list
    ///     (.backup-files.txt) of every save file as it was, and for each one the backup folder its copy is in: this
    ///     one, or an older one when the file hadn't changed.
    ///   - when the saves are the same as in the newest backup, an automatic backup isn't made at all.
    ///   - fewer automatic backups are kept (a setting), and together they stay under a size (a setting): the oldest
    ///     go first. Backups made by hand are never removed by this.
    ///   - when a backup is removed, the files newer backups still point at are moved on to the next one first.
    /// Backups from before 1.16.1 (whole copies, no list) stay readable and are removed by the same limits.
    ///
    /// File work only (no Unity, no BepInEx), so it runs off the main thread and can be tested outside the game.
    /// </summary>
    internal static class BackupStore
    {
        public const string ListName = ".backup-files.txt", ReasonName = ".backup-reason.txt";
        private static readonly object Gate = new object();

        internal sealed class Entry
        {
            public string Rel, Hash, Holder;     // the file, what is in it, the backup folder its copy is in
            public long Size, Time;              // (Time: last write, UTC ticks)
        }

        internal sealed class Info
        {
            public string Dir = "", Reason = "";
            public DateTime Time;
            public long Size;        // what this backup's own folder holds
            public int Files;        // how many save files it covers
            public bool Auto, Whole; // Whole: made before 1.16.1 (a whole copy, no list)
        }

        private static bool OurFile(string name) => name.Equals(ListName, StringComparison.OrdinalIgnoreCase) || name.Equals(ReasonName, StringComparison.OrdinalIgnoreCase);

        private static string Name(string dir) => Path.GetFileName(dir.TrimEnd('\\', '/'));

        private static DateTime TimeOf(string dir)
        {
            var reason = Path.Combine(dir, ReasonName);
            try { return File.Exists(reason) ? File.GetLastWriteTime(reason) : Directory.GetCreationTime(dir); } catch { return DateTime.MinValue; }
        }

        /// <summary>The backup folders, newest first.</summary>
        private static List<string> Dirs(string backups)
        {
            if (!Directory.Exists(backups)) return new List<string>();
            return Directory.GetDirectories(backups).OrderByDescending(TimeOf).ThenByDescending(d => Name(d), StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A path that stays inside the folder it is joined to.</summary>
        private static bool Inside(string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.IndexOf(':') >= 0 || rel[0] == '/' || rel[0] == '\\') return false;
            foreach (var part in rel.Split('/', '\\'))
                if (part == "..") return false;
            return true;
        }

        private static List<Entry> ReadList(string dir)
        {
            var file = Path.Combine(dir, ListName);
            if (!File.Exists(file)) return null;
            var list = new List<Entry>();
            foreach (var line in File.ReadAllLines(file))
            {
                var a = line.Split('\t');
                if (a.Length < 5 || !long.TryParse(a[1], out var size) || !long.TryParse(a[2], out var time)) continue;
                if (!Inside(a[0]) || !Inside(a[4]) || a[4].IndexOfAny(new[] { '/', '\\' }) >= 0) continue;   // (never a path out of the folder)
                list.Add(new Entry { Rel = a[0], Size = size, Time = time, Hash = a[3], Holder = a[4] });
            }
            return list;
        }

        private static void WriteList(string dir, List<Entry> list)
        {
            var file = Path.Combine(dir, ListName);
            var tmp = file + ".tmp";
            File.WriteAllLines(tmp, list.Select(e => e.Rel + "\t" + e.Size + "\t" + e.Time + "\t" + e.Hash + "\t" + e.Holder).ToArray());
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        private static string HashOf(string file)
        {
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        /// <summary>Copies a file (File.Copy: the copy keeps the time the save was written) and says what is in the copy.</summary>
        private static string CopyHashed(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
            return HashOf(to);
        }

        /// <summary>
        /// Gives a backup from before 1.16.1 (a whole copy) its list, so the next backup only has to hold what changed
        /// since it. (A copy keeps the time its file was written, so an unchanged save is known by size and time.)
        /// </summary>
        private static List<Entry> Adopt(string dir)
        {
            var name = Name(dir);
            var root = dir.TrimEnd('\\', '/');
            var list = new List<Entry>();
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (OurFile(Path.GetFileName(f))) continue;
                try
                {
                    var fi = new FileInfo(f);
                    list.Add(new Entry { Rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'), Size = fi.Length, Time = fi.LastWriteTimeUtc.Ticks, Hash = HashOf(f), Holder = name });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            WriteList(dir, list);
            return list;
        }

        private static long OwnSize(string dir, out int files)
        {
            long size = 0;
            files = 0;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (OurFile(Path.GetFileName(f))) continue;
                    files++;
                    try { size += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return size;
        }

        /// <summary>A note in the backups folder for whoever opens it in Explorer.</summary>
        private static void Explain(string backups)
        {
            try
            {
                var file = Path.Combine(backups, "READ ME.txt");
                if (File.Exists(file)) return;
                File.WriteAllText(file,
                    "Save backups made by the Mods menu (Nomad Mod Manager).\r\n\r\n" +
                    "Each folder is one backup. To save room, a backup holds only the save files that had changed since the backup\r\n" +
                    "before it. The file .backup-files.txt in it lists every save file and the folder its copy is in.\r\n\r\n" +
                    "To put a backup back: Mods menu > Activity > Save backups > Restore (at the main menu).\r\n" +
                    "To remove backups, use the Delete buttons there. Deleting a folder here by hand can take files away from\r\n" +
                    "the newer backups that share them.\r\n");
            }
            catch { }
        }

        /// <summary>What a new backup did.</summary>
        internal sealed class Made
        {
            public string Dir;
            public int Files, Copied;   // save files covered; of those, the ones that had changed
            public long Size;           // what was copied
            public bool Same;           // nothing had changed since the newest backup (and so none was made, when automatic)
        }

        /// <summary>
        /// Backs up <paramref name="files"/> (full path and path inside the saves folder). Only what changed since the
        /// newest backup is copied. An automatic backup of saves that haven't changed isn't made (Same, Dir null).
        /// </summary>
        public static Made Create(string backups, List<(string Full, string Rel)> files, string reason, bool automatic, DateTime now)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(backups);
                // what the newest backup with a list holds
                Dictionary<string, Entry> before = null;
                var dirs = Dirs(backups);
                foreach (var d in dirs)
                {
                    List<Entry> l = null;
                    try { l = ReadList(d); } catch { }
                    // (the newest one is from before 1.16.1: it gets its list now)
                    if (l == null && d == dirs[0]) { try { l = Adopt(d); } catch { } }
                    if (l == null) continue;
                    before = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in l) before[e.Rel] = e;
                    break;
                }
                Explain(backups);
                var stamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
                var dir = Path.Combine(backups, stamp + (automatic ? "_auto" : ""));
                for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(backups, stamp + "_" + n + (automatic ? "_auto" : ""));
                var name = Name(dir);
                var list = new List<Entry>();
                var copy = new List<(string Full, Entry E)>();
                foreach (var (full, rel) in files)
                {
                    FileInfo fi;
                    try { fi = new FileInfo(full); if (!fi.Exists) continue; } catch { continue; }
                    var e = new Entry { Rel = rel.Replace('\\', '/'), Size = fi.Length, Time = fi.LastWriteTimeUtc.Ticks, Holder = name };
                    Entry was = null;
                    if (before != null && before.TryGetValue(e.Rel, out was) && was.Size == e.Size && File.Exists(Path.Combine(backups, was.Holder, was.Rel)))
                    {
                        if (was.Time == e.Time) { e.Hash = was.Hash; e.Holder = was.Holder; list.Add(e); continue; }
                        // (written again: the same if what is in it is the same)
                        try { e.Hash = HashOf(full); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                        if (e.Hash == was.Hash) { e.Holder = was.Holder; list.Add(e); continue; }
                    }
                    copy.Add((full, e));
                }
                var made = new Made { Files = list.Count + copy.Count, Copied = copy.Count };
                if (copy.Count == 0 && before != null && list.Count == before.Count)
                {
                    made.Same = true;
                    if (automatic) return made;
                }
                if (made.Files == 0) return made;
                Directory.CreateDirectory(dir);
                foreach (var (full, e) in copy)
                {
                    try
                    {
                        e.Hash = CopyHashed(full, Path.Combine(dir, e.Rel));
                        made.Size += e.Size;
                        list.Add(e);
                    }
                    catch (IOException) { made.Copied--; made.Files--; }                    // in use by the game: skipped
                    catch (UnauthorizedAccessException) { made.Copied--; made.Files--; }
                }
                WriteList(dir, list);
                File.WriteAllText(Path.Combine(dir, ReasonName), reason ?? "");
                made.Dir = dir;
                return made;
            }
        }

        /// <summary>The backups, newest first.</summary>
        public static List<Info> List(string backups)
        {
            var result = new List<Info>();
            try
            {
                foreach (var d in Dirs(backups))
                {
                    var b = new Info { Dir = d, Time = TimeOf(d), Auto = Name(d).EndsWith("_auto", StringComparison.OrdinalIgnoreCase) };
                    try { var r = Path.Combine(d, ReasonName); if (File.Exists(r)) b.Reason = File.ReadAllText(r).Trim(); } catch { }
                    b.Size = OwnSize(d, out var own);
                    List<Entry> l = null;
                    try { l = ReadList(d); } catch { }
                    b.Whole = l == null;
                    b.Files = l?.Count ?? own;
                    result.Add(b);
                }
            }
            catch { }
            return result;
        }

        /// <summary>How much room all the backups take.</summary>
        public static long TotalSize(string backups)
        {
            long size = 0;
            try
            {
                if (Directory.Exists(backups))
                    foreach (var f in Directory.GetFiles(backups, "*", SearchOption.AllDirectories))
                        try { size += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return size;
        }

        /// <summary>
        /// Removes a backup. Files of its own that newer backups point at are moved to the oldest of those first, and
        /// their lists changed to say so.
        /// </summary>
        public static void Remove(string backups, string dir)
        {
            lock (Gate)
            {
                if (!Directory.Exists(dir)) return;
                var name = Name(dir);
                // the other backups that have a list, oldest first
                var others = Dirs(backups).Where(d => !Name(d).Equals(name, StringComparison.OrdinalIgnoreCase)).Reverse().ToList();
                var lists = new List<(string Dir, List<Entry> List, bool Changed)>();
                foreach (var d in others)
                {
                    List<Entry> l = null;
                    try { l = ReadList(d); } catch { }
                    if (l != null) lists.Add((d, l, false));
                }
                var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // file -> the backup it went to
                var stuck = false;
                for (var i = 0; i < lists.Count; i++)
                {
                    var (d, l, changed) = lists[i];
                    foreach (var e in l)
                    {
                        if (!e.Holder.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!moved.TryGetValue(e.Rel, out var to))
                        {
                            var from = Path.Combine(dir, e.Rel);
                            var dest = Path.Combine(d, e.Rel);
                            try
                            {
                                if (File.Exists(from))
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                    if (File.Exists(dest)) File.Delete(dest);
                                    File.Move(from, dest);
                                }
                                else if (!File.Exists(dest)) continue;      // (its copy is gone already: nothing to keep)
                            }
                            catch { stuck = true; continue; }               // (in use: this backup stays until it can be moved)
                            to = Name(d);
                            moved[e.Rel] = to;
                        }
                        e.Holder = to;
                        changed = true;
                    }
                    if (changed) { try { WriteList(d, l); } catch { stuck = true; } }
                }
                if (stuck) return;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Keeps the automatic backups within the limits: at most <paramref name="keep"/> of them, taking at most
        /// <paramref name="maxBytes"/> together (0: no limit). The oldest go first; the newest one always stays. The ones
        /// made by hand don't count and are never removed (or a few of those would leave no room for automatic ones).
        /// Returns how many were removed.
        /// </summary>
        public static int Prune(string backups, int keep, long maxBytes)
        {
            lock (Gate)
            {
                var removed = 0;
                try
                {
                    keep = Math.Max(1, keep);
                    var autos = Dirs(backups).Where(d => Name(d).EndsWith("_auto", StringComparison.OrdinalIgnoreCase)).ToList();   // newest first
                    while (autos.Count > keep)
                    {
                        Remove(backups, autos[autos.Count - 1]);
                        if (Directory.Exists(autos[autos.Count - 1])) break;     // (couldn't: try again another time)
                        autos.RemoveAt(autos.Count - 1);
                        removed++;
                    }
                    while (maxBytes > 0 && autos.Count > 1 && autos.Sum(d => OwnSize(d, out _)) > maxBytes)
                    {
                        Remove(backups, autos[autos.Count - 1]);
                        if (Directory.Exists(autos[autos.Count - 1])) break;
                        autos.RemoveAt(autos.Count - 1);
                        removed++;
                    }
                }
                catch { }
                return removed;
            }
        }

        /// <summary>Removes every automatic backup. Returns how many.</summary>
        public static int RemoveAutomatic(string backups)
        {
            lock (Gate)
            {
                var n = 0;
                // oldest first, so a file is moved on at most once per backup removed
                foreach (var d in Dirs(backups).Where(d => Name(d).EndsWith("_auto", StringComparison.OrdinalIgnoreCase)).Reverse().ToList())
                {
                    Remove(backups, d);
                    if (!Directory.Exists(d)) n++;
                }
                return n;
            }
        }

        /// <summary>The files of a backup: the path inside the saves folder, and where its copy is.</summary>
        public static List<(string Rel, string Copy)> Files(string backups, string dir)
        {
            var result = new List<(string, string)>();
            var l = ReadList(dir);
            if (l != null)
            {
                foreach (var e in l) result.Add((e.Rel, Path.Combine(backups, e.Holder, e.Rel)));
                return result;
            }
            // (from before 1.16.1: a whole copy)
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (OurFile(Path.GetFileName(f))) continue;
                result.Add((f.Substring(dir.Length).TrimStart('\\', '/'), f));
            }
            return result;
        }

        /// <summary>Puts a backup's files back into the saves folder. Returns how many.</summary>
        public static int Restore(string backups, string dir, string saves)
        {
            lock (Gate)
            {
                var n = 0;
                foreach (var (rel, copy) in Files(backups, dir))
                {
                    if (!File.Exists(copy)) continue;
                    var dest = Path.Combine(saves, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(copy, dest, true);
                    n++;
                }
                return n;
            }
        }
    }
}

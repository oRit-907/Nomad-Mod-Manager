using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace NomadModManager
{
    // ======================================================================
    // Conflict finder

    /// <summary>
    /// Mods that change the same game code. Harmony knows every patch and who made it; two mods that both replace
    /// or rewrite the same method (prefixes that can skip it, transpilers that rewrite it) can step on each other.
    /// Two postfixes on one method are normal and left out.
    /// </summary>
    internal static class Conflicts
    {
        internal sealed class Clash
        {
            public string Method = "";
            public List<string> Owners = new List<string>();
        }

        private static List<Clash> _cache;
        private static float _at = -100f;

        public static List<Clash> All()
        {
            if (_cache != null && Time.realtimeSinceStartup - _at < 15f) return _cache;
            _at = Time.realtimeSinceStartup;
            var list = new List<Clash>();
            try
            {
                foreach (var method in Harmony.GetAllPatchedMethods().ToList())
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    // Owners that can change what the method does (not just add to it afterwards).
                    var strong = info.Prefixes.Select(p => p.owner).Concat(info.Transpilers.Select(p => p.owner))
                        .Where(IsModOwner).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var all = strong.Concat(info.Postfixes.Select(p => p.owner).Where(IsModOwner)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    // A clash: two mods rewrite it, or one rewrites it and another also patches it.
                    var transpilers = info.Transpilers.Select(p => p.owner).Where(IsModOwner).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    if (strong.Count >= 2 || (transpilers >= 1 && all.Count >= 2))
                        list.Add(new Clash { Method = (method.DeclaringType?.Name ?? "?") + "." + method.Name, Owners = all });
                }
            }
            catch (Exception e) { ModManagerPlugin.Log?.LogDebug("Conflict scan: " + e.Message); }
            return _cache = list;
        }

        /// <summary>Harmony ids that are BepInEx itself, its compatibility layers, or this menu aren't mods.</summary>
        private static bool IsModOwner(string owner) =>
            !string.IsNullOrEmpty(owner)
            && !owner.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase)
            && !owner.StartsWith("IgniteLoader", StringComparison.OrdinalIgnoreCase)
            && !owner.StartsWith("HarmonyX", StringComparison.OrdinalIgnoreCase)
            && owner.IndexOf("nomadmodmanager", StringComparison.OrdinalIgnoreCase) < 0
            && owner.IndexOf("unitycompat", StringComparison.OrdinalIgnoreCase) < 0
            && owner.IndexOf("bepinex5compat", StringComparison.OrdinalIgnoreCase) < 0
            && owner.IndexOf("bepinex6compat", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>Does this Harmony id belong to the mod? (Usually it's the plugin's GUID, sometimes with a suffix.)</summary>
        private static bool Owns(ModEntry m, string owner) =>
            m.Guids.Any(g => owner.Equals(g, StringComparison.OrdinalIgnoreCase)
                             || owner.StartsWith(g + ".", StringComparison.OrdinalIgnoreCase)
                             || owner.StartsWith(g, StringComparison.OrdinalIgnoreCase) && g.Length >= 8);

        /// <summary>The other mods this one clashes with, and on which methods.</summary>
        public static List<(string Other, List<string> Methods)> For(ModEntry m, IEnumerable<ModEntry> mods)
        {
            if (m.Guids.Count == 0) return new List<(string, List<string>)>();
            var others = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var list = mods.ToList();
            foreach (var c in All())
            {
                if (!c.Owners.Any(o => Owns(m, o))) continue;
                foreach (var o in c.Owners.Where(o => !Owns(m, o)))
                {
                    var name = list.FirstOrDefault(x => x != m && Owns(x, o))?.Name ?? NameOfOwner(o);
                    if (!others.TryGetValue(name, out var ms)) others[name] = ms = new List<string>();
                    if (!ms.Contains(c.Method)) ms.Add(c.Method);
                }
            }
            return others.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        private static string NameOfOwner(string owner)
        {
            foreach (var kv in Bep.Plugins)
                if (owner.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value.Metadata.Name;
            foreach (var o in Bep.OtherPlugins)
                if (owner.StartsWith(o.Guid, StringComparison.OrdinalIgnoreCase)) return o.Name;
            return owner;
        }
    }

    // ======================================================================
    // Save backups

    /// <summary>
    /// Backs up Nomad Drive's saves (Unity's persistent data folder) to the data folder's save-backups before the menu
    /// changes mods, and puts a backup back on request. 1.16.1: a backup holds only what changed, unchanged saves
    /// aren't backed up again, and the backups stay under a number and a size (see <see cref="BackupStore"/>).
    /// </summary>
    internal static class SaveBackups
    {
        private static DateTime _last = DateTime.MinValue;

        public static string SaveDir => Application.persistentDataPath;
        public static string BackupDir => Path.Combine(Pending.Dir(Paths.GameRootPath), "save-backups");

        // The settings (file ModManager, section Backups).
        public static bool Automatic => ModManagerPlugin.I?.BackupsOn?.Value ?? true;
        public static int Keep => Mathf.Clamp(ModManagerPlugin.I?.BackupsKept?.Value ?? 5, 1, 50);
        public static int MaxMb => Mathf.Clamp(ModManagerPlugin.I?.BackupsMaxMb?.Value ?? 500, 0, 100000);
        private static long MaxBytes => MaxMb * 1024L * 1024L;

        internal sealed class Backup
        {
            public string Dir = "", Reason = "";
            public DateTime Time;
            public long Size;       // what this backup's own folder holds (the saves that had changed)
            public int Files;       // the save files it covers
            public bool Auto;
        }

        private static bool Skip(string rel)
        {
            var name = Path.GetFileName(rel);
            var top = rel.Split('/', '\\')[0];
            return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                   || top.Equals("Unity", StringComparison.OrdinalIgnoreCase) || top.Equals("Crashes", StringComparison.OrdinalIgnoreCase)
                   || top.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase) || top.Equals("il2cpp", StringComparison.OrdinalIgnoreCase);
        }

        private static List<(string Full, string Rel)> SaveFiles(string root)
        {
            var list = new List<(string, string)>();
            if (!Directory.Exists(root)) return list;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(root.Length).TrimStart('\\', '/');
                if (!Skip(rel)) list.Add((f, rel));
            }
            return list;
        }

        public static string SizeText(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : bytes >= 1024 * 1024 ? (bytes / (1024 * 1024)) + " MB"
            : Math.Max(bytes > 0 ? 1 : 0, bytes / 1024) + " KB";

        /// <summary>
        /// Backs up the saves. An automatic one: at most once every few minutes (several changes in a row = one backup),
        /// only when a save changed since the last backup, and not at all when automatic backups are switched off.
        /// </summary>
        public static Backup Create(string reason, bool automatic)
        {
            try
            {
                if (automatic && (!Automatic || (DateTime.Now - _last).TotalMinutes < 5)) return null;
                string saves = SaveDir, backups = BackupDir;      // (read here: Unity's and the settings' values are the main thread's)
                int keep = Keep;
                long max = MaxBytes;
                if (automatic)
                {
                    // 1.13.3: the automatic backup (before a mod is installed, removed or switched) is made in the
                    // background. Copying a few hundred save files stopped the game for a second each time (the
                    // stutter log showed 1.1 s at the click on Install); nothing waits for it, since changing a mod
                    // doesn't touch the saves.
                    _last = DateTime.Now;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            var made = BackupStore.Create(backups, SaveFiles(saves), reason, true, DateTime.Now);
                            if (made.Dir == null) return;       // (no saves, or none changed since the last backup)
                            BackupStore.Prune(backups, keep, max);
                            _stale = true;
                            Activity.Add("Backed up your saves (" + reason + ")");
                        }
                        catch (Exception e)
                        {
                            try { ModManagerPlugin.Log?.LogWarning("Couldn't back up the saves: " + e.Message); } catch { }
                        }
                    });
                    return null;
                }
                var m = BackupStore.Create(backups, SaveFiles(saves), reason, false, DateTime.Now);
                _stale = true;
                if (m.Dir == null) return null;
                _last = DateTime.Now;
                Activity.Add("Backed up your saves (" + reason + ")");
                return new Backup { Dir = m.Dir, Reason = reason, Time = DateTime.Now, Size = m.Size, Files = m.Files };
            }
            catch (Exception e)
            {
                ModManagerPlugin.Log?.LogWarning("Couldn't back up the saves: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// At game start, in the background: the backups are brought within the limits. This is also what cuts down the
        /// piles older versions left (ten whole copies of the saves, several GB for some players).
        /// </summary>
        public static void TidyAtStart()
        {
            try
            {
                string backups = BackupDir;
                int keep = Keep;
                long max = MaxBytes;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (!Directory.Exists(backups)) return;
                        long before = BackupStore.TotalSize(backups);
                        int n = BackupStore.Prune(backups, keep, max);
                        if (n == 0) return;
                        long after = BackupStore.TotalSize(backups);
                        _stale = true;
                        var text = $"Removed {n} old automatic save backup(s): {SizeText(before)} before, {SizeText(after)} now";
                        Activity.Add(text);
                        try { ModManagerPlugin.Log?.LogMessage(text + "."); } catch { }
                    }
                    catch (Exception e)
                    {
                        try { ModManagerPlugin.Log?.LogWarning("Couldn't tidy the save backups: " + e.Message); } catch { }
                    }
                });
            }
            catch { }
        }

        // The list is read from the disk every few seconds at most (it was read on every frame the Activity page was
        // open: thousands of files for a player with a big pile).
        private static List<Backup> _list;
        private static long _total;
        private static float _listedAt;
        private static volatile bool _stale = true;

        public static List<Backup> List()
        {
            if (_list != null && !_stale && Time.unscaledTime - _listedAt < 5f) return _list;
            _stale = false;
            _listedAt = Time.unscaledTime;
            var list = new List<Backup>();
            long total = 0;
            try
            {
                var dir = BackupDir;
                foreach (var b in BackupStore.List(dir))
                    list.Add(new Backup { Dir = b.Dir, Reason = b.Reason, Time = b.Time, Size = b.Size, Files = b.Files, Auto = b.Auto });
                total = BackupStore.TotalSize(dir);
            }
            catch { }
            _total = total;
            return _list = list;
        }

        /// <summary>The room all the backups take (as of the last <see cref="List"/>).</summary>
        public static long TotalSize => _total;

        /// <summary>After a setting changed: the backups are brought within the new limits.</summary>
        public static void ApplyLimits()
        {
            string backups = BackupDir;
            int keep = Keep;
            long max = MaxBytes;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (BackupStore.Prune(backups, keep, max) > 0) Activity.Add("Removed old automatic save backups (the limits changed)"); } catch { }
                _stale = true;
            });
        }

        /// <summary>Puts a backup's files back (the current saves are backed up first, so this can be undone).</summary>
        public static string Restore(Backup b)
        {
            try
            {
                Create("before restoring the backup from " + b.Time.ToString("d MMM HH:mm"), false);
                BackupStore.Restore(BackupDir, b.Dir, SaveDir);
                _stale = true;
                Activity.Add("Restored the save backup from " + b.Time.ToString("d MMM HH:mm"));
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public static void Delete(Backup b)
        {
            try { BackupStore.Remove(BackupDir, b.Dir); } catch { }
            _stale = true;
        }

        /// <summary>Removes every automatic backup. Returns how many.</summary>
        public static int DeleteAutomatic()
        {
            var n = 0;
            try { n = BackupStore.RemoveAutomatic(BackupDir); } catch { }
            _stale = true;
            if (n > 0) Activity.Add($"Deleted {n} automatic save backup(s)");
            return n;
        }
    }

    // ======================================================================
    // Activity

    /// <summary>What the menu did, newest last (BepInEx\NomadModManager\activity.log, kept to the last 300 lines).</summary>
    internal static class Activity
    {
        private static string FilePath => Path.Combine(Pending.Dir(Paths.GameRootPath), "activity.log");
        private static List<(DateTime Time, string Text)> _cache;

        public static void Add(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text.Replace("\n", " ") + "\r\n");
                _cache = null;
                if (new FileInfo(FilePath).Length > 120_000)
                {
                    var lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines.Skip(Math.Max(0, lines.Length - 300)).ToArray());
                }
            }
            catch { }
        }

        public static List<(DateTime Time, string Text)> Recent(int max = 200)
        {
            if (_cache != null) return _cache;
            var list = new List<(DateTime, string)>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var l in File.ReadAllLines(FilePath))
                    {
                        if (l.Length < 21) continue;
                        if (DateTime.TryParse(l.Substring(0, 19), out var t)) list.Add((t, l.Substring(21)));
                    }
            }
            catch { }
            list.Reverse();
            return _cache = list.Take(max).ToList();
        }
    }

    /// <summary>Errors and warnings in this session's BepInEx log, grouped by who said them.</summary>
    internal static class SessionErrors
    {
        internal sealed class Entry
        {
            public string Source = "", Level = "", Text = "";
            public int Count;
        }

        private static readonly Regex Header = new Regex(@"^\[(?<lvl>\w+)\s*:\s*(?<src>[^\]]*?)\s*\]\s?(?<msg>.*)$");
        private static List<Entry> _cache;
        private static DateTime _time;
        private static long _size;

        public static List<Entry> Read()
        {
            try
            {
                var fi = new FileInfo(StartupLog.LogPath);
                if (!fi.Exists) return new List<Entry>();
                if (_cache != null && fi.LastWriteTimeUtc == _time && fi.Length == _size) return _cache;
                _time = fi.LastWriteTimeUtc; _size = fi.Length;
                string text;
                using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                var byKey = new Dictionary<string, Entry>();
                var order = new List<Entry>();
                foreach (var line in text.Replace("\r", "").Split('\n'))
                {
                    var h = Header.Match(line);
                    if (!h.Success) continue;
                    var lvl = h.Groups["lvl"].Value;
                    if (lvl != "Error" && lvl != "Fatal" && lvl != "Warning") continue;
                    var msg = h.Groups["msg"].Value.Trim();
                    if (msg.Length == 0 || msg.StartsWith("Unable to start Unity log writer")) continue;
                    var src = h.Groups["src"].Value.Trim();
                    var key = lvl + "|" + src + "|" + msg;
                    if (byKey.TryGetValue(key, out var e)) { e.Count++; continue; }
                    e = new Entry { Source = src, Level = lvl, Text = msg.Length > 240 ? msg.Substring(0, 239) + "…" : msg, Count = 1 };
                    byKey[key] = e;
                    order.Add(e);
                }
                // Errors first, then warnings; each newest-looking last as in the log.
                return _cache = order.OrderBy(e => e.Level == "Warning" ? 1 : 0).ToList();
            }
            catch { return _cache ?? new List<Entry>(); }
        }
    }
}

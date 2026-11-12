#if NEXUS_EDITION
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// The Nexus edition's Nexus installs. The menu never downloads anything itself: Install opens the mod's file page on
    /// Nexus Mods in the player's own web browser, the player downloads it there as usual (Slow download, or Fast
    /// download for Premium members), and the menu watches their Downloads folder. When a new .zip or .dll has finished
    /// downloading there, the menu installs it, and the next mod in the list opens. The player's downloaded file is left
    /// where it is.
    /// </summary>
    internal sealed partial class ModsMenu
    {
        private bool _browserOpen;   // the "waiting for your download" panel is showing
        private readonly List<(int ModId, int FileId)> _browserQueue = new List<(int, int)>();
        private (int ModId, int FileId) _browserNow;
        private string _browserStatus = "";
        private bool _browserStatusGood = true;
        private bool _browserInstalling;
        private int _browserDone, _browserTotal;
        private string _browserName = "";

        private DateTime _watchSince;
        private float _nextWatch;
        private readonly Dictionary<string, long> _seenSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _wrongType = new List<string>();

        /// <summary>
        /// Every folder the player's web browsers save downloads to: the DownloadsFolder setting, the download folder set in
        /// Opera, Opera GX, Chrome, Edge, Brave, Vivaldi and Firefox, and Windows' own Downloads folder (which can be moved,
        /// e.g. to E:\Downloads). Looked up again each time a mod opens, so a changed browser setting counts.
        /// </summary>
        private List<string> _watchDirs = new List<string>();

        private static List<string> FindDownloadDirs()
        {
            var dirs = new List<string>();
            void Add(string d)
            {
                try
                {
                    d = Environment.ExpandEnvironmentVariables((d ?? "").Trim().Trim('"'));
                    if (d.Length > 0 && Directory.Exists(d) && !dirs.Any(x => string.Equals(x.TrimEnd('\\', '/'), d.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) dirs.Add(d);
                }
                catch { }
            }
            Add(ModManagerPlugin.I?.DownloadsFolder?.Value);
            var roaming = Environment.GetEnvironmentVariable("APPDATA") ?? "";
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            // Chromium browsers keep it in their Preferences file: "download": { "default_directory": "..." }
            foreach (var pref in new[]
            {
                Path.Combine(roaming, @"Opera Software\Opera Stable\Preferences"),
                Path.Combine(roaming, @"Opera Software\Opera Stable\Default\Preferences"),
                Path.Combine(roaming, @"Opera Software\Opera GX Stable\Preferences"),
                Path.Combine(roaming, @"Opera Software\Opera GX Stable\Default\Preferences"),
                Path.Combine(local, @"Google\Chrome\User Data\Default\Preferences"),
                Path.Combine(local, @"Microsoft\Edge\User Data\Default\Preferences"),
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data\Default\Preferences"),
                Path.Combine(local, @"Vivaldi\User Data\Default\Preferences"),
            })
            {
                try
                {
                    if (!File.Exists(pref)) continue;
                    var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(pref));
                    Add((string)j.SelectToken("download.default_directory"));
                    Add((string)j.SelectToken("savefile.default_directory"));
                }
                catch { }
            }
            // Firefox: user_pref("browser.download.dir", "...") in each profile's prefs.js
            try
            {
                var profiles = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
                if (Directory.Exists(profiles))
                    foreach (var prof in Directory.GetDirectories(profiles))
                    {
                        var js = Path.Combine(prof, "prefs.js");
                        if (!File.Exists(js)) continue;
                        foreach (var line in File.ReadAllLines(js))
                        {
                            if (line.IndexOf("\"browser.download.dir\"", StringComparison.Ordinal) < 0) continue;
                            var parts = line.Split('"');
                            if (parts.Length >= 4) Add(parts[3].Replace("\\\\", "\\"));
                        }
                    }
            }
            catch { }
            // Windows' Downloads folder, wherever it was moved to
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                    Add(k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                    Add(k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string);
            }
            catch { }
            var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            if (home.Length > 0) Add(Path.Combine(home, "Downloads"));
            return dirs;
        }

        private string WatchedText => _watchDirs.Count == 0 ? "your Downloads folder" : string.Join(" and ", _watchDirs.ToArray());

        // ------------------------------------------------------------------ entry points

        /// <summary>Nexus mods to install (file id 0 = the mod's newest main file): they open one after another in the web browser.</summary>
        private void BrowseNexus(List<(int ModId, int FileId)> items, string what)
        {
            items = items.Where(i => i.ModId > 0).GroupBy(i => i.ModId).Select(g => g.First()).ToList();
            if (items.Count == 0) return;
            SaveBackups.Create("before installing " + what, true);
            Activity.Add($"Installing {what} from Nexus Mods");
            _browserQueue.Clear();
            _browserQueue.AddRange(items);
            _browserDone = 0; _browserTotal = items.Count;
            _browserOpen = true;
            NextInBrowser();
        }

        /// <summary>Nomad Drive's mods on Nexus, in the player's web browser.</summary>
        private void OpenNexusHere()
        {
            Application.OpenURL($"https://www.nexusmods.com/games/{Nexus.GameDomain}/mods");
            Toast("Opened Nexus Mods in your web browser. Press Install on a mod here and the Mods menu installs what you download.", true);
        }

        private static string NexusFilesUrl(int modId, int fileId) =>
            $"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{modId}?tab=files" + (fileId > 0 ? "&file_id=" + fileId : "");

        private void NextInBrowser()
        {
            if (_browserQueue.Count == 0) return;
            _browserNow = _browserQueue[0];
            _browserQueue.RemoveAt(0);
            _browserName = _cat.Mods.FirstOrDefault(m => m.NexusId == _browserNow.ModId)?.Name
                           ?? _results.FirstOrDefault(m => m.Id == _browserNow.ModId)?.Name ?? $"mod #{_browserNow.ModId}";
            _host.StartCoroutine(OpenNext(_browserNow.ModId));
        }

        private IEnumerator OpenNext(int modId)
        {
            if (_browserNow.FileId == 0)
            {
                _browserStatus = "Finding the newest file…"; _browserStatusGood = true;
                var files = new List<NexusFileInfo>();
                yield return Nexus.Files(modId, l => files = l);   // newest first
                if (_browserNow.ModId != modId || !_browserOpen) yield break;
                var f = files.FirstOrDefault(x => x.Category == "MAIN") ?? files.FirstOrDefault(x => x.Category != "ARCHIVED" && x.Category != "OLD_VERSION") ?? files.FirstOrDefault();
                if (f != null) _browserNow = (modId, f.Id);
            }
            StartWatching();
            Application.OpenURL(NexusFilesUrl(_browserNow.ModId, _browserNow.FileId));
            _browserStatus = (_browserTotal > 1 ? $"{_browserDone + 1} of {_browserTotal}: " : "")
                + $"{_browserName} is open on Nexus Mods in your web browser. Download it there (free accounts: Slow download), and the Mods menu installs it.";
            _browserStatusGood = true;
        }

        /// <summary>What's already in the Downloads folder doesn't count: only files that arrive from now on.</summary>
        private void StartWatching()
        {
            _watchSince = DateTime.Now.AddSeconds(-5);
            _watchDirs = FindDownloadDirs();
            ModManagerPlugin.Log.LogInfo("Waiting for a Nexus download in: " + (_watchDirs.Count > 0 ? string.Join(", ", _watchDirs.ToArray()) : "(no Downloads folder found)"));
            _seenSize.Clear();
            _wrongType.Clear();
            _nextWatch = 0;
        }

        private void CloseBrowser()
        {
            _browserOpen = false;
            _browserQueue.Clear();
            _browserInstalling = false;
            _cat.Refresh();
        }

        // ------------------------------------------------------------------ watching the Downloads folder

        /// <summary>From Tick, every frame while the panel shows: once a second, a finished new download in the Downloads folder.</summary>
        private bool BrowserInput(UnityEngine.InputSystem.Keyboard kb, UnityEngine.InputSystem.Gamepad pad)
        {
            WatchDownloads();
            return false;   // the panel's buttons work with the controller like the rest of the menu
        }

        private bool BrowserDismiss() => false;

        private void WatchDownloads()
        {
            if (!_browserOpen || _browserInstalling || _browserNow.ModId == 0 || Time.unscaledTime < _nextWatch) return;
            _nextWatch = Time.unscaledTime + 1f;
            var all = new List<string>();
            foreach (var dir in _watchDirs)
            {
                try { all.AddRange(Directory.GetFiles(dir)); } catch { }
            }
            // a browser still writing: Chrome/Edge/Opera .crdownload, Firefox .part, others .partial / .download
            // (only ones written to in the last two minutes: an old abandoned download doesn't hold things up)
            var busy = all.Any(f => (f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                                    && Recent(f));
            foreach (var f in all)
            {
                if (_handled.Contains(f)) continue;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".zip" && ext != ".dll" && ext != ".7z" && ext != ".rar") continue;
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                if (fi.LastWriteTime < _watchSince && fi.CreationTime < _watchSince) continue;
                // finished = the same size twice in a row, nothing still downloading, and it can be opened
                if (busy || !_seenSize.TryGetValue(f, out var was) || was != fi.Length || fi.Length == 0) { _seenSize[f] = fi.Length; continue; }
                try { using (File.Open(f, FileMode.Open, FileAccess.Read, FileShare.Read)) { } } catch { continue; }
                _handled.Add(f);
                _host.StartCoroutine(InstallDownloaded(f));
                return;
            }
        }

        private static bool Recent(string f)
        {
            try { return (DateTime.Now - File.GetLastWriteTime(f)).TotalMinutes < 2; } catch { return false; }
        }

        private void BrowserType(char ch) { }

        /// <summary>The file the player downloaded for the mod that's open: matched to its Nexus file, then installed like any mod.</summary>
        private IEnumerator InstallDownloaded(string path)
        {
            if (_browserInstalling) yield break;
            _browserInstalling = true;
            try
            {
                var modId = _browserNow.ModId; var fileId = _browserNow.FileId;
                var original = Path.GetFileName(path);
                _browserStatus = "Installing " + original + "…"; _browserStatusGood = true;
                string name = "", author = "", modVersion = "";
                yield return Nexus.Info(modId, (n, a, v) => { name = n; author = a; modVersion = v; });
                var files = new List<NexusFileInfo>();
                yield return Nexus.Files(modId, l => files = l);
                var stem = Path.GetFileNameWithoutExtension(original).ToLowerInvariant();
                // Nexus names downloads "<file name>-<mod id>-<version>-<timestamp>": match on the file's own name first
                var file = files.FirstOrDefault(f => f.FileName.Length > 0 && stem.StartsWith(Path.GetFileNameWithoutExtension(f.FileName).ToLowerInvariant()))
                           ?? files.FirstOrDefault(f => f.Id == fileId && fileId > 0)
                           ?? files.FirstOrDefault(f => f.Category == "MAIN");
                if (name.Length == 0) name = _browserName;
                var version = file != null && file.Version.Length > 0 ? file.Version : modVersion;

                bool staged; string error = null;
                try { staged = Community.InstallNexusFile(path, modId, file?.Id ?? fileId, name, author, version, original); }
                catch (Exception ex) { staged = false; error = ex.Message; ModManagerPlugin.Log.LogWarning("Installing " + name + " from " + path + ": " + ex); }
                _cat.Refresh();
                if (error != null) { _browserStatus = $"Couldn't install {name}: {error}"; _browserStatusGood = false; yield break; }

                _browserDone++;
                var msg = $"{name} v{version.TrimStart('v', 'V')} is installed" + (staged ? " — it replaces files in use, so it switches over when you restart." : ".");
                Toast(msg, true);
                if (_browserQueue.Count > 0) { _browserStatus = msg + " Next one…"; NextInBrowser(); }
                else { _browserStatus = msg + (_browserTotal > 1 ? $" All {_browserDone} done." : ""); _browserNow = (0, 0); }
            }
            finally { _browserInstalling = false; }
        }

        // ------------------------------------------------------------------ drawing

        private void DrawBrowser(Rect r)
        {
            if (Btn(new Rect(r.x, r.y, 160 * _s, 40 * _s), _browserNow.ModId == 0 ? "‹  Done" : "‹  Cancel", false)) { CloseBrowser(); return; }
            var y = r.y + 64 * _s;
            Lbl(new Rect(r.x, y, r.width, 40 * _s), _browserNow.ModId == 0 ? "Installed from Nexus Mods" : "Waiting for your download", Tint(_h2, CText));
            y += 52 * _s;
            Lbl(new Rect(r.x, y, r.width, 70 * _s), _browserStatus, Tint(_wrap, _browserStatusGood ? CText : CWarn));
            y += 84 * _s;
            if (_browserNow.ModId == 0) return;

            var box = new Rect(r.x, y, r.width, 150 * _s);
            Fill(box, Alpha(CAccent, 0.12f), 12 * _s);
            Lbl(new Rect(box.x + 18 * _s, box.y + 12 * _s, box.width - 36 * _s, box.height - 24 * _s),
                "1. On the page that opened, press the file's download button (free accounts: Slow download, then wait a few seconds).\n"
                + "2. Let your browser save it. The Mods menu is watching " + WatchedText + ".\n"
                + "3. It installs as soon as the download finishes. Your downloaded file stays where it is.", Tint(_wrapSmall, CText));
            y = box.yMax + 14 * _s;
            var x = r.x;
            if (Btn(new Rect(x, y, 260 * _s, 42 * _s), "Open the page again", true)) Application.OpenURL(NexusFilesUrl(_browserNow.ModId, _browserNow.FileId));
            x += 272 * _s;
            if (_browserQueue.Count > 0 && Btn(new Rect(x, y, 200 * _s, 42 * _s), "Skip this one", false)) NextInBrowser();
            y += 60 * _s;
            Lbl(new Rect(r.x, y, r.width, 50 * _s),
                "Downloads saved somewhere else? Set DownloadsFolder on the Mods menu's Settings page.", Tint(_wrapSmall, CMuted));
            y += 60 * _s;

            // Already downloaded it (before pressing Install, or while the game was closed)? Pick it here.
            if (Time.unscaledTime >= _nextRecent) { _nextRecent = Time.unscaledTime + 3f; _recent = RecentArchives(); }
            if (_recent.Count == 0 || _browserInstalling) return;
            Lbl(new Rect(r.x, y, r.width, 30 * _s), "Already downloaded it? Recent downloads:", Tint(_smallBold, CText));
            y += 36 * _s;
            foreach (var f in _recent)
            {
                var row = new Rect(r.x, y, r.width, 44 * _s);
                Fill(row, CField, 10 * _s);
                Lbl(new Rect(row.x + 14 * _s, row.y, row.width - 200 * _s, row.height), Clip(Path.GetFileName(f), 90), Tint(_small, CText));
                if (Btn(new Rect(row.xMax - 170 * _s, row.y + 3 * _s, 160 * _s, 38 * _s), "Install this", true)) { _handled.Add(f); _host.StartCoroutine(InstallDownloaded(f)); return; }
                y += 50 * _s;
            }
        }

        private List<string> _recent = new List<string>();
        private float _nextRecent;

        /// <summary>The newest mod archives in the watched folders from the last day (at most 4).</summary>
        private List<string> RecentArchives()
        {
            var list = new List<(string F, DateTime T)>();
            foreach (var dir in _watchDirs)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".zip" && ext != ".7z" && ext != ".rar" && ext != ".dll") continue;
                        var t = File.GetLastWriteTime(f);
                        if ((DateTime.Now - t).TotalHours < 24) list.Add((f, t));
                    }
                }
                catch { }
            }
            return list.OrderByDescending(x => x.T).Take(4).Select(x => x.F).ToList();
        }
    }
}
#endif

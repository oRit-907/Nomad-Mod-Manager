using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    // =====================================================================
    // Mods from the Nomad Drive Mods community website (nomaddrivemods.online).
    // Unlike Nexus, its files can be downloaded directly, so the Mods menu installs them itself: no app needed.
    // Every file there has passed a VirusTotal scan before the site lists it.
    // =====================================================================

    internal sealed class SiteMod
    {
        public int Id;
        public string Name = "", Summary = "", Category = Categories.Misc, Author = "", Version = "";
        public int VersionId, Downloads, Endorsements;
        public int NexusId;   // the same mod on Nexus Mods, when the author linked it (0 = not linked)
        public long Updated;
        public string Thumb = "", Page = "";
        public string Card = "", Header = "";   // the author's square card picture and wide header banner ("" = none)

        public static SiteMod Parse(JToken t) => new SiteMod
        {
            Id = (int?)t["id"] ?? 0,
            Name = (string)t["name"] ?? "",
            Summary = (string)t["summary"] ?? "",
            Category = (string)t["category"] ?? Categories.Misc,
            Author = (string)t["author"] ?? "",
            Version = (string)t["version"] ?? "",
            NexusId = (int?)t["nexusId"] ?? 0,
            VersionId = (int?)t["versionId"] ?? 0,
            Downloads = (int?)t["downloads"] ?? 0,
            Endorsements = (int?)t["endorsements"] ?? 0,
            Updated = (long?)t["updated"] ?? 0,
            Thumb = (string)t["thumb"] ?? "",
            Card = (string)t["card"] ?? "",
            Header = (string)t["header"] ?? "",
            Page = (string)t["page"] ?? "",
        };
    }

    internal sealed class SiteVersion
    {
        public int Id;
        public string Version = "", Changelog = "", File = "", Sha256 = "", Download = "";
        public long Size, Created;
    }

    internal sealed class SiteRequirement
    {
        public int Id;                 // a mod on the site (0 = off-site)
        public string Name = "", Note = "", Url = "";
    }

    internal sealed class SiteDetails
    {
        public SiteMod Mod;
        public string Description = "", Requirements = "";
        public List<SiteVersion> Versions = new List<SiteVersion>();
        public List<SiteRequirement> Requires = new List<SiteRequirement>();
        public List<KeyValuePair<string, string>> Images = new List<KeyValuePair<string, string>>(); // picture URL, its label
        public bool Endorsed, Mine, Subscribed;     // for the player's website account (false without one)
        public bool BugReports = true;              // players can report bugs on the mod's page (the author can turn it off)
    }

    /// <summary>What this menu installed from the website: BepInEx\NomadModManager\community.json.</summary>
    internal sealed class SiteInstall
    {
        public int Id, VersionId;
        public string Name = "", Author = "", Version = "";
        public List<string> Files = new List<string>();
        public bool Sub;    // installed for a website subscription: kept updated, and removed when the player unsubscribes
    }

    internal static class Community
    {
        /// <summary>
        /// Whether the website has a newer version than the one installed. A mod can have several files for the same version
        /// (BepInEx 5 and BepInEx 6), and the listing names the newest file, so a higher file id alone isn't an update:
        /// when both version numbers are known, the version number decides.
        /// </summary>
        public static bool SiteNewer(int latestId, string latestVersion, int installedId, string installedVersion)
        {
            if (latestId <= installedId) return false;
            if ((latestVersion ?? "").Trim().Length == 0 || (installedVersion ?? "").Trim().Length == 0) return true;
            return ModVersion.IsNewer(latestVersion, installedVersion);
        }

        public const string DefaultSite = "https://nomaddrivemods.online/";
        public static readonly string[] SortNames = { "Updated", "Newest", "Popular", "Endorsed" };
        private static readonly string[] SortKeys = { "updated", "new", "downloads", "endorsed" };

        /// <summary>The site's address (from the config), ending in "/". Falls back to http:// if https:// can't be reached.</summary>
        public static string Site = DefaultSite;
        private static bool _triedHttp;

        public static string HostName { get { try { return new Uri(Site).Host; } catch { return Site; } } }

        /// <summary>No secure certificate yet: switch to plain http once. True if it switched.</summary>
        public static bool TryHttpFallback()
        {
            if (_triedHttp || !Site.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            _triedHttp = true;
            Site = "http://" + Site.Substring(8);
            return true;
        }

        // ------------------------------------------------------------------
        // Reading the site

        internal static IEnumerator GetJson(string query, Action<JObject> ok, Action<string> fail)
        {
            if (Edition.Nexus) { fail("The website isn't part of the Nexus edition of the Mods menu."); yield break; }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var req = UnityWebRequest.Get(Site + "index.php?p=api&" + query))
                {
                    req.timeout = 20;
                    SiteAccount.Sign(req); // lets the site say whether this player endorsed the mod
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        JObject json = null;
                        try { json = JObject.Parse(req.downloadHandler.text); } catch { }
                        if (json == null) { fail($"{HostName} sent something that isn't a mod list. Is the address in the config right?"); yield break; }
                        ok(json);
                        yield break;
                    }
                    // No secure certificate yet: try plain http once.
                    if (attempt == 0 && !_triedHttp && Site.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && req.responseCode == 0)
                    {
                        _triedHttp = true;
                        Site = "http://" + Site.Substring(8);
                        continue;
                    }
                    var err = req.responseCode == 404 ? "Mod not found" : $"Couldn't reach {HostName} ({req.error})";
                    fail(err);
                    yield break;
                }
            }
        }

        public static IEnumerator Search(string text, int sort, string category, int page, Action<List<SiteMod>, int, Dictionary<string, int>, string> done)
        {
            var q = "do=mods&per=48&page=" + page + "&sort=" + SortKeys[Mathf.Clamp(sort, 0, SortKeys.Length - 1)];
            if (!string.IsNullOrEmpty(text?.Trim())) q += "&q=" + Uri.EscapeDataString(text.Trim());
            if (!string.IsNullOrEmpty(category)) q += "&cat=" + Uri.EscapeDataString(category);
            JObject json = null; string err = null;
            yield return GetJson(q, j => json = j, e => err = e);
            if (err != null) { done(null, 0, null, err); yield break; }
            var list = (json["mods"] as JArray ?? new JArray()).Select(SiteMod.Parse).Where(m => m.Id > 0 && m.VersionId > 0).ToList();
            var counts = new Dictionary<string, int>();
            foreach (var c in json["categories"] as JArray ?? new JArray()) counts[(string)c["name"] ?? ""] = (int?)c["count"] ?? 0;
            done(list, (int?)json["total"] ?? list.Count, counts, null);
        }

        public static IEnumerator Details(int id, Action<SiteDetails, string> done)
        {
            JObject json = null; string err = null;
            yield return GetJson("do=mod&id=" + id, j => json = j, e => err = e);
            if (err != null) { done(null, err); yield break; }
            // The site's descriptions are BBCode; it also sends a plain-text copy for games.
            var d = new SiteDetails
            {
                Mod = SiteMod.Parse(json),
                Description = (string)json["descriptionText"] ?? (string)json["description"] ?? "",
                Requirements = (string)json["requirementsText"] ?? (string)json["requirements"] ?? "",
                Endorsed = (bool?)json["endorsed"] ?? false,
                Subscribed = (bool?)json["subscribed"] ?? false,
                Mine = (bool?)json["mine"] ?? false,
                BugReports = (bool?)json["bugReports"] ?? true,
            };
            var labels = (json["imageLabels"] as JArray ?? new JArray()).Select(x => (string)x ?? "").ToList();
            var pics = (json["images"] as JArray ?? new JArray()).Select(x => (string)x ?? "").ToList();
            for (var i = 0; i < pics.Count; i++)
                if (pics[i].Length > 0) d.Images.Add(new KeyValuePair<string, string>(pics[i], i < labels.Count ? labels[i] : ""));
            foreach (var q in json["requires"] as JArray ?? new JArray())
                d.Requires.Add(new SiteRequirement { Id = (int?)q["id"] ?? 0, Name = (string)q["name"] ?? "", Note = (string)q["note"] ?? "", Url = (string)q["page"] ?? "" });
            foreach (var q in json["requiresOffsite"] as JArray ?? new JArray())
                d.Requires.Add(new SiteRequirement { Name = (string)q["name"] ?? "", Note = (string)q["note"] ?? "", Url = (string)q["url"] ?? "" });
            foreach (var v in json["versions"] as JArray ?? new JArray())
                d.Versions.Add(new SiteVersion
                {
                    Id = (int?)v["id"] ?? 0, Version = (string)v["version"] ?? "", Changelog = (string)v["changelog"] ?? "",
                    File = (string)v["file"] ?? "", Sha256 = ((string)v["sha256"] ?? "").ToLowerInvariant(), Download = (string)v["download"] ?? "",
                    Size = (long?)v["size"] ?? 0, Created = (long?)v["created"] ?? 0,
                });
            done(d, null);
        }

        /// <summary>The latest version of each installed website mod, for updates.</summary>
        public static IEnumerator Check(IEnumerable<int> ids, Action<List<SiteMod>> done, IEnumerable<int> nexusIds = null, IEnumerable<string> names = null)
        {
            if (Edition.Nexus) { done(new List<SiteMod>()); yield break; }   // Nexus edition: updates come from Nexus only
            var list = ids.Distinct().ToList();
            var nx = (nexusIds ?? Enumerable.Empty<int>()).Distinct().Take(200).ToList();
            var nm = (names ?? Enumerable.Empty<string>()).Distinct().Take(200).ToList();
            if (list.Count == 0 && nx.Count == 0 && nm.Count == 0) { done(new List<SiteMod>()); yield break; }
            JObject json = null;
            var q = "do=check&ids=" + string.Join(",", list.Select(i => i.ToString()).ToArray());
            if (nx.Count > 0) q += "&nexus=" + string.Join(",", nx.Select(i => i.ToString()).ToArray());
            if (nm.Count > 0) q += "&names=" + Uri.EscapeDataString(string.Join(",", nm.ToArray()));
            yield return GetJson(q, j => json = j, e => { });
            done(json == null ? new List<SiteMod>() : (json["mods"] as JArray ?? new JArray()).Select(SiteMod.Parse).ToList());
        }

        // ------------------------------------------------------------------
        // What's installed from the site

        private static string ManifestPath => Path.Combine(Pending.Dir(Paths.GameRootPath), "community.json");

        public static List<SiteInstall> Installed()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return new List<SiteInstall>();
                return (JObject.Parse(File.ReadAllText(ManifestPath))["Mods"] as JArray ?? new JArray()).Select(t => new SiteInstall
                {
                    Id = (int?)t["Id"] ?? 0, VersionId = (int?)t["VersionId"] ?? 0, Name = (string)t["Name"] ?? "", Author = (string)t["Author"] ?? "",
                    Version = (string)t["Version"] ?? "", Files = (t["Files"] as JArray ?? new JArray()).Select(f => (string)f).Where(f => !string.IsNullOrEmpty(f)).ToList(),
                    Sub = (bool?)t["Sub"] ?? false,
                }).Where(m => m.Id > 0).ToList();
            }
            catch { return new List<SiteInstall>(); }
        }

        private static void SaveInstalled(List<SiteInstall> mods)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath));
            var arr = new JArray(mods.Select(m => new JObject
            {
                ["Id"] = m.Id, ["VersionId"] = m.VersionId, ["Name"] = m.Name, ["Author"] = m.Author, ["Version"] = m.Version,
                ["Files"] = new JArray(m.Files.ToArray()), ["Sub"] = m.Sub,
            }));
            var tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, new JObject { ["Mods"] = arr }.ToString());
            if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
            File.Move(tmp, ManifestPath);
        }

        /// <summary>The website mod that owns an entry in BepInEx\plugins (a folder or a single DLL), if any.</summary>
        public static SiteInstall OwnerOf(List<SiteInstall> installed, string rel)
        {
            rel = Pending.Norm(rel);
            return installed.FirstOrDefault(m => m.Files.Any(f =>
                f.Equals(rel, StringComparison.OrdinalIgnoreCase) || f.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)
                || f.Equals(rel + Pending.OffSuffix, StringComparison.OrdinalIgnoreCase)));
        }

        // ------------------------------------------------------------------
        // Installing

        public static int Busy;            // the mod being installed (0 = none)
        public static float Progress;
        public static string Status = "";

        /// <summary>Downloads a version (the newest if versionId is 0), checks it, and puts its files in place.</summary>
        public static IEnumerator Install(int modId, int versionId, Action<bool, string> done, bool sub = false)
        {
            if (Edition.Nexus) { done(false, "Website mods aren't part of the Nexus edition of the Mods menu."); yield break; }
            if (Busy != 0) { done(false, "Another mod is still installing."); yield break; }
            Busy = modId; Progress = 0; Status = "Getting the mod's details…";
            try
            {
                SiteDetails d = null; string err = null;
                yield return Details(modId, (x, e) => { d = x; err = e; });
                if (d == null) { done(false, err ?? "Couldn't load the mod."); yield break; }
                var v = versionId > 0 ? d.Versions.FirstOrDefault(x => x.Id == versionId) : d.Versions.FirstOrDefault();
                if (v == null || v.Download.Length == 0) { done(false, "That file isn't available."); yield break; }
                var ext = Path.GetExtension(v.File).ToLowerInvariant();
                if (ext == ".exe") { done(false, $"{d.Mod.Name}: that file is a program (.exe). The Mods menu only installs mods, never programs."); yield break; }
                if (ext != ".zip" && ext != ".dll")
                {
                    done(false, $"{d.Mod.Name} comes as a {ext} file, which the game can't unpack. Opened its page: download it there and install it by hand.");
                    Application.OpenURL(d.Mod.Page);
                    yield break;
                }

                // Download to BepInEx\NomadModManager\downloads
                var dir = Path.Combine(Pending.Dir(Paths.GameRootPath), "downloads");
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, $"site-{modId}-{v.Id}{ext}");
                Status = $"Downloading {d.Mod.Name}…";
                using (var req = new UnityWebRequest(v.Download, "GET"))
                {
                    req.downloadHandler = new DownloadHandlerFile(tmp, false);
                    SiteAccount.Sign(req); // counts as the player's download, so the site tells them about updates
                    req.timeout = 600;
                    var op = req.SendWebRequest();
                    while (!op.isDone) { if (v.Size > 0) Progress = Mathf.Clamp01(req.downloadedBytes / (float)v.Size) * 0.9f; yield return null; }
                    if (req.result != UnityWebRequest.Result.Success) { TryDelete(tmp); done(false, $"The download failed ({req.error})."); yield break; }
                }
                Progress = 0.92f; Status = "Checking the file…";
                yield return null;

                string result = null; var ok = false;
                try
                {
                    if (v.Sha256.Length == 64 && Sha256(tmp) != v.Sha256) throw new InvalidOperationException("The download doesn't match the file on the site (it may have been cut off). Try again.");
                    Status = "Installing…";
                    var restart = InstallFile(tmp, ext, d, v, sub);
                    ok = true;
                    result = restart ? $"{d.Mod.Name} v{v.Version.TrimStart('v', 'V')} is installed — it replaces files in use, so it switches over when you restart."
                                     : $"{d.Mod.Name} v{v.Version.TrimStart('v', 'V')} is installed.";
                    Activity.Add($"Installed {d.Mod.Name} v{v.Version} from {HostName}");
                }
                catch (Exception e)
                {
                    result = $"Couldn't install {d.Mod.Name}: {e.Message}";
                    ModManagerPlugin.Log.LogWarning(result + "\n" + e);
                }
                finally { TryDelete(tmp); }
                Progress = 1f;
                done(ok, result);
            }
            finally { Busy = 0; Status = ""; }
        }

        // ------------------------------------------------------------------
        // Subscriptions: mods the player subscribed to on the website install, update and uninstall by themselves.

        public static bool Syncing;
        private static float _lastSync = -999f;

        /// <summary>
        /// Brings the game in line with the account's subscriptions: installs subscribed mods that are missing, updates
        /// them when the site has a newer file, and removes mods installed for a subscription the player ended.
        /// report(text, good) tells the player what happened; changed() runs when files changed.
        /// </summary>
        public static IEnumerator SyncSubscriptions(Action<string, bool> report, Action changed, bool force = false)
        {
            if (Edition.Nexus || Syncing || !SiteAccount.Has || Busy != 0) yield break;
            if (!force && Time.realtimeSinceStartup - _lastSync < 120f) yield break; // at most every 2 minutes
            Syncing = true; _lastSync = Time.realtimeSinceStartup;
            try
            {
                JObject j = null; long code = 0;
                yield return SiteAccount.Post("account.subscriptions", new Dictionary<string, string> { ["bep"] = Bep.Major.ToString() }, (x, c) => { j = x; code = c; });
                if (j == null || code != 200 || !(j["mods"] is JArray arr)) yield break;
                var subs = arr.Select(t => (Id: (int?)t["id"] ?? 0, VersionId: (int?)t["versionId"] ?? 0, Name: (string)t["name"] ?? "", Version: (string)t["version"] ?? "")).Where(t => t.Id > 0).ToList();
                var wanted = new HashSet<int>(subs.Select(s => s.Id));
                int added = 0, updated = 0, removed = 0;

                foreach (var s in subs)
                {
                    if (s.VersionId <= 0) continue;
                    if (MatchHost.IsPinned(s.Id)) continue;   // held on a host's version until "Back to my mods"
                    var have = Installed().FirstOrDefault(m => m.Id == s.Id);
                    if (have != null && have.VersionId == s.VersionId)
                    {
                        if (!have.Sub) { var all = Installed(); all.First(m => m.Id == s.Id).Sub = true; SaveInstalled(all); } // now kept by the subscription
                        continue;
                    }
                    // Never swap a newer copy for an older one: when the mod's own DLL (say a test build) is newer than the
                    // website's version, keep it and say so once.
                    if (have != null && s.Version.Length > 0)
                    {
                        var mine = InstalledDllVersion(have);
                        if (mine.Length > 0 && ModVersion.IsNewer(mine, s.Version))
                        {
                            if (_keptNewer.Add(s.Id + "|" + s.VersionId))
                                ModManagerPlugin.Log.LogMessage($"Subscription {s.Name}: kept your installed {mine}, it's newer than the website's {s.Version}.");
                            continue;
                        }
                        // The same version number (a file uploaded again): nothing to update.
                        if (ModVersion.Same(mine.Length > 0 ? mine : have.Version, s.Version)) continue;
                    }
                    var ok = false; string msg = null;
                    yield return Install(s.Id, s.VersionId, (o, m) => { ok = o; msg = m; }, sub: true);
                    if (ok) { if (have == null) added++; else updated++; }
                    else if (msg != null) ModManagerPlugin.Log.LogWarning("Subscription " + s.Name + ": " + msg);
                }

                foreach (var gone in Installed().Where(m => m.Sub && !wanted.Contains(m.Id)).ToList())
                {
                    RemoveSiteInstall(gone);
                    Activity.Add($"Removed {gone.Name} (unsubscribed on {HostName})");
                    removed++;
                }

                if (added + updated + removed > 0)
                {
                    changed?.Invoke();
                    report?.Invoke($"Subscriptions synced: {added} installed, {updated} updated, {removed} removed. Restart the game if a mod asks for it.", true);
                }
            }
            finally { Syncing = false; }
        }

        private static readonly HashSet<string> _keptNewer = new HashSet<string>();

        /// <summary>The version of the plugin a website install's DLLs hold (the running one, else read from the file).</summary>
        private static string InstalledDllVersion(SiteInstall mod)
        {
            try
            {
                var root = Paths.GameRootPath;
                var best = "";
                foreach (var rel in mod.Files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    var full = Path.GetFullPath(SafeJoin(root, rel));
                    foreach (var info in Bep.Plugins.Values)
                    {
                        if (info?.Location == null) continue;
                        if (!Path.GetFullPath(info.Location).Equals(full, StringComparison.OrdinalIgnoreCase)) continue;
                        var v = info.Metadata.Version.ToString();
                        if (best.Length == 0 || ModVersion.IsNewer(v, best)) best = v;
                    }
                }
                return best;
            }
            catch { return ""; }
        }

        /// <summary>Deletes a website install's files (or removes them at the next launch if they're in use) and forgets it.</summary>
        public static void RemoveSiteInstall(SiteInstall mod)
        {
            var root = Paths.GameRootPath;
            foreach (var rel in mod.Files)
            {
                try
                {
                    var full = SafeJoin(root, rel);
                    foreach (var f in new[] { full, full + Pending.OffSuffix, full + Pending.NewSuffix })
                        if (File.Exists(f)) { try { File.Delete(f); } catch { Pending.Add(root, "remove", Pending.Norm(rel)); } }
                }
                catch { }
            }
            // Empty folders the mod leaves behind in BepInEx\plugins
            foreach (var dir in mod.Files.Select(f => Pending.Norm(f)).Where(f => f.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) && f.Count(c => c == '/') >= 3)
                         .Select(f => string.Join("/", f.Split('/').Take(3).ToArray())).Distinct())
            {
                try { var full = SafeJoin(root, dir); if (Directory.Exists(full) && !Directory.GetFileSystemEntries(full, "*", SearchOption.AllDirectories).Any(e => File.Exists(e))) Directory.Delete(full, true); } catch { }
            }
            var all = Installed();
            all.RemoveAll(m => m.Id == mod.Id);
            SaveInstalled(all);
        }

        /// <summary>Puts the files in place. Returns true if some of them wait for a restart (they replace files in use).</summary>
        private static bool InstallFile(string path, string ext, SiteDetails d, SiteVersion v, bool sub = false)
        {
            var installed = Installed();
            var previous = installed.FirstOrDefault(m => m.Id == d.Mod.Id);
            var staged = Place(path, ext, d.Mod.Name, Path.GetFileName(v.File), previous?.Files, out var now);
            installed.RemoveAll(m => m.Id == d.Mod.Id);
            installed.Add(new SiteInstall { Id = d.Mod.Id, VersionId = v.Id, Name = d.Mod.Name, Author = d.Mod.Author, Version = v.Version, Files = now, Sub = sub || (previous?.Sub ?? false) });
            SaveInstalled(installed);
            return staged;
        }

        // ------------------------------------------------------------------
        // Nexus mods installed from the in-game browser: recorded in BepInEx\NomadModManager\nexus.json, the same shape
        // as Nomad Mod Downloader's installed.json (ModId, FileId, Name, Author, Version, Files), so the Installed list
        // knows which Nexus mod each folder is and checks it for updates.

        private static string NexusManifestPath => Path.Combine(Pending.Dir(Paths.GameRootPath), "nexus.json");

        public static JArray NexusInstalled()
        {
            try { return File.Exists(NexusManifestPath) ? (JObject.Parse(File.ReadAllText(NexusManifestPath))["Mods"] as JArray ?? new JArray()) : new JArray(); }
            catch { return new JArray(); }
        }

        /// <summary>Installs a file the player downloaded from Nexus (a .zip or .dll). Returns true if some files wait for a restart.</summary>
        public static bool InstallNexusFile(string path, int modId, int fileId, string modName, string author, string version, string fileName)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".zip" && ext != ".dll" && !Unpack.Handles(ext)) throw new InvalidOperationException("That file can't be installed here (only .zip, .7z, .rar and .dll).");
            var list = NexusInstalled();
            var previous = list.OfType<JObject>().FirstOrDefault(m => ((int?)m["ModId"] ?? 0) == modId);
            var prevFiles = (previous?["Files"] as JArray)?.Select(f => (string)f).Where(f => !string.IsNullOrEmpty(f)).ToList();
            var staged = Place(path, ext, modName, fileName, prevFiles, out var now);
            if (previous != null) previous.Remove();
            list.Add(new JObject
            {
                ["Id"] = modId + ":" + fileId, ["ModId"] = modId, ["FileId"] = fileId, ["Name"] = modName, ["Author"] = author, ["FileName"] = fileName,
                ["Version"] = version, ["InstalledAt"] = DateTime.UtcNow.ToString("o"), ["Files"] = new JArray(now.Cast<object>().ToArray()), ["From"] = "NomadModManager",
            });
            Directory.CreateDirectory(Path.GetDirectoryName(NexusManifestPath));
            var tmp = NexusManifestPath + ".tmp";
            File.WriteAllText(tmp, new JObject { ["Mods"] = list }.ToString());
            if (File.Exists(NexusManifestPath)) File.Delete(NexusManifestPath);
            File.Move(tmp, NexusManifestPath);
            Activity.Add($"Installed {modName} v{version} from Nexus Mods" + (Edition.Nexus ? "" : " (in-game browser)"));
            return staged;
        }

        /// <summary>
        /// Writes a mod's files (a .zip laid out by <see cref="Plan"/>, or a single .dll) into the game, and takes away the
        /// files an older version had that this one doesn't. DLLs in use wait next to the old ones until the next start.
        /// </summary>
        private static bool Place(string path, string ext, string modName, string dllName, List<string> previousFiles, out List<string> files)
        {
            var root = Paths.GameRootPath;
            var folder = "BepInEx/plugins/" + SafeName(modName);
            var written = new List<string>();
            var staged = false;

            void Put(string rel, Action<Stream> write)
            {
                rel = Pending.Norm(rel);
                var full = SafeJoin(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                var exists = File.Exists(full) || File.Exists(full + Pending.OffSuffix);
                var inBep = rel.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase);
                // A DLL that's already there is loaded by the game: the new one waits next to it and is swapped in at launch.
                var target = exists && inBep && rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? full + Pending.NewSuffix : full;
                try { using (var fs = File.Create(target)) write(fs); }
                catch (IOException) when (target == full && inBep)
                {
                    target = full + Pending.NewSuffix;
                    using (var fs = File.Create(target)) write(fs);
                }
                if (target != full) staged = true;
                written.Add(rel);
            }

            if (ext == ".dll")
            {
                var rel = folder + "/" + SafeName(string.IsNullOrEmpty(dllName) || !dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path) : dllName);
                Put(rel, s => { using (var src = File.OpenRead(path)) src.CopyTo(s); });
            }
            else if (Unpack.Handles(ext))
            {
                // .7z / .rar: unpacked in memory, then placed by the same rules as a zip
                var all = Unpack.ReadAll(path);
                var plan = Plan(all.Keys.ToList(), folder);
                if (plan.Count == 0) throw new InvalidOperationException("There's nothing in this file that can be installed.");
                foreach (var rel in plan.Values) SafeJoin(root, rel);
                foreach (var kv in all)
                    if (plan.TryGetValue(kv.Key, out var rel)) { var bytes = kv.Value; Put(rel, s => s.Write(bytes, 0, bytes.Length)); }
            }
            else
            {
                using (var zip = new MiniZip(path))
                {
                    var plan = Plan(zip.Entries.Select(e => e.Name).ToList(), folder);
                    if (plan.Count == 0) throw new InvalidOperationException("There's nothing in this file that can be installed.");
                    foreach (var rel in plan.Values) SafeJoin(root, rel); // every path is checked before anything is written
                    foreach (var e in zip.Entries)
                        if (plan.TryGetValue(e.Name, out var rel)) Put(rel, s => zip.Extract(e, s));
                }
            }

            // Files the old version had that the new one doesn't: removed (now, or at launch if they're in use).
            var now = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
            if (previousFiles != null)
                foreach (var old in previousFiles.Where(f => !now.Contains(f)))
                {
                    try
                    {
                        var full = SafeJoin(root, old);
                        if (File.Exists(full)) { try { File.Delete(full); } catch { Pending.Add(root, "remove", old); } }
                    }
                    catch { }
                }

            files = now.ToList();
            return staged;
        }

        // ------------------------------------------------------------------
        // Where the files go: the same rules as Nomad Mod Downloader.

        private static readonly HashSet<string> Junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };
        private static readonly HashSet<string> DocExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".url", ".png", ".jpg", ".jpeg", ".gif", ".pdf", ".html", ".htm", ".rtf", ".webp" };
        private static readonly HashSet<string> BepSubdirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "plugins", "patchers", "config", "monomod" };
        private static readonly HashSet<string> LoaderRootFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "winhttp.dll", "version.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt", "run_bepinex.sh", "libdoorstop.so", "libdoorstop.dylib" };
        private static readonly string[] LoaderFolders = { "BepInEx/core/", "BepInEx/cache/", "BepInEx/unity-libs/", "BepInEx/interop/", "dotnet/", "doorstop_libs/" };

        private static bool IsLoaderFile(string rel) =>
            LoaderRootFiles.Contains(rel) || LoaderFolders.Any(f => rel.StartsWith(f, StringComparison.OrdinalIgnoreCase))
            || rel.Equals("BepInEx/config/BepInEx.cfg", StringComparison.OrdinalIgnoreCase) || rel.Equals("BepInEx/LogOutput.log", StringComparison.OrdinalIgnoreCase);

        private static bool IsRootDoc(string rel) => rel.IndexOf('/') < 0 && DocExt.Contains(Path.GetExtension(rel));

        /// <summary>zip entry name -> where it goes, relative to the game folder. BepInEx itself is never replaced.</summary>
        public static Dictionary<string, string> Plan(List<string> names, string folder)
        {
            var entries = names.Select(n => (Key: n, Path: Pending.Norm(n)))
                .Where(e => e.Path.Length > 0 && !e.Key.EndsWith("/") && !e.Path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) && !Junk.Contains(Path.GetFileName(e.Path)))
                .Where(e => !e.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) // programs are never installed
                .ToList();
            var map = new Dictionary<string, string>();
            if (entries.Count == 0) return map;

            // Strip wrapper folders like "MyMod-1.2/" that every entry shares.
            var paths = entries.Select(e => e.Path).ToList();
            while (true)
            {
                var segs = paths.Select(p => p.Split('/')).ToList();
                if (segs.Any(s => s.Length < 2)) break;
                var first = segs[0][0];
                if (!segs.All(s => s[0].Equals(first, StringComparison.OrdinalIgnoreCase))) break;
                if (first.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) || BepSubdirs.Contains(first) || first.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) break;
                paths = paths.Select(p => p.Substring(p.IndexOf('/') + 1)).ToList();
            }
            var items = entries.Select((e, i) => (e.Key, Rel: paths[i])).Where(i => !IsLoaderFile(i.Rel)).ToList();

            // Laid out from the game folder: .../BepInEx/plugins/...
            var withBep = items.Select(i => (i.Key, i.Rel, Idx: Array.FindIndex(i.Rel.Split('/'), s => s.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)))).Where(x => x.Idx >= 0).ToList();
            if (withBep.Count > 0)
            {
                var depth = withBep.Select(x => x.Idx).Min();
                var prefixSegs = withBep.First(x => x.Idx == depth).Rel.Split('/').Take(depth).ToArray();
                var prefix = prefixSegs.Length == 0 ? "" : string.Join("/", prefixSegs) + "/";
                foreach (var i in items)
                {
                    if (!i.Rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = i.Rel.Substring(prefix.Length);
                    if (!IsRootDoc(rel)) map[i.Key] = rel;
                }
                return map;
            }
            // Starts at plugins/ patchers/ config/
            if (items.Any(i => i.Rel.Contains("/") && BepSubdirs.Contains(i.Rel.Split('/')[0])))
            {
                foreach (var i in items)
                {
                    var first = i.Rel.Split('/')[0];
                    if (i.Rel.Contains("/") && BepSubdirs.Contains(first)) map[i.Key] = "BepInEx/" + i.Rel;
                    else if (!IsRootDoc(i.Rel)) map[i.Key] = folder + "/" + i.Rel;
                }
                return map;
            }
            // Loose files: the mod's own folder in BepInEx\plugins
            if (!items.Any(i => i.Rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The file has no plugin (.dll) in it, so it can't be installed automatically. Check the mod's page for how to install it.");
            foreach (var i in items) map[i.Key] = folder + "/" + i.Rel;
            return map;
        }

        // ------------------------------------------------------------------
        // Helpers

        public static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(name) ? "mod" : name;
        }

        /// <summary>Game folder + relative path, refusing anything that would land outside it.</summary>
        public static string SafeJoin(string root, string rel)
        {
            rel = Pending.Norm(rel);
            if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('/').Any(s => s == ".." || s.Contains(":")))
                throw new InvalidOperationException("Refusing an unsafe path in the file: " + rel);
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing an unsafe path in the file: " + rel);
            return full;
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }

    /// <summary>
    /// Reads .zip files. The game's .NET has the decompressor (DeflateStream) but not the zip format around it
    /// (System.IO.Compression.ZipArchive isn't shipped), so this reads the zip's directory itself.
    /// Stored and deflated entries; no encryption, no zip64.
    /// </summary>
    internal sealed class MiniZip : IDisposable
    {
        internal sealed class Entry
        {
            public string Name = "";
            public int Method, Flags;
            public long Compressed, Size, HeaderOffset;
        }

        private readonly FileStream _fs;
        private readonly BinaryReader _r;
        public readonly List<Entry> Entries = new List<Entry>();
        private const long MaxTotal = 4L * 1024 * 1024 * 1024; // refuses zip bombs

        public MiniZip(string path)
        {
            _fs = File.OpenRead(path);
            _r = new BinaryReader(_fs);
            // End of central directory: the last 22 bytes, plus a comment of up to 64 KB.
            var len = _fs.Length;
            var tail = (int)Math.Min(len, 22 + 65535);
            _fs.Position = len - tail;
            var buf = _r.ReadBytes(tail);
            var eocd = -1;
            for (var i = buf.Length - 22; i >= 0; i--)
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("This isn't a valid .zip file.");
            var count = BitConverter.ToUInt16(buf, eocd + 10);
            var cdOffset = BitConverter.ToUInt32(buf, eocd + 16);
            if (count == 0xFFFF || cdOffset == 0xFFFFFFFF) throw new InvalidDataException("This .zip uses the zip64 format, which isn't supported here.");

            _fs.Position = cdOffset;
            long total = 0;
            for (var i = 0; i < count; i++)
            {
                if (_r.ReadUInt32() != 0x02014B50) throw new InvalidDataException("The .zip file is damaged.");
                _r.ReadUInt16(); _r.ReadUInt16();                 // made by, needed
                var flags = _r.ReadUInt16();
                var method = _r.ReadUInt16();
                _r.ReadUInt32(); _r.ReadUInt32();                 // time/date, crc
                var comp = _r.ReadUInt32();
                var size = _r.ReadUInt32();
                var nameLen = _r.ReadUInt16();
                var extraLen = _r.ReadUInt16();
                var commentLen = _r.ReadUInt16();
                _r.ReadUInt16(); _r.ReadUInt16(); _r.ReadUInt32(); // disk, attributes
                var offset = _r.ReadUInt32();
                var name = Encoding.UTF8.GetString(_r.ReadBytes(nameLen)).Replace('\\', '/');
                _fs.Position += extraLen + commentLen;
                total += size;
                if (total > MaxTotal) throw new InvalidDataException("The .zip unpacks to more than 4 GB.");
                Entries.Add(new Entry { Name = name, Method = method, Flags = flags, Compressed = comp, Size = size, HeaderOffset = offset });
            }
        }

        public void Extract(Entry e, Stream to)
        {
            if ((e.Flags & 1) != 0) throw new InvalidDataException(e.Name + " is password-protected.");
            if (e.Method != 0 && e.Method != 8) throw new InvalidDataException(e.Name + " uses a compression the game can't unpack (only standard .zip is supported).");
            _fs.Position = e.HeaderOffset;
            if (_r.ReadUInt32() != 0x04034B50) throw new InvalidDataException("The .zip file is damaged.");
            _fs.Position = e.HeaderOffset + 26;
            var nameLen = _r.ReadUInt16();
            var extraLen = _r.ReadUInt16();
            _fs.Position = e.HeaderOffset + 30 + nameLen + extraLen;
            var raw = new SubStream(_fs, e.Compressed);
            var src = e.Method == 8 ? (Stream)new DeflateStream(raw, CompressionMode.Decompress) : raw;
            var buf = new byte[81920];
            long written = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                written += n;
                if (written > e.Size + 1024) throw new InvalidDataException(e.Name + " is bigger than the .zip says.");
                to.Write(buf, 0, n);
            }
            if (written != e.Size) throw new InvalidDataException(e.Name + " couldn't be unpacked completely.");
            if (src != raw) src.Dispose();
        }

        public void Dispose() { _r.Close(); _fs.Dispose(); }

        /// <summary>A window onto part of the zip, so the decompressor can't read past the entry.</summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _s;
            private long _left;
            public SubStream(Stream s, long length) { _s = s; _left = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) return 0;
                var n = _s.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= n;
                return n;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}

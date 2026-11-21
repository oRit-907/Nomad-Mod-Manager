using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    /// <summary>
    /// 1.10.0: gets Check BepInEx from nomaddrivemods.online (not GitHub any more). 1.16.3: Check BepInEx is part of Nomad
    /// Mod Downloader (5.0.0 on), so this gets that: reads the site's /app?app=downloader answer, downloads the app with
    /// progress, checks it against the site's SHA-256, puts it in %LOCALAPPDATA%\Programs\Nomad Mod Downloader for this
    /// Windows user, registers it for the game and opens it on its Check BepInEx page.
    /// Only ever runs when the player presses the button.
    /// </summary>
    internal static class AppFetcher
    {
        public const string Repo = "oRit-907/Nomad-Mod-Manager";   // the in-game browser's download (InGameBrowser): browser-vX.Y.Z releases
        public const string Site = "https://www.nomaddrivemods.online";
        public const string ReleasesPage = Site + "/downloader";
        private const string Api = Site + "/index.php?p=app&app=downloader";

        public static bool Busy { get; private set; }
        public static float Progress { get; private set; }
        public static string Status { get; private set; } = "";
        public static long Size { get; private set; }

        public static string InstallDir => Path.Combine(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Paths.GameRootPath, "Programs"), "Nomad Mod Downloader");

        public static void Start(MonoBehaviour host, Action<bool, string> done)
        {
            if (Edition.Nexus) { done(false, "The Nexus edition of the Mods menu doesn't download programs."); return; }
            if (Busy) return;
            host.StartCoroutine(Run(done));
        }

        private static IEnumerator Run(Action<bool, string> done)
        {
            Busy = true; Progress = 0; Status = "Finding the latest version…";
            string url = null, version = null, sha = null;
            long size = 0;

            using (var req = UnityWebRequest.Get(Api))
            {
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("User-Agent", "NomadModManager/" + ModManagerPlugin.Version);
                req.timeout = 30;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Finish(done, false, "Couldn't reach nomaddrivemods.online (" + req.error + ")."); yield break; }
                try
                {
                    var rel = JObject.Parse(req.downloadHandler.text);
                    version = (string)rel["version"];
                    var f = rel["files"]?["exe"] as JObject;
                    if (f != null)
                    {
                        url = (string)f["url"];
                        size = (long?)f["size"] ?? 0;
                        sha = ((string)f["sha256"] ?? "").ToLowerInvariant();
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(url) || !url.StartsWith(Site + "/", StringComparison.OrdinalIgnoreCase) || sha == null || sha.Length != 64)
            {
                Finish(done, false, "The website has no Nomad Mod Downloader download right now.");
                yield break;
            }

            Size = size;
            var temp = Path.Combine(Path.GetTempPath(), "NomadModDownloader-" + version + ".exe");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            Status = $"Downloading Nomad Mod Downloader {version}…";
            using (var req = new UnityWebRequest(url, "GET"))
            {
                req.downloadHandler = new DownloadHandlerFile(temp, false);
                req.SetRequestHeader("User-Agent", "NomadModManager/" + ModManagerPlugin.Version);
                var op = req.SendWebRequest();
                // (downloadProgress is stripped from the game; downloadedBytes against the site's size works the same)
                while (!op.isDone) { Progress = size > 0 ? Mathf.Clamp01(req.downloadedBytes / (float)size) : 0f; yield return null; }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    Finish(done, false, "Download failed (" + req.error + ").");
                    yield break;
                }
            }

            try
            {
                string got;
                using (var s = File.OpenRead(temp)) using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                if (got != sha) { File.Delete(temp); Finish(done, false, "The download didn't match its checksum, so it wasn't used. Try again."); yield break; }
            }
            catch (Exception e) { Finish(done, false, "Couldn't check the download: " + e.Message); yield break; }

            // Put it in place for this user (one exe, nothing to install), then register and open it.
            Status = $"Setting up Nomad Mod Downloader {version}…";
            var target = Path.Combine(InstallDir, "NomadModDownloader.exe");
            try
            {
                Directory.CreateDirectory(InstallDir);
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }
            catch (Exception e)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                Finish(done, false, "Couldn't put the app in " + InstallDir + ": " + e.Message);
                yield break;
            }
            try { Downloader.Register(target); } catch { }
            ModManagerPlugin.Log.LogMessage($"Installed Nomad Mod Downloader {version} from nomaddrivemods.online to {target}");

            Progress = 1;
            Finish(done, Downloader.ShowCheck(), $"Nomad Mod Downloader {version} is in {InstallDir}. Its Check BepInEx page is open.");
        }

        private static void Finish(Action<bool, string> done, bool ok, string message)
        {
            Busy = false;
            Status = message;
            if (!ok) ModManagerPlugin.Log.LogWarning("Getting Nomad Mod Downloader: " + message);
            try { done?.Invoke(ok, message); } catch { }
        }
    }
}

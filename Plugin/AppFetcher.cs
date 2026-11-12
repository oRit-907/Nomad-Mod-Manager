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
    /// 1.10.0: gets Check BepInEx from nomaddrivemods.online (not GitHub any more): reads the site's /app answer, downloads
    /// the installer with progress, checks it against the site's SHA-256, installs it quietly for this Windows user
    /// (%LOCALAPPDATA%\Programs\Check BepInEx), registers it for the game and opens it.
    /// Only ever runs when the player presses the button.
    /// </summary>
    internal static class AppFetcher
    {
        public const string Repo = "oRit-907/Nomad-Mod-Manager";   // the in-game browser's download (InGameBrowser): browser-vX.Y.Z releases
        public const string Site = "https://www.nomaddrivemods.online";
        public const string ReleasesPage = Site + "/bepinex";
        private const string Api = Site + "/app";

        public static bool Busy { get; private set; }
        public static float Progress { get; private set; }
        public static string Status { get; private set; } = "";
        public static long Size { get; private set; }

        public static string InstallDir => Path.Combine(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Paths.GameRootPath, "Programs"), "Check BepInEx");

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
                    var f = rel["files"]?["setup"] as JObject;
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
                Finish(done, false, "The website has no Check BepInEx download right now.");
                yield break;
            }

            Size = size;
            var temp = Path.Combine(Path.GetTempPath(), "CheckBepInEx-Setup-" + version + ".exe");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            Status = $"Downloading Check BepInEx {version}…";
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

            // Install quietly for this user, then register and open it.
            Status = $"Installing Check BepInEx {version}…";
            Process setup;
            try { setup = Process.Start(new ProcessStartInfo(temp, "/S") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception e) { Finish(done, false, "Couldn't start the installer: " + e.Message); yield break; }
            var waited = 0f;
            while (setup != null && !setup.HasExited && waited < 180f) { waited += Time.unscaledDeltaTime; yield return null; }
            try { File.Delete(temp); } catch { }
            var target = Path.Combine(InstallDir, "CheckBepInEx.exe");
            if (!File.Exists(target)) { Finish(done, false, "The installer didn't finish."); yield break; }
            try { Downloader.Register(target); } catch { }
            ModManagerPlugin.Log.LogMessage($"Installed Check BepInEx {version} from nomaddrivemods.online to {target}");

            Progress = 1;
            Finish(done, Downloader.Show(), $"Check BepInEx {version} is installed in {InstallDir}.");
        }

        private static void Finish(Action<bool, string> done, bool ok, string message)
        {
            Busy = false;
            Status = message;
            if (!ok) ModManagerPlugin.Log.LogWarning("Getting Check BepInEx: " + message);
            try { done?.Invoke(ok, message); } catch { }
        }
    }
}

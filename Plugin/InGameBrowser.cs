using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    /// <summary>
    /// The browser for Nexus Mods inside the Mods menu: NomadBrowser.exe (Chromium, drawn off-screen) runs next to the game,
    /// sends pictures of the page over a local connection, and gets the mouse and keyboard back. What the player downloads
    /// there is installed by the menu (<see cref="Community.InstallNexusFile"/>), so Nomad Mod Downloader isn't needed.
    /// The browser itself (about 170 MB) is downloaded from this project's GitHub release the first time it's used.
    /// </summary>
    internal static class InGameBrowser
    {
        /// <summary>The browser version this menu uses: GitHub release browser-vX.Y.Z with NomadBrowser-X.Y.Z.zip.</summary>
        public const string BrowserVersion = "1.1.0";
        /// <summary>
        /// The Chromium bundle the browser runs on (CefSharp's version). While it stays the same, an update only fetches
        /// NomadBrowser-app-X.Y.Z.zip (the small browser app), not the whole 170 MB again.
        /// </summary>
        public const string BrowserBase = "cefsharp-152.0.150";
        private const string ReleaseApi = "https://api.github.com/repos/" + AppFetcher.Repo + "/releases/tags/browser-v" + BrowserVersion;

        public static string Dir => Path.Combine(Pending.Dir(Paths.GameRootPath), "browser");
        public static string Exe => Path.Combine(Dir, "NomadBrowser.exe");
        private static string VersionFile => Path.Combine(Dir, "version.txt");
        private static string BaseFile => Path.Combine(Dir, "base.txt");

        /// <summary>Chromium is there already (1.0.0 installs had no base.txt, but the same Chromium).</summary>
        public static bool HasBase
        {
            get
            {
                try
                {
                    if (!File.Exists(Exe) || !File.Exists(Path.Combine(Dir, "libcef.dll"))) return false;
                    if (File.Exists(BaseFile)) return File.ReadAllText(BaseFile).Trim() == BrowserBase;
                    return File.Exists(VersionFile) && File.ReadAllText(VersionFile).Trim() == "1.0.0";
                }
                catch { return false; }
            }
        }
        private static string DataDir => Path.Combine(Pending.Dir(Paths.GameRootPath), "browser-data");

        public static bool Installed
        {
            get { try { return File.Exists(Exe) && File.Exists(VersionFile) && File.ReadAllText(VersionFile).Trim() == BrowserVersion; } catch { return false; } }
        }

        // ------------------------------------------------------------------ getting the browser (once)

        public static bool Fetching { get; private set; }
        public static float FetchProgress { get; private set; }
        public static string FetchStatus { get; private set; } = "";

        /// <summary>Downloads the browser from GitHub, checks it against GitHub's SHA-256 and unpacks it. done(ok, message).</summary>
        public static IEnumerator Fetch(Action<bool, string> done)
        {
            if (Edition.Nexus) { done(false, "The Nexus edition of the Mods menu uses your own web browser."); yield break; }
            if (Fetching) yield break;
            Fetching = true; FetchProgress = 0; FetchStatus = "Finding the browser on GitHub…";
            Close();   // its files are about to be replaced
            var appOnly = HasBase;
            var wanted = (appOnly ? "NomadBrowser-app-" : "NomadBrowser-") + BrowserVersion + ".zip";
            string url = null, sha = null; long size = 0;
            using (var req = UnityWebRequest.Get(ReleaseApi))
            {
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.SetRequestHeader("User-Agent", "NomadModManager/" + ModManagerPlugin.Version);
                req.timeout = 30;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Fetched(done, false, "Couldn't reach GitHub (" + req.error + ")."); yield break; }
                try
                {
                    var asset = (JObject.Parse(req.downloadHandler.text)["assets"] as JArray ?? new JArray()).OfType<JObject>()
                        .FirstOrDefault(a => ((string)a["name"] ?? "").Equals(wanted, StringComparison.OrdinalIgnoreCase));
                    if (asset != null)
                    {
                        url = (string)asset["browser_download_url"];
                        size = (long?)asset["size"] ?? 0;
                        var digest = (string)asset["digest"] ?? "";
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest.Substring(7).ToLowerInvariant();
                    }
                }
                catch { }
            }
            if (url == null || !url.StartsWith("https://github.com/" + AppFetcher.Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase) || sha == null || sha.Length != 64)
            { Fetched(done, false, "The browser isn't on GitHub yet. Try again later."); yield break; }

            var zip = Path.Combine(Pending.Dir(Paths.GameRootPath), "browser-download.zip");
            try { if (File.Exists(zip)) File.Delete(zip); Directory.CreateDirectory(Path.GetDirectoryName(zip)); } catch { }
            FetchStatus = appOnly ? "Updating the browser…" : "Downloading the browser (one time, about " + (size / 1048576) + " MB)…";
            using (var req = new UnityWebRequest(url, "GET"))
            {
                req.downloadHandler = new DownloadHandlerFile(zip, false);
                req.SetRequestHeader("User-Agent", "NomadModManager/" + ModManagerPlugin.Version);
                var op = req.SendWebRequest();
                while (!op.isDone) { FetchProgress = size > 0 ? Mathf.Clamp01(req.downloadedBytes / (float)size) * 0.8f : 0f; yield return null; }
                if (req.result != UnityWebRequest.Result.Success) { TryDelete(zip); Fetched(done, false, "The download failed (" + req.error + ")."); yield break; }
            }

            // Checking and unpacking 400 MB takes a moment: off the game's main thread.
            FetchStatus = "Checking and unpacking the browser…"; FetchProgress = 0.85f;
            string error = null; var finished = false;
            var worker = new Thread(() =>
            {
                try
                {
                    if (Community.Sha256(zip) != sha) throw new InvalidOperationException("The download doesn't match GitHub's checksum. Try again.");
                    if (appOnly)
                    {
                        // Just the browser app: its files go over the old ones, Chromium stays.
                        using (var z = new MiniZip(zip))
                            foreach (var e in z.Entries)
                            {
                                var rel = e.Name.Replace('\\', '/').TrimStart('/');
                                if (rel.Length == 0 || rel.EndsWith("/")) continue;
                                var full = Community.SafeJoin(Dir, rel);
                                Directory.CreateDirectory(Path.GetDirectoryName(full));
                                var part = full + ".part";
                                using (var fs = File.Create(part)) z.Extract(e, fs);
                                if (File.Exists(full)) File.Delete(full);
                                File.Move(part, full);
                            }
                        File.WriteAllText(VersionFile, BrowserVersion);
                        File.WriteAllText(BaseFile, BrowserBase);
                        return;
                    }
                    var temp = Dir + ".new";
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    Directory.CreateDirectory(temp);
                    using (var z = new MiniZip(zip))
                        foreach (var e in z.Entries)
                        {
                            var rel = e.Name.Replace('\\', '/').TrimStart('/');
                            if (rel.Length == 0 || rel.EndsWith("/")) continue;
                            var full = Community.SafeJoin(temp, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            using (var fs = File.Create(full)) z.Extract(e, fs);
                        }
                    File.WriteAllText(Path.Combine(temp, "version.txt"), BrowserVersion);
                    File.WriteAllText(Path.Combine(temp, "base.txt"), BrowserBase);
                    if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
                    Directory.Move(temp, Dir);
                }
                catch (Exception e) { error = e.Message; }
                finally { TryDelete(zip); finished = true; }
            }) { IsBackground = true };
            worker.Start();
            while (!finished) yield return null;
            if (error != null) { Fetched(done, false, "Couldn't set up the browser: " + error); yield break; }
            FetchProgress = 1;
            ModManagerPlugin.Log.LogMessage("Downloaded the in-game browser " + BrowserVersion + " to " + Dir);
            Fetched(done, true, "The browser is ready.");
        }

        private static void Fetched(Action<bool, string> done, bool ok, string msg)
        {
            Fetching = false; FetchStatus = ok ? "" : msg;
            if (!ok) ModManagerPlugin.Log.LogWarning("In-game browser: " + msg);
            try { done?.Invoke(ok, msg); } catch { }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        // ------------------------------------------------------------------ running it

        private static Process _proc;
        private static TcpListener _listener;
        private static TcpClient _client;
        private static NetworkStream _net;
        private static Thread _reader;
        private static string _token = "";
        private static readonly object Lock = new object();
        // The page picture, BGRA, bottom row first (as Unity wants). The browser sends the whole picture once (and after a
        // resize), then only the rows that changed; those are kept as a band (in Unity's rows) until the next Pump.
        private static byte[] _pixels;
        private static int _pw, _ph;
        private static bool _newPicture, _wholePicture;
        private static int _bandLow = int.MaxValue, _bandHigh;
        private static readonly Dictionary<int, Texture2D> Strips = new Dictionary<int, Texture2D>();   // by height (powers of two)
        private static readonly Dictionary<int, byte[]> StripBytes = new Dictionary<int, byte[]>();
        private static bool _noCopyTexture, _checkedCopy;
        private static int _gen;   // which start the reader thread belongs to (an old one's last words are ignored)

        // Starting it again if it stops by itself (not when the menu closes it): at most 3 times in 2 minutes.
        private static bool _wanted;
        private static int _lastW = 1280, _lastH = 720;
        private static readonly List<float> Restarts = new List<float>();
        public static bool Restarting { get; private set; }
        private static readonly List<string> Events = new List<string>();   // (Queue<T> is stripped from the game)

        public static bool Running => _proc != null && !_proc.HasExited;
        public static bool Connected => _net != null;
        public static Texture2D Texture { get; private set; }
        public static string Url = "", Title = "";
        public static bool Loading, CanBack, CanForward;
        /// <summary>What Nexus's page shows: "free" (Slow download), "premium" (Start download), "signedout", or "".</summary>
        public static string Account = "";

        /// <summary>Starts the browser on a page (or, if it's running, goes there).</summary>
        public static bool Open(string url, int width, int height, out string error)
        {
            Restarts.Clear();
            return Start(url, width, height, out error);
        }

        private static bool Start(string url, int width, int height, out string error)
        {
            error = null;
            _wanted = true; _lastW = width; _lastH = height;
            if (Running && Connected) { Send("nav " + url); return true; }
            Stop();
            if (!Installed) { error = "The browser isn't downloaded yet."; return false; }
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var port = ((IPEndPoint)_listener.Server.LocalEndPoint).Port;   // (the game's TcpListener has no LocalEndpoint or Pending: its Socket does)
                var rnd = new System.Security.Cryptography.RNGCryptoServiceProvider();
                var bytes = new byte[16]; rnd.GetBytes(bytes);
                _token = string.Concat(bytes.Select(b => b.ToString("x2")).ToArray());
                Directory.CreateDirectory(DataDir);
                int pid; using (var me = Process.GetCurrentProcess()) pid = me.Id;
                var args = $"--port {port} --token {_token} --data \"{DataDir}\" --url \"{url.Replace("\"", "%22")}\" --size {width} {height} --parent {pid}";
                _proc = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir });
                Url = url; Title = ""; Loading = true;
                int gen; lock (Lock) gen = ++_gen;
                _reader = new Thread(() => Read(gen)) { IsBackground = true, Name = "NomadBrowser" };
                _reader.Start();
                return true;
            }
            catch (Exception e) { error = e.Message; Stop(); return false; }
        }

        /// <summary>Closes the browser (the menu closed it, or the game is quitting).</summary>
        public static void Close()
        {
            _wanted = false; Restarting = false;
            Stop();
        }

        private static void Stop()
        {
            try { Send("quit"); } catch { }
            try { _net?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            var p = _proc;
            _net = null; _client = null; _listener = null; _proc = null;
            if (p != null) new Thread(() => { try { if (!p.WaitForExit(3000)) p.Kill(); } catch { } }) { IsBackground = true }.Start();
            lock (Lock) { _gen++; _pixels = null; _newPicture = false; _wholePicture = false; _bandLow = int.MaxValue; _bandHigh = 0; Events.Clear(); }
        }

        /// <summary>The page's size in pixels (kept, so a restart opens at the same size).</summary>
        public static void Resize(int width, int height)
        {
            _lastW = width; _lastH = height;
            Send($"size {width} {height}");
        }

        public static void Send(string line)
        {
            var net = _net;
            if (net == null) return;
            var b = Encoding.UTF8.GetBytes(line + "\n");
            try { lock (net) net.Write(b, 0, b.Length); } catch { }
        }

        private static void Read(int gen)
        {
            try
            {
                var listener = _listener;
                // Wait (up to 30 s) for the browser to connect, and check it's ours.
                var started = DateTime.UtcNow;
                while (!listener.Server.Poll(50000, SelectMode.SelectRead)) { if ((DateTime.UtcNow - started).TotalSeconds > 30 || _proc == null || _proc.HasExited) throw new IOException("The browser didn't start."); }
                var client = listener.AcceptTcpClient();
                listener.Stop();
                client.NoDelay = true; client.Client.ReceiveBufferSize = 8 * 1024 * 1024;
                var net = client.GetStream();
                var hello = ReadLine(net);
                if (hello != "hello " + _token) { client.Close(); throw new IOException("Something else connected to the browser's port."); }
                lock (Lock) { if (gen != _gen) { client.Close(); return; } _client = client; _net = net; }
                var head = new byte[5];
                while (true)
                {
                    ReadAll(net, head, 5);
                    var len = BitConverter.ToInt32(head, 1);
                    if (len < 0 || len > 64 * 1024 * 1024) throw new IOException("Bad message from the browser.");
                    var data = new byte[len];
                    ReadAll(net, data, len);
                    if (head[0] == 1 && len >= 8)
                    {
                        var w = BitConverter.ToInt32(data, 0); var h = BitConverter.ToInt32(data, 4);
                        if (w <= 0 || h <= 0 || (long)w * h * 4 + 8 != len) continue;
                        // The browser's rows come top first; Unity's textures start at the bottom row (flipped here, off the main thread).
                        var px = new byte[w * h * 4];
                        var row = w * 4;
                        for (var y = 0; y < h; y++) Buffer.BlockCopy(data, 8 + y * row, px, (h - 1 - y) * row, row);
                        lock (Lock) { if (gen != _gen) return; _pixels = px; _pw = w; _ph = h; _newPicture = true; _wholePicture = true; }
                    }
                    else if (head[0] == 3 && len >= 16)
                    {
                        // Just the rows that changed: into the picture, and the band to upload grows to cover them.
                        int w = BitConverter.ToInt32(data, 0), h = BitConverter.ToInt32(data, 4), top = BitConverter.ToInt32(data, 8), rows = BitConverter.ToInt32(data, 12);
                        var row = w * 4;
                        if (w <= 0 || h <= 0 || top < 0 || rows <= 0 || top + rows > h || (long)rows * row + 16 != len) continue;
                        lock (Lock)
                        {
                            if (gen != _gen) return;
                            if (_pixels == null || _pw != w || _ph != h) continue;   // a resize is on its way as a whole picture
                            for (var y = 0; y < rows; y++) Buffer.BlockCopy(data, 16 + y * row, _pixels, (h - 1 - (top + y)) * row, row);
                            _bandLow = Math.Min(_bandLow, h - (top + rows));
                            _bandHigh = Math.Max(_bandHigh, h - top);
                            _newPicture = true;
                        }
                    }
                    else if (head[0] == 2) lock (Lock) { if (gen != _gen) return; Events.Add(Encoding.UTF8.GetString(data)); }
                }
            }
            catch (Exception e)
            {
                lock (Lock) if (gen == _gen) Events.Add("closed " + e.Message);
            }
        }

        private static string ReadLine(NetworkStream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 200) { if (s.Read(one, 0, 1) <= 0) break; if (one[0] == (byte)'\n') break; sb.Append((char)one[0]); }
            return sb.ToString();
        }

        private static void ReadAll(NetworkStream s, byte[] buf, int count)
        {
            var got = 0;
            while (got < count) { var n = s.Read(buf, got, count - got); if (n <= 0) throw new IOException("The browser closed."); got += n; }
        }

        /// <summary>Call every frame while the browser shows: updates the picture and returns the events that came in.</summary>
        public static List<string> Pump()
        {
            var events = new List<string>();
            lock (Lock)
            {
                if (_newPicture) Upload();
                events.AddRange(Events); Events.Clear();
            }
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                // It stopped by itself (crashed, or Windows closed it): start it again on the same page, a few times.
                if (e.StartsWith("closed ") && _wanted)
                {
                    var now = Time.unscaledTime;
                    Restarts.RemoveAll(t => now - t > 120f);
                    if (Restarts.Count < 3)
                    {
                        Restarts.Add(now);
                        ModManagerPlugin.Log.LogWarning("In-game browser stopped (" + e.Substring(7) + "); starting it again on " + Url);
                        Restarting = true;
                        var url = Url.Length > 0 ? Url : "https://www.nexusmods.com/games/" + Nexus.GameDomain + "/mods";
                        if (Start(url, _lastW, _lastH, out var err)) { events.RemoveAt(i--); continue; }
                        e = events[i] = "closed " + err;
                    }
                    Restarting = false;
                }
                if (e == "ready") Restarting = false;
            }
            foreach (var e in events)
            {
                if (e.StartsWith("url ")) Url = e.Substring(4);
                else if (e.StartsWith("title ")) Title = e.Substring(6);
                else if (e.StartsWith("state "))
                {
                    var p = e.Split(' ');
                    Loading = p.Length > 1 && p[1] == "1"; CanBack = p.Length > 2 && p[2] == "1"; CanForward = p.Length > 3 && p[3] == "1";
                }
                else if (e.StartsWith("log ")) ModManagerPlugin.Log.LogInfo("Browser: " + e.Substring(4));
                else if (e.StartsWith("account ")) Account = e.Substring(8).Trim();
            }
            return events;
        }

        /// <summary>
        /// The new picture onto the texture (under Lock). A band of changed rows goes up on its own, through a small strip
        /// texture copied into place on the graphics card; a whole picture, or a band of most of the page, is uploaded whole.
        /// </summary>
        private static void Upload()
        {
            int w = _pw, h = _ph, row = w * 4;
            if (_pixels == null || w <= 0 || h <= 0) return;
            if (!_checkedCopy)
            {
                _checkedCopy = true;
                try { if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0) _noCopyTexture = true; } catch { _noCopyTexture = true; }
            }
            var whole = _wholePicture || Texture == null || Texture.width != w || Texture.height != h || _noCopyTexture;
            var low = Math.Max(0, _bandLow); var rows = Math.Min(h, _bandHigh) - low;
            if (!whole && rows <= 0) { _newPicture = false; return; }
            if (!whole && rows > h * 6 / 10) whole = true;
            if (!whole)
            {
                var stripH = 16;
                while (stripH < rows) stripH *= 2;
                if (stripH > h) stripH = h;
                try
                {
                    if (!Strips.TryGetValue(stripH, out var strip) || strip == null || strip.width != w)
                    {
                        if (strip != null) UnityEngine.Object.Destroy(strip);
                        strip = Strips[stripH] = new Texture2D(w, stripH, TextureFormat.BGRA32, false);
                        StripBytes[stripH] = new byte[row * stripH];
                    }
                    var bytes = StripBytes[stripH];
                    Buffer.BlockCopy(_pixels, low * row, bytes, 0, rows * row);
                    strip.LoadRawTextureData(bytes);
                    strip.Apply(false);
                    Graphics.CopyTexture(strip, 0, 0, 0, 0, w, rows, Texture, 0, 0, 0, low);
                }
                catch (Exception e)
                {
                    // (a graphics card or game build without texture copies: whole pictures from now on)
                    _noCopyTexture = true; whole = true;
                    ModManagerPlugin.Log.LogInfo("In-game browser: uploading whole pictures (" + e.Message + ")");
                }
            }
            if (whole)
            {
                if (Texture == null || Texture.width != w || Texture.height != h)
                {
                    if (Texture != null) UnityEngine.Object.Destroy(Texture);
                    Texture = new Texture2D(w, h, TextureFormat.BGRA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    foreach (var t in Strips.Values) if (t != null) UnityEngine.Object.Destroy(t);
                    Strips.Clear(); StripBytes.Clear();
                }
                Texture.LoadRawTextureData(_pixels);
                Texture.Apply(false);
            }
            _newPicture = false; _wholePicture = false; _bandLow = int.MaxValue; _bandHigh = 0;
        }

        /// <summary>The Nexus mod id in a Nexus address (…/nomaddrive/mods/123…), or 0.</summary>
        public static int ModIdOf(string url)
        {
            var u = (url ?? "").ToLowerInvariant();
            if (u.IndexOf("nexusmods.com/", StringComparison.Ordinal) < 0) return 0;
            var key = "/" + Nexus.GameDomain + "/mods/";   // nexusmods.com/nomaddrive/mods/12 or nexusmods.com/games/nomaddrive/mods/12
            var i = u.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            var n = 0;
            while (i < u.Length && u[i] >= '0' && u[i] <= '9' && n < 100000000) n = n * 10 + (u[i++] - '0');
            return n;
        }

        /// <summary>The file_id in a Nexus address (…?tab=files&amp;file_id=456), or 0.</summary>
        public static int FileIdOf(string url)
        {
            var u = (url ?? "").ToLowerInvariant();
            var i = u.IndexOf("file_id=", StringComparison.Ordinal);
            if (i < 0) return 0;
            i += "file_id=".Length;
            var n = 0;
            while (i < u.Length && u[i] >= '0' && u[i] <= '9' && n < 100000000) n = n * 10 + (u[i++] - '0');
            return n;
        }
    }
}

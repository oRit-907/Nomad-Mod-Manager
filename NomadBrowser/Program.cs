using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;
using SharpCompress.Archives;

namespace NomadBrowser
{
    /// <summary>
    /// The Mods menu's in-game browser. Started by the menu with:
    ///   NomadBrowser.exe --port N --token T --data DIR --url URL --size W H [--parent PID]
    /// It connects to the menu on 127.0.0.1:N, says the token, then:
    ///   to the menu:   [1][int len][int w][int h][w*h*4 bytes BGRA]               the whole page picture (first one, and after a resize)
    ///                  [3][int len][int w][int h][int y][int rows][rows*w*4 bytes] just the rows that changed (full width)
    ///                  [2][int len][UTF-8 line]                                    events: url, title, state, dl-start, dl-progress,
    ///                                                                              dl-done, dl-error, log, account, blocked, ctx
    ///   from the menu: UTF-8 lines: nav, size, move, leave, down, up, wheel, key, char, focus, back, fwd, reload, stop,
    ///                  copy, cut, paste, selectall, undo, redo, copylink, quit
    /// Pages stay on Nexus Mods and Nomad Drive Mods (and the sign-in pages they use): a link anywhere else isn't opened
    /// here, the menu is told ("blocked URL") and offers to open it in the player's own web browser.
    ///   NomadBrowser.exe --smoke-test  starts Chromium, draws a test page and exits 0 if it painted (used by the release build).
    /// Downloads go to DIR\downloads. A finished one that's a .7z/.rar/… is repacked into a plain .zip (the game can only
    /// unpack .zip); programs (.exe, .msi, …) are left out. The menu installs the file; this process never touches the game.
    /// It closes when the menu disconnects (the game quit or the menu closed the browser).
    /// </summary>
    internal static class Program
    {
        private static TcpClient _tcp;
        private static NetworkStream _net;
        private static readonly object SendLock = new object();
        private static ChromiumWebBrowser _browser;
        private static string _data = "", _downloads = "";
        private static volatile bool _quit;

        // The newest picture of the page, sent by the frame thread at most 30 times a second.
        private static readonly object FrameLock = new object();
        private static byte[] _frame = new byte[0];
        private static int _fw, _fh;
        private static bool _frameDirty;
        private static int _dirtyTop = int.MaxValue, _dirtyBottom;   // the rows that changed since the last send
        private static bool _sizeChanged = true;                       // the next send is a whole picture
        private static string _ctxLink = "";                           // the link the player right-clicked last

        [STAThread]
        private static int Main(string[] args)
        {
            string Arg(string name, string def = "") { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
            if (Array.IndexOf(args, "--smoke-test") >= 0) return SmokeTest();
            int.TryParse(Arg("--port"), out var port);
            var token = Arg("--token");
            _data = Arg("--data", Path.Combine(Path.GetTempPath(), "NomadBrowser"));
            var url = Arg("--url", "https://www.nexusmods.com/nomaddrive");
            var si = Array.IndexOf(args, "--size");
            int w = 1280, h = 720;
            if (si >= 0 && si + 2 < args.Length) { int.TryParse(args[si + 1], out w); int.TryParse(args[si + 2], out h); }
            w = Math.Max(320, Math.Min(3840, w)); h = Math.Max(240, Math.Min(2160, h));
            if (port <= 0 || token.Length < 16) return 2;

            Directory.CreateDirectory(_data);
            _downloads = Path.Combine(_data, "downloads");
            Directory.CreateDirectory(_downloads);

            try
            {
                _tcp = new TcpClient { NoDelay = true, SendBufferSize = 8 * 1024 * 1024 };
                _tcp.Connect("127.0.0.1", port);
                _net = _tcp.GetStream();
                var hello = Encoding.UTF8.GetBytes("hello " + token + "\n");
                _net.Write(hello, 0, hello.Length);
            }
            catch { return 3; }
            if (int.TryParse(Arg("--parent"), out var parent) && parent > 0) WatchParent(parent);

            var settings = NewSettings();
            if (!Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null)) { Event("error Couldn't start the browser."); return 4; }

            _browser = new ChromiumWebBrowser(url, new BrowserSettings { WindowlessFrameRate = 30 }) { Size = new System.Drawing.Size(w, h) };
            _browser.DownloadHandler = new Downloads();
            _browser.LifeSpanHandler = new Popups();
            _browser.JsDialogHandler = new Dialogs();
            _browser.RequestHandler = new OnlyNexus();
            _browser.MenuHandler = new ContextMenu();
            _browser.Paint += OnPaint;
            _browser.AddressChanged += (s, e) => Event("url " + e.Address);
            _browser.TitleChanged += (s, e) => Event("title " + e.Title);
            _browser.LoadingStateChanged += (s, e) =>
            {
                Event($"state {(e.IsLoading ? 1 : 0)} {(e.CanGoBack ? 1 : 0)} {(e.CanGoForward ? 1 : 0)}");
                if (!e.IsLoading) WatchAccount();
            };
            _browser.LoadError += (s, e) => { if (e.ErrorCode != CefErrorCode.Aborted && e.Frame.IsMain) Event("log Couldn't open " + e.FailedUrl + " (" + e.ErrorText + ")"); };

            var sender = new Thread(SendFrames) { IsBackground = true, Name = "frames" };
            sender.Start();
            Event("ready");
            try { ReadCommands(); } catch { }
            _quit = true;
            try { _browser.Dispose(); } catch { }
            try { Cef.Shutdown(); } catch { }
            return 0;
        }

        private static CefSettings NewSettings()
        {
            var settings = new CefSettings
            {
                RootCachePath = Path.Combine(_data, "cef"),
                CachePath = Path.Combine(_data, "cef", "profile"),   // keeps the player signed in to Nexus between games
                PersistSessionCookies = true,
                LogSeverity = LogSeverity.Disable,
                Locale = "en-US",   // only en-US.pak ships (the other languages would add 50 MB)
                WindowlessRenderingEnabled = true,   // CEF's helper processes run CefSharp.BrowserSubprocess.exe, next to this one
            };
            settings.SetOffScreenRenderingBestPerformanceArgs();   // software drawing, no GPU process work (so no GPU libraries ship)
            settings.CefCommandLineArgs["autoplay-policy"] = "user-gesture-required";
            settings.CefCommandLineArgs["mute-audio"] = "1";
            return settings;
        }

        /// <summary>If the game goes away without closing the connection (it crashed or was killed), this closes too.</summary>
        private static void WatchParent(int pid)
        {
            System.Diagnostics.Process game;
            try { game = System.Diagnostics.Process.GetProcessById(pid); } catch { return; }
            new Thread(() =>
            {
                try { game.WaitForExit(); } catch { return; }
                _quit = true;
                try { _net?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
            }) { IsBackground = true, Name = "parent" }.Start();
        }

        /// <summary>
        /// For the release build: starts Chromium the way the game does, draws a red test page and checks the picture.
        /// 0 = it painted, 5 = nothing painted in a minute, 4 = Chromium didn't start.
        /// </summary>
        private static int SmokeTest()
        {
            _data = Path.Combine(Path.GetTempPath(), "NomadBrowser-smoke-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_data);
            if (!Cef.Initialize(NewSettings(), performDependencyCheck: false, browserProcessHandler: null)) { Console.Error.WriteLine("Cef.Initialize failed"); return 4; }
            var painted = new ManualResetEventSlim(false);
            var page = "data:text/html,<html><body style='margin:0;background:%23ff0000'><h1>NomadBrowser</h1></body></html>";
            var b = new ChromiumWebBrowser(page, new BrowserSettings { WindowlessFrameRate = 30 }) { Size = new System.Drawing.Size(640, 480) };
            b.Paint += (s, e) =>
            {
                if (e.IsPopup || e.Width < 100 || e.Height < 100) return;
                var px = new byte[4];
                Marshal.Copy(e.BufferHandle + ((e.Height - 10) * e.Width + e.Width / 2) * 4, px, 0, 4);   // near the bottom: the red background
                if (px[2] > 200 && px[1] < 60 && px[0] < 60) painted.Set();
            };
            var ok = painted.Wait(TimeSpan.FromSeconds(60));
            Console.WriteLine(ok ? "painted" : "nothing painted");
            try { b.Dispose(); } catch { }
            try { Cef.Shutdown(); } catch { }
            try { Directory.Delete(_data, true); } catch { }
            return ok ? 0 : 5;
        }

        // ------------------------------------------------------------------ which download button Nexus shows

        /// <summary>
        /// Reads Nexus's page (only the text of its buttons and links) to tell the menu which button to point at:
        /// "free" (Slow download), "premium" (Start download / Fast download) or "signedout" (a Log in link and no
        /// download button). Nexus draws the page after it loads, so it looks a few times. Nothing is clicked.
        /// </summary>
        private const string AccountScript = @"(function(){
  var t=[].slice.call(document.querySelectorAll('button,a,[role=button]')).map(function(e){return (e.innerText||e.textContent||'').replace(/\s+/g,' ').trim().toLowerCase();});
  function has(s){for(var i=0;i<t.length;i++){if(t[i]===s||t[i].indexOf(s)===0)return true;}return false;}
  if(has('slow download'))return 'free';
  if(has('start download')||has('fast download'))return 'premium';
  if(has('log in')||has('login')||has('sign in'))return 'signedout';
  return '';
})()";

        private static int _watch;
        private static string _account = "";

        private static void WatchAccount()
        {
            var me = Interlocked.Increment(ref _watch);
            new Thread(() =>
            {
                for (var i = 0; i < 10 && !_quit && me == _watch; i++)
                {
                    Thread.Sleep(i == 0 ? 400 : 1200);
                    try
                    {
                        if (!_browser.CanExecuteJavascriptInMainFrame) continue;
                        var r = _browser.EvaluateScriptAsync(AccountScript, TimeSpan.FromSeconds(3)).Result;
                        var state = r.Success ? (r.Result as string ?? "") : "";
                        if (state.Length == 0) continue;
                        if (state != _account) { _account = state; Event("account " + state); }
                        if (state != "signedout") return;   // a download button was found
                    }
                    catch { }
                }
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------------ to the menu

        private static void Send(byte type, byte[] payload, int count)
        {
            lock (SendLock)
            {
                try
                {
                    var head = new byte[5];
                    head[0] = type;
                    BitConverter.GetBytes(count).CopyTo(head, 1);
                    _net.Write(head, 0, 5);
                    _net.Write(payload, 0, count);
                }
                catch { _quit = true; }
            }
        }

        private static void Event(string line)
        {
            var b = Encoding.UTF8.GetBytes(line.Replace('\n', ' ').Replace('\r', ' '));
            Send(2, b, b.Length);
        }

        /// <summary>Keeps the whole picture, and which rows changed (Chromium says what it redrew: often just a line or two).</summary>
        private static void OnPaint(object sender, OnPaintEventArgs e)
        {
            if (e.IsPopup) return;
            var row = e.Width * 4;
            var n = row * e.Height;
            lock (FrameLock)
            {
                var whole = _frame.Length != n || _fw != e.Width || _fh != e.Height;
                if (whole) { _frame = new byte[n]; _fw = e.Width; _fh = e.Height; _sizeChanged = true; }
                var top = whole ? 0 : Math.Max(0, e.DirtyRect.Y);
                var bottom = whole ? e.Height : Math.Min(e.Height, e.DirtyRect.Y + e.DirtyRect.Height);
                if (bottom <= top) return;
                Marshal.Copy(e.BufferHandle + top * row, _frame, top * row, (bottom - top) * row);
                _dirtyTop = Math.Min(_dirtyTop, top);
                _dirtyBottom = Math.Max(_dirtyBottom, bottom);
                _frameDirty = true;
            }
        }

        /// <summary>
        /// At most 30 times a second: the rows that changed since last time (type 3), or the whole picture after a resize
        /// (type 1). A still page sends nothing at all.
        /// </summary>
        private static void SendFrames()
        {
            byte[] copy = new byte[0];
            while (!_quit)
            {
                var have = false; byte type = 3; var count = 0;
                lock (FrameLock)
                {
                    if (_frameDirty && _fw > 0)
                    {
                        var row = _fw * 4;
                        int top = _sizeChanged ? 0 : _dirtyTop, rows = (_sizeChanged ? _fh : _dirtyBottom) - top;
                        type = _sizeChanged ? (byte)1 : (byte)3;
                        var head = type == 1 ? 8 : 16;
                        count = head + rows * row;
                        if (copy.Length < count) copy = new byte[count];
                        BitConverter.GetBytes(_fw).CopyTo(copy, 0);
                        BitConverter.GetBytes(_fh).CopyTo(copy, 4);
                        if (type == 3) { BitConverter.GetBytes(top).CopyTo(copy, 8); BitConverter.GetBytes(rows).CopyTo(copy, 12); }
                        Buffer.BlockCopy(_frame, top * row, copy, head, rows * row);
                        _frameDirty = false; _sizeChanged = false; _dirtyTop = int.MaxValue; _dirtyBottom = 0;
                        have = true;
                    }
                }
                if (have) Send(type, copy, count);
                Thread.Sleep(33);
            }
        }

        // ------------------------------------------------------------------ from the menu

        private static void ReadCommands()
        {
            var buf = new byte[4096];
            var line = new List<byte>();
            while (!_quit)
            {
                var n = _net.Read(buf, 0, buf.Length);
                if (n <= 0) return;   // the menu went away
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\n') { line.Add(buf[i]); continue; }
                    var cmd = Encoding.UTF8.GetString(line.ToArray());
                    line.Clear();
                    try { Handle(cmd); } catch (Exception ex) { Event("log " + ex.Message); }
                    if (_quit) return;
                }
            }
        }

        private static void Handle(string cmd)
        {
            var p = cmd.Split(' ');
            int I(int i) => i < p.Length && int.TryParse(p[i], out var v) ? v : 0;
            var host = _browser.IsBrowserInitialized ? _browser.GetBrowserHost() : null;
            var mods = (CefEventFlags)I(p.Length - 1);
            switch (p[0])
            {
                case "nav": _browser.Load(cmd.Substring(4)); break;
                case "size":
                    _browser.Size = new System.Drawing.Size(Math.Max(320, Math.Min(3840, I(1))), Math.Max(240, Math.Min(2160, I(2))));
                    break;
                case "move": host?.SendMouseMoveEvent(I(1), I(2), false, mods); break;
                case "leave": host?.SendMouseMoveEvent(I(1), I(2), true, mods); break;
                case "down":
                case "up":
                    host?.SendMouseClickEvent(I(1), I(2), I(3) == 1 ? MouseButtonType.Right : I(3) == 2 ? MouseButtonType.Middle : MouseButtonType.Left, p[0] == "up", Math.Max(1, I(4)), mods);
                    break;
                case "wheel": host?.SendMouseWheelEvent(I(1), I(2), I(3), I(4), mods); break;
                case "key":
                    // key down|up <windows key code> <modifiers>
                    host?.SendKeyEvent(new KeyEvent { Type = p[1] == "up" ? KeyEventType.KeyUp : KeyEventType.RawKeyDown, WindowsKeyCode = I(2), NativeKeyCode = 0, Modifiers = mods });
                    break;
                case "char": host?.SendKeyEvent(new KeyEvent { Type = KeyEventType.Char, WindowsKeyCode = I(1), Modifiers = mods }); break;
                case "focus": host?.SetFocus(I(1) == 1); break;
                case "back": if (_browser.CanGoBack) _browser.Back(); break;
                case "fwd": if (_browser.CanGoForward) _browser.Forward(); break;
                case "reload": _browser.Reload(); break;
                case "stop": _browser.Stop(); break;
                // Copy and paste use Windows' clipboard, like any browser.
                case "copy": Focused()?.Copy(); break;
                case "cut": Focused()?.Cut(); break;
                case "paste": Focused()?.Paste(); break;
                case "selectall": Focused()?.SelectAll(); break;
                case "undo": Focused()?.Undo(); break;
                case "redo": Focused()?.Redo(); break;
                case "copylink":
                    if (_ctxLink.Length > 0) { try { System.Windows.Forms.Clipboard.SetText(_ctxLink); } catch (Exception ex) { Event("log Couldn't copy the link: " + ex.Message); } }
                    break;
                case "quit": _quit = true; break;
            }
        }

        private static IFrame Focused()
        {
            try { var b = _browser.GetBrowser(); return b?.FocusedFrame ?? b?.MainFrame; } catch { return null; }
        }

        // ------------------------------------------------------------------ downloads

        private static readonly HashSet<string> Programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".jar" };
        private static readonly HashSet<string> Archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

        private sealed class Downloads : IDownloadHandler
        {
            private readonly Dictionary<int, string> _page = new Dictionary<int, string>();   // download id -> the page it came from

            public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) => true;

            public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem item, IBeforeDownloadCallback callback)
            {
                var name = SafeName(string.IsNullOrEmpty(item.SuggestedFileName) ? "download" : item.SuggestedFileName);
                var ext = Path.GetExtension(name);
                if (Programs.Contains(ext)) { Event("dl-error " + name + " is a program. The Mods menu only installs mods, never programs."); return true; }
                if (!Archives.Contains(ext) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) { Event("dl-error " + name + " isn't a mod file (.zip, .7z, .rar or .dll)."); return true; }
                var path = Path.Combine(_downloads, DateTime.Now.ToString("HHmmss") + "-" + name);
                lock (_page) _page[item.Id] = chromiumWebBrowser.Address ?? "";
                Event("dl-start " + name);
                if (!callback.IsDisposed) using (callback) callback.Continue(path, showDialog: false);
                return true;
            }

            public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem item, IDownloadItemCallback callback)
            {
                if (item.IsInProgress) { Event($"dl-progress {item.PercentComplete} {item.ReceivedBytes} {item.TotalBytes}"); return; }
                if (item.IsCancelled) { Event("dl-error The download was cancelled."); return; }
                if (!item.IsComplete || string.IsNullOrEmpty(item.FullPath)) return;
                string page;
                lock (_page) { _page.TryGetValue(item.Id, out page); _page.Remove(item.Id); }
                var full = item.FullPath;
                var original = Path.GetFileName(full).Substring(Math.Min(7, Path.GetFileName(full).Length));
                new Thread(() =>
                {
                    try
                    {
                        Event("dl-progress 100 " + item.ReceivedBytes + " " + item.TotalBytes);
                        var ready = Prepare(full);
                        Event("dl-done " + ready + "\t" + original + "\t" + (page ?? "") + "\t" + (item.Url ?? ""));
                    }
                    catch (Exception ex) { Event("dl-error Couldn't open " + original + ": " + ex.Message); }
                }) { IsBackground = true }.Start();
            }
        }

        /// <summary>A .zip or .dll as it is; anything else unpacked and packed again as a plain .zip (programs left out).</summary>
        private static string Prepare(string file)
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return file;
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) && IsPlainZip(file)) return file;
            var zip = Path.ChangeExtension(file, null) + ".repacked.zip";
            if (File.Exists(zip)) File.Delete(zip);
            using (var archive = ArchiveFactory.OpenArchive(file, new SharpCompress.Readers.ReaderOptions()))
            using (var output = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                long total = 0;
                foreach (var e in archive.Entries.Where(x => !x.IsDirectory && !string.IsNullOrEmpty(x.Key)))
                {
                    var key = e.Key.Replace('\\', '/').TrimStart('/');
                    if (key.Split('/').Any(s => s == "..") || Programs.Contains(Path.GetExtension(key))) continue;
                    total += e.Size;
                    if (total > 3L * 1024 * 1024 * 1024) throw new InvalidDataException("it unpacks to more than 3 GB");
                    var entry = output.CreateEntry(key, CompressionLevel.Fastest);
                    using (var src = e.OpenEntryStream())
                    using (var dst = entry.Open()) src.CopyTo(dst);
                }
            }
            try { File.Delete(file); } catch { }
            return zip;
        }

        /// <summary>A .zip the game's own unpacker reads: stored or deflated entries, no zip64.</summary>
        private static bool IsPlainZip(string file)
        {
            try
            {
                if (new FileInfo(file).Length >= uint.MaxValue) return false;
                using (var z = ZipFile.OpenRead(file))
                    foreach (var e in z.Entries)
                        if (e.Length >= uint.MaxValue || e.CompressedLength >= uint.MaxValue) return false;
                using (var a = ArchiveFactory.OpenArchive(file, new SharpCompress.Readers.ReaderOptions()))
                    foreach (var e in a.Entries)
                        if (e.CompressionType != SharpCompress.Common.CompressionType.Deflate && e.CompressionType != SharpCompress.Common.CompressionType.None) return false;
                return true;
            }
            catch { return false; }
        }

        private static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 120 ? name.Substring(name.Length - 120) : name;
        }

        // ------------------------------------------------------------------ popups and dialogs

        /// <summary>Links that open a new window (sign-in with Google, Discord…) open in the same view instead.</summary>
        private sealed class Popups : CefSharp.Handler.LifeSpanHandler
        {
            protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName,
                WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
                ref bool noJavascriptAccess, out IWebBrowser newBrowser)
            {
                newBrowser = null;
                if (string.IsNullOrEmpty(targetUrl)) return true;
                if (IsAllowed(targetUrl)) chromiumWebBrowser.Load(targetUrl);
                else Event("blocked " + targetUrl);
                return true;
            }
        }

        // ------------------------------------------------------------------ where pages may go

        /// <summary>
        /// The places pages may open: Nexus Mods (and its file servers, for downloads), Nomad Drive Mods, and the sign-in
        /// pages Nexus and the site use. Only whole pages are checked; what a page loads inside itself isn't.
        /// </summary>
        private static readonly string[] Allowed =
        {
            "nexusmods.com", "nexus-cdn.com", "nomaddrivemods.online",
            // signing in with Google, Discord, Steam or Apple (Google passes through YouTube's sign-in page to set its cookies)
            "accounts.google.com", "accounts.youtube.com", "discord.com", "discordapp.com", "steamcommunity.com", "login.steampowered.com",
            "appleid.apple.com", "challenges.cloudflare.com",
        };

        internal static bool IsAllowed(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return false;
            var host = u.Host.ToLowerInvariant().TrimEnd('.');
            return Allowed.Any(a => host == a || host.EndsWith("." + a, StringComparison.Ordinal));
        }

        private sealed class OnlyNexus : CefSharp.Handler.RequestHandler
        {
            protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect)
            {
                if (!frame.IsMain || IsAllowed(request.Url)) return false;
                Event("blocked " + request.Url);
                return true;   // not opened here: the menu offers the player's own web browser
            }

            protected override bool OnOpenUrlFromTab(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, WindowOpenDisposition targetDisposition, bool userGesture)
            {
                if (IsAllowed(targetUrl)) { chromiumWebBrowser.Load(targetUrl); return true; }
                Event("blocked " + targetUrl);
                return true;
            }
        }

        // ------------------------------------------------------------------ right-click menu

        /// <summary>Chromium's own right-click menu can't show off-screen: the menu draws its own (Copy, Paste, Open link…).</summary>
        private sealed class ContextMenu : CefSharp.Handler.ContextMenuHandler
        {
            protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
            {
                model.Clear();
                var link = parameters.LinkUrl ?? "";
                if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) link = "";
                _ctxLink = link;
                var sel = string.IsNullOrEmpty(parameters.SelectionText) ? 0 : 1;
                Event($"ctx {parameters.XCoord} {parameters.YCoord} {(parameters.IsEditable ? 1 : 0)} {sel}\t{link}");
            }
        }

        /// <summary>Page alerts and confirms can't be shown off-screen: they're answered "OK" and logged.</summary>
        private sealed class Dialogs : CefSharp.Handler.JsDialogHandler
        {
            protected override bool OnJSDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType, string messageText,
                string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
            {
                Event("log " + messageText);
                callback.Continue(true);
                return true;
            }
        }
    }
}

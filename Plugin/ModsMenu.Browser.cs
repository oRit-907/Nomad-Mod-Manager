using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

#if !NEXUS_EDITION
namespace NomadModManager
{
    /// <summary>
    /// Nexus installs inside the game: the Nexus page of each mod opens in the menu's own browser (<see cref="InGameBrowser"/>);
    /// the player presses Nexus's download button (Slow download, or Fast download for Premium members), and the menu
    /// installs what they downloaded, like Nomad Mod Downloader does. Several mods open one after another.
    /// </summary>
    internal sealed partial class ModsMenu
    {
        private bool _browserOpen;
        private readonly List<(int ModId, int FileId)> _browserQueue = new List<(int, int)>();
        private (int ModId, int FileId) _browserNow;
        private string _browserStatus = "";
        private bool _browserStatusGood = true;
        private bool _browserInstalling;
        private int _browserDone, _browserTotal;
        private Rect _browserRect;
        private Vector2 _lastMouse = new Vector2(-1, -1);
        private int _sentW, _sentH;
        private float _sizeAt;
        private float _lastClickAt; private Vector2 _lastClickPos; private int _clickCount;
        private string _browserError = "";
        private bool _browserPrompt;          // showing "press the download button" (worded for the player's Nexus account)
        private bool _browserAutoUpdate;      // fetching a newer browser app by itself (Chromium already there)
        private float _browserOpenedAt;

        // The right-click menu (Chromium can't draw its own off-screen): where, and what it offers.
        private bool _ctxOpen, _ctxEditable, _ctxSelection;
        private Vector2 _ctxAt;               // in page pixels
        private string _ctxLink = "";
        private Rect _ctxRect;
        // A link that leaves Nexus Mods / Nomad Drive Mods: not opened in here, offered for the player's own browser.
        private string _blockedUrl = "";
        private Rect _blockedRect;

        // The controller: a cursor moved with the left stick.
        private bool _padCursorOn, _padClick, _padADown;
        private Vector2 _padCursor = new Vector2(-1, -1);
        private Vector2 _padWheel;
        private Rect _browserPanel;
        private int _padArrow = -1; private float _padArrowNext;
        private readonly Dictionary<int, float> _keyRepeat = new Dictionary<int, float>();

        // ------------------------------------------------------------------ entry points

        /// <summary>Nexus mods to install (file id 0 = the mod's newest main file): they open one after another in the in-game browser.</summary>
        private void BrowseNexus(List<(int ModId, int FileId)> items, string what)
        {
            items = items.Where(i => i.ModId > 0).GroupBy(i => i.ModId).Select(g => g.First()).ToList();
            if (items.Count == 0) return;
            SaveBackups.Create("before installing " + what, true);
            Activity.Add($"Installing {what} from Nexus in the Mods menu's browser");
            _browserQueue.Clear();
            _browserQueue.AddRange(items);
            _browserDone = 0; _browserTotal = items.Count;
            _browserOpen = true; _browserError = ""; _browserOpenedAt = Time.unscaledTime; ResetBrowserOverlays();
            _browserStatus = "Opening Nexus…";
            _browserStatusGood = true;
            if (!InGameBrowser.Installed) return;   // DrawBrowser offers the one-time download first
            NextInBrowser();
        }

        /// <summary>Nexus's Nomad Drive mods in the menu's browser, to look around (a file downloaded there is installed too).</summary>
        private void OpenNexusHere()
        {
            _browserQueue.Clear(); _browserNow = (0, 0); _browserDone = 0; _browserTotal = 0;
            _browserOpen = true; _browserError = ""; _browserOpenedAt = Time.unscaledTime; ResetBrowserOverlays();
            _browserStatus = "Open a mod, go to its Files tab and press the download button: the Mods menu installs it.";
            _browserStatusGood = true;
            if (!InGameBrowser.Installed) return;
            var w = Mathf.Max(800, Screen.width - (int)(460 * _s)); var h = Mathf.Max(500, Screen.height - (int)(260 * _s));
            if (!InGameBrowser.Open($"https://www.nexusmods.com/games/{Nexus.GameDomain}/mods", w, h, out var err)) _browserError = "The browser didn't start: " + err;
            else { _sentW = w; _sentH = h; }
        }

        private static string NexusFilesUrl(int modId, int fileId) =>
            $"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{modId}?tab=files" + (fileId > 0 ? "&file_id=" + fileId : "");

        /// <summary>
        /// The next mod: straight to its file's download page (where Nexus shows Slow download, or Fast download for Premium), not the
        /// Files tab. Without a file id, the mod's newest main file is looked up first.
        /// </summary>
        private void NextInBrowser()
        {
            if (_browserQueue.Count == 0) return;
            _browserNow = _browserQueue[0];
            _browserQueue.RemoveAt(0);
            if (_browserNow.FileId > 0) { OpenInBrowser(); return; }
            _browserStatus = "Finding the newest file…"; _browserStatusGood = true;
            _host.StartCoroutine(FindNewestFile(_browserNow.ModId));
        }

        private IEnumerator FindNewestFile(int modId)
        {
            var files = new List<NexusFileInfo>();
            yield return Nexus.Files(modId, l => files = l);   // newest first
            if (_browserNow.ModId != modId || !_browserOpen) yield break;
            var f = files.FirstOrDefault(x => x.Category == "MAIN") ?? files.FirstOrDefault(x => x.Category != "ARCHIVED" && x.Category != "OLD_VERSION") ?? files.FirstOrDefault();
            if (f != null) _browserNow = (modId, f.Id);   // otherwise the Files tab, to pick one by hand
            OpenInBrowser();
        }

        private void OpenInBrowser()
        {
            var url = NexusFilesUrl(_browserNow.ModId, _browserNow.FileId);
            var w = Mathf.Max(640, (int)_browserRect.width); var h = Mathf.Max(400, (int)_browserRect.height);
            if (_browserRect.width < 10) { w = Mathf.Max(800, Screen.width - (int)(460 * _s)); h = Mathf.Max(500, Screen.height - (int)(260 * _s)); }
            if (!InGameBrowser.Open(url, w, h, out var err))
            {
                _browserError = "The browser didn't start: " + err;
                ModManagerPlugin.Log.LogWarning("In-game browser: " + err);
                return;
            }
            _sentW = w; _sentH = h;
            var step = _browserNow.FileId > 0 ? "press the download button on this page." : "pick a file and press its download button.";
            _browserStatus = (_browserTotal > 1 ? $"{_browserDone + 1} of {_browserTotal}: " : "") + Cap1(step);
            _browserStatusGood = true;
            _browserPrompt = _browserNow.FileId > 0;
            InGameBrowser.Account = "";
        }

        /// <summary>The download prompt, worded for what Nexus shows this player (free, Premium or not signed in).</summary>
        private string DownloadPrompt()
        {
            var n = _browserTotal > 1 ? $"{_browserDone + 1} of {_browserTotal}: " : "";
            switch (InGameBrowser.Account)
            {
                case "free": return n + "Press Slow download on this page. Nexus makes free accounts wait a few seconds.";
                case "premium": return n + "Press Start download on this page.";
                case "signedout": return n + "Log in to Nexus first (top right of the page), then press the download button.";
                default: return n + "Press the download button on this page.";
            }
        }

        private static string Cap1(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t.Substring(1) : t;

        private void ResetBrowserOverlays()
        {
            _ctxOpen = false; _blockedUrl = ""; _padClick = false; _padADown = false; _padCursorOn = false; _keyRepeat.Clear();
        }

        /// <summary>Esc / B: first closes the right-click menu or the link notice, if one shows.</summary>
        private bool BrowserDismiss()
        {
            if (_ctxOpen) { _ctxOpen = false; return true; }
            if (_blockedUrl.Length > 0) { _blockedUrl = ""; return true; }
            return false;
        }

        /// <summary>A button in the browser panel that the controller's cursor can press too.</summary>
        private bool PadBtn(Rect r, string text, bool primary)
        {
            var aimed = _padCursorOn && r.Contains(_padCursor);
            var hit = Btn(r, text, primary, aimed);
            if (aimed && _padClick) { _padClick = false; return true; }
            return hit;
        }

        private void CloseBrowser()
        {
            _browserOpen = false;
            ResetBrowserOverlays();
            _browserQueue.Clear();
            _browserInstalling = false;
            _browserPrompt = false;
            InGameBrowser.Close();
            _cat.Refresh();
        }

        /// <summary>When the menu's browser can't be used: the mod's Nexus page in the player's own web browser.</summary>
        private void NexusFallback()
        {
            var ids = new List<int> { _browserNow.ModId }.Concat(_browserQueue.Select(q => q.ModId)).Where(i => i > 0).Distinct().ToList();
            CloseBrowser();
            if (ids.Count == 0) return;
            Application.OpenURL(NexusFilesUrl(ids[0], 0));
            Toast("Opened the mod on Nexus in your web browser. Download it there and install it by hand, or try the Mods menu's browser again later.", false);
        }

        // ------------------------------------------------------------------ drawing

        private void DrawBrowser(Rect r)
        {
            // Events from the browser: page state, downloads.
            foreach (var e in InGameBrowser.Pump()) BrowserEvent(e);

            _browserPanel = r;
            var bar = new Rect(r.x, r.y, r.width, 44 * _s);
            var x = bar.x;
            if (PadBtn(new Rect(x, bar.y, 120 * _s, 40 * _s), "‹  Close", false)) { CloseBrowser(); return; }
            x += 128 * _s;
            var live = InGameBrowser.Connected;
            if (live)
            {
                if (PadBtn(new Rect(x, bar.y, 48 * _s, 40 * _s), "←", false) && InGameBrowser.CanBack) InGameBrowser.Send("back");
                x += 54 * _s;
                if (PadBtn(new Rect(x, bar.y, 48 * _s, 40 * _s), "→", false) && InGameBrowser.CanForward) InGameBrowser.Send("fwd");
                x += 54 * _s;
                if (PadBtn(new Rect(x, bar.y, 96 * _s, 40 * _s), InGameBrowser.Loading ? "Stop" : "Reload", false)) InGameBrowser.Send(InGameBrowser.Loading ? "stop" : "reload");   // (the game's font has no ⟳ or ✕)
                x += 102 * _s;
                if (PadBtn(new Rect(x, bar.y, 90 * _s, 40 * _s), "Files", false) && _browserNow.ModId > 0) InGameBrowser.Send("nav " + NexusFilesUrl(_browserNow.ModId, _browserNow.FileId));
                x += 98 * _s;
            }
            var addr = new Rect(x, bar.y, bar.xMax - x, 40 * _s);
            Fill(addr, CField, 10 * _s);
            var shown = InGameBrowser.Url.Replace("https://", "").Replace("www.", "");
            Lbl(new Rect(addr.x + 14 * _s, addr.y, addr.width - 28 * _s, addr.height), (InGameBrowser.Loading ? "Loading…  " : "") + Clip(shown, 120), Tint(_small, CMuted));

            // What to do / what's happening
            if (_browserPrompt) { _browserStatus = DownloadPrompt(); _browserStatusGood = InGameBrowser.Account != "signedout"; }
            var info = new Rect(r.x, bar.yMax + 8 * _s, r.width, 30 * _s);
            if (InGameBrowser.Restarting) Lbl(info, "The browser stopped by itself. Starting it again on the same page…", Tint(_smallBold, CWarn));
            else Lbl(info, _browserStatus, Tint(_smallBold, _browserStatusGood ? CText : CWarn));
            if (_padCursorOn)
                Lbl(new Rect(info.x, info.y, info.width, info.height), "A click   B back   X type   Y copy / paste   Start Enter   R stick scroll   ",
                    Tint(new GUIStyle(_small) { alignment = TextAnchor.MiddleRight }, CMuted));

            var page = new Rect(r.x, info.yMax + 8 * _s, r.width, r.yMax - info.yMax - 8 * _s);
            _browserRect = page;
            Fill(page, Color.white, 8 * _s);

            if (!InGameBrowser.Installed)
            {
                // A newer browser app with the same Chromium: a small download, done without asking.
                if (InGameBrowser.HasBase && !InGameBrowser.Fetching && !_browserAutoUpdate)
                {
                    _browserAutoUpdate = true;
                    _host.StartCoroutine(InGameBrowser.Fetch((ok, msg) =>
                    {
                        _browserAutoUpdate = false;
                        if (!ok) { Toast(msg, false); return; }
                        if (_browserOpen) { if (_browserQueue.Count > 0) NextInBrowser(); else if (_browserNow.ModId > 0) { _browserQueue.Insert(0, _browserNow); NextInBrowser(); } else OpenNexusHere(); }
                    }));
                }
                DrawBrowserSetup(page);
                return;
            }
            if (_browserError.Length > 0)
            {
                Lbl(new Rect(page.x + 30 * _s, page.y + 30 * _s, page.width - 60 * _s, 60 * _s), _browserError, Tint(_wrap, CInk));
                if (PadBtn(new Rect(page.x + 30 * _s, page.y + 100 * _s, 160 * _s, 42 * _s), "Try again", true)) { _browserError = ""; if (_browserNow.ModId > 0) _browserQueue.Insert(0, _browserNow); NextInBrowser(); }
                if (PadBtn(new Rect(page.x + 200 * _s, page.y + 100 * _s, 300 * _s, 42 * _s), "Open in my web browser", false)) NexusFallback();
                DrawPadCursor();
                return;
            }
            var tex = InGameBrowser.Texture;
            if (tex != null) GUI.DrawTexture(page, tex, ScaleMode.StretchToFill, false);
            else Lbl(page, InGameBrowser.Running ? "Opening Nexus Mods…" : "Starting the browser…", Center(_body, CInk));

            // Keep the browser the size it's shown at (sharp text), at most every quarter second.
            if (Event.current.type == EventType.Repaint && live && Time.unscaledTime - _sizeAt > 0.25f)
            {
                var w = Mathf.RoundToInt(page.width); var h = Mathf.RoundToInt(page.height);
                if (Mathf.Abs(w - _sentW) > 2 || Mathf.Abs(h - _sentH) > 2) { InGameBrowser.Resize(w, h); _sentW = w; _sentH = h; _sizeAt = Time.unscaledTime; }
            }

            DrawLinkNotice(page);
            DrawContextMenu(page);
            DrawPadCursor();
        }

        /// <summary>A link to somewhere other than Nexus Mods or Nomad Drive Mods: it can open in the player's own browser.</summary>
        private void DrawLinkNotice(Rect page)
        {
            if (_blockedUrl.Length == 0) { _blockedRect = default; return; }
            var host = _blockedUrl;
            try { host = new Uri(_blockedUrl).Host; } catch { }
            var box = new Rect(page.x + 16 * _s, page.y + 16 * _s, page.width - 32 * _s, 64 * _s);
            _blockedRect = box;
            Fill(box, CCard, 10 * _s);
            var bw = 290 * _s; var sw = 130 * _s;
            Lbl(new Rect(box.x + 16 * _s, box.y, box.width - bw - sw - 48 * _s, box.height), "That link goes to " + Clip(host, 60) + ", outside Nexus Mods. It opens in your own web browser, not in here.", Tint(_wrap, CText));
            if (PadBtn(new Rect(box.xMax - bw - sw - 24 * _s, box.y + 11 * _s, bw, 42 * _s), "Open in my web browser", true))
            {
                if (_blockedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || _blockedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) Application.OpenURL(_blockedUrl);
                _blockedUrl = "";
            }
            else if (PadBtn(new Rect(box.xMax - sw - 12 * _s, box.y + 11 * _s, sw, 42 * _s), "Stay here", false)) _blockedUrl = "";
        }

        /// <summary>The right-click menu: copy, cut, paste, select all, and for links copy or open in the player's own browser.</summary>
        private void DrawContextMenu(Rect page)
        {
            var tex = InGameBrowser.Texture;
            if (!_ctxOpen || tex == null) { _ctxRect = default; return; }
            var items = new List<(string Text, Action Do)>();
            if (_ctxSelection) items.Add(("Copy", () => InGameBrowser.Send("copy")));
            if (_ctxEditable && _ctxSelection) items.Add(("Cut", () => InGameBrowser.Send("cut")));
            if (_ctxEditable) items.Add(("Paste", () => InGameBrowser.Send("paste")));
            items.Add(("Select all", () => InGameBrowser.Send("selectall")));
            if (_ctxLink.Length > 0)
            {
                items.Add(("Copy link", () => InGameBrowser.Send("copylink")));
                var link = _ctxLink;
                items.Add(("Open link in my web browser", () => Application.OpenURL(link)));
            }
            if (!_ctxSelection && !_ctxEditable && _ctxLink.Length == 0 && InGameBrowser.CanBack) items.Insert(0, ("Back", () => InGameBrowser.Send("back")));
            var w = 300 * _s; var ih = 40 * _s;
            var at = new Vector2(page.x + _ctxAt.x * page.width / tex.width, page.y + _ctxAt.y * page.height / tex.height);
            var box = new Rect(Mathf.Min(at.x, page.xMax - w - 6 * _s), Mathf.Min(at.y, page.yMax - items.Count * (ih + 4 * _s) - 14 * _s), w, items.Count * (ih + 4 * _s) + 8 * _s);
            _ctxRect = box;
            Fill(new Rect(box.x - 2 * _s, box.y - 2 * _s, box.width + 4 * _s, box.height + 4 * _s), CEdge, 12 * _s);
            Fill(box, CCard, 10 * _s);
            for (var i = 0; i < items.Count; i++)
                if (PadBtn(new Rect(box.x + 4 * _s, box.y + 4 * _s + i * (ih + 4 * _s), w - 8 * _s, ih), items[i].Text, false))
                {
                    _ctxOpen = false;
                    InGameBrowser.Send("focus 1");
                    items[i].Do();
                    break;
                }
        }

        private void DrawPadCursor()
        {
            _padClick = false;   // a controller press that hit no button
            if (!_padCursorOn || Event.current.type != EventType.Repaint) return;
            var c = _padCursor; var r = 11 * _s;
            Fill(new Rect(c.x - r - 2 * _s, c.y - r - 2 * _s, (r + 2 * _s) * 2, (r + 2 * _s) * 2), CInk, r + 2 * _s);
            Fill(new Rect(c.x - r, c.y - r, r * 2, r * 2), CAccent, r);
            Fill(new Rect(c.x - 3 * _s, c.y - 3 * _s, 6 * _s, 6 * _s), CInk, 3 * _s);
        }

        private void DrawBrowserSetup(Rect page)
        {
            var p = new Rect(page.x + 40 * _s, page.y + 40 * _s, page.width - 80 * _s, page.height - 80 * _s);
            Lbl(new Rect(p.x, p.y, p.width, 40 * _s), "Install Nexus mods without leaving the game", Tint(_h2, CInk));
            Lbl(new Rect(p.x, p.y + 50 * _s, p.width, 90 * _s),
                "The Mods menu shows Nexus Mods in its own browser. You sign in to Nexus there and press the download button, and the menu installs the mod. "
                + "The browser is a one-time download from this project's GitHub (about 170 MB, checked against GitHub's checksum).", Tint(_wrap, CInk));
            if (InGameBrowser.Fetching)
            {
                var bar = new Rect(p.x, p.y + 160 * _s, Mathf.Min(560 * _s, p.width), 36 * _s);
                Fill(bar, new Color(0.85f, 0.85f, 0.85f), 10 * _s);
                Fill(new Rect(bar.x, bar.y, bar.width * InGameBrowser.FetchProgress, bar.height), CAccent, 10 * _s);
                Lbl(new Rect(p.x, bar.yMax + 10 * _s, p.width, 30 * _s), InGameBrowser.FetchStatus, Tint(_small, CInk));
                return;
            }
            if (InGameBrowser.FetchStatus.Length > 0) Lbl(new Rect(p.x, p.y + 140 * _s, p.width, 30 * _s), InGameBrowser.FetchStatus, Tint(_smallBold, new Color(0.75f, 0.2f, 0.15f)));
            if (PadBtn(new Rect(p.x, p.y + 180 * _s, 260 * _s, 46 * _s), "Get the browser", true))
                _host.StartCoroutine(InGameBrowser.Fetch((ok, msg) => { if (ok) { if (_browserOpen) { if (_browserQueue.Count > 0) NextInBrowser(); else OpenNexusHere(); } } else Toast(msg, false); }));
            if (PadBtn(new Rect(p.x + 272 * _s, p.y + 180 * _s, 300 * _s, 46 * _s), "Open in my web browser", false)) NexusFallback();
            DrawPadCursor();
        }

        // ------------------------------------------------------------------ events from the browser

        private void BrowserEvent(string e)
        {
            if (e.StartsWith("dl-start ")) { _browserPrompt = false; _browserStatus = "Downloading " + e.Substring(9) + "…"; _browserStatusGood = true; }
            else if (e.StartsWith("dl-progress "))
            {
                var p = e.Split(' ');
                if (p.Length > 1 && int.TryParse(p[1], out var pct) && pct >= 0) _browserStatus = $"Downloading… {pct}%" + (p.Length > 2 && long.TryParse(p[2], out var got) ? $"  ({got / 1048576f:0.0} MB)" : "");
            }
            else if (e.StartsWith("dl-error ")) { _browserPrompt = false; _browserStatus = e.Substring(9); _browserStatusGood = false; }
            else if (e.StartsWith("dl-done "))
            {
                var parts = e.Substring(8).Split('\t');
                if (parts.Length >= 3) _host.StartCoroutine(InstallDownloaded(parts[0], parts[1], parts[2]));
            }
            else if (e.StartsWith("blocked ")) _blockedUrl = e.Substring(8).Trim();
            else if (e.StartsWith("ctx "))
            {
                // ctx X Y editable selection<TAB>link
                var tab = e.IndexOf('\t');
                var p = (tab >= 0 ? e.Substring(4, tab - 4) : e.Substring(4)).Split(' ');
                _ctxLink = tab >= 0 ? e.Substring(tab + 1).Trim() : "";
                if (!_ctxLink.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !_ctxLink.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) _ctxLink = "";
                int.TryParse(p.Length > 0 ? p[0] : "", out var cx); int.TryParse(p.Length > 1 ? p[1] : "", out var cy);
                _ctxAt = new Vector2(cx, cy);
                _ctxEditable = p.Length > 2 && p[2] == "1"; _ctxSelection = p.Length > 3 && p[3] == "1";
                _ctxOpen = true;
            }
            else if (e.StartsWith("closed "))
            {
                if (_browserOpen && _browserError.Length == 0) _browserError = "The browser closed (" + e.Substring(7) + ").";
            }
        }

        /// <summary>What the player downloaded on Nexus: which mod and file it is, then installed like any mod.</summary>
        private IEnumerator InstallDownloaded(string path, string original, string pageUrl)
        {
            if (_browserInstalling) yield break;
            _browserInstalling = true;
            try
            {
                var modId = InGameBrowser.ModIdOf(pageUrl);
                if (modId == 0) modId = _browserNow.ModId;
                if (modId == 0)
                {
                    _browserStatus = "Download mods from their own page on Nexus (the Files tab), so the menu knows which mod it is.";
                    _browserStatusGood = false;
                    yield break;
                }
                var fileId = InGameBrowser.FileIdOf(pageUrl);
                if (fileId == 0 && modId == _browserNow.ModId) fileId = _browserNow.FileId;
                _browserStatus = "Installing…"; _browserStatusGood = true;

                string name = "", author = "", modVersion = "";
                yield return Nexus.Info(modId, (n, a, v) => { name = n; author = a; modVersion = v; });
                var files = new List<NexusFileInfo>();
                yield return Nexus.Files(modId, l => files = l);
                var stem = Path.GetFileNameWithoutExtension(original).ToLowerInvariant();
                var file = files.FirstOrDefault(f => f.Id == fileId && fileId > 0)
                           ?? files.FirstOrDefault(f => f.FileName.Length > 0 && stem.StartsWith(Path.GetFileNameWithoutExtension(f.FileName).ToLowerInvariant()))
                           ?? files.FirstOrDefault(f => f.Category == "MAIN");
                if (name.Length == 0) name = Path.GetFileNameWithoutExtension(original);
                var version = file != null && file.Version.Length > 0 ? file.Version : modVersion;

                bool staged; string error = null;
                try { staged = Community.InstallNexusFile(path, modId, file?.Id ?? fileId, name, author, version, original); }
                catch (Exception ex) { staged = false; error = ex.Message; ModManagerPlugin.Log.LogWarning("Installing " + name + " from the browser: " + ex); }
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                _cat.Refresh();
                if (error != null) { _browserStatus = $"Couldn't install {name}: {error}"; _browserStatusGood = false; yield break; }

                _browserDone++;
                var msg = $"{name} v{version.TrimStart('v', 'V')} is installed" + (staged ? " — it replaces files in use, so it switches over when you restart." : ".");
                Toast(msg, true);
                if (_browserQueue.Count > 0) { _browserStatus = msg + " Next one…"; NextInBrowser(); }
                else _browserStatus = msg + (_browserTotal > 1 ? $" All {_browserDone} done. Close the browser when you're ready." : " Close the browser when you're ready.");
            }
            finally { _browserInstalling = false; }
        }

        // ------------------------------------------------------------------ mouse and keyboard → the page

        /// <summary>
        /// From Tick, every frame while the browser shows: the mouse, wheel, keys and controller go to the page.
        /// Returns true: the browser takes all input (the menu's own controller navigation waits).
        /// </summary>
        private bool BrowserInput(UnityEngine.InputSystem.Keyboard kb, UnityEngine.InputSystem.Gamepad pad)
        {
            BrowserPad(pad);
            if (!InGameBrowser.Connected || _browserRect.width < 10 || InGameBrowser.Texture == null) return true;
            UnityEngine.InputSystem.Mouse mouse = null;
            try { mouse = UnityEngine.InputSystem.Mouse.current; } catch { }
            var mods = KeyMods(kb);
            if (mouse != null)
            {
                var sp = mouse.position.ReadValue();
                var gui = new Vector2(sp.x, Screen.height - sp.y);
                if ((gui - _lastMouse).sqrMagnitude > 16f && _lastMouse.x >= 0) _padCursorOn = false;   // the mouse moved: its pointer, not the controller's
                var over = OverOverlay(gui);
                var inside = _browserRect.Contains(gui) && !over;
                var (px, py) = PagePoint(gui);
                var held = mods | (mouse.leftButton.isPressed ? 16 : 0) | (mouse.rightButton.isPressed ? 64 : 0);
                if (!_padCursorOn)
                {
                    if (inside && (gui - _lastMouse).sqrMagnitude > 0.5f) InGameBrowser.Send($"move {px} {py} {held}");
                    else if (!inside && _browserRect.Contains(_lastMouse) && !OverOverlay(_lastMouse)) InGameBrowser.Send($"leave {px} {py} {held}");
                }
                _lastMouse = gui;
                // A click outside the right-click menu closes it (and still reaches the page).
                if (_ctxOpen && !over && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) _ctxOpen = false;
                if (inside)
                {
                    void Click(int button, bool up)
                    {
                        if (!up)
                        {
                            _clickCount = Time.unscaledTime - _lastClickAt < 0.4f && (gui - _lastClickPos).sqrMagnitude < 36 ? _clickCount + 1 : 1;
                            _lastClickAt = Time.unscaledTime; _lastClickPos = gui;
                            InGameBrowser.Send("focus 1");
                        }
                        InGameBrowser.Send($"{(up ? "up" : "down")} {px} {py} {button} {Mathf.Clamp(_clickCount, 1, 3)} {held}");
                    }
                    if (mouse.leftButton.wasPressedThisFrame) Click(0, false);
                    if (mouse.leftButton.wasReleasedThisFrame) Click(0, true);
                    if (mouse.rightButton.wasPressedThisFrame) Click(1, false);
                    if (mouse.rightButton.wasReleasedThisFrame) Click(1, true);
                    if (mouse.middleButton.wasPressedThisFrame) Click(2, false);
                    if (mouse.middleButton.wasReleasedThisFrame) Click(2, true);
                    var wheel = mouse.scroll.ReadValue();
                    if (Mathf.Abs(wheel.y) > 0.01f || Mathf.Abs(wheel.x) > 0.01f)
                    {
                        // Windows sends 120 a notch; some setups report 1 a notch.
                        float Notches(float v) => Mathf.Abs(v) < 10 ? v * 120f : v;
                        InGameBrowser.Send($"wheel {px} {py} {Mathf.RoundToInt(Notches(wheel.x))} {Mathf.RoundToInt(Notches(wheel.y))} {mods}");
                    }
                }
            }
            if (kb == null) return true;
            // Keys that don't type a character (typing itself comes through OnText). Held, they repeat like in any browser.
            foreach (var (key, vk) in BrowserKeys)
            {
                UnityEngine.InputSystem.Controls.KeyControl k;
                try { k = kb[key]; } catch { continue; }
                if (k.wasPressedThisFrame) { PressKey(vk, mods); _keyRepeat[(int)key] = Time.unscaledTime + 0.45f; }
                else if (k.isPressed && _keyRepeat.TryGetValue((int)key, out var next) && Time.unscaledTime >= next && vk != 13)
                {
                    InGameBrowser.Send($"key down {vk} {mods}"); if (vk == 8) InGameBrowser.Send($"char 8 {mods}");
                    _keyRepeat[(int)key] = Time.unscaledTime + 0.04f;
                }
                if (k.wasReleasedThisFrame) { InGameBrowser.Send($"key up {vk} {mods}"); _keyRepeat.Remove((int)key); }
            }
            // Ctrl+C / X / V / A / Z / Y: copy, cut, paste, select all, undo, redo (through Windows' clipboard).
            if ((mods & 4) != 0 && (mods & 8) == 0)
                foreach (var (key, cmd) in CtrlKeys)
                {
                    UnityEngine.InputSystem.Controls.KeyControl k;
                    try { k = kb[key]; } catch { continue; }
                    if (k.wasPressedThisFrame) InGameBrowser.Send(cmd == "undo" && (mods & 2) != 0 ? "redo" : cmd);
                }
            return true;
        }

        private static int KeyMods(UnityEngine.InputSystem.Keyboard kb)
        {
            var mods = 0;
            if (kb == null) return 0;
            try
            {
                if (kb.shiftKey.isPressed) mods |= 2;
                if (kb.ctrlKey.isPressed) mods |= 4;
                if (kb.altKey.isPressed) mods |= 8;
            }
            catch { }
            return mods;
        }

        private static void PressKey(int vk, int mods)
        {
            InGameBrowser.Send($"key down {vk} {mods}");
            if (vk == 13 || vk == 8 || vk == 9) InGameBrowser.Send($"char {vk} {mods}");
        }

        /// <summary>A point on the screen (IMGUI coordinates) in the page's own pixels.</summary>
        private (int X, int Y) PagePoint(Vector2 gui)
        {
            var tex = InGameBrowser.Texture;
            if (tex == null || _browserRect.width < 1) return (0, 0);
            return (Mathf.RoundToInt((gui.x - _browserRect.x) * tex.width / _browserRect.width), Mathf.RoundToInt((gui.y - _browserRect.y) * tex.height / _browserRect.height));
        }

        private bool OverOverlay(Vector2 gui) => (_ctxOpen && _ctxRect.Contains(gui)) || (_blockedUrl.Length > 0 && _blockedRect.Contains(gui));

        // ------------------------------------------------------------------ the controller → the page

        /// <summary>
        /// Left stick moves a cursor over the page, A clicks, right stick scrolls, B goes back (or closes the browser),
        /// LB / RB back and forward, Y right-clicks (copy and paste), X opens Steam's on-screen keyboard, Start is Enter,
        /// the d-pad the arrow keys. On the toolbar and the menu's own buttons, A presses them.
        /// </summary>
        private void BrowserPad(UnityEngine.InputSystem.Gamepad pad)
        {
            if (pad == null) return;
            var dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            var settled = Time.unscaledTime - _browserOpenedAt > 0.3f;   // the A press that opened the browser isn't a click on the page
            var panel = _browserPanel.width > 10 ? _browserPanel : new Rect(0, 0, Screen.width, Screen.height);
            void Wake()
            {
                if (_padCursorOn) return;
                _padCursorOn = true;
                if (!panel.Contains(_padCursor)) _padCursor = _browserRect.width > 10 ? _browserRect.center : panel.center;
            }

            var stick = pad.leftStick.ReadValue();
            var m = stick.magnitude;
            if (m > 0.15f)
            {
                Wake();
                var speed = Mathf.Lerp(160f, 1500f, (m - 0.15f) / 0.85f * ((m - 0.15f) / 0.85f)) * _s;   // slow near the middle for aiming, fast at the edge
                _padCursor += new Vector2(stick.x, -stick.y).normalized * speed * Mathf.Min(1f, m) * dt;
                _padCursor.x = Mathf.Clamp(_padCursor.x, panel.x, panel.xMax - 1); _padCursor.y = Mathf.Clamp(_padCursor.y, panel.y, panel.yMax - 1);
            }
            var live = InGameBrowser.Connected && InGameBrowser.Texture != null && _browserRect.width > 10;
            var onPage = live && _browserRect.Contains(_padCursor) && !OverOverlay(_padCursor);
            var (px, py) = PagePoint(_padCursor);
            if (m > 0.15f && onPage) InGameBrowser.Send($"move {px} {py} {(_padADown ? 16 : 0)}");

            // A: a click on the page (held = drag), or a press of the button under the cursor.
            if (pad.buttonSouth.wasPressedThisFrame && settled)
            {
                Wake();
                if (onPage)
                {
                    if (_ctxOpen) _ctxOpen = false;
                    InGameBrowser.Send("focus 1");
                    InGameBrowser.Send($"down {px} {py} 0 1 16");
                    _padADown = true;
                }
                else _padClick = true;
            }
            if (pad.buttonSouth.wasReleasedThisFrame && _padADown) { _padADown = false; InGameBrowser.Send($"up {px} {py} 0 1 0"); }
            // Y: right-click, for the copy / paste menu.
            if (pad.buttonNorth.wasPressedThisFrame && onPage) { Wake(); InGameBrowser.Send($"down {px} {py} 1 1 64"); InGameBrowser.Send($"up {px} {py} 1 1 0"); }
            // B: close the menu or notice, else back a page, else close the browser.
            if (pad.buttonEast.wasPressedThisFrame && settled)
            {
                if (BrowserDismiss()) { }
                else if (live && InGameBrowser.CanBack) InGameBrowser.Send("back");
                else { CloseBrowser(); return; }
            }
            if (pad.leftShoulder.wasPressedThisFrame && live) InGameBrowser.Send("back");
            if (pad.rightShoulder.wasPressedThisFrame && live) InGameBrowser.Send("fwd");
            if (pad.startButton.wasPressedThisFrame && live) { PressKey(13, 0); InGameBrowser.Send("key up 13 0"); }
            if (pad.buttonWest.wasPressedThisFrame && live) SteamKeyboard();

            // Right stick: scroll where the cursor is (the middle of the page if it's off the page).
            var rs = pad.rightStick.ReadValue();
            if (live && rs.magnitude > 0.2f)
            {
                _padWheel += rs * 1600f * dt * Mathf.Min(1f, rs.magnitude);
                int wx = (int)_padWheel.x, wy = (int)_padWheel.y;
                if (wx != 0 || wy != 0)
                {
                    var (sx, sy) = onPage ? (px, py) : PagePoint(_browserRect.center);
                    InGameBrowser.Send($"wheel {sx} {sy} {wx} {wy} 0");
                    _padWheel -= new Vector2(wx, wy);
                }
            }
            else _padWheel = Vector2.zero;

            // D-pad: arrow keys (held: repeat).
            var arrow = pad.dpad.left.isPressed ? 37 : pad.dpad.up.isPressed ? 38 : pad.dpad.right.isPressed ? 39 : pad.dpad.down.isPressed ? 40 : -1;
            if (!live) arrow = -1;
            if (arrow != _padArrow) { if (_padArrow > 0) InGameBrowser.Send($"key up {_padArrow} 0"); _padArrow = arrow; if (arrow > 0) { InGameBrowser.Send($"key down {arrow} 0"); _padArrowNext = Time.unscaledTime + 0.4f; } }
            else if (arrow > 0 && Time.unscaledTime >= _padArrowNext) { InGameBrowser.Send($"key down {arrow} 0"); _padArrowNext = Time.unscaledTime + 0.07f; }
        }

        private bool _steamTyping;

        /// <summary>
        /// Steam's on-screen keyboard (in Big Picture mode and on Steam Deck), through the game's Steamworks library;
        /// what's typed there goes to the page's focused text box.
        /// </summary>
        private void SteamKeyboard()
        {
            if (_steamTyping) return;
            try
            {
                var utils = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Steamworks.SteamUtils", false)).FirstOrDefault(t => t != null);
                var show = utils?.GetMethod("ShowGamepadTextInput");
                var ev = utils?.GetEvent("OnGamepadTextInputDismissed");
                var get = utils?.GetMethod("GetEnteredGamepadText");
                if (show == null || ev == null || get == null) throw new MissingMethodException("Steamworks");
                var ps = show.GetParameters();
                Action<bool> done = null;
                done = submitted =>
                {
                    _steamTyping = false;
                    try { ev.RemoveEventHandler(null, done); } catch { }
                    if (!submitted || !_browserOpen) return;
                    string text;
                    try { text = get.Invoke(null, null) as string ?? ""; } catch { return; }
                    InGameBrowser.Send("focus 1");
                    foreach (var ch in text) if (ch >= 32 && ch != 127) InGameBrowser.Send($"char {(int)ch} 0");
                };
                ev.AddEventHandler(null, done);
                // (normal, single line, the prompt, at most 256 characters, nothing typed yet)
                var ok = show.Invoke(null, new object[] { Enum.ToObject(ps[0].ParameterType, 0), Enum.ToObject(ps[1].ParameterType, 0), "Type for the page", 256, "" }) as bool? == true;
                if (ok) { _steamTyping = true; return; }
                try { ev.RemoveEventHandler(null, done); } catch { }
            }
            catch (Exception e) { ModManagerPlugin.Log.LogInfo("Steam's on-screen keyboard: " + e.Message); }
            Toast("Steam's on-screen keyboard opens in Big Picture mode or on Steam Deck. Otherwise, type with a keyboard.", false);
        }

        private static readonly (UnityEngine.InputSystem.Key Key, int Vk)[] BrowserKeys =
        {
            (UnityEngine.InputSystem.Key.Backspace, 8), (UnityEngine.InputSystem.Key.Tab, 9), (UnityEngine.InputSystem.Key.Enter, 13), (UnityEngine.InputSystem.Key.NumpadEnter, 13),
            (UnityEngine.InputSystem.Key.PageUp, 33), (UnityEngine.InputSystem.Key.PageDown, 34), (UnityEngine.InputSystem.Key.End, 35), (UnityEngine.InputSystem.Key.Home, 36),
            (UnityEngine.InputSystem.Key.LeftArrow, 37), (UnityEngine.InputSystem.Key.UpArrow, 38), (UnityEngine.InputSystem.Key.RightArrow, 39), (UnityEngine.InputSystem.Key.DownArrow, 40),
            (UnityEngine.InputSystem.Key.Delete, 46),
        };

        private static readonly (UnityEngine.InputSystem.Key Key, string Cmd)[] CtrlKeys =
        {
            (UnityEngine.InputSystem.Key.A, "selectall"), (UnityEngine.InputSystem.Key.C, "copy"), (UnityEngine.InputSystem.Key.V, "paste"), (UnityEngine.InputSystem.Key.X, "cut"),
            (UnityEngine.InputSystem.Key.Z, "undo"), (UnityEngine.InputSystem.Key.Y, "redo"),
        };

        /// <summary>A typed character, for the page (control characters are sent as keys instead).</summary>
        private void BrowserType(char ch)
        {
            if (ch < 32 || ch == 127) return;
            var mods = 0;
            try { var kb = UnityEngine.InputSystem.Keyboard.current; if (kb != null && kb.shiftKey.isPressed) mods |= 2; } catch { }
            InGameBrowser.Send($"char {(int)ch} {mods}");
        }
    }
}
#endif

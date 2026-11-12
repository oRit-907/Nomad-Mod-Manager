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
            _browserOpen = true; _browserError = "";
            _browserStatus = "Opening Nexus…";
            _browserStatusGood = true;
            if (!InGameBrowser.Installed) return;   // DrawBrowser offers the one-time download first
            NextInBrowser();
        }

        /// <summary>Nexus's Nomad Drive mods in the menu's browser, to look around (a file downloaded there is installed too).</summary>
        private void OpenNexusHere()
        {
            _browserQueue.Clear(); _browserNow = (0, 0); _browserDone = 0; _browserTotal = 0;
            _browserOpen = true; _browserError = "";
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

        private void CloseBrowser()
        {
            _browserOpen = false;
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

            var bar = new Rect(r.x, r.y, r.width, 44 * _s);
            var x = bar.x;
            if (Btn(new Rect(x, bar.y, 120 * _s, 40 * _s), "‹  Close", false)) { CloseBrowser(); return; }
            x += 128 * _s;
            var live = InGameBrowser.Connected;
            if (live)
            {
                if (Btn(new Rect(x, bar.y, 48 * _s, 40 * _s), "←", false) && InGameBrowser.CanBack) InGameBrowser.Send("back");
                x += 54 * _s;
                if (Btn(new Rect(x, bar.y, 48 * _s, 40 * _s), "→", false) && InGameBrowser.CanForward) InGameBrowser.Send("fwd");
                x += 54 * _s;
                if (Btn(new Rect(x, bar.y, 96 * _s, 40 * _s), InGameBrowser.Loading ? "Stop" : "Reload", false)) InGameBrowser.Send(InGameBrowser.Loading ? "stop" : "reload");   // (the game's font has no ⟳ or ✕)
                x += 102 * _s;
                if (Btn(new Rect(x, bar.y, 90 * _s, 40 * _s), "Files", false) && _browserNow.ModId > 0) InGameBrowser.Send("nav " + NexusFilesUrl(_browserNow.ModId, _browserNow.FileId));
                x += 98 * _s;
            }
            var addr = new Rect(x, bar.y, bar.xMax - x, 40 * _s);
            Fill(addr, CField, 10 * _s);
            var shown = InGameBrowser.Url.Replace("https://", "").Replace("www.", "");
            Lbl(new Rect(addr.x + 14 * _s, addr.y, addr.width - 28 * _s, addr.height), (InGameBrowser.Loading ? "Loading…  " : "") + Clip(shown, 120), Tint(_small, CMuted));

            // What to do / what's happening
            if (_browserPrompt) { _browserStatus = DownloadPrompt(); _browserStatusGood = InGameBrowser.Account != "signedout"; }
            var info = new Rect(r.x, bar.yMax + 8 * _s, r.width, 30 * _s);
            Lbl(info, _browserStatus, Tint(_smallBold, _browserStatusGood ? CText : CWarn));

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
                if (Btn(new Rect(page.x + 30 * _s, page.y + 100 * _s, 160 * _s, 42 * _s), "Try again", true)) { _browserError = ""; if (_browserNow.ModId > 0) _browserQueue.Insert(0, _browserNow); NextInBrowser(); }
                if (Btn(new Rect(page.x + 200 * _s, page.y + 100 * _s, 300 * _s, 42 * _s), "Open in my web browser", false)) NexusFallback();
                return;
            }
            var tex = InGameBrowser.Texture;
            if (tex != null) GUI.DrawTexture(page, tex, ScaleMode.StretchToFill, false);
            else Lbl(page, InGameBrowser.Running ? "Opening Nexus Mods…" : "Starting the browser…", Center(_body, CInk));

            // Keep the browser the size it's shown at (sharp text), at most every quarter second.
            if (Event.current.type == EventType.Repaint && live && Time.unscaledTime - _sizeAt > 0.25f)
            {
                var w = Mathf.RoundToInt(page.width); var h = Mathf.RoundToInt(page.height);
                if (Mathf.Abs(w - _sentW) > 2 || Mathf.Abs(h - _sentH) > 2) { InGameBrowser.Send($"size {w} {h}"); _sentW = w; _sentH = h; _sizeAt = Time.unscaledTime; }
            }
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
            if (Btn(new Rect(p.x, p.y + 180 * _s, 260 * _s, 46 * _s), "Get the browser", true))
                _host.StartCoroutine(InGameBrowser.Fetch((ok, msg) => { if (ok) { if (_browserOpen) { if (_browserQueue.Count > 0) NextInBrowser(); else OpenNexusHere(); } } else Toast(msg, false); }));
            if (Btn(new Rect(p.x + 272 * _s, p.y + 180 * _s, 300 * _s, 46 * _s), "Open in my web browser", false)) NexusFallback();
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

        /// <summary>From Tick, every frame while the browser shows: the mouse, wheel and special keys go to the page.</summary>
        private void BrowserInput(UnityEngine.InputSystem.Keyboard kb)
        {
            if (!InGameBrowser.Connected || _browserRect.width < 10 || InGameBrowser.Texture == null) return;
            UnityEngine.InputSystem.Mouse mouse = null;
            try { mouse = UnityEngine.InputSystem.Mouse.current; } catch { }
            var tex = InGameBrowser.Texture;
            var mods = 0;
            if (kb != null)
            {
                if (kb.shiftKey.isPressed) mods |= 2;
                if (kb.ctrlKey.isPressed) mods |= 4;
                if (kb.altKey.isPressed) mods |= 8;
            }
            if (mouse != null)
            {
                var sp = mouse.position.ReadValue();
                var gui = new Vector2(sp.x, Screen.height - sp.y);
                var inside = _browserRect.Contains(gui);
                var px = Mathf.RoundToInt((gui.x - _browserRect.x) * tex.width / _browserRect.width);
                var py = Mathf.RoundToInt((gui.y - _browserRect.y) * tex.height / _browserRect.height);
                var held = mods | (mouse.leftButton.isPressed ? 16 : 0) | (mouse.rightButton.isPressed ? 64 : 0);
                if (inside && (gui - _lastMouse).sqrMagnitude > 0.5f) InGameBrowser.Send($"move {px} {py} {held}");
                else if (!inside && _browserRect.Contains(_lastMouse)) InGameBrowser.Send($"leave {px} {py} {held}");
                _lastMouse = gui;
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
            if (kb == null) return;
            // Keys that don't type a character (typing itself comes through OnText).
            foreach (var (key, vk) in BrowserKeys)
            {
                UnityEngine.InputSystem.Controls.KeyControl k;
                try { k = kb[key]; } catch { continue; }
                if (k.wasPressedThisFrame) { InGameBrowser.Send($"key down {vk} {mods}"); if (vk == 13) InGameBrowser.Send($"char 13 {mods}"); if (vk == 8 || vk == 9) InGameBrowser.Send($"char {vk} {mods}"); }
                if (k.wasReleasedThisFrame) InGameBrowser.Send($"key up {vk} {mods}");
            }
            // Ctrl+A / C / V / X / Z / Y: copy, paste and the rest.
            if ((mods & 4) != 0)
                foreach (var (key, vk) in CtrlKeys)
                {
                    UnityEngine.InputSystem.Controls.KeyControl k;
                    try { k = kb[key]; } catch { continue; }
                    if (k.wasPressedThisFrame) InGameBrowser.Send($"key down {vk} {mods}");
                    if (k.wasReleasedThisFrame) InGameBrowser.Send($"key up {vk} {mods}");
                }
        }

        private static readonly (UnityEngine.InputSystem.Key Key, int Vk)[] BrowserKeys =
        {
            (UnityEngine.InputSystem.Key.Backspace, 8), (UnityEngine.InputSystem.Key.Tab, 9), (UnityEngine.InputSystem.Key.Enter, 13), (UnityEngine.InputSystem.Key.NumpadEnter, 13),
            (UnityEngine.InputSystem.Key.PageUp, 33), (UnityEngine.InputSystem.Key.PageDown, 34), (UnityEngine.InputSystem.Key.End, 35), (UnityEngine.InputSystem.Key.Home, 36),
            (UnityEngine.InputSystem.Key.LeftArrow, 37), (UnityEngine.InputSystem.Key.UpArrow, 38), (UnityEngine.InputSystem.Key.RightArrow, 39), (UnityEngine.InputSystem.Key.DownArrow, 40),
            (UnityEngine.InputSystem.Key.Delete, 46),
        };

        private static readonly (UnityEngine.InputSystem.Key Key, int Vk)[] CtrlKeys =
        {
            (UnityEngine.InputSystem.Key.A, 65), (UnityEngine.InputSystem.Key.C, 67), (UnityEngine.InputSystem.Key.V, 86), (UnityEngine.InputSystem.Key.X, 88),
            (UnityEngine.InputSystem.Key.Z, 90), (UnityEngine.InputSystem.Key.Y, 89),
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

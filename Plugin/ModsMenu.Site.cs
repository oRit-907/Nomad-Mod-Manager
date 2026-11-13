using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// Browse mods: the community website (nomaddrivemods.online). Its files are scanned by VirusTotal and download
    /// directly, so they install right here, without Nomad Mod Downloader. (Nexus mods are on their own page.)
    /// </summary>
    internal sealed partial class ModsMenu
    {
        private void RunSiteSearch(bool more)
        {
            if (_siteLoading) return;
            _siteLoading = true;
            _siteSearched = true;
            _siteError = null;
            if (!more) { _siteResults.Clear(); _sitePage = 0; _scrollY = 0; }
            var page = _sitePage + 1;
            _host.StartCoroutine(Community.Search(_siteSearch, _siteSort, _siteCategory, page, (list, total, counts, err) =>
            {
                _siteLoading = false;
                if (err != null) { _siteError = err; return; }
                _sitePage = page;
                _siteTotal = total;
                if (counts != null) _siteCounts = counts;
                _siteResults.AddRange(list.Where(m => _siteResults.All(r => r.Id != m.Id)));
                foreach (var m in list) _cat.SetSiteInfo(m);
                _cat.Refresh();
            }));
        }

        private void DrawSite(Rect r)
        {
            Lbl(new Rect(r.x, r.y, r.width, 36 * _s), "Browse mods", _h2);
            var all = _siteCounts.Values.Sum(v => v);
            var sub = (all > 0 ? all + " mods" : "Mods") + " from " + Community.HostName + "  ·  virus-scanned  ·  install right here";
            // Account buttons on the right: the website account (or the button to make one), alerts, upload.
            if (SiteAccount.Has && !SiteAccount.Checked) _host.StartCoroutine(SiteAccount.Refresh());
            var hx = r.xMax;
            hx -= 170 * _s;
            if (Btn(new Rect(hx, r.y + 4 * _s, 170 * _s, 40 * _s), "Upload a mod", false)) OpenSiteSignedIn("/upload");
            if (SiteAccount.Has)
            {
                hx -= 218 * _s;
                if (Btn(new Rect(hx, r.y + 4 * _s, 210 * _s, 40 * _s), Clip(SiteAccount.Name, 16) + "  ›", false)) OpenSiteSignedIn("/account");
                var alerts = SiteAccount.Notifications > 0 ? $"Alerts ({SiteAccount.Notifications})" : "Alerts";
                hx -= 138 * _s;
                if (Btn(new Rect(hx, r.y + 4 * _s, 130 * _s, 40 * _s), alerts, SiteAccount.Notifications > 0)) OpenSiteSignedIn("/notifications");
            }
            else
            {
                hx -= 248 * _s;
                if (Btn(new Rect(hx, r.y + 4 * _s, 240 * _s, 40 * _s), SiteAccount.PairCode != null ? "Cancel sign-in" : "Sign in to the website", false)) { if (SiteAccount.PairCode != null) SiteAccount.CancelSignIn(); else if (!SiteAccount.Busy) SignInSite(); }
            }
            // Refresh: the list again from the site, fresh pictures, and update checks for what's installed.
            hx -= 138 * _s;
            if (Btn(new Rect(hx, r.y + 4 * _s, 130 * _s, 40 * _s), _siteLoading ? "Refreshing…" : "Refresh", false) && !_siteLoading)
            {
                Nexus.ClearThumbs(u => u.IndexOf(Community.HostName, StringComparison.OrdinalIgnoreCase) >= 0);
                RunSiteSearch(false);
                CheckUpdates();
                Toast($"Refreshing {Community.HostName}…", true);
            }
            Lbl(new Rect(r.x, r.y + 34 * _s, hx - r.x - 12 * _s, 22 * _s), sub, Tint(_small, CMuted));

            var y = r.y + 70 * _s;
            var sortW = 118 * _s;
            var fieldR = new Rect(r.x, y, r.width - Community.SortNames.Length * (sortW + 8 * _s) - 118 * _s, 42 * _s);
            Fill(fieldR, CField, 11 * _s);
            var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
            Lbl(new Rect(fieldR.x + 16 * _s, fieldR.y, fieldR.width - 56 * _s, fieldR.height),
                _siteSearch.Length > 0 ? _siteSearch + caret : "Search mods — just start typing", Tint(_body, _siteSearch.Length > 0 ? CText : CMuted));
            if (_siteSearch.Length > 0)
            {
                var xr = new Rect(fieldR.xMax - 38 * _s, fieldR.y + 7 * _s, 28 * _s, 28 * _s);
                Lbl(xr, "×", Center(_bold, xr.Contains(Event.current.mousePosition) ? CAccent : CMuted));
                if (Hit(xr)) { _siteSearch = ""; RunSiteSearch(false); }
            }
            if (Btn(new Rect(fieldR.xMax + 8 * _s, y, 102 * _s, 42 * _s), "Search", true) || _siteEnter) { _siteEnter = false; RunSiteSearch(false); }
            var sx = fieldR.xMax + 118 * _s;
            for (var i = 0; i < Community.SortNames.Length; i++)
            {
                if (Btn(new Rect(sx, y, sortW, 42 * _s), Community.SortNames[i], false, _siteSort == i) && _siteSort != i) { _siteSort = i; RunSiteSearch(false); }
                sx += sortW + 8 * _s;
            }
            y += 54 * _s;

            // Categories with the site's counts (the site does the filtering, so every mod in one is found)
            var chips = new List<string> { null };
            chips.AddRange(Categories.Order.Where(c => (_siteCounts.TryGetValue(c, out var n) && n > 0) || c == _siteCategory));
            var cx = r.x;
            foreach (var c in chips)
            {
                var label = c == null ? "All (" + all + ")" : c + " (" + (_siteCounts.TryGetValue(c, out var n) ? n : 0) + ")";
                var w = _smallBold.CalcSize(Lang.C(label)).x + 30 * _s;
                if (cx + w > r.xMax) break;
                var cr = new Rect(cx, y, w, 32 * _s);
                var on = _siteCategory == c;
                var hover = cr.Contains(Event.current.mousePosition);
                Fill(cr, on ? CAccent : hover ? CEdge : CLine, 16 * _s);
                Lbl(cr, label, Center(_smallBold, on ? CInk : CText));
                if (Hit(cr) && !on) { _siteCategory = c; RunSiteSearch(false); }
                cx += w + 8 * _s;
            }
            y += 46 * _s;

            // Asks once. The player signs in (or makes an account) on the website themselves; "Not now" hides this.
            if (!SiteAccount.Has && (!SiteAccount.Declined || _siteAskAccount || SiteAccount.PairCode != null))
            {
                var box = new Rect(r.x, y, r.width, 92 * _s);
                Fill(box, Alpha(CAccent, 0.12f), 12 * _s);
                var waiting = SiteAccount.PairCode != null;
                Lbl(new Rect(box.x + 18 * _s, box.y + 10 * _s, box.width - 420 * _s, 28 * _s),
                    waiting ? "Finish signing in in your browser" : "Sign in to your " + Community.HostName + " account?", Tint(_bold, CAccent));
                Lbl(new Rect(box.x + 18 * _s, box.y + 38 * _s, box.width - 420 * _s, 48 * _s),
                    waiting ? "Log in, or make a free account, then press Connect on the page. The menu picks it up by itself."
                            : "The website opens in your browser: log in, or make a free account there. Then mods you install here count as yours, the site tells you when they update, and you can endorse mods from the game.",
                    _wrapSmall);
                if (waiting)
                {
                    if (Btn(new Rect(box.xMax - 392 * _s, box.y + 24 * _s, 236 * _s, 44 * _s), "Cancel", false)) SiteAccount.CancelSignIn();
                }
                else
                {
                    if (Btn(new Rect(box.xMax - 392 * _s, box.y + 24 * _s, 236 * _s, 44 * _s), "Sign in", true) && !SiteAccount.Busy) SignInSite();
                    if (Btn(new Rect(box.xMax - 146 * _s, box.y + 24 * _s, 128 * _s, 44 * _s), "Not now", false)) { SiteAccount.Decline(); _siteAskAccount = false; }
                }
                y += 104 * _s;
            }

            if (Community.Busy != 0)
            {
                var bar = new Rect(r.x, y, r.width, 44 * _s);
                Fill(bar, CField, 11 * _s);
                Fill(new Rect(bar.x, bar.y, Mathf.Max(22 * _s, bar.width * Mathf.Clamp01(Community.Progress)), bar.height), Alpha(CAccent, 0.35f), 11 * _s);
                Lbl(new Rect(bar.x + 16 * _s, bar.y, bar.width - 32 * _s, bar.height), Community.Status + $"  {Community.Progress * 100:0}%", Tint(_smallBold, CText));
                y += 56 * _s;
            }

            var view = new Rect(r.x, y, r.width, r.yMax - y);
            if (_siteError != null)
            {
                Lbl(new Rect(view.x, view.y, view.width, 60 * _s), _siteError, Tint(_wrap, CBad));
                if (Btn(new Rect(view.x, view.y + 64 * _s, 140 * _s, 40 * _s), "Try again", true)) RunSiteSearch(false);
                if (Btn(new Rect(view.x + 150 * _s, view.y + 64 * _s, 220 * _s, 40 * _s), "Open the website", false)) Application.OpenURL(Community.Site);
                return;
            }
            if (_siteResults.Count == 0)
            {
                Lbl(new Rect(view.x, view.y, view.width, 40 * _s), _siteLoading ? "Loading mods…"
                    : _siteSearch.Length > 0 || _siteCategory != null ? "No mods found." : "No mods on " + Community.HostName + " yet. Be the first to upload one!", Tint(_body, CMuted));
                return;
            }

            const float cardH = 178, gap = 14;
            var cols = view.width > 1300 * _s ? 3 : 2;
            var colW = (view.width - 18 * _s - (cols - 1) * gap * _s) / cols;
            var rows = (_siteResults.Count + cols - 1) / cols;
            var more = _siteResults.Count < _siteTotal;
            var inner = BeginScroll(view, rows * (cardH + gap) * _s + (more ? 64 * _s : 0));
            for (var i = 0; i < _siteResults.Count; i++)
            {
                var cr = new Rect((i % cols) * (colW + gap * _s), inner.y + (i / cols) * (cardH + gap) * _s, colW, cardH * _s);
                if (cr.yMax < -50 || cr.y > view.height + 50) continue;
                SiteCard(cr, _siteResults[i]);
            }
            if (more && Btn(new Rect((inner.width - 220 * _s) / 2, inner.y + rows * (cardH + gap) * _s + 10 * _s, 220 * _s, 42 * _s), _siteLoading ? "Loading…" : "Load more", false))
                RunSiteSearch(true);
            EndScroll();
        }

        /// <summary>The longest word the Install button can show (for sizing it).</summary>
        private static string InstallLabel(SiteMod m) => ModEntry.IsLoaderName(m.Name) ? "Check BepInEx" : "Install";

        /// <summary>
        /// Where a website mod stands (Installed, Installing, Update ready…), next to its Install / Update / Details buttons.
        /// </summary>
        private void SiteStatus(Rect r, SiteMod m, string loaderLabel = null)
        {
            var entry = SiteInstalledEntry(m);   // installed from here, or the same mod installed from Nexus
            if (ModEntry.IsLoaderName(m.Name)) { if (Btn(r, loaderLabel ?? (_bold.CalcSize(Lang.C(InstallLabel(m))).x + 24 * _s <= r.width ? InstallLabel(m) : "Check"), false)) { if (!Downloader.ShowCheck()) Toast("Install or repair BepInEx with Check BepInEx (get it on nomaddrivemods.online), with the game closed.", true); } return; }
            if (Community.Busy == m.Id) { StatusText(r, $"Installing… {Community.Progress * 100:0}%", CAccent); return; }
            if (entry != null && entry.UpdateStaged) { StatusText(r, entry.LiveLater ? "Press Reload to finish" : "Restart to finish", CWarn); return; }
            // Only when a version is out that's higher than the installed one (from here or Nexus, whichever is higher).
            if (entry != null && entry.UpdateAvailable) { StatusText(r, "Update ready", CWarn); return; }
            if (entry != null) StatusText(r, "Installed", CGood);
        }

        private void SiteCard(Rect r, SiteMod m)
        {
            var hover = r.Contains(Event.current.mousePosition);
            Fill(r, hover ? Lighten(CCard, 0.03f) : CCard, 14 * _s);
            var th = r.height - 24 * _s;
            // The author's square card picture when there is one (like Browse mods on the site), else the main picture.
            var square = m.Card.Length > 0;
            var thumb = new Rect(r.x + 12 * _s, r.y + 12 * _s, square ? th : Mathf.Min(th * 16f / 9f, r.width * 0.42f), th);
            Fill(thumb, CRow, 10 * _s);
            var picUrl = square ? m.Card : m.Thumb;
            var tex = Nexus.Thumb(_host, picUrl);
            if (tex != null) GUI.DrawTexture(thumb, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 10 * _s);
            else Lbl(thumb, string.IsNullOrEmpty(picUrl) ? "No picture" : "…", Center(_small, CMuted));

            var x = thumb.xMax + 16 * _s;
            var w = r.xMax - x - 14 * _s;
            Lbl(new Rect(x, r.y + 10 * _s, w, 28 * _s), m.Name, _bold);
            Lbl(new Rect(x, r.y + 38 * _s, w, 22 * _s), $"{m.Category}  ·  by {m.Author}  ·  v{m.Version.TrimStart('v', 'V')}  ·  {Short(m.Downloads)} downloads", Tint(_small, CMuted));
            Lbl(new Rect(x, r.y + 64 * _s, w, 54 * _s), Clip(m.Summary, 140), _wrapSmall);
            // Both buttons stay inside the card: Install (or Update) takes what its words need, Details the rest, and the
            // status shows when the mod is installed. Install puts the mod in now, with the website mods it needs;
            // Subscribe on its page does the same and keeps it updated.
            var by = r.yMax - 52 * _s;
            float Need(string t) => Mathf.Max(120 * _s, _bold.CalcSize(Lang.C(t)).x + 36 * _s);
            if (ModEntry.IsLoaderName(m.Name))
            {
                var label = InstallLabel(m);
                if (Need(label) > w) label = "Check";
                SiteStatus(new Rect(x, by, Mathf.Min(Need(label), w), 40 * _s), m, label);
            }
            else
            {
                var entry = SiteInstalledEntry(m);
                var bx = x;
                if (entry == null || (entry.UpdateAvailable && !entry.UpdateStaged))
                {
                    // Not installed: Install. Installed with a newer version out: Update.
                    var busy = Community.Busy == m.Id || _siteInstalling == m.Id;
                    var main = entry == null ? "Install" : "Update";
                    var mw = Mathf.Min(Need(busy ? "Installing…" : main), w);
                    if (busy) StatusText(new Rect(bx, by, mw, 40 * _s), Community.Busy == m.Id ? $"Installing… {Community.Progress * 100:0}%" : "Installing…", CAccent);
                    else if (Btn(new Rect(bx, by, mw, 40 * _s), main, true)) { if (entry == null) InstallSiteMod(m); else UpdateMod(entry); }
                    bx += mw + 8 * _s;
                    if (Need("Details") <= x + w - bx && Btn(new Rect(bx, by, Need("Details"), 40 * _s), "Details", false)) OpenSiteDetails(m);
                }
                else
                {
                    var bw = Mathf.Min(Need("Details"), w);
                    if (Btn(new Rect(bx, by, bw, 40 * _s), "Details", false)) OpenSiteDetails(m);
                    var sw = w - bw - 8 * _s;
                    if (sw > 90 * _s) SiteStatus(new Rect(bx + bw + 8 * _s, by, sw, 40 * _s), m);
                }
            }
            if (Hit(r)) OpenSiteDetails(m);
        }

        private int _siteDetailTab;      // 0 = description and files, 1 = images
        private List<int> _siteMissingNexus = new List<int>();   // Nexus mods the open website mod needs that aren't installed

        /// <summary>The Nexus mod id in a requirement's link (nexusmods.com/nomaddrive/mods/123), or 0.</summary>
        private static int NexusIdOf(string url)
        {
            var u = (url ?? "").ToLowerInvariant();
            var key = "nexusmods.com/" + Nexus.GameDomain + "/mods/";
            var i = u.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            var n = 0;
            while (i < u.Length && u[i] >= '0' && u[i] <= '9' && n < 100000000) n = n * 10 + (u[i++] - '0');
            return n;
        }

        /// <summary>BepInEx itself isn't installed from here (Nomad Mod Downloader's Check BepInEx does that).</summary>
        private static bool IsLoaderReq(string name, int nexusId) => nexusId == 7 || ModEntry.IsLoaderName(name);

        /// <summary>Website mods that need mods from Nexus: they open one after another in the menu's browser and install from there.</summary>
        private void InstallNexusRequirements(List<int> ids, string forName)
        {
            ids = ids.Distinct().ToList();
            if (ids.Count == 0) return;
            InstallMany(ids, $"the Nexus mods {forName} needs");
            Toast($"{forName} needs {ids.Count} mod(s) from Nexus: they open here one after another. Press the download button for each.", true);
        }
        private int _siteViewer = -1;    // the picture open full size on the Images tab

        private void OpenSiteDetails(SiteMod m)
        {
            _siteDetailTab = 0; _siteViewer = -1;
            _siteScroll = _scrollY;
            _scrollY = 0;
            _siteDetails = null;
            _siteDetailsLoading = m.Id;
            _hasFocus = false;
            var id = m.Id;
            _host.StartCoroutine(Community.Details(id, (d, err) =>
            {
                if (_siteDetailsLoading != id) return;
                _siteDetailsLoading = 0;
                _siteDetails = d;
                if (d == null) { Toast(err ?? "Couldn't load the mod's page.", false); CloseSiteDetails(); }
                else { _cat.SetSiteInfo(d.Mod); _cat.Refresh(); }
            }));
        }

        private void CloseSiteDetails()
        {
            _siteDetails = null;
            _siteDetailsLoading = 0;
            _scrollY = _siteScroll;
            _hasFocus = false;
        }

        /// <summary>The site's descriptions use a little Markdown (# headings, - lists, **bold**): shown as plain text here.</summary>
        private static string PlainText(string s)
        {
            var lines = (s ?? "").Replace("\r\n", "\n").Split('\n').Select(l =>
            {
                var t = l.TrimStart();
                if (t.StartsWith("#")) t = t.TrimStart('#').Trim().ToUpperInvariant();
                else if (t.StartsWith("- ") || t.StartsWith("* ")) t = "•  " + t.Substring(2);
                return t.Replace("**", "").Replace("`", "");
            });
            return string.Join("\n", lines.ToArray()).Trim();
        }

        private void DrawSiteDetails(Rect r)
        {
            if (Btn(new Rect(r.x, r.y, 190 * _s, 40 * _s), "‹  Back to mods", false)) { CloseSiteDetails(); return; }
            if (_siteDetails == null) { Lbl(new Rect(r.x, r.y + 60 * _s, r.width, 40 * _s), "Loading the mod's page…", Tint(_body, CMuted)); return; }
            var d = _siteDetails;
            var m = d.Mod;
            var top = r.y + 56 * _s;

            // Left: picture, requirements, the scan note
            var leftW = Mathf.Min(r.width * 0.4f, 640 * _s);
            var pic = new Rect(r.x, top, leftW, leftW * 9f / 16f);
            Fill(pic, CRow, 12 * _s);
            if (m.Header.Length > 0 && !d.Images.Any(x => x.Key == m.Header)) d.Images.Insert(0, new KeyValuePair<string, string>(m.Header, "Header"));
            var mainPic = d.Images.Count > 0 && d.Images[0].Key != m.Header ? d.Images[0].Key : d.Images.Count > 1 ? d.Images[1].Key : m.Thumb;
            var tex = Nexus.Thumb(_host, mainPic);
            if (tex != null) GUI.DrawTexture(pic, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 12 * _s);
            else Lbl(pic, string.IsNullOrEmpty(mainPic) ? "No picture" : "Loading picture…", Center(_small, CMuted));
            if (d.Images.Count > 0)
            {
                var more = new Rect(pic.xMax - 150 * _s, pic.yMax - 40 * _s, 140 * _s, 30 * _s);
                Fill(more, new Color(0, 0, 0, 0.55f), 8 * _s);
                Lbl(more, d.Images.Count == 1 ? "View picture" : $"All {d.Images.Count} pictures", Center(_smallBold, CText));
                if (Hit(pic)) { _siteDetailTab = 1; _siteViewer = 0; }
            }
            var ly = pic.yMax + 20 * _s;
            Lbl(new Rect(r.x, ly, leftW, 28 * _s), "Requirements", Tint(_bold, CAccent));
            ly += 34 * _s;
            // Mods from the site (installable right here) and off-site ones (opened in the browser)
            var missing = new List<int>();
            var missingNexus = new List<int>();
            foreach (var q in d.Requires.Take(8))
            {
                var row = new Rect(r.x, ly, leftW, 44 * _s);
                Fill(row, CRow, 10 * _s);
                var nx = q.Id == 0 ? NexusIdOf(q.Url) : 0;
                // BepInEx is always there (the Mods menu runs on it), so it's never installed or offered as missing.
                var loader = IsLoaderReq(q.Name, nx);
                var have = loader || (q.Id > 0 ? _cat.IsSiteInstalled(q.Id) : nx > 0 && _cat.IsInstalled(nx));
                if (q.Id > 0 && !have) missing.Add(q.Id);
                if (nx > 0 && !have) missingNexus.Add(nx);
                StatusText(new Rect(row.x + 14 * _s, row.y, 30 * _s, row.height), "", have ? CGood : q.Id > 0 ? CWarn : CMuted);
                var label = q.Name + (nx > 0 ? "  (Nexus Mods)" : q.Id == 0 ? "  (outside the site)" : "") + (q.Note.Length > 0 ? "  —  " + q.Note : "");
                Lbl(new Rect(row.x + 34 * _s, row.y, row.width - 170 * _s, row.height), Clip(label, 70), _body);
                var b = new Rect(row.xMax - 128 * _s, row.y + 4 * _s, 120 * _s, 36 * _s);
                if (have) Lbl(b, "Installed", Center(_smallBold, CGood));
                else if (q.Id > 0) { if (Community.Busy == q.Id) StatusText(b, "Installing…", CAccent); else if (Btn(b, "View", false)) OpenSiteDetails(new SiteMod { Id = q.Id, Name = q.Name }); }
                else if (nx > 0 && !IsLoaderReq(q.Name, nx)) { if (Btn(b, "Install", true)) InstallFromNexus(nx); }   // the menu's browser
                else if (q.Url.Length > 0 && Btn(b, "Open", false)) Application.OpenURL(q.Url);
                ly += 50 * _s;
            }
            _siteMissing = missing;
            _siteMissingNexus = missingNexus;
            var req = PlainText(d.Requirements);
            var reqText = req.Length > 0 ? req : d.Requires.Count > 0 ? "" : "Just BepInEx (Check BepInEx sets it up).";
            if (reqText.Length > 0)
            {
                var reqH = Mathf.Max(24 * _s, Mathf.Min(_wrapSmall.CalcHeight(Lang.C(reqText), leftW), r.yMax - ly - 70 * _s));
                Lbl(new Rect(r.x, ly, leftW, reqH), reqText, req.Length > 0 ? _wrapSmall : Tint(_small, CMuted));
                ly += reqH + 16 * _s;
            }
            Lbl(new Rect(r.x, ly, leftW, 44 * _s), "Every file on " + Community.HostName + " passed a VirusTotal virus scan before it was listed.", Tint(_wrapSmall, CMuted));

            // Right: title, buttons, description and files
            var rx = pic.xMax + 28 * _s;
            var rw = r.xMax - rx;
            Lbl(new Rect(rx, top - 4 * _s, rw, 36 * _s), m.Name, _h2);
            Lbl(new Rect(rx, top + 34 * _s, rw, 22 * _s),
                $"{m.Category}  ·  by {m.Author}  ·  v{m.Version.TrimStart('v', 'V')}  ·  {Short(m.Downloads)} downloads  ·  {Short(m.Endorsements)} endorsements", Tint(_small, CMuted));
            var by = top + 70 * _s;
            var bx = rx;
            // Install puts the mod (and the website mods it needs) in now; Subscribe (below) does that and keeps it updated.
            if (ModEntry.IsLoaderName(m.Name) || SiteInstalledEntry(m) != null || Community.Busy == m.Id || _siteInstalling == m.Id)
            {
                if (_siteInstalling == m.Id && Community.Busy != m.Id) StatusText(new Rect(bx, by, 160 * _s, 42 * _s), "Installing…", CAccent);
                else SiteStatus(new Rect(bx, by, 160 * _s, 42 * _s), m);
            }
            else if (Btn(new Rect(bx, by, 160 * _s, 42 * _s), "Install", true)) InstallSiteMod(m, d);
            bx += 168 * _s;
            if (Btn(new Rect(bx, by, 92 * _s, 42 * _s), "Page", false)) Application.OpenURL(m.Page);
            // Subscribe: the mod installs now and keeps itself updated; unsubscribing removes it.
            var sb = new Rect(bx + 100 * _s + (d.Mine ? 0 : 148 * _s), by, 170 * _s, 42 * _s);
            // Report a bug: the mod's Bugs tab on the website, signed in to the player's account.
            if ((d.BugReports || d.Mine) && Btn(new Rect(sb.xMax + 8 * _s, by, 160 * _s, 42 * _s), "Report a bug", false))
            {
                OpenSiteSignedIn("/mod/" + m.Id + "?tab=bugs");
                Toast(SiteAccount.Has ? $"Opening {m.Name}'s Bugs page: press Report a bug there." : $"Opening {m.Name}'s Bugs page. Log in on the website to report a bug.", true);
            }
            if (_siteSubscribing == m.Id) StatusText(sb, "…", CAccent);
            else if (Btn(sb, d.Subscribed ? "Subscribed" : "Subscribe", d.Subscribed))
            {
                if (!SiteAccount.Has) { _siteAskAccount = true; CloseSiteDetails(); Toast("Sign in to your website account first (top of Browse mods), then you can subscribe from here.", true); }
                else
                {
                    _siteSubscribing = m.Id;
                    var want = !d.Subscribed;
                    _host.StartCoroutine(SiteAccount.Subscribe(m.Id, want, (ok, on, err) =>
                    {
                        _siteSubscribing = 0;
                        if (!ok) { Toast(err, false); return; }
                        d.Subscribed = on;
                        Toast(on ? $"Subscribed to {m.Name}: it installs now and keeps itself up to date." : $"Unsubscribed from {m.Name}. It's removed if it was installed for the subscription.", true);
                        if (on && _siteMissingNexus.Count > 0) InstallNexusRequirements(_siteMissingNexus, m.Name);
                        _host.StartCoroutine(Community.SyncSubscriptions((msg, good) => Toast(msg, good), () => _cat.Refresh(), force: true));
                    }));
                }
            }
            if (!d.Mine)
            {
                var eb = new Rect(bx + 100 * _s, by, 140 * _s, 42 * _s);
                if (_siteEndorsing == m.Id) StatusText(eb, "…", CAccent);
                else if (Btn(eb, d.Endorsed ? "Endorsed" : "Endorse", d.Endorsed))
                {
                    if (!SiteAccount.Has) { _siteAskAccount = true; CloseSiteDetails(); Toast("Sign in to your website account first (top of Browse mods), then you can endorse from here.", true); }
                    else
                    {
                        _siteEndorsing = m.Id;
                        var want = !d.Endorsed;
                        _host.StartCoroutine(SiteAccount.Endorse(m.Id, want, (ok, on, count, err) =>
                        {
                            _siteEndorsing = 0;
                            if (!ok) { Toast(err, false); return; }
                            d.Endorsed = on; m.Endorsements = count;
                            Toast(on ? $"Endorsed {m.Name}. Thanks — it helps the author!" : $"Took back your endorsement of {m.Name}.", true);
                        }));
                    }
                }
            }

            // Tabs: the description and files, or the pictures. Refresh loads the page and its pictures again.
            var ty = by + 56 * _s;
            var tabs = new[] { "Description & files", d.Images.Count > 0 ? $"Images ({d.Images.Count})" : "Images" };
            var tx = rx;
            for (var t = 0; t < tabs.Length; t++)
            {
                var tw = _bold.CalcSize(Lang.C(tabs[t])).x + 30 * _s;
                if (Btn(new Rect(tx, ty, tw, 36 * _s), tabs[t], false, _siteDetailTab == t)) { _siteDetailTab = t; _siteViewer = -1; _scrollY = 0; }
                tx += tw + 8 * _s;
            }
            if (Btn(new Rect(r.xMax - 130 * _s, ty, 130 * _s, 36 * _s), "Refresh", false))
            {
                Nexus.ClearThumbs(u => u.IndexOf(Community.HostName, StringComparison.OrdinalIgnoreCase) >= 0);
                var keepTab = _siteDetailTab;
                OpenSiteDetails(m);
                _siteDetailTab = keepTab;
                Toast("Refreshing " + m.Name + "…", true);
                return;
            }
            if (_siteDetailTab == 1) { DrawImages(new Rect(rx, ty + 48 * _s, rw, r.yMax - ty - 48 * _s), d.Images, ref _siteViewer); return; }

            var view = new Rect(rx, ty + 48 * _s, rw, r.yMax - ty - 48 * _s);
            var wrapW = rw - 30 * _s;
            var desc = PlainText(d.Description);
            if (desc.Length == 0) desc = m.Summary.Length > 0 ? m.Summary : "No description.";
            var descH = _wrap.CalcHeight(Lang.C(desc), wrapW);
            var vers = d.Versions.Take(8).ToList();
            var verH = vers.Select(v => 64 * _s + (v.Changelog.Length > 0 ? _wrapSmall.CalcHeight(Lang.C(PlainText(v.Changelog)), wrapW - 220 * _s) + 8 * _s : 0)).ToList();
            var inner = BeginScroll(view, descH + 60 * _s + verH.Sum() + vers.Count * 10 * _s + 20 * _s);
            var y = inner.y;
            Lbl(new Rect(0, y, wrapW, descH), desc, _wrap);
            y += descH + 20 * _s;
            Lbl(new Rect(0, y, wrapW, 28 * _s), "Files", Tint(_bold, CAccent));
            y += 40 * _s;
            var entry = _cat.SiteEntry(m.Id);
            for (var i = 0; i < vers.Count; i++)
            {
                var v = vers[i];
                var fr = new Rect(0, y, wrapW, verH[i]);
                if (fr.yMax > -20 && fr.y < view.height + 20)
                {
                    Fill(fr, CRow, 12 * _s);
                    var mine = entry != null && entry.SiteVersionId == v.Id;
                    Lbl(new Rect(fr.x + 16 * _s, fr.y + 8 * _s, fr.width - 220 * _s, 26 * _s),
                        "v" + v.Version.TrimStart('v', 'V') + (i == 0 ? "   (latest)" : "") + (mine ? "   · installed" : ""), Tint(_bold, mine ? CGood : CText));
                    var when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(v.Created).ToLocalTime();
                    Lbl(new Rect(fr.x + 16 * _s, fr.y + 34 * _s, fr.width - 220 * _s, 22 * _s), $"{v.File}  ·  {when:d MMM yyyy}  ·  {v.Size / 1048576f:0.##} MB", Tint(_small, CMuted));
                    if (v.Changelog.Length > 0) Lbl(new Rect(fr.x + 16 * _s, fr.y + 60 * _s, fr.width - 220 * _s, fr.height - 64 * _s), PlainText(v.Changelog), _wrapSmall);
                    var b = new Rect(fr.xMax - 196 * _s, fr.y + 12 * _s, 182 * _s, 40 * _s);
                    if (Community.Busy == m.Id) StatusText(b, "Installing…", CAccent);
                }
                y += verH[i] + 10 * _s;
            }
            EndScroll();
        }

        /// <summary>The mod's pictures: a grid of them with their labels, and one at a time, large, when clicked.</summary>
        private void DrawImages(Rect r, List<KeyValuePair<string, string>> images, ref int viewer)
        {
            if (images.Count == 0) { Lbl(new Rect(r.x, r.y + 10 * _s, r.width, 30 * _s), "This mod has no pictures.", Tint(_body, CMuted)); return; }
            if (viewer >= 0 && viewer < images.Count)
            {
                var it = images[viewer];
                var labelH = it.Value.Length > 0 ? 30 * _s : 0;
                var frame = new Rect(r.x, r.y, r.width, r.height - 56 * _s - labelH);
                Fill(frame, new Color(0.06f, 0.07f, 0.1f, 1f), 12 * _s);
                var big = Nexus.Thumb(_host, it.Key);
                if (big != null) GUI.DrawTexture(new Rect(frame.x + 8 * _s, frame.y + 8 * _s, frame.width - 16 * _s, frame.height - 16 * _s), big, ScaleMode.ScaleToFit, true);
                else Lbl(frame, "Loading picture…", Center(_small, CMuted));
                if (labelH > 0) Lbl(new Rect(r.x, frame.yMax + 6 * _s, r.width, 24 * _s), it.Value, Center(_body, CText));
                var y = r.yMax - 46 * _s;
                if (Btn(new Rect(r.x, y, 120 * _s, 42 * _s), "‹  Back", false) || (viewer > 0 && KeyNow(KeyCode.LeftArrow))) { if (viewer > 0) viewer--; }
                Lbl(new Rect(r.x + 130 * _s, y, r.width - 400 * _s, 42 * _s), $"{viewer + 1} of {images.Count}", Center(_small, CMuted));
                if (Btn(new Rect(r.xMax - 260 * _s, y, 120 * _s, 42 * _s), "Next  ›", false) || KeyNow(KeyCode.RightArrow)) { if (viewer < images.Count - 1) viewer++; }
                if (Btn(new Rect(r.xMax - 130 * _s, y, 130 * _s, 42 * _s), "All pictures", false)) viewer = -1;
                return;
            }
            var cols = r.width > 900 * _s ? 3 : 2;
            var gap = 12 * _s;
            var tileW = (r.width - 30 * _s - gap * (cols - 1)) / cols;
            var tileH = tileW * 9f / 16f + 30 * _s;
            var rows = (images.Count + cols - 1) / cols;
            var inner = BeginScroll(r, rows * (tileH + gap));
            for (var i = 0; i < images.Count; i++)
            {
                var tile = new Rect((i % cols) * (tileW + gap), inner.y + (i / cols) * (tileH + gap), tileW, tileH);
                if (tile.yMax < -20 || tile.y > r.height + 20) continue;
                var img = new Rect(tile.x, tile.y, tileW, tileW * 9f / 16f);
                Fill(img, CRow, 10 * _s);
                var t = Nexus.Thumb(_host, images[i].Key);
                if (t != null) GUI.DrawTexture(img, t, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 10 * _s);
                else Lbl(img, "…", Center(_small, CMuted));
                Lbl(new Rect(tile.x, img.yMax + 4 * _s, tileW, 24 * _s), Clip(images[i].Value.Length > 0 ? images[i].Value : "Picture " + (i + 1), 48), Tint(_small, CMuted));
                if (Hit(img)) viewer = i;
            }
            EndScroll();
        }

        private static bool KeyNow(KeyCode k)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || e.keyCode != k) return false;
            e.Use();
            return true;
        }

        private int _siteInstalling;   // a mod whose Install was pressed (its page is being read for what it needs)

        /// <summary>The installed copy of a website mod: from the site, or the same mod installed from Nexus.</summary>
        private ModEntry SiteInstalledEntry(SiteMod m) =>
            _cat.SiteEntry(m.Id) ?? _cat.Mods.FirstOrDefault(e => e.AltSiteId == m.Id && e.State != ModState.WillRemove);

        /// <summary>
        /// Install on Browse mods: the mod and the website mods it needs that aren't installed (from its page), then the
        /// Nexus mods it needs open one after another. A save backup is made first.
        /// </summary>
        private void InstallSiteMod(SiteMod m, SiteDetails known = null)
        {
            if (Community.Busy != 0 || _siteInstalling != 0) { Toast("Another mod is still installing — one moment.", false); return; }
            if (known != null) { InstallSiteModWith(m, known); return; }
            _siteInstalling = m.Id;
            _host.StartCoroutine(Community.Details(m.Id, (d, err) =>
            {
                _siteInstalling = 0;
                if (d == null) { Toast(err ?? $"Couldn't read {m.Name}'s page. Try again.", false); return; }
                _cat.SetSiteInfo(d.Mod);
                InstallSiteModWith(m, d);
            }));
        }

        private void InstallSiteModWith(SiteMod m, SiteDetails d)
        {
            var ids = new List<int>();
            var nexus = new List<int>();
            foreach (var q in d.Requires)
            {
                var nx = q.Id == 0 ? NexusIdOf(q.Url) : 0;
                if (IsLoaderReq(q.Name, nx)) continue;   // BepInEx: skipped, it's already there
                if (q.Id > 0 && q.Id != m.Id && !_cat.IsSiteInstalled(q.Id)) ids.Add(q.Id);
                else if (nx > 0 && !_cat.IsInstalled(nx)) nexus.Add(nx);
            }
            ids.Add(m.Id);
            if (ids.Count == 1) InstallFromSite(m.Id, 0, m.Name);   // (makes the save backup)
            else { Toast(Lang.F("Installing {0} and {1} mod(s) it needs…", m.Name, ids.Count - 1), true); InstallSiteMany(ids.Distinct().ToList(), "Installed"); }
            if (nexus.Count > 0) InstallNexusRequirements(nexus, m.Name);
        }

        private void InstallFromSite(int modId, int versionId, string name, Action afterOk = null)
        {
            if (Community.Busy != 0) { Toast("Another mod is still installing — one moment.", false); return; }
            SaveBackups.Create("before installing a mod", true);
            Toast($"Installing {name}…", true);
            _host.StartCoroutine(Community.Install(modId, versionId, (ok, msg) =>
            {
                Toast(msg, ok);
                if (ok) afterOk?.Invoke();
                _cat.Refresh();
            }));
        }

        private List<int> _siteMissing = new List<int>();
        private int _siteSubscribing;   // the mod whose subscription is being changed
        private bool _siteAskAccount;   // show the "make an account?" box again (after "Not now"), e.g. when Endorse is pressed
        private int _siteEndorsing;

        private void SignInSite()
        {
            _host.StartCoroutine(SiteAccount.SignIn((ok, msg) =>
            {
                Toast(msg, ok);
                if (ok) { _siteAskAccount = false; _host.StartCoroutine(SiteAccount.Refresh()); if (_siteDetails != null) OpenSiteDetails(_siteDetails.Mod); }
            }));
        }

        /// <summary>Opens a page of the website in the browser, signed in to the player's account when they have one.</summary>
        private void OpenSiteSignedIn(string page)
        {
            _host.StartCoroutine(SiteAccount.OpenSignedIn(page, (ok, msg) => { if (msg.Length > 0) Toast(msg, ok); }));
        }

        /// <summary>Installs or updates several website mods, one after another.</summary>
        private Dictionary<int, Action> _afterSiteInstall = new Dictionary<int, Action>();

        private void InstallSiteMany(List<int> ids, string verb = "Updated")
        {
            if (Community.Busy != 0) { Toast("Another mod is still installing — one moment.", false); return; }
            SaveBackups.Create(verb == "Updated" ? "before updating mods" : "before installing mods", true);
            _host.StartCoroutine(InstallSiteManyRun(ids, verb));
        }

        private IEnumerator InstallSiteManyRun(List<int> ids, string verb)
        {
            var done = 0;
            foreach (var id in ids)
            {
                var ok = false;
                yield return Community.Install(id, 0, (o, msg) => { ok = o; if (!o) Toast(msg, false); });
                if (ok) done++;
                if (_afterSiteInstall.TryGetValue(id, out var after)) { _afterSiteInstall.Remove(id); if (ok) after(); }
            }
            _cat.Refresh();
            if (done > 0) Toast($"{verb} {done} mod(s) from {Community.HostName}." + (_cat.Mods.Any(m => m.UpdateStaged && m.LiveLater) ? " Press Reload to finish." : _cat.Mods.Any(m => m.UpdateStaged) ? " Restart to finish." : ""), true);
        }
    }
}

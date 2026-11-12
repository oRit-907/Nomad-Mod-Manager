using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// Mod packs: sets of mods from the website that players subscribe to in one go. Browse them, subscribe, and make
    /// and edit your own packs (from your installed mods, or by searching the site) without leaving the game.
    /// Subscribing works like subscribing to every mod in the pack: they install now and keep themselves updated.
    /// </summary>
    internal sealed partial class ModsMenu
    {
        private readonly List<SitePack> _packs = new List<SitePack>();
        private bool _packsLoading, _packsLoaded, _packsMineView;
        private string _packsError;
        private SitePack _pack;                 // the pack whose page is open
        private int _packLoading;               // the pack being loaded (0 = none)
        private int _packBusy;                  // a pack being changed (subscribe, add, remove, delete)
        private bool _packEditing;              // editing your own pack: Remove buttons and "Add mods"
        private bool _packNaming;               // typing a name for a new pack, or a new name
        private int _packRenaming;              // the pack being renamed (0 = a new pack)
        private string _packName = "";
        private bool _packFromInstalled = true; // a new pack starts with your installed website mods
        private string _packSearch = "";
        private bool _packSearchEnter, _packSearching;
        private readonly List<SiteMod> _packFound = new List<SiteMod>();
        private readonly List<NexusMod> _packNexusFound = new List<NexusMod>();   // Nexus Mods results for the same search
        private float _packDeleteArmed;         // Delete is pressed twice
        private float _packListScroll;

        private void LoadPacks()
        {
            if (_packsLoading) return;
            _packsLoading = true; _packsError = null;
            _host.StartCoroutine(SitePacks.List((list, err) =>
            {
                _packsLoading = false; _packsLoaded = true;
                if (err != null) { _packsError = err; return; }
                _packs.Clear(); _packs.AddRange(list);
            }));
            if (SiteAccount.Has) _host.StartCoroutine(SitePacks.LoadMine(err => { }));
        }

        private void OpenPack(int id, bool edit = false)
        {
            _packListScroll = _scrollY; _scrollY = 0;
            _pack = null; _packLoading = id; _packEditing = edit; _packDeleteArmed = 0;
            _packSearch = ""; _packFound.Clear(); _packNexusFound.Clear();
            _host.StartCoroutine(SitePacks.Details(id, (p, err) =>
            {
                if (_packLoading != id) return;
                _packLoading = 0;
                if (p == null) { Toast(err ?? "Couldn't load the pack.", false); ClosePack(); return; }
                _pack = p;
                foreach (var m in p.ModList) _cat.SetSiteInfo(m);
            }));
        }

        private void ClosePack()
        {
            _pack = null; _packLoading = 0; _packEditing = false; _packNaming = false;
            _scrollY = _packListScroll;
        }

        private bool IsMyPack(int id) => SitePacks.Mine.Any(p => p.Id == id);

        private void SubscribePack(SitePack p)
        {
            if (!SiteAccount.Has) { NeedAccount("subscribe to packs"); return; }
            if (_packBusy != 0) return;
            _packBusy = p.Id;
            var want = !p.Subscribed;
            _host.StartCoroutine(SitePacks.Subscribe(p.Id, want, (ok, on, err) =>
            {
                _packBusy = 0;
                if (!ok) { Toast(err, false); return; }
                p.Subscribed = on;
                foreach (var x in _packs.Where(x => x.Id == p.Id)) x.Subscribed = on;
                if (on) p.Subscribers++; else p.Subscribers = Math.Max(0, p.Subscribers - 1);
                Toast(on ? $"Subscribed to {p.Name}: its {p.Mods} mods install now and keep themselves updated."
                         : $"Unsubscribed from {p.Name}. Its mods are removed, except ones you subscribed to some other way.", true);
                // Its mods from Nexus: the missing ones are installed from Nexus, one after another.
                if (on) InstallNexusRequirements(p.Nexus.Where(n => !_cat.IsInstalled(n.Id)).Select(n => n.Id).ToList(), p.Name);
                _host.StartCoroutine(Community.SyncSubscriptions((msg, good) => Toast(msg, good), () => _cat.Refresh(), force: true));
            }));
        }

        private void NeedAccount(string what)
        {
            _siteAskAccount = true;
            Toast($"Sign in to your website account first (top of Browse mods), then you can {what}.", true);
            ShowTab(TabSite);
        }

        /// <summary>Website mods you have installed (they can start a new pack, or be added to one).</summary>
        private List<SiteInstall> InstalledSiteMods() =>
            Community.Installed().Where(i => i.Id > 0 && !ModEntry.IsLoaderName(i.Name)).GroupBy(i => i.Id).Select(g => g.First()).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        private void StartNewPack()
        {
            if (!SiteAccount.Has) { NeedAccount("make packs"); return; }
            _packNaming = true; _packRenaming = 0;
            _packName = (SiteAccount.Name.Length > 0 ? SiteAccount.Name + "'s" : "My") + " mods";
        }

        private void FinishNaming()
        {
            var name = _packName.Trim();
            if (name.Length < 3) { Toast("Give the pack a name (at least 3 letters).", false); return; }
            _packNaming = false;
            if (_packRenaming != 0)
            {
                var id = _packRenaming;
                _host.StartCoroutine(SitePacks.Rename(id, name, (p, err) =>
                {
                    if (p == null) { Toast(err, false); return; }
                    if (_pack != null && _pack.Id == id) _pack.Name = p.Name;
                    foreach (var x in _packs.Where(x => x.Id == id)) x.Name = p.Name;
                    Toast("Pack renamed.", true);
                }));
                return;
            }
            var mods = _packFromInstalled ? InstalledSiteMods().Select(i => i.Id).ToList() : new List<int>();
            _packBusy = -1;
            _host.StartCoroutine(SitePacks.Create(name, mods, (p, err) =>
            {
                _packBusy = 0;
                if (p == null) { Toast(err, false); return; }
                Toast(mods.Count > 0 ? $"Made {p.Name} with {p.Mods} of your mods. Others can subscribe to it on the website and in the game." : $"Made {p.Name}. Now add its mods.", true);
                _packsMineView = true;
                LoadPacks();
                OpenPack(p.Id, edit: true);
            }));
        }

        private void PackAdd(SitePack p, int modId, string name)
        {
            if (_packBusy != 0) return;
            _packBusy = p.Id;
            _host.StartCoroutine(SitePacks.Add(p.Id, modId, (np, err) =>
            {
                _packBusy = 0;
                if (np == null) { Toast(err, false); return; }
                Toast($"Added {name} to {p.Name}. Subscribers get it at their next sync.", true);
                if (_pack != null && _pack.Id == p.Id) OpenPackKeepEditing();
            }));
        }

        private const int NexusLoaderId = 7;   // BepInEx Pack on Nexus: every player has it already, so it isn't added to packs

        private void PackAddNexus(SitePack p, int nexusId, string name)
        {
            if (_packBusy != 0) return;
            _packBusy = p.Id;
            _host.StartCoroutine(SitePacks.AddNexus(p.Id, nexusId, (np, err) =>
            {
                _packBusy = 0;
                if (np == null) { Toast(err, false); return; }
                Toast($"Added {name} (Nexus Mods) to {p.Name}.", true);
                if (_pack != null && _pack.Id == p.Id) OpenPackKeepEditing();
            }));
        }

        private void PackRemoveNexus(SitePack p, PackNexus n)
        {
            if (_packBusy != 0) return;
            _packBusy = p.Id;
            _host.StartCoroutine(SitePacks.RemoveNexus(p.Id, n.Id, (np, err) =>
            {
                _packBusy = 0;
                if (np == null) { Toast(err, false); return; }
                p.Nexus.RemoveAll(x => x.Id == n.Id);
                Toast($"Removed {n.Name} from {p.Name}.", true);
            }));
        }

        private void PackRemove(SitePack p, SiteMod m)
        {
            if (_packBusy != 0) return;
            _packBusy = p.Id;
            _host.StartCoroutine(SitePacks.Remove(p.Id, m.Id, (np, err) =>
            {
                _packBusy = 0;
                if (np == null) { Toast(err, false); return; }
                p.ModList.RemoveAll(x => x.Id == m.Id); p.Mods = p.ModList.Count;
                Toast($"Removed {m.Name} from {p.Name}.", true);
            }));
        }

        /// <summary>Reloads the open pack's page after a change, staying in edit mode and where you were.</summary>
        private void OpenPackKeepEditing()
        {
            var id = _pack.Id; var scroll = _scrollY; var search = _packSearch; var found = _packFound.ToList(); var nfound = _packNexusFound.ToList();
            _host.StartCoroutine(SitePacks.Details(id, (p, err) =>
            {
                if (p == null || _pack == null || _pack.Id != id) return;
                _pack = p; _scrollY = scroll; _packSearch = search;
                _packFound.Clear(); _packFound.AddRange(found);
                _packNexusFound.Clear(); _packNexusFound.AddRange(nfound);
            }));
        }

        private void RunPackSearch()
        {
            if (_packSearching || _packSearch.Trim().Length == 0) return;
            _packSearching = true;
            var text = _packSearch.Trim();
            var waiting = 2; string firstErr = null;
            void Finished(string err)
            {
                if (err != null && firstErr == null) firstErr = err;
                if (--waiting > 0) return;
                _packSearching = false;
                if (_packFound.Count == 0 && _packNexusFound.Count == 0)
                    Toast(firstErr ?? "No mods on the website or Nexus Mods match \"" + text + "\".", false);
            }
            _packFound.Clear(); _packNexusFound.Clear();
            _host.StartCoroutine(Community.Search(text, 0, null, 1, (list, total, counts, err) =>
            {
                if (err == null) _packFound.AddRange(list.Where(m => !ModEntry.IsLoaderName(m.Name)).Take(12));
                Finished(err);
            }));
            _host.StartCoroutine(Nexus.Search(text, 0, 0, 12, (list, total, err) =>
            {
                if (err == null && list != null)
                    _packNexusFound.AddRange(list.Where(n => !n.Hidden && n.Id != NexusLoaderId && n.Id != Nexus.DownloaderModId && !ModEntry.IsLoaderName(n.Name)).Take(8));
                Finished(err);
            }));
        }

        // ------------------------------------------------------------------ the list

        private void DrawPacks(Rect r)
        {
            if (!_packsLoaded && !_packsLoading) LoadPacks();
            Lbl(new Rect(r.x, r.y, r.width, 36 * _s), "Mod packs", _h2);
            var hx = r.xMax - 190 * _s;
            if (Btn(new Rect(hx, r.y + 4 * _s, 190 * _s, 40 * _s), "+  Make a pack", true) && _packBusy == 0) StartNewPack();
            hx -= 138 * _s;
            if (Btn(new Rect(hx, r.y + 4 * _s, 130 * _s, 40 * _s), _packsLoading ? "Refreshing…" : "Refresh", false) && !_packsLoading) LoadPacks();
            Lbl(new Rect(r.x, r.y + 34 * _s, hx - r.x - 12 * _s, 44 * _s),
                "Sets of mods put together by players. Subscribe to a pack and every mod in it installs now and keeps itself updated, including mods added to the pack later.",
                Tint(_wrapSmall, CMuted));

            var y = r.y + 86 * _s;
            if (_packNaming && _packRenaming == 0) { DrawPackNaming(new Rect(r.x, y, r.width, 0), ref y); }

            // All packs / My packs
            var tabs = new[] { "All packs", SiteAccount.Has ? $"My packs ({SitePacks.Mine.Count})" : "My packs" };
            var tx = r.x;
            for (var i = 0; i < 2; i++)
            {
                var w = _bold.CalcSize(Lang.C(tabs[i])).x + 30 * _s;
                if (Btn(new Rect(tx, y, w, 38 * _s), tabs[i], false, _packsMineView == (i == 1)))
                {
                    if (i == 1 && !SiteAccount.Has) NeedAccount("make packs");
                    else { _packsMineView = i == 1; _scrollY = 0; }
                }
                tx += w + 8 * _s;
            }
            y += 52 * _s;

            var view = new Rect(r.x, y, r.width, r.yMax - y);
            if (_packsError != null && _packs.Count == 0)
            {
                Lbl(new Rect(view.x, view.y, view.width, 50 * _s), _packsError, Tint(_wrap, CBad));
                if (Btn(new Rect(view.x, view.y + 56 * _s, 140 * _s, 40 * _s), "Try again", true)) LoadPacks();
                return;
            }
            var list = _packsMineView
                ? SitePacks.Mine.Select(m => _packs.FirstOrDefault(p => p.Id == m.Id) is SitePack pub ? Merge(pub, m) : m).ToList()
                : _packs;
            if (list.Count == 0)
            {
                Lbl(new Rect(view.x, view.y, view.width, 60 * _s), _packsLoading ? "Loading packs…"
                    : _packsMineView ? "You haven't made a pack yet. Press Make a pack: it can start with the website mods you have installed."
                    : "No mod packs yet. Make the first one!", Tint(_wrap, CMuted));
                return;
            }

            const float cardH = 156, gap = 14;
            var cols = view.width > 1300 * _s ? 3 : 2;
            var colW = (view.width - 18 * _s - (cols - 1) * gap * _s) / cols;
            var rows = (list.Count + cols - 1) / cols;
            var inner = BeginScroll(view, rows * (cardH + gap) * _s);
            for (var i = 0; i < list.Count; i++)
            {
                var cr = new Rect((i % cols) * (colW + gap * _s), inner.y + (i / cols) * (cardH + gap) * _s, colW, cardH * _s);
                if (cr.yMax < -50 || cr.y > view.height + 50) continue;
                PackCard(cr, list[i]);
            }
            EndScroll();
        }

        private static SitePack Merge(SitePack pub, SitePack mine)
        {
            pub.ModIds = mine.ModIds; pub.Live = mine.Live;
            return pub;
        }

        private void DrawPackNaming(Rect r, ref float y)
        {
            var renaming = _packRenaming != 0;
            var box = new Rect(r.x, y, r.width, (renaming ? 74 : 112) * _s);
            Fill(box, Alpha(CAccent, 0.12f), 12 * _s);
            var field = new Rect(box.x + 16 * _s, box.y + 14 * _s, box.width - 420 * _s, 44 * _s);
            Fill(field, CField, 11 * _s);
            var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
            Lbl(new Rect(field.x + 16 * _s, field.y, field.width - 30 * _s, field.height), _packName.Length > 0 ? _packName + caret : "Type the pack's name", Tint(_body, _packName.Length > 0 ? CText : CMuted));
            if (Btn(new Rect(field.xMax + 10 * _s, field.y, 200 * _s, 44 * _s), _packBusy == -1 ? "Making…" : renaming ? "Rename" : "Make the pack", true) && _packBusy == 0) FinishNaming();
            if (Btn(new Rect(field.xMax + 218 * _s, field.y, 170 * _s, 44 * _s), "Cancel", false)) _packNaming = false;
            if (!renaming)
            {
                var n = InstalledSiteMods().Count;
                var tick = new Rect(box.x + 16 * _s, box.y + 68 * _s, box.width - 32 * _s, 32 * _s);
                Lbl(tick, (_packFromInstalled ? "☑  " : "☐  ") + (n > 0 ? $"Start with the {n} website mods I have installed" : "Start empty (you have no website mods installed yet)"),
                    Tint(_body, tick.Contains(Event.current.mousePosition) ? CAccent : CText));
                if (Hit(tick) && n > 0) _packFromInstalled = !_packFromInstalled;
            }
            y += box.height + 12 * _s;
        }

        private void PackCard(Rect r, SitePack p)
        {
            var hover = r.Contains(Event.current.mousePosition);
            Fill(r, hover ? Lighten(CCard, 0.03f) : CCard, 14 * _s);
            var th = r.height - 24 * _s;
            var cover = new Rect(r.x + 12 * _s, r.y + 12 * _s, th, th);
            DrawPackCover(cover, p.Pictures);
            var x = cover.xMax + 16 * _s;
            var w = r.xMax - x - 14 * _s;
            Lbl(new Rect(x, r.y + 10 * _s, w, 28 * _s), p.Name, _bold);
            var mine = IsMyPack(p.Id);
            Lbl(new Rect(x, r.y + 38 * _s, w, 22 * _s), $"by {p.Author}{(mine ? " (you)" : "")}  ·  {p.Mods} mods  ·  {Short(p.Subscribers)} subscribers" + (p.Live ? "" : "  ·  hidden"), Tint(_small, p.Live ? CMuted : CWarn));
            Lbl(new Rect(x, r.y + 62 * _s, w, 40 * _s), Clip(p.Summary, 110), _wrapSmall);
            var by = r.yMax - 52 * _s;
            if (mine)
            {
                if (Btn(new Rect(x, by, 120 * _s, 40 * _s), "Edit", true)) OpenPack(p.Id, edit: true);
            }
            else if (_packBusy == p.Id) StatusText(new Rect(x, by, 170 * _s, 40 * _s), "…", CAccent);
            else if (Btn(new Rect(x, by, 170 * _s, 40 * _s), p.Subscribed ? "Subscribed" : "Subscribe", !p.Subscribed)) SubscribePack(p);
            if (Btn(new Rect(x + (mine ? 128 : 178) * _s, by, 108 * _s, 40 * _s), "Details", false)) OpenPack(p.Id);
            if (Hit(r)) OpenPack(p.Id);
        }

        /// <summary>Up to four of the pack's mod pictures, in a grid.</summary>
        private void DrawPackCover(Rect r, List<string> pics)
        {
            Fill(r, CRow, 10 * _s);
            var n = Mathf.Min(4, pics.Count);
            if (n == 0) { Lbl(r, "📦", Center(Size(_h2, 40), CMuted)); return; }
            var half = (r.width - 2 * _s) / 2;
            for (var i = 0; i < n; i++)
            {
                Rect c;
                if (n == 1) c = r;
                else if (n == 2) c = new Rect(r.x + i * (half + 2 * _s), r.y, half, r.height);
                else if (n == 3 && i == 0) c = new Rect(r.x, r.y, half, r.height);
                else { var k = n == 3 ? i + 1 : i; c = new Rect(r.x + (k % 2) * (half + 2 * _s), r.y + (k / 2) * (half + 2 * _s), half, half); }
                var tex = Nexus.Thumb(_host, pics[i]);
                if (tex != null) GUI.DrawTexture(c, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 8 * _s);
            }
        }

        // ------------------------------------------------------------------ one pack

        private void DrawPack(Rect r)
        {
            if (Btn(new Rect(r.x, r.y, 190 * _s, 40 * _s), "‹  Back to packs", false)) { ClosePack(); return; }
            var p = _pack;
            if (p == null) { Lbl(new Rect(r.x, r.y + 60 * _s, r.width, 40 * _s), "Loading the pack…", Tint(_body, CMuted)); return; }
            var mine = IsMyPack(p.Id);

            var top = r.y + 56 * _s;
            var cover = new Rect(r.x, top, 170 * _s, 170 * _s);
            DrawPackCover(cover, p.ModList.Select(m => m.Card.Length > 0 ? m.Card : m.Thumb).Concat(p.Nexus.Select(n => n.Thumb)).Where(u => u.Length > 0).Take(4).ToList());
            var x = cover.xMax + 22 * _s;
            var w = r.xMax - x;
            Lbl(new Rect(x, top - 4 * _s, w, 36 * _s), p.Name, _h2);
            Lbl(new Rect(x, top + 34 * _s, w, 22 * _s), $"Mod pack by {p.Author}{(mine ? " (you)" : "")}  ·  {p.ModList.Count + p.Nexus.Count} mods  ·  {Short(p.Subscribers)} subscribers", Tint(_small, CMuted));
            if (p.Summary.Length > 0) Lbl(new Rect(x, top + 60 * _s, w, 44 * _s), p.Summary, _wrapSmall);

            var bx = x; var by = top + 118 * _s;
            bool B(string text, bool primary, float bw)
            {
                var hit = Btn(new Rect(bx, by, bw * _s, 42 * _s), text, primary);
                bx += (bw + 8) * _s;
                return hit;
            }
            if (_packBusy == p.Id) { StatusText(new Rect(bx, by, 220 * _s, 42 * _s), "…", CAccent); bx += 228 * _s; }
            else if (p.ModList.Count + p.Nexus.Count > 0 && B(p.Subscribed ? "Subscribed to the pack" : "Subscribe to the pack", !p.Subscribed, 250)) SubscribePack(p);
            if (mine)
            {
                if (B(_packEditing ? "Done editing" : "Edit", _packEditing, 150)) { _packEditing = !_packEditing; _packNaming = false; }
                if (_packEditing)
                {
                    if (B("Rename", false, 120)) { _packNaming = true; _packRenaming = p.Id; _packName = p.Name; }
                    var armed = Time.unscaledTime - _packDeleteArmed < 4f;
                    if (B(armed ? "Press again to delete" : "Delete pack", false, armed ? 230 : 150))
                    {
                        if (!armed) _packDeleteArmed = Time.unscaledTime;
                        else
                        {
                            _packDeleteArmed = 0;
                            var id = p.Id; var name = p.Name;
                            _host.StartCoroutine(SitePacks.Delete(id, (ok, err) =>
                            {
                                if (!ok) { Toast(err, false); return; }
                                _packs.RemoveAll(x => x.Id == id);
                                Toast($"Deleted {name}. Its subscribers' mods are removed at their next sync, unless they have them some other way.", true);
                                ClosePack();
                            }));
                        }
                    }
                }
            }
            if (B("On the website", false, 180)) OpenSiteSignedIn("/pack/" + p.Id);

            var y = top + 186 * _s;
            if (_packNaming && _packRenaming == p.Id) DrawPackNaming(new Rect(r.x, y, r.width, 0), ref y);
            var note = p.Subscribed
                ? "Subscribed: every mod in this pack is installed and kept updated, and mods added to the pack later are installed too."
                : mine && _packEditing ? "Changes reach subscribers at their next sync."
                : "Subscribe once and all of these install now, keep themselves updated, and come off again if you unsubscribe.";
            Lbl(new Rect(r.x, y, r.width, 24 * _s), note, Tint(_small, p.Subscribed ? CGood : CMuted));
            y += 32 * _s;

            // The mods, then (when editing) ways to add more
            var installed = _packEditing ? InstalledSiteMods().Where(i => p.ModList.All(m => m.Id != i.Id)).ToList() : new List<SiteInstall>();
            const float rowH = 66, gap = 8;
            var desc = p.Description.Trim();
            var descH = desc.Length > 0 && !_packEditing ? Mathf.Min(_wrapSmall.CalcHeight(Lang.C(desc), r.width - 30 * _s), 400 * _s) + 20 * _s : 0;
            var nexusInstalled = _packEditing
                ? _cat.Mods.Where(m => m.NexusId > 0 && m.SiteId == 0 && !m.IsSelf && !m.IsLoader && m.NexusId != Nexus.DownloaderModId && p.Nexus.All(n => n.Id != m.NexusId))
                    .GroupBy(m => m.NexusId).Select(g => g.First()).ToList()
                : new List<ModEntry>();
            var addH = _packEditing ? (60 + 50) * _s + _packFound.Count * (rowH + gap) * _s
                + (_packNexusFound.Count > 0 ? 44 * _s + _packNexusFound.Count * (rowH + gap) * _s : 0)
                + (installed.Count > 0 ? 44 * _s + installed.Count * (rowH + gap) * _s : 0)
                + (nexusInstalled.Count > 0 ? 44 * _s + nexusInstalled.Count * (rowH + gap) * _s : 0) : 0;
            var nexusH = p.Nexus.Count > 0 ? 50 * _s + p.Nexus.Count * (rowH + gap) * _s : 0;
            var contentH = descH + 40 * _s + Mathf.Max(1, p.ModList.Count) * (rowH + gap) * _s + nexusH + addH;
            var view = new Rect(r.x, y, r.width, r.yMax - y);
            var inner = BeginScroll(view, contentH);
            var iy = inner.y;
            if (descH > 0) { Lbl(new Rect(0, iy, inner.width - 12 * _s, descH - 20 * _s), desc, _wrapSmall); iy += descH; }
            Lbl(new Rect(0, iy, inner.width, 30 * _s), $"Mods in this pack ({p.ModList.Count})", _bold);
            iy += 40 * _s;
            if (p.ModList.Count == 0) { Lbl(new Rect(0, iy, inner.width, 30 * _s), mine ? "None yet. Add some below." : "No mods yet.", Tint(_body, CMuted)); iy += (rowH + gap) * _s; }
            foreach (var m in p.ModList.ToList())
            {
                var row = new Rect(0, iy, inner.width, rowH * _s);
                if (row.yMax > -40 && row.y < view.height + 40) PackModRow(row, m, p, _packEditing ? "Remove" : null);
                iy += (rowH + gap) * _s;
            }
            if (p.Nexus.Count > 0)
            {
                iy += 10 * _s;
                Lbl(new Rect(0, iy, inner.width, 30 * _s), $"From Nexus Mods ({p.Nexus.Count})", _bold);
                iy += 40 * _s;
                foreach (var n in p.Nexus.ToList())
                {
                    var row = new Rect(0, iy, inner.width, rowH * _s);
                    if (row.yMax > -40 && row.y < view.height + 40) PackNexusRow(row, n, p, mine && _packEditing ? "Remove" : null);
                    iy += (rowH + gap) * _s;
                }
            }
            if (_packEditing)
            {
                iy += 10 * _s;
                Lbl(new Rect(0, iy, inner.width, 30 * _s), "Add mods", _bold);
                iy += 38 * _s;
                var field = new Rect(0, iy, inner.width - 140 * _s, 42 * _s);
                Fill(field, CField, 11 * _s);
                var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
                Lbl(new Rect(field.x + 16 * _s, field.y, field.width - 30 * _s, field.height), _packSearch.Length > 0 ? _packSearch + caret : "Search the website and Nexus Mods — just start typing", Tint(_body, _packSearch.Length > 0 ? CText : CMuted));
                if (Btn(new Rect(field.xMax + 10 * _s, iy, 120 * _s, 42 * _s), _packSearching ? "…" : "Search", true) || _packSearchEnter) { _packSearchEnter = false; RunPackSearch(); }
                iy += 54 * _s;
                foreach (var m in _packFound)
                {
                    var row = new Rect(0, iy, inner.width, rowH * _s);
                    if (row.yMax > -40 && row.y < view.height + 40) PackModRow(row, m, p, p.ModList.Any(x => x.Id == m.Id) ? "" : "Add");
                    iy += (rowH + gap) * _s;
                }
                if (_packNexusFound.Count > 0)
                {
                    Lbl(new Rect(0, iy + 6 * _s, inner.width, 30 * _s), "From Nexus Mods", Tint(_smallBold, CMuted));
                    iy += 44 * _s;
                    foreach (var n in _packNexusFound)
                    {
                        var row = new Rect(0, iy, inner.width, rowH * _s);
                        if (row.yMax > -40 && row.y < view.height + 40)
                            PackNexusRow(row, new PackNexus { Id = n.Id, Name = n.Name, Author = n.Author, Thumb = n.Thumb }, p, p.Nexus.Any(x => x.Id == n.Id) ? "" : "Add");
                        iy += (rowH + gap) * _s;
                    }
                }
                if (installed.Count > 0)
                {
                    Lbl(new Rect(0, iy + 6 * _s, inner.width, 30 * _s), "Website mods you have installed", Tint(_smallBold, CMuted));
                    iy += 44 * _s;
                    foreach (var i in installed)
                    {
                        var row = new Rect(0, iy, inner.width, rowH * _s);
                        if (row.yMax > -40 && row.y < view.height + 40)
                        {
                            Fill(row, row.Contains(Event.current.mousePosition) ? CCard : CRow, 12 * _s);
                            Lbl(new Rect(row.x + 18 * _s, row.y + 8 * _s, row.width - 200 * _s, 28 * _s), i.Name, _bold);
                            Lbl(new Rect(row.x + 18 * _s, row.y + 36 * _s, row.width - 200 * _s, 22 * _s), (i.Author.Length > 0 ? "by " + i.Author + "  ·  " : "") + "v" + i.Version.TrimStart('v', 'V'), Tint(_small, CMuted));
                            if (Btn(new Rect(row.xMax - 140 * _s, row.y + (row.height - 40 * _s) / 2, 126 * _s, 40 * _s), "Add", true)) PackAdd(p, i.Id, i.Name);
                        }
                        iy += (rowH + gap) * _s;
                    }
                }
                if (nexusInstalled.Count > 0)
                {
                    Lbl(new Rect(0, iy + 6 * _s, inner.width, 30 * _s), "Nexus Mods mods you have installed", Tint(_smallBold, CMuted));
                    iy += 44 * _s;
                    foreach (var m in nexusInstalled)
                    {
                        var row = new Rect(0, iy, inner.width, rowH * _s);
                        if (row.yMax > -40 && row.y < view.height + 40)
                            PackNexusRow(row, new PackNexus { Id = m.NexusId, Name = m.Name, Author = m.Author, Thumb = "" }, p, "Add");
                        iy += (rowH + gap) * _s;
                    }
                }
            }
            EndScroll();
        }

        /// <summary>A Nexus mod in a pack: picture, name, and Installed or Install (from Nexus).</summary>
        private void PackNexusRow(Rect row, PackNexus n, SitePack p, string action)
        {
            Fill(row, row.Contains(Event.current.mousePosition) ? CCard : CRow, 12 * _s);
            var pic = new Rect(row.x + 8 * _s, row.y + 6 * _s, row.height - 12 * _s, row.height - 12 * _s);
            Fill(pic, CField, 8 * _s);
            var tex = n.Thumb.Length > 0 ? Nexus.Thumb(_host, n.Thumb) : null;
            if (tex != null) GUI.DrawTexture(pic, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 8 * _s);
            var x = pic.xMax + 14 * _s;
            var right = row.xMax - 12 * _s;
            var by = row.y + (row.height - 40 * _s) / 2;
            if (action == "Remove") { right -= 120 * _s; if (Btn(new Rect(right, by, 120 * _s, 40 * _s), "Remove", false)) PackRemoveNexus(p, n); right -= 8 * _s; }
            else if (action == "Add") { right -= 120 * _s; if (Btn(new Rect(right, by, 120 * _s, 40 * _s), "Add", true)) PackAddNexus(p, n.Id, n.Name); right -= 8 * _s; }
            else if (action == "") { right -= 130 * _s; StatusText(new Rect(right, by, 130 * _s, 40 * _s), "In the pack", CGood); right -= 8 * _s; }
            var b = new Rect(right - 150 * _s, by, 150 * _s, 40 * _s);
            if (_cat.IsInstalled(n.Id)) StatusText(b, "Installed", CGood);
            else if (Btn(b, "Install", true)) InstallFromNexus(n.Id);
            Lbl(new Rect(x, row.y + 8 * _s, b.x - x - 10 * _s, 28 * _s), n.Name, _bold);
            Lbl(new Rect(x, row.y + 36 * _s, b.x - x - 10 * _s, 22 * _s), "Nexus Mods" + (n.Author.Length > 0 ? "  ·  by " + n.Author : ""), Tint(_small, CMuted));
        }

        /// <summary>A mod in a pack: picture, name, Installed / Update ready, and Remove or Add while editing (Subscribe to the pack, or open the mod, to install).</summary>
        private void PackModRow(Rect row, SiteMod m, SitePack p, string action)
        {
            Fill(row, row.Contains(Event.current.mousePosition) ? CCard : CRow, 12 * _s);
            var pic = new Rect(row.x + 8 * _s, row.y + 6 * _s, row.height - 12 * _s, row.height - 12 * _s);
            Fill(pic, CField, 8 * _s);
            var tex = Nexus.Thumb(_host, m.Card.Length > 0 ? m.Card : m.Thumb);
            if (tex != null) GUI.DrawTexture(pic, tex, ScaleMode.ScaleAndCrop, true, 0, Color.white, 0, 8 * _s);
            var x = pic.xMax + 14 * _s;
            var right = row.xMax - 12 * _s;
            var by = row.y + (row.height - 40 * _s) / 2;
            if (action == "Remove") { right -= 120 * _s; if (Btn(new Rect(right, by, 120 * _s, 40 * _s), "Remove", false)) PackRemove(p, m); right -= 8 * _s; }
            else if (action == "Add") { right -= 120 * _s; if (Btn(new Rect(right, by, 120 * _s, 40 * _s), "Add", true)) PackAdd(p, m.Id, m.Name); right -= 8 * _s; }
            else if (action == "") { right -= 130 * _s; StatusText(new Rect(right, by, 130 * _s, 40 * _s), "In the pack", CGood); right -= 8 * _s; }
            right -= 150 * _s;
            SiteStatus(new Rect(right, by, 150 * _s, 40 * _s), m);
            Lbl(new Rect(x, row.y + 8 * _s, right - x - 10 * _s, 28 * _s), m.Name, _bold);
            Lbl(new Rect(x, row.y + 36 * _s, right - x - 10 * _s, 22 * _s), $"{m.Category}  ·  by {m.Author}  ·  v{m.Version.TrimStart('v', 'V')}", Tint(_small, CMuted));
            if (Hit(new Rect(row.x, row.y, right - row.x, row.height))) { ShowTab(TabSite); OpenSiteDetails(m); }
        }
    }
}

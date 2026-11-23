using System;
using System.Linq;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>The My mods page (1.17.0): an author's dashboard with their mods' downloads and endorsements on both sites.</summary>
    internal sealed partial class ModsMenu
    {
        private void OpenAuthor(bool force)
        {
            if (!AuthorStats.Loading) _host.StartCoroutine(AuthorStats.Load(force));
        }

        private static string Plus(long n) => n > 0 ? "+" + Short(n) : n == 0 ? "0" : "–";

        private void DrawAuthor(Rect r)
        {
            PerfStyles();
            var names = AuthorStats.Names();
            Lbl(new Rect(r.x, r.y, r.width, 36 * _s), "My mods", _h2);
            if (names.Count > 0)
                Lbl(new Rect(r.x, r.y + 38 * _s, r.width * 0.6f, 24 * _s), "by " + string.Join(", ", names.ToArray()), Tint(_small, CMuted));

            // Refresh and the website's notifications (new bug reports and comments arrive there)
            var bx = r.xMax;
            bx -= 150 * _s;
            if (Btn(new Rect(bx, r.y, 150 * _s, 40 * _s), AuthorStats.Loading ? "Loading…" : "Refresh", false) && !AuthorStats.Loading) OpenAuthor(true);
            if (!Edition.Nexus && SiteAccount.Has)
            {
                var nl = SiteAccount.Notifications > 0 ? $"Notifications ({SiteAccount.Notifications})" : "Notifications";
                var nw = CS(_bold, Lang.C(nl)).x + 44 * _s;
                bx -= nw + 10 * _s;
                if (Btn(new Rect(bx, r.y, nw, 40 * _s), nl, SiteAccount.Notifications > 0))
                    _host.StartCoroutine(SiteAccount.OpenSignedIn("/notifications", (ok, msg) => { if (msg.Length > 0) Toast(msg, ok); }));
            }

            var y = r.y + 76 * _s;
            if (!AuthorStats.Loaded)
            {
                var msg = AuthorStats.Loading ? "Looking up your mods on " + (Edition.Nexus ? "Nexus Mods" : Community.HostName + " and Nexus Mods") + "…"
                    : AuthorStats.Error.Length > 0 ? AuthorStats.Error : "Press Refresh to look up your mods.";
                Lbl(new Rect(r.x, y, r.width, 60 * _s), msg, Tint(_wrap, AuthorStats.Error.Length > 0 && !AuthorStats.Loading ? CWarn : CMuted));
                return;
            }

            // ---- totals
            var mods = AuthorStats.Mods;
            var cw = (r.width - 3 * 14 * _s) / 4f;
            var ch = 104 * _s;
            var since = AuthorStats.ComparedWith.HasValue ? "since " + AuthorStats.ComparedWith.Value.ToString("d MMM HH:mm") : "from the next time you look";
            Card(new Rect(r.x, y, cw, ch), "Downloads", Short(AuthorStats.TotalDownloads), AuthorStats.WeekDownloads > 0 ? Plus(AuthorStats.WeekDownloads) + " this week" : "this week: –");
            Card(new Rect(r.x + (cw + 14 * _s), y, cw, ch), "Endorsements", Short(AuthorStats.TotalEndorsements), AuthorStats.WeekEndorsements > 0 ? Plus(AuthorStats.WeekEndorsements) + " this week" : "this week: –");
            Card(new Rect(r.x + 2 * (cw + 14 * _s), y, cw, ch), "Mods", mods.Count.ToString(),
                Edition.Nexus ? "on Nexus Mods" : $"{mods.Count(m => m.OnSite)} on the site · {mods.Count(m => m.OnNexus)} on Nexus");
            Card(new Rect(r.x + 3 * (cw + 14 * _s), y, cw, ch), "New since last time",
                AuthorStats.ComparedWith.HasValue ? Plus(AuthorStats.NewDownloads) : "–",
                AuthorStats.ComparedWith.HasValue ? $"downloads · {Plus(AuthorStats.NewEndorsements)} endorsements, {since}" : since);
            y += ch + 16 * _s;

            var notes = string.Join("  ", new[] { AuthorStats.Error, AuthorStats.NexusNote }.Where(n => n.Length > 0).ToArray());
            if (notes.Length > 0)
            {
                Lbl(new Rect(r.x, y, r.width, 24 * _s), notes, Tint(_small, CWarn));
                y += 28 * _s;
            }

            // ---- each mod
            const float rowH = 74, gap = 8;
            var view = new Rect(r.x, y, r.width, r.yMax - y);
            var inner = BeginScroll(view, mods.Count * (rowH + gap) * _s);
            var w = inner.width;
            float cName = w * 0.36f, cCol = 110 * _s, x0 = 18 * _s;
            var cy = inner.y;
            foreach (var m in mods)
            {
                var row = new Rect(0, cy, w, rowH * _s);
                if (row.yMax > -20 && row.y < view.height + 20)
                {
                    Fill(row, CRow, 12 * _s);
                    Lbl(new Rect(x0, row.y + 8 * _s, cName - 20 * _s, 28 * _s), m.Name, Tint(_bold, CText));
                    var where = (m.OnSite ? "Website" : "") + (m.OnSite && m.OnNexus ? " + " : "") + (m.OnNexus ? "Nexus" : "");
                    var upd = m.Updated > 0 ? "   ·   updated " + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(m.Updated).ToLocalTime().ToString("d MMM yyyy") : "";
                    Lbl(new Rect(x0, row.y + 38 * _s, cName - 20 * _s, 26 * _s), (m.Version.Length > 0 ? "v" + m.Version + "   ·   " : "") + where + upd, Tint(_small, CMuted));

                    var cx = cName;
                    Stat(new Rect(cx, row.y, cCol, row.height), "Downloads", Short(m.Downloads),
                        m.OnSite && m.OnNexus ? $"{Short(m.SiteDownloads)} + {Short(m.NexusDownloads)}" : ""); cx += cCol;
                    Stat(new Rect(cx, row.y, cCol, row.height), "Endorsed", Short(m.Endorsements), ""); cx += cCol;
                    Stat(new Rect(cx, row.y, cCol, row.height), "This week", Plus(m.WeekDownloads), m.WeekEndorsements > 0 ? Plus(m.WeekEndorsements) + " endorsed" : "", m.WeekDownloads > 0 ? CGood : CMuted); cx += cCol;
                    Stat(new Rect(cx, row.y, cCol, row.height), "New", Plus(m.NewDownloads), m.NewEndorsements > 0 ? Plus(m.NewEndorsements) + " endorsed" : "", m.NewDownloads > 0 ? CAccent : CMuted); cx += cCol + 10 * _s;

                    // buttons on the right
                    var bxr = w - 14 * _s;
                    var by = row.y + (row.height - 38 * _s) / 2;
                    if (m.OnNexus)
                    {
                        bxr -= 86 * _s;
                        if (Btn(new Rect(bxr, by, 86 * _s, 38 * _s), "Bugs", false)) Application.OpenURL(m.NexusPage + "?tab=bugs");
                        bxr -= 96 * _s;
                        if (Btn(new Rect(bxr, by, 90 * _s, 38 * _s), "Nexus", false)) Application.OpenURL(m.NexusPage);
                    }
                    if (m.OnSite && !Edition.Nexus)
                    {
                        bxr -= 96 * _s;
                        if (Btn(new Rect(bxr, by, 90 * _s, 38 * _s), "Site", false))
                            Application.OpenURL(m.SitePage.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? m.SitePage : Community.Site + "mod/" + m.SiteId);
                    }
                }
                cy += (rowH + gap) * _s;
            }
            EndScroll();
        }

        private void Card(Rect r, string title, string big, string small)
        {
            Fill(r, CCard, 14 * _s);
            Lbl(new Rect(r.x + 16 * _s, r.y + 10 * _s, r.width - 32 * _s, 22 * _s), title, Tint(_small, CMuted));
            Lbl(new Rect(r.x + 16 * _s, r.y + 32 * _s, r.width - 32 * _s, 40 * _s), big, Tint(Size(_bold, 30), CAccent));
            Lbl(new Rect(r.x + 16 * _s, r.y + 74 * _s, r.width - 32 * _s, 22 * _s), small, Tint(_small, CText));
        }

        private void Stat(Rect r, string label, string value, string under, Color? color = null)
        {
            Lbl(new Rect(r.x, r.y + 6 * _s, r.width - 12 * _s, 20 * _s), label, _perfRightBold);
            Lbl(new Rect(r.x, r.y + 26 * _s, r.width - 12 * _s, 26 * _s), value, Tint(_perfRight, color ?? CText));
            if (under.Length > 0) Lbl(new Rect(r.x, r.y + 50 * _s, r.width - 12 * _s, 20 * _s), under, Tint(_perfRight, CMuted));
        }
    }
}

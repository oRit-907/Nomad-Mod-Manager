using System;
using System.Linq;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>The Performance page (1.17.0): each mod's cost per frame, its worst frame, and the stutters it was in.</summary>
    internal sealed partial class ModsMenu
    {
        private GUIStyle _perfRight, _perfRightBold;
        private float _perfStyleScale = -1;

        private void PerfStyles()
        {
            if (_perfRight != null && Mathf.Approximately(_perfStyleScale, _s)) return;
            _perfStyleScale = _s;
            _perfRight = new GUIStyle(Tint(_small, CText)) { alignment = TextAnchor.MiddleRight };
            _perfRightBold = new GUIStyle(Tint(_smallBold, CMuted)) { alignment = TextAnchor.MiddleRight };
        }

        private static Color MsColor(double ms) => ms < 0.3 ? CGood : ms < 1.5 ? CWarn : CBad;

        private void DrawPerformance(Rect r)
        {
            PerfStyles();
            Lbl(new Rect(r.x, r.y, r.width, 36 * _s), "Performance", _h2);

            // Start / Stop, Reset, Copy report
            var bx = r.xMax;
            var running = PerfMonitor.Running;
            if (PerfMonitor.Buckets.Count > 0 && !PerfMonitor.Preparing)
            {
                bx -= 190 * _s;
                if (Btn(new Rect(bx, r.y, 190 * _s, 40 * _s), "Copy report", false))
                {
                    GUIUtility.systemCopyBuffer = PerfMonitor.Report();
                    Toast("The performance report is copied. Paste it in a bug report or on Discord.", true);
                }
                if (running)
                {
                    bx -= 130 * _s;
                    if (Btn(new Rect(bx, r.y, 120 * _s, 40 * _s), "Reset", false)) PerfMonitor.Reset();
                }
                bx -= 10 * _s;
            }
            var label = PerfMonitor.Preparing ? "Cancel" : PerfMonitor.Stopping ? "Stopping…" : running ? "Stop" : "Start measuring";
            var sw = CS(_bold, Lang.C(label)).x + 44 * _s;
            if (Btn(new Rect(bx - sw, r.y, sw, 40 * _s), label, !running && !PerfMonitor.Preparing) && !PerfMonitor.Stopping)
            {
                if (running || PerfMonitor.Preparing) PerfMonitor.Stop(_host);
                else { PerfMonitor.MaxMinutes = _host.PerfMaxMinutes?.Value ?? 15; PerfMonitor.Start(_host); }
            }

            var y = r.y + 50 * _s;
            string line; Color lc = CMuted;
            if (PerfMonitor.Preparing) line = $"Getting ready: {PerfMonitor.Done} of {PerfMonitor.Total} pieces of mod code…";
            else if (PerfMonitor.Stopping) line = "Taking the timing off…";
            else if (running)
            {
                line = $"Measuring for {PerfMonitor.Elapsed:m\\:ss}   ·   {PerfMonitor.Fps:0} fps ({PerfMonitor.FrameMs:0.0} ms a frame)   ·   mods {PerfMonitor.ModsMs:0.00} ms ({PerfMonitor.ModsShare:P0} of each frame)   ·   memory cleanups {PerfMonitor.CleanupsPerMin:0.#}/min";
                lc = CText;
            }
            else if (PerfMonitor.Buckets.Count > 0) line = "Stopped. These are the last results. " + PerfMonitor.Note;
            else line = PerfMonitor.Note.Length > 0 ? PerfMonitor.Note
                : "See how long each mod takes every frame. Press Start measuring, close the menu and play or drive for a few minutes, then come back here. It's off until you start it, and stops by itself after a while.";
            var lh = CH(_wrapSmall, Lang.C(line), r.width);
            Lbl(new Rect(r.x, y, r.width, lh), line, Tint(_wrapSmall, lc));
            y += lh + 14 * _s;

            if (PerfMonitor.Buckets.Count == 0 || PerfMonitor.Preparing) return;

            var list = PerfMonitor.Buckets.OrderByDescending(b => b.AvgMs).ThenByDescending(b => b.PeakMs).ToList();
            var stutters = PerfMonitor.Stutters;
            const float rowH = 52, headH = 30, gap = 6;
            var stutterH = stutters.Count == 0 ? 60 : stutters.Count * 34 + 50;
            var contentH = (headH + list.Count * (rowH + gap) + 30 + stutterH) * _s;
            var view = new Rect(r.x, y, r.width, r.yMax - y);
            var inner = BeginScroll(view, contentH);
            var w = inner.width;
            // columns: name | per frame | share | worst | calls | stutters
            float cName = w * 0.40f, cCol = (w - cName) / 5f;
            float x0 = 18 * _s;
            var cy = inner.y;
            Lbl(new Rect(x0, cy, cName, headH * _s), "Mod", Tint(_smallBold, CMuted));
            string[] heads = { "Per frame", "Share", "Worst frame", "Calls a frame", "In stutters" };
            for (var i = 0; i < heads.Length; i++) Lbl(new Rect(cName + cCol * i, cy, cCol - 14 * _s, headH * _s), heads[i], _perfRightBold);
            cy += headH * _s;

            var top = 0.05; foreach (var b0 in list) if (b0.AvgMs > top) top = b0.AvgMs;
            foreach (var b in list)
            {
                var row = new Rect(0, cy, w, rowH * _s);
                if (row.yMax > -20 && row.y < view.height + 20)
                {
                    Fill(row, CRow, 12 * _s);
                    Lbl(new Rect(x0, row.y + 4 * _s, cName - 30 * _s, 26 * _s), b.Name, Tint(_bold, CText));
                    // a bar: how this mod compares with the slowest one
                    var bar = new Rect(x0, row.y + 34 * _s, cName - 40 * _s, 6 * _s);
                    Fill(bar, CField, 3 * _s);
                    Fill(new Rect(bar.x, bar.y, Mathf.Max(3 * _s, bar.width * (float)(b.AvgMs / top)), bar.height), MsColor(b.AvgMs), 3 * _s);
                    var cells = new[]
                    {
                        (b.AvgMs < 0.005 ? "<0.01 ms" : b.AvgMs.ToString("0.00") + " ms", MsColor(b.AvgMs)),
                        (b.Share.ToString("P1"), CText),
                        (b.PeakMs.ToString("0.0") + " ms", b.PeakMs >= PerfMonitor.StutterMs ? CBad : b.PeakMs >= 10 ? CWarn : CText),
                        (b.CallsPerFrame.ToString("0.#"), CMuted),
                        (b.Stutters.ToString(), b.Stutters > 0 ? CWarn : CMuted),
                    };
                    for (var i = 0; i < cells.Length; i++)
                        Lbl(new Rect(cName + cCol * i, row.y, cCol - 14 * _s, row.height), cells[i].Item1, Tint(_perfRight, cells[i].Item2));
                }
                cy += (rowH + gap) * _s;
            }

            cy += 14 * _s;
            Lbl(new Rect(x0, cy, w, 30 * _s), $"Stutters (frames over {PerfMonitor.StutterMs} ms), newest first", Tint(_bold, CText));
            cy += 36 * _s;
            if (stutters.Count == 0)
                Lbl(new Rect(x0, cy, w, 24 * _s), running ? "None yet." : "None while it was measuring.", Tint(_small, CGood));
            foreach (var s in stutters)
            {
                if (cy > -40 && cy < view.height + 40)
                {
                    Lbl(new Rect(x0, cy, 90 * _s, 30 * _s), s.When.ToString("HH:mm:ss"), Tint(_small, CMuted));
                    Lbl(new Rect(x0 + 90 * _s, cy, 90 * _s, 30 * _s), s.FrameMs.ToString("0") + " ms", Tint(_smallBold, s.FrameMs >= 150 ? CBad : CWarn));
                    double modsMs = 0; foreach (var m0 in s.Mods) modsMs += m0.Ms;
                    string who = s.Mods.Count == 0 ? "No mod took over 1 ms: the game itself (loading, saving or memory cleanup)."
                        : string.Join("   ·   ", s.Mods.Take(3).Select(m => $"{m.Name} {m.Ms:0.0} ms").ToArray())
                          + (modsMs < s.FrameMs * 0.3 ? "   (most of it was the game itself)" : "");
                    Lbl(new Rect(x0 + 190 * _s, cy, w - 210 * _s, 30 * _s), who, Tint(_small, CText));
                }
                cy += 34 * _s;
            }
            EndScroll();
        }
    }
}

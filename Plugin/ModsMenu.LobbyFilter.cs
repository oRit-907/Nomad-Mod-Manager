using UnityEngine;

namespace NomadModManager
{
    /// <summary>The mods filter bar over the game's lobby list (see LobbyFilter).</summary>
    internal sealed partial class ModsMenu
    {
        private void DrawLobbyFilter()
        {
            var bar = LobbyFilter.BarRect(44 * _s);
            if (bar == null) return;
            Styles();
            var r = bar.Value;
            Fill(r, Alpha(CPanel, 0.92f), 10 * _s);
            var x = r.x + 12 * _s;
            Lbl(new Rect(x, r.y, 70 * _s, r.height), "Mods:", Tint(_smallBold, CAccent));
            x += 70 * _s;

            bool Pill(string text, bool on, float w)
            {
                var pr = new Rect(x, r.y + 6 * _s, w, r.height - 12 * _s);
                var hover = pr.Contains(Event.current.mousePosition);
                Fill(pr, on ? (hover ? CAccentHover : CAccent) : (hover ? CEdge : CLine), 9 * _s);
                Lbl(pr, text, Center(_smallBold, on ? CInk : CText));
                x += w + 8 * _s;
                return GUI.Button(pr, GUIContent.none, _invisible);
            }

            if (Pill("Only games I can join", LobbyFilter.Joinable, 210 * _s)) LobbyFilter.Joinable = !LobbyFilter.Joinable;
            if (Pill("Best match first", LobbyFilter.Best, 170 * _s)) LobbyFilter.Best = !LobbyFilter.Best;

            // "Using: Any ▸" cycles through the mods the listed games use.
            var mod = LobbyFilter.Mod;
            var label = mod.Length == 0 ? "Using: any mod ▸" : "Using: " + (mod.Length > 22 ? mod.Substring(0, 21) + "…" : mod) + " ▸";
            var w = Mathf.Min(_smallBold.CalcSize(new GUIContent(label)).x + 30 * _s, r.xMax - x - 60 * _s);
            if (w > 80 * _s && Pill(label, mod.Length > 0, w))
            {
                var mods = LobbyFilter.ModsInList();
                var i = mods.FindIndex(m => ModCatalog.NameKey(m) == ModCatalog.NameKey(mod));
                LobbyFilter.Mod = mods.Count == 0 || i == mods.Count - 1 ? "" : mods[i + 1];
                if (mods.Count == 0) Toast("None of the games in the list use mods that the Mods menu can see.", false);
            }
            if (mod.Length > 0 && Pill("✕", false, 40 * _s)) LobbyFilter.Mod = "";
        }
    }
}

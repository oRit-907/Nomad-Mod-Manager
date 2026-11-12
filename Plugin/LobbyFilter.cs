using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EvilCore.UI.MainMenu.Panels;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NomadModManager
{
    /// <summary>
    /// Mods filters for the game's lobby list (Join game): only games you can join with your mods, the best mod
    /// match first, and games using a particular mod. The game draws the list; this hides and reorders its rows
    /// after each refresh, and the Mods menu draws the filter bar in a gap made at the top of the list.
    /// </summary>
    internal static class LobbyFilter
    {
        private static ConfigEntry<bool> _joinable, _best;
        private static ConfigEntry<string> _mod;
        internal static JoinGamePanel Panel;
        internal static RectTransform Space;   // the gap at the top of the list the bar is drawn over
        internal static RectTransform Viewport;
        internal static int Shown, Total;

        private static void Bind()
        {
            if (_joinable != null) return;
            var cfg = ModManagerPlugin.I.Config;
            _joinable = cfg.Bind("Lobby list", "OnlyJoinable", false, "Join game: only show games you can join with your mods.");
            _best = cfg.Bind("Lobby list", "BestMatchFirst", true, "Join game: games whose mods match yours best come first.");
            _mod = cfg.Bind("Lobby list", "UsingMod", "", "Join game: only show games using this mod (its name; empty = any).");
        }

        public static bool Joinable { get { Bind(); return _joinable.Value; } set { Bind(); _joinable.Value = value; Apply(); } }
        public static bool Best { get { Bind(); return _best.Value; } set { Bind(); _best.Value = value; Apply(); } }
        public static string Mod { get { Bind(); return _mod.Value ?? ""; } set { Bind(); _mod.Value = value ?? ""; Apply(); } }

        /// <summary>The mods used by the games in the list, most used first (for the "Using" picker).</summary>
        public static List<string> ModsInList()
        {
            var counts = new Dictionary<string, (string Name, int N)>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries())
            {
                if (!LobbyInfo.Seen.TryGetValue(e.LobbyId ?? "", out var info)) continue;
                foreach (var m in info.Mods.Where(m => m.Name.Length > 0))
                {
                    var k = ModCatalog.NameKey(m.Name);
                    counts[k] = counts.TryGetValue(k, out var c) ? (c.Name, c.N + 1) : (m.Name, 1);
                }
            }
            return counts.Values.OrderByDescending(c => c.N).ThenBy(c => c.Name).Select(c => c.Name).ToList();
        }

        private static List<LobbyEntryUI> Entries()
        {
            if (Panel == null) return new List<LobbyEntryUI>();
            return Traverse.Create(Panel).Field("_lobbyEntries").GetValue<List<LobbyEntryUI>>()?.Where(e => e != null).ToList() ?? new List<LobbyEntryUI>();
        }

        [HarmonyPatch(typeof(JoinGamePanel), "RenderLobbies")]
        [HarmonyPostfix]
        private static void Rendered(JoinGamePanel __instance)
        {
            Panel = __instance;
            try { MakeSpace(); Apply(); }
            catch (Exception e) { ModManagerPlugin.Log?.LogInfo("Lobby filter skipped: " + e.Message); }
        }

        /// <summary>A gap at the top of the list (when the list lays its rows out itself), for the filter bar.</summary>
        private static void MakeSpace()
        {
            var content = Traverse.Create(Panel).Field("lobbyListContent").GetValue<RectTransform>();
            if (content == null) return;
            Viewport = content.GetComponentInParent<ScrollRect>()?.viewport ?? content.parent as RectTransform;
            if (Space != null && Space.parent == content) { Space.SetSiblingIndex(0); return; }
            if (content.GetComponent<VerticalLayoutGroup>() == null) { Space = null; return; }   // no layout: the bar goes above the list instead
            var go = new GameObject("NMM_LobbyFilterSpace", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(content, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 56f;
            le.flexibleHeight = 0;
            Space = (RectTransform)go.transform;
            Space.SetSiblingIndex(0);
        }

        /// <summary>Hides the games the filters leave out and puts the best matches first.</summary>
        public static void Apply()
        {
            if (Panel == null || !Panel) return;
            var cat = ModManagerPlugin.I?.Catalog;
            if (cat == null) return;
            var mine = LobbyMods.Mine(cat);
            var mod = Mod;
            var rows = new List<(LobbyEntryUI Row, float Score, int Order)>();
            var entries = Entries();
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                LobbyInfo.Seen.TryGetValue(e.LobbyId ?? "", out var info);
                var canJoin = info == null || info.CanJoin(mine);   // no Mods menu on the host: Safe join keeps you in
                var usesMod = mod.Length == 0 || (info != null && info.Mods.Any(m => ModCatalog.NameKey(m.Name) == ModCatalog.NameKey(mod)));
                var show = usesMod && (!Joinable || canJoin);
                e.gameObject.SetActive(show);
                // 0: your mods match; 1: you can join (optional mods / no mods); 2+: mods to get first.
                var score = info == null ? 1f : !canJoin ? 2f + info.MissingRequired(mine).Count : info.DiffWith(mine).CanPlay ? 0f : 1f;
                rows.Add((e, score, i));
            }
            Total = rows.Count;
            Shown = rows.Count(r => r.Row.gameObject.activeSelf);
            var ordered = Best ? rows.OrderBy(r => r.Score).ThenBy(r => r.Order).ToList() : rows.OrderBy(r => r.Order).ToList();
            var first = Space != null && Space.parent == ordered.FirstOrDefault().Row?.transform.parent ? 1 : 0;
            for (var i = 0; i < ordered.Count; i++) ordered[i].Row.transform.SetSiblingIndex(first + i);

            var status = Traverse.Create(Panel).Field("statusText").GetValue<TextMeshProUGUI>();
            if (status != null && Total > 0 && (Joinable || mod.Length > 0))
                status.text = Shown == 0 ? "No games match your mods filter." : $"{Shown} of {Total} games shown (mods filter)";
        }

        /// <summary>Where the filter bar goes on screen (IMGUI coordinates), or null when the lobby list isn't showing.</summary>
        public static Rect? BarRect(float height)
        {
            if (Panel == null || !Panel || !Panel.isActiveAndEnabled) return null;
            if (Space != null && Space.gameObject.activeInHierarchy)
            {
                var r = ScreenRect(Space);
                var vp = Viewport != null ? ScreenRect(Viewport) : r;
                if (r.y < vp.y - 2 || r.yMax > vp.yMax + 2) return null;   // scrolled out of view
                return new Rect(r.x + 8, r.center.y - height / 2, r.width - 16, height);
            }
            if (Viewport == null) return null;
            var v = ScreenRect(Viewport);
            return new Rect(v.x, v.y - height - 6, v.width, height);
        }

        private static Rect ScreenRect(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            var canvas = rt.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var a = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
            var b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            return new Rect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }
    }
}

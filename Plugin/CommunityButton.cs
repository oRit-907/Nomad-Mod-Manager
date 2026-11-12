using System;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NomadModManager
{
    /// <summary>
    /// "Join Modding Community!" on the game's main menu, right above its "Join Discord Community!" button:
    /// a copy of that button (same look and the same wave animation on its text) that opens nomaddrivemods.online.
    /// </summary>
    internal static class CommunityButton
    {
        private const string ObjectName = "NomadModManager_CommunityButton";
        private const string Label = "Join Modding Community!";
        private static readonly Color Amber = new Color(0.91f, 0.57f, 0.23f, 1f);
        private static float _nextLook;
        private static GameObject _made;
        private static RectTransform _src;      // the game's Discord button: ours sits right above it and follows it
        private static CanvasGroup _srcGroup, _group;
        private static Sprite _icon;
        private static bool _logged;

        /// <summary>Called every frame at the main menu; looks for the Discord button now and then until the copy exists.</summary>
        public static void Keep()
        {
            if (Edition.Nexus) return;   // no website button in the Nexus edition
            if (_made != null && _src != null)
            {
                if (!_made.activeSelf) _made.SetActive(true);
                Follow();
                return;
            }
            if (Time.unscaledTime < _nextLook) return;
            _nextLook = Time.unscaledTime + 1f;
            try { Make(); }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Community button: " + e.Message); _nextLook = Time.unscaledTime + 30f; }
        }

        private static void Make()
        {
            // The game's Discord button: an ExternalLinkButton whose link is a Discord invite.
            MonoBehaviour link = null;
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || mb.GetType().Name != "ExternalLinkButton" || !mb.gameObject.activeInHierarchy) continue;
                var url = mb.GetType().GetField("url", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mb) as string ?? "";
                if (url.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0) { link = mb; break; }
            }
            if (link == null) return;
            var src = link.gameObject;
            var parent = src.transform.parent;
            var existing = parent.Find(ObjectName);
            if (existing != null) { _made = existing.gameObject; Hook(src); return; }
            if (!_logged) { _logged = true; ModManagerPlugin.Log.LogInfo("Discord button found: " + Describe(src.transform, 0)); }

            var go = UnityEngine.Object.Instantiate(src, parent, false);
            go.name = ObjectName;

            // Where it goes: the website (not the Steam store overlay for an app).
            var lb = go.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == "ExternalLinkButton");
            if (lb != null)
            {
                var t = lb.GetType();
                t.GetField("url", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(lb, Community.Site);
                t.GetField("steamAppId", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(lb, 0u);
            }

            // The text: the game's own words would come back from its localization, so that goes; the wave stays.
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (c != null && c.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0) UnityEngine.Object.DestroyImmediate(c);
            foreach (var tx in go.GetComponentsInChildren<TMP_Text>(true)) tx.text = Label;

            // The icon: the site's bus on amber instead of Discord's logo on blurple.
            // The badge (Content/LogoBadge) turns amber; its white logo (LogoBadge/Logo) becomes the site's bus.
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                if (img.gameObject == go) continue;
                var spriteName = img.sprite != null ? img.sprite.name : "";
                if (img.gameObject.name == "Logo" || spriteName.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var s = Icon();
                    if (s != null) { img.sprite = s; img.overrideSprite = null; img.color = Color.white; img.preserveAspect = true; }
                }
                else if (img.gameObject.name == "LogoBadge") img.color = new Color(Amber.r, Amber.g, Amber.b, img.color.a);
            }
            // One gamepad shortcut per button: the copy doesn't take the Discord one.
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c != null && (c.GetType().Name == "UIPadShortcut" || c.GetType().Name == "UIAppear")) UnityEngine.Object.DestroyImmediate(c);
            var pad = go.transform.Find("PadGlyph");
            if (pad != null) UnityEngine.Object.DestroyImmediate(pad.gameObject);

            // Above the Discord button: first in a layout group, otherwise kept one button-height above it (Follow).
            if (parent.GetComponent<LayoutGroup>() != null) go.transform.SetSiblingIndex(src.transform.GetSiblingIndex());
            go.SetActive(true);
            _made = go;
            Hook(src);
            Follow();
            ModManagerPlugin.Log.LogInfo("Added \"" + Label + "\" above the Discord button on the main menu.");
        }

        private static void Hook(GameObject src)
        {
            _src = (RectTransform)src.transform;
            _srcGroup = src.GetComponent<CanvasGroup>();
            _group = _made.GetComponent<CanvasGroup>();
        }

        /// <summary>Every frame: right above the Discord button, fading in with it (the game slides and fades its menu in).</summary>
        private static void Follow()
        {
            var rt = (RectTransform)_made.transform;
            if (_made.transform.parent.GetComponent<LayoutGroup>() == null)
            {
                rt.anchorMin = _src.anchorMin; rt.anchorMax = _src.anchorMax; rt.pivot = _src.pivot;
                rt.sizeDelta = _src.sizeDelta;
                rt.anchoredPosition = _src.anchoredPosition + new Vector2(0f, _src.rect.height + 12f);
            }
            if (_srcGroup != null && _group != null) { _group.alpha = _srcGroup.alpha; _group.interactable = _srcGroup.interactable; _group.blocksRaycasts = _srcGroup.blocksRaycasts; }
        }

        private static Sprite Icon()
        {
            if (_icon != null) return _icon;
            try
            {
                using (var s = typeof(CommunityButton).Assembly.GetManifestResourceStream("community-icon.png"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    s.Read(bytes, 0, bytes.Length);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(bytes)) return null;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    UnityEngine.Object.DontDestroyOnLoad(tex);
                    _icon = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    _icon.name = "NomadModManager_CommunityIcon";
                    return _icon;
                }
            }
            catch { return null; }
        }

        private static string Describe(Transform t, int depth)
        {
            var sb = new StringBuilder();
            sb.Append(t.name).Append(" [").Append(string.Join(", ", t.GetComponents<Component>().Where(c => c != null).Select(c =>
            {
                var n = c.GetType().Name;
                if (c is Image i) n += "(" + (i.sprite != null ? i.sprite.name : "-") + " " + ColorUtility.ToHtmlStringRGBA(i.color) + ")";
                return n;
            }).ToArray())).Append("]");
            if (depth < 3) foreach (Transform ch in t) sb.Append(" { ").Append(Describe(ch, depth + 1)).Append(" }");
            return sb.ToString();
        }
    }
}

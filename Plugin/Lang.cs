using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// The menu in the game's language. Words come from Nomad Mod Downloader's translations (embedded) plus the
    /// menu's own (Lang/*.json); anything without a translation stays English. Numbers inside a text are matched
    /// as {0}, {1}…, so "Installed (9)" finds "Installed ({0})".
    /// </summary>
    internal static class Lang
    {
        public static string Code { get; private set; } = "en";
        private static Dictionary<string, string> _words = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> Memo = new Dictionary<string, string>();
        private static readonly Regex Digits = new Regex(@"\d+");
        private static float _nextCheck;

        /// <summary>Follows the game's language (checked every few seconds), unless the config picks one.</summary>
        public static void Update(string setting)
        {
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 5f;
            var code = (setting ?? "").Trim();
            if (code.Length == 0 || code.Equals("Game", StringComparison.OrdinalIgnoreCase)) code = GameLanguage();
            code = Normalize(code);
            if (code == Code) return;
            Code = code;
            Load();
        }

        private static string GameLanguage()
        {
            try { return UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale?.Identifier.Code ?? "en"; }
            catch { return "en"; }
        }

        private static string Normalize(string code)
        {
            code = code.ToLowerInvariant();
            if (code.StartsWith("zh")) return "zh";
            if (code.StartsWith("pt")) return "pt";
            var two = code.Length >= 2 ? code.Substring(0, 2) : code;
            return new[] { "de", "es", "fr", "it", "ja", "ko", "pl", "pt", "ru", "tr", "zh" }.Contains(two) ? two : "en";
        }

        private static void Load()
        {
            var words = new Dictionary<string, string>();
            if (Code != "en")
                foreach (var name in new[] { "Lang.app." + Code + ".json", "Lang.menu." + Code + ".json" }) // the menu's own last: they win
                {
                    try
                    {
                        using (var s = typeof(Lang).Assembly.GetManifestResourceStream(name))
                        {
                            if (s == null) continue;
                            using (var r = new StreamReader(s))
                            {
                                var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(r.ReadToEnd());
                                if (d != null) foreach (var kv in d) if (!string.IsNullOrEmpty(kv.Value)) words[kv.Key] = kv.Value;
                            }
                        }
                    }
                    catch (Exception e) { ModManagerPlugin.Log?.LogDebug("Translations " + name + ": " + e.Message); }
                }
            _words = words;
            Memo.Clear();
            ModManagerPlugin.Log?.LogInfo(Code == "en" ? "Mods menu language: English." : $"Mods menu language: {Code} ({words.Count} phrases).");
        }

        /// <summary>The text in the menu's language (or as it is, when there's no translation).</summary>
        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text) || _words.Count == 0) return text;
            if (Memo.TryGetValue(text, out var done)) return done;
            string result = null;
            if (_words.TryGetValue(text, out var t)) result = t;
            else if (text.IndexOfAny("0123456789".ToCharArray()) >= 0)
            {
                // "Update all (3)" → "Update all ({0})"
                var nums = new List<string>();
                var key = Digits.Replace(text, m => { nums.Add(m.Value); return "{" + (nums.Count - 1) + "}"; });
                if (_words.TryGetValue(key, out var tt))
                {
                    result = tt;
                    for (var i = 0; i < nums.Count; i++) result = result.Replace("{" + i + "}", nums[i]);
                }
            }
            result = result ?? text;
            if (Memo.Count > 4000) Memo.Clear();
            Memo[text] = result;
            return result;
        }

        public static GUIContent C(string text) => new GUIContent(T(text));
    }
}

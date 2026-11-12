#nullable disable
using System;
using System.Collections.Generic;

namespace NomadModManager
{
    /// <summary>
    /// Compares mod version numbers the way people write them, so versions from this site, Nexus Mods and a DLL
    /// line up: "v3.2.4" = "3.2.4.0", "1.0b" is after "1.0a", "1.2-beta2" is before "1.2", and tags like
    /// "-BepInEx6", "(BE5)" or "Steam" are ignored. Shared by the in-game Mods menu and Nomad Mod Downloader.
    /// </summary>
    public static class ModVersion
    {
        private sealed class Parsed
        {
            public readonly List<long> Numbers = new List<long>();
            public int Letter;      // "1.0b" → 2; 0 = none
            public int Stage = 5;   // 1 alpha, 2 beta, 3 preview, 4 rc, 5 release, 6 hotfix
            public long StageNumber;
        }

        /// <summary>Negative when a is older than b, 0 when they're the same version, positive when a is newer.
        /// Versions that can't be read count as the same (null when either can't be read, through <see cref="TryCompare"/>).</summary>
        public static int Compare(string a, string b) => TryCompare(a, b) ?? 0;

        public static int? TryCompare(string a, string b)
        {
            var x = Parse(a);
            var y = Parse(b);
            if (x == null || y == null) return null;
            var n = Math.Max(x.Numbers.Count, y.Numbers.Count);
            for (var i = 0; i < n; i++)
            {
                var p = i < x.Numbers.Count ? x.Numbers[i] : 0;
                var q = i < y.Numbers.Count ? y.Numbers[i] : 0;
                if (p != q) return p < q ? -1 : 1;
            }
            if (x.Letter != y.Letter) return x.Letter < y.Letter ? -1 : 1;
            if (x.Stage != y.Stage) return x.Stage < y.Stage ? -1 : 1;
            if (x.StageNumber != y.StageNumber) return x.StageNumber < y.StageNumber ? -1 : 1;
            return 0;
        }

        /// <summary>Is <paramref name="latest"/> a newer version than <paramref name="current"/>? False when either can't be read.</summary>
        public static bool IsNewer(string latest, string current) => (TryCompare(latest, current) ?? 0) > 0;

        /// <summary>The same version, however it's written ("v1.2" and "1.2.0").</summary>
        public static bool Same(string a, string b) => TryCompare(a, b) == 0;

        /// <summary>A tidy form for showing: "v3.2.4" → "3.2.4".</summary>
        public static string Clean(string v)
        {
            v = (v ?? "").Trim();
            if (v.StartsWith("version", StringComparison.OrdinalIgnoreCase)) v = v.Substring(7).Trim();
            if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1])) v = v.Substring(1);
            return v;
        }

        private static Parsed Parse(string s)
        {
            s = Clean(s).ToLowerInvariant();
            if (s.Length == 0 || !char.IsDigit(s[0])) return null;
            var r = new Parsed();
            var i = 0;
            // Numbers separated by dots (also "_" or "," as people sometimes type them).
            while (i < s.Length)
            {
                var start = i;
                long n = 0;
                while (i < s.Length && char.IsDigit(s[i]) && i - start < 15) { n = n * 10 + (s[i] - '0'); i++; }
                if (i == start) break;
                r.Numbers.Add(n);
                if (i + 1 < s.Length && (s[i] == '.' || s[i] == '_' || s[i] == ',') && char.IsDigit(s[i + 1])) { i++; continue; }
                break;
            }
            if (r.Numbers.Count == 0) return null;
            var rest = s.Substring(i);
            // A single letter right after the number: "1.0b".
            if (rest.Length > 0 && char.IsLetter(rest[0]) && (rest.Length == 1 || !char.IsLetter(rest[1])))
            {
                r.Letter = rest[0] - 'a' + 1;
                rest = rest.Substring(1);
            }
            // A pre-release or hotfix word: "-beta2", " rc1", "-hotfix".
            var word = "";
            var j = 0;
            while (j < rest.Length && !char.IsLetter(rest[j])) j++;
            while (j < rest.Length && char.IsLetter(rest[j])) word += rest[j++];
            int stage;
            switch (word)
            {
                case "alpha": case "a": stage = 1; break;
                case "beta": case "b": stage = 2; break;
                case "pre": case "preview": case "dev": case "test": case "snapshot": case "nightly": case "wip": case "experimental": stage = 3; break;
                case "rc": stage = 4; break;
                case "hotfix": case "fix": case "patch": stage = 6; break;
                default: stage = 5; break; // "-bepinex6", "steam"…: a label, not part of the version
            }
            if (stage != 5)
            {
                r.Stage = stage;
                long sn = 0;
                while (j < rest.Length && !char.IsDigit(rest[j]) && !char.IsLetter(rest[j])) j++;
                while (j < rest.Length && char.IsDigit(rest[j]) && sn < 1000000) sn = sn * 10 + (rest[j++] - '0');
                r.StageNumber = sn;
            }
            return r;
        }
    }
}

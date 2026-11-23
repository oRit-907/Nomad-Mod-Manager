using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>One of the player's own mods on the My mods page, with its numbers on the website and on Nexus.</summary>
    internal sealed class AuthorMod
    {
        public string Name = "", Version = "", Key = "";
        public int SiteId, NexusId;
        public string SitePage = "";
        public long SiteDownloads, SiteEndorsements, NexusDownloads, NexusEndorsements;
        public bool OnSite, OnNexus;
        public long Updated;                      // unix seconds, the newest of the two
        public long Downloads => SiteDownloads + NexusDownloads;
        public long Endorsements => SiteEndorsements + NexusEndorsements;
        // changes: since the player last opened the page (last game session), and over 7 days
        public long NewDownloads = -1, NewEndorsements = -1, WeekDownloads = -1, WeekEndorsements = -1;
        public string NexusPage => $"https://www.nexusmods.com/{Nexus.GameDomain}/mods/{NexusId}";
    }

    /// <summary>
    /// The My mods page (1.17.0): finds the player's own mods on the community website and on Nexus Mods by their author
    /// name, with downloads and endorsements, and keeps a small history (BepInEx\plugins\NomadModManager\data\
    /// author-stats.json) so the page can show what's new since last time and over the last week.
    /// Everything it reads is public; the website account only adds the notifications count.
    /// </summary>
    internal static class AuthorStats
    {
        public static readonly List<AuthorMod> Mods = new List<AuthorMod>();
        public static bool Loading { get; private set; }
        public static bool Loaded { get; private set; }
        public static string Error = "", NexusNote = "";
        public static DateTime LoadedAt;
        public static long TotalDownloads, TotalEndorsements, NewDownloads, NewEndorsements, WeekDownloads, WeekEndorsements;
        public static DateTime? ComparedWith;

        private static string FilePath => Path.Combine(Pending.Dir(Paths.GameRootPath), "author-stats.json");

        /// <summary>The names the player publishes under: the config's list, else the website account's name.</summary>
        public static List<string> Names()
        {
            var list = new List<string>();
            foreach (var n in (ModManagerPlugin.I?.AuthorNames?.Value ?? "").Split(',', ';'))
                if (n.Trim().Length > 0) list.Add(n.Trim());
            if (list.Count == 0 && !Edition.Nexus && SiteAccount.Has && SiteAccount.Name.Trim().Length > 0) list.Add(SiteAccount.Name.Trim());
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The page shows in the menu for players who publish mods (signed in to the website, or names in the config).</summary>
        public static bool Visible => Names().Count > 0;

        private static bool IsMine(string author, List<string> names) =>
            !string.IsNullOrEmpty(author) && names.Any(n => string.Equals(n, author.Trim(), StringComparison.OrdinalIgnoreCase));

        public static IEnumerator Load(bool force)
        {
            if (Loading) yield break;
            if (!force && Loaded && (DateTime.Now - LoadedAt).TotalMinutes < 10) yield break;
            Loading = true;
            Error = ""; NexusNote = "";
            try
            {
                var names = Names();
                if (names.Count == 0) { Error = "Add your author name in Settings › Author › MyNames (or sign in to the website)."; yield break; }
                var found = new List<AuthorMod>();

                // ---- the website: read every page of mods and keep the player's own
                if (!Edition.Nexus)
                {
                    string siteErr = null;
                    for (var page = 1; page <= 25; page++)
                    {
                        JObject json = null; string err = null;
                        yield return Community.GetJson("do=mods&per=48&sort=updated&page=" + page, j => json = j, e => err = e);
                        if (err != null) { siteErr = err; break; }
                        var arr = json["mods"] as JArray ?? new JArray();
                        foreach (var t in arr)
                        {
                            var m = SiteMod.Parse(t);
                            if (m.Id <= 0 || !IsMine(m.Author, names)) continue;
                            found.Add(new AuthorMod
                            {
                                Name = m.Name, Version = m.Version, SiteId = m.Id, NexusId = m.NexusId, SitePage = m.Page, OnSite = true,
                                SiteDownloads = m.Downloads, SiteEndorsements = m.Endorsements, Updated = m.Updated,
                            });
                        }
                        var total = (int?)json["total"] ?? 0;
                        if (arr.Count == 0 || page * 48 >= total) break;
                    }
                    if (siteErr != null) Error = "The website: " + siteErr;
                }

                // ---- Nexus: the player's mods by author name, plus the Nexus pages the website mods link to
                var nexus = new Dictionary<int, JToken>();
                string nexusErr = null;
                foreach (var name in names)
                {
                    var ok = false;
                    yield return NexusByAuthor(name, list => { ok = true; foreach (var n in list) nexus[(int?)n["modId"] ?? 0] = n; }, e => nexusErr = e);
                    if (!ok) break;
                }
                if (nexusErr != null || nexus.Count == 0)
                {
                    // That kind of search isn't there (or failed): look through Nomad Drive's newest mods instead.
                    nexusErr = null;
                    yield return NexusScan(names, list => { foreach (var n in list) nexus[(int?)n["modId"] ?? 0] = n; }, e => nexusErr = e);
                }
                var linked = found.Where(f => f.NexusId > 0 && !nexus.ContainsKey(f.NexusId)).Select(f => f.NexusId).Distinct().ToList();
                if (linked.Count > 0)
                {
                    JToken data = null;
                    var q = "query {" + string.Join("", linked.Take(40).Select(id => $" m{id}: mod(modId: {id}, gameId: {Nexus.GameId}) {{ modId name version author downloads endorsements updatedAt }}").ToArray()) + " }";
                    yield return Nexus.Query(q, null, d => data = d, e => nexusErr = nexusErr ?? e);
                    if (data is JObject o) foreach (var p in o.Properties()) if (p.Value is JObject mo) nexus[(int?)mo["modId"] ?? 0] = mo;
                }
                if (nexusErr != null) NexusNote = "Nexus Mods: " + nexusErr;

                foreach (var kv in nexus)
                {
                    if (kv.Key <= 0) continue;
                    var n = kv.Value;
                    var name = Nexus.Html((string)n["name"] ?? "");
                    var mine = found.FirstOrDefault(f => f.NexusId == kv.Key)
                               ?? found.FirstOrDefault(f => f.NexusId == 0 && ModCatalog.NameKey(f.Name) == ModCatalog.NameKey(name));
                    if (mine == null) { mine = new AuthorMod { Name = name, Version = (string)n["version"] ?? "" }; found.Add(mine); }
                    mine.NexusId = kv.Key; mine.OnNexus = true;
                    mine.NexusDownloads = (long?)n["downloads"] ?? 0;
                    mine.NexusEndorsements = (long?)n["endorsements"] ?? 0;
                    if (DateTime.TryParse((string)n["updatedAt"], out var up))
                    {
                        var secs = (long)(up.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        if (secs > mine.Updated) mine.Updated = secs;
                    }
                    if (!mine.OnSite && mine.Version.Length == 0) mine.Version = (string)n["version"] ?? "";
                }
                foreach (var m in found) m.Key = m.SiteId > 0 ? "s" + m.SiteId : "n" + m.NexusId;

                Compare(found);
                Mods.Clear();
                Mods.AddRange(found.OrderByDescending(m => m.Downloads));
                TotalDownloads = Mods.Sum(m => m.Downloads);
                TotalEndorsements = Mods.Sum(m => m.Endorsements);
                Loaded = true;
                LoadedAt = DateTime.Now;
                if (found.Count == 0 && Error.Length == 0)
                    Error = "No mods found under " + string.Join(", ", names.ToArray()) + ". Check the names in Settings › Author › MyNames.";
            }
            finally { Loading = false; }
            if (!Edition.Nexus && SiteAccount.Has) yield return SiteAccount.Refresh();   // the notifications count
        }

        /// <summary>Nexus's own search by author (exact name, any capitals).</summary>
        private static IEnumerator NexusByAuthor(string name, Action<List<JToken>> ok, Action<string> fail)
        {
            var result = new List<JToken>();
            for (var offset = 0; offset < 500; offset += 50)
            {
                var filter = new JObject
                {
                    ["gameDomainName"] = new JArray(new JObject { ["value"] = Nexus.GameDomain, ["op"] = "EQUALS" }),
                    ["author"] = new JArray(new JObject { ["value"] = name, ["op"] = "WILDCARD" }),
                };
                var vars = new JObject { ["f"] = filter, ["o"] = offset, ["c"] = 50 };
                const string q = "query($f: ModsFilter, $o: Int, $c: Int) { mods(filter: $f, offset: $o, count: $c) { totalCount nodes { modId name version author downloads endorsements updatedAt } } }";
                JToken data = null; string err = null;
                yield return Nexus.Query(q, vars, d => data = d, e => err = e);
                if (err != null) { fail(err); yield break; }
                var nodes = data?["mods"]?["nodes"] as JArray ?? new JArray();
                result.AddRange(nodes.Where(n => string.Equals(((string)n["author"] ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)));
                var total = (int?)data?["mods"]?["totalCount"] ?? 0;
                if (nodes.Count < 50 || offset + 50 >= total) break;
            }
            ok(result);
        }

        /// <summary>The fallback: Nomad Drive's mods, newest first (up to 400), keeping the player's.</summary>
        private static IEnumerator NexusScan(List<string> names, Action<List<JToken>> ok, Action<string> fail)
        {
            var result = new List<JToken>();
            for (var offset = 0; offset < 400; offset += 50)
            {
                var filter = new JObject { ["gameDomainName"] = new JArray(new JObject { ["value"] = Nexus.GameDomain, ["op"] = "EQUALS" }) };
                var vars = new JObject { ["f"] = filter, ["s"] = new JArray(new JObject { ["updatedAt"] = new JObject { ["direction"] = "DESC" } }), ["o"] = offset, ["c"] = 50 };
                const string q = "query($f: ModsFilter, $s: [ModsSort!], $o: Int, $c: Int) { mods(filter: $f, sort: $s, offset: $o, count: $c) { totalCount nodes { modId name version author downloads endorsements updatedAt } } }";
                JToken data = null; string err = null;
                yield return Nexus.Query(q, vars, d => data = d, e => err = e);
                if (err != null) { fail(err); yield break; }
                var nodes = data?["mods"]?["nodes"] as JArray ?? new JArray();
                result.AddRange(nodes.Where(n => IsMine((string)n["author"], names)));
                var total = (int?)data?["mods"]?["totalCount"] ?? 0;
                if (nodes.Count < 50 || offset + 50 >= total) break;
            }
            ok(result);
        }

        // ------------------------------------------------------------------ history: what's new

        private static JObject _file;
        private static bool _comparedThisSession;
        private static JObject _sessionBase;   // the numbers from before this game session, for "new since last time"

        private static JObject ReadFile()
        {
            if (_file != null) return _file;
            try { if (File.Exists(FilePath)) _file = JObject.Parse(File.ReadAllText(FilePath)); } catch { }
            return _file ?? (_file = new JObject());
        }

        private static void Compare(List<AuthorMod> mods)
        {
            var f = ReadFile();
            if (!_comparedThisSession)
            {
                _comparedThisSession = true;
                _sessionBase = f["last"] as JObject;
                if (DateTime.TryParse((string)f["lastAt"], out var at)) ComparedWith = at; else ComparedWith = null;
            }
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var days = f["days"] as JObject ?? new JObject();
            // the newest day that is at least 7 days old (or the oldest one there is)
            JObject week = null;
            foreach (var p in days.Properties().OrderByDescending(p => p.Name))
                if (string.CompareOrdinal(p.Name, DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd")) <= 0) { week = p.Value as JObject; break; }
            if (week == null) week = days.Properties().OrderBy(p => p.Name).FirstOrDefault(p => p.Name != today)?.Value as JObject;

            NewDownloads = NewEndorsements = WeekDownloads = WeekEndorsements = 0;
            var now = new JObject();
            foreach (var m in mods)
            {
                now[m.Key] = new JArray(m.Downloads, m.Endorsements);
                if (_sessionBase?[m.Key] is JArray b && b.Count >= 2)
                {
                    m.NewDownloads = Math.Max(0, m.Downloads - (long)b[0]); m.NewEndorsements = Math.Max(0, m.Endorsements - (long)b[1]);
                    NewDownloads += m.NewDownloads; NewEndorsements += m.NewEndorsements;
                }
                if (week?[m.Key] is JArray w && w.Count >= 2)
                {
                    m.WeekDownloads = Math.Max(0, m.Downloads - (long)w[0]); m.WeekEndorsements = Math.Max(0, m.Endorsements - (long)w[1]);
                    WeekDownloads += m.WeekDownloads; WeekEndorsements += m.WeekEndorsements;
                }
            }
            days[today] = now;
            foreach (var old in days.Properties().OrderByDescending(p => p.Name).Skip(40).ToList()) old.Remove();
            f["days"] = days;
            f["last"] = now;
            f["lastAt"] = DateTime.Now.ToString("o");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, f.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("My mods: couldn't save author-stats.json: " + e.Message); }
        }
    }
}

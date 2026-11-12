using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NomadModManager
{
    /// <summary>A mod pack on the website: a set of mods players subscribe to in one go.</summary>
    internal sealed class SitePack
    {
        public int Id, Mods, Subscribers;
        public string Name = "", Summary = "", Author = "", Page = "", Description = "";
        public bool Live = true, Subscribed;
        public long Updated;
        public List<string> Pictures = new List<string>();
        public List<int> ModIds = new List<int>();          // for the player's own packs
        public List<SiteMod> ModList = new List<SiteMod>(); // when the pack's page is open
        public List<PackNexus> Nexus = new List<PackNexus>(); // mods from Nexus Mods in the pack (when its page is open)

        public static SitePack Parse(JToken t)
        {
            var p = new SitePack
            {
                Id = (int?)t["id"] ?? 0,
                Name = (string)t["name"] ?? "",
                Summary = (string)t["summary"] ?? "",
                Author = (string)t["author"] ?? "",
                Page = (string)t["page"] ?? "",
                Description = (string)t["descriptionText"] ?? "",
                Mods = (int?)t["mods"] ?? 0,
                Subscribers = (int?)t["subscribers"] ?? 0,
                Updated = (long?)t["updated"] ?? 0,
                Live = (bool?)t["live"] ?? true,
                Subscribed = (bool?)t["subscribed"] ?? false,
            };
            foreach (var x in t["pictures"] as JArray ?? new JArray()) p.Pictures.Add((string)x ?? "");
            foreach (var x in t["modIds"] as JArray ?? new JArray()) p.ModIds.Add((int?)x ?? 0);
            foreach (var x in t["modList"] as JArray ?? new JArray()) p.ModList.Add(SiteMod.Parse(x));
            if (p.ModIds.Count > 0 && p.Mods == 0) p.Mods = p.ModIds.Count;
            foreach (var x in t["nexus"] as JArray ?? new JArray())
                p.Nexus.Add(new PackNexus { Id = (int?)x["id"] ?? 0, Name = (string)x["name"] ?? "", Author = (string)x["author"] ?? "", Thumb = (string)x["thumb"] ?? "" });
            p.Nexus.RemoveAll(n => n.Id <= 0);
            if (p.ModList.Count > 0 || p.Nexus.Count > 0) p.Mods = p.ModList.Count + p.Nexus.Count;
            return p;
        }
    }

    /// <summary>A mod from Nexus Mods in a pack (installed from Nexus, one click each).</summary>
    internal sealed class PackNexus
    {
        public int Id;
        public string Name = "", Author = "", Thumb = "";
    }

    /// <summary>The website's mod packs: browse them, subscribe, and make and edit your own (do=packs, do=pack, account.pack.*).</summary>
    internal static class SitePacks
    {
        /// <summary>The player's own packs, as last loaded (for "Add to pack").</summary>
        public static List<SitePack> Mine = new List<SitePack>();

        public static IEnumerator List(Action<List<SitePack>, string> done)
        {
            JObject json = null; string err = null;
            yield return Community.GetJson("do=packs", j => json = j, e => err = e);
            if (err != null) { done(null, err); yield break; }
            done((json["packs"] as JArray ?? new JArray()).Select(SitePack.Parse).Where(p => p.Id > 0).ToList(), null);
        }

        public static IEnumerator Details(int id, Action<SitePack, string> done)
        {
            JObject json = null; string err = null;
            yield return Community.GetJson("do=pack&id=" + id, j => json = j, e => err = e);
            if (err != null) { done(null, err == "Mod not found" ? "That pack isn't there any more." : err); yield break; }
            done(SitePack.Parse(json), null);
        }

        public static IEnumerator LoadMine(Action<string> done)
        {
            JObject j = null; long code = 0;
            yield return SiteAccount.Post("account.packs", new Dictionary<string, string>(), (x, c) => { j = x; code = c; });
            if (code != 200 || !(j?["packs"] is JArray arr)) { done(Err(j, code, "Couldn't load your packs.")); yield break; }
            Mine = arr.Select(SitePack.Parse).ToList();
            foreach (var p in Mine) p.Author = SiteAccount.Name;   // your own packs
            done(null);
        }

        /// <summary>Makes a new pack, optionally starting with some mods. Answers the new pack.</summary>
        public static IEnumerator Create(string name, IEnumerable<int> mods, Action<SitePack, string> done)
            => Change("account.pack.create", new Dictionary<string, string> { ["name"] = name, ["mods"] = string.Join(",", (mods ?? new int[0]).Select(i => i.ToString()).ToArray()) }, done);

        public static IEnumerator Rename(int id, string name, Action<SitePack, string> done)
            => Change("account.pack.update", new Dictionary<string, string> { ["id"] = id.ToString(), ["name"] = name }, done);

        public static IEnumerator Add(int id, int mod, Action<SitePack, string> done)
            => Change("account.pack.add", new Dictionary<string, string> { ["id"] = id.ToString(), ["mod"] = mod.ToString() }, done);

        public static IEnumerator Remove(int id, int mod, Action<SitePack, string> done)
            => Change("account.pack.remove", new Dictionary<string, string> { ["id"] = id.ToString(), ["mod"] = mod.ToString() }, done);

        /// <summary>Adds a Nexus Mods mod (its Nexus id) to one of your packs.</summary>
        public static IEnumerator AddNexus(int id, int nexusId, Action<SitePack, string> done)
            => Change("account.pack.add-nexus", new Dictionary<string, string> { ["id"] = id.ToString(), ["nexus"] = nexusId.ToString() }, done);

        public static IEnumerator RemoveNexus(int id, int nexusId, Action<SitePack, string> done)
            => Change("account.pack.remove-nexus", new Dictionary<string, string> { ["id"] = id.ToString(), ["nexus"] = nexusId.ToString() }, done);

        public static IEnumerator Delete(int id, Action<bool, string> done)
        {
            JObject j = null; long code = 0;
            yield return SiteAccount.Post("account.pack.delete", new Dictionary<string, string> { ["id"] = id.ToString() }, (x, c) => { j = x; code = c; });
            if (code != 200) { done(false, Err(j, code, "Couldn't delete the pack.")); yield break; }
            Mine.RemoveAll(p => p.Id == id);
            done(true, null);
        }

        public static IEnumerator Subscribe(int id, bool on, Action<bool, bool, string> done)
        {
            JObject j = null; long code = 0;
            yield return SiteAccount.Post("account.pack.subscribe", new Dictionary<string, string> { ["id"] = id.ToString(), ["on"] = on ? "1" : "0" }, (x, c) => { j = x; code = c; });
            if (code != 200 || j == null) { done(false, false, Err(j, code, "Couldn't change the subscription.")); yield break; }
            done(true, (bool?)j["subscribed"] ?? on, null);
        }

        private static IEnumerator Change(string what, Dictionary<string, string> form, Action<SitePack, string> done)
        {
            JObject j = null; long code = 0;
            yield return SiteAccount.Post(what, form, (x, c) => { j = x; code = c; });
            if (code != 200 || j == null || j["id"] == null) { done(null, Err(j, code, "Couldn't change the pack.")); yield break; }
            var p = SitePack.Parse(j);
            p.Author = SiteAccount.Name;
            Mine.RemoveAll(x => x.Id == p.Id);
            Mine.Insert(0, p);
            done(p, null);
        }

        private static string Err(JObject j, long code, string fallback) =>
            (string)j?["error"] ?? (code == 0 ? "Couldn't reach " + Community.HostName + "." : code == 401 ? "Sign in to your website account first." : fallback);
    }
}

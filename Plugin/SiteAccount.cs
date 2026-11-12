using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    /// <summary>
    /// The player's own account on the community website, connected to the Mods menu when they sign in on the website.
    /// The site hands back a long random key that stays in BepInEx\NomadModManager\account.json on this PC; it's sent
    /// with installs (so they count as the player's downloads and the site can tell them about updates), endorsements
    /// and the one-time "Website account" link that signs the browser in. The site only keeps the key's hash.
    /// </summary>
    internal static class SiteAccount
    {
        public static string Key = "", Name = "";
        public static int Id, Notifications;
        public static bool SteamLinked, Declined, Busy, Checked;
        private static bool _loaded;

        private static string FilePath => Path.Combine(Pending.Dir(Paths.GameRootPath), "account.json");
        public static bool Has { get { if (Edition.Nexus) return false; Load(); return Key.Length == 64; } }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var j = JObject.Parse(File.ReadAllText(FilePath));
                Key = (string)j["Key"] ?? ""; Name = (string)j["Name"] ?? ""; Id = (int?)j["Id"] ?? 0; Declined = (bool?)j["Declined"] ?? false;
                if (Key.Length != 64) Key = "";
            }
            catch (Exception e) { ModManagerPlugin.Log?.LogWarning("Couldn't read account.json: " + e.Message); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, new JObject { ["Key"] = Key, ["Name"] = Name, ["Id"] = Id, ["Declined"] = Declined,
                    ["Note"] = "Your Nomad Drive Mods website account for the Mods menu. Keep this file private: it signs in as you." }.ToString());
            }
            catch (Exception e) { ModManagerPlugin.Log?.LogWarning("Couldn't save account.json: " + e.Message); }
        }

        public static void Decline() { Load(); Declined = true; Save(); }

        /// <summary>The player's Steam name, from the game's own Steam library (Facepunch.Steamworks), or "".</summary>
        public static string SteamName()
        {
            try
            {
                var t = Type.GetType("Steamworks.SteamClient, Facepunch.Steamworks.Win64");
                if (t == null) return "";
                if (!(t.GetProperty("IsValid")?.GetValue(null, null) is bool ok) || !ok) return "";
                return t.GetProperty("Name")?.GetValue(null, null) as string ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Adds the key to a request to the site (only to the site's own address).</summary>
        public static void Sign(UnityWebRequest req)
        {
            if (Has && SameSite(req.url)) req.SetRequestHeader("X-Nomad-Key", Key);
        }

        private static bool SameSite(string url)
        {
            try { return new Uri(url).Host.Equals(Community.HostName, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        internal static IEnumerator Post(string what, Dictionary<string, string> form, Action<JObject, long> done)
        {
            if (Edition.Nexus) { done(null, 0); yield break; }
            var body = string.Join("&", form.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")).ToArray());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var req = new UnityWebRequest(Community.Site + "index.php?p=api&do=" + what, "POST", new DownloadHandlerBuffer(), new UploadHandlerRaw(Encoding.UTF8.GetBytes(body))))
                {
                    req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                    if (Has) req.SetRequestHeader("X-Nomad-Key", Key);
                    req.timeout = 20;
                    yield return req.SendWebRequest();
                    if (req.responseCode == 0 && attempt == 0 && Community.TryHttpFallback()) continue;
                    JObject j = null;
                    try { j = JObject.Parse(req.downloadHandler.text); } catch { }
                    done(j, req.responseCode);
                    yield break;
                }
            }
        }

        private static string Err(JObject j, long code, string fallback) =>
            (string)j?["error"] ?? (code == 0 ? "Couldn't reach " + Community.HostName + "." : fallback);

        /// <summary>Waiting for the player to press Connect on the website (the code shown in the menu), or null.</summary>
        public static string PairCode;

        /// <summary>
        /// Signs in to the player's own website account: the site gives a code and opens in the browser, where they log
        /// in or make an account themselves (password or Steam) and press Connect; then the menu picks up the key.
        /// The menu never makes an account and the password never goes through the game.
        /// </summary>
        public static IEnumerator SignIn(Action<bool, string> done)
        {
            if (Edition.Nexus) { done(false, "No website account in the Nexus edition of the Mods menu."); yield break; }
            if (Busy) yield break;
            Busy = true;
            try
            {
                JObject j = null; long code = 0;
                yield return Post("account.pair", new Dictionary<string, string>(), (x, c) => { j = x; code = c; });
                var url = (string)j?["url"] ?? ""; var pc = (string)j?["code"] ?? ""; var secret = (string)j?["secret"] ?? "";
                if (code != 200 || !url.StartsWith(Community.Site, StringComparison.OrdinalIgnoreCase) || pc.Length == 0 || secret.Length == 0)
                { done(false, Err(j, code, "Couldn't start signing in.")); yield break; }
                PairCode = pc;
                Application.OpenURL(url);
                var until = Time.realtimeSinceStartup + 600f;
                while (Time.realtimeSinceStartup < until && PairCode != null)
                {
                    yield return new WaitForSecondsRealtime(2.5f);
                    if (PairCode == null) break; // cancelled
                    yield return Post("account.pair.check", new Dictionary<string, string> { ["code"] = pc, ["secret"] = secret }, (x, c) => { j = x; code = c; });
                    var status = (string)j?["status"] ?? "";
                    if (status == "expired") break;
                    if (status != "done") continue;
                    var key = (string)j["key"] ?? "";
                    if (key.Length != 64) break;
                    Key = key; Name = (string)j["name"] ?? ""; Id = (int?)j["id"] ?? 0; Declined = false; Checked = false;
                    Save();
                    PairCode = null;
                    done(true, $"Signed in as “{Name}”. Mods you install here now count as yours, and the site tells you when they update.");
                    yield break;
                }
                var cancelled = PairCode == null;
                PairCode = null;
                if (!cancelled) done(false, "Signing in timed out. Press Sign in to try again.");
            }
            finally { Busy = false; }
        }

        public static void CancelSignIn() => PairCode = null;

        /// <summary>Checks the account is still there and how many notifications are waiting.</summary>
        public static IEnumerator Refresh()
        {
            if (!Has || Busy) yield break;
            Checked = true;
            JObject j = null; long code = 0;
            yield return Post("account.me", new Dictionary<string, string>(), (x, c) => { j = x; code = c; });
            if (code == 401) { ModManagerPlugin.Log?.LogWarning("The website account was removed on the site; the Mods menu forgot it."); Key = ""; Name = ""; Id = 0; Save(); yield break; }
            if (code != 200 || j == null) yield break;
            Name = (string)j["name"] ?? Name; Notifications = (int?)j["notifications"] ?? 0; SteamLinked = (bool?)j["steamLinked"] ?? false;
        }

        /// <summary>Opens the website in the browser, signed in, at a page (e.g. "/notifications").</summary>
        public static IEnumerator OpenSignedIn(string page, Action<bool, string> done)
        {
            if (Edition.Nexus) { done(false, ""); yield break; }
            if (!Has) { Application.OpenURL(Community.Site + page.TrimStart('/')); done(true, ""); yield break; }
            JObject j = null; long code = 0;
            yield return Post("account.login", new Dictionary<string, string> { ["next"] = page }, (x, c) => { j = x; code = c; });
            var url = (string)j?["url"] ?? "";
            if (code != 200 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !SameSite(url)) { done(false, Err(j, code, "Couldn't sign in to the site.")); yield break; }
            Application.OpenURL(url);
            if (page.StartsWith("/notifications")) Notifications = 0;
            done(true, "Opened " + Community.HostName + " in your browser, signed in as " + Name + ".");
        }

        /// <summary>Endorses or un-endorses a website mod.</summary>
        /// <summary>Subscribes to a mod on the website (it then installs and stays updated by itself), or ends it.</summary>
        public static IEnumerator Subscribe(int modId, bool on, Action<bool, bool, string> done)
        {
            JObject j = null; long code = 0;
            yield return Post("account.subscribe", new Dictionary<string, string> { ["id"] = modId.ToString(), ["on"] = on ? "1" : "0" }, (x, c) => { j = x; code = c; });
            if (code != 200 || j == null) { done(false, false, Err(j, code, "Couldn't change the subscription.")); yield break; }
            done(true, (bool?)j["subscribed"] ?? on, "");
        }

        public static IEnumerator Endorse(int modId, bool on, Action<bool, bool, int, string> done)
        {
            JObject j = null; long code = 0;
            yield return Post("endorse", new Dictionary<string, string> { ["id"] = modId.ToString(), ["on"] = on ? "1" : "0" }, (x, c) => { j = x; code = c; });
            if (code != 200 || j == null) { done(false, false, 0, Err(j, code, "Couldn't endorse the mod.")); yield break; }
            done(true, (bool?)j["endorsed"] ?? on, (int?)j["endorsements"] ?? 0, "");
        }
    }
}

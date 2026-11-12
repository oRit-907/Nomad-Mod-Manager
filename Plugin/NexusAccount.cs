using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NomadModManager
{
    /// <summary>
    /// Sign in with Nexus: the player's own Nexus Mods account, through Nexus's sign-in page (OAuth 2.0 with PKCE, as
    /// Nexus asks apps to do). The menu never sees the password. Nexus sends the sign-in back to this PC only
    /// (http://127.0.0.1:47380/nexus-callback); the tokens are kept in BepInEx\NomadModManager\nexus-account.json on this
    /// PC, never sent anywhere but Nexus, and never put in exported logs. Everything done with the account is something
    /// the player pressed (Endorse, Track). Signing out deletes the tokens; access can also be taken back on Nexus.
    ///
    /// Before Nexus registers the app (its client id goes in <see cref="ClientIdBuiltIn"/>), sign-in isn't available;
    /// for testing, a personal API key can be pasted instead (Nexus allows those for testing and personal use only).
    /// </summary>
    internal static class NexusAccount
    {
        /// <summary>The client id Nexus gives this app when it's registered ("" until then). The config can set one for testing.</summary>
        public const string ClientIdBuiltIn = "";
        public const int CallbackPort = 47380;
        public static string RedirectUri => $"http://127.0.0.1:{CallbackPort}/nexus-callback";
        private const string Authorize = "https://users.nexusmods.com/oauth/authorize";
        private const string TokenUrl = "https://users.nexusmods.com/oauth/token";
        private const string Api = "https://api.nexusmods.com/v1";

        public static string ClientId
        {
            get
            {
                var c = ModManagerPlugin.NexusClientId?.Value?.Trim() ?? "";
                return c.Length > 0 ? c : ClientIdBuiltIn;
            }
        }

        public static bool CanSignIn => ClientId.Length > 0;
        public static bool SignedIn => _access.Length > 0 || _apiKey.Length > 0;
        public static bool UsingTestKey => _apiKey.Length > 0;
        public static string UserName { get; private set; } = "";
        public static bool Premium { get; private set; }
        public static bool SigningIn { get; private set; }
        public static string Status { get; private set; } = "";

        private static string _access = "", _refresh = "", _apiKey = "";
        private static long _expiresAt;   // unix seconds
        private static bool _loaded;

        private static string FilePath => Path.Combine(Pending.Dir(Paths.GameRootPath), "nexus-account.json");

        // ------------------------------------------------------------------ keeping it

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var j = JObject.Parse(File.ReadAllText(FilePath));
                _access = (string)j["access"] ?? ""; _refresh = (string)j["refresh"] ?? ""; _apiKey = (string)j["apikey"] ?? "";
                _expiresAt = (long?)j["expires"] ?? 0;
                UserName = (string)j["name"] ?? ""; Premium = (bool?)j["premium"] ?? false;
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Nexus account: couldn't read the saved sign-in (" + e.Message + ")."); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var j = new JObject { ["access"] = _access, ["refresh"] = _refresh, ["apikey"] = _apiKey, ["expires"] = _expiresAt, ["name"] = UserName, ["premium"] = Premium };
                File.WriteAllText(FilePath, j.ToString());
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Nexus account: couldn't save the sign-in (" + e.Message + ")."); }
        }

        public static void SignOut()
        {
            _access = _refresh = _apiKey = ""; _expiresAt = 0; UserName = ""; Premium = false;
            Endorsed.Clear(); Tracked.Clear(); _listsLoaded = false;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            Activity.Add("Signed out of Nexus Mods");
        }

        // ------------------------------------------------------------------ signing in

        /// <summary>Opens Nexus's sign-in page in the player's web browser and waits (up to 5 minutes) for it to come back.</summary>
        public static IEnumerator SignIn(Action<bool, string> done)
        {
            if (SigningIn) yield break;
            if (!CanSignIn) { done(false, "Sign in with Nexus isn't switched on yet: Nexus hasn't registered the Mods menu."); yield break; }
            SigningIn = true; Status = "Waiting for you to sign in on Nexus in your web browser…";
            string code = null, error = null;
            TcpListener listener = null;
            try
            {
                var verifier = RandomHex(48);
                string challenge;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    challenge = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var state = RandomHex(16);
                try { listener = new TcpListener(IPAddress.Loopback, CallbackPort); listener.Start(); }
                catch (Exception e) { Finish(done, false, $"Couldn't wait for Nexus's answer on this PC (port {CallbackPort} is in use: {e.Message})."); yield break; }

                var url = Authorize + "?client_id=" + Uri.EscapeDataString(ClientId) + "&response_type=code&scope=" +
                          "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&state=" + state +
                          "&code_challenge_method=S256&code_challenge=" + challenge;
                Application.OpenURL(url);

                var finished = false;
                var l = listener;
                new Thread(() =>
                {
                    try
                    {
                        var until = DateTime.UtcNow.AddMinutes(5);
                        while (DateTime.UtcNow < until)
                        {
                            if (!l.Server.Poll(500000, SelectMode.SelectRead)) continue;
                            using (var c = l.AcceptTcpClient())
                            {
                                c.ReceiveTimeout = 5000;
                                var s = c.GetStream();
                                var line = ReadLine(s);   // GET /nexus-callback?code=…&state=… HTTP/1.1
                                var parts = line.Split(' ');
                                var target = parts.Length > 1 ? parts[1] : "";
                                if (!target.StartsWith("/nexus-callback", StringComparison.Ordinal)) { Reply(s, 404, "Not here."); continue; }
                                var q = Query(target);
                                if (!q.TryGetValue("state", out var st) || st != state) { Reply(s, 400, "This sign-in wasn't started by the Mods menu. Try again from the game."); continue; }
                                if (q.TryGetValue("code", out var cd) && cd.Length > 0)
                                {
                                    code = cd;
                                    Reply(s, 200, "You're signed in to Nexus Mods in the Mods menu. You can close this tab and go back to Nomad Drive.");
                                }
                                else
                                {
                                    error = q.TryGetValue("error_description", out var ed) ? ed : q.TryGetValue("error", out var er) ? er : "Nexus didn't sign you in.";
                                    Reply(s, 200, "Not signed in: " + Html(error) + ". You can close this tab.");
                                }
                                return;
                            }
                        }
                        error = "The sign-in took longer than 5 minutes. Try again.";
                    }
                    catch (Exception e) { error = e.Message; }
                    finally { finished = true; }
                }) { IsBackground = true, Name = "NexusSignIn" }.Start();
                while (!finished) yield return null;
                if (code == null) { Finish(done, false, error ?? "Not signed in."); yield break; }

                Status = "Signing in…";
                JObject tokens = null; string terr = null;
                yield return PostForm(TokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["redirect_uri"] = RedirectUri, ["scope"] = "",
                    ["client_id"] = ClientId, ["code"] = code, ["code_verifier"] = verifier,
                }, null, (j, e) => { tokens = j as JObject; terr = e; });
                if (tokens == null || !Take(tokens)) { Finish(done, false, "Nexus didn't finish the sign-in" + (terr != null ? " (" + terr + ")" : "") + "."); yield break; }
                _apiKey = "";
                Save();
                Activity.Add("Signed in to Nexus Mods as " + UserName);
                Finish(done, true, "Signed in to Nexus Mods as " + UserName + ".");
            }
            finally
            {
                try { listener?.Stop(); } catch { }
                SigningIn = false;
            }
        }

        private static void Finish(Action<bool, string> done, bool ok, string msg)
        {
            Status = ok ? "" : msg;
            if (!ok) ModManagerPlugin.Log.LogWarning("Nexus sign-in: " + msg);
            try { done?.Invoke(ok, msg); } catch { }
        }

        /// <summary>For testing before Nexus registers the app: the player's personal API key (from nexusmods.com › Site preferences › API keys), from the clipboard.</summary>
        public static IEnumerator UseTestKey(string key, Action<bool, string> done)
        {
            key = (key ?? "").Trim();
            if (key.Length < 20 || key.Any(char.IsWhiteSpace)) { done(false, "Copy your personal API key from Nexus first (Site preferences › API keys), then press this again."); yield break; }
            JToken user = null; string err = null;
            yield return Get(Api + "/users/validate.json", ("apikey", key), (j, e) => { user = j; err = e; });
            if (user == null) { done(false, "Nexus didn't accept that key" + (err != null ? " (" + err + ")" : "") + "."); yield break; }
            _apiKey = key; _access = _refresh = ""; _expiresAt = 0;
            UserName = (string)user["name"] ?? ""; Premium = (bool?)user["is_premium"] ?? false;
            Save();
            Activity.Add("Signed in to Nexus Mods (personal API key, for testing) as " + UserName);
            done(true, "Signed in to Nexus Mods as " + UserName + " with your personal API key (for testing).");
        }

        /// <summary>Reads Nexus's token answer: the tokens, when they run out, and who signed in (from the token itself).</summary>
        private static bool Take(JObject t)
        {
            var access = (string)t["access_token"] ?? "";
            if (access.Length == 0) return false;
            _access = access;
            _refresh = (string)t["refresh_token"] ?? _refresh;
            _expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ((long?)t["expires_in"] ?? 3600) - 60;
            try
            {
                var payload = access.Split('.')[1].Replace('-', '+').Replace('_', '/');
                payload += new string('=', (4 - payload.Length % 4) % 4);
                var user = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))["user"];
                UserName = (string)user?["username"] ?? UserName;
                Premium = (user?["membership_roles"] as JArray)?.Any(r => (string)r == "premium") ?? false;
            }
            catch { }
            return true;
        }

        /// <summary>The header that says who's asking: the sign-in's token (renewed when it runs out), or the test key.</summary>
        private static IEnumerator Auth(Action<(string, string)?> got)
        {
            Load();
            if (_apiKey.Length > 0) { got(("apikey", _apiKey)); yield break; }
            if (_access.Length == 0) { got(null); yield break; }
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= _expiresAt && _refresh.Length > 0)
            {
                JToken t = null; long code = 0;
                yield return PostForm(TokenUrl, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = _refresh, ["client_id"] = ClientId },
                    null, (j, e) => t = j, c => code = c);
                if (t is JObject o && Take(o)) Save();
                else if (code >= 400 && code < 500)
                {
                    // Nexus no longer accepts it (the player took the app's access back on Nexus): signed out.
                    ModManagerPlugin.Log.LogMessage("Nexus sign-in ended (Nexus didn't renew it). Sign in again from Settings.");
                    SignOut();
                    got(null); yield break;
                }
            }
            got(("Authorization", "Bearer " + _access));
        }

        // ------------------------------------------------------------------ what the account is used for (all on the player's press)

        /// <summary>Mods this account has endorsed and tracks (Nomad Drive only), read once when needed.</summary>
        public static readonly HashSet<int> Endorsed = new HashSet<int>(), Tracked = new HashSet<int>();
        private static bool _listsLoaded, _listsLoading;

        public static IEnumerator LoadLists()
        {
            if (!SignedIn || _listsLoaded || _listsLoading) yield break;
            _listsLoading = true;
            try
            {
                (string, string)? auth = null;
                yield return Auth(a => auth = a);
                if (auth == null) yield break;
                JToken e = null, t = null;
                yield return Get(Api + "/user/endorsements.json", auth.Value, (j, err) => e = j);
                yield return Get(Api + "/user/tracked_mods.json", auth.Value, (j, err) => t = j);
                Endorsed.Clear(); Tracked.Clear();
                foreach (var x in (e as JArray ?? new JArray()).OfType<JObject>())
                    if ((string)x["domain_name"] == Nexus.GameDomain && ((string)x["status"] ?? "") == "Endorsed") Endorsed.Add((int?)x["mod_id"] ?? 0);
                foreach (var x in (t as JArray ?? new JArray()).OfType<JObject>())
                    if ((string)x["domain_name"] == Nexus.GameDomain) Tracked.Add((int?)x["mod_id"] ?? 0);
                _listsLoaded = e != null || t != null;
            }
            finally { _listsLoading = false; }
        }

        /// <summary>Endorses a mod (or takes the endorsement back). Nexus only lets you endorse mods you've downloaded.</summary>
        public static IEnumerator Endorse(int modId, string version, bool on, Action<bool, string> done)
        {
            (string, string)? auth = null;
            yield return Auth(a => auth = a);
            if (auth == null) { done(false, "Sign in with Nexus first (Settings)."); yield break; }
            JToken r = null; string err = null;
            yield return PostForm($"{Api}/games/{Nexus.GameDomain}/mods/{modId}/{(on ? "endorse" : "abstain")}.json",
                new Dictionary<string, string> { ["version"] = version ?? "" }, auth.Value, (j, e) => { r = j; err = e; });
            if (r == null)
            {
                var why = err ?? "";
                if (why.IndexOf("NOT_DOWNLOADED", StringComparison.OrdinalIgnoreCase) >= 0) why = "Nexus only lets you endorse mods you've downloaded from it";
                else if (why.IndexOf("TOO_SOON", StringComparison.OrdinalIgnoreCase) >= 0) why = "Nexus asks you to wait a bit after downloading before endorsing";
                else if (why.IndexOf("IS_OWN_MOD", StringComparison.OrdinalIgnoreCase) >= 0) why = "You can't endorse your own mod";
                done(false, why.Length > 0 ? why + "." : "Nexus didn't take the endorsement."); yield break;
            }
            if (on) Endorsed.Add(modId); else Endorsed.Remove(modId);
            done(true, null);
        }

        /// <summary>Tracks a mod on Nexus (its updates show in the player's Nexus notifications), or stops tracking it.</summary>
        public static IEnumerator Track(int modId, bool on, Action<bool, string> done)
        {
            (string, string)? auth = null;
            yield return Auth(a => auth = a);
            if (auth == null) { done(false, "Sign in with Nexus first (Settings)."); yield break; }
            JToken r = null; string err = null;
            var url = $"{Api}/user/tracked_mods.json?domain_name={Nexus.GameDomain}";
            yield return Send(on ? "POST" : "DELETE", url, Form(new Dictionary<string, string> { ["mod_id"] = modId.ToString() }), auth.Value, (j, e) => { r = j; err = e; }, null);
            if (r == null) { done(false, err ?? "Nexus didn't change tracking."); yield break; }
            if (on) Tracked.Add(modId); else Tracked.Remove(modId);
            done(true, null);
        }

        // ------------------------------------------------------------------ talking to Nexus

        private static string Form(Dictionary<string, string> f) => string.Join("&", f.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")).ToArray());

        private static IEnumerator PostForm(string url, Dictionary<string, string> form, (string, string)? auth, Action<JToken, string> done, Action<long> code = null)
            => Send("POST", url, Form(form), auth, done, code);

        private static IEnumerator Get(string url, (string, string)? auth, Action<JToken, string> done) => Send("GET", url, null, auth, done, null);

        private static IEnumerator Send(string method, string url, string form, (string, string)? auth, Action<JToken, string> done, Action<long> code)
        {
            using (var req = new UnityWebRequest(url, method))
            {
                if (form != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(form));
                    req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("Application-Name", "NomadModManager");
                req.SetRequestHeader("Application-Version", ModManagerPlugin.Version);
                if (auth != null) req.SetRequestHeader(auth.Value.Item1, auth.Value.Item2);
                req.timeout = 20;
                yield return req.SendWebRequest();
                code?.Invoke(req.responseCode);
                var text = req.downloadHandler?.text ?? "";
                if (req.result != UnityWebRequest.Result.Success)
                {
                    string msg = null;
                    try { var j = JToken.Parse(text); msg = (string)j["message"] ?? (string)j["error_description"] ?? (string)j["error"]; } catch { }
                    done(null, msg ?? req.error);
                    yield break;
                }
                try { done(text.Trim().Length == 0 ? new JObject() : JToken.Parse(text), null); }
                catch { done(new JObject(), null); }
            }
        }

        // ------------------------------------------------------------------ the local answer page

        private static string Html(string t) => (t ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")).ToArray());
        }

        private static string ReadLine(NetworkStream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 8192) { if (s.Read(one, 0, 1) <= 0) break; if (one[0] == (byte)'\n') break; if (one[0] != (byte)'\r') sb.Append((char)one[0]); }
            return sb.ToString();
        }

        private static Dictionary<string, string> Query(string target)
        {
            var d = new Dictionary<string, string>();
            var i = target.IndexOf('?');
            if (i < 0) return d;
            foreach (var kv in target.Substring(i + 1).Split('&'))
            {
                var e = kv.IndexOf('=');
                if (e <= 0) continue;
                d[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
            }
            return d;
        }

        private static void Reply(NetworkStream s, int status, string text)
        {
            var html = "<!doctype html><meta charset=utf-8><title>Nomad Mod Manager</title><body style=\"font-family:sans-serif;background:#1d2024;color:#f3f4f5;padding:40px\"><h2>Nomad Mod Manager</h2><p>" + text + "</p>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            try { s.Write(head, 0, head.Length); s.Write(body, 0, body.Length); s.Flush(); } catch { }
        }
    }
}

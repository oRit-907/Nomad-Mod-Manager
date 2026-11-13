using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using EvilCore.Networking;
using EvilCore.Popups;
using EvilCore.UI.MainMenu.Panels;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace NomadModManager
{
    // ======================================================================================================
    // What a host puts on its lobby (readable from the lobby list before joining)
    // ======================================================================================================

    /// <summary>
    /// The host's multiplayer mods, the ones guests must have, and any settings it shares, as lobby attributes.
    /// Lobby attributes show up in lobby search results, so the lobby list can check a game before you join it.
    /// </summary>
    internal sealed class LobbyInfo
    {
        public const string ModsKey = "nmm_host", ReqKey = "nmm_req", CfgKey = "nmm_cfg";
        private const int Max = 990, CfgParts = 3;
        private const char Sep = '\u0001';

        public string LobbyId = "";
        public List<SharedMod> Mods = new List<SharedMod>();       // the host's mods that change multiplayer
        public List<SharedMod> Required = new List<SharedMod>();   // the ones guests must have
        public string Settings = "";                               // shared settings (see HostSettings)

        /// <summary>Lobbies seen in the lobby list, by id.</summary>
        public static readonly Dictionary<string, LobbyInfo> Seen = new Dictionary<string, LobbyInfo>();

        public static string Build(List<SharedMod> mine, ModCatalog cat)
        {
            var net = mine.Where(m => m.Net).ToList();
            var req = net.Where(HostPrefs.IsRequired).ToList();
            return ModList.Encode(net, Max) + Sep + ModList.Encode(req, Max) + Sep + HostSettings.Build(cat);
        }

        /// <summary>The attributes to set. Always all of them, so leftovers from a longer list are overwritten.</summary>
        public static Dictionary<string, string> Split(string built)
        {
            var p = built.Split(Sep);
            var d = new Dictionary<string, string> { [ModsKey] = p.Length > 0 ? p[0] : "", [ReqKey] = p.Length > 1 ? p[1] : "" };
            var cfg = p.Length > 2 ? p[2] : "";
            for (var i = 0; i < CfgParts; i++)
                d[CfgKey + i] = cfg.Length > i * Max ? cfg.Substring(i * Max, Math.Min(Max, cfg.Length - i * Max)) : "";
            return d;
        }

        public static LobbyInfo FromDetails(LobbyDetails details, string lobbyId)
        {
            var mods = Attr(details, ModsKey);
            if (string.IsNullOrEmpty(mods) || mods.Trim().Length == 0) return null;
            var info = new LobbyInfo { LobbyId = lobbyId ?? "" };
            info.Mods = ModList.Decode(mods.Trim()) ?? new List<SharedMod>();
            info.Required = ModList.Decode((Attr(details, ReqKey) ?? "").Trim()) ?? new List<SharedMod>();
            var sb = new StringBuilder();
            for (var i = 0; i < CfgParts; i++) sb.Append(Attr(details, CfgKey + i) ?? "");
            info.Settings = sb.ToString().Trim();
            return info;
        }

        private static string Attr(LobbyDetails details, string key)
        {
            try
            {
                var o = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
                if (details.CopyAttributeByKey(ref o, out var a) == Result.Success && a.HasValue) return a.Value.Data?.Value.AsUtf8;
            }
            catch { }
            return null;
        }

        /// <summary>Your multiplayer mods compared with the host's.</summary>
        public ModDiff DiffWith(List<SharedMod> mine) => ModDiff.Of(mine.Where(m => m.Net).ToList(), Mods);

        /// <summary>
        /// Nothing stops you joining: you have every mod the host requires, on the same version. The host's other
        /// multiplayer mods are optional (the host said so), and yours the host doesn't have are paused while you're in
        /// their game (see SafeJoin), so neither gets you dropped.
        /// </summary>
        public bool CanJoin(List<SharedMod> mine) => MissingRequired(mine).Count == 0;

        /// <summary>The host's multiplayer mods you don't have that the host marked optional.</summary>
        public List<SharedMod> MissingOptional(List<SharedMod> mine)
        {
            var req = new HashSet<string>(Required.Select(r => r.Key));
            return DiffWith(mine).TheyHaveNet.Where(m => !req.Contains(m.Key)).ToList();
        }

        /// <summary>Mods the host requires that you don't have on (or have on another version).</summary>
        public List<SharedMod> MissingRequired(List<SharedMod> mine)
        {
            var d = ModDiff.Of(mine, Required);
            return d.TheyHave.Concat(d.Versions.Select(v => v.Theirs)).ToList();
        }
    }

    // ======================================================================================================
    // Host choices: which mods guests must have, which mods' settings guests use
    // ======================================================================================================

    internal static class HostPrefs
    {
        private static ConfigEntry<string> _notRequired, _shareSettings;

        private static void Bind()
        {
            if (_notRequired != null) return;
            var cfg = ModManagerPlugin.I.Config;
            _notRequired = cfg.Bind("Multiplayer", "NotRequired", "",
                "When you host: mods that change multiplayer which guests don't have to have (comma-separated; managed in Mods > Multiplayer).");
            _shareSettings = cfg.Bind("Multiplayer", "ShareSettings", "",
                "When you host: mods whose settings guests use while they're in your game (comma-separated; managed in Mods > Multiplayer).");
        }

        public static string KeyOf(ModEntry m) => m.NexusId > 0 ? "#" + m.NexusId : "~" + ModCatalog.NameKey(m.Name);

        private static HashSet<string> Set(ConfigEntry<string> e) =>
            new HashSet<string>((e.Value ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);

        private static void Put(ConfigEntry<string> e, string key, bool on)
        {
            var s = Set(e);
            if (on) s.Add(key); else s.Remove(key);
            e.Value = string.Join(",", s.OrderBy(x => x).ToArray());
            LobbyMods.Changed();
        }

        // Every mod that changes multiplayer is required unless the host says otherwise.
        public static bool IsRequired(SharedMod m) { Bind(); return !Set(_notRequired).Contains(m.Key); }
        public static bool IsRequired(ModEntry m) { Bind(); return !Set(_notRequired).Contains(KeyOf(m)); }
        public static void SetRequired(ModEntry m, bool on) { Bind(); Put(_notRequired, KeyOf(m), !on); }

        public static bool SharesSettings(ModEntry m) { Bind(); return Set(_shareSettings).Contains(KeyOf(m)); }
        public static void SetShareSettings(ModEntry m, bool on) { Bind(); Put(_shareSettings, KeyOf(m), on); }
    }

    // ======================================================================================================
    // Host settings: guests use the host's values for the mods the host picked, and get their own back after
    // ======================================================================================================

    internal static class HostSettings
    {
        private static string _applied;
        private static readonly Dictionary<ConfigEntryBase, object> Originals = new Dictionary<ConfigEntryBase, object>();
        public static readonly List<string> UsingFor = new List<string>();

        /// <summary>"guid\tsection\tkey\tvalue" lines for the mods the host shares, while they're running.</summary>
        public static string Build(ModCatalog cat)
        {
            var sb = new StringBuilder();
            foreach (var m in cat.Mods.Where(m => !m.IsSelf && Profiles.IsOn(m) && HostSettings.Shared(m)))
                foreach (var (guid, _) in ModSettings.PluginsOf(m))
                {
                    var cf = ModSettings.LiveConfig(guid);
                    if (cf == null) continue;
                    foreach (var def in cf.Keys.ToList())
                    {
                        ConfigEntryBase e;
                        try { e = cf[def]; } catch { continue; }
                        if (e.SettingType.Name == "KeyboardShortcut" || e.SettingType.Name == "KeyCode") continue;   // keys are personal
                        string v;
                        try { v = TomlTypeConverter.ConvertToString(e.BoxedValue, e.SettingType); } catch { continue; }
                        var line = Clean(guid) + "\t" + Clean(def.Section) + "\t" + Clean(def.Key) + "\t" + Clean(v) + "\n";
                        if (sb.Length + line.Length > 2900) return sb.ToString();
                        sb.Append(line);
                    }
                }
            return sb.ToString();
        }

        private static bool Shared(ModEntry m) => HostPrefs.SharesSettings(m);
        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace('\u0001', ' ');

        /// <summary>Guest side: use the host's values (in memory only, never written to your config files).</summary>
        public static void Apply(LobbyInfo info)
        {
            if (info == null || info.Settings == _applied) return;
            Restore(quiet: true);
            _applied = info.Settings;
            if (string.IsNullOrEmpty(info.Settings)) return;
            var mods = new HashSet<string>();
            foreach (var line in info.Settings.Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length < 4) continue;
                var cf = ModSettings.LiveConfig(f[0]);
                if (cf == null) continue;
                ConfigEntryBase e;
                try { e = cf[new ConfigDefinition(f[1], f[2])]; } catch { continue; }
                if (e == null) continue;
                try
                {
                    var value = TomlTypeConverter.ConvertToValue(f[3], e.SettingType);
                    if (Equals(value, e.BoxedValue)) continue;
                    if (!Originals.ContainsKey(e)) Originals[e] = e.BoxedValue;
                    Set(cf, e, value);
                    mods.Add(Bep.Plugins.TryGetValue(f[0], out var pi) ? pi.Metadata.Name : f[0]);
                }
                catch { }
            }
            UsingFor.Clear();
            UsingFor.AddRange(mods.OrderBy(x => x));
            if (UsingFor.Count > 0)
                ModManagerPlugin.I?.Menu?.Toast("Using the host's settings for " + string.Join(", ", UsingFor.ToArray()) + " while you're in their game.", true);
        }

        /// <summary>Puts your own values back (when you leave the game).</summary>
        public static void Restore(bool quiet = false)
        {
            if (Originals.Count == 0) { _applied = null; UsingFor.Clear(); return; }
            foreach (var kv in Originals.ToList())
            {
                try { Set(kv.Key.ConfigFile, kv.Key, kv.Value); } catch { }
            }
            Originals.Clear();
            _applied = null;
            if (!quiet && UsingFor.Count > 0) ModManagerPlugin.I?.Menu?.Toast("Your own settings are back for " + string.Join(", ", UsingFor.ToArray()) + ".", true);
            UsingFor.Clear();
        }

        private static void Set(ConfigFile cf, ConfigEntryBase e, object value)
        {
            var save = cf.SaveOnConfigSet;
            cf.SaveOnConfigSet = false;   // the host's values are only borrowed: never write them to your .cfg
            try { e.BoxedValue = value; }
            finally { cf.SaveOnConfigSet = save; }
        }
    }

    // ======================================================================================================
    // The lobby list: a mods badge on each game, and a check before joining
    // ======================================================================================================

    internal static class LobbyListPatches
    {
        private static readonly HashSet<string> JoinAnyway = new HashSet<string>();

        [HarmonyPatch(typeof(EOSLobbyManager), "ParseLobbyDetails")]
        [HarmonyPostfix]
        private static void Parsed(LobbyDetails details, LobbySearchResult __result)
        {
            try
            {
                if (string.IsNullOrEmpty(__result.LobbyId)) return;
                var info = LobbyInfo.FromDetails(details, __result.LobbyId);
                if (info != null) LobbyInfo.Seen[__result.LobbyId] = info; else LobbyInfo.Seen.Remove(__result.LobbyId);
            }
            catch { }
        }

        [HarmonyPatch(typeof(LobbyEntryUI), "Setup")]
        [HarmonyPostfix]
        private static void Row(LobbyEntryUI __instance, LobbySearchResult lobby)
        {
            try
            {
                if (!LobbyInfo.Seen.TryGetValue(lobby.LobbyId ?? "", out var info)) return;
                var label = Traverse.Create(__instance).Field("lobbyNameText").GetValue<TextMeshProUGUI>();
                if (label == null) return;
                var (text, color) = Badge(info);
                label.text = lobby.LobbyName + $"  <size=75%><color={color}>{text}</color></size>";
            }
            catch { }
        }

        /// <summary>The badge for a game in the lobby list.</summary>
        public static (string Text, string Color) Badge(LobbyInfo info)
        {
            var cat = ModManagerPlugin.I?.Catalog;
            if (cat == null) return ("Modded", "#C9B79C");
            var mine = LobbyMods.Mine(cat);
            var req = info.MissingRequired(mine).Count;
            if (req > 0) return ($"Needs {req} mod{(req == 1 ? "" : "s")}", "#E5735B");
            var opt = info.MissingOptional(mine).Count;
            var paused = info.DiffWith(mine).YouHaveNet.Count();
            var text = info.Mods.Count == 0 ? "Mods menu, no game-changing mods" : "Mods OK";
            if (opt > 0) text += $" · {opt} optional";
            if (paused > 0) text += $" · {paused} of yours paused";
            return (text, "#7CC87C");
        }

        [HarmonyPatch(typeof(JoinGamePanel), "ProceedToJoin")]
        [HarmonyPrefix]
        private static bool BeforeJoin(JoinGamePanel __instance, LobbySearchResult lobby)
        {
            try
            {
                var id = lobby.LobbyId ?? "";
                if (JoinAnyway.Remove(id)) return true;
                if (!LobbyInfo.Seen.TryGetValue(id, out var info)) return true;
                var cat = ModManagerPlugin.I?.Catalog;
                if (cat == null) return true;
                var mine = LobbyMods.Mine(cat);
                var missingReq = info.MissingRequired(mine);
                var d = info.DiffWith(mine);
                if (missingReq.Count == 0) return true;   // optional ones and yours the host lacks don't get you dropped (SafeJoin pauses yours)

                var popups = Traverse.Create(__instance).Field("_popupService").GetValue<IPopupService>();
                if (popups == null) return true;
                var body = new StringBuilder();
                if (missingReq.Count > 0) body.Append("The host needs every player to have: " + Names(missingReq) + ".\n");
                var get = info.MissingOptional(mine);
                if (get.Count > 0) body.Append("Optional (you can join without them): " + Names(get) + ".\n");
                if (d.VersionsNet.Any()) body.Append("On another version: " + string.Join(", ", d.VersionsNet.Select(v => $"{v.Theirs.Name} (host v{v.Theirs.Version}, you v{v.Mine.Version})").ToArray()) + ".\n");
                if (d.YouHaveNet.Any()) body.Append("The host doesn't have: " + Names(d.YouHaveNet) + " (their multiplayer parts are paused while you're in this game).\n");
                body.Append("\nJoining like this can fail or drop you. Match host gets you the same multiplayer mods (your own setup is saved, so you can go back after).");

                popups.Show(PopupRequest.Create()
                    .TitleLiteral(missingReq.Count > 0 ? "This game needs other mods" : "Your mods don't match the host's")
                    .BodyLiteral(body.ToString())
                    .ButtonLiteral("Match host", PopupButtonRole.Primary, () => ModManagerPlugin.I?.Menu?.OpenMatch(info, lobby.LobbyName))
                    .ButtonLiteral("Join anyway", PopupButtonRole.Secondary, () =>
                    {
                        JoinAnyway.Add(id);
                        AccessTools.Method(typeof(JoinGamePanel), "ProceedToJoin")?.Invoke(__instance, new object[] { lobby });
                    })
                    .Button(PopupButtonRole.Cancel)
                    .WithTone(PopupTone.Warning));
                return false;
            }
            catch (Exception e)
            {
                ModManagerPlugin.Log?.LogInfo("Lobby mod check skipped: " + e.Message);
                return true;
            }
        }

        private static string Names(IEnumerable<SharedMod> mods) => string.Join(", ", mods.Select(m => m.Name.Length > 0 ? m.Name : "mod #" + m.Id).ToArray());
    }

    // ======================================================================================================
    // Match host / Back to my mods
    // ======================================================================================================

    internal static class MatchHost
    {
        public const string SavedProfile = "My mods (before matching a host)";

        private static bool HasProfile => Profiles.Load().Any(p => p.Name == SavedProfile);
        /// <summary>There's something to go back to: the saved setup, or your own versions of mods matching replaced.</summary>
        public static bool HasSaved => HasProfile || Load().Site.Count + Load().Nexus.Count > 0;

        /// <summary>A mod to get on an exact version: from the website (mod + file) or from Nexus (mod + version).</summary>
        internal sealed class Want
        {
            public int SiteId, SiteVersionId, NexusId;
            public string Version = "", Name = "";
        }

        /// <summary>
        /// What matching changed, kept on disk so "Back to my mods" can put your own versions back (even after a restart):
        /// your website files and Nexus versions from before, and the website mods held on the host's version (their
        /// subscriptions don't update them while you're matched).
        /// </summary>
        private sealed class State
        {
            public Dictionary<int, int> Site = new Dictionary<int, int>();          // website mod -> your file before
            public Dictionary<int, string> Nexus = new Dictionary<int, string>();   // Nexus mod -> your version before
            public Dictionary<int, string> Names = new Dictionary<int, string>();   // for messages
            public List<int> Pinned = new List<int>();                             // website mods held on the host's version
        }

        private static string StatePath => System.IO.Path.Combine(Pending.Dir(BepInEx.Paths.GameRootPath), "match-host.json");
        private static State _state;

        private static State Load()
        {
            if (_state != null) return _state;
            try { if (System.IO.File.Exists(StatePath)) _state = Newtonsoft.Json.JsonConvert.DeserializeObject<State>(System.IO.File.ReadAllText(StatePath)); } catch { }
            return _state ?? (_state = new State());
        }

        private static void Save()
        {
            try
            {
                var st = Load();
                if (st.Site.Count == 0 && st.Nexus.Count == 0 && st.Pinned.Count == 0) { if (System.IO.File.Exists(StatePath)) System.IO.File.Delete(StatePath); return; }
                System.IO.File.WriteAllText(StatePath, Newtonsoft.Json.JsonConvert.SerializeObject(st, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e) { ModManagerPlugin.Log?.LogWarning("Couldn't save the match-host state: " + e.Message); }
        }

        /// <summary>A website mod held on the host's version: subscriptions leave it alone until "Back to my mods".</summary>
        public static bool IsPinned(int siteId) => siteId > 0 && Load().Pinned.Contains(siteId);

        private static bool SameVersion(string a, string b) =>
            string.Equals((a ?? "").TrimStart('v', 'V'), (b ?? "").TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase) || ModVersion.TryCompare(a, b) == 0;

        /// <summary>
        /// Gets your multiplayer mods in line with the host's, on the host's exact versions: switches on ones you have
        /// but had off, switches off ones the host doesn't have, and gets the rest (website mods straight from the site,
        /// Nexus mods through the Downloader). Your own setup and versions are saved for "Back to my mods".
        /// </summary>
        public static string Run(ModCatalog cat, List<SharedMod> hostMods, Action<List<Want>> getSite, Action<List<Want>, string> getNexus, out List<string> notFound)
        {
            notFound = new List<string>();
            if (!HasProfile) Profiles.Capture(SavedProfile, cat);
            var st = Load();
            int on = 0, off = 0;
            var site = new List<Want>(); var nexus = new List<Want>();

            foreach (var h in hostMods)
            {
                var m = Find(cat, h);
                if (m != null)
                {
                    if (!Profiles.IsOn(m)) { cat.Enable(m); on++; }
                    if (h.Version.Length == 0 || SameVersion(h.Version, m.Version)) continue;
                    // Remember your version once (the first match), so going back restores what you really had.
                    if (m.SiteId > 0 && m.SiteVersionId > 0) { if (!st.Site.ContainsKey(m.SiteId)) st.Site[m.SiteId] = m.SiteVersionId; st.Names[m.SiteId] = m.Name; }
                    else if (m.NexusId > 0 && !st.Nexus.ContainsKey(m.NexusId)) st.Nexus[m.NexusId] = m.Version.TrimStart('v', 'V');
                }
                if (h.SiteId > 0 && h.SiteVersionId > 0)
                {
                    site.Add(new Want { SiteId = h.SiteId, SiteVersionId = h.SiteVersionId, Version = h.Version, Name = h.Name });
                    if (!st.Pinned.Contains(h.SiteId)) st.Pinned.Add(h.SiteId);
                }
                else if (h.Id > 0) nexus.Add(new Want { NexusId = h.Id, Version = h.Version, Name = h.Name });
                else if (m == null) notFound.Add(h.Name);
            }
            // Yours the host doesn't have: switch off (only mods that change multiplayer).
            foreach (var m in cat.Mods.Where(m => !m.IsSelf && !m.IsLoader && Profiles.IsOn(m) && m.State != ModState.Library && NetCheck.AffectsCached(m)).ToList())
                if (!hostMods.Any(h => Same(m, h))) { cat.Disable(m); off++; }
            Save();

            if (site.Count > 0) getSite(site);
            if (nexus.Count > 0) getNexus(nexus, $"{nexus.Count} mod(s) to match the host");
            var parts = new List<string>();
            if (on > 0) parts.Add($"switched on {on}");
            if (off > 0) parts.Add($"switched off {off}");
            if (site.Count > 0) parts.Add($"getting {site.Count} from the website on the host's versions");
            if (nexus.Count > 0) parts.Add($"opening {nexus.Count} from Nexus Mods on the host's versions");
            return parts.Count == 0 ? "Your multiplayer mods already match the host's." : "Matching the host: " + string.Join(", ", parts.ToArray()) + ".";
        }

        /// <summary>Your own mods again: the saved on/off setup, and your own versions of anything matching replaced.</summary>
        public static string Back(ModCatalog cat, Action<List<Want>> getSite, Action<List<Want>, string> getNexus)
        {
            var st = Load();
            var p = Profiles.Load().FirstOrDefault(x => x.Name == SavedProfile);
            if (p == null && st.Site.Count == 0 && st.Nexus.Count == 0) { st.Pinned.Clear(); Save(); return "There's no saved setup to go back to."; }
            var changed = 0; var missing = new List<ProfileMod>();
            if (p != null) { (changed, missing) = Profiles.Apply(p, cat); Profiles.Delete(SavedProfile); }
            var site = st.Site.Select(kv => new Want { SiteId = kv.Key, SiteVersionId = kv.Value, Name = st.Names.TryGetValue(kv.Key, out var n) ? n : "mod #" + kv.Key }).ToList();
            var nexus = st.Nexus.Select(kv => new Want { NexusId = kv.Key, Version = kv.Value, Name = "mod #" + kv.Key }).ToList();
            st.Site.Clear(); st.Nexus.Clear(); st.Pinned.Clear(); st.Names.Clear();
            Save();
            if (site.Count > 0) getSite(site);
            if (nexus.Count > 0) getNexus(nexus, $"your own version of {nexus.Count} mod(s)");
            var msg = changed == 0 && site.Count + nexus.Count == 0 ? "You're already on your own mods." : $"Back to your own mods ({changed} change(s))";
            if (site.Count + nexus.Count > 0) msg += $", putting back your own version of {site.Count + nexus.Count} mod(s)";
            return msg + "." + (missing.Count > 0 ? $" {missing.Count} aren't installed any more." : "");
        }

        private static ModEntry Find(ModCatalog cat, SharedMod h) => cat.Mods.FirstOrDefault(m => !m.IsSelf && Same(m, h));

        private static bool Same(ModEntry m, SharedMod h) =>
            (h.Id > 0 && m.NexusId == h.Id) || (h.SiteId > 0 && (m.SiteId == h.SiteId || m.AltSiteId == h.SiteId))
            || (h.Name.Length > 0 && ModCatalog.NameKey(m.Name) == ModCatalog.NameKey(h.Name));
    }

    // ======================================================================================================
    // Join doctor: when you get dropped, say why in plain words
    // ======================================================================================================

    internal sealed class DropReport
    {
        public DateTime When;
        public string Host = "", Title = "", Why = "";
        public float Seconds;
        public List<string> Fixes = new List<string>();
        public LobbyInfo HostInfo;
        public List<string> Log = new List<string>();
    }

    internal static class JoinDoctor
    {
        public static DropReport Last;
        private static bool _hooked, _connected, _remoteBye, _lost;
        private static float _connectedAt;
        private static int _syncErrors;
        private static readonly List<string> Lines = new List<string>();
        private static PropertyInfo _clientConnected, _serverActive;
        private static Func<bool> _isConnected, _isHost;   // (1.13.1: called directly, not through reflection every frame)
        private static LobbyInfo _hostInfo;
        private static List<LobbyMods.Player> _players = new List<LobbyMods.Player>();

        public static void Tick()
        {
            try
            {
                if (!_hooked) { Application.logMessageReceived += OnLog; _hooked = true; }
                if (_clientConnected == null)
                {
                    var mirror = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Mirror");
                    if (mirror == null) return;
                    _clientConnected = mirror.GetType("Mirror.NetworkClient")?.GetProperty("isConnected", BindingFlags.Static | BindingFlags.Public);
                    _serverActive = mirror.GetType("Mirror.NetworkServer")?.GetProperty("active", BindingFlags.Static | BindingFlags.Public);
                    if (_clientConnected == null) return;
                    _isConnected = SafeJoin.Getter(_clientConnected);
                    _isHost = _serverActive != null ? SafeJoin.Getter(_serverActive) : () => false;
                }
                var host = _isHost();
                var now = !host && _isConnected();
                if (now)
                {
                    if (LobbyMods.Host != null) _hostInfo = LobbyMods.Host;
                    if (LobbyMods.Players.Count > 0) _players = LobbyMods.Players.ToList();
                }
                if (now && !_connected)
                {
                    _connected = true; _connectedAt = Time.realtimeSinceStartup; _remoteBye = _lost = false; _syncErrors = 0;
                    Lines.Clear(); NetGuard.ForgetHeld();
                }
                else if (!now && _connected)
                {
                    _connected = false;
                    Diagnose(Time.realtimeSinceStartup - _connectedAt);
                }
            }
            catch { }
        }

        private static void OnLog(string text, string stack, LogType type)
        {
            if (!_connected || string.IsNullOrEmpty(text)) return;
            var t = text.Trim();
            var keep = false;
            if (t == "Disconnected." || t.Contains("Received internal message: DISCONNECT")) { _remoteBye = true; keep = true; }
            if (t.IndexOf("ConnectionLost", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Timeout", StringComparison.Ordinal) >= 0) { _lost = true; keep = true; }
            if ((t.Contains("OnDeserialize") || t.Contains("Deserialize")) && (t.Contains("mismatch") || t.Contains("failed") || t.Contains("Exception"))) { _syncErrors++; keep = true; }
            if (t.StartsWith("Held back ")) keep = true;
            if (type == LogType.Exception) keep = true;
            if (keep && Lines.Count < 40) Lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + (t.Length > 300 ? t.Substring(0, 300) + "…" : t));
        }

        private static void Diagnose(float secs)
        {
            var cat = ModManagerPlugin.I?.Catalog;
            var host = _players.FirstOrDefault(p => p.IsHost && !p.IsMe);
            var r = new DropReport { When = DateTime.Now, Seconds = secs, Host = host?.Name ?? "the host", HostInfo = _hostInfo, Log = Lines.ToList() };
            var mine = cat != null ? LobbyMods.Mine(cat) : new List<SharedMod>();
            var hostMods = _hostInfo?.Mods ?? host?.Mods?.Where(m => m.Net).ToList();
            var syncMods = cat?.Mods.Where(m => !m.IsSelf && Profiles.IsOn(m) && NetCheck.Why(m) == NetCheck.SyncPatch).ToList() ?? new List<ModEntry>();

            if (_syncErrors > 0)
            {
                r.Title = "The world didn't load the same on both sides";
                r.Why = "Your game and the host's read the world differently. That happens when a mod that changes how the world is synced is on one side but not the other, or on different versions.";
                if (syncMods.Count > 0) r.Fixes.Add("Mods of yours that change world syncing: " + string.Join(", ", syncMods.Select(m => m.Name).ToArray()) + ". Make sure the host has the same version, or switch them off for this game.");
            }
            else if (_remoteBye && secs < 15f)
            {
                r.Title = "The host removed you right after you joined";
                r.Why = "The host's game ended your connection within seconds. That's almost always mods: something your game sent the host's game didn't understand, or the host is set to remove players without the right mods.";
                if (NetGuard.Held.Count > 0) r.Fixes.Add("Held back so they couldn't get you kicked: " + string.Join(", ", NetGuard.Held.ToArray()) + ".");
                if (hostMods != null)
                {
                    var d = ModDiff.Of(mine.Where(m => m.Net).ToList(), hostMods);
                    if (d.TheyHaveNet.Any()) r.Fixes.Add("The host has mods you don't: " + string.Join(", ", d.TheyHaveNet.Select(m => m.Name).ToArray()) + ". Use Match host.");
                    if (d.YouHaveNet.Any()) r.Fixes.Add("You have mods the host doesn't: " + string.Join(", ", d.YouHaveNet.Select(m => m.Name).ToArray()) + ". Match host switches them off for this game.");
                }
                else r.Fixes.Add("The host doesn't have the Mods menu, so their mods can't be checked. Try switching off your mods marked with a red X, or ask the host which mods they use.");
            }
            else if (_remoteBye)
            {
                r.Title = "The host ended the game or removed you";
                r.Why = $"You were in for {Mathf.RoundToInt(secs)} seconds, then the host's game closed the connection. The host may have quit, gone back to the menu or kicked you.";
            }
            else if (_lost)
            {
                r.Title = "The connection dropped";
                r.Why = "The connection between you and the host stopped working (nothing came through for too long). This is usually the network, not mods.";
                r.Fixes.Add("Try again, pick a game with a lower ping, or ask the host to check their connection.");
            }
            else return;   // you left yourself: nothing to report

            if (syncMods.Count > 0 && _syncErrors == 0 && hostMods == null)
                r.Fixes.Add("Mods of yours that change world syncing (they must match the host): " + string.Join(", ", syncMods.Select(m => m.Name).ToArray()) + ".");
            Last = r;
            ModManagerPlugin.Log?.LogInfo("Join doctor: " + r.Title + " — " + r.Why);
            ModManagerPlugin.I?.Menu?.Toast("Dropped from the game: " + r.Title.ToLowerInvariant() + ". Open Mods > Multiplayer to see why.", false);
        }
    }

    // ======================================================================================================
    // For mod authors: ask before sending anything to the host
    // ======================================================================================================

    /// <summary>
    /// A small helper for mod authors, so a mod never gets a player kicked by talking to a host that doesn't have it.
    /// Call it through reflection so your mod still runs without the Mods menu:
    /// <code>
    /// var t = Type.GetType("NomadModManager.NomadNet, NomadModManager");
    /// var safe = t == null || (bool)t.GetMethod("SafeToSend").Invoke(null, new object[] { "your.plugin.guid" });
    /// </code>
    /// </summary>
    public static class NomadNet
    {
        /// <summary>In an online game (a lobby) right now.</summary>
        public static bool Online => LobbyMods.InLobby;

        /// <summary>You're the host of the online game.</summary>
        public static bool IsHost => LobbyMods.IsHost;

        /// <summary>The host has the Mods menu, so its mod list is known.</summary>
        public static bool HostKnown => LobbyMods.Host != null || LobbyMods.Players.Any(p => p.IsHost && !p.IsMe && p.Mods != null);

        /// <summary>The host has the mod that contains this plugin.</summary>
        public static bool HostHas(string pluginGuid)
        {
            var m = ModFor(pluginGuid);
            return m != null && NetGuard.HostHas(m);
        }

        /// <summary>
        /// Safe to send your mod's own messages to the host: you're offline, you're the host, or the host has your mod.
        /// When this is false, don't send: a host without your mod would kick the player.
        /// </summary>
        public static bool SafeToSend(string pluginGuid) => !Online || IsHost || HostHas(pluginGuid);

        /// <summary>Names of the players in the lobby who have the mod that contains this plugin.</summary>
        public static string[] PlayersWith(string pluginGuid)
        {
            var m = ModFor(pluginGuid);
            if (m == null) return new string[0];
            var key = ModCatalog.NameKey(m.Name);
            return LobbyMods.Players.Where(p => p.Mods != null && p.Mods.Any(x => (m.NexusId > 0 && x.Id == m.NexusId) || ModCatalog.NameKey(x.Name) == key))
                .Select(p => p.Name).ToArray();
        }

        private static ModEntry ModFor(string guid) =>
            ModManagerPlugin.I?.Catalog?.Mods.FirstOrDefault(m => m.Guids.Any(g => string.Equals(g, guid, StringComparison.OrdinalIgnoreCase)));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using EpicTransport;
using EvilCore.Networking;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>One mod in a mod list that's shared with other players (Nexus id, version, name).</summary>
    internal sealed class SharedMod
    {
        public int Id;
        public string Version = "", Name = "";
        public int SiteId, SiteVersionId;   // on the community website: the mod and the exact file (version) installed
        public bool Net;   // changes what's sent between players, so everyone needs the same one (see NetCheck)
        public string Key => Id > 0 ? "#" + Id : SiteId > 0 && Name.Length == 0 ? "s" + SiteId : "~" + ModCatalog.NameKey(Name);
    }

    /// <summary>
    /// A compact text form of the mods that are on, for other players: "1|51@1.1.3@Nomad Mod Manager|~@2.0@Some Mod".
    /// A "!" in front marks a mod that changes multiplayer ("!13@1.0@Name"); older menus read those by name.
    /// A 4th part "s12.345" says where it is on the website (mod 12, file 345), so others can get that exact version.
    /// Used in the lobby (each player's member attribute) and as a share code (base64 of the same).
    /// </summary>
    internal static class ModList
    {
        public static List<SharedMod> Mine(ModCatalog cat) =>
            cat.Mods.Where(m => !m.IsSelf && Profiles.IsOn(m))
                .Select(m => new SharedMod { Id = m.NexusId, Version = m.Version.TrimStart('v', 'V'), Name = m.Name, Net = NetCheck.Affects(m), SiteId = m.SiteId, SiteVersionId = m.SiteVersionId })
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Says "this list marks its multiplayer mods". Older menus skip it; lists without it are treated as all mattering.</summary>
        private const string NetMarker = "*n";

        private static string Clean(string s, int max) => new string((s ?? "").Where(c => c != '|' && c != '@').Take(max).ToArray());

        public static string Encode(List<SharedMod> mods, int maxLength = int.MaxValue)
        {
            foreach (var nameLen in new[] { 40, 20, 0 })
            {
                var s = "1|" + NetMarker + "|" + string.Join("|", mods.Select(m =>
                {
                    var site = m.SiteId > 0 ? "@s" + m.SiteId + (m.SiteVersionId > 0 ? "." + m.SiteVersionId : "") : "";
                    var name = nameLen > 0 || m.Id <= 0 || site.Length > 0 ? "@" + Clean(m.Name, Math.Max(nameLen, m.Id <= 0 ? 20 : 0)) : "";
                    return (m.Net ? "!" : "") + (m.Id > 0 ? m.Id.ToString() : "~") + "@" + Clean(m.Version, 16) + name + site;
                }).ToArray());
                if (s.Length <= maxLength || nameLen == 0) return s.Length <= maxLength ? s : s.Substring(0, maxLength);
            }
            return "1|";
        }

        public static List<SharedMod> Decode(string s)
        {
            var list = new List<SharedMod>();
            if (string.IsNullOrEmpty(s) || !s.StartsWith("1|")) return null;
            var marked = false;
            foreach (var part in s.Substring(2).Split('|'))
            {
                if (part.Length == 0) continue;
                if (part == NetMarker) { marked = true; continue; }
                var f = part.Split('@');
                var m = new SharedMod { Version = f.Length > 1 ? f[1] : "", Name = f.Length > 2 ? f[2] : "" };
                if (f.Length > 3 && f[3].StartsWith("s"))
                {
                    var sv = f[3].Substring(1).Split('.');
                    int.TryParse(sv[0], out m.SiteId);
                    if (sv.Length > 1) int.TryParse(sv[1], out m.SiteVersionId);
                }
                var id = f[0];
                if (id.StartsWith("!")) { m.Net = true; id = id.Substring(1); }
                int.TryParse(id, out m.Id);
                if (m.Id > 0 || m.Name.Length > 0 || m.SiteId > 0) list.Add(m);
            }
            // From an older Mods menu: it can't say which mods change multiplayer, so count them all.
            if (!marked) foreach (var m in list) m.Net = true;
            return list;
        }

        /// <summary>Share codes: "NMM:" + base64 of the list, so they survive chat apps.</summary>
        public static string ToCode(List<SharedMod> mods) => "NMM:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Encode(mods)));

        /// <summary>
        /// An invite link for Discord and the like: the website shows your mods (and the join code), and its button opens
        /// Nomad Mod Downloader, which installs them on your versions. c = the list (base64url), j = join code, h = name.
        /// </summary>
        public static string ToInviteLink(List<SharedMod> mods, string joinCode, string hostName)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Encode(mods, 1800))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var link = Community.Site + "invite?c=" + b64;
            if (!string.IsNullOrEmpty(joinCode)) link += "&j=" + Uri.EscapeDataString(joinCode);
            if (!string.IsNullOrEmpty(hostName)) link += "&h=" + Uri.EscapeDataString(hostName.Length > 32 ? hostName.Substring(0, 32) : hostName);
            return link;
        }

        public static List<SharedMod> FromCode(string code)
        {
            try
            {
                code = (code ?? "").Trim();
                // An invite link ("…/invite?c=…"): the list is base64url.
                var ci = code.IndexOf("invite?c=", StringComparison.OrdinalIgnoreCase);
                if (ci >= 0)
                {
                    var rb = new StringBuilder();
                    foreach (var c in code.Substring(ci + 9)) { if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) break; rb.Append(c); }
                    var raw = rb.ToString().Replace('-', '+').Replace('_', '/');
                    raw = raw.PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=');
                    return Decode(Encoding.UTF8.GetString(Convert.FromBase64String(raw)));
                }
                var i = code.IndexOf("NMM:", StringComparison.Ordinal);
                if (i < 0) return null;
                var sb = new StringBuilder();
                foreach (var c in code.Substring(i + 4))
                {
                    if (!(char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=')) break;
                    sb.Append(c);
                }
                var b64 = sb.ToString();
                return Decode(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            }
            catch { return null; }
        }
    }

    /// <summary>How two mod lists differ.</summary>
    internal sealed class ModDiff
    {
        public readonly List<SharedMod> TheyHave = new List<SharedMod>();          // they have, you don't
        public readonly List<SharedMod> YouHave = new List<SharedMod>();           // you have, they don't
        public readonly List<(SharedMod Mine, SharedMod Theirs)> Versions = new List<(SharedMod, SharedMod)>();
        public bool Same => TheyHave.Count == 0 && YouHave.Count == 0 && Versions.Count == 0;
        // Only differences in mods that change multiplayer matter for playing together; the rest are fine.
        public IEnumerable<SharedMod> TheyHaveNet => TheyHave.Where(m => m.Net);
        public IEnumerable<SharedMod> YouHaveNet => YouHave.Where(m => m.Net);
        public IEnumerable<(SharedMod Mine, SharedMod Theirs)> VersionsNet => Versions.Where(v => v.Mine.Net || v.Theirs.Net);
        public bool CanPlay => !TheyHaveNet.Any() && !YouHaveNet.Any() && !VersionsNet.Any();

        public static ModDiff Of(List<SharedMod> mine, List<SharedMod> theirs)
        {
            var d = new ModDiff();
            var a = mine.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
            var b = theirs.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
            // A mod known by id on one side and by name on the other is still the same mod.
            foreach (var t in theirs)
            {
                var m = a.TryGetValue(t.Key, out var x) ? x : mine.FirstOrDefault(y => t.Name.Length > 0 && ModCatalog.NameKey(y.Name) == ModCatalog.NameKey(t.Name));
                if (m == null) d.TheyHave.Add(t);
                else if (!string.Equals(m.Version, t.Version, StringComparison.OrdinalIgnoreCase) && m.Version.Length > 0 && t.Version.Length > 0) d.Versions.Add((m, t));
            }
            foreach (var m in mine)
            {
                var t = b.TryGetValue(m.Key, out var x) ? x : theirs.FirstOrDefault(y => m.Name.Length > 0 && ModCatalog.NameKey(y.Name) == ModCatalog.NameKey(m.Name));
                if (t == null) d.YouHave.Add(m);
            }
            return d;
        }
    }

    /// <summary>
    /// Compares mods with the other players in the same Nomad Drive lobby. Each player who has this menu puts their mod
    /// list on their own lobby membership (an EOS member attribute, like the game's display name); everyone reads the
    /// others'. Nothing is sent over the game's own connection, so players without the menu are never affected.
    /// </summary>
    internal static class LobbyMods
    {
        internal sealed class Player
        {
            public string Id = "", Name = "";
            public bool IsHost, IsMe;
            public List<SharedMod> Mods;   // null: they don't have the Mods menu (or haven't shared yet)
            public ModDiff Diff;
        }

        private const string Key = "nmm_mods";
        private const int Chunk = 900, MaxChunks = 3;

        public static readonly List<Player> Players = new List<Player>();
        public static bool InLobby { get; private set; }
        public static string LobbyId => _lobby;
        /// <summary>What the host put on the lobby itself (also visible in the lobby list before joining). Null: no Mods menu on the host.</summary>
        public static LobbyInfo Host { get; private set; }
        public static bool IsHost { get; private set; }
        public static string Error { get; private set; }
        /// <summary>The host's mods differ from yours in a way that matters for playing together.</summary>
        public static bool HostMismatch => Players.Any(p => p.IsHost && !p.IsMe && p.Diff != null && !p.Diff.CanPlay);

        private static EOSLobbyManager _mgr;
        private static float _nextFind, _nextRead, _nextShare, _nextMine, _joinedAt;
        private static string _lobby, _sharedFor, _sharedText, _shareBusy;
        private static int _sharedChunks, _shareTries;
        private static List<SharedMod> _mine = new List<SharedMod>();
        private static readonly HashSet<string> Told = new HashSet<string>();
        private static bool _disabled;

        /// <summary>My mod list, worked out at most every few seconds (not every frame).</summary>
        public static List<SharedMod> Mine(ModCatalog cat)
        {
            if (Time.realtimeSinceStartup >= _nextMine) { _mine = ModList.Mine(cat); _nextMine = Time.realtimeSinceStartup + 5f; }
            return _mine;
        }

        public static void Tick(ModCatalog cat)
        {
            if (_disabled) return;
            try
            {
                if (_mgr == null)
                {
                    if (Time.realtimeSinceStartup < _nextFind) return;
                    _nextFind = Time.realtimeSinceStartup + 3f;
                    _mgr = UnityEngine.Object.FindObjectOfType<EOSLobbyManager>();
                    if (_mgr == null) return;
                }
                var lobbyId = ((IEOSLobbyManager)_mgr).CurrentLobbyId;
                InLobby = _mgr.IsInLobby && !string.IsNullOrEmpty(lobbyId);
                if (!InLobby)
                {
                    if (_lobby != null) Left?.Invoke();
                    if (Players.Count > 0) Players.Clear(); _lobby = null; _sharedFor = null; _sharedChunks = 0; _hostChunks = 0; Host = null; Told.Clear(); return;
                }
                IsHost = _mgr.IsOwner;
                if (_lobby != lobbyId) { _lobby = lobbyId; _joinedAt = Time.realtimeSinceStartup; _shareTries = 0; _sharedChunks = 0; _nextShare = 0; }

                if (Time.realtimeSinceStartup >= _nextRead)
                {
                    _nextRead = Time.realtimeSinceStartup + 4f;
                    Read(lobbyId, Mine(cat));
                }

                // Share mine, but only once the game has finished its own lobby setup: it updates the lobby itself
                // right after creating or joining (your name, the host's address and region), and a second update
                // racing those can make EOS drop the game's one, which breaks joining.
                var text = ModList.Encode(Mine(cat), Chunk * MaxChunks);
                if (IsHost) text += "\n" + LobbyInfo.Build(Mine(cat), cat);
                if (_shareBusy != null || (_sharedFor == lobbyId && _sharedText == text)) return;
                if (Time.realtimeSinceStartup < _nextShare || !GameSetupDone(lobbyId)) return;
                if (_shareTries >= 3) return;   // EOS keeps refusing: leave the lobby alone, the share code still works
                _shareTries++;
                _nextShare = Time.realtimeSinceStartup + 30f;   // never more than once every 30 seconds
                Share(lobbyId, text);
            }
            catch (Exception e)
            {
                // A game update changed the lobby code: stop trying, the rest of the menu keeps working.
                Error = e.Message;
                _disabled = true;
                ModManagerPlugin.Log?.LogWarning("Multiplayer mod check is off: " + e.Message);
            }
        }

        /// <summary>The game's own lobby updates are in: my name is set, and the host's address and region are published.</summary>
        private static bool GameSetupDone(string lobbyId)
        {
            var since = Time.realtimeSinceStartup - _joinedAt;
            if (since < 8f) return false;
            if (since > 45f) return true;   // whatever happened, the game is done trying by now
            var lobbies = EOSSDKComponent.GetLobbyInterface();
            var me = EOSSDKComponent.LocalUserProductId;
            if (lobbies == null || me == null) return false;
            var copy = new CopyLobbyDetailsHandleOptions { LobbyId = lobbyId, LocalUserId = me };
            if (lobbies.CopyLobbyDetailsHandle(ref copy, out var details) != Result.Success || details == null) return false;
            try
            {
                if (MemberText(details, me, "display_name") == null) return false;
                if (IsHost)
                {
                    var k = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = "ping_loc" };
                    if (details.CopyAttributeByKey(ref k, out var a) != Result.Success || !a.HasValue) return false;
                }
                return true;
            }
            finally { details.Release(); }
        }

        /// <summary>
        /// The host's multiplayer mods, read from the lobby right now (not the copy from a few seconds ago).
        /// Null when the host has no Mods menu, so its mods can't be known (most often: a game without mods).
        /// </summary>
        public static List<SharedMod> HostModsNow()
        {
            try
            {
                if (_mgr == null) _mgr = UnityEngine.Object.FindObjectOfType<EOSLobbyManager>();
                var lobbyId = _mgr != null ? ((IEOSLobbyManager)_mgr).CurrentLobbyId : null;
                var lobbies = EOSSDKComponent.GetLobbyInterface();
                var me = EOSSDKComponent.LocalUserProductId;
                if (string.IsNullOrEmpty(lobbyId) || lobbies == null || me == null) return Host?.Mods;
                var copy = new CopyLobbyDetailsHandleOptions { LobbyId = lobbyId, LocalUserId = me };
                if (lobbies.CopyLobbyDetailsHandle(ref copy, out var details) != Result.Success || details == null) return Host?.Mods;
                try { return LobbyInfo.FromDetails(details, lobbyId)?.Mods; }
                finally { details.Release(); }
            }
            catch { return Host?.Mods; }
        }

        /// <summary>The game's join code for the lobby you're in ("" when there isn't one).</summary>
        public static string JoinCode
        {
            get
            {
                try { if (_mgr == null) _mgr = UnityEngine.Object.FindObjectOfType<EOSLobbyManager>(); return _mgr != null && InLobby ? ((IEOSLobbyManager)_mgr).JoinCode ?? "" : ""; }
                catch { return ""; }
            }
        }

        /// <summary>Something that goes into the shared lists changed (host choices): share again soon.</summary>
        public static void Changed() { _nextMine = 0; _nextShare = Math.Min(_nextShare, Time.realtimeSinceStartup + 3f); }

        /// <summary>Raised when you leave a lobby (back to the menu, kicked or disconnected).</summary>
        public static event Action Left;
        private static int _hostChunks;

        private static void AddLobbyAttr(LobbyModification handle, string key, string value)
        {
            var attr = new AttributeData { Key = key, Value = new AttributeDataValue { AsUtf8 = string.IsNullOrEmpty(value) ? " " : value } };
            var add = new LobbyModificationAddAttributeOptions { Attribute = attr, Visibility = LobbyAttributeVisibility.Public };
            handle.AddAttribute(ref add);
        }

        private static void Share(string lobbyId, string signature)
        {
            var nl = signature.IndexOf('\n');
            var text = nl >= 0 ? signature.Substring(0, nl) : signature;
            var hostText = nl >= 0 ? signature.Substring(nl + 1) : null;
            var lobbies = EOSSDKComponent.GetLobbyInterface();
            var me = EOSSDKComponent.LocalUserProductId;
            if (lobbies == null || me == null) return;
            var mod = new UpdateLobbyModificationOptions { LobbyId = lobbyId, LocalUserId = me };
            if (lobbies.UpdateLobbyModification(ref mod, out var handle) != Result.Success || handle == null) return;
            // Only the parts that have text, never "" (EOS refuses empty values, which made the whole update fail).
            var chunks = Math.Min(MaxChunks, Math.Max(1, (text.Length + Chunk - 1) / Chunk));
            for (var i = 0; i < chunks; i++)
            {
                var part = text.Substring(i * Chunk, Math.Min(Chunk, text.Length - i * Chunk));
                var attr = new AttributeData { Key = Key + i, Value = new AttributeDataValue { AsUtf8 = part } };
                var add = new LobbyModificationAddMemberAttributeOptions { Attribute = attr, Visibility = LobbyAttributeVisibility.Public };
                handle.AddMemberAttribute(ref add);
            }
            // This game's EOS SDK can't remove attributes: parts left over from a longer list become a single space.
            for (var i = chunks; i < _sharedChunks; i++)
            {
                var attr = new AttributeData { Key = Key + i, Value = new AttributeDataValue { AsUtf8 = " " } };
                var add = new LobbyModificationAddMemberAttributeOptions { Attribute = attr, Visibility = LobbyAttributeVisibility.Public };
                handle.AddMemberAttribute(ref add);
            }
            // The host also puts its multiplayer mods, the ones guests must have and any shared settings on the lobby
            // itself, where the lobby list can see them before anyone joins.
            if (hostText != null && IsHost)
            {
                var parts = LobbyInfo.Split(hostText);
                foreach (var kv in parts) AddLobbyAttr(handle, kv.Key, kv.Value);
            }
            var update = new UpdateLobbyOptions { LobbyModificationHandle = handle };
            _shareBusy = signature;
            lobbies.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo cb) =>
            {
                var sent = _shareBusy;
                _shareBusy = null;
                if (cb.ResultCode == Result.Success) { _sharedFor = lobbyId; _sharedText = sent; _sharedChunks = Math.Max(_sharedChunks, chunks); _shareTries = 0; }
                else ModManagerPlugin.Log?.LogInfo("Couldn't share the mod list with the lobby (" + cb.ResultCode + "); trying again in a while.");
            });
            handle.Release();
        }

        private static void Read(string lobbyId, List<SharedMod> mine)
        {
            var lobbies = EOSSDKComponent.GetLobbyInterface();
            var me = EOSSDKComponent.LocalUserProductId;
            if (lobbies == null || me == null) return;
            var copy = new CopyLobbyDetailsHandleOptions { LobbyId = lobbyId, LocalUserId = me };
            if (lobbies.CopyLobbyDetailsHandle(ref copy, out var details) != Result.Success || details == null) return;
            try
            {
                string host = null;
                var hk = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = "host_puid" };
                if (details.CopyAttributeByKey(ref hk, out var ha) == Result.Success && ha.HasValue) host = ha.Value.Data?.Value.AsUtf8;
                Host = LobbyInfo.FromDetails(details, lobbyId);
                if (Host != null && !IsHost) HostSettings.Apply(Host);
                var countOpts = new LobbyDetailsGetMemberCountOptions();
                var count = details.GetMemberCount(ref countOpts);
                var players = new List<Player>();
                var myId = me.ToString();
                for (uint i = 0; i < count; i++)
                {
                    var byIndex = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                    var puid = details.GetMemberByIndex(ref byIndex);
                    if (puid == null) continue;
                    var id = puid.ToString();
                    var p = new Player { Id = id, IsMe = id == myId, IsHost = host != null && id == host, Name = MemberText(details, puid, "display_name") ?? "Player" };
                    var sb = new StringBuilder();
                    for (var c = 0; c < MaxChunks; c++) sb.Append(MemberText(details, puid, Key + c) ?? "");
                    var all = sb.ToString().Trim();
                    p.Mods = all.Length > 0 ? ModList.Decode(all) : null;
                    if (!p.IsMe && p.Mods != null) p.Diff = ModDiff.Of(mine, p.Mods);
                    players.Add(p);
                }
                Players.Clear();
                Players.AddRange(players.OrderBy(p => p.IsHost ? 0 : p.IsMe ? 1 : 2).ThenBy(p => p.Name));
                Announce();
            }
            finally { details.Release(); }
        }

        private static string MemberText(LobbyDetails details, ProductUserId who, string key)
        {
            var o = new LobbyDetailsCopyMemberAttributeByKeyOptions { TargetUserId = who, AttrKey = key };
            if (details.CopyMemberAttributeByKey(ref o, out var a) == Result.Success && a.HasValue) return a.Value.Data?.Value.AsUtf8;
            return null;
        }

        /// <summary>
        /// Once per player per list: say on screen when their mods differ from yours in a way that breaks playing together.
        /// Mods that only change your own screen or controls (a HUD, a cheat menu) don't count.
        /// </summary>
        private static void Announce()
        {
            foreach (var p in Players.Where(p => !p.IsMe && p.Diff != null))
            {
                var d = p.Diff;
                var they = d.TheyHaveNet.Count(); var you = d.YouHaveNet.Count(); var vers = d.VersionsNet.Count();
                var key = p.Id + "|" + they + "|" + you + "|" + vers;
                if (!Told.Add(key)) continue;
                if (d.CanPlay) continue;
                var who = p.IsHost ? "The host" : p.Name;
                var parts = new List<string>();
                if (they > 0) parts.Add($"has {they} multiplayer mod(s) you don't");
                if (you > 0) parts.Add($"is missing {you} of your multiplayer mods");
                if (vers > 0) parts.Add($"has {vers} on another version");
                ModManagerPlugin.I?.Menu?.Toast($"{who} {string.Join(", ", parts.ToArray())}: joining can fail or drop you. See Mods > Multiplayer.", false);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// Safe join: play in a game whose host has no mods (or not your mods) without getting dropped.
    ///
    /// Joining with mods goes wrong in three ways, and each is handled while you're a guest:
    ///  1. Your mods send the host messages or Commands it doesn't know. NetGuard holds those back.
    ///  2. Your mods change how the world is read (patches to the game's OnSerialize / OnDeserialize / SyncVars / RPC
    ///     code), so your game reads the host's vanilla data wrong and disconnects. Those patches are taken off for
    ///     the mods the host doesn't have the moment you connect, and put back when you leave.
    ///  3. An error inside a mod while handling the host's data drops you. Mirror is told not to disconnect over that.
    /// The rest of each mod (its menus, your own screen and controls) keeps working.
    /// </summary>
    internal static class SafeJoin
    {
        private sealed class Paused
        {
            public MethodBase Original;
            public MethodInfo Patch;
            public string Owner, Mod;
            public int Kind;   // 0 prefix, 1 postfix, 2 transpiler, 3 finalizer
            public int Priority;
            public string[] Before, After;
        }

        private static readonly List<Paused> Held = new List<Paused>();
        /// <summary>Mods whose multiplayer parts are paused in the game you're in (for the Multiplayer tab).</summary>
        public static readonly List<string> PausedMods = new List<string>();
        /// <summary>The host has no Mods menu: probably a game without mods.</summary>
        public static bool HostUnknown { get; private set; }
        public static bool Engaged { get; private set; }

        private static ConfigEntry<bool> _on;
        private static PropertyInfo _clientActive, _serverActive;
        private static Func<bool> _isClient, _isServer;   // (1.13.1: called directly, not through reflection every frame)

        /// <summary>A static bool property as a plain call (falls back to reflection if it can't be bound).</summary>
        internal static Func<bool> Getter(PropertyInfo p)
        {
            try { return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), p.GetGetMethod()); }
            catch { return () => (bool)p.GetValue(null, null); }
        }
        private static bool _setup, _failed;
        private static float _nextTolerant;

        /// <summary>The game's sync methods (same list NetCheck looks for).</summary>
        private static readonly string[] SyncNames = { "OnSerialize", "OnDeserialize", "SerializeSyncVars", "DeserializeSyncVars" };
        private static bool IsSync(MethodBase m) =>
            SyncNames.Contains(m.Name) || m.Name.StartsWith("UserCode_", StringComparison.Ordinal) || m.Name.StartsWith("InvokeUserCode_", StringComparison.Ordinal);

        public static bool On
        {
            get { Bind(); return _on.Value; }
            set { Bind(); _on.Value = value; }
        }

        private static void Bind()
        {
            if (_on != null) return;
            _on = ModManagerPlugin.I.Config.Bind("Multiplayer", "SafeJoin", true,
                "When you join someone else's game: pause the multiplayer parts of mods the host doesn't have, so you don't get dropped (they come back when you leave).");
        }

        /// <summary>Every frame (cheap): engage on connecting as a guest, release on leaving.</summary>
        public static void Tick()
        {
            if (_failed) return;
            try
            {
                if (!_setup && !Setup()) return;
                var guest = _isClient() && !_isServer();
                if (guest && !Engaged && On) Engage();
                else if (!guest && Engaged) Release();
                if (Time.realtimeSinceStartup >= _nextTolerant) { _nextTolerant = Time.realtimeSinceStartup + 5f; Tolerant(); }
            }
            catch (Exception e)
            {
                _failed = true;
                ModManagerPlugin.Log?.LogWarning("Safe join is off: " + e.Message);
                try { Release(); } catch { }
            }
        }

        private static bool Setup()
        {
            var mirror = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Mirror");
            if (mirror == null) return false;
            var client = mirror.GetType("Mirror.NetworkClient");
            var server = mirror.GetType("Mirror.NetworkServer");
            _clientActive = client?.GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            _serverActive = server?.GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            if (_clientActive == null || _serverActive == null) throw new Exception("this version of the game's networking isn't recognised");
            _isClient = Getter(_clientActive); _isServer = Getter(_serverActive);
            _setup = true;
            return true;
        }

        /// <summary>Guest side: an error in a mod while reading the host's data is logged, not a reason to disconnect.</summary>
        private static void Tolerant()
        {
            var client = _clientActive.DeclaringType;
            var f = client.GetField("exceptionsDisconnect", BindingFlags.Static | BindingFlags.Public);
            if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(null)) f.SetValue(null, false);
            var p = client.GetProperty("exceptionsDisconnect", BindingFlags.Static | BindingFlags.Public);
            if (p != null && p.CanWrite && p.PropertyType == typeof(bool) && (bool)p.GetValue(null, null)) p.SetValue(null, false, null);
        }

        private static void Engage()
        {
            Engaged = true;
            Held.Clear(); PausedMods.Clear();
            if (LobbyMods.Error != null) return;   // can't tell what the host has: change nothing
            var hostMods = LobbyMods.HostModsNow();
            HostUnknown = hostMods == null;
            var cat = ModManagerPlugin.I?.Catalog;
            if (cat == null) return;

            bool HostHas(ModEntry m) => hostMods != null && hostMods.Any(h =>
                (m.NexusId > 0 && h.Id == m.NexusId) || (h.SiteId > 0 && (m.SiteId == h.SiteId || m.AltSiteId == h.SiteId))
                || (h.Name.Length > 0 && ModCatalog.NameKey(h.Name) == ModCatalog.NameKey(m.Name)));

            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                try
                {
                    if (original.DeclaringType == null || !IsSync(original) || NetGuard.IsMod(original.DeclaringType.Assembly)) continue;   // the game's own sync code only
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    var lists = new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers };
                    for (var kind = 0; kind < lists.Length; kind++)
                    {
                        foreach (var patch in lists[kind].ToList())
                        {
                            var pm = patch.PatchMethod;
                            var mod = pm?.DeclaringType != null ? NetGuard.ModFor(pm.DeclaringType.Assembly) : null;
                            if (mod == null || mod.IsSelf || mod.IsLoader || HostHas(mod)) continue;
                            new Harmony(patch.owner).Unpatch(original, pm);
                            Held.Add(new Paused { Original = original, Patch = pm, Owner = patch.owner, Mod = mod.Name, Kind = kind, Priority = patch.priority, Before = patch.before, After = patch.after });
                            if (!PausedMods.Contains(mod.Name)) PausedMods.Add(mod.Name);
                        }
                    }
                }
                catch (Exception e) { ModManagerPlugin.Log?.LogInfo($"Safe join couldn't pause a patch on {original?.Name}: {e.Message}"); }
            }

            // Mods whose messages and Commands NetGuard will hold back (they change multiplayer, the host lacks them).
            foreach (var m in cat.Mods.Where(m => !m.IsSelf && !m.IsLoader && Profiles.IsOn(m) && NetCheck.AffectsCached(m) && !HostHas(m)))
                if (!PausedMods.Contains(m.Name)) PausedMods.Add(m.Name);
            PausedMods.Sort(StringComparer.OrdinalIgnoreCase);

            if (PausedMods.Count > 0)
            {
                var who = HostUnknown ? "This host doesn't have mods (or the Mods menu)" : "The host doesn't have some of your mods";
                ModManagerPlugin.Log?.LogInfo($"Safe join: paused {Held.Count} sync patch(es); multiplayer parts paused for {string.Join(", ", PausedMods.ToArray())}.");
                Activity.Add("Safe join: paused the multiplayer parts of " + string.Join(", ", PausedMods.ToArray()));
                ModManagerPlugin.I?.Menu?.Toast($"{who}: paused the multiplayer parts of {string.Join(", ", PausedMods.ToArray())} so you don't get dropped. They come back when you leave.", true);
            }
        }

        /// <summary>Puts every paused patch back, in the order they were taken off.</summary>
        private static void Release()
        {
            Engaged = false;
            HostUnknown = false;
            foreach (var p in Held)
            {
                try
                {
                    var hm = new HarmonyMethod(p.Patch) { priority = p.Priority, before = p.Before, after = p.After };
                    var h = new Harmony(p.Owner);
                    switch (p.Kind)
                    {
                        case 0: h.Patch(p.Original, prefix: hm); break;
                        case 1: h.Patch(p.Original, postfix: hm); break;
                        case 2: h.Patch(p.Original, transpiler: hm); break;
                        default: h.Patch(p.Original, finalizer: hm); break;
                    }
                }
                catch (Exception e) { ModManagerPlugin.Log?.LogWarning($"Safe join couldn't put back {p.Mod}'s patch on {p.Original?.Name}: {e.Message}. Restart the game to be sure."); }
            }
            if (Held.Count > 0) ModManagerPlugin.Log?.LogInfo($"Safe join: put back {Held.Count} patch(es).");
            Held.Clear();
            PausedMods.Clear();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// Keeps mods from getting you kicked in multiplayer.
    ///
    /// Nomad Drive's networking (Mirror) disconnects a player the moment they send a message the host doesn't know.
    /// Some mods send their own messages as soon as you join (Sleep Anywhere says "hello" to the host, for example),
    /// so joining a host who doesn't have that mod drops you about a second after connecting.
    ///
    /// As a guest: a mod's own messages only go out once the host is known to have that mod (from the lobby mod
    /// check). Until then they're held back: that mod's multiplayer extras wait, but you stay in the game.
    /// As a host: an unknown message from a guest is ignored instead of kicking them.
    /// </summary>
    internal static class NetGuard
    {
        private static Harmony _harmony;
        private static bool _ready, _failed;
        private static Type _connToServer, _netServer, _netMessage;
        private static MethodInfo _sendGeneric;
        private static PropertyInfo _serverActive;
        private static readonly HashSet<Assembly> Seen = new HashSet<Assembly>();
        private static readonly HashSet<Type> Patched = new HashSet<Type>();
        private static readonly HashSet<string> Told = new HashSet<string>();
        /// <summary>Mods whose messages were held back in this game (for the join doctor).</summary>
        internal static readonly HashSet<string> Held = new HashSet<string>();
        internal static void ForgetHeld() { Held.Clear(); Told.Clear(); }
        private static float _nextScan;
        private static string _gameDir;

        /// <summary>Called every frame; does its work every few seconds.</summary>
        public static void Tick()
        {
            if (_failed || Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = Time.realtimeSinceStartup + 3f;
            try
            {
                if (!_ready && !Setup()) return;
                Tolerant();
                PatchNewMessageTypes();
            }
            catch (Exception e)
            {
                _failed = true;
                ModManagerPlugin.Log?.LogWarning("Multiplayer kick protection is off: " + e.Message);
            }
        }

        private static bool Setup()
        {
            var mirror = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Mirror");
            if (mirror == null) return false;
            _connToServer = mirror.GetType("Mirror.NetworkConnectionToServer");
            _netServer = mirror.GetType("Mirror.NetworkServer");
            _netMessage = mirror.GetType("Mirror.NetworkMessage");
            var conn = mirror.GetType("Mirror.NetworkConnection");
            _sendGeneric = conn?.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "Send" && m.IsGenericMethodDefinition);
            _serverActive = _netServer?.GetProperty("active", BindingFlags.Static | BindingFlags.Public);
            if (_connToServer == null || _netServer == null || _netMessage == null || _sendGeneric == null)
                throw new Exception("this version of the game's networking isn't recognised");
            _gameDir = Path.GetFullPath(Application.dataPath);
            _harmony = new Harmony(ModManagerPlugin.Guid + ".netguard");
            // A mod's own networked objects call the host with Commands: a host without the mod can't run them.
            var nb = mirror.GetType("Mirror.NetworkBehaviour");
            var sendCmd = nb?.GetMethod("SendCommandInternal", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (sendCmd != null)
            {
                try { _harmony.Patch(sendCmd, prefix: new HarmonyMethod(typeof(NetGuard), nameof(CommandPrefix))); }
                catch (Exception e) { ModManagerPlugin.Log?.LogInfo("Couldn't guard mods' Commands: " + e.Message); }
            }
            _ready = true;
            ModManagerPlugin.Log?.LogInfo("Multiplayer kick protection is on.");
            return true;
        }

        /// <summary>Host side: a guest's unknown message is ignored, not a reason to kick them.</summary>
        private static void Tolerant()
        {
            var f = _netServer.GetField("exceptionsDisconnect", BindingFlags.Static | BindingFlags.Public);
            if (f != null && (bool)f.GetValue(null)) f.SetValue(null, false);
        }

        /// <summary>Finds network messages that mods define and guards sending them to the host.</summary>
        private static void PatchNewMessageTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!Seen.Add(asm) || !IsMod(asm)) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (!t.IsValueType || t.IsGenericTypeDefinition || !_netMessage.IsAssignableFrom(t) || !Patched.Add(t)) continue;
                    try
                    {
                        _harmony.Patch(_sendGeneric.MakeGenericMethod(t), prefix: new HarmonyMethod(typeof(NetGuard), nameof(SendPrefix)));
                    }
                    catch (Exception e) { ModManagerPlugin.Log?.LogInfo($"Couldn't guard {t.FullName}: {e.Message}"); }
                }
            }
        }

        internal static bool Ready => _ready;

        internal static bool IsMod(Assembly asm)
        {
            try
            {
                if (asm.IsDynamic) return false;
                var name = asm.GetName().Name;
                if (name == "Mirror" || name.StartsWith("Mirror.", StringComparison.Ordinal) || name.StartsWith("Unity", StringComparison.Ordinal)
                    || name.StartsWith("System", StringComparison.Ordinal) || name == "mscorlib" || name.StartsWith("BepInEx", StringComparison.Ordinal)
                    || name.StartsWith("NomadModManager", StringComparison.Ordinal)) return false;
                var loc = asm.Location;
                // Loaded from memory (a mod started without a restart) or from outside the game's own folder: a mod.
                return string.IsNullOrEmpty(loc) || !Path.GetFullPath(loc).StartsWith(_gameDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Runs before a mod's message is sent. False holds it back.</summary>
        private static bool SendPrefix(object __instance, MethodBase __originalMethod)
        {
            try
            {
                // Only messages from a guest to a remote host can get you kicked.
                if (__instance == null || __instance.GetType() != _connToServer) return true;
                if (_serverActive != null && (bool)_serverActive.GetValue(null, null)) return true;
                var t = __originalMethod.GetGenericArguments()[0];
                var mod = ModFor(t.Assembly);
                if (HostHas(mod)) return true;
                var name = mod?.Name ?? t.Assembly.GetName().Name;
                Held.Add(name);
                if (Told.Add(name))
                {
                    ModManagerPlugin.Log?.LogInfo($"Held back {t.Name} from {name}: the host hasn't shown they have that mod, and sending it would get you kicked.");
                    ModManagerPlugin.I?.Menu?.Toast($"{name}'s multiplayer features are paused in this game: the host doesn't seem to have it, and its messages would get you kicked.", false);
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>Runs before a networked object sends a Command to the host. False holds it back.</summary>
        private static bool CommandPrefix(object __instance)
        {
            try
            {
                if (__instance == null || (_serverActive != null && (bool)_serverActive.GetValue(null, null))) return true;   // you're the host
                var asm = __instance.GetType().Assembly;
                if (!IsMod(asm)) return true;   // the game's own objects
                var mod = ModFor(asm);
                if (HostHas(mod)) return true;
                var name = mod?.Name ?? asm.GetName().Name;
                Held.Add(name);
                if (Told.Add(name + "|cmd"))
                    ModManagerPlugin.Log?.LogInfo($"Held back a Command from {name}: the host doesn't have that mod, so it can't run it.");
                return false;
            }
            catch { return true; }
        }

        internal static ModEntry ModFor(Assembly asm)
        {
            var cat = ModManagerPlugin.I?.Catalog;
            if (cat == null) return null;
            var name = asm.GetName().Name;
            var live = name.IndexOf("_live", StringComparison.Ordinal);
            if (live > 0) name = name.Substring(0, live);   // a mod started without a restart is renamed Name_liveN
            return cat.Mods.FirstOrDefault(m => m.Dlls.Any(d => string.Equals(Path.GetFileNameWithoutExtension(d), name, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>The host shared their mod list (they have the Mods menu) and it has this mod.</summary>
        internal static bool HostHas(ModEntry mod)
        {
            if (LobbyMods.Error != null) return true;   // the lobby check can't work: don't block anything
            if (mod == null) return false;
            var host = LobbyMods.Players.FirstOrDefault(p => p.IsHost && !p.IsMe);
            var list = host?.Mods ?? LobbyMods.Host?.Mods;
            if (list == null) return false;
            var key = ModCatalog.NameKey(mod.Name);
            return list.Any(h => (mod.NexusId > 0 && h.Id == mod.NexusId) || (h.Name.Length > 0 && ModCatalog.NameKey(h.Name) == key));
        }
    }
}

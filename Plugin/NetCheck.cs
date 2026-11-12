using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace NomadModManager
{
    /// <summary>
    /// Which mods change what goes over the network in multiplayer. A mod like that has to match on every PC
    /// (otherwise joining fails or players get dropped); a mod that only changes your own screen or controls doesn't.
    /// Found by looking inside the mod's DLLs: networked objects, network messages, or patches to the game's sync code.
    /// </summary>
    internal static class NetCheck
    {
        private static readonly Dictionary<string, (DateTime When, int Why)> Cache = new Dictionary<string, (DateTime, int)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Mirror calls that create networked objects or send the mod's own messages.</summary>
        private static readonly HashSet<string> NetCalls = new HashSet<string>(StringComparer.Ordinal)
        { "Spawn", "RegisterHandler", "ReplaceHandler", "Send", "SendToAll", "SendToReady", "SendToObservers", "RegisterPrefab", "RegisterSpawnHandler" };

        /// <summary>The game's sync methods: patching these changes what each side reads and writes.</summary>
        private static readonly string[] SyncNames = { "OnSerialize", "OnDeserialize", "SerializeSyncVars", "DeserializeSyncVars", "UserCode_", "InvokeUserCode_" };

        private static readonly Dictionary<string, (float At, bool Net)> ByMod = new Dictionary<string, (float, bool)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Same as <see cref="Affects(ModEntry)"/>, remembered for a few seconds: cheap enough to ask every frame.</summary>
        public static bool AffectsCached(ModEntry m)
        {
            if (m == null) return false;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (ByMod.TryGetValue(m.Full, out var c) && now - c.At < 5f) return c.Net;
            var net = Affects(m);
            ByMod[m.Full] = (now, net);
            return net;
        }

        /// <summary>Why a mod changes multiplayer.</summary>
        public const int None = 0, Objects = 1, Messages = 2, SyncPatch = 3;

        public static bool Affects(ModEntry m) => Why(m) != None;
        public static bool Affects(string dll) => Why(dll) != None;

        /// <summary>The strongest reason any of the mod's DLLs has (patching the game's sync code first: that's what breaks loading a world).</summary>
        public static int Why(ModEntry m)
        {
            if (m == null || m.IsSelf || m.IsLoader) return None;
            var why = None;
            foreach (var dll in m.Dlls) { var w = Why(dll); if (w == SyncPatch) return w; if (w != None) why = w; }
            return why;
        }

        public static string Explain(int why) =>
            why == SyncPatch ? "changes how the game syncs the world and objects between players"
            : why == Messages ? "sends its own messages between players"
            : why == Objects ? "adds its own networked objects" : "";

        public static int Why(string dll)
        {
            try
            {
                if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) return None;
                var when = File.GetLastWriteTimeUtc(dll);
                if (Cache.TryGetValue(dll, out var c) && c.When == when) return c.Why;
                var why = Scan(dll);
                Cache[dll] = (when, why);
                return why;
            }
            catch { return None; }
        }

        private static int Scan(string dll)
        {
            // Read from memory so the file is never held open.
            using (var ms = new MemoryStream(File.ReadAllBytes(dll)))
            using (var asm = AssemblyDefinition.ReadAssembly(ms, TypeLoader.ReaderParameters))
            {
                var module = asm.MainModule;
                // 1.10.1: a mod can say its networking is optional and safe with players who don't have it (it only
                // talks to copies of itself, and never makes the game drop anyone): then it doesn't have to match.
                //   [assembly: AssemblyMetadata("NomadNetOptional", "true")]
                if (asm.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                    && a.ConstructorArguments.Count == 2 && a.ConstructorArguments[0].Value as string == "NomadNetOptional"
                    && string.Equals(a.ConstructorArguments[1].Value as string, "true", StringComparison.OrdinalIgnoreCase)))
                    return None;
                // Without Mirror or Harmony a mod can't touch what's sent between players.
                if (!module.AssemblyReferences.Any(r => r.Name == "Mirror" || r.Name.StartsWith("Mirror.", StringComparison.Ordinal) || r.Name == "0Harmony"))
                    return None;
                var why = None;
                foreach (var t in All(module.Types))
                {
                    if (t.BaseType != null && (t.BaseType.FullName == "Mirror.NetworkBehaviour")) why = Math.Max(why, Objects);
                    if (t.BaseType?.FullName == "Mirror.NetworkMessage" || t.Interfaces.Any(i => i.InterfaceType.FullName == "Mirror.NetworkMessage")) why = Math.Max(why, Messages);
                    if (t.CustomAttributes.Any(IsSyncPatch)) return SyncPatch;
                    foreach (var meth in t.Methods)
                    {
                        if (meth.CustomAttributes.Any(IsSyncPatch)) return SyncPatch;
                        if (!meth.HasBody) continue;
                        foreach (var ins in meth.Body.Instructions)
                        {
                            if (ins.Operand is MethodReference mr && mr.DeclaringType?.Namespace == "Mirror"
                                && (mr.DeclaringType.Name == "NetworkServer" || mr.DeclaringType.Name == "NetworkClient" || mr.DeclaringType.Name.StartsWith("NetworkConnection", StringComparison.Ordinal))
                                && NetCalls.Contains(mr.Name)) why = Math.Max(why, mr.Name == "Spawn" || mr.Name == "RegisterPrefab" || mr.Name == "RegisterSpawnHandler" ? Objects : Messages);
                            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string str && IsSyncName(str)) return SyncPatch;
                        }
                    }
                }
                return why;
            }
        }

        private static bool IsSyncPatch(CustomAttribute a)
        {
            if (!a.AttributeType.Name.StartsWith("HarmonyPatch", StringComparison.Ordinal)) return false;
            try { return a.ConstructorArguments.Any(x => x.Value is string s && IsSyncName(s)); }
            catch { return false; }
        }

        private static bool IsSyncName(string s) => SyncNames.Any(n => s == n || (n.EndsWith("_", StringComparison.Ordinal) && s.StartsWith(n, StringComparison.Ordinal)));

        private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
        {
            foreach (var t in types)
            {
                yield return t;
                foreach (var n in All(t.NestedTypes)) yield return n;
            }
        }
    }
}

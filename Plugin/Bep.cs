using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using Mono.Cecil;
using UnityEngine;
using BepInEx.Bootstrap;

namespace NomadModManager
{
    /// <summary>The few places BepInEx 5 and 6 differ, so the rest of the menu is the same code for both.</summary>
    internal static class Bep
    {
        public const int Major = 5;
        public static Dictionary<string, PluginInfo> Plugins => Chainloader.PluginInfos;
        public static GameObject Manager => Chainloader.ManagerObject;
        public static PluginInfo ToPluginInfo(TypeDefinition type, string dll) => Chainloader.ToPluginInfo(type);
        public static bool VersionOk(BepInDependency d, PluginInfo have) => d.MinimumVersion == null || have.Metadata.Version >= d.MinimumVersion;

        /// <summary>A plugin started by the other BepInEx (through BepInEx 5 or 6 Compatibility).</summary>
        public sealed class Other
        {
            public string Guid = "", Name = "", Version = "", Location = "";
            public bool Running;
        }

        private static List<Other> _others = new List<Other>();
        private static float _othersAt = -100f;

        /// <summary>
        /// Plugins the other BepInEx's chainloader started. BepInEx 5 Compatibility runs BepInEx 5's own chainloader
        /// beside BepInEx 6 (and BepInEx 6 Compatibility the other way round), so those mods aren't in <see cref="Plugins"/>.
        /// Read by reflection: this menu is built against one BepInEx only.
        /// </summary>
        public static List<Other> OtherPlugins
        {
            get
            {
                if (Time.realtimeSinceStartup - _othersAt < 2f) return _others;
                _othersAt = Time.realtimeSinceStartup;
                var result = new List<Other>();
                try
                {
                    IDictionary dict = null;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string n;
                        try { n = a.GetName().Name; } catch { continue; }
                        if (n != "BepInEx.Unity.Mono") continue; // BepInEx 6's runtime
                        var t = a.GetType("BepInEx.Unity.Mono.Bootstrap.UnityChainloader");
                        var inst = t?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
                        dict = inst?.GetType().GetProperty("Plugins", BindingFlags.Instance | BindingFlags.Public)?.GetValue(inst, null) as IDictionary;
                        if (dict != null) break;
                    }
                    if (dict != null)
                        foreach (var info in dict.Values)
                        {
                            if (info == null) continue;
                            var it = info.GetType();
                            var meta = it.GetProperty("Metadata")?.GetValue(info, null);
                            if (meta == null) continue;
                            var mt = meta.GetType();
                            result.Add(new Other
                            {
                                Guid = mt.GetProperty("GUID")?.GetValue(meta, null) as string ?? "",
                                Name = mt.GetProperty("Name")?.GetValue(meta, null) as string ?? "",
                                Version = mt.GetProperty("Version")?.GetValue(meta, null)?.ToString() ?? "",
                                Location = it.GetProperty("Location")?.GetValue(info, null) as string ?? "",
                                Running = it.GetProperty("Instance")?.GetValue(info, null) != null,
                            });
                        }
                }
                catch { }
                return _others = result;
            }
        }

        /// <summary>The other BepInEx's chainloader (BepInEx 5 under BepInEx 5 Compatibility): its plugin list and manager object.</summary>
        public static IDictionary OtherDict(out GameObject manager)
        {
            // Here (BepInEx 5) the other one is BepInEx 6, run beside it by BepInEx 6 Compatibility: its UnityChainloader.
            manager = null;
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n;
                    try { n = a.GetName().Name; } catch { continue; }
                    if (n != "BepInEx.Unity.Mono") continue;
                    var t = a.GetType("BepInEx.Unity.Mono.Bootstrap.UnityChainloader");
                    if (t == null) continue;
                    var inst = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
                    var dict = inst?.GetType().GetProperty("Plugins", BindingFlags.Instance | BindingFlags.Public)?.GetValue(inst, null) as IDictionary;
                    if (dict == null) continue;
                    const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var mo = t.GetProperty("ManagerObject", any) ?? inst.GetType().GetProperty("ManagerObject", any);
                    manager = mo?.GetValue(mo.GetGetMethod(true)?.IsStatic == true ? null : inst, null) as GameObject;
                    return dict;
                }
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Reading BepInEx 6's plugin list: " + e.Message); }
            return null;
        }

        /// <summary>The BepInEx 5 runtime assembly, when BepInEx 5 Compatibility is running it.</summary>
        public static Assembly OtherRuntime()
        {
            return null;
        }

        public static void ForgetOthers() => _othersAt = -100f;

        /// <summary>Is a plugin with this GUID running, in either BepInEx?</summary>
        public static bool IsRunning(string guid) =>
            (Plugins.TryGetValue(guid, out var p) && p.Instance != null) || OtherPlugins.Any(o => o.Running && string.Equals(o.Guid, guid, StringComparison.OrdinalIgnoreCase));
    }
}

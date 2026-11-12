using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;
using UnityEngine;

namespace NomadModManager
{
    internal sealed class HotVerdict
    {
        public string Dll;
        public bool CanLoad;
        public bool NotAPlugin;       // a support library: nothing to start, it loads when something needs it
        public bool WrongGame;        // BepInProcess filter excludes Nomad Drive
        public string Reason = "";    // why it needs a restart
        public bool Fresh;            // an older copy of the same DLL was loaded earlier: start it under a new name
        public readonly List<PluginInfo> Plugins = new List<PluginInfo>();
    }

    /// <summary>
    /// Loads a plugin DLL that appeared after BepInEx's startup, the same way the chainloader does,
    /// when it's safe to; otherwise says why a restart is needed.
    /// </summary>
    internal static class HotLoader
    {
        /// <summary>GUID prefixes of mods known to start cleanly when loaded late.</summary>
        private static readonly string[] Trusted = { "nomaddrive.", "akorit." };

        /// <summary>Methods that have already run by the time you're at the main menu; patching them late does nothing.</summary>
        private static readonly HashSet<string> StartupMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Awake", "Start", "OnEnable", "Configure", "Install", "InstallBindings", "Initialize", "Init", "Construct", "Bootstrap" };

        private static bool _resolverHooked;

        public static HotVerdict Check(string dll)
        {
            var v = new HotVerdict { Dll = dll };
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(dll, TypeLoader.ReaderParameters); }
            catch (Exception e) { v.Reason = "Couldn't read the DLL (" + e.GetType().Name + ")"; return v; }

            using (asm)
            {
                var refs = asm.MainModule.AssemblyReferences.Select(r => r.Name).ToList();
                if (!refs.Contains("BepInEx"))
                {
                    v.NotAPlugin = true;
                    v.Reason = "Support library";
                    return v;
                }
                if (refs.Contains("BepInEx.Preloader") || refs.Contains("BepInEx.Preloader.Core") || HasPatcherEntry(asm))
                {
                    v.Reason = "Preloader patcher";
                    return v;
                }

                foreach (var t in asm.MainModule.Types)
                {
                    PluginInfo info = null;
                    try { info = Bep.ToPluginInfo(t, dll); } catch { }
                    if (info == null) continue;
                    SetProp(info, "Location", dll);
                    v.Plugins.Add(info);
                }
                if (v.Plugins.Count == 0) { v.NotAPlugin = true; v.Reason = "Support library"; return v; }

                // An older copy of this DLL loaded earlier (and since stopped, or replaced): the new one starts under a new name.
                var name = asm.Name.Name;
                v.Fresh = AppDomain.CurrentDomain.GetAssemblies().Any(a => SafeName(a) == name);

                var batch = new HashSet<string>(v.Plugins.Select(p => p.Metadata.GUID));
                foreach (var p in v.Plugins)
                {
                    var procs = p.Processes.ToList();
                    if (procs.Count > 0 && procs.All(x => !string.Equals(x.ProcessName.Replace(".exe", ""), Paths.ProcessName, StringComparison.OrdinalIgnoreCase)))
                    {
                        v.WrongGame = true; v.Reason = "Made for a different game"; return v;
                    }
                    if (Bep.Plugins.ContainsKey(p.Metadata.GUID))
                    {
                        v.Reason = "Replaces a mod that's already running"; return v;
                    }
                    foreach (var d in p.Dependencies.Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))
                    {
                        if (batch.Contains(d.DependencyGUID)) continue;
                        if (!Bep.Plugins.TryGetValue(d.DependencyGUID, out var have) || have.Instance == null)
                        { v.Reason = "Needs " + d.DependencyGUID + ", which isn't loaded"; return v; }
                        if (!Bep.VersionOk(d, have))
                        { v.Reason = "Needs a newer " + have.Metadata.Name; return v; }
                    }
                    foreach (var inc in p.Incompatibilities)
                        if (Bep.Plugins.ContainsKey(inc.IncompatibilityGUID))
                        { v.Reason = "Conflicts with " + inc.IncompatibilityGUID; return v; }
                }

                // The mod can say for itself: [assembly: AssemblyMetadata("NomadHotLoad", "true" | "false")]
                var opt = asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                                                                   && a.ConstructorArguments.Count == 2 && (a.ConstructorArguments[0].Value as string) == "NomadHotLoad");
                var optValue = opt?.ConstructorArguments[1].Value as string;
                if (string.Equals(optValue, "false", StringComparison.OrdinalIgnoreCase)) { v.Reason = "The mod asks for a restart"; return v; }
                var trusted = string.Equals(optValue, "true", StringComparison.OrdinalIgnoreCase)
                              || v.Plugins.All(p => Trusted.Any(t => p.Metadata.GUID.StartsWith(t, StringComparison.OrdinalIgnoreCase)));

                if (!trusted)
                {
                    var startup = FindStartupPatch(asm);
                    if (startup != null) { v.Reason = "Changes game startup code (" + startup + ")"; return v; }
                }

                v.CanLoad = true;
                return v;
            }
        }

        /// <summary>Loads the DLL and starts its plugins on BepInEx's manager object, like the chainloader would have.</summary>
        public static List<string> Load(HotVerdict v)
        {
            HookResolver();
            var started = new List<string>();
            var asm = Assembly.LoadFrom(v.Dll);
            var host = Bep.Manager;
            if (host == null)
            {
                host = new GameObject("NomadModManager_HotLoaded");
                UnityEngine.Object.DontDestroyOnLoad(host);
            }

            foreach (var info in Order(v.Plugins))
            {
                var type = asm.GetType(GetProp<string>(info, "TypeName"), true);
                Bep.Plugins[info.Metadata.GUID] = info;
                try
                {
                    var instance = (BaseUnityPlugin)host.AddComponent(type);
                    SetProp(info, "Instance", instance);
                    started.Add(info.Metadata.Name + " " + info.Metadata.Version);
                }
                catch
                {
                    Bep.Plugins.Remove(info.Metadata.GUID);
                    throw;
                }
            }
            return started;
        }

        // ------------------------------------------------------------------
        // Live on/off and updates, for mods that say they support it:
        // [assembly: AssemblyMetadata("NomadHotLoad", "true")] means the mod sets itself up from scratch when it starts
        // and cleans up (Harmony patches, objects it made) in OnDestroy, so it can be stopped and started any time.

        /// <summary>What a DLL is: a plugin or not, and whether it opted in to being started and stopped live.</summary>
        public static (bool Plugin, bool Live) Inspect(string dll)
        {
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(dll)))
                using (var asm = AssemblyDefinition.ReadAssembly(ms, TypeLoader.ReaderParameters))
                {
                    var plugin = AllTypes(asm.MainModule.Types).Any(t => !t.IsAbstract && t.CustomAttributes.Any(a => a.AttributeType.Name == "BepInPlugin"));
                    return (plugin, string.Equals(OptIn(asm), "true", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { return (false, false); }
        }

        private static string OptIn(AssemblyDefinition asm) =>
            asm.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                                                     && a.ConstructorArguments.Count == 2 && (a.ConstructorArguments[0].Value as string) == "NomadHotLoad")
               ?.ConstructorArguments[1].Value as string;

        /// <summary>
        /// Why a mod's DLL can't be swapped while the game runs (null = it can). Mods that opted in always can;
        /// others can at the main menu unless they need something that only happens at launch.
        /// </summary>
        public static string RefreshBlocker(string dll)
        {
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(dll)))
                using (var asm = AssemblyDefinition.ReadAssembly(ms, TypeLoader.ReaderParameters))
                {
                    var refs = asm.MainModule.AssemblyReferences.Select(r => r.Name).ToList();
                    var opt = OptIn(asm);
                    if (string.Equals(opt, "false", StringComparison.OrdinalIgnoreCase)) return "The mod asks for a restart";
                    if (refs.Contains("BepInEx.Preloader") || refs.Contains("BepInEx.Preloader.Core") || HasPatcherEntry(asm)) return "Preloader patcher";
                    if (!string.Equals(opt, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        var startup = FindStartupPatch(asm);
                        if (startup != null) return "Changes game startup code (" + startup + ")";
                    }
                    return null;
                }
            }
            catch (Exception e) { return "Couldn't read the DLL (" + e.GetType().Name + ")"; }
        }

        /// <summary>
        /// Stops running plugins right away and clears what they left behind, so a new copy can start in the same frame:
        /// their OnDestroy runs, then any Harmony patches, components and scene/camera event handlers from their DLL are removed.
        /// </summary>
        public static List<string> Stop(IEnumerable<string> guids)
        {
            var stopped = new List<string>();
            var asms = new HashSet<Assembly>();
            foreach (var g in guids.ToList())
            {
                if (!Bep.Plugins.TryGetValue(g, out var info) || info == null) continue;
                if (info.Instance is Component c && c != null)
                {
                    asms.Add(c.GetType().Assembly);
                    try { UnityEngine.Object.DestroyImmediate(c); }
                    catch (Exception e) { ModManagerPlugin.Log.LogWarning("Stopping " + info.Metadata.Name + ": " + e.Message); }
                }
                SetProp(info, "Instance", null);
                Bep.Plugins.Remove(g);
                stopped.Add(info.Metadata.Name);
            }
            // ...and the ones BepInEx 5 Compatibility runs.
            var other = Bep.OtherDict(out _);
            if (other != null)
                foreach (var g in guids.ToList())
                {
                    if (!other.Contains(g)) continue;
                    var info = other[g];
                    var inst = info?.GetType().GetProperty("Instance")?.GetValue(info, null) as Component;
                    var name = g;
                    try { name = (info.GetType().GetProperty("Metadata")?.GetValue(info, null))?.GetType().GetProperty("Name")?.GetValue(info.GetType().GetProperty("Metadata").GetValue(info, null), null) as string ?? g; } catch { }
                    if (inst != null)
                    {
                        asms.Add(inst.GetType().Assembly);
                        try { UnityEngine.Object.DestroyImmediate(inst); }
                        catch (Exception e) { ModManagerPlugin.Log.LogWarning("Stopping " + name + ": " + e.Message); }
                    }
                    try { SetProp(info, "Instance", null); } catch { }
                    other.Remove(g);
                    stopped.Add(name);
                }
            Bep.ForgetOthers();
            foreach (var a in asms) CleanUp(a);
            return stopped;
        }

        /// <summary>Removes what a stopped mod's DLL still has hooked into the game.</summary>
        private static void CleanUp(Assembly asm)
        {
            bool Mine(MethodBase m) => m != null && m.DeclaringType != null && m.DeclaringType.Assembly == asm;

            // Harmony patches (whoever made them)
            var unpatched = 0;
            try
            {
                foreach (var original in HarmonyLib.Harmony.GetAllPatchedMethods().ToList())
                {
                    var pi = HarmonyLib.Harmony.GetPatchInfo(original);
                    if (pi == null) continue;
                    foreach (var p in pi.Prefixes.Concat(pi.Postfixes).Concat(pi.Transpilers).Concat(pi.Finalizers).ToList())
                    {
                        if (!Mine(p.PatchMethod)) continue;
                        try { new HarmonyLib.Harmony(p.owner).Unpatch(original, p.PatchMethod); unpatched++; } catch { }
                    }
                }
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Unpatching: " + e.Message); }

            // Components it added to objects in the scene (not prefabs/assets), and objects that only held those.
            var removed = 0;
            try
            {
                foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (mb == null || mb.GetType().Assembly != asm) continue;
                    var go = mb.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    try { UnityEngine.Object.DestroyImmediate(mb); removed++; } catch { continue; }
                    try
                    {
                        if (go != null && go.transform.parent == null && go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1 && go != Bep.Manager)
                            UnityEngine.Object.DestroyImmediate(go);
                    }
                    catch { }
                }
            }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Removing leftovers: " + e.Message); }

            // Static Unity events it subscribed to.
            var unhooked = 0;
            foreach (var t in new[] { typeof(UnityEngine.SceneManagement.SceneManager), typeof(Application), typeof(Camera), typeof(Canvas) })
            {
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    try
                    {
                        var d = f.GetValue(null) as Delegate;
                        if (d == null) continue;
                        var keep = d.GetInvocationList().Where(x => !Mine(x.Method)).ToArray();
                        if (keep.Length == d.GetInvocationList().Length) continue;
                        unhooked += d.GetInvocationList().Length - keep.Length;
                        f.SetValue(null, keep.Length == 0 ? null : Delegate.Combine(keep));
                    }
                    catch { }
                }
            }
            if (unpatched + removed + unhooked > 0)
                ModManagerPlugin.Log.LogMessage($"Cleaned up {asm.GetName().Name}: {unpatched} patch(es), {removed} component(s), {unhooked} event handler(s).");
        }

        private static int _generation;

        /// <summary>
        /// Starts fresh copies of plugin DLLs, even when older copies were loaded earlier this session.
        /// Each DLL is loaded from memory under a new assembly name (Mono can't load the same assembly twice,
        /// and a DLL loaded from memory isn't locked, so later updates can replace the file). DLLs in the same batch
        /// that reference each other are pointed at each other's new copies.
        /// Each item: Dll = where the mod lives, From = where the bytes come from (a staged update, or the DLL itself).
        /// </summary>
        public static List<string> LoadFresh(IEnumerable<(string Dll, string From)> items)
        {
            HookResolver();
            var list = items.ToList();
            return list.Count == 0 ? new List<string>() : LoadFreshPlugins(list);
        }

        /// <summary>Loads DLLs from memory under new assembly names, pointing ones in the same batch at each other.</summary>
        private static List<(string Dll, Type[] Types)> Rewrite(List<(string Dll, string From)> items)
        {
            var defs = new List<(string Dll, AssemblyDefinition Asm, MemoryStream Src)>();
            try
            {
                foreach (var it in items)
                {
                    var src = new MemoryStream(File.ReadAllBytes(it.From ?? it.Dll));
                    defs.Add((it.Dll, AssemblyDefinition.ReadAssembly(src, TypeLoader.ReaderParameters), src));
                }
                var renames = new Dictionary<string, string>();
                foreach (var d in defs) renames[d.Asm.Name.Name] = StripLive(d.Asm.Name.Name) + "_live" + (++_generation);
                var result = new List<(string, Type[])>();
                foreach (var d in defs)
                {
                    d.Asm.Name.Name = renames[d.Asm.Name.Name];
                    d.Asm.MainModule.Name = d.Asm.Name.Name + ".dll";
                    foreach (var r in d.Asm.MainModule.AssemblyReferences)
                    {
                        var hit = renames.FirstOrDefault(kv => StripLive(kv.Key) == StripLive(r.Name));
                        if (hit.Value != null) r.Name = hit.Value;
                    }
                    byte[] bytes;
                    using (var ms = new MemoryStream()) { d.Asm.Write(ms); bytes = ms.ToArray(); }
                    var loaded = Assembly.Load(bytes);
                    Type[] types;
                    try { types = loaded.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    result.Add((d.Dll, types));
                }
                return result;
            }
            finally
            {
                foreach (var d in defs) { try { d.Asm.Dispose(); d.Src.Dispose(); } catch { } }
            }
        }

        private static List<string> LoadFreshPlugins(List<(string Dll, string From)> list)
        {
            var defs = new List<(string Dll, AssemblyDefinition Asm, MemoryStream Src, List<PluginInfo> Infos)>();
            try
            {
                foreach (var it in list)
                {
                    var src = new MemoryStream(File.ReadAllBytes(it.From ?? it.Dll));
                    var asm = AssemblyDefinition.ReadAssembly(src, TypeLoader.ReaderParameters);
                    var infos = new List<PluginInfo>();
                    foreach (var t in asm.MainModule.Types)
                    {
                        PluginInfo info = null;
                        try { info = Bep.ToPluginInfo(t, it.Dll); } catch { }
                        if (info == null) continue;
                        SetProp(info, "Location", it.Dll);
                        infos.Add(info);
                    }
                    defs.Add((it.Dll, asm, src, infos));
                }
                var all = defs.SelectMany(d => d.Infos).ToList();
                if (all.Count == 0) throw new InvalidOperationException("There's no plugin in it");
                var batch = new HashSet<string>(all.Select(p => p.Metadata.GUID));
                foreach (var p in all)
                {
                    if (Bep.Plugins.TryGetValue(p.Metadata.GUID, out var running) && running?.Instance != null)
                        throw new InvalidOperationException(p.Metadata.Name + " is still running");
                    foreach (var d in p.Dependencies.Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))
                    {
                        if (batch.Contains(d.DependencyGUID)) continue;
                        if (!Bep.Plugins.TryGetValue(d.DependencyGUID, out var have) || have.Instance == null)
                            throw new InvalidOperationException("Needs " + d.DependencyGUID + ", which isn't running");
                    }
                }

                var renames = new Dictionary<string, string>();
                foreach (var d in defs) renames[d.Asm.Name.Name] = StripLive(d.Asm.Name.Name) + "_live" + (++_generation);
                var bytes = new List<byte[]>();
                foreach (var d in defs)
                {
                    d.Asm.Name.Name = renames[d.Asm.Name.Name];
                    d.Asm.MainModule.Name = d.Asm.Name.Name + ".dll";
                    foreach (var r in d.Asm.MainModule.AssemblyReferences)
                    {
                        var hit = renames.FirstOrDefault(kv => StripLive(kv.Key) == StripLive(r.Name));
                        if (hit.Value != null) r.Name = hit.Value;
                    }
                    using (var ms = new MemoryStream()) { d.Asm.Write(ms); bytes.Add(ms.ToArray()); }
                }

                var loaded = bytes.Select(Assembly.Load).ToList();
                var host = Bep.Manager;
                if (host == null)
                {
                    host = new GameObject("NomadModManager_HotLoaded");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                }
                var typeOf = new Dictionary<PluginInfo, Type>();
                for (int i = 0; i < defs.Count; i++)
                    foreach (var info in defs[i].Infos) typeOf[info] = loaded[i].GetType(GetProp<string>(info, "TypeName"), true);

                var started = new List<string>();
                foreach (var info in Order(all))
                {
                    Bep.Plugins[info.Metadata.GUID] = info;
                    try
                    {
                        var instance = (BaseUnityPlugin)host.AddComponent(typeOf[info]);
                        SetProp(info, "Instance", instance);
                        started.Add(info.Metadata.Name + " " + info.Metadata.Version);
                    }
                    catch
                    {
                        Bep.Plugins.Remove(info.Metadata.GUID);
                        throw;
                    }
                }
                return started;
            }
            finally
            {
                foreach (var d in defs) { try { d.Asm.Dispose(); d.Src.Dispose(); } catch { } }
            }
        }

        private static string StripLive(string name)
        {
            var i = name.LastIndexOf("_live", StringComparison.Ordinal);
            return i > 0 && name.Substring(i + 5).All(char.IsDigit) && name.Length > i + 5 ? name.Substring(0, i) : name;
        }

        // ------------------------------------------------------------------

        private static IEnumerable<PluginInfo> Order(List<PluginInfo> plugins)
        {
            var left = plugins.ToList();
            var done = new HashSet<string>();
            while (left.Count > 0)
            {
                var next = left.FirstOrDefault(p => p.Dependencies.All(d => done.Contains(d.DependencyGUID) || !left.Any(o => o.Metadata.GUID == d.DependencyGUID)))
                           ?? left[0];
                left.Remove(next);
                done.Add(next.Metadata.GUID);
                yield return next;
            }
        }

        private static bool HasPatcherEntry(AssemblyDefinition asm) =>
            asm.MainModule.Types.Any(t => t.Properties.Any(p => p.Name == "TargetDLLs" && p.GetMethod != null && p.GetMethod.IsStatic)
                                       && t.Methods.Any(m => m.Name == "Patch" && m.IsStatic));

        /// <summary>Finds a [HarmonyPatch(typeof(X), "Awake")]-style patch on a startup method.</summary>
        private static string FindStartupPatch(AssemblyDefinition asm)
        {
            foreach (var t in AllTypes(asm.MainModule.Types))
            {
                foreach (var attrs in new[] { t.CustomAttributes }.Concat(t.Methods.Select(m => m.CustomAttributes)))
                {
                    foreach (var a in attrs)
                    {
                        if (!a.AttributeType.Name.StartsWith("HarmonyPatch")) continue;
                        TypeReference target = null;
                        string method = null;
                        foreach (var arg in a.ConstructorArguments)
                        {
                            if (arg.Value is TypeReference tr) target = tr;
                            else if (arg.Value is string s) method = s;
                        }
                        if (method != null && StartupMethods.Contains(method))
                            return (target != null ? target.Name + "." : "") + method;
                    }
                }
            }
            return null;
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var t in types)
            {
                yield return t;
                foreach (var n in AllTypes(t.NestedTypes)) yield return n;
            }
        }

        /// <summary>Lets a late-loaded mod find DLLs that sit next to it in BepInEx\plugins.</summary>
        private static void HookResolver()
        {
            if (_resolverHooked) return;
            _resolverHooked = true;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var name = new AssemblyName(e.Name).Name;
                    var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => SafeName(a) == name);
                    if (loaded != null) return loaded;
                    var file = Directory.GetFiles(Paths.PluginPath, name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                    return file != null ? Assembly.LoadFrom(file) : null;
                }
                catch { return null; }
            };
        }

        private static string SafeName(Assembly a)
        {
            try { return a.GetName().Name; } catch { return ""; }
        }

        internal static T GetProp<T>(object target, string name)
        {
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return (T)p?.GetGetMethod(true)?.Invoke(target, null);
        }

        internal static void SetProp(object target, string name, object value)
        {
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p?.GetSetMethod(true)?.Invoke(target, new[] { value });
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace NomadModManager
{
    /// <summary>
    /// The Performance page's measuring (1.17.0): how long each mod takes every frame.
    ///
    /// While it runs, each mod's own per-frame code is timed: its Update, LateUpdate, FixedUpdate and OnGUI, and every
    /// Harmony prefix, postfix and finalizer it put on the game's code. The times add up per mod, per frame; at the end
    /// of each frame they are rolled into averages, worst frames and a list of stutters with the mods in them.
    ///
    /// It costs nothing while it's off: the timing patches are only put on when the player presses Start, a few a frame
    /// so starting doesn't freeze the game, and taken off again on Stop (or by itself after a while). Transpilers and
    /// coroutines can't be timed this way, and a mod's time includes game code it calls.
    /// </summary>
    internal static class PerfMonitor
    {
        internal sealed class Bucket
        {
            public string Guid = "", Name = "";
            public int Methods;
            // this frame
            public long FrameTicks; public int FrameCalls; public int Depth;
            // this window (averages are worked out every couple of seconds)
            public long WinTicks, WinCalls;
            // since Start
            public long TotalTicks, TotalCalls, PeakTicks; public int Stutters;
            // shown
            public double AvgMs, CallsPerFrame, Share, PeakMs;
        }

        internal sealed class Stutter
        {
            public DateTime When;
            public double FrameMs;
            public List<(string Name, double Ms)> Mods = new List<(string, double)>();
        }

        public const string HarmonyId = "akorit.nomadmodmanager.performance";
        private const float WindowSeconds = 2f;
        public const int StutterMs = 50;
        private const int MaxStutters = 40, PatchPerFrame = 6;

        public static bool Running { get; private set; }
        public static bool Preparing { get; private set; }
        public static bool Stopping { get; private set; }
        public static int Done, Total, Skipped;
        public static DateTime StartedAt;
        public static double Fps, FrameMs, ModsMs, ModsShare, CleanupsPerMin;
        public static long Frames;
        public static readonly List<Bucket> Buckets = new List<Bucket>();
        public static readonly List<Stutter> Stutters = new List<Stutter>();
        public static string Note = "";

        private static readonly Dictionary<MethodBase, Bucket> Map = new Dictionary<MethodBase, Bucket>();
        private static readonly List<MethodBase> Patched = new List<MethodBase>();
        private static Harmony _h;
        private static int _mainThread;
        private static volatile bool _live;
        private static long _lastEnd, _winFrameTicks, _winFrames;
        private static float _winStart;
        private static int _gcAtWindow;
        private static float _gcClock;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>The most minutes it keeps measuring before it stops by itself (0 = until Stop).</summary>
        public static int MaxMinutes = 15;

        public static bool Busy => Running || Preparing || Stopping;
        public static TimeSpan Elapsed => Running ? DateTime.Now - StartedAt : TimeSpan.Zero;

        // ------------------------------------------------------------------ start and stop

        public static void Start(MonoBehaviour host)
        {
            if (Busy) return;
            host.StartCoroutine(StartRoutine(host));
        }

        public static void Stop(MonoBehaviour host)
        {
            if (!Running && !Preparing) return;
            host.StartCoroutine(StopRoutine());
        }

        /// <summary>Clears what was measured so far, keeping the timing on.</summary>
        public static void Reset()
        {
            foreach (var b in Buckets)
            {
                b.WinTicks = b.WinCalls = b.TotalTicks = b.TotalCalls = b.PeakTicks = 0; b.Stutters = 0;
                b.AvgMs = b.CallsPerFrame = b.Share = b.PeakMs = 0;
            }
            Stutters.Clear();
            Frames = 0; _winFrames = 0; _winFrameTicks = 0; _winStart = Time.realtimeSinceStartup;
            StartedAt = DateTime.Now;
        }

        private static IEnumerator StartRoutine(MonoBehaviour host)
        {
            Preparing = true;
            Note = "";
            yield return null;   // (pressed inside the menu's OnGUI: patch from the next frame, never code that is running right now)
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            Map.Clear(); Patched.Clear(); Buckets.Clear(); Stutters.Clear();
            Done = Skipped = 0;
            var work = new List<(MethodBase Method, Bucket Bucket)>();
            try { work = FindWork(); }
            catch (Exception e) { ModManagerPlugin.Log.LogWarning("Performance: couldn't list the mods' code: " + e.Message); }
            Total = work.Count;
            if (Total == 0)
            {
                Preparing = false;
                Note = "No mod code to time was found.";
                yield break;
            }
            _h = new Harmony(HarmonyId);
            var pre = new HarmonyMethod(typeof(PerfMonitor).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            var fin = new HarmonyMethod(typeof(PerfMonitor).GetMethod(nameof(Fin), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
            // A few a frame: putting a patch on takes about a millisecond each.
            var n = 0;
            foreach (var (method, bucket) in work)
            {
                if (!Preparing) break;   // Stop pressed while getting ready
                try
                {
                    Map[method] = bucket;
                    _h.Patch(method, prefix: pre, finalizer: fin);
                    Patched.Add(method);
                    bucket.Methods++;
                }
                catch (Exception e)
                {
                    Map.Remove(method);
                    Skipped++;
                    ModManagerPlugin.Log.LogDebug($"Performance: can't time {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                }
                Done++;
                if (++n % PatchPerFrame == 0) yield return null;
            }
            if (!Preparing) yield break;
            Buckets.RemoveAll(b => b.Methods == 0);
            Preparing = false;
            Running = true;
            Reset();
            _gcAtWindow = SafeGcCount(); _gcClock = Time.realtimeSinceStartup;
            _lastEnd = Stopwatch.GetTimestamp();
            _live = true;
            ModManagerPlugin.Log.LogInfo($"Performance: timing {Patched.Count} methods in {Buckets.Count} mods" + (Skipped > 0 ? $" ({Skipped} couldn't be timed)." : "."));
            Activity.Add($"Started measuring mod performance ({Buckets.Count} mods)");
            host.StartCoroutine(FrameLoop());
        }

        private static IEnumerator StopRoutine()
        {
            var wasRunning = Running;
            _live = false;
            Running = false;
            Preparing = false;
            Stopping = true;
            var n = 0;
            foreach (var m in Patched.ToList())
            {
                try { _h?.Unpatch(m, HarmonyPatchType.All, HarmonyId); } catch { }
                if (++n % (PatchPerFrame * 2) == 0) yield return null;
            }
            Patched.Clear();
            Map.Clear();
            Stopping = false;
            if (wasRunning) Activity.Add("Stopped measuring mod performance");
        }

        /// <summary>Every mod's per-frame methods and Harmony patch methods, with the mod they belong to.</summary>
        private static List<(MethodBase, Bucket)> FindWork()
        {
            var byAsm = new Dictionary<Assembly, Bucket>();
            foreach (var info in Bep.Plugins.Values)
            {
                if (info?.Instance == null) continue;
                var asm = info.Instance.GetType().Assembly;
                if (asm == typeof(PerfMonitor).Assembly && info.Metadata.GUID != ModManagerPlugin.Guid) continue;
                if (byAsm.ContainsKey(asm)) continue;
                var b = new Bucket { Guid = info.Metadata.GUID, Name = info.Metadata.Name };
                byAsm[asm] = b;
                Buckets.Add(b);
            }
            var work = new List<(MethodBase, Bucket)>();
            var seen = new HashSet<MethodBase>();
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            string[] messages = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
            foreach (var kv in byAsm)
            {
                Type[] types;
                try { types = kv.Key.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    try
                    {
                        if (t.IsGenericTypeDefinition || t.IsAbstract && t.IsSealed || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                        foreach (var name in messages)
                        {
                            var m = t.GetMethod(name, inst, null, Type.EmptyTypes, null);
                            if (m == null || m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                            if (seen.Add(m)) work.Add((m, kv.Value));
                        }
                    }
                    catch { }
                }
            }
            // Harmony patches the mods put on the game: the patch methods themselves are timed.
            foreach (var target in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(target); } catch { continue; }
                if (info == null) continue;
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    if (p.owner == HarmonyId) continue;
                    var pm = p.PatchMethod;
                    if (pm == null || pm.IsGenericMethodDefinition || pm.DeclaringType == null) continue;
                    if (!byAsm.TryGetValue(pm.DeclaringType.Assembly, out var bucket)) continue;
                    try { if (pm.GetMethodBody() == null) continue; } catch { continue; }
                    if (seen.Add(pm)) work.Add((pm, bucket));
                }
            }
            return work;
        }

        // ------------------------------------------------------------------ timing (runs inside the mods' code)

        private static void Pre(MethodBase __originalMethod, out long __state)
        {
            __state = 0;
            if (!_live || Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            if (!Map.TryGetValue(__originalMethod, out var b)) return;
            if (b.Depth++ > 0) { __state = -1; return; }   // already inside this mod: counted once
            __state = Stopwatch.GetTimestamp();
        }

        private static void Fin(MethodBase __originalMethod, long __state)
        {
            if (__state == 0) return;
            if (!Map.TryGetValue(__originalMethod, out var b)) return;
            if (b.Depth > 0) b.Depth--;
            if (__state < 0) return;
            b.FrameTicks += Stopwatch.GetTimestamp() - __state;
            b.FrameCalls++;
        }

        // ------------------------------------------------------------------ adding up, once a frame

        private static IEnumerator FrameLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (Running)
            {
                yield return eof;
                if (!Running) yield break;
                try { EndFrame(); } catch (Exception e) { ModManagerPlugin.Log.LogWarning("Performance: " + e.Message); }
                if (MaxMinutes > 0 && Elapsed.TotalMinutes >= MaxMinutes)
                {
                    Note = $"Stopped by itself after {MaxMinutes} minutes (Settings › Performance › MostMinutes).";
                    ModManagerPlugin.I.Menu?.Toast("Mod performance: measuring stopped by itself after " + MaxMinutes + " minutes. The results stay on the Performance page.", true);
                    Stop(ModManagerPlugin.I);
                }
            }
        }

        private static void EndFrame()
        {
            var now = Stopwatch.GetTimestamp();
            var frame = now - _lastEnd;
            _lastEnd = now;
            Frames++;
            _winFrames++;
            _winFrameTicks += frame;
            var frameMs = frame * TickMs;
            var stutter = frameMs >= StutterMs && Frames > 3;
            Stutter s = stutter ? new Stutter { When = DateTime.Now, FrameMs = frameMs } : null;
            foreach (var b in Buckets)
            {
                var t = b.FrameTicks;
                if (t > 0)
                {
                    b.WinTicks += t; b.TotalTicks += t;
                    if (t > b.PeakTicks) { b.PeakTicks = t; b.PeakMs = t * TickMs; }
                    if (s != null && t * TickMs >= 1) { s.Mods.Add((b.Name, t * TickMs)); b.Stutters++; }
                }
                b.WinCalls += b.FrameCalls; b.TotalCalls += b.FrameCalls;
                b.FrameTicks = 0; b.FrameCalls = 0; b.Depth = 0;
            }
            if (s != null)
            {
                s.Mods.Sort((a, c) => c.Ms.CompareTo(a.Ms));
                Stutters.Insert(0, s);
                if (Stutters.Count > MaxStutters) Stutters.RemoveAt(Stutters.Count - 1);
            }
            if (Time.realtimeSinceStartup - _winStart < WindowSeconds) return;

            // A window is over: work out what the page shows.
            double frames = Math.Max(1, _winFrames), mods = 0;
            foreach (var b in Buckets)
            {
                b.AvgMs = b.WinTicks * TickMs / frames;
                b.CallsPerFrame = b.WinCalls / frames;
                b.Share = _winFrameTicks > 0 ? (double)b.WinTicks / _winFrameTicks : 0;
                mods += b.WinTicks;
                b.WinTicks = b.WinCalls = 0;
            }
            FrameMs = _winFrameTicks * TickMs / frames;
            Fps = FrameMs > 0 ? 1000.0 / FrameMs : 0;
            ModsMs = mods * TickMs / frames;
            ModsShare = _winFrameTicks > 0 ? mods / _winFrameTicks : 0;
            _winFrames = 0; _winFrameTicks = 0; _winStart = Time.realtimeSinceStartup;
            var gc = SafeGcCount();
            var mins = (Time.realtimeSinceStartup - _gcClock) / 60f;
            if (mins >= 0.25f) { CleanupsPerMin = (gc - _gcAtWindow) / mins; _gcAtWindow = gc; _gcClock = Time.realtimeSinceStartup; }
        }

        private static int SafeGcCount() { try { return GC.CollectionCount(0); } catch { return 0; } }

        // ------------------------------------------------------------------ the report

        /// <summary>Plain text of what was measured, for a mod's author or Discord.</summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Nomad Mod Manager {ModManagerPlugin.Version} - mod performance, measured for {Elapsed:mm\\:ss} ({Frames} frames)");
            sb.AppendLine($"{Fps:0} fps, {FrameMs:0.0} ms a frame, mods {ModsMs:0.00} ms ({ModsShare:P0}), memory cleanups {CleanupsPerMin:0.#} a minute");
            sb.AppendLine();
            sb.AppendLine("Mod                               ms/frame   share   worst ms   calls/frame   in stutters");
            foreach (var b in Buckets.OrderByDescending(x => x.AvgMs))
                sb.AppendLine($"{Clip(b.Name, 32),-32}  {b.AvgMs,8:0.000}  {b.Share,6:P1}  {b.PeakMs,9:0.0}  {b.CallsPerFrame,12:0.#}  {b.Stutters,12}");
            if (Stutters.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Stutters (frames over {StutterMs} ms), newest first:");
                foreach (var s in Stutters)
                    sb.AppendLine($"  {s.When:HH:mm:ss}  {s.FrameMs,6:0} ms  " + (s.Mods.Count == 0 ? "no mod took over 1 ms (the game itself)" : string.Join(", ", s.Mods.Take(3).Select(m => $"{m.Name} {m.Ms:0.0} ms").ToArray())));
            }
            return sb.ToString();
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}

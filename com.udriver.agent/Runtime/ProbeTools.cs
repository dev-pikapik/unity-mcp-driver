using System;
using System.Collections;
using System.Globalization;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

namespace UDriver
{
    public static class ProbeTools
    {
        sealed class Probe
        {
            public int Id; public string Kind, Desc, Owner;
            public Func<string> Stats; public Action Remove;
            public List<string> Hits;
        }

        sealed class Watch { public int Id; public string Path, Label, Last; public int Changes; }

        /// <summary>Settings and hit log shared by all methods of one trace probe.</summary>
        sealed class TraceProbe
        {
            public int Hits, Max; public bool Args, Stack, Pause;
            public string[] Capture; public JObject When;
            public readonly List<string> Log = new List<string>();
        }

        /// <summary>A path read from a traced call: root is "this", a parameter name or argN, then fields/indexes.</summary>
        sealed class Expr { public string Text; public int Root; public List<string> Toks; }

        sealed class TraceInfo { public TraceProbe P; public int Count; public Expr[] Capture; public Expr When; }

        static readonly List<Probe> Probes = new List<Probe>();
        static readonly List<Watch> Watches = new List<Watch>();
        static readonly ConcurrentDictionary<MethodBase, TraceInfo> Traced = new ConcurrentDictionary<MethodBase, TraceInfo>();
        static int _nextId = 1;

        public static int Count { get { return Probes.Count; } }

        internal static void Register()
        {
            Driver.Register("watch_add", Driver.Sync(WatchAdd));
#if !ENABLE_IL2CPP
            Driver.Register("trace_add", Driver.Sync(TraceAdd));
#endif
            Driver.Register("trigger_add", Driver.Sync(TriggerAdd));
            Driver.Register("probe_list", Driver.Sync(List));
            Driver.Register("probe_remove", Driver.Sync(RemoveTool));
            Driver.Register("profiler_get", Driver.Sync(ProfilerGet));
            Driver.Register("profiler_list", Driver.Sync(ProfilerList));
            Driver.Register("profiler_capture", ProfilerCapture);
            InitProfiler();
        }

        // ---------------- registry ----------------

        static Probe Add(string kind, string desc, Func<string> stats, Action remove)
        {
            var p = new Probe { Id = _nextId++, Kind = kind, Desc = desc, Stats = stats, Remove = remove, Owner = Driver.Current != null ? Driver.Current.Client : "" };
            Probes.Add(p);
            return p;
        }

        public static bool RemoveById(int id)
        {
            var p = Probes.Find(x => x.Id == id);
            if (p == null) return false;
            Probes.Remove(p);
            try { p.Remove?.Invoke(); } catch (Exception e) { Debug.LogWarning("[UDriver] removing probe " + id + ": " + e.Message); }
            return true;
        }

        public static void RemoveAll()
        {
            foreach (var p in Probes.ToArray()) RemoveById(p.Id);
        }

        static void List(JObject a, ToolResponse r)
        {
            if (a.Has("id"))
            {
                var p = Probes.Find(x => x.Id == a.Int("id"));
                if (p == null) throw new ToolException("no probe with id " + a.Int("id") + " - probe_list shows the active ones");
                r.Text(Line(p));
                if (p.Hits == null) return;
                int offset = Math.Max(0, a.Int("offset", 0)), limit = Math.Max(1, a.Int("limit", 20)), total, end;
                lock (p.Hits)
                {
                    total = p.Hits.Count; end = Math.Min(total, offset + limit);
                    for (int i = offset; i < end; i++) r.Text(p.Hits[i]);
                }
                r.Text(total == 0 ? "no hits recorded yet" : "hits shown " + Math.Min(offset + 1, end) + "-" + end + " of " + total + (end < total ? "; next page: offset=" + end : ""));
                return;
            }
            if (Probes.Count == 0) { r.Text("no active probes"); return; }
            foreach (var p in Probes) r.Text(Line(p));
        }

        static string Me { get { return Driver.Current != null ? Driver.Current.Client : ""; } }

        static string Line(Probe p)
        {
            return "probe " + p.Id + " [" + p.Kind + "] " + p.Desc + " | " + p.Stats() + (p.Owner != Me ? " | set by " + p.Owner : "");
        }

        static void RemoveTool(JObject a, ToolResponse r)
        {
            bool force = a.Bool("force");
            if (a.Bool("all"))
            {
                int n = 0, kept = 0;
                foreach (var p in Probes.ToArray())
                    if (force || p.Owner == Me) { RemoveById(p.Id); n++; } else kept++;
                r.Text("removed " + n + " probe(s)" + (kept > 0 ? "; kept " + kept + " set by other agents (force=true removes them too)" : ""));
                return;
            }
            if (!a.Has("id")) throw new ToolException("give id or all=true");
            var q = Probes.Find(x => x.Id == a.Int("id"));
            if (q == null) { r.Text("no probe with id " + a.Int("id")); return; }
            if (q.Owner != Me && !force) throw new ToolException("probe " + q.Id + " was set by " + q.Owner + "; pass force=true to remove it anyway");
            RemoveById(q.Id);
            r.Text("removed probe " + q.Id + (q.Owner != Me ? " (set by " + q.Owner + ")" : ""));
        }

        // ---------------- watches (polled every frame) ----------------

        static string Eval(string path)
        {
            try { return Util.Cut(StateTools.Format(StateTools.Resolve(path), 1, 10, false), 400); }
            catch (Exception e) { return "<error: " + (e is ToolException ? e.Message : e.GetType().Name + ": " + e.Message) + ">"; }
        }

        static void WatchAdd(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            if (path == null) throw new ToolException("path is required");
            var w = new Watch { Path = path, Label = a.Str("label", path), Last = Eval(path) };
            var p = Add("watch", path, () => w.Changes + " change(s), current: " + Util.OneLine(w.Last, 120), () => Watches.Remove(w));
            w.Id = p.Id;
            Watches.Add(w);
            r.Text("probe " + p.Id + ": watching " + path + " every frame; changes are emitted as 'watch' events. Current value: " + w.Last);
        }

        /// <summary>Called from LateUpdate.</summary>
        internal static void Tick()
        {
            FrameTimingManager.CaptureFrameTimings();
            if (_pauseRequested) { _pauseRequested = false; PauseGame(); }
            for (int i = 0; i < Watches.Count; i++)
            {
                var w = Watches[i];
                string now = Eval(w.Path);
                if (now == w.Last) continue;
                w.Changes++;
                EventStore.Emit("watch", "watch " + w.Id + " " + w.Label + ": " + Util.OneLine(w.Last, 150) + " -> " + Util.OneLine(now, 150), now);
                w.Last = now;
            }
        }

        // ---------------- triggers ----------------

        static void TriggerAdd(JObject a, ToolResponse r)
        {
            string action = a.Str("action");
            if (action != "pause" && action != "capture_frame" && action != "mark") throw new ToolException("action must be pause, capture_frame or mark");
            if (!a.Has("event") && !a.Has("contains")) throw new ToolException("give event and/or contains");
            var t = new Trigger { Cat = a.Str("event"), Contains = a.Str("contains"), Action = action, StatePath = a.Str("state"), Once = a.Bool("once", true) };
            var p = Add("trigger", "on [" + (t.Cat ?? "*") + "]" + (t.Contains != null ? " containing '" + t.Contains + "'" : "") + " -> " + action,
                () => t.Fired + " hit(s)",
                () => { var l = new List<Trigger>(EventStore.Triggers); l.Remove(t); EventStore.Triggers = l.ToArray(); });
            t.Id = p.Id;
            var list = new List<Trigger>(EventStore.Triggers) { t };
            EventStore.Triggers = list.ToArray();
            r.Text("probe " + p.Id + ": trigger armed (" + p.Desc + (t.Once ? ", once" : ", repeating") + "). It emits a 'trigger' event when it fires.");
        }

        // ---------------- method tracing through Harmony (looked up by reflection, so it is optional) ----------------

        static Type _hType, _hMethodType;
        static object _harmony;
        static MethodInfo _patch, _unpatch, _prefix, _postfix, _finalizer;
        internal static Type FindHarmony()
        {
            if (_hType != null) return _hType;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("HarmonyLib.Harmony", false);
                if (t == null) continue;
                _hType = t; _hMethodType = asm.GetType("HarmonyLib.HarmonyMethod", false);
                break;
            }
            return _hType;
        }

        public static bool HarmonyAvailable { get { return FindHarmony() != null; } }

        static void EnsureHarmony()
        {
            if (_harmony != null) return;
            if (FindHarmony() == null || _hMethodType == null)
                throw new ToolException("method tracing needs Harmony: put 0Harmony.dll (Lib.Harmony 2.4 or newer, net48 build; 2.3 cannot patch on Apple Silicon) into Assets/Plugins and rebuild. Works on Mono only, not IL2CPP. "
                                        + "Without it use watch_add (field polling), state_get, and log/ui events.");
            _harmony = Activator.CreateInstance(_hType, "udriver.trace");
            foreach (var m in _hType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var ps = m.GetParameters();
                if (m.Name == "Patch" && ps.Length >= 3 && ps[0].ParameterType == typeof(MethodBase)) _patch = m;
                if (m.Name == "Unpatch" && ps.Length == 2 && ps[1].ParameterType == typeof(MethodInfo)) _unpatch = m;
            }
            const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Static;
            _prefix = typeof(ProbeTools).GetMethod("TracePrefix", f);
            _postfix = typeof(ProbeTools).GetMethod("TraceResult", f);
            _finalizer = typeof(ProbeTools).GetMethod("TraceFinalizer", f);
            if (_patch == null || _unpatch == null) throw new ToolException("unsupported Harmony version (Patch/Unpatch not found)");
        }

        internal static object HMethod(MethodInfo m)
        {
            return _hMethodType.GetConstructor(new[] { typeof(MethodInfo) }).Invoke(new object[] { m });
        }

        // Harmony injects parameters by name: __originalMethod, __args, __instance, __result, __exception, __state.
        static void TracePrefix(MethodBase __originalMethod, object[] __args, object __instance, out bool __state)
        {
            __state = false;
            TraceInfo ti;
            if (__originalMethod == null || !Traced.TryGetValue(__originalMethod, out ti)) return;
            Interlocked.Increment(ref ti.Count);
            var tp = ti.P;
            if (ti.When != null)
            {
                bool pass;
                try { pass = StateTools.Test(Read(ti.When, __args, __instance), tp.When.Str("op", "=="), tp.When["value"]); }
                catch { pass = false; }
                if (!pass) return;
            }
            int hit = Interlocked.Increment(ref tp.Hits);
            if (hit > tp.Max) return;
            __state = true;
            var sb = new StringBuilder("call ").Append(__originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Name : "?").Append('.').Append(__originalMethod.Name).Append('(');
            if (tp.Args && __args != null)
                for (int i = 0; i < __args.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(StateTools.Short(__args[i])); }
            sb.Append(')');
            if (ti.Capture != null)
                foreach (var c in ti.Capture)
                {
                    sb.Append(" | ").Append(c.Text).Append('=');
                    try { var v = Read(c, __args, __instance); sb.Append(v == StateTools.Missing ? "<none>" : StateTools.Short(v)); }
                    catch (Exception e) { sb.Append("<error: ").Append((e.InnerException ?? e).Message).Append('>'); }
                }
            string text = sb.ToString(), stack = tp.Stack ? new System.Diagnostics.StackTrace(2, true).ToString() : null;   // skips this prefix and the patched copy of the method
            lock (tp.Log) tp.Log.Add("hit " + hit + " f" + Time.frameCount + " " + text + (stack != null ? "\n    " + stack.Trim().Replace("\n", "\n    ") : ""));
            EventStore.Emit("trace", text, stack);
            if (tp.Pause) { if (Driver.OnMainThread) PauseGame(); else _pauseRequested = true; }
        }

        static volatile bool _pauseRequested;

        /// <summary>Editor: a real editor pause (resume with editor_play action=resume). Build: timeScale=0 (resume with time_control action=resume).</summary>
        static void PauseGame()
        {
            if (Application.isEditor) Debug.Break();
            else CaptureTools.Pause();
        }

        static object Read(Expr e, object[] args, object instance)
        {
            if (e.Root == -2) return StateTools.Missing;
            object root = e.Root == -1 ? instance : args != null && e.Root < args.Length ? args[e.Root] : null;
            return StateTools.Pick(root, e.Toks);
        }

        static Expr Compile(string text, MethodBase m)
        {
            var toks = StateTools.Tokens(text);
            if (toks.Count == 0) throw new ToolException("empty capture/when path");
            string head = toks[0];
            toks.RemoveAt(0);
            int root = -2;
            if (head == "this") root = m.IsStatic ? -2 : -1;
            else
            {
                var ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++) if (ps[i].Name == head || head == "arg" + i) root = i;
            }
            return new Expr { Text = text, Root = root, Toks = toks };
        }

        static void TraceResult(MethodBase __originalMethod, object __result, bool __state)
        {
            if (!__state || __originalMethod == null) return;
            EventStore.Emit("trace", "return " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " -> " + StateTools.Short(__result));
        }

        static void TraceFinalizer(MethodBase __originalMethod, Exception __exception)
        {
            if (__exception == null || __originalMethod == null) return;
            EventStore.Emit("trace", "EXCEPTION in " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + ": " + __exception.GetType().Name + ": " + __exception.Message, __exception.StackTrace);
        }

        static void TraceAdd(JObject a, ToolResponse r)
        {
            string pattern = a.Str("pattern");
            if (pattern == null) throw new ToolException("pattern is required, e.g. Game.Orders.OrderService.*");
            EnsureHarmony();
            var match = Util.Matcher(pattern);
            bool wild = pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
            var methods = new List<MethodBase>();
            foreach (var t in StateTools.GameTypes(false))
            {
                if (t.IsInterface || t.IsGenericTypeDefinition || t.Name.IndexOf('<') >= 0) continue;
                string tn = t.FullName ?? t.Name;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName || m.Name.IndexOf('<') >= 0 || m.GetMethodBody() == null) continue;
                    string full = tn + "." + m.Name;
                    if (wild ? match(full) : string.Equals(full, pattern, StringComparison.OrdinalIgnoreCase) || full.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase)) methods.Add(m);
                }
            }
            if (methods.Count == 0) throw new ToolException("no methods match '" + pattern + "'. Pattern is matched against Namespace.Type.Method - check types_find / type_members.");
            if (methods.Count > 300) throw new ToolException(methods.Count + " methods match - too broad (limit 300). Narrow the pattern.");
            Patch(pattern, methods, a, r);
        }

        /// <summary>Traces the given methods and returns the probe id; the self-test calls it directly because its methods are not game code.</summary>
        internal static int Patch(string pattern, List<MethodBase> methods, JObject a, ToolResponse r)
        {
            EnsureHarmony();
            bool result = a.Bool("result");
            var tp = new TraceProbe { Args = a.Bool("args", true), Stack = a.Bool("stack"), Pause = a.Bool("pause"), Max = Math.Max(1, a.Int("maxHits", a.Int("max", 2000))),
                                      Capture = a.Strs("capture"), When = a["when"] as JObject };
            if (a["when"] != null && (tp.When == null || tp.When.Str("field") == null)) throw new ToolException("when is one condition: {\"field\":\"amount\",\"op\":\">\",\"value\":100} - field starts with a parameter name, argN or this");
            if (tp.When != null) StateTools.CheckOp(tp.When.Str("op", "=="));
            var patched = new List<MethodBase>();
            var failed = new List<string>();
            foreach (var m in methods)
            {
                if (Traced.ContainsKey(m)) continue;
                var ti = new TraceInfo { P = tp, When = tp.When != null ? Compile(tp.When.Str("field"), m) : null };
                if (tp.Capture != null) { ti.Capture = new Expr[tp.Capture.Length]; for (int i = 0; i < ti.Capture.Length; i++) ti.Capture[i] = Compile(tp.Capture[i], m); }
                Traced[m] = ti;
                try
                {
                    var argv = new object[_patch.GetParameters().Length];
                    argv[0] = m; argv[1] = HMethod(_prefix);
                    if (result && ((MethodInfo)m).ReturnType != typeof(void)) argv[2] = HMethod(_postfix);
                    if (argv.Length > 4) argv[4] = HMethod(_finalizer);
                    _patch.Invoke(_harmony, argv);
                    patched.Add(m);
                }
                catch (Exception e)
                {
                    TraceInfo dummy; Traced.TryRemove(m, out dummy);
                    failed.Add(m.DeclaringType.Name + "." + m.Name + " (" + (e.InnerException ?? e).Message + ")");
                }
            }
            if (patched.Count == 0) throw new ToolException("could not patch any method. First error: " + (failed.Count > 0 ? failed[0] : "already traced by another probe"));

            var p = Add("trace", pattern + " (" + patched.Count + " method(s))" + (tp.When != null ? " when " + tp.When.Str("field") + " " + tp.When.Str("op", "==") + " " + tp.When["value"] : "") + (tp.Pause ? ", pauses" : ""),
                () =>
                {
                    var sb = new StringBuilder(); int total = 0, shown = 0;
                    foreach (var m in patched) { TraceInfo ti; if (!Traced.TryGetValue(m, out ti)) continue; total += ti.Count; if (ti.Count > 0 && shown++ < 12) sb.Append(' ').Append(m.DeclaringType.Name).Append('.').Append(m.Name).Append(" x").Append(ti.Count).Append(';'); }
                    return total + " call(s) total, " + Math.Min(tp.Hits, tp.Max) + " hit(s) recorded" + (tp.Hits > tp.Max ? " (maxHits " + tp.Max + " reached, later hits dropped)" : "") + ";"
                           + (total == 0 ? " no traced method has been called yet" : sb.ToString());
                },
                () =>
                {
                    foreach (var m in patched)
                    {
                        TraceInfo dummy; Traced.TryRemove(m, out dummy);
                        foreach (var patch in new[] { _prefix, _postfix, _finalizer })
                            try { _unpatch.Invoke(_harmony, new object[] { m, patch }); } catch { }
                    }
                });
            p.Hits = tp.Log;
            r.Text("probe " + p.Id + ": tracing " + patched.Count + " method(s) matching '" + pattern + "'. Hits appear as 'trace' events; read them page by page with probe_list id=" + p.Id + " offset=0."
                   + (tp.Pause ? " Each hit pauses the game: " + (Application.isEditor ? "resume with editor_play action=resume." : "resume with time_control action=resume.") : ""));
            if (tp.Capture != null || tp.When != null)
                foreach (var m in patched)
                {
                    TraceInfo ti; if (!Traced.TryGetValue(m, out ti)) continue;
                    var bad = new List<string>();
                    if (ti.When != null && ti.When.Root == -2) bad.Add(ti.When.Text);
                    if (ti.Capture != null) foreach (var c in ti.Capture) if (c.Root == -2) bad.Add(c.Text);
                    if (bad.Count > 0) r.Text("  note: " + m.DeclaringType.Name + "." + m.Name + " has no 'this' or parameter for: " + string.Join(", ", bad) + " (parameters: " + string.Join(", ", Array.ConvertAll(m.GetParameters(), x => x.Name)) + ")");
                }
            for (int i = 0; i < patched.Count && i < 25; i++) r.Text("  " + patched[i].DeclaringType.Name + "." + patched[i].Name);
            if (patched.Count > 25) r.Text("  ... and " + (patched.Count - 25) + " more");
            if (failed.Count > 0) r.Text(failed.Count + " method(s) could not be patched, e.g. " + failed[0]);
            return p.Id;
        }

        // ---------------- profiler ----------------

        sealed class Counter { public string Label; public ProfilerRecorder Rec; public string Unit; }

        static readonly List<Counter> Counters = new List<Counter>();

        static void AddCounter(string category, string name, string unit)
        {
            foreach (var c in Counters) if (c.Label == category + "/" + name) return;
            try { Counters.Add(new Counter { Label = category + "/" + name, Rec = new ProfilerRecorder(category, name, 30), Unit = unit }); }
            catch (Exception e) { Debug.LogWarning("[UDriver] profiler counter " + category + "/" + name + ": " + e.Message); }
        }

        static void InitProfiler()
        {
            if (Counters.Count > 0) return;
            AddCounter("Internal", "Main Thread", "ns");
            AddCounter("Memory", "Total Used Memory", "bytes");
            AddCounter("Memory", "Total Reserved Memory", "bytes");
            AddCounter("Memory", "GC Used Memory", "bytes");
            AddCounter("Memory", "GC Reserved Memory", "bytes");
            AddCounter("Memory", "System Used Memory", "bytes");
            AddCounter("Memory", "GC Allocated In Frame", "bytes");
            AddCounter("Render", "Draw Calls Count", "");
            AddCounter("Render", "Batches Count", "");
            AddCounter("Render", "SetPass Calls Count", "");
            AddCounter("Render", "Triangles Count", "");
            AddCounter("Render", "Vertices Count", "");
            foreach (var c in Counters) c.Rec.Start();
        }

        static string Val(double v, string unit)
        {
            if (unit == "ns") return (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " ms";
            if (unit == "bytes") return v >= 1048576 ? (v / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (v / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static void ProfilerGet(JObject a, ToolResponse r)
        {
            var add = a.Strs("add");
            if (add != null)
                foreach (var s in add)
                {
                    int i = s.IndexOf('/');
                    if (i <= 0) throw new ToolException("counters must be given as \"Category/Name\" (see profiler_list)");
                    int before = Counters.Count;
                    AddCounter(s.Substring(0, i), s.Substring(i + 1), "");
                    if (Counters.Count > before) Counters[Counters.Count - 1].Rec.Start();
                }
            r.Text("fps (last frame): " + (Time.unscaledDeltaTime > 0 ? (1f / Time.unscaledDeltaTime).ToString("0.0", CultureInfo.InvariantCulture) : "?") + " | frame " + Time.frameCount
                   + " | managed heap: " + Val(GC.GetTotalMemory(false), "bytes") + " | GC gen0 collections: " + GC.CollectionCount(0)
                   + " | unity allocated: " + Val(Profiler.GetTotalAllocatedMemoryLong(), "bytes") + " | mono used: " + Val(Profiler.GetMonoUsedSizeLong(), "bytes"));
            if (FrameTimingManager.GetLatestTimings((uint)Timings.Length, Timings) is var ft && ft > 0)
            {
                double cpu = 0, main = 0, render = 0, gpu = 0;
                for (int i = 0; i < ft; i++) { cpu += Timings[i].cpuFrameTime; main += Timings[i].cpuMainThreadFrameTime; render += Timings[i].cpuRenderThreadFrameTime; gpu += Timings[i].gpuFrameTime; }
                r.Text("frame timing (avg over " + ft + " frames): cpu " + (cpu / ft).ToString("0.00", CultureInfo.InvariantCulture) + " ms, main thread " + (main / ft).ToString("0.00", CultureInfo.InvariantCulture) + " ms, render thread "
                       + (render / ft).ToString("0.00", CultureInfo.InvariantCulture) + " ms, gpu " + (gpu > 0 ? (gpu / ft).ToString("0.00", CultureInfo.InvariantCulture) + " ms" : "n/a"));
            }
            else r.Text(FrameTimingManager.IsFeatureEnabled() ? "frame timing: no data yet" : "frame timing: off in this build (Player Settings > Frame Timing Stats)");
            foreach (var c in Counters)
            {
                if (!c.Rec.Valid) { r.Text("  " + c.Label + ": n/a in this build" + (Debug.isDebugBuild ? "" : " (many counters need a Development Build)")); continue; }
                int n = (int)c.Rec.Count;   // ProfilerRecorder.Count is a long
                if (n == 0) { r.Text("  " + c.Label + ": " + Val(c.Rec.LastValue, c.Unit)); continue; }
                double sum = 0, max = 0;
                for (int i = 0; i < n; i++) { double v = c.Rec.GetSample(i).Value; sum += v; if (v > max) max = v; }
                r.Text("  " + c.Label + ": last " + Val(c.Rec.LastValue, c.Unit) + ", avg " + Val(sum / n, c.Unit) + ", max " + Val(max, c.Unit) + " (over " + n + " frames)");
            }
        }

        static readonly FrameTiming[] Timings = new FrameTiming[30];

        static IEnumerator ProfilerCapture(JObject a, ToolResponse r)
        {
            if (!Application.isEditor && !Debug.isDebugBuild) throw new ToolException("a profiler capture needs a Development Build or the editor; profiler_get works here");
            if (!Profiler.supported) throw new ToolException("the profiler is not available in this build");
            if (Profiler.enabled) throw new ToolException("the profiler is already recording; try again when that capture ends");
            int frames = Math.Max(1, Math.Min(a.Int("frames", 120), 3600));
            string dir = StateTools.ExportDir();
            long limit = StateTools.ExportLimit();
            StateTools.MakeRoom(dir, Math.Min(frames * 100000L, limit));
            string file = Path.Combine(dir, "profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture).Substring(0, 6));
            Sandbox.Own(() => Profiler.logFile = file);
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            try { for (int i = 0; i < frames; i++) yield return null; }
            finally { Profiler.enabled = false; Profiler.enableBinaryLog = false; Profiler.logFile = ""; }
            file += ".raw";
            long size = -1;
            Sandbox.Own(() => { if (File.Exists(file)) size = new FileInfo(file).Length; });
            if (size < 0) throw new ToolException("Unity did not write the profiler capture on this platform");
            Driver.OwnerOnly(file, false);
            if (size > limit)
            {
                Sandbox.Own(() => File.Delete(file));
                throw new ToolException("the capture was " + size / 1048576 + " MB, over the " + limit / 1048576 + " MB limit of the exports folder (the owner sets it with UDRIVER_EXPORTS_MAX_MB), and was deleted");
            }
            StateTools.MakeRoom(dir, size, file);
            MemoryTools.Out(r, new JObject { ["file"] = file, ["bytes"] = size, ["frames"] = frames, ["open"] = "Unity Profiler: Window > Analysis > Profiler, then Load the file" });
        }

        static void ProfilerList(JObject a, ToolResponse r)
        {
            string contains = a.Str("contains");
            int limit = a.Int("limit", 60), n = 0, more = 0;
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                string label = d.Category.ToString() + "/" + d.Name;
                if (contains != null && !Util.Contains(label, contains)) continue;
                if (n < limit) { r.Text("  " + label + " (" + d.UnitType + ")"); n++; } else more++;
            }
            r.Text(n + " counter(s)" + (more > 0 ? ", " + more + " more - use 'contains' to filter" : "") + ". Add one with profiler_get add=[\"Category/Name\"].");
        }
    }
}

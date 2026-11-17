using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            public int Id; public string Kind, Desc;
            public Func<string> Stats; public Action Remove;
        }

        sealed class Watch { public int Id; public string Path, Label, Last; public int Changes; }

        sealed class TraceInfo { public int ProbeId, Count, Max; public bool Args, Stack; }

        static readonly List<Probe> Probes = new List<Probe>();
        static readonly List<Watch> Watches = new List<Watch>();
        static readonly ConcurrentDictionary<MethodBase, TraceInfo> Traced = new ConcurrentDictionary<MethodBase, TraceInfo>();
        static int _nextId = 1;

        public static int Count { get { return Probes.Count; } }

        internal static void Register()
        {
            Driver.Register("watch_add", Driver.Sync(WatchAdd));
            Driver.Register("trace_add", Driver.Sync(TraceAdd));
            Driver.Register("trigger_add", Driver.Sync(TriggerAdd));
            Driver.Register("probe_list", Driver.Sync(List));
            Driver.Register("probe_remove", Driver.Sync(RemoveTool));
            Driver.Register("profiler_get", Driver.Sync(ProfilerGet));
            Driver.Register("profiler_list", Driver.Sync(ProfilerList));
            Driver.Register("memory_top", Driver.Sync(MemoryTop));
            InitProfiler();
        }

        // ---------------- registry ----------------

        static Probe Add(string kind, string desc, Func<string> stats, Action remove)
        {
            var p = new Probe { Id = _nextId++, Kind = kind, Desc = desc, Stats = stats, Remove = remove };
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
            if (Probes.Count == 0) { r.Text("no active probes"); return; }
            foreach (var p in Probes) r.Text("probe " + p.Id + " [" + p.Kind + "] " + p.Desc + " | " + p.Stats());
        }

        static void RemoveTool(JObject a, ToolResponse r)
        {
            if (a.Bool("all")) { int n = Probes.Count; RemoveAll(); r.Text("removed " + n + " probe(s)"); return; }
            if (!a.Has("id")) throw new ToolException("give id or all=true");
            r.Text(RemoveById(a.Int("id")) ? "removed probe " + a.Int("id") : "no probe with id " + a.Int("id"));
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
        static bool _harmonySearched;

        static Type FindHarmony()
        {
            if (_harmonySearched) return _hType;
            _harmonySearched = true;
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
                throw new ToolException("method tracing needs Harmony: put 0Harmony.dll (Lib.Harmony 2.x, net48 build) into Assets/Plugins and rebuild. Works on Mono only, not IL2CPP. "
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

        static object HMethod(MethodInfo m)
        {
            return _hMethodType.GetConstructor(new[] { typeof(MethodInfo) }).Invoke(new object[] { m });
        }

        // Harmony injects parameters by name: __originalMethod, __args, __result, __exception.
        static void TracePrefix(MethodBase __originalMethod, object[] __args)
        {
            TraceInfo ti;
            if (__originalMethod == null || !Traced.TryGetValue(__originalMethod, out ti)) return;
            if (Interlocked.Increment(ref ti.Count) > ti.Max) return;
            var sb = new StringBuilder("call ").Append(__originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Name : "?").Append('.').Append(__originalMethod.Name).Append('(');
            if (ti.Args && __args != null)
                for (int i = 0; i < __args.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(StateTools.Short(__args[i])); }
            sb.Append(')');
            EventStore.Emit("trace", sb.ToString(), ti.Stack ? Environment.StackTrace : null);
        }

        static void TraceResult(MethodBase __originalMethod, object __result)
        {
            TraceInfo ti;
            if (__originalMethod == null || !Traced.TryGetValue(__originalMethod, out ti) || ti.Count > ti.Max) return;
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

            bool args = a.Bool("args", true), result = a.Bool("result"), stack = a.Bool("stack");
            int max = a.Int("max", 2000);
            var patched = new List<MethodBase>();
            var failed = new List<string>();
            int probeId = _nextId;
            foreach (var m in methods)
            {
                if (Traced.ContainsKey(m)) continue;
                var ti = new TraceInfo { ProbeId = probeId, Max = max, Args = args, Stack = stack };
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

            var p = Add("trace", pattern + " (" + patched.Count + " method(s))",
                () =>
                {
                    var sb = new StringBuilder(); int total = 0, shown = 0;
                    foreach (var m in patched) { TraceInfo ti; if (!Traced.TryGetValue(m, out ti)) continue; total += ti.Count; if (ti.Count > 0 && shown++ < 12) sb.Append(' ').Append(m.DeclaringType.Name).Append('.').Append(m.Name).Append(" x").Append(ti.Count).Append(';'); }
                    return total + " call(s) total;" + (total == 0 ? " no traced method has been called yet" : sb.ToString());
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
            r.Text("probe " + p.Id + ": tracing " + patched.Count + " method(s) matching '" + pattern + "'. Calls appear as 'trace' events; probe_list shows call counts.");
            for (int i = 0; i < patched.Count && i < 25; i++) r.Text("  " + patched[i].DeclaringType.Name + "." + patched[i].Name);
            if (patched.Count > 25) r.Text("  ... and " + (patched.Count - 25) + " more");
            if (failed.Count > 0) r.Text(failed.Count + " method(s) could not be patched, e.g. " + failed[0]);
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
            if (unit == "ns") return (v / 1e6).ToString("0.00") + " ms";
            if (unit == "bytes") return v >= 1048576 ? (v / 1048576.0).ToString("0.0") + " MB" : (v / 1024.0).ToString("0.0") + " KB";
            return v.ToString("0.##");
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
            r.Text("fps (last frame): " + (Time.unscaledDeltaTime > 0 ? (1f / Time.unscaledDeltaTime).ToString("0.0") : "?") + " | frame " + Time.frameCount
                   + " | managed heap: " + Val(GC.GetTotalMemory(false), "bytes") + " | GC gen0 collections: " + GC.CollectionCount(0)
                   + " | unity allocated: " + Val(Profiler.GetTotalAllocatedMemoryLong(), "bytes") + " | mono used: " + Val(Profiler.GetMonoUsedSizeLong(), "bytes"));
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

        static void MemoryTop(JObject a, ToolResponse r)
        {
            string name = a.Str("type", "Texture");
            var type = typeof(UnityEngine.Object).Assembly.GetType("UnityEngine." + name, false) ?? StateTools.FindType("UnityEngine." + name) ?? StateTools.FindType(name);
            if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type)) throw new ToolException("'" + name + "' is not a UnityEngine.Object type. Try Texture, Texture2D, RenderTexture, Mesh, AudioClip, Material, Sprite, Font, Shader, AnimationClip.");
            var objs = Resources.FindObjectsOfTypeAll(type);
            var list = new List<KeyValuePair<long, UnityEngine.Object>>();
            long total = 0;
            foreach (var o in objs) { long sz = Profiler.GetRuntimeMemorySizeLong(o); total += sz; list.Add(new KeyValuePair<long, UnityEngine.Object>(sz, o)); }
            list.Sort((x, y) => y.Key.CompareTo(x.Key));
            r.Text(objs.Length + " " + type.Name + " object(s) loaded, total " + Val(total, "bytes"));
            int limit = a.Int("limit", 15);
            for (int i = 0; i < list.Count && i < limit; i++)
                r.Text("  " + Val(list[i].Key, "bytes") + "  " + list[i].Value.GetType().Name + " \"" + list[i].Value.name + "\"");
        }
    }
}

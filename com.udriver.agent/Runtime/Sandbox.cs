using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UDriver
{
    public sealed class SandboxException : Exception { internal bool Quiet; public SandboxException(string m) : base(m) { } }

    /// <summary>
    /// Runs agent code compiled by the server's eval tool. The server rewrites every agent method to call
    /// Enter/Exit, Tick at loop back-edges, CheckAbort at catch starts and Alloc before new T[n]; while agent
    /// code is on the stack (or flows from it into threads and tasks) the guards below wall off the computer.
    /// </summary>
    public static class Sandbox
    {
        sealed class Eval { public int Id, Ms, CallbackMs; public long Steps, Memory; public bool Computer; public volatile string Stopped; }

        const int Chunk = 4096, MaxDepth = 1000;
        static readonly AsyncLocal<int> flow = new AsyncLocal<int>();
        static readonly Dictionary<int, Eval> evals = new Dictionary<int, Eval>();
        [ThreadStatic] static Eval cur;
        [ThreadStatic] static int depth, left, trusted;
        [ThreadStatic] static bool owns, hosted, busy;
        [ThreadStatic] static long steps, deadline, mem0;

        static Eval Find(int id) { lock (evals) { Eval e; return evals.TryGetValue(id, out e) ? e : null; } }

        static Eval Current { get { return depth > 0 ? cur : flow.Value != 0 ? Find(flow.Value) : null; } }

        static bool InAgent { get { return depth > 0 || flow.Value != 0; } }

        /// <summary>Agent code is running and the owner has not opened the computer to it.</summary>
        static bool Walled { get { if (trusted > 0 || busy || !InAgent) return false; var e = Current; return e == null || !e.Computer; } }

        static volatile bool tainted;
        static readonly HashSet<string> known = new HashSet<string>();

        /// <summary>The game's own code after an agent changed the game: it may have been pointed at the computer, so the wall holds until the game restarts.</summary>
        static bool Fenced { get { return tainted && GameCode; } }

        static bool GameCode { get { return trusted == 0 && !busy && !InAgent; } }

        /// <summary>The driver's own files (registration, exports, snapshots) stay writable after an agent changed the game.</summary>
        internal static void Own(Action a) { trusted++; try { a(); } finally { trusted--; } }

        public static void Enter(int id)
        {
            if (depth == 0)
            {
                int f = flow.Value;
                var e = Find(f != 0 ? f : id);
                if (e == null) throw new SandboxException("eval " + id + " is no longer loaded");
                if (e.Stopped != null) throw new SandboxException("eval " + e.Id + " stopped: " + e.Stopped) { Quiet = true };
                cur = e; steps = 0; left = Chunk; mem0 = GC.GetTotalMemory(false);
                deadline = Stopwatch.GetTimestamp() + (hosted ? e.Ms : e.CallbackMs) * Stopwatch.Frequency / 1000;
                if (f == 0) { flow.Value = e.Id; owns = true; }
            }
            else
            {
                if (--left <= 0) Slow();
                if (depth >= MaxDepth) Stop("recursion deeper than " + MaxDepth);
                try { RuntimeHelpers.EnsureSufficientExecutionStack(); }
                catch (InsufficientExecutionStackException) { Stop("stack overflow"); }
            }
            depth++;
        }

        public static void Exit()
        {
            if (--depth > 0) return;
            if (owns) { flow.Value = 0; owns = false; }
            cur = null;
        }

        public static int Depth() { return depth; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Tick() { if (--left <= 0) Slow(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Slow()
        {
            steps += Chunk; left = Chunk;
            var e = cur;
            if (e == null) return;
            if (e.Stopped != null) Stop(null);
            if (steps > e.Steps) Stop("step limit (" + e.Steps + ")");
            if (Stopwatch.GetTimestamp() > deadline) TimeUp();
            if (GC.GetTotalMemory(false) - mem0 > e.Memory) Stop("memory limit (" + (e.Memory >> 20) + " MB)");
        }

        static void TimeUp() { Stop("time limit (" + (hosted ? cur.Ms : cur.CallbackMs) + " ms)"); }

        static void Stop(string why)
        {
            var e = cur;
            if (e == null) throw new SandboxException(why);
            bool first = why != null && e.Stopped == null;
            if (first) e.Stopped = why;
            left = 0;
            throw new SandboxException("eval " + e.Id + " stopped: " + e.Stopped) { Quiet = !first };
        }

        public static void CheckAbort() { var e = cur; if (e != null && e.Stopped != null) Stop(null); }

        /// <summary>Before new T[n]: refuse arrays that would not fit the memory limit.</summary>
        public static long Alloc(long n, int size)
        {
            var e = cur;
            if (e != null && n > 0 && n * size > e.Memory - (GC.GetTotalMemory(false) - mem0)) Stop("memory limit (" + (e.Memory >> 20) + " MB): new array of " + n + " items");
            return n;
        }

        /// <summary>
        /// Exception filter of every agent method. On threads the host does not own (game callbacks, coroutines,
        /// agent threads, timers) the outermost agent frame swallows a sandbox stop so the game keeps running, and the eval's later callbacks stay silent.
        /// </summary>
        public static bool Orphan(object x, int d)
        {
            var s = x as SandboxException;
            if (hosted || s == null || !(d == 1 || d == 0 && depth == 0)) return false;
            if (s.Quiet) return true;
            var e = d == 1 ? cur : null;
            if (e != null && e.Stopped == null) e.Stopped = s.Message;
            Debug.LogWarning("[UDriver] eval: " + s.Message + (e != null ? "; its callbacks are stopped" : ""));
            return true;
        }

        static readonly ConditionalWeakTable<object, Dictionary<MethodInfo, Delegate>> wrapped = new ConditionalWeakTable<object, Dictionary<MethodInfo, Delegate>>();
        static readonly object noTarget = new object();

        /// <summary>
        /// A delegate to code outside the agent assembly (method group, reflection, expression compile): the
        /// wrapper re-enters the agent context whenever it is invoked. Equal delegates get the same wrapper,
        /// so -= removes what += added.
        /// </summary>
        public static Delegate Wrap(Delegate d, int id)
        {
            if (d == null || d.GetInvocationList().Length != 1) return d;
            var map = wrapped.GetOrCreateValue(d.Target ?? noTarget);
            var key = d.Method;
            lock (map)
            {
                Delegate w;
                if (map.TryGetValue(key, out w) && w.GetType() == d.GetType()) return w;
                var inv = d.GetType().GetMethod("Invoke");
                var ps = inv.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                var dv = Expression.Variable(typeof(int));
                var ex = Expression.Parameter(typeof(Exception));
                var body = Expression.Block(new[] { dv }, Expression.TryCatch(
                    Expression.Block(
                        Expression.Call(typeof(Sandbox).GetMethod("Enter"), Expression.Constant(id)),
                        Expression.Assign(dv, Expression.Call(typeof(Sandbox).GetMethod("Depth"))),
                        Expression.TryFinally(Expression.Invoke(Expression.Constant(d), ps), Expression.Call(typeof(Sandbox).GetMethod("Exit")))),
                    Expression.Catch(ex, Expression.Default(inv.ReturnType), Expression.Call(typeof(Sandbox).GetMethod("Orphan"), ex, dv))));
                trusted++;
                try { w = Expression.Lambda(d.GetType(), body, ps).Compile(); }
                finally { trusted--; }
                map[key] = w;
                return w;
            }
        }

        // ---------- the eval tool ----------

        internal static void Register()
        {
            Driver.Register("eval", Driver.Sync(Run));
#if UNITY_EDITOR
            AppDomain.CurrentDomain.DomainUnload += (s, e) => { if (harmony != null) Call(harmony, "UnpatchSelf"); };
#endif
        }

        static void Run(JObject a, ToolResponse r)
        {
#if ENABLE_IL2CPP
            if (a.Bool("arm")) throw new ToolException("the computer guards could not be installed (IL2CPP build)");
            throw new ToolException("eval needs a Mono build; IL2CPP games cannot load code at run time");
#else
            if (a.Bool("refs")) { r.Text(Refs().ToString(Formatting.None)); return; }
            var stop = a.Str("stop");
            if (stop != null)
            {
                lock (evals)
                    foreach (var e in evals.Values)
                        if (stop == "all" || stop == e.Id.ToString()) e.Stopped = e.Stopped ?? "stopped by the agent";
                r.Text("stopped " + stop);
                return;
            }
            if (a.Str("harmony") != null && ProbeTools.FindHarmony() == null) Assembly.Load(harmonyBytes = Convert.FromBase64String(a.Str("harmony")));
            if (a.Bool("arm"))
            {
                var no = Install();
                if (no.Count > 0) throw new ToolException("the computer guards could not be installed (" + string.Join("; ", no.Take(5)) + ")");
                if (a.Bool("taint")) { if (Application.isEditor) throw new ToolException(EditorOpen); Taint(); }
                r.Text(tainted ? "walled" : "armed");
                if (tainted) r.Text(MarkDir);
                return;
            }
            var asm = a.Str("asm");
            if (asm == null) throw new ToolException("code is required");
            var ev = new Eval
            {
                Id = a.Int("id"), Ms = Math.Max(a.Int("timeoutMs", 10000), 1), CallbackMs = Math.Max(a.Int("callbackMs", 1000), 1),
                Steps = Math.Max(a.Long("maxSteps", 500000000), 1), Memory = Math.Max(a.Long("maxMemoryMB", 256), 1) << 20, Computer = a.Bool("computer"),
            };
            if (!ev.Computer)
            {
                if (Application.isEditor) throw new ToolException(EditorOpen);
                var missing = Install();
                if (missing.Count > 0)
                    throw new ToolException("eval is off in this game: the computer guards could not be installed (" + string.Join("; ", missing.Take(5)) + ")");
                Taint();
            }
            lock (evals) evals[ev.Id] = ev;
            var run = Assembly.Load(Convert.FromBase64String(asm)).GetType("Agent")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (run == null) throw new ToolException("code must define public static class Agent with public static object Run()");
            hosted = true;
            try
            {
                object res = run.Invoke(null, null);
                var co = res as IEnumerator;
                if (co != null && Driver.Host != null) { Driver.Host.StartCoroutine(co); r.Text("eval " + ev.Id + ": coroutine started (stop it with eval stop=" + ev.Id + ")"); }
                else r.Text("eval " + ev.Id + ": " + Util.Cut(Show(res, 0), 20000));
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                r.Error(e is SandboxException ? e.Message : e.GetType().Name + ": " + e.Message + "\n" + Util.Cut(e.StackTrace, 1500));
            }
            finally { hosted = false; }
#endif
        }

        static byte[] harmonyBytes;
        static string markReal, install;
        static string[] saves;

        internal static string MarkDir { get { return Path.Combine(Application.persistentDataPath, "UDriver"); } }

        /// <summary>An agent changed this game (or another build sharing its saves) while Computer access was off: the marker in its
        /// data folder, or the server's record of it when the marker is gone.</summary>
        public static bool Marked()
        {
            string dir = MarkDir, list = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver", "targets.json");
            if (File.Exists(Path.Combine(dir, "changed"))) return true;
            try { return File.Exists(list) && (JObject.Parse(File.ReadAllText(list))["games"] as JArray ?? new JArray()).Any(g => string.Equals((string)g["mark"], dir, PathCase)); }
            catch (Exception) { return false; }
        }

        /// <summary>From now on, and after every restart until the owner clears the mark, the game's own code is walled too.</summary>
        static void Taint()
        {
            tainted = true;
            Own(() =>
            {
                Directory.CreateDirectory(MarkDir);
                File.WriteAllText(Path.Combine(MarkDir, "changed"), "An agent changed this game while Computer access was off, so its own code cannot reach the computer. The owner clears this in the UDriver panel.\n");
                if (harmonyBytes != null) File.WriteAllBytes(Path.Combine(MarkDir, "0Harmony.dll"), harmonyBytes);
            });
        }

#if !ENABLE_IL2CPP
        /// <summary>Before any scene or game code runs, so a changed save cannot act on the computer before the server connects.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Early()
        {
            if (Application.isEditor || !Marked()) return;
            try
            {
                string h = new[] { MarkDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver") }.Select(d => Path.Combine(d, "0Harmony.dll")).FirstOrDefault(File.Exists);
                if (ProbeTools.FindHarmony() == null && h != null) Assembly.Load(harmonyBytes = File.ReadAllBytes(h));
                var no = Install();
                if (no.Count > 0) throw new Exception(string.Join("; ", no.Take(5)));
                Taint();
            }
            catch (Exception e)
            {
                Debug.LogError("UDriver: an agent changed this game while Computer access was off, and the guards that keep the game's code away from the computer could not start (" + e.Message + "), so the game closes. The owner can clear the mark in the UDriver panel.");
                Process.GetCurrentProcess().Kill();
            }
        }
#endif

        static string Show(object o, int level)
        {
            if (o == null) return "null";
            var u = o as UnityEngine.Object;
            if (u is object) return u ? u.GetType().Name + " '" + u.name + "'" : u.GetType().Name + " (destroyed)";
            if (o is string s) return s;
            if (o is IFormattable f) return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            if (o is IEnumerable list && level == 0)
            {
                var items = new List<string>();
                foreach (var x in list) { if (items.Count == 200) { items.Add("..."); break; } items.Add(Show(x, 1)); }
                return "[" + items.Count + "] " + string.Join("\n", items);
            }
            return o.ToString();
        }

        static JObject Refs()
        {
            var locs = new JArray();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string loc;
                try { loc = asm.IsDynamic ? "" : asm.Location; } catch { loc = ""; }
                if (loc != "") locs.Add(loc);
            }
            return new JObject
            {
                ["refs"] = locs, ["dir"] = Path.GetDirectoryName(typeof(object).Assembly.Location),
                ["managed"] = Application.isEditor ? null : Path.GetDirectoryName(typeof(UnityEngine.Object).Assembly.Location),
                ["harmony"] = ProbeTools.FindHarmony() != null, ["editor"] = Application.isEditor,
            };
        }

        const string EditorOpen = "the computer guards could not be installed: the Unity editor runs its own and the project's tools, which could act on a change outside the project";

        // ---------- computer guards (Harmony prefixes, active only while Walled) ----------

        static object harmony;
        static List<string> missing;
        static string[] roots, writeRoots, personal;
        static readonly HashSet<int> ports = new HashSet<int>();
        static readonly string[] CodeExt = { ".dll", ".exe", ".so", ".dylib", ".bundle", ".x86_64", ".x86", ".cs", ".asmdef", ".asmref", ".rsp", ".jslib", ".jspre", ".mm", ".m", ".c", ".cpp", ".h", ".java", ".kt", ".jar", ".aar", ".pdb", ".mdb", ".config", ".plist", ".sh", ".command", ".bat", ".cmd", ".ps1", ".app" };
        static readonly string[] Protected = { "UDriver", "HarmonyLib", "MonoMod", "Mono", "Microsoft.Win32", "System.Runtime.InteropServices", "System.Reflection.Emit", "System.Runtime.Loader", "System.IO.Pipes", "System.IO.MemoryMappedFiles", "Unity.Collections", "Unity.Jobs", "Unity.Burst", "UnityEditor", "UnityEngine.Windows", "System.Runtime.CompilerServices.Unsafe" };
        static readonly StringComparison PathCase = Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        static List<string> Install()
        {
            if (missing != null) return missing;
            var h = ProbeTools.FindHarmony();
            if (h == null) return new List<string> { "Harmony is not loaded" };
            missing = new List<string>();
            harmony = Activator.CreateInstance(h, "udriver.sandbox");
            string project = Path.GetDirectoryName(Application.dataPath);
            roots = new[] { project, Application.dataPath, Application.persistentDataPath, Application.temporaryCachePath }.Select(Real).ToArray();
            writeRoots = Application.isEditor ? new[] { Application.dataPath, Application.persistentDataPath, Application.temporaryCachePath }.Select(Real).ToArray() : roots;
            string logDir = Application.isEditor || string.IsNullOrEmpty(Application.consoleLogPath) ? null : Path.GetDirectoryName(Application.consoleLogPath);
            if (!string.IsNullOrEmpty(logDir)) writeRoots = roots = roots.Append(Real(logDir)).ToArray();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            markReal = Real(MarkDir);
            if (!Application.isEditor) install = Real(Application.platform == RuntimePlatform.OSXPlayer ? project : Application.dataPath);
            saves = new[] { Application.persistentDataPath, Application.temporaryCachePath }.Select(Real).ToArray();
            personal = new[] { string.IsNullOrEmpty(home) ? null : Path.GetDirectoryName(Real(home)), "/Volumes", "/media", "/mnt", "/run/media" }.Where(d => !string.IsNullOrEmpty(d)).ToArray();
            busy = true;
            try
            {
                var core = typeof(object).Assembly;
                var sys = typeof(Socket).Assembly;
                foreach (var t in new[] { "System.IO.MonoIO", "System.IO.FileSystem", "System.IO.Enumeration.FileSystemEnumerableFactory" })
                    foreach (var m in Methods(core.GetType(t), true).Where(m => m.IsStatic && m.Name != "GetException" && m.GetParameters().All(p => !p.ParameterType.Name.Contains("Span`1")) && m.GetParameters().Any(p => p.ParameterType == typeof(string))))
                        Patch(m, "PathArgs");
                foreach (var m in Methods(core.GetType("System.IO.FileSystem"), true).Where(m => m.IsStatic && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(ReadOnlySpan<char>)))
                    Patch(m, "SpanPath");
                foreach (var m in Methods(typeof(Process), true).Where(m => m.Name.StartsWith("Start") || m.Name == "Kill" || m.Name.StartsWith("GetProcess") && m.Name != "GetProcessHandle"))
                    Patch(m, m.Name.StartsWith("GetProcess") ? "Refuse" : "Launch");
                foreach (var m in Methods(typeof(Socket), true).Where(m => m.IsStatic && (m.Name == "Connect_internal" || m.Name == "SendTo_internal" || m.Name == "Bind_internal")))
                    Patch(m, "SockArgs");
                Patch(typeof(Socket).GetMethod("Bind", new[] { typeof(EndPoint) }), null, "Bound");
                // Runtime natives that open a caller's path themselves, not through MonoIO.
                foreach (var m in Methods(typeof(Socket), true).Where(m => m.Name.Contains("SendFile"))) Patch(m, "PathArgs");
                Patch(typeof(FileVersionInfo).GetMethod("GetVersionInfo_internal", BindingFlags.NonPublic | BindingFlags.Instance), "PathArgs");
                Patch(typeof(AssemblyName).GetMethod("GetAssemblyName"), "PathArgs");
                foreach (var m in Methods(typeof(Enumerable).Assembly.GetType("System.IO.MemoryMappedFiles.MemoryMapImpl"), true).Where(m => m.Name == "OpenFile")) Patch(m, "PathArgs");
                foreach (var m in Methods(core.GetType("System.Security.AccessControl.NativeObjectSecurity"), false).Where(m => m.Name == "InternalGet" || m.Name == "InternalSet")) Patch(m, "PathArgs");
                foreach (var t in new[] { "System.IO.Ports.SerialPortStream", "System.IO.Ports.WinSerialStream" })
                    foreach (var c in sys.GetType(t)?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? new ConstructorInfo[0]) Patch(c, "PathArgs");
                // Directory listings by any enumerator, and folder watchers: file names are computer data too.
                Patch(core.GetType("System.IO.Enumeration.FileSystemEntry")?.GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Static), "ListDir");
                foreach (var c in typeof(FileSystemWatcher).GetConstructors()) Patch(c, "Watch");
                Patch(typeof(FileSystemWatcher).GetProperty("Path").GetSetMethod(), "Watch");
                Patch(typeof(FileSystemWatcher).GetProperty("EnableRaisingEvents").GetSetMethod(), "Watch");
                Patch(typeof(FileSystemWatcher).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance), "Watch");
                foreach (var t in new[] { "System.IO.DefaultWatcher", "System.IO.KeventWatcher", "System.IO.FAMWatcher", "System.IO.InotifyWatcher", "System.IO.WindowsWatcher", "System.IO.CoreFXFileSystemWatcherProxy", "System.IO.KqueueMonitor" })
                    foreach (var m in Methods(sys.GetType(t), false).Where(m => m.Name == "StartDispatching" || m.Name == "Add" || m.Name == "Scan")) Patch(m, "Watch");
                foreach (var m in Methods(typeof(Dns), true).Where(m => m.IsStatic && m.GetParameters().Any(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(IPAddress))))
                    Patch(m, "DnsArgs");
                foreach (var m in Methods(typeof(ThreadPool), true).Where(m => m.IsPublic && m.Name.StartsWith("Unsafe"))) Patch(m, "Refuse");
                Patch(typeof(ExecutionContext).GetMethod("SuppressFlow"), "Refuse");
                Patch(typeof(Task).GetMethod("InternalWait", BindingFlags.NonPublic | BindingFlags.Instance), "Clamp0", "Late");
                Patch(typeof(Task).GetMethod("WaitAllBlockingCore", BindingFlags.NonPublic | BindingFlags.Static), "Clamp1", "Late");
                Patch(typeof(Monitor).GetMethod("ObjWait", BindingFlags.NonPublic | BindingFlags.Static), "Clamp1", "Late");
                Patch(typeof(WaitHandle).GetMethod("InternalWaitOne", BindingFlags.NonPublic | BindingFlags.Static), "Clamp1Long", "Late");
                Patch(typeof(WaitHandle).GetMethod("WaitMultiple", BindingFlags.NonPublic | BindingFlags.Static), "Clamp1", "Late");
                Patch(typeof(WaitHandle).GetMethod("SignalAndWaitOne", BindingFlags.NonPublic | BindingFlags.Static), "Clamp2", "Late");
                Patch(typeof(Thread).GetMethod("Sleep", new[] { typeof(int) }), "Clamp0", "Late");
                Patch(typeof(Thread).GetMethod("Join", new[] { typeof(int) }), "Clamp0", "Late");
                Patch(typeof(Thread).GetMethod("Sleep", new[] { typeof(TimeSpan) }), "SleepSpan");
                Patch(typeof(Thread).GetMethod("Join", new[] { typeof(TimeSpan) }), "JoinSpan");
                Patch(typeof(Thread).GetMethod("Join", Type.EmptyTypes), "JoinAll");
                Patch(typeof(Application).GetMethod("OpenURL"), "Launch");
                Patch(Type.GetType("UnityEngine.Networking.UnityWebRequest, UnityEngine.UnityWebRequestModule")?.GetMethod("SendWebRequest"), "Web", null, false);
                foreach (var m in Methods(Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule"), false).Where(m => m.Name == "CaptureScreenshot")) Patch(m, "PathArgs");
                foreach (var m in Methods(Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule"), false).Where(m => m.Name.StartsWith("LoadFromFile") || m.Name.StartsWith("Recompress"))) Patch(m, "PathArgs");
                // Unity natives that write a file at a caller's path without managed IO.
                foreach (var c in Type.GetType("UnityEngine.Networking.DownloadHandlerFile, UnityEngine.UnityWebRequestModule")?.GetConstructors() ?? new ConstructorInfo[0]) Patch(c, "PathArgs");
                Patch(typeof(UnityEngine.Profiling.Profiler).GetProperty("logFile")?.GetSetMethod(), "PathArgs");
                foreach (var m in Methods(Type.GetType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine.CoreModule"), false).Where(m => m.Name == "TakeSnapshot")) Patch(m, "PathArgs");
                foreach (var m in Methods(Type.GetType("UnityEngine.Caching, UnityEngine.AssetBundleModule"), false).Where(m => m.Name == "AddCache")) Patch(m, "PathArgs");
                foreach (var t in new[] { "UnityEngine.Windows.File", "UnityEngine.Windows.Directory" })
                    foreach (var m in Methods(typeof(Application).Assembly.GetType(t), false).Where(m => m.IsPublic)) Patch(m, "PathArgs");
                // Microphone, camera, location and their permission prompts; listing devices opens nothing and stays allowed.
                foreach (var n in new[] { "UnityEngine.Microphone:Start", "UnityEngine.WebCamTexture:Play", "UnityEngine.Application:RequestUserAuthorization", "UnityEngine.Device.Application:RequestUserAuthorization",
                                          "UnityEngine.LocationService:Start", "UnityEngine.Compass:set_enabled", "UnityEngine.Gyroscope:set_enabled", "UnityEngine.Windows.Speech.DictationRecognizer:Start",
                                          "UnityEngine.Windows.Speech.PhraseRecognizer:Start", "UnityEngine.Windows.WebCam.PhotoCapture:CreateAsync", "UnityEngine.Windows.WebCam.VideoCapture:CreateAsync",
                                          "UnityEngine.Android.Permission:RequestUserPermission" })
                {
                    var tn = n.Split(':');
                    var t = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType(tn[0])).FirstOrDefault(x => x != null);
                    foreach (var m in Methods(t, false).Where(m => m.IsPublic && m.Name.StartsWith(tn[1]))) Patch(m, "Launch");
                }
                Patch(typeof(GUIUtility).GetProperty("systemCopyBuffer")?.GetSetMethod(), "Refuse", null, false);
                Patch(typeof(GUIUtility).GetProperty("systemCopyBuffer")?.GetGetMethod(), "Refuse", null, false);
                // Reflection into protected code (sandbox, Harmony, interop, native methods) and delegates made outside agent IL.
                foreach (var t in new[] { "System.Reflection.RuntimeMethodInfo", "System.Reflection.RuntimeConstructorInfo" })
                    foreach (var m in Methods(core.GetType(t), true).Where(m => m.Name == "Invoke")) Patch(m, "TargetInstance");
                foreach (var t in new[] { "System.Reflection.RuntimeFieldInfo", "System.Reflection.RtFieldInfo" })
                    foreach (var m in Methods(core.GetType(t), false).Where(m => m.Name == "GetValue" || m.Name == "SetValue" || m.Name == "GetValueDirect" || m.Name == "SetValueDirect")) Patch(m, "TargetInstance");
                foreach (var m in Methods(typeof(Activator), true).Where(m => m.Name.StartsWith("CreateInstance"))) Patch(m, "TargetArgs");
                foreach (var m in Methods(typeof(Delegate), true).Where(m => m.IsStatic && m.Name == "CreateDelegate")) Patch(m, "TargetArgs", "WrapResult");
                foreach (var m in Methods(core.GetType("System.Reflection.RuntimeMethodInfo"), false).Where(m => m.Name == "CreateDelegate")) Patch(m, "TargetInstance", "WrapResult");
                Patch(typeof(RuntimeMethodHandle).GetMethod("GetFunctionPointer"), "Refuse");
                var linq = typeof(Expression).Assembly;
                Patch(linq.GetType("System.Linq.Expressions.Compiler.LambdaCompiler")?.GetMethod("Compile", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(LambdaExpression) }, null), "TreePre", "TreePost", true, "TreeFin");
                Patch(linq.GetType("System.Linq.Expressions.Interpreter.LightCompiler")?.GetMethod("CompileTop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), "TreePre", null, false, "TreeFin");
                Patch(linq.GetType("System.Linq.Expressions.Interpreter.LightDelegateCreator")?.GetMethod("CreateDelegate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null), null, "WrapResult", false);
            }
            finally { busy = false; }
            return missing;
        }

        static IEnumerable<MethodBase> Methods(Type t, bool required)
        {
            if (t == null) { if (required) missing.Add("type not found"); return Enumerable.Empty<MethodBase>(); }
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var list = t.GetMethods(f).Where(m => !m.IsAbstract && !m.ContainsGenericParameters).Cast<MethodBase>().ToList();
            if (list.Count == 0 && required) missing.Add("no methods in " + t.Name);
            return list;
        }

        static MethodInfo hpatch;

        static void Patch(MethodBase m, string pre, string post = null, bool required = true, string fin = null)
        {
            if (m == null) { if (required) missing.Add("method for " + (pre ?? post) + " not found"); return; }
            try
            {
                if (hpatch == null) hpatch = harmony.GetType().GetMethods().First(x => x.Name == "Patch" && x.GetParameters().Length >= 5 && x.GetParameters()[0].ParameterType == typeof(MethodBase));
                var argv = new object[hpatch.GetParameters().Length];
                argv[0] = m; argv[1] = HM(pre); argv[2] = HM(post); argv[4] = HM(fin);
                hpatch.Invoke(harmony, argv);
            }
            catch (Exception e)
            {
                if ((m.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0) return;
                if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                if (required) missing.Add(m.DeclaringType?.Name + "." + m.Name + ": " + e.Message.Split('\n')[0]);
            }
        }

        static object HM(string name)
        {
            return name == null ? null : ProbeTools.HMethod(typeof(Sandbox).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        static void Call(object o, string name) { o.GetType().GetMethod(name, Type.EmptyTypes)?.Invoke(o, null); }

        static void Deny(string what) { throw new SandboxException("not allowed without computer access: " + what); }

        static string Sig(MethodBase m) { return m.DeclaringType?.Name + "." + m.Name; }

        static void Refuse(MethodBase __originalMethod) { if (Walled) Deny(Sig(__originalMethod)); }

        static void Launch(MethodBase __originalMethod) { if (Walled || Fenced) Deny(Sig(__originalMethod)); }

        /// <summary>A blocking wait inside agent code ends at the eval's deadline and then stops the eval, so a deadlock cannot freeze the game.</summary>
        static void Clamp(ref int ms)
        {
            if (depth == 0 || busy || cur == null) return;
            CheckAbort();
            long rest = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (rest <= 0) TimeUp();
            if (ms < 0 || ms > rest) ms = (int)rest + 1;
        }

        static void Late() { if (depth > 0 && !busy && cur != null && Stopwatch.GetTimestamp() > deadline) TimeUp(); }

        static void Clamp0(ref int __0) { Clamp(ref __0); }
        static void Clamp1(ref int __1) { Clamp(ref __1); }
        static void Clamp2(ref int __2) { Clamp(ref __2); }
        static void Clamp1Long(ref long __1) { int ms = __1 < 0 || __1 > int.MaxValue ? -1 : (int)__1; Clamp(ref ms); __1 = ms; }
        static bool SleepSpan(TimeSpan __0) { if (depth == 0) return true; Thread.Sleep((int)Math.Min(__0.TotalMilliseconds, int.MaxValue)); return false; }
        static bool JoinSpan(Thread __instance, TimeSpan __0, ref bool __result) { if (depth == 0) return true; __result = __instance.Join((int)Math.Min(__0.TotalMilliseconds, int.MaxValue)); return false; }
        static bool JoinAll(Thread __instance) { if (depth == 0) return true; __instance.Join(-1); return false; }

        [DllImport("libc", EntryPoint = "realpath")] static extern IntPtr realpath(string path, IntPtr resolved);
        [DllImport("libc", EntryPoint = "free")] static extern void free(IntPtr p);

        /// <summary>Resolves symlinks of the deepest existing ancestor, so a link inside an allowed folder cannot point outside it.</summary>
        static string Real(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('/', '\\'), rest = "";
            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor) return full;
            for (string c = full; !string.IsNullOrEmpty(c); c = Path.GetDirectoryName(c))
            {
                IntPtr r = realpath(c, IntPtr.Zero);
                if (r == IntPtr.Zero) { rest = "/" + Path.GetFileName(c) + rest; continue; }
                string s = Marshal.PtrToStringAnsi(r); free(r);
                return s.TrimEnd('/') + rest;
            }
            return full;
        }

        static bool Under(string p, string[] dirs)
        {
            foreach (var d in dirs)
                if (string.Equals(p, d, PathCase) || p.StartsWith(d + "/", PathCase) || p.StartsWith(d + "\\", PathCase)) return true;
            return false;
        }

        static bool Writes(MethodBase m, object[] args)
        {
            string n = m.Name;
            if (n == ".ctor" || n == "set_logFile" || n == "TakeSnapshot" || n == "AddCache") return true;
            if (n == "Open" || n == "OpenFile" || n.StartsWith("CaptureScreenshot"))
            {
                if (n[0] == 'C') return true;
                foreach (var x in args)
                {
                    if (x is FileMode fm && fm != FileMode.Open) return true;
                    if (x is FileAccess fa && (fa & FileAccess.Write) != 0) return true;
                    if (x is Enum e && e.GetType().Name == "MemoryMappedFileAccess" && e.ToString() != "Read" && e.ToString() != "ReadExecute") return true;
                }
                return false;
            }
            foreach (var w in new[] { "Create", "Delete", "Remove", "Move", "Copy", "Replace", "Set", "Write", "Link", "Recompress" })
                if (n.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static void CheckPath(string s, bool write, MethodBase m)
        {
            if (string.IsNullOrEmpty(s)) return;
            busy = true;
            try
            {
                string p = Real(s);
                bool ok = Under(p, write ? writeRoots : roots) || !write && !InAgent && !Under(p, personal);
                if (ok && write && (InAgent || !Under(p, saves)))
                {
                    string ext = Path.GetExtension(p).ToLowerInvariant();
                    ok = Array.IndexOf(CodeExt, ext) < 0 && p.IndexOf("/Contents/MacOS", PathCase) < 0;
                }
                if (ok && write && (UnityProject(p) || install != null && Under(p, new[] { install }))) ok = false;
                if (write && (Under(p, new[] { markReal }) || m.Name.IndexOf("Create", StringComparison.Ordinal) < 0 && Under(markReal, new[] { p }))) ok = false;
                if (!ok) Deny((write ? "write " : "read ") + s + " (" + Sig(m) + ")");
            }
            finally { busy = false; }
        }

        /// <summary>Under a Unity project's Assets, Packages, settings or Library, or loose in its folder: what the editor imports or compiles.</summary>
        static bool UnityProject(string p)
        {
            for (string child = p, d = Path.GetDirectoryName(p); !string.IsNullOrEmpty(d); child = d, d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, "ProjectSettings", "ProjectVersion.txt")))
                    return child == p || Array.IndexOf(new[] { "Assets", "Packages", "ProjectSettings", "Library", "UserSettings" }, Path.GetFileName(child)) >= 0;
            return false;
        }

        static void PathArgs(object[] __args, MethodBase __originalMethod)
        {
            bool agent = Walled;
            if (!agent && !Fenced) return;
            var ps = __originalMethod.GetParameters();
            bool write = Writes(__originalMethod, __args);
            for (int i = 0; i < ps.Length && i < __args.Length; i++)
                if (__args[i] is string s && ps[i].Name.IndexOf("xpression", StringComparison.Ordinal) < 0)
                {
                    if (ps[i].Name == "pathWithPattern") s = Path.GetDirectoryName(s);
                    else if (ps[i].Name.IndexOf("attern", StringComparison.Ordinal) >= 0) continue;
                    CheckPath(s, write, __originalMethod);
                }
        }

        static void SpanPath(ReadOnlySpan<char> __0, MethodBase __originalMethod)
        {
            bool write = Writes(__originalMethod, new object[0]);
            if (Walled || Fenced) CheckPath(__0.ToString(), write, __originalMethod);
        }

        static void ListDir(ReadOnlySpan<char> __2, MethodBase __originalMethod) { if (Walled || Fenced) CheckPath(__2.ToString(), false, __originalMethod); }

        static void Watch(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!Walled && !Fenced || __args.Length == 1 && __args[0] is bool on && !on) return;
            var ps = __originalMethod.GetParameters();
            for (int i = 0; i < __args.Length; i++) if (ps[i].Name != "filter") CheckPath(__args[i] as string ?? (__args[i] as FileSystemWatcher)?.Path, false, __originalMethod);
            CheckPath((__instance as FileSystemWatcher)?.Path, false, __originalMethod);
        }

        static bool Loopback(IPAddress ip) { return IPAddress.IsLoopback(ip) || ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()); }

        /// <summary>Loopback ports opened inside this process after the guards were installed (game servers, the agent's own sockets); never the driver's.</summary>
        static bool GamePort(int port) { lock (ports) return ports.Contains(port) && (port < ToolManifest.GamePort || port > ToolManifest.GamePort + 40); }

        /// <summary>Hosts the game reached before any agent changed it (its backend); loopback counts per port.</summary>
        static bool Known(string host, bool add)
        {
            if (host == null) return false;
            lock (known) return add ? known.Add(host) : known.Contains(host);
        }

        static string Host(IPAddress ip, int port) { return Loopback(ip) ? ip + ":" + port : ip.ToString(); }

        static void SockArgs(object[] __args, MethodBase __originalMethod)
        {
            bool agent = Walled;
            if (!agent && !GameCode) return;
            var sa = __args.OfType<SocketAddress>().FirstOrDefault();
            IPAddress ip = null; int port = 0;
            if (sa != null && sa.Family == AddressFamily.InterNetwork) ip = new IPAddress(new[] { sa[4], sa[5], sa[6], sa[7] });
            else if (sa != null && sa.Family == AddressFamily.InterNetworkV6) { var b = new byte[16]; for (int i = 0; i < 16; i++) b[i] = sa[8 + i]; ip = new IPAddress(b); }
            if (sa != null && sa.Size >= 4) port = sa[2] << 8 | sa[3];
            bool bind = __originalMethod.Name.StartsWith("Bind");
            if (ip != null && Loopback(ip) && (bind || GamePort(port))) return;
            string key = ip != null ? Host(ip, port) : sa?.ToString();
            if (!agent && !tainted) { Known(key, true); return; }
            if (!agent && Known(key, false)) return;
            Deny(Sig(__originalMethod) + " " + (ip == null ? sa?.Family.ToString() : ip + ":" + port));
        }

        static void Bound(Socket __instance)
        {
            if (__instance.LocalEndPoint is IPEndPoint ep) lock (ports) ports.Add(ep.Port);
        }

        static void DnsArgs(object[] __args, MethodBase __originalMethod)
        {
            bool agent = Walled;
            if (!agent && !GameCode) return;
            string key = null;
            foreach (var x in __args)
            {
                IPAddress ip;
                if (x is IPAddress a && Loopback(a) || x is string s && (s == "localhost" || IPAddress.TryParse(s, out ip) && Loopback(ip))) return;
                if (key == null && (x is string || x is IPAddress)) key = "dns:" + x;
            }
            if (!agent && !tainted) { Known(key, true); return; }
            if (!agent && Known(key, false)) return;
            Deny(Sig(__originalMethod) + " " + string.Join(",", __args.Select(x => x?.ToString())));
        }

        static void Web(object __instance)
        {
            bool agent = Walled;
            if (!agent && !GameCode) return;
            var t = __instance.GetType();
            var u = new Uri((string)t.GetProperty("url").GetValue(__instance));
            IPAddress ip;
            bool loop = u.Host == "localhost" || IPAddress.TryParse(u.Host.Trim('[', ']'), out ip) && Loopback(ip);
            bool local = (u.Scheme == "http" || u.Scheme == "https") && loop && GamePort(u.Port);
            string key = "web:" + u.Scheme + "://" + (loop ? u.Authority : u.Host);
            if (!agent && !tainted) { Known(key, true); return; }
            if (!local && (agent || !Known(key, false))) Deny("UnityWebRequest " + u);
            t.GetProperty("redirectLimit").SetValue(__instance, 0);
        }

        static bool IsProtected(Type t)
        {
            for (; t != null; t = t.DeclaringType)
            {
                if (t.IsGenericParameter) return false;
                if (t.IsPointer) return true;
                if (t.IsByRef || t.IsArray) return IsProtected(t.GetElementType());
                string n = t.FullName ?? t.Name;
                foreach (var p in Protected) if (n == p || n.StartsWith(p + ".", StringComparison.Ordinal)) return true;
                if (t.IsGenericType && t.GetGenericArguments().Any(IsProtected)) return true;
                var an = t.Assembly.GetName().Name;
                if (an.StartsWith("UnityEditor", StringComparison.Ordinal) || an == "0Harmony") return true;
            }
            return false;
        }

        static bool IsProtected(MemberInfo m)
        {
            if (m == null) return false;
            if (m is Type mt) return IsProtected(mt);
            if (IsProtected(m.DeclaringType)) return true;
            var mb = m as MethodBase;
            if (mb == null) return m is FieldInfo fi && IsProtected(fi.FieldType);
            if ((mb.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0 || (mb.Attributes & MethodAttributes.PinvokeImpl) != 0) return true;
            if ((mb.DeclaringType == typeof(Assembly) || mb.DeclaringType == typeof(AppDomain) || mb.DeclaringType == typeof(Activator))
                && (mb.Name.StartsWith("Load") || mb.Name.StartsWith("ReflectionOnly") || mb.Name.StartsWith("UnsafeLoad") || mb.Name.StartsWith("Execute") || mb.Name.StartsWith("CreateDomain") || mb.Name.StartsWith("DefineDynamic") || mb.Name.EndsWith("From"))) return true;
            if (mb.IsGenericMethod && mb.GetGenericArguments().Any(IsProtected)) return true;
            if (mb.GetParameters().Any(p => IsProtected(p.ParameterType))) return true;
            return mb is MethodInfo mi && IsProtected(mi.ReturnType);
        }

        static void Check(MemberInfo m, string op) { if (m != null && (Walled || Fenced) && IsProtected(m)) Deny(op + " " + m.DeclaringType?.FullName + "." + m.Name); }

        static void TargetInstance(object __instance, MethodBase __originalMethod) { Check(__instance as MemberInfo, "reflection " + __originalMethod.Name); }

        static void TargetArgs(object[] __args, MethodBase __originalMethod)
        {
            if (!Walled && !Fenced) return;
            if (__originalMethod.Name.EndsWith("From") || __args.Any(x => x is string) && __originalMethod.DeclaringType == typeof(Activator)) Deny(Sig(__originalMethod));
            foreach (var x in __args) Check(x as MemberInfo, "reflection " + __originalMethod.Name);
        }

        static void WrapResult(ref Delegate __result)
        {
            var e = Current;
            if (e != null && trusted == 0) __result = Wrap(__result, e.Id);
        }

        sealed class TreeCheck : ExpressionVisitor
        {
            protected override Expression VisitMethodCall(MethodCallExpression n) { Check(n.Method, "expression"); return base.VisitMethodCall(n); }
            protected override Expression VisitMember(MemberExpression n) { Check(n.Member, "expression"); return base.VisitMember(n); }
            protected override Expression VisitNew(NewExpression n) { Check(n.Constructor, "expression"); return base.VisitNew(n); }
            protected override Expression VisitUnary(UnaryExpression n) { Check(n.Method, "expression"); return base.VisitUnary(n); }
            protected override Expression VisitBinary(BinaryExpression n) { Check(n.Method, "expression"); return base.VisitBinary(n); }
            protected override Expression VisitConstant(ConstantExpression n) { Check(n.Value as MemberInfo, "expression"); return base.VisitConstant(n); }
        }

        static void TreePre(object[] __args)
        {
            bool w = Walled || Fenced;
            trusted++;
            if (w && __args.Length > 0 && __args[0] is Expression x) { trusted--; try { new TreeCheck().Visit(x); } finally { trusted++; } }
        }

        static void TreePost(ref Delegate __result)
        {
            var e = Current;
            if (e != null && trusted == 1) { trusted--; try { __result = Wrap(__result, e.Id); } finally { trusted++; } }
        }

        static Exception TreeFin(Exception __exception) { trusted--; return __exception; }
    }
}

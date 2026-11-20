using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace UDriver
{
    internal sealed class QaRunner : MonoBehaviour
    {
        IEnumerator Start()
        {
            var end = new WaitForEndOfFrame();
            while (true) { yield return end; QaTools.EndOfFrame(); }
        }

        void Update() { QaTools.Tick(); }
    }

    /// <summary>Testing mode: deduplicated findings with the code involved, automatic crawling and repro shrinking.</summary>
    public static class QaTools
    {
        sealed class Finding { public int Id; public string Sig; public JObject J; public byte[] Jpg; }
        sealed class Act { public float T; public string Tool, Text, Target; public GameObject Go; public JObject Args; public int Sig, USig; }
        sealed class LogLine { public string Msg, Stack; public LogType Type; }
        sealed class Ctl { public string Key, Label, Name; public JObject Click, Replay; public GameObject Go; public bool Input; public Vector2 Pt; }
        sealed class Off { public string Key, Desc; public GameObject Go; public Rect R; public float Frac; }

        sealed class Run
        {
            public int Count, Errors, Max = int.MaxValue, Iter;
            public float Deadline = float.MaxValue, Delay;
            public string LastError;
            public Func<string, bool>[] Avoid = new Func<string, bool>[0];
            public List<JObject> Route = new List<JObject>();
            public readonly List<JObject> Steps = new List<JObject>();
            public readonly Dictionary<string, List<string>> Screens = new Dictionary<string, List<string>>();
            public readonly Dictionary<string, List<JObject>> Routes = new Dictionary<string, List<JObject>>();
            public readonly Dictionary<string, string> Seen = new Dictionary<string, string>();
            public readonly HashSet<string> Clicked = new HashSet<string>(), Avoided = new HashSet<string>(), Reached = new HashSet<string>(), Blocked = new HashSet<string>(), Tried = new HashSet<string>();
            public bool Left { get { return Count < Max && Iter++ < Max * 4 + 100 && Time.realtimeSinceStartup < Deadline; } }
        }

        const int MaxFindings = 200, MaxImages = 20, MaxActions = 200, KeepActions = 20;
        static readonly string[] ActionTools = { "ui_click", "pointer", "key", "play", "type_text", "ui_set_value", "ui_drag" };
        static readonly string[] SkipFrames = { "UnityEngine.", "UnityEditor.", "System.", "Mono.", "UDriver.", "Unity.", "Newtonsoft.", "TMPro.", "Microsoft." };
        static readonly string[] NotUser = { "Unity", "System", "Mono", "UDriver", "Newtonsoft", "mscorlib", "netstandard", "Microsoft", "nunit", "0Harmony", "Bee.", "ExCSS", "JetBrains", "ReportGenerator" };
        static readonly string[] PerFrame = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };
        static readonly string[] RecNames = { "BehaviourUpdate", "LateBehaviourUpdate", "FixedBehaviourUpdate", "CoroutinesDelayedCalls" }, RecLabels = { "Update", "LateUpdate", "FixedUpdate", "coroutines" };
        static readonly string[] MonkeyKeys = { "Escape", "Space", "Return", "Tab", "UpArrow", "DownArrow", "LeftArrow", "RightArrow", "W", "A", "S", "D" };
        static readonly HashSet<string> Mutators = new HashSet<string> { "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Insert", "InsertRange", "Clear", "set_Item", "Enqueue", "Dequeue", "Push", "Pop", "Sort", "Reverse", "TryAdd", "TryRemove", "AddFirst", "AddLast", "RemoveFirst", "RemoveLast", "UnionWith", "ExceptWith", "IntersectWith" };
        static readonly HashSet<string> PosSetters = new HashSet<string> { "RectTransform.set_anchoredPosition", "RectTransform.set_anchoredPosition3D", "RectTransform.set_offsetMin", "RectTransform.set_offsetMax", "RectTransform.set_sizeDelta", "RectTransform.set_anchorMin", "RectTransform.set_anchorMax", "RectTransform.SetInsetAndSizeFromParentEdge", "RectTransform.SetSizeWithCurrentAnchors", "Transform.set_position", "Transform.set_localPosition", "Transform.Translate", "Transform.SetPositionAndRotation", "Transform.SetLocalPositionAndRotation" };
        static readonly HashSet<string> Lookups = new HashSet<string> { "Component.GetComponent", "GameObject.GetComponent", "Component.GetComponentInChildren", "Component.GetComponentInParent", "GameObject.Find", "GameObject.FindWithTag", "GameObject.FindGameObjectWithTag", "Object.FindObjectOfType", "Object.FindFirstObjectByType", "Object.FindAnyObjectByType", "Object.FindObjectsByType", "Object.FindObjectsOfType" };
        static readonly Regex NormRx = new Regex(@"""[^""]*""|'[^']*'|0x[0-9a-fA-F]+|\d+(\.\d+)?"), AtRx = new Regex(@"\(at (.+):(\d+)\)\s*$"), InRx = new Regex(@"\bin ([^<].*?):(\d+)\s*$"), IlRx = new Regex(@"\[0x([0-9a-fA-F]+)\]");
        static readonly Regex NetRx = new Regex(@"curl error|cannot (resolve|connect to) (destination )?host|unitywebrequest|http(/[\d.]+)? (error|[45]\d\d)|\bssl\b|\btls\b|certificate|connection (refused|reset|timed out|closed)|name or service not known|no such host|socketexception|webexception", RegexOptions.IgnoreCase);
        static readonly Regex BackRx = new Regex(@"back|close|cancel|return|done|resume|continue|^\s*(ok|x|×|✕)\s*$", RegexOptions.IgnoreCase);

        static readonly List<Finding> Findings = new List<Finding>();
        static readonly Dictionary<string, Finding> BySig = new Dictionary<string, Finding>();
        static readonly List<Finding> NeedImage = new List<Finding>();
        static readonly ConcurrentQueue<LogLine> Logs = new ConcurrentQueue<LogLine>();
        static readonly List<Act> Actions = new List<Act>();
        static int _nextId, _images, _dropped, _pid, _tick, _exFrame, _ownFrame, _lastOwnFrame, _mid, _ownDepth;
        static float _ownSec, _lastOwnSec, _midT, _quietUntil;
        static bool _hooked, _watch, _perf, _nan, _leak, _visual, _wantMagenta, _hasPoint;
        static float _stuckSec = 10, _watchStart, _next, _stuckFrom;
        static string[] _fields = new string[0];
        static string _projectRoot;
        static JObject _run;
        static readonly List<JObject> RunPath = new List<JObject>();
        static GameObject _lastTarget;
        static Vector2 _lastPoint;
        static Recorder[] _rec;

        internal static void Register()
        {
            Driver.Register("qa_watch", Driver.Sync(Watch));
            Driver.Register("qa_findings", Driver.Sync(FindingsTool));
            Driver.Register("qa_crawl", Crawl);
            Driver.Register("qa_isolate", Isolate);
            Driver.Register("qa_risk", Driver.Sync(Risk));
            Driver.Register("qa_visual", Visual);
            if (Driver.Host != null && Driver.Host.GetComponent<QaRunner>() == null) Driver.Host.gameObject.AddComponent<QaRunner>();
            if (_hooked) return;
            _hooked = true;
            _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _projectRoot = Application.isEditor ? Path.GetDirectoryName(Application.dataPath).Replace('\\', '/') + "/" : null;
            Application.logMessageReceivedThreaded += (msg, stack, type) =>
            {
                if (type != LogType.Log && Logs.Count < 1000) Logs.Enqueue(new LogLine { Msg = msg ?? "", Stack = stack, Type = type });
            };
            SceneManager.sceneLoaded += (s, m) => _quietUntil = Time.realtimeSinceStartup + 0.3f;
        }

        // ---------------- per frame ----------------

        static void Safe(string what, Action a)
        {
            using var own = OwnTimer.Start();
            try { a(); }
            catch (Exception e) { Debug.LogWarning("[UDriver] qa " + what + ": " + e.GetType().Name + ": " + e.Message); }
        }

        internal static void Tick()
        {
            LogLine l;
            for (int n = 0; n < 50 && Logs.TryDequeue(out l); n++) { var x = l; Safe("log", () => FromLog(x)); }
            if (!_watch) return;
            if (_perf) Safe("perf", Perf);
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 1; _tick++;
            if (_nan) Safe("nan", Nan);
            if (_fields.Length > 0) Safe("fields", Fields);
            Safe("stuck", Stuck);
            if (_tick % 2 == 0 && _leak) Safe("leak", Leak);
            if (_tick % 2 == 1 && _visual) Safe("visual", VisualWatch);
            if (_tick % 10 == 0 && _visual) _wantMagenta = true;
        }

        internal static void EndOfFrame()
        {
            if ((NeedImage.Count == 0 && !_wantMagenta) || Application.isBatchMode) return;
            bool magenta = _wantMagenta;
            _wantMagenta = false;
            using var own = OwnTimer.Start();
            int w = 0, h = 0;
            Color32[] px = null;
            Safe("screenshot", () => px = CaptureTools.Grab(640, out w, out h));
            if (px == null) { NeedImage.Clear(); return; }
            if (NeedImage.Count > 0)
            {
                var jpg = CaptureTools.Encode(px, w, h, 70);
                foreach (var f in NeedImage) if (_images < MaxImages) { f.Jpg = jpg; f.J["hasImage"] = true; _images++; }
                NeedImage.Clear();
            }
            if (magenta) MagentaCheck(px);
        }

        /// <summary>Called by the driver before every tool call: in-game actions go into the action log.</summary>
        internal static void OnCall(string tool, JObject args)
        {
            try
            {
                if (Array.IndexOf(ActionTools, tool) >= 0) Log(tool, args, args, null);
            }
            catch (Exception e) { Debug.LogWarning("[UDriver] qa action log: " + e.Message); }
        }

        static void Log(string tool, JObject args, JObject record, string text)
        {
            string target = null;
            GameObject go = null;
            if (args.Has("id") || args.Has("name") || (tool != "type_text" && (args.Has("text") || (args.Has("x") && args.Has("y")))))
            {
                try
                {
                    var t = UiTools.Resolve(tool == "type_text" ? new JObject { ["id"] = args["id"], ["name"] = args["name"] } : args);
                    if (t.Go != null) { go = t.Go; target = StateTools.PathOf(t.Go); }
                    if (text == null) text = t.Go != null ? Ugui.Desc(t.Go) : t.El != null ? UiTools.Desc(t.El) : null;
                    if (tool == "ui_click" || tool == "pointer" || tool == "ui_drag") { _lastTarget = t.Go; _lastPoint = t.Screen; _hasPoint = true; }
                }
                catch { }
            }
            Actions.Add(new Act
            {
                T = Time.realtimeSinceStartup, Tool = tool, Args = (JObject)record.DeepClone(), Target = target, Go = go, Sig = _watch ? Progress(true) : 0, USig = _watch ? Progress(false) : 0,
                Text = Util.OneLine(tool + " " + (text ?? record.ToString(Formatting.None)), 160)
            });
            if (Actions.Count > MaxActions) Actions.RemoveAt(0);
        }

        // ---------------- findings ----------------

        static string Scene() { return SceneManager.GetActiveScene().name; }

        static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static string FirstLine(string s) { int i = s.IndexOf('\n'); return (i < 0 ? s : s.Substring(0, i)).Trim(); }

        static Finding Report(string kind, string sigSrc, string severity, string title, string message, string stack, JObject where, string obj, out bool fresh)
        {
            string sig = Hash(sigSrc);
            double now = Math.Round(Time.realtimeSinceStartupAsDouble, 2);
            Finding f;
            fresh = false;
            if (BySig.TryGetValue(sig, out f)) { f.J["count"] = (int)f.J["count"] + 1; f.J["lastTime"] = now; return f; }
            if (Findings.Count >= MaxFindings) { _dropped++; return null; }
            fresh = true;
            using var own = OwnTimer.Start();
            var acts = new JArray();
            for (int i = Math.Max(0, Actions.Count - KeepActions); i < Actions.Count; i++)
                acts.Add(new JObject { ["t"] = Math.Round(Actions[i].T, 2), ["tool"] = Actions[i].Tool, ["args"] = Actions[i].Args.DeepClone(), ["text"] = Actions[i].Text });
            var evs = new JArray();
            foreach (var e in EventStore.Query(0, new[] { "log" }, null, 10, true)) evs.Add(Util.OneLine(EventStore.Fmt(e, false), 300));
            f = new Finding { Id = ++_nextId, Sig = sig };
            f.J = new JObject
            {
                ["id"] = f.Id, ["sig"] = sig, ["kind"] = kind, ["severity"] = severity, ["title"] = Util.OneLine(title, 200), ["message"] = Util.Cut(message, 2000),
                ["stack"] = string.IsNullOrEmpty(stack) ? null : Util.Cut(stack, 4000), ["where"] = where, ["object"] = obj, ["scene"] = Scene(),
                ["count"] = 1, ["firstTime"] = now, ["lastTime"] = now, ["firstUtc"] = DateTime.UtcNow.ToString("o"),
                ["actions"] = acts, ["events"] = evs, ["involved"] = new JArray(), ["shared"] = new JArray(), ["hasImage"] = false
            };
            if (_run != null)
            {
                var run = (JObject)_run.DeepClone();
                run["steps"] = new JArray(RunPath.ConvertAll(x => (JToken)x.DeepClone()));
                f.J["run"] = run;
            }
            Findings.Add(f); BySig[sig] = f;
            if (_images + NeedImage.Count < MaxImages && !Application.isBatchMode) NeedImage.Add(f);
            return f;
        }

        static JObject Where(string key, string file = null, int line = 0, int il = -1)
        {
            return new JObject
            {
                ["type"] = key == null ? "" : TypeOf(key), ["method"] = key == null ? "" : MethodOf(key), ["file"] = file?.Replace('\\', '/'),
                ["line"] = line > 0 ? (JToken)line : JValue.CreateNull(), ["ilOffset"] = il >= 0 ? (JToken)il : JValue.CreateNull()
            };
        }

        static void Inv(Finding f, string type, string role)
        {
            var inv = (JArray)f.J["involved"];
            if (inv.Count >= 20 || string.IsNullOrEmpty(type)) return;
            foreach (var x in inv) if ((string)x["type"] == type && (string)x["role"] == role) return;
            inv.Add(new JObject { ["type"] = type, ["role"] = role });
        }

        static void FromLog(LogLine l)
        {
            if (l.Msg.StartsWith("[UDriver]", StringComparison.Ordinal)) return;
            bool net = NetRx.IsMatch(l.Msg);
            string kind = net ? "network" : l.Type == LogType.Exception ? "exception" : l.Type == LogType.Assert ? "assert" : l.Type == LogType.Error ? "error" : null;
            if (kind == null) return;
            string file; int line, il;
            string key = UserFrame(l.Stack, out file, out line, out il), first = FirstLine(l.Msg);
            bool fresh;
            var f = Report(kind, kind + "|" + NormRx.Replace(Util.Cut(first, 300), "#") + "|" + key, kind == "exception" ? "critical" : net && l.Type == LogType.Warning ? "minor" : "major",
                Util.OneLine(first, 120) + (key != null ? " in " + key : ""), l.Msg, l.Stack, Where(key, file, line, il), null, out fresh);
            if (fresh && kind == "exception") _exFrame = Time.frameCount;
            if (fresh && key != null) Explain(f, key);
        }

        /// <summary>First stack frame in game code: Logical key, plus file:line from "(at path:line)" / "in path:line" and the IL offset.</summary>
        static string UserFrame(string stack, out string file, out int line, out int il)
        {
            file = null; line = 0; il = -1;
            if (string.IsNullOrEmpty(stack)) return null;
            foreach (var raw in stack.Split('\n'))
            {
                var s = raw.Trim();
                if (s.StartsWith("at ", StringComparison.Ordinal)) s = s.Substring(3);
                int p = s.IndexOf('(');
                if (p <= 0) continue;
                string name = s.Substring(0, p).Trim();
                if (name.Length == 0 || name.IndexOf(' ') >= 0) continue;
                bool skip = false;
                foreach (var x in SkipFrames) if (name.StartsWith(x, StringComparison.Ordinal)) { skip = true; break; }
                if (skip) continue;
                int d = name.LastIndexOf(':');
                if (d < 0) { d = name.LastIndexOf('.'); if (d > 0 && name[d - 1] == '.') d--; }
                if (d <= 0) continue;
                var m = AtRx.Match(s);
                if (!m.Success) m = InRx.Match(s);
                if (m.Success) { file = Rel(m.Groups[1].Value); int.TryParse(m.Groups[2].Value, out line); }
                var mi = IlRx.Match(s);
                if (mi.Success) il = Convert.ToInt32(mi.Groups[1].Value, 16);
                return Logical(name.Substring(0, d), name.Substring(d + 1));
            }
            return null;
        }

        static string Rel(string p)
        {
            p = p.Replace('\\', '/');
            if (_projectRoot != null && p.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase)) return p.Substring(_projectRoot.Length);
            foreach (var m in new[] { "/Assets/", "/Packages/" }) { int i = p.IndexOf(m, StringComparison.Ordinal); if (i >= 0) return p.Substring(i + 1); }
            return p;
        }

        // ---------------- code index (IL) ----------------

        static readonly HashSet<Assembly> Scanned = new HashSet<Assembly>();
        static readonly Dictionary<Assembly, bool> UserCache = new Dictionary<Assembly, bool>();
        static readonly Dictionary<string, HashSet<string>> RIdx = Idx(), WIdx = Idx(), CIdx = Idx(), YIdx = Idx(), TIdx = Idx();
        static readonly HashSet<string> Statics = new HashSet<string>(), None = new HashSet<string>();
        static Dictionary<string, List<KeyValuePair<string, string>>> _writers;
        static Dictionary<short, OpCode> _ops;

        static Dictionary<string, HashSet<string>> Idx() { return new Dictionary<string, HashSet<string>>(); }
        static void Add(Dictionary<string, HashSet<string>> d, string k, string v) { HashSet<string> s; if (!d.TryGetValue(k, out s)) d[k] = s = new HashSet<string>(); s.Add(v); }
        static HashSet<string> Get(Dictionary<string, HashSet<string>> d, string k) { HashSet<string> s; return k != null && d.TryGetValue(k, out s) ? s : None; }

        /// <summary>Maps compiler-generated names back to the source method: Saver+&lt;SaveRoutine&gt;d__5.MoveNext -> Saver.SaveRoutine, lambdas -> their enclosing method.</summary>
        static string Logical(string type, string method)
        {
            var segs = new List<string>(type.Replace('/', '+').Split('+'));
            if (method.StartsWith("<", StringComparison.Ordinal)) method = Inner(method, method);
            while (segs.Count > 1 && segs[segs.Count - 1].StartsWith("<", StringComparison.Ordinal))
            {
                method = Inner(segs[segs.Count - 1], method);
                segs.RemoveAt(segs.Count - 1);
            }
            return string.Join(".", segs) + "." + method;
        }

        static string Inner(string s, string d) { int e = s.IndexOf('>'); return e > 1 ? s.Substring(1, e - 1) : d; }
        static int Split(string key) { int d = key.LastIndexOf('.'); if (d > 0 && key[d - 1] == '.') d--; return d; }
        static string TypeOf(string key) { int d = Split(key); return d > 0 ? key.Substring(0, d) : key; }
        static string MethodOf(string key) { int d = Split(key); return d > 0 ? key.Substring(d + 1) : ""; }
        static bool Ctor(string key) { return key.EndsWith("..ctor", StringComparison.Ordinal) || key.EndsWith("..cctor", StringComparison.Ordinal); }

        static string MKey(Type t, string method)
        {
            if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();
            return Logical(t.FullName ?? t.Name, method);
        }

        static string TKey(Type t) { return TypeOf(MKey(t, "_")); }

        static string FKey(FieldInfo f)
        {
            string n = f.Name;
            if (n.StartsWith("<", StringComparison.Ordinal) && n.EndsWith(">k__BackingField", StringComparison.Ordinal)) n = Inner(n, n);
            return TKey(f.DeclaringType) + "." + n;
        }

        static bool User(Assembly asm)
        {
            bool u;
            if (UserCache.TryGetValue(asm, out u)) return u;
            string n = asm.GetName().Name;
            u = !asm.IsDynamic && !n.EndsWith("-Editor", StringComparison.Ordinal) && !n.EndsWith(".Editor", StringComparison.Ordinal) && !n.EndsWith("-Editor-firstpass", StringComparison.Ordinal);
            foreach (var p in NotUser) if (n.StartsWith(p, StringComparison.Ordinal)) u = false;
            return UserCache[asm] = u;
        }

        static bool UserField(FieldInfo f)
        {
            var t = f.DeclaringType;
            if (t == null || !User(t.Assembly) || (t.FullName ?? "").IndexOf('<') >= 0) return false;
            return !f.Name.StartsWith("<", StringComparison.Ordinal) || f.Name.EndsWith(">k__BackingField", StringComparison.Ordinal);
        }

        static void Scan()
        {
            using var own = OwnTimer.Start();
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (Scanned.Contains(asm) || !User(asm)) continue;
                Scanned.Add(asm);
                _writers = null;
                foreach (var t in StateTools.TypesOf(asm))
                {
                    try
                    {
                        foreach (var m in t.GetMethods(all)) ScanMethod(t, m);
                        foreach (var m in t.GetConstructors(all)) ScanMethod(t, m);
                        if (t.Name.IndexOf('<') < 0)
                            foreach (var fi in t.GetFields(all)) if (fi.IsStatic && !fi.IsInitOnly && !fi.IsLiteral && fi.Name.IndexOf('<') < 0) Statics.Add(FKey(fi));
                    }
                    catch (Exception) { }
                }
            }
        }

        static void Il(MethodBase m, Action<OpCode, MemberInfo> visit)
        {
            byte[] il;
            try { var body = m.GetMethodBody(); il = body == null ? null : body.GetILAsByteArray(); } catch { return; }
            if (il == null) return;
            if (_ops == null)
            {
                _ops = new Dictionary<short, OpCode>();
                foreach (var fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                    if (fi.FieldType == typeof(OpCode)) { var o = (OpCode)fi.GetValue(null); _ops[o.Value] = o; }
            }
            var dt = m.DeclaringType;
            Type[] ta = dt != null && dt.IsGenericType ? dt.GetGenericArguments() : null, ma = m.IsGenericMethod ? m.GetGenericArguments() : null;
            for (int i = 0; i < il.Length;)
            {
                short code = il[i++];
                if (code == 0xFE && i < il.Length) code = unchecked((short)(0xFE00 | il[i++]));
                OpCode op;
                if (!_ops.TryGetValue(code, out op)) return;
                var ot = op.OperandType;
                int size = ot == OperandType.InlineNone ? 0 : ot == OperandType.ShortInlineBrTarget || ot == OperandType.ShortInlineI || ot == OperandType.ShortInlineVar ? 1
                    : ot == OperandType.InlineVar ? 2 : ot == OperandType.InlineI8 || ot == OperandType.InlineR ? 8 : 4;
                if (i + size > il.Length) return;
                MemberInfo mi = null;
                if (ot == OperandType.InlineField || ot == OperandType.InlineMethod)
                {
                    int tok = BitConverter.ToInt32(il, i);
                    try { mi = ot == OperandType.InlineField ? (MemberInfo)m.Module.ResolveField(tok, ta, ma) : m.Module.ResolveMethod(tok, ta, ma); } catch { }
                }
                if (ot == OperandType.InlineSwitch) size += 4 * BitConverter.ToInt32(il, i);
                i += size;
                visit(op, mi);
            }
        }

        static void Push(List<KeyValuePair<string, Type>> recent, string key, Type t)
        {
            recent.Add(new KeyValuePair<string, Type>(key, t));
            if (recent.Count > 6) recent.RemoveAt(0);
        }

        /// <summary>The most recently loaded field (or getter result) an instance method is called on; the call consumes it.</summary>
        static string Receiver(List<KeyValuePair<string, Type>> recent, Type dt)
        {
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var ft = recent[i].Value;
                if (ft == null || !(dt.IsAssignableFrom(ft) || (dt.IsGenericType && ft.IsGenericType && dt.GetGenericTypeDefinition() == ft.GetGenericTypeDefinition()))) continue;
                string k = recent[i].Key;
                recent.RemoveAt(i);
                return k;
            }
            return null;
        }

        static void ScanMethod(Type t, MethodBase m)
        {
            string key = MKey(t, m.Name);
            Add(TIdx, TypeOf(key), key);
            var recent = new List<KeyValuePair<string, Type>>();
            string enumerated = null;
            Il(m, (op, mi) =>
            {
                var f = mi as FieldInfo;
                if (f != null)
                {
                    bool store = op.Value == OpCodes.Stfld.Value || op.Value == OpCodes.Stsfld.Value;
                    if (store && enumerated != null && f.Name.StartsWith("<>7__wrap", StringComparison.Ordinal)) Add(YIdx, key, enumerated);
                    enumerated = null;
                    if (!UserField(f)) return;
                    string fk = FKey(f);
                    if (store) Add(WIdx, key, fk);
                    else { Add(RIdx, key, fk); Push(recent, fk, f.FieldType); }
                    return;
                }
                var c = mi as MethodBase;
                if (c == null || c.DeclaringType == null)
                {
                    if (op.Name.StartsWith("stelem", StringComparison.Ordinal))
                        for (int i = recent.Count - 1; i >= 0; i--) if (recent[i].Value != null && recent[i].Value.IsArray) { Add(WIdx, key, recent[i].Key); break; }
                    return;
                }
                var dt = c.DeclaringType;
                if (op.Value == OpCodes.Newobj.Value) Add(CIdx, key, "new:" + dt.Name);
                if (User(dt.Assembly))
                {
                    string ck = MKey(dt, c.Name);
                    if (ck != key) Add(CIdx, key, ck);
                    var info = c as MethodInfo;
                    if (info != null && c.Name.StartsWith("get_", StringComparison.Ordinal)) { Add(RIdx, key, "()" + ck); Push(recent, "()" + ck, info.ReturnType); }
                    return;
                }
                Add(CIdx, key, dt.Name + "." + c.Name);
                if (c.IsStatic) return;
                var rcv = Receiver(recent, dt);
                if (c.Name == "GetEnumerator") enumerated = rcv;
                else if (rcv != null && Mutators.Contains(c.Name)) Add(WIdx, key, rcv);
            });
        }

        /// <summary>A field, or the single field a game getter returns ("()Type.get_X").</summary>
        static string Res(string x)
        {
            if (!x.StartsWith("()", StringComparison.Ordinal)) return x;
            string found = null;
            foreach (var r in Get(RIdx, x.Substring(2)))
            {
                if (r.StartsWith("()", StringComparison.Ordinal)) continue;
                if (found != null) return null;
                found = r;
            }
            return found;
        }

        /// <summary>Field -> methods that write it directly or call a game method that does (value = that callee).</summary>
        static Dictionary<string, List<KeyValuePair<string, string>>> WritersMap()
        {
            if (_writers != null) return _writers;
            var direct = Idx();
            foreach (var kv in WIdx) if (!Ctor(kv.Key)) foreach (var w in kv.Value) { var f = Res(w); if (f != null) Add(direct, f, kv.Key); }
            var callers = Idx();
            foreach (var kv in CIdx) foreach (var c in kv.Value) Add(callers, c, kv.Key);
            _writers = new Dictionary<string, List<KeyValuePair<string, string>>>();
            foreach (var kv in direct)
            {
                var l = new List<KeyValuePair<string, string>>();
                foreach (var m in kv.Value) l.Add(new KeyValuePair<string, string>(m, null));
                foreach (var m in kv.Value)
                    foreach (var caller in Get(callers, m))
                        if (!kv.Value.Contains(caller) && !Ctor(caller) && !l.Exists(x => x.Key == caller)) l.Add(new KeyValuePair<string, string>(caller, m));
                _writers[kv.Key] = l;
            }
            return _writers;
        }

        static List<KeyValuePair<string, string>> Writers(string field)
        {
            List<KeyValuePair<string, string>> l;
            return WritersMap().TryGetValue(field, out l) ? l : new List<KeyValuePair<string, string>>();
        }

        static List<string> Readers(string field)
        {
            var l = new List<string>();
            foreach (var kv in RIdx) foreach (var x in kv.Value) if (Res(x) == field) { l.Add(kv.Key); break; }
            return l;
        }

        static string W(KeyValuePair<string, string> w) { return w.Key + (w.Value != null ? " (via " + w.Value + ")" : ""); }

        static int TypeCount(List<KeyValuePair<string, string>> ws)
        {
            var s = new HashSet<string>();
            foreach (var w in ws) s.Add(TypeOf(w.Key));
            return s.Count;
        }

        static JArray Arr(IEnumerable<string> items, int max)
        {
            var a = new JArray();
            foreach (var x in items) { if (a.Count >= max) break; a.Add(x); }
            return a;
        }

        /// <summary>Fields the failing method reads and every other method that writes them: what breaks in combination.</summary>
        static void Explain(Finding f, string key)
        {
            Scan();
            Inv(f, TypeOf(key), "failing method " + key);
            var fields = new List<string>();
            foreach (var x in Get(YIdx, key)) { var r = Res(x); if (r != null && !fields.Contains(r)) fields.Add(r); }
            int yields = fields.Count;
            foreach (var x in Get(RIdx, key)) { var r = Res(x); if (r != null && !fields.Contains(r)) fields.Add(r); }
            var rows = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();
            for (int i = 0; i < fields.Count; i++)
            {
                var ws = Writers(fields[i]);
                if (!ws.Exists(w => w.Key != key && w.Value != key)) continue;
                rows.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(fields[i], ws));
            }
            Func<KeyValuePair<string, List<KeyValuePair<string, string>>>, int> score = r => TypeCount(r.Value) + (fields.IndexOf(r.Key) < yields ? 100 : 0);
            rows.Sort((a, b) => score(b).CompareTo(score(a)));
            var shared = (JArray)f.J["shared"];
            for (int i = 0; i < rows.Count && i < 8; i++)
            {
                string fld = rows[i].Key;
                shared.Add(new JObject { ["field"] = fld, ["readers"] = Arr(Readers(fld), 10), ["writers"] = Arr(rows[i].Value.ConvertAll(W), 10), ["acrossYield"] = fields.IndexOf(fld) < yields });
                foreach (var w in rows[i].Value) Inv(f, TypeOf(w.Key), "writes " + fld + " in " + MethodOf(w.Key) + (w.Value != null ? " (via " + w.Value + ")" : ""));
            }
            for (int i = Actions.Count - 1, n = 0; i >= 0 && n < 3; i--)
                if (Actions[i].Target != null) { Inv(f, Actions[i].Target, "target of " + Actions[i].Tool + " before this"); n++; }
        }

        // ---------------- click handlers ----------------

        static readonly Dictionary<Type, List<FieldInfo>> EventFieldCache = new Dictionary<Type, List<FieldInfo>>();
        static readonly Dictionary<Type, FieldInfo> DelegateFieldCache = new Dictionary<Type, FieldInfo>();
        static readonly FieldInfo CallsField = typeof(UnityEventBase).GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic);

        static List<FieldInfo> EventFields(Type t)
        {
            List<FieldInfo> l;
            if (EventFieldCache.TryGetValue(t, out l)) return l;
            l = new List<FieldInfo>();
            for (var x = t; x != null && x != typeof(MonoBehaviour) && x != typeof(Component); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) l.Add(f);
            return EventFieldCache[t] = l;
        }

        static FieldInfo FieldOfType(Type t, Type want, string name)
        {
            FieldInfo found;
            if (name == null && DelegateFieldCache.TryGetValue(t, out found)) return found;
            found = null;
            for (var x = t; x != null && found == null; x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (name != null ? f.Name == name : want.IsAssignableFrom(f.FieldType)) { found = f; break; }
            if (name == null) DelegateFieldCache[t] = found;
            return found;
        }

        /// <summary>The game method a listener really calls: a lambda resolves to the first game method it invokes.</summary>
        static string HandlerKey(MethodInfo m)
        {
            if (m.DeclaringType == null) return null;
            if (!m.Name.StartsWith("<", StringComparison.Ordinal)) return MKey(m.DeclaringType, m.Name);
            string found = null;
            Il(m, (op, mi) =>
            {
                var c = mi as MethodBase;
                if (found == null && c != null && c.DeclaringType != null && User(c.DeclaringType.Assembly) && !c.Name.StartsWith("<", StringComparison.Ordinal) && (c.DeclaringType.FullName ?? "").IndexOf('<') < 0)
                    found = MKey(c.DeclaringType, c.Name);
            });
            return found ?? MKey(m.DeclaringType, m.Name);
        }

        static void Handlers(Component c, List<string> into)
        {
            foreach (var f in EventFields(c.GetType()))
            {
                var ev = f.GetValue(c) as UnityEventBase;
                if (ev == null) continue;
                for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                {
                    var t = ev.GetPersistentTarget(i);
                    string m = ev.GetPersistentMethodName(i);
                    if (t != null && !string.IsNullOrEmpty(m)) { var k = MKey(t.GetType(), m); if (!into.Contains(k)) into.Add(k); }
                }
                var calls = CallsField == null ? null : CallsField.GetValue(ev);
                var rf = calls == null ? null : FieldOfType(calls.GetType(), null, "m_RuntimeCalls");
                var list = rf == null ? null : rf.GetValue(calls) as IList;
                if (list == null) continue;
                foreach (var call in list)
                {
                    var df = call == null ? null : FieldOfType(call.GetType(), typeof(Delegate), null);
                    var d = df == null ? null : df.GetValue(call) as Delegate;
                    var k = d == null ? null : HandlerKey(d.Method);
                    if (k != null && !into.Contains(k)) into.Add(k);
                }
            }
        }

        static List<string> HandlersOf(GameObject go)
        {
            var l = new List<string>();
            if (go != null) foreach (var c in go.GetComponents<Component>()) if (c != null) Handlers(c, l);
            return l;
        }

        static List<string> SceneHandlers()
        {
            var l = new List<string>();
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)))
            {
                var mb = o as MonoBehaviour;
                if (mb == null || !mb.gameObject.scene.isLoaded || (mb.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                if (EventFields(mb.GetType()).Count > 0) Handlers(mb, l);
            }
            return l;
        }

        static void InvolveComponents(Finding f, GameObject go, string role)
        {
            if (go == null) return;
            foreach (var c in go.GetComponents<MonoBehaviour>()) if (c != null && User(c.GetType().Assembly)) Inv(f, TKey(c.GetType()), role);
        }

        // ---------------- stuck ----------------

        static string Round(Vector3 v) { return Mathf.RoundToInt(v.x * 10) + "," + Mathf.RoundToInt(v.y * 10) + "," + Mathf.RoundToInt(v.z * 10); }

        /// <summary>Changes whenever the player could see progress: scene, visible UI (texts, values, positions), camera and player position.</summary>
        static int Progress(bool world)
        {
            var sb = new StringBuilder(Scene());
            foreach (var go in Ugui.All()) sb.Append('\n').Append(Ugui.Line(go).Replace(" focused", ""));
            foreach (var e in UiTools.All(true))
            {
                if (UiTools.Interactive(e)) sb.Append('\n').Append(UiTools.Line(e).Replace(" focused", ""));
                else { var t = UiTools.OwnText(e); if (!string.IsNullOrEmpty(t)) sb.Append('\n').Append(t); }
            }
            if (!world) return sb.ToString().GetHashCode() | 1;
            var cam = Camera.main;
            if (cam != null) sb.Append('\n').Append(Round(cam.transform.position));
            GameObject p = null;
            try { p = GameObject.FindWithTag("Player"); } catch { }
            if (p != null) sb.Append('\n').Append(Round(p.transform.position));
            return sb.ToString().GetHashCode() | 1;
        }

        static string Blocker(GameObject target, Vector2 pt, out GameObject hit)
        {
            hit = Ugui.Raycast(pt);
            if (hit == null) return "nothing under the clicked point (" + Mathf.RoundToInt(pt.x) + "," + Mathf.RoundToInt(pt.y) + ") receives uGUI clicks";
            if (hit == target || hit.transform.IsChildOf(target.transform)) { hit = null; return null; }
            var r = Ugui.ScreenRect(hit);
            bool full = r.width * r.height >= 0.8f * Screen.width * Screen.height;
            return (full ? "a full-screen raycast-blocking object " : "another object ") + Ugui.Desc(hit) + " at " + StateTools.PathOf(hit) + " covers the clicked point of " + target.name + ", so the clicks never reach it";
        }

        static void Stuck()
        {
            float now = Time.realtimeSinceStartup;
            if (Actions.Count < 3 || now - Actions[Actions.Count - 1].T < (_run != null ? 0.2f : 1f)) return;
            int cur = Progress(true), ucur = Progress(false);
            var w = new List<Act>();
            for (int i = Actions.Count - 1; i >= 0; i--)
            {
                var a = Actions[i];
                if (now - a.T > _stuckSec || a.T <= _stuckFrom || (a.Target != null ? a.USig != ucur : a.Sig != cur)) break;
                w.Add(a);
            }
            if (w.Count < 3 || !w.Exists(x => x.Target != null)) return;
            _stuckFrom = now;
            var target = _lastTarget;
            var hs = HandlersOf(target);
            GameObject hit = null;
            string block = target != null && _hasPoint ? Blocker(target, _lastPoint, out hit) : null;
            var dead = block != null ? w.FindLast(a => a.Go != null && a.Go != target && HandlersOf(a.Go).Count > 0) : null;
            string key = dead != null ? HandlersOf(dead.Go)[0] : hs.Count > 0 ? hs[0] : null;
            var lines = new List<string>();
            for (int i = w.Count - 1; i >= 0; i--)
            {
                int n = 1;
                while (i > 0 && w[i - 1].Text == w[i].Text) { n++; i--; }
                lines.Add(w[i].Text + (n > 1 ? " x" + n : ""));
            }
            string msg = w.Count + " actions within " + _stuckSec + " s left the screen unchanged: " + string.Join("; ", lines) + ". "
                + (hs.Count > 0 ? "Click handler of the last clicked control: " + string.Join(", ", hs) + ". " : target != null ? "The last clicked control has no click handler. " : "")
                + (block ?? (target != null ? "The clicks reach the control, so its handler runs but changes nothing visible (or throws)." : ""))
                + (dead != null ? " The overlay stays because clicking " + dead.Go.name + " (" + key + ") changed nothing." : "");
            bool fresh;
            var f = Report("stuck", "stuck|" + Scene() + "|" + (key ?? (target != null ? StateTools.PathOf(target) : "")), "critical",
                "Stuck: " + w.Count + " actions changed nothing" + (target != null ? " (last clicked " + target.name + ")" : ""), msg, null, Where(key), target != null ? StateTools.PathOf(target) : null, out fresh);
            if (!fresh) return;
            foreach (var h in hs) Inv(f, TypeOf(h), "click handler " + h);
            for (var t = hit != null ? hit.transform : null; t != null; t = t.parent) InvolveComponents(f, t.gameObject, "on " + StateTools.PathOf(t.gameObject) + ", which covers the clicked point");
            if (key != null) Explain(f, key);
        }

        // ---------------- perf / leaks / NaN / watched fields ----------------

        static readonly float[] Dts = new float[120], Sorted = new float[120];
        static int _dtN, _dtI;
        static bool _softGpu, _softSeen;

        static void Perf()
        {
            float dt = Time.unscaledDeltaTime;
            int fr = Time.frameCount;
            if (Time.realtimeSinceStartup - dt < _quietUntil || fr - _exFrame <= 1) return;
            dt -= fr - 1 == _ownFrame ? _ownSec : fr - 1 == _lastOwnFrame ? _lastOwnSec : 0;
            if (_dtN >= 30)
            {
                Array.Copy(Dts, Sorted, _dtN);
                Array.Sort(Sorted, 0, _dtN);
                float med = Sorted[_dtN / 2];
                for (int i = 0; i < _dtN; i++) Sorted[i] = Mathf.Abs(Dts[i] - med);
                Array.Sort(Sorted, 0, _dtN);
                float mad = Sorted[_dtN / 2];
                bool soft = _softGpu || med > 0.04f;
                _softSeen |= soft;
                if (dt > (soft ? Mathf.Max(0.25f, 5 * med, med + 8 * mad) : Mathf.Max(0.15f, 4 * med, med + 6 * mad))) Spike(dt, med);
                else if (!soft && dt > Mathf.Max(0.05f, 3 * med, med + 6 * mad))
                {
                    float now = Time.realtimeSinceStartup;
                    _mid = now - _midT < 10f ? _mid + 1 : 1;
                    _midT = now;
                    if (_mid >= 3) Spike(dt, med);
                }
            }
            Dts[_dtI] = dt; _dtI = (_dtI + 1) % Dts.Length;
            if (_dtN < Dts.Length) _dtN++;
        }

        /// <summary>Time UDriver's own tools spent on the main thread, so it does not count as a game frame spike.</summary>
        internal static void Own(string tool, float sec)
        {
            if (tool == null || tool == "batch" || tool.StartsWith("qa_", StringComparison.Ordinal) || Array.IndexOf(ActionTools, tool) >= 0) return;
            Own(sec);
        }

        static void Own(float sec)
        {
            int fr = Time.frameCount;
            if (fr != _ownFrame) { _lastOwnFrame = _ownFrame; _lastOwnSec = _ownSec; _ownFrame = fr; _ownSec = 0; }
            _ownSec += sec;
        }

        struct OwnTimer : IDisposable
        {
            float _t0;
            public static OwnTimer Start() { _ownDepth++; return new OwnTimer { _t0 = Time.realtimeSinceStartup }; }
            public void Dispose() { if (--_ownDepth == 0) Own(Time.realtimeSinceStartup - _t0); }
        }

        const string SoftNote = "Frame spike checks were made less strict: the graphics are software-rendered (no graphics card), so frames are slow and uneven anyway.";

        static void Spike(float dt, float med)
        {
            var parts = new List<string>();
            if (_rec != null) for (int i = 0; i < _rec.Length; i++) if (_rec[i].isValid) parts.Add(RecLabels[i] + " " + (_rec[i].elapsedNanoseconds / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            var last = Actions.Count > 0 && Time.realtimeSinceStartup - Actions[Actions.Count - 1].T < 3 ? Actions[Actions.Count - 1] : null;
            var hs = last != null && (last.Tool == "ui_click" || last.Tool == "pointer") ? HandlersOf(_lastTarget) : new List<string>();
            string key = hs.Count > 0 ? hs[0] : null, ms = (dt * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            string msg = "frame took " + ms + " ms (median " + (med * 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms). "
                + (parts.Count > 0 ? "Script time in that frame: " + string.Join(", ", parts) + ". " : "Script timing is not available here (no profiler recorder in this player). ")
                + (key != null ? "It followed the action \"" + last.Text + "\", whose click handler is " + key + "." : "A frame spike has no stack, so the method is not known; profiler_get or trace_add on suspected Update methods narrows it down.");
            bool fresh;
            var f = Report("perf", "perf|" + Scene() + "|" + key, "minor", "Frame spike " + ms + " ms" + (key != null ? " after " + key : " in " + Scene()), msg, null, Where(key), null, out fresh);
            if (f == null) return;
            if (fresh || dt * 1000 > (double)(f.J["ms"] ?? 0)) { f.J["ms"] = Math.Round(dt * 1000); f.J["message"] = msg; }
        }

        static readonly List<Dictionary<string, int>> Samples = new List<Dictionary<string, int>>();
        static readonly List<float> SampleT = new List<float>();
        static readonly List<long> HeapS = new List<long>();

        static int Val(Dictionary<string, int> d, string k) { int v; return d.TryGetValue(k, out v) ? v : 0; }

        static void Leak()
        {
            var d = new Dictionary<string, int>();
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(UObject)))
            {
                if (o == null || (o.hideFlags & HideFlags.DontSave) != 0) continue;
                string n = o.GetType().Name;
                d[n] = Val(d, n) + 1;
            }
            Samples.Add(d); SampleT.Add(Time.realtimeSinceStartup); HeapS.Add(Profiler.GetMonoUsedSizeLong());
            if (Samples.Count > 5) { Samples.RemoveAt(0); SampleT.RemoveAt(0); HeapS.RemoveAt(0); }
            if (Samples.Count < 5) return;
            float secs = SampleT[4] - SampleT[0];
            foreach (var kv in d)
            {
                int up = 0, first = Val(Samples[0], kv.Key);
                bool down = false;
                for (int i = 1; i < 5; i++) { int a = Val(Samples[i - 1], kv.Key), b = Val(Samples[i], kv.Key); if (b < a) down = true; if (b > a) up++; }
                if (!down && up >= 3 && kv.Value - first >= 10) LeakFinding(kv.Key, first, kv.Value, secs);
            }
            bool grows = true;
            for (int i = 1; i < 5; i++) if (HeapS[i] < HeapS[i - 1]) grows = false;
            if (grows && HeapS[4] - HeapS[0] > 32L << 20)
            {
                bool fresh;
                var f = Report("gc", "gc|" + Scene(), "minor", "Managed heap keeps growing in " + Scene(),
                    "managed heap " + (HeapS[0] >> 20) + " MB -> " + (HeapS[4] >> 20) + " MB in " + secs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s without being collected", null, Where(null), null, out fresh);
                if (f != null && !fresh) f.J["message"] = "managed heap " + (HeapS[0] >> 20) + " MB -> " + (HeapS[4] >> 20) + " MB in " + secs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s without being collected";
            }
        }

        static List<string> Creators(string type)
        {
            Scan();
            var marks = new HashSet<string> { "new:" + type };
            if (type == "Material") { marks.Add("Renderer.get_material"); marks.Add("Renderer.get_materials"); }
            if (type == "Mesh") marks.Add("MeshFilter.get_mesh");
            var t = type == "GameObject" ? typeof(GameObject) : StateTools.FindType(type);
            if (t != null && (t == typeof(GameObject) || typeof(Component).IsAssignableFrom(t))) { marks.Add("Object.Instantiate"); marks.Add("GameObject.AddComponent"); marks.Add("new:GameObject"); }
            if (t != null && typeof(ScriptableObject).IsAssignableFrom(t)) { marks.Add("ScriptableObject.CreateInstance"); marks.Add("Object.Instantiate"); }
            var l = new List<string>();
            foreach (var kv in CIdx) if (kv.Value.Overlaps(marks)) l.Add(kv.Key);
            l.Sort((x, y) => (Array.IndexOf(PerFrame, MethodOf(y)) >= 0).CompareTo(Array.IndexOf(PerFrame, MethodOf(x)) >= 0));
            return l;
        }

        static void LeakFinding(string type, int from, int to, float secs)
        {
            string msg = type + " objects: " + from + " -> " + to + " in " + secs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s (+" + ((to - from) / Mathf.Max(1, secs)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "/s) and still growing";
            bool fresh;
            var f = BySig.ContainsKey(Hash("leak|" + type)) ? Report("leak", "leak|" + type, "major", "", "", null, null, null, out fresh) : null;
            if (f != null) { f.J["message"] = msg + f.J["created"]; return; }
            var cr = Creators(type);
            string key = cr.Count > 0 ? cr[0] : null, created = key != null ? "; created in " + string.Join(", ", cr.GetRange(0, Math.Min(5, cr.Count))) : "; no game method creates it directly (new/Instantiate)";
            f = Report("leak", "leak|" + type, "major", "Leak: " + type + " count keeps growing" + (key != null ? " (" + key + ")" : ""), msg + created, null, Where(key), null, out fresh);
            if (f == null) return;
            f.J["created"] = created;
            foreach (var c in cr.GetRange(0, Math.Min(10, cr.Count))) Inv(f, TypeOf(c), "creates " + type + " in " + MethodOf(c));
        }

        static readonly Dictionary<Type, FieldInfo[]> FloatFields = new Dictionary<Type, FieldInfo[]>();

        static FieldInfo[] Floats(Type t)
        {
            FieldInfo[] r;
            if (FloatFields.TryGetValue(t, out r)) return r;
            var l = new List<FieldInfo>();
            for (var x = t; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = f.FieldType;
                    if (ft == typeof(float) || ft == typeof(double) || ft == typeof(Vector2) || ft == typeof(Vector3) || ft == typeof(Vector4) || ft == typeof(Quaternion)) l.Add(f);
                }
            return FloatFields[t] = l.ToArray();
        }

        static bool Bad(float x, bool inf) { return float.IsNaN(x) || (inf && float.IsInfinity(x)); }

        static bool Bad(object v, bool inf)
        {
            if (v is float x) return Bad(x, inf);
            if (v is double d) return double.IsNaN(d) || (inf && double.IsInfinity(d));
            if (v is Vector2 a) return Bad(a.x, inf) || Bad(a.y, inf);
            if (v is Vector3 b) return Bad(b.x, inf) || Bad(b.y, inf) || Bad(b.z, inf);
            if (v is Vector4 c) return Bad(c.x, inf) || Bad(c.y, inf) || Bad(c.z, inf) || Bad(c.w, inf);
            if (v is Quaternion q) return Bad(q.x, inf) || Bad(q.y, inf) || Bad(q.z, inf) || Bad(q.w, inf);
            return false;
        }

        static List<string> PositionWriters(GameObject go, bool parents)
        {
            Scan();
            var l = new List<string>();
            for (var t = go.transform; t != null; t = parents ? t.parent : null)
                foreach (var c in t.GetComponents<MonoBehaviour>())
                {
                    if (c == null || !User(c.GetType().Assembly)) continue;
                    foreach (var m in Get(TIdx, TKey(c.GetType()))) if (!l.Contains(m) && Get(CIdx, m).Overlaps(PosSetters)) l.Add(m);
                }
            return l;
        }

        static void Nan()
        {
            Scan();
            var seen = new HashSet<GameObject>();
            foreach (var mb in Util.FindAll<MonoBehaviour>())
            {
                if (mb == null || !mb.isActiveAndEnabled || !User(mb.GetType().Assembly)) continue;
                foreach (var f in Floats(mb.GetType()))
                {
                    object v;
                    try { v = f.GetValue(mb); } catch { continue; }
                    if (!Bad(v, f.FieldType != typeof(float) && f.FieldType != typeof(double))) continue;
                    var ws = Writers(FKey(f)).ConvertAll(x => x.Key);
                    NanFinding(mb, FKey(f), f.Name + " = " + v, ws);
                }
                if (seen.Add(mb.gameObject) && Bad(mb.transform.position, true))
                    NanFinding(mb, TKey(mb.GetType()) + ".transform.position", "transform.position = " + mb.transform.position, PositionWriters(mb.gameObject, false));
            }
        }

        static void NanFinding(MonoBehaviour mb, string what, string value, List<string> writers)
        {
            string type = TKey(mb.GetType());
            Func<string, int> rank = x => (TypeOf(x) == type ? 2 : 0) + (Array.IndexOf(PerFrame, MethodOf(x)) >= 0 ? 1 : 0);
            writers.Sort((x, y) => rank(y).CompareTo(rank(x)));
            string own = writers.Count > 0 && TypeOf(writers[0]) == type ? writers[0] : null;
            bool fresh;
            var f = Report("nan", "nan|" + what, "major", "NaN/Infinity in " + what, value + " on " + StateTools.PathOf(mb.gameObject)
                + (writers.Count > 0 ? "; written by " + string.Join(", ", writers.GetRange(0, Math.Min(6, writers.Count))) : ""), null,
                own != null ? Where(own) : Where(type + ".", null), StateTools.PathOf(mb.gameObject), out fresh);
            if (!fresh) return;
            Inv(f, type, "holds " + what);
            foreach (var w in writers) Inv(f, TypeOf(w), "writes " + what + " in " + MethodOf(w));
        }

        static readonly Dictionary<string, bool> FieldOk = new Dictionary<string, bool>();

        static void Fields()
        {
            foreach (var p in _fields)
            {
                object v = null;
                string err = null;
                try { v = StateTools.Resolve(p); } catch (Exception e) { err = e.Message; }
                bool isNull = err != null || v == null || (v is UObject o && o == null), nan = !isNull && Bad(v, true), was;
                if (!FieldOk.TryGetValue(p, out was)) was = true;
                FieldOk[p] = !isNull && !nan;
                if (!was || FieldOk[p]) continue;
                string kind = nan ? "nan" : "null", name = p.Substring(p.LastIndexOfAny(new[] { '.', '/' }) + 1);
                var ws = new List<KeyValuePair<string, string>>();
                foreach (var kv in WritersMap()) if (kv.Key.EndsWith("." + name, StringComparison.Ordinal)) ws.AddRange(kv.Value);
                bool fresh;
                var f = Report(kind, kind + "|" + p, "major", p + (nan ? " became NaN/Infinity" : " became null or destroyed"), p + " = " + (err ?? StateTools.Short(v))
                    + (ws.Count > 0 ? "; written by " + string.Join(", ", ws.ConvertAll(W).GetRange(0, Math.Min(6, ws.Count))) : ""), null, Where(ws.Count > 0 ? ws[0].Key : null), p, out fresh);
                if (fresh) foreach (var w in ws) Inv(f, TypeOf(w.Key), "writes " + name + " in " + MethodOf(w.Key));
            }
        }

        // ---------------- visual ----------------

        static float Visible(Rect r)
        {
            float a = r.width * r.height;
            if (a < 1) return 1;
            float w = Mathf.Min(r.xMax, Screen.width) - Mathf.Max(r.xMin, 0), h = Mathf.Min(r.yMax, Screen.height) - Mathf.Max(r.yMin, 0);
            return w <= 0 || h <= 0 ? 0 : w * h / a;
        }

        static bool Scrolled(GameObject go)
        {
#if UDRV_UGUI
            return go.GetComponentInParent<UnityEngine.UI.ScrollRect>() != null;
#else
            return false;
#endif
        }

        static List<Off> OffScreen()
        {
            var l = new List<Off>();
            foreach (var go in Ugui.All())
            {
                if (!Ugui.Interactive(go) || Scrolled(go)) continue;
                var r = Ugui.ScreenRect(go);
                float v = Visible(r);
                if (v < 0.5f) l.Add(new Off { Key = StateTools.PathOf(go), Desc = Ugui.Desc(go), Go = go, R = r, Frac = v });
            }
            foreach (var e in UiTools.All(true))
            {
                if (!UiTools.Interactive(e)) continue;
                bool inScroll = false;
                for (var p = e.hierarchy.parent; p != null && !inScroll; p = p.hierarchy.parent) inScroll = p.GetType().Name == "ScrollView";
                if (inScroll) continue;
                var r = UiTools.ScreenRect(e);
                float v = Visible(r);
                if (v < 0.5f) l.Add(new Off { Key = "uitk:" + UiTools.Desc(e), Desc = UiTools.Desc(e), R = r, Frac = v });
            }
            return l;
        }

        static string RectStr(Rect r) { return "[" + Mathf.RoundToInt(r.x) + "," + Mathf.RoundToInt(r.y) + "," + Mathf.RoundToInt(r.width) + "," + Mathf.RoundToInt(r.height) + "]"; }

        static string OffFinding(Off o)
        {
            var ms = o.Go != null ? PositionWriters(o.Go, true) : new List<string>();
            string key = ms.Count > 0 ? ms[0] : null;
            string msg = o.Desc + " at " + RectStr(o.R) + " is " + Mathf.RoundToInt(o.Frac * 100) + "% inside the " + Screen.width + "x" + Screen.height + " screen, so it cannot be clicked"
                + (ms.Count > 0 ? "; its position is set in " + string.Join(", ", ms.GetRange(0, Math.Min(6, ms.Count))) : "");
            bool fresh;
            var f = Report("visual", "visual|offscreen|" + o.Key, "major", "Control off screen: " + (o.Go != null ? o.Go.name : o.Desc) + (key != null ? " (" + key + ")" : ""), msg, null, Where(key), o.Key, out fresh);
            if (fresh) foreach (var m in ms) Inv(f, TypeOf(m), "sets the position in " + MethodOf(m));
            return msg;
        }

        static List<KeyValuePair<GameObject, string>> BrokenRenderers()
        {
            var l = new List<KeyValuePair<GameObject, string>>();
            foreach (var r in Util.FindAll<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                string why = r.sharedMaterials.Length == 0 ? "has no material" : null;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) why = "has an empty material slot (renders magenta or not at all)";
                    else if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader" || !m.shader.isSupported) why = "uses material " + m.name + " whose shader " + (m.shader != null ? m.shader.name : "is missing") + " renders as the magenta error shader";
                    if (why != null) break;
                }
                if (why != null) l.Add(new KeyValuePair<GameObject, string>(r.gameObject, r.GetType().Name + " " + why));
            }
            return l;
        }

        static List<KeyValuePair<GameObject, string>> BrokenImages()
        {
            var l = new List<KeyValuePair<GameObject, string>>();
#if UDRV_UGUI
            foreach (var img in Util.FindAll<UnityEngine.UI.Image>())
            {
                if (!img.isActiveAndEnabled || img.color.a <= 0.01f) continue;
                if (!ReferenceEquals(img.sprite, null) && img.sprite == null) l.Add(new KeyValuePair<GameObject, string>(img.gameObject, "Image references a sprite that no longer exists"));
                else if (img.sprite == null && img.type != UnityEngine.UI.Image.Type.Simple) l.Add(new KeyValuePair<GameObject, string>(img.gameObject, "Image is " + img.type + " but has no sprite, so it renders as a plain rectangle"));
            }
            foreach (var raw in Util.FindAll<UnityEngine.UI.RawImage>())
                if (raw.isActiveAndEnabled && raw.color.a > 0.01f && raw.texture == null) l.Add(new KeyValuePair<GameObject, string>(raw.gameObject, "RawImage has no texture and renders as a plain rectangle"));
#endif
            return l;
        }

        static void Broken(KeyValuePair<GameObject, string> b, string severity)
        {
            string path = StateTools.PathOf(b.Key);
            bool fresh;
            var f = Report("visual", "visual|broken|" + path, severity, Util.OneLine(b.Key.name + ": " + b.Value, 120), path + ": " + b.Value, null, Where(null), path, out fresh);
            if (fresh) InvolveComponents(f, b.Key, "on the broken object");
        }

        static readonly Dictionary<string, int> Strikes = new Dictionary<string, int>();

        static void VisualWatch()
        {
            var now = new HashSet<string>();
            foreach (var o in OffScreen())
            {
                now.Add(o.Key);
                int s = Val(Strikes, o.Key) + 1;
                Strikes[o.Key] = s;
                if (s >= 2) OffFinding(o);
            }
            foreach (var k in new List<string>(Strikes.Keys)) if (!now.Contains(k)) Strikes.Remove(k);
            foreach (var b in BrokenRenderers()) Broken(b, "major");
        }

        static float Magenta(Color32[] px)
        {
            int n = 0;
            foreach (var c in px) if (c.r > 200 && c.g < 70 && c.b > 200) n++;
            return px.Length == 0 ? 0 : n / (float)px.Length;
        }

        static void MagentaCheck(Color32[] px)
        {
            float m = Magenta(px);
            if (m < 0.005f) return;
            bool fresh;
            Report("visual", "visual|magenta|" + Scene(), "major", "Magenta on screen: " + (m * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "% of pixels",
                (m * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "% of the screen is pure magenta, the colour Unity draws for a missing or unsupported shader", null, Where(null), null, out fresh);
        }

        static string Root()
        {
            string data = Application.dataPath;
            if (Application.isEditor) return Path.GetDirectoryName(data);
            return Application.platform == RuntimePlatform.OSXPlayer ? Path.GetDirectoryName(Path.GetDirectoryName(data)) : Path.GetDirectoryName(data);
        }

        static byte[] Png(Color32[] px, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            var png = tex.EncodeToPNG();
            UObject.Destroy(tex);
            return png;
        }

        static IEnumerator Visual(JObject a, ToolResponse r)
        {
            string act = a.Str("action", "check"), name = Regex.Replace(a.Str("name", "main"), @"[^A-Za-z0-9_.-]", "_").Trim('.');
            if (act != "check" && act != "save" && act != "compare") throw new ToolException("action must be check, save or compare");
            if (Application.isBatchMode) throw new ToolException("nothing is rendered in batch mode (-batchmode), so there is no screen to check. Start the game or editor with a window.");
            if (name.Length == 0) name = "main";
            string dir = Path.Combine(Root(), "UDriverReports", "baselines"), file = Path.Combine(dir, name + ".png");
            int w, h;
            if (act == "check")
            {
                Scan();
                var lines = new List<string>();
                foreach (var o in OffScreen()) lines.Add("off screen: " + OffFinding(o));
                foreach (var b in BrokenRenderers()) { Broken(b, "major"); lines.Add("material: " + StateTools.PathOf(b.Key) + " " + b.Value); }
                foreach (var b in BrokenImages()) { Broken(b, "minor"); lines.Add("image: " + StateTools.PathOf(b.Key) + " " + b.Value); }
                yield return new WaitForEndOfFrame();
                using var ownCheck = OwnTimer.Start();
                var shot = CaptureTools.Grab(640, out w, out h);
                float m = Magenta(shot);
                MagentaCheck(shot);
                r.Text(lines.Count == 0 ? "no off-screen controls, broken materials or empty images found" : lines.Count + " visual problem(s):");
                foreach (var l in lines) r.Text("  " + l);
                r.Text("magenta pixels: " + (m * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" + (m >= 0.005f ? " - a missing or unsupported shader is visible" : ""));
                yield break;
            }
            Texture2D bt = null;
            if (act == "compare")
            {
                byte[] saved = null;
                Sandbox.Own(() => { if (File.Exists(file)) saved = File.ReadAllBytes(file); });
                if (saved == null) throw new ToolException("no baseline '" + name + "' in " + dir + " - save one first with action=save");
                bt = new Texture2D(2, 2);
                bt.LoadImage(saved);
            }
            yield return new WaitForEndOfFrame();
            using var own = OwnTimer.Start();
            var px = CaptureTools.Grab(bt != null ? bt.width : 1024, out w, out h);
            if (bt == null)
            {
                var png = Png(px, w, h);
                Sandbox.Own(() => { Directory.CreateDirectory(dir); File.WriteAllBytes(file, png); });
                r.Text("saved baseline '" + name + "' (" + w + "x" + h + ") to " + file);
                yield break;
            }
            try
            {
                if (bt.width != w || bt.height != h)
                    throw new ToolException("the screen is " + w + "x" + h + " at the baseline width but the baseline is " + bt.width + "x" + bt.height + ": the window size or aspect changed. Save the baseline again.");
                var bp = bt.GetPixels32();
                var diff = new Color32[px.Length];
                int changed = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 p = px[i], q = bp[i];
                    if (Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b) > 48) { changed++; diff[i] = new Color32(255, 0, 0, 255); }
                    else diff[i] = new Color32((byte)(p.r / 3), (byte)(p.g / 3), (byte)(p.b / 3), 255);
                }
                float pct = 100f * changed / Mathf.Max(1, px.Length), thr = a.Num("threshold", 1);
                string dfile = Path.Combine(dir, name + ".diff.png");
                var dpng = Png(diff, w, h);
                Sandbox.Own(() => File.WriteAllBytes(dfile, dpng));
                r.Text((pct > thr ? "DIFFERENT" : "SAME") + ": " + pct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "% of pixels changed against baseline '" + name + "' (threshold " + thr + "%). Diff image, changed pixels in red: " + dfile);
                r.Image(CaptureTools.Encode(diff, w, h, 75), "image/jpeg");
                bool fresh;
                if (pct > thr) Report("visual", "visual|baseline|" + name, "minor", "Screen differs from baseline " + name + " by " + pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%", pct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "% of pixels changed; diff image " + dfile, null, Where(null), null, out fresh);
            }
            finally { UObject.Destroy(bt); }
        }

        // ---------------- tools: watch / findings / risk ----------------

        static void StartWatch(JObject a)
        {
            _watch = true;
            _watchStart = _stuckFrom = Time.realtimeSinceStartup;
            _stuckSec = Mathf.Clamp(a.Num("stuckSec", 10), 2, 600);
            _fields = a.Strs("fields") ?? new string[0];
            _perf = a.Bool("perf", true); _nan = a.Bool("nan", true); _leak = a.Bool("leak", true); _visual = a.Bool("visual", true);
            _dtN = 0;
            _softSeen = false;
            _softGpu = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || Regex.IsMatch(SystemInfo.graphicsDeviceName ?? "", "llvmpipe|softpipe|swiftshader|basic render", RegexOptions.IgnoreCase);
            Samples.Clear(); SampleT.Clear(); HeapS.Clear(); Strikes.Clear(); FieldOk.Clear();
            if (_perf && _rec == null)
            {
                _rec = new Recorder[RecNames.Length];
                for (int i = 0; i < RecNames.Length; i++) { _rec[i] = Recorder.Get(RecNames[i]); if (_rec[i].isValid) _rec[i].enabled = true; }
            }
        }

        static string Flags()
        {
            var l = new List<string> { "exceptions" };
            if (_perf) l.Add("perf"); if (_nan) l.Add("nan"); if (_leak) l.Add("leak"); if (_visual) l.Add("visual");
            l.Add("stuck (" + _stuckSec + " s)");
            if (_fields.Length > 0) l.Add("fields " + string.Join(", ", _fields));
            return string.Join(", ", l);
        }

        static void Watch(JObject a, ToolResponse r)
        {
            string act = a.Str("action", "status");
            if (act == "start") { StartWatch(a); r.Text("watching: " + Flags() + ". Findings so far: " + Findings.Count + " (qa_findings)."); return; }
            if (act == "stop") { _watch = false; r.Text("stopped watching after " + (Time.realtimeSinceStartup - _watchStart).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s; exceptions, errors and asserts are still collected. Findings: " + Findings.Count + "."); return; }
            if (act != "status") throw new ToolException("action must be start, stop or status");
            r.Text(_watch ? "watching for " + (Time.realtimeSinceStartup - _watchStart).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s: " + Flags() : "not watching: exceptions, errors and asserts are collected; action=start adds the rest");
            var kinds = new SortedDictionary<string, int>();
            foreach (var f in Findings) { string k = (string)f.J["kind"]; kinds[k] = (kinds.ContainsKey(k) ? kinds[k] : 0) + 1; }
            var parts = new List<string>();
            foreach (var kv in kinds) parts.Add(kv.Key + " " + kv.Value);
            r.Text("findings: " + Findings.Count + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "") + (_dropped > 0 ? ", " + _dropped + " more dropped at the cap of " + MaxFindings : "")
                   + " | in-game actions logged: " + Actions.Count + " | code index: " + Scanned.Count + " game assemblies, " + WIdx.Count + " writing methods");
            if (_softSeen) r.Text(SoftNote);
            foreach (var p in _fields) { bool ok; r.Text("  " + p + ": " + (FieldOk.TryGetValue(p, out ok) ? (ok ? "ok" : "BAD") : "not checked yet")); }
        }

        static Finding Need(int id)
        {
            foreach (var f in Findings) if (f.Id == id) return f;
            throw new ToolException("no finding #" + id + " (qa_findings lists them)");
        }

        static string Loc(JToken where)
        {
            if (where == null || where.Type != JTokenType.Object) return "";
            string type = (string)where["type"], file = (string)where["file"];
            return (string.IsNullOrEmpty(type) ? "" : type + "." + where["method"]) + (string.IsNullOrEmpty(file) ? "" : "  " + file + ":" + where["line"]);
        }

        static string Line(Finding f)
        {
            var j = f.J;
            int n = (int)j["count"];
            return "#" + f.Id + " " + j["severity"] + " " + j["kind"] + (n > 1 ? " x" + n : "") + "  " + j["title"] + "  " + Loc(j["where"]);
        }

        static void Full(Finding f, ToolResponse r)
        {
            var j = f.J;
            r.Text(Line(f));
            r.Text("scene " + j["scene"] + ", first " + j["firstTime"] + " s, last " + j["lastTime"] + " s" + (j["object"].Type == JTokenType.Null ? "" : ", object " + j["object"]));
            r.Text("message: " + Util.Cut((string)j["message"], 1500));
            foreach (var s in (JArray)j["shared"]) r.Text("shared: " + s["field"] + "  read by " + string.Join(", ", s["readers"]) + "  written by " + string.Join(", ", s["writers"]));
            foreach (var i in (JArray)j["involved"]) r.Text("involved: " + i["type"] + " - " + i["role"]);
            foreach (var x in (JArray)j["actions"]) r.Text("action " + x["t"] + " s: " + x["text"]);
            foreach (var e in (JArray)j["events"]) r.Text("log: " + e);
            if (j["run"] != null) r.Text("run: " + j["run"].ToString(Formatting.None));
            if (j["stack"].Type != JTokenType.Null) r.Text("stack:\n" + Util.Cut((string)j["stack"], 1500));
            r.Text(f.Jpg != null ? "screenshot of the first occurrence: qa_findings id=" + f.Id + " image=true" : "no screenshot");
        }

        static void FindingsTool(JObject a, ToolResponse r)
        {
            int since = a.Int("since", 0);
            var list = new List<Finding>();
            if (a.Has("id")) list.Add(Need(a.Int("id")));
            else foreach (var f in Findings) if (f.Id > since) list.Add(f);
            string format = a.Str("format", "text");
            if (format != "text" && format != "json") throw new ToolException("format must be text or json");
            if (format == "json")
            {
                var arr = new JArray();
                foreach (var f in list) arr.Add(f.J.DeepClone());
                r.Text(new JObject { ["instance"] = new JObject { ["product"] = Application.productName, ["pid"] = _pid, ["port"] = Driver.Port, ["perfNote"] = _softSeen ? SoftNote : null }, ["dropped"] = _dropped, ["findings"] = arr }.ToString(Formatting.None));
            }
            else if (a.Has("id")) Full(list[0], r);
            else
            {
                if (list.Count == 0) r.Text("no findings" + (since > 0 ? " after #" + since : "") + (_watch ? "" : " (until qa_watch action=start only exceptions, errors and asserts are collected)"));
                foreach (var f in list) r.Text(Line(f));
                if (_dropped > 0) r.Text(_dropped + " more were dropped at the cap of " + MaxFindings + " findings");
                if (_softSeen) r.Text(SoftNote);
            }
            if (a.Bool("image") && a.Has("id"))
            {
                if (list[0].Jpg != null) r.Image(list[0].Jpg, "image/jpeg");
                else r.Text("no screenshot for #" + list[0].Id + " (only the first " + MaxImages + " findings get one, and none in batch mode)");
            }
            if (a.Bool("clear")) { Findings.Clear(); BySig.Clear(); NeedImage.Clear(); _images = 0; _dropped = 0; r.Text("findings cleared"); }
        }

        static void Risk(JObject a, ToolResponse r)
        {
            Scan();
            var ws = WritersMap();
            var score = new Dictionary<string, int>();
            var why = new Dictionary<string, List<string>>();
            Action<string, int, string> add = (type, pts, reason) =>
            {
                score[type] = Val(score, type) + pts;
                List<string> l;
                if (!why.TryGetValue(type, out l)) why[type] = l = new List<string>();
                if (!l.Contains(reason)) l.Add(reason);
            };
            var byType = new Dictionary<string, int>();
            foreach (var f in Findings) { string t = (string)f.J["where"]?["type"]; if (!string.IsNullOrEmpty(t)) byType[t] = Val(byType, t) + 1; }
            foreach (var kv in byType) add(kv.Key, 5 * kv.Value, kv.Value + " finding(s) so far");
            foreach (var kv in ws)
            {
                int types = TypeCount(kv.Value);
                if (types >= 2) add(TypeOf(kv.Key), 3, kv.Key + " is written by " + types + " types: " + string.Join(", ", kv.Value.ConvertAll(W).GetRange(0, Math.Min(5, kv.Value.Count))));
            }
            foreach (var kv in YIdx)
                foreach (var x in kv.Value)
                {
                    var fld = Res(x);
                    if (fld == null) continue;
                    List<KeyValuePair<string, string>> l;
                    var others = ws.TryGetValue(fld, out l) ? l.FindAll(w => w.Key != kv.Key) : new List<KeyValuePair<string, string>>();
                    add(TypeOf(kv.Key), others.Count > 0 ? 6 : 3, kv.Key + " enumerates " + fld + " across a yield" + (others.Count > 0 ? " while " + string.Join(", ", others.ConvertAll(W).GetRange(0, Math.Min(4, others.Count))) + " can change it" : ""));
                }
            foreach (var kv in TIdx)
                foreach (var m in kv.Value)
                {
                    string mn = MethodOf(m);
                    if (mn != "Start" && mn != "Awake") continue;
                    var deps = new List<string>();
                    foreach (var c in Get(CIdx, m)) if (Lookups.Contains(c)) deps.Add(c);
                    foreach (var x in Get(RIdx, m)) { var fld = Res(x); if (fld != null && TypeOf(fld) != kv.Key && !deps.Contains("reads " + fld)) deps.Add("reads " + fld); }
                    if (deps.Count > 0) add(kv.Key, 1, mn + " depends on other objects being ready: " + string.Join(", ", deps.GetRange(0, Math.Min(4, deps.Count))));
                }
            foreach (var s in Statics)
            {
                List<KeyValuePair<string, string>> l;
                if (ws.TryGetValue(s, out l) && l.Count > 0) add(TypeOf(s), 2, "static mutable " + s + " (written by " + string.Join(", ", l.ConvertAll(W).GetRange(0, Math.Min(3, l.Count))) + ") survives scene loads");
            }
            var scenes = new SortedDictionary<string, HashSet<string>>();
            var placed = new HashSet<string>();
            foreach (var mb in Util.FindAll<MonoBehaviour>())
            {
                if (mb == null || !User(mb.GetType().Assembly)) continue;
                string sn = mb.gameObject.scene.name, t = TKey(mb.GetType());
                if (!scenes.ContainsKey(sn)) scenes[sn] = new HashSet<string>();
                scenes[sn].Add(t); placed.Add(t);
            }
            var rest = new HashSet<string>();
            foreach (var t in score.Keys) if (!placed.Contains(t)) rest.Add(t);
            scenes["(code not on a loaded scene object)"] = rest;
            if (score.Count == 0) { r.Text("no risk signals in " + Scanned.Count + " game assemblies (no shared writers, coroutine enumerations, init dependencies, static state or findings)"); return; }
            foreach (var kv in scenes)
            {
                var ranked = new List<string>();
                foreach (var t in kv.Value) if (score.ContainsKey(t)) ranked.Add(t);
                if (ranked.Count == 0) continue;
                ranked.Sort((x, y) => score[y].CompareTo(score[x]));
                r.Text(kv.Key + ":");
                foreach (var t in ranked.GetRange(0, Math.Min(15, ranked.Count))) r.Text("  " + score[t] + "  " + t + ": " + string.Join("; ", why[t]));
            }
        }

        // ---------------- crawl / monkey / isolate ----------------

        static bool IsInput(GameObject go)
        {
            foreach (var c in go.GetComponents<Component>()) if (c != null && c.GetType().Name.Contains("InputField")) return true;
            return false;
        }

        static bool Lands(GameObject go, Vector2 pt)
        {
            var hit = Ugui.Raycast(pt);
            return hit != null && (hit == go || hit.transform.IsChildOf(go.transform));
        }

        static string Short(string s) { s = FirstLine(s ?? ""); return s.Length > 40 ? s.Substring(0, 40) : s; }

        static List<Ctl> Controls()
        {
            using var own = OwnTimer.Start();
            var l = new List<Ctl>();
            foreach (var go in Ugui.All())
            {
                if (!Ugui.Interactive(go)) continue;
                var rc = Ugui.ScreenRect(go);
                if (rc.width < 1 || rc.height < 1 || Visible(rc) <= 0) continue;
                var pt = Rect.MinMaxRect(Mathf.Max(rc.xMin, 0), Mathf.Max(rc.yMin, 0), Mathf.Min(rc.xMax, Screen.width), Mathf.Min(rc.yMax, Screen.height)).center;
                if (!Lands(go, pt)) continue;
                string text = Short(Ugui.TextOf(go));
                var rep = new JObject { ["name"] = go.name };
                if (text.Length > 0) rep["text"] = text;
                l.Add(new Ctl { Key = StateTools.PathOf(go), Label = Ugui.Desc(go), Name = go.name + " " + text, Click = new JObject { ["id"] = Ugui.Id(go) }, Replay = rep, Go = go, Input = IsInput(go), Pt = pt });
            }
            foreach (var e in UiTools.All(true))
            {
                if (!UiTools.Interactive(e)) continue;
                var rc = UiTools.ScreenRect(e);
                if (rc.width < 1 || rc.height < 1 || Visible(rc) <= 0) continue;
                string text = Short(UiTools.OwnText(e)), name = e.name ?? "";
                bool named = name.Length > 0 && !name.StartsWith("unity-", StringComparison.Ordinal);
                var rep = named ? new JObject { ["name"] = name } : text.Length > 0 ? new JObject { ["text"] = text } : new JObject { ["x"] = Mathf.Round(rc.center.x), ["y"] = Mathf.Round(rc.center.y) };
                l.Add(new Ctl { Key = "uitk:" + name + "|" + e.GetType().Name + "|" + text, Label = UiTools.Desc(e), Name = name + " " + text, Click = new JObject { ["id"] = UiTools.Id(e) }, Replay = rep, Input = e.GetType().Name.Contains("TextField"), Pt = rc.center });
            }
            return l;
        }

        static string ScreenSig(List<Ctl> ctls)
        {
            var keys = ctls.ConvertAll(c => c.Key);
            keys.Sort(StringComparer.Ordinal);
            return Hash(Scene() + "\n" + string.Join("\n", keys));
        }

        static bool Avoid(Run c, string s)
        {
            foreach (var m in c.Avoid) if (m(s)) return true;
            return false;
        }

        static void See(Run c, string sig, List<Ctl> ctls)
        {
            if (!c.Screens.ContainsKey(sig)) { c.Screens[sig] = ctls.ConvertAll(x => x.Key); c.Routes[sig] = new List<JObject>(c.Route); }
            foreach (var x in ctls)
            {
                if (c.Seen.ContainsKey(x.Key)) continue;
                c.Seen[x.Key] = x.Label;
                if (Avoid(c, x.Name) || HandlersOf(x.Go).Exists(h => Avoid(c, h))) c.Avoided.Add(x.Key);
            }
        }

        static int Left(Run c, string sig)
        {
            int n = 0;
            foreach (var k in c.Screens[sig]) if (!c.Clicked.Contains(k) && !c.Avoided.Contains(k)) n++;
            return n;
        }

        static IEnumerator Step(Run c, string tool, JObject args, JObject replay, string text)
        {
            c.Count++;
            if (_run != null) _run["step"] = c.Count;
            var st = new JObject { ["tool"] = tool, ["args"] = (replay ?? args).DeepClone() };
            c.Steps.Add(st);
            RunPath.Add(st);
            if (RunPath.Count > 100) RunPath.RemoveAt(0);
            Log(tool, args, replay ?? args, text);
            var res = new ToolResponse();
            yield return Driver.Guard(() => Driver.Invoke(tool, (JObject)args.DeepClone(), res), res);
            if (res.IsError) { c.Errors++; c.LastError = tool + ": " + Util.OneLine(res.AllText(), 200); }
            float until = Time.realtimeSinceStartup + c.Delay;
            do yield return null; while (Time.realtimeSinceStartup < until);
        }

        static IEnumerator Press(Run c, Ctl x)
        {
            bool lands = x.Go == null || Lands(x.Go, x.Pt);
            foreach (var h in HandlersOf(x.Go)) (lands ? c.Reached : c.Blocked).Add(h);
            var args = (JObject)x.Click.DeepClone();
            var rep = (JObject)x.Replay.DeepClone();
            if (!x.Input) { yield return Step(c, "ui_click", args, rep, x.Label); yield break; }
            rep.Remove("text");
            if (!rep.Has("name")) rep = (JObject)args.DeepClone();
            args["text"] = rep["text"] = "qa test 42";
            args["submit"] = rep["submit"] = true;
            yield return Step(c, "type_text", args, rep, x.Label);
        }

        static IEnumerator Reload()
        {
            var s = SceneManager.GetActiveScene();
            if (s.buildIndex >= 0) SceneManager.LoadScene(s.buildIndex); else SceneManager.LoadScene(s.name);
            RunPath.Clear();
            yield return null;
            yield return null;
            float until = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
            _quietUntil = Time.realtimeSinceStartup + 0.3f;
        }

        static IEnumerator Back(Run c, string sig)
        {
            yield return Step(c, "key", new JObject { ["key"] = "Escape" }, null, "Escape");
            if (ScreenSig(Controls()) != sig) yield break;
            foreach (var x in Controls())
            {
                if (!BackRx.IsMatch(x.Name) || c.Avoided.Contains(x.Key)) continue;
                c.Clicked.Add(x.Key);
                yield return Press(c, x);
                if (ScreenSig(Controls()) != sig) yield break;
            }
            yield return Reload();
        }

        static IEnumerator CrawlLoop(Run c)
        {
            while (c.Left)
            {
                var ctls = Controls();
                string sig = ScreenSig(ctls);
                See(c, sig, ctls);
                var next = ctls.Find(x => !c.Clicked.Contains(x.Key) && !c.Avoided.Contains(x.Key));
                if (next != null)
                {
                    c.Clicked.Add(next.Key);
                    yield return Press(c, next);
                    if (ScreenSig(Controls()) != sig) c.Route.Add(c.Steps[c.Steps.Count - 1]);
                    continue;
                }
                string target = null;
                foreach (var kv in c.Screens) if (kv.Key != sig && !c.Tried.Contains(kv.Key) && Left(c, kv.Key) > 0) { target = kv.Key; break; }
                if (target == null) yield break;
                yield return Back(c, sig);
                string now = ScreenSig(Controls());
                List<JObject> route;
                c.Route = c.Routes.TryGetValue(now, out route) ? new List<JObject>(route) : new List<JObject>();
                if (now != sig && (route == null || Left(c, now) > 0)) continue;
                c.Tried.Add(target);
                yield return Reload();
                foreach (var st in c.Routes[target])
                {
                    if (!c.Left) yield break;
                    yield return Step(c, (string)st["tool"], (JObject)st["args"], null, "replay to reach a screen with unclicked controls");
                }
                c.Route = new List<JObject>(c.Routes[target]);
            }
        }

        static IEnumerator MonkeyLoop(Run c, int seed)
        {
            var rng = new System.Random(seed);
            while (c.Left)
            {
                var ctls = Controls();
                See(c, ScreenSig(ctls), ctls);
                double p = rng.NextDouble();
                if (p < 0.65 && ctls.Count > 0)
                {
                    var fresh = ctls.FindAll(y => !c.Clicked.Contains(y.Key) && !c.Avoided.Contains(y.Key));
                    var x = fresh.Count > 0 && rng.NextDouble() < 0.6 ? fresh[rng.Next(fresh.Count)] : ctls[rng.Next(ctls.Count)];
                    if (c.Avoided.Contains(x.Key)) continue;
                    c.Clicked.Add(x.Key);
                    yield return Press(c, x);
                }
                else if (p < 0.8)
                {
                    var pt = new Vector2(rng.Next(Screen.width), rng.Next(Screen.height));
                    var hit = Ugui.Raycast(pt);
                    if (hit != null && (Avoid(c, hit.name + " " + Ugui.TextOf(hit)) || (hit.transform.parent != null && Avoid(c, hit.transform.parent.name)))) continue;
                    var click = new JObject { ["x"] = pt.x, ["y"] = pt.y };
                    if (hit == null) click["method"] = "input";
                    yield return Step(c, "ui_click", click, null, "click at " + pt.x + "," + pt.y);
                }
                else if (p < 0.92)
                {
                    string k = MonkeyKeys[rng.Next(MonkeyKeys.Length)];
                    yield return Step(c, "key", new JObject { ["key"] = k }, null, "key " + k);
                }
                else
                {
                    var a = new JObject { ["x"] = rng.Next(Screen.width), ["y"] = rng.Next(Screen.height), ["to_x"] = rng.Next(Screen.width), ["to_y"] = rng.Next(Screen.height), ["durationMs"] = 200, ["method"] = "input" };
                    yield return Step(c, "ui_drag", a, null, "drag");
                }
            }
        }

        static IEnumerator Crawl(JObject a, ToolResponse r)
        {
            string mode = a.Str("mode", "crawl");
            if (mode != "crawl" && mode != "monkey") throw new ToolException("mode must be crawl or monkey");
            int seed = a.Int("seed", 1), delay = Mathf.Clamp(a.Int("delayMs", 250), 0, 10000);
            float seconds = Mathf.Clamp(a.Num("seconds", 120), 1, 3600), t0 = Time.realtimeSinceStartup, oldScale = Time.timeScale;
            var c = new Run { Max = Mathf.Clamp(a.Int("steps", 200), 1, 5000), Deadline = t0 + seconds, Delay = delay / 1000f };
            c.Avoid = Array.ConvertAll(a.Strs("avoid") ?? new[] { "quit", "exit", "delete" }, Util.Matcher);
            bool hadWatch = _watch, scaled = a.Has("timeScale");
            string startSig = ScreenSig(Controls());
            if (!_watch) StartWatch(new JObject());
            if (scaled) Time.timeScale = Mathf.Clamp(a.Num("timeScale"), 0, 100);
            int firstId = _nextId;
            _run = new JObject { ["mode"] = mode, ["seed"] = seed, ["delayMs"] = delay, ["timeScale"] = Time.timeScale, ["step"] = 0 };
            RunPath.Clear();
            try { yield return mode == "crawl" ? CrawlLoop(c) : MonkeyLoop(c, seed); }
            finally
            {
                _run = null;
                if (scaled) Time.timeScale = oldScale;
                if (!hadWatch) _watch = false;
            }
            if (Time.timeScale != oldScale || ScreenSig(Controls()) != startSig)
            {
                yield return Reload();
                Time.timeScale = oldScale;
            }
            int clicked = 0;
            var unclicked = new List<string>();
            foreach (var kv in c.Seen) if (c.Clicked.Contains(kv.Key)) clicked++; else if (!c.Avoided.Contains(kv.Key)) unclicked.Add(kv.Value);
            var all = SceneHandlers();
            var notReached = all.FindAll(h => !c.Reached.Contains(h));
            r.Text(mode + ": " + c.Count + " steps in " + (Time.realtimeSinceStartup - t0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s, " + c.Screens.Count + " screens, controls clicked " + clicked + "/" + c.Seen.Count
                   + (c.Avoided.Count > 0 ? " (" + c.Avoided.Count + " avoided)" : "") + (c.Errors > 0 ? ", " + c.Errors + " actions failed (last: " + c.LastError + ")" : ""));
            r.Text("click handlers reached (" + c.Reached.Count + "): " + string.Join(", ", Arr(c.Reached, 30)));
            r.Text("click handlers not reached (" + notReached.Count + "): " + string.Join(", ", Arr(notReached, 30)));
            if (c.Blocked.Count > 0) r.Text("clicked but covered by another object: " + string.Join(", ", Arr(c.Blocked, 20)));
            if (unclicked.Count > 0) r.Text("not clicked (" + unclicked.Count + "): " + string.Join(", ", Arr(unclicked, 20)));
            int fresh = 0;
            foreach (var f in Findings)
            {
                if (f.Id <= firstId) continue;
                fresh++;
                r.Text("NEW " + Line(f));
                var steps = f.J["run"]?["steps"];
                if (steps != null) r.Text("    steps: " + Util.Cut(steps.ToString(Formatting.None), 1500));
            }
            r.Text(fresh == 0 ? "no new findings" : fresh + " new finding(s); details: qa_findings id=N, shrink a repro: qa_isolate id=N");
        }

        static int CountOf(string sig) { Finding f; return BySig.TryGetValue(sig, out f) ? (int)f.J["count"] : 0; }

        static IEnumerator Attempt(Run c, List<JObject> steps, bool reset, string sig, bool[] hit)
        {
            if (reset) yield return Reload();
            int before = CountOf(sig);
            foreach (var s in steps)
            {
                float wait = Time.realtimeSinceStartup + s.Int("waitMs", 0) / 1000f;
                while (Time.realtimeSinceStartup < wait) yield return null;
                yield return Step(c, (string)s["tool"], s["args"] as JObject ?? new JObject(), null, null);
            }
            float until = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < until) yield return null;
            hit[0] = CountOf(sig) > before;
        }

        static IEnumerator Isolate(JObject a, ToolResponse r)
        {
            var f = Need(a.Int("id"));
            var src = a["steps"] as JArray ?? f.J["run"]?["steps"] as JArray ?? (JArray)f.J["actions"];
            var all = new List<JObject>();
            int delay = Mathf.Clamp(a.Int("delayMs", 250), 0, 10000);
            double prev = -1;
            foreach (var s in src)
            {
                var o = s as JObject;
                string tool = o == null ? null : (string)o["tool"];
                if (tool == null || tool == "batch" || tool.StartsWith("qa_", StringComparison.Ordinal)) continue;
                var st = new JObject { ["tool"] = tool, ["args"] = o["args"] ?? new JObject() };
                double t = o["t"] != null ? (double)o["t"] : -1;
                int wait = o["waitMs"] != null ? (int)o["waitMs"] : prev >= 0 && t >= 0 ? Mathf.Clamp((int)((t - prev) * 1000) - delay, 0, 10000) : 0;
                if (wait > 0) st["waitMs"] = wait;
                if (t >= 0) prev = t;
                all.Add(st);
            }
            if (all.Count == 0) throw new ToolException("finding #" + f.Id + " has no recorded in-game steps; pass steps [{tool, args}] (ui_click, key, type_text, ...)");
            string resetMode = a.Str("reset", "scene");
            if (resetMode != "scene" && resetMode != "none") throw new ToolException("reset must be scene or none");
            bool reset = resetMode == "scene";
            int maxRuns = Mathf.Clamp(a.Int("maxRuns", 30), 1, 500), runs = 0;
            var c = new Run { Delay = delay / 1000f };
            bool hadWatch = _watch;
            if (!_watch) StartWatch(new JObject());
            _run = new JObject { ["mode"] = "isolate", ["seed"] = 0, ["delayMs"] = delay, ["timeScale"] = Time.timeScale, ["step"] = 0 };
            RunPath.Clear();
            var hit = new bool[1];
            var cur = all;
            try
            {
                yield return Attempt(c, cur, reset, f.Sig, hit);
                runs++;
                if (!hit[0])
                {
                    r.Text("finding #" + f.Id + " did not reproduce with all " + all.Count + " steps (1 run). It may depend on timing or on state from before these steps: try a larger delayMs, reset=none, or more steps.");
                    yield break;
                }
                int n = 2;
                while (cur.Count >= 2 && runs < maxRuns)
                {
                    var chunks = new List<List<JObject>>();
                    for (int i = 0; i < n; i++) { int from = cur.Count * i / n, to = cur.Count * (i + 1) / n; if (to > from) chunks.Add(cur.GetRange(from, to - from)); }
                    bool found = false;
                    for (int i = 0; i < chunks.Count && runs < maxRuns && !found; i++)
                    {
                        yield return Attempt(c, chunks[i], reset, f.Sig, hit);
                        runs++;
                        if (hit[0]) { cur = chunks[i]; n = 2; found = true; }
                    }
                    for (int i = 0; n > 2 && i < chunks.Count && runs < maxRuns && !found; i++)
                    {
                        var rest = new List<JObject>(cur);
                        rest.RemoveRange(cur.IndexOf(chunks[i][0]), chunks[i].Count);
                        yield return Attempt(c, rest, reset, f.Sig, hit);
                        runs++;
                        if (hit[0]) { cur = rest; n = Math.Max(n - 1, 2); found = true; }
                    }
                    if (found) continue;
                    if (n >= cur.Count) break;
                    n = Math.Min(n * 2, cur.Count);
                }
            }
            finally
            {
                _run = null;
                if (!hadWatch) _watch = false;
            }
            r.Text("minimal reproduction of #" + f.Id + ": " + cur.Count + " of " + all.Count + " steps, " + runs + " runs" + (runs >= maxRuns ? " (stopped at maxRuns; it may shrink further)" : "") + (reset ? ", scene reloaded before each run" : ""));
            for (int i = 0; i < cur.Count; i++) r.Text("  " + (i + 1) + ". " + cur[i]["tool"] + " " + cur[i]["args"].ToString(Formatting.None));
            r.Text("steps: " + new JArray(cur.ConvertAll(x => (JToken)x)).ToString(Formatting.None));
        }
    }
}

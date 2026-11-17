using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UDriver
{
    /// <summary>Error meant to be shown to the agent as-is (no stack trace).</summary>
    public class ToolException : Exception { public ToolException(string m) : base(m) { } }

    public sealed class ToolResponse
    {
        public readonly JArray Content = new JArray();
        public bool IsError;
        readonly StringBuilder _sb = new StringBuilder();

        public void Text(string s) { _sb.AppendLine(s); }
        public void Error(string s) { IsError = true; _sb.AppendLine("ERROR: " + s); }

        public void Image(byte[] data, string mime)
        {
            FlushText();
            Content.Add(new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mime });
        }

        /// <summary>All text written so far (used by self_test / batch to look at a nested tool's output).</summary>
        public string AllText()
        {
            var sb = new StringBuilder();
            foreach (var c in Content) if ((string)c["type"] == "text") sb.AppendLine((string)c["text"]);
            sb.Append(_sb);
            return sb.ToString();
        }

        public int ImageBytes()
        {
            int n = 0;
            foreach (var c in Content) if ((string)c["type"] == "image") n += ((string)c["data"]).Length * 3 / 4;
            return n;
        }

        public void FlushText()
        {
            if (_sb.Length == 0) return;
            Content.Add(new JObject { ["type"] = "text", ["text"] = _sb.ToString().TrimEnd() });
            _sb.Clear();
        }
    }

    public delegate IEnumerator ToolHandler(JObject args, ToolResponse res);

    internal sealed class Job
    {
        public ToolHandler Handler;
        public JObject Args;
        public readonly ToolResponse Res = new ToolResponse();
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>Optional hook for game code: UDriver.QA.Emit("AlgorithmStarted", "id=5").</summary>
    public static class QA
    {
        public static void Emit(string name, string detail = null) { EventStore.Emit("custom", name, detail); }
    }

    public static class Driver
    {
        public const int DefaultPort = 17890;
        public static int Port { get; private set; }
        public static bool Running { get; private set; }

        internal static DriverHost Host;
        internal static int MainThreadId;
        internal static readonly ConcurrentQueue<Job> Jobs = new ConcurrentQueue<Job>();

        static readonly Dictionary<string, ToolHandler> Handlers = new Dictionary<string, ToolHandler>();
        static readonly Dictionary<string, ToolDef> Defs = new Dictionary<string, ToolDef>();
        static string _toolsJson;
        static HttpListener _listener;

        // ---------------- security ----------------

        /// <summary>
        /// False (default): state_set / state_call may only touch game code, UnityEngine and basic value/collection types.
        /// True only when a human started the process with environment variable UDRIVER_UNSAFE=1. It cannot be changed through tools.
        /// </summary>
        public static bool AllowUnsafe { get; private set; }

        static string _token;

        /// <summary>Per-user file through which the MCP server learns the access token of a running game/editor.</summary>
        public static string TokenFile(int port)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver", port + ".token");
        }

        /// <summary>Takes the token from UDRIVER_TOKEN (set by the MCP server when it launches a build) or generates a random one, and publishes it in the token file.</summary>
        public static string CreateToken(int port)
        {
            string t = Environment.GetEnvironmentVariable("UDRIVER_TOKEN");
            if (string.IsNullOrEmpty(t))
            {
                var b = new byte[24];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
                t = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            }
            try
            {
                // On macOS/Linux the folder is made 0700 and the file 0600 before the token is written; the file is then renamed into place.
                string f = TokenFile(port), dir = Path.GetDirectoryName(f), tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
                Directory.CreateDirectory(dir);
                OwnerOnly(dir, true);
                try
                {
                    File.WriteAllText(tmp, "");
                    OwnerOnly(tmp, false);
                    File.WriteAllText(tmp, t);
                    if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
            catch (Exception e) { Debug.LogWarning("[UDriver] could not write the token file (app_connect will not work, app_launch will): " + e.Message); }
            return t;
        }

#if (UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR_WIN
        [DllImport("libc", EntryPoint = "chmod")] static extern int ChmodLibc(string path, int mode);
        [DllImport("libc.so.6", EntryPoint = "chmod")] static extern int ChmodGlibc(string path, int mode);

        /// <summary>chmod 0700 (folder) / 0600 (file). Throws if that cannot be done, so a secret is never written with wider permissions.</summary>
        static void OwnerOnly(string path, bool folder)
        {
            int mode = folder ? 448 : 384;   // 0700 : 0600
            int rc;
            try { rc = ChmodLibc(path, mode); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { rc = ChmodGlibc(path, mode); }
            if (rc != 0) throw new IOException("could not restrict permissions of " + path);
        }
#else
        static void OwnerOnly(string path, bool folder) { }   // Windows: the user profile is already private to the user
#endif

        static bool SameText(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>
        /// Request gate shared by the game driver and the editor bridge. Rejects: other Host values (DNS rebinding),
        /// anything sent by a web page (browsers always attach Origin to cross-site POSTs), and callers without the token.
        /// </summary>
        public static bool Authorize(HttpListenerRequest req, int port, string token, bool needToken, out int status, out string error)
        {
            status = 403;
            string host = req.Headers["Host"] ?? "";
            if (host != "127.0.0.1:" + port && host != "localhost:" + port) { error = "unexpected Host header"; return false; }
            if (!string.IsNullOrEmpty(req.Headers["Origin"])) { error = "requests from web pages are not accepted"; return false; }
            if (needToken && !SameText(req.Headers["X-UDriver-Token"], token)) { status = 401; error = "missing or wrong X-UDriver-Token"; return false; }
            error = null;
            return true;
        }

        public static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            bool force = false;
#if UDRIVER_FORCE
            force = true;
#endif
            // Never active in release players unless explicitly forced.
            if (!(Application.isEditor || Debug.isDebugBuild || force)) return;

            int port = DefaultPort;
            string startup = null;
            var argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == "-udriver-port") int.TryParse(argv[i + 1], out port);
                if (argv[i] == "-udriver-startup") startup = argv[i + 1];
            }
            Start(port);
            if (startup != null) RunStartupFile(startup);
        }

        public static void Start(int port)
        {
            if (Running) return;
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            Port = port;
            AllowUnsafe = Environment.GetEnvironmentVariable("UDRIVER_UNSAFE") == "1";
            _token = CreateToken(port);
            Application.runInBackground = true;

            var go = new GameObject("[UDriver]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Host = go.AddComponent<DriverHost>();

            RegisterAll();

            try
            {
                _listener = new HttpListener();
                // Loopback literal only. A "localhost" prefix can make Mono fall back to listening on ALL interfaces when name resolution fails.
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();
                var t = new Thread(Listen) { IsBackground = true, Name = "UDriver.Http" };
                t.Start();
                Running = true;
                Debug.Log("[UDriver] listening on http://127.0.0.1:" + port + " (" + Handlers.Count + " tools)");
            }
            catch (Exception e)
            {
                Debug.LogError("[UDriver] could not start HTTP listener on port " + port + ": " + e.Message);
            }
        }

        static void RegisterAll()
        {
            Handlers.Clear(); Defs.Clear(); _toolsJson = null;
            foreach (var d in ToolManifest.Tools) Defs[d.Name] = d;
            Register("status", Sync(Status));
            Register("batch", Batch);
            Register("self_test", SelfTest.Run);
            EventStore.Init();
            UiTools.Register();
            InputTools.Register();
            CaptureTools.Register();
            StateTools.Register();
            ProbeTools.Register();
        }

        /// <summary>Runs another tool from inside a tool (same arguments an agent would send).</summary>
        public static IEnumerator Invoke(string tool, JObject args, ToolResponse res)
        {
            ToolHandler h;
            if (!Handlers.TryGetValue(tool, out h)) throw new ToolException("unknown tool '" + tool + "'");
            return h(args ?? new JObject(), res);
        }

        static IEnumerator Batch(JObject a, ToolResponse r)
        {
            var calls = a["calls"] as JArray;
            if (calls == null || calls.Count == 0) throw new ToolException("calls must be a non-empty array of {tool, args}");
            if (calls.Count > 20) throw new ToolException("at most 20 calls per batch");
            bool stop = a.Bool("stopOnError", true);
            for (int i = 0; i < calls.Count; i++)
            {
                string tool = (string)calls[i]["tool"];
                r.Text("--- [" + i + "] " + tool);
                ToolHandler h;
                if (tool == null || tool == "batch" || !Handlers.TryGetValue(tool, out h)) { r.Error("unknown tool '" + tool + "'"); if (stop) yield break; continue; }
                yield return h(calls[i]["args"] as JObject ?? new JObject(), r);
                if (r.IsError && stop) { r.Text("batch stopped at call " + i + " (stopOnError)"); yield break; }
            }
        }

        /// <summary>
        /// Edit-mode execution (called by the editor bridge on the editor main thread, outside Play Mode):
        /// runs a reflection/inspection tool to completion without a running game loop.
        /// </summary>
        public static void RunEditMode(string tool, JObject args, ToolResponse res)
        {
            if (Handlers.Count == 0) { MainThreadId = Thread.CurrentThread.ManagedThreadId; RegisterAll(); }
            if (Array.IndexOf(ToolManifest.EditModeTools, tool) < 0)
                throw new ToolException("'" + tool + "' needs a running game: enter Play Mode (editor_play action=play) or start a build (app_launch), then call it again");
            ToolHandler h;
            if (!Handlers.TryGetValue(tool, out h)) throw new ToolException("unknown tool '" + tool + "'");
            var it = h(args ?? new JObject(), res);
            for (int guard = 0; guard < 10000 && it.MoveNext(); guard++) { }
        }

        public static void Stop()
        {
            Running = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            ProbeTools.RemoveAll();
        }

        /// <summary>Register a handler for a tool declared in ToolManifest.</summary>
        public static void Register(string name, ToolHandler handler)
        {
            if (!Defs.ContainsKey(name)) Defs[name] = new ToolDef { Name = name, Desc = name, Params = new ToolParam[0] };
            Handlers[name] = handler; _toolsJson = null;
        }

        /// <summary>Register a custom project-specific tool.</summary>
        public static void Register(ToolDef def, ToolHandler handler) { Defs[def.Name] = def; Handlers[def.Name] = handler; _toolsJson = null; }

        public static ToolHandler Sync(Action<JObject, ToolResponse> a) { return (args, res) => RunSync(a, args, res); }
        static IEnumerator RunSync(Action<JObject, ToolResponse> a, JObject args, ToolResponse res) { a(args, res); yield break; }

        // ---------------- HTTP ----------------

        static void Listen()
        {
            var l = _listener;
            while (l != null && l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); }
                catch (Exception e)
                {
                    if (!l.IsListening) break;   // stopped or disposed
                    Debug.LogWarning("[UDriver] accepting a request failed: " + e.Message);
                    Thread.Sleep(100);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string body; int status = 200;
            try
            {
                string path = ctx.Request.Url.AbsolutePath, verb = ctx.Request.HttpMethod, deny;
                if (!Authorize(ctx.Request, Port, _token, path != "/ping", out status, out deny))
                    body = new JObject { ["ok"] = false, ["error"] = deny }.ToString(Formatting.None);
                else if (path == "/ping" && verb == "GET") { status = 200; body = "{\"ok\":true}"; }
                else if (path == "/tools" && verb == "GET") { status = 200; body = ToolsJson(); }
                else if (path == "/call" && verb == "POST" && !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                { status = 415; body = "{\"ok\":false,\"error\":\"Content-Type must be application/json\"}"; }
                else if (path == "/call" && verb == "POST")
                {
                    status = 200;
                    string reqBody;
                    using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) reqBody = r.ReadToEnd();
                    var req = JObject.Parse(reqBody);
                    body = Call((string)req["tool"], req["args"] as JObject ?? new JObject()).ToString(Formatting.None);
                }
                else { status = 404; body = "{\"ok\":false,\"error\":\"not found\"}"; }
            }
            catch (Exception e)
            {
                status = 500;
                body = new JObject { ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message }.ToString(Formatting.None);
            }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch { }
        }

        static JObject Call(string tool, JObject args)
        {
            ToolHandler h;
            lock (Handlers) { if (tool == null || !Handlers.TryGetValue(tool, out h)) h = null; }
            if (h == null) return new JObject { ["ok"] = false, ["error"] = "unknown tool '" + tool + "'" };
            var job = new Job { Handler = h, Args = args };
            Jobs.Enqueue(job);
            int timeout = args.Int("timeoutMs", 0) + 120000;
            if (!job.Done.Wait(timeout))
                return new JObject { ["ok"] = false, ["error"] = "timeout: the game's main thread did not finish '" + tool + "' (game frozen, or the tool hung)" };
            job.Res.FlushText();
            return new JObject { ["ok"] = !job.Res.IsError, ["isError"] = job.Res.IsError, ["content"] = job.Res.Content };
        }

        static string ToolsJson()
        {
            lock (Handlers)
            {
                if (_toolsJson != null) return _toolsJson;
                var arr = new JArray();
                foreach (var kv in Handlers)
                {
                    var d = Defs[kv.Key];
                    var props = new JObject(); var req = new JArray();
                    foreach (var p in d.Params)
                    {
                        var o = new JObject { ["description"] = p.Desc };
                        if (p.Type != "any") o["type"] = p.Type;
                        if (p.Type == "array") o["items"] = new JObject();
                        props[p.Name] = o;
                        if (p.Required) req.Add(p.Name);
                    }
                    arr.Add(new JObject
                    {
                        ["name"] = d.Name, ["description"] = d.Desc,
                        ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = req }
                    });
                }
                return _toolsJson = new JObject { ["tools"] = arr }.ToString(Formatting.None);
            }
        }

        // ---------------- main-thread job runner ----------------

        internal static IEnumerator RunJob(Job job)
        {
            // Flattens nested IEnumerators ourselves so that an exception anywhere fails the job instead of hanging it.
            var stack = new Stack<IEnumerator>();
            bool failed = false;
            try { stack.Push(job.Handler(job.Args, job.Res)); }
            catch (Exception e) { Fail(job, e); failed = true; }
            while (!failed && stack.Count > 0)
            {
                object cur = null; bool moved = false;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) cur = stack.Peek().Current;
                }
                catch (Exception e) { Fail(job, e); failed = true; }
                if (failed) break;
                if (!moved) { stack.Pop(); continue; }
                if (cur is IEnumerator nested) { stack.Push(nested); continue; }
                yield return cur;
            }
            job.Done.Set();
        }

        static void Fail(Job job, Exception e)
        {
            if (e is System.Reflection.TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            if (e is ToolException) job.Res.Error(e.Message);
            else job.Res.Error(e.GetType().Name + ": " + e.Message + "\n" + Util.Cut(e.StackTrace, 900));
        }

        static void RunStartupFile(string file)
        {
            try
            {
                foreach (var item in JArray.Parse(File.ReadAllText(file)))
                {
                    string tool = (string)item["tool"];
                    ToolHandler h;
                    if (tool == null || !Handlers.TryGetValue(tool, out h)) { Debug.LogWarning("[UDriver] startup: unknown tool " + tool); continue; }
                    Jobs.Enqueue(new Job { Handler = h, Args = item["args"] as JObject ?? new JObject() });
                    EventStore.Emit("startup", "queued " + tool + " " + (item["args"] ?? "").ToString(Formatting.None));
                }
            }
            catch (Exception e) { Debug.LogError("[UDriver] startup file failed: " + e.Message); }
        }

        // ---------------- status ----------------

        static void Status(JObject a, ToolResponse r)
        {
            var scenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i).name);
            r.Text("app: " + Application.productName + " " + Application.version + " | unity " + Application.unityVersion + " | " + Application.platform
                   + (Application.isEditor ? " (editor play mode)" : Debug.isDebugBuild ? " (development build)" : " (release build)"));
            r.Text("scenes: " + string.Join(", ", scenes));
            r.Text("screen: " + Screen.width + "x" + Screen.height + " | focused: " + Application.isFocused + " | frame: " + Time.frameCount
                   + " | fps: " + (Time.unscaledDeltaTime > 0 ? (1f / Time.unscaledDeltaTime).ToString("0") : "?") + " | timeScale: " + Time.timeScale);
            r.Text("ui panels: " + UiTools.Roots().Count + " | input methods: event" + (InputTools.InjectAvailable ? ", input" : " (Input System not active: method=input unavailable)"));
            r.Text("harmony (trace_add): " + (ProbeTools.HarmonyAvailable ? "available" : "not found - add 0Harmony.dll to Assets/Plugins to enable method tracing"));
            r.Text("safety: " + (AllowUnsafe ? "UNSAFE - state_set/state_call may reach any .NET API (UDRIVER_UNSAFE=1)" : "restricted - state_set/state_call limited to game code, UnityEngine and basic types; access token required; loopback only"));
            r.Text("probes: " + ProbeTools.Count + " | recording frames: " + CaptureTools.Recording + " (" + CaptureTools.FrameCount + " stored) | last event seq: " + EventStore.LastSeq);
        }
    }

    public static class Util
    {
        static bool Null(JToken t) { return t == null || t.Type == JTokenType.Null; }
        public static bool Has(this JObject a, string k) { return !Null(a[k]); }
        public static string Str(this JObject a, string k, string d = null) { var t = a[k]; return Null(t) ? d : t.ToString(); }
        public static int Int(this JObject a, string k, int d = 0) { var t = a[k]; return Null(t) ? d : (int)Math.Round(t.ToObject<double>()); }
        public static long Long(this JObject a, string k, long d = 0) { var t = a[k]; return Null(t) ? d : (long)Math.Round(t.ToObject<double>()); }
        public static float Num(this JObject a, string k, float d = 0) { var t = a[k]; return Null(t) ? d : t.ToObject<float>(); }
        public static bool Bool(this JObject a, string k, bool d = false) { var t = a[k]; return Null(t) ? d : t.ToObject<bool>(); }

        public static string[] Strs(this JObject a, string k)
        {
            var t = a[k];
            if (Null(t)) return null;
            if (t is JArray arr) { var l = new List<string>(); foreach (var x in arr) l.Add(x.ToString()); return l.ToArray(); }
            return new[] { t.ToString() };
        }

        /// <summary>All active loaded objects of a type. Wraps the API that changed in Unity 6000.4 (the sort-mode overload became obsolete).</summary>
        public static T[] FindAll<T>() where T : UnityEngine.Object
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>();
#else
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#endif
        }

        /// <summary>JSON value -> CLR type. Adds what JToken.ToObject lacks: enums by name (case-insensitive) or number.</summary>
        public static object To(JToken token, Type type)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t.IsEnum)
            {
                if (token.Type == JTokenType.Integer) return Enum.ToObject(t, token.ToObject<long>());
                try { return Enum.Parse(t, token.ToString(), true); }
                catch (ArgumentException) { throw new ToolException("'" + token + "' is not a value of enum " + t.Name + ". Valid: " + string.Join(", ", Enum.GetNames(t))); }
            }
            if (Driver.AllowUnsafe) return token.ToObject(type);
            return token.ToObject(type, SafeJson);
        }

        // Own serializer: ignores global JsonConvert.DefaultSettings (e.g. TypeNameHandling set by the game) and refuses values that
        // would let a caller name arbitrary types, delegates or raw memory (a string can otherwise become a System.Type).
        static readonly JsonSerializer SafeJson = new JsonSerializer { TypeNameHandling = TypeNameHandling.None, Converters = { new NoUnsafeValues() } };

        sealed class NoUnsafeValues : JsonConverter
        {
            public override bool CanConvert(Type t)
            {
                t = Nullable.GetUnderlyingType(t) ?? t;
                return t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer || typeof(MemberInfo).IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t)
                       || t.Namespace == "System.Reflection";
            }
            public override bool CanWrite { get { return false; } }
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                throw new ToolException("a value of type " + objectType.Name + " cannot be passed in under the safety policy. Only a human can lift this, by starting the game/editor with environment variable UDRIVER_UNSAFE=1.");
            }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) { throw new NotSupportedException(); }
        }

        public static string Cut(string s, int n) { return s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "..."; }

        public static string OneLine(string s, int n) { return Cut(s == null ? "" : s.Replace("\r", "").Replace("\n", "\\n"), n); }

        public static bool Contains(string s, string sub) { return s != null && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Substring match, or wildcard match if the pattern has '*' / '?'.</summary>
        public static Func<string, bool> Matcher(string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0) return s => Contains(s, pattern);
            var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            return s => s != null && rx.IsMatch(s);
        }
    }
}

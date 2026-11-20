using System;
using System.Globalization;
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

        public void Append(ToolResponse o)
        {
            FlushText(); o.FlushText();
            foreach (var c in o.Content) Content.Add(c.DeepClone());
            IsError |= o.IsError;
        }
    }

    public delegate IEnumerator ToolHandler(JObject args, ToolResponse res);

    internal sealed class Job
    {
        public ToolHandler Handler;
        public string Tool;
        public JObject Args;
        public string Client = "", Holder = "";
        public volatile bool Cancelled;
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
        public static int Port { get; private set; }
        public static bool Running { get; private set; }

        internal static DriverHost Host;
        internal static int MainThreadId;
        /// <summary>The job whose coroutine is running right now (main thread).</summary>
        internal static Job Current;
        static int _pid, _active;
        const int MaxBody = 4 * 1024 * 1024, MaxActive = 32;
        internal static readonly ConcurrentQueue<Job> Jobs = new ConcurrentQueue<Job>();
        static readonly ConcurrentDictionary<string, Job> Calls = new ConcurrentDictionary<string, Job>();

        static readonly Dictionary<string, ToolHandler> Handlers = new Dictionary<string, ToolHandler>();
        static readonly Dictionary<string, ToolDef> Defs = new Dictionary<string, ToolDef>();
        static string _toolsJson;
        static HttpListener _listener;

        // ---------------- security ----------------

        /// <summary>True only if a human set UDRIVER_UNSAFE=1: state_set / state_call may then reach any type.</summary>
        public static bool AllowUnsafe { get; private set; }

        /// <summary>The operating system keeps this game from the folder app_launch named in UDRIVER_SHELL (UDriver's own), so the shell around it is on.</summary>
        static bool Shelled;

        static string FileFor(int port, string ext)
        {
            return Path.Combine(HomeDir(), port + ext);
        }

        /// <summary>~/.udriver; USERPROFILE first, the same as the server.</summary>
        internal static string HomeDir()
        {
            string u = Environment.GetEnvironmentVariable("USERPROFILE");
            return Path.Combine(string.IsNullOrEmpty(u) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : u, ".udriver");
        }

        /// <summary>Only a hash of the key is kept, in this process's environment, so it survives editor reloads and is shared by the editor and game ports; children do not inherit it.</summary>
        static readonly object PairLock = new object();
        static string KeyVar => "UDRIVER_KEY_SHA256_" + System.Diagnostics.Process.GetCurrentProcess().Id;

        static string Hash(string key)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");
        }

        /// <summary>The first key wins: from app_launch (UDRIVER_TOKEN), or the first /pair for a game started by hand.</summary>
        public static bool Pair(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 32 || key.Length > 128) return false;
            lock (PairLock)
            {
                string have = Environment.GetEnvironmentVariable(KeyVar);
                if (string.IsNullOrEmpty(have)) { Environment.SetEnvironmentVariable(KeyVar, Hash(key)); return true; }
                return SameText(have, Hash(key));
            }
        }

        public static void Publish(int port, string kind)
        {
            string t = Environment.GetEnvironmentVariable("UDRIVER_TOKEN");
            Environment.SetEnvironmentVariable("UDRIVER_TOKEN", null);
            if (!string.IsNullOrEmpty(t)) Pair(t);
            string shell = Environment.GetEnvironmentVariable("UDRIVER_SHELL");
            Environment.SetEnvironmentVariable("UDRIVER_SHELL", null);
            if (!string.IsNullOrEmpty(shell))
                try { Sandbox.Own(() => Directory.GetFileSystemEntries(shell)); } catch (UnauthorizedAccessException) { Shelled = true; } catch (Exception) { }
            // UDriver wrote the discovery file for a game it walled off.
            if (Shelled) return;
            try
            {
                var info = new JObject
                {
                    ["kind"] = kind, ["name"] = Application.productName,
                    ["project"] = Application.isEditor ? Path.GetDirectoryName(Application.dataPath) : GamePath(),
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id, ["unity"] = Application.unityVersion, ["port"] = port,
                };
                Sandbox.Own(() => File.Delete(FileFor(port, ".token")));
                WritePrivate(FileFor(port, ".json"), info.ToString(Formatting.None));
            }
            catch (Exception e) { Debug.LogWarning("[UDriver] could not write the discovery file (app_connect will not find this game, app_launch will): " + e.Message); }
        }

        internal static string GamePath()
        {
            string data = Application.dataPath, dir = Path.GetDirectoryName(data);
            if (Application.platform == RuntimePlatform.OSXPlayer) return dir;
            string name = Path.GetFileName(data);
            if (name.EndsWith("_Data")) name = name.Substring(0, name.Length - 5);
            foreach (var ext in new[] { ".exe", ".x86_64", ".arm64", ".x86", "" })
                if (File.Exists(Path.Combine(dir, name + ext))) return Path.Combine(dir, name + ext);
            return Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        }

        public static void Unpublish(int port)
        {
            try { Sandbox.Own(() => { File.Delete(FileFor(port, ".token")); File.Delete(FileFor(port, ".json")); }); } catch { }
        }

        /// <summary>0700 folder and 0600 file before anything is written.</summary>
        static void WritePrivate(string f, string text) { Sandbox.Own(() => WriteOwn(f, text)); }

        static void WriteOwn(string f, string text)
        {
            string dir = Path.GetDirectoryName(f), tmp = f + "." + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            Directory.CreateDirectory(dir);
            OwnerOnly(dir, true);
            try
            {
                File.WriteAllText(tmp, "");
                OwnerOnly(tmp, false);
                File.WriteAllText(tmp, text);
                if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

#if (UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR_WIN
        [DllImport("libc", EntryPoint = "chmod")] static extern int ChmodLibc(string path, int mode);
        [DllImport("libc.so.6", EntryPoint = "chmod")] static extern int ChmodGlibc(string path, int mode);

        /// <summary>Throws rather than leave a secret readable by others.</summary>
        internal static void OwnerOnly(string path, bool folder)
        {
            int mode = folder ? 448 : 384;   // 0700 : 0600
            int rc;
            try { rc = ChmodLibc(path, mode); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { rc = ChmodGlibc(path, mode); }
            if (rc != 0) throw new IOException("could not restrict permissions of " + path);
        }
#else
        internal static void OwnerOnly(string path, bool folder) { }   // Windows: the user profile is already private to the user
#endif

        static bool SameText(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Rejects other Host values (DNS rebinding), requests from web pages, and callers without the token.</summary>
        public static bool Authorize(HttpListenerRequest req, int port, bool needToken, out int status, out string error)
        {
            status = 403;
            string host = req.Headers["Host"] ?? "";
            if (host != "127.0.0.1:" + port && host != "localhost:" + port) { error = "unexpected Host header"; return false; }
            if (!string.IsNullOrEmpty(req.Headers["Origin"])) { error = "requests from web pages are not accepted"; return false; }
            if (needToken && !(Environment.GetEnvironmentVariable(KeyVar) is string have && have.Length > 0 && req.Headers["X-UDriver-Token"] is string key && SameText(have, Hash(key))))
            { status = 401; error = "missing or wrong X-UDriver-Token"; return false; }
            error = null;
            return true;
        }

        /// <summary>POST /pair with the key in X-UDriver-Token: accepted once per process, again only with the same key.</summary>
        public static void PairReply(HttpListenerRequest req, out int status, out string body)
        {
            bool ok = Pair(req.Headers["X-UDriver-Token"]);
            status = ok ? 200 : 409;
            body = ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"already connected to another program\"}";
        }

        public static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            bool force = false;
#if UDRIVER_FORCE
            force = true;
#endif
            if (!(Application.isEditor || Debug.isDebugBuild || force)) return;

            int port = 0;
            int slot = 0;
            var argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (argv[i] == "-udriver-port") int.TryParse(argv[i + 1], out port);
                if (argv[i] == "-udriver-slot") int.TryParse(argv[i + 1], out slot);
            }
            if (slot > 0) Slots.Redirect(slot);
            string startup = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UDRIVER_TOKEN")) ? null : Environment.GetEnvironmentVariable("UDRIVER_STARTUP");
            Environment.SetEnvironmentVariable("UDRIVER_STARTUP", null);
            Start(port);
            if (!string.IsNullOrEmpty(startup) && Running) RunStartup(startup);
        }

        public static void Start(int port)
        {
            if (Running) return;
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            AllowUnsafe = Environment.GetEnvironmentVariable("UDRIVER_UNSAFE") == "1";
            _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Application.runInBackground = true;

            var go = new GameObject("[UDriver]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Host = go.AddComponent<DriverHost>();

            RegisterAll();

            _listener = Bind(port > 0 ? new[] { port } : ToolManifest.Ports(false), out int bound, out string error);
            if (_listener == null) { Debug.LogError("[UDriver] could not start HTTP listener on port " + (port > 0 ? port.ToString() : ToolManifest.GamePort + "-" + ToolManifest.GamePortLast) + ": " + error); return; }
            Port = bound;
            Publish(bound, "game");
            new Thread(Listen) { IsBackground = true, Name = "UDriver.Http" }.Start();
            Running = true;
            Debug.Log("[UDriver] listening on http://127.0.0.1:" + bound + " (" + Handlers.Count + " tools)");
        }

        /// <summary>Loopback literal only: "localhost" can make Mono listen on all interfaces.</summary>
        public static HttpListener Bind(IEnumerable<int> ports, out int port, out string error)
        {
            port = 0; error = "no port to try";
            foreach (int p in ports)
            {
                var l = new HttpListener();
                try
                {
                    l.Prefixes.Add("http://127.0.0.1:" + p + "/");
                    l.Start();
                    port = p;
                    return l;
                }
                catch (Exception e) { error = e.Message; try { l.Close(); } catch { } }
            }
            return null;
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
#if UDRV_PHYSICS
            AutoplayTools.Register();
#endif
            MemoryTools.Register();
            RenderTools.Register();
            RecordTools.Register();
            Sandbox.Register();
            QaTools.Register();
            AppMap.Register();
            QueryTools.Register();
            NetCapture.Register();
        }

        /// <summary>Runs another tool from inside a tool (same arguments an agent would send).</summary>
        public static IEnumerator Invoke(string tool, JObject args, ToolResponse res)
        {
            ToolHandler h;
            if (tool == "eval" || !Handlers.TryGetValue(tool, out h)) throw new ToolException(Unknown(tool));
            return h(args ?? new JObject(), res);
        }

        internal static bool Knows(string tool) { return Handlers.ContainsKey(tool); }

        static IEnumerator Batch(JObject a, ToolResponse r)
        {
            var calls = a["calls"] as JArray;
            if (calls == null || calls.Count == 0) throw new ToolException("calls must be a non-empty array of {tool, args}");
            if (calls.Count > 50) throw new ToolException("at most 50 calls per batch");
            bool stop = a.Bool("stopOnError", true);
            int first = a.Int("first", 0);
            string output = a.Str("output", "all");
            if (output != "all" && output != "last" && output != "errors") throw new ToolException("output must be all, last or errors");
            ToolResponse last = null;
            for (int i = 0; i < calls.Count; i++)
            {
                string tool = (string)calls[i]["tool"];
                var s = new ToolResponse();
                s.Text("--- [" + (first + i) + "] " + tool);
                ToolHandler h;
                if (tool == null || tool == "batch" || tool == "eval" || !Handlers.TryGetValue(tool, out h)) s.Error(Unknown(tool));
                else
                {
                    QaTools.OnCall(tool, calls[i]["args"] as JObject ?? new JObject());
                    yield return Guard(() => h(calls[i]["args"] as JObject ?? new JObject(), s), s);
                }
                last = null;
                if (s.IsError || (output == "all" && !((JObject)calls[i]).Bool("quiet"))) r.Append(s);
                else if (output == "last") last = s;
                if (s.IsError && stop) { r.Text("batch stopped at call " + (first + i) + " (stopOnError)"); yield break; }
            }
            if (last != null) r.Append(last);
        }

        /// <summary>Runs an inspection tool to completion in the editor outside Play Mode.</summary>
        public static void RunEditMode(string tool, JObject args, ToolResponse res)
        {
            if (Handlers.Count == 0) { MainThreadId = Thread.CurrentThread.ManagedThreadId; RegisterAll(); }
            if (Array.IndexOf(ToolManifest.EditModeTools, tool) < 0)
                throw new ToolException("'" + tool + "' needs a running game: enter Play Mode (editor_play action=play) or start a build (app_launch), then call it again");
            ToolHandler h;
            if (!Handlers.TryGetValue(tool, out h)) throw new ToolException(Unknown(tool));
            var it = Guard(() => h(args ?? new JObject(), res), res);
            for (int guard = 0; guard < 10000 && it.MoveNext(); guard++) { }
        }

        /// <summary>Serves inspection tools while the editor is paused: there are no frames, so DriverHost does not run.</summary>
        public static void PumpPaused()
        {
            Job job;
            while (Jobs.TryDequeue(out job))
            {
                if (Array.IndexOf(ToolManifest.EditModeTools, job.Tool) >= 0)
                {
                    var it = RunJob(job);
                    for (int guard = 0; guard < 10000 && it.MoveNext(); guard++) { }
                }
                else job.Res.Error("the editor is paused, so '" + job.Tool + "' cannot run (inspection tools still work). Resume with editor_play action=resume.");
                job.Done.Set();
            }
        }

        public static void Stop()
        {
            if (Running) Unpublish(Port);
            Running = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            ProbeTools.RemoveAll();
            InputTools.Unpatch();
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
            bool busy = Interlocked.Increment(ref _active) > MaxActive;
            try
            {
                string path = ctx.Request.Url.AbsolutePath, verb = ctx.Request.HttpMethod, deny;
                if (busy) { status = 503; body = "{\"ok\":false,\"error\":\"too many requests at once\"}"; }
                else if (!Authorize(ctx.Request, Port, path != "/ping" && path != "/pair", out status, out deny))
                    body = new JObject { ["ok"] = false, ["error"] = deny }.ToString(Formatting.None);
                else if (path == "/ping" && verb == "GET") { status = 200; body = "{\"ok\":true,\"pid\":" + _pid + "}"; }
                else if (path == "/pair" && verb == "POST") PairReply(ctx.Request, out status, out body);
                else if (path == "/tools" && verb == "GET") { status = 200; body = ToolsJson(); }
                else if (path == "/cancel" && verb == "POST" && ctx.Request.ContentLength64 is >= 0 and <= 1024)
                {
                    string id;
                    using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) id = (string)JObject.Parse(r.ReadToEnd())["call"] ?? "";
                    Job job;
                    bool found = Calls.TryGetValue(id, out job);
                    if (found) job.Cancelled = true;
                    status = 200; body = "{\"ok\":" + (found ? "true" : "false") + "}";
                }
                else if (path == "/call" && verb == "POST" && !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                { status = 415; body = "{\"ok\":false,\"error\":\"Content-Type must be application/json\"}"; }
                else if (path == "/call" && verb == "POST" && (ctx.Request.ContentLength64 < 0 || ctx.Request.ContentLength64 > MaxBody))
                { status = 413; body = "{\"ok\":false,\"error\":\"request body missing its length or larger than 4 MB\"}"; }
                else if (path == "/call" && verb == "POST")
                {
                    status = 200;
                    string reqBody;
                    using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) reqBody = r.ReadToEnd();
                    var req = JObject.Parse(reqBody);
                    string client = ctx.Request.Headers["X-UDriver-Client"];
                    body = Call((string)req["tool"], req["args"] as JObject ?? new JObject(), string.IsNullOrEmpty(client) ? "another agent" : Util.OneLine(client, 40), ctx.Request.Headers["X-UDriver-Holder"], ctx.Request.Headers["X-UDriver-Call"]).ToString(Formatting.None);
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
            finally { Interlocked.Decrement(ref _active); }
        }

        static JObject Call(string tool, JObject args, string client, string holder, string callId)
        {
            ToolHandler h;
            lock (Handlers) { if (tool == null || !Handlers.TryGetValue(tool, out h)) h = null; }
            if (h == null) return new JObject { ["ok"] = false, ["error"] = Unknown(tool) };
            var job = new Job { Handler = h, Tool = tool, Args = args, Client = client, Holder = string.IsNullOrEmpty(holder) ? client : holder };
            bool tracked = !string.IsNullOrEmpty(callId) && callId.Length <= 64 && Calls.TryAdd(callId, job);
            try
            {
                Jobs.Enqueue(job);
                int ms = Math.Min(Math.Max(args.Int("timeoutMs", 0), 0), 600000), wait = ms > 0 ? ms + 2000 : 120000;
                if (!job.Done.Wait(wait))
                {
                    job.Cancelled = true;
                    return new JObject { ["ok"] = false, ["error"] = "timeout: '" + tool + "' did not finish within " + wait + " ms" + (job.Done.Wait(2000)
                        ? " and was stopped; any input it held is released" : "; the game's main thread looks frozen, so it will be stopped as soon as the game runs again") };
                }
            }
            finally { if (tracked) Calls.TryRemove(callId, out _); }
            if (job.Cancelled) return new JObject { ["ok"] = false, ["error"] = "cancelled: '" + tool + "' was stopped by its caller; any input it held is released" };
            job.Res.FlushText();
            return new JObject { ["ok"] = !job.Res.IsError, ["isError"] = job.Res.IsError, ["content"] = job.Res.Content };
        }

        static string Unknown(string tool)
        {
            return tool != null && Defs.ContainsKey(tool) ? "tool '" + tool + "' is not available in this game build" : "unknown tool '" + tool + "'";
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
            QaTools.OnCall(job.Tool, job.Args);
            var it = Guard(() => job.Handler(job.Args, job.Res), job.Res);
            try
            {
                while (!job.Cancelled)
                {
                    Current = job;
                    float t0 = Time.realtimeSinceStartup;
                    bool more = it.MoveNext();
                    QaTools.Own(job.Tool, Time.realtimeSinceStartup - t0);
                    Current = null;
                    if (!more) break;
                    yield return it.Current;
                }
            }
            finally
            {
                Current = job;
                ((IDisposable)it).Dispose();
                Current = null;
                job.Done.Set();
            }
        }

        /// <summary>Flattens nested enumerators so an exception anywhere becomes an error in res instead of escaping or hanging.</summary>
        internal static IEnumerator Guard(Func<IEnumerator> start, ToolResponse res)
        {
            var stack = new Stack<IEnumerator>();
            bool failed = false;
            try { stack.Push(start()); }
            catch (Exception e) { Fail(res, e); failed = true; }
            try
            {
                while (!failed && stack.Count > 0)
                {
                    object cur = null; bool moved = false;
                    try
                    {
                        moved = stack.Peek().MoveNext();
                        if (moved) cur = stack.Peek().Current;
                    }
                    catch (Exception e) { Fail(res, e); failed = true; }
                    if (failed) break;
                    if (!moved) { stack.Pop(); continue; }
                    if (cur is IEnumerator nested) { stack.Push(nested); continue; }
                    yield return cur;
                }
            }
            finally
            {
                // Runs the finally blocks of unfinished tools when a call fails or is cancelled.
                while (stack.Count > 0)
                {
                    var d = stack.Pop() as IDisposable;
                    try { if (d != null) d.Dispose(); } catch (Exception e) { Fail(res, e); }
                }
            }
        }

        static void Fail(ToolResponse res, Exception e)
        {
            if (e is System.Reflection.TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            if (e is ToolException) res.Error(e.Message);
            else res.Error(e.GetType().Name + ": " + e.Message + "\n" + Util.Cut(e.StackTrace, 900));
        }

        static void RunStartup(string json)
        {
            try
            {
                foreach (var item in JArray.Parse(json))
                {
                    string tool = (string)item["tool"];
                    ToolHandler h;
                    if (tool == "batch" || tool == "eval") { Debug.LogWarning("[UDriver] startup: " + tool + " is not run at startup"); continue; }
                    if (tool == null || !Handlers.TryGetValue(tool, out h)) { Debug.LogWarning("[UDriver] startup: unknown tool " + tool); continue; }
                    Jobs.Enqueue(new Job { Handler = h, Tool = tool, Args = item["args"] as JObject ?? new JObject(), Client = "startup", Holder = "startup" });
                    EventStore.Emit("startup", "queued " + tool + " " + (item["args"] ?? "").ToString(Formatting.None));
                }
            }
            catch (Exception e) { Debug.LogError("[UDriver] startup calls failed: " + e.Message); }
        }

        // ---------------- status ----------------

        static void Status(JObject a, ToolResponse r)
        {
            var scenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i).name);
            r.Text("app: " + Application.productName + " " + Application.version + " | unity " + Application.unityVersion + " | " + Application.platform
                   + (Application.isEditor ? " (editor play mode)" : Debug.isDebugBuild ? " (development build)" : " (release build)"));
            r.Text("scenes: " + string.Join(", ", scenes));
            if (Shelled) r.Text("computer: walled off by the operating system (files, programs and network outside the game are closed)");
            if (Slots.Slot > 0) r.Text("copy slot " + Slots.Slot + ": " + (Slots.Problem == null ? "saves and PlayerPrefs in " + Slots.Folder : "saves are shared with other copies (" + Slots.Problem + ")"));
            r.Text("input: " + InputTools.Controls(Current));
            r.Text("screen: " + Screen.width + "x" + Screen.height + " | focused: " + Application.isFocused + " | frame: " + Time.frameCount
                   + " | fps: " + (Time.unscaledDeltaTime > 0 ? (1f / Time.unscaledDeltaTime).ToString("0", CultureInfo.InvariantCulture) : "?") + " | timeScale: " + Time.timeScale);
            r.Text("ui panels: " + UiTools.Roots().Count + " | input methods: event" + (InputTools.InjectAvailable ? ", input" : " (Input System not active: method=input unavailable)") + (InputTools.OsKeysAvailable ? ", os (real key and mouse events for the old Input Manager)" : "") + (ProbeTools.HarmonyAvailable ? ", patch (old Input Manager through Harmony)" : ""));
            #if ENABLE_IL2CPP
            r.Text("trace_add: not available in IL2CPP builds (method tracing needs a Mono build)");
#else
            r.Text("harmony (trace_add): " + (ProbeTools.HarmonyAvailable ? "available" : "not found - add 0Harmony.dll to Assets/Plugins to enable method tracing"));
#endif
            r.Text("safety: " + (AllowUnsafe ? "UNSAFE - state_set/state_call may reach any .NET API (UDRIVER_UNSAFE=1)" : "restricted - state_set/state_call limited to game code, UnityEngine and basic types; access token required; loopback only"));
            r.Text("probes: " + ProbeTools.Count + " | recording frames: " + CaptureTools.Recording + " (" + CaptureTools.FrameCount + " stored) | last event seq: " + EventStore.LastSeq);
        }
    }

    public static class Util
    {
#if UNITY_2020_2_OR_NEWER
        public static double Seconds { get { return Time.realtimeSinceStartupAsDouble; } }
#else
        public static double Seconds { get { return Time.realtimeSinceStartup; } }
#endif
        static bool Null(JToken t) { return t == null || t.Type == JTokenType.Null; }
        public static bool Has(this JObject a, string k) { return !Null(a[k]); }
        public static string Str(this JObject a, string k, string d = null) { var t = a[k]; return Null(t) ? d : t.ToString(); }
        public static int Int(this JObject a, string k, int d = 0) { var t = a[k]; return Null(t) ? d : (int)Math.Round(Number(t, k)); }
        public static long Long(this JObject a, string k, long d = 0) { var t = a[k]; return Null(t) ? d : (long)Math.Round(Number(t, k)); }
        public static float Num(this JObject a, string k, float d = 0) { var t = a[k]; return Null(t) ? d : (float)Number(t, k); }

        /// <summary>Also takes an element id the way UDriver prints it ("#1000001").</summary>
        static double Number(JToken t, string k)
        {
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.ToObject<double>();
            double v;
            if (t.Type == JTokenType.String && double.TryParse(((string)t).Trim().TrimStart('#'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            throw new ToolException(k + " must be a number, got " + Cut(t.ToString(Formatting.None), 60));
        }
        public static bool Bool(this JObject a, string k, bool d = false) { var t = a[k]; return Null(t) ? d : t.ToObject<bool>(); }

        public static string[] Strs(this JObject a, string k)
        {
            var t = a[k];
            if (Null(t)) return null;
            if (t is JArray arr) { var l = new List<string>(); foreach (var x in arr) l.Add(x.ToString()); return l.ToArray(); }
            return new[] { t.ToString() };
        }

        /// <summary>Wraps the API that changed across Unity versions.</summary>
        public static T[] FindAll<T>() where T : UnityEngine.Object
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>();
#elif UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<T>();
#endif
        }

        /// <summary>JToken.ToObject plus enums by name or number.</summary>
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

        // Ignores the game's JsonConvert.DefaultSettings and refuses values that could name arbitrary types.
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

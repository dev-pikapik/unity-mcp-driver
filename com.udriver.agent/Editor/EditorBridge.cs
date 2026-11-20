namespace UDriver.Editor
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using UnityEditor;
    using UnityEditor.Build.Reporting;
    using UnityEditor.Compilation;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    #if UDRV_TESTS
    using UnityEditor.TestTools.TestRunner.Api;
    #endif

    /// <summary>Editor bridge on its own port; restarts on every script reload, long operations keep state in SessionState.</summary>
    [InitializeOnLoad]
    public static class EditorBridge
    {
        const string KPort = "UDriver.Port", KErrors = "UDriver.CompileErrors", KBuild = "UDriver.Build", KTestState = "UDriver.TestState", KTestLines = "UDriver.TestLines", KTestStart = "UDriver.TestStart";

        sealed class EJob
        {
            public string Tool; public JObject Args;
            public int State;   // 0 queued, 1 started, 2 given up before it started
            public readonly ToolResponse Res = new ToolResponse();
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static readonly ConcurrentQueue<EJob> Jobs = new ConcurrentQueue<EJob>();
        static readonly Dictionary<string, Action<JObject, ToolResponse>> Tools = new Dictionary<string, Action<JObject, ToolResponse>>();
        static HttpListener _listener;
        static int _port, _pid;
        const string MarkedText = "stays closed: an agent changed this game while Computer access was off, and its saved data is shared with Play Mode. The owner can clear the mark in the UDriver panel.";

        static EditorBridge()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            Tools["editor_status"] = Status;
            Tools["editor_play"] = Play;
            Tools["editor_compile"] = Compile;
            Tools["editor_tests"] = TestsTool;
            Tools["editor_build"] = Build;
            Tools["editor_scene"] = SceneTool;
            Tools["editor_assets"] = Assets;
            Tools["editor_menu"] = Menu;

            EditorApplication.playModeStateChanged += s =>
            {
                if (s != PlayModeStateChange.ExitingEditMode || !Sandbox.Marked()) return;
                EditorApplication.isPlaying = false;
                Debug.LogError("[UDriver] Play Mode " + MarkedText);
            };
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += () => { StopListener(); Driver.Unpublish(_port); };
            CompilationPipeline.compilationStarted += _ => SessionState.SetString(KErrors, "[]");
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            TestsInit();
            _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            StartListener();
        }

        // ---------------- HTTP ----------------

        static void StartListener()
        {
            IEnumerable<int> ports = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_EDITOR_PORT"), out int fixedPort) ? new[] { fixedPort }
                : new[] { SessionState.GetInt(KPort, 0), EditorPrefs.GetInt("UDriver.EditorPort", ToolManifest.EditorPort) }.Where(p => p > 0).Concat(ToolManifest.Ports(true)).Distinct();
            _listener = Driver.Bind(ports, out _port, out string error);
            if (_listener == null) { Debug.LogWarning("[UDriver] editor bridge found no free port (" + error + ")"); return; }
            SessionState.SetInt(KPort, _port);
            Driver.Publish(_port, "editor");
            var l = _listener;
            new Thread(() => Listen(l)) { IsBackground = true, Name = "UDriver.EditorHttp" }.Start();
        }

        static void StopListener()
        {
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        static void BeforeReload()
        {
            TestsFlush();
            StopListener();
        }

        static void Listen(HttpListener l)
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); }
                catch (Exception e)
                {
                    if (!l.IsListening) break;   // stopped or disposed
                    Debug.LogWarning("[UDriver] editor bridge: accepting a request failed: " + e.Message);
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
                if (!Driver.Authorize(ctx.Request, _port, path != "/ping" && path != "/pair", out status, out deny))
                    body = new JObject { ["ok"] = false, ["error"] = deny }.ToString(Formatting.None);
                else if (path == "/ping" && verb == "GET") { status = 200; body = "{\"ok\":true,\"editor\":true,\"pid\":" + _pid + "}"; }
                else if (path == "/pair" && verb == "POST") Driver.PairReply(ctx.Request, out status, out body);
                else if (path == "/call" && verb == "POST" && !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                { status = 415; body = "{\"ok\":false,\"error\":\"Content-Type must be application/json\"}"; }
                else if (path == "/call" && verb == "POST" && (ctx.Request.ContentLength64 < 0 || ctx.Request.ContentLength64 > 4 * 1024 * 1024))
                { status = 413; body = "{\"ok\":false,\"error\":\"request body missing its length or larger than 4 MB\"}"; }
                else if (path == "/call" && verb == "POST")
                {
                    status = 200;
                    string reqBody;
                    using (var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) reqBody = rd.ReadToEnd();
                    var req = JObject.Parse(reqBody);
                    var job = new EJob { Tool = (string)req["tool"], Args = req["args"] as JObject ?? new JObject() };
                    Jobs.Enqueue(job);
                    // The server gives up after 90 s, so a blocked main thread is reported long before that.
                    if (!job.Done.Wait(10000) && Interlocked.CompareExchange(ref job.State, 2, 0) == 0)
                        body = new JObject { ["ok"] = false, ["error"] = "the Unity editor is blocked (a dialog is open, or a long import, build or test run is in progress) - '" + job.Tool + "' was not run; look at the Unity window, then retry" }.ToString(Formatting.None);
                    else if (!job.Done.Wait(70000))
                        body = new JObject { ["ok"] = false, ["error"] = "'" + job.Tool + "' is still running in the editor after 80 s and goes on; check the result with editor_status" }.ToString(Formatting.None);
                    else
                    {
                        job.Res.FlushText();
                        body = new JObject { ["ok"] = !job.Res.IsError, ["isError"] = job.Res.IsError, ["content"] = job.Res.Content }.ToString(Formatting.None);
                    }
                }
                else { status = 404; body = "{\"ok\":false,\"error\":\"not found\"}"; }
            }
            catch (Exception e) { status = 500; body = new JObject { ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message }.ToString(Formatting.None); }
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

        static void Pump()
        {
            if (EditorApplication.isPlaying && EditorApplication.isPaused) Driver.PumpPaused();
            EJob job;
            while (Jobs.TryDequeue(out job))
            {
                if (Interlocked.CompareExchange(ref job.State, 1, 0) != 0) continue;
                try
                {
                    Action<JObject, ToolResponse> h;
                    if (job.Tool != null && Tools.TryGetValue(job.Tool, out h)) h(job.Args, job.Res);
                    else if (EditorApplication.isPlaying) job.Res.Error("'" + job.Tool + "' is served by the game driver while in Play Mode (port " + Driver.Port + ") - call app_connect");
                    else
                    {
                        Driver.RunEditMode(job.Tool, job.Args, job.Res);
                        if (ToolManifest.Level(job.Tool) >= 2) EditorSceneManager.MarkAllScenesDirty();
                    }
                }
                catch (Exception e)
                {
                    if (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
                    job.Res.Error(e is ToolException ? e.Message : e.GetType().Name + ": " + e.Message + "\n" + Util.Cut(e.StackTrace, 600));
                }
                job.Done.Set();
            }
        }

        // ---------------- status / play ----------------

        static void Status(JObject a, ToolResponse r)
        {
            r.Text("unity " + Application.unityVersion + " | project: " + Path.GetDirectoryName(Application.dataPath) + " | target: " + EditorUserBuildSettings.activeBuildTarget);
            r.Text("play mode: " + (EditorApplication.isPlaying ? (EditorApplication.isPaused ? "playing (paused)" : "playing") : EditorApplication.isPlayingOrWillChangePlaymode ? "entering..." : "stopped")
                   + " | compiling: " + EditorApplication.isCompiling + " | importing: " + EditorApplication.isUpdating);
            var scenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); scenes.Add(s.path + (s.isDirty ? " (unsaved)" : "")); }
            r.Text("open scenes: " + string.Join(", ", scenes));
            var errs = JArray.Parse(SessionState.GetString(KErrors, "[]"));
            r.Text("compile errors: " + errs.Count + (EditorUtility.scriptCompilationFailed ? " (last compilation FAILED - see editor_compile)" : ""));
            r.Text("tests: " + SessionState.GetString(KTestState, "never run") + " | build: " + Util.OneLine(SessionState.GetString(KBuild, "never built"), 200));
            r.Text(EditorApplication.isPlaying ? "game driver: port " + Driver.Port + " (all runtime tools available)" : "not in Play Mode: runtime UI/input tools need editor_play action=play; inspection tools (scene_tree, state_get, types_find...) work now");
        }

        static void Play(JObject a, ToolResponse r)
        {
            switch (a.Str("action"))
            {
                case "play":
                    if (EditorUtility.scriptCompilationFailed) throw new ToolException("cannot enter Play Mode: scripts have compile errors (editor_compile)");
                    if (Sandbox.Marked()) throw new ToolException("cannot enter Play Mode: it " + MarkedText);
                    EditorApplication.isPlaying = true;
                    r.Text("entering Play Mode. Scripts reload now; wait a few seconds, then call app_connect.");
                    break;
                case "stop": EditorApplication.isPlaying = false; r.Text("leaving Play Mode"); break;
                case "pause": EditorApplication.isPaused = true; r.Text("paused (true editor pause: Update is not called)"); break;
                case "resume": EditorApplication.isPaused = false; r.Text("resumed"); break;
                case "step": EditorApplication.Step(); r.Text("advanced one frame (editor stays paused)"); break;
                default: throw new ToolException("action must be play, stop, pause, resume or step");
            }
        }

        // ---------------- compilation ----------------

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            var arr = JArray.Parse(SessionState.GetString(KErrors, "[]"));
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error || arr.Count >= 200) continue;
                arr.Add(new JObject { ["file"] = m.file, ["line"] = m.line, ["message"] = m.message, ["assembly"] = Path.GetFileName(assembly) });
            }
            SessionState.SetString(KErrors, arr.ToString(Formatting.None));
        }

        static void Compile(JObject a, ToolResponse r)
        {
            if (a.Str("action", "status") == "refresh")
            {
                AssetDatabase.Refresh();
                CompilationPipeline.RequestScriptCompilation();
                r.Text("refresh + recompile requested. Poll editor_compile until compiling=false.");
                return;
            }
            var errs = JArray.Parse(SessionState.GetString(KErrors, "[]"));
            r.Text("compiling: " + EditorApplication.isCompiling + " | last compilation failed: " + EditorUtility.scriptCompilationFailed + " | errors recorded: " + errs.Count);
            foreach (var e in errs) r.Text("  " + e["file"] + "(" + e["line"] + "): " + Util.OneLine((string)e["message"], 400));
            if (EditorUtility.scriptCompilationFailed && errs.Count == 0) r.Text("  (errors happened before the bridge loaded - run editor_compile action=refresh to capture them)");
        }

        // ---------------- tests ----------------

#if UDRV_TESTS
        static readonly List<string> TestLines = new List<string>();
        static int _unflushed, _passed, _failed, _skipped;

        sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite) return;
                var st = result.TestStatus;
                if (st == TestStatus.Passed) _passed++; else if (st == TestStatus.Failed) _failed++; else _skipped++;
                var o = new JObject { ["name"] = result.Test.FullName, ["status"] = st.ToString(), ["sec"] = Math.Round(result.Duration, 3) };
                if (st != TestStatus.Passed) { o["message"] = Util.Cut(result.Message, 1500); o["stack"] = Util.Cut(result.StackTrace, 1500); }
                TestLines.Add(o.ToString(Formatting.None));
                SessionState.SetString(KTestState, "running: " + _passed + " passed, " + _failed + " failed, " + _skipped + " skipped so far");
                if (++_unflushed >= 25 || st == TestStatus.Failed) TestsFlush();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                TestsFlush();
                SessionState.SetString(KTestState, "finished: " + result.PassCount + " passed, " + result.FailCount + " failed, " + result.SkipCount + " skipped in " + (EditorApplication.timeSinceStartup - SessionState.GetFloat(KTestStart, (float)EditorApplication.timeSinceStartup)).ToString("0.0", CultureInfo.InvariantCulture) + "s (" + result.TestStatus + ")");
            }
        }

        static TestRunnerApi _api;

        static void TestsInit()
        {
            // Restore progress across the script reloads that PlayMode test runs cause.
            string saved = SessionState.GetString(KTestLines, "");
            if (saved.Length > 0) TestLines.AddRange(saved.Split('\n'));
            foreach (var l in TestLines)
            {
                if (l.Contains("\"status\":\"Passed\"")) _passed++; else if (l.Contains("\"status\":\"Failed\"")) _failed++; else _skipped++;
            }
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.hideFlags = HideFlags.HideAndDontSave;
            _api.RegisterCallbacks(new Callbacks());
        }

        static void TestsFlush()
        {
            _unflushed = 0;
            SessionState.SetString(KTestLines, string.Join("\n", TestLines));
        }

        static void TestsTool(JObject a, ToolResponse r)
        {
            string action = a.Str("action", "status");
            if (action == "run")
            {
                if (EditorApplication.isPlaying) throw new ToolException("leave Play Mode first (editor_play action=stop)");
                if (EditorUtility.scriptCompilationFailed) throw new ToolException("scripts have compile errors (editor_compile)");
                var filter = new Filter
                {
                    testMode = string.Equals(a.Str("mode", "EditMode"), "PlayMode", StringComparison.OrdinalIgnoreCase) ? TestMode.PlayMode : TestMode.EditMode,
                    testNames = a.Strs("names"), groupNames = a.Strs("groups"), categoryNames = a.Strs("categories"), assemblyNames = a.Strs("assemblies")
                };
                TestLines.Clear(); _passed = _failed = _skipped = 0; TestsFlush();
                SessionState.SetString(KTestState, "running: started " + filter.testMode);
                SessionState.SetFloat(KTestStart, (float)EditorApplication.timeSinceStartup);
                _api.Execute(new ExecutionSettings(filter));
                r.Text("test run started (" + filter.testMode + "). Poll editor_tests action=status; when finished use action=results." + (filter.testMode == TestMode.PlayMode ? " PlayMode runs enter Play Mode and reload scripts." : ""));
                return;
            }
            r.Text("tests: " + SessionState.GetString(KTestState, "never run") + " | recorded results: " + TestLines.Count);
            if (action != "results") return;
            bool onlyFailed = a.Bool("onlyFailed", true);
            int limit = a.Int("limit", 30), n = 0, more = 0;
            foreach (var l in TestLines)
            {
                JObject o;
                try { o = JObject.Parse(l); } catch { continue; }
                string st = (string)o["status"];
                if (onlyFailed && st != "Failed") continue;
                if (n >= limit) { more++; continue; }
                n++;
                r.Text(st.ToUpperInvariant() + "  " + o["name"] + "  (" + o["sec"] + "s)");
                if (o["message"] != null && ((string)o["message"]).Length > 0) r.Text("    " + ((string)o["message"]).Trim().Replace("\n", "\n    "));
                if (o["stack"] != null && ((string)o["stack"]).Length > 0) r.Text("    " + ((string)o["stack"]).Trim().Replace("\n", "\n    "));
            }
            r.Text(n + " test(s) listed" + (more > 0 ? ", " + more + " more (raise limit)" : "") + (onlyFailed ? " (failed only; onlyFailed=false for all)" : ""));
        }
#else
        static void TestsInit() { }
        static void TestsFlush() { }
        static void TestsTool(JObject a, ToolResponse r) { throw new ToolException("the Unity Test Framework package (com.unity.test-framework) is not installed in this project"); }
#endif

        // ---------------- build ----------------

        static void Build(JObject a, ToolResponse r)
        {
            if (a.Str("action", "status") != "build") { r.Text("build: " + SessionState.GetString(KBuild, "never built")); return; }
            var target = EditorUserBuildSettings.activeBuildTarget;
            string root = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            string ext = target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 ? ".exe" : target == BuildTarget.StandaloneOSX ? ".app"
                : target == BuildTarget.StandaloneLinux64 ? ".x86_64" : target == BuildTarget.Android ? ".apk" : "";
            string example = "Builds/" + target + "/" + PlayerSettings.productName + ext;
            string path = Path.GetFullPath(Path.Combine(root, a.Str("path") ?? example));
            var cmp = Application.platform == RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var inside = new List<string> { root };
            if (a.Strs("buildDirs") is string[] dirs) inside.AddRange(dirs);
            if (!inside.Exists(d => path.StartsWith(d.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, cmp)))
                throw new ToolException("Builds go inside the project folder or a folder listed in UDRIVER_BUILDS_DIR, for example " + example);
            if (ext.Length > 0 && !path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && !(target == BuildTarget.Android && path.EndsWith(".aab", StringComparison.OrdinalIgnoreCase)))
                throw new ToolException("the active build target is " + target + ", so the output must end with " + ext + ", for example " + example + ". Switch the target in Build Settings to build for another platform");
            if (EditorApplication.isPlaying) throw new ToolException("leave Play Mode first (editor_play action=stop)");
            if (EditorUtility.scriptCompilationFailed) throw new ToolException("scripts have compile errors (editor_compile)");
            var scenes = new List<string>();
            var given = a.Strs("scenes");
            if (given != null) { foreach (var g in given) RequireProjectPath(g); scenes.AddRange(given); }
            else foreach (var s in EditorBuildSettings.scenes) if (s.enabled) scenes.Add(s.path);
            if (scenes.Count == 0) throw new ToolException("no scenes: Build Settings has no enabled scenes and none were passed");
            bool dev = a.Bool("development", true);
            var opts = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(), locationPathName = path, target = target,
                options = dev ? BuildOptions.Development : BuildOptions.None
            };
            SessionState.SetString(KBuild, "RUNNING since " + DateTime.Now.ToString("HH:mm:ss") + " -> " + path);
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var report = BuildPipeline.BuildPlayer(opts);
                    var s = report.summary;
                    SessionState.SetString(KBuild, s.result.ToString().ToUpperInvariant() + ": " + s.outputPath + " | " + s.totalErrors + " error(s), " + s.totalWarnings + " warning(s) | "
                                                   + (s.totalSize / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB | " + s.totalTime.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s | " + (dev ? "development" : "release") + " " + s.platform);
                }
                catch (Exception e) { SessionState.SetString(KBuild, "FAILED: " + e.Message); }
            };
            r.Text("build started -> " + path + " (" + scenes.Count + " scene(s), " + (dev ? "development" : "release") + ", " + opts.target
                   + "). The editor does not answer while building; poll editor_build action=status, then app_launch the result.");
        }

        // ---------------- scenes / assets / menu ----------------

        static void SceneTool(JObject a, ToolResponse r)
        {
            string action = a.Str("action", "list");
            if (action == "open")
            {
                if (EditorApplication.isPlaying) throw new ToolException("leave Play Mode first");
                string path = a.Str("path");
                if (path == null) throw new ToolException("path is required (see action=list or editor_assets filter='t:Scene')");
                RequireProjectPath(path);
                EditorSceneManager.OpenScene(path);
                r.Text("opened " + path);
            }
            else if (action == "save") { r.Text(EditorSceneManager.SaveOpenScenes() ? "open scenes saved" : "saving failed"); }
            else if (action != "list") throw new ToolException("action must be list, open or save");
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); r.Text("open: " + s.path + (s.isDirty ? " (unsaved changes)" : "")); }
            foreach (var s in EditorBuildSettings.scenes) r.Text("build settings: " + s.path + (s.enabled ? "" : " (disabled)"));
        }

        static void Assets(JObject a, ToolResponse r)
        {
            string filter = a.Str("filter");
            if (filter == null) throw new ToolException("filter is required, e.g. 't:VisualTreeAsset' or 't:Script Order'");
            string folder = a.Str("folder");
            if (folder != null) RequireProjectPath(folder);
            var guids = folder != null ? AssetDatabase.FindAssets(filter, new[] { folder }) : AssetDatabase.FindAssets(filter);
            int limit = a.Int("limit", 50);
            for (int i = 0; i < guids.Length && i < limit; i++) r.Text(AssetDatabase.GUIDToAssetPath(guids[i]));
            r.Text(guids.Length + " asset(s)" + (guids.Length > limit ? ", first " + limit + " shown" : "") + ". Files are plain text on disk under the project folder (UXML, USS, C#).");
        }

        /// <summary>Only paths inside this project (Assets/ or Packages/), never elsewhere on disk.</summary>
        static void RequireProjectPath(string path)
        {
            string p = path.Replace('\\', '/');
            bool inside = (p == "Assets" || p == "Packages" || p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal))
                          && !p.Contains("..") && !Path.IsPathRooted(p);
            if (!inside) throw new ToolException("'" + path + "' is outside this Unity project. Only paths under Assets/ or Packages/ are accepted.");
        }

        static void Menu(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            if (path == null) throw new ToolException("path is required");
            r.Text(EditorApplication.ExecuteMenuItem(path) ? "executed menu item: " + path : "menu item not found or disabled: " + path);
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using UDriver;

internal static class Program
{
    const string Instructions =
        "UDriver lets you see and operate a running Unity client. Typical flow: app_launch (or app_connect if the game/editor is already running) -> status -> " +
        "ui_tree / screenshot to see the screen -> act with ui_click, type_text, ui_set_value, ui_drag, ui_scroll, key -> wait_for to confirm the effect (pass sinceSeq from the action result). " +
        "To play the game itself (walk, jump, look around), learn the controls with input_actions and run a whole stretch in one play call. " +
        "Several games and editors can run at once: targets lists them; pass target=<name> when more than one of a kind is running. " +
        "To investigate a bug without trusting the UI: events_query (logs, exceptions, UI events), types_find / type_members to discover the code, state_get to read any object, " +
        "watch_add / trace_add / trigger_add to set up your own probes, frames_record + frames_get for frame-by-frame, time_control to pause/step. Memory: memory_summary -> memory_snapshot take -> play -> memory_snapshot diff -> memory_refs (who holds it); memory_watch for slow growth. Slow frames: render_stats -> render_scene -> screenshot camera=... " +
        "Element ids (#N) come from ui_tree / ui_find and go stale when the UI is rebuilt; ids >= 1000000 are uGUI objects, smaller ones UI Toolkit. Always back a verdict with evidence (events, state values, screenshots). " +
        "If the Unity editor is open, editor_* tools work even outside Play Mode: editor_status, editor_compile (compiler errors), editor_tests (run EditMode/PlayMode tests), editor_build, editor_play, editor_scene, editor_assets; " +
        "scene_tree / state_get / types_find also work in edit mode. Run self_test once in a new project to learn which capabilities work there.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static readonly Stream Stdout = Console.OpenStandardOutput();
    static readonly object WriteLock = new();

    // ---------------- targets ----------------

    internal sealed class Target
    {
        public string Name = "", Kind = "game", Path = "", Unity = "", Owner = "";
        public int Port, Pid;
        public bool Up, Live;
        public Process? Proc;
        public Registry.Game? Game;
    }

    static int EnvPort(string name, int def) => int.TryParse(Environment.GetEnvironmentVariable(name), out var p) ? p : def;
    static readonly int GamePortSetting = EnvPort("UDRIVER_PORT", ToolManifest.GamePort);
    static readonly Dictionary<int, string> Fixed = new()
    {
        [GamePortSetting] = "game",
        [EnvPort("UDRIVER_EDITOR_PORT", ToolManifest.EditorPort)] = "editor",
    };
    static readonly List<Target> Launched = new();

    /// <summary>One connected agent: its own last target, and the label Unity shows to other agents.</summary>
    internal sealed class Session
    {
        public string Label = "", Client = "", LastTarget = "", Name = "";
        public bool Local = true, Off;
        public int LastGame, LastEditor, Slot;
        public DateTime LastSeen, Written;
        public string WrittenTarget = "";
    }

    internal static readonly AsyncLocal<Session?> Current = new();
    static readonly AsyncLocal<CancellationToken> Cancel = new();
    internal static readonly Session Solo = new() { Label = "agent " + Environment.ProcessId };
    internal static Session Me => Current.Value ?? Solo;

    static void Used(Target t)
    {
        if (t.Kind == "editor") Me.LastEditor = t.Port; else Me.LastGame = t.Port;
        Me.LastTarget = t.Name;
    }
    static JsonArray? _liveTools;
    static string _liveSig = "";

    // ---------------- policy (set by a human in the MCP config) ----------------

    static readonly string[] BuildDirs = (Environment.GetEnvironmentVariable("UDRIVER_BUILDS_DIR") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => { try { return RealPath(d); } catch { return Path.GetFullPath(d); } }).ToArray();
    internal static bool ReadOnly = Environment.GetEnvironmentVariable("UDRIVER_READONLY") == "1";
    static readonly bool Timing = Environment.GetEnvironmentVariable("UDRIVER_TIMING") == "1";

    internal static int CallCount;
    internal static string LastTool = "";
    internal static DateTime LastCallUtc;
    static readonly HashSet<string> Disabled = new((Environment.GetEnvironmentVariable("UDRIVER_DISABLE_TOOLS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    static readonly HashSet<string> ObserveTools = new()
    {
        "app_status", "app_connect", "targets", "status", "ui_tree", "ui_find", "ui_inspect", "screenshot", "screen_pick", "locate", "scene_tree", "state_get",
        "state_query", "errors", "types_find", "type_members", "events_query", "wait_for", "input_actions", "frames_get", "probe_list",
        "profiler_get", "profiler_list", "memory_top", "memory_summary", "memory_snapshot", "memory_watch", "memory_refs", "render_stats", "render_scene",
        "editor_status", "editor_assets",
    };
    static readonly Dictionary<string, string[]> ObserveActions = new()
    {
        ["editor_compile"] = new[] { "status" }, ["editor_tests"] = new[] { "status", "results" }, ["editor_build"] = new[] { "status" },
        ["editor_scene"] = new[] { "list" }, ["time_control"] = new[] { "status" }, ["frames_record"] = new[] { "status" },
    };
    /// <summary>Everything else (state_set, state_call, trace_add, editor_build, editor_scene, editor_menu, new tools) needs full access.</summary>
    static readonly HashSet<string> PlayTools = new()
    {
        "app_launch", "app_kill", "ui_click", "ui_hover", "ui_drag", "pointer", "ui_scroll", "ui_scroll_to", "ui_set_value", "ui_focus", "type_text", "key", "play",
        "frames_record", "time_control", "events_config", "watch_add", "trigger_add", "probe_remove", "state_export", "memory_capture", "self_test",
        "editor_play", "editor_compile", "editor_tests",
    };

    static int Need(string name, JsonObject? args)
    {
        if (name == "batch" || ObserveTools.Contains(name)) return 0;
        if (ObserveActions.TryGetValue(name, out var ok) && ok.Contains((string?)args?["action"] ?? "status")) return 0;
        return PlayTools.Contains(name) ? 1 : 2;
    }

    /// <summary>The owner's access level for this game (UDriver panel); checked wherever a call reaches a game or editor.</summary>
    static string? Denied(string name, JsonObject? args, Target? t, Registry.Game? g = null)
    {
        g ??= t?.Game;
        string level = g?.Access ?? (t == null ? Registry.DefaultAccess : "none");
        if (Need(name, args) <= Registry.Level(level)) return null;
        string game = g?.Name ?? t?.Name ?? "this game";
        return level == "none" ? $"\"{game}\" is not allowed: the owner has not given agents access to it. Ask the owner to set its access in the UDriver panel."
            : $"\"{game}\" allows {level} only; ask the owner to raise access in the UDriver panel.";
    }

    static readonly HashSet<string> OffByDefault = new() { "editor_menu" };
    static readonly HashSet<string> Enabled = new((Environment.GetEnvironmentVariable("UDRIVER_ENABLE_TOOLS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    static readonly HashSet<string> SafeGameArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "-screen-width", "-screen-height", "-screen-fullscreen", "-screen-quality", "-popupwindow", "-window-mode", "-monitor",
        "-force-d3d11", "-force-d3d12", "-force-vulkan", "-force-glcore", "-force-metal", "-batchmode", "-nographics",
    };
    static readonly HashSet<string> ExtraGameArgs = new((Environment.GetEnvironmentVariable("UDRIVER_ALLOWED_ARGS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    internal static string RealPath(string path) => RealPath(path, 0);

    static string RealPath(string path, int depth)
    {
        string full = Path.GetFullPath(path);
        if (depth > 32) throw new IOException("too many levels of links: " + path);
        string cur = Path.GetPathRoot(full)!;
        foreach (var part in full.Substring(cur.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(cur, part);
            string? link = new FileInfo(next).LinkTarget;
            cur = link == null ? next : RealPath(Path.Combine(cur, link), depth + 1);
        }
        return cur;
    }

    static bool Approved(string path, out string real)
    {
        try { real = RealPath(path); } catch { real = ""; return false; }
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string r = real;
        return BuildDirs.Select(d => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(d => r.Equals(d, cmp) || r.StartsWith(d + Path.DirectorySeparatorChar, cmp));
    }

    /// <summary>The panel's tool switches apply to stdio agents under this one id.</summary>
    internal const string StdioClient = "local";
    /// <summary>The same for agents on the UDRIVER_URL_SECRET link.</summary>
    internal const string LinkClient = "link";

    static string? Blocked(string name, JsonObject? args) =>
        Registry.ToolOff(name, Me.Client.Length > 0 ? Me.Client : StdioClient) ? $"'{name}' is turned off in the UDriver panel." : Configured(name, args);

    /// <summary>Limits from the server's environment variables.</summary>
    static string? Configured(string name, JsonObject? args)
    {
        if (Disabled.Contains(name)) return $"'{name}' is disabled on this machine (UDRIVER_DISABLE_TOOLS).";
        if (OffByDefault.Contains(name) && !Enabled.Contains(name))
            return $"'{name}' is off by default because it can act outside the Unity project. A human can enable it with UDRIVER_ENABLE_TOOLS={name} in the MCP server configuration.";
        if (name == "editor_build" && args != null && (string?)args["action"] == "build")
        {
            string? outPath = (string?)args["path"];
            if (BuildDirs.Length == 0) return "editor_build is not enabled: a human must set UDRIVER_BUILDS_DIR (the folder builds may be written to) in the MCP server configuration.";
            if (outPath != null && Path.IsPathFullyQualified(outPath))
            {
                if (!Approved(outPath, out string real))
                    return $"editor_build refused: an absolute output path must be inside the approved build folders ({string.Join(", ", BuildDirs)}); a relative path or none builds inside the project.";
                args["path"] = real;
            }
        }
        if (!ReadOnly || ObserveTools.Contains(name)) return null;
        if (ObserveActions.TryGetValue(name, out var ok))
        {
            if (args == null) return null;
            string action = (string?)args["action"] ?? "status";
            if (ok.Contains(action)) return null;
            return $"read-only mode (UDRIVER_READONLY=1): '{name}' allows only action = {string.Join(" | ", ok)}.";
        }
        return $"read-only mode (UDRIVER_READONLY=1): '{name}' changes state and is not available. Only a human can change this in the MCP server configuration.";
    }

    // ---------------- access tokens ----------------

    static readonly Dictionary<int, string> Tokens = new();

    internal static readonly string HomeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver");

    static string TokenFile(int port) => Path.Combine(HomeDir, port + ".token");

    /// <summary>The target name given by app_launch, shared by all agents; valid only while the same process runs on the port.</summary>
    static string NameFile(int port) => Path.Combine(HomeDir, port + ".name");

    internal static string? LaunchName(int port, int pid)
    {
        try { var j = JsonNode.Parse(File.ReadAllText(NameFile(port))); return pid > 0 && (int?)j?["pid"] == pid ? (string?)j?["name"] : null; }
        catch { return null; }
    }

    internal static void PrivateDir(string dir)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir).SetAccessControl(OwnerOnly(new DirectorySecurity(), InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)); return; }
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, owner);
        File.SetUnixFileMode(dir, owner);
    }

    internal static void WritePrivate(string path, string text)
    {
        PrivateDir(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var opt = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) opt.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var fs = OperatingSystem.IsWindows()
                ? new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, OwnerOnly(new FileSecurity(), InheritanceFlags.None))
                : new FileStream(tmp, opt))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false))) w.Write(text);
            File.Move(tmp, path, true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>An access list that lets only the current Windows user in, ignoring rights inherited from parent folders.</summary>
    [SupportedOSPlatform("windows")]
    static T OwnerOnly<T>(T sec, InheritanceFlags inherit) where T : FileSystemSecurity
    {
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    static string TokenFor(int port, bool reload = false)
    {
        lock (Tokens)
        {
            if (!reload && Tokens.TryGetValue(port, out var t)) return t;
            try { t = File.ReadAllText(TokenFile(port)).Trim(); } catch { t = ""; }
            if (t.Length > 0) Tokens[port] = t;
            return t;
        }
    }

    /// <summary>Re-reads the token file once on 401: the game may have restarted.</summary>
    static async Task<(int status, string body)> Http2(HttpMethod method, int port, string path, string? json, double timeoutSec = 0)
    {
        string call = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
            req.Headers.TryAddWithoutValidation("X-UDriver-Token", TokenFor(port, attempt > 0));
            req.Headers.TryAddWithoutValidation("X-UDriver-Client", Me.Label);
            req.Headers.TryAddWithoutValidation("X-UDriver-Call", call);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Cancel.Value);
            if (timeoutSec > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                using var resp = await Http.SendAsync(req, cts.Token);
                string body = await resp.Content.ReadAsStringAsync(cts.Token);
                if ((int)resp.StatusCode == 401 && attempt == 0) continue;
                return ((int)resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (Cancel.Value.IsCancellationRequested && path == "/call")
            {
                _ = StopCall(port, call);
                throw;
            }
        }
    }

    /// <summary>Ends the call inside the game the same way its timeout does.</summary>
    static async Task StopCall(int port, string call)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/cancel");
            req.Headers.TryAddWithoutValidation("X-UDriver-Token", TokenFor(port));
            req.Content = new StringContent(new JsonObject { ["call"] = call }.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var _ = await Http.SendAsync(req, cts.Token);
        }
        catch { }
    }

    static ToolParam P(string n, string t, string d, bool req = false) => new() { Name = n, Type = t, Desc = d, Required = req };

    static readonly ToolDef[] LocalTools =
    {
        new() { Name = "app_launch", Desc = "Start a Unity build (Development Build with the UDriver package) and wait until its driver answers. Several builds can run at once, each on its own port. Returns the app status.",
            Params = new[]
            {
                P("path", "string", "The game to start: the name of a saved build from targets, or on Windows the .exe or its build folder, on Linux the game file (.x86_64) or its folder, on macOS the .app.", true),
                P("name", "string", "Target name for this copy, the same for every agent; must not be used by another running game (default: the product name, numbered -2, -3 when several copies run)."),
                P("args", "array", "Extra command-line arguments for the game, e.g. [\"-screen-width\",\"1280\",\"-screen-height\",\"720\",\"-screen-fullscreen\",\"0\"]."),
                P("port", "integer", $"Driver port (default: the first free one from {ToolManifest.GamePort})."),
                P("startup", "array", "Tool calls to run inside the game at boot, before the first scene - for probes that must exist from the start. Items: {\"tool\":\"trace_add\",\"args\":{...}}."),
                P("waitSec", "integer", "How long to wait for the driver (default 90)."),
            } },
        new() { Name = "app_kill", Desc = "Terminate a build started by app_launch through this server; target names the copy to stop.",
            Params = new[] { P("force", "boolean", "Also stop a build that another agent started.") } },
        new() { Name = "app_status", Desc = "Is the launched process alive and is its driver reachable?", Params = Array.Empty<ToolParam>() },
        new() { Name = "app_connect", Desc = "Attach to an already running game or editor Play Mode instead of launching one; waits for it to answer.",
            Params = new[] { P("port", "integer", "Game driver port of an instance missing from targets."), P("editorPort", "integer", "Editor bridge port of an instance missing from targets.") } },
        new() { Name = "targets", Desc = "Running Unity games and editors, one line each: name, kind, port, state, Unity version, build or project path. Pass the name as target to other tools.",
            Params = Array.Empty<ToolParam>() },
    };

    static readonly HashSet<string> ServerTools = new(LocalTools.Select(t => t.Name).Append("batch"));

    /// <summary>Must stay below LocalTools: static fields initialize in order.</summary>
    static readonly HashSet<string> KnownTools = new(LocalTools.Select(t => t.Name).Concat(ToolManifest.Tools.Select(t => t.Name)));

    internal static bool HttpOn;

    internal static async Task<JsonObject?> HandleMessage(JsonNode? msg)
    {
        string? method = (string?)msg?["method"];
        JsonNode? id = msg?["id"];
        if (method == null || id == null) return null;
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        try { reply["result"] = await Dispatch(method, msg!["params"]); }
        catch (RpcError e) { reply["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message }; }
        catch (OperationCanceledException) when (Cancel.Value.IsCancellationRequested) { reply["error"] = new JsonObject { ["code"] = -32800, ["message"] = "cancelled" }; }
        catch (Exception e) { reply["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.GetType().Name + ": " + e.Message }; }
        return reply;
    }

    static async Task Main(string[] args)
    {
        // Installer hooks. On Windows the first start after Setup opens the terminal screen; updates install only when asked.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => { if (OperatingSystem.IsWindows()) Environment.Exit(Cli.Run(["tui"]).GetAwaiter().GetResult()); }).Run();
        if (args is ["setup"]) { HttpMode.TerminalSetup(); return; }
        if (args is ["inject" or "restore", var gamePath]) { Environment.ExitCode = Inject.Run(args[0], gamePath); return; }
        if (args is ["tui" or "status" or "approve" or "deny" or "code" or "tools", ..]) { Environment.ExitCode = await Cli.Run(args); return; }
        if (int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_HTTP_PORT"), out int httpPort))
        {
            HttpOn = true;
            await HttpMode.Run(httpPort);
            return;
        }
        Registry.Claim(Solo);
        var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var running = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch { continue; }
            if ((string?)msg?["method"] == "notifications/cancelled")
            {
                if (msg["params"]?["requestId"] is JsonNode rid && running.TryGetValue(rid.ToJsonString(), out var stop)) try { stop.Cancel(); } catch { }
                continue;
            }
            string key = msg?["id"]?.ToJsonString() ?? "";
            var cts = new CancellationTokenSource();
            if (key.Length > 0) running[key] = cts;
            _ = Task.Run(async () =>
            {
                Cancel.Value = cts.Token;
                try
                {
                    var reply = await HandleMessage(msg);
                    if (reply != null && !cts.IsCancellationRequested) Send(reply);
                }
                finally { if (key.Length > 0) running.TryRemove(new(key, cts)); cts.Dispose(); }
            });
        }
        StopLaunched();
    }

    internal static void StopLaunched()
    {
        lock (Launched)
            foreach (var t in Launched)
            {
                try { if (!t.Proc!.HasExited) t.Proc.Kill(true); } catch { }
                try { if (LaunchName(t.Port, t.Proc!.Id) != null) File.Delete(NameFile(t.Port)); } catch { }
            }
    }

    sealed class RpcError : Exception
    {
        public readonly int Code;
        public RpcError(int code, string message) : base(message) { Code = code; }
    }

    static void Send(JsonNode node)
    {
        if (HttpOn) return;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        lock (WriteLock) { Stdout.Write(bytes, 0, bytes.Length); Stdout.Flush(); }
    }

    static void ToolsChanged()
    {
        _liveTools = null;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
    }

    static readonly string[] Versions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    static async Task<JsonNode> Dispatch(string method, JsonNode? prm)
    {
        if (method != "ping" && Registry.Revoked(Me))
            throw new RpcError(-32001, "UDriver: this agent was disconnected by the owner. Only the owner can connect it again, by restarting it.");
        switch (method)
        {
            case "initialize":
                if (prm?["clientInfo"]?["name"] is JsonValue agent && agent.TryGetValue<string>(out var agentName)) Registry.Rename(Me, agentName);
                return new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains((string?)prm?["protocolVersion"] ?? "") ? (string?)prm!["protocolVersion"] : Versions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = !HttpOn } },
                    ["serverInfo"] = new JsonObject { ["name"] = "udriver", ["version"] = ToolManifest.Version },
                    ["instructions"] = Instructions,
                };
            case "ping": return new JsonObject();
            case "resources/list": return new JsonObject { ["resources"] = new JsonArray() };
            case "prompts/list": return new JsonObject { ["prompts"] = new JsonArray() };
            case "tools/list": return new JsonObject { ["tools"] = await ListTools() };
            case "tools/call":
                string name = (string?)prm?["name"] ?? throw new RpcError(-32602, "missing tool name");
                var args = prm?["arguments"] as JsonObject ?? new JsonObject();
                string? want = args["target"]?.ToString();
                args.Remove("target");
                Interlocked.Increment(ref CallCount); LastTool = KnownTools.Contains(name) ? name : "(project tool)"; LastCallUtc = Me.LastSeen = DateTime.UtcNow;
                if (Blocked(name, args) is string refusal) return Result(refusal, true);
                if (HttpOn)
                {
                    // Remote connector proxies cut requests off after about 100 s.
                    if (IntArg(args, "timeoutMs", 0) > 60000) args["timeoutMs"] = 60000;
                    if (name == "app_launch" && IntArg(args, "waitSec", 90) > 60) args["waitSec"] = 60;
                }
                if (name == "batch" && args["calls"] is JsonArray inner)
                    foreach (var c in inner)
                        if (Blocked((string?)c?["tool"] ?? "", c?["args"] as JsonObject ?? new JsonObject()) is string r2) return Result("batch refused: " + r2, true);
                var sw = Timing ? Stopwatch.StartNew() : null;
                try { return await Run(name, args, want); }
                catch (Exception e) when (e is not RpcError) { return Result(e.GetType().Name + ": " + e.Message, true); }
                finally
                {
                    Registry.Touch(Me);
                    if (sw != null) Console.Error.WriteLine($"timing {name} {sw.Elapsed.TotalMilliseconds:0.0} ms");
                }
            default: throw new RpcError(-32601, "method not found: " + method);
        }
    }

    static async Task<JsonNode> Run(string name, JsonObject args, string? want) => name switch
    {
        "app_launch" => await Launch(args),
        "app_kill" => await Kill(want, args),
        "app_status" or "targets" => await ListTargets(want),
        "app_connect" => await Connect(args, want),
        "batch" => await Batch(args, want),
        _ => await CallUnity(name, args, want),
    };

    // ---------------- tool list ----------------

    static JsonObject TargetParam() => new()
    {
        ["type"] = "string",
        ["description"] = "Which game or editor to use: a name from targets. Optional while only one of the needed kind is running (then the last one used).",
    };

    static JsonObject ToolJson(ToolDef d)
    {
        var props = new JsonObject();
        var req = new JsonArray();
        foreach (var p in d.Params)
        {
            var o = new JsonObject { ["description"] = p.Desc };
            if (p.Type != "any") o["type"] = p.Type;
            if (p.Type == "array") o["items"] = new JsonObject();
            props[p.Name] = o;
            if (p.Required) req.Add(p.Name);
        }
        if (d.Name != "app_launch" && d.Name != "targets") props["target"] = TargetParam();
        return new JsonObject
        {
            ["name"] = d.Name, ["description"] = d.Desc,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req },
        };
    }

    static async Task<JsonArray> ListTools()
    {
        var tools = new JsonArray();
        var known = new HashSet<string>();
        int top = Registry.Games.Select(g => Registry.Level(g.Access)).Append(BuildDirs.Length > 0 ? Registry.Level(Registry.DefaultAccess) : -1).Max();
        foreach (var d in LocalTools.Concat(ToolManifest.Tools))
        {
            known.Add(d.Name);
            if (Blocked(d.Name, null) == null && (d.Name is "targets" or "app_status" || Need(d.Name, null) <= top)) tools.Add(ToolJson(d));
        }
        var live = _liveTools;
        if (live == null)
        {
            live = new JsonArray();
            foreach (var t in (await Targets()).Where(t => t.Kind == "game" && t.Up))
                try
                {
                    var (status, body) = await Http2(HttpMethod.Get, t.Port, "/tools", null, 1.5);
                    if (status == 200 && JsonNode.Parse(body)?["tools"] is JsonArray arr)
                        foreach (var x in arr.OfType<JsonObject>()) { var c = (JsonObject)x.DeepClone(); c["game"] = t.Game?.Id; live.Add(c); }
                }
                catch { }
            _liveTools = live;
        }
        foreach (var t in live)
        {
            string n = (string?)t?["name"] ?? "";
            if (t == null || Blocked(n, null) != null || Need(n, null) > Registry.Level(Registry.Games.FirstOrDefault(g => g.Id == (string?)t["game"])?.Access ?? "none") || !known.Add(n)) continue;
            var copy = t.DeepClone();
            copy.AsObject().Remove("game");
            if (copy["inputSchema"]?["properties"] is JsonObject props) props["target"] = TargetParam();
            tools.Add(copy);
        }
        return tools;
    }

    /// <summary>Every tool an agent may see, for the panel: the lowest access level it works at, and a fixed limit if one applies.</summary>
    internal static JsonArray ToolCatalog()
    {
        var list = new JsonArray();
        var seen = new HashSet<string>();
        var live = (_liveTools ?? new JsonArray()).Select(t => (Name: (string?)t?["name"] ?? "", Desc: (string?)t?["description"] ?? ""));
        foreach (var (n, desc) in LocalTools.Concat(ToolManifest.Tools).Select(d => (d.Name, d.Desc)).Concat(live))
            if (n.Length > 0 && seen.Add(n))
                list.Add(new JsonObject
                {
                    ["name"] = n, ["description"] = desc,
                    ["access"] = Registry.Levels[Need(n, null)], ["limit"] = Configured(n, null),
                });
        return list;
    }

    // ---------------- helpers ----------------

    static JsonObject Result(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    static int IntArg(JsonObject args, string key, int def)
    {
        var n = args[key];
        if (n == null) return def;
        try { return (int)Math.Round(n.GetValue<double>()); } catch { return int.TryParse(n.ToString(), out var v) ? v : def; }
    }

    internal static async Task<bool> Ping(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Http.GetStringAsync($"http://127.0.0.1:{port}/ping", cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>-1 when nothing answers, 0 when the driver does not report its process id.</summary>
    static async Task<int> PingPid(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            return (int?)JsonNode.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/ping", cts.Token))?["pid"] ?? 0;
        }
        catch { return -1; }
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    static bool Free(int port)
    {
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; }
    }

    internal static async Task<List<Target>> Targets()
    {
        var found = new Dictionary<int, Target>();
        lock (Fixed) foreach (var (port, kind) in Fixed) found[port] = new Target { Port = port, Kind = kind };
        string[] files;
        try { files = Directory.GetFiles(HomeDir, "*.json"); } catch { files = Array.Empty<string>(); }
        foreach (var f in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int port) || port < 1024 || port > 65535) continue;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f))!;
                found[port] = new Target
                {
                    Port = port, Kind = (string?)j["kind"] == "editor" ? "editor" : "game", Name = ((string?)j["name"] ?? "").Trim(),
                    Path = (string?)j["project"] ?? "", Unity = (string?)j["unity"] ?? "", Pid = (int?)j["pid"] ?? 0,
                };
            }
            catch { }
        }
        lock (Launched)
            foreach (var l in Launched)
            {
                if (!found.TryGetValue(l.Port, out var t)) found[l.Port] = t = new Target { Port = l.Port, Path = l.Path, Name = Path.GetFileNameWithoutExtension(l.Path) };
                t.Proc = l.Proc;
                t.Owner = l.Owner;
                if (l.Name.Length > 0) t.Name = l.Name;
            }
        var named = new HashSet<Target>();
        foreach (var t in found.Values)
            if (LaunchName(t.Port, t.Proc?.Id ?? t.Pid) is { Length: > 0 } n) { t.Name = n; named.Add(t); }
            else lock (Launched) if (Launched.Any(l => l.Port == t.Port && l.Name.Length > 0)) named.Add(t);
        var list = found.Values.OrderBy(t => t.Port).ToList();
        // Only a UDriver driver counts: it left a token and an info file, and answers with the live process id written there.
        await Task.WhenAll(list.Select(async t => t.Up = t.Pid > 0 && File.Exists(TokenFile(t.Port)) && await PingPid(t.Port) == t.Pid && Alive(t.Pid)));
        foreach (var t in list) t.Live = t.Up || (t.Proc != null ? !t.Proc.HasExited : t.Pid > 0 && Alive(t.Pid));
        list = list.Where(t => t.Live || t.Proc != null).ToList();
        Registry.Attach(list);
        // Names are unique per kind, so an editor and its Play Mode game share one; names given by app_launch keep theirs.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in list.OrderBy(t => named.Contains(t) ? 0 : 1))
        {
            string name = t.Name.Length > 0 ? t.Name : t.Kind;
            t.Name = name;
            for (int i = 2; !taken.Add(t.Kind + "/" + t.Name); i++) t.Name = name + "-" + i;
        }
        string sig = string.Join(",", list.Where(t => t.Kind == "game" && t.Up).Select(t => t.Port));
        if (sig != _liveSig) { _liveSig = sig; _liveTools = null; }
        return list;
    }

    static string Names(IEnumerable<Target> ts) => ts.Any() ? string.Join(", ", ts.Select(t => $"{t.Name} ({t.Kind})")) : "none";

    /// <summary>By target name, else by saved game while only one target of that kind uses it.</summary>
    static Target? Named(List<Target> all, string want, string kind = "")
    {
        var byName = all.Where(t => t.Name.Equals(want, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Kind == kind ? 0 : 1).FirstOrDefault();
        if (byName != null) return byName;
        var saved = all.Where(t => t.Game != null && (t.Game.Id == want || t.Game.Name.Equals(want, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.Kind == kind ? 0 : 1).ToList();
        return saved.Count(t => t.Kind == saved[0].Kind) == 1 ? saved[0] : null;
    }

    static Target? Pick(List<Target> all, string kind, string? want, out string? error)
    {
        error = null;
        if (want != null)
        {
            var t = Named(all, want, kind);
            if (t == null) error = $"No target named '{want}'. Targets: {Names(all)}.";
            return t;
        }
        var live = all.Where(t => t.Kind == kind && t.Up).ToList();
        if (live.Count == 0) live = all.Where(t => t.Kind == kind && t.Live).ToList();
        if (live.Count <= 1) return live.FirstOrDefault();
        var last = live.FirstOrDefault(t => t.Port == (kind == "editor" ? Me.LastEditor : Me.LastGame));
        if (last == null) error = $"Several {kind}s are running: {Names(live)}. Pass target with one of these names.";
        return last;
    }

    static string Line(Target t)
    {
        string state = t.Up ? "running" : t.Proc is { HasExited: true } p ? $"exited (code {p.ExitCode})" : t.Proc != null ? "starting" : "not answering";
        return $"{t.Name}  {t.Kind}  port {t.Port}  {state}" + (t.Proc != null ? $"  pid {t.Proc.Id} (app_launch by {(t.Owner == Me.Label ? "you" : t.Owner)})" : "")
               + "  " + AccessText(t.Game?.Access ?? "none") + (t.Game != null && t.Game.Name != t.Name ? $" (saved as {t.Game.Name})" : "")
               + (t.Unity.Length > 0 ? "  unity " + t.Unity : "") + (t.Path.Length > 0 ? "  " + t.Path : "");
    }

    static string AccessText(string access) => access == "none" ? "not allowed (the owner can set its access in the UDriver panel)" : "access " + access;

    static string Saved(Registry.Game g) => $"{g.Name}  {g.Kind}  not running  {AccessText(g.Access)}" + (g.Kind == "build" ? "  (app_launch path=" + g.Name + ")" : "  (open it in the Unity editor)") + "  " + g.Path;

    static async Task<JsonNode> ListTargets(string? want)
    {
        var all = await Targets();
        var idle = Registry.Games.Where(g => !all.Any(t => t.Game?.Id == g.Id)).ToList();
        if (want != null)
            return Named(all, want) is Target t ? Result(Line(t))
                : idle.FirstOrDefault(g => g.Id == want || g.Name.Equals(want, StringComparison.OrdinalIgnoreCase)) is Registry.Game g ? Result(Saved(g))
                : Result($"No target named '{want}'. Targets: {Names(all)}.", true);
        string text = all.Count == 0 ? "No Unity game or editor with the UDriver package is running." : string.Join("\n", all.Select(Line));
        return Result(idle.Count == 0 ? text : text + "\nSaved, not running:\n" + string.Join("\n", idle.Select(Saved)));
    }

    /// <summary>Null when nothing listens on the port.</summary>
    internal static async Task<JsonNode?> TryCall(int port, string name, JsonObject args)
    {
        var body = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() };
        int status; string text;
        try { (status, text) = await Http2(HttpMethod.Post, port, "/call", body.ToJsonString()); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !Cancel.Value.IsCancellationRequested) { return null; }
        if (status == 401)
            return Result($"The game/editor on port {port} rejected the access token. It was probably started by another user or the token file {TokenFile(port)} is not readable. Restart it, or launch the build with app_launch.", true);
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException)
        {
            return Result($"The Unity editor or game on port {port} dropped the call while reloading scripts (entering or leaving Play Mode, or after a code change). Repeat the call in a second or two.", true);
        }
        if (json?["content"] is JsonArray content)
            return new JsonObject { ["content"] = content.DeepClone(), ["isError"] = json["isError"]?.GetValue<bool>() ?? false };
        return Result((string?)json?["error"] ?? "unexpected response: " + text, true);
    }

    /// <summary>The bridge is offline during script reloads.</summary>
    static async Task<JsonNode> CallEditor(Target t, string name, JsonObject args)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var er = await TryCall(t.Port, name, args);
            if (er != null) { Used(t); return er; }
            if (sw.Elapsed.TotalSeconds > 25) break;
            await Task.Delay(700, Cancel.Value);
        }
        return Result($"The Unity editor bridge '{t.Name}' is not reachable on port {t.Port}. Is the editor open with the UDriver package installed and compiling without errors? (It is briefly offline during script reloads.)", true);
    }

    static async Task<JsonNode> CallUnity(string name, JsonObject args, string? want)
    {
        var all = await Targets();
        bool editorTool = name.StartsWith("editor_", StringComparison.Ordinal);
        var t = Pick(all, editorTool ? "editor" : "game", want, out var error);
        if (error != null) return Result(error, true);
        if (t != null && editorTool != (t.Kind == "editor") && (editorTool || !ToolManifest.EditModeTools.Contains(name)))
            return Result(editorTool ? $"'{t.Name}' is a game; {name} needs an editor. Targets: {Names(all)}."
                : $"'{t.Name}' is an editor; {name} needs a running game (editor_play action=play, or app_launch). Targets: {Names(all)}.", true);
        if (editorTool && t == null)
            return Result("No Unity editor with the UDriver package is open. Is the package installed and compiling without errors? (The editor is briefly offline during script reloads.)", true);
        if (t != null && Denied(name, args, t) is string no) return Result(no, true);
        if (t?.Kind == "editor") return await CallEditor(t, name, args);

        var res = t == null ? null : await TryCall(t.Port, name, args);
        if (res != null) { Used(t!); return res; }
        if (ToolManifest.EditModeTools.Contains(name) && Pick(all, "editor", null, out _) is Target ed && Denied(name, args, ed) == null && await TryCall(ed.Port, name, args) is JsonNode er)
        {
            Used(ed);
            return er;
        }
        string game = t == null ? "No game is running." : $"The game '{t.Name}' is not reachable on port {t.Port}."
            + (t.Proc is { HasExited: true } p ? $" The launched game has exited (code {p.ExitCode}) - it may have crashed." : "");
        if (all.Any(x => x.Kind == "editor" && x.Up))
            return Result($"{game} The Unity editor is open: enter Play Mode with editor_play action=play, or start a build with app_launch. " +
                          "Editor tools (editor_*) and inspection tools (scene_tree, state_get, types_find, type_members) work right now.", true);
        return Result($"{game} Start it with app_launch, or run the game / editor Play Mode and call app_connect.", true);
    }

    static bool GameTool(string tool) => !ServerTools.Contains(tool) && !tool.StartsWith("editor_", StringComparison.Ordinal);

    static string Step(JsonNode? c, string? want, out string? target)
    {
        target = c?["target"]?.ToString() ?? want;
        return (string?)c?["tool"] ?? "";
    }

    static async Task<JsonNode> Batch(JsonObject args, string? want)
    {
        if (args["calls"] is not JsonArray calls || calls.Count == 0) return Result("calls must be a non-empty array of {tool, args}", true);
        if (calls.Count > 50) return Result("at most 50 calls per batch", true);
        bool stop = args["stopOnError"] is not JsonValue v || !v.TryGetValue(out bool b) || b;
        string output = (string?)args["output"] ?? "all";
        if (output is not ("all" or "last" or "errors")) return Result("output must be all, last or errors", true);
        foreach (var c in calls)
            if (c is JsonObject step && step["args"] is JsonObject a && a["target"] is JsonNode at)
            {
                a.Remove("target");
                step["target"] ??= at;
            }
        var content = new JsonArray();
        bool failed = false;
        for (int i = 0; i < calls.Count;)
        {
            string tool = Step(calls[i], want, out string? target);
            int end = i + 1;
            JsonNode? res = null;
            Cancel.Value.ThrowIfCancellationRequested();
            if (GameTool(tool) && Pick(await Targets(), "game", target, out _) is { Kind: "game", Up: true } game && Denied(tool, calls[i]?["args"] as JsonObject, game) == null)
            {
                while (end < calls.Count && GameTool(Step(calls[end], want, out var next)) && next == target && Denied((string?)calls[end]?["tool"] ?? "", calls[end]?["args"] as JsonObject, game) == null) end++;
                var run = new JsonArray(calls.Skip(i).Take(end - i).Select(c => (JsonNode)new JsonObject { ["tool"] = (string?)c?["tool"], ["args"] = c?["args"]?.DeepClone() ?? new JsonObject(), ["quiet"] = c?["quiet"]?.DeepClone() }).ToArray());
                string part = output == "last" && end < calls.Count ? "errors" : output;
                res = await TryCall(game.Port, "batch", new JsonObject { ["calls"] = run, ["stopOnError"] = stop, ["first"] = i, ["output"] = part });
                if (res != null) Used(game);
            }
            bool here = res == null;
            if (here)
            {
                end = i + 1;
                res = tool == "batch" ? Result("batch cannot be nested", true) : await Run(tool, calls[i]?["args"] as JsonObject ?? new JsonObject(), target);
            }
            bool err = res!["isError"]?.GetValue<bool>() ?? false;
            bool quiet = calls[i]?["quiet"] is JsonValue q && q.TryGetValue(out bool qb) && qb;
            if (!here || err || output == "all" && !quiet || output == "last" && end == calls.Count)
            {
                if (here) content.Add(new JsonObject { ["type"] = "text", ["text"] = $"--- [{i}] {tool}" });
                foreach (var c in res["content"] as JsonArray ?? new JsonArray()) content.Add(c?.DeepClone());
            }
            failed |= err;
            if (err && stop)
            {
                if (here) content.Add(new JsonObject { ["type"] = "text", ["text"] = $"batch stopped at call {i} (stopOnError)" });
                break;
            }
            i = end;
        }
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = $"batch: all {calls.Count} calls succeeded" });
        return new JsonObject { ["content"] = content, ["isError"] = failed };
    }

    // ---------------- process tools ----------------

    internal static async Task<JsonNode> Launch(JsonObject args, bool byOwner = false)
    {
        string path = (string?)args["path"] ?? throw new ArgumentException("path is required");
        var g = Registry.Find(path);
        if (g is { Kind: "project" }) return Result($"'{g.Name}' is a Unity project, not a build: open it in the Unity editor, then use editor_play.", true);
        string real;
        if (g != null)
        {
            real = RealPath(g.Path);
            if (Directory.Exists(real))
                real = RealPath(real.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? Directory.GetFiles(Path.Combine(real, "Contents", "MacOS")).FirstOrDefault() ?? real : GameFile(real) ?? real);
            if (!File.Exists(real)) return Result($"The game file of '{g.Name}' was not found at {g.Path}. The owner can fix the path in the UDriver panel.", true);
        }
        else if (BuildDirs.Length == 0)
            return Result("app_launch is not enabled on this machine. A human must list the folder(s) that contain game builds in the MCP server configuration: " +
                          "environment variable UDRIVER_BUILDS_DIR (several folders separated by '" + Path.PathSeparator + "'). Until then, start the game manually and use app_connect.", true);
        else
        {
            string refused = $"refused: {path} is not a saved game and is outside the approved build folders ({string.Join(", ", BuildDirs)}). Only a human can change UDRIVER_BUILDS_DIR or add games in the UDriver panel.";
            if (!Approved(path, out real)) return Result(refused, true);
            if (Directory.Exists(real))
            {
                path = real.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetFiles(Path.Combine(real, "Contents", "MacOS")).FirstOrDefault() ?? real
                    : GameFile(real) ?? real;
                if (!Approved(path, out real)) return Result(refused, true);
            }
            if (!File.Exists(real)) return Result(Directory.Exists(real) ? "no Unity game file (NAME.exe or NAME.x86_64 next to a NAME_Data folder) was found in " + path : "file not found: " + path, true);
            g = Registry.ForPath(real);
        }
        if (!byOwner && Denied("app_launch", args, null, g) is string no) return Result(no, true);
        if (Inject.Ensure(real) is string cannot) return Result(cannot, true);
        if (args["args"] is JsonArray given)
        {
            string? prev = null;
            foreach (var x in given)
            {
                string a = x?.ToString() ?? "";
                if (a.StartsWith('-'))
                {
                    if (!SafeGameArgs.Contains(a) && !ExtraGameArgs.Contains(a))
                        return Result($"refused: game argument '{a}' is not on the allowed list ({string.Join(" ", SafeGameArgs)}). A human can allow more names with UDRIVER_ALLOWED_ARGS.", true);
                    prev = a;
                }
                else
                {
                    bool plain = a.Length > 0 && a.Length <= 32 && a.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_');
                    if (prev == null || (!ExtraGameArgs.Contains(prev) && !plain))
                        return Result($"refused: value '{a}' is not accepted for a game argument (only short plain values after an allowed switch; no paths).", true);
                    prev = null;
                }
            }
        }
        int lp;
        if (args["port"] != null)
        {
            lp = IntArg(args, "port", 0);
            if (lp < 1024 || lp > 65535) return Result("refused: port must be between 1024 and 65535", true);
            if (await Ping(lp))
                return Result($"Something already answers on port {lp}. Use app_connect to attach, or leave out port to get a free one.", true);
        }
        else
        {
            var used = (await Targets()).Select(t => t.Port).ToHashSet();
            lp = ToolManifest.Ports(false).Prepend(GamePortSetting).FirstOrDefault(p => !used.Contains(p) && Free(p));
            if (lp == 0) return Result($"No free driver port from {ToolManifest.GamePort} to {ToolManifest.GamePortLast}: stop a game with app_kill first.", true);
        }

        string label = ((string?)args["name"])?.Trim() ?? "";
        if (label.Length > 64 || label.Any(char.IsControl)) return Result("name must be at most 64 characters on one line", true);
        if (label.Length > 0 && (await Targets()).Any(t => t.Kind == "game" && t.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
            return Result($"The name '{label}' is already used by a running game (see targets). Choose another name.", true);

        // Start exactly the file that was checked, not a path whose links could point elsewhere.
        var psi = new ProcessStartInfo(real)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(real)!,
            // stdout is the MCP channel.
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("UDRIVER_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(key);
        if (Environment.GetEnvironmentVariable("UDRIVER_EXPORTS_MAX_MB") is { Length: > 0 } exportsMax) psi.Environment["UDRIVER_EXPORTS_MAX_MB"] = exportsMax;
        // Through the environment, not the command line, which other users can see.
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        psi.Environment["UDRIVER_TOKEN"] = token;
        lock (Tokens) Tokens[lp] = token;
        psi.ArgumentList.Add("-udriver-port");
        psi.ArgumentList.Add(lp.ToString());
        string? startupFile = null;
        if (args["startup"] is JsonArray startup && startup.Count > 0)
        {
            foreach (var c in startup)
                if (((string?)c?["tool"] == "batch" ? "batch cannot run at startup; list the calls one by one" : Blocked((string?)c?["tool"] ?? "", c?["args"] as JsonObject ?? new JsonObject()) ?? (byOwner ? null : Denied((string?)c?["tool"] ?? "", c?["args"] as JsonObject, null, g))) is string r3)
                    return Result("startup refused: " + r3, true);
            startupFile = Path.Combine(HomeDir, "startup-" + Guid.NewGuid().ToString("N") + ".json");
            WritePrivate(startupFile, startup.ToJsonString());
            psi.ArgumentList.Add("-udriver-startup");
            psi.ArgumentList.Add(startupFile);
        }
        if (args["args"] is JsonArray extra)
            foreach (var x in extra) if (x != null) psi.ArgumentList.Add(x.ToString());

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start the process");
        Registry.Started(real, proc);
        if (label.Length > 0) WritePrivate(NameFile(lp), new JsonObject { ["name"] = label, ["pid"] = proc.Id }.ToJsonString());
        lock (Launched)
        {
            Launched.RemoveAll(t => t.Port == lp);
            Launched.Add(new Target { Port = lp, Path = real, Name = label, Proc = proc, Owner = Me.Label });
        }
        int launchedPid = proc.Id;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            try { if (startupFile != null) File.Delete(startupFile); } catch { }
            try { if (LaunchName(lp, launchedPid) != null) File.Delete(NameFile(lp)); } catch { }
        };
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        int waitSec = IntArg(args, "waitSec", 90);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < waitSec)
        {
            if (proc.HasExited) return Result($"The game exited during startup with code {proc.ExitCode}. Check its log: {PlayerLog(real)}", true);
            int pid = await PingPid(lp);
            if (pid > 0 && pid != proc.Id)
            {
                try { proc.Kill(true); } catch { }
                lock (Launched) Launched.RemoveAll(t => t.Proc == proc);
                lock (Tokens) Tokens.Remove(lp);
                return Result($"Another game took port {lp} at the same moment, so this one was stopped. Call app_launch again.", true);
            }
            if (pid >= 0)
            {
                ToolsChanged();
                var status = await TryCall(lp, "status", new JsonObject());
                string text = (string?)status?["content"]?[0]?["text"] ?? "";
                string name = (await Targets()).FirstOrDefault(t => t.Port == lp)?.Name ?? "";
                // Keeps the number of an unnamed copy when others start or stop.
                if (name.Length > 0) WritePrivate(NameFile(lp), new JsonObject { ["name"] = name, ["pid"] = proc.Id }.ToJsonString());
                Me.LastGame = lp; Me.LastTarget = name;
                return Result($"Launched '{name}' (pid {proc.Id}) and connected in {sw.Elapsed.TotalSeconds:0.0}s on port {lp}.\n{text}");
            }
            await Task.Delay(400);
        }
        return Result($"The game started (pid {proc.Id}) but its driver did not answer on port {lp} within {waitSec}s. " +
                      "Check that this is a Development Build containing the UDriver package (the driver stays off in release builds), then retry app_connect.", true);
    }

    /// <summary>The file named after its NAME_Data folder, so UnityCrashHandler*.exe is skipped.</summary>
    internal static string? GameFile(string dir) => Directory.GetDirectories(dir, "*_Data")
        .SelectMany(d => new[] { ".exe", ".x86_64", ".x86", "" }.Select(ext => d[..^5] + ext))
        .FirstOrDefault(File.Exists);

    static string PlayerLog(string exe)
    {
        string data = OperatingSystem.IsMacOS() ? Path.GetFullPath(Path.Combine(exe, "..", "..", "Resources", "Data")) : Path.ChangeExtension(exe, null) + "_Data";
        string[] info = { "<company>", "<product>" };
        try { var l = File.ReadAllLines(Path.Combine(data, "app.info")); if (l.Length >= 2) info = l; } catch { }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = OperatingSystem.IsWindows() ? Path.Combine(home, "AppData", "LocalLow")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Logs")
            : Path.Combine(home, ".config", "unity3d");
        return Path.Combine(dir, info[0].Trim(), info[1].Trim(), "Player.log");
    }

    static async Task<JsonNode> Kill(string? want, JsonObject args)
    {
        var all = await Targets();
        var mine = all.Where(t => t.Proc != null).ToList();
        var running = mine.Where(t => !t.Proc!.HasExited).ToList();
        var k = want != null ? Named(all, want, "game") : running.Count == 1 ? running[0] : running.FirstOrDefault(t => t.Port == Me.LastGame);
        if (k?.Proc == null)
        {
            if (want != null) return Result((k == null ? $"No target named '{want}'." : $"'{k.Name}' was not started with app_launch by this server, so it cannot stop it.") + $" Started here: {Names(mine)}.", true);
            if (running.Count == 0) return Result("No running process was started by this server.");
            return Result($"Several builds are running: {Names(running)}. Pass target with one of these names.", true);
        }
        if (Denied("app_kill", args, k) is string no) return Result(no, true);
        bool other = k.Owner != Me.Label;
        if (other && !(args["force"] is JsonValue fv && fv.TryGetValue(out bool force) && force))
            return Result($"'{k.Name}' was started by {k.Owner}, not by you. Pass force=true to stop it anyway.", true);
        if (!k.Proc!.HasExited) { k.Proc.Kill(true); k.Proc.WaitForExit(5000); }
        lock (Launched) Launched.RemoveAll(t => t.Port == k.Port);
        _liveTools = null;
        return Result($"'{k.Name}' terminated." + (other ? $" It had been started by {k.Owner}." : ""));
    }

    static async Task<JsonNode> Connect(JsonObject args, string? want)
    {
        // Only ports where a UDriver instance has published a token: never poke arbitrary local services.
        foreach (var (key, kind) in new[] { ("port", "game"), ("editorPort", "editor") })
        {
            if (args[key] == null) continue;
            int p = IntArg(args, key, 0);
            lock (Fixed)
            {
                if (!Fixed.ContainsKey(p) && TokenFor(p, true).Length == 0) return Result($"refused: no UDriver instance has registered on port {p} (no token file).", true);
                Fixed[p] = kind;
            }
            if (kind == "game") Me.LastGame = p; else Me.LastEditor = p;
        }
        bool Ready(List<Target> ts) => ts.Any(t => t.Up && (want != null ? t.Name.Equals(want, StringComparison.OrdinalIgnoreCase) : t.Kind == "game"));
        var sw = Stopwatch.StartNew();
        List<Target> all;
        while (!Ready(all = await Targets()) && sw.Elapsed.TotalSeconds < 12) await Task.Delay(500);
        if (!Ready(all))
            return Result((want != null ? $"'{want}' does not answer." : "No game answers.") + (all.Any(t => t.Kind == "editor" && t.Up)
                ? " The editor bridge is reachable: the editor is open but not in Play Mode (editor_play action=play), or still loading."
                : " Is the game (Development Build) or editor Play Mode running with the UDriver package?") + $" Targets: {Names(all)}.", true);
        ToolsChanged();
        return await CallUnity("status", new JsonObject(), want);
    }
}

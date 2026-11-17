// UDriver MCP server: speaks MCP over stdio, launches the Unity build and forwards tool calls
// to the in-game driver at http://127.0.0.1:<port>. No external packages required.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using UDriver;

internal static class Program
{
    const string Instructions =
        "UDriver lets you see and operate a running Unity client. Typical flow: app_launch (or app_connect if the game/editor is already running) -> status -> " +
        "ui_tree / screenshot to see the screen -> act with ui_click, type_text, ui_set_value, ui_drag, ui_scroll, key -> wait_for to confirm the effect (pass sinceSeq from the action result). " +
        "To investigate a bug without trusting the UI: events_query (logs, exceptions, UI events), types_find / type_members to discover the code, state_get to read any object, " +
        "watch_add / trace_add / trigger_add to set up your own probes, frames_record + frames_get for frame-by-frame, time_control to pause/step, profiler_get / memory_top for performance. " +
        "Element ids (#N) come from ui_tree / ui_find and go stale when the UI is rebuilt; ids >= 1000000 are uGUI objects, smaller ones UI Toolkit. Always back a verdict with evidence (events, state values, screenshots). " +
        "If the Unity editor is open, editor_* tools work even outside Play Mode: editor_status, editor_compile (compiler errors), editor_tests (run EditMode/PlayMode tests), editor_build, editor_play, editor_scene, editor_assets; " +
        "scene_tree / state_get / types_find also work in edit mode. Run self_test once in a new project to learn which capabilities work there.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static readonly Stream Stdout = Console.OpenStandardOutput();
    static readonly object WriteLock = new();
    static int _port = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_PORT"), out var p) ? p : 17890;
    static int _editorPort = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_EDITOR_PORT"), out var ep) ? ep : 17891;
    static Process? _proc;

    static string Base => $"http://127.0.0.1:{_port}";

    // ---------------- policy (set by the human in the MCP config; the model cannot change it) ----------------

    static readonly string[] BuildDirs = (Environment.GetEnvironmentVariable("UDRIVER_BUILDS_DIR") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(d => { try { return RealPath(d); } catch { return Path.GetFullPath(d); } }).ToArray();
    internal static bool ReadOnly = Environment.GetEnvironmentVariable("UDRIVER_READONLY") == "1";   // can be switched from the control panel

    // Shown on the control panel.
    internal static int CallCount;
    internal static string LastTool = "";
    internal static DateTime LastCallUtc;
    internal static int GamePort => _port;
    internal static int EditorPort => _editorPort;
    static readonly HashSet<string> Disabled = new((Environment.GetEnvironmentVariable("UDRIVER_DISABLE_TOOLS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Tools that only observe. In read-only mode nothing else is offered or executed.
    static readonly HashSet<string> ObserveTools = new()
    {
        "app_status", "app_connect", "status", "ui_tree", "ui_find", "ui_inspect", "screenshot", "screen_pick", "locate", "scene_tree", "state_get",
        "types_find", "type_members", "events_query", "events_config", "wait_for", "frames_record", "frames_get", "watch_add", "probe_list", "probe_remove",
        "profiler_get", "profiler_list", "memory_top", "editor_status", "editor_assets",
    };
    // Tools that are read-only for some actions only.
    static readonly Dictionary<string, string[]> ObserveActions = new()
    {
        ["editor_compile"] = new[] { "status" }, ["editor_tests"] = new[] { "status", "results" }, ["editor_build"] = new[] { "status" },
        ["editor_scene"] = new[] { "list" }, ["time_control"] = new[] { "status" },
    };

    // Tools that can reach beyond the Unity project; off unless a human enables them by name.
    static readonly HashSet<string> OffByDefault = new() { "editor_menu" };
    static readonly HashSet<string> Enabled = new((Environment.GetEnvironmentVariable("UDRIVER_ENABLE_TOOLS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Command-line switches the model may pass to a launched build: window/graphics only, nothing that takes a file path.
    static readonly HashSet<string> SafeGameArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "-screen-width", "-screen-height", "-screen-fullscreen", "-screen-quality", "-popupwindow", "-window-mode", "-monitor",
        "-force-d3d11", "-force-d3d12", "-force-vulkan", "-force-glcore", "-force-metal", "-batchmode", "-nographics",
    };
    static readonly HashSet<string> ExtraGameArgs = new((Environment.GetEnvironmentVariable("UDRIVER_ALLOWED_ARGS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Absolute path with '..' removed and links resolved in every component (parts that do not exist yet are kept as given).</summary>
    static string RealPath(string path) => RealPath(path, 0);

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

    /// <summary>Resolves links and '..' and tells whether the path lies inside a human-approved build folder.</summary>
    static bool Approved(string path, out string real)
    {
        try { real = RealPath(path); } catch { real = ""; return false; }
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string r = real;
        // An approved entry may also be a single macOS .app bundle (build-only setup approves just that game).
        return BuildDirs.Select(d => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(d => r.Equals(d, cmp) || r.StartsWith(d + Path.DirectorySeparatorChar, cmp));
    }

    /// <summary>Why a call is refused by policy, or null if allowed.</summary>
    static string? Blocked(string name, JsonObject? args)
    {
        if (Disabled.Contains(name)) return $"'{name}' is disabled on this machine (UDRIVER_DISABLE_TOOLS).";
        if (OffByDefault.Contains(name) && !Enabled.Contains(name))
            return $"'{name}' is off by default because it can act outside the Unity project. A human can enable it with UDRIVER_ENABLE_TOOLS={name} in the MCP server configuration.";
        if (name == "editor_build" && args != null && (string?)args["action"] == "build")
        {
            string? outPath = (string?)args["path"];
            if (BuildDirs.Length == 0) return "editor_build is not enabled: a human must set UDRIVER_BUILDS_DIR (the folder builds may be written to) in the MCP server configuration.";
            if (outPath == null || !Path.IsPathRooted(outPath) || !Approved(outPath, out _))
                return $"editor_build refused: the output path must be an absolute path inside the approved build folders ({string.Join(", ", BuildDirs)}).";
        }
        if (!ReadOnly || ObserveTools.Contains(name)) return null;
        if (ObserveActions.TryGetValue(name, out var ok))
        {
            if (args == null) return null; // listing: the tool exists, with limited actions
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

    /// <summary>Writes a file only this user can read (0600 in a 0700 folder on macOS/Linux), atomically: the content never sits in a file with wider permissions.</summary>
    internal static void WritePrivate(string path, string text)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir); File.WriteAllText(path, text); return; }
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, owner);
        File.SetUnixFileMode(dir, owner);   // also tightens a folder created earlier with default permissions
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false))) w.Write(text);
            File.Move(tmp, path, true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static string TokenFor(int port, bool reload = false)
    {
        if (!reload && Tokens.TryGetValue(port, out var t)) return t;
        try { t = File.ReadAllText(TokenFile(port)).Trim(); } catch { t = ""; }
        if (t.Length > 0) Tokens[port] = t;
        return t;
    }

    /// <summary>HTTP call to the game/editor with the access token; re-reads the token file once if the game was restarted.</summary>
    static async Task<(int status, string body)> Http2(HttpMethod method, int port, string path, string? json, double timeoutSec = 0)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
            req.Headers.TryAddWithoutValidation("X-UDriver-Token", TokenFor(port, attempt > 0));
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)) : new CancellationTokenSource();
            using var resp = await Http.SendAsync(req, cts.Token);
            string body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 401 && attempt == 0) continue;
            return ((int)resp.StatusCode, body);
        }
    }

    static ToolParam P(string n, string t, string d, bool req = false) => new() { Name = n, Type = t, Desc = d, Required = req };

    static readonly ToolDef[] LocalTools =
    {
        new() { Name = "app_launch", Desc = "Start a Unity build (Development Build with the UDriver package) and wait until its driver answers. Returns the app status.",
            Params = new[]
            {
                P("path", "string", "Path to the executable (.exe, Linux binary, or macOS .app).", true),
                P("args", "array", "Extra command-line arguments for the game, e.g. [\"-screen-width\",\"1280\",\"-screen-height\",\"720\",\"-screen-fullscreen\",\"0\"]."),
                P("port", "integer", "Driver port (default 17890 or $UDRIVER_PORT)."),
                P("startup", "array", "Tool calls to run inside the game at boot, before the first scene - for probes that must exist from the start. Items: {\"tool\":\"trace_add\",\"args\":{...}}."),
                P("waitSec", "integer", "How long to wait for the driver (default 90)."),
            } },
        new() { Name = "app_kill", Desc = "Terminate the build started by app_launch.", Params = Array.Empty<ToolParam>() },
        new() { Name = "app_status", Desc = "Is the launched process alive and is the in-game driver reachable?", Params = Array.Empty<ToolParam>() },
        new() { Name = "app_connect", Desc = "Attach to an already running game or editor Play Mode instead of launching one.",
            Params = new[] { P("port", "integer", "Game driver port (default 17890; if nothing answers there, a running game on another port is found automatically)."), P("editorPort", "integer", "Editor bridge port (default 17891).") } },
    };

    // Names the control panel may show; any other name is shown generically. Must stay below LocalTools (static fields initialize in order).
    static readonly HashSet<string> KnownTools = new(LocalTools.Select(t => t.Name).Concat(ToolManifest.Tools.Select(t => t.Name)));

    /// <summary>True when serving MCP over HTTP (remote connector) instead of stdio.</summary>
    internal static bool HttpOn;

    /// <summary>Handles one JSON-RPC message; returns the reply, or null for notifications.</summary>
    internal static async Task<JsonObject?> HandleMessage(JsonNode? msg)
    {
        string? method = (string?)msg?["method"];
        JsonNode? id = msg?["id"];
        if (method == null || id == null) return null;
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        try { reply["result"] = await Dispatch(method, msg!["params"]); }
        catch (RpcError e) { reply["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message }; }
        catch (Exception e) { reply["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.GetType().Name + ": " + e.Message }; }
        return reply;
    }

    static async Task Main()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_HTTP_PORT"), out int httpPort))
        {
            HttpOn = true;
            await HttpMode.Run(httpPort);
            return;
        }
        var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch { continue; }
            var reply = await HandleMessage(msg);
            if (reply != null) Send(reply); // notifications and responses need no answer
        }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
    }

    sealed class RpcError : Exception
    {
        public readonly int Code;
        public RpcError(int code, string message) : base(message) { Code = code; }
    }

    static void Send(JsonNode node)
    {
        if (HttpOn) return; // no server-initiated messages over plain request/response HTTP
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        lock (WriteLock) { Stdout.Write(bytes, 0, bytes.Length); Stdout.Flush(); }
    }

    static readonly string[] Versions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    static async Task<JsonNode> Dispatch(string method, JsonNode? prm)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains((string?)prm?["protocolVersion"] ?? "") ? (string?)prm!["protocolVersion"] : Versions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = !HttpOn } },
                    ["serverInfo"] = new JsonObject { ["name"] = "udriver", ["version"] = "0.1.0" },
                    ["instructions"] = Instructions,
                };
            case "ping": return new JsonObject();
            case "resources/list": return new JsonObject { ["resources"] = new JsonArray() };
            case "prompts/list": return new JsonObject { ["prompts"] = new JsonArray() };
            case "tools/list": return new JsonObject { ["tools"] = await ListTools() };
            case "tools/call":
                string name = (string?)prm?["name"] ?? throw new RpcError(-32602, "missing tool name");
                var args = prm?["arguments"] as JsonObject ?? new JsonObject();
                CallCount++; LastTool = KnownTools.Contains(name) ? name : "(project tool)"; LastCallUtc = DateTime.UtcNow;
                if (Blocked(name, args) is string refusal) return Result(refusal, true);
                if (HttpOn)
                {
                    // Proxies in front of a remote connector cut requests off after about 100 s: keep every call well below that.
                    if (IntArg(args, "timeoutMs", 0) > 60000) args["timeoutMs"] = 60000;
                    if (name == "app_launch" && IntArg(args, "waitSec", 90) > 60) args["waitSec"] = 60;
                }
                if (name == "batch" && args["calls"] is JsonArray inner)
                    foreach (var c in inner)
                        if (Blocked((string?)c?["tool"] ?? "", c?["args"] as JsonObject ?? new JsonObject()) is string r2) return Result("batch refused: " + r2, true);
                try
                {
                    return name switch
                    {
                        "app_launch" => await Launch(args),
                        "app_kill" => Kill(),
                        "app_status" => await AppStatus(),
                        "app_connect" => await Connect(args),
                        _ => await CallUnity(name, args),
                    };
                }
                catch (Exception e) when (e is not RpcError) { return Result(e.GetType().Name + ": " + e.Message, true); }
            default: throw new RpcError(-32601, "method not found: " + method);
        }
    }

    // ---------------- tool list ----------------

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
        return new JsonObject
        {
            ["name"] = d.Name, ["description"] = d.Desc,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req },
        };
    }

    static async Task<JsonArray> ListTools()
    {
        var tools = new JsonArray();
        foreach (var d in LocalTools) if (Blocked(d.Name, null) == null) tools.Add(ToolJson(d));
        // The built-in manifest is always advertised, so the list is complete even before the game starts.
        var known = new HashSet<string>();
        foreach (var d in ToolManifest.Tools) { known.Add(d.Name); if (Blocked(d.Name, null) == null) tools.Add(ToolJson(d)); }
        try
        {
            // Project-specific tools registered in the game through Driver.Register(ToolDef, handler).
            var (status, body) = await Http2(HttpMethod.Get, _port, "/tools", null, 1.5);
            if (status == 200 && JsonNode.Parse(body)?["tools"] is JsonArray live)
                foreach (var t in live)
                    if (t != null && !known.Contains((string?)t["name"] ?? "") && Blocked((string?)t["name"] ?? "", null) == null) tools.Add(t.DeepClone());
        }
        catch { }
        return tools;
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

    static Task<bool> Ping() => Ping(_port);


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

    /// <summary>Calls a tool on the given port. Returns null when nothing is listening there.</summary>
    static async Task<JsonNode?> TryCall(int port, string name, JsonObject args)
    {
        var body = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() };
        int status; string text;
        try { (status, text) = await Http2(HttpMethod.Post, port, "/call", body.ToJsonString()); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return null; }
        if (status == 401)
            return Result($"The game/editor on port {port} rejected the access token. It was probably started by another user or the token file {TokenFile(port)} is not readable. Restart it, or launch the build with app_launch.", true);
        var json = JsonNode.Parse(text);
        if (json?["content"] is JsonArray content)
            return new JsonObject { ["content"] = content.DeepClone(), ["isError"] = json["isError"]?.GetValue<bool>() ?? false };
        return Result((string?)json?["error"] ?? "unexpected response: " + text, true);
    }

    static async Task<JsonNode> CallUnity(string name, JsonObject args)
    {
        if (name.StartsWith("editor_", StringComparison.Ordinal))
        {
            // The editor bridge restarts on every script reload (compile, entering Play Mode): retry for a while.
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var er = await TryCall(_editorPort, name, args);
                if (er != null) return er;
                if (sw.Elapsed.TotalSeconds > 25) break;
                await Task.Delay(700);
            }
            return Result($"The Unity editor bridge is not reachable on port {_editorPort}. Is the editor open with the UDriver package installed and compiling without errors? (It is briefly offline during script reloads.)", true);
        }

        var res = await TryCall(_port, name, args);
        if (res != null) return res;
        if (ToolManifest.EditModeTools.Contains(name))
        {
            var er = await TryCall(_editorPort, name, args);
            if (er != null) return er;
        }
        string why = _proc is { HasExited: true } ? $" The launched game has exited (code {_proc.ExitCode}) - it may have crashed." : "";
        if (await Ping(_editorPort))
            return Result($"The game is not running (nothing on port {_port}).{why} The Unity editor is open: enter Play Mode with editor_play action=play and then app_connect, or start a build with app_launch. " +
                          "Editor tools (editor_*) and inspection tools (scene_tree, state_get, types_find, type_members) work right now.", true);
        return Result($"The Unity app is not reachable on port {_port}.{why} Start it with app_launch, or run the game / editor Play Mode and call app_connect.", true);
    }

    // ---------------- process tools ----------------

    internal static async Task<JsonNode> Launch(JsonObject args)
    {
        string path = (string?)args["path"] ?? throw new ArgumentException("path is required");
        // The model may only start executables from folders a human has approved. Checked before touching the file system at all.
        if (BuildDirs.Length == 0)
            return Result("app_launch is not enabled on this machine. A human must list the folder(s) that contain game builds in the MCP server configuration: " +
                          "environment variable UDRIVER_BUILDS_DIR (several folders separated by '" + Path.PathSeparator + "'). Until then, start the game manually and use app_connect.", true);
        string refused = $"refused: {path} is outside the approved build folders ({string.Join(", ", BuildDirs)}). Only a human can change UDRIVER_BUILDS_DIR.";
        if (!Approved(path, out string real)) return Result(refused, true);
        if (real.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(real))
        {
            path = Directory.GetFiles(Path.Combine(real, "Contents", "MacOS")).FirstOrDefault() ?? real;
            if (!Approved(path, out real)) return Result(refused, true);
        }
        if (!File.Exists(real)) return Result("file not found: " + path, true);
        // Game arguments: only known window/graphics switches with plain values. Unity switches such as -logFile take a file path and could overwrite any file.
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
        int lp = IntArg(args, "port", _port);
        if (lp < 1024 || lp > 65535) return Result("refused: port must be between 1024 and 65535", true);
        _port = lp;
        if (await Ping())
            return Result($"Something already answers on port {_port} (a game instance is running). Use app_connect to attach, app_kill to stop the one this server started, or pass another port.", true);

        // Start exactly the file that was checked, not a path whose links could point elsewhere.
        var psi = new ProcessStartInfo(real)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(real)!,
            // The game must not inherit our stdio: stdout is the MCP channel.
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Fresh secret for this run, handed over through the environment (not the command line, which other users can see).
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        psi.Environment["UDRIVER_TOKEN"] = token;
        Tokens[_port] = token;
        psi.ArgumentList.Add("-udriver-port");
        psi.ArgumentList.Add(_port.ToString());
        string? startupFile = null;
        if (args["startup"] is JsonArray startup && startup.Count > 0)
        {
            foreach (var c in startup)
                if (Blocked((string?)c?["tool"] ?? "", c?["args"] as JsonObject ?? new JsonObject()) is string r3) return Result("startup refused: " + r3, true);
                startupFile = Path.Combine(HomeDir, "startup-" + Guid.NewGuid().ToString("N") + ".json");
            WritePrivate(startupFile, startup.ToJsonString());
            psi.ArgumentList.Add("-udriver-startup");
            psi.ArgumentList.Add(startupFile);
        }
        if (args["args"] is JsonArray extra)
            foreach (var x in extra) if (x != null) psi.ArgumentList.Add(x.ToString());

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start the process");
        if (startupFile != null)
        {
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) => { try { File.Delete(startupFile); } catch { } };
        }
        _proc.OutputDataReceived += (_, _) => { };
        _proc.ErrorDataReceived += (_, _) => { };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        int waitSec = IntArg(args, "waitSec", 90);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < waitSec)
        {
            if (_proc.HasExited) return Result($"The game exited during startup with code {_proc.ExitCode}. Check its Player.log.", true);
            if (await Ping())
            {
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
                var status = await CallUnity("status", new JsonObject());
                string text = (string?)status["content"]?[0]?["text"] ?? "";
                return Result($"Launched (pid {_proc.Id}) and connected in {sw.Elapsed.TotalSeconds:0.0}s on port {_port}.\n{text}");
            }
            await Task.Delay(400);
        }
        return Result($"The game started (pid {_proc.Id}) but its driver did not answer on port {_port} within {waitSec}s. " +
                      "Check that this is a Development Build containing the UDriver package (the driver stays off in release builds), then retry app_connect.", true);
    }

    static JsonNode Kill()
    {
        if (_proc == null || _proc.HasExited) return Result("No running process was started by this server.");
        _proc.Kill(true);
        _proc.WaitForExit(5000);
        return Result("Game process terminated.");
    }

    static async Task<JsonNode> AppStatus()
    {
        string proc = _proc == null ? "no process launched by this server" : _proc.HasExited ? $"launched process exited with code {_proc.ExitCode}" : $"launched process running (pid {_proc.Id})";
        bool up = await Ping(), ed = await Ping(_editorPort);
        return Result($"{proc}; game driver on port {_port}: {(up ? "reachable" : "NOT reachable")}; editor bridge on port {_editorPort}: {(ed ? "reachable" : "not reachable")}");
    }

    /// <summary>A running game on another port, found through the token files games publish in ~/.udriver and confirmed with that token.</summary>
    static async Task<int?> FindGame()
    {
        string[] files;
        try { files = Directory.GetFiles(HomeDir, "*.token"); } catch { return null; }
        foreach (var f in files.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int p) || p < 1024 || p > 65535 || p == _port || p == _editorPort || !await Ping(p)) continue;
            // Only a game answers /tools (the editor bridge does not), and only with the right token.
            try { if ((await Http2(HttpMethod.Get, p, "/tools", null, 1.5)).status == 200) return p; } catch { }
        }
        return null;
    }

    static async Task<JsonNode> Connect(JsonObject args)
    {
        int wantPort = IntArg(args, "port", _port), wantEditor = IntArg(args, "editorPort", _editorPort);
        // Only talk to new ports where a UDriver instance has published a token: never poke arbitrary local services.
        // The current ports (from the configuration, app_launch or an earlier app_connect) are already trusted.
        foreach (int p in new[] { wantPort, wantEditor })
            if (p != 17890 && p != 17891 && p != _port && p != _editorPort && TokenFor(p, true).Length == 0)
                return Result($"refused: no UDriver instance has registered on port {p} (no token file).", true);
        _port = wantPort; _editorPort = wantEditor;
        // Entering Play Mode reloads scripts, so give the driver a few seconds to come up. Without an explicit port, a game on another port also counts.
        var sw = Stopwatch.StartNew();
        while (!await Ping() && sw.Elapsed.TotalSeconds < 12)
        {
            if (args["port"] == null && await FindGame() is int found) { _port = found; break; }
            await Task.Delay(500);
        }
        if (!await Ping())
            return Result($"Nothing answers on game port {_port}." + (await Ping(_editorPort)
                ? " The editor bridge is reachable: the editor is open but not in Play Mode (editor_play action=play), or still loading."
                : " Is the game (Development Build) or editor Play Mode running with the UDriver package?"), true);
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
        return await CallUnity("status", new JsonObject());
    }
}

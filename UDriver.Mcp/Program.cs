using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UDriver;

internal static partial class Program
{
    const string Instructions =
        "UDriver lets you see and operate a running Unity client. Typical flow: app_launch (or app_connect if the game/editor is already running) -> status -> " +
        "ui_tree / screenshot to see the screen -> act with ui_click, type_text, ui_set_value, ui_drag, ui_scroll, key -> wait_for to confirm the effect (pass sinceSeq from the action result). " +
        "To play the game itself (walk, jump, look around), learn the controls with input_actions and run a whole stretch in one play call. " +
        "In an unfamiliar app (a tool, terminal, CRM): app_map first, then query / chart_data for data, form_fill for forms, net_capture + net_log for its network, code_read for its code. " +
        "Several games and editors, and several copies of one game, can run at once: list_instances lists them; pass instance=<name> or use_instance. Other agents may work alongside: where_was_i, take_controls, team_post / team_read, claim. " +
        "To investigate a bug without trusting the UI: events_query (logs, exceptions, UI events), types_find / type_members to discover the code, state_get to read any object, " +
        "watch_add / trace_add / trigger_add to set up your own probes, frames_record + frames_get for frame-by-frame, time_control to pause/step. Memory: memory_summary -> memory_snapshot take -> play -> memory_snapshot diff -> memory_refs (who holds it); memory_watch for slow growth. Slow frames: render_stats -> render_scene -> screenshot camera=... " +
        "Element ids (#N) come from ui_tree / ui_find and go stale when the UI is rebuilt; ids >= 1000000 are uGUI objects, smaller ones UI Toolkit. Always back a verdict with evidence (events, state values, screenshots). " +
        "If the Unity editor is open, editor_* tools work even outside Play Mode: editor_status, editor_compile (compiler errors), editor_tests (run EditMode/PlayMode tests), editor_build, editor_play, editor_scene, editor_assets; " +
        "scene_tree / state_get / types_find also work in edit mode. Run self_test once in a new project to learn which capabilities work there. " +
        "Testing: qa_watch action=start -> play or qa_crawl -> qa_findings -> qa_isolate -> qa_report (one per distinct finding); editor_tests runs unit tests, qa_scenario saves and reruns repeatable checks, " +
        "qa_parallel runs several copies of a game at once with different seeds and timing; the prompt 'test' has the full playbook. " +
        "Call permissions first: it shows which tools the owner has turned off for you and which switch turns each on. If UDriver could not do something you needed (a tool failed, gave unusable output, or is missing), call report_problem before working around it or giving up.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static readonly Stream Stdout = Console.OpenStandardOutput();
    static readonly object WriteLock = new();

    // ---------------- targets ----------------

    internal sealed class Target
    {
        public string Name = "", Kind = "game", Path = "", Unity = "", Owner = "";
        public int Port, Pid;
        public bool Up, Live, Shell;
        public Process? Proc;
        public JsonObject? Launch;
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

    static void Used(Target t, JsonNode? res)
    {
        if (t.Kind == "editor") Me.LastEditor = t.Port; else Me.LastGame = t.Port;
        Me.LastTarget = t.Name;
        Note(t, res);
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

    static readonly Dictionary<string, string[]> ObserveActions = new()
    {
        ["editor_compile"] = new[] { "status" }, ["editor_tests"] = new[] { "status", "results" }, ["editor_build"] = new[] { "status" },
        ["editor_scene"] = new[] { "list" }, ["time_control"] = new[] { "status" }, ["frames_record"] = new[] { "status" },
        ["qa_watch"] = new[] { "status" }, ["qa_scenario"] = new[] { "list" },
    };
    static int Need(string name, JsonObject? args)
    {
        if (name == "leak_check") return (args?["action"] is JsonObject one ? new JsonArray(one.DeepClone()) : args?["action"] as JsonArray ?? new JsonArray())
            .Select(c => c is JsonObject step && (string?)step["tool"] is string t ? Need(t, step["args"] as JsonObject) : 2).DefaultIfEmpty(0).Max();
        if (name == "rec_replay") return (Recording((string?)args?["id"], out _, out _)?["actions"] as JsonArray ?? new JsonArray())
            .Select(x => x?["input"]?.GetValue<bool>() == true ? 1 : Need((string?)x?["tool"] ?? "", x?["args"] as JsonObject)).Append(1).Max();
        if (ObserveActions.TryGetValue(name, out var ok) && ok.Contains((string?)args?["action"] ?? "status")) return 0;
        if (args?["method"]?.ToString() == "os" || name == "app_launch" && Registry.Find(args?["path"]?.ToString() ?? "") is { Kind: "project" }) return 3;
        if ((string?)args?["drive"] is "auto" or "direct" || ((string?)args?["fire"])?.StartsWith("method:") == true) return 2;
        int level = ToolManifest.Level(name);
        if (name == "rule_add" && args?["do"] is JsonArray d)
            foreach (var c in d) if ((string?)c?["tool"] is string t) level = Math.Max(level, Need(t, c?["args"] as JsonObject));
        return level;
    }

    /// <summary>Agents on this computer: stdio and local keys, over loopback only. Everyone else is remote and never reaches level 3.</summary>
    static bool LocalOrigin => Me.Local && (Me.Client is "" or StdioClient || Me.Client.StartsWith("l_"));

    internal static readonly string[] LevelNames = { "Look", "Play", "Change the game", "Computer access" };

    const string ComputerSwitch = "Tools → Computer → Computer access in the UDriver app",
        OnlyHere = "Only for agents on the computer that runs UDriver, never for remote agents",
        AskOwner = " Ask the owner to turn it on in UDriver if you need it.";
    static string LevelSwitch(int level) => $"What agents may do → {level} · {LevelNames[level]} in the UDriver panel";
    static string GameSwitch(Registry.Game g, int need) => $"Games → {g.Name} → Agents may → {Cli.AccessNames[Math.Min(need, 2) + 1]} in the UDriver app";

    internal static string? LevelOff(int level, string what = "This")
    {
        if (level < 3) return Registry.LevelOn(level, LocalOrigin) ? null
            : $"{what} needs level {level} ({LevelNames[level]}), which is turned off for {(LocalOrigin ? "agents on this computer" : "remote agents")} ({LevelSwitch(level)}).{AskOwner}";
        if (!LocalOrigin) return $"{what} reaches beyond the game, so it is only available to agents on the computer that runs UDriver, never to remote agents.";
        return Registry.LevelOn(3, true) ? null
            : $"{what} could reach files, programs or the network outside the game, and Computer access is off ({ComputerSwitch}).{AskOwner}";
    }

    /// <summary>The owner's access level for this game (UDriver panel); checked wherever a call reaches a game or editor.</summary>
    static string? Denied(string name, JsonObject? args, Target? t, Registry.Game? g = null)
    {
        g ??= t?.Game;
        string level = g?.Access ?? (t == null ? Registry.DefaultAccess : "none");
        if (Math.Min(Need(name, args), 2) <= (g == null ? Registry.Level(level) : GameLevel(g, t?.Shell == true))) return null;
        string game = g?.Name ?? t?.Name ?? "this game";
        if (Registry.Level(level) >= 2)
            return $"Not done: \"{game}\" calls code on the computer directly ({Inject.Native(g!)}), so while Computer access is off agents can only look and play in it ({ComputerSwitch}).{AskOwner}";
        if (level == "none") return $"\"{game}\" is not allowed: the owner has not given agents access to it. Ask the owner to choose what agents may do in it (Games → {game} → Agents may in the UDriver app).";
        int need = Need(name, args);
        return $"{name} needs more access to \"{game}\", which allows {level} only ({(g == null ? "Games → Agents may in the UDriver app" : GameSwitch(g, need))}).{AskOwner}";
    }

    /// <summary>The game's access level, held at Play while Computer access is off when the game's own code could get past the in-process wall,
    /// unless this copy runs in the computer shell UDriver started it in.</summary>
    static int GameLevel(Registry.Game g, bool shell = false)
    {
        int level = Registry.Level(g.Access);
        return level >= 2 && LevelOff(3) != null && !shell && Inject.Native(g).Length > 0 ? 1 : level;
    }

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

    static string? Blocked(string name, JsonObject? args)
    {
        if (name == "permissions") return null;
        string client = Me.Client.Length > 0 ? Me.Client : StdioClient;
        if (Registry.ToolOff(name, client)) return $"{name} is turned off in the UDriver app ({HttpMode.ToolSwitch(name, client)}).{AskOwner}";
        if (name == "rule_add" && args?["do"] is JsonArray d)
            foreach (var c in d)
                if ((string?)c?["tool"] is string t && Blocked(t, c?["args"] as JsonObject ?? new JsonObject()) is string r) return "rule refused: " + r;
        return LevelOff(Need(name, args), name) ?? Configured(name, args);
    }

    /// <summary>The permissions tool: for each added game, the tools this agent may not call now, grouped by the switch that turns them on.</summary>
    static JsonNode Permissions(JsonObject args)
    {
        string want = (string?)args["game"] ?? "";
        var games = want.Length == 0 ? Registry.Games : Registry.Find(want) is { } one ? new List<Registry.Game> { one } : null;
        if (games == null) return Result($"No game called \"{want}\" is added in UDriver. Added: {string.Join(", ", Registry.Games.Select(g => g.Name))}.", true);
        string client = Me.Client.Length > 0 ? Me.Client : StdioClient;
        var places = HttpMode.ToolPlaces();
        var names = places.Keys.Where(n => n != "permissions").ToList();
        string? Why(string n, Registry.Game? g)
        {
            if (Registry.ToolOff(n, client)) return HttpMode.ToolSwitch(n, client, places);
            int need = Need(n, null);
            if (need == 3 && !LocalOrigin) return OnlyHere;
            if (LevelOff(need) != null) return need == 3 ? ComputerSwitch : LevelSwitch(need);
            if (Configured(n, null) != null) return "Turned off on this computer (UDRIVER_DISABLE_TOOLS)";
            if (g != null && Math.Min(need, 2) > GameLevel(g)) return Registry.Level(g.Access) >= 2 ? ComputerSwitch : GameSwitch(g, need);
            return null;
        }
        JsonObject Off(IEnumerable<(string Tool, string Switch)> off)
        {
            var o = new JsonObject();
            foreach (var x in off.GroupBy(x => x.Switch)) o[x.Key] = new JsonArray(x.Select(y => (JsonNode)y.Tool).ToArray());
            return o;
        }
        var everywhere = names.Select(n => (Tool: n, Switch: Why(n, null))).Where(x => x.Switch != null).ToDictionary(x => x.Tool, x => x.Switch!);
        var list = new JsonArray();
        foreach (var g in games)
        {
            var row = new JsonObject { ["game"] = g.Name, ["agentsMay"] = Cli.AccessNames[Registry.Level(g.Access) + 1] };
            if (g.Access == "none") { row["turnOn"] = $"Games → {g.Name} → Agents may in the UDriver app"; list.Add(row); continue; }
            var off = names.Where(n => !everywhere.ContainsKey(n)).Select(n => (Tool: n, Switch: Why(n, g) ?? "")).Where(x => x.Switch.Length > 0).ToList();
            var mayUse = new JsonObject();
            foreach (var grp in names.GroupBy(n => places[n].Group))
            {
                var ok = grp.Where(n => !everywhere.ContainsKey(n) && off.All(x => x.Tool != n)).ToList();
                mayUse[grp.Key] = ok.Count == grp.Count() ? "all" : ok.Count == 0 ? "none" : new JsonArray(ok.Select(n => (JsonNode)n).ToArray());
            }
            row["mayUse"] = mayUse;
            if (off.Count > 0) row["offHere"] = Off(off);
            list.Add(row);
        }
        var res = new JsonObject
        {
            ["you"] = LocalOrigin ? "an agent on this computer" : "a remote agent",
            ["computerAccess"] = !LocalOrigin ? "never for remote agents" : Registry.LevelOn(3, true) ? "on" : "off",
            ["offEverywhere"] = Off(everywhere.Select(x => (x.Key, x.Value))),
            ["games"] = list,
            ["note"] = games.Count == 0 ? "No games are added yet; the owner adds them in the UDriver app." : "Each key under off is the switch the owner changes to turn those tools on. Only the owner can change switches.",
        };
        return Result(res.ToJsonString());
    }

    /// <summary>Limits from the server's environment variables.</summary>
    static string? Configured(string name, JsonObject? args)
    {
        if (Disabled.Contains(name)) return $"'{name}' is disabled on this machine (UDRIVER_DISABLE_TOOLS).";
        if (name == "editor_build" && args != null && (string?)args["action"] == "build")
        {
            var keys = args.Select(kv => kv.Key).Where(k => k.Equals("path", StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count > 1) return "editor_build refused: give the output path once, as 'path'.";
            string? outPath = keys.Count == 1 ? (string?)args[keys[0]] : null;
            if (keys.Count == 1 && keys[0] != "path") { args.Remove(keys[0]); args["path"] = outPath; }
            if (BuildDirs.Length == 0) return "editor_build is not enabled: a human must set UDRIVER_BUILDS_DIR (the folder builds may be written to) in the MCP server configuration.";
            if (outPath != null && Path.IsPathFullyQualified(outPath))
            {
                if (!Approved(outPath, out string real))
                    return $"editor_build refused: an absolute output path must be inside the approved build folders ({string.Join(", ", BuildDirs)}); a relative path or none builds inside the project.";
                args["path"] = real;
            }
            args["buildDirs"] = new JsonArray(BuildDirs.Select(d => (JsonNode)d).ToArray());
        }
        if (!ReadOnly || ToolManifest.Level(name) == 0) return null;
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

    /// <summary>Keys for games and editors: given to the ones UDriver starts, or paired with ones the owner allowed. Never written to disk.</summary>
    static readonly Dictionary<int, string> Tokens = new();

    /// <summary>The panel server keeps the keys for every UDriver process on this computer.</summary>
    internal static bool IsHub;

    // USERPROFILE first: on Windows GetFolderPath ignores it, so a test could not point UDriver at another folder.
    internal static readonly string HomeDir = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } u ? u : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver");

    internal static string HubFile => Path.Combine(HomeDir, "panel.port");

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
        string tmp = path + "." + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
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

    internal static void Keep(int port, string key) { lock (Tokens) Tokens[port] = key; }

    internal static async Task<string> TokenFor(int port, bool renew = false)
    {
        lock (Tokens) if (!renew && Tokens.TryGetValue(port, out var t)) return t;
        string key = (IsHub || renew ? null : await Hub(port, null)) ?? await Pair(port);
        lock (Tokens) if (key.Length > 0) Tokens[port] = key; else Tokens.Remove(port);
        if (renew && key.Length > 0) await Hub(port, key);
        return key;
    }

    /// <summary>A key from the panel server, or (with key) a key for it to keep. Null when no panel runs.</summary>
    internal static async Task<string?> Hub(int port, string? key)
    {
        try
        {
            if (IsHub || !int.TryParse(File.ReadAllText(HubFile).Trim(), out int hub)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{hub}/panel/api/game-key")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["port"] = port.ToString(), ["key"] = key ?? "" }),
            };
            req.Headers.Authorization = new("Bearer", File.ReadAllText(Path.Combine(HomeDir, "app.key")).Trim());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await Http.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode && JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["key"]?.ToString() is { Length: > 0 } k ? k : null;
        }
        catch { return null; }
    }

    /// <summary>Gives a game or editor started by hand its key, once the owner has allowed it. The first program to pair keeps it until the game restarts.</summary>
    internal static async Task<string> Pair(int port)
    {
        var all = await Targets();
        if (all.FirstOrDefault(t => t.Port == port && t.Up) is not { } target || (target.Game?.Access ?? "none") == "none") return "";
        string key;
        lock (Tokens) key = all.Where(t => t.Pid == target.Pid && t.Port != port).Select(t => Tokens.GetValueOrDefault(t.Port)).FirstOrDefault(k => k != null)
            ?? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/pair") { Content = new StringContent("") };
            req.Headers.TryAddWithoutValidation("X-UDriver-Token", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await Http.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode ? key : "";
        }
        catch { return ""; }
    }

    /// <summary>Gets a new key once on 401: the game may have restarted.</summary>
    static async Task<(int status, string body)> Http2(HttpMethod method, int port, string path, string? json, double timeoutSec = 0)
    {
        string call = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
            req.Headers.TryAddWithoutValidation("X-UDriver-Token", await TokenFor(port, attempt > 0));
            req.Headers.TryAddWithoutValidation("X-UDriver-Client", Who(Me));
            req.Headers.TryAddWithoutValidation("X-UDriver-Holder", HolderId());
            req.Headers.TryAddWithoutValidation("X-UDriver-Call", call);
            if (LevelOff(3) == null) req.Headers.TryAddWithoutValidation("X-UDriver-Computer", "1");
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
            req.Headers.TryAddWithoutValidation("X-UDriver-Token", await TokenFor(port));
            req.Content = new StringContent(new JsonObject { ["call"] = call }.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var _ = await Http.SendAsync(req, cts.Token);
        }
        catch { }
    }

    static ToolParam P(string n, string t, string d, bool req = false) => new() { Name = n, Type = t, Desc = d, Required = req };

    static readonly ToolDef[] LocalTools =
    {
        new() { Name = "permissions", Desc = "Call this first. Shows what the owner allows you to do in each added game: which tools are off for you and the exact switch in UDriver that turns each on, " +
            "whether you are a remote agent or one on this computer, and whether Computer access is on. A tool that is off only returns a refusal, so check here instead of trying it.",
            Params = new[] { P("game", "string", "Only this game (its name from targets). Default: every added game.") } },
        new() { Name = "app_launch", Desc = "Start a Unity build (Development Build with the UDriver package) and wait until its driver answers. Several builds can run at once, each on its own port. Returns the app status. " +
            "For a Unity project the owner saved with access full, while Computer access is on, opens it in the Unity editor version from its ProjectVersion.txt (installed through Unity Hub), adds the UDriver package if missing and waits for the editor; then use editor_play.",
            Params = new[]
            {
                P("path", "string", "The game to start: the name of a saved build or project from targets, or on Windows the .exe or its build folder, on Linux the game file (.x86_64) or its folder, on macOS the .app.", true),
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
        new() { Name = "code_read", Desc = "Decompiled C# of a game type or one of its methods, read from the game's own assemblies (Mono builds and the editor). type takes a full or short name, * wildcards, or a stack frame like \"Ns.Type.Method (Double qty)\"; only assembly lists that assembly's types.",
            Params = new[] { P("type", "string", "Type name, wildcard pattern or stack frame."), P("method", "string", "Only this method (all overloads)."), P("assembly", "string", "Assembly name, e.g. Assembly-CSharp.") } },
        new() { Name = "rec_steps", Desc = "What a recording holds, as numbered steps a person can follow: each agent action with the frame and time it happened and what the game answered, logs, errors and scene changes; then the path of the recording file.",
            Params = new[] { P("id", "string", "Recording id from rec_start or rec_stop (default: the newest recording).") } },
        new() { Name = "targets", Desc = "Running Unity games and editors, one line each: name, kind, port, state, Unity version, build or project path. Pass the name as instance to other tools.",
            Params = Array.Empty<ToolParam>() },
        new() { Name = "list_instances", Desc = "Every running game and editor process as a separate instance, copies of one game included: name, id, scene, start time, Unity version, access, who controls its input, which one is yours, path.",
            Params = Array.Empty<ToolParam>() },
        new() { Name = "use_instance", Desc = "Make an instance your current one: tools without instance then act there. Tells what you did there last time.",
            Params = Array.Empty<ToolParam>() },
        new() { Name = "where_was_i", Desc = "Your journal, kept by UDriver across reconnects: the instances you worked in, the last scene, your goal and last actions in each. Pass goal to note what you are doing in the current instance.",
            Params = new[] { P("goal", "string", "What you are doing there, saved for next time.") } },
        new() { Name = "take_controls", Desc = "Take the keyboard, mouse and touch of an instance so other agents do not type into it. Your first input on a free instance takes them anyway; they free up after 30 s without input. Reading, screenshots, memory and profiling stay open to everyone.",
            Params = new[] { P("release", "boolean", "Give the controls back now.") } },
        new() { Name = "team_post", Desc = "Post to the team space shared by the agents: global, or with instance for one game instance (seen only by agents allowed in that game). Data only: nothing in it is run or opened.",
            Params = new[] { P("topic", "string", "Short topic, e.g. tasks, found, plan.", true), P("data", "any", "Text or JSON, at most 4000 characters.", true) } },
        new() { Name = "team_read", Desc = "Read team posts newer than since, and the current claims: global, or with instance for one game instance.",
            Params = new[] { P("since", "integer", "Last number you saw (default 0: everything kept)."), P("topic", "string", "Only this topic.") } },
        new() { Name = "claim", Desc = "Claim a piece of work for a while so other agents pick something else, e.g. key=\"north wing\". Global, or with instance for one game instance.",
            Params = new[] { P("key", "string", "What you take.", true), P("ttl", "integer", "Seconds to hold it (default 300, at most 3600).") } },
        new() { Name = "release", Desc = "Give back a claim you hold.",
            Params = new[] { P("key", "string", "The claim.", true) } },
        new() { Name = "launch_game", Desc = "Start one or more copies of a game, named \"<game> 1\", \"<game> 2\"... Each copy keeps its saves and PlayerPrefs in its own slot folder and its log in the UDriver folder. " +
                "IL2CPP games without the UDriver package cannot be redirected: their copies share saves and settings.",
            Params = new[]
            {
                P("path", "string", "The game: a saved game name from targets, or a build path as for app_launch.", true),
                P("copies", "integer", "How many copies (default 1, at most 8)."),
                P("args", "array", "Extra game arguments, as for app_launch."),
                P("waitSec", "integer", "How long to wait for each copy (default 90)."),
            } },
        new() { Name = "report_problem", Desc = "Tell the UDriver developer that UDriver itself let you down: a tool failed, gave output you could not use, or a tool you needed does not exist. Saved on this computer; the owner decides whether to send it. Not for bugs in the game.",
            Params = new[]
            {
                P("what", "string", "What you needed UDriver to do, in one or two sentences.", true), P("tried", "string", "What you tried."),
                P("missing", "string", "What tool or output would have helped."), P("tool", "string", "The UDriver tool involved, if any."), P("error", "string", "The error text you got, if any."),
            } },
        new() { Name = "qa_report", Desc = "Write a bug report into the game's UDriverReports folder (project folder, or the folder holding the build): Markdown with steps to reproduce, expected/actual, suspected cause with file:line and what breaks together, stack, log lines and screenshot, plus JSON. Repeats of the same finding update the same report. Pass findings or all=true to report what qa_findings found, or title/steps/expected/actual/stack for a bug you found yourself.",
            Params = new[]
            {
                P("findings", "array", "Finding ids from qa_findings (or finding objects)."), P("all", "boolean", "Report every current finding."),
                P("title", "string", "Short title (manual report, or to rename a single finding)."), P("steps", "array", "Steps to reproduce, one per item (manual report)."),
                P("expected", "string", "What should happen."), P("actual", "string", "What happens instead."), P("stack", "string", "Stack trace text; file:line is read from it."),
                P("severity", "string", "critical | major | minor."), P("source", "string", "Folder with the game's C# sources when the target is a build; file:line is found by scanning *.cs there."),
            } },
        new() { Name = "qa_scenario", Desc = "Repeatable checks: save a list of tool calls with expected text, run it again later through the normal tools (switches and access apply), list saved ones. Stored in UDriverReports/scenarios.",
            Params = new[]
            {
                P("action", "string", "list (default) | save | run."), P("name", "string", "Scenario name (letters, digits, space, _ . -)."),
                P("steps", "array", "For save: [{\"tool\":\"ui_click\",\"args\":{...},\"expect\":\"text the result must contain\",\"expectNot\":\"text it must not contain\"}]."),
            } },
        new() { Name = "qa_parallel", Desc = "Run one in-game tool (default qa_crawl) on several copies of the same game at once, each with its own seed, delay and time scale, then merge qa_findings from all copies by signature: which copies and variations reproduced each finding. A finding hit only under some variations points to a timing or combination bug.",
            Params = new[]
            {
                P("targets", "array", "Target names to use (default: every running copy of the same game as target)."), P("copies", "integer", "Start more copies of the build first until this many run."),
                P("tool", "string", "In-game tool to run (default qa_crawl)."), P("args", "object", "Arguments for the tool; seed is the base seed."),
                P("vary", "boolean", "Give each copy seed = base + i, delayMs 0/150/400 and timeScale 1/2 (default true)."),
            } },
    };

    static readonly HashSet<string> ServerTools = new(LocalTools.Select(t => t.Name).Append("batch").Append("eval"));

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
        if (args is ["shell-exec", _, _, _, ..]) { Environment.ExitCode = Shell.Exec(args); return; }
        // Installer hooks. On Windows the first start after Setup opens the terminal screen; updates install only when asked.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => { if (OperatingSystem.IsWindows()) Environment.Exit(Cli.Run(["tui"]).GetAwaiter().GetResult()); }).Run();
        if (args is ["setup"]) { HttpMode.TerminalSetup(); return; }
        if (args is ["inject" or "restore", var gamePath]) { Environment.ExitCode = Inject.Run(args[0], gamePath); return; }
        if (args is ["tui" or "status" or "approve" or "deny" or "tools" or "levels" or "computer" or "clear-mark", ..]) { Environment.ExitCode = await Cli.Run(args); return; }
        // Game drivers from before 2.0 keep their keys in files; a running game's file stays, it tells OldAgent the game needs updating.
        try { foreach (var f in Directory.EnumerateFiles(HomeDir, "*.token")) if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int tp) || Free(tp)) File.Delete(f); } catch { }
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
            foreach (var t in Launched.Where(t => t.Kind != "editor"))
            {
                Stop(t.Proc!, t.Port);
                try { if (LaunchName(t.Port, t.Proc!.Id) != null) File.Delete(NameFile(t.Port)); } catch { }
            }
    }

    /// <summary>Lets the game shut down and remove its files (SIGTERM, WM_CLOSE on Windows), SIGKILL after 3 s, then drops what it left behind.</summary>
    static void Stop(Process p, int port)
    {
        try
        {
            if (!p.HasExited)
            {
                if (OperatingSystem.IsWindows()) p.CloseMainWindow(); else kill(p.Id, 15);
                if (!p.WaitForExit(3000)) { p.Kill(true); p.WaitForExit(2000); }
            }
        }
        catch { }
        var json = Path.Combine(HomeDir, port + ".json");
        try { if ((int?)JsonNode.Parse(File.ReadAllText(json))?["pid"] == p.Id) File.Delete(json); } catch { }
    }

    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);

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
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = !HttpOn }, ["prompts"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "udriver", ["version"] = ToolManifest.Version },
                    ["instructions"] = Instructions,
                };
            case "ping": return new JsonObject();
            case "resources/list": return new JsonObject { ["resources"] = new JsonArray() };
            case "prompts/list": return new JsonObject { ["prompts"] = new JsonArray { TestPrompt() } };
            case "prompts/get":
                if ((string?)prm?["name"] != "test") throw new RpcError(-32602, "unknown prompt: " + prm?["name"]);
                return new JsonObject
                {
                    ["description"] = (string?)TestPrompt()["description"],
                    ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = TestPlaybook(prm?["arguments"]?["what"]?.ToString()) } } },
                };
            case "tools/list": return new JsonObject { ["tools"] = await ListTools() };
            case "tools/call":
                string name = (string?)prm?["name"] ?? throw new RpcError(-32602, "missing tool name");
                var args = prm?["arguments"] as JsonObject ?? new JsonObject();
                string? want = args["instance"]?.ToString() ?? args["target"]?.ToString();
                args.Remove("target"); args.Remove("instance");
                Calling.Value = (name, args.ToJsonString());
                Interlocked.Increment(ref CallCount); LastTool = KnownTools.Contains(name) ? name : "(project tool)"; LastCallUtc = Me.LastSeen = DateTime.UtcNow;
                if (Blocked(name, args) is string refusal) return Result(refusal, true);
                if (HttpOn)
                {
                    // Remote connector proxies cut requests off after about 100 s.
                    if (IntArg(args, "timeoutMs", 0) > 60000) args["timeoutMs"] = 60000;
                    if (name == "app_launch" && IntArg(args, "waitSec", 90) > 60) args["waitSec"] = 60;
                }
                if ((name == "batch" ? args["calls"] : name == "leak_check" ? args["action"] is JsonObject one ? new JsonArray(one.DeepClone()) : args["action"] : null) is JsonArray inner)
                    foreach (var c in inner)
                        if (Blocked((string?)c?["tool"] ?? "", c?["args"] as JsonObject ?? new JsonObject()) is string r2) return Result(name + " refused: " + r2, true);
                var def = LocalTools.Concat(ToolManifest.Tools).FirstOrDefault(d => d.Name == name);
                var unknown = def == null ? new List<string>() : args.Select(kv => kv.Key).Where(k => k != "timeoutMs" && !def.Params.Any(p => p.Name == k)).ToList();
                var sw = Timing ? Stopwatch.StartNew() : null;
                JsonNode? res = null;
                try
                {
                    res = await Run(name, args, want);
                    if (name == "status" && res["isError"]?.GetValue<bool>() != true && res["content"] is JsonArray said)
                        said.Add(new JsonObject { ["type"] = "text", ["text"] = "To see which tools you may use here and which are turned off, call permissions." });
                    if (unknown.Count > 0 && res["content"] is JsonArray content)
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = $"warning: unknown argument {string.Join(", ", unknown.Select(k => $"'{k}'"))} was ignored (known: {string.Join(", ", def!.Params.Select(p => p.Name))})" });
                    return res;
                }
                catch (Exception e) when (e is not RpcError)
                {
                    if (e is not OperationCanceledException && e.GetType() != typeof(ArgumentException))
                        await Record("exception", name, args, e.GetType().Name + ": " + e.Message, e.StackTrace ?? "", 0, want);
                    return res = Result(e.GetType().Name + ": " + e.Message, true);
                }
                finally
                {
                    Remember(name, args, res);
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
        "rec_start" => await RecStart(args, want),
        "rec_replay" => await RecReplay(args, want),
        "rec_steps" => RecSteps(args),
        "list_instances" => await ListInstances(),
        "use_instance" => await UseInstance(want),
        "where_was_i" => await WhereWasI(args, want),
        "take_controls" => await TakeControls(args, want),
        "team_post" => await TeamPost(args, want),
        "team_read" => await TeamRead(args, want),
        "claim" => await Claim(args, want, true),
        "release" => await Claim(args, want, false),
        "launch_game" => await LaunchCopies(args),
        "report_problem" => await ReportProblem(args, want),
        "qa_report" => await QaReport(args, want),
        "qa_scenario" => await QaScenario(args, want),
        "qa_parallel" => await QaParallel(args, want),
        "code_read" => await ReadCode(args, want),
        "permissions" => Permissions(args),
        _ => await CallUnity(name, args, want),
    };

    /// <summary>Reads only assemblies under the game's own folder (the project folder for an editor).</summary>
    static async Task<JsonNode> ReadCode(JsonObject args, string? want)
    {
        var map = await CallUnity("app_map", new JsonObject { ["section"] = "assemblies" }, want);
        if ((bool?)map["isError"] == true) return map;
        var t = (await Targets()).FirstOrDefault(x => x.Name == Me.LastTarget);
        if (t?.Game is not { Path.Length: > 0 } g) return Result("This game is not in the owner's list, so its code cannot be read.", true);
        string root = t.Kind == "editor" ? g.Path : ManagedDir(g.Path);
        string lines = string.Join("\n", (map["content"] as JsonArray ?? new()).Select(c => (string?)c?["text"]));
        return Result(CodeRead.Read(lines, root, (string?)args["type"], (string?)args["method"], (string?)args["assembly"]));
    }

    /// <summary>Only a build's own assemblies are read, never other files in the folder it sits in.</summary>
    static string ManagedDir(string build)
    {
        if (build.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return Path.Combine(build, "Contents", "Resources", "Data", "Managed");
        if (Directory.Exists(build) && GameFile(build) is string exe) build = exe;
        return Path.Combine(Path.ChangeExtension(build, null) + "_Data", "Managed");
    }

    // ---------------- tool list ----------------

    static JsonObject TargetParam() => new()
    {
        ["type"] = "string",
        ["description"] = "Which running game or editor instance to use: a name or id from list_instances. Default: your current instance; with only one running, that one.",
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
        if (d.Name is not ("app_launch" or "targets" or "list_instances" or "launch_game" or "permissions")) props["instance"] = TargetParam();
        if (d.Name == "use_instance") req.Add("instance");
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
        var running = await Targets();
        // Every tool, including those the owner turned off: agents read the list once, and each call checks the switches and the game's access.
        // A game's own tools stay hidden while agents have no access to that game, so its tool names do not leak.
        foreach (var d in LocalTools.Concat(ToolManifest.Tools))
        {
            known.Add(d.Name);
            tools.Add(ToolJson(d));
        }
        var live = _liveTools;
        if (live == null)
        {
            live = new JsonArray();
            foreach (var t in running.Where(t => t.Kind == "game" && t.Up))
                try
                {
                    var (status, body) = await Http2(HttpMethod.Get, t.Port, "/tools", null, 1.5);
                    if (status == 200 && JsonNode.Parse(body)?["tools"] is JsonArray arr)
                        foreach (var x in arr.OfType<JsonObject>()) { var c = (JsonObject)x.DeepClone(); c["game"] = t.Game?.Id; c["shell"] = t.Shell; live.Add(c); }
                }
                catch { }
            _liveTools = live;
        }
        foreach (var t in live)
        {
            string n = (string?)t?["name"] ?? "";
            if (t == null || Registry.Games.FirstOrDefault(g => g.Id == (string?)t["game"]) is not { } lg || Registry.Level(lg.Access) < 0 || !known.Add(n)) continue;
            var copy = t.DeepClone();
            copy.AsObject().Remove("game");
            copy.AsObject().Remove("shell");
            if (copy["inputSchema"]?["properties"] is JsonObject props) props["instance"] = TargetParam();
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
            if (n.Length > 0 && n != "permissions" && seen.Add(n))
                list.Add(new JsonObject
                {
                    ["name"] = n, ["description"] = desc,
                    ["level"] = Need(n, null), ["access"] = Registry.Levels[Math.Min(Need(n, null), 2)], ["limit"] = Configured(n, null),
                });
        return list;
    }

    // ---------------- helpers ----------------

    internal static JsonObject Result(string text, bool isError = false) => new()
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

    static bool Gone(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } catch { return false; }
    }

    internal const string OldAgentText = "This game has an old UDriver inside, so agents cannot connect to it. Close the game and start it once from UDriver (Launch in the UDriver app, or app_launch): UDriver then updates it.";

    /// <summary>A game with a UDriver package from before 2.0 inside: such drivers leave a key file, the current one never does.</summary>
    internal static bool OldAgent(int port) => File.Exists(Path.Combine(HomeDir, port + ".token"));

    static bool Free(int port)
    {
        try { using var c = new TcpClient(); if (c.ConnectAsync(IPAddress.Loopback, port).Wait(300)) return false; } catch { }
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
                if ((int?)j["pid"] is > 0 and int pid && Gone(pid)) { File.Delete(f); continue; }
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
                if (!found.TryGetValue(l.Port, out var t)) found[l.Port] = t = new Target { Port = l.Port, Kind = l.Kind, Path = l.Path, Name = Path.GetFileNameWithoutExtension(l.Path) };
                t.Proc = l.Proc;
                t.Launch = l.Launch;
                t.Owner = l.Owner;
                t.Shell = l.Shell;
                if (l.Name.Length > 0) t.Name = l.Name;
            }
        var named = new HashSet<Target>();
        foreach (var t in found.Values)
            if (LaunchName(t.Port, t.Proc?.Id ?? t.Pid) is { Length: > 0 } n) { t.Name = n; named.Add(t); }
            else lock (Launched) if (Launched.Any(l => l.Port == t.Port && l.Name.Length > 0)) named.Add(t);
        var list = found.Values.OrderBy(t => t.Port).ToList();
        // Only a UDriver driver counts: it left an info file, and answers with the live process id written there.
        await Task.WhenAll(list.Select(async t => t.Up = t.Pid > 0 && await PingPid(t.Port) == t.Pid && Alive(t.Pid)));
        foreach (var t in list) t.Live = t.Up || (t.Proc != null ? !t.Proc.HasExited : t.Pid > 0 && Alive(t.Pid));
        list = list.Where(t => t.Live || t.Proc != null).ToList();
        Registry.Attach(list);
        // A target goes by its saved name, which stays the same while it runs; names are unique per kind, so an editor
        // and its Play Mode game share one, and never take the saved name of another game. Names given by app_launch keep theirs.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = Registry.Games;
        foreach (var t in list.OrderBy(t => named.Contains(t) ? 0 : 1))
        {
            string name = !named.Contains(t) && t.Game != null ? t.Game.Name : t.Name.Length > 0 ? t.Name : t.Kind;
            t.Name = name;
            for (int i = 2; (!named.Contains(t) && saved.Any(g => g.Id != t.Game?.Id && g.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))) || !taken.Add(t.Kind + "/" + t.Name); i++) t.Name = name + "-" + i;
        }
        string sig = string.Join(",", list.Where(t => t.Kind == "game" && t.Up).Select(t => t.Port + ":" + t.Game?.Access + (t.Shell ? ":shell" : "")));
        if (sig != _liveSig) { _liveSig = sig; _liveTools = null; }
        return list;
    }

    static string Names(IEnumerable<Target> ts) => ts.Any() ? string.Join(", ", ts.Select(t => $"{t.Name} ({t.Kind})")) : "none";

    /// <summary>By target name, else by saved game while only one target of that kind uses it.</summary>
    static Target? Named(List<Target> all, string want, string kind = "")
    {
        var byName = all.Where(t => t.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || InstanceId(t) == want).OrderBy(t => t.Kind == kind ? 0 : 1).FirstOrDefault();
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

    static string Saved(Registry.Game g) => $"{g.Name}  {g.Kind}  not running  {AccessText(g.Access)}" + "  (app_launch path=" + g.Name + ")" + "  " + g.Path;

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
        int ms = Math.Clamp(IntArg(args, "timeoutMs", 0), 0, 600000);
        double limit = (ms > 0 ? ms + 4000 : 122000) / 1000.0 + 10;
        try { (status, text) = await Http2(HttpMethod.Post, port, "/call", body.ToJsonString(), limit); }
        catch (TaskCanceledException) when (!Cancel.Value.IsCancellationRequested)
        {
            return await Watch(port, name, args, Result($"timeout: '{name}' got no answer within {limit:0} s.", true));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !Cancel.Value.IsCancellationRequested) { return null; }
        if (status == 401 && OldAgent(port)) return Result(OldAgentText, true);
        if (status == 401)
            return Result($"The game or editor on port {port} is not connected to this UDriver. If the owner has not allowed it yet, ask them to set its access in the UDriver panel. If another program connected to it first, restart it, or start the build with app_launch.", true);
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException)
        {
            return Result($"The Unity editor or game on port {port} dropped the call while reloading scripts (entering or leaving Play Mode, or after a code change). Repeat the call in a second or two.", true);
        }
        return await Watch(port, name, args, json?["content"] is JsonArray content
            ? new JsonObject { ["content"] = content.DeepClone(), ["isError"] = json["isError"]?.GetValue<bool>() ?? false }
            : Result((string?)json?["error"] ?? "unexpected response: " + text, true));
    }

    /// <summary>The bridge is offline during script reloads.</summary>
    static async Task<JsonNode> CallEditor(Target t, string name, JsonObject args)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var er = await Send(t.Port, name, args);
            if (er != null) { Used(t, er); return er; }
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
        if (t?.Kind == "editor" && EditorLocked(name, args) is string locked) return Result(locked, true);
        if (t?.Kind == "editor") return await CallEditor(t, name, args);

        if (t != null && await Eval.Arm(t, Need(name, args) >= 2) is JsonNode refused) return refused;
        var res = t == null ? null : await Send(t.Port, name, args);
        if (res != null) { Used(t!, res); return res; }
        if (ToolManifest.EditModeTools.Contains(name) && Pick(all, "editor", null, out _) is Target ed && Denied(name, args, ed) == null)
        {
            if (EditorLocked(name, args) is string locked2) return Result(locked2, true);
            if (await Send(ed.Port, name, args) is JsonNode er) { Used(ed, er); return er; }
        }
        string game = t == null ? "No game is running." : $"The game '{t.Name}' is not reachable on port {t.Port}."
            + (t.Proc is { HasExited: true } p ? $" The launched game has exited (code {p.ExitCode}) - it may have crashed." : "");
        if (all.Any(x => x.Kind == "editor" && x.Up))
            return Result($"{game} The Unity editor is open: enter Play Mode with editor_play action=play, or start a build with app_launch. " +
                          "Editor tools (editor_*) and inspection tools (scene_tree, state_get, types_find, type_members) work right now.", true);
        return Result($"{game} Start it with app_launch, or run the game / editor Play Mode and call app_connect.", true);
    }

    /// <summary>The editor runs its own and the project's tools on whatever an agent changes, and they cannot be walled in-process.</summary>
    static string? EditorLocked(string name, JsonObject? args) => Need(name, args) >= 2 && LevelOff(3) != null
        ? "Not done: with Computer access off, agents can look and play in the Unity editor but not change it, because the editor's own and the project's tools could act on a change outside the project. Change a running game instead, or ask the owner to turn on Computer access." : null;

    static Task<JsonNode?> Send(int port, string name, JsonObject args) => name == "eval" ? Eval.Run(port, args) : TryCall(port, name, args);

    static bool GameTool(string tool) => !ServerTools.Contains(tool) && !tool.StartsWith("editor_", StringComparison.Ordinal);

    static string Step(JsonNode? c, string? want, out string? target)
    {
        target = c?["instance"]?.ToString() ?? c?["target"]?.ToString() ?? want;
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
            if (c is JsonObject step && step["args"] is JsonObject a && (a["instance"] ?? a["target"]) is JsonNode at)
            {
                a.Remove("target"); a.Remove("instance");
                step["target"] ??= at.DeepClone();
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
                res = await Eval.Arm(game, run.Any(c => Need((string?)c?["tool"] ?? "", c?["args"] as JsonObject) >= 2))
                      ?? await TryCall(game.Port, "batch", new JsonObject { ["calls"] = run, ["stopOnError"] = stop, ["first"] = i, ["output"] = part });
                if (res != null) Used(game, res);
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

    internal static async Task<JsonNode> Launch(JsonObject args, bool byOwner = false, int slot = 0)
    {
        string path = (string?)args["path"] ?? throw new ArgumentException("path is required");
        var g = Registry.Find(path);
        if (g is { Kind: "project" }) return await OpenProject(g, args, byOwner);
        string real;
        if (g != null)
        {
            real = RealPath(g.Path);
            if (Directory.Exists(real))
                real = RealPath(Inject.GameExe(real));
            if (!File.Exists(real)) return Result($"The game file of '{g.Name}' was not found at {g.Path}. The owner can fix the path in the UDriver panel.", true);
        }
        else if (Registry.Classify(path, out string kind, out _, out _) == null && kind == "project")
            return Result($"refused: {path} is not a Unity project the owner saved. Only saved projects open (see targets); the owner can add this one in the UDriver panel.", true);
        else if (BuildDirs.Length == 0)
            return Result("app_launch is not enabled on this machine. A human must list the folder(s) that contain game builds in the MCP server configuration: " +
                          "environment variable UDRIVER_BUILDS_DIR (several folders separated by '" + Path.PathSeparator + "'). Until then, start the game manually and use app_connect.", true);
        else
        {
            string refused = $"refused: {path} is not a saved game and is outside the approved build folders ({string.Join(", ", BuildDirs)}). Only a human can change UDRIVER_BUILDS_DIR or add games in the UDriver panel.";
            if (!Approved(path, out real)) return Result(refused, true);
            if (Directory.Exists(real))
            {
                path = Inject.GameExe(real);
                if (!Approved(path, out real)) return Result(refused, true);
            }
            if (!File.Exists(real)) return Result(Directory.Exists(real) ? "no Unity game file (NAME.exe or NAME.x86_64 next to a NAME_Data folder) was found in " + path : "file not found: " + path, true);
            g = Registry.ForPath(real);
        }
        if (!byOwner && Denied("app_launch", args, null, g) is string no) return Result(no, true);
        if (Inject.Ensure(real, out string? agentLib, byOwner || LevelOff(3) == null) is string cannot) return Result(cannot, true);
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
        // As Steam does when it starts a game; without it the game asks Steam to start it again and quits, so UDriver loses it.
        if (Inject.SteamAppId(real) is { } steamId) psi.Environment["SteamAppId"] = psi.Environment["SteamGameId"] = steamId;
        // Only the game gets the variable; the agent removes it before the game starts anything else.
        if (agentLib != null) psi.Environment[OperatingSystem.IsMacOS() ? "DYLD_INSERT_LIBRARIES" : "LD_PRELOAD"] = agentLib;
        if (Environment.GetEnvironmentVariable("UDRIVER_EXPORTS_MAX_MB") is { Length: > 0 } exportsMax) psi.Environment["UDRIVER_EXPORTS_MAX_MB"] = exportsMax;
        // Through the environment, not the command line, which other users can see.
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        psi.Environment["UDRIVER_TOKEN"] = token;
        lock (Tokens) Tokens[lp] = token;
        _ = Hub(lp, token);
        psi.ArgumentList.Add("-udriver-port");
        psi.ArgumentList.Add(lp.ToString());
        if (args["startup"] is JsonArray startup && startup.Count > 0)
        {
            foreach (var c in startup)
                if (((string?)c?["tool"] == "batch" ? "batch cannot run at startup; list the calls one by one" : (string?)c?["tool"] == "eval" ? "eval cannot run at startup" : !byOwner && LevelOff(3) != null && Need((string?)c?["tool"] ?? "", c?["args"] as JsonObject) >= 2 ? "a change at startup runs before UDriver can keep the computer closed; it needs Computer access" : Blocked((string?)c?["tool"] ?? "", c?["args"] as JsonObject ?? new JsonObject()) ?? (byOwner ? null : Denied((string?)c?["tool"] ?? "", c?["args"] as JsonObject, null, g))) is string r3)
                    return Result("startup refused: " + r3, true);
            psi.Environment["UDRIVER_STARTUP"] = startup.ToJsonString();
        }
        if (args["args"] is JsonArray extra)
            foreach (var x in extra) if (x != null) psi.ArgumentList.Add(x.ToString());
        string log = PlayerLog(real);
        if (slot > 0)
        {
            log = Path.Combine(HomeDir, "logs", $"{Path.GetFileNameWithoutExtension(real)}-slot{slot}.log");
            PrivateDir(Path.GetDirectoryName(log)!);
            psi.ArgumentList.Add("-udriver-slot");
            psi.ArgumentList.Add(slot.ToString());
            psi.ArgumentList.Add("-logFile");
            psi.ArgumentList.Add(log);
        }

        // While Computer access is off the game starts inside the computer shell, if this computer can put it there.
        bool wanted = LevelOff(3) != null;
        var launched = DateTime.UtcNow.AddSeconds(-2);
        var proc = wanted ? Shell.Start(psi, real, log, HomeDir) : null;
        bool shell = proc != null;
        proc ??= Process.Start(psi) ?? throw new InvalidOperationException("could not start the process");
        // The game cannot write UDriver's folder from inside the shell, so its discovery file comes from here.
        if (shell) WritePrivate(Path.Combine(HomeDir, lp + ".json"), new JsonObject { ["kind"] = "game", ["name"] = Path.GetFileNameWithoutExtension(real), ["project"] = OperatingSystem.IsMacOS() ? Path.GetFullPath(Path.Combine(real, "..", "..", "..")) : real, ["pid"] = proc.Id, ["port"] = lp }.ToJsonString());
        Registry.Started(real, proc);
        if (label.Length > 0) WritePrivate(NameFile(lp), new JsonObject { ["name"] = label, ["pid"] = proc.Id }.ToJsonString());
        lock (Launched)
        {
            Launched.RemoveAll(t => t.Port == lp);
            Launched.Add(new Target { Port = lp, Path = real, Name = label, Proc = proc, Owner = Me.Label, Launch = (JsonObject)args.DeepClone() });
        }
        int launchedPid = proc.Id;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
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
            if (proc.HasExited)
                return Result(File.Exists(log) && File.GetLastWriteTimeUtc(log) >= launched
                    ? $"The game exited during startup with code {proc.ExitCode}. Check its log: {log}"
                    : $"The game closed before Unity started (exit code {proc.ExitCode}), so it wrote no log.", true);
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
                if (shell && text.Contains(Shell.Mark) && Shell.Active(proc.Id))
                {
                    lock (Launched) foreach (var l in Launched.Where(l => l.Proc == proc)) l.Shell = true;
                    ToolsChanged();
                }
                else if (wanted && (g ?? Registry.ForPath(real)) is { } known && Inject.Native(known).Length > 0) text += "\nNot in the computer shell: " + (shell ? "the operating system did not confirm it" : "it cannot be set up on this computer") + ". If the game's own code could reach the computer, agents can only look and play in it while Computer access is off.";
                string name = (await Targets()).FirstOrDefault(t => t.Port == lp)?.Name ?? "";
                // Keeps the number of an unnamed copy when others start or stop.
                if (name.Length > 0) WritePrivate(NameFile(lp), new JsonObject { ["name"] = name, ["pid"] = proc.Id }.ToJsonString());
                Me.LastGame = lp; Me.LastTarget = name;
                return Result($"Launched '{name}' (pid {proc.Id}) and connected in {sw.Elapsed.TotalSeconds:0.0}s on port {lp}.\n{text}");
            }
            await Task.Delay(400);
        }
        return Result($"The game started (pid {proc.Id}) but its driver did not answer on port {lp} within {waitSec}s. " + (agentLib != null
            ? "UDriver's agent for IL2CPP games did not start inside it; the game may block added libraries. Check its log: " + log
            : "Check that this is a Development Build containing the UDriver package (the driver stays off in release builds), then retry app_connect."), true);
    }

    static readonly Regex RecId = new("^rec-[0-9]{8}-[0-9]{6}-[0-9a-f]{4}$");

    static JsonObject? Recording(string? id, out string file, out string? error)
    {
        string dir = Path.Combine(HomeDir, "exports");
        id ??= Directory.Exists(dir) ? Directory.GetFiles(dir, "rec-*.json").Select(Path.GetFileNameWithoutExtension).Where(n => RecId.IsMatch(n!)).Max() : null;
        file = "";
        error = id == null ? "No recordings yet: rec_start, then rec_stop." : !RecId.IsMatch(id) ? "id must be a recording id from rec_start or rec_stop, like rec-20261120-101500-ab12" : null;
        if (error != null) return null;
        file = Path.Combine(dir, id + ".json");
        if (!File.Exists(file)) { error = $"No recording {id} in {dir}."; return null; }
        return JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
    }

    static JsonNode RecSteps(JsonObject args)
    {
        var rec = Recording((string?)args["id"], out var file, out var error);
        if (rec == null) return Result(error!, true);
        int n = 0;
        var steps = (rec["steps"] as JsonArray ?? new JsonArray()).Select(x => (string?)x ?? "").Select(x => x.StartsWith("frame ", StringComparison.Ordinal) ? ++n + ". " + x : x);
        return Result(string.Join("\n", steps) + "\nRecording file: " + file);
    }

    static async Task<JsonNode> RecStart(JsonObject args, string? want)
    {
        var t = Pick(await Targets(), "game", want, out _);
        if (t is not { Proc.HasExited: false, Launch: not null } || t.Owner != Me.Label || args["fresh"] is JsonValue fv && fv.TryGetValue(out bool fresh) && !fresh)
            return await CallUnity("rec_start", args, want);
        if (Denied("rec_start", args, t) is string no) return Result(no, true);
        var again = (JsonObject)t.Launch.DeepClone();
        again["name"] = t.Name;
        again["port"] = t.Port;
        again["startup"] = new JsonArray(new JsonObject { ["tool"] = "rec_start", ["args"] = args.DeepClone() });
        await Kill(t.Name, new JsonObject());
        var up = await Launch(again);
        return up["isError"]?.GetValue<bool>() == true ? up : await CallUnity("rec_start", args, t.Name);
    }

    static async Task<JsonNode> RecReplay(JsonObject args, string? want)
    {
        var rec = Recording((string?)args["id"], out _, out var error);
        if (rec == null) return Result(error!, true);
        if (rec["exact"]?.GetValue<bool>() != true) return await CallUnity("rec_replay", args, want);
        string id = (string)args["id"]!, name = "replay-" + id[^4..];
        var call = new JsonObject { ["id"] = id };
        var (lastGame, lastTarget) = (Me.LastGame, Me.LastTarget);
        var up = await Launch(new JsonObject
        {
            ["path"] = (string?)rec["game"], ["name"] = name, ["args"] = rec["args"]?.DeepClone(),
            ["startup"] = new JsonArray(new JsonObject { ["tool"] = "rec_replay", ["args"] = call.DeepClone() }),
        });
        if (up["isError"]?.GetValue<bool>() == true) return up;
        call["timeoutMs"] = Math.Min(600000, (rec["frames"]?.GetValue<int>() ?? 0) * 50 + 30000);
        try { return await CallUnity("rec_replay", call, name); }
        finally
        {
            await Kill(name, new JsonObject { ["force"] = true });
            (Me.LastGame, Me.LastTarget) = (lastGame, lastTarget);
        }
    }

    /// <summary>Opens a saved project in the editor version it was made with; editor scripts run, so only at access full and only from this computer.</summary>
    static async Task<JsonNode> OpenProject(Registry.Game g, JsonObject args, bool byOwner)
    {
        if (args.Select(a => a.Key).FirstOrDefault(k => k is not ("path" or "waitSec")) is string extra)
            return Result($"refused: '{extra}' does not apply to a Unity project. For a project app_launch takes only path and waitSec; the Unity editor is always the one Unity Hub installed for the project's version.", true);
        if (!byOwner)
        {
            if (Registry.Level(g.Access) < 2)
                return Result(g.Access == "none" ? $"\"{g.Name}\" is not allowed: the owner has not given agents access to it. Ask the owner to choose what agents may do in it (Games → {g.Name} → Agents may in the UDriver app)."
                    : $"\"{g.Name}\" allows {g.Access} only. Opening a project runs its editor scripts, so it needs full access ({GameSwitch(g, 2)}).{AskOwner}", true);
        }
        string real;
        try { real = RealPath(g.Path); } catch { real = ""; }
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!real.Equals(g.Path, cmp) || !Directory.Exists(Path.Combine(real, "Assets")) || !Directory.Exists(Path.Combine(real, "ProjectSettings")))
            return Result($"refused: the saved project \"{g.Name}\" is no longer a Unity project folder at {g.Path} (moved, deleted or replaced by a link). The owner can fix the path in the UDriver panel.", true);

        var all = await Targets();
        if (all.FirstOrDefault(t => t.Kind == "editor" && t.Live && t.Game?.Id == g.Id) is Target open)
        {
            Used(open, null);
            return Result($"\"{g.Name}\" is already open in the Unity editor '{open.Name}' on port {open.Port}{(open.Up ? "" : " (still starting; wait with app_connect editorPort=" + open.Port + ")")}. Use editor_play to enter Play Mode.");
        }

        string ver = "";
        try { ver = File.ReadLines(Path.Combine(real, "ProjectSettings", "ProjectVersion.txt")).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("m_EditorVersion:"))?[16..].Trim() ?? ""; } catch { }
        var editors = UnityEditors();
        if (!editors.TryGetValue(ver, out string? unity))
            return Result((ver.Length == 0 ? $"\"{g.Name}\" names no Unity version in ProjectSettings/ProjectVersion.txt." : $"\"{g.Name}\" needs Unity {ver}, which is not installed through Unity Hub.")
                          + $" Installed: {(editors.Count == 0 ? "none" : string.Join(", ", editors.Keys.Order()))}. Install that version with Unity Hub; UDriver never opens a project in another version, because that upgrades it.", true);
        if (!Directory.Exists(Path.Combine(real, "Packages", "com.udriver.agent")) && HttpMode.InstallPackage(real) is string cannot) return Result(cannot, true);

        var used = all.Select(t => t.Port).ToHashSet();
        int lp = ToolManifest.Ports(true).Prepend(EnvPort("UDRIVER_EDITOR_PORT", ToolManifest.EditorPort)).FirstOrDefault(p => !used.Contains(p) && Free(p));
        if (lp == 0) return Result($"No free editor port from {ToolManifest.EditorPort} to {ToolManifest.EditorPortLast}: close a Unity editor first.", true);
        var psi = new ProcessStartInfo(unity)
        {
            UseShellExecute = false, WorkingDirectory = real,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("UDRIVER_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(key);
        if (Environment.GetEnvironmentVariable("UDRIVER_EXPORTS_MAX_MB") is { Length: > 0 } exportsMax) psi.Environment["UDRIVER_EXPORTS_MAX_MB"] = exportsMax;
        string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        psi.Environment["UDRIVER_TOKEN"] = token;
        psi.Environment["UDRIVER_EDITOR_PORT"] = lp.ToString();
        lock (Tokens) Tokens[lp] = token;
        psi.ArgumentList.Add("-projectPath");
        psi.ArgumentList.Add(real);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start Unity");
        Registry.Started(real, proc);
        lock (Launched)
        {
            Launched.RemoveAll(t => t.Port == lp);
            Launched.Add(new Target { Port = lp, Kind = "editor", Path = real, Proc = proc, Owner = Me.Label });
        }
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        int waitSec = IntArg(args, "waitSec", 300);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < waitSec)
        {
            if (proc.HasExited)
            {
                lock (Launched) Launched.RemoveAll(t => t.Proc == proc);
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string log = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log")
                    : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Logs", "Unity", "Editor.log") : Path.Combine(home, ".config", "unity3d", "Editor.log");
                return Result($"Unity {ver} closed while opening \"{g.Name}\" (code {proc.ExitCode}). If the project is open in another Unity editor, close it there first. Editor log: {log}", true);
            }
            if (await PingPid(lp) == proc.Id)
            {
                ToolsChanged();
                var t = (await Targets()).FirstOrDefault(x => x.Port == lp);
                if (t != null) Used(t, null);
                return Result($"Opened \"{g.Name}\" in Unity {ver} (pid {proc.Id}) in {sw.Elapsed.TotalSeconds:0}s; its editor is target '{t?.Name}' on port {lp}. " +
                              "Use editor_play to enter Play Mode. app_kill closes this editor without saving.");
            }
            await Task.Delay(1000);
        }
        return Result($"Unity {ver} is opening \"{g.Name}\" (pid {proc.Id}), but its editor did not answer on port {lp} within {waitSec}s; the first open of a project can take several minutes. " +
                      $"Wait with app_connect editorPort={lp}, then use editor_play.");
    }

    /// <summary>Editor version to executable, only from Unity Hub's install folders.</summary>
    static Dictionary<string, string> UnityEditors()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor")
            : OperatingSystem.IsMacOS() ? "/Applications/Unity/Hub/Editor" : Path.Combine(home, "Unity", "Hub", "Editor"),
        };
        string hub = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "UnityHub") : Path.Combine(home, ".config", "UnityHub");
        try { if ((string?)JsonNode.Parse(File.ReadAllText(Path.Combine(hub, "secondaryInstallPath.json"))) is { Length: > 0 } second && Path.IsPathFullyQualified(second)) roots.Add(second); } catch { }
        string exe = OperatingSystem.IsWindows() ? Path.Combine("Editor", "Unity.exe") : OperatingSystem.IsMacOS() ? "Unity.app/Contents/MacOS/Unity" : Path.Combine("Editor", "Unity");
        var found = new Dictionary<string, string>();
        foreach (var root in roots)
            try { foreach (var d in Directory.GetDirectories(root)) if (File.Exists(Path.Combine(d, exe))) found.TryAdd(Path.GetFileName(d), Path.Combine(d, exe)); } catch { }
        return found;
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
        Stop(k.Proc!, k.Port);
        lock (Launched) Launched.RemoveAll(t => t.Port == k.Port);
        _liveTools = null;
        return Result($"'{k.Name}' terminated." + (other ? $" It had been started by {k.Owner}." : ""));
    }

    static async Task<JsonNode> Connect(JsonObject args, string? want)
    {
        // Only ports where a UDriver instance has published its info file: never poke arbitrary local services.
        foreach (var (key, kind) in new[] { ("port", "game"), ("editorPort", "editor") })
        {
            if (args[key] == null) continue;
            int p = IntArg(args, key, 0);
            lock (Fixed)
            {
                if (!Fixed.ContainsKey(p) && !File.Exists(Path.Combine(HomeDir, p + ".json"))) return Result($"refused: no UDriver instance has registered on port {p}.", true);
                Fixed[p] = kind;
            }
            if (kind == "game") Me.LastGame = p; else Me.LastEditor = p;
        }
        bool Ready(List<Target> ts) => want != null ? Named(ts, want)?.Up == true : ts.Any(t => t.Up && t.Kind == "game");
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

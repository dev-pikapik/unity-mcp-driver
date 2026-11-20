using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>Several agents and several copies of one game: named instances, each agent's journal, input leases, the team space, launching copies.</summary>
internal static partial class Program
{
    static string InstanceId(Target t) => (t.Pid > 0 ? t.Pid : t.Proc?.Id ?? 0).ToString();

    static string Who(Session s) => s.Name.Length > 0 ? s.Name : s.Label;

    static string ClientKey => Me.Client.Length > 0 ? Me.Client : StdioClient;

    static DateTime Started(Target t)
    {
        try { using var p = Process.GetProcessById(int.Parse(InstanceId(t))); return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    static bool Allowed(Registry.Game? g) => g != null && g.Access != "none";

    static string? Line(JsonNode? res, string key) =>
        Regex.Match((string?)res?["content"]?[0]?["text"] ?? "", "^" + key + @": (.*)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    // ---------------- instances ----------------

    static async Task<JsonNode> ListInstances()
    {
        var all = (await Targets()).OrderBy(Started).ToList();
        if (all.Count == 0) return Result("No Unity game or editor with the UDriver package is running. Start one with app_launch or launch_game.");
        var lines = await Task.WhenAll(all.Select(async t =>
        {
            string? scene = null, who = null;
            if (t.Up && t.Kind == "game" && Allowed(t.Game))
                try
                {
                    var (status, body) = await Http2(HttpMethod.Post, t.Port, "/call", new JsonObject { ["tool"] = "status", ["args"] = new JsonObject() }.ToJsonString(), 2);
                    if (status == 200) { var res = JsonNode.Parse(body); scene = Line(res, "scenes"); who = Line(res, "input"); }
                }
                catch { }
            string pid = InstanceId(t);
            string state = t.Up ? "running" : t.Proc is { HasExited: true } ? "exited" : t.Proc != null ? "starting" : "not answering";
            bool mine = t.Port == (t.Kind == "editor" ? Me.LastEditor : Me.LastGame);
            var start = Started(t);
            return $"{t.Name}  id {pid}  {t.Kind}  {state}" + (scene != null ? "  scene " + scene : "") + (start != DateTime.MaxValue ? $"  started {start:HH:mm:ss}" : "")
                   + (t.Unity.Length > 0 ? "  unity " + t.Unity : "") + "  " + AccessText(t.Game?.Access ?? "none") + (who != null ? "  " + who : "") + (mine ? "  <- your current" : "")
                   + (t.Path.Length > 0 ? "  " + t.Path : "");
        }));
        return Result(string.Join("\n", lines) + "\nPass instance=<name or id> to any tool, or use_instance to switch.");
    }

    static async Task<JsonNode> UseInstance(string? want)
    {
        if (want == null) return Result("instance is required: a name or id from list_instances.", true);
        var all = await Targets();
        var t = Named(all, want);
        if (t == null) return Result($"No instance named '{want}'. Instances: {Names(all)}.", true);
        if (Denied("use_instance", null, t) is string no) return Result(no, true);
        if (t.Kind == "editor") Me.LastEditor = t.Port; else Me.LastGame = t.Port;
        Me.LastTarget = t.Name;
        string text = $"Now working in {t.Name} (id {InstanceId(t)}).";
        if (Journal()[InstanceId(t)] is JsonObject e) text += "\nLast time here: " + Entry(InstanceId(t), e, all);
        return Result(text);
    }

    // ---------------- journal ----------------

    static readonly object JournalLock = new();

    static string JournalFile => Path.Combine(HomeDir, "journal", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientKey)))[..16].ToLowerInvariant() + ".json");

    static JsonObject Journal()
    {
        lock (JournalLock)
            try { return JsonNode.Parse(File.ReadAllText(JournalFile)) as JsonObject ?? new JsonObject(); } catch { return new JsonObject(); }
    }

    static readonly AsyncLocal<(string Tool, string Args)> Calling = new();

    /// <summary>What this agent did in an instance; kept per client so it survives reconnects and restarts.</summary>
    static void Note(Target t, JsonNode? res, string? scene = null, string? goal = null)
    {
        string id = InstanceId(t);
        if (id == "0") return;
        lock (JournalLock)
        {
            var j = Journal();
            var e = j[id] as JsonObject ?? new JsonObject();
            j.Remove(id);
            e["name"] = t.Name; e["game"] = t.Game?.Id; e["kind"] = t.Kind; e["path"] = t.Path; e["seen"] = DateTime.UtcNow.ToString("O");
            if ((scene ?? Line(res, "scenes")) is string s) e["scene"] = s;
            if (goal != null) e["goal"] = goal;
            if (res != null && Calling.Value.Tool is { Length: > 0 } tool)
            {
                var acts = e["actions"] as JsonArray ?? new JsonArray();
                e.Remove("actions");
                string args = Calling.Value.Args.Length > 120 ? Calling.Value.Args[..120] + "..." : Calling.Value.Args;
                bool err = res["isError"]?.GetValue<bool>() ?? false;
                acts.Add($"{DateTime.Now:HH:mm:ss} {tool} {args}" + (err ? " (error)" : ""));
                while (acts.Count > 10) acts.RemoveAt(0);
                e["actions"] = acts;
            }
            j[id] = e;
            while (j.Count > 20) j.Remove(j.First().Key);
            try { WritePrivate(JournalFile, j.ToJsonString()); } catch { }
        }
    }

    static string Entry(string id, JsonObject e, List<Target> all)
    {
        var now = all.FirstOrDefault(t => InstanceId(t) == id);
        DateTime.TryParse((string?)e["seen"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen);
        string text = $"{(string?)e["name"]} (id {id}, {(string?)e["kind"]}) " + (now != null ? $"running now as {now.Name}" : "no longer running")
                      + $", last here {seen.ToLocalTime():HH:mm:ss}";
        if (e["scene"] != null) text += ", scene " + (string?)e["scene"];
        if (e["goal"] != null) text += "\n  goal: " + (string?)e["goal"];
        if (e["actions"] is JsonArray a && a.Count > 0) text += "\n  last actions: " + string.Join("; ", a.Select(x => (string?)x));
        if (now == null && all.FirstOrDefault(t => t.Game != null && t.Game.Id == (string?)e["game"]) is Target same) text += $"\n  the same game runs now as {same.Name} (id {InstanceId(same)})";
        return text;
    }

    static async Task<JsonNode> WhereWasI(JsonObject args, string? want)
    {
        var all = await Targets();
        Target? t = null;
        if (want != null && (t = Named(all, want)) == null && !Journal().Any(x => x.Key == want || (string?)x.Value?["name"] == want))
            return Result($"No instance named '{want}'. Instances: {Names(all)}.", true);
        if (t != null && Denied("where_was_i", null, t) is string no) return Result(no, true);
        if ((string?)args["goal"] is string goal)
        {
            t ??= all.FirstOrDefault(x => x.Port == Me.LastGame && x.Kind == "game") ?? all.FirstOrDefault(x => x.Port == Me.LastEditor && x.Kind == "editor");
            if (t == null) return Result("No current instance to note the goal for: pass instance, or use_instance first.", true);
            goal = new string(goal.Where(c => !char.IsControl(c)).ToArray());
            Note(t, null, goal: goal.Length > 300 ? goal[..300] : goal);
        }
        var games = Registry.Games;
        var lines = new List<string>();
        int hidden = 0;
        foreach (var (id, node) in Journal().Reverse())
        {
            if (node is not JsonObject e) continue;
            if (want != null && id != want && !string.Equals((string?)e["name"], t?.Name ?? want, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Allowed(games.FirstOrDefault(g => g.Id == (string?)e["game"]))) { hidden++; continue; }
            lines.Add(Entry(id, e, all));
        }
        string cur = all.FirstOrDefault(x => x.Name == Me.LastTarget) is Target c && Allowed(c.Game) ? $"Your current instance: {c.Name}." : "No current instance.";
        string text = lines.Count == 0 ? "Nothing recorded yet for you" + (want != null ? " in " + want : "") + "." : string.Join("\n", lines);
        if (hidden > 0) text += $"\n{hidden} more place(s) are hidden: the owner has not given access to those games.";
        return Result(cur + "\n" + text);
    }

    // ---------------- input leases ----------------

    /// <summary>The game agent keeps the lease, so agents on every UDriver server see the same one; this id tells callers apart there.</summary>
    internal static string HolderId() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Me.Client.Length > 0 ? Me.Client : "stdio " + Environment.ProcessId)))[..16];

    static async Task<JsonNode> TakeControls(JsonObject args, string? want)
    {
        var t = Pick(await Targets(), "game", want, out var error);
        if (error != null) return Result(error, true);
        if (t == null) return Result("No game is running.", true);
        if (Denied("take_controls", args, t) is string no) return Result(no, true);
        var res = await TryCall(t.Port, "take_controls", args);
        if (res == null) return Result($"The game '{t.Name}' is not reachable on port {t.Port}.", true);
        if (!(res["isError"]?.GetValue<bool>() ?? false)) { Me.LastGame = t.Port; Me.LastTarget = t.Name; }
        return res;
    }

    // ---------------- team space ----------------

    const int TeamKeep = 300, TeamDataMax = 4000;
    static string TeamFile => Path.Combine(HomeDir, "team.jsonl");
    static string ClaimsFile => Path.Combine(HomeDir, "claims.json");

    /// <summary>Every UDriver server on this computer shares the team files; the lock keeps their reads and writes whole.</summary>
    static T TeamLocked<T>(Func<T> work) => Registry.Locked(TeamFile + ".lock", work);

    static List<JsonObject> TeamLoad()
    {
        var team = new List<JsonObject>();
        try
        {
            foreach (var line in File.ReadLines(TeamFile))
                try { if (JsonNode.Parse(line) is JsonObject o) team.Add(o); } catch { }
        }
        catch { }
        return team;
    }

    static long LastSeq(List<JsonObject> team) => team.Count > 0 ? (long?)team[^1]["seq"] ?? 0 : 0;

    static int Left(JsonNode? claim) =>
        DateTime.TryParse((string?)claim?["until"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var until) ? (int)(until - DateTime.UtcNow).TotalSeconds : 0;

    /// <summary>Claims still running whose server is still alive; a claim dies with the server that holds it.</summary>
    static JsonObject LiveClaims()
    {
        JsonObject all;
        try { all = JsonNode.Parse(File.ReadAllText(ClaimsFile)) as JsonObject ?? new(); } catch { all = new(); }
        foreach (var (key, c) in all.ToList())
            if (Left(c) <= 0 || (int?)c?["pid"] is not int pid || !Alive(pid)) all.Remove(key);
        return all;
    }

    /// <summary>The instance a team call is about, or null for the global space; an error when that game is not allowed.</summary>
    static async Task<(Target? T, string? Error)> TeamScope(string? want, string tool)
    {
        if (want == null) return (null, null);
        var all = await Targets();
        var t = Named(all, want);
        if (t == null) return (null, $"No instance named '{want}'. Instances: {Names(all)}.");
        return (t, Denied(tool, null, t));
    }

    static string? Plain(string? s, int max, string what) =>
        string.IsNullOrWhiteSpace(s) ? $"{what} is required." : s.Length > max || s.Any(char.IsControl) ? $"{what} must be one line of at most {max} characters." : null;

    static async Task<JsonNode> TeamPost(JsonObject args, string? want)
    {
        string? topic = (string?)args["topic"];
        if (Plain(topic, 64, "topic") is string bad) return Result(bad, true);
        var data = args["data"]?.DeepClone();
        if (data == null || data.ToJsonString().Length > TeamDataMax) return Result($"data is required: text or JSON of at most {TeamDataMax} characters.", true);
        var (t, error) = await TeamScope(want, "team_post");
        if (error != null) return Result(error, true);
        long seq = TeamLocked(() =>
        {
            var team = TeamLoad();
            long next = LastSeq(team) + 1;
            team.Add(new JsonObject
            {
                ["seq"] = next, ["time"] = DateTime.UtcNow.ToString("O"), ["from"] = Who(Me), ["game"] = t?.Game?.Id, ["instance"] = t?.Name,
                ["topic"] = topic, ["data"] = data,
            });
            WritePrivate(TeamFile, string.Join("\n", team.TakeLast(TeamKeep).Select(o => o.ToJsonString())) + "\n");
            return next;
        });
        return Result($"Posted #{seq} to {(t != null ? t.Name : "the whole team")} under '{topic}'.");
    }

    static async Task<JsonNode> TeamRead(JsonObject args, string? want)
    {
        long since = IntArg(args, "since", 0);
        string? topic = (string?)args["topic"];
        var (t, error) = await TeamScope(want, "team_read");
        if (error != null) return Result(error, true);
        var games = Registry.Games;
        var (team, live) = TeamLocked(() => (TeamLoad(), LiveClaims()));
        long last = LastSeq(team);
        var seen = team.Where(o => (long?)o["seq"] > since && (topic == null || (string?)o["topic"] == topic)
                                   && ((string?)o["game"] is not string g ? t == null : Allowed(games.FirstOrDefault(x => x.Id == g)) && (t == null || (string?)o["instance"] == t.Name))).ToList();
        var claims = new List<string>();
        foreach (var (key, c) in live)
        {
            int cut = key.IndexOf('\n');
            string game = key[..cut];
            if (game.Length == 0 ? t == null : Allowed(games.FirstOrDefault(x => x.Id == game)) && (t == null || t.Game?.Id == game))
                claims.Add($"'{key[(cut + 1)..]}' by {c?["who"]} for {Left(c)} s");
        }
        var text = new StringBuilder();
        foreach (var o in seen)
            text.Append($"#{o["seq"]} {(DateTime.TryParse((string?)o["time"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at.ToLocalTime().ToString("HH:mm:ss") : "")} " +
                        $"{o["from"]}{((string?)o["instance"] is string i ? " in " + i : "")} [{o["topic"]}]: {o["data"]?.ToJsonString()}\n");
        if (seen.Count == 0) text.Append("No new posts.\n");
        if (claims.Count > 0) text.Append("Claims: " + string.Join("; ", claims) + "\n");
        text.Append($"Next time pass since={last}.");
        return Result(text.ToString());
    }

    static async Task<JsonNode> Claim(JsonObject args, string? want, bool take)
    {
        string? key = (string?)args["key"];
        if (Plain(key, 128, "key") is string bad) return Result(bad, true);
        var (t, error) = await TeamScope(want, take ? "claim" : "release");
        if (error != null) return Result(error, true);
        string id = (t?.Game?.Id ?? "") + "\n" + key;
        string where = t != null ? " in " + t.Name : "";
        int ttl = Math.Clamp(IntArg(args, "ttl", 300), 5, 3600);
        return TeamLocked(() =>
        {
            var claims = LiveClaims();
            var c = claims[id];
            if (c != null && (string?)c["owner"] != HolderId())
                return Result($"'{key}'{where} is claimed by {c["who"]} for {Left(c)} more s. Pick another task or ask in team_post.", true);
            if (!take && c == null) return Result($"'{key}'{where} was not claimed.");
            if (take) claims[id] = new JsonObject { ["owner"] = HolderId(), ["pid"] = Environment.ProcessId, ["who"] = Who(Me), ["until"] = DateTime.UtcNow.AddSeconds(ttl).ToString("O") };
            else claims.Remove(id);
            WritePrivate(ClaimsFile, claims.ToJsonString());
            return Result(take ? $"You claimed '{key}'{where} for {ttl} s." : $"Released '{key}'{where}.");
        });
    }

    // ---------------- copies ----------------

    static async Task<JsonNode> LaunchCopies(JsonObject args)
    {
        string path = (string?)args["path"] ?? throw new ArgumentException("path is required");
        int copies = IntArg(args, "copies", 1);
        if (copies < 1 || copies > 8) return Result("copies must be from 1 to 8.", true);
        string product = Registry.Find(path)?.Name ?? Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\'));
        var text = new List<string>();
        bool failed = false;
        for (int i = 0; i < copies; i++)
        {
            var taken = (await Targets()).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int slot = 1;
            while (taken.Contains($"{product} {slot}")) slot++;
            var one = new JsonObject { ["path"] = path, ["name"] = $"{product} {slot}", ["args"] = args["args"]?.DeepClone(), ["waitSec"] = args["waitSec"]?.DeepClone() };
            var res = await Launch(one, slot: slot);
            string line = (string?)res["content"]?[0]?["text"] ?? "";
            text.Add(line.Split('\n')[0]);
            if (res["isError"]?.GetValue<bool>() ?? false) { failed = true; break; }
        }
        return Result(string.Join("\n", text) + (failed ? "" : "\nEach copy keeps its saves and settings in its own slot. list_instances shows them; pass instance=<name> to work in one."), failed);
    }
}

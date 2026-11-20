using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attr = Terminal.Gui.Drawing.Attribute;

// Terminal mode and short commands. Both talk to the running server's /panel/api with app.key, like the native apps.
static class Cli
{
    static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_HTTP_PORT"), out int p) ? p : 8787;
    static readonly HttpClient Http = new() { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(20) };
    internal static readonly string[] AccessNames = { "Not allowed", "Watch", "Play", "Everything" };

    internal static async Task<int> Run(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["tui"]:
                    if (await Ensure() is string why) return Say(why);
                    Screen.Show();
                    return 0;
                case ["status"]:
                    if (!await Running()) return Say("UDriver is not running.");
                    Console.WriteLine(await Status());
                    return 0;
                case ["approve" or "deny", var id]:
                    return await Act($"pending/{args[0]}", ("id", id), args[0] == "approve" ? "Allowed." : "Denied.");
                case ["code"]:
                    if (await Ensure() is string down) return Say(down);
                    var c = await Post("remote/code");
                    if (c["ok"]?.GetValue<bool>() != true) return Say((string?)c["error"] ?? "No code.");
                    Console.WriteLine($"{c["code"]}  (works once, expires in 10 minutes)\n{c["address"]}");
                    return 0;
                case ["tools", "on" or "off", var tool, ..] when args.Length == 3 || args is [_, _, _, "--for", _]:
                    return await Act("tools/set", ("tool", tool), args[1] == "on" ? "Turned on." : "Turned off.",
                        ("enabled", args[1] == "on" ? "true" : "false"), ("client", args.Length == 5 ? args[4] : ""));
                case ["levels"]:
                    if (await Ensure() is string off) return Say(off);
                    Console.WriteLine("Level                    This computer  Remote");
                    foreach (var l in (await Get("tools"))["levels"]!.AsArray())
                        Console.WriteLine($"{l!["level"]} {l["name"],-22} {((bool?)l["local"] == true ? "On" : "Off"),-14} {((int?)l["level"] == 3 ? "Never" : (bool?)l["remote"] == true ? "On" : "Off")}\n  {l["text"]}");
                    return 0;
                case ["levels", "on" or "off", var level, ..] when args.Length == 3 || args is [_, _, _, "--remote"]:
                    return await Act("levels/set", ("level", level), args[1] == "on" ? "Turned on." : "Turned off.",
                        ("enabled", args[1] == "on" ? "true" : "false"), ("for", args.Length == 4 ? "remote" : "local"));
                case ["clear-mark", var game]:
                    return await Act("games/clear-mark", ("id", game), "The mark is cleared. The game's own code can reach the computer again from its next start.");
                case ["computer", "on" or "off"]:
                    return await Act("levels/set", ("level", "3"), args[1] == "on" ? "Computer access is on. At your own risk." : "Computer access is off.",
                        ("enabled", args[1] == "on" ? "true" : "false"));
            }
            return Say("Usage: udriver-mcp tui | status | approve <id> | deny <id> | code | tools on|off <tool> [--for <client>] | levels [on|off <0-2> [--remote]] | computer on|off | clear-mark <game>");
        }
        catch (HttpRequestException e) { return Say("UDriver does not answer: " + e.Message); }
        catch (TaskCanceledException) { return Say("UDriver did not answer in time."); }
    }

    static int Say(string text) { Console.Error.WriteLine(text); return 1; }

    static async Task<int> Act(string path, (string, string) first, string done, params (string, string)[] more)
    {
        if (await Ensure() is string why) return Say(why);
        var j = await Post(path, [first, .. more]);
        if (j["ok"]?.GetValue<bool>() != true) return Say((string?)j["error"] ?? "Failed.");
        Console.WriteLine(done);
        return 0;
    }

    static async Task<string> Status()
    {
        var games = await Get("games");
        var agents = await Get("agents");
        var pending = await Get("pending");
        var remote = await Get("remote");
        var w = new StringWriter();
        w.WriteLine($"UDriver is running on port {Port}.");
        w.WriteLine("Access from anywhere: " + (remote["on"]?.GetValue<bool>() == true ? "On  " + remote["address"] : "Off"));
        foreach (var g in games["games"]!.AsArray())
            w.WriteLine($"game   {g!["name"],-24} {g["state"],-12} agents may: {AccessNames[Registry.Level((string?)g["access"] ?? "watch") + 1]}{((bool?)g["changed"] == true ? "  changed by an agent: run clear-mark to trust it again" : "")}{((string?)g["native"] is { Length: > 0 } why ? "  look and play only while Computer access is off: " + why : "")}");
        foreach (var a in agents["agents"]!.AsArray())
            w.WriteLine($"agent  {a!["label"],-24} {a["from"]}{((bool?)a["disconnected"] == true ? "  (disconnected)" : "")}");
        foreach (var r in pending["pending"]!.AsArray())
            w.WriteLine($"ask    {r!["id"]}  {r["app"]} -> {r["to"]}  check number {r["check"]}");
        return w.ToString().TrimEnd();
    }

    static bool _signed;
    internal static async Task<JsonObject> Get(string path) => await Send(new HttpRequestMessage(HttpMethod.Get, "/panel/api/" + path));
    internal static async Task<JsonObject> Post(string path, params (string Key, string Value)[] form) =>
        await Send(new HttpRequestMessage(HttpMethod.Post, "/panel/api/" + path) { Content = new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Key, f.Value))) });

    static async Task<JsonObject> Send(HttpRequestMessage req)
    {
        if (!_signed)
        {
            string key = File.ReadAllText(Path.Combine(Program.HomeDir, "app.key")).Trim();
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            _signed = true;
        }
        using var res = await Http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        try { return JsonNode.Parse(body) as JsonObject ?? new JsonObject { ["ok"] = false, ["error"] = body }; }
        catch { return new JsonObject { ["ok"] = false, ["error"] = body.Length > 200 ? body[..200] : body }; }
    }

    static async Task<bool> Running()
    {
        try { using var r = await Http.GetAsync("/panel"); return true; } catch { return false; }
    }

    // Starts this same program as the background server when nothing answers, and keeps it running after the terminal closes.
    static async Task<string?> Ensure()
    {
        if (await Running()) return File.Exists(Path.Combine(Program.HomeDir, "app.key")) ? null : "Another program uses port " + Port + ".";
        string exe = Environment.ProcessPath!;
        var argv = Path.GetFileNameWithoutExtension(exe) == "dotnet" ? new List<string> { typeof(Cli).Assembly.Location } : new List<string>();
        string logs = Path.Combine(Program.HomeDir, "logs");
        Directory.CreateDirectory(logs);
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(exe) { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exec nohup \"$0\" \"$@\" >>\"$UDRIVER_LOG\" 2>&1 </dev/null", exe } };
        argv.ForEach(psi.ArgumentList.Add);
        psi.UseShellExecute = false;
        psi.Environment["UDRIVER_HTTP_PORT"] = Port.ToString();
        psi.Environment["UDRIVER_PANEL"] = "1";
        psi.Environment["UDRIVER_LOG"] = Path.Combine(logs, "server.log");
        string package = Path.Combine(Program.HomeDir, "package");
        if (Environment.GetEnvironmentVariable("UDRIVER_DIST") == null && Directory.Exists(package)) psi.Environment["UDRIVER_DIST"] = package;
        using var proc = Process.Start(psi)!;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(200);
            if (await Running() && File.Exists(Path.Combine(Program.HomeDir, "app.key"))) return null;
            if (OperatingSystem.IsWindows() && proc.HasExited) break;
        }
        return "UDriver could not start. Details are in " + psi.Environment["UDRIVER_LOG"];
    }
}

// The full-screen view from the approved mockup: tabs, one table per tab, the approval prompt and the one-time code.
sealed class Screen : Terminal.Gui.Views.Runnable
{
    static readonly string[] Tabs = { "Games", "Agents", "Tools", "Access" };
    int _tab, _row;
    string _for = "";
    JsonObject _games = new(), _agents = new(), _pending = new(), _remote = new(), _tools = new(), _clients = new();
    string _note = "", _code = "";
    DateTime _codeUntil;
    bool _down;

    internal static void Show()
    {
        using IApplication app = Application.Create();
        app.Init();
        var s = new Screen { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _ = s.Poll(app);
        app.Run(s);
    }

    async Task Poll(IApplication app)
    {
        while (true)
        {
            try
            {
                var all = await Task.WhenAll(Cli.Get("games"), Cli.Get("agents"), Cli.Get("pending"), Cli.Get("remote"), Cli.Get("tools"), Cli.Get("clients"));
                app.Invoke(() => { (_games, _agents, _pending, _remote, _tools, _clients) = (all[0], all[1], all[2], all[3], all[4], all[5]); _down = false; SetNeedsDraw(); });
            }
            catch { app.Invoke(() => { _down = true; SetNeedsDraw(); }); }
            await Task.Delay(1000);
        }
    }

    static JsonArray List(JsonObject o, string name) => o[name] as JsonArray ?? new JsonArray();
    JsonArray Rows => _tab switch { 0 => List(_games, "games"), 1 => People, 2 => Features, _ => Remotes };
    JsonArray Remotes => new(List(_clients, "clients").Where(c => (string?)c!["kind"] is not ("stdio" or "link") && (string?)c["status"] is "connected" or "allowed").Select(c => c!.DeepClone()).ToArray());

    JsonArray People
    {
        get
        {
            var clients = List(_clients, "clients");
            var live = List(_agents, "agents").Where(a => (bool?)a!["disconnected"] != true).Select(a => a!.DeepClone()).ToList();
            foreach (var a in live) a["label"] = (string?)clients.FirstOrDefault(c => (string?)c!["id"] == (string?)a["client"])?["name"] ?? (string?)a["label"] ?? "Agent";
            var before = clients.Where(c => (string?)c!["kind"] != "stdio" && (string?)c["status"] == "allowed" && !live.Any(a => (string?)a["client"] == (string?)c["id"] || (string?)a["label"] == (string?)c["name"]))
                .GroupBy(c => (string?)c!["name"] ?? "Agent")
                .Select(g => (JsonNode)new JsonObject
                {
                    ["label"] = g.Key, ["kind"] = (string?)g.First()!["kind"], ["from"] = (string?)g.First()!["kind"] is "remote" or "link" ? "remote" : "",
                    ["ids"] = new JsonArray(g.Select(c => c!["id"]!.DeepClone()).ToArray()),
                });
            return new JsonArray(live.Concat(before).ToArray());
        }
    }

    JsonArray Features
    {
        get
        {
            var rows = new JsonArray();
            string last = "";
            bool On(JsonNode? t) => t!["enabled"]?.GetValue<bool>() != false && (_for == "" || !((t["offFor"] as JsonArray)?.Any(x => (string?)x == _for) ?? false));
            foreach (var f in List(_tools, "tools").GroupBy(t => (string?)t!["feature"] ?? ""))
            {
                string group = (string?)f.First()!["group"] ?? "";
                rows.Add(new JsonObject
                {
                    ["group"] = group == last ? "" : group, ["feature"] = f.Key, ["tools"] = string.Join(",", f.Select(t => (string?)t!["name"])),
                    ["on"] = f.Count(On), ["all"] = f.Count(), ["locked"] = _for != "" && f.All(t => t!["enabled"]?.GetValue<bool>() == false),
                });
                last = group;
            }
            return rows;
        }
    }
    JsonNode? Pick => Rows.Count == 0 ? null : Rows[Math.Clamp(_row, 0, Rows.Count - 1)];
    JsonNode? Ask => List(_pending, "pending").FirstOrDefault();

    static readonly Color Bg = new(30, 30, 32), Fg = new(225, 225, 225), Dim_ = new(140, 140, 145), Green = new(80, 200, 110), Yellow = new(240, 200, 40), Blue = new(90, 190, 255), Red = new(240, 90, 80);
    void Put(int x, int y, string text, Color fg, Color? bg = null, TextStyle style = TextStyle.None)
    {
        SetAttribute(new Attr(fg, bg ?? Bg, style));
        AddStr(x, y, text);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        SetAttribute(new Attr(Fg, Bg));
        FillRect(Viewport, new System.Text.Rune(' '));
        int y = 1, w = Viewport.Width;
        Put(2, y, "UDriver", Fg, style: TextStyle.Bold);
        if (_down) Put(11, y, "● Not answering", Red);
        else
        {
            Put(11, y, "● Ready", Green);
            bool on = _remote["on"]?.GetValue<bool>() == true;
            Put(22, y, "Access from anywhere:", Dim_);
            Put(44, y, on ? "On" : "Off", on ? Green : Dim_);
            if (on) Put(48, y, (string?)_remote["address"] ?? "", Blue);
        }
        y += 2;
        for (int i = 0, x = 2; i < Tabs.Length; x += Tabs[i].Length + 6, i++)
            Put(x, y, $" {i + 1} {Tabs[i]} ", i == _tab ? Bg : Dim_, i == _tab ? Fg : null);
        if (_tab == 2)
        {
            if (List(_clients, "clients").All(c => (string?)c!["id"] != _for)) _for = "";
            Put(48, y, "For:", Dim_);
            Put(53, y, Cut("◂ " + ((string?)List(_clients, "clients").FirstOrDefault(c => (string?)c!["id"] == _for)?["name"] ?? "All agents") + " ▸", w - 55), Fg, style: TextStyle.Bold);
        }
        y += 2;

        var (heads, cols) = _tab switch
        {
            0 => (new[] { "GAME", "STATUS", "AGENTS MAY", "AGENT" }, new[] { 4, 20, 38, 54 }),
            1 => (new[] { "AGENT", "FROM", "GAME", "LAST SEEN" }, new[] { 4, 28, 48, 66 }),
            2 => (new[] { "GROUP", "FEATURE", "STATE", "TOOLS" }, new[] { 4, 12, 42, 48 }),
            _ => (new[] { "SIGNED-IN APP", "KIND", "", "" }, new[] { 4, 40, 60, 70 }),
        };
        for (int i = 0; i < heads.Length; i++) Put(cols[i], y, heads[i], Dim_);
        y++;
        var rows = Rows;
        _row = Math.Clamp(_row, 0, Math.Max(rows.Count - 1, 0));
        if (rows.Count == 0) Put(4, y++, _tab switch { 0 => "No games yet. Add one in the UDriver app or panel.", 1 => "No agent is connected right now.", 2 => "No tools.", _ => "No apps have signed in from outside this computer." }, Dim_);
        int bottom = Viewport.Height - (_tab == 0 ? 13 : 11), first = Math.Max(0, _row - Math.Max(bottom - y, 3) + 1);
        for (int i = first; i < rows.Count && y < bottom; i++, y++)
        {
            var r = rows[i]!;
            bool sel = i == _row;
            Color? bg = sel ? Fg : null;
            Color fg = sel ? Bg : Fg;
            if (sel) { SetAttribute(new Attr(Bg, Fg)); FillRect(new System.Drawing.Rectangle(1, y, Math.Min(w - 2, 78), 1), new System.Text.Rune(' ')); Put(2, y, "▸", fg, bg); }
            string[] cells = Cells(r);
            for (int c = 0; c < cells.Length; c++)
            {
                var color = fg;
                if (!sel && _tab == 0 && c == 1) color = cells[1].StartsWith("● R") ? Fg : cells[1].StartsWith("● E") ? Yellow : Dim_;
                if (!sel && _tab == 2 && c == 2) color = (bool?)r["locked"] == true ? Dim_ : cells[2] == "On" ? Green : Red;
                if (!sel && _tab == 2 && c == 3) color = cells[3].StartsWith("some off") ? Yellow : Dim_;
                Put(cols[c], y, Cut(cells[c], (c + 1 < cols.Length ? cols[c + 1] : w) - cols[c] - 1), color, bg);
            }
        }
        if (_tab == 0 && Pick is JsonNode g)
        {
            Put(4, ++y, "Engine", Dim_);
            Put(12, y++, Cut((string?)g["engine"] is { Length: > 0 } e ? e : "Unity", w - 14), Fg);
        }

        y = Math.Max(y + 1, Viewport.Height - 11);
        Put(2, y, new string('─', Math.Min(w - 4, 76)), Dim_);
        y += 2;
        if (Ask is JsonNode a)
        {
            int more = List(_pending, "pending").Count - 1;
            Put(2, y, "!", Yellow, style: TextStyle.Bold);
            Put(4, y++, $"{a["app"]} wants to use your games" + (more > 0 ? $"   (+{more} more)" : ""), Fg, style: TextStyle.Bold);
            Put(4, y++, $"Access goes to {a["to"]}", Fg);
            Put(4, y, "Check number", Fg);
            Put(18, y, $"{a["check"]}", Fg, style: TextStyle.Bold);
            Put(24, y++, "Allow only if the agent shows the same number.", Dim_);
            y++;
            Put(4, y, " A ", Bg, Fg); Put(8, y, "Allow", Fg);
            Put(18, y, " D ", Bg, Fg); Put(22, y++, "Deny", Fg);
        }
        else y += 5;
        y++;
        int left = (int)Math.Ceiling((_codeUntil - DateTime.UtcNow).TotalMinutes);
        if (_code.Length > 0 && left > 0)
        {
            Put(2, y, "Code for a new agent:", Dim_);
            Put(25, y, _code, Fg, style: TextStyle.Bold);
            Put(27 + _code.Length, y, $"once, {left} min", Dim_);
        }
        else if (_note.Length > 0) Put(2, y, Cut(_note, w - 4), Yellow);

        string keys = _tab switch
        {
            0 => "↑↓ choose   ←→ change access   Enter start/stop   N new code   Q quit",
            1 => "↑↓ choose   X disconnect or remove   N new code   Q quit",
            2 => "↑↓ choose   ←→ for which agent   Space turn on/off   N new code   Q quit",
            _ => "↑↓ choose   X sign out app   N new code   Q quit",
        };
        Put(2, Viewport.Height - 1, Cut("1-4 tabs   " + keys, w - 4), Dim_);
        return true;
    }

    string[] Cells(JsonNode r)
    {
        switch (_tab)
        {
            case 0:
                string state = (string?)r["state"] ?? "";
                string agent = List(_agents, "agents").Select(x => (string?)x!["target"] ?? "").Any(t => t.Length > 0 && (t == (string?)r["id"] || t.Contains((string?)r["name"] ?? "\0")))
                    ? string.Join(", ", List(_agents, "agents").Where(x => ((string?)x!["target"] ?? "") is { Length: > 0 } t && (t == (string?)r["id"] || t.Contains((string?)r["name"] ?? "\0"))).Select(x => (string?)x!["label"]))
                    : "—";
                return new[] { (string?)r["name"] ?? "", (state == "not running" ? "○ " : "● ") + char.ToUpper(state[0]) + state[1..], $"[ {Cli.AccessNames[Registry.Level((string?)r["access"] ?? "watch") + 1]} ]", agent };
            case 1:
                int? ago = (int?)r["agoSec"];
                return new[] { (string?)r["label"] ?? "", (string?)r["from"] == "remote" ? "From anywhere" : "On this computer", (string?)r["target"] ?? "", r["ids"] != null ? "Not connected now" : ago is int s ? (s < 60 ? "just now" : $"{s / 60} min ago") : "" };
            case 2:
                int on = (int?)r["on"] ?? 0, all = (int?)r["all"] ?? 0;
                return new[] { (string?)r["group"] ?? "", (string?)r["feature"] ?? "", on > 0 ? "On" : "Off", (on > 0 && on < all ? "some off · " : "") + ((string?)r["tools"] ?? "").Replace(",", " · ") };
            default:
                return new[] { (string?)r["name"] ?? "", (string?)r["kind"] ?? "", "", "" };
        }
    }

    static string Cut(string s, int n) => n <= 1 ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";

    protected override bool OnKeyDown(Key key)
    {
        char ch = char.ToLowerInvariant((char)key.AsRune.Value);
        var pick = Pick;
        if (key == Key.CursorUp) _row--;
        else if (key == Key.CursorDown) _row++;
        else if (ch is >= '1' and <= '4') { _tab = ch - '1'; _row = 0; }
        else if (key == Key.Tab) { _tab = (_tab + 1) % Tabs.Length; _row = 0; }
        else if (ch == 'q' || key == Key.Esc) App?.RequestStop();
        else if (ch is 'a' or 'd' && Ask is JsonNode a) Do($"pending/{(ch == 'a' ? "approve" : "deny")}", ch == 'a' ? "Allowed." : "Denied.", ("id", (string?)a["id"] ?? ""));
        else if (ch == 'n') _ = NewCode();
        else if (_tab == 0 && pick != null && (key == Key.CursorLeft || key == Key.CursorRight))
        {
            int level = Math.Clamp(Registry.Level((string?)pick["access"] ?? "watch") + (key == Key.CursorLeft ? -1 : 1), 0, 2);
            pick["access"] = Registry.Levels[level];
            Do("games/edit", "Agents may now: " + Cli.AccessNames[level + 1] + ".", ("id", (string?)pick["id"] ?? ""), ("access", Registry.Levels[level]));
        }
        else if (_tab == 0 && pick != null && key == Key.Enter)
            Do((string?)pick["state"] == "not running" ? "games/start" : "games/stop", (string?)pick["state"] == "not running" ? "Starting…" : "Stopped.", ("id", (string?)pick["id"] ?? ""));
        else if (_tab == 1 && pick?["ids"] is JsonArray ids && ch == 'x')
        {
            if ((string?)pick["kind"] == "link") _note = "The secret link cannot be removed here.";
            else foreach (var id in ids) Do("clients/revoke", "Removed.", ("id", (string?)id ?? ""));
        }
        else if (_tab == 1 && pick != null && ch == 'x') Do("agents/disconnect", "Disconnected.", ("id", pick["id"]?.ToString() ?? ""));
        else if (_tab == 2 && (key == Key.CursorLeft || key == Key.CursorRight))
        {
            var all = List(_clients, "clients").Select(c => (string?)c!["id"] ?? "").Prepend("").ToList();
            _for = all[(all.IndexOf(_for) + (key == Key.CursorLeft ? all.Count - 1 : 1)) % all.Count];
        }
        else if (_tab == 2 && pick != null && ch == ' ' && (bool?)pick["locked"] == true) _note = "Switched off for every agent. Turn it on under All agents first.";
        else if (_tab == 2 && pick != null && ch == ' ')
        {
            bool on = (int?)pick["on"] > 0;
            string[] names = ((string?)pick["tools"] ?? "").Split(',');
            foreach (var t in List(_tools, "tools").Where(t => names.Contains((string?)t!["name"])))
                if (_for == "") t!["enabled"] = !on;
                else if (t!["offFor"] is JsonArray off) { if (on) off.Add(_for); else foreach (var x in off.Where(x => (string?)x == _for).ToList()) off.Remove(x); }
            Do("tools/set", on ? "Turned off." : "Turned on.", ("tool", string.Join(",", names)), ("enabled", on ? "false" : "true"), ("client", _for));
        }
        else if (_tab == 3 && pick != null && ch == 'x') Do("clients/revoke", "Signed out.", ("id", (string?)pick["id"] ?? ""));
        else return base.OnKeyDown(key);
        SetNeedsDraw();
        return true;
    }

    void Do(string path, string done, params (string, string)[] form) => _ = Task.Run(async () =>
    {
        string note;
        try { var j = await Cli.Post(path, form); note = j["ok"]?.GetValue<bool>() == true ? done : (string?)j["error"] ?? "Failed."; }
        catch (Exception e) { note = e.Message; }
        App?.Invoke(() => { _note = note; _code = ""; SetNeedsDraw(); });
    });

    async Task NewCode()
    {
        JsonObject j;
        try { j = await Cli.Post("remote/code"); } catch (Exception e) { j = new JsonObject { ["error"] = e.Message }; }
        App?.Invoke(() =>
        {
            if (j["ok"]?.GetValue<bool>() == true) { _code = (string?)j["code"] ?? ""; _codeUntil = DateTime.UtcNow.AddSeconds((int?)j["expiresSec"] ?? 600); }
            else { _code = ""; _note = (string?)j["error"] ?? "No code."; }
            SetNeedsDraw();
        });
    }
}

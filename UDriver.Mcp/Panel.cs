using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

internal static partial class HttpMode
{
    static string EnvFile => Path.Combine(Program.HomeDir, "remote.env");
    static string TunnelYml => Path.Combine(Program.HomeDir, "cloudflared.yml");
    static readonly string Launcher = OperatingSystem.IsWindows() ? "UDriver.cmd" : "UDriver.command";

    static bool _panel;
    static string? _panelKey;
    static string PanelKey => _panelKey ??= LoadPanelKey();
    static string PidFile => Path.Combine(Program.HomeDir, "panel.pid");
    static readonly Dictionary<string, (DateTime Exp, string Csrf)> Sessions = new();
    static readonly Dictionary<string, DateTime> Tickets = new();   // sha256(ticket) -> expiry
    const string LocalOnly = "This is available only on the computer that runs UDriver.";
    static readonly List<PosixSignalRegistration> Signals = new();

    static Process? _cf;
    static readonly HashSet<string> CfConns = new();
    static string _cfNote = "";

    static readonly Regex ConnRx = new(@"connIndex=(\d+)", RegexOptions.Compiled);

    // ---------------- lifecycle ----------------

    static void StartPanel()
    {
        foreach (var sig in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
            try { Signals.Add(PosixSignalRegistration.Create(sig, c => { c.Cancel = true; Environment.Exit(0); })); } catch { }
        if (_public.Length > 0) { StartTunnel(); Notify("UDriver started: " + _public); }
        _ = Task.Run(Monitor);
        if (Env("UDRIVER_OPEN_PANEL") == "1") OpenBrowser($"http://127.0.0.1:{_port}/panel?ticket={NewTicket()}");
    }

    static string LoadPanelKey()
    {
        string file = Path.Combine(Program.HomeDir, "panel.key");
        try { string k = File.ReadAllText(file).Trim(); if (k.Length >= 32) return k; } catch { }
        string key = Rand();
        Program.WritePrivate(file, key);
        return key;
    }

    /// <summary>A link that opens the local panel once, within a minute.</summary>
    static string NewTicket()
    {
        string t = Rand();
        lock (Gate)
        {
            foreach (var k in Tickets.Where(x => x.Value < DateTime.UtcNow).Select(x => x.Key).ToList()) Tickets.Remove(k);
            if (Tickets.Count < 20) Tickets[Hash(t)] = DateTime.UtcNow.AddSeconds(60);
        }
        return t;
    }

    static bool TakeTicket(string t)
    {
        lock (Gate) return Tickets.Remove(Hash(t), out var exp) && exp > DateTime.UtcNow;
    }

    static void SignIn(HttpContext ctx, bool local)
    {
        string sid = Rand();
        var life = local ? TimeSpan.FromDays(30) : TimeSpan.FromHours(2);
        lock (Gate)
        {
            foreach (var k in Sessions.Where(x => x.Value.Exp < DateTime.UtcNow).Select(x => x.Key).ToList()) Sessions.Remove(k);
            Sessions[Hash(sid)] = (DateTime.UtcNow + life, Rand(18));
        }
        ctx.Response.Cookies.Append("udp", sid, new CookieOptions { HttpOnly = true, Secure = !local, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = life });
    }

    /// <summary>Brings the tunnel back if cloudflared exits while remote access is on.</summary>
    static async Task Monitor()
    {
        while (true)
        {
            await Task.Delay(5000);
            bool down;
            lock (Gate) down = _cf is not { HasExited: false } && _busyState.Length == 0 && DateTime.UtcNow - _cfStarted > TimeSpan.FromSeconds(30);
            if (down && _public.Length > 0) StartTunnel();
        }
    }

    /// <summary>Stops the games, the agents and the tunnel; remote access stays off until the server starts again.</summary>
    static async Task<(JsonArray Games, int Agents)> StopAll()
    {
        var names = new JsonArray();
        var all = await Program.Targets();
        foreach (var g in Registry.Games) if (StopGame(g, all)) names.Add(g.Name);
        Program.StopLaunched();
        int agents = 0;
        foreach (var a in Registry.Agents()) if ((bool?)a?["disconnected"] != true && DisconnectAgent((int)a!["id"]!)) agents++;
        RevokeClient(null);
        _public = "";
        KillTunnel();
        return (names, agents);
    }

    static void Notify(string text)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var psi = new ProcessStartInfo("osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("display notification \"" + text.Replace("\\", "").Replace("\"", "") + "\" with title \"UDriver\"");
            Process.Start(psi);
        }
        catch { }
    }

    static void OpenBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer" : "xdg-open") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add(url);
            Process.Start(psi);
        }
        catch { }
    }

    // ---------------- settings file (~/.udriver/remote.env, lines KEY="value") ----------------

    static string? GetSetting(string key)
    {
        try
        {
            foreach (var l in File.ReadAllLines(EnvFile))
                if (l.StartsWith(key + "="))
                {
                    // Same rule as the launcher's reader: the value is literal text, one pair of surrounding quotes is dropped.
                    string v = l.Substring(key.Length + 1);
                    return v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;
                }
        }
        catch { }
        return null;
    }

    static void SetSetting(string key, string? value)
    {
        if (value != null && value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("a setting cannot contain a line break");
        var lines = File.Exists(EnvFile) ? File.ReadAllLines(EnvFile).ToList() : new List<string>();
        lines.RemoveAll(l => l.StartsWith(key + "="));
        if (value != null) lines.Add($"{key}=\"{value}\"");
        Program.WritePrivate(EnvFile, string.Join("\n", lines) + "\n");
    }

    // ---------------- who is asking ----------------

    static bool IsLocal(HttpContext ctx)
    {
        string host = ctx.Request.Host.Value ?? "";
        if (host != "127.0.0.1:" + _port && host != "localhost:" + _port) return false;
        var h = ctx.Request.Headers;
        if (h.ContainsKey("Cf-Ray") || h.ContainsKey("Cf-Connecting-Ip") || h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Cdn-Loop")) return false;
        return ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    }

    static (bool Ok, string Csrf) Who(HttpContext ctx, bool local)
    {
        string sid = ctx.Request.Cookies["udp"] ?? "";
        lock (Gate)
        {
            if (sid.Length > 0 && Sessions.TryGetValue(Hash(sid), out var s) && s.Exp > DateTime.UtcNow) return (true, s.Csrf);
        }
        return (false, "");
    }

    static bool OwnOrigin(HttpContext ctx, bool local)
    {
        string origin = ctx.Request.Headers["Origin"].ToString();
        return origin.Length == 0 || origin == (local ? "http://" + ctx.Request.Host.Value : _public);
    }

    /// <summary>No framing; only our own nonce'd script and style blocks apply.</summary>
    static Task Html(HttpContext ctx, string html, int status = 200)
    {
        string nonce = Rand(18);
        html = html.Replace("<script>", $"<script nonce=\"{nonce}\">").Replace("<style>", $"<style nonce=\"{nonce}\">");
        return Write(ctx, status, "text/html; charset=utf-8", html, r =>
        {
            r.Headers["X-Frame-Options"] = "DENY";
            r.Headers["Content-Security-Policy"] = $"default-src 'self'; style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
            r.Headers["Referrer-Policy"] = "same-origin";   // not no-referrer: then browsers send "Origin: null" on form posts and the origin checks reject them
        });
    }

    // ---------------- routes ----------------

    static async Task Panel(HttpContext ctx, string path, string verb, bool local)
    {
        if (path == "/") { ctx.Response.Redirect("/panel"); return; }
        if (!local && _public.Length == 0) { await Write(ctx, 404, "text/plain", "not found"); return; }
        // The launcher trades the panel key, sent in the body, for a ticket; the ticket in the link becomes a session cookie.
        if (path == "/panel/ticket" && verb == "POST" && local)
        {
            string key;
            using (var rd = new StreamReader(ctx.Request.Body, Encoding.UTF8)) key = (await rd.ReadToEndAsync()).Trim();
            if (Same(key, PanelKey)) await Write(ctx, 200, "text/plain", NewTicket());
            else await Write(ctx, 403, "text/plain", "wrong key");
            return;
        }
        if (path == "/panel" && verb == "GET" && local && ctx.Request.Query.ContainsKey("ticket"))
        {
            if (TakeTicket(ctx.Request.Query["ticket"].ToString())) SignIn(ctx, true);
            ctx.Response.Redirect("/panel");
            return;
        }
        var who = Who(ctx, local);

        if (path == "/panel" && verb == "GET")
        {
            var q = ctx.Request.Query;
            if (!who.Ok) await Html(ctx, LoginPage(null));
            else if (q.ContainsKey("agent")) await Html(ctx, AgentPage(local, who.Csrf));
            else if (!local) await Html(ctx, PanelPage(false, who.Csrf));
            else if (q.ContainsKey("setup")) await Html(ctx, SetupPage(who.Csrf));
            else if (q.ContainsKey("remote")) await Html(ctx, RemotePage(who.Csrf));
            else if (q.ContainsKey("start") || Registry.Games.Count == 0 && !File.Exists(TunnelYml)) await Html(ctx, MainPage());
            else await Html(ctx, PanelPage(true, who.Csrf));
            return;
        }
        if (path == "/panel/login" && verb == "POST")
        {
            var f = await Form(ctx);
            string? err = OwnOrigin(ctx, local) ? await CheckPassword(ctx, f.GetValueOrDefault("password", "")) : "Request rejected.";
            if (err != null) { await Html(ctx, LoginPage(err), StatusOf(err)); return; }
            SignIn(ctx, local);
            ctx.Response.Redirect("/panel");
            return;
        }
        if (!who.Ok) { await Write(ctx, 401, "text/plain; charset=utf-8", "Sign-in required: open /panel"); return; }

        if (path == "/panel/api/games" && verb == "GET") { await Json(ctx, 200, await GamesJson()); return; }
        if (path == "/panel/api/agents" && verb == "GET") { await Json(ctx, 200, new JsonObject { ["agents"] = Registry.Agents() }); return; }
        if (path == "/panel/api/remote" && verb == "GET") { await Json(ctx, 200, RemoteJson()); return; }
        if (verb != "POST") { await Write(ctx, 404, "text/plain", "not found"); return; }

        var form = await Form(ctx);
        if (!OwnOrigin(ctx, local) || !Same(form.GetValueOrDefault("csrf", ""), who.Csrf)) { await Write(ctx, 403, "text/plain; charset=utf-8", "Request rejected (reload the panel page)."); return; }
        if (path.StartsWith("/panel/api/remote/") && await RemoteAction(ctx, path, form, local)) return;

        switch (path)
        {
            case "/panel/stop":
                await StopAll();
                await Html(ctx, StoppedPage());
                _ = Task.Run(async () => { await Task.Delay(600); Notify("UDriver stopped"); Environment.Exit(0); });
                return;
            case "/panel/logout":
                lock (Gate) Sessions.Remove(Hash(ctx.Request.Cookies["udp"] ?? ""));
                ctx.Response.Cookies.Delete("udp");
                ctx.Response.Redirect("/panel"); return;
            case "/panel/api/choose" when local:
                await Json(ctx, 200, new JsonObject { ["ok"] = true, ["path"] = await ChooseFolder(form.GetValueOrDefault("kind") == "build") });
                return;
            case "/panel/api/games/check" when local:
                string? bad = Registry.Classify(DroppedPath(form.GetValueOrDefault("path", "")), out string kind, out string real, out string found);
                await Json(ctx, bad == null ? 200 : 400, bad == null ? new JsonObject { ["ok"] = true, ["kind"] = kind, ["name"] = found, ["path"] = real } : Fail(bad));
                return;
            case "/panel/api/games/add" when local:
                string? addErr = Registry.Add(DroppedPath(form.GetValueOrDefault("path", "")), form.GetValueOrDefault("access"), out var added);
                if (addErr == null && form.GetValueOrDefault("name", "").Trim() is { Length: > 0 } name && name != added!.Name) addErr = Registry.Edit(added.Id, null, null, name);
                await Json(ctx, addErr == null ? 200 : 400, addErr == null ? new JsonObject { ["ok"] = true, ["id"] = added!.Id, ["warning"] = added.Kind == "project" ? InstallPackage(added.Path) : null } : Fail(addErr));
                return;
            case "/panel/api/games/edit":
                string id = form.GetValueOrDefault("id", "");
                string? newPath = form.TryGetValue("path", out var p) ? DroppedPath(p) : null, access = form.GetValueOrDefault("access");
                if (!local && (newPath != null || form.ContainsKey("name") || access != null && Raises(access, Registry.Games.FirstOrDefault(g => g.Id == id)?.Access)))
                { await Json(ctx, 400, Fail(LocalOnly)); return; }
                string? editErr = Registry.Edit(id, newPath, access, form.GetValueOrDefault("name"));
                string? warning = editErr == null && newPath != null && Registry.Find(id) is { Kind: "project" } edited ? InstallPackage(edited.Path) : null;
                await Json(ctx, editErr == null ? 200 : 400, editErr == null ? new JsonObject { ["ok"] = true, ["warning"] = warning } : Fail(editErr));
                return;
            case "/panel/api/games/remove" when local:
                bool removed = Registry.Remove(form.GetValueOrDefault("id", ""));
                await Json(ctx, removed ? 200 : 400, removed ? new JsonObject { ["ok"] = true } : Fail("This game is no longer in the list."));
                return;
            case "/panel/api/games/start" when local:
                if (Registry.Games.FirstOrDefault(g => g.Id == form.GetValueOrDefault("id")) is not { } sg) { await Json(ctx, 400, Fail("This game is no longer in the list.")); return; }
                var started = await Program.Launch(new JsonObject { ["path"] = sg.Id, ["waitSec"] = 60 }, true);
                bool startOk = !(started["isError"]?.GetValue<bool>() ?? false);
                await Json(ctx, startOk ? 200 : 400, new JsonObject { ["ok"] = startOk, ["text"] = (string?)started["content"]?[0]?["text"] ?? "", ["error"] = startOk ? null : (string?)started["content"]?[0]?["text"] });
                return;
            case "/panel/api/games/stop":
                var stopGame = Registry.Games.FirstOrDefault(g => g.Id == form.GetValueOrDefault("id"));
                bool stopped = stopGame != null && StopGame(stopGame, await Program.Targets());
                await Json(ctx, stopped ? 200 : 400, stopped ? new JsonObject { ["ok"] = true } : Fail("This game is not running or was not started through UDriver."));
                return;
            case "/panel/api/default-access":
                if (!local && Raises(form.GetValueOrDefault("access", ""), Registry.DefaultAccess)) { await Json(ctx, 400, Fail(LocalOnly)); return; }
                string? defErr = Registry.SetDefault(form.GetValueOrDefault("access", ""));
                await Json(ctx, defErr == null ? 200 : 400, defErr == null ? new JsonObject { ["ok"] = true } : Fail(defErr));
                return;
            case "/panel/api/agents/disconnect":
                bool gone = int.TryParse(form.GetValueOrDefault("id"), out int slot) && DisconnectAgent(slot);
                await Json(ctx, gone ? 200 : 400, gone ? new JsonObject { ["ok"] = true } : Fail("This agent is no longer connected."));
                return;
            case "/panel/api/stop-all":
                var all = await StopAll();
                await Json(ctx, 200, new JsonObject { ["ok"] = true, ["stoppedGames"] = all.Games, ["disconnectedAgents"] = all.Agents });
                return;
            case "/panel/api/password" when local:
                string pw = form.GetValueOrDefault("password", "");
                if (pw.Length == 0) pw = NewPassword();
                else if (pw.Length < 12) { await Json(ctx, 400, Fail("The password must be at least 12 characters long.")); return; }
                SetPassword(pw);
                await Json(ctx, 200, new JsonObject { ["ok"] = true, ["password"] = pw });
                return;
            default:
                await Write(ctx, 404, "text/plain", "not found"); return;
        }
    }

    static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    /// <summary>True when the new level is higher than the current one or not a level at all.</summary>
    static bool Raises(string access, string? current) => !Registry.Levels.Contains(access) || Registry.Level(access) > Registry.Level(current ?? "watch");

    /// <summary>Every running copy of the game started by a UDriver server: the last one in targets.json and each whose driver
    /// answers with the process id that its server wrote to the port's .name file.</summary>
    static List<Process> Copies(Registry.Game g, List<Program.Target> all)
    {
        var list = new List<Process>();
        if (Registry.Running(g) is { } last) list.Add(last);
        foreach (var t in all)
            if (t.Game?.Id == g.Id && t.Kind == "game" && t.Up && Program.LaunchName(t.Port, t.Pid) != null && list.All(x => x.Id != t.Pid))
                try { list.Add(Process.GetProcessById(t.Pid)); } catch { }
        return list;
    }

    static bool StopGame(Registry.Game g, List<Program.Target> all)
    {
        bool any = false;
        foreach (var p in Copies(g, all))
            using (p)
                try { p.Kill(true); p.WaitForExit(5000); any = true; } catch { }
        return any;
    }

    /// <summary>Signs a remote client out (null: every client) and refuses its open session.</summary>
    static void RevokeClient(string? one)
    {
        List<Program.Session> sessions;
        lock (Gate)
        {
            foreach (var k in _store.AccessClient.Where(x => one == null || x.Value == one).Select(x => x.Key).ToList()) { _store.Access.Remove(k); _store.AccessClient.Remove(k); }
            foreach (var k in _store.RefreshClient.Where(x => one == null || x.Value == one).Select(x => x.Key).ToList()) { _store.Refresh.Remove(k); _store.RefreshClient.Remove(k); }
            foreach (var k in Rotated.Where(x => one == null || x.Value.Client == one).Select(x => x.Key).ToList()) Rotated.Remove(k);
            Save();
            sessions = Agents.Values.Where(s => one == null || s.Client == one).ToList();
            foreach (var s in sessions) Agents.Remove(s.Client);
        }
        foreach (var s in sessions) { s.Off = true; Registry.Release(s); }
    }

    static bool DisconnectAgent(int slot)
    {
        if (Registry.Own(slot) is not { } s) return Registry.Disconnect(slot);
        if (s.Client.Length > 0) { RevokeClient(s.Client); return true; }
        lock (Gate) Agents.Remove(s.Client);
        s.Off = true;
        Registry.Release(s);
        return true;
    }

    static async Task<JsonObject> GamesJson()
    {
        var all = await Program.Targets();
        var games = new JsonArray();
        foreach (var g in Registry.Games)
        {
            var live = all.Where(t => t.Game?.Id == g.Id).ToList();
            var copies = Copies(g, all);
            copies.ForEach(c => c.Dispose());
            games.Add(new JsonObject
            {
                ["id"] = g.Id, ["name"] = g.Name, ["kind"] = g.Kind, ["path"] = g.Path, ["access"] = g.Access,
                ["state"] = live.Any(t => t.Kind == "editor" && t.Up) ? "editor open" : live.Any(t => t.Kind == "game" && t.Live) || copies.Count > 0 ? "running" : "not running",
                ["startedByUDriver"] = copies.Count > 0, ["exists"] = File.Exists(g.Path) || Directory.Exists(g.Path),
            });
        }
        return new JsonObject { ["defaultAccess"] = Registry.DefaultAccess, ["games"] = games };
    }

    // ---------------- games on this computer ----------------

    static async Task<string> ChooseFolder(bool build)
    {
        const string projectPrompt = "Choose your Unity project folder (the one containing Assets and ProjectSettings)";
        var wait = TimeSpan.FromMinutes(5);
        try
        {
            var r = OperatingSystem.IsMacOS()
                ? await RunTool("osascript", wait, "-e", build
                    ? "POSIX path of (choose file with prompt \"Choose the game build (the .app file)\" of type {\"com.apple.application-bundle\"})"
                    : "POSIX path of (choose folder with prompt \"" + projectPrompt + "\")")
                : OperatingSystem.IsWindows()
                ? await RunTool("powershell", wait, "-NoProfile", "-STA", "-Command", "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Add-Type -AssemblyName System.Windows.Forms; " + (build
                    ? "$d = New-Object System.Windows.Forms.OpenFileDialog; $d.Title = 'Choose the game (the .exe file)'; $d.Filter = 'Game (*.exe)|*.exe'; $p = 'FileName'"
                    : "$d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = '" + projectPrompt + "'; $p = 'SelectedPath'")
                    + "; $f = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }; if ($d.ShowDialog($f) -eq 'OK') { $d.$p }")
                : build
                ? await RunTool("zenity", wait, "--file-selection", "--title=Choose the game file")
                : await RunTool("zenity", wait, "--file-selection", "--directory", "--title=" + projectPrompt);
            return r.Code == 0 ? r.Out.Trim().TrimEnd('/', '\\') : "";
        }
        catch { return ""; }   // no dialog tool on this system: the page falls back to typing the path
    }

    static async Task<(int Code, string Out)> RunTool(string exe, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Exception("could not start " + exe);
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new Exception(exe + ": timed out"); }
        return (p.ExitCode, (await so) + (await se));
    }

    static string NewPassword()
    {
        const string abc = "abcdefghijkmnpqrstuvwxyz23456789";
        var sb = new StringBuilder();
        for (int i = 0; i < 24; i++) { if (i > 0 && i % 6 == 0) sb.Append('-'); sb.Append(abc[System.Security.Cryptography.RandomNumberGenerator.GetInt32(abc.Length)]); }
        return sb.ToString();
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>A path as typed, pasted or dropped into a terminal: quotes and backslash escapes removed, ~ expanded.</summary>
    static string DroppedPath(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'')) s = s[1..^1];
        else if (!OperatingSystem.IsWindows()) s = Regex.Replace(s, @"\\(.)", "$1");
        if (s == "~" || s.StartsWith("~/")) s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..];
        return s;
    }

    /// <summary>Copies com.udriver.agent into the project's Packages folder; returns a warning when that was not possible.</summary>
    static string? InstallPackage(string project)
    {
        try { if (File.ReadAllText(Path.Combine(project, "Packages", "manifest.json")).Contains("\"com.udriver.agent\"")) return null; } catch { }
        string pkg = new[] { Path.Combine(Env("UDRIVER_DIST"), "com.udriver.agent"), Path.Combine(Program.HomeDir, "package", "com.udriver.agent") }
            .FirstOrDefault(d => Path.IsPathFullyQualified(d) && Directory.Exists(d)) ?? "";
        if (pkg.Length == 0) return $"The UDriver package (com.udriver.agent) was not found, so it was not added to the project. Start UDriver with {Launcher} from its folder, then add the project again.";
        try { CopyDir(pkg, Path.Combine(project, "Packages", "com.udriver.agent")); return null; }
        catch (Exception e) { return "Could not add the UDriver package to the project: " + e.Message; }
    }

    /// <summary>How an agent starts this server: the dotnet host and the dll, or the app itself.</summary>
    static string ServerCommand()
    {
        static string Q(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
        string exe = Environment.ProcessPath ?? "dotnet";
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? Q(exe) + " " + Q(typeof(HttpMode).Assembly.Location) : Q(exe);
    }

    /// <summary>`udriver-mcp setup`: the setup questions of the browser page, answered in a terminal.</summary>
    internal static void TerminalSetup()
    {
        bool color = !Console.IsOutputRedirected && Env("NO_COLOR").Length == 0;
        string C(string code, string s) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;
        void Say(string s = "") => Console.WriteLine(s.Length == 0 ? "" : "     " + s);
        string? Ask(string prompt) { Console.Write("     " + prompt); return Console.ReadLine()?.Trim(); }

        Console.WriteLine();
        Console.WriteLine(C("1", "UDriver setup") + " " + C("90", "— three questions, change any of them later"));
        Console.WriteLine();
        Console.WriteLine(C("94", "1/3") + "  " + C("1", "What should the agent control?"));
        Say(C("90", "Drag a Unity project folder or a game build here, then press Enter."));
        int had = Registry.Games.Count;
        if (had > 0) Say(C("90", $"Press Enter alone to keep the {had} game{(had == 1 ? "" : "s")} already added."));
        while (true)
        {
            string? line = Ask("> ");
            if (line == null || line.Length == 0 && Registry.Games.Count > 0) break;
            if (line.Length == 0) continue;
            if (Registry.Add(DroppedPath(line), null, out var g) is string err) { Say(C("91", err)); continue; }
            string? warn = g!.Kind == "project" ? InstallPackage(g.Path) : null;
            Say(C("92", g.Kind == "build" ? "Game build found." : warn == null ? "Unity project found. UDriver package added." : "Unity project found."));
            if (warn != null) Say(C("91", warn));
            string? more = Ask(C("90", "Add another? [y/N] "));
            if (more == null || !more.StartsWith("y", StringComparison.OrdinalIgnoreCase)) break;
            Say(C("90", "Drag the next one here, then press Enter."));
        }
        if (Registry.Games.Count == 0) { Console.WriteLine(C("91", "No game was added, so nothing was changed. Run setup again when you know where the game is.")); Environment.Exit(1); }

        Console.WriteLine();
        Console.WriteLine(C("94", "2/3") + "  " + C("1", "What may the agent do?"));
        Say("1 Watch    2 Play    3 Full control");
        string def = (Registry.Level(Registry.DefaultAccess) + 1).ToString(), pick;
        while ((pick = Ask($"> {C("90", $"[{def}] ")}") ?? "") is not ("" or "1" or "2" or "3")) Say(C("91", "Type 1, 2 or 3."));
        string access = Registry.Levels[(pick.Length == 0 ? def : pick)[0] - '1'];
        Registry.SetDefault(access);
        foreach (var g in Registry.Games) Registry.Edit(g.Id, null, access, null);

        Console.WriteLine();
        Console.WriteLine(C("94", "3/3") + "  " + C("1", "Where is your agent?"));
        Say("1 On this computer    2 Somewhere else");
        while ((pick = Ask($"> {C("90", "[1] ")}") ?? "") is not ("" or "1" or "2")) Say(C("91", "Type 1 or 2."));

        Console.WriteLine();
        if (pick == "2")
            Console.WriteLine(C("92", "Done.") + $" To reach UDriver from somewhere else, start it with {Launcher} and turn on Remote access in the panel that opens.");
        else
            Console.WriteLine(C("92", "Done.") + " Add UDriver to your agent as an MCP server that runs this command, then ask the agent to open your game:\n" + ServerCommand());
        Console.WriteLine(C("90", "Change anything later:") + " " + ServerCommand() + " setup");
    }

    // ---------------- pages ----------------

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    // Html() adds the nonce to <style>, so no style="" attributes.
    const string Css = """
        :root{color-scheme:light dark;
          --bg:#F2F2F7;--card:#FFFFFF;--well:#F2F2F7;--text:#1C1C1E;--text-2:#5E5E63;--sep:#E5E5EA;--edge:#C7C7CC;--gray-btn:#E9E9EE;--seg:#E3E3E8;--seg-on:#FFFFFF;
          --blue:#0066CC;--blue-hover:#004C99;--green:#248A3D;--green-text:#1D7A34;--red:#C4281C;--orange:#B25000;--icon:#1C1C1E;--scrim:rgba(0,0,0,.3);
          --font:-apple-system,BlinkMacSystemFont,"SF Pro Text","Helvetica Neue",Arial,sans-serif;--mono:ui-monospace,"SF Mono",Menlo,Consolas,monospace}
        @media (prefers-color-scheme:dark){:root{
          --bg:#000000;--card:#1C1C1E;--well:#2C2C2E;--text:#F2F2F7;--text-2:#AEAEB2;--sep:#38383A;--edge:#545458;--gray-btn:#2C2C2E;--seg:#2C2C2E;--seg-on:#636366;
          --blue:#4DA3FF;--blue-hover:#80BDFF;--green:#30D158;--green-text:#30D158;--red:#FF6961;--orange:#FFB340;--icon:#3A3A3C;--scrim:rgba(0,0,0,.6)}}
        *,*::before,*::after{box-sizing:border-box} [hidden]{display:none!important}
        body{margin:0;background:var(--bg);color:var(--text);font:17px/1.35 var(--font);-webkit-font-smoothing:antialiased}
        a{color:var(--blue)} a:hover{color:var(--blue-hover)}
        :focus-visible{outline:3px solid var(--blue);outline-offset:2px}
        main{max-width:680px;margin:0 auto;padding:48px 24px 72px;display:flex;flex-direction:column;gap:32px}
        main.narrow{max-width:480px}
        .mono,code{font-family:var(--mono);font-size:15px;overflow-wrap:anywhere}
        .sr{position:absolute!important;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
        .icon rect{fill:var(--icon)}
        .top{display:flex;flex-wrap:wrap;align-items:center;gap:12px 14px}
        .top .icon{width:40px;height:40px}
        .top h1{flex:1;margin:0;font-size:28px;font-weight:700;letter-spacing:-.02em}
        .hero{display:flex;flex-direction:column;align-items:center;gap:14px;text-align:center;padding-top:24px}
        .hero .icon{width:64px;height:64px}
        .title{margin:0;font-size:34px;line-height:1.15;font-weight:700;letter-spacing:-.02em}
        .subtitle{margin:0;max-width:470px;font-size:17px;line-height:1.45;color:var(--text-2)}
        .head{display:flex;flex-direction:column;gap:8px}
        .back{align-self:flex-start;display:inline-flex;align-items:center;min-height:44px;text-decoration:none}
        section{display:flex;flex-direction:column;gap:8px}
        .sec-h{margin:0;padding:0 16px;font-size:13px;font-weight:400;letter-spacing:.02em;text-transform:uppercase;color:var(--text-2)}
        .step-h{margin:0 0 2px;font-size:20px;font-weight:600;letter-spacing:-.01em}
        .group{background:var(--card);border-radius:12px;overflow:hidden}
        .group>*+*{border-top:1px solid var(--sep)}
        .group.pad{padding:16px}
        .row{display:flex;flex-wrap:wrap;align-items:center;gap:8px 14px;padding:12px 16px}
        .row.fixed{flex-wrap:nowrap}
        .k{flex:1 1 250px;min-width:0;display:flex;flex-direction:column;gap:2px}
        .name{font-weight:600}
        .sub{font-size:13px;color:var(--text-2)} .sub .mono{font-size:13px}
        .st{display:inline-flex;align-items:center;gap:7px;font-size:13px;color:var(--text-2)}
        .top .st{font-size:15px}
        .dot{width:8px;height:8px;border-radius:50%;flex:none;border:1.5px solid var(--text-2)}
        .st.on{color:var(--green-text)} .st.on .dot{background:var(--green);border-color:var(--green)}
        .st.bad{color:var(--red)} .st.bad .dot{background:var(--red);border-color:var(--red)}
        .st.wait{color:var(--orange)} .st.wait .dot{background:var(--orange);border-color:var(--orange)}
        .select{min-height:44px;padding:0 10px;border:0;border-radius:10px;background:var(--well);color:var(--text);font:inherit;font-size:15px;cursor:pointer}
        .link{display:inline-flex;align-items:center;min-height:44px;padding:0 4px;border:0;background:none;color:var(--blue);font:inherit;font-size:15px;text-decoration:none;cursor:pointer}
        .link.danger{color:var(--red)} .link:disabled{opacity:.45;cursor:default}
        .pill{display:inline-flex;align-items:center;justify-content:center;min-height:36px;min-width:84px;padding:0 16px;border:0;border-radius:18px;font:inherit;font-size:15px;font-weight:600;text-decoration:none;cursor:pointer;background:var(--gray-btn);color:var(--blue)}
        .pill.blue{background:var(--blue);color:#fff} .pill.plain{color:var(--text)} .pill:disabled{opacity:.45;cursor:default}
        a.pill.blue:hover{color:#fff}
        .add{display:flex;align-items:center;gap:10px;width:100%;min-height:48px;padding:0 16px;border:0;background:none;color:var(--blue);font:inherit;text-align:left;cursor:pointer}
        .add svg{width:20px;height:20px;flex:none}
        .empty{padding:14px 16px;color:var(--text-2);font-size:15px}
        .foot{margin:0;padding:0 16px;font-size:13px;line-height:1.45;color:var(--text-2)}
        .foot.err{color:var(--red)}
        .foot.center{text-align:center}
        .footer{display:flex;flex-wrap:wrap;justify-content:space-between;gap:8px 24px;padding:0 16px}
        .footer form{margin:0}
        .switch{flex:none;width:51px;height:31px;padding:2px;border:0;border-radius:16px;background:var(--seg);cursor:pointer;transition:background .2s}
        .switch span{display:block;width:27px;height:27px;border-radius:50%;background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.25);transition:transform .2s}
        .switch[aria-checked=true]{background:var(--green)} .switch[aria-checked=true] span{transform:translateX(20px)}
        .switch:disabled{opacity:.5;cursor:default}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:16px}
        .card{background:var(--card);border-radius:16px;padding:24px;display:flex;flex-direction:column;gap:10px}
        .card svg{width:28px;height:28px;stroke:var(--blue)}
        .card h2{margin:0;font-size:20px;font-weight:600;letter-spacing:-.01em}
        .card p{margin:0;font-size:15px;line-height:1.45;color:var(--text-2);flex:1}
        .cmd{margin-top:10px;display:flex;align-items:center;gap:8px;min-height:44px;padding:6px 6px 6px 14px;border-radius:12px;background:var(--well)}
        .cmd code{flex:1;min-width:0}
        .cmd button{min-height:32px;padding:0 12px;border:0;border-radius:8px;background:var(--card);color:var(--blue);font:inherit;font-size:15px;font-weight:600;cursor:pointer}
        .btn{display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:0 20px;border:0;border-radius:12px;font:inherit;font-weight:600;text-decoration:none;cursor:pointer;background:var(--gray-btn);color:var(--blue)}
        .btn.primary{min-height:50px;padding:0 28px;background:var(--blue);color:#fff}
        a.btn.primary:hover{color:#fff}
        .btn:disabled{opacity:.45;cursor:default}
        .card .btn{margin-top:10px}
        .actions{display:flex;flex-wrap:wrap;align-items:center;gap:12px 20px}
        .actions .note{font-size:13px;color:var(--text-2)}
        .narrow .actions .btn{width:100%}
        .field{display:block;width:100%;min-height:44px;padding:0 12px;border:0;border-radius:10px;background:var(--well);color:var(--text);font:inherit}
        .input{min-height:44px;min-width:0;padding:0 12px;border:1px solid var(--edge);border-radius:10px;background:var(--card);color:var(--text);font:inherit}
        .field.mono,.input.mono{font-family:var(--mono);font-size:15px}
        .choice{display:flex;align-items:flex-start;gap:12px;padding:14px 16px;cursor:pointer}
        .choice input{width:20px;height:20px;margin:2px 0 0;flex:none;accent-color:var(--blue)}
        .choice span{display:flex;flex-direction:column;gap:2px}
        .choice b{font-weight:600}
        .choice small{font-size:15px;line-height:1.4;color:var(--text-2)}
        .hint{display:flex;align-items:center;gap:7px;margin:0;font-size:13px;line-height:1.45;color:var(--text-2)}
        .hint.ok{color:var(--green-text)} .hint.ok::before{content:"\2713";font-weight:700}
        .hint.bad{color:var(--red)}
        dialog.sheet{width:min(480px,calc(100% - 32px));padding:28px;border:0;border-radius:18px;background:var(--card);color:var(--text);box-shadow:0 24px 64px rgba(0,0,0,.2)}
        dialog.sheet::backdrop{background:var(--scrim)}
        .sheet form{display:flex;flex-direction:column;gap:22px;margin:0}
        .sheet h2{margin:0;font-size:22px;font-weight:700;letter-spacing:-.01em}
        .seg{display:grid;grid-template-columns:repeat(2,1fr);gap:2px;padding:2px;border-radius:11px;background:var(--seg)}
        .seg button{min-height:44px;border:0;border-radius:9px;background:none;color:var(--text);font:inherit;font-size:15px;cursor:pointer}
        .seg button[aria-pressed=true]{background:var(--seg-on);box-shadow:0 1px 3px rgba(0,0,0,.12);font-weight:600}
        .fld{display:flex;flex-direction:column;gap:6px}
        .fld label{font-size:15px;font-weight:600}
        .pick{display:flex;flex-wrap:wrap;gap:8px}
        .pick .input{flex:1 1 220px}
        .sheet-btns{display:flex;flex-wrap:wrap;align-items:center;gap:10px}
        .sheet-btns .grow{flex:1}
        .namebox{display:flex;flex-wrap:wrap;align-items:center;gap:8px 12px;padding:12px 16px}
        .namebox label{flex:0 0 96px}
        .namebox .input{flex:1 1 140px}
        .namebox .sfx{color:var(--text-2)}
        .badge{width:24px;height:24px;flex:none}
        .badge.no circle{fill:none;stroke:var(--edge);stroke-width:2}
        .code{font-family:var(--mono);font-size:28px;font-weight:600;letter-spacing:.04em}
        ol.steps{margin:0;padding:14px 16px 14px 40px;display:flex;flex-direction:column;gap:10px;background:var(--card);border-radius:12px;line-height:1.4}
        .spin{width:18px;height:18px;flex:none;border-radius:50%;border:2.5px solid var(--sep);border-top-color:var(--text-2);animation:spin .8s linear infinite}
        @keyframes spin{to{transform:rotate(360deg)}}
        .toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);width:max-content;max-width:calc(100% - 32px);margin:0;padding:12px 18px;border-radius:12px;background:var(--text);color:var(--bg);font-size:15px;line-height:1.4;box-shadow:0 8px 24px rgba(0,0,0,.2);overflow-wrap:anywhere}
        .toast.bad{background:var(--red);color:#fff}
        @media (max-width:480px){main{padding:32px 16px 56px}.title{font-size:30px}}
        @media (prefers-reduced-motion:reduce){*,*::before,*::after{transition:none!important}.spin{animation-duration:2.4s}}
        """;

    const string Icon = """<svg class="icon" viewBox="0 0 64 64" aria-hidden="true"><rect width="64" height="64" rx="15" fill="#1C1C1E"/><path d="M24 18v28l8-7 5 11 5-2.4-5-10.6h10z" fill="#FFFFFF"/></svg>""";
    const string Plus = """<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><path d="M10 4v12M4 10h12"/></svg>""";
    const string Back = """<a class="back" href="/panel"><svg width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12.5 4.5 7 10l5.5 5.5"/></svg>UDriver</a>""";
    const string AccessText = "Watch: look only. Play: also click, type, move and press keys. Full control: also change game data.";

    internal static string Shell(string body, string title = "UDriver") =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">" +
        $"<meta name=\"color-scheme\" content=\"light dark\"><title>{H(title)}</title><style>{Css}</style></head><body>" +
        body + "</body></html>";

    /// <summary>A panel page: its data for the script sits on main, the shared script below it.</summary>
    static string Screen(string page, bool local, string csrf, string title, string body, string cls = "") => Shell(
        $"<main class=\"{cls}\" data-page=\"{page}\" data-csrf=\"{H(csrf)}\" data-local=\"{(local ? "1" : "")}\" data-cmd=\"{H(ServerCommand())}\">{body}</main>" +
        $"<p id=\"say\" class=\"toast\" role=\"status\" hidden></p><script>{Js}</script>", title);

    static string GameDialog() => $"""
        <dialog id="gd" class="sheet" aria-labelledby="gd-t"><form id="gf">
        <h2 id="gd-t">Add a game</h2>
        <div class="seg" role="group" aria-label="Kind"><button type="button" id="gk-project" aria-pressed="true">Unity project</button><button type="button" id="gk-build" aria-pressed="false">Game build</button></div>
        <div class="fld"><label for="gp">Where is it?</label>
        <div class="pick"><input id="gp" class="input mono" type="text" autocomplete="off" spellcheck="false" required><button type="button" class="btn" id="gchoose">Choose…</button></div>
        <p id="gst" class="hint" role="status"></p></div>
        <div class="fld"><label for="gn">Name</label><input id="gn" class="input" type="text" autocomplete="off"><p class="hint">The agent uses this name to tell your games apart.</p></div>
        <p class="hint">You can change the folder at any time. For a Unity project, UDriver adds its package to the project for you.</p>
        <div class="sheet-btns"><button type="button" class="link danger" id="grm" hidden>Remove from list</button><span class="grow"></span><button type="button" class="pill plain" id="gcancel">Cancel</button><button type="submit" class="pill blue" id="gok">Add game</button></div>
        </form></dialog>
        """;

    static string LoginPage(string? error) => Shell($"""
        <main class="narrow">
        <header class="hero">{Icon}<h1 class="title">Sign in to UDriver</h1><p class="subtitle">Enter the panel password to open the control panel.</p></header>
        <form method="post" action="/panel/login"><section>
        <div class="group pad"><label class="sr" for="password">Panel password</label><input class="field" type="password" id="password" name="password" placeholder="Panel password" autofocus autocomplete="current-password"></div>
        {(error != null ? $"<p class=\"foot err\" role=\"alert\">{H(error)}</p>" : "")}
        </section><div class="actions"><button class="btn primary" type="submit">Sign In</button></div></form>
        <p class="foot center">On the computer that runs UDriver, double-click {Launcher} to open the panel without a password.</p>
        </main>
        """, "Sign in · UDriver");

    static string StoppedPage() => Shell($"""
        <main class="narrow">
        <header class="hero">{Icon}<h1 class="title">UDriver stopped</h1><p class="subtitle">Games started through UDriver, the tunnel and the server are off. Agents can no longer reach your games.</p></header>
        <section><h2 class="sec-h">To start again</h2>
        <div class="group"><div class="row"><span class="k">Double-click {Launcher} in the udriver folder on the computer with Unity.</span></div></div>
        <p class="foot">The panel won't open while UDriver is off. That's expected.</p></section>
        </main>
        """, "UDriver stopped");

    static string MainPage() => Screen("main", true, "", "Set up UDriver", $"""
        <header class="hero">{Icon}<h1 class="title">Set up UDriver</h1>
        <p class="subtitle">Let an agent play and test your Unity game. Choose how you want to set it up. Both ways ask the same three questions.</p></header>
        <div class="cards">
        <section class="card"><svg viewBox="0 0 28 28" fill="none" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="5" width="22" height="18" rx="3.5"/><path d="M3 10.5h22"/></svg>
        <h2>In the browser</h2><p>Three short steps on this page. Nothing to type in a terminal.</p><a class="btn primary" href="/panel?setup=1">Continue here</a></section>
        <section class="card"><svg viewBox="0 0 28 28" fill="none" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="5" width="22" height="18" rx="3.5"/><path d="M8.5 11l4 3.5-4 3.5M15 18h5"/></svg>
        <h2>In the terminal</h2><p>The same three questions, answered in a terminal window.</p>
        <div class="cmd"><code id="setupcmd">{H(ServerCommand())} setup</code><button type="button" id="copysetup">Copy</button></div></section>
        </div>
        <p class="foot center">Everything runs on this computer. The agent sees only the games you add: never your files, terminal or other apps.</p>
        """);

    static string SetupPage(string csrf) => Screen("setup", true, csrf, "Set up UDriver", $"""
        <header class="head">{Back}<h1 class="title">Set up UDriver</h1><p class="subtitle">Three questions. You can change every answer later.</p></header>
        <section><h2 class="step-h">1. What should the agent control?</h2>
        <div class="group" id="games"><button type="button" class="add" id="addgame">{Plus}Add a project or a build…</button></div>
        <p class="foot">Add as many as you need. The agent can drive several at the same time, for example two game clients side by side.</p></section>
        <section><h2 class="step-h">2. What may the agent do?</h2>
        <div class="group">
        <label class="choice"><input type="radio" name="level" value="watch"><span><b>Watch</b><small>Look at the screen, the interface and the game data. Changes nothing.</small></span></label>
        <label class="choice"><input type="radio" name="level" value="play"><span><b>Play</b><small>Also click, type, move and press keys, like a player would.</small></span></label>
        <label class="choice"><input type="radio" name="level" value="full"><span><b>Full control</b><small>Also change game data and call game code. For debugging.</small></span></label>
        </div>
        <p class="foot">At every level the agent works only inside the games above. It never gets your files, terminal or other apps.</p></section>
        <section><h2 class="step-h">3. Where is your agent?</h2>
        <div class="group">
        <label class="choice"><input type="radio" name="where" value="here" checked><span><b>On this computer</b><small>Agents running on this machine. The panel gives you the one command to add.</small></span></label>
        <label class="choice"><input type="radio" name="where" value="remote"><span><b>Somewhere else</b><small>A web agent, a phone or another machine. You sign in to Cloudflare and pick your domain on the next screen.</small></span></label>
        </div></section>
        <div class="actions"><button type="button" class="btn primary" id="finish">Finish setup</button><span class="note">Takes about a minute.</span></div>
        {GameDialog()}
        """);

    static string PanelPage(bool local, string csrf) => Screen("panel", local, csrf, "UDriver", $"""
        <header class="top">{Icon}<h1>UDriver</h1><span class="st on" id="alive"><i class="dot"></i><span>On</span></span></header>
        <section><h2 class="sec-h">Games</h2>
        <div class="group" id="games">{(local ? $"""<button type="button" class="add" id="addgame">{Plus}Add a project or a build…</button>""" : """<div class="empty" id="addgame">Adding games, changing folders, launching games and raising access are available only on the computer that runs UDriver.</div>""")}</div>
        <p class="foot">Access is set for each game. {AccessText} No agent ever gets your files, terminal or other apps.</p></section>
        <section><h2 class="sec-h" id="agents-h">Connected agents</h2>
        <div class="group" id="agents"><div class="row" id="connect"><div class="k"><span>Connect another agent</span><span class="sub">Any agent that supports MCP. On this computer: add UDriver as an MCP server with the command you copy here.</span></div><button type="button" class="pill" id="copycmd">Copy command</button></div></div></section>
        <section><h2 class="sec-h">Remote access</h2>
        <div class="group">
        <div class="row fixed"><div class="k"><span id="rm-label">Use from another device or a web agent</span><span class="st" id="rm-st"><i class="dot"></i><span id="rm-text">Checking…</span></span></div>
        <button type="button" class="switch" role="switch" aria-checked="false" aria-labelledby="rm-label" id="rm-switch"><span></span></button></div>
        <div class="row" id="rm-addr" hidden><div class="k"><span class="sub">Address</span><span class="mono" id="rm-url"></span></div><button type="button" class="pill" id="rm-copy">Copy address</button></div>
        <div class="row" id="rm-code" hidden><div class="k"><span>Add a remote agent</span><span class="sub">Gives the address and a one-time code to copy.</span></div><a class="pill blue" href="/panel?agent=1">Get a code</a></div>
        {(local ? """<div class="row" id="rm-cf"><div class="k"><span>Cloudflare</span><span class="sub" id="rm-cf-text"></span></div><a class="link" href="/panel?remote=1" id="rm-change">Change address…</a><button type="button" class="link danger" id="rm-signout" hidden>Sign out</button></div>""" : "")}
        <div class="row" id="rm-tunnel" hidden><div class="k"><span>Tunnel</span><span class="sub">Restart it if a remote agent cannot connect.</span></div><button type="button" class="link" id="rm-restart">Restart</button></div>
        {(local ? """<div class="row"><div class="k"><span>Panel password</span><span class="sub" id="pw-text">Opens this panel from another device. Never given to agents.</span></div><button type="button" class="link" id="pw-change">Change…</button></div>""" : "")}
        </div>
        <p class="foot">Turning this off closes the tunnel at once. Remote agents lose access; agents on this computer keep working.</p></section>
        <footer class="footer">
        {(local ? """<a class="link" href="/panel?setup=1">Run setup again</a>""" : $"""<form method="post" action="/panel/logout"><input type="hidden" name="csrf" value="{H(csrf)}"><button class="link" type="submit">Sign out of the panel</button></form>""")}
        <form method="post" action="/panel/stop" id="stopform"><input type="hidden" name="csrf" value="{H(csrf)}"><button class="link danger" type="submit">Stop everything</button></form>
        </footer>
        {(local ? GameDialog() : "")}
        {(local ? """
        <dialog id="pd" class="sheet" aria-labelledby="pd-t"><form id="pf"><h2 id="pd-t">Panel password</h2>
        <div class="fld"><label for="pp">New password</label><input id="pp" class="input" type="password" autocomplete="new-password" minlength="12"><p class="hint">At least 12 characters. Leave it empty and UDriver makes one for you.</p></div>
        <div class="sheet-btns"><span class="grow"></span><button type="button" class="pill plain" id="pcancel">Cancel</button><button type="submit" class="pill blue">Save</button></div></form></dialog>
        """ : "")}
        """);

    static string RemotePage(string csrf) => Screen("remote", true, csrf, "Remote access · UDriver", $"""
        <header class="head">{Back}<h1 class="title">Remote access</h1><p class="subtitle">Use UDriver from another device or a web agent. Three steps, no terminal.</p></header>
        <section id="r-install" hidden><h2 class="step-h">First, get cloudflared</h2>
        <div class="group"><div class="row"><div class="k"><span class="name">cloudflared is not installed</span><span class="sub" id="r-inst-text">Cloudflare's free app that runs the tunnel on this computer.</span></div>
        <button type="button" class="pill blue" id="r-inst" hidden>Install cloudflared</button><a class="pill blue" id="r-dl" target="_blank" rel="noopener" hidden>Download from Cloudflare</a></div></div>
        <p class="foot" id="r-inst-foot">After installing it, this page notices it on its own.</p></section>
        <section><h2 class="step-h">1. Sign in to Cloudflare</h2>
        <div class="group"><div class="row">
        <svg class="badge" id="r-yes" viewBox="0 0 24 24" aria-hidden="true" hidden><circle cx="12" cy="12" r="11" fill="#248A3D"/><path d="m7 12.5 3.2 3.2L17 9" fill="none" stroke="#FFFFFF" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>
        <svg class="badge no" id="r-no" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10"/></svg>
        <div class="k"><span class="name" id="r-cf-t">Not signed in</span><span class="sub" id="r-cf-sub">Sign in and pick the domain to use.</span></div>
        <button type="button" class="link" id="r-login">Sign in to Cloudflare</button></div>
        <div class="row" id="r-wait" hidden><span class="spin" aria-hidden="true"></span><div class="k"><span>Finish the sign-in in the Cloudflare tab</span><span class="sub">No tab? <a id="r-wait-url" target="_blank" rel="noopener">Open the Cloudflare page</a>.</span></div><button type="button" class="link" id="r-cancel">Cancel</button></div>
        </div>
        <p class="foot">Cloudflare opens in a new tab, shows the domains in your account and asks which one to use. UDriver never sees your Cloudflare password.</p></section>
        <section><h2 class="step-h">2. Choose the address</h2>
        <div class="group"><div class="namebox"><label for="rn" id="rn-l">Name</label><input id="rn" class="input" type="text" value="udriver" autocomplete="off" spellcheck="false" autocapitalize="none"><span class="sfx" id="rs"></span></div>
        <div class="row"><div class="k"><span class="sub">Agents will connect to</span><span class="mono" id="r-url"></span></div></div></div>
        <p class="foot" id="r-host-foot"></p></section>
        <section><h2 class="step-h">3. Set a panel password</h2>
        <div class="group"><div class="namebox"><label for="rp">Password</label><input id="rp" class="input" type="password" autocomplete="new-password"><button type="button" class="link" id="r-show">Show</button></div></div>
        <p class="foot" id="r-pw-foot">Opens this panel from another device. Agents never use it: each new agent gets its own one-time code.</p></section>
        <div class="actions"><button type="button" class="btn primary" id="r-on">Turn on remote access</button><span class="note" id="r-note">Creates the tunnel. Takes about ten seconds.</span></div>
        <p class="foot err" id="r-err" role="alert" hidden></p>
        <p class="foot">The tunnel runs on this computer and nothing is opened on your router. You can restart it, move it to another domain or turn it off in the panel at any time.</p>
        """);

    static string AgentPage(bool local, string csrf) => Screen("agent", local, csrf, "Add a remote agent · UDriver", $"""
        <header class="head">{Back}<h1 class="title">Add a remote agent</h1><p class="subtitle">Copy two things into your agent. That is all.</p></header>
        <section><div class="group">
        <div class="row"><div class="k"><span class="sub">Address</span><span class="mono" id="a-url">…</span></div><button type="button" class="pill" id="a-copy-url">Copy</button></div>
        <div class="row"><div class="k"><span class="sub">One-time code</span><span class="code" id="a-code">…</span><span class="sub" id="a-exp">Works once. Expires in 10 minutes.</span></div><button type="button" class="link" id="a-new">New code</button><button type="button" class="pill blue" id="a-copy-code">Copy</button></div>
        </div></section>
        <section><h2 class="sec-h">What to do</h2>
        <ol class="steps"><li>In your agent, add an MCP server and paste the address.</li><li>A sign-in page opens. Paste the code and press Allow.</li><li>The agent appears under Connected agents. Disconnect it there at any time.</li></ol>
        <p class="foot">The code lets in one agent. Your panel password is never given to agents.</p></section>
        <div class="actions"><a class="btn primary" href="/panel">Done</a></div>
        """, "narrow");

    const string Js = """
        "use strict";
        const $=id=>document.getElementById(id), M=document.querySelector("main").dataset, csrf=M.csrf, local=M.local==="1";
        const el=(tag,cls,text)=>{ const e=document.createElement(tag); if(cls) e.className=cls; if(text!=null) e.textContent=text; return e; };
        const button=(cls,text,fn)=>{ const b=el("button",cls,text); b.type="button"; b.onclick=()=>fn(b); return b; };
        const show=(id,on)=>{ const e=$(id); if(e) e.hidden=!on; };
        const on=(id,ev,fn)=>{ const e=$(id); if(e) e[ev]=fn; };
        let busy=0, sayTimer;
        function say(text,bad){ const s=$("say"); clearTimeout(sayTimer); s.textContent=text; s.className="toast"+(bad?" bad":""); s.hidden=!text; if(text) sayTimer=setTimeout(()=>s.hidden=true,bad?9000:4000); }
        async function post(url,data={}){
          const r=await fetch(url,{method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},body:new URLSearchParams({csrf,...data})});
          if(r.status===401||r.status===403){ location.reload(); throw new Error("Please sign in again."); }
          const j=await r.json().catch(()=>({ok:false,error:"UDriver did not answer. Is it still running?"}));
          if(!j.ok) throw new Error(j.error||"Something went wrong.");
          return j;
        }
        const get=url=>fetch(url).then(r=>{ if(r.status===401) location.reload(); return r.ok?r.json():Promise.reject(new Error("UDriver is not answering.")); });
        async function act(b,fn,label){
          busy++; const t=b&&b.textContent; if(b){ b.disabled=true; if(label) b.textContent=label; }
          try{ await fn(); } catch(e){ say(e.message,true); }
          finally{ busy--; if(b){ b.disabled=false; if(label) b.textContent=t; } refresh(); }
        }
        async function copy(text,b){
          try{ await navigator.clipboard.writeText(text); const t=b.textContent; b.textContent="Copied"; setTimeout(()=>b.textContent=t,1500); }
          catch{ say("Copy it from here: "+text); }
        }
        const sub=(kind,path)=>{ const s=el("span","sub",(kind==="project"?"Unity project":"Game build")+" · "); s.append(el("span","mono",path)); return s; };
        const status=(cls,text)=>{ const s=el("span","st "+cls); s.append(el("i","dot"),el("span",null,text)); return s; };
        const emptyRow=text=>el("div","empty",text);

        // ---- the game dialog (setup and panel) ----
        let gEdit=null, gKind="project", gTimer, gSeq=0;
        function setKind(k){ gKind=k; for(const x of ["project","build"]) $("gk-"+x).setAttribute("aria-pressed",String(x===k));
          $("gp").placeholder=k==="project"?"Full path of the Unity project folder":"Full path of the game (.app, .exe or the game file)"; }
        function openGame(g){
          gEdit=g||null; $("gd-t").textContent=g?"Change "+g.name:"Add a game"; $("gok").textContent=g?"Save":"Add game";
          $("grm").hidden=!g; $("gp").value=g?g.path:""; $("gn").value=g?g.name:""; $("gn").dataset.auto=g?"":"1";
          $("gst").textContent=""; $("gst").className="hint"; setKind(g?g.kind:"project"); $("gd").showModal(); $("gp").focus();
        }
        async function check(){
          const p=$("gp").value.trim(), s=$("gst"), n=++gSeq;
          if(!p){ s.textContent=""; s.className="hint"; return; }
          try{
            const j=await post("/panel/api/games/check",{path:p}); if(n!==gSeq) return;
            s.className="hint ok"; s.textContent=j.kind==="project"?"Unity project found":"Game build found"; setKind(j.kind);
            if(!$("gn").value||$("gn").dataset.auto==="1"){ $("gn").value=j.name; $("gn").dataset.auto="1"; }
          }catch(e){ if(n===gSeq){ s.className="hint bad"; s.textContent=e.message; } }
        }
        if($("gd")){
          on("gk-project","onclick",()=>setKind("project")); on("gk-build","onclick",()=>setKind("build"));
          on("gp","oninput",()=>{ clearTimeout(gTimer); gTimer=setTimeout(check,400); });
          on("gn","oninput",()=>$("gn").dataset.auto="");
          on("gcancel","onclick",()=>$("gd").close());
          on("gchoose","onclick",()=>act($("gchoose"),async()=>{ const j=await post("/panel/api/choose",{kind:gKind}); if(j.path){ $("gp").value=j.path; await check(); } },"Waiting…"));
          on("grm","onclick",()=>{ if(gEdit&&confirm("Remove "+gEdit.name+" from UDriver? Agents can no longer use it. Nothing is deleted from your disk."))
            act($("grm"),async()=>{ await post("/panel/api/games/remove",{id:gEdit.id}); $("gd").close(); }); });
          on("gf","onsubmit",e=>{ e.preventDefault(); act($("gok"),async()=>{
            const d={path:$("gp").value.trim()}, n=$("gn").value.trim(); if(n) d.name=n;
            let j; if(gEdit){ if(d.path===gEdit.path) delete d.path; j=await post("/panel/api/games/edit",{id:gEdit.id,...d}); } else j=await post("/panel/api/games/add",d);
            $("gd").close(); if(j.warning) say(j.warning,true); }); });
          on("addgame","onclick",()=>openGame());
        }

        // ---- pages ----
        const ACCESS=[["watch","Watch"],["play","Play"],["full","Full control"]];
        let addRow=$("addgame"), connectRow=$("connect"), setupLevel=false;
        function gameRows(games,full){
          if(!games.length) return [emptyRow(full?"No games yet. Add a Unity project or a game build so agents have something to work with.":"No games yet.")];
          return games.map(g=>{
            const row=el("div","row"), k=el("div","k");
            k.append(el("span","name",g.name),sub(g.kind,g.path));
            if(full){
              k.append(!g.exists?status("bad","Not found at this path. Use Change folder."):g.state==="editor open"?status("on","Editor open"):g.state==="running"?status("on","Running"):status("","Not running"));
              const sel=el("select","select"); sel.setAttribute("aria-label","Access for "+g.name);
              const cur=ACCESS.findIndex(([v])=>v===g.access);
              ACCESS.forEach(([v,t],i)=>{ const higher=!local&&i>cur, o=el("option",null,higher?t+" (only on the computer that runs UDriver)":t); o.value=v; o.selected=i===cur; o.disabled=higher; sel.append(o); });
              sel.onchange=()=>act(sel,()=>post("/panel/api/games/edit",{id:g.id,access:sel.value}));
              row.append(k,sel);
              if(local) row.append(button("link","Change folder…",()=>openGame(g)));
              if(g.kind==="build"&&g.exists){
                if(g.state!=="running"){ if(local) row.append(button("pill blue","Launch",b=>act(b,()=>post("/panel/api/games/start",{id:g.id}),"Launching…"))); }
                else if(g.startedByUDriver) row.append(button("pill plain","Stop",b=>act(b,()=>post("/panel/api/games/stop",{id:g.id}))));
              }
            } else row.append(k,button("link","Change…",()=>openGame(g)));
            return row;
          });
        }
        function since(s){ const h=Math.floor(s/3600), m=Math.floor(s%3600/60); return h?h+" h "+m+" min":m?m+" min":"less than a minute"; }
        const BUSY={installing:"Installing cloudflared…","signing-in":"Waiting for the Cloudflare sign-in…","setting-up":"Setting up the tunnel…"};
        function remoteLine(r){
          if(BUSY[r.state]) return ["wait",BUSY[r.state]];
          if(r.state==="online") return ["on","On. Tunnel online for "+since(r.onlineSec||0)+"."];
          if(r.state==="starting") return ["wait","On. The tunnel is starting…"];
          if(r.on) return ["bad","On, but the tunnel is not connected. "+(r.error||"Try Restart.")];
          if(r.state==="error") return ["bad","Off. "+r.error];
          if(r.state==="no-cloudflared") return ["","Off. Needs Cloudflare's free cloudflared app."];
          if(r.state==="signed-out") return ["","Off. Not set up yet."];
          if(r.state==="ready") return ["","Off. Signed in to Cloudflare; choose an address next."];
          return ["","Off."];
        }
        let R={};
        async function panel(){
          const [g,a,r]=await Promise.all([get("/panel/api/games"),get("/panel/api/agents"),get("/panel/api/remote")]); R=r;
          $("games").replaceChildren(...gameRows(g.games,true),addRow);
          const agents=a.agents.filter(x=>!x.disconnected);
          $("agents-h").textContent="Connected agents · "+agents.length;
          $("agents").replaceChildren(...(agents.length?agents.map(x=>{
            const row=el("div","row"), k=el("div","k");
            const where=x.from==="remote"?"Remote"+(r.host?" · through "+r.host:""):"On this computer";
            k.append(el("span","name","Agent "+x.id),el("span","sub",where+(x.target?" · working in "+x.target+" now":"")));
            row.append(k,button("link danger","Disconnect",b=>act(b,()=>post("/panel/api/agents/disconnect",{id:x.id}))));
            return row;
          }):[emptyRow("No agent is connected right now.")]),connectRow);
          const [cls,text]=remoteLine(r); $("rm-st").className="st "+cls; $("rm-text").textContent=text;
          $("rm-switch").setAttribute("aria-checked",String(r.on)); $("rm-switch").disabled=!!BUSY[r.state]||(!local&&!r.on);
          show("rm-addr",r.on); $("rm-url").textContent=r.address; show("rm-code",r.on); show("rm-tunnel",r.on);
          if($("rm-cf")){
            $("rm-cf-text").textContent=!r.cloudflared?"cloudflared is not installed":r.signedIn?"Signed in"+(r.domain?" · domain "+r.domain:""):"Not signed in";
            $("rm-change").textContent=r.saved?"Change address…":"Set up…"; show("rm-signout",r.signedIn&&r.cloudflared);
          }
          if($("pw-text")&&!r.passwordSet) $("pw-text").textContent="Not set. Needed to open this panel from another device. Never given to agents.";
          $("alive").className="st on"; $("alive").lastChild.textContent="On";
        }
        async function setup(){
          const g=await get("/panel/api/games");
          $("games").replaceChildren(...gameRows(g.games,false),addRow);
          if(!setupLevel){ setupLevel=true; for(const x of document.getElementsByName("level")) x.checked=x.value===g.defaultAccess; }
          setup.games=g.games;
        }
        async function remote(){
          const r=await get("/panel/api/remote"); R=r;
          show("r-install",!r.cloudflared); show("r-inst",!r.cloudflared&&!!r.installer); show("r-dl",!r.cloudflared&&!r.installer);
          $("r-dl").href=r.downloads; $("r-inst").disabled=r.state==="installing"; $("r-inst").textContent=r.state==="installing"?"Installing…":"Install cloudflared";
          if(r.installer) $("r-inst-text").textContent="Cloudflare's free app that runs the tunnel. UDriver installs it with "+(r.installer==="brew"?"Homebrew":"winget")+".";
          show("r-yes",r.signedIn); show("r-no",!r.signedIn);
          $("r-cf-t").textContent=r.signedIn?"Signed in":"Not signed in";
          $("r-cf-sub").textContent=r.signedIn?(r.domain?"Domain "+r.domain:"Domain: the one you picked in Cloudflare"):"Sign in and pick the domain to use.";
          $("r-login").textContent=r.signedIn?"Pick another domain…":"Sign in to Cloudflare"; $("r-login").disabled=!r.cloudflared||!!BUSY[r.state];
          show("r-wait",r.state==="signing-in"); show("r-wait-url",!!r.loginUrl); if(r.loginUrl) $("r-wait-url").href=r.loginUrl;
          if(remote.domain===undefined){ remote.domain=r.domain; if(r.host) $("rn").value=r.domain?r.host.slice(0,-r.domain.length-1):r.host; nameMode(); }
          $("rp").placeholder=r.passwordSet?"Leave empty to keep the current one":"At least 12 characters";
          const doing=r.state==="setting-up"; $("r-on").disabled=!!BUSY[r.state]||!r.cloudflared;
          $("r-on").textContent=doing?"Turning on…":r.on?"Save":"Turn on remote access";
          const err=r.error&&(r.state==="error"||r.on&&r.state!=="online"&&r.state!=="starting")?r.error:"";
          $("r-err").textContent=err; show("r-err",!!err);
          if(remote.waiting&&!doing){ remote.waiting=false; if(r.on&&!err) location.href="/panel"; }
        }
        const NAME=/^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/, DOMAIN=/^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$/;
        function host(){ const v=$("rn").value.trim().toLowerCase(); return remote.domain?v+"."+remote.domain:v; }
        function hostError(h){ const i=h.indexOf("."); if(i<0) return "Enter the full address, for example udriver.example.com.";
          if(!NAME.test(h.slice(0,i))) return "The name can use only a to z, 0 to 9 and hyphens, and cannot start or end with a hyphen.";
          if(!DOMAIN.test(h.slice(i+1))) return "This is not a domain name: "+h.slice(i+1); return ""; }
        function nameMode(){
          const d=remote.domain; $("rn-l").textContent=d?"Name":"Address"; $("rs").textContent=d?"."+d:""; show("rs",!!d);
          $("rn").placeholder=d?"udriver":"udriver.example.com"; showHost();
          $("r-host-foot").textContent=d?"":"Type the whole address: a name, a dot, and the domain you picked in Cloudflare.";
        }
        function showHost(){ const h=host(), e=h?hostError(h):""; $("r-url").textContent=h&&!e?"https://"+h+"/mcp":e||"—"; }
        const pages={panel,setup,remote,agent:async()=>{},main:async()=>{}};
        let failing=0;
        async function refresh(){
          if(busy||document.activeElement&&document.activeElement.tagName==="SELECT") return;
          try{ await pages[M.page](); failing=0; }
          catch(e){ if(++failing>1&&$("alive")){ $("alive").className="st bad"; $("alive").lastChild.textContent="Not answering"; } }
        }

        if(M.page==="main") on("copysetup","onclick",()=>copy($("setupcmd").textContent,$("copysetup")));
        if(M.page==="panel"){
          on("copycmd","onclick",()=>copy(M.cmd,$("copycmd")));
          on("rm-copy","onclick",()=>copy(R.address,$("rm-copy")));
          on("rm-restart","onclick",()=>act($("rm-restart"),()=>post("/panel/api/remote/restart"),"Restarting…"));
          on("rm-switch","onclick",()=>{
            if(R.on){ if(confirm("Turn off remote access? Remote agents lose access at once."+(local?"":" You will lose this page too."))) act($("rm-switch"),()=>post("/panel/api/remote/off")); }
            else if(R.saved&&R.cloudflared&&R.passwordSet) act($("rm-switch"),()=>post("/panel/api/remote/on"));
            else location.href="/panel?remote=1";
          });
          on("rm-signout","onclick",()=>{ if(confirm("Sign out of Cloudflare? Remote access turns off."))
            act($("rm-signout"),async()=>{ const j=await post("/panel/api/remote/signout"); if(j.note) say(j.note); }); });
          on("pw-change","onclick",()=>{ $("pp").value=""; $("pd").showModal(); $("pp").focus(); });
          on("pcancel","onclick",()=>$("pd").close());
          on("pf","onsubmit",e=>{ e.preventDefault(); act(null,async()=>{ const typed=$("pp").value, j=await post("/panel/api/password",{password:typed}); $("pd").close();
            say(typed?"The panel password was changed.":"New panel password: "+j.password+" Save it now: it is not shown again."); }); });
          on("stopform","onsubmit",e=>{ if(!confirm("Stop everything? This stops the games started through UDriver, disconnects every agent, closes the tunnel and stops UDriver.")) e.preventDefault(); });
          if(new URLSearchParams(location.search).has("done")) say("Setup is done. To connect an agent on this computer, use Copy command under Connected agents.");
        }
        if(M.page==="setup") on("finish","onclick",()=>act($("finish"),async()=>{
          const games=setup.games||[], level=[...document.getElementsByName("level")].find(x=>x.checked), where=[...document.getElementsByName("where")].find(x=>x.checked).value;
          if(!games.length) throw new Error("Add at least one Unity project or game build first.");
          if(!level) throw new Error("Choose what the agent may do.");
          await post("/panel/api/default-access",{access:level.value});
          for(const g of games) await post("/panel/api/games/edit",{id:g.id,access:level.value});
          location.href=where==="remote"?"/panel?remote=1":"/panel?done=1";
        }));
        if(M.page==="remote"){
          on("rn","oninput",showHost);
          on("r-show","onclick",()=>{ const p=$("rp"), b=$("r-show"); p.type=p.type==="password"?"text":"password"; b.textContent=p.type==="password"?"Show":"Hide"; });
          on("r-inst","onclick",()=>act($("r-inst"),()=>post("/panel/api/remote/install")));
          on("r-login","onclick",()=>act($("r-login"),async()=>{ await post("/panel/api/remote/login"); remote.domain=""; nameMode(); }));
          on("r-cancel","onclick",()=>act($("r-cancel"),()=>post("/panel/api/remote/cancel")));
          on("r-on","onclick",()=>act($("r-on"),async()=>{
            const h=host(), e=hostError(h), pw=$("rp").value; if(e) throw new Error(e);
            if(pw&&pw.length<12) throw new Error("The panel password must be at least 12 characters long.");
            if(!pw&&!R.passwordSet) throw new Error("Set a panel password of at least 12 characters.");
            await post("/panel/api/remote/on",{host:h,password:pw}); remote.waiting=true; $("rp").value="";
          }));
        }
        if(M.page==="agent"){
          const code=()=>act($("a-new"),async()=>{ try{ const j=await post("/panel/api/remote/code"); $("a-url").textContent=j.address; $("a-code").textContent=j.code; $("a-exp").textContent="Works once. Expires in 10 minutes."; }
            catch(e){ $("a-code").textContent="—"; $("a-exp").textContent=e.message; } });
          on("a-new","onclick",code); on("a-copy-url","onclick",()=>copy($("a-url").textContent,$("a-copy-url"))); on("a-copy-code","onclick",()=>copy($("a-code").textContent,$("a-copy-code")));
          code();
        }
        refresh(); if(M.page!=="main"&&M.page!=="agent") setInterval(refresh,2000);
        """;
}

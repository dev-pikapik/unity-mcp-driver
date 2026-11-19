using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    const string WrongCode = "This code is wrong, already used or expired. Get a new code in the UDriver panel.";
    const string CodeText = "A one-time code lets in one agent or one device, once. Get it in the UDriver panel on the computer that runs UDriver or on a device that is already connected.";
    sealed class Device { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public DateTime Since { get; set; } public DateTime Expires { get; set; } }
    static Dictionary<string, Device>? _devices;   // sha256(session) -> a device that signed in with a one-time code
    static string DevicesFile => Path.Combine(Program.HomeDir, "devices.json");
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
        _ = PanelKey;   // the launcher reads panel.key to open the panel of a server that is already running
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

    static void SignIn(HttpContext ctx)
    {
        string sid = Rand();
        var life = TimeSpan.FromDays(30);
        lock (Gate)
        {
            foreach (var k in Sessions.Where(x => x.Value.Exp < DateTime.UtcNow).Select(x => x.Key).ToList()) Sessions.Remove(k);
            Sessions[Hash(sid)] = (DateTime.UtcNow + life, Rand(18));
        }
        ctx.Response.Cookies.Append("udp", sid, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = life });
    }

    /// <summary>Signs in a device that entered a one-time code: 256 random bits in the cookie, only their hash kept in devices.json.</summary>
    static void SignInDevice(HttpContext ctx)
    {
        string sid = Rand();
        var life = TimeSpan.FromDays(30);
        lock (Gate)
        {
            var all = Devices();
            if (all.Count >= 50) all.Remove(all.MinBy(x => x.Value.Since).Key);
            all[Hash(sid)] = new Device { Id = Rand(9), Name = DeviceName(ctx.Request.Headers.UserAgent.ToString()), Since = DateTime.UtcNow, Expires = DateTime.UtcNow + life };
            SaveDevices();
        }
        ctx.Response.Cookies.Append("udp", sid, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = life });
    }

    /// <summary>The signed-in devices, without expired ones; call under Gate.</summary>
    static Dictionary<string, Device> Devices()
    {
        if (_devices == null) try { _devices = JsonSerializer.Deserialize<Dictionary<string, Device>>(File.ReadAllText(DevicesFile)); } catch { }
        _devices ??= new();
        foreach (var k in _devices.Where(x => x.Value.Expires < DateTime.UtcNow).Select(x => x.Key).ToList()) _devices.Remove(k);
        return _devices;
    }

    static void SaveDevices()
    {
        try { Program.WritePrivate(DevicesFile, JsonSerializer.Serialize(_devices)); }
        catch (Exception e) { Console.Error.WriteLine("UDriver: could not save " + DevicesFile + ": " + e.Message); }
    }

    /// <summary>Removes the device with this id, or every device when id is null.</summary>
    static bool DisconnectDevice(string? id)
    {
        lock (Gate)
        {
            var all = Devices();
            var gone = all.Where(x => id == null || x.Value.Id == id).Select(x => x.Key).ToList();
            gone.ForEach(k => all.Remove(k));
            if (gone.Count > 0) SaveDevices();
            return gone.Count > 0;
        }
    }

    static JsonArray DevicesJson(HttpContext ctx)
    {
        string mine = Hash(ctx.Request.Cookies["udp"] ?? "");
        lock (Gate)
            return new JsonArray(Devices().OrderBy(x => x.Value.Since).Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Value.Id, ["name"] = x.Value.Name, ["since"] = x.Value.Since.ToString("o"), ["you"] = x.Key == mine,
            }).ToArray());
    }

    /// <summary>A short name such as "iPhone, Safari" from a browser's User-Agent.</summary>
    static string DeviceName(string ua)
    {
        string device = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Macintosh") ? "Mac" : ua.Contains("CrOS") ? "Chromebook" : ua.Contains("Linux") ? "Linux" : "Device";
        string browser = ua.Contains("Edg") ? "Edge" : ua.Contains("Firefox") || ua.Contains("FxiOS") ? "Firefox" : ua.Contains("OPR") ? "Opera"
            : ua.Contains("Chrome") || ua.Contains("CriOS") ? "Chrome" : ua.Contains("Safari") ? "Safari" : "";
        return browser.Length > 0 ? device + ", " + browser : device;
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
        DisconnectDevice(null);
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
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
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

    /// <summary>Local requests use the session from the launcher's ticket, requests through the tunnel a device session.</summary>
    static (bool Ok, string Csrf) Who(HttpContext ctx, bool local)
    {
        string sid = ctx.Request.Cookies["udp"] ?? "";
        if (sid.Length == 0) return (false, "");
        lock (Gate)
        {
            if (!local) return Devices().ContainsKey(Hash(sid)) ? (true, Hash("csrf:" + sid)) : (false, "");
            if (Sessions.TryGetValue(Hash(sid), out var s) && s.Exp > DateTime.UtcNow) return (true, s.Csrf);
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
            if (TakeTicket(ctx.Request.Query["ticket"].ToString())) SignIn(ctx);
            ctx.Response.Redirect("/panel");
            return;
        }
        var who = Who(ctx, local);

        if (path == "/panel" && verb == "GET")
        {
            var q = ctx.Request.Query;
            if (!who.Ok) await Html(ctx, LoginPage(local, null));
            else if (q.ContainsKey("agent") || q.ContainsKey("device")) await Html(ctx, AgentPage(local, who.Csrf, q.ContainsKey("device")));
            else if (!local) await Html(ctx, PanelPage(false, who.Csrf));
            else if (q.ContainsKey("setup")) await Html(ctx, SetupPage(who.Csrf));
            else if (q.ContainsKey("remote")) await Html(ctx, RemotePage(who.Csrf));
            else if (q.ContainsKey("start") || Registry.Games.Count == 0 && !File.Exists(TunnelYml)) await Html(ctx, MainPage());
            else await Html(ctx, PanelPage(true, who.Csrf));
            return;
        }
        if (path == "/panel/login" && verb == "POST" && !local)
        {
            var f = await Form(ctx);
            string? err = OwnOrigin(ctx, local) ? await Slow(ctx, () => TakeCode(f.GetValueOrDefault("otc", "")), WrongCode) : "Request rejected.";
            if (err != null) { await Html(ctx, LoginPage(false, err), StatusOf(err)); return; }
            SignInDevice(ctx);
            ctx.Response.Redirect("/panel");
            return;
        }
        if (!who.Ok) { await Write(ctx, 401, "text/plain; charset=utf-8", "Sign-in required: open /panel"); return; }

        if (path == "/panel/api/games" && verb == "GET") { await Json(ctx, 200, await GamesJson()); return; }
        if (path == "/panel/api/agents" && verb == "GET") { await Json(ctx, 200, new JsonObject { ["agents"] = Registry.Agents(), ["devices"] = DevicesJson(ctx) }); return; }
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
                lock (Gate) { Sessions.Remove(Hash(ctx.Request.Cookies["udp"] ?? "")); if (Devices().Remove(Hash(ctx.Request.Cookies["udp"] ?? ""))) SaveDevices(); }
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
            case "/panel/api/devices/disconnect":
                bool off = DisconnectDevice(form.GetValueOrDefault("id", "-"));
                await Json(ctx, off ? 200 : 400, off ? new JsonObject { ["ok"] = true } : Fail("This device is no longer connected."));
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
    // Text colours are the darker iOS variants where the standard ones fall under 4.5:1.
    const string Css = """
        :root{color-scheme:light dark;
          --bg:#F2F2F7;--card:#FFFFFF;--sheet:#F2F2F7;--text:#000000;--text-2:#6C6C70;--text-3:#C4C4C7;--sep:#C6C6C8;--fill:#E3E3E8;--seg-on:#FFFFFF;--track:#E9E9EA;
          --blue:#0071E3;--blue-fill:#0071E3;--soft:#E1EDFB;--on-soft:#0062CC;--red:#E0281E;--red-dot:#FF3B30;--green:#34C759;--green-text:#1F7A36;--orange:#FF9500;--orange-text:#A84A00;
          --icon:#1C1C1E;--scrim:rgba(0,0,0,.4);
          --font:-apple-system,BlinkMacSystemFont,"Segoe UI",system-ui,sans-serif;--mono:ui-monospace,"SF Mono",Menlo,Consolas,monospace;
          --pad-l:max(16px,env(safe-area-inset-left));--pad-r:max(16px,env(safe-area-inset-right))}
        @media (prefers-color-scheme:dark){
          :root{--bg:#000000;--card:#1C1C1E;--sheet:#1C1C1E;--text:#FFFFFF;--text-2:#98989F;--text-3:#5A5A5E;--sep:#38383A;--fill:#2C2C2E;--seg-on:#636366;--track:#39393D;
            --blue:#0A84FF;--soft:#1C2A3D;--on-soft:#409CFF;--red:#FF453A;--red-dot:#FF453A;--green:#30D158;--green-text:#30D158;--orange:#FF9F0A;--orange-text:#FF9F0A;--icon:#3A3A3C;--scrim:rgba(0,0,0,.6)}
          dialog.sheet{--card:#2C2C2E;--sep:#48484A;--fill:#3A3A3C;--seg-on:#757579;--soft:#22324A;--blue:#409CFF;--red:#FF6961}}
        *,*::before,*::after{box-sizing:border-box} [hidden]{display:none!important}
        html{-webkit-text-size-adjust:100%;text-size-adjust:100%}
        body{margin:0;background:var(--bg);color:var(--text);font:17px/1.3 var(--font);-webkit-font-smoothing:antialiased;-webkit-tap-highlight-color:transparent}
        a{color:var(--blue);text-decoration:none}
        button,input{font:inherit;color:inherit}
        button{cursor:pointer}
        :focus-visible{outline:3px solid var(--blue);outline-offset:2px}
        main{max-width:640px;margin:0 auto;padding:calc(8px + env(safe-area-inset-top)) var(--pad-r) calc(40px + env(safe-area-inset-bottom)) var(--pad-l);display:flex;flex-direction:column;gap:28px}
        .mono,code{font-family:var(--mono);font-size:15px;overflow-wrap:anywhere}
        .url{font-size:15px;line-height:20px;overflow-wrap:anywhere}
        .sr{position:absolute!important;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
        .icon rect{fill:var(--icon)}
        .top{display:flex;align-items:center;gap:12px;padding-top:12px}
        .top .icon{width:40px;height:40px;flex:none}
        .top h1{flex:1;min-width:0;margin:0;font-size:34px;line-height:41px;font-weight:700}
        .top .st{font-size:15px;font-weight:600}
        .hero{display:flex;flex-direction:column;align-items:center;gap:12px;padding-top:28px;text-align:center}
        .hero .icon{width:72px;height:72px}
        .title{margin:0;font-size:34px;line-height:41px;font-weight:700}
        .subtitle{margin:0;font-size:17px;line-height:23px;color:var(--text-2)}
        .head{display:flex;flex-direction:column;gap:4px}
        .back{align-self:flex-start;display:inline-flex;align-items:center;min-height:44px;margin-left:-8px;padding:0 8px 0 2px;font-size:17px}
        .back svg{width:24px;height:24px}
        section{display:flex;flex-direction:column;gap:7px}
        .sec-h{margin:0;padding:0 16px;font-size:13px;line-height:18px;font-weight:400;text-transform:uppercase;color:var(--text-2)}
        .step-h{margin:0 0 3px;padding:0 4px;font-size:20px;line-height:25px;font-weight:600}
        .group{margin:0;padding:0;list-style:none;background:var(--card);border-radius:12px;overflow:hidden}
        .group>*{position:relative;margin:0}
        .group>*+*::before{content:"";position:absolute;top:0;left:16px;right:0;border-top:.5px solid var(--sep);pointer-events:none}
        .group :focus-visible{outline-offset:-3px}
        .group .pill:focus-visible,.group .link:focus-visible,.group .switch:focus-visible{outline-offset:2px}
        .row{display:flex;align-items:center;gap:12px;min-height:44px;padding:11px 16px}
        .row.game{flex-direction:column;align-items:stretch;gap:10px;padding-top:12px;padding-bottom:12px}
        .k{flex:1;min-width:0;display:flex;flex-direction:column;gap:3px}
        .name{font-weight:600}
        .sub{font-size:13px;line-height:18px;color:var(--text-2)} .sub .mono{font-size:13px}
        .clamp{display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden}
        .btns{display:flex;align-items:center;justify-content:space-between;gap:12px;margin:-6px 0 -10px}
        .btns .link:first-child{margin-left:-4px}
        .st{display:inline-flex;align-items:center;gap:6px;font-size:13px;line-height:18px;color:var(--text-2)}
        .dot{width:8px;height:8px;border-radius:50%;flex:none;background:var(--text-3)}
        .st.on{color:var(--green-text)} .st.on .dot{background:var(--green)}
        .st.bad{color:var(--red)} .st.bad .dot{background:var(--red-dot)}
        .st.wait{color:var(--orange-text)} .st.wait .dot{background:var(--orange)}
        .empty{min-height:44px;padding:12px 16px;font-size:15px;line-height:20px;color:var(--text-2)}
        .foot{margin:0;padding:0 16px;font-size:13px;line-height:18px;color:var(--text-2)}
        .foot.err{color:var(--red)} .foot.center{text-align:center}
        .act{display:flex;align-items:center;gap:12px;width:100%;min-height:44px;padding:11px 16px;border:0;background:none;color:var(--blue);font-size:17px;line-height:22px;text-align:left}
        .act.danger{color:var(--red)} .act.center{justify-content:center;text-align:center}
        .act.chev{color:var(--text)}
        .act.chev::after{content:"";flex:none;width:9px;height:9px;margin:0 4px 0 auto;border:solid var(--text-3);border-width:2px 2px 0 0;transform:rotate(45deg)}
        .act svg{width:22px;height:22px;flex:none}
        .act:active,.choice:active{background:var(--fill)}
        .link{flex:none;display:inline-flex;align-items:center;min-height:44px;padding:0 4px;border:0;background:none;color:var(--blue);font-size:17px}
        .link.danger{color:var(--red)}
        .pill{position:relative;flex:none;display:inline-flex;align-items:center;justify-content:center;min-width:72px;min-height:34px;padding:0 14px;border:0;border-radius:17px;background:var(--soft);color:var(--on-soft);font-size:15px;font-weight:600}
        .pill::after{content:"";position:absolute;inset:-5px -2px}
        .pill.blue{background:var(--blue-fill);color:#FFFFFF}
        .btn{display:flex;align-items:center;justify-content:center;width:100%;min-height:50px;padding:0 20px;border:0;border-radius:12px;background:var(--soft);color:var(--on-soft);font-size:17px;font-weight:600;text-align:center}
        .btn.primary{background:var(--blue-fill);color:#FFFFFF}
        .btn.plain{background:var(--card);color:var(--blue)}
        .btn.danger{background:var(--card);color:var(--red)}
        .link:active,.pill:active,.btn:active{opacity:.6}
        .btn:disabled,.pill:disabled,.link:disabled,.act:disabled{opacity:.4;cursor:default}
        .stack{display:flex;flex-direction:column;gap:10px}
        main>form.stack{gap:24px}
        .switch{appearance:none;-webkit-appearance:none;position:relative;flex:none;width:51px;height:31px;margin:0;border-radius:16px;background:var(--track);cursor:pointer;transition:background .2s}
        .switch::before{content:"";position:absolute;top:2px;left:2px;width:27px;height:27px;border-radius:50%;background:#FFFFFF;box-shadow:0 2px 5px rgba(0,0,0,.2);transition:transform .2s}
        .switch::after{content:"";position:absolute;inset:-7px -4px}
        .switch:checked{background:var(--green)} .switch:checked::before{transform:translateX(20px)}
        .switch:disabled{opacity:.4;cursor:default}
        .seg{display:grid;grid-auto-flow:column;grid-auto-columns:1fr;padding:2px;border-radius:10px;background:var(--fill)}
        .seg label{position:relative;display:flex}
        .seg input{position:absolute;inset:0;width:100%;height:100%;margin:0;opacity:0;cursor:pointer}
        .seg span{flex:1;display:flex;align-items:center;justify-content:center;min-height:40px;padding:0 6px;border-radius:8px;font-size:15px;line-height:18px;text-align:center}
        .seg input:checked+span{background:var(--seg-on);font-weight:600;box-shadow:0 2px 6px rgba(0,0,0,.12)}
        .seg input:focus-visible+span{outline:3px solid var(--blue);outline-offset:-1px}
        .seg input:active+span{opacity:.6}
        .seg input:disabled{cursor:default} .seg input:disabled+span{opacity:.4}
        .choice{display:flex;align-items:center;gap:12px;min-height:44px;padding:11px 16px;cursor:pointer}
        .choice input{position:absolute;opacity:0;pointer-events:none}
        .choice b{font-weight:400}
        .choice small{font-size:13px;line-height:18px;color:var(--text-2)}
        .choice .check{flex:none;width:22px;height:22px;color:var(--blue);visibility:hidden}
        .choice input:checked~.check{visibility:visible}
        .choice:has(input:focus-visible){outline:3px solid var(--blue);outline-offset:-3px}
        .field{display:block;width:100%;min-width:0;min-height:44px;padding:11px 16px;border:0;border-radius:0;background:none;color:var(--text);font-size:17px}
        .field.mono{font-family:var(--mono);font-size:16px}
        .field::placeholder,.otc::placeholder{color:var(--text-2);opacity:1}
        .field.mono::placeholder{font-family:var(--font);font-size:17px}
        .otc{display:block;width:100%;min-height:64px;padding:12px 16px;border:0;background:none;color:var(--text);font:600 28px/1.2 var(--mono);letter-spacing:.1em;text-align:center;text-transform:uppercase}
        .otc::placeholder{font-weight:400}
        .namebox{flex-wrap:wrap;gap:4px 12px}
        .namebox label{flex:none}
        .nameline{flex:1 1 170px;display:flex;align-items:center;min-width:0}
        .nameline .field{flex:1;padding:0;text-align:right}
        .nameline .sfx{color:var(--text-2)}
        .badge{width:24px;height:24px;flex:none}
        .badge.no circle{fill:none;stroke:var(--text-3);stroke-width:2}
        .codebox{flex-direction:column;gap:6px;padding:20px 16px 16px;text-align:center}
        .code{font:600 34px/1.15 var(--mono);letter-spacing:.06em}
        .steps li{display:flex;align-items:flex-start;gap:12px;min-height:44px;padding:11px 16px;line-height:22px}
        .steps .n{flex:none;display:grid;place-items:center;width:24px;height:24px;margin-top:-1px;border-radius:50%;background:var(--soft);color:var(--on-soft);font-size:13px;font-weight:600}
        .hint{display:flex;align-items:center;gap:6px;margin:0;padding:0 16px;font-size:13px;line-height:18px;color:var(--text-2)}
        .hint:empty{display:none}
        .hint.ok{color:var(--green-text)} .hint.ok::before{content:"\2713";font-weight:700}
        .hint.bad{color:var(--red)}
        .spin{width:20px;height:20px;flex:none;border-radius:50%;border:2.5px solid var(--sep);border-top-color:var(--text-2);animation:spin .8s linear infinite}
        @keyframes spin{to{transform:rotate(360deg)}}
        .toast{inset:auto auto calc(20px + env(safe-area-inset-bottom)) 50%;transform:translateX(-50%);width:max-content;max-width:calc(100% - 32px);margin:0;padding:12px 18px;border:0;border-radius:14px;background:var(--text);color:var(--bg);font-size:15px;line-height:20px;overflow-wrap:anywhere}
        .toast.bad{background:#D70015;color:#FFFFFF}
        dialog.sheet{width:100%;max-width:100%;max-height:calc(100% - 24px - env(safe-area-inset-top));margin:auto 0 0;padding:0;border:0;border-radius:14px 14px 0 0;background:var(--sheet);color:var(--text);overscroll-behavior:contain}
        dialog.sheet[open]{animation:up .32s cubic-bezier(.2,.8,.2,1)}
        dialog.sheet::backdrop{background:var(--scrim)}
        @keyframes up{from{transform:translateY(100%)}}
        .sheet-body{display:flex;flex-direction:column;gap:20px;margin:0;padding:20px var(--pad-r) calc(16px + env(safe-area-inset-bottom)) var(--pad-l)}
        .sheet h2{margin:0;font-size:20px;line-height:25px;font-weight:700;text-align:center}
        .sheet-msg{margin:-12px 0 4px;font-size:15px;line-height:20px;color:var(--text-2);text-align:center}
        @media (min-width:600px){
          main{padding-top:calc(32px + env(safe-area-inset-top))}
          dialog.sheet{width:440px;margin:auto;border-radius:14px}
          dialog.sheet[open]{animation:pop .2s ease-out}
          .sheet-body{padding:24px}}
        @keyframes pop{from{opacity:0;transform:scale(.96)}}
        @media (prefers-reduced-motion:reduce){*,*::before,*::after{transition:none!important;animation:none!important}}
        """;

    const string Icon = """<svg class="icon" viewBox="0 0 64 64" aria-hidden="true"><rect width="64" height="64" rx="15" fill="#1C1C1E"/><path d="M24 18v28l8-7 5 11 5-2.4-5-10.6h10z" fill="#FFFFFF"/></svg>""";
    const string Plus = """<svg viewBox="0 0 22 22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M11 4v14M4 11h14"/></svg>""";
    const string Check = """<svg class="check" viewBox="0 0 22 22" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m5 11.5 4 4L17 7"/></svg>""";
    const string Back = """<a class="back" href="/panel"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M15 5 8 12l7 7"/></svg>UDriver</a>""";
    const string AccessText = "Watch: look only. Play: also click, type, move and press keys. Full control: also change game data.";

    internal static string Shell(string body, string title = "UDriver") =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">" +
        "<meta name=\"color-scheme\" content=\"light dark\"><meta name=\"theme-color\" content=\"#F2F2F7\" media=\"(prefers-color-scheme: light)\"><meta name=\"theme-color\" content=\"#000000\" media=\"(prefers-color-scheme: dark)\">" +
        $"<title>{H(title)}</title><style>{Css}</style></head><body>" +
        body + "</body></html>";

    /// <summary>A panel page: its data for the script sits on main, the shared script below it.</summary>
    static string Screen(string page, bool local, string csrf, string title, string body) => Shell(
        $"<main data-page=\"{page}\" data-csrf=\"{H(csrf)}\" data-local=\"{(local ? "1" : "")}\" data-cmd=\"{H(ServerCommand())}\">{body}</main>" +
        """
        <dialog id="ask" class="sheet" aria-labelledby="ask-t" aria-describedby="ask-m"><div class="sheet-body">
        <h2 id="ask-t"></h2><p id="ask-m" class="sheet-msg"></p>
        <div class="stack"><button type="button" class="btn danger" id="ask-ok"></button><button type="button" class="btn plain" id="ask-no" autofocus>Cancel</button></div>
        </div></dialog>
        <p id="say" class="toast" role="status" popover="manual"></p>
        """ + $"<script>{Js}</script>", title);

    static string GameDialog() => """
        <dialog id="gd" class="sheet" aria-labelledby="gd-t"><form id="gf" class="sheet-body">
        <h2 id="gd-t">Add a game</h2>
        <div class="seg" role="radiogroup" aria-label="What is it?"><label><input type="radio" name="gk" id="gk-project" value="project" checked><span>Unity project</span></label><label><input type="radio" name="gk" id="gk-build" value="build"><span>Game build</span></label></div>
        <section><label class="sec-h" for="gp">Where is it?</label>
        <div class="group"><input id="gp" class="field mono" type="text" autocomplete="off" autocapitalize="none" spellcheck="false" required><button type="button" class="act" id="gchoose">Choose on this computer…</button></div>
        <p id="gst" class="hint" role="status"></p></section>
        <section><label class="sec-h" for="gn">Name</label><div class="group"><input id="gn" class="field" type="text" autocomplete="off"></div>
        <p class="foot">The agent uses this name to tell your games apart.</p></section>
        <p class="foot">You can change the folder at any time. For a Unity project, UDriver adds its package to the project for you.</p>
        <div class="stack"><button type="submit" class="btn primary" id="gok">Add game</button><button type="button" class="btn plain" id="gcancel">Cancel</button><button type="button" class="btn danger" id="grm" hidden>Remove from list</button></div>
        </form></dialog>
        """;

    static string LoginPage(bool local, string? error) => Shell(local ? $"""
        <main>
        <header class="hero">{Icon}<h1 class="title">Open UDriver</h1><p class="subtitle">To open the panel on this computer, double-click {Launcher} in the udriver folder.</p></header>
        </main>
        """ : $"""
        <main>
        <header class="hero">{Icon}<h1 class="title">Open UDriver</h1><p class="subtitle">Enter the one-time code to use the UDriver panel on this device.</p></header>
        <form method="post" action="/panel/login" class="stack"><section>
        <div class="group">{CodeField}</div>
        {(error != null ? $"<p class=\"foot err\" role=\"alert\">{H(error)}</p>" : "")}
        <p class="foot">Get the code on the computer that runs UDriver: in the panel, open Remote access, then Open on another device.</p>
        </section><button class="btn primary" type="submit">Continue</button></form>
        </main>
        """, "Open UDriver");

    static string StoppedPage() => Shell($"""
        <main>
        <header class="hero">{Icon}<h1 class="title">UDriver stopped</h1><p class="subtitle">Games started through UDriver, the tunnel and the server are off. Agents can no longer reach your games.</p></header>
        <section><h2 class="sec-h">To start again</h2>
        <div class="group"><div class="row"><span class="k">Double-click {Launcher} in the udriver folder on the computer with Unity.</span></div></div>
        <p class="foot">The panel won't open while UDriver is off. That's expected.</p></section>
        </main>
        """, "UDriver stopped");

    static string MainPage() => Screen("main", true, "", "Set up UDriver", $"""
        <header class="hero">{Icon}<h1 class="title">Set up UDriver</h1>
        <p class="subtitle">Let an agent play and test your Unity game. Three short questions; you can change every answer later.</p></header>
        <section><a class="btn primary" href="/panel?setup=1">Set up here</a>
        <p class="foot center">Three short steps on this page. Nothing to type.</p></section>
        <section><h2 class="sec-h">Or in the Terminal app</h2>
        <div class="group"><div class="row"><code id="setupcmd">{H(ServerCommand())} setup</code></div><button type="button" class="act" id="copysetup">Copy command</button></div>
        <p class="foot">Paste it into a Terminal window. It asks the same three questions.</p></section>
        <p class="foot center">Everything runs on this computer. The agent sees only the games you add: never your files, terminal or other apps.</p>
        """);

    static string SetupPage(string csrf) => Screen("setup", true, csrf, "Set up UDriver", $"""
        <header class="head">{Back}<h1 class="title">Set up UDriver</h1><p class="subtitle">Three questions. You can change every answer later.</p></header>
        <section><h2 class="step-h">1. What should the agent control?</h2>
        <div class="group" id="games"><button type="button" class="act" id="addgame">{Plus}Add a project or a build…</button></div>
        <p class="foot">Add as many as you need. The agent can drive several at the same time, for example two game clients side by side.</p></section>
        <section><h2 class="step-h" id="level-h">2. What may the agent do?</h2>
        <div class="seg" role="radiogroup" aria-labelledby="level-h">
        <label><input type="radio" name="level" value="watch"><span>Watch</span></label><label><input type="radio" name="level" value="play"><span>Play</span></label><label><input type="radio" name="level" value="full"><span>Full control</span></label></div>
        <div class="group">
        <div class="row"><span class="k"><b>Watch</b><span class="sub">Look at the screen, the interface and the game data. Changes nothing.</span></span></div>
        <div class="row"><span class="k"><b>Play</b><span class="sub">Also click, type, move and press keys, like a player would.</span></span></div>
        <div class="row"><span class="k"><b>Full control</b><span class="sub">Also change game data and call game code. For finding bugs.</span></span></div>
        </div>
        <p class="foot">At every level the agent works only inside the games above. It never gets your files, terminal or other apps.</p></section>
        <section><h2 class="step-h">3. Where is your agent?</h2>
        <div class="group">
        <label class="choice"><input type="radio" name="where" value="here" checked><span class="k"><b>On this computer</b><small>Agents running on this machine. The panel gives you the one command to add.</small></span>{Check}</label>
        <label class="choice"><input type="radio" name="where" value="remote"><span class="k"><b>Somewhere else</b><small>A web agent, a phone or another machine. On the next screen you sign in to Cloudflare, the free service that connects them.</small></span>{Check}</label>
        </div></section>
        <section><button type="button" class="btn primary" id="finish">Finish setup</button><p class="foot center">Takes about a minute.</p></section>
        {GameDialog()}
        """);

    static string PanelPage(bool local, string csrf) => Screen("panel", local, csrf, "UDriver", $"""
        <header class="top">{Icon}<h1>UDriver</h1><span class="st on" id="alive"><i class="dot"></i><span>On</span></span></header>
        <section><h2 class="sec-h">Games</h2>
        <div class="group" id="games">{(local ? $"""<button type="button" class="act" id="addgame">{Plus}Add a project or a build…</button>""" : """<div class="empty" id="addgame">Adding games, changing folders, launching games and raising access work only on the computer that runs UDriver.</div>""")}</div>
        <p class="foot">Access is set for each game. {AccessText} No agent ever gets your files, terminal or other apps.</p></section>
        <section><h2 class="sec-h" id="agents-h">Connected agents</h2>
        <div class="group" id="agents"><button type="button" class="act" id="copycmd">Copy command to connect an agent</button></div>
        <p class="foot">On this computer, add UDriver to your agent as an MCP server (the standard way agents plug in tools) with this command.</p></section>
        <section><h2 class="sec-h" id="devices-h">Connected devices</h2>
        <div class="group" id="devices"></div>
        <p class="foot">Phones, tablets and other computers that opened this panel with a one-time code. Each stays signed in for 30 days unless you disconnect it.</p></section>
        <section><h2 class="sec-h">Remote access</h2>
        <div class="group">
        <div class="row"><div class="k"><label for="rm-switch" id="rm-label">Use from another device or a web agent</label><span class="st" id="rm-st"><i class="dot"></i><span id="rm-text">Checking…</span></span></div>
        <input type="checkbox" class="switch" role="switch" id="rm-switch" aria-describedby="rm-text"></div>
        <div class="row" id="rm-addr" hidden><div class="k"><span class="sub">Address</span><span class="url" id="rm-url"></span></div><button type="button" class="pill" id="rm-copy">Copy</button></div>
        <a class="act chev" id="rm-code" href="/panel?agent=1" hidden><span class="k"><span>Add a remote agent</span><span class="sub">Get the address and a one-time code</span></span></a>
        <a class="act chev" id="rm-device" href="/panel?device=1" hidden><span class="k"><span>Open on another device</span><span class="sub">Use this panel on a phone, a tablet or another computer</span></span></a>
        {(local ? """<div class="row" id="rm-cf"><div class="k"><span>Cloudflare</span><span class="sub" id="rm-cf-text"></span></div></div><a class="act chev" href="/panel?remote=1" id="rm-change">Change address…</a><button type="button" class="act danger" id="rm-signout" hidden>Sign out of Cloudflare</button>""" : "")}
        <div class="row" id="rm-tunnel" hidden><div class="k"><span>Tunnel</span><span class="sub">Restart it if a remote agent cannot connect.</span></div><button type="button" class="link" id="rm-restart">Restart</button></div>
        </div>
        <p class="foot">Turning this off closes the tunnel at once. Remote agents and devices lose access; agents on this computer keep working.</p></section>
        <section><div class="group">
        {(local ? """<a class="act chev" href="/panel?setup=1">Run setup again</a>""" : $"""<form method="post" action="/panel/logout"><input type="hidden" name="csrf" value="{H(csrf)}"><button class="act" type="submit">Sign out of the panel</button></form>""")}
        <form method="post" action="/panel/stop" id="stopform"><input type="hidden" name="csrf" value="{H(csrf)}"><button class="act danger" type="submit">Stop everything</button></form>
        </div>
        <p class="foot">Stop everything turns UDriver off on the computer that runs it, together with the games it started.</p></section>
        {(local ? GameDialog() : "")}
        """);

    static string RemotePage(string csrf) => Screen("remote", true, csrf, "Remote access · UDriver", $"""
        <header class="head">{Back}<h1 class="title">Remote access</h1><p class="subtitle">Use UDriver from another device or a web agent. Two steps, no terminal.</p></header>
        <section id="r-install" hidden><h2 class="step-h">First, get cloudflared</h2>
        <div class="group"><div class="row"><div class="k"><span class="name">cloudflared is not installed</span><span class="sub" id="r-inst-text">Cloudflare's free app that runs the tunnel on this computer.</span></div></div>
        <button type="button" class="act" id="r-inst" hidden>Install cloudflared</button><a class="act" id="r-dl" target="_blank" rel="noopener" hidden>Download from Cloudflare</a></div>
        <p class="foot" id="r-inst-foot">After installing it, this page notices it on its own.</p></section>
        <section><h2 class="step-h">1. Sign in to Cloudflare</h2>
        <div class="group"><div class="row">
        <svg class="badge" id="r-yes" viewBox="0 0 24 24" aria-hidden="true" hidden><circle cx="12" cy="12" r="11" fill="#34C759"/><path d="m7 12.5 3.2 3.2L17 9" fill="none" stroke="#FFFFFF" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>
        <svg class="badge no" id="r-no" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10"/></svg>
        <div class="k"><span class="name" id="r-cf-t">Not signed in</span><span class="sub" id="r-cf-sub">Sign in and pick the domain to use.</span></div></div>
        <button type="button" class="act" id="r-login">Sign in to Cloudflare</button>
        <div class="row" id="r-wait" hidden><span class="spin" aria-hidden="true"></span><div class="k"><span>Finish the sign-in in the Cloudflare tab</span><span class="sub">No tab? <a id="r-wait-url" target="_blank" rel="noopener">Open the Cloudflare page</a>.</span></div><button type="button" class="link" id="r-cancel">Cancel</button></div>
        </div>
        <p class="foot">Cloudflare opens in a new tab, shows the domains in your account and asks which one to use. UDriver never sees your Cloudflare password.</p></section>
        <section><h2 class="step-h">2. Choose the address</h2>
        <div class="group"><div class="row namebox"><label for="rn" id="rn-l">Name</label><span class="nameline"><input id="rn" class="field" type="text" value="udriver" autocomplete="off" spellcheck="false" autocapitalize="none"><span class="sfx" id="rs"></span></span></div>
        <div class="row"><div class="k"><span class="sub">Agents will connect to</span><span class="url" id="r-url"></span></div></div></div>
        <p class="foot" id="r-host-foot"></p></section>
        <section><button type="button" class="btn primary" id="r-on">Turn on remote access</button><p class="foot center" id="r-note">Creates the tunnel. Takes about ten seconds.</p>
        <p class="foot err" id="r-err" role="alert" hidden></p></section>
        <p class="foot">Each agent or device then gets in with its own one-time code. {CodeText}</p>
        <p class="foot">The tunnel runs on this computer and nothing is opened on your router. You can restart it, move it to another domain or turn it off in the panel at any time.</p>
        """);

    static string AgentPage(bool local, string csrf, bool device) => Screen("agent", local, csrf, device ? "Open on another device · UDriver" : "Add a remote agent · UDriver", $"""
        <header class="head">{Back}<h1 class="title">{(device ? "Open on another device" : "Add a remote agent")}</h1><p class="subtitle">{(device ? "Open the address on the other device and enter the code there." : "Copy two things into your agent. That is all.")}</p></header>
        <section><h2 class="sec-h">Address</h2><div class="group">
        <div class="row"><span class="mono k" id="a-url"{(device ? " data-panel=\"1\"" : "")}>…</span></div><button type="button" class="act" id="a-copy-url">Copy address</button>
        </div></section>
        <section><h2 class="sec-h">One-time code</h2><div class="group">
        <div class="row codebox"><span class="code" id="a-code">…</span><span class="sub" id="a-exp">Works once. Expires in 10 minutes.</span></div>
        <button type="button" class="act" id="a-copy-code">Copy code</button><button type="button" class="act" id="a-new">Get a new code</button>
        </div></section>
        <section><h2 class="sec-h">What to do</h2>
        <ol class="group steps">{(device
            ? "<li><span class=\"n\">1</span><span>On the phone, tablet or other computer, open the address in a browser.</span></li><li><span class=\"n\">2</span><span>Enter the code and press Continue.</span></li><li><span class=\"n\">3</span><span>The device appears under Connected devices. Disconnect it there at any time.</span></li>"
            : "<li><span class=\"n\">1</span><span>In your agent, add an MCP server and paste the address.</span></li><li><span class=\"n\">2</span><span>A sign-in page opens. Paste the code and press Allow.</span></li><li><span class=\"n\">3</span><span>The agent appears under Connected agents. Disconnect it there at any time.</span></li>")}</ol>
        <p class="foot">{CodeText}</p></section>
        <a class="btn primary" href="/panel">Done</a>
        """);

    const string Js = """
        "use strict";
        const $=id=>document.getElementById(id), M=document.querySelector("main").dataset, csrf=M.csrf, local=M.local==="1";
        const el=(tag,cls,text)=>{ const e=document.createElement(tag); if(cls) e.className=cls; if(text!=null) e.textContent=text; return e; };
        const button=(cls,text,fn)=>{ const b=el("button",cls,text); b.type="button"; b.onclick=()=>fn(b); return b; };
        const show=(id,on)=>{ const e=$(id); if(e) e.hidden=!on; };
        const on=(id,ev,fn)=>{ const e=$(id); if(e) e[ev]=fn; };
        let busy=0, sayTimer;
        function say(text,bad){ const s=$("say"); clearTimeout(sayTimer); s.textContent=text; s.className="toast"+(bad?" bad":"");
          if(s.matches(":popover-open")) s.hidePopover(); if(text){ s.showPopover(); sayTimer=setTimeout(()=>s.hidePopover(),bad?9000:4000); } }
        function ask(title,text,ok){
          const d=$("ask"); $("ask-t").textContent=title; $("ask-m").textContent=text; $("ask-ok").textContent=ok; d.showModal();
          return new Promise(res=>{ $("ask-ok").onclick=()=>{ res(true); d.close(); }; $("ask-no").onclick=()=>d.close(); d.onclose=()=>res(false); });
        }
        for(const d of document.querySelectorAll("dialog")) d.addEventListener("click",e=>{ if(e.target===d) d.close(); });
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
        const sub=(kind,path)=>{ const s=el("span","sub clamp",(kind==="project"?"Unity project":"Game build")+" · "); s.title=path; s.append(el("span","mono",path)); return s; };
        const status=(cls,text)=>{ const s=el("span","st "+cls); s.append(el("i","dot"),el("span",null,text)); return s; };
        const emptyRow=text=>el("div","empty",text);

        // ---- the game dialog (setup and panel) ----
        let gEdit=null, gKind="project", gTimer, gSeq=0;
        function setKind(k){ gKind=k; for(const x of ["project","build"]) $("gk-"+x).checked=x===k;
          $("gp").placeholder=k==="project"?"Path to the Unity project folder":"Path to the .app, .exe or game file"; }
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
          on("gk-project","onchange",()=>setKind("project")); on("gk-build","onchange",()=>setKind("build"));
          on("gp","oninput",()=>{ clearTimeout(gTimer); gTimer=setTimeout(check,400); });
          on("gn","oninput",()=>$("gn").dataset.auto="");
          on("gcancel","onclick",()=>$("gd").close());
          on("gchoose","onclick",()=>act($("gchoose"),async()=>{ const j=await post("/panel/api/choose",{kind:gKind}); if(j.path){ $("gp").value=j.path; await check(); } },"Waiting for you to choose…"));
          on("grm","onclick",async()=>{ if(gEdit&&await ask("Remove "+gEdit.name+"?","Agents can no longer use it. Nothing is deleted from your disk.","Remove"))
            act($("grm"),async()=>{ await post("/panel/api/games/remove",{id:gEdit.id}); $("gd").close(); }); });
          on("gf","onsubmit",e=>{ e.preventDefault(); act($("gok"),async()=>{
            const d={path:$("gp").value.trim()}, n=$("gn").value.trim(); if(n) d.name=n;
            let j; if(gEdit){ if(d.path===gEdit.path) delete d.path; j=await post("/panel/api/games/edit",{id:gEdit.id,...d}); } else j=await post("/panel/api/games/add",d);
            $("gd").close(); if(j.warning) say(j.warning,true); }); });
          on("addgame","onclick",()=>openGame());
        }

        // ---- pages ----
        const ACCESS=[["watch","Watch"],["play","Play"],["full","Full control"]];
        let addRow=$("addgame"), connectRow=$("copycmd"), setupLevel=false;
        function accessSeg(g){
          const s=el("div","seg"), cur=ACCESS.findIndex(([v])=>v===g.access); s.setAttribute("role","radiogroup"); s.setAttribute("aria-label","Access for "+g.name);
          ACCESS.forEach(([v,t],i)=>{ const l=el("label"), r=el("input"); r.type="radio"; r.name="acc-"+g.id; r.id="acc-"+g.id+"-"+v; r.value=v; r.checked=i===cur; r.disabled=!local&&i>cur;
            r.onchange=()=>act(null,()=>post("/panel/api/games/edit",{id:g.id,access:v})); l.append(r,el("span",null,t)); s.append(l); });
          return s;
        }
        function gameRows(games,full){
          if(!games.length) return [emptyRow(full?"No games yet. Add a Unity project or a game build so agents have something to work with.":"No games yet.")];
          return games.map(g=>{
            const row=el("div","row"+(full?" game":"")), k=el("div","k");
            k.append(el("span","name",g.name),sub(g.kind,g.path));
            if(full){
              k.append(!g.exists?status("bad","Not found at this path. Use Change folder."):g.state==="editor open"?status("on","Editor open"):g.state==="running"?status("on","Running"):status("","Not running"));
              const btns=el("div","btns");
              if(local) btns.append(button("link","Change folder…",()=>openGame(g)));
              if(g.kind==="build"&&g.exists){
                if(g.state!=="running"){ if(local) btns.append(button("pill blue","Launch",b=>act(b,()=>post("/panel/api/games/start",{id:g.id}),"Launching…"))); }
                else if(g.startedByUDriver) btns.append(button("pill","Stop",b=>act(b,()=>post("/panel/api/games/stop",{id:g.id}))));
              }
              row.append(k,accessSeg(g)); if(btns.childElementCount) row.append(btns);
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
          const devices=a.devices||[];
          $("devices-h").textContent="Connected devices · "+devices.length;
          $("devices").replaceChildren(...(devices.length?devices.map(d=>{
            const row=el("div","row"), k=el("div","k");
            k.append(el("span","name",d.name+(d.you?" (this device)":"")),el("span","sub","Connected "+new Date(d.since).toLocaleDateString(undefined,{day:"numeric",month:"short",year:"numeric"})));
            row.append(k,button("link danger","Disconnect",async b=>{ if(!d.you||await ask("Disconnect this device?","To open the panel here again, you will need a new one-time code.","Disconnect"))
              act(b,()=>post("/panel/api/devices/disconnect",{id:d.id})); }));
            return row;
          }):[emptyRow("No other device is connected.")]));
          const [cls,text]=remoteLine(r); $("rm-st").className="st "+cls; $("rm-text").textContent=text;
          $("rm-switch").checked=!!r.on; $("rm-switch").disabled=!!BUSY[r.state]||(!local&&!r.on);
          show("rm-addr",r.on); $("rm-url").textContent=r.address; show("rm-code",r.on); show("rm-device",r.on); show("rm-tunnel",r.on);
          if($("rm-cf")){
            $("rm-cf-text").textContent=!r.cloudflared?"cloudflared is not installed":r.signedIn?"Signed in"+(r.domain?" · domain "+r.domain:""):"Not signed in";
            $("rm-change").textContent=r.saved?"Change address…":"Set up…"; show("rm-signout",r.signedIn&&r.cloudflared);
          }
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
          if(busy) return;
          const f=document.activeElement&&document.activeElement.id;
          try{ await pages[M.page](); failing=0; }
          catch(e){ if(++failing>1&&$("alive")){ $("alive").className="st bad"; $("alive").lastChild.textContent="Not answering"; } }
          if(f&&$(f)&&document.activeElement!==$(f)) $(f).focus({preventScroll:true});
        }

        if(M.page==="main") on("copysetup","onclick",()=>copy($("setupcmd").textContent,$("copysetup")));
        if(M.page==="panel"){
          on("copycmd","onclick",()=>copy(M.cmd,$("copycmd")));
          on("rm-copy","onclick",()=>copy(R.address,$("rm-copy")));
          on("rm-restart","onclick",()=>act($("rm-restart"),()=>post("/panel/api/remote/restart"),"Restarting…"));
          on("rm-switch","onclick",async e=>{
            e.preventDefault();
            if(R.on){ if(await ask("Turn off remote access?","Remote agents and devices lose access at once."+(local?"":" You will lose this page too."),"Turn off")) act($("rm-switch"),()=>post("/panel/api/remote/off")); }
            else if(R.saved&&R.cloudflared) act($("rm-switch"),()=>post("/panel/api/remote/on"));
            else location.href="/panel?remote=1";
          });
          on("rm-signout","onclick",async()=>{ if(await ask("Sign out of Cloudflare?","Remote access turns off.","Sign out"))
            act($("rm-signout"),async()=>{ const j=await post("/panel/api/remote/signout"); if(j.note) say(j.note); }); });
          on("stopform","onsubmit",async e=>{ e.preventDefault();
            if(await ask("Stop everything?","This stops the games started through UDriver, disconnects every agent and device, closes the tunnel and turns UDriver off.","Stop everything")) $("stopform").submit(); });
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
          on("r-inst","onclick",()=>act($("r-inst"),()=>post("/panel/api/remote/install")));
          on("r-login","onclick",()=>act($("r-login"),async()=>{ await post("/panel/api/remote/login"); remote.domain=""; nameMode(); }));
          on("r-cancel","onclick",()=>act($("r-cancel"),()=>post("/panel/api/remote/cancel")));
          on("r-on","onclick",()=>act($("r-on"),async()=>{
            const h=host(), e=hostError(h); if(e) throw new Error(e);
            await post("/panel/api/remote/on",{host:h}); remote.waiting=true;
          }));
        }
        if(M.page==="agent"){
          const code=()=>act($("a-new"),async()=>{ try{ const j=await post("/panel/api/remote/code"); $("a-url").textContent=$("a-url").dataset.panel?j.panel:j.address; $("a-code").textContent=j.code; $("a-exp").textContent="Works once. Expires in 10 minutes."; }
            catch(e){ $("a-code").textContent="—"; $("a-exp").textContent=e.message; } });
          on("a-new","onclick",code); on("a-copy-url","onclick",()=>copy($("a-url").textContent,$("a-copy-url"))); on("a-copy-code","onclick",()=>copy($("a-code").textContent,$("a-copy-code")));
          code();
        }
        refresh(); if(M.page!=="main"&&M.page!=="agent") setInterval(refresh,2000);
        """;
}

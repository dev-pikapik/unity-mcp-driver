// Control panel: a page in the browser instead of console commands.
//   On this computer:    http://127.0.0.1:<port>/panel   (no password; setup, status, stop)
//   On the public domain: https://<host>/panel            (owner password; status, stop)
// The server runs only while its launcher window is open; nothing is installed as a background service.
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

internal static partial class HttpMode
{
    static readonly string HomeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver");
    static string EnvFile => Path.Combine(HomeDir, "remote.env");
    static string TunnelYml => Path.Combine(HomeDir, "cloudflared.yml");
    static string CfPath => Env("UDRIVER_CLOUDFLARED").Length > 0 ? Env("UDRIVER_CLOUDFLARED") : "cloudflared";

    static bool _panel, _setupMode;
    static readonly DateTime Started = DateTime.UtcNow;
    static readonly string LocalCsrf = Rand(18);
    static readonly Dictionary<string, (DateTime Exp, string Csrf)> Sessions = new();
    static readonly List<PosixSignalRegistration> Signals = new();

    static Process? _cf;
    static readonly HashSet<string> CfConns = new();
    static string _cfNote = "";
    static bool _gameUp, _editorUp;
    static bool? _publicOk;

    static readonly List<string> SetupLog = new();
    static bool _setupRunning, _setupDone;
    static string? _setupError;

    static readonly Regex ConnRx = new(@"connIndex=(\d+)", RegexOptions.Compiled);

    // ---------------- lifecycle ----------------

    static void StartPanel()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillTunnel();
        foreach (var sig in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
            try { Signals.Add(PosixSignalRegistration.Create(sig, c => { c.Cancel = true; KillTunnel(); Environment.Exit(0); })); } catch { }
        if (!_setupMode) { StartTunnel(); Notify("UDriver started: " + _public); }
        _ = Task.Run(Monitor);
        if (Env("UDRIVER_OPEN_PANEL") == "1") OpenBrowser($"http://127.0.0.1:{_port}/panel");
    }

    static async Task Monitor()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        for (int tick = 0; ; tick++)
        {
            try
            {
                var g = Program.Ping(Program.GamePort); var e = Program.Ping(Program.EditorPort);
                _gameUp = await g; _editorUp = await e;
                if (!_setupMode && Env("UDRIVER_SKIP_PUBLIC_CHECK").Length == 0 && (tick == 2 || tick % 8 == 7))
                {
                    try { _publicOk = (await http.GetStringAsync(_public + "/.well-known/oauth-authorization-server")).Contains("/authorize"); }
                    catch { _publicOk = false; }
                }
            }
            catch { }
            await Task.Delay(4000);
        }
    }

    static void StartTunnel()
    {
        lock (Gate) { if (_cf is { HasExited: false }) return; CfConns.Clear(); }
        try
        {
            var psi = new ProcessStartInfo(CfPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
            foreach (var a in new[] { "tunnel", "--config", TunnelYml, "run", "udriver" }) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            void Line(string? l)
            {
                if (l == null) return;
                var m = ConnRx.Match(l);
                if (m.Success)
                    lock (Gate)
                    {
                        if (l.Contains("Registered tunnel connection")) CfConns.Add(m.Groups[1].Value);
                        else if (l.Contains("Unregistered tunnel connection") || l.Contains("Connection terminated") || l.Contains("Lost connection")) CfConns.Remove(m.Groups[1].Value);
                    }
                if (l.Contains(" ERR ")) _cfNote = l.Length > 220 ? l[..220] : l;
            }
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) => Line(e.Data);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => { lock (Gate) CfConns.Clear(); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _cf = p; _cfNote = "";
        }
        catch (Exception e) { _cfNote = "could not start cloudflared: " + e.Message; }
    }

    static void KillTunnel()
    {
        try { if (_cf is { HasExited: false }) _cf.Kill(true); } catch { }
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

    /// <summary>True only for a browser on this computer talking to the local address directly (never for traffic that came through the tunnel).</summary>
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
        if (local) return (true, LocalCsrf);
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

    /// <summary>Every HTML page goes out through here: no framing, and only our own &lt;script&gt; and &lt;style&gt; blocks apply (a fresh nonce per response).</summary>
    static Task Html(HttpContext ctx, string html, int status = 200, string formAction = "'self'")
    {
        string nonce = Rand(18);
        html = html.Replace("<script>", $"<script nonce=\"{nonce}\">").Replace("<style>", $"<style nonce=\"{nonce}\">");
        return Write(ctx, status, "text/html; charset=utf-8", html, r =>
        {
            r.Headers["X-Frame-Options"] = "DENY";
            r.Headers["Content-Security-Policy"] = $"default-src 'self'; style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; frame-ancestors 'none'; form-action {formAction}; base-uri 'none'";
            r.Headers["Referrer-Policy"] = "same-origin";   // not no-referrer: then browsers send "Origin: null" on form posts and the origin checks reject them
        });
    }

    // ---------------- routes ----------------

    static async Task Panel(HttpContext ctx, string path, string verb, bool local)
    {
        if (path == "/") { ctx.Response.Redirect("/panel"); return; }
        if (!local && (_setupMode || _public.Length == 0)) { await Write(ctx, 404, "text/plain", "not found"); return; }
        var who = Who(ctx, local);

        if (path == "/panel" && verb == "GET")
        {
            if (!who.Ok) { await Html(ctx, LoginPage(null)); return; }
            if (local && (_setupMode || ctx.Request.Query.ContainsKey("setup"))) { await Html(ctx, SetupPage()); return; }
            await Html(ctx, StatusPage(local, who.Csrf));
            return;
        }
        if (path == "/panel/login" && verb == "POST" && !local)
        {
            var f = await Form(ctx);
            string? err = OwnOrigin(ctx, false) ? CheckPassword(ctx, f.GetValueOrDefault("password", "")) : "Request rejected.";
            if (err != null) { await Task.Delay(700); await Html(ctx, LoginPage(err)); return; }
            string sid = Rand();
            lock (Gate)
            {
                foreach (var k in Sessions.Where(x => x.Value.Exp < DateTime.UtcNow).Select(x => x.Key).ToList()) Sessions.Remove(k);
                Sessions[Hash(sid)] = (DateTime.UtcNow.AddHours(2), Rand(18));
            }
            ctx.Response.Cookies.Append("udp", sid, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(2) });
            ctx.Response.Redirect("/panel");
            return;
        }
        if (!who.Ok) { await Write(ctx, 401, "text/plain; charset=utf-8", "Sign-in required: open /panel"); return; }

        if (path == "/panel/status.json" && verb == "GET") { await Json(ctx, 200, StatusJson()); return; }
        if (path == "/panel/progress" && verb == "GET" && local)
        {
            JsonArray lines; lock (SetupLog) lines = new JsonArray(SetupLog.Select(x => (JsonNode)x!).ToArray());
            await Json(ctx, 200, new JsonObject { ["lines"] = lines, ["running"] = _setupRunning, ["done"] = _setupDone, ["error"] = _setupError });
            return;
        }
        if (verb != "POST") { await Write(ctx, 404, "text/plain", "not found"); return; }

        var form = await Form(ctx);
        if (!OwnOrigin(ctx, local) || !Same(form.GetValueOrDefault("csrf", ""), who.Csrf)) { await Write(ctx, 403, "text/plain; charset=utf-8", "Request rejected (reload the panel page)."); return; }

        switch (path)
        {
            case "/panel/stop":
                await Html(ctx, StoppedPage());
                _ = Task.Run(async () => { await Task.Delay(600); Notify("UDriver stopped"); KillTunnel(); Environment.Exit(0); });
                return;
            case "/panel/revoke":
                lock (Gate) { _store.Access.Clear(); _store.Refresh.Clear(); _store.RefreshClient.Clear(); Save(); }
                ctx.Response.Redirect("/panel"); return;
            case "/panel/readonly":
                Program.ReadOnly = form.GetValueOrDefault("on") == "1";
                SetSetting("UDRIVER_READONLY", Program.ReadOnly ? "1" : null);
                ctx.Response.Redirect("/panel"); return;
            case "/panel/logout":
                lock (Gate) Sessions.Remove(Hash(ctx.Request.Cookies["udp"] ?? ""));
                ctx.Response.Cookies.Delete("udp");
                ctx.Response.Redirect("/panel"); return;
            case "/panel/tunnel" when local:
                KillTunnel(); await Task.Delay(500); StartTunnel();
                ctx.Response.Redirect("/panel"); return;
            case "/panel/choose-folder" when local:
                await Json(ctx, 200, new JsonObject { ["path"] = await ChooseFolder() });
                return;
            case "/panel/setup" when local:
                if (_setupRunning) { await Json(ctx, 200, new JsonObject { ["ok"] = true }); return; }
                lock (SetupLog) SetupLog.Clear();
                _setupRunning = true; _setupError = null; _setupDone = false;
                string d = form.GetValueOrDefault("domain", ""), pr = form.GetValueOrDefault("project", "");
                _ = Task.Run(() => RunSetup(d, pr));
                await Json(ctx, 200, new JsonObject { ["ok"] = true });
                return;
            default:
                await Write(ctx, 404, "text/plain", "not found"); return;
        }
    }

    static JsonObject StatusJson()
    {
        int conns, sessions;
        lock (Gate) { conns = CfConns.Count; sessions = _store.Refresh.Count(x => x.Value > DateTime.UtcNow); }
        bool alive = _cf is { HasExited: false };
        return new JsonObject
        {
            ["running"] = true, ["connector"] = _public + "/mcp", ["upSec"] = (int)(DateTime.UtcNow - Started).TotalSeconds,
            ["tunnelAlive"] = alive, ["tunnelConnections"] = conns, ["tunnelNote"] = alive && conns > 0 ? "" : _cfNote,
            ["publicOk"] = _publicOk, ["editor"] = _editorUp, ["game"] = _gameUp,
            ["sessions"] = sessions, ["calls"] = Program.CallCount, ["lastTool"] = Program.LastTool,
            ["lastAgoSec"] = Program.LastCallUtc == default ? null : (int)(DateTime.UtcNow - Program.LastCallUtc).TotalSeconds,
            ["readOnly"] = Program.ReadOnly,
        };
    }

    // ---------------- setup (runs on this computer only) ----------------

    static async Task<string> ChooseFolder()
    {
        if (!OperatingSystem.IsMacOS()) return "";
        var r = await RunTool("osascript", TimeSpan.FromMinutes(5), "-e", "POSIX path of (choose folder with prompt \"Choose your Unity project folder (the one containing Assets and Packages)\")");
        return r.Code == 0 ? r.Out.Trim().TrimEnd('/') : "";
    }

    static async Task<(int Code, string Out)> RunTool(string exe, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Exception("could not start " + exe);
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new Exception(exe + ": timed out"); }
        return (p.ExitCode, (await so) + (await se));
    }

    static async Task<string?> TunnelId()
    {
        var r = await RunTool(CfPath, TimeSpan.FromSeconds(60), "tunnel", "list");
        foreach (var line in r.Out.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "udriver" && Guid.TryParse(parts[0], out _)) return parts[0];
        }
        return null;
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

    static async Task RunSetup(string domain, string project)
    {
        void L(string s) { lock (SetupLog) SetupLog.Add(s); }
        try
        {
            string d = Regex.Replace(domain.Trim().ToLowerInvariant(), @"^https?://", "");
            int slash = d.IndexOf('/'); if (slash >= 0) d = d.Substring(0, slash);
            if (!Regex.IsMatch(d, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")) throw new Exception("Could not read the domain. It should look like example.org");
            string host = d.StartsWith("udriver.") ? d : "udriver." + d;

            project = project.Trim().TrimEnd('/');
            if (project.Length == 0 || !Directory.Exists(Path.Combine(project, "Assets")) || !Directory.Exists(Path.Combine(project, "Packages")))
                throw new Exception("This is not a Unity project folder: it must contain the Assets and Packages folders");
            string pkg = Path.Combine(Env("UDRIVER_DIST"), "com.udriver.agent");
            if (!Directory.Exists(pkg)) throw new Exception("The com.udriver.agent folder was not found next to the launch file");

            string cfHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared");
            if (!File.Exists(Path.Combine(cfHome, "cert.pem")))
            {
                L("A Cloudflare tab has opened: sign in and choose your domain. Waiting…");
                await RunTool(CfPath, TimeSpan.FromMinutes(5), "tunnel", "login");
                if (!File.Exists(Path.Combine(cfHome, "cert.pem"))) throw new Exception("Not signed in to Cloudflare");
            }
            L("Signed in to Cloudflare");

            string? id = await TunnelId();
            if (id == null)
            {
                L("Creating the tunnel…");
                var c = await RunTool(CfPath, TimeSpan.FromSeconds(90), "tunnel", "create", "udriver");
                id = await TunnelId();
                if (id == null) throw new Exception("Could not create the tunnel: " + c.Out.Trim());
            }
            string cred = Path.Combine(cfHome, id + ".json");
            if (!File.Exists(cred)) throw new Exception("Tunnel key not found: " + cred + ". If the udriver tunnel was created on another computer, delete it in Cloudflare and try again");
            L("Tunnel ready");

            var route = await RunTool(CfPath, TimeSpan.FromSeconds(90), "tunnel", "route", "dns", "--overwrite-dns", "udriver", host);
            if (route.Code != 0) throw new Exception("Could not link the address " + host + ". The domain must be in the Cloudflare account you signed in to. " + route.Out.Trim());
            if (!route.Out.Contains(host + " which") && !route.Out.Contains(host + " is already")) L("Warning, please check the address: " + route.Out.Trim());
            L("Address linked: " + host);

            Directory.CreateDirectory(HomeDir);
            Program.WritePrivate(TunnelYml, $"tunnel: {id}\ncredentials-file: {cred}\ningress:\n  - hostname: {host}\n    service: http://127.0.0.1:{_port}\n  - service: http_status:404\n");
            string pass = GetSetting("UDRIVER_OWNER_PASSWORD") ?? "";
            if (pass.Length < 12) { pass = NewPassword(); L("Owner password created"); } else L("Owner password kept unchanged");
            SetSetting("UDRIVER_HTTP_PORT", _port.ToString());
            SetSetting("UDRIVER_PUBLIC_URL", "https://" + host);
            SetSetting("UDRIVER_OWNER_PASSWORD", pass);
            if (GetSetting("UDRIVER_BUILDS_DIR") == null)
            {
                string builds = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UDriverBuilds");
                Directory.CreateDirectory(builds);
                SetSetting("UDRIVER_BUILDS_DIR", builds);
            }

            CopyDir(pkg, Path.Combine(project, "Packages", "com.udriver.agent"));
            File.WriteAllText(Path.Combine(HomeDir, "project"), project);
            L("Package copied into the Unity project");

            L("Done. Restarting the server…");
            _setupDone = true;
            await Task.Delay(1800);
            KillTunnel();
            Environment.Exit(3);   // the launcher restarts the server with the new settings
        }
        catch (Exception e) { _setupError = e.Message; }
        finally { _setupRunning = false; }
    }

    // ---------------- pages ----------------

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    // One small design system for every page (panel, setup, sign-in, OAuth). Html() adds the nonce to <style>, so no style="" attributes anywhere.
    const string Css = """
        /* Layout: grouped background, centered 680px column, inset grouped lists (radius 12, rows 44+, 16px insets). */
        :root{color-scheme:light dark;
          --bg:#F2F2F7;--surface:#FFFFFF;--surface-2:#F2F2F7;--label:#000;--label-2:rgba(60,60,67,.6);--sep:rgba(60,60,67,.29);--fill:rgba(120,120,128,.16);
          --accent:#007AFF;--accent-2:#5AC8FA;--accent-tint:rgba(0,122,255,.12);--ring:rgba(0,122,255,.4);
          --green:#34C759;--orange:#FF9500;--red:#FF3B30;--red-tint:rgba(255,59,48,.12);--gray:#8E8E93;--bar:rgba(242,242,247,.8);
          --font:-apple-system,BlinkMacSystemFont,"SF Pro Text","Helvetica Neue",system-ui,sans-serif;--mono:ui-monospace,"SF Mono",Menlo,monospace}
        @media (prefers-color-scheme:dark){:root{
          --bg:#000000;--surface:#1C1C1E;--surface-2:#2C2C2E;--label:#FFF;--label-2:rgba(235,235,245,.6);--sep:rgba(84,84,88,.65);--fill:rgba(120,120,128,.32);
          --accent:#0A84FF;--accent-2:#64D2FF;--accent-tint:rgba(10,132,255,.18);--ring:rgba(10,132,255,.5);
          --green:#30D158;--orange:#FF9F0A;--red:#FF453A;--red-tint:rgba(255,69,58,.18);--gray:#8E8E93;--bar:rgba(0,0,0,.72)}}
        *,*::before,*::after{box-sizing:border-box} [hidden]{display:none!important}
        html{-webkit-text-size-adjust:100%;background:var(--bg)}
        body{margin:0;background:var(--bg);color:var(--label);font:17px/1.35 var(--font);letter-spacing:-.01em;-webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale}
        :focus-visible{outline:3px solid var(--ring);outline-offset:2px}
        .mono,code{font-family:var(--mono);font-size:15px;letter-spacing:0}
        .sr{position:absolute!important;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
        .offscreen{position:fixed;top:0;left:-9999px;opacity:0}
        .bar{position:sticky;top:0;z-index:10;background:var(--bar);-webkit-backdrop-filter:saturate(180%) blur(20px);backdrop-filter:saturate(180%) blur(20px);border-bottom:.5px solid var(--sep)}
        .bar-in{max-width:680px;margin:0 auto;padding:0 16px;height:52px;display:flex;align-items:center;justify-content:space-between;gap:12px}
        .brand{display:flex;align-items:center;gap:8px;font-weight:600}
        .brand .icon{width:26px;height:26px}
        .pill{display:inline-flex;align-items:center;padding:4px 10px;border-radius:999px;background:var(--fill);font-size:13px;font-weight:500;white-space:nowrap}
        main{max-width:680px;margin:0 auto;padding:0 16px 56px}
        main.narrow{max-width:480px}
        .title{font-size:34px;line-height:41px;font-weight:700;letter-spacing:-.02em;margin:24px 0 2px}
        .subtitle{font-size:15px;line-height:20px;color:var(--label-2);margin:0}
        .hero{text-align:center;padding-top:56px}
        .hero .icon{display:block;width:76px;height:76px;margin:0 auto 18px;filter:drop-shadow(0 4px 12px rgba(0,122,255,.25))}
        .hero .title{margin:0 0 6px}
        .hero .subtitle{max-width:40ch;margin:0 auto}
        section{margin-top:32px}
        .sec-h{font-size:13px;line-height:18px;font-weight:400;text-transform:uppercase;letter-spacing:.04em;color:var(--label-2);margin:0 16px 7px}
        .sec-h label{cursor:default}
        .group{background:var(--surface);border-radius:12px;overflow:hidden}
        .group>*{position:relative}
        .group>*+*::before{content:"";position:absolute;top:0;left:16px;right:0;border-top:.5px solid var(--sep);pointer-events:none}
        .group.pad{padding:16px}
        .foot{font-size:13px;line-height:18px;color:var(--label-2);margin:7px 16px 0}
        .foot.err{color:var(--red)}
        .foot.center{text-align:center;margin-top:20px}
        .row{display:flex;align-items:center;gap:12px;min-height:44px;padding:11px 16px}
        label.row{cursor:pointer}
        .k{flex:1;min-width:0}
        .k code{display:block;margin-top:2px;color:var(--label-2);overflow-wrap:anywhere}
        .v{color:var(--label-2);text-align:right;min-width:0;overflow-wrap:anywhere}
        .st{display:inline-flex;align-items:center;gap:7px}
        .dot{width:8px;height:8px;border-radius:50%;background:var(--gray);flex:none}
        .dot.ok{background:var(--green)} .dot.warn{background:var(--orange)} .dot.bad{background:var(--red)}
        .act{display:flex;align-items:center;gap:8px;width:100%;min-height:44px;padding:11px 16px;margin:0;border:0;background:none;color:var(--accent);font:inherit;text-align:left;text-decoration:none;cursor:pointer;-webkit-tap-highlight-color:transparent}
        .act:hover{background:var(--surface-2)} .act:active{background:var(--fill)}
        .act:focus-visible{outline-offset:-3px}
        .act.danger{color:var(--red)} .act.center{justify-content:center} .act.nav{color:var(--label)}
        .chev{margin-left:auto;color:var(--label-2);font-size:20px;line-height:1}
        .btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;min-height:32px;padding:6px 14px;margin:0;border:0;border-radius:10px;font:inherit;font-size:15px;font-weight:500;white-space:nowrap;cursor:pointer;flex:none;-webkit-tap-highlight-color:transparent;transition:filter .15s,opacity .15s}
        .btn:hover{filter:brightness(1.08)} .btn:active{filter:brightness(.9)}
        .btn:disabled{opacity:.45;cursor:default;filter:none}
        .btn.tinted{background:var(--accent-tint);color:var(--accent)}
        .btn.primary{width:100%;min-height:50px;padding:14px 20px;background:var(--accent);color:#fff;font-size:17px;font-weight:600}
        .actions{margin-top:28px}
        .field{display:block;width:100%;min-height:44px;padding:11px 14px;margin:0;border:0;border-radius:10px;background:var(--surface-2);color:var(--label);font:inherit;font-size:17px}
        .field.mono{font-family:var(--mono);font-size:15px}
        .field::placeholder{color:var(--label-2)}
        .switch{-webkit-appearance:none;appearance:none;position:relative;flex:none;width:51px;height:31px;margin:0;border-radius:16px;background:var(--fill);cursor:pointer;transition:background .2s}
        .switch::before{content:"";position:absolute;top:2px;left:2px;width:27px;height:27px;border-radius:50%;background:#fff;box-shadow:0 3px 8px rgba(0,0,0,.15),0 1px 1px rgba(0,0,0,.16);transition:transform .2s}
        .switch:checked{background:var(--green)} .switch:checked::before{transform:translateX(20px)}
        .switch:disabled{opacity:.6}
        .path{display:flex;flex:1;min-width:0;font:14px/1.3 var(--mono);letter-spacing:0}
        .path .h{flex:0 1 auto;min-width:2ch;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .path .t{flex:0 0 auto;max-width:85%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .path .ph{font-family:var(--font);font-size:17px;letter-spacing:-.01em;color:var(--label-2)}
        .linkbtn{padding:0;border:0;background:none;color:var(--accent);font:inherit;cursor:pointer}
        .spin{width:18px;height:18px;flex:none;border-radius:50%;border:2.5px solid var(--fill);border-top-color:var(--label-2);animation:spin .8s linear infinite}
        .ck,.xk{position:relative;width:18px;height:18px;flex:none;border-radius:50%;background:var(--green)}
        .ck::after{content:"";position:absolute;left:6.5px;top:3.5px;width:4px;height:8px;border:solid #fff;border-width:0 2px 2px 0;transform:rotate(45deg)}
        .xk{background:var(--red);color:#fff;font:700 12px/18px var(--font);text-align:center}
        @keyframes spin{to{transform:rotate(360deg)}}
        dialog.alert{width:min(300px,calc(100% - 48px));padding:0;border:0;border-radius:14px;background:var(--surface);color:var(--label);text-align:center;box-shadow:0 24px 64px rgba(0,0,0,.3)}
        dialog.alert[open]{animation:pop .18s ease-out}
        dialog.alert::backdrop{background:rgba(0,0,0,.35)}
        .alert-body{padding:20px 18px 18px}
        .alert h2{font-size:17px;line-height:22px;font-weight:600;margin:0 0 4px}
        .alert p{font-size:13px;line-height:18px;margin:0}
        .alert-btns{display:flex;border-top:.5px solid var(--sep)}
        .alert-btns button{flex:1;min-height:44px;margin:0;border:0;background:none;color:var(--accent);font:inherit;cursor:pointer}
        .alert-btns button+button{border-left:.5px solid var(--sep)}
        .alert-btns button:hover{background:var(--surface-2)} .alert-btns button:focus-visible{outline-offset:-3px}
        .alert-btns .danger{color:var(--red);font-weight:600}
        @keyframes pop{from{opacity:0;transform:scale(1.08)}}
        @media (max-width:480px){.title{font-size:30px;line-height:36px}.k code{font-size:14px}.hero{padding-top:40px}}
        @media (prefers-reduced-motion:reduce){*,*::before,*::after{transition:none!important}dialog.alert[open]{animation:none}.spin{animation-duration:2.4s}}
        """;

    const string Icon = """<svg class="icon" viewBox="0 0 64 64" aria-hidden="true"><defs><linearGradient id="ig" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ig1"/><stop offset="1" class="ig2"/></linearGradient></defs><rect width="64" height="64" rx="14.3" fill="url(#ig)"/><path d="M32 14.5 47 23v18L32 49.5 17 41V23Z M17.5 23.3 32 31.6l14.5-8.3 M32 31.6V49" fill="none" stroke="#fff" stroke-width="3.2" stroke-linejoin="round" stroke-linecap="round"/></svg>""";

    internal static string Shell(string body, string title = "UDriver") =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">" +
        $"<meta name=\"color-scheme\" content=\"light dark\"><title>{H(title)}</title><style>{Css}.ig1{{stop-color:var(--accent-2)}}.ig2{{stop-color:var(--accent)}}</style></head><body>" +
        body + "</body></html>";

    static string LoginPage(string? error) => Shell($"""
        <main class="narrow">
        <header class="hero">{Icon}<h1 class="title">Sign in to UDriver</h1><p class="subtitle">Enter the owner password to open the control panel.</p></header>
        <form method="post" action="/panel/login"><section>
        <div class="group pad"><label class="sr" for="password">Owner password</label><input class="field" type="password" id="password" name="password" placeholder="Owner password" autofocus autocomplete="current-password"></div>
        {(error != null ? $"<p class=\"foot err\" role=\"alert\">{H(error)}</p>" : "")}
        </section><div class="actions"><button class="btn primary" type="submit">Sign In</button></div></form>
        <p class="foot center">Only the owner of the computer running UDriver can sign in.</p>
        </main>
        """, "Sign in · UDriver");

    static string StoppedPage() => Shell($"""
        <main class="narrow">
        <header class="hero">{Icon}<h1 class="title">UDriver stopped</h1><p class="subtitle">The server and the tunnel are off. Claude can no longer reach Unity.</p></header>
        <section><h2 class="sec-h">To start again</h2>
        <div class="group"><div class="row"><span class="k">Double-click <code>UDriver.command</code> in the <code>udriver</code> folder on the computer with Unity.</span></div></div>
        <p class="foot">This page on your domain won't open while the server is off. That's expected.</p></section>
        </main>
        """, "UDriver stopped");

    static string SetupPage()
    {
        string host = GetSetting("UDRIVER_PUBLIC_URL")?.Replace("https://", "") ?? "";
        string project = "";
        try { project = File.ReadAllText(Path.Combine(HomeDir, "project")).Trim(); } catch { }
        return Shell($$"""
            <main class="narrow">
            <header class="hero">{{Icon}}<h1 class="title">Set up Unity MCP Driver</h1><p class="subtitle">Connect Claude to your Unity project. Fill in two things, the rest is automatic.</p></header>
            <section><h2 class="sec-h"><label for="domain">Your Cloudflare domain</label></h2>
              <div class="group pad"><input class="field" type="text" id="domain" placeholder="example.org" value="{{H(host)}}" autocapitalize="off" autocomplete="off" spellcheck="false" aria-describedby="domainhelp"></div>
              <p class="foot" id="domainhelp">The tunnel will be available at <span id="tunnelhost">udriver.your-domain</span>.</p></section>
            <section><h2 class="sec-h">Unity project</h2>
              <div class="group">
                <div class="row"><span class="path" id="pathview"></span><button class="btn tinted" type="button" id="pick">Choose…</button></div>
                <div class="row" id="manual" hidden><label class="sr" for="project">Unity project folder</label><input class="field mono" type="text" id="project" placeholder="/Users/you/Projects/MyGame" value="{{H(project)}}" autocapitalize="off" autocomplete="off" spellcheck="false"></div>
              </div>
              <p class="foot err" id="pickmsg" hidden>No folder was chosen. You can type the path below instead.</p>
              <p class="foot">The folder that contains Assets and Packages. The driver is copied into its Packages folder. <button class="linkbtn" type="button" id="typepath">Type a path instead</button></p></section>
            <div class="actions"><button class="btn primary" type="button" id="go">Install</button></div>
            <section id="progress" hidden><h2 class="sec-h">Installing</h2><div class="group" id="log" aria-live="polite"></div><p class="foot err" id="err" role="alert" hidden></p></section>
            </main>
            <script>
            // Values from the server are always inserted as text, never as HTML.
            const csrf="{{LocalCsrf}}", $=id=>document.getElementById(id);
            const post=(u,d)=>fetch(u,{method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},body:new URLSearchParams({csrf,...d})});
            const el=(tag,cls,text)=>{ const e=document.createElement(tag); if(cls) e.className=cls; if(text!=null) e.textContent=text; return e; };
            const domain=$("domain"), project=$("project"), go=$("go");
            const showHost=()=>{ const d=domain.value.trim().toLowerCase().replace(/^https?:\/\//,"").replace(/\/.*$/,""); $("tunnelhost").textContent=d?(d.startsWith("udriver.")?d:"udriver."+d):"udriver.your-domain"; };
            const showPath=()=>{ const p=project.value.trim().replace(/\/+$/,""), v=$("pathview"); v.title=p;
              if(!p){ v.replaceChildren(el("span","ph","No folder chosen")); return; }
              const i=p.lastIndexOf("/"); v.replaceChildren(el("span","h",p.slice(0,i+1)),el("span","t",p.slice(i+1))); };
            const manual=()=>{ $("manual").hidden=false; $("typepath").hidden=true; project.focus(); };
            domain.addEventListener("input",showHost); project.addEventListener("input",showPath); showHost(); showPath();
            $("typepath").addEventListener("click",manual);
            $("pick").addEventListener("click",async()=>{
              const b=$("pick"); b.disabled=true; let r={};
              try{ r=await (await post("/panel/choose-folder",{})).json(); }catch(e){}
              b.disabled=false;
              if(r.path){ project.value=r.path; showPath(); $("pickmsg").hidden=true; } else { $("pickmsg").hidden=false; manual(); }
            });
            const step=(icon,text)=>{ const r=el("div","row"); r.append(icon,el("span","k",text)); return r; };
            const fail=msg=>{ $("err").textContent="Installation stopped: "+msg; $("err").hidden=false; go.disabled=false; go.textContent="Try Again"; };
            go.addEventListener("click",async()=>{
              go.disabled=true; go.textContent="Installing…"; $("progress").hidden=false; $("err").hidden=true;
              $("log").replaceChildren(step(el("span","spin"),"Starting…"));
              try{ await post("/panel/setup",{domain:domain.value,project:project.value}); }catch(e){ fail("the server did not answer."); return; }
              const t=setInterval(async()=>{
                let s; try{ s=await (await fetch("/panel/progress")).json(); }catch(e){ return; }
                const n=s.lines.length;
                if(n) $("log").replaceChildren(...s.lines.map((x,i)=>step(i<n-1||s.done?el("span","ck"):s.error?el("span","xk","!"):el("span","spin"),x)));
                if(s.error){ clearInterval(t); if(!n) $("log").replaceChildren(step(el("span","xk","!"),"Could not start")); fail(s.error); }
                if(s.done){ clearInterval(t); go.textContent="Opening the control panel…"; const w=setInterval(async()=>{ try{ const r=await fetch("/panel/status.json"); if(r.ok){ clearInterval(w); location.href="/panel"; } }catch(e){} },1500); }
              },1000);
            });
            </script>
            """, "Set up UDriver");
    }

    static string StatusPage(bool local, string csrf)
    {
        string token = $"<input type=\"hidden\" name=\"csrf\" value=\"{csrf}\">";
        string Act(string action, string label, string cls = "") =>
            $"<form method=\"post\" action=\"{action}\">{token}<button class=\"act {cls}\" type=\"submit\">{label}</button></form>";
        string secret = local
            ? $"""
              <div class="row"><div class="k">Owner password<code id="pwmask" aria-label="Password hidden">••••••••••••</code><code id="pw" hidden>{H(_password)}</code></div>
                <button class="btn tinted" type="button" id="pwbtn" aria-pressed="false">Show</button><button class="btn tinted" type="button" data-copy="pw">Copy</button></div>
              """
            : "";
        string localOnly = local
            ? $"""
              <section><h2 class="sec-h">This computer</h2><div class="group">
                {Act("/panel/tunnel", "Restart tunnel")}
                <a class="act nav" href="/panel?setup=1">Change domain or project<span class="chev" aria-hidden="true">›</span></a>
              </div><p class="foot">These settings are only available on the computer running UDriver.</p></section>
              """
            : $"""<section><div class="group">{Act("/panel/logout", "Sign Out")}</div></section>""";
        return Shell($$"""
            <header class="bar"><div class="bar-in"><span class="brand">{{Icon}}UDriver</span><span class="pill" id="pill" role="status"><span class="st"><i class="dot ok"></i>Running</span></span></div></header>
            <main>
            <h1 class="title">Control Panel</h1>
            <p class="subtitle">{{(local ? "On this computer" : "On your domain")}}<span id="up"></span></p>
            <section><h2 class="sec-h">Connect Claude</h2><div class="group">
              <div class="row"><div class="k">Connector address<code id="addr">{{H(_public)}}<wbr>/mcp</code></div><button class="btn tinted" type="button" data-copy="addr">Copy</button></div>
              {{secret}}
            </div><p class="foot">In Claude, add a custom connector with this address. It will ask for the owner password once.</p></section>
            <section><h2 class="sec-h">Status</h2><div class="group">
              <div class="row"><span class="k">Cloudflare tunnel</span><span class="v" id="tunnel">…</span></div>
              <div class="row"><span class="k">Reachable from the internet</span><span class="v" id="public">…</span></div>
              <div class="row"><span class="k">Unity Editor</span><span class="v" id="editor">…</span></div>
              <div class="row"><span class="k">Game (Play Mode or build)</span><span class="v" id="game">…</span></div>
              <div class="row"><span class="k">Claude</span><span class="v" id="claude">…</span></div>
              <div class="row"><span class="k">Last action</span><span class="v" id="last">…</span></div>
            </div><p class="foot">Updates automatically every few seconds.</p><p class="foot" id="tunnelnote" hidden></p></section>
            <section><h2 class="sec-h">Access</h2><div class="group">
              <form method="post" action="/panel/readonly" id="roform">{{token}}<input type="hidden" name="on" id="roon" value="{{(Program.ReadOnly ? "0" : "1")}}">
                <label class="row" for="ro"><span class="k">Observe only</span><input class="switch" type="checkbox" role="switch" id="ro"{{(Program.ReadOnly ? " checked" : "")}}></label></form>
              {{Act("/panel/revoke", "Disconnect Claude", "danger")}}
            </div><p class="foot">With Observe only on, Claude can see the screen and state but can't click or change anything. Disconnect signs Claude out; it will ask for the password again.</p></section>
            {{localOnly}}
            <section><div class="group"><form method="post" action="/panel/stop" id="stopform">{{token}}<button class="act danger center" type="submit">Stop Everything</button></form></div>
              <p class="foot">Turns off the server and the tunnel, same as closing the UDriver window. Claude can't reach Unity until you start it again.</p></section>
            </main>
            <dialog class="alert" id="stopdlg" aria-labelledby="stoptitle" aria-describedby="stoptext">
              <div class="alert-body"><h2 id="stoptitle">Stop everything?</h2><p id="stoptext">The server and the tunnel will turn off. Claude won't be able to reach Unity until you start UDriver again.</p></div>
              <div class="alert-btns"><button type="button" id="stopcancel" autofocus>Cancel</button><button type="button" class="danger" id="stopyes">Stop</button></div>
            </dialog>
            <script>
            // Values from the server are always inserted as text, never as HTML.
            const $=id=>document.getElementById(id);
            const st=(kind,t)=>{ const s=document.createElement("span"), d=document.createElement("i"); s.className="st"; d.className="dot "+kind; s.append(d,t); return s; };
            const set=(id,kind,t)=>$(id).replaceChildren(st(kind,t));
            async function copy(btn,text){
              try{ await navigator.clipboard.writeText(text); }
              catch(e){ const a=document.createElement("textarea"); a.value=text; a.readOnly=true; a.className="offscreen"; document.body.append(a); a.select(); document.execCommand("copy"); a.remove(); }
              btn.textContent="Copied"; setTimeout(()=>{ btn.textContent="Copy"; },1500);
            }
            document.querySelectorAll("[data-copy]").forEach(b=>b.addEventListener("click",()=>copy(b,$(b.dataset.copy).textContent)));
            const pw=$("pw");
            if(pw) $("pwbtn").addEventListener("click",e=>{ const show=pw.hidden; pw.hidden=!show; $("pwmask").hidden=show; e.currentTarget.textContent=show?"Hide":"Show"; e.currentTarget.setAttribute("aria-pressed",String(show)); });
            const ro=$("ro"); let busy=false;
            ro.addEventListener("change",()=>{ busy=true; $("roon").value=ro.checked?"1":"0"; $("roform").submit(); });
            const dlg=$("stopdlg");
            $("stopform").addEventListener("submit",e=>{ e.preventDefault(); dlg.showModal(); });
            $("stopcancel").addEventListener("click",()=>dlg.close());
            $("stopyes").addEventListener("click",()=>{ dlg.close(); $("stopform").submit(); });
            const dur=s=>s<60?s+" s":s<3600?Math.floor(s/60)+" min":Math.floor(s/3600)+" h";
            async function tick(){
              let s; try{ const r=await fetch("/panel/status.json"); if(r.status==401){ location.reload(); return; } s=await r.json(); }
              catch(e){ set("pill","bad","Not responding"); return; }
              set("pill","ok","Running");
              $("up").textContent=" · running for "+dur(s.upSec);
              set("tunnel", ...(!s.tunnelAlive?["bad","Not running"]:s.tunnelConnections>0?["ok","Connected"]:["warn","Connecting…"]));
              $("tunnelnote").hidden=!s.tunnelNote; $("tunnelnote").textContent=s.tunnelNote?"Tunnel message: "+s.tunnelNote:"";
              set("public", ...(s.publicOk===null?["","Checking…"]:s.publicOk?["ok","Yes"]:["bad","No"]));
              set("editor", ...(s.editor?["ok","Open"]:["bad","Not found"]));
              set("game", ...(s.game?["ok","Running"]:["","Not running"]));
              set("claude", ...(s.sessions>0?["ok","Signed in"]:["","Not connected"]));
              $("last").textContent=s.lastTool?s.lastTool+", "+dur(s.lastAgoSec)+" ago · "+s.calls+" total":"Nothing yet";
              if(!busy) ro.checked=s.readOnly;
            }
            tick(); setInterval(tick,3000);
            </script>
            """);
    }
}

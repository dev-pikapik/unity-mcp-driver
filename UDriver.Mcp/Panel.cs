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

    /// <summary>Every HTML page goes out through here: no framing, and only our own &lt;script&gt; blocks run (a fresh nonce per response).</summary>
    static Task Html(HttpContext ctx, string html, int status = 200, string formAction = "'self'")
    {
        string nonce = Rand(18);
        return Write(ctx, status, "text/html; charset=utf-8", html.Replace("<script>", $"<script nonce=\"{nonce}\">"), r =>
        {
            r.Headers["X-Frame-Options"] = "DENY";
            r.Headers["Content-Security-Policy"] = $"default-src 'self'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; frame-ancestors 'none'; form-action {formAction}; base-uri 'none'";
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

    static string Shell(string body) => """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>UDriver</title>
        <style>
        body{font-family:system-ui,-apple-system,sans-serif;background:#15171b;color:#e9e9ea;margin:0;padding:32px 16px;display:flex;justify-content:center}
        main{max-width:560px;width:100%} h1{font-size:22px;margin:0 0 4px} .sub{color:#9aa0a6;margin:0 0 24px;font-size:14px}
        .card{background:#1f2228;border:1px solid #30343c;border-radius:12px;padding:16px 18px;margin-bottom:14px}
        .row{display:flex;justify-content:space-between;gap:12px;padding:7px 0;border-bottom:1px solid #2a2e35;font-size:15px} .row:last-child{border-bottom:0}
        .k{color:#9aa0a6} .ok{color:#7bd88f} .bad{color:#ff8a80} .wait{color:#e6c07b}
        .big{font-size:18px;font-weight:600;display:flex;align-items:center;gap:10px} .dot{width:12px;height:12px;border-radius:50%;background:#7bd88f;display:inline-block}
        input[type=text],input[type=password]{width:100%;padding:11px;font-size:16px;border-radius:8px;border:1px solid #4a4f58;background:#16181c;color:#fff;box-sizing:border-box;margin-top:6px}
        label{display:block;margin-bottom:14px;font-size:14px;color:#c8ccd1}
        button{padding:11px 16px;font-size:15px;border-radius:8px;border:0;background:#3a3f48;color:#fff;cursor:pointer} button.main{background:#d97757} button.stop{background:#c0392b;width:100%;font-size:16px;padding:13px}
        button:disabled{opacity:.5} code{background:#16181c;padding:3px 7px;border-radius:6px;font-size:14px;word-break:break-all} .m{color:#9aa0a6;font-size:13px}
        form.in{display:inline} ul{padding-left:18px;margin:8px 0} li{margin:4px 0} a{color:#8ab4f8}
        </style></head><body><main>
        """ + body + "</main></body></html>";

    static string LoginPage(string? error) => Shell($"""
        <h1>UDriver</h1><p class="sub">Control panel. Sign-in for the owner only.</p>
        <div class="card"><form method="post" action="/panel/login">
        {(error != null ? $"<p class=\"bad\">{H(error)}</p>" : "")}
        <label>Owner password<input type="password" name="password" autofocus autocomplete="current-password"></label>
        <button class="main" type="submit">Sign in</button></form></div>
        """);

    static string StoppedPage() => Shell("""
        <h1>UDriver stopped</h1><p class="sub">The server and the tunnel are off. Claude can no longer reach Unity.</p>
        <div class="card"><p>To start it again, double-click <code>UDriver.command</code> in the <code>udriver</code> folder on the computer with Unity.</p>
        <p class="m">This page on your domain will not open while the server is off. That is expected.</p></div>
        """);

    static string SetupPage()
    {
        string host = GetSetting("UDRIVER_PUBLIC_URL")?.Replace("https://", "") ?? "";
        string project = "";
        try { project = File.ReadAllText(Path.Combine(HomeDir, "project")).Trim(); } catch { }
        return Shell($$"""
            <h1>UDriver setup</h1><p class="sub">Fill in two fields, the rest happens automatically.</p>
            <div class="card" id="formcard">
            <label>Your Cloudflare domain<input type="text" id="domain" placeholder="example.org" value="{{H(host)}}" autocapitalize="off" spellcheck="false"></label>
            <label>Unity project folder
              <input type="text" id="project" placeholder="/Users/you/Projects/MyGame" value="{{H(project)}}" spellcheck="false"></label>
            <p><button type="button" id="pick">Choose folder…</button></p>
            <p class="m">The connector address will be <code>https://udriver.your-domain/mcp</code>. The driver package will be copied into the project's Packages folder.</p>
            <button class="main" type="button" id="go">Install</button>
            </div>
            <div class="card" id="progress" style="display:none"><b>Installation progress</b><ul id="log"></ul><p id="err" class="bad"></p></div>
            <script>
            const csrf="{{LocalCsrf}}";
            const post=(u,d)=>fetch(u,{method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},body:new URLSearchParams({csrf,...d})});
            document.getElementById("pick").onclick=async()=>{const r=await (await post("/panel/choose-folder",{})).json(); if(r.path) document.getElementById("project").value=r.path; else alert("Could not open the folder picker. Paste the path manually.");};
            document.getElementById("go").onclick=async()=>{
              document.getElementById("go").disabled=true; document.getElementById("progress").style.display="block"; document.getElementById("err").textContent="";
              await post("/panel/setup",{domain:document.getElementById("domain").value,project:document.getElementById("project").value});
              const t=setInterval(async()=>{
                let s; try{ s=await (await fetch("/panel/progress")).json(); }catch(e){ return; }
                document.getElementById("log").replaceChildren(...s.lines.map(x=>{ const li=document.createElement("li"); li.textContent=x; return li; }));
                if(s.error){ clearInterval(t); document.getElementById("err").textContent="Error: "+s.error; document.getElementById("go").disabled=false; }
                if(s.done){ clearInterval(t); const w=setInterval(async()=>{ try{ const r=await fetch("/panel/status.json"); if(r.ok){ clearInterval(w); location.href="/panel"; } }catch(e){} },1500); }
              },1000);
            };
            </script>
            """);
    }

    static string StatusPage(bool local, string csrf)
    {
        string F(string action, string label, string cls = "", string extra = "") =>
            $"<form class=\"in\" method=\"post\" action=\"{action}\"><input type=\"hidden\" name=\"csrf\" value=\"{csrf}\">{extra}<button class=\"{cls}\" type=\"submit\">{label}</button></form>";
        string secret = local
            ? $"<div class=\"row\"><span class=\"k\">Owner password</span><span><code id=\"pw\" style=\"display:none\">{H(_password)}</code> <button type=\"button\" id=\"pwbtn\">show</button></span></div>"
            : "";
        string localOnly = local
            ? $"<div class=\"card\"><p class=\"m\" style=\"margin-top:0\">Only on this computer</p>{F("/panel/tunnel", "Restart tunnel")} <a href=\"/panel?setup=1\"><button type=\"button\">Change domain or project</button></a></div>"
            : $"<div class=\"card\">{F("/panel/logout", "Sign out of panel")}</div>";
        return Shell($$"""
            <h1>UDriver</h1><p class="sub">{{(local ? "Panel on this computer" : "Panel on your domain")}}. Updates automatically.</p>
            <div class="card"><div class="big"><span class="dot"></span>Server is running <span class="m" id="up"></span></div>
              <p class="m">While it runs, Claude can reach Unity. To stop it, use the button below or close the launch window on the computer.</p></div>
            <div class="card">
              <div class="row"><span class="k">Connector address</span><code>{{H(_public)}}/mcp</code></div>
              {{secret}}
              <div class="row"><span class="k">Cloudflare tunnel</span><span id="tunnel">…</span></div>
              <div class="row"><span class="k">Reachable from the internet</span><span id="public">…</span></div>
              <div class="row"><span class="k">Unity Editor</span><span id="editor">…</span></div>
              <div class="row"><span class="k">Game (Play Mode or build)</span><span id="game">…</span></div>
              <div class="row"><span class="k">Claude</span><span id="claude">…</span></div>
              <div class="row"><span class="k">Last action</span><span id="last">…</span></div>
              <div class="row"><span class="k">Mode</span><span id="mode">…</span></div>
            </div>
            <div class="card">
              {{F("/panel/readonly", "Turn on “view only”", "", "<input type=\"hidden\" name=\"on\" value=\"1\">")}}
              {{F("/panel/readonly", "Allow actions", "", "<input type=\"hidden\" name=\"on\" value=\"0\">")}}
              {{F("/panel/revoke", "Disconnect Claude (reset sign-in)")}}
              <p class="m">“View only”: Claude can see the screen and state but cannot click or change anything. After a sign-in reset, Claude will ask for the password again.</p>
            </div>
            {{localOnly}}
            <form method="post" action="/panel/stop" id="stopform"><input type="hidden" name="csrf" value="{{csrf}}"><button class="stop" type="submit">Stop everything</button></form>
            <script>
            // Values from the server are always inserted as text, never as HTML.
            const span=(cls,t)=>{ const e=document.createElement("span"); e.className=cls; e.textContent=t; return e; };
            const set=(id,...parts)=>document.getElementById(id).replaceChildren(...parts), yes=t=>span("ok",t), no=t=>span("bad",t), wait=t=>span("wait",t);
            const pw=document.getElementById("pw");
            if(pw) document.getElementById("pwbtn").addEventListener("click",()=>{ pw.style.display=pw.style.display=="none"?"inline":"none"; });
            document.getElementById("stopform").addEventListener("submit",e=>{ if(!confirm("Stop the server and the tunnel?")) e.preventDefault(); });
            const ago=s=>s<60?s+"s ago":s<3600?Math.floor(s/60)+" min ago":Math.floor(s/3600)+" h ago";
            async function tick(){
              let s; try{ const r=await fetch("/panel/status.json"); if(r.status==401){ location.reload(); return; } s=await r.json(); }
              catch(e){ const d=span("dot",""); d.style.background="#ff8a80"; document.querySelector(".big").replaceChildren(d,"Server is not responding"); return; }
              set("up","· "+ago(s.upSec).replace(" ago",""));
              set("tunnel", ...(!s.tunnelAlive?[no("not running")]:s.tunnelConnections>0?[yes("connected ("+s.tunnelConnections+")")]:[wait("connecting…")].concat(s.tunnelNote?[Object.assign(document.createElement("div"),{className:"m",textContent:s.tunnelNote})]:[])));
              set("public", s.publicOk===null?wait("checking…"):s.publicOk?yes("yes"):no("no"));
              set("editor", s.editor?yes("open"):no("not found"));
              set("game", s.game?yes("running"):span("k","not running"));
              set("claude", s.sessions>0?yes("signed in"):span("k","not connected"));
              set("last", s.lastTool?(s.lastTool+", "+ago(s.lastAgoSec)+" · total "+s.calls):span("k","nothing yet"));
              set("mode", s.readOnly?wait("view only"):"full access");
            }
            tick(); setInterval(tick,3000);
            </script>
            """);
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static partial class HttpMode
{
    sealed class Client { public string Name { get; set; } = ""; public List<string> RedirectUris { get; set; } = new(); public bool Used { get; set; } public bool Local { get; set; } }   // Used: has obtained a token; Local: a key from the panel, valid on this computer only
    sealed class Code { public string ClientId = "", RedirectUri = "", Challenge = ""; public DateTime Expires; }
    sealed class Pending { public string Client = "", App = "", Dest = "", Ip = "", Redirect = "", Challenge = ""; public int Check; public DateTime Expires; public bool? Ok; }
    sealed class Store
    {
        public Dictionary<string, Client> Clients { get; set; } = new();
        public Dictionary<string, DateTime> Access { get; set; } = new();          // sha256(token) -> expiry
        public Dictionary<string, string> AccessClient { get; set; } = new();      // sha256(token) -> client id
        public Dictionary<string, string> RefreshClient { get; set; } = new();     // sha256(token) -> client id
        public Dictionary<string, DateTime> Refresh { get; set; } = new();
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, Code> Codes = new();
    static Store _store = new();
    static string _public = "", _urlSecret = "", _storeFile = "";
    static int _port;

    // Wrong codes are counted per address and in total; over the limit the answer is 429 before any hashing.
    static readonly Dictionary<string, (int Fails, DateTime Seen)> Failures = new();
    static int _globalFails; static DateTime _globalWindowStart;
    static readonly SemaphoreSlim Checking = new(1, 1);
    const int IpLimit = 10, AllLimit = 50;
    const string TooMany = "Too many wrong attempts. Wait 10 minutes, then try again.";
    const string CheckBusy = "Another sign-in is being checked. Try again in a moment.";
    const string Expired = "This request has expired. Start connecting again from the app.";
    static readonly Dictionary<string, Program.Session> Agents = new();
    static readonly Dictionary<string, (DateTime Until, string Client, JsonObject Reply)> Rotated = new();   // sha256(old refresh token) -> its replacement
    static readonly Queue<(DateTime At, string Ip)> Registrations = new();
    static readonly Dictionary<string, Pending> Waiting = new();   // sign-ins the owner can confirm in the panel instead of typing a code
    static readonly HashSet<string> BadSchemes = new() { "javascript", "data", "file", "vbscript", "about", "blob", "ws", "wss", "ftp",
        "ms-msdt", "search-ms", "search", "ms-officecmd", "ms-word", "ms-excel", "ms-powerpoint", "ms-settings", "ms-appinstaller" };

    static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";
    static string Rand(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    public static async Task Run(int port)
    {
        _public = Env("UDRIVER_PUBLIC_URL").TrimEnd('/');
        try { if (GetSetting("UDRIVER_OWNER_PASSWORD") != null) SetSetting("UDRIVER_OWNER_PASSWORD", null); }
        catch (Exception e) { Console.Error.WriteLine("UDriver: could not remove the old password from " + EnvFile + ": " + e.Message); }
        Program.Solo.Label = "owner (panel)";
        _urlSecret = Env("UDRIVER_URL_SECRET");
        bool secretMode = Env("UDRIVER_AUTH") == "secret-url";
        _panel = Env("UDRIVER_PANEL") == "1";
        _port = port;
        if (_panel) _public = RemoteSaved() ? (GetSetting("UDRIVER_PUBLIC_URL") ?? "").TrimEnd('/') : "";
        if (secretMode ? _urlSecret.Length < 32 : !_panel)
        {
            Console.Error.WriteLine(secretMode
                ? "UDRIVER_URL_SECRET must be at least 32 characters."
                : "HTTP mode needs UDRIVER_PANEL=1 (agents sign in with one-time codes from the panel) or UDRIVER_AUTH=secret-url.");
            Environment.Exit(2);
        }
        _storeFile = Path.Combine(Program.HomeDir, "oauth.json");
        try { _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_storeFile)) ?? new Store(); } catch { _store = new Store(); }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            o.Limits.MaxConcurrentConnections = 200;
            o.AddServerHeader = false;
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { KillTunnel(); Program.StopLaunched(); if (Program.IsHub) try { File.Delete(Program.HubFile); } catch { } };
        var app = builder.Build();
        // The launcher opens the panel only when the process on the port is the one named here.
        if (_panel) app.Lifetime.ApplicationStarted.Register(() =>
        {
            Program.WritePrivate(PidFile, Environment.ProcessId.ToString());
            Program.WritePrivate(Program.HubFile, port.ToString());
            Program.IsHub = true;
        });
        app.Run(async ctx =>
        {
            // The apps use their own server only when it says it is this version or newer.
            ctx.Response.Headers["X-UDriver-Version"] = UDriver.ToolManifest.Version;
            try { await Handle(ctx, secretMode); }
            catch (Exception e) { try { if (!ctx.Response.HasStarted) await Write(ctx, 500, "application/json", new JsonObject { ["error"] = e.GetType().Name }.ToJsonString()); } catch { } }
        });
        if (_panel)
        {
            StartPanel();
            Console.Error.WriteLine($"UDriver is running. Its panel opens in your browser: http://127.0.0.1:{port}/panel" + (_public.Length > 0 ? $"\nFrom another device: {_public}/panel" : "")
                + "\n\nKeep this window open. Closing it stops UDriver.");
        }
        else Console.Error.WriteLine($"UDriver MCP (HTTP) on http://127.0.0.1:{port}  auth: {(secretMode ? "secret URL" : "OAuth")}  public: {_public}");
        if (secretMode) NewLinkCode();
        await app.RunAsync();
    }

    static void Save()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var k in _store.Access.Where(x => x.Value < now).Select(x => x.Key).ToList()) { _store.Access.Remove(k); _store.AccessClient.Remove(k); }
            foreach (var k in _store.Refresh.Where(x => x.Value < now).Select(x => x.Key).ToList()) { _store.Refresh.Remove(k); _store.RefreshClient.Remove(k); }
            Program.WritePrivate(_storeFile, JsonSerializer.Serialize(_store));
        }
        catch { }
    }

    static Task Write(HttpContext ctx, int status, string type, string body, Action<HttpResponse>? extra = null) => Write(ctx, status, type, Encoding.UTF8.GetBytes(body), extra);

    static async Task Write(HttpContext ctx, int status, string type, byte[] bytes, Action<HttpResponse>? extra = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        extra?.Invoke(ctx.Response);
        ctx.Response.ContentLength = bytes.Length;
        if (bytes.Length > 0) await ctx.Response.Body.WriteAsync(bytes);
    }

    static Task Json(HttpContext ctx, int status, JsonNode body) => Write(ctx, status, "application/json", body.ToJsonString());
    static Task OAuthError(HttpContext ctx, int status, string error, string description) =>
        Json(ctx, status, new JsonObject { ["error"] = error, ["error_description"] = description });

    /// <summary>The visitor's address: Cloudflare's header for requests that came through the tunnel, else the connection.</summary>
    static string ClientIp(HttpContext ctx)
    {
        string ip = ctx.Request.Headers["Cf-Connecting-Ip"].ToString();
        bool tunnel = Uri.TryCreate(_public, UriKind.Absolute, out var pu) && ctx.Request.Host.Value == pu.Authority;
        return tunnel && ip.Length is > 0 and <= 64 ? ip : ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    static int StatusOf(string? error) => error is TooMany or CheckBusy ? 429 : 200;

    /// <summary>Runs one check at a time; refuses without running it once this address or everyone together failed too often.</summary>
    static async Task<string?> Slow(HttpContext ctx, Func<bool> check, string error)
    {
        bool local = IsLocal(ctx);
        string ip = ClientIp(ctx);
        var window = TimeSpan.FromMinutes(10);
        bool Over()
        {
            if (local) return false;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (now - _globalWindowStart > window) { _globalWindowStart = now; _globalFails = 0; }
                var f = Failures.GetValueOrDefault(ip);
                return _globalFails >= AllLimit || now - f.Seen <= window && f.Fails >= IpLimit;
            }
        }
        if (Over()) return TooMany;
        if (!await Checking.WaitAsync(TimeSpan.FromSeconds(10))) return CheckBusy;
        try
        {
            if (Over()) return TooMany;
            bool ok = check();
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (ok) { Failures.Remove(ip); return null; }
                if (!local)
                {
                    var f = Failures.GetValueOrDefault(ip);
                    if (now - f.Seen > window) f.Fails = 0;
                    f.Seen = now; f.Fails++;
                    if (Failures.Count >= 1000 && !Failures.ContainsKey(ip))
                        foreach (var k in Failures.Where(x => now - x.Value.Seen > window).Select(x => x.Key).ToList()) Failures.Remove(k);
                    if (Failures.Count < 1000 || Failures.ContainsKey(ip)) Failures[ip] = f;
                    _globalFails++;
                }
            }
            await Task.Delay(700);
            return error;
        }
        finally { Checking.Release(); }
    }

    static async Task<Dictionary<string, string>> Form(HttpContext ctx)
    {
        using var rd = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        string body = await rd.ReadToEndAsync();
        var d = new Dictionary<string, string>();
        if ((ctx.Request.ContentType ?? "").StartsWith("application/json"))
        {
            if (JsonNode.Parse(body) is JsonObject o) foreach (var kv in o) d[kv.Key] = kv.Value?.ToString() ?? "";
            return d;
        }
        var q = HttpUtility.ParseQueryString(body);
        foreach (string? k in q.AllKeys) if (k != null) d[k] = q[k] ?? "";
        return d;
    }

    static async Task Handle(HttpContext ctx, bool secretMode)
    {
        string path = ctx.Request.Path.Value ?? "/", verb = ctx.Request.Method;

        // Only our own names; anything else is DNS rebinding.
        string host = ctx.Request.Host.Value ?? "";
        string publicHost = Uri.TryCreate(_public, UriKind.Absolute, out var pu) ? pu.Authority : "";
        if (host != publicHost && host != "127.0.0.1:" + _port && host != "localhost:" + _port)
        { await OAuthError(ctx, 421, "misdirected_request", "unexpected Host"); return; }

        if (_panel && !secretMode && (path == "/" || path == "/panel" || path.StartsWith("/panel/")))
        { await Panel(ctx, path, verb, IsLocal(ctx)); return; }
        if (_panel && _public.Length == 0 && !(path == "/mcp" && IsLocal(ctx))) { await Write(ctx, 503, "text/plain; charset=utf-8", "Remote access to UDriver is off"); return; }

        // The sign-in form is posted only by its own page; everything else answers to Bearer tokens, so CORS without credentials.
        string origin = ctx.Request.Headers["Origin"].ToString();
        if (path == "/authorize" && verb == "POST" && !OwnOrigin(ctx, IsLocal(ctx)))
        { await OAuthError(ctx, 403, "forbidden", "origin not allowed"); return; }
        if (origin.Length > 0 && origin != _public && (path is "/mcp" or "/token" or "/register" || path.StartsWith("/.well-known/")))
        {
            var h = ctx.Response.Headers;
            h["Access-Control-Allow-Origin"] = origin;
            h["Vary"] = "Origin";
            h["Access-Control-Expose-Headers"] = "WWW-Authenticate, Mcp-Session-Id";
            if (verb == "OPTIONS")
            {
                h["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                h["Access-Control-Allow-Headers"] = "Authorization, Content-Type, Accept, Mcp-Protocol-Version, Mcp-Session-Id, Last-Event-ID";
                h["Access-Control-Max-Age"] = "600";
                ctx.Response.StatusCode = 204;
                return;
            }
        }

        if (secretMode)
        {
            if (!Same(path, "/mcp/" + _urlSecret)) { await Write(ctx, 404, "text/plain", "not found"); return; }
            Program.Current.Value = Agent(Program.LinkClient, IsLocal(ctx));
            if (!File.Exists(LinkUsedFile)) Program.WritePrivate(LinkUsedFile, "");
            await Mcp(ctx, true); return;
        }

        if (path.StartsWith("/.well-known/oauth-protected-resource"))
        {
            await Json(ctx, 200, new JsonObject
            {
                ["resource"] = _public + "/mcp", ["authorization_servers"] = new JsonArray(_public), ["bearer_methods_supported"] = new JsonArray("header"),
            });
        }
        else if (path.StartsWith("/.well-known/oauth-authorization-server") || path.StartsWith("/.well-known/openid-configuration"))
        {
            await Json(ctx, 200, new JsonObject
            {
                ["issuer"] = _public, ["authorization_endpoint"] = _public + "/authorize", ["token_endpoint"] = _public + "/token",
                ["registration_endpoint"] = _public + "/register", ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"), ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none", "client_secret_post", "client_secret_basic"),
            });
        }
        else if (path == "/register" && verb == "POST") await Register(ctx);
        else if (path == "/authorize" && (verb == "GET" || verb == "POST")) await Authorize(ctx);
        else if (path == "/authorize/status" && verb == "GET") await Json(ctx, 200, new JsonObject { ["state"] = PendingState(ctx.Request.Query["id"].ToString()) });
        else if (path == "/token" && verb == "POST") await Token(ctx);
        else if (path == "/mcp")
        {
            string auth = ctx.Request.Headers["Authorization"].ToString();
            string? clientId = null;
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                lock (Gate)
                {
                    string key = Hash(auth.Substring(7).Trim());
                    if (_store.Access.TryGetValue(key, out var exp) && exp > DateTime.UtcNow && _store.AccessClient.TryGetValue(key, out var id)
                        && _store.Clients.TryGetValue(id, out var c) && (!c.Local || IsLocal(ctx))) clientId = id;
                }
            if (clientId == null)
            {
                await Write(ctx, 401, "application/json", "{\"error\":\"invalid_token\"}",
                    r => r.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{_public}/.well-known/oauth-protected-resource\"");
                return;
            }
            Program.Current.Value = Agent(clientId, IsLocal(ctx));
            await Mcp(ctx);
        }
        else await Write(ctx, 404, "text/plain", "not found");
    }

    // ---------------- MCP endpoint ----------------

    static Program.Session Agent(string clientId, bool local)
    {
        Program.Session? s;
        lock (Gate)
        {
            if (!Agents.TryGetValue(clientId, out s)) Agents[clientId] = s = new Program.Session { Client = clientId };
            s.Local = local;
        }
        lock (s) if (s.Slot == 0 && !s.Off) Registry.Claim(s);
        return s;
    }

    static async Task Mcp(HttpContext ctx, bool link = false)
    {
        if (ctx.Request.Method != "POST") { await Write(ctx, 405, "text/plain", "POST only", r => r.Headers["Allow"] = "POST"); return; }
        string body;
        using (var rd = new StreamReader(ctx.Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        JsonNode? msg;
        try { msg = JsonNode.Parse(body); }
        catch { await Json(ctx, 400, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "parse error" } }); return; }

        bool unlocked = true;
        if (link)
        {
            string sid = ctx.Request.Headers["Mcp-Session-Id"].ToString();
            if ((string?)(msg as JsonObject)?["method"] == "initialize")
            {
                sid = Rand(24);
                lock (Gate)
                {
                    if (LinkSessions.Count >= 256) foreach (var k in LinkSessions.Where(x => !x.Value).Select(x => x.Key).ToList()) LinkSessions.Remove(k);
                    LinkSessions[sid] = false;
                }
                ctx.Response.Headers["Mcp-Session-Id"] = sid;
                ShowLinkCode();
            }
            bool known;
            lock (Gate) known = LinkSessions.TryGetValue(sid, out unlocked);
            if (!known) { await Write(ctx, 404, "text/plain", "Unknown session: start a new one with initialize."); return; }
        }

        async Task<JsonObject?> One(JsonNode? m)
        {
            if (!unlocked && Unlock(ctx, m) is { } early) return early;
            var work = Program.HandleMessage(m);
            if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(90))) == work) return await work;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = m?["id"]?.DeepClone(), ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The call is still running after 90 s and was detached. Check the result with status / events_query." }), ["isError"] = true,
            } };
        }

        if (msg is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var m in batch) { var r = await One(m); if (r != null) replies.Add(r); }
            if (replies.Count == 0) await Write(ctx, 202, "application/json", ""); else await Json(ctx, 200, replies);
            return;
        }
        var reply = await One(msg);
        if (!unlocked && reply?["result"]?["tools"] is JsonArray listed) listed.Add(UnlockTool());
        if (reply == null) await Write(ctx, 202, "application/json", ""); else await Json(ctx, 200, reply);
    }

    // ---------------- secret link: the owner's code ----------------

    static readonly Dictionary<string, bool> LinkSessions = new();
    static string _linkCode = "";
    static int _linkMisses;
    static DateTime _linkShown;

    /// <summary>Shown only where the server runs: the link alone is not enough.</summary>
    static void NewLinkCode()
    {
        lock (Gate) { _linkCode = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8"); _linkMisses = 0; _linkShown = default; }
        ShowLinkCode();
    }

    static void ShowLinkCode()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _linkShown < TimeSpan.FromSeconds(30)) return;
            _linkShown = DateTime.UtcNow;
        }
        Console.Error.WriteLine($"Code for apps that open the secret link: {_linkCode}. Give it only to your own agent.");
    }

    static JsonObject UnlockTool() => new()
    {
        ["name"] = "unlock",
        ["description"] = "This link also needs the owner's code. Ask the owner for the 8-digit code shown where UDriver runs, then call unlock with it once.",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["code"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("code") },
    };

    /// <summary>Until the code is given, the link answers only unlock; five wrong codes make a new one.</summary>
    static JsonObject? Unlock(HttpContext ctx, JsonNode? m)
    {
        if ((string?)(m as JsonObject)?["method"] != "tools/call") return null;
        JsonObject Text(string text, bool error) => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = m?["id"]?.DeepClone(),
            ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = error },
        };
        if ((string?)m?["params"]?["name"] != "unlock")
            return Text("This link also needs the owner's code. Ask the owner for the 8-digit code shown where UDriver runs, then call unlock with it.", true);
        string sid = ctx.Request.Headers["Mcp-Session-Id"].ToString(), code = m?["params"]?["arguments"]?["code"]?.ToString().Trim() ?? "";
        bool ok, renew = false;
        lock (Gate)
        {
            ok = Same(code, _linkCode);
            if (ok) LinkSessions[sid] = true;
            else if (++_linkMisses >= 5) renew = true;
        }
        if (renew) NewLinkCode();
        return ok ? Text("Unlocked. All tools work in this session now.", false)
            : Text(renew ? "Wrong code. After five wrong codes UDriver made a new one; ask the owner for it." : "Wrong code. Ask the owner for the code shown where UDriver runs.", true);
    }

    // ---------------- OAuth ----------------

    static bool Loopback(string host) => host is "localhost" or "127.0.0.1" or "[::1]";

    /// <summary>Any https site, http on this computer only, or a native app's own scheme (RFC 8252) that a browser cannot run.</summary>
    static bool RedirectAllowed(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.UserInfo.Length > 0 || u.Fragment.Length > 0) return false;
        if (u.Scheme == "https") return u.Host.Length > 0;
        if (u.Scheme == "http") return Loopback(u.Host);
        return Regex.IsMatch(u.Scheme, "^[a-z][a-z0-9+.-]*$") && !BadSchemes.Contains(u.Scheme);
    }

    static string Dest(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Scheme is "https" or "http" ? u.IdnHost : u.Scheme + ":" : "";

    static bool RedirectMatches(Client c, string uri)
    {
        if (c.RedirectUris.Contains(uri)) return true;
        // Native apps may use a different loopback port than the one they registered.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != "http" || !Loopback(u.Host)) return false;
        return c.RedirectUris.Any(r => Uri.TryCreate(r, UriKind.Absolute, out var x) && x.Scheme == "http" && x.Host == u.Host && x.AbsolutePath == u.AbsolutePath);
    }

    static async Task Register(HttpContext ctx)
    {
        // Registering grants nothing: sign-in still needs the owner's one-time code or approval.
        JsonObject? req;
        using (var rd = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            try { req = JsonNode.Parse(await rd.ReadToEndAsync()) as JsonObject; } catch { req = null; }
        var all = (req?["redirect_uris"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
        // Some agent apps also send test callbacks: keep the allowed ones, drop the rest.
        var uris = all.Where(RedirectAllowed).ToList();
        if (uris.Count == 0 || all.Count > 10)
        { await OAuthError(ctx, 400, "invalid_redirect_uri", "redirect URIs must be https, http on localhost, or an app's own scheme"); return; }
        string id = "c_" + Rand(16), name = ((string?)req?["client_name"] ?? "MCP client");
        if (name.Length > 80) name = name.Substring(0, 80);
        string ip = ClientIp(ctx);
        var now = DateTime.UtcNow;
        string? evicted = null;
        lock (Gate)
        {
            // Rate-limited; at the cap the oldest never-used app goes first.
            while (Registrations.Count > 0 && Registrations.Peek().At < now.AddMinutes(-10)) Registrations.Dequeue();
            if (Registrations.Count >= 100 || Registrations.Count(x => x.Ip == ip) >= 10) id = "";
            else
            {
                Registrations.Enqueue((now, ip));
                if (_store.Clients.Count >= 200)
                    _store.Clients.Remove(evicted = _store.Clients.OrderBy(x => x.Value.Used).ThenBy(x => x.Value.Local).First().Key);
                _store.Clients[id] = new Client { Name = name, RedirectUris = uris };
                Save();
            }
        }
        if (evicted != null) Registry.ForgetClient(evicted);
        if (id.Length == 0) { await OAuthError(ctx, 429, "too_many_requests", "too many app registrations, try again in a few minutes"); return; }
        // A requested client secret is issued but never checked: PKCE and the one-time code protect sign-in.
        string method = (string?)req?["token_endpoint_auth_method"] ?? "none";
        if (method != "client_secret_post" && method != "client_secret_basic") method = "none";
        var reply = new JsonObject
        {
            ["client_id"] = id, ["client_name"] = name, ["redirect_uris"] = new JsonArray(uris.Select(u => (JsonNode)u!).ToArray()),
            ["token_endpoint_auth_method"] = method, ["grant_types"] = new JsonArray("authorization_code", "refresh_token"), ["response_types"] = new JsonArray("code"),
        };
        if (method != "none") { reply["client_secret"] = Rand(24); reply["client_secret_expires_at"] = 0; }
        await Json(ctx, 201, reply);
    }

    const string CodeField = """<input class="otc" type="text" id="otc" name="otc" placeholder="XXXX-XXXX" aria-label="One-time code" autofocus inputmode="text" autocomplete="one-time-code" autocapitalize="characters" autocorrect="off" spellcheck="false" enterkeyhint="go" required>""";

    static string Page(Dictionary<string, string> p, string clientName, string redirect, string? error)
    {
        string H(string s) => WebUtility.HtmlEncode(s);
        var hidden = new StringBuilder();
        foreach (var k in new[] { "response_type", "client_id", "redirect_uri", "code_challenge", "code_challenge_method", "state", "scope", "resource", "pending" })
            if (p.TryGetValue(k, out var v)) hidden.Append($"<input type=\"hidden\" name=\"{k}\" value=\"{H(v)}\">");
        string pid = p.GetValueOrDefault("pending", "");
        Pending? w;
        lock (Gate) Waiting.TryGetValue(pid, out w);
        return Shell($"""
            <main>
            <header class="hero">{Icon}<h1 class="title">Allow access?</h1><p class="subtitle">The app “{H(clientName)}” wants to use your Unity games on this computer.</p></header>
            <ul class="group"><li class="row"><div class="k"><span class="sub">Access goes to</span><span class="title url">{H(Dest(redirect))}</span></div></li>
            {(w != null ? $"<li class=\"row\"><div class=\"k\"><span class=\"sub\">Check number</span><span class=\"title\">{w.Check}</span></div></li>" : "")}</ul>
            <form method="post" action="/authorize" class="stack">{hidden}<section>
            <div class="group">{CodeField}</div>
            {(error != null ? $"<p class=\"foot err\" role=\"alert\">{H(error)}</p>" : "")}
            <p class="foot">Paste the one-time code from the UDriver panel{(w != null ? ", or allow this request in the panel if it shows the same check number" : "")}.</p>
            {(w != null ? "<p class=\"foot\" id=\"wait\" role=\"status\">Waiting for the panel…</p>" : "")}
            </section><button class="btn primary" type="submit">Allow</button></form>
            <p class="foot center">After you allow, you'll be sent back to {H(Dest(redirect))}<br>If you didn't start this connection yourself, close this page.</p>
            </main>
            {(w != null ? PollScript.Replace("{id}", pid) : "")}
            """, "Allow access · UDriver");
    }

    const string PollScript = """
        <script>
        const form=document.querySelector("form"), wait=document.getElementById("wait");
        const timer=setInterval(async()=>{ let s; try{ s=(await (await fetch("/authorize/status?id={id}")).json()).state; }catch{ return; }
          if(s==="approved"){ clearInterval(timer); form.submit(); }
          else if(s!=="waiting"){ clearInterval(timer); wait.textContent=s==="denied"?"The request was declined in the UDriver panel.":"This request has expired. Start connecting again from the app."; } },2000);
        </script>
        """;

    /// <summary>Lists the sign-in in the panel for 5 minutes under a check number no other listed request has;
    /// "" once 3 from this address or 20 in total are open, and the page then takes only a one-time code.</summary>
    static string Wait(HttpContext ctx, string client, string app, string redirect, string challenge)
    {
        string id = Rand(16), ip = ClientIp(ctx);
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            foreach (var k in Waiting.Where(x => x.Value.Expires < now).Select(x => x.Key).ToList()) Waiting.Remove(k);
            if (Waiting.Count >= 20 || Waiting.Count(x => x.Value.Ip == ip && x.Value.Ok != false) >= 3) return "";
            int check;
            do check = RandomNumberGenerator.GetInt32(10, 100); while (Waiting.Values.Any(x => x.Check == check));
            Waiting[id] = new Pending
            {
                Client = client, App = app, Dest = Dest(redirect), Ip = ip, Redirect = redirect, Challenge = challenge, Check = check, Expires = now.AddMinutes(5),
            };
        }
        return id;
    }

    static string PendingState(string id)
    {
        lock (Gate)
            return !Waiting.TryGetValue(id, out var w) || w.Expires < DateTime.UtcNow ? "expired" : w.Ok switch { null => "waiting", true => "approved", false => "denied" };
    }

    static JsonArray PendingJson()
    {
        var now = DateTime.UtcNow;
        lock (Gate)
            return new JsonArray(Waiting.Where(x => x.Value.Ok == null && x.Value.Expires > now).OrderBy(x => x.Value.Expires).Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Key, ["client"] = x.Value.Client, ["app"] = x.Value.App, ["to"] = x.Value.Dest, ["ip"] = x.Value.Ip, ["check"] = x.Value.Check,
                ["expiresInSec"] = (int)(x.Value.Expires - now).TotalSeconds,
            }).ToArray());
    }

    static bool Decide(string id, bool ok)
    {
        lock (Gate)
        {
            if (!Waiting.TryGetValue(id, out var w) || w.Ok != null || w.Expires < DateTime.UtcNow) return false;
            w.Ok = ok;
            return true;
        }
    }

    static async Task Authorize(HttpContext ctx)
    {
        Dictionary<string, string> p;
        if (ctx.Request.Method == "POST") p = await Form(ctx);
        else { p = new(); foreach (var kv in ctx.Request.Query) p[kv.Key] = kv.Value.ToString(); }

        Client? client;
        lock (Gate) _store.Clients.TryGetValue(p.GetValueOrDefault("client_id", ""), out client);
        string redirect = p.GetValueOrDefault("redirect_uri", "");
        if (client == null || !RedirectMatches(client, redirect) || !RedirectAllowed(redirect))
        { await Write(ctx, 400, "text/plain; charset=utf-8", "Unknown app or redirect address. Remove the connector and add it again."); return; }
        if (p.GetValueOrDefault("response_type") != "code" || p.GetValueOrDefault("code_challenge", "").Length < 20 || p.GetValueOrDefault("code_challenge_method") != "S256")
        { await Write(ctx, 400, "text/plain; charset=utf-8", "Requests without PKCE (S256) are not accepted."); return; }
        string host = Dest(redirect);

        if (ctx.Request.Method == "GET")
        {
            p["pending"] = Wait(ctx, p["client_id"], client.Name, redirect, p["code_challenge"]);
            await Html(ctx, Page(p, client.Name, redirect, null));
            return;
        }

        string pid = p.GetValueOrDefault("pending", ""), otc = p.GetValueOrDefault("otc", "");
        bool approved = false, expired;
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            bool found = Waiting.TryGetValue(pid, out var w);
            if (found && w!.Ok == true && w.Client == p["client_id"] && w.Redirect == redirect && w.Challenge == p["code_challenge"])
            { approved = w.Expires > now; Waiting.Remove(pid); }
            // The page posts itself without a code once the panel approved; by then the request may have run out or been used.
            expired = !approved && pid.Length > 0 && otc.Length == 0 && (!found || w!.Expires <= now);
        }
        string? err = approved ? null : expired ? Expired : await Slow(ctx, () => TakeCode(otc), WrongCode);
        if (err != null) { await Html(ctx, Page(p, client.Name, redirect, err), StatusOf(err)); return; }

        string code = Rand();
        lock (Gate)
        {
            foreach (var k in Codes.Where(x => x.Value.Expires < DateTime.UtcNow).Select(x => x.Key).ToList()) Codes.Remove(k);
            Codes[Hash(code)] = new Code { ClientId = p["client_id"], RedirectUri = redirect, Challenge = p["code_challenge"], Expires = DateTime.UtcNow.AddSeconds(120) };
        }
        string sep = redirect.Contains('?') ? "&" : "?";
        string target = redirect + sep + "code=" + Uri.EscapeDataString(code) + (p.TryGetValue("state", out var st) ? "&state=" + Uri.EscapeDataString(st) : "");
        // Not a 302: the page's form-action would apply to every redirect hop.
        await Html(ctx, Shell($"""
            <main>
            <header class="hero">{Icon}<h1 class="title">Access allowed</h1><p class="subtitle">Taking you back to {WebUtility.HtmlEncode(host)}…</p></header>
            <a class="btn primary" id="go" href="{WebUtility.HtmlEncode(target)}">Continue</a>
            <p class="foot center">If nothing happens in a few seconds, press Continue.</p>
            </main>
            <script>location.replace(document.getElementById("go").href)</script>
            """, "Access allowed · UDriver"));
    }

    /// <summary>A key for an app on this computer that can only send a URL and a header; it works only through 127.0.0.1.</summary>
    static (string Id, string Token) AddLocalClient(string name)
    {
        string id = "l_" + Rand(16), token = Rand(48);
        lock (Gate)
        {
            _store.Clients[id] = new Client { Name = name, Used = true, Local = true };
            _store.Access[Hash(token)] = DateTime.MaxValue;
            _store.AccessClient[Hash(token)] = id;
            Save();
        }
        return (id, token);
    }

    static string LinkUsedFile => Path.Combine(Program.HomeDir, "link.used");

    /// <summary>status: connected (an agent is on now), allowed (may connect without asking), waiting (asks now), denied (has no access).</summary>
    static JsonArray ClientsJson()
    {
        var live = Registry.Agents().Where(a => (bool?)a!["disconnected"] != true).Select(a => (string?)a!["client"] is { Length: > 0 } c ? c : Program.StdioClient).ToHashSet();
        string Live(string id, string otherwise) => live.Contains(id) ? "connected" : otherwise;
        var list = new JsonArray(new JsonObject { ["id"] = Program.StdioClient, ["name"] = "Apps that start UDriver themselves", ["kind"] = "stdio", ["status"] = Live(Program.StdioClient, "allowed") });
        if (live.Contains(Program.LinkClient) || File.Exists(LinkUsedFile))
            list.Add(new JsonObject { ["id"] = Program.LinkClient, ["name"] = "Apps with the secret link", ["kind"] = "link", ["used"] = true, ["status"] = Live(Program.LinkClient, "allowed") });
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            var tokens = _store.AccessClient.Where(x => _store.Access.GetValueOrDefault(x.Key) > now)
                .Concat(_store.RefreshClient.Where(x => _store.Refresh.GetValueOrDefault(x.Key) > now)).Select(x => x.Value).ToHashSet();
            var asks = Waiting.Values.Where(w => w.Expires > now).ToList();
            foreach (var (id, c) in _store.Clients)
                list.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = c.Name, ["kind"] = c.Local ? "local" : "remote", ["used"] = c.Used,
                    ["status"] = Live(id, tokens.Contains(id) ? "allowed" : asks.Any(w => w.Client == id && w.Ok != false) ? "waiting" : "denied"),
                    ["to"] = new JsonArray(c.RedirectUris.Select(Dest).Distinct().Select(x => (JsonNode)x!).ToArray()),
                });
        }
        return list;
    }

    static bool KnownClient(string id)
    {
        lock (Gate) return id is Program.StdioClient or Program.LinkClient || _store.Clients.ContainsKey(id);
    }

    static JsonObject Issue(string clientId)
    {
        string access = Rand(), refresh = Rand();
        lock (Gate)
        {
            _store.Access[Hash(access)] = DateTime.UtcNow.AddHours(1);
            _store.AccessClient[Hash(access)] = clientId;
            _store.Refresh[Hash(refresh)] = DateTime.UtcNow.AddDays(30);
            _store.RefreshClient[Hash(refresh)] = clientId;
            Save();
        }
        return new JsonObject { ["access_token"] = access, ["token_type"] = "Bearer", ["expires_in"] = 3600, ["refresh_token"] = refresh };
    }

    static async Task Token(HttpContext ctx)
    {
        var p = await Form(ctx);
        string grant = p.GetValueOrDefault("grant_type", ""), clientId = p.GetValueOrDefault("client_id", "");
        // client_secret_basic is accepted; PKCE proves the caller, any secret is ignored.
        string basic = ctx.Request.Headers["Authorization"].ToString();
        if (clientId.Length == 0 && basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            try { clientId = Uri.UnescapeDataString(Encoding.UTF8.GetString(Convert.FromBase64String(basic.Substring(6).Trim())).Split(':')[0]); } catch { }
        if (grant == "authorization_code")
        {
            Code? c;
            string key = Hash(p.GetValueOrDefault("code", ""));
            lock (Gate) { if (Codes.TryGetValue(key, out c)) Codes.Remove(key); }   // single use
            string verifier = p.GetValueOrDefault("code_verifier", "");
            string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            bool known;
            lock (Gate) known = _store.Clients.ContainsKey(clientId);
            if (c == null || !known || c.Expires < DateTime.UtcNow || c.ClientId != clientId || c.RedirectUri != p.GetValueOrDefault("redirect_uri", "") || verifier.Length < 43 || !Same(challenge, c.Challenge))
            { await OAuthError(ctx, 400, "invalid_grant", "authorization code is invalid, expired, or the PKCE verifier does not match"); return; }
            lock (Gate) if (_store.Clients.TryGetValue(clientId, out var cl)) cl.Used = true;   // saved by Issue
            await Json(ctx, 200, Issue(clientId));
        }
        else if (grant == "refresh_token")
        {
            string key = Hash(p.GetValueOrDefault("refresh_token", ""));
            bool ok;
            JsonObject? again = null;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                foreach (var k in Rotated.Where(x => x.Value.Until < now).Select(x => x.Key).ToList()) Rotated.Remove(k);
                if (Rotated.TryGetValue(key, out var r) && r.Client == clientId) again = r.Reply;
                ok = again == null && _store.Clients.ContainsKey(clientId) && _store.Refresh.TryGetValue(key, out var exp) && exp > now && _store.RefreshClient.GetValueOrDefault(key) == clientId;
                if (ok) { _store.Refresh.Remove(key); _store.RefreshClient.Remove(key); }   // rotation
            }
            // A client that lost the answer may retry for 30 s and gets the same new pair.
            if (again != null) { await Json(ctx, 200, again.DeepClone()); return; }
            if (!ok) { await OAuthError(ctx, 400, "invalid_grant", "refresh token is invalid or expired"); return; }
            var reply = Issue(clientId);
            lock (Gate) Rotated[key] = (now.AddSeconds(30), clientId, reply);
            await Json(ctx, 200, reply.DeepClone());
        }
        else await OAuthError(ctx, 400, "unsupported_grant_type", "use authorization_code or refresh_token");
    }
}

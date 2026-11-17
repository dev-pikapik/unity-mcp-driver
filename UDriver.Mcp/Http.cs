// MCP over HTTP ("Streamable HTTP", JSON responses) for remote connectors, protected by OAuth 2.1 with PKCE.
// The listener (Kestrel) binds to 127.0.0.1 only; a tunnel (e.g. cloudflared) provides the public HTTPS address.
// Kestrel is used instead of HttpListener because HttpListener rejects requests whose Host header is the public name.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static partial class HttpMode
{
    sealed class Client { public string Name { get; set; } = ""; public List<string> RedirectUris { get; set; } = new(); public bool Used { get; set; } }   // Used: has obtained a token
    sealed class Code { public string ClientId = "", RedirectUri = "", Challenge = ""; public DateTime Expires; }
    sealed class Store
    {
        public Dictionary<string, Client> Clients { get; set; } = new();
        public Dictionary<string, DateTime> Access { get; set; } = new();          // sha256(token) -> expiry
        public Dictionary<string, string> RefreshClient { get; set; } = new();     // sha256(token) -> client id
        public Dictionary<string, DateTime> Refresh { get; set; } = new();
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, Code> Codes = new();
    static Store _store = new();
    static string _public = "", _password = "", _urlSecret = "", _storeFile = "";
    static readonly List<string> RedirectHosts = new()
    {
        "claude.ai", "claude.com",
        // Gemini: the app page, its per-user OAuth callback, and the Gemini Enterprise callback.
        "gemini.google.com", "oauth-redirect.googleusercontent.com", "vertexaisearch.cloud.google.com",
    };
    static int _port;

    // Wrong-password lockout, per client address, plus a much higher global cap so guessing from many addresses stays limited too.
    static readonly Dictionary<string, (int Fails, DateTime Seen, DateTime LockedUntil)> Failures = new();
    static int _globalFails; static DateTime _globalWindowStart, _globalLockedUntil;
    static readonly Queue<(DateTime At, string Ip)> Registrations = new();

    static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";
    static string Rand(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    public static async Task Run(int port)
    {
        _public = Env("UDRIVER_PUBLIC_URL").TrimEnd('/');
        _password = Env("UDRIVER_OWNER_PASSWORD");
        _urlSecret = Env("UDRIVER_URL_SECRET");
        bool secretMode = Env("UDRIVER_AUTH") == "secret-url";
        _panel = Env("UDRIVER_PANEL") == "1";
        _port = port;
        bool configured = _password.Length >= 12 && _public.StartsWith("http") && (!_panel || File.Exists(TunnelYml));
        if (_panel && !secretMode && !configured)
        {
            // First start: nothing is set up yet. Serve only the local setup form.
            _setupMode = true; _public = "";
        }
        else if (secretMode ? _urlSecret.Length < 32 : !configured)
        {
            Console.Error.WriteLine(secretMode
                ? "UDRIVER_URL_SECRET must be at least 32 characters."
                : "HTTP mode needs UDRIVER_PUBLIC_URL (the public https address) and UDRIVER_OWNER_PASSWORD (at least 12 characters).");
            Environment.Exit(2);
        }
        foreach (var h in Env("UDRIVER_OAUTH_REDIRECT_HOSTS").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) RedirectHosts.Add(h);
        _storeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver", "oauth.json");
        try { _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_storeFile)) ?? new Store(); } catch { _store = new Store(); }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);          // loopback only; never exposed directly
            o.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            o.AddServerHeader = false;
        });
        var app = builder.Build();
        app.Run(async ctx =>
        {
            try { await Handle(ctx, secretMode); }
            catch (Exception e) { try { if (!ctx.Response.HasStarted) await Write(ctx, 500, "application/json", new JsonObject { ["error"] = e.GetType().Name }.ToJsonString()); } catch { } }
        });
        if (_panel)
        {
            StartPanel();
            Console.Error.WriteLine(_setupMode
                ? $"UDriver: open http://127.0.0.1:{port}/panel in your browser and fill in the setup form."
                : $"UDriver IS RUNNING. Panel: http://127.0.0.1:{port}/panel  and  {_public}/panel\nTo stop: use the button in the panel or close this window.");
        }
        else Console.Error.WriteLine($"UDriver MCP (HTTP) on http://127.0.0.1:{port}  auth: {(secretMode ? "secret URL" : "OAuth")}  public: {_public}");
        await app.RunAsync();
    }

    static void Save()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var k in _store.Access.Where(x => x.Value < now).Select(x => x.Key).ToList()) _store.Access.Remove(k);
            foreach (var k in _store.Refresh.Where(x => x.Value < now).Select(x => x.Key).ToList()) { _store.Refresh.Remove(k); _store.RefreshClient.Remove(k); }
            Program.WritePrivate(_storeFile, JsonSerializer.Serialize(_store));
        }
        catch { }
    }

    static async Task Write(HttpContext ctx, int status, string type, string body, Action<HttpResponse>? extra = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
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

    /// <summary>The visitor's address: Cloudflare's header when the request came through the tunnel, otherwise the TCP peer.</summary>
    static string ClientIp(HttpContext ctx)
    {
        string ip = ctx.Request.Headers["Cf-Connecting-Ip"].ToString();
        return ip.Length is > 0 and <= 64 ? ip : ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    /// <summary>Checks the owner password. Returns null if correct, otherwise the message to show. 5 wrong attempts lock that address for 10 minutes.</summary>
    static string? CheckPassword(HttpContext ctx, string given)
    {
        const string wait = "Too many wrong attempts. Please wait 10 minutes.";
        string ip = ClientIp(ctx);
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (now < _globalLockedUntil) return wait;
            var f = Failures.GetValueOrDefault(ip);
            if (now < f.LockedUntil) return wait;
            if (Same(given, _password)) { Failures.Remove(ip); return null; }
            if (now - f.Seen > TimeSpan.FromMinutes(10)) f.Fails = 0;
            f.Seen = now;
            if (++f.Fails >= 5) { f.LockedUntil = now.AddMinutes(10); f.Fails = 0; }
            // ponytail: when full, forget addresses that are not locked; the global cap below bounds what that gives away.
            if (Failures.Count >= 1000 && !Failures.ContainsKey(ip))
                foreach (var k in Failures.Where(x => x.Value.LockedUntil < now).Select(x => x.Key).ToList()) Failures.Remove(k);
            Failures[ip] = f;
            if (now - _globalWindowStart > TimeSpan.FromMinutes(10)) { _globalWindowStart = now; _globalFails = 0; }
            if (++_globalFails >= 100) _globalLockedUntil = now.AddMinutes(10);
            return "Wrong password.";
        }
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

        // Accept only our own names: the public one (via the tunnel) and the local one. Anything else is a rebinding attempt.
        string host = ctx.Request.Host.Value ?? "";
        string publicHost = Uri.TryCreate(_public, UriKind.Absolute, out var pu) ? pu.Authority : "";
        if (host != publicHost && host != "127.0.0.1:" + _port && host != "localhost:" + _port)
        { await OAuthError(ctx, 421, "misdirected_request", "unexpected Host"); return; }

        if (_panel && !secretMode && (path == "/" || path == "/panel" || path.StartsWith("/panel/")))
        { await Panel(ctx, path, verb, IsLocal(ctx)); return; }
        if (_setupMode) { await Write(ctx, 503, "text/plain; charset=utf-8", "UDriver is not set up yet"); return; }

        // Browsers attach Origin to cross-site requests; only the connector's own sites may call in from a page.
        string origin = ctx.Request.Headers["Origin"].ToString();
        if (origin.Length > 0 && origin != _public && !(Uri.TryCreate(origin, UriKind.Absolute, out var ou) && RedirectHosts.Contains(ou.Host)))
        { await OAuthError(ctx, 403, "forbidden", "origin not allowed"); return; }

        // Connectors that call from the browser (Gemini) need CORS. Bearer tokens only, no cookies, so no credentials flag.
        if (origin.Length > 0 && origin != _public)
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
            await Mcp(ctx); return;
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
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
            });
        }
        else if (path == "/register" && verb == "POST") await Register(ctx);
        else if (path == "/authorize" && (verb == "GET" || verb == "POST")) await Authorize(ctx);
        else if (path == "/token" && verb == "POST") await Token(ctx);
        else if (path == "/mcp")
        {
            string auth = ctx.Request.Headers["Authorization"].ToString();
            bool ok = false;
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                lock (Gate) ok = _store.Access.TryGetValue(Hash(auth.Substring(7).Trim()), out var exp) && exp > DateTime.UtcNow;
            if (!ok)
            {
                await Write(ctx, 401, "application/json", "{\"error\":\"invalid_token\"}",
                    r => r.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{_public}/.well-known/oauth-protected-resource\"");
                return;
            }
            await Mcp(ctx);
        }
        else await Write(ctx, 404, "text/plain", "not found");
    }

    // ---------------- MCP endpoint ----------------

    static async Task Mcp(HttpContext ctx)
    {
        if (ctx.Request.Method != "POST") { await Write(ctx, 405, "text/plain", "POST only", r => r.Headers["Allow"] = "POST"); return; }
        string body;
        using (var rd = new StreamReader(ctx.Request.Body, Encoding.UTF8)) body = await rd.ReadToEndAsync();
        JsonNode? msg;
        try { msg = JsonNode.Parse(body); }
        catch { await Json(ctx, 400, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "parse error" } }); return; }

        async Task<JsonObject?> One(JsonNode? m)
        {
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
        if (reply == null) await Write(ctx, 202, "application/json", ""); else await Json(ctx, 200, reply);
    }

    // ---------------- OAuth ----------------

    static bool RedirectAllowed(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == "https" && RedirectHosts.Contains(u.Host)) return true;
        return u.Scheme == "http" && (u.Host == "localhost" || u.Host == "127.0.0.1");   // native apps on this machine
    }

    static bool RedirectMatches(Client c, string uri)
    {
        if (c.RedirectUris.Contains(uri)) return true;
        // Native apps may use a different loopback port than the one they registered.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != "http" || (u.Host != "localhost" && u.Host != "127.0.0.1")) return false;
        return c.RedirectUris.Any(r => Uri.TryCreate(r, UriKind.Absolute, out var x) && x.Scheme == "http" && x.Host == u.Host && x.AbsolutePath == u.AbsolutePath);
    }

    static async Task Register(HttpContext ctx)
    {
        JsonObject? req;
        using (var rd = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            try { req = JsonNode.Parse(await rd.ReadToEndAsync()) as JsonObject; } catch { req = null; }
        var uris = (req?["redirect_uris"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
        if (uris.Count == 0 || uris.Count > 10 || !uris.All(RedirectAllowed))
        { await OAuthError(ctx, 400, "invalid_redirect_uri", "redirect URIs must point to " + string.Join(", ", RedirectHosts) + " or to localhost"); return; }
        string id = "c_" + Rand(16), name = ((string?)req?["client_name"] ?? "MCP client");
        if (name.Length > 80) name = name.Substring(0, 80);
        string ip = ClientIp(ctx);
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            // Registration needs no sign-in, so it is rate-limited, and at the cap the oldest app that never got a token goes first.
            while (Registrations.Count > 0 && Registrations.Peek().At < now.AddMinutes(-10)) Registrations.Dequeue();
            if (Registrations.Count >= 100 || Registrations.Count(x => x.Ip == ip) >= 10) id = "";
            else
            {
                Registrations.Enqueue((now, ip));
                if (_store.Clients.Count >= 200)
                    _store.Clients.Remove(_store.Clients.FirstOrDefault(x => !x.Value.Used).Key ?? _store.Clients.Keys.First());
                _store.Clients[id] = new Client { Name = name, RedirectUris = uris };
                Save();
            }
        }
        if (id.Length == 0) { await OAuthError(ctx, 429, "too_many_requests", "too many app registrations, try again in a few minutes"); return; }
        await Json(ctx, 201, new JsonObject
        {
            ["client_id"] = id, ["client_name"] = name, ["redirect_uris"] = new JsonArray(uris.Select(u => (JsonNode)u!).ToArray()),
            ["token_endpoint_auth_method"] = "none", ["grant_types"] = new JsonArray("authorization_code", "refresh_token"), ["response_types"] = new JsonArray("code"),
        });
    }

    static string Page(Dictionary<string, string> p, string clientName, string redirectHost, string? error)
    {
        string H(string s) => WebUtility.HtmlEncode(s);
        var hidden = new StringBuilder();
        foreach (var k in new[] { "response_type", "client_id", "redirect_uri", "code_challenge", "code_challenge_method", "state", "scope", "resource" })
            if (p.TryGetValue(k, out var v)) hidden.Append($"<input type=\"hidden\" name=\"{k}\" value=\"{H(v)}\">");
        return Shell($"""
            <main class="narrow">
            <header class="hero">{Icon}<h1 class="title">Allow access?</h1><p class="subtitle">The app “{H(clientName)}” wants to control Unity on this computer.</p></header>
            <form method="post" action="/authorize">{hidden}<section>
            <div class="group pad"><label class="sr" for="password">Owner password</label><input class="field" type="password" id="password" name="password" placeholder="Owner password" autofocus autocomplete="current-password"></div>
            {(error != null ? $"<p class=\"foot err\" role=\"alert\">{H(error)}</p>" : "")}
            </section><div class="actions"><button class="btn primary" type="submit">Allow</button></div></form>
            <p class="foot center">After you allow, you'll be sent back to {H(redirectHost)}.<br>If you didn't start this connection yourself, close this page.</p>
            </main>
            """, "Allow access · UDriver");
    }

    static async Task Authorize(HttpContext ctx)
    {
        Dictionary<string, string> p;
        if (ctx.Request.Method == "POST") p = await Form(ctx);
        else { p = new(); foreach (var kv in ctx.Request.Query) p[kv.Key] = kv.Value.ToString(); }

        Client? client;
        lock (Gate) _store.Clients.TryGetValue(p.GetValueOrDefault("client_id", ""), out client);
        string redirect = p.GetValueOrDefault("redirect_uri", "");
        // Never redirect to an address that was not registered: show the error here instead.
        if (client == null || !RedirectMatches(client, redirect) || !RedirectAllowed(redirect))
        { await Write(ctx, 400, "text/plain; charset=utf-8", "Unknown app or redirect address. Remove the connector and add it again."); return; }
        if (p.GetValueOrDefault("response_type") != "code" || p.GetValueOrDefault("code_challenge", "").Length < 20 || p.GetValueOrDefault("code_challenge_method") != "S256")
        { await Write(ctx, 400, "text/plain; charset=utf-8", "Requests without PKCE (S256) are not accepted."); return; }
        string host = new Uri(redirect).Host;

        // The form posts here and is then redirected to the app, so form-action must allow the app's origin too.
        string formAction = "'self' " + new Uri(redirect).GetLeftPart(UriPartial.Authority);
        if (ctx.Request.Method == "GET") { await Html(ctx, Page(p, client.Name, host, null), formAction: formAction); return; }

        string? err = CheckPassword(ctx, p.GetValueOrDefault("password", ""));
        if (err != null) { await Task.Delay(700); await Html(ctx, Page(p, client.Name, host, err), formAction: formAction); return; }

        string code = Rand();
        lock (Gate)
        {
            foreach (var k in Codes.Where(x => x.Value.Expires < DateTime.UtcNow).Select(x => x.Key).ToList()) Codes.Remove(k);
            Codes[Hash(code)] = new Code { ClientId = p["client_id"], RedirectUri = redirect, Challenge = p["code_challenge"], Expires = DateTime.UtcNow.AddSeconds(120) };
        }
        string sep = redirect.Contains('?') ? "&" : "?";
        string target = redirect + sep + "code=" + Uri.EscapeDataString(code) + (p.TryGetValue("state", out var st) ? "&state=" + Uri.EscapeDataString(st) : "");
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Redirect(target);
    }

    static JsonObject Issue(string clientId)
    {
        string access = Rand(), refresh = Rand();
        lock (Gate)
        {
            _store.Access[Hash(access)] = DateTime.UtcNow.AddHours(1);
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
        // Some clients send the id with HTTP Basic (client_secret_basic). Clients are public: PKCE proves the caller, any secret is ignored.
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
            if (c == null || c.Expires < DateTime.UtcNow || c.ClientId != clientId || c.RedirectUri != p.GetValueOrDefault("redirect_uri", "") || verifier.Length < 43 || !Same(challenge, c.Challenge))
            { await OAuthError(ctx, 400, "invalid_grant", "authorization code is invalid, expired, or the PKCE verifier does not match"); return; }
            lock (Gate) if (_store.Clients.TryGetValue(clientId, out var cl)) cl.Used = true;   // saved by Issue
            await Json(ctx, 200, Issue(clientId));
        }
        else if (grant == "refresh_token")
        {
            string key = Hash(p.GetValueOrDefault("refresh_token", ""));
            bool ok;
            lock (Gate)
            {
                ok = _store.Refresh.TryGetValue(key, out var exp) && exp > DateTime.UtcNow && _store.RefreshClient.GetValueOrDefault(key) == clientId;
                if (ok) { _store.Refresh.Remove(key); _store.RefreshClient.Remove(key); }   // rotation
            }
            if (!ok) { await OAuthError(ctx, 400, "invalid_grant", "refresh token is invalid or expired"); return; }
            await Json(ctx, 200, Issue(clientId));
        }
        else await OAuthError(ctx, 400, "unsupported_grant_type", "use authorization_code or refresh_token");
    }
}

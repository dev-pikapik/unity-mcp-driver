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
        public Dictionary<string, string> AccessClient { get; set; } = new();      // sha256(token) -> client id
        public Dictionary<string, string> RefreshClient { get; set; } = new();     // sha256(token) -> client id
        public Dictionary<string, DateTime> Refresh { get; set; } = new();
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, Code> Codes = new();
    static Store _store = new();
    static string _public = "", _urlSecret = "", _storeFile = "";
    static readonly List<string> RedirectHosts = new()
    {
        "claude.ai", "claude.com",
        // Google-hosted agent apps: per-user OAuth callbacks and the enterprise callback.
        "oauth-redirect.googleusercontent.com", "oauth-redirect-sandbox.googleusercontent.com",
        "oauth-redirect-test.googleusercontent.com", "vertexaisearch.cloud.google.com",
    };
    static readonly Dictionary<string, string> RedirectPaths = new()
    {
        ["claude.ai"] = "/api/mcp/auth_callback", ["claude.com"] = "/api/mcp/auth_callback",
        ["oauth-redirect.googleusercontent.com"] = "/r/", ["oauth-redirect-sandbox.googleusercontent.com"] = "/r/",
        ["oauth-redirect-test.googleusercontent.com"] = "/r/", ["vertexaisearch.cloud.google.com"] = "/oauth-redirect",
    };
    static int _port;

    // Wrong codes are counted per address and in total; over the limit the answer is 429 before any hashing.
    static readonly Dictionary<string, (int Fails, DateTime Seen)> Failures = new();
    static int _globalFails; static DateTime _globalWindowStart;
    static readonly SemaphoreSlim Checking = new(1, 1);
    const int IpLimit = 10, AllLimit = 50;
    const string TooMany = "Too many wrong attempts. Wait 10 minutes, then try again.";
    const string CheckBusy = "Another sign-in is being checked. Try again in a moment.";
    static readonly Dictionary<string, Program.Session> Agents = new();
    static readonly Dictionary<string, (DateTime Until, string Client, JsonObject Reply)> Rotated = new();   // sha256(old refresh token) -> its replacement
    static readonly Queue<(DateTime At, string Ip)> Registrations = new();

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
        foreach (var h in Env("UDRIVER_OAUTH_REDIRECT_HOSTS").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) RedirectHosts.Add(h);
        _storeFile = Path.Combine(Program.HomeDir, "oauth.json");
        try { _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_storeFile)) ?? new Store(); } catch { _store = new Store(); }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            o.Limits.MaxConcurrentConnections = 200;
            o.AddServerHeader = false;
        });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { KillTunnel(); Program.StopLaunched(); };
        var app = builder.Build();
        // The launcher opens the panel only when the process on the port is the one named here.
        if (_panel) app.Lifetime.ApplicationStarted.Register(() => Program.WritePrivate(PidFile, Environment.ProcessId.ToString()));
        app.Run(async ctx =>
        {
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
        if (_panel && _public.Length == 0) { await Write(ctx, 503, "text/plain; charset=utf-8", "Remote access to UDriver is off"); return; }

        // Pages may call in only from the connector's own sites.
        string origin = ctx.Request.Headers["Origin"].ToString();
        if (origin.Length > 0 && origin != _public && !(Uri.TryCreate(origin, UriKind.Absolute, out var ou) && RedirectHosts.Contains(ou.Host)))
        { await OAuthError(ctx, 403, "forbidden", "origin not allowed"); return; }

        // Bearer tokens only, so CORS without credentials.
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
            Program.Current.Value = Agent("", IsLocal(ctx));
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
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none", "client_secret_post", "client_secret_basic"),
            });
        }
        else if (path == "/register" && verb == "POST") await Register(ctx);
        else if (path == "/authorize" && (verb == "GET" || verb == "POST")) await Authorize(ctx);
        else if (path == "/token" && verb == "POST") await Token(ctx);
        else if (path == "/mcp")
        {
            string auth = ctx.Request.Headers["Authorization"].ToString();
            string? clientId = null;
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                lock (Gate)
                {
                    string key = Hash(auth.Substring(7).Trim());
                    if (_store.Access.TryGetValue(key, out var exp) && exp > DateTime.UtcNow) _store.AccessClient.TryGetValue(key, out clientId);
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
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.UserInfo.Length > 0 || u.Fragment.Length > 0) return false;
        if (u.Scheme == "http") return u.Host == "localhost" || u.Host == "127.0.0.1";   // native apps on this machine
        if (u.Scheme != "https" || !u.IsDefaultPort || !RedirectHosts.Contains(u.Host)) return false;
        if (!RedirectPaths.TryGetValue(u.Host, out var want)) return true;
        string path = u.AbsolutePath;
        return u.Query.Length == 0 && (want.EndsWith('/') ? path.Length > want.Length && path.StartsWith(want, StringComparison.Ordinal) && path.IndexOf('/', want.Length) < 0 : path == want);
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
        var all = (req?["redirect_uris"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
        // Some agent apps also send test callbacks: keep the allowed ones, drop the rest.
        var uris = all.Where(RedirectAllowed).ToList();
        if (uris.Count == 0 || all.Count > 10)
        { await OAuthError(ctx, 400, "invalid_redirect_uri", "redirect URIs must point to " + string.Join(", ", RedirectHosts) + " or to localhost"); return; }
        string id = "c_" + Rand(16), name = ((string?)req?["client_name"] ?? "MCP client");
        if (name.Length > 80) name = name.Substring(0, 80);
        string ip = ClientIp(ctx);
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            // Open registration: rate-limited; at the cap the oldest never-used app goes first.
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
        foreach (var k in new[] { "response_type", "client_id", "redirect_uri", "code_challenge", "code_challenge_method", "state", "scope", "resource" })
            if (p.TryGetValue(k, out var v)) hidden.Append($"<input type=\"hidden\" name=\"{k}\" value=\"{H(v)}\">");
        return Shell($"""
            <main>
            <header class="hero">{Icon}<h1 class="title">Allow access?</h1><p class="subtitle">The app “{H(clientName)}” wants to use your Unity games on this computer.</p></header>
            <form method="post" action="/authorize" class="stack">{hidden}<section>
            <div class="group">{CodeField}</div>
            {(error != null ? $"<p class=\"foot err\" role=\"alert\">{H(error)}</p>" : "")}
            <p class="foot">Paste the one-time code from the UDriver panel.</p>
            </section><button class="btn primary" type="submit">Allow</button></form>
            <p class="foot center">After you allow, you'll be sent back to {H(redirect)}<br>If you didn't start this connection yourself, close this page.</p>
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
        if (client == null || !RedirectMatches(client, redirect) || !RedirectAllowed(redirect))
        { await Write(ctx, 400, "text/plain; charset=utf-8", "Unknown app or redirect address. Remove the connector and add it again."); return; }
        if (p.GetValueOrDefault("response_type") != "code" || p.GetValueOrDefault("code_challenge", "").Length < 20 || p.GetValueOrDefault("code_challenge_method") != "S256")
        { await Write(ctx, 400, "text/plain; charset=utf-8", "Requests without PKCE (S256) are not accepted."); return; }
        string host = new Uri(redirect).Host;

        if (ctx.Request.Method == "GET") { await Html(ctx, Page(p, client.Name, redirect, null)); return; }

        string? err = await Slow(ctx, () => TakeCode(p.GetValueOrDefault("otc", "")), WrongCode);
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
            if (c == null || c.Expires < DateTime.UtcNow || c.ClientId != clientId || c.RedirectUri != p.GetValueOrDefault("redirect_uri", "") || verifier.Length < 43 || !Same(challenge, c.Challenge))
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
                ok = again == null && _store.Refresh.TryGetValue(key, out var exp) && exp > now && _store.RefreshClient.GetValueOrDefault(key) == clientId;
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

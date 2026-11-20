using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;

namespace UDriver;

static class Api
{
    public static int Port = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_HTTP_PORT"), out var p) ? p : 8787;
    static readonly Version Own = Version.TryParse(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0], out var v) ? v : new();
    public static readonly string Home = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } u ? u : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver");
    static readonly string KeyPath = Path.Combine(Home, "app.key");
    static readonly string Log = Path.Combine(Home, "logs", "server.log");
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static async Task<HttpResponseMessage> Send(string path, Dictionary<string, string>? form, int seconds)
    {
        var req = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{Port}{path}");
        if (File.Exists(KeyPath)) req.Headers.Authorization = new("Bearer", File.ReadAllText(KeyPath).Trim());
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return await Http.SendAsync(req, cts.Token);
    }

    public static async Task<JsonObject> Json(string path, Dictionary<string, string>? form = null, int seconds = 15)
    {
        using var res = await Send("/panel/api/" + path, form, seconds);
        JsonObject? o = null;
        try { o = JsonNode.Parse(await res.Content.ReadAsStringAsync()) as JsonObject; } catch { }
        if (o == null) throw new Exception(res.IsSuccessStatusCode ? "UDriver gave an answer this app does not understand." : $"UDriver answered with error {(int)res.StatusCode}.");
        if (o["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b) throw new Exception(o.S("error") is { Length: > 0 } e ? e : "Something went wrong.");
        return o;
    }

    public static Task<JsonObject> Get(string path) => Json(path);
    public static Task<JsonObject> Post(string path, params (string Key, string Value)[] form) => Json(path, form.ToDictionary(f => f.Key, f => f.Value));

    public static async Task<byte[]?> Bytes(string path)
    {
        try
        {
            using var res = await Send("/panel/api/" + path, null, 5);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync() : null;
        }
        catch { return null; }
    }

    static async Task<bool> Running() => await ServerVersion() != null;

    /// The version the server on the port reports: null when nothing answers, "" when it is not UDriver 2.0.2 or newer.
    static async Task<string?> ServerVersion()
    {
        try
        {
            using var cts = new CancellationTokenSource(1000);
            using var r = await Http.GetAsync($"http://127.0.0.1:{Port}/panel", cts.Token);
            return r.Headers.TryGetValues("X-UDriver-Version", out var v) ? v.First() : "";
        }
        catch { return null; }
    }

    /// True when the server on the port is UDriver of this app's version or newer, not an older install.
    static async Task<bool> Ours() => Version.TryParse(await ServerVersion(), out var v) && v >= Own;

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static (string File, string[] Args)? Server()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "udriver-mcp.exe");
        var dll = Environment.GetEnvironmentVariable("UDRIVER_SERVER_DLL");
        if (dll == null && File.Exists(exe)) return (exe, []);
        dll ??= Path.Combine(Home, "server", "udriver-mcp.dll");
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var dotnet = new[] { root == null ? null : Path.Combine(root, "dotnet.exe"), Path.Combine(Home, "dotnet", "dotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") }.FirstOrDefault(File.Exists);
        return File.Exists(dll) && dotnet != null ? (dotnet, [dll]) : null;
    }

    public static async Task<string?> Ensure()
    {
        // Another program or an older UDriver on the port is left alone: the server then starts on a free port.
        if (await Ours())
        {
            for (int i = 0; i < 50 && !File.Exists(KeyPath); i++) await Task.Delay(200);
            return File.Exists(KeyPath) ? null : "UDriver is taking too long to start.";
        }
        int first = Port;
        if (int.TryParse(File.Exists(Path.Combine(Home, "panel.port")) ? File.ReadAllText(Path.Combine(Home, "panel.port")).Trim() : "", out int saved) && saved != Port)
        {
            Port = saved;
            if (await Ours()) return null;
            Port = first;
        }
        if (await Running()) Port = FreePort();
        if (Server() is not { } s) return "UDriver is not fully installed on this computer. Run the UDriver installer once, then open this app again.";
        Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
        var psi = new ProcessStartInfo(s.File) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in s.Args) psi.ArgumentList.Add(a);
        foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("VELOPACK_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
        psi.Environment["UDRIVER_HTTP_PORT"] = Port.ToString();
        psi.Environment["UDRIVER_PANEL"] = "1";
        psi.Environment["UDRIVER_LOG"] = Log;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        var package = Path.Combine(Home, "package");
        if (Environment.GetEnvironmentVariable("UDRIVER_DIST") == null && Directory.Exists(package)) psi.Environment["UDRIVER_DIST"] = package;
        using var proc = Process.Start(psi)!;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(200);
            if (await Running() && File.Exists(KeyPath)) return null;
            if (proc.HasExited) return $"UDriver stopped right after starting. Details are in {Log}.";
        }
        return "UDriver is taking too long to start.";
    }
}

static class J
{
    public static string S(this JsonNode? n, string k) => n?[k] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
    public static bool B(this JsonNode? n, string k, bool d = false) => n?[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
    public static int I(this JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;
    public static JsonNode[] A(this JsonNode? n, string k) => n?[k] is JsonArray a ? a.OfType<JsonNode>().ToArray() : [];
}

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace UDriver;

static class Api
{
    public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_HTTP_PORT"), out var p) ? p : 8787;
    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".udriver");
    static readonly string KeyPath = Path.Combine(Home, "app.key");
    static readonly string Log = Path.Combine(Home, "logs", "server.log");
    static readonly HttpClient Http = new() { BaseAddress = new($"http://127.0.0.1:{Port}"), Timeout = Timeout.InfiniteTimeSpan };

    static async Task<HttpResponseMessage> Send(string path, Dictionary<string, string>? form, int seconds)
    {
        var req = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, path);
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

    static async Task<bool> Running()
    {
        try { using var cts = new CancellationTokenSource(1000); using var r = await Http.GetAsync("/panel", cts.Token); return true; }
        catch { return false; }
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
        if (await Running())
        {
            for (int i = 0; i < 50 && !File.Exists(KeyPath); i++) await Task.Delay(200);
            return File.Exists(KeyPath) ? null : $"Another program uses port {Port}. Close it, then open UDriver again.";
        }
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

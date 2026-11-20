using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace UDriver.App;

sealed class ServerError(string message) : Exception(message);

/// The local server's /panel/api, signed in with ~/.udriver/app.key.
static class Api
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public static readonly string Home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static int Port = int.TryParse(Environment.GetEnvironmentVariable("UDRIVER_HTTP_PORT"), out int p) ? p : 8787;
    static readonly Version Own = Version.TryParse(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0], out var v) ? v : new();
    static string Base => $"http://127.0.0.1:{Port}";
    public static string Dll => Environment.GetEnvironmentVariable("UDRIVER_SERVER_DLL") ?? Home + "/.udriver/server/udriver-mcp.dll";
    public static string Bundled => Path.Combine(AppContext.BaseDirectory, "udriver-mcp");
    public static bool UseBundled => Environment.GetEnvironmentVariable("UDRIVER_SERVER_DLL") == null && File.Exists(Bundled);
    static string? key;
    static Process? started;

    static string? Read(string name)
    {
        try { string s = File.ReadAllText(Path.Combine(Home, ".udriver", name)).Trim(); return s.Length > 0 ? s : null; }
        catch { return null; }
    }

    public static async Task<(byte[] Data, int Status)> Data(string path, Dictionary<string, string>? form = null, int timeout = 20, bool again = true)
    {
        key ??= Read("app.key") ?? throw new ServerError("UDriver has not finished starting.");
        using var req = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var resp = await Http.SendAsync(req, cts.Token);
        int status = (int)resp.StatusCode;
        if (status is 401 or 403 && again) { key = null; return await Data(path, form, timeout, false); }
        return (await resp.Content.ReadAsByteArrayAsync(cts.Token), status);
    }

    public static async Task<JsonNode> Json(string path, Dictionary<string, string>? form = null, int timeout = 20)
    {
        var (d, _) = await Data(path, form, timeout);
        JsonNode? j;
        try { j = JsonNode.Parse(d); } catch { j = null; }
        if (j is not JsonObject) throw new ServerError(Encoding.UTF8.GetString(d, 0, Math.Min(200, d.Length)));
        if (j["ok"] is JsonValue ok && ok.TryGetValue(out bool fine) && !fine) throw new ServerError(j.S("error") is { Length: > 0 } e ? e : "Something went wrong.");
        return j;
    }

    public static async Task<bool> Running() => await ServerVersion() != null;

    /// The version the server on the port reports: null when nothing answers, "" when it is not UDriver 2.0.2 or newer.
    static async Task<string?> ServerVersion()
    {
        try
        {
            using var c = new CancellationTokenSource(1000);
            using var r = await Http.GetAsync(Base + "/panel", c.Token);
            return r.Headers.TryGetValues("X-UDriver-Version", out var v) ? v.First() : "";
        }
        catch { return null; }
    }

    /// True when the server on the port is UDriver of this app's version or newer, not an older install.
    static async Task<bool> Ours() => Version.TryParse(await ServerVersion(), out var v) && v >= Own;

    static bool Free(int port)
    {
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; }
        catch (SocketException) { return false; }
    }

    /// Moves to the first port after the usual one that runs this app's UDriver (true) or is free, so the address in agent setups survives a restart.
    static async Task<bool> NextPort()
    {
        int first = Port;
        for (Port = first + 1; Port <= first + 20; Port++)
        {
            if (await Ours()) return true;
            if (Free(Port)) return false;
        }
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return false;
    }

    /// The server as a command line: the copy inside this app, or .NET with the installed server.
    public static (string File, string[] Args)? Command()
    {
        if (UseBundled) return (Bundled, []);
        if (!File.Exists(Dll)) return null;
        string?[] candidates = [Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } r ? r + "/dotnet" : null, Home + "/.udriver/dotnet/dotnet",
            Home + "/.dotnet/dotnet", "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/lib64/dotnet/dotnet", "/usr/bin/dotnet"];
        return candidates.FirstOrDefault(File.Exists) is { } dotnet ? (dotnet, [Dll]) : null;
    }

    /// Stops an older UDriver server left running by a previous version, with its tunnel: two servers on one tunnel address split agents between them, and the old one turns them away.
    static async Task StopOlder()
    {
        int keep = Port;
        var ports = Enumerable.Range(keep, 21).ToList();
        if (int.TryParse(Read("panel.port"), out int saved) && !ports.Contains(saved)) ports.Add(saved);
        foreach (int port in ports)
        {
            Port = port;
            // A port taken but not answering is a hung server: stop it only if it is udriver-mcp.
            var v = await ServerVersion();
            if (v == null ? Free(port) : Version.TryParse(v, out var ver) && ver >= Own) continue;
            string net;
            try
            {
                using var ss = Process.Start(new ProcessStartInfo("ss", $"-Hltnp sport = :{port}") { RedirectStandardOutput = true, UseShellExecute = false })!;
                net = await ss.StandardOutput.ReadToEndAsync();
            }
            catch { continue; }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(net, "\\(\"([^\"]+)\",pid=(\\d+)"))
            {
                if (m.Groups[1].Value != "udriver-mcp" && !(!string.IsNullOrEmpty(v) && m.Groups[1].Value == "dotnet")) continue;
                try { using var p = Process.GetProcessById(int.Parse(m.Groups[2].Value)); p.Kill(true); p.WaitForExit(5000); } catch { }
            }
        }
        Port = keep;
    }

    /// Starts the server unless this app's UDriver already answers; returns an error to show, or null once it answers.
    /// Another program on the port is left alone: the server then starts on the next free port.
    public static async Task<string?> EnsureServer()
    {
        await StopOlder();
        if (await Ours()) return null;
        int first = Port;
        if (int.TryParse(Read("panel.port"), out int saved) && saved != Port)
        {
            Port = saved;
            if (await Ours()) return null;
            Port = first;
        }
        if (started is not { HasExited: false } && (await Running() || !Free(Port)) && await NextPort()) return null;
        if (started is not { HasExited: false } && Start() is { } error) return error;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(200);
            if (await Running()) return null;
            if (started!.HasExited) return "UDriver stopped right after starting. Details are in ~/.udriver/logs/server.log.";
        }
        return "UDriver is taking too long to start.";
    }

    static string? Start()
    {
        if (Command() is not var (file, args))
            return File.Exists(Dll) ? "UDriver needs .NET 10. Run the UDriver installer once, then open this app again."
                : "UDriver is not installed on this computer yet. Run the UDriver installer once, then open this app again.";
        Directory.CreateDirectory(Home + "/.udriver/logs");
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Home + "/.udriver" };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["UDRIVER_HTTP_PORT"] = Port.ToString();
        psi.Environment["UDRIVER_PANEL"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        if (!psi.Environment.ContainsKey("UDRIVER_DIST") && Directory.Exists(Home + "/.udriver/package")) psi.Environment["UDRIVER_DIST"] = Home + "/.udriver/package";
        var log = new StreamWriter(Home + "/.udriver/logs/server.log") { AutoFlush = true };
        try
        {
            started = Process.Start(psi)!;
            started.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            started.OutputDataReceived += (_, _) => { };
            started.BeginErrorReadLine();
            started.BeginOutputReadLine();
        }
        catch (Exception e) { return "UDriver could not start: " + e.Message; }
        return null;
    }

    /// The server lives inside this app's folder, which closes with the app, so it stops with the app.
    public static void StopStarted()
    {
        try { if (started is { HasExited: false }) started.Kill(); } catch { }
    }
}

static class JsonExt
{
    public static string S(this JsonNode? n, string k) => n?[k] is JsonValue v ? v.ToString() : "";
    public static bool B(this JsonNode? n, string k, bool fallback = false) => n?[k] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
    public static int? I(this JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue(out int i) ? i : null;
    public static List<JsonNode> L(this JsonNode? n, string k) => n?[k] is JsonArray a ? a.OfType<JsonNode>().ToList() : [];
    public static string Key(params object?[] parts) => string.Join("\u001f", parts.Select(p => p is JsonNode j ? j.ToJsonString() : p?.ToString()));
}

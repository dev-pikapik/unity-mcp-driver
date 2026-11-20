using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

/// <summary>Remote access: the Cloudflare tunnel run by this server, and one-time codes for remote agents and devices. Changed only from the panel.</summary>
internal static partial class HttpMode
{
    // Our own Cloudflare sign-in lives in ~/.udriver/cloudflare (cloudflared runs with HOME there); a sign-in made outside UDriver is used but never removed.
    static string CfHome => Path.Combine(Program.HomeDir, "cloudflare");
    static string OwnCert => Path.Combine(CfHome, ".cloudflared", "cert.pem");
    static string SharedCfDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared");
    static string? Cert => File.Exists(OwnCert) ? OwnCert : File.Exists(Path.Combine(SharedCfDir, "cert.pem")) ? Path.Combine(SharedCfDir, "cert.pem") : null;
    const string CfRelease = "https://github.com/cloudflare/cloudflared/releases/latest/download/";

    static readonly Regex NameRx = new("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);
    static readonly Regex DomainRx = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled);
    static readonly Regex RoutedRx = new(@"Added CNAME (\S+) which will route|(\S+) is already configured to route", RegexOptions.Compiled);
    static readonly Regex CreatedRx = new(@"Created tunnel udriver with id ([0-9a-fA-F-]{36})", RegexOptions.Compiled);
    static readonly Regex LoginUrlRx = new(@"https://dash\.cloudflare\.com/\S+", RegexOptions.Compiled);

    static string _busyState = "", _remoteError = "", _loginUrl = "";
    static bool _cancelled;
    static Process? _busyProc;
    static DateTime _onlineSince, _cfStarted;

    const string CodeAbc = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    static readonly List<(string Hash, DateTime Exp)> OneTime = new();

    // ---------------- one-time codes ----------------

    static string CodeKey(string code) => Hash(new string(code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()));

    static string NewCode()
    {
        var c = new char[8];
        for (int i = 0; i < c.Length; i++) c[i] = CodeAbc[RandomNumberGenerator.GetInt32(CodeAbc.Length)];
        string code = new string(c, 0, 4) + "-" + new string(c, 4, 4);
        lock (Gate)
        {
            OneTime.RemoveAll(x => x.Exp < DateTime.UtcNow);
            if (OneTime.Count >= 20) OneTime.RemoveAt(0);
            OneTime.Add((CodeKey(code), DateTime.UtcNow.AddMinutes(10)));
        }
        return code;
    }

    /// <summary>New agents may register only while a code issued on this computer is waiting to be used.</summary>
    static bool CodeLive()
    {
        lock (Gate) { OneTime.RemoveAll(x => x.Exp < DateTime.UtcNow); return OneTime.Count > 0; }
    }

    /// <summary>True once per issued code; every live code is compared, each in constant time.</summary>
    static bool TakeCode(string given)
    {
        byte[] key = Encoding.ASCII.GetBytes(CodeKey(given));
        int hit = -1;
        lock (Gate)
        {
            OneTime.RemoveAll(x => x.Exp < DateTime.UtcNow);
            for (int i = 0; i < OneTime.Count; i++)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(OneTime[i].Hash), key)) hit = i;
            if (hit >= 0) OneTime.RemoveAt(hit);
        }
        return hit >= 0;
    }

    // ---------------- cloudflared ----------------

    static string? OnPath(string exe) => Env("PATH").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d, exe)).FirstOrDefault(p => Path.IsPathFullyQualified(p) && File.Exists(p));

    static string? CfExe()
    {
        string set = Env("UDRIVER_CLOUDFLARED");
        if (Path.IsPathFullyQualified(set)) return File.Exists(set) ? set : null;
        string exe = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        return OnPath(exe) ?? new[]
        {
            "/opt/homebrew/bin/cloudflared", "/usr/local/bin/cloudflared", Path.Combine(Program.HomeDir, "bin", exe),
            Path.Combine(Env("LOCALAPPDATA"), "Microsoft", "WinGet", "Links", exe), Path.Combine(Env("ProgramFiles(x86)"), "cloudflared", exe), Path.Combine(Env("ProgramFiles"), "cloudflared", exe),
        }.FirstOrDefault(p => Path.IsPathFullyQualified(p) && File.Exists(p));
    }

    static string? Winget() => OperatingSystem.IsWindows() ? OnPath("winget.exe") : null;

    static Task<(int Code, string Out)> Cf(params string[] args) =>
        RunTool(CfExe() ?? throw new Exception("cloudflared is not installed."), TimeSpan.FromSeconds(90), args);

    /// <summary>The line of cloudflared output worth showing: its last error, else its last line, without the timestamp.</summary>
    static string Brief(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string l = lines.LastOrDefault(x => x.Contains(" ERR ")) ?? lines.LastOrDefault() ?? "no answer";
        l = Regex.Replace(l, @"^\S+Z (ERR|INF|WRN) ", "");
        return l.Length > 300 ? l[..300] : l;
    }

    static string Host() => Uri.TryCreate(GetSetting("UDRIVER_PUBLIC_URL") ?? "", UriKind.Absolute, out var u) ? u.Host : "";
    static bool RemoteSaved() => File.Exists(TunnelYml) && Host().Length > 0 && GetSetting("UDRIVER_REMOTE") != "off";

    static string? HostError(string host)
    {
        int dot = host.IndexOf('.');
        if (dot < 0) return "Enter the full address, for example udriver.example.com.";
        if (!NameRx.IsMatch(host[..dot])) return "The name can use only a to z, 0 to 9 and hyphens (up to 63), and cannot start or end with a hyphen.";
        if (host.Length > 253 || !DomainRx.IsMatch(host[(dot + 1)..])) return "This is not a domain name: " + host[(dot + 1)..];
        return null;
    }

    static bool Busy(string state, Func<Task> work)
    {
        lock (Gate)
        {
            if (_busyState.Length > 0) return false;
            _busyState = state; _remoteError = ""; _cancelled = false;
        }
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception e) { if (!_cancelled) _remoteError = e.Message; }
            finally { lock (Gate) { _busyState = ""; _loginUrl = ""; } }
        });
        return true;
    }

    static async Task Install()
    {
        if (Winget() is { } winget)
        {
            var r = await RunTool(winget, TimeSpan.FromMinutes(15), "install", "--id", "Cloudflare.cloudflared", "--exact", "--silent", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity");
            if (CfExe() == null) throw new Exception("cloudflared was not installed: " + Brief(r.Out));
            return;
        }
        string arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "arm64", Architecture.Arm => "arm", Architecture.X86 => "386", _ => "amd64" };
        string file = OperatingSystem.IsMacOS() ? $"cloudflared-darwin-{arch}.tgz" : OperatingSystem.IsWindows() ? $"cloudflared-windows-{(arch == "386" ? arch : "amd64")}.exe" : "cloudflared-linux-" + arch;
        string dir = Path.Combine(Program.HomeDir, "bin"), exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared"), part = exe + ".part";
        Directory.CreateDirectory(dir);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            await using var from = await http.GetStreamAsync((Env("UDRIVER_CLOUDFLARED_RELEASE") is { Length: > 0 } u ? u : CfRelease) + file);
            await using var to = File.Create(part);
            Stream? data = from;
            if (file.EndsWith(".tgz"))
            {
                var tar = new TarReader(new GZipStream(from, CompressionMode.Decompress));
                TarEntry? entry;
                do entry = await tar.GetNextEntryAsync(); while (entry != null && entry.Name != "cloudflared");
                data = entry?.DataStream;
            }
            await (data ?? throw new IOException("empty download")).CopyToAsync(to);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            File.Delete(part);
            throw new Exception("Could not download cloudflared. Check the internet connection and try again.");
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(part, exe, true);
    }

    static async Task Login()
    {
        string cf = CfExe() ?? throw new Exception("cloudflared is not installed.");
        string home = Path.Combine(CfHome, "login"), got = Path.Combine(home, ".cloudflared", "cert.pem");
        try { Directory.Delete(home, true); } catch { }
        Program.PrivateDir(CfHome); Program.PrivateDir(home);
        var psi = new ProcessStartInfo(cf) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in new[] { "tunnel", "login" }) psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = home;
        using var p = Process.Start(psi) ?? throw new Exception("could not start cloudflared");
        var log = new StringBuilder();
        void Line(string? l)
        {
            if (l == null) return;
            lock (log) log.AppendLine(l);
            if (LoginUrlRx.Match(l) is { Success: true } m) _loginUrl = m.Value;
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        lock (Gate) _busyProc = p;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        finally { lock (Gate) _busyProc = null; }
        if (!File.Exists(got)) throw new Exception("The Cloudflare sign-in did not finish: " + Brief(log.ToString()));
        Program.PrivateDir(Path.GetDirectoryName(OwnCert)!);
        File.Move(got, OwnCert, true);
        try { Directory.Delete(home, true); } catch { }
    }

    static async Task<string?> TunnelId(string cert)
    {
        var r = await Cf("tunnel", "--origincert", cert, "list");
        foreach (var line in r.Out.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "udriver" && Guid.TryParse(parts[0], out _)) return parts[0];
        }
        if (r.Code != 0) throw new Exception("Could not read your tunnels from Cloudflare: " + Brief(r.Out));
        return null;
    }

    /// <summary>Creates or reuses the "udriver" tunnel and links host to it. Nothing is saved unless Cloudflare linked exactly this host.</summary>
    static async Task Configure(string host)
    {
        string cert = Cert ?? throw new Exception("Sign in to Cloudflare first.");
        string? id = await TunnelId(cert);
        if (id == null)
        {
            var c = await Cf("tunnel", "--origincert", cert, "create", "udriver");
            id = CreatedRx.Match(c.Out) is { Success: true } m ? m.Groups[1].Value : throw new Exception("Could not create the tunnel: " + Brief(c.Out));
        }
        string cred = new[] { Path.GetDirectoryName(cert)!, Path.GetDirectoryName(OwnCert)!, SharedCfDir }.Select(d => Path.Combine(d, id + ".json")).FirstOrDefault(File.Exists) ?? "";
        if (cred.Length == 0)
        {
            cred = Path.Combine(Path.GetDirectoryName(OwnCert)!, id + ".json");
            Program.PrivateDir(CfHome); Program.PrivateDir(Path.GetDirectoryName(cred)!);
            var t = await Cf("tunnel", "--origincert", cert, "token", "--cred-file", cred, id);
            if (!File.Exists(cred)) throw new Exception("Could not get the key of your udriver tunnel: " + Brief(t.Out));
        }
        var route = await Cf("tunnel", "--origincert", cert, "route", "dns", id, host);
        var r = RoutedRx.Match(route.Out);
        string linked = !r.Success ? "" : r.Groups[1].Success ? r.Groups[1].Value : r.Groups[2].Value;
        if (!linked.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            if (linked.StartsWith(host + ".", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"You are signed in to Cloudflare with the domain {linked[(host.Length + 1)..]}, so Cloudflare created {linked} instead of {host}. Nothing was changed in UDriver. "
                                    + $"Delete {linked} under DNS in the Cloudflare dashboard, then use an address ending in .{linked[(host.Length + 1)..]} or pick another domain.");
            throw new Exception(route.Out.Contains("already exists")
                ? $"The address {host} is already used by another record in Cloudflare. Choose another name."
                : $"Could not link the address {host}: {Brief(route.Out)}");
        }
        Program.WritePrivate(TunnelYml, $"tunnel: {id}\ncredentials-file: {cred}\n");
        SetSetting("UDRIVER_HTTP_PORT", _port.ToString());
        SetSetting("UDRIVER_PUBLIC_URL", "https://" + host);
    }

    static async Task TurnOn(string? host)
    {
        if (host != null && (host != Host() || !File.Exists(TunnelYml))) await Configure(host);
        SetSetting("UDRIVER_REMOTE", null);
        _public = "https://" + Host();
        KillTunnel();
        StartTunnel();
        Notify("UDriver remote access is on: " + _public);
    }

    static void TurnOff(bool save)
    {
        _public = "";
        if (save) SetSetting("UDRIVER_REMOTE", "off");
        KillTunnel();
    }

    /// <summary>Forgets UDriver's own sign-in and the tunnel key it saved; a sign-in made outside UDriver stays.</summary>
    static string SignOut()
    {
        TurnOff(true);
        bool ours = File.Exists(OwnCert);
        string cfg = File.Exists(TunnelYml) ? File.ReadAllText(TunnelYml) : "";
        if (cfg.Contains("credentials-file: " + CfHome)) { File.Delete(TunnelYml); SetSetting("UDRIVER_PUBLIC_URL", null); }
        try { Directory.Delete(CfHome, true); } catch { }
        return ours || Cert == null ? "" : $"UDriver is using a Cloudflare sign-in made outside UDriver ({Path.Combine(SharedCfDir, "cert.pem")}). It was left in place.";
    }

    static void StartTunnel()
    {
        lock (Gate) { if (_cf is { HasExited: false }) return; CfConns.Clear(); _onlineSince = default; _cfStarted = DateTime.UtcNow; }
        try
        {
            // Everything the tunnel carries goes to this server: an address or port saved by another version or computer never ends in a 404.
            var keep = File.ReadAllLines(TunnelYml).TakeWhile(l => !l.StartsWith("ingress:", StringComparison.Ordinal));
            Program.WritePrivate(TunnelYml, string.Join("\n", keep) + $"\ningress:\n  - service: http://127.0.0.1:{_port}\n");
            var psi = new ProcessStartInfo(CfExe() ?? throw new Exception("cloudflared is not installed")) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
            foreach (var a in new[] { "tunnel", "--no-autoupdate", "--config", TunnelYml, "run", "udriver" }) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            void Line(string? l)
            {
                if (l == null) return;
                var m = ConnRx.Match(l);
                if (m.Success)
                    lock (Gate)
                    {
                        if (l.Contains("Registered tunnel connection")) { CfConns.Add(m.Groups[1].Value); if (_onlineSince == default) _onlineSince = DateTime.UtcNow; }
                        else if (l.Contains("Unregistered tunnel connection") || l.Contains("Connection terminated") || l.Contains("Lost connection")) CfConns.Remove(m.Groups[1].Value);
                        if (CfConns.Count == 0) _onlineSince = default;
                    }
                if (l.Contains(" ERR ")) _cfNote = Brief(l);
            }
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) => Line(e.Data);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                lock (Gate) { if (p != _cf) return; CfConns.Clear(); _onlineSince = default; }
                if (_cfNote.Length == 0) _cfNote = $"The tunnel stopped (cloudflared exited with code {p.ExitCode}).";
            };
            _cfNote = "";
            lock (Gate) _cf = p;
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
        }
        catch (Exception e) { _cfNote = "Could not start the tunnel: " + e.Message; }
    }

    static void KillTunnel()
    {
        Process? p;
        lock (Gate) { p = _cf; _cf = null; CfConns.Clear(); _onlineSince = default; }
        try { if (p is { HasExited: false }) { p.Kill(true); p.WaitForExit(3000); } } catch { }
    }

    static JsonObject RemoteJson()
    {
        bool alive, installed = CfExe() != null, saved = File.Exists(TunnelYml) && Host().Length > 0;
        int conns; DateTime since; string busy;
        lock (Gate) { alive = _cf is { HasExited: false }; conns = CfConns.Count; since = _onlineSince; busy = _busyState; }
        bool on = _public.Length > 0;
        string state = busy.Length > 0 ? busy
            : on ? (alive && conns > 0 ? "online" : alive ? "starting" : "error")
            : _remoteError.Length > 0 ? "error"
            : !installed ? "no-cloudflared" : saved ? "off" : Cert == null ? "signed-out" : "ready";
        string host = Host();
        return new JsonObject
        {
            ["state"] = state, ["on"] = on, ["error"] = _remoteError.Length > 0 ? _remoteError : on && !(alive && conns > 0) ? _cfNote : "",
            ["cloudflared"] = installed, ["installer"] = installed ? null : Winget() != null ? "winget" : "download",
            ["signedIn"] = Cert != null, ["ownSignIn"] = File.Exists(OwnCert), ["saved"] = saved,
            ["host"] = host, ["domain"] = host.Contains('.') ? host[(host.IndexOf('.') + 1)..] : "", ["address"] = host.Length > 0 ? "https://" + host + "/mcp" : "",
            ["onlineSec"] = since == default ? null : (int)(DateTime.UtcNow - since).TotalSeconds,
            ["loginUrl"] = _loginUrl,
        };
    }

    /// <summary>POST /panel/api/remote/*; false when the path is not one of them.</summary>
    static async Task<bool> RemoteAction(HttpContext ctx, string path, Dictionary<string, string> form, bool local)
    {
        JsonObject ok = new() { ["ok"] = true };
        JsonObject? reply = null;
        string? error = null;
        const string busy = "UDriver is still busy with Cloudflare. Wait a moment and try again.";
        switch (path)
        {
            case "/panel/api/remote/code":
                if (!local) { await Json(ctx, 403, Fail(LocalOnly)); return true; }
                if (_public.Length == 0) error = "Turn on remote access first.";
                else reply = new JsonObject { ["ok"] = true, ["code"] = NewCode(), ["address"] = _public + "/mcp", ["panel"] = _public + "/panel", ["expiresSec"] = 600 };
                break;
            case "/panel/api/remote/off":
                TurnOff(true); reply = ok;
                break;
            case "/panel/api/remote/restart":
                if (_public.Length == 0) error = "Remote access is off.";
                else { KillTunnel(); StartTunnel(); reply = ok; }
                break;
            case "/panel/api/remote/install" when local:
                error = CfExe() != null ? "cloudflared is already installed." : Busy("installing", Install) ? null : busy;
                break;
            case "/panel/api/remote/login" when local:
                error = CfExe() == null ? "Install cloudflared first." : Busy("signing-in", Login) ? null : busy;
                break;
            case "/panel/api/remote/cancel" when local:
                lock (Gate) { _cancelled = true; try { _busyProc?.Kill(true); } catch { } }
                reply = ok;
                break;
            case "/panel/api/remote/signout" when local:
                if (_busyState.Length > 0) error = busy;
                else reply = new JsonObject { ["ok"] = true, ["note"] = SignOut() };
                break;
            case "/panel/api/remote/on" when local:
                string host = form.GetValueOrDefault("host", "").Trim().ToLowerInvariant();
                error = host.Length > 0 ? HostError(host) : File.Exists(TunnelYml) && Host().Length > 0 ? null : "Choose the address first.";
                if (error == null && CfExe() == null) error = "Install cloudflared first.";
                if (error != null) break;
                error = Busy("setting-up", () => TurnOn(host.Length > 0 ? host : null)) ? null : busy;
                break;
            default:
                return false;
        }
        await Json(ctx, error == null ? 200 : 400, error == null ? reply ?? ok : Fail(error));
        return true;
    }
}

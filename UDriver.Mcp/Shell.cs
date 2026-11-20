using System.Diagnostics;
using System.Runtime.InteropServices;
using UDriver;

/// <summary>
/// The operating system's wall around a game UDriver starts while Computer access is off: writes only to the game folder,
/// its saves, logs and temp folders and UDriver's exports; reads only those plus system and font files and UDriver's list of
/// games; no other programs; network open except UDriver's own ports. macOS: a Seatbelt profile; Linux: Landlock. Both are set by this program
/// just before it becomes the game. Windows has none: an AppContainer cannot accept UDriver's loopback connection without an administrator.
/// </summary>
static class Shell
{
    internal const string Mark = "walled off by the operating system";

    /// <summary>Ports of UDriver itself (panel and server, Unity editors): closed to a game in the shell.</summary>
    static IEnumerable<int> Closed()
    {
        var ports = new SortedSet<int> { 8787 };
        foreach (var v in new[] { "UDRIVER_HTTP_PORT", "UDRIVER_EDITOR_PORT" })
            if (int.TryParse(Environment.GetEnvironmentVariable(v), out int p)) ports.Add(p);
        for (int p = ToolManifest.EditorPort; p <= ToolManifest.EditorPortLast; p++) ports.Add(p);
        return ports;
    }

    static string Real(string path) => Program.RealPath(path).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Company and product from the game's app.info; they name its save and log folders.</summary>
    static (string Company, string Product) Names(string data)
    {
        try { var l = File.ReadAllLines(Path.Combine(data, "app.info")); if (l.Length >= 2 && l[0].Trim().Length > 0 && l[1].Trim().Length > 0) return (l[0].Trim(), l[1].Trim()); } catch { }
        return ("", "");
    }

    /// <summary>Starts the game inside the shell; null when this computer cannot put it there (the game then starts without it).</summary>
    internal static Process? Start(ProcessStartInfo psi, string exe, string log, string home)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, "");
            Program.PrivateDir(Path.Combine(Program.HomeDir, "exports"));
            psi.Environment["UDRIVER_SHELL"] = home;
            if (OperatingSystem.IsMacOS()) return Mac(psi, exe, log);
            if (OperatingSystem.IsLinux()) return Linux(psi, exe, log);
        }
        catch (Exception e) { Console.Error.WriteLine("[udriver-mcp] the computer shell could not be set up: " + e.Message); }
        psi.Environment.Remove("UDRIVER_SHELL");
        return null;
    }

    /// <summary>Asks the operating system whether the process is inside the shell.</summary>
    internal static bool Active(int pid)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return SandboxDenies(pid, "file-read-data", Real(Program.HomeDir)) && SandboxDenies(pid, "file-write-create", Real(Program.HomeDir));
            if (OperatingSystem.IsLinux())
            {
                string s = File.ReadAllText($"/proc/{pid}/status");
                return s.Contains("\nNoNewPrivs:\t1") && s.Contains("\nSeccomp:\t2");
            }
        }
        catch { }
        return false;
    }

    // ---------------- macOS ----------------

    const string MacProfile = """
        (version 1)
        (allow default)
        {CLOSED}
        (deny process-exec*)
        (allow process-exec (literal (param "EXE")))
        (deny lsopen)
        (deny appleevent-send)
        (deny signal (target others))
        {STEAM}
        (deny mach-lookup
          (global-name "com.apple.pasteboard.1") (global-name "com.apple.pboard")
          (global-name "com.apple.SecurityServer") (global-name-regex #"^com\.apple\.securityd") (global-name-regex #"^com\.apple\.ctkd")
          (global-name "com.apple.security.agent") (global-name "com.apple.security.KeychainStasher") (global-name "com.apple.security.kcsharing")
          (global-name "com.apple.security.cloudkeychainproxy3") (global-name "com.apple.security.octagon") (global-name "com.apple.keychainsharingmessagingd"))
        (deny file-write*)
        (allow file-write*
          (subpath (param "GAME")) (subpath (param "SAVE")) (subpath (param "SAVEID")) (subpath (param "LOGS")) (subpath (param "TEMP")) (subpath (param "CACHE")) (subpath (param "CACHES"))
          (subpath (param "EXPORTS")) (literal (param "LOG")) (literal "/dev/null") (literal "/dev/dtracehelper") (regex #"^/dev/tty") (regex #"^/private/tmp/mm-exhelper\.dylib\."))
        (deny file-read*)
        (allow file-read*
          (subpath (param "GAME")) (subpath (param "GAMEDIR")) (subpath (param "WORKSHOP")) (subpath (param "STEAMCLIENT")) (literal (param "STEAMREG")) (subpath (param "SAVE")) (subpath (param "SAVEID")) (subpath (param "LOGS")) (subpath (param "TEMP")) (subpath (param "CACHE")) (subpath (param "CACHES")) (subpath (param "SERVER"))
          (subpath (param "EXPORTS")) (literal (param "TARGETS")) (literal (param "LOG")) (literal (param "HARMONY")) (regex #"^/private/tmp/mm-exhelper\.dylib\.") (literal "/") (subpath "/System") (subpath "/usr/lib") (subpath "/usr/share") (subpath "/Library/Fonts")
          (subpath "/Library/Apple/System") (subpath "/private/var/db/timezone") (subpath "/private/etc") (subpath "/dev")
          (subpath (param "METAL")) (subpath (string-append (param "HOME") "/Library/Fonts"))
          (subpath (string-append (param "HOME") "/Library/Keyboard Layouts")) (subpath (string-append (param "HOME") "/Library/Input Methods")))
        (allow file-read-metadata)
        (deny file-read-metadata (subpath (param "HOME")))
        (allow file-read-metadata (literal (param "HOME")) (subpath (param "GAME")) (subpath (param "GAMEDIR")) (literal (param "STEAM")) (literal (param "STEAMAPPS")) (literal (param "STEAMCOMMON")) (subpath (param "WORKSHOP")) (subpath (param "STEAMCLIENT")) (literal (param "STEAMREG")) (subpath (param "SERVER")) (subpath (param "EXPORTS")) (literal (param "TARGETS")) (subpath (param "SAVE")) (subpath (param "SAVEID")) (subpath (param "LOGS")) (literal (param "LOG")) (literal (param "SAVEUP")) (literal (param "LOGSUP"))
          (literal (string-append (param "HOME") "/Library")) (literal (string-append (param "HOME") "/Library/Application Support"))
          (literal (string-append (param "HOME") "/Library/Logs")) (literal (string-append (param "HOME") "/Library/Caches")) (subpath (param "CACHES")) (subpath (string-append (param "HOME") "/Library/Fonts")))
        """;

    // Not /usr/bin/sandbox-exec: macOS strips DYLD_INSERT_LIBRARIES from its own programs, so an IL2CPP game would lose its agent.
    static Process? Mac(ProcessStartInfo psi, string exe, string log)
    {
        if (MacDirs(exe, log) is not { } dirs) return null;
        foreach (var k in new[] { "SAVE", "LOGS", "TEMP", "CACHE", "CACHES" }) Directory.CreateDirectory(dirs[k]);
        return Helper(psi, exe, log);
    }

    /// <summary>The folders the profile opens to the game; null when the game has no company, product or bundle id to name them.</summary>
    static Dictionary<string, string>? MacDirs(string exe, string log)
    {
        string app = Real(Path.Combine(exe, "..", "..", ".."));
        var (company, product) = Names(Path.Combine(app, "Contents", "Resources", "Data"));
        string id = Inject.PlistValue(app, "CFBundleIdentifier");
        if (company.Length == 0 || id.Length == 0 || Path.GetFileName(id) != id || id.StartsWith('.')) return null;
        // Unity takes the home folder from the user database, not from HOME.
        string home = Real(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(getpwuid(getuid()), 48))!);
        // A Steam game reads its install folder and its Workshop items and reaches the Steam client; other games get nothing of Steam (the names fall back to the game itself).
        string dir = Path.GetDirectoryName(app)!, steam = Path.Combine(home, "Library", "Application Support", "Steam");
        bool fromSteam = Path.GetFileName(Path.GetDirectoryName(dir)) == "common" && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir))) == "steamapps";
        string temp = Real(Path.GetTempPath()), cache = Path.Combine(Path.GetDirectoryName(temp)!, "C");
        return new Dictionary<string, string>
        {
            ["GAME"] = app, ["HOME"] = home, ["EXE"] = Real(exe), ["LOG"] = Real(log), ["HARMONY"] = Path.Combine(Real(Program.HomeDir), "0Harmony.dll"),
            ["EXPORTS"] = Path.Combine(Real(Program.HomeDir), "exports"), ["TARGETS"] = Path.Combine(Real(Program.HomeDir), "targets.json"),
            ["SERVER"] = Real(AppContext.BaseDirectory), ["GAMEDIR"] = fromSteam ? dir : app,
            ["STEAM"] = fromSteam ? steam : app, ["STEAMCOMMON"] = fromSteam ? Path.GetDirectoryName(dir)! : app, ["STEAMAPPS"] = fromSteam ? Path.GetDirectoryName(Path.GetDirectoryName(dir))! : app,
            ["WORKSHOP"] = fromSteam && Inject.SteamAppId(exe) is { } steamId ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, "workshop", "content", steamId) : app, ["STEAMCLIENT"] = fromSteam ? Path.Combine(steam, "Steam.AppBundle") : app, ["STEAMREG"] = fromSteam ? Path.Combine(steam, "registry.vdf") : app,
            ["SAVE"] = Path.Combine(home, "Library", "Application Support", company, product),
            ["LOGS"] = Path.Combine(home, "Library", "Logs", company, product),
            // Unity games also keep files under their bundle id.
            ["SAVEID"] = Path.Combine(home, "Library", "Application Support", id),
            ["SAVEUP"] = Path.Combine(home, "Library", "Application Support", company), ["LOGSUP"] = Path.Combine(home, "Library", "Logs", company),
            ["TEMP"] = Path.Combine(temp, company, product), ["CACHE"] = Path.Combine(cache, id), ["METAL"] = Path.Combine(cache, "com.apple.metal"),
            // Unity's asset bundle cache (Caching) and the system's URL cache.
            ["CACHES"] = Path.Combine(home, "Library", "Caches", id),
        };
    }

    [DllImport("libsandbox.1.dylib")] static extern int sandbox_init_with_parameters(string profile, ulong flags, string?[] parameters, out IntPtr error);
    [DllImport("libc")] static extern IntPtr getpwuid(uint uid);
    [DllImport("libc")] static extern uint getuid();
    [DllImport("libc", EntryPoint = "sandbox_check")]
    static extern int SandboxCheckArm(int pid, string op, int type, long r3, long r4, long r5, long r6, long r7, string path);
    [DllImport("libc", EntryPoint = "sandbox_check")]
    static extern int SandboxCheckX64(int pid, string op, int type, string path);

    /// <summary>sandbox_check is variadic: on Apple arm64 the path goes on the stack, after the eight registers.</summary>
    static bool SandboxDenies(int pid, string op, string path) =>
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? SandboxCheckArm(pid, op, 1, 0, 0, 0, 0, 0, path) : SandboxCheckX64(pid, op, 1, path)) == 1;

    // ---------------- Linux ----------------

    [DllImport("libc", SetLastError = true)] static extern long syscall(long n, IntPtr a, nint b, uint c);
    [DllImport("libc", SetLastError = true)] static extern long syscall(long n, int a, int b, IntPtr c, uint d);
    [DllImport("libc", SetLastError = true)] static extern long syscall(long n, int a, uint b);
    [DllImport("libc", SetLastError = true)] static extern int prctl(int op, nint a, nint b, nint c, nint d);
    [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern int execve(string path, string?[] argv, string?[] envp);

    const long CreateRuleset = 444, AddRule = 445, RestrictSelf = 446;
    const ulong Execute = 1, WriteFile = 2, ReadFile = 4, ReadDir = 8, Truncate = 1 << 14, AllFs = (1 << 15) - 1, ConnectTcp = 2;

    static int Abi() => (int)syscall(CreateRuleset, IntPtr.Zero, 0, 1);

    static Process? Linux(ProcessStartInfo psi, string exe, string log)
    {
        if (Abi() < 4) return null;
        return Helper(psi, exe, log);
    }

    /// <summary>Starts this program as shell-exec, which closes itself in and becomes the game.</summary>
    static Process? Helper(ProcessStartInfo psi, string exe, string log)
    {
        // The IL2CPP agent goes to the game only, not into this program on the way.
        foreach (var v in new[] { "DYLD_INSERT_LIBRARIES", "LD_PRELOAD" })
            if (psi.Environment.TryGetValue(v, out var lib) && lib != null) { psi.Environment.Remove(v); psi.Environment["UDRIVER_PRELOAD"] = lib; }
        string self = Environment.ProcessPath!;
        var pre = new List<string>();
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") pre.Add(typeof(Shell).Assembly.Location);
        pre.AddRange(new[] { "shell-exec", string.Join(",", Closed()), log, exe });
        for (int i = 0; i < pre.Count; i++) psi.ArgumentList.Insert(i, pre[i]);
        psi.FileName = self;
        return Process.Start(psi);
    }

    /// <summary>udriver-mcp shell-exec PORTS LOG EXE ARGS...: closes this process in and becomes the game. Exits 126 when it cannot.</summary>
    internal static int Exec(string[] a)
    {
        var closed = a[1].Split(',').Select(int.Parse).ToHashSet();
        string log = a[2], exe = Path.GetFullPath(a[3]), dir = Path.GetDirectoryName(exe)!;
        if (Environment.GetEnvironmentVariable("UDRIVER_PRELOAD") is { Length: > 0 } lib)
            Environment.SetEnvironmentVariable(OperatingSystem.IsMacOS() ? "DYLD_INSERT_LIBRARIES" : "LD_PRELOAD", lib);
        Environment.SetEnvironmentVariable("UDRIVER_PRELOAD", null);
        if (OperatingSystem.IsMacOS())
        {
            if (MacDirs(exe, log) is not { } dirs) return Fail("no company, product or bundle id in the game");
            // Temporary files of the game's libraries go to its own temp folder, the one place under the system's temp it may write.
            Environment.SetEnvironmentVariable("TMPDIR", dirs["TEMP"] + "/");
            string profile = MacProfile.Replace("{CLOSED}", string.Join("\n", closed.Select(p => $"(deny network-outbound (remote tcp \"localhost:{p}\"))")))
                // The Steam library checks that Steam runs with kill(pid, 0) and refuses to start the game when that fails.
                .Replace("{STEAM}", dirs["STEAM"] != dirs["GAME"] ? "(allow signal (target others))" : "");
            var macArgv = a.Skip(3).Append(null).ToArray();
            var macEnvp = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(e => e.Key + "=" + e.Value).Append(null).ToArray();
            if (sandbox_init_with_parameters(profile, 0, dirs.SelectMany(d => new string?[] { d.Key, d.Value }).Append(null).ToArray(), out var err) != 0)
                return Fail("sandbox " + (err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) : ""));
            execve(exe, macArgv, macEnvp);
            return Fail("exec " + Marshal.GetLastPInvokeError());
        }
        var (company, product) = Names(Path.ChangeExtension(exe, null) + "_Data");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string save = Path.Combine(home, ".config", "unity3d", company, product), temp = Path.Combine(Path.GetTempPath(), company, product);
        if (company.Length == 0) return Fail("no company and product in the game's app.info");
        Directory.CreateDirectory(save); Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TMPDIR", temp);

        var attr = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(attr, (long)AllFs); Marshal.WriteInt64(attr, 8, (long)ConnectTcp);
        int rs = (int)syscall(CreateRuleset, attr, 16, 0);
        if (rs < 0) return Fail("Landlock ruleset " + Marshal.GetLastPInvokeError());
        var rule = Marshal.AllocHGlobal(16);
        void Allow(string path, ulong access, bool file = false)
        {
            if (file ? !File.Exists(path) : !Directory.Exists(path)) return;
            int fd = open(path, 0x200000 | 0x80000);
            if (fd < 0) return;
            Marshal.WriteInt64(rule, (long)access); Marshal.WriteInt32(rule, 8, fd);
            long r = syscall(AddRule, rs, 1, rule, 0);
            close(fd);
            if (r < 0) throw new IOException($"Landlock rule {path}: {Marshal.GetLastPInvokeError()}");
        }
        const ulong read = ReadFile | ReadDir, rw = AllFs & ~Execute;
        foreach (var p in new[] { "/usr", "/lib", "/lib64", "/lib32", "/etc", "/proc", "/sys", "/opt", "/var/cache/fontconfig",
                     AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location)!, Path.Combine(home, ".fonts"), Path.Combine(home, ".local", "share", "fonts"), Path.Combine(home, ".cache", "fontconfig") })
            Allow(p, read);
        foreach (var p in new[] { dir, save, temp, "/dev" }) Allow(p, rw);
        Allow(log, WriteFile | ReadFile | Truncate, true);
        Allow(Path.Combine(Program.HomeDir, "0Harmony.dll"), ReadFile, true);
        Allow(Path.Combine(Program.HomeDir, "targets.json"), ReadFile, true);
        Allow(Path.Combine(Program.HomeDir, "exports"), rw);
        if (Environment.GetEnvironmentVariable("XAUTHORITY") is { Length: > 0 } xa) Allow(xa, ReadFile, true);
        Allow(exe, Execute | ReadFile, true);
        foreach (var ld in new[] { "/lib/ld-linux-aarch64.so.1", "/lib64/ld-linux-x86-64.so.2" })
            if (File.Exists(ld)) Allow(new FileInfo(ld).ResolveLinkTarget(true)?.FullName ?? ld, Execute | ReadFile, true);
        // Interpreters of foreign binaries (Rosetta, qemu) that run the game.
        foreach (var f in Directory.Exists("/proc/sys/fs/binfmt_misc") ? Directory.GetFiles("/proc/sys/fs/binfmt_misc").Where(f => Path.GetFileName(f) is not ("register" or "status")) : [])
            foreach (var l in File.ReadLines(f).Where(l => l.StartsWith("interpreter ", StringComparison.Ordinal))) Allow(l[12..], Execute | ReadFile, true);
        for (int port = 1; port <= 65535; port++)
        {
            if (closed.Contains(port)) continue;
            Marshal.WriteInt64(rule, (long)ConnectTcp); Marshal.WriteInt64(rule, 8, port);
            if (syscall(AddRule, rs, 2, rule, 0) < 0) return Fail("Landlock port rule " + Marshal.GetLastPInvokeError());
        }
        var argv = a.Skip(3).Append(null).ToArray();
        var envp = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(e => e.Key + "=" + e.Value).Append(null).ToArray();
        if (prctl(38, 1, 0, 0, 0) != 0) return Fail("no_new_privs");
        if (syscall(RestrictSelf, rs, 0) < 0) return Fail("Landlock restrict " + Marshal.GetLastPInvokeError());
        // A one-instruction seccomp filter that allows everything: it marks the process for Active().
        var filter = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(filter, 0x7fff0000L << 32 | 0x06);
        var prog = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(prog, 1); Marshal.WriteIntPtr(prog, 8, filter);
        if (prctl(22, 2, prog, 0, 0) != 0) return Fail("seccomp");
        execve(exe, argv, envp);
        return Fail("exec " + Marshal.GetLastPInvokeError());
    }

    static int Fail(string why) { Console.Error.WriteLine("[udriver-mcp] shell: " + why); return 126; }
}

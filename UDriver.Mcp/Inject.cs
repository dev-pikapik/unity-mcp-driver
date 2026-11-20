using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

/// <summary>Puts the UDriver agent into a built Mono game that does not include the package, and takes it out again.</summary>
static class Inject
{
    internal const string Il2Cpp = "This game was built with IL2CPP. UDriver can control it only if the game includes the UDriver package.";
    const string Dll = "UDriver.Runtime.dll", Json = "Newtonsoft.Json.dll", Harmony = "0Harmony.dll";
    static readonly Assembly Self = typeof(Inject).Assembly;
    static readonly string Stamp = Convert.ToHexString(SHA256.HashData(Self.GetManifestResourceNames().Order()
        .SelectMany(n => { using var s = Self.GetManifestResourceStream(n)!; using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }).ToArray()))[..16];

    /// <summary>Game file for a game folder, a .app or the game file itself.</summary>
    internal static string GameExe(string path)
    {
        string real = Program.RealPath(path.TrimEnd('/', '\\'));
        if (real.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(real))
            return Directory.GetFiles(Path.Combine(real, "Contents", "MacOS")).FirstOrDefault() ?? real;
        return Directory.Exists(real) ? Program.GameFile(real) ?? real : real;
    }

    static string? AppDir(string exe)
    {
        int i = exe.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return i > 0 ? exe[..(i + 4)] : null;
    }

    static string DataDir(string exe) => AppDir(exe) is string app
        ? Path.Combine(app, "Contents", "Resources", "Data")
        : Regex.Replace(exe, @"\.(exe|x86_64|x86)$", "", RegexOptions.IgnoreCase) + "_Data";

    static string BackupDir(string data) =>
        Path.Combine(Program.HomeDir, "backups", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..16].ToLowerInvariant());

    static bool IsIl2Cpp(string exe, string data) =>
        !Directory.Exists(Path.Combine(data, "Managed")) || Directory.Exists(Path.Combine(data, "il2cpp_data"))
        || new[] { "GameAssembly.dll", "GameAssembly.so", Path.Combine("Contents", "Frameworks", "GameAssembly.dylib") }.Any(f => File.Exists(Path.Combine(AppDir(exe) ?? Path.GetDirectoryName(exe)!, f)));

    static Match UnityVersion(string data)
    {
        string ggm = Path.Combine(data, "globalgamemanagers");
        var head = new byte[4096];
        if (File.Exists(ggm)) { using var f = File.OpenRead(ggm); f.ReadAtLeast(head, head.Length, false); }
        return Regex.Match(Encoding.Latin1.GetString(head), @"(\d{4})\.(\d+)\.\d+[abfp]\d+");
    }

    /// <summary>"Unity 6000.0.23f1 · Mono" for a build, "Unity 6000.0.23f1" for a project; "" when it cannot be read.</summary>
    internal static string Engine(Registry.Game g)
    {
        try
        {
            if (g.Kind == "project")
            {
                var p = Regex.Match(File.ReadAllText(Path.Combine(g.Path, "ProjectSettings", "ProjectVersion.txt")), @"m_EditorVersion:\s*(\S+)");
                return p.Success ? "Unity " + p.Groups[1].Value : "";
            }
            string exe = GameExe(g.Path), data = DataDir(exe);
            var v = UnityVersion(data);
            return v.Success ? $"Unity {v.Value} · {(IsIl2Cpp(exe, data) ? "IL2CPP" : "Mono")}" : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Why this build's own code could get past UDriver's in-process wall; "" when it cannot.</summary>
    internal static string Native(Registry.Game g)
    {
        if (g.Kind != "build") return "";
        try
        {
            string exe = GameExe(g.Path), data = DataDir(exe);
            if (!Directory.Exists(data)) return "its files cannot be found at " + g.Path;
            return IsIl2Cpp(exe, data) ? "it is built with IL2CPP, so all its code is native" : Eval.Native(Path.Combine(data, "Managed"));
        }
        catch (Exception e) { return "its code cannot be read: " + e.Message; }
    }

    /// <summary>Makes sure the game at this game file has the agent; an error text when it cannot.
    /// An IL2CPP game is not changed: lib is the native agent to load into it at launch.</summary>
    internal static string? Ensure(string exe, out string? lib, bool computer = true)
    {
        lib = null;
        string data = DataDir(exe), managed = Path.Combine(data, "Managed"), bak = BackupDir(data);
        string list = Path.Combine(data, "ScriptingAssemblies.json");
        var manifest = File.Exists(Path.Combine(bak, "manifest.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(bak, "manifest.json"))) : null;
        if (manifest != null)
        {
            if ((string?)manifest["stamp"] == Stamp) return null;
            Undo(exe);
        }
        else if (File.Exists(list) && File.ReadAllText(list).Contains('"' + Dll + '"')) return null;
        string root = AppDir(exe) ?? Path.GetDirectoryName(exe)!;
        if (IsIl2Cpp(exe, data)) return Preload(exe, out lib);
        if (!File.Exists(list)) return "This game is missing " + list + ", so UDriver cannot be added to it.";
        if (!computer && UnityVersion(data) is { Success: true } v && int.Parse(v.Groups[1].Value) < 6000)
            return $"Not done: this game was made with Unity {v.Value}, and UDriver keeps agents away from the computer only in games made with Unity 6 or newer. Adding UDriver to an older game needs Computer access, which only the owner can turn on, on the computer that runs UDriver.";

        var own = new[] { Json, Harmony }.Where(n => !File.Exists(Path.Combine(managed, n))).ToArray();
        byte[] dll;
        try { dll = Compile(exe, managed, own.Contains(Json)); }
        catch (Exception e) { return "UDriver could not be added to this game: " + e.Message; }

        string loads = Path.Combine(data, "RuntimeInitializeOnLoads.json");
        var touched = new List<string> { list, loads, Path.Combine(managed, Dll) };
        touched.AddRange(own.Select(n => Path.Combine(managed, n)));
        bool sign = OperatingSystem.IsMacOS() && AppDir(exe) != null;
        if (sign)
        {
            touched.AddRange(Directory.GetFiles(Path.Combine(root, "Contents", "MacOS")));
            touched.Add(Path.Combine(root, "Contents", "_CodeSignature", "CodeResources"));
        }

        Directory.CreateDirectory(Path.Combine(bak, "files"));
        var changed = new JsonArray(); var added = new JsonArray();
        foreach (var f in touched)
        {
            string rel = Path.GetRelativePath(root, f);
            if (File.Exists(f)) { File.Copy(f, Path.Combine(bak, "files", Convert.ToHexString(Encoding.UTF8.GetBytes(rel))), true); changed.Add(rel); }
            else added.Add(rel);
        }
        File.WriteAllText(Path.Combine(bak, "manifest.json"), new JsonObject { ["stamp"] = Stamp, ["root"] = root, ["changed"] = changed, ["added"] = added }.ToJsonString());

        try
        {
            File.WriteAllBytes(Path.Combine(managed, Dll), dll);
            foreach (var n in own) File.WriteAllBytes(Path.Combine(managed, n), Resource(n));

            var sa = JsonNode.Parse(File.ReadAllText(list))!;
            var names = sa["names"]!.AsArray(); var types = sa["types"]!.AsArray();
            foreach (var n in own.Append(Dll))
                if (!names.Any(x => (string?)x == n)) { names.Add(n); types.Add(16); }
            File.WriteAllText(list, sa.ToJsonString());

            var rl = File.Exists(loads) ? JsonNode.Parse(File.ReadAllText(loads))! : new JsonObject { ["root"] = new JsonArray() };
            rl["root"]!.AsArray().Insert(0, new JsonObject { ["assemblyName"] = "UDriver.Runtime", ["nameSpace"] = "UDriver", ["className"] = "Sandbox", ["methodName"] = "Early", ["loadTypes"] = 4, ["isUnityClass"] = false });
            rl["root"]!.AsArray().Add(new JsonObject { ["assemblyName"] = "UDriver.Runtime", ["nameSpace"] = "UDriver", ["className"] = "Driver", ["methodName"] = "Boot", ["loadTypes"] = 1, ["isUnityClass"] = false });
            File.WriteAllText(loads, rl.ToJsonString());

            if (sign)
            {
                using var p = Process.Start(new ProcessStartInfo("/usr/bin/codesign", new[] { "-f", "-s", "-", root }) { RedirectStandardError = true, RedirectStandardOutput = true })!;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("codesign failed: " + err.Trim());
            }
        }
        catch (Exception e) { Undo(exe); return "UDriver could not be added to this game: " + e.Message; }
        Eval.Native(managed);
        return null;
    }

    static string? Preload(string exe, out string? lib)
    {
        lib = null;
        if (OperatingSystem.IsWindows()) return Il2Cpp;
        string file = OperatingSystem.IsMacOS() ? "udriver-il2cpp.dylib" : "libudriver-il2cpp.so";
        // Installed next to the server; in a development run, in the repository's native folder.
        string? found = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native") }
            .Select(d => Path.GetFullPath(Path.Combine(d, file))).FirstOrDefault(File.Exists);
        if (found == null) return $"This game was built with IL2CPP. UDriver needs its agent for such games ({file}), but it is missing from {AppContext.BaseDirectory}. Reinstall UDriver.";
        if (OperatingSystem.IsMacOS() && Hardened(AppDir(exe) ?? exe))
            return "This game was built with IL2CPP and signed with Apple's hardened runtime, which does not let UDriver load its agent into it. " +
                   "Build the game again without hardened runtime (Unity's own builds are fine), or include the UDriver package in the game.";
        lib = found;
        return null;
    }

    /// <summary>True when macOS will drop DYLD_INSERT_LIBRARIES or refuse an ad-hoc signed library for this program.</summary>
    static bool Hardened(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/codesign", new[] { "-d", "--verbose=1", "--entitlements", "-", "--xml", path }) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            var err = p.StandardError.ReadToEndAsync();
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            bool On(string key) => Regex.IsMatch(xml, @"<key>com\.apple\.security\.cs\." + key + @"</key>\s*<true\s*/>");
            return Regex.IsMatch(err.Result, @"flags=0x[0-9a-f]+\([^)]*\bruntime\b") && !(On("allow-dyld-environment-variables") && On("disable-library-validation"));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Puts back every file that Ensure changed, byte for byte.</summary>
    internal static string Undo(string exe) => Restore(BackupDir(DataDir(exe)));

    static string Restore(string bak)
    {
        string mf = Path.Combine(bak, "manifest.json");
        if (!File.Exists(mf)) return "UDriver has not changed this game, so there is nothing to restore.";
        var m = JsonNode.Parse(File.ReadAllText(mf))!;
        string root = (string)m["root"]!;
        foreach (var rel in m["changed"]!.AsArray())
            File.Copy(Path.Combine(bak, "files", Convert.ToHexString(Encoding.UTF8.GetBytes((string)rel!))), Path.Combine(root, (string)rel!), true);
        foreach (var rel in m["added"]!.AsArray()) File.Delete(Path.Combine(root, (string)rel!));
        Directory.Delete(bak, true);
        return "The original game files are back.";
    }

    internal static byte[] Resource(string name)
    {
        using var s = Self.GetManifestResourceStream(name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static byte[] Compile(string exe, string managed, bool ownJson)
    {
        var defines = new List<string> { "UDRIVER_FORCE", "ENABLE_MONO", "UNITY_STANDALONE" };
        defines.Add(AppDir(exe) != null ? "UNITY_STANDALONE_OSX" : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "UNITY_STANDALONE_WIN" : "UNITY_STANDALONE_LINUX");
        if (File.Exists(Path.Combine(managed, "UnityEngine.UI.dll"))) defines.Add("UDRV_UGUI");
        if (File.Exists(Path.Combine(managed, "Unity.InputSystem.dll"))) defines.AddRange(new[] { "UDRV_INPUTSYSTEM", "ENABLE_INPUT_SYSTEM" });
        var v = UnityVersion(Path.GetDirectoryName(managed)!);
        if (!v.Success) throw new Exception("the Unity version of this game could not be read.");
        int major = int.Parse(v.Groups[1].Value), minor = int.Parse(v.Groups[2].Value);
        if (major < 2019 || major == 2019 && minor < 4) throw new Exception($"it was made with Unity {v.Value}, and UDriver needs Unity 2019.4 or newer.");
        for (int y = 2019; y <= Math.Min(major, 2023); y++)
            for (int m = 1; m <= (y < major ? 4 : minor); m++) defines.Add($"UNITY_{y}_{m}_OR_NEWER");
        for (int m = 0; major >= 6000 && m <= (major == 6000 ? minor : 9); m++) defines.Add($"UNITY_6000_{m}_OR_NEWER");

        var opts = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines);
        var trees = Self.GetManifestResourceNames().Where(n => n.StartsWith("rt/"))
            .Select(n => CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(Resource(n)), opts, n, Encoding.UTF8));
        var refs = Directory.GetFiles(managed, "*.dll").Where(f => Path.GetFileName(f) != Dll)
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
        if (ownJson) refs.Add(MetadataReference.CreateFromImage(Resource(Json)));
        var c = CSharpCompilation.Create("UDriver.Runtime", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var ms = new MemoryStream();
        var r = c.Emit(ms, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!r.Success)
            throw new Exception($"the agent does not build against this game's Unity {v.Value}: " + string.Join("; ", r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)));
        return ms.ToArray();
    }

    /// <summary>Restores every game that has a backup; 1 when any of them could not be restored.</summary>
    static int RestoreAll()
    {
        string dir = Path.Combine(Program.HomeDir, "backups");
        int failed = 0;
        foreach (var bak in Directory.Exists(dir) ? Directory.GetDirectories(dir) : [])
        {
            string mf = Path.Combine(bak, "manifest.json");
            if (!File.Exists(mf)) continue;
            string root = bak;
            try { root = (string?)JsonNode.Parse(File.ReadAllText(mf))?["root"] ?? bak; Console.WriteLine(root + ": " + Restore(bak)); }
            catch (Exception e) { failed++; Console.WriteLine(root + ": could not restore the original files: " + e.Message); }
        }
        return failed > 0 ? 1 : 0;
    }

    /// <summary>Command line: inject or restore a game.</summary>
    internal static int Run(string cmd, string path)
    {
        if (cmd == "restore" && path == "--all") return RestoreAll();
        string exe = GameExe(path);
        string msg = cmd == "restore" ? Undo(exe) : Ensure(exe, out var lib) ?? (lib != null ? "This IL2CPP game stays as it is: UDriver loads its agent into it when it launches the game." : "UDriver is in this game now. Launch it from UDriver to control it.");
        Console.WriteLine(msg);
        return cmd == "restore" || msg.StartsWith("UDriver is in") || msg.StartsWith("This IL2CPP game stays") ? 0 : 1;
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>Puts the UDriver agent into a built Mono game that does not include the package, and takes it out again.</summary>
static class Inject
{
    internal const string Il2Cpp = "This game was built with IL2CPP. UDriver can control it only if the game includes the UDriver package.";
    const string Dll = "UDriver.Runtime.dll", Json = "Newtonsoft.Json.dll";
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

    /// <summary>Makes sure the game at this game file has the agent; an error text when it cannot.</summary>
    internal static string? Ensure(string exe)
    {
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
        if (IsIl2Cpp(exe, data)) return Il2Cpp;
        if (!File.Exists(list)) return "This game is missing " + list + ", so UDriver cannot be added to it.";

        bool ownJson = !File.Exists(Path.Combine(managed, Json));
        byte[] dll;
        try { dll = Compile(exe, managed, ownJson); }
        catch (Exception e) { return "UDriver could not be added to this game: " + e.Message; }

        string loads = Path.Combine(data, "RuntimeInitializeOnLoads.json");
        var touched = new List<string> { list, loads, Path.Combine(managed, Dll) };
        if (ownJson) touched.Add(Path.Combine(managed, Json));
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
            if (ownJson) File.WriteAllBytes(Path.Combine(managed, Json), Resource(Json));

            var sa = JsonNode.Parse(File.ReadAllText(list))!;
            var names = sa["names"]!.AsArray(); var types = sa["types"]!.AsArray();
            foreach (var n in ownJson ? new[] { Json, Dll } : new[] { Dll })
                if (!names.Any(x => (string?)x == n)) { names.Add(n); types.Add(16); }
            File.WriteAllText(list, sa.ToJsonString());

            var rl = File.Exists(loads) ? JsonNode.Parse(File.ReadAllText(loads))! : new JsonObject { ["root"] = new JsonArray() };
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
        return null;
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

    static byte[] Resource(string name)
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
        if (major < 6000) throw new Exception($"it was made with Unity {v.Value}, and UDriver needs Unity 6 or newer.");
        if (major > 6000 || minor >= 4) defines.Add("UNITY_6000_4_OR_NEWER");

        var opts = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines);
        var trees = Self.GetManifestResourceNames().Where(n => n.StartsWith("rt/"))
            .Select(n => CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(Resource(n)), opts, n));
        var refs = Directory.GetFiles(managed, "*.dll").Where(f => Path.GetFileName(f) != Dll)
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
        if (ownJson) refs.Add(MetadataReference.CreateFromImage(Resource(Json)));
        var c = CSharpCompilation.Create("UDriver.Runtime", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var ms = new MemoryStream();
        var r = c.Emit(ms);
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
        string msg = cmd == "restore" ? Undo(exe) : Ensure(exe) ?? "UDriver is in this game now. Launch it from UDriver to control it.";
        Console.WriteLine(msg);
        return cmd == "restore" || msg.StartsWith("UDriver is in") ? 0 : 1;
    }
}

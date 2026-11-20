using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

/// <summary>Decompiled C# of a game type or method, from assemblies listed by app_map section=assemblies; reads only files under the game/project folder.</summary>
internal static class CodeRead
{
    const int Cap = 60 * 1024;
    static readonly StringComparison PathCmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    sealed record Asm(string Name, string Path, bool Game);
    sealed record Hit(Asm Asm, string Full);

    /// <summary>assemblies: JSON lines {name,path,game}. type: full or short name, wildcards, or a stack frame
    /// ("Ns.Type.Method (Double qty)", "Type:Method ()"); empty type with assembly = that assembly's types by namespace.</summary>
    public static string Read(string assemblies, string root, string? type, string? method = null, string? assembly = null)
    {
        string realRoot = Real(root);
        var all = new List<Asm>();
        int outside = 0;
        foreach (var line in assemblies.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (JsonNode.Parse(line) is not JsonObject o || o["path"]?.GetValue<string>() is not { } p) continue;
            if (!File.Exists(p)) continue;
            if (!Under(Real(p), realRoot)) { outside++; continue; }
            all.Add(new Asm(o["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(p), p, o["game"]?.GetValue<bool>() ?? false));
        }
        if (all.Count == 0) return "no readable assemblies under " + root + (outside > 0 ? " (" + outside + " listed assemblies are outside that folder and were refused)" : "");

        if (string.IsNullOrWhiteSpace(type))
        {
            if (assembly == null) return "pass type (e.g. PlayerController, Game.*Service, or a stack frame) or assembly to list its types";
            var a = all.Find(x => x.Name.Equals(assembly, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x.Path).Equals(assembly, StringComparison.OrdinalIgnoreCase));
            return a == null ? "assembly '" + assembly + "' is not among the readable assemblies: " + string.Join(", ", all.Where(x => x.Game).Select(x => x.Name)) : Cut(ListTypes(a), null);
        }

        var (name, frameMethod, args) = Frame(type);
        method ??= frameMethod;
        var pool = assembly == null ? all : all.FindAll(x => x.Name.Equals(assembly, StringComparison.OrdinalIgnoreCase));
        var hits = Find(pool, name);
        if (hits.Count == 0 && method == null && name.LastIndexOf('.') is var dot and > 0)
        {
            hits = Find(pool, name[..dot]);
            if (hits.Count > 0) method = name[(dot + 1)..];
        }
        if (hits.Count == 0) return NotFound(pool, name);
        if (hits.Count > 1)
            return hits.Count + " types match '" + name + "', pass one full name:\n" + string.Join("\n", hits.Take(40).Select(h => "  " + h.Full.Replace('+', '.') + "  (" + h.Asm.Name + ")")) + (hits.Count > 40 ? "\n  ..." : "");
        return Decompile(hits[0], all, method, args);
    }

    static (string Type, string? Method, string[]? Args) Frame(string s)
    {
        s = s.Trim();
        if (s.StartsWith("at ")) s = s[3..];
        int p = s.IndexOf('(');
        string[]? args = null;
        if (p >= 0)
        {
            int e = s.IndexOf(')', p);
            args = s[(p + 1)..(e > p ? e : s.Length)].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Split(' ')[0]).ToArray();
            s = s[..p].Trim();
        }
        s = s.Replace(':', '.');
        var sm = Regex.Match(s, @"^(.+)[.+]<(\w+)>d__\d+\.MoveNext$");
        if (sm.Success) return (sm.Groups[1].Value, sm.Groups[2].Value, null);
        if (p < 0) return (s, null, null);
        int dot = s.LastIndexOf('.');
        return dot > 0 ? (s[..dot], s[(dot + 1)..], args) : (s, null, args);
    }

    static IEnumerable<(Asm Asm, string Full, string Ns)> Types(IEnumerable<Asm> pool)
    {
        foreach (var a in pool)
        {
            var list = new List<(Asm, string, string)>();
            try
            {
                using var pe = new PEReader(File.OpenRead(a.Path));
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var h in md.TypeDefinitions)
                {
                    var t = md.GetTypeDefinition(h);
                    string n = md.GetString(t.Name);
                    if (n.StartsWith('<')) continue;
                    var outer = t.GetDeclaringType();
                    string ns = md.GetString(outer.IsNil ? t.Namespace : md.GetTypeDefinition(outer).Namespace);
                    for (; !outer.IsNil; outer = md.GetTypeDefinition(outer).GetDeclaringType()) n = md.GetString(md.GetTypeDefinition(outer).Name) + "+" + n;
                    list.Add((a, ns.Length > 0 ? ns + "." + n : n, ns));
                }
            }
            catch (BadImageFormatException) { }
            foreach (var x in list) yield return x;
        }
    }

    static string Short(string full) { int i = full.LastIndexOfAny(['.', '+']); string s = full[(i + 1)..]; int g = s.IndexOf('`'); return g > 0 ? s[..g] : s; }

    static List<Hit> Find(List<Asm> pool, string name)
    {
        string q = name.Replace('+', '.');
        var rx = q.IndexOfAny(['*', '?']) >= 0 ? new Regex("^" + Regex.Escape(q).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase) : null;
        bool Match(string f) => rx != null ? rx.IsMatch(f.Replace('+', '.')) || rx.IsMatch(Short(f)) : f.Replace('+', '.') == q || (q.IndexOf('.') < 0 && Short(f) == q);
        var hits = Types(pool).Where(t => Match(t.Full)).Select(t => new Hit(t.Asm, t.Full)).ToList();
        var exact = hits.FindAll(h => h.Full.Replace('+', '.') == q);
        if (exact.Count > 0) hits = exact;
        return hits.Any(h => h.Asm.Game) ? hits.FindAll(h => h.Asm.Game) : hits;
    }

    static string NotFound(List<Asm> pool, string name)
    {
        string s = Short(name).Trim('*', '?').ToLowerInvariant();
        var close = Types(pool).Where(t => { var n = Short(t.Full).ToLowerInvariant(); return s.Length >= 3 && (n.Contains(s) || s.Contains(n) && n.Length >= 4 || Distance(n, s) <= 2); })
                               .OrderBy(t => t.Asm.Game ? 0 : 1).Take(15).Select(t => "  " + t.Full.Replace('+', '.') + "  (" + t.Asm.Name + ")").ToList();
        return "type '" + name + "' not found in " + pool.Count + " assemblies" + (close.Count > 0 ? ". Close matches:\n" + string.Join("\n", close) : "; list one assembly's types with assembly=<name> and no type");
    }

    static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++) { int t = d[j]; d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1)); prev = t; }
        }
        return d[b.Length];
    }

    static string ListTypes(Asm a)
    {
        var sb = new StringBuilder(a.Name + " (" + a.Path + ")\n");
        foreach (var g in Types([a]).Where(t => t.Full.IndexOf('+') < 0).GroupBy(t => t.Ns).OrderBy(g => g.Key))
            sb.Append(g.Key.Length > 0 ? g.Key : "(global)").Append(": ").AppendJoin(", ", g.Select(t => Short(t.Full)).Order()).Append('\n');
        return sb.ToString();
    }

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "Int32", ["long"] = "Int64", ["short"] = "Int16", ["uint"] = "UInt32", ["ulong"] = "UInt64", ["float"] = "Single",
        ["double"] = "Double", ["bool"] = "Boolean", ["string"] = "String", ["object"] = "Object", ["byte"] = "Byte", ["char"] = "Char", ["decimal"] = "Decimal",
    };

    static bool SameType(IType t, string s) { s = s[(s.LastIndexOf('.') + 1)..]; return string.Equals(t.Name, Aliases.GetValueOrDefault(s, s), StringComparison.OrdinalIgnoreCase); }

    static string Decompile(Hit hit, List<Asm> all, string? method, string[]? args)
    {
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in all.Select(a => Path.GetDirectoryName(a.Path)!).Distinct())
            foreach (var f in Directory.EnumerateFiles(dir, "*.dll")) refs.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        foreach (var a in all) refs[Path.GetFileNameWithoutExtension(a.Path)] = a.Path;
        var file = Load(hit.Asm.Path);
        var dec = new CSharpDecompiler(file, new Resolver(refs), new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
        var td = dec.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(hit.Full));
        string head = "// " + hit.Full.Replace('+', '.') + " in " + hit.Asm.Name + " (" + hit.Asm.Path + ")\n";
        if (method == null || td == null) return Cut(head + dec.DecompileTypeAsString(new FullTypeName(hit.Full)), td);

        bool ctor = method is ".ctor" or "ctor" || method == td.Name;
        var ms = td.Methods.Where(m => ctor ? m.IsConstructor && !m.IsStatic : m.Name == method).ToList();
        if (ms.Count == 0) ms = td.Properties.Where(p => p.Name == method).SelectMany(p => new[] { p.Getter, p.Setter }).OfType<IMethod>().ToList();
        if (ms.Count == 0)
            return "no method '" + method + "' on " + hit.Full.Replace('+', '.') + ". Methods: " + string.Join(", ", td.Methods.Where(m => !m.Name.StartsWith('<')).Select(m => m.Name).Distinct());
        if (args != null)
        {
            var fit = ms.FindAll(m => m.Parameters.Count == args.Length && m.Parameters.Select((p, i) => SameType(p.Type, args[i])).All(x => x));
            if (fit.Count > 0) ms = fit;
        }
        var sb = new StringBuilder(head);
        var pdb = Pdb(hit.Asm.Path);
        foreach (var m in ms)
        {
            var src = pdb == null ? null : Source(pdb, td, m);
            if (src != null) sb.Append("// source: ").Append(src).Append('\n');
        }
        sb.Append(dec.DecompileAsString(ms.Select(m => (EntityHandle)m.MetadataToken).ToArray()));
        return Cut(sb.ToString(), null);
    }

    static PEFile Load(string path) => new(path, File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);

    static MetadataReader? Pdb(string dll)
    {
        string p = Path.ChangeExtension(dll, ".pdb");
        if (!File.Exists(p)) return null;
        try { return MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(p), MetadataStreamOptions.PrefetchMetadata).GetMetadataReader(); }
        catch (BadImageFormatException) { return null; }
    }

    /// <summary>"file:first-last" from sequence points; iterator/async bodies live in the compiler-generated MoveNext.</summary>
    static string? Source(MetadataReader pdb, ITypeDefinition td, IMethod m)
    {
        var handles = new List<MethodDefinitionHandle> { (MethodDefinitionHandle)m.MetadataToken };
        foreach (var n in td.NestedTypes.Where(n => n.Name.StartsWith("<" + m.Name + ">")))
            handles.AddRange(n.Methods.Where(x => x.Name == "MoveNext").Select(x => (MethodDefinitionHandle)x.MetadataToken));
        string? doc = null; int first = int.MaxValue, last = 0;
        foreach (var h in handles)
            foreach (var sp in pdb.GetMethodDebugInformation(h).GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                doc ??= pdb.GetString(pdb.GetDocument(sp.Document).Name);
                first = Math.Min(first, sp.StartLine); last = Math.Max(last, sp.EndLine);
            }
        return doc == null ? null : doc + ":" + first + "-" + last;
    }

    static string Cut(string s, ITypeDefinition? td)
    {
        if (s.Length <= Cap) return s;
        return s[..Cap] + "\n// truncated at " + Cap / 1024 + " KB - ask for one method" + (td == null ? "" : ": " + string.Join(", ", td.Methods.Where(m => !m.Name.StartsWith('<')).Select(m => m.Name).Distinct()));
    }

    static bool Under(string path, string root) => path.Equals(root, PathCmp) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathCmp);

    static string Real(string p)
    {
        var full = Path.GetFullPath(p);
        var cur = Path.GetPathRoot(full)!;
        foreach (var part in full[cur.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, part);
            FileSystemInfo fi = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            if (fi.LinkTarget != null && fi.ResolveLinkTarget(true) is { } t) cur = Real(t.FullName);
        }
        return cur;
    }

    sealed class Resolver(Dictionary<string, string> refs) : IAssemblyResolver
    {
        readonly Dictionary<string, MetadataFile?> _loaded = new(StringComparer.OrdinalIgnoreCase);
        MetadataFile? Get(string name)
        {
            lock (_loaded)
            {
                if (_loaded.TryGetValue(name, out var f)) return f;
                try { f = refs.TryGetValue(name, out var p) ? Load(p) : null; } catch (BadImageFormatException) { f = null; }
                return _loaded[name] = f;
            }
        }
        public MetadataFile? Resolve(IAssemblyReference r) => Get(r.Name);
        public MetadataFile? ResolveModule(MetadataFile main, string name) => Get(Path.GetFileNameWithoutExtension(name));
        public Task<MetadataFile?> ResolveAsync(IAssemblyReference r) => Task.FromResult(Resolve(r));
        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile main, string name) => Task.FromResult(ResolveModule(main, name));
        public IDisposable? BeginSnapshot() => null;
    }
}

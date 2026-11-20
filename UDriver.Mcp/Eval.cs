using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

/// <summary>
/// The eval tool: compiles the agent's C# here, refuses code that could reach past the game (raw memory, native
/// code, code loading, the sandbox itself), adds the budget calls of UDriver.Sandbox and sends the assembly to the game.
/// </summary>
static class Eval
{
    const int MaxValueType = 16 << 10, MaxFrame = 64 << 10;

    /// <summary>Null when the game or editor is not reachable.</summary>
    public static async Task<JsonNode?> Run(int port, JsonObject args)
    {
        if (args["stop"] is JsonNode stop) return await Program.TryCall(port, "eval", new JsonObject { ["stop"] = stop.ToString() });
        if ((string?)args["code"] is not { Length: > 0 } code) return Program.Result("code is required", true);
        var info = await Program.TryCall(port, "eval", new JsonObject { ["refs"] = true });
        if (info == null || info["isError"]?.GetValue<bool>() == true) return info;
        var game = JsonNode.Parse((string?)info["content"]?[0]?["text"] ?? "{}")!;
        int id = Random.Shared.Next(1, int.MaxValue);
        byte[] asm;
        try { asm = Build(code, id, References(game)); }
        catch (Refused e) { return Program.Result(e.Message, true); }
        var call = new JsonObject
        {
            ["asm"] = Convert.ToBase64String(asm), ["id"] = id, ["computer"] = Program.LevelOff(3) == null,
            ["timeoutMs"] = args["timeoutMs"]?.DeepClone(), ["maxMemoryMB"] = args["maxMemoryMB"]?.DeepClone(),
        };
        if (game["harmony"]?.GetValue<bool>() != true) call["harmony"] = Convert.ToBase64String(global::Inject.Resource("0Harmony.dll"));
        return await Program.TryCall(port, "eval", call);
    }

    /// <summary>Installs the computer guards in a game before a call reaches it; after a call that changes the game they also hold
    /// the game's own code until it restarts. A result only when the change must not go ahead.</summary>
    public static async Task<JsonNode?> Arm(Program.Target t, bool change)
    {
        if (Program.LevelOff(3) == null) return null;
        int port = t.Port;
        change |= t.Game?.Mark.Length > 0;
        var call = new JsonObject { ["arm"] = true, ["taint"] = change };
        var r = await Program.TryCall(port, "eval", call);
        if (Text(r).Contains("Harmony is not loaded"))
        {
            call["harmony"] = Convert.ToBase64String(global::Inject.Resource("0Harmony.dll"));
            r = await Program.TryCall(port, "eval", call);
        }
        var said = Text(r).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool walled = said is ["walled", ..];
        if (change && walled && t.Game != null && said is [_, var mark, ..] && Path.GetFileName(mark) == "UDriver" && mark != t.Game.Mark)
        {
            File.WriteAllBytes(Path.Combine(Program.HomeDir, "0Harmony.dll"), global::Inject.Resource("0Harmony.dll"));
            Registry.SetMark(t.Game.Id, t.Game.Mark = mark);
        }
        // A game inside the computer shell is held by the operating system, with or without these guards.
        return change && !walled && !t.Shell
            ? Program.Result("Not done: this game cannot keep the computer closed after a change (" + (r == null ? "it did not answer" : Text(r).Replace("ERROR: ", "")) + "). Looking and playing still work; changing it needs computer access.", true) : null;
    }

    static string Text(JsonNode? r) => (string?)r?["content"]?[0]?["text"] ?? "";

    sealed class Refused(string m) : Exception(m);

    public static byte[] Build(string code, int id, List<string> refs)
    {
        var bytes = Compile(code, "UDriverEval" + id, refs);
        Check(bytes, refs);
        return Rewrite(bytes, id);
    }

    static readonly ConcurrentDictionary<string, (DateTime, MetadataReference)> Cache = new();

    static MetadataReference Ref(string path)
    {
        var t = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var c) && c.Item1 == t) return c.Item2;
        var r = MetadataReference.CreateFromFile(path);
        Cache[path] = (t, r);
        return r;
    }

    static bool EditorOnly(string name) =>
        name.StartsWith("UnityEditor", StringComparison.Ordinal) || name.Contains(".Editor", StringComparison.Ordinal) || name.EndsWith("Editor", StringComparison.Ordinal)
        || name.StartsWith("UDriver", StringComparison.Ordinal) || name == "0Harmony" || name.StartsWith("UDriverEval", StringComparison.Ordinal);

    /// <summary>The game's loaded assemblies plus, in a player, everything in its Managed folder; editor-only ones are left out.</summary>
    static List<string> References(JsonNode game)
    {
        var paths = (game["refs"] as JsonArray ?? new JsonArray()).Select(n => (string?)n ?? "").ToList();
        if ((string?)game["managed"] is { } managed && Directory.Exists(managed)) paths.AddRange(Directory.GetFiles(managed, "*.dll"));
        if ((string?)game["dir"] is { } dir) paths.Add(Path.Combine(dir, "Facades", "netstandard.dll"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return paths.Where(p => File.Exists(p) && !EditorOnly(Path.GetFileNameWithoutExtension(p)) && seen.Add(Path.GetFileName(p))).ToList();
    }

    static readonly Regex UsingLine = new(@"^\s*using\s+(static\s+)?[\w.]+(\s*=\s*[\w.<>, ]+)?\s*;\s*$");

    /// <summary>A method body becomes Agent.Run; a trailing expression is returned; yield makes it a coroutine.</summary>
    internal static string Source(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        int n = lines.TakeWhile(l => l.Trim() == "" || UsingLine.IsMatch(l)).Count();
        string usings = string.Join("\n", lines.Take(n)), body = string.Join("\n", lines.Skip(n)).TrimEnd();
        const string head = "using System; using System.Collections; using System.Collections.Generic; using System.Linq; using System.Text; using UnityEngine; using Object = UnityEngine.Object; using Random = UnityEngine.Random;\n";
        if (Regex.IsMatch(body, @"\bclass\s+Agent\b")) return head + usings + "\n#line " + (n + 1) + "\n" + body;
        bool iterator = Regex.IsMatch(body, @"\byield\s+(return|break)\b");
        if (!iterator && !body.EndsWith(';') && !body.EndsWith('}'))
            for (int i = body.Length - 1; i >= -1; i--)
                if (i < 0 || body[i] is ';' or '}')
                {
                    var tail = body[(i + 1)..];
                    if (SyntaxFactory.ParseExpression(tail) is { } e && !e.ContainsDiagnostics && e.FullSpan.Length == tail.Length)
                    {
                        body = body[..(i + 1)] + " return (" + tail.Trim() + ");";
                        break;
                    }
                }
        return head + usings + "\npublic static class Agent { public static " + (iterator ? "IEnumerator" : "object") + " Run() {\n#line " + (n + 1) + "\n" + body
               + "\n#line hidden\n" + (iterator ? "" : "return null;") + "\n} }";
    }

    static byte[] Compile(string code, string name, List<string> refs)
    {
        var tree = CSharpSyntaxTree.ParseText(Source(code), new CSharpParseOptions(LanguageVersion.CSharp9));
        var c = CSharpCompilation.Create(name, new[] { tree }, refs.Select(Ref),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: false));
        using var ms = new MemoryStream();
        var r = c.Emit(ms);
        if (r.Success) return ms.ToArray();
        var errors = r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)
            .Select(d => $"line {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}");
        throw new Refused("compile error:\n" + string.Join("\n", errors));
    }

    // ---------- static rules ----------

    static readonly HashSet<Code> DeniedOps = new() { Code.Calli, Code.Localloc, Code.Cpblk, Code.Initblk, Code.Jmp, Code.Arglist, Code.Mkrefany, Code.Refanyval, Code.Refanytype, Code.Tail };
    static readonly string[] DeniedNs =
    {
        "UDriver", "HarmonyLib", "MonoMod", "Mono", "Microsoft.Win32", "System.Runtime.InteropServices", "System.Reflection.Emit", "System.Runtime.Loader",
        "System.IO.Pipes", "System.IO.MemoryMappedFiles", "Unity.Collections", "Unity.Jobs", "Unity.Burst", "UnityEditor", "UnityEngine.Windows",
    };
    static readonly HashSet<string> DeniedTypes = new()
    {
        "System.Runtime.CompilerServices.Unsafe", "System.TypedReference", "System.ArgIterator", "System.RuntimeArgumentHandle", "System.Reflection.Pointer",
    };
    static readonly Regex DeniedMember = new(@"^System\.(Reflection\.Assembly::(Load|UnsafeLoad|ReflectionOnly)|AppDomain::(Load|ExecuteAssembly|CreateDomain|DefineDynamicAssembly|CreateInstance\w*From|ApplyPolicy)|Activator::CreateInstanceFrom|RuntimeMethodHandle::GetFunctionPointer)");

    static string? Deny(TypeReference t)
    {
        var top = t; while (top.DeclaringType != null) top = top.DeclaringType;
        string ns = top.Namespace, full = ns + "." + top.Name.Split('`')[0];
        if (DeniedTypes.Contains(full)) return full;
        foreach (var p in DeniedNs) if (ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal)) return full;
        return null;
    }

    static string? TR(TypeReference? t) => t switch
    {
        null or GenericParameter or TypeDefinition => null,
        PointerType => "pointer",
        FunctionPointerType => "function pointer",
        PinnedType => "pinned",
        SentinelType => "varargs",
        TypeSpecification s when s is not GenericInstanceType => s is IModifierType m ? TR(m.ModifierType) ?? TR(s.ElementType) : TR(s.ElementType),
        GenericInstanceType g => TR(g.ElementType) ?? g.GenericArguments.Select(TR).FirstOrDefault(x => x != null),
        _ => Deny(t),
    };

    static string? MR(MethodReference mr)
    {
        if (mr is GenericInstanceMethod g)
        {
            if (g.GenericArguments.Select(TR).FirstOrDefault(x => x != null) is { } x) return x;
            mr = g.ElementMethod;
        }
        string full = mr.DeclaringType.GetElementType().FullName + "::" + mr.Name;
        if (DeniedMember.IsMatch(full)) return full;
        if (mr.DeclaringType.FullName == "System.Activator" && mr.Name == "CreateInstance" && mr.Parameters.FirstOrDefault()?.ParameterType.MetadataType == MetadataType.String) return full + "(string)";
        return TR(mr.DeclaringType) ?? TR(mr.ReturnType) ?? mr.Parameters.Select(p => TR(p.ParameterType)).FirstOrDefault(x => x != null);
    }

    static string? CA(Mono.Cecil.ICustomAttributeProvider p) => p.CustomAttributes.Select(a => TR(a.AttributeType)).FirstOrDefault(x => x != null);

    /// <summary>Approximate size in bytes: what a value of this type occupies in a local, a field or an array slot.</summary>
    static long Size(TypeReference t, int depth = 0)
    {
        switch (t.MetadataType)
        {
            case MetadataType.Boolean or MetadataType.Byte or MetadataType.SByte: return 1;
            case MetadataType.Char or MetadataType.Int16 or MetadataType.UInt16: return 2;
            case MetadataType.Int32 or MetadataType.UInt32 or MetadataType.Single: return 4;
            case MetadataType.Int64 or MetadataType.UInt64 or MetadataType.Double: return 8;
        }
        if (!t.IsValueType || depth > 8 || t is ByReferenceType) return 8;
        if (t is GenericInstanceType g) return 16 + g.GenericArguments.Sum(a => Size(a, depth + 1));
        TypeDefinition? d;
        try { d = t.Resolve(); } catch { d = null; }
        if (d == null) return 8;
        if (d.IsEnum) return Size(d.GetEnumUnderlyingType(), depth + 1);
        return Math.Max(d.ClassSize, d.Fields.Where(f => !f.IsStatic).Sum(f => Size(f.FieldType, depth + 1)));
    }

    static void Check(byte[] bytes, List<string> refs)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in refs.Select(Path.GetDirectoryName).Distinct()) resolver.AddSearchDirectory(dir);
        using var m = ModuleDefinition.ReadModule(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        void No(string? why) { if (why != null) throw new Refused("refused: " + why + ". eval code may use the game and Unity, not raw memory, native code, code loading or the sandbox itself."); }
        void Big(TypeReference t, string what) { if (t.IsValueType && Size(t) > MaxValueType) No(what + " " + t.Name + " is larger than " + (MaxValueType >> 10) + " KB"); }
        if (m.HasModuleReferences) No("native library " + m.ModuleReferences[0].Name);
        foreach (var ca in m.Assembly.CustomAttributes.Concat(m.CustomAttributes)) No(TR(ca.AttributeType));
        foreach (var t in m.GetTypes())
        {
            if (t.IsExplicitLayout && t.HasFields) No("explicit struct layout in " + t.Name);
            if (t.IsValueType && Size(t) > MaxValueType) No("struct " + t.Name + " is larger than " + (MaxValueType >> 10) + " KB");
            No(TR(t.BaseType) ?? t.Interfaces.Select(i => TR(i.InterfaceType)).FirstOrDefault(x => x != null) ?? CA(t));
            foreach (var f in t.Fields) { No(TR(f.FieldType) ?? CA(f)); Big(f.FieldType, "field"); }
            foreach (var meth in t.Methods)
            {
                if (meth.IsPInvokeImpl || meth.HasPInvokeInfo || meth.IsInternalCall || meth.IsNative || meth.IsUnmanaged) No("native method " + meth.Name);
                if (meth.Name == "Finalize" && meth.IsVirtual && !meth.HasParameters) No("finalizer in " + t.Name);
                No(TR(meth.ReturnType) ?? meth.Parameters.Select(p => TR(p.ParameterType)).FirstOrDefault(x => x != null) ?? CA(meth));
                if (!meth.HasBody) continue;
                long frame = meth.Parameters.Sum(p => Size(p.ParameterType));
                foreach (var v in meth.Body.Variables) { No(TR(v.VariableType)); Big(v.VariableType, "local"); frame += Size(v.VariableType); }
                if (frame > MaxFrame) No("method " + meth.Name + " needs more than " + (MaxFrame >> 10) + " KB of stack");
                foreach (var ins in meth.Body.Instructions)
                {
                    if (DeniedOps.Contains(ins.OpCode.Code)) No("IL " + ins.OpCode.Name);
                    No(ins.Operand switch
                    {
                        MethodReference mr => MR(mr),
                        FieldReference fr => TR(fr.DeclaringType) ?? TR(fr.FieldType),
                        TypeReference tr => TR(tr),
                        CallSite => "calli",
                        _ => null,
                    });
                    if (ins.Operand is TypeReference vt) Big(vt, "type");
                }
            }
        }
    }

    // ---------- the game's own code ----------

    static readonly Regex GameNative = new(@"^System\.(Reflection\.Emit\.|Runtime\.Loader\.|Runtime\.InteropServices\.(NativeLibrary::|Marshal::GetDelegateForFunctionPointer))");
    static readonly HashSet<string> UnityLibs = new(StringComparer.OrdinalIgnoreCase) { "__Internal", "UnityPlayer", "UnityPlayer.dll", "libunity", "libUnityPlayer.so" };
    static readonly ConcurrentDictionary<string, (string Stamp, string Why)> Scanned = new();
    static string ScanFile => Path.Combine(Program.HomeDir, "scan.json");

    static bool Platform(string n) => n is "mscorlib" or "netstandard" or "System" or "UnityEngine" || n.StartsWith("System.", StringComparison.Ordinal)
        || n.StartsWith("Mono.", StringComparison.Ordinal) || n.StartsWith("UDriver.", StringComparison.Ordinal) || n.StartsWith("UnityEngine.", StringComparison.Ordinal) && n.EndsWith("Module", StringComparison.Ordinal);

    /// <summary>Why the game's own code could reach the computer past the in-process wall (a native call, a function pointer, code it
    /// generates or loads, a hook that may run before UDriver's); "" when nothing can. Code that cannot be read counts as such a reason.
    /// Remembered in ~/.udriver by the hash of the assemblies, so a rebuilt game is scanned again.</summary>
    internal static string Native(string managed)
    {
        string[] files;
        try { files = Directory.GetFiles(managed, "*.dll").Where(f => !Platform(Path.GetFileNameWithoutExtension(f))).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception e) { return "its code cannot be read: " + e.Message; }
        string stamp = string.Join("|", files.Select(f => f + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks));
        if (Scanned.TryGetValue(managed, out var seen) && seen.Stamp == stamp) return seen.Why;
        string why = "";
        try
        {
            var bytes = files.Select(File.ReadAllBytes).ToArray();
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (int i = 0; i < files.Length; i++) { sha.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(files[i]) + "\0")); sha.AppendData(bytes[i]); }
            string hash = Convert.ToHexString(sha.GetHashAndReset());
            JsonObject cache;
            try { cache = JsonNode.Parse(File.ReadAllText(ScanFile)) as JsonObject ?? new(); } catch { cache = new(); }
            if ((string?)cache[hash] is string known) why = known;
            else
            {
                byte[] harmony = global::Inject.Resource("0Harmony.dll");
                for (int i = 0; i < files.Length && why.Length == 0; i++)
                    try { why = Path.GetFileName(files[i]) == "0Harmony.dll" && bytes[i].AsSpan().SequenceEqual(harmony) ? "" : Scan(Path.GetFileNameWithoutExtension(files[i]), bytes[i], managed) ?? ""; }
                    catch (Exception e) { why = Path.GetFileName(files[i]) + " cannot be read: " + e.Message; }
                cache[hash] = why;
                Directory.CreateDirectory(Program.HomeDir);
                File.WriteAllText(ScanFile, cache.ToJsonString());
            }
        }
        catch (Exception e) { why = "its code cannot be read: " + e.Message; }
        if (why.Length > 0) Console.Error.WriteLine("UDriver: " + managed + ": the game's own code could get past the wall while Computer access is off: " + why);
        Scanned[managed] = (stamp, why);
        return why;
    }

    static string? Scan(string name, byte[] bytes, string managed)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var m = ModuleDefinition.ReadModule(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        // Unity's own assemblies call only into Unity's runtime: through its internal calls, its libraries and the function pointers it hands
        // them; any other way to a native address (a library, NativeLibrary, Marshal) is still caught below.
        bool unity = name.StartsWith("Unity.", StringComparison.Ordinal) || name.StartsWith("UnityEngine.", StringComparison.Ordinal);
        bool Fp(TypeReference? t) => !unity && t switch
        {
            null => false,
            FunctionPointerType => true,
            GenericInstanceType g => g.GenericArguments.Any(Fp) || Fp(g.ElementType),
            TypeSpecification s => Fp(s.ElementType),
            _ => false,
        };
        if (m.AssemblyReferences.Any(a => a.Name == "0Harmony")) return $"{name}: changes code while it runs through Harmony";
        foreach (var t in m.GetTypes())
        {
            if (t.Fields.FirstOrDefault(f => Fp(f.FieldType)) is { } fpf) return $"{name}: function pointer {t.FullName}.{fpf.Name}";
            foreach (var meth in t.Methods)
            {
                string at = $"{name}: {t.FullName}::{meth.Name}";
                if ((meth.IsPInvokeImpl || meth.IsInternalCall || meth.IsNative || meth.IsUnmanaged)
                    && !(unity && (meth.IsInternalCall || meth.PInvokeInfo?.Module.Name is string lib && UnityLibs.Contains(lib))))
                    return at + " calls native code" + (meth.PInvokeInfo?.Module.Name is string l ? " in " + l : "");
                if (meth.CustomAttributes.Any(a => a.AttributeType.FullName == "UnityEngine.RuntimeInitializeOnLoadMethodAttribute"
                    && a.ConstructorArguments.Count > 0 && Convert.ToInt32(a.ConstructorArguments[0].Value) == 4))
                    return at + " runs at SubsystemRegistration, possibly before UDriver";
                if (Fp(meth.ReturnType) || meth.Parameters.Any(p => Fp(p.ParameterType))) return at + " uses a function pointer";
                if (!meth.HasBody) continue;
                if (meth.Body.Variables.Any(v => Fp(v.VariableType))) return at + " uses a function pointer";
                if (!unity && meth.Body.Instructions.Any(i => i.OpCode.Code == Code.Calli)) return at + " calls through a function pointer";
            }
        }
        foreach (var r in m.GetMemberReferences())
        {
            string full = r.DeclaringType.GetElementType().FullName + "::" + r.Name;
            if (full is "System.Reflection.Assembly::Load" or "System.Reflection.Assembly::LoadWithPartialName"
                && r is MethodReference { Parameters: [{ ParameterType.MetadataType: MetadataType.String }] }) continue;   // by name: only assemblies the game ships, all scanned here
            if (DeniedMember.IsMatch(full) || GameNative.IsMatch(full)) return $"{name}: uses {full}";
        }
        foreach (var r in m.GetTypeReferences())
            if (GameNative.IsMatch(r.FullName + "::")) return $"{name}: uses {r.FullName}";
        return null;
    }

    // ---------- rewrite ----------

    static byte[] Rewrite(byte[] bytes, int id)
    {
        using var m = ModuleDefinition.ReadModule(new MemoryStream(bytes));
        var scope = new AssemblyNameReference("UDriver.Runtime", new Version(0, 0, 0, 0));
        m.AssemblyReferences.Add(scope);
        var sb = new TypeReference("UDriver", "Sandbox", m, scope);
        var ts = m.TypeSystem;
        var del = new TypeReference("System", "Delegate", m, ts.CoreLibrary);
        MethodReference Rt(string n, TypeReference ret, params TypeReference[] ps)
        {
            var r = new MethodReference(n, ret, sb);
            foreach (var p in ps) r.Parameters.Add(new ParameterDefinition(p));
            return r;
        }
        var tick = Rt("Tick", ts.Void); var enter = Rt("Enter", ts.Void, ts.Int32); var exit = Rt("Exit", ts.Void);
        var check = Rt("CheckAbort", ts.Void); var wrap = Rt("Wrap", del, del, ts.Int32); var depth = Rt("Depth", ts.Int32);
        var orphan = Rt("Orphan", ts.Boolean, ts.Object, ts.Int32); var alloc = Rt("Alloc", ts.Int64, ts.Int64, ts.Int32);
        foreach (var t in m.GetTypes())
            foreach (var meth in t.Methods.Where(x => x.HasBody))
            {
                var body = meth.Body;
                body.SimplifyMacros();
                var il = body.GetILProcessor();
                var ins = body.Instructions.ToList();
                var idx = new Dictionary<Instruction, int>();
                for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
                for (int i = 1; i < ins.Count; i++)
                    if (ins[i].OpCode == OpCodes.Newobj && ins[i].Operand is MethodReference ctor && ctor.Parameters.Count == 2
                        && ctor.Parameters[1].ParameterType.MetadataType == MetadataType.IntPtr
                        && (ins[i - 1].OpCode == OpCodes.Ldftn || ins[i - 1].OpCode == OpCodes.Ldvirtftn) && !IsOwn((MethodReference)ins[i - 1].Operand))
                    {
                        var cast = il.Create(OpCodes.Castclass, ctor.DeclaringType);
                        il.InsertAfter(ins[i], cast);
                        il.InsertBefore(cast, il.Create(OpCodes.Ldc_I4, id));
                        il.InsertBefore(cast, il.Create(OpCodes.Call, wrap));
                    }
                var back = ins.Where(x => x.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch
                                          && (x.Operand is Instruction tg ? new[] { tg } : (Instruction[])x.Operand).Any(tg => idx[tg] <= idx[x])).ToList();
                foreach (var x in back) Inject(il, x, il.Create(OpCodes.Call, tick));
                foreach (var x in ins.Where(x => x.OpCode == OpCodes.Newarr).ToList())
                    Inject(il, x, il.Create(OpCodes.Conv_I8), il.Create(OpCodes.Ldc_I4, (int)Math.Min(Size((TypeReference)x.Operand), MaxValueType)), il.Create(OpCodes.Call, alloc), il.Create(OpCodes.Conv_Ovf_I));
                foreach (var h in body.ExceptionHandlers.ToList())
                {
                    if (h.HandlerType == ExceptionHandlerType.Catch) Inject(il, h.HandlerStart, il.Create(OpCodes.Call, check));
                    if (h.HandlerType == ExceptionHandlerType.Filter) { Inject(il, h.FilterStart, il.Create(OpCodes.Call, check)); Inject(il, h.HandlerStart, il.Create(OpCodes.Call, check)); }
                }
                WrapBody(meth, il, id, enter, exit, depth, orphan);
                body.OptimizeMacros();
            }
        using var ms = new MemoryStream();
        m.Write(ms);
        return ms.ToArray();
    }

    static bool IsOwn(MethodReference r) => (r is GenericInstanceMethod g ? g.ElementMethod : r).DeclaringType.GetElementType() is TypeDefinition;

    /// <summary>x becomes the first injected instruction and its original opcode follows the rest, so branches and handler bounds that pointed at x run the injection first.</summary>
    static void Inject(ILProcessor il, Instruction x, params Instruction[] seq)
    {
        var copy = il.Create(OpCodes.Nop);
        copy.OpCode = x.OpCode; copy.Operand = x.Operand;
        x.OpCode = seq[0].OpCode; x.Operand = seq[0].Operand;
        var at = x;
        foreach (var s in seq.Skip(1).Append(copy)) { il.InsertAfter(at, s); at = s; }
    }

    /// <summary>Enter(id) ... try { body } finally { Exit() } inside a filter that lets Sandbox.Orphan end stopped callbacks quietly.</summary>
    static void WrapBody(MethodDefinition meth, ILProcessor il, int id, MethodReference enter, MethodReference exit, MethodReference depth, MethodReference orphan)
    {
        var body = meth.Body;
        var first = body.Instructions[0];
        VariableDefinition? ret = null;
        if (meth.ReturnType.MetadataType != MetadataType.Void) { ret = new VariableDefinition(meth.ReturnType); body.Variables.Add(ret); }
        var endRet = il.Create(OpCodes.Ret);
        var end = ret != null ? il.Create(OpCodes.Ldloc, ret) : endRet;
        var fin = il.Create(OpCodes.Call, exit);
        foreach (var x in body.Instructions.ToList())
            if (x.OpCode == OpCodes.Ret)
            {
                if (ret == null) { x.OpCode = OpCodes.Leave; x.Operand = end; }
                else { x.OpCode = OpCodes.Stloc; x.Operand = ret; il.InsertAfter(x, il.Create(OpCodes.Leave, end)); }
            }
        var d = new VariableDefinition(meth.Module.TypeSystem.Int32); body.Variables.Add(d);
        var filt = il.Create(OpCodes.Ldloc, d); var hs = il.Create(OpCodes.Pop);
        var open = body.ExceptionHandlers.Where(h => h.HandlerEnd == null).ToList();
        il.Append(fin); foreach (var h in open) h.HandlerEnd = fin; il.Append(il.Create(OpCodes.Endfinally));
        il.Append(filt); il.Append(il.Create(OpCodes.Call, orphan)); il.Append(il.Create(OpCodes.Endfilter));
        il.Append(hs); il.Append(il.Create(OpCodes.Leave, end));
        if (ret != null) il.Append(end);
        il.Append(endRet);
        var ld = il.Create(OpCodes.Ldc_I4, id);
        il.InsertBefore(first, ld); il.InsertBefore(first, il.Create(OpCodes.Call, enter));
        il.InsertBefore(first, il.Create(OpCodes.Call, depth)); il.InsertBefore(first, il.Create(OpCodes.Stloc, d));
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = fin, HandlerStart = fin, HandlerEnd = filt });
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter) { TryStart = ld, TryEnd = filt, FilterStart = filt, HandlerStart = hs, HandlerEnd = end });
    }
}

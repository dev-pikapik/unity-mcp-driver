using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UDriver;

/// <summary>Testing mode on the server: bug reports, saved scenarios, parallel runs and the testing playbook.</summary>
internal static partial class Program
{
    static readonly object ReportLock = new();
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static string S(JsonNode? n) => n?.ToString() ?? "";

    static JsonObject TestPrompt() => new()
    {
        ["name"] = "test", ["title"] = "Test the game",
        ["description"] = "Find bugs in a Unity game with UDriver's testing tools and write one report per bug.",
        ["arguments"] = new JsonArray { new JsonObject { ["name"] = "what", ["description"] = "What to test (a screen, a feature, a flow); default: the whole game.", ["required"] = false } },
    };

    static string TestPlaybook(string? what) => $"""
        Test {(string.IsNullOrWhiteSpace(what) ? "the running game" : what.Trim())} with UDriver and report every bug you find.
        1. qa_watch action=start, so errors, NaN values, leaks, stalls, slow frames and visual faults are caught from now on.
        2. Learn the structure: ui_tree, scene_tree, and qa_risk for shared state that several systems change.
        3. If the Unity editor is open, run the unit tests with editor_tests.
        4. qa_crawl mode=crawl to reach every screen and control, then qa_crawl mode=monkey with a seed (note the seed). With several copies of a build, qa_parallel runs them at once with different seeds and timing.
        5. Play the main flows yourself the way a player would (play, ui_click, key, type_text).
        6. Exercise combinations: press actions while background systems run (timers, spawning, saving, loading), pause and resume (time_control), repeat actions fast, leave a screen in the middle of an action.
        7. After each stretch read qa_findings; take a screenshot when something looks wrong.
        8. For each finding run qa_isolate to get the shortest steps that still reproduce it.
        9. Write one qa_report per distinct finding (it adds file:line and what breaks together). Save checks worth repeating with qa_scenario.
        10. Finish with a summary: each bug with its severity, file:line and which systems break together.
        If UDriver itself fails, gives output you cannot use, or lacks a tool you need, call report_problem before working around it.
        """;

    /// <summary>The project folder of an editor, or the folder holding a build (.app, .exe, .x86_64).</summary>
    static string ReportRoot(string path)
    {
        var app = Regex.Match(path, @"^.*?\.app(?=[\\/]|$)", RegexOptions.IgnoreCase);
        if (app.Success) path = app.Value;
        return Directory.Exists(path) && !app.Success ? path : Path.GetDirectoryName(path) ?? path;
    }

    /// <summary>A folder for qa files inside the game's folder; a link that leads out of it is refused.</summary>
    static string QaDir(string root, string sub = "")
    {
        string dir = Path.Combine(root, "UDriverReports", sub), top = RealPath(root), real = RealPath(dir);
        if (!real.StartsWith(top + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{dir} leads outside {root}");
        return real;
    }

    /// <summary>Writes through a new temporary file, so a link planted at the name is replaced, not followed.</summary>
    static void Put(string file, Action<string> write)
    {
        string tmp = file + "." + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try { write(tmp); File.Move(tmp, file, true); }
        finally { File.Delete(tmp); }
    }

    /// <summary>The target (running or saved) whose folder gets the reports, after the owner's access check.</summary>
    static async Task<(Target? T, Registry.Game? G, string Root, string? Error)> QaPlace(string tool, JsonObject args, string? want)
    {
        var all = await Targets();
        var t = Pick(all, "game", want, out _) ?? Pick(all, "editor", want, out _);
        var g = t?.Game ?? (want != null ? Registry.Find(want) : null);
        string path = g?.Path ?? t?.Path ?? "";
        if (path.Length == 0)
            return (t, g, "", want != null ? $"No running or saved game named '{want}'. Targets: {Names(all)}." : "No game is running: pass target with a running or saved game.");
        if (Denied(tool, args, t, g) is string no) return (t, g, "", no);
        return (t, g, ReportRoot(path), null);
    }

    static (List<JsonObject> Found, JsonNode? Instance, List<byte[]> Images) Findings(JsonNode res)
    {
        var found = new List<JsonObject>();
        var images = new List<byte[]>();
        JsonNode? inst = null;
        foreach (var c in res["content"] as JsonArray ?? new JsonArray())
        {
            if ((string?)c?["type"] == "image")
            {
                try { images.Add(Convert.FromBase64String((string?)c["data"] ?? "")); } catch (FormatException) { }
                continue;
            }
            JsonNode? j;
            try { j = JsonNode.Parse(((string?)c?["text"] ?? "").Split('\n')[0]); } catch (JsonException) { continue; }
            if (j is JsonObject o) { inst ??= o["instance"]?.DeepClone(); j = o["findings"]; }
            foreach (var f in (j as JsonArray ?? new JsonArray()).OfType<JsonObject>()) found.Add((JsonObject)f.DeepClone());
        }
        return (found, inst, images);
    }

    static async Task<JsonNode> QaReport(JsonObject args, string? want)
    {
        var (t, g, root, err) = await QaPlace("qa_report", args, want);
        if (err != null) return Result(err, true);
        string code = args["source"]?.ToString() is { Length: > 0 } src && Directory.Exists(src) ? src : root;
        if (code != root && !File.Exists(Path.Combine(code, "ProjectSettings", "ProjectVersion.txt")) && LevelOff(3) is string wall)
            return Result("source must be a Unity project folder (with ProjectSettings). " + wall, true);
        var picked = args["findings"] as JsonArray ?? new JsonArray();
        var ids = picked.OfType<JsonValue>().Select(S).ToHashSet();
        bool all = args["all"] is JsonValue v && v.TryGetValue(out bool b) && b;
        var list = new List<(JsonObject F, byte[]? Jpg)>();
        JsonNode? inst = null;
        if (all || ids.Count > 0)
        {
            if (Blocked("qa_findings", null) is string off) return Result(off, true);
            string? game = t?.Name ?? want;
            var res = await CallUnity("qa_findings", new JsonObject { ["format"] = "json" }, game);
            if (res["isError"]?.GetValue<bool>() == true) return Result("qa_findings failed: " + res["content"]?[0]?["text"], true);
            var (found, top, _) = Findings(res);
            inst = top;
            foreach (var brief in found.Where(f => all || ids.Contains(S(f["id"])) || ids.Contains(S(f["sig"]))))
            {
                var f = brief;
                byte[]? jpg = null;
                if (brief["id"] is JsonValue id)
                {
                    var one = await CallUnity("qa_findings", new JsonObject { ["format"] = "json", ["id"] = id.DeepClone(), ["image"] = true }, game);
                    var (full, _, images) = Findings(one);
                    if (full.Count > 0) f = full[0];
                    jpg = images.FirstOrDefault();
                }
                list.Add((f, jpg));
            }
            if (list.Count == 0) return Result(all ? "qa_findings has no findings to report." : "None of these findings exist (any more): " + string.Join(", ", ids), true);
        }
        foreach (var f in picked.OfType<JsonObject>()) list.Add(((JsonObject)f.DeepClone(), null));
        if (list.Count == 0)
        {
            string title = S(args["title"]).Trim();
            if (title.Length == 0) return Result("Pass findings (ids from qa_findings), all=true, or at least a title for a manual report.", true);
            var steps = args["steps"] is JsonArray sa ? sa.Select(S) : S(args["steps"]).Split('\n');
            list.Add((new JsonObject
            {
                ["kind"] = "manual", ["title"] = title, ["message"] = S(args["actual"]), ["stack"] = S(args["stack"]),
                ["steps"] = new JsonArray(steps.Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => (JsonNode)x!).ToArray()),
            }, null));
        }
        else if (list.Count == 1 && S(args["title"]).Trim() is { Length: > 0 } rename) list[0].F["title"] = rename;
        if (S(args["severity"]) is "critical" or "major" or "minor") foreach (var (f, _) in list) f["severity"] = S(args["severity"]);
        var lines = new List<string>();
        foreach (var (f, jpg) in list)
            try { lines.Add(WriteReport(root, code, f, f["instance"] ?? inst, jpg, args, t, g)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Result($"Could not write the report into {root}: {e.Message}", true); }
        if (S(inst?["perfNote"]) is { Length: > 0 } pn) lines.Add(pn);
        return Result(string.Join("\n", lines));
    }

    static string WriteReport(string root, string code, JsonObject f, JsonNode? inst, byte[]? jpg, JsonObject args, Target? t, Registry.Game? g)
    {
        string dir = QaDir(root);
        Directory.CreateDirectory(dir);
        string title = S(f["title"]) is { Length: > 0 } ti ? ti : "Untitled problem", kind = S(f["kind"]), stack = S(f["stack"]);
        string sig = S(f["sig"]) is { Length: > 0 } fs ? fs : Sig("report", kind, title + "\n" + stack);
        var instance = new JsonObject
        {
            ["target"] = S(inst?["target"]) is { Length: > 0 } tn ? tn : t?.Name ?? g?.Name, ["product"] = inst?["product"]?.DeepClone(),
            ["pid"] = inst?["pid"]?.DeepClone() ?? (t != null ? t.Proc?.Id ?? t.Pid : null), ["port"] = inst?["port"]?.DeepClone() ?? t?.Port, ["run"] = f["run"]?.DeepClone(),
        };
        lock (ReportLock)
        {
            string indexFile = Path.Combine(dir, "index.json");
            JsonObject index;
            try { index = JsonNode.Parse(File.ReadAllText(indexFile)) as JsonObject ?? new JsonObject(); } catch (Exception e) when (e is IOException or JsonException) { index = new JsonObject(); }
            var entry = index[sig] as JsonObject;
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string sid = Regex.Replace(sig.ToLowerInvariant(), "[^a-z0-9]", "") is { Length: > 0 } sx ? sx : "0";
            string name = S(entry?["file"]) is { Length: > 0 } old && !old.Contains('/') && !old.Contains('\\') ? old
                : DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } sl ? sl[..Math.Min(sl.Length, 40)].TrimEnd('-') : "report") + "-" + sid[..Math.Min(sid.Length, 6)];
            var instances = entry?["instances"]?.DeepClone() as JsonArray ?? new JsonArray();
            if (!instances.Any(x => JsonNode.DeepEquals(x, instance))) instances.Add(instance.DeepClone());
            entry = new JsonObject
            {
                ["file"] = name, ["title"] = title, ["severity"] = S(f["severity"]) is { Length: > 0 } sv ? sv : "major",
                ["count"] = ((int?)entry?["count"] ?? 0) + 1, ["firstSeen"] = S(entry?["firstSeen"]) is { Length: > 0 } fsn ? fsn : now, ["lastSeen"] = now,
                ["instances"] = instances,
            };
            index[sig] = entry;
            string basePath = Path.Combine(dir, name);
            if (jpg != null) Put(basePath + ".jpg", tmp => File.WriteAllBytes(tmp, jpg));
            var cause = Cause(f, code, root);
            string md = Markdown(f, entry, instance, cause, File.Exists(basePath + ".jpg") ? name + ".jpg" : null, args, t, g);
            Put(basePath + ".md", tmp => File.WriteAllText(tmp, md));
            var json = (JsonObject)f.DeepClone();
            json["report"] = new JsonObject
            {
                ["sig"] = sig, ["count"] = entry["count"]!.DeepClone(), ["instance"] = instance.DeepClone(),
                ["cause"] = cause == null ? null : new JsonObject { ["file"] = cause.Value.File, ["line"] = cause.Value.Line, ["where"] = cause.Value.Where },
            };
            Put(basePath + ".json", tmp => File.WriteAllText(tmp, json.ToJsonString(Indented)));
            Put(indexFile, tmp => File.WriteAllText(tmp, index.ToJsonString(Indented)));
            return ((int)entry["count"]! == 1 ? "New report: " : $"Updated report (reported {entry["count"]} times): ") + basePath + ".md";
        }
    }

    static string Markdown(JsonObject f, JsonObject entry, JsonObject inst, (string File, int Line, string Where)? cause, string? image, JsonObject args, Target? t, Registry.Game? g)
    {
        var b = new StringBuilder();
        b.Append($"# {entry["title"]}\n\n**Severity:** {entry["severity"]}");
        if (S(f["kind"]) is { Length: > 0 } kind) b.Append($" · **Kind:** {kind}");
        if ((int?)f["count"] is > 1 and int n) b.Append($" · **Happened:** {n} times in the game");
        b.Append($" · **Reported:** {entry["count"]} time(s), last {entry["lastSeen"]}\n\n## Environment\n");
        b.Append($"- Game: {g?.Name ?? t?.Name ?? "unknown"}{(g != null ? $" ({(g.Kind == "project" ? "Unity project" : "build")})" : "")}\n");
        string engine = g != null ? Inject.Engine(g) : t?.Unity is { Length: > 0 } u ? "Unity " + u : "";
        if (engine.Length > 0) b.Append($"- Unity: {engine}\n");
        b.Append($"- OS: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}\n- UDriver: {ToolManifest.Version}\n");
        if (S(inst["target"]).Length > 0) b.Append($"- Instance: {inst["target"]}{(inst["pid"] != null ? $", pid {inst["pid"]}" : "")}{(inst["port"] != null ? $", port {inst["port"]}" : "")}\n");
        if (inst["run"] is JsonObject run && run.Count > 0) b.Append($"- Variation: {string.Join(", ", run.Where(p => p.Key != "steps").Select(p => $"{p.Key} {p.Value}"))}\n");
        if (S(f["scene"]).Length > 0) b.Append($"- Scene: {f["scene"]}\n");
        if (S(f["object"]).Length > 0) b.Append($"- Object: {f["object"]}\n");
        var steps = (f["actions"] as JsonArray)?.OfType<JsonObject>().Select(Human).ToList() ?? (f["steps"] as JsonArray)?.Select(S).ToList() ?? new List<string>();
        b.Append("\n## Steps to reproduce\n");
        if (steps.Count == 0) b.Append("Not recorded.\n");
        for (int i = 0; i < steps.Count; i++) b.Append($"{i + 1}. {steps[i]}\n");
        string expected = S(args["expected"]) is { Length: > 0 } ex ? ex : Expected(S(f["kind"]));
        if (expected.Length > 0) b.Append($"\n## Expected\n{expected}\n");
        b.Append($"\n## Actual\n{(S(args["actual"]) is { Length: > 0 } ac ? ac : S(f["message"]) is { Length: > 0 } m ? m : entry["title"])}\n");
        var involved = (f["involved"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var shared = (f["shared"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (cause != null || involved.Count > 0 || shared.Count > 0)
        {
            b.Append("\n## Suspected cause\n");
            if (cause is { } c)
                b.Append(c.File.Length > 0 ? $"- `{c.File}:{c.Line}`{(c.Where.Length > 0 ? $" in `{c.Where}`" : "")}\n" : $"- `{c.Where}` (no source line: build without symbols)\n");
            if (involved.Count > 0 || shared.Count > 0) b.Append("\n### What breaks together\n");
            foreach (var x in involved) b.Append($"- {x["type"]}{(S(x["role"]).Length > 0 ? $" ({x["role"]})" : "")}\n");
            foreach (var x in shared)
                b.Append($"- `{x["field"]}`: read by {Join(x["readers"])}; changed by {Join(x["writers"])}\n");
        }
        if (S(f["stack"]).Length > 0) b.Append($"\n## Stack\n```\n{f["stack"]}\n```\n");
        if (f["events"] is JsonArray ev && ev.Count > 0)
            b.Append("\n## Last log lines\n```\n").Append(string.Join("\n", ev.Select(e => e is JsonObject o ? S(o["text"] ?? o["message"]) : S(e)))).Append("\n```\n");
        if (image != null) b.Append($"\n## Screenshot\n![screenshot]({image})\n");
        return b.ToString();
    }

    static string Join(JsonNode? n) => n is JsonArray a && a.Count > 0 ? string.Join(", ", a.Select(S)) : "nothing found";

    static string Expected(string kind) => kind switch
    {
        "exception" or "error" or "assert" => "No errors or exceptions.",
        "nan" => "Values stay valid numbers.",
        "null" => "Watched objects stay valid.",
        "perf" => "Frames stay smooth (under 50 ms).",
        "gc" or "leak" => "Memory and object counts stay flat.",
        "stuck" => "Every action visibly moves the game forward.",
        "visual" => "Every control is on screen and drawn correctly.",
        "network" => "Network requests succeed.",
        _ => "",
    };

    /// <summary>"Click "Buy" (ui_click name=BuyButton)".</summary>
    static string Human(JsonObject a)
    {
        string tool = S(a["tool"]);
        var args = a["args"] as JsonObject ?? new JsonObject();
        string verb = tool switch
        {
            "ui_click" => "Click", "ui_hover" => "Hover over", "ui_drag" => "Drag", "pointer" => "Move the pointer", "ui_scroll" or "ui_scroll_to" => "Scroll",
            "ui_set_value" => "Set", "ui_focus" => "Focus", "type_text" => "Type", "key" => "Press", "play" => "Play", "wait_for" => "Wait for",
            "time_control" => "Change game time", "editor_play" => "Enter or leave Play Mode", _ => "Run",
        };
        string label = S(a["text"]) is { Length: > 0 } tx ? tx : S(args["text"] ?? args["key"] ?? args["value"]);
        string detail = string.Join(" ", args.Where(p => p.Value is JsonValue).Select(p => $"{p.Key}={p.Value}"));
        return $"{verb}{(label.Length > 0 ? $" \"{label}\"" : "")} ({tool}{(detail.Length > 0 ? " " + detail : "")})";
    }

    static readonly Regex AtFile = new(@"(?:\(at |\bin )(.+?\.cs):(\d+)");
    static readonly Regex Frame = new(@"^\s*(?:at\s+)?([\w.+`<>]+)[.:]([\w<>`]+)\s*\(");

    static (string File, int Line, string Where)? Cause(JsonObject f, string code, string root)
    {
        var w = f["where"] as JsonObject;
        string type = S(w?["type"]), method = S(w?["method"]), file = S(w?["file"]);
        int line = int.TryParse(S(w?["line"]), out int l) ? l : 0;
        if ((file.Length == 0 || line <= 0) && type.Length == 0)
            foreach (var s in S(f["stack"]).Split('\n'))
            {
                var fr = Frame.Match(s);
                if (!fr.Success || Regex.IsMatch(fr.Groups[1].Value, @"^(UnityEngine|UnityEditor|System|Mono|UDriver)\.")) continue;
                (type, method) = (fr.Groups[1].Value, fr.Groups[2].Value);
                if (AtFile.Match(s) is { Success: true } at) (file, line) = (at.Groups[1].Value, int.Parse(at.Groups[2].Value));
                break;
            }
        if ((file.Length == 0 || line <= 0) && type.Length > 0 && method.Length > 0)
        {
            string title = S(f["title"]);
            string? hint = S(f["kind"]) switch
            {
                "leak" when Regex.Match(title, @"^(?:Leak: )?(\w+) count") is { Success: true } m => $@"\bnew\s+{m.Groups[1].Value}\b",
                "nan" when Regex.Match(title, @"\.(\w+)$") is { Success: true } m => $@"\b{m.Groups[1].Value}\s*[-+*/]?=[^=]",
                "perf" => @"^\s*(for|foreach|while)\b.*\n\s*(for|foreach|while)\b",
                "stuck" => @"^\s*(else\s+)?if\s*\(.*\n(\s*\{\s*\n)?.*\b(SetActive\s*\(|enabled\s*=[^=]|interactable\s*=[^=])",
                "visual" => @"\b(anchoredPosition|localPosition|position|offsetMin|offsetMax|sizeDelta)\s*=[^=]",
                _ => null,
            };
            if (Locate(code, type, method, hint) is { Line: > 0 } found) (file, line) = found;
        }
        string where = type.Length > 0 ? $"{type}.{method}" + (w?["ilOffset"] is JsonNode il && file.Length == 0 ? $" (IL offset {il})" : "") : "";
        if (file.Length == 0) return where.Length > 0 ? ("", 0, where) : null;
        foreach (var dir in new[] { code, root })
            if (Path.IsPathFullyQualified(file) && file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) file = Path.GetRelativePath(dir, file);
        return (file.Replace('\\', '/'), line, where);
    }

    /// <summary>The declaration of Type.Method in the C# sources, or the first line inside it that matches hint.</summary>
    static (string File, int Line) Locate(string dir, string type, string method, string? hint)
    {
        var parts = type.Split('.', '+', '/');
        string cls = parts[^1];
        if (cls.StartsWith('<') && parts.Length > 1) { method = cls; cls = parts[^2]; }
        cls = Regex.Replace(cls, @"`\d+$", "");
        if (Regex.Match(method, "<([^>]+)>") is { Success: true } inner) method = inner.Groups[1].Value;
        if (method == ".ctor") method = cls;
        string scan = Directory.Exists(Path.Combine(dir, "Assets")) ? Path.Combine(dir, "Assets") : dir;
        if (!Directory.Exists(scan) || !Regex.IsMatch(cls + method, @"^\w+$")) return ("", 0);
        var clsRx = new Regex($@"\b(class|struct)\s+{cls}\b");
        var declRx = new Regex($@"^\s*(?:[\w<>\[\],.?]+\s+)+{method}\s*(<[^>]*>)?\s*\(|^\s*{method}\s*\(");
        var hintRx = hint != null ? new Regex(hint) : null;
        bool multi = hint?.Contains(@"\n") == true;
        foreach (var file in Directory.EnumerateFiles(scan, "*.cs", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }
            int c = Array.FindIndex(lines, x => clsRx.IsMatch(x));
            if (c < 0) continue;
            for (int i = c; i < lines.Length; i++)
            {
                if (!declRx.IsMatch(lines[i]) || lines[i].TrimEnd().EndsWith(';')) continue;
                if (hintRx != null)
                    for (int j = i, depth = 0; j < lines.Length; j++)
                    {
                        if (hintRx.IsMatch(multi ? string.Join("\n", lines.Skip(j).Take(3)) : lines[j])) return (file, j + 1);
                        depth += lines[j].Count(ch => ch == '{') - lines[j].Count(ch => ch == '}');
                        if (depth <= 0 && lines[j].Contains('}')) break;
                    }
                return (file, i + 1);
            }
        }
        return ("", 0);
    }

    static async Task<JsonNode> QaScenario(JsonObject args, string? want)
    {
        string action = S(args["action"]) is { Length: > 0 } a ? a : "list";
        if (action is not ("list" or "save" or "run")) return Result("action must be list, save or run", true);
        var (_, _, root, err) = await QaPlace("qa_scenario", args, want);
        if (err != null) return Result(err, true);
        string dir;
        try { dir = QaDir(root, "scenarios"); } catch (IOException e) { return Result(e.Message, true); }
        if (action == "list")
        {
            var saved = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToList() : new();
            return Result(saved.Count == 0 ? "No saved scenarios yet (qa_scenario action=save)." : "Saved scenarios: " + string.Join(", ", saved));
        }
        string name = S(args["name"]).Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_][A-Za-z0-9 _.-]{0,59}$")) return Result("name is required: letters, digits, space, _ . - (up to 60).", true);
        string file = Path.Combine(dir, name + ".json");
        if (action == "save")
        {
            if (args["steps"] is not JsonArray steps || steps.Count == 0 || steps.Count > 100) return Result("steps must be 1-100 items {tool, args, expect, expectNot}.", true);
            if (steps.Any(s => S(s?["tool"]) is "" or "qa_scenario")) return Result("every step needs a tool (qa_scenario cannot run itself).", true);
            Directory.CreateDirectory(dir);
            Put(file, tmp => File.WriteAllText(tmp, new JsonObject { ["name"] = name, ["steps"] = steps.DeepClone() }.ToJsonString(Indented)));
            return Result($"Saved scenario '{name}' with {steps.Count} steps: {file}");
        }
        JsonArray list;
        try { list = JsonNode.Parse(File.ReadAllText(file))?["steps"] as JsonArray ?? new JsonArray(); }
        catch (Exception e) when (e is IOException or JsonException) { return Result($"No saved scenario '{name}'.", true); }
        var lines = new List<string>();
        int passed = 0;
        foreach (var step in list)
        {
            string tool = S(step?["tool"]);
            var stepArgs = step?["args"]?.DeepClone() as JsonObject ?? new JsonObject();
            string? target = step?["target"]?.ToString() ?? want;
            JsonNode res = tool == "qa_scenario" ? Result("qa_scenario cannot run itself", true) : Blocked(tool, stepArgs) is string off ? Result(off, true) : await Run(tool, stepArgs, target);
            string text = string.Join("\n", (res["content"] as JsonArray ?? new JsonArray()).Select(c => S(c?["text"])));
            string expect = S(step?["expect"]), expectNot = S(step?["expectNot"]);
            string? why = res["isError"]?.GetValue<bool>() == true ? "error" : expect.Length > 0 && !text.Contains(expect, StringComparison.OrdinalIgnoreCase) ? $"no \"{expect}\" in the result"
                : expectNot.Length > 0 && text.Contains(expectNot, StringComparison.OrdinalIgnoreCase) ? $"\"{expectNot}\" in the result" : null;
            string head = $"[{lines.Count}] {tool}";
            if (why == null) { passed++; lines.Add("PASS " + head); continue; }
            lines.Add($"FAIL {head}: {why}: {(text.Length > 300 ? text[..300] + "…" : text)}");
            break;
        }
        bool ok = passed == list.Count;
        return Result($"Scenario '{name}': {passed}/{list.Count} steps passed{(ok ? "" : ", stopped at the first failure")}.\n" + string.Join("\n", lines), !ok);
    }

    static async Task<JsonNode> QaParallel(JsonObject args, string? want)
    {
        string tool = S(args["tool"]) is { Length: > 0 } tl ? tl : "qa_crawl";
        if (!GameTool(tool) || tool == "batch") return Result("tool must be an in-game tool such as qa_crawl or play.", true);
        if ((Blocked(tool, null) ?? Blocked("qa_findings", null)) is string off) return Result(off, true);
        if (args["targets"] is not (null or JsonArray) || args["args"] is not (null or JsonObject)) return Result("targets must be an array of names and args an object.", true);
        var all = await Targets();
        List<Target> runs;
        if (args["targets"] is JsonArray names && names.Count > 0)
        {
            runs = names.Select(n => Named(all, S(n), "game")).OfType<Target>().Where(x => x.Kind == "game").DistinctBy(x => x.Port).ToList();
            if (runs.Count < names.Count) return Result($"Not every name in targets is a running game. Targets: {Names(all)}.", true);
        }
        else
        {
            var t = Pick(all, "game", want, out var e);
            if (t == null) return Result(e ?? "No game is running. Start one with app_launch.", true);
            List<Target> Copies() => all.Where(x => x.Kind == "game" && x.Up && (x.Port == t.Port || t.Game != null && x.Game?.Id == t.Game.Id)).ToList();
            runs = Copies();
            int copies = IntArg(args, "copies", 0);
            if (copies > 8) return Result("copies: at most 8.", true);
            if (copies > runs.Count)
            {
                if (t.Game is not { Kind: "build" } build) return Result("copies can start more copies only of a game build saved in the UDriver panel.", true);
                var (lastGame, lastTarget) = (Me.LastGame, Me.LastTarget);
                var r = await LaunchCopies(new JsonObject { ["path"] = build.Id, ["copies"] = copies - runs.Count });
                if (r["isError"]?.GetValue<bool>() == true) return Result($"Could not start the copies: {r["content"]?[0]?["text"]}", true);
                (Me.LastGame, Me.LastTarget) = (lastGame, lastTarget);
                all = await Targets();
                runs = Copies();
            }
        }
        if (runs.Count == 0) return Result("No running copy of this game answers.", true);
        bool vary = args["vary"] is not JsonValue vv || !vv.TryGetValue(out bool vb) || vb;
        var baseArgs = args["args"] as JsonObject ?? new JsonObject();
        int seed = IntArg(baseArgs, "seed", 1);
        var plan = runs.Select((r, i) =>
        {
            var a = (JsonObject)baseArgs.DeepClone();
            if (vary) { a["seed"] = seed + i; a["delayMs"] = new[] { 0, 150, 400 }[i % 3]; a["timeScale"] = new[] { 1, 2 }[i % 2]; }
            return (T: r, Args: a);
        }).ToList();
        var results = await Task.WhenAll(plan.Select(p => CallUnity(tool, (JsonObject)p.Args.DeepClone(), p.T.Name)));
        var finds = await Task.WhenAll(runs.Select(r => CallUnity("qa_findings", new JsonObject { ["format"] = "json" }, r.Name)));
        var bySig = new Dictionary<string, (JsonObject F, List<string> Hits)>();
        var sb = new StringBuilder($"Ran {tool} on {runs.Count} copies:\n");
        for (int i = 0; i < runs.Count; i++)
        {
            var (r, a) = plan[i];
            int pid = r.Proc?.Id ?? r.Pid;
            string first = S(results[i]["content"]?[0]?["text"]).Split('\n')[0];
            sb.Append($"- {r.Name} (pid {pid}, port {r.Port}, seed {a["seed"]}, delayMs {a["delayMs"]}, timeScale {a["timeScale"]}): {(results[i]["isError"]?.GetValue<bool>() == true ? "error: " : "")}{first}\n");
            foreach (var f in Findings(finds[i]).Found)
            {
                string sig = S(f["sig"]) is { Length: > 0 } s ? s : S(f["title"]);
                var run = f["run"] as JsonObject;
                string how = run != null && run.Count > 0 ? string.Join(", ", run.Where(p => p.Key != "steps").Select(p => $"{p.Key} {p.Value}")) : $"seed {a["seed"]}, delayMs {a["delayMs"]}, timeScale {a["timeScale"]}";
                if (!bySig.TryGetValue(sig, out var hit)) bySig[sig] = hit = (f, new List<string>());
                hit.Hits.Add($"{r.Name} pid {pid} port {r.Port} id {S(f["id"])} ({how})");
            }
        }
        if (bySig.Count == 0) sb.Append("\nNo findings on any copy.");
        else sb.Append($"\n{bySig.Count} distinct findings (by signature):\n");
        foreach (var (sig, (f, hits)) in bySig.OrderBy(x => Array.IndexOf(new[] { "critical", "major", "minor" }, S(x.Value.F["severity"]))))
        {
            sb.Append($"- [{S(f["severity"])}] {S(f["title"])} (sig {sig}): {hits.Count}/{runs.Count} copies: {string.Join("; ", hits)}\n");
            if (hits.Count < runs.Count && runs.Count > 1)
                sb.Append("  Only some variations reproduce it: likely breaks in combination (timing, speed or order). Compare the variations above, then qa_isolate on a copy that hit it.\n");
        }
        if (bySig.Count > 0) sb.Append("Next: qa_isolate on a copy that hit each finding, then qa_report with target set to that copy.");
        return Result(sb.ToString().TrimEnd());
    }
}

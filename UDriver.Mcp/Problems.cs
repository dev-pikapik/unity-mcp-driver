using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UDriver;

/// <summary>Reports about UDriver itself: ~/.udriver/reports/&lt;sig&gt;.json, shown in the panel's Problems section.</summary>
internal static partial class Program
{
    static string ReportDir => Path.Combine(HomeDir, "reports");
    static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly object ProblemLock = new();
    static readonly List<JsonObject> Recent = new();
    static readonly Dictionary<string, int> Repeats = new();
    const string IssuePage = "https://github.com/dev-pikapik/unity-mcp-driver/issues/new";

    static readonly Regex SecretName = new("token|secret|password|passwd|key|auth|cookie|session", RegexOptions.IgnoreCase);
    static readonly Regex SecretPair = new(@"(\w*(?:token|secret|password|passwd|key|auth|cookie|session)\w*[""']?\s*[=:]\s*[""']?)[^\s""'&,;]+", RegexOptions.IgnoreCase);
    static readonly Regex Email = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+");
    static readonly Regex OtherHome = new(@"(?:[A-Za-z]:)?[\\/](?:Users|home)[\\/][^\\/\s""']+", RegexOptions.IgnoreCase);
    static readonly Regex UrlQuery = new(@"(\w+://[^\s?#""']+)\?[^\s""']*");
    static readonly Regex LongToken = new(@"(?<![\w+=-])(?=[\w+=-]*\d)(?=[\w+=-]*[A-Za-z])[\w+=-]{24,}");

    internal static string CleanText(string? s, int cap = 400)
    {
        s ??= "";
        if (UserHome.Length > 1) s = s.Replace(UserHome, "~");
        s = OtherHome.Replace(s, "~");
        s = UrlQuery.Replace(s, "$1?[redacted]");
        s = Email.Replace(s, "[email]");
        s = SecretPair.Replace(s, "$1[redacted]");
        s = LongToken.Replace(s, "[redacted]");
        return s.Length > cap ? s[..cap] + "…" : s;
    }

    static JsonNode? Clean(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, SecretName.IsMatch(p.Key) ? (JsonNode?)"[redacted]" : Clean(p.Value)))),
        JsonArray a => new JsonArray(a.Take(20).Select(Clean).ToArray()),
        JsonValue v when v.TryGetValue(out string? s) => CleanText(s),
        _ => n?.DeepClone(),
    };

    static string Sig(string kind, string tool, string error)
    {
        string e = Regex.Replace(error, @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", "G");
        e = Regex.Replace(e, @"""[^""]*""|'[^']*'|“[^”]*”", "Q");
        e = Regex.Replace(e, @"(~|[A-Za-z]:)?([\\/][^\s\\/:()]+)+[\\/]?", "P");
        e = Regex.Replace(e, @"\d+", "N");
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(kind + "\n" + tool + "\n" + e)))[..12].ToLowerInvariant();
    }

    static void Remember(string tool, JsonObject args, JsonNode? res)
    {
        bool ok = res != null && res["isError"]?.GetValue<bool>() != true;
        lock (ProblemLock)
        {
            Recent.Add(new JsonObject { ["tool"] = tool, ["args"] = Clean(args), ["ok"] = ok });
            if (Recent.Count > 10) Recent.RemoveAt(0);
        }
    }

    static async Task<string> EngineOf(int port, string? want)
    {
        try
        {
            var all = await Targets();
            var t = port > 0 ? all.FirstOrDefault(x => x.Port == port) : Pick(all, "game", want, out _) ?? Pick(all, "editor", want, out _);
            if (t == null) return "";
            string e = t.Game != null ? Inject.Engine(t.Game) : "";
            if (e.Length == 0 && t.Unity.Length > 0) e = "Unity " + t.Unity;
            return t.Kind == "editor" || t.Game?.Kind == "project" ? e + " · editor" : e;
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Saves or bumps the report and returns how many times it was seen.</summary>
    static async Task<int> Record(string kind, string tool, JsonObject? args, string error, string stack, int port, string? want, JsonObject? extra = null, int seen = 1)
    {
        try
        {
            error = CleanText(error, 2000);
            string sig = Sig(kind, tool, kind == "agent" ? (string?)extra?["what"] + "\n" + error : error);
            string engine = await EngineOf(port, want);
            lock (ProblemLock)
            {
                string file = Path.Combine(ReportDir, sig + ".json");
                JsonObject? old = null;
                try { old = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch (Exception e) when (e is IOException or JsonException) { }
                string now = DateTime.UtcNow.ToString("o");
                var r = new JsonObject
                {
                    ["sig"] = sig, ["kind"] = kind, ["version"] = ToolManifest.Version,
                    ["os"] = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture, ["unity"] = engine,
                    ["tool"] = tool, ["args"] = Clean(args), ["error"] = error, ["stack"] = CleanText(stack, 3000),
                    ["calls"] = new JsonArray(Recent.Select(c => c.DeepClone()).ToArray()),
                    ["count"] = old != null ? ((int?)old["count"] ?? 0) + 1 : seen,
                    ["firstSeen"] = (string?)old?["firstSeen"] ?? now, ["lastSeen"] = now,
                };
                foreach (var (k, v) in extra ?? new JsonObject()) r[k] = Clean(v);
                WritePrivate(file, r.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return (int)r["count"]!;
            }
        }
        catch (Exception e) { Console.Error.WriteLine("UDriver: could not save a problem report: " + e.Message); return 0; }
    }

    /// <summary>Looks at a game's answer: UDriver crashes and timeouts are recorded, the same error three times becomes a report.</summary>
    static async Task<JsonNode> Watch(int port, string name, JsonObject args, JsonNode res)
    {
        if (res["isError"]?.GetValue<bool>() != true) return res;
        string text = (string?)res["content"]?[0]?["text"] ?? "";
        if (text.StartsWith("cancelled:", StringComparison.Ordinal)) return res;
        if (text.StartsWith("timeout:", StringComparison.Ordinal))
        {
            bool answers;
            try { answers = (await Http2(HttpMethod.Post, port, "/call", new JsonObject { ["tool"] = "status", ["args"] = new JsonObject() }.ToJsonString(), 3)).status == 200; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { answers = false; }
            if (!answers)
                return Result(text + "\nThe game's main thread is not responding: it does not answer status either, so the game itself is frozen or stuck in a long loop. " +
                              "This is a problem in the game, not in UDriver: read its last log lines or restart it.", true);
            await Record("timeout", name, args, text, "", port, null);
            return res;
        }
        int nl = text.IndexOf('\n');
        string frame = nl < 0 ? "" : text[(nl + 1)..].Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal)) ?? "";
        if (Regex.IsMatch(text, @"^[\w.]+(Exception|Error): ") && frame.Contains("UDriver."))
        {
            await Record("exception", name, args, text[..nl], text[(nl + 1)..], port, null);
            return res;
        }
        string sig = Sig("repeated", name, CleanText(text, 2000));
        int n;
        lock (ProblemLock) n = Repeats[sig] = Repeats.GetValueOrDefault(sig) + 1;
        if (n >= 3) await Record("repeated", name, args, text, "", port, null, seen: n);
        return res;
    }

    static async Task<JsonNode> ReportProblem(JsonObject args, string? want)
    {
        string what = args["what"]?.ToString().Trim() ?? "";
        if (what.Length == 0) return Result("what is required: say in a sentence or two what you needed UDriver to do.", true);
        var extra = new JsonObject { ["what"] = CleanText(what, 1000) };
        foreach (var k in new[] { "tried", "missing" })
            if (args[k]?.ToString() is { Length: > 0 } v) extra[k] = CleanText(v, 1000);
        int n = await Record("agent", args["tool"]?.ToString() ?? "", null, args["error"]?.ToString() ?? "", "", 0, want, extra);
        if (n == 0) return Result("The problem report could not be saved on this computer.", true);
        return Result($"Saved as a UDriver problem report ({(n == 1 ? "first time" : n + " times so far")}). The owner can send it to the UDriver developer from the Problems section of the UDriver panel. " +
                      "Carry on with a workaround if there is one.");
    }

    /// <summary>Saved reports, newest first, each with a prefilled GitHub issue link built only from sanitized fields.</summary>
    internal static List<JsonObject> Problems()
    {
        var list = new List<JsonObject>();
        string[] files;
        try { files = Directory.GetFiles(ReportDir, "*.json"); } catch (IOException) { return list; }
        foreach (var f in files)
            try
            {
                if (JsonNode.Parse(File.ReadAllText(f)) is not JsonObject r || (string?)r["sig"] != Path.GetFileNameWithoutExtension(f)) continue;
                string first = ((string?)r["kind"] == "agent" ? (string?)r["what"] : (string?)r["error"])?.Split('\n')[0] ?? "";
                string title = $"[{r["kind"]}] {(r["tool"]?.ToString() is { Length: > 0 } tl ? tl + ": " : "")}{first}";
                if (title.Length > 120) title = title[..120] + "…";
                string body = IssueBody(r);
                list.Add(new JsonObject
                {
                    ["id"] = (string?)r["sig"], ["kind"] = (string?)r["kind"], ["tool"] = (string?)r["tool"], ["what"] = (string?)r["what"],
                    ["error"] = first, ["count"] = (int?)r["count"] ?? 1, ["firstSeen"] = (string?)r["firstSeen"], ["lastSeen"] = (string?)r["lastSeen"],
                    ["issueUrl"] = $"{IssuePage}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}",
                });
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { }
        return list.OrderByDescending(p => (string?)p["lastSeen"], StringComparer.Ordinal).ToList();
    }

    static string IssueBody(JsonObject r)
    {
        var b = new StringBuilder();
        string kind = (string?)r["kind"] ?? "";
        b.Append(kind switch
        {
            "agent" => "An agent reported that UDriver could not do what it needed.",
            "timeout" => "A UDriver tool timed out while the game was still responsive.",
            "exception" => "A UDriver tool stopped with an internal error.",
            _ => "A UDriver tool failed the same way several times in a row.",
        }).Append("\n\n");
        foreach (var (k, label) in new[] { ("what", "Needed"), ("tried", "Tried"), ("missing", "Missing") })
            if ((string?)r[k] is { Length: > 0 } v) b.Append($"**{label}:** {v}\n\n");
        b.Append($"| | |\n|---|---|\n| UDriver | {r["version"]} |\n| OS | {r["os"]} |\n| Unity | {r["unity"]} |\n| Tool | {r["tool"]} |\n")
         .Append($"| Seen | {r["count"]} times, {r["firstSeen"]} – {r["lastSeen"]} |\n\n");
        if (r["args"] is JsonObject { Count: > 0 } a) b.Append("**Arguments**\n```json\n").Append(a.ToJsonString()).Append("\n```\n\n");
        if ((string?)r["error"] is { Length: > 0 } err) b.Append("**Error**\n```\n").Append(err).Append("\n```\n\n");
        if ((string?)r["stack"] is { Length: > 0 } st) b.Append("**Stack**\n```\n").Append(st).Append("\n```\n\n");
        if (r["calls"] is JsonArray { Count: > 0 } calls)
        {
            b.Append("**Last calls**\n");
            foreach (var c in calls) b.Append($"- `{c?["tool"]}` {((bool?)c?["ok"] == true ? "ok" : "error")} `{c?["args"]?.ToJsonString()}`\n");
        }
        string s = b.ToString();
        return s.Length > 7000 ? s[..6990] + "\n…" : s;
    }

    internal static bool DismissProblem(string id)
    {
        if (!Regex.IsMatch(id, "^[0-9a-f]{12}$")) return false;
        lock (ProblemLock)
        {
            Repeats.Remove(id);
            string file = Path.Combine(ReportDir, id + ".json");
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }
}

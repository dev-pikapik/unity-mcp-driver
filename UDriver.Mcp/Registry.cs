using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

/// <summary>Games the owner allowed (~/.udriver/targets.json) and the agents connected on this computer (~/.udriver/agents).</summary>
internal static class Registry
{
    internal sealed class Game
    {
        public string Id = "", Name = "", Kind = "", Path = "", Access = "";
        public int Pid, SeenPid;
        public long PidStart, SeenStart;
    }

    sealed class Data
    {
        public string DefaultAccess = "play";
        public List<Game> Games = new();
        public SortedSet<string> ToolsOff = new() { "state_call" };
        public SortedDictionary<string, SortedSet<string>> ClientToolsOff = new();   // client id -> tools turned off for it
    }

    internal static readonly string[] Levels = { "watch", "play", "full" };
    internal static int Level(string access) => access == "none" ? -1 : Math.Max(Array.IndexOf(Levels, access), 0);

    static string File_ => Path.Combine(Program.HomeDir, "targets.json");
    static string AgentDir => Path.Combine(Program.HomeDir, "agents");
    static readonly StringComparison PathCmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static readonly object Sync = new();
    static Data _data = new();
    static (DateTime, long) _stamp = (default, -2);

    // ---------------- file access ----------------

    /// <summary>Serializes writers of all server processes; the lock is released when the process dies.</summary>
    static T Locked<T>(string file, Func<T> work)
    {
        Program.PrivateDir(Path.GetDirectoryName(file)!);
        var opt = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opt.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        for (int i = 0; ; i++)
        {
            FileStream held;
            try { held = new FileStream(file, opt); }
            catch (IOException) when (i < 500) { Thread.Sleep(5 + Random.Shared.Next(10)); continue; }
            using (held) return work();
        }
    }

    static Data Parse(string text)
    {
        var j = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("targets.json is not a JSON object");
        var d = new Data { DefaultAccess = Valid((string?)j["defaultAccess"]) ?? "play" };
        foreach (var g in j["games"] as JsonArray ?? new JsonArray())
            if (g is JsonObject o && (string?)o["id"] is { Length: > 0 } id)
                d.Games.Add(new Game
                {
                    Id = id, Name = (string?)o["name"] ?? id, Kind = (string?)o["kind"] == "project" ? "project" : "build", Path = (string?)o["path"] ?? "",
                    Access = (string?)o["access"] == "none" ? "none" : Valid((string?)o["access"]) ?? "watch", Pid = (int?)o["pid"] ?? 0, PidStart = (long?)o["pidStart"] ?? 0,
                    SeenPid = (int?)o["seenPid"] ?? 0, SeenStart = (long?)o["seenStart"] ?? 0,
                });
        if (j["toolsOff"] is JsonArray off) d.ToolsOff = new(off.Select(x => (string?)x ?? "").Where(x => x.Length > 0));
        foreach (var (client, tools) in j["clientToolsOff"] as JsonObject ?? new JsonObject())
            if (tools is JsonArray a) d.ClientToolsOff[client] = new(a.Select(x => (string?)x ?? "").Where(x => x.Length > 0));
        return d;
    }

    static JsonObject ToJson(Game g)
    {
        var o = new JsonObject { ["id"] = g.Id, ["name"] = g.Name, ["kind"] = g.Kind, ["path"] = g.Path, ["access"] = g.Access };
        if (g.Pid > 0) { o["pid"] = g.Pid; o["pidStart"] = g.PidStart; }
        if (g.SeenPid > 0) { o["seenPid"] = g.SeenPid; o["seenStart"] = g.SeenStart; }
        return o;
    }

    static string? Valid(string? access) => access != null && Levels.Contains(access) ? access : null;

    static Data Load()
    {
        lock (Sync)
        {
            var fi = new FileInfo(File_);
            var stamp = fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (default(DateTime), -1L);
            if (stamp == _stamp) return _data;
            if (!fi.Exists) { _stamp = stamp; return _data = new Data(); }
            for (int i = 0; i < 5; i++)
                try { _data = Parse(File.ReadAllText(File_)); _stamp = stamp; break; }
                catch (Exception) when (i < 4) { Thread.Sleep(20); }
                catch (Exception e) { Console.Error.WriteLine("UDriver: cannot read " + File_ + ": " + e.Message); }
            return _data;
        }
    }

    static T Change<T>(Func<Data, T> work) => Locked(File_ + ".lock", () =>
    {
        var d = File.Exists(File_) ? Parse(File.ReadAllText(File_)) : new Data();
        T result = work(d);
        static JsonArray Names(IEnumerable<string> s) => new(s.Select(x => (JsonNode)x!).ToArray());
        var json = new JsonObject { ["defaultAccess"] = d.DefaultAccess, ["games"] = new JsonArray(d.Games.Select(g => (JsonNode)ToJson(g)).ToArray()) };
        json["toolsOff"] = Names(d.ToolsOff);
        if (d.ClientToolsOff.Count > 0) json["clientToolsOff"] = new JsonObject(d.ClientToolsOff.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)Names(x.Value))));
        Program.WritePrivate(File_, json.ToJsonString(new() { WriteIndented = true }));
        lock (Sync) _stamp = (default, -2);
        return result;
    });

    // ---------------- games ----------------

    internal static string DefaultAccess => Load().DefaultAccess;
    internal static List<Game> Games => Load().Games;

    internal static Game? Find(string want) => Load().Games.FirstOrDefault(g => g.Id == want) ?? Load().Games.FirstOrDefault(g => g.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
        ?? (Path.IsPathFullyQualified(want) ? ForPath(want) : null);

    /// <summary>The .app for a file inside it; links resolved where the path exists.</summary>
    internal static string Norm(string path)
    {
        string real;
        try { real = Program.RealPath(path); } catch { real = Path.GetFullPath(path); }
        int i = real.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return i > 0 ? real[..(i + 4)] : real;
    }

    static Game? Match(List<Game> games, string norm, int pid) =>
        games.FirstOrDefault(g => norm.Length > 0 && (g.Path.Equals(norm, PathCmp) || g.Kind == "build" && norm.StartsWith(g.Path + Path.DirectorySeparatorChar, PathCmp)))
        ?? (pid > 0 ? games.FirstOrDefault(g => g.Pid == pid && Same(g.Pid, g.PidStart) || g.SeenPid == pid && Same(g.SeenPid, g.SeenStart)) : null);

    static Process? Proc(int pid, long start)
    {
        if (pid <= 0) return null;
        try
        {
            var p = Process.GetProcessById(pid);
            if (!p.HasExited && Math.Abs(p.StartTime.ToUniversalTime().Ticks - start) < TimeSpan.TicksPerSecond) return p;
            p.Dispose();
        }
        catch { }
        return null;
    }

    static bool Same(int pid, long start)
    {
        using var p = Proc(pid, start);
        return p != null;
    }

    static long StartOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
    }

    internal static Game? ForPath(string path) => Match(Load().Games, Norm(path), 0);

    /// <summary>Kind, real path and name of a Unity project folder or game build; an error text when it is neither.</summary>
    internal static string? Classify(string path, out string kind, out string real, out string name)
    {
        kind = real = name = "";
        path = path.Trim();
        if (path.Length > 1) path = path.TrimEnd('/', '\\');
        if (path.Length == 0) return "Enter the path of a Unity project folder or a game.";
        if (!Path.IsPathFullyQualified(path)) return "Enter the full path, starting from the disk root: " + path;
        try { real = Program.RealPath(path); } catch { return "Nothing was found at this path: " + path; }
        int app = real.IndexOf(".app" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (app > 0 && File.Exists(real)) real = real[..(app + 4)];
        if (Directory.Exists(real))
        {
            if (Directory.Exists(Path.Combine(real, "Assets")) && Directory.Exists(Path.Combine(real, "ProjectSettings")))
            { kind = "project"; name = Path.GetFileName(real); return null; }
            if (real.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(Path.Combine(real, "Contents", "Resources", "Data"))) return "This app is not a Unity game (it has no Contents/Resources/Data folder).";
                kind = "build"; name = Path.GetFileNameWithoutExtension(real); return null;
            }
            if (Program.GameFile(real) is not string exe)
                return "This folder is neither a Unity project (no Assets and ProjectSettings folders inside) nor a game build (no game file next to a NAME_Data folder).";
            real = exe;
        }
        else if (!File.Exists(real)) return "Nothing was found at this path: " + path;
        else if (!real.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Path.ChangeExtension(real, null) + "_Data"))
            return "This file is not a Unity game: the NAME_Data folder that belongs to it is missing.";
        kind = "build"; name = Path.GetFileNameWithoutExtension(real);
        return null;
    }

    static string UniqueName(Data d, string name, Game? self = null)
    {
        name = name.Trim().Length > 0 ? name.Trim() : "game";
        string n = name;
        for (int i = 2; d.Games.Any(g => g != self && g.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++) n = name + "-" + i;
        return n;
    }

    static Game AddTo(Data d, string kind, string real, string name, string? access)
    {
        var g = new Game { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Name = UniqueName(d, name), Kind = kind, Path = real, Access = access ?? d.DefaultAccess };
        d.Games.Add(g);
        return g;
    }

    /// <summary>Gives every live target its record; unknown ones are added with no access until the owner allows them.
    /// The process is remembered so a running game keeps its record when the owner changes the path.</summary>
    internal static void Attach(List<Program.Target> targets)
    {
        var games = Load().Games;
        var unknown = new List<(Program.Target T, string Norm)>();
        var moved = new List<(string Id, int Pid)>();
        foreach (var t in targets)
        {
            string norm = t.Path.Length > 0 ? Norm(t.Path) : "";
            int pid = t.Proc?.Id ?? t.Pid;
            t.Game = Match(games, norm, pid);
            if (t.Game == null && norm.Length > 0) unknown.Add((t, norm));
            else if (t.Game != null && pid > 0 && t.Game.SeenPid != pid && !targets.Any(o => (o.Proc?.Id ?? o.Pid) == t.Game.SeenPid)) moved.Add((t.Game.Id, pid));
        }
        if (unknown.Count == 0 && moved.Count == 0) return;
        try
        {
            Change(d =>
            {
                foreach (var (id, pid) in moved)
                    if (d.Games.FirstOrDefault(g => g.Id == id) is { } g) { g.SeenPid = pid; g.SeenStart = StartOf(pid); }
                foreach (var (t, norm) in unknown)
                {
                    if ((t.Game = Match(d.Games, norm, t.Proc?.Id ?? t.Pid)) != null) continue;
                    bool known = Classify(norm, out string kind, out string real, out string name) == null;
                    if (!known) { kind = t.Kind == "editor" || Directory.Exists(norm) ? "project" : "build"; real = norm; name = Path.GetFileNameWithoutExtension(norm); }
                    t.Game = AddTo(d, kind, real, t.Name.Length > 0 && kind == "build" ? t.Name : name, "none");
                    if ((t.Proc?.Id ?? t.Pid) is > 0 and int pid) { t.Game.SeenPid = pid; t.Game.SeenStart = StartOf(pid); }
                }
                return 0;
            });
        }
        catch (Exception e) { Console.Error.WriteLine("UDriver: cannot update targets.json: " + e.Message); }
    }

    /// <summary>Records the process of a build started through UDriver so the owner can stop it.</summary>
    internal static void Started(string exe, Process proc)
    {
        try
        {
            long start = proc.StartTime.ToUniversalTime().Ticks;
            string norm = Norm(exe);
            Change(d =>
            {
                var g = Match(d.Games, norm, 0);
                if (g == null)
                {
                    bool known = Classify(norm, out _, out string real, out string n) == null;
                    g = AddTo(d, "build", known ? real : norm, n.Length > 0 ? n : Path.GetFileNameWithoutExtension(norm), null);
                }
                g.Pid = proc.Id; g.PidStart = start;
                return 0;
            });
        }
        catch (Exception e) { Console.Error.WriteLine("UDriver: cannot update targets.json: " + e.Message); }
    }

    /// <summary>The live process of a game started through UDriver; null after it exited or its pid was reused.</summary>
    internal static Process? Running(Game g) => Proc(g.Pid, g.PidStart);

    // ---------------- owner changes (panel only) ----------------

    internal static string? Add(string path, string? access, out Game? added)
    {
        added = null;
        if (access != null && Valid(access) == null) return "Access must be watch, play or full.";
        if (Classify(path, out string kind, out string real, out string name) is string err) return err;
        if (kind == "build" && Inject.Ensure(Inject.GameExe(real)) is string cannot) return cannot;
        added = Change(d =>
        {
            var same = Match(d.Games, real, 0);
            if (same != null && same.Path.Equals(real, PathCmp)) { same.Access = access ?? same.Access; return same; }
            return AddTo(d, kind, real, name, access);
        });
        return null;
    }

    internal static string? Edit(string id, string? path, string? access, string? name)
    {
        string kind = "", real = "";
        if (access != null && Valid(access) == null) return "Access must be watch, play or full.";
        if (path != null && Classify(path, out kind, out real, out _) is string err) return err;
        return Change(d =>
        {
            var g = d.Games.FirstOrDefault(x => x.Id == id);
            if (g == null) return "This game is no longer in the list.";
            if (path != null) { g.Path = real; g.Kind = kind; }
            if (access != null) g.Access = access;
            if (name != null) g.Name = UniqueName(d, name, g);
            return (string?)null;
        });
    }

    internal static bool Remove(string id) => Change(d => d.Games.RemoveAll(g => g.Id == id) > 0);

    internal static string? SetDefault(string access) =>
        Valid(access) == null ? "Access must be watch, play or full." : Change(d => { d.DefaultAccess = access; return (string?)null; });

    // ---------------- tools turned off in the panel ----------------

    internal static bool ToolOff(string tool, string client)
    {
        var d = Load();
        return d.ToolsOff.Contains(tool) || d.ClientToolsOff.TryGetValue(client, out var off) && off.Contains(tool);
    }

    internal static (SortedSet<string> All, SortedDictionary<string, SortedSet<string>> Clients) ToolsOff()
    {
        var d = Load();
        return (d.ToolsOff, d.ClientToolsOff);
    }

    /// <summary>client null: for every agent.</summary>
    internal static void SetTool(IEnumerable<string> tools, string? client, bool on) => Change(d =>
    {
        var off = client == null ? d.ToolsOff : d.ClientToolsOff.TryGetValue(client, out var s) ? s : d.ClientToolsOff[client] = new();
        if (on) off.ExceptWith(tools); else off.UnionWith(tools);
        if (client != null && off.Count == 0) d.ClientToolsOff.Remove(client);
        return 0;
    });

    internal static void ForgetClient(string client) => Change(d => d.ClientToolsOff.Remove(client));

    // ---------------- agents ----------------

    static string AgentFile(int n, string ext) => Path.Combine(AgentDir, n + ext);
    static readonly List<Program.Session> Mine = new();

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    static JsonObject? ReadAgent(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch { return null; }
    }

    static void Write(Program.Session s) => Program.WritePrivate(AgentFile(s.Slot, ".json"), new JsonObject
    {
        ["label"] = s.Label, ["name"] = s.Name, ["pid"] = Environment.ProcessId, ["from"] = s.Local ? "this computer" : "remote", ["client"] = s.Client,
        ["target"] = s.LastTarget, ["lastSeen"] = s.LastSeen == default ? null : s.LastSeen.ToString("O"),
    }.ToJsonString());

    /// <summary>Gives the session the lowest free "agent N" on this computer.</summary>
    internal static void Claim(Program.Session s)
    {
        Locked(Path.Combine(AgentDir, ".lock"), () =>
        {
            var taken = new HashSet<int>();
            foreach (var f in Directory.GetFiles(AgentDir, "*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int n))
                {
                    if (ReadAgent(f) is { } a && (int?)a["pid"] is int pid && Alive(pid)) taken.Add(n);
                    else { try { File.Delete(f); File.Delete(AgentFile(n, ".off")); } catch { } }
                }
            int slot = 1;
            while (taken.Contains(slot)) slot++;
            try { File.Delete(AgentFile(slot, ".off")); } catch { }
            s.Slot = slot; s.Label = "agent " + slot;
            Write(s);
            return 0;
        });
        lock (Mine)
        {
            if (Mine.Count == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (Mine) foreach (var m in Mine.ToList()) Release(m); };
            Mine.Add(s);
        }
    }

    /// <summary>The name the agent gave in initialize, shown in the panel instead of "agent N": no control characters, at most 64.</summary>
    internal static void Rename(Program.Session s, string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 64) name = name.Substring(0, char.IsHighSurrogate(name[63]) ? 63 : 64);
        s.Name = name;
        s.Written = default;
        Touch(s);
    }

    internal static void Touch(Program.Session s)
    {
        if (s.Slot == 0 || s.Off) return;
        var now = DateTime.UtcNow;
        if (now - s.Written < TimeSpan.FromSeconds(1) && s.LastTarget == s.WrittenTarget) return;
        s.Written = now; s.WrittenTarget = s.LastTarget;
        try { Write(s); } catch { }
    }

    internal static void Release(Program.Session s)
    {
        if (s.Slot == 0) return;
        try { File.Delete(AgentFile(s.Slot, ".json")); File.Delete(AgentFile(s.Slot, ".off")); } catch { }
        lock (Mine) Mine.Remove(s);
        s.Slot = 0;
    }

    /// <summary>Once the owner disconnects this agent it stays refused until it is restarted.</summary>
    internal static bool Revoked(Program.Session s)
    {
        if (s.Off) return true;
        if (s.Slot == 0) return false;
        string f = AgentFile(s.Slot, ".off");
        try { if (File.Exists(f) && File.ReadAllText(f).Trim() == Environment.ProcessId.ToString()) s.Off = true; } catch { }
        return s.Off;
    }

    internal static JsonArray Agents()
    {
        var list = new JsonArray();
        string[] files;
        try { files = Directory.GetFiles(AgentDir, "*.json"); } catch { return list; }
        foreach (var f in files.OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out int n) ? n : int.MaxValue))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int slot)) continue;
            var a = ReadAgent(f);
            if (a == null || (int?)a["pid"] is not int pid || !Alive(pid))
            {
                if (a != null) try { File.Delete(f); File.Delete(AgentFile(slot, ".off")); } catch { }
                continue;
            }
            DateTime.TryParse((string?)a["lastSeen"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen);
            list.Add(new JsonObject
            {
                ["id"] = slot, ["label"] = (string?)a["name"] is { Length: > 0 } name ? name : (string?)a["label"], ["from"] = (string?)a["from"], ["client"] = (string?)a["client"] ?? "", ["target"] = (string?)a["target"] ?? "",
                ["agoSec"] = seen == default ? null : (int)(DateTime.UtcNow - seen).TotalSeconds, ["pid"] = pid,
                ["disconnected"] = File.Exists(AgentFile(slot, ".off")),
            });
        }
        return list;
    }

    internal static Program.Session? Own(int slot)
    {
        lock (Mine) return Mine.FirstOrDefault(m => m.Slot == slot);
    }

    /// <summary>Marks another process's agent as disconnected; it refuses every call from then on.</summary>
    internal static bool Disconnect(int slot)
    {
        var a = ReadAgent(AgentFile(slot, ".json"));
        if (a == null || (int?)a["pid"] is not int pid || !Alive(pid)) return false;
        Program.WritePrivate(AgentFile(slot, ".off"), pid.ToString());
        return true;
    }
}

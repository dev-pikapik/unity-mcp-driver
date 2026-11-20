// Shared with the MCP server: no Unity or JSON dependencies.

namespace UDriver
{
    using System.Collections.Generic;

    public sealed class ToolParam
    {
        public string Name, Type, Desc;   // Type: string | integer | number | boolean | array | object | any
        public bool Required;
    }

    public sealed class ToolDef
    {
        public string Name, Desc;
        public ToolParam[] Params;
    }

    public static class ToolManifest
    {
        public const string Version = "2.0.11";

        /// <summary>Game drivers take 17890, then 17911-17930; editor bridges 17891-17910.</summary>
        public const int GamePort = 17890, EditorPort = 17891, EditorPortLast = 17910, GamePortLast = 17930;

        public static IEnumerable<int> Ports(bool editor)
        {
            if (!editor) yield return GamePort;
            int from = editor ? EditorPort : EditorPortLast + 1, to = editor ? EditorPortLast : GamePortLast;
            for (int p = from; p <= to; p++) yield return p;
        }

        /// <summary>How far a tool reaches: 0 looks, 1 plays, 2 changes the game, 3 may reach files, programs or the network outside the game.</summary>
        public static int Level(string name) => Look.Contains(name) ? 0 : Play.Contains(name) ? 1 : Beyond.Contains(name) ? 3 : 2;

        static readonly HashSet<string> Look = new HashSet<string>
        {
            "app_status", "app_connect", "targets", "batch", "status", "ui_tree", "ui_find", "ui_inspect", "screenshot", "screen_pick", "locate", "scene_tree", "state_get",
            "state_query", "errors", "types_find", "type_members", "events_query", "wait_for", "input_actions", "frames_get", "probe_list",
            "profiler_get", "profiler_list", "profiler_capture", "leak_check", "rec_steps", "memory_top", "memory_summary", "memory_snapshot", "memory_watch", "memory_refs", "render_stats", "render_scene",
            "editor_status", "editor_assets", "list_instances", "where_was_i", "team_read", "qa_findings", "qa_risk", "report_problem", "perceive", "rule_list",
            "app_map", "query", "chart_data", "net_log", "code_read",
        };
        static readonly HashSet<string> Play = new HashSet<string>
        {
            "app_launch", "app_kill", "ui_click", "ui_hover", "ui_drag", "pointer", "ui_scroll", "ui_scroll_to", "ui_focus", "type_text", "key", "play",
            "frames_record", "time_control", "events_config", "watch_add", "trigger_add", "probe_remove", "state_export", "memory_capture", "self_test", "rec_start", "rec_stop", "rec_replay",
            "editor_play", "editor_compile", "editor_tests", "take_controls", "launch_game", "use_instance", "team_post", "claim", "release",
            "qa_watch", "qa_crawl", "qa_isolate", "qa_visual", "qa_report", "qa_scenario", "qa_parallel", "rule_add", "rule_remove", "move",
        };
        static readonly HashSet<string> Beyond = new HashSet<string> { "state_call", "editor_menu" };

        static ToolParam P(string n, string t, string d, bool req = false) => new ToolParam { Name = n, Type = t, Desc = d, Required = req };

        static ToolDef T(string n, string d, params ToolParam[] p) => new ToolDef { Name = n, Desc = d, Params = p };

        // Target of a UI action: element id / name / text, or raw screen point.
        static ToolParam[] Tg(string prefix, params ToolParam[] extra)
        {
            var l = new List<ToolParam>
            {
                P(prefix + "id", "integer", "Element id from ui_tree / ui_find (the number after #). Ids >= 1000000 are uGUI objects."),
                P(prefix + "name", "string", "Element name (exact match preferred, then substring). Alternative to id."),
                P(prefix + "text", "string", "Visible text of the element (substring). Alternative to id."),
                P(prefix + "x", "number", "Screen X in pixels, origin top-left (same space as screenshots at scale 1). Alternative to id."),
                P(prefix + "y", "number", "Screen Y in pixels, origin top-left."),
            };
            l.AddRange(extra);
            return l.ToArray();
        }

        static readonly ToolParam Method = P("method", "string", "'event' (default): synthesize UI Toolkit events, deterministic, UI only. 'input': inject through the Input System virtual mouse/keyboard (also reaches uGUI and gameplay code). 'os' (needs Computer access): real mouse events to the game's own window, for games that read Input.mousePosition / GetMouseButton (Windows and Linux X11 builds); coordinates are kept inside the game window. 'patch': Harmony patches on the old Input Manager (Mono, needs Harmony): Input.mousePosition, mouseScrollDelta, GetMouseButton* and GetKey(KeyCode.Mouse0..2) in the game and in uGUI return the agent's pointer, any window, any focus.");
        static readonly ToolParam Mods = P("modifiers", "array", "Held modifiers: any of shift, ctrl, alt, cmd.");

        const string MatchDesc = "Names, tags or script types separated by '|', e.g. 'Zombie' or 'tag:Enemy|comp:Health|name:Boss'. A bare word matches part of a name, a tag or a script type.";
        static readonly ToolParam PlayerP = P("player", "string", "The player object (path or #id). Default: the object tagged Player, else the main camera's root.");
        static readonly ToolParam DriveP = P("drive", "string", "'input' (default): virtual keyboard and mouse. 'auto': the same, switching to direct control when the game ignores them (games on the old Input Manager). 'direct': turn and move the player object itself and call its Fire/Shoot/Jump methods. 'auto' and 'direct' change the game, so they need full access.");

        public static readonly ToolDef[] Tools =
        {
            T("status", "App overview: product/version, scene, screen size, fps, time scale, UI panels, available input methods, active probes, last event seq. Call first."),

            // ---- UI Toolkit: reading ----
            T("ui_tree", "Dump the live UI: UI Toolkit panels and uGUI canvases (what is really on screen, incl. elements created from code). One line per element: #id Type \"name\" .classes \"text\" =value [x,y,w,h] flags. '*' marks interactive elements. Coordinates are screen pixels (top-left origin).",
                P("root", "string", "Only the subtree of this element: id (#12 or 12), name, or uGUI path like /Canvas/Panel."),
                P("id", "integer", "Only the subtree of the element with this id from ui_tree / ui_find (the number after #). Same as root=#id."),
                P("depth", "integer", "Max depth (default 25)."),
                P("maxNodes", "integer", "Max printed lines (default 300), shared equally between the UI panels and canvases."),
                P("visibleOnly", "boolean", "Skip hidden elements (default true)."),
                P("interactiveOnly", "boolean", "Only clickable/editable elements (default false)."),
                P("compact", "boolean", "Hide anonymous wrappers and control internals (default true).")),
            T("ui_find", "Search elements. All given filters must match.",
                P("name", "string", "Name substring (case-insensitive)."),
                P("text", "string", "Text/value substring."),
                P("cls", "string", "USS class (exact)."),
                P("type", "string", "Type name, e.g. Button, TextField, ScrollView."),
                P("visibleOnly", "boolean", "Default true."),
                P("limit", "integer", "Default 30.")),
            T("ui_inspect", "Everything about one element: classes, enabled/visible state, value, layout, resolved style, ancestors, and a hit-test telling which element actually receives a click at its center (detects overlap).", Tg("")),

            // ---- UI Toolkit / input: acting ----
            T("ui_click", "Click an element or a screen point (move, press, release). Returns the UI events that resulted, so you can tell if the click registered.",
                Tg("", P("button", "string", "left (default) | right | middle."), P("count", "integer", "1 = click, 2 = double click."), P("holdMs", "integer", "Hold the button this long before release (long press)."), Mods, Method)),
            T("ui_hover", "Move the pointer over an element/point (hover states, tooltips).", Tg("", Method)),
            T("ui_drag", "Press at the source, move in steps, release at the destination (sliders, drag & drop, swipe-scroll).",
                Tg("", Tg("to_", P("steps", "integer", "Intermediate moves (default 12)."), P("durationMs", "integer", "Total move time (default 300)."), P("holdMs", "integer", "Pause after press before moving (default 0)."), P("button", "string", "left (default) | right | middle."), Method))),
            T("pointer", "Low-level pointer control for custom gestures and holding a button across several calls.",
                Tg("", P("action", "string", "down | move | up.", true), P("button", "string", "left (default) | right | middle."), Mods, Method)),
            T("ui_scroll", "Scroll. mode=offset (default) shifts the nearest ScrollView by pixels, deterministic. mode=wheel sends a mouse wheel event at the point.",
                Tg("", P("dx", "number", "Horizontal amount (px in offset mode)."), P("dy", "number", "Vertical amount, positive = down."), P("mode", "string", "offset | wheel."), Method)),
            T("ui_scroll_to", "Scroll the enclosing ScrollView(s) so the element becomes visible. Long ListView/TreeView lists build rows only while they are on screen: pass the list (id or name) and index to bring an off-screen item into view, then click it.",
                Tg("", P("index", "integer", "Item number in a ListView, TreeView or MultiColumnListView given by id/name."))),
            T("ui_set_value", "Set a control's value directly (fires the normal ChangeEvent): TextField, Toggle, Slider, DropdownField, etc. 'property' lets you set another public property, e.g. index, selectedTabIndex, selectedIndex.",
                Tg("", P("value", "any", "New value (string, number, bool, or JSON for structs).", true), P("property", "string", "Property name (default: value)."))),
            T("ui_focus", "Give keyboard focus to an element.", Tg("")),
            T("type_text", "Type text as keystrokes into the given (or currently focused) element. Falls back to setting the value directly if keystrokes had no effect.",
                Tg("", P("text", "string", "Text to type.", true), P("clear", "boolean", "Clear the field first."), P("submit", "boolean", "Press Enter afterwards."), P("method", "string", "event (default) | input | set."))),
            T("key", "Press, hold or release a key like a real keyboard. Key names are UnityEngine.KeyCode names (Return, Escape, Tab, Space, Backspace, LeftArrow, W, Alpha1, LeftShift, F5...; Enter and Digit1 work too). method=auto (default) picks the route: the focused UI Toolkit element; else the selected uGUI object (Return/Space submit, Escape cancels, arrows and Tab move the selection, a text field takes every key); else gameplay input, read by Input.GetKey or Input System actions.",
                P("key", "string", "Key name.", true), P("action", "string", "press (default) | down | up."), P("holdMs", "integer", "For press: how long to hold."), Mods,
                P("method", "string", "auto (default) | event: UI Toolkit events | input: Input System virtual keyboard (Input System Key names) | os (needs Computer access): real key events posted to the game's own window (macOS, Windows and Linux X11 builds), for gameplay code even while a UI object is selected | patch: Harmony patches make Input.GetKey/GetKeyDown/GetKeyUp and GetAxis Horizontal/Vertical return the key (Mono, needs Harmony; also in the editor and unfocused windows).")),
            T("play", "Play the game for a stretch in one call: each step holds/taps keys, presses an Input System action, tilts the left stick or moves the mouse for its duration, frame by frame inside the game; use it for walking, jumping and looking around. Example: {\"steps\":[{\"hold\":[\"W\",\"LeftShift\"],\"ms\":800},{\"tap\":[\"Space\"],\"look\":[200,0],\"ms\":300}],\"observe\":[\"/Player:Transform.position\"]}.",
                P("steps", "array", "Up to 100 steps, at most 60000 ms in total. Step fields (all optional): hold (Input System Key names held during the step: W, LeftShift, Space...), tap (keys pressed for one frame at the start), action (Input System action name or Map/Action; its keyboard or mouse-button binding is held during the step), move ([x,y] left gamepad stick, -1..1), look ([dx,dy] total mouse delta over the step, +y up), ms (duration; default one frame).", true),
                P("observe", "array", "State paths (as in state_get) read after every step."),
                P("stopWhen", "object", "{\"path\":\"<state path>\",\"equals\":\"<value>\"}: stop as soon as the value matches (checked every frame; without equals, when the path resolves)."),
                P("timeoutMs", "integer", "Stop the whole call after this many ms, releasing the keys it holds (default: when the steps end).")),
            T("input_actions", "List the game's Input System controls: action maps, actions, their bindings and current values, from active PlayerInput components and enabled actions; call it before play to learn the action names. Example: {}."),

            // ---- capture / time ----
            T("screenshot", "Capture the real framebuffer (exactly what is displayed). With annotate=true, interactive elements are outlined and labelled with their ids. With camera, renders only what that camera sees (no overlay UI).",
                P("maxWidth", "integer", "Downscale to this width (default 1024)."), P("quality", "integer", "JPEG quality (default 75)."), P("annotate", "boolean", "Draw element boxes + ids (default false)."),
                P("camera", "string", "Camera name, GameObject path or #u id (see render_scene).")),
            T("frames_record", "Ring-buffer recording of frames for frame-by-frame analysis.",
                P("action", "string", "start | stop | clear | status.", true), P("fps", "number", "Capture rate (default 5; use 0 for every frame)."), P("maxWidth", "integer", "Frame width (default 480)."),
                P("seconds", "number", "Buffer length (default 20)."), P("onlyChanges", "boolean", "Skip frames identical to the previous one (default true).")),
            T("frames_get", "Fetch recorded frames as images (max 8 per call). Use frames_record status to see available indices.",
                P("indices", "array", "Explicit frame indices."), P("from", "integer", "First index."), P("to", "integer", "Last index."), P("step", "integer", "Take every Nth (default 1).")),
            T("time_control", "Pause / resume / step the game via Time.timeScale.",
                P("action", "string", "pause | resume | step | scale | status.", true), P("frames", "integer", "For step: frames to advance (default 1)."), P("scale", "number", "For scale: new Time.timeScale.")),

            // ---- events ----
            T("events_query", "Read the unified event log. Categories: log.log/log.warning/log.error/log.exception, ui.click/ui.down/ui.up/ui.change/ui.focus/ui.blur/ui.keydown/ui.wheel/ui.submit/ui.move, trace, watch, trigger, scene, custom. A category prefix (e.g. 'ui', 'log') matches all sub-categories.",
                P("sinceSeq", "integer", "Only events after this seq. Omit to get the most recent ones."), P("categories", "array", "Category filters."), P("contains", "string", "Substring filter (case-insensitive)."),
                P("limit", "integer", "Default 50."), P("detail", "boolean", "Include detail/stack traces (default false).")),
            T("events_config", "Enable/disable event categories (ui.move is off by default), clear the log, view per-category counts, or capture every thrown exception (even caught ones). Example: firstChance=true firstChanceMax=500, then events_query categories=[\"exception.firstchance\"] detail=true.",
                P("enable", "array", "Categories to enable."), P("disable", "array", "Categories to disable."), P("clear", "boolean", "Drop all stored events and error groups."),
                P("firstChance", "boolean", "Turn first-chance exception capture on or off (Mono only)."), P("firstChanceMax", "integer", "Stop capturing after this many exceptions (default 1000).")),
            T("errors", "Errors and exceptions logged since start, grouped by type and top stack frame with counts and first/last time; start here when something went wrong. Example: full=true limit=5 contains=\"NullReference\".",
                P("full", "boolean", "Include the whole stack of the latest occurrence of each group."), P("contains", "string", "Filter by text in type, message or stack."),
                P("offset", "integer", "Skip this many groups."), P("limit", "integer", "Groups to show (default 20).")),
            T("wait_for", "Block until a condition is met or timeout. Any ONE of the given conditions ends the wait. Returns MATCHED/TIMEOUT plus a summary of everything that happened meanwhile. Pass sinceSeq from the preceding action so fast events are not missed.",
                P("event", "string", "Event category to wait for."), P("contains", "string", "Substring the event text must contain."),
                P("ui_name", "string", "Wait for an element with this name."), P("ui_text", "string", "Wait for an element with this text."), P("gone", "boolean", "Invert the UI condition: wait until the element disappears."),
                P("state", "string", "State path (see state_get) to poll."), P("equals", "string", "Expected formatted value of the state path. Omit to wait until the path resolves."),
                P("sinceSeq", "integer", "Start of the window (default: now)."), P("frames", "integer", "Just wait this many frames (no other condition needed)."), P("ms", "integer", "Just wait this many milliseconds (no other condition needed)."), P("timeoutMs", "integer", "Default 10000.")),

            // ---- state / reflection ----
            T("scene_tree", "GameObject hierarchy of all loaded scenes (incl. DontDestroyOnLoad) with component names.",
                P("root", "string", "GameObject path like /Root/Child to start from."), P("depth", "integer", "Default 3."), P("maxNodes", "integer", "Default 200."), P("activeOnly", "boolean", "Default true.")),
            T("state_get", "Read any value via reflection (private members included), page through big objects and collections, and get ids (#u.. Unity objects, #o.. other objects) that work as paths in every state tool. Example: path=\"/Game:Inventory.items\" offset=30 limit=10 fields=[\"id\",\"owner.name\"] depth=2; path syntax '/Go/Path:Component.field.sub[0]', '@Namespace.Type.staticMember', '#u123:Rigidbody.velocity', '#o7.items[2]', '#12.text' (UI element).",
                P("path", "string", "State path.", true), P("depth", "integer", "How deep to expand objects (default 1, max 4)."), P("offset", "integer", "Skip this many top-level entries (collection items or members)."),
                P("limit", "integer", "Entries per level (default 30)."), P("fields", "array", "Only these members of the object (or of each item of a collection); paths like 'owner.name' work."),
                P("filter", "string", "Keep top-level entries whose name or value contains this text."), P("members", "string", "all (default, private included) | public | serialized (what Unity saves: public or [SerializeField], not [NonSerialized])."),
                P("props", "boolean", "Also evaluate public properties (default false; getters may have side effects).")),
            T("state_query", "Search, sort, group and summarize a large collection (100k items stay responsive: the scan is spread over frames); use it instead of paging state_get when looking for specific items. Example: path=\"/Game:Units.all\" where=[{\"field\":\"hp\",\"op\":\"<\",\"value\":10}] select=[\"name\",\"hp\"] sort=\"-hp\" limit=20, or group=\"team\" stats=true.",
                P("path", "string", "State path of a list, array, set or dictionary (dictionary items have Key and Value).", true),
                P("where", "array", "Conditions, all must hold: {field, op, value}; op is == != < <= > >= contains; field '.' is the item itself. Numbers compare as numbers, text ignores case."),
                P("select", "array", "Fields to show per item (default: a one-line summary)."), P("sort", "string", "Field to sort by; '-field' for descending."),
                P("offset", "integer", "Skip this many matches."), P("limit", "integer", "Matches to show (default 20)."), P("group", "string", "Field to count items by."),
                P("stats", "boolean", "Count, item types, min/max/avg of numbers and unique count of text (for select fields, or for the items themselves).")),
            T("state_export", "Write a whole collection to a JSONL file (one item per line) for offline analysis when it is too big to read through the tools. Example: path=\"/Game:Orders.history\" depth=2; the file goes to ~/.udriver/exports (owner-only, max 200 MB per file; the folder keeps 2 GB by default and deletes its oldest files first) and the reply gives its path, line count, size and the first 3 lines.",
                P("path", "string", "State path of a collection.", true), P("depth", "integer", "How deep to serialize each item (default 2, max 4); Unity objects become {\"$ref\":\"#u..\"}.")),
            T("state_set", "Write a field/property via reflection.", P("path", "string", "State path ending in a member or index.", true), P("value", "any", "New value.", true)),
            T("state_call", "Invoke a method via reflection on the object at 'path' (or a static method if path is '@Type'); the overload is picked by the argument types. Example: path=\"@System.Math\" method=\"Max\" args=[1.5,2]; when several overloads fit equally, the error lists them and types=[\"double\",\"double\"] picks one.",
                P("path", "string", "State path of the target object, or '@Namespace.Type'.", true), P("method", "string", "Method name.", true), P("args", "array", "Arguments."),
                P("types", "array", "Parameter type names that select the overload, e.g. [\"string\"].")),
            T("types_find", "Search loaded game types by name ('*' wildcards allowed). Use this to discover the code before placing probes.",
                P("pattern", "string", "e.g. *Order* or Game.Trading.*", true), P("limit", "integer", "Default 40."), P("includeEngine", "boolean", "Also search Unity/System assemblies (default false).")),
            T("type_members", "List fields, properties and methods of a type.",
                P("type", "string", "Type name (full or short).", true), P("contains", "string", "Member name filter."), P("inherited", "boolean", "Include inherited members (default false).")),

            // ---- probes ----
            T("watch_add", "Probe: poll a state path every frame and emit a 'watch' event whenever its value changes.",
                P("path", "string", "State path.", true), P("label", "string", "Short label for the events.")),
            T("trace_add", "Probe: record calls of the matching methods as hits, optionally only when a condition holds, with captured values and stacks, or pause the game on a hit (Mono only; a Unity project needs 0Harmony.dll, built games get it from UDriver). Example: pattern=\"Game.Bank.Withdraw\" when={\"field\":\"amount\",\"op\":\">\",\"value\":1000} capture=[\"this.balance\",\"account.owner\"] stack=true maxHits=5, then probe_list id=<probe>.",
                P("pattern", "string", "Namespace.Type.Method with '*' wildcards, e.g. Game.Orders.OrderService.* or *Algorithm*.Start", true), P("args", "boolean", "Log argument values (default true)."), P("result", "boolean", "Log return values (default false)."),
                P("capture", "array", "Values to record per hit: paths starting with this, a parameter name or argN, e.g. this.hp, order.items[0].price."),
                P("when", "object", "One condition {field, op, value} (field as in capture; op == != < <= > >= contains); calls that fail it are only counted."),
                P("stack", "boolean", "Record the stack trace of each hit (default false)."), P("pause", "boolean", "Pause on each hit: editor pause in the editor (resume with editor_play action=resume), timeScale=0 in a build (time_control action=resume)."),
                P("maxHits", "integer", "Record at most this many hits (default 2000; calls are still counted).")),
            T("trigger_add", "Probe: when a matching event occurs, run an action: pause (freeze time), capture_frame (store a frame in the frame buffer), mark (just emit a 'trigger' event). Optionally snapshot a state path at that moment.",
                P("event", "string", "Event category to match."), P("contains", "string", "Substring to match."), P("action", "string", "pause | capture_frame | mark.", true),
                P("state", "string", "State path to snapshot into the trigger event."), P("once", "boolean", "Remove after first hit (default true).")),
            T("probe_list", "List active probes (watches, traces, triggers) with hit counts, or read one trace probe's recorded hits page by page. Example: id=3 offset=0 limit=20.",
                P("id", "integer", "Probe whose hits to read."), P("offset", "integer", "Skip this many hits."), P("limit", "integer", "Hits per page (default 20).")),
            T("probe_remove", "Remove a probe. Probes set by other agents stay unless force=true.", P("id", "integer", "Probe id."), P("all", "boolean", "Remove all your probes."),
                P("force", "boolean", "Also remove probes set by other agents.")),

            // ---- profiler / memory ----
            T("profiler_get", "Current performance counters averaged over the last 30 frames: fps, CPU main/render thread and GPU frame time, memory, GC allocations per frame (Development Build), draw calls. 'add' registers extra counters (\"Category/Name\", see profiler_list).",
                P("add", "array", "Extra counters as \"Category/Name\" strings.")),
            T("profiler_list", "List available profiler counters/markers in this build.", P("contains", "string", "Name filter."), P("limit", "integer", "Default 60.")),
            T("memory_summary", "Memory at a glance, instantly: Unity used/reserved, managed heap, graphics, textures/meshes/materials/animations/RenderTextures/audio (count and bytes), object counts, GC allocations per frame. Start of a leak hunt: memory_summary -> memory_snapshot take -> play -> memory_snapshot diff -> memory_refs."),
            T("memory_top", "Heaviest loaded Unity objects with id, bytes, size/format for textures, vertices/triangles for meshes; runtime=true marks objects made by code (not assets), unused=true runtime textures/meshes/materials no scene renderer or material uses. group=true totals by type. Example: type=Texture2D runtime=true limit=10.",
                P("type", "string", "UnityEngine.Object type name: Texture, Texture2D, RenderTexture, Mesh, AudioClip, Material, Shader, AnimationClip, GameObject... (default: all objects)."),
                P("name", "string", "Name substring filter."), P("runtime", "boolean", "Only objects created while running."), P("group", "boolean", "Totals per type instead of single objects."),
                P("offset", "integer", "Skip this many entries."), P("limit", "integer", "Entries to show (default 15).")),
            T("memory_snapshot", "Light snapshots of all Unity objects (count and bytes per type plus object ids), kept per agent (max 5), and diffs between them: what was added or grew, with the new objects listed by id. Example: action=take name=before, play, then action=diff from=before (compares with now).",
                P("action", "string", "take (default) | diff | list | drop."), P("name", "string", "Snapshot name for take/drop (default s1, s2...)."),
                P("from", "string", "For diff: base snapshot (default your latest)."), P("to", "string", "For diff: second snapshot (default: now)."),
                P("offset", "integer", "Skip this many new objects."), P("limit", "integer", "Types and new objects to show (default 15).")),
            T("memory_watch", "Sample memory several times while the game runs and report the trend per object type: bytes/s, count/s and whether it rises monotonically; finds what grows over a long session. Example: samples=10 intervalMs=1000.",
                P("samples", "integer", "Number of samples (default 10, max 120)."), P("intervalMs", "integer", "Time between samples (default 1000)."),
                P("limit", "integer", "Growing types to show (default 10)."), P("timeoutMs", "integer", "Allow samples x intervalMs beyond 100 s (up to 600000).")),
            T("memory_refs", "Who keeps an object alive: shortest reference paths from static fields of game code, scene components and DontDestroyOnLoad to it, as state paths like @Game.Cache.items[3]. The same walk finds destroyed Unity objects still referenced from code and the biggest collections. Example: id=\"#u-4242\" (from memory_top or memory_snapshot diff), or find=all.",
                P("id", "string", "Object id #u.. or #o.. to find holders of."), P("find", "string", "holders (default with id) | destroyed | collections | all (default without id)."),
                P("from", "string", "Walk only from this state path instead of all roots."), P("maxDepth", "integer", "Longest path (default 16)."), P("maxNodes", "integer", "Objects to visit at most (default 2000000)."),
                P("offset", "integer", "Skip this many entries of the destroyed/collections lists."), P("limit", "integer", "Paths or entries to show (default 5 paths, 20 entries).")),
            T("memory_capture", "Write a full memory snapshot (.snap for the Unity Memory Profiler package) to ~/.udriver/exports (owner-only; the folder keeps 2 GB by default and deletes its oldest files first) and return its path and size; editor or Development Build only. Use it when memory_snapshot and memory_refs are not detailed enough."),
            T("profiler_capture", "Record N frames of the Unity Profiler into a .raw file in ~/.udriver/exports (owner-only, same limits as memory_capture) and return its path and size; open it in Window > Analysis > Profiler. Editor or Development Build only.",
                P("frames", "integer", "Frames to record (default 120, max 3600).")),
            T("leak_check", "Find a leak: runs the action once to warm up, counts Unity objects by type and managed collections, runs the action N more times, counts again and lists what grew by at least one per repeat. The action should leave the game as it found it (open and close a menu, load and leave a level); each of its tools needs its own level.",
                P("action", "array", "The steps of one repeat: {tool, args} or an array of them, e.g. [{\"tool\":\"ui_click\",\"args\":{\"text\":\"Open\"}},{\"tool\":\"ui_click\",\"args\":{\"text\":\"Close\"}}].", true),
                P("repeats", "integer", "How many times to repeat (default 5, 2-100)."), P("timeoutMs", "integer", "Allow more than 100 s for slow actions (up to 600000).")),

            // ---- record and replay ----
            T("rec_start", "Start recording the game: keyboard, mouse and gamepad input frame by frame, the agents' UI actions, logs and errors, the random seed and a state checksum every 30 frames, at a fixed 60 frames per second. Returns the recording id on the first line. A game started with app_launch is restarted first so the recording begins from a fresh start and rec_replay can repeat it exactly; any other game is recorded as it runs.",
                P("fresh", "boolean", "false: record the running game without restarting it (rec_replay is then approximate)."), P("seed", "integer", "Random seed to start from (default: a new one).")),
            T("rec_stop", "Stop the recording and save it to ~/.udriver/exports (owner-only). Without rec_start it saves the last 2 minutes, which are always kept. Returns the recording id on the first line; rec_steps shows what happened."),
            T("rec_replay", "Play a recording back and compare the state checksums: answers \"match\" or \"diverged at frame N\" with the objects that differ. A recording from a fresh start replays in a new copy of the same build that is closed afterwards; otherwise it replays inside the running game and is marked approximate.",
                P("id", "string", "Recording id from rec_start or rec_stop.", true)),

            // ---- rendering ----
            T("render_stats", "Measure rendering over N frames: frame time min/avg/p95/max and spikes, CPU main/render thread and GPU time, what bounds the frame, draw calls, batches, SetPass calls, triangles, vertices, shadow casters, RenderTexture count and memory. Start here when the frame is slow, then render_scene.",
                P("frames", "integer", "Frames to measure (default 60, max 600).")),
            T("render_scene", "What is drawn and why it costs: render pipeline, quality, vsync, screen; cameras (order, target, culling mask, clear flags, size); visible renderers sorted by triangles or materials with shaders; lights and shadows; hints on what breaks batching. Example: camera=\"Main Camera\" sort=materials limit=10.",
                P("camera", "string", "Only this camera (name, GameObject path or #u id)."), P("sort", "string", "tris (default) | materials."),
                P("offset", "integer", "Skip this many renderers."), P("limit", "integer", "Renderers to show (default 20).")),
            // ---- world / misc ----
            T("screen_pick", "What is under a screen point: UI Toolkit element, uGUI raycast hits, and scene objects whose renderer bounds the camera ray crosses.",
                P("x", "number", "Screen X (top-left origin).", true), P("y", "number", "Screen Y.", true)),
            T("locate", "Where a GameObject is on screen (to click 3D/2D world objects with x,y + method=input).",
                P("path", "string", "GameObject path like /Root/Child.", true)),
            T("batch", "Run several tools in one round trip, in order. Output of each call is concatenated.",
                P("calls", "array", "Items: {\"tool\":\"ui_click\",\"args\":{...}}, optionally with \"target\" (next to tool or inside args) and \"quiet\":true (drop this call's output unless it fails) (max 50). Steps may mix games, editor_*, app_* and targets.", true), P("stopOnError", "boolean", "Stop at the first failing call (default true)."),
                P("output", "string", "all (default) | last (only the last call's output) | errors (only failing calls). Failing calls are always shown.")),
            T("self_test", "Driver diagnostics: builds a temporary UI and verifies that every core capability really works in this project/build (tree, click, typing, keys, drag, scroll, value setting, screenshots, events, reflection, watches, input injection, uGUI, memory, rendering). Run once after installing; report the output if something fails."),

            // ---- testing ----
            T("qa_watch", "Testing mode. Exceptions, errors and asserts are always collected as findings from the start of the game; action=start adds frame spikes, heap and object-count growth (leaks), NaN/Infinity in game components and transforms, watched fields turning null or NaN, stuck UI (3+ actions without any visible change), off-screen controls and broken materials. Read results with qa_findings.",
                P("action", "string", "start | stop | status (default)."), P("stuckSec", "number", "Window for the stuck check in seconds (default 10)."),
                P("fields", "array", "State paths (as in state_get) to watch for null, destroyed or NaN."), P("perf", "boolean", "Frame spikes (default true)."), P("nan", "boolean", "NaN/Infinity scan (default true)."),
                P("leak", "boolean", "Object-count and heap growth (default true)."), P("visual", "boolean", "Off-screen controls, error shaders, magenta pixels (default true).")),
            T("qa_findings", "Deduplicated problems found in this game: kind, severity, where (type, method, file:line), how often, the last actions and log lines before it, and the code involved: fields the failing method reads and which other methods write them (what breaks in combination).",
                P("since", "integer", "Only findings with an id above this."), P("id", "integer", "One finding in full."), P("format", "string", "text (default) | json: {instance:{product,pid,port}, findings:[...]}."),
                P("image", "boolean", "With id: attach the screenshot taken at its first occurrence."), P("clear", "boolean", "Forget all findings after answering.")),
            T("qa_crawl", "Explore the game automatically. mode=crawl clicks every interactive control on every screen it reaches, types into inputs and goes back (Escape, back/close buttons, scene reload); mode=monkey sends seeded random clicks, keys and drags. Returns coverage (screens, controls clicked of total, click handlers reached and not reached), the steps taken and the new findings with the steps that led to each.",
                P("mode", "string", "crawl (default) | monkey."), P("seed", "integer", "Random seed for monkey (default 1)."), P("steps", "integer", "Most actions (default 200)."), P("seconds", "number", "Time budget (default 120)."),
                P("avoid", "array", "Skip controls whose name, text or handler contains one of these (default quit, exit, delete)."),
                P("delayMs", "integer", "Pause after each action (default 250; small values click fast and reveal races)."), P("timeScale", "number", "Time.timeScale during the run, restored afterwards.")),
            T("qa_isolate", "Shrink a reproduction: replays subsets of the steps (delta debugging), reloading the scene between runs, and returns the smallest list of steps that still produces the same finding.",
                P("id", "integer", "Finding to reproduce.", true), P("steps", "array", "Steps {tool, args, waitMs} of in-game tools; waitMs pauses before the step (default: the steps recorded with the finding and the pauses between them)."),
                P("reset", "string", "scene (default): reload the active scene before each run | none."), P("maxRuns", "integer", "Most replays (default 30)."), P("delayMs", "integer", "Pause after each step (default 250).")),
            T("qa_risk", "Where bugs are likely: per loaded scene and game type, ranked by findings so far, fields written by 2+ types, collections enumerated inside coroutines across a yield, Start/Awake that read other components, and static mutable fields."),
            T("qa_visual", "Visual checks. action=check lists controls off screen, renderers with an error shader or no material, images without a sprite or texture, and the share of magenta pixels. save stores a named screenshot baseline; compare diffs the screen against it (percent of changed pixels plus a diff image).",
                P("action", "string", "check (default) | save | compare."), P("name", "string", "Baseline name for save/compare (default main)."), P("threshold", "number", "Percent of changed pixels that counts as a difference (default 1).")),

            // ---- Understanding any app: structure, data, network ----
            T("app_map", "Explain the app in one call: scenes and screens, UI with human labels (what each button, field, list and chart is and which method it calls), the scripts that drive it (components, ScriptableObjects, static managers), event wiring and data models (classes holding collections of records). Compact by default; drill down with section or target. Call this first in an unfamiliar app.",
                P("section", "string", "overview (default) | ui | scripts | events | data | assemblies."),
                P("focus", "string", "Drill down into one type name or object path (e.g. TradingService or /Canvas/OrderPanel)."),
                P("limit", "integer", "Max entries per list (default 40).")),
            T("query", "Pick exactly the pieces you need from the live app with a selector and get them as JSON, CSV or lines. Roots: a state path (/Go:Comp.f, @Ns.Type.static, #u12), a type name with * wildcards (every live instance, incl. singletons). Steps: .member, [*] items, [n], [a:b], [?.field op value && ...] with op == != < <= > >= ~= (contains), ..name (search deeper). Example: select=\"Contact[?.company ~= 'acme']\" fields=[\"name\",\"email\"] format=csv.",
                P("select", "string", "Selector.", true), P("fields", "array", "Only these members of each result (paths like 'owner.name'); csv then has exactly these columns."),
                P("format", "string", "lines (default) | json | csv."), P("offset", "integer", "Skip this many results."), P("limit", "integer", "Results to show (default 50, csv/json up to 10000).")),
            T("chart_data", "Charts and graphs in the app: without id lists every chart found (line renderers, chart components, arrays behind a graph); with id returns its series points and the field the data comes from.",
                P("id", "string", "Chart id from the list."), P("last", "integer", "Only the last N points."), P("format", "string", "lines (default) | json | csv.")),
            T("form_fill", "Fill a form by its visible labels and optionally press its submit button. Example: fields={\"Name\":\"Ann Lee\",\"Email\":\"ann@demo.test\",\"Stage\":\"Won\"} submit=\"Save\". Dropdowns take the option text, toggles true/false.",
                P("fields", "object", "Label -> value.", true), P("root", "string", "Only inside this panel (object path or name)."), P("submit", "string", "Label of the button to press after filling.")),
            T("net_capture", "Record the app's own network traffic (UnityWebRequest, HttpClient, WebSocket, TCP sockets): requests, responses and messages, read with net_log. Needs 0Harmony.dll in the app; Mono only.",
                P("action", "string", "start | stop | status (default) | clear."), P("bodies", "boolean", "Keep bodies (default true)."), P("maxBody", "integer", "Bytes kept per body (default 65536).")),
            T("net_log", "Read recorded network traffic as a live feed (since = last seq you saw), one entry in full (id), or where the values from one response ended up in the app (id + trace=true: object fields and charts holding them).",
                P("since", "integer", "Only entries after this seq."), P("contains", "string", "URL or body substring."), P("kind", "string", "http | websocket | socket."),
                P("id", "integer", "One entry in full."), P("trace", "boolean", "With id: find where the response values live in the app."), P("limit", "integer", "Entries to show (default 30).")),

            // ---- Unity editor (served by the editor bridge, also outside Play Mode) ----
            T("editor_status", "Editor state: Unity version, project, Play Mode, compiling, compile errors, open scenes, last test run and build."),
            T("editor_play", "Control Play Mode. Entering/leaving Play Mode reloads scripts, so the driver is briefly unreachable; then use app_connect.",
                P("action", "string", "play | stop | pause | resume | step (true single-frame step).", true)),
            T("editor_compile", "Script compilation: current state and the list of compiler errors with file and line. action=refresh re-imports assets and recompiles.",
                P("action", "string", "status (default) | refresh.")),
            T("eval", "Run C# inside the game (or the editor outside Play Mode): read and change fields, find objects, walk the hierarchy, call game code, start coroutines. "
                + "The code is a method body returning a value (or ending in an expression), or a full 'public static class Agent { public static object Run() {...} }'; return an IEnumerator to run it as a coroutine. "
                + "Files outside the game/project folder, other programs, launching processes and the computer's network are blocked unless the owner turned on computer access. Runs at your own risk.",
                P("code", "string", "C# statements, e.g. var g = Object.FindObjectOfType<Game>(); g.score = 10; return g.score;"),
                P("timeoutMs", "integer", "Time limit of the call (default 10000); coroutines and callbacks get 1000 ms per step."),
                P("maxMemoryMB", "integer", "Memory the code may allocate (default 256)."),
                P("stop", "string", "Stop an earlier eval's coroutines and callbacks: its id, or all.")),
            T("editor_tests", "Unity Test Framework. action=run starts EditMode or PlayMode tests (optionally filtered), status shows progress, results lists outcomes with messages and stack traces.",
                P("action", "string", "run | status | results.", true), P("mode", "string", "EditMode (default) | PlayMode."),
                P("names", "array", "Full test names to run."), P("groups", "array", "Regex groups (e.g. namespace or fixture names)."), P("categories", "array", "NUnit categories."), P("assemblies", "array", "Test assembly names."),
                P("onlyFailed", "boolean", "For results: only failed tests (default true)."), P("limit", "integer", "For results: max tests listed (default 30).")),
            T("editor_build", "Build a player from the editor (Development Build by default so the driver is active). The editor is blocked while building; poll with action=status.",
                P("action", "string", "build | status.", true), P("path", "string", "Absolute output path inside the project folder and an approved build folder: Game.exe on Windows, Game.app on macOS, Game.x86_64 on Linux, e.g. C:/Projects/Game/Builds/StandaloneWindows64/Game.exe."), P("development", "boolean", "Default true."), P("scenes", "array", "Scene paths (default: enabled scenes from Build Settings).")),
            T("editor_scene", "Scenes in the editor: list (open scenes + Build Settings), open a scene by asset path, or save open scenes.",
                P("action", "string", "list | open | save.", true), P("path", "string", "Scene asset path for open.")),
            T("editor_assets", "Search project assets (AssetDatabase.FindAssets syntax, e.g. 't:VisualTreeAsset order', 't:Scene', 't:Script Trade').",
                P("filter", "string", "Search filter.", true), P("folder", "string", "Limit to a folder, e.g. Assets/UI."), P("limit", "integer", "Default 50.")),
            T("editor_menu", "Execute an editor menu item by path, e.g. 'Assets/Refresh' or a project's custom menu.", P("path", "string", "Menu path.", true)),
            T("perceive", "Look around the player: its own state (position, facing, grounded, health and ammo fields) and nearby objects with name, tag, scripts, distance, bearing, speed, whether they approach, whether the camera sees them, and their health. Read-only.",
                P("match", "string", "Only these objects. " + MatchDesc),
                P("radius", "number", "Meters around the player (default 30)."),
                P("limit", "integer", "Max objects, nearest first (default 25)."),
                P("visibleOnly", "boolean", "Only objects the camera sees (default false)."),
                P("scenery", "boolean", "Also list walls, crates and other objects without scripts (default false)."),
                PlayerP),
            T("rule_add", "Add a rule that runs inside the game every frame until removed. Any game: read values by state path, check a condition, and run tool calls or send an event when it becomes true, e.g. values={\"kills\": \"/GameStats:GameStats.zombiesKilled\"} when=\"rose(kills) && kills >= 10\" do=[{\"tool\": \"key\", \"args\": {\"key\": \"Space\"}}, {\"emit\": \"kills {kills}\"}] max=1. Shooter games: aim at and shoot the nearest matching object but never with an avoided object near the line of fire, keep a distance, walk up to targets, jump over low obstacles, report objects coming near. Results arrive as 'autoplay' events (events_query, wait_for) and in rule_list. A rule with an existing name replaces it.",
                P("name", "string", "Rule name.", true),
                P("values", "object", "Names for game values the condition uses: name -> state path (as in state_get), e.g. {\"hp\": \"/Player:Health.current\", \"n\": \"@Game.orders.Count\"}. Read at every check."),
                P("when", "string", "Condition over the values: numbers, \"text\", true/false, + - * / %, < <= > >= == !=, && || !, parentheses. Functions: prev(x), delta(x), changed(x), rose(x), fell(x), abs(x), ma(x, n), min(x, n), max(x, n) over the last n checks (none until there are n), crosses_above(a, b), crosses_below(a, b); in 3D: dist(\"Zombie\") nearest distance, count(\"Zombie\", 10) within 10 m, seen(\"Zombie\", 10) visible within 10 m. With the shooter fields, the shooter acts only while it is true."),
                P("do", "array", "What to run when the condition becomes true, in order: {\"tool\": \"key\", \"args\": {\"key\": \"Space\"}} for any tool, or {\"emit\": \"hp {hp}\"} for an 'autoplay' event with values filled in."),
                P("cooldown", "number", "Seconds to wait between fires (default 0)."),
                P("max", "integer", "Stop after this many fires; the rule stays listed as done (default: no limit)."),
                P("every", "integer", "Check every this many frames (default 1)."),
                P("edge", "boolean", "true (default): fire when the condition becomes true. false: fire at every check where it is true (with cooldown)."),
                P("match", "string", "Targets. " + MatchDesc),
                P("avoid", "string", "Never shoot these or through them; hits on them are counted. " + MatchDesc),
                P("shoot", "boolean", "Aim at the nearest target in range that the camera can see past obstacles and fire when the crosshair is on it."),
                P("keepAway", "number", "Back away from targets closer than this many meters."),
                P("approach", "number", "Walk toward the nearest target while it is farther than this many meters."),
                P("hunt", "boolean", "When nothing to shoot is in sight, walk toward the nearest hidden target (or circle it when it is close) and face it until it shows up."),
                P("jump", "boolean", "Jump over low obstacles in the way."),
                P("alert", "number", "Report targets entering and leaving this many meters."),
                P("range", "number", "Max distance to engage (default 40)."),
                P("fire", "string", "'mouse' (default, left button), 'key:<Key>' or 'method:Type.Method' (called on the player's object or children)."),
                P("turnSpeed", "number", "Max turn speed in degrees per second (default 900)."),
                P("speed", "number", "Walking speed for direct control, m/s (default 4)."),
                DriveP, PlayerP),
            T("rule_list", "Rules running in the game with live counts: checks, fires, last fire, current values, last error; for the shooter shots, hits, kills, misses, hits on avoided objects, reaction time from first sight to first shot."),
            T("rule_remove", "Stop rules and release every key and button they held. Returns their final counts.",
                P("name", "string", "Rule name, or 'all' (default).")),
            T("move", "Move the player once and wait until done: walk to a point or object (along the NavMesh when the scene has one, else straight with sidesteps and jumps), turn to look at something, jump, run, or strafe for a while.",
                P("to", "any", "Point [x,y,z], object #id or path, or a match for the nearest such object."),
                P("look", "any", "Point [x,y,z] or object to turn the camera to."),
                P("jump", "boolean", "Jump (also while walking)."),
                P("run", "boolean", "Hold run (Left Shift)."),
                P("strafe", "string", "'left' or 'right' for 'seconds'."),
                P("seconds", "number", "How long to strafe (default 1)."),
                P("stopAt", "number", "Arrived when this close, meters (default 1)."),
                P("timeout", "number", "Give up after this many seconds (default 15)."),
                P("speed", "number", "Walking speed for direct control, m/s (default 4)."),
                DriveP, PlayerP),
        };

        /// <summary>Tools that also work in the editor outside Play Mode (pure reflection / scene inspection).</summary>
        public static readonly string[] EditModeTools =
        {
            "status", "scene_tree", "state_get", "state_set", "state_call", "types_find", "type_members",
            "events_query", "events_config", "profiler_get", "profiler_list", "memory_top", "memory_summary", "memory_snapshot", "memory_refs", "render_scene", "probe_list", "probe_remove", "locate", "state_query", "state_export", "errors", "eval", "app_map", "query", "chart_data"
        };
    }
}

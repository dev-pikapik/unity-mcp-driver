// Shared with the MCP server: no Unity or JSON dependencies.
using System.Collections.Generic;

namespace UDriver
{
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
        public const string Version = "0.8.1";

        /// <summary>Game drivers take 17890, then 17911-17930; editor bridges 17891-17910.</summary>
        public const int GamePort = 17890, EditorPort = 17891, EditorPortLast = 17910, GamePortLast = 17930;

        public static IEnumerable<int> Ports(bool editor)
        {
            if (!editor) yield return GamePort;
            int from = editor ? EditorPort : EditorPortLast + 1, to = editor ? EditorPortLast : GamePortLast;
            for (int p = from; p <= to; p++) yield return p;
        }

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

        static readonly ToolParam Method = P("method", "string", "'event' (default): synthesize UI Toolkit events, deterministic, UI only. 'input': inject through the Input System virtual mouse/keyboard (also reaches uGUI and gameplay code).");
        static readonly ToolParam Mods = P("modifiers", "array", "Held modifiers: any of shift, ctrl, alt, cmd.");

        public static readonly ToolDef[] Tools =
        {
            T("status", "App overview: product/version, scene, screen size, fps, time scale, UI panels, available input methods, active probes, last event seq. Call first."),

            // ---- UI Toolkit: reading ----
            T("ui_tree", "Dump the live UI: UI Toolkit panels and uGUI canvases (what is really on screen, incl. elements created from code). One line per element: #id Type \"name\" .classes \"text\" =value [x,y,w,h] flags. '*' marks interactive elements. Coordinates are screen pixels (top-left origin).",
                P("root", "integer", "Only the subtree of this element id."),
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
            T("key", "Press, hold or release a key. With method=event use UnityEngine.KeyCode names (Return, Escape, Tab, Space, Backspace, Delete, LeftArrow, A, Alpha1, F5...). With method=input use Input System Key names (Enter, Escape, Digit1...).",
                P("key", "string", "Key name.", true), P("action", "string", "press (default) | down | up."), P("holdMs", "integer", "For press: how long to hold."), Mods, Method),
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
            T("trace_add", "Probe: record calls of the matching methods as hits, optionally only when a condition holds, with captured values and stacks, or pause the game on a hit (needs 0Harmony.dll in the project; Mono only). Example: pattern=\"Game.Bank.Withdraw\" when={\"field\":\"amount\",\"op\":\">\",\"value\":1000} capture=[\"this.balance\",\"account.owner\"] stack=true maxHits=5, then probe_list id=<probe>.",
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
            T("profiler_get", "Current performance counters: frame time/fps, memory, GC, draw calls. 'add' registers extra counters (\"Category/Name\", see profiler_list).",
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

            // ---- Unity editor (served by the editor bridge, also outside Play Mode) ----
            T("editor_status", "Editor state: Unity version, project, Play Mode, compiling, compile errors, open scenes, last test run and build."),
            T("editor_play", "Control Play Mode. Entering/leaving Play Mode reloads scripts, so the driver is briefly unreachable; then use app_connect.",
                P("action", "string", "play | stop | pause | resume | step (true single-frame step).", true)),
            T("editor_compile", "Script compilation: current state and the list of compiler errors with file and line. action=refresh re-imports assets and recompiles.",
                P("action", "string", "status (default) | refresh.")),
            T("editor_tests", "Unity Test Framework. action=run starts EditMode or PlayMode tests (optionally filtered), status shows progress, results lists outcomes with messages and stack traces.",
                P("action", "string", "run | status | results.", true), P("mode", "string", "EditMode (default) | PlayMode."),
                P("names", "array", "Full test names to run."), P("groups", "array", "Regex groups (e.g. namespace or fixture names)."), P("categories", "array", "NUnit categories."), P("assemblies", "array", "Test assembly names."),
                P("onlyFailed", "boolean", "For results: only failed tests (default true)."), P("limit", "integer", "For results: max tests listed (default 30).")),
            T("editor_build", "Build a player from the editor (Development Build by default so the driver is active). The editor is blocked while building; poll with action=status.",
                P("action", "string", "build | status.", true), P("path", "string", "Absolute output path inside an approved build folder: Game.exe on Windows, Game.app on macOS, Game.x86_64 on Linux, e.g. C:/Builds/QA/Game.exe."), P("development", "boolean", "Default true."), P("scenes", "array", "Scene paths (default: enabled scenes from Build Settings).")),
            T("editor_scene", "Scenes in the editor: list (open scenes + Build Settings), open a scene by asset path, or save open scenes.",
                P("action", "string", "list | open | save.", true), P("path", "string", "Scene asset path for open.")),
            T("editor_assets", "Search project assets (AssetDatabase.FindAssets syntax, e.g. 't:VisualTreeAsset order', 't:Scene', 't:Script Trade').",
                P("filter", "string", "Search filter.", true), P("folder", "string", "Limit to a folder, e.g. Assets/UI."), P("limit", "integer", "Default 50.")),
            T("editor_menu", "Execute an editor menu item by path, e.g. 'Assets/Refresh' or a project's custom menu.", P("path", "string", "Menu path.", true)),
        };

        /// <summary>Tools that also work in the editor outside Play Mode (pure reflection / scene inspection).</summary>
        public static readonly string[] EditModeTools =
        {
            "status", "scene_tree", "state_get", "state_set", "state_call", "types_find", "type_members",
            "events_query", "events_config", "profiler_get", "profiler_list", "memory_top", "memory_summary", "memory_snapshot", "memory_refs", "render_scene", "probe_list", "probe_remove", "locate", "state_query", "state_export", "errors"
        };
    }
}

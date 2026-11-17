// Shared between the Unity package and the MCP server (no Unity / JSON dependencies here).
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
                P("maxNodes", "integer", "Max printed lines (default 300)."),
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
            T("ui_inspect", "Everything about one element: classes, enabled/visible state, value, layout, resolved style, ancestors, and a hit-test telling which element actually receives a click at its center (detects overlap).",
                P("id", "integer", "Element id.", true)),

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
            T("ui_scroll_to", "Scroll the enclosing ScrollView(s) so the element becomes visible.", Tg("")),
            T("ui_set_value", "Set a control's value directly (fires the normal ChangeEvent): TextField, Toggle, Slider, DropdownField, etc. 'property' lets you set another public property, e.g. index, selectedTabIndex, selectedIndex.",
                Tg("", P("value", "any", "New value (string, number, bool, or JSON for structs).", true), P("property", "string", "Property name (default: value)."))),
            T("ui_focus", "Give keyboard focus to an element.", Tg("")),
            T("type_text", "Type text as keystrokes into the given (or currently focused) element. Falls back to setting the value directly if keystrokes had no effect.",
                Tg("", P("text", "string", "Text to type.", true), P("clear", "boolean", "Clear the field first."), P("submit", "boolean", "Press Enter afterwards."), P("method", "string", "event (default) | input | set."))),
            T("key", "Press, hold or release a key. With method=event use UnityEngine.KeyCode names (Return, Escape, Tab, Space, Backspace, Delete, LeftArrow, A, Alpha1, F5...). With method=input use Input System Key names (Enter, Escape, Digit1...).",
                P("key", "string", "Key name.", true), P("action", "string", "press (default) | down | up."), P("holdMs", "integer", "For press: how long to hold."), Mods, Method),

            // ---- capture / time ----
            T("screenshot", "Capture the real framebuffer (exactly what is displayed). With annotate=true, interactive elements are outlined and labelled with their ids.",
                P("maxWidth", "integer", "Downscale to this width (default 1024)."), P("quality", "integer", "JPEG quality (default 75)."), P("annotate", "boolean", "Draw element boxes + ids (default false).")),
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
            T("events_config", "Enable/disable event categories (ui.move is off by default), clear the log, or view per-category counts.",
                P("enable", "array", "Categories to enable."), P("disable", "array", "Categories to disable."), P("clear", "boolean", "Drop all stored events.")),
            T("wait_for", "Block until a condition is met or timeout. Any ONE of the given conditions ends the wait. Returns MATCHED/TIMEOUT plus a summary of everything that happened meanwhile. Pass sinceSeq from the preceding action so fast events are not missed.",
                P("event", "string", "Event category to wait for."), P("contains", "string", "Substring the event text must contain."),
                P("ui_name", "string", "Wait for an element with this name."), P("ui_text", "string", "Wait for an element with this text."), P("gone", "boolean", "Invert the UI condition: wait until the element disappears."),
                P("state", "string", "State path (see state_get) to poll."), P("equals", "string", "Expected formatted value of the state path. Omit to wait until the path resolves."),
                P("sinceSeq", "integer", "Start of the window (default: now)."), P("frames", "integer", "Just wait this many frames (no other condition needed)."), P("timeoutMs", "integer", "Default 10000.")),

            // ---- state / reflection ----
            T("scene_tree", "GameObject hierarchy of all loaded scenes (incl. DontDestroyOnLoad) with component names.",
                P("root", "string", "GameObject path like /Root/Child to start from."), P("depth", "integer", "Default 3."), P("maxNodes", "integer", "Default 200."), P("activeOnly", "boolean", "Default true.")),
            T("state_get", "Read any value via reflection (private members included). Path syntax: '/Go/Path:Component.field.sub[0]' for scene objects, '@Namespace.Type.staticMember.field' for statics, '#12.property' for a UI element by id. Use '[key]' for lists/dictionaries.",
                P("path", "string", "State path.", true), P("depth", "integer", "How deep to expand objects (default 1)."), P("props", "boolean", "Also evaluate public properties (default false; getters may have side effects)."), P("maxItems", "integer", "Collection items to show (default 30).")),
            T("state_set", "Write a field/property via reflection.", P("path", "string", "State path ending in a member or index.", true), P("value", "any", "New value.", true)),
            T("state_call", "Invoke a method via reflection on the object at 'path' (or a static method if path is '@Type').",
                P("path", "string", "State path of the target object, or '@Namespace.Type'.", true), P("method", "string", "Method name.", true), P("args", "array", "Arguments.")),
            T("types_find", "Search loaded game types by name ('*' wildcards allowed). Use this to discover the code before placing probes.",
                P("pattern", "string", "e.g. *Order* or Game.Trading.*", true), P("limit", "integer", "Default 40."), P("includeEngine", "boolean", "Also search Unity/System assemblies (default false).")),
            T("type_members", "List fields, properties and methods of a type.",
                P("type", "string", "Type name (full or short).", true), P("contains", "string", "Member name filter."), P("inherited", "boolean", "Include inherited members (default false).")),

            // ---- probes ----
            T("watch_add", "Probe: poll a state path every frame and emit a 'watch' event whenever its value changes.",
                P("path", "string", "State path.", true), P("label", "string", "Short label for the events.")),
            T("trace_add", "Probe: record every call of the matching methods as 'trace' events (needs 0Harmony.dll in the project; Mono only). Pattern matches 'Namespace.Type.Method' with '*' wildcards.",
                P("pattern", "string", "e.g. Game.Orders.OrderService.* or *Algorithm*.Start", true), P("args", "boolean", "Log argument values (default true)."), P("result", "boolean", "Log return values (default false)."),
                P("stack", "boolean", "Attach a stack trace to each call (default false)."), P("max", "integer", "Stop logging a method after this many calls (default 2000; calls are still counted).")),
            T("trigger_add", "Probe: when a matching event occurs, run an action: pause (freeze time), capture_frame (store a frame in the frame buffer), mark (just emit a 'trigger' event). Optionally snapshot a state path at that moment.",
                P("event", "string", "Event category to match."), P("contains", "string", "Substring to match."), P("action", "string", "pause | capture_frame | mark.", true),
                P("state", "string", "State path to snapshot into the trigger event."), P("once", "boolean", "Remove after first hit (default true).")),
            T("probe_list", "List active probes (watches, traces, triggers) with hit counts."),
            T("probe_remove", "Remove a probe.", P("id", "integer", "Probe id."), P("all", "boolean", "Remove all probes.")),

            // ---- profiler / memory ----
            T("profiler_get", "Current performance counters: frame time/fps, memory, GC, draw calls. 'add' registers extra counters (\"Category/Name\", see profiler_list).",
                P("add", "array", "Extra counters as \"Category/Name\" strings.")),
            T("profiler_list", "List available profiler counters/markers in this build.", P("contains", "string", "Name filter."), P("limit", "integer", "Default 60.")),
            T("memory_top", "Largest loaded assets of a type by runtime memory (Texture, Mesh, AudioClip, Material, Sprite, Font, RenderTexture...).",
                P("type", "string", "UnityEngine.Object type name (default Texture)."), P("limit", "integer", "Default 15.")),
            // ---- world / misc ----
            T("screen_pick", "What is under a screen point: UI Toolkit element, uGUI raycast hits, and scene objects whose renderer bounds the camera ray crosses.",
                P("x", "number", "Screen X (top-left origin).", true), P("y", "number", "Screen Y.", true)),
            T("locate", "Where a GameObject is on screen (to click 3D/2D world objects with x,y + method=input).",
                P("path", "string", "GameObject path like /Root/Child.", true)),
            T("batch", "Run several tools in one round trip, in order. Output of each call is concatenated.",
                P("calls", "array", "Items: {\"tool\":\"ui_click\",\"args\":{...}} (max 20).", true), P("stopOnError", "boolean", "Stop at the first failing call (default true).")),
            T("self_test", "Driver diagnostics: builds a temporary UI and verifies that every core capability really works in this project/build (tree, click, typing, keys, drag, scroll, value setting, screenshots, events, reflection, watches, input injection, uGUI). Run once after installing; report the output if something fails."),

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
            "events_query", "events_config", "profiler_get", "profiler_list", "memory_top", "probe_list", "locate"
        };
    }
}

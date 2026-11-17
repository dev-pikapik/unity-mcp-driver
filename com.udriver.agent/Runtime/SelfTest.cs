using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UDriver
{
    /// <summary>
    /// Builds a temporary UI and drives it through the same tools an agent uses, so one call tells
    /// exactly which capabilities work in this Unity version / project / build.
    /// </summary>
    public static class SelfTest
    {
        static readonly List<string> Lines = new List<string>();
        static int _fail, _pass, _warn;

        static void Check(string name, bool ok, string info = null, bool warnOnly = false)
        {
            if (ok) _pass++; else if (warnOnly) _warn++; else _fail++;
            Lines.Add((ok ? "PASS  " : warnOnly ? "WARN  " : "FAIL  ") + name + (string.IsNullOrEmpty(info) ? "" : "  -> " + Util.OneLine(info, 260)));
        }

        /// <summary>Runs a tool and turns any exception into text instead of aborting the whole self-test.</summary>
        static IEnumerator Tool(string tool, JObject args, ToolResponse outRes)
        {
            IEnumerator it = null;
            try { it = Driver.Invoke(tool, args, outRes); } catch (Exception e) { outRes.Error(e.GetType().Name + ": " + e.Message); }
            while (it != null)
            {
                bool moved = false;
                try { moved = it.MoveNext(); } catch (Exception e) { outRes.Error(e.GetType().Name + ": " + e.Message); }
                if (!moved) break;
                yield return it.Current;
            }
        }

        static bool Has(long since, string cat) { return EventStore.Query(since, new[] { cat }, null, 1, false).Count > 0; }

        public static IEnumerator Run(JObject a, ToolResponse r)
        {
            Lines.Clear(); _fail = _pass = _warn = 0;
            GameObject go = null, canvas = null;
            PanelSettings ps = null;
            ToolResponse t;

            // ---------- temporary UI Toolkit panel ----------
            ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.sortingOrder = 30000;
            foreach (var other in Resources.FindObjectsOfTypeAll<PanelSettings>())
                if (other != ps && other.themeStyleSheet != null) { ps.themeStyleSheet = other.themeStyleSheet; break; }
            go = new GameObject("[UDriver SelfTest]");
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = ps;
            yield return null; yield return null;
            var root = doc.rootVisualElement;
            Check("create runtime UI Toolkit panel", root != null && root.panel != null, ps.themeStyleSheet == null ? "no theme found in project (elements are unstyled, tests still valid)" : null);
            if (root == null || root.panel == null) { Finish(r, go, canvas, ps); yield break; }

            int clicks = 0, downs = 0, moves = 0, ups = 0;
            var btn = new Button(() => clicks++) { name = "udriver-selftest-btn", text = "SelfTest" };
            var field = new TextField { name = "udriver-selftest-field" };
            var toggle = new Toggle { name = "udriver-selftest-toggle" };
            var pad = new VisualElement { name = "udriver-selftest-pad" };
            var scroll = new ScrollView { name = "udriver-selftest-scroll" };
            var tall = new VisualElement();
            tall.style.height = 600; tall.style.width = 100;
            scroll.Add(tall);
            int y = 10;
            foreach (var e in new VisualElement[] { btn, field, toggle, pad, scroll })
            {
                e.style.position = Position.Absolute; e.style.left = 10; e.style.top = y; e.style.width = 220; e.style.height = 40;
                e.style.backgroundColor = new Color(0.15f, 0.15f, 0.2f, 0.9f);
                root.Add(e);
                y += 50;
            }
            pad.RegisterCallback<PointerDownEvent>(e => downs++);
            pad.RegisterCallback<PointerMoveEvent>(e => moves++);
            pad.RegisterCallback<PointerUpEvent>(e => ups++);
            yield return null; yield return null; yield return null;
            UiTools.HookPanels();

            // ---------- reading ----------
            Check("layout resolved", btn.worldBound.width > 1 && btn.worldBound.height > 1, "button rect " + UiTools.ScreenRect(btn));
            Check("ui_find locates element by name", UiTools.FindFirst("udriver-selftest-btn", null) == btn);
            t = new ToolResponse();
            yield return Tool("ui_tree", new JObject(), t);
            Check("ui_tree lists the element", t.AllText().Contains("udriver-selftest-btn"), t.IsError ? t.AllText() : null);
            t = new ToolResponse();
            yield return Tool("ui_inspect", new JObject { ["id"] = UiTools.Id(btn) }, t);
            Check("ui_inspect", !t.IsError && t.AllText().Contains("hit-test"), t.IsError ? t.AllText() : null);

            // ---------- click (synthesized events) ----------
            long seq = EventStore.LastSeq;
            t = new ToolResponse();
            yield return Tool("ui_click", new JObject { ["id"] = UiTools.Id(btn) }, t);
            Check("ui_click method=event triggers Button.clicked", clicks == 1, "clicks=" + clicks + (t.IsError ? " " + t.AllText() : ""));
            Check("event log captured the ClickEvent", Has(seq, "ui.click"));
            clicks = 0;
            t = new ToolResponse();
            yield return Tool("ui_click", new JObject { ["text"] = "SelfTest" }, t);
            Check("ui_click by visible text", clicks == 1, "clicks=" + clicks);
            clicks = 0;
            var c0 = UiTools.ScreenRect(btn).center;
            t = new ToolResponse();
            yield return Tool("ui_click", new JObject { ["x"] = c0.x, ["y"] = c0.y }, t);
            Check("ui_click by screen coordinates", clicks == 1, "clicks=" + clicks + " at " + c0);

            // ---------- typing & keys ----------
            t = new ToolResponse();
            yield return Tool("type_text", new JObject { ["id"] = UiTools.Id(field), ["text"] = "abc 123" }, t);
            bool fellBack = t.AllText().Contains("fell back");
            Check("type_text fills a TextField", field.value == "abc 123", "value='" + field.value + "'" + (t.IsError ? " " + t.AllText() : ""));
            Check("type_text via real key events (no fallback)", !fellBack, fellBack ? "keystroke events did not edit the field; the driver set the value directly instead (still usable)" : null, true);
            t = new ToolResponse();
            yield return Tool("key", new JObject { ["key"] = "Backspace" }, t);
            yield return null;
            Check("key Backspace edits the focused field", field.value == "abc 12", "value='" + field.value + "'" + (t.IsError ? " " + t.AllText() : ""), true);

            // ---------- set value ----------
            seq = EventStore.LastSeq;
            t = new ToolResponse();
            yield return Tool("ui_set_value", new JObject { ["id"] = UiTools.Id(toggle), ["value"] = true }, t);
            Check("ui_set_value on Toggle", toggle.value, t.IsError ? t.AllText() : null);
            Check("event log captured the ChangeEvent", Has(seq, "ui.change"));

            // ---------- drag ----------
            var pr = UiTools.ScreenRect(pad);
            t = new ToolResponse();
            yield return Tool("ui_drag", new JObject { ["x"] = pr.x + 10, ["y"] = pr.center.y, ["to_x"] = pr.xMax - 10, ["to_y"] = pr.center.y, ["steps"] = 6, ["durationMs"] = 60 }, t);
            Check("ui_drag delivers down / move / up", downs == 1 && moves >= 3 && ups == 1, "down=" + downs + " move=" + moves + " up=" + ups + (t.IsError ? " " + t.AllText() : ""));

            // ---------- scroll ----------
            t = new ToolResponse();
            yield return Tool("ui_scroll", new JObject { ["id"] = UiTools.Id(scroll), ["dy"] = 80 }, t);
            Check("ui_scroll moves a ScrollView", scroll.scrollOffset.y > 1, "offset=" + scroll.scrollOffset + (t.IsError ? " " + t.AllText() : ""));

            // ---------- screenshot ----------
            t = new ToolResponse();
            yield return Tool("screenshot", new JObject { ["maxWidth"] = 640, ["annotate"] = true }, t);
            Check("screenshot returns an image", !t.IsError && t.ImageBytes() > 500, t.IsError ? t.AllText() : t.ImageBytes() + " bytes", Application.isBatchMode);
            Check("screenshot annotation lists the test button", t.AllText().Contains("udriver-selftest-btn"), null, true);

            // ---------- reflection, watch, wait_for ----------
            t = new ToolResponse();
            yield return Tool("state_get", new JObject { ["path"] = "#" + UiTools.Id(btn) + ".text" }, t);
            Check("state_get reads a property via path", t.AllText().Contains("SelfTest"), t.AllText());
            t = new ToolResponse();
            yield return Tool("state_set", new JObject { ["path"] = "#" + UiTools.Id(btn) + ".tooltip", ["value"] = "tip" }, t);
            Check("state_set writes a property", btn.tooltip == "tip", t.IsError ? t.AllText() : null);
            t = new ToolResponse();
            yield return Tool("scene_tree", new JObject { ["root"] = "/[UDriver SelfTest]", ["depth"] = 1 }, t);
            Check("scene_tree lists scene objects with components", t.AllText().Contains("UIDocument"), t.AllText());
            t = new ToolResponse();
            yield return Tool("types_find", new JObject { ["pattern"] = "*", ["limit"] = 3 }, t);
            Check("types_find enumerates game assemblies", !t.IsError, t.AllText(), true);

            seq = EventStore.LastSeq;
            t = new ToolResponse();
            yield return Tool("watch_add", new JObject { ["path"] = "#" + UiTools.Id(btn) + ".text" }, t);
            btn.text = "Changed";
            yield return null; yield return null;
            Check("watch_add reports a value change", Has(seq, "watch"), t.IsError ? t.AllText() : null);
            t = new ToolResponse();
            yield return Tool("wait_for", new JObject { ["ui_text"] = "Changed", ["timeoutMs"] = 1000 }, t);
            Check("wait_for sees UI state", t.AllText().StartsWith("MATCHED"), t.AllText());
            seq = EventStore.LastSeq;
            Debug.LogWarning("[UDriver] self-test log probe");
            yield return null;
            Check("log capture", EventStore.Query(seq, new[] { "log" }, "self-test log probe", 1, false).Count > 0);
            t = new ToolResponse();
            yield return Tool("probe_remove", new JObject { ["all"] = true }, t);
            t = new ToolResponse();
            yield return Tool("profiler_get", new JObject(), t);
            Check("profiler_get", !t.IsError && t.AllText().Contains("fps"), t.IsError ? t.AllText() : null);

            // ---------- Input System injection ----------
            if (InputTools.InjectAvailable)
            {
                clicks = 0;
                t = new ToolResponse();
                yield return Tool("ui_click", new JObject { ["x"] = c0.x, ["y"] = c0.y, ["method"] = "input" }, t);
                for (int i = 0; i < 5; i++) yield return null;
                Check("ui_click method=input (Input System virtual mouse) reaches UI Toolkit", clicks == 1,
                    "clicks=" + clicks + (clicks == 0 ? " - injection did not reach the UI (window not focused, or the UI input actions do not listen to <Mouse>); method=event still works" : ""), true);
            }
            else Check("Input System injection available", false, "Input System package not active - method=input is unavailable, method=event works", true);

            // ---------- uGUI ----------
            if (Ugui.Available)
            {
                int uclicks = 0;
                canvas = Ugui.SelfTestCreate(() => uclicks++);
                yield return null; yield return null;
                var ubtn = Ugui.FindFirst("udriver-selftest-ugui-btn", null);
                Check("uGUI element discovered", ubtn != null);
                if (ubtn != null)
                {
                    t = new ToolResponse();
                    yield return Tool("ui_click", new JObject { ["id"] = Ugui.Id(ubtn) }, t);
                    Check("ui_click on a uGUI Button triggers onClick", uclicks == 1, "clicks=" + uclicks + " " + (t.IsError || uclicks != 1 ? t.AllText() : ""));
                }
            }
            else Check("uGUI support", false, "com.unity.ugui not installed - uGUI tools inactive", true);

            Check("method tracing (Harmony)", ProbeTools.HarmonyAvailable, ProbeTools.HarmonyAvailable ? null : "0Harmony.dll not found - trace_add unavailable, everything else works", true);
            Finish(r, go, canvas, ps);
        }

        static void Finish(ToolResponse r, GameObject go, GameObject canvas, PanelSettings ps)
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            if (canvas != null) UnityEngine.Object.Destroy(canvas);
            if (ps != null) UnityEngine.Object.Destroy(ps);
            r.Text("UDriver self-test: " + _pass + " passed, " + _fail + " FAILED, " + _warn + " warning(s) | Unity " + Application.unityVersion + " | " + Application.platform
                   + (Application.isEditor ? " editor" : Debug.isDebugBuild ? " dev build" : " release build") + " | screen " + Screen.width + "x" + Screen.height + " | focused " + Application.isFocused);
            foreach (var l in Lines) r.Text(l);
            if (_fail > 0) r.Text("Send this whole report to the developer of the driver: each FAIL line names the capability that needs a fix for this Unity version.");
        }
    }
}

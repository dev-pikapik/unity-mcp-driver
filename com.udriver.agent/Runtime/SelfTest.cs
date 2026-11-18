using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace UDriver
{
    /// <summary>Drives a temporary UI through the same tools to show which capabilities work here.</summary>
    public static class SelfTest
    {
        static readonly List<string> Lines = new List<string>();
        static int _fail, _pass, _warn;

        static void Check(string name, bool ok, string info = null, bool warnOnly = false)
        {
            if (ok) _pass++; else if (warnOnly) _warn++; else _fail++;
            Lines.Add((ok ? "PASS  " : warnOnly ? "WARN  " : "FAIL  ") + name + (string.IsNullOrEmpty(info) ? "" : "  -> " + Util.OneLine(info, 260)));
        }

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

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int TraceTarget(int n, Tuple<int, string, float> item) { return n + item.Item1; }

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
            scroll.contentContainer.style.flexShrink = 0;   // normally set by the theme, which a project may not have
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
            t = new ToolResponse();
            yield return Tool("batch", new JObject { ["calls"] = new JArray(new JObject { ["tool"] = "status" }, new JObject { ["tool"] = "wait_for", ["args"] = new JObject { ["frames"] = 1 } }), ["output"] = "last" }, t);
            Check("batch output=last returns only the last call", !t.IsError && t.AllText().Contains("waited 1 frame") && !t.AllText().Contains("app: "), t.AllText());

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
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            int jumps = 0;
            bool walked = false;
            var walk = new InputAction("udriver-selftest-walk", binding: "<Keyboard>/w");
            var jump = new InputAction("udriver-selftest-jump", binding: "<Keyboard>/space");
            walk.performed += c => { walked = true; go.transform.position = new Vector3(1, go.transform.position.y, 0); };
            jump.performed += c => { jumps++; go.transform.position += Vector3.up; };
            walk.Enable(); jump.Enable();
            go.transform.position = Vector3.zero;
            t = new ToolResponse();
            yield return Tool("play", new JObject
            {
                ["steps"] = new JArray(new JObject { ["hold"] = new JArray("W"), ["tap"] = new JArray("Space"), ["ms"] = 100 }, new JObject()),
                ["observe"] = new JArray("/[UDriver SelfTest]:Transform.position")
            }, t);
            yield return null;
            Check("play holds and taps keys through the Input System", walked && jumps == 1 && !walk.IsPressed(), "walked=" + walked + " jumps=" + jumps + " still pressed=" + walk.IsPressed() + " | " + t.AllText());
            Check("play observe reads state after each step", t.AllText().Contains("[1]") && t.AllText().Contains("position=(1"), t.AllText());
            walk.Dispose(); jump.Dispose();
#endif

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

            // ---------- deep state: paging, ids, query, export, errors, traces, overloads ----------
            var big = new List<Tuple<int, string, float>>(100000);
            for (int i = 0; i < 100000; i++) big.Add(Tuple.Create(i, "item" + i, i * 0.5f));
            string bigId = StateTools.IdOf(big);
            t = new ToolResponse();
            yield return Tool("state_get", new JObject { ["path"] = bigId, ["offset"] = 10, ["limit"] = 2, ["fields"] = new JArray("Item2") }, t);
            Check("state_get pages a collection by id (offset, limit, fields)", t.AllText().Contains("\"item11\"") && !t.AllText().Contains("\"item12\"") && t.AllText().Contains("shown 11-12 of 100000"), t.AllText());
            t = new ToolResponse();
            yield return Tool("state_get", new JObject { ["path"] = bigId, ["filter"] = "item4242", ["limit"] = 5 }, t);
            Check("state_get filter", t.AllText().Contains("[4242]") && t.AllText().Contains("of 11 matching"), t.AllText());
            new GameObject("dup").transform.SetParent(go.transform);
            new GameObject("dup").transform.SetParent(go.transform);
            t = new ToolResponse();
            yield return Tool("state_get", new JObject { ["path"] = "/[UDriver SelfTest]/dup" }, t);
            Check("an ambiguous GameObject path lists the matches with ids", t.IsError && t.AllText().Contains("matches 2") && t.AllText().Contains("#u"), t.AllText());
            t = new ToolResponse();
            yield return Tool("state_get", new JObject { ["path"] = StateTools.IdOf(go) + ":UIDocument.enabled" }, t);
            Check("#u id works as a path", !t.IsError && t.AllText().Contains("True"), t.AllText());

            t = new ToolResponse();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            yield return Tool("state_query", new JObject
            {
                ["path"] = bigId, ["select"] = new JArray("Item1", "Item3"), ["sort"] = "-Item1", ["limit"] = 3, ["stats"] = true,
                ["where"] = new JArray(new JObject { ["field"] = "Item1", ["op"] = ">=", ["value"] = 99990 }, new JObject { ["field"] = "Item2", ["op"] = "contains", ["value"] = "9999" }),
            }, t);
            long qms = clock.ElapsedMilliseconds;
            Check("state_query where + sort + limit + stats over 100k items in < 2 s", qms < 2000 && t.AllText().Contains("10 matched") && t.AllText().Contains("[99999]")
                  && t.AllText().Contains("min 99990, max 99999, avg 99994.5"), qms + " ms | " + t.AllText());

            t = new ToolResponse();
            yield return Tool("state_export", new JObject { ["path"] = StateTools.IdOf(big.GetRange(0, 50)) }, t);
            var m = System.Text.RegularExpressions.Regex.Match(t.AllText(), @" to (.+?\.jsonl)");
            string file = m.Success ? m.Groups[1].Value : null;
            int lines = file != null && System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file).Length : -1;
            if (lines >= 0) System.IO.File.Delete(file);
            Check("state_export writes JSONL into ~/.udriver/exports", lines == 50 && file.Contains(System.IO.Path.Combine(".udriver", "exports")), t.AllText());

            // ---------- memory: leak, snapshot diff, holders, destroyed but referenced ----------
            t = new ToolResponse();
            yield return Tool("memory_summary", new JObject(), t);
            Check("memory_summary", !t.IsError && t.AllText().Contains("\"managed\""), t.AllText());
            yield return Tool("memory_snapshot", new JObject { ["action"] = "take", ["name"] = "udriver-selftest" }, t = new ToolResponse());
            Check("memory_snapshot take", !t.IsError, t.AllText());
            var leak = new List<UnityEngine.Object>();
            for (int i = 0; i < 3; i++) leak.Add(new Texture2D(64, 64) { name = "udriver-selftest-leak" + i });
            var dead = new GameObject("udriver-selftest-destroyed");
            string leakId = StateTools.IdOf(leak[1]);
            leak.Add(dead);
            UnityEngine.Object.Destroy(dead);
            yield return null;
            string deadId = StateTools.IdOf(dead);
            yield return Tool("memory_snapshot", new JObject { ["action"] = "diff", ["from"] = "udriver-selftest", ["limit"] = 500 }, t = new ToolResponse());
            bool grew = false;
            try { foreach (var x in (JArray)JObject.Parse(t.AllText())["types"]) if ((string)x["type"] == "Texture2D" && (long)x["count"] >= 3) grew = true; } catch { }
            Check("memory_snapshot diff shows the leaked textures", grew && t.AllText().Contains(leakId), Util.Cut(t.AllText(), 400));
            yield return Tool("memory_snapshot", new JObject { ["action"] = "drop", ["name"] = "udriver-selftest" }, new ToolResponse());
            yield return Tool("memory_refs", new JObject { ["id"] = leakId, ["from"] = StateTools.IdOf(leak), ["find"] = "all" }, t = new ToolResponse());
            string refs = t.AllText();
            Check("memory_refs finds the holder", refs.Contains(StateTools.IdOf(leak) + "[1]"), refs);
            Check("memory_refs finds a destroyed object still referenced", deadId.StartsWith("#o", StringComparison.Ordinal) && ReferenceEquals(StateTools.ById(deadId), dead) && refs.Contains("\"" + deadId + "\"") && refs.Contains(StateTools.IdOf(leak) + "[3]"), deadId + " " + refs);
            foreach (var o in leak) if (o != null) UnityEngine.Object.Destroy(o);
            leak.Clear();

            // ---------- rendering: counters, scene, camera picture ----------
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "udriver-selftest-cube";
            UnityEngine.Object.Destroy(cube.GetComponent<Collider>());
            cube.transform.position = new Vector3(10000, 10000, 10005);
            var camRt = new RenderTexture(64, 64, 24);
            var cam = new GameObject("udriver-selftest-camera").AddComponent<Camera>();
            cam.transform.position = new Vector3(10000, 10000, 10000);
            cam.targetTexture = camRt;
            yield return Tool("render_stats", new JObject { ["frames"] = 5 }, t = new ToolResponse());
            var rs = t.IsError ? null : JObject.Parse(t.AllText());
            var dc = rs != null ? rs["counters"]["drawCalls"] as JObject : null;
            var tri = rs != null ? rs["counters"]["triangles"] as JObject : null;
            Check("render_stats counts draw calls and triangles", dc != null && tri != null && (double)dc["avg"] > 0 && (double)tri["avg"] > 0, Util.Cut(t.AllText(), 400), rs != null && rs["counters"]["drawCalls"].Type == JTokenType.Null);
            yield return Tool("render_scene", new JObject { ["camera"] = StateTools.IdOf(cam) }, t = new ToolResponse());
            Check("render_scene lists what a camera sees", t.AllText().Contains("udriver-selftest-cube"), Util.Cut(t.AllText(), 400));
            yield return Tool("screenshot", new JObject { ["camera"] = StateTools.IdOf(cam), ["maxWidth"] = 64 }, t = new ToolResponse());
            Check("screenshot camera= renders one camera", !t.IsError && t.ImageBytes() > 0, t.AllText());
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(cam.gameObject);
            UnityEngine.Object.Destroy(camRt);
            UnityEngine.Object.Destroy(cube);

            string probeTag = "#" + Time.frameCount;
            for (int i = 0; i < 2; i++) Debug.LogError("[UDriver] self-test error probe (expected) " + probeTag);
            yield return null;
            t = new ToolResponse();
            yield return Tool("errors", new JObject { ["contains"] = "self-test error probe", ["full"] = true }, t);
            var gm = System.Text.RegularExpressions.Regex.Match(t.AllText(), @"(\d+)x error at ");
            Check("errors groups repeats and keeps the full stack", gm.Success && int.Parse(gm.Groups[1].Value) >= 2 && t.AllText().Contains(probeTag) && t.AllText().Contains("SelfTest.cs"), t.AllText());
            t = new ToolResponse();
            yield return Tool("events_config", new JObject { ["firstChance"] = true, ["firstChanceMax"] = 5 }, t);
            seq = EventStore.LastSeq;
            try { throw new InvalidOperationException("self-test first-chance probe"); } catch (InvalidOperationException) { }
            yield return Tool("events_config", new JObject { ["firstChance"] = false }, new ToolResponse());
            Check("first-chance capture records a caught exception", EventStore.Query(seq, new[] { "exception.firstchance" }, "first-chance probe", 1, false).Count > 0, t.AllText(), t.AllText().Contains("not supported"));

            t = new ToolResponse();
            yield return Tool("state_call", new JObject { ["path"] = "@System.Math", ["method"] = "Max", ["args"] = new JArray(1.5, 2) }, t);
            var amb = new ToolResponse();
            yield return Tool("state_call", new JObject { ["path"] = "@System.Convert", ["method"] = "ToString", ["args"] = new JArray(JValue.CreateNull()) }, amb);
            var picked = new ToolResponse();
            yield return Tool("state_call", new JObject { ["path"] = "@System.Convert", ["method"] = "ToString", ["args"] = new JArray(JValue.CreateNull()), ["types"] = new JArray("string") }, picked);
            Check("state_call picks overloads by argument types, lists ties, accepts types", t.AllText().Contains("double Max(") && t.AllText().Contains("-> 2")
                  && amb.IsError && amb.AllText().Contains("equally") && !picked.IsError, t.AllText() + " | " + amb.AllText() + " | " + picked.AllText());

            if (ProbeTools.HarmonyAvailable)
            {
                t = new ToolResponse();
                int probe = -1;
                try
                {
                    probe = ProbeTools.Patch("SelfTest.TraceTarget", new List<MethodBase> { typeof(SelfTest).GetMethod("TraceTarget", BindingFlags.NonPublic | BindingFlags.Static) },
                        new JObject { ["capture"] = new JArray("item.Item2"), ["when"] = new JObject { ["field"] = "n", ["op"] = ">", ["value"] = 5 }, ["maxHits"] = 2, ["stack"] = true }, t);
                }
                catch (Exception e) { t.Error(e.Message); }
                for (int i = 0; i < 10; i++) TraceTarget(i, big[i]);
                var hits = new ToolResponse();
                yield return Tool("probe_list", new JObject { ["id"] = probe, ["offset"] = 1, ["limit"] = 1 }, hits);
                string h = hits.AllText();
                Check("trace_add when + capture + maxHits + stack, hits read page by page", h.Contains("item.Item2=\"item7\"") && h.Contains("hits shown 2-2 of 2") && h.Contains("10 call(s) total") && h.Contains("SelfTest"),
                      t.AllText() + " | " + h);
                yield return Tool("probe_remove", new JObject { ["id"] = probe }, new ToolResponse());
            }

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

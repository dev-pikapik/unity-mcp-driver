using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace UDriver
{
    public static class InputTools
    {
        static EventModifiers _mods;
        static bool _evDown;
        static int _buttons; // pressed-button mask for method=input (bit0 left, bit1 right, bit2 middle)

        public static bool InjectAvailable { get { return Inj.Available; } }

        internal static void Register()
        {
            Driver.Register("ui_click", Click);
            Driver.Register("ui_hover", Hover);
            Driver.Register("ui_drag", Drag);
            Driver.Register("pointer", Pointer);
            Driver.Register("ui_scroll", Scroll);
            Driver.Register("ui_scroll_to", ScrollTo);
            Driver.Register("ui_set_value", SetValue);
            Driver.Register("ui_focus", Focus);
            Driver.Register("type_text", TypeText);
            Driver.Register("key", KeyTool);
        }

        // ---------------- primitives ----------------

        static string Method(JObject a, ToolResponse r)
        {
            string m = a.Str("method", "event");
            // Verified in Unity 6 source (DefaultEventSystem.Update): on desktop, UI Toolkit drops device input while the app is unfocused.
            if (m == "input" && !Application.isFocused)
                r.Text("WARNING: the game window is not focused. On Windows/macOS/Linux Unity's UI ignores device input in that state, so method=input may have no effect on UI. Use method=event (works unfocused) or bring the window to the front.");
            if (m == "input" && !Inj.Available)
                throw new ToolException("method=input needs the Input System package with 'Active Input Handling' set to Input System or Both. Use method=event.");
            if (m != "input" && m != "event" && m != "set") throw new ToolException("unknown method '" + m + "'");
            return m;
        }

        static int Btn(JObject a)
        {
            string b = a.Str("button", "left");
            return b == "right" ? 1 : b == "middle" ? 2 : 0;
        }

        static void SetMods(JObject a)
        {
            _mods = EventModifiers.None;
            var ms = a.Strs("modifiers");
            if (ms == null) return;
            foreach (var m in ms)
            {
                switch (m.ToLowerInvariant())
                {
                    case "shift": _mods |= EventModifiers.Shift; break;
                    case "ctrl": case "control": _mods |= EventModifiers.Control; break;
                    case "alt": _mods |= EventModifiers.Alt; break;
                    case "cmd": case "command": _mods |= EventModifiers.Command; break;
                }
            }
        }

        static VisualElement Top(UiTools.Target t)
        {
            if (t.Top == null) throw new ToolException("there is no UI Toolkit panel at that point - use method=input to send real input to uGUI / the game world");
            return t.Top;
        }

        /// <summary>Synthesize a pointer / wheel event and dispatch it through the panel exactly like a real one (target is picked by position).</summary>
        static void SendMouse(VisualElement top, EventType type, Vector2 panelPos, int button, int clicks, Vector2 delta)
        {
            var ev = new Event { type = type, mousePosition = panelPos, button = button, clickCount = clicks, delta = delta, modifiers = _mods };
            EventBase e;
            switch (type)
            {
                case EventType.MouseDown: e = PointerDownEvent.GetPooled(ev); break;
                case EventType.MouseUp: e = PointerUpEvent.GetPooled(ev); break;
                case EventType.ScrollWheel: e = WheelEvent.GetPooled(ev); break;
                default: e = PointerMoveEvent.GetPooled(ev); break;
            }
            using (e) top.SendEvent(e);
        }

        /// <summary>uGUI route: the target is a canvas object, or a uGUI press is in progress.</summary>
        static bool U(UiTools.Target t) { return t.Go != null || Ugui.Pressed; }

        static void Move(UiTools.Target t, string m, bool dragging, Vector2 delta)
        {
            if (m == "input") Inj.SendMouse(t.Screen, _buttons, Vector2.zero);
            else if (U(t)) Ugui.Move(t.Screen);
            else SendMouse(Top(t), dragging ? EventType.MouseDrag : EventType.MouseMove, t.Panel, 0, 0, delta);
        }

        static void Down(UiTools.Target t, string m, int button, int clicks)
        {
            if (m == "input") { _buttons |= 1 << button; Inj.SendMouse(t.Screen, _buttons, Vector2.zero); }
            else if (t.Go != null) Ugui.Down(t.Screen, button, clicks);
            else { SendMouse(Top(t), EventType.MouseDown, t.Panel, button, clicks, Vector2.zero); _evDown = true; }
        }

        static void Up(UiTools.Target t, string m, int button, int clicks)
        {
            if (m == "input") { _buttons &= ~(1 << button); Inj.SendMouse(t.Screen, _buttons, Vector2.zero); }
            else if (U(t)) Ugui.Up(t.Screen, button);
            else { SendMouse(Top(t), EventType.MouseUp, t.Panel, button, clicks, Vector2.zero); _evDown = false; }
        }

        static string Where(UiTools.Target t)
        {
            return (t.Go != null ? Ugui.Desc(t.Go) + " " : t.El != null ? UiTools.Desc(t.El) + " " : "") + "at (" + Mathf.RoundToInt(t.Screen.x) + "," + Mathf.RoundToInt(t.Screen.y) + ")";
        }

        /// <summary>What happened in the UI since the action started - lets the agent verify the action took effect.</summary>
        static void Feedback(ToolResponse r, long seq, bool expectClick)
        {
            var evs = EventStore.Query(seq, new[] { "ui.click", "ui.change", "ui.submit", "ui.focus" }, null, 8, false);
            foreach (var e in evs) r.Text("  " + EventStore.Fmt(e, false));
            r.Text("seq_before=" + seq + " | " + EventStore.Summary(seq));
            if (expectClick && EventStore.Query(seq, new[] { "ui.click" }, null, 1, false).Count == 0)
                r.Text("NOTE: no ClickEvent was produced. The element may be covered, disabled, or not a UI Toolkit element - check ui_inspect, or retry with method=input.");
        }

        static VisualElement Focused(out VisualElement top)
        {
            top = null;
            foreach (var root in UiTools.Roots())
            {
                if (top == null) top = root;
                var f = root.focusController != null ? root.focusController.focusedElement as VisualElement : null;
                if (f != null) { top = root; return f; }
            }
            return null;
        }

        static void SendKey(VisualElement top, bool down, char c, KeyCode code)
        {
            if (top == null) throw new ToolException("no UI Toolkit panel to send keys to - use method=input");
            EventBase e = down ? (EventBase)KeyDownEvent.GetPooled(c, code, _mods) : KeyUpEvent.GetPooled(c, code, _mods);
            using (e) top.SendEvent(e);
        }

        /// <summary>Runtime UI Toolkit drives submit/cancel/tab through navigation events rather than raw keys.</summary>
        static void Nav(VisualElement top, VisualElement focused, KeyCode code)
        {
            EventBase e = null;
            if (code == KeyCode.Return || code == KeyCode.KeypadEnter) e = NavigationSubmitEvent.GetPooled(_mods);
            else if (code == KeyCode.Escape) e = NavigationCancelEvent.GetPooled(_mods);
            else if (code == KeyCode.Tab) e = NavigationMoveEvent.GetPooled((_mods & EventModifiers.Shift) != 0 ? NavigationMoveEvent.Direction.Previous : NavigationMoveEvent.Direction.Next, _mods);
            else if (!(focused is TextElement) && !(focused is TextField))
            {
                if (code == KeyCode.UpArrow) e = NavigationMoveEvent.GetPooled(NavigationMoveEvent.Direction.Up, _mods);
                else if (code == KeyCode.DownArrow) e = NavigationMoveEvent.GetPooled(NavigationMoveEvent.Direction.Down, _mods);
                else if (code == KeyCode.LeftArrow) e = NavigationMoveEvent.GetPooled(NavigationMoveEvent.Direction.Left, _mods);
                else if (code == KeyCode.RightArrow) e = NavigationMoveEvent.GetPooled(NavigationMoveEvent.Direction.Right, _mods);
            }
            if (e == null) return;
            e.target = focused ?? top;
            using (e) top.SendEvent(e);
        }

        static string SetProperty(VisualElement el, string prop, JToken value)
        {
            var p = UiTools.Prop(el.GetType(), prop);
            if (p == null || !p.CanWrite) throw new ToolException(el.GetType().Name + " has no writable public property '" + prop + "'");
            object v;
            // EnumField.value is typed System.Enum: take the concrete enum type from the current value.
            var target = p.PropertyType;
            if (target == typeof(Enum) && p.CanRead && p.GetValue(el) != null) target = p.GetValue(el).GetType();
            try { v = Util.To(value, target); }
            catch (ToolException) { throw; }
            catch (Exception e) { throw new ToolException("cannot convert value to " + p.PropertyType.Name + ": " + e.Message); }
            p.SetValue(el, v);
            var now = p.GetValue(el);
            return now == null ? "null" : now.ToString();
        }

        static string Snapshot(VisualElement el)
        {
            if (el == null) return null;
            string text = null;
            var p = UiTools.Prop(el.GetType(), "text");
            if (p != null && p.CanRead) { try { text = p.GetValue(el) as string; } catch { } }
            return UiTools.ValueOf(el) + "|" + text;
        }

        // ---------------- tools ----------------

        static IEnumerator Click(JObject a, ToolResponse r)
        {
            UiTools.HookPanels();
            var t = UiTools.Resolve(a);
            string m = Method(a, r);
            int button = Btn(a), count = Mathf.Clamp(a.Int("count", 1), 1, 3), hold = a.Int("holdMs", 0);
            long seq = EventStore.LastSeq;
            string warn = UiTools.Warnings(t);
            if (warn != null) r.Text(warn);
            SetMods(a);
            Move(t, m, false, Vector2.zero);
            yield return null;
            for (int i = 1; i <= count; i++)
            {
                Down(t, m, button, i);
                if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else yield return null;
                Up(t, m, button, i);
                yield return null;
            }
            _mods = EventModifiers.None;
            yield return null;
            r.Text((count > 1 ? "double-clicked " : hold > 0 ? "long-pressed (" + hold + " ms) " : "clicked ") + Where(t) + " via " + m);
            Feedback(r, seq, button == 0 && m == "event" && (t.El != null || t.Go != null));
        }

        static IEnumerator Hover(JObject a, ToolResponse r)
        {
            var t = UiTools.Resolve(a);
            string m = Method(a, r);
            Move(t, m, false, Vector2.zero);
            yield return null; yield return null;
            r.Text("pointer moved to " + Where(t) + " via " + m);
        }

        static IEnumerator Drag(JObject a, ToolResponse r)
        {
            UiTools.HookPanels();
            var from = UiTools.Resolve(a);
            var to = UiTools.Resolve(a, "to_");
            string m = Method(a, r);
            int button = Btn(a), steps = Mathf.Max(1, a.Int("steps", 12)), hold = a.Int("holdMs", 0);
            float stepWait = a.Int("durationMs", 300) / 1000f / steps;
            long seq = EventStore.LastSeq;
            var top = from.Top ?? to.Top;
            // The whole gesture goes to the UI system of the source; express the destination in its coordinates.
            if (top != null) to.Panel = UiTools.ScreenToPanel(top, to.Screen);
            to.Top = top;
            var dragGo = from.Go;

            Move(from, m, false, Vector2.zero);
            yield return null;
            Down(from, m, button, 1);
            if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else yield return null;
            var cur = from;
            for (int i = 1; i <= steps; i++)
            {
                float k = i / (float)steps;
                var next = new UiTools.Target { Top = top, Go = dragGo, Screen = Vector2.Lerp(from.Screen, to.Screen, k), Panel = Vector2.Lerp(from.Panel, to.Panel, k) };
                Move(next, m, true, next.Panel - cur.Panel);
                cur = next;
                if (stepWait > 0.02f) yield return new WaitForSecondsRealtime(stepWait); else yield return null;
            }
            Up(cur, m, button, 1);
            yield return null; yield return null;
            r.Text("dragged from " + Where(from) + " to " + Where(to) + " in " + steps + " steps via " + m);
            Feedback(r, seq, false);
        }

        static IEnumerator Pointer(JObject a, ToolResponse r)
        {
            UiTools.HookPanels();
            var t = UiTools.Resolve(a);
            string m = Method(a, r), action = a.Str("action");
            int button = Btn(a);
            long seq = EventStore.LastSeq;
            SetMods(a);
            if (action == "down") { Move(t, m, false, Vector2.zero); yield return null; Down(t, m, button, 1); }
            else if (action == "up") Up(t, m, button, 1);
            else if (action == "move") Move(t, m, _evDown, Vector2.zero);
            else throw new ToolException("action must be down, move or up");
            _mods = EventModifiers.None;
            yield return null;
            r.Text("pointer " + action + " " + Where(t) + " via " + m);
            Feedback(r, seq, false);
        }

        static IEnumerator Scroll(JObject a, ToolResponse r)
        {
            var t = UiTools.Resolve(a);
            float dx = a.Num("dx"), dy = a.Num("dy");
            if (a.Str("mode", "offset") == "wheel")
            {
                string m = Method(a, r);
                if (m == "input") Inj.SendMouse(t.Screen, _buttons, new Vector2(-dx, -dy));
                else if (t.Go != null) Ugui.Wheel(t.Screen, dx, dy);
                else { Move(t, m, false, Vector2.zero); SendMouse(Top(t), EventType.ScrollWheel, t.Panel, 0, 0, new Vector2(dx, dy)); }
                yield return null;
                if (m == "input") Inj.SendMouse(t.Screen, _buttons, Vector2.zero);
                yield return null;
                r.Text("wheel (" + dx + "," + dy + ") " + Where(t) + " via " + m);
                yield break;
            }
            if (t.Go != null) { string res = Ugui.ScrollBy(t.Go, dx, dy); yield return null; r.Text(res); yield break; }
            var el = t.El ?? (t.Top != null ? t.Top.panel.Pick(t.Panel) : null);
            ScrollView sv = null;
            for (var p = el; p != null && sv == null; p = p.hierarchy.parent) sv = p as ScrollView;
            if (sv == null) throw new ToolException("no ScrollView at or above the target - try mode=wheel, or ui_drag for swipe-style scrolling");
            var before = sv.scrollOffset;
            sv.scrollOffset = before + new Vector2(dx, dy);
            yield return null;
            var max = new Vector2(Mathf.Max(0, sv.contentContainer.layout.width - sv.contentViewport.layout.width), Mathf.Max(0, sv.contentContainer.layout.height - sv.contentViewport.layout.height));
            r.Text("scrolled " + UiTools.Desc(sv) + ": offset " + before + " -> " + sv.scrollOffset + " (max " + max + ")" + (sv.scrollOffset == before ? " - did not move (already at the limit?)" : ""));
        }

        static IEnumerator ScrollTo(JObject a, ToolResponse r)
        {
            var t = UiTools.Resolve(a);
            if (t.Go != null && !a.Has("x")) { string res = Ugui.ScrollTo(t.Go); yield return null; r.Text(res); yield break; }
            if (t.El == null) throw new ToolException("ui_scroll_to needs an element (id, name or text)");
            int n = 0;
            for (var p = t.El.hierarchy.parent; p != null; p = p.hierarchy.parent)
            {
                var sv = p as ScrollView;
                if (sv != null && sv.contentContainer != null && UiTools.IsAncestor(sv.contentContainer, t.El)) { sv.ScrollTo(t.El); n++; }
            }
            yield return null; yield return null;
            r.Text(n == 0 ? "element is not inside a ScrollView" : "scrolled " + n + " ScrollView(s); now: " + UiTools.Line(t.El));
        }

        static IEnumerator SetValue(JObject a, ToolResponse r)
        {
            UiTools.HookPanels();
            var t = UiTools.Resolve(a);
            long seq = EventStore.LastSeq;
            if (t.Go != null && !a.Has("x"))
            {
                string res = Ugui.SetValue(t.Go, a.Str("property"), a["value"]);
                yield return null;
                r.Text("set " + Ugui.Desc(t.Go) + ": " + Util.OneLine(res, 200));
                Feedback(r, seq, false);
                yield break;
            }
            if (t.El == null) throw new ToolException("ui_set_value needs an element (id, name or text)");
            string prop = a.Str("property", "value");
            string now = SetProperty(t.El, prop, a["value"]);
            yield return null;
            r.Text("set " + UiTools.Desc(t.El) + "." + prop + " = " + Util.OneLine(now, 200));
            Feedback(r, seq, false);
        }

        static IEnumerator Focus(JObject a, ToolResponse r)
        {
            var t = UiTools.Resolve(a);
            if (t.Go != null && !a.Has("x")) { Ugui.Focus(t.Go); yield return null; r.Text("selected uGUI object is now: " + Ugui.Desc(Ugui.Selected())); yield break; }
            if (t.El == null) throw new ToolException("ui_focus needs an element (id, name or text)");
            t.El.Focus();
            yield return null;
            VisualElement top;
            r.Text("focused element is now: " + UiTools.Desc(Focused(out top)));
        }

        static IEnumerator TypeText(JObject a, ToolResponse r)
        {
            UiTools.HookPanels();
            string text = a.Str("text");
            if (text == null) throw new ToolException("text is required");
            string m = Method(a, r);
            long seq = EventStore.LastSeq;
            VisualElement el = null, top;
            GameObject uguiTarget = null;
            if (a.Has("id") || a.Has("name"))
            {
                var sel = new JObject();
                if (a.Has("id")) sel["id"] = a.Int("id"); else sel["name"] = a.Str("name");
                var tt = UiTools.Resolve(sel);
                if (tt.Go != null && m != "input") uguiTarget = tt.Go;
                else if (tt.El != null) { el = tt.El; el.Focus(); yield return null; yield return null; }
                else if (tt.Go != null) { Ugui.Focus(tt.Go); yield return null; }
            }
            var focusedNow = Focused(out top);
            if (uguiTarget == null && el == null && focusedNow == null && m != "input") uguiTarget = Ugui.Selected();
            if (uguiTarget != null)
            {
                string res = Ugui.TypeText(uguiTarget, text, a.Bool("clear"), a.Bool("submit"));
                yield return null;
                r.Text("typed into " + Ugui.Desc(uguiTarget) + " (uGUI fields are filled by setting the text; onValueChanged fires): " + Util.OneLine(res, 200));
                Feedback(r, seq, false);
                yield break;
            }
            var subject = el ?? focusedNow;
            if (subject == null && m != "input") throw new ToolException("nothing is focused - pass id/name of the field to type into");
            if (a.Bool("clear") && subject != null && UiTools.Prop(subject.GetType(), "value") != null) { SetProperty(subject, "value", new JValue("")); yield return null; }

            string before = Snapshot(subject), how = m;
            if (m == "set") SetProperty(subject, "value", new JValue((UiTools.ValueOf(subject) ?? "") + text));
            else
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (m == "input") Inj.SendText(c);
                    else if (c == '\n') { SendKey(top, true, '\0', KeyCode.Return); SendKey(top, true, '\n', KeyCode.None); }
                    else SendKey(top, true, c, KeyCode.None);
                    if (i % 6 == 5) yield return null;
                }
                yield return null; yield return null;
                var vp = subject != null ? UiTools.Prop(subject.GetType(), "value") : null;
                if (subject != null && text.Length > 0 && Snapshot(subject) == before && vp != null && vp.PropertyType == typeof(string))
                {
                    SetProperty(subject, "value", new JValue((UiTools.ValueOf(subject) ?? "") + text));
                    how = m + " had no effect -> fell back to setting the value directly";
                }
            }
            yield return null;
            if (a.Bool("submit"))
            {
                if (m == "input") { Inj.SendKey("Enter", true); yield return null; Inj.SendKey("Enter", false); }
                else { var f = Focused(out top); SendKey(top, true, '\0', KeyCode.Return); Nav(top, f, KeyCode.Return); SendKey(top, false, '\0', KeyCode.Return); }
                yield return null;
            }
            r.Text("typed " + text.Length + " char(s) into " + UiTools.Desc(subject) + " (" + how + "); value is now: " + Util.OneLine(subject != null ? UiTools.ValueOf(subject) : "?", 200));
            Feedback(r, seq, false);
        }

        static IEnumerator KeyTool(JObject a, ToolResponse r)
        {
            UiTools.HookPanels();
            string key = a.Str("key");
            if (key == null) throw new ToolException("key is required");
            string m = Method(a, r), action = a.Str("action", "press");
            int hold = a.Int("holdMs", 0);
            long seq = EventStore.LastSeq;
            if (m == "input")
            {
                if (action != "up") Inj.SendKey(key, true);
                if (action == "press") { if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else yield return null; }
                if (action != "down") Inj.SendKey(key, false);
            }
            else
            {
                KeyCode code;
                if (!Enum.TryParse(key, true, out code))
                    throw new ToolException("unknown key '" + key + "'. With method=event use UnityEngine.KeyCode names: Return, Escape, Tab, Space, Backspace, Delete, LeftArrow, A, Alpha1, F5...");
                SetMods(a);
                VisualElement top;
                var f = Focused(out top);
                // No UI Toolkit focus but a selected uGUI object: route submit / cancel / arrows there.
                if (f == null && action != "up" && Ugui.Key(code)) { yield return null; r.Text("key " + key + " sent to uGUI selection " + Ugui.Desc(Ugui.Selected())); Feedback(r, seq, false); yield break; }
                if (action != "up") { SendKey(top, true, '\0', code); Nav(top, f, code); }
                if (action == "press") { if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else yield return null; }
                if (action != "down") SendKey(top, false, '\0', code);
                _mods = EventModifiers.None;
            }
            yield return null;
            r.Text("key " + action + " " + key + " via " + m);
            Feedback(r, seq, false);
        }

        // ---------------- Input System injection ----------------

#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
        static class Inj
        {
            public static readonly bool Available = true;
            static Mouse _mouse;
            static Keyboard _keyboard;
            static readonly HashSet<Key> Held = new HashSet<Key>();

            static void Ensure()
            {
                if (_mouse != null && _mouse.added) return;
                _mouse = InputSystem.AddDevice<Mouse>("UDriverMouse");
                _keyboard = InputSystem.AddDevice<Keyboard>("UDriverKeyboard");
                // Keep receiving injected input while the game window is in the background.
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            }

            public static void SendMouse(Vector2 screenTopLeft, int buttons, Vector2 scroll)
            {
                Ensure();
                var st = new MouseState { position = new Vector2(screenTopLeft.x, Screen.height - screenTopLeft.y), scroll = scroll };
                // Fully qualified: UnityEngine.UIElements also declares a MouseButton enum.
                st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, (buttons & 1) != 0)
                       .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Right, (buttons & 2) != 0)
                       .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Middle, (buttons & 4) != 0);
                InputSystem.QueueStateEvent(_mouse, st);
            }

            public static void SendKey(string name, bool down)
            {
                Ensure();
                Key k;
                if (!Enum.TryParse(name, true, out k))
                    throw new ToolException("unknown key '" + name + "'. With method=input use Input System Key names: Enter, Escape, Tab, Space, Backspace, Delete, LeftArrow, A, Digit1, F5...");
                if (down) Held.Add(k); else Held.Remove(k);
                var arr = new Key[Held.Count];
                Held.CopyTo(arr);
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(arr));
            }

            public static void SendText(char c)
            {
                Ensure();
                InputSystem.QueueTextEvent(_keyboard, c);
            }
        }
#else
        static class Inj
        {
            public static readonly bool Available = false;
            public static void SendMouse(Vector2 screenTopLeft, int buttons, Vector2 scroll) { }
            public static void SendKey(string name, bool down) { }
            public static void SendText(char c) { }
        }
#endif
    }
}

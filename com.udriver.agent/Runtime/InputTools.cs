using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
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
        public static bool OsKeysAvailable { get { return Os.Available && LegacyInput; } }

        /// <summary>False when the player runs the Input System only, so Input.GetKey cannot see any key.</summary>
        static bool LegacyInput { get { try { Input.GetKey(KeyCode.None); return true; } catch (InvalidOperationException) { return false; } } }

        internal static void Register()
        {
            Driver.Register("ui_click", Leased("ui_click", Click));
            Driver.Register("ui_hover", Leased("ui_hover", Hover));
            Driver.Register("ui_drag", Leased("ui_drag", Drag));
            Driver.Register("pointer", Leased("pointer", Pointer));
            Driver.Register("ui_scroll", Leased("ui_scroll", Scroll));
            Driver.Register("ui_scroll_to", Leased("ui_scroll_to", ScrollTo));
            Driver.Register("ui_set_value", Leased("ui_set_value", SetValue));
            Driver.Register("ui_focus", Leased("ui_focus", Focus));
            Driver.Register("type_text", Leased("type_text", TypeText));
            Driver.Register("key", Leased("key", KeyTool));
            Driver.Register("play", Leased("play", Play));
            Driver.Register("input_actions", Driver.Sync((a, r) => Inj.Describe(r)));
            Driver.Register("take_controls", Driver.Sync(TakeControls));
        }

        // ---------------- one agent controls the input until it is idle ----------------

        const float OwnerIdle = 30f;
        static string _owner, _ownerName;
        static float _ownerLast;

        static string Owner(Job me)
        {
            if (_owner == null || Time.realtimeSinceStartup - _ownerLast >= OwnerIdle) return null;
            return me != null && me.Holder == _owner ? null : _ownerName;
        }

        static void Own(Job me)
        {
            if (Owner(me) is string other)
                throw new ToolException("This game is being controlled by " + other + " right now. You can still read it, take screenshots, inspect memory and profile it. " +
                                        "Input frees up after " + OwnerIdle + " s without input from " + other + "; meanwhile coordinate with team_post or work in another instance (list_instances).");
            _owner = me.Holder; _ownerName = me.Client; _ownerLast = Time.realtimeSinceStartup;
        }

        internal static string Controls(Job me)
        {
            if (_owner == null || Time.realtimeSinceStartup - _ownerLast >= OwnerIdle) return "controls free";
            return (me != null && me.Holder == _owner ? "controlled by you" : "controlled by " + _ownerName) + " (idle " + (int)(Time.realtimeSinceStartup - _ownerLast) + " s)";
        }

        static void TakeControls(JObject a, ToolResponse r)
        {
            var me = Driver.Current;
            if (me == null) throw new ToolException("take_controls needs a caller");
            if (a.Bool("release", false))
            {
                if (_owner == me.Holder) _owner = null;
                r.Text("Released the controls.");
                return;
            }
            Own(me);
            r.Text("You control this game. Other agents get an error on input until you are idle for " + OwnerIdle + " s or call take_controls release=true.");
        }

        // ---------------- one agent at a time on the input devices ----------------

        static Job _lease;
        static float _leaseEnd;
        const float LeaseWait = 10f;

        static ToolHandler Leased(string name, ToolHandler h) { return (a, r) => WithLease(name, h, a, r); }

        internal static void EnsureDevices() { Inj.Ensure(); }

        static IEnumerator WithLease(string name, ToolHandler h, JObject a, ToolResponse r)
        {
            var me = Driver.Current;
            if (me != null) Own(me);
            float t0 = Time.realtimeSinceStartup;
            while (me != null && _lease != null && _lease != me && !_lease.Done.IsSet && _lease.Client != me.Client)
            {
                if (_lease.Tool == "play")
                    throw new ToolException("input busy: " + _lease.Client + " is running play, ~" + Mathf.Max(0, Mathf.RoundToInt((_leaseEnd - Time.realtimeSinceStartup) * 1000f)) + " ms left. Try again after that.");
                if (Time.realtimeSinceStartup - t0 > LeaseWait)
                    throw new ToolException("input busy: " + _lease.Client + " has been using the input for over " + LeaseWait + " s (" + _lease.Tool + "). Try again in a moment.");
                yield return null;
            }
            var prev = _lease;
            _lease = me;
            _leaseEnd = Time.realtimeSinceStartup;
            int sent = Inj.Sent;
            var rec = RecordTools.Action(name, a);
            try { yield return h(a, r); }
            finally
            {
                RecordTools.End(rec, Inj.Sent != sent, r);
                if (_lease == me) _lease = prev != null && prev != me && !prev.Done.IsSet ? prev : null;
                if (me != null && _owner == me.Holder) _ownerLast = Time.realtimeSinceStartup;
            }
        }

        const int MaxHoldMs = 10000;

        const string NeedInputSystem = "needs the Input System: install com.unity.inputsystem and set Player Settings > Active Input Handling to Input System or Both. A game that reads only the old Input Manager cannot be driven this way.";

        // ---------------- primitives ----------------

        static string Method(JObject a, ToolResponse r)
        {
            string m = a.Str("method", "event");
            // UI Toolkit drops device input while the app is unfocused.
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
            if (t.Top == null) throw new ToolException("there is no UI Toolkit panel at that point - " + (Inj.Available ? "use method=input to send real input to uGUI / the game world"
                : "target a uGUI or UI Toolkit element by id or name (to scroll: a ScrollRect or ScrollView). Real input at a point " + NeedInputSystem));
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
            // Text fields match editing keys (arrows, Home/End, Page, F-keys, Backspace, Delete) only with the FunctionKey flag real keyboards set.
            var mods = (code >= KeyCode.UpArrow && code <= KeyCode.F15) || code == KeyCode.Backspace || code == KeyCode.Delete ? _mods | EventModifiers.FunctionKey : _mods;
            EventBase e = down ? (EventBase)KeyDownEvent.GetPooled(c, code, mods) : KeyUpEvent.GetPooled(c, code, mods);
            using (e) top.SendEvent(e);
        }

        static KeyCode Code(string key)
        {
            KeyCode c;
            string k = key.Length == 1 && char.IsDigit(key[0]) ? "Alpha" + key
                : key.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) ? "Alpha" + key.Substring(5)
                : key.Equals("Enter", StringComparison.OrdinalIgnoreCase) ? "Return"
                : key.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : key;
            if (!Enum.TryParse(k, true, out c) || c == KeyCode.None || char.IsDigit(k[0]))
                throw new ToolException("unknown key '" + key + "'. Use UnityEngine.KeyCode names: Return, Escape, Tab, Space, Backspace, Delete, LeftArrow, W, Alpha1, LeftShift, F5...");
            return c;
        }

        /// <summary>Input System Key name for a KeyCode.</summary>
        static string KeyName(KeyCode c)
        {
            if (c >= KeyCode.Alpha0 && c <= KeyCode.Alpha9) return "Digit" + (c - KeyCode.Alpha0);
            return c == KeyCode.Return ? "Enter" : c == KeyCode.KeypadEnter ? "NumpadEnter" : c == KeyCode.LeftControl ? "LeftCtrl" : c == KeyCode.RightControl ? "RightCtrl" : c.ToString();
        }

        static bool Held(KeyCode c)
        {
            try { return Input.GetKey(c); } catch (InvalidOperationException) { return false; }
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
            StateTools.Guard(p, "ui_set_value");
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

        /// <summary>Text fields keep a stale selection after Focus(); typed characters must land at the end.</summary>
        static void CaretToEnd(VisualElement el)
        {
            if (el == null) return;
            PropertyInfo text = UiTools.Prop(el.GetType(), "text"), cur = UiTools.Prop(el.GetType(), "cursorIndex"), sel = UiTools.Prop(el.GetType(), "selectIndex");
            if (text == null || cur == null || sel == null || !cur.CanWrite || !sel.CanWrite) return;
            int n = ((text.GetValue(el) as string) ?? "").Length;
            sel.SetValue(el, n); cur.SetValue(el, n);
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
            int button = Btn(a), count = Mathf.Clamp(a.Int("count", 1), 1, 3), hold = Mathf.Clamp(a.Int("holdMs", 0), 0, MaxHoldMs);
            long seq = EventStore.LastSeq;
            string warn = UiTools.Warnings(t);
            if (warn != null) r.Text(warn);
            SetMods(a);
            bool now = m == "event" && hold == 0;
            var btn = t.El as Button ?? t.El?.GetFirstAncestorOfType<Button>();
            bool fired = false;
            Action onClick = () => fired = true;
            if (btn != null) btn.clicked += onClick;
            try
            {
                Move(t, m, false, Vector2.zero);
                if (!now) yield return null;
                for (int i = 1; i <= count; i++)
                {
                    Down(t, m, button, i);
                    if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else if (!now) yield return null;
                    Up(t, m, button, i);
                    if (!now) yield return null;
                }
                _mods = EventModifiers.None;
                yield return null;
            }
            finally { if (btn != null) btn.clicked -= onClick; }
            r.Text((count > 1 ? "double-clicked " : hold > 0 ? "long-pressed (" + hold + " ms) " : "clicked ") + Where(t) + " via " + m);
            if (fired) r.Text("the button's clicked handler ran");
            Feedback(r, seq, button == 0 && m == "event" && !fired && !EventStore.IsDisabled("ui.click") && (t.El != null || t.Go != null));
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
            int button = Btn(a), steps = Mathf.Clamp(a.Int("steps", 12), 1, 500), hold = Mathf.Clamp(a.Int("holdMs", 0), 0, MaxHoldMs);
            float stepWait = Mathf.Clamp(a.Int("durationMs", 300), 0, MaxHoldMs) / 1000f / steps;
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
            if (sv == null) throw new ToolException("no ScrollView at or above the target - target a ScrollView or an element inside one" + (Inj.Available ? ", or try mode=wheel method=input" : "") + ", or use ui_drag for swipe-style scrolling");
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
            if (a.Has("index"))
            {
                var list = t.El as BaseVerticalCollectionView ?? t.El.GetFirstAncestorOfType<BaseVerticalCollectionView>();
                if (list == null) throw new ToolException("index needs a ListView, TreeView or MultiColumnListView (or an element inside one)");
                int count = list.itemsSource != null ? list.itemsSource.Count : 0, i = a.Int("index");
                if (i < 0 || i >= count) throw new ToolException("index " + i + " is outside the list (" + count + " items)");
                list.ScrollToItem(i);
                yield return null; yield return null;
                var row = list.GetRootElementForIndex(i);
                r.Text("scrolled " + UiTools.Desc(list) + " to item " + i + " of " + count + (row != null ? "; row: " + UiTools.Line(row) : "; the row is not built yet, call ui_find in a moment"));
                yield break;
            }
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
            var cp = a.Bool("clear") && subject != null ? UiTools.Prop(subject.GetType(), "value") : null;
            if (cp != null)
            {
                // Number fields (IntegerField, FloatField...) cannot take "": select their text and delete it like a user would.
                if (cp.PropertyType == typeof(string)) SetProperty(subject, "value", new JValue(""));
                else if (UiTools.Prop(subject.GetType(), "textSelection")?.GetValue(subject) is ITextSelection sel) { sel.SelectAll(); SendKey(top, true, '\0', KeyCode.Backspace); }
                else throw new ToolException("clear works on text and number fields only; use ui_set_value for " + subject.GetType().Name);
                yield return null;
            }

            string before = Snapshot(subject), how = m;
            var vp = subject != null ? UiTools.Prop(subject.GetType(), "value") : null;
            bool str = vp != null && vp.PropertyType == typeof(string);
            string want = str && m == "event" && text.IndexOf('\n') < 0 ? ((string)vp.GetValue(subject) ?? "") + text : null;
            if (m == "set") SetProperty(subject, "value", new JValue((UiTools.ValueOf(subject) ?? "") + text));
            else
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (m == "input") Inj.SendText(c);
                    else
                    {
                        CaretToEnd(subject);
                        if (c == '\n') { SendKey(top, true, '\0', KeyCode.Return); SendKey(top, true, '\n', KeyCode.None); }
                        else SendKey(top, true, c, KeyCode.None);
                    }
                    // The text editor sees the inserted character only after the panel updates.
                    if (m != "input" || i % 6 == 5) yield return null;
                }
                yield return null; yield return null;
                string got = want != null ? (string)vp.GetValue(subject) : null;
                if (got != want)
                {
                    SetProperty(subject, "value", new JValue(want));
                    how = m + " produced '" + Util.OneLine(got, 80) + "' -> fell back to setting the value directly (Unity's text editor lags when the panel is not redrawn, e.g. batch mode)";
                }
                else if (str && text.Length > 0 && Snapshot(subject) == before)
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
            string m = a.Str("method", "auto"), action = a.Str("action", "press"), note = "";
            if (m != "auto" && m != "os") Method(a, r);
            if (m == "os" && !Os.Available) throw new ToolException("method=os works only in macOS and Windows game builds");
            int hold = Mathf.Clamp(a.Int("holdMs", 0), 0, MaxHoldMs);
            long seq = EventStore.LastSeq;
            SetMods(a);
            var code = m == "input" ? KeyCode.None : Code(key);
            if (m == "auto" || m == "event")
            {
                VisualElement top;
                var f = Focused(out top);
                // No UI Toolkit focus but a selected uGUI object: route the key there.
                if (f == null && action != "up" && Ugui.Key(code, _mods))
                {
                    _mods = EventModifiers.None;
                    yield return null;
                    r.Text("key " + key + " sent to the selected uGUI object " + Ugui.Desc(Ugui.Selected()) + (m == "auto" ? " (to reach gameplay code instead, use method=os)" : ""));
                    Feedback(r, seq, false);
                    yield break;
                }
                if (m == "auto") m = f != null ? "event" : Inj.Available ? "input" : Os.Available ? "os" : top != null ? "event" : null;
                if (m == null) throw new ToolException("nothing can take this key: no UI element is focused or selected (ui_focus one first), the Input System package is not active, and real key events are posted only by macOS and Windows game builds");
                if (m == "input") key = KeyName(code);
                if (m == "event")
                {
                    if (action != "up") { SendKey(top, true, '\0', code); Nav(top, f, code); }
                    if (action == "press") { if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else yield return null; }
                    if (action != "down") SendKey(top, false, '\0', code);
                }
            }
            if (m == "input")
            {
                if (action != "up") Inj.SendKey(key, true);
                if (action == "press") { if (hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f); else yield return null; }
                if (action != "down") Inj.SendKey(key, false);
            }
            else if (m == "os")
            {
                if (action != "up")
                {
                    Os.Send(code, true, _mods);
                    bool seen = false;
                    float until = Time.realtimeSinceStartup + 0.5f;
                    if (!LegacyInput) note = "; this game reads the Input System only, so Input.GetKey cannot confirm it - method=auto sends keys through the Input System";
                    else
                    {
                        while (!(seen = Held(code)) && Time.realtimeSinceStartup < until) yield return null;
                        note = seen ? "; the game's Input.GetKey saw it" : "; WARNING: the game's Input.GetKey did not see it - bring the game window to the front and retry";
                    }
                    if (action == "press" && hold > 0) yield return new WaitForSecondsRealtime(hold / 1000f);
                }
                if (action != "down") { Os.Send(code, false, _mods); yield return null; }
            }
            _mods = EventModifiers.None;
            yield return null;
            r.Text("key " + action + " " + key + " via " + m + note);
            Feedback(r, seq, false);
        }

        // ---------------- play ----------------

        sealed class Step
        {
            public string[] Hold, Tap, Keys;
            public int Mouse, Ms;
            public Vector2? Move;
            public Vector2 Look;
        }

        static Vector2? Vec(JToken t, string name)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            var arr = t as JArray;
            if (arr == null || arr.Count != 2) throw new ToolException(name + " must be [x, y]");
            return new Vector2(arr[0].ToObject<float>(), arr[1].ToObject<float>());
        }

        static string Read(string path)
        {
            try { return Util.OneLine(StateTools.Format(StateTools.Resolve(path), 0, 10, false), 200); }
            catch (Exception e) { return "<error: " + Util.OneLine(e.Message, 120) + ">"; }
        }

        static bool Matches(string path, string eq)
        {
            string v;
            try { v = StateTools.Format(StateTools.Resolve(path), 0, 10, false); } catch { return false; }
            return eq == null || string.Equals(v.Trim('"'), eq.Trim('"'), StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerator Play(JObject a, ToolResponse r)
        {
            if (!Inj.Available) throw new ToolException("play " + NeedInputSystem);
            var arr = a["steps"] as JArray;
            if (arr == null || arr.Count == 0) throw new ToolException("steps must be a non-empty array, e.g. [{\"hold\":[\"W\"],\"ms\":500}]");
            if (arr.Count > 100) throw new ToolException("at most 100 steps per play call");
            var observe = a.Strs("observe") ?? new string[0];
            var stop = a["stopWhen"] as JObject;
            string stopPath = stop != null ? stop.Str("path") : null, stopEq = stop != null ? stop.Str("equals") : null;
            if (stop != null && stopPath == null) throw new ToolException("stopWhen needs path, e.g. {\"path\":\"/Player:Health.hp\",\"equals\":\"0\"}");
            var steps = new List<Step>();
            int totalMs = 0;
            foreach (var tok in arr)
            {
                var o = tok as JObject ?? throw new ToolException("each step must be an object");
                var st = new Step { Hold = o.Strs("hold") ?? new string[0], Tap = o.Strs("tap") ?? new string[0], Keys = new string[0], Ms = Mathf.Max(0, o.Int("ms", 0)), Move = Vec(o["move"], "move"), Look = Vec(o["look"], "look") ?? Vector2.zero };
                foreach (var k in st.Hold) Inj.CheckKey(k);
                foreach (var k in st.Tap) Inj.CheckKey(k);
                if (o.Has("action")) Inj.ActionInput(o.Str("action"), out st.Keys, out st.Mouse);
                totalMs += st.Ms;
                steps.Add(st);
            }
            if (totalMs > 60000) throw new ToolException("steps add up to " + totalMs + " ms; at most 60000 ms per play call");
            _leaseEnd = Time.realtimeSinceStartup + totalMs / 1000f;

            long seq = EventStore.LastSeq;
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int mouse = 0, done = 0, f0 = Time.frameCount;
            bool stick = false;
            float start = Time.realtimeSinceStartup;
            string stopped = null;
            try
            {
                foreach (var st in steps)
                {
                    var want = new HashSet<string>(st.Hold, StringComparer.OrdinalIgnoreCase);
                    want.UnionWith(st.Keys);
                    want.UnionWith(st.Tap);
                    foreach (var k in new List<string>(held)) if (!want.Contains(k)) { Inj.SendKey(k, false); held.Remove(k); }
                    foreach (var k in want) if (!held.Contains(k) && !Inj.IsDown(k)) { held.Add(k); Inj.SendKey(k, true); }
                    if (st.Mouse != mouse) { mouse = st.Mouse; Inj.Look(Vector2.zero, mouse | _buttons); }
                    if (st.Move != null || stick) { Inj.Stick(st.Move ?? Vector2.zero); stick = true; }
                    float t0 = Time.realtimeSinceStartup;
                    int sf = Time.frameCount;
                    var sent = Vector2.zero;
                    bool first = true;
                    while (true)
                    {
                        if (st.Look != Vector2.zero)
                        {
                            var target = st.Look * (st.Ms > 0 ? Mathf.Clamp01((Time.realtimeSinceStartup - t0 + Time.unscaledDeltaTime) * 1000f / st.Ms) : 1f);
                            Inj.Look(target - sent, mouse | _buttons);
                            sent = target;
                        }
                        yield return null;
                        if (first)
                            foreach (var k in st.Tap)
                                if (Array.IndexOf(st.Hold, k) < 0 && Array.IndexOf(st.Keys, k) < 0 && held.Remove(k)) Inj.SendKey(k, false);
                        first = false;
                        if (stopPath != null && Matches(stopPath, stopEq)) stopped = "stopWhen " + stopPath + " = " + Read(stopPath);
                        else if (Time.realtimeSinceStartup - start > 60f) stopped = "time limit 60 s";
                        if (stopped != null || (Time.realtimeSinceStartup - t0) * 1000f >= st.Ms) break;
                    }
                    var line = "[" + done + "] " + Mathf.RoundToInt((Time.realtimeSinceStartup - t0) * 1000f) + "ms " + (Time.frameCount - sf) + "f";
                    foreach (var path in observe) line += "  " + path + "=" + Read(path);
                    r.Text(line);
                    done++;
                    if (stopped != null) break;
                }
            }
            finally
            {
                foreach (var k in held) Inj.SendKey(k, false);
                if (mouse != 0) Inj.Look(Vector2.zero, _buttons);
                if (stick) Inj.Stick(Vector2.zero);
            }
            var errs = EventStore.Query(seq, new[] { "log.error", "log.exception", "log.assert" }, null, 6, false);
            for (int i = 0; i < errs.Count && i < 5; i++) r.Text("  " + EventStore.Fmt(errs[i], false));
            if (errs.Count > 5) r.Text("  ... more errors: events_query sinceSeq=" + seq + " categories=[\"log\"]");
            r.Text("total: " + done + "/" + steps.Count + " steps, " + (Time.frameCount - f0) + " frames, " + Mathf.RoundToInt((Time.realtimeSinceStartup - start) * 1000f) + " ms"
                   + (stopped != null ? ", stopped by " + stopped : ""));
        }

        // ---------------- real key events for the old Input Manager ----------------

        /// <summary>Key events posted to this game's own window, the only input Input.GetKey reads.</summary>
        static class Os
        {
            static readonly bool Mac = Application.platform == RuntimePlatform.OSXPlayer, Win = Application.platform == RuntimePlatform.WindowsPlayer;
            public static readonly bool Available = Mac || Win;
            static readonly int Pid = System.Diagnostics.Process.GetCurrentProcess().Id;

            static char Char(KeyCode c, EventModifiers mods)
            {
                if (c >= KeyCode.A && c <= KeyCode.Z && (mods & EventModifiers.Shift) != 0) return (char)(c - 32);
                return c >= KeyCode.Space && c < KeyCode.Delete ? (char)c : '\0';
            }

            static Exception NoCode(KeyCode c) { return new ToolException("key " + c + " cannot be sent as a real key event; letters, digits, punctuation, Space, Return, Escape, Tab, Backspace, Delete, arrows, Home/End/PageUp/PageDown, Shift/Control/Alt and F1-F12 can"); }

            public static void Send(KeyCode c, bool down, EventModifiers mods)
            {
                if (Mac) { PostMac(c, down, mods); return; }
                var w = Window();
                if (w == IntPtr.Zero) throw new ToolException("the game window was not found");
                var with = new List<KeyCode>();
                if ((mods & EventModifiers.Shift) != 0) with.Add(KeyCode.LeftShift);
                if ((mods & EventModifiers.Control) != 0) with.Add(KeyCode.LeftControl);
                if ((mods & EventModifiers.Alt) != 0) with.Add(KeyCode.LeftAlt);
                int vk = WinKey(c);
                if (down) foreach (var x in with) PostWin(w, WinKey(x), x, true);
                PostWin(w, vk, c, down);
                if (!down) foreach (var x in with) PostWin(w, WinKey(x), x, false);
            }

            // macOS: virtual key codes of the ANSI layout, indexed by the KeyCode character.
            const string MacChars = "asdfhgzxcv\0bqweryt123465=97-80]ou[ip\0lj'k;\\,/nm.\t `";
            const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            [DllImport(CG)] static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
            [DllImport(CG)] static extern void CGEventSetType(IntPtr e, uint type);
            [DllImport(CG)] static extern void CGEventSetFlags(IntPtr e, ulong flags);
            [DllImport(CG)] static extern void CGEventKeyboardSetUnicodeString(IntPtr e, UIntPtr length, ref ushort s);
            [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern void CFRelease(IntPtr p);
            const string ObjC = "/usr/lib/libobjc.A.dylib";
            [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
            [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr arg);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void Send(IntPtr self, IntPtr sel, IntPtr arg, [MarshalAs(UnmanagedType.I1)] bool flag);

            static int MacKey(KeyCode c)
            {
                int i = c > KeyCode.None && (int)c < 128 ? MacChars.IndexOf((char)c) : -1;
                if (i >= 0) return i;
                if (c >= KeyCode.F1 && c <= KeyCode.F12) return new[] { 0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F }[c - KeyCode.F1];
                switch (c)
                {
                    case KeyCode.Return: return 0x24; case KeyCode.KeypadEnter: return 0x4C; case KeyCode.Escape: return 0x35;
                    case KeyCode.Backspace: return 0x33; case KeyCode.Delete: return 0x75;
                    case KeyCode.LeftArrow: return 0x7B; case KeyCode.RightArrow: return 0x7C; case KeyCode.DownArrow: return 0x7D; case KeyCode.UpArrow: return 0x7E;
                    case KeyCode.Home: return 0x73; case KeyCode.End: return 0x77; case KeyCode.PageUp: return 0x74; case KeyCode.PageDown: return 0x79;
                    case KeyCode.LeftShift: return 0x38; case KeyCode.RightShift: return 0x3C; case KeyCode.LeftControl: return 0x3B; case KeyCode.RightControl: return 0x3E;
                    case KeyCode.LeftAlt: return 0x3A; case KeyCode.RightAlt: return 0x3D; case KeyCode.LeftCommand: return 0x37; case KeyCode.RightCommand: return 0x36;
                }
                throw NoCode(c);
            }

            static ulong MacFlags(EventModifiers m)
            {
                return ((m & EventModifiers.Shift) != 0 ? 0x20000UL : 0) | ((m & EventModifiers.Control) != 0 ? 0x40000UL : 0)
                     | ((m & EventModifiers.Alt) != 0 ? 0x80000UL : 0) | ((m & EventModifiers.Command) != 0 ? 0x100000UL : 0);
            }

            static void PostMac(KeyCode c, bool down, EventModifiers mods)
            {
                var e = CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)MacKey(c), down);
                var own = c == KeyCode.LeftShift || c == KeyCode.RightShift ? EventModifiers.Shift : c == KeyCode.LeftControl || c == KeyCode.RightControl ? EventModifiers.Control
                    : c == KeyCode.LeftAlt || c == KeyCode.RightAlt ? EventModifiers.Alt : c == KeyCode.LeftCommand || c == KeyCode.RightCommand ? EventModifiers.Command : EventModifiers.None;
                // A modifier key reaches an app as a flags-changed event, not a key down/up.
                if (own != EventModifiers.None) CGEventSetType(e, 12);
                CGEventSetFlags(e, MacFlags(down ? mods | own : mods));
                // The real layout may not be Latin; give the character the agent asked for.
                ushort ch = Char(c, mods);
                if (ch != 0) CGEventKeyboardSetUnicodeString(e, (UIntPtr)1, ref ch);
                // Into the game's own event queue: unlike posting through the window server, this needs no Accessibility permission and works in the background.
                var ns = Send(objc_getClass("NSEvent"), sel_registerName("eventWithCGEvent:"), e);
                Send(Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication")), sel_registerName("postEvent:atStart:"), ns, false);
                CFRelease(e);
            }

            // Windows: WM_KEYDOWN / WM_KEYUP to the game window.
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w, out int pid);
            [DllImport("user32.dll")] static extern bool PostMessage(IntPtr w, uint msg, IntPtr wp, IntPtr lp);
            [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);

            static IntPtr Window()
            {
                for (var w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "UnityWndClass", null); w != IntPtr.Zero; w = FindWindowEx(IntPtr.Zero, w, "UnityWndClass", null))
                {
                    int pid;
                    GetWindowThreadProcessId(w, out pid);
                    if (pid == Pid) return w;
                }
                return IntPtr.Zero;
            }

            static int WinKey(KeyCode c)
            {
                if (c >= KeyCode.A && c <= KeyCode.Z) return c - KeyCode.A + 'A';
                if (c >= KeyCode.Alpha0 && c <= KeyCode.Alpha9 || c == KeyCode.Space || c == KeyCode.Return || c == KeyCode.Tab || c == KeyCode.Backspace || c == KeyCode.Escape) return (int)c;
                if (c >= KeyCode.F1 && c <= KeyCode.F12) return 0x70 + (c - KeyCode.F1);
                switch (c)
                {
                    case KeyCode.KeypadEnter: return 0x0D; case KeyCode.Delete: return 0x2E;
                    case KeyCode.LeftArrow: return 0x25; case KeyCode.UpArrow: return 0x26; case KeyCode.RightArrow: return 0x27; case KeyCode.DownArrow: return 0x28;
                    case KeyCode.Home: return 0x24; case KeyCode.End: return 0x23; case KeyCode.PageUp: return 0x21; case KeyCode.PageDown: return 0x22;
                    case KeyCode.LeftShift: return 0xA0; case KeyCode.RightShift: return 0xA1; case KeyCode.LeftControl: return 0xA2;
                    case KeyCode.RightControl: return 0xA3; case KeyCode.LeftAlt: return 0xA4; case KeyCode.RightAlt: return 0xA5;
                    case KeyCode.Semicolon: return 0xBA; case KeyCode.Equals: return 0xBB; case KeyCode.Comma: return 0xBC; case KeyCode.Minus: return 0xBD;
                    case KeyCode.Period: return 0xBE; case KeyCode.Slash: return 0xBF; case KeyCode.BackQuote: return 0xC0;
                    case KeyCode.LeftBracket: return 0xDB; case KeyCode.Backslash: return 0xDC; case KeyCode.RightBracket: return 0xDD; case KeyCode.Quote: return 0xDE;
                }
                throw NoCode(c);
            }

            static void PostWin(IntPtr w, int vk, KeyCode c, bool down)
            {
                long scan = MapVirtualKey((uint)vk, 0);
                bool ext = vk >= 0x21 && vk <= 0x2E || c == KeyCode.KeypadEnter || vk == 0xA3 || vk == 0xA5;
                long lp = 1 | scan << 16 | (ext ? 1L << 24 : 0) | (down ? 0 : 0xC0000000L);
                PostMessage(w, down ? 0x100u : 0x101u, (IntPtr)vk, (IntPtr)lp);
            }
        }

        // ---------------- Input System injection ----------------

#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
        static class Inj
        {
            public static readonly bool Available = true;
            static Mouse _mouse;
            static Keyboard _keyboard;
            static Gamepad _pad;
            static readonly HashSet<Key> Held = new HashSet<Key>();

            public static int Sent;

            public static void Ensure()
            {
                Sent++;
                if (_mouse == null || !_mouse.added)
                {
                    // Keep receiving injected input while the game window is in the background.
                    InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
                    InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
                    _mouse = InputSystem.AddDevice<Mouse>("UDriverMouse");
                    _keyboard = InputSystem.AddDevice<Keyboard>("UDriverKeyboard");
                }
                // Devices added while the window was unfocused start disabled.
                foreach (var d in new InputDevice[] { _mouse, _keyboard, _pad })
                    if (d != null && d.added && !d.enabled) InputSystem.EnableDevice(d);
            }

            public static void SendMouse(Vector2 screenTopLeft, int buttons, Vector2 scroll)
            {
                Ensure();
                Queue(new Vector2(screenTopLeft.x, Screen.height - screenTopLeft.y), buttons, scroll, Vector2.zero);
            }

            public static void Look(Vector2 delta, int buttons)
            {
                Ensure();
                Queue(_mouse.position.ReadValue(), buttons, Vector2.zero, delta);
            }

            static void Queue(Vector2 position, int buttons, Vector2 scroll, Vector2 delta)
            {
                var st = new MouseState { position = position, scroll = scroll, delta = delta };
                // Fully qualified: UnityEngine.UIElements also declares a MouseButton enum.
                st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, (buttons & 1) != 0)
                       .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Right, (buttons & 2) != 0)
                       .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Middle, (buttons & 4) != 0);
                InputSystem.QueueStateEvent(_mouse, st);
            }

            static Key Parse(string name)
            {
                Key k;
                if (!Enum.TryParse(name, true, out k) || k == Key.None)
                    throw new ToolException("unknown key '" + name + "'. With method=input use Input System Key names: Enter, Escape, Tab, Space, Backspace, Delete, LeftArrow, A, Digit1, F5...");
                return k;
            }

            public static void CheckKey(string name) { Parse(name); }

            public static bool IsDown(string name) { return Held.Contains(Parse(name)); }

            public static void SendKey(string name, bool down)
            {
                Ensure();
                var k = Parse(name);
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

            public static void Stick(Vector2 v)
            {
                Ensure();
                if (_pad == null || !_pad.added) { _pad = InputSystem.AddDevice<Gamepad>("UDriverGamepad"); Ensure(); }
                InputSystem.QueueStateEvent(_pad, new GamepadState { leftStick = v });
            }

            static List<InputAction> Actions()
            {
                var list = new List<InputAction>();
                foreach (var pi in PlayerInput.all) if (pi.actions != null) foreach (var act in pi.actions) if (!list.Contains(act)) list.Add(act);
                foreach (var act in InputSystem.ListEnabledActions()) if (!list.Contains(act)) list.Add(act);
                return list;
            }

            public static void ActionInput(string name, out string[] keys, out int mouse)
            {
                Ensure();
                InputAction act = null;
                foreach (var x in Actions())
                    if ((string.Equals(x.name, name, StringComparison.OrdinalIgnoreCase) || string.Equals((x.actionMap != null ? x.actionMap.name + "/" : "") + x.name, name, StringComparison.OrdinalIgnoreCase))
                        && (act == null || x.enabled && !act.enabled)) act = x;
                if (act == null) throw new ToolException("no Input System action '" + name + "' among enabled actions and PlayerInput assets - list them with input_actions");
                foreach (var b in act.bindings)
                {
                    if (b.isComposite || string.IsNullOrEmpty(b.effectivePath)) continue;
                    var key = InputControlPath.TryFindControl(_keyboard, b.effectivePath) as KeyControl;
                    if (key != null) { keys = new[] { key.keyCode.ToString() }; mouse = 0; return; }
                    var c = InputControlPath.TryFindControl(_mouse, b.effectivePath);
                    mouse = c == null ? 0 : c == _mouse.leftButton ? 1 : c == _mouse.rightButton ? 2 : c == _mouse.middleButton ? 4 : 0;
                    if (mouse != 0) { keys = new string[0]; return; }
                }
                throw new ToolException("action '" + act.name + "' has no keyboard key or mouse button binding - use hold/tap with a key, move or look");
            }

            public static void Describe(ToolResponse r)
            {
                foreach (var pi in PlayerInput.all)
                {
                    var devs = new List<string>();
                    foreach (var d in pi.devices) devs.Add(d.name);
                    r.Text("PlayerInput " + StateTools.PathOf(pi.gameObject) + "  map: " + (pi.currentActionMap != null ? pi.currentActionMap.name : "-")
                           + "  scheme: " + (pi.currentControlScheme ?? "-") + "  devices: " + string.Join(", ", devs));
                }
                var maps = new List<string>();
                var byMap = new Dictionary<string, List<InputAction>>();
                foreach (var act in Actions())
                {
                    string m = act.actionMap != null ? act.actionMap.name : "(no map)";
                    if (!byMap.ContainsKey(m)) { maps.Add(m); byMap[m] = new List<InputAction>(); }
                    byMap[m].Add(act);
                }
                if (maps.Count == 0) { r.Text("no enabled Input System actions and no PlayerInput in the loaded scenes"); return; }
                foreach (var m in maps)
                {
                    r.Text(m + ":");
                    foreach (var act in byMap[m])
                    {
                        var sb = new StringBuilder();
                        foreach (var b in act.bindings)
                        {
                            if (b.isPartOfComposite) sb.Append(' ').Append(b.name).Append('=').Append(b.effectivePath);
                            else sb.Append(sb.Length > 0 ? " | " : "").Append(b.effectivePath).Append(b.isComposite ? ":" : "");
                        }
                        object v = null;
                        if (act.enabled) { try { v = act.ReadValueAsObject(); } catch { } }
                        r.Text("  " + act.name + " (" + act.type + (string.IsNullOrEmpty(act.expectedControlType) ? "" : " " + act.expectedControlType) + ")"
                               + (act.enabled ? " = " + (v ?? "-") : " [disabled]") + "  " + sb);
                    }
                }
            }
        }
#else
        static class Inj
        {
            public static readonly bool Available = false;
            public static readonly int Sent = 0;
            public static void Ensure() { }
            public static void SendMouse(Vector2 screenTopLeft, int buttons, Vector2 scroll) { }
            public static void SendKey(string name, bool down) { }
            public static void SendText(char c) { }
            public static void Look(Vector2 delta, int buttons) { }
            public static void Stick(Vector2 v) { }
            public static void CheckKey(string name) { }
            public static bool IsDown(string name) { return false; }
            public static void ActionInput(string name, out string[] keys, out int mouse) { keys = new string[0]; mouse = 0; }
            public static void Describe(ToolResponse r) { throw new ToolException("input_actions " + NeedInputSystem); }
        }
#endif
    }
}

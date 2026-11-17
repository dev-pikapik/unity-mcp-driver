using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UDRV_UGUI
using UnityEngine.EventSystems;
using UnityEngine.UI;
#endif

namespace UDriver
{
    /// <summary>uGUI (Canvas) support. Element ids are >= IdBase so they never collide with UI Toolkit ids.</summary>
    public static class Ugui
    {
        public const int IdBase = 1000000;

#if UDRV_UGUI
        public static readonly bool Available = true;

        static readonly Dictionary<GameObject, int> IdOf = new Dictionary<GameObject, int>();
        static readonly Dictionary<int, GameObject> ById = new Dictionary<int, GameObject>();
        static readonly Vector3[] Corners = new Vector3[4];
        static int _next = IdBase;
        static PointerEventData _ped;   // active press, if any
        static GameObject _hover;

        public static bool Pressed { get { return _ped != null; } }

        // ---------------- ids & facts ----------------

        public static int Id(GameObject go)
        {
            int id;
            if (IdOf.TryGetValue(go, out id)) return id;
            id = ++_next; IdOf[go] = id; ById[id] = go;
            return id;
        }

        public static GameObject Get(int id)
        {
            GameObject go;
            return ById.TryGetValue(id, out go) && go != null && go.activeInHierarchy ? go : null;
        }

        static List<Canvas> Canvases()
        {
            var list = new List<Canvas>();
            foreach (var c in Util.FindAll<Canvas>())
                if (c.isRootCanvas && c.isActiveAndEnabled) list.Add(c);
            list.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
            return list;
        }

        public static Rect ScreenRect(GameObject go)
        {
            var rt = go.transform as RectTransform;
            if (rt == null) return new Rect(0, 0, 0, 0);
            var canvas = go.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            Camera cam = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            rt.GetWorldCorners(Corners);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, Corners[i]);
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            return new Rect(x0, Screen.height - y1, x1 - x0, y1 - y0);
        }

        static string StrProp(Component c, string name)
        {
            var p = UiTools.Prop(c.GetType(), name);
            if (p == null || !p.CanRead) return null;
            try { var v = p.GetValue(c); return v == null ? null : v.ToString(); } catch { return null; }
        }

        /// <summary>Text of a Text / TMP_Text component on this object (not input fields).</summary>
        static string LabelOn(GameObject go)
        {
            foreach (var g in go.GetComponents<Graphic>())
            {
                var p = UiTools.Prop(g.GetType(), "text");
                if (p != null && p.PropertyType == typeof(string)) return StrProp(g, "text") ?? "";
            }
            return null;
        }

        static bool Clickable(GameObject go) { return go.GetComponent<IPointerClickHandler>() != null || go.GetComponent<IPointerDownHandler>() != null || go.GetComponent<IDragHandler>() != null; }

        public static bool Interactive(GameObject go)
        {
            var sel = go.GetComponent<Selectable>();
            return sel != null ? sel.IsInteractable() : Clickable(go);
        }

        static bool IsElement(GameObject go)
        {
            return go.GetComponent<Selectable>() != null || go.GetComponent<ScrollRect>() != null || Clickable(go) || !string.IsNullOrEmpty(LabelOn(go));
        }

        public static string TextOf(GameObject go)
        {
            string own = LabelOn(go);
            if (!string.IsNullOrEmpty(own)) return own;
            if (go.GetComponent<Selectable>() == null && !Clickable(go)) return "";
            var sb = new StringBuilder();
            foreach (var g in go.GetComponentsInChildren<Graphic>(false))
            {
                if (g.gameObject == go) continue;
                string t = LabelOn(g.gameObject);
                if (string.IsNullOrEmpty(t)) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(t);
                if (sb.Length > 80) break;
            }
            return sb.ToString();
        }

        static string ValueOf(GameObject go)
        {
            var sel = go.GetComponent<Selectable>();
            if (sel == null) return null;
            if (sel.GetType().Name.Contains("InputField")) return StrProp(sel, "text");
            return StrProp(sel, "isOn") ?? StrProp(sel, "value");
        }

        static string Kind(GameObject go)
        {
            var sel = go.GetComponent<Selectable>();
            if (sel != null) return sel.GetType().Name;
            if (go.GetComponent<ScrollRect>() != null) return "ScrollRect";
            foreach (var g in go.GetComponents<Graphic>()) if (UiTools.Prop(g.GetType(), "text") != null) return g.GetType().Name;
            var h = go.GetComponent<IPointerClickHandler>() as Component;
            return h != null ? h.GetType().Name : "UIElement";
        }

        public static string Desc(GameObject go)
        {
            if (go == null) return "(none)";
            string t = TextOf(go);
            return "#" + Id(go) + " " + Kind(go) + " \"" + go.name + "\"" + (t.Length > 0 ? " \"" + Util.OneLine(t, 40) + "\"" : "") + " (uGUI)";
        }

        public static string Line(GameObject go)
        {
            var sb = new StringBuilder();
            sb.Append('#').Append(Id(go)).Append(' ').Append(Kind(go)).Append(" \"").Append(go.name).Append('"');
            string t = TextOf(go);
            if (t.Length > 0) sb.Append(" \"").Append(Util.OneLine(t, 60)).Append('"');
            string v = ValueOf(go);
            if (v != null) sb.Append(" =").Append(Util.OneLine(v, 60));
            var r = ScreenRect(go);
            sb.Append(" [").Append(Mathf.RoundToInt(r.x)).Append(',').Append(Mathf.RoundToInt(r.y)).Append(',').Append(Mathf.RoundToInt(r.width)).Append(',').Append(Mathf.RoundToInt(r.height)).Append(']');
            var sel = go.GetComponent<Selectable>();
            if (Interactive(go)) sb.Append(" *");
            if (sel != null && !sel.IsInteractable()) sb.Append(" disabled");
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject == go) sb.Append(" focused");
            return sb.ToString();
        }

        // ---------------- enumeration ----------------

        static void Collect(Transform t, List<GameObject> into)
        {
            if (!t.gameObject.activeInHierarchy) return;
            if (IsElement(t.gameObject)) into.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
        }

        public static List<GameObject> All()
        {
            var list = new List<GameObject>();
            foreach (var c in Canvases()) Collect(c.transform, list);
            return list;
        }

        static void Walk(Transform t, int indent, StringBuilder sb, bool interactiveOnly, int max, ref int count, ref int skipped)
        {
            var go = t.gameObject;
            if (!go.activeInHierarchy) return;
            bool print = interactiveOnly ? Interactive(go) : IsElement(go);
            if (print)
            {
                if (count >= max) skipped++;
                else { sb.Append(' ', indent * 2).AppendLine(Line(go)); count++; }
            }
            int next = print && !interactiveOnly ? indent + 1 : indent;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), next, sb, interactiveOnly, max, ref count, ref skipped);
        }

        /// <summary>Appends uGUI canvases (or the subtree of 'root') to a ui_tree dump. Returns the number of canvases.</summary>
        public static int Tree(StringBuilder sb, GameObject root, bool interactiveOnly, int max, ref int count, ref int skipped)
        {
            if (root != null) { Walk(root.transform, 0, sb, interactiveOnly, max, ref count, ref skipped); return 1; }
            var canvases = Canvases();
            foreach (var c in canvases)
            {
                sb.AppendLine("canvas \"" + c.name + "\" (uGUI, " + c.renderMode + ", order " + c.sortingOrder + ")");
                Walk(c.transform, 1, sb, interactiveOnly, max, ref count, ref skipped);
            }
            return canvases.Count;
        }

        public static GameObject FindFirst(string name, string text)
        {
            GameObject partial = null;
            foreach (var go in All())
            {
                if (name != null)
                {
                    if (text != null && !Util.Contains(TextOf(go), text)) continue;
                    if (go.name == name) return go;
                    if (partial == null && Util.Contains(go.name, name)) partial = go;
                }
                else if (text != null)
                {
                    string t = TextOf(go);
                    if (t.Length == 0) continue;
                    // Prefer the clickable owner of a label over the label itself.
                    if (string.Equals(t, text, StringComparison.OrdinalIgnoreCase)) { if (Interactive(go)) return go; if (partial == null) partial = go; }
                    else if (partial == null && Util.Contains(t, text)) partial = go;
                }
            }
            return partial;
        }

        public static void Find(string name, string text, string type, List<string> lines)
        {
            foreach (var go in All())
            {
                if (name != null && !Util.Contains(go.name, name)) continue;
                if (type != null && !string.Equals(Kind(go), type, StringComparison.OrdinalIgnoreCase)) continue;
                if (text != null && !Util.Contains(TextOf(go), text) && !Util.Contains(ValueOf(go), text)) continue;
                lines.Add(Line(go) + "  path=" + StateTools.PathOf(go));
            }
        }

        // ---------------- raycasting ----------------

        static EventSystem Es()
        {
            var es = EventSystem.current;
            if (es == null) throw new ToolException("the scene has no active EventSystem, so uGUI cannot receive events");
            return es;
        }

        static Vector2 Bl(Vector2 screenTopLeft) { return new Vector2(screenTopLeft.x, Screen.height - screenTopLeft.y); }

        static GameObject Cast(PointerEventData ped, out RaycastResult first)
        {
            var results = new List<RaycastResult>();
            Es().RaycastAll(ped, results);
            first = results.Count > 0 ? results[0] : default(RaycastResult);
            return results.Count > 0 ? results[0].gameObject : null;
        }

        /// <summary>Topmost uGUI object at a screen point (top-left origin), or null.</summary>
        public static GameObject Raycast(Vector2 screenTopLeft)
        {
            if (EventSystem.current == null) return null;
            RaycastResult rr;
            return Cast(new PointerEventData(EventSystem.current) { position = Bl(screenTopLeft) }, out rr);
        }

        public static List<string> RaycastAll(Vector2 screenTopLeft, int limit)
        {
            var lines = new List<string>();
            if (EventSystem.current == null) return lines;
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = Bl(screenTopLeft) }, results);
            for (int i = 0; i < results.Count && i < limit; i++) lines.Add(Line(results[i].gameObject) + "  path=" + StateTools.PathOf(results[i].gameObject));
            return lines;
        }

        public static string Warnings(GameObject go)
        {
            var sb = new StringBuilder();
            var sel = go.GetComponent<Selectable>();
            if (sel != null && !sel.IsInteractable()) sb.AppendLine("WARNING: target is not interactable (disabled).");
            var r = ScreenRect(go);
            if (r.width < 1 || r.height < 1) sb.AppendLine("WARNING: target has zero size.");
            if (EventSystem.current == null) sb.AppendLine("WARNING: no EventSystem in the scene.");
            else
            {
                var hit = Raycast(r.center);
                if (hit == null) sb.AppendLine("WARNING: nothing is raycast-hit at the target point (off-screen, masked, or raycastTarget off). Try ui_scroll_to.");
                else if (!hit.transform.IsChildOf(go.transform) && !go.transform.IsChildOf(hit.transform)) sb.AppendLine("WARNING: target is covered - the pointer lands on " + Desc(hit) + " instead.");
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }

        // ---------------- pointer actions (mirror what StandaloneInputModule does) ----------------

        public static void Move(Vector2 screenTopLeft)
        {
            var es = Es();
            var pos = Bl(screenTopLeft);
            var ped = _ped ?? new PointerEventData(es);
            ped.delta = pos - ped.position;
            ped.position = pos;
            RaycastResult rr;
            var hit = Cast(ped, out rr);
            ped.pointerCurrentRaycast = rr;
            if (hit != _hover)
            {
                if (_hover != null) ExecuteEvents.ExecuteHierarchy(_hover, ped, ExecuteEvents.pointerExitHandler);
                if (hit != null) ExecuteEvents.ExecuteHierarchy(hit, ped, ExecuteEvents.pointerEnterHandler);
                _hover = hit;
            }
            if (_ped == null || _ped.pointerDrag == null) return;
            if (!_ped.dragging)
            {
                ExecuteEvents.Execute(_ped.pointerDrag, _ped, ExecuteEvents.beginDragHandler);
                _ped.dragging = true;
                if (_ped.pointerPress != null && _ped.pointerPress != _ped.pointerDrag)
                {
                    // A drag started on a different object than the pressed one cancels the click.
                    ExecuteEvents.Execute(_ped.pointerPress, _ped, ExecuteEvents.pointerUpHandler);
                    _ped.eligibleForClick = false; _ped.pointerPress = null; _ped.rawPointerPress = null;
                }
            }
            ExecuteEvents.Execute(_ped.pointerDrag, _ped, ExecuteEvents.dragHandler);
        }

        public static void Down(Vector2 screenTopLeft, int button, int clicks)
        {
            var es = Es();
            Move(screenTopLeft);
            var pos = Bl(screenTopLeft);
            var ped = new PointerEventData(es)
            {
                position = pos, pressPosition = pos, button = (PointerEventData.InputButton)button, clickCount = clicks,
                pointerId = -1 - button, eligibleForClick = true, useDragThreshold = true
            };
            RaycastResult rr;
            var hit = Cast(ped, out rr);
            ped.pointerCurrentRaycast = rr; ped.pointerPressRaycast = rr;
            _ped = ped;
            var selectable = hit != null ? ExecuteEvents.GetEventHandler<ISelectHandler>(hit) : null;
            if (selectable != es.currentSelectedGameObject) es.SetSelectedGameObject(selectable, ped);
            if (hit == null) return;
            var pressed = ExecuteEvents.ExecuteHierarchy(hit, ped, ExecuteEvents.pointerDownHandler);
            if (pressed == null) pressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
            ped.pointerPress = pressed; ped.rawPointerPress = hit;
            ped.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(hit);
            if (ped.pointerDrag != null) ExecuteEvents.Execute(ped.pointerDrag, ped, ExecuteEvents.initializePotentialDrag);
            EventStore.Emit("ui.down", "down " + Desc(pressed ?? hit) + " button=" + button);
        }

        public static void Up(Vector2 screenTopLeft, int button)
        {
            if (_ped == null) return;
            var ped = _ped;
            ped.position = Bl(screenTopLeft);
            RaycastResult rr;
            var hit = Cast(ped, out rr);
            ped.pointerCurrentRaycast = rr;
            if (ped.pointerPress != null) ExecuteEvents.Execute(ped.pointerPress, ped, ExecuteEvents.pointerUpHandler);
            var clickHandler = hit != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) : null;
            if (ped.pointerPress != null && ped.pointerPress == clickHandler && ped.eligibleForClick)
            {
                ExecuteEvents.Execute(ped.pointerPress, ped, ExecuteEvents.pointerClickHandler);
                EventStore.Emit("ui.click", "click " + Desc(ped.pointerPress) + " button=" + button);
            }
            else if (ped.pointerDrag != null && ped.dragging && hit != null) ExecuteEvents.ExecuteHierarchy(hit, ped, ExecuteEvents.dropHandler);
            if (ped.pointerDrag != null && ped.dragging) ExecuteEvents.Execute(ped.pointerDrag, ped, ExecuteEvents.endDragHandler);
            EventStore.Emit("ui.up", "up " + Desc(hit) + " button=" + button);
            _ped = null;
        }

        public static void Wheel(Vector2 screenTopLeft, float dx, float dy)
        {
            var ped = new PointerEventData(Es()) { position = Bl(screenTopLeft), scrollDelta = new Vector2(-dx, -dy) };
            RaycastResult rr;
            var hit = Cast(ped, out rr);
            ped.pointerCurrentRaycast = rr;
            if (hit != null) ExecuteEvents.ExecuteHierarchy(hit, ped, ExecuteEvents.scrollHandler);
        }

        // ---------------- higher-level actions ----------------

        public static string ScrollBy(GameObject go, float dx, float dy)
        {
            var sr = go.GetComponentInParent<ScrollRect>();
            if (sr == null || sr.content == null) throw new ToolException("no ScrollRect at or above the target - try mode=wheel or ui_drag");
            var before = sr.content.anchoredPosition;
            sr.content.anchoredPosition = before + new Vector2(-dx, dy);
            sr.normalizedPosition = new Vector2(Mathf.Clamp01(sr.normalizedPosition.x), Mathf.Clamp01(sr.normalizedPosition.y));
            return "scrolled " + Desc(sr.gameObject) + ": content " + before + " -> " + sr.content.anchoredPosition + ", normalized " + sr.normalizedPosition;
        }

        public static string ScrollTo(GameObject go)
        {
            var rt = go.transform as RectTransform;
            var sr = go.transform.parent != null ? go.transform.parent.GetComponentInParent<ScrollRect>() : null;
            if (sr == null || sr.content == null || rt == null) return "element is not inside a ScrollRect";
            Canvas.ForceUpdateCanvases();
            var viewport = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
            Vector2 childInView = viewport.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
            Vector2 diff = viewport.rect.center - childInView;
            if (!sr.horizontal) diff.x = 0;
            if (!sr.vertical) diff.y = 0;
            sr.content.anchoredPosition += diff;
            sr.normalizedPosition = new Vector2(Mathf.Clamp01(sr.normalizedPosition.x), Mathf.Clamp01(sr.normalizedPosition.y));
            Canvas.ForceUpdateCanvases();
            return "scrolled " + Desc(sr.gameObject) + "; now: " + Line(go);
        }

        static Component ValueOwner(GameObject go, string prop, out PropertyInfo p)
        {
            p = null;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform || c is CanvasRenderer) continue;
                foreach (var name in prop != null ? new[] { prop } : c.GetType().Name.Contains("InputField") ? new[] { "text" } : new[] { "isOn", "value" })
                {
                    var x = UiTools.Prop(c.GetType(), name);
                    if (x != null && x.CanWrite) { p = x; return c; }
                }
            }
            return null;
        }

        public static string SetValue(GameObject go, string prop, JToken value)
        {
            PropertyInfo p;
            var c = ValueOwner(go, prop, out p);
            if (c == null) throw new ToolException("no component on '" + go.name + "' has a writable '" + (prop ?? "text / isOn / value") + "' property");
            object v;
            try { v = Util.To(value, p.PropertyType); }
            catch (ToolException) { throw; }
            catch (Exception e) { throw new ToolException("cannot convert value to " + p.PropertyType.Name + ": " + e.Message); }
            p.SetValue(c, v);
            EventStore.Emit("ui.change", "change " + Desc(go) + " " + c.GetType().Name + "." + p.Name + " -> " + Util.OneLine(Convert.ToString(p.GetValue(c)), 80));
            return c.GetType().Name + "." + p.Name + " = " + Convert.ToString(p.GetValue(c));
        }

        public static GameObject Selected() { return EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null; }

        public static void Focus(GameObject go) { Es().SetSelectedGameObject(go); }

        public static string TypeText(GameObject go, string text, bool clear, bool submit)
        {
            PropertyInfo p;
            var c = ValueOwner(go, "text", out p);
            if (c == null || p.PropertyType != typeof(string)) throw new ToolException("'" + go.name + "' is not a text input (no writable string 'text')");
            Focus(go);
            string now = (clear ? "" : (string)p.GetValue(c) ?? "") + text;
            p.SetValue(c, now);
            EventStore.Emit("ui.change", "change " + Desc(go) + " text -> " + Util.OneLine(now, 80));
            if (submit)
            {
                // InputField / TMP_InputField expose onSubmit / onEndEdit UnityEvent<string>.
                foreach (var evName in new[] { "onSubmit", "onEndEdit" })
                {
                    var ep = UiTools.Prop(c.GetType(), evName);
                    var ev = ep != null ? ep.GetValue(c) : null;
                    var inv = ev != null ? ev.GetType().GetMethod("Invoke", new[] { typeof(string) }) : null;
                    if (inv != null) inv.Invoke(ev, new object[] { now });
                }
                ExecuteEvents.Execute(go, new BaseEventData(Es()), ExecuteEvents.submitHandler);
                EventStore.Emit("ui.submit", "submit " + Desc(go));
            }
            return "text = " + now;
        }

        /// <summary>Submit / cancel / navigation keys for the selected uGUI object. Returns false if nothing is selected.</summary>
        public static bool Key(KeyCode code)
        {
            var go = Selected();
            if (go == null) return false;
            var es = Es();
            if (code == KeyCode.Return || code == KeyCode.KeypadEnter) ExecuteEvents.Execute(go, new BaseEventData(es), ExecuteEvents.submitHandler);
            else if (code == KeyCode.Escape) ExecuteEvents.Execute(go, new BaseEventData(es), ExecuteEvents.cancelHandler);
            else
            {
                MoveDirection dir = code == KeyCode.UpArrow ? MoveDirection.Up : code == KeyCode.DownArrow ? MoveDirection.Down : code == KeyCode.LeftArrow ? MoveDirection.Left : code == KeyCode.RightArrow ? MoveDirection.Right : MoveDirection.None;
                if (dir == MoveDirection.None) return false;
                ExecuteEvents.Execute(go, new AxisEventData(es) { moveDir = dir }, ExecuteEvents.moveHandler);
            }
            return true;
        }

        public static void Inspect(GameObject go, ToolResponse r)
        {
            r.Text(Line(go));
            r.Text("path: " + StateTools.PathOf(go) + " | layer: " + LayerMask.LayerToName(go.layer));
            var comps = new List<string>();
            foreach (var c in go.GetComponents<Component>()) if (c != null) comps.Add(c.GetType().Name);
            r.Text("components: " + string.Join(", ", comps) + " (read with state_get '" + StateTools.PathOf(go) + ":Component.field')");
            var rt = go.transform as RectTransform;
            if (rt != null) r.Text("anchoredPosition=" + rt.anchoredPosition + " sizeDelta=" + rt.sizeDelta + " anchorMin=" + rt.anchorMin + " anchorMax=" + rt.anchorMax + " pivot=" + rt.pivot + " scale=" + rt.localScale);
            foreach (var g in go.GetComponents<Graphic>()) r.Text(g.GetType().Name + ": color=" + g.color + " raycastTarget=" + g.raycastTarget + " enabled=" + g.enabled);
            var cg = go.GetComponentInParent<CanvasGroup>();
            if (cg != null) r.Text("CanvasGroup on '" + cg.name + "': alpha=" + cg.alpha + " interactable=" + cg.interactable + " blocksRaycasts=" + cg.blocksRaycasts);
            string w = Warnings(go);
            r.Text(w ?? "hit-test at center: this element (or its child) receives the pointer");
        }

        /// <summary>Builds a temporary canvas + button and checks that a synthesized click reaches it.</summary>
        public static GameObject SelfTestCreate(Action onClick)
        {
            var root = new GameObject("[UDriver SelfTest Canvas]");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            root.AddComponent<GraphicRaycaster>();
            if (EventSystem.current == null) root.AddComponent<EventSystem>();
            var go = new GameObject("udriver-selftest-ugui-btn");
            go.transform.SetParent(root.transform, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.5f, 0.9f, 1f);
            var btn = go.AddComponent<UnityEngine.UI.Button>();
            btn.onClick.AddListener(() => onClick());
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-10, -10); rt.sizeDelta = new Vector2(160, 40);
            return root;
        }
#else
        public static readonly bool Available = false;
        public static bool Pressed { get { return false; } }
        public static int Id(GameObject go) { return 0; }
        public static GameObject Get(int id) { return null; }
        public static Rect ScreenRect(GameObject go) { return new Rect(0, 0, 0, 0); }
        public static bool Interactive(GameObject go) { return false; }
        public static string Desc(GameObject go) { return "(uGUI not available)"; }
        public static string Line(GameObject go) { return ""; }
        public static List<GameObject> All() { return new List<GameObject>(); }
        public static int Tree(StringBuilder sb, GameObject root, bool interactiveOnly, int max, ref int count, ref int skipped) { return 0; }
        public static GameObject FindFirst(string name, string text) { return null; }
        public static void Find(string name, string text, string type, List<string> lines) { }
        public static GameObject Raycast(Vector2 screenTopLeft) { return null; }
        public static List<string> RaycastAll(Vector2 screenTopLeft, int limit) { return new List<string>(); }
        public static string Warnings(GameObject go) { return null; }
        public static void Move(Vector2 screenTopLeft) { }
        public static void Down(Vector2 screenTopLeft, int button, int clicks) { }
        public static void Up(Vector2 screenTopLeft, int button) { }
        public static void Wheel(Vector2 screenTopLeft, float dx, float dy) { }
        public static string ScrollBy(GameObject go, float dx, float dy) { return ""; }
        public static string ScrollTo(GameObject go) { return ""; }
        public static string SetValue(GameObject go, string prop, JToken value) { return ""; }
        public static GameObject Selected() { return null; }
        public static void Focus(GameObject go) { }
        public static string TypeText(GameObject go, string text, bool clear, bool submit) { return ""; }
        public static bool Key(KeyCode code) { return false; }
        public static void Inspect(GameObject go, ToolResponse r) { }
        public static GameObject SelfTestCreate(Action onClick) { return null; }
#endif
    }
}

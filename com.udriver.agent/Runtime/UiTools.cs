using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UDriver
{
    public static class UiTools
    {
        /// <summary>A resolved action target. Screen = pixels, top-left origin. Panel = UI Toolkit panel coordinates.</summary>
        public struct Target
        {
            public VisualElement El, Top;
            public GameObject Go;          // set when the target is a uGUI object
            public Vector2 Screen, Panel;
        }

        static readonly ConditionalWeakTable<VisualElement, object> IdOf = new ConditionalWeakTable<VisualElement, object>();
        static readonly Dictionary<int, WeakReference<VisualElement>> ById = new Dictionary<int, WeakReference<VisualElement>>();
        static readonly HashSet<VisualElement> Hooked = new HashSet<VisualElement>();
        static readonly Dictionary<(Type, string), PropertyInfo> PropCache = new Dictionary<(Type, string), PropertyInfo>();
        static readonly Dictionary<Type, bool> FieldCache = new Dictionary<Type, bool>();
        static int _nextId = 1;

        internal static void Register()
        {
            Driver.Register("ui_tree", Driver.Sync(Tree));
            Driver.Register("ui_find", Driver.Sync(Find));
            Driver.Register("ui_inspect", Driver.Sync(Inspect));
        }

        // ---------------- ids ----------------

        public static int Id(VisualElement e)
        {
            object o;
            if (IdOf.TryGetValue(e, out o)) return (int)o;
            int id = _nextId++;
            IdOf.Add(e, id);
            ById[id] = new WeakReference<VisualElement>(e);
            if (ById.Count > 60000)
            {
                var dead = new List<int>();
                foreach (var kv in ById) { VisualElement x; if (!kv.Value.TryGetTarget(out x) || x.panel == null) dead.Add(kv.Key); }
                foreach (int d in dead) ById.Remove(d);
            }
            return id;
        }

        public static VisualElement Get(int id)
        {
            WeakReference<VisualElement> w; VisualElement e;
            return ById.TryGetValue(id, out w) && w.TryGetTarget(out e) && e.panel != null ? e : null;
        }

        static VisualElement Need(int id)
        {
            var e = Get(id);
            if (e == null) throw new ToolException("element #" + id + " no longer exists (ids go stale when the UI is rebuilt) - call ui_tree / ui_find again");
            return e;
        }

        // ---------------- panels & coordinates ----------------

        static List<VisualElement> _roots;
        static int _rootsFrame = -1;

        /// <summary>Root elements of all live runtime panels, topmost first; cached for the current frame in Play Mode.</summary>
        public static List<VisualElement> Roots()
        {
            if (_roots != null && _rootsFrame == Time.frameCount && Application.isPlaying) return _roots;
            var list = new List<KeyValuePair<float, VisualElement>>();
            foreach (var d in Util.FindAll<UIDocument>())
            {
                var root = d.rootVisualElement;
                if (root == null || root.panel == null) continue;
                var top = root.panel.visualTree;
                bool dup = false;
                foreach (var kv in list) if (kv.Value == top) { dup = true; break; }
                if (!dup) list.Add(new KeyValuePair<float, VisualElement>(d.panelSettings != null ? d.panelSettings.sortingOrder : 0, top));
            }
            list.Sort((x, y) => y.Key.CompareTo(x.Key));
            var res = new List<VisualElement>();
            foreach (var kv in list) res.Add(kv.Value);
            _rootsFrame = Time.frameCount;
            return _roots = res;
        }

        static Vector2 Scale(VisualElement top)
        {
            var l = top.layout;
            return l.width > 0 && l.height > 0 ? new Vector2(Screen.width / l.width, Screen.height / l.height) : Vector2.one;
        }

        public static Vector2 PanelToScreen(VisualElement top, Vector2 p) { var s = Scale(top); return new Vector2(p.x * s.x, p.y * s.y); }
        public static Vector2 ScreenToPanel(VisualElement top, Vector2 p) { var s = Scale(top); return new Vector2(p.x / s.x, p.y / s.y); }

        public static Rect ScreenRect(VisualElement e)
        {
            var s = Scale(e.panel.visualTree); var b = e.worldBound;
            return new Rect(b.x * s.x, b.y * s.y, b.width * s.x, b.height * s.y);
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin), x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }

        // ---------------- element facts ----------------

        static bool IsVisible(VisualElement e)
        {
            var s = e.resolvedStyle;
            return s.display != DisplayStyle.None && s.visibility == Visibility.Visible && s.opacity > 0.01f;
        }

        public static bool VisibleDeep(VisualElement e)
        {
            if (e.panel == null) return false;
            for (var p = e; p != null; p = p.hierarchy.parent) if (!IsVisible(p)) return false;
            return true;
        }

        public static bool IsAncestor(VisualElement ancestor, VisualElement e)
        {
            for (var p = e; p != null; p = p.hierarchy.parent) if (p == ancestor) return true;
            return false;
        }

        public static PropertyInfo Prop(Type t, string name)
        {
            PropertyInfo p;
            if (PropCache.TryGetValue((t, name), out p)) return p;
            for (var c = t; c != null && c != typeof(object) && p == null; c = c.BaseType)
            {
                var x = c.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (x != null && x.GetIndexParameters().Length == 0) p = x;
            }
            PropCache[(t, name)] = p;
            return p;
        }

        static string PropStr(VisualElement e, string name)
        {
            var p = Prop(e.GetType(), name);
            if (p == null || !p.CanRead) return null;
            try { var v = p.GetValue(e); return v == null ? "null" : v.ToString(); } catch { return null; }
        }

        public static string ValueOf(VisualElement e)
        {
            // ToggleButtonGroupState prints as a 64-bit mask; list the indices of the pressed buttons instead.
            if (e is ToggleButtonGroup g)
            {
                var on = new StringBuilder();
                for (int i = 0; i < g.value.length; i++) if (g.value[i]) on.Append(on.Length > 0 ? "," : "").Append(i);
                return "on [" + on + "]";
            }
            return PropStr(e, "value");
        }

        static bool IsBaseField(Type t)
        {
            bool r;
            if (FieldCache.TryGetValue(t, out r)) return r;
            for (var c = t; c != null && !r; c = c.BaseType) r = c.IsGenericType && c.GetGenericTypeDefinition() == typeof(BaseField<>);
            FieldCache[t] = r;
            return r;
        }

        static bool IsLeafControl(VisualElement e) { return (e is Button || IsBaseField(e.GetType())) && !(e is RadioButtonGroup); }

        public static bool Interactive(VisualElement e)
        {
            if (e is Button || e is Foldout || IsBaseField(e.GetType())) return true;
            if (e.hierarchy.parent == null) return false;
            foreach (var c in e.GetClasses())
                if (Util.Contains(c, "button") || Util.Contains(c, "btn") || Util.Contains(c, "clickable") || c.EndsWith("tab__header", StringComparison.Ordinal)) return true;
            return e.focusable && e.pickingMode == PickingMode.Position && !(e is ScrollView) && !(e is TemplateContainer) && !(e is TextElement);
        }

        static void Gather(VisualElement e, StringBuilder sb, int depth)
        {
            for (int i = 0; i < e.hierarchy.childCount && sb.Length < 80; i++)
            {
                var c = e.hierarchy[i];
                var te = c as TextElement;
                if (te != null && !string.IsNullOrEmpty(te.text)) { if (sb.Length > 0) sb.Append(" | "); sb.Append(te.text); }
                else if (depth > 0) Gather(c, sb, depth - 1);
            }
        }

        /// <summary>Text the user sees on this element (its own text, or the texts inside a control).</summary>
        public static string OwnText(VisualElement e)
        {
            var te = e as TextElement;
            if (te != null && !string.IsNullOrEmpty(te.text)) return te.text;
            if (IsBaseField(e.GetType())) { var l = PropStr(e, "label"); return l == null || l == "null" ? "" : l; }
            if (te != null || Interactive(e)) { var sb = new StringBuilder(); Gather(e, sb, 3); return sb.ToString(); }
            return "";
        }

        static bool Named(VisualElement e) { return !string.IsNullOrEmpty(e.name) && !e.name.StartsWith("unity-", StringComparison.Ordinal); }

        public static string Desc(VisualElement e)
        {
            if (e == null) return "(none)";
            string t = OwnText(e);
            return "#" + Id(e) + " " + e.GetType().Name + (string.IsNullOrEmpty(e.name) ? "" : " \"" + e.name + "\"") + (t.Length > 0 ? " \"" + Util.OneLine(t, 40) + "\"" : "");
        }

        public static string Line(VisualElement e) { return Line(e, false, true); }

        static string Line(VisualElement e, bool clipped, bool compact)
        {
            var sb = new StringBuilder();
            sb.Append('#').Append(Id(e)).Append(' ').Append(e.GetType().Name);
            if (!string.IsNullOrEmpty(e.name)) sb.Append(" \"").Append(e.name).Append('"');
            int n = 0;
            foreach (var c in e.GetClasses())
            {
                if (compact && c.StartsWith("unity-", StringComparison.Ordinal)) continue;
                if (n++ == 0) sb.Append(' ');
                sb.Append('.').Append(c);
                if (compact && n >= 4) break;
            }
            string t = OwnText(e);
            if (t.Length > 0) sb.Append(" \"").Append(Util.OneLine(t, 60)).Append('"');
            string v = ValueOf(e);
            if (v != null) sb.Append(" =").Append(Util.OneLine(v, 60));
            var r = ScreenRect(e);
            sb.Append(" [").Append(Mathf.RoundToInt(r.x)).Append(',').Append(Mathf.RoundToInt(r.y)).Append(',').Append(Mathf.RoundToInt(r.width)).Append(',').Append(Mathf.RoundToInt(r.height)).Append(']');
            if (Interactive(e)) sb.Append(" *");
            if (!e.enabledInHierarchy) sb.Append(" disabled");
            if (!VisibleDeep(e)) sb.Append(" hidden");
            if (clipped) sb.Append(" clipped(scrolled out)");
            if (e.focusController != null && ReferenceEquals(e.focusController.focusedElement, e)) sb.Append(" focused");
            return sb.ToString();
        }

        static string PathOf(VisualElement e, int max)
        {
            var parts = new List<string>();
            for (var p = e.hierarchy.parent; p != null && parts.Count < max; p = p.hierarchy.parent) if (Named(p)) parts.Add(p.name);
            parts.Reverse();
            return parts.Count == 0 ? "(root)" : string.Join(">", parts);
        }

        /// <summary>All elements of all panels, depth-first.</summary>
        public static IEnumerable<VisualElement> All(bool visibleOnly)
        {
            var stack = new Stack<VisualElement>();
            var roots = Roots();
            for (int i = roots.Count - 1; i >= 0; i--) stack.Push(roots[i]);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (visibleOnly && !IsVisible(e)) continue;
                yield return e;
                for (int i = e.hierarchy.childCount - 1; i >= 0; i--) stack.Push(e.hierarchy[i]);
            }
        }

        public static VisualElement FindFirst(string name, string text)
        {
            VisualElement partial = null;
            foreach (var e in All(true))
            {
                if (name != null)
                {
                    if (string.IsNullOrEmpty(e.name)) continue;
                    if (text != null && !Util.Contains(OwnText(e), text)) continue;
                    if (e.name == name) return e;
                    if (partial == null && Util.Contains(e.name, name)) partial = e;
                }
                else if (text != null)
                {
                    string t = OwnText(e);
                    if (t.Length == 0) continue;
                    if (string.Equals(t, text, StringComparison.OrdinalIgnoreCase)) return e;
                    if (partial == null && Util.Contains(t, text)) partial = e;
                }
            }
            return partial;
        }

        // ---------------- target resolution ----------------

        public static Target Resolve(JObject a, string prefix = "")
        {
            VisualElement el = null;
            GameObject go = null;
            if (a.Has(prefix + "id"))
            {
                int id = a.Int(prefix + "id");
                if (id < Ugui.IdBase) el = Need(id);
                else
                {
                    go = Ugui.Get(id);
                    if (go == null) throw new ToolException("uGUI element #" + id + " no longer exists or is inactive - call ui_tree / ui_find again");
                }
            }
            else if (a.Has(prefix + "name") || a.Has(prefix + "text"))
            {
                el = FindFirst(a.Str(prefix + "name"), a.Str(prefix + "text"));
                if (el == null) go = Ugui.FindFirst(a.Str(prefix + "name"), a.Str(prefix + "text"));
                if (el == null && go == null) throw new ToolException("no visible element with name='" + a.Str(prefix + "name") + "' text='" + a.Str(prefix + "text") + "' - check ui_tree / ui_find");
            }

            var t = new Target();
            if (go != null)
            {
                t.Go = go;
                var rc = Intersect(Ugui.ScreenRect(go), new Rect(0, 0, Screen.width, Screen.height));
                t.Screen = rc.width > 0 && rc.height > 0 ? rc.center : Ugui.ScreenRect(go).center;
            }
            else if (el != null)
            {
                t.El = el; t.Top = el.panel.visualTree;
                var rect = el.worldBound;
                var vis = Intersect(rect, t.Top.worldBound);
                for (var p = el.hierarchy.parent; p != null; p = p.hierarchy.parent)
                {
                    var sv = p as ScrollView;
                    if (sv != null) vis = Intersect(vis, sv.contentViewport.worldBound);
                }
                t.Panel = vis.width > 0 && vis.height > 0 ? vis.center : rect.center;
                t.Screen = PanelToScreen(t.Top, t.Panel);
            }
            else if (a.Has(prefix + "x") && a.Has(prefix + "y"))
            {
                t.Screen = new Vector2(a.Num(prefix + "x"), a.Num(prefix + "y"));
                bool hitAny = false;
                foreach (var top in Roots())
                {
                    var p = ScreenToPanel(top, t.Screen);
                    var hit = top.panel.Pick(p);
                    if (t.Top == null || hit != null) { t.Top = top; t.Panel = p; }
                    if (hit != null) { hitAny = true; break; }
                }
                // Nothing from UI Toolkit under the point: the pointer belongs to uGUI if a canvas element is there.
                if (!hitAny) t.Go = Ugui.Raycast(t.Screen);
            }
            else throw new ToolException("specify a target: " + prefix + "id, " + prefix + "name, " + prefix + "text, or " + prefix + "x and " + prefix + "y");
            return t;
        }

        /// <summary>Reasons why acting on this target may not do what the agent expects (or null).</summary>
        public static string Warnings(Target t)
        {
            if (t.Go != null) return Ugui.Warnings(t.Go);
            if (t.El == null) return null;
            var sb = new StringBuilder();
            if (!VisibleDeep(t.El)) sb.AppendLine("WARNING: target is hidden (display/visibility/opacity).");
            if (!t.El.enabledInHierarchy) sb.AppendLine("WARNING: target is disabled.");
            var b = t.El.worldBound;
            if (b.width < 1 || b.height < 1) sb.AppendLine("WARNING: target has zero size.");
            var hit = t.Top.panel.Pick(t.Panel);
            if (!t.Top.worldBound.Contains(t.Panel)) sb.AppendLine("WARNING: target is outside the screen - scroll it into view (ui_scroll_to) or make the window larger.");
            else if (hit == null) sb.AppendLine("WARNING: nothing is pickable at the target point (scrolled out of view, off-screen, or pickingMode=Ignore). Try ui_scroll_to.");
            else if (!IsAncestor(t.El, hit) && !IsAncestor(hit, t.El)) sb.AppendLine("WARNING: target is covered - the pointer lands on " + Desc(hit) + " instead.");
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }

        // ---------------- tools ----------------

        sealed class Ctx
        {
            public readonly StringBuilder Sb = new StringBuilder();
            public int Count, Max, Skipped;
            public bool VisibleOnly, InteractiveOnly, Compact;
        }

        static void Walk(VisualElement e, int indent, int depthLeft, Rect? clip, Ctx c)
        {
            if (c.VisibleOnly && !IsVisible(e)) return;
            bool inter = Interactive(e);
            bool print = c.InteractiveOnly ? inter : !c.Compact || Named(e) || inter || (e is TextElement && OwnText(e).Length > 0);
            if (print)
            {
                if (c.Count >= c.Max) c.Skipped++;
                else
                {
                    var b = e.worldBound;
                    bool clipped = clip.HasValue && b.width > 0 && !clip.Value.Overlaps(b);
                    c.Sb.Append(' ', indent * 2).AppendLine(Line(e, clipped, c.Compact));
                    c.Count++;
                }
            }
            if (depthLeft <= 0 || (c.Compact && IsLeafControl(e))) return;
            var sv = e as ScrollView;
            Rect? childClip = sv != null ? sv.contentViewport.worldBound : clip;
            int next = print && !c.InteractiveOnly ? indent + 1 : indent;
            for (int i = 0; i < e.hierarchy.childCount; i++) Walk(e.hierarchy[i], next, depthLeft - 1, childClip, c);
        }

        static void Tree(JObject a, ToolResponse r)
        {
            HookPanels();
            var c = new Ctx { Max = a.Int("maxNodes", 300), VisibleOnly = a.Bool("visibleOnly", true), InteractiveOnly = a.Bool("interactiveOnly"), Compact = a.Bool("compact", true) };
            int depth = a.Int("depth", 25);
            if (a.Has("root") && a.Int("root") >= Ugui.IdBase)
            {
                var rootGo = Ugui.Get(a.Int("root"));
                if (rootGo == null) throw new ToolException("uGUI element #" + a.Int("root") + " not found");
                Ugui.Tree(c.Sb, rootGo, c.InteractiveOnly, c.Max, ref c.Count, ref c.Skipped);
            }
            else if (a.Has("root")) Walk(Need(a.Int("root")), 0, depth, null, c);
            else
            {
                var roots = Roots();
                int max = c.Max;
                int canvases = Ugui.Tree(c.Sb, null, c.InteractiveOnly, max, ref c.Count, ref c.Skipped, roots.Count);
                if (roots.Count == 0 && canvases == 0)
                {
                    r.Text("No UI found: no active UIDocument (UI Toolkit) and no active Canvas (uGUI). If the game draws its UI another way (IMGUI, world-space objects), use screenshot + screen_pick + x,y with method=input.");
                    return;
                }
                for (int i = 0; i < roots.Count; i++)
                {
                    var l = roots[i].layout;
                    c.Sb.AppendLine("panel " + i + " (" + Mathf.RoundToInt(l.width) + "x" + Mathf.RoundToInt(l.height) + " panel units, screen " + Screen.width + "x" + Screen.height + ")");
                    c.Max = c.Count + Math.Max(0, max - c.Count) / (roots.Count - i);
                    Walk(roots[i], 1, depth, null, c);
                }
                if (c.Skipped > 0 && roots.Count + canvases > 1) c.Sb.AppendLine("(maxNodes is shared equally between the " + (roots.Count + canvases) + " panels and canvases above)");
            }
            r.Text(c.Sb.ToString().TrimEnd());
            if (c.Skipped > 0) r.Text("... " + c.Skipped + " more element(s) not shown (maxNodes). Raise maxNodes, or narrow with root=<id of a panel element or canvas child> / interactiveOnly / ui_find.");
        }

        static bool TypeMatch(Type t, string name)
        {
            for (var c = t; c != null; c = c.BaseType) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Find(JObject a, ToolResponse r)
        {
            HookPanels();
            string name = a.Str("name"), text = a.Str("text"), cls = a.Str("cls"), type = a.Str("type");
            if (name == null && text == null && cls == null && type == null) throw new ToolException("give at least one of: name, text, cls, type");
            int limit = a.Int("limit", 30), n = 0, more = 0;
            foreach (var e in All(a.Bool("visibleOnly", true)))
            {
                if (name != null && !Util.Contains(e.name, name)) continue;
                if (cls != null && !e.ClassListContains(cls)) continue;
                if (type != null && !TypeMatch(e.GetType(), type)) continue;
                if (text != null && !Util.Contains(OwnText(e), text) && !Util.Contains(ValueOf(e), text)) continue;
                if (n < limit) { r.Text(Line(e) + "  path=" + PathOf(e, 5)); n++; } else more++;
            }
            if (cls == null)
            {
                var lines = new List<string>();
                Ugui.Find(name, text, type, lines);
                foreach (var l in lines) { if (n < limit) { r.Text(l); n++; } else more++; }
            }
            r.Text(n + " match(es)" + (more > 0 ? ", " + more + " more not shown" : ""));
        }

        static void Inspect(JObject a, ToolResponse r)
        {
            var t = Resolve(a);
            if (t.Go != null) { Ugui.Inspect(t.Go, r); return; }
            var e = t.El ?? (t.Top != null ? t.Top.panel.Pick(t.Panel) : null);
            if (e == null) throw new ToolException("no element at that point");
            var s = e.resolvedStyle;
            r.Text(Line(e, false, false));
            r.Text("type: " + e.GetType().FullName + " | path: " + PathOf(e, 30));
            r.Text("enabledSelf=" + e.enabledSelf + " enabledInHierarchy=" + e.enabledInHierarchy + " visible=" + VisibleDeep(e) + " pickingMode=" + e.pickingMode
                   + " focusable=" + e.focusable + " children=" + e.hierarchy.childCount);
            r.Text("screen rect: " + ScreenRect(e) + " | panel rect: " + e.worldBound + " | layout: " + e.layout);
            r.Text("style: display=" + s.display + " visibility=" + s.visibility + " opacity=" + s.opacity + " position=" + s.position
                   + " flexDirection=" + s.flexDirection + " flexGrow=" + s.flexGrow + " flexShrink=" + s.flexShrink + " alignItems=" + s.alignItems + " justifyContent=" + s.justifyContent
                   + " width=" + s.width + " height=" + s.height
                   + " margin(t,r,b,l)=" + s.marginTop + "," + s.marginRight + "," + s.marginBottom + "," + s.marginLeft
                   + " padding(t,r,b,l)=" + s.paddingTop + "," + s.paddingRight + "," + s.paddingBottom + "," + s.paddingLeft
                   + " color=" + s.color + " background=" + s.backgroundColor + " fontSize=" + s.fontSize + " borderWidth(t)=" + s.borderTopWidth);
            string v = ValueOf(e);
            if (v != null) r.Text("value (" + Prop(e.GetType(), "value").PropertyType.Name + "): " + Util.Cut(v, 500));
            var te = e as TextElement;
            if (te != null) r.Text("text: " + Util.Cut(te.text, 500));
            if (!string.IsNullOrEmpty(e.tooltip)) r.Text("tooltip: " + e.tooltip);
            if (e.userData != null) r.Text("userData: " + e.userData.GetType().FullName + " (read with state_get '#" + Id(e) + ".userData')");
            if (e.dataSource != null) r.Text("dataSource: " + e.dataSource.GetType().FullName + " (read with state_get '#" + Id(e) + ".dataSource')");
            var center = e.worldBound.center;
            var hit = e.panel.Pick(center);
            r.Text("hit-test at center: " + (hit == null ? "nothing pickable" : hit == e ? "this element" : IsAncestor(e, hit) ? "its child " + Desc(hit) : IsAncestor(hit, e) ? "its ancestor " + Desc(hit) + " (this element is not pickable itself)" : "COVERED by " + Desc(hit)));
            string w = Warnings(new Target { El = e, Top = e.panel.visualTree, Panel = center });
            if (w != null) r.Text(w);
            int shown = Math.Min(e.hierarchy.childCount, 15);
            for (int i = 0; i < shown; i++) r.Text("  child: " + Line(e.hierarchy[i], false, true));
            if (e.hierarchy.childCount > shown) r.Text("  ... " + (e.hierarchy.childCount - shown) + " more children (use ui_tree root=" + Id(e) + ")");
        }

        // ---------------- event hooks ----------------

        /// <summary>Attach capture-phase listeners to every panel root, so all UI events are recorded regardless of game code.</summary>
        public static void HookPanels()
        {
            foreach (var top in Roots())
            {
                if (!Hooked.Add(top)) continue;
                Reg<PointerDownEvent>(top, "ui.down"); Reg<PointerUpEvent>(top, "ui.up"); Reg<ClickEvent>(top, "ui.click");
                Reg<PointerMoveEvent>(top, "ui.move"); Reg<WheelEvent>(top, "ui.wheel");
                Reg<KeyDownEvent>(top, "ui.keydown"); Reg<KeyUpEvent>(top, "ui.keyup");
                Reg<FocusInEvent>(top, "ui.focus"); Reg<FocusOutEvent>(top, "ui.blur");
                Reg<NavigationSubmitEvent>(top, "ui.submit"); Reg<NavigationCancelEvent>(top, "ui.cancel"); Reg<NavigationMoveEvent>(top, "ui.navmove");
                RegChange<string>(top); RegChange<bool>(top); RegChange<int>(top); RegChange<float>(top); RegChange<double>(top); RegChange<long>(top);
                RegChange<Enum>(top); RegChange<Vector2>(top); RegChange<Vector3>(top); RegChange<Color>(top);
            }
        }

        static void Reg<T>(VisualElement top, string cat) where T : EventBase<T>, new()
        {
            top.RegisterCallback<T>(e =>
            {
                if (EventStore.IsDisabled(cat)) return;
                var sb = new StringBuilder(cat.Substring(3)).Append(' ').Append(Desc(e.target as VisualElement));
                var pe = e as IPointerEvent; var ke = e as IKeyboardEvent; var we = e as WheelEvent;
                if (pe != null) { var sc = PanelToScreen(top, pe.position); sb.Append(" at (").Append(Mathf.RoundToInt(sc.x)).Append(',').Append(Mathf.RoundToInt(sc.y)).Append(") button=").Append(pe.button); }
                else if (ke != null) { sb.Append(" key=").Append(ke.keyCode); if (ke.character != '\0') sb.Append(" char='").Append(ke.character).Append('\''); }
                else if (we != null) sb.Append(" delta=").Append(we.delta);
                EventStore.Emit(cat, sb.ToString());
            }, TrickleDown.TrickleDown);
        }

        static void RegChange<V>(VisualElement top)
        {
            top.RegisterCallback<ChangeEvent<V>>(e =>
                EventStore.Emit("ui.change", "change " + Desc(e.target as VisualElement) + " " + Util.OneLine(Convert.ToString(e.previousValue), 80) + " -> " + Util.OneLine(Convert.ToString(e.newValue), 80)),
                TrickleDown.TrickleDown);
        }
    }
}

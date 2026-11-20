namespace UDriver
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using UnityEngine;
    using UnityEngine.Events;
    using UnityEngine.SceneManagement;
    using UnityEngine.UIElements;

    /// <summary>app_map: what the app is made of, in human terms.</summary>
    internal static class AppMap
    {
        internal const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static void Register() { Driver.Register("app_map", Driver.Sync(Map)); }

        internal sealed class Wire
        {
            public string Event, Method, Where, Kind;
            public Type Type;
            public override string ToString() { return Type.Name + "." + Method + (Where != null ? " on " + Where : ""); }
        }

        internal sealed class Item
        {
            public string Role, Label, Screen, Panel, Path, Note;
            public int Id, Rows = -1;
            public GameObject Go;
            public VisualElement El;
            public readonly List<string> Labels = new List<string>();
            public List<Wire> Wires = new List<Wire>();
        }

        // ---------------- names, types, instances ----------------

        internal static string Human(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Replace("(Clone)", "");
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i] == '_' || name[i] == '-' ? ' ' : name[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) sb.Append(' ');
                if (c != ' ' || (sb.Length > 0 && sb[sb.Length - 1] != ' ')) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Member name as a state path uses it: auto-property backing fields become the property.</summary>
        internal static string Clean(string field) { return field.StartsWith("<", StringComparison.Ordinal) ? field.Substring(1, field.IndexOf('>') - 1) : field; }

        internal static bool Is(Type t, string name)
        {
            for (; t != null; t = t.BaseType) if (t.Name == name) return true;
            return false;
        }

        internal static bool IsColl(object v) { return v is IEnumerable && !(v is string) && !(v is UnityEngine.Object); }

        internal static bool Game(Type t) { return t != null && StateTools.IsGame(t.Assembly) && t.Name.IndexOf('<') < 0; }

        /// <summary>Instance fields declared by the game's own classes (engine base classes are skipped).</summary>
        internal static IEnumerable<FieldInfo> Fields(Type type)
        {
            for (var t = type; t != null && Game(t); t = t.BaseType)
                foreach (var f in t.GetFields(Inst))
                    if (f.Name.IndexOf('<') < 0 || f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)) yield return f;
        }

        internal static bool Live(UnityEngine.Object o)
        {
            if (o == null) return false;
            var go = o as GameObject ?? (o as Component)?.gameObject;
            if (go == null) return true;
            return go.scene.IsValid() && go.scene.isLoaded && (Driver.Host == null || go.transform.root != Driver.Host.transform);
        }

        internal static List<MonoBehaviour> GameBehaviours()
        {
            var l = new List<MonoBehaviour>();
            foreach (var m in Resources.FindObjectsOfTypeAll<MonoBehaviour>()) if (m != null && Game(m.GetType()) && Live(m)) l.Add(m);
            l.Sort((x, y) => string.CompareOrdinal(StateTools.PathOf(x.gameObject), StateTools.PathOf(y.gameObject)));
            return l;
        }

        static List<FieldInfo> _statics;
        static int _staticsAsm;

        /// <summary>Static fields of game classes that hold an object: "@Ns.Type.member" -> value.</summary>
        internal static List<KeyValuePair<string, object>> Statics()
        {
            int n = AppDomain.CurrentDomain.GetAssemblies().Length;
            if (_statics == null || n != _staticsAsm)
            {
                _statics = new List<FieldInfo>(); _staticsAsm = n;
                foreach (var t in StateTools.GameTypes(false))
                {
                    if (!Game(t) || t.ContainsGenericParameters || t.IsEnum || t.IsInterface) continue;
                    foreach (var f in t.GetFields(Stat))
                    {
                        var ft = f.FieldType;
                        if (f.IsLiteral || (f.Name.IndexOf('<') >= 0 && !f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)) || ft.IsPrimitive || ft.IsEnum
                            || ft == typeof(string) || typeof(Delegate).IsAssignableFrom(ft) || typeof(Type).IsAssignableFrom(ft)) continue;
                        _statics.Add(f);
                    }
                }
            }
            var res = new List<KeyValuePair<string, object>>();
            foreach (var f in _statics)
            {
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                if (v == null || (v is UnityEngine.Object && (UnityEngine.Object)v == null)) continue;
                res.Add(new KeyValuePair<string, object>("@" + f.DeclaringType.FullName + "." + Clean(f.Name), v));
            }
            return res;
        }

        /// <summary>Type name as a query root: short when unambiguous.</summary>
        internal static string RootName(Type t) { return StateTools.FindType(t.Name) == t ? t.Name : t.FullName; }

        internal static string Where(UnityEngine.Object o)
        {
            var go = o as GameObject ?? (o as Component)?.gameObject;
            return go != null ? StateTools.PathOf(go) : o is ScriptableObject ? "asset \"" + o.name + "\"" : null;
        }

        // ---------------- wiring ----------------

        static readonly FieldInfo CallsField = typeof(UnityEventBase).GetField("m_Calls", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo RegistryField = typeof(CallbackEventHandler).GetField("m_CallbackRegistry", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Add(Delegate d, string ev, string kind, List<Wire> into)
        {
            var m = d.Method;
            var t = m.DeclaringType;
            while (t != null && t.IsNested && t.Name.IndexOf('<') >= 0) t = t.DeclaringType;
            if (!Game(t)) return;
            string name = m.Name;
            int a = name.IndexOf('<'), b = name.IndexOf('>');
            if (a >= 0 && b > a) name = name.Substring(a + 1, b - a - 1) + " (lambda)";
            var target = d.Target as UnityEngine.Object;
            foreach (var w in into) if ((w.Event == ev || kind == "callback") && w.Type == t && w.Method == name) return;
            into.Add(new Wire { Event = ev, Type = t, Method = name, Kind = kind, Where = target != null ? Where(target) : null });
        }

        static void Delegates(object o, string ev, string kind, Func<string, bool> skip, List<Wire> into)
        {
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(Inst))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType) || (skip != null && skip(f.Name))) continue;
                    var d = f.GetValue(o) as Delegate;
                    if (d != null) foreach (var x in d.GetInvocationList()) Add(x, ev ?? Clean(f.Name), kind, into);
                }
        }

        internal static void UnityEventWires(UnityEventBase ev, string name, List<Wire> into)
        {
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var t = ev.GetPersistentTarget(i);
                string m = ev.GetPersistentMethodName(i);
                if (t != null && !string.IsNullOrEmpty(m)) into.Add(new Wire { Event = name, Type = t.GetType(), Method = m, Kind = "persistent", Where = Where(t) });
            }
            try
            {
                var calls = CallsField != null ? CallsField.GetValue(ev) : null;
                var f = calls != null ? calls.GetType().GetField("m_RuntimeCalls", Inst) : null;
                var rt = f != null ? f.GetValue(calls) as IEnumerable : null;
                if (rt != null) foreach (var call in rt) if (call != null) Delegates(call, name, "runtime", null, into);
            }
            catch { }
        }

        static string EventLabel(string field)
        {
            field = Clean(field);
            return field.StartsWith("m_", StringComparison.Ordinal) && field.Length > 2 ? char.ToLowerInvariant(field[2]) + field.Substring(3) : field;
        }

        internal static List<Wire> WiresOf(GameObject go)
        {
            var l = new List<Wire>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                try
                {
                    for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour); t = t.BaseType)
                        foreach (var f in t.GetFields(Inst))
                            if (typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) { var ev = f.GetValue(c) as UnityEventBase; if (ev != null) UnityEventWires(ev, EventLabel(f.Name), l); }
                }
                catch { }
                if (Game(c.GetType()) && c.GetType().GetInterface("IPointerClickHandler") != null)
                    l.Add(new Wire { Event = "click", Type = c.GetType(), Method = "OnPointerClick", Kind = "handler", Where = StateTools.PathOf(go) });
            }
            return l;
        }

        static string EventName(Type delegateType)
        {
            var args = delegateType.IsGenericType ? delegateType.GetGenericArguments() : null;
            string n = args != null && args.Length > 0 ? args[0].Name : delegateType.Name;
            int k = n.IndexOf('`');
            if (k > 0) n = n.Substring(0, k);
            if (n.EndsWith("Event", StringComparison.Ordinal) && n.Length > 5) n = n.Substring(0, n.Length - 5);
            return char.ToLowerInvariant(n[0]) + n.Substring(1);
        }

        /// <summary>Delegates of game code reachable from a UI Toolkit callback registry (internal layout differs between Unity versions).</summary>
        static void Callbacks(object o, int depth, HashSet<object> seen, List<Wire> into)
        {
            if (o == null || depth > 6 || seen.Count > 400 || o is VisualElement || o is IPanel || o is string || o.GetType().IsPrimitive || !seen.Add(o)) return;
            var d = o as Delegate;
            if (d != null) { foreach (var x in d.GetInvocationList()) Add(x, EventName(d.GetType()), "callback", into); return; }
            var en = o as IEnumerable;
            if (en != null) { foreach (var x in en) Callbacks(x, depth + 1, seen, into); return; }
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(Inst))
                    if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum) Callbacks(f.GetValue(o), depth + 1, seen, into);
        }

        internal static List<Wire> WiresOf(VisualElement e)
        {
            var l = new List<Wire>();
            try
            {
                Delegates(e, null, "runtime", n => { n = n.ToLowerInvariant(); return n.Contains("make") || n.Contains("bind") || n.Contains("destroy"); }, l);
                var cp = UiTools.Prop(e.GetType(), "clickable");
                var clickable = cp != null ? cp.GetValue(e) : null;
                if (clickable != null) Delegates(clickable, "click", "runtime", null, l);
                if (RegistryField != null) Callbacks(RegistryField.GetValue(e), 0, new HashSet<object>(), l);
            }
            catch { }
            return l;
        }

        // ---------------- UI items ----------------

        static readonly string[] UguiRoles = { "Button:button", "Toggle:toggle", "TMP_InputField:field", "InputField:field", "TMP_Dropdown:dropdown", "Dropdown:dropdown", "Slider:slider", "Scrollbar:scrollbar", "ScrollRect:list" };

        internal static string Role(GameObject go, out Component main)
        {
            main = null;
            int best = UguiRoles.Length;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                for (int i = 0; i < best; i++)
                    if (Is(c.GetType(), UguiRoles[i].Substring(0, UguiRoles[i].IndexOf(':')))) { best = i; main = c; break; }
            }
            if (main != null)
            {
                string role = UguiRoles[best].Substring(UguiRoles[best].IndexOf(':') + 1);
                bool tabby = Util.Contains(go.name, "tab") || (role == "toggle" && Get(main, "group") != null && go.transform.parent != null && Util.Contains(go.transform.parent.name, "tab"));
                return (role == "button" || role == "toggle") && tabby ? "tab" : role;
            }
            if (!string.IsNullOrEmpty(Ugui.LabelOn(go))) return "text";
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                if (c.GetType().GetInterface("IPointerClickHandler") != null) { main = c; return "button"; }
                if (Is(c.GetType(), "Image") || Is(c.GetType(), "RawImage")) return "image";
            }
            return null;
        }

        internal static string Role(VisualElement e)
        {
            var t = e.GetType();
            if (e.ClassListContains("unity-tab__header") || (e is Button && Util.Contains(e.name, "tab"))) return "tab";
            if (Is(t, "Tab")) return null;
            if (e is Button) return "button";
            if (e is BaseVerticalCollectionView || e is ScrollView) return "list";
            if (Is(t, "Toggle") || Is(t, "RadioButton")) return "toggle";
            if (Is(t, "BasePopupField`2") || Is(t, "EnumField")) return "dropdown";
            if (Is(t, "BaseSlider`1") || Is(t, "MinMaxSlider")) return "slider";
            if (UiTools.IsBaseField(t)) return "field";
            if (e is Label) return "text";
            if (e is Image) return "image";
            return null;
        }

        internal static object Get(object o, string prop)
        {
            var p = o != null ? UiTools.Prop(o.GetType(), prop) : null;
            try { return p != null && p.CanRead ? p.GetValue(o) : null; } catch { return null; }
        }

        internal static List<string> Options(object control)
        {
            var res = new List<string>();
            var opts = Get(control, "options") as IEnumerable ?? Get(control, "choices") as IEnumerable;
            if (opts != null) foreach (var o in opts) res.Add(o as string ?? Convert.ToString(Get(o, "text")));
            return res;
        }

        static readonly Dictionary<string, Rect> NoRects = new Dictionary<string, Rect>();

        /// <summary>The closest text left of or above the control (screen rects, top-left origin).</summary>
        static string Nearest(Rect c, List<KeyValuePair<Rect, string>> texts)
        {
            string best = null; float bestD = float.MaxValue;
            foreach (var kv in texts)
            {
                var r = kv.Key; float d;
                if (r.xMax <= c.xMin + 4 && r.yMin < c.yMax && r.yMax > c.yMin) d = c.xMin - r.xMax;
                else if (r.yMax <= c.yMin + 4 && r.xMin < c.xMax && r.xMax > c.xMin) d = (c.yMin - r.yMax) * 1.5f;
                else continue;
                if (d < bestD) { bestD = d; best = kv.Value; }
            }
            return best;
        }

        static string NearestLabel(GameObject go)
        {
            var self = go.transform;
            for (int up = 0; up < 2 && self.parent != null; up++, self = self.parent)
            {
                var texts = new List<KeyValuePair<Rect, string>>();
                foreach (Transform s in self.parent)
                {
                    if (s == self || !s.gameObject.activeInHierarchy) continue;
                    Component main;
                    string role = Role(s.gameObject, out main);
                    if (role == "text") texts.Add(new KeyValuePair<Rect, string>(Ugui.ScreenRect(s.gameObject), Ugui.LabelOn(s.gameObject)));
                }
                string n = Nearest(Ugui.ScreenRect(self.gameObject), texts);
                if (n != null) return n.Trim();
            }
            return null;
        }

        static string NearestLabel(VisualElement e)
        {
            for (int up = 0; up < 2 && e.hierarchy.parent != null; up++, e = e.hierarchy.parent)
            {
                var p = e.hierarchy.parent;
                int i = p.hierarchy.IndexOf(e);
                if (i == 0) continue;
                var s = p.hierarchy[i - 1] as Label;
                return s != null && !string.IsNullOrEmpty(s.text) ? s.text.Trim() : null;
            }
            return null;
        }

        static void Labels(Item it, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || c.Trim().Length == 0) continue;
                string t = Util.OneLine(c.Trim().TrimEnd(':', '*').Trim(), 60);
                if (!it.Labels.Contains(t)) it.Labels.Add(t);
            }
            it.Label = it.Labels.Count > 0 ? it.Labels[0] : it.Role;
        }

        internal static string ValueText(Item it)
        {
            if (it.Go != null)
            {
                Component main;
                Role(it.Go, out main);
                if (it.Role == "dropdown" && main != null)
                {
                    var opts = Options(main); var v = Get(main, "value");
                    int i = v is int ? (int)v : -1;
                    return i >= 0 && i < opts.Count ? opts[i] : Convert.ToString(v);
                }
                return Ugui.ValueOf(it.Go);
            }
            return it.El != null && it.Role != "button" && it.Role != "list" ? UiTools.ValueOf(it.El) : null;
        }

        static void UguiItem(GameObject go, bool texts, List<Item> into)
        {
            Component main;
            string role = Role(go, out main);
            if (role == null || role == "scrollbar" || role == "image" || (role == "text" && !texts)) return;
            var it = new Item { Role = role, Go = go, Id = Ugui.Id(go), Path = StateTools.PathOf(go) };
            Transform panel = go.transform, canvasT = null;
            for (var p = go.transform; p != null; p = p.parent)
            {
                var cv = p.GetComponent<Canvas>();
                if (cv != null && cv.isRootCanvas) { canvasT = p; break; }
                panel = p;
            }
            if (canvasT == null) return;
            it.Screen = canvasT.name;
            it.Panel = panel == go.transform && role != "list" ? null : PanelTitle(panel.gameObject);
            for (var p = go.transform.parent; p != null && p != canvasT; p = p.parent)
            {
                Component pm;
                string pr = Role(p.gameObject, out pm);
                if (pr == "button" || pr == "toggle" || pr == "tab" || pr == "field" || pr == "dropdown" || pr == "slider") return;
                if (pr == "list")
                {
                    var content = Get(pm, "content") as Transform;
                    if (content == null) continue;
                    for (var q = go.transform; q.parent != null; q = q.parent)
                        if (q.parent == content) { int row = q.GetSiblingIndex(); if (row >= 3) return; it.Note = "row " + row; break; }
                }
            }
            if (role == "list")
            {
                var content = Get(main, "content") as Transform;
                int n = 0;
                if (content != null) foreach (Transform c in content) if (c.gameObject.activeSelf) n++;
                it.Rows = n;
            }
            string own = role == "field" || role == "dropdown" || role == "slider" || role == "list" ? null : Ugui.TextOf(go);
            string placeholder = role == "field" ? Ugui.LabelOn(((Component)Get(main, "placeholder") ?? go.transform).gameObject) : null;
            Labels(it, own, role == "text" ? null : NearestLabel(go), placeholder, Human(go.name), go.name);
            if (role != "text") it.Wires = WiresOf(go);
            into.Add(it);
        }

        static string PanelTitle(GameObject panel)
        {
            foreach (var g in panel.GetComponentsInChildren<Transform>(false))
                if ((Util.Contains(g.name, "title") || Util.Contains(g.name, "header") || Util.Contains(g.name, "caption")) && !string.IsNullOrEmpty(Ugui.LabelOn(g.gameObject)))
                    return Util.OneLine(Ugui.LabelOn(g.gameObject), 40);
            return Human(panel.name);
        }

        static string PanelTitle(VisualElement panel)
        {
            foreach (var l in panel.Query<Label>().ToList())
                if ((Util.Contains(l.name, "title") || Util.Contains(l.name, "header") || l.ClassListContains("title")) && !l.ClassListContains("unity-tab__header-label") && !string.IsNullOrEmpty(l.text)) return Util.OneLine(l.text, 40);
            if (!string.IsNullOrEmpty(panel.name)) return Human(panel.name);
            var first = panel.Q<Label>();
            return first != null && !string.IsNullOrEmpty(first.text) ? Util.OneLine(first.text, 40) : panel.GetType().Name;
        }

        static Dictionary<VisualElement, string> Documents()
        {
            var d = new Dictionary<VisualElement, string>();
            foreach (var doc in Util.FindAll<UIDocument>()) if (doc.rootVisualElement != null && !d.ContainsKey(doc.rootVisualElement)) d[doc.rootVisualElement] = doc.name;
            return d;
        }

        /// <summary>Top-level containers inside a document root (one wrapper level is looked through).</summary>
        static VisualElement PanelRoot(VisualElement docRoot)
        {
            if (docRoot.hierarchy.childCount != 1) return docRoot;
            var only = docRoot.hierarchy[0];
            if (only.hierarchy.childCount < 2) return docRoot;
            for (int i = 0; i < only.hierarchy.childCount; i++) if (Role(only.hierarchy[i]) != null) return docRoot;
            return only;
        }

        static void UitkItem(VisualElement e, Dictionary<VisualElement, string> docs, bool texts, List<Item> into)
        {
            string role = Role(e);
            if (role == null || role == "image" || (role == "text" && !texts)) return;
            VisualElement doc = null, panel = null, child = e;
            string note = null;
            for (var p = e.hierarchy.parent; p != null; child = p, p = p.hierarchy.parent)
            {
                if (p is BaseVerticalCollectionView || (p is Button && e is TextElement) || (UiTools.IsBaseField(p.GetType()))) return;
                var sv = p as ScrollView;
                if (sv != null && child.hierarchy.parent == sv.contentContainer) { int row = sv.contentContainer.IndexOf(child); if (row >= 3) return; note = note ?? "row " + row; }
                if (docs.ContainsKey(p)) { doc = p; break; }
            }
            if (doc == null) return;
            var pr = PanelRoot(doc);
            for (var p = e; p != null && p != doc; p = p.hierarchy.parent) if (p.hierarchy.parent == pr) { panel = p; break; }
            var it = new Item { Role = role, El = e, Id = UiTools.Id(e), Screen = docs[doc], Note = note, Panel = panel != null && panel != e ? PanelTitle(panel) : null };
            it.Path = "#" + it.Id + " " + UiTools.PathOf(e, 4) + (string.IsNullOrEmpty(e.name) ? "" : ">" + e.name);
            var cv = e as BaseVerticalCollectionView;
            if (cv != null) it.Rows = cv.itemsSource != null ? cv.itemsSource.Count : 0;
            else if (e is ScrollView) it.Rows = ((ScrollView)e).contentContainer.childCount;
            string own = role == "list" ? null : UiTools.OwnText(e);
            if (string.IsNullOrEmpty(own) && role == "tab") own = e.Q<Label>()?.text;
            Labels(it, own, role == "text" ? null : NearestLabel(e), Human(e.name), e.name, e.GetType().Name);
            if (role != "text") it.Wires = WiresOf(e);
            into.Add(it);
        }

        /// <summary>Visible controls of uGUI canvases and UI Toolkit documents with role, human label and wiring; texts optional.</summary>
        internal static List<Item> Items(bool texts)
        {
            var items = new List<Item>();
            foreach (var go in Ugui.All()) UguiItem(go, texts, items);
            var docs = Documents();
            foreach (var e in UiTools.All(true)) UitkItem(e, docs, texts, items);
            foreach (var c in QueryTools.FindCharts())
            {
                var comp = c.Owner as Component;
                if (comp == null || comp.GetComponentInParent<Canvas>() == null || !comp.gameObject.activeInHierarchy) continue;
                var it = new Item { Role = "chart", Go = comp.gameObject, Id = Ugui.Id(comp.gameObject), Path = StateTools.PathOf(comp.gameObject), Rows = c.Points, Note = "chart_data id=" + c.Id };
                var root = comp.GetComponentInParent<Canvas>().rootCanvas.transform;
                var panel = comp.transform;
                while (panel.parent != null && panel.parent != root) panel = panel.parent;
                it.Screen = root.name; it.Panel = panel == comp.transform ? null : PanelTitle(panel.gameObject);
                Labels(it, NearestLabel(comp.gameObject), Human(comp.gameObject.name));
                bool dup = false;
                foreach (var x in items) if (x.Role == "chart" && x.Go == it.Go) dup = true;
                if (!dup) items.Add(it);
            }
            return items;
        }

        // ---------------- output ----------------

        static string Fmt(Item it, bool full)
        {
            var sb = new StringBuilder(it.Role).Append(" \"").Append(Util.OneLine(it.Label, 50)).Append('"');
            string v = it.Role == "button" || it.Role == "tab" || it.Role == "text" || it.Role == "chart" ? null : ValueText(it);
            if (v != null) sb.Append(" = ").Append(v.Length == 0 ? "\"\"" : Util.OneLine(v, 40));
            if (it.Rows >= 0) sb.Append(it.Role == "chart" ? " (" + it.Rows + " points)" : " (" + it.Rows + " rows)");
            var wires = new List<string>();
            foreach (var w in it.Wires) wires.Add((full ? w.Event + ": " : "") + (full ? w.ToString() : w.Type.Name + "." + w.Method) + (full ? " [" + w.Kind + "]" : ""));
            if (wires.Count > 0) sb.Append(" -> ").Append(string.Join(", ", full ? wires : wires.GetRange(0, Math.Min(3, wires.Count))));
            if (!full && wires.Count > 3) sb.Append(" +" + (wires.Count - 3));
            if (it.Note != null && (full || it.Role == "chart")) sb.Append(" (").Append(it.Note).Append(')');
            sb.Append("  #").Append(it.Id);
            if (full) sb.Append(' ').Append(it.Path.StartsWith("#", StringComparison.Ordinal) ? it.Path.Substring(it.Path.IndexOf(' ') + 1) : it.Path);
            return sb.ToString();
        }

        static string Backend() { return Type.GetType("Mono.Runtime") != null ? "Mono" : "IL2CPP"; }

        static void Map(JObject a, ToolResponse r)
        {
            string section = a.Str("section", "overview"), target = a.Str("focus");
            int limit = Math.Max(1, a.Int("limit", 40));
            if (target != null) { Target(target, limit, r); return; }
            switch (section)
            {
                case "overview": Overview(limit, r); break;
                case "ui": Ui(true, limit * 5, r); break;
                case "scripts": Scripts(limit * 5, true, r); break;
                case "events": Events(limit * 5, r); break;
                case "data": Data(limit * 5, true, r); break;
                case "assemblies": Assemblies(r); break;
                default: throw new ToolException("section must be overview, ui, scripts, events, data or assemblies");
            }
        }

        static void Overview(int limit, ToolResponse r)
        {
            r.Text("app: \"" + Application.productName + "\" " + Application.version + " by " + Application.companyName + " (" + Application.identifier + ") | Unity " + Application.unityVersion
                   + " | " + Backend() + " | " + Application.platform + (Application.isEditor ? " (editor)" : Debug.isDebugBuild ? " development build" : ""));
            var loaded = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                loaded.Add(s.name + (s == SceneManager.GetActiveScene() ? " (active)" : "") + (s.isLoaded ? "" : " (loading)"));
            }
            var build = new List<string>();
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++) build.Add(System.IO.Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i)));
            r.Text("scenes: " + string.Join(", ", loaded) + " | build list: " + (build.Count > 0 ? string.Join(", ", build) : "(none)"));
            Ui(false, limit, r);
            Scripts(limit, false, r);
            Data(limit, false, r);
            r.Text("next: app_map section=ui|scripts|events|data, app_map focus=<Type or /Object/Path>, query select=\"<Type>.<list>[*]\", chart_data, form_fill");
        }

        static void Ui(bool full, int limit, ToolResponse r)
        {
            var items = Items(full);
            var screens = new List<string>();
            foreach (var cv in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!cv.isRootCanvas || !Live(cv)) continue;
                var panels = new List<string>(); var hidden = new List<string>();
                foreach (Transform c in cv.transform) (c.gameObject.activeSelf ? panels : hidden).Add(PanelTitle(c.gameObject));
                screens.Add("canvas \"" + cv.name + "\" (uGUI" + (cv.isActiveAndEnabled ? "" : ", hidden") + ")" + Panels(panels, hidden));
            }
            var docs = Documents();
            foreach (var kv in docs)
            {
                var panels = new List<string>(); var hidden = new List<string>();
                var pr = PanelRoot(kv.Key);
                for (int i = 0; i < pr.hierarchy.childCount; i++) (UiTools.VisibleDeep(pr.hierarchy[i]) ? panels : hidden).Add(PanelTitle(pr.hierarchy[i]));
                screens.Add("document \"" + kv.Value + "\" (UI Toolkit)" + Panels(panels, hidden));
            }
            r.Text("screens: " + (screens.Count == 0 ? "no uGUI canvas or UI Toolkit document" : ""));
            int shown = 0, more = 0;
            foreach (var s in screens) r.Text("  " + s);
            var groups = new Dictionary<string, int>();
            var order = new List<KeyValuePair<string, Item>>();
            foreach (var it in items)
            {
                string key = full ? it.Id.ToString() : it.Screen + "|" + it.Panel + "|" + Fmt(it, false).Split(new[] { "  #" }, StringSplitOptions.None)[0];
                int n;
                if (groups.TryGetValue(key, out n)) { groups[key] = n + 1; continue; }
                groups[key] = 1;
                order.Add(new KeyValuePair<string, Item>(key, it));
            }
            r.Text(full ? "ui (" + items.Count + " elements; #id works in ui_click / ui_set_value):" : "controls (#id for ui_click / ui_set_value; texts in section=ui):");
            string lastGroup = null;
            foreach (var kv in order)
            {
                if (shown >= limit) { more++; continue; }
                var it = kv.Value;
                string g = it.Screen + (it.Panel != null ? " / " + it.Panel : "");
                if (g != lastGroup) { r.Text("  [" + g + "]"); lastGroup = g; }
                int n = groups[kv.Key];
                r.Text("    " + Fmt(it, full) + (n > 1 ? " (x" + n + ")" : ""));
                shown++;
            }
            if (more > 0) r.Text("  ... " + more + " more (raise limit" + (full ? "" : " or use section=ui") + ")");
        }

        static string Panels(List<string> panels, List<string> hidden)
        {
            return (panels.Count > 0 ? " panels: " + string.Join(", ", panels) : "") + (hidden.Count > 0 ? "; hidden: " + string.Join(", ", hidden) : "");
        }

        static void Scripts(int limit, bool full, ToolResponse r)
        {
            var byType = new Dictionary<Type, List<MonoBehaviour>>();
            var order = new List<Type>();
            foreach (var m in GameBehaviours())
            {
                List<MonoBehaviour> l;
                if (!byType.TryGetValue(m.GetType(), out l)) { byType[m.GetType()] = l = new List<MonoBehaviour>(); order.Add(m.GetType()); }
                l.Add(m);
            }
            r.Text("scripts (game code; app_map focus=<Type> for fields and methods):");
            var line = new List<string>();
            int shown = 0;
            foreach (var t in order)
            {
                if (shown++ >= limit) { line.Add("... " + (order.Count - limit) + " more"); break; }
                var l = byType[t];
                var where = new List<string>();
                for (int i = 0; i < l.Count && i < (full ? 5 : 1); i++) where.Add(StateTools.PathOf(l[i].gameObject));
                int off = 0; foreach (var m in l) if (!m.isActiveAndEnabled) off++;
                string s = t.Name + " x" + l.Count + " at " + string.Join(", ", where) + (l.Count > where.Count ? ", ..." : "") + (off > 0 ? " (" + off + " inactive)" : "");
                if (full) r.Text("  " + s + (t.BaseType != null && t.BaseType != typeof(MonoBehaviour) ? " : " + StateTools.TypeName(t.BaseType) : ""));
                else line.Add(s);
            }
            if (!full) Wrap(r, "  components: ", line);
            var sos = new List<string>();
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
                if (so != null && Game(so.GetType())) sos.Add(so.GetType().Name + " \"" + so.name + "\" " + StateTools.IdOf(so));
            if (sos.Count > 0) Wrap(r, "  ScriptableObjects: ", Cap(sos, limit));
            var statics = new List<string>();
            foreach (var kv in Statics())
            {
                var vt = kv.Value.GetType();
                if (!Game(vt) || IsColl(kv.Value)) continue;
                statics.Add(kv.Key.Substring(1) + " -> " + vt.Name + " " + StateTools.IdOf(kv.Value));
            }
            if (statics.Count > 0) Wrap(r, "  static instances: ", Cap(statics, limit));
            if (full)
            {
                int plain = 0;
                var names = new List<string>();
                foreach (var t in StateTools.GameTypes(false))
                    if (Game(t) && !t.IsNested && !typeof(UnityEngine.Object).IsAssignableFrom(t) && !t.IsEnum && !t.IsInterface) { plain++; if (names.Count < limit) names.Add(t.Name); }
                Wrap(r, "  other classes (" + plain + "): ", names);
            }
        }

        static List<string> Cap(List<string> l, int limit)
        {
            if (l.Count <= limit) return l;
            var res = l.GetRange(0, limit);
            res.Add("... " + (l.Count - limit) + " more");
            return res;
        }

        static void Wrap(ToolResponse r, string head, List<string> parts)
        {
            if (parts.Count == 0) return;
            var sb = new StringBuilder(head);
            foreach (var p in parts)
            {
                if (sb.Length > head.Length && sb.Length + p.Length > 160) { r.Text(sb.ToString().TrimEnd(' ', ';')); sb.Clear().Append(' ', head.Length); }
                sb.Append(p).Append("; ");
            }
            r.Text(sb.ToString().TrimEnd(' ', ';'));
        }

        internal sealed class Model { public string Path, Elem; public int Count, Owners; public List<string> Fields = new List<string>(); }

        internal static Type ElementType(object coll)
        {
            var t = coll.GetType();
            if (t.IsArray) return t.GetElementType();
            if (coll is IDictionary) return t.IsGenericType && t.GetGenericArguments().Length == 2 ? t.GetGenericArguments()[1] : null;
            foreach (var i in t.GetInterfaces()) if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return i.GetGenericArguments()[0];
            return null;
        }

        static bool Record(Type t) { return Game(t) && !typeof(UnityEngine.Object).IsAssignableFrom(t) && !t.IsEnum && Fields(t).GetEnumerator().MoveNext(); }

        static int Count(object coll)
        {
            var c = coll as ICollection;
            if (c != null) return c.Count;
            int n = 0;
            foreach (var x in (IEnumerable)coll) if (++n > 100000) break;
            return n;
        }

        static void Models(object owner, string prefix, int depth, Dictionary<string, Model> into, HashSet<object> seen)
        {
            foreach (var f in Fields(owner.GetType()))
            {
                object v;
                try { v = f.GetValue(owner); } catch { continue; }
                if (v == null || v is UnityEngine.Object) continue;
                string path = prefix + "." + Clean(f.Name);
                if (IsColl(v))
                {
                    var et = ElementType(v);
                    if (et == null || !Record(et) || !seen.Add(v)) continue;
                    Model m;
                    if (!into.TryGetValue(path, out m))
                    {
                        into[path] = m = new Model { Path = path, Elem = StateTools.TypeName(v.GetType()) };
                        foreach (var rf in Fields(et)) if (m.Fields.Count < 10) m.Fields.Add(Clean(rf.Name) + ":" + StateTools.TypeName(rf.FieldType));
                    }
                    m.Count += Count(v); m.Owners++;
                }
                else if (depth == 0 && Record(v.GetType()) && seen.Add(v)) Models(v, path, 1, into, seen);
            }
        }

        internal static List<Model> Models()
        {
            var into = new Dictionary<string, Model>();
            var seen = new HashSet<object>();
            foreach (var m in GameBehaviours()) Models(m, RootName(m.GetType()), 0, into, seen);
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>()) if (so != null && Game(so.GetType())) Models(so, RootName(so.GetType()), 0, into, seen);
            foreach (var kv in Statics())
            {
                if (kv.Value is UnityEngine.Object) continue;
                if (IsColl(kv.Value))
                {
                    var et = ElementType(kv.Value);
                    if (et == null || !Record(et) || !seen.Add(kv.Value)) continue;
                    var m = new Model { Path = kv.Key, Elem = StateTools.TypeName(kv.Value.GetType()), Count = Count(kv.Value), Owners = 1 };
                    foreach (var rf in Fields(et)) if (m.Fields.Count < 10) m.Fields.Add(Clean(rf.Name) + ":" + StateTools.TypeName(rf.FieldType));
                    into[kv.Key] = m;
                }
                else if (Game(kv.Value.GetType()) && seen.Add(kv.Value)) Models(kv.Value, kv.Key, 0, into, seen);
            }
            var l = new List<Model>(into.Values);
            l.Sort((x, y) => y.Count.CompareTo(x.Count));
            return l;
        }

        static void Data(int limit, bool full, ToolResponse r)
        {
            var models = Models();
            r.Text("data (collections of records; the path works in query, e.g. query select=\"<path>[-5:]\"):" + (models.Count == 0 ? " none found" : ""));
            for (int i = 0; i < models.Count; i++)
            {
                if (i >= limit) { r.Text("  ... " + (models.Count - limit) + " more"); break; }
                var m = models[i];
                r.Text("  " + m.Path + "  " + m.Elem + " x" + m.Count + (m.Owners > 1 ? " in " + m.Owners + " objects" : "") + "  {" + string.Join(", ", full ? m.Fields : StripTypes(m.Fields)) + "}");
            }
            if (!full) return;
            var charts = QueryTools.FindCharts();
            if (charts.Count > 0) r.Text("numeric series: " + charts.Count + " (see chart_data)");
        }

        static List<string> StripTypes(List<string> fields)
        {
            var l = new List<string>();
            foreach (var f in fields) l.Add(f.Substring(0, f.IndexOf(':')));
            return l;
        }

        static void Events(int limit, ToolResponse r)
        {
            int n = 0, more = 0;
            r.Text("event wiring (control -> handler; persistent = set in the scene, runtime = AddListener / += in code, callback = RegisterCallback):");
            var seen = new HashSet<GameObject>();
            foreach (var it in Items(false))
            {
                if (it.Go != null) seen.Add(it.Go);
                foreach (var w in it.Wires)
                {
                    if (n++ >= limit) { more++; continue; }
                    r.Text("  " + it.Role + " \"" + Util.OneLine(it.Label, 40) + "\" #" + it.Id + "  " + w.Event + " -> " + w + " [" + w.Kind + "]");
                }
            }
            foreach (var m in GameBehaviours())
            {
                if (!seen.Add(m.gameObject)) continue;
                foreach (var w in WiresOf(m.gameObject))
                {
                    if (w.Kind == "handler") continue;
                    if (n++ >= limit) { more++; continue; }
                    r.Text("  " + StateTools.PathOf(m.gameObject) + "  " + w.Event + " -> " + w + " [" + w.Kind + "]");
                }
            }
            if (n == 0) r.Text("  none found");
            if (more > 0) r.Text("  ... " + more + " more (raise limit)");
        }

        static void Assemblies(ToolResponse r)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                string loc;
                try { loc = asm.Location; } catch { loc = ""; }
                r.Text(new JObject { ["name"] = asm.GetName().Name, ["path"] = loc, ["game"] = StateTools.IsGame(asm) }.ToString(Formatting.None));
            }
        }

        // ---------------- drill-down ----------------

        static void Target(string target, int limit, ToolResponse r)
        {
            if (target.StartsWith("/", StringComparison.Ordinal) || target.StartsWith("#", StringComparison.Ordinal))
            {
                var o = StateTools.Resolve(target);
                var el = o as VisualElement;
                if (el != null) { ObjectEl(el, limit, r); return; }
                var go = o as GameObject ?? (o as Component)?.gameObject;
                if (go == null) throw new ToolException(target + " is a " + (o == null ? "null" : o.GetType().Name) + ", not a GameObject or UI element");
                ObjectGo(go, limit, r);
                return;
            }
            var t = StateTools.FindType(target);
            if (t == null)
            {
                var near = new List<string>();
                foreach (var x in StateTools.GameTypes(false)) if (Game(x) && Util.Contains(x.Name, target) && near.Count < 10) near.Add(x.FullName);
                throw new ToolException("no type '" + target + "'" + (near.Count > 0 ? "; similar: " + string.Join(", ", near) : "") + ". Object paths start with '/'.");
            }
            TypeInfo(t, limit, r);
        }

        static void TypeInfo(Type t, int limit, ToolResponse r)
        {
            string why = StateTools.Why(t, "");
            if (why != null) throw new ToolException(t.FullName + " is hidden by the safety policy: " + why);
            r.Text(t.FullName + (t.BaseType != null ? " : " + StateTools.TypeName(t.BaseType) : "") + "  [" + t.Assembly.GetName().Name + "]");
            var inst = QueryTools.Instances(t);
            var ids = new List<string>();
            foreach (var node in inst)
            {
                if (ids.Count >= limit) { ids.Add("... " + (inst.Count - limit) + " more"); break; }
                var uo = node.V as UnityEngine.Object;
                string w = uo != null ? Where(uo) : null;
                ids.Add(node.Id() + (w != null ? " " + w : ""));
            }
            r.Text("instances: " + inst.Count + (ids.Count > 0 ? " - " + string.Join(", ", ids) : "") + (inst.Count > 0 ? " (query select=\"" + RootName(t) + "\")" : ""));
            object one = inst.Count == 1 ? inst[0].V : null;
            foreach (var kv in Statics()) if (ReferenceEquals(kv.Value, one)) r.Text("singleton: " + kv.Key);
            var calls = new Dictionary<string, List<string>>();
            foreach (var it in Items(false))
                foreach (var w in it.Wires)
                    if (w.Type == t || w.Type.IsAssignableFrom(t) || t.IsAssignableFrom(w.Type))
                    {
                        List<string> l;
                        if (!calls.TryGetValue(w.Method, out l)) calls[w.Method] = l = new List<string>();
                        l.Add(it.Role + " \"" + Util.OneLine(it.Label, 30) + "\" #" + it.Id + " (" + w.Event + ")");
                    }
            int n = 0;
            var flags = Game(t) ? Inst | BindingFlags.Static : BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            r.Text("fields" + (one != null ? " (values of the only instance)" : "") + ":");
            for (var c = t; c != null && (c == t || Game(c)) && n < limit * 2; c = c.BaseType)
                foreach (var f in c.GetFields(flags))
                {
                    if (f.Name.IndexOf('<') >= 0 && !f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)) continue;
                    string val = "";
                    if (f.IsStatic || one != null)
                    {
                        try { val = " = " + Util.OneLine(StateTools.Brief(f.GetValue(f.IsStatic ? null : one)), 100); } catch (Exception e) { val = " = <" + e.GetType().Name + ">"; }
                    }
                    r.Text("  " + (f.IsStatic ? "static " : "") + StateTools.TypeName(f.FieldType) + " " + Clean(f.Name) + val);
                    n++;
                }
            r.Text("methods:");
            for (var c = t; c != null && (c == t || Game(c)); c = c.BaseType)
                foreach (var m in c.GetMethods(flags))
                {
                    if (m.IsSpecialName || m.Name.IndexOf('<') >= 0) continue;
                    List<string> by;
                    r.Text("  " + StateTools.Signature(m) + (calls.TryGetValue(m.Name, out by) ? "  <- " + string.Join(", ", by) : ""));
                }
            foreach (var kv in calls) if (kv.Key.EndsWith("(lambda)", StringComparison.Ordinal)) r.Text("  lambda in " + kv.Key.Replace(" (lambda)", "") + "  <- " + string.Join(", ", kv.Value));
            r.Text("call with state_call path=\"" + (one != null ? inst[0].Id() : "<id from instances>") + "\" method=\"<name>\"; read with query select=\"" + RootName(t) + ".<field>\"");
        }

        static void ObjectGo(GameObject go, int limit, ToolResponse r)
        {
            r.Text(StateTools.PathOf(go) + " " + StateTools.IdOf(go) + (go.activeInHierarchy ? "" : " (inactive)") + " scene " + go.scene.name);
            var comps = new List<string>();
            foreach (var c in go.GetComponents<Component>()) if (c != null) comps.Add(c.GetType().Name + (Game(c.GetType()) ? "*" : ""));
            r.Text("components: " + string.Join(", ", comps) + " (* = game code; app_map focus=<Type>)");
            Component main;
            string role = Role(go, out main);
            if (role != null)
            {
                var tmp = new List<Item>();
                UguiItem(go, true, tmp);
                r.Text("ui: " + (tmp.Count > 0 ? Fmt(tmp[0], true) + (tmp[0].Labels.Count > 1 ? "; labels: " + string.Join(" | ", tmp[0].Labels) : "") : role));
            }
            foreach (var w in WiresOf(go)) r.Text("  " + w.Event + " -> " + w + " [" + w.Kind + "]");
            var kids = new List<string>();
            foreach (Transform c in go.transform)
            {
                if (kids.Count >= limit) { kids.Add("... " + (go.transform.childCount - limit) + " more"); break; }
                Component m;
                string cr = Role(c.gameObject, out m);
                kids.Add(c.name + (cr != null ? " [" + cr + (cr == "text" ? " \"" + Util.OneLine(Ugui.LabelOn(c.gameObject), 30) + "\"" : "") + "]" : "") + (c.childCount > 0 ? " (+" + c.childCount + ")" : "") + (c.gameObject.activeSelf ? "" : " (inactive)"));
            }
            r.Text("children (" + go.transform.childCount + "): " + string.Join(", ", kids));
        }

        static void ObjectEl(VisualElement e, int limit, ToolResponse r)
        {
            r.Text(UiTools.Line(e) + "  path " + UiTools.PathOf(e, 8));
            var tmp = new List<Item>();
            UitkItem(e, Documents(), true, tmp);
            if (tmp.Count > 0) r.Text("ui: " + Fmt(tmp[0], true) + (tmp[0].Labels.Count > 1 ? "; labels: " + string.Join(" | ", tmp[0].Labels) : ""));
            else foreach (var w in WiresOf(e)) r.Text("  " + w.Event + " -> " + w + " [" + w.Kind + "]");
            var kids = new List<string>();
            for (int i = 0; i < e.hierarchy.childCount; i++)
            {
                if (kids.Count >= limit) { kids.Add("... more"); break; }
                var c = e.hierarchy[i];
                string cr = Role(c);
                kids.Add("#" + UiTools.Id(c) + " " + c.GetType().Name + (string.IsNullOrEmpty(c.name) ? "" : " \"" + c.name + "\"") + (cr != null ? " [" + cr + "]" : ""));
            }
            r.Text("children (" + e.hierarchy.childCount + "): " + string.Join(", ", kids));
        }
    }
}

namespace UDriver
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Newtonsoft.Json.Linq;
    using UnityEngine;
    using UnityEngine.SceneManagement;

    public static class StateTools
    {
        const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static readonly string[] EnginePrefixes = { "System", "mscorlib", "netstandard", "Unity", "Mono.", "Newtonsoft", "UDriver", "0Harmony", "nunit", "Bee.", "ExCSS", "ReportGenerator" };
        static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        /// <summary>A value plus (for '@Type' roots) the static type whose members are being addressed.</summary>
        struct Ref { public object Obj; public Type Static; }

        internal static void Register()
        {
            Driver.Register("scene_tree", Driver.Sync(SceneTree));
            Driver.Register("state_get", Driver.Sync(Get));
            Driver.Register("state_set", Driver.Sync(Set));
            Driver.Register("state_call", Driver.Sync(Call));
            Driver.Register("state_query", QueryTool);
            Driver.Register("state_export", ExportTool);
            Driver.Register("types_find", Driver.Sync(TypesFind));
            Driver.Register("type_members", Driver.Sync(TypeMembers));
            Driver.Register("screen_pick", Driver.Sync(ScreenPick));
            Driver.Register("locate", Driver.Sync(Locate));
        }

        // ---------------- safety policy for writes and calls ----------------

        static readonly string[] SafeSystemNamespaces = { "System.Collections", "System.Linq", "System.Text", "System.Numerics" };
        static readonly HashSet<string> SafeSystemTypes = new HashSet<string>
        {
            "System.Object", "System.String", "System.Math", "System.MathF", "System.Convert", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
            "System.Guid", "System.Array", "System.Enum", "System.ValueType", "System.Decimal", "System.Random", "System.Version", "System.BitConverter"
        };
        static readonly string[] AllowedUnity =
        {
            "UnityEngine.UI", "UnityEngine.UIElements", "UnityEngine.EventSystems", "UnityEngine.InputSystem", "UnityEngine.SceneManagement", "UnityEngine.Rendering",
            "UnityEngine.AI", "UnityEngine.Animations", "UnityEngine.Audio", "UnityEngine.Tilemaps", "UnityEngine.TextCore", "UnityEngine.U2D", "UnityEngine.Playables",
            "UnityEngine.Timeline", "UnityEngine.Events", "UnityEngine.Pool", "UnityEngine.Serialization", "UnityEngine.Splines",
            "Unity.Mathematics", "Unity.Collections", "Unity.Cinemachine", "Unity.AI.Navigation", "TMPro",
        };
        static readonly string[] DeniedUnity =
        {
            // network, devices and the OS
            "UnityEngine.Networking", "UnityEngine.Windows", "UnityEngine.AndroidJava", "UnityEngine.Android", "UnityEngine.iOS", "UnityEngine.Apple", "UnityEngine.WSA",
            "UnityEngine.Microphone", "UnityEngine.WebCam", "UnityEngine.Application.OpenURL", "UnityEngine.Application.Unload", "UnityEngine.Diagnostics", "UnityEngine.CrashReport",
            "UnityEngine.Application.Quit", "UnityEngine.Application.RequestUserAuthorization", "UnityEngine.Handheld", "UnityEngine.GUIUtility.systemCopyBuffer",
            // persistent storage outside the scene (registry / plist / files)
            "UnityEngine.PlayerPrefs", "Unity.IO.", "Unity.Loading.", "Unity.Content.",
            // raw memory
            "Unity.Collections.LowLevel.Unsafe", "Unity.Jobs.LowLevel.Unsafe", "Unity.Burst.",
            // Unity APIs that read or write files at a caller-chosen path
            "UnityEngine.ScreenCapture", "UnityEngine.Profiling", "UnityEngine.AssetBundle", "UnityEngine.Caching", "UnityEngine.Video", "UnityEngine.WWW",
            "UnityEngine.Windows.File", "UnityEngine.Windows.Directory", "UnityEngine.ImageConversion", "UnityEngine.Experimental"
        };

        /// <summary>Reason why reflection must not touch this type/member under the default policy, or null if it may.</summary>
        internal static string Why(Type t, string memberName)
        {
            if (Driver.AllowUnsafe || t == null) return null;
            string full = (t.FullName ?? t.Name), ns = t.Namespace ?? "";
            if (ns == "UDriver" || ns.StartsWith("UDriver.", StringComparison.Ordinal)) return "driver internals";
            if (!IsEngine(t.Assembly)) return EditorOnly(t.Assembly) ? "editor code (use the editor_* tools)" : null; // the game's own code and its plugins
            if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return "UnityEditor API (use the editor_* tools)";
            if (ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal) || ns.StartsWith("Unity.", StringComparison.Ordinal) || ns == "TMPro")
            {
                string member = full + "." + memberName;
                foreach (var d in DeniedUnity) if (member.StartsWith(d, StringComparison.Ordinal)) return "Unity API that reaches outside the game (files, network, devices, OS)";
                if (ns == "UnityEngine") return null;
                foreach (var a in AllowedUnity) if (ns == a || ns.StartsWith(a + ".", StringComparison.Ordinal)) return null;
                return "Unity namespace outside the game-play list (" + ns + ")";
            }
            if (t.IsPrimitive || t.IsEnum || SafeSystemTypes.Contains(full) || full.StartsWith("System.Tuple", StringComparison.Ordinal)
                || full.StartsWith("System.ValueTuple", StringComparison.Ordinal) || full.StartsWith("System.Nullable", StringComparison.Ordinal)) return null;
            if (ns.StartsWith("System.Linq.Expressions", StringComparison.Ordinal)) return "code generation API";
            foreach (var a in SafeSystemNamespaces) if (ns == a || ns.StartsWith(a + ".", StringComparison.Ordinal)) return null;
            return "system API outside the safe list (files, processes, network, environment, reflection, threading...)";
        }

        /// <summary>No file, process, network, environment, reflection or UnityEditor APIs and no driver internals unless a human set UDRIVER_UNSAFE=1.</summary>
        internal static void Guard(MemberInfo m, string op)
        {
            string why = Why(m.DeclaringType, m.Name);
            if (why == null) return;
            throw new ToolException(op + " on " + (m.DeclaringType.FullName ?? m.DeclaringType.Name) + "." + m.Name + " is blocked by the safety policy: " + why
                                    + ". Only a human can lift this, by starting the game/editor with environment variable UDRIVER_UNSAFE=1.");
        }

        /// <summary>The indexer setter that a list or dictionary write will run.</summary>
        static void GuardItem(object o, Type itf)
        {
            var map = o.GetType().GetInterfaceMap(itf);
            Guard(map.TargetMethods[Array.FindIndex(map.InterfaceMethods, m => m.Name == "set_Item")], "state_set");
        }

        // ---------------- types ----------------

#if UNITY_EDITOR
        static HashSet<string> _playerAssemblies;
        static readonly Dictionary<Assembly, bool> EditorOnlyCache = new Dictionary<Assembly, bool>();

        /// <summary>Assemblies that exist only in the editor: not part of a player build and referencing UnityEditor.</summary>
        static bool EditorOnly(Assembly asm)
        {
            lock (EditorOnlyCache)
            {
                bool r;
                if (EditorOnlyCache.TryGetValue(asm, out r)) return r;
                if (_playerAssemblies == null)
                {
                    _playerAssemblies = new HashSet<string>();
                    foreach (var x in UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Player)) _playerAssemblies.Add(x.name);
                }
                r = false;
                if (!_playerAssemblies.Contains(asm.GetName().Name))
                    foreach (var n in asm.GetReferencedAssemblies()) if (n.Name.StartsWith("UnityEditor", StringComparison.Ordinal)) { r = true; break; }
                return EditorOnlyCache[asm] = r;
            }
        }
#else
        static bool EditorOnly(Assembly asm) { return false; }
#endif

        internal static bool IsGame(Assembly asm) { return !asm.IsDynamic && !IsEngine(asm) && !EditorOnly(asm); }

        internal static bool IsEngine(Assembly asm)
        {
            string n = asm.GetName().Name;
            foreach (var p in EnginePrefixes) if (n.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static Type[] TypesOf(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { var l = new List<Type>(); foreach (var t in e.Types) if (t != null) l.Add(t); return l.ToArray(); }
            catch { return new Type[0]; }
        }

        public static IEnumerable<Type> GameTypes(bool includeEngine)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || (!includeEngine && IsEngine(asm))) continue;
                foreach (var t in TypesOf(asm)) yield return t;
            }
        }

        /// <summary>Finds a type by full name, or by short name (game assemblies first).</summary>
        public static Type FindType(string name)
        {
            Type found;
            if (TypeCache.TryGetValue(name, out found)) return found;
            for (int pass = 0; pass < 2 && found == null; pass++)
            {
                Type byShort = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic || IsEngine(asm) != (pass == 1)) continue;
                    var t = asm.GetType(name, false);
                    if (t != null) { found = t; break; }
                    if (name.IndexOf('.') < 0 && byShort == null)
                        foreach (var x in TypesOf(asm)) if (x.Name == name) { byShort = x; break; }
                }
                if (found == null) found = byShort;
            }
            if (found != null) TypeCache[name] = found;
            return found;
        }

        internal static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int"; if (t == typeof(float)) return "float"; if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string"; if (t == typeof(object)) return "object"; if (t == typeof(double)) return "double"; if (t == typeof(long)) return "long";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                var args = new List<string>();
                foreach (var g in t.GetGenericArguments()) args.Add(TypeName(g));
                string n = t.Name; int i = n.IndexOf('`');
                return (i > 0 ? n.Substring(0, i) : n) + "<" + string.Join(",", args) + ">";
            }
            return t.Name;
        }

        // ---------------- path resolution ----------------

        internal static IEnumerable<GameObject> RootObjects()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (var go in s.GetRootGameObjects()) yield return go;
            }
            if (Driver.Host != null)
                foreach (var go in Driver.Host.gameObject.scene.GetRootGameObjects())
                    if (go != Driver.Host.gameObject) yield return go;
        }

        public static string PathOf(GameObject go)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        static Camera MainCamera()
        {
            if (Camera.main != null) return Camera.main;
            Camera best = null;
            foreach (var c in Camera.allCameras) if (best == null || c.depth > best.depth) best = c;
            return best;
        }

        static void ScreenPick(JObject a, ToolResponse r)
        {
            var p = new Vector2(a.Num("x"), a.Num("y"));
            r.Text("point (" + p.x + "," + p.y + ") on screen " + Screen.width + "x" + Screen.height);
            bool any = false;
            foreach (var top in UiTools.Roots())
            {
                var hit = top.panel.Pick(UiTools.ScreenToPanel(top, p));
                if (hit != null) { r.Text("UI Toolkit: " + UiTools.Line(hit)); any = true; }
            }
            foreach (var l in Ugui.RaycastAll(p, 5)) { r.Text("uGUI: " + l); any = true; }
            var cam = MainCamera();
            if (cam != null)
            {
                var ray = cam.ScreenPointToRay(new Vector3(p.x, Screen.height - p.y, 0));
                var hits = new List<KeyValuePair<float, Renderer>>();
                foreach (var rend in Util.FindAll<Renderer>())
                {
                    float d;
                    if (rend.enabled && rend.bounds.IntersectRay(ray, out d)) hits.Add(new KeyValuePair<float, Renderer>(d, rend));
                }
                hits.Sort((x, y) => x.Key.CompareTo(y.Key));
                for (int i = 0; i < hits.Count && i < 5; i++)
                {
                    r.Text("world (camera '" + cam.name + "', bounds test, distance " + hits[i].Key.ToString("0.##", CultureInfo.InvariantCulture) + "): " + PathOf(hits[i].Value.gameObject) + " [" + hits[i].Value.GetType().Name + "]");
                    any = true;
                }
            }
            if (!any) r.Text("nothing found under this point");
        }

        static void Locate(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            var go = path != null ? FindGo(path) : null;
            if (go == null) throw new ToolException("GameObject '" + path + "' not found - use scene_tree");
            r.Text(PathOf(go) + " | active=" + go.activeInHierarchy + " | world position " + go.transform.position);
            if (go.transform is RectTransform && go.GetComponentInParent<Canvas>() != null && Ugui.Available)
            {
                var rc = Ugui.ScreenRect(go);
                r.Text("uGUI rect on screen: [" + Mathf.RoundToInt(rc.x) + "," + Mathf.RoundToInt(rc.y) + "," + Mathf.RoundToInt(rc.width) + "," + Mathf.RoundToInt(rc.height) + "], center (" + Mathf.RoundToInt(rc.center.x) + "," + Mathf.RoundToInt(rc.center.y) + ")");
                return;
            }
            var cam = MainCamera();
            if (cam == null) { r.Text("no enabled camera - cannot project to the screen"); return; }
            var rend = go.GetComponentInChildren<Renderer>();
            Vector3 world = rend != null ? rend.bounds.center : go.transform.position;
            var sp = cam.WorldToScreenPoint(world);
            bool on = sp.z > 0 && sp.x >= 0 && sp.x <= Screen.width && sp.y >= 0 && sp.y <= Screen.height;
            r.Text("screen point (camera '" + cam.name + "'): x=" + Mathf.RoundToInt(sp.x) + " y=" + Mathf.RoundToInt(Screen.height - sp.y) + " depth=" + sp.z.ToString("0.##", CultureInfo.InvariantCulture)
                   + (sp.z <= 0 ? " - BEHIND the camera" : on ? " - on screen" : " - outside the screen")
                   + ". Click it with ui_click x,y method=input (may be occluded: check screen_pick).");
        }

        /// <summary>GameObject by hierarchy path or #u id; a path that matches several objects is an error listing them.</summary>
        internal static GameObject FindGo(string path)
        {
            if (path.StartsWith("#u", StringComparison.Ordinal))
            {
                var o = ById(path);
                var go = o as GameObject ?? (o as Component)?.gameObject;
                if (go == null) throw new ToolException(path + " is a " + o.GetType().Name + ", not a GameObject");
                return go;
            }
            var parts = path.Trim('/').Split('/');
            var cur = new List<Transform>();
            foreach (var root in RootObjects()) if (root.name == parts[0]) cur.Add(root.transform);
            for (int i = 1; i < parts.Length && cur.Count > 0; i++)
            {
                var next = new List<Transform>();
                foreach (var t in cur) foreach (Transform c in t) if (c.name == parts[i]) next.Add(c);
                cur = next;
            }
            if (cur.Count <= 1) return cur.Count == 1 ? cur[0].gameObject : null;
            var sb = new StringBuilder("'" + path + "' matches " + cur.Count + " GameObjects - address one by its id:");
            for (int i = 0; i < cur.Count && i < 20; i++) sb.Append("\n  ").Append(PathOf(cur[i].gameObject)).Append(' ').Append(IdOf(cur[i].gameObject));
            if (cur.Count > 20) sb.Append("\n  ... and ").Append(cur.Count - 20).Append(" more");
            throw new ToolException(sb.ToString());
        }

        static Component FindComponent(GameObject go, string name, string where)
        {
            foreach (var c in go.GetComponents<Component>())
                if (c != null && (c.GetType().Name == name || c.GetType().FullName == name)) return c;
            throw new ToolException("component '" + name + "' not found on " + where);
        }

        // ---------------- object ids ----------------

        const int MaxObjIds = 10000;
        static readonly Dictionary<int, WeakReference> ObjById = new Dictionary<int, WeakReference>();
        static readonly ConditionalWeakTable<object, object> IdByObj = new ConditionalWeakTable<object, object>();
        static readonly Queue<int> ObjOrder = new Queue<int>();
        static int _nextObjId;

        internal static long Key(UnityEngine.Object o)
        {
#if UNITY_6000_4_OR_NEWER
            return (long)EntityId.ToULong(o.GetEntityId());
#else
            return o.GetInstanceID();
#endif
        }

        /// <summary>"#u&lt;instanceID&gt;" for live Unity objects, "#o&lt;n&gt;" for destroyed Unity objects and other objects (held weakly, the newest 10000), null for values.</summary>
        public static string IdOf(object v)
        {
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo != null) return "#u" + Key(uo);
            if (v == null || v is string || v is Type || v.GetType().IsValueType) return null;
            lock (ObjById)
            {
                object id;
                if (IdByObj.TryGetValue(v, out id)) return "#o" + id;
                if (ObjOrder.Count >= MaxObjIds)
                {
                    int old = ObjOrder.Dequeue();
                    var target = ObjById[old].Target;
                    if (target != null) IdByObj.Remove(target);
                    ObjById.Remove(old);
                }
                int n = ++_nextObjId;
                ObjById[n] = new WeakReference(v);
                IdByObj.Add(v, n);
                ObjOrder.Enqueue(n);
                return "#o" + n;
            }
        }

        internal static object ById(string id)
        {
            int n = 0;
            bool ok = id.Length > 2 && int.TryParse(id.Substring(2), out n);
            if (id.StartsWith("#u", StringComparison.Ordinal))
            {
#if UNITY_6000_4_OR_NEWER
                long e;
                var o = long.TryParse(id.Substring(2), out e) ? Resources.EntityIdToObject(EntityId.FromULong((ulong)e)) : null;
#elif UNITY_2020_2_OR_NEWER
                var o = ok ? Resources.InstanceIDToObject(n) : null;
#else
                var o = ok ? Array.Find(Resources.FindObjectsOfTypeAll<UnityEngine.Object>(), x => x.GetInstanceID() == n) : null;
#endif
                if (o == null) throw new ToolException("object " + id + " not found (destroyed?)");
                return o;
            }
            if (ok && id[1] == 'o')
            {
                object t = null; WeakReference w;
                lock (ObjById) if (ObjById.TryGetValue(n, out w)) t = w.Target;
                if (t == null) throw new ToolException("object " + id + " is gone (collected, or older than the newest " + MaxObjIds + " ids) - get a fresh id with state_get");
                return t;
            }
            throw new ToolException("bad id '" + id + "': ids look like #u12345 (Unity object) or #o17 (other object)");
        }

        static object Member(Ref r, string name)
        {
            var type = r.Static ?? r.Obj.GetType();
            var m = Readable(type, name);
            if (m == null) throw new ToolException("no field/property '" + name + "' on " + type.FullName + " - use type_members to list them");
            Guard(m, "reading");
            var f = m as FieldInfo;
            if (f != null) return f.GetValue(On(r, name, f.IsStatic));
            var p = (PropertyInfo)m;
            return p.GetValue(On(r, name, p.GetGetMethod(true).IsStatic));
        }

        static object On(Ref r, string name, bool isStatic)
        {
            if (isStatic) return null;
            if (r.Obj == null) throw new ToolException("'" + name + "' is an instance field of " + r.Static.FullName + ", not static - read it on an object: pass the object id from state_get, e.g. #u<id>." + name + ", or a path like /GameObject:" + r.Static.Name + "." + name);
            return r.Obj;
        }

        /// <summary>Field or readable property. IL2CPP stripping removes getters nothing calls but keeps the data,
        /// so a missing property falls back to its backing field (auto-property or m_Name).</summary>
        static MemberInfo Readable(Type type, string name)
        {
            foreach (var n in new[] { name, "<" + name + ">k__BackingField", "m_" + name })
                for (var t = type; t != null; t = t.BaseType)
                {
                    var f = t.GetField(n, AllDeclared);
                    if (f != null) return f;
                    if (n != name) continue;
                    var p = t.GetProperty(n, AllDeclared);
                    if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null) return p;
                }
            return null;
        }

        static object Index(object obj, string key)
        {
            int i;
            var dict = obj as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry e in dict) if (Convert.ToString(e.Key) == key) return e.Value;
                throw new ToolException("key '" + key + "' not found in dictionary");
            }
            if (!int.TryParse(key, out i)) throw new ToolException("index '" + key + "' is not a number");
            var list = obj as IList;
            if (list != null)
            {
                if (i < 0 || i >= list.Count) throw new ToolException("index " + i + " out of range (count " + list.Count + ")");
                return list[i];
            }
            var en = obj as IEnumerable;
            if (en == null) throw new ToolException(obj.GetType().Name + " is not indexable");
            int n = 0;
            foreach (var x in en) if (n++ == i) return x;
            throw new ToolException("index " + i + " out of range (count " + n + ")");
        }

        /// <summary>Splits ".a.b[3].c" into tokens "a","b","[3]","c".</summary>
        internal static List<string> Tokens(string s)
        {
            var res = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '.') { i++; continue; }
                if (s[i] == '[')
                {
                    int j = s.IndexOf(']', i);
                    if (j < 0) throw new ToolException("missing ']' in path");
                    res.Add(s.Substring(i, j - i + 1)); i = j + 1; continue;
                }
                int k = i;
                while (k < s.Length && s[k] != '.' && s[k] != '[') k++;
                res.Add(s.Substring(i, k - i)); i = k;
            }
            return res;
        }

        static Ref Root(string path, out string rest)
        {
            rest = "";
            if (path.StartsWith("#u", StringComparison.Ordinal) || path.StartsWith("#o", StringComparison.Ordinal))
            {
                int k = 2;
                while (k < path.Length && (char.IsDigit(path[k]) || (k == 2 && path[k] == '-'))) k++;
                var o = ById(path.Substring(0, k));
                rest = path.Substring(k);
                if (!rest.StartsWith(":", StringComparison.Ordinal) || !(o is GameObject)) return new Ref { Obj = o };
                int end = rest.IndexOfAny(new[] { '.', '[' });
                var comp = FindComponent((GameObject)o, end >= 0 ? rest.Substring(1, end - 1) : rest.Substring(1), path.Substring(0, k));
                rest = end >= 0 ? rest.Substring(end) : "";
                return new Ref { Obj = comp };
            }
            if (path.StartsWith("#"))
            {
                int k = 1;
                while (k < path.Length && char.IsDigit(path[k])) k++;
                var el = UiTools.Get(int.Parse(path.Substring(1, k - 1)));
                if (el == null) throw new ToolException("UI element " + path.Substring(0, k) + " not found (stale id?)");
                rest = path.Substring(k);
                return new Ref { Obj = el };
            }
            if (path.StartsWith("@"))
            {
                // Longest dotted prefix that names a type.
                string s = path.Substring(1);
                int cut = s.IndexOf('[');
                string head = cut >= 0 ? s.Substring(0, cut) : s;
                var parts = head.Split('.');
                for (int n = parts.Length; n >= 1; n--)
                {
                    var t = FindType(string.Join(".", parts, 0, n));
                    if (t == null) continue;
                    rest = s.Substring(string.Join(".", parts, 0, n).Length);
                    return new Ref { Static = t };
                }
                throw new ToolException("no type found at the start of '" + path + "' - use types_find");
            }
            if (path.StartsWith("/"))
            {
                int colon = path.IndexOf(':');
                string goPath = colon >= 0 ? path.Substring(0, colon) : path;
                var go = FindGo(goPath);
                if (go == null) throw new ToolException("GameObject '" + goPath + "' not found - use scene_tree");
                if (colon < 0) return new Ref { Obj = go };
                string after = path.Substring(colon + 1);
                int end = after.IndexOfAny(new[] { '.', '[' });
                string compName = end >= 0 ? after.Substring(0, end) : after;
                rest = end >= 0 ? after.Substring(end) : "";
                return new Ref { Obj = FindComponent(go, compName, goPath) };
            }
            throw new ToolException("path must start with '/' (GameObject path), '@' (static type), '#u'/'#o' (object id from state_get) or '#' (UI element id). Example: /App/Services:OrderService.orders[0].status");
        }

        static Ref Walk(string path, int dropLast, out string last)
        {
            string rest;
            var cur = Root(path, out rest);
            var toks = Tokens(rest);
            last = null;
            if (dropLast > 0)
            {
                if (toks.Count == 0) throw new ToolException("path must end with a member or index");
                last = toks[toks.Count - 1]; toks.RemoveAt(toks.Count - 1);
            }
            foreach (var tok in toks)
            {
                if (cur.Static == null && cur.Obj == null) throw new ToolException("null reached before '" + tok + "' in " + path);
                object next = tok[0] == '[' ? Index(cur.Obj, tok.Substring(1, tok.Length - 2).Trim('"', '\'')) : Member(cur, tok);
                cur = new Ref { Obj = next };
            }
            return cur;
        }

        public static object Resolve(string path)
        {
            string last;
            var r = Walk(path, 0, out last);
            return r.Static != null ? (object)r.Static : r.Obj;
        }

        // ---------------- formatting ----------------

        internal static bool Simple(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(TimeSpan) || t == typeof(Guid)
                   || (t.IsValueType && t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal) && !typeof(IEnumerable).IsAssignableFrom(t));
        }

        /// <summary>Thread-safe, shallow description of a value (used by traces).</summary>
        public static string Short(object v)
        {
            if (v == null) return "null";
            var t = v.GetType();
            if (v is string) return "\"" + Util.OneLine((string)v, 120) + "\"";
            if (Simple(t)) return v.ToString();
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null)) return Driver.OnMainThread ? (uo == null ? "destroyed " + t.Name : t.Name + " \"" + uo.name + "\"") : t.Name;
            var col = v as ICollection;
            if (col != null) return TypeName(t) + "(count " + col.Count + ")";
            return TypeName(t);
        }

        /// <summary>Short() plus the object id, for values the caller may want to open next.</summary>
        internal static string Brief(object v)
        {
            string id = v == null || Simple(v.GetType()) ? null : IdOf(v);
            return id == null ? Short(v) : Short(v) + " " + id;
        }

        sealed class Opts
        {
            public int Limit = 30;
            public bool Props;
            public string Members = "all";   // all | public | serialized
        }

        sealed class Page { public int Offset, Limit, Total = -1, Shown; public string Filter; }

        struct Entry { public string Label, Error; public object Value; }

        public static string Format(object v, int depth, int maxItems, bool props)
        {
            var sb = new StringBuilder();
            Format(sb, v, new Opts { Limit = maxItems, Props = props }, depth, null, 0, null);
            return sb.ToString();
        }

        /// <summary>Collections do not consume a depth level; objects do. 'fields' applies at the first object level.</summary>
        static void Format(StringBuilder sb, object v, Opts o, int depth, string[] fields, int indent, Page page)
        {
            if (sb.Length > 20000 || indent > 14) { sb.Append("..."); return; }
            if (Header(sb, v) || depth <= 0) return;
            bool obj = !(v is IEnumerable) || v is Transform;
            string pad = new string(' ', (indent + 1) * 2);
            int n = 0, shown = 0, from = page != null ? page.Offset : 0, limit = page != null ? page.Limit : obj ? 80 : o.Limit;
            bool more = false;
            foreach (var e in Entries(v, o, fields))
            {
                if (page != null && page.Filter != null && !Util.Contains(e.Label, page.Filter) && (e.Error != null || !Util.Contains(Line(e.Value, false), page.Filter))) continue;
                if (n++ < from) continue;
                if (shown >= limit)
                {
                    more = true;
                    if (page == null) break;
                    continue;
                }
                shown++;
                sb.Append('\n').Append(pad).Append(e.Label).Append(" = ");
                if (e.Error != null) sb.Append("<error: ").Append(e.Error).Append('>');
                else Format(sb, e.Value, o, obj ? depth - 1 : depth, obj ? null : fields, indent + 1, null);
            }
            if (page != null) { page.Total = n; page.Shown = shown; }
            else if (more) sb.Append('\n').Append(pad).Append(v is ICollection ? "... " + (((ICollection)v).Count - limit) + " more" : "... more");
        }

        /// <summary>Type, count and id; true when that already says everything (simple, null, destroyed or hidden).</summary>
        static bool Header(StringBuilder sb, object v, bool ids = true)
        {
            if (v == null) { sb.Append("null"); return true; }
            var t = v.GetType();
            if (v is Type) { sb.Append("type ").Append(((Type)v).FullName); return true; }
            if (v is string || Simple(t)) { sb.Append(Short(v)); return true; }
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) { sb.Append("destroyed ").Append(t.Name); return true; }
            bool obj = !(v is IEnumerable) || v is Transform;
            sb.Append(!ReferenceEquals(uo, null) ? t.Name + " \"" + uo.name + "\"" : TypeName(t));
            if (!obj && v is ICollection) sb.Append(" count=").Append(((ICollection)v).Count);
            string id = ids ? IdOf(v) : null;
            if (id != null) sb.Append(' ').Append(id);
            if (obj && Why(t, "") != null) { sb.Append(" (contents hidden by the safety policy)"); return true; }
            return false;
        }

        static IEnumerable<Entry> Entries(object v, Opts o, string[] fields)
        {
            var dict = v as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry de in dict) yield return new Entry { Label = "[" + Short(de.Key) + "]", Value = de.Value };
                yield break;
            }
            var en = v as IEnumerable;
            if (en != null && !(v is Transform))
            {
                int i = 0;
                foreach (var x in en) yield return new Entry { Label = "[" + i++ + "]", Value = x };
                yield break;
            }
            if (fields != null)
            {
                foreach (var f in fields)
                {
                    var e = new Entry { Label = f };
                    try { e.Value = Pick(v, Tokens(f)); if (e.Value == Missing) e.Error = "no field/property '" + f + "'"; }
                    catch (Exception ex) { e.Error = (ex.InnerException ?? ex).Message; }
                    yield return e;
                }
                yield break;
            }
            for (var c = v.GetType(); c != null && c != typeof(object) && c != typeof(MonoBehaviour) && c != typeof(Behaviour) && c != typeof(Component) && c != typeof(UnityEngine.Object); c = c.BaseType)
            {
                foreach (var f in c.GetFields(AllDeclared))
                {
                    if (f.IsStatic || f.Name.IndexOf('<') >= 0 && !f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)) continue;
                    if (o.Members == "public" && !f.IsPublic) continue;
                    if (o.Members == "serialized" && (f.IsInitOnly || f.IsNotSerialized || !f.IsPublic && !f.IsDefined(typeof(SerializeField), true) && !f.IsDefined(typeof(SerializeReference), true))) continue;
                    var e = new Entry { Label = f.Name.StartsWith("<", StringComparison.Ordinal) ? f.Name.Substring(1, f.Name.IndexOf('>') - 1) : f.Name };
                    try { e.Value = f.GetValue(v); } catch (Exception ex) { e.Error = ex.Message; }
                    yield return e;
                }
                if (!o.Props) continue;
                foreach (var p in c.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    var e = new Entry { Label = p.Name + " (prop)" };
                    try { e.Value = p.GetValue(v); } catch (Exception ex) { e.Error = (ex.InnerException ?? ex).Message; }
                    yield return e;
                }
            }
        }

        // ---------------- field paths inside collection items ----------------

        internal static readonly object Missing = new object();
        static readonly Dictionary<KeyValuePair<Type, string>, MemberInfo> Getters = new Dictionary<KeyValuePair<Type, string>, MemberInfo>();

        /// <summary>Instance field or property by name, looked up and safety-checked once per type; Missing when the type has none.</summary>
        static object Get1(object x, string name)
        {
            var key = new KeyValuePair<Type, string>(x.GetType(), name);
            MemberInfo m;
            lock (Getters)
            {
                if (!Getters.TryGetValue(key, out m))
                {
                    m = Readable(key.Key, name);
                    if (m != null) Guard(m, "reading");
                    Getters[key] = m;
                }
            }
            if (m == null) return Missing;
            var fi = m as FieldInfo;
            return fi != null ? fi.GetValue(fi.IsStatic ? null : x) : ((PropertyInfo)m).GetValue(x);
        }

        /// <summary>Follows tokens of a relative path ("owner.name", "items[0]"); no tokens means the value itself.</summary>
        internal static object Pick(object x, List<string> toks)
        {
            foreach (var tok in toks)
            {
                if (x == null || x == Missing) return x;
                x = tok[0] == '[' ? Index(x, tok.Substring(1, tok.Length - 2).Trim('"', '\'')) : Get1(x, tok);
            }
            return x;
        }

        static double? Number(object v)
        {
            if (v == null || v is string || v is bool || v is char || v.GetType().IsEnum || !(v is IConvertible)) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        internal static string Text(object v)
        {
            if (v == null) return "null";
            if (v is string) return (string)v;
            var f = v as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString();
        }

        static int Compare(object x, object y)
        {
            if (x == y) return 0;
            if (x == null || x == Missing) return -1;
            if (y == null || y == Missing) return 1;
            double? a = Number(x), b = Number(y);
            double d;
            if (a != null && b == null && y is string && double.TryParse((string)y, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) b = d;
            if (b != null && a == null && x is string && double.TryParse((string)x, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) a = d;
            if (a != null && b != null) return a.Value.CompareTo(b.Value);
            return string.Compare(Text(x), Text(y), StringComparison.OrdinalIgnoreCase);
        }

        internal static readonly string[] Ops = { "==", "!=", "<", "<=", ">", ">=", "contains" };

        /// <summary>Compares a value with a JSON value: numbers numerically, everything else as text ignoring case.</summary>
        internal static bool Test(object v, string op, JToken want)
        {
            if (v == Missing) return false;
            if (v is UnityEngine.Object && (UnityEngine.Object)v == null) v = null;
            object w = want == null || want.Type == JTokenType.Null ? null : want is JValue ? ((JValue)want).Value : want.ToString(Newtonsoft.Json.Formatting.None);
            if (op == "contains")
            {
                if (v == null || w == null) return false;
                var en = v as IEnumerable;
                if (en != null && !(v is string)) { foreach (var x in en) if (Compare(x, w) == 0) return true; return false; }
                return Util.Contains(Text(v), Text(w));
            }
            if (v == null || w == null) return op == "==" ? v == w : op == "!=" && v != w;
            int c = Compare(v, w);
            switch (op)
            {
                case "==": return c == 0;
                case "!=": return c != 0;
                case "<": return c < 0;
                case "<=": return c <= 0;
                case ">": return c > 0;
                case ">=": return c >= 0;
            }
            throw new ToolException("op must be one of " + string.Join(" ", Ops));
        }

        sealed class Cond { public string Field, Op; public List<string> Toks; public JToken Value; public int Missed; }

        internal static void CheckOp(string op)
        {
            if (Array.IndexOf(Ops, op) < 0) throw new ToolException("op '" + op + "' is not supported; use one of " + string.Join(" ", Ops));
        }

        static Cond ParseCond(JToken t)
        {
            var o = t as JObject;
            if (o == null || o.Str("field") == null) throw new ToolException("each condition is {\"field\":\"hp\",\"op\":\"<\",\"value\":10} (field \".\" is the item itself)");
            var c = new Cond { Field = o.Str("field"), Op = o.Str("op", "=="), Value = o["value"] };
            CheckOp(c.Op);
            c.Toks = Tokens(c.Field);
            return c;
        }

        // ---------------- walking big collections without freezing the game ----------------

        internal const double FrameBudgetMs = 8;

        internal sealed class Scan { public int Count, Frames; public string Partial; }

        /// <summary>Visits items a few milliseconds per frame; visit returns false to stop early.</summary>
        internal static IEnumerator Visit(object src, Func<object, int, bool> visit, Scan s)
        {
            var list = src as IList;
            var it = list == null ? ((IEnumerable)src).GetEnumerator() : null;
            var slice = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; ; i++)
            {
                object x;
                if (list != null)
                {
                    if (i >= list.Count) break;
                    x = list[i];
                }
                else
                {
                    bool next;
                    try { next = it.MoveNext(); }
                    catch (InvalidOperationException) { s.Partial = "the collection changed during the scan; results cover the first " + i + " item(s)"; break; }
                    if (!next) break;
                    x = it.Current;
                }
                s.Count++;
                if (!visit(x, i)) break;
                if (slice.Elapsed.TotalMilliseconds > FrameBudgetMs) { s.Frames++; yield return null; slice.Reset(); slice.Start(); }
            }
        }

        static object Collection(string path)
        {
            var src = Resolve(path);
            if (src == null || src is string || !(src is IEnumerable) || src is Transform)
                throw new ToolException(path + " is " + (src == null ? "null" : "a " + TypeName(src.GetType())) + ", not a collection - point path at a list, array, set or dictionary");
            return src;
        }

        /// <summary>One-line view of an item: simple values as is, objects as their first members.</summary>
        internal static string Line(object x, bool ids = true)
        {
            var sb = new StringBuilder();
            if (Header(sb, x, ids) || !(!(x is IEnumerable) || x is Transform)) return sb.ToString();
            int n = 0;
            sb.Append(" {");
            foreach (var e in Entries(x, new Opts(), null))
            {
                if (n++ >= 8) { sb.Append(", ..."); break; }
                sb.Append(n > 1 ? ", " : "").Append(e.Label).Append('=').Append(e.Error != null ? "<error>" : Short(e.Value));
            }
            return Util.OneLine(sb.Append('}').ToString(), 400);
        }

        sealed class Stat
        {
            public int Count, Numbers; public double Min = double.MaxValue, Max = double.MinValue, Sum;
            public readonly HashSet<string> Strings = new HashSet<string>();
            public void Add(object v)
            {
                if (v == Missing) return;
                Count++;
                double? d = Number(v);
                if (d != null) { Numbers++; Sum += d.Value; if (d.Value < Min) Min = d.Value; if (d.Value > Max) Max = d.Value; }
                else if (v is string || v is Enum || v is bool || v is char) Strings.Add(Text(v));
            }
            public string Describe()
            {
                var s = Count + " value(s)";
                if (Numbers > 0) s += "; numbers " + Numbers + ": min " + Min.ToString("0.###", CultureInfo.InvariantCulture) + ", max " + Max.ToString("0.###", CultureInfo.InvariantCulture) + ", avg " + (Sum / Numbers).ToString("0.###", CultureInfo.InvariantCulture);
                if (Strings.Count > 0) s += "; text: " + Strings.Count + " unique";
                return s;
            }
        }

        static IEnumerator QueryTool(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            if (path == null) throw new ToolException("path is required, e.g. /Game:Inventory.items");
            var src = Collection(path);
            var where = new List<Cond>();
            if (a["where"] is JArray) foreach (var w in (JArray)a["where"]) where.Add(ParseCond(w));
            else if (a["where"] != null) where.Add(ParseCond(a["where"]));
            var select = a.Strs("select");
            var selToks = new List<List<string>>();
            if (select != null) foreach (var f in select) selToks.Add(Tokens(f));
            string sort = a.Str("sort"), group = a.Str("group");
            bool desc = sort != null && sort.StartsWith("-", StringComparison.Ordinal);
            var sortToks = sort != null ? Tokens(sort.TrimStart('-', '+')) : null;
            var groupToks = group != null ? Tokens(group) : null;
            bool stats = a.Bool("stats");
            int limit = Math.Max(0, a.Int("limit", 20)), offset = Math.Max(0, a.Int("offset", 0));

            var keep = new List<KeyValuePair<object, KeyValuePair<int, object>>>();   // sort key -> (index, item)
            var groups = new Dictionary<string, int>();
            var types = new Dictionary<string, int>();
            var stat = new List<Stat>();
            for (int i = 0; i < Math.Max(1, selToks.Count); i++) stat.Add(new Stat());
            int matched = 0;
            bool early = false;
            var scan = new Scan();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            yield return Visit(src, (x, i) =>
            {
                foreach (var c in where)
                {
                    var v = Pick(x, c.Toks);
                    if (v == Missing) c.Missed++;
                    if (!Test(v, c.Op, c.Value)) return true;
                }
                matched++;
                if (sortToks != null) keep.Add(new KeyValuePair<object, KeyValuePair<int, object>>(Pick(x, sortToks), new KeyValuePair<int, object>(i, x)));
                else if (matched > offset && keep.Count < limit) keep.Add(new KeyValuePair<object, KeyValuePair<int, object>>(null, new KeyValuePair<int, object>(i, x)));
                if (groupToks != null)
                {
                    var g = Pick(x, groupToks);
                    string k = g == Missing ? "(missing)" : g == null || Simple(g.GetType()) ? Text(g) : Brief(g);
                    int n; groups.TryGetValue(k, out n); groups[k] = n + 1;
                }
                if (stats)
                {
                    string tn = x == null ? "null" : TypeName(x.GetType());
                    int n; types.TryGetValue(tn, out n); types[tn] = n + 1;
                    if (selToks.Count == 0) stat[0].Add(x);
                    else for (int s = 0; s < selToks.Count; s++) stat[s].Add(Pick(x, selToks[s]));
                }
                early = sortToks == null && groupToks == null && !stats && keep.Count >= limit;
                return !early;
            }, scan);
            long ms = clock.ElapsedMilliseconds;

            if (sortToks != null)
            {
                keep.Sort((p, q) => desc ? Compare(q.Key, p.Key) : Compare(p.Key, q.Key));
                keep = keep.GetRange(Math.Min(offset, keep.Count), Math.Max(0, Math.Min(limit, keep.Count - offset)));
            }
            r.Text(path + ": " + (early ? "stopped after " + scan.Count + " item(s) once " + limit + " matched" : "scanned " + scan.Count + " item(s)") + ", " + (early ? "" : matched + " matched, ")
                   + ms + " ms over " + (scan.Frames + 1) + " frame(s)" + (scan.Partial != null ? " - " + scan.Partial : ""));
            foreach (var c in where) if (c.Missed > 0) r.Text("note: '" + c.Field + "' does not exist on " + c.Missed + " item(s); those never match");
            foreach (var kv in keep)
            {
                var x = kv.Value.Value;
                var sb = new StringBuilder("[" + kv.Value.Key + "] ");
                if (select == null) sb.Append(Line(x));
                else
                {
                    string id = x == null || Simple(x.GetType()) ? null : IdOf(x);
                    if (id != null) sb.Append(id).Append(' ');
                    for (int s = 0; s < select.Length; s++)
                    {
                        object v; try { v = Pick(x, selToks[s]); } catch (Exception e) { v = "<error: " + e.Message + ">"; }
                        sb.Append(s > 0 ? ", " : "").Append(select[s]).Append('=').Append(v == Missing ? "<none>" : Brief(v));
                    }
                }
                r.Text(Util.OneLine(sb.ToString(), 600));
            }
            if (!early) r.Text(keep.Count == 0 ? "shown 0 of " + matched : "shown " + (offset + 1) + "-" + (offset + keep.Count) + " of " + matched + (offset + keep.Count < matched ? "; next page: offset=" + (offset + keep.Count) : ""));
            else r.Text("shown " + (offset + 1) + "-" + (offset + keep.Count) + "; more may follow: next page offset=" + (offset + keep.Count) + " (add stats=true for the full count)");
            if (groupToks != null)
            {
                var gl = new List<KeyValuePair<string, int>>(groups);
                gl.Sort((p, q) => q.Value.CompareTo(p.Value));
                r.Text("group by " + group + ": " + gl.Count + " group(s)");
                for (int i = 0; i < gl.Count && i < 30; i++) r.Text("  " + Util.OneLine(gl[i].Key, 120) + ": " + gl[i].Value);
                if (gl.Count > 30) r.Text("  ... " + (gl.Count - 30) + " more group(s)");
            }
            if (stats)
            {
                var tl = new List<string>();
                foreach (var kv in types) tl.Add(kv.Key + " x" + kv.Value);
                r.Text("stats: count " + matched + "; types: " + string.Join(", ", tl));
                if (selToks.Count == 0) r.Text("  items: " + stat[0].Describe());
                else for (int s = 0; s < select.Length; s++) r.Text("  " + select[s] + ": " + stat[s].Describe());
            }
        }

        // ---------------- export ----------------

        const long ExportCap = 200L * 1024 * 1024;

        internal static JToken Json(object v, int depth)
        {
            if (v == null) return JValue.CreateNull();
            var t = v.GetType();
            if (v is string || t.IsPrimitive && !(v is IntPtr || v is UIntPtr) || v is decimal) return new JValue(v);
            if (Simple(t)) return new JValue(Text(v));
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null)) return uo == null ? new JObject { ["$destroyed"] = t.Name } : new JObject { ["$ref"] = IdOf(uo), ["$type"] = t.Name, ["name"] = uo.name };
            string hidden = v is IEnumerable ? null : Why(t, "");
            if (hidden != null) return new JObject { ["$type"] = TypeName(t), ["$hidden"] = hidden };
            if (depth <= 0) return new JObject { ["$type"] = TypeName(t) };
            var dict = v as IDictionary;
            if (dict != null)
            {
                var o = new JObject();
                foreach (DictionaryEntry de in dict) o[Text(de.Key)] = Json(de.Value, depth - 1);
                return o;
            }
            var en = v as IEnumerable;
            if (en != null && !(v is Transform))
            {
                var arr = new JArray();
                foreach (var x in en) { if (arr.Count >= 10000) { arr.Add("... more"); break; } arr.Add(Json(x, depth - 1)); }
                return arr;
            }
            var res = new JObject();
            foreach (var e in Entries(v, new Opts(), null))
            {
                JToken j;
                try { j = e.Error != null ? new JObject { ["$error"] = e.Error } : Json(e.Value, depth - 1); }
                catch (Exception ex) { j = new JObject { ["$error"] = ex.Message }; }
                res[e.Label] = j;
            }
            return res;
        }

        internal static string ExportDir()
        {
            string dir = Path.Combine(Driver.HomeDir(), "exports");
            Sandbox.Own(() => Directory.CreateDirectory(dir));
            Driver.OwnerOnly(dir, true);
            return dir;
        }

        /// <summary>The most bytes ~/.udriver/exports may hold: UDRIVER_EXPORTS_MAX_MB, 2048 by default.</summary>
        internal static long ExportLimit()
        {
            long mb;
            return long.TryParse(Environment.GetEnvironmentVariable("UDRIVER_EXPORTS_MAX_MB"), out mb) && mb > 0 ? mb * 1048576 : 2048L * 1048576;
        }

        /// <summary>Deletes the oldest files, never keep, until the folder has room for reserve more bytes besides keep.</summary>
        internal static void MakeRoom(string dir, long reserve, string keep = null)
        {
            FileInfo[] files = null;
            Sandbox.Own(() => files = new DirectoryInfo(dir).GetFiles());
            Array.Sort(files, (x, y) => x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc));
            long total = 0, limit = ExportLimit();
            foreach (var f in files) if (f.FullName != keep) total += f.Length;
            foreach (var f in files)
            {
                if (total + reserve <= limit) break;
                if (f.FullName == keep) continue;
                try { Sandbox.Own(f.Delete); total -= f.Length; } catch { }
            }
        }

        static IEnumerator ExportTool(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            if (path == null) throw new ToolException("path is required, e.g. /Game:Inventory.items");
            var src = Collection(path);
            int depth = Mathf.Clamp(a.Int("depth", 2), 0, 4);
            string dir = ExportDir();
            long limit = ExportLimit(), cap = Math.Min(ExportCap, limit);
            MakeRoom(dir, cap);
            string file = Path.Combine(dir, "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture).Substring(0, 6) + ".jsonl");
            var dictSrc = src as IDictionary;
            var first = new List<string>();
            long size = 0;
            bool full = false;
            var scan = new Scan();
            bool written = false;
            try
            {
                FileStream created = null;
                Sandbox.Own(() => created = new FileStream(file, FileMode.CreateNew, FileAccess.Write));
                using (var fs = created)
                {
                    Driver.OwnerOnly(file, false);
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        var entries = new List<object>();
                        if (dictSrc != null) foreach (DictionaryEntry de in dictSrc) entries.Add(de);
                        yield return Visit(dictSrc != null ? entries : src, (x, i) =>
                        {
                            string line = (dictSrc != null
                                ? new JObject { ["key"] = Json(((DictionaryEntry)x).Key, depth), ["value"] = Json(((DictionaryEntry)x).Value, depth) }
                                : Json(x, depth)).ToString(Newtonsoft.Json.Formatting.None);
                            long bytes = Encoding.UTF8.GetByteCount(line) + 1;
                            if (size + bytes > cap) { full = true; return false; }
                            w.Write(line); w.Write('\n');
                            size += bytes;
                            if (first.Count < 3) first.Add(line);
                            return true;
                        }, scan);
                    }
                }
                written = size > 0;
            }
            finally { if (!written) Sandbox.Own(() => File.Delete(file)); }
            if (size == 0 && !full) { r.Text("nothing was exported: " + path + (scan.Partial != null ? " - " + scan.Partial : " is empty")); yield break; }
            if (full && cap < ExportCap)
            {
                Sandbox.Own(() => File.Delete(file));
                throw new ToolException("the collection is bigger than " + limit / 1048576 + " MB, the most the exports folder may hold (the owner sets it with UDRIVER_EXPORTS_MAX_MB); nothing was written. Export a smaller part or use a lower depth");
            }
            MakeRoom(dir, size, file);
            int lines = scan.Count - (full ? 1 : 0);
            r.Text("exported " + lines + " line(s), " + (size >= 1048576 ? (size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (size / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB") + " to " + file
                   + (full ? " - stopped at the 200 MB limit" : "") + (scan.Partial != null ? " - " + scan.Partial : ""));
            foreach (var l in first) r.Text("  " + Util.Cut(l, 300));
        }

        // ---------------- tools ----------------

        static void Get(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            if (path == null) throw new ToolException("path is required");
            var o = new Opts { Limit = Math.Max(1, a.Int("limit", a.Int("maxItems", 30))), Props = a.Bool("props"), Members = a.Str("members", "all") };
            if (o.Members != "all" && o.Members != "public" && o.Members != "serialized") throw new ToolException("members must be all, public or serialized");
            var page = new Page { Offset = Math.Max(0, a.Int("offset", 0)), Limit = o.Limit, Filter = a.Str("filter") };
            var sb = new StringBuilder(path).Append(" = ");
            int depth = Mathf.Clamp(a.Int("depth", 1), 0, 4);
            var v = Resolve(path);
            Format(sb, v, o, depth == 0 && a.Strs("fields") != null ? 1 : depth, a.Strs("fields"), 0, page);
            bool cut = sb.Length > 20000;
            r.Text(cut ? sb.ToString(0, 20000) + "\n..." : sb.ToString());
            if (page.Total < 0) { r.Text("shown 1-1 of 1"); return; }
            r.Text((page.Shown == 0 ? "shown 0 of " + page.Total : "shown " + (page.Offset + 1) + "-" + (page.Offset + page.Shown) + " of " + page.Total)
                   + (page.Filter != null ? " matching '" + page.Filter + "'" : "")
                   + (page.Offset + page.Shown < page.Total ? "; next page: offset=" + (page.Offset + page.Shown) : "")
                   + (cut ? "; output cut at 20000 characters - use fields, limit or a lower depth" : ""));
        }

        static void Set(JObject a, ToolResponse r)
        {
            string path = a.Str("path"), last;
            if (path == null || a["value"] == null) throw new ToolException("path and value are required");
            var parent = Walk(path, 1, out last);
            var val = a["value"];
            if (last[0] == '[')
            {
                string key = last.Substring(1, last.Length - 2).Trim('"', '\'');
                var list = parent.Obj as IList; var dict = parent.Obj as IDictionary;
                if (list != null) { if (!list.GetType().IsArray) GuardItem(list, typeof(IList)); var et = list.GetType().IsArray ? list.GetType().GetElementType() : list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object); list[int.Parse(key)] = Util.To(val, et); }
                else if (dict != null && dict.GetType().IsGenericType) { GuardItem(dict, typeof(IDictionary)); var ga = dict.GetType().GetGenericArguments(); dict[Convert.ChangeType(key, ga[0])] = Util.To(val, ga[1]); }
                else throw new ToolException("target is not a list or dictionary");
            }
            else
            {
                var type = parent.Static ?? parent.Obj.GetType();
                bool done = false;
                for (var t = type; t != null && !done; t = t.BaseType)
                {
                    var f = t.GetField(last, AllDeclared);
                    if (f != null) { Guard(f, "state_set"); f.SetValue(On(parent, last, f.IsStatic), Util.To(val, f.FieldType)); done = true; break; }
                    var p = t.GetProperty(last, AllDeclared);
                    if (p != null && p.GetSetMethod(true) != null) { Guard(p, "state_set"); p.SetValue(On(parent, last, p.GetSetMethod(true).IsStatic), Util.To(val, p.PropertyType)); done = true; }
                }
                if (!done) throw new ToolException("no writable field/property '" + last + "' on " + type.FullName);
            }
            r.Text(path + " = " + Format(Resolve(path), 1, 30, false));
        }

        static void Call(JObject a, ToolResponse r)
        {
            string path = a.Str("path"), method = a.Str("method"), last;
            if (path == null || method == null) throw new ToolException("path and method are required");
            var target = Walk(path, 0, out last);
            if (target.Static == null && target.Obj == null) throw new ToolException("target is null");
            var args = a["args"] as JArray ?? new JArray();
            var types = a.Strs("types");
            var type = target.Static ?? target.Obj.GetType();
            var seen = new List<string>();
            var fits = new List<KeyValuePair<int, MethodInfo>>();
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(AllDeclared))
                {
                    if (m.Name != method || seen.Contains(Signature(m))) continue;   // an override hides the base declaration
                    seen.Add(Signature(m));
                    var ps = m.GetParameters();
                    if (ps.Length != args.Count || (target.Static != null && !m.IsStatic)) continue;
                    if (types != null && (types.Length != ps.Length || !TypesMatch(ps, types))) continue;
                    int score = 0;
                    for (int i = 0; i < ps.Length && score >= 0; i++) { int f = Fit(args[i], ps[i].ParameterType); score = f < 0 ? -1 : score + f; }
                    if (score >= 0) fits.Add(new KeyValuePair<int, MethodInfo>(score, m));
                }
            }
            if (seen.Count == 0) throw new ToolException("no method '" + method + "' on " + type.FullName);
            fits.Sort((x, y) => y.Key.CompareTo(x.Key));
            if (fits.Count > 1 && fits[0].Key == fits[1].Key)
            {
                var tied = new List<string>();
                foreach (var f in fits) if (f.Key == fits[0].Key) tied.Add(Signature(f.Value));
                throw new ToolException("the arguments fit several overloads of '" + method + "' equally: " + string.Join("; ", tied) + ". Pick one with types, e.g. types=[\"string\"].");
            }
            string refused = null;
            foreach (var f in fits)
            {
                var m = f.Value;
                var ps = m.GetParameters();
                Guard(m, "state_call");   // before the arguments are built: converting them can already run code
                object[] argv = new object[ps.Length];
                try { for (int i = 0; i < ps.Length; i++) argv[i] = Util.To(args[i], ps[i].ParameterType); }
                catch (ToolException e) { refused = e.Message; continue; }
                catch { continue; }
                object res = m.Invoke(m.IsStatic ? null : target.Obj, argv);
                r.Text("called " + Signature(m) + " -> " + (m.ReturnType == typeof(void) ? "void" : Format(res, 1, 30, false)));
                return;
            }
            throw new ToolException("no overload of '" + method + "' fits these " + args.Count + " argument(s)" + (types != null ? " and types" : "") + ". Overloads: " + string.Join("; ", seen) + (refused != null ? ". " + refused : ""));
        }

        static bool TypesMatch(ParameterInfo[] ps, string[] types)
        {
            for (int i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                if (!string.Equals(types[i], TypeName(t), StringComparison.OrdinalIgnoreCase) && !string.Equals(types[i], t.Name, StringComparison.OrdinalIgnoreCase)
                    && types[i] != t.FullName) return false;
            }
            return true;
        }

        /// <summary>How well a JSON argument fits a parameter type: -1 never, 0 only by conversion, higher is closer.</summary>
        static int Fit(JToken arg, Type p)
        {
            var u = Nullable.GetUnderlyingType(p) ?? p;
            bool number = u.IsPrimitive && u != typeof(bool) && u != typeof(char) && u != typeof(IntPtr) && u != typeof(UIntPtr) || u == typeof(decimal);
            switch (arg.Type)
            {
                case JTokenType.Null: return !p.IsValueType || u != p ? 2 : -1;
                case JTokenType.Integer: return u == typeof(int) ? 4 : u == typeof(long) ? 3 : number || u.IsEnum ? 2 : u == typeof(object) ? 1 : -1;
                case JTokenType.Float: return u == typeof(double) ? 4 : u == typeof(float) ? 3 : u == typeof(decimal) ? 2 : u == typeof(object) ? 1 : -1;
                case JTokenType.Boolean: return u == typeof(bool) ? 4 : u == typeof(object) ? 1 : -1;
                case JTokenType.String: return u == typeof(string) ? 4 : u.IsEnum || u == typeof(char) ? 2 : u == typeof(object) ? 1 : number || u == typeof(bool) ? -1 : 0;
                case JTokenType.Array: return u.IsArray || typeof(IList).IsAssignableFrom(u) ? 3 : u == typeof(object) ? 1 : 0;
                default: return u.IsPrimitive || u == typeof(string) ? -1 : 0;
            }
        }

        internal static string Signature(MethodBase m)
        {
            var ps = new List<string>();
            foreach (var p in m.GetParameters()) ps.Add(TypeName(p.ParameterType) + " " + p.Name);
            var mi = m as MethodInfo;
            return (m.IsStatic ? "static " : "") + (mi != null ? TypeName(mi.ReturnType) + " " : "") + m.Name + "(" + string.Join(", ", ps) + ")";
        }

        static void TypesFind(JObject a, ToolResponse r)
        {
            string pattern = a.Str("pattern");
            if (pattern == null) throw new ToolException("pattern is required");
            var match = Util.Matcher(pattern);
            int limit = a.Int("limit", 40), n = 0, more = 0;
            foreach (var t in GameTypes(a.Bool("includeEngine")))
            {
                if (t.Name.IndexOf('<') >= 0 || !(match(t.FullName) || match(t.Name))) continue;
                if (n >= limit) { more++; continue; }
                n++;
                r.Text((t.FullName ?? t.Name) + "  [" + t.Assembly.GetName().Name + "]" + (t.IsAbstract && t.IsSealed ? " static" : t.IsInterface ? " interface" : t.IsEnum ? " enum" : "")
                       + (t.BaseType != null && t.BaseType != typeof(object) && !t.IsEnum ? " : " + TypeName(t.BaseType) : ""));
            }
            r.Text(n + " type(s)" + (more > 0 ? ", " + more + " more not shown - refine the pattern" : ""));
        }

        static void TypeMembers(JObject a, ToolResponse r)
        {
            string name = a.Str("type");
            var type = name != null ? FindType(name) : null;
            if (type == null) throw new ToolException("type '" + name + "' not found - use types_find");
            string contains = a.Str("contains"); bool inherited = a.Bool("inherited");
            r.Text(type.FullName + (type.BaseType != null ? " : " + TypeName(type.BaseType) : ""));
            int n = 0;
            for (var t = type; t != null && t != typeof(object) && n < 200; t = inherited ? t.BaseType : null)
            {
                if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal) && t != type) break;
                foreach (var f in t.GetFields(AllDeclared))
                {
                    if (f.Name.IndexOf('<') >= 0 || (contains != null && !Util.Contains(f.Name, contains))) continue;
                    r.Text("  field  " + (f.IsStatic ? "static " : "") + TypeName(f.FieldType) + " " + f.Name); n++;
                }
                foreach (var p in t.GetProperties(AllDeclared))
                {
                    if (contains != null && !Util.Contains(p.Name, contains)) continue;
                    r.Text("  prop   " + TypeName(p.PropertyType) + " " + p.Name + " {" + (p.CanRead ? " get;" : "") + (p.CanWrite ? " set;" : "") + " }"); n++;
                }
                foreach (var m in t.GetMethods(AllDeclared))
                {
                    if (m.IsSpecialName || m.Name.IndexOf('<') >= 0 || (contains != null && !Util.Contains(m.Name, contains))) continue;
                    r.Text("  method " + Signature(m)); n++;
                }
            }
            r.Text(n + " member(s)" + (inherited ? "" : " declared on this type (inherited=true for base types)"));
        }

        static void SceneTree(JObject a, ToolResponse r)
        {
            int depth = a.Int("depth", 3), max = a.Int("maxNodes", 200), count = 0, skipped = 0;
            bool activeOnly = a.Bool("activeOnly", true);
            var sb = new StringBuilder();
            Action<Transform, int> walk = null;
            walk = (t, d) =>
            {
                var go = t.gameObject;
                if (activeOnly && !go.activeInHierarchy) return;
                if (count >= max) { skipped++; return; }
                count++;
                var comps = new List<string>();
                foreach (var c in go.GetComponents<Component>()) if (c != null && !(c is Transform)) comps.Add(c.GetType().Name);
                sb.Append(' ', d * 2).Append(go.name);
                if (comps.Count > 0) sb.Append(" [").Append(string.Join(", ", comps)).Append(']');
                if (!go.activeInHierarchy) sb.Append(" (inactive)");
                if (d >= depth && t.childCount > 0) sb.Append(" (+").Append(t.childCount).Append(" children)");
                sb.Append('\n');
                if (d < depth) for (int i = 0; i < t.childCount; i++) walk(t.GetChild(i), d + 1);
            };
            string root = a.Str("root");
            if (root != null)
            {
                var go = FindGo(root);
                if (go == null) throw new ToolException("GameObject '" + root + "' not found");
                walk(go.transform, 0);
            }
            else foreach (var go in RootObjects()) walk(go.transform, 0);
            r.Text(sb.ToString().TrimEnd());
            if (skipped > 0) r.Text("... " + skipped + " more object(s) not shown (maxNodes) - use root to go deeper");
            r.Text("Read state with state_get, e.g. /ObjectName:ComponentType.field");
        }
    }
}

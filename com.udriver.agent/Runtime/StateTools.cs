using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UDriver
{
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
        static string Why(Type t, string memberName)
        {
            if (Driver.AllowUnsafe || t == null) return null;
            string full = (t.FullName ?? t.Name), ns = t.Namespace ?? "";
            if (ns == "UDriver" || ns.StartsWith("UDriver.", StringComparison.Ordinal)) return "driver internals";
            if (!IsEngine(t.Assembly)) return null; // the game's own code and its plugins
            if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) return "UnityEditor API (use the editor_* tools)";
            if (ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal) || ns.StartsWith("Unity.", StringComparison.Ordinal) || ns == "TMPro")
            {
                string member = full + "." + memberName;
                foreach (var d in DeniedUnity) if (member.StartsWith(d, StringComparison.Ordinal)) return "Unity API that reaches outside the game (files, network, devices, OS)";
                return null;
            }
            if (t.IsPrimitive || t.IsEnum || SafeSystemTypes.Contains(full) || full.StartsWith("System.Tuple", StringComparison.Ordinal)
                || full.StartsWith("System.ValueTuple", StringComparison.Ordinal) || full.StartsWith("System.Nullable", StringComparison.Ordinal)) return null;
            if (ns.StartsWith("System.Linq.Expressions", StringComparison.Ordinal)) return "code generation API";
            foreach (var a in SafeSystemNamespaces) if (ns == a || ns.StartsWith(a + ".", StringComparison.Ordinal)) return null;
            return "system API outside the safe list (files, processes, network, environment, reflection, threading...)";
        }

        /// <summary>
        /// Keeps reflection inside the game: no file/process/network/environment/reflection APIs, no UnityEditor API,
        /// no driver internals - for reading, writing and calling alike - unless a human set UDRIVER_UNSAFE=1.
        /// </summary>
        internal static void Guard(MemberInfo m, string op)
        {
            string why = Why(m.DeclaringType, m.Name);
            if (why == null) return;
            throw new ToolException(op + " on " + (m.DeclaringType.FullName ?? m.DeclaringType.Name) + "." + m.Name + " is blocked by the safety policy: " + why
                                    + ". Only a human can lift this, by starting the game/editor with environment variable UDRIVER_UNSAFE=1.");
        }

        // ---------------- types ----------------

        static bool IsEngine(Assembly asm)
        {
            string n = asm.GetName().Name;
            foreach (var p in EnginePrefixes) if (n.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        static Type[] TypesOf(Assembly asm)
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

        static string TypeName(Type t)
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

        static IEnumerable<GameObject> RootObjects()
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
                    r.Text("world (camera '" + cam.name + "', bounds test, distance " + hits[i].Key.ToString("0.##") + "): " + PathOf(hits[i].Value.gameObject) + " [" + hits[i].Value.GetType().Name + "]");
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
            r.Text("screen point (camera '" + cam.name + "'): x=" + Mathf.RoundToInt(sp.x) + " y=" + Mathf.RoundToInt(Screen.height - sp.y) + " depth=" + sp.z.ToString("0.##")
                   + (sp.z <= 0 ? " - BEHIND the camera" : on ? " - on screen" : " - outside the screen")
                   + ". Click it with ui_click x,y method=input (may be occluded: check screen_pick).");
        }

        static GameObject FindGo(string path)
        {
            var parts = path.Trim('/').Split('/');
            foreach (var root in RootObjects())
            {
                if (root.name != parts[0]) continue;
                var t = root.transform;
                for (int i = 1; i < parts.Length && t != null; i++) t = t.Find(parts[i]);
                if (t != null) return t.gameObject;
            }
            return null;
        }

        static object Member(Ref r, string name)
        {
            var type = r.Static ?? r.Obj.GetType();
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, AllDeclared);
                if (f != null) { Guard(f, "reading"); return f.GetValue(f.IsStatic ? null : r.Obj); }
                var p = t.GetProperty(name, AllDeclared);
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null) { Guard(p, "reading"); return p.GetValue(p.GetGetMethod(true).IsStatic ? null : r.Obj); }
            }
            throw new ToolException("no field/property '" + name + "' on " + type.FullName + " - use type_members to list them");
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
        static List<string> Tokens(string s)
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
                foreach (var c in go.GetComponents<Component>())
                    if (c != null && (c.GetType().Name == compName || c.GetType().FullName == compName)) return new Ref { Obj = c };
                throw new ToolException("component '" + compName + "' not found on " + goPath);
            }
            throw new ToolException("path must start with '/' (GameObject path), '@' (static type) or '#' (UI element id). Example: /App/Services:OrderService.orders[0].status");
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

        static bool Simple(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(TimeSpan) || t == typeof(Guid)
                   || (t.IsValueType && t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal));
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

        public static string Format(object v, int depth, int maxItems, bool props)
        {
            var sb = new StringBuilder();
            Format(sb, v, depth, maxItems, props, 0);
            return sb.ToString();
        }

        static void Format(StringBuilder sb, object v, int depth, int maxItems, bool props, int indent)
        {
            if (sb.Length > 20000 || indent > 14) { sb.Append("..."); return; }
            if (v == null) { sb.Append("null"); return; }
            var t = v.GetType();
            if (v is Type) { sb.Append("type ").Append(((Type)v).FullName); return; }
            if (v is string || Simple(t)) { sb.Append(Short(v)); return; }
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) { sb.Append("destroyed ").Append(t.Name); return; }
            string pad = new string(' ', (indent + 1) * 2);

            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append(TypeName(t)).Append(" count=").Append(dict.Count);
                if (depth <= 0) return;
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ >= maxItems) { sb.Append('\n').Append(pad).Append("... ").Append(dict.Count - maxItems).Append(" more"); break; }
                    sb.Append('\n').Append(pad).Append('[').Append(Short(e.Key)).Append("] = ");
                    Format(sb, e.Value, depth, maxItems, props, indent + 1);   // collections do not consume a depth level
                }
                return;
            }
            var en = v as IEnumerable;
            if (en != null && !(v is Transform))
            {
                var col = v as ICollection;
                sb.Append(TypeName(t));
                if (col != null) sb.Append(" count=").Append(col.Count);
                if (depth <= 0) return;
                int n = 0;
                foreach (var x in en)
                {
                    if (n >= maxItems) { sb.Append('\n').Append(pad).Append("... more"); break; }
                    sb.Append('\n').Append(pad).Append('[').Append(n++).Append("] = ");
                    Format(sb, x, depth, maxItems, props, indent + 1);
                }
                return;
            }

            sb.Append(!ReferenceEquals(uo, null) ? t.Name + " \"" + uo.name + "\"" : TypeName(t));
            if (depth <= 0) return;
            if (Why(t, "") != null) { sb.Append(" (contents hidden by the safety policy)"); return; }
            int count = 0;
            for (var c = t; c != null && c != typeof(object) && c != typeof(MonoBehaviour) && c != typeof(Behaviour) && c != typeof(Component) && c != typeof(UnityEngine.Object); c = c.BaseType)
            {
                foreach (var f in c.GetFields(AllDeclared))
                {
                    if (f.IsStatic || f.Name.IndexOf('<') >= 0 && !f.Name.EndsWith("k__BackingField", StringComparison.Ordinal)) continue;
                    if (count++ >= 80) break;
                    string name = f.Name.StartsWith("<", StringComparison.Ordinal) ? f.Name.Substring(1, f.Name.IndexOf('>') - 1) : f.Name;
                    sb.Append('\n').Append(pad).Append(name).Append(" = ");
                    object fv;
                    try { fv = f.GetValue(v); } catch (Exception e) { sb.Append("<error: ").Append(e.Message).Append('>'); continue; }
                    Format(sb, fv, depth - 1, maxItems, props, indent + 1);
                }
                if (!props) continue;
                foreach (var p in c.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0 || count++ >= 120) continue;
                    sb.Append('\n').Append(pad).Append(p.Name).Append(" (prop) = ");
                    object pv;
                    try { pv = p.GetValue(v); } catch (Exception e) { sb.Append("<error: ").Append((e.InnerException ?? e).Message).Append('>'); continue; }
                    Format(sb, pv, depth - 1, maxItems, props, indent + 1);
                }
            }
        }

        // ---------------- tools ----------------

        static void Get(JObject a, ToolResponse r)
        {
            string path = a.Str("path");
            if (path == null) throw new ToolException("path is required");
            r.Text(path + " = " + Format(Resolve(path), a.Int("depth", 1), a.Int("maxItems", 30), a.Bool("props")));
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
                if (list != null) { var et = list.GetType().IsArray ? list.GetType().GetElementType() : list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object); list[int.Parse(key)] = Util.To(val, et); }
                else if (dict != null && dict.GetType().IsGenericType) { var ga = dict.GetType().GetGenericArguments(); dict[Convert.ChangeType(key, ga[0])] = Util.To(val, ga[1]); }
                else throw new ToolException("target is not a list or dictionary");
            }
            else
            {
                var type = parent.Static ?? parent.Obj.GetType();
                bool done = false;
                for (var t = type; t != null && !done; t = t.BaseType)
                {
                    var f = t.GetField(last, AllDeclared);
                    if (f != null) { Guard(f, "state_set"); f.SetValue(f.IsStatic ? null : parent.Obj, Util.To(val, f.FieldType)); done = true; break; }
                    var p = t.GetProperty(last, AllDeclared);
                    if (p != null && p.GetSetMethod(true) != null) { Guard(p, "state_set"); p.SetValue(p.GetSetMethod(true).IsStatic ? null : parent.Obj, Util.To(val, p.PropertyType)); done = true; }
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
            var type = target.Static ?? target.Obj.GetType();
            var seen = new List<string>();
            string refused = null;
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(AllDeclared))
                {
                    if (m.Name != method) continue;
                    var ps = m.GetParameters();
                    seen.Add(Signature(m));
                    if (ps.Length != args.Count || (target.Static != null && !m.IsStatic)) continue;
                    Guard(m, "state_call");   // before the arguments are built: converting them can already run code
                    object[] argv = new object[ps.Length];
                    try { for (int i = 0; i < ps.Length; i++) argv[i] = Util.To(args[i], ps[i].ParameterType); }
                    catch (ToolException e) { refused = e.Message; continue; }
                    catch { continue; }
                    object res = m.Invoke(m.IsStatic ? null : target.Obj, argv);
                    r.Text("called " + Signature(m) + " -> " + (m.ReturnType == typeof(void) ? "void" : Format(res, 1, 30, false)));
                    return;
                }
            }
            throw new ToolException(seen.Count == 0 ? "no method '" + method + "' on " + type.FullName : "no overload of '" + method + "' fits " + args.Count + " argument(s). Overloads: " + string.Join("; ", seen) + (refused != null ? ". " + refused : ""));
        }

        static string Signature(MethodBase m)
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

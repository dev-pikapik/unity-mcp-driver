namespace UDriver
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Reflection;
    using System.Text;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using UnityEngine;
    using UnityEngine.UIElements;

    /// <summary>query (selectors over the live object graph), chart_data (numeric series behind charts), form_fill (fill a form by its labels).</summary>
    internal static class QueryTools
    {
        const int MaxNodes = 100000;

        internal static void Register()
        {
            Driver.Register("query", Query);
            Driver.Register("chart_data", Driver.Sync(ChartData));
            Driver.Register("form_fill", Fill);
        }

        /// <summary>A value plus how it was reached, so every result has an id that works as a state path.</summary>
        internal sealed class Node
        {
            public object V;
            public Node Up;
            public string Seg, Path;

            public string Id()
            {
                if (Path != null) return Path;
                string up = Up != null ? Up.Id() : null;
                bool structural = V == null || AppMap.IsColl(V) || V.GetType().IsValueType || V is string;
                return Path = (structural ? null : StateTools.IdOf(V)) ?? (up != null ? up + Seg : StateTools.IdOf(V));
            }
        }

        // ---------------- selector parsing ----------------

        sealed class Cmp { public List<string> Toks; public string Op; public JToken Value; }

        sealed class Step { public char Kind; public string Name; public int A, B; public bool HasA, HasB; public List<List<Cmp>> Or; }

        sealed class Sel { public char Kind; public string Root; public readonly List<Step> Steps = new List<Step>(); }

        sealed class Parser
        {
            static readonly string[] OpList = { "==", "!=", "<=", ">=", "~=", "<", ">", "=" };
            readonly string s;
            int i;

            internal Parser(string text) { s = text; }

            ToolException Err(string msg, int at)
            {
                at = Math.Min(at, s.Length);
                return new ToolException("select: " + msg + " at position " + (at + 1) + "\n  " + s + "\n  " + new string(' ', at) + "^");
            }

            static bool Ident(char c) { return char.IsLetterOrDigit(c) || c == '_'; }
            void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            bool At(string t) { return string.CompareOrdinal(s, i, t, 0, t.Length) == 0; }

            internal Sel Parse()
            {
                var q = new Sel();
                Ws();
                if (i >= s.Length) throw Err("empty selector", 0);
                int start = i;
                char c = s[i];
                if (c == '/' || c == '@' || c == '#')
                {
                    while (i < s.Length && s[i] != '[' && !At("..")) i++;
                    q.Kind = 'p'; q.Root = s.Substring(start, i - start).Trim();
                }
                else if (Ident(c) || c == '*' || c == '?')
                {
                    while (i < s.Length && (Ident(s[i]) || s[i] == '*' || s[i] == '?' || (s[i] == '.' && !At("..")))) i++;
                    q.Kind = 't'; q.Root = s.Substring(start, i - start).TrimEnd('.');
                }
                else throw Err("a selector starts with a type name, *, /Object/Path, @Type.static or #id", i);
                while (true)
                {
                    Ws();
                    if (i >= s.Length) return q;
                    if (At("..")) { i += 2; q.Steps.Add(new Step { Kind = 'd', Name = Name("..") }); }
                    else if (s[i] == '.') { i++; q.Steps.Add(new Step { Kind = '.', Name = Name(".") }); }
                    else if (s[i] == '[') q.Steps.Add(Bracket());
                    else throw Err("unexpected '" + s[i] + "' (steps are .member, ..member, [*], [n], [a:b], [\"key\"], [?condition])", i);
                }
            }

            string Name(string after)
            {
                int st = i;
                while (i < s.Length && Ident(s[i])) i++;
                if (i == st) throw Err("expected a member name after '" + after + "'", i);
                return s.Substring(st, i - st);
            }

            Step Bracket()
            {
                int open = i++;
                Ws();
                if (i >= s.Length) throw Err("missing ']'", open);
                Step st;
                if (s[i] == '*') { i++; st = new Step { Kind = '*' }; }
                else if (s[i] == '?') { i++; st = new Step { Kind = '?', Or = Or() }; }
                else if (s[i] == '"' || s[i] == '\'') st = new Step { Kind = 'k', Name = Quoted() };
                else
                {
                    st = new Step { Kind = 'i' };
                    st.HasA = Int(out st.A);
                    Ws();
                    if (i < s.Length && s[i] == ':') { i++; Ws(); st.Kind = ':'; st.HasB = Int(out st.B); }
                    else if (!st.HasA) throw Err("expected *, ?condition, a number, a:b or \"key\" inside [ ]", i);
                }
                Ws();
                if (i >= s.Length || s[i] != ']') throw Err("expected ']'", i);
                i++;
                return st;
            }

            bool Int(out int v)
            {
                int st = i;
                if (i < s.Length && s[i] == '-') i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                v = 0;
                if (i == st || (i == st + 1 && s[st] == '-')) { i = st; return false; }
                if (!int.TryParse(s.Substring(st, i - st), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) throw Err("number too large", st);
                return true;
            }

            string Quoted()
            {
                char q = s[i];
                int st = i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != q) { if (s[i] == '\\' && i + 1 < s.Length) i++; sb.Append(s[i++]); }
                if (i >= s.Length) throw Err("unclosed quote", st);
                i++;
                return sb.ToString();
            }

            List<List<Cmp>> Or()
            {
                var or = new List<List<Cmp>>();
                while (true)
                {
                    var and = new List<Cmp> { Cmp() };
                    Ws();
                    while (At("&&")) { i += 2; and.Add(Cmp()); Ws(); }
                    or.Add(and);
                    if (!At("||")) return or;
                    i += 2;
                }
            }

            Cmp Cmp()
            {
                Ws();
                if (i >= s.Length || s[i] != '.') throw Err("a condition starts with .field (or . for the item itself), e.g. [?.price > 10]", i);
                int st = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && "=!<>~&|]".IndexOf(s[i]) < 0)
                {
                    if (s[i] != '[') { i++; continue; }
                    int j = s.IndexOf(']', i);
                    if (j < 0) throw Err("missing ']'", i);
                    i = j + 1;
                }
                var c = new Cmp { Toks = StateTools.Tokens(s.Substring(st, i - st)) };
                Ws();
                foreach (var op in OpList)
                    if (At(op)) { c.Op = op == "~=" ? "contains" : op == "=" ? "==" : op; i += op.Length; break; }
                if (c.Op == null)
                {
                    if (i < s.Length && s[i] != ']' && !At("&&") && !At("||")) throw Err("expected an operator (== != < <= > >= ~=), &&, || or ]", i);
                    return c;
                }
                Ws();
                c.Value = Value();
                return c;
            }

            JToken Value()
            {
                if (i < s.Length && (s[i] == '"' || s[i] == '\'')) return new JValue(Quoted());
                int st = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ']' && !At("&&") && !At("||")) i++;
                string w = s.Substring(st, i - st);
                double d;
                if (w.Length == 0 || "=!<>~&|".IndexOf(w[0]) >= 0) throw Err("expected a value (number, 'text', true, false or null)", st);
                if (w == "true" || w == "false") return new JValue(w == "true");
                if (w == "null") return JValue.CreateNull();
                return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? new JValue(d) : new JValue(w);
            }
        }

        // ---------------- roots ----------------

        static readonly Dictionary<string, List<Type>> TypeCache = new Dictionary<string, List<Type>>();
        static int _typeCacheAsm;

        /// <summary>Game types by name, full name or wildcard; an engine type (Unity objects first) only for an exact name no game type has.</summary>
        static List<Type> TypesFor(string pat)
        {
            int asm = AppDomain.CurrentDomain.GetAssemblies().Length;
            if (asm != _typeCacheAsm) { TypeCache.Clear(); _typeCacheAsm = asm; }
            List<Type> l;
            if (TypeCache.TryGetValue(pat, out l)) return l;
            l = new List<Type>();
            bool wild = pat.IndexOf('*') >= 0 || pat.IndexOf('?') >= 0;
            var m = wild ? Util.Matcher(pat) : null;
            foreach (var t in StateTools.GameTypes(false))
                if (AppMap.Game(t) && (wild ? m(t.Name) || m(t.FullName) : t.Name == pat || t.FullName == pat)) l.Add(t);
            if (l.Count == 0 && !wild)
            {
                Type best = null;
                foreach (var t in StateTools.GameTypes(true))
                    if ((t.Name == pat || t.FullName == pat) && (best == null || (!typeof(UnityEngine.Object).IsAssignableFrom(best) && typeof(UnityEngine.Object).IsAssignableFrom(t)))) best = t;
                if (best != null) l.Add(best);
            }
            l.RemoveAll(t => StateTools.Why(t, "") != null);
            return TypeCache[pat] = l;
        }

        static bool IsStatic(Type t, string name)
        {
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
            for (; t != null; t = t.BaseType) if (t.GetField(name, f) != null || t.GetProperty(name, f) != null) return true;
            return false;
        }

        static List<Node> Roots(Sel q)
        {
            var res = new List<Node>();
            if (q.Kind == 'p') { res.Add(new Node { V = StateTools.Resolve(q.Root), Path = q.Root }); return res; }
            var parts = new List<string>(q.Root.Split('.'));
            List<Type> types = null;
            int n = 1;
            if (parts[0] != "*")
            {
                for (n = parts.Count; n >= 1; n--) if ((types = TypesFor(string.Join(".", parts.GetRange(0, n).ToArray()))).Count > 0) break;
                if (n == 0)
                {
                    var near = new List<string>();
                    foreach (var t in StateTools.GameTypes(false)) if (AppMap.Game(t) && Util.Contains(t.Name, parts[0]) && near.Count < 8) near.Add(t.Name);
                    throw new ToolException("select: no type '" + parts[0] + "'" + (near.Count > 0 ? "; similar: " + string.Join(", ", near) : "")
                                            + ". Roots: TypeName, Ns.Type*, *, /Object/Path, @Type.static, #id (app_map section=scripts lists the game's types)");
                }
            }
            var rest = parts.GetRange(n, parts.Count - n);
            if (types == null) foreach (var m in AppMap.GameBehaviours()) res.Add(new Node { V = m });
            else if (types.Count == 1 && rest.Count > 0 && IsStatic(types[0], rest[0]))
            {
                string p = "@" + types[0].FullName + "." + rest[0];
                res.Add(new Node { V = StateTools.Resolve(p), Path = p });
                rest.RemoveAt(0);
            }
            else
            {
                var seen = new HashSet<object>();
                foreach (var t in types) Instances(t, res, seen);
            }
            q.Steps.InsertRange(0, rest.ConvertAll(x => new Step { Kind = '.', Name = x }));
            return res;
        }

        internal static List<Node> Instances(Type t)
        {
            var res = new List<Node>();
            Instances(t, res, new HashSet<object>());
            return res;
        }

        /// <summary>Live instances: Unity objects in loaded scenes and ScriptableObjects, objects held by static fields, and (plain classes) records held by game components, assets and statics.</summary>
        static void Instances(Type t, List<Node> into, HashSet<object> seen)
        {
            bool unity = typeof(UnityEngine.Object).IsAssignableFrom(t);
            if (unity)
            {
                var found = new List<UnityEngine.Object>();
                foreach (var o in Resources.FindObjectsOfTypeAll(t)) if (AppMap.Live(o)) found.Add(o);
                found.Sort((x, y) => string.CompareOrdinal(AppMap.Where(x) ?? x.name, AppMap.Where(y) ?? y.name));
                foreach (var o in found) if (seen.Add(o)) into.Add(new Node { V = o });
            }
            var statics = AppMap.Statics();
            foreach (var kv in statics) if (t.IsInstanceOfType(kv.Value) && seen.Add(kv.Value)) into.Add(new Node { V = kv.Value, Path = kv.Key });
            if (unity) return;
            foreach (var m in AppMap.GameBehaviours()) Held(t, new Node { V = m }, 0, into, seen);
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>()) if (so != null && AppMap.Game(so.GetType())) Held(t, new Node { V = so }, 0, into, seen);
            foreach (var kv in statics) Held(t, new Node { V = kv.Value, Path = kv.Key }, 0, into, seen);
        }

        static void Held(Type t, Node owner, int depth, List<Node> into, HashSet<object> seen)
        {
            var v = owner.V;
            if (AppMap.IsColl(v))
            {
                var et = AppMap.ElementType(v);
                if (et != null && (t.IsAssignableFrom(et) || et.IsAssignableFrom(t)))
                    foreach (var x in Expand(owner, true)) if (t.IsInstanceOfType(x.V) && seen.Add(x.V)) into.Add(x);
                return;
            }
            if (depth > 1 || !AppMap.Game(v.GetType())) return;
            foreach (var f in AppMap.Fields(v.GetType()))
            {
                object x;
                try { x = f.GetValue(v); } catch { continue; }
                if (x == null || x is UnityEngine.Object) continue;
                var n = new Node { V = x, Up = owner, Seg = "." + AppMap.Clean(f.Name) };
                if (t.IsInstanceOfType(x)) { if (seen.Add(x)) into.Add(n); }
                else if (AppMap.IsColl(x) || depth == 0) Held(t, n, depth + 1, into, seen);
            }
        }

        // ---------------- evaluation ----------------

        sealed class Ctx
        {
            readonly System.Diagnostics.Stopwatch _w = System.Diagnostics.Stopwatch.StartNew();
            public int Frames;
            public readonly List<string> Notes = new List<string>();

            public bool Tick()
            {
                if (_w.Elapsed.TotalMilliseconds <= StateTools.FrameBudgetMs) return false;
                Frames++; _w.Reset(); _w.Start();
                return true;
            }

            public void Note(string s) { if (Notes.Count < 10 && !Notes.Contains(s)) Notes.Add(s); }
        }

        static bool Dead(object v) { return v == null || (v is UnityEngine.Object && (UnityEngine.Object)v == null); }

        /// <summary>Items of a collection node; dictionary items are {Key, Value} entries (or the values).</summary>
        static List<Node> Expand(Node n, bool values)
        {
            var l = new List<Node>();
            var dict = n.V as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry e in dict) l.Add(new Node { V = values ? e.Value : e, Up = n, Seg = "[\"" + StateTools.Text(e.Key) + "\"]" });
                return l;
            }
            int i = 0;
            foreach (var x in (IEnumerable)n.V) { l.Add(new Node { V = x, Up = n, Seg = "[" + i++ + "]" }); if (i >= MaxNodes) break; }
            return l;
        }

        static void Select(List<Node> l, Step st, List<Node> into)
        {
            int n = l.Count;
            if (st.Kind == 'i')
            {
                int k = st.A < 0 ? n + st.A : st.A;
                if (k >= 0 && k < n) into.Add(l[k]);
                return;
            }
            int a = !st.HasA ? 0 : st.A < 0 ? Math.Max(0, n + st.A) : Math.Min(st.A, n);
            int b = !st.HasB ? n : st.B < 0 ? Math.Max(0, n + st.B) : Math.Min(st.B, n);
            for (int k = a; k < b; k++) into.Add(l[k]);
        }

        static bool Truthy(object v) { return v != StateTools.Missing && !Dead(v) && !(v is bool && !(bool)v); }

        static bool Match(object x, List<List<Cmp>> or, Ctx c)
        {
            foreach (var and in or)
            {
                bool ok = true;
                foreach (var cmp in and)
                {
                    object v;
                    try { v = StateTools.Pick(x, cmp.Toks); }
                    catch (ToolException e) { c.Note(e.Message); ok = false; break; }
                    if (!(cmp.Op == null ? Truthy(v) : StateTools.Test(v, cmp.Op, cmp.Value))) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        static void Member(Node n, string name, List<Node> into, Ctx c)
        {
            if (n.V is DictionaryEntry && (name == "Value" || name == "value")) { into.Add(new Node { V = ((DictionaryEntry)n.V).Value, Up = n.Up, Seg = n.Seg }); return; }
            var v = StateTools.Pick(n.V, new List<string> { name });
            if (v != StateTools.Missing) { into.Add(new Node { V = v, Up = n, Seg = "." + name }); return; }
            c.Note("no member '" + name + "' on " + StateTools.TypeName(n.V.GetType()) + (AppMap.IsColl(n.V) ? " - it is a collection, use [*]." + name : ""));
        }

        static bool Walkable(object x) { return !Dead(x) && (x is GameObject || AppMap.IsColl(x) || AppMap.Game(x.GetType())); }

        static IEnumerable<Node> Children(Node n, string skip)
        {
            var v = n.V;
            if (AppMap.IsColl(v))
            {
                foreach (var x in Expand(n, true)) if (Walkable(x.V)) yield return x;
                yield break;
            }
            var go = v as GameObject;
            if (go != null)
            {
                foreach (var m in go.GetComponents<MonoBehaviour>()) if (m != null && AppMap.Game(m.GetType())) yield return new Node { V = m };
                yield break;
            }
            if (!AppMap.Game(v.GetType())) yield break;
            foreach (var f in AppMap.Fields(v.GetType()))
            {
                string name = AppMap.Clean(f.Name);
                if (name == skip) continue;
                object x = null;
                try { x = f.GetValue(v); } catch { }
                if (Walkable(x)) yield return new Node { V = x, Up = n, Seg = "." + name };
            }
        }

        /// <summary>..name: breadth-first through game objects, their fields and collection items (depth 4).</summary>
        static void Deep(Node start, string name, List<Node> into, Ctx c)
        {
            var seen = new HashSet<object>();
            var q = new Queue<KeyValuePair<Node, int>>();
            var one = new List<string> { name };
            q.Enqueue(new KeyValuePair<Node, int>(start, 0));
            while (q.Count > 0 && into.Count < MaxNodes)
            {
                var kv = q.Dequeue();
                var v = kv.Key.V;
                if (Dead(v) || (!v.GetType().IsValueType && !seen.Add(v))) continue;
                if (seen.Count > 20000) { c.Note("..'" + name + "' stopped after 20000 objects; start from a narrower root"); return; }
                if (!AppMap.IsColl(v))
                {
                    object hit;
                    try { hit = StateTools.Pick(v, one); } catch (ToolException e) { c.Note(e.Message); hit = StateTools.Missing; }
                    if (hit != StateTools.Missing) into.Add(new Node { V = hit, Up = kv.Key, Seg = "." + name });
                }
                if (kv.Value < 4) foreach (var x in Children(kv.Key, name)) q.Enqueue(new KeyValuePair<Node, int>(x, kv.Value + 1));
            }
        }

        static IEnumerator Apply(Step st, List<Node> set, List<Node> into, Ctx c)
        {
            if (st.Kind == 'i' || st.Kind == ':')
            {
                if (set.Count == 0 || !set.TrueForAll(x => AppMap.IsColl(x.V))) { Select(set, st, into); yield break; }
                foreach (var n in set) { Select(Expand(n, false), st, into); if (c.Tick()) yield return null; }
                yield break;
            }
            foreach (var n in set)
            {
                if (into.Count >= MaxNodes) { c.Note("stopped at " + MaxNodes + " results; narrow the selector"); yield break; }
                if (c.Tick()) yield return null;
                if (Dead(n.V)) continue;
                switch (st.Kind)
                {
                    case '.': Member(n, st.Name, into, c); break;
                    case 'd': Deep(n, st.Name, into, c); break;
                    case 'k':
                        string seg = "[\"" + st.Name + "\"]";
                        into.Add(new Node { V = StateTools.Pick(n.V, new List<string> { seg }), Up = n, Seg = seg });
                        break;
                    case '*':
                        if (AppMap.IsColl(n.V)) into.AddRange(Expand(n, false));
                        else c.Note(StateTools.TypeName(n.V.GetType()) + " is not a collection; [*] skipped it");
                        break;
                    case '?':
                        if (!AppMap.IsColl(n.V)) { if (Match(n.V, st.Or, c)) into.Add(n); break; }
                        foreach (var x in Expand(n, false))
                        {
                            if (Match(x.V, st.Or, c)) into.Add(x);
                            if (c.Tick()) yield return null;
                        }
                        break;
                }
            }
        }

        // ---------------- output ----------------

        static object Val(object x, List<string> toks) { var v = StateTools.Pick(x, toks); return v == StateTools.Missing ? null : v; }

        static string Cell(object v) { return v == null ? "" : StateTools.Simple(v.GetType()) ? StateTools.Text(v) : StateTools.Short(v); }

        static string Csv(string s) { return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\""; }

        static List<string> Columns(List<Node> page)
        {
            var cols = new List<string>();
            var first = page.Find(n => !Dead(n.V));
            if (first != null && AppMap.Game(first.V.GetType()))
                foreach (var f in AppMap.Fields(first.V.GetType())) if (StateTools.Simple(f.FieldType)) cols.Add(AppMap.Clean(f.Name));
            if (cols.Count == 0) cols.Add(".");
            return cols;
        }

        static IEnumerator Query(JObject a, ToolResponse r)
        {
            string sel = a.Str("select"), format = a.Str("format", "lines");
            if (string.IsNullOrEmpty(sel)) throw new ToolException("select is required, e.g. select=\"Contact[?.company ~= 'acme']\"");
            if (format != "lines" && format != "json" && format != "csv") throw new ToolException("format must be lines, json or csv");
            var fields = a.Strs("fields") is string[] fs ? Array.ConvertAll(string.Join(",", fs).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), x => x.Trim()) : null;
            int offset = Math.Max(0, a.Int("offset", 0)), limit = Math.Max(1, Math.Min(a.Int("limit", 50), format == "lines" ? 1000 : 10000));
            var q = new Parser(sel).Parse();
            var c = new Ctx();
            var set = Roots(q);
            foreach (var st in q.Steps)
            {
                var next = new List<Node>();
                yield return Apply(st, set, next, c);
                set = next;
            }
            var page = set.GetRange(Math.Min(offset, set.Count), Math.Max(0, Math.Min(limit, set.Count - offset)));
            var cols = fields != null ? new List<string>(fields) : format == "csv" ? Columns(page) : null;
            var toks = cols != null ? cols.ConvertAll(StateTools.Tokens) : null;
            if (format == "lines")
            {
                r.Text(set.Count + " result" + (set.Count == 1 ? "" : "s") + (c.Frames > 0 ? " (scanned over " + (c.Frames + 1) + " frames)" : "") + (set.Count > 0 ? "; ids work in state_get / state_set / state_call" : ""));
                foreach (var note in c.Notes) r.Text("note: " + note);
                foreach (var n in page)
                {
                    if (toks == null) { r.Text(n.Id() + "  " + StateTools.Line(n.V, false)); continue; }
                    var parts = new List<string>();
                    for (int k = 0; k < toks.Count; k++) parts.Add(cols[k] + "=" + StateTools.Brief(Val(n.V, toks[k])));
                    r.Text(n.Id() + "  " + string.Join(", ", parts));
                }
            }
            else if (format == "json")
            {
                var arr = new JArray();
                foreach (var n in page)
                {
                    JObject o;
                    if (toks == null)
                    {
                        var j = StateTools.Json(n.V, 2);
                        o = j as JObject ?? new JObject { ["value"] = j };
                        o.Remove("$id");
                        o.AddFirst(new JProperty("$id", n.Id()));
                    }
                    else
                    {
                        o = new JObject { ["$id"] = n.Id() };
                        for (int k = 0; k < toks.Count; k++) o[cols[k]] = StateTools.Json(Val(n.V, toks[k]), 1);
                    }
                    arr.Add(o);
                }
                r.Text(arr.ToString(Formatting.None));
            }
            else
            {
                bool ids = fields == null;
                var head = cols.ConvertAll(col => Csv(col == "." ? "value" : col));
                if (ids) head.Insert(0, "id");
                var sb = new StringBuilder(string.Join(",", head));
                foreach (var n in page)
                {
                    var row = toks.ConvertAll(t => Csv(Cell(Val(n.V, t))));
                    if (ids) row.Insert(0, Csv(n.Id() ?? ""));
                    sb.Append('\n').Append(string.Join(",", row));
                }
                r.Text(sb.ToString());
            }
            if (format != "lines" && (c.Notes.Count > 0 || set.Count == 0))
            {
                r.FlushText();
                r.Text(set.Count + " results");
                foreach (var note in c.Notes) r.Text("note: " + note);
            }
            if (offset + page.Count < set.Count)
            {
                r.FlushText();
                r.Text("shown " + (page.Count == 0 ? "none" : (offset + 1) + "-" + (offset + page.Count)) + " of " + set.Count + "; next page: offset=" + (offset + page.Count));
            }
        }

        // ---------------- charts ----------------

        internal sealed class Chart { public string Id, Kind, Data; public int Points, Rank; public object Owner, Series; public List<FieldInfo> Cols; }

        static readonly string[] Keywords = { "chart", "graph", "plot", "candle", "series", "line", "ohlc", "spark" };
        static readonly string[] XNames = { "time", "t", "x", "date", "timestamp", "ts", "index", "day", "tick" };

        static bool Num(Type t) { return t == typeof(float) || t == typeof(double) || t == typeof(decimal); }

        static FieldInfo Field(Type t, params string[] names)
        {
            foreach (var n in names)
                foreach (var f in AppMap.Fields(t))
                    if (string.Equals(AppMap.Clean(f.Name), n, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>series (float/double list), xy (Vector2/3 list), candles (OHLC records), records (x + numbers), line (LineRenderer); null otherwise.</summary>
        static string SeriesKind(object v, out List<FieldInfo> cols)
        {
            cols = null;
            if (v is LineRenderer) return "line";
            if (!(v is IList) || !AppMap.IsColl(v)) return null;
            var et = AppMap.ElementType(v);
            if (et == null) return null;
            if (Num(et)) return "series";
            if (et == typeof(Vector2) || et == typeof(Vector3)) return "xy";
            if (!AppMap.Game(et) || typeof(UnityEngine.Object).IsAssignableFrom(et)) return null;
            FieldInfo x = Field(et, XNames), open = Field(et, "open", "o"), high = Field(et, "high", "h"), low = Field(et, "low", "l"), close = Field(et, "close", "c");
            if (close != null && (open != null ? 1 : 0) + (high != null ? 1 : 0) + (low != null ? 1 : 0) >= 2)
            {
                cols = new List<FieldInfo>();
                foreach (var f in new[] { x, open, high, low, close, Field(et, "volume", "vol", "v") }) if (f != null) cols.Add(f);
                return "candles";
            }
            if (x == null) return null;
            cols = new List<FieldInfo> { x };
            foreach (var f in AppMap.Fields(et))
                if (f != x && cols.Count < 5 && (Num(f.FieldType) || f.FieldType == typeof(int) || f.FieldType == typeof(long))) cols.Add(f);
            return cols.Count > 1 ? "records" : null;
        }

        static string[] Header(object v, string kind, List<FieldInfo> cols)
        {
            if (kind == "line") return new[] { "i", "x", "y", "z" };
            if (kind == "series") return new[] { "i", "value" };
            if (kind == "xy") return AppMap.ElementType(v) == typeof(Vector3) ? new[] { "x", "y", "z" } : new[] { "x", "y" };
            return cols.ConvertAll(f => AppMap.Clean(f.Name)).ToArray();
        }

        static object[] Row(object v, string kind, List<FieldInfo> cols, int i)
        {
            if (kind == "line") { var p = ((LineRenderer)v).GetPosition(i); return new object[] { i, p.x, p.y, p.z }; }
            var item = ((IList)v)[i];
            if (kind == "series") return new[] { i, item };
            if (item is Vector2) { var p = (Vector2)item; return new object[] { p.x, p.y }; }
            if (item is Vector3) { var p = (Vector3)item; return new object[] { p.x, p.y, p.z }; }
            var row = new object[cols.Count];
            for (int k = 0; k < cols.Count; k++) row[k] = item == null ? null : cols[k].GetValue(item);
            return row;
        }

        static int Count(object v) { return v is LineRenderer ? ((LineRenderer)v).positionCount : ((IList)v).Count; }

        static int Rank(string name, object owner)
        {
            foreach (var k in Keywords) if (Util.Contains(name, k)) return 0;
            return owner is VisualElement || AppMap.Is(owner.GetType(), "Graphic") ? 1 : 2;
        }

        static void Collect(object owner, object o, string id, string where, string prefix, int depth, List<Chart> into)
        {
            foreach (var f in AppMap.Fields(o.GetType()))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (v == null || v is UnityEngine.Object) continue;
                string path = prefix + "." + AppMap.Clean(f.Name);
                List<FieldInfo> cols;
                string kind = SeriesKind(v, out cols);
                if (kind != null) into.Add(new Chart { Id = id + path, Kind = kind, Owner = owner, Series = v, Cols = cols, Points = Count(v), Data = where + path, Rank = Rank(where + path, owner) });
                else if (depth == 0 && !AppMap.IsColl(v) && AppMap.Game(v.GetType())) Collect(owner, v, id, where, path, 1, into);
            }
        }

        /// <summary>LineRenderers and numeric series held by game components and UI Toolkit elements (directly or one object deeper), likeliest charts first.</summary>
        internal static List<Chart> FindCharts()
        {
            var l = new List<Chart>();
            foreach (var lr in Resources.FindObjectsOfTypeAll<LineRenderer>())
            {
                if (!AppMap.Live(lr)) continue;
                string where = StateTools.PathOf(lr.gameObject) + ":LineRenderer";
                l.Add(new Chart { Id = StateTools.IdOf(lr), Kind = "line", Owner = lr, Series = lr, Points = lr.positionCount, Data = where, Rank = Rank(where, lr) });
            }
            foreach (var m in AppMap.GameBehaviours()) Collect(m, m, StateTools.IdOf(m), StateTools.PathOf(m.gameObject) + ":" + m.GetType().Name, "", 0, l);
            foreach (var e in UiTools.All(false)) if (AppMap.Game(e.GetType())) Collect(e, e, "#" + UiTools.Id(e), UiTools.PathOf(e, 4), "", 0, l);
            l.Sort((x, y) => x.Rank != y.Rank ? x.Rank - y.Rank : string.CompareOrdinal(x.Data, y.Data));
            return l;
        }

        static void ChartData(JObject a, ToolResponse r)
        {
            string id = a.Str("id"), format = a.Str("format", "lines");
            if (format != "lines" && format != "json" && format != "csv") throw new ToolException("format must be lines, json or csv");
            if (id == null)
            {
                var charts = FindCharts();
                if (charts.Count == 0) { r.Text("no charts: no LineRenderer and no game component holding a numeric series (float/double list, Vector2/3 list, OHLC or x/y records)"); return; }
                r.Text(charts.Count + " series, likeliest charts first (chart_data id=<id> for the points; the id also works in query and state_get):");
                foreach (var c in charts)
                {
                    var last = c.Points > 0 ? Row(c.Series, c.Kind, c.Cols, c.Points - 1) : null;
                    r.Text("  " + c.Id + "  " + c.Kind + ", " + c.Points + " points" + (last != null ? ", last " + string.Join(" ", Array.ConvertAll(last, Cell)) : "") + "  data " + c.Data);
                }
                return;
            }
            var v = StateTools.Resolve(id);
            List<FieldInfo> cols;
            string kind = SeriesKind(v, out cols);
            if (kind == null) throw new ToolException(id + " is " + (v == null ? "null" : "a " + StateTools.TypeName(v.GetType())) + ", not a numeric series; chart_data without id lists them");
            var head = Header(v, kind, cols);
            int n = Count(v), from = Math.Max(0, n - Math.Max(1, a.Int("last", format == "lines" ? 100 : 10000)));
            if (format == "json")
            {
                var arr = new JArray();
                for (int i = from; i < n; i++)
                {
                    var row = Row(v, kind, cols, i);
                    var o = new JObject();
                    for (int k = 0; k < head.Length; k++) o[head[k]] = StateTools.Json(row[k], 0);
                    arr.Add(o);
                }
                r.Text(arr.ToString(Formatting.None));
                return;
            }
            if (format == "lines")
            {
                string data = null;
                foreach (var c in FindCharts()) if (c.Id == id) data = c.Data;
                r.Text(id + "  " + kind + ", " + n + " points" + (data != null ? ", data " + data : "") + (from > 0 ? "; last " + (n - from) + " shown (raise last)" : "")
                       + (kind == "line" ? "" : "; query select=\"" + id + "[-5:]\""));
            }
            var sb = new StringBuilder(string.Join(format == "csv" ? "," : "\t", head));
            for (int i = from; i < n; i++) sb.Append('\n').Append(string.Join(format == "csv" ? "," : "\t", Array.ConvertAll(Row(v, kind, cols, i), x => format == "csv" ? Csv(Cell(x)) : Cell(x))));
            r.Text(sb.ToString());
        }

        // ---------------- forms ----------------

        static readonly string[] Settable = { "field", "dropdown", "toggle", "slider" };

        static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        static int Distance(string a, string b)
        {
            var d = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) d[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int prev = d[0];
                d[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cur = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = cur;
                }
            }
            return d[b.Length];
        }

        /// <summary>Control whose label (own text, nearest label, placeholder or name) matches: exact first, then containing; else the closest labels.</summary>
        static AppMap.Item Find(List<AppMap.Item> items, string label, Predicate<AppMap.Item> ok, List<string> near)
        {
            string key = Norm(label);
            AppMap.Item part = null;
            foreach (var it in items)
            {
                if (!ok(it)) continue;
                if (label == "#" + it.Id) return it;
                foreach (var l in it.Labels)
                {
                    string n = Norm(l);
                    if (n == key) return it;
                    if (part == null && key.Length >= 3 && n.Length >= 3 && (n.Contains(key) || key.Contains(n))) part = it;
                }
            }
            if (part != null) return part;
            var scored = new List<KeyValuePair<int, string>>();
            foreach (var it in items) if (ok(it)) scored.Add(new KeyValuePair<int, string>(Distance(key, Norm(it.Label)), "\"" + it.Label + "\" (" + it.Role + " #" + it.Id + ")"));
            scored.Sort((x, y) => x.Key.CompareTo(y.Key));
            foreach (var s in scored) if (near.Count < 3 && !near.Contains(s.Value)) near.Add(s.Value);
            return null;
        }

        static JToken Prepare(AppMap.Item it, JToken v, out string err)
        {
            err = null;
            if (it.Role == "toggle" && v.Type == JTokenType.String)
            {
                string s = ((string)v).Trim().ToLowerInvariant();
                if (s == "yes" || s == "on" || s == "checked" || s == "1" || s == "true") return true;
                if (s == "no" || s == "off" || s == "unchecked" || s == "0" || s == "false") return false;
            }
            if (it.Role != "dropdown") return v;
            Component main = null;
            if (it.Go != null) AppMap.Role(it.Go, out main);
            var opts = AppMap.Options(it.Go != null ? main : (object)it.El);
            if (opts.Count == 0) return v;
            string want = v.ToString();
            int idx = opts.FindIndex(o => string.Equals(o, want, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = opts.FindIndex(o => Util.Contains(o, want));
            if (idx < 0 && v.Type == JTokenType.Integer && it.Go != null) return v;
            if (idx < 0) { err = "no option \"" + want + "\"; options: " + string.Join(", ", opts); return null; }
            return it.Go != null ? new JValue(idx) : new JValue(opts[idx]);
        }

        static string Quote(string s) { return s == null ? "-" : "\"" + Util.OneLine(s, 60) + "\""; }

        static IEnumerator Fill(JObject a, ToolResponse r)
        {
            var fields = a["fields"] as JObject;
            if (fields == null || fields.Count == 0) throw new ToolException("fields must be an object {\"label\": value}, e.g. {\"Email\": \"a@b.c\", \"Country\": \"France\", \"Subscribe\": true}");
            string root = a.Str("root"), submit = a.Str("submit");
            var all = AppMap.Items(false);
            var items = root == null ? all : all.FindAll(it => Util.Contains(it.Screen, root) || Util.Contains(it.Panel, root) || Util.Contains(it.Path, root));
            if (items.Count == 0) throw new ToolException("no visible control under root '" + root + "' (matched against screen, panel title and object path; see app_map section=ui)");
            int failed = 0;
            foreach (var kv in fields)
            {
                var near = new List<string>();
                var it = Find(items, kv.Key, x => Array.IndexOf(Settable, x.Role) >= 0, near);
                if (it == null) { failed++; r.Text("\"" + kv.Key + "\": no matching field" + (near.Count > 0 ? "; closest: " + string.Join(", ", near) : "")); continue; }
                string desc = it.Role + " " + (it.Go != null ? "#" + it.Id + " " + it.Path : it.Path), err;
                var v = Prepare(it, kv.Value, out err);
                if (err != null) { failed++; r.Text("\"" + kv.Key + "\" -> " + desc + ": " + err); continue; }
                string old = AppMap.ValueText(it);
                var s = new ToolResponse();
                yield return Driver.Guard(() => Driver.Invoke("ui_set_value", new JObject { ["id"] = it.Id, ["value"] = v }, s), s);
                if (s.IsError) { failed++; r.Text("\"" + kv.Key + "\" -> " + desc + ": " + Util.OneLine(s.AllText().Trim(), 300)); continue; }
                r.Text("\"" + kv.Key + "\" -> " + desc + ": " + Quote(old) + " -> " + Quote(AppMap.ValueText(it)));
            }
            if (submit == null)
            {
                if (failed > 0) r.Error(failed + " of " + fields.Count + " field(s) not set");
                yield break;
            }
            if (failed > 0) { r.Error("submit \"" + submit + "\" skipped: " + failed + " field(s) not set"); yield break; }
            var close = new List<string>();
            Predicate<AppMap.Item> button = x => x.Role == "button" || x.Role == "tab";
            var b = Find(items, submit, button, close) ?? Find(all, submit, button, new List<string>());
            if (b == null) throw new ToolException("no button \"" + submit + "\"" + (close.Count > 0 ? "; closest: " + string.Join(", ", close) : ""));
            r.Text("submit: " + b.Role + " \"" + b.Label + "\" #" + b.Id);
            var sub = new ToolResponse();
            yield return Driver.Guard(() => Driver.Invoke("ui_click", new JObject { ["id"] = b.Id }, sub), sub);
            r.Append(sub);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
#if UDRV_PHYSICS
using Newtonsoft.Json.Linq;
using UnityEngine;
#endif

namespace UDriver
{
    /// <summary>Generic rules: named values read at every check, a condition over them, and actions (tool calls or events) with limits.</summary>
    public static class RuleEngine
    {
        static readonly Dictionary<string, Func<string, object>> Sources = new Dictionary<string, Func<string, object>>();

        /// <summary>A rule value written "scheme:rest" is read by this resolver on the main thread at every check.</summary>
        public static void AddSource(string scheme, Func<string, object> resolver) { Sources[scheme] = resolver; }

        internal static object Read(string path)
        {
            int c = path.IndexOf(':');
            if (c > 0 && "/@#".IndexOf(path[0]) < 0)
            {
                Func<string, object> f;
                if (Sources.TryGetValue(path.Substring(0, c), out f)) return f(path.Substring(c + 1));
                throw new ToolException("nothing reads '" + path.Substring(0, c) + ":' values in this game; use a state path like @Type.field or /Object:Component.field.");
            }
            return StateTools.Resolve(path);
        }
    }

#if UDRV_PHYSICS
    sealed class Logic
    {
        public readonly List<string> Names = new List<string>(), Paths = new List<string>();
        public readonly Dictionary<string, object> Now = new Dictionary<string, object>();
        public Func<object> When;
        public string WhenText, LastError, Done;
        public JArray Do;
        public float Cooldown, LastFire = float.NegativeInfinity;
        public int Max, Every = 1, Checks, Fires, ErrorsInRow;
        public bool Edge = true, Preset, Holds, Busy;
        int _frames;
        bool _was;

        public static Logic Parse(JObject a)
        {
            var when = (string)a["when"];
            if (string.IsNullOrWhiteSpace(when))
            {
                foreach (var k in new[] { "values", "do", "cooldown", "max", "every", "edge" })
                    if (a[k] != null) throw new ToolException(k + " needs a condition: add when, e.g. when: \"score > 10\".");
                return null;
            }
            var l = new Logic { WhenText = when };
            if (a["values"] != null)
            {
                var vs = a["values"] as JObject;
                if (vs == null) throw new ToolException("values is an object of name -> state path, e.g. {\"score\": \"@Game.score\"}.");
                foreach (var p in vs.Properties())
                {
                    if (!Parser.Word(p.Name)) throw new ToolException("value name '" + p.Name + "' must be one word of letters, digits and _, e.g. score or hp2.");
                    var path = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                    if (string.IsNullOrEmpty(path)) throw new ToolException("value '" + p.Name + "' needs a state path in quotes, e.g. \"@Game.score\".");
                    try { l.Now[p.Name] = Val(RuleEngine.Read(path)); }
                    catch (Exception e) { throw new ToolException("value '" + p.Name + "' (" + path + ") cannot be read: " + Msg(e).TrimEnd('.') + ". Fix the path (state_get shows what is there) or add the rule once the object exists."); }
                    l.Names.Add(p.Name); l.Paths.Add(path);
                }
            }
            l.When = new Parser(when, l).Run();
            if (a["do"] != null)
            {
                l.Do = a["do"] as JArray;
                if (l.Do == null || l.Do.Count == 0) throw new ToolException("do is a list of actions, e.g. [{\"tool\": \"key\", \"args\": {\"key\": \"Space\"}}, {\"emit\": \"score {score}\"}].");
                foreach (var t in l.Do)
                {
                    var o = t as JObject;
                    if (o == null || (o["tool"] == null) == (o["emit"] == null) || o["emit"] != null && o["emit"].Type != JTokenType.String || o["tool"] != null && o["tool"].Type != JTokenType.String)
                        throw new ToolException("each do item is {\"tool\": \"name\", \"args\": {...}} or {\"emit\": \"text\"}.");
                    var tool = (string)o["tool"];
                    if (tool == null) continue;
                    if (tool == "rule_add" || tool == "rule_remove" || tool == "batch") throw new ToolException(tool + " cannot run inside a rule; list the calls in do one by one, and add or remove rules from the agent.");
                    if (!Driver.Knows(tool)) throw new ToolException("no tool '" + tool + "' in this game; use a tool name like key, ui_click or state_call.");
                    if (o["args"] != null && !(o["args"] is JObject)) throw new ToolException("args of " + tool + " is an object, e.g. {\"key\": \"Space\"}.");
                }
            }
            l.Cooldown = a.Num("cooldown"); l.Max = a.Int("max"); l.Every = a.Int("every", 1); l.Edge = a.Bool("edge", true);
            if (l.Cooldown < 0 || l.Max < 0 || l.Every < 1) throw new ToolException("cooldown and max are 0 or more (0 = none), every is 1 or more.");
            return l;
        }

        public void Tick(Rule r, UDriverPilot pilot)
        {
            if (Done != null || ++_frames % Every != 0) return;
            Checks++;
            for (int i = 0; i < Names.Count; i++)
            {
                try { Now[Names[i]] = Val(RuleEngine.Read(Paths[i])); }
                catch (Exception e) { Now[Names[i]] = double.NaN; LastError = "value " + Names[i] + ": " + Msg(e); }
            }
            bool on;
            try { on = Truthy(When()); }
            catch (Exception e) { on = false; LastError = "when: " + Msg(e); }
            bool fire = on && (!Edge || !_was);
            _was = Holds = on;
            if (!fire || Do == null || Busy || Time.unscaledTime - LastFire < Cooldown) return;
            Fires++;
            LastFire = Time.unscaledTime;
            if (Max > 0 && Fires >= Max) Done = "done: reached max " + Max + " fires";
            pilot.StartCoroutine(Run(r, Fires, Time.realtimeSinceStartupAsDouble, Shown(), new Dictionary<string, object>(Now)));
        }

        IEnumerator Run(Rule r, int k, double startedAt, string shown, Dictionary<string, object> now)
        {
            Busy = true;
            var done = new List<string>();
            string error = null;
            foreach (JObject o in Do)
            {
                var emit = (string)o["emit"];
                if (emit != null)
                {
                    foreach (var kv in now) emit = emit.Replace("{" + kv.Key + "}", Show(kv.Value));
                    EventStore.Emit("autoplay", r.Name + ": " + emit);
                    done.Add("emit ok");
                    continue;
                }
                var tool = (string)o["tool"];
                var args = (JObject)(o["args"] ?? new JObject()).DeepClone();
                var res = new ToolResponse();
                var it = Driver.Guard(() => Driver.Invoke(tool, args, res), res);
                while (it.MoveNext()) yield return it.Current;
                if (!res.IsError) { done.Add(tool + " ok"); continue; }
                var text = res.AllText();
                int at = text.IndexOf("ERROR: ", StringComparison.Ordinal);
                error = tool + " failed: " + Util.OneLine((at >= 0 ? text.Substring(at + 7) : text).Trim(), 200);
                done.Add(error);
                break;
            }
            Busy = false;
            EventStore.Emit("autoplay", "rule " + r.Name + " fired #" + k + " at " + startedAt.ToString("0.000", CultureInfo.InvariantCulture) + "s" + (shown.Length > 0 ? ": " + shown : "") + " -> " + string.Join(", ", done));
            if (error == null) { ErrorsInRow = 0; yield break; }
            LastError = error;
            if (++ErrorsInRow < 3 || Done != null) yield break;
            Done = "stopped: its actions failed 3 fires in a row; fix them and add the rule again";
            EventStore.Emit("autoplay", "rule " + r.Name + " " + Done + " (" + error + ")");
        }

        string Shown() => string.Join(" ", Names.Select(n => n + "=" + Show(Now[n])));

        public string Report()
        {
            var sb = new StringBuilder(", when ").Append(WhenText).Append(": checks ").Append(Checks).Append(", fires ").Append(Fires);
            if (Fires > 0) sb.Append(" (last ").Append((Time.unscaledTime - LastFire).ToString("0.0")).Append(" s ago)");
            if (Names.Count > 0) sb.Append(", values ").Append(Shown());
            if (LastError != null) sb.Append(", last error: ").Append(LastError);
            if (Done != null) sb.Append(". ").Append(Done);
            return sb.ToString();
        }

        static string Msg(Exception e)
        {
            if (e is System.Reflection.TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            return e is ToolException ? e.Message : e.GetType().Name + ": " + e.Message;
        }

        internal static object Val(object v)
        {
            if (v == null || v is UnityEngine.Object u && u == null) return double.NaN;
            if (v is bool b) return b ? 1.0 : 0.0;
            if (v is string || v is Enum || v is char) return v.ToString();
            if (v.GetType().IsPrimitive || v is decimal) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return v.ToString();
        }

        internal static double Num(object v)
        {
            if (v is double d) return d;
            double x;
            return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : double.NaN;
        }

        internal static bool Truthy(object v) => v is string s ? s.Length > 0 : !double.IsNaN((double)v) && (double)v != 0;

        internal static string Show(object v) => v is double d ? (double.IsNaN(d) ? "none" : d.ToString("0.###", CultureInfo.InvariantCulture)) : (string)v;
    }

    /// <summary>Compiles a when expression to closures; each function call keeps its own history.</summary>
    sealed class Parser
    {
        static readonly Dictionary<string, string> Usage = new Dictionary<string, string>
        {
            { "prev", "prev(x)" }, { "delta", "delta(x)" }, { "ma", "ma(x, 20)" }, { "min", "min(x, 20)" }, { "max", "max(x, 20)" },
            { "crosses_above", "crosses_above(a, b)" }, { "crosses_below", "crosses_below(a, b)" }, { "changed", "changed(x)" },
            { "rose", "rose(x)" }, { "fell", "fell(x)" }, { "abs", "abs(x)" },
            { "dist", "dist(\"Zombie\")" }, { "count", "count(\"Zombie\", 10)" }, { "seen", "seen(\"Zombie\", 10)" },
        };
        readonly string _s;
        readonly Logic _l;
        readonly HashSet<Func<object>> _consts = new HashSet<Func<object>>();
        int _i;

        public Parser(string s, Logic l) { _s = s; _l = l; }

        public static bool Word(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

        public Func<object> Run()
        {
            var e = Or();
            Skip();
            if (_i < _s.Length) throw Bad("unexpected '" + _s[_i] + "'" + (_s[_i] == '=' ? " (compare with ==)" : _s[_i] == '&' || _s[_i] == '|' ? " (use && or ||)" : ""));
            return e;
        }

        ToolException Bad(string what) => new ToolException("when \"" + _s + "\", at position " + (_i + 1) + ": " + what + ".");
        void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        bool Eat(string op)
        {
            Skip();
            if (string.CompareOrdinal(_s, _i, op, 0, op.Length) != 0) return false;
            _i += op.Length;
            return true;
        }
        static object B(bool b) => b ? 1.0 : 0.0;

        Func<object> Or()
        {
            var a = And();
            while (Eat("||")) { var x = a; var y = And(); a = () => { bool p = Logic.Truthy(x()), q = Logic.Truthy(y()); return B(p || q); }; }
            return a;
        }

        Func<object> And()
        {
            var a = Cmp();
            while (Eat("&&")) { var x = a; var y = Cmp(); a = () => { bool p = Logic.Truthy(x()), q = Logic.Truthy(y()); return B(p && q); }; }
            return a;
        }

        Func<object> Cmp()
        {
            var a = Sum();
            for (;;)
            {
                string op = Eat("==") ? "==" : Eat("!=") ? "!=" : Eat("<=") ? "<=" : Eat(">=") ? ">=" : Eat("<") ? "<" : Eat(">") ? ">" : null;
                if (op == null) return a;
                var x = a; var y = Sum();
                a = () => B(Compare(op, x(), y()));
            }
        }

        static bool Compare(string op, object p, object q)
        {
            if (op == "==" || op == "!=") return Same(p, q) == (op == "==");
            double a = Logic.Num(p), b = Logic.Num(q);
            return op == "<" ? a < b : op == "<=" ? a <= b : op == ">" ? a > b : a >= b;
        }

        static bool Same(object p, object q)
        {
            if (p is string s && q is string t) return s == t;
            double a = Logic.Num(p), b = Logic.Num(q);
            return a == b || double.IsNaN(a) && double.IsNaN(b);
        }

        Func<object> Sum()
        {
            var a = Product();
            for (;;)
            {
                bool plus = Eat("+");
                if (!plus && !Eat("-")) return a;
                var x = a; var y = Product();
                if (plus) a = () => { var p = x(); var q = y(); return p is string || q is string ? (object)(Logic.Show(p) + Logic.Show(q)) : Logic.Num(p) + Logic.Num(q); };
                else a = () => Logic.Num(x()) - Logic.Num(y());
            }
        }

        Func<object> Product()
        {
            var a = Unary();
            for (;;)
            {
                char op = Eat("*") ? '*' : Eat("/") ? '/' : Eat("%") ? '%' : ' ';
                if (op == ' ') return a;
                var x = a; var y = Unary();
                a = () => { double p = Logic.Num(x()), q = Logic.Num(y()); return op == '*' ? p * q : op == '/' ? p / q : p % q; };
            }
        }

        Func<object> Unary()
        {
            if (Eat("!")) { var x = Unary(); return () => B(!Logic.Truthy(x())); }
            if (Eat("-")) { var x = Unary(); return () => -Logic.Num(x()); }
            return Primary();
        }

        Func<object> Primary()
        {
            Skip();
            if (_i >= _s.Length) throw Bad("the expression ends too early");
            char c = _s[_i];
            if (Eat("(")) { var e = Or(); if (!Eat(")")) throw Bad("missing )"); return e; }
            if (c == '"' || c == '\'')
            {
                int end = _s.IndexOf(c, _i + 1);
                if (end < 0) throw Bad("a string is not closed");
                object str = _s.Substring(_i + 1, end - _i - 1);
                _i = end + 1;
                return Const(str);
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                double d;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { _i = start; throw Bad("not a number"); }
                return Const(d);
            }
            if (!char.IsLetter(c) && c != '_') throw Bad("unexpected '" + c + "'");
            int at = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            string name = _s.Substring(at, _i - at);
            if (name == "true" || name == "false") return Const(B(name == "true"));
            if (Eat("(")) return Call(name, at);
            if (!_l.Now.ContainsKey(name)) { _i = at; throw Bad("unknown name '" + name + "': add it to values, e.g. values: {\"" + name + "\": \"@Game.field\"}"); }
            var now = _l.Now;
            return () => now[name];
        }

        Func<object> Const(object v) { Func<object> f = () => v; _consts.Add(f); return f; }

        Func<object> Call(string f, int at)
        {
            string use;
            if (!Usage.TryGetValue(f, out use)) { _i = at; throw Bad("unknown function '" + f + "'; there are " + string.Join(", ", Usage.Keys)); }
            Skip();
            if (use.Contains('"') && (_i >= _s.Length || _s[_i] != '"' && _s[_i] != '\'')) throw Bad(f + " needs a pattern in quotes, e.g. " + use);
            var a = new List<Func<object>>();
            if (!Eat(")")) do a.Add(Or()); while (Eat(","));
            if (!Eat(")")) throw Bad("missing ) after the arguments of " + f);
            if (a.Count != use.Count(ch => ch == ',') + 1) { _i = at; throw Bad(f + " takes " + (use.Count(ch => ch == ',') + 1) + " argument(s), e.g. " + use); }
            var x = a[0];
            var y = a.Count > 1 ? a[1] : null;
            switch (f)
            {
                case "abs": return () => Math.Abs(Logic.Num(x()));
                case "prev": { object last = double.NaN; return () => { var v = x(); var p = last; last = v; return p; }; }
                case "delta": { double last = double.NaN; return () => { double v = Logic.Num(x()), d = v - last; last = v; return d; }; }
                case "changed": case "rose": case "fell":
                {
                    object last = null;
                    return () =>
                    {
                        var v = x(); var p = last; last = v;
                        if (p == null) return B(false);
                        if (f == "changed") return B(!Same(p, v));
                        double d = Logic.Num(v) - Logic.Num(p);
                        return B(f == "rose" ? d > 0 : d < 0);
                    };
                }
                case "ma": case "min": case "max":
                {
                    var q = new Queue<double>();
                    return () =>
                    {
                        double v = Logic.Num(x()), n = Logic.Num(y());
                        n = n >= 1 ? Math.Min(n, 100000) : 1;
                        if (!double.IsNaN(v)) q.Enqueue(v);
                        while (q.Count > n) q.Dequeue();
                        return q.Count < n ? double.NaN : f == "ma" ? q.Average() : f == "min" ? q.Min() : q.Max();
                    };
                }
                case "crosses_above": case "crosses_below":
                {
                    double pa = double.NaN, pb = double.NaN;
                    return () =>
                    {
                        double va = Logic.Num(x()), vb = Logic.Num(y());
                        bool hit = f == "crosses_above" ? pa <= pb && va > vb : pa >= pb && va < vb;
                        pa = va; pb = vb;
                        return B(hit);
                    };
                }
            }
            if (!_consts.Contains(x) || !(x() is string pattern)) { _i = at; throw Bad(f + " needs a pattern in quotes, e.g. " + use); }
            var m = new Matcher(pattern);
            if (f == "dist") return () =>
            {
                var b = Me();
                double best = double.PositiveInfinity;
                if (b != null) foreach (var go in AutoplayTools.Around(b, 500f)) if (m.Is(go)) best = Math.Min(best, Vector3.Distance(b.Root.position, Ent.Center(go)));
                return best;
            };
            if (f == "count") return () =>
            {
                var b = Me(); double r = Logic.Num(y());
                return b == null || !(r > 0) ? 0.0 : AutoplayTools.Around(b, (float)r).Count(m.Is);
            };
            return () =>
            {
                var b = Me(); double r = Logic.Num(y());
                if (b == null || !(r > 0)) return B(false);
                var planes = GeometryUtility.CalculateFrustumPlanes(b.Cam);
                return B(AutoplayTools.Around(b, (float)r).Any(go => m.Is(go) && AutoplayTools.Visibility(b, go, Ent.Bounds(go), planes) == "visible"));
            };
        }

        static Body Me() => UDriverPilot.I != null ? UDriverPilot.I.Me() : null;
    }
#endif
}

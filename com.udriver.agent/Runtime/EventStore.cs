using System;
using System.Globalization;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UDriver
{
    public sealed class Ev
    {
        public long Seq; public int Frame; public double Time;
        public string Cat, Text, Detail;
    }

    public sealed class Trigger
    {
        public int Id; public string Cat, Contains, Action, StatePath;
        public bool Once; public int Fired;
    }

    /// <summary>Thread-safe ring buffer of everything that happens: logs, UI events, traces, watches, triggers.</summary>
    public static class EventStore
    {
        const int Cap = 20000;
        static readonly Ev[] Buf = new Ev[Cap];
        static readonly object Lock = new object();
        static readonly HashSet<string> Disabled = new HashSet<string> { "ui.move" };
        static long _next = 1;
        static int _frame; static double _time;
        static bool _inited;

        internal static volatile Trigger[] Triggers = new Trigger[0];
        static readonly ConcurrentQueue<KeyValuePair<Trigger, Ev>> Fired = new ConcurrentQueue<KeyValuePair<Trigger, Ev>>();

        public static long LastSeq { get { lock (Lock) return _next - 1; } }

        internal static void Init()
        {
            Driver.Register("events_query", Driver.Sync(QueryTool));
            Driver.Register("events_config", Driver.Sync(ConfigTool));
            Driver.Register("errors", Driver.Sync(ErrorsTool));
            Driver.Register("wait_for", WaitFor);
            if (_inited) return;
            _inited = true;
            Application.logMessageReceivedThreaded += (msg, stack, type) =>
            {
                string cat = "log." + type.ToString().ToLowerInvariant();
                if (string.IsNullOrEmpty(stack) && (type == LogType.Error || type == LogType.Assert)) stack = CallerStack();
                Emit(cat, msg, stack);
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Group(cat, msg, stack);
            };
            SceneManager.sceneLoaded += (s, m) => Emit("scene", "loaded " + s.name + " (" + m + ")");
            SceneManager.sceneUnloaded += s => Emit("scene", "unloaded " + s.name);
        }

        static bool CatMatch(string cat, string pattern)
        {
            return cat == pattern || (cat.Length > pattern.Length && cat[pattern.Length] == '.' && cat.StartsWith(pattern, StringComparison.Ordinal));
        }

        public static bool IsDisabled(string cat)
        {
            lock (Lock) { foreach (var d in Disabled) if (CatMatch(cat, d)) return true; }
            return false;
        }

        public static void Emit(string cat, string text, string detail = null)
        {
            Ev e;
            lock (Lock)
            {
                foreach (var d in Disabled) if (CatMatch(cat, d)) return;
                e = new Ev { Seq = _next++, Frame = _frame, Time = _time, Cat = cat, Text = text ?? "", Detail = detail };
                Buf[e.Seq % Cap] = e;
            }
            if (cat == "trigger") return;
            var trs = Triggers;
            for (int i = 0; i < trs.Length; i++)
            {
                var t = trs[i];
                if (t.Cat != null && !CatMatch(cat, t.Cat)) continue;
                if (t.Contains != null && !Util.Contains(e.Text, t.Contains)) continue;
                Fired.Enqueue(new KeyValuePair<Trigger, Ev>(t, e));
            }
        }

        /// <summary>Called every frame on the main thread.</summary>
        internal static void Tick()
        {
            _frame = Time.frameCount; _time = Util.Seconds;
            KeyValuePair<Trigger, Ev> f;
            while (Fired.TryDequeue(out f))
            {
                var t = f.Key;
                if (t.Once && t.Fired > 0) continue;
                t.Fired++;
                string state = null;
                if (t.StatePath != null)
                {
                    try { state = t.StatePath + " = " + StateTools.Format(StateTools.Resolve(t.StatePath), 1, 30, false); }
                    catch (Exception e) { state = t.StatePath + " = <error: " + e.Message + ">"; }
                }
                if (t.Action == "pause") CaptureTools.Pause();
                else if (t.Action == "capture_frame") CaptureTools.RequestFrame("trigger " + t.Id);
                Emit("trigger", "trigger " + t.Id + " (" + t.Action + ") fired by #" + f.Value.Seq + " [" + f.Value.Cat + "] " + Util.OneLine(f.Value.Text, 200), state);
                if (t.Once) ProbeTools.RemoveById(t.Id);
            }
        }

        public static List<Ev> Query(long since, string[] cats, string contains, int limit, bool tail)
        {
            var res = new List<Ev>();
            lock (Lock)
            {
                long first = Math.Max(since + 1, Math.Max(1, _next - Cap));
                if (tail)
                {
                    for (long s = _next - 1; s >= first && res.Count < limit; s--) { var e = Buf[s % Cap]; if (Match(e, cats, contains)) res.Add(e); }
                    res.Reverse();
                }
                else
                {
                    for (long s = first; s < _next && res.Count < limit; s++) { var e = Buf[s % Cap]; if (Match(e, cats, contains)) res.Add(e); }
                }
            }
            return res;
        }

        /// <summary>Events after since that the ring buffer has already overwritten.</summary>
        public static long Lost(long since) { lock (Lock) return Math.Max(0, _next - Cap - (since + 1)); }

        static bool Match(Ev e, string[] cats, string contains)
        {
            if (e == null) return false;
            if (cats != null)
            {
                bool any = false;
                for (int i = 0; i < cats.Length && !any; i++) any = CatMatch(e.Cat, cats[i]);
                if (!any) return false;
            }
            return contains == null || Util.Contains(e.Text, contains);
        }

        public static string Fmt(Ev e, bool detail)
        {
            string s = "#" + e.Seq + " f" + e.Frame + " " + e.Time.ToString("0.00", CultureInfo.InvariantCulture) + "s [" + e.Cat + "] " + Util.OneLine(e.Text, 400);
            if (detail && !string.IsNullOrEmpty(e.Detail)) s += "\n    " + Util.Cut(e.Detail.TrimEnd(), 1500).Replace("\n", "\n    ");
            return s;
        }

        /// <summary>Per-category counts since a seq, plus the most recent errors.</summary>
        public static string Summary(long since)
        {
            var counts = new SortedDictionary<string, int>();
            var errors = new List<Ev>();
            foreach (var e in Query(since, null, null, Cap, false))
            {
                int c; counts.TryGetValue(e.Cat, out c); counts[e.Cat] = c + 1;
                if (e.Cat == "log.error" || e.Cat == "log.exception" || e.Cat == "log.assert") errors.Add(e);
            }
            long lost = Lost(since);
            string head = "events since #" + since + (lost > 0 ? " (" + lost + " older event(s) were overwritten before they could be read)" : "") + ": ";
            if (counts.Count == 0) return head + "none";
            var sb = new StringBuilder(head);
            foreach (var kv in counts) sb.Append(kv.Key).Append(" x").Append(kv.Value).Append("  ");
            for (int i = Math.Max(0, errors.Count - 5); i < errors.Count; i++) sb.Append("\n  ").Append(Fmt(errors[i], false));
            return sb.ToString();
        }

        // ---------------- errors grouped by type and top frame ----------------

        sealed class ErrGroup { public string Type, Top, Msg, Stack; public int Count, FirstFrame, LastFrame; public double First, Last; }

        const int MaxGroups = 500;
        static readonly Dictionary<string, ErrGroup> Groups = new Dictionary<string, ErrGroup>();
        static int _ungrouped;

        static void Group(string cat, string msg, string stack)
        {
            int colon = msg.IndexOf(':');
            string type = cat == "log.exception" && colon > 0 && msg.IndexOf(' ', 0, colon) < 0 ? msg.Substring(0, colon) : cat.Substring(4);
            string top = TopFrame(stack);
            string key = type + "|" + (top.Length > 0 ? top : Util.OneLine(msg, 200));
            lock (Lock)
            {
                ErrGroup g;
                if (!Groups.TryGetValue(key, out g))
                {
                    if (Groups.Count >= MaxGroups) { _ungrouped++; return; }
                    Groups[key] = g = new ErrGroup { Type = type, Top = top, First = _time, FirstFrame = _frame };
                }
                g.Count++; g.Last = _time; g.LastFrame = _frame; g.Msg = msg; g.Stack = stack;
            }
        }

        /// <summary>The logging code's own stack, for release builds where Unity passes none.</summary>
        static string CallerStack()
        {
            var frames = new System.Diagnostics.StackTrace(1, true).GetFrames() ?? new System.Diagnostics.StackFrame[0];
            int start = 0;
            for (int i = 0; i < frames.Length; i++)
            {
                var t = frames[i].GetMethod()?.DeclaringType;
                if (t != null && (t.FullName.StartsWith("UnityEngine.Debug", StringComparison.Ordinal) || t.FullName.StartsWith("UnityEngine.Logger", StringComparison.Ordinal))) start = i + 1;
            }
            var sb = new StringBuilder();
            for (int i = start; i < frames.Length; i++)
            {
                var m = frames[i].GetMethod();
                if (m == null || m.DeclaringType == null) continue;
                string file = frames[i].GetFileName();
                sb.Append(m.DeclaringType.FullName).Append(':').Append(m.Name).Append(file != null ? " (at " + file + ":" + frames[i].GetFileLineNumber() + ")" : "").Append('\n');
            }
            return sb.ToString();
        }

        static string TopFrame(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            foreach (var raw in stack.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("UnityEngine.Debug", StringComparison.Ordinal) || l.StartsWith("UnityEngine.Logger", StringComparison.Ordinal)
                    || l.StartsWith("Rethrow as", StringComparison.Ordinal)) continue;
                return l;
            }
            return "";
        }

        static void ErrorsTool(JObject a, ToolResponse r)
        {
            bool full = a.Bool("full");
            int limit = a.Int("limit", 20), offset = Math.Max(0, a.Int("offset", 0)), dropped;
            string contains = a.Str("contains");
            var gs = new List<ErrGroup>();
            lock (Lock)
            {
                foreach (var g in Groups.Values)
                    if (contains == null || Util.Contains(g.Type + " " + g.Msg + " " + g.Stack, contains))
                        gs.Add(new ErrGroup { Type = g.Type, Top = g.Top, Msg = g.Msg, Stack = g.Stack, Count = g.Count, FirstFrame = g.FirstFrame, LastFrame = g.LastFrame, First = g.First, Last = g.Last });
                dropped = _ungrouped;
            }
            gs.Sort((x, y) => y.Last.CompareTo(x.Last));
            int end = Math.Min(gs.Count, offset + limit);
            for (int i = offset; i < end; i++)
            {
                var g = gs[i];
                r.Text(g.Count + "x " + g.Type + (g.Top.Length > 0 ? " at " + g.Top : "") + " | first " + g.First.ToString("0.00", CultureInfo.InvariantCulture) + "s f" + g.FirstFrame
                       + ", last " + g.Last.ToString("0.00", CultureInfo.InvariantCulture) + "s f" + g.LastFrame + "\n    " + Util.OneLine(g.Msg, 400)
                       + (full && !string.IsNullOrEmpty(g.Stack) ? "\n    " + g.Stack.TrimEnd().Replace("\n", "\n    ") : ""));
            }
            r.Text((gs.Count == 0 ? "no errors" + (contains != null ? " matching '" + contains + "'" : "") + " logged since the driver started (or the last events_config clear=true)"
                       : "shown " + Math.Min(offset + 1, end) + "-" + end + " of " + gs.Count + " error group(s), newest first" + (end < gs.Count ? "; next page: offset=" + end : ""))
                   + (dropped > 0 ? "; " + dropped + " error(s) not grouped (limit " + MaxGroups + " groups)" : "")
                   + (full || gs.Count == 0 ? "" : "; full=true adds the whole stack of the latest occurrence"));
        }

        // ---------------- first-chance exceptions (every throw, even ones the game catches) ----------------

        [ThreadStatic] static bool _inFirstChance;
        static int _firstChanceLeft;
        static bool _firstChanceOn;
        static volatile bool _probeSeen;

        sealed class FirstChanceProbe : Exception { }

        static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is FirstChanceProbe) { _probeSeen = true; return; }
            if (_inFirstChance || e.Exception is ToolException) return;
            _inFirstChance = true;
            try
            {
                int left = Interlocked.Decrement(ref _firstChanceLeft);
                if (left >= 0) Emit("exception.firstchance", e.Exception.GetType().FullName + ": " + e.Exception.Message, new System.Diagnostics.StackTrace(1, true).ToString());
                if (left == 0) Emit("exception.firstchance", "buffer limit reached - capture paused; turn it on again with events_config firstChance=true");
            }
            catch { }
            finally { _inFirstChance = false; }
        }

        static string FirstChance(bool on, int max)
        {
#if ENABLE_IL2CPP
            return "first-chance exception capture is not supported in IL2CPP builds (the runtime does not raise the event). Use errors and log events instead.";
#else
            Interlocked.Exchange(ref _firstChanceLeft, on ? Math.Max(1, max) : 0);
            try
            {
                if (on && !_firstChanceOn) AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                if (!on && _firstChanceOn) AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            }
            catch (Exception e) when (e is PlatformNotSupportedException || e is NotSupportedException || e is NotImplementedException)
            {
                return "first-chance exception capture is not supported on this platform (" + e.Message + "). Use errors and log events instead.";
            }
            _firstChanceOn = on;
            if (on)
            {
                _probeSeen = false;
                try { throw new FirstChanceProbe(); } catch (FirstChanceProbe) { }
                if (!_probeSeen)
                {
                    AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
                    _firstChanceOn = false;
                    return "first-chance exception capture is not supported by this runtime (it never raises AppDomain.FirstChanceException; Unity's Mono is one such runtime). "
                           + "Exceptions the game catches stay invisible here; use errors for uncaught ones, or trace_add on the method you suspect.";
                }
            }
            return on ? "first-chance capture ON: every thrown exception (up to " + Math.Max(1, max) + ") becomes an 'exception.firstchance' event with its stack - read them with events_query categories=[\"exception.firstchance\"] detail=true"
                      : "first-chance capture OFF";
#endif
        }

        // ---------------- tools ----------------

        static void QueryTool(JObject a, ToolResponse r)
        {
            bool hasSince = a.Has("sinceSeq");
            int limit = a.Int("limit", 50);
            var evs = Query(a.Long("sinceSeq", 0), a.Strs("categories"), a.Str("contains"), limit, !hasSince);
            bool detail = a.Bool("detail");
            foreach (var e in evs) r.Text(Fmt(e, detail));
            long lost = hasSince ? Lost(a.Long("sinceSeq", 0)) : 0;
            if (lost > 0) r.Text(lost + " event(s) after #" + a.Long("sinceSeq", 0) + " were overwritten before they could be read (the log keeps the last " + Cap + ")");
            r.Text(evs.Count + " event(s); last seq = " + LastSeq
                   + (evs.Count >= limit ? (hasSince ? "; limit reached - continue with sinceSeq=" + evs[evs.Count - 1].Seq : "; limit reached - older events omitted") : ""));
        }

        static string _configBy;

        static void ConfigTool(JObject a, ToolResponse r)
        {
            string me = Driver.Current != null ? Driver.Current.Client : "";
            if (a.Has("enable") || a.Has("disable") || a.Bool("clear") || a.Has("firstChance"))
            {
                if (_configBy != null && _configBy != me) r.Text("note: the event settings were last changed by " + _configBy);
                _configBy = me;
            }
            else if (_configBy != null && _configBy != me) r.Text("last changed by " + _configBy);
            lock (Lock)
            {
                var en = a.Strs("enable"); if (en != null) foreach (var c in en) Disabled.Remove(c);
                var dis = a.Strs("disable"); if (dis != null) foreach (var c in dis) Disabled.Add(c);
                if (a.Bool("clear")) { Array.Clear(Buf, 0, Cap); Groups.Clear(); _ungrouped = 0; }
            }
            if (a.Has("firstChance")) r.Text(FirstChance(a.Bool("firstChance"), a.Int("firstChanceMax", 1000)));
            var counts = new SortedDictionary<string, int>();
            foreach (var e in Query(0, null, null, Cap, false)) { int c; counts.TryGetValue(e.Cat, out c); counts[e.Cat] = c + 1; }
            string dl; lock (Lock) dl = string.Join(", ", Disabled);
            r.Text("disabled categories: " + (dl.Length == 0 ? "(none)" : dl));
            r.Text("stored events (capacity " + Cap + "), last seq " + LastSeq + ":");
            foreach (var kv in counts) r.Text("  " + kv.Key + ": " + kv.Value);
        }

        static IEnumerator WaitFor(JObject a, ToolResponse r)
        {
            long since = a.Long("sinceSeq", LastSeq);
            float timeout = a.Int("timeoutMs", 10000) / 1000f;
            string cat = a.Str("event"), contains = a.Str("contains"), uiName = a.Str("ui_name"), uiText = a.Str("ui_text"), path = a.Str("state"), eq = a.Str("equals");
            bool gone = a.Bool("gone");
            bool ui = uiName != null || uiText != null;
            bool evCond = cat != null || (contains != null && !ui && path == null);
            int frames = a.Int("frames", 0), wait = a.Int("ms", 0);
            if (!evCond && !ui && path == null)
            {
                if (frames <= 0 && wait <= 0) throw new ToolException("give at least one condition: event/contains, ui_name/ui_text, state, frames or ms");
                for (int i = 0; i < frames; i++) yield return null;
                if (wait > 0) yield return new WaitForSecondsRealtime(wait / 1000f);
                r.Text("waited " + (frames > 0 ? frames + " frame(s)" : "") + (frames > 0 && wait > 0 ? " + " : "") + (wait > 0 ? wait + " ms" : ""));
                r.Text(Summary(since));
                yield break;
            }

            float t0 = Time.realtimeSinceStartup;
            string hit = null, lastState = null;
            while (true)
            {
                if (evCond)
                {
                    var evs = Query(since, cat == null ? null : new[] { cat }, contains, 1, false);
                    if (evs.Count > 0) hit = "event " + Fmt(evs[0], true);
                }
                if (hit == null && ui)
                {
                    var el = UiTools.FindFirst(uiName, uiText);
                    if ((el != null) != gone) hit = gone ? "element is gone" : "element present: " + UiTools.Line(el);
                }
                if (hit == null && path != null)
                {
                    bool ok = true;
                    try { lastState = StateTools.Format(StateTools.Resolve(path), 0, 10, false); }
                    catch (Exception e) { ok = false; lastState = "<error: " + e.Message + ">"; }
                    if (ok && (eq == null || string.Equals(lastState.Trim('"'), eq.Trim('"'), StringComparison.OrdinalIgnoreCase))) hit = "state " + path + " = " + lastState;
                }
                if (hit != null || Time.realtimeSinceStartup - t0 > timeout) break;
                yield return null;
            }
            int ms = (int)((Time.realtimeSinceStartup - t0) * 1000);
            r.Text(hit != null ? "MATCHED after " + ms + " ms: " + hit : "TIMEOUT after " + ms + " ms: condition not met" + (lastState != null ? " (last value of " + path + ": " + lastState + ")" : ""));
            r.Text(Summary(since));
        }
    }
}

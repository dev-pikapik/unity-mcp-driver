using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
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
            Driver.Register("wait_for", WaitFor);
            if (_inited) return;
            _inited = true;
            Application.logMessageReceivedThreaded += (msg, stack, type) => Emit("log." + type.ToString().ToLowerInvariant(), msg, stack);
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
            _frame = Time.frameCount; _time = Time.realtimeSinceStartupAsDouble;
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
            string s = "#" + e.Seq + " f" + e.Frame + " " + e.Time.ToString("0.00") + "s [" + e.Cat + "] " + Util.OneLine(e.Text, 400);
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
            if (counts.Count == 0) return "events since #" + since + ": none";
            var sb = new StringBuilder("events since #" + since + ": ");
            foreach (var kv in counts) sb.Append(kv.Key).Append(" x").Append(kv.Value).Append("  ");
            for (int i = Math.Max(0, errors.Count - 5); i < errors.Count; i++) sb.Append("\n  ").Append(Fmt(errors[i], false));
            return sb.ToString();
        }

        // ---------------- tools ----------------

        static void QueryTool(JObject a, ToolResponse r)
        {
            bool hasSince = a.Has("sinceSeq");
            int limit = a.Int("limit", 50);
            var evs = Query(a.Long("sinceSeq", 0), a.Strs("categories"), a.Str("contains"), limit, !hasSince);
            bool detail = a.Bool("detail");
            foreach (var e in evs) r.Text(Fmt(e, detail));
            r.Text(evs.Count + " event(s); last seq = " + LastSeq
                   + (evs.Count >= limit ? (hasSince ? "; limit reached - continue with sinceSeq=" + evs[evs.Count - 1].Seq : "; limit reached - older events omitted") : ""));
        }

        static void ConfigTool(JObject a, ToolResponse r)
        {
            lock (Lock)
            {
                var en = a.Strs("enable"); if (en != null) foreach (var c in en) Disabled.Remove(c);
                var dis = a.Strs("disable"); if (dis != null) foreach (var c in dis) Disabled.Add(c);
                if (a.Bool("clear")) Array.Clear(Buf, 0, Cap);
            }
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
            int frames = a.Int("frames", 0);
            if (!evCond && !ui && path == null)
            {
                if (frames <= 0) throw new ToolException("give at least one condition: event/contains, ui_name/ui_text, state, or frames");
                for (int i = 0; i < frames; i++) yield return null;
                r.Text("waited " + frames + " frame(s)");
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

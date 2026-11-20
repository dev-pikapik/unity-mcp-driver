namespace UDriver
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    #if UDRV_UGUI
    using UnityEngine.UI;
    #endif
    #if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
    using UnityEngine.InputSystem;
    using UnityEngine.InputSystem.LowLevel;
    #endif

    /// <summary>rec_start / rec_stop / rec_replay: input, agent actions, logs and state checksums, replayed frame by frame.</summary>
    public static class RecordTools
    {
        const int Every = 30, Fps = 60, RingSeconds = 120;

        sealed class Rec
        {
            public string Id;
            public int Start, Seed, PrevCapture;
            public long Seq;
            public bool Fresh;
            public DateTime When;
            public readonly List<JObject> Actions = new List<JObject>();
            public readonly List<JObject> Checks = new List<JObject>();
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            public InputEventTrace Trace;
#endif
        }

        sealed class Replay
        {
            public string Id, Result;
            public JObject File;
            public int Start, End, Next, PrevCapture;
            public bool Exact, Boot;
            public readonly List<JObject> Actions = new List<JObject>();
            public readonly Dictionary<int, JObject> Checks = new Dictionary<int, JObject>();
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            public InputEventTrace Trace;
            public InputEventTrace.ReplayController Ctl;
#endif
        }

        static Rec _rec, _ring;
        static Replay _play;
        static int _phase;
        static readonly Queue<KeyValuePair<float, int>> RingFrames = new Queue<KeyValuePair<float, int>>();

        internal static void Register()
        {
            Driver.Register("rec_start", Driver.Sync(Start));
            Driver.Register("rec_stop", Driver.Sync(Stop));
            Driver.Register("rec_replay", ReplayTool);
        }

        static string NewId() { return "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture).Substring(0, 4); }

        static Rec Begin(bool trace, int capacity)
        {
            var r = new Rec { Id = NewId(), Start = Time.frameCount, Seq = EventStore.LastSeq, When = DateTime.Now };
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            if (trace)
            {
                r.Trace = capacity > 0 ? new InputEventTrace(capacity, growBuffer: false) : new InputEventTrace(1 << 20, growBuffer: true);
                r.Trace.recordFrameMarkers = true;
                r.Trace.Enable();
            }
#endif
            return r;
        }

        static void Start(JObject a, ToolResponse r)
        {
            if (_rec != null) { r.Text(_rec.Id); r.Text("recording since frame " + _rec.Start + (_rec.Fresh ? ", the first frame of a fresh start" : "") + "; rec_stop ends it"); return; }
            if (_play != null && _play.Result == null) throw new ToolException("a replay is running; wait for it to finish");
            InputTools.EnsureDevices();
            var rec = Begin(true, 0);
            rec.Fresh = Driver.Current != null && Driver.Current.Client == "startup";
            rec.Seed = a.Has("seed") ? a.Int("seed") : Environment.TickCount;
            rec.PrevCapture = Time.captureFramerate;
            Time.captureFramerate = Fps;
            UnityEngine.Random.InitState(rec.Seed);
            _rec = rec;
            r.Text(rec.Id);
            r.Text("recording from frame " + rec.Start + " at a fixed " + Fps + " frames per second"
                   + (rec.Fresh ? " from the first frame of a fresh start, so rec_replay can repeat it exactly" : "; rec_replay of it is approximate because the game was already running"));
        }

        static void Stop(JObject a, ToolResponse r)
        {
            Rec rec = _rec;
            bool ring = rec == null;
            if (ring)
            {
                if (_ring == null) throw new ToolException("nothing is recorded yet");
                rec = _ring;
                _ring = null;
                foreach (var kv in RingFrames) { rec.Start = Math.Max(rec.Start, kv.Value); break; }
            }
            else
            {
                _rec = null;
                Time.captureFramerate = rec.PrevCapture;
            }
            string file = Save(rec, ring);
            r.Text(rec.Id);
            r.Text((ring ? "saved the last " + RingSeconds + " s (nothing was being recorded)" : "saved the recording") + ": " + file + " - rec_steps " + rec.Id + " lists what happened");
        }

        static string Save(Rec rec, bool ring)
        {
            int end = Time.frameCount - rec.Start;
            var o = new JObject
            {
                ["id"] = rec.Id, ["product"] = Application.productName, ["version"] = Application.version, ["unity"] = Application.unityVersion,
                ["build"] = Application.buildGUID, ["development"] = Debug.isDebugBuild, ["platform"] = Application.platform.ToString(),
                ["game"] = Driver.GamePath(), ["args"] = GameArgs(), ["started"] = rec.When.ToString("yyyy-MM-dd HH:mm:ss"),
                ["exact"] = rec.Fresh, ["ring"] = ring, ["seed"] = rec.Seed, ["fps"] = Fps, ["every"] = Every, ["frames"] = end,
            };
            var devices = new JArray();
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            if (rec.Trace != null)
            {
                rec.Trace.Disable();
                using (var ms = new MemoryStream()) { rec.Trace.WriteTo(ms); o["trace"] = Convert.ToBase64String(ms.ToArray()); }
                rec.Trace.Dispose();
            }
            foreach (var d in InputSystem.devices)
                if (d.name.StartsWith("UDriver", StringComparison.Ordinal)) devices.Add(new JObject { ["id"] = d.deviceId, ["name"] = d.name, ["layout"] = d.layout });
#endif
            o["devices"] = devices;
            var acts = new JArray();
            foreach (var x in rec.Actions)
                if ((int)x["frame"] >= rec.Start && x["input"] != null)
                    acts.Add(new JObject { ["f"] = (int)x["frame"] - rec.Start, ["p"] = x["p"], ["tool"] = x["tool"], ["args"] = x["args"], ["input"] = x["input"], ["said"] = x["said"] });
            o["actions"] = acts;
            o["checks"] = new JArray(rec.Checks.ToArray());
            var events = new JArray();
            foreach (var e in EventStore.Query(rec.Seq, null, null, 5000, true))
                if (e.Frame >= rec.Start && (e.Cat.StartsWith("log.", StringComparison.Ordinal) || e.Cat.StartsWith("exception", StringComparison.Ordinal) || e.Cat == "scene" || e.Cat == "ui.click" || e.Cat == "ui.submit" || e.Cat == "ui.change"))
                    events.Add(new JObject { ["f"] = e.Frame - rec.Start, ["cat"] = e.Cat, ["text"] = e.Text });
            o["events"] = events;
            o["steps"] = Steps(o);
            string dir = StateTools.ExportDir();
            string json = o.ToString(Formatting.None);
            StateTools.MakeRoom(dir, json.Length);
            string file = Path.Combine(dir, rec.Id + ".json");
            Sandbox.Own(() => File.WriteAllText(file, json));
            Driver.OwnerOnly(file, false);
            StateTools.MakeRoom(dir, 0, file);
            return file;
        }

        static JArray GameArgs()
        {
            var res = new JArray();
            var argv = Environment.GetCommandLineArgs();
            for (int i = 1; i < argv.Length; i++)
            {
                if (argv[i].StartsWith("-udriver-", StringComparison.Ordinal) || argv[i] == "-logFile") { i++; continue; }
                if (argv[i].StartsWith("-psn_", StringComparison.Ordinal)) continue;
                res.Add(argv[i]);
            }
            return res;
        }

        static JArray Steps(JObject o)
        {
            var lines = new List<KeyValuePair<int, string>>();
            foreach (var x in (JArray)o["actions"])
                lines.Add(new KeyValuePair<int, string>((int)x["f"], (string)x["tool"] + " " + ((JObject)x["args"]).ToString(Formatting.None) + ((string)x["said"] is string s && s.Length > 0 ? " -> " + s : "")));
            foreach (var x in (JArray)o["events"])
                if ((string)x["cat"] != "ui.click" || ((JArray)o["actions"]).Count == 0)
                    lines.Add(new KeyValuePair<int, string>((int)x["f"], (string)x["cat"] + ": " + (string)x["text"]));
            lines.Sort((p, q) => p.Key.CompareTo(q.Key));
            var res = new JArray();
            res.Add((bool)o["ring"] ? "The last " + RingSeconds + " s of " + o["product"] + " (saved without rec_start; input and actions only, replay is approximate)."
                    : "Recording of " + o["product"] + " " + o["version"] + " started " + o["started"] + ": " + o["frames"] + " frames at " + Fps + " fps (" + ((int)o["frames"] / (float)Fps).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s), random seed " + o["seed"]
                      + ((bool)o["exact"] ? ", from a fresh start of the game." : ", in a game that was already running."));
            if (o["trace"] != null) res.Add("Keyboard, mouse and gamepad input is in the recording and replays frame by frame.");
            foreach (var l in lines) res.Add("frame " + l.Key + " (" + (l.Key / (float)Fps).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s): " + l.Value);
            res.Add("end at frame " + o["frames"] + "; " + ((JArray)o["checks"]).Count + " state checksums to compare on replay.");
            return res;
        }

        // ---------------- per-frame hooks ----------------

        /// <summary>Called by DriverHost before it runs queued calls: 0 Update, 1 LateUpdate, 2 end of frame.</summary>
        internal static void Tick(int phase)
        {
            _phase = phase;
            if (phase == 0)
            {
                if (_ring == null) _ring = Begin(true, 2 << 20);
                if (RingFrames.Count == 0 || Time.realtimeSinceStartup - LastKey() >= 1f)
                {
                    RingFrames.Enqueue(new KeyValuePair<float, int>(Time.realtimeSinceStartup, Time.frameCount));
                    while (RingFrames.Count > RingSeconds + 1) RingFrames.Dequeue();
                    float cut = Time.realtimeSinceStartup - RingSeconds;
                    while (_ring.Actions.Count > 0 && (float)_ring.Actions[0]["t"] < cut) _ring.Actions.RemoveAt(0);
                }
            }
            if (phase == 1 && _rec != null && (Time.frameCount - _rec.Start) % Every == 0) _rec.Checks.Add(Check(Time.frameCount - _rec.Start));
            if (_play != null && _play.Result == null) Step(phase);
        }

        static float LastKey() { float t = 0; foreach (var kv in RingFrames) t = kv.Key; return t; }

        /// <summary>Notes an agent input call (InputTools.WithLease); End finishes the entry.</summary>
        internal static JObject Action(string tool, JObject a)
        {
            if (_rec == null && _ring == null) return null;
            var args = (JObject)a.DeepClone();
            foreach (var p in new[] { "", "to_" })
                if (args.Has(p + "id"))
                    try
                    {
                        var t = UiTools.Resolve(a, p);
                        args.Remove(p + "id");
                        if (t.Go != null) args[p + "path"] = StateTools.PathOf(t.Go);
                        else { args[p + "x"] = Mathf.RoundToInt(t.Screen.x); args[p + "y"] = Mathf.RoundToInt(t.Screen.y); }
                    }
                    catch (ToolException) { }
            var x = new JObject { ["tool"] = tool, ["args"] = args, ["frame"] = Time.frameCount, ["p"] = _phase, ["t"] = Time.realtimeSinceStartup };
            if (_rec != null) _rec.Actions.Add(x);
            if (_ring != null) _ring.Actions.Add(x);
            return x;
        }

        internal static void End(JObject x, bool injected, ToolResponse r)
        {
            if (x == null) return;
            string said = r.AllText().Trim();
            int nl = said.IndexOf('\n');
            if (nl >= 0) said = said.Substring(0, nl).Trim();
            x["input"] = injected;
            x["said"] = said.Length > 200 ? said.Substring(0, 200) : said;
        }

        // ---------------- state checksum ----------------

        static void Mix(ref uint h, int v) { for (int i = 0; i < 4; i++) { h ^= (uint)(v >> (8 * i)) & 0xFF; h *= 16777619; } }
        static void Mix(ref uint h, float v) { Mix(ref h, v.GetHashCode()); }
        static void Mix(ref uint h, string s) { if (s != null) foreach (char c in s) Mix(ref h, (int)c); }
        static void Mix(ref uint h, Vector3 v) { Mix(ref h, v.x); Mix(ref h, v.y); Mix(ref h, v.z); }

        static void Hash(Transform t, ref uint h, ref int n)
        {
            n++;
            Mix(ref h, t.name);
            Mix(ref h, t.gameObject.activeSelf ? 1 : 0);
            Mix(ref h, t.localPosition); Mix(ref h, t.localScale);
            var q = t.localRotation;
            Mix(ref h, q.x); Mix(ref h, q.y); Mix(ref h, q.z); Mix(ref h, q.w);
#if UDRV_UGUI
            var text = t.GetComponent<Text>();
            if (text != null) Mix(ref h, text.text);
#endif
            foreach (Transform c in t) Hash(c, ref h, ref n);
        }

        static JObject Check(int f)
        {
            uint all = 2166136261;
            int n = 0;
            var parts = new JObject();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                Mix(ref all, scene.name);
                foreach (var go in scene.GetRootGameObjects())
                {
                    uint h = 2166136261;
                    Hash(go.transform, ref h, ref n);
                    Mix(ref all, (int)h);
                    if (parts.Count < 200) parts[scene.name + "/" + go.name] = h.ToString("X8");
                }
            }
            Mix(ref all, JsonUtility.ToJson(UnityEngine.Random.state));
            return new JObject { ["f"] = f, ["sum"] = all.ToString("X8"), ["objects"] = n, ["parts"] = parts };
        }

        // ---------------- replay ----------------

        static IEnumerator ReplayTool(JObject a, ToolResponse r)
        {
            string id = a.Str("id");
            if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^rec-[0-9]{8}-[0-9]{6}-[0-9a-f]{4}$")) throw new ToolException("id must be a recording id from rec_start or rec_stop, like rec-20261120-101500-ab12");
            bool boot = Driver.Current != null && Driver.Current.Client == "startup";
            if (_play == null || _play.Id != id || _play.Result != null && !_play.Boot || boot)
            {
                if (_play != null && _play.Result == null) throw new ToolException("another replay (" + _play.Id + ") is running");
                if (_rec != null) throw new ToolException("stop the recording (rec_stop) before replaying");
                string file = Path.Combine(StateTools.ExportDir(), id + ".json");
                string json = null;
                Sandbox.Own(() => { if (File.Exists(file)) json = File.ReadAllText(file); });
                if (json == null) throw new ToolException("no recording " + id + " in " + StateTools.ExportDir());
                BeginReplay(JObject.Parse(json), boot);
            }
            var p = _play;
            int wait = 0;
            while (p.Result == null) { if (++wait % 60 == 0) r.FlushText(); yield return null; }
            r.Text(p.Result);
        }

        static void BeginReplay(JObject o, bool fresh)
        {
            var p = new Replay { Id = (string)o["id"], File = o, Start = Time.frameCount, End = (int)o["frames"], Exact = fresh && (bool)o["exact"], Boot = fresh, PrevCapture = Time.captureFramerate };
            foreach (JObject x in (JArray)o["actions"]) if (!(bool?)x["input"] ?? true) p.Actions.Add(x);
            if (o["trace"] == null) foreach (JObject x in (JArray)o["actions"]) if ((bool?)x["input"] ?? false) p.Actions.Add(x);
            p.Actions.Sort((x, y) => ((int)x["f"] * 3 + (int)x["p"]).CompareTo((int)y["f"] * 3 + (int)y["p"]));
            foreach (JObject c in (JArray)o["checks"]) p.Checks[(int)c["f"]] = c;
            InputTools.EnsureDevices();
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            if (o["trace"] != null)
            {
                p.Trace = InputEventTrace.LoadFrom(new MemoryStream(Convert.FromBase64String((string)o["trace"])));
                p.Ctl = p.Trace.Replay();
                foreach (JObject d in (JArray)o["devices"])
                {
                    var now = InputSystem.GetDevice((string)d["name"]) ?? InputSystem.AddDevice((string)d["layout"], (string)d["name"]);
                    if (now.deviceId != (int)d["id"]) p.Ctl.WithDeviceMappedFromTo((int)d["id"], now.deviceId);
                }
                p.Ctl.PlayAllFramesOneByOne();
            }
#endif
            Time.captureFramerate = (int)o["fps"];
            UnityEngine.Random.InitState((int)o["seed"]);
            _play = p;
        }

        static void Step(int phase)
        {
            var p = _play;
            int f = Time.frameCount - p.Start;
            while (p.Next < p.Actions.Count && ((int)p.Actions[p.Next]["f"] < f || ((int)p.Actions[p.Next]["f"] == f && (int)p.Actions[p.Next]["p"] <= phase)))
            {
                var x = p.Actions[p.Next++];
                var args = (JObject)x["args"].DeepClone();
                foreach (var pre in new[] { "", "to_" })
                    if (args[pre + "path"] != null)
                        try
                        {
                            var go = StateTools.FindGo((string)args[pre + "path"]);
                            if (go != null) { args.Remove(pre + "path"); args[pre + "id"] = Ugui.Id(go); }
                        }
                        catch (ToolException) { }
                var res = new ToolResponse();
                Driver.Host.StartCoroutine(Driver.Guard(() => Driver.Invoke((string)x["tool"], args, res), res));
            }
            if (phase != 1) return;
            JObject want;
            if (p.Checks.TryGetValue(f, out want))
            {
                var got = Check(f);
                if ((string)got["sum"] != (string)want["sum"]) { Finish(p, f, want, got); return; }
            }
            if (f >= p.End) Finish(p, f, null, null);
        }

        static void Finish(Replay p, int f, JObject want, JObject got)
        {
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            if (p.Ctl != null) { p.Ctl.Dispose(); p.Trace.Dispose(); }
#endif
            Time.captureFramerate = p.PrevCapture;
            string how = p.Exact ? "" : " (approximate: UDriver did not start this game for the replay, so it replayed inside the running game; differences are expected)";
            string build = (string)p.File["build"] != Application.buildGUID ? " The game was rebuilt since the recording." : "";
            if (want == null) { p.Result = "match: " + p.Checks.Count + " state checksums over " + p.End + " frames are the same" + how + "." + build; return; }
            var diff = new List<string>();
            var wp = (JObject)want["parts"]; var gp = (JObject)got["parts"];
            foreach (var kv in wp) if ((string)gp[kv.Key] != (string)kv.Value) diff.Add(kv.Key);
            foreach (var kv in gp) if (wp[kv.Key] == null) diff.Add(kv.Key + " (new)");
            int last = f - Every;
            p.Result = "diverged at frame " + f + " (" + (f / (float)Fps).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s; " + (last < 0 ? "the first checksum already differs" : "frame " + last + " still matched") + ")" + how + ": "
                       + (diff.Count > 0 ? "different objects: " + string.Join(", ", diff.GetRange(0, Math.Min(diff.Count, 10)).ToArray()) : "objects " + want["objects"] + " -> " + got["objects"] + ", random state or scene list differs")
                       + "." + build;
        }
    }
}

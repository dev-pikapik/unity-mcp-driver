using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
#if UDRV_PHYSICS
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UDRV_AI
using UnityEngine.AI;
#endif

namespace UDriver
{
    public static class AutoplayTools
    {
        public static void Register()
        {
            Driver.Register("perceive", Perceive);
            Driver.Register("rule_add", RuleAdd);
            Driver.Register("rule_list", Driver.Sync(RuleList));
            Driver.Register("rule_remove", Driver.Sync(RuleRemove));
            Driver.Register("move", InputTools.Leased("move", Move));
        }

        static void NeedPlay()
        {
            if (!Application.isPlaying) throw new ToolException("this works while the game runs (in the editor: Play Mode first).");
        }

        // ---------------- perceive ----------------

        static IEnumerator Perceive(JObject a, ToolResponse r)
        {
            NeedPlay();
            var body = Body.Find((string)a["player"]);
            float radius = (float?)a["radius"] ?? 30f;
            int limit = (int?)a["limit"] ?? 25;
            var match = (string)a["match"] is string ms ? new Matcher(ms) : null;
            bool visibleOnly = (bool?)a["visibleOnly"] ?? false, scenery = (bool?)a["scenery"] ?? false;

            var first = new Dictionary<GameObject, Vector3>();
            foreach (var go in Around(body, radius)) first[go] = Ent.Center(go);
            Vector3 selfFirst = body.Root.position;
            float t0 = Time.time;
            for (int i = 0; i < 3; i++) yield return null;
            float dt = Mathf.Max(Time.time - t0, 1e-4f);
            if (body.Root == null) throw new ToolException("the player object is gone.");

            var sb = new StringBuilder();
            var selfVel = (body.Root.position - selfFirst) / dt;
            var cf = body.Cam.transform.forward;
            sb.Append("self ").Append(body.Root.name).Append(' ').Append(StateTools.IdOf(body.Root.gameObject))
              .Append(" pos ").Append(V(body.Root.position)).Append(" facing ").Append(Mathf.RoundToInt(Yaw(cf))).Append("° pitch ").Append(Mathf.RoundToInt(Pitch(cf)))
              .Append("° speed ").Append(selfVel.magnitude.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(body.Grounded() ? " grounded" : " in air");
            foreach (var s in Stats.All(body.Root.gameObject, true)) sb.Append(' ').Append(s);
            sb.AppendLine();

            var planes = GeometryUtility.CalculateFrustumPlanes(body.Cam);
            var rows = new List<KeyValuePair<float, string>>();
            int total = 0;
            foreach (var go in Around(body, radius))
            {
                bool isScenery = !Ent.HasScript(go) && go.GetComponentInParent<Rigidbody>() == null;
                if (isScenery && !scenery && match == null) continue;
                if (match != null && !match.Is(go)) continue;
                var c = Ent.Center(go);
                float d = Vector3.Distance(body.Root.position, c);
                var b = Ent.Bounds(go);
                string vis = Visibility(body, go, b, planes);
                if (visibleOnly && vis != "visible") continue;
                total++;
                var line = new StringBuilder();
                line.Append(go.name).Append(' ').Append(StateTools.IdOf(go));
                if (go.tag != "Untagged") line.Append(" tag=").Append(go.tag);
                var scripts = Ent.Scripts(go).Select(m => m.GetType().Name).Distinct().Take(4).ToArray();
                if (scripts.Length > 0) line.Append(" [").Append(string.Join(",", scripts)).Append(']');
                line.Append(' ').Append(d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("m ").Append(Bearing(body, c));
                float dy = c.y - body.Cam.transform.position.y;
                if (Mathf.Abs(dy) > 1f) line.Append(dy > 0 ? " above " : " below ").Append(Mathf.Abs(dy).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('m');
                if (isScenery) line.Append(" size ").Append(b.size.x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append('x').Append(b.size.y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append('x').Append(b.size.z.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                Vector3 p0;
                if (first.TryGetValue(go, out p0))
                {
                    var v = (c - p0) / dt;
                    if (v.magnitude > 0.2f)
                    {
                        line.Append(" speed ").Append(v.magnitude.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        float closing = Vector3.Dot(v, (body.Root.position - c).normalized);
                        if (closing > 0.3f) line.Append(" approaching"); else if (closing < -0.3f) line.Append(" moving away");
                    }
                }
                line.Append(' ').Append(vis);
                foreach (var s in Stats.All(go, false)) line.Append(' ').Append(s);
                rows.Add(new KeyValuePair<float, string>(d, line.ToString()));
            }
            rows.Sort((x, y) => x.Key.CompareTo(y.Key));
            sb.Append(total).Append(total == 1 ? " object" : " objects").Append(" within ").Append(radius).Append(" m");
            if (total > limit) sb.Append(" (nearest ").Append(limit).Append(" shown)");
            sb.AppendLine(":");
            foreach (var row in rows.Take(limit)) sb.AppendLine(row.Value);
            r.Text(sb.ToString().TrimEnd());
        }

        internal static IEnumerable<GameObject> Around(Body body, float radius)
        {
            var seen = new HashSet<GameObject>();
            int n = Physics.OverlapSphereNonAlloc(body.Root.position, radius, Buf, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var go = Ent.Of(Buf[i]);
                if (go == null || body.Owns(go.transform) || !seen.Add(go)) continue;
                yield return go;
            }
        }
        static readonly Collider[] Buf = new Collider[1024];

        internal static string Visibility(Body body, GameObject go, Bounds b, Plane[] planes)
        {
            var hit = body.FirstHit(body.Cam.transform.position, b.center);
            var blocker = hit.HasValue ? Ent.Of(hit.Value.collider) : null;
            if (blocker != null && blocker != go) return "hidden behind " + blocker.name;
            return GeometryUtility.TestPlanesAABB(planes, b) ? "visible" : "off-screen";
        }

        static string Bearing(Body body, Vector3 p)
        {
            float b = Mathf.DeltaAngle(Yaw(body.Cam.transform.forward), Yaw(p - body.Root.position));
            float a = Mathf.Abs(b);
            string side = b < 0 ? "left" : "right";
            string word = a < 15 ? "ahead" : a < 60 ? "ahead-" + side : a < 120 ? side : a < 165 ? "behind-" + side : "behind";
            return "bearing " + (b >= 0 ? "+" : "") + Mathf.RoundToInt(b) + "° " + word;
        }

        internal static float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        internal static float Pitch(Vector3 v) => Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
        internal static string V(Vector3 v) => "(" + v.x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," + v.y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," + v.z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")";

        // ---------------- rules ----------------

        static IEnumerator RuleAdd(JObject a, ToolResponse r)
        {
            NeedPlay();
            var rule = new Rule { Name = (string)a["name"], Spec = a };
            if (string.IsNullOrEmpty(rule.Name)) throw new ToolException("name is required.");
            rule.Logic = Logic.Parse(a);
            if (rule.Logic != null) rule.Logic.Preset = Preset.Any(k => a[k] != null);
            if (rule.Shooter)
            {
                if ((string)a["match"] is string m) rule.Targets = new Matcher(m);
                if ((string)a["avoid"] is string av) rule.Avoid = new Matcher(av);
                rule.Shoot = (bool?)a["shoot"] ?? false;
                rule.Jump = (bool?)a["jump"] ?? false;
                rule.KeepAway = (float?)a["keepAway"] ?? 0;
                rule.Approach = (float?)a["approach"] ?? 0;
                rule.Hunt = (bool?)a["hunt"] ?? false;
                rule.Alert = (float?)a["alert"] ?? 0;
                rule.Range = (float?)a["range"] ?? 40;
                rule.Fire = (string)a["fire"] ?? "mouse";
                Common(rule, a);
                if (rule.Targets == null && (rule.Shoot || rule.Hunt || rule.KeepAway > 0 || rule.Approach > 0 || rule.Alert > 0)) throw new ToolException("match is required to shoot, hunt, keep away, approach or alert.");
                if (!rule.Shoot && !rule.Hunt && !rule.Jump && rule.KeepAway <= 0 && rule.Approach <= 0 && rule.Alert <= 0) throw new ToolException("give the rule something to do: when and do (a condition and the tool calls to run), or shoot, hunt, keepAway, approach, jump or alert.");
            }
            else if (rule.Logic.Do == null) throw new ToolException("add do: what to run when the condition holds, e.g. [{\"emit\": \"score {score}\"}] or [{\"tool\": \"key\", \"args\": {\"key\": \"Space\"}}].");
            var me = Driver.Current;
            if (me != null && (rule.Shooter || rule.Logic.Do.Any(o => InputTools.IsInput((string)o["tool"])))) { InputTools.Own(me); rule.Holder = me.Holder; }
            UDriverPilot.Add(rule);
            yield return null;
            if (rule.Shooter) r.Text("rule '" + rule.Name + "' runs every frame" + (rule.Logic != null ? " while " + rule.Logic.WhenText : "") + " (" + UDriverPilot.I.Describe() + "). Events: events_query cats=['autoplay'].");
            else r.Text("rule '" + rule.Name + "' checks " + rule.Logic.WhenText + (rule.Logic.Every == 1 ? " every frame" : " every " + rule.Logic.Every + " frames") + ". Fires: events_query cats=['autoplay'], counts in rule_list.");
        }

        static readonly string[] Preset = { "match", "avoid", "shoot", "keepAway", "approach", "hunt", "jump", "alert", "fire", "drive" };

        static void Common(Rule rule, JObject a)
        {
            rule.TurnSpeed = (float?)a["turnSpeed"] ?? 900;
            rule.Speed = (float?)a["speed"] ?? 4;
            rule.Drive = (string)a["drive"] ?? "input";
            if (rule.Drive != "auto" && rule.Drive != "input" && rule.Drive != "direct") throw new ToolException("drive is 'auto', 'input' or 'direct'.");
            rule.Player = (string)a["player"];
            if (rule.Drive == "input" && !Keys.Available) throw new ToolException("virtual input needs the Input System package and Active Input Handling set to Input System or Both. Use drive='direct'.");
            if (rule.Fire.StartsWith("key:", StringComparison.Ordinal)) Keys.Check(rule.Fire.Substring(4));
            else if (rule.Fire != "mouse" && !rule.Fire.StartsWith("method:", StringComparison.Ordinal)) throw new ToolException("fire is 'mouse', 'key:<Key>' or 'method:Type.Method'.");
        }

        static void RuleList(JObject a, ToolResponse r)
        {
            if (UDriverPilot.I == null || UDriverPilot.I.Rules.Count == 0) { r.Text("no rules running."); return; }
            r.Text(UDriverPilot.I.Describe());
            foreach (var rule in UDriverPilot.I.Rules) r.Text(rule.Report());
        }

        static void RuleRemove(JObject a, ToolResponse r)
        {
            string name = (string)a["name"] ?? "all";
            if (UDriverPilot.I == null) { r.Text("no rules running."); return; }
            var gone = UDriverPilot.I.Rules.Where(x => name == "all" || x.Name == name).ToList();
            if (gone.Count == 0) throw new ToolException("no rule '" + name + "'. Running: " + string.Join(", ", UDriverPilot.I.Rules.Select(x => x.Name)));
            foreach (var rule in gone) { UDriverPilot.I.Rules.Remove(rule); r.Text(rule.Report()); }
            if (UDriverPilot.I.Rules.Count == 0) UDriverPilot.I.Stop();
        }

        // ---------------- move ----------------

        static IEnumerator Move(JObject a, ToolResponse r)
        {
            NeedPlay();
            var rule = new Rule { Name = "move", Temp = true, Spec = a, Jump = true };
            Common(rule, a);
            var body = Body.Find(rule.Player);
            rule.StopAt = (float?)a["stopAt"] ?? 1f;
            rule.Run = (bool?)a["run"] ?? false;
            bool jumpNow = (bool?)a["jump"] ?? false;
            float timeout = (float?)a["timeout"] ?? 15f, seconds = (float?)a["seconds"] ?? 1f;
            if (a["to"] != null) rule.Goal = Goal.Parse(a["to"], body);
            if (a["look"] != null) rule.Look = Goal.Parse(a["look"], body);
            string strafe = (string)a["strafe"];
            if (strafe != null) { if (strafe != "left" && strafe != "right") throw new ToolException("strafe is 'left' or 'right'."); rule.Strafe = strafe == "left" ? -1 : 1; }
            if (rule.Goal == null && rule.Look == null && strafe == null && !jumpNow) throw new ToolException("give to, look, strafe or jump.");
            rule.JumpOnce = jumpNow;
            rule.Holder = Driver.Current?.Holder;

            UDriverPilot.Add(rule);
            float t0 = Time.unscaledTime, lastProgress = t0;
            Vector3 lastPos = body.Root.position;
            float baseY = lastPos.y, peak = 0, jumpAt = -1;
            string result = null;
            while (result == null)
            {
                yield return null;
                if (body.Root == null) { result = "the player object is gone"; break; }
                float now = Time.unscaledTime;
                if (Vector3.Distance(body.Root.position, lastPos) > 0.3f) { lastPos = body.Root.position; lastProgress = now; }
                bool arrived = rule.Goal == null || rule.Goal.Flat(body.Root.position) <= rule.StopAt;
                bool looked = rule.Look == null || rule.LookError < 2f;
                bool strafed = strafe == null || now - t0 >= seconds;
                if (jumpNow && !rule.JumpOnce && jumpAt < 0) jumpAt = now;
                peak = Mathf.Max(peak, body.Root.position.y - baseY);
                bool jumped = !jumpNow || jumpAt >= 0 && (now - jumpAt > 2f || peak > 0.1f && body.Root.position.y - baseY < 0.05f);
                if (arrived && looked && strafed && jumped) result = "done";
                else if (now - t0 > timeout) result = "timed out after " + timeout + " s";
                else if (rule.Goal != null && !arrived && now - lastProgress > 3f) result = "stuck for 3 s";
                if (!UDriverPilot.I || !UDriverPilot.I.Rules.Contains(rule)) result = result ?? "stopped";
            }
            if (UDriverPilot.I) { UDriverPilot.I.Rules.Remove(rule); if (UDriverPilot.I.Rules.Count == 0) UDriverPilot.I.Stop(); }
            var sb = new StringBuilder(result).Append(" in ").Append((Time.unscaledTime - t0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
            if (body.Root != null)
            {
                sb.Append(", at ").Append(V(body.Root.position)).Append(" facing ").Append(Mathf.RoundToInt(Yaw(body.Cam.transform.forward))).Append('°');
                if (rule.Goal != null) sb.Append(", ").Append(rule.Goal.Flat(body.Root.position).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" m from the goal");
                if (rule.UsedNavMesh) sb.Append(" (NavMesh path)");
                if (jumpNow) sb.Append(peak > 0.1f ? ", jumped " + peak.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m high" : ", the player did not leave the ground");
            }
            if (rule.Note != null) sb.Append(". ").Append(rule.Note);
            r.Text(sb.ToString());
        }
    }

    sealed class Goal
    {
        public GameObject Go; public Vector3 Point;
        public Vector3 Pos => Go != null ? Ent.Center(Go) : Point;
        public bool Gone => !ReferenceEquals(Go, null) && Go == null;
        public float Flat(Vector3 from) { var d = Pos - from; d.y = 0; return d.magnitude; }

        public static Goal Parse(JToken t, Body body)
        {
            if (t is JArray arr && arr.Count == 3) return new Goal { Point = new Vector3((float)arr[0], (float)arr[1], (float)arr[2]) };
            var s = (string)t;
            if (string.IsNullOrEmpty(s)) throw new ToolException("expected [x,y,z], an object #id or path, or a match.");
            if (s.StartsWith("#", StringComparison.Ordinal) || s.Contains("/"))
            {
                var o = StateTools.Resolve(s);
                var go = o as GameObject ?? (o as Component)?.gameObject;
                if (go == null) throw new ToolException(s + " is not a scene object.");
                return new Goal { Go = go };
            }
            var m = new Matcher(s);
            GameObject best = null; float bd = float.MaxValue;
            int n = Physics.OverlapSphereNonAlloc(body.Root.position, 500f, Buf, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var go = Ent.Of(Buf[i]);
                if (go == null || body.Owns(go.transform) || !m.Is(go)) continue;
                float d = Vector3.Distance(body.Root.position, Ent.Center(go));
                if (d < bd) { bd = d; best = go; }
            }
            if (best == null) throw new ToolException("nothing matches '" + s + "' within 500 m.");
            return new Goal { Go = best };
        }
        static readonly Collider[] Buf = new Collider[2048];
    }

    sealed class Matcher
    {
        readonly List<KeyValuePair<char, string>> _terms = new List<KeyValuePair<char, string>>();
        readonly Dictionary<GameObject, bool> _cache = new Dictionary<GameObject, bool>();

        public Matcher(string spec)
        {
            foreach (var raw in spec.Split('|', ','))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                char k = '*';
                int c = p.IndexOf(':');
                if (c > 0)
                {
                    switch (p.Substring(0, c).Trim().ToLowerInvariant())
                    {
                        case "tag": k = 't'; break;
                        case "name": k = 'n'; break;
                        case "comp": case "component": case "script": k = 'c'; break;
                        case "layer": k = 'l'; break;
                        default: throw new ToolException("unknown match kind in '" + p + "': use tag:, name:, comp: or layer:.");
                    }
                    p = p.Substring(c + 1).Trim();
                }
                _terms.Add(new KeyValuePair<char, string>(k, p));
            }
            if (_terms.Count == 0) throw new ToolException("empty match.");
        }

        public bool Is(GameObject go)
        {
            bool r;
            if (_cache.TryGetValue(go, out r)) return r;
            if (_cache.Count > 4096) _cache.Clear();
            return _cache[go] = Test(go);
        }

        bool Test(GameObject go)
        {
            foreach (var t in _terms)
            {
                bool name = go.name.IndexOf(t.Value, StringComparison.OrdinalIgnoreCase) >= 0;
                bool tag = go.tag == t.Value;
                switch (t.Key)
                {
                    case 'n': if (name) return true; break;
                    case 't': if (tag) return true; break;
                    case 'l': if (LayerMask.LayerToName(go.layer) == t.Value) return true; break;
                    case 'c': if (HasType(go, t.Value)) return true; break;
                    default: if (name || tag || HasType(go, t.Value)) return true; break;
                }
            }
            return false;
        }

        static bool HasType(GameObject go, string type)
        {
            foreach (var c in go.GetComponents<Component>())
                if (c != null && string.Equals(c.GetType().Name, type, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    static class Ent
    {
        public static bool IsEngine(Type t)
        {
            var ns = t.Namespace ?? "";
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("Unity.", StringComparison.Ordinal)
                || ns.StartsWith("TMPro", StringComparison.Ordinal) || ns.StartsWith("UDriver", StringComparison.Ordinal);
        }

        public static IEnumerable<MonoBehaviour> Scripts(GameObject go) => go.GetComponents<MonoBehaviour>().Where(m => m != null && !IsEngine(m.GetType()));
        public static bool HasScript(GameObject go) => Scripts(go).Any();

        /// <summary>The object a collider belongs to: the nearest of itself and up to 3 parents with a game script, else its rigidbody, else itself.</summary>
        public static GameObject Of(Collider c)
        {
            if (c == null) return null;
            var t = c.transform;
            for (int i = 0; t != null && i < 4; i++, t = t.parent)
                if (HasScript(t.gameObject)) return t.gameObject;
            return c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject;
        }

        public static Bounds Bounds(GameObject go)
        {
            bool any = false; var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) foreach (var rd in go.GetComponentsInChildren<Renderer>()) { if (!any) { b = rd.bounds; any = true; } else b.Encapsulate(rd.bounds); }
            return b;
        }

        public static Vector3 Center(GameObject go) => Bounds(go).center;
    }

    /// <summary>Numeric fields that look like health or ammo, read by reflection (fields only, so no game code runs).</summary>
    static class Stats
    {
        static readonly Dictionary<Type, FieldInfo[]> Health = new Dictionary<Type, FieldInfo[]>(), Ammo = new Dictionary<Type, FieldInfo[]>();

        static string Norm(string n)
        {
            n = n.ToLowerInvariant();
            if (n.StartsWith("<")) n = n.Substring(1, Math.Max(0, n.IndexOf('>') - 1));
            if (n.StartsWith("m_")) n = n.Substring(2);
            return n.TrimStart('_');
        }
        static bool IsHealth(string n) => n.Contains("health") || n.EndsWith("hp") || n.Contains("hitpoint") || n == "life" || n == "lives" || n == "armor" || n == "shield";
        static bool IsAmmo(string n) => n.Contains("ammo") || n.Contains("bullet") || n.Contains("magazine");
        static bool Numeric(Type t) => t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long) || t == typeof(short) || t == typeof(byte);

        static FieldInfo[] Fields(Type t, Dictionary<Type, FieldInfo[]> cache, Func<string, bool> like)
        {
            FieldInfo[] f;
            if (cache.TryGetValue(t, out f)) return f;
            f = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                 .Where(x => Numeric(x.FieldType) && like(Norm(x.Name))).ToArray();
            return cache[t] = f;
        }

        public static IEnumerable<string> All(GameObject go, bool deep)
        {
            var scripts = deep ? go.GetComponentsInChildren<MonoBehaviour>().Where(m => m != null && !Ent.IsEngine(m.GetType())) : Ent.Scripts(go);
            foreach (var m in scripts)
            {
                var t = m.GetType();
                foreach (var f in Fields(t, Health, IsHealth).Concat(Fields(t, Ammo, IsAmmo)))
                    yield return (deep ? t.Name + "." : "") + Norm(f.Name) + "=" + Fmt(f.GetValue(m));
            }
        }

        /// <summary>The main health value (first health-like field not named max*), or NaN.</summary>
        public static double Main(GameObject go, Dictionary<Type, FieldInfo[]> cache, Func<string, bool> like)
        {
            foreach (var m in Ent.Scripts(go))
                foreach (var f in Fields(m.GetType(), cache, like))
                    if (!Norm(f.Name).StartsWith("max")) return Convert.ToDouble(f.GetValue(m));
            return double.NaN;
        }
        public static double MainHealth(GameObject go) => Main(go, Health, IsHealth);

        public static double SelfAmmo(GameObject root)
        {
            foreach (var m in root.GetComponentsInChildren<MonoBehaviour>())
                if (m != null && !Ent.IsEngine(m.GetType()))
                    foreach (var f in Fields(m.GetType(), Ammo, IsAmmo))
                        if (!Norm(f.Name).StartsWith("max") && !Norm(f.Name).Contains("magazine")) return Convert.ToDouble(f.GetValue(m));
            return double.NaN;
        }

        static string Fmt(object v) => v is float fl ? fl.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : v is double d ? d.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : Convert.ToString(v);
    }

    /// <summary>The player: its root object and camera.</summary>
    sealed class Body
    {
        public Transform Root; public Camera Cam;
        readonly RaycastHit[] _hits = new RaycastHit[32];

        public static Body Find(string player)
        {
            Transform root = null;
            if (!string.IsNullOrEmpty(player))
            {
                var o = player.StartsWith("#", StringComparison.Ordinal) || player.Contains("/") ? StateTools.Resolve(player) : GameObject.Find(player);
                root = (o as GameObject)?.transform ?? (o as Component)?.transform;
                if (root == null) throw new ToolException("no player object '" + player + "'.");
            }
            if (root == null) { var p = GameObject.FindWithTag("Player"); if (p != null) root = p.transform; }
            var cam = root != null ? root.GetComponentInChildren<Camera>() : null;
            if (cam == null) cam = Camera.main;
            if (cam == null) throw new ToolException("no camera: the scene has no Camera tagged MainCamera. Pass player.");
            if (root == null) root = cam.transform.root;
            return new Body { Root = root, Cam = cam };
        }

        public bool Owns(Transform t) => t == Root || t.IsChildOf(Root);

        public bool Grounded()
        {
            var cc = Root.GetComponent<CharacterController>();
            if (cc != null) return cc.isGrounded || Ray(Feet + Vector3.up * 0.1f, Vector3.down, 0.3f).HasValue;
            return Ray(Feet + Vector3.up * 0.1f, Vector3.down, 0.3f).HasValue;
        }

        public Vector3 Feet
        {
            get
            {
                var c = Root.GetComponent<Collider>();
                return c != null ? new Vector3(Root.position.x, c.bounds.min.y, Root.position.z) : Root.position;
            }
        }

        /// <summary>First solid hit along a ray, ignoring the player itself.</summary>
        public RaycastHit? Ray(Vector3 from, Vector3 dir, float dist)
        {
            int n = Physics.RaycastNonAlloc(from, dir, _hits, dist, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit? best = null;
            for (int i = 0; i < n; i++)
                if (!Owns(_hits[i].collider.transform) && (best == null || _hits[i].distance < best.Value.distance)) best = _hits[i];
            return best;
        }

        public RaycastHit? FirstHit(Vector3 from, Vector3 to) { var d = to - from; return Ray(from, d.normalized, d.magnitude + 0.5f); }
    }

    /// <summary>Virtual keyboard and mouse through the Input System (InputTools).</summary>
    static class Keys
    {
#if UDRV_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
        public static readonly bool Available = true;
#else
        public static readonly bool Available = false;
#endif
        static readonly HashSet<string> Held = new HashSet<string>();
        public static void Check(string key) => InputTools.Inj.CheckKey(key);
        public static void Set(string key, bool down)
        {
            if (down ? !Held.Add(key) : !Held.Remove(key)) return;
            InputTools.Inj.SendKey(key, down);
        }
        public static void Mouse(Vector2 delta, int buttons) => InputTools.Inj.Look(delta, buttons);
        public static void ReleaseAll()
        {
            foreach (var k in Held.ToArray()) Set(k, false);
            Mouse(Vector2.zero, 0);
        }
    }

    sealed class Rule
    {
        public string Name, Fire = "mouse", Drive = "input", Player, Note, Holder;
        public JObject Spec;
        public Matcher Targets, Avoid;
        public bool Shoot, Hunt, Jump, Run, Temp, JumpOnce, UsedNavMesh;
        public float KeepAway, Approach, Alert, Range = 40, TurnSpeed = 900, Speed = 4, StopAt = 1, LookError = 180;
        public int Strafe;
        public Goal Goal, Look;
        public int Shots, Hits, Kills, Misses, Forbidden, Presses;
        public readonly List<int> ReactFrames = new List<int>(), DecideFrames = new List<int>();
        public readonly List<float> ReactMs = new List<float>();
        public readonly HashSet<GameObject> Near = new HashSet<GameObject>();
        public readonly float Started = Time.unscaledTime;
#if UDRV_AI
        public NavMeshPath Path;
#endif
        public int Corner; public float PathAt = -1;
        public Logic Logic;
        public bool Shooter => Logic == null || Logic.Preset;
        public bool Acting => Logic == null || Logic.Preset && Logic.Holds && Logic.Done == null;

        static string Spread(IEnumerable<float> xs, string unit)
        {
            var v = xs.OrderBy(x => x).ToList();
            return "median " + Mathf.RoundToInt(v[v.Count / 2]) + ", 90% within " + Mathf.RoundToInt(v[Mathf.Min(v.Count - 1, (int)(v.Count * 0.9f))]) + ", max " + Mathf.RoundToInt(v[v.Count - 1]) + unit + " (" + v.Count + " targets)";
        }

        public string Report()
        {
            var sb = new StringBuilder("rule '").Append(Name).Append("'");
            if (Targets != null) sb.Append(" match=").Append((string)Spec["match"]);
            if (Avoid != null) sb.Append(" avoid=").Append((string)Spec["avoid"]);
            sb.Append(", running ").Append((Time.unscaledTime - Started).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
            if (Logic != null) sb.Append(Logic.Report());
            if (Shoot)
            {
                sb.Append(": shots ").Append(Shots).Append(", hits ").Append(Hits).Append(", kills ").Append(Kills).Append(", misses ").Append(Misses)
                  .Append(", hits on avoided ").Append(Forbidden);
                if (DecideFrames.Count > 0) sb.Append(", decision from a target on screen (and the pilot free) to pulling the trigger ").Append(Spread(DecideFrames.Select(x => (float)x), " frames"));
                if (ReactFrames.Count > 0)
                    sb.Append(", to the first real shot ").Append(Spread(ReactFrames.Select(x => (float)x), " frames")).Append(" = ").Append(Spread(ReactMs, " ms")).Append(" (the game's cooldown and reload included)");
                sb.Append(" (").Append(Presses).Append(" fire presses)");
            }
            if (Note != null) sb.Append(". Note: ").Append(Note);
            return sb.ToString();
        }
    }

    /// <summary>Runs the rules every frame after the game's own scripts.</summary>
    [DefaultExecutionOrder(32000)]
    sealed class UDriverPilot : MonoBehaviour
    {
        public static UDriverPilot I;
        public readonly List<Rule> Rules = new List<Rule>();
        Body _body;
        string _player;
        bool _direct;
        float _gainX = 0.1f, _gainY = 0.1f, _sentX, _sentY, _prevYaw, _prevPitch, _lastJump, _lastHurt, _stillSince;
        int _dead, _still, _pressedFrame = -10;
        Vector3 _stillPos;
        bool _mouseWorks;
        static bool _inputIgnored;
        bool _havePrev, _acting;
        double _ammo = double.NaN, _ammoMax, _selfHealth = double.NaN;
        GameObject _aimed;
        float _directPitch = float.NaN;
        readonly Dictionary<GameObject, Track> _tracks = new Dictionary<GameObject, Track>();
        readonly Collider[] _buf = new Collider[1024];
        int _nextScan;

        sealed class Track
        {
            public double Health = double.NaN;
            public int SeenFrame = -1, ShotFrame = -1000, AimFrame; public float SeenTime, AimTime;
            public bool Decided, Reacted, Dead;
            public string Name;
            public Rule TargetOf, AvoidOf;
            public int PendingShotFrame = -1;
        }
        Rule _pressRule;
        GameObject _pressTarget;
        int _pressFrame = -100;

        public static void Add(Rule rule)
        {
            if (I == null)
            {
                var go = new GameObject("UDriver Pilot") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                I = go.AddComponent<UDriverPilot>();
            }
            I.Rules.RemoveAll(x => x.Name == rule.Name);
            I.Rules.Add(rule);
            if (!rule.Shooter) return;
            if (rule.Player != null) { I._player = rule.Player; I._body = null; }
            if (rule.Drive == "direct") I._direct = true;
            else if (rule.Drive == "input") I._direct = false;
            else if (!Keys.Available || _inputIgnored) I._direct = true;
            I._nextScan = 0;
            I._tracks.Clear();
        }

        public string Describe()
        {
            var b = Me();
            return "player " + (b != null ? b.Root.name : "not found") + ", camera " + (b != null ? b.Cam.name : "-") + ", control " + (_direct ? "direct" : "virtual keyboard and mouse")
                 + (_direct || Keys.Available ? "" : " (no Input System)");
        }

        public void Stop()
        {
            if (!_direct && Keys.Available) Keys.ReleaseAll();
            Rules.Clear();
            I = null;
            Destroy(gameObject);
        }

        void OnDestroy() { if (I == this) I = null; }

        internal Body Me()
        {
            if (_body == null || _body.Root == null || _body.Cam == null)
            {
                try { _body = UDriver.Body.Find(_player); } catch (ToolException) { _body = null; }
            }
            return _body;
        }

        static void Emit(Rule r, string text) => EventStore.Emit("autoplay", r.Name + ": " + text);

        void LateUpdate()
        {
            if (Rules.Count == 0) { Stop(); return; }
            foreach (var r in Rules) if (r.Holder != null && (r.Logic == null || r.Logic.Done == null)) InputTools.Touch(r.Holder);
            foreach (var r in Rules.ToArray()) r.Logic?.Tick(r, this);
            if (!Rules.Any(r => r.Acting))
            {
                if (_acting && !_direct && Keys.Available) Keys.ReleaseAll();
                _acting = _havePrev = false;
                return;
            }
            _acting = true;
            var body = Me();
            if (body == null) return;
            int frame = Time.frameCount;
            float now = Time.unscaledTime;
            var cam = body.Cam.transform;
            var pos = body.Root.position;

            if (frame >= _nextScan) { Scan(body); _nextScan = frame + 5; }
            Observe(body, frame, now);

            Vector3 move = Vector3.zero;
            bool jump = false, jumpAllowed = false, run = false, fire = false;
            Rule aimRule = null; GameObject aimGo = null; Vector3? aimPt = null;

            foreach (var r in Rules)
            {
                if (!r.Acting) continue;
                if (r.Jump) jumpAllowed = true;
                if (r.Run) run = true;
                if (r.JumpOnce && Time.unscaledTime - _lastJump > 0.3f) { jump = true; r.JumpOnce = false; }
                GameObject nearest = null; float nd = float.MaxValue;
                foreach (var kv in _tracks)
                {
                    var go = kv.Key;
                    if (go == null || !go.activeInHierarchy || kv.Value.TargetOf != r) continue;
                    var c = Ent.Center(go);
                    float d = Vector3.Distance(pos, c);
                    if (r.Alert > 0)
                    {
                        bool inside = d <= r.Alert;
                        if (inside && r.Near.Add(go)) Emit(r, "enter " + go.name + " " + StateTools.IdOf(go) + " " + d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m");
                        else if (!inside && r.Near.Remove(go)) Emit(r, "leave " + go.name + " " + d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m");
                    }
                    if (d < nd) { nd = d; nearest = go; }
                    if (r.KeepAway > 0 && d < r.KeepAway) { var away = pos - c; away.y = 0; move += away.normalized * (r.KeepAway - d) / r.KeepAway * 2f; }
                }
                if (r.Shoot && aimRule == null)
                {
                    GameObject best = null; float bd = float.MaxValue;
                    foreach (var kv in _tracks)
                    {
                        var go = kv.Key;
                        if (go == null || !go.activeInHierarchy || kv.Value.TargetOf != r || kv.Value.AvoidOf != null || kv.Value.Dead) continue;
                        var c = Ent.Center(go);
                        float d = Vector3.Distance(cam.position, c);
                        if (d > r.Range || d >= bd || !Clear(body, go, c)) continue;
                        best = go; bd = d;
                    }
                    if (best != null) { aimRule = r; aimGo = best; aimPt = Ent.Center(best); }
                }
                if (r.Hunt && aimRule != r && nearest != null)
                {
                    var to = Ent.Center(nearest) - pos; to.y = 0;
                    move += nd > r.KeepAway + 1 ? to.normalized : Vector3.Cross(Vector3.up, to).normalized;
                    jumpAllowed = true;
                    if (aimPt == null) aimPt = Ent.Center(nearest);
                }
                if (r.Approach > 0 && nearest != null && nd > r.Approach) { var to = Ent.Center(nearest) - pos; to.y = 0; move += to.normalized; }
                if (r.Goal != null)
                {
                    if (r.Goal.Gone) { r.Note = "the goal object is gone"; r.Goal = null; }
                    else if (r.Goal.Flat(pos) > r.StopAt) { move += Steer(r, pos); jumpAllowed = true; }
                }
                if (r.Strafe != 0) { var right = cam.right; right.y = 0; move += right.normalized * r.Strafe; }
                if (r.Look != null && aimPt == null) { if (r.Look.Gone) r.Look = null; else aimPt = r.Look.Pos; }
            }

            foreach (var r in Rules) if (r.Look != null) r.LookError = Vector3.Angle(cam.forward, r.Look.Pos - cam.position);

            if (move.sqrMagnitude > 1e-4f) move = Avoid(body, move, jumpAllowed, ref jump);
            if (_direct || _mouseWorks || move.sqrMagnitude < 1e-4f || (pos - _stillPos).sqrMagnitude > 0.0025f) { _stillPos = pos; _still = 0; _stillSince = now; }
            else if (++_still >= 10 && now - _stillSince > 0.6f) { _still = int.MinValue; InputIgnored("keyboard"); }

            float turn = Rules.Max(r => r.TurnSpeed);
            Aim(body, aimPt, turn);

            if (aimGo != null)
            {
                Track at;
                if (_aimed != aimGo && _tracks.TryGetValue(aimGo, out at)) { at.AimFrame = frame; at.AimTime = now; }
                _aimed = aimGo;
                fire = OnTarget(body, aimGo, aimRule);
            }
            else _aimed = null;

            Act(body, aimRule, aimGo, fire, move, jump, run, frame, now);
        }

        /// <summary>Walls, crates and pillars between the camera and the object block it.</summary>
        bool Clear(Body body, GameObject go, Vector3 c)
        {
            var hit = body.FirstHit(body.Cam.transform.position, c);
            return !hit.HasValue || Ent.Of(hit.Value.collider) == go;
        }

        /// <summary>The crosshair ray hits the target first and no avoided object is within reach of the line of fire.</summary>
        bool OnTarget(Body body, GameObject target, Rule r)
        {
            var cam = body.Cam.transform;
            var hit = body.Ray(cam.position, cam.forward, r.Range + 5f);
            if (!hit.HasValue || Ent.Of(hit.Value.collider) != target) return false;
            if (r.Avoid == null) return true;
            var a = cam.position; var b = hit.Value.point;
            foreach (var kv in _tracks)
            {
                if (kv.Value.AvoidOf != r || kv.Key == null || !kv.Key.activeInHierarchy) continue;
                var bounds = Ent.Bounds(kv.Key);
                var c = bounds.center;
                var ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(c - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                if (Vector3.Distance(c, a + ab * t) < bounds.extents.magnitude + 0.35f) return false;
            }
            return true;
        }

        void Scan(Body body)
        {
            float radius = 10f;
            foreach (var r in Rules) radius = Mathf.Max(radius, r.Range, r.Alert, r.KeepAway, r.Approach);
            int n = Physics.OverlapSphereNonAlloc(body.Root.position, radius + 5f, _buf, ~0, QueryTriggerInteraction.Collide);
            var seen = new HashSet<GameObject>();
            for (int i = 0; i < n; i++)
            {
                var go = Ent.Of(_buf[i]);
                if (go == null || body.Owns(go.transform) || !seen.Add(go)) continue;
                Track t;
                if (!_tracks.TryGetValue(go, out t))
                {
                    Rule target = null, avoid = null;
                    foreach (var r in Rules)
                    {
                        if (avoid == null && r.Avoid != null && r.Avoid.Is(go)) avoid = r;
                        if (target == null && r.Targets != null && r.Targets.Is(go)) target = r;
                    }
                    if (target == null && avoid == null) continue;
                    t = new Track { TargetOf = target, AvoidOf = avoid, Health = Stats.MainHealth(go), Name = go.name };
                    _tracks[go] = t;
                }
            }
        }

        /// <summary>Counts real shots (the player's ammo going down after a press, or refilling to one short of full when reload and shot share a frame), first sight, hits, kills and hits on avoided objects.</summary>
        void Observe(Body body, int frame, float now)
        {
            double ammo = Stats.SelfAmmo(body.Root.gameObject);
            if (ammo > _ammoMax) _ammoMax = ammo;
            if (!double.IsNaN(ammo) && !double.IsNaN(_ammo) && _pressRule != null && frame - _pressFrame <= 3)
            {
                if (ammo < _ammo) Fired(_pressRule, _pressTarget, frame, (int)Math.Round(_ammo - ammo));
                else if (ammo > _ammo && ammo == _ammoMax - 1) Fired(_pressRule, _pressTarget, frame, 1);
            }
            _ammo = ammo;

            var planes = GeometryUtility.CalculateFrustumPlanes(body.Cam);
            List<GameObject> dead = null;
            foreach (var kv in _tracks)
            {
                var go = kv.Key; var t = kv.Value;
                bool gone = go == null || !go.activeInHierarchy;
                double h = gone ? double.NaN : Stats.MainHealth(go);
                bool lost = !t.Dead && (gone || (!double.IsNaN(t.Health) && !double.IsNaN(h) && h < t.Health));
                if (lost && frame - t.ShotFrame <= 15)
                {
                    if (t.AvoidOf != null)
                    {
                        t.AvoidOf.Forbidden++;
                        Emit(t.AvoidOf, "HIT AN AVOIDED OBJECT " + t.Name + (gone ? " (gone)" : " health " + t.Health + "→" + h));
                    }
                    else if (t.TargetOf != null)
                    {
                        var r = t.TargetOf;
                        if (t.PendingShotFrame >= 0) { r.Hits++; t.PendingShotFrame = -1; }
                        t.Dead = gone || h <= 0;
                        if (t.Dead) { r.Kills++; Emit(r, "kill " + t.Name + " (" + r.Kills + " total)"); }
                        else Emit(r, "hit " + t.Name + " health " + t.Health + "→" + h);
                    }
                }
                if (t.PendingShotFrame >= 0 && frame - t.PendingShotFrame > 20 && !lost)
                {
                    t.PendingShotFrame = -1;
                    if (t.TargetOf != null) { t.TargetOf.Misses++; Emit(t.TargetOf, "miss " + t.Name); }
                }
                if (gone) { (dead = dead ?? new List<GameObject>()).Add(go); continue; }
                t.Health = h;
                if (t.SeenFrame < 0 && t.TargetOf != null && t.TargetOf.Shoot)
                {
                    var b = Ent.Bounds(go);
                    if (Vector3.Distance(body.Root.position, b.center) <= t.TargetOf.Range && GeometryUtility.TestPlanesAABB(planes, b) && Clear(body, go, b.center))
                    { t.SeenFrame = frame; t.SeenTime = now; }
                }
            }
            if (dead != null) foreach (var go in dead) { _tracks.Remove(go); foreach (var r in Rules) r.Near.Remove(go); }

            double hp = Stats.MainHealth(body.Root.gameObject);
            if (!double.IsNaN(_selfHealth) && hp < _selfHealth && now - _lastHurt > 1f)
            {
                _lastHurt = now;
                EventStore.Emit("autoplay", "player hurt, health " + hp + (hp <= 0 ? " (down)" : ""));
            }
            _selfHealth = hp;
        }

        void Fired(Rule r, GameObject target, int frame, int n)
        {
            r.Shots += n;
            Track t;
            if ((object)target != null && _tracks.TryGetValue(target, out t))
            {
                t.ShotFrame = frame;
                if (t.PendingShotFrame < 0) t.PendingShotFrame = frame;
                if (!t.Reacted)
                {
                    t.Reacted = true;
                    if (t.SeenFrame >= 0) { r.ReactFrames.Add(frame - Math.Max(t.SeenFrame, t.AimFrame)); r.ReactMs.Add((Time.unscaledTime - Math.Max(t.SeenTime, t.AimTime)) * 1000f); }
                }
            }
            foreach (var kv in _tracks) if (kv.Value.AvoidOf == r) kv.Value.ShotFrame = frame;
        }

        Vector3 Steer(Rule r, Vector3 pos)
        {
            var goal = r.Goal.Pos;
#if UDRV_AI
            if (Time.unscaledTime - r.PathAt > 0.5f)
            {
                r.PathAt = Time.unscaledTime;
                NavMeshHit a, b;
                if (NavMesh.SamplePosition(pos, out a, 2f, NavMesh.AllAreas) && NavMesh.SamplePosition(goal, out b, 3f, NavMesh.AllAreas))
                {
                    r.Path = r.Path ?? new NavMeshPath();
                    if (NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, r.Path) && r.Path.corners.Length > 1) { r.Corner = 1; r.UsedNavMesh = true; }
                    else r.Path = null;
                }
                else r.Path = null;
            }
            if (r.Path != null)
            {
                var cs = r.Path.corners;
                while (r.Corner < cs.Length - 1 && Flat(cs[r.Corner] - pos) < 0.6f) r.Corner++;
                if (r.Corner < cs.Length) goal = cs[r.Corner];
            }
#endif
            var d = goal - pos; d.y = 0;
            return d.normalized;
        }

        static float Flat(Vector3 v) { v.y = 0; return v.magnitude; }

        /// <summary>Jumps over low obstacles in the way, sidesteps tall ones.</summary>
        Vector3 Avoid(Body body, Vector3 move, bool jumpAllowed, ref bool jump)
        {
            float mag = Mathf.Min(move.magnitude, 1f);
            var dir = move.normalized;
            var feet = body.Feet;
            var low = body.Ray(feet + Vector3.up * 0.3f, dir, 1.0f);
            if (!low.HasValue || IsTracked(low.Value.collider)) return dir * mag;
            if (jumpAllowed && !body.Ray(feet + Vector3.up * 1.3f, dir, 1.4f).HasValue)
            {
                if (Time.unscaledTime - _lastJump > 0.6f) jump = true;
                return dir * mag;
            }
            foreach (var a in new[] { 45f, -45f, 90f, -90f, 135f, -135f })
            {
                var d = Quaternion.Euler(0, a, 0) * dir;
                if (!body.Ray(feet + Vector3.up * 0.3f, d, 1.5f).HasValue && !body.Ray(feet + Vector3.up * 1.0f, d, 1.5f).HasValue) return d * mag;
            }
            return Vector3.zero;
        }

        bool IsTracked(Collider c) { var go = Ent.Of(c); return go != null && _tracks.ContainsKey(go); }

        void InputIgnored(string what)
        {
            if (Rules.Where(r => r.Shooter).All(r => r.Drive == "auto"))
            {
                _direct = _inputIgnored = true;
                Keys.ReleaseAll();
                foreach (var r in Rules) r.Note = "the game ignored the virtual " + what + ", so the pilot switched to direct control";
                EventStore.Emit("autoplay", "the game ignores the virtual " + what + " (old Input Manager?): switched to direct control");
                return;
            }
            foreach (var r in Rules) r.Note = "the game ignores the virtual " + what + " (old Input Manager?); add the rule with drive='auto' or 'direct'";
            EventStore.Emit("autoplay", "the game ignores the virtual " + what + " (old Input Manager?): add the rule with drive='auto' or 'direct'");
        }

        /// <summary>Turns the camera toward the point, at most turnSpeed degrees per second.</summary>
        void Aim(Body body, Vector3? point, float turnSpeed)
        {
            var cam = body.Cam.transform;
            float yaw = AutoplayTools.Yaw(cam.forward), pitch = AutoplayTools.Pitch(cam.forward);
            if (!_direct && _havePrev)
            {
                float dYaw = Mathf.DeltaAngle(_prevYaw, yaw), dPitch = pitch - _prevPitch;
                if (Mathf.Abs(_sentX) > 1f)
                {
                    if (Mathf.Abs(dYaw) > 0.01f) { _gainX = Mathf.Lerp(_gainX, Mathf.Clamp(dYaw / _sentX, -5f, 5f), 0.3f); _dead = 0; _mouseWorks = true; }
                    else if (++_dead == 21) InputIgnored("mouse");
                }
                if (Mathf.Abs(_sentY) > 1f && Mathf.Abs(dPitch) > 0.01f && Mathf.Abs(pitch) < 75f)
                    _gainY = Mathf.Lerp(_gainY, Mathf.Clamp(dPitch / _sentY, -5f, 5f), 0.3f);
            }
            _prevYaw = yaw; _prevPitch = pitch; _havePrev = true;
            _sentX = _sentY = 0;
            if (!point.HasValue) { _directPitch = float.NaN; return; }

            var to = point.Value - cam.position;
            float step = turnSpeed * Mathf.Max(Time.unscaledDeltaTime, 1f / 240f);
            float ey = Mathf.Clamp(Mathf.DeltaAngle(yaw, AutoplayTools.Yaw(to)), -step, step);
            float ep = Mathf.Clamp(AutoplayTools.Pitch(to) - pitch, -step, step);
            if (_direct)
            {
                var root = body.Root;
                if (float.IsNaN(_directPitch)) _directPitch = pitch;
                _directPitch = Mathf.MoveTowards(_directPitch, AutoplayTools.Pitch(to), step);
                if (root != cam && cam.IsChildOf(root))
                {
                    root.Rotate(0, ey, 0, Space.World);
                    var e = cam.localEulerAngles;
                    cam.localEulerAngles = new Vector3(-_directPitch, e.y, 0);
                }
                else cam.rotation = Quaternion.Euler(-_directPitch, yaw + ey, 0);
                return;
            }
            if (Mathf.Abs(_gainX) < 0.002f) _gainX = 0.1f * Mathf.Sign(_gainX == 0 ? 1 : _gainX);
            if (Mathf.Abs(_gainY) < 0.002f) _gainY = 0.1f * Mathf.Sign(_gainY == 0 ? 1 : _gainY);
            _sentX = Mathf.Clamp(ey / _gainX, -800f, 800f);
            _sentY = Mathf.Clamp(ep / _gainY, -800f, 800f);
        }

        void Act(Body body, Rule aimRule, GameObject aimGo, bool fire, Vector3 move, bool jump, bool run, int frame, float now)
        {
            bool shot = false;
            if (fire && aimRule != null)
            {
                Track t;
                if (_tracks.TryGetValue(aimGo, out t) && !t.Decided && t.SeenFrame >= 0) { t.Decided = true; aimRule.DecideFrames.Add(frame - Math.Max(t.SeenFrame, t.AimFrame)); }
                string how = aimRule.Fire;
                if (how.StartsWith("method:", StringComparison.Ordinal) || (_direct && how == "mouse")) shot = CallFire(body, aimRule, how);
                else if (frame - _pressedFrame >= 2) { _pressedFrame = frame; shot = true; }
            }
            bool press = !_direct && frame == _pressedFrame && aimRule != null;
            if (shot) Shot(aimRule, aimGo, frame);

            if (_direct)
            {
                if (move.sqrMagnitude > 1e-4f)
                {
                    float speed = Rules.Max(r => r.Speed) * (run ? 1.75f : 1f);
                    var delta = move * speed * Time.deltaTime;
                    var cc = body.Root.GetComponent<CharacterController>();
                    var rb = body.Root.GetComponent<Rigidbody>();
                    if (cc != null && cc.enabled) cc.Move(delta);
                    else if (rb != null && !rb.isKinematic) rb.MovePosition(rb.position + delta);
                    else body.Root.position += delta;
                }
                if (jump && now - _lastJump > 0.6f && CallMethod(body, new[] { "Jump" }) != null) _lastJump = now;
                return;
            }

            var keyFire = aimRule != null && aimRule.Fire.StartsWith("key:", StringComparison.Ordinal) ? aimRule.Fire.Substring(4) : null;
            if (keyFire != null) Keys.Set(keyFire, press);
            bool mouse = press && keyFire == null;
            Keys.Mouse(new Vector2(_sentX, _sentY), mouse ? 1 : 0);

            var local = Quaternion.Inverse(Quaternion.Euler(0, AutoplayTools.Yaw(body.Cam.transform.forward), 0)) * move;
            float m = Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.z), 1e-4f);
            local /= m;
            bool moving = move.sqrMagnitude > 1e-4f;
            Keys.Set("W", moving && local.z > 0.4f);
            Keys.Set("S", moving && local.z < -0.4f);
            Keys.Set("D", moving && local.x > 0.4f);
            Keys.Set("A", moving && local.x < -0.4f);
            Keys.Set("LeftShift", run && moving);
            bool space = jump && now - _lastJump > 0.6f;
            if (space) _lastJump = now;
            Keys.Set("Space", space);
        }

        /// <summary>A fire press or call. Real shots are counted when the player's ammo goes down, or per press when the game has no ammo field.</summary>
        void Shot(Rule r, GameObject target, int frame)
        {
            r.Presses++;
            _pressRule = r; _pressTarget = target; _pressFrame = frame;
            if (double.IsNaN(_ammo)) Fired(r, target, frame, 1);
        }

        bool CallFire(Body body, Rule r, string how)
        {
            object res;
            if (how.StartsWith("method:", StringComparison.Ordinal))
            {
                var spec = how.Substring(7);
                int dot = spec.LastIndexOf('.');
                if (dot <= 0) { r.Note = "fire method must be Type.Method"; return false; }
                res = CallMethod(body, new[] { spec.Substring(dot + 1) }, spec.Substring(0, dot));
            }
            else res = CallMethod(body, new[] { "Fire", "Shoot" });
            if (res == null) { r.Note = "no Fire()/Shoot() method on the player; pass fire='method:Type.Method'"; return false; }
            return !(res is bool b) || b;
        }

        static readonly object NoResult = new object();
        readonly Dictionary<string, KeyValuePair<Component, MethodInfo>> _methods = new Dictionary<string, KeyValuePair<Component, MethodInfo>>();

        /// <summary>Calls the first parameterless method with one of the names on the player's scripts. Returns null when there is none.</summary>
        object CallMethod(Body body, string[] names, string type = null)
        {
            string key = type + ":" + string.Join(",", names);
            KeyValuePair<Component, MethodInfo> found;
            if (!_methods.TryGetValue(key, out found) || found.Key == null)
            {
                found = default;
                foreach (var m in body.Root.GetComponentsInChildren<MonoBehaviour>())
                {
                    if (m == null || Ent.IsEngine(m.GetType()) || (type != null && m.GetType().Name != type)) continue;
                    foreach (var n in names)
                    {
                        var mi = m.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (mi == null) continue;
                        StateTools.Guard(mi, "call");
                        found = new KeyValuePair<Component, MethodInfo>(m, mi);
                        break;
                    }
                    if (found.Key != null) break;
                }
                if (found.Key == null) return null;
                _methods[key] = found;
            }
            return found.Value.Invoke(found.Key, null) ?? NoResult;
        }
    }
}
#endif

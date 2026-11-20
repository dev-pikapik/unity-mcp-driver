using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
#if UNITY_2022_2_OR_NEWER
using Unity.Profiling.Memory;
#else
using UnityEngine.Profiling.Memory.Experimental;
#endif
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;
using UObject = UnityEngine.Object;

namespace UDriver
{
    public static class MemoryTools
    {
        internal static void Register()
        {
            Driver.Register("memory_summary", Driver.Sync(Summary));
            Driver.Register("memory_top", Top);
            Driver.Register("memory_snapshot", Snapshot);
            Driver.Register("memory_watch", Watch);
            Driver.Register("memory_refs", Refs);
            Driver.Register("memory_capture", Capture);
            Driver.Register("leak_check", LeakCheck);
        }

        internal static void Out(ToolResponse r, JToken o) { r.Text(o.ToString(Formatting.None)); }

        static string Me { get { return Driver.Current != null ? Driver.Current.Client : ""; } }

        static readonly string NoSizes = "object sizes need a Development Build (Profiler.GetRuntimeMemorySizeLong returns 0 here)";

        // ---------------- summary ----------------

        static readonly string[][] Counters =
        {
            new[] { "totalUsed", "Total Used Memory" }, new[] { "totalReserved", "Total Reserved Memory" }, new[] { "gcUsed", "GC Used Memory" },
            new[] { "gcReserved", "GC Reserved Memory" }, new[] { "gfxUsed", "Gfx Used Memory" }, new[] { "audioUsed", "Audio Used Memory" },
            new[] { "videoUsed", "Video Used Memory" }, new[] { "systemUsed", "System Used Memory" },
            new[] { "textureCount", "Texture Count" }, new[] { "textureBytes", "Texture Memory" }, new[] { "meshCount", "Mesh Count" }, new[] { "meshBytes", "Mesh Memory" },
            new[] { "materialCount", "Material Count" }, new[] { "materialBytes", "Material Memory" }, new[] { "animationClipCount", "AnimationClip Count" },
            new[] { "animationClipBytes", "AnimationClip Memory" }, new[] { "assetCount", "Asset Count" }, new[] { "gameObjectCount", "Game Object Count" },
            new[] { "sceneObjectCount", "Scene Object Count" }, new[] { "objectCount", "Object Count" },
            new[] { "gcAllocBytesPerFrame", "GC Allocated In Frame" }, new[] { "gcAllocsPerFrame", "GC Allocation In Frame Count" },
        };

        static ProfilerRecorder[] _recs;

        static JToken Counter(int i)
        {
            if (_recs == null)
            {
                _recs = new ProfilerRecorder[Counters.Length];
                for (int k = 0; k < Counters.Length; k++) _recs[k] = ProfilerRecorder.StartNew(ProfilerCategory.Memory, Counters[k][1]);
            }
            var r = _recs[i];
            if (!r.Valid) return JValue.CreateNull();
            long v = r.CurrentValue;
            return v != 0 ? v : r.LastValue;
        }

        static JObject SizeOf(Type type)
        {
            long bytes = 0;
            var all = Resources.FindObjectsOfTypeAll(type);
            foreach (var o in all) bytes += Profiler.GetRuntimeMemorySizeLong(o);
            return new JObject { ["count"] = all.Length, ["bytes"] = bytes };
        }

        static void Summary(JObject a, ToolResponse r)
        {
            var c = new JObject();
            int missing = 0;
            for (int i = 0; i < Counters.Length; i++) { var v = Counter(i); if (v.Type == JTokenType.Null) missing++; c[Counters[i][0]] = v; }
            var o = new JObject
            {
                ["unity"] = new JObject { ["used"] = Profiler.GetTotalAllocatedMemoryLong(), ["reserved"] = Profiler.GetTotalReservedMemoryLong(), ["graphicsDriver"] = Profiler.GetAllocatedMemoryForGraphicsDriver() },
                ["managed"] = new JObject { ["used"] = Profiler.GetMonoUsedSizeLong(), ["heap"] = Profiler.GetMonoHeapSizeLong(), ["gcCollections"] = new JArray(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)) },
                ["counters"] = c,
                ["renderTextures"] = SizeOf(typeof(RenderTexture)),
                ["audioClips"] = SizeOf(typeof(AudioClip)),
                ["frame"] = Time.frameCount,
            };
            if (missing > 0) o["note"] = missing + " counter(s) are null: they need a Development Build or are not reported on this platform";
            else if (!Profiler.supported) o["note"] = NoSizes;
            Out(r, o);
        }

        // ---------------- object census ----------------

        sealed class Census
        {
            public readonly Dictionary<string, long[]> ByType = new Dictionary<string, long[]>();
            public Dictionary<long, long> Ids;
            public long Objects, Bytes, Heap, Used;
            public float Time;
            public int Frames;
        }

        static IEnumerator Take(Census c, bool ids)
        {
            if (ids) c.Ids = new Dictionary<long, long>();
            c.Heap = GC.GetTotalMemory(false);
            c.Used = Profiler.GetTotalAllocatedMemoryLong();
            c.Time = Time.realtimeSinceStartup;
            var all = Resources.FindObjectsOfTypeAll(typeof(UObject));
            var s = new StateTools.Scan();
            yield return StateTools.Visit(all, (x, i) =>
            {
                var o = (UObject)x;
                if (o == null) return true;
                long b = Profiler.GetRuntimeMemorySizeLong(o);
                string t = o.GetType().Name;
                long[] v;
                if (!c.ByType.TryGetValue(t, out v)) c.ByType[t] = v = new long[2];
                v[0]++; v[1] += b;
                if (ids) c.Ids[StateTools.Key(o)] = b;
                c.Objects++; c.Bytes += b;
                return true;
            }, s);
            c.Frames = s.Frames;
        }

        static UObject Alive(long key)
        {
            try { return StateTools.ById("#u" + key) as UObject; } catch (ToolException) { return null; }
        }

        static bool InScene(UObject o)
        {
            var go = o as GameObject ?? (o is Component ? ((Component)o).gameObject : null);
            return go != null && go.scene.IsValid();
        }

        /// <summary>Made by code while running (not loaded from an asset or a scene file).</summary>
        static bool Runtime(UObject o)
        {
#if UNITY_EDITOR
            return !UnityEditor.EditorUtility.IsPersistent(o) && !InScene(o) && (o.hideFlags & HideFlags.DontSave) == 0;
#else
            return StateTools.Key(o) < 0 && !InScene(o);
#endif
        }

        static JObject Describe(UObject o, long bytes, HashSet<UObject> used)
        {
            var j = new JObject { ["id"] = StateTools.IdOf(o), ["type"] = o.GetType().Name, ["name"] = o.name, ["bytes"] = bytes };
            var tex = o as Texture;
            if (tex != null)
            {
                j["size"] = tex.width + "x" + tex.height;
                j["format"] = tex is Texture2D ? ((Texture2D)tex).format.ToString() : tex is RenderTexture ? ((RenderTexture)tex).format.ToString() : tex.graphicsFormat.ToString();
                j["mips"] = tex.mipmapCount;
                if (tex.isReadable) j["readable"] = true;
            }
            var mesh = o as Mesh;
            if (mesh != null)
            {
                j["verts"] = mesh.vertexCount;
                j["tris"] = Tris(mesh);
                j["subMeshes"] = mesh.subMeshCount;
                if (mesh.isReadable) j["readable"] = true;
            }
            var clip = o as AudioClip;
            if (clip != null) { j["seconds"] = Math.Round(clip.length, 2); j["channels"] = clip.channels; j["loadType"] = clip.loadType.ToString(); }
            if (Runtime(o))
            {
                j["runtime"] = true;
                if (used != null && !used.Contains(o) && (tex != null || mesh != null || o is Material || o is Sprite)) j["unused"] = true;
            }
            return j;
        }

        internal static long Tris(Mesh m)
        {
            long n = 0;
            for (int i = 0; i < m.subMeshCount; i++) if (m.GetTopology(i) == MeshTopology.Triangles) n += m.GetIndexCount(i) / 3;
            return n;
        }

        /// <summary>Textures, meshes, materials and sprites referenced by scene renderers, mesh filters/colliders, cameras, UI graphics or their materials.</summary>
        static HashSet<UObject> Used()
        {
            var used = new HashSet<UObject>();
            var mats = new List<Material>();
            foreach (var c in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                var rend = c as Renderer;
                if (rend != null) { rend.GetSharedMaterials(mats); foreach (var m in mats) if (m != null) used.Add(m); }
                var mf = c as MeshFilter; if (mf != null && mf.sharedMesh != null) used.Add(mf.sharedMesh);
                var sk = c as SkinnedMeshRenderer; if (sk != null && sk.sharedMesh != null) used.Add(sk.sharedMesh);
                var mc = c as MeshCollider; if (mc != null && mc.sharedMesh != null) used.Add(mc.sharedMesh);
                var sr = c as SpriteRenderer; if (sr != null && sr.sprite != null) { used.Add(sr.sprite); used.Add(sr.sprite.texture); }
                var cam = c as Camera; if (cam != null && cam.targetTexture != null) used.Add(cam.targetTexture);
#if UDRV_UGUI
                var g = c as UnityEngine.UI.Graphic;
                if (g != null) { if (g.mainTexture != null) used.Add(g.mainTexture); if (g.material != null) used.Add(g.material); }
#endif
            }
            var ids = new List<int>();
            foreach (var o in new List<UObject>(used))
            {
                var m = o as Material;
                if (m == null) continue;
                m.GetTexturePropertyNameIDs(ids);
                foreach (var id in ids) { var tx = m.GetTexture(id); if (tx != null) used.Add(tx); }
            }
            return used;
        }

        static Type ObjectType(string name)
        {
            if (name == null || name == "Object") return typeof(UObject);
            var type = typeof(UObject).Assembly.GetType("UnityEngine." + name, false) ?? StateTools.FindType("UnityEngine." + name) ?? StateTools.FindType(name);
            if (type == null || !typeof(UObject).IsAssignableFrom(type))
                throw new ToolException("'" + name + "' is not a UnityEngine.Object type. Try Texture, Texture2D, RenderTexture, Mesh, AudioClip, Material, Shader, AnimationClip, GameObject or a game ScriptableObject type.");
            return type;
        }

        static IEnumerator Top(JObject a, ToolResponse r)
        {
            var type = ObjectType(a.Str("type"));
            string name = a.Str("name");
            bool group = a.Bool("group"), onlyRuntime = a.Bool("runtime");
            int offset = Math.Max(0, a.Int("offset")), limit = Mathf.Clamp(a.Int("limit", 15), 1, 500);
            var list = new List<KeyValuePair<long, UObject>>();
            var groups = new Dictionary<string, long[]>();
            long total = 0;
            var s = new StateTools.Scan();
            yield return StateTools.Visit(Resources.FindObjectsOfTypeAll(type), (x, i) =>
            {
                var o = (UObject)x;
                if (o == null || (name != null && !Util.Contains(o.name, name)) || (onlyRuntime && !Runtime(o))) return true;
                long b = Profiler.GetRuntimeMemorySizeLong(o);
                total += b;
                if (group)
                {
                    long[] v; string t = o.GetType().Name;
                    if (!groups.TryGetValue(t, out v)) groups[t] = v = new long[2];
                    v[0]++; v[1] += b;
                }
                else list.Add(new KeyValuePair<long, UObject>(b, o));
                return true;
            }, s);
            var res = new JObject { ["type"] = type.Name, ["bytes"] = total };
            var items = new JArray();
            if (group)
            {
                var g = new List<KeyValuePair<string, long[]>>(groups);
                g.Sort((x, y) => y.Value[1] != x.Value[1] ? y.Value[1].CompareTo(x.Value[1]) : y.Value[0].CompareTo(x.Value[0]));
                res["total"] = g.Count;
                for (int i = offset; i < g.Count && i < offset + limit; i++) items.Add(new JObject { ["type"] = g[i].Key, ["count"] = g[i].Value[0], ["bytes"] = g[i].Value[1] });
            }
            else
            {
                list.Sort((x, y) => y.Key.CompareTo(x.Key));
                res["total"] = list.Count;
                HashSet<UObject> used = null;
                for (int i = offset; i < list.Count && i < offset + limit; i++)
                {
                    var o = list[i].Value;
                    if (o == null) continue;
                    if (used == null && Runtime(o)) used = Used();
                    items.Add(Describe(o, list[i].Key, used));
                }
            }
            res["offset"] = offset;
            res["items"] = items;
            if (!Profiler.supported) res["note"] = NoSizes;
            Out(r, res);
        }

        // ---------------- snapshots ----------------

        sealed class Snap { public string Name, Owner; public int Frame; public Census C; }

        static readonly List<Snap> Snaps = new List<Snap>();
        const int SnapsPerAgent = 5, SnapsTotal = 20;

        static Snap Find(string name)
        {
            for (int i = Snaps.Count - 1; i >= 0; i--) if (Snaps[i].Owner == Me && (name == null || Snaps[i].Name == name)) return Snaps[i];
            throw new ToolException(name == null ? "you have no snapshots yet - take one with memory_snapshot action=take" : "no snapshot '" + name + "' of yours (see action=list)");
        }

        static IEnumerator Snapshot(JObject a, ToolResponse r)
        {
            string action = a.Str("action", "take");
            var sw = Stopwatch.StartNew();
            if (action == "list")
            {
                var arr = new JArray();
                foreach (var s in Snaps) if (s.Owner == Me) arr.Add(new JObject { ["name"] = s.Name, ["frame"] = s.Frame, ["secondsAgo"] = Math.Round(Time.realtimeSinceStartup - s.C.Time, 1), ["objects"] = s.C.Objects, ["bytes"] = s.C.Bytes });
                Out(r, new JObject { ["snapshots"] = arr, ["max"] = SnapsPerAgent });
                yield break;
            }
            if (action == "drop")
            {
                string n = a.Str("name");
                int removed = Snaps.RemoveAll(s => s.Owner == Me && (n == null || s.Name == n));
                Out(r, new JObject { ["dropped"] = removed });
                yield break;
            }
            if (action == "take")
            {
                var c = new Census();
                yield return Take(c, true);
                int mine = 0;
                foreach (var s in Snaps) if (s.Owner == Me) mine++;
                string name = a.Str("name", "s" + (mine + 1));
                Snaps.RemoveAll(s => s.Owner == Me && s.Name == name);
                var dropped = new JArray();
                while (CountMine() >= SnapsPerAgent) { var old = Snaps.Find(s => s.Owner == Me); dropped.Add(old.Name); Snaps.Remove(old); }
                while (Snaps.Count >= SnapsTotal) { dropped.Add(Snaps[0].Name + " (" + Snaps[0].Owner + ")"); Snaps.RemoveAt(0); }
                Snaps.Add(new Snap { Name = name, Owner = Me, Frame = Time.frameCount, C = c });
                var o = new JObject { ["snapshot"] = name, ["objects"] = c.Objects, ["bytes"] = c.Bytes, ["types"] = c.ByType.Count, ["managedHeap"] = c.Heap, ["ms"] = sw.ElapsedMilliseconds, ["frames"] = c.Frames };
                if (dropped.Count > 0) o["dropped"] = dropped;
                if (!Profiler.supported) o["note"] = NoSizes + "; counts and new objects still work";
                Out(r, o);
                yield break;
            }
            if (action != "diff") throw new ToolException("action must be take, diff, list or drop");
            var from = Find(a.Str("from"));
            Census to;
            string toName = a.Str("to");
            if (toName == null) { to = new Census(); yield return Take(to, true); }
            else to = Find(toName).C;
            int offset = Math.Max(0, a.Int("offset")), limit = Mathf.Clamp(a.Int("limit", 15), 1, 500);

            var types = new List<JObject>();
            foreach (var kv in to.ByType)
            {
                long[] b; from.C.ByType.TryGetValue(kv.Key, out b);
                long dc = kv.Value[0] - (b != null ? b[0] : 0), db = kv.Value[1] - (b != null ? b[1] : 0);
                if (dc != 0 || db != 0) types.Add(new JObject { ["type"] = kv.Key, ["count"] = dc, ["bytes"] = db, ["now"] = kv.Value[0] });
            }
            foreach (var kv in from.C.ByType)
                if (!to.ByType.ContainsKey(kv.Key)) types.Add(new JObject { ["type"] = kv.Key, ["count"] = -kv.Value[0], ["bytes"] = -kv.Value[1], ["now"] = 0 });
            types.Sort((x, y) =>
            {
                int c = Math.Abs((long)y["bytes"]).CompareTo(Math.Abs((long)x["bytes"]));
                return c != 0 ? c : Math.Abs((long)y["count"]).CompareTo(Math.Abs((long)x["count"]));
            });

            var added = new List<KeyValuePair<long, long>>();
            long addedBytes = 0;
            int gone = 0;
            foreach (var kv in to.Ids) if (!from.C.Ids.ContainsKey(kv.Key)) { added.Add(kv); addedBytes += kv.Value; }
            foreach (var k in from.C.Ids.Keys) if (!to.Ids.ContainsKey(k)) gone++;
            added.Sort((x, y) => y.Value.CompareTo(x.Value));
            var items = new JArray();
            for (int i = offset; i < added.Count && i < offset + limit; i++)
            {
                var o = Alive(added[i].Key);
                items.Add(o != null ? Describe(o, added[i].Value, null) : new JObject { ["id"] = "#u" + added[i].Key, ["bytes"] = added[i].Value, ["destroyed"] = true });
            }
            var typeArr = new JArray();
            for (int i = 0; i < types.Count && i < limit; i++) typeArr.Add(types[i]);
            var res = new JObject
            {
                ["from"] = from.Name, ["to"] = toName ?? "now", ["seconds"] = Math.Round(to.Time - from.C.Time, 1),
                ["objects"] = Change(from.C.Objects, to.Objects), ["bytes"] = Change(from.C.Bytes, to.Bytes), ["managedHeap"] = Change(from.C.Heap, to.Heap), ["unityUsed"] = Change(from.C.Used, to.Used),
                ["types"] = typeArr, ["typesChanged"] = types.Count,
                ["new"] = new JObject { ["total"] = added.Count, ["bytes"] = addedBytes, ["offset"] = offset, ["items"] = items },
                ["gone"] = gone, ["ms"] = sw.ElapsedMilliseconds,
            };
            if (!Profiler.supported) res["note"] = NoSizes;
            Out(r, res);
        }

        /// <summary>Totals can shrink (memory freed, assets unloaded): a negative change means less than before.</summary>
        static JObject Change(long before, long after) { return new JObject { ["before"] = before, ["after"] = after, ["change"] = after - before }; }

        static int CountMine() { int n = 0; foreach (var s in Snaps) if (s.Owner == Me) n++; return n; }

        // ---------------- growth over time ----------------

        static double Slope(List<double> t, List<double> v)
        {
            double mt = 0, mv = 0, num = 0, den = 0;
            for (int i = 0; i < t.Count; i++) { mt += t[i]; mv += v[i]; }
            mt /= t.Count; mv /= t.Count;
            for (int i = 0; i < t.Count; i++) { num += (t[i] - mt) * (v[i] - mv); den += (t[i] - mt) * (t[i] - mt); }
            return den > 0 ? num / den : 0;
        }

        static bool Rising(List<double> v)
        {
            for (int i = 1; i < v.Count; i++) if (v[i] < v[i - 1]) return false;
            return v[v.Count - 1] > v[0];
        }

        static JObject Trend(List<double> t, List<double> v)
        {
            return new JObject { ["first"] = (long)v[0], ["last"] = (long)v[v.Count - 1], ["perSec"] = Math.Round(Slope(t, v), 1), ["rising"] = Rising(v) };
        }

        static IEnumerator Watch(JObject a, ToolResponse r)
        {
            int samples = Mathf.Clamp(a.Int("samples", 10), 2, 120), interval = Mathf.Clamp(a.Int("intervalMs", 1000), 100, 60000), limit = Mathf.Clamp(a.Int("limit", 10), 1, 100);
            int budget = a.Int("timeoutMs", 100000);
            if ((long)samples * interval > budget) throw new ToolException("samples x intervalMs = " + (long)samples * interval + " ms is longer than " + budget + " ms; pass timeoutMs (up to 600000) to watch longer");
            var t = new List<double>();
            var series = new Dictionary<string, List<double>[]>();
            var heap = new List<double>(); var used = new List<double>(); var gfx = new List<double>(); var objs = new List<double>();
            for (int k = 0; k < samples; k++)
            {
                if (k > 0) { float until = Time.realtimeSinceStartup + interval / 1000f; while (Time.realtimeSinceStartup < until) yield return null; }
                var c = new Census();
                yield return Take(c, false);
                t.Add(c.Time); heap.Add(c.Heap); used.Add(c.Used); gfx.Add(Profiler.GetAllocatedMemoryForGraphicsDriver()); objs.Add(c.Objects);
                foreach (var kv in c.ByType)
                {
                    List<double>[] s;
                    if (!series.TryGetValue(kv.Key, out s)) { series[kv.Key] = s = new[] { new List<double>(), new List<double>() }; for (int i = 0; i < k; i++) { s[0].Add(0); s[1].Add(0); } }
                    s[0].Add(kv.Value[0]); s[1].Add(kv.Value[1]);
                }
                foreach (var s in series.Values) if (s[0].Count < k + 1) { s[0].Add(0); s[1].Add(0); }
            }
            var grow = new List<JObject>();
            foreach (var kv in series)
            {
                var cnt = kv.Value[0]; var b = kv.Value[1];
                if (cnt[cnt.Count - 1] <= cnt[0] && b[b.Count - 1] <= b[0]) continue;
                grow.Add(new JObject
                {
                    ["type"] = kv.Key, ["count"] = new JArray((long)cnt[0], (long)cnt[cnt.Count - 1]), ["bytes"] = new JArray((long)b[0], (long)b[b.Count - 1]),
                    ["countPerSec"] = Math.Round(Slope(t, cnt), 2), ["bytesPerSec"] = Math.Round(Slope(t, b), 1), ["rising"] = Rising(cnt) || Rising(b),
                });
            }
            grow.Sort((x, y) =>
            {
                int c = ((bool)y["rising"]).CompareTo((bool)x["rising"]);
                return c != 0 ? c : ((double)y["bytesPerSec"]).CompareTo((double)x["bytesPerSec"]);
            });
            var arr = new JArray();
            for (int i = 0; i < grow.Count && i < limit; i++) arr.Add(grow[i]);
            var res = new JObject
            {
                ["samples"] = samples, ["seconds"] = Math.Round(t[t.Count - 1] - t[0], 1),
                ["managedHeap"] = Trend(t, heap), ["unityUsed"] = Trend(t, used), ["graphicsDriver"] = Trend(t, gfx), ["objects"] = Trend(t, objs),
                ["growing"] = new JObject { ["total"] = grow.Count, ["items"] = arr },
            };
            if (!Profiler.supported) res["note"] = NoSizes + "; object counts still work";
            Out(r, res);
        }

        // ---------------- reference walk ----------------

        sealed class ByRef : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        sealed class Node { public Node Up; public string Edge; public int Index = -1, Depth; }

        static string PathOf(Node n)
        {
            var parts = new List<string>();
            for (; n != null; n = n.Up) parts.Add(n.Edge ?? "[" + n.Index + "]");
            parts.Reverse();
            return string.Concat(parts);
        }

        static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, bool> RefCache = new Dictionary<Type, bool>();

        static bool MayHoldRefs(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t.IsPointer || t == typeof(string) || t == typeof(decimal)) return false;
            if (!t.IsValueType) return true;
            bool r;
            if (RefCache.TryGetValue(t, out r)) return r;
            RefCache[t] = false;
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.FieldType != t && MayHoldRefs(f.FieldType)) { r = true; break; }
            return RefCache[t] = r;
        }

        static FieldInfo[] FieldsOf(Type type)
        {
            FieldInfo[] fs;
            if (FieldCache.TryGetValue(type, out fs)) return fs;
            var l = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (MayHoldRefs(f.FieldType) && StateTools.Why(t, f.Name) == null) l.Add(f);
            return FieldCache[type] = l.ToArray();
        }

        static string Label(FieldInfo f)
        {
            string n = f.Name;
            int k = n.IndexOf(">k__BackingField", StringComparison.Ordinal);
            return "." + (n[0] == '<' && k > 0 ? n.Substring(1, k - 1) : n);
        }

        sealed class Walk
        {
            public object Target;
            public bool Holders, Destroyed, Collections;
            public int MaxDepth, MaxNodes, PathLimit, Nodes, TargetHits, MinColl = 2;
            public bool Truncated;
            public readonly List<string> Paths = new List<string>();
            public readonly List<JObject> Dead = new List<JObject>();
            public readonly List<KeyValuePair<int, Node>> Colls = new List<KeyValuePair<int, Node>>();
            public readonly Dictionary<Node, string> CollType = new Dictionary<Node, string>();
            public readonly HashSet<string> Hidden = new HashSet<string>();
            readonly Dictionary<object, Node> _seen = new Dictionary<object, Node>(new ByRef());
            public readonly Queue<KeyValuePair<object, Node>> Queue = new Queue<KeyValuePair<object, Node>>();

            public bool Done { get { return Holders && !Destroyed && !Collections && Paths.Count >= PathLimit; } }

            public void Offer(object v, Node up, string edge, int index = -1)
            {
                if (v == null) return;
                var t = v.GetType();
                if (t.IsPrimitive || t.IsEnum || v is string || v is Type || v is MemberInfo) return;
                if (ReferenceEquals(v, Target))
                {
                    TargetHits++;
                    if (Paths.Count < PathLimit) Paths.Add(PathOf(new Node { Up = up, Edge = edge, Index = index }));
                    return;
                }
                if (_seen.ContainsKey(v)) return;
                if (Nodes >= MaxNodes) { Truncated = true; return; }
                var n = new Node { Up = up, Edge = edge, Index = index, Depth = up == null ? 0 : up.Depth + 1 };
                _seen[v] = n;
                Nodes++;
                var uo = v as UObject;
                // Id 0 in the editor: the placeholder for an unassigned serialized field, not a destroyed object (players report 0 for destroyed objects too).
                if (!ReferenceEquals(uo, null) && uo == null && Destroyed && (!Application.isEditor || StateTools.Key(uo) != 0))
                    Dead.Add(new JObject { ["id"] = StateTools.IdOf(uo), ["type"] = t.Name, ["path"] = PathOf(n) });
                if (n.Depth < MaxDepth) Queue.Enqueue(new KeyValuePair<object, Node>(v, n));
            }

            void Coll(Node n, int count, Type t)
            {
                if (!Collections || count < MinColl) return;
                Colls.Add(new KeyValuePair<int, Node>(count, n));
                CollType[n] = StateTools.TypeName(t);
            }

            static bool Simple(object k) { return k is string || k is Enum || (k != null && k.GetType().IsPrimitive); }

            /// <summary>Offers the children of x; yields every 256 elements so a huge collection spreads over frames.</summary>
            public IEnumerable<bool> Expand(object x, Node n)
            {
                var t = x.GetType();
                var arr = x as Array;
                if (arr != null)
                {
                    Coll(n, arr.Length, t);
                    if (!MayHoldRefs(t.GetElementType())) yield break;
                    int i = 0;
                    foreach (var e in arr) { Offer(e, n, null, i++); if ((i & 255) == 0) yield return true; }
                    yield break;
                }
                var del = x as Delegate;
                if (del != null)
                {
                    foreach (var d in del.GetInvocationList())
                        if (d.Target != null) Offer(d.Target, n, "{target of " + (d.Method.DeclaringType != null ? d.Method.DeclaringType.Name + "." : "") + d.Method.Name + "}");
                    yield break;
                }
                string why = StateTools.Why(t, "");
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("System.Collections", StringComparison.Ordinal) && x is IEnumerable)
                {
                    bool refs = true;
                    if (t.IsGenericType) { refs = false; foreach (var g in t.GetGenericArguments()) refs |= MayHoldRefs(g); }
                    var coll = x as ICollection;
                    if (coll != null) Coll(n, coll.Count, t);
                    if (!refs) yield break;
                    var dict = x as IDictionary;
                    if (dict != null)
                    {
                        var entries = new List<DictionaryEntry>();
                        foreach (DictionaryEntry e in dict) entries.Add(e);
                        for (int i = 0; i < entries.Count; i++)
                        {
                            Offer(entries[i].Key, n, ".Keys", -1);
                            Offer(entries[i].Value, n, Simple(entries[i].Key) ? "[" + entries[i].Key + "]" : ".Values[" + i + "]");
                            if ((i & 255) == 255) yield return true;
                        }
                        yield break;
                    }
                    var list = x as IList;
                    var items = new List<object>();
                    if (list == null) foreach (var e in (IEnumerable)x) items.Add(e);
                    for (int i = 0; i < (list != null ? list.Count : items.Count); i++)
                    {
                        Offer(list != null ? list[i] : items[i], n, null, i);
                        if ((i & 255) == 255) yield return true;
                    }
                    yield break;
                }
                if (why != null) { if (Hidden.Count < 20) Hidden.Add(StateTools.TypeName(t)); yield break; }
                foreach (var f in FieldsOf(t))
                {
                    object v;
                    try { v = f.GetValue(x); } catch { continue; }
                    Offer(v, n, Label(f));
                }
            }
        }

        /// <summary>Walks from static fields of game code and scene components (or from one state path); st[0] counts roots, st[1] frames.</summary>
        static IEnumerator WalkAll(Walk w, string from, int[] st)
        {
            var slice = Stopwatch.StartNew();
            if (from != null) { w.Offer(StateTools.Resolve(from), null, from); st[0] = 1; }
            else
            {
                foreach (var t in StateTools.GameTypes(false))
                {
                    if (slice.Elapsed.TotalMilliseconds > StateTools.FrameBudgetMs) { st[1]++; yield return null; slice.Reset(); slice.Start(); }
                    if (t.ContainsGenericParameters || StateTools.Why(t, "") != null) continue;
                    foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsLiteral || !MayHoldRefs(f.FieldType)) continue;
                        object v;
                        try { v = f.GetValue(null); } catch { continue; }
                        if (v == null) continue;
                        st[0]++;
                        w.Offer(v, null, "@" + t.FullName + Label(f));
                    }
                }
                foreach (var go in StateTools.RootObjects())
                    foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null) { st[0]++; w.Offer(mb, null, StateTools.PathOf(mb.gameObject) + ":" + mb.GetType().Name); }
            }
            int k = 0;
            while (w.Queue.Count > 0 && !w.Done)
            {
                var e = w.Queue.Dequeue();
                foreach (var _ in w.Expand(e.Key, e.Value))
                    if (slice.Elapsed.TotalMilliseconds > StateTools.FrameBudgetMs) { st[1]++; yield return null; slice.Reset(); slice.Start(); }
                if ((++k & 63) == 0 && slice.Elapsed.TotalMilliseconds > StateTools.FrameBudgetMs) { st[1]++; yield return null; slice.Reset(); slice.Start(); }
            }
        }

        static IEnumerator Refs(JObject a, ToolResponse r)
        {
            string id = a.Str("id"), from = a.Str("from"), find = a.Str("find", id != null ? "holders" : "all");
            if (find != "holders" && find != "destroyed" && find != "collections" && find != "all") throw new ToolException("find must be holders, destroyed, collections or all");
            if (find == "holders" && id == null) throw new ToolException("id is required to find holders, e.g. id=\"#u-12345\" from memory_top or memory_snapshot");
            int offset = Math.Max(0, a.Int("offset")), limit = Mathf.Clamp(a.Int("limit", find == "holders" ? 5 : 20), 1, 200);
            var w = new Walk
            {
                Holders = id != null && (find == "holders" || find == "all"), Destroyed = find == "destroyed" || find == "all", Collections = find == "collections" || find == "all",
                MaxDepth = Mathf.Clamp(a.Int("maxDepth", 16), 1, 64), MaxNodes = Mathf.Clamp(a.Int("maxNodes", 2000000), 1000, 10000000), PathLimit = limit,
            };
            if (id != null) w.Target = StateTools.ById(id.StartsWith("#", StringComparison.Ordinal) ? id : "#" + id);
            var sw = Stopwatch.StartNew();
            var st = new int[2];
            yield return WalkAll(w, from, st);
            int roots = st[0], frames = st[1];
            var res = new JObject();
            if (w.Target != null)
            {
                var uo = w.Target as UObject;
                res["target"] = new JObject { ["id"] = StateTools.IdOf(w.Target), ["type"] = StateTools.TypeName(w.Target.GetType()), ["name"] = uo != null ? uo.name : StateTools.Short(w.Target) };
            }
            if (w.Holders)
            {
                var h = new JObject { ["total"] = w.TargetHits, ["paths"] = new JArray(w.Paths.ToArray()) };
                if (w.TargetHits == 0) h["note"] = "no path from " + (from ?? "static fields of game code, loaded scene components or DontDestroyOnLoad") + " within depth " + w.MaxDepth
                                                   + ": it is held natively (a loaded asset, Unity caches), by members hidden by the safety policy, deeper than maxDepth, or by nothing (it can be unloaded or collected)";
                res["holders"] = h;
            }
            if (w.Destroyed)
            {
                var arr = new JArray();
                for (int i = offset; i < w.Dead.Count && i < offset + limit; i++) arr.Add(w.Dead[i]);
                res["destroyed"] = new JObject { ["total"] = w.Dead.Count, ["offset"] = offset, ["items"] = arr };
            }
            if (w.Collections)
            {
                w.Colls.Sort((x, y) => y.Key.CompareTo(x.Key));
                var arr = new JArray();
                for (int i = offset; i < w.Colls.Count && i < offset + limit; i++)
                    arr.Add(new JObject { ["count"] = w.Colls[i].Key, ["type"] = w.CollType[w.Colls[i].Value], ["path"] = PathOf(w.Colls[i].Value) });
                res["collections"] = new JObject { ["total"] = w.Colls.Count, ["offset"] = offset, ["items"] = arr };
            }
            var walk = new JObject { ["roots"] = roots, ["objects"] = w.Nodes, ["frames"] = frames, ["ms"] = sw.ElapsedMilliseconds, ["complete"] = !w.Truncated && (w.Queue.Count == 0 || w.Done) };
            if (w.Truncated) walk["note"] = "stopped at maxNodes=" + w.MaxNodes + "; raise maxNodes or narrow with from";
            if (w.Hidden.Count > 0) walk["hiddenByPolicy"] = new JArray(new List<string>(w.Hidden).ToArray());
            res["walk"] = walk;
            Out(r, res);
        }

        // ---------------- full snapshot for the Memory Profiler ----------------

        const long CaptureCap = 4L * 1024 * 1024 * 1024;

        static IEnumerator Capture(JObject a, ToolResponse r)
        {
            if (!Application.isEditor && !Debug.isDebugBuild) throw new ToolException("a full memory snapshot needs a Development Build or the editor; memory_summary, memory_top, memory_snapshot and memory_refs work here");
            string dir = StateTools.ExportDir();
            long limit = Math.Min(CaptureCap, StateTools.ExportLimit());
            StateTools.MakeRoom(dir, Math.Min(Profiler.GetTotalReservedMemoryLong(), limit));
            string file = Path.Combine(dir, "memory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture).Substring(0, 6) + ".snap");
            string done = null;
            bool ok = false;
            var sw = Stopwatch.StartNew();
            Sandbox.Own(() => MemoryProfiler.TakeSnapshot(file, (p, success) => { done = p ?? ""; ok = success; }, CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects | CaptureFlags.NativeAllocations));
            while (done == null)
            {
                if (sw.ElapsedMilliseconds > 110000) throw new ToolException("the snapshot did not finish within 110 s");
                yield return null;
            }
            long size = -1;
            if (ok) Sandbox.Own(() => { if (File.Exists(file)) size = new FileInfo(file).Length; });
            if (size < 0) throw new ToolException("Unity could not take the memory snapshot on this platform");
            Driver.OwnerOnly(file, false);
            if (size > limit)
            {
                Sandbox.Own(() => File.Delete(file));
                throw new ToolException("the snapshot was " + size / 1048576 + " MB, over the " + limit / 1048576 + " MB limit for one file in the exports folder (the owner sets it with UDRIVER_EXPORTS_MAX_MB), and was deleted");
            }
            StateTools.MakeRoom(dir, size, file);
            Out(r, new JObject { ["file"] = file, ["bytes"] = size, ["ms"] = sw.ElapsedMilliseconds, ["open"] = "Unity Memory Profiler package (com.unity.memoryprofiler): Window > Analysis > Memory Profiler, then import the file" });
        }

        // ---------------- leak check ----------------

        static IEnumerator LeakCheck(JObject a, ToolResponse r)
        {
            var steps = a["action"] as JArray ?? (a["action"] is JObject ? new JArray(a["action"]) : null);
            if (steps == null || steps.Count == 0)
                throw new ToolException("action must be the steps that should leave memory as it was, as {tool, args} or an array of them, e.g. [{\"tool\":\"ui_click\",\"args\":{\"text\":\"Open\"}},{\"tool\":\"ui_click\",\"args\":{\"text\":\"Close\"}}]");
            foreach (var step in steps)
            {
                string t = step is JObject ? (string)step["tool"] : null;
                if (t == null || t == "batch" || t == "leak_check" || t == "eval" || t.StartsWith("rec_", StringComparison.Ordinal)) throw new ToolException("leak_check cannot repeat '" + t + "'");
            }
            int n = Mathf.Clamp(a.Int("repeats", 5), 2, 100);
            var sw = Stopwatch.StartNew();
            yield return Repeat(steps, 1, r);
            var before = new Census();
            var wb = new Walk { Collections = true, MaxDepth = 16, MaxNodes = 2000000, MinColl = 1 };
            GC.Collect();
            yield return Take(before, false);
            yield return WalkAll(wb, null, new int[2]);
            yield return Repeat(steps, n, r);
            for (int i = 0; i < 5; i++) yield return null;
            var after = new Census();
            var wa = new Walk { Collections = true, MaxDepth = 16, MaxNodes = 2000000, MinColl = 1 };
            GC.Collect();
            yield return Take(after, false);
            yield return WalkAll(wa, null, new int[2]);

            var types = new JArray();
            foreach (var kv in after.ByType)
            {
                long[] b;
                long was = before.ByType.TryGetValue(kv.Key, out b) ? b[0] : 0;
                if (kv.Value[0] - was >= n)
                    types.Add(new JObject { ["type"] = kv.Key, ["before"] = was, ["after"] = kv.Value[0], ["perRepeat"] = Math.Round((kv.Value[0] - was) / (double)n, 1), ["bytes"] = kv.Value[1] - (b != null ? b[1] : 0) });
            }
            var counts = new Dictionary<string, int>();
            foreach (var c in wb.Colls) counts[PathOf(c.Value)] = c.Key;
            var colls = new JArray();
            foreach (var c in wa.Colls)
            {
                string path = PathOf(c.Value);
                int was;
                counts.TryGetValue(path, out was);
                if (c.Key - was >= n && !wa.CollType[c.Value].EndsWith("]", StringComparison.Ordinal))
                    colls.Add(new JObject { ["path"] = path, ["type"] = wa.CollType[c.Value], ["before"] = was, ["after"] = c.Key, ["perRepeat"] = Math.Round((c.Key - was) / (double)n, 1) });
            }
            var res = new JObject
            {
                ["verdict"] = types.Count + colls.Count > 0 ? "leak suspected: these grew by at least one per repeat" : "no leak found: nothing grew with every repeat",
                ["repeats"] = n, ["unityObjects"] = types, ["collections"] = colls,
                ["managedHeapGrowth"] = after.Heap - before.Heap, ["ms"] = sw.ElapsedMilliseconds,
                ["next"] = "memory_refs id=... or from=<collection path> shows what holds the objects",
            };
            if (!Application.isEditor && !Debug.isDebugBuild) res["note"] = NoSizes;
            if (wa.Truncated) res["walkNote"] = "the managed walk stopped at 2000000 objects; collections deeper in the heap were not compared";
            Out(r, res);
        }

        static IEnumerator Repeat(JArray steps, int times, ToolResponse r)
        {
            for (int i = 0; i < times; i++)
            {
                var s = new ToolResponse();
                yield return Driver.Invoke("batch", new JObject { ["calls"] = steps.DeepClone(), ["output"] = "errors" }, s);
                if (s.IsError) { r.Append(s); throw new ToolException("the action failed, so leak_check stopped"); }
            }
        }
    }
}

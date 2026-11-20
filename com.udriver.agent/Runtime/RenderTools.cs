using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace UDriver
{
    public static class RenderTools
    {
        internal static void Register()
        {
            Driver.Register("render_stats", Stats);
            Driver.Register("render_scene", Driver.Sync(Scene));
        }

        static readonly string[][] Counters =
        {
            new[] { "drawCalls", "Draw Calls Count", "Standard Draw Calls Count", "Standard Indirect Draw Calls Count", "Standard Instanced Draw Calls Count", "SRP Batcher Draw Calls Count", "BRG Draw Calls Count", "BRG Indirect Draw Calls Count" },
            new[] { "batches", "Batches Count" }, new[] { "setPassCalls", "SetPass Calls Count" },
            new[] { "triangles", "Triangles Count" }, new[] { "vertices", "Vertices Count" }, new[] { "shadowCasters", "Shadow Casters Count" },
            new[] { "visibleSkinnedMeshes", "Visible Skinned Meshes Count" }, new[] { "renderTextures", "Render Textures Count" }, new[] { "renderTextureBytes", "Render Textures Bytes" },
            new[] { "staticBatchedDrawCalls", "Static Batched Draw Calls Count" }, new[] { "dynamicBatchedDrawCalls", "Dynamic Batched Draw Calls Count" },
            new[] { "instancedBatchedDrawCalls", "Instanced Batched Draw Calls Count" },
        };

        static double R(double v) { return Math.Round(v, 2); }

#if UNITY_2022_2_OR_NEWER
        static double RefreshRate() { return Screen.currentResolution.refreshRateRatio.value; }
        static int MipmapLimit() { return QualitySettings.globalTextureMipmapLimit; }
#else
        static double RefreshRate() { return Screen.currentResolution.refreshRate; }
        static int MipmapLimit() { return QualitySettings.masterTextureLimit; }
#endif

        static JToken Spread(List<double> v)
        {
            if (v.Count == 0) return JValue.CreateNull();
            double sum = 0, max = 0;
            foreach (var x in v) { sum += x; if (x > max) max = x; }
            if (max <= 0) return JValue.CreateNull();
            return new JObject { ["avg"] = R(sum / v.Count), ["max"] = R(max) };
        }

        static IEnumerator Stats(JObject a, ToolResponse r)
        {
            int frames = Mathf.Clamp(a.Int("frames", 60), 1, 600);
            var recs = new ProfilerRecorder[Counters.Length][];
            for (int i = 0; i < recs.Length; i++)
            {
                var one = ProfilerRecorder.StartNew(ProfilerCategory.Render, Counters[i][1], frames + 2);
                recs[i] = new ProfilerRecorder[one.Valid ? 1 : Counters[i].Length - 1];
                recs[i][0] = one;
                for (int k = 1; k < recs[i].Length; k++) recs[i][k] = ProfilerRecorder.StartNew(ProfilerCategory.Render, Counters[i][k + 1], frames + 2);
            }
            var main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", frames + 2);
            var dt = new List<double>(); var frameNo = new List<int>();
            var cpuMain = new List<double>(); var cpuRender = new List<double>(); var gpu = new List<double>();
            var ft = new FrameTiming[1];
            var sw = Stopwatch.StartNew();
            try
            {
                yield return null;
                for (int i = 0; i < frames; i++)
                {
                    FrameTimingManager.CaptureFrameTimings();
                    yield return null;
                    dt.Add(Time.unscaledDeltaTime * 1000.0); frameNo.Add(Time.frameCount - 1);
#if UNITY_2022_1_OR_NEWER
                    if (FrameTimingManager.GetLatestTimings(1, ft) > 0) { cpuMain.Add(ft[0].cpuMainThreadFrameTime); cpuRender.Add(ft[0].cpuRenderThreadFrameTime); gpu.Add(ft[0].gpuFrameTime); }
#else
                    if (FrameTimingManager.GetLatestTimings(1, ft) > 0) { cpuMain.Add(ft[0].cpuFrameTime); cpuRender.Add(0); gpu.Add(ft[0].gpuFrameTime); }
#endif
                }
                var counters = new JObject();
                int missing = 0;
                for (int i = 0; i < recs.Length; i++)
                {
                    var v = new List<double>();
                    foreach (var rec in recs[i])
                        if (rec.Valid) for (int k = 0; k < rec.Count; k++) { if (k < v.Count) v[k] += rec.GetSample(k).Value; else v.Add(rec.GetSample(k).Value); }
                    if (v.Count == 0) { counters[Counters[i][0]] = null; missing++; continue; }
                    var s = Spread(v);
                    counters[Counters[i][0]] = s.Type == JTokenType.Null ? (JToken)0 : s;
                }
                var mainMs = new List<double>();
                if (main.Valid) for (int k = 0; k < main.Count; k++) mainMs.Add(main.GetSample(k).Value / 1e6);

                var sorted = new List<double>(dt); sorted.Sort();
                double sum = 0; foreach (var x in dt) sum += x;
                double avg = sum / dt.Count, median = sorted[sorted.Count / 2], p95 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
                var spikes = new List<int>();
                for (int i = 0; i < dt.Count; i++) if (dt[i] > 2 * median && dt[i] - median > 4) spikes.Add(i);
                spikes.Sort((x, y) => dt[y].CompareTo(dt[x]));
                var worst = new JArray();
                for (int i = 0; i < spikes.Count && i < 5; i++) worst.Add(new JObject { ["frame"] = frameNo[spikes[i]], ["ms"] = R(dt[spikes[i]]) });

                var cm = cpuMain.Count > 0 ? Spread(cpuMain) : Spread(mainMs);
                var cr = Spread(cpuRender); var g = Spread(gpu);
                int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
                double refresh = RefreshRate();
                double cap = vsync > 0 && refresh > 0 ? 1000.0 * vsync / refresh : target > 0 ? 1000.0 / target : 0;
                string bound = null;
                double busy = Math.Max(Avg(cm), Math.Max(Avg(cr), Avg(g)));
                if (cap > 0 && avg >= cap * 0.9 && busy > 0 && busy < cap * 0.8) bound = vsync > 0 ? "vsync (the game waits for the display)" : "targetFrameRate (the game waits)";
                else if (busy > 0) bound = busy == Avg(g) ? "gpu" : busy == Avg(cr) ? "cpu render thread" : "cpu main thread";

                var res = new JObject
                {
                    ["frames"] = dt.Count, ["seconds"] = R(sw.Elapsed.TotalSeconds),
                    ["frameMs"] = new JObject { ["min"] = R(sorted[0]), ["avg"] = R(avg), ["p95"] = R(p95), ["max"] = R(sorted[sorted.Count - 1]) },
                    ["fps"] = R(1000.0 / avg),
                    ["spikes"] = new JObject { ["count"] = spikes.Count, ["worst"] = worst },
                    ["cpuMainMs"] = cm, ["cpuRenderMs"] = cr, ["gpuMs"] = g, ["bound"] = bound,
                    ["counters"] = counters,
                    ["vSyncCount"] = vsync, ["targetFrameRate"] = target, ["refreshRate"] = R(refresh),
                };
                var notes = new List<string>();
                if (missing > 0) notes.Add(missing + " counter(s) are null: " + (Debug.isDebugBuild ? "not reported by this platform or graphics API" : "they need a Development Build"));
                if (g.Type == JTokenType.Null || cr.Type == JTokenType.Null) notes.Add("GPU / render-thread time need Frame Timing Stats (Player Settings > Other) and a supported graphics API");
                if (notes.Count > 0) res["note"] = string.Join("; ", notes);
                MemoryTools.Out(r, res);
            }
            finally
            {
                foreach (var x in recs) foreach (var y in x) y.Dispose();
                main.Dispose();
            }
        }

        static double Avg(JToken t) { return t is JObject ? (double)t["avg"] : 0; }

        // ---------------- what is drawn ----------------

        static List<Camera> GameCameras()
        {
            var l = new List<Camera>();
            foreach (var c in Camera.allCameras) if (c.cameraType == CameraType.Game) l.Add(c);
            l.Sort((x, y) => x.depth.CompareTo(y.depth));
            return l;
        }

        internal static Camera FindCamera(string s)
        {
            if (s.StartsWith("#", StringComparison.Ordinal) || s.StartsWith("/", StringComparison.Ordinal))
            {
                var o = StateTools.Resolve(s);
                var c = o as Camera ?? (o is GameObject ? ((GameObject)o).GetComponent<Camera>() : o is Component ? ((Component)o).GetComponent<Camera>() : null);
                if (c == null) throw new ToolException(s + " is not a camera");
                return c;
            }
            var names = new List<string>();
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (c.cameraType != CameraType.Game || !c.gameObject.scene.IsValid()) continue;
                if (string.Equals(c.name, s, StringComparison.OrdinalIgnoreCase)) return c;
                names.Add("\"" + c.name + "\" " + StateTools.IdOf(c));
            }
            throw new ToolException("no camera named '" + s + "'. Cameras: " + (names.Count > 0 ? string.Join(", ", names) : "none"));
        }

        static string Mask(int mask)
        {
            if (mask == -1) return "Everything";
            if (mask == 0) return "Nothing";
            var l = new List<string>();
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { string n = LayerMask.LayerToName(i); l.Add(string.IsNullOrEmpty(n) ? i.ToString() : n); }
            return string.Join(",", l);
        }

        sealed class Rend { public Renderer R; public Mesh Mesh; public long Tris; public int Verts, Cams; public Material[] Mats; }

        static void Scene(JObject a, ToolResponse r)
        {
            string camArg = a.Str("camera"), sort = a.Str("sort", "tris");
            int offset = Math.Max(0, a.Int("offset")), limit = Mathf.Clamp(a.Int("limit", 20), 1, 500);
            var cams = camArg != null ? new List<Camera> { FindCamera(camArg) } : GameCameras();
            var planes = new List<Plane[]>();
            foreach (var c in cams) planes.Add(GeometryUtility.CalculateFrustumPlanes(c));
            var camTris = new long[cams.Count]; var camCount = new int[cams.Count];
            var tris = new Dictionary<Mesh, long>();
            var rends = new List<Rend>();
            int total = 0;
            foreach (var rd in Util.FindAll<Renderer>())
            {
                if (!rd.enabled || !rd.gameObject.activeInHierarchy) continue;
                total++;
                Mesh mesh = rd is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)rd).sharedMesh : null;
                if (mesh == null && rd is MeshRenderer) { var mf = rd.GetComponent<MeshFilter>(); if (mf != null) mesh = mf.sharedMesh; }
                long t = 0;
                if (mesh != null && !tris.TryGetValue(mesh, out t)) tris[mesh] = t = MemoryTools.Tris(mesh);
                var e = new Rend { R = rd, Mesh = mesh, Tris = t, Verts = mesh != null ? mesh.vertexCount : 0, Mats = rd.sharedMaterials };
                var b = rd.bounds;
                for (int i = 0; i < cams.Count; i++)
                    if ((cams[i].cullingMask & (1 << rd.gameObject.layer)) != 0 && GeometryUtility.TestPlanesAABB(planes[i], b)) { e.Cams++; camTris[i] += t; camCount[i]++; }
                if (e.Cams > 0) rends.Add(e);
            }

            var camArr = new JArray();
            for (int i = 0; i < cams.Count; i++)
            {
                var c = cams[i];
                var rt = c.targetTexture;
                camArr.Add(new JObject
                {
                    ["id"] = StateTools.IdOf(c), ["path"] = StateTools.PathOf(c.gameObject), ["depth"] = c.depth, ["enabled"] = c.enabled && c.gameObject.activeInHierarchy,
                    ["target"] = rt != null ? "RenderTexture \"" + rt.name + "\" " + rt.width + "x" + rt.height : "display " + (c.targetDisplay + 1),
                    ["size"] = c.pixelWidth + "x" + c.pixelHeight, ["clear"] = c.clearFlags.ToString(), ["cullingMask"] = Mask(c.cullingMask),
                    ["projection"] = c.orthographic ? "ortho " + R(c.orthographicSize) : "fov " + R(c.fieldOfView), ["clip"] = new JArray(R(c.nearClipPlane), R(c.farClipPlane)),
                    ["hdr"] = c.allowHDR, ["msaa"] = c.allowMSAA, ["renderers"] = camCount[i], ["tris"] = camTris[i],
                });
            }

            if (sort == "materials") rends.Sort((x, y) => y.Mats.Length != x.Mats.Length ? y.Mats.Length.CompareTo(x.Mats.Length) : y.Tris.CompareTo(x.Tris));
            else if (sort == "tris") rends.Sort((x, y) => y.Tris.CompareTo(x.Tris));
            else throw new ToolException("sort must be tris or materials");
            var items = new JArray();
            for (int i = offset; i < rends.Count && i < offset + limit; i++)
            {
                var e = rends[i];
                var shaders = new HashSet<string>();
                foreach (var m in e.Mats) if (m != null && m.shader != null) shaders.Add(m.shader.name);
                var j = new JObject { ["id"] = StateTools.IdOf(e.R), ["path"] = StateTools.PathOf(e.R.gameObject), ["type"] = e.R.GetType().Name, ["tris"] = e.Tris, ["verts"] = e.Verts, ["materials"] = e.Mats.Length, ["shaders"] = new JArray(new List<string>(shaders).ToArray()) };
                if (e.R.shadowCastingMode != ShadowCastingMode.Off) j["castsShadows"] = true;
                if (e.R.isPartOfStaticBatch) j["staticBatch"] = true;
                items.Add(j);
            }

            bool srp = GraphicsSettings.currentRenderPipeline != null, srpBatcher = srp && GraphicsSettings.useScriptableRenderPipelineBatching;
            var lights = new JArray();
            int allLights = 0, localLights = 0, shadowLights = 0;
            foreach (var l in Util.FindAll<Light>())
            {
                if (!l.enabled || !l.gameObject.activeInHierarchy) continue;
                if (l.shadows != LightShadows.None) shadowLights++;
                allLights++;
                if (l.type != LightType.Directional) localLights++;
                if (lights.Count < 20)
                    lights.Add(new JObject { ["id"] = StateTools.IdOf(l), ["path"] = StateTools.PathOf(l.gameObject), ["type"] = l.type.ToString(), ["shadows"] = l.shadows.ToString(), ["intensity"] = R(l.intensity), ["range"] = R(l.range), ["baked"] = l.bakingOutput.lightmapBakeType.ToString() });
            }

            var res = new JObject
            {
                ["pipeline"] = srp ? GraphicsSettings.currentRenderPipeline.GetType().Name + " \"" + GraphicsSettings.currentRenderPipeline.name + "\"" : "Built-in",
                ["srpBatcher"] = srp ? (JToken)srpBatcher : null,
                ["graphics"] = SystemInfo.graphicsDeviceType + " | " + SystemInfo.graphicsDeviceName,
                ["quality"] = new JObject
                {
                    ["level"] = QualitySettings.names[QualitySettings.GetQualityLevel()], ["vSyncCount"] = QualitySettings.vSyncCount, ["targetFrameRate"] = Application.targetFrameRate,
                    ["antiAliasing"] = QualitySettings.antiAliasing, ["shadows"] = QualitySettings.shadows.ToString(), ["shadowDistance"] = R(QualitySettings.shadowDistance),
                    ["pixelLightCount"] = QualitySettings.pixelLightCount, ["lodBias"] = R(QualitySettings.lodBias), ["textureMipmapLimit"] = MipmapLimit(),
                },
                ["screen"] = Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode + " @" + R(RefreshRate()) + "Hz",
                ["cameras"] = camArr,
                ["renderers"] = new JObject { ["active"] = total, ["visible"] = rends.Count, ["offset"] = offset, ["items"] = items },
                ["lights"] = new JObject { ["total"] = allLights, ["withShadows"] = shadowLights, ["items"] = lights },
                ["hints"] = Hints(rends, srp, srpBatcher, localLights),
            };
            MemoryTools.Out(r, res);
        }

        static JArray Hints(List<Rend> rends, bool srp, bool srpBatcher, int localLights)
        {
            var hints = new JArray();
            var same = new Dictionary<KeyValuePair<Mesh, Material>, int>();
            var byShader = new Dictionary<Shader, HashSet<Material>>();
            int multi = 0, blocks = 0;
            foreach (var e in rends)
            {
                if (e.Mats.Length > 1) multi++;
                if (e.R.HasPropertyBlock()) blocks++;
                foreach (var m in e.Mats)
                {
                    if (m == null || m.shader == null) continue;
                    HashSet<Material> set;
                    if (!byShader.TryGetValue(m.shader, out set)) byShader[m.shader] = set = new HashSet<Material>();
                    set.Add(m);
                }
                if (e.Mesh != null && e.Mats.Length == 1 && e.Mats[0] != null && !e.Mats[0].enableInstancing && !e.R.isPartOfStaticBatch && e.R is MeshRenderer)
                {
                    var k = new KeyValuePair<Mesh, Material>(e.Mesh, e.Mats[0]);
                    int n; same.TryGetValue(k, out n); same[k] = n + 1;
                }
            }
            if (!srpBatcher)
                foreach (var kv in same)
                    if (kv.Value >= 10 && hints.Count < 5)
                        hints.Add(kv.Value + " visible renderers share mesh \"" + kv.Key.Key.name + "\" and material \"" + kv.Key.Value.name + "\" without GPU instancing: enable it on the material (or mark them static) to draw them in a few calls");
            if (!srpBatcher)
                foreach (var kv in byShader)
                    if (kv.Value.Count >= 10 && hints.Count < 8)
                        hints.Add(kv.Value.Count + " different materials use shader \"" + kv.Key.name + "\": each one costs a SetPass call; share materials or use a texture atlas");
            if (srp && !srpBatcher) hints.Add("the SRP Batcher is off: turn it on in the render pipeline asset to batch materials that share a shader");
            if (srpBatcher && blocks > 0) hints.Add(blocks + " visible renderer(s) use a MaterialPropertyBlock, which takes them out of the SRP Batcher");
            if (multi > 0) hints.Add(multi + " visible renderer(s) have several materials: each material is a separate draw call (sort=materials lists them)");
            if (!srp && localLights > QualitySettings.pixelLightCount) hints.Add(localLights + " point/spot/area lights but pixelLightCount=" + QualitySettings.pixelLightCount + ": the rest are drawn per vertex");
            return hints;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UDriver
{
    public static class CaptureTools
    {
        sealed class Frame { public int Index, FrameNo; public double Time; public byte[] Jpg; public string Label; }

        static readonly List<Frame> Frames = new List<Frame>();
        static bool _rec, _onlyChanges = true;
        static float _interval = 0.2f;
        static int _recWidth = 480, _cap = 100, _nextIndex, _lastHash;
        static float _savedScale = 1f;

        public static bool Recording { get { return _rec; } }
        public static int FrameCount { get { return Frames.Count; } }

        internal static void Register()
        {
            Driver.Register("screenshot", Screenshot);
            Driver.Register("frames_record", Driver.Sync(Record));
            Driver.Register("frames_get", Driver.Sync(GetFrames));
            Driver.Register("time_control", TimeControl);
        }

        // ---------------- grabbing ----------------

        /// <summary>Reads the current framebuffer. Must be called at end of frame. Pixels are bottom-up (row 0 = bottom).</summary>
        static Color32[] Grab(int maxWidth, out int w, out int h)
        {
            var src = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                w = src.width; h = src.height;
                if (w <= maxWidth) return src.GetPixels32();
                int nw = Mathf.Max(16, maxWidth), nh = Mathf.Max(1, Mathf.RoundToInt(h * (nw / (float)w)));
                var rt = RenderTexture.GetTemporary(nw, nh, 0);
                var prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var dst = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                dst.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                dst.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                var px = dst.GetPixels32();
                UnityEngine.Object.Destroy(dst);
                w = nw; h = nh;
                return px;
            }
            finally { UnityEngine.Object.Destroy(src); }
        }

        static byte[] Encode(Color32[] px, int w, int h, int quality)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            var jpg = tex.EncodeToJPG(Mathf.Clamp(quality, 10, 100));
            UnityEngine.Object.Destroy(tex);
            return jpg;
        }

        static IEnumerator Screenshot(JObject a, ToolResponse r)
        {
            yield return new WaitForEndOfFrame();
            int w, h;
            var px = Grab(a.Int("maxWidth", 1024), out w, out h);
            float scale = w / (float)Screen.width;
            r.Text("screen " + Screen.width + "x" + Screen.height + ", image " + w + "x" + h + ", frame " + Time.frameCount
                   + (Mathf.Abs(scale - 1f) > 0.001f ? ". Image is scaled: screen_coord = image_coord * " + (1f / scale).ToString("0.###") : ". Image coords = screen coords"));
            if (a.Bool("annotate")) r.Text(Annotate(px, w, h, scale));
            r.Image(Encode(px, w, h, a.Int("quality", 75)), "image/jpeg");
        }

        // ---------------- annotation (boxes + ids drawn into the pixels) ----------------

        static readonly string[] Digits =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111"
        };

        static readonly Color32[] Palette =
        {
            new Color32(255, 64, 64, 255), new Color32(64, 200, 64, 255), new Color32(64, 128, 255, 255),
            new Color32(255, 160, 0, 255), new Color32(220, 64, 220, 255), new Color32(0, 200, 200, 255)
        };

        static void Px(Color32[] px, int w, int h, int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            px[(h - 1 - y) * w + x] = c; // y is top-down here
        }

        static void Fill(Color32[] px, int w, int h, int x0, int y0, int rw, int rh, Color32 c)
        {
            for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Px(px, w, h, x, y, c);
        }

        static void Label(Color32[] px, int w, int h, int x, int y, int number, Color32 bg)
        {
            const int s = 2;
            string str = number.ToString();
            Fill(px, w, h, x, y, str.Length * 4 * s + s, 5 * s + 2 * s, bg);
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < str.Length; i++)
            {
                string glyph = Digits[str[i] - '0'];
                for (int gy = 0; gy < 5; gy++)
                    for (int gx = 0; gx < 3; gx++)
                        if (glyph[gy * 3 + gx] == '1') Fill(px, w, h, x + s + i * 4 * s + gx * s, y + s + gy * s, s, s, white);
            }
        }

        static string Annotate(Color32[] px, int w, int h, float scale)
        {
            var legend = new StringBuilder("annotated elements (label = element id):");
            int n = 0;
            foreach (var e in UiTools.All(true))
            {
                if (!UiTools.Interactive(e)) continue;
                var rc = UiTools.ScreenRect(e);
                if (rc.width < 2 || rc.height < 2 || rc.xMax < 0 || rc.yMax < 0 || rc.x > Screen.width || rc.y > Screen.height) continue;
                var c = Palette[n % Palette.Length];
                int x = Mathf.RoundToInt(rc.x * scale), y = Mathf.RoundToInt(rc.y * scale), rw = Mathf.RoundToInt(rc.width * scale), rh = Mathf.RoundToInt(rc.height * scale);
                Fill(px, w, h, x, y, rw, 2, c); Fill(px, w, h, x, y + rh - 2, rw, 2, c);
                Fill(px, w, h, x, y, 2, rh, c); Fill(px, w, h, x + rw - 2, y, 2, rh, c);
                Label(px, w, h, x, y, UiTools.Id(e), c);
                legend.Append("\n  ").Append(UiTools.Desc(e));
                if (++n >= 80) { legend.Append("\n  ... (first 80 only)"); break; }
            }
            foreach (var go in Ugui.All())
            {
                if (n >= 80) break;
                if (!Ugui.Interactive(go)) continue;
                var rc = Ugui.ScreenRect(go);
                if (rc.width < 2 || rc.height < 2 || rc.xMax < 0 || rc.yMax < 0 || rc.x > Screen.width || rc.y > Screen.height) continue;
                var c = Palette[n % Palette.Length];
                int x = Mathf.RoundToInt(rc.x * scale), y = Mathf.RoundToInt(rc.y * scale), rw = Mathf.RoundToInt(rc.width * scale), rh = Mathf.RoundToInt(rc.height * scale);
                Fill(px, w, h, x, y, rw, 2, c); Fill(px, w, h, x, y + rh - 2, rw, 2, c);
                Fill(px, w, h, x, y, 2, rh, c); Fill(px, w, h, x + rw - 2, y, 2, rh, c);
                Label(px, w, h, x, y, Ugui.Id(go), c);
                legend.Append("\n  ").Append(Ugui.Desc(go));
                n++;
            }
            return n == 0 ? "no interactive UI elements to annotate" : legend.ToString();
        }

        // ---------------- frame recording ----------------

        static void Store(string label)
        {
            int w, h;
            var px = Grab(_recWidth, out w, out h);
            int hash = 17;
            for (int i = 0; i < px.Length; i += 7) { var c = px[i]; hash = hash * 31 + (c.r << 16 | c.g << 8 | c.b); }
            if (_onlyChanges && label == null && hash == _lastHash && Frames.Count > 0) return;
            _lastHash = hash;
            Frames.Add(new Frame { Index = _nextIndex++, FrameNo = Time.frameCount, Time = Time.realtimeSinceStartupAsDouble, Jpg = Encode(px, w, h, 60), Label = label });
            while (Frames.Count > Mathf.Max(1, _cap)) Frames.RemoveAt(0);
        }

        static IEnumerator RecLoop()
        {
            var wait = new WaitForEndOfFrame();
            double next = 0;
            while (_rec)
            {
                yield return wait;
                if (!_rec) break;
                if (Time.realtimeSinceStartupAsDouble < next) continue;
                next = Time.realtimeSinceStartupAsDouble + _interval;
                try { Store(null); } catch (Exception e) { _rec = false; Debug.LogWarning("[UDriver] frame recording stopped: " + e.Message); }
            }
        }

        static IEnumerator OneShot(string label)
        {
            yield return new WaitForEndOfFrame();
            try { Store(label); } catch (Exception e) { Debug.LogWarning("[UDriver] frame capture failed: " + e.Message); }
        }

        /// <summary>Capture one labelled frame into the buffer (used by triggers).</summary>
        public static void RequestFrame(string label)
        {
            if (Driver.Host != null) Driver.Host.StartCoroutine(OneShot(label));
        }

        static string RecStatus()
        {
            if (Frames.Count == 0) return "recording: " + _rec + ", no frames stored";
            var f0 = Frames[0]; var f1 = Frames[Frames.Count - 1];
            var sb = new StringBuilder("recording: " + _rec + ", " + Frames.Count + " frame(s), indices " + f0.Index + ".." + f1.Index
                                       + ", time " + f0.Time.ToString("0.00") + "s.." + f1.Time.ToString("0.00") + "s (now " + Time.realtimeSinceStartupAsDouble.ToString("0.00") + "s)");
            foreach (var f in Frames) if (f.Label != null) sb.Append("\n  labelled: index " + f.Index + " (" + f.Label + ", frame " + f.FrameNo + ")");
            return sb.ToString();
        }

        static void Record(JObject a, ToolResponse r)
        {
            string action = a.Str("action", "status");
            if (action == "start")
            {
                float fps = a.Num("fps", 5);
                _interval = fps <= 0 ? 0 : 1f / fps;
                _recWidth = a.Int("maxWidth", 480);
                _onlyChanges = a.Bool("onlyChanges", true);
                _cap = Mathf.Clamp(Mathf.RoundToInt(a.Num("seconds", 20) * (fps <= 0 ? 60 : fps)), 10, 1500);
                if (!_rec) { _rec = true; Driver.Host.StartCoroutine(RecLoop()); }
            }
            else if (action == "stop") _rec = false;
            else if (action == "clear") Frames.Clear();
            else if (action != "status") throw new ToolException("action must be start, stop, clear or status");
            r.Text(RecStatus());
        }

        static void GetFrames(JObject a, ToolResponse r)
        {
            if (Frames.Count == 0) throw new ToolException("no frames stored - use frames_record start (or a capture_frame trigger) first");
            var want = new List<int>();
            var idx = a["indices"] as JArray;
            if (idx != null) foreach (var t in idx) want.Add(t.ToObject<int>());
            else
            {
                int from = a.Int("from", Frames[Math.Max(0, Frames.Count - 8)].Index), to = a.Int("to", Frames[Frames.Count - 1].Index), step = Mathf.Max(1, a.Int("step", 1));
                for (int i = from; i <= to; i += step) want.Add(i);
            }
            int sent = 0;
            foreach (int i in want)
            {
                var f = Frames.Find(x => x.Index == i);
                if (f == null) continue;
                if (sent >= 8) { r.Text("... limit of 8 frames per call reached; request the rest separately"); break; }
                r.Text("frame index " + f.Index + " | game frame " + f.FrameNo + " | t=" + f.Time.ToString("0.00") + "s" + (f.Label != null ? " | " + f.Label : ""));
                r.Image(f.Jpg, "image/jpeg");
                sent++;
            }
            if (sent == 0) r.Text("none of the requested indices are in the buffer. " + RecStatus());
        }

        // ---------------- time ----------------

        public static void Pause()
        {
            if (Time.timeScale > 0) _savedScale = Time.timeScale;
            Time.timeScale = 0;
        }

        static IEnumerator TimeControl(JObject a, ToolResponse r)
        {
            string action = a.Str("action", "status");
            if (action == "pause") Pause();
            else if (action == "resume") Time.timeScale = _savedScale > 0 ? _savedScale : 1f;
            else if (action == "scale") { Time.timeScale = Mathf.Max(0, a.Num("scale", 1)); if (Time.timeScale > 0) _savedScale = Time.timeScale; }
            else if (action == "step")
            {
                int n = Mathf.Clamp(a.Int("frames", 1), 1, 600);
                Time.timeScale = _savedScale > 0 ? _savedScale : 1f;
                for (int i = 0; i < n; i++) yield return null;
                Time.timeScale = 0;
                r.Text("advanced " + n + " frame(s)");
            }
            else if (action != "status") throw new ToolException("action must be pause, resume, step, scale or status");
            r.Text("timeScale=" + Time.timeScale + " frame=" + Time.frameCount + " time=" + Time.time.ToString("0.000")
                   + (Time.timeScale == 0 ? " (PAUSED: scaled time is frozen; code using unscaled time or Update without deltaTime still runs)" : ""));
        }
    }
}

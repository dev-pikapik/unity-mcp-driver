using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UDriver
{
    /// <summary>net_capture / net_log: the app's own traffic recorded through Harmony patches (UnityWebRequest, HttpClient, ClientWebSocket, Socket).</summary>
    public static class NetCapture
    {
        sealed class Entry
        {
            public long Seq; public DateTime Time; public double Ms;
            public string Kind, Method, Url, Error, ReqHeaders, ResHeaders;
            public int Status; public long Size;
            public byte[] ReqBody, ResBody;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
        }

        const int MaxEntries = 5000;
        const string Id = "udriver.net";
        static readonly LinkedList<Entry> Log = new LinkedList<Entry>();
        static readonly ConditionalWeakTable<object, StringBuilder> UwrHeaders = new ConditionalWeakTable<object, StringBuilder>();
        static readonly ConditionalWeakTable<object, Entry> UwrPending = new ConditionalWeakTable<object, Entry>();
        static readonly ConditionalWeakTable<object, string> WsUrls = new ConditionalWeakTable<object, string>();
        static readonly ConditionalWeakTable<Socket, object> Inner = new ConditionalWeakTable<Socket, object>();
        static readonly System.Threading.AsyncLocal<bool> InHttp = new System.Threading.AsyncLocal<bool>();
        static long _seq;
        static int _pending;
        static object _harmony;
        static readonly List<string> Patched = new List<string>();
        static volatile bool _bodies = true;
        static volatile int _maxBody = 65536;

        internal static void Register()
        {
            Driver.Register("net_capture", Driver.Sync(Capture));
            Driver.Register("net_log", NetLog);
        }

        // ---------------- patching ----------------

        static Type Find(string name, string asm)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(name, false); if (t != null) return t; }
            try { return Type.GetType(name + ", " + asm, false); } catch { return null; }
        }

        static IEnumerable<KeyValuePair<MethodBase, string[]>> Targets()
        {
            var uwr = Find("UnityEngine.Networking.UnityWebRequest", "UnityEngine.UnityWebRequestModule");
            if (uwr != null)
            {
                yield return Pair(uwr.GetMethod("SendWebRequest", Type.EmptyTypes), null, "UwrSent");
                yield return Pair(uwr.GetMethod("SetRequestHeader", new[] { typeof(string), typeof(string) }), "UwrHeader", null);
                yield return Pair(uwr.GetMethod("Dispose", Type.EmptyTypes), "UwrDone", null);
            }
            var req = Find("System.Net.Http.HttpRequestMessage", "System.Net.Http");
            var inv = Find("System.Net.Http.HttpMessageInvoker", "System.Net.Http");
            if (inv != null && req != null) yield return Pair(inv.GetMethod("SendAsync", new[] { req, typeof(System.Threading.CancellationToken) }), "HttpSend", "HttpSent");
            var ws = Find("System.Net.WebSockets.ClientWebSocket", "System");
            if (ws != null)
                foreach (var m in ws.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "ConnectAsync") yield return Pair(m, "WsConnect", "Outside");
                    if (m.Name == "SendAsync") yield return Pair(m, "WsSend", "Outside");
                    if (m.Name == "ReceiveAsync") yield return Pair(m, "Inside", "WsReceived");
                    if (m.Name.StartsWith("Close")) yield return Pair(m, "Inside", "Outside");
                }
            foreach (var m in typeof(Socket).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if ((m.Name == "Send" || m.Name == "Receive") && ps.Length == 5 && ps[0].ParameterType == typeof(byte[]) && ps[4].ParameterType.IsByRef) yield return Pair(m, null, "SocketDone");
                if ((m.Name == "EndSend" || m.Name == "EndReceive") && ps.Length == 2) yield return Pair(m, null, "SocketEnd");
                if (m.Name == "BeginSend" || m.Name == "BeginReceive" || m.Name == "SendAsync" || m.Name == "ReceiveAsync") yield return Pair(m, "SocketStart", null);
            }
        }

        static KeyValuePair<MethodBase, string[]> Pair(MethodBase m, string prefix, string postfix) { return new KeyValuePair<MethodBase, string[]>(m, new[] { prefix, postfix }); }

        static void Start(ToolResponse r)
        {
            var h = ProbeTools.FindHarmony();
            if (h == null) throw new ToolException("net_capture needs 0Harmony.dll next to the app's assemblies (Lib.Harmony 2.4+, net48 build, in Assets/Plugins of the project or the game's Managed folder). Mono only, not IL2CPP.");
            if (_harmony != null) { r.Text("already capturing (" + Patched.Count + " methods); bodies=" + _bodies + ", maxBody=" + _maxBody + ". Read with net_log since=" + _seq + "."); return; }
            MethodInfo patch = null;
            foreach (var m in h.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "Patch" && m.GetParameters().Length >= 3 && m.GetParameters()[0].ParameterType == typeof(MethodBase)) patch = m;
            if (patch == null) throw new ToolException("unsupported Harmony version (Harmony.Patch not found)");
            _harmony = Activator.CreateInstance(h, Id);
            var failed = new List<string>();
            foreach (var t in Targets())
            {
                if (t.Key == null) continue;
                string name = t.Key.DeclaringType.Name + "." + t.Key.Name;
                try
                {
                    var argv = new object[patch.GetParameters().Length];
                    argv[0] = t.Key;
                    for (int i = 0; i < 2; i++) if (t.Value[i] != null) argv[i + 1] = ProbeTools.HMethod(typeof(NetCapture).GetMethod(t.Value[i], BindingFlags.NonPublic | BindingFlags.Static));
                    patch.Invoke(_harmony, argv);
                    if (!Patched.Contains(name)) Patched.Add(name);
                }
                catch (Exception e) { failed.Add(name + " (" + (e.InnerException ?? e).Message + ")"); }
            }
            if (Patched.Count == 0) { Stop(); throw new ToolException("could not patch any network method" + (failed.Count > 0 ? ": " + failed[0] : " (none of UnityWebRequest, HttpClient, ClientWebSocket, Socket found)")); }
            r.Text("capturing: " + string.Join(", ", Patched) + ". bodies=" + _bodies + ", maxBody=" + _maxBody + ". Read with net_log since=" + _seq + ".");
            if (failed.Count > 0) r.Text(failed.Count + " method(s) not patched, e.g. " + failed[0]);
        }

        static void Stop()
        {
            if (_harmony == null) return;
            try { _harmony.GetType().GetMethod("UnpatchAll", new[] { typeof(string) }).Invoke(_harmony, new object[] { Id }); }
            finally { _harmony = null; Patched.Clear(); }
        }

        static void Capture(JObject a, ToolResponse r)
        {
            string action = a.Str("action", "status");
            if (a.Has("bodies")) _bodies = a.Bool("bodies");
            if (a.Has("maxBody")) _maxBody = Mathf.Clamp(a.Int("maxBody"), 0, 16 << 20);
            if (action == "start") Start(r);
            else if (action == "stop")
            {
                bool was = _harmony != null;
                Stop();
                r.Text(was ? "capture stopped, all network patches removed; " + Count() + " entries kept (net_log, or action=clear)" : "capture was not running");
            }
            else if (action == "clear") { lock (Log) Log.Clear(); r.Text("log cleared; capture " + (_harmony != null ? "still running" : "is off") + ", next seq " + (_seq + 1)); }
            else if (action == "status")
            {
                var kinds = new SortedDictionary<string, int>();
                lock (Log) foreach (var e in Log) { int n; kinds.TryGetValue(e.Kind, out n); kinds[e.Kind] = n + 1; }
                var parts = new List<string>();
                foreach (var k in kinds) parts.Add(k.Key + " " + k.Value);
                r.Text((_harmony != null ? "capturing " + string.Join(", ", Patched) : "not capturing" + (ProbeTools.HarmonyAvailable ? "" : " (0Harmony.dll is not loaded in this app)"))
                       + "\n" + Count() + " entries (" + (parts.Count > 0 ? string.Join(", ", parts) : "none") + "), last seq " + _seq + ", " + _pending + " in flight; bodies=" + _bodies + ", maxBody=" + _maxBody);
            }
            else throw new ToolException("action must be start, stop, status or clear");
        }

        static int Count() { lock (Log) return Log.Count; }

        // ---------------- recording ----------------

        static byte[] Keep(byte[] b, int offset = 0, int count = -1)
        {
            if (b == null || !_bodies) return null;
            if (count < 0) count = b.Length - offset;
            var k = new byte[Math.Max(0, Math.Min(Math.Min(count, b.Length - offset), _maxBody))];
            Buffer.BlockCopy(b, offset, k, 0, k.Length);
            return k;
        }

        static void Add(Entry e)
        {
            e.Ms = e.Clock.Elapsed.TotalMilliseconds;
            lock (Log)
            {
                if (_harmony == null) return;
                e.Seq = ++_seq;
                Log.AddLast(e);
                if (Log.Count > MaxEntries) Log.RemoveFirst();
            }
        }

        static Entry New(string kind, string method, string url) { return new Entry { Kind = kind, Method = method, Url = url, Time = DateTime.Now }; }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p != null ? p.GetValue(o, null) : o.GetType().GetMethod(name, Type.EmptyTypes).Invoke(o, null);
        }

        static string Headers(object headers)
        {
            var sb = new StringBuilder();
            var dict = headers as IDictionary;
            if (dict != null) foreach (DictionaryEntry kv in dict) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            else if (headers is IEnumerable<KeyValuePair<string, IEnumerable<string>>>)
                foreach (var kv in (IEnumerable<KeyValuePair<string, IEnumerable<string>>>)headers) sb.Append(kv.Key).Append(": ").Append(string.Join(", ", kv.Value)).Append('\n');
            return sb.ToString();
        }

        // Harmony binds by name: __instance, __args, __result, __state.
        static void UwrHeader(object __instance, object[] __args)
        {
            try { UwrHeaders.GetOrCreateValue(__instance).Append(__args[0]).Append(": ").Append(__args[1]).Append('\n'); } catch { }
        }

        static void UwrSent(object __instance, object __result)
        {
            var op = __result as AsyncOperation;
            if (op == null) return;
            try
            {
                var e = New("http", (string)Get(__instance, "method"), (string)Get(__instance, "url"));
                StringBuilder h;
                if (UwrHeaders.TryGetValue(__instance, out h)) e.ReqHeaders = h.ToString();
                try { e.ReqBody = Keep(Get(Get(__instance, "uploadHandler"), "data") as byte[]); } catch { }
                System.Threading.Interlocked.Increment(ref _pending);
                UwrPending.Remove(__instance); UwrPending.Add(__instance, e);
                op.completed += _ => UwrDone(__instance);
            }
            catch { }
        }

        static void UwrDone(object __instance)
        {
            Entry e;
            if (!UwrPending.TryGetValue(__instance, out e)) return;
            UwrPending.Remove(__instance);
            System.Threading.Interlocked.Decrement(ref _pending);
            var req = __instance;
            try
            {
                if (!(bool)Get(req, "isDone")) e.Error = "disposed before it finished";
                e.Status = (int)(long)Get(req, "responseCode");
                e.Error = Get(req, "error") as string;
                e.ResHeaders = Headers(Get(req, "GetResponseHeaders"));
                e.Size = (long)(ulong)Get(req, "downloadedBytes");
                try { e.ResBody = Keep(Get(Get(req, "downloadHandler"), "data") as byte[]); } catch { }
            }
            catch (Exception x) { e.Error = e.Error ?? "response not readable: " + (x.InnerException ?? x).Message; }
            Add(e);
        }

        static void HttpSend(object[] __args, out object __state)
        {
            __state = null;
            InHttp.Value = true;
            try
            {
                var req = __args[0];
                var e = New("http", Convert.ToString(Get(req, "Method")), Convert.ToString(Get(req, "RequestUri")));
                var content = Get(req, "Content");
                e.ReqHeaders = Headers(Get(req, "Headers")) + (content != null ? Headers(Get(content, "Headers")) : "");
                for (var t = content != null ? content.GetType() : null; t != null; t = t.BaseType)
                    if (t.Name == "ByteArrayContent")
                    {
                        var read = (Task<byte[]>)Get(content, "ReadAsByteArrayAsync");
                        if (read.Status == TaskStatus.RanToCompletion) e.ReqBody = Keep(read.Result);
                        break;
                    }
                __state = e;
            }
            catch { }
        }

        static void HttpSent(MethodBase __originalMethod, ref object __result, object __state)
        {
            InHttp.Value = false;
            if (__state != null) Wrap(__originalMethod, ref __result, (Entry)__state, HttpDone);
        }

        static async Task HttpDone(Entry e, object res)
        {
            e.Status = Convert.ToInt32(Get(res, "StatusCode"));
            var content = Get(res, "Content");
            if (content == null) return;
            var ch = Get(content, "Headers");
            e.ResHeaders = Headers(Get(res, "Headers")) + Headers(ch);
            var len = Get(ch, "ContentLength") as long?;
            if (Convert.ToString(Get(ch, "ContentType")).StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) || len > 16 << 20) { e.Size = len ?? -1; return; }
            await ((Task)Get(content, "LoadIntoBufferAsync")).ConfigureAwait(false);
            var bytes = await ((Task<byte[]>)Get(content, "ReadAsByteArrayAsync")).ConfigureAwait(false);
            e.Size = bytes.Length; e.ResBody = Keep(bytes);
        }

        /// <summary>Replaces a Task&lt;T&gt; or ValueTask&lt;T&gt; result with one that records the entry when the original finishes, then hands the same result to the app.</summary>
        static void Wrap(MethodBase m, ref object result, Entry e, Func<Entry, object, Task> done)
        {
            var t = ((MethodInfo)m).ReturnType;
            if (!t.IsGenericType) return;
            bool value = t.GetGenericTypeDefinition() == typeof(ValueTask<>);
            var task = value ? t.GetMethod("AsTask").Invoke(result, null) : result;
            if (!(task is Task)) return;
            var wrapped = typeof(NetCapture).GetMethod("Wait", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(t.GetGenericArguments()[0]).Invoke(null, new[] { task, e, done });
            result = value ? Activator.CreateInstance(t, wrapped) : wrapped;
        }

        static async Task<T> Wait<T>(Task<T> task, Entry e, Func<Entry, object, Task> done)
        {
            System.Threading.Interlocked.Increment(ref _pending);
            try
            {
                var res = await task.ConfigureAwait(false);
                try { await done(e, res).ConfigureAwait(false); }
                catch (Exception x) { e.Error = "response not readable: " + (x.InnerException ?? x).Message; }
                return res;
            }
            catch (Exception x) { e.Error = x.GetType().Name + ": " + x.Message; throw; }
            finally { System.Threading.Interlocked.Decrement(ref _pending); Add(e); }
        }

        static string WsUrl(object ws) { string u; return WsUrls.TryGetValue(ws, out u) ? u : "ws"; }

        static void WsConnect(object __instance, object[] __args)
        {
            InHttp.Value = true;
            try
            {
                string url = Convert.ToString(__args[0]);
                WsUrls.Remove(__instance); WsUrls.Add(__instance, url);
                Add(New("websocket", "connect", url));
            }
            catch { }
        }

        static void WsSend(object __instance, object[] __args)
        {
            InHttp.Value = true;
            try
            {
                var data = __args[0] is ArraySegment<byte> ? ((ArraySegment<byte>)__args[0]).ToArray() : ((ReadOnlyMemory<byte>)__args[0]).ToArray();
                var e = New("websocket", "send " + __args[1], WsUrl(__instance));
                e.Size = data.Length; e.ReqBody = Keep(data);
                Add(e);
            }
            catch { }
        }

        static void WsReceived(object __instance, MethodBase __originalMethod, object[] __args, ref object __result)
        {
            InHttp.Value = false;
            try
            {
                var buf = __args[0] is ArraySegment<byte> ? (Memory<byte>)(ArraySegment<byte>)__args[0] : (Memory<byte>)__args[0];
                Wrap(__originalMethod, ref __result, New("websocket", "receive", WsUrl(__instance)), (e, res) =>
                {
                    int n = Convert.ToInt32(Get(res, "Count"));
                    e.Method = "receive " + Get(res, "MessageType"); e.Size = n; e.ResBody = Keep(buf.Slice(0, n).ToArray());
                    return Task.FromResult(0);
                });
            }
            catch { }
        }

        static void Inside() { InHttp.Value = true; }
        static void Outside() { InHttp.Value = false; }

        /// <summary>Sockets first used inside an HttpClient or ClientWebSocket call are already recorded at that level.</summary>
        static void SocketStart(Socket __instance)
        {
            if (InHttp.Value) Inner.GetValue(__instance, _ => Id);
        }

        static void SocketDone(Socket __instance, MethodBase __originalMethod, object[] __args, int __result)
        {
            try { SocketAdd(__instance, __originalMethod.Name.EndsWith("Send"), (byte[])__args[0], (int)__args[1], __result); } catch { }
        }

        static void SocketEnd(Socket __instance, MethodBase __originalMethod, object[] __args, int __result)
        {
            try
            {
                var ar = __args[0];
                var mem = (Memory<byte>)ar.GetType().GetField("Buffer").GetValue(ar);
                bool send = __originalMethod.Name == "EndSend";
                int offset = (int)ar.GetType().GetField("Offset").GetValue(ar) - (send ? __result : 0);
                SocketAdd(__instance, send, _bodies && offset >= 0 && mem.Length >= offset + __result ? mem.Slice(offset, __result).ToArray() : null, 0, __result);
            }
            catch { }
        }

        static void SocketAdd(Socket s, bool send, byte[] data, int offset, int n)
        {
            object inner;
            if (n <= 0 || InHttp.Value || Inner.TryGetValue(s, out inner)) return;
            var local = s.LocalEndPoint as IPEndPoint;
            if (local != null && local.Port == Driver.Port) return;
            var e = New("socket", send ? "send" : "receive", "tcp://" + s.RemoteEndPoint);
            e.Size = n;
            if (send) e.ReqBody = Keep(data, offset, n); else e.ResBody = Keep(data, offset, n);
            Add(e);
        }
        // ---------------- reading ----------------

        static string Decode(byte[] b)
        {
            if (b == null) return null;
            foreach (var c in b) if (c < 9 || (c > 13 && c < 32)) return null;
            return Encoding.UTF8.GetString(b);
        }

        static string Body(byte[] b, int max)
        {
            var s = Decode(b);
            if (s != null) return Util.Cut(s, max);
            var sb = new StringBuilder("hex");
            for (int i = 0; i < b.Length && i < max / 3; i++) sb.Append(' ').Append(b[i].ToString("x2"));
            return sb.Append(b.Length > max / 3 ? " ..." : "").ToString();
        }

        static string Bytes(long n) { return n < 0 ? "? B" : n < 1024 ? n + " B" : (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"; }

        static long SizeOf(Entry e) { return e.Size != 0 ? e.Size : (e.ResBody ?? e.ReqBody ?? new byte[0]).Length; }

        static string Line(Entry e)
        {
            var b = e.ResBody ?? e.ReqBody;
            return "#" + e.Seq + " " + e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + e.Kind + " " + e.Method + " " + Util.Cut(e.Url, 120)
                   + (e.Status != 0 ? " " + e.Status : "") + (e.Kind == "http" || SizeOf(e) > 0 ? " " + Bytes(SizeOf(e)) : "") + (e.Kind == "http" ? " " + e.Ms.ToString("0", CultureInfo.InvariantCulture) + " ms" : "")
                   + (e.Error != null ? " ERROR " + Util.OneLine(e.Error, 100) : "") + (b != null && b.Length > 0 ? "  " + Util.OneLine(Body(b, 80), 80) : "");
        }

        static string Full(Entry e)
        {
            var sb = new StringBuilder("#" + e.Seq + " " + e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + e.Kind + " " + e.Method + " " + e.Url + "\n");
            sb.Append(e.Status != 0 ? "status " + e.Status + ", " : "").Append(Bytes(SizeOf(e))).Append(e.Kind == "http" ? ", " + e.Ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "").Append(e.Error != null ? "\nerror: " + e.Error : "").Append('\n');
            Section(sb, "request headers", e.ReqHeaders);
            if (e.ReqBody != null) Section(sb, (e.Kind == "http" ? "request body" : "data") + " (" + Bytes(e.ReqBody.Length) + " kept)", Body(e.ReqBody, 65536));
            Section(sb, "response headers", e.ResHeaders);
            if (e.ResBody != null) Section(sb, (e.Kind == "http" ? "response body" : "data") + " (" + Bytes(e.ResBody.Length) + " kept)", Body(e.ResBody, 65536));
            if (e.ReqBody == null && e.ResBody == null && SizeOf(e) > 0) sb.Append("body not kept (bodies were off, or the body is a stream)\n");
            return sb.ToString();
        }

        static void Section(StringBuilder sb, string title, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            sb.Append(title).Append(":\n");
            foreach (var l in text.TrimEnd('\n').Split('\n')) sb.Append("  ").Append(l).Append('\n');
        }

        static Entry Lookup(long seq)
        {
            lock (Log) foreach (var e in Log) if (e.Seq == seq) return e;
            throw new ToolException("no entry #" + seq + (seq <= _seq ? " (dropped: the log keeps the newest " + MaxEntries + ", or it was cleared)" : " yet (last seq " + _seq + ")"));
        }

        static IEnumerator NetLog(JObject a, ToolResponse r)
        {
            if (a.Has("id"))
            {
                var e = Lookup(a.Long("id"));
                if (!a.Bool("trace")) { r.Text(Full(e)); yield break; }
                var it = Trace(e, r);
                while (it.MoveNext()) yield return it.Current;
                yield break;
            }
            if (a.Bool("trace")) throw new ToolException("trace needs id = the seq of a response, e.g. net_log id=12 trace=true");
            long since = a.Long("since", -1);
            string contains = a.Str("contains"), kind = a.Str("kind");
            if (kind != null && kind != "http" && kind != "websocket" && kind != "socket") throw new ToolException("kind must be http, websocket or socket");
            int limit = Mathf.Clamp(a.Int("limit", 30), 1, 500);
            var all = new List<Entry>();
            lock (Log) foreach (var e in Log) if (e.Seq > since && (kind == null || e.Kind == kind)) all.Add(e);
            if (contains != null) all = all.FindAll(e => Util.Contains(e.Url, contains) || Util.Contains(e.Method, contains) || Util.Contains(Decode(e.ReqBody), contains) || Util.Contains(Decode(e.ResBody), contains));
            if (all.Count == 0)
            {
                r.Text(_harmony == null && _seq == 0 ? "nothing recorded - start with net_capture action=start, then use the app" : "no entries" + (since >= 0 ? " after #" + since : "") + (_harmony == null ? "; capture is off" : "") + ". Last seq " + _seq + ".");
                yield break;
            }
            var show = since >= 0 ? all.GetRange(0, Math.Min(limit, all.Count)) : all.GetRange(Math.Max(0, all.Count - limit), Math.Min(limit, all.Count));
            foreach (var e in show) r.Text(Line(e));
            long last = show[show.Count - 1].Seq;
            r.Text(since >= 0 && all.Count > show.Count ? (all.Count - show.Count) + " more - net_log since=" + last : (since < 0 && all.Count > show.Count ? (all.Count - show.Count) + " older not shown. " : "") + "Next: net_log since=" + _seq + "; one in full: net_log id=" + last + (_harmony == null ? " (capture is off)" : ""));
        }

        // ---------------- trace ----------------

        static void Wanted(JToken t, List<KeyValuePair<string, object>> list, HashSet<string> seen)
        {
            foreach (var v in t is JValue ? new[] { t } : (IEnumerable<JToken>)((JContainer)t).DescendantsAndSelf())
            {
                var jv = v as JValue;
                if (jv == null || jv.Value == null || list.Count >= 200) continue;
                double d;
                if (jv.Type == JTokenType.Float || jv.Type == JTokenType.Integer) Offer(Convert.ToDouble(jv.Value, CultureInfo.InvariantCulture), jv.Path, list, seen);
                else if (jv.Type == JTokenType.String)
                {
                    string s = (string)jv.Value;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) Offer(d, jv.Path, list, seen);
                    else if (s.Length >= 4 && seen.Add("s" + s)) list.Add(new KeyValuePair<string, object>(jv.Path, s));
                }
            }
        }

        static void Offer(double d, string label, List<KeyValuePair<string, object>> list, HashSet<string> seen)
        {
            if ((d != Math.Floor(d) || Math.Abs(d) > 999) && !double.IsInfinity(d) && seen.Add("d" + d.ToString("R", CultureInfo.InvariantCulture))) list.Add(new KeyValuePair<string, object>(label, d));
        }

        static bool Same(object v, object want)
        {
            var s = want as string;
            if (s != null) return v is string && (string)v == s;
            if (v is string || v is bool || v is char || v is Enum || !(v is IConvertible)) return false;
            double d = (double)want, x;
            try { x = Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return false; }
            double tol = v is float ? 1e-4 : v is double || v is decimal ? 1e-6 : 0;
            return tol == 0 ? x == d : Math.Abs(x - d) <= tol * Math.Abs(d);
        }

        static IEnumerator Trace(Entry e, ToolResponse r)
        {
            string text = Decode(e.ResBody);
            if (text == null) throw new ToolException("entry #" + e.Seq + " has no text response to trace" + (e.ResBody == null ? (_bodies ? "" : " (bodies were off when it was recorded)") : " (binary body)"));
            var want = new List<KeyValuePair<string, object>>();
            var seen = new HashSet<string>();
            try { Wanted(JToken.Parse(text), want, seen); }
            catch (Newtonsoft.Json.JsonException)
            {
                if (text.Trim().Length >= 4 && text.Length <= 200) want.Add(new KeyValuePair<string, object>("body", text.Trim()));
                foreach (Match m in Regex.Matches(text, @"-?\d+(?:\.\d+)?"))
                    if (want.Count < 200) Offer(double.Parse(m.Value, CultureInfo.InvariantCulture), "@" + m.Index, want, seen);
            }
            if (want.Count == 0) throw new ToolException("entry #" + e.Seq + " has no distinctive values to look for (numbers with a fraction or above 999, strings of 4+ characters)");

            var hits = new List<string>[want.Count];
            var visited = new HashSet<object>(new MemoryTools.ByRef());
            var stack = new Stack<KeyValuePair<object, string>>();
            var depth = new Stack<int>();
            var slice = Stopwatch.StartNew();
            int nodes = 0, frames = 0;
            Action<object, string, int> visit = null;
            visit = (v, path, d) =>
            {
                if (v == null) return;
                var t = v.GetType();
                if (t.IsPrimitive || v is string || v is decimal)
                {
                    for (int i = 0; i < want.Count; i++)
                        if (Same(v, want[i].Value)) (hits[i] ?? (hits[i] = new List<string>())).Add(path);
                    return;
                }
                if (t.IsEnum || d > 3 || (v is UnityEngine.Object && d > 0) || !visited.Add(v)) return;
                stack.Push(new KeyValuePair<object, string>(v, path)); depth.Push(d);
            };
            foreach (var t in StateTools.GameTypes(false))
            {
                if (t.ContainsGenericParameters || StateTools.Why(t, "") != null) continue;
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral) continue;
                    object v;
                    try { v = f.GetValue(null); } catch { continue; }
                    visit(v, "@" + t.FullName + MemoryTools.Label(f), 1);
                }
            }
            foreach (var go in StateTools.RootObjects())
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && !StateTools.IsEngine(mb.GetType().Assembly)) visit(mb, StateTools.PathOf(mb.gameObject) + ":" + mb.GetType().Name, 0);
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
                if (so != null && !StateTools.IsEngine(so.GetType().Assembly)) visit(so, StateTools.IdOf(so), 0);

            while (stack.Count > 0 && nodes < 300000)
            {
                var top = stack.Pop(); int d = depth.Pop();
                var o = top.Key; string path = top.Value;
                nodes++;
                if ((nodes & 255) == 0 && slice.Elapsed.TotalMilliseconds > StateTools.FrameBudgetMs) { frames++; yield return null; slice.Reset(); slice.Start(); }
                if (o is UnityEngine.Object && (UnityEngine.Object)o == null) continue;
                var dict = o as IDictionary;
                if (dict != null)
                {
                    int n = 0;
                    try { foreach (DictionaryEntry de in dict) { if (n++ >= 200) break; visit(de.Value, path + "[" + de.Key + "]", d); } } catch (InvalidOperationException) { }
                    continue;
                }
                if (o is IEnumerable)
                {
                    int n = 0;
                    try { foreach (var x in (IEnumerable)o) { if (n >= 200) break; visit(x, path + "[" + n++ + "]", d); } } catch (InvalidOperationException) { }
                    continue;
                }
                for (var t = o.GetType(); t != null && !StateTools.IsEngine(t.Assembly); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (StateTools.Why(t, f.Name) != null) continue;
                        object v;
                        try { v = f.GetValue(o); } catch { continue; }
                        visit(v, path + MemoryTools.Label(f), d + 1);
                    }
            }

            r.Text("trace #" + e.Seq + " " + e.Method + " " + Util.Cut(e.Url, 100) + ": " + want.Count + " distinctive value(s), " + nodes + " objects searched" + (nodes >= 300000 ? " (stopped at the limit)" : "") + (frames > 0 ? " over " + frames + " frames" : ""));
            var missing = new List<string>();
            for (int i = 0; i < want.Count; i++)
            {
                string label = want[i].Key + " = " + (want[i].Value is string ? "\"" + Util.OneLine((string)want[i].Value, 60) + "\"" : ((double)want[i].Value).ToString("R", CultureInfo.InvariantCulture));
                if (hits[i] == null) { missing.Add(label); continue; }
                r.Text(label);
                for (int k = 0; k < hits[i].Count && k < 10; k++) r.Text("  -> " + hits[i][k]);
                if (hits[i].Count > 10) r.Text("  ... and " + (hits[i].Count - 10) + " more");
            }
            if (missing.Count == want.Count) r.Text("none of these values is held by a game object field right now (searched scene MonoBehaviours, ScriptableObjects and static fields, 3 levels deep)");
            if (missing.Count > 0) r.Text("not found: " + Util.Cut(string.Join("; ", missing), 600));
            r.Text("paths work with state_get / watch_add");
        }
    }
}

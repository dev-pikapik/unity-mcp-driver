#!/usr/bin/env python3
# native/test.py [game.app] [launches]: injects the agent into a built IL2CPP game and drives every tool.
import base64, http.client, json, os, shutil, signal, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.expanduser("~/udriver-il2cpp-work")
APP = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] else WORK + "/build/TestGame.app"
LAUNCHES = int(sys.argv[2]) if len(sys.argv) > 2 else 10
HOME = WORK + "/home-test"
LOGS = WORK + "/logs"
TOKEN = "test-token-0123456789abcdef0123456789"
ON = {"X-UDriver-Token": TOKEN, "X-UDriver-Computer": "1"}  # the server sends X-UDriver-Computer: 1 only when computer access is on
exe = APP + "/Contents/MacOS/" + os.path.basename(APP)[:-4]
fails = []


def check(cond, what):
    print(("ok   " if cond else "FAIL ") + what)
    if not cond: fails.append(what)
    return cond


def launch(n, paired=True):
    shutil.rmtree(HOME, ignore_errors=True); os.makedirs(HOME); os.makedirs(LOGS, exist_ok=True)
    env = dict(os.environ, HOME=HOME, DYLD_INSERT_LIBRARIES=HERE + "/udriver-il2cpp.dylib")
    env.pop("UDRIVER_UNSAFE", None); env.pop("UDRIVER_TOKEN", None)
    if paired: env["UDRIVER_TOKEN"] = TOKEN
    log = open(f"{LOGS}/game-{n}.log", "w")
    port = ["-udriver-port", os.environ.get("UDRIVER_PORT", "18890")]
    p = subprocess.Popen([exe, "-screen-fullscreen", "0", "-screen-width", "960", "-screen-height", "600", "-logFile", "-"] + port, env=env, stdout=log, stderr=log)
    t0 = time.time()
    while time.time() - t0 < 60 and p.poll() is None:
        js = [f for f in os.listdir(HOME + "/.udriver") if f.endswith(".json")] if os.path.isdir(HOME + "/.udriver") else []
        if js:
            info = json.load(open(HOME + "/.udriver/" + js[0]))
            return p, info, time.time() - t0
        time.sleep(0.2)
    return p, None, time.time() - t0


def quit(p, info):
    p.send_signal(signal.SIGTERM)
    try: p.wait(15)
    except subprocess.TimeoutExpired: p.kill(); p.wait()
    left = os.listdir(HOME + "/.udriver") if os.path.isdir(HOME + "/.udriver") else []
    return left


def req(port, verb, path, body=None, headers=None, raw=None):
    c = http.client.HTTPConnection("127.0.0.1", port, timeout=130)
    h = {"Host": f"127.0.0.1:{port}"}
    if headers is not None: h.update(headers)
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    if data is not None: h.setdefault("Content-Type", "application/json")
    try:
        c.request(verb, path, data, h)
        r = c.getresponse()
        return r.status, r.read()
    except (ConnectionError, OSError) as e:
        return 0, str(e).encode()


def startup_loop():
    for n in range(LAUNCHES):
        p, info, dt = launch(n, n % 2 == 0)  # odd launches are started by hand and paired like UDriver 2.0 does
        ok = info is not None and req(info["port"], "GET", "/ping")[0] == 200
        if ok and n % 2:
            ok = req(info["port"], "POST", "/call", {"tool": "status", "args": {}}, {"X-UDriver-Token": TOKEN})[0] == 401 \
                and req(info["port"], "POST", "/pair", headers={"X-UDriver-Token": "short"})[0] == 409 \
                and req(info["port"], "POST", "/pair", headers={"X-UDriver-Token": TOKEN})[0] == 200
        if ok:
            st, b = req(info["port"], "POST", "/call", {"tool": "status", "args": {}}, {"X-UDriver-Token": TOKEN})
            ok = st == 200 and json.loads(b)["ok"]
        alive = p.poll() is None
        left = quit(p, info) if alive else ["crashed"]
        check(ok and alive and not left, f"launch {n + 1}: up in {dt:.1f}s, status call ok={ok}, files left={left}")


def call(port, tool, args=None, headers=None):
    st, b = req(port, "POST", "/call", {"tool": tool, "args": args or {}}, dict(ON, **(headers or {})))
    r = json.loads(b) if st == 200 else {"isError": True, "content": [{"type": "text", "text": b.decode(errors="replace")}]}
    text = "\n".join(c.get("text", "") for c in r.get("content", []))
    with open(f"{LOGS}/test-calls.log", "a") as f: f.write(f"== {tool} {json.dumps(args)} isError={r.get('isError')}\n{text}\n")
    return r.get("isError"), text, r.get("content", [])


def refused(port, tool, args, what):
    err, text, _ = call(port, tool, args)
    reason = "safety policy" if "blocked by the safety policy" in text else "absent from this build" if any(w in text for w in ("not found", "no type found", "no method", "no writable", "not available")) else None
    check(err and reason is not None, f"refuses {what} ({reason or text[:120]})")


def raw_status(port, head, body=b""):
    import socket
    s = socket.create_connection(("127.0.0.1", port), 10)
    try:
        s.sendall(head.encode() + body)
        return int(s.recv(64).split()[1])
    except (OSError, IndexError, ValueError): return 0
    finally: s.close()


def tools_run():
    p, info, dt = launch("tools")
    if not check(info is not None, f"agent published {HOME}/.udriver/<port>.json in {dt:.1f}s"): return quit(p, info)
    port = info["port"]
    try:
        check(info["kind"] == "game" and info["pid"] == p.pid and info["project"] == exe and info["unity"].startswith("6"), f"publish file {info}")
        check(not os.path.exists(HOME + f"/.udriver/{port}.token") and os.stat(HOME + f"/.udriver/{port}.json").st_mode & 0o777 == 0o600 and os.stat(HOME + "/.udriver").st_mode & 0o777 == 0o700, "no token file; modes 0600/0700")
        check(req(port, "POST", "/pair", headers={"X-UDriver-Token": TOKEN})[0] == 200, "pair again with the same key")
        check(req(port, "POST", "/pair", headers={"X-UDriver-Token": "x" * 40})[0] == 409 and req(port, "GET", "/tools", headers={"X-UDriver-Token": "x" * 40})[0] == 401, "pair with another key -> 409, key not taken")
        st, b = req(port, "GET", "/ping", headers={})
        check(st == 200 and json.loads(b) == {"ok": True, "pid": p.pid}, "ping without token")
        st, b = req(port, "GET", "/tools", headers=ON)
        names = [t["name"] for t in json.loads(b)["tools"]] if st == 200 else []
        check(len(names) == 23 and "screenshot" in names and "profiler_get" not in names, f"/tools lists only implemented tools ({len(names)})")
        writes = ("state_set", "state_call", "ui_set_value")
        for off in ({"X-UDriver-Token": TOKEN}, {"X-UDriver-Token": TOKEN, "X-UDriver-Computer": "yes"}):
            st, b = req(port, "GET", "/tools", headers=off)
            left = [t["name"] for t in json.loads(b)["tools"]] if st == 200 else []
            check(len(left) == 20 and not set(writes) & set(left) and "ui_click" in left and "type_text" in left, f"computer access off ({off.get('X-UDriver-Computer', 'no header')}): /tools hides the changes ({len(left)})")

        err, text, _ = call(port, "status")
        check(not err and "unity 6" in text and "restricted" in text and "computer access: on" in text, "status")
        off = {"X-UDriver-Computer": "0"}
        _, before, _ = call(port, "state_get", {"path": "/Canvas:Game.clicks"}, off)
        for tool, a in (("state_set", {"path": "/Canvas:Game.clicks", "value": 99}), ("state_call", {"path": "/Canvas:Game", "method": "Play"}),
                        ("ui_set_value", {"name": "NameInput", "value": "Zed"})):
            err, text, _ = call(port, tool, a, off)
            check(err and text.startswith(f"ERROR: Not done: '{tool}' changes the game, and Computer access is off") and "turn Computer access on in the UDriver panel" in text, f"computer access off: {tool} refused calmly")
        err, text, _ = call(port, "batch", {"calls": [{"tool": "state_get", "args": {"path": "/Canvas:Game.clicks"}}, {"tool": "state_set", "args": {"path": "/Canvas:Game.clicks", "value": 99}}]}, off)
        _, after, _ = call(port, "state_get", {"path": "/Canvas:Game.clicks"}, off)
        check(err and "[1] state_set" in text and "Computer access is off" in text and after == before and before.startswith("/Canvas:Game.clicks = "), "computer access off: a change inside batch is refused, nothing changed")
        err, text, _ = call(port, "status", headers=off)
        check(not err and "computer access: off" in text, "computer access off: status says so")
        err, text, _ = call(port, "scene_tree")
        check(not err and "Canvas" in text and "PlayButton" in text, "scene_tree shows the Canvas")
        err, text, _ = call(port, "locate", {"path": "/Canvas/MainScreen/PlayButton"})
        check(not err and "center (480,202)" in text, "locate")
        err, text, _ = call(port, "ui_tree")
        check(not err and 'canvas "Canvas"' in text and '"PlayButton" "Play"' in text, "ui_tree finds the Canvas and Play")
        main = next((l.split()[0] for l in text.splitlines() if '"PlayButton"' in l), "#0")
        err, t1, _ = call(port, "ui_tree", {"root": main})
        err2, t2, _ = call(port, "ui_tree", {"id": main})
        check(not err and not err2 and t1.startswith(main + ' Button "PlayButton"') and t1 == t2 and 'canvas "Canvas"' not in t1, f"ui_tree root={main} and id={main} show the subtree")
        err, text, _ = call(port, "ui_tree", {"root": "#1999999"})
        check(err and "no element with id #1999999" in text, "ui_tree with an id never issued says there is no such element")
        err, text, _ = call(port, "ui_find", {"text": "Play"})
        check(not err and "PlayButton" in text, "ui_find")
        err, text, _ = call(port, "ui_click", {"text": "Play"}, off)
        _, c1, _ = call(port, "state_get", {"path": "/Canvas/MainScreen/Counter:Text.text"})
        check(not err and "Clicks: 1" in c1, "ui_click Play changes the counter (computer access off)")
        err, text, _ = call(port, "type_text", {"name": "NameInput", "text": "Bob"}, off)
        _, v, _ = call(port, "state_get", {"path": "/Canvas/MainScreen/NameInput:InputField.text"})
        check(not err and '"Bob"' in v, "type_text into NameInput (computer access off)")
        err, text, _ = call(port, "ui_set_value", {"name": "NameInput", "value": "Zed"})
        _, v, _ = call(port, "state_get", {"path": "/Canvas/MainScreen/NameInput:InputField.text"})
        check(not err and '"Zed"' in v, "ui_set_value")
        err, text, _ = call(port, "ui_focus", {"name": "PlayButton"})
        check(not err and "PlayButton" in text, "ui_focus")
        err, text, _ = call(port, "key", {"key": "Return"})
        _, v, _ = call(port, "state_get", {"path": "/Canvas:Game.clicks"})
        check(not err and v.startswith("/Canvas:Game.clicks = 2"), "key Return submits the focused button")
        err, text, _ = call(port, "key", {"key": "A", "method": "os"})
        check(not err and "Input.GetKey saw it" in text, "key via os events")
        for a in ("down", "up"): err, text, _ = call(port, "pointer", {"action": a, "x": 480, "y": 202})
        _, v, _ = call(port, "state_get", {"path": "/Canvas:Game.clicks"})
        check(not err and v.startswith("/Canvas:Game.clicks = 3"), "pointer down+up clicks Play")
        err, text, _ = call(port, "state_set", {"path": "/Canvas:Game.clicks", "value": 41})
        _, v, _ = call(port, "state_get", {"path": "/Canvas:Game.clicks"})
        check(not err and v.startswith("/Canvas:Game.clicks = 41"), "state_set changes Game.clicks")
        err, text, _ = call(port, "state_call", {"path": "/Canvas:Game", "method": "Play"})
        _, v, _ = call(port, "state_get", {"path": "/Canvas:Game.clicks"})
        check(not err and v.startswith("/Canvas:Game.clicks = 42"), "state_call Game.Play")
        err, q, _ = call(port, "state_query", {"path": "/Canvas/MainScreen/PlayButton:Button.onClick.m_PersistentCalls.m_Calls", "where": [{"field": "m_MethodName", "op": "==", "value": "Play"}]})
        check(not err and "Play" in q, "state_query over the button's persistent calls")
        err, text, _ = call(port, "types_find", {"pattern": "Game"})
        check(not err and "Game" in text, "types_find")
        err, text, _ = call(port, "type_members", {"type": "Game"})
        check(not err and "OpenSecond" in text, "type_members")
        err, text, content = call(port, "screenshot")
        img = [c for c in content if c.get("type") == "image"]
        data = base64.b64decode(img[0]["data"]) if img else b""
        check(not err and img and img[0]["mimeType"] == "image/jpeg" and data[:3] == b"\xff\xd8\xff" and data[-2:] == b"\xff\xd9", f"screenshot is a JPEG ({len(data)} bytes)")
        err, text, _ = call(port, "time_control", {"action": "pause"})
        err2, v, _ = call(port, "time_control", {"action": "resume"})
        check(not err and not err2 and text.startswith("timeScale=0") and v.startswith("timeScale=1"), "time_control pause and resume")
        err, text, _ = call(port, "wait_for", {"frames": 3})
        check(not err and "waited 3 frame(s)" in text, "wait_for frames")
        err, text, _ = call(port, "batch", {"calls": [{"tool": "ui_click", "args": {"name": "OpenSecondButton"}}, {"tool": "wait_for", "args": {"ui_name": "BackButton", "timeoutMs": 2000}}]})
        check(not err and "MATCHED" in text, "batch: open second screen, wait for Back")
        call(port, "state_call", {"path": "@UnityEngine.Debug", "method": "LogError", "args": ["boom from test"]})
        err, text, _ = call(port, "errors")
        check(not err and "boom from test" in text, "errors groups a logged error")
        err, text, _ = call(port, "memory_summary")
        check(not err and json.loads(text)["unity"]["used"] > 0, "memory_summary")
        t0, out = time.time(), {}
        import threading
        th = threading.Thread(target=lambda: out.update(r=req(port, "POST", "/call", {"tool": "wait_for", "args": {"ms": 20000}}, {"X-UDriver-Token": TOKEN, "X-UDriver-Call": "w1"})))
        th.start(); time.sleep(0.5)
        st, b = req(port, "POST", "/cancel", {"call": "w1"}, {"X-UDriver-Token": TOKEN})
        th.join(25)
        check(st == 200 and json.loads(b)["ok"] and time.time() - t0 < 5, f"cancel stops a running call ({time.time() - t0:.1f}s)")

        refused(port, "state_call", {"path": "@System.IO.File", "method": "WriteAllText", "args": [WORK + "/pwned", "x"]}, "System.IO.File.WriteAllText")
        refused(port, "state_call", {"path": "@System.Diagnostics.Process", "method": "Start", "args": ["/usr/bin/true"]}, "Process.Start")
        refused(port, "state_call", {"path": "@UnityEngine.Application", "method": "OpenURL", "args": ["https://example.com"]}, "Application.OpenURL")
        refused(port, "state_call", {"path": "@System.Net.WebClient", "method": "DownloadString", "args": ["https://example.com"]}, "WebClient download")
        refused(port, "state_call", {"path": "@System.Environment", "method": "GetEnvironmentVariable", "args": ["UDRIVER_TOKEN"]}, "reading environment variables")
        refused(port, "state_set", {"path": "@System.Environment.CurrentDirectory", "value": "/"}, "System.Environment write")
        check(not os.path.exists(WORK + "/pwned"), "no file was written")
        check(req(port, "POST", "/call", {"tool": "status", "args": {}}, {})[0] == 401, "no token -> 401")
        check(req(port, "POST", "/call", {"tool": "status", "args": {}}, {"X-UDriver-Token": "wrong"})[0] == 401, "wrong token -> 401")
        check(req(port, "POST", "/call", {"tool": "status", "args": {}}, {"X-UDriver-Token": TOKEN, "Origin": "https://evil.example"})[0] == 403, "Origin header -> 403")
        big = 5 * 1024 * 1024
        st = raw_status(port, f"POST /call HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nX-UDriver-Token: {TOKEN}\r\nContent-Type: application/json\r\nContent-Length: {big}\r\n\r\n", b"{" * 65536)
        check(st == 413, f"5 MB body -> {st}")
        check(req(port, "GET", "/ping")[0] == 200, "agent still answers after the attacks")
    finally:
        alive = p.poll() is None
        left = quit(p, info) if alive else ["crashed"]
        check(alive and not left, f"game alive until quit, files removed at exit (left={left})")


if __name__ == "__main__":
    if os.path.exists(f"{LOGS}/test-calls.log"): os.remove(f"{LOGS}/test-calls.log")
    tools_run()
    startup_loop()
    print(f"{len(fails)} failure(s)")
    sys.exit(1 if fails else 0)

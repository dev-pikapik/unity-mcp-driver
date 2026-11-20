#!/usr/bin/env python3
"""Scenario checks for the "understand any app" tools on the two demo apps (tests/demo-terminal, tests/mini-crm).

Runs a throwaway udriver-mcp over stdio with its own HOME, starts each demo build on a spare driver port and drives it
only through MCP tool calls. Nothing about the apps' classes or fields is hard-coded: everything is discovered from
app_map / net_log / chart_data / query output. Exit code 0 = every step passed.

  python3 tests/apps-check.py                      # both apps, builds for this OS
  python3 tests/apps-check.py --only crm --shots /tmp/shots
  python3 tests/apps-check.py --transport http     # call the in-app driver directly (no .NET on this machine)
  UDRIVER_MCP=/path/to/udriver-mcp python3 tests/apps-check.py   # use an installed or published server
"""
import argparse
import base64
import csv
import io
import json
import os
import platform
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OS = {"Darwin": "macOS", "Linux": "Linux", "Windows": "Windows"}[platform.system()]
RESULTS = []


def step(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(("PASS " if ok else "FAIL ") + name + (("  -- " + detail) if detail else ""), flush=True)
    return ok


def busy(port):
    with socket.socket() as s:
        s.settimeout(0.3)
        return s.connect_ex(("127.0.0.1", port)) == 0


def free_port(start):
    for p in range(start, start + 50):
        if not busy(p):
            return p
    raise SystemExit("no free port from %d" % start)


class ToolError(Exception):
    pass


class Mcp:
    """udriver-mcp over stdio (newline-delimited JSON-RPC)."""

    def __init__(self, home, env):
        mine = os.path.expanduser("~/.dotnet/dotnet" + (".exe" if OS == "Windows" else ""))
        dotnet = mine if os.path.exists(mine) else shutil.which("dotnet")
        if dotnet == mine:
            env = dict(env, DOTNET_ROOT=os.path.dirname(mine))
        dll = os.path.join(REPO, "UDriver.Mcp", "bin", "Debug", "net10.0", "udriver-mcp.dll")
        cmd = [os.environ["UDRIVER_MCP"]] if os.environ.get("UDRIVER_MCP") else [dotnet, dll]
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=open(os.path.join(home, "mcp.log"), "ab"), env=env, text=True, encoding="utf-8")
        self.n = 0
        self.lock = threading.Lock()
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "apps-check", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, msg):
        self.p.stdin.write(json.dumps(msg) + "\n")
        self.p.stdin.flush()

    def rpc(self, method, params):
        with self.lock:
            self.n += 1
            self.send({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params})
            while True:
                line = self.p.stdout.readline()
                if not line:
                    raise ToolError("udriver-mcp exited")
                msg = json.loads(line)
                if msg.get("id") == self.n:
                    if "error" in msg:
                        raise ToolError(json.dumps(msg["error"]))
                    return msg["result"]

    def tools(self):
        return [t["name"] for t in self.rpc("tools/list", {})["tools"]]

    def call(self, name, /, **args):
        r = self.rpc("tools/call", {"name": name, "arguments": args})
        text = "\n\n".join(c.get("text", "") for c in r.get("content", []) if c.get("type") == "text")
        images = [base64.b64decode(c["data"]) for c in r.get("content", []) if c.get("type") == "image"]
        if r.get("isError"):
            raise ToolError("%s: %s" % (name, text[:600]))
        return text, images

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(5)
        except Exception:
            self.p.kill()


class Http:
    """The in-app driver's own /call endpoint (same tools, no server in between)."""

    def __init__(self, udir, port):
        self.udir, self.port = udir, port

    def tools(self):
        return None

    def call(self, name, /, **args):
        token = open(os.path.join(self.udir, "%d.token" % self.port)).read().strip()
        req = urllib.request.Request("http://127.0.0.1:%d/call" % self.port, data=json.dumps({"tool": name, "args": args}).encode(),
                                     headers={"Content-Type": "application/json", "X-UDriver-Token": token})
        r = json.loads(urllib.request.urlopen(req, timeout=60).read())
        if r.get("ok") is False:
            raise ToolError("%s: %s" % (name, r.get("error")))
        content = r.get("content", [])
        text = "\n\n".join(c.get("text", "") for c in content if c.get("type") == "text")
        images = [base64.b64decode(c["data"]) for c in content if c.get("type") == "image"]
        if r.get("isError"):
            raise ToolError("%s: %s" % (name, text[:600]))
        return text, images

    def close(self):
        pass


class App:
    """One demo build running with its own driver port, called by game name (MCP) or port (HTTP)."""

    def __init__(self, s, folder, port, extra=()):
        root = os.path.join(HERE, folder, "Builds", OS)
        if OS == "macOS":
            macos = os.path.join(root, next(d for d in os.listdir(root) if d.endswith(".app")), "Contents", "MacOS")
            exe = os.path.join(macos, os.listdir(macos)[0])
        else:
            exe = os.path.join(root, next(f for f in os.listdir(root) if f.endswith((".x86_64", ".exe")) and not f.startswith("UnityCrash")))
        self.s, self.port = s, port
        self.p = subprocess.Popen([exe, "-udriver-port", str(port), "-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720",
                                   "-logFile", os.path.join(s.home, folder + ".log"), *extra],
                                  env=dict(os.environ, HOME=s.home, USERPROFILE=s.home), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        info = os.path.join(s.udir, "%d.json" % port)
        for _ in range(240):
            if os.path.exists(info) and busy(port):
                break
            time.sleep(0.5)
        else:
            raise SystemExit("%s did not start its driver on port %d" % (folder, port))
        self.name = json.load(open(info))["name"]
        self.http = Http(s.udir, port)

    def __call__(self, tool, /, **args):
        if self.s.mcp:
            return self.s.mcp.call(tool, target=self.name, **args)
        return self.http.call(tool, **args)

    def text(self, tool, /, **args):
        return self(tool, **args)[0]

    def shot(self, name, what):
        images = self("screenshot", maxWidth=1280)[1]
        if images:
            path = os.path.join(self.s.shots, name + ".jpg")
            open(path, "wb").write(images[0])
            print("     shot %s: %s" % (path, what))

    def stop(self):
        self.p.terminate()
        try:
            self.p.wait(10)
        except subprocess.TimeoutExpired:
            self.p.kill()


class Session:
    def __init__(self, transport, shots):
        self.home = tempfile.mkdtemp(prefix="udriver-apps-check-")
        os.makedirs(os.path.join(self.home, ".udriver"), mode=0o700)
        # A Windows game finds the profile through the shell, not USERPROFILE, so its driver files land in the real one.
        self.udir = os.path.join(os.path.expanduser("~") if OS == "Windows" else self.home, ".udriver")
        self.grants = os.path.join(self.udir, "targets.json")
        self.saved = open(self.grants, "rb").read() if OS == "Windows" and os.path.exists(self.grants) else None
        self.shots = shots
        os.makedirs(shots, exist_ok=True)
        self.transport, self.mcp, self.procs = transport, None, []

    def connect(self, apps):
        if self.transport != "mcp":
            return
        if not self.mcp:
            self.mcp = Mcp(self.home, dict(os.environ, HOME=self.home, USERPROFILE=self.home,
                                           UDRIVER_PORT=str(free_port(17931)), UDRIVER_EDITOR_PORT=str(free_port(17941))))
        self.mcp.call("targets")
        # The owner's step: a game that just showed up gets no access until it is granted in the panel.
        d = json.load(open(self.grants))
        for g in d["games"]:
            g["access"] = "full"
        d["toolsOff"] = []
        time.sleep(1.1)
        json.dump(d, open(self.grants, "w"))
        names = self.mcp.call("targets")[0]
        missing = [a.name for a in apps if a.name not in names]
        if missing:
            raise SystemExit("MCP does not see " + ", ".join(missing) + ":\n" + names)

    def close(self):
        if self.mcp:
            self.mcp.close()
        for p in self.procs:
            p.stop() if isinstance(p, App) else p.terminate()
            if isinstance(p, App) and OS == "Windows":
                for ext in (".json", ".token"):
                    try:
                        os.remove(os.path.join(self.udir, "%d%s" % (p.port, ext)))
                    except OSError:
                        pass
        if OS == "Windows" and self.mcp:
            # the server keeps its grants in the real profile too: put the owner's back
            if self.saved is not None:
                open(self.grants, "wb").write(self.saved)
            elif os.path.exists(self.grants):
                os.remove(self.grants)
        shutil.rmtree(self.home, ignore_errors=True)


def rows(text):
    """The csv block of a tool result (notes such as '0 results' come as separate blocks) as a list of dicts."""
    return list(csv.DictReader(io.StringIO(text.strip().split("\n\n")[0])))


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return None


def data_lists(app):
    """Collections the app keeps its records in: [(select path, [fields])] from app_map section=data."""
    out = []
    for m in re.finditer(r"^\s*([\w.]+)\s+\S+ x\d+\s+\{([^}]*)\}", app.text("app_map", section="data", limit=100), re.M):
        out.append((m.group(1), [f.split(":")[0].strip() for f in m.group(2).split(",")]))
    return out


def pick_list(lists, word, need=()):
    for path, fields in lists:
        if word in path.split(".")[-1].lower() and all(f in fields for f in need):
            return path
    return None


def code(app, cls, method):
    try:
        return app.text("code_read", type=cls, method=method)
    except ToolError as e:
        return "ERROR " + str(e)


def terminal(s, feed_url):
    t = App(s, "demo-terminal", free_port(17912), ["-feed", feed_url])
    s.procs.append(t)
    s.connect([t])
    time.sleep(3)

    # 1. structure: the Buy button, its handler, and the method that places the order
    ov = t.text("app_map")
    m = re.search(r'button "Buy[^"]*" -> ([\w.]+)\.(\w+)\s+#(\d+)', ov)
    step("terminal 1a app_map finds the Buy button and its handler", m, m and m.group(0))
    if not m:
        return
    view, handler, buy_id = m.group(1).split(".")[-1], m.group(2), int(m.group(3))
    detail = t.text("app_map", focus=view)
    fields = dict((name, typ) for typ, name in re.findall(r"^\s*([\w.<>\[\]]+) (\w+) =", detail, re.M))
    own = set(re.findall(r"^\s*(?:[\w.<>\[\]]+ )?(\w+)\(", detail, re.M))
    seen, todo, calls = set(), [handler], []
    if s.mcp:
        while todo and len(seen) < 6:
            meth = todo.pop(0)
            seen.add(meth)
            body = code(t, view, meth)
            calls += [(fields[f], c) for f, c in re.findall(r"\b(\w+)\.(\w+)\(", body) if f in fields]
            todo += [c for c in re.findall(r"\b(\w+)\(", body) if c in own and c not in seen and c not in todo]
    else:
        for typ in sorted(set(fields.values())):
            try:
                calls += [(typ, c) for c in re.findall(r"^\s*\S+ (\w*Order\w*)\(", t.text("app_map", focus=typ), re.M)]
            except ToolError:
                pass
    order = next(((ty, c) for ty, c in calls if "order" in c.lower()), calls[0] if calls else None)
    step("terminal 1b follows Buy to the order method (%s)" % ("code_read" if s.mcp else "app_map"), order, order and "%s.%s -> %s.%s" % (view, handler, *order))
    if not order:
        return
    otype, ometh = order[0].split(".")[-1], order[1]
    if s.mcp:
        src = code(t, otype, ometh)
        step("terminal 1c code_read shows the order method's code", ometh + "(" in src and "source:" in src,
             (re.search(r"// source: \S+", src) or re.search(r".*", src)).group(0))
    else:
        print("SKIP terminal 1c code_read (a server tool, not in the app's HTTP driver)")
    probe = t.text("trace_add", pattern="*." + otype + "." + ometh, stack=True, maxHits=5)
    pid = re.search(r"\d+", probe).group(0)
    t("ui_click", id=buy_id)
    time.sleep(1)
    hits = t.text("probe_list", id=int(pid))
    t("probe_remove", id=int(pid))
    step("terminal 1d clicking Buy calls that method (trace)", handler in hits and ometh in hits, hits.splitlines()[0] if hits else "")

    # 2. price source: record traffic, then trace one response's values into the app
    t("net_capture", action="start")
    time.sleep(4)
    log = t.text("net_log", limit=60)
    entries = re.findall(r"^#(\d+) \S+ \w+ \w+ (\S+)", log, re.M)
    last = {}
    for seq, url in entries:
        last[url.split("?")[0]] = int(seq)
    price_path, src_url = None, None
    hits = []
    for url, seq in last.items():
        key = ""
        for line in t.text("net_log", id=seq, trace=True).splitlines():
            k = re.match(r"\s*(\S+) = ", line)
            key = k.group(1) if k else key
            hit = re.search(r"->\s*(/\S*:[\w.]*price\w*)\s*$", line, re.I)
            if hit:
                hits.append(("[" in key, url, hit.group(1)))
    # A single field fed from a list item (an order book level) is a coincidence; a top-level key is the source.
    if hits:
        _, src_url, price_path = min(hits)
    t("net_capture", action="stop")
    step("terminal 2 net_capture + net_log trace: a feed response lands in the price field", price_path,
         "%s -> %s (%d addresses seen)" % (src_url, price_path, len(last)))

    # 3. charts: list them, take the close series, export it
    charts = t.text("chart_data")
    ids = re.findall(r"^\s*(#\S+)\s+(\w+)", charts, re.M)
    candles = next((i for i, kind in ids if kind == "candles"), None)
    series = [i for i, kind in ids if kind == "series"]
    closes = rows(t.text("chart_data", id=candles, format="csv")) if candles else []
    out = os.path.join(s.shots, "terminal-close.csv")
    with open(out, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["time", "close"])
        w.writerows([r["time"], r["close"]] for r in closes)
    step("terminal 3 chart_data: close series exported to CSV", len(closes) > 10 and num(closes[-1].get("close")) is not None,
         "%d charts, %d closes -> %s" % (len(ids), len(closes), out))

    # 3b. memory values via query
    def live(path):
        return num(json.loads(t.text("query", select=path, format="json"))[0]["value"])

    price = live(price_path) if price_path else None
    close = num(closes[-1]["close"]) if closes else None
    step("terminal 3b query reads the live price", price and close and abs(price - close) / close < 0.05, "price %s, last close %s" % (price, close))

    lists = data_lists(t)
    positions = pick_list(lists, "position", ("quantity",))
    orders = pick_list(lists, "order", ("status",))

    def held():
        r = rows(t.text("query", select=positions + "[*]", fields=["quantity"], format="csv", limit=1000))
        return sum(num(x["quantity"]) or 0 for x in r)

    # 4. trigger: buy when the price crosses above its moving average
    before = held() if positions else None
    try:
        t("rule_list")
        engine = True
    except ToolError:
        engine = False
    if engine:
        owner = re.search(r"([\w.]+\.\w+) -> %s\b" % otype, ov)
        rule = "buy-ma-cross"
        t("rule_add", name=rule, values={"price": price_path}, when="crosses_above(price, ma(price, 10))", every=30, max=1,
          do=[{"tool": "state_call", "args": {"path": "@" + owner.group(1), "method": ometh, "args": ["Buy", 10, 0]}}])
        t0, stats = time.time(), ""
        while time.time() - t0 < 120 and not re.search(r"fires [1-9]", stats):
            time.sleep(1)
            stats = t.text("rule_list")
        time.sleep(0.5)
        after = held() if positions else None
        t("rule_remove", name=rule)
        fired = re.search(r"when .*", stats)
        step("terminal 4 trigger: in-game rule crosses_above(price, ma(price,10)) -> %s.%s -> position grows" % (otype, ometh),
             re.search(r"fires [1-9]", stats) and after is not None and after > before,
             "%s; held %s -> %s" % (fired and fired.group(0)[:160], before, after))
    else:
        ma_id = next((i for i in series if "ma" in i.lower() or "average" in i.lower()), series[0] if series else None)

        def gap():
            p = live(price_path)
            ma = rows(t.text("chart_data", id=ma_id, last=1, format="csv"))
            v = num(list(ma[-1].values())[-1]) if ma else None
            return None if p is None or v is None else p - v

        fired, prev, t0 = None, None, time.time()
        while ma_id and positions and time.time() - t0 < 120:
            g = gap()
            if prev is not None and g is not None and (prev <= 0) != (g <= 0):
                t("ui_click", id=buy_id)
                fired = "%s after %.0fs (gap %.3f -> %.3f)" % ("up" if g > 0 else "down", time.time() - t0, prev, g)
                break
            prev = g if g is not None else prev
            time.sleep(0.5)
        time.sleep(1)
        after = held() if positions else None
        step("terminal 4 trigger: price crosses its MA -> Buy -> position grows (NO rule engine in this app: client-side loop)",
             fired and after is not None and after > before, "%s; held %s -> %s via %s" % (fired, before, after, positions))

    # 5. limit order, then cancel it
    def open_orders():
        return rows(t.text("query", select=orders + "[?.status == 'Open']", fields=["id", "limitPrice", "status"], format="csv", limit=1000)) if orders else []

    limit = round((price or 100) * 0.5, 2)
    t("form_fill", fields={"Quantity": "2", "Limit price": str(limit)}, submit="Buy")
    time.sleep(1)
    placed = [r for r in open_orders() if num(r["limitPrice"]) == limit]
    step("terminal 5a limit order placed and open", placed, "limit %s, %d open" % (limit, len(placed)))
    t.shot("terminal-limit-open", "limit order waiting in the orders list")
    ui = t.text("app_map", section="ui", limit=200)
    cancel = re.search(r'button "Cancel" ->[^\n]*?\(row \d+\)\s+#(\d+)', ui)
    if cancel:
        t("ui_click", id=int(cancel.group(1)))
        time.sleep(1)
    gone = placed and not [r for r in open_orders() if r["id"] == placed[0]["id"]]
    step("terminal 5b limit order cancelled and gone from open orders", gone, cancel and cancel.group(0))
    t("form_fill", fields={"Limit price": ""})
    t.shot("terminal-after-cancel", "terminal after the cancel: chart, positions, no open order")


def crm(s):
    c = App(s, "mini-crm", free_port(17913))
    s.procs.append(c)
    s.connect([a for a in s.procs if isinstance(a, App)])
    time.sleep(2)
    lists = data_lists(c)
    contacts = pick_list(lists, "contact", ("name", "email"))
    deals = pick_list(lists, "deal", ())
    email = "ada.testwell@checkworks.test"

    def find(path, cond, fields):
        return rows(c.text("query", select=path + "[?" + cond + "]", fields=fields, format="csv", limit=1000))

    c("ui_click", text="New contact")
    c("form_fill", fields={"Name": "Ada Testwell", "Email": email, "Phone": "+1 555 0100", "Company": "Checkworks Ltd", "Tags": "qa"}, submit="Save")
    made = find(contacts, ".email == '%s'" % email, ["name", "company"])
    step("crm 1 form_fill creates a contact", len(made) == 1, "%s in %s" % (made, contacts))
    c.shot("crm-created", "new contact Ada Testwell in the contacts list")

    def search(text):
        ov = c.text("app_map")
        box = re.search(r'field "[^"]*search[^"]*"[^\n]*#(\d+)', ov, re.I)
        if box:
            c("ui_set_value", id=int(box.group(1)), value=text)
            time.sleep(0.5)
        return re.findall(r'list "([^"]*)" \((\d+) rows\)', c.text("app_map"))

    shown = search("Testwell")
    step("crm 2 search narrows the list to the new contact", any(n == "1" for _, n in shown), str(shown))

    c("ui_click", text="Edit")
    c("form_fill", fields={"Phone": "+1 555 0199", "Company": "Checkworks Group"}, submit="Save")
    edited = find(contacts, ".email == '%s'" % email, ["phone", "company"])
    step("crm 3 edit saves new values", edited and edited[0]["company"] == "Checkworks Group", str(edited))

    c("ui_click", text="Deals")
    c("ui_click", text="New deal")
    c("form_fill", fields={"Title": "Checkworks pilot", "Contact": "Ada Testwell", "Amount (USD)": "4200", "Stage": "Lead"}, submit="Save")
    deal = find(deals, ".title == 'Checkworks pilot'", ["title", "amount", "stage"]) if deals else []
    step("crm 4 add a deal", len(deal) == 1, "%s in %s" % (deal, deals))
    c("form_fill", fields={"Stage": "Won"})
    time.sleep(0.5)
    deal = find(deals, ".title == 'Checkworks pilot'", ["stage"]) if deals else []
    step("crm 5 change the deal stage", deal and deal[0]["stage"] == "Won", str(deal))
    c.shot("crm-deal-won", "deal Checkworks pilot in stage Won")

    c("ui_click", text="Contacts")
    search("Testwell")
    c("ui_click", text="Delete")
    time.sleep(0.5)
    step("crm 6 delete the contact", not find(contacts, ".email == '%s'" % email, ["name"]), "")
    c.shot("crm-deleted", "contacts list after the delete")

    out = c.text("query", select=contacts + "[*]", fields=["name", "email"], format="csv", limit=10000).split("\n\n")[0] + "\n"
    path = os.path.join(s.shots, "crm-contacts.csv")
    open(path, "w").write(out)
    r = rows(out)
    step("crm 7 query csv with only name and email", out.splitlines()[0].strip() == "name,email" and len(r) > 0 and all("@" in x["email"] for x in r),
         "%d rows -> %s" % (len(r), path))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", choices=["terminal", "crm"])
    ap.add_argument("--shots", default=os.path.join(tempfile.gettempdir(), "udriver-apps-check"))
    ap.add_argument("--transport", choices=["mcp", "http"], default="mcp")
    a = ap.parse_args()
    s = Session(a.transport, a.shots)
    feed = None
    try:
        if a.only != "crm":
            fport = free_port(18766)
            feed = subprocess.Popen([sys.executable, os.path.join(HERE, "demo-terminal", "mock_feed.py")],
                                    env=dict(os.environ, DEMO_FEED_PORT=str(fport)), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            while not busy(fport):
                time.sleep(0.2)
            terminal(s, "http://127.0.0.1:%d" % fport)
        if a.only != "terminal":
            crm(s)
    except (ToolError, SystemExit) as e:
        step("run", False, str(e)[:600])
    finally:
        s.close()
        if feed:
            feed.terminate()
    bad = [n for n, ok in RESULTS if not ok]
    print("%s %s: %d passed, %d failed" % (OS, a.transport, len(RESULTS) - len(bad), len(bad)))
    sys.exit(1 if bad or not RESULTS else 0)


if __name__ == "__main__":
    main()

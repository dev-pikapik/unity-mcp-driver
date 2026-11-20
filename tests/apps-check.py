#!/usr/bin/env python3
"""Scenario checks for the "understand any app" tools on the two demo apps (tests/demo-terminal, tests/mini-crm).

Runs a throwaway udriver-mcp over stdio with its own HOME, starts each demo build on a spare driver port and drives it
only through MCP tool calls. Nothing about the apps' classes or fields is hard-coded: everything is discovered from
app_map / net_log / chart_data / query output. Exit code 0 = every step passed.

  python3 tests/apps-check.py                      # both apps, builds for this OS
  python3 tests/apps-check.py --only crm --shots /tmp/shots
  python3 tests/apps-check.py --transport http     # call the in-app driver directly (no .NET on this machine)
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
        dotnet = shutil.which("dotnet") or os.path.expanduser("~/.dotnet/dotnet")
        dll = os.path.join(REPO, "UDriver.Mcp", "bin", "Debug", "net10.0", "udriver-mcp.dll")
        self.p = subprocess.Popen([dotnet, dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
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

    def call(self, name, **args):
        r = self.rpc("tools/call", {"name": name, "arguments": args})
        text = "\n".join(c.get("text", "") for c in r.get("content", []) if c.get("type") == "text")
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

    def __init__(self, home, port):
        self.home, self.port = home, port

    def tools(self):
        return None

    def call(self, name, **args):
        token = open(os.path.join(self.home, ".udriver", "%d.token" % self.port)).read().strip()
        req = urllib.request.Request("http://127.0.0.1:%d/call" % self.port, data=json.dumps({"tool": name, "args": args}).encode(),
                                     headers={"Content-Type": "application/json", "X-UDriver-Token": token})
        r = json.loads(urllib.request.urlopen(req, timeout=60).read())
        content = r.get("content", [])
        text = "\n".join(c.get("text", "") for c in content if c.get("type") == "text")
        images = [base64.b64decode(c["data"]) for c in content if c.get("type") == "image"]
        if r.get("isError"):
            raise ToolError("%s: %s" % (name, text[:600]))
        return text, images

    def close(self):
        pass

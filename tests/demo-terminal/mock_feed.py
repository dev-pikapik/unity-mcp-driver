#!/usr/bin/env python3
import json
import math
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

SYMBOL = "DEMO/USD"
SEED = 20261120
STEP_SECONDS = 0.5
STEPS_PER_CANDLE = 10
HISTORY_CANDLES = 300
CANDLE_SECONDS = 5
BOOK_LEVELS = 10


def noise(i):
    x = (i * 0x9E3779B1 + SEED) & 0xFFFFFFFF
    x ^= x >> 16
    x = (x * 0x85EBCA6B) & 0xFFFFFFFF
    x ^= x >> 13
    x = (x * 0xC2B2AE35) & 0xFFFFFFFF
    x ^= x >> 16
    return x / 2147483648.0 - 1


class PriceModel:
    def __init__(self, now):
        self.origin = int(math.floor(now / CANDLE_SECONDS)) * CANDLE_SECONDS - HISTORY_CANDLES * CANDLE_SECONDS
        self.walk = []
        self.lock = threading.Lock()

    def step_at(self, now):
        return int(math.floor((now - self.origin) / STEP_SECONDS))

    def cents(self, i):
        with self.lock:
            while len(self.walk) <= i:
                prev = self.walk[-1] if self.walk else 0.0
                self.walk.append(prev * 0.995 + 0.25 * noise(len(self.walk)))
            w = self.walk[i]
        p = 248.5 + w + 2.0 * math.sin(2 * math.pi * i / 200.0)
        return int(math.floor(p * 100 + 0.5))

    def price(self, i):
        return self.cents(i) / 100.0

    def volume(self, i):
        return 1 + int(math.floor((noise(i + 1000003) + 1) * 20))

    def candle(self, k, last_step):
        first = k * STEPS_PER_CANDLE
        last = min(first + STEPS_PER_CANDLE - 1, last_step)
        prices = [self.price(i) for i in range(first, last + 1)]
        return {
            "time": self.origin + k * CANDLE_SECONDS,
            "open": prices[0],
            "high": max(prices),
            "low": min(prices),
            "close": prices[-1],
            "volume": float(sum(self.volume(i) for i in range(first, last + 1))),
        }

    def history(self, step, limit):
        current = step // STEPS_PER_CANDLE
        return [self.candle(k, step) for k in range(max(0, current - limit + 1), current + 1)]

    def tick(self, step):
        return {
            "symbol": SYMBOL,
            "time": self.origin + step * STEP_SECONDS,
            "price": self.price(step),
            "step": step,
            "candle": self.candle(step // STEPS_PER_CANDLE, step),
        }

    def book(self, step):
        c = self.cents(step)
        bids, asks = [], []
        for j in range(BOOK_LEVELS):
            bids.append({"price": (c - 1 - 5 * j) / 100.0, "size": float(5 + int(math.floor((noise(step * 31 + j + 17) + 1) * 60)))})
            asks.append({"price": (c + 1 + 5 * j) / 100.0, "size": float(5 + int(math.floor((noise(step * 31 + j + 517) + 1) * 60)))})
        return {"symbol": SYMBOL, "time": self.origin + step * STEP_SECONDS, "bids": bids, "asks": asks}


model = PriceModel(time.time())
order_seq = [1000]
order_lock = threading.Lock()


class FeedHandler(BaseHTTPRequestHandler):
    server_version = "NorthwindDemoFeed/1.0"

    def send_json(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlparse(self.path)
        step = model.step_at(time.time())
        if url.path == "/api/candles":
            try:
                limit = int(parse_qs(url.query).get("limit", ["120"])[0])
            except ValueError:
                return self.send_json(400, {"error": "limit must be a number"})
            limit = max(1, min(limit, HISTORY_CANDLES))
            self.send_json(200, {"symbol": SYMBOL, "interval": CANDLE_SECONDS, "candles": model.history(step, limit)})
        elif url.path == "/api/tick":
            self.send_json(200, model.tick(step))
        elif url.path == "/api/book":
            self.send_json(200, model.book(step))
        else:
            self.send_json(404, {"error": "not found"})

    def do_POST(self):
        if urlparse(self.path).path != "/api/orders":
            return self.send_json(404, {"error": "not found"})
        try:
            order = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
            side = order["side"]
            qty = float(order["quantity"])
            limit = float(order.get("limitPrice") or 0)
        except (ValueError, KeyError, TypeError):
            return self.send_json(400, {"error": "expected JSON with side, quantity and optional limitPrice"})
        step = model.step_at(time.time())
        last = model.price(step)
        crossed = limit <= 0 or (side == "Buy" and last <= limit) or (side == "Sell" and last >= limit)
        with order_lock:
            order_seq[0] += 1
            ref = "SIM-%d" % order_seq[0]
        self.send_json(200, {
            "id": ref,
            "clientOrderId": order.get("clientOrderId", 0),
            "status": "filled" if crossed else "accepted",
            "side": side,
            "quantity": qty,
            "limitPrice": limit,
            "fillPrice": last if crossed else 0.0,
            "time": time.time(),
        })

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    port = int(os.environ.get("DEMO_FEED_PORT", "18765"))
    server = ThreadingHTTPServer(("127.0.0.1", port), FeedHandler)
    print("demo feed for %s on http://127.0.0.1:%d" % (SYMBOL, port), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass

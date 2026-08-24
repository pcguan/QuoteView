#!/usr/bin/env python3
"""QuoteView snapshot server.

Runs on the NAS behind nginx (/quoteview/api/ -> 127.0.0.1:8388). Three jobs:

1. Hand each client a persistent id (POST /register).
2. Accept each client's groups+contracts every 5 minutes (POST /sync) and keep
   the latest copy per client on disk.
3. After the SH/SZ close, fetch the day's intraday trend for the UNION of every
   client's SH/SZ contracts — sequential, throttled — and persist one JSON per
   contract per day. Clients query it back (GET /dates, GET /trend).

Stored trend files use exactly the C# client's TrendSeries JSON shape
(Code/Name/PreClose/Points[{Time,Price,AvgPrice,Volume}]), so the client
deserializes them with the same code it uses for its own local cache.

Stdlib only — the container just needs python3.
"""

import json
import os
import re
import threading
import time
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

DATA = os.environ.get("QV_DATA", "/data")
LOG_FILE = os.environ.get("QV_LOG", "")
PORT = int(os.environ.get("QV_PORT", "8388"))
RETAIN_DAYS = int(os.environ.get("QV_RETAIN_DAYS", "7"))
FETCH_GAP_S = float(os.environ.get("QV_FETCH_GAP", "1.5"))
# Clients silent for this long stop contributing to the union.
CLIENT_TTL_DAYS = 14

CN = timezone(timedelta(hours=8))  # no DST in China
CODE_RE = re.compile(r"^(SH|SZ)\d{6}$")
ID_RE = re.compile(r"^[0-9a-f]{32}$")

CLIENTS = os.path.join(DATA, "clients")
TRENDS = os.path.join(DATA, "trends")
STATE = os.path.join(DATA, "state.json")

_lock = threading.Lock()


def log(msg):
    line = f"{datetime.now(CN):%F %T} {msg}"
    print(line, flush=True)
    if not LOG_FILE:
        return
    try:
        # Size-capped by simple rotation: server.log -> server.log.1 at 10MB.
        if os.path.exists(LOG_FILE) and os.path.getsize(LOG_FILE) > 10 * 1024 * 1024:
            os.replace(LOG_FILE, LOG_FILE + ".1")
        with open(LOG_FILE, "a") as f:
            f.write(line + "\n")
    except OSError:
        pass


# ---------------------------------------------------------------- storage

def load_state():
    try:
        with open(STATE) as f:
            return json.load(f)
    except Exception:
        return {}


def save_state(state):
    tmp = STATE + ".tmp"
    with open(tmp, "w") as f:
        json.dump(state, f, ensure_ascii=False)
    os.replace(tmp, STATE)


def trend_dir(code):
    return os.path.join(TRENDS, code)


def trend_path(code, day):
    return os.path.join(trend_dir(code), f"{day}.json")


def trend_dates(code):
    d = trend_dir(code)
    if not os.path.isdir(d):
        return []
    out = []
    for name in os.listdir(d):
        if name.endswith(".json") and re.match(r"^\d{4}-\d{2}-\d{2}\.json$", name):
            out.append(name[:-5])
    return sorted(out, reverse=True)


def prune(code):
    for day in trend_dates(code)[RETAIN_DAYS:]:
        try:
            os.remove(trend_path(code, day))
        except OSError:
            pass


def union_codes():
    """SH/SZ codes across every client synced within the TTL, deduped."""
    cutoff = time.time() - CLIENT_TTL_DAYS * 86400
    seen = set()
    if not os.path.isdir(CLIENTS):
        return []
    for name in os.listdir(CLIENTS):
        path = os.path.join(CLIENTS, name)
        if not name.endswith(".json") or os.path.getmtime(path) < cutoff:
            continue
        try:
            with open(path) as f:
                doc = json.load(f)
        except Exception:
            continue
        for group in doc.get("groups", []):
            for code in group.get("codes", []):
                code = str(code).upper()
                if CODE_RE.match(code):
                    seen.add(code)
    return sorted(seen)


# ---------------------------------------------------------------- fetching

def fetch_trend(code):
    """One contract's day series from EastMoney, in the client's JSON shape."""
    market = "1" if code.startswith("SH") else "0"
    secid = f"{market}.{code[2:]}"
    url = ("https://push2his.eastmoney.com/api/qt/stock/trends2/get"
           "?fields1=f1,f2,f3,f4,f5,f6,f7,f8"
           "&fields2=f51,f52,f53,f54,f55,f56,f57,f58"
           "&ut=fa5fd1943c7b386f172d6893dbfba10b&iscr=0&ndays=1"
           f"&secid={secid}")
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer/1.0)",
        "Referer": "https://quote.eastmoney.com/",
    })
    last = None
    for _ in range(3):
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                doc = json.load(r)
            data = doc.get("data") or {}
            points = []
            for row in data.get("trends") or []:
                c = row.split(",")
                if len(c) < 8:
                    continue
                # Same column map as the C# client: time, price=c[2],
                # volume=c[5], average=c[7].
                points.append({
                    "Time": c[0],
                    "Price": float(c[2]),
                    "AvgPrice": float(c[7]),
                    "Volume": float(c[5]),
                })
            if not points:
                return None
            return {
                "Code": code,
                "Name": data.get("name") or code,
                "PreClose": float(data.get("preClose") or 0),
                "Points": points,
            }
        except Exception as e:  # noqa: BLE001 - retry then give up
            last = e
            time.sleep(2)
    log(f"fetch {code} failed: {last}")
    return None


def sweep_once():
    """One throttled pass over whatever is missing for today. Returns idle time hint."""
    now = datetime.now(CN)
    # After close + settle margin only; well-defined because SH/SZ share one bell.
    if now.weekday() >= 5 or now.time() < datetime.strptime("15:20", "%H:%M").time():
        return
    day = f"{now:%F}"

    state = load_state()
    if state.get("holiday") == day:
        return

    missing = [c for c in union_codes() if not os.path.exists(trend_path(c, day))]
    if not missing:
        return

    log(f"sweep {day}: {len(missing)} contracts to fetch")
    done = failed = 0
    for code in missing:
        series = fetch_trend(code)
        if series is None:
            failed += 1
        elif not series["Points"][-1]["Time"].startswith(day):
            # A weekday whose data belongs to an older session: holiday. One
            # probe settles it for the whole list.
            state["holiday"] = day
            save_state(state)
            log(f"sweep {day}: stale data returned -> holiday, aborting")
            return
        else:
            os.makedirs(trend_dir(code), exist_ok=True)
            tmp = trend_path(code, day) + ".tmp"
            with open(tmp, "w") as f:
                json.dump(series, f, ensure_ascii=False)
            os.replace(tmp, trend_path(code, day))
            prune(code)
            done += 1
        time.sleep(FETCH_GAP_S)

    state["last_sweep"] = {"day": day, "done": done, "failed": failed,
                           "at": f"{datetime.now(CN):%F %T}"}
    save_state(state)
    log(f"sweep {day}: done={done} failed={failed}")


def scheduler():
    while True:
        try:
            sweep_once()
        except Exception as e:  # noqa: BLE001 - the loop must survive anything
            log(f"sweep error: {e}")
        time.sleep(300)


# ---------------------------------------------------------------- http

class Handler(BaseHTTPRequestHandler):
    server_version = "QuoteViewServer/1.0"

    def _json(self, obj, status=200):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _bad(self, msg, status=400):
        self._json({"error": msg}, status)

    def log_message(self, fmt, *args):  # default logger spams stderr per request
        pass

    def do_GET(self):
        url = urlparse(self.path)
        q = parse_qs(url.query)

        if url.path == "/dates":
            code = (q.get("code") or [""])[0].upper()
            if not CODE_RE.match(code):
                return self._bad("bad code")
            return self._json({"dates": trend_dates(code)})

        if url.path == "/trend":
            code = (q.get("code") or [""])[0].upper()
            day = (q.get("date") or [""])[0]
            if not CODE_RE.match(code) or not re.match(r"^\d{4}-\d{2}-\d{2}$", day):
                return self._bad("bad code/date")
            path = trend_path(code, day)
            if not os.path.exists(path):
                return self._bad("not found", 404)
            with open(path, "rb") as f:
                body = f.read()
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if url.path == "/status":
            state = load_state()
            clients = len([n for n in os.listdir(CLIENTS)]) if os.path.isdir(CLIENTS) else 0
            return self._json({
                "clients": clients,
                "union": len(union_codes()),
                "last_sweep": state.get("last_sweep"),
                "holiday": state.get("holiday"),
            })

        self._bad("not found", 404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length > 512 * 1024:
            return self._bad("payload too large", 413)
        raw = self.rfile.read(length) if length else b""

        if self.path == "/register":
            cid = uuid.uuid4().hex
            os.makedirs(CLIENTS, exist_ok=True)
            with open(os.path.join(CLIENTS, cid + ".json"), "w") as f:
                json.dump({"groups": [], "registered": f"{datetime.now(CN):%F %T}"}, f)
            log(f"register {cid}")
            return self._json({"id": cid})

        if self.path == "/sync":
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            cid = str(doc.get("id") or "")
            if not ID_RE.match(cid):
                return self._bad("bad id")
            groups = doc.get("groups")
            if not isinstance(groups, list) or len(groups) > 200:
                return self._bad("bad groups")
            clean = []
            for g in groups[:200]:
                codes = [str(c).upper() for c in (g.get("codes") or [])[:2000]]
                clean.append({"name": str(g.get("name") or "")[:64], "codes": codes})
            os.makedirs(CLIENTS, exist_ok=True)
            path = os.path.join(CLIENTS, cid + ".json")
            with _lock:
                doc_out = {"groups": clean, "synced": f"{datetime.now(CN):%F %T}"}
                tmp = path + ".tmp"
                with open(tmp, "w") as f:
                    json.dump(doc_out, f, ensure_ascii=False)
                os.replace(tmp, path)
            total = sum(len(g["codes"]) for g in clean)
            return self._json({"ok": True, "groups": len(clean), "contracts": total})

        self._bad("not found", 404)


def main():
    os.makedirs(DATA, exist_ok=True)
    os.makedirs(CLIENTS, exist_ok=True)
    os.makedirs(TRENDS, exist_ok=True)

    threading.Thread(target=scheduler, daemon=True).start()

    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    log(f"listening on 127.0.0.1:{PORT}, data={DATA}, retain={RETAIN_DAYS}d")
    server.serve_forever()


if __name__ == "__main__":
    main()

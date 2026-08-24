#!/usr/bin/env python3
"""QuoteView snapshot server.

Runs on the NAS behind nginx (/quoteview/api/ -> 127.0.0.1:8388). Three jobs:

1. Account registration and login (POST /register, POST /login): interaction
   is account-level, not per-installation — several machines logging into the
   same account share one set of groups. Passwords are PBKDF2-hashed; logins
   mint bearer tokens that survive server restarts.
2. Accept the account's groups+contracts every 5 minutes (POST /sync, Bearer
   auth) and keep the latest copy per account on disk.
3. After the SH/SZ close, fetch the day's intraday trend for the UNION of every
   account's SH/SZ contracts — sequential, throttled, dual-source — and persist
   one JSON per contract per day. Clients query it back (GET /dates, GET /trend,
   Bearer auth).

Stored trend files use exactly the C# client's TrendSeries JSON shape
(Code/Name/PreClose/Points[{Time,Price,AvgPrice,Volume}]), so the client
deserializes them with the same code it uses for its own local cache.

Stdlib only — the container just needs python3.
"""

import hashlib
import json
import os
import re
import secrets
import threading
import time
import urllib.request
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
USER_RE = re.compile(r"^[A-Za-z0-9_]{3,32}$")
TOKEN_RE = re.compile(r"^[0-9a-f]{64}$")

ACCOUNTS = os.path.join(DATA, "accounts")
TRENDS = os.path.join(DATA, "trends")
STATE = os.path.join(DATA, "state.json")

MAX_ACCOUNTS = 10          # personal server; also caps drive-by registrations
MAX_TOKENS = 10            # devices per account; oldest token drops off
PBKDF2_ITERS = 100_000

_lock = threading.Lock()
_token_cache = {}          # token -> username, rebuilt on miss


def hash_pw(password, salt):
    return hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt),
                               PBKDF2_ITERS).hex()


def account_path(user):
    return os.path.join(ACCOUNTS, user + ".json")


def load_account(user):
    try:
        with open(account_path(user)) as f:
            return json.load(f)
    except Exception:
        return None


def save_account(user, doc):
    os.makedirs(ACCOUNTS, exist_ok=True)
    path = account_path(user)
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(doc, f, ensure_ascii=False)
    os.replace(tmp, path)


def user_for_token(token):
    """Bearer token -> username, or None. Cached; a miss rescans the accounts
    directory once (a handful of files) before giving up."""
    if not TOKEN_RE.match(token or ""):
        return None
    user = _token_cache.get(token)
    if user:
        doc = load_account(user)
        if doc and token in doc.get("tokens", []):
            return user
        _token_cache.pop(token, None)
    if not os.path.isdir(ACCOUNTS):
        return None
    for name in os.listdir(ACCOUNTS):
        if not name.endswith(".json"):
            continue
        candidate = name[:-5]
        doc = load_account(candidate)
        if doc and token in doc.get("tokens", []):
            _token_cache[token] = candidate
            return candidate
    return None


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
    """SH/SZ codes across every account synced within the TTL, deduped."""
    cutoff = time.time() - CLIENT_TTL_DAYS * 86400
    seen = set()
    if not os.path.isdir(ACCOUNTS):
        return []
    for name in os.listdir(ACCOUNTS):
        path = os.path.join(ACCOUNTS, name)
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

def fetch_eastmoney(code):
    """(series, data_day) from EastMoney trends2, or (None, None)."""
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
    for _ in range(2):
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
                return None, None
            series = {
                "Code": code,
                "Name": data.get("name") or code,
                "PreClose": float(data.get("preClose") or 0),
                "Points": points,
            }
            return series, points[-1]["Time"][:10]
        except Exception:  # noqa: BLE001
            time.sleep(2)
    return None, None


def fetch_tencent(code):
    """(series, data_day) from Tencent minute/query — the fallback for when
    EastMoney throttles the trends2 path with connection resets (it does, in
    bursts; the desktop client grew this same second source for that reason).

    Row shape is `HHmm price cumulativeVolume [cumulativeAmount]`: volume is
    differenced into per-minute bars, the average line is cumulative
    amount / (cumulative volume × 100) — A-share volumes are in 手 — and the
    times get the response's own date prepended so stored files look exactly
    like the EastMoney ones.
    """
    api = code.lower()
    url = f"https://web.ifzq.gtimg.cn/appstock/app/minute/query?code={api}"
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer/1.0)",
    })
    for _ in range(2):
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                doc = json.load(r)
            node = (doc.get("data") or {}).get(api) or {}
            inner = node.get("data") or {}
            raw_day = str(inner.get("date") or "")
            day = f"{raw_day[:4]}-{raw_day[4:6]}-{raw_day[6:8]}" if len(raw_day) == 8 else None

            qt = (node.get("qt") or {}).get(api) or []
            name = qt[1] if len(qt) > 1 else code
            pre_close = float(qt[4]) if len(qt) > 4 else 0.0

            points = []
            prev_cum = 0.0
            for row in inner.get("data") or []:
                c = str(row).split()
                if len(c) < 3:
                    continue
                price = float(c[1])
                if price <= 0:
                    continue
                cum = float(c[2])
                amount = float(c[3]) if len(c) > 3 else 0.0
                t = f"{c[0][:2]}:{c[0][2:]}" if len(c[0]) == 4 else c[0]
                points.append({
                    "Time": f"{day} {t}" if day else t,
                    "Price": price,
                    "AvgPrice": amount / (cum * 100) if amount > 0 and cum > 0 else 0.0,
                    "Volume": max(0.0, cum - prev_cum),
                })
                prev_cum = cum
            if not points:
                return None, None
            return {
                "Code": code,
                "Name": name,
                "PreClose": pre_close,
                "Points": points,
            }, day
        except Exception:  # noqa: BLE001
            time.sleep(2)
    return None, None


def fetch_trend(code):
    """Dual-source: (series, data_day) or (None, None)."""
    series, day = fetch_eastmoney(code)
    if series is None:
        series, day = fetch_tencent(code)
    return series, day


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
    done = failed = streak = 0
    for code in missing:
        series, data_day = fetch_trend(code)
        if series is None:
            failed += 1
            streak += 1
            # Both sources down N times in a row = we're being throttled.
            # Continuing just feeds the throttle; the next 5-minute tick
            # resumes from wherever this stopped (file existence = done).
            if streak >= 5:
                log(f"sweep {day}: {streak} consecutive failures, backing off "
                    f"(done={done} failed={failed})")
                return
        elif data_day != day:
            # A weekday whose data belongs to an older session: holiday. One
            # probe settles it for the whole list.
            state["holiday"] = day
            save_state(state)
            log(f"sweep {day}: stale data ({data_day}) -> holiday, aborting")
            return
        else:
            streak = 0
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

    def _auth(self):
        """The account behind the Bearer token, or None (401 already sent)."""
        header = self.headers.get("Authorization") or ""
        token = header[7:] if header.startswith("Bearer ") else ""
        user = user_for_token(token)
        if user is None:
            self._bad("unauthorized", 401)
        return user

    def do_GET(self):
        url = urlparse(self.path)
        q = parse_qs(url.query)

        if url.path == "/dates":
            if self._auth() is None:
                return
            code = (q.get("code") or [""])[0].upper()
            if not CODE_RE.match(code):
                return self._bad("bad code")
            return self._json({"dates": trend_dates(code)})

        if url.path == "/trend":
            if self._auth() is None:
                return
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
            accounts = (len([n for n in os.listdir(ACCOUNTS) if n.endswith(".json")])
                        if os.path.isdir(ACCOUNTS) else 0)
            return self._json({
                "accounts": accounts,
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
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            user = str(doc.get("username") or "")
            password = str(doc.get("password") or "")
            if not USER_RE.match(user):
                return self._bad("用户名需为 3~32 位字母/数字/下划线")
            if len(password) < 6:
                return self._bad("密码至少 6 位")
            with _lock:
                if os.path.exists(account_path(user)):
                    return self._bad("用户名已存在", 409)
                existing = (len([n for n in os.listdir(ACCOUNTS) if n.endswith(".json")])
                            if os.path.isdir(ACCOUNTS) else 0)
                if existing >= MAX_ACCOUNTS:
                    return self._bad("注册已关闭", 403)
                salt = secrets.token_hex(16)
                token = secrets.token_hex(32)
                save_account(user, {
                    "auth": {"salt": salt, "hash": hash_pw(password, salt),
                             "iters": PBKDF2_ITERS},
                    "tokens": [token],
                    "groups": [],
                    "created": f"{datetime.now(CN):%F %T}",
                })
            _token_cache[token] = user
            log(f"register account {user}")
            return self._json({"token": token, "username": user})

        if self.path == "/login":
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            user = str(doc.get("username") or "")
            password = str(doc.get("password") or "")
            account = load_account(user) if USER_RE.match(user) else None
            if account is None:
                return self._bad("用户名或密码错误", 401)
            auth = account.get("auth") or {}
            expect = auth.get("hash") or ""
            got = hashlib.pbkdf2_hmac("sha256", password.encode(),
                                      bytes.fromhex(auth.get("salt") or "00"),
                                      int(auth.get("iters") or PBKDF2_ITERS)).hex()
            if not secrets.compare_digest(expect, got):
                return self._bad("用户名或密码错误", 401)
            token = secrets.token_hex(32)
            with _lock:
                account = load_account(user) or account
                tokens = (account.get("tokens") or [])[-(MAX_TOKENS - 1):]
                tokens.append(token)
                account["tokens"] = tokens
                save_account(user, account)
            _token_cache[token] = user
            log(f"login {user}")
            return self._json({"token": token, "username": user})

        if self.path == "/sync":
            user = self._auth()
            if user is None:
                return
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            groups = doc.get("groups")
            if not isinstance(groups, list) or len(groups) > 200:
                return self._bad("bad groups")
            clean = []
            for g in groups[:200]:
                codes = [str(c).upper() for c in (g.get("codes") or [])[:2000]]
                clean.append({"name": str(g.get("name") or "")[:64], "codes": codes})
            with _lock:
                account = load_account(user)
                if account is None:
                    return self._bad("unauthorized", 401)
                account["groups"] = clean
                account["synced"] = f"{datetime.now(CN):%F %T}"
                save_account(user, account)
            total = sum(len(g["codes"]) for g in clean)
            return self._json({"ok": True, "groups": len(clean), "contracts": total})

        self._bad("not found", 404)


def main():
    os.makedirs(DATA, exist_ok=True)
    os.makedirs(ACCOUNTS, exist_ok=True)
    os.makedirs(TRENDS, exist_ok=True)

    threading.Thread(target=scheduler, daemon=True).start()

    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    log(f"listening on 127.0.0.1:{PORT}, data={DATA}, retain={RETAIN_DAYS}d")
    server.serve_forever()


if __name__ == "__main__":
    main()

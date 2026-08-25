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

# Accounts invisible to the web console (test/probe accounts). They work
# normally over the API; they just never appear in accounts/sessions/logs.
HIDDEN_ACCOUNTS = set(filter(None, os.environ.get("QV_HIDDEN_ACCOUNTS", "qa_probe").split(",")))
MAX_TOKENS = 10            # devices per account; oldest token drops off
PBKDF2_ITERS = 100_000

_lock = threading.Lock()
_token_cache = {}          # token -> username, rebuilt on miss


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
    """Bearer token -> (username, account_doc), or (None, None). Tokens are
    dicts carrying created/ip/version/last-seen; legacy plain strings from the
    first deployment are upgraded on read."""
    if not TOKEN_RE.match(token or ""):
        return None, None
    user = _token_cache.get(token)
    candidates = [user] if user else []
    if not candidates and os.path.isdir(ACCOUNTS):
        candidates = [n[:-5] for n in os.listdir(ACCOUNTS) if n.endswith(".json")]
    for candidate in candidates:
        doc = load_account(candidate)
        if doc is None:
            continue
        normalize_tokens(doc)
        if any(t["t"] == token for t in doc["tokens"]):
            _token_cache[token] = candidate
            return candidate, doc
    _token_cache.pop(token, None)
    return None, None


def normalize_tokens(doc):
    """Upgrades legacy plain-string tokens to the dict shape in place."""
    tokens = doc.get("tokens") or []
    doc["tokens"] = [
        t if isinstance(t, dict) else {"t": t, "created": "", "ip": "", "ver": "", "seen": ""}
        for t in tokens
    ]


def touch_token(user, token, ip, ver):
    """Records activity on a token: last-seen, ip and client version.

    Reloads inside the lock on purpose: the doc the auth check read is a
    snapshot from before the lock, and saving that snapshot would silently
    undo any write (a settings PUT, a sync) that landed in between — the
    classic lost update, and exactly how the first /settings write vanished."""
    stamp = f"{datetime.now(CN):%F %T}"
    with _lock:
        doc = load_account(user)
        if doc is None:
            return
        normalize_tokens(doc)
        for t in doc["tokens"]:
            if t["t"] == token:
                t["seen"] = stamp
                if ip:
                    t["ip"] = ip
                if ver:
                    t["ver"] = ver
                break
        save_account(user, doc)


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


KLINES = os.path.join(DATA, "klines")
KLINE_TTL_S = 300
_kline_lock = threading.Lock()


def kline_body(secid, klt, fqt, lmt):
    """The upstream EastMoney kline response, verbatim, cached for a few
    minutes: the client keeps its own settled-day cache, so what lands here is
    mostly first-opens — the TTL just keeps N clients opening the same chart
    from turning into N upstream hits. Serves stale on upstream failure."""
    safe = secid.replace(".", "_")
    path = os.path.join(KLINES, safe, f"{klt}-{fqt}-{lmt}.json")

    meta = None
    try:
        with open(path) as f:
            meta = json.load(f)
    except Exception:
        pass
    if meta and time.time() - meta.get("at", 0) < KLINE_TTL_S:
        return meta["body"]

    url = ("https://push2his.eastmoney.com/api/qt/stock/kline/get"
           "?fields1=f1,f2,f3,f4,f5,f6"
           "&fields2=f51,f52,f53,f54,f55,f56,f57"
           f"&klt={klt}&fqt={fqt}&secid={secid}&end=20500101&lmt={lmt}")
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer/1.0)",
        "Referer": "https://quote.eastmoney.com/",
    })
    for _ in range(3):
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                body = r.read().decode("utf-8")
            doc = json.loads(body)
            # An empty answer ({"data": null}) is EastMoney throttling politely.
            # Caching it would poison this key until a good fetch happens to
            # replace it — treat it as a failure like any other.
            if not ((doc.get("data") or {}).get("klines") or []):
                raise ValueError("empty klines")
            with _kline_lock:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                tmp = path + ".tmp"
                with open(tmp, "w") as f:
                    json.dump({"at": time.time(), "body": body}, f)
                os.replace(tmp, path)
            return body
        except Exception:
            time.sleep(1.5)

    return meta["body"] if meta else None


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

    all_codes = union_codes()
    missing = [c for c in all_codes if not os.path.exists(trend_path(c, day))]
    if not missing:
        enrich_summaries(day, all_codes)
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
    enrich_summaries(day, all_codes)


def enrich_summaries(day, codes):
    """Attaches the day's closing metrics (change %, turnover, volume, the
    outer/inner split) to every snapshot of `day` still missing them — ONE
    batched Tencent quote request for all of them. Field map matches the C#
    client: [32] pct, [6] volume(手), [37] amount(万元, A-share), [7]/[8]
    outer/inner. Runs after the close, so the quote IS the day's final print."""
    missing = [c for c in codes
               if os.path.exists(trend_path(c, day))]
    todo = []
    for c in missing:
        try:
            with open(trend_path(c, day)) as f:
                if "\"Summary\"" not in f.read(200000):
                    todo.append(c)
        except OSError:
            pass
    if not todo:
        return

    got = {}
    for i in range(0, len(todo), 400):
        chunk = todo[i:i + 400]
        url = "https://qt.gtimg.cn/q=" + ",".join(c.lower() for c in chunk)
        req = urllib.request.Request(url, headers={
            "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer/1.0)",
            "Referer": "https://gu.qq.com/",
        })
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                body = r.read().decode("gbk", errors="replace")
        except Exception as e:  # noqa: BLE001
            log(f"enrich {day}: quote batch failed: {e}")
            continue
        for seg in body.split(";"):
            seg = seg.strip()
            if not seg.startswith("v_"):
                continue
            api, _, val = seg[2:].partition("=")
            f = val.strip().strip('"').split("~")
            if len(f) < 40:
                continue
            def num(i):
                try:
                    return float(f[i])
                except (ValueError, IndexError):
                    return 0.0
            got[api.upper()] = {
                "Percent": num(32),
                "Volume": num(6),
                "Amount": num(37) * 1e4,
                "Outer": num(7),
                "Inner": num(8),
            }
        time.sleep(1.0)

    done = 0
    for c in todo:
        summary = got.get(c)
        if summary is None:
            continue
        path = trend_path(c, day)
        try:
            with open(path) as f:
                doc = json.load(f)
            doc["Summary"] = summary
            tmp = path + ".tmp"
            with open(tmp, "w") as f:
                json.dump(doc, f, ensure_ascii=False)
            os.replace(tmp, path)
            done += 1
        except Exception:  # noqa: BLE001
            pass
    log(f"enrich {day}: summaries added to {done}/{len(todo)} snapshots")


def scheduler():
    while True:
        try:
            sweep_once()
        except Exception as e:  # noqa: BLE001 - the loop must survive anything
            log(f"sweep error: {e}")
        time.sleep(300)


# ---------------------------------------------------------------- http

WEB_SESSION_IDLE_H = 12


def web_sessions():
    return load_state().get("websessions") or {}


def web_session_check(token):
    """(user, role) for a live web-admin session, refreshing last-seen; None
    when missing/expired. Sessions live in state.json so a container restart
    doesn't log every admin out."""
    if not TOKEN_RE.match(token or ""):
        return None
    now = datetime.now(CN)
    with _lock:
        state = load_state()
        sessions = state.get("websessions") or {}
        entry = sessions.get(token)
        if not entry:
            return None
        try:
            seen = datetime.strptime(entry["seen"], "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
        except Exception:
            seen = now
        if now - seen > timedelta(hours=WEB_SESSION_IDLE_H):
            del sessions[token]
            state["websessions"] = sessions
            save_state(state)
            return None
        entry["seen"] = f"{now:%F %T}"
        state["websessions"] = sessions
        save_state(state)
        return entry["user"], entry["role"]


def web_session_create(user, role, ip):
    token = secrets.token_hex(32)
    with _lock:
        state = load_state()
        sessions = state.get("websessions") or {}
        # Drop stale entries while we're here; cap total.
        now = datetime.now(CN)
        for t in list(sessions):
            try:
                seen = datetime.strptime(sessions[t]["seen"], "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                if now - seen > timedelta(hours=WEB_SESSION_IDLE_H):
                    del sessions[t]
            except Exception:
                del sessions[t]
        sessions[token] = {"user": user, "role": role, "ip": ip,
                           "at": f"{now:%F %T}", "seen": f"{now:%F %T}"}
        state["websessions"] = dict(list(sessions.items())[-20:])
        save_state(state)
    return token


def web_session_drop(token):
    with _lock:
        state = load_state()
        sessions = state.get("websessions") or {}
        if token in sessions:
            del sessions[token]
            state["websessions"] = sessions
            save_state(state)


def verify_password(account, password):
    auth = account.get("auth") or {}
    got = hashlib.pbkdf2_hmac("sha256", password.encode(),
                              bytes.fromhex(auth.get("salt") or "00"),
                              int(auth.get("iters") or PBKDF2_ITERS)).hex()
    return secrets.compare_digest(auth.get("hash") or "", got)


def role_of(account):
    return account.get("role") or "user"

ADMIN_PAGE = """<!doctype html><html lang=zh><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1">
<title>QuoteView 管理台</title>
<style>
:root{--bg:#0B0F17;--card:#12161F;--line:#232B3B;--head:#1A2030;--fg:#EDF1F7;
--mut:#8B93A3;--dim:#5F6672;--up:#3DD68C;--warn:#FFC107;--bad:#EF5350;--acc:#4C8DFF}
*{box-sizing:border-box}
body{background:var(--bg);color:var(--fg);font:13px/1.6 'Microsoft YaHei',sans-serif;margin:0}
header{display:flex;align-items:center;gap:24px;padding:14px 28px;background:var(--card);
border-bottom:1px solid var(--line)}
header h1{font-size:16px;margin:0}
nav{display:flex;gap:4px}
nav button{background:none;border:none;color:var(--mut);padding:8px 16px;font-size:13px;
cursor:pointer;border-radius:5px}
nav button.act{background:var(--head);color:var(--fg)}
#who{margin-left:auto;color:var(--dim);font-size:12px}
main{padding:20px 28px;max-width:1280px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;
padding:16px 18px;margin-bottom:18px}
.card h2{font-size:13px;color:var(--mut);margin:0 0 12px;font-weight:600}
table{border-collapse:collapse;width:100%;font-size:12px}
th{color:var(--dim);text-align:left;font-weight:normal;padding:5px 12px;
border-bottom:1px solid var(--line);white-space:nowrap}
td{padding:6px 12px;border-bottom:1px solid #1A2030;vertical-align:middle}
tr:hover td{background:#161B27}
.tag{display:inline-block;padding:0 8px;border-radius:9px;font-size:11px;line-height:18px}
.t-on{background:#12351F;color:var(--up)}.t-off{background:#252B38;color:var(--mut)}
.t-bad{background:#3A1520;color:var(--bad)}.t-role{background:#152743;color:var(--acc)}
button.op{background:var(--head);color:var(--fg);border:1px solid #39435A;border-radius:4px;
padding:3px 10px;margin:1px 2px;cursor:pointer;font-size:12px}
button.op:hover{border-color:#5C6B8A}
button.danger{color:var(--bad)}
input,select{background:#0F1420;color:var(--fg);border:1px solid #39435A;border-radius:4px;
padding:4px 9px;font-size:12px}
#msg{position:fixed;right:24px;bottom:20px;background:var(--head);border:1px solid #39435A;
border-radius:6px;padding:8px 16px;color:var(--warn);display:none;max-width:420px}
.mono{font-family:Consolas,monospace}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:18px}
@media(max-width:900px){.grid2{grid-template-columns:1fr}}
.dim{color:var(--dim)}.mut{color:var(--mut)}
button.icon{background:var(--head);border:1px solid #39435A;border-radius:4px;width:26px;
height:26px;padding:0;cursor:pointer;vertical-align:middle;color:var(--mut)}
button.icon:hover{border-color:#5C6B8A;color:var(--fg)}
button.icon svg{width:14px;height:14px;display:block;margin:auto}
button.icon.spin svg{animation:spin .8s linear infinite}
@keyframes spin{to{transform:rotate(360deg)}}
.lv{display:inline-block;padding:0 7px;border-radius:8px;font-size:11px;line-height:17px}
.lv-info{background:#152743;color:var(--acc)}
.lv-warn{background:#3A2F10;color:var(--warn)}
.lv-error{background:#3A1520;color:var(--bad)}
tr.err td{background:#2A1218}
tr.err:hover td{background:#331722}
</style>
<section id=login-view style="display:none;max-width:340px;margin:120px auto">
  <div class=card>
    <h2 style="font-size:15px;color:var(--fg)">QuoteView 管理台</h2>
    <div style="margin:12px 0 6px" class=mut>管理员账户</div>
    <input id=lu style="width:100%" autocomplete=username>
    <div style="margin:12px 0 6px" class=mut>密码</div>
    <input id=lp type=password style="width:100%" autocomplete=current-password
           onkeydown="if(event.key==='Enter')doLogin()">
    <div id=lerr style="color:var(--bad);min-height:1.5em;margin-top:8px"></div>
    <button class=op style="width:100%;padding:7px 0;margin-top:4px" onclick="doLogin()">登 录</button>
    <div class=dim style="margin-top:10px;font-size:11px">仅管理员角色可登录；与客户端同一套账户密码。</div>
  </div>
</section>
<div id=console-view style="display:none">
<header>
  <h1>QuoteView 管理台</h1>
  <nav>
    <button id=nav-acct class=act onclick="show('acct')">账户管理</button>
    <button id=nav-sess onclick="show('sess')">会话管理</button>
    <button id=nav-logs onclick="show('logs')">日志管理</button>
  </nav>
  <span id=who></span>
  <button class=op onclick="doLogout()" style="margin-left:12px">登出</button>
</header>
<main>
  <section id=tab-acct>
    <div class=card>
      <h2>新增账户</h2>
      <input id=nu placeholder="用户名（3~32 位字母/数字/下划线）" style="width:240px">
      <input id=np placeholder="初始密码（≥6 位）" type=password style="width:200px">
      <button class=op onclick="createAccount()">创建</button>
      <span class=dim>　新账户默认为普通用户；角色由系统管理员在列表中调整</span>
    </div>
    <div class=card><h2>账户列表
        <button id=ra class=icon title="刷新" onclick="spinReload('ra', loadAccounts)"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.5v3h-3" stroke-linecap="round"
                  stroke-linejoin="round"/></svg></button></h2>
      <div id=acct-list>加载中…</div></div>
  </section>

  <section id=tab-sess hidden>
    <div class=card><h2>账户状态与在线会话（在线 = 客户端处于登录状态，登出/被踢/过期即下线）
        <button id=rs class=icon title="刷新" onclick="spinReload('rs', loadSessions)"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.5v3h-3" stroke-linecap="round"
                  stroke-linejoin="round"/></svg></button></h2>
      <div id=sess-list>加载中…</div></div>
  </section>

  <section id=tab-logs hidden>
    <div class=card>
      <h2>登录日志　
        <select id=ll onchange="renderLogs()">
          <option value="">全部级别</option>
          <option value=info>INFO</option>
          <option value=warn>WARN</option>
          <option value=error>ERROR</option>
        </select>
        <input id=lf placeholder="按用户/IP/日期/事件过滤…" style="width:220px"
          oninput="renderLogs()">
        <button id=rl class=icon title="刷新日志" onclick="spinReload('rl', loadLogs)">
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.5v3h-3" stroke-linecap="round"
                  stroke-linejoin="round"/></svg>
        </button>
      </h2>
      <div id=login-list>加载中…</div>
    </div>
    <div class=grid2>
      <div class=card><h2>登录统计（按用户汇总）</h2><div id=login-stats></div></div>
      <div class=card><h2>密码修改日志</h2><div id=pw-list></div></div>
    </div>
  </section>
</main>
</div>
<div id=msg></div>
<script>
const ROLE = {user:'普通用户', admin:'管理员', sysadmin:'系统管理员'};
let ME = null, LOGS = null;
const $ = id => document.getElementById(id);
const tok = () => sessionStorage.getItem('qvadm') || '';
async function api(path, body){
  const r = await fetch('api/' + path, {
    method: body ? 'POST' : 'GET',
    headers: Object.assign({'X-Admin-Token': tok()},
      body ? {'Content-Type':'application/json'} : {}),
    body: body ? JSON.stringify(body) : undefined,
  });
  if (r.status === 401 && path !== 'login'){ toLogin(); throw new Error('unauthorized'); }
  return r.json();
}
function toLogin(){
  sessionStorage.removeItem('qvadm');
  $('console-view').style.display = 'none';
  $('login-view').style.display = 'block';
  $('lp').value = ''; $('lerr').textContent = '';
}
async function doLogin(){
  const r = await fetch('api/login', {method:'POST',
    headers:{'Content-Type':'application/json'},
    body: JSON.stringify({username: $('lu').value.trim(), password: $('lp').value})});
  const d = await r.json();
  if (!r.ok){ $('lerr').textContent = d.error || '登录失败'; return; }
  sessionStorage.setItem('qvadm', d.token);
  enterConsole();
}
async function doLogout(){
  try { await api('logout', {}); } catch(e){}
  toLogin();
}
function enterConsole(){
  $('login-view').style.display = 'none';
  $('console-view').style.display = 'block';
  show('acct');
}
async function boot(){
  if (!tok()) return toLogin();
  try { await api('me'); enterConsole(); } catch(e){ /* toLogin already ran */ }
}
function msg(t){ const m = $('msg'); m.textContent = t; m.style.display = 'block';
  clearTimeout(m._t); m._t = setTimeout(() => m.style.display = 'none', 4000); }
function show(tab){
  for (const t of ['acct','sess','logs']){
    $('tab-'+t).hidden = t !== tab;
    $('nav-'+t).className = t === tab ? 'act' : '';
  }
  refresh(tab);
}
function active(){ return ['acct','sess','logs'].find(t => !$('tab-'+t).hidden); }

async function refresh(tab){
  tab = tab || active();
  if (tab === 'acct') loadAccounts();
  else if (tab === 'sess') loadSessions();
  else loadLogs();
}

async function loadAccounts(){
  const d = await api('accounts');
  ME = d.me;
  $('who').textContent = `${ME.username} · ${ROLE[ME.role]}`;
  const rows = d.accounts.map(a => {
    const canAct = ME.role === 'sysadmin' ? a.role !== 'sysadmin' : a.role === 'user';
    const canPw = a.role !== 'sysadmin' && (ME.role === 'sysadmin' || a.role === 'user');
    const roleCell = ME.role === 'sysadmin' && a.role !== 'sysadmin'
      ? `<select onchange="act('role',{username:'${a.username}',role:this.value})">
           <option value=user ${a.role==='user'?'selected':''}>普通用户</option>
           <option value=admin ${a.role==='admin'?'selected':''}>管理员</option></select>`
      : `<span class="tag t-role">${ROLE[a.role]||a.role}</span>`;
    return `<tr>
      <td><b>${a.username}</b></td>
      <td>${roleCell}</td>
      <td>${a.disabled ? '<span class="tag t-bad">已禁用</span>' : '<span class="tag t-on">正常</span>'}</td>
      <td class=mut>${a.groups} 组 / ${a.contracts} 合约</td>
      <td class=mut>${a.has_settings ? '已同步' : '<span class=dim>无</span>'}</td>
      <td class=mut>${a.online > 0 ? `<span class="tag t-on">${a.online} 在线</span>` : '<span class=dim>—</span>'}</td>
      <td>
        ${canPw ? `<button class=op onclick="passwd('${a.username}')">改密码</button>` : ''}
        ${canAct ? `<button class=op onclick="act('logout',{username:'${a.username}'})">登出</button>
        <button class=op onclick="act('disable',{username:'${a.username}',disabled:${!a.disabled}})">${a.disabled?'启用':'禁用'}</button>
        <button class="op danger" onclick="del('${a.username}')">删除</button>` : ''}
        ${!canAct && !canPw ? '<span class=dim>无权限</span>' : ''}
      </td></tr>`;
  }).join('');
  $('acct-list').innerHTML = `<table><tr><th>账户</th><th>角色</th><th>状态</th>
    <th>数据</th><th>设置同步</th><th>会话</th><th>操作</th></tr>${rows}</table>`;
}

async function loadSessions(){
  const d = await api('sessions');
  const blocks = d.accounts.map(a => {
    const state = a.disabled ? '<span class="tag t-bad">已禁用</span>'
      : a.online.length ? '<span class="tag t-on">在线</span>' : '<span class="tag t-off">离线</span>';
    const rows = a.online.map(t => `<tr>
      <td class=mono>${t.ip}</td><td class=mono>${t.ver}</td>
      <td class=mono>${t.created}</td><td class=mono>${t.seen}</td>
      <td class=mono>${t.duration||'-'}</td></tr>`).join('');
    const table = a.online.length
      ? `<table><tr><th>IP</th><th>客户端版本</th><th>登录时间</th><th>最近活动</th><th>登录时长</th></tr>${rows}</table>`
      : '<div class=dim style="padding:4px 12px">无在线会话</div>';
    return `<div style="margin-bottom:14px">
      <div style="margin-bottom:4px"><b>${a.username}</b>
        <span class="tag t-role">${ROLE[a.role]||a.role}</span> ${state}</div>${table}</div>`;
  }).join('');
  $('sess-list').innerHTML = blocks || '<div class=dim>无账户</div>';
}

async function spinReload(btnId, loader){
  const b = $(btnId); b.classList.add('spin');
  try { await loader(); } finally { setTimeout(() => b.classList.remove('spin'), 300); }
}
async function loadLogs(){
  LOGS = await api('logs');
  renderLogs();
  const stats = {};
  for (const l of LOGS.logins){
    if ((l.level || 'info') !== 'info') continue;   // stats = successful logins
    const st = stats[l.user] = stats[l.user] || {n:0, last:'', ips:new Set()};
    st.n++; if (l.at > st.last) st.last = l.at; if (l.ip) st.ips.add(l.ip);
  }
  $('login-stats').innerHTML = `<table><tr><th>用户</th><th>登录次数</th>
    <th>独立 IP（去重）</th><th>最近登录</th></tr>` +
    Object.entries(stats).sort((a,b) => b[1].n - a[1].n).map(([u,st]) =>
      `<tr><td><b>${u}</b></td><td>${st.n}</td>
       <td>${st.ips.size}<div class="dim mono" style="font-size:11px">${[...st.ips].join('<br>')}</div></td>
       <td class=mono>${st.last}</td></tr>`).join('') + '</table>';
  $('pw-list').innerHTML = LOGS.passwords.length
    ? `<table><tr><th>用户</th><th>时间</th><th>IP</th><th>操作者</th></tr>` +
      LOGS.passwords.map(l => `<tr><td><b>${l.user}</b></td><td class=mono>${l.at}</td>
        <td class=mono>${l.ip||'-'}</td><td>${l.by==='self'?'本人':l.by}</td></tr>`).join('') + '</table>'
    : '<div class=dim>无记录</div>';
}
function renderLogs(){
  if (!LOGS) return;
  const f = $('lf').value.trim().toLowerCase();
  const lv = $('ll').value;
  const rows = LOGS.logins.filter(l =>
    (!lv || (l.level||'info') === lv) &&
    (!f || l.user.toLowerCase().includes(f) || l.ip.includes(f) || l.at.includes(f)
        || (l.event||'').toLowerCase().includes(f)))
    .slice(0, 200).map(l => {
      const level = l.level || 'info';
      return `<tr${level === 'error' ? ' class=err' : ''}>
        <td><span class="lv lv-${level}">${level.toUpperCase()}</span></td>
        <td><b>${l.user}</b></td><td>${l.event||'登录成功'}</td>
        <td class=mono>${l.at}</td><td class=mono>${l.ip||'-'}</td>
        <td class=mono>${l.ver||'-'}</td></tr>`;
    }).join('');
  $('login-list').innerHTML = rows
    ? `<table><tr><th>级别</th><th>用户</th><th>事件</th><th>时间</th><th>IP</th><th>客户端版本</th></tr>${rows}</table>`
    : '<div class=dim>无匹配记录</div>';
}

async function act(path, body){ const r = await api('act/' + path, body); msg(r.error || '已完成'); refresh(); }
function createAccount(){
  const u = $('nu').value.trim(), p = $('np').value;
  if (!u || !p) return msg('用户名和密码都要填');
  act('create', {username:u, password:p}); $('np').value = '';
}
function passwd(u){
  const p = prompt(`为 ${u} 设置新密码（管理员重置无需旧密码）：`); if (!p) return;
  const kick = confirm('同时登出该用户当前所有登录状态？（确定=登出，取消=保留）');
  act('password', {username:u, password:p, logout:kick});
}
function del(u){ if (confirm(`确认删除账户 ${u}？其分组与设置数据将一并删除。`)) act('delete', {username:u}); }

boot();
setInterval(() => { if ($('console-view').style.display !== 'none') refresh(); }, 10000);
</script></html>"""


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

    def _ip(self):
        # Behind EdgeOne + the nginx SNI stream layer, the socket peer and even
        # X-Real-IP are loopback; the visitor's address arrives in the CDN's
        # X-Forwarded-For (first hop).
        forwarded = (self.headers.get("X-Forwarded-For") or "").split(",")[0].strip()
        real = self.headers.get("X-Real-IP") or ""
        for candidate in (forwarded, real):
            if candidate and candidate != "127.0.0.1":
                return candidate
        return real or self.client_address[0]

    def _ver(self):
        raw = (self.headers.get("X-QV-Version") or "")[:32]
        return re.sub(r"[^A-Za-z0-9._-]", "", raw)

    def _auth(self):
        """(user, doc, token) behind the Bearer token, or None (response sent).
        Also refreshes the token's last-seen/ip/version — the admin page's
        online view is built from exactly these touches."""
        header = self.headers.get("Authorization") or ""
        token = header[7:] if header.startswith("Bearer ") else ""
        user, doc = user_for_token(token)
        if user is None or doc is None:
            self._bad("unauthorized", 401)
            return None
        if doc.get("disabled"):
            self._bad("账户已禁用", 401)
            return None
        touch_token(user, token, self._ip(), self._ver())
        # Re-read after the touch so callers see the freshest doc.
        return user, load_account(user) or doc, token

    def _admin(self):
        """Web-console auth: the X-Admin-Token header must name a live admin
        session (minted by /web/api/login). Returns (username, role) or None
        (401 already sent)."""
        result = web_session_check(self.headers.get("X-Admin-Token") or "")
        if result is None:
            self._bad("unauthorized", 401)
        return result

    # ------------------------------------------------------------ GET

    def do_GET(self):
        url = urlparse(self.path)
        q = parse_qs(url.query)

        if url.path == "/web" or url.path == "/web/":
            # The page itself is public; it decides login-vs-console by asking
            # /web/api/me with its stored token.
            body = ADMIN_PAGE.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if url.path == "/web/api/me":
            actor = self._admin()
            if actor is None:
                return
            return self._json({"username": actor[0], "role": actor[1]})

        if url.path == "/web/api/sessions":
            if self._admin() is None:
                return
            now = datetime.now(CN)
            out = []
            for name in sorted(os.listdir(ACCOUNTS)) if os.path.isdir(ACCOUNTS) else []:
                if not name.endswith(".json"):
                    continue
                if name[:-5] in HIDDEN_ACCOUNTS:
                    continue
                doc = load_account(name[:-5])
                if doc is None:
                    continue
                normalize_tokens(doc)
                sessions = []
                for t in doc["tokens"]:
                    ip = t.get("ip") or ""
                    # Loopback/blank IPs are pre-proxy-fix leftovers, no signal.
                    if ip in ("", "127.0.0.1"):
                        continue
                    # Online = the session EXISTS (signed in, not logged out /
                    # kicked / expired) — per spec, not an activity window. The
                    # last-activity column lets the viewer judge staleness.
                    created = t.get("created") or ""
                    duration = ""
                    try:
                        created_dt = datetime.strptime(created, "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                        minutes = int((now - created_dt).total_seconds() // 60)
                        duration = f"{minutes // 60}h{minutes % 60:02d}m"
                    except Exception:
                        pass
                    sessions.append({"ip": ip, "ver": t.get("ver") or "-",
                                     "created": created, "seen": t.get("seen") or "-",
                                     "duration": duration})
                out.append({"username": name[:-5], "role": role_of(doc),
                            "disabled": bool(doc.get("disabled")), "online": sessions})
            return self._json({"accounts": out})

        if url.path == "/web/api/logs":
            if self._admin() is None:
                return
            logins, passwords = [], []
            for name in sorted(os.listdir(ACCOUNTS)) if os.path.isdir(ACCOUNTS) else []:
                if not name.endswith(".json"):
                    continue
                user = name[:-5]
                if user in HIDDEN_ACCOUNTS:
                    continue
                doc = load_account(user)
                if doc is None:
                    continue
                for entry in doc.get("logins") or []:
                    logins.append({"user": user, "at": entry.get("at") or "",
                                   "ip": entry.get("ip") or "", "ver": entry.get("ver") or "",
                                   "level": entry.get("level") or "info",
                                   "event": entry.get("event") or "登录成功"})
                for entry in doc.get("pwlogs") or []:
                    passwords.append({"user": user, "at": entry.get("at") or "",
                                      "ip": entry.get("ip") or "", "by": entry.get("by") or ""})
            logins.sort(key=lambda x: x["at"], reverse=True)
            passwords.sort(key=lambda x: x["at"], reverse=True)
            return self._json({"logins": logins[:500], "passwords": passwords[:200]})

        if url.path == "/web/api/accounts":
            actor = self._admin()
            if actor is None:
                return
            return self._json({"me": {"username": actor[0], "role": actor[1]},
                               "accounts": self._account_summaries()})

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

        if url.path == "/kline":
            if self._auth() is None:
                return
            secid = (q.get("secid") or [""])[0]
            klt = (q.get("klt") or [""])[0]
            fqt = (q.get("fqt") or [""])[0]
            lmt = (q.get("lmt") or [""])[0]
            if not re.match(r"^\d{1,3}\.[A-Za-z0-9]{1,12}$", secid) \
                    or klt not in ("101", "102", "103") or fqt not in ("0", "1", "2") \
                    or not lmt.isdigit() or not 1 <= int(lmt) <= 1000:
                return self._bad("bad kline params")
            body = kline_body(secid, klt, fqt, int(lmt))
            if body is None:
                return self._bad("upstream unavailable", 502)
            data = body.encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return

        if url.path == "/groups":
            authed = self._auth()
            if authed is None:
                return
            _, doc, _ = authed
            return self._json({"groups": doc.get("groups") or [],
                               "at": int(doc.get("groups_at") or 0)})

        if url.path == "/settings":
            authed = self._auth()
            if authed is None:
                return
            _, doc, _ = authed
            settings = doc.get("settings")
            if settings is None:
                return self._bad("not found", 404)
            return self._json({"settings": settings, "updated": doc.get("settings_updated")})

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

    def _account_summaries(self):
        now = datetime.now(CN)
        out = []
        if not os.path.isdir(ACCOUNTS):
            return out
        for name in sorted(os.listdir(ACCOUNTS)):
            if not name.endswith(".json"):
                continue
            user = name[:-5]
            if user in HIDDEN_ACCOUNTS:
                continue
            doc = load_account(user)
            if doc is None:
                continue
            normalize_tokens(doc)
            tokens = []
            online = 0
            for t in doc["tokens"]:
                ip = t.get("ip") or ""
                is_online = ip not in ("", "127.0.0.1")   # valid session = online
                duration = ""
                try:
                    created_dt = datetime.strptime(t.get("created") or "",
                                                   "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                    minutes = int((now - created_dt).total_seconds() // 60)
                    duration = f"{minutes // 60}h{minutes % 60:02d}m"
                except Exception:
                    pass
                online += 1 if is_online else 0
                tokens.append({"online": is_online, "ip": t.get("ip"), "ver": t.get("ver"),
                               "created": t.get("created"), "seen": t.get("seen"), "duration": duration})
            groups = doc.get("groups") or []
            out.append({
                "username": user,
                "role": role_of(doc),
                "disabled": bool(doc.get("disabled")),
                "groups": len(groups),
                "contracts": sum(len(g.get("codes") or []) for g in groups),
                "has_settings": doc.get("settings") is not None,
                "online": online,
                "tokens": tokens,
                "logins": list(reversed((doc.get("logins") or [])[-20:])),
            })
        return out

    # ------------------------------------------------------------ POST

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length > 512 * 1024:
            return self._bad("payload too large", 413)
        raw = self.rfile.read(length) if length else b""

        if self.path == "/web/api/login":
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            user = str(doc.get("username") or "")
            password = str(doc.get("password") or "")
            account = load_account(user) if USER_RE.match(user) else None
            if account is None or account.get("disabled") \
                    or role_of(account) not in ("admin", "sysadmin") \
                    or not verify_password(account, password):
                self._log_login(user, "error", "管理台登录失败") if account is not None else None
                return self._bad("用户名或密码错误，或无管理权限", 401)
            token = web_session_create(user, role_of(account), self._ip())
            self._log_login(user, "info", "管理台登录")
            log(f"web login {user} from {self._ip()}")
            return self._json({"token": token, "username": user, "role": role_of(account)})

        if self.path == "/web/api/logout":
            token = self.headers.get("X-Admin-Token") or ""
            session = web_session_check(token)
            web_session_drop(token)
            if session is not None:
                self._log_login(session[0], "info", "管理台登出")
            return self._json({"ok": True})

        if self.path.startswith("/web/api/act/"):
            return self._admin_post(self.path[13:], raw)

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
                token = self._mint()
                save_account(user, {
                    "auth": {"salt": salt, "hash": hash_pw(password, salt),
                             "iters": PBKDF2_ITERS},
                    "tokens": [token],
                    "groups": [],
                    "logins": [self._login_entry()],
                    "created": f"{datetime.now(CN):%F %T}",
                })
            _token_cache[token["t"]] = user
            log(f"register account {user} from {self._ip()}")
            return self._json({"token": token["t"], "username": user})

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
            if account.get("disabled"):
                self._log_login(user, "error", "登录失败：账户已禁用")
                return self._bad("账户已禁用", 403)
            if not verify_password(account, password):
                self._log_login(user, "error", "登录失败：密码错误")
                return self._bad("用户名或密码错误", 401)
            token = self._mint()
            with _lock:
                account = load_account(user) or account
                normalize_tokens(account)
                cutoff = f"{datetime.now(CN) - timedelta(days=30):%F %T}"
                account["tokens"] = [t for t in account["tokens"]
                                     if (t.get("seen") or t.get("created") or "") >= cutoff]
                account["tokens"] = account["tokens"][-(MAX_TOKENS - 1):] + [token]
                logins = account.get("logins") or []
                account["logins"] = (logins + [self._login_entry()])[-100:]
                save_account(user, account)
            _token_cache[token["t"]] = user
            log(f"login {user} from {self._ip()} ver={self._ver() or '-'}")
            return self._json({"token": token["t"], "username": user})

        if self.path == "/sync":
            authed = self._auth()
            if authed is None:
                return
            user, _, _ = authed
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            groups = doc.get("groups")
            if not isinstance(groups, list) or len(groups) > 200:
                return self._bad("bad groups")
            at = int(doc.get("at") or 0)
            with _lock:
                account = load_account(user)
                if account is None:
                    return self._bad("unauthorized", 401)
                # Old clients (≤1.0.52) send no "panel" field. Defaulting those
                # to true silently wiped the rotation flags every 5 minutes —
                # instead, a missing field inherits the stored flag by group
                # name, so an outdated machine can't destroy what newer ones set.
                stored_panel = {g.get("name"): bool(g.get("panel", True))
                                for g in account.get("groups") or []}
                clean = []
                for g in groups[:200]:
                    name = str(g.get("name") or "")[:64]
                    codes = [str(c).upper() for c in (g.get("codes") or [])[:2000]]
                    panel = (bool(g["panel"]) if "panel" in g
                             else stored_panel.get(name, True))
                    clean.append({"name": name, "codes": codes, "panel": panel})
                # Arrival order IS the order: every push overwrites. Multiple
                # clients racing is resolved by "last push wins", per design.
                account["groups"] = clean
                account["groups_at"] = at
                account["synced"] = f"{datetime.now(CN):%F %T}"
                save_account(user, account)
            total = sum(len(g["codes"]) for g in clean)
            return self._json({"ok": True, "groups": len(clean), "contracts": total})

        if self.path == "/logout":
            authed = self._auth()
            if authed is None:
                return
            user, _, token = authed
            with _lock:
                account = load_account(user)
                if account is not None:
                    normalize_tokens(account)
                    account["tokens"] = [t for t in account["tokens"] if t["t"] != token]
                    save_account(user, account)
            _token_cache.pop(token, None)
            self._log_login(user, "info", "登出")
            return self._json({"ok": True})

        if self.path == "/password":
            authed = self._auth()
            if authed is None:
                return
            user, _, token = authed
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            old_pw = str(doc.get("old") or "")
            new_pw = str(doc.get("new") or "")
            if len(new_pw) < 6:
                return self._bad("新密码至少 6 位")
            with _lock:
                account = load_account(user)
                if account is None:
                    return self._bad("unauthorized", 401)
                if not verify_password(account, old_pw):
                    return self._bad("旧密码不正确", 403)
                salt = secrets.token_hex(16)
                account["auth"] = {"salt": salt, "hash": hash_pw(new_pw, salt),
                                   "iters": PBKDF2_ITERS}
                # Password change invalidates every OTHER session; the one that
                # made the change keeps working.
                normalize_tokens(account)
                account["tokens"] = [t for t in account["tokens"] if t["t"] == token]
                pwlogs = account.get("pwlogs") or []
                account["pwlogs"] = (pwlogs + [{
                    "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                    "by": "self", "ver": self._ver(),
                }])[-50:]
                save_account(user, account)
            _token_cache.clear()
            log(f"password self-change {user} from {self._ip()}")
            return self._json({"ok": True})

        if self.path == "/settings":
            authed = self._auth()
            if authed is None:
                return
            user, _, _ = authed
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            settings = doc.get("settings")
            if not isinstance(settings, dict):
                return self._bad("bad settings")
            with _lock:
                account = load_account(user)
                if account is None:
                    return self._bad("unauthorized", 401)
                account["settings"] = settings
                account["settings_updated"] = f"{datetime.now(CN):%F %T}"
                save_account(user, account)
            return self._json({"ok": True})

        self._bad("not found", 404)

    def _mint(self):
        return {"t": secrets.token_hex(32), "created": f"{datetime.now(CN):%F %T}",
                "ip": self._ip(), "ver": self._ver(), "seen": f"{datetime.now(CN):%F %T}"}

    def _login_entry(self):
        return {"at": f"{datetime.now(CN):%F %T}", "ip": self._ip(), "ver": self._ver(),
                "level": "info", "event": "登录成功"}

    def _log_login(self, user, level, event):
        """Appends a login-stream entry (e.g. a failed attempt) to the account."""
        with _lock:
            account = load_account(user)
            if account is None:
                return
            logins = account.get("logins") or []
            account["logins"] = (logins + [{
                "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                "ver": self._ver(), "level": level, "event": event,
            }])[-100:]
            save_account(user, account)

    def _admin_post(self, action, raw):
        actor = self._admin()
        if actor is None:
            return
        actor_name, actor_role = actor
        try:
            doc = json.loads(raw) if raw else {}
        except Exception:
            return self._bad("bad json")
        user = str(doc.get("username") or "")
        if action != "create" and not USER_RE.match(user):
            return self._bad("bad username")

        if action == "create":
            user = str(doc.get("username") or "")
            password = str(doc.get("password") or "")
            if not USER_RE.match(user):
                return self._bad("用户名需为 3~32 位字母/数字/下划线")
            if len(password) < 6:
                return self._bad("密码至少 6 位")
            with _lock:
                if os.path.exists(account_path(user)):
                    return self._bad("用户名已存在", 409)
                salt = secrets.token_hex(16)
                save_account(user, {
                    "auth": {"salt": salt, "hash": hash_pw(password, salt),
                             "iters": PBKDF2_ITERS},
                    "tokens": [], "groups": [], "logins": [],
                    "created": f"{datetime.now(CN):%F %T}",
                })
            log(f"admin[{actor_name}]: create {user}")
            return self._json({"ok": True})

        account = load_account(user)
        if account is None:
            return self._bad("账户不存在", 404)

        target_role = role_of(account)

        # Permission wall. 普通管理员 only touches ordinary users; 系统管理员
        # touches everyone except the one thing that must stay intact — the
        # single sysadmin account itself (delete/disable/demote would orphan
        # the system).
        if actor_role == "admin" and target_role != "user":
            return self._bad("无权操作管理员账户", 403)
        if target_role == "sysadmin" and action in ("delete", "disable", "role"):
            return self._bad("系统管理员账户不可删除/禁用/改角色", 403)

        if action == "role":
            if actor_role != "sysadmin":
                return self._bad("仅系统管理员可修改角色", 403)
            role = str(doc.get("role") or "")
            if role not in ("user", "admin"):
                return self._bad("角色只能是 user 或 admin")
            with _lock:
                account["role"] = role
                save_account(user, account)
            log(f"admin[{actor_name}]: role {user} -> {role}")
            return self._json({"ok": True})

        if action == "delete":
            with _lock:
                try:
                    os.remove(account_path(user))
                except OSError:
                    pass
            _token_cache.clear()
            log(f"admin[{actor_name}]: delete {user}")
            return self._json({"ok": True})

        if action == "disable":
            with _lock:
                account["disabled"] = bool(doc.get("disabled"))
                if account["disabled"]:
                    account["tokens"] = []   # 禁用即踢下线
                save_account(user, account)
            _token_cache.clear()
            log(f"admin[{actor_name}]: disable {user} -> {account['disabled']}")
            return self._json({"ok": True})

        if action == "logout":
            with _lock:
                account["tokens"] = []
                save_account(user, account)
            _token_cache.clear()
            self._log_login(user, "warn", f"被管理员登出（{actor_name}）")
            log(f"admin[{actor_name}]: logout {user}")
            return self._json({"ok": True})

        if action == "password":
            password = str(doc.get("password") or "")
            if len(password) < 6:
                return self._bad("密码至少 6 位")
            with _lock:
                salt = secrets.token_hex(16)
                account["auth"] = {"salt": salt, "hash": hash_pw(password, salt),
                                   "iters": PBKDF2_ITERS}
                if doc.get("logout"):
                    account["tokens"] = []
                pwlogs = account.get("pwlogs") or []
                account["pwlogs"] = (pwlogs + [{
                    "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                    "by": f"admin:{actor_name}",
                }])[-50:]
                save_account(user, account)
            _token_cache.clear()
            log(f"admin[{actor_name}]: password {user} (logout={bool(doc.get('logout'))})")
            return self._json({"ok": True})

        return self._bad("not found", 404)


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

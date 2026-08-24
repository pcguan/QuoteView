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
</style>
<header>
  <h1>QuoteView 管理台</h1>
  <nav>
    <button id=nav-acct class=act onclick="show('acct')">账户管理</button>
    <button id=nav-sess onclick="show('sess')">会话管理</button>
    <button id=nav-logs onclick="show('logs')">日志管理</button>
  </nav>
  <span id=who></span>
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
    <div class=card><h2>账户列表</h2><div id=acct-list>加载中…</div></div>
  </section>

  <section id=tab-sess hidden>
    <div class=card><h2>账户状态与在线会话（在线 = 10 分钟内有活动；仅展示有效会话）</h2>
      <div id=sess-list>加载中…</div></div>
  </section>

  <section id=tab-logs hidden>
    <div class=card>
      <h2>登录日志　<input id=lf placeholder="按用户/IP/日期过滤…" style="width:220px"
          oninput="renderLogs()"></h2>
      <div id=login-list>加载中…</div>
    </div>
    <div class=grid2>
      <div class=card><h2>登录统计（按用户汇总）</h2><div id=login-stats></div></div>
      <div class=card><h2>密码修改日志</h2><div id=pw-list></div></div>
    </div>
  </section>
</main>
<div id=msg></div>
<script>
const ROLE = {user:'普通用户', admin:'管理员', sysadmin:'系统管理员'};
let ME = null, LOGS = null;
const $ = id => document.getElementById(id);
const api = (path, body) => fetch('admin/' + path, body ? {method:'POST',
  headers:{'Content-Type':'application/json'}, body: JSON.stringify(body)} : {}).then(r => r.json());
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
      ? `<table><tr><th>IP</th><th>客户端版本</th><th>登录时间</th><th>最近活动</th><th>在线时长</th></tr>${rows}</table>`
      : '<div class=dim style="padding:4px 12px">无在线会话</div>';
    return `<div style="margin-bottom:14px">
      <div style="margin-bottom:4px"><b>${a.username}</b>
        <span class="tag t-role">${ROLE[a.role]||a.role}</span> ${state}</div>${table}</div>`;
  }).join('');
  $('sess-list').innerHTML = blocks || '<div class=dim>无账户</div>';
}

async function loadLogs(){
  LOGS = await api('logs');
  renderLogs();
  const stats = {};
  for (const l of LOGS.logins){
    const st = stats[l.user] = stats[l.user] || {n:0, last:'', ips:new Set()};
    st.n++; if (l.at > st.last) st.last = l.at; if (l.ip) st.ips.add(l.ip);
  }
  $('login-stats').innerHTML = `<table><tr><th>用户</th><th>登录次数</th>
    <th>独立 IP 数</th><th>最近登录</th></tr>` +
    Object.entries(stats).sort((a,b) => b[1].n - a[1].n).map(([u,st]) =>
      `<tr><td><b>${u}</b></td><td>${st.n}</td><td>${st.ips.size}</td>
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
  const rows = LOGS.logins.filter(l =>
    !f || l.user.toLowerCase().includes(f) || l.ip.includes(f) || l.at.includes(f))
    .slice(0, 200).map(l => `<tr><td><b>${l.user}</b></td><td class=mono>${l.at}</td>
      <td class=mono>${l.ip||'-'}</td><td class=mono>${l.ver||'-'}</td></tr>`).join('');
  $('login-list').innerHTML = rows
    ? `<table><tr><th>用户</th><th>时间</th><th>IP</th><th>客户端版本</th></tr>${rows}</table>`
    : '<div class=dim>无匹配记录</div>';
}

async function act(path, body){ const r = await api(path, body); msg(r.error || '已完成'); refresh(); }
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

loadAccounts(); setInterval(() => refresh(), 30000);
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
        return (self.headers.get("X-QV-Version") or "")[:32]

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
        """HTTP Basic auth for the console, against ACCOUNT credentials — the
        same username/password the client uses. Only admin/sysadmin roles get
        in. Returns (username, role) or None (401 already sent)."""
        import base64
        header = self.headers.get("Authorization") or ""
        if header.startswith("Basic "):
            try:
                user, _, pw = base64.b64decode(header[6:]).decode().partition(":")
                account = load_account(user) if USER_RE.match(user) else None
                if (account is not None and not account.get("disabled")
                        and role_of(account) in ("admin", "sysadmin")
                        and verify_password(account, pw)):
                    return user, role_of(account)
            except Exception:
                pass
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="QuoteView Admin"')
        self.send_header("Content-Length", "0")
        self.end_headers()
        return None

    # ------------------------------------------------------------ GET

    def do_GET(self):
        url = urlparse(self.path)
        q = parse_qs(url.query)

        if url.path == "/admin" or url.path == "/admin/":
            if self._admin() is None:
                return
            body = ADMIN_PAGE.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if url.path == "/admin/sessions":
            if self._admin() is None:
                return
            now = datetime.now(CN)
            out = []
            for name in sorted(os.listdir(ACCOUNTS)) if os.path.isdir(ACCOUNTS) else []:
                if not name.endswith(".json"):
                    continue
                doc = load_account(name[:-5])
                if doc is None:
                    continue
                normalize_tokens(doc)
                sessions = []
                for t in doc["tokens"]:
                    seen = t.get("seen") or ""
                    ip = t.get("ip") or ""
                    # Online means active in the last 10 minutes; loopback/blank
                    # IPs are pre-proxy-fix leftovers and carry no information.
                    if not seen or ip in ("", "127.0.0.1"):
                        continue
                    try:
                        seen_dt = datetime.strptime(seen, "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                    except Exception:
                        continue
                    if now - seen_dt > timedelta(minutes=10):
                        continue
                    duration = ""
                    created = t.get("created") or ""
                    try:
                        created_dt = datetime.strptime(created, "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                        minutes = int((seen_dt - created_dt).total_seconds() // 60)
                        duration = f"{minutes // 60}h{minutes % 60:02d}m"
                    except Exception:
                        pass
                    sessions.append({"ip": ip, "ver": t.get("ver") or "-",
                                     "created": created, "seen": seen, "duration": duration})
                out.append({"username": name[:-5], "role": role_of(doc),
                            "disabled": bool(doc.get("disabled")), "online": sessions})
            return self._json({"accounts": out})

        if url.path == "/admin/logs":
            if self._admin() is None:
                return
            logins, passwords = [], []
            for name in sorted(os.listdir(ACCOUNTS)) if os.path.isdir(ACCOUNTS) else []:
                if not name.endswith(".json"):
                    continue
                user = name[:-5]
                doc = load_account(user)
                if doc is None:
                    continue
                for entry in doc.get("logins") or []:
                    logins.append({"user": user, "at": entry.get("at") or "",
                                   "ip": entry.get("ip") or "", "ver": entry.get("ver") or ""})
                for entry in doc.get("pwlogs") or []:
                    passwords.append({"user": user, "at": entry.get("at") or "",
                                      "ip": entry.get("ip") or "", "by": entry.get("by") or ""})
            logins.sort(key=lambda x: x["at"], reverse=True)
            passwords.sort(key=lambda x: x["at"], reverse=True)
            return self._json({"logins": logins[:500], "passwords": passwords[:200]})

        if url.path == "/admin/accounts":
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
            return self._json({"groups": doc.get("groups") or []})

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
            doc = load_account(user)
            if doc is None:
                continue
            normalize_tokens(doc)
            tokens = []
            online = 0
            for t in doc["tokens"]:
                seen = t.get("seen") or ""
                is_online = False
                duration = ""
                try:
                    if seen:
                        seen_dt = datetime.strptime(seen, "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                        is_online = (now - seen_dt) <= timedelta(minutes=10)
                    created = t.get("created") or ""
                    if created and seen:
                        created_dt = datetime.strptime(created, "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                        minutes = int((seen_dt - created_dt).total_seconds() // 60)
                        duration = f"{minutes // 60}h{minutes % 60:02d}m"
                except Exception:
                    pass
                online += 1 if is_online else 0
                tokens.append({"online": is_online, "ip": t.get("ip"), "ver": t.get("ver"),
                               "created": t.get("created"), "seen": seen, "duration": duration})
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

        if self.path.startswith("/admin/"):
            return self._admin_post(self.path[7:], raw)

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
                return self._bad("账户已禁用", 403)
            auth = account.get("auth") or {}
            expect = auth.get("hash") or ""
            got = hashlib.pbkdf2_hmac("sha256", password.encode(),
                                      bytes.fromhex(auth.get("salt") or "00"),
                                      int(auth.get("iters") or PBKDF2_ITERS)).hex()
            if not secrets.compare_digest(expect, got):
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
                account["logins"] = (logins + [self._login_entry()])[-50:]
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
        return {"at": f"{datetime.now(CN):%F %T}", "ip": self._ip(), "ver": self._ver()}

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

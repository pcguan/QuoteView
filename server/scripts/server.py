#!/usr/bin/env python3
"""QuoteView snapshot server.

Runs on the NAS behind nginx (/quoteview/api/ -> 127.0.0.1:8388). Three jobs:

1. Account registration and login (POST /register, POST /login): interaction
   is account-level, not per-installation — several machines logging into the
   same account share one set of groups. Passwords are PBKDF2-hashed; logins
   mint bearer tokens that survive server restarts.
2. Accept the account's groups+contracts on change (POST /sync, Bearer
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

import base64
import hashlib
import ipaddress
import json
from collections import Counter
import os
import re
import secrets
import socket as socketlib
import struct
import threading
import time
import urllib.request
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

DATA = os.environ.get("QV_DATA", "/data")
LOG_FILE = os.environ.get("QV_LOG", "")
PORT = int(os.environ.get("QV_PORT", "8388"))
# 127.0.0.1 = nginx-only (the hardened default). The NAS sets 0.0.0.0 so the
# console is also reachable LAN-direct (http://<nas-ip>:8388/web/) when the
# internet link — and with it the public domain — is down.
BIND = os.environ.get("QV_BIND", "127.0.0.1")
# 0 (the default) = keep the trend archive FOREVER. A year of the full
# universe measures under 2GB — not worth ever losing a comparison day over.
RETAIN_DAYS = int(os.environ.get("QV_RETAIN_DAYS", "0"))
FETCH_GAP_S = float(os.environ.get("QV_FETCH_GAP", "1.5"))

# ---- 配置修改 audit-log differ -------------------------------------------
# Mirrors the client's StealthField enum by ordinal (append-only over there, so
# this list only ever grows) — the payload stores field ids, the log wants names.
STEALTH_FIELDS = [
    "合约编码", "合约名称", "最新价", "涨跌额", "涨跌幅", "今开", "最高", "最低",
    "昨收", "时间", "成交量", "成交额", "总市值", "流通市值", "换手率", "量比",
    "振幅", "均价", "市盈TTM", "市净率", "分组名", "昨日涨幅", "3日涨幅",
    "5日涨幅", "10日涨幅", "20日涨幅", "60日涨幅", "年初至今", "涨速",
    "主力净流入", "主力占比", "超大单", "大单", "中单", "小单", "外盘", "内盘",
    "涨停价", "跌停价", "52周最高", "52周最低", "股息率", "行业", "地区",
    "概念", "备注",
]
CHART_NAMES = {0: "关闭", 1: "分时缩略图", 2: "五档盘口"}
SETTING_KEYS = ("stealth", "quoteColumns", "notes", "aggEqual", "paneWidth",
                "stealthTemplates", "stealthActive")


def field_name(i):
    try:
        return STEALTH_FIELDS[int(i)]
    except Exception:
        return "字段%s" % i


def brief(items, cap=6):
    items = list(items)
    if len(items) <= cap:
        return "、".join(items)
    return "、".join(items[:cap]) + " 等%d项" % len(items)


def fmt_num(v):
    try:
        f = float(v)
        return str(int(f)) if f == int(f) else ("%g" % f)
    except Exception:
        return str(v)


def diff_stealth(old, new, prefix=""):
    """One human sentence per concrete change inside a StealthConfig blob."""
    old, new = old or {}, new or {}
    parts = []
    for key, label in (("shade", "亮度"), ("rows", "显示行数"), ("rowGap", "行距"),
                       ("fontSize", "字体大小")):
        if old.get(key) != new.get(key):
            parts.append("%s%s %s→%s" % (prefix, label,
                                         fmt_num(old.get(key)), fmt_num(new.get(key))))
    if old.get("header") != new.get("header"):
        parts.append(prefix + ("显示列名" if new.get("header") else "隐藏列名"))
    if old.get("headerColor") != new.get("headerColor"):
        parts.append(prefix + "列名颜色")
    if old.get("chart") != new.get("chart"):
        parts.append("%s面板图表→%s" % (prefix,
                     CHART_NAMES.get(new.get("chart"), new.get("chart"))))
    if (old.get("left"), old.get("top")) != (new.get("left"), new.get("top")):
        parts.append("%s面板位置" % prefix)
    of = {f.get("field"): f for f in old.get("fields") or [] if isinstance(f, dict)}
    nf = {f.get("field"): f for f in new.get("fields") or [] if isinstance(f, dict)}
    shown, hidden, recolored = [], [], []
    for i in sorted(set(of) | set(nf), key=lambda x: (x is None, x)):
        o, n = of.get(i) or {}, nf.get(i) or {}
        if bool(o.get("visible")) != bool(n.get("visible")):
            (shown if n.get("visible") else hidden).append(field_name(i))
        elif any(o.get(k) != n.get(k) for k in ("color", "pos", "neg")):
            recolored.append(field_name(i))
    if shown:
        parts.append(prefix + "显示字段+" + brief(shown))
    if hidden:
        parts.append(prefix + "显示字段-" + brief(hidden))
    if recolored:
        parts.append(prefix + "字段颜色(" + brief(recolored) + ")")
    if not parts and old != new:
        parts.append(prefix + "面板外观调整")
    return parts


def diff_columns(old, new):
    om = {c.get("key"): c for c in old or [] if isinstance(c, dict)}
    nm = {c.get("key"): c for c in new or [] if isinstance(c, dict)}
    shown, hidden, width, order = [], [], [], False
    for k in om.keys() | nm.keys():
        o, n = om.get(k) or {}, nm.get(k) or {}
        name = k or "?"
        if bool(o.get("visible", True)) != bool(n.get("visible", True)):
            (shown if n.get("visible", True) else hidden).append(name)
            continue
        if o.get("width") != n.get("width"):
            width.append(name)
        if o.get("order") != n.get("order"):
            order = True
    parts = []
    if shown:
        parts.append("显示列+" + brief(shown))
    if hidden:
        parts.append("显示列-" + brief(hidden))
    if width:
        parts.append("列宽(" + brief(sorted(width)) + ")")
    if order:
        parts.append("列顺序调整")
    if not parts and om != nm:
        parts.append("行情列调整")
    return parts


def diff_notes(old, new):
    old, new = old or {}, new or {}
    parts = []
    added = sorted(c for c in new if c not in old)
    removed = sorted(c for c in old if c not in new)
    edited = sorted(c for c in new if c in old and old[c] != new[c])
    if added:
        parts.append("备注+" + brief(added))
    if removed:
        parts.append("备注-" + brief(removed))
    if edited:
        parts.append("改备注(" + brief(edited) + ")")
    return parts


def diff_templates(old, new, act_old, act_new):
    om = {t.get("name"): t for t in old or [] if isinstance(t, dict)}
    nm = {t.get("name"): t for t in new or [] if isinstance(t, dict)}
    parts = []
    parts += ["+模板「%s」" % n for n in nm if n not in om]
    parts += ["-模板「%s」" % n for n in om if n not in nm]
    for n in nm:
        if n in om and om[n] != nm[n]:
            parts += (diff_stealth((om[n] or {}).get("stealth"),
                                   (nm[n] or {}).get("stealth"),
                                   prefix="模板「%s」" % n)
                      or ["改模板「%s」" % n])
    if act_old != act_new and act_new:
        parts.append("切换模板→「%s」" % act_new)
    return parts


def settings_detail(stored, settings):
    """What actually changed between two settings blobs, as short sentences.
    Empty list = an echo push (only the client's "at" stamp moved)."""
    parts = []
    if stored.get("stealth") != settings.get("stealth"):
        parts += diff_stealth(stored.get("stealth"), settings.get("stealth"))
    if stored.get("quoteColumns") != settings.get("quoteColumns"):
        parts += diff_columns(stored.get("quoteColumns"), settings.get("quoteColumns"))
    if stored.get("notes") != settings.get("notes"):
        parts += diff_notes(stored.get("notes"), settings.get("notes"))
    if stored.get("aggEqual") != settings.get("aggEqual"):
        parts.append("涨跌幅口径→" + ("等权" if settings.get("aggEqual") else "加权"))
    if stored.get("paneWidth") != settings.get("paneWidth"):
        parts.append("分组栏宽度 %s→%s" % (fmt_num(stored.get("paneWidth")),
                                           fmt_num(settings.get("paneWidth"))))
    if (stored.get("stealthTemplates") != settings.get("stealthTemplates")
            or stored.get("stealthActive") != settings.get("stealthActive")):
        parts += diff_templates(stored.get("stealthTemplates"),
                                settings.get("stealthTemplates"),
                                stored.get("stealthActive"),
                                settings.get("stealthActive"))
    parts += ["调整 %s" % k
              for k in sorted(set(settings) | set(stored))
              if k not in SETTING_KEYS and k != "at"
              and settings.get(k) != stored.get(k)]
    return parts
# Clients silent for this long stop contributing to the union.
CLIENT_TTL_DAYS = 14

CN = timezone(timedelta(hours=8))  # no DST in China
CODE_RE = re.compile(r"^(SH|SZ)\d{6}$")


def _valid_ip(text):
    try:
        ipaddress.ip_address(text)
        return True
    except ValueError:
        return False
KR_CODE_RE = re.compile(r"^KR\d{6}$")
USER_RE = re.compile(r"^[A-Za-z0-9_]{3,32}$")
TOKEN_RE = re.compile(r"^[0-9a-f]{64}$")

ACCOUNTS = os.path.join(DATA, "accounts")
TRENDS = os.path.join(DATA, "trends")
STATE = os.path.join(DATA, "state.json")

MAX_ACCOUNTS = 10          # personal server; also caps drive-by registrations

# 公网自助注册默认关闭：任何人都能占满账户槽位，更实的是注册后 /sync 的合约会
# 进入 union_codes/news_universe——服务端替他去东财/腾讯抓取，抓取预算被耗光
# 还会连累正常归档（连续失败退避）。建号走管理台（/web/api/act/create）。
# QV_OPEN_REGISTER=1 可临时放开一段时间。
OPEN_REGISTER = os.environ.get("QV_OPEN_REGISTER", "") == "1"

# 单账户能带进抓取宇宙的合约数上限。/sync 本身允许 200 组×2000 码，那是客户端
# 自己的数据，照存；但服务端的外连预算不能按那个量级放开。超出部分按代码序截断
# （只影响服务端归档/资讯，不动账户存的分组）。
MAX_ACCOUNT_CODES = int(os.environ.get("QV_MAX_ACCOUNT_CODES", "1000"))

# Accounts invisible to the web console (test/probe accounts). They work
# normally over the API; they just never appear in accounts/sessions/logs.
HIDDEN_ACCOUNTS = set(filter(None, os.environ.get("QV_HIDDEN_ACCOUNTS", "qa_probe").split(",")))
MAX_TOKENS = 10            # devices per account; oldest token drops off
PBKDF2_ITERS = 100_000

_lock = threading.Lock()
_token_cache = {}          # token -> username, rebuilt on miss

# Live WebSocket connections: token -> {user, ip, ver, since}. THE source of
# truth for "connected right now": an entry exists exactly while the socket is
# open, so a client close — graceful or crash (FIN/RST) — drops it instantly.
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
WS_CONNS = {}
_ws_lock = threading.Lock()


_log_lock = threading.Lock()


def log(msg):
    line = f"{datetime.now(CN):%F %T} {msg}"
    print(line, flush=True)
    if not LOG_FILE:
        return
    try:
        # Size-capped by simple rotation: server.log -> server.log.1 at 10MB.
        # 检查+轮转+写入必须在同一把锁里：每请求一线程，两个线程同时越过大小
        # 检查时，后一个 replace 会把前一个刚建的空文件盖到 .1 上，刚归档的
        # 10MB 历史整体消失；抢输的那条日志还会被下面的 except 静默吞掉。
        with _log_lock:
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


# ---------------------------------------------------------- login throttling
# 键是【账户 + 来源 IP】。只按账户会把限速本身变成一把武器：任何知道用户名的
# 人每分钟发一次错误口令，就能把真正的用户（含管理员和管理台）永久锁在门外。
# 按 (账户, IP) 记账后，攻击者锁住的只是自己那条来源。
#
# 判定与计数在同一把锁里完成（先扣额度、再校验口令）：check-then-act 会被并发
# 整批穿透——200 条连接可以同时读到"计数 0"，把 15 分钟 10 次的预算一次用成
# 200 次。
#
# 残余风险：分布式换 IP 的爆破不受本机制约束，那由 8 位口令下限和前置 CDN 兜。
_login_fails = {}          # (user, ip) -> [连续失败次数, 解禁时刻, 最后一次时刻]
_login_fail_lock = threading.Lock()
LOGIN_FAIL_SOFT = 5        # 起，每次失败换来指数级增长的等待
LOGIN_FAIL_HARD = 10       # 起，固定锁 15 分钟
LOGIN_FAIL_RESET_S = 1800  # 这么久没再失败即清零，避免"攒了几周"式误锁
LOGIN_FAIL_KEYS = 512      # 内存上限：来源 IP 可变，不能让它无限增长


PASSWORD_RULE = "密码至少 8 位，且不能是纯数字或常见弱口令"
_WEAK = {"password", "12345678", "123456789", "1234567890", "qwertyui",
         "abcd1234", "admin123", "88888888", "11111111", "iloveyou"}


def strong_enough(password):
    return (len(password) >= 8 and not password.isdigit()
            and password.lower() not in _WEAK)


def login_attempt(user, ip):
    """预扣一次尝试额度；返回还需等待的秒数，0 = 放行去校验口令。"""
    key = (user, ip)
    now = time.time()
    with _login_fail_lock:
        entry = _login_fails.get(key)
        if entry and now - entry[2] > LOGIN_FAIL_RESET_S:
            entry = None
        if entry and entry[1] > now:
            # 锁定期内不再累加，否则持续敲门就能无限续期
            return int(entry[1] - now) + 1

        n = (entry[0] if entry else 0) + 1
        until = 0.0
        if n >= LOGIN_FAIL_HARD:
            until = now + 900
        elif n >= LOGIN_FAIL_SOFT:
            until = now + min(300, 2 ** (n - LOGIN_FAIL_SOFT + 1))
        _login_fails[key] = [n, until, now]

        if len(_login_fails) > LOGIN_FAIL_KEYS:
            oldest = sorted(_login_fails, key=lambda k: _login_fails[k][2])
            for k in oldest[:LOGIN_FAIL_KEYS // 4]:
                _login_fails.pop(k, None)
        return 0


def login_ok(user, ip):
    with _login_fail_lock:
        _login_fails.pop((user, ip), None)


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


# ---------------------------------------------------------- token 活跃度落盘
# seen 只是活跃度：每个认证请求都全量重写账户 JSON（还要抢全局 _lock，把互不
# 相关的账户和端点串起来），多客户端 30s 轮询下每分钟几十次落盘。改为 60s 节流
# ——ip/ver 是身份信息，一变就立刻落盘。内存里始终保留最新 seen，管理台视图用
# touch_seen 覆盖磁盘上的旧值，否则在线判定会滞后一个节流周期。
TOUCH_FLUSH_S = 60
TOUCH_KEYS = 256           # 令牌会随登录轮换，内存表不能无限增长
_touch_lock = threading.Lock()
_touch_seen = {}           # token -> {"seen": 时刻串, "at": 上次落盘, "ip", "ver"}


def touch_seen(token):
    """内存里该令牌的最新 seen（可能比磁盘新），没有则 None。"""
    with _touch_lock:
        entry = _touch_seen.get(token)
        return entry["seen"] if entry else None


def touch_token(user, token, ip, ver):
    """Records activity on a token: last-seen, ip and client version.
    Returns True when the account file was actually rewritten.

    Reloads inside the lock on purpose: the doc the auth check read is a
    snapshot from before the lock, and saving that snapshot would silently
    undo any write (a settings PUT, a sync) that landed in between — the
    classic lost update, and exactly how the first /settings write vanished."""
    stamp = f"{datetime.now(CN):%F %T}"
    now = time.time()
    with _touch_lock:
        entry = _touch_seen.get(token)
        if (entry is not None and now - entry["at"] < TOUCH_FLUSH_S
                and (not ip or ip == entry["ip"])
                and (not ver or ver == entry["ver"])):
            entry["seen"] = stamp
            return False
        _touch_seen[token] = {
            "seen": stamp, "at": now,
            "ip": ip or (entry or {}).get("ip") or "",
            "ver": ver or (entry or {}).get("ver") or "",
        }
        if len(_touch_seen) > TOUCH_KEYS:
            oldest = sorted(_touch_seen, key=lambda k: _touch_seen[k]["at"])
            for k in oldest[:TOUCH_KEYS // 4]:
                _touch_seen.pop(k, None)

    with _lock:
        doc = load_account(user)
        if doc is None:
            return False
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
    return True


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


# ---------------------------------------------------------- retention principle
# 服务端数据永远保留（2026-09-01 定的原则）：客户端可以不保留，但用户要数据时必须
# 能从服务端拿到。工作集列表照旧截断保证读写快；被截掉的条目一律先落入只追加的
# 归档文件（data/archive/<kind>.jsonl，一行一条 JSON），永不删除。

ARCHIVE_DIR = os.path.join(DATA, "archive")


def archive_overflow(kind, entries):
    if not entries:
        return
    try:
        os.makedirs(ARCHIVE_DIR, exist_ok=True)
        with open(os.path.join(ARCHIVE_DIR, f"{kind}.jsonl"), "a") as f:
            for e in entries:
                f.write(json.dumps(e, ensure_ascii=False) + "\n")
    except Exception as e:  # noqa: BLE001 — archiving must never break the write path
        log(f"archive {kind} failed: {e}")


def cap_log(kind, owner, entries, keep):
    """Trim a per-account log list to its working-set size, archiving what
    falls off the end (tagged with the owner) instead of discarding it."""
    if len(entries) > keep:
        archive_overflow(kind, [{**e, "user": owner} for e in entries[:-keep]])
        return entries[-keep:]
    return entries


def prune(code):
    if RETAIN_DAYS <= 0:
        return
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
        mine = set()
        for group in doc.get("groups", []):
            for code in group.get("codes", []):
                code = str(code).upper()
                if CODE_RE.match(code):
                    mine.add(code)
        quota = sorted(mine)[:MAX_ACCOUNT_CODES]
        if len(quota) < len(mine):
            log(f"union: {name[:-5]} 超配额，只取 {len(quota)}/{len(mine)} 个合约")
        seen.update(quota)
    return sorted(seen)


def kr_codes():
    """KR codes across every account synced within the TTL, deduped. Korea has
    no queryable daily history anywhere we reach (EastMoney's period fields are
    broken there, Tencent has no KR klines in any form), so the server
    archives each session's close itself — see kr_sweep_tick."""
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
                if KR_CODE_RE.match(code):
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
    last = None
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
        except Exception as e:  # noqa: BLE001
            # 异常对象出了 except 块就被清掉，重试循环里必须自己留一份：没有
            # 类型和合约的日志，事后分不清限流(ConnectionReset/空响应)和网络故障。
            last = e
            time.sleep(2)
    log(f"fetch eastmoney {code} failed: {type(last).__name__}: {last}")
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
    last = None
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
        except Exception as e:  # noqa: BLE001
            last = e
            time.sleep(2)
    log(f"fetch tencent {code} failed: {type(last).__name__}: {last}")
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
    last = None
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
        except Exception as e:
            # 留住末次异常：ValueError("empty klines") 是限流，
            # ConnectionReset/timeout 是网络故障，日志里得能分开。
            last = e
            time.sleep(1.5)

    # EastMoney exhausted: Tencent fallback, converted to the EastMoney shape
    # the clients parse (the same chain every client carries itself — but the
    # proxy answering means one upstream hit still serves everyone). Tencent
    # covers SH/SZ/HK/US with history (US via exchange suffix); BJ/KR have none.
    body = kline_tencent(secid, klt, fqt, lmt)
    if body is not None:
        return body

    log(f"kline {secid} klt={klt} fqt={fqt} both sources failed: "
        f"{type(last).__name__}: {last}" + (" (serving stale)" if meta else ""))
    return meta["body"] if meta else None


def kline_tencent(secid, klt, fqt, lmt):
    """Tencent fqkline as an EastMoney-shaped kline body, or None. qfq daily/
    weekly/monthly only — the shapes the app actually requests."""
    span = {"101": "day", "102": "week", "103": "month"}.get(klt)
    if span is None or fqt != "1":
        return None

    market, _, symbol = secid.partition(".")
    if market == "1":
        api = "sh" + symbol
    elif market == "0":
        # EastMoney folds Beijing into market 0; BSE symbols start 4/8/9.
        api = ("bj" if symbol[:1] in ("4", "8", "9") else "sz") + symbol
    elif market == "116":
        api = "hk" + symbol
    elif market in ("105", "106", "107"):
        # This endpoint needs the exchange suffix for US (the quote endpoint
        # doesn't): without it Tencent returns a one-row stub, which the client
        # then stamped as the day's fetch and showed blank returns all session.
        api = "us" + symbol + {"105": ".OQ", "106": ".N", "107": ".A"}[market]
    else:
        return None

    url = ("https://web.ifzq.gtimg.cn/appstock/app/fqkline/get"
           f"?param={api.lower()},{span},,,{max(1, int(lmt))},qfq")
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer/1.0)",
        "Referer": "https://gu.qq.com/",
    })
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            doc = json.loads(r.read().decode("utf-8"))
        node = (doc.get("data") or {}).get(api.lower()) or {}
        rows = node.get("qfq" + span) or node.get(span) or []
        # Tencent rows: [date, open, close, high, low, volume]; EastMoney kline
        # strings: "date,open,close,high,low,volume,amount".
        klines = [",".join([str(x) for x in row[:6]] + ["0"])
                  for row in rows if isinstance(row, list) and len(row) >= 6]
        # Fewer than two rows is a stub, not history — serving it is worse than
        # serving nothing (the client caches it as the day's answer).
        if len(klines) < 2:
            return None
        log(f"kline fallback tencent {api} {span} rows={len(klines)}")
        return json.dumps({"data": {"code": symbol, "klines": klines}})
    except Exception as e:
        log(f"kline tencent {api} {span} failed: {type(e).__name__}: {e}")
        return None


# ------------------------------------------------------------- KR daily closes

KR_DAILY = os.path.join(DATA, "kr-daily.json")


def kr_load():
    try:
        with open(KR_DAILY) as f:
            return json.load(f)
    except Exception:
        return {}


def kr_sweep_tick():
    """Self-maintained daily-close archive for Korea, one batched Tencent
    quote per day after the KR close (15:30 KST = 14:30 CST). Each record keeps
    the session's own date (taken from the QUOTE's timestamp, so a KR holiday
    re-reads the old session and just overwrites the same entry), its close and
    its previous close — one day of archive already yields one 昨日涨幅 pair.
    Clients read it back via GET /krdaily."""
    # Safe whenever the KR session (09:00-15:30 KST = 08:00-14:30 CST) is NOT
    # live: the record's date comes from the QUOTE's own timestamp and the
    # archive dedups by it, so an off-hours pass always lands on the last
    # completed session — a small-hours catch-up included. Two passes a day:
    # one after the close, one early-morning (catches a server that was down
    # or deployed after the close).
    now = datetime.now(CN)
    live = datetime.strptime("07:50", "%H:%M").time() <= now.time() \
        < datetime.strptime("15:01", "%H:%M").time()
    if live:
        return

    mark = f"{now:%F}-" + ("pm" if now.time() >= datetime.strptime("15:01", "%H:%M").time() else "am")
    state = load_state()
    if state.get("kr_day") == mark:
        return

    codes = kr_codes()
    if not codes:
        with _lock:
            state = load_state()
            state["kr_day"] = mark
            save_state(state)
        return

    url = "https://qt.gtimg.cn/q=" + ",".join(c.lower() for c in codes[:400])
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer/1.0)",
        "Referer": "https://gu.qq.com/",
    })
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            body = r.read().decode("gbk", errors="replace")
    except Exception as e:  # noqa: BLE001
        log(f"kr sweep: quote batch failed: {e}")
        return

    doc = kr_load()
    added = 0
    for seg in body.split(";"):
        seg = seg.strip()
        if not seg.startswith("v_"):
            continue
        api, _, val = seg[2:].partition("=")
        f = val.strip().strip('"').split("~")
        if len(f) < 33:
            continue
        code = api.upper()
        try:
            close = float(f[3])
            prev = float(f[4])
            pct = float(f[32])
        except ValueError:
            continue
        # KR timestamps come dashed ("2026-08-31 14:30:03"); the date names the
        # session this quote settles, holiday-proof by construction.
        day = f[30][:10]
        if close <= 0 or not re.match(r"^\d{4}-\d{2}-\d{2}$", day):
            continue
        records = [r for r in doc.get(code, []) if r.get("date") != day]
        records.append({"date": day, "close": close, "prev": prev, "pct": pct})
        records.sort(key=lambda r: r["date"])
        doc[code] = records   # 永久保留（服务端数据不丢原则）
        added += 1

    if added:
        tmp = KR_DAILY + ".tmp"
        with open(tmp, "w") as fo:
            json.dump(doc, fo)
        os.replace(tmp, KR_DAILY)
    with _lock:
        state = load_state()
        state["kr_day"] = mark
        save_state(state)
    log(f"kr sweep: archived {added}/{len(codes)} closes")


# 上证指数永不停牌，用它判"今天到底开没开市"。此前是拿抓取列表里第一只返回旧日期的
# 合约下结论——一只停牌股、或源端一次串日，就把整个交易日标成节假日、放弃全部归档，
# 数据永久丢失（违反"服务端数据永远保留"铁律）。
HOLIDAY_PROBE = "SH000001"


def sweep_once():
    """One throttled pass over whatever is missing for today. Returns idle time hint."""
    now = datetime.now(CN)
    # One minute after the SH/SZ bell: the closing auction settles at 15:00 and
    # the minute feeds carry the full day right away — same-day history should
    # be queryable while the day is still fresh, not at 15:20.
    if now.weekday() >= 5 or now.time() < datetime.strptime("15:01", "%H:%M").time():
        return
    day = f"{now:%F}"

    if load_state().get("holiday") == day:
        return

    all_codes = union_codes()
    missing = [c for c in all_codes if not os.path.exists(trend_path(c, day))]
    if not missing:
        enrich_summaries(day, all_codes)
        return

    # 节假日判定只认指数探针的会话日期。探针拿不到就照常抓——宁可多抓一轮，
    # 也不能因为一次网络抖动把当天判成休市。
    probe, probe_day = fetch_trend(HOLIDAY_PROBE)
    if probe is not None and probe_day != day:
        with _lock:
            st = load_state()
            st["holiday"] = day
            save_state(st)
        log(f"sweep {day}: index probe reports {probe_day} -> holiday, aborting")
        return
    if probe is None:
        log(f"sweep {day}: holiday probe unreachable, sweeping anyway")

    log(f"sweep {day}: {len(missing)} contracts to fetch")
    done = failed = stale = streak = 0
    for code in missing:
        series, data_day = fetch_trend(code)
        if series is not None and data_day == day:
            streak = 0
            os.makedirs(trend_dir(code), exist_ok=True)
            tmp = trend_path(code, day) + ".tmp"
            with open(tmp, "w") as f:
                json.dump(series, f, ensure_ascii=False)
            os.replace(tmp, trend_path(code, day))
            prune(code)
            done += 1
        else:
            # 单只合约的旧日期只是这一只的问题（停牌/源端串日），不再是对
            # 整个交易日的判决——它和抓取失败一样，等下一轮重试。
            failed += 1
            if series is not None:
                stale += 1
            streak += 1
            # Both sources down N times in a row = we're being throttled.
            # Continuing just feeds the throttle; the next 5-minute tick
            # resumes from wherever this stopped (file existence = done).
            if streak >= 5:
                log(f"sweep {day}: {streak} consecutive failures, backing off "
                    f"(done={done} failed={failed} stale={stale})")
                return
        time.sleep(FETCH_GAP_S)

    # 锁内重读再改自有键：这里的 state 快照可能已是几分钟前的，整体回写会把
    # 期间别的线程写入的 websessions/kr_day 一并抹掉（丢更新）。
    with _lock:
        st = load_state()
        st["last_sweep"] = {"day": day, "done": done, "failed": failed, "stale": stale,
                            "at": f"{datetime.now(CN):%F %T}"}
        save_state(st)
    log(f"sweep {day}: done={done} failed={failed} stale={stale}")
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


# ---------------------------------------------------------------- 资讯
# Per-account watch feed: the union of every account's A-share contracts is the
# fetch universe; each account's own GROUPS are its "关注板块" — /news assembles
# the pool back along that structure, so no external industry mapping is needed.
# Tone (利多/利空) is keyword-scored — deterministic and honest about being a
# heuristic; the deep analysis stays in the daily brief.

NEWS_POOL_PATH = os.path.join(DATA, "news-pool.json")
NEWS_TTL_S = 3600          # refresh each code at most hourly
NEWS_BATCH = 30            # codes per scheduler pass, spreads the load
NEWS_RETAIN_S = 7 * 86400
_news_lock = threading.Lock()

TONE_GOOD = ("预增", "增持", "回购", "中标", "斩获", "突破", "增长", "上调", "扭亏",
             "分红", "新高", "合作", "签署", "订单", "涨价", "超预期", "获批", "专利",
             "扩产", "创新高", "大涨", "净利增", "业绩增")
TONE_BAD = ("预减", "减持", "下调", "亏损", "立案", "调查", "诉讼", "处罚", "警示",
            "解禁", "质押", "退市", "下滑", "违规", "终止", "减产", "召回", "缺陷",
            "大跌", "净利降", "业绩降", "商誉减值")


def news_tone(text):
    good = sum(1 for w in TONE_GOOD if w in text)
    bad = sum(1 for w in TONE_BAD if w in text)
    return "利多" if good > bad else "利空" if bad > good else "中性"


def load_news_pool():
    try:
        with open(NEWS_POOL_PATH, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_news_pool(pool):
    tmp = NEWS_POOL_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(pool, f, ensure_ascii=False)
    os.replace(tmp, NEWS_POOL_PATH)


def news_universe():
    """code -> secid for every A-share contract in ANY account's groups."""
    out = {}
    if not os.path.isdir(ACCOUNTS):
        return out
    for name in os.listdir(ACCOUNTS):
        if not name.endswith(".json"):
            continue
        doc = load_account(name[:-5])
        mine = {}
        for g in (doc or {}).get("groups") or []:
            for code in g.get("codes") or []:
                code = str(code).upper()
                if code.startswith("SH"):
                    mine[code] = "1." + code[2:]
                elif code.startswith(("SZ", "BJ")):
                    mine[code] = "0." + code[2:]
        # 同 union_codes：单账户的抓取配额（超出的截断只影响资讯，不动分组本身）
        for code in sorted(mine)[:MAX_ACCOUNT_CODES]:
            out[code] = mine[code]
    return out


def _fetch_json(url):
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; QuoteViewServer)"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        return json.loads(resp.read().decode("utf-8", "replace"))


def fetch_announcements(code):
    """EastMoney per-stock announcements; 业绩类 recognised from the column."""
    code6 = code[2:]
    doc = _fetch_json(
        "https://np-anotice-stock.eastmoney.com/api/security/ann?sr=-1&page_size=8"
        f"&page_index=1&ann_type=A&client_source=web&stock_list={code6}")
    items = []
    for a in (doc.get("data") or {}).get("list") or []:
        title = str(a.get("title") or "")[:160]
        if not title:
            continue
        columns = " ".join(c.get("column_name") or "" for c in a.get("columns") or [])
        kind = "业绩" if any(w in columns + title
                             for w in ("业绩", "年度报告", "季度报告", "半年度报告",
                                       "利润分配", "业绩预告", "业绩快报")) else "公告"
        name = ""
        for c in a.get("codes") or []:
            if c.get("stock_code") == code6:
                name = c.get("short_name") or ""
        when = str(a.get("notice_date") or "")[:10]
        items.append({
            "id": "ann-" + str(a.get("art_code") or title)[:40],
            "code": code, "name": name, "time": when + " 00:00",
            "title": title, "kind": kind, "tone": news_tone(title),
            "url": f"https://data.eastmoney.com/notices/detail/{code6}/"
                   f"{a.get('art_code')}.html",
        })
    return items


def fetch_stock_news(code, secid):
    doc = _fetch_json(
        "https://np-listapi.eastmoney.com/comm/wap/getListInfo?client=wap&type=1"
        f"&mTypeAndCode={secid}&pageSize=8&pageIndex=1")
    items = []
    for a in (doc.get("data") or {}).get("list") or []:
        title = str(a.get("Art_Title") or "")[:160]
        if not title:
            continue
        items.append({
            "id": "news-" + str(a.get("Art_Code") or title)[:40],
            "code": code, "name": "", "time": str(a.get("Art_ShowTime") or "")[:16],
            "title": title, "kind": "新闻", "tone": news_tone(title),
            "url": str(a.get("Art_Url") or ""),
        })
    return items


def ws_broadcast(obj):
    payload = json.dumps(obj, ensure_ascii=False).encode()
    with _ws_lock:
        conns = list(WS_CONNS.items())
    for token, conn in conns:
        try:
            ws_send_conn(conn, 1, payload)
        except Exception:
            # 半死的对端（缓冲满不收包）会一路吃满 socket 的 20s 超时，把 news
            # 调度线程按连接串行拖住。就地摘掉并关 socket：handler 线程的读随即
            # 报错走它自己的 finally，重复 pop 无害。
            with _ws_lock:
                if WS_CONNS.get(token) is conn:
                    WS_CONNS.pop(token, None)
            try:
                sock = conn.get("sock")
                if sock is not None:
                    sock.close()
            except Exception:
                pass


def news_sweep_tick():
    """One throttled batch over stale codes; runs from the scheduler loop."""
    now = datetime.now(CN)
    if not 8 <= now.hour <= 23:
        return

    universe = news_universe()
    if not universe:
        return

    with _news_lock:
        pool = load_news_pool()
    ts = time.time()
    stale = [c for c in sorted(universe)
             if ts - (pool.get(c) or {}).get("fetched", 0) > NEWS_TTL_S][:NEWS_BATCH]
    if not stale:
        return

    added = 0
    for code in stale:
        items = []
        for fetch in (lambda: fetch_announcements(code),
                      lambda: fetch_stock_news(code, universe[code])):
            try:
                items.extend(fetch())
            except Exception:
                pass   # per-source failure: keep what the other source gave
            time.sleep(FETCH_GAP_S)

        entry = pool.get(code) or {"items": []}
        known = {i["id"] for i in entry["items"]}
        fresh = [i for i in items if i["id"] not in known]
        added += len(fresh)
        merged = fresh + entry["items"]
        cutoff = f"{datetime.now(CN) - timedelta(seconds=NEWS_RETAIN_S):%F %H:%M}"
        kept = [i for i in merged if (i.get("time") or "") >= cutoff][:40]
        archive_overflow("news", [{**i, "code": code}
                                  for i in merged if i not in kept])
        pool[code] = {"fetched": ts, "items": kept}

    # Contracts nobody watches any more age out of the pool — their items go
    # to the archive first (the retention principle).
    for code in [c for c in pool if c not in universe]:
        archive_overflow("news", [{**i, "code": code}
                                  for i in (pool[code].get("items") or [])])
        del pool[code]

    with _news_lock:
        save_news_pool(pool)
    if added:
        log(f"news sweep: {len(stale)} codes, +{added} items")
        ws_broadcast({"news": added})


SCHED_BEAT = {}


def _beat(name):
    """Watchdog trail: each engine stamps its last completed pass. A stamp
    older than an hour means that engine is HUNG (not merely erroring — errors
    still stamp), which is otherwise invisible from outside the process."""
    SCHED_BEAT[name] = time.time()
    for other, at in SCHED_BEAT.items():
        if other != name and time.time() - at > 3600:
            log(f"WATCHDOG: engine '{other}' silent for {int(time.time() - at)}s")


def scheduler():
    """Trend archiving + KR closes. News runs on ITS OWN thread (see
    news_scheduler): an hour-long news crawl or a hung fetch there must never
    delay the 15:01 archive promise — the engines only share the log."""
    while True:
        try:
            sweep_once()
        except Exception as e:  # noqa: BLE001 - the loop must survive anything
            log(f"sweep error: {e}")
        try:
            kr_sweep_tick()
        except Exception as e:  # noqa: BLE001
            log(f"kr sweep error: {e}")
        _beat("archive")
        # Wake AT the archive minute: a flat 300s cadence could push the first
        # after-close pass to ~15:06, defeating the 15:01 promise.
        now = datetime.now(CN)
        target = now.replace(hour=15, minute=1, second=0, microsecond=0)
        if now.weekday() < 5 and now < target:
            time.sleep(min(300, max(1, (target - now).total_seconds())))
        else:
            time.sleep(300)


def news_scheduler():
    while True:
        try:
            news_sweep_tick()
        except Exception as e:  # noqa: BLE001
            log(f"news sweep error: {e}")
        _beat("news")
        time.sleep(300)


# ---------------------------------------------------------------- http

WEB_SESSION_IDLE_H = 12


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

def ws_read_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def ws_read_frame(sock):
    """One frame: (opcode, unmasked payload), or (None, None) on EOF."""
    hdr = ws_read_exact(sock, 2)
    if hdr is None:
        return None, None
    b1, b2 = hdr
    opcode = b1 & 0x0F
    masked = b2 & 0x80
    length = b2 & 0x7F
    if length == 126:
        ext = ws_read_exact(sock, 2)
        if ext is None:
            return None, None
        length = struct.unpack(">H", ext)[0]
    elif length == 127:
        ext = ws_read_exact(sock, 8)
        if ext is None:
            return None, None
        length = struct.unpack(">Q", ext)[0]
    if length > 65536:
        return 8, b""              # oversized on a control channel = go away
    mask = ws_read_exact(sock, 4) if masked else b""
    payload = ws_read_exact(sock, length) if length else b""
    if payload is None:
        return None, None
    if masked:
        payload = bytes(c ^ mask[i % 4] for i, c in enumerate(payload))
    return opcode, payload


def ws_send(sock, opcode, payload=b""):
    hdr = bytes([0x80 | opcode])
    n = len(payload)
    if n < 126:
        hdr += bytes([n])
    elif n < 65536:
        hdr += bytes([126]) + struct.pack(">H", n)
    else:
        hdr += bytes([127]) + struct.pack(">Q", n)
    sock.sendall(hdr + payload)


def ws_send_conn(conn, opcode, payload=b""):
    """Same frame, serialized per connection: the news thread broadcasts while
    the connection's own handler thread answers pings, and two sendall calls on
    one socket can interleave their bytes on a partial write — which corrupts
    that client's whole stream, not just the frame."""
    sock = conn.get("sock")
    if sock is None:
        return
    with conn["lock"]:
        ws_send(sock, opcode, payload)


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
      <input id=np placeholder="初始密码（≥8 位，非纯数字）" type=password style="width:200px">
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
    <div class=card><h2>账户状态与在线会话（在线 = 登录状态；活跃 = 长连接在线，断开即时感知；旧版客户端按心跳 3 分钟判定）
        <button id=rs class=icon title="刷新" onclick="spinReload('rs', loadSessions)"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.5v3h-3" stroke-linecap="round"
                  stroke-linejoin="round"/></svg></button></h2>
      <div id=sess-list>加载中…</div></div>
  </section>

  <section id=tab-logs hidden>
    <div class=card>
      <h2>登录日志　
        <select id=ll onchange="filterChanged('login')">
          <option value="">全部级别</option>
          <option value=info>INFO</option>
          <option value=warn>WARN</option>
          <option value=error>ERROR</option>
        </select>
        <input id=lf placeholder="按用户/IP/日期/事件过滤…" style="width:220px"
          oninput="filterChanged('login')">
        <button id=rl class=icon title="刷新日志" onclick="spinReload('rl', loadLogs)">
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.5v3h-3" stroke-linecap="round"
                  stroke-linejoin="round"/></svg>
        </button>
      </h2>
      <div id=login-list>加载中…</div>
    </div>
    <div class=card>
      <h2>配置修改日志（设置 / 分组推送，只记实际变更）　
        <select id=ck onchange="filterChanged('chg')">
          <option value="">全部类型</option>
          <option value=设置>设置</option>
          <option value=分组>分组</option>
        </select>
        <input id=cf placeholder="按用户/IP/日期/内容过滤…" style="width:220px"
          oninput="filterChanged('chg')">
        <button id=rc class=icon title="刷新日志" onclick="spinReload('rc', loadLogs)">
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6">
            <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 1.5v3h-3" stroke-linecap="round"
                  stroke-linejoin="round"/></svg>
        </button>
      </h2>
      <div id=chg-list>加载中…</div>
    </div>
    <div class=grid2>
      <div class=card><h2>登录统计（按用户汇总）</h2><div id=login-stats></div></div>
      <div class=card><h2>客户端异常
        <span class=dim style="font-size:12px;font-weight:normal">（客户端未处理异常自动上报，同类 10 分钟内只报一次）</span></h2>
        <div id=fault-list></div></div>
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
      ? `<select onchange="act('role',{username:'${esc(a.username)}',role:this.value})">
           <option value=user ${a.role==='user'?'selected':''}>普通用户</option>
           <option value=admin ${a.role==='admin'?'selected':''}>管理员</option></select>`
      : `<span class="tag t-role">${esc(ROLE[a.role]||a.role)}</span>`;
    return `<tr>
      <td><b>${esc(a.username)}</b></td>
      <td>${roleCell}</td>
      <td>${a.disabled ? '<span class="tag t-bad">已禁用</span>' : '<span class="tag t-on">正常</span>'}</td>
      <td class=mut>${a.groups} 组 / ${a.contracts} 合约</td>
      <td class=mut>${a.has_settings ? '已同步' : '<span class=dim>无</span>'}</td>
      <td class=mut>${a.online > 0 ? `<span class="tag t-on">${a.online} 在线</span>` : '<span class=dim>—</span>'}</td>
      <td>
        ${canPw ? `<button class=op onclick="passwd('${esc(a.username)}')">改密码</button>` : ''}
        ${canAct ? `<button class=op onclick="act('logout',{username:'${esc(a.username)}'})">登出</button>
        <button class=op onclick="act('disable',{username:'${esc(a.username)}',disabled:${!a.disabled}})">${a.disabled?'启用':'禁用'}</button>
        <button class="op danger" onclick="del('${esc(a.username)}')">删除</button>` : ''}
        ${!canAct && !canPw ? '<span class=dim>无权限</span>' : ''}
      </td></tr>`;
  }).join('');
  $('acct-list').innerHTML = `<table><tr><th>账户</th><th>角色</th><th>状态</th>
    <th>数据</th><th>设置同步</th><th>会话</th><th>操作</th></tr>${rows}</table>`;
}

async function loadSessions(){
  const d = await api('sessions');
  const blocks = d.accounts.map(a => {
    const act = a.online.filter(t => t.active).length;
    const state = a.disabled ? '<span class="tag t-bad">已禁用</span>'
      : a.online.length
        ? `<span class="tag t-on">在线 ${a.online.length}</span> <span class="tag ${act ? 't-on' : 't-off'}">活跃 ${act}</span>`
        : '<span class="tag t-off">离线</span>';
    const rows = a.online.map(t => `<tr>
      <td>${t.active ? '<span class="tag t-on">活跃</span>' : '<span class="tag t-off">挂起</span>'}</td>
      <td class=mono>${esc(t.ip)}</td><td class=mono>${esc(t.ver)}</td>
      <td class=mono>${esc(t.created)}</td><td class=mono>${esc(t.seen)}</td>
      <td class=mono>${esc(t.duration||'-')}</td></tr>`).join('');
    const table = a.online.length
      ? `<table><tr><th>状态</th><th>IP</th><th>客户端版本</th><th>登录时间</th><th>最近活动</th><th>登录时长</th></tr>${rows}</table>`
      : '<div class=dim style="padding:4px 12px">无在线会话</div>';
    return `<div style="margin-bottom:14px">
      <div style="margin-bottom:4px"><b>${esc(a.username)}</b>
        <span class="tag t-role">${esc(ROLE[a.role]||a.role)}</span> ${state}</div>${table}</div>`;
  }).join('');
  $('sess-list').innerHTML = blocks || '<div class=dim>无账户</div>';
}

async function spinReload(btnId, loader){
  const b = $(btnId); b.classList.add('spin');
  try { await loader(); } finally { setTimeout(() => b.classList.remove('spin'), 300); }
}
// Fixed rows + pagination for every log list: page size from a dropdown,
// prev/next, page resets when the filter changes. The lists used to render
// everything and just grow.
// 一处兜住全部用户可控插值。分组名、备注、异常详情等都出自客户端，直插
// innerHTML 曾让任意注册用户能把脚本存进管理员的页面（存储型 XSS）。
function esc(v){
  return String(v == null ? '' : v).replace(/[&<>"']/g,
    c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

const PAGES = { login: {page:0, size:50}, chg: {page:0, size:50}, pw: {page:0, size:50}, fault: {page:0, size:20} };

function pager(key, total){
  const st = PAGES[key];
  const pages = Math.max(1, Math.ceil(total / st.size));
  if (st.page >= pages) st.page = pages - 1;
  const opts = [20, 50, 100, 200].map(n =>
    `<option value=${n} ${n === st.size ? 'selected' : ''}>${n} 行/页</option>`).join('');
  return `<div style="display:flex;align-items:center;gap:10px;margin-top:8px">
    <select onchange="setPageSize('${key}', this.value)">${opts}</select>
    <button class=op ${st.page === 0 ? 'disabled' : ''}
            onclick="setPage('${key}', ${st.page - 1})">上一页</button>
    <span class=dim>第 ${st.page + 1} / ${pages} 页 · 共 ${total} 条</span>
    <button class=op ${st.page >= pages - 1 ? 'disabled' : ''}
            onclick="setPage('${key}', ${st.page + 1})">下一页</button>
  </div>`;
}
function pageSlice(key, rows){
  const st = PAGES[key];
  return rows.slice(st.page * st.size, (st.page + 1) * st.size);
}
function setPage(key, p){ PAGES[key].page = Math.max(0, p); rerenderLog(key); }
function setPageSize(key, n){ PAGES[key].size = +n; PAGES[key].page = 0; rerenderLog(key); }
function rerenderLog(key){
  if (key === 'login') renderLogs();
  else if (key === 'chg') renderChanges();
  else if (key === 'fault') renderFaults();
  else renderPw();
}
function renderFaults(){
  if (!LOGS) return;
  const all = LOGS.faults || [];
  const rows = pageSlice('fault', all).map(l => `<tr class=err>
      <td class=mono>${esc(l.at)}</td><td><b>${esc(l.user)}</b></td>
      <td><span class="tag t-bad">${esc(l.kind)}</span></td>
      <td style="max-width:640px;word-break:break-all;font-size:11px" class=mono>${
        esc(l.detail)}</td>
      <td class=mono>${esc(l.ip||'-')}</td><td class=mono>${esc(l.ver||'-')}</td></tr>`).join('');
  $('fault-list').innerHTML = all.length
    ? `<table><tr><th>时间</th><th>用户</th><th>类型</th><th>异常详情</th><th>IP</th><th>客户端版本</th></tr>${rows}</table>`
      + pager('fault', all.length)
    : '<div class=dim>无异常上报——两台机器都健康</div>';
}
function filterChanged(key){ PAGES[key].page = 0; rerenderLog(key); }

async function loadLogs(){
  LOGS = await api('logs');
  renderLogs();
  renderChanges();
  renderPw();
  renderFaults();
  const stats = {};
  for (const l of LOGS.logins){
    if ((l.level || 'info') !== 'info') continue;   // stats = successful logins
    const st = stats[l.user] = stats[l.user] || {n:0, last:'', ips:new Set()};
    st.n++; if (l.at > st.last) st.last = l.at; if (l.ip) st.ips.add(l.ip);
  }
  $('login-stats').innerHTML = `<table><tr><th>用户</th><th>登录次数</th>
    <th>独立 IP（去重）</th><th>最近登录</th></tr>` +
    Object.entries(stats).sort((a,b) => b[1].n - a[1].n).map(([u,st]) =>
      `<tr><td><b>${esc(u)}</b></td><td>${st.n}</td>
       <td>${st.ips.size}<div class="dim mono" style="font-size:11px">${
         [...st.ips].map(esc).join('<br>')}</div></td>
       <td class=mono>${esc(st.last)}</td></tr>`).join('') + '</table>';
}
function renderPw(){
  if (!LOGS) return;
  const all = LOGS.passwords || [];
  const rows = pageSlice('pw', all).map(l =>
    `<tr><td><b>${esc(l.user)}</b></td><td class=mono>${esc(l.at)}</td>
     <td class=mono>${esc(l.ip||'-')}</td><td>${l.by==='self'?'本人':esc(l.by)}</td></tr>`).join('');
  $('pw-list').innerHTML = all.length
    ? `<table><tr><th>用户</th><th>时间</th><th>IP</th><th>操作者</th></tr>${rows}</table>`
      + pager('pw', all.length)
    : '<div class=dim>无记录</div>';
}
function renderChanges(){
  if (!LOGS) return;
  const f = $('cf').value.trim().toLowerCase();
  const k = $('ck').value;
  const all = (LOGS.changes || []).filter(l =>
    (!k || l.kind === k) &&
    (!f || l.user.toLowerCase().includes(f) || l.ip.includes(f) || l.at.includes(f)
        || (l.detail||'').toLowerCase().includes(f)));
  const rows = pageSlice('chg', all).map(l => `<tr>
      <td class=mono>${esc(l.at)}</td><td><b>${esc(l.user)}</b></td>
      <td><span class="tag t-role">${esc(l.kind)}</span></td>
      <td style="max-width:520px;word-break:break-all">${esc(l.detail)}</td>
      <td class=mono>${esc(l.ip||'-')}</td><td class=mono>${esc(l.ver||'-')}</td></tr>`).join('');
  $('chg-list').innerHTML = all.length
    ? `<table><tr><th>时间</th><th>用户</th><th>类型</th><th>变更内容</th><th>IP</th><th>客户端版本</th></tr>${rows}</table>`
      + pager('chg', all.length)
    : '<div class=dim>无记录</div>';
}
function renderLogs(){
  if (!LOGS) return;
  const f = $('lf').value.trim().toLowerCase();
  const lv = $('ll').value;
  const all = LOGS.logins.filter(l =>
    (!lv || (l.level||'info') === lv) &&
    (!f || l.user.toLowerCase().includes(f) || l.ip.includes(f) || l.at.includes(f)
        || (l.event||'').toLowerCase().includes(f)));
  const rows = pageSlice('login', all).map(l => {
      const level = l.level || 'info';
      return `<tr${level === 'error' ? ' class=err' : ''}>
        <td><span class="lv lv-${esc(level)}">${esc(level.toUpperCase())}</span></td>
        <td><b>${esc(l.user)}</b></td><td>${esc(l.event||'登录成功')}</td>
        <td class=mono>${esc(l.at)}</td><td class=mono>${esc(l.ip||'-')}</td>
        <td class=mono>${esc(l.ver||'-')}</td></tr>`;
    }).join('');
  $('login-list').innerHTML = all.length
    ? `<table><tr><th>级别</th><th>用户</th><th>事件</th><th>时间</th><th>IP</th><th>客户端版本</th></tr>${rows}</table>`
      + pager('login', all.length)
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
    protocol_version = "HTTP/1.1"   # Upgrade (WebSocket) requires 1.1 responses

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
        # X-Forwarded-For (first hop). Forwarding headers are only believed when
        # the SOCKET PEER is that loopback proxy — a direct connection to
        # 0.0.0.0:8388 can set any header it likes, and audit rows (and the
        # admin page they render into) must not be forgeable. Every candidate
        # is parsed as a real IP, which also closes the injection path into the
        # console's HTML.
        peer = self.client_address[0]
        if peer not in ("127.0.0.1", "::1"):
            return peer

        forwarded = (self.headers.get("X-Forwarded-For") or "").split(",")[0].strip()
        real = (self.headers.get("X-Real-IP") or "").strip()
        for candidate in (forwarded, real):
            if candidate and candidate not in ("127.0.0.1", "::1") and _valid_ip(candidate):
                return candidate
        return peer

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
        entry = next((t for t in doc.get("tokens") or []
                      if isinstance(t, dict) and t.get("t") == token), None)
        if entry is not None and entry.get("kicked"):
            self._bad("kicked", 401)
            return None
        if touch_token(user, token, self._ip(), self._ver()):
            # Re-read only when the touch actually wrote: that write merged in
            # whatever landed since our snapshot. A throttled touch changes
            # nothing on disk, so the snapshot is still the freshest copy.
            doc = load_account(user) or doc
        return user, doc, token

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

        if url.path == "/web":
            # Redirect to the slashed form: the SPA fetches 'api/…' RELATIVE to
            # the page URL, and only /web/ makes that resolve to /web/api/….
            self.send_response(301)
            self.send_header("Location", "/web/")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        if url.path == "/web/":
            # The page itself is public; it decides login-vs-console by asking
            # /web/api/me with its stored token.
            body = ADMIN_PAGE.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            # 纵深防御：即使某天漏了一处转义，CSP 也不让注入的脚本跑起来。
            # 页面自带内联 <style>/<script>，故 script/style 允许 'unsafe-inline'
            # 但不允许任何外部源，且禁止内联事件之外的外链与对象。
            self.send_header(
                "Content-Security-Policy",
                "default-src 'none'; script-src 'unsafe-inline'; "
                "style-src 'unsafe-inline'; img-src data:; connect-src 'self'; "
                "form-action 'none'; base-uri 'none'; frame-ancestors 'none'")
            self.send_header("X-Content-Type-Options", "nosniff")
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
                    if t.get("kicked"):
                        continue   # force-logged-out; kept only for the reason
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
                    # A live long connection is the definitive answer; the
                    # 3-minute heartbeat window only covers older clients that
                    # don't hold one yet.
                    active = t["t"] in WS_CONNS
                    # 磁盘上的 seen 最多滞后一个落盘节流周期（TOUCH_FLUSH_S），
                    # 内存里的才是最新的。
                    seen = touch_seen(t["t"]) or t.get("seen") or ""
                    if not active:
                        try:
                            seen_dt = datetime.strptime(seen,
                                                        "%Y-%m-%d %H:%M:%S").replace(tzinfo=CN)
                            active = (now - seen_dt) <= timedelta(minutes=3)
                        except Exception:
                            pass
                    sessions.append({"ip": ip, "ver": t.get("ver") or "-",
                                     "created": created, "seen": seen or "-",
                                     "duration": duration, "active": active})
                out.append({"username": name[:-5], "role": role_of(doc),
                            "disabled": bool(doc.get("disabled")), "online": sessions})
            return self._json({"accounts": out})

        if url.path == "/web/api/logs":
            if self._admin() is None:
                return
            logins, passwords, changes, faults = [], [], [], []
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
                for entry in doc.get("cfglogs") or []:
                    changes.append({"user": user, "at": entry.get("at") or "",
                                    "ip": entry.get("ip") or "", "ver": entry.get("ver") or "",
                                    "kind": entry.get("kind") or "",
                                    "detail": entry.get("detail") or ""})
                for entry in doc.get("faultlogs") or []:
                    faults.append({"user": user, "at": entry.get("at") or "",
                                   "ip": entry.get("ip") or "", "ver": entry.get("ver") or "",
                                   "kind": entry.get("kind") or "",
                                   "detail": entry.get("detail") or ""})
            logins.sort(key=lambda x: x["at"], reverse=True)
            faults.sort(key=lambda x: x["at"], reverse=True)
            passwords.sort(key=lambda x: x["at"], reverse=True)
            changes.sort(key=lambda x: x["at"], reverse=True)
            # Pagination lives client-side now, so the merged caps are only a
            # payload guard, not a display limit.
            return self._json({"logins": logins[:1000], "passwords": passwords[:500],
                               "changes": changes[:1000], "faults": faults[:500]})

        if url.path == "/web/api/accounts":
            actor = self._admin()
            if actor is None:
                return
            return self._json({"me": {"username": actor[0], "role": actor[1]},
                               "accounts": self._account_summaries()})

        if url.path == "/ws":
            return self._ws()

        if url.path == "/news":
            authed = self._auth()
            if authed is None:
                return
            _, doc, _ = authed
            with _news_lock:
                pool = load_news_pool()
            groups_out = []
            for g in doc.get("groups") or []:
                rows = []
                for code in g.get("codes") or []:
                    rows.extend((pool.get(str(code).upper()) or {}).get("items") or [])
                rows.sort(key=lambda i: i.get("time") or "", reverse=True)
                if rows:
                    groups_out.append({"name": g.get("name") or "", "items": rows[:40]})
            return self._json({"groups": groups_out,
                               "updated": f"{datetime.now(CN):%F %T}"})

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

        if url.path == "/krdaily":
            if self._auth() is None:
                return
            code = (q.get("code") or [""])[0].upper()
            if not KR_CODE_RE.match(code):
                return self._bad("bad code")
            records = kr_load().get(code) or []
            candles = []
            # The oldest record's own previous close is a free extra point —
            # it makes 昨日涨幅 computable from a single archived day.
            if records and records[0].get("prev", 0) > 0:
                candles.append({"date": "", "close": records[0]["prev"]})
            for r in records:
                candles.append({"date": r.get("date", ""), "close": r.get("close", 0)})
            return self._json({"candles": candles[-270:]})

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
            # 无鉴权端点，只回一条健康信号（归档调度还在不在跑）。账户数、合约
            # 并集这些业务数据在有鉴权的管理台看，不从公网裸奔。
            return self._json({"ok": True,
                               "last_sweep": load_state().get("last_sweep")})

        self._bad("not found", 404)

    # ------------------------------------------------------------ websocket

    def _ws(self):
        """The persistent presence channel. Handshake, then an auth frame, then
        the connection IS the online signal: registered while open, dropped the
        instant the peer closes (FIN/RST included). Client keepalive pings are
        answered here; 20s of silence counts as a dead peer."""
        key = self.headers.get("Sec-WebSocket-Key")
        if (self.headers.get("Upgrade") or "").lower() != "websocket" or not key:
            return self._bad("not a websocket request")

        accept = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
        self.send_response(101, "Switching Protocols")
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        self.wfile.flush()

        sock = self.connection
        # Clients ping every 5s; four misses = dead peer. This is also the
        # silent-death (cable pull / power loss) detection bound.
        sock.settimeout(20)
        self.close_connection = True

        token = None
        user = None
        try:
            # First frame must be the auth message.
            opcode, payload = ws_read_frame(sock)
            if opcode != 1:
                return
            try:
                doc = json.loads(payload.decode())
            except Exception:
                return
            token = str(doc.get("token") or "")
            ver = re.sub(r"[^A-Za-z0-9._-]", "", str(doc.get("ver") or ""))[:32]
            user, account = user_for_token(token)
            if user is None or (account or {}).get("disabled") or any(
                    isinstance(t, dict) and t.get("t") == token and t.get("kicked")
                    for t in (account or {}).get("tokens") or []):
                ws_send(sock, 8)
                return

            ip = self._ip()
            # 每个连接自带一把发送锁：广播（news 线程）与这里的 ok/pong 会并发
            # 写同一个 socket，无锁时部分写会让两个帧的字节交错。
            conn = {"user": user, "ip": ip, "ver": ver,
                    "since": f"{datetime.now(CN):%F %T}", "sock": sock,
                    "lock": threading.Lock()}
            with _ws_lock:
                WS_CONNS[token] = conn
            touch_token(user, token, ip, ver)
            ws_send_conn(conn, 1, b'{"ok":true}')
            log(f"ws connect {user} from {ip} ver={ver or '-'}")

            while True:
                opcode, payload = ws_read_frame(sock)
                if opcode is None or opcode == 8:
                    break
                if opcode == 9:                    # ping -> pong (echo payload)
                    ws_send_conn(conn, 10, payload)
                # any traffic proves liveness; touch_token throttles the write
                touch_token(user, token, ip, None)
        except Exception:
            pass   # timeouts and resets all mean the same thing: gone
        finally:
            if token is not None:
                with _ws_lock:
                    WS_CONNS.pop(token, None)
            if user is not None:
                log(f"ws disconnect {user}")

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
                if t.get("kicked"):
                    continue
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
                               "created": t.get("created"),
                               # 内存里的 seen 比磁盘新（落盘按 TOUCH_FLUSH_S 节流）
                               "seen": touch_seen(t["t"]) or t.get("seen"),
                               "duration": duration})
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
            ip = self._ip()
            if account is not None:
                blocked = login_attempt(user, ip)
                if blocked:
                    log(f"web login throttled {user} from {ip} ({blocked}s left)")
                    return self._bad(f"尝试过于频繁，请 {blocked} 秒后重试", 429)
            if account is None or account.get("disabled") \
                    or role_of(account) not in ("admin", "sysadmin") \
                    or not verify_password(account, password):
                if account is not None:
                    self._log_login(user, "error", "管理台登录失败")
                return self._bad("用户名或密码错误，或无管理权限", 401)
            login_ok(user, ip)
            token = web_session_create(user, role_of(account), ip)
            self._log_login(user, "info", "管理台登录")
            log(f"web login {user} from {self._ip()}")
            return self._json({"token": token, "username": user, "role": role_of(account)})

        if self.path == "/clientlog":
            authed = self._auth()
            if authed is None:
                return
            user, doc, _ = authed
            try:
                body = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            kind = str(body.get("kind") or "")[:32]
            detail = str(body.get("detail") or "")[:4000]
            if not kind or not detail:
                return self._bad("bad fault")
            with _lock:
                doc = load_account(user) or doc
                faults = doc.get("faultlogs") or []
                faults.append({"at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                               "ver": self._ver(), "kind": kind, "detail": detail})
                doc["faultlogs"] = cap_log("faults", user, faults, 100)
                save_account(user, doc)
            log(f"client fault {user} {kind}: {detail[:120]}")
            return self._json({"ok": True})

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
            if not OPEN_REGISTER:
                log(f"register refused (closed) from {self._ip()}")
                return self._bad("自助注册已关闭，请联系管理员开通账户", 403)
            try:
                doc = json.loads(raw)
            except Exception:
                return self._bad("bad json")
            user = str(doc.get("username") or "")
            password = str(doc.get("password") or "")
            if not USER_RE.match(user):
                return self._bad("用户名需为 3~32 位字母/数字/下划线")
            if not strong_enough(password):
                return self._bad(PASSWORD_RULE)
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
            # 限速只对存在的账户计数：不存在的用户名枚举不到任何信息，也就
            # 不给攻击者一条撑爆内存的路。
            ip = self._ip()
            blocked = login_attempt(user, ip)
            if blocked:
                log(f"login throttled {user} from {ip} ({blocked}s left)")
                return self._bad(f"尝试过于频繁，请 {blocked} 秒后重试", 429)
            if account.get("disabled"):
                self._log_login(user, "error", "登录失败：账户已禁用")
                return self._bad("账户已禁用", 403)
            if not verify_password(account, password):
                self._log_login(user, "error", "登录失败：密码错误")
                return self._bad("用户名或密码错误", 401)
            login_ok(user, ip)
            token = self._mint()
            with _lock:
                account = load_account(user) or account
                normalize_tokens(account)
                cutoff = f"{datetime.now(CN) - timedelta(days=30):%F %T}"
                kick_cutoff = f"{datetime.now(CN) - timedelta(days=1):%F %T}"
                account["tokens"] = [t for t in account["tokens"]
                                     if (t.get("seen") or t.get("created") or "") >= cutoff
                                     and not (t.get("kicked")
                                              and (t.get("kickedAt") or "") < kick_cutoff)]
                account["tokens"] = account["tokens"][-(MAX_TOKENS - 1):] + [token]
                logins = account.get("logins") or []
                account["logins"] = cap_log("logins", user, logins + [self._login_entry()], 100)
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
                prev_groups = account.get("groups") or []
                stored_panel = {g.get("name"): bool(g.get("panel", True))
                                for g in prev_groups}
                clean = []
                for g in groups[:200]:
                    name = str(g.get("name") or "")[:64]
                    codes = [str(c).upper() for c in (g.get("codes") or [])[:2000]]
                    panel = (bool(g["panel"]) if "panel" in g
                             else stored_panel.get(name, True))
                    clean.append({"name": name, "codes": codes, "panel": panel})
                # Diff vs the copy being replaced (inside the lock, before the
                # overwrite): multiset of codes plus group add/remove and 轮换
                # flips. Feeds server.log and the web-console 配置修改 log; a
                # rename shows as +组/-组 (indistinguishable from add+remove).
                oc = Counter(c for g in prev_groups for c in g.get("codes") or [])
                nc = Counter(c for g in clean for c in g["codes"])

                def locs(code, gs):
                    return ",".join(g.get("name") or "?" for g in gs
                                    if code in (g.get("codes") or []))

                added_c = list((nc - oc).elements())
                removed_c = list((oc - nc).elements())
                delta = (["+" + c + "(" + locs(c, clean) + ")" for c in added_c[:8]]
                         + (["…共+%d个合约" % len(added_c)] if len(added_c) > 8 else [])
                         + ["-" + c + "(" + locs(c, prev_groups) + ")"
                            for c in removed_c[:8]]
                         + (["…共-%d个合约" % len(removed_c)] if len(removed_c) > 8 else []))
                old_names = [g.get("name") or "" for g in prev_groups]
                new_names = [g["name"] for g in clean]
                added_g = [n for n in new_names if n not in old_names]
                removed_g = [n for n in old_names if n not in new_names]
                # A rename otherwise reads as delete+create: pair an added group
                # with a removed one holding exactly the same contract list.
                renames = []
                for a in list(added_g):
                    a_codes = next(g["codes"] for g in clean if g["name"] == a)
                    src = next((r for r in removed_g
                                if next((g.get("codes") for g in prev_groups
                                         if (g.get("name") or "") == r), None) == a_codes),
                               None)
                    if src is not None:
                        renames.append("组改名「%s」→「%s」" % (src, a))
                        added_g.remove(a)
                        removed_g.remove(src)
                parts = (delta + renames
                         + ["+组「%s」" % n for n in added_g]
                         + ["-组「%s」" % n for n in removed_g]
                         + ["「%s」轮换%s" % (g["name"], "开" if g["panel"] else "关")
                            for g in clean if g["name"] in stored_panel
                            and g["panel"] != stored_panel[g["name"]]])
                if not parts and clean != prev_groups:
                    parts = ["顺序/结构调整"]
                # Arrival order IS the order: every push overwrites. Multiple
                # clients racing is resolved by "last push wins", per design.
                account["groups"] = clean
                account["groups_at"] = at
                account["synced"] = f"{datetime.now(CN):%F %T}"
                if parts:
                    cfg = account.get("cfglogs") or []
                    account["cfglogs"] = cap_log("cfglogs", user, cfg + [{
                        "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                        "ver": self._ver() or "", "kind": "分组",
                        "detail": " ".join(parts),
                    }], 300)
                save_account(user, account)
            total = sum(len(g["codes"]) for g in clean)
            log(f"groups push {user} from {self._ip()} ver={self._ver() or '-'} "
                f"{len(clean)}g/{total}c" + (" " + " ".join(delta) if delta else ""))
            return self._json({"ok": True, "groups": len(clean), "contracts": total})

        if self.path == "/ping":
            if self._auth() is None:
                return
            return self._json({"ok": True})

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
            if not strong_enough(new_pw):
                return self._bad(PASSWORD_RULE)
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
                account["pwlogs"] = cap_log("pwlogs", user, pwlogs + [{
                    "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                    "by": "self", "ver": self._ver(),
                }], 50)
                login_ok(user, self._ip())   # 改密成功即清掉本机失败计数
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
                # Structural-clobber guard: a client from before templates
                # existed (≤1.0.65) can't carry these keys — its push must not
                # silently delete what newer clients saved. Same precedent as
                # the group panel-flag guard.
                stored = account.get("settings") or {}
                # Account/client split (2026-08-27): only the ACCOUNT slice —
                # 模板库 + 备注 — lives on the server. Everything else a client
                # may still send (亮度, column layout, active template, 口径…)
                # is client-local by design and ignored here; the stored copy
                # keeps serving those legacy keys to pre-split pullers.
                merged = dict(stored)
                for key in ("stealthTemplates", "notes", "at"):
                    if key in settings:
                        merged[key] = settings[key]
                # Concrete diff; empty -> echo push (or a purely client-local
                # change), which gets no 配置修改 entry.
                detail = "；".join(settings_detail(stored, merged))[:300]
                account["settings"] = merged
                account["settings_updated"] = f"{datetime.now(CN):%F %T}"
                if detail:
                    cfg = account.get("cfglogs") or []
                    account["cfglogs"] = cap_log("cfglogs", user, cfg + [{
                        "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                        "ver": self._ver() or "", "kind": "设置",
                        "detail": detail,
                    }], 300)
                save_account(user, account)
            # Ops trail: settings pushes are whole-blob overwrites, so WHO/WHEN/
            # from WHERE matters the moment two machines disagree about "nobody
            # changed anything".
            log(f"settings push {user} from {self._ip()} ver={self._ver() or '-'} "
                f"{len(raw)}B" + (f" [{detail}]" if detail else " (无变更)"))
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
            account["logins"] = cap_log("logins", user, logins + [{
                "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                "ver": self._ver(), "level": level, "event": event,
            }], 100)
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
            if not strong_enough(password):
                return self._bad(PASSWORD_RULE)
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
                account = load_account(user) or account
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
                # 锁外读来的快照可能已过期，锁内重读只改自有键——否则会把
                # 并发写入的分组/设置/日志一起回滚。
                account = load_account(user) or account
                normalize_tokens(account)
                account["disabled"] = bool(doc.get("disabled"))
                if account["disabled"]:
                    account["tokens"] = []   # 禁用即踢下线
                save_account(user, account)
            _token_cache.clear()
            log(f"admin[{actor_name}]: disable {user} -> {account['disabled']}")
            return self._json({"ok": True})

        if action == "logout":
            # Revoke-with-reason, NOT delete: a deleted token 401s exactly like
            # an expired one, and the client's remembered-password self-heal
            # silently logged straight back in — force logout looked like a
            # no-op. A kicked marker lets _auth answer "kicked", which the
            # client treats as "stay signed out until a HUMAN logs in".
            with _lock:
                account = load_account(user) or account
                normalize_tokens(account)
                for t in account["tokens"]:
                    t["kicked"] = actor_name
                    t["kickedAt"] = f"{datetime.now(CN):%F %T}"
                save_account(user, account)
            _token_cache.clear()
            with _ws_lock:
                for tok, conn in list(WS_CONNS.items()):
                    if conn.get("user") == user:
                        try:
                            conn.get("sock") and conn["sock"].close()
                        except Exception:
                            pass
                        WS_CONNS.pop(tok, None)
            self._log_login(user, "warn", f"被管理员登出（{actor_name}）")
            log(f"admin[{actor_name}]: logout {user}")
            return self._json({"ok": True})

        if action == "password":
            password = str(doc.get("password") or "")
            if not strong_enough(password):
                return self._bad(PASSWORD_RULE)
            with _lock:
                account = load_account(user) or account
                salt = secrets.token_hex(16)
                account["auth"] = {"salt": salt, "hash": hash_pw(password, salt),
                                   "iters": PBKDF2_ITERS}
                if doc.get("logout"):
                    account["tokens"] = []
                pwlogs = account.get("pwlogs") or []
                account["pwlogs"] = cap_log("pwlogs", user, pwlogs + [{
                    "at": f"{datetime.now(CN):%F %T}", "ip": self._ip(),
                    "by": f"admin:{actor_name}",
                }], 50)
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
    threading.Thread(target=news_scheduler, daemon=True).start()

    server = ThreadingHTTPServer((BIND, PORT), Handler)
    log(f"listening on {BIND}:{PORT}, data={DATA}, "
        f"retain={'forever' if RETAIN_DAYS <= 0 else f'{RETAIN_DAYS}d'}")
    server.serve_forever()


if __name__ == "__main__":
    main()

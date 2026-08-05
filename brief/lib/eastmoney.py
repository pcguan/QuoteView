"""EastMoney endpoints, with every field number in one place.

Field numbers are NOT reusable across endpoints — `f127` is 市净率 in `clist`,
细分行业 in `stock/get`, and 3日涨幅 in `ulist.np`. Each map below therefore
states which endpoint it belongs to, and any change goes here rather than being
scattered through the callers.

The readings used here were verified against values computed from K-line data
(see ../../docs/data-source-fields.md).
"""

from __future__ import annotations

import json

from . import net

REFERER = "https://quote.eastmoney.com/"

# ---------------------------------------------------------------- endpoints

CLIST = "https://push2delay.eastmoney.com/api/qt/clist/get"
ULIST = "https://push2.eastmoney.com/api/qt/ulist.np/get"
KLINE = "https://push2his.eastmoney.com/api/qt/stock/kline/get"

# ------------------------------------------------------------- field tables

# clist / ulist share the stock-row numbering.
QUOTE_FIELDS = {
    "f12": "code",
    "f13": "market",
    "f14": "name",
    "f2": "price",
    "f3": "pct",          # 今日涨跌幅 %
    "f4": "change",       # 涨跌额
    "f5": "volume",       # 成交量(手)
    "f6": "amount",       # 成交额(元)
    "f8": "turnover",     # 换手率 %
    "f18": "prev_close",
    "f20": "total_cap",
    "f21": "float_cap",
}

# clist board rows (fs=m:90+t:2 行业 / t:3 概念 / t:1 地区).
BOARD_FIELDS = {
    "f12": "code",
    "f14": "name",
    "f3": "pct",
    "f104": "up",         # 上涨家数
    "f105": "down",       # 下跌家数
    "f128": "leader",     # 领涨股
    "f136": "leader_pct",
}

# The six A-share indices worth a daily line, as secid -> label.
INDICES = {
    "1.000001": "上证指数",
    "0.399001": "深证成指",
    "0.399006": "创业板指",
    "1.000688": "科创50",
    "0.899050": "北证50",
    "1.000300": "沪深300",
    "0.399905": "中证500",
}

# Filters covering every A-share, for breadth counting.
A_SHARE_FS = "m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23,m:0+t:81+s:2048"


def _get_json(url: str) -> dict:
    return json.loads(net.get(url, referer=REFERER))


def _rows(payload: dict) -> list[dict]:
    """clist/ulist return `diff` as either a list or an index-keyed object."""
    data = payload.get("data")
    if not isinstance(data, dict):
        return []

    diff = data.get("diff")
    if isinstance(diff, dict):
        return [diff[k] for k in sorted(diff, key=lambda x: int(x))]
    if isinstance(diff, list):
        return diff
    return []


def _rename(row: dict, fields: dict[str, str]) -> dict:
    out = {}
    for key, name in fields.items():
        value = row.get(key)
        # EastMoney sends "-" for "not applicable"; keep that distinct from 0.
        out[name] = None if value in ("-", "") else value
    return out


def indices() -> list[dict]:
    """Headline index levels and moves."""
    secids = ",".join(INDICES)
    fields = ",".join(["f12", "f13", "f14", "f2", "f3", "f4", "f5", "f6"])
    payload = _get_json(f"{ULIST}?fltt=2&secids={secids}&fields={fields}")

    out = []
    for row in _rows(payload):
        item = _rename(row, QUOTE_FIELDS)
        item["label"] = INDICES.get(f"{row.get('f13')}.{row.get('f12')}", item.get("name"))
        out.append(item)
    return out


def boards(kind: str = "industry", limit: int = 500) -> list[dict]:
    """Industry / concept / region boards, sorted by move, best first."""
    fs = {"industry": "m:90+t:2", "concept": "m:90+t:3", "region": "m:90+t:1"}[kind]
    fields = ",".join(BOARD_FIELDS)

    out: list[dict] = []
    page = 1
    while len(out) < limit:
        payload = _get_json(
            f"{CLIST}?pn={page}&pz=100&po=1&fltt=2&invt=2&fid=f3&fs={fs}&fields={fields}")
        rows = _rows(payload)
        if not rows:
            break
        out.extend(_rename(r, BOARD_FIELDS) for r in rows)
        if len(rows) < 100:
            break
        page += 1

    return out[:limit]


def breadth() -> dict:
    """
    Advance/decline and limit counts across every A-share.

    Counted from the market itself rather than read from a summary field: pulling
    the ranked list in both directions gives the tails (limit-ups and limit-downs)
    at the same time, and the totals come from the endpoint's own `total`.
    """
    fields = "f12,f13,f14,f3"

    def page(order: int, pages: int) -> list[dict]:
        rows: list[dict] = []
        for pn in range(1, pages + 1):
            payload = _get_json(
                f"{CLIST}?pn={pn}&pz=100&po={order}&fltt=2&invt=2&fid=f3"
                f"&fs={A_SHARE_FS}&fields={fields}")
            got = _rows(payload)
            if not got:
                break
            rows.extend(got)
        return rows

    top = page(1, 3)      # strongest 300
    bottom = page(0, 3)   # weakest 300

    total = 0
    payload = _get_json(f"{CLIST}?pn=1&pz=1&po=1&fltt=2&invt=2&fid=f3&fs={A_SHARE_FS}&fields=f12")
    if isinstance(payload.get("data"), dict):
        total = payload["data"].get("total") or 0

    def pct(row: dict) -> float | None:
        v = row.get("f3")
        return v if isinstance(v, (int, float)) else None

    # A 20% board (创业板 30xxx / 科创 688) versus a 10% one — the threshold
    # differs, so "at the limit" is judged per board rather than by one number.
    def at_limit(row: dict, up: bool) -> bool:
        p = pct(row)
        if p is None:
            return False
        code = str(row.get("f12") or "")
        wide = code.startswith(("30", "688", "8", "4"))
        edge = 19.5 if wide else 9.8
        return p >= edge if up else p <= -edge

    return {
        "total": total,
        "limit_up": sum(1 for r in top if at_limit(r, True)),
        "limit_down": sum(1 for r in bottom if at_limit(r, False)),
        "top": [_rename(r, {"f12": "code", "f14": "name", "f3": "pct"}) for r in top[:20]],
        "bottom": [_rename(r, {"f12": "code", "f14": "name", "f3": "pct"}) for r in bottom[:20]],
    }


def market_breadth() -> dict:
    """
    Advancing / declining / unchanged, counted stock by stock.

    Deliberately NOT summed from the industry boards: `m:90+t:2` returns 496
    boards that overlap heavily (一级/二级Ⅱ/三级Ⅲ all present), so adding their
    per-board counts gave 16,393 for a market of 5,889 — every stock counted
    two or three times. There is no board level that partitions the market.

    So it pages the whole market instead. `pz` is capped at 100 server-side, so
    this is ~59 small requests asking for one field each; throttled, it takes
    about 20 seconds and runs twice a day.
    """
    import time as _time

    fields = "f12,f3"
    per_page = 100

    payload = _get_json(f"{CLIST}?pn=1&pz=1&po=1&fltt=2&invt=2&fid=f3&fs={A_SHARE_FS}&fields=f12")
    total = (payload.get("data") or {}).get("total") or 0
    if not total:
        raise net.FetchError("breadth: market total unavailable")

    up = down = flat = seen = 0
    pages = (total + per_page - 1) // per_page

    for pn in range(1, pages + 1):
        page = _get_json(
            f"{CLIST}?pn={pn}&pz={per_page}&po=1&fltt=2&invt=2&fid=f3"
            f"&fs={A_SHARE_FS}&fields={fields}")
        rows = _rows(page)
        if not rows:
            break

        for row in rows:
            pct = row.get("f3")
            if not isinstance(pct, (int, float)):
                continue     # suspended: no move to classify
            seen += 1
            if pct > 0:
                up += 1
            elif pct < 0:
                down += 1
            else:
                flat += 1

        _time.sleep(0.25)

    # A short count means paging was cut off; report it rather than passing off a
    # partial tally as the market's breadth.
    if seen < total * 0.9:
        raise net.FetchError(f"breadth: only counted {seen} of {total}")

    return {"total": total, "counted": seen, "advancing": up,
            "declining": down, "unchanged": flat}


def quotes(secids: list[str]) -> list[dict]:
    """Batch quote for arbitrary contracts (the watchlist)."""
    if not secids:
        return []

    fields = ",".join(QUOTE_FIELDS)
    payload = _get_json(f"{ULIST}?fltt=2&secids={','.join(secids)}&fields={fields}")
    return [_rename(r, QUOTE_FIELDS) for r in _rows(payload)]


def daily(secid: str, limit: int = 6) -> list[dict]:
    """
    Recent daily bars, unadjusted. Row order is date, open, CLOSE, high, low —
    close is third, not the usual OHLC.
    """
    url = (f"{KLINE}?fields1=f1,f2,f3&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59"
           f"&klt=101&fqt=0&end=20500101&lmt={limit}&secid={secid}")
    payload = _get_json(url)

    data = payload.get("data") or {}
    out = []
    for line in data.get("klines") or []:
        c = line.split(",")
        if len(c) < 9:
            continue
        out.append({
            "date": c[0],
            "open": float(c[1]),
            "close": float(c[2]),
            "high": float(c[3]),
            "low": float(c[4]),
            "volume": float(c[5]),
            "amount": float(c[6]),
            "pct": float(c[8]),
        })
    return out

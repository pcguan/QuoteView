#!/usr/bin/env python3
"""Structured market data for one trading day -> data/YYYYMMDD/*.json

This is the deterministic half of the pipeline. It writes numbers and nothing
else: no interpretation, no ranking commentary, no derived opinions. Whatever
the brief later says about the market has to trace back to a file written here.

Every source is fetched independently. A source that fails is recorded in
_status.json as "error" and its file is simply absent — the brief then prints
N/A. Nothing is ever inferred to fill a gap, because a plausible-looking
invented number is far worse than a visible blank.
"""

from __future__ import annotations

import csv
import json
import os
import sys
import traceback
from datetime import datetime, timezone, timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib import eastmoney as em  # noqa: E402
from lib import net  # noqa: E402

BASE = os.path.dirname(os.path.abspath(__file__))
CN = timezone(timedelta(hours=8))


def trading_day() -> tuple[str, str]:
    """
    The trading date this run describes, and how it was determined.

    Preferred source is the market itself: the last daily bar of the Shanghai
    Composite IS the last session, which covers weekends and holidays without
    shipping a calendar.

    That endpoint is also the most rate-limited one EastMoney serves, so it must
    not be able to sink the whole run — it did exactly that once, taking down
    every other source with it. On failure this falls back to the Beijing
    calendar date and SAYS SO in _status.json, because a fallback date can be
    wrong on a holiday and the reader needs to know which one they got.
    """
    try:
        bars = em.daily("1.000001", limit=2)
        if bars:
            return bars[-1]["date"].replace("-", ""), "index-bar"
    except Exception:  # noqa: BLE001
        pass

    return datetime.now(CN).strftime("%Y%m%d"), "clock(fallback)"


def load_watchlist() -> list[dict]:
    path = os.path.join(BASE, "watchlist.csv")
    if not os.path.exists(path):
        return []

    with open(path, encoding="utf-8-sig") as fh:
        return [row for row in csv.DictReader(fh) if row.get("code")]


def secid_of(code: str) -> str | None:
    """`SH600519` / `600519.SH` / `600519` -> EastMoney secid."""
    code = code.strip().upper()
    if not code:
        return None

    if code.startswith(("SH", "SZ", "BJ")):
        prefix, digits = code[:2], code[2:]
    elif "." in code:
        digits, prefix = code.split(".", 1)
    else:
        digits = code
        prefix = "SH" if digits.startswith(("6", "5")) else "SZ"

    if not digits.isdigit():
        return None

    market = 1 if prefix == "SH" else 0
    return f"{market}.{digits}"


def main() -> int:
    started = datetime.now(CN)

    day, day_source = trading_day()

    out_dir = os.path.join(BASE, "data", day)
    os.makedirs(out_dir, exist_ok=True)

    status: dict[str, dict] = {}

    def task(name: str, fn) -> None:
        """Run one source in isolation; a failure never stops the others."""
        try:
            payload = fn()
            with open(os.path.join(out_dir, f"{name}.json"), "w", encoding="utf-8") as fh:
                json.dump(payload, fh, ensure_ascii=False, indent=1)
            count = len(payload) if isinstance(payload, (list, dict)) else 1
            status[name] = {"state": "ok", "items": count}
            print(f"  {name:<12} ok ({count})")
        except Exception as exc:  # noqa: BLE001
            # A failed fetch may still leave a perfectly good file from earlier
            # the same trading day. Calling that "error" tells the reader to
            # discard a section that is actually fine — but calling it "ok" would
            # hide that the number is not from this run. So: stale, with the time.
            path = os.path.join(out_dir, f"{name}.json")
            previous = None
            if os.path.exists(path):
                previous = datetime.fromtimestamp(os.path.getmtime(path), CN)

            if previous is not None and previous.strftime("%Y%m%d") == day:
                status[name] = {
                    "state": "stale",
                    "fetched_at": previous.isoformat(timespec="seconds"),
                    "error": f"{type(exc).__name__}: {exc}",
                    "note": "本次抓取失败，沿用本交易日早些时候的数据",
                }
                print(f"  {name:<12} STALE 沿用 {previous:%H:%M:%S} 的数据 "
                      f"({type(exc).__name__})", file=sys.stderr)
            else:
                status[name] = {"state": "error", "error": f"{type(exc).__name__}: {exc}"}
                print(f"  {name:<12} ERROR {type(exc).__name__}: {exc}", file=sys.stderr)

    print(f"trading day {day} [{day_source}]  (run at {started:%Y-%m-%d %H:%M:%S %Z})")

    task("indices", em.indices)
    task("breadth", em.breadth)
    task("industry", lambda: em.boards("industry"))
    task("concept", lambda: em.boards("concept"))

    # Derived aggregates belong HERE, not in the brief: the LLM is forbidden to
    # do arithmetic, and it correctly refused to add the two exchanges' turnover
    # together. Anything that needs computing gets computed once, by code, and
    # written down as a fact like any other.
    def summary() -> dict:
        idx = em.indices()

        def amount_of(label: str) -> float | None:
            for row in idx:
                if row.get("label") == label:
                    v = row.get("amount")
                    return v if isinstance(v, (int, float)) else None
            return None

        sh, sz = amount_of("上证指数"), amount_of("深证成指")

        counts = em.market_breadth()

        return {
            # Sum of the two exchange totals; index turnover is exchange-wide.
            "total_amount": (sh + sz) if (sh is not None and sz is not None) else None,
            "sh_amount": sh,
            "sz_amount": sz,
            "advancing": counts["advancing"],
            "declining": counts["declining"],
            "unchanged": counts["unchanged"],
            "counted": counts["counted"],
            "breadth_basis": f"逐只统计沪深京 A 股，实际计入 {counts['counted']}/{counts['total']} 只（停牌无涨跌幅者不计）",
        }

    task("summary", summary)

    watchlist = load_watchlist()
    if watchlist:
        def watch() -> list[dict]:
            secids, meta = [], {}
            for row in watchlist:
                sid = secid_of(row["code"])
                if not sid:
                    continue
                secids.append(sid)
                meta[sid.split(".")[1]] = row
            rows = em.quotes(secids)
            for r in rows:
                extra = meta.get(str(r.get("code")), {})
                r["tag"] = extra.get("tag", "")
                r["watch_name"] = extra.get("name", "")
            return rows

        task("watchlist", watch)
    else:
        # Emptying watchlist.csv must actually remove the section, not just stop
        # refreshing it. A file left behind from when the list was populated looks
        # exactly like current data to whatever reads this directory next — which
        # is how a deleted watchlist kept showing up in the brief.
        stale_watchlist = os.path.join(out_dir, "watchlist.json")
        if os.path.exists(stale_watchlist):
            os.remove(stale_watchlist)
            print("  watchlist    关注池为空，已清除上次留下的 watchlist.json")

    status["_meta"] = {
        "trading_day": day,
        "trading_day_source": day_source,
        "fetched_at": started.isoformat(timespec="seconds"),
        "sources": "eastmoney",
    }

    with open(os.path.join(out_dir, "_status.json"), "w", encoding="utf-8") as fh:
        json.dump(status, fh, ensure_ascii=False, indent=1)

    failed = [k for k, v in status.items() if v.get("state") == "error"]
    print(f"\n-> {out_dir}")
    print(f"   ok={len([v for v in status.values() if v.get('state') == 'ok'])} failed={len(failed)}")

    # Exit 0 even with partial failures: the brief is still worth generating from
    # what did arrive, and _status.json says exactly what is missing.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

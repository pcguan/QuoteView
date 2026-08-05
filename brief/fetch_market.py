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


def trading_day() -> str:
    """
    The trading date this run describes, taken from the market rather than the
    clock: the last daily bar of the Shanghai Composite IS the last session,
    which handles weekends and holidays without shipping a calendar.
    """
    bars = em.daily("1.000001", limit=2)
    if not bars:
        raise net.FetchError("cannot determine trading day: no index bars")
    return bars[-1]["date"].replace("-", "")


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

    try:
        day = trading_day()
    except Exception as exc:  # noqa: BLE001
        print(f"FATAL: {exc}", file=sys.stderr)
        return 2

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
            status[name] = {
                "state": "error",
                "error": f"{type(exc).__name__}: {exc}",
            }
            print(f"  {name:<12} ERROR {type(exc).__name__}: {exc}", file=sys.stderr)
            traceback.print_exc(file=sys.stderr)

    print(f"trading day {day}  (run at {started:%Y-%m-%d %H:%M:%S %Z})")

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

    status["_meta"] = {
        "trading_day": day,
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

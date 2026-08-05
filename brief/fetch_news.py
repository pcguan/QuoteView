#!/usr/bin/env python3
"""Whitelisted source text -> raw/YYYYMMDD/

Two outputs per run:

  raw/YYYYMMDD/<source>.<ext>   the body exactly as served, untouched
  raw/YYYYMMDD/index.json       one entry per headline: title, time, url, and
                                the file above it came from

The index exists so every line of the brief can name its origin. A claim with
no `source` pointing at a file here does not belong in the brief.

Sources are a fixed whitelist. Nothing is discovered at runtime and nothing
outside ALLOWED_DOMAINS is fetched, so the input set is auditable rather than
whatever a search happened to surface.
"""

from __future__ import annotations

import html
import json
import time
import os
import re
import sys
import traceback
from datetime import datetime, timezone, timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib import eastmoney as em  # noqa: E402
from lib import net  # noqa: E402

# How far back to collect. A "daily" brief that only saw the last 90 minutes of
# headlines is not a daily brief — measured, one page of each source covered
# 14:04-15:25 and 15:00-15:29 respectively, so anything from the previous session
# was simply invisible. This reaches back past the previous close.
LOOKBACK_HOURS = 30

# Page ceilings, so a source that never reports an old enough timestamp can't
# loop forever.
MAX_PAGES = 30

BASE = os.path.dirname(os.path.abspath(__file__))
CN = timezone(timedelta(hours=8))

# Anything not on this list is never requested.
#
# 证券时报 (www.stcn.com) was dropped: its 快讯 list page renders client-side, so
# the HTML contains no article links at all, and the site exposes no list API
# (its only ajax endpoint, /xinpi/list-ajax.html, serves 信披 and returns empty).
# A source that can never yield rows is worse than no source — it reports an
# anomaly every single day and trains you to ignore the anomaly line.
#
# 财联社 (www.cls.cn) was dropped too: answers 418 to non-browser clients.
ALLOWED_DOMAINS = {
    "newsapi.eastmoney.com",
    "zhibo.sina.com.cn",
    "www.cninfo.com.cn",
}


def check_domain(url: str) -> None:
    host = url.split("/")[2]
    if host not in ALLOWED_DOMAINS:
        raise net.FetchError(f"domain not whitelisted: {host}")


# ------------------------------------------------------------------ sources

def eastmoney_flash() -> tuple[str, str, list[dict]]:
    """EastMoney 快讯, paged back until LOOKBACK_HOURS is covered."""
    cutoff = (datetime.now(CN) - timedelta(hours=LOOKBACK_HOURS)).strftime("%Y-%m-%d %H:%M:%S")

    items: list[dict] = []
    bodies: list[str] = []

    for page in range(1, MAX_PAGES + 1):
        url = f"https://newsapi.eastmoney.com/kuaixun/v1/getlist_102_ajaxResult_50_{page}_.html"
        check_domain(url)
        body = net.get(url)
        bodies.append(body)

        payload = json.loads(body[body.find("{"):].rstrip().rstrip(";"))
        rows = payload.get("LivesList") or []
        if not rows:
            break

        oldest = ""
        for row in rows:
            when = str(row.get("showtime") or "")
            oldest = min(oldest, when) if oldest else when
            items.append({
                "title": html.unescape(str(row.get("digest") or row.get("title") or "")).strip(),
                "time": when,
                "url": str(row.get("url_unique") or row.get("url_m") or ""),
            })

        if oldest and oldest < cutoff:
            break
        time.sleep(0.3)

    # Every page kept, so a headline can still be checked against what was served.
    return "eastmoney_flash.json", "\n".join(bodies), items


def sina_live() -> tuple[str, str, list[dict]]:
    """Sina 7x24 财经直播, paged back the same way."""
    cutoff = (datetime.now(CN) - timedelta(hours=LOOKBACK_HOURS)).strftime("%Y-%m-%d %H:%M:%S")

    items: list[dict] = []
    bodies: list[str] = []

    for page in range(1, MAX_PAGES + 1):
        url = (f"https://zhibo.sina.com.cn/api/zhibo/feed?page={page}&page_size=100"
               "&zhibo_id=152&tag_id=0&dire=f&dpc=1")
        check_domain(url)
        body = net.get(url)
        bodies.append(body)

        rows = json.loads(body).get("result", {}).get("data", {}).get("feed", {}).get("list") or []
        if not rows:
            break

        oldest = ""
        for row in rows:
            when = str(row.get("create_time") or "")
            oldest = min(oldest, when) if oldest else when
            items.append({
                "title": html.unescape(str(row.get("rich_text") or row.get("text") or "")).strip(),
                "time": when,
                "url": str(row.get("docurl") or ""),
            })

        if oldest and oldest < cutoff:
            break
        time.sleep(0.3)

    return "sina_live.json", "\n".join(bodies), items


def cninfo_notices() -> tuple[str, str, list[dict]]:
    """
    巨潮资讯 announcements — the statutory disclosure channel, so this is the
    one source whose contents are legally required to be accurate.
    """
    url = "http://www.cninfo.com.cn/new/hisAnnouncement/query"
    check_domain(url)
    body = net.post(
        url,
        {"pageNum": 1, "pageSize": 60, "column": "szse", "tabName": "fulltext",
         "sortName": "", "sortType": "", "isHLtitle": "true"},
        referer="http://www.cninfo.com.cn/new/commonUrl?url=disclosure/list/notice")

    payload = json.loads(body)
    items = []
    for row in payload.get("announcements") or []:
        title = re.sub(r"<[^>]+>", "", str(row.get("announcementTitle") or "")).strip()
        stamp = row.get("announcementTime")
        when = ""
        if isinstance(stamp, (int, float)):
            when = datetime.fromtimestamp(stamp / 1000, CN).strftime("%Y-%m-%d %H:%M")
        items.append({
            "title": f"{row.get('secName') or ''} {title}".strip(),
            "time": when,
            "url": "http://static.cninfo.com.cn/" + str(row.get("adjunctUrl") or ""),
        })
    return "cninfo_notices.json", body, items


SOURCES = {
    "eastmoney_flash": ("东财快讯", eastmoney_flash),
    "sina_live": ("新浪7x24", sina_live),
    "cninfo_notices": ("巨潮公告", cninfo_notices),
}


def main() -> int:
    started = datetime.now(CN)

    # Same fallback as fetch_market: the kline endpoint is the most throttled one
    # and must not decide whether news gets collected at all.
    try:
        day = em.daily("1.000001", limit=2)[-1]["date"].replace("-", "")
    except Exception:  # noqa: BLE001
        day = started.strftime("%Y%m%d")

    out_dir = os.path.join(BASE, "raw", day)
    os.makedirs(out_dir, exist_ok=True)

    index: list[dict] = []
    status: dict[str, dict] = {}

    print(f"trading day {day}  (run at {started:%Y-%m-%d %H:%M:%S %Z})")

    for key, (label, fn) in SOURCES.items():
        try:
            filename, body, items = fn()
            with open(os.path.join(out_dir, filename), "w", encoding="utf-8") as fh:
                fh.write(body)

            for item in items:
                if not item.get("title"):
                    continue
                index.append({
                    "source": filename,      # the file this line can be checked against
                    "source_label": label,
                    "title": item["title"],
                    "time": item.get("time", ""),
                    "url": item.get("url", ""),
                })

            # Zero items is a failure, not a success: the request worked but the
            # parser matched nothing, which usually means the page changed shape.
            # Reporting it as ok would let a source rot silently for weeks.
            state = "ok" if items else "empty"
            status[key] = {"state": state, "items": len(items), "file": filename}
            print(f"  {label:<12} {state} ({len(items)} 条) -> {filename}",
                  file=sys.stderr if state == "empty" else sys.stdout)
        except Exception as exc:  # noqa: BLE001
            status[key] = {"state": "error", "error": f"{type(exc).__name__}: {exc}"}
            print(f"  {label:<12} ERROR {type(exc).__name__}: {exc}", file=sys.stderr)
            traceback.print_exc(file=sys.stderr)

    # Same story often arrives from both feeds within seconds. Dedupe on the
    # opening of the headline, keeping the EARLIEST copy — that timestamp is when
    # the market could first have known, which is the thing that matters when a
    # move precedes the explanation.
    index.sort(key=lambda x: x.get("time", ""))
    unique, seen = [], set()
    for item in index:
        key = item["title"][:36]
        if key in seen:
            continue
        seen.add(key)
        unique.append(item)

    duplicates = len(index) - len(unique)
    unique.reverse()   # newest first, which is how it gets read

    with open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8") as fh:
        json.dump(unique, fh, ensure_ascii=False, indent=1)

    index = unique

    status["_meta"] = {
        "trading_day": day,
        "fetched_at": started.isoformat(timespec="seconds"),
        "headlines": len(index),
        "duplicates_removed": duplicates,
        "lookback_hours": LOOKBACK_HOURS,
        "allowed_domains": sorted(ALLOWED_DOMAINS),
    }
    with open(os.path.join(out_dir, "_status.json"), "w", encoding="utf-8") as fh:
        json.dump(status, fh, ensure_ascii=False, indent=1)

    ok = [v for v in status.values() if v.get("state") == "ok"]
    bad = [k for k, v in status.items() if v.get("state") in ("error", "empty")]
    print(f"\n-> {out_dir}\n   合计 {len(index)} 条标题, {len(ok)} 个源成功"
          + (f", 异常源: {', '.join(bad)}" if bad else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

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
import os
import re
import sys
import traceback
from datetime import datetime, timezone, timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib import eastmoney as em  # noqa: E402
from lib import net  # noqa: E402

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
    """EastMoney 快讯 — JSONP-ish body, list under LivesList."""
    url = "https://newsapi.eastmoney.com/kuaixun/v1/getlist_102_ajaxResult_50_1_.html"
    check_domain(url)
    body = net.get(url)

    start = body.find("{")
    payload = json.loads(body[start:].rstrip().rstrip(";"))

    items = []
    for row in payload.get("LivesList") or []:
        items.append({
            "title": html.unescape(str(row.get("digest") or row.get("title") or "")).strip(),
            "time": str(row.get("showtime") or ""),
            "url": str(row.get("url_unique") or row.get("url_m") or ""),
        })
    return "eastmoney_flash.json", body, items


def sina_live() -> tuple[str, str, list[dict]]:
    """Sina 7x24 财经直播."""
    url = ("https://zhibo.sina.com.cn/api/zhibo/feed?page=1&page_size=50"
           "&zhibo_id=152&tag_id=0&dire=f&dpc=1")
    check_domain(url)
    body = net.get(url)
    payload = json.loads(body)

    items = []
    for row in (payload.get("result", {}).get("data", {}).get("feed", {}).get("list") or []):
        text = html.unescape(str(row.get("rich_text") or row.get("text") or "")).strip()
        items.append({
            "title": text,
            "time": str(row.get("create_time") or ""),
            "url": str(row.get("docurl") or ""),
        })
    return "sina_live.json", body, items


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

    with open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8") as fh:
        json.dump(index, fh, ensure_ascii=False, indent=1)

    status["_meta"] = {
        "trading_day": day,
        "fetched_at": started.isoformat(timespec="seconds"),
        "headlines": len(index),
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

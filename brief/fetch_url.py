#!/usr/bin/env python3
"""Controlled fetch of one discovered URL -> raw/YYYYMMDD/web-*.html

The only way the brief generator is allowed to pull a page. WebSearch may find a
URL; this is what actually retrieves it, and it exists so three things stay true:

  1. **Whitelist is enforced in code, not by instruction.** A model asked nicely
     to stay on approved domains will mostly comply. A script that refuses
     everything else always does.

  2. **The body lands on disk before anything reads it.** That file is what a
     later claim cites, so a line in the brief remains checkable against exactly
     the bytes that were served — the same guarantee the API-fetched sources give.

  3. **No second-hand summarising.** WebFetch passes a page through another model
     before the caller sees it; numbers do not survive that reliably. Here the
     text is stored verbatim and read from the file.

Usage:  python3 fetch_url.py <url> [--day YYYYMMDD]
Prints the stored filename on success; exits non-zero with a reason otherwise.
"""

from __future__ import annotations

import hashlib
import os
import re
import sys
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib import net  # noqa: E402

BASE = os.path.dirname(os.path.abspath(__file__))
CN = timezone(timedelta(hours=8))

# Domains allowed for discovered pages. Deliberately limited to outlets with an
# identifiable publisher and a correction process — regulators, exchanges, state
# media and established financial press. Aggregators, forums and self-media are
# not here: they restate other people's reporting, and a restatement is exactly
# what this pipeline is built to avoid citing.
ALLOWED_DOMAINS = {
    # 监管与官方
    "www.csrc.gov.cn", "www.pbc.gov.cn", "www.stats.gov.cn", "www.mof.gov.cn",
    "www.ndrc.gov.cn", "www.miit.gov.cn", "www.gov.cn", "www.safe.gov.cn",
    "www.customs.gov.cn",
    # 交易所与法定披露
    "www.sse.com.cn", "www.szse.cn", "www.bse.cn", "www.cninfo.com.cn",
    "www.chinaclear.cn",
    # 通讯社与主流财经媒体
    "www.news.cn", "www.xinhuanet.com", "finance.people.com.cn",
    "www.stcn.com", "www.cs.com.cn", "www.zqrb.cn", "www.yicai.com",
    "www.nbd.com.cn", "www.jiemian.com", "wallstreetcn.com",
    "finance.sina.com.cn", "finance.eastmoney.com", "www.10jqka.com.cn",
    # 海外
    "www.reuters.com", "www.bloomberg.com", "www.wsj.com", "www.ft.com",
    "www.cnbc.com", "apnews.com",
}


def host_of(url: str) -> str:
    match = re.match(r"https?://([^/]+)", url.strip(), re.I)
    return match.group(1).lower() if match else ""


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print("用法: fetch_url.py <url> [--day YYYYMMDD]", file=sys.stderr)
        return 2

    url = args[0]

    day = ""
    if "--day" in sys.argv:
        index = sys.argv.index("--day")
        if index + 1 < len(sys.argv):
            day = sys.argv[index + 1]
    if not day:
        day = datetime.now(CN).strftime("%Y%m%d")

    host = host_of(url)
    if host not in ALLOWED_DOMAINS:
        print(f"拒绝：{host or url} 不在白名单内。"
              f"允许的域名见 fetch_url.py 的 ALLOWED_DOMAINS。", file=sys.stderr)
        return 3

    out_dir = os.path.join(BASE, "raw", day)
    os.makedirs(out_dir, exist_ok=True)

    # Name derived from the URL, so fetching the same page twice reuses one file
    # and citations stay stable across runs.
    stamp = hashlib.sha1(url.encode()).hexdigest()[:10]
    slug = re.sub(r"[^a-z0-9]+", "-", host.replace("www.", ""))[:24].strip("-")
    filename = f"web-{slug}-{stamp}.html"
    path = os.path.join(out_dir, filename)

    if os.path.exists(path):
        print(filename)
        return 0

    try:
        body = net.get(url, timeout=25, attempts=2)
    except Exception as exc:  # noqa: BLE001
        print(f"抓取失败：{type(exc).__name__}: {exc}", file=sys.stderr)
        return 4

    # The URL is recorded inside the file: the filename alone doesn't say where
    # the bytes came from, and that matters when someone audits a citation later.
    header = (f"<!-- fetched-by: fetch_url.py\n"
              f"     url: {url}\n"
              f"     at: {datetime.now(CN).isoformat(timespec='seconds')} -->\n")

    with open(path, "w", encoding="utf-8") as fh:
        fh.write(header + body)

    print(filename)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

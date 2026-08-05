#!/usr/bin/env python3
"""Gate on the generated brief before it reaches a client.

The brief's JSON is written by hand by the model, so it can be malformed, and a
`source` can name a file that doesn't exist. Both failures are invisible until
something downstream breaks, which is why they're checked here rather than
trusted.

What is checked is only what can be checked mechanically — shape, and whether
every citation resolves. Whether a line is classified sensibly is not something
a script can judge, and pretending otherwise would be worse than not checking.

Exit codes: 0 pass, 1 fail (publishing should stop).
"""

from __future__ import annotations

import json
import os
import sys

BASE = os.path.dirname(os.path.abspath(__file__))

# Words that mean the generator started predicting instead of aggregating. They
# are banned in CLAUDE.md; this is the enforcement.
BANNED = ["预计", "有望", "值得关注", "值得留意", "或将", "建议买入", "建议配置",
          "目标价", "看涨", "看跌", "逢低", "抄底"]


def fail(message: str) -> None:
    print(f"  FAIL  {message}")


def main() -> int:
    day = sys.argv[1] if len(sys.argv) > 1 else ""
    if not day:
        briefs = sorted(f for f in os.listdir(os.path.join(BASE, "out"))
                        if f.startswith("brief-") and f.endswith(".json"))
        if not briefs:
            print("verify: out/ 下没有简报")
            return 1
        day = briefs[-1][len("brief-"):-len(".json")]

    path = os.path.join(BASE, "out", f"brief-{day}.json")
    raw_dir = os.path.join(BASE, "raw", day)

    print(f"verify {os.path.relpath(path, BASE)}")

    if not os.path.exists(path):
        fail(f"缺少 {path}")
        return 1

    # 1. Parseable at all.
    try:
        with open(path, encoding="utf-8") as fh:
            brief = json.load(fh)
    except json.JSONDecodeError as exc:
        fail(f"JSON 非法: {exc}")
        return 1

    problems = 0

    # 2. Required shape. A missing section is a real defect: it silently drops a
    #    whole class of information from the client.
    for key in ("trading_day", "market", "bullish", "bearish", "unverified", "counterpoint"):
        if key not in brief:
            fail(f"缺少字段 {key}")
            problems += 1

    # 3. Counterpoint is mandatory and non-empty — the section exists to offset
    #    confirmation bias, and an empty one defeats its whole purpose.
    if not brief.get("counterpoint"):
        fail("反方视角为空（该章节强制非空）")
        problems += 1

    # 4. Every citation resolves to a file actually written by the fetcher.
    unresolved = 0
    total = 0
    for section in ("bullish", "bearish", "unverified"):
        for item in brief.get(section, []):
            total += 1
            source = item.get("source", "")
            if not source or not os.path.exists(os.path.join(raw_dir, source)):
                unresolved += 1
                if unresolved <= 5:
                    fail(f"{section}: source='{source}' 不存在 — {item.get('text','')[:40]}")

    if unresolved:
        fail(f"共 {unresolved}/{total} 条无法溯源")
        problems += 1
    else:
        print(f"  ok    {total} 条线索全部可溯源到 raw/{day}/")

    # 5. Forbidden vocabulary anywhere in the text.
    blob = json.dumps(brief, ensure_ascii=False)
    hits = [word for word in BANNED if word in blob]
    if hits:
        fail(f"出现禁用词（属于预测/建议）：{'、'.join(hits)}")
        problems += 1
    else:
        print("  ok    无预测/建议类措辞")

    # 6. Numbers must match the fetched data, spot-checked on the indices — this
    #    is the one place a fabricated figure would be most plausible.
    data_indices = os.path.join(BASE, "data", day, "indices.json")
    if os.path.exists(data_indices):
        with open(data_indices, encoding="utf-8") as fh:
            truth = {row["label"]: row for row in json.load(fh)}

        mismatched = 0
        for index in brief.get("market", {}).get("indices", []):
            source_row = truth.get(index.get("label"))
            if not source_row:
                continue
            for field in ("price", "pct"):
                a, b = index.get(field), source_row.get(field)
                if isinstance(a, (int, float)) and isinstance(b, (int, float)) and abs(a - b) > 0.011:
                    fail(f"指数 {index.get('label')} 的 {field}: 简报 {a} ≠ data {b}")
                    mismatched += 1

        if mismatched:
            problems += 1
        else:
            print("  ok    指数数字与 data/ 一致")

    print("verify:", "通过" if problems == 0 else f"{problems} 项不通过")
    return 0 if problems == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())

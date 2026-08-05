#!/usr/bin/env bash
# Publishes the brief to the NAS, where any client can fetch it over HTTP.
#
# Same channel the updater already uses (nginx serves that directory), so a new
# machine needs nothing configured: install the exe, and 资讯 works. Pushing by
# scp to individual desktops only ever reached the machines this host happens to
# have SSH access to, which is not a distribution mechanism.
#
# Layout on the NAS:
#   /quoteview/brief/index.json           available days, newest first
#   /quoteview/brief/brief-YYYYMMDD.json  one day
set -uo pipefail

BASE="$(cd "$(dirname "$0")" && pwd)"
REMOTE="/vol3/1000/HDD2/tool/docker/nginx/html/quoteview/brief"
KEEP=30

DAY="${1:-}"
if [ -z "$DAY" ]; then
  DAY=$(ls -1 "$BASE/out" 2>/dev/null | sed -n 's/^brief-\([0-9]\{8\}\)\.json$/\1/p' | sort | tail -1)
fi

[ -z "$DAY" ] && { echo "publish: 没有可推送的简报"; exit 1; }

FILE="$BASE/out/brief-$DAY.json"
[ -f "$FILE" ] || { echo "publish: 缺少 $FILE"; exit 1; }

# Index built from what is actually on disk here, newest first, capped — the
# client uses it to discover days without guessing filenames.
python3 - "$BASE" "$KEEP" > "$BASE/out/index.json" <<'PY'
import json, os, re, sys
base, keep = sys.argv[1], int(sys.argv[2])
days = sorted({m.group(1) for f in os.listdir(os.path.join(base, "out"))
               if (m := re.fullmatch(r"brief-(\d{8})\.json", f))}, reverse=True)[:keep]
json.dump({"days": days, "latest": days[0] if days else None},
          sys.stdout, ensure_ascii=False, indent=1)
PY

ssh -o ConnectTimeout=10 nas "mkdir -p $REMOTE" || { echo "publish: NAS 不可达"; exit 1; }

if scp -q "$FILE" "$BASE/out/index.json" "nas:$REMOTE/"; then
  # 644 or nginx can't read them (the directory defaults to 700 → 403).
  ssh nas "chmod 644 $REMOTE/*.json"
  echo "  已发布 brief-$DAY.json + index.json"
else
  echo "  发布失败"
  exit 1
fi

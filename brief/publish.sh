#!/usr/bin/env bash
# Copies the day's brief to the machines that run QuoteView.
#
# NOT published to the NAS: the master copy stays in out/ here, and the client
# reads a plain local file. That keeps the brief off any public URL — it is an
# internal daily note, not a release artifact.
set -uo pipefail

BASE="$(cd "$(dirname "$0")" && pwd)"
DAY="${1:-}"

if [ -z "$DAY" ]; then
  DAY=$(ls -1 "$BASE/out" 2>/dev/null | sed -n 's/^brief-\([0-9]\{8\}\)\.json$/\1/p' | sort | tail -1)
fi

[ -z "$DAY" ] && { echo "publish: 没有可推送的简报"; exit 1; }

FILE="$BASE/out/brief-$DAY.json"
[ -f "$FILE" ] || { echo "publish: 缺少 $FILE"; exit 1; }

for host in corp-win pc-guan; do
  # cmd expands %APPDATA% itself, which avoids nesting PowerShell quoting inside
  # bash inside ssh — that nesting is what silently produced an empty path.
  dir=$(ssh -o ConnectTimeout=10 "$host" \
        'cmd /c "mkdir %APPDATA%\StockClient\brief 2>nul & echo %APPDATA%\StockClient\brief"' \
        2>/dev/null | tr -d '\r' | tail -1)

  if [ -z "$dir" ] || [ "${dir#*StockClient}" = "$dir" ]; then
    echo "  $host 不可达或路径异常，跳过"   # a desktop being off must not fail the run
    continue
  fi

  if scp -q "$FILE" "$host:${dir//\\//}/brief-$DAY.json"; then
    echo "  $host <- brief-$DAY.json  ($dir)"
  else
    echo "  $host 推送失败"
  fi
done

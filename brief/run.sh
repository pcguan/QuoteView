#!/usr/bin/env bash
# One daily run: fetch -> fetch -> generate.
#
# Written for cron, which means: no shell profile, no PATH, no proxy variables,
# no assumptions. Everything it needs is set here explicitly.
#
#   ./run.sh            full run
#   ./run.sh --fetch    fetch only, skip the brief (cheap, no LLM usage)
#   ./run.sh --force    run even on a non-trading day

set -uo pipefail

BASE="$(cd "$(dirname "$0")" && pwd)"
cd "$BASE" || exit 1

LOG="$BASE/run.log"
exec > >(tee -a "$LOG") 2>&1

echo "=============================================================="
echo "$(date '+%F %T %Z')  run.sh $*"

# cron has no environment: the proxy is required for every source, and its
# absence is the single most likely reason an unattended run fetches nothing.
export HTTP_PROXY="${HTTP_PROXY:-http://192.168.33.9:7890}"
export HTTPS_PROXY="${HTTPS_PROXY:-$HTTP_PROXY}"
export PATH="/root/.nvm/versions/node/v26.1.0/bin:/usr/local/bin:/usr/bin:/bin"

# Where the claude CLI keeps its credentials. Without it the CLI reports
# "Not logged in" and exits 1 — which is exactly how the first scheduled run
# failed: data and headlines were fetched fine, then the brief never generated.
# Same class of problem as the proxy: anything carried by an environment variable
# has to be stated explicitly, because cron provides none.
export CLAUDE_CONFIG_DIR="${CLAUDE_CONFIG_DIR:-/root/workspace/claude_code/.claude}"

FETCH_ONLY=0
FORCE=0
for arg in "$@"; do
  case "$arg" in
    --fetch) FETCH_ONLY=1 ;;
    --force) FORCE=1 ;;
  esac
done

# Weekend guard. Holidays are NOT hardcoded — fetch_market.py derives the trading
# day from the last index bar, so on a holiday it simply re-reports the previous
# session rather than inventing one. The weekend check is only here to avoid two
# pointless runs.
DOW=$(date +%u)
if [ "$FORCE" -eq 0 ] && [ "$DOW" -ge 6 ]; then
  echo "周末，跳过（--force 可强制）"
  exit 0
fi

python3 fetch_market.py || echo "WARN: fetch_market 非零退出"
echo
python3 fetch_news.py || echo "WARN: fetch_news 非零退出"
echo

if [ "$FETCH_ONLY" -eq 1 ]; then
  echo "--fetch：跳过简报生成"
  exit 0
fi

DAY=$(python3 - <<'PY'
import json, os, glob
dirs = sorted(glob.glob(os.path.join("data", "2*")))
print(os.path.basename(dirs[-1]) if dirs else "")
PY
)

if [ -z "$DAY" ]; then
  echo "FATAL: 没有可用的数据目录"
  exit 2
fi

echo "生成简报: $DAY"

# --max-turns bounds a runaway session; acceptEdits lets it write out/ without
# prompting (the permission file already restricts WHERE it can write).
claude -p "读取 data/$DAY/ 和 raw/$DAY/，按 CLAUDE.md 的章节结构生成 out/brief-$DAY.md 和 out/brief-$DAY.json。严格遵守 CLAUDE.md 的约束：数字只能来自 data/，每条线索必须标注 raw/ 下的来源文件名，禁止预测和建议。" \
  --allowedTools "Read,Write,Glob,Grep,WebSearch,Bash(python3 fetch_url.py:*)" \
  --permission-mode acceptEdits \
  --max-turns 60
STATUS=$?

# Exit-code guard: the subscription runs on a 5-hour rolling window and can be
# exhausted mid-run. A non-zero exit with no output file means exactly that, and
# must be visible in the log rather than silently leaving yesterday's brief in
# place.
if [ $STATUS -ne 0 ]; then
  echo "FATAL: claude 退出码 $STATUS（用量窗口耗尽？API 错误？）"
  exit $STATUS
fi

if [ ! -f "out/brief-$DAY.md" ]; then
  echo "FATAL: claude 退出码 0 但没有产出 out/brief-$DAY.md"
  exit 3
fi

# Machine checks before anything leaves this machine: the JSON is hand-written by
# the model, so malformed output and citations pointing at nonexistent files are
# both real possibilities. A brief that fails here is NOT distributed — the client
# keeps yesterday's, which is better than showing an unverifiable one.
echo
python3 "$BASE/verify.py" "$DAY"
if [ $? -ne 0 ]; then
  echo "FATAL: 简报未通过校验，不分发"
  exit 4
fi

echo
echo "完成: out/brief-$DAY.md ($(wc -c < "out/brief-$DAY.md") 字节)"

# Hand the JSON to the desktops that run QuoteView. Best effort: a machine being
# off is normal and must not turn a good run into a failed one.
echo "分发:"
"$BASE/publish.sh" "$DAY" || echo "  分发未完成（不影响本次生成）"

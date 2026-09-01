#!/usr/bin/env bash
# Installs the schedule. Re-run this after a container rebuild — cron is not a
# service that survives one, and neither is the crontab.
set -euo pipefail
BASE="$(cd "$(dirname "$0")" && pwd)"

service cron start >/dev/null 2>&1 || true

# The proxy address is not in this (public) repo and no longer in the crontab
# either: run.sh reads it from the untracked .env.local. Check it here so the
# schedule isn't installed to fail silently at 07:30.
[ -f "$BASE/.env.local" ] || {
  echo "缺 $BASE/.env.local —— 照 .env.local.example 填上代理地址再装"; exit 1; }

# CRON_TZ so the times mean Beijing regardless of the container's clock.
# 07:30 pre-open (yesterday's close is final by then), 15:30 post-close.
# Mon-Fri only; holidays need no rule because fetch_market.py derives the trading
# day from the last index bar, so a holiday run just re-reports the last session.
crontab - <<CRON
CRON_TZ=Asia/Shanghai
30 7 * * 1-5 $BASE/run.sh >> $BASE/cron.log 2>&1
30 15 * * 1-5 $BASE/run.sh >> $BASE/cron.log 2>&1
CRON

echo "已安装:"
crontab -l

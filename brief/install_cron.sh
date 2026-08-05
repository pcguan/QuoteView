#!/usr/bin/env bash
# Installs the schedule. Re-run this after a container rebuild — cron is not a
# service that survives one, and neither is the crontab.
set -euo pipefail
BASE="$(cd "$(dirname "$0")" && pwd)"

service cron start >/dev/null 2>&1 || true

# CRON_TZ so the times mean Beijing regardless of the container's clock.
# 07:30 pre-open (yesterday's close is final by then), 15:30 post-close.
# Mon-Fri only; holidays need no rule because fetch_market.py derives the trading
# day from the last index bar, so a holiday run just re-reports the last session.
crontab - <<CRON
CRON_TZ=Asia/Shanghai
HTTP_PROXY=http://192.168.33.9:7890
HTTPS_PROXY=http://192.168.33.9:7890
30 7 * * 1-5 $BASE/run.sh >> $BASE/cron.log 2>&1
30 15 * * 1-5 $BASE/run.sh >> $BASE/cron.log 2>&1
CRON

echo "已安装:"
crontab -l

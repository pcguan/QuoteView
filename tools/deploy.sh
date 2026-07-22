#!/usr/bin/env bash
# 固定工作流：源码只在本地；corp-win 编译+验证；两台桌面只放最终 exe。
#
#   ./tools/deploy.sh          编译 + 验证 + 同步到两台桌面
#   ./tools/deploy.sh --build  只编译验证，不同步
set -euo pipefail

LOCAL="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_HOST=corp-win
USER_HOST=pc-guan
REMOTE="C:/work/stock"

say() { printf '\n=== %s ===\n' "$1"; }

# Windows locks a running exe, so the Desktop copy can't land while the app is
# up — deploy has to stop it. Say so out loud: killing it silently is why the
# stealth panel looked like it "just vanished" mid-use.
stop_running() {
  local host=$1
  if ssh "$host" "tasklist /fi \"imagename eq StockClient.exe\" | find /i \"StockClient.exe\"" >/dev/null 2>&1; then
    printf '  ! %s 上 StockClient 正在运行，已停止以便覆盖 exe（部署后需手动重开）\n' "$host"
    ssh "$host" "taskkill /f /im StockClient.exe" >/dev/null 2>&1 || true
  fi
}

say "同步源码到编译机 $BUILD_HOST"
ssh "$BUILD_HOST" "if not exist C:\\work\\stock mkdir C:\\work\\stock" >/dev/null 2>&1
stop_running "$BUILD_HOST"
scp -q -r "$LOCAL/src" "$LOCAL/tools" "$BUILD_HOST:$REMOTE/"

say "数据层验证（打真实行情接口）"
ssh "$BUILD_HOST" "cd C:\\work\\stock\\tools\\Smoke && dotnet run -c Release" 2>&1 | tail -20

say "编译 exe（framework-dependent，约 7MB）"
ssh "$BUILD_HOST" "cd C:\\work\\stock\\src\\StockClient.App && dotnet publish -c Release -r win-x64 --self-contained false -o C:\\work\\stock\\dist" 2>&1 \
  | grep -iE "error|dist" || true

if [[ "${1:-}" == "--build" ]]; then
  echo "(--build：跳过同步)"
  exit 0
fi

say "同步 exe 到两台桌面"
# 编译机自己的桌面
ssh "$BUILD_HOST" "powershell -NoProfile -Command \"\$d=[Environment]::GetFolderPath('Desktop'); Copy-Item C:\\work\\stock\\dist\\StockClient.exe \$d\\StockClient.exe -Force\""

# 用户机：只经桌面落地，不留源码
TMP=$(mktemp -d)
scp -q "$BUILD_HOST:$REMOTE/dist/StockClient.exe" "$TMP/StockClient.exe"
USER_DESKTOP=$(ssh "$USER_HOST" "powershell -NoProfile -Command \"[Environment]::GetFolderPath('Desktop')\"" | tr -d '\r')
stop_running "$USER_HOST"
scp -q "$TMP/StockClient.exe" "$USER_HOST:${USER_DESKTOP//\\//}/StockClient.exe"
rm -rf "$TMP"

say "结果（两台哈希必须一致，否则是传输被截断）"
for h in "$BUILD_HOST" "$USER_HOST"; do
  printf '%-10s ' "$h"
  ssh "$h" "powershell -NoProfile -Command \"\$f=[Environment]::GetFolderPath('Desktop')+'\\StockClient.exe'; \$i=Get-Item \$f; '{0}  {1:N1} MB  {2:yyyy-MM-dd HH:mm:ss}' -f \$i.FullName, (\$i.Length/1MB), \$i.LastWriteTime\"" | tr -d '\r'
done

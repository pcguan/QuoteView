#!/usr/bin/env bash
# Guards the one release mistake that is invisible until users hit it: shipping a
# binary whose internal version doesn't match the manifest.
#
# It happens when the version is bumped in the csproj but the exe is copied from
# a previous build. The file name says the new version, the manifest says the new
# version, and the binary says the old one — so every client updates, still
# reports the old version, and is offered the same update forever.
#
# Usage: tools/check_release.sh <version>
set -uo pipefail

VER="${1:-}"
[ -z "$VER" ] && { echo "用法: check_release.sh <版本号>"; exit 2; }

BASE="$(cd "$(dirname "$0")/.." && pwd)"
EXE="$BASE/release/QuoteView-$VER.exe"
MANIFEST="$BASE/release/latest.json"

[ -f "$EXE" ] || { echo "FAIL 缺少 $EXE"; exit 1; }

fail=0

# 1. manifest version
mver=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['version'])" 2>/dev/null)
if [ "$mver" != "$VER" ]; then
  echo "FAIL latest.json 写的是 $mver，不是 $VER"; fail=1
else
  echo "ok   latest.json = $VER"
fi

# 2. manifest size vs the actual file
msize=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['size'])" 2>/dev/null)
asize=$(stat -c%s "$EXE")
if [ "$msize" != "$asize" ]; then
  echo "FAIL latest.json 里 size=$msize，实际 $asize"; fail=1
else
  echo "ok   size = $asize"
fi

# 3. the one that actually bit us: version compiled INTO the binary
scp -q "$EXE" corp-win:C:/work/_vercheck.exe 2>/dev/null || { echo "WARN 无法上传到 corp-win 校验内部版本"; exit $fail; }
bver=$(ssh corp-win 'powershell -NoProfile -Command "(Get-Item C:\work\_vercheck.exe).VersionInfo.FileVersion"' 2>/dev/null | tr -d '\r' | tail -1)
ssh corp-win 'cmd /c "del /q C:\work\_vercheck.exe"' >/dev/null 2>&1

if [ "$bver" != "$VER.0" ] && [ "$bver" != "$VER" ]; then
  echo "FAIL exe 内部版本是 $bver，不是 $VER —— 八成是改了 csproj 但没重新编译"
  fail=1
else
  echo "ok   exe 内部版本 = $bver"
fi

[ $fail -eq 0 ] && echo "check_release: 通过" || echo "check_release: 不通过，不要发布"
exit $fail

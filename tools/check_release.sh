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
fi
# 2b. 尺寸下限：manifest 的 size 是从本地文件算的，scp 截断时两者会“自洽地”一致
# ——一次 977KB 半截 exe 就这样骗过了上面的比对。真实产物 ~7.4MB，低于下限必是残件。
if [ "$asize" -lt 6000000 ]; then
  echo "FAIL exe 只有 $asize 字节（<6MB 下限），像是传输被截断的残件——重新从 corp-win 拉取"; fail=1
else
  echo "ok   size = $asize"
fi

# 3. the one that actually bit us: version compiled INTO the binary.
# Read straight out of the PE version resource here, rather than asking corp-win
# for VersionInfo.FileVersion: the tunnel to that machine drops often enough that
# the check kept degrading to a WARN, and a check that skips itself when the
# network hiccups is not a gate. VS_FIXEDFILEINFO starts at signature 0xFEEF04BD;
# the two DWORDs following it and dwStrucVersion are the file version, high word first.
bver=$(python3 - "$EXE" <<'PYVER' 2>/dev/null
import struct, sys
data = open(sys.argv[1], "rb").read()
at = data.find(b"\xbd\x04\xef\xfe")
if at >= 0:
    ms, ls = struct.unpack_from("<II", data, at + 8)
    print(f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}")
PYVER
)

if [ -z "$bver" ]; then
  echo "FAIL 读不出 exe 内部版本（版本资源缺失？）"; fail=1
elif [ "$bver" != "$VER.0" ] && [ "$bver" != "$VER" ]; then
  echo "FAIL exe 内部版本是 $bver，不是 $VER —— 八成是改了 csproj 但没重新编译"
  fail=1
else
  echo "ok   exe 内部版本 = $bver"
fi

[ $fail -eq 0 ] && echo "check_release: 通过" || echo "check_release: 不通过，不要发布"
exit $fail

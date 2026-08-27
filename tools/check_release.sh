#!/usr/bin/env bash
# Release gate. Two failure classes it exists to stop:
#   1. Stale binary: version bumped in csproj but the exe came from an old build
#      (filename + manifest claim the new version, the binary reports the old).
#   2. Truncated sync: the scp from the build machine broke mid-transfer and the
#      local "latest.json size/sha" was then GENERATED FROM the truncated file —
#      self-consistent, and it shipped a 977KB stub that took a client down.
#
# The fix for (2) is a BUILD MANIFEST written ON THE BUILD MACHINE right after
# compilation (release/build.json: version/size/sha256). The local artifact must
# match that manifest byte-for-byte; a missing manifest IS a failed sync.
#
# Usage: tools/check_release.sh <version>
set -uo pipefail

VER="${1:-}"
[ -z "$VER" ] && { echo "用法: check_release.sh <版本号>"; exit 2; }

BASE="$(cd "$(dirname "$0")/.." && pwd)"
EXE="$BASE/release/QuoteView-$VER.exe"
MANIFEST="$BASE/release/latest.json"
BUILD="$BASE/release/build.json"

[ -f "$EXE" ] || { echo "FAIL 缺少 $EXE"; exit 1; }

fail=0

# 0. The build manifest, written on corp-win at compile time. Missing = the
#    sync failed (or the flow skipped a step) — hard stop, no degradation.
if [ ! -f "$BUILD" ]; then
  echo "FAIL 缺少 release/build.json（编译机构建清单）——按 RELEASE.md 步骤 2 生成并同步"; fail=1
  bver_m=""; bsize=""; bsha=""
else
  bver_m=$(python3 -c "import json;print(json.load(open('$BUILD'))['version'])" 2>/dev/null)
  bsize=$(python3 -c "import json;print(json.load(open('$BUILD'))['size'])" 2>/dev/null)
  bsha=$(python3 -c "import json;print(json.load(open('$BUILD'))['sha256'].lower())" 2>/dev/null)
fi

asize=$(stat -c%s "$EXE")
asha=$(sha256sum "$EXE" | cut -d' ' -f1)

# 1. Local artifact vs build manifest — the sync-integrity gate.
if [ -n "$bsha" ]; then
  case "$bver_m" in
    "$VER"|"$VER.0") echo "ok   build.json 版本 = $bver_m" ;;
    *) echo "FAIL build.json 版本是 $bver_m，不是 $VER —— 编译机上的产物不是这一版"; fail=1 ;;
  esac
  if [ "$bsize" != "$asize" ]; then
    echo "FAIL 本地 exe $asize 字节 ≠ 编译机产物 $bsize 字节 —— 同步被截断，重新拉取"; fail=1
  fi
  if [ "$bsha" != "$asha" ]; then
    echo "FAIL 本地 exe SHA-256 与编译机产物不符 —— 同步损坏，重新拉取"; fail=1
  else
    echo "ok   sha256 与编译机一致 = ${asha:0:16}…"
  fi
fi

# 2. latest.json consistency (version / size / sha256 must all describe THIS exe).
mver=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['version'])" 2>/dev/null)
msize=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['size'])" 2>/dev/null)
msha=$(python3 -c "import json;print(json.load(open('$MANIFEST')).get('sha256','').lower())" 2>/dev/null)
[ "$mver" != "$VER" ] && { echo "FAIL latest.json 写的是 $mver，不是 $VER"; fail=1; } \
  || echo "ok   latest.json = $VER"
[ "$msize" != "$asize" ] && { echo "FAIL latest.json size=$msize，实际 $asize"; fail=1; } \
  || echo "ok   size = $asize"
if [ -z "$msha" ]; then
  echo "FAIL latest.json 缺 sha256 字段（客户端靠它校验下载完整性）"; fail=1
elif [ "$msha" != "$asha" ]; then
  echo "FAIL latest.json sha256 与 exe 不符"; fail=1
else
  echo "ok   latest.json sha256 一致"
fi

# 3. Version compiled INTO the binary, read from the local PE version resource
#    (VS_FIXEDFILEINFO, signature 0xFEEF04BD) — no dependency on the corp-win
#    tunnel, so this check can never degrade into a skipped WARN.
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
  echo "FAIL exe 内部版本是 $bver，不是 $VER —— 八成是改了 csproj 但没重新编译"; fail=1
else
  echo "ok   exe 内部版本 = $bver"
fi

[ $fail -eq 0 ] && echo "check_release: 通过" || echo "check_release: 不通过，不要发布"
exit $fail

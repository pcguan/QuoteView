#!/usr/bin/env bash
# 一键发版：测试 → corp-win 编译 → 构建清单 → 回传 → check_release → commit+push
# → NAS + GitHub 双源 → 公网核验。任何一步失败即中止，残件不出门。
#
# 用法:  tools/release.sh <版本> <发布说明> [提交信息]
#   版本      必须与 csproj 的 <Version> 一致（脚本核验，不代改）
#   发布说明  写入 latest.json 的 notes 与 GitHub 正文
#   提交信息  省略时用 "v<版本> <发布说明>"
#
#        tools/release.sh --rollback <回退到的版本> <坏版本> [说明]
#   坏版本已经铺出去时的撤回手段：把 latest.json 指回旧版并打上 force，
#   同时删掉坏版本的 GitHub release+tag（否则 NAS 宕机时兜底源会把它再装回去）。
#
# GitHub 令牌从 `git config qv.ghtoken` 或环境变量 QV_GH_TOKEN 读取
# （均不在被跟踪文件里；仓库是公开的）。
set -euo pipefail
cd "$(dirname "$0")/.."

NAS_HOST=nas
NAS_DIR=/vol3/1000/HDD2/tool/docker/nginx/html/quoteview
NAS_URL=https://nas.pcguan.cn/quoteview
REPO=pcguan/QuoteView
TOKEN="${QV_GH_TOKEN:-$(git config qv.ghtoken || true)}"

step() { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }

gh_api() { curl -s -H "Authorization: token $TOKEN" -H "User-Agent: sc" "$@"; }

# 带 HTTP 码的调用：把码打在末行，调用方必须判。裸 curl -s 在 401/403/网络失败
# 时也返回 0 且吐一段 JSON，回退流程曾因此把"删不掉"读成"已经删过了"。
gh_code() {
  curl -s -o /tmp/qv-gh-body.$$ -w '%{http_code}' \
    -H "Authorization: token $TOKEN" -H "User-Agent: sc" "$@"
}
gh_body() { cat /tmp/qv-gh-body.$$ 2>/dev/null; }

# NAS 上的字节才是客户端真正下载的东西：同一条隧道的 scp 截断过一次
# （2026-08-27 的 977KB 残件），而 latest.json 只要正常，整条链看上去全绿。
nas_sha() { ssh "$NAS_HOST" "sha256sum '$NAS_DIR/$1' 2>/dev/null" | cut -d' ' -f1; }
nas_size() { ssh "$NAS_HOST" "stat -c%s '$NAS_DIR/$1' 2>/dev/null"; }
public_size() {
  curl -sI "$NAS_URL/$1" | tr -d '\r' | awk 'tolower($1)=="content-length:"{print $2}' | tail -1
}

# ---------------------------------------------------------------- 回退（撤回）
if [ "${1:-}" = "--rollback" ]; then
  VER="${2:?用法: tools/release.sh --rollback <回退到的版本> <坏版本> [说明]}"
  BAD="${3:?缺少要撤回的坏版本}"
  NOTES="${4:-回退到 v$VER}"
  [ -n "$TOKEN" ] || { echo "缺 GitHub 令牌（git config qv.ghtoken 或 QV_GH_TOKEN）"; exit 1; }
  [ "$VER" != "$BAD" ] || { echo "回退目标与坏版本相同 — 中止"; exit 1; }

  step "1/4 取 NAS 上 v$VER 的实际字节"
  RSHA=$(nas_sha "QuoteView-$VER.exe") || true
  RSIZE=$(nas_size "QuoteView-$VER.exe") || true
  [ -n "$RSHA" ] && [ -n "$RSIZE" ] \
    || { echo "NAS 上没有 QuoteView-$VER.exe — 回退目标必须是已经发布过的版本"; exit 1; }
  echo "sha256=$RSHA  size=$RSIZE"

  step "2/4 latest.json 指回 v$VER（force）"
  # force 让客户端接受"版本号变小"的这一次更新；下一次正常发版重写 latest.json
  # 时不带该字段，回退状态自然结束，不需要手工清理。
  jq -n --arg v "$VER" --arg n "$NOTES" --arg sha "$RSHA" --argjson size "$RSIZE" \
    --arg url "$NAS_URL/QuoteView-$VER.exe" --arg d "$(date +%F)" \
    '{version:$v, url:$url, size:$size, sha256:$sha, notes:$n, published:$d, force:true}' \
    > release/latest.json
  scp -q release/latest.json "$NAS_HOST:$NAS_DIR/"
  ssh "$NAS_HOST" "chmod 644 $NAS_DIR/latest.json"

  step "3/4 撤回 GitHub 的 v$BAD"
  # 这一步失败必须中止：坏版本留在兜底源上，客户端会在 NAS 与 GitHub 之间
  # 反复降级/升级，比不回退更糟。
  CODE=$(gh_code "https://api.github.com/repos/$REPO/releases/tags/v$BAD")
  case "$CODE" in
    200)
      BID=$(gh_body | jq -r '.id')
      for path in "releases/$BID" "git/refs/tags/v$BAD"; do
        DC=$(gh_code -X DELETE "https://api.github.com/repos/$REPO/$path")
        case "$DC" in
          204|404) ;;
          *) echo "❌ 删除 $path 失败（HTTP $DC）：$(gh_body | jq -r '.message // .')"; exit 1 ;;
        esac
      done
      echo "已删除 release v$BAD 及其 tag（兜底源随之回落到上一版）"
      ;;
    404) echo "GitHub 上没有 v$BAD（已删过）— 跳过" ;;
    *) echo "❌ 查询 v$BAD 失败（HTTP $CODE）：$(gh_body | jq -r '.message // .')"; exit 1 ;;
  esac

  step "4/4 公网核验"
  LJ=$(curl -s "$NAS_URL/latest.json?t=$(date +%s)")
  echo "NAS   : $(echo "$LJ" | jq -c '{version,sha256,force}')"
  fail=0
  [ "$(echo "$LJ" | jq -r .version)" = "$VER" ] || { echo "❌ latest.json 版本不是 $VER"; fail=1; }
  [ "$(echo "$LJ" | jq -r .sha256)" = "$RSHA" ] || { echo "❌ latest.json sha256 与 NAS 上的 exe 不符"; fail=1; }
  [ "$(echo "$LJ" | jq -r '.force')" = "true" ] || { echo "❌ latest.json 缺 force，客户端不会降级"; fail=1; }
  [ "$(public_size "QuoteView-$VER.exe")" = "$RSIZE" ] || { echo "❌ 公网取到的 exe 长度与 NAS 上不符"; fail=1; }

  # 兜底源也要核：NAS 回退了而 GitHub 还挂着坏版本，等于没回退。
  GLATEST=$(curl -s -H "User-Agent: sc" "https://api.github.com/repos/$REPO/releases/latest" | jq -r '.tag_name // ""')
  GBAD=$(gh_code "https://api.github.com/repos/$REPO/releases/tags/v$BAD")
  echo "GitHub: latest=$GLATEST  v$BAD=HTTP $GBAD"
  [ "$GLATEST" != "v$BAD" ] || { echo "❌ GitHub 的 latest 仍是坏版本 v$BAD"; fail=1; }
  [ "$GBAD" = "404" ] || { echo "❌ GitHub 上的 v$BAD 仍在（HTTP $GBAD）"; fail=1; }
  [ $fail -eq 0 ] || exit 1
  rm -f /tmp/qv-gh-body.$$
  echo "✅ 已回退到 v$VER；在线客户端下一轮检查（30s）内拉回"
  exit 0
fi

# ---------------------------------------------------------------- 正常发版
VER="${1:?用法: tools/release.sh <版本> <发布说明> [提交信息]}"
NOTES="${2:?缺少发布说明}"
MSG="${3:-v$VER $NOTES}"

step "0/8 版本核验"
grep -q "<Version>$VER</Version>" src/StockClient.App/StockClient.App.csproj \
  || { echo "csproj 的 <Version> 不是 $VER — 先抬版本再发"; exit 1; }
[ -n "$TOKEN" ] || { echo "缺 GitHub 令牌（git config qv.ghtoken 或 QV_GH_TOKEN）"; exit 1; }

step "1/8 同步源码到 corp-win"
scp -q -r src tools corp-win:C:/work/stock/

step "2/8 单元测试（corp-win）"
ssh corp-win "cd C:\\work\\stock\\src\\StockClient.Tests && dotnet test -v q --nologo" \
  || { echo "测试失败 — 中止发版"; exit 1; }

step "3/8 编译 + 构建清单（corp-win）"
# 先清旧 dist：publish 没跑成而上一次的 dist 还在时，后面的清单/回传会照常从陈旧
# 的 exe 走完全程——版本号相同的陈旧产物正是 check_release 唯一拦不住的一类。
ssh corp-win "if exist C:\\work\\stock\\dist rmdir /s /q C:\\work\\stock\\dist" || true
# 判定只看退出码：ssh 会透传远端退出码（连不上是 255），比 grep 文本可靠——
# 隧道半途断连、输出被截断时，文本判定会把失败当成功放行。
ssh corp-win "cd C:\\work\\stock\\src\\StockClient.App && dotnet publish -c Release -r win-x64 --self-contained false -o C:\\work\\stock\\dist" \
  || { echo "编译或连接失败 — 中止"; exit 1; }
ssh corp-win "powershell -c \"\$f=Get-Item C:\\work\\stock\\dist\\QuoteView.exe; @{version=\$f.VersionInfo.FileVersion; size=\$f.Length; sha256=(Get-FileHash \$f.FullName -Algorithm SHA256).Hash.ToLower()} | ConvertTo-Json -Compress | Set-Content C:\\work\\stock\\dist\\build.json\""

step "4/8 产物回传 + 清理"
scp -q corp-win:C:/work/stock/dist/QuoteView.exe "release/QuoteView-$VER.exe"
scp -q corp-win:C:/work/stock/dist/build.json release/build.json
ssh corp-win "rmdir /s /q C:\\work\\stock\\dist"
SIZE=$(jq -r .size release/build.json)
SHA=$(jq -r .sha256 release/build.json)

step "5/8 latest.json + check_release"
# 正常发版永远不写 force：它是回退的一次性状态，留着会把手装了更高测试版的机器一直拽回来。
jq -n --arg v "$VER" --arg n "$NOTES" --arg sha "$SHA" --argjson size "$SIZE" \
  --arg url "$NAS_URL/QuoteView-$VER.exe" \
  --arg d "$(date +%F)" \
  '{version:$v, url:$url, size:$size, sha256:$sha, notes:$n, published:$d}' > release/latest.json
tools/check_release.sh "$VER"

step "6/8 commit + push"
git add -A
git commit -m "$MSG" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>" || echo "（无改动可提交，跳过）"
git push

step "7/8 双源发布"
scp -q "release/QuoteView-$VER.exe" release/latest.json "$NAS_HOST:$NAS_DIR/"
ssh "$NAS_HOST" "chmod 644 $NAS_DIR/*"
RSHA=$(nas_sha "QuoteView-$VER.exe") || true
[ "$RSHA" = "$SHA" ] \
  || { echo "NAS 上 QuoteView-$VER.exe 的 sha256 是 ${RSHA:-空}，本地是 $SHA — 上传被截断，重传"; exit 1; }
echo "ok   NAS exe sha256 一致"

BODY="$NOTES

SHA256: $SHA"
# release 可能已经存在（上一次跑到一半失败）：重跑必须能续，不能死在 422 already_exists。
RID=$(gh_api "https://api.github.com/repos/$REPO/releases/tags/v$VER" | jq -r '.id // empty')
if [ -n "$RID" ]; then
  echo "GitHub 上已有 v$VER（id=$RID），沿用；正文与资产按本次产物覆盖"
  # 半截上传留下的同名资产会让新上传 422，先删干净。
  for AID in $(gh_api "https://api.github.com/repos/$REPO/releases/$RID/assets" \
               | jq -r '.[] | select(.name=="QuoteView.exe") | .id'); do
    gh_api -X DELETE "https://api.github.com/repos/$REPO/releases/$RID/assets/$AID" >/dev/null
  done
  # 正文里的 SHA256 行是兜底源唯一的下载校验锚点，重跑时同样要更新。
  jq -n --arg b "$BODY" '{body:$b}' \
    | gh_api -X PATCH "https://api.github.com/repos/$REPO/releases/$RID" --data-binary @- >/dev/null
else
  RESP=$(jq -n --arg t "v$VER" --arg b "$BODY" \
           '{tag_name:$t, target_commitish:"main", name:$t, body:$b}' \
         | gh_api -X POST "https://api.github.com/repos/$REPO/releases" --data-binary @-)
  RID=$(echo "$RESP" | jq -r '.id // empty')
  [ -n "$RID" ] \
    || { echo "GitHub release 创建失败：$(echo "$RESP" | jq -r '.message // "无响应"')"; exit 1; }
fi
ASSET=$(curl -s -X POST -H "Authorization: token $TOKEN" -H "User-Agent: sc" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @"release/QuoteView-$VER.exe" \
  "https://uploads.github.com/repos/$REPO/releases/$RID/assets?name=QuoteView.exe")
STATE=$(echo "$ASSET" | jq -r '.state // empty')
ASIZE=$(echo "$ASSET" | jq -r '.size // empty')
[ "$STATE" = "uploaded" ] \
  || { echo "GitHub 资产上传失败（state=${STATE:-无}）：$(echo "$ASSET" | jq -r '.message // ""')"; exit 1; }
[ "$ASIZE" = "$SIZE" ] || { echo "GitHub 资产 $ASIZE 字节 ≠ 本地 $SIZE 字节 — 上传被截断"; exit 1; }

step "8/8 公网核验"
LJ=$(curl -s "$NAS_URL/latest.json?t=$(date +%s)")
NV=$(echo "$LJ" | jq -r .version)
NS=$(echo "$LJ" | jq -r .sha256)
CL=$(public_size "QuoteView-$VER.exe")
sleep 5
GJ=$(curl -s -H "User-Agent: sc" "https://api.github.com/repos/$REPO/releases/latest")
GT=$(echo "$GJ" | jq -r .tag_name)
GS=$(echo "$GJ" | jq -r '.body // "" | capture("SHA256: (?<h>[0-9a-f]{64})").h' 2>/dev/null || true)
GA=$(echo "$GJ" | jq -r '[.assets[]? | select(.name=="QuoteView.exe")] | length')
echo "NAS   : $NV $NS  exe=$CL 字节"
echo "GitHub: $GT $GS  QuoteView.exe × $GA"
fail=0
[ "$NV" = "$VER" ] || { echo "❌ NAS latest.json 版本是 $NV"; fail=1; }
[ "$NS" = "$SHA" ] || { echo "❌ NAS latest.json sha256 与本次产物不符"; fail=1; }
# 公网这一腿单独查 exe 本体：latest.json 正确而 exe 是残件时，客户端会一直卡在校验失败。
[ "$CL" = "$SIZE" ] || { echo "❌ 公网 exe $CL 字节 ≠ $SIZE — NAS 上是残件或 nginx 读不到"; fail=1; }
[ "$GT" = "v$VER" ] || { echo "❌ GitHub 最新 release 是 $GT"; fail=1; }
[ "$GS" = "$SHA" ] || { echo "❌ GitHub 正文缺 SHA256 行或不符 — 兜底源无法校验下载"; fail=1; }
[ "$GA" = "1" ] || { echo "❌ GitHub release 里没有 QuoteView.exe 资产 — 兜底源整段失效"; fail=1; }
[ $fail -eq 0 ] || exit 1
echo "✅ v$VER 双源一致，发布完成"

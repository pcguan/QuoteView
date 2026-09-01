#!/usr/bin/env bash
# 一键发版：测试 → corp-win 编译 → 构建清单 → 回传 → check_release → commit+push
# → NAS + GitHub 双源 → 公网核验。任何一步失败即中止，残件不出门。
#
# 用法:  tools/release.sh <版本> <发布说明> [提交信息]
#   版本      必须与 csproj 的 <Version> 一致（脚本核验，不代改）
#   发布说明  写入 latest.json 的 notes 与 GitHub 正文
#   提交信息  省略时用 "v<版本> <发布说明>"
#
# GitHub 令牌从 `git config qv.ghtoken` 或环境变量 QV_GH_TOKEN 读取
# （均不在被跟踪文件里；仓库是公开的）。
set -euo pipefail
cd "$(dirname "$0")/.."

VER="${1:?用法: tools/release.sh <版本> <发布说明> [提交信息]}"
NOTES="${2:?缺少发布说明}"
MSG="${3:-v$VER $NOTES}"
NAS_DIR=/vol3/1000/HDD2/tool/docker/nginx/html/quoteview
TOKEN="${QV_GH_TOKEN:-$(git config qv.ghtoken || true)}"

step() { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }

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
ssh corp-win "cd C:\\work\\stock\\src\\StockClient.App && dotnet publish -c Release -r win-x64 --self-contained false -o C:\\work\\stock\\dist" \
  | grep -iE "error|错误" && { echo "编译报错 — 中止"; exit 1; } || true
ssh corp-win "powershell -c \"\$f=Get-Item C:\\work\\stock\\dist\\QuoteView.exe; @{version=\$f.VersionInfo.FileVersion; size=\$f.Length; sha256=(Get-FileHash \$f.FullName -Algorithm SHA256).Hash.ToLower()} | ConvertTo-Json -Compress | Set-Content C:\\work\\stock\\dist\\build.json\""

step "4/8 产物回传 + 清理"
scp -q corp-win:C:/work/stock/dist/QuoteView.exe "release/QuoteView-$VER.exe"
scp -q corp-win:C:/work/stock/dist/build.json release/build.json
ssh corp-win "rmdir /s /q C:\\work\\stock\\dist"
SIZE=$(jq -r .size release/build.json)
SHA=$(jq -r .sha256 release/build.json)

step "5/8 latest.json + check_release"
jq -n --arg v "$VER" --arg n "$NOTES" --arg sha "$SHA" --argjson size "$SIZE" \
  --arg url "https://nas.pcguan.cn/quoteview/QuoteView-$VER.exe" \
  --arg d "$(date +%F)" \
  '{version:$v, url:$url, size:$size, sha256:$sha, notes:$n, published:$d}' > release/latest.json
tools/check_release.sh "$VER"

step "6/8 commit + push"
git add -A
git commit -m "$MSG" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>" || echo "（无改动可提交，跳过）"
git push

step "7/8 双源发布"
scp -q "release/QuoteView-$VER.exe" release/latest.json "nas:$NAS_DIR/"
ssh nas "chmod 644 $NAS_DIR/*"
BODY="$NOTES

SHA256: $SHA"
RID=$(jq -n --arg t "v$VER" --arg b "$BODY" \
        '{tag_name:$t, target_commitish:"main", name:$t, body:$b}' \
      | curl -s -X POST -H "Authorization: token $TOKEN" -H "User-Agent: sc" \
          https://api.github.com/repos/pcguan/QuoteView/releases --data-binary @- \
      | jq -r '.id')
[ "$RID" != "null" ] || { echo "GitHub release 创建失败"; exit 1; }
STATE=$(curl -s -X POST -H "Authorization: token $TOKEN" -H "User-Agent: sc" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @"release/QuoteView-$VER.exe" \
  "https://uploads.github.com/repos/pcguan/QuoteView/releases/$RID/assets?name=QuoteView.exe" \
  | jq -r '.state')
[ "$STATE" = "uploaded" ] || { echo "GitHub 资产上传失败（state=$STATE）"; exit 1; }

step "8/8 公网核验"
NV=$(curl -s https://nas.pcguan.cn/quoteview/latest.json | jq -r .version)
NS=$(curl -s https://nas.pcguan.cn/quoteview/latest.json | jq -r .sha256)
sleep 5
GT=$(curl -s -H "User-Agent: sc" https://api.github.com/repos/pcguan/QuoteView/releases/latest | jq -r .tag_name)
GS=$(curl -s -H "User-Agent: sc" https://api.github.com/repos/pcguan/QuoteView/releases/latest \
     | jq -r '.body | capture("SHA256: (?<h>[0-9a-f]{64})").h')
echo "NAS   : $NV $NS"
echo "GitHub: $GT $GS"
[ "$NV" = "$VER" ] && [ "$NS" = "$SHA" ] && [ "$GT" = "v$VER" ] && [ "$GS" = "$SHA" ] \
  && echo "✅ v$VER 双源一致，发布完成" \
  || { echo "❌ 双源核验不一致"; exit 1; }

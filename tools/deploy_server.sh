#!/usr/bin/env bash
# 服务端发布：scp → 逐文件哈希比对 → 容器内语法门 → force-recreate → 健康检查。
# 走的是和 exe 同一条会截断的隧道（docs/RELEASE.md），而 server.py 是单文件、
# 残缺即 crash-loop，sync/trend/kline/clientlog 全线不可用——所以每一腿都要核。
#
# 用法:  tools/deploy_server.sh [--env]
#   --env  连 cfg/server.env 一起推。默认不推：NAS 上那份带真实 QV_ADMIN_PASSWORD，
#          仓库这份是占位，覆盖会把管理台口令清空。
#
# 回滚：仓库就是部署源——`git checkout <上一提交> -- server/` 后重跑本脚本。
set -euo pipefail
cd "$(dirname "$0")/.."

NAS_HOST=nas
REMOTE=/vol3/1000/HDD2/tool/docker/quoteview-server
STATUS_URL=https://nas.pcguan.cn/quoteview/api/status
WITH_ENV=0
[ "${1:-}" = "--env" ] && WITH_ENV=1

# 镜像与端口从部署源本身读，避免和 compose/env 两处各写一份。
IMAGE=$(awk '/^[[:space:]]*image:[[:space:]]/{print $2; exit}' server/docker-compose.yml)
PORT=$(awk -F= '/^QV_PORT=/{print $2; exit}' server/cfg/server.env | tr -d '[:space:]')

step() { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }

# 传完就核：截断的 scp 不会报错，只会让下一步的容器起不来。
verify() {
  local src=$1 dst=$2 lsha rsha
  lsha=$(sha256sum "$src" | cut -d' ' -f1)
  rsha=$(ssh "$NAS_HOST" "sha256sum '$dst' 2>/dev/null" | cut -d' ' -f1) || true
  [ "$lsha" = "$rsha" ] \
    || { echo "FAIL $dst 与本地不符（远端 ${rsha:-缺失}）— 传输被截断，重跑"; exit 1; }
  echo "ok   ${dst#"$REMOTE"/}"
}

step "1/4 同步部署源"
scp -q server/docker-compose.yml "$NAS_HOST:$REMOTE/"
scp -q server/scripts/server.py "$NAS_HOST:$REMOTE/scripts/"
verify server/docker-compose.yml "$REMOTE/docker-compose.yml"
verify server/scripts/server.py "$REMOTE/scripts/server.py"
if [ "$WITH_ENV" -eq 1 ]; then
  echo "! 覆盖 cfg/server.env —— NAS 上的 QV_ADMIN_PASSWORD 会被仓库里的占位值替换"
  scp -q server/cfg/server.env "$NAS_HOST:$REMOTE/cfg/"
  verify server/cfg/server.env "$REMOTE/cfg/server.env"
fi

step "2/4 语法门（一次性容器，不碰运行中的服务）"
# NAS 宿主机不一定有 python3，用服务自己的镜像跑；scripts 只读挂载，字节码写 /tmp。
ssh "$NAS_HOST" "docker run --rm -v '$REMOTE/scripts:/scripts:ro' --entrypoint python3 $IMAGE \
  -c 'import py_compile; py_compile.compile(\"/scripts/server.py\", cfile=\"/tmp/server.pyc\", doraise=True)'" \
  || { echo "FAIL server.py 语法不过（或残缺）— 未重启容器，线上仍是旧版"; exit 1; }
echo "ok   py_compile"

step "3/4 重建容器"
# 名字冲突 = NAS 上跑着一个手工 docker run 出来的同名容器（compose 不认它）。
# 不自动 rm：那会误删别人手工起的东西；提示清楚，由人确认后再跑。
if ! ssh "$NAS_HOST" "cd $REMOTE && docker compose up -d --force-recreate" 2>&1 | tee /tmp/qv-compose.$$; then
  if grep -q "already in use" /tmp/qv-compose.$$; then
    echo
    echo "❌ 同名容器不是 compose 创建的（多半是谁手工 docker run 起的）。"
    echo "   确认它就是本服务后： ssh $NAS_HOST 'docker rm -f quoteview-server' 再重跑本脚本。"
    echo "   数据在宿主机卷上（$REMOTE/data、log），删容器不丢数据。"
  fi
  rm -f /tmp/qv-compose.$$
  exit 1
fi
rm -f /tmp/qv-compose.$$

step "4/4 健康检查"
ok=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
  sleep 1
  if ssh "$NAS_HOST" "curl -fsS -m 3 http://127.0.0.1:$PORT/status" >/dev/null 2>&1; then
    ok=1
    break
  fi
done
if [ "$ok" -ne 1 ]; then
  echo "FAIL 容器起来了但 /status 不应答（10 秒内）"
  ssh "$NAS_HOST" "docker logs quoteview-server --tail 30" || true
  echo
  echo "回滚：git checkout <上一提交> -- server/scripts/server.py && tools/deploy_server.sh"
  exit 1
fi
echo "ok   容器 /status 正常"
curl -fsS -m 10 "$STATUS_URL" >/dev/null 2>&1 \
  && echo "ok   公网 $STATUS_URL 正常" \
  || echo "WARN 公网 $STATUS_URL 不通——容器是好的，问题在 nginx/DNS/外网这一段"
echo
echo "✅ 服务端已更新"

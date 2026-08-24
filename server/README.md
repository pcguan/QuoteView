# QuoteView 快照服务端

NAS 容器，为所有 QuoteView 客户端统一抓取并归档沪深合约的收盘分时快照。

## 部署（NAS，compose 管理，与该主机其他容器同一套规范）

仓库里的 `server/` 就是部署源；NAS 目录布局：

目录结构遵循该主机的 `cfg/data/log/scripts` 容器家规（同 nginx、sub2api）：

```
/vol3/1000/HDD2/tool/docker/quoteview-server/
  docker-compose.yml
  cfg/server.env         # 全部可调参数（TZ/端口/保留天数/限流间隔）
  scripts/server.py      # 服务端代码
  data/                  # clients/ trends/ state.json（容器数据，勿动）
  log/server.log         # 应用日志（10MB 自轮转一份 .1）
```

仓库 `server/` 与之一一对应（少 data/ 与 log/）。发布改动：

```bash
scp server/docker-compose.yml nas:/vol3/1000/HDD2/tool/docker/quoteview-server/
scp server/cfg/server.env    nas:/vol3/1000/HDD2/tool/docker/quoteview-server/cfg/
scp server/scripts/server.py nas:/vol3/1000/HDD2/tool/docker/quoteview-server/scripts/
ssh nas 'cd /vol3/1000/HDD2/tool/docker/quoteview-server && docker compose up -d --force-recreate'
```

nginx（`cfg/nginx.conf`，nas.pcguan.cn server 块）已加：

```nginx
location /quoteview/api/ {
    proxy_pass http://127.0.0.1:8388/;
    proxy_set_header Host $host;
    proxy_read_timeout 30s;
}
```

改动后 `docker exec nginx nginx -t && docker exec nginx nginx -s reload`。

nginx 三条 location 分工：`/quoteview/`（静态：更新+简报）、`/quoteview/api/`（客户端接口）、
`/quoteview/web/`（Web 管理台，反代到容器 `/web/`）。

## 接口

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| POST | /register | - | `{"username","password"}` → `{"token"}`（注册即登录；账户数上限 10） |
| POST | /login | - | 同上；密码 PBKDF2-SHA256(10万次) 存储，令牌每账户保留最近 10 个 |
| POST | /sync | Bearer | `{"groups":[{"name","codes":[]}]}`，5 分钟一次，按账户存 |
| GET | /dates?code=SH600519 | Bearer | 该合约已归档的日期 |
| GET | /trend?code=…&date=YYYY-MM-DD | Bearer | 单日分时（客户端 TrendSeries 同构 JSON） |
| GET/POST | /settings | Bearer | 账户个性化设置（客户端启动拉取、修改后上传） |
| GET | /status | - | 账户数/合约并集/上次扫描 |
| GET | /groups | Bearer | 账户的分组（切换用户时客户端恢复用） |
| GET | /kline | Bearer | K 线代理（secid/klt/fqt/lmt 白名单校验，5 分钟缓存，空响应不缓存） |
| GET | /admin | Basic(账户密码) | Web 管理台，仅 admin/sysadmin 角色可进 |
| POST | /password | Bearer | 自助改密（校验旧密码；其他会话失效、当前保留） |
| GET/POST | /admin/* | Basic(账户密码) | accounts / sessions / logs / create / delete / disable / logout / password / role |

**角色**：`user` 普通用户（默认）/ `admin` 普通管理员（只能操作 user）/ `sysadmin` 系统管理员
（唯一，可操作所有人并改角色；自身不可删除/禁用/降级）。

## 行为

- 每 5 分钟检查一次；工作日北京时间 15:20 后开始扫描当日缺失的合约（并集来自 14 天内活跃的客户端）。
- 严格串行，每只间隔 `QV_FETCH_GAP`（默认 1.5s）；东财 trends2 直连（NAS 无需代理）。
- 节假日探测：数据点自带日期，拉到旧交易日数据即整批停止并标记当日。
- 每合约保留 `QV_RETAIN_DAYS`（默认 7）天。

## 运维

```bash
ssh nas 'tail -50 /vol3/1000/HDD2/tool/docker/quoteview-server/log/server.log'
docker logs quoteview-server --tail 50        # 同样内容的 stdout 副本
curl -s https://nas.pcguan.cn/quoteview/api/status
```

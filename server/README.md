# QuoteView 快照服务端

NAS 容器，为所有 QuoteView 客户端统一抓取并归档沪深合约的收盘分时快照。

## 部署（NAS，compose 管理，与该主机其他容器同一套规范）

仓库里的 `server/` 就是部署源；NAS 目录布局：

```
/vol3/1000/HDD2/tool/docker/quoteview-server/
  docker-compose.yml     # 本目录的 docker-compose.yml
  app/server.py          # 本目录的 server.py
  data/                  # clients/ trends/ state.json（容器数据，勿动）
```

发布改动：

```bash
scp server/docker-compose.yml nas:/vol3/1000/HDD2/tool/docker/quoteview-server/
scp server/server.py nas:/vol3/1000/HDD2/tool/docker/quoteview-server/app/
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

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | /register | 返回 `{"id": …}`，客户端持久化 |
| POST | /sync | `{"id", "groups":[{"name","codes":[]}]}`，5 分钟一次 |
| GET | /dates?code=SH600519 | 该合约已归档的日期 |
| GET | /trend?code=…&date=YYYY-MM-DD | 单日分时（客户端 TrendSeries 同构 JSON） |
| GET | /status | 客户端数/合约并集/上次扫描 |

## 行为

- 每 5 分钟检查一次；工作日北京时间 15:20 后开始扫描当日缺失的合约（并集来自 14 天内活跃的客户端）。
- 严格串行，每只间隔 `QV_FETCH_GAP`（默认 1.5s）；东财 trends2 直连（NAS 无需代理）。
- 节假日探测：数据点自带日期，拉到旧交易日数据即整批停止并标记当日。
- 每合约保留 `QV_RETAIN_DAYS`（默认 7）天。

## 运维

```bash
docker logs quoteview-server --tail 50
curl -s https://nas.pcguan.cn/quoteview/api/status
```

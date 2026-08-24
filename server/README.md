# QuoteView 快照服务端

NAS 容器，为所有 QuoteView 客户端统一抓取并归档沪深合约的收盘分时快照。

## 部署（NAS）

```bash
# 代码与数据目录
/vol3/1000/HDD2/tool/docker/quoteview-server/app/server.py   # 本文件旁的 server.py
/vol3/1000/HDD2/tool/docker/quoteview-server/data/           # clients/ trends/ state.json

docker run -d --name quoteview-server --network host --restart unless-stopped \
  -e TZ=Asia/Shanghai -e QV_DATA=/data -e QV_PORT=8388 \
  -v /vol3/1000/HDD2/tool/docker/quoteview-server/app:/app:ro \
  -v /vol3/1000/HDD2/tool/docker/quoteview-server/data:/data \
  --entrypoint python3 mine/claude:latest /app/server.py
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

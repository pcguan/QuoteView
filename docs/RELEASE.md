# 发布与更新流程

QuoteView 每次改动的**标准流程**。日常代码改动走 **1–4**;要发一版给用户自动更新，再走 **5–6**。

**前置约定**
- 编译只在 **corp-win**（开发机无 .NET SDK；SSH host `corp-win`，产物在 `C:\work\stock`）。
- **git 必须走代理**（`http.proxy=192.168.33.9:7890`），否则 `push` 卡死。
- 更新源：**国内 NAS 为主**（`https://nas.pcguan.cn/quoteview/`）、**GitHub 兜底**；发版**两个源都要更新**。
- 令牌不写进任何被跟踪文件（仓库公开）；只在运行时环境 / `.git/config` / 私有 memory 里。

---

## 1. 本地改源码
在开发机编辑 `src/`。

## 2. corp-win 编译构建（失败则回步骤 1 修复重来）
```bash
scp -q -r src corp-win:C:/work/stock/
ssh corp-win "cd C:\work\stock\src\StockClient.App && dotnet publish -c Release -r win-x64 --self-contained false -o C:\work\stock\dist"
```
- 只关心有没有 `error`/`错误`；有就回步骤 1 改，再来。
- `deploy.sh` 会先跑 Smoke（打真实接口），东财偶发限流会中断——直接 `dotnet publish` 跳过即可。

## 3. 同步产物回本地 + 清理中间产物
```bash
scp -q corp-win:C:/work/stock/dist/QuoteView.exe release/QuoteView-<ver>.exe   # 取最终 exe
ssh corp-win "rmdir /s /q C:\work\stock\dist"                                   # 清理发布中间产物
```
- `release/` 已 gitignore（大二进制不进库），只作本地暂存 + 国内源上传的主拷贝。
- `bin/obj` 可留着给下次增量编译；要彻底清就 `rmdir /s /q C:\work\stock\src\StockClient.App\bin ...\obj`。

## 4. 提交并推送
```bash
git add -A && git commit -m "<中文简述本次改动>"
git push        # 走 .git/config 里的代理，直接 push；卡死就检查 http.proxy
```

---

## 5. 发布新版本（要让用户 app 自动更新时）

先抬版本并出对应 exe：
- `src/StockClient.App/StockClient.App.csproj` 的 `<Version>` +1（如 1.0.1→1.0.2）；
- 在 `CHANGELOG.md` 顶部记这一版的改动；
- 重新走 **步骤 2、3** 出新 `release/QuoteView-<ver>.exe`。

> **发布产物**：当前 QuoteView 是 framework-dependent 单文件，产物就是 `QuoteView-<ver>.exe` +
> 版本清单 `latest.json` + `CHANGELOG.md`。若以后加压缩包/安装器，按同样两源分发即可。

### 5a. 国内源（主）——NAS nginx
写 `release/latest.json`：
```json
{ "version": "<ver>", "url": "https://nas.pcguan.cn/quoteview/QuoteView-<ver>.exe",
  "size": <字节数>, "notes": "本版更新说明", "published": "<yyyy-mm-dd>" }
```
上传到 NAS 静态目录（宿主机路径，`ssh nas` 可直接写）：
```bash
ssh nas 'mkdir -p /vol3/1000/HDD2/tool/docker/nginx/html/quoteview'
scp -q release/QuoteView-<ver>.exe release/latest.json nas:/vol3/1000/HDD2/tool/docker/nginx/html/quoteview/
ssh nas 'chmod 644 /vol3/1000/HDD2/tool/docker/nginx/html/quoteview/*'   # 必做！默认 700 → nginx 读不到 403
```
（nginx 已配好 `location /quoteview/`，无需改配置；serves at `https://nas.pcguan.cn/quoteview/`。）

### 5b. GitHub 源（兜底）
```bash
# 建 release（带 token；<PAT> 见私有 memory，勿写进仓库）
curl -s -X POST -H "Authorization: token <PAT>" -H "User-Agent: sc" \
  https://api.github.com/repos/pcguan/QuoteView/releases \
  -d '{"tag_name":"v<ver>","target_commitish":"main","name":"v<ver>","body":"本版更新说明"}'
# 拿到返回的 release id，从 corp-win 上传资产（文件在那、外网可用）
ssh corp-win 'powershell -NoProfile -Command "Invoke-RestMethod -Uri \"https://uploads.github.com/repos/pcguan/QuoteView/releases/<id>/assets?name=QuoteView.exe\" -Method Post -Headers @{Authorization=\"token <PAT>\";\"User-Agent\"=\"sc\"} -ContentType application/octet-stream -InFile C:\work\stock\dist\QuoteView.exe"'
```
- GitHub 资产名固定 **`QuoteView.exe`**（客户端按此名找）；NAS 上是 `QuoteView-<ver>.exe`（manifest 给全 URL）。
- 别忘了步骤 4 把 csproj/CHANGELOG 的改动 commit+push。
- **不再手动部署到桌面 / 不 kill 运行中的 QuoteView 进程**——发布到两个源后，运行中的 app 走
  **线上自更新**（国内源优先，「检查更新」或下次启动）拉到新版。这也顺带每次都验证了自更新链路。

---

## 6. 走公网验证两个源一致
本地（或任意机器）用 curl 验证——**两个源的 version 和 size 都要和本地 exe 对得上**：
```bash
# 国内源
curl -s https://nas.pcguan.cn/quoteview/latest.json                       # version 对不对
curl -sI https://nas.pcguan.cn/quoteview/QuoteView-<ver>.exe | grep -i "200\|content-length"
# GitHub 源
curl -s -H "User-Agent: sc" https://api.github.com/repos/pcguan/QuoteView/releases/latest \
  | python3 -c "import json,sys;d=json.load(sys.stdin);print(d['tag_name'], d['assets'][0]['size'])"
```
两边对上了，才算发布完成。之后运行中的旧版 app（启动后台查 / 标题栏「检查更新」）会**国内优先**拉到新版并自更新。

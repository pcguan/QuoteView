# 发布与更新流程

**日常发版一条命令**（v1.1.0 起）：抬好 csproj 版本、写好 CHANGELOG 后——

```bash
tools/release.sh <版本> <发布说明> [提交信息]
```

测试→corp-win 编译→构建清单→回传→check_release→commit+push→NAS+GitHub 双源→公网核验，
任一步失败即中止。GitHub 令牌读 `git config qv.ghtoken`（不入库）。以下手工步骤仅供
脚本失效时排障参考。

QuoteView 每次改动的**标准流程**。日常代码改动走 **1–4**;要发一版给用户自动更新，再走 **5–6**。

**前置约定**
- 编译只在 **corp-win**（开发机无 .NET SDK；SSH host `corp-win`，产物在 `C:\work\stock`）。
- **git 必须走代理**（`http.proxy=192.168.33.9:7890`），否则 `push` 卡死。
- 更新源：**国内 NAS 为主**（`https://nas.pcguan.cn/quoteview/`）、**GitHub 兜底**；发版**两个源都要更新**。
- 令牌不写进任何被跟踪文件（仓库公开）；只在运行时环境 / `.git/config` / 私有 memory 里。

---

## 1. 本地改源码
在开发机编辑 `src/`。

## 2. corp-win 编译构建 + 生成构建清单（失败则回步骤 1 修复重来）
```bash
scp -q -r src corp-win:C:/work/stock/
ssh corp-win "cd C:\work\stock\src\StockClient.App && dotnet publish -c Release -r win-x64 --self-contained false -o C:\work\stock\dist"
# 构建清单在【编译机上】生成——版本/字节数/SHA-256 与产物同源，是同步完整性的锚点
ssh corp-win "powershell -c \"\$f=Get-Item C:\work\stock\dist\QuoteView.exe; @{version=\$f.VersionInfo.FileVersion; size=\$f.Length; sha256=(Get-FileHash \$f.FullName -Algorithm SHA256).Hash.ToLower()} | ConvertTo-Json -Compress | Set-Content C:\work\stock\dist\build.json\""
```
- 只关心有没有 `error`/`错误`；有就回步骤 1 改，再来。

## 3. 同步产物+清单回本地 + 清理中间产物
```bash
scp -q corp-win:C:/work/stock/dist/QuoteView.exe release/QuoteView-<ver>.exe
scp -q corp-win:C:/work/stock/dist/build.json release/build.json
ssh corp-win "rmdir /s /q C:\work\stock\dist"
```
- `check_release.sh` 会拿本地 exe 对 `build.json` 逐项核验（版本/字节数/SHA-256），
  **清单缺失或对不上 = 同步失败**，直接 FAIL——scp 半途断连的残件再也过不了关
  （2026-08-27 一个 977KB 残件曾因"清单从残件生成"而自洽过检并放倒了 home-win）。
- **隧道不稳时的兜底传输**：scp/长 ssh 流反复截断的话，改用分块 base64 过 ssh exec 通道
  （512KB/块、逐块校验重试，`[Convert]::ToBase64String(bytes, off, len)` 读段），
  拼好后仍走 check_release 整体核验（2026-08-28 实战过一次）。
- **发布动作必须全部链在 check_release 之后**——包括 GitHub：一次残件曾因 GitHub 上传
  写在校验链之外而被推上兜底源。
- `release/` 已 gitignore（大二进制不进库），只作本地暂存 + 国内源上传的主拷贝。

## 4. 提交并推送
```bash
git add -A && git commit -m "<中文简述本次改动>"
git push        # 走 .git/config 里的代理，直接 push；卡死就检查 http.proxy
```

---

## 5. 发布新版本（要让用户 app 自动更新时）

> ⚠️ **抬完版本必须重新编译。**改 `<Version>` 之后如果直接从上次的 `dist/` 拷 exe，
> 文件名和 manifest 都写着新版本，而**二进制里还是旧版本号**——客户端更新完仍报旧版、
> 于是被反复提示同一个更新。发布前跑 `tools/check_release.sh <版本>` 卡这一关（内部版本号在本机直接读 PE 版本资源，
> 不依赖 corp-win——那条隧道一断，检查就会退化成 WARN 而形同虚设）。

先抬版本并出对应 exe：
- `src/StockClient.App/StockClient.App.csproj` 的 `<Version>` +1（如 1.0.1→1.0.2）；
- 在 `CHANGELOG.md` 顶部记这一版的改动；
- 重新走 **步骤 2、3** 出新 `release/QuoteView-<ver>.exe`。

> **发布产物**：当前 QuoteView 是 framework-dependent 单文件，产物就是 `QuoteView-<ver>.exe` +
> 版本清单 `latest.json` + `CHANGELOG.md`。若以后加压缩包/安装器，按同样两源分发即可。

### 5a. 国内源（主）——NAS nginx
写 `release/latest.json`（size/sha256 直接取自 build.json，客户端下载后按 sha256 校验）：
```json
{ "version": "<ver>", "url": "https://nas.pcguan.cn/quoteview/QuoteView-<ver>.exe",
  "size": <build.json的size>, "sha256": "<build.json的sha256>",
  "notes": "本版更新说明", "published": "<yyyy-mm-dd>" }
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
# 正文末尾必须带一行 "SHA256: <build.json的sha256>"——客户端对 GitHub 兜底源就靠它校验下载
curl -s -X POST -H "Authorization: token <PAT>" -H "User-Agent: sc" \
  https://api.github.com/repos/pcguan/QuoteView/releases \
  -d '{"tag_name":"v<ver>","target_commitish":"main","name":"v<ver>","body":"本版更新说明"}'
# 拿到返回的 release id，直接从本机上传资产（走代理即可，不必绕 corp-win）
curl -s -X POST -H "Authorization: token <PAT>" -H "User-Agent: sc" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @release/QuoteView-<ver>.exe \
  "https://uploads.github.com/repos/pcguan/QuoteView/releases/<id>/assets?name=QuoteView.exe"
```
- GitHub 资产名固定 **`QuoteView.exe`**（客户端按此名找）；NAS 上是 `QuoteView-<ver>.exe`（manifest 给全 URL）。
- 别忘了步骤 4 把 csproj/CHANGELOG 的改动 commit+push。
- **不再手动部署到桌面 / 不 kill 运行中的 QuoteView 进程**——发布到两个源后，运行中的 app 走
  **线上自更新**（国内源优先，「检查更新」或下次启动）拉到新版。这也顺带每次都验证了自更新链路。

---

## 5c. 发布前自检（必做）

```bash
tools/check_release.sh 1.0.35
```

三项：`latest.json` 的版本、`latest.json` 的 size 与实际文件、**exe 内部编译进去的版本号**。
第三项是唯一会静默出错的一项，其余两项肉眼也能发现。

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

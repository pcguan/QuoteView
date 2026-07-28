# QuoteView 实现说明（开发向）

产品介绍见 [README](../README.md)。这里是实现细节、设计取舍、踩过的坑——给改代码的人看的。
数据源接口字段枚举另见 [data-source-fields.md](data-source-fields.md)。

## 部署工作流

源码只在开发机。编译、跑真实接口的验证都在 **corp-win**（唯一有外网的 Windows 编译机，
SSH host `corp-win`）；两台桌面（corp-win、pc-guan）只放最终 exe。

```bash
./tools/deploy.sh          # 同步源码 → corp-win 编译 + Smoke → 分发到两台桌面
./tools/deploy.sh --build  # 只编译验证，不分发
```

Smoke 打真实接口，东财 kline 偶发限流会让它中断；这种时候手动 `ssh corp-win` 直接
`dotnet publish` 跳过 Smoke 即可（App 编译不依赖接口）。exe 输出名为 `QuoteView.exe`
（`AssemblyName`），项目文件夹/命名空间仍是 `StockClient`。

## 合约查询：排序与全量列表的两个实现约束

**结果集整体替换，不做增量 Add。** 浏览全部市场是 28,638 条，逐条塞进 `ObservableCollection`
会触发同样多次 `CollectionChanged`。`Results` 是 `IReadOnlyList<Contract>`，一次性替换 + 一次
`PropertyChanged`；`DataGrid` 开了行虚拟化。

**排序方向的状态在 ViewModel，不在 `DataGridColumn.SortDirection`。** 因为每次排序都会替换
`ItemsSource`，而 `DataGrid` 在 `ItemsSource` 变化时会把列的 `SortDirection` 清成 null——
拿它当 toggle 依据的话，第二次点击永远读到 null、永远算升序，方向死活反不过来。
`MainWindow.ResultGrid_Sorting` 接管 `Sorting` 事件（`e.Handled = true`），方向由
`MainViewModel.SortField` / `SortAscending` 决定。

## K 线图 / 分时图

- **数据源**：K 线用东财 `push2his` kline（六市场全历史，一次请求返回上市至今，不分页）；
  分时用东财 `trends2`。东财 kline 被限流时**自动切腾讯 `fqkline` 兜底**（A股/港股有历史，
  BJ/US/KR 只当天），状态栏标明来源。
- **按交易日缓存**：`KlineRepository` 缓存优先 → 东财 → 腾讯，键为 `合约/周期_复权/交易日`，
  全量历史每交易日**只拉一次**，其余读盘。这也让前复权在除权日自动正确（每交易日重拉）。
- **盘中快照要顶更**（v1.0.14 修的 bug）：光按交易日缓存不够——盘中拉到的最后一根 K 线是
  **没走完的**（收盘价=当时价，最高/最低/成交量都不全）。原来它会被钉住一整天，收盘后再打开
  图表看到的还是半截数据。现在 `KlineSeries.FetchedAt` 记抓取时刻，`IMarketClock.IsAfterClose`
  按**交易所本地时钟**判断是否已过收盘（+20 分钟缓冲，等收盘竞价和上游落库）：
  收盘后抓的当天有效；收盘前抓的则在下次读取时**只重拉末尾 2 根**（几百字节）拼回去，
  20 秒内不重复请求。拼接按**日期切断**而不是逐根合并——东财周/月线的进行中桶用
  **该周期内最新交易日**当标签（本周二拉是 `07-28`，周三就变 `07-29`），逐根合并会让同一周变成两根。
- **窗口内 30 秒静默刷新**：K 线窗开着时定时走同一条缓存路径，`KlineChart.UpdateSeries`
  只换数据、**保留缩放平移**（`SetSeries` 才重置视口）；拖动中直接跳过这次刷新。
- **降采样（LOD）**：可见根数超过像素列数时按像素聚合成合成蜡烛，绘制量恒定 ≈ 屏幕宽度，
  缩放到全部几千根也不卡（`KlineChart`）。
- 涨跌幅相对**昨收**，不是相对开盘。

## 简洁面板

- **全局快捷键**（`RegisterHotKey`，注册在主窗口，进面板只是最小化）：`Win+Alt+End` 主/面板
  互切，`Win+Alt+↑/↓` 变亮暗，`Win+Alt+←/→` 切合约，`Win+Alt+PageUp/Down` 切分组，
  `Win+Alt+Delete` 开关分时缩略图。
- **置顶防遮挡**：`WS_EX_TOPMOST` 只保证压过非置顶窗口，任务栏也是置顶、会周期性抢到前面；
  面板 500ms 轮询检测被覆盖则重新 `NOTOPMOST→TOPMOST` 抢回（`EnsureOnTop`）。
- **分时缩略图**：单合约、内存单日缓存、过期（>15s）重拉，线尾用 1s 现价平滑；开关缩略图时
  把该块高度吸收进窗口 `Top`（向上生长），行情行位置不动。

## 实时行情：两路轮询

主行情是**腾讯 `qt.gtimg.cn` 每 1 秒一个批量请求**（全组合约一把拉，`QuotePoller`）——价格/涨跌/量额/市值/换手/市盈市净等都在这一条里，六市场通吃。

**涨速 / 主力资金**（f22/f62/f66/f72/f78/f84/f184）腾讯没有,只东财有、且只 A 股。所以旁挂第二路
`EastMoneyExtraPoller`（东财 `ulist.np`，5 秒一个批量请求）,严格隔离:

- **按需启动**:仅当"涨速/主力净流入/…"这几列**有一列可见**时才轮询（视图监听列 Visibility → `SetFundFlowActive`）,没人看就一个请求都不发;
- 只请求活动分组里的 **A 股**,secid 用 `f13.f12` 消歧 SZ 与 BJ（两者 f13 都是 0）;
- **失败静默 + 自适应退避**（5s→翻倍到 30s,成功即恢复）,东财挂了只让这几列变旧,腾讯主链路零影响;
- 只写涨速/资金流字段,绝不碰价格,避免和 1s 价格更新互相覆盖。

## 数据源与规模

| 用途 | 来源 |
| --- | --- |
| 合约列表 | 东财 `push2delay.eastmoney.com/api/qt/clist/get` |
| 板块目录 | 东财 `clist` `m:90+t:1/2/3`（地区/行业/概念） |
| 实时行情（主） | 腾讯 `qt.gtimg.cn`，1s 批量，`kr` 前缀支持韩股 |
| 涨速/主力资金（次） | 东财 `push2.../ulist.np/get`，5s 批量，A股、按需启动 |
| K 线（主） | 东财 `push2his.eastmoney.com/api/qt/stock/kline/get`，六市场全历史 |
| K 线（兜底） | 腾讯 `web.ifzq.gtimg.cn/appstock/app/fqkline/get`，A股/港股全史、BJ/US/KR 仅当日 |
| 分时 | 东财 `push2his.../trends2/get`，六市场当日 |
| 版本检查/更新 | 国内 NAS `nas.pcguan.cn/quoteview/`（主）+ GitHub Releases（兜底） |

实测各市场规模与拉取成本（顺序分页，无并发）：

| 市场 | `fs` 过滤器 | 数量 | 页数 |
| --- | --- | --- | --- |
| 沪A（主板+科创+ETF） | `m:1+t:2,m:1+t:23` / `m:1+b:MK0021..24` | 3,324 | 34 |
| 深A（主板+创业+ETF） | `m:0+t:6,m:0+t:80` / `m:0+b:MK0021..24` | 3,749 | 38 |
| 北交所 | `m:0+t:81+s:2048` | 338 | 4 |
| 港股 | `m:128+t:1,2,3,4` | 4,699 | 47 |
| 美股 | `m:105,106,107` | 13,662 | 137 |
| 韩股 | `m:177` | 2,866 | 29 |

**合计约 28,600 条 / 约 289 请求 / 全量约 4 秒 / 磁盘约 2 MB。**

## 列设置（实时行情）

腾讯每秒行情携带的字段很多（价格/量额/市值/换手/市盈市净/振幅/均价…）+ 板块（行业/地区/概念）+ A股涨速/资金流,
默认只显示几列,其余可在**列设置窗口**（`ColumnSettingsWindow`,表头行右端齿轮按钮 / 表头右键）里配置:

- 全部列平铺成 chips,勾选显隐、**拖动排序**,带 全选/全清/默认 快捷。
- 拖拽是**鼠标捕获式**（`CaptureMouse`+`MouseMove`）,不是 OLE `DoDragDrop`——后者事件节流,浮影会卡;
  捕获式全速率跟手,浮影是拖起瞬间的**位图快照**（实时引用会因让位重建控件而变白）。
- 大数**进位显示**（`NumConverter`:3110123→311.01万,市值到亿/万亿）,悬停看真实值;下方选中行徽章保持真实值。
- 列显隐/顺序/宽度经 `QuoteColumns` 持久化（监听 Width/Visibility/DisplayIndex 三个 DP + 300ms 去抖）。

## 在线更新

启动 1.5 秒后静默首查,之后**每 30 秒**轮询;发现新版在**底部状态栏**弹提示条（更新/关闭,关闭后同版本不再自动打扰）。

- **多源顺序**:`UpdateService` 先查国内 NAS（`DomesticReleaseClient` 读 `latest.json`）,失败/超时再 GitHub
  （`GithubReleaseClient` 匿名读 `releases/latest`,**客户端不带 token**）;单源 10 秒超时,坏源快速跳过;
  GitHub 兜底自动限频 5 分钟一次,防匿名配额（60/hr）耗尽。UI 不显示来源（隐私）。
- **自更新**:下载新 exe → 把正在运行的 exe 改名 `.old` → 新 exe 就位 → 带 `--updated` 启动新进程 → 自己退出。
  单实例互斥锁对 `--updated` 会**等旧实例退出后接管**（否则新副本会把自己当重复实例退出 = "更新没重启"）。
  下次启动清扫残留 `.old`。发布见 [RELEASE.md](RELEASE.md)。

## 缓存

```
%APPDATA%\StockClient\contracts\{MARKET}\{yyyy-MM-dd}\symbols.json          合约列表
%APPDATA%\StockClient\boards\{yyyy-MM-dd}\boards.json                       板块目录（行业/概念/地区）
%APPDATA%\StockClient\klines\{CODE}\{period}_{adjust}\{yyyy-MM-dd}.json     K 线
%APPDATA%\StockClient\groups.json                                          分组 + 面板/列配置
%APPDATA%\StockClient\panel.log                                            诊断日志（默认开）
```

（`%APPDATA%` 下的目录名仍是 `StockClient`——项目/命名空间未改，只有 exe 名叫 QuoteView。）

合约列表和 K 线都按**交易日**分层，各自保留最近 **7 天**（`ContractCache` / `KlineCache`
的 `RetainDays`）。K 线的腾讯兜底数据带来源标记、不会在东财恢复后继续被当全量数据用。
K 线缓存文件还带 `FetchedAt`，用来区分盘中快照和收盘后的定稿数据（见「K 线图 / 分时图」）；
旧版缓存没这个字段，读出来是零值、被当过期数据顶更一次，不需要迁移。

### 跨日：为什么用交易所本地时区，而不是客户端日期

用客户端本地日期判断"今天"，对美股是错的，而且不是"多刷一次"这种小问题：

| 北京时间 | 纽约时间 | 客户端日期规则的行为 | 后果 |
| --- | --- | --- | --- |
| 7/15 00:00 | 7/14 **12:00** 盘中 | 日期变了 → 拉取并盖戳 `07-15` | 拿到的是 **7/14** 的列表 |
| 7/15 12:00 | 7/15 **00:00** 真正跨日 | 戳 == 今天 → **不刷新** | 7/15 新股全漏 |
| 7/15 21:30–16 04:00 | 7/15 盘中 | 整场用 7/14 的列表 | **永久落后一个交易日** |

所以 `fetchedOn` 存的是**该市场本地日期**，判断也用该市场此刻的本地日期（`MarketClock`）。
夏令时交给 `TimeZoneInfo`（IANA ID 在 Windows 上经 ICU 解析），不自己算。

**为什么本地日历日 ≈ 交易日**：这六个市场的连续交易时段都不跨越各自的本地午夜
（沪深京 9:30–15:00、港 9:30–16:00、韩 9:00–15:30、美 9:30–16:00），所以本地日历日与交易日一一对应，
**不需要四国节假日表**——那种表会过期，判错就是该刷新时不刷新。

**长时间运行**：`MainViewModel` 每 15 分钟重新评估各市场（各市场跨日时刻不同，不能用一个午夜定时器）。

## 已验证的坑

**① 北交所与深A 在东财撞市场号。** 东财对北交所返回 `market 0`，与深A 完全相同，
所以**绝不能用响应里的 `f13` 反推交易所**——必须按板块 `fs` 分别拉取并由代码打标签。
后果很隐蔽：腾讯行情要 `bj920992`，写成 `sz920992` 会让该行**从响应里整行消失且不报错**。
（注：`f13` 本身仍要存下来当 K 线 secid 前缀，只是不能拿来判交易所——美股分布在 105/106/107。）

**② `pz` 被服务端硬顶在 100 条**，请求 20000 也只返回 100，必须老实分页。
但 K 线接口的 `lmt` 没有这个限制，一次返回全部历史。

**③ 北交所代码迁移**：苏轴股份东财只有新代码 `920418`，旧代码 `430418` 已不在列表
（腾讯行情仍兼容旧代码）。

**④ 东财 K 线接口会针对性限流。** 反复请求后，`push2his` 的 **kline 路径**被连接层 reset
（同 host 的 `trends2` 分时却仍通），重试无效——只能等或换源。所以 K 线做了腾讯兜底 +
按交易日缓存。

**⑤ K 线行列序是 date, open, close, high, low, …** —— close 在第三位，不是常规 OHLC。
按直觉位置读会把收盘价和最高价搞反。Smoke 有 high≥max(open,close) 的越界断言。

**⑥ 全局热键是"独占先到先得"。** 同一组合只有第一个 `RegisterHotKey` 成功的进程独占，
后来者注册失败。另有键盘钩子（输入法的 Alt+Shift）会在更底层吞键，导致注册成功却永不触发。
`RegisterHotKey` 需要 GUI 线程的消息队列，所以 PowerShell 探测不了，只能在 app 里实测。

**⑦ 前复权跨除权日会整条重算。** 前复权以最新价为锚回算，每次除权，从上市到昨天的每个
前复权值都变。按交易日缓存天然规避：每交易日重拉，除权当天那次就是最新正确的。

**⑧ 东财字段号跟接口绑定，不能跨接口套用。** 例：`f127` 在 `clist` 里是市净率，在
`stock/get` 里是细分行业名。详见 [data-source-fields.md](data-source-fields.md)。

**⑨ 本机 git 传输被 DPI 封，必须走代理。** github 网页/api 直连能通，但 git 智能传输
（receive-pack/upload-pack）直连会卡死；`git config http.proxy` 指向本地代理即可。

## 结构

```
src/StockClient.Core/
  Contracts/   市场枚举 + fs 过滤器 + 时区；合约 + symbols.json；东财 clist 分页；按交易日缓存
  Boards/      板块（行业/概念/地区）+ boards.json；东财 clist m:90+t:1/2/3；按交易日缓存
  Quotes/      腾讯批量行情(主)+1s 轮询；东财 ulist(次,涨速/资金流)+5s 轮询；K 线主/兜底+缓存；分时 trends2+内存日缓存
  Updates/     国内/GitHub 双源发布客户端（DomesticReleaseClient / GithubReleaseClient）
  Groups/      分组 + 简洁面板 + 列布局配置（groups.json）
src/StockClient.App/   WPF UI
  Views/       主窗口、行情/合约查询、K 线/分时图、简洁面板 + 设置、分时缩略图、列设置窗（拖动排序）
  ViewModels/  Main / Quotes / Kline
  Services/    UpdateService（版本检查 + 自更新，多源顺序 + 改名替换重启）
tools/Smoke/   数据层冒烟测试（打真实接口）
tools/deploy.sh  同步源码 → corp-win 编译 → 分发桌面
tools/ghpush.py  git 传输被封时的兜底：走 api.github.com 推送整棵树
```

`symbols.json`：

```jsonc
{ "market": "SH", "tradingDate": "2026-07-15", "fetchedAtUtc": "...", "count": 3324,
  "symbols": [ { "code": "SH600519", "name": "贵州茅台",
                 "py": "guizhoumaotai", "pyi": "gzmt" } ] }
```

拼音由 `TinyPinyin.Net` 在拉取时生成（28,600 条实测 11ms），落盘供搜索匹配，界面不展示。

## 已知局限

**当天新股可能当天搜不到。** 刷新点在市场本地 00:00，若东财在开盘后才把新股加进列表，
要到次日才出现。堵这个洞需要引入交易时段与节假日判断，即上面刻意避开的会过期的东西。
这种情况下直接输入完整代码仍可用。

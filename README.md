# QuoteView

C# WPF 客户端，覆盖**六个交易所**（沪A / 深A / 北交所 / 港股 / 美股 / 韩股）的
合约查询、实时行情、K 线/分时图，以及一个桌面置顶的简洁行情面板。

## 运行

```
QuoteView.exe
```

**framework-dependent x64 单文件，目标机需装 .NET 8 桌面运行时。** 打包运行时会让 exe
到 168MB，而部署链路只有 ~0.5 MB/s（见下方部署工作流），两台目标机都已装 .NET 8。

```bash
cd src\StockClient.App && dotnet run -c Release     # 开发
cd tools\Smoke        && dotnet run -c Release      # 数据层冒烟测试（打真实接口）
```

### 部署工作流

源码只在开发机（无外网出口）。编译、跑真实接口的验证都在 **corp-win**（唯一有外网的
Windows 编译机，SSH host `corp-win`）；两台桌面（corp-win、pc-guan）只放最终 exe。

```bash
./tools/deploy.sh          # 同步源码 → corp-win 编译 + Smoke → 分发到两台桌面
./tools/deploy.sh --build  # 只编译验证，不分发
```

Smoke 打真实接口，东财 kline 偶发限流会让它中断；这种时候手动 `ssh corp-win` 直接
`dotnet publish` 跳过 Smoke 即可（App 编译不依赖接口）。

## 功能总览

| 模块 | 说明 |
| --- | --- |
| 合约查询 | 六市场全量合约列表 + 编码/中文/拼音搜索 + 表头排序 |
| 实时行情 | 分组自选、腾讯批量轮询（1s）、双击开 K 线图 |
| K 线图 | 日/周/月 + 分时，东财主源、腾讯兜底，按交易日缓存，全量历史 + 缩放平移 |
| 简洁面板 | 桌面置顶多行行情条，独立设置窗口，全局快捷键 |
| 分组管理 | 拖拽排序、导入导出、列布局持久化 |

## 合约查询

- 启动时按**各交易所自己的交易日**判断缓存是否命中，未命中则从东财拉取
- 选中某个交易所（或"全部市场"）且不输关键词时，**全量列出该范围内的所有合约**
- 搜索支持：合约编码（`600519` / `SH600519`）、中文模糊（`茅台` / `半导体`）、拼音（`gzmt`）
- 点击「代码」「名称」「交易所」表头排序，可升降切换；排序状态在切换市场/搜索后保留
- **拼音只用于匹配，不在界面展示**

### 排序与全量列表的两个实现约束

**结果集整体替换，不做增量 Add。** 浏览全部市场是 28,638 条，逐条塞进 `ObservableCollection`
会触发同样多次 `CollectionChanged`。`Results` 是 `IReadOnlyList<Contract>`，一次性替换 + 一次
`PropertyChanged`；`DataGrid` 开了行虚拟化。

**排序方向的状态在 ViewModel，不在 `DataGridColumn.SortDirection`。** 因为每次排序都会替换
`ItemsSource`，而 `DataGrid` 在 `ItemsSource` 变化时会把列的 `SortDirection` 清成 null——
拿它当 toggle 依据的话，第二次点击永远读到 null、永远算升序，方向死活反不过来。
`MainWindow.ResultGrid_Sorting` 接管 `Sorting` 事件（`e.Handled = true`），方向由
`MainViewModel.SortField` / `SortAscending` 决定。

## K 线图 / 分时图

双击实时行情或合约查询里的合约，弹出独立图窗。周期按钮：**分时 / 日K / 周K / 月K**，
另有 **前/不/后复权** 切换。

- **数据源**：K 线用东财 `push2his` kline（六市场全历史，一次请求返回上市至今，不分页）；
  分时用东财 `trends2`。东财 kline 被限流时**自动切腾讯 `fqkline` 兜底**（A股/港股有历史，
  BJ/US/KR 只当天），状态栏标明来源。
- **按交易日缓存**：`KlineRepository` 缓存优先 → 东财 → 腾讯，键为 `合约/周期_复权/交易日`，
  同一合约每交易日**最多请求一次**，其余读盘。这也让前复权在除权日自动正确（每交易日重拉）。
- **降采样（LOD）**：可见根数超过像素列数时按像素聚合成合成蜡烛，绘制量恒定 ≈ 屏幕宽度，
  缩放到全部几千根也不卡（`KlineChart`）。
- MA5/10/20/60 均线（图例可点击显隐）、成交量副图、十字光标读数。**红涨绿跌**（A股惯例，
  已过色盲校验）。涨跌幅相对**昨收**，不是相对开盘。

## 简洁面板

桌面置顶的行情条，主窗口退到后台。右键或主界面「面板设置」打开独立设置窗口
（行数、透明度、字段显隐、涨跌分色）。

- **全局快捷键**（`RegisterHotKey`，注册在主窗口，进面板只是最小化）：`Win+Alt+End` 主/面板
  互切，`Win+Alt+↑/↓` 变亮暗，`Win+Alt+←/→` 切合约，`Win+Alt+PageUp/Down` 切分组。
- **置顶防遮挡**：`WS_EX_TOPMOST` 只保证压过非置顶窗口，任务栏也是置顶、会周期性抢到前面；
  面板 500ms 轮询检测被覆盖则重新 `NOTOPMOST→TOPMOST` 抢回（`EnsureOnTop`）。

## 数据源

| 用途 | 来源 |
| --- | --- |
| 合约列表 | 东财 `push2delay.eastmoney.com/api/qt/clist/get` |
| 实时行情 | 腾讯 `qt.gtimg.cn`，`kr` 前缀支持韩股 |
| K 线（主） | 东财 `push2his.eastmoney.com/api/qt/stock/kline/get`，六市场全历史 |
| K 线（兜底） | 腾讯 `web.ifzq.gtimg.cn/appstock/app/fqkline/get`，A股/港股全史、BJ/US/KR 仅当日 |
| 分时 | 东财 `push2his.../trends2/get`，六市场当日 |

实测各市场规模与拉取成本（顺序分页，无并发）：

| 市场 | `fs` 过滤器 | 数量 | 页数 |
| --- | --- | --- | --- |
| 沪A（主板+科创+ETF） | `m:1+t:2,m:1+t:23` / `m:1+b:MK0021..24` | 3,324 | 34 |
| 深A（主板+创业+ETF） | `m:0+t:6,m:0+t:80` / `m:0+b:MK0021..24` | 3,749 | 38 |
| 北交所 | `m:0+t:81+s:2048` | 338 | 4 |
| 港股 | `m:128+t:1,2,3,4` | 4,699 | 47 |
| 美股 | `m:105,106,107` | 13,662 | 137 |
| 韩股 | `m:177` | 2,866 | 29 |

**合计约 28,600 条 / 约 289 请求 / 全量约 4 秒 / 磁盘约 2 MB。** 美股 137 页顺序拉完实测
1086ms、8ms/页、0 失败，未触发限流。

## 缓存

```
%APPDATA%\StockClient\contracts\{MARKET}\{yyyy-MM-dd}\symbols.json          合约列表
%APPDATA%\StockClient\boards\{yyyy-MM-dd}\boards.json                       板块目录（行业/概念/地区）
%APPDATA%\StockClient\klines\{CODE}\{period}_{adjust}\{yyyy-MM-dd}.json     K 线
%APPDATA%\StockClient\groups.json                                          分组 + 面板/列配置
%APPDATA%\StockClient\panel.log                                            诊断日志（默认开）
```

合约列表和 K 线都按**交易日**分层，各自保留最近 **7 天**（`ContractCache` / `KlineCache`
的 `RetainDays`）。K 线的腾讯兜底数据带来源标记、不会在东财恢复后继续被当全量数据用。

### 跨日：为什么用交易所本地时区，而不是客户端日期

用客户端本地日期判断"今天"，对美股是错的，而且不是"多刷一次"这种小问题：

| 北京时间 | 纽约时间 | 客户端日期规则的行为 | 后果 |
| --- | --- | --- | --- |
| 7/15 00:00 | 7/14 **12:00** 盘中 | 日期变了 → 拉取并盖戳 `07-15` | 拿到的是 **7/14** 的列表 |
| 7/15 12:00 | 7/15 **00:00** 真正跨日 | 戳 == 今天 → **不刷新** | 7/15 新股全漏 |
| 7/15 21:30–16 04:00 | 7/15 盘中 | 整场用 7/14 的列表 | **永久落后一个交易日** |

所以 `fetchedOn` 存的是**该市场本地日期**，判断也用该市场此刻的本地日期（`MarketClock`）。
夏令时交给 `TimeZoneInfo`（IANA ID 在 Windows 上经 ICU 解析，已验证 `America/New_York` 正确
报告 EDT），不自己算。

**为什么本地日历日 ≈ 交易日**：这六个市场的连续交易时段都不跨越各自的本地午夜
（沪深京 9:30–15:00、港 9:30–16:00、韩 9:00–15:30、美 9:30–16:00），所以本地日历日与交易日一一对应，
**不需要四国节假日表**——那种表会过期，判错就是该刷新时不刷新。周末/假日最多多拉一次相同数据，
仍满足"每天最多一次"。

**长时间运行**：`MainViewModel` 每 15 分钟重新评估各市场（各市场跨日时刻不同，不能用一个午夜定时器）。

## 已验证的坑

**① 北交所与深A 在东财撞市场号。** 东财对北交所返回 `market 0`，与深A 完全相同，
所以**绝不能用响应里的 `f13` 反推交易所**——必须按板块 `fs` 分别拉取并由代码打标签。
后果很隐蔽：腾讯行情要 `bj920992`，写成 `sz920992` 会让该行**从响应里整行消失且不报错**。
`tools\Smoke` 里有针对这一点的断言。（注：`f13` 本身仍要存下来当 K 线 secid 前缀，只是不能
拿来判交易所——美股分布在 105/106/107，无法从代码推断。）

**② `pz` 被服务端硬顶在 100 条**，请求 20000 也只返回 100，必须老实分页。
但 K 线接口的 `lmt` 没有这个限制，一次返回全部历史。

**③ 北交所代码迁移**：苏轴股份东财只有新代码 `920418`，旧代码 `430418` 已不在列表
（腾讯行情仍兼容旧代码）。

**④ 东财 K 线接口会针对性限流。** 反复请求后，`push2his` 的 **kline 路径**被连接层 reset
（同 host 的 `trends2` 分时却仍通），重试无效——只能等或换源。所以 K 线做了腾讯兜底 +
按交易日缓存，正常低频使用不会触发。

**⑤ K 线行列序是 date, open, close, high, low, …** —— close 在第三位，不是常规 OHLC。
按直觉位置读会把收盘价和最高价搞反。Smoke 有 high≥max(open,close) 的越界断言。

**⑥ 全局热键是"独占先到先得"，不是同时/最近生效。** 同一组合只有第一个 `RegisterHotKey`
成功的进程独占，后来者注册失败。另有键盘钩子（输入法的 Alt+Shift）会在更底层吞键，导致
注册成功却永不触发——这是当初分组键弃用 Alt+Shift 的原因。`RegisterHotKey` 需要 GUI 线程
的消息队列，所以 PowerShell 探测不了，只能在 app 里实测。

**⑦ 前复权跨除权日会整条重算。** 前复权以最新价为锚回算，每次除权，从上市到昨天的每个
前复权值都变。按交易日缓存天然规避：每交易日重拉，除权当天那次就是最新正确的。不复权/
后复权是永久静态的。

## 结构

```
src/StockClient.Core/
  Contracts/
    Market.cs / MarketClock.cs   市场枚举 + fs 过滤器 + 时区；按交易所时区算交易日
    Contract.cs                  合约 + symbols.json 结构（含 K 线 secid 用的市场号）
    EastMoneyContractClient.cs   东财 clist 分页 + 按板块打标签 + 拼音生成
    ContractCache.cs / ContractRepository.cs   按交易日缓存 + 按需加载 + 本地搜索排序
  Boards/
    Board.cs                     板块（行业/概念/地区）+ boards.json 结构；名字后缀 Ⅱ/Ⅲ 判层级
    EastMoneyBoardClient.cs      东财 clist m:90+t:1/2/3 分页（三类板块目录）
    BoardCache.cs / BoardRepository.cs   按交易日缓存一份，随合约刷新
  Quotes/
    TencentQuoteClient.cs        腾讯批量实时行情（GBK）
    QuotePoller.cs               1s 轮询单个活动分组
    EastMoneyKlineClient.cs / TencentKlineClient.cs   K 线主源 / 兜底源
    EastMoneyTrendClient.cs      分时（trends2）
    KlineCache.cs / KlineRepository.cs   按交易日缓存 + 缓存优先→东财→腾讯
  Groups/GroupStore.cs           分组 + 简洁面板 + 列布局配置（groups.json）
src/StockClient.App/             WPF UI
  Views/                         主窗口、行情/合约查询、K 线/分时图、简洁面板 + 设置
  ViewModels/                    Main / Quotes / Kline
tools/Smoke/                     数据层冒烟测试（打真实接口）
tools/deploy.sh                  同步源码 → corp-win 编译 → 分发桌面
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

# PulseWin Services 层模块地图

- 审计日期：2026-10-20　版本：1.21.6（`PulseWin.csproj`）
- 范围：`Services\`（27 个文件）+ `Services\Spend\`（12 个文件），共 39 个 `.cs`，实读 9,458 行
- 排除：`bin\`、`obj\`、`publish\`
- "被谁依赖"只记**本仓库真实存在的调用点**；`Views\`/`App.xaml.cs` 的行号仅作定位。
- 与风险清单的对应关系：本文件只描述结构，问题与修法见 [services-audit.md](services-audit.md)。

---

## 1. 分层概览

```
UI / App（不在本次审计范围）
MainWindow · SettingsWindow · SpendWindow · SourceCard · CardRenderer · App.xaml.cs
        │                         │                              │
        ▼                         ▼                              ▼
SnapshotsShown            SpendLedgerCache →                UsageEngine（状态 / 账号操作）
SnapshotSource             SpendLedger/SpendSummary          同步循环 = 数据采集
TraySummary · BurnRate     ChannelUsage · ModelUsage
                           UsageRepository · ModelPrices
                           SpendStores · DshUsageStore
        │                         │                              │
        └────────── 基础设施：AppDataPaths(+AtomicFile) · Diagnostics · ProxyHttp ──────────┘
                              设置/凭据/键：AppSettings · SourceCatalog · SourceKeys
```

三层的关系一句话：**采集层写快照和仓库，统计层从源库+仓库算账，呈现层只读快照贴到界面上**。三层的产物都是不可变对象或磁盘文件，层与层之间没有回调（唯一的事件是 `UsageEngine.SnapshotsChanged`，在 `App.xaml.cs:227-232` 里被 `Dispatcher.BeginInvoke` 转到 UI）。

---

## 2. `Services\`（根目录，27 个文件）

| 文件 | 行数 | 一句话职责 | 依赖（它调用谁） | 被谁依赖（调用点） |
|---|---:|---|---|---|
| `UsageEngine.cs` | 860 | 后台同步引擎：并行同步各"条目"、落快照、维护每源状态/退避/静默期、OpenCode 附加账号增删改 | `AppSettings`、`AppDataPaths`、`SnapshotCache`、`SourceCatalog`/`SourceKeys`、三个 `*ApiClient`、两个 `CredentialResolver`、`Diagnostics`、`BrowserCookieStore`、`OpenCodeConsoleClient`、`OpenCodeUsageStatsClient` | `App.xaml.cs:226-236`（构造/Start/刷新）、`SettingsWindow.xaml.cs:305`、`SourceCard.cs:385-405`（`StatusForSource`） |
| `AppSettings.cs` | 426 | 设置单例：加载/归一/迁移（旧三布尔→`VisibleSources`）/原子保存；含 OpenCode 账号清单（不含 Key） | `AppDataPaths`/`AtomicFile`、`Diagnostics`、`SourceCatalog`、`SourceKeys` | 几乎所有层（`AppSettings.Current` 是全局读取点）、`Views` 设置页、`SourceCard` |
| `AppDataPaths.cs` | 145 | 便携数据目录决策（exe\Data 或 %LOCALAPPDATA%）+ 全部数据文件名 + 旧目录迁移；同文件内含 `AtomicFile`（.tmp→Move） | 无（只依赖 BCL） | `UsageEngine`、`SnapshotSource`、`SnapshotCache`、`DeepSeek*`、`UsageRepository`、`ModelPrices*`、`Diagnostics`、`CommandCodeWebSession` |
| `SourceCatalog.cs` | 85 | 三个数据源的**静态元数据表**（键/显示名/月池叫法/快照文件/凭据文件） | `MonitorSource`、`AppDataPaths` | `UsageEngine`、`AppSettings.Keys`、`SnapshotSource`、`SourceKeys`、`Views` |
| `SourceKeys.cs` | 51 | 源键 ↔ 实例键（`opencode:2`）解析；`IsKnown` 同时认两种 | `SourceCatalog` | `AppSettings`、`UsageEngine`、`SnapshotSource`、`SourceCard` |
| `QuotaModels.cs` | 143 | 领域 record/enum：`QuotaKind`/`QuotaWindow`/`UsageSnapshot`/`CredentialCandidate`/`CredentialValidationResult`/`CommandCodeApiException`/`BalanceBasis` | 无 | 所有 API 客户端、`UsageEngine`、`SnapshotSource`、`SnapshotCache`、`Views` |
| `SnapshotCache.cs` | 47 | 单个快照文件的读写（读容错、写走 `AtomicFile`） | `AppDataPaths`/`AtomicFile` | `UsageEngine`（三个主 cache + 每账号 new 一个） |
| `SnapshotSource.cs` | 342 | 快照 JSON → UI 模型（`SubData`/`PoolData`/`Money`）；另含 `PoolData`/`SubData`/`Money`/`SourceProfile` 定义与 `Stamp()` 变更探测 | `AppDataPaths`（静态自建）、`AppSettings`、`SourceCatalog`/`SourceKeys`、`DeepSeekLedger`、`Diagnostics` | `App.xaml.cs:1048`（托盘）、`MainWindow.xaml.cs:356,376,444`、`SpendWindow.xaml.cs:494`、`RailShot.cs`、`CardRenderer` |
| `CredentialResolver.cs` | 151 | `DpapiSecretStore`（DPAPI+entropy 加密存取）+ 凭据候选发现（环境变量→CLI auth.json→手动存储） | `AppDataPaths`、`AtomicFile`、`QuotaModels` | `UsageEngine`（GOAT/DeepSeek 两个解析器） |
| `OpenCodeCredentialResolver.cs` | 144 | OpenCode 专用：主 Key（env→auth.json→DPAPI）+ 附加账号清单（整份 JSON DPAPI 加密） | `DpapiSecretStore`（`CredentialResolver.cs`）、`QuotaModels` | `UsageEngine`（`DiscoverCandidates`/`GetAccountKey`/`LoadAccounts`/`SaveAccounts`） |
| `CommandCodeApiClient.cs` | 404 | GOAT 额度：`whoami`→`billing/credits`+`billing/subscriptions`（并行）→ 可选 `usage/summary`，月池按固定 70 credits 换算 | `ProxyHttp`、`QuotaModels` | `UsageEngine`（`_goatApi`） |
| `OpenCodeGoApiClient.cs` | 165 | OpenCode Go `/zen/go/v1/usage` → 三个窗口百分比（整数、向下取整） | `ProxyHttp`、`QuotaModels` | `UsageEngine`（`_goApi`）、`App.xaml.cs:334`（诊断） |
| `DeepSeekApiClient.cs` | 332 | DeepSeek `/user/balance` → 单一"余额"池；结合峰值基准与小时采样生成分母 | `DeepSeekBaseline`、`DeepSeekLedger`、`AppSettings`、`ProxyHttp`、`QuotaModels` | `UsageEngine`（`_deepSeekApi`） |
| `DeepSeekBalance.cs` | 87 | 观测峰值（每币种）与 `UsedFraction` 纯函数；`sinceTopUp` 模式的分母来源 | `AppDataPaths`/`AtomicFile`、`Diagnostics` | `DeepSeekApiClient` |
| `DeepSeekLedger.cs` | 135 | 余额的按小时采样账本（保留 7 天）→ "今日逐时消耗" | `AppDataPaths`/`AtomicFile`、`Diagnostics` | `DeepSeekApiClient`（`Record`）、`SnapshotSource`（`TodayHourlySpend`，每次 `LoadAll` 各 new 一个） |
| `OpenCodeConsoleClient.cs` | 252 | 网页控制台 `/console/api/go/status` → `meters` 微美分原值 + 网页同款四舍五入百分比；`--go-meters` 诊断解析入口 | `BrowserCookieStore`、`ProxyHttp`、`QuotaModels` | `UsageEngine.GetWebMetersAsync`（经 60s 缓存）、`App.xaml.cs` 诊断 |
| `OpenCodeUsageStatsClient.cs` | 398 | 官方**聚合**读数：`orgs`→`usage/{summary,models,cost-by-day}`；内存 + 落盘 15 分钟缓存；`WindowFor` 把区间映射成 24h/7d/30d | `BrowserCookieStore`、`AppSettings`、`ProxyHttp`、`AppDataPaths`（cache 文件） | `UsageEngine.MergeWebMetersAsync`（10s 子超时）、`SpendWindow.xaml.cs:410,1754`、`App.xaml.cs:277,281`（诊断） |
| `CommandCodeWebSession.cs` | 229 | 浏览器会话通道读 `/internal/usage/charts` 逐模型明细（`ModelCacheBucket`）；主机是 `api.commandcode.ai` | `BrowserCookieStore`、`AppSettings`、`ModelPrices.DisplayName`、`ProxyHttp` | `SpendWindow.xaml.cs:1523`、`App.xaml.cs:640`（诊断） |
| `BrowserCookieStore.cs` | 280 | Edge/Chrome cookie 库复制+AES-GCM/DPAPI 解密、按站点缓存 Cookie 头、手动粘贴 cookie 的 DPAPI 存取 | `AppDataPaths`（手动 cookie 文件）、`Microsoft.Data.Sqlite`、`ProtectedData` | `OpenCodeConsoleClient`、`OpenCodeUsageStatsClient`、`CommandCodeWebSession`、`SettingsWindow`（`HasManual`/`SaveManual`/`ClearManual`） |
| `BrowserLoginTargets.cs` | 56 | 两个登录目标（opencode/commandcode）的 URL、站点、会话 cookie 名 | 无 | `App.xaml.cs:202`、`SettingsWindow.xaml.cs:414`、`BrowserLoginWindow`（`Views`） |
| `ZCodeActivity.cs` | 325 | 增量读 ZCode 的 `zcode-<日期>.jsonl`：配对 `turn.started`/`completed`，判定"在跑"与"在烧哪条通道" | `Diagnostics` | `MainWindow.xaml.cs:487`（每秒 `Poll`）、`App.xaml.cs:730`（诊断） |
| `DshActivity.cs` | 214 | 读 DSH 的会话投影 JSON（`turnBoundary`/`sessionStats`/`modelSelection`）判定活动状态 | `Diagnostics` | `MainWindow.xaml.cs:488`（每秒 `Poll`）、`App.xaml.cs:752` |
| `ProxyHttp.cs` | 40 | 唯一 `HttpClient` 工厂：代理模式（系统/直连/手动）+ 超时 | `AppSettings` | 所有 API 客户端、`ModelPricesUpdater`、`UpdateChecker` |
| `Diagnostics.cs` | 54 | 100 条环形诊断日志，每次 `Note` 全量落盘 `diagnostics.json` | `AppDataPaths`/`AtomicFile` | 几乎全部（`UsageEngine`、各客户端、`SnapshotSource`、`DshUsageStore`、`SpendLedger`…） |
| `UpdateChecker.cs` | 109 | GitHub releases 检查（启动 30s 后、每 2 小时），只提示不自动装；`Changed` 事件当前无订阅者 | `ProxyHttp`、`Diagnostics` | `App.xaml.cs:238,1189`、`SettingsWindow.xaml.cs:524-535` |
| `BurnRate.cs` | 62 | 内存采样（30 分钟窗口）→ "约几小时后耗尽"；证据不足返回 null | 无 | `MainWindow.xaml.cs:381`（`Observe`）、`CardRenderer.cs:274-277`（`Estimate`/`Describe`） |
| `TraySummary.cs` | 27 | 托盘悬停摘要文本（每源取最紧张池） | `SubData`/`PoolData`/`Money`（`SnapshotSource.cs`） | `App.xaml.cs:1049`（并自行截断到 63 字符，`:1050`） |

---

## 3. `Services\Spend\`（12 个文件）

| 文件 | 行数 | 一句话职责 | 依赖（它调用谁） | 被谁依赖（调用点） |
|---|---:|---|---|---|
| `SpendLedger.cs` | 720 | **账本**：读三源→定价→跨源去重→并入本地仓库；同文件还有 `SpendSummary`（区间汇总/环比/24h 分布）与 `SpendFormat`（token/金额格式化） | `SpendStores`（`IUsageStore` 实现）、`DshUsageStore`、`ModelPrices`、`UsageRepository`、`UsageStoreSupport`、`AppSettings`（汇率） | `SpendLedgerCache.BuildOnce`、`SpendWindow`（大量）、`App.xaml.cs:500,615`（诊断）、`ChannelUsage`/`ModelUsage` |
| `SpendStores.cs` | 492 | `UsageStoreSupport`（只读 SQLite 打开、项目身份/显示名、列探测、时间戳解析）+ `ZCodeUsageStore`+`ZCodeSessionIndex`（parent 链归根）+ `OpenCodeUsageStore`（`message.data` JSON） | `Microsoft.Data.Sqlite`、`Spend\AgentUsageRecord`、`TokenTally`、`Diagnostics` | `SpendLedger.Build` |
| `DshUsageStore.cs` | 553 | DSH 会话 `session.v4.jsonl.zstd` 读取：zstd 解压、事件解析、子代理归根、**文件级静态缓存**（`CacheGate`，512 文件/12 万条上限） | `ZstdSharp`、`UsageStoreSupport`（项目身份）、`AgentUsageRecord`、`Diagnostics` | `SpendLedger.Build`；诊断读 `CacheStats`/`LastFold` |
| `AgentUsageRecord.cs` | 71 | 三源统一记录 + `DedupKey` + `IUsageStore` 接口（只读契约） | `TokenTally` | 三个 store、`SpendLedger` |
| `TokenTally.cs` | 57 | 四类 token / 四类成本 / 牌价（每百万 token 美元）与"缓存价缺失回退输入价"的唯一计价算法 | 无 | 整个统计层（`SpendEntry`/`ModelUsage`/`ChannelUsage`/`UsageRepository`） |
| `ModelPrices.cs` | 305 | 牌价表：读 `Data\model-prices.json`（下载版）→ 内置嵌入资源兜底；规范化模型名；第一方优先、套餐次之的查找 | `SnapshotSource.DataPaths`、`Diagnostics`、嵌入资源 `model-prices.json` | `SpendLedger.Build`、`CommandCodeWebSession`/`SpendLedger`/`ChannelUsage`/`ModelUsage`（`DisplayName`）、`ModelPricesUpdater`、`SpendWindow` |
| `ModelPricesUpdater.cs` | 208 | models.dev 全量下载→白名单裁剪→原子写→`ModelPrices.Invalidate`；失败退避 5 分钟、并发单飞 | `ProxyHttp`、`AtomicFile`、`ModelPrices`（白名单/`Current`）、`Diagnostics`、`UpdateChecker.CurrentVersion` | `SpendWindow.xaml.cs:421`、`App.xaml.cs:563`（诊断 `ForceRefresh`） |
| `UsageRepository.cs` | 386 | 本地聚合仓库 `Data\usage-history.sqlite`：明细增量灌入（seen 去重）、天/小时聚合、分级保留（天 10 年/小时 35 天/去重键 95 天）、schema 版本重建 | `SnapshotSource.DataPaths`、`Microsoft.Data.Sqlite`、`Diagnostics`、`SpendEntry` | `SpendLedger.Build`（`MergeIn`）；`DeleteForRebuild` 无调用方 |
| `SpendLedgerCache.cs` | 97 | 账本缓存：单飞构建 + 引用计数（归零释放）+ `Invalidate`；构建跑在线程池 | `SpendLedger`、`ModelPrices` | `SpendWindow.xaml.cs:439`、`SpendWindow.xaml.cs:425`（价目更新后作废） |
| `ChannelRegistry.cs` | 248 | providerId → "套餐"（渠道）：先读 ZCode `provider_config.json`，再退内置前缀规则；`SourceKeyOf` 给活动判定用 | `Diagnostics`、`SourceCatalog`（间接） | `ChannelUsageIndex`、`MainWindow`（`IsBurning`）、诊断 |
| `ChannelUsage.cs` | 510 | 套餐维度聚合：逐日/今日逐时/区间逐时/逐模型/会话数/遥测（耗时、TTFT、工具、重试、失败）、全时历史 | `SpendLedger`、`ChannelRegistry`、`ModelPrices`、`TokenTally` | `SpendWindow.xaml.cs:488`、`App.xaml.cs:659`（诊断） |
| `ModelUsage.cs` | 248 | 模型维度聚合（按显示名归并）：逐日、Top8 项目、会话数、牌价与"是否算得出价" | `SpendLedger`、`ModelPrices`、`TokenTally` | `SpendWindow`、`App.xaml.cs:621`（诊断） |

---

## 4. 关键依赖链（读代码时按这条走）

1. **采集链**：`UsageEngine.Start` → `Loop` → `RunRoundAsync` → `SyncOnce`（并行）→ `SyncSourceAsync`/`SyncOpenCodeAccountAsync` → `CredentialResolver.DiscoverCandidates` → `IUsageProvider.GetUsageAsync`（三个客户端之一）→（OpenCode 额外）`MergeWebMetersAsync` → `OpenCodeConsoleClient` / `OpenCodeUsageStatsClient`（都要 `BrowserCookieStore`）→ `SnapshotCache.SaveAsync`（`AtomicFile`）。
2. **呈现链**：`MainWindow.Tick`（15s）→ `SnapshotSource.Stamp()`（变了才继续）→ `LoadAll` → `LoadOne`（读快照 JSON）→ `SubData`/`PoolData` → `CardRenderer`/`TraySummary`；`DeepSeek` 源额外挂 `DeepSeekLedger.TodayHourlySpend`。
3. **统计链**：`SpendWindow.ReloadLedger` → `SpendLedgerCache.AcquireAsync`（单飞）→ `SpendLedger.Build(ModelPrices.Current)` → 三个 `IUsageStore.Read()` → 定价/去重 → `UsageRepository.MergeIn`（写仓库 + 读回聚合）→ `SpendSummary.Build`；之后 `ChannelUsageIndex.Build` / `ModelUsageIndex.Build` 按需再聚合。
4. **价目链**：`ModelPricesUpdater.RefreshIfStale`（后台 `Task.Run`）→ 下载裁剪 → `AtomicFile.WriteText` → `ModelPrices.Invalidate()` → 回调（UI `Dispatcher.BeginInvoke`）→ `SpendLedgerCache.Invalidate()` → 重算。

---

## 5. 静态状态清单（多线程/多窗口共享的东西都在这）

| 静态成员 | 位置 | 是否有同步 | 备注 |
|---|---|---|---|
| `AppSettings._current` / `Gate` | `AppSettings.cs:177-187` | 读无锁（引用读，安全）、写 `Save()` 无锁 | `Save()` 逐字段回写共享单例，见 P2-12 |
| `AppSettings.Current.OpenCodeAccounts`（`List`） | `AppSettings.cs:147` | **无** | 后台枚举 vs UI 增删 → P1-2 |
| `ModelPrices._current` / `Gate` | `ModelPrices.cs:112-122` | 有锁 | — |
| `ChannelRegistry._current` / `Gate` | `ChannelRegistry.cs:75-96` | 有锁 | `Invalidate` 无调用方（P2-10） |
| `DshUsageStore.Cache` / `_cachedRecords` / 计数 | `DshUsageStore.cs:65-71` | 有锁（`CacheGate`） | 逻辑正确，但截断结果被缓存 → P1-5 |
| `OpenCodeUsageStatsClient.Cache` | `OpenCodeUsageStatsClient.cs:86-87` | **无** | P1-3 |
| `BrowserCookieStore.Cache` / `ManualStores` / `LastError` | `BrowserCookieStore.cs:22-30` | **无** | P1-4 |
| `SpendLedgerCache._ledger` / `_building` / `_refCount` | `SpendLedgerCache.cs:28-31` | 有锁（单飞正确） | 引用计数归零释放 |
| `Diagnostics.Entries` | `Diagnostics.cs:14` | 有锁（但落盘在锁内） | P2-15 |
| `BurnRate.Tracks` | `BurnRate.cs:17-18` | 有锁 | — |
| `UpdateChecker.Available` / `_checking` | `UpdateChecker.cs:22-30` | `_checking` 原子；状态字段无锁 | 单写者（Timer 线程），可接受 |
| `ModelPricesUpdater._busy` / `_retryAfter` | `ModelPricesUpdater.cs:32-33` | `_busy` 原子；`_retryAfter` 普通字段 | 单飞足够，见注释 |
| `SnapshotSource.Paths`（`static new AppDataPaths()`） | `SnapshotSource.cs:154` | 静态初始化 | 与 `UsageEngine` 各自 new 一份 `AppDataPaths`（两次写探针），见 P2 列表 |
| `UsageEngine._instanceStatus` | `UsageEngine.cs:55` | **无**（实例字段但被并行任务写） | P1-1 |

---

## 6. 线程归属（谁在什么线程上跑）

| 执行体 | 线程 | 触碰的关键状态 |
|---|---|---|
| `UsageEngine.Loop` → `SyncOnce` → 各 `SyncEntryAsync` | 线程池（`Task.Run`，条目内并行） | `_instanceStatus`、`_retryAfter`、`_lastError`（并发字典）、`SourceStatus` 三个对象、`AppSettings` 读取、静态 `Cache`（P1-3/P1-4） |
| `ModelPricesUpdater.Refresh` 的 `Task.Run` | 线程池 | `ModelPrices`、`AtomicFile`、回调里只做 `Dispatcher.BeginInvoke`（正确） |
| `UpdateChecker` `System.Threading.Timer` | 线程池 | `Available`/`LastError`/`Changed`（无订阅者） |
| `MainWindow.Tick`（渲染回调） | UI | `ZCodeActivity.Poll`/`DshActivity.Poll`（每秒）、`SnapshotSource.Stamp/LoadAll`（每 15s）、`BurnRate.Observe` |
| `SpendWindow.ReloadLedger` → `SpendLedgerCache.AcquireAsync` | 构建在线程池，续体经 `TaskScheduler.FromCurrentSynchronizationContext()` 回 UI | `SpendLedger`（不可变，构建后只读） |
| `SpendWindow` 打开时 `OpenCodeUsageStatsClient.TryFetchAsync` | `async` 续体回 UI | 静态 `Cache`（P1-3） |
| `UsageEngine.SnapshotsChanged` | 后台触发，`App.xaml.cs:227-232` 用 `Dispatcher.BeginInvoke` 回 UI | 正确 |

---

## 7. 文件写入点（是否原子、失败会怎样）

| 目标文件 | 写入者 | 方式 | 失败处理 |
|---|---|---|---|
| `Data\settings.json` | `AppSettings.Save`（UI 线程） | `AtomicFile.WriteText` | catch → 诊断一条，内存值已改（不回滚） |
| `Data\*-snapshot*.json` | `SnapshotCache.SaveAsync`（后台） | `AtomicFile.WriteTextAsync` | 异常上抛 → 该源本轮判失败（P1-7）；取消时留 `.tmp`（P2-1） |
| `Data\credential.bin` 等 | `DpapiSecretStore.Write` | `AtomicFile.WriteBytes` | 异常上抛到调用方（设置页 catch） |
| `Data\opencode-go-accounts.bin` | `OpenCodeCredentialResolver.SaveAccounts` | 同上 | 同上 |
| `Data\deepseek-baseline.json` | `DeepSeekBaseline.Save` | `AtomicFile.WriteText` | catch → 诊断 |
| `Data\deepseek-ledger.json` | `DeepSeekLedger.Save` | `AtomicFile.WriteText` | catch → 诊断（那一小时采样丢） |
| `Data\diagnostics.json` | `Diagnostics.Flush` | `AtomicFile.WriteText` | catch 吞掉（"日志失败不影响主流程"） |
| `Data\model-prices.json` | `ModelPricesUpdater` | `AtomicFile.WriteText` | catch → 退避 5 分钟，旧表继续用 |
| `Data\opencode-usage-stats.json` | `OpenCodeUsageStatsClient.SaveToDisk` | **`File.WriteAllText`（非原子）** | catch 吞掉（P2-2） |
| `Data\usage-history.sqlite` | `UsageRepository.MergeIn` | SQLite 事务 | catch → 降级（但降级本身有 P1-6） |
| `%TEMP%\pulsewin-cookies-<pid>-<browser>.db` | `BrowserCookieStore.ReadCookiesFrom` | 先 `File.Copy` 再删 | `finally` 删除；并发同 pid 同名会互踩（P1-4 相邻风险） |
| 快照的读取 | `SnapshotSource.LoadOne` | `File.ReadAllText`（`FileShare.Read`） | 与原子替换冲突 → P1-7 |

---

## 8. 读这份地图时的两个提醒

1. **`SpendLedger.cs` 一个文件装了三件事**：账本构建、`SpendSummary` 区间汇总、`SpendFormat` 格式化。改 P0-1 时注意 `SpendSummary.Build` 的 `selected` 同时包含三种 `Kind`，`Requests` 之外其它字段都显式排除了小时行——不要"顺手"把 `hourly` 也一起改（`:452` 那行是**故意**收小时行的）。
2. **同一个数据源有两套"读取路径"是设计而非重复**：快照（`SnapshotCache`/`SnapshotSource`，秒级 UI 用）与统计（`SpendLedger`/`UsageRepository`，按需构建）。快照是"当前读数"，账本是"历史明细"，两者口径不同、互不替代；唯一共享的文件只有 `Data\` 目录本身。

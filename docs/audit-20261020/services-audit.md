# PulseWin Services 层审计报告（只读）

- 审计日期：2026-10-20
- 审计对象：`D:\Program-Work\52Tools\PulseWin\Services\`（含 `Services\Spend\`）
- 版本基线：`PulseWin.csproj` `<Version>1.21.6</Version>`
- 审计方式：**全量逐文件通读**，未抽样。39 个 `.cs` 文件、按本机实读 9,458 行（含注释与空行；任务书给的 8,136 行应是去空行/注释后的口径）。
- 排除目录：`bin\`、`obj\`、`publish\`（构建产物）。
- 写入范围：本文件与 `services-map.md`，均在 `docs\audit-20261020\` 下。**未修改任何源码**。
- 行号口径：全部来自逐文件实读（与 `Select-String` 交叉核对过关键条目）。跨层引用的 `Views\` / `App.xaml.cs` 行号仅作定位证据，不计入清单。

---

## 0. 结论摘要

| 级别 | 条数 | 一句话 |
|---|---|---|
| **P0** | **1** | 「调用次数」在近 35 天区间内被天级聚合行 + 小时聚合行**双算**，界面与 CSV 导出都是错的（实测今天 916 次显示成 ~1832 次）。 |
| **P1** | **7** | 4 处共享可变状态无锁（两个静态 `Dictionary`、一个实例 `Dictionary`、一个 `List`）；1 处会话解析的异常边界太粗会**静默丢整段历史**；2 处降级路径本身会造错数据/丢写。 |
| **P2** | **17** | 死代码、重复实现、原子写残留、静默吞异常、口径夹取不一致、可测试性缺口等。 |

**最该先修的三件事**

1. `SpendLedger.cs:520` 的 `Requests` 口径（P0-1）——一行改动，改完界面/导出的调用次数立刻对。
2. `UsageEngine.cs:55/334-342` 的 `_instanceStatus` 与 `UsageEngine.cs:301` 对 `AppSettings.OpenCodeAccounts` 的并发访问（P1-1、P1-2）——多开一个 OpenCode 账号就会踩到，症状是"某一轮同步莫名不做"。
3. `DshUsageStore.cs:360` 的单行异常中断整份会话（P1-5）——DSH 是本机最大的用量来源之一，一旦某会话中间出现一行坏 JSON，那一行之后的所有记录会**永久**缺失（缓存还会把截断结果固化下来）。

---

## 1. 历史踩坑复核（6 条，逐条给结论）

| # | 历史坑 | 结论 | 证据 / 残留风险 |
|---|---|---|---|
| 1 | 同步循环被一次 HTTP 超时永久打死（v1.20.5） | **已彻底修好**，三层边界都在 | ① 各客户端把 `TaskCanceledException` 转成普通异常：`CommandCodeApiClient.cs:191-194`、`DeepSeekApiClient.cs:287-290`、`OpenCodeGoApiClient.cs:84-87`、`OpenCodeConsoleClient.cs:104-111`、`OpenCodeUsageStatsClient.cs:268-274`；② `UsageEngine.RunRoundAsync` 用 `catch (OperationCanceledException) when (token.IsCancellationRequested)` 区分"进程退出"与"通道自己的超时"（`UsageEngine.cs:219-233`），另有 2 分钟 watchdog（`UsageEngine.cs:157, 205`）；③ `Loop` 只对取消返回（`UsageEngine.cs:166-184`），并有 `EnsureLoopRunning` 自愈（`UsageEngine.cs:139-147`）。残留：自愈只在"手动刷新"时触发（见 P2-13）。 |
| 2 | 凭据候选被过期 Key 挡住（v1.21.3） | **已修好** | `UsageEngine.SyncSourceAsync` 对每个候选单独 try/catch，鉴权失败只记 `authFailure` 后**继续试下一个**（`UsageEngine.cs:407-437`），循环结束才 `throw authFailure ?? last`（`:440`）。残留两个小问题：`authFailure` 优先级高于普通错误（P2-4）、DPAPI 读失败静默返回 null（P2-9）。 |
| 3 | 账本/缓存共享（SpendLedger、DshUsageStore） | **设计正确，但共享状态没锁全** | `SpendLedgerCache` 的单飞 + 引用计数实现是对的（`SpendLedgerCache.cs:52-96`，且在锁内完成）；`DshUsageStore` 的静态缓存有 `CacheGate`（`DshUsageStore.cs:65, 121-140, 217-241`）。**P1**：`OpenCodeUsageStatsClient.Cache`、`BrowserCookieStore.Cache` 完全无锁；`DshUsageStore` 把"解析被异常中断"的部分结果按最终长度缓存。**P1/P2**：`UsageRepository` 降级分支重复累加；`DeepSeekLedger`/`DeepSeekBaseline` 的 `lock` 是实例级、保护不了共享文件。 |
| 4 | OpenCode 官方读数走聚合接口、没有"自然日"档 | **符合规格，且口径写明了** | 只读 `usage/summary`、`usage/models`、`usage/cost-by-day`（`OpenCodeUsageStatsClient.cs:216-241`），不做请求翻页；`WindowFor` 把「今天」映射成 `24h` 并标注"近 24 小时"（`:159-164`），界面用的也是这个 label（`SpendWindow.xaml.cs:1753`）。当日滞后：官方 `cost-by-day` 只用于按天展示，不参与"今天"口径，未见矛盾。 |
| 5 | DSH 读 zstd 事件流（Node 只能解第一帧的坑） | **本仓库没有这个坑**（已实证） | `DshUsageStore.Parse` 只 new 一个 `DecompressionStream`（`DshUsageStore.cs:292`），ZstdSharp.Port 0.8.8 会连续解完所有帧。实证（12:10 采样，会话仍在写入）：117 个会话文件共 **6,268** 条 `assistant/message`（其中带 `usage` 的 **6,264** 条），而 `Data\usage-history.sqlite` 的 DSH `sum(requests)=5,865`、watermark 为 `2026-10-19T20:28`；按时间戳统计该 watermark **之后**的带 usage 步数为 **401**，`6,264 − 401 = 5,863`，与 5,865 相差 2 条（去重键/边界误差）。若只解第一帧，入库数应只有几百条。**结论：不要因为"Node 有坑"就改写这里**；真正的风险在 P1-5。 |
| 6 | 峰谷时段 / 额度窗口 / 向下取整 vs 四舍五入 | **取整已按网页公式对齐**；峰谷有口径缺口 | 网页口径用整数四舍五入公式 `(used*200+limit)/(limit*2)`（`OpenCodeConsoleClient.cs:181`），并用 `ExactPercent` 覆盖 API 的向下取整整数（`UsageEngine.cs:690-709`），显示端 `PoolData.PercentText` 的 `:0` 同样是四舍五入（`SnapshotSource.cs:50`）——两侧已一致。峰谷：`RangeHourly` 只由 `agg_hour` 喂（`ChannelUsage.cs:194-200`），而小时行只保留 35 天（`UsageRepository.cs:35`），所以「全部区间」的时段分布实际只覆盖近 35 天 → P2-7。 |

---

## 2. P0（功能/数据错乱）

### P0-1　`SpendSummary` 的「调用次数」把小时聚合行又算了一遍，近 35 天区间直接翻倍

- **位置**：`Services\Spend\SpendLedger.cs:520`（配合 `:392-394`、`:411-419`、`:452`）
- **问题**：`Build` 里 `selected` 同时包含天级聚合行（`Kind=Day`）、小时聚合行（`Kind=Hour`）和未入库明细（`Live`）。代码在 `:419 if (!isHourRow)` 里累加 token/成本/请求之外的东西，**唯独最后构造对象时用的是 `selected.Sum(e => e.Requests)`**，于是 `agg_hour` 行（它就是为了"另一个切法"而存在的重复数据）被再算一次。
- **实测证据**（只读查询 `Data\usage-history.sqlite`）：
  - `agg_day` 2026-10-19：`sum(requests)=916`
  - `agg_hour` 20261019：`sum(requests)=916`（同一批量的另一种切法）
  - 于是「今天」区间 `SpendSummary.Requests ≈ 1,832`（再加本小时尚未入库的明细）。
- **触发条件**：任何覆盖"已进仓库的日子"的区间——也就是 `今天 / 近 7 天 / 近 30 天` 的**默认路径**，只要仓库里有 `agg_hour`（保留 35 天）。不需要任何异常或特殊操作。
- **影响面**：
  - 总览"范围行"：`SpendWindow.xaml.cs:890, 903`（`{summary.Requests:N0} 次调用`）
  - CSV 导出：`SpendWindow.xaml.cs:2468`（`调用,{summary.Requests}`）
  - 快照/对比卡：`SpendWindow.xaml.cs:2434`
  - 同文件 `MonthForChannel`（`:837`）**已经**跳过 Hour 行并注释"否则总量翻倍"，说明这是遗漏而非设计。
- **建议修法**（改 3 处，不动其它逻辑）：
  1. 在 `Build` 的累加段（`SpendLedger.cs:396` 附近）加 `long requests = 0;`
  2. 在 `if (!isHourRow) { ... }` 块内（例如 `:424` `cost += entry.Cost;` 之后）加 `requests += entry.Requests;`
  3. 把 `:520` 的 `selected.Sum(e => e.Requests)` 改成 `requests`。
- **回归验证**：修完后「今天」的调用次数应等于当天 `agg_day` 的 `sum(requests)`（当前 916），且与套餐页（`ChannelUsage.Requests`，本来就正确）一致。

---

## 3. P1（潜在崩溃 / 泄漏 / 静默数据丢失）

### P1-1　`_instanceStatus` 是普通 `Dictionary`，却被并行同步任务同时写、UI 同时读

- **位置**：`Services\UsageEngine.cs:55`（字段）、`:334-342`（`InstanceStatus` 惰性插入）、`:470`（并行任务里调用）、`:326-331`（`StatusForSource` 供 UI 读）
- **问题**：`SyncOnce` 用 `Task.WhenAll(due.Select(...))` **并行**同步所有条目（`UsageEngine.cs:273-274`）；每个 OpenCode 附加账号走 `SyncOpenCodeAccountAsync` → 第一行就 `InstanceStatus(entry)`（`:470`），会对同一个 `Dictionary` 做插入。**两个以上附加账号 = 两个线程同时写**。同时设置窗口/卡片在 UI 线程读它（`Views\SettingsWindow.xaml.cs:305`、`Views\SourceCard.cs:405`）。`Dictionary` 的并发读写是未定义行为，最坏情况是插入死循环（CPU 100%）。
- **触发条件**：设置里添加 ≥2 个 OpenCode 附加账号（`opencode:2`、`opencode:3`），或"同步进行中打开设置页"。
- **影响**：状态对象丢失/重复（rail 显示旧状态、`FetchedAt` 不更新），概率性线程池死循环。
- **建议修法**：把 `:55` 改成
  `private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceStatus> _instanceStatus = new(StringComparer.Ordinal);`
  并把 `:336-341` 改为 `return _instanceStatus.GetOrAdd(entry, _ => new SourceStatus());`（两行，其余调用点不用改）。
  更彻底的做法：在 `SyncOnce` 的 `Task.WhenAll` **之前**单线程预建好所有 instance 的状态对象（`foreach (var entry in due) if (IsInstanceKey(entry)) InstanceStatus(entry);`），同步路径只读不写。

### P1-2　后台同步线程枚举 `AppSettings.Current.OpenCodeAccounts`，UI 线程同时 Add/Remove

- **位置**：`Services\UsageEngine.cs:301`（`foreach (var account in AppSettings.Current.OpenCodeAccounts)`，被 `SyncOnce` 的 `:250 .ToList()` 在**后台线程**完整枚举）；写方：`:764`（Add）、`:784`（RemoveAll）、`:804`（替换元素）
- **问题**：`AppSettings.OpenCodeAccounts` 是 `List<OpenCodeAccountInfo>`（`AppSettings.cs:147`），`List` 的枚举器带版本号校验。UI 线程在"添加账号"时先 `Add`（`:764`）再 `Save()`，这个窗口与后台枚举重叠就会抛 `InvalidOperationException: Collection was modified`。
- **触发条件**：同步轮正在跑（默认每 60s 一次，`SyncOnce` 里枚举只有微秒级窗口，但添账号会做一次网络校验后立刻 Add）时点「添加账号」/「删除」。
- **影响**：`RunRoundAsync` 兜住异常（`UsageEngine.cs:234-238`）→ **那一轮整轮不同步**，界面表现为"数据 X 分钟前 · 未能刷新"+ 诊断里一条"同步一轮失败"。用户很可能把它当成网络问题。
- **建议修法**（二选一）：
  - 最小改动：`UsageEngine.cs:301` 改为 `foreach (var account in AppSettings.Current.OpenCodeAccounts.ToArray())`——`ToArray()` 仍会枚举，**不能**单独解决，需配合加锁；
  - 推荐：给 `AppSettings` 加一把内部锁（复用 `AppSettings.cs:19` 的 `Gate`），提供 `IReadOnlyList<OpenCodeAccountInfo> AccountsSnapshot()`（锁内 `ToList()`）供引擎用；`AddOpenCodeAccountAsync`/`RemoveOpenCodeAccount`/`RenameOpenCodeAccount` 的 `List` 改动也放进同一把锁。引擎侧把 `:301` 改成遍历快照。
  - 若不想动 `AppSettings`：把清单改成"每次重建新 List 再整体赋值"（copy-on-write），即 `settings.OpenCodeAccounts = newList;`，只在赋值那一瞬有原子性——配合 `:301` 先取局部变量即可。

### P1-3　`OpenCodeUsageStatsClient.Cache` 是静态 `Dictionary`，后台同步线程与 UI 同时读写

- **位置**：`Services\OpenCodeUsageStatsClient.cs:86-87`（字段）、`:96-100`（`Invalidate` → `Cache.Clear()`）、`:128-130`（`LoadFromDisk` 写）、`:192-198`（读）、`:264`（写）、`:135-144`（`SaveToDisk` 里 `Cache.Values.Select(...)` 枚举）
- **问题**：写方有两类：后台同步（`UsageEngine.cs:628` 每轮调 `TryFetchAsync("30d")`）、UI（`SpendWindow.xaml.cs:1754` 模型页、`:410` 「重新读取」→ `Invalidate()`）。`Dictionary` 并发写可能导致内部桶损坏（读取时死循环）；`SaveToDisk` 的 `foreach (Cache.Values)` 与另一线程 `Cache[range]=...` 重叠则会抛 `InvalidOperationException`，被 `:143` 的裸 `catch` 吞掉——表现为"落盘缓存时好时坏"。
- **触发条件**：开启「浏览器会话明细」后，同步轮与用量窗同时活动（这是常态）。
- **建议修法**：
  1. 加 `private static readonly object CacheGate = new();`，`:96-100`/`:192-198`/`:264-265`/`:120-133`/`:135-144` 全部进锁；
  2. 或改 `ConcurrentDictionary<string, (OpenCodeUsageStats, DateTimeOffset)>`，`SaveToDisk` 先 `Cache.ToArray()` 再序列化（避免枚举时被改）。
  3. 顺手把 `:140 File.WriteAllText(CacheFile, ...)` 改成 `AtomicFile.WriteText(...)`（见 P2-2）。

### P1-4　`BrowserCookieStore` 的静态 `Cache`/`ManualStores`/`LastError` 无任何同步

- **位置**：`Services\BrowserCookieStore.cs:22-23`（两个 `Dictionary`）、`:26-30`（`LastError`/`LastSource` 静态属性）、`:45-48`/`:54`/`:74`（写）、`:99`/`:105`/`:112`（`Remove`）、`:118-128`（`ManualStores` 惰性插入）
- **问题**：这个类是"谁都能调"的静态工具。调用方横跨两个线程：
  - 后台同步：`OpenCodeConsoleClient.cs:76`、`OpenCodeUsageStatsClient.cs:200`、`CommandCodeWebSession.cs:205`（三者可能同轮并行，因为 `SyncOnce` 并行 + `MergeWebMetersAsync` 在任务里）；
  - UI：设置页 `SaveManual`/`ClearManual`/`HasManual`（`Views\SettingsWindow.xaml.cs` 的登录按钮路径，`BrowserLoginTargets` 相关）。
  `ManualStores` 的 `TryGetValue`+`Add` 是典型 check-then-act，两个线程同站点首调会各建一个 `DpapiSecretStore`；`Cache` 并发写的危害同 P1-3。
- **触发条件**：启用浏览器会话明细 + 首轮同步（缓存冷）+ 用户同时点设置页登录。
- **建议修法**：`BrowserCookieStore.cs:22` 上方加 `private static readonly object Gate = new();`，把 `GetCookieHeader` 整个方法体、`Invalidate`、`SaveManual`、`ClearManual`、`HasManual`、`ManualStore` 全部 `lock (Gate)`；`LastError`/`LastSource` 的赋值也放锁内（读侧接受"可能是上一瞬的值"）。

### P1-5　`DshUsageStore.Parse`：一行坏 JSON 就中断整份会话，而且把截断结果按最终长度缓存下来

- **位置**：`Services\Spend\DshUsageStore.cs:288-364`（`try` 包住整个 `while`）、`:360-364`（`catch (Exception) { truncated = true; NoteTruncation(...); }`）、缓存写入 `:139`、缓存键 `:123-124`
- **问题**：注释说"半行/坏行:跳过"（`:273-275`），但**实际实现是整份中止**：`ParseMessage`（`:398-439`）里的 `JsonDocument.Parse`、`step/start`/`session`/`subagent/catalog`/`session/title` 分支的 `JsonDocument.Parse`，任何一个抛 `JsonException` 都会跳出 `while`，该行之后的**全部**记录丢失。更糟的是：`Read()` 在 `:136-140` 会把这份"被中断的结果"按 `(info.Length, LastWriteTicks)` 存进缓存；文件继续增长后重新解析仍会在这同一行中断，于是**同一位置之后的历史永久缺失**，且不会有任何用户可见提示（只有一条"读到未写完的尾部"的诊断，而它其实是误报——文件是完整的，是解析器自己停了）。
- **触发条件**：① 某行 JSON 语法坏（写盘中断的残留、工具写入异常）；② 未来 DSH 改了某个事件的字段类型（例如把 `usage.inputTokens` 变成字符串），`JsonDocument.Parse` 不抛，但 `ParseMessage` 里 `GetLong` 返回 0 → 记为 0 token（静默少计，属另一类）；③ 半行只在**末尾**时无害（这才是注释想说的场景）。
- **影响**：DSH 是三大来源之一（本机约 6,260 步），静默少计且缓存固化，重开程序也不会恢复。
- **建议修法**：
  1. 把"逐行解析"的异常就地吃掉：在 `while` 内包一层 `try { ... } catch (JsonException) { badLines++; continue; }`，`while` 外只保留"流/文件级"异常（`IOException`、`ZstdException` 等）作为截断判定；
  2. `:364` 的 `truncated` 语义改成"是否在**最后一行**中断"，并按它决定是否允许缓存（若中途中断则 `Store` 跳过，或把 `Truncated=true` 记进 `ParsedFile` 并在 `Read()` 里对 `Truncated` 的文件不落缓存——目前 `Truncated` 字段只写不读，`:94`/`:389`）；
  3. `NoteTruncation` 的文案区分"文件未写完"与"某行解析失败（有 N 行被跳过）"。
- **备注**：`Truncated` 现在完全没人读，是死字段（见 P2-5），正好可以用在 2) 上。

### P1-6　`UsageRepository.MergeIn` 的降级分支不清空 `remaining`，会把 90 天前的明细算两遍

- **位置**：`Services\Spend\UsageRepository.cs:133-139`
- **问题**：
  ```csharp
  catch (Exception ex) {
      Diagnostics.Note("用量仓库读写失败,本次统计只用源库明细", ex);
      aggregated.Clear();
      foreach (var entry in live) remaining.Add(entry);   // ← remaining 里已经有东西了
  }
  ```
  `remaining` 在 `try` 里已经被填过（`:107-111` 把早于 `cutoff` 的老行加进去）。一旦异常发生在循环之后（最典型：`:130-131` 读 `agg_day`/`agg_hour` 时失败、或 `LoadDayRows` 的 `GetInt64` 类型不符抛 `InvalidCastException`），`live` 会被**整体再追加一遍** → 老行（>90 天）在统计里出现两次：token、金额、请求数全部翻倍，且 `Notes` 里只有一条"仓库读写失败"，用户看到的是"数字突然变大"。
- **触发条件**：仓库 SQLite 读失败/被占用/结构变更（`schema_version` 被人改、列类型变了）。属于"降级路径自己再造一个错"。
- **建议修法**：`:137` 之前加 `remaining.Clear();`（一行）。更稳的是把降级整体改写为"重建返回值"：`return (Array.Empty<SpendEntry>(), live);`（`IReadOnlyList` 语义下直接返回 `live`，不碰 `remaining`）。

### P1-7　快照/账本 JSON 的"读"与"原子替换"互相踩：写入失败 + `.tmp` 残留

- **位置**：写：`Services\AppDataPaths.cs:119-136`（`AtomicFile.TempPath`/`WriteText`/`WriteTextAsync`，`:127`/`:135` `File.Move(temp, path, true)`）；读：`Services\SnapshotSource.cs:283`（`File.ReadAllText(file)`，UI 线程，`MainWindow.xaml.cs:356/444` 每 15s 触发）、`Services\DeepSeekLedger.cs:114`、`Services\DeepSeekBalance.cs:47`、`Services\SnapshotCache.cs:29`
- **问题**：`File.ReadAllText` 以 `FileShare.Read` 打开目标文件（不含 `FILE_SHARE_DELETE`）；`File.Move(src, dest, overwrite: true)` 走 `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`，需要目标可被替换。两者重叠时写方抛 `IOException`（共享冲突）。异常一路上抛到 `SnapshotCache.SaveAsync`（`SnapshotCache.cs:42-46`，无 catch）→ `SyncSourceAsync` 的 catch（`UsageEngine.cs:443`）→ 该源本轮被判"同步失败"（状态变 `Stale`、卡片显示红字），但 API 其实已经成功取到数据。同时 `AtomicFile` **没有** `finally` 清理，失败后 `Data\snapshot.json.<pid>.tmp` 永久残留（`MainWindow` 每 15s 读一次，长跑会积累多个 pid 的残留文件）。
- **触发条件**：引擎落盘（每 60s，三个源 + 附加账号各一次）与 rail/用量窗读快照的时间点重叠。单次概率低，但每 15s 一次读、一天上千次落盘，属"偶发但必然会发生"。
- **建议修法**：
  1. 读取侧统一改成允许替换的共享模式，例如新增 `AtomicFile.ReadAllTextShared(path)`：`using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var sr = new StreamReader(fs); return sr.ReadToEnd();`，并把 `SnapshotSource.cs:283`、`DeepSeekLedger.cs:114`、`DeepSeekBalance.cs:47` 换过去；
  2. 写入侧给 `File.Move` 加 3 次 × 30ms 的重试（只重试 `IOException`），仍失败则记诊断并清理临时文件；
  3. `WriteText`/`WriteTextAsync`/`WriteBytes` 用 `try { ... } finally { if (File.Exists(temporaryPath)) try { File.Delete(temporaryPath); } catch { } }` 收口。

---

## 4. P2（代码质量 / 健壮性 / 死代码）

> 按"同一主题合并成一条"的方式列，每条仍给到文件:行号与改法。

### P2-1　`AtomicFile.WriteTextAsync` 被取消时留下半截临时文件
- `SnapshotCache.SaveAsync`（`SnapshotCache.cs:42-46`）把同步令牌直接传给 `File.WriteAllTextAsync`（`AppDataPaths.cs:126`）。令牌在写盘途中被取消（`RunRoundAsync` 的 2 分钟 watchdog、进程退出）→ `WriteAllTextAsync` 抛 `OperationCanceledException`，`:127 File.Move` 不执行，临时文件留在 `Data\`。
- **改法**：同 P1-7 的 3)；另建议 `:122-127` 里不要透传取消令牌到"写文件"这一步——落盘是最后一步且极短，用 `CancellationToken.None` 更安全（宁可多写一次，也不要留垃圾）。

### P2-2　`OpenCodeUsageStatsClient.SaveToDisk` 用非原子写，且缓存文件永不清理
- `OpenCodeUsageStatsClient.cs:135-144`：`File.WriteAllText(CacheFile, ...)`。断电/崩溃会留下截断的 JSON（`LoadFromDisk` 的裸 `catch` 会当没有，可接受），但更实际的问题是：这里与 `AtomicFile` 的约定不一致（全项目只有它和命令诊断输出不用原子写）。
- **改法**：`:140` 改 `AtomicFile.WriteText(CacheFile, json);`（`AtomicFile` 是 `internal`，同在 `PulseWin` 程序集内可直接用）。

### P2-3　`DeepSeekLedger` / `DeepSeekBaseline` 的 `lock` 是实例级，保护不了共享文件
- `DeepSeekLedger.cs:34`（`private readonly object _gate = new();`）、`:39-66`（读-改-写整段在锁内）、`:124-134`；`DeepSeekBaseline.cs:34`。但 `DeepSeekLedger` 有**两个实例**：`UsageEngine.cs:96`（长期）与 `SnapshotSource.cs:229/252`（每次 `LoadAll` 都 `new DeepSeekLedger(Paths)`）。两个实例各有一把锁 → 互不排斥；`Record` 的"读全量-改-写"与 UI 侧 `TodayHourlySpend` 的 `ReadAllText` 重叠时，写方 `File.Move` 可能共享冲突（同 P1-7），`Save` 的 catch 只记一条诊断，那一小时的采样点就丢了。
- **改法**：把 `_gate` 改成 `private static readonly object Gate = new();`（两个类都改）。`DeepSeekLedger` 的状态全在文件里，静态锁是正确的粒度。

### P2-4　凭据循环结束后 `authFailure` 压过普通错误 → 误判成"需要重新登录"并静默 30 分钟
- `UsageEngine.cs:406/430/440` + `:279-283`：只要**任一**候选被服务端 401/403，最终 `throw authFailure`，即使最后一个候选其实是超时/断网。随后 `denied=true` → 状态 `AuthenticationRequired` + `_retryAfter` 静默 30 分钟（`:282`）。
- **触发**：`~/.commandcode/auth.json` 里留着一把过期 Key（第一候选），用户手填的有效 Key 这轮恰好超时。
- **影响**：界面显示"Key 无效或已过期"（错误结论），且 30 分钟内不再重试，用户以为得重新登录。
- **改法**：`:440` 改成"最后一个候选的错误优先"：`throw last ?? authFailure ?? new Exception("未知错误");`；或者只在**所有**候选都是鉴权失败时才认定 denied：循环里记 `int authFailures` 与 `int otherFailures`，`throw authFailures > 0 && otherFailures == 0 ? authFailure : last`。

### P2-5　DSH：`ParsedFile.Truncated` 只写不读（死字段），`FromUnixMs` 把坏时间当"现在"
- `DshUsageStore.cs:94`（record 成员）、`:286`/`:362`/`:389`（赋值，无读取方）；`:540-544` `FromUnixMs` 的 `catch (ArgumentOutOfRangeException) { return DateTime.Now; }`——一条时间戳坏掉的记录会被算进"今天"，污染当日统计且无法辨认。
- **改法**：`Truncated` 用在 P1-5 的缓存判定上；`FromUnixMs` 改成返回 `DateTime?`，`Parse` 里 `?.` 过滤掉坏记录（宁可少一条，不要错一条——与本项目"不猜数字"的原则一致）。

### P2-6　`DshActivity` 每秒枚举并 `stat` 一百多个投影文件（UI 线程）
- `DshActivity.cs:143-156`（`Directory.EnumerateFiles(_directory, "*.json")` + 每文件 `File.GetLastWriteTimeUtc`），调用方 `MainWindow.xaml.cs:484-488` 在渲染回调里**每秒**执行一次。本机 `~/.dsh/storages/session_projcache/sessions` 有 **117** 个文件 → 每秒 117 次 syscall。
- **改法**：给 `RecentProjections` 加 2~3 秒的节流（记录 `_lastScanAt` + 缓存结果），或在 `Poll()` 里判断"距上次扫描 < 2000ms 就直接返回上次结果"。DSH 的"在跑"判定本身有 5 分钟宽限，秒级精度并不必要。

### P2-7　`RangeHourly`（峰谷分布）只覆盖 35 天，却常被当成"整个区间"
- `ChannelUsage.cs:194-200`（只由 `Kind==Hour` 喂）+ `UsageRepository.cs:35`（`HourRetentionDays = 35`）+ `:302`（按 35 天清理）。选「全部」区间时，token/金额是全史，而 24 小时分布只有近 35 天，页面上两组数字并列却没有覆盖说明。
- **改法**：`ChannelUsage` 加 `bool RangeHourlyPartial`（当 `span==All && 仓库最早小时行 > ledger.Earliest` 时为真），界面据此在时段分布标题上标"近 35 天"；或干脆把 `agg_hour` 的清理阈值提高到与 `agg_day` 一致。

### P2-8　OpenCode 网页读数与 API 的"指纹匹配"允许 ±1，存在误配的可能
- `UsageEngine.cs:669-686`（`WorkspaceMatches`：要求 `apiUsed <= 网页Percent <= apiUsed+1`，全部窗口满足才采用）、`:654-662`（第一个命中者胜）。若账号下有**两个工作区用量接近**（例如都刚过 10%），会取到列表里的第一个，把别的工作区的额度安到这个账号上；`:609` 每次匹配失败还会刷一条诊断（每轮一次）。
- **待确认**：本机 `orgs` 目前只有 1 个工作区时无法验证误配概率。建议在匹配成功时也记一条诊断（带上 workspaceId 与三个百分比），便于事后核对是否为同一个工作区。
- **改法**：把 `:609` 的失败诊断改成"内容变化才记"（同 `_lastError` 的做法），避免每 60s 一条。

### P2-9　`DpapiSecretStore.Read` 静默吞掉"解密失败"，与"没配 Key"不可区分
- `CredentialResolver.cs:29-52`：`catch (CryptographicException) { return null; }`、`catch (IOException) { return null; }`。DPAPI entropy 变更、凭据文件被破坏、跨用户拷贝，全都表现为"未配置凭据，请在设置中输入 API Key"（`UsageEngine.cs:396-401`），用户会以为是自己没填。
- **改法**：两个 catch 里各加 `Diagnostics.Note("读取加密凭据失败", ex);`（不改变返回 null 的语义）；设置页可据 `Diagnostics.Recent()` 提示"已保存的 Key 解不开，请重新输入"。

### P2-10　死代码与未使用成员（已逐一确认无调用方）
| 位置 | 内容 | 结论 |
|---|---|---|
| `UpdateChecker.cs:33` | `public static event Action? Changed;`（`:72` 触发） | 全仓无 `+=` 订阅者，事件永远空转 |
| `Spend\ChannelRegistry.cs:93-96` | `Invalidate()` | 全仓无调用（`ChannelRegistry.Current` 会被永久缓存，改 ZCode 配置后需重启） |
| `Spend\UsageRepository.cs:144-148` | `DeleteForRebuild()` | 无调用方；`meta` 里的 `watermark`（`:122-123`）也只写不读 |
| `Spend\ModelPrices.cs:289-292` | `NormalizedVariants` 只 `yield return key` | 与 `:260` 的 `TryGetValue` 完全重复，`TryVendorLookup` 的第二轮循环是空转 |
| `Spend\SpendStores.cs:199` | `int iId = selected.IndexOf("id");` | 赋值后从未使用 |
| `Spend\DshUsageStore.cs:94/389` | `ParsedFile.Truncated` | 只写不读（见 P2-5） |
- **建议**：`UpdateChecker.Changed` 与 `ChannelRegistry.Invalidate` 二选一——要么接上（设置页改代理/渠道后需要失效），要么删掉；`DeleteForRebuild` 建议保留但接到设置页"重建统计库"按钮（现在只能手删 sqlite）。
- **注意**：`ModelPrices.NormalizedVariants` 删掉前确认 `TryVendorLookup`（`:257-267`）的语义没变——它就是"先规范化键、再原样键"，删掉空转循环即可，行为不变。

### P2-11　`AppSettings` 的字段复制写了三遍（`Save` / `Sanitized` / `Clone`），加一个设置要改三处
- `AppSettings.cs:227-266`（`Save` 把 `clean` 逐字段回写）、`:268-303`（`Sanitized` 构造新对象）、`:400-422`（`Clone` 第三份拷贝）。三份清单目前一致，但历史上已经出现过"新字段只在两处加"的问题（见类注释里 `VisibleSources` 的默认值坑，`:51-57`）。
- **改法**：`Clone()` 直接 `=> Sanitized()`（`Sanitized` 已经是"归一后的全新对象"，语义就是深拷贝）；`Save()` 保留逐字段回写但加一条提醒注释。或引入 `record`/`with` 式的唯一构造点。

### P2-12　`AppSettings.Save()` 逐字段改共享单例，后台线程可能读到"半新半旧"的组合
- `AppSettings.cs:227-266`：`Save` 在 UI 线程把 20 多个字段依次赋回 `this`，其间后台同步线程可能读到"新的 `VisibleSources` + 旧的 `OpenCodeAccounts`"。`Save()` 本身无锁（`Gate` 只保护 `Load`）。
- **影响**：极端下出现一轮"多同步了一个源/少同步了一个源"，无持久损害。
- **改法**：`Save()` 整体 `lock (Gate)`（与 `Current` 的 getter 同一把锁）；或者照 P1-2 的建议改成 copy-on-write 的不可变快照。

### P2-13　同步循环的自愈只在"手动刷新"时触发
- `UsageEngine.cs:112-147`：`EnsureLoopRunning` 只由 `RequestRefreshNow`（`:118-133`）调用。当前 `Loop` 的异常边界是完整的（见历史坑 1 复核），但一旦将来有异常从 `Loop` 里漏出（例如 `_signal.WaitAsync` 在 `Dispose` 竞态下抛 `ObjectDisposedException`），循环就此停摆，且**没有任何定时器会把它拉起来**——这正是 v1.20.5 的用户体验。
- **改法**：`Loop` 的 `while` 体内再包一层 `try { ... } catch (Exception ex) { Diagnostics.Note("同步循环异常,1 秒后重试", ex); }` + `await Task.Delay(1000, token)`；这样"循环永不退出"变成结构性保证，而不是靠逐个 catch 的自觉。

### P2-14　`RequestRefreshNow` 的 `Release` 有 `SemaphoreFullException` 风险
- `UsageEngine.cs:125`：`if (_signal.CurrentCount == 0) _signal.Release();` 是 check-then-act。`SemaphoreSlim(0,1)` 在两次并发调用之间可能都被判为 0 → 第二次 `Release` 抛 `SemaphoreFullException`，而方法只 catch 了 `ObjectDisposedException`（`:130-132`）→ 异常直接抛给 UI 调用方（点"立即刷新"变成一个未处理异常）。
- **触发**：目前 `RequestRefreshNow` 的调用点基本都在 UI 线程（`App.xaml.cs:236`、设置页、账号增删），实际很难并发；属于"将来一旦从后台调用就炸"的隐患。
- **改法**：`:125` 改成 `try { _signal.Release(); } catch (SemaphoreFullException) { }`（放弃 `CurrentCount` 判断，直接吞满异常）。

### P2-15　`Diagnostics.Note` 在锁内做全量落盘，UI 线程也会被它拖住
- `Diagnostics.cs:20-30`（`lock (Gate) { Entries.Add(...); Flush(); }`）、`:42-53`（`Flush` 每次把 100 行整体 `AtomicFile.WriteText`）。`Note` 的调用方既有后台（同步、解析）也有 UI（`Views`、`App` 里的 catch）。
- **影响**：磁盘慢/杀软扫描时，UI 线程上的一次 `Note` 就是一次同步文件写。
- **改法**：`Flush` 移出锁（锁内只做 `Entries` 变更并取一份快照 `string[]`），或加 200~500ms 的合并写（节流）。诊断是"排查用"，丢最后一条无妨。

### P2-16　`UsageRepository` 每次构建都全表读 `agg_day` + `agg_hour`
- `UsageRepository.cs:130-131` + `:310-376`：`LoadDayRows`/`LoadHourRows` 没有 `WHERE`，把整张表读成对象。天级保留 10 年（`:34`），重度用户几年后每次打开用量窗都要把全史聚合行读进内存（当前 150 行，量级不是问题，但曲线是线性增长）。
- **改法**：`MergeIn` 接收 `(DateTime from, DateTime to)`，`LoadDayRows` 加 `WHERE day >= $from`（小时行同理）；`SpendLedger` 需要全史时（`SpendSpan.All`）再传 `MinValue`。

### P2-17　`TraySummary` 自己不截断，靠调用方兜底
- `TraySummary.cs:17-25` 的注释写"超了截断"，但方法里没有截断；真正截断在 `App.xaml.cs:1050`（`if (tooltip.Length > 63) tooltip = tooltip[..63];`）。`NotifyIcon.Text` 的 63 字符硬限制是 WinForms 的约束——任何新调用方（诊断、截图工具）忘了截就会抛。
- **改法**：把截断移进 `BuildTooltip`（末尾 `return text.Length > 63 ? text[..63] : text;`），调用方那行可以保留（幂等）。

---

## 5. 口径与数据一致性核对（专项）

| 口径点 | 现状 | 判定 |
|---|---|---|
| 四类 token 与来源总量的差 | ZCode 减缓存重叠、推理不二次加（`SpendStores.cs:230-237`）；DSH 四类直接取用（`DshUsageStore.cs:371-377`，`Math.Max(0, total-四类)` 进 unclassified）；OpenCode 推理并入 output（`SpendStores.cs:462-466`） | **正确**，三处口径各自有注释且与各自的源一致 |
| 无价模型 ≠ 0 元 | `TokenTally.HasNegative`/`UnpricedTokens`/`SpendFormat.Amount`（`SpendLedger.cs:709-712`） | **正确**，且"真实 0 元"与"算不出"分开了 |
| 天级聚合行的时间戳 | 固定为当天 12:00（`UsageRepository.cs:332`） | **已知占位值**：`SpendSummary`（`:452 if (!isDayRow)`）与 `ChannelUsage`（`:322`）都显式把天级行排除在小时分布之外，正确；但任何新消费者都必须记得排除——建议在 `SpendEntry` 上加 `public bool IsDayPlaceholder => Kind == SpendAggKind.Day;` 让这条规则可被发现 |
| 小时行 vs 天级行 | 同一批量的两种切法，互斥使用 | **唯一漏洞是 P0-1**；其余（总量/模型/套餐/会话）都正确排除了小时行 |
| 向下取整 vs 四舍五入 | API 整数（floor）→ 网页整数（round-half-up）→ 采用 `ExactPercent` 显示再 round | **一致**（见历史坑 6） |
| 缓存命中率 | `CacheRead / (Input+CacheWrite+CacheRead)`，无输入返回 null | **正确**（"不适用"没被写成 0%） |
| 金额汇率 | `SpendFormat.Rate = AppSettings.UsdToCny`，0 = 美元原值，只影响显示 | **正确** |
| DSH 子代理归根 | 分组用根会话，去重用 `SourceSessionId`（`DshUsageStore.cs:186-195`、`AgentUsageRecord.cs:51-52`、`UsageRepository.cs:384-385`） | **正确**，且注释解释了"改归根规则不能让仓库重灌"的理由，属于本仓库质量最高的一段 |
| DeepSeek"今日消耗" | 相邻小时都有采样且余额下降才算（`DeepSeekLedger.cs:85-98`），0 点恒为 0 | **诚实但需告知**：程序没运行的小时无柱；0 点永远算不出（没有前一日 23 点）。建议 0 点那格显示"—"而不是 0，避免"没花钱"的误读 |
| OpenCode「今天」 | 官方只有 24h 滚动窗口，标签写"近 24 小时"（`OpenCodeUsageStatsClient.cs:159-164`） | **诚实**，无需改 |

---

## 6. 可测试性缺口（哪些逻辑硬编码了时间/网络/文件系统）

**已有的好接缝（应该继续用）**
- `CommandCodeApiClient(HttpClient, Func<DateTimeOffset>? utcNow)`（`:44`）、`DeepSeekApiClient(..., Func<DateTimeOffset>?)`（`:39`）、`OpenCodeGoApiClient`（`:17`）——时钟可注入。
- `CredentialResolver(..., Func<string,string?>? environmentReader, ...)`（`:87-100`）——环境变量可注入。
- `AppSettings.ParseForTest(json)`（`:196-207`）——迁移逻辑可离线验证。
- `OpenCodeConsoleClient.ParseStatusForDiagnostics(json,...)`（`:137-142`）、`UsageEngine.PickMatchingWorkspaceForDiagnostics`（`:665-667`）——纯解析函数已暴露。
- `DeepSeekBaseline.Advanced`/`UsedFraction`（`:76-86`）是纯函数。

**硬编码导致的不可测点（按影响排序）**
1. **时间**：`SpendSummary.Build/Range` 直接用 `DateTime.Now`（`SpendLedger.cs:382`），`ChannelUsageIndex.Build`（`ChannelUsage.cs:161`）、`ModelUsageIndex.Build`（`ModelUsage.cs:99`）、`ChannelUsage.Accumulator._todayDay`（`:280`）、`UsageRepository` 的 `DateTime.Now`（`:98/124/298`）、`DshUsageStore.FromUnixMs`（`:542`）、`ZCodeActivity.TodayLogPath`（`:77`）——"今天/近 7 天"的边界无法在单测里固定，跨零点/月末的行为只能用人工等时间。建议引入 `internal static Func<DateTime> Now = () => DateTime.Now;`（或 `ITimeProvider`），至少让 `SpendSummary`/`UsageRepository` 可注入。
2. **文件系统根**：`UsageStoreSupport.HomeDirectory`（`SpendStores.cs:12-13`）、`DshUsageStore.RootDirectory`（`:51`）、`ZCodeActivity`/`DshActivity` 的目录（`:54-59`/`:38-43`）全是 `Environment.GetFolderPath` 直取，虽然后两者已支持构造参数覆盖（可测），但 `DshUsageStore`/`ZCodeUsageStore`/`OpenCodeUsageStore` 的路径是 `IsPresent` 里写死的——想拿 fixture 库测解析只能改环境变量。
3. **静态单例**：`AppSettings.Current`、`ModelPrices.Current`、`ChannelRegistry.Current`、`SnapshotSource.DataPaths`（`SnapshotSource.cs:154`，静态 `new AppDataPaths()`，构造时还会写探测文件）都是进程级静态，`SpendFormat.Rate`（`SpendLedger.cs:662`）直接读单例 → 格式化结果的单测会受"本机 settings.json"影响。
4. **网络直连**：5 个静态 `HttpClient`（`OpenCodeUsageStatsClient.cs:74`、`OpenCodeConsoleClient.cs:54`、`CommandCodeWebSession.cs:37`、`ModelPricesUpdater.cs:31`、`UpdateChecker.cs:20`）+ `BrowserCookieStore` 的静态状态，都没有接口化 → `BrowserCookieStore` 的 cookie 读取无法在测试里替身（只能靠"未登录"的真实路径）。
5. **私有静态解析**：`DshUsageStore.Parse`（`:276`）是 `private static` 且路径写死，想用"真实 fixture 文件"验证多帧/截断行为，只能把 `RootDirectory` 变可注入（改成实例字段 + 构造函数参数）。

---

## 7. 建议修复顺序（按收益/成本比）

1. **P0-1**（1 行有效改动，立刻消除唯一的数字错误）
2. **P1-1 / P1-2**（两个账号就会踩，改法都是局部：`ConcurrentDictionary` + 快照/锁）
3. **P1-5 + P2-5**（DSH 静默少计，且缓存会固化；顺手把 `Truncated` 用起来）
4. **P1-3 / P1-4**（静态缓存加锁，各 5~10 行）
5. **P1-6 / P1-7**（降级路径与原子写，各 1~10 行）
6. **P2-13**（把"循环永不退出"变成结构性保证，10 行以内，防止未来回归 v1.20.5）
7. 其余 P2 按"改设置/账号相关代码时顺手改"处理（尤其 P2-10 死代码、P2-11 三份字段拷贝）。

---

## 8. 未覆盖 / 待确认（明确声明，不猜）

- **未审计**：`Views\`、`Rendering\`、`App.xaml.cs`、`Models\`、`Themes\`（只在需要确认 Services 的调用方时读取了片段，未做体系审计）。WebView2 生命周期（`BrowserLoginWindow`）在 `Views\`，本报告未评价。
- **待确认 1**：`QuotaWindow.Percent` 夹取到 0..100（`QuotaModels.cs:89-91`）与 `PoolData.Fraction` 夹取到 1.02（`SnapshotSource.cs:45-46`）并存——环的绘制用哪一个、超 100% 时是"满圈"还是"溢出"，需要看 `Rendering\` 才能定论。
- **待确认 2**：`ChannelUsage.RangeHourly` 在「全部」区间的实际展示文案（是否误导），取决于 `SpendWindow` 的峰谷卡片措辞（`Views` 侧），本报告只证明了数据覆盖只有 35 天。
- **待确认 3**：`OpenCodeUsageStatsClient` 一次 `TryFetchAsync` 在 N 个工作区时是 `1 + 3N` 次**串行**请求（`OpenCodeUsageStatsClient.cs:216-242`，每次都用同一个 30s 超时的 `HttpClient`）。本机仓库里 OpenCode 只有 1 天数据（`agg_day` 1 行），**无法据此反推账号下的工作区数量**，所以 N≥3 时是否会顶到 `RoundTimeout` 的 2 分钟（`UsageEngine.cs:157`，以及 `MergeWebMetersAsync` 那 10s 子超时 `:625`）需要真机多工作区实测。
- **未验证**：`ZstdSharp.Port 0.8.8` 的 `DecompressionStream` 在多帧/截断边界上的具体异常类型（`ZstdException`?）。结论"多帧能解"是基于本机 117 个真实文件与本仓库入库计数的实证，不是装配级验证。

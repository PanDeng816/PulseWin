# PulseWin Views + Rendering 层审计报告（只读）

- 审计日期：2026-10-20
- 审计对象：`Views\`（18 个 `.cs`/`.xaml`）+ `Rendering\`（7 个 `.cs`）
- 版本基线：`PulseWin.csproj` `<Version>1.21.6</Version>`（`net8.0-windows`，`SystemAware` DPI）
- 审计方式：**逐文件全文通读，未抽样**。共 25 个文件，按编辑器行号实读 **9,232 行**（Views 7,927 + Rendering 1,305，含注释与空行）。
  - 口径说明：任务书给的 6,966 / 1,102 行与本机 `Measure-Object -Line`（只计非空行）一致；本报告所有行号取自逐行实读，并与 `Select-String` 交叉核对过关键条目（例：`SpendWindow.xaml.cs` 的 `RenderChart` 在 1027、`RenderHeatmap` 在 1098、`task.Result` 在 447，三处 `Select-String` 与实读一致）。
- 排除目录：`bin\`、`obj\`、`publish\`（构建产物）。
- 写入范围：**仅本文件**（`docs\audit-20261020\views-audit.md`）。未修改任何源码。
- 交叉引用：同目录 `services-audit.md`（Services 39 文件，P0 1 / P1 7 / P2 17）。本报告不重复其结论，只在末尾「交叉点」一节对齐。

---

## 0. 结论摘要

| 级别 | 条数 | 一句话 |
|---|---|---|
| **P0** | **0** | 未发现"必然崩溃或必然显示错数"的 Views/Rendering 路径。唯一必错数的是 Services 的 P0-1（调用次数双算），本报告只负责列出它在界面上的落点（见 §5）。 |
| **P1** | **4** | 拖动设置滑块 → 每个事件一次全量快照重读 + 悬浮窗改尺寸（UI 线程同步 IO）；快照"读"与引擎"原子替换"相撞 → 该源从 rail 上整条消失、卡片可能指向错源；登录窗的 2FA 看门狗被共享重入标志**饿死**（从不执行）；环下读数文本缓存无上限增长。 |
| **P2** | **18** | 钩子接线顺序、越界防护、resize 不重画、自定义区间不作用于套餐页、async void/async lambda 无兜底、每帧克隆大几何、诊断模式全局副作用、文件职责过大等。 |

**最该先修的三件事**

1. **P1-1**：`SettingsWindow.xaml.cs:239-254` → `App.xaml.cs:1144` → `MainWindow.ApplySettings()` → `SnapshotSource.LoadAll()`。拖动一次"背景不透明度"滑块 = 几十次磁盘读 + 几十次悬浮窗尺寸变更。改法很小：滑块只写内存 + 复用已有的 300ms `_saveTimer` 防抖，`SettingsChanged` 拆成"只重绘"和"重载数据"两条。
2. **P1-3**：`BrowserLoginWindow.xaml.cs:301` 与 `:216` 共用 `_polling`，而 `:112`/`:113` 在同一个 Tick 里先后调用 → `ResumeIfStuckAsync()` **永远**在第一步就返回。这个看门狗正是为"GitHub 2FA/passkey 之后不回跳"写的，现在等于没写。
3. **P1-4**：`MainWindow` 的 `_textCache`（`:80`）只在 DPI 变化（`:1262`）或改设置（`:429`）时清空。余额型读数（`¥1234.5` 这类）几乎每秒都可能是新字符串，常驻托盘数周会累积上千条 `FormattedText`。加一个上限即可。

---

## 1. 先给结论：任务书四条线索的核对结果

| 线索 | 结论 | 证据 |
|---|---|---|
| ① `BrowserLoginWindow.xaml.cs:77` `async void OnLoadedAsync` 无兜底会崩进程 | **不成立（无 try/catch 缺失问题），但该文件另有更严重的真问题** | `:88-129` 整段包在 `try/catch (TimeoutException)` + `catch (Exception)` 里；且 `App.xaml.cs:51-75` 有 `DispatcherUnhandledException` 兜底（`Handled = true`，同类只弹一次框）。所以 async void 不会崩进程。**真正的问题见 P1-3**（看门狗被饿死）与 P2-6（`_ =` 丢弃的 Task、事件不解绑）。 |
| ② `SettingsWindow.xaml.cs:532` `CheckUpdate_Click` 同类问题 | **风险低，只需要补兜底** | `UpdateChecker.CheckAsync()` 自身 `try/catch` 覆盖全body（`Services\UpdateChecker.cs:64-83`），`finally` 里复位 `_checking`；`RefreshUpdateHint()` 只做字符串格式化。见 P2-6。 |
| ③ `SpendWindow` 21 处 `+=` 是否有事件泄漏 | **不成立**：21 处里只有 **10** 处是事件订阅，其余 11 处是数值/字符串累加（`:840-842`、`:1482-1483`、`:1623-1624` 的 `tokens += …`，`:1511`、`:1604` 的字符串拼接） | 10 处订阅对象全是**窗口自有的控件**或**每次重建的短命子元素**：`:365` 5 个区间 RadioButton（窗口级）、`:550/:555` 每次 `RenderViewTabs` 新建的 tab、`:301` 曲线命中矩形、`:1157` 热图格子、`:1880/:1945/:2064` 每次新建的 Canvas、`:2383` 5 个 XAML 里的 UniformGrid（有 `_autoCols` 去重，`:2359`、`:2382`）。**没有一处订阅静态/长生命周期对象**，不需要 `-=`。详见 P2-15（热图的性能，而非泄漏）。 |
| ④ `SpendWindow` 生产路径里的注入代码 / 大方法 / 同步等待 | 注入代码**只在诊断模式**（`internal` + 仅 `UiShot`/`RailShot` 调用）；**没有 >200 行的方法**；`.Result` 只有 1 处且不会阻塞 | 注入：`UiShotInjectOfficialUsage`（`:1807`）、`UiShotTabCount/Label/OpenTab`（`:2549-2568`）、`MainWindow.InjectForShot`（`:283`）/`SuppressActivity`（`:615`）/`ForceBurning`（`:618`），全部 `internal`，生产路径无调用方。最大方法 `DrawAreaChart`（`:138-309`，171 行）。同步等待：`var ledger = task.Result;`（`:447`）位于 `ContinueWith(..., FromCurrentSynchronizationContext())` 内、task 已完成，**不会**阻塞 UI 线程；本层无 `.Wait()`、无 `Thread.Sleep`、无 `Task.Run`。→ 拆文件建议见 P2-18。 |

另：`async void` 全层共 2 处（上述两个），`async Task` 8 处（`BrowserLoginWindow:137/214/252/299`、`SourceCard:426/513`、`SpendWindow:1517/1744`），另有 2 个 `async` lambda（`SourceCard.cs:111`、`:230`）——后者才是真正缺兜底的地方（P2-6）。

---

## 2. P0（功能失效 / 崩溃）

**本层无 P0。** 说明：`App.xaml.cs:51-75` 把 UI 线程未处理异常吞掉（记诊断 + 同类只提示一次），所以本层多数异常表现为"一次错误框 + 该功能当帧失效"，而不是进程退出。这也是我把下面几条定级为 P1/P2 而不是 P0 的原因——**评级依据是用户可见后果，不是异常类型**。

---

## 3. P1（必然卡顿 / 偶发错乱 / 泄漏）

### P1-1　拖动设置滑块 = 每个事件一次全量快照重读 + 悬浮窗改尺寸（UI 线程同步磁盘 IO）

- **位置**：
  - `Views\SettingsWindow.xaml.cs:239-244`（`SettingSlider_ValueChanged`）→ `:246-254`（`Apply()` 里 `SettingsChanged?.Invoke()`）
  - `Views\SettingsWindow.xaml:214-216`、`:296-298`、`:427-429`（三个 `Slider` 都挂 `ValueChanged`）
  - `App.xaml.cs:1144`（`settings.SettingsChanged += () => _main?.ApplySettings();`）
  - `Views\MainWindow.xaml.cs:427-431`（`ApplySettings()` → `_textCache.Clear(); ReloadData();`）
  - `Views\MainWindow.xaml.cs:354-356`（`ReloadData` 第一行就 `SnapshotSource.LoadAll()`）
  - `Services\SnapshotSource.cs:202-235`（`LoadAll` 逐源加载）、`:278-283`（`File.ReadAllText`）、`:229-230`/`:252-253`（余额型还要 `new DeepSeekLedger(...).TodayHourlySpend()`，又一次读盘）
  - `Views\MainWindow.xaml.cs:898-903`（`ApplyRailSize()` 每 tick 改 `Width/Height`）
- **问题**：`Slider` 在拖动过程中**连续**触发 `ValueChanged`（一次拖动几十到上百个事件）。当前每个事件都走完整链路：写设置 → `SettingsChanged` → `ApplySettings()` → **磁盘读 3~4 个快照 JSON + 反序列化 + 重算 BurnRate 采样** → `RebuildLayout()` → 改悬浮窗 `Width/Height`（分层窗口改尺寸会整窗重绘）→ `InvalidateVisual()`。
  而这三个滑块里，`SurfaceOpacity` / `AlertThreshold` **只影响绘制**，根本不需要重读快照；只有 `SyncIntervalSeconds` 需要让引擎知道，也不需要重载数据。
- **触发条件**：打开设置 → 外观页拖动"背景不透明度"，或圆环页拖"报警阈值"，或刷新页拖"同步间隔"。属**必然发生**的日常操作。
- **影响**：拖动明显发涩；每秒几十次文件打开/关闭（杀软扫描时更糟）；一旦与引擎落盘相撞，`LoadOne` 的 `catch`（`SnapshotSource.cs:334-339`）会写一条 `Diagnostics.Note`，而 `Diagnostics` 是**锁内同步落盘**（Services P2-15）→ UI 线程上再叠一次文件写。悬浮窗尺寸每帧变一次，也会让 rail 的环在拖动期间抖动。
- **改法**（三处小改动）：
  1. `Apply()` 只写内存设置，`SettingsChanged?.Invoke()` 换成已有的 300ms 防抖：`SaveSoon()` 之后在 `_saveTimer.Tick` 里统一触发一次 `SettingsChanged`（`SettingsWindow.xaml.cs:58-63` 现成的定时器）。
  2. 给 `SettingsChanged` 分两档：新增 `RenderSettingsChanged`（只 `_textCache.Clear(); InvalidateVisual();`）与 `SourcesChanged`（才 `ReloadData()`）。`SettingsWindow` 里"显示哪些源 / 圆环顺序 / 账号增删"走后者（`:126-131`、`:172-178`、`:188-230`），其余滑块/勾选走前者。
  3. `MainWindow.ApplySettings()` 拆成 `ApplyRenderSettings()` 与 `ReloadSources()`，后者只在源集合/顺序变化时调。

### P1-2　快照的"读"与引擎的"原子替换"相撞 → 该源从 rail 上消失，卡片可能指向错源

- **位置**：
  - 读（UI 线程）：`Views\MainWindow.xaml.cs:441-445`（每 15s 比对 `SnapshotSource.Stamp()`）、`:376`（`_lastStamp`）、`:354-356`
  - 读的实现：`Services\SnapshotSource.cs:283`（`File.ReadAllText(file)`，默认 `FileShare.Read`，**不含** `FileShare.Delete`）
  - 写：`Services\AppDataPaths.cs:119-136`（`AtomicFile` 的 `File.Move(temp, path, overwrite: true)`，即 `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`）——详见 services-audit **P1-7**
  - 失败降级：`Services\SnapshotSource.cs:334-339`（`catch` → 记诊断 → `return null`）→ `:223`（`if (LoadOne(...) is not { } sub) continue;`，**整条源被跳过**）
  - 下游：`Views\MainWindow.xaml.cs:387-388`（卡片只按 `shown < _subs.Count` 重配）、`:928`（`PositionCard` 用 `RingCenter(_cardFor)`）、`:959-965`（`RingCenter` 直接索引 `_ringY`）
- **问题**：`Tick` 每 15s 在 **UI 线程**同步读快照文件；引擎每 60s 落盘（三源 + 每个附加账号）。两者重叠时 `ReadAllText` 抛 `IOException`（共享冲突），`LoadOne` 吞掉并返回 null，`LoadAll` 于是**少一条源**。`_subs` 一短：`RebuildLayout` 重排 → rail 高度变化（视觉上跳一下）；更麻烦的是索引语义变了——`_cardFor` 是 `_subs` 的下标，少一条后它可能指向**另一个源**，此时详情卡显示的是别的订阅的数据（`ReloadData` 的 `:387` 只判 `shown < _subs.Count`，不校验"还是不是同一个 key"）。
- **触发条件**：引擎落盘窗口（每 60s，三源 + 附加账号各一次）与 15s 轮询重叠。单次概率低，但一天上千次落盘 × 几千次读，属"偶发但必然会发生"。
- **影响**：rail 偶发少一个环（下一轮又回来）；卡片内容与环短暂错位；若 `_cardFor` 越界则见 P2-2。
- **改法**：
  1. 读侧改成允许替换的共享模式（与 services-audit P1-7 的建议合并做）：`new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)` + `StreamReader`，在 `SnapshotSource.cs:283` 一处替换即可，三个调用方（`MainWindow`、`SpendWindow`、`SnapshotSource.LoadAllSources`）同时受益。
  2. `LoadAll` 对"读失败"不要静默少一条：缓存上一份成功结果，按 `Key` 合并——读失败的源沿用上次的 `SubData`（并保留 `FetchedAt`，让卡片自己显示"数据 X 分钟前"）。
  3. 给 `_cardFor` 改成保存 **源 Key** 而不是下标（见 P2-2）。

### P1-3　登录窗的 2FA 看门狗被共享重入标志饿死，`ResumeIfStuckAsync` 从不执行

- **位置**：`Views\BrowserLoginWindow.xaml.cs`
  - `:47-48`（`_polling` 注释写的是"上一次 await 还没回来就不再发一次"——它被当成**两个**轮询器共用的锁）
  - `:109-115`（`_poll.Tick` 里连续调用 `_ = TryCaptureAsync(); _ = ResumeIfStuckAsync();`）
  - `:299-305`（`TryCaptureAsync`：`if (_captured || _polling) return;` → … → `_polling = true;` **同步**设置）
  - `:214-217`（`ResumeIfStuckAsync`：第一行 `if (_captured || _polling) return;`）
  - 相关状态：`:50-57`（`_githubStuckSince` / `StuckGrace=4s` / `MaxAutoResumes=3`）、`:173`（停在无关 GitHub 页时置位）、`:214-246`（看门狗本体）、`:231`（`_autoResumes++` 只在这里）
- **问题**：`async` 方法在第一个 `await` 之前是**同步**执行的。每个 Tick 里 `TryCaptureAsync()` 先把 `_polling` 置为 `true`（`:305`），随后同一个 Tick 调用的 `ResumeIfStuckAsync()` 在 `:216` 直接 `return`。`GetCookiesAsync` 在两次 Tick 之间总会完成把 `_polling` 放回 `false`，但下一次 Tick 又是"先捕获、后看门狗"的顺序 → **`ResumeIfStuckAsync` 永远进不到第 218 行**，`_autoResumes` 永远是 0。
- **触发条件**：账号开了两步验证/passkey，OAuth 之后 GitHub 把窗口留在无关页（这正是 `:205-212` 注释里写的实测场景）。此时登录**不会**自动续跑，用户只看到"这页与授权无关;几秒内没往回跳就自动重新发起登录"——而它并不会。
- **影响**：注释承诺的行为不生效；该场景下用户必须自己点「继续登录」（导航条，`:289-290`）或「重新载入」（`:352-357`）。因为登录窗是模态的（`SettingsWindow.xaml.cs:419-423` 的 `ShowDialog()`），用户会卡在这一步。
- **改法**：拆成两个标志，Tick 改成
  ```csharp
  _poll.Tick += (_, _) =>
  {
      if (!_capturing) _ = TryCaptureAsync();
      if (!_resuming)  _ = ResumeIfStuckAsync();
  };
  ```
  `TryCaptureAsync` 用 `_capturing`、`ResumeIfStuckAsync` 用 `_resuming`，各自 `try/finally` 复位。顺带把 `:257` 的 `core.Source?.GetHashCode()` 换成 `core.Source` 字符串比较（哈希碰撞会让导航条少注入一次，属 P2 级细节）。

### P1-4　环下读数的 `FormattedText` 缓存无上限，常驻进程缓慢增长

- **位置**：`Views\MainWindow.xaml.cs:80`（`private readonly Dictionary<(string, double, FontWeight, Color), FormattedText> _textCache`）、`:1104-1118`（`FitLabel` 用"读数文本 + 量化后的字号"做 key）、`:1257-1274`（`NewText`：`_textCache[key] = ft;` 只增不减）、`:1262-1266`（只有 DPI 变化才 `Clear()`）、`:429`（改设置时清）
- **问题**：key 里含**文本本身**。百分比读数的取值空间有限（`10.6%` 这类，几百个），但余额型源的读数是金额字符串（`CardRenderer` 侧同款逻辑下会出现 `¥1234.56`），几乎每分钟都可能是新值；`FitLabel` 还会按 0.5pt 量化产生多个字号变体。每个 `FormattedText` 持有字形运行与字体回退信息，不是几字节的东西。常驻托盘数周不重启、又不动设置，这个字典只涨不落。
- **触发条件**：长期运行 + 余额型源（DeepSeek）或读数经常变化的源；不需要任何异常操作。
- **影响**：内存缓慢上涨（估计量级：数千条目、数 MB 到十几 MB）；同时 `Dictionary` 越来越大，查表与 `_textCache.Clear()` 的代价也随之上升。
- **改法**（任选，改动都在 `NewText` 一处）：
  - 加容量上限：`if (_textCache.Count > 512) _textCache.Clear();`（清空的代价只是重排一次文本，下一帧就恢复）；
  - 或把"余额型/金额型读数"排除出缓存（这类字符串本来就一次性的），只缓存百分比与 `--`；
  - 或者把 key 从"文本"改成"文本的槽位"（源 key + 显示种类），命中后校验 `ft.Text` 是否需要重建——最省内存，但改动稍大。

---

## 4. P2（质量 / 健壮性 / 性能 / 死代码）

> 同一主题合并成一条；每条都给文件:行号、触发条件与具体改法。

### P2-1　`HookHitTest` 在 `OnSourceInitialized` **内部**订阅 `SourceInitialized`，钩子永远装不上
- **位置**：`Views\MainWindow.xaml.cs:327-334`（`base.OnSourceInitialized(e);` 之后才 `Native.HookHitTest(this, HitArea);`）、`Rendering\Native.cs:187-205`（`HookHitTest` 内部 `w.SourceInitialized += ...` 里才 `src.AddHook`）
- **问题**：WPF 在 `base.OnSourceInitialized(e)` 里**当场**触发 `SourceInitialized`，之后再 `+=` 的处理器不会为这次初始化执行。所以 `WM_NCHITTEST` 那条 `AddHook` 从未注册，`HTTRANSPARENT` 的"形状命中穿透"是死代码。
  对照证据：同一文件的 `ApplyToolWindowStyle`/`HookScreenChanges`/`HookContextMenu`（`:255-258`）都在**构造函数**里注册，是正确的；只有 `HookHitTest` 留在 `OnSourceInitialized` 里。
- **触发条件**：每次启动都成立（不是偶发）。
- **为何一直没人发现**：`AllowsTransparency=True` 的窗口是 per-pixel alpha 的 layered window，其**命中测试由 OS 按 alpha 通道做**（alpha=0 的像素本来就放行鼠标）。所以"透明角落能点穿"这个效果由系统兜住了，`HookHitTest` 失效没有可见后果。
- **影响**：一段意在控制点击穿透的代码在空转。风险是**将来**有人为了"半透明区域也能点穿"去依赖它，或在 alpha 不为 0 的区域（例如以后给 rail 加阴影/描边层）发现点穿了/点不穿的行为与代码不符。
- **改法**：把 `:330` 这行移到构造函数（`:255-258` 旁边）；或者反过来在 `HookHitTest` 里改成"若 `HwndSource.FromHwnd(handle)` 已有句柄就直接 `AddHook`，否则才订阅事件"的双路径写法，避免调用顺序陷阱。改完请实测一次"透明角落点击是否落到下层窗口"。

### P2-2　`RingCenter(_cardFor)` 缺上界校验：源数量减少而卡片仍可见时 `IndexOutOfRangeException`
- **位置**：`Views\MainWindow.xaml.cs:923-949`（`PositionCard`，每帧被 `HandlePointer` 末尾调用）、`:959-965`（`RingCenter`：`Pt.P(_ringY[ringIndex])` 无检查）、`:387-388`（`ReloadData` 只在 `shown < _subs.Count` 时重配卡片，否则**既不隐藏也不清 `_cardFor`**）
- **问题**：`_ringY` 在 `RebuildLayout` 里重建为 `_subs.Count` 长度；`_cardFor` 是旧下标。若在卡片可见期间 `_subs` 变短（取消勾选一个源 / 附加账号被删 / P1-2 的读失败丢源），`_cardFor` 可能 ≥ `_ringY.Count` → `PositionCard` → `RingCenter` 越界。
- **触发条件**：详情卡可见时数据源数量减少。**待确认**：正常路径下指针离开 rail 就会 `HideCard()`（`:718-723`），要改设置就得把指针移到设置窗，所以实际很难同时成立（设置窗居屏、卡片贴右缘，通常不重叠）。属于"便宜的防御"而非已证实的线上问题。
- **影响**：异常会走 `App.xaml.cs:51-75` 的兜底（一次错误框），当帧的 `Tick` 中断，卡片停在上一次位置。
- **改法**：两行防御 + 一个语义修正：`RingCenter` 里 `if (ringIndex < 0 || ringIndex >= _ringY.Count) return new Point(Width / 2, Height / 2);`；`ReloadData` 的 `:387` 改成"`_cardFor` 的源 key 还在 `_subs` 里才重配，否则 `HideCard()`"，并把 `_cardFor` 由 `int?` 改为 `string?`（存 `sub.Key`）。

### P2-3　概览页的曲线与热图不跟窗口宽度重画（resize 后停在旧宽度）
- **位置**：`Views\SpendWindow.xaml.cs:1060-1077`（`RenderChart` 取 `ChartCanvas.ActualWidth` 画一次，**没有** `SizeChanged` 订阅）、`:1120-1131`（`RenderHeatmap` 用 `HeatCanvas.ActualWidth` 算列数，同样没订阅）；对照：套餐页三个画布都订阅了 `SizeChanged`（`:1880`、`:1945`、`:2064`）
- **问题**：`SpendWindow` 是 `ResizeMode="CanResize"`（`SpendWindow.xaml:5`）。概览页的每日曲线（`ChartCanvas`，XAML `:197`）与活动热图（`HeatCanvas`，XAML `:188`）只在 `Render` 时按当时的宽度画一次。拉宽窗口后曲线不变宽（右侧留白），拉窄后热图被裁（`ClipToBounds`），直到用户切一次区间才会重画。
- **触发条件**：打开用量窗 → 拖动窗口边缘。
- **改法**：给这两个 Canvas 在 `Loaded` 时订阅一次 `SizeChanged`（带 100~150ms 节流，避免拖动窗口时每像素重绘），或统一在窗口 `SizeChanged` 里重跑 `RenderChart`/`RenderHeatmap`。注意用去重集合，别每次 `Render` 都 `+=`（那才会变成泄漏）。

### P2-4　自定义区间 / 单日下钻只作用于总览，套餐 tab 与额度条仍按 `_span` 出数
- **位置**：`Views\SpendWindow.xaml.cs:488`（`_channels = ChannelUsageIndex.Build(_ledger, _span);` —— 只看 `_span`，不看 `_customActive` / `_dayFilter`）、`:512-538`（tab 徽标用这些渠道数）、`:848-857`（`RangeLabel()` 却说"当前区间"）、`:1268-1290`（套餐页副标题写"本页统计的时间范围"）、`:397-404`（`CustomApply_Click` 只 `RenderAll()`）
- **问题**：用户在顶部选「自定义 9/1~9/10」，总览所有数字切到了 9/1~9/10，但**同一屏**下方的套餐 tab 徽标、每个套餐页的 KPI、额度条里的"本区间"全部还是 `_span`（默认"今天"）的数。`ChannelUsageIndex` 目前只有 `Build(ledger, span)`（`Services\Spend\ChannelUsage.cs:159`）与 `BuildAllTime`（`:238`），没有按日期区间的重载。
- **触发条件**：选「自定义」并应用；或点热图/曲线下钻到某一天（`EnterDayFocus`，`:1207-1217`）。
- **影响**：同屏两套区间，用户会把套餐页的数字当成本区间的数字。
- **改法**：二选一——① 最小改动：`_customActive || _dayFilter is not null` 时把 `TabRow` 里的套餐 tab 置灰/隐藏，并给 `PriceSourceText` 或 `ScopeLine` 加一句"套餐页仍按「今天」统计"；② 正确做法：给 `ChannelUsageIndex` 加 `Build(ledger, DateTime from, DateTime to)` 重载（内部把 `_span` 换成显式区间），`RenderAll` 传 `EffectiveRange()`。

### P2-5　`RenderAll` 每次都在 UI 线程同步读盘，且多次全量扫 `_ledger.Entries`
- **位置**：`Views\SpendWindow.xaml.cs:494`（`_subs = SnapshotSource.LoadAllSources();` —— 每个源一个 `File.ReadAllText` + JSON 反序列化，余额型还要读 DeepSeek 账本）、`:982`（`RenderDelta` 里再 `SpendSummary.Build` 一次）、`:835-843`（`MonthForChannel` 全表扫）、`:1477-1485` / `:1616-1627`（`CountLocalSince` 全表扫）、`:1107-1113`（`RenderHeatmap` 全表扫）
- **问题**：`RenderAll` 的调用点很密：切区间（`:380`）、切 tab（`:520`/`:534`→`SwitchView`→`:572`）、刷新（`:411`）、价格表更新回调（`:430`）、加载完成（`:460`）。每次都要读全部快照文件，并对 `_ledger.Entries` 做 4~6 遍线性扫描。
- **触发条件**：每次点击区间/tab/刷新。
- **影响**：目前数据量（天级行约 150 条）下每次几毫秒到几十毫秒，属"能感觉到但不致命"；快照文件在杀软扫描或机械盘上会明显卡。与 P1-2 叠加时还会放大共享冲突概率。
- **改法**：① `_subs` 只在"额度条需要刷新"时读（把 `LoadAllSources()` 从 `RenderAll` 移到 `ReloadLedger` 的完成回调里，或按 `SnapshotSource.Stamp()` 短路）；② `RenderAll` 里把 `SpendSummary.Build` 的结果传给 `RenderDelta`（现在它自己又 Build 一次），把 `MonthForChannel`/`CountLocalSince` 的过滤结果缓存在一次遍历里。

### P2-6　`async void` / `async` lambda 缺兜底：失败后界面停在"正在验证…"
- **位置**：`Views\SourceCard.cs:111`（`_saveButton.Click += async (_, _) => await SaveKeyAsync();`）、`:230`（`addButton.Click += async (_, _) => await AddAccountAsync();`）、`Views\SettingsWindow.xaml.cs:532`（`CheckUpdate_Click`）
- **问题**：`SaveKeyAsync`（`:513-522`）与 `AddAccountAsync`（`:426-447`）内部都没有 `try/catch`。它们调用 `_engine.SaveManualKeyAsync` / `_engine.AddOpenCodeAccountAsync`——网络/凭据/DPAPI 任何一处抛异常，都会从 `async void` 边界逃出去：`_hintText` 永远停在"正在验证 Key…"、`_accountHint` 永远停在"正在验证 Key…"，用户以为在等，实际已经失败。异常本身会被 `App.xaml.cs:51-75` 吞掉（弹一次框），所以是"半失效"而不是崩溃。
- **触发条件**：验证 Key 时网络异常、代理配错、DPAPI 读失败等。
- **改法**：两个方法体各包一层 `try { … } catch (Exception ex) { _hintText.Text = "验证失败:" + ex.Message; Diagnostics.Note(...); }`；`CheckUpdate_Click` 同理加 `try/catch`（虽然 `UpdateChecker.CheckAsync` 现在自己吞，但别依赖别人的实现细节）。

### P2-7　`TintPicker.xaml` 里 `Tag="selected"` 的触发器永不命中（死代码）
- **位置**：`Views\TintPicker.xaml:20-22`（`<Trigger Property="Tag" Value="selected">`），对照 `Views\TintPicker.xaml.cs:70`（`Tag = hex`，存的是色值）
- **问题**：`Tag` 存的是 `"#4D6BFE"` 这类色值，永远不等于字符串 `"selected"`，触发器是死的。选中态实际由代码隐藏设置 `chip.BorderBrush` 实现（`:125-129`）。
- **改法**：删掉这个 Trigger（避免以后有人以为选中态是模板在管），或改成 `IsChecked`/自定义附加属性驱动，让选中态回到 XAML 一处。

### P2-8　卡片行高用 `pixelsPerDip = 96` 量，绘制却用实时 DPI，两边口径不一致
- **位置**：`Rendering\CardRenderer.cs:70-80`（`MeasureRowPitch`/`MeasureLine` → `Text("本周明细", size, …, 96)`）、`:356-359`（`Text(...)` 第 7 个参数直接当 `FormattedText` 的 `pixelsPerDip`）；绘制侧：`Views\CardWindow.cs:67`（`CardRenderer.Draw(..., Native.Scale(this), ...)`）、`Views\MainWindow.xaml.cs:915`（`CardLayout.SubCard` 决定窗高）
- **问题**：布局高度是按 `pixelsPerDip=96` 量出来的，绘制时用的却是 `1.0`（100% DPI）或 `2.0`（200% DPI）。`FormattedText` 的 `Width/Height` 虽是 DIU，但字形栅格化时的取整会让不同 `pixelsPerDip` 下的行高差零点几 DIU。卡片高度直接引用这些 `*Block`（`:52`、`:58`、`:61`、`:64`、`:67`），差量会累积到"底部一句话被裁 1px"或"底部空一条"。
- **触发条件**：任意 DPI 下打开详情卡；200% 缩放下更明显（`CardShot` 特意用 96 出图，正好掩盖了这个偏差）。
- **改法**：`MeasureLine` 改成 `Text(..., 1.0)`（纯 DIU 逻辑尺寸），并让 `CardShot` 也用 `1.0`；或者在 `CardRenderer.Draw` 里改成用与测量同源的固定 `pixelsPerDip`（绘制质量在 `dc` 层面仍由 WPF 按真实 DPI 处理）。两种都能让"量的"和"画的"一致。**待确认**：极端文本下是否真会裁 1px，需要按 200% DPI 跑一次 `--card-shot` 对照。

### P2-9　每帧克隆大几何 / 每帧解析颜色字符串 / 每帧新建 `StreamGeometry`
- **位置**：`Views\MainWindow.xaml.cs:1145-1158`（`Icons.DeepSeekGeometry().Clone()` + `new MatrixTransform`，每个源每帧一次；`DeepSeekGeometry` 是一条上千点的路径——见 `Rendering\Icons.cs:60`）、`:1153-1157`（OpenCode 方块同样 `Clone()`）、`:1178-1184`（`TintFor` → `PanelPalette.Parse(...)`，在 `DrawArcLayer` 里每层每帧调一次）、`:1230`/`:1240`/`:1249`（`RailGeometry.Arc(...)` 每帧新建 `StreamGeometry`，`Rendering\RailGeometry.cs:131-149`）
- **问题**：`DrawComposite` 每帧为每个源做 1 次几何 `Clone` + 1 次 `MatrixTransform`；`TintFor` 每层都要 `AppSettings.Current.TintFor(...)`（字符串）→ `PanelPalette.Parse`（`Models\Models.cs:42-65`：`TrimStart`、`Substring`、`Convert.ToByte`，每次 2~3 个字符串分配）。60fps × 3 源 ≈ 每秒数百次大对象克隆与上千次字符串分配。
- **触发条件**：rail 可见且处于快档（指针停在 rail 上、有动画、有源正在被烧）时。
- **影响**：GC gen0 压力与瞬时 CPU（项目本身就为此做了 `_textCache`/`BrushCache`/`PenCache`，唯独几何与颜色解析没做）。
- **改法**：① 按 `(baseKey, box)` 缓存"已带 `MatrixTransform` 且 `Freeze()` 的几何"，呼吸动画的缩放只影响 `box`，量化到 0.5pt 后组合数很少；② `TintFor` 的结果按 `(sourceKey, tintString, fraction 分档, spent)` 缓存，或干脆在 `ApplySettings`/`ApplyRenderSettings` 时把每个源的颜色算好存进字段（设置不变就不重算）；③ `RailGeometry.Arc` 的结果按 `(半径, 起始角, 扫过角)` 缓存（刷新扫光的角度是连续变化的，可量化到 1°）。

### P2-10　`FlyoutMenu._open` 静态字段在菜单关闭后仍引用已关闭的窗与整棵可视树
- **位置**：`Views\FlyoutMenu.cs:44`（`private static FlyoutMenuWindow? _open;`）、`:50-53`（下次 `Show` 才 `_open?.CloseSelf(); _open = null;`）、`:110`（`_open = window;`）、`:173-176`（菜单项点击路径自己把 `_open` 置空）、`:231-236`（`CloseSelf`）
- **问题**：菜单因失焦/Esc/点项关闭后，`_open` 仍指向那个已 `Close()` 的窗口对象——连同它的 `StackPanel`、每个 `Border` 的 hover `Style`、每条菜单项的 `Click` 闭包（闭包里可能捕获了 App 的 `this`、`UsageEngine`）。直到下一次弹菜单才被覆盖。对一个长期驻留的托盘程序，这是"每次弹菜单多留一份残骸直到下次弹菜单"的语义，最坏情况（弹出后再不弹）是永久多留一份。
- **改法**：在 `FlyoutMenuWindow` 的 `Closed` 里回调清空：`Closed += (_, _) => { if (ReferenceEquals(FlyoutMenu.OpenMenu, this)) FlyoutMenu.OpenMenu = null; _closed?.Invoke(); };`（把 `_open` 暴露成 `internal static` 属性）；或者 `Show` 里用 `WeakReference<FlyoutMenuWindow>`。

### P2-11　设置窗每秒无差别重写所有卡片的状态文本（且读的是 Services 侧无锁字典）
- **位置**：`Views\SettingsWindow.xaml.cs:53-55`（`_statusTimer` 1s）、`:302-307`（`RefreshStatus` 遍历 `_sourceCards` + 每帧读 `SnapshotSource.DataDirectory`）、`Views\SourceCard.cs:271-283`（`RefreshStatus` 连写 4 个 `TextBlock.Text`，OpenCode 还要再走一遍 `RefreshAccountStatus`）、`:401-406`（`RefreshAccountStatus` 对每个账号再查一次 `StatusForSource`）
- **问题**：状态 99% 的时间没变，但每秒仍产生十余次字符串拼接 + 十几次 `Text` 属性赋值（每次都触发 measure invalidation）。同时 `_engine.StatusForSource` 读的是 Services P1-1 里那个**无锁 `Dictionary`**，后台同步线程正在插键——这正是 Services P1-1 的数据竞争在 UI 侧的读端。
- **改法**：`SourceCard` 里缓存上次的 `(statusText, credentialSource, state)`，值没变就不赋值；`SettingsWindow` 把定时器降到 2s（状态本来按分钟级变化），或在 `SnapshotsChanged` 事件里刷新而不是轮询。配合 Services P1-1 改成 `ConcurrentDictionary` 后这条读路径才真正安全。

### P2-12　`ReloadLedger` 用 `ContinueWith + .Result` 回 UI，且先摸 UI 再判 `_closed`
- **位置**：`Views\SpendWindow.xaml.cs:439-462`，重点 `:441`（`_loading = false;`）、`:444`（`PriceSourceText.Text = "读取失败:…"`）、`:447`（`var ledger = task.Result;`）、`:450-453`（`if (_closed) { Release(); return; }`）
- **问题**：① `task.Result` 在 `ContinueWith(..., TaskScheduler.FromCurrentSynchronizationContext())` 里虽然不会阻塞（task 必然已完成），但这是"看起来会死锁"的写法，且 `Result` 会把异常包成 `AggregateException`；② 出错分支在 `:444` **先**写 `PriceSourceText`，之后才在 `:459` 判 `Dispatcher.HasShutdownStarted`——如果窗口已关、Dispatcher 已停，这个赋值发生在已关闭的窗口上（WPF 不抛，但语义已经不对）；③ 如果 `FromCurrentSynchronizationContext()` 拿到的调度器在排队时已关闭，`ContinueWith` 会抛 `TaskSchedulerException`，变成一条未观察异常（只有 `App.xaml.cs:39-43` 的 `UnobservedTaskException` 日志能看到）。
- **改法**：改成 `private async void ReloadLedger()`（或 `async Task` + 调用方 `_ =`），`var ledger = await SpendLedgerCache.AcquireAsync();`，用 `try/catch` 包住，开头判 `_closed || Dispatcher.HasShutdownStarted`。可读性和正确性同时改善，改动约 15 行。

### P2-13　诊断出图路径会改全局状态（`ShutdownMode`、静态 `Attach`、停表）
- **位置**：`Views\UiShot.cs:27`（`Application.Current.ShutdownMode = OnExplicitShutdown;`）、`Views\RailShot.cs:36-38`（`Diagnostics.Attach(paths); AppSettings.Attach(paths);`）、`Views\MainWindow.xaml.cs:283-298`（`InjectForShot`：`_ticker.Stop()`、`SuppressActivity = true`、重置 `_refreshUntil`）
- **问题**：这些是**进程级**副作用。当前它们只在 `--ui-shot` / `--rail-shot` / `--rail-real` 分支里跑（`App.xaml.cs:133-195`），进程随即退出，所以不算线上风险。但：① `InjectForShot` 会永久停掉主循环，一旦将来有人从正常路径调用它，rail 就静止了；② 若将来把 `--ui-shot` 改成"在已运行实例里截图"，`ShutdownMode` 与 `Attach` 会污染正在用的进程；③ `RailShot.RunReal` 的 `Attach` 若被调用两次（同一进程内第二次进入诊断），`AppSettings`/`Diagnostics` 的静态状态会被换掉。
- **改法**：① 给这三个入口加断言 `Debug.Assert(App.Current is App { IsHeadless: true })`，或把 `_headless` 提升成可读属性供其校验；② `InjectForShot` 里加注释"仅在进程只用于出图时调用"，并把"停表"改成"暂停 60s 后自恢复"（或提供 `ResumeAfterShot()`）；③ `UiShot.Run` 里保存/恢复 `ShutdownMode`（目前因为进程随即退出可以不改，但要写清前提）。

### P2-14　导出在 UI 线程同步写盘（大区间会僵住）
- **位置**：`Views\SpendWindow.xaml.cs:2460-2461`（JSON：`File.WriteAllText`）、`:2492`（CSV：`File.WriteAllText` 带 BOM）；数据准备在 `:2409`（`SpendSummary.Build`）已经做完了
- **问题**：CSV/JSON 的内容在内存里拼好后一次性同步写出。选「全部」区间、会话上万条时字符串可达数 MB，`File.WriteAllText` 期间 UI 线程完全阻塞；写到网络盘/U 盘更明显。`catch` 只覆盖到"导出失败"文案，用户会以为程序卡死。
- **改法**：`File.WriteAllTextAsync` + `await`（方法改 `async void` 并加 `try/catch`，与 P2-6 同款）；或在写之前先 `PriceSourceText.Text = "正在导出…"` 并 `await Dispatcher.Yield()` 让界面先刷一帧。

### P2-15　热图每次重渲染重建 420 个 `Rectangle` + 420 个 ToolTip，且每格挂一个闭包
- **位置**：`Views\SpendWindow.xaml.cs:1135-1161`（`for col in weekColumns` × `for row in 7`：每格 `new Rectangle`、设 `ToolTip` 字符串、`rect.MouseLeftButtonDown += (_, e) => …`）、`:1128`（`weekColumns = Math.Clamp(fitColumns, 1, 60)`）
- **问题**：最多 60×7=420 个 `Rectangle` 元素，每个带 ToolTip 字符串和 lambda；`RenderAll` 每次都 `HeatCanvas.Children.Clear()` 再全量重建。切一次区间就是 420 个元素 + 420 个字符串 + 420 个委托的分配与布局。
- **触发条件**：切区间 / 切 tab / 刷新（任何 `RenderAll`）。
- **影响**：一次渲染多出几百个可视元素与分配；对 132px 高的小图属于过重。不是泄漏（`Clear()` 后旧元素可回收），是纯粹的"用错工具"。
- **改法**：热图改成自绘（`Canvas` 换成自定义 `FrameworkElement` + `OnRender` 里 `DrawRoundedRectangle`），命中与 ToolTip 用 `OnMouseMove` 算格子索引（`col = x/step`），既快又天然支持 P2-3 的 resize。

### P2-16　`SessionWindow` / `RingOrderList` 的小健壮性问题
- **位置与问题**：
  1. `Views\SessionWindow.cs:32-35`（`ordered.First().SessionId`）与 `:48-51`（`ordered.First()` 取 `Agent`/`Project`/`Timestamp`）：`entries` 为空时抛 `InvalidOperationException`。当前唯一调用点 `Views\SpendWindow.xaml.cs:2516-2519` 已判 `entries.Count == 0` 返回，所以是"契约靠调用方守"。同一个文件 `:83` 的 `tokensAt / max * 74` 在 `max == 0` 时会得到 `NaN`（虽然 `tokensAt == 0` 时走了另一分支，但两处假设耦合）。
  2. `Views\SessionWindow.cs:18`（`BarBase = 180`）与 `:186-187`（`Height=4, Width=180` 的容器）是同一个 180 的两份拷贝，`fraction * BarBase` 一旦与容器宽不一致就会超出。
  3. `Views\RingOrderList.cs:209-214`：落点用 `(int)(y / rowH)` 估算，`rowH = row.ActualHeight`（不含 `:76` 给每行设的 `Margin 0,1,0,1`）。每行少 2diu，行数多时累计偏移，落点可能差一格。
- **改法**：1) `SessionWindow` 构造开头 `if (entries.Count == 0) { Content = new TextBlock { Text = "没有明细" }; return; }`；2) 条宽改 `Width = fraction * barContainerWidth` 或让容器 `Width` 绑定同一常量；3) `RingOrderList` 改成"用每行的 `TranslatePoint`/`TransformToAncestor` 求最近行中心"而不是除法估算。

### P2-17　"整个区间"的时段分布其实只覆盖近 35 天（Views 侧文案确认了 Services P2-7 的误导）
- **位置**：`Views\SpendWindow.xaml.cs:1925-1926`（`ch.RangeHourly` 求和、标题写"时段分布"）、`:1991-1995`（tooltip 明写 `(整个区间累计)`）、`:1966-1975`（峰谷占比/金额都以 `RangeHourly` 为分子分母）、`Views\SpendWindow.xaml:297`（`ChannelPeakHint` 只说峰时段价格，不说覆盖范围）
- **问题**：`ChannelUsage.RangeHourly` 只由小时聚合行喂（`Services\Spend\ChannelUsage.cs:194-200`），而 `agg_hour` 只保留 35 天（`Services\Spend\UsageRepository.cs:35`）。选「全部」时，上面的 token/金额是全史，下面的 24 小时分布只有近 35 天，而文案两处都写着"整个区间"。这正是 services-audit **P2-7** 留下的"待确认 2"，现在可以定论：**文案确实误导**。
- **改法**：`ChannelUsage` 加 `bool RangeHourlyPartial`（services-audit P2-7 的建议），Views 侧在 `ChannelPeakTitle` 后缀"（近 35 天）"并把 `:1993` 的 tooltip 改成"近 35 天累计"；或者干脆把 `agg_hour` 的保留期提到与 `agg_day` 一致（数据侧解决，Views 不动）。

### P2-18　`SpendWindow` 文件职责过大（2590 行、5 类职责），但**没有一个 >200 行的方法**
- **位置**：`Views\SpendWindow.xaml.cs` 全文件。方法体量实测：`DrawAreaChart`（`:138-309`，171 行）、`ExportCurrent`（`:2403-2501`，98 行）、`RenderChannelModels`（`:2089-2187`，98 行）、`Render`（`:861-937`，77 行）、`SelectChannel`（`:1268-1340`，72 行）、`RenderHeatmap`（`:1098-1189`，91 行）
- **问题**：单文件混合了 ① 曲线/热图绘制（`:93-346`、`:1027-1203`）② 数据加载与重算（`:414-505`、`:827-845`）③ 套餐页渲染（`:1268-2340`）④ 导出（`:2389-2506`）⑤ 出图钩子（`:2549-2568`）。改一处渲染要在一个 2590 行的文件里跳。
- **改法**（纯搬移，不改行为）：拆成 `Views\Spend\SpendChartPainter.cs`（`DrawAreaChart`/`SmoothSegments`/`AddXLabels`/`AreaFill`/`BandFill`/`HeatBrush`）、`Views\Spend\SpendExporter.cs`（`ExportCurrent`/`Csv`）、`Views\Spend\SpendShots.cs`（`UiShot*` 三个成员 + `UiShotInjectOfficialUsage`）。这样"注入代码只在诊断文件里"，也顺带解决线索 ④ 的可维护性问题。
- 顺带一个 XAML 小项：`Views\SettingsWindow.xaml:401` 的 `ManualCookieBox` 没套 `Style="{StaticResource LightTextBox}"`（同排其它输入框都套了，`ProxyBox:462`、`UsdRateBox:478`），外观与高度会与其他输入框不一致。

---

## 5. 与 Services 审计的交叉点（两边都涉及，需要一起改）

| # | Services 条目 | Views/Rendering 落点 | 需要一起改的原因与建议 |
|---|---|---|---|
| 1 | **P0-1** 调用次数双算（`SpendLedger.cs:520`） | `SpendWindow.xaml.cs:890`、`:903`（`{summary.Requests:N0} 次调用`）、`:2468`（CSV 的 `调用,{summary.Requests}`）、`:2434`（JSON 的 `requests`） | 修 Services 一处，Views 四个显示点自动变对。**不要**在 Views 侧做减法或补丁——那是掩盖。修完按 services-audit 的回归口径核对这四个点。 |
| 2 | **P1-7** 快照"读"与"原子替换"互踩 | 本报告 **P1-2**：`MainWindow.xaml.cs:441-445`（15s UI 线程读）+ `SpendWindow.xaml.cs:494`（每次渲染读）+ `SnapshotSource.cs:283` | 必须一起改：读侧统一改共享模式（`FileShare.ReadWrite \| FileShare.Delete`），写侧重试 + `finally` 清 `.tmp`。只改一边仍有窗口期。 |
| 3 | **P1-1** `_instanceStatus` 无锁 `Dictionary` | 本报告 **P2-11**：`SettingsWindow.xaml.cs:305`、`SourceCard.cs:405`（UI 每秒读） | Services 改成 `ConcurrentDictionary` 是必要的；同时 Views 侧应按 P2-11 把"每秒无差别重写文本"改成"变化才写"，把竞争窗口从每秒降到事件驱动。 |
| 4 | **P1-2** `AppSettings.OpenCodeAccounts` 并发枚举 | `SourceCard.cs:324`（`RebuildAccounts` 每次重建都 `foreach` 同一 `List`）、`SettingsWindow.xaml.cs:169`（`ReloadRingOrder` → `RingOrder.Load(AppSettings.Current.VisibleSources)` 同样枚举设置里的 `List`） | Services 建议的"快照 API"（锁内 `ToList()`）应同时供 Views 用：`RebuildAccounts` 改成遍历 `AppSettings.Current.AccountsSnapshot()`，与引擎用同一份快照，否则只是把竞争从引擎挪到界面。 |
| 5 | **P1-3** `OpenCodeUsageStatsClient.Cache` 无锁 | `SpendWindow.xaml.cs:410`（`Invalidate()`）、`:1754`（`TryFetchAsync` 读缓存）、`:1761`（`LastStatus`） | 「重新读取」按钮与官方读数异步加载都在这条静态缓存上；Services 加锁后 Views 不用改，但要注意 `Invalidate()` 与在途 `TryFetchAsync` 的先后（加锁后语义才确定）。 |
| 6 | **P2-6** `DshActivity` 每秒枚举 117 个投影文件 | `MainWindow.xaml.cs:484-489`（每秒 `_activity.Poll()` + `_dshActivity.Poll()`，都在 UI 线程） | 建议在 **Views 侧**节流最省事：把 `(int)now != _activityPollSecond` 改成分频（`_activityPollSecond % 2 == 0` 才轮询），或把 `Poll()` 的 117 次 `stat` 改成 2s 一次、心跳判定仍按秒。 |
| 7 | **P2-15** `Diagnostics.Note` 锁内全量落盘 | `MainWindow.xaml.cs:503`（心跳，每 60s 在 UI 线程）、`BrowserLoginWindow.xaml.cs:84/91/104/122/128`（登录窗，UI 线程）、`SettingsWindow`/`SpendWindow` 各 `catch` 里的 `Note` | 与 P1-1、P1-2 叠加时危害放大（UI 线程一次 `Note` = 一次同步写盘）。建议 Services 侧把 `Flush` 移出锁并加 200~500ms 合并写；Views 侧不需要改。 |
| 8 | **P2-14** `RequestRefreshNow` 的 `SemaphoreFullException` | `MainWindow.xaml.cs:697`（点环 → `RefreshRequested?.Invoke()`）→ `App.xaml.cs:236` → `UsageEngine.RequestRefreshNow` | 若 Services 不改，这个异常会从 `HandlePointer` 一路冒到 DispatcherTimer，被 `App.xaml.cs:51-75` 吞掉 + 弹一次框 + 当帧失效。**建议 Services 侧直接吞满异常**（services-audit 给的改法），Views 侧不必包 try。 |
| 9 | **P2-7** `RangeHourly` 只覆盖 35 天 | 本报告 **P2-17**：`SpendWindow.xaml.cs:1993` 的 `(整个区间累计)`、`:1925-1926` 与 `SpendWindow.xaml:297` | services-audit 的"待确认 2"现在有答案：文案确实把 35 天说成了"整个区间"。两边一起改（数据加 `RangeHourlyPartial` 或加保留期 + 文案改）。 |
| 10 | services-audit「待确认 1」：`QuotaWindow.Percent` 夹 0..100 vs `PoolData.Fraction` 夹 1.02，环到底怎么画 | `Rendering\CardRenderer.cs:235`（`Math.Clamp(pool.Fraction, 0, 1)`）、`MainWindow.xaml.cs:1222`（`Math.Clamp(pool.Fraction, 0, 1)`）、`:1234`（`spent ? 360 : frac * 360`）、`Models\Models.cs:15-20`（`fraction >= 1` → `Exhausted` 深红） | **可以给 Services 一个确定答案**：环的绘制处处先 `Clamp(0,1)`，`>=1` 一律画满圈并强制深红（`TintFor` 里 `if (spent) return automatic;`，自定义环色盖不住）。也就是说超 100% 不会溢出成"绕两圈"，而是"满圈 + 深红"。Services 侧只需保证 `Fraction` 在 1.0 附近的夹取不要来回改（`SnapshotSource.cs:45-46` 的 1.02 对绘制无影响）。 |
| 11 | **P1-5** DSH 解析丢行 / **P2-5** `FromUnixMs` 坏时间 | `SpendWindow.xaml.cs:2516-2518`（会话下钻只从 `LiveEntries` 取）、`:943-959`（最近会话列表） | 不冲突，但同源：DSH 少行 → 最近会话列表少行、下钻窗口里少了时间段。修 Services 后这两处自动恢复，Views 不需要改。 |
| 12 | **P2-10** 死代码（`UpdateChecker.Changed` 无订阅者） | `SettingsWindow.xaml.cs:522-530`（`RefreshUpdateHint` 只在切到「关于」页与点检查时刷新，从不订阅 `Changed`） | 把 `UpdateChecker.Changed` 接到 `RefreshUpdateHint` 就能在后台检查完成时自动更新「关于」页（当前用户停在关于页时看不到新版本，除非手动点检查）。两边各一行。 |

---

## 6. 已核对"不是问题"的项（避免后来人重复怀疑）

1. **`Slider` 的 `Minimum` 初始化陷阱**：`SettingsWindow.xaml.cs:16-23` 的 `_loading = true` 是**字段初始化器**，执行在构造函数体（含 `InitializeComponent()`）之前；`LoadSettings` 里所有程序化赋值也在 `_loading=true` 区间内（`:68-100`），最后 `:98` 才置 false。XAML 里 `Minimum=30/50/99/15` 的 coercion 触发的那次 `ValueChanged` 被正确挡住。**处理正确，不需要改**。
2. **`RowVm` 与 XAML 绑定**：`RowVm`（`SpendWindow.xaml.cs:2571-2589`）是 `init` 属性 + 每次整体替换 `ItemsSource`（`RenderList`，`:1020-1023`），**不需要** `INotifyPropertyChanged`；`RowTemplate`（`SpendWindow.xaml:83-114`）里绑定的 `Name/Tokens/TokensTip/Amount/AmountTip/Detail/BarWidth/BarBrush/RowCursor` 全部存在且类型匹配，`Cursor="{Binding RowCursor}"` 是 `Cursor` 类型、OneTime 绑定，正确。无绑定路径错误。
3. **`{StaticResource}` 键**：`Themes\Light.xaml` 里实际存在 `WindowBackBrush`、`TextBrush`、`TextSubBrush`、`CardBorderBrush`、`LightButton`、`PrimaryButton`、`LightTextBox`、`LightCheckBox`、`LightCombo`、`LightSlider`（`Light.xaml:8-20`、`:23`、`:57`、`:85`、`:121`、`:186`、`:263`），且 `App.xaml:7` 已合并该字典。`BrowserLoginWindow.xaml`、`SettingsWindow.xaml`、`TintPicker.xaml` 的静态资源引用都能解析，**不会**在解析期抛 `XamlParseException`。
4. **DPI / 坐标换算**：`app.manifest:9-10` 声明 `SystemAware`，与 `Rendering\Native.cs:207-208`（`VisualTreeHelper.GetDpi`）和 `Layout.cs:9-13`（pt→diu）的口径一致；`WM_NCHITTEST` 的 lParam 是**屏幕**坐标（`Native.cs:196-202` 正确减 `Left/Top`），`WM_RBUTTONUP` 的 lParam 是**客户区**坐标（`:228-237` 正确不减、只在弹菜单时加回）。两处方向都对，注释也解释过，**不是**陷阱。
5. **GDI / 句柄**：`Views\` 与 `Rendering\` 里没有 `CreateSolidBrush`/`CreatePen`/`DeleteObject`/`SelectObject` 之类调用，所有绘制走 WPF `DrawingContext`；`Rendering\Icons.cs:15-36` 的 PNG 流用 `using` 释放、解码后位图 `Freeze()` 缓存；`BrowserLoginWindow.OnClosed`（`:365-376`）停表 + `View.Dispose()` + 释放命名互斥量。**无句柄泄漏**。
6. **`WebView2` 环境生命周期**：`BrowserLoginWindow.cs:137-156` 用静态 `_sharedEnvironment` + `SemaphoreSlim` 单飞，避免"同进程第二次 `CreateAsync`"这个已知坑；进程级不释放是刻意的（`CoreWebView2Environment` 与浏览器进程同寿）。`NavigationCompleted`/`ProcessFailed` 未 `-=`（`:99-105`）不影响回收（`View.Dispose()` 后 `CoreWebView2` 一并释放）。仅建议在 `OnClosed` 里补 `NavigationCompleted -= OnNavigationCompleted` 作为对称写法。
7. **`DrawingContext`/`RenderTargetBitmap` 释放**：`CardShot.cs:142-144`、`UiShot.cs:270-278`、`RailShot.cs:154-160` 的 `DrawingVisual` 都用 `using (dc = …RenderOpen())`；`RenderTargetBitmap`/`PngBitmapEncoder` 交给 GC（`Freezable` 无 `IDisposable`），不是泄漏。
8. **`TaskScheduler.UnobservedTaskException`**：`App.xaml.cs:39-43` 已有兜底（记诊断 + `SetObserved`），所以 `_ = LoadAccountModelsAsync(...)`（`SpendWindow.xaml.cs:1513`、`:1655`）即使抛异常也不会让进程死掉——只会"静默不更新"，属 P2（见 P2-6 的同类问题）。

---

## 7. 未覆盖 / 待确认（明确声明，不猜）

- **未做体系审计**：`App.xaml.cs`（1290 行）、`Models\`、`Themes\Light.xaml`（只核对了资源键与 Slider 相关模板）、`Services\`（引用 `services-audit.md`）。本报告只在需要确认调用链时读了这些文件的片段，行号都标了出处。
- **待确认 1（P2-2）**：`_cardFor` 越界在正常交互下是否可达——需要"卡片可见 + 源集合变短"同时成立。按当前指针逻辑很难，但代价两行，建议直接加防护。
- **待确认 2（P2-8）**：`pixelsPerDip=96` 与实时 DPI 的行高差是否真会导致 1px 裁切，需要按 200% DPI 跑一次 `--card-shot` 并与 `--ui-shot` 交叉比对。
- **待确认 3（P1-2 的视觉后果）**：本机没有可用的"引擎落盘 ↔ 15s 轮询"对齐日志，无法证明"rail 偶发少一个环"在真实使用中出现过。结论是从代码路径必然性推出来的（`FileShare.Read` vs `MoveFileEx` 的冲突是确定的，见 services-audit P1-7），但症状出现的频次未实测。
- **未验证**：`FlyoutMenuWindow.Activate()` 在"从托盘弹出"（前台是别的程序）时的实际焦点行为。代码注释说"激活自己、失焦即关"与系统菜单一致，但没有真机验证是否会抢走用户当前窗口的焦点；如果会，属于体验问题（P2 级）。这需要真机交互，静态审计无法定论。
- **未量化**：P1-4 的内存增长速度（取决于读数文本的取值空间与运行时长），只给了量级判断；建议在真实长跑进程上用任务管理器观察 `FormattedText` 数量（或临时打印 `_textCache.Count` 到诊断日志）后再定要不要做更彻底的改法。

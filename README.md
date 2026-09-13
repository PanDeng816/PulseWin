# Pulse for Windows

一个 Windows 11 屏幕边缘的 AI 额度浮窗:黑色玻璃 rail 上,每个订阅一个
**复合同心环**(内细圈 = 5 小时用量,外粗圈 = 月总额度),用量 ≤80% 绿色、
>80% 红色报警;hover 弹出三池明细卡。数据源:Command Code GOAT、OpenCode Go
与 DeepSeek(余额),自动 60 秒同步,不依赖任何第三方服务。

展示层还原自 [qunqin24/Pulse](https://github.com/qunqin24/Pulse)(macOS),
数据层移植自
[ahuud251/goat-go-usage-monitor](https://github.com/ahuud251/goat-go-usage-monitor)。
见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 预览

贴边 rail 上的复合同心环:内细圈 = 5 小时用量,外粗圈 = 月总额度;
阈值内绿色,超过报警线转红(下例中 GOAT 56% 绿、OpenCode Go 99% 红)。
鼠标悬停弹出该订阅的明细卡——三个时间池各自的百分比条、已用金额与重置倒计时:

| Command Code GOAT 明细卡 | OpenCode Go 明细卡 |
|---|---|
| ![GOAT 明细卡:5小时/本周/总额度三池与右侧复合环](docs/screenshot-goat-card.png) | ![OpenCode Go 明细卡:5小时/本周/本月三池,99% 转红报警](docs/screenshot-opencode-card.png) |

托盘右键菜单:显示/隐藏浮窗、立即刷新、设置、开机自动启动、退出:

<p align="center"><img src="docs/screenshot-tray-menu.png" width="200" alt="托盘右键菜单"></p>

## 下载

**免安装**:到 [Releases 页面](https://github.com/PanDeng816/PulseWin/releases/latest)
下载 `PulseWin.exe`,双击即用(自包含单文件,无需安装 .NET)。
数据保存在 exe 同目录的 `Data\` 下,卸载 = 删除整个文件夹。

## 功能

- 屏幕右/左/上边缘停靠或自由浮动;鼠标探到边缘滑出,移开 0.9s 自动隐藏
- 每订阅一个复合环:内圈 = 5小时用量,外圈 = 月总额度(周额度在 hover 卡内)
- DeepSeek 是**余额型**数据源,只占一圈:要么按所选基准画百分比,要么直接显示
  余额金额(详见下文)
- **环尺寸固定、显示哪些源可选**:设置里勾选要显示哪几个;关掉的既不显示也不占
  高度,多一个源 rail 就长一截,环本身不缩
- **读数可隐藏**:关掉后 rail 是一条纯环列,环间距与整体高度同步收窄
- 报警线(默认 80%,可调):阈值内绿、超过转红、封顶深红
- hover 卡片:三池各自的百分比条、已用金额、重置时间;数据超过 3 分钟没更新时
  会标出"⚠ 数据 X 分钟前 · 未能刷新"
- 点击环 = 立即刷新(真的会打一次接口,弧变暗 + 亮段游走直到数据回来)
- 托盘:双击显示/隐藏、右键菜单(立即刷新、设置、开机自动启动、退出)
- 只允许运行一个实例:重复启动会唤醒已有窗口,不会起第二个浮窗

## 额度口径(GOAT)

Command Code 的服务端只发**剩余额度**、不发上限,而且 GOAT 的额度单位是
**credits(用量价值单位)而不是美元**:满额模型(allowance $70)1 credit = $1 用量,
allowance $60 的 DeepSeek 每 $1 用量要扣 70/60 个 credit。所以

```
月上限 = 70 credits(5小时 cap 14、周 cap 35 正是它的 20%/50%,同一刻度)
已用   = 70 − 服务端 remaining
美元   = 已用 credits × (模型 allowance / 70)     # DeepSeek 即 × 60/70
```

`ModelMonthlyAllowance` 常量在 `Services/CommandCodeApiClient.cs`,换模型要同步改
(GLM-5.3 Flash $40、MiniMax M3 $47、Sol/Hy3/GLM-5.2 满额 $70)。

**高峰/低谷已自动计入**:服务端按请求发生时刻的实际单价扣 credit(高峰价是错峰的
2 倍),所以高峰时段额度掉得更快——百分比本身就是"实际花费"的口径,工具无需额外换算。

## 余额口径(DeepSeek)

DeepSeek 和另外两个源的根本差别:**它卖的是预付余额,不是套餐**。接口只有一条,
而且是官方文档里的:

```
GET https://api.deepseek.com/user/balance
→ {"is_available": true, "balance_infos": [{"currency":"CNY","total_balance":"110.00",
   "granted_balance":"10.00","topped_up_balance":"100.00"}]}
```

回包里**没有额度、没有窗口、没有重置时间**,API 里也没有消费历史。所以"环"要画
百分比就缺一个分母,本程序给三个来源,在设置里选:

| 基准 | 分母 | 环上显示 |
|---|---|---|
| **自上次充值**(默认) | 本程序观察到的历史最高余额 | `(峰值 − 余额) / 峰值` → 百分比 |
| 只看余额 | 没有 | 不画百分比,直接显示 `¥42.3` |
| 我的预算 | 你填的"满箱"金额 | `(预算 − 余额) / 预算` → 百分比 |

默认用"自上次充值",因为它的分母是**观察出来的**而不是猜的:余额上涨只可能是充值,
所以一涨就把峰值重置回满。代价是首次运行没有历史峰值,环从 0% 开始,直到真的花了
钱——那是关于"本程序看到了什么"的真实陈述。峰值按**币种**各存一份
(`Data\deepseek-baseline.json`),因为一个账户可以同时持有 CNY 和 USD,两者不能相加
也不能跨币种比大小。

另外两条口径:环上的金额用**截断**而非四舍五入的短写法(显示得比实际多是错错了
方向),精确值在 hover 卡里;只有 DeepSeek 自己的 `is_available` 能表示"账户花光
了",自设预算走到 100% 不算——那是你画的线,不是 DeepSeek 的判定。

### 卡片里的"今日消耗"柱状图

DeepSeek 的 API **不提供任何用量或消费历史**,所以拿不到真实的 token 数。卡片上
那张柱状图是本程序**自己观察**出来的:每次同步成功记下余额(每小时留一个点),相邻
两个小时的差额就是那一小时的消耗。

也就是说它衡量的是"余额下降",不是账单。**程序没运行的那些小时没有采样,柱子上是
空的**——宁可缺一根柱子,也不能把"没看见"画成"没花钱"。数据按币种分开存
(`Data\deepseek-ledger.json`,保留最近 7 天),余额上涨(充值)的那一小时记 0。

卡片脚注同时给出 `topped_up_balance` / `granted_balance` 的拆分(这是 DeepSeek 回包
直接提供的),所以能一眼看出余额里有多少是自己充的。

## 运行

需要 Windows 10/11。两种方式:

```powershell
# 开发运行(需 .NET 8 SDK)
dotnet run

# 单文件成品(自包含,免安装)
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
.\publish\PulseWin.exe
```

`EnableCompressionInSingleFile` 把自包含产物从 ~154MB 压到 ~70MB,代价是首次
启动多花一点解压时间。

## 设置与数据

托盘 → **设置…**:
- 显示两个数据源的连接状态与凭据来源
- 可手动输入 API Key(先联网验证,通过后 DPAPI 加密保存并立即同步)
- 调整显示与刷新偏好,即时生效并写入 `Data\settings.json`:
  - **报警阈值**(50%~99%,默认 80%)
  - **背景不透明度**(30%~100%,默认 80%)
  - **同步间隔**(15~300 秒,默认 60 秒)

凭据自动发现顺序(与 GOAT-Go-Usage-Monitor 一致):
- Command Code GOAT:环境变量 `COMMAND_CODE_API_KEY` → `~\.commandcode\auth.json` → DPAPI 手动存储
- OpenCode Go:环境变量 `OPENCODE_GO_API_KEY` → `~\.local\share\opencode\auth.json` 中 opencode-go 登录态 → DPAPI 手动存储
- DeepSeek:环境变量 `DEEPSEEK_API_KEY` → DPAPI 手动存储。它没有可借用的 CLI 登录态
  (Key 只存在于 DeepSeek 控制台),所以没配 Key 时这个环不显示,也不会拖慢另外两个源
  的刷新节奏

## 隐私

- **无硬编码密钥、无遥测、无第三方服务器**:请求只发给 Command Code / OpenCode 官方接口
- 所有数据(用量快照、加密凭据、设置)存在 **exe 同目录 `Data\`**(不可写时回退
  `%LOCALAPPDATA%\PulseWin`),首次启动会自动迁移旧
  GOAT-Go-Usage-Monitor 目录的数据
- API Key 用 Windows DPAPI 按当前用户加密(`credential.bin`),只有本机本用户可解密
- 接口异常会记到 `Data\diagnostics.json`(最近 100 条),不含密钥
- 上传/分发源码时请勿包含 `bin/`、`obj/`、`publish/`、`Data/`(见 `.gitignore`)

## 免责声明

非官方社区工具,与 Command Code、OpenCode 及其运营方无隶属或背书关系。

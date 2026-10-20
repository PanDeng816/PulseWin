# Token Monitor 视觉与内容参考（源码提取，2026-10-13）

来源：[Javis603/token-monitor](https://github.com/Javis603/token-monitor) v0.69.0 Windows 便携版，**用户试用当天即卸载**（判断"不如自研的 PulseWin 好用"），但要求留下其美术设计与内容结构作参照。提取方式：解包 Electron `app.asar`（`src/electron/renderer/` 下的 `styles.css`、`edgeDock/dock.css`、`themePresets.js`）——数值均为源文件原值，非目测。

对照基准：本目录 `上游Pulse-视觉规格-20260925.md`（qunqin24/Pulse）。三者关系：Pulse 是 macOS 上游、Token Monitor 是跨平台成品、PulseWin 是本机自研还原。

---

## 一、颜色与主题系统（`themePresets.js` + `styles.css` 的 `:root`）

**可主题化的 5 个键**（这是它设计上最值得抄的一条）：`accent` / `chart`（图表色）/ `bg`（玻璃底色）/ `text` / `muted`。每个键都直接映射到一个 CSS 变量（`--accent`、`--chart-color`、`--glass-rgb`、`--text`、`--muted`），换主题 = 换这 5 个值，其余全部派生。

默认调色板（石墨档）：

| 名称 | 值 | 说明 |
|---|---|---|
| `--text` | `#eef5fb` | 主文字（近白，略偏蓝） |
| `--muted` | `#a3adbb` | 次要文字 |
| `--accent` | `#b7ead4` | **薄荷绿**，互动强调色（`--accent-rgb: 183,234,212` 另存一份 RGB 供描边/辉光/激活态用） |
| `--success` | `#b7ead4` | 语义绿，**刻意与自定义 accent 解耦**（换主题不跟着变） |
| `--number` | `#f3fbf7` | 大号 TOTAL 数字专用色，随 `--text` 走 |
| `--blue` / `--chart-color` | `#73bdf5` | 信息蓝 / 图表默认色，与 accent 解耦 |
| `--glass-rgb` | `48, 52, 56`（`#303438`） | 玻璃底色 tint |
| `--panel-rgb` | `16, 21, 30` | 下沉面板/提示卡 |
| `--sunken-rgb` | `4, 8, 13` | 更深的轨道/输入框底 |
| `--orange` / `--purple` / `--yellow` / `--red` | `#f4a073` `#b394f4` `#f1d973` `#f47788` | 状态色，全部独立于主题 |

**三套内置一键主题**（整组换色，不是只换 accent；注释明说"全部保持深色，真正的亮色模式是另一件更大的工作"）：

- `default` — 石墨（上表，也是重置点）
- `obsidian` — 近黑：bg `#0b0c0e`、text `#eceef2`、accent `#e6e8ec`、muted `#8f949c`
- `porcelain` — 亮色：bg `#f6f7f9`、text `#1c1f26`、accent `#2563eb`、muted `#5b626d`

**亮色模式的做法（关键技巧）**：亮主题时不是"把深色变量改浅"，而是**整套 overlay/line/panel/sunken 的 RGB 一起翻转**——`--overlay-rgb` 从 `255,255,255` 翻成 `15,18,24`、`--line-rgb` 翻成 `24,28,36`、`--panel-rgb` 翻成 `255,255,255`（白卡）、`--sunken-rgb` 翻成 `188,196,206`、`--success` 翻成更深的 `#18794e`。这样面板/描边在浅底上仍然是"深色压浅色"，可读性不塌。翻转由 JS 的 `themeCssVarEntries()` 执行。

**主题代码分享**：把主题序列化成一段带版本号的代码串（`THEME_CODE_VERSION = 'TM2'`，键序 `accent/bg/text/muted/chart`，旧版 `TM1` 只认前 4 个），支持"复制主题代码 / 粘贴并应用"，另可"重置回品牌色"。**厂商色与外观偏好不参与分享**，只分享界面配色。

**厂商色（vendor colour）**：为每个 AI 厂商存品牌色，用于列表行/图标染色；另有"默认"兜底色；面板提供"重置回品牌色"。

## 二、材质与玻璃（`styles.css`）

- 底：`--glass: rgba(var(--glass-rgb), 0.68)`，配 `blur(32px) saturate(115%)`。
- 分层透明度：`--line-alpha: 0.138`、`--line-strong-alpha: 0.238`、`--control-alpha: 0.049`（控件底），文字以外的东西全是"同一个 RGB 调不同 alpha"。
- 面板另有 `--glass-strong: rgba(31,35,41,0.56)`、`--glass-soft: rgba(255,255,255,0.05)`。
- 局部再降模糊：卡片 `blur(28px) saturate(115%)`、次级 `blur(14~18px)`、小元素 `blur(10px) saturate(120%)`。
- 阴影只有一档：`--shadow: rgba(0,0,0,0.36)`。

> ⚠️ **不要照搬到 PulseWin 的悬浮层**：本机 v1.7.3 已实测「`backdrop-filter`/SetWindowCompositionAttribute 类真玻璃会不可逆打坏分层窗口的 per-pixel alpha，且模糊矩形跟不上圆弧」，液态玻璃已整体删除。Token Monitor 能用玻璃是因为它的窗口是**矩形面板**。若要借鉴，只用在**设置窗/统计窗这类矩形窗口**上。

## 三、排版

- 字体栈：正文 `var(--ui-font, ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace)`——**正文默认就是等宽字体**；标题另用 `var(--display-font, -apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", sans-serif)`。
- **全局 `font-variant-numeric: tabular-nums`**（数字等宽对齐，额度/金额跳动不抖）。
- **字号整体很小**（统计 top 值）：`0.625rem`(10px) 出现 90 次、`0.6875rem`(11px) 72 次、`0.5625rem`(9px) 49 次、`0.75rem`(12px) 19 次；根字号 `calc(16px * var(--ui-text-scale))`，即**有整体文字缩放开关**。
- Dock 内：数值 11~12px、统计标签 9px / 字重 600 / `letter-spacing: 0.02em` / 省略号截断、标签最大宽 54px。

## 四、圆角与描边

- 圆角阶梯：**6px 为主力**（43 次），其余 3/4/5/7/8/10/14px，`999px` 用于胶囊，`50%` 用于圆点。
- 悬浮球单独一个变量：`--floating-bubble-radius: 17px`。
- 描边：主力 `1px solid var(--line)`（25 次），少数用 `rgba(var(--line-rgb), 0.13~0.22)` 的中间档。

## 五、动效

- 时长集中在 **120ms / 140ms / 160ms / 180ms / 200ms**，缓动多为 `ease`；少数用 `cubic-bezier(0.25, 1, 0.5, 1)`（平滑收尾）。
- **环进度专用曲线**：`stroke-dashoffset 420ms cubic-bezier(0.2, 0.8, 0.2, 1)`——比通用过渡慢得多，读数变化看起来是"长过去"的。
- 全局**减少动效**开关：`data-reduce-motion` 三态（开/关/跟随系统），开启时把过渡压到 `0.01ms`、停掉所有装饰动画。

## 六、Edge Dock（贴边栏）—— 与 PulseWin rail 最直接的对照（`edgeDock/dock.css`）

结构与状态机（这套命名本身就是一份可抄的规格）：

- 根：`.edge-dock-root[data-side="left|right"][data-surface="dock|peek"][data-peek-mode="handle"]`，有 `.is-handle-hidden`、`.is-dragging`、`.is-compact` 变体。
- 收起态叫 **peek/handle**：只留一个把手，`--edge-dock-handle-retreat: 100%` 控制退出距离（右侧 `100%`、左侧 `-100%`）。
- 每个源 = 一个 `.edge-dock-cell`，含 `.edge-dock-ring`（**42×42**，compact 34×34）+ `.edge-dock-value` 数值 + `.edge-dock-mark` 图标（compact 14×14）。
- 环：`stroke-width: 4`、`stroke-linecap: round`、`fill: none`，颜色取 `var(--ring-color, var(--accent))`。
- **状态用 data 属性表达**，渲染层只管动画：`data-status="error"`（弧降到 `opacity: .35`）、`data-running="yes"`、`data-waiting="yes"`、`data-severity="low|critical|unknown"`。
- **"正在跑"有独立内圈**：`.edge-dock-ring-spinner` 是**与额度弧无关的另一条弧**，只做 transform 旋转；等用户输入的 `.edge-dock-ring-waiting`、完成态 `.edge-dock-ring-complete` 各有一条。→ 即"额度环 + 运行态环"两层，而不是把额度环变色来表示运行。
- 焦点态：`drop-shadow(0 0 3px color-mix(in srgb, var(--ring-color,var(--accent)) 55%, transparent))`。
- 数值分档配色：`low → var(--orange)`、`critical → var(--red)`、`unknown → var(--muted)`。
- 详情卡 `.edge-dock-card`：**宽 280px**、padding `12px 14px 10px`、gap 10、字号 12。
- 刷新按钮有 `data-state="busy|success|error"` 三态（转圈/成功/失败反馈）。

## 七、内容/功能清单（值得对照的能力面）

按"PulseWin 有没有"标注（依据本机记忆与 PulseWin 源码目录）：

| Token Monitor 的功能 | PulseWin 现状 | 可借鉴度 |
|---|---|---|
| **订阅记账**：记录每个 AI 账号"实际付了多少钱"（充值/订阅二选一、套餐名、金额、币种、周期、首次扣款日、下次扣款日、自动续费），悬停方案标签即可看到 | 无（PulseWin 只有用量与额度） | **高**——能把"花了多少"和"用了多少"并排，成本感更强 |
| **自定义模型单价覆盖**（USD / 1M tokens，按模型，作用于所有 provider） | 无 | 高——官方价与网关价不一致时用得着 |
| **模型别名合并**（"移除前缀"把 `anthropic/claude-opus-5` → `claude-opus-5`；"合并重复"把同一模型两种写法并一行） | 无（PulseWin 按套餐/模型聚合时也会遇到同名不同写法） | 高——成本极低、直接治"同一模型裂成两行" |
| **运行态与额度态分离**（独立 spinner 内弧 / waiting / complete，三套状态） | v1.16.x 已有"按通道判定正在被烧"的心跳/呼吸 | 中——思路同源，可看它的 data 属性命名法 |
| **订阅信息标签悬停显示**（额度页悬停账号方案标签即看记账内容） | 无 | 中 |
| **数据导出**：CSV + JSON，可手动一次或**自动导出到指定文件夹**（给 Excel/Obsidian/自写脚本） | 有 CSV/JSON 导出（v1.12.0） | 中——"自动定时导出到文件夹"这一条 PulseWin 没有 |
| **保留已删除会话的用量**（源工具清掉会话后，仍留会话总量与每日活动） | 无 | 中 |
| **采集频率**（实时追踪 / 自适应 / 定时）与**"追踪 vs 显示"分离**（追踪控制采集、显示只影响主列表，且列表可拖动排序） | 有刷新节拍（60fps/10fps 两档） | 中——"追踪"与"显示"分离的模型值得参考 |
| **托盘文字可选**（今日 Tokens / 金额 / 最接近用尽的额度%）、**托盘/菜单栏图标开关** | 有托盘与阈值告警 | 中——"托盘显示最接近用尽的百分比"是很实用的一条 |
| **悬浮球**（可拖动，只显示图标/文字；悬停预览或点击展开；可收成"悬浮小窗"窄条） | 有 rail 与贴边条 | 中——`--floating-bubble-radius: 17px` 与"展开方式：悬停/点击"可选 |
| **多设备同步**：macOS 走 iCloud 云盘，跨平台走自建 **Hub**（显示 URL + 共享密钥 + 端口，跨网络建议 Tailscale/ZeroTier） | 无 | 低（本机单机场景） |
| **背景图片** + 缩放/深度/玻璃三滑杆 + "Windows 玻璃样式（Acrylic）" | v1.7.3 已删玻璃 | 低——玻璃在 PulseWin 上已被证伪；背景图对悬浮层无意义 |
| **主题代码分享**（复制/粘贴一段主题码） | 有自定义环色/主题 | **高**——把配色序列化成一段可分享的短码，成本低、体验好 |
| **界面文字缩放**（`--ui-text-scale` 整体缩放） | 无 | 中 |
| **减少动效**三态开关 | 无 | 中——无障碍项，实现便宜 |
| 账号 Email 遮罩、"显示来源"开关 | 无 | 低 |

## 八、给 PulseWin 的落地建议（按性价比排序）

1. **抄"5 键换肤"模型**：把主题收敛成 `accent / chart / bg / text / muted` 五个值 + 自动派生其余变量，并配 3 套内置档（石墨 / 近黑 / 亮）。PulseWin 现在环色、卡片、统计窗各自持色，改成"一套 5 键驱动全部"能显著减少配色不一致。
2. **抄主题代码分享**（可分享码 + 粘贴应用 + 重置回品牌色），版本号前缀防格式漂移。
3. **抄"运行态独立弧"的表达**：额度弧与"正在跑"的弧分开，运行态不要靠给额度弧变色。
4. **加订阅记账**：账号 → 实际付费（金额/币种/周期/首次扣款日），悬停查看；与额度并排能直接看出"这钱花得值不值"。
5. **加模型别名合并 + 自定义模型单价覆盖**：都是小改动，直接治"同名不同写法裂行"和"网关价与官方价不一致"。
6. **统一动效词汇**：120/140/160/180/200ms 五档 + `tabular-nums` + 环专用 420ms 曲线；补 `data-reduce-motion` 三态。
7. **亮色模式若要做**，走它的"整套 overlay/line/panel/sunken 一起翻转"路线，而不是逐个改色。
8. **不要借鉴**：真玻璃（已被本机证伪，且模糊跟不上圆弧）、背景图+玻璃滑杆、多设备 Hub（本机单机用不上）。

## 九、可直接复用的数值速查

- 环：42px（紧凑 34px），描边 4px，圆帽，无填充；焦点辉光 `drop-shadow 0 0 3px @55%`。
- 详情卡：宽 280（上游 Pulse 是 250），padding 12/14/10，gap 10，正文 12px。
- 圆角：主力 6px，胶囊 999px，悬浮球 17px。
- 字号：正文等宽 10/11/12px；标签 9px/600 字重/0.02em 字距；大数字 tabular-nums。
- 玻璃：`rgba(bg, 0.68)` + `blur(32px) saturate(115%)`；线 `alpha 0.138` / 强线 `0.238` / 控件底 `0.049`。
- 阴影：只有一档 `rgba(0,0,0,0.36)`。
- 动效：120/140/160/180/200ms；环 420ms `cubic-bezier(0.2,0.8,0.2,1)`。

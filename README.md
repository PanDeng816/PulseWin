# Pulse for Windows

一个 Windows 11 屏幕边缘的 AI 额度浮窗:黑色玻璃 rail 上,每个订阅一个
**复合同心环**(内细圈 = 5 小时用量,外粗圈 = 月总额度),用量 ≤80% 绿色、
>80% 红色报警;hover 弹出三池明细卡。数据源:Command Code GOAT 与
OpenCode Go,自动 60 秒同步,不依赖任何第三方服务。

展示层还原自 [qunqin24/Pulse](https://github.com/qunqin24/Pulse)(macOS),
数据层移植自
[ahuud251/goat-go-usage-monitor](https://github.com/ahuud251/goat-go-usage-monitor)。
见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 下载

**免安装**:到 [Releases 页面](https://github.com/PanDeng816/PulseWin/releases/latest)
下载 `PulseWin.exe`,双击即用(自包含单文件,无需安装 .NET)。
数据保存在 exe 同目录的 `Data\` 下,卸载 = 删除整个文件夹。

## 功能

- 屏幕右/左/上边缘停靠或自由浮动;鼠标探到边缘滑出,移开 0.9s 自动隐藏
- 每订阅一个复合环:内圈 = 5小时用量,外圈 = 月总额度(周额度在 hover 卡内)
- 报警线(默认 80%,可调):阈值内绿、超过转红、封顶深红
- hover 卡片:三池各自的百分比条、已用金额、重置时间;数据超过 3 分钟没更新时
  会标出"⚠ 数据 X 分钟前 · 未能刷新"
- 点击环 = 立即刷新(真的会打一次接口,弧变暗 + 亮段游走直到数据回来)
- 托盘:双击显示/隐藏、右键菜单(立即刷新、设置、开机自动启动、退出)
- 只允许运行一个实例:重复启动会唤醒已有窗口,不会起第二个浮窗

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

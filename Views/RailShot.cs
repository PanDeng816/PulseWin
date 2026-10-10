using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseWin;

/// <summary>
/// rail 主界面的离屏出图(<c>--rail-shot</c>)。用合成数据渲染"空闲源只画外圈、
/// 正在使用的源才加内圈"的实际效果——环是自绘的,除了看图没有别的核对办法,
/// 而靠真人 hover/等数据又慢又不可复现。
///
/// 产物在 <c>Data\rail-shot\</c>。
/// </summary>
internal static class RailShot
{
    public static void Run()
    {
        string dir = Path.Combine(SnapshotSource.DataDirectory, "rail-shot");
        Directory.CreateDirectory(dir);

        try
        {
            Render(Case(), Path.Combine(dir, "rail.png"));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("rail 出图失败", ex);
        }
    }

    /// <summary>真实快照出图(<c>--rail-real</c>):不注入,走 LoadAll 的真实加载路径。
    /// 正常启动时设置与诊断由 UsageEngine 构造时挂接;这条诊断不构造引擎,要自己挂。</summary>
    public static void RunReal()
    {
        var paths = new AppDataPaths();
        Diagnostics.Attach(paths);
        AppSettings.Attach(paths);

        string dir = Path.Combine(SnapshotSource.DataDirectory, "rail-shot");
        Directory.CreateDirectory(dir);

        try
        {
            Render(null, Path.Combine(dir, "rail-real.png"));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("rail 真实数据出图失败", ex);
        }
    }

    /// <summary>
    /// 三种源各摆一个,把关键情形都覆盖到:
    ///  · GOAT:模拟"正在被烧"(内圈出现;5 小时用掉 32%)+ 总额度 86%(红)
    ///  · OpenCode Go:5 小时窗口是 0 且不活跃(只画外圈,这是默认该有的整洁样子)
    ///  · DeepSeek:余额型,本来就只有一圈
    /// </summary>
    private static List<SubData> Case() =>
    [
        new SubData
        {
            Key = "goat", Name = "GOAT", AccountLabel = "PanDeng816",
            PeriodTokens = 3_594_700_000,
            Pools =
            {
                new PoolData { Label = "5小时", PoolKind = "FiveHour", Unit = "cr",
                    Used = 4.5, Cap = 14, IsAvailable = true,
                    ResetAt = DateTimeOffset.UtcNow.AddHours(3) },
                new PoolData { Label = "本周", PoolKind = "Weekly", Unit = "cr",
                    Used = 20, Cap = 35, IsAvailable = true,
                    ResetAt = DateTimeOffset.UtcNow.AddDays(2) },
                new PoolData { Label = "总额度", PoolKind = "Monthly", Unit = "cr",
                    Used = 60.3, Cap = 70, IsAvailable = true,
                    ResetAt = DateTimeOffset.UtcNow.AddDays(3) },
            },
        },
        new SubData
        {
            // 5 小时窗口 = 0:内圈**不该出现**,只画总额度外环。
            // 这条源的池用**网页口径**(Unit=%,并带 meters 的微美分金额)——卡片行3
            // 因此走"$6.38 / $60"那条分支,出图能一并核对它。
            // PeriodTokens 也照真实情形给上(官方 30 天窗口):卡片头部于是走
            // "5.7亿 · 本月已用 Token"那条分支,与 GOAT 卡片同一读法。
            Key = "opencode", Name = "GO", AccountLabel = "PanDeng",
            PeriodTokens = 570_000_000,
            Pools =
            {
                new PoolData { Label = "5小时", PoolKind = "FiveHour", Unit = "%",
                    Used = 0, Cap = 100, IsAvailable = true,
                    ResetAt = DateTimeOffset.UtcNow.AddHours(5) },
                new PoolData { Label = "本周", PoolKind = "Weekly", Unit = "%",
                    Used = 8.5, Cap = 100, IsAvailable = true,
                    ReadingFrom = "网页控制台", UsedAmount = 2.55, LimitAmount = 30,
                    ResetAt = DateTimeOffset.UtcNow.AddDays(3) },
                new PoolData { Label = "本月", PoolKind = "Monthly", Unit = "%",
                    Used = 10.63, Cap = 100, IsAvailable = true,
                    ReadingFrom = "网页控制台", UsedAmount = 6.38, LimitAmount = 60,
                    ResetAt = DateTimeOffset.UtcNow.AddDays(19) },
            },
        },
        new SubData
        {
            // 附加账号(go2):未订阅 = 只有月池、无读数 —— 一个灰环占位,
            // "账号存在,只是还没额度"。
            Key = "opencode:2", Name = "GO·go2", AccountLabel = "go2",
            Pools =
            {
                new PoolData { Label = "总额度", PoolKind = "Monthly", Unit = "%",
                    IsAvailable = false, Note = "未订阅或无权读取;开通订阅后自动开始显示" },
            },
        },
        new SubData
        {
            Key = "deepseek", Name = "DeepSeek", AccountLabel = "PanDeng",
            Pools =
            {
                new PoolData { Label = "余额", PoolKind = "Balance", Unit = "$",
                    IsAvailable = true, Amount = 12.34, Used = 7.66, Cap = 20.0,
                    EstimateFrom = "自上次充值" },
            },
        },
    ];

    /// <param name="subs">合成数据;null = 不注入,用 <see cref="SnapshotSource.LoadAll"/> 的真实数据。</param>
    private static void Render(List<SubData>? subs, string path)
    {
        var window = new MainWindow();
        window.Show();
        if (subs is not null)
        {
            // **必须在 Show 之后注入**:OnSourceInitialized(Show 触发)里会 ReloadData(),
            // 从快照加载真实数据,把先前注入的合成数据覆盖掉(第一版就栽在这:
            // 出图只剩一个源,因为那个目录下没有快照、退回了占位数据)。
            window.InjectForShot(subs);
            // 内圈的判定是"正在被烧"而不是"窗口有用量"(v1.16.1 起),出图要展示
            // GOAT 双环就得明确告诉它 GOAT 正在被烧;改动绘制后强制重画一帧。
            window.ForceBurning.Add("goat");
            window.InvalidateVisual();
        }
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        window.UpdateLayout();

        double w = window.ActualWidth, h = window.ActualHeight;
        // 2 倍渲染:rail 只有 ~75diu 宽,1 倍图上看不清端部留白与环有没有被裁,
        // 也没法判断"内圈该不该出现"这种细节。
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(w * 2), (int)Math.Ceiling(h * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(window);

        // 深灰底,好看清黑色玻璃 rail 的边界
        var bg = new RenderTargetBitmap(
            (int)Math.Ceiling(w * 2), (int)Math.Ceiling(h * 2), 192, 192, PixelFormats.Pbgra32);
        var bv = new DrawingVisual();
        using (var dc = bv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)), null, new Rect(0, 0, w, h));
            dc.DrawImage(bmp, new Rect(0, 0, w, h));
        }
        bg.Render(bv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bg));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Diagnostics.Note($"rail 已出图 {path} ({w:0}x{h:0})");
        window.Close();
    }
}

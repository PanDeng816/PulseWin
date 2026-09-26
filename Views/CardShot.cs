using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseWin;

/// <summary>
/// 详情卡的离屏渲染(`--card-shot`)。用合成数据按真实排版规则出图,不开界面、不联网。
///
/// 为什么要有它:卡片的排版规则是"内容涨了就缩字号、放不下就被裁"这类像素级判断,
/// 靠真人 hover 复现既慢又不成体系。这里一次把所有会出现的极端情况(最长金额、
/// 最长剩余时间、有/无预测、陈旧提示、余额型带柱图)都渲出来,一眼看缺不缺字。
/// </summary>
internal static class CardShot
{
    public static void Run()
    {
        string dir = Path.Combine(SnapshotSource.DataDirectory, "card-shot");
        Directory.CreateDirectory(dir);

        try
        {
            foreach (var (name, sub, stale) in Cases())
            {
                var (win, body) = CardLayout.SubCard(sub.Pools.Count, stale, sub.HourlySpend is not null);
                Render(sub, win, body, stale, Path.Combine(dir, name + ".png"));
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Note("卡片出图失败", ex);
        }
    }

    /// <summary>各种会出现的极端情况。名字即文件名,一眼知道看的是哪一种。</summary>
    private static IEnumerable<(string Name, SubData Sub, bool Stale)> Cases()
    {
        // 1) GOAT:典型三池(5小时/本周/总额度)
        yield return ("goat-typical", Goat(new[]
        {
            Pool("5小时", "FiveHour", 0, 14, "cr", TimeSpan.FromHours(3.2)),
            Pool("本周", "Weekly", 9, 35, "cr", TimeSpan.FromDays(2.3)),
            Pool("总额度", "Monthly", 60.3, 70, "cr", TimeSpan.FromDays(3.9)),
        }), false);

        // 2) GOAT 最坏:长金额 + 长剩余 + 消耗预测(历史上被裁的就是这一类)
        yield return ("goat-worst", Goat(new[]
        {
            Pool("5小时", "FiveHour", 0, 14, "cr", TimeSpan.FromHours(3.2)),
            Pool("本周", "Weekly", 21.5, 35, "cr", TimeSpan.FromDays(6.9)),
            Pool("总额度", "Monthly", 9999.99, 12345.67, "$", TimeSpan.FromDays(22.9)),
        }), false);

        // 3) GOAT 陈旧 + 有预测
        yield return ("goat-stale", Goat(new[]
        {
            Pool("5小时", "FiveHour", 4.5, 14, "cr", TimeSpan.FromHours(1.5)),
            Pool("本周", "Weekly", 20, 35, "cr", TimeSpan.FromDays(4.4)),
            Pool("总额度", "Monthly", 61, 70, "cr", TimeSpan.FromDays(3.1)),
        }, fetched: DateTimeOffset.UtcNow.AddMinutes(-42)), true);

        // 4) 余额型(DeepSeek):余额 + 今日柱图 + 充值/赠送拆分
        yield return ("deepseek-balance", new SubData
        {
            Key = "deepseek",
            Name = "DeepSeek",
            AccountLabel = "PanDeng",
            Pools = new List<PoolData>
            {
                new()
                {
                    Label = "余额", PoolKind = "Balance", Unit = "$",
                    IsAvailable = true, Amount = 12.34, Used = 7.66, Cap = 20.0,
                    EstimateFrom = "自上次充值", ToppedUpAmount = 15.0, GrantedAmount = 5.0,
                },
            },
            HourlySpend = BuildHourly(),
        }, false);

        static SubData Goat(PoolData[] pools, DateTimeOffset? fetched = null) => new()
        {
            Key = "goat", Name = "GOAT", AccountLabel = "PanDeng816",
            PeriodTokens = 3_594_700_000, Pools = pools.ToList(),
            FetchedAt = fetched ?? DateTimeOffset.UtcNow,
        };
    }

    private static PoolData Pool(string label, string kind, double used, double cap, string unit,
        TimeSpan resetIn) => new()
    {
        Label = label, PoolKind = kind, Used = used, Cap = cap, Unit = unit,
        IsAvailable = true, ResetAt = DateTimeOffset.UtcNow + resetIn,
    };

    private static double[] BuildHourly()
    {
        var arr = new double[24];
        var rnd = new Random(7);
        for (int h = 0; h < 24; h++)
            arr[h] = h < 9 ? 0 : Math.Round(rnd.NextDouble() * 1.2, 2);
        return arr;
    }

    private static void Render(SubData sub, Size win, Rect body, bool stale, string path)
    {
        // 与真机一致:容器按实际窗口尺寸分配 diu→px,卡窗在 200% DPI 下也走这套。
        double dpi = 96;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            CardRenderer.Draw(dc, sub, body, stale, dpi, DateTimeOffset.UtcNow);

        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(win.Width), (int)Math.Ceiling(win.Height), dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(visual);
        // PNG 保留透明通道看不出卡片边界,铺一层浅灰底便于看边距
        var bg = new RenderTargetBitmap(
            (int)Math.Ceiling(win.Width), (int)Math.Ceiling(win.Height), dpi, dpi, PixelFormats.Pbgra32);
        var bv = new DrawingVisual();
        using (var dc = bv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), null,
                new Rect(0, 0, win.Width, win.Height));
            dc.DrawImage(bmp, new Rect(0, 0, win.Width, win.Height));
        }
        bg.Render(bv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bg));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}

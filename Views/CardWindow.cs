using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PulseWin;

/// <summary>详情卡窗布局:规则圆角方框(无指针),贴 rail 一侧放置。</summary>
public static class CardLayout
{
    public const double Margin = 12;
    public static readonly double GapToRail = Card.HorizontalGap;

    // 与 CardWindow.DrawPoolRow 的行距保持一致(中文字体行高偏大,取足量防溢出)
    private static double RowHeightPt = 76;
    private static double StaleRowPt = 22;
    /// <summary>
    /// "今日消耗"区块:标题行 + 柱状图 + 脚注。
    /// 这个数是**实测累加**出来的(中文 FormattedText 行高约 1.35 倍字号,比拉丁大不少):
    /// 标题 22 + 间距 9 + 柱图 40 + 间距 8 + 脚注 19 + 收尾 5 ≈ 103,取 106 留余量。
    /// 之前按 74 留,脚注被推到卡片外面去了。
    /// </summary>
    private static double ChartBlockPt = 106;

    /// <summary>账户卡的窗/内容框。高度按内容动态计算,防止文字溢出。</summary>
    public static (Size win, Rect body) SubCard(int poolCount, bool showStale, bool showChart)
    {
        double w = Card.Width;
        // 头部区(图标 + 两行名 + 下移留白)→ 首行 y≈58
        double h = Pt.P(58) + RowHeightPt * poolCount + Pt.P(24)
            + (showStale ? StaleRowPt : 0)
            + (showChart ? ChartBlockPt : 0);
        double m = Margin;
        return (new Size(w + m * 2, h + m * 2), new Rect(m, m, w, h));
    }
}

/// <summary>详情卡窗:黑色玻璃圆角方框。显示一个订阅的 5小时/本周/总额度三池明细。</summary>
public sealed class CardWindow : Window
{
    private SubData? _sub;
    private Rect _body;
    private bool _showStale;
    private readonly DispatcherTimer _ticker;

    public CardWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Native.ApplyToolWindowStyle(this);

        // 每秒重绘一次:剩余时间倒计时实时走动。隐藏时停表,不做无用重绘。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) =>
        {
            if (_sub is not null && IsVisible) InvalidateVisual();
        };
    }

    public void Configure(SubData sub, Size winSize, Rect body, bool showStale)
    {
        _sub = sub;
        _body = body;
        _showStale = showStale;
        Width = winSize.Width; Height = winSize.Height;
        InvalidateVisual();
    }

    /// <summary>只移动位置不重绘(rail 滑入/拖动时让卡片跟着走,避免与 rail 重叠)。</summary>
    public void MoveTo(Point topLeft)
    {
        Left = topLeft.X;
        Top = topLeft.Y;
    }

    public void ShowCard()
    {
        if (!IsVisible) Show();
        if (!_ticker.IsEnabled) _ticker.Start();
        Native.BringToTopmost(this); // 卡窗也要压在其他置顶窗口之上
    }

    public void HideCard()
    {
        _sub = null;
        _ticker.Stop();
        Hide();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_sub is not { } sub) return;
        double dpi = Native.Scale(this);

        double r = Card.CornerRadius;
        dc.DrawRoundedRectangle(Solid(PanelPalette.Surface), null, _body, r, r);

        double x = _body.X, y = _body.Y;
        double w = _body.Width;
        double pad = Card.Padding;
        double px = x + pad;
        double contentW = w - pad * 2;

        // 组头:图标 + 名称 + 账户标识
        double iconBox = Pt.P(18);
        double py = y + pad - 2;
        DrawLogo(dc, sub, new Rect(px, py, iconBox, iconBox));
        var nameFt = Text(sub.Name, Pt.P(15), FontWeights.SemiBold, Solid(PanelPalette.Primary), dpi);
        dc.DrawText(nameFt, new Point(px + iconBox + Pt.P(7), py + (iconBox - nameFt.Height) / 2));
        var acctFt = Text(sub.AccountLabel, Pt.P(11), FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
        dc.DrawText(acctFt, new Point(px + iconBox + Pt.P(7), py + (iconBox - nameFt.Height) / 2 + nameFt.Height + 1));

        // 头部右侧:总用量。有 token 数据的源(GOAT)显示"本月已用 X.X M token";
        // 无 token 的源(GO)回退显示月度用量百分比。
        if (sub.PeriodTokens is { } tokens)
        {
            var tokenFt = Text(FormatTokens(tokens), Pt.P(17), FontWeights.Bold,
                Solid(PanelPalette.Primary), dpi);
            dc.DrawText(tokenFt, new Point(px + contentW - tokenFt.Width, py - 1));
            var labelFt = Text("本月已用 Token", Pt.P(10.5), FontWeights.Normal,
                Solid(PanelPalette.Dim), dpi);
            dc.DrawText(labelFt, new Point(px + contentW - labelFt.Width, py - 1 + tokenFt.Height + Pt.P(1)));
        }
        else if (sub.Pools.FirstOrDefault(p => p.PoolKind == "Monthly") is { IsAvailable: true } monthPool)
        {
            // 头部只报百分比,不报金额。金额要先乘"1 credit 值多少美元"(模型的 monthly
            // allowance),而那个数官方随时会调、换个模型也会变(见 CommandCodeApiClient
            // 的 CreditUnit 说明)—— 写在这里等于把服务端原值换成一个会飘的推算值。
            // 绝对量放在下面的池行里,以 credits(服务端原值)呈现。
            Color mt = UsageTint.For(monthPool.Fraction, monthPool.IsSpent);
            var usedFt = Text(monthPool.PercentText, Pt.P(17), FontWeights.Bold, Solid(mt), dpi);
            dc.DrawText(usedFt, new Point(px + contentW - usedFt.Width, py - 1));
            var labelFt = Text("本月额度已用", Pt.P(10.5),
                FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
            dc.DrawText(labelFt, new Point(px + contentW - labelFt.Width, py - 1 + usedFt.Height + Pt.P(1)));
        }
        else if (sub.Pools.FirstOrDefault(p => p.PoolKind == "Balance") is { IsAvailable: true } balancePool)
        {
            // 余额型:右边就是钱本身。它没有"已用/总额"可写,而钱就是这一池的全部读数。
            Color bt = UsageTint.For(balancePool.Fraction, balancePool.IsSpent);
            var amountFt = Text(Money.Exact(balancePool.Amount, balancePool.Unit), Pt.P(17),
                FontWeights.Bold, Solid(bt), dpi);
            dc.DrawText(amountFt, new Point(px + contentW - amountFt.Width, py - 1));
            var basisFt = Text(balancePool.EstimateFrom ?? "当前余额", Pt.P(10.5),
                FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
            dc.DrawText(basisFt, new Point(px + contentW - basisFt.Width, py - 1 + amountFt.Height + Pt.P(1)));
        }

        // 三池行(整体下移,头部与池行之间留出更从容的空间)
        double rowY = py + iconBox + Pt.P(26);
        foreach (var pool in sub.Pools)
            rowY = DrawPoolRow(dc, pool, new Point(px, rowY), contentW, dpi);

        // 余额型数据源再画一张"今日消耗"(数据来自本程序自己的按小时采样)
        double contentBottom = rowY;
        if (sub.HourlySpend is { } hourly)
        {
            var balancePool = sub.Pools.FirstOrDefault(p => p.PoolKind == "Balance");
            contentBottom = DrawSpendChart(dc, hourly, balancePool, new Point(px, rowY), contentW, dpi);
        }

        // 数据陈旧提示:网络断了以后界面照旧显示最后一次成功数据,如果不标明
        // 用户会以为看到的是最新值。
        if (_showStale && sub.FetchedAt is { } fetched)
        {
            var age = DateTimeOffset.UtcNow - fetched;
            string text = age.TotalMinutes < 60
                ? $"数据 {(int)Math.Max(age.TotalMinutes, 1)} 分钟前"
                : $"数据 {age.TotalHours:0.#} 小时前";
            var staleFt = Text("⚠ " + text + " · 未能刷新", Pt.P(10.5), FontWeights.Normal,
                Solid(UsageTint.Alarm), dpi);
            double staleY = contentBottom - Pt.P(4);
            dc.DrawText(staleFt, new Point(px, staleY));
        }
    }

    /// <summary>
    /// 今日每小时的消耗柱状图。
    ///
    /// 数据来自本程序对余额的按小时采样,**没有运行的那些小时是空的**——所以
    /// 图上留一条极细的淡线表示"没采样",而不是画成 0:宁可缺一根柱子,也不能把
    /// "没看见"说成"没花钱"。
    /// </summary>
    private double DrawSpendChart(DrawingContext dc, double[] spend, PoolData? pool,
        Point p, double width, double dpi)
    {
        string unit = pool?.Unit ?? "$";
        double total = spend.Sum();

        var titleFt = Text("今日消耗", Pt.P(12.5), FontWeights.Medium, Solid(PanelPalette.Primary), dpi);
        dc.DrawText(titleFt, p);
        var totalFt = Text(Money.Exact(total, unit), Pt.P(12.5), FontWeights.SemiBold,
            Solid(total > 0 ? UsageTint.Good : PanelPalette.Dim), dpi);
        dc.DrawText(totalFt, new Point(p.X + width - totalFt.Width, p.Y));

        double chartTop = p.Y + titleFt.Height + Pt.P(7);
        double chartHeight = Pt.P(30);
        double gap = Pt.P(1.5);
        double barWidth = (width - gap * 23) / 24;
        double max = spend.Length > 0 ? spend.Max() : 0;
        double floorHeight = Pt.P(1.2);

        // 一条很淡的基线:柱底对齐之外,也让"没采样的小时"有个参照物
        var axisPen = new Pen(Solid(WithAlpha(PanelPalette.Primary, 0.14)), 1);
        axisPen.Freeze();
        dc.DrawLine(axisPen, new Point(p.X, chartTop + chartHeight),
            new Point(p.X + width, chartTop + chartHeight));

        for (int hour = 0; hour < spend.Length && hour < 24; hour++)
        {
            double barHeight = max > 0 ? spend[hour] / max * chartHeight : 0;
            double drawn = Math.Max(barHeight, floorHeight);
            var rect = new Rect(p.X + hour * (barWidth + gap), chartTop + chartHeight - drawn,
                barWidth, drawn);
            dc.DrawRoundedRectangle(
                Solid(spend[hour] > 0 ? UsageTint.Good : WithAlpha(PanelPalette.Primary, 0.10)),
                null, rect, Pt.P(1), Pt.P(1));
        }

        // 脚注:写清数据怎么来的,顺带给出现成的充值/赠送拆分(API 直接提供)。
        // 金额为 0 的那一项不写,免得出现"赠送 ¥0.00"这种废话。
        string source = max > 0 ? "按小时采样余额差" : "今日暂无采样";
        string split = "";
        if (pool is { ToppedUpAmount: { } topped } && topped > 0)
        {
            split = $" · 充值 {Money.Exact(topped, unit)}";
            if (pool.GrantedAmount is { } granted && granted > 0)
                split += $" + 赠送 {Money.Exact(granted, unit)}";
        }
        var noteFt = Text(source + split, Pt.P(10.5), FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
        double noteY = chartTop + chartHeight + Pt.P(6);
        dc.DrawText(noteFt, new Point(p.X, noteY));

        // 收尾间距留给外层的 Card.Padding,这里只补一点点
        return noteY + noteFt.Height + Pt.P(4);
    }

    private double DrawPoolRow(DrawingContext dc, PoolData pool, Point p, double wdt, double dpi)
    {
        bool avail = pool.IsAvailable;
        double frac = avail ? Math.Clamp(pool.Fraction, 0, 1) : 0;
        Color tint = avail ? UsageTint.For(frac, pool.IsSpent) : PanelPalette.Track;

        // 行1:池名 + 读数(>80% 红)
        var labelFt = Text(pool.Label, Pt.P(12.5), FontWeights.Medium, Solid(PanelPalette.Primary), dpi);
        dc.DrawText(labelFt, new Point(p.X, p.Y));
        string readout = !avail ? "--"
            : pool.PoolKind == "Balance" ? pool.DisplayText
            : pool.PercentText;
        var pctFt = Text(readout, Pt.P(12.5), FontWeights.SemiBold, Solid(tint), dpi);
        dc.DrawText(pctFt, new Point(p.X + wdt - pctFt.Width, p.Y));

        // 行2:进度条
        double barY = p.Y + labelFt.Height + Pt.P(5);
        double barH = Card.ProgressBarHeight;
        var track = new Rect(p.X, barY, wdt, barH);
        dc.DrawRoundedRectangle(Solid(WithAlpha(PanelPalette.Primary, 0.12)), null, track, barH / 2, barH / 2);
        if (frac > 0.004)
        {
            var fill = new Rect(p.X, barY, Math.Max(wdt * frac, barH), barH);
            dc.DrawRoundedRectangle(Solid(tint), null, fill, barH / 2, barH / 2);
        }

        // 行3:用量 / 剩余时间(倒计时实时)
        string detail = !avail ? "暂无读数"
            : pool.PoolKind == "Balance" ? BalanceDetail(pool)
            // credits 是服务端原值,直接照搬(不折美元);写一次单位,免得"70 cr / 35 cr"啰嗦
            : pool.Unit == "cr"
                ? $"{pool.Used ?? 0:0.#} / {pool.Cap ?? 0:0.#} cr · {pool.RemainingText()}"
                : $"{Amount(pool.Used, pool.Unit)} / {Amount(pool.Cap, pool.Unit)} · {pool.RemainingText()}";
        var detailFt = Text(detail, Pt.P(10.5), FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
        double detailY = barY + barH + Pt.P(5);
        dc.DrawText(detailFt, new Point(p.X, detailY));

        return detailY + detailFt.Height + Pt.P(12);
    }

    /// <summary>
    /// 余额型池的行3:钱 + 分母从哪来。没有"重置"可倒计时——预付余额不会翻篇,
    /// 所以这里也不该出现剩余时间。
    /// </summary>
    private static string BalanceDetail(PoolData pool)
    {
        string money = $"余额 {Money.Exact(pool.Amount, pool.Unit)}";
        return pool.EstimateFrom is { } from ? $"{money} · {from}"
            : pool.Note is { } note ? $"{money} · {note}"
            : money;
    }

    private static string Amount(double? v, string unit) => v is not { } n
        ? "--"
        : unit == "%" ? n.ToString("0") + "%"
        : unit + n.ToString("0.00");

    /// <summary>Token 总量:≥1M 保留 1 位小数,<1M 保留 2 位小数,单位 M。</summary>
    private static string FormatTokens(long tokens)
    {
        double m = tokens / 1_000_000d;
        return m >= 1 ? m.ToString("0.0") + "M" : m.ToString("0.00") + "M";
    }

    private void DrawLogo(DrawingContext dc, SubData sub, Rect box)
    {
        if (sub.Key == "goat")
        {
            var bmp = Icons.GoatBitmap();
            if (bmp is not null) dc.DrawImage(bmp, box);
            return;
        }

        if (sub.Key == "deepseek")
        {
            var whale = Icons.DeepSeekGeometry().Clone();
            double whaleScale = box.Width / 24.0;
            whale.Transform = new MatrixTransform(whaleScale, 0, 0, whaleScale, box.X, box.Y);
            dc.DrawGeometry(Solid(PanelPalette.Primary), null, whale);
            return;
        }

        var g = Icons.OpenCodeGeometry().Clone();
        double s = box.Width / 24.0;
        g.Transform = new MatrixTransform(s, 0, 0, s, box.X, box.Y);
        dc.DrawGeometry(Solid(PanelPalette.Primary), null, g);
    }

    private static FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dpi) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, dpi);

    private static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);
}

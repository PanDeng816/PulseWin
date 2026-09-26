using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 详情卡窗布局:规则圆角方框(无指针),贴 rail 一侧放置。
///
/// 高度**不另立常量**,而是直接向 <see cref="CardRenderer"/> 要实际步进——
/// 从前两套数各写一份,结果就是"卡片比内容矮(裁字)"或"底部留一大块空",
/// 每次调行距都要人工对齐两个文件。见 v1.10.1 的紧凑化。
/// </summary>
public static class CardLayout
{
    public const double Margin = 12;
    public static readonly double GapToRail = Card.HorizontalGap;

    /// <summary>账户卡的窗/内容框。高度按内容动态计算,防止文字溢出/留大片空。</summary>
    public static (Size win, Rect body) SubCard(int poolCount, bool showStale, bool showChart)
    {
        double w = Card.Width;
        double h = CardRenderer.HeaderBlock
            + CardRenderer.RowPitch * poolCount
            + CardRenderer.TailBlock
            + (showStale ? CardRenderer.StaleBlock : 0)
            + (showChart ? CardRenderer.ChartBlock : 0);
        double m = Margin;
        return (new Size(w + m * 2, h + m * 2), new Rect(m, m, w, h));
    }
}

/// <summary>
/// 详情卡内容绘制。从 <see cref="CardWindow"/> 抽出来是为了能**离屏渲染**:
/// 卡片的排版规则(文字会不会被右缘切、底部会不会溢出)必须在像素上核对,
/// 而不是等用户 hover 时才发现——诊断开关 <c>--card-shot</c> 就是走这条路径出图。
///
/// 下面那组 <c>*Block</c>/<c>RowPitch</c> 是**绘制步进本身**,布局高度直接引用它们,
/// 所以改间距不会出现"画面改了、卡片高度没改"的错位。
/// </summary>
public static class CardRenderer
{
    // —— 关键竖直间距(单位 mac pt,与绘制代码里用的同一批值) ——
    private const double RowGapAfterLabel = 3;     // 池名行 → 进度条
    private const double RowGapAfterBar = 3;       // 进度条 → 明细行
    private const double RowGapAfterDetail = 7;    // 明细行 → 下一行
    private const double ChartGapAfterTitle = 7;
    private const double ChartGapAfterPlot = 6;

    /// <summary>头部区高度:顶部留白 + 图标 + 与首行的留白(即首行相对 body 顶的偏移)。</summary>
    public static readonly double HeaderBlock = Pt.P(18) - 2 + Pt.P(18) + Pt.P(18);

    /// <summary>
    /// 一行的实际绘制步进。用与绘制同款的 FormattedText 量出来,而不是拍一个整数——
    /// 中文行高是字体相关的浮点值,手写的整数迟早与画面错开。
    /// </summary>
    public static readonly double RowPitch = MeasureRowPitch();

    /// <summary>末行之后到 body 底的留白(与顶部留白相称:Pad 减去行尾自带的间隔)。</summary>
    public static readonly double TailBlock = Pt.P(18) - Pt.P(RowGapAfterDetail);

    /// <summary>数据陈旧提示占的高度(与上一块的间隔 + 一行文字)。</summary>
    public static readonly double StaleBlock = Pt.P(2) + MeasureLine(Pt.P(10.5));

    /// <summary>"今日消耗"区块(标题 + 柱图 + 脚注)的高度。</summary>
    public static readonly double ChartBlock = Pt.P(12.5 + ChartGapAfterTitle)
        + Pt.P(26) + Pt.P(ChartGapAfterPlot) + MeasureLine(Pt.P(10.5));

    private static double MeasureRowPitch()
    {
        double label = MeasureLine(Pt.P(12.5));
        double detail = MeasureLine(Pt.P(10.5));
        return label + Pt.P(RowGapAfterLabel) + Card.ProgressBarHeight + Pt.P(RowGapAfterBar)
            + detail + Pt.P(RowGapAfterDetail);
    }

    /// <summary>量一行中文文本的高度(最坏情况按全中文取,英文只会更矮)。</summary>
    private static double MeasureLine(double size) =>
        Text("本周明细", size, FontWeights.Normal, Brushes.Black, 96).Height;

    public static void Draw(DrawingContext dc, SubData sub, Rect body, bool showStale,
        double dpi, DateTimeOffset now)
    {
        double r = Card.CornerRadius;
        dc.DrawRoundedRectangle(Solid(PanelPalette.Surface), null, body, r, r);

        double x = body.X, y = body.Y;
        double w = body.Width;
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

        DrawHeaderReadout(dc, sub, new Point(px, py), contentW, dpi);

        // 三池行
        double rowY = y + HeaderBlock;
        foreach (var pool in sub.Pools)
            rowY = DrawPoolRow(dc, sub, pool, new Point(px, rowY), contentW, dpi, now);

        // 余额型数据源再画一张"今日消耗"(数据来自本程序自己的按小时采样)
        double cursorY = rowY;
        if (sub.HourlySpend is { } hourly)
        {
            var balancePool = sub.Pools.FirstOrDefault(p => p.PoolKind == "Balance");
            cursorY = DrawSpendChart(dc, hourly, balancePool, new Point(px, cursorY), contentW, dpi);
        }

        // 数据陈旧提示:网络断了以后界面照旧显示最后一次成功数据,如果不标明
        // 用户会以为看到的是最新值。
        if (showStale && sub.FetchedAt is { } fetched)
        {
            var age = now - fetched;
            string text = age.TotalMinutes < 60
                ? $"数据 {(int)Math.Max(age.TotalMinutes, 1)} 分钟前"
                : $"数据 {age.TotalHours:0.#} 小时前";
            var staleFt = Text("⚠ " + text + " · 未能刷新", Pt.P(10.5), FontWeights.Normal,
                Solid(UsageTint.Alarm), dpi);
            dc.DrawText(staleFt, new Point(px, cursorY + Pt.P(2)));
        }
    }

    /// <summary>
    /// 头部右侧读数。有 token 数据的源(GOAT)显示"本月已用 X.X M token";
    /// 无 token 的源(GO)回退显示月度用量百分比;余额型(DeepSeek)直接显示余额。
    /// </summary>
    private static void DrawHeaderReadout(DrawingContext dc, SubData sub, Point p, double contentW, double dpi)
    {
        if (sub.PeriodTokens is { } tokens)
        {
            var tokenFt = Text(FormatTokens(tokens), Pt.P(17), FontWeights.Bold,
                Solid(PanelPalette.Primary), dpi);
            dc.DrawText(tokenFt, new Point(p.X + contentW - tokenFt.Width, p.Y - 1));
            var labelFt = Text("本月已用 Token", Pt.P(10.5), FontWeights.Normal,
                Solid(PanelPalette.Dim), dpi);
            dc.DrawText(labelFt, new Point(p.X + contentW - labelFt.Width, p.Y - 1 + tokenFt.Height + Pt.P(1)));
        }
        else if (sub.Pools.FirstOrDefault(pl => pl.PoolKind == "Monthly") is { IsAvailable: true } monthPool)
        {
            // 头部只报百分比,不报金额。金额要先乘"1 credit 值多少美元"(模型的 monthly
            // allowance),而那个数官方随时会调、换个模型也会变——写在这里等于把服务端
            // 原值换成一个会飘的推算值。绝对量放在下面的池行里,以 credits(服务端原值)呈现。
            Color mt = UsageTint.For(monthPool.Fraction, monthPool.IsSpent);
            var usedFt = Text(monthPool.PercentText, Pt.P(17), FontWeights.Bold, Solid(mt), dpi);
            dc.DrawText(usedFt, new Point(p.X + contentW - usedFt.Width, p.Y - 1));
            var labelFt = Text("本月额度已用", Pt.P(10.5), FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
            dc.DrawText(labelFt, new Point(p.X + contentW - labelFt.Width, p.Y - 1 + usedFt.Height + Pt.P(1)));
        }
        else if (sub.Pools.FirstOrDefault(pl => pl.PoolKind == "Balance") is { IsAvailable: true } balancePool)
        {
            // 余额型:右边就是钱本身。它没有"已用/总额"可写,而钱就是这一池的全部读数。
            Color bt = UsageTint.For(balancePool.Fraction, balancePool.IsSpent);
            var amountFt = Text(Money.Exact(balancePool.Amount, balancePool.Unit), Pt.P(17),
                FontWeights.Bold, Solid(bt), dpi);
            dc.DrawText(amountFt, new Point(p.X + contentW - amountFt.Width, p.Y - 1));
            var basisFt = Text(balancePool.EstimateFrom ?? "当前余额", Pt.P(10.5),
                FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
            dc.DrawText(basisFt, new Point(p.X + contentW - basisFt.Width, p.Y - 1 + amountFt.Height + Pt.P(1)));
        }
    }

    /// <summary>
    /// 今日每小时的消耗柱状图。
    ///
    /// 数据来自本程序对余额的按小时采样,**没有运行的那些小时是空的**——所以
    /// 图上留一条极细的淡线表示"没采样",而不是画成 0:宁可缺一根柱子,也不能把
    /// "没看见"说成"没花钱"。返回值 = 下一块内容应从这里开始的 y(<see cref="ChartBlock"/>)。
    /// </summary>
    private static double DrawSpendChart(DrawingContext dc, double[] spend, PoolData? pool,
        Point p, double width, double dpi)
    {
        string unit = pool?.Unit ?? "$";
        double total = spend.Sum();

        var titleFt = Text("今日消耗", Pt.P(12.5), FontWeights.Medium, Solid(PanelPalette.Primary), dpi);
        dc.DrawText(titleFt, p);
        var totalFt = Text(Money.Exact(total, unit), Pt.P(12.5), FontWeights.SemiBold,
            Solid(total > 0 ? UsageTint.Good : PanelPalette.Dim), dpi);
        dc.DrawText(totalFt, new Point(p.X + width - totalFt.Width, p.Y));

        double chartTop = p.Y + titleFt.Height + Pt.P(ChartGapAfterTitle);
        double chartHeight = Pt.P(26);
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
        double noteY = chartTop + chartHeight + Pt.P(ChartGapAfterPlot);
        dc.DrawText(noteFt, new Point(p.X, noteY));

        return noteY + noteFt.Height;
    }

    private static double DrawPoolRow(DrawingContext dc, SubData sub, PoolData pool, Point p, double wdt,
        double dpi, DateTimeOffset now)
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
        double barY = p.Y + labelFt.Height + Pt.P(RowGapAfterLabel);
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
                ? $"{pool.Used ?? 0:0.#} / {pool.Cap ?? 0:0.#} cr · {pool.RemainingText(now)}"
                : $"{Amount(pool.Used, pool.Unit)} / {Amount(pool.Cap, pool.Unit)} · {pool.RemainingText(now)}";
        // 消耗预测:证据足够时给一句"照当前速度",够不到重置就预警(上游 forecast 同款口径)。
        // 文案要短:"照当前速度"这五个字是冗余的("约"已经表意),带着它整行会在
        // 最坏情况下(长金额 + 长剩余时间 + 预测)超出卡片宽度被裁掉。
        double? hoursLeft = pool.ResetAt is { } ra ? (ra - now).TotalHours : null;
        if (avail && pool.HasPercent && !pool.IsSpent
            && BurnRate.Estimate($"{sub.Key}:{pool.PoolKind}", hoursLeft) is { } burn)
        {
            detail += burn.Exhausts
                ? $" · 约 {BurnRate.Describe(burn.Hours)}后用完"
                : " · 预计够用到重置";
        }
        // 行3 必须自己保证不超出卡片:它是单行绘制,内容却会随金额位数、剩余天数
        // 与有没有预测而变长(实测最坏能到 338diu,而内容区只有约 290diu)。
        // 放不下就按比例缩字号(量化 0.5pt,护住文本缓存),而不是让字被卡片边缘切掉。
        var detailFt = FitText(detail, Pt.P(10.5), FontWeights.Normal, Solid(PanelPalette.Dim), dpi, wdt);
        double detailY = barY + barH + Pt.P(RowGapAfterBar);
        dc.DrawText(detailFt, new Point(p.X, detailY));

        return detailY + detailFt.Height + Pt.P(RowGapAfterDetail);
    }

    /// <summary>
    /// 取一个能塞进 <paramref name="maxWidth"/> 的文本:先按给定字号试,放不下就按比例缩。
    /// 字号**量化到 0.5pt**——否则 FormattedText 会因为连续变化的字号不断新建对象。
    /// 下限 8.5pt:再小就难以辨认,那种情况说明这一行的信息确实过密。
    /// </summary>
    private static FormattedText FitText(string s, double baseSize, FontWeight weight,
        Brush brush, double dpi, double maxWidth)
    {
        var ft = Text(s, baseSize, weight, brush, dpi);
        if (ft.Width <= maxWidth) return ft;

        double step = Pt.P(0.5);
        double min = Pt.P(8.5);
        double target = Math.Max(baseSize * maxWidth / ft.Width, min);
        double quantized = Math.Max(Math.Floor(target / step) * step, min);
        return Text(s, quantized, weight, brush, dpi);
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

    private static void DrawLogo(DrawingContext dc, SubData sub, Rect box)
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

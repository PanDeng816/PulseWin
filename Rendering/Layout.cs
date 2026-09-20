using System.Windows;

namespace PulseWin;

/// <summary>停靠轴。浮动时沿用竖直布局(胶囊)。</summary>
public enum DockEdge { Left, Right, Top, Floating }

/// <summary>把 mac pt 规格换算为 WPF 设备无关单位(mac 1pt = 1/72in,WPF 1diu = 1/96in)。</summary>
public static class Pt
{
    public const double U = 96.0 / 72.0;
    public static double P(double macPt) => macPt * U;
}

/// <summary>
/// DockLayout + DetailCardLayout + UsageRingView 的全部几何常量(标准档)。
/// 名称与数值直接对应 Pulse mac 版源码,方便日后逐项比对。
/// </summary>
public static class Dock
{
    // —— rail 本体 ——
    public static double Width => Pt.P(64);
    public static double VerticalPadding => Pt.P(46);
    public static double HorizontalPadding => Pt.P(10);

    public static double RingDiameter => Pt.P(36);
    public static double RingLineWidth => Pt.P(4);
    public static double RingToTextSpacing => Pt.P(6);
    public static double PercentFontSize => Pt.P(13);
    public static double PercentTextHeight => Pt.P(16);
    public static double PercentTextWidth => Pt.P(38);
    public static double ItemSpacing => Pt.P(30);

    public static double SecondRingDiameter => Pt.P(26);
    public static double SecondRingLineWidth => Pt.P(2.5);

    public static double CornerRadius => Pt.P(26);
    public static double FlareHeight => Pt.P(24);
    public static double FlareWidth => Pt.P(38);

    public static double ItemHeight => RingDiameter + RingToTextSpacing + PercentTextHeight;

    /// <summary>一个 item 沿 rail 方向占的长度(侧轨=环叠标签,顶部=更宽者)。</summary>
    public static double ItemLength(bool vertical, bool showsPercent) =>
        !showsPercent ? RingDiameter
        : vertical ? ItemHeight
        : Math.Max(RingDiameter, PercentTextWidth);

    /// <summary>rail 厚度:侧轨恒 width;顶轨带标签时另算。</summary>
    public static double Thickness(bool vertical, bool showsPercent) =>
        vertical || !showsPercent ? Width : ItemHeight + HorizontalPadding * 2;

    public static double EndPadding(bool docked) => docked ? VerticalPadding : VerticalPadding - FlareHeight;

    public static double ItemStep(bool vertical, bool showsPercent) =>
        ItemLength(vertical, showsPercent) + ItemSpacing;

    /// <summary>第一个环中心离端头的距离(侧轨,标签在下:0 + 半环)。</summary>
    public static double FirstRingAlong(bool vertical, bool showsPercent) =>
        EndPadding(docked: true)
        + (vertical ? RingDiameter / 2 : ItemLength(vertical, showsPercent) / 2);

    /// <summary>rail 全长。</summary>
    public static double Length(int itemCount, bool vertical, bool showsPercent, bool docked = true) =>
        EndPadding(docked) * 2
        + ItemLength(vertical, showsPercent) * itemCount
        + ItemSpacing * (itemCount - 1);

    public static Size RailSize(int count, bool vertical, bool showsPercent, bool docked = true) =>
        vertical
            ? new Size(Width, Length(count, vertical, showsPercent, docked))
            : new Size(Length(count, vertical, showsPercent, docked), Thickness(vertical, showsPercent));

    // —— 折叠 sliver ——
    public static double CollapsedWidth => Pt.P(6);
    public static double CollapsedLength => Pt.P(96);
    public static double CollapsedHitWidth => Pt.P(20);

    public static Size CollapsedSize(bool vertical) => vertical
        ? new Size(CollapsedWidth, CollapsedLength)
        : new Size(CollapsedLength, CollapsedWidth);

    // —— 环内部件 ——
    public static double CentreGap => Pt.P(4);
    public static double IconScale => 0.8;
    public static double SecondRingSqueeze => Pt.P(2);
    public static double HaloRadius => Pt.P(10);
    public static double ClockGap => Pt.P(3);
    public static double ClockLineWidth => Pt.P(2);
    public static double BusySweep => 0.22;
    public static double RefreshSweep => 0.16;
    public static double BusyPeriodS => 1.0;
    public static double RefreshPeriodS => 0.85;

    public static double CentreDiameter(bool hasSecond) =>
        Math.Max(RingDiameter - (RingLineWidth + CentreGap) * 2 - (hasSecond ? SecondRingSqueeze * 2 : 0), 0);

    public static double BusyDiameter(bool hasSecond) => hasSecond
        ? Math.Max(CentreDiameter(true) + SecondRingSqueeze, 0)
        : Math.Max(RingDiameter - RingLineWidth * 1.5 - CentreGap, 0);

    public static double ClockDiameter =>
        RingDiameter + RingLineWidth + (ClockGap + ClockLineWidth / 2) * 2;

    // —— 吸附距离(拖拽时融合到屏幕边的判定阈值) ——
    public static double DockSnapDistance => Pt.P(40);
}

/// <summary>详情卡几何。卡片圆角体 + 指向环的尖角在同一 path 里。</summary>
public static class Card
{
    public static double Width => Pt.P(250);
    public static double Padding => Pt.P(18);
    public static double CornerRadius => Pt.P(20);
    public static double PointerWidth => Pt.P(20);
    public static double PointerHeight => Pt.P(40);
    public static double HorizontalGap => Pt.P(8);

    public static double ContentSpacing => Pt.P(14);
    public static double RowInternalSpacing => Pt.P(7);
    public static double ProgressBarHeight => Pt.P(6);
    public static double HeaderHeight => Pt.P(19);
    public static double HeaderIconSize => Pt.P(16);

    public static double TitleFontSize => Pt.P(14);
    public static double RowFontSize => Pt.P(11.5);
    public static double FootnoteFontSize => Pt.P(11);
    public static double RowTextLineHeight => Pt.P(14);

    public static double RowHeight => RowTextLineHeight + RowInternalSpacing + ProgressBarHeight + RowInternalSpacing + RowTextLineHeight;

    /// <summary>卡片本体高度(不含指针区)。</summary>
    public static double BodyHeight(int windowCount) =>
        Padding * 2 + HeaderHeight + windowCount * (ContentSpacing + RowHeight);

    /// <summary>指针位于 rail 一侧时,整个卡窗沿 rail 方向的尺寸。</summary>
    public static double FullLength(int windowCount) => BodyHeight(windowCount) + PointerHeight + Pt.P(8);

    /// <summary>整个卡窗的尺寸。side=false 表示卡挂在横轨下方(指针朝上)。</summary>
    public static Size FullSize(int windowCount, bool side) => side
        ? new Size(Width + PointerWidth + HorizontalGap, FullLength(windowCount))
        : new Size(FullLength(windowCount), Width + PointerWidth + HorizontalGap);
}

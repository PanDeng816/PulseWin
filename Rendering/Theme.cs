using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 全应用唯一的设计规格来源,对齐上游 qunqin24/Pulse 的视觉语言。
/// 缓动/时长/阴影参数从这里取;颜色仍在 <see cref="UsageTint"/>(语义色)与
/// <see cref="PanelPalette"/>(面板色)、<see cref="LightPalette"/>(工具窗亮色)。
/// 规格依据:00Chat\tmp\pulse-upstream-visual-spec.md(从上游源码逐值提取)。
/// </summary>
public static class Theme
{
    /// <summary>复合字体:拉丁走 Segoe UI,中文回退雅黑。行高按中文预算(见 v1.0 的裁字教训)。</summary>
    public const string TextFamily = "Segoe UI, Microsoft YaHei UI";

    /// <summary>悬浮层主曲线:进入用 easeOutCubic(快出缓停,上游 spring 的主体观感)。</summary>
    public static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double u = 1 - t;
        return 1 - u * u * u;
    }

    /// <summary>退出用 easeInQuad(先慢后快,收得干脆)。</summary>
    public static double EaseInQuad(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t;
    }

    /// <summary>轻微过冲(卡片/菜单弹出)。overshoot 调小到 1.2,只比spring的"落定"多一点点弹。</summary>
    public static double EaseOutBack(double t, double overshoot = 1.2)
    {
        t = Math.Clamp(t, 0, 1);
        double u = t - 1;
        return 1 + (overshoot + 1) * u * u * u + overshoot * u * u;
    }

    /// <summary>动画时长(毫秒)。取值对标上游:开合 spring(0.32s)、卡片行 0.14s、菜单淡入取中间值。</summary>
    public static class Dur
    {
        public const int PeekInMs = 320;
        public const int PeekOutMs = 260;
        public const int CardInMs = 160;
        public const int MenuInMs = 110;
        public const int HaloMs = 140;
    }

    /// <summary>
    /// 悬浮层外阴影规格(自绘,不用 BlurEffect——分层窗口实时模糊太贵)。
    /// 三层圆角矩形逼近一次柔和投影:近层定形、中层过渡、远层铺开。
    /// alpha 是各层相对面板黑的不透明度;扩边是阴影相对形状的外扩 diu。
    /// </summary>
    public static class Shadow
    {
        /// <summary>三层 {下移, 外扩, alpha}</summary>
        public static readonly (double OffsetY, double Spread, byte Alpha)[] Panel =
        {
            (1.0, 2.0, 0x56),
            (3.0, 6.0, 0x30),
            (7.0, 14.0, 0x1C),
        };

        /// <summary>详情卡比 rail 浮空更高,阴影更远一点。</summary>
        public static readonly (double OffsetY, double Spread, byte Alpha)[] Card =
        {
            (2.0, 3.0, 0x59),
            (5.0, 9.0, 0x34),
            (11.0, 20.0, 0x1E),
        };
    }
}

/// <summary>
/// 工具窗(设置/统计/会话)的亮色主题,对齐上游设置窗的 mac 原生观感:
/// 白底、灰分组、黑标题、蓝控件。XAML 侧同款值在 Themes\Light.xaml,两边不要漂。
/// </summary>
public static class LightPalette
{
    /// <summary>内容区白。</summary>
    public static readonly Color WindowBack = Color.FromRgb(0xFF, 0xFF, 0xFF);
    /// <summary>侧栏浅灰。</summary>
    public static readonly Color SidebarBack = Color.FromRgb(0xF5, 0xF5, 0xF7);
    /// <summary>正文黑。</summary>
    public static readonly Color Text = Color.FromRgb(0x1D, 0x1D, 0x1F);
    /// <summary>行副标题/说明灰。</summary>
    public static readonly Color TextSub = Color.FromRgb(0x86, 0x86, 0x8B);
    /// <summary>侧栏分组标签灰。</summary>
    public static readonly Color TextGroup = Color.FromRgb(0x8E, 0x8E, 0x93);
    /// <summary>分组卡描边。</summary>
    public static readonly Color CardBorder = Color.FromRgb(0xE3, 0xE3, 0xE6);
    /// <summary>hairline 分隔线。</summary>
    public static readonly Color Separator = Color.FromRgb(0xE8, 0xE8, 0xEC);
    /// <summary>控件蓝(选中/hover/主按钮)。</summary>
    public static readonly Color Accent = Color.FromRgb(0x00, 0x7A, 0xFF);
    /// <summary>控件蓝的深一档(按压)。</summary>
    public static readonly Color AccentPress = Color.FromRgb(0x00, 0x64, 0xD2);
    /// <summary>导航行选中灰(内容区打开的是别的页时)。</summary>
    public static readonly Color NavHover = Color.FromRgb(0xE9, 0xE9, 0xEC);
    /// <summary>输入框底/分段控件轨道。</summary>
    public static readonly Color FieldBack = Color.FromRgb(0xF0, 0xF0, 0xF2);
    /// <summary>输入框边。</summary>
    public static readonly Color FieldBorder = Color.FromRgb(0xD8, 0xD8, 0xDC);
}

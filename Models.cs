using System.Windows.Media;

namespace PulseWin;

/// <summary>颜色语言:80% 以内正常绿,超过 80% 报警红,封顶深红。</summary>
public static class UsageTint
{
    public static readonly Color Good = Color.FromRgb(0x00, 0xE6, 0x8C);
    public static readonly Color Alarm = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color Exhausted = Color.FromRgb(0xD9, 0x17, 0x21);

    public const double AlarmThreshold = 0.80;

    public static Color For(double fraction, bool isSpent)
    {
        if (isSpent || fraction >= 1) return Exhausted;
        if (fraction >= AlarmThreshold) return Alarm;
        return Good;
    }
}

/// <summary>黑色玻璃面上的可读色(不透明黑底,白色前景)。</summary>
public static class PanelPalette
{
    /// 80% 不透明的深黑玻璃(桌面透出约 20%)。
    public static readonly Color Surface = Color.FromArgb(0xCC, 0x0A, 0x0A, 0x0E);
    /// 18% 白轨道。
    public static readonly Color Track = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    /// 前景白。
    public static readonly Color Primary = Color.FromRgb(0xF5, 0xF5, 0xF7);
    /// 55% 白(次要文字)。
    public static readonly Color Dim = Color.FromArgb(0x8C, 0xF5, 0xF5, 0xF7);
    /// 更淡(组名/辅助)。
    public static readonly Color Faint = Color.FromArgb(0x59, 0xF5, 0xF5, 0xF7);
}

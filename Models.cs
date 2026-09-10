using System.Windows.Media;

namespace PulseWin;

/// <summary>颜色语言:阈值以内正常绿,超过阈值报警红,封顶深红。阈值可在设置里改。</summary>
public static class UsageTint
{
    public static readonly Color Good = Color.FromRgb(0x00, 0xE6, 0x8C);
    public static readonly Color Alarm = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color Exhausted = Color.FromRgb(0xD9, 0x17, 0x21);

    /// <summary>报警阈值(0~1),来自用户设置,默认 0.80。</summary>
    public static double AlarmThreshold => AppSettings.Current.AlertThreshold;

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
    /// <summary>深黑玻璃。不透明度来自设置(默认 80%,桌面透出约 20%)。</summary>
    public static Color Surface => WithOpacity(0x0A, 0x0A, 0x0E, AppSettings.Current.SurfaceOpacity);

    private static Color WithOpacity(byte r, byte g, byte b, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), r, g, b);

    /// <summary>18% 白轨道。</summary>
    public static readonly Color Track = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    /// <summary>前景白。</summary>
    public static readonly Color Primary = Color.FromRgb(0xF5, 0xF5, 0xF7);
    /// <summary>55% 白(次要文字)。</summary>
    public static readonly Color Dim = Color.FromArgb(0x8C, 0xF5, 0xF5, 0xF7);
    /// <summary>更淡(组名/辅助)。</summary>
    public static readonly Color Faint = Color.FromArgb(0x59, 0xF5, 0xF5, 0xF7);
}

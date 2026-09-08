using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseWin;

/// <summary>品牌图标:GOAT 用官方彩色羊 logo(来自 GOAT-Go-Usage-Monitor),GO 用单色方块 logo。</summary>
public static class Icons
{
    private static BitmapSource? _goat;
    private static Geometry? _opencodeOutline;

    public static BitmapSource? GoatBitmap()
    {
        if (_goat is not null) return _goat;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/goat-white.png");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;
            using (stream)
            {
                var decoder = PngBitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var src = decoder.Frames[0];
                // 预缩到环心显示尺寸(14pt≈19diu,给 2x 余量),避免每次绘制缩放大图
                double scale = 40.0 / src.PixelWidth;
                _goat = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                _goat.Freeze();
            }
        }
        catch (Exception) { _goat = null; }
        return _goat;
    }

    /// <summary>opencode 方块 logo(24 viewBox 外框+内方,evenodd 挖成"回"形)。</summary>
    public static Geometry OpenCodeGeometry()
    {
        if (_opencodeOutline is null)
        {
            var p = Geometry.Parse("M16 6H8v12h8V6zM4 2h16v20H4z");
            p.Freeze();
            _opencodeOutline = p;
        }
        return _opencodeOutline;
    }
}

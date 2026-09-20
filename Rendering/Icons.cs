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
    private static Geometry? _deepSeekOutline;

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

    /// <summary>
    /// DeepSeek 官方鲸鱼 logo(24 viewBox,evenodd 挖空出细节)。
    /// 取自 mac 版 Pulse 仓库的 deepseek.svg;原文件用了 SVG 的紧凑圆弧写法
    /// (两个 flag 连写,如 <c>a5.5 5.5 0 01-1.7-1.1</c>),WPF 的解析器不认,
    /// 已逐个拆开并空格分隔。单色填充——环心用白色画在黑色玻璃上。
    /// </summary>
    public static Geometry DeepSeekGeometry()
    {
        if (_deepSeekOutline is null)
        {
            var g = Geometry.Parse("F0 M 23.748 4.482 c -.254 -.124 -.364 .113 -.512 .234 -.051 .039 -.094 .09 -.137 .136 -.372 .397 -.806 .657 -1.373 .626 -.829 -.046 -1.537 .214 -2.163 .848 -.133 -.782 -.575 -1.248 -1.247 -1.548 -.352 -.156 -.708 -.311 -.955 -.65 -.172 -.241 -.219 -.51 -.305 -.774 -.055 -.16 -.11 -.323 -.293 -.35 -.2 -.031 -.278 .136 -.356 .276 -.313 .572 -.434 1.202 -.422 1.84 .027 1.436 .633 2.58 1.838 3.393 .137 .093 .172 .187 .129 .323 -.082 .28 -.18 .552 -.266 .833 -.055 .179 -.137 .217 -.329 .14 a 5.526 5.526 0 0 1 -1.736 -1.18 c -.857 -.828 -1.631 -1.742 -2.597 -2.458 a 11.365 11.365 0 0 0 -.689 -.471 c -.985 -.957 .13 -1.743 .388 -1.836 .27 -.098 .093 -.432 -.779 -.428 -.872 .004 -1.67 .295 -2.687 .684 a 3.055 3.055 0 0 1 -.465 .137 9.597 9.597 0 0 0 -2.883 -.102 c -1.885 .21 -3.39 1.102 -4.497 2.623 C .082 8.606 -.231 10.684 .152 12.85 c .403 2.284 1.569 4.175 3.36 5.653 1.858 1.533 3.997 2.284 6.438 2.14 1.482 -.085 3.133 -.284 4.994 -1.86 .47 .234 .962 .327 1.78 .397 .63 .059 1.236 -.03 1.705 -.128 .735 -.156 .684 -.837 .419 -.961 -2.155 -1.004 -1.682 -.595 -2.113 -.926 1.096 -1.296 2.746 -2.642 3.392 -7.003 .05 -.347 .007 -.565 0 -.845 -.004 -.17 .035 -.237 .23 -.256 a 4.173 4.173 0 0 0 1.545 -.475 c 1.396 -.763 1.96 -2.015 2.093 -3.517 .02 -.23 -.004 -.467 -.247 -.588 z M 11.581 18 c -2.089 -1.642 -3.102 -2.183 -3.52 -2.16 -.392 .024 -.321 .471 -.235 .763 .09 .288 .207 .486 .371 .739 .114 .167 .192 .416 -.113 .603 -.673 .416 -1.842 -.14 -1.897 -.167 -1.361 -.802 -2.5 -1.86 -3.301 -3.307 -.774 -1.393 -1.224 -2.887 -1.298 -4.482 -.02 -.386 .093 -.522 .477 -.592 a 4.696 4.696 0 0 1 1.529 -.039 c 2.132 .312 3.946 1.265 5.468 2.774 .868 .86 1.525 1.887 2.202 2.891 .72 1.066 1.494 2.082 2.48 2.914 .348 .292 .625 .514 .891 .677 -.802 .09 -2.14 .11 -3.054 -.614 z m 1 -6.44 a .306 .306 0 0 1 .415 -.287 .302 .302 0 0 1 .2 .288 .306 .306 0 0 1 -.31 .307 .303 .303 0 0 1 -.304 -.308 z m 3.11 1.596 c -.2 .081 -.399 .151 -.59 .16 a 1.245 1.245 0 0 1 -.798 -.254 c -.274 -.23 -.47 -.358 -.552 -.758 a 1.73 1.73 0 0 1 .016 -.588 c .07 -.327 -.008 -.537 -.239 -.727 -.187 -.156 -.426 -.199 -.688 -.199 a .559 .559 0 0 1 -.254 -.078 c -.11 -.054 -.2 -.19 -.114 -.358 .028 -.054 .16 -.186 .192 -.21 .356 -.202 .767 -.136 1.146 .016 .352 .144 .618 .408 1.001 .782 .391 .451 .462 .576 .685 .914 .176 .265 .336 .537 .445 .848 .067 .195 -.019 .354 -.25 .452 z");
            g.Freeze();
            _deepSeekOutline = g;
        }
        return _deepSeekOutline;
    }
}

using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 用户可调项(落盘 Data\settings.json)。以前报警阈值/同步间隔都是编译期常量,
/// 设置窗口只能填 API Key——开放这几项后不用改代码重发就能调整观感。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>用量报警阈值(0~1)。超过即转红。</summary>
    public double AlertThreshold { get; set; } = 0.80;

    /// <summary>同步成功后的等待间隔(秒)。</summary>
    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>rail 背景不透明度(0~1)。</summary>
    public double SurfaceOpacity { get; set; } = 0.80;

    private static AppSettings? _current;
    private static AppDataPaths? _paths;

    public static AppSettings Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (Gate) return _current ??= Load();
        }
    }

    public static void Attach(AppDataPaths paths) => _paths = paths;

    private static AppSettings Load()
    {
        try
        {
            if (_paths is { } p && File.Exists(p.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(p.SettingsFile), JsonOptions);
                if (loaded is not null) return loaded.Sanitized();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取设置失败,使用默认值", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        var clean = Sanitized();
        AlertThreshold = clean.AlertThreshold;
        SyncIntervalSeconds = clean.SyncIntervalSeconds;
        SurfaceOpacity = clean.SurfaceOpacity;
        try
        {
            if (_paths is { } p)
                AtomicFile.WriteText(p.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("保存设置失败", ex);
        }
    }

    private AppSettings Sanitized() => new()
    {
        AlertThreshold = Math.Clamp(AlertThreshold, 0.5, 0.99),
        SyncIntervalSeconds = Math.Clamp(SyncIntervalSeconds, 15, 600),
        SurfaceOpacity = Math.Clamp(SurfaceOpacity, 0.3, 1.0),
    };

    /// <summary>把设置回写成当前值的副本(供 UI 显示)。</summary>
    public AppSettings Clone() => new()
    {
        AlertThreshold = AlertThreshold,
        SyncIntervalSeconds = SyncIntervalSeconds,
        SurfaceOpacity = SurfaceOpacity,
    };
}

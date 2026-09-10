using System.IO;

namespace PulseWin;

/// <summary>
/// 便携数据目录:默认在 exe 同目录的 Data\ 下(用户可能删除旧工具目录);
/// 目录不可写时回退 %LOCALAPPDATA%\PulseWin。文件名与 GOAT-Go-Usage-Monitor
/// 保持一致,凭据 DPAPI entropy 也一致——旧目录数据可直接迁移使用。
/// </summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string? rootOverride = null)
    {
        string requested = Path.GetFullPath(rootOverride
            ?? Path.Combine(AppContext.BaseDirectory, "Data"));
        try
        {
            Directory.CreateDirectory(requested);
            string probe = Path.Combine(requested, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            RootDirectory = requested;
        }
        catch
        {
            RootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PulseWin");
            Directory.CreateDirectory(RootDirectory);
        }
    }

    public string RootDirectory { get; }

    /// <summary>旧 GOAT-Go-Usage-Monitor 的数据目录(首次启动迁移源)。</summary>
    public static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CommandCodeGOATMonitor");

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string SnapshotFile => Path.Combine(RootDirectory, "snapshot.json");
    public string OpenCodeSnapshotFile => Path.Combine(RootDirectory, "opencode-go-snapshot.json");
    public string CredentialFile => Path.Combine(RootDirectory, "credential.bin");
    public string OpenCodeCredentialFile => Path.Combine(RootDirectory, "opencode-go-credential.bin");
    public string DiagnosticsFile => Path.Combine(RootDirectory, "diagnostics.json");

    /// <summary>首次启动:把旧目录的快照/加密凭据/设置复制过来(不删除旧目录)。</summary>
    public void MigrateFromLegacy()
    {
        try
        {
            string legacy = LegacyDirectory;
            if (!Directory.Exists(legacy)) return;
            foreach (var (file, target) in new[]
            {
                ("snapshot.json", SnapshotFile),
                ("opencode-go-snapshot.json", OpenCodeSnapshotFile),
                ("credential.bin", CredentialFile),
                ("opencode-go-credential.bin", OpenCodeCredentialFile),
                ("settings.json", SettingsFile),
            })
            {
                string src = Path.Combine(legacy, file);
                if (File.Exists(src) && !File.Exists(target))
                    File.Copy(src, target, overwrite: false);
            }
        }
        catch
        {
            // 迁移失败不致命:可重新配置凭据
        }
    }
}

internal static class AtomicFile
{
    /// <summary>
    /// 先写 .tmp 再 Move。临时名带进程号:多个实例(或旧版 monitor)同时落盘时,
    /// 共用同一个 .tmp 会互相踩,Move 抛 IOException 把快照写坏。
    /// </summary>
    private static string TempPath(string path) =>
        $"{path}.{Environment.ProcessId}.tmp";

    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = TempPath(path);
        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    public static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = TempPath(path);
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, true);
    }

    public static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = TempPath(path);
        File.WriteAllBytes(temporaryPath, content);
        File.Move(temporaryPath, path, true);
    }
}

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
    public string DeepSeekSnapshotFile => Path.Combine(RootDirectory, "deepseek-snapshot.json");
    public string CredentialFile => Path.Combine(RootDirectory, "credential.bin");
    public string OpenCodeCredentialFile => Path.Combine(RootDirectory, "opencode-go-credential.bin");
    public string DeepSeekCredentialFile => Path.Combine(RootDirectory, "deepseek-credential.bin");

    /// <summary>OpenCode Go 附加账号清单(DPAPI 整体加密;主账号沿用 OpenCodeCredentialFile)。</summary>
    public string OpenCodeAccountsFile => Path.Combine(RootDirectory, "opencode-go-accounts.bin");

    /// <summary>
    /// OpenCode Go 某个账号实例的快照文件。主实例(1)沿用历史文件名,附加实例带
    /// 编号后缀——快照按账号分开落盘,rail 上的每个环才能各自读取各自的额度。
    /// </summary>
    public string OpenCodeSnapshotFileFor(int instance) => instance <= 1
        ? OpenCodeSnapshotFile
        : Path.Combine(RootDirectory, $"opencode-go-snapshot-{instance}.json");

    /// <summary>
    /// OpenCode Go 某个账号的**本账期累计量**(按服务端逐请求明细增量累加,见
    /// <see cref="OpenCodePeriodUsage"/>)。每个账号一份:账期起点不同,不能混。
    /// </summary>
    public string OpenCodePeriodUsageFileFor(int instance) => Path.Combine(
        RootDirectory, instance <= 1
            ? "opencode-period-usage.json"
            : $"opencode-period-usage-{instance}.json");

    /// <summary>
    /// 磁盘上实际存在的附加实例快照:(实例号, 文件路径)。附加账号删除后快照会被
    /// 引擎删掉,但万一残留(进程中断),调用方还要拿设置里的账号清单再筛一遍。
    /// </summary>
    public IEnumerable<(int Instance, string File)> OpenCodeInstanceSnapshots()
    {
        if (!Directory.Exists(RootDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "opencode-go-snapshot-*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string suffix = name["opencode-go-snapshot-".Length..];
            if (int.TryParse(suffix, out int instance) && instance >= 2)
                yield return (instance, file);
        }
    }

    /// <summary>DeepSeek 余额的观测峰值(每币种一份),sinceTopUp 模式的分母来源。</summary>
    public string DeepSeekBaselineFile => Path.Combine(RootDirectory, "deepseek-baseline.json");

    /// <summary>DeepSeek 余额的按小时采样账本,"今日消耗"柱状图的数据来源。</summary>
    public string DeepSeekLedgerFile => Path.Combine(RootDirectory, "deepseek-ledger.json");
    public string DiagnosticsFile => Path.Combine(RootDirectory, "diagnostics.json");

    /// <summary>模型牌价缓存(从 models.dev 拉取后裁剪;不存在时用内置表)。</summary>
    public string ModelPricesFile => Path.Combine(RootDirectory, "model-prices.json");

    /// <summary>对账历史(JSONL,每次渲染「官方 vs 本机」对照卡追加一条)。</summary>
    public string ReconcileFile => Path.Combine(RootDirectory, "reconcile.jsonl");

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
    /// 先写 .tmp 再 Move。临时名带进程号 + 线程号:进程号防多个实例(或旧版 monitor)
    /// 同时落盘,线程号防**本进程内**并发写同一目标——Diagnostics 的落盘在锁外合并写
    /// (见它的说明),两个线程同时过 300ms 门槛就会并写 diagnostics.json,共用同一个
    /// .tmp 会互相踩,Move 抛 IOException 把快照写坏。
    /// </summary>
    private static string TempPath(string path) =>
        $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";

    /// <summary>
    /// 与原子替换共存地读:允许别的线程/进程正把新文件 Move 上来。
    /// 普通 <c>File.ReadAllText</c> 只申请 Read 共享,碰上 MoveFileEx 替换目标就会抛
    /// 共享冲突——上层把它当成"该源本轮同步失败"(状态变 Stale、卡片红字),而数据
    /// 其实已经取到了。用 ReadWrite|Delete 打开即可;代价是可能读到替换前的那一版,
    /// 对快照来说完全可接受。
    /// </summary>
    public static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Move 撞上读句柄时重试几次;仍失败才上抛(临时文件由 finally 清理)。</summary>
    private static void MoveWithRetry(string temporaryPath, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(30);
            }
        }
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = TempPath(path);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            MoveWithRetry(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = TempPath(path);
        try
        {
            File.WriteAllText(temporaryPath, content);
            MoveWithRetry(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = TempPath(path);
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            MoveWithRetry(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

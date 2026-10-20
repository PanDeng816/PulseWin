using System.IO;

namespace PulseWin;

/// <summary>
/// 轻量诊断日志(移植自 GOAT-Go-Usage-Monitor 的做法)。同步失败、快照读写异常
/// 都记到 Data\diagnostics.json,否则这些故障只表现为"界面没数据",无从排查。
/// 只保留最近若干条,不无限增长。
///
/// **落盘是合并写(300ms)**:故障风暴时每条 Note 都整文件重写会把锁持有时间拉长,
/// 而写盘发生在 UI 线程与后台线程两侧,曾出现"把某一轮同步拖到 2 分钟 watchdog"的
/// 放大效应。现在锁内只改内存并取快照,写盘在锁外、且 300ms 内只写一次。
/// 进程退出前调用 <see cref="FlushNow"/> 保证尾部日志不丢。
/// </summary>
public static class Diagnostics
{
    private const int MaxEntries = 100;
    private const int FlushIntervalMs = 300;

    private static readonly object Gate = new();
    private static readonly List<string> Entries = new();
    private static AppDataPaths? _paths;
    private static DateTime _lastFlushUtc = DateTime.MinValue;

    /// <summary>由 UsageEngine 在构造时提供数据目录;未初始化时退化为不落盘。</summary>
    public static void Attach(AppDataPaths paths) => _paths = paths;

    public static void Note(string message)
    {
        string line = $"{DateTimeOffset.Now:MM-dd HH:mm:ss} {message}";
        string? snapshot = null;
        lock (Gate)
        {
            Entries.Add(line);
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(0, Entries.Count - MaxEntries);

            if ((DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds >= FlushIntervalMs)
            {
                _lastFlushUtc = DateTime.UtcNow;
                snapshot = string.Join(Environment.NewLine, Entries);
            }
        }
        if (snapshot is not null) WriteSnapshot(snapshot);
    }

    /// <summary>把异常记成一行(类型 + 消息 + 内层异常的第一个非空消息)。</summary>
    public static void Note(string context, Exception ex) =>
        Note($"{context}: {ex.GetType().Name} {ex.Message}" +
             (ex.InnerException is { } inner ? $" <- {inner.GetType().Name} {inner.Message}" : ""));

    public static IReadOnlyList<string> Recent()
    {
        lock (Gate) return Entries.ToArray();
    }

    /// <summary>立即落盘(进程退出前调用,补上合并窗口里没写的那几条)。</summary>
    public static void FlushNow()
    {
        string snapshot;
        lock (Gate)
        {
            _lastFlushUtc = DateTime.UtcNow;
            snapshot = string.Join(Environment.NewLine, Entries);
        }
        WriteSnapshot(snapshot);
    }

    private static void WriteSnapshot(string snapshot)
    {
        if (_paths is not { } paths) return;
        try
        {
            AtomicFile.WriteText(paths.DiagnosticsFile, snapshot);
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }
}

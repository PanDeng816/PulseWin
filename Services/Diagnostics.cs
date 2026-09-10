using System.IO;

namespace PulseWin;

/// <summary>
/// 轻量诊断日志(移植自 GOAT-Go-Usage-Monitor 的做法)。同步失败、快照读写异常
/// 都记到 Data\diagnostics.json,否则这些故障只表现为"界面没数据",无从排查。
/// 只保留最近若干条,不无限增长。
/// </summary>
public static class Diagnostics
{
    private const int MaxEntries = 100;
    private static readonly object Gate = new();
    private static readonly List<string> Entries = new();
    private static AppDataPaths? _paths;

    /// <summary>由 UsageEngine 在构造时提供数据目录;未初始化时退化为不落盘。</summary>
    public static void Attach(AppDataPaths paths) => _paths = paths;

    public static void Note(string message)
    {
        string line = $"{DateTimeOffset.Now:MM-dd HH:mm:ss} {message}";
        lock (Gate)
        {
            Entries.Add(line);
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            Flush();
        }
    }

    /// <summary>把异常记成一行(类型 + 消息 + 内层异常的第一个非空消息)。</summary>
    public static void Note(string context, Exception ex) =>
        Note($"{context}: {ex.GetType().Name} {ex.Message}" +
             (ex.InnerException is { } inner ? $" <- {inner.GetType().Name} {inner.Message}" : ""));

    public static IReadOnlyList<string> Recent()
    {
        lock (Gate) return Entries.ToArray();
    }

    private static void Flush()
    {
        if (_paths is not { } paths) return;
        try
        {
            AtomicFile.WriteText(paths.DiagnosticsFile, string.Join(Environment.NewLine, Entries));
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }
}

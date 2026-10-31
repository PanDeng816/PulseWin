using System.IO;
using System.Text;

namespace PulseWin;

/// <summary>
/// DSH 会话解析结果的**落盘缓存**(<c>Data\dsh-parse-cache.bin</c>)。
///
/// **为什么需要**:<see cref="DshUsageStore"/> 的内存缓存只救得了"程序还开着"的那段时间。
/// 会话文件是 zstd 整份压缩的,冷启动要把 688 个文件、215 MB 全部解压再逐行扫一遍——
/// 本机实测 5~9 秒,占整个账本构建的 95%。而这些文件里 98% 是历史会话,内容**永远不变**,
/// 每次开机都重算一遍纯属白烧 CPU。
///
/// **为什么是二进制而不是 JSON**:这是 29,000 条记录 × 20 个字段的机械结构,JSON 序列化
/// 光反射就一两百毫秒;手写 BinaryWriter 几十毫秒,文件也只有一半大。格式内部私有、
/// 坏了就整份丢弃重建,不需要可读性。
///
/// **两条失效规矩**:
/// 1. <see cref="ParserVersion"/>——改了 <see cref="DshUsageStore"/> 的解析口径就 +1,
///    旧缓存整份作废。不加这个,口径修好之后界面上的数字还是旧缓存算出来的,而且
///    "怎么改都不变"会被当成没生效(项目里踩过"缓存固化旧结果"的坑)。
/// 2. 每个条目都带 (长度, 最后修改时间)——活动会话一追加就自动失效,只重解析那一个。
///
/// **写坏了不算事故**:读失败一律吞掉返回空表,下一次 Read 会全量解析并重写。
/// </summary>
internal static class DshParseCache
{
    /// <summary>文件头魔数,防止把别的文件(或半截文件)当成缓存读。</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PWDSPC01");

    /// <summary>容器格式版本。改布局(字段增删/换序)时 +1。</summary>
    private const int FormatVersion = 1;

    /// <summary>
    /// 解析器版本。**改了 <see cref="DshUsageStore"/> 里 Parse 的口径就 +1**,
    /// 旧缓存整份作废,免得用户看到的是旧口径算出来的数。
    /// </summary>
    private const int ParserVersion = 1;

    /// <summary>一个会话文件条目:身份 + 解析结果。</summary>
    internal sealed record Entry(string Path, long Length, long Ticks, DshUsageStore.ParsedFile Parsed);

    public static string Location => Path.Combine(
        SnapshotSource.DataPaths.RootDirectory, "dsh-parse-cache.bin");

    /// <summary>
    /// 读回全部条目。文件不存在/格式不符/版本不符/内容损坏都返回空表(不是错误——
    /// 下一次 Read 会全量解析并重写)。
    /// </summary>
    public static List<Entry> Load()
    {
        var result = new List<Entry>();
        string path = Location;
        if (!File.Exists(path)) return result;

        // 共享位放宽到 ReadWrite|Delete:写缓存用的是"临时文件 + 原子替换",
        // 独占读会让另一个进程(诊断模式 --dsh/--spend)的替换失败。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        byte[] magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic)) return result;
        if (reader.ReadInt32() != FormatVersion) return result;
        if (reader.ReadInt32() != ParserVersion) return result;

        int count = reader.ReadInt32();
        // 防呆:缓存里最多也就几千个会话,超了必然是文件被写坏
        if (count < 0 || count > 100_000) return result;

        for (int i = 0; i < count; i++)
        {
            string filePath = reader.ReadString();
            long length = reader.ReadInt64();
            long ticks = reader.ReadInt64();

            string sessionId = reader.ReadString();
            string? project = ReadNullable(reader);
            string? title = ReadNullable(reader);
            bool isSubagent = reader.ReadBoolean();

            int childCount = reader.ReadInt32();
            if (childCount < 0 || childCount > 100_000) return new List<Entry>();
            var children = new List<string>(childCount);
            for (int c = 0; c < childCount; c++) children.Add(reader.ReadString());

            int recordCount = reader.ReadInt32();
            if (recordCount < 0 || recordCount > 1_000_000) return new List<Entry>();
            var records = new List<AgentUsageRecord>(recordCount);
            for (int r = 0; r < recordCount; r++)
            {
                long timeTicks = reader.ReadInt64();
                string model = reader.ReadString();
                long input = reader.ReadInt64();
                long cacheWrite = reader.ReadInt64();
                long cacheRead = reader.ReadInt64();
                long output = reader.ReadInt64();
                long unclassified = reader.ReadInt64();
                string? provider = ReadNullable(reader);
                string? recordSession = ReadNullable(reader);
                string? recordProject = ReadNullable(reader);
                string? recordTitle = ReadNullable(reader);
                long reasoning = reader.ReadInt64();
                long durationMs = reader.ReadInt64();
                long ttftMs = reader.ReadInt64();
                long tools = reader.ReadInt64();
                long retries = reader.ReadInt64();
                long failures = reader.ReadInt64();
                string? sourceSession = ReadNullable(reader);

                records.Add(new AgentUsageRecord(
                    new DateTime(timeTicks, DateTimeKind.Local),
                    model,
                    new TokenTally(input, cacheWrite, cacheRead, output),
                    unclassified,
                    DshUsageStore.AgentName,
                    provider,
                    recordSession,
                    recordProject,
                    recordTitle,
                    ReasoningTokens: reasoning,
                    DurationMs: durationMs,
                    TimeToFirstTokenMs: ttftMs,
                    ToolCalls: tools,
                    Retries: retries,
                    Failures: failures,
                    SourceSessionId: sourceSession));
            }

            // 只缓存"完整解析完"的会话,读回来 Truncated 恒为 false
            result.Add(new Entry(filePath, length, ticks,
                new DshUsageStore.ParsedFile(sessionId, project, title, isSubagent, children, records, false)));
        }
        return result;
    }

    /// <summary>
    /// 整份重写。先写 <c>.tmp</c> 再原子替换——写到一半断电也不会留下半截缓存,
    /// 最坏情况是替换没发生、旧缓存还在(下一次 Read 会再写一遍)。
    /// </summary>
    public static void Save(IReadOnlyList<Entry> entries)
    {
        string target = Location;
        string? directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = target + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                   FileShare.None, 1 << 16))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(ParserVersion);
            writer.Write(entries.Count);

            foreach (var entry in entries)
            {
                writer.Write(entry.Path);
                writer.Write(entry.Length);
                writer.Write(entry.Ticks);

                var parsed = entry.Parsed;
                writer.Write(parsed.SessionId);
                WriteNullable(writer, parsed.Project);
                WriteNullable(writer, parsed.Title);
                writer.Write(parsed.IsSubagent);

                writer.Write(parsed.ChildSessionIds.Count);
                foreach (string child in parsed.ChildSessionIds) writer.Write(child);

                writer.Write(parsed.Records.Count);
                foreach (var record in parsed.Records)
                {
                    writer.Write(record.Timestamp.Ticks);
                    writer.Write(record.Model);
                    writer.Write(record.Tally.Input);
                    writer.Write(record.Tally.CacheWrite);
                    writer.Write(record.Tally.CacheRead);
                    writer.Write(record.Tally.Output);
                    writer.Write(record.UnclassifiedTokens);
                    WriteNullable(writer, record.ProviderId);
                    WriteNullable(writer, record.SessionId);
                    WriteNullable(writer, record.Project);
                    WriteNullable(writer, record.Title);
                    writer.Write(record.ReasoningTokens);
                    writer.Write(record.DurationMs);
                    writer.Write(record.TimeToFirstTokenMs);
                    writer.Write(record.ToolCalls);
                    writer.Write(record.Retries);
                    writer.Write(record.Failures);
                    WriteNullable(writer, record.SourceSessionId);
                }
            }
        }
        File.Move(temp, target, overwrite: true);
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    private static string? ReadNullable(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;
}

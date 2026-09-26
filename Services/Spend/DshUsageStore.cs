using System.IO;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace PulseWin;

/// <summary>
/// DSH(DeepSeek Harness)桌面版的用量来源。
///
/// **数据在哪**:<c>~\.dsh\sessions\&lt;工作区&gt;\&lt;会话&gt;\session.v4.jsonl.zstd</c>——
/// 事件流(jsonl)整份被 zstd 压缩。一条 <c>assistant/message</c> 事件 = 一个"步"
/// (一次模型调用),自带 <c>usage</c>(inputTokens / outputTokens / cacheReadTokens /
/// totalTokens)与 <c>message.source</c>(provider / model);另有 <c>session</c> 行给会话号
/// 与工作目录、<c>session/title</c> 给会话标题。<c>step/start</c> 的 <c>time</c> 是
/// 请求发出时刻,配上 message 的 <c>time</c> 与首个流式 chunk 的 <c>time</c> 就能算出
/// 耗时与首字延迟。**这是 DSH 唯一的本机账单来源**:它不写 SQLite,也没有别的明细表。
///
/// **口径(先记牢再改)**:这里的 <c>inputTokens</c> 是**未命中缓存的输入**
/// (DSH 自己的会话投影里就叫 uncachedInputTokens),<c>cacheReadTokens</c> 单列、
/// <c>cacheWriteTokens</c> 恒为 0;实测 <c>totalTokens</c> ≡ input + output + cacheRead
/// (本机 1047 步无一例外)。所以四类**直接取用,不要做减法**——这点与
/// <see cref="ZCodeUsageStore"/> 恰好相反(ZCode 的 input_tokens 含缓存,那边必须减掉重叠)。
///
/// **两处必须小心的地方**:
/// 1. zstd 是**流式**压缩,而 DSH 一直在往活动会话里追加。读到半个帧会抛异常,
///    这时**保住已经解出来的部分**(截掉的只是尾部那几条),下一轮长度变了会重读补上;
///    绝不能因为一次截断就把整个会话丢掉。
/// 2. 会话标题**首轮之后**才生成,通常排在若干 message 之后。所以会话号、工作目录、
///    标题一律**整份解析完再回填**给每一条记录——边读边填会让前面的记录缺标题。
///
/// **为什么带文件级缓存**:解析一份会话 = 解压 + 逐行扫(本机 23 个会话全量约 0.7 秒,
/// 而 tool/result 行动辄几百 KB)。用量窗每次打开都会重建账本,可历史会话的内容永远
/// 不变——按 (长度, 修改时间) 命中就复用,只重读还在写的那个。会话数只增不减,没有这份
/// 缓存的话一个季度之后每次打开用量窗都要等十几秒,所以它**跨账本存活**(不随
/// <see cref="SpendLedgerCache"/> 释放),改由这里的硬上限控制内存:超出就按最近使用
/// 时间淘汰(见 <see cref="MaxCachedFiles"/> / <see cref="MaxCachedRecords"/>)。
/// </summary>
public sealed class DshUsageStore : IUsageStore
{
    /// <summary>来源显示名。**同时是账本里的 Agent 列**("ZCode"/"OpenCode"/"DSH" 三者并列)。</summary>
    public const string AgentName = "DSH";

    public string Name => AgentName;

    public string Location => RootDirectory;

    public bool IsPresent => Directory.Exists(RootDirectory);

    /// <summary>DSH 的会话根目录(诊断与界面都要显示它)。</summary>
    public static string RootDirectory => Path.Combine(UsageStoreSupport.HomeDirectory, ".dsh", "sessions");

    /// <summary>文件名带格式版本(v4),通配匹配是为了将来 v5 也能进来而不是静默漏掉。</summary>
    private const string SessionFilePattern = "session.v*.jsonl.zstd";

    /// <summary>缓存的会话文件数上限(超出按最近使用时间淘汰四分之一)。</summary>
    private const int MaxCachedFiles = 512;

    /// <summary>
    /// 缓存的记录条数上限:这份缓存常驻(见类注释),所以必须有硬上限——
    /// 12 万条约十几 MB,够覆盖上千个会话,再多就淘汰最久没被碰过的那些。
    /// </summary>
    private const long MaxCachedRecords = 120_000;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CachedFile> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static long _cachedRecords;
    private static long _cacheHits;
    private static long _cacheMisses;
    private static long _truncations;
    private static string? _lastTruncationPath;

    private sealed class CachedFile
    {
        public long Length { get; init; }
        public long LastWriteTicks { get; init; }
        public required ParsedFile Parsed { get; init; }
        public long LastUsed { get; set; }
    }

    /// <summary>一份会话解析后的产物。记录里的会话号/项目/标题都已回填好。</summary>
    private sealed record ParsedFile(List<AgentUsageRecord> Records, bool Truncated);

    /// <summary>缓存与解析的诊断计数(给 --dsh 用:命中率说明缓存到底有没有起作用)。</summary>
    public static (long Hits, long Misses, int Files, long Records, long Truncations) CacheStats
    {
        get
        {
            lock (CacheGate) return (_cacheHits, _cacheMisses, Cache.Count, _cachedRecords, _truncations);
        }
    }

    public IReadOnlyList<AgentUsageRecord> Read()
    {
        var all = new List<AgentUsageRecord>();
        foreach (var (path, sessionId) in EnumerateSessionFiles())
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) continue;
            long ticks = info.LastWriteTimeUtc.Ticks;
            long now = Environment.TickCount64;

            ParsedFile? parsed = null;
            lock (CacheGate)
            {
                if (Cache.TryGetValue(path, out var hit)
                    && hit.Length == info.Length && hit.LastWriteTicks == ticks)
                {
                    hit.LastUsed = now;
                    parsed = hit.Parsed;
                    _cacheHits++;
                }
                else
                {
                    _cacheMisses++;
                }
            }

            if (parsed is null)
            {
                parsed = Parse(path, sessionId);
                lock (CacheGate) Store(path, info.Length, ticks, parsed, now);
            }
            if (parsed.Records.Count > 0) all.AddRange(parsed.Records);
        }
        return all;
    }

    private static void Store(string path, long length, long ticks, ParsedFile parsed, long now)
    {
        if (Cache.Remove(path, out var old)) _cachedRecords -= old.Parsed.Records.Count;
        Cache[path] = new CachedFile
        {
            Length = length,
            LastWriteTicks = ticks,
            Parsed = parsed,
            LastUsed = now
        };
        _cachedRecords += parsed.Records.Count;
        PruneIfNeeded();
    }

    /// <summary>淘汰按最近使用时间:历史会话被读到一次之后就长期不再被碰,先淘汰它们。</summary>
    private static void PruneIfNeeded()
    {
        if (Cache.Count <= MaxCachedFiles && _cachedRecords <= MaxCachedRecords) return;
        foreach (string key in Cache.OrderBy(kv => kv.Value.LastUsed)
                     .Take(Math.Max(1, Cache.Count / 4))
                     .Select(kv => kv.Key).ToList())
        {
            if (Cache.Remove(key, out var gone)) _cachedRecords -= gone.Parsed.Records.Count;
        }
    }

    /// <summary>
    /// 枚举全部会话文件。目录名同时当会话号的**兜底**(正常应以 <c>session</c> 事件里的 id 为准,
    /// 但文件被截断在第一条事件之前时就只剩目录名了)。
    /// </summary>
    private static IEnumerable<(string Path, string SessionId)> EnumerateSessionFiles()
    {
        string root = RootDirectory;
        if (!Directory.Exists(root)) yield break;

        string[] workspaces;
        try { workspaces = Directory.GetDirectories(root); }
        catch (Exception ex) { Diagnostics.Note("DSH 会话目录无法枚举", ex); yield break; }

        foreach (string workspace in workspaces)
        {
            string[] sessions;
            try { sessions = Directory.GetDirectories(workspace); }
            catch { continue; }

            foreach (string session in sessions)
            {
                string[] files;
                try { files = Directory.GetFiles(session, SessionFilePattern); }
                catch { continue; }
                foreach (string file in files) yield return (file, Path.GetFileName(session));
            }
        }
    }

    /// <summary>
    /// 解析一份会话。**异常一律吞成"截断"**:活动会话随时在追加,读到半个 zstd 帧是
    /// 常态而不是故障,保住已解出的记录、记一条诊断即可(下一轮长度变了会重读)。
    /// </summary>
    private static ParsedFile Parse(string path, string sessionDirectoryName)
    {
        string? sessionId = null, project = null, title = null;
        var steps = new List<StepRecord>();
        var retries = new Dictionary<(int Turn, int Step), int>();
        var failures = new Dictionary<(int Turn, int Step), int>();
        int turn = 0, step = 0;
        long stepStart = 0;
        bool truncated = false;

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var stream = new DecompressionStream(file);
            using var reader = new StreamReader(stream, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ReadOnlySpan<char> type = EventType(line);
                if (type.IsEmpty) continue;

                if (type.SequenceEqual("assistant/message"))
                {
                    ParseMessage(line, turn, step, stepStart, retries, failures, steps);
                    continue;
                }
                if (type.SequenceEqual("step/start"))
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    long at = GetLong(root, "time");
                    if (at > 0) stepStart = at;
                    if (root.TryGetProperty("data", out var data))
                    {
                        turn = (int)GetLong(data, "turn");
                        step = (int)GetLong(data, "step");
                    }
                    continue;
                }
                if (type.SequenceEqual("llm/retry"))
                {
                    Count(retries, line);
                    continue;
                }
                if (type.SequenceEqual("assistant/attempt"))
                {
                    if (IsFailedAttempt(line)) Count(failures, line);
                    continue;
                }
                if (type.SequenceEqual("session"))
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    sessionId = Str(root, "id") ?? sessionId;
                    project = UsageStoreSupport.ProjectIdentity(Str(root, "cwd")) ?? project;
                    continue;
                }
                if (type.SequenceEqual("session/title"))
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                        title = Str(data, "title") ?? title;
                }
            }
        }
        catch (Exception ex)
        {
            truncated = true;
            NoteTruncation(path, ex);
        }

        // 回填:标题/项目/会话号都是整份走完才确定的,不能边读边填
        string id = sessionId ?? sessionDirectoryName;
        var records = new List<AgentUsageRecord>(steps.Count);
        foreach (var s in steps)
        {
            records.Add(new AgentUsageRecord(
                FromUnixMs(s.Time),
                s.Model,
                new TokenTally(s.Input, s.CacheWrite, s.CacheRead, s.Output),
                // 四类之和与来源自报总量的差:实测恒为 0,留着是为了"来源多报了"时不当成没事
                Math.Max(0L, s.Total - s.Input - s.CacheWrite - s.CacheRead - s.Output),
                AgentName,
                s.Provider,
                id,
                project,
                title,
                ReasoningTokens: 0,          // DSH 的 outputTokens 已含推理,且不单列,故不猜
                DurationMs: s.DurationMs,
                TimeToFirstTokenMs: s.TimeToFirstTokenMs,
                ToolCalls: s.Tools,
                Retries: s.Retries,
                Failures: s.Failures));
        }
        return new ParsedFile(records, truncated);
    }

    /// <summary>一步(一次模型调用)的原始数字,回填元数据之前的中间形态。</summary>
    private readonly record struct StepRecord(
        long Time, string Model, string? Provider,
        long Input, long CacheWrite, long CacheRead, long Output, long Total,
        long Tools, long DurationMs, long TimeToFirstTokenMs, long Retries, long Failures);

    private static void ParseMessage(
        string line, int turn, int step, long stepStart,
        Dictionary<(int, int), int> retries, Dictionary<(int, int), int> failures,
        List<StepRecord> steps)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data)) return;
        if (!data.TryGetProperty("usage", out var usage)) return;

        long input = GetLong(usage, "inputTokens");
        long output = GetLong(usage, "outputTokens");
        long cacheRead = GetLong(usage, "cacheReadTokens");
        long cacheWrite = GetLong(usage, "cacheWriteTokens");
        long total = GetLong(usage, "totalTokens");
        long at = GetLong(root, "time");
        if (at <= 0) return;

        string? model = null, provider = null;
        long tools = 0;
        long firstChunk = 0;
        if (data.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("source", out var source))
            {
                model = Str(source, "model");
                provider = Str(source, "provider");
            }
            tools = CountToolCalls(message);
        }
        if (data.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Array)
            firstChunk = FirstChunkTime(stream);

        steps.Add(new StepRecord(
            at, model ?? "(unknown)", provider,
            input, cacheWrite, cacheRead, output, total,
            tools,
            stepStart > 0 && at > stepStart ? at - stepStart : 0,
            stepStart > 0 && firstChunk > stepStart ? firstChunk - stepStart : 0,
            Take(retries, (turn, step)),
            Take(failures, (turn, step))));
    }

    private static void Count(Dictionary<(int, int), int> bucket, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            var key = ((int)GetLong(data, "turn"), (int)GetLong(data, "step"));
            bucket[key] = bucket.GetValueOrDefault(key) + 1;
        }
        catch (JsonException) { }
    }

    private static int Take(Dictionary<(int, int), int> bucket, (int, int) key)
    {
        if (!bucket.TryGetValue(key, out int value)) return 0;
        bucket.Remove(key);        // 取走即清:同一个 step 只算一次,后面的 message 不再重复计
        return value;
    }

    /// <summary>
    /// 这一条 attempt 是不是失败收尾。判据是流最后那个 <c>finish</c> chunk 的
    /// <c>reason.kind == "error"</c>——**不能拿整行做字符串搜索**:消息正文里出现
    /// "kind":"error" 字样会误判成失败。
    /// </summary>
    private static bool IsFailedAttempt(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
            if (!data.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in stream.EnumerateArray())
            {
                if (!item.TryGetProperty("chunk", out var chunk)) continue;
                if (Str(chunk, "type") != "finish") continue;
                if (!chunk.TryGetProperty("reason", out var reason)) continue;
                return Str(reason, "kind") == "error";
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>首个带时间戳的流式 chunk = 首字时刻(DSH 的 TTFT 就是拿它算的)。</summary>
    private static long FirstChunkTime(JsonElement stream)
    {
        foreach (var item in stream.EnumerateArray())
        {
            if (!item.TryGetProperty("chunk", out _)) continue;
            long time = GetLong(item, "time");
            if (time > 0) return time;
        }
        return 0;
    }

    /// <summary>这一条助手消息里发起了几次工具调用(是"步"的遥测,不是另算的用量)。</summary>
    private static long CountToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return 0;
        long count = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (Str(item, "type") == "tool-call") count++;
        }
        return count;
    }

    /// <summary>
    /// 取事件类型。事件行的固定开头是 <c>{"type":"xxx"</c>,所以只在**前 32 个字符**里找
    /// <c>"type":"</c>——tool/result 那种几百 KB 的行不该被整行扫一遍(全量解析 0.7 秒里
    /// 大半省在这)。
    /// </summary>
    private static ReadOnlySpan<char> EventType(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan();
        if (span.Length < 12 || span[0] != '{') return default;
        ReadOnlySpan<char> head = span[..Math.Min(span.Length, 32)];
        int at = head.IndexOf("\"type\":\"", StringComparison.Ordinal);
        if (at < 0) return default;
        int start = at + 8;
        ReadOnlySpan<char> rest = span[start..];
        int end = rest.IndexOf('"');
        return end <= 0 ? default : rest[..end];
    }

    private static void NoteTruncation(string path, Exception ex)
    {
        lock (CacheGate)
        {
            _truncations++;
            // 活动会话每次读都可能截断,别把 Diagnostics 冲爆:同一个文件只记第一次
            if (string.Equals(_lastTruncationPath, path, StringComparison.OrdinalIgnoreCase)) return;
            _lastTruncationPath = path;
        }
        Diagnostics.Note($"DSH 会话读到未写完的尾部,已保留已解析部分({Path.GetFileName(Path.GetDirectoryName(path))})", ex);
    }

    private static DateTime FromUnixMs(long milliseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime; }
        catch (ArgumentOutOfRangeException) { return DateTime.Now; }
    }

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed) ? parsed : 0;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

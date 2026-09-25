using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// ZCode 此刻是不是正在跑一轮。读的是它自己的应用日志
/// <c>~/.zcode/cli/log/zcode-&lt;日期&gt;.jsonl</c>,配对 <c>turn.started</c> 与
/// <c>turn.completed/failed</c>。
///
/// **为什么不看用量库**:ZCode 的 <c>turn_usage</c>/<c>model_usage</c> 只在回合**结束时**
/// 才落库,全库没有一行 running(实测),查不出"进行中";<c>tool_usage</c> 的 running 行
/// 倒是实时的,但崩溃残留的僵尸行能挂几天(实测见过 40 小时和 28 天的)。
///
/// **也不看文件修改时间**。"最近 N 秒有写入"两个方向都会错:慢工具跑几分钟不写一个字,
/// 而回合结束后客户端还会继续往同一份记录写记账信息(标题、模式),mtime 一直新鲜——
/// 一个已经死掉的回合会永远显示在转(上游专门踩过这个坑)。
///
/// 等待超时按"这一轮在等什么"分档(上游规格):等工具 5 分钟(编译/测试期间不写日志),
/// 等模型 90 秒(上游实测中位数 7 秒、99% 在 70 秒内)。计时一律用**记录自带的时间戳**,
/// 不用文件时间。
/// </summary>
public sealed class ZCodeActivity
{
    /// <summary>等工具:慢构建、长测试期间日志是安静的。</summary>
    private static readonly TimeSpan ToolGrace = TimeSpan.FromMinutes(5);

    /// <summary>等模型:实测中位数 7 秒,99% 在 70 秒内,90 秒足够。</summary>
    private static readonly TimeSpan ModelGrace = TimeSpan.FromSeconds(90);

    /// <summary>首次打开(或换日)时往回读多少字节来还原"此刻已经在跑的回合"。</summary>
    private const int InitialTailBytes = 1 << 20;

    /// <summary>一次最多读多少字节,防止日志被追写时一口气读进来。</summary>
    private const int MaxChunkBytes = 8 << 20;

    private readonly string _logDirectory;
    private readonly MemoryStream _pending = new();
    private readonly Dictionary<string, TurnState> _open = new(StringComparer.Ordinal);

    private string _path = "";
    private long _offset;
    private bool _dropUntilNewline;
    private DateTimeOffset? _lastMissingNote;

    public ZCodeActivity(string? logDirectory = null)
    {
        _logDirectory = logDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".zcode", "cli", "log");
    }

    /// <summary>ZCode 是否正在跑一轮。</summary>
    public bool IsWorking { get; private set; }

    /// <summary>实际读的日志目录(诊断入口要显示出来)。</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>今天那份日志的完整路径。</summary>
    public string TodayLogPath => Path.Combine(_logDirectory, $"zcode-{DateTime.Now:yyyy-MM-dd}.jsonl");

    /// <summary>上一次日志读取失败的原因(诊断用),成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前未闭合的回合数(诊断用)。</summary>
    public int OpenTurns => _open.Count;

    private sealed class TurnState
    {
        public string? LastEvent { get; set; }
        public DateTimeOffset LastAt { get; set; }
    }

    /// <summary>
    /// 读一次新增的日志并重算状态。由主循环按秒节流调用,读不到东西时开销只有一次
    /// 文件长度检查。返回状态是否发生了变化(界面据此决定要不要重画)。
    /// </summary>
    public bool Poll()
    {
        bool before = IsWorking;
        try
        {
            SwitchFileIfNeeded();
            ReadNewLines();
            IsWorking = Evaluate(DateTimeOffset.UtcNow);
            LastError = null;
        }
        catch (Exception ex)
        {
            // 日志读不了不该影响浮窗:当作"不在工作",并只在错误内容变化时记一条
            LastError = ex.Message;
            if (_lastErrorText != ex.Message)
            {
                _lastErrorText = ex.Message;
                Diagnostics.Note("读取 ZCode 活动日志失败", ex);
            }
            IsWorking = false;
        }

        if (before != IsWorking)
            Diagnostics.Note($"ZCode 活动状态: {(IsWorking ? "开始工作" : "空闲")}(未闭合回合 {_open.Count})");
        return before != IsWorking;
    }

    private string? _lastErrorText;

    /// <summary>本地日期变了就换文件;换之前先把旧文件读到尾,免得跨午夜那几条事件丢掉。</summary>
    private void SwitchFileIfNeeded()
    {
        string today = TodayLogPath;
        if (today == _path) return;

        if (_path.Length > 0) ReadNewLines();      // 旧文件读干净再走

        _path = today;
        _pending.SetLength(0);
        _dropUntilNewline = false;
        _offset = 0;

        var info = new FileInfo(today);
        if (info.Exists)
        {
            // 从尾部往前读一段,把"此刻已经在跑的那一轮"直接认出来。起点大概率落在
            // 一行中间,所以第一行丢弃(见 ReadNewLines 的 _dropUntilNewline)。
            _offset = Math.Max(0, info.Length - InitialTailBytes);
            _dropUntilNewline = _offset > 0;
        }
        else if (_lastMissingNote is null || DateTimeOffset.Now - _lastMissingNote > TimeSpan.FromHours(6))
        {
            _lastMissingNote = DateTimeOffset.Now;
            Diagnostics.Note($"ZCode 活动日志不存在({today}),活动指示按空闲处理");
        }
    }

    private void ReadNewLines()
    {
        if (_path.Length == 0) return;
        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < _offset)
        {
            // 文件被重建或轮转(长度反而变小):从头重读
            _offset = 0;
            _pending.SetLength(0);
        }
        if (stream.Length <= _offset) return;

        stream.Seek(_offset, SeekOrigin.Begin);
        int count = (int)Math.Min(stream.Length - _offset, MaxChunkBytes);
        var buffer = new byte[count];
        int read = stream.Read(buffer, 0, count);
        _offset += read;
        if (read <= 0) return;

        if (_dropUntilNewline)
        {
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline < 0) return;                 // 这一块还没有换行,整块丢掉
            _pending.Write(buffer, newline + 1, read - newline - 1);
            _dropUntilNewline = false;
        }
        else
        {
            _pending.Write(buffer, 0, read);
        }

        // 按字节找换行,只在完整的行上做 UTF-8 解码:多字节字符被块边界切开时
        // 才不会解出替换字符
        byte[] data = _pending.GetBuffer();
        int length = (int)_pending.Length;
        int start = 0;
        for (int i = 0; i < length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            HandleLine(Encoding.UTF8.GetString(data, start, i - start));
            start = i + 1;
        }
        if (start > 0)
        {
            int remain = length - start;
            Buffer.BlockCopy(data, start, data, 0, remain);
            _pending.SetLength(remain);
        }
    }

    private void HandleLine(string line)
    {
        int brace = line.IndexOf('{');
        if (brace < 0) return;                       // 空行或非 JSON 行
        string? ev, turnId, stamp;
        try
        {
            using var doc = JsonDocument.Parse(line[brace..]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            ev = Str(root, "event");
            turnId = Str(root, "turnId");
            stamp = Str(root, "timestamp");
        }
        catch (JsonException)
        {
            return;                                   // 半行/坏行:跳过,不因此中断整份日志
        }
        if (string.IsNullOrEmpty(ev)) return;

        DateTimeOffset at = DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.UtcNow;

        if (ev == "turn.started")
        {
            if (turnId is not null) _open[turnId] = new TurnState { LastEvent = ev, LastAt = at };
            return;
        }
        if (ev is "turn.completed" or "turn.failed" or "turn.cancelled")
        {
            if (turnId is not null) _open.Remove(turnId);
            return;
        }
        // 应用重启:重启前那些回合永远不会再来 completed 了,清掉重来
        if (ev.StartsWith("bootstrap.", StringComparison.Ordinal))
        {
            _open.Clear();
            return;
        }
        // 其余事件只更新"这一轮最后的动静":在等什么决定了它还能算多久
        if (turnId is not null && _open.TryGetValue(turnId, out var state))
        {
            state.LastEvent = ev;
            state.LastAt = at;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>还有没有"活着"的回合。顺带把超时的清掉,免得字典越积越多。</summary>
    private bool Evaluate(DateTimeOffset now)
    {
        List<string>? expired = null;
        bool working = false;
        foreach (var (id, state) in _open)
        {
            if (now - state.LastAt <= GraceFor(state.LastEvent))
            {
                working = true;
                continue;
            }
            (expired ??= []).Add(id);
        }
        if (expired is not null)
        {
            foreach (string id in expired) _open.Remove(id);
        }
        return working;
    }

    /// <summary>等工具给 5 分钟,其余(等模型、刚开始)给 90 秒。</summary>
    private static TimeSpan GraceFor(string? lastEvent) =>
        lastEvent is not null && lastEvent.StartsWith("tool.", StringComparison.Ordinal)
            ? ToolGrace
            : ModelGrace;
}

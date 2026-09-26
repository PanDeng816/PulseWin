using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// DSH(DeepSeek Harness)此刻是不是正在跑一轮。
///
/// **读的是它的会话投影**,不是会话正文:<c>~\.dsh\storages\session_projcache\sessions\*.json</c>。
/// 每个会话一份**明文**小 JSON(约 20 KB),里面 <c>record.rows.turnBoundary.val.openTurnStartSeq</c>
/// 非空 = 有一轮还没结束,<c>record.rows.sessionStats.val.openStep</c> 给出当前这一步的
/// 开始时刻。**为什么不读会话正文**(<c>session.v4.jsonl.zstd</c>):那是 zstd 压缩的事件流,
/// 判断"在不在跑"要把整份解压一遍(单个会话上百毫秒),而这个是现成的投影、还是明文的。
///
/// **为什么不看文件修改时间**:与 ZCode 那边同一个理由——"最近 N 秒有写入"两个方向都会错。
/// 这里用 mtime 只做**兜底**:投影里的 <c>startTime</c> 才是"这一步什么时候开始的"。
///
/// **时间门限必须要有**:异常退出会留下一个永远不闭合的 openTurn(DSH 重启不会回头补),
/// 没有门限的话那个会话会永远显示成"在跑"。所以取 <c>max(openStep.startTime, 文件时间)</c>,
/// 超过 <see cref="ToolGrace"/> 就当它已经死了——这个宽限给的是"等工具"(跑长构建、
/// 长命令期间既没有新事件、startTime 也不再前进)。
/// </summary>
public sealed class DshActivity
{
    /// <summary>等工具/等模型的宽限。一轮里最长的静默就是工具执行期间。</summary>
    private static readonly TimeSpan ToolGrace = TimeSpan.FromMinutes(5);

    /// <summary>只解析最近有写入的投影文件:正在跑的会话一定在其中,老的没必要看。</summary>
    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(10);

    /// <summary>一次最多看几个候选(最新的几个)。多开并行会话也够。</summary>
    private const int MaxCandidates = 4;

    private readonly string _directory;
    private string? _lastErrorText;

    public DshActivity(string? projectionDirectory = null)
    {
        _directory = projectionDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "storages", "session_projcache", "sessions");
    }

    /// <summary>DSH 是否正在跑一轮。</summary>
    public bool IsWorking { get; private set; }

    /// <summary>读的投影目录(诊断要显示出来)。</summary>
    public string ProjectionDirectory => _directory;

    /// <summary>上一次读取失败的原因(诊断用),成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>投影里"回合还开着"的会话数(诊断用;含已被判定为死掉的)。</summary>
    public int OpenTurns { get; private set; }

    /// <summary>正在跑的那个会话 id(诊断用),空闲时为 null。</summary>
    public string? ActiveSession { get; private set; }

    /// <summary>本轮判定用的时间基准(诊断用:一眼看出是不是卡在门限上)。</summary>
    public DateTimeOffset? LastSignalAt { get; private set; }

    private sealed record SessionState(bool OpenTurn, DateTimeOffset? StepStart);

    /// <summary>
    /// 读一次投影并重算状态。由主循环按秒调用——没动静时开销只是枚举一次目录。
    /// 返回状态是否发生了变化(界面据此决定要不要重画)。
    /// </summary>
    public bool Poll()
    {
        bool before = IsWorking;
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset newest = DateTimeOffset.MinValue;
            bool working = false;
            int open = 0;
            string? active = null;

            foreach (var (path, written) in RecentProjections(now))
            {
                if (!TryReadState(path, out var state) || !state.OpenTurn) continue;
                open++;
                // 门限的时间基准:投影里的 step 开始时刻优先,文件时间兜底
                DateTimeOffset reference = state.StepStart is { } step && step > written ? step : written;
                if (now - reference > ToolGrace) continue;      // 僵尸回合:当它已经死了
                working = true;
                if (reference > newest) newest = reference;
                active ??= Path.GetFileNameWithoutExtension(path);
            }

            OpenTurns = open;
            ActiveSession = working ? active : null;
            LastSignalAt = working ? newest : null;
            IsWorking = working;
            LastError = null;
        }
        catch (Exception ex)
        {
            // 读不了不该影响浮窗:当作"不在工作",并只在错误内容变化时记一条
            LastError = ex.Message;
            if (_lastErrorText != ex.Message)
            {
                _lastErrorText = ex.Message;
                Diagnostics.Note("读取 DSH 活动投影失败", ex);
            }
            IsWorking = false;
        }

        if (before != IsWorking)
            Diagnostics.Note($"DSH 活动状态: {(IsWorking ? $"开始工作({ActiveSession})" : "空闲")}(开着的回合 {OpenTurns})");
        return before != IsWorking;
    }

    /// <summary>
    /// 最近写过的投影文件,新的在前。用目录枚举一次拿全(含时间戳),不逐个 stat 两遍。
    /// </summary>
    private List<(string Path, DateTimeOffset Written)> RecentProjections(DateTimeOffset now)
    {
        var all = new List<(string Path, DateTimeOffset Written)>();
        if (!Directory.Exists(_directory)) return all;

        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            var stamp = new DateTimeOffset(written, TimeSpan.Zero);
            if (now - stamp > FreshWindow) continue;
            all.Add((path, stamp));
        }
        return all.OrderByDescending(x => x.Written).Take(MaxCandidates).ToList();
    }

    private static bool TryReadState(string path, out SessionState state)
    {
        state = new SessionState(false, null);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("record", out var record)) return false;
            if (!record.TryGetProperty("rows", out var rows)) return false;

            bool openTurn = false;
            if (rows.TryGetProperty("turnBoundary", out var boundary)
                && boundary.TryGetProperty("val", out var boundaryValue)
                && boundaryValue.ValueKind == JsonValueKind.Object
                && boundaryValue.TryGetProperty("openTurnStartSeq", out var seq))
            {
                openTurn = seq.ValueKind == JsonValueKind.Number;
            }

            DateTimeOffset? stepStart = null;
            if (rows.TryGetProperty("sessionStats", out var stats)
                && stats.TryGetProperty("val", out var statsValue)
                && statsValue.ValueKind == JsonValueKind.Object
                && statsValue.TryGetProperty("openStep", out var openStep)
                && openStep.ValueKind == JsonValueKind.Object
                && openStep.TryGetProperty("startTime", out var startTime)
                && startTime.ValueKind == JsonValueKind.Number
                && startTime.TryGetInt64(out long ms) && ms > 0)
            {
                stepStart = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }

            state = new SessionState(openTurn, stepStart);
            return true;
        }
        catch (Exception)
        {
            // 半写完的投影:当它不存在,下一轮再读
            return false;
        }
    }
}

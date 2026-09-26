namespace PulseWin;

/// <summary>
/// 一次**有据可查**的 token 消耗。这是所有读取器的统一产物:
/// 各家 CLI 的库/日志形状千差万别,读出来都归一到这个形状,后面分桶与计价只有一条路。
///
/// **一个读不出 token 数的来源不产生记录。** 宁可不显示,也不显示 0——
/// 上游的原话是"一个猜出来的数字没人看得出它错了"。
/// </summary>
public sealed record AgentUsageRecord(
    DateTime Timestamp,
    string Model,
    TokenTally Tally,
    /// <summary>来源报了总量但无法归入四类的部分。计入总数,**绝不参与计价**。</summary>
    long UnclassifiedTokens,
    /// <summary>哪个 CLI/客户端花的(ZCode / OpenCode)。</summary>
    string Agent,
    string? ProviderId,
    string? SessionId,
    string? Project,
    string? Title,
    /// <summary>
    /// 输出里**属于推理**的部分(是 Tally.Output 的子集,不是额外量)。
    /// 只用于展示"推理占比",绝不重复计入总量。
    /// </summary>
    long ReasoningTokens = 0,
    /// <summary>这次请求的耗时(毫秒)。0 = 来源没记。</summary>
    long DurationMs = 0,
    /// <summary>首字延迟(毫秒)。0 = 来源没记。</summary>
    long TimeToFirstTokenMs = 0,
    /// <summary>这次请求触发了多少次工具调用。</summary>
    long ToolCalls = 0,
    /// <summary>重试次数。</summary>
    long Retries = 0,
    /// <summary>失败/取消的次数(1 = 这次就是失败/取消;0 = 成功完成)。</summary>
    long Failures = 0,
    /// <summary>
    /// 来源自己的会话号。**只在"会话号被改写过"的来源上出现**——DSH 的子代理会话会
    /// 被归根到主会话(见 <see cref="DshUsageStore"/>):<see cref="SessionId"/> 承担
    /// **分组**(子任务花的钱算在主任务头上),这一个才承担**去重**。null = 与 SessionId 相同。
    ///
    /// **为什么必须分开**:去重键从前直接用 SessionId,那么归根规则一改,同一批明细的键
    /// 就全变了 → 本地仓库把它们当成新记录再灌一遍 → 数字直接翻倍(明细本身没变,变的只是
    /// "它属于哪个会话")。分开之后,归根怎么改都不影响"这条明细我见过没有"。
    /// </summary>
    string? SourceSessionId = null)
{
    public long TotalTokens => Tally.Total + UnclassifiedTokens;

    /// <summary>来源自报的记录标识,用于跨源去重。</summary>
    public string DedupKey =>
        $"{Agent}|{SourceSessionId ?? SessionId}|{Timestamp:yyyyMMddHHmmssfff}|{Model}|{TotalTokens}";
}

/// <summary>
/// 本机用量来源。每个来源自己知道"在不在本机"和"怎么读"。
/// </summary>
public interface IUsageStore
{
    /// <summary>显示名(ZCode / OpenCode)。</summary>
    string Name { get; }

    /// <summary>本机是否存在这个来源的库(不存在不是错误,只是没装过/没用过)。</summary>
    bool IsPresent { get; }

    /// <summary>来源位置(诊断与界面显示用)。</summary>
    string Location { get; }

    /// <summary>读全部记录。失败时抛异常,由调用方记进 Notes。</summary>
    IReadOnlyList<AgentUsageRecord> Read();
}

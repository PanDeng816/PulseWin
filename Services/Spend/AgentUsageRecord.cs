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
    string? Title)
{
    public long TotalTokens => Tally.Total + UnclassifiedTokens;

    /// <summary>来源自报的记录标识,用于跨源去重。</summary>
    public string DedupKey =>
        $"{Agent}|{SessionId}|{Timestamp:yyyyMMddHHmmssfff}|{Model}|{TotalTokens}";
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

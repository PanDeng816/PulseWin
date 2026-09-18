namespace PulseWin;

/// <summary>
/// 一次调用的四类 token。上游 Pulse 的口径,也是这块统计的地基:
/// 分类计数之和必须能对上来源报告的总量;对不上时差额进
/// <c>UnclassifiedTokens</c>,**绝不塞进 Input**,也绝不为它定价。
///
/// **字段是 long 不是 int**:单次请求用不到,但账本要按月累加,
/// 实测一个月就有 40 亿 token——int 会在 21 亿处回绕,把总量算成一个比一周还小的数。
/// </summary>
public readonly record struct TokenTally(long Input, long CacheWrite, long CacheRead, long Output)
{
    public static readonly TokenTally Zero = default;

    /// <summary>四类之和。不含未分类部分。</summary>
    public long Total => Input + CacheWrite + CacheRead + Output;

    public static TokenTally operator +(TokenTally a, TokenTally b) => new(
        a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);

    /// <summary>四类里是否有负数(坏数据;builder 会跳过整条记录而不是夹到 0)。</summary>
    public bool HasNegative => Input < 0 || CacheWrite < 0 || CacheRead < 0 || Output < 0;
}

/// <summary>四类 token 各自的估算金额(美元)。</summary>
public readonly record struct TokenCost(double Input, double CacheWrite, double CacheRead, double Output)
{
    public static readonly TokenCost Zero = default;

    public double Total => Input + CacheWrite + CacheRead + Output;

    public static TokenCost operator +(TokenCost a, TokenCost b) => new(
        a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
}

/// <summary>
/// 一个模型的公开 API 牌价,**每百万 token 美元**。
/// </summary>
/// <param name="CacheWrite">缓存写价;缺失表示该厂商不单独计价,回退输入价。</param>
/// <param name="Vendor">价目来自哪个厂商条目(第一方 id 或套餐厂商名),卡片要显示出来。</param>
public sealed record ModelPrice(
    double Input,
    double Output,
    double? CacheRead,
    double? CacheWrite,
    string Vendor)
{
    /// <summary>
    /// 只有一种把 token 变成钱的算法:分类计数 × 各自牌价。
    /// 缓存价缺失时回退到输入价——这是厂商自己的算法(没单独计价就是按输入算),不是估算。
    /// </summary>
    public TokenCost Cost(TokenTally tally) => new(
        tally.Input * Input / 1_000_000d,
        tally.CacheWrite * (CacheWrite ?? Input) / 1_000_000d,
        tally.CacheRead * (CacheRead ?? Input) / 1_000_000d,
        tally.Output * Output / 1_000_000d);
}

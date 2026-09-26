namespace PulseWin;

/// <summary>
/// 账本的**共享缓存 + 引用计数**。
///
/// 为什么要它：账本（<see cref="SpendLedger"/>）是"把本机几万条用量明细读进内存并聚合"
/// 的产物，构建一次要扫两个 SQLite 库、几秒 CPU、几十 MB 瞬时内存。以前每个消费者
/// （用量统计窗）都自己 <c>SpendLedger.Build</c> 一份；现在又多了"模型"页，
/// 两处各建一份就是白白翻倍。
///
/// 三条规矩：
/// 1. **单飞（single-flight）**：并发请求只触发一次构建，其余等同一个结果。
///    <see cref="SpendLedger.Build"/> 会顺手把明细写进本地仓库（<c>UsageRepository.MergeIn</c>），
///    并发跑两份会互相争抢那个 SQLite 文件。
/// 2. **引用计数**：窗口 <see cref="Acquire"/> / <see cref="Release"/>。计数归零就丢掉账本，
///    内存立刻还给系统——不做"留着下次秒开"的常驻缓存（那正是之前关掉统计窗后
///    内存不回落的来源）。
/// 3. **可失效**：价目表更新或仓库重建后 <see cref="Invalidate"/>，下次重新构建。
///
/// **一个刻意的例外**：DSH 的用量读取器带一份自己的文件级缓存(见
/// <see cref="DshUsageStore"/>)，它**不随账本释放**。理由是量级差得远——账本是
/// "几万条明细 + 聚合"的几十 MB、关窗后确实没人用；那份缓存是"每个会话一串小记录"、
/// 有硬上限(十几 MB 量级)，换来的是"再次打开用量窗不用把几百个会话重新解压一遍"。
/// 会话数只增不减:一个季度之后，冷启动就是十几秒的"正在读取中"，那比常驻十几 MB 更糟。
/// </summary>
public static class SpendLedgerCache
{
    private static readonly object Gate = new();
    private static Task<SpendLedger>? _building;
    private static SpendLedger? _ledger;
    private static int _refCount;

    /// <summary>账本已构建完成的次数（诊断/测试用；每次真正 Build 会 +1）。</summary>
    public static int BuildCount { get; private set; }

    /// <summary>当前持有点数（诊断用）。</summary>
    public static int RefCount
    {
        get { lock (Gate) return _refCount; }
    }

    /// <summary>账本是否在内存里（诊断用）。</summary>
    public static bool IsResident
    {
        get { lock (Gate) return _ledger is not null; }
    }

    /// <summary>
    /// 取一份账本并占一个引用。调用方用完必须 <see cref="Release"/>。
    /// 构建在线程池上跑，不阻塞 UI。
    /// </summary>
    public static Task<SpendLedger> AcquireAsync()
    {
        lock (Gate)
        {
            _refCount++;
            if (_ledger is not null) return Task.FromResult(_ledger);
            // 单飞：已有构建在跑就复用那一个，不再启第二个
            return _building ??= Task.Run(BuildOnce);
        }
    }

    private static SpendLedger BuildOnce()
    {
        var ledger = SpendLedger.Build(ModelPrices.Current);
        lock (Gate)
        {
            _ledger = ledger;
            _building = null;
            BuildCount++;
        }
        return ledger;
    }

    /// <summary>还一个引用。计数归零时释放账本。</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_refCount > 0) _refCount--;
            if (_refCount == 0) _ledger = null;
        }
    }

    /// <summary>
    /// 作废缓存（价目表更新、仓库重建后调用）。已在飞的构建结果会被丢弃，
    /// 持有旧引用的窗口下一次取就会拿到新账本。
    /// </summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _ledger = null;
            _building = null;
        }
    }
}

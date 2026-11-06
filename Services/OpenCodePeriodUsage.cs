using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PulseWin;

/// <summary>已经计入过的一条请求(去重集合的一项)。</summary>
/// <param name="Id">服务端的 <c>requestID</c>。</param>
/// <param name="StartedAtMs">这条请求的 <c>startedAt</c>——**用它按"多久以前"裁剪去重集合**,
/// 而不是按条数(按条数裁就是 v1.26.9 及以前的 bug:突发时集合装不下,老 ID 被挤掉,
/// 于是它们每轮都被重新计一次,见下面 <see cref="OpenCodePeriodTracker"/> 的说明)。</param>
public sealed record SeenRequest(string Id, long StartedAtMs);

/// <summary>一个账期内的累计量(按服务端逐请求明细累加)。</summary>
/// <param name="PeriodStart">这条累计对应的账期起点(<c>go/status</c> 的 <c>access.startsAt</c>)。
/// 它与落盘值不一致 = 账期已重置,必须清零重算。</param>
/// <param name="Tokens">本账期已用 token(输入 + 输出 + 缓存读 + 缓存写,与官方聚合同口径)。</param>
/// <param name="Requests">本账期已计的推理请求数。</param>
/// <param name="LastSeenMs">已经累计到的时刻(毫秒);下一轮从它往回 <c>OverlapMs</c> 开始。</param>
/// <param name="Incomplete">账期开头有一段没计入(程序没在重置那一刻跑),这一整个账期都成立。</param>
/// <param name="Recent">
/// 去重集合:最近 <see cref="OpenCodePeriodTracker.KeepMs"/> 内已经计入过的请求。
/// **为什么需要它**:①服务端的 <c>since</c> 是**闭区间**(实测 <c>since=t</c> 会返回
/// <c>startedAt == t</c> 的那条);②每轮把 <c>since</c> 回退
/// <see cref="OpenCodePeriodTracker.OverlapMs"/> 去捡晚入库的条目。这两件事都会让同一条
/// 请求被看到多次,不挡就会重复计入。
/// </param>
/// <param name="TruncatedRounds">
/// 连续"单轮翻不完"的轮数。攒到 <see cref="OpenCodePeriodTracker.GiveUpAfterTruncatedRounds"/>
/// 就放弃补齐、从当前时刻重开(停机太久导致积压超过单轮上限时,否则读数会永久冻住)。
/// </param>
/// <param name="StateVersion">
/// 落盘格式版本。<c>0</c> = v1.26.9 及以前的文件(去重集合是 <c>LastSeenIds</c>,按条数裁);
/// <c>2</c> = <see cref="Recent"/> 按时间裁。**旧文件的累计值不能接着用**:它可能已经把
/// 同一批请求重复计过(2026-11-06 实测虚高 15%),只能从账期起点重扫。
/// </param>
/// <param name="HeadMissing">
/// 这个账期的**开头**永久缺了一段(建立状态时账期已经过去很久,或积压超限后从"现在"重开)。
/// 与 <see cref="Incomplete"/> 的区别:后者还包含"正在补历史、暂时落后"这种会自己消失的状态——
/// 两者混在一个字段里,补历史剩下最后一轮时会把"不完整"永久钉住(2026-11-06 踩到)。
/// </param>
public sealed record OpenCodePeriodUsage(
    DateTimeOffset PeriodStart,
    long Tokens,
    long Requests,
    long LastSeenMs,
    bool Incomplete,
    DateTimeOffset UpdatedAt,
    SeenRequest[]? Recent = null,
    int TruncatedRounds = 0,
    int StateVersion = 0,
    bool HeadMissing = false);

/// <summary>
/// OpenCode Go **本账期**的 token 累计——回答"重置之后用了多少"。
///
/// **为什么要绕这么远**:官方聚合接口(<c>/console/api/usage/summary</c>)只有
/// <c>24h / 7d / 30d / all</c> 四档,不接受任意起止(实测 <c>from/to</c> 与
/// <c>start/end</c> 要么 400、要么被忽略返回默认窗口),所以"从账期起点到现在"这个
/// 区间拿不到。而账期起点本身在 <c>go/status</c> 里是精确的(<c>access.startsAt</c>)。
/// 唯一能精确切在那一刻上的数据源是逐请求明细 <c>/console/api/request-logs?since=ms</c>
/// (每条带 startedAt 与 input/output/cacheRead/cacheWrite;只保留 30 天)。
///
/// **全量翻太重**(重度账号 117 页 / 353 秒,见 v1.16 的教训),所以这里做增量:
/// 落盘 {账期起点, 已累计 token, 已累计到的最新 startedAt},每轮只拉
/// <c>since=LastSeenMs - OverlapMs</c> 之后的新条目——程序常开时通常一页就够。
/// 账期一变就清零重算,于是"重置后归零"是天然的。
///
/// **v1.27.0 修掉的重复计入(2026-11-06)**:去重集合原来按**条数**上限 500 裁,
/// 而回退窗口是 10 分钟——实测突发时 10 分钟内就有 **934 条**明细(11-06 13:46 前后),
/// 集合装不下,最老的那批 ID 被挤掉,下一轮同一批请求再被计一次,如此往复 20 轮。
/// 官方明细全量 18,163 条 / 30.18 亿,界面记成 34,128 条 / 34.82 亿(+87% 条数 / +15% token);
/// 按同样的规则离线复现是 37,738 条 / 35.92 亿,数量级对得上。
/// 现在改成**按"多久以前"裁**(<see cref="KeepMs"/> = 2 倍回退窗口),并用
/// <c>until</c> 把单轮窗口切成 6 小时片,补历史时不再需要"翻不完就整轮作废"。
///
/// **缺口怎么处理**:如果建立状态时账期已经进行了 6 小时以上(说明程序没在重置那一刻跑),
/// 就从"现在"开始累计并标 <see cref="OpenCodePeriodUsage.Incomplete"/>——宁可承认少了开头,
/// 也不把不完整的数当完整数报。
/// </summary>
public static class OpenCodePeriodTracker
{
    private const string ApiBase = "https://opencode.ai/console/api";
    private const string Origin = "https://opencode.ai";

    /// <summary>当前落盘格式版本(<c>0</c> = v1.26.9 及以前)。</summary>
    private const int CurrentStateVersion = 2;

    /// <summary>官方逐请求明细每页最多 100 条。</summary>
    private const int PageSize = 100;

    /// <summary>
    /// 单轮最多翻几页。程序常开时是 1 页;停机后补翻会多几页,再多就留到下一轮。
    ///
    /// **12 页不够用过一次**(2026-10-31):`request-logs` 里混着 <c>category=api</c> 的控制台审计
    /// 记录,三小时就有 1,325 条 api、只有 17 条 inference —— 12 页 1,200 条连三个小时都覆盖不了,
    /// 于是每轮都判"截断"、`since` 永远推不动、浮窗头部退回"近30天"(读数被冻在 14:28)。
    /// 现在请求端带 <c>category=inference</c>(实测服务端支持,返回全 inference),页数立刻降一个量级;
    /// 上限仍放宽到 40 页,给"停机一天再启动"留余量。
    /// </summary>
    private const int MaxPagesPerRound = 40;

    /// <summary>单轮窗口最长多久。<c>until</c> 实测可用(见 v1.27.0 说明),所以补历史按片走。</summary>
    private const long WindowMs = 6 * 60 * 60 * 1000;

    /// <summary>窗口装不下时最多对半砍几次(6h → 3h → 1.5h → 45min)。</summary>
    private const int MaxWindowHalvings = 3;

    /// <summary>连续多少轮截断后放弃补齐、从当前时刻重开(见 RefreshCoreAsync 里兜底段)。</summary>
    private const int GiveUpAfterTruncatedRounds = 3;

    /// <summary>
    /// 每轮把 <c>since</c> 往回收多少。官方明细**入库有延迟**(几十秒到几分钟),晚入库的条目
    /// startedAt 早于已推进的 <c>LastSeenMs</c>;不回退的话它们永远拉不到(2026-10-31 实测:
    /// 重启补拉后比官方全量少 39 条 / 0.8%)。回退这段 + 按 requestID 去重,就能把它们捡回来。
    /// </summary>
    private const long OverlapMs = 10 * 60 * 1000;

    /// <summary>
    /// 去重集合保留多久以内的请求。<b>必须明显大于 <see cref="OverlapMs"/></b>——边界要能
    /// 盖住"上一轮回退的 10 分钟"加上"这两轮之间新推进的一段"。
    /// **按时间裁而不是按条数裁**是 v1.27.0 的关键改动:条数上限在突发时必然装不下。
    /// </summary>
    private const long KeepMs = 2 * OverlapMs;

    /// <summary>去重集合的硬上限(纯防跑飞;按时间裁之后实测只占几百到两千条)。</summary>
    private const int MaxRecentIds = 20_000;

    /// <summary>读数落后"现在"超过这么久,就当还没补齐(卡片会标注不完整)。</summary>
    private const long BehindWarnMs = 10 * 60 * 1000;

    /// <summary>账期已经过去多久以内,才认为"从账期起点开始累计"是完整的。</summary>
    private static readonly TimeSpan FreshWindow = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>上一次的结果说明(诊断/界面用)。</summary>
    public static string? LastStatus { get; private set; }

    private static HttpClient CreateClient()
    {
        var client = ProxyHttp.Create(TimeSpan.FromSeconds(30));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("Origin", Origin);
        client.DefaultRequestHeaders.Add("Referer", Origin + "/console/");
        return client;
    }

    /// <summary>
    /// 刷新某个账号(某个工作区)本账期的累计。返回 null = 这轮拿不到(原因见
    /// <see cref="LastStatus"/>),调用方应保留原读数。
    ///
    /// **跨进程互斥**:诊断进程与正在运行的实例可能同时累加同一份状态文件,
    /// 各算一遍再把各自的结果覆盖回去 = 同一批请求被计两次(2026-10-28 实测多出
    /// 4 条 / 66 万 tokens,正是这个原因)。用独占的文件锁把"读-算-写"整段包住;
    /// 拿不到锁就跳过这一轮,下一轮再算——这里宁可少算一轮,也不能算重。
    /// </summary>
    public static async Task<OpenCodePeriodUsage?> RefreshAsync(
        AppDataPaths paths,
        int instance,
        string workspaceId,
        DateTimeOffset periodStart,
        string cookie,
        CancellationToken token)
    {
        string lockPath = paths.OpenCodePeriodUsageFileFor(instance) + ".lock";
        FileStream? gate = null;
        try
        {
            gate = await AcquireLockAsync(lockPath, token);
            if (gate is null)
            {
                LastStatus = "另一个实例正在累计本账期用量,这一轮跳过";
                return null;
            }
            return await RefreshCoreAsync(paths, instance, workspaceId, periodStart, cookie, token);
        }
        finally
        {
            gate?.Dispose();
        }
    }

    private static async Task<FileStream?> AcquireLockAsync(string lockPath, CancellationToken token)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(150, token);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(150, token);
            }
        }
        return null;
    }

    private static async Task<OpenCodePeriodUsage?> RefreshCoreAsync(
        AppDataPaths paths,
        int instance,
        string workspaceId,
        DateTimeOffset periodStart,
        string cookie,
        CancellationToken token)
    {
        string file = paths.OpenCodePeriodUsageFileFor(instance);
        OpenCodePeriodUsage? state = Load(file);

        long periodMs = periodStart.ToUnixTimeMilliseconds();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        bool newPeriod = state is null || state.PeriodStart != periodStart;

        // 旧格式的累计值不能接着用:它可能已经把同一批请求重复计过(见类注释)。
        // 从账期起点重扫,按 6 小时切片、每轮补一段,程序中途退出下次接着补。
        bool rebuilt = false;
        if (!newPeriod && state!.StateVersion < CurrentStateVersion)
        {
            state = new OpenCodePeriodUsage(
                periodStart, 0, 0, periodMs, Incomplete: false,
                DateTimeOffset.Now, null, 0, CurrentStateVersion, HeadMissing: false);
            rebuilt = true;
        }

        long sinceMs;
        bool missedHead = false;
        if (newPeriod)
        {
            // 账期刚重置(6 小时内)⇒ 从这里开始累计是完整的;否则只能从现在算,并标注缺口。
            var age = DateTimeOffset.UtcNow - periodStart;
            bool fresh = age <= FreshWindow && age >= TimeSpan.FromMinutes(-5);
            sinceMs = fresh ? periodMs : nowMs;
            missedHead = !fresh;
            state = new OpenCodePeriodUsage(periodStart, 0, 0, sinceMs, missedHead,
                DateTimeOffset.Now, null, 0, CurrentStateVersion, HeadMissing: missedHead);
        }
        else
        {
            sinceMs = Math.Max(periodMs, state!.LastSeenMs - OverlapMs);
        }

        long untilMs = Math.Min(nowMs, sinceMs + WindowMs);

        long addTokens = 0, addRequests = 0;
        List<SeenRequest> counted = new();
        bool truncated = false;

        // 窗口装不下(停机很久 / 极端突发)就把窗口对半砍再来——已算的丢掉、不推进边界,
        // 所以不会漏也不会重;切片本身是 6 小时,砍三次还装不下说明数据病态,整轮作废。
        for (int halvings = 0; ; halvings++)
        {
            addTokens = 0;
            addRequests = 0;
            counted = new List<SeenRequest>();
            truncated = false;

            // 去重集合 = 上次落盘的那批。**同一条请求一旦计入就当场加进集合**,
            // 这样"本轮前面几页"和"上一轮的重叠区间"用同一道闸,不给轮内重复留口子。
            var recentIds = new HashSet<string>(StringComparer.Ordinal);
            if (state!.Recent is { Length: > 0 } knownIds)
            {
                foreach (var seen in knownIds)
                    if (!string.IsNullOrEmpty(seen.Id)) recentIds.Add(seen.Id);
            }

            string? cursor = null;
            int pages = 0;
            try
            {
                while (true)
                {
                    if (pages >= MaxPagesPerRound)
                    {
                        truncated = true;
                        break;
                    }

                    // category=inference:服务端实测支持,只回模型请求。不带它,控制台审计的 api
                    // 记录会把每页名额吃光(2026-10-31:3 小时 1,325 条 api / 17 条 inference)。
                    // until:服务端实测接受(v1.27.0 起用它把长窗口切成片,响应里也回显它)。
                    string query = $"?limit={PageSize}&since={sinceMs}&until={untilMs}&category=inference"
                        + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                    using var doc = await GetJsonAsync("request-logs" + query, cookie, workspaceId, token);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            // category=="inference" 才是模型请求;"api" 是控制台自身调用(审计日志)
                            if (!string.Equals(Str(item, "category"), "inference", StringComparison.OrdinalIgnoreCase))
                                continue;

                            long started = Long(item, "startedAt");
                            string rid = Str(item, "requestID") ?? Str(item, "id") ?? "";
                            // 已经计过(上一轮的回退区间 / 本轮前面几页)直接跳过,否则会重复加。
                            if (rid.Length > 0 && !recentIds.Add(rid))
                                continue;

                            // 与官方 summary 同口径:输出里已经含推理分量,不再单独加 reasoningTokens
                            long tokens = Long(item, "inputTokens")
                                + Long(item, "outputTokens")
                                + Long(item, "cacheReadTokens")
                                + Long(item, "cacheWriteTokens");
                            addTokens += tokens;
                            addRequests++;
                            if (rid.Length > 0) counted.Add(new SeenRequest(rid, started));
                        }
                    }

                    cursor = Str(root, "nextCursor");
                    pages++;
                    if (string.IsNullOrEmpty(cursor)) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (CommandCodeApiException ex) when (ex.IsAuthenticationError)
            {
                LastStatus = "浏览器登录态已过期,读不到本账期明细";
                return null;
            }
            catch (Exception ex)
            {
                LastStatus = "读取本账期明细失败:" + ex.Message;
                return null;
            }

            if (!truncated) break;

            long span = untilMs - sinceMs;
            if (halvings >= MaxWindowHalvings || span <= OverlapMs)
            {
                // **兜底**:连续几轮都补不动说明积压已经超过单轮上限。再"下轮重试"就是
                // 永久卡死——读数冻住、浮窗退回"近30天"(2026-10-31 实际踩到)。
                // 这时放弃补齐,从当前时刻重开并标 Incomplete:承认少了一段,但读数能继续涨。
                int rounds = state!.TruncatedRounds + 1;
                if (rounds >= GiveUpAfterTruncatedRounds)
                {
                    var restarted = state with
                    {
                        LastSeenMs = nowMs,
                        Recent = null,
                        Incomplete = true,
                        HeadMissing = true,
                        TruncatedRounds = 0,
                        StateVersion = CurrentStateVersion,
                        UpdatedAt = DateTimeOffset.Now,
                    };
                    try
                    {
                        Save(file, restarted);
                    }
                    catch (Exception ex)
                    {
                        LastStatus = "本账期累计落盘失败:" + ex.Message;
                        return null;
                    }
                    LastStatus = "积压明细超过单轮上限,已从当前时刻重新累计(中间一段未计入)";
                    return restarted;
                }

                var pending = state! with { TruncatedRounds = rounds, UpdatedAt = DateTimeOffset.Now };
                try
                {
                    Save(file, pending);
                }
                catch { /* 计数写失败不致命,下轮还会再截断 */ }
                LastStatus = $"本账期明细超过 {MaxPagesPerRound} 页,本轮不更新(第 {rounds} 轮,"
                    + $"连续 {GiveUpAfterTruncatedRounds} 轮后将从当前时刻重开)";
                return null;
            }

            // 窗口对半砍:只留更早的那一半,下一遍从 sinceMs 重新翻。
            untilMs = sinceMs + span / 2;
        }

        // 边界推进到**窗口末端**(而不是"最后见到的 startedAt"):窗口是按时钟切的,
        // 末端之前可能有一段本来就没有请求;不推到末端,since 就会一直停在原地看着同一段空区间。
        long lastSeen = Math.Max(state!.LastSeenMs, untilMs);

        // 去重集合按**时间**裁:晚于 lastSeen - KeepMs 的都留着(条数硬上限只防跑飞)。
        long floor = lastSeen - KeepMs;
        var recent = new List<SeenRequest>();
        if (state.Recent is { Length: > 0 } carried)
        {
            foreach (var seen in carried)
                if (seen.StartedAtMs >= floor) recent.Add(seen);
        }
        foreach (var seen in counted)
            if (seen.StartedAtMs >= floor) recent.Add(seen);
        if (recent.Count > MaxRecentIds)
            recent = recent.Skip(recent.Count - MaxRecentIds).ToList();

        // 读数落后"现在"就是还没补齐(重扫历史 / 停机很久);账期开头永久缺的那段另算。
        bool incomplete = state.HeadMissing || untilMs < nowMs - BehindWarnMs;

        var updated = state with
        {
            Tokens = state.Tokens + addTokens,
            Requests = state.Requests + addRequests,
            LastSeenMs = lastSeen,
            Recent = recent.Count > 0 ? recent.ToArray() : null,
            Incomplete = incomplete,
            HeadMissing = state.HeadMissing,
            TruncatedRounds = 0,
            StateVersion = CurrentStateVersion,
            UpdatedAt = DateTimeOffset.Now,
        };

        // 没有变化就不写盘:按时间裁之后去重集合可能有几百上千条,每 30 秒重写一次纯属写放大。
        bool changed = rebuilt
            || updated.Tokens != state.Tokens
            || updated.Requests != state.Requests
            || updated.LastSeenMs != state.LastSeenMs
            || updated.Incomplete != state.Incomplete
            || updated.HeadMissing != state.HeadMissing
            || updated.StateVersion != state.StateVersion;

        if (changed)
        {
            try
            {
                Save(file, updated);
            }
            catch (Exception ex)
            {
                LastStatus = "本账期累计落盘失败:" + ex.Message;
            }
        }

        LastStatus = $"本账期已累计 {updated.Tokens:N0} tokens / {updated.Requests:N0} 次"
            + (updated.Incomplete ? "(还在补齐)" : "")
            + (rebuilt ? " · 旧口径重算中" : "");
        return updated;
    }

    /// <summary>
    /// 诊断用:把账期内的逐请求明细**完整拉一遍**却不落盘,用来核对"累计值为什么比官方
    /// 24h 还大"这类问题——重点看同一条请求是否被服务端返回了多次(重复 requestID)。
    /// </summary>
    public static async Task<IReadOnlyList<string>> AuditAsync(
        string workspaceId, DateTimeOffset periodStart, string cookie, CancellationToken token)
    {
        var lines = new List<string>();
        long sinceMs = periodStart.ToUnixTimeMilliseconds();
        long rawTokens = 0, dedupedTokens = 0;
        int raw = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dupSamples = new List<string>();
        string? cursor = null;
        int pages = 0;

        while (true)
        {
            if (pages >= 400) { lines.Add("超过 400 页,停止"); break; }
            string query = $"?limit={PageSize}&since={sinceMs}"
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var doc = await GetJsonAsync("request-logs" + query, cookie, workspaceId, token);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!string.Equals(Str(item, "category"), "inference", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string rid = Str(item, "requestID") ?? Str(item, "id") ?? "";
                    long t = Long(item, "inputTokens") + Long(item, "outputTokens")
                        + Long(item, "cacheReadTokens") + Long(item, "cacheWriteTokens");
                    raw++;
                    rawTokens += t;
                    if (seen.Add(rid))
                    {
                        dedupedTokens += t;
                    }
                    else if (dupSamples.Count < 8)
                    {
                        dupSamples.Add($"{rid} startedAt={Long(item, "startedAt")} tokens={t:N0}"
                            + $" model={Str(item, "model")} outcome={Str(item, "outcome")}");
                    }
                }
            }
            cursor = Str(root, "nextCursor");
            pages++;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        lines.Add($"明细 {raw} 条 / 去重后 {seen.Count} 条 · 原始 {rawTokens:N0} tokens / 去重 {dedupedTokens:N0} tokens");
        lines.Add($"页数 {pages};重复样例 {dupSamples.Count} 条:");
        lines.AddRange(dupSamples.Select(s => "  " + s));
        return lines;
    }

    /// <summary>
    /// 读落盘的本账期累计(不联网)。给"这一轮没拉到"兜底用:与其把卡片退回滚动 30 天
    /// (数字和标签一起跳变),不如继续显示上次的累计值 + 它有多旧(见 <c>PeriodTokensAt</c>)。
    /// <paramref name="periodStart"/> 对不上 = 账期已重置,旧值不代表新账期,返回 false。
    /// </summary>
    public static bool TryLoadCached(AppDataPaths paths, int instance, DateTimeOffset periodStart,
        out OpenCodePeriodUsage usage)
    {
        usage = null!;
        var cached = Load(paths.OpenCodePeriodUsageFileFor(instance));
        if (cached is null || cached.PeriodStart != periodStart) return false;
        usage = cached;
        return true;
    }

    /// <summary>
    /// 落盘(主文件 + 备份)。备份是为了扛"主文件写坏/被清掉":那种情况下重建会把累计
    /// 从 0 开始,界面上等于读数突然归零——比"稍旧"难解释得多。
    /// </summary>
    private static void Save(string file, OpenCodePeriodUsage state)
    {
        string json = JsonSerializer.Serialize(state, JsonOptions);
        AtomicFile.WriteText(file, json);
        try { AtomicFile.WriteText(file + ".bak", json); }
        catch { /* 备份失败不影响主文件 */ }
    }

    private static OpenCodePeriodUsage? Load(string file)
    {
        if (LoadOne(file) is { } primary) return primary;
        // 主文件读不出来(被清/写坏/JSON 截断)时退回备份;备份也没有才当"没有状态"。
        return LoadOne(file + ".bak");
    }

    private static OpenCodePeriodUsage? LoadOne(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<OpenCodePeriodUsage>(AtomicFile.ReadAllTextShared(file));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(
        string pathAndQuery, string cookie, string? orgId, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/" + pathAndQuery);
        req.Headers.Add("Cookie", cookie);
        if (orgId is not null) req.Headers.Add("x-org-id", orgId);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CommandCodeApiException(resp.StatusCode, "浏览器登录态已过期,请重新登录 opencode.ai");
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long Long(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.String) return long.TryParse(v.GetString(), out long s) ? s : 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        return 0;
    }
}

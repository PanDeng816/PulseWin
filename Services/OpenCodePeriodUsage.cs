using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PulseWin;

/// <summary>一个账期内的累计量(按服务端逐请求明细累加)。</summary>
/// <param name="PeriodStart">这条累计对应的账期起点(<c>go/status</c> 的 <c>access.startsAt</c>)。
/// 它与落盘值不一致 = 账期已重置,必须清零重算。</param>
/// <param name="Tokens">本账期已用 token(输入 + 输出 + 缓存读 + 缓存写,与官方聚合同口径)。</param>
/// <param name="Requests">本账期已计的推理请求数。</param>
/// <param name="LastSeenMs">已经累计到的最新 <c>startedAt</c>(毫秒);下一轮只拉它之后的。</param>
/// <param name="Incomplete">建立状态时账期已经进行了很久(程序没从头跑),账期开头那段缺了。</param>
/// <param name="LastSeenIds">
/// 已经计入过的、<c>startedAt == LastSeenMs</c> 那几条明细的 requestID。
/// **为什么需要它**:服务端的 <c>since</c> 是**闭区间**(实测 <c>since=t</c> 会返回
/// <c>startedAt == t</c> 的那条),而下一轮的 <c>since</c> 正是上一轮的最新 startedAt,
/// 只靠毫秒数推进就会把边界那条每轮再计一次(2026-10-30 实测:账期真值 4,991 条 /
/// 8.18 亿,界面记成了 5,882 条 / 10.3 亿,多出来的 891 条就是 891 个同步轮次的边界)。
/// 记 ID 而不是把 LastSeenMs +1:同一毫秒里有第二条新请求时,+1 会把它整条漏掉。
/// </param>
public sealed record OpenCodePeriodUsage(
    DateTimeOffset PeriodStart,
    long Tokens,
    long Requests,
    long LastSeenMs,
    bool Incomplete,
    DateTimeOffset UpdatedAt,
    string[]? LastSeenIds = null);

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
/// 落盘 {账期起点, 已累计 token, 已累计到的最新 startedAt},每轮只拉 <c>since=LastSeenMs</c>
/// 之后的新条目——程序常开时通常一页就够。账期一变就清零重算,于是"重置后归零"是天然的。
///
/// **缺口怎么处理**:如果建立状态时账期已经进行了 6 小时以上(说明程序没在重置那一刻跑),
/// 就从"现在"开始累计并标 <see cref="OpenCodePeriodUsage.Incomplete"/>——宁可承认少了开头,
/// 也不把不完整的数当完整数报。单轮翻页超过上限同样不更新(保持上次数值,下轮重试)。
/// </summary>
public static class OpenCodePeriodTracker
{
    private const string ApiBase = "https://opencode.ai/console/api";
    private const string Origin = "https://opencode.ai";

    /// <summary>官方逐请求明细每页最多 100 条。</summary>
    private const int PageSize = 100;

    /// <summary>单轮最多翻几页。程序常开时是 1 页;停机几天后补翻会多几页,再多就留到下一轮。</summary>
    private const int MaxPagesPerRound = 12;

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

        bool newPeriod = state is null || state.PeriodStart != periodStart;
        long sinceMs;
        bool incomplete;
        if (newPeriod)
        {
            // 账期刚重置(6 小时内)⇒ 从这里开始累计是完整的;否则只能从现在算,并标注缺口。
            var age = DateTimeOffset.UtcNow - periodStart;
            bool fresh = age <= FreshWindow && age >= TimeSpan.FromMinutes(-5);
            sinceMs = fresh ? periodStart.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            incomplete = !fresh;
            state = new OpenCodePeriodUsage(periodStart, 0, 0, sinceMs, incomplete, DateTimeOffset.Now);
        }
        else
        {
            sinceMs = state!.LastSeenMs;
            incomplete = state.Incomplete;
        }

        long maxSeen = sinceMs;
        long addTokens = 0, addRequests = 0;
        string? cursor = null;
        int pages = 0;
        bool truncated = false;

        // 边界去重:since 是**闭区间**,上一轮计入过的 startedAt==sinceMs 那几条会被再返回一次。
        // 用上一轮记下的 requestID 把它们挡掉(同一毫秒的新请求 ID 不在集合里,照常计入)。
        var skipIds = new HashSet<string>(StringComparer.Ordinal);
        if (!newPeriod && state!.LastSeenIds is { Length: > 0 } knownIds)
        {
            foreach (var id in knownIds)
                if (!string.IsNullOrEmpty(id)) skipIds.Add(id);
        }
        // 本批里 startedAt == maxSeen 的那些 ID:落盘后作为下一轮的边界集合。
        var maxSeenIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (true)
            {
                if (pages >= MaxPagesPerRound)
                {
                    truncated = true;
                    break;
                }

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
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        // category=="inference" 才是模型请求;"api" 是控制台自身调用(审计日志)
                        if (!string.Equals(Str(item, "category"), "inference", StringComparison.OrdinalIgnoreCase))
                            continue;

                        long started = Long(item, "startedAt");
                        string rid = Str(item, "requestID") ?? Str(item, "id") ?? "";
                        // 边界那条上一轮已经计过(闭区间会再返回一次),跳过——否则每轮多算一条。
                        if (started == sinceMs && rid.Length > 0 && skipIds.Contains(rid))
                            continue;

                        // 与官方 summary 同口径:输出里已经含推理分量,不再单独加 reasoningTokens
                        long tokens = Long(item, "inputTokens")
                            + Long(item, "outputTokens")
                            + Long(item, "cacheReadTokens")
                            + Long(item, "cacheWriteTokens");
                        addTokens += tokens;
                        addRequests++;
                        if (started > maxSeen)
                        {
                            maxSeen = started;
                            maxSeenIds.Clear();   // 边界移到新的一毫秒,旧的 ID 不再需要
                        }
                        if (started == maxSeen && rid.Length > 0) maxSeenIds.Add(rid);
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

        if (truncated)
        {
            LastStatus = $"本账期明细超过 {MaxPagesPerRound} 页,本轮不更新(下轮从同一点重试)";
            return null;
        }

        // 边界那一毫秒**停在原地**:靠 LastSeenIds 挡重复,而不是把 LastSeenMs 往前挪 1ms——
        // 同一毫秒里若还有一条没拉过的新请求,+1 会把整条漏掉(实测 since 是闭区间)。
        var boundaryIds = new HashSet<string>(StringComparer.Ordinal);
        if (maxSeen == sinceMs)
        {
            // 本轮没有更新的条目:沿用上一轮的边界集合,再把同毫秒新见到的 ID 并进去。
            if (state!.LastSeenIds is { Length: > 0 } carried)
                foreach (var id in carried)
                    if (!string.IsNullOrEmpty(id)) boundaryIds.Add(id);
        }
        boundaryIds.UnionWith(maxSeenIds);

        var updated = state! with
        {
            Tokens = state.Tokens + addTokens,
            Requests = state.Requests + addRequests,
            LastSeenMs = maxSeen,
            LastSeenIds = boundaryIds.Count > 0 ? boundaryIds.ToArray() : null,
            Incomplete = incomplete,
            UpdatedAt = DateTimeOffset.Now,
        };

        try
        {
            AtomicFile.WriteText(file, JsonSerializer.Serialize(updated, JsonOptions));
        }
        catch (Exception ex)
        {
            LastStatus = "本账期累计落盘失败:" + ex.Message;
        }

        LastStatus = $"本账期已累计 {updated.Tokens:N0} tokens / {updated.Requests:N0} 次"
            + (updated.Incomplete ? "(账期开头一段未计入)" : "");
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
            if (pages >= 60) { lines.Add("超过 60 页,停止"); break; }
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

    private static OpenCodePeriodUsage? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<OpenCodePeriodUsage>(AtomicFile.ReadAllTextShared(file));
        }
        catch
        {
            return null;   // 坏了就当没有:重建时最多丢掉本轮之前的累计
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

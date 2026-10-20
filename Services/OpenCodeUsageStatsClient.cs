using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PulseWin;

/// <summary>官方按模型的聚合(金额来自官方计费字段,不用本机牌价)。</summary>
public sealed record OpenCodeModelStat(
    string Model,
    string Provider,
    long Requests,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    double Cost)
{
    public long TotalTokens => Input + Output + CacheRead + CacheWrite;
}

/// <summary>官方按自然日(UTC)的聚合。</summary>
public sealed record OpenCodeDayStat(string Day, long Requests, long Tokens, double Cost);

/// <summary>
/// OpenCode 服务端的**权威聚合读数**——就是控制台用量页自己读的那套接口
/// (<c>/console/api/usage/{summary,models,cost-by-day}</c>)。账号级,覆盖所有设备与 Key。
/// 网页 usage 页「Last 30 days → Cost / Requests / Tokens」三格显示的就是它。
/// </summary>
public sealed record OpenCodeUsageStats(
    string Range,
    long Requests,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    double Cost,
    IReadOnlyList<OpenCodeModelStat> Models,
    IReadOnlyList<OpenCodeDayStat> Days,
    DateTimeOffset FetchedAt)
{
    /// <summary>官方口径的总 token(= 输入 + 输出 + 缓存读 + 缓存写),不含推理分量
    /// (服务端的 output 已经把它含在内了,这一点与逐请求明细不同)。</summary>
    public long TotalTokens => Input + Output + CacheRead + CacheWrite;

    /// <summary>这个窗口里官方有记录的天数。</summary>
    public int ActiveDays => Days.Count;
}

/// <summary>
/// 读 OpenCode 的**账号级用量聚合**。
///
/// **为什么不再翻 request-logs**:那是逐请求明细,只能游标串行翻页(100 条/页)。重度账号
/// 实测 **117 页 / 353 秒**,而界面上的"近 30 天"根本翻不完——旧的 40 页上限只够最近两三天,
/// 报出来的数字(1.77 亿 tokens / $2.58)只有真值(5.5 亿 / $9.65)的三分之一,却标着
/// "近 30 天"。控制台自己的用量页之所以一点就出数,是因为它读的是下面这三个**聚合**接口。
///
/// 协议(2026-10,取自控制台前端 bundle 里的查询定义):
///  · GET /console/api/orgs                          → 工作区列表
///  · GET /console/api/usage/summary?range=7d|30d    → 总量(requests/tokens/cost)
///  · GET /console/api/usage/models?range=…          → 按模型
///  · GET /console/api/usage/cost-by-day?range=…     → 按自然日
///  都要带 <c>x-org-id: wrk_xxx</c>;数值字段一律是**字符串**(服务端 BigInt 的 JSON 形态)。
///
/// 整条通道与 GOAT 的浏览器会话共用一个开关,默认关、读不到就安静返回 null。
/// </summary>
public static class OpenCodeUsageStatsClient
{
    private const string ApiBase = "https://opencode.ai/console/api";
    private const string Origin = "https://opencode.ai";
    /// <summary>微美分 → 美元。1 微美分 = 1e-6 美分 = 1e-8 美元(与 go/status 同一单位)。</summary>
    private const double MicroCentsPerUsd = 100_000_000d;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>上一次成功/失败的原因(界面与诊断显示用)。</summary>
    public static string? LastStatus { get; private set; }

    public static bool? Available { get; private set; }

    /// <summary>
    /// 结果缓存,按 range 分开存,**内存 + 落盘两层**。三条请求加起来不到一秒,缓存不
    /// 是为了省等待,而是为了"打开用量窗就出数、不必每次都问服务端":窗口每次切页/切区间、
    /// 每次重开应用都会走到这里。
    /// </summary>
    private static readonly Dictionary<string, (OpenCodeUsageStats Stats, DateTimeOffset At)> Cache =
        new(StringComparer.Ordinal);

    /// <summary>Cache 是纯静态,后台同步与 UI 线程会同时读写(取数 / Invalidate / 落盘),
    /// 不加锁时并发写会损坏 Dictionary 的内部结构。</summary>
    private static readonly object CacheGate = new();

    /// <summary>缓存新鲜度。官方这个窗口是**滚动 30 天**的统计量,十五分钟内不会有
    /// 需要立刻反映的变化;想要当下的数就点顶部的「重新读取」。</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    private static AppDataPaths? _paths;

    /// <summary>丢掉结果缓存(界面上的「重新读取」用)。</summary>
    public static void Invalidate()
    {
        lock (CacheGate) Cache.Clear();
        SaveToDisk();
    }

    /// <summary>数据目录(缓存落盘用)。引擎构造时 Attach。</summary>
    public static void Attach(AppDataPaths paths)
    {
        _paths = paths;
        LoadFromDisk();
    }

    private static string CacheFile => _paths is null
        ? ""
        : Path.Combine(_paths.RootDirectory, "opencode-usage-stats.json");

    /// <summary>落盘形态:每个 range 一条读数 + 抓取时刻。</summary>
    private sealed record CachedStats(OpenCodeUsageStats Stats, DateTimeOffset At);

    /// <summary>
    /// 把上次的读数从磁盘捡回来。**它不是数据源、只是加速**:文件坏了 / 格式变了就当没有,
    /// 下次联网照样能拿到。有了它,应用重启后第一次打开用量窗也是秒出数。
    /// </summary>
    private static void LoadFromDisk()
    {
        try
        {
            if (CacheFile.Length == 0 || !File.Exists(CacheFile)) return;
            var saved = JsonSerializer.Deserialize<List<CachedStats>>(File.ReadAllText(CacheFile));
            if (saved is null) return;
            lock (CacheGate)
            {
                foreach (var item in saved)
                {
                    if (item.Stats.Range.Length > 0) Cache[item.Stats.Range] = (item.Stats, item.At);
                }
            }
        }
        catch { /* 缓存坏了就当没有——不影响联网取值 */ }
    }

    private static void SaveToDisk()
    {
        try
        {
            if (CacheFile.Length == 0) return;
            string json;
            lock (CacheGate)
            {
                json = JsonSerializer.Serialize(
                    Cache.Values.Select(v => new CachedStats(v.Stats, v.At)).ToList());
            }
            AtomicFile.WriteText(CacheFile, json);
        }
        catch { /* 落盘失败不影响本次结果 */ }
    }

    /// <summary>把缓存年龄说成人话(状态行显示"多久前读的")。</summary>
    private static string Age(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        return span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} 秒"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} 分钟"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} 小时"
            : $"{(int)span.TotalDays} 天";
    }

    /// <summary>把界面区间映射到官方提供的窗口。「今天」用 24h(滚动 24 小时,官方没有
    /// "自然日"这一档),「近 7 天」用 7d,其余(近 30 天 / 自定义 / 全部)一律 30d——
    /// 官方最多只给 30 天。</summary>
    public static (string Range, string Label) WindowFor(SpendSpan span) => span switch
    {
        SpendSpan.Today => ("24h", "近 24 小时"),
        SpendSpan.Week => ("7d", "近 7 天"),
        _ => ("30d", "近 30 天"),
    };

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
    /// 拉账号下**全部工作区**在 <paramref name="range"/>(<c>7d</c>/<c>30d</c>)窗口内的官方聚合。
    /// 返回 null = 通道当前不可用(原因见 <see cref="LastStatus"/>)。
    /// </summary>
    /// <param name="forceRefresh">true = 跳过结果缓存重新拉(界面上的「重新读取」)。</param>
    public static async Task<OpenCodeUsageStats?> TryFetchAsync(
        string range,
        CancellationToken token = default,
        bool forceRefresh = false)
    {
        if (!AppSettings.Current.UseBrowserSessionForModelDetail)
        {
            LastStatus = "未启用(可在本页开启「浏览器会话明细」)";
            return null;
        }

        if (!forceRefresh)
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue(range, out var hit)
                    && DateTimeOffset.Now - hit.At < CacheLifetime)
                {
                    Available = true;
                    LastStatus = $"已读到官方聚合读数({range},{Age(hit.At)}前的缓存)";
                    return hit.Stats;
                }
            }
        }

        string? cookie = BrowserCookieStore.GetCookieHeader("opencode", forceRefresh: false);
        if (cookie is null)
        {
            Available = false;
            LastStatus = BrowserCookieStore.LastError ?? "读不到 opencode.ai 的登录 cookie";
            return null;
        }

        try
        {
            long requests = 0, input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            double cost = 0;
            int workspaces = 0;
            var models = new Dictionary<string, ModelAcc>(StringComparer.Ordinal);
            var days = new Dictionary<string, (long Req, long Tok, double Cost)>(StringComparer.Ordinal);

            using (var doc = await GetJsonAsync("orgs", cookie, null, token))
            {
                foreach (var (id, _) in ParseOrgs(doc.RootElement))
                {
                    workspaces++;
                    using (var summary = await GetJsonAsync($"usage/summary?range={range}", cookie, id, token))
                    {
                        var root = summary.RootElement;
                        requests += LongStr(root, "totalRequests");
                        input += LongStr(root, "totalInputTokens");
                        output += LongStr(root, "totalOutputTokens");
                        cacheRead += LongStr(root, "totalCacheReadTokens");
                        cacheWrite += LongStr(root, "totalCacheWrite5mTokens")
                            + LongStr(root, "totalCacheWrite1hTokens");
                        cost += LongStr(root, "totalCostMicroCents") / MicroCentsPerUsd;
                    }

                    using (var modelDoc = await GetJsonAsync($"usage/models?range={range}", cookie, id, token))
                    {
                        MergeModels(modelDoc.RootElement, models);
                    }

                    using (var dayDoc = await GetJsonAsync($"usage/cost-by-day?range={range}", cookie, id, token))
                    {
                        MergeDays(dayDoc.RootElement, days);
                    }
                }
            }

            if (workspaces == 0)
            {
                Available = false;
                LastStatus = "账号下没有工作区(接口返回了空列表)";
                return null;
            }

            Available = true;
            LastStatus = $"已读到官方聚合读数({range},{workspaces} 个工作区)";
            var stats = new OpenCodeUsageStats(
                range, requests, input, output, cacheRead, cacheWrite, cost,
                models.Select(kv => new OpenCodeModelStat(kv.Key, kv.Value.Provider, kv.Value.Req,
                        kv.Value.In, kv.Value.Out, kv.Value.Cr, kv.Value.Cw, kv.Value.Cost))
                    .OrderByDescending(m => m.TotalTokens)
                    .ToList(),
                days.Select(kv => new OpenCodeDayStat(kv.Key, kv.Value.Req, kv.Value.Tok, kv.Value.Cost))
                    .OrderByDescending(d => d.Day, StringComparer.Ordinal)
                    .ToList(),
                DateTimeOffset.Now);
            lock (CacheGate) Cache[range] = (stats, DateTimeOffset.Now);
            SaveToDisk();
            return stats;
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            // HttpClient.Timeout 到期抛的是 TaskCanceledException(= OperationCanceledException),
            // 不能当成"程序正在退出"往外抛(见 UsageEngine.RunRoundAsync 的说明)。
            // 转成普通异常,交给下面的兜底:有旧读数就退回旧读数。
            throw new CommandCodeApiException(null, "连接 OpenCode 用量接口超时。", ex);
        }
        catch (OperationCanceledException) { throw; }
        catch (CommandCodeApiException ex) when (ex.IsAuthenticationError)
        {
            BrowserCookieStore.Invalidate("opencode");
            Invalidate();
            Available = false;
            LastStatus = "浏览器登录态已过期,请重新登录 opencode.ai";
            return null;
        }
        catch (Exception ex)
        {
            // 拉不到不是世界末日:有旧读数就先用它,并把"多旧"写在状态里(宁可标着时间显示旧数,
            // 也不要让整块变成"读不到")
            if (Cache.TryGetValue(range, out var stale))
            {
                Available = false;
                LastStatus = $"读取失败,退回 {Age(stale.At)}前的读数:" + ex.Message;
                return stale.Stats;
            }
            Available = false;
            LastStatus = "读取官方聚合读数失败:" + ex.Message;
            return null;
        }
    }

    private sealed record ModelAcc(
        string Provider, long Req, long In, long Out, long Cr, long Cw, double Cost);

    private static void MergeModels(JsonElement root, Dictionary<string, ModelAcc> into)
    {
        foreach (var item in Items(root))
        {
            string model = String(item, "model") ?? "(unknown)";
            string provider = String(item, "provider") ?? "";
            long inTok = LongStr(item, "totalInputTokens");
            long outTok = LongStr(item, "totalOutputTokens");
            long cr = LongStr(item, "totalCacheReadTokens");
            long cw = LongStr(item, "totalCacheWrite5mTokens") + LongStr(item, "totalCacheWrite1hTokens");
            double cost = LongStr(item, "totalCostMicroCents") / MicroCentsPerUsd;
            var acc = into.GetValueOrDefault(model, new ModelAcc(provider, 0, 0, 0, 0, 0, 0));
            into[model] = acc with
            {
                Provider = acc.Provider.Length > 0 ? acc.Provider : provider,
                Req = acc.Req + LongStr(item, "totalRequests"),
                In = acc.In + inTok,
                Out = acc.Out + outTok,
                Cr = acc.Cr + cr,
                Cw = acc.Cw + cw,
                Cost = acc.Cost + cost,
            };
        }
    }

    private static void MergeDays(JsonElement root, Dictionary<string, (long Req, long Tok, double Cost)> into)
    {
        if (root.ValueKind != JsonValueKind.Array) return;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string day = String(item, "date") ?? "";
            if (day.Length == 0) continue;
            var acc = into.GetValueOrDefault(day, (0, 0, 0));
            into[day] = (
                acc.Req + LongStr(item, "totalRequests"),
                acc.Tok + LongStr(item, "totalTokens"),
                acc.Cost + LongStr(item, "totalCostMicroCents") / MicroCentsPerUsd);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray();
        }
        return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [];
    }

    private static IEnumerable<(string Id, string Name)> ParseOrgs(JsonElement root)
    {
        var result = new List<(string, string)>();
        foreach (var item in Items(root))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string id = String(item, "id") ?? "";
            if (!id.StartsWith("wrk_", StringComparison.Ordinal)) continue;
            result.Add((id, String(item, "name") ?? ""));
        }
        return result;
    }

    private static async Task<JsonDocument> GetJsonAsync(
        string pathAndQuery, string cookie, string? orgId, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/" + pathAndQuery);
        req.Headers.Add("Cookie", cookie);
        if (orgId is not null) req.Headers.Add("x-org-id", orgId);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CommandCodeApiException(resp.StatusCode, "浏览器登录态已过期,请重新登录 opencode.ai");
        }
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static string? String(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>读一个数值字段。服务端把 BigInt 序列化成**字符串**,这里两种形态都收。</summary>
    private static long LongStr(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.String) return long.TryParse(v.GetString(), out long s) ? s : 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        return 0;
    }
}

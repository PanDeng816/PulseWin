using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// Command Code 的**浏览器会话**通道——用来读只有登录态才开放的逐模型明细。
///
/// **为什么需要它**：Command Code 的 API Key 只够读账户级数字（额度、余额、汇总）。
/// 逐模型、逐时间桶的明细（含缓存读/写 token）在 <c>/internal/usage/charts</c> 下，
/// **API Key 加 Origin/Referer 伪装也是 401，必须有浏览器登录 cookie**。
///
/// **登录会话从哪来**：v1.18.0 起走内嵌 WebView2 登录窗
/// （<see cref="BrowserLoginWindow"/> / <see cref="BrowserLoginTargets.CommandCode"/>），
/// 登录后 cookie 由 <see cref="BrowserCookieStore"/> 以 DPAPI 落盘。以前想"读本机浏览器
/// cookie 库"，那条路已被 Edge/Chrome 的 v20 App-Bound 加密堵死。
///
/// **整条通道是可选的**：读不到 cookie 就安静地返回 null，界面用本地数据兜底并说明原因。
/// 绝不因为它让主界面出问题。
/// </summary>
public static class CommandCodeWebSession
{
    private const string InternalCharts = "internal/usage/charts";
    private const string InternalUsage = "internal/usage";
    /// <summary>浏览器口径的站点(Origin / Referer 用这个)。</summary>
    private const string SiteOrigin = "https://commandcode.ai";
    /// <summary>**接口真正的主机**。2026-10-10 实测:<c>commandcode.ai/internal/usage/charts</c>
    /// 返回 404(Next.js 的页面壳),<c>api.commandcode.ai</c> 同路径才返回 200 + JSON。
    /// 这条以前没验证过——因为拿不到 cookie,整条通道从没真正跑起来过。</summary>
    private const string ApiOrigin = "https://api.commandcode.ai";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>上一次失败/成功的原因（界面显示用）。</summary>
    public static string? LastStatus { get; private set; }

    /// <summary>cookie 是否可用（null = 还没试过）。</summary>
    public static bool? Available { get; private set; }

    /// <summary>缓存的 cookie 头（成功后短时间内复用，避免每次都去解浏览器库）。</summary>
    private static string? _cookieHeader;
    private static DateTimeOffset _cookieAt;

    private static HttpClient CreateClient()
    {
        var client = ProxyHttp.Create(TimeSpan.FromSeconds(25));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("Origin", SiteOrigin);
        client.DefaultRequestHeaders.Add("Referer", SiteOrigin + "/");
        return client;
    }

    /// <summary>
    /// 拉一段窗口内的逐模型缓存明细。<paramref name="from"/>/<paramref name="to"/> 是本地时间。
    /// 返回 null = 这条通道当前不可用（原因见 <see cref="LastStatus"/>）。
    /// </summary>
    public static async Task<List<ModelCacheBucket>?> TryFetchModelCacheAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken token = default)
    {
        // 默认关(见 AppSettings.UseBrowserSessionForModelDetail 的说明:读 cookie 有
        // 被 AV 误判的风险,且浏览器运行时 cookie 库是排他锁的、多半读不到)。
        if (!AppSettings.Current.UseBrowserSessionForModelDetail)
        {
            LastStatus = "未启用(可在本页开启「浏览器会话明细」)";
            return null;
        }

        string? cookie = GetCookieHeader(forceRefresh: false);
        if (cookie is null) return null;

        string query = $"?from={Uri.EscapeDataString(from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}"
            + $"&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}";

        try
        {
            // **必须绝对 URI**:这个 HttpClient 没设 BaseAddress,给相对路径会直接抛
            // InvalidOperationException(这条以前从没跑到过,所以一直没暴露)。
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiOrigin + "/" + InternalCharts + query);
            req.Headers.Add("Cookie", cookie);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // 登录态过期:清掉缓存,下次重解浏览器库(用户重新登录后即可恢复)
                BrowserCookieStore.Invalidate("commandcode");
                Available = false;
                LastStatus = "浏览器登录态已过期,请重新登录 commandcode.ai";
                return null;
            }
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var buckets = ParseCharts(doc.RootElement);
            Available = true;
            LastStatus = buckets.Count > 0
                ? $"已从浏览器会话读到 {buckets.Count} 条逐模型明细"
                : "浏览器会话可用,但这个窗口没有逐模型明细";
            return buckets;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Available = false;
            LastStatus = "读取逐模型明细失败:" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>/internal/usage/charts</c> 的回包。**结构按 goat-gauge 的读法归一**：
    /// 它按 时间桶 × 模型 返回 cacheReadInputTokens / cacheCreationInputTokens 等。
    /// 这里对字段名做宽松匹配（服务端字段大小写/命名会变），解析不出就跳过该行。
    /// </summary>
    private static List<ModelCacheBucket> ParseCharts(JsonElement root)
    {
        var result = new List<ModelCacheBucket>();
        // 回包可能是数组，也可能是 { data: [...] } / { charts: [...] } / { rows: [...] }
        JsonElement rows = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "data", "charts", "rows", "buckets", "items" })
            {
                if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    rows = inner;
                    break;
                }
            }
        }
        if (rows.ValueKind != JsonValueKind.Array) return result;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string? model = Str(row, "model", "modelId", "model_id", "name");
            if (string.IsNullOrWhiteSpace(model)) continue;

            // 字段名以**实测回包**为准(2026-10-10 拿到真 cookie 后核过一条完整响应):
            //   model / provider / timeBucket / requests / tokensIn / tokensOut / tokensTotal /
            //   cacheReadInputTokens / cacheCreationInputTokens / totalCost / inputCost / ...
            // 从前只认 inputTokens/outputTokens/timestamp,与真名全部对不上 ⇒ 四类 token 全 0,
            // 被下面那条"全 0 就跳过"过滤光,明细永远是空的。旧名保留在后面做兼容。
            long cacheRead = Long(row, "cacheReadInputTokens", "cache_read", "cacheRead", "cacheReadTokens");
            long cacheWrite = Long(row, "cacheCreationInputTokens", "cache_write", "cacheWrite", "cacheCreationTokens");
            long input = Long(row, "tokensIn", "inputTokens", "input", "input_tokens");
            long output = Long(row, "tokensOut", "outputTokens", "output", "output_tokens");
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0) continue;

            DateTimeOffset? at = Time(row, "timeBucket", "timestamp", "time", "bucket", "date");
            result.Add(new ModelCacheBucket(
                ModelPrices.DisplayName(model), at, input, cacheWrite, cacheRead, output));
        }
        return result;
    }

    private static string? Str(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static long Long(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (!o.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n64)) return n64;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s)) return s;
        }
        return 0;
    }

    private static DateTimeOffset? Time(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (!o.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long epoch))
            {
                return epoch < 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : DateTimeOffset.FromUnixTimeMilliseconds(epoch);
            }
            if (v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }

    // ————————— 浏览器 cookie 读取 —————————

    /// <summary>拼出 commandcode.ai 的 Cookie 头(cookie 解密与缓存共用
    /// <see cref="BrowserCookieStore"/>)。读不到返回 null。</summary>
    private static string? GetCookieHeader(bool forceRefresh) =>
        BrowserCookieStore.GetCookieHeader("commandcode", forceRefresh);
}

/// <summary>一条"逐模型 / 时间桶"的缓存明细（来自 Command Code 浏览器会话通道）。</summary>
public sealed record ModelCacheBucket(
    string Model,
    DateTimeOffset? At,
    long Input,
    long CacheWrite,
    long CacheRead,
    long Output)
{
    public long Total => Input + CacheWrite + CacheRead + Output;

    /// <summary>缓存命中率 = 缓存读 / 全部输入。</summary>
    public double? CacheHit
    {
        get
        {
            long inputTotal = Input + CacheWrite + CacheRead;
            return inputTotal > 0 ? (double)CacheRead / inputTotal : null;
        }
    }
}

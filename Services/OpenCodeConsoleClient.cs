using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PulseWin;

/// <summary>控制台(网页)给出的一个额度窗口读数。</summary>
/// <param name="Percent">与 opencode.ai 网页**完全同一个公式**算出的整数百分比。</param>
/// <param name="UsedUsd">已用金额(美元)。</param>
/// <param name="LimitUsd">这个窗口的额度上限(美元)。</param>
public sealed record OpenCodeConsoleWindow(
    double Percent,
    double UsedUsd,
    double LimitUsd,
    DateTimeOffset? ResetAt)
{
    /// <summary>未取整的精确百分比(如 10.63),用于界面上的细节展示。</summary>
    public double ExactPercent => LimitUsd > 0 ? UsedUsd / LimitUsd * 100 : 0;
}

/// <summary>一个工作区(网页左上角 go1/go2 下拉里的那一项)的三个额度窗口。</summary>
public sealed record OpenCodeConsoleMeters(
    string WorkspaceId,
    string WorkspaceName,
    OpenCodeConsoleWindow? FiveHour,
    OpenCodeConsoleWindow? Weekly,
    OpenCodeConsoleWindow? Monthly);

/// <summary>
/// OpenCode Go 的**网页口径**额度读数——<c>GET /console/api/go/status</c>。
///
/// **为什么需要它**:API Key 那个端点(<c>/zen/go/v1/usage</c>)只回**向下取整**的
/// 整数百分比,而网页(以及同门的 GoGauge)读的是 <c>access.meters</c> 里的
/// <c>usedMicroCents / limitMicroCents</c> 微美分原值,前端再自己算百分比——
/// 两边取整方式不同,同一个真实用量会差 1 个百分点(网页 11% / API Key 10%)。
/// 想跟网页对得上,就只能走网页这条通道(需要浏览器登录 cookie)。
///
/// 百分比公式照抄网页前端,一字不差:
/// <c>(usedMicroCents * 200 + limitMicroCents) / (limitMicroCents * 2)</c>,
/// 整数除法 = 四舍五入(round half up)。用 <see cref="long"/> 做整数运算,
/// 与网页的 BigInt 结果逐位一致(Go Plus 的月度上限 $240 = 2.4e10 微美分,
/// 乘 200 之后仍远在 long 范围内)。
///
/// 整条通道与明细共用「浏览器会话明细」开关,默认关;读不到就安静返回 null。
/// </summary>
public static class OpenCodeConsoleClient
{
    private const string ApiBase = "https://opencode.ai/console/api";
    private const string Origin = "https://opencode.ai";

    /// <summary>微美分 → 美元。1 微美分 = 1e-6 美分 = 1e-8 美元。</summary>
    private const double MicroCentsPerUsd = 100_000_000d;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>上一次成功/失败的原因(界面与诊断显示用)。</summary>
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
    /// 拉账号下**全部工作区**的额度读数。返回 null = 通道当前不可用(原因见
    /// <see cref="LastStatus"/>)。开关判断在调用方(与明细共用同一个开关)。
    /// </summary>
    public static async Task<IReadOnlyList<OpenCodeConsoleMeters>?> TryFetchMetersAsync(
        CancellationToken token = default)
    {
        string? cookie = BrowserCookieStore.GetCookieHeader("opencode", forceRefresh: false);
        if (cookie is null)
        {
            LastStatus = BrowserCookieStore.LastError ?? "读不到 opencode.ai 的登录 cookie";
            return null;
        }

        try
        {
            var result = new List<OpenCodeConsoleMeters>();
            using (var doc = await GetJsonAsync("orgs", cookie, null, token))
            {
                foreach (var (id, name) in ParseOrgs(doc.RootElement))
                {
                    var meters = await FetchMetersAsync(id, name, cookie, token);
                    if (meters is not null) result.Add(meters);
                }
            }

            if (result.Count == 0)
            {
                LastStatus = "账号下没有可读的工作区额度(接口返回了空列表)";
                return null;
            }

            LastStatus = $"已从网页控制台读到 {result.Count} 个工作区的额度读数";
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (CommandCodeApiException ex) when (ex.IsAuthenticationError)
        {
            BrowserCookieStore.Invalidate("opencode");
            LastStatus = "浏览器登录态已过期,请重新登录 opencode.ai";
            return null;
        }
        catch (Exception ex)
        {
            LastStatus = "读取网页额度失败:" + ex.Message;
            return null;
        }
    }

    private static async Task<OpenCodeConsoleMeters?> FetchMetersAsync(
        string workspaceId, string workspaceName, string cookie, CancellationToken token)
    {
        using var doc = await GetJsonAsync("go/status", cookie, workspaceId, token);
        return ParseStatus(doc.RootElement, workspaceId, workspaceName);
    }

    /// <summary>
    /// 解析一段 go/status 响应。诊断自检(<c>--go-meters</c>)直接拿合成响应喂它,
    /// 好在读不到浏览器 cookie 时也能验住「解析 + 百分比公式」这条最容易出错的路径。
    /// </summary>
    internal static OpenCodeConsoleMeters? ParseStatusForDiagnostics(
        string json, string workspaceId, string workspaceName)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseStatus(doc.RootElement, workspaceId, workspaceName);
    }

    private static OpenCodeConsoleMeters? ParseStatus(JsonElement root, string workspaceId, string workspaceName)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("access", out var access) || access.ValueKind != JsonValueKind.Object)
        {
            // access = null 表示这个工作区没有 Go 订阅(或订阅已过期)——不是错误,跳过
            return null;
        }
        if (!access.TryGetProperty("meters", out var meters) || meters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        DateTimeOffset? periodEnd = ParseDate(Str(access, "endsAt"));
        return new OpenCodeConsoleMeters(
            workspaceId,
            workspaceName,
            ParseMeter(meters, "fiveHour", periodEnd),
            ParseMeter(meters, "week", periodEnd),
            ParseMeter(meters, "month", periodEnd));
    }

    private static OpenCodeConsoleWindow? ParseMeter(JsonElement meters, string name, DateTimeOffset? periodEnd)
    {
        if (!meters.TryGetProperty(name, out var meter) || meter.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long limit = Long(meter, "limitMicroCents");
        if (limit <= 0) return null;
        long used = Long(meter, "usedMicroCents");

        // 与网页前端同一个整数公式:四舍五入(round half up)
        long percent = (used * 200 + limit) / (limit * 2);

        return new OpenCodeConsoleWindow(
            percent,
            used / MicroCentsPerUsd,
            limit / MicroCentsPerUsd,
            ParseDate(Str(meter, "resetsAt")) ?? periodEnd);
    }

    private static IEnumerable<(string Id, string Name)> ParseOrgs(JsonElement root)
    {
        var result = new List<(string, string)>();
        var array = root;
        if (array.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "items", "data", "orgs" })
            {
                if (array.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    array = inner;
                    break;
                }
            }
        }
        if (array.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string id = Str(item, "id") ?? "";
            if (!id.StartsWith("wrk_", StringComparison.Ordinal)) continue;
            result.Add((id, Str(item, "name") ?? ""));
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

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long Long(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s)) return s;
        return 0;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}

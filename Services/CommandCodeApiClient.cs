using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

public interface IUsageProvider
{
    Task<CredentialValidationResult> ValidateCredentialAsync(string apiKey, CancellationToken cancellationToken);
    Task<UsageSnapshot> GetUsageAsync(CredentialCandidate credential, CancellationToken cancellationToken);
}

public sealed class CommandCodeApiClient : IUsageProvider
{
    public static readonly Uri ProductionBaseAddress = new("https://api.commandcode.ai/");

    /// <summary>
    /// GOAT 的月额度池,单位是 credits(服务端口径)。5 小时 cap 14、周 cap 35 都是它的
    /// 分片(20%/50%),三者同一刻度,所以窗口上限不用问服务端。
    /// </summary>
    private const double GoatMonthlyCredits = 70d;

    /// <summary>
    /// GOAT 的窗口读数一律以 **credits** 为单位,不做美元折算。
    ///
    /// 服务端只发 credits(月池 70,且没有模型级额度接口),而"1 credit 值多少美元"取决于
    /// 所选模型的 monthly allowance —— 那是个官方随时会调的数(V4.1 Flash 常态 $40,
    /// 2026-09-11~09-20 曾"限时提升"到 $60,之后又回落)。把显示建立在这个数上,官方一调、
    /// 或换一个 allowance 不同的模型,同一份服务端数据就会被折算成另一个金额,切换日还会
    /// 看到"已用"凭空跳变 —— 而那已经不是服务端给的原始事实了。
    ///
    /// 所以这里只呈现服务端原值:每池的 credits 读数 + 百分比。百分比 = used / 70,只依赖
    /// 固定的池子,与 allowance 无关,换模型、官方调额度都不会变。
    /// </summary>
    private const string CreditUnit = "cr";

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;

    public CommandCodeApiClient(HttpClient httpClient, Func<DateTimeOffset>? utcNow = null)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= ProductionBaseAddress;
        // 30s,不是 15s:实测四个端点里 `billing/subscriptions` 常态 3~4s,但会偶发
        // 超过 15s —— 那一轮就整轮算失败(诊断里见过"连接 Command Code 超时")。
        // 用户感知上,"30 秒内拿到数据"远好于"15 秒就放弃、然后等下一轮"。
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = await GetIdentityAsync(apiKey, cancellationToken);
            return CredentialValidationResult.Valid(identity);
        }
        catch (CommandCodeApiException exception)
        {
            return CredentialValidationResult.Invalid(exception.Message);
        }
    }

    public async Task<UsageSnapshot> GetUsageAsync(
        CredentialCandidate credential,
        CancellationToken cancellationToken)
    {
        // 先并行取身份，再并行取额度与订阅：两者互不依赖，缩短串行等待。
        var identity = await GetIdentityAsync(credential.ApiKey, cancellationToken);
        var organizationQuery = string.IsNullOrWhiteSpace(identity.OrganizationId)
            ? string.Empty
            : $"?orgId={Uri.EscapeDataString(identity.OrganizationId)}";

        // 额度与订阅互不依赖，并行请求以缩短刷新耗时。
        var creditsTask = GetJsonAsync($"alpha/billing/credits{organizationQuery}", credential.ApiKey, cancellationToken);
        var subscriptionTask = GetJsonAsync($"alpha/billing/subscriptions{organizationQuery}", credential.ApiKey, cancellationToken);
        await Task.WhenAll(creditsTask, subscriptionTask);
        using var creditsTaskResult = await creditsTask;
        using var subscriptionTaskResult = await subscriptionTask;

        var creditsRoot = creditsTaskResult.RootElement;
        var subscriptionRoot = subscriptionTaskResult.RootElement;
        var credits = RequireObject(creditsRoot, "credits");
        var subscription = TryObject(subscriptionRoot, "data");

        var planId = ReadString(subscription, "planId")
            ?? ReadString(credits, "planId")
            ?? "unknown";
        var planName = PlanDisplayName(planId);
        var currentPeriodStart = ReadString(subscription, "currentPeriodStart");
        var currentPeriodEnd = ParseDate(ReadString(subscription, "currentPeriodEnd"));
        var monthlyRemaining = ReadDouble(credits, "monthlyCredits");
        var purchasedCredits = ReadDouble(credits, "purchasedCredits") ?? 0d;
        var freeCredits = ReadDouble(credits, "freeCredits") ?? 0d;

        // GOAT 的窗口以 credits 计;其余套餐服务端本来就发美元,原样用。
        string windowUnit = IsGoatPlan(planId) ? CreditUnit : "$";
        var windows = new List<QuotaWindow>
        {
            ParseServerWindow(creditsRoot, "fiveHour", QuotaKind.FiveHour, "5 小时", windowUnit),
            ParseServerWindow(creditsRoot, "weekly", QuotaKind.Weekly, "本周", windowUnit),
        };

        double? periodCost = null;
        long? periodRequests = null;
        long? periodTokens = null;
        try
        {
            var summaryQuery = BuildSummaryQuery(identity.OrganizationId, currentPeriodStart);
            using var summaryDocument = await GetJsonAsync(
                $"alpha/usage/summary{summaryQuery}",
                credential.ApiKey,
                cancellationToken);
            periodCost = ReadDouble(summaryDocument.RootElement, "totalCost");
            periodRequests = ReadLong(summaryDocument.RootElement, "totalCount");
            periodTokens = ReadLong(summaryDocument.RootElement, "totalTokens");
        }
        catch (CommandCodeApiException exception) when (!exception.IsAuthenticationError)
        {
            // Summary is diagnostic only. Billing meters remain authoritative.
        }

        // 月窗口直接由服务端剩余 credits 换算,不再依赖 summary 的花费。
        windows.Add(BuildMonthlyWindow(planId, monthlyRemaining, currentPeriodEnd));

        var studioUrl = $"https://commandcode.ai/{Uri.EscapeDataString(identity.StudioSlug)}/settings/usage";
        return new UsageSnapshot(
            planId,
            planName,
            identity.AccountLabel,
            credential.Source,
            windows,
            purchasedCredits,
            freeCredits,
            periodCost,
            periodRequests,
            periodTokens,
            _utcNow(),
            studioUrl);
    }

    private async Task<ApiIdentity> GetIdentityAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("alpha/whoami", apiKey, cancellationToken);
        var root = document.RootElement;
        var user = TryObject(root, "user");
        var organization = TryObject(root, "org");

        var account = ReadString(user, "userName")
            ?? ReadString(user, "name")
            ?? ReadString(organization, "login")
            ?? "Command Code";
        var organizationId = ReadString(organization, "id");
        var studioSlug = ReadString(organization, "login")
            ?? ReadString(user, "userName")
            ?? account;

        return new ApiIdentity(account, organizationId, studioSlug);
    }

    private async Task<JsonDocument> GetJsonAsync(
        string endpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("GOATGoUsageMonitor/1.0.4");

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusException(response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommandCodeApiException(null, "连接 Command Code 超时。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CommandCodeApiException(null, "无法连接 Command Code，请检查网络。", exception);
        }
        catch (JsonException exception)
        {
            throw new CommandCodeApiException(null, "Command Code 返回了无法识别的数据。", exception);
        }
    }

    private static CommandCodeApiException CreateStatusException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new CommandCodeApiException(statusCode, "API Key 无效或已过期。"),
        HttpStatusCode.Forbidden => new CommandCodeApiException(statusCode, "当前 API Key 无权读取额度。"),
        HttpStatusCode.TooManyRequests => new CommandCodeApiException(statusCode, "Command Code 暂时限制了查询，请稍后重试。"),
        _ when (int)statusCode >= 500 => new CommandCodeApiException(statusCode, "Command Code 服务暂时不可用。"),
        _ => new CommandCodeApiException(statusCode, $"Command Code 请求失败（HTTP {(int)statusCode}）。")
    };

    private static QuotaWindow ParseServerWindow(
        JsonElement root,
        string propertyName,
        QuotaKind kind,
        string label,
        string unit)
    {
        var limits = TryObject(root, "windowLimits");
        var limited = ReadBoolean(limits, "limited") ?? true;
        var window = TryObject(limits, propertyName);
        var used = ReadDouble(window, "used");
        var cap = ReadDouble(window, "cap");
        var resetAt = ParseResetAt(window, "resetAt");
        var available = limited && used is not null && cap is > 0;

        return new QuotaWindow(
            kind,
            label,
            used,
            cap,
            resetAt,
            available,
            available ? null : limited ? "服务端暂未返回该窗口" : "当前套餐不受此窗口限制",
            unit);
    }

    private QuotaWindow BuildMonthlyWindow(
        string planId,
        double? monthlyRemaining,
        DateTimeOffset? resetAt)
    {
        if (IsGoatPlan(planId) && monthlyRemaining is not null)
        {
            // 服务端只发"剩余 credits"、不发上限。池子固定 70 credits —— 5 小时 14、周 35
            // 正是它的 20%/50%,所以上限不必反推。
            // 旧写法拿 summary 的 totalCost 反推(剩余 + 花费),但那是 API 标价口径的记录,
            // 换 V4.1 Flash 后比池子实际扣减虚高,分母被撑到 77+,百分比偏大约 4 个点。
            // 全是服务端原值:已用/满额都是 credits(不折算美元,见 CreditUnit 的说明)。
            double creditsUsed = Math.Clamp(
                GoatMonthlyCredits - monthlyRemaining.Value, 0d, GoatMonthlyCredits);
            return new QuotaWindow(
                QuotaKind.Monthly,
                "本月",
                creditsUsed,
                GoatMonthlyCredits,
                resetAt,
                true,
                null,
                CreditUnit);
        }

        return new QuotaWindow(
            QuotaKind.Monthly,
            "本月",
            null,
            null,
            resetAt,
            false,
            monthlyRemaining is null
                ? "服务端暂未返回月余额"
                : $"剩余 {monthlyRemaining.Value:0.##} credits；未知套餐不推算上限");
    }

    private static string BuildSummaryQuery(string? organizationId, string? since)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            parts.Add($"orgId={Uri.EscapeDataString(organizationId)}");
        }

        if (!string.IsNullOrWhiteSpace(since))
        {
            parts.Add($"since={Uri.EscapeDataString(since)}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    private static bool IsGoatPlan(string planId) =>
        planId.Replace('_', '-').StartsWith("individual-goat", StringComparison.OrdinalIgnoreCase);

    private static string PlanDisplayName(string planId) => planId.Replace('_', '-').ToLowerInvariant() switch
    {
        var value when value.StartsWith("individual-goat", StringComparison.Ordinal) => "GOAT",
        var value when value.StartsWith("individual-go", StringComparison.Ordinal) => "Go",
        var value when value.StartsWith("individual-pro", StringComparison.Ordinal) => "Pro",
        var value when value.StartsWith("individual-max", StringComparison.Ordinal) => "Max",
        var value when value.StartsWith("individual-ultra", StringComparison.Ordinal) => "Ultra",
        _ => planId == "unknown" ? "未知套餐" : planId
    };

    private static JsonElement RequireObject(JsonElement parent, string propertyName)
    {
        var value = TryObject(parent, propertyName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new CommandCodeApiException(null, $"Command Code 数据缺少 {propertyName} 字段。");
        }

        return value;
    }

    private static JsonElement TryObject(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? ReadDouble(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static long? ReadLong(JsonElement parent, string propertyName)
    {
        var number = ReadDouble(parent, propertyName);
        return number is null ? null : Convert.ToInt64(number.Value);
    }

    private static bool? ReadBoolean(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? ParseResetAt(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            return epoch < 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : DateTimeOffset.FromUnixTimeMilliseconds(epoch);
        }

        return value.ValueKind == JsonValueKind.String ? ParseDate(value.GetString()) : null;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}

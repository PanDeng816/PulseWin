using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

public sealed class OpenCodeGoApiClient : IUsageProvider
{
    public static readonly Uri ProductionBaseAddress = new("https://opencode.ai/");
    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;

    public OpenCodeGoApiClient(HttpClient httpClient, Func<DateTimeOffset>? utcNow = null)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= ProductionBaseAddress;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CredentialValidationResult> ValidateCredentialAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            await GetUsageAsync(new CredentialCandidate(CredentialSource.ManualEntry, apiKey), cancellationToken);
            return CredentialValidationResult.Valid(new ApiIdentity("OpenCode Go", null, "opencode.ai"));
        }
        catch (CommandCodeApiException exception)
        {
            return CredentialValidationResult.Invalid(exception.Message);
        }
    }

    public async Task<UsageSnapshot> GetUsageAsync(CredentialCandidate credential, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(credential.ApiKey, cancellationToken);
        var root = document.RootElement;
        if (!TryObject(root, "usage", out var usage))
        {
            throw new CommandCodeApiException(null, "OpenCode Go 返回了无法识别的额度数据。");
        }

        return new UsageSnapshot(
            "opencode-go",
            "GO",
            "OpenCode Go",
            credential.Source,
            [
                ParseWindow(usage, "rolling", QuotaKind.FiveHour, "5 小时"),
                ParseWindow(usage, "weekly", QuotaKind.Weekly, "本周"),
                ParseWindow(usage, "monthly", QuotaKind.Monthly, "本月")
            ],
            0,
            0,
            null,
            null,
            null,
            _utcNow(),
            "https://opencode.ai");
    }

    private async Task<JsonDocument> GetJsonAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "zen/go/v1/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("GOATGoUsageMonitor/1.0.4");

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusException(response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommandCodeApiException(null, "连接 OpenCode Go 超时。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CommandCodeApiException(null, "无法连接 OpenCode Go，请检查网络。", exception);
        }
        catch (JsonException exception)
        {
            throw new CommandCodeApiException(null, "OpenCode Go 返回了无法识别的数据。", exception);
        }
    }

    private static QuotaWindow ParseWindow(JsonElement usage, string propertyName, QuotaKind kind, string label)
    {
        if (!TryObject(usage, propertyName, out var window))
        {
            return new QuotaWindow(kind, label, null, null, null, false, "服务端暂未返回该窗口", "%");
        }

        var percent = ReadDouble(window, "percent");
        var status = ReadString(window, "status");
        var resetAt = ParseDate(ReadString(window, "resetsAt"));
        var rateLimited = string.Equals(status, "rate-limited", StringComparison.OrdinalIgnoreCase);
        var available = percent is not null || rateLimited;
        var used = rateLimited ? 100d : percent;
        return new QuotaWindow(
            kind,
            label,
            used,
            available ? 100d : null,
            resetAt,
            available,
            rateLimited ? "额度已用尽" : available ? null : "服务端暂未返回用量百分比",
            "%");
    }

    private static CommandCodeApiException CreateStatusException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new CommandCodeApiException(statusCode, "OpenCode Go API Key 无效或已过期。"),
        HttpStatusCode.Forbidden => new CommandCodeApiException(statusCode, "当前账户未订阅 OpenCode Go 或无权读取额度。"),
        HttpStatusCode.TooManyRequests => new CommandCodeApiException(statusCode, "OpenCode Go 暂时限制了查询，请稍后重试。"),
        _ when (int)statusCode >= 500 => new CommandCodeApiException(statusCode, "OpenCode Go 服务暂时不可用。"),
        _ => new CommandCodeApiException(statusCode, $"OpenCode Go 请求失败（HTTP {(int)statusCode}）。")
    };

    private static bool TryObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}

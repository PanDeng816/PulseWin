using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

/// <summary>
/// DeepSeek 预付余额。
///
/// 唯一一条路由,而且是官方文档里的:
/// <code>GET https://api.deepseek.com/user/balance</code>
/// 返回 <c>{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00",
/// "granted_balance":"10.00","topped_up_balance":"100.00"}]}</code>。
///
/// **没有额度、没有窗口、没有重置、也没有消费历史接口**。别的数据源至少会报一个
/// 百分比,这一个只报钱。所以环要画百分比就必须自己定分母,三种来源见
/// <see cref="BalanceBasis"/>——本程序观察到的、干脆没有、或用户自己填的。
///
/// 所有数字(包括金额)都以**字符串**下发,在这里解析掉,下游不必知道。
/// </summary>
public sealed class DeepSeekApiClient : IUsageProvider
{
    public static readonly Uri ProductionBaseAddress = new("https://api.deepseek.com/");

    /// <summary>用于自动发现的环境变量(与官方 SDK 惯例一致)。</summary>
    public const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";

    /// <summary>DeepSeek 官方文档里与 chat completions 并列的余额接口。</summary>
    private const string BalanceEndpoint = "user/balance";

    private readonly HttpClient _httpClient;
    private readonly DeepSeekBaseline _baseline;
    private readonly DeepSeekLedger _ledger;
    private readonly Func<DateTimeOffset> _utcNow;

    public DeepSeekApiClient(
        HttpClient httpClient,
        DeepSeekBaseline baseline,
        DeepSeekLedger ledger,
        Func<DateTimeOffset>? utcNow = null)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= ProductionBaseAddress;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _baseline = baseline;
        _ledger = ledger;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await GetUsageAsync(new CredentialCandidate(CredentialSource.ManualEntry, apiKey), cancellationToken);
            return CredentialValidationResult.Valid(new ApiIdentity("DeepSeek", null, "deepseek.com"));
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
        using var document = await GetJsonAsync(credential.ApiKey, cancellationToken);
        var root = document.RootElement;

        var isAvailable = ReadBoolean(root, "is_available");
        var purses = ReadPurses(root);
        if (purses.Count == 0)
        {
            throw new CommandCodeApiException(null, "DeepSeek 未返回可读余额。");
        }

        var settings = AppSettings.Current;
        var purse = SelectPurse(purses, settings.DeepSeekCurrency)
            ?? throw new CommandCodeApiException(null, "DeepSeek 未返回可读余额。");

        var now = _utcNow();

        // 峰值在每种模式下都推进:以后切到"自上次充值"时,峰值已经在那儿了,
        // 而不是从切换那一刻的余额重新开始。
        var marks = _baseline.Load();
        var previous = marks.GetValueOrDefault(purse.Currency);
        var mark = DeepSeekBaseline.Advanced(previous, purse.Total, now);
        if (previous is null || previous.Peak != mark.Peak)
        {
            marks[purse.Currency] = mark;
            _baseline.Save(marks);
        }

        // 账本:API 不给消费历史,"今日消耗"只能靠这样一小时一个点自己攒
        _ledger.Record(purse.Currency, purse.Total, now);

        var window = BuildWindow(purse, settings, mark, isAvailable, now);

        return new UsageSnapshot(
            "deepseek-balance",
            "DeepSeek",
            purse.Currency,
            credential.Source,
            [window],
            0,
            0,
            null,
            null,
            null,
            now,
            "https://platform.deepseek.com/usage");
    }

    /// <summary>
    /// 按所选基准造出唯一的那一池。三种模式产出同一个 <see cref="QuotaKind.Balance"/> 池,
    /// 区别只在有没有分母,以及分母来自哪里。
    /// </summary>
    private static QuotaWindow BuildWindow(
        Purse purse,
        AppSettings settings,
        DeepSeekBaseline.Mark mark,
        bool? isAvailable,
        DateTimeOffset now)
    {
        double? used = null;
        double? cap = null;
        string? estimateFrom = null;
        string? note = null;

        switch (settings.DeepSeekBasis)
        {
            case BalanceBasis.SinceTopUp:
                var fraction = DeepSeekBaseline.UsedFraction(purse.Total, mark.Peak);
                if (fraction is { } f)
                {
                    used = mark.Peak - purse.Total;
                    cap = mark.Peak;
                    estimateFrom = "自上次充值";
                }
                else
                {
                    // 峰值为 0:这个账户从来没有过额度,不是花光了。
                    note = "开始观察";
                }
                break;

            case BalanceBasis.Budget:
                if (settings.DeepSeekBudget is { } budget && budget > 0 && double.IsFinite(budget))
                {
                    used = Math.Clamp(budget - purse.Total, 0d, budget);
                    cap = budget;
                    estimateFrom = "自设预算";
                }
                else
                {
                    note = "未设置预算";
                }
                break;

            case BalanceBasis.BalanceOnly:
            default:
                note = null;
                break;
        }

        // 只有 DeepSeek 自己的 is_available 能说"花光了"。自己填的预算走到 100%
        // 时账户很可能还能付钱,那是用户画的线,不是 DeepSeek 的判定。
        bool spent = isAvailable == false;

        if (spent)
        {
            // 账户已无法支付:无论分母来自哪里都让它读到满,环才会整圈报红。
            cap ??= purse.Total;
            used = cap;
        }

        return new QuotaWindow(
            QuotaKind.Balance,
            "余额",
            used,
            cap,
            ResetAt: null,
            IsAvailable: true,
            Note: note,
            Unit: Symbol(purse.Currency),
            Amount: purse.Total,
            EstimateFrom: estimateFrom,
            GrantedAmount: purse.Granted,
            ToppedUpAmount: purse.ToppedUp);
    }

    // ————————————————— 读取回包 —————————————————

    private sealed record Purse(
        string Currency,
        double Total,
        double? Granted,
        double? ToppedUp);

    private static List<Purse> ReadPurses(JsonElement root)
    {
        var result = new List<Purse>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("balance_infos", out var infos) ||
            infos.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object) continue;
            var currency = ReadString(info, "currency");
            var total = Money(ReadString(info, "total_balance"));
            if (string.IsNullOrWhiteSpace(currency) || total is null) continue;

            result.Add(new Purse(
                currency.Trim().ToUpperInvariant(),
                total.Value,
                Money(ReadString(info, "granted_balance")),
                Money(ReadString(info, "topped_up_balance"))));
        }

        return result;
    }

    /// <summary>
    /// 环跟着哪个币种。回包是**数组**,一个账户可以同时持有 CNY 和 USD;
    /// 两者不能相加,也不能跨币种比大小(¥100 和 $10 不是一回事)。
    /// 顺序:用户选的 → 第一个有钱的 → 第一个。
    /// </summary>
    private static Purse? SelectPurse(List<Purse> purses, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var chosen = purses.FirstOrDefault(p =>
                string.Equals(p.Currency, preferred.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen is not null) return chosen;
        }

        return purses.FirstOrDefault(p => p.Total > 0) ?? purses[0];
    }

    /// <summary>金额以字符串下发;缺失或无法解析一律当作**缺失**而不是 0——
    /// 余额被读成 0 会画出满红环并报告"账户已花光"。</summary>
    private static double? Money(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static string Symbol(string currency) => currency.ToUpperInvariant() switch
    {
        "CNY" => "¥",
        "USD" => "$",
        _ => currency
    };

    private async Task<JsonDocument> GetJsonAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BalanceEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("PulseWin/1.1");

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
            throw new CommandCodeApiException(null, "连接 DeepSeek 超时。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CommandCodeApiException(null, "无法连接 DeepSeek,请检查网络。", exception);
        }
        catch (JsonException exception)
        {
            throw new CommandCodeApiException(null, "DeepSeek 返回了无法识别的数据。", exception);
        }
    }

    private static CommandCodeApiException CreateStatusException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new CommandCodeApiException(statusCode, "DeepSeek API Key 无效或已过期。"),
        HttpStatusCode.Forbidden => new CommandCodeApiException(statusCode, "该 DeepSeek Key 无权读取余额。"),
        HttpStatusCode.TooManyRequests => new CommandCodeApiException(statusCode, "DeepSeek 暂时限制了查询,请稍后重试。"),
        _ when (int)statusCode >= 500 => new CommandCodeApiException(statusCode, "DeepSeek 服务暂时不可用。"),
        _ => new CommandCodeApiException(statusCode, $"DeepSeek 请求失败(HTTP {(int)statusCode})。")
    };

    private static string? ReadString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}

using System.IO;
using System.Net;

namespace PulseWin;

public enum QuotaKind
{
    FiveHour,
    Weekly,
    Monthly,
    /// <summary>预付余额(DeepSeek)。不是额度池:没有上限、没有重置周期。</summary>
    Balance
}

public enum MonitorSource
{
    CommandCodeGoat,
    OpenCodeGo,
    DeepSeek
}

/// <summary>
/// 余额型数据源(DeepSeek)的"分母"从哪来。DeepSeek 只发余额金额,不发任何额度,
/// 所以环要画百分比就必须自己选一个基准。
/// </summary>
public enum BalanceBasis
{
    /// <summary>以本程序观察到的历史最高余额为满分(余额上涨只可能是充值)。</summary>
    SinceTopUp,
    /// <summary>不画百分比,直接在环上显示余额金额。</summary>
    BalanceOnly,
    /// <summary>以用户自己填的预算为满分。</summary>
    Budget
}

public enum CredentialSource
{
    Environment,
    CliAuthFile,
    OpenCodeAuthFile,
    ProtectedStore,
    ManualEntry
}

public sealed record CredentialCandidate(CredentialSource Source, string ApiKey)
{
    public string SourceLabel => Source switch
    {
        CredentialSource.Environment => "环境变量",
        CredentialSource.CliAuthFile => "Command Code CLI",
        CredentialSource.OpenCodeAuthFile => "OpenCode",
        CredentialSource.ProtectedStore => "Windows 加密存储",
        CredentialSource.ManualEntry => "手动输入",
        _ => "未知来源"
    };
}

public sealed record QuotaWindow(
    QuotaKind Kind,
    string Label,
    double? Used,
    double? Cap,
    DateTimeOffset? ResetAt,
    bool IsAvailable,
    string? Note = null,
    string Unit = "$",
    /// <summary>该池的当前金额读数(余额型数据源用)。与 Used/Cap 无关,单独携带。</summary>
    double? Amount = null,
    /// <summary>分母是估算出来的(余额型数据源),卡片要标明它来自哪里。</summary>
    string? EstimateFrom = null,
    /// <summary>赠送余额(DeepSeek 回包里的 granted_balance)。</summary>
    double? GrantedAmount = null,
    /// <summary>自己充值的余额(DeepSeek 回包里的 topped_up_balance)。</summary>
    double? ToppedUpAmount = null)
{
    public double? Remaining => Used is not null && Cap is not null
        ? Math.Max(0, Cap.Value - Used.Value)
        : null;

    public double? Percent => Used is not null && Cap is > 0
        ? Math.Clamp(Used.Value / Cap.Value * 100d, 0d, 100d)
        : null;

    /// <summary>这一池是否有可用读数(有百分比,或至少有一个金额)。</summary>
    public bool HasReading => IsAvailable && (Percent is not null || Amount is not null);
}

public sealed record UsageSnapshot(
    string PlanId,
    string PlanName,
    string AccountLabel,
    CredentialSource CredentialSource,
    List<QuotaWindow> Windows,
    double PurchasedCredits,
    double FreeCredits,
    double? PeriodCost,
    long? PeriodRequests,
    long? PeriodTokens,
    DateTimeOffset FetchedAt,
    string StudioUsageUrl)
{
    public double ExtraCredits => Math.Max(0, PurchasedCredits) + Math.Max(0, FreeCredits);

    public QuotaWindow? Find(QuotaKind kind) => Windows.FirstOrDefault(window => window.Kind == kind);
}

public sealed record ApiIdentity(string AccountLabel, string? OrganizationId, string StudioSlug);

public sealed record CredentialValidationResult(bool IsValid, ApiIdentity? Identity, string? ErrorMessage)
{
    public static CredentialValidationResult Valid(ApiIdentity identity) => new(true, identity, null);
    public static CredentialValidationResult Invalid(string message) => new(false, null, message);
}

public sealed class CommandCodeApiException : Exception
{
    public CommandCodeApiException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
    public bool IsAuthenticationError => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

public enum ConnectionState
{
    Loading,
    Connected,
    Stale,
    AuthenticationRequired,
    Error
}

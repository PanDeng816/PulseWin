using System.IO;
using System.Net;

namespace PulseWin;

public enum QuotaKind
{
    FiveHour,
    Weekly,
    Monthly
}

public enum MonitorSource
{
    CommandCodeGoat,
    OpenCodeGo
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
    string Unit = "$")
{
    public double? Remaining => Used is not null && Cap is not null
        ? Math.Max(0, Cap.Value - Used.Value)
        : null;

    public double? Percent => Used is not null && Cap is > 0
        ? Math.Clamp(Used.Value / Cap.Value * 100d, 0d, 100d)
        : null;
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

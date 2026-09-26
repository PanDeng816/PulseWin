namespace PulseWin;

/// <summary>
/// 一个数据源的**静态元数据**——把"这个源叫什么、月池怎么叫、快照与凭据文件在哪、
/// 没配凭据时提示什么"从各处的 if/switch 里收拢到一张表。
///
/// **为什么要有它**:从前加一个源要改六处(设置里的三个布尔、六种顺序排列的常量、
/// 数据源页三张硬编码卡、<see cref="SnapshotSource.LoadAll"/> 的三个分支、
/// <see cref="UsageEngine"/> 的四路 switch、环色三个字段)。源一多,那六处里任何
/// 一处漏改都是静默的错(少一个环、顺序对不上、凭据读不到)。现在只加这里一条。
/// </summary>
/// <param name="Id">引擎内部的强类型键(字典/退避计数用)。</param>
/// <param name="Key">设置与快照用的稳定短键(goat / opencode / deepseek),落盘不随显示名变。</param>
/// <param name="DisplayName">设置页里的完整名(如 "Command Code GOAT")。</param>
/// <param name="RailName">rail 卡片标题(如 "GOAT"、"GO")。</param>
/// <param name="MonthlyLabel">月池的叫法(总额度 / 本月 / 余额)。</param>
/// <param name="IsBalance">余额型:rail 上画金额而非百分比,卡片另带"今日消耗"柱图。</param>
/// <param name="CredentialHelp">未找到凭据时,设置页给出的获取途径提示。</param>
/// <param name="SnapshotFile">快照文件(从 <see cref="AppDataPaths"/> 取,不重复写文件名)。</param>
/// <param name="CredentialFile">手动保存的 Key 落盘位置。</param>
public sealed record SourceDescriptor(
    MonitorSource Id,
    string Key,
    string DisplayName,
    string RailName,
    string MonthlyLabel,
    bool IsBalance,
    string CredentialHelp,
    Func<AppDataPaths, string> SnapshotFile,
    Func<AppDataPaths, string> CredentialFile);

/// <summary>
/// 本机支持的**全部数据源**的一张表。顺序即界面上的默认顺序。
/// <see cref="All"/> 是唯一的真相来源:设置存取、快照加载、引擎同步、
/// 设置页生成都从这里取,不再各自硬编码。
/// </summary>
public static class SourceCatalog
{
    public static IReadOnlyList<SourceDescriptor> All { get; } = new SourceDescriptor[]
    {
        new(
            Id: MonitorSource.CommandCodeGoat,
            Key: "goat",
            DisplayName: "Command Code GOAT",
            RailName: "GOAT",
            MonthlyLabel: "总额度",
            IsBalance: false,
            CredentialHelp: "未找到可用凭据:可输入 Command Code API Key(或登录过 Command Code CLI 后重启本程序自动读取)。",
            SnapshotFile: p => p.SnapshotFile,
            CredentialFile: p => p.CredentialFile),
        new(
            Id: MonitorSource.OpenCodeGo,
            Key: "opencode",
            DisplayName: "OpenCode Go",
            RailName: "GO",
            MonthlyLabel: "本月",
            IsBalance: false,
            CredentialHelp: "未找到可用凭据:可输入 OpenCode Go API Key(或本机 OpenCode auth.json 里有 opencode-go 登录态时自动读取)。",
            SnapshotFile: p => p.OpenCodeSnapshotFile,
            CredentialFile: p => p.OpenCodeCredentialFile),
        new(
            Id: MonitorSource.DeepSeek,
            Key: "deepseek",
            DisplayName: "DeepSeek",
            RailName: "DeepSeek",
            MonthlyLabel: "余额",
            IsBalance: true,
            CredentialHelp: "未找到凭据:输入 DeepSeek API Key(在 platform.deepseek.com 控制台创建)。",
            SnapshotFile: p => p.DeepSeekSnapshotFile,
            CredentialFile: p => p.DeepSeekCredentialFile),
    };

    /// <summary>已知的稳定短键(落盘与查找用)。</summary>
    public static IReadOnlyList<string> Keys { get; } = All.Select(s => s.Key).ToArray();

    public static bool IsKnownKey(string? key) =>
        key is not null && All.Any(s => s.Key == key);

    /// <summary>按短键查;未知键返回 null(调用方决定是忽略还是兜底)。</summary>
    public static SourceDescriptor? ByKey(string? key) =>
        key is null ? null : All.FirstOrDefault(s => s.Key == key);

    /// <summary>按强类型键查(引擎里用)。</summary>
    public static SourceDescriptor ById(MonitorSource id) => All.First(s => s.Id == id);
}

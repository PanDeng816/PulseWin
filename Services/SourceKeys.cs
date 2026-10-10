namespace PulseWin;

/// <summary>
/// 源键与「账号实例键」的解析。
///
/// 一个源可以挂**多个账号**:OpenCode Go 的每个工作区(workspace)是一把独立的
/// API Key、一个独立的额度池(官方控制台里就是 go1/go2 那个下拉)。主账号沿用
/// 源键(opencode),附加账号用「源键:编号」(opencode:2、opencode:3…)。
/// 编号是**稳定标识**——Key 重新生成、标签改名之后,环顺序与显示状态仍跟着它。
/// 其余源只有主账号,键就是源键本身。
///
/// 注意键的分隔符是「:」——设置、快照文件名、托盘告警键里都是它。
/// </summary>
public static class SourceKeys
{
    public const char Separator = ':';

    /// <summary>OpenCode Go 的源键(唯一支持多账号的源)。</summary>
    public const string OpenCode = "opencode";

    /// <summary>
    /// 拆出 (源键, 实例号)。主账号实例号 = 1;不是合法键(未知源、编号 &lt; 2)返回 null。
    /// </summary>
    public static (string BaseKey, int Instance)? TryParse(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        int colon = key.IndexOf(Separator);
        if (colon < 0)
            return SourceCatalog.IsKnownKey(key) ? (key, 1) : null;

        string baseKey = key[..colon];
        if (!SourceCatalog.IsKnownKey(baseKey)) return null;
        if (!int.TryParse(key[(colon + 1)..], out int instance) || instance < 2) return null;
        return (baseKey, instance);
    }

    /// <summary>实例键:主账号就是源键,附加账号带 :n 后缀。</summary>
    public static string InstanceKey(string baseKey, int instance) =>
        instance <= 1 ? baseKey : $"{baseKey}{Separator}{instance}";

    /// <summary>剥回源键(已经是源键则原样返回)。</summary>
    public static string BaseKey(string key)
    {
        int colon = key.IndexOf(Separator);
        return colon < 0 ? key : key[..colon];
    }

    /// <summary>源键或实例键都算「已知」——可见性列表与环顺序的过滤用这条,
    /// 不能再收紧成 <see cref="SourceCatalog.IsKnownKey"/>(那会把附加账号静默丢掉)。</summary>
    public static bool IsKnown(string? key) => TryParse(key) is not null;
}

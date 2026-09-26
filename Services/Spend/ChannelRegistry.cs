using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 一个"套餐/渠道"（channel）——用户理解的订阅单位，比如 Command Code GOAT、OpenCode Go、
/// DeepSeek、BigModel 包月。它是**用量归属的单位**：同一个模型走不同渠道就是不同的账。
///
/// **为什么要单独建一层**：本机用量记录里只有 provider_id（ZCode 是 UUID 或
/// <c>builtin:xxx</c> 这类字符串，OpenCode 是 <c>opencode-go</c>）。直接拿它当分组键
/// 会显示成 <c>3ed88ac7-8466-4205-…</c> 这种没人看得懂的东西。这层负责把 id 翻成名字、
/// 并把同属一个套餐的多个 id 归到一起。
/// </summary>
public sealed record ChannelInfo(
    /// <summary>归属键（用于把用量记录分组；同一套餐的多个 provider id 指向它）。</summary>
    string Key,
    /// <summary>显示名（如 "Command Code GOAT"）。</summary>
    string Name,
    /// <summary>来源客户端（ZCode / OpenCode），显示在副标题。</summary>
    string Agent,
    /// <summary>这个渠道对应的数据源键（goat / opencode / deepseek / null=未知）。</summary>
    string? SourceKey,
    /// <summary>本机见过这个渠道的哪些 provider id。</summary>
    IReadOnlyList<string> ProviderIds,
    /// <summary>
    /// 客户端配置里这个渠道是否启用（订阅中）。null = 配置里没记/无从判断。
    /// **未启用不代表没有历史用量**——用户可能只是这个月没续订，过阵子会续。
    /// 界面据此显示"未启用",但历史数据照旧展示。
    /// </summary>
    bool? Enabled = null);

/// <summary>
/// 渠道注册表：provider_id → 套餐。**先读 ZCode 自己的 provider 配置**
/// （<c>~\.zcode\v2\provider_config.json</c> 里有 providerId→providerName 的权威映射），
/// 读不到再退回内置规则（UUID 无法自带名字）。
///
/// 全部读不到也不影响：未知 id 会被归成一个"未知渠道"分组并原样显示 id，
/// 宁可难看也不丢数据。
/// </summary>
public sealed class ChannelRegistry
{
    /// <summary>
    /// 内置的 id/前缀 → 渠道规则（覆盖 CLI 配置里没有的、以及同一套餐的多种写法）。
    ///
    /// **顺序有意义**：前缀匹配先命中者胜，所以更具体的写在前面。
    /// 这几个"同一套餐两种写法"必须归到同一个名字，否则界面上会同一个套餐裂成好几行：
    /// <c>builtin:bigmodel-start-plan</c> 与 <c>account:bigmodel-start-plan</c> 都是智谱包月；
    /// <c>opencode-go</c> / <c>opencode-go-glm</c> / <c>opencode-go-responses</c> 都是 OpenCode Go。
    /// </summary>
    private static readonly (string Match, string Name, string? SourceKey)[] BuiltinRules =
    [
        // DSH(DeepSeek Harness)不把 provider 名字写在能被这里读到的配置文件里,
        // 它用的是**路由名**(如 cordis.patch.yml 里的 commandcode-goat)。
        // 不加这三条,DSH 的用量会显示成"渠道 commandco"、并和 ZCode 的 Command Code GOAT
        // 裂成两行——同一份额度两个账,用户看到的就是"金额对不上"。
        ("commandcode-goat", "Command Code GOAT", "goat"),
        ("commandcode", "Command Code GOAT", "goat"),
        ("goat", "Command Code GOAT", "goat"),
        // DSH 也可以直接走官方 DeepSeek 通道(路由名就叫 deepseek),那条路的额度是
        // DeepSeek 余额而不是 GOAT 池——不归到 DeepSeek 桶会多出一行"渠道 deepseek"。
        ("deepseek", "DeepSeek", "deepseek"),
        ("bigmodel-start-plan", "BigModel 包月", "bigmodel"),
        ("bigmodel", "BigModel 包月", "bigmodel"),
        // OpenCode Go 的几种路由前缀（Responses / glm 变体也在内）
        ("opencode-go", "OpenCode Go", "opencode"),
        ("opencode", "OpenCode Go", "opencode"),
        // OpenCode Go 曾经用过的 UUID 配置 id（2026-08 那阵配的是
        // opencode.ai/zen/go/v1 + deepseek-v4-flash）。**已核实**：它在旧
        // config.json 备份里的名字就是 "opencode go"，所以归到 OpenCode Go，
        // 不能当成一个陌生的新渠道（那会让用户看到"渠道 9c477982"却不知道是什么）。
        ("9c477982-bc8f-4fda-a3f6-21e988358f5f", "OpenCode Go", "opencode"),
    ];

    private static ChannelRegistry? _current;
    private static readonly object Gate = new();

    /// <summary>providerId → 渠道信息。</summary>
    private readonly Dictionary<string, ChannelInfo> _byProviderId = new(StringComparer.Ordinal);
    private readonly List<ChannelInfo> _channels = [];

    private ChannelRegistry() { }

    public static ChannelRegistry Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (Gate) return _current ??= Load();
        }
    }

    public static void Invalidate()
    {
        lock (Gate) _current = null;
    }

    private static ChannelRegistry Load()
    {
        var registry = new ChannelRegistry();
        // 1) 先读 ZCode 的 provider 配置(权威名字)
        try
        {
            registry.ReadZCodeProviderConfig();
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取 ZCode provider 配置失败,渠道名退化为内置规则", ex);
        }
        return registry;
    }

    private static string ZCodeProviderConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zcode", "v2", "provider_config.json");

    private void ReadZCodeProviderConfig()
    {
        string path = ZCodeProviderConfigPath;
        if (!File.Exists(path)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("config", out var config)) return;
        if (!config.TryGetProperty("providerConfigRules", out var rules)) return;
        if (!rules.TryGetProperty("providerRules", out var providers)
            || providers.ValueKind != JsonValueKind.Array) return;

        foreach (var p in providers.EnumerateArray())
        {
            string? id = Str(p, "providerId");
            string? name = Str(p, "providerName");
            if (string.IsNullOrWhiteSpace(id)) continue;
            string display = string.IsNullOrWhiteSpace(name) ? id : name!;
            // enabled 缺省视为已启用（多数 provider 条目不写这个字段）
            bool enabled = p.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False
                ? false
                : true;
            Register(new ChannelInfo(
                Key: id,
                Name: NormalizeName(display),
                Agent: "ZCode",
                SourceKey: SourceKeyFor(id, display),
                ProviderIds: [id],
                Enabled: enabled));
        }
    }

    /// <summary>把配置里的名字规范成显示名（"CommandCode goat" → "Command Code GOAT"）。</summary>
    private static string NormalizeName(string raw)
    {
        string name = raw.Trim();
        if (name.Contains("commandcode", StringComparison.OrdinalIgnoreCase))
            return "Command Code GOAT";
        if (name.Contains("opencode", StringComparison.OrdinalIgnoreCase))
            return "OpenCode Go";
        if (name.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            return "DeepSeek";
        // 其余原样（去掉 " (Responses)" 这类路由后缀）
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    private static string? SourceKeyFor(string providerId, string? name)
    {
        string probe = (providerId + " " + (name ?? "")).ToLowerInvariant();
        if (probe.Contains("commandcode") || probe.Contains("goat")) return "goat";
        if (probe.Contains("opencode")) return "opencode";
        if (probe.Contains("deepseek")) return "deepseek";
        if (probe.Contains("bigmodel") || probe.Contains("glm")) return "bigmodel";
        return null;
    }

    private void Register(ChannelInfo channel)
    {
        // 已存在同名渠道:把 provider id 并进去,不要建两个同名行
        var existing = _channels.FirstOrDefault(c =>
            string.Equals(c.Name, channel.Name, StringComparison.Ordinal)
            && string.Equals(c.Agent, channel.Agent, StringComparison.Ordinal));
        if (existing is not null)
        {
            var ids = existing.ProviderIds.Concat(channel.ProviderIds).Distinct(StringComparer.Ordinal).ToList();
            int i = _channels.IndexOf(existing);
            // 合并时"启用"取或：只要有一个 id 是启用的，这个套餐就算启用中
            bool? enabled = (existing.Enabled, channel.Enabled) switch
            {
                (true, _) or (_, true) => true,
                (false, false) => false,
                _ => existing.Enabled ?? channel.Enabled,
            };
            var merged = existing with { ProviderIds = ids, Enabled = enabled };
            _channels[i] = merged;
            foreach (var id in ids) _byProviderId[id] = merged;
            return;
        }
        _channels.Add(channel);
        foreach (var id in channel.ProviderIds) _byProviderId[id] = channel;
    }

    /// <summary>
    /// 把一个 provider id 解析成渠道。未知的 id 用**内置规则**猜（如 opencode-go-* 前缀），
    /// 再不行就原样作为一个"未命名渠道"，保证数据不丢。
    /// </summary>
    public ChannelInfo Resolve(string? providerId, string agent)
    {
        if (!string.IsNullOrWhiteSpace(providerId) && _byProviderId.TryGetValue(providerId!, out var known))
            return known;

        string id = providerId ?? "";
        foreach (var (match, name, sourceKey) in BuiltinRules)
        {
            if (id.Contains(match, StringComparison.OrdinalIgnoreCase))
                return new ChannelInfo(id.Length > 0 ? id : name, name, agent, sourceKey, [id]);
        }

        // ZCode 里 provider_id 常是 UUID:没有映射时给一个有辨识度的短标签,而不是全 UUID
        string label = id.Length > 0 ? ShortId(id) : "未标注渠道";
        return new ChannelInfo(id.Length > 0 ? id : "(none)", label, agent,
            SourceKeyFor(id, null), [id]);
    }

    private static string ShortId(string id) =>
        id.Length <= 12 ? id : "渠道 " + id[..8];

    /// <summary>本机配置里已知的渠道（用于列表顺序与"没有用量的渠道"提示）。</summary>
    public IReadOnlyList<ChannelInfo> KnownChannels => _channels;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

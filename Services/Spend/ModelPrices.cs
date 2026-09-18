using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 模型牌价表。**只有一种来源:厂商公开发布的 API 价**(models.dev)。
///
/// 绝不自己编价,也绝不把"套餐里买的"折算成牌价——查不到就只算 token、不显示金额,
/// 这是上游 Pulse 最硬的一条规矩:一个猜出来的价格看起来像对的数字,比没有更糟。
///
/// 查找顺序(与上游一致):**第一方厂商价永远优先**;第一方查不到才退到套餐厂商
/// (如 <c>opencode-go</c> 卖的 <c>deepseek-v4.1-flash</c>——DeepSeek 官方条目里没有它)。
/// </summary>
public sealed class ModelPrices
{
    private const string BuiltinResource = "PulseWin.Services.Spend.model-prices.json";

    private readonly Dictionary<string, Dictionary<string, PriceEntry>> _firstParty;
    private readonly Dictionary<string, Dictionary<string, PriceEntry>> _plan;
    /// <summary>规范化模型键 → 第一方价(含厂商名,界面要显示价目从哪来)。</summary>
    private readonly Dictionary<string, (string Vendor, PriceEntry Entry)> _firstPartyIndex = new(StringComparer.Ordinal);
    /// <summary>规范化模型键 → 套餐价,已按 <see cref="PlanVendorOrder"/> 定过胜负。</summary>
    private readonly Dictionary<string, (string Vendor, PriceEntry Entry)> _planIndex = new(StringComparer.Ordinal);
    /// <summary>本机看到的 provider 前缀(如 <c>opencode-go</c>、<c>builtin:bigmodel-start-plan</c>)到价目厂商名的映射。</summary>
    private readonly Dictionary<string, string> _providerAliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 第一方厂商的优先顺序。**只列模型原厂**:云厂商挂别人模型的转售条目一律不进第一方表
    /// (否则 alibaba-cn 会按字母序抢在 deepseek 前头,把 DeepSeek 的模型按阿里的价算)。
    /// </summary>
    private static readonly string[] FirstPartyOrder =
    [
        "deepseek", "anthropic", "openai", "google", "xai", "mistral",
        "zai", "zhipuai", "moonshotai", "minimax", "xiaomi",
        "stepfun", "stepfun-ai", "longcat", "sakana"
    ];

    /// <summary>
    /// 套餐厂商的优先顺序。**按与用户实际订阅的相关度排,不是字母序**:
    /// 两个转售商都卖同一个模型时,先命中的那个决定金额,所以顺序必须是刻意的
    /// (kilo 与 opencode-go 都卖 deepseek-v4.1-flash,价差一倍:0.3/1.2 与 0.15/0.6)。
    /// </summary>
    private static readonly string[] PlanVendorOrder =
    [
        "opencode-go", "opencode", "zai-coding-plan", "zhipuai-coding-plan",
        "kimi-for-coding", "cline-pass", "kilo", "volcengine-coding-plan",
        "minimax-coding-plan", "minimax-cn-coding-plan", "alibaba-coding-plan",
        "alibaba-coding-plan-cn", "tencent-coding-plan", "xiaomi-token-plan-cn",
        "stepfun-step-plan", "umans-ai-coding-plan", "iflowcn"
    ];

    private readonly record struct PriceEntry(double Input, double Output, double? CacheRead, double? CacheWrite);

    public string Source { get; }
    public DateTimeOffset? FetchedAt { get; }
    public int ModelCount { get; }

    private ModelPrices(
        Dictionary<string, Dictionary<string, PriceEntry>> firstParty,
        Dictionary<string, Dictionary<string, PriceEntry>> plan,
        string source,
        DateTimeOffset? fetchedAt)
    {
        _firstParty = firstParty;
        _plan = plan;
        Source = source;
        FetchedAt = fetchedAt;
        ModelCount = firstParty.Sum(v => v.Value.Count) + plan.Sum(v => v.Value.Count);

        // 本机 provider id 里出现的几种写法,映射到价目表里的厂商条目。
        // 只映射**确实对应**的:ZCode 里 glm 走的是 BigModel 包月,价目按智谱第一方算。
        _providerAliases["opencode-go"] = "opencode-go";
        _providerAliases["opencode-go-glm"] = "opencode-go";
        _providerAliases["opencode-go-responses"] = "opencode-go";
        _providerAliases["builtin:bigmodel-start-plan"] = "zai-coding-plan";
        _providerAliases["bigmodel-start-plan"] = "zai-coding-plan";

        // 规范化索引:把各家对同一个模型的写法归到一个键上,再按既定优先级定胜负。
        // 有了它,"先试哪个候选写法"就不再影响结果——只有厂商优先级影响结果。
        foreach (var (vendor, models) in firstParty.OrderBy(v => PriorityOf(v.Key, FirstPartyOrder)))
        {
            foreach (var (id, entry) in models)
            {
                _firstPartyIndex.TryAdd(Normalize(id), (vendor, entry));
            }
        }
        foreach (var vendor in plan.Keys
                     .OrderBy(v => PriorityOf(v, PlanVendorOrder))
                     .ThenBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (id, entry) in plan[vendor])
            {
                _planIndex.TryAdd(Normalize(id), (vendor, entry));
            }
        }
    }

    private static int PriorityOf(string vendor, string[] order)
    {
        int index = Array.IndexOf(order, vendor);
        return index < 0 ? int.MaxValue : index;
    }

    private static ModelPrices? _current;
    private static readonly object Gate = new();

    public static ModelPrices Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (Gate) return _current ??= Load();
        }
    }

    private static ModelPrices Load()
    {
        // 用户缓存(下载版)优先,内置表兜底。两者同格式。
        try
        {
            var cached = TryReadFile(SnapshotSource.DataPaths.ModelPricesFile);
            if (cached is not null) return cached;
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取价目缓存失败,改用内置表", ex);
        }
        try
        {
            var builtin = TryReadBuiltin();
            if (builtin is not null) return builtin;
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取内置价目表失败", ex);
        }
        return new ModelPrices(new(), new(), "无价目表", null);
    }

    /// <summary>下载成功后重载(不必重启程序)。</summary>
    public static void Invalidate()
    {
        lock (Gate) _current = null;
    }

    private static ModelPrices? TryReadBuiltin()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BuiltinResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "内置价目表", null);
    }

    private static ModelPrices? TryReadFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        string source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "models.dev" : "models.dev";
        DateTimeOffset? at = doc.RootElement.TryGetProperty("generatedAt", out var g)
            && DateTimeOffset.TryParse(g.GetString(), out var parsed) ? parsed : null;
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), source, at);
    }

    private static ModelPrices Parse(string json, string source, DateTimeOffset? fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var firstParty = ReadGroup(doc.RootElement, "firstParty");
        var plan = ReadGroup(doc.RootElement, "plan");
        return new ModelPrices(firstParty, plan, source, fetchedAt);
    }

    private static Dictionary<string, Dictionary<string, PriceEntry>> ReadGroup(JsonElement root, string name)
    {
        var result = new Dictionary<string, Dictionary<string, PriceEntry>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(name, out var group)) return result;
        foreach (var vendor in group.EnumerateObject())
        {
            var models = new Dictionary<string, PriceEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in vendor.Value.EnumerateObject())
            {
                var e = model.Value;
                double input = e.TryGetProperty("i", out var i) ? i.GetDouble() : double.NaN;
                if (double.IsNaN(input)) continue;
                models[model.Name] = new PriceEntry(
                    input,
                    e.TryGetProperty("o", out var o) ? o.GetDouble() : 0,
                    e.TryGetProperty("r", out var r) ? r.GetDouble() : null,
                    e.TryGetProperty("w", out var w) ? w.GetDouble() : null);
            }
            if (models.Count > 0) result[vendor.Name] = models;
        }
        return result;
    }

    /// <summary>
    /// 查一个模型的牌价。<paramref name="providerHint"/> 是来源里记的 provider
    /// (ZCode 的 provider_id / OpenCode 的 providerID)。
    ///
    /// 顺序是硬性的:**第一方厂商价 → 该 provider 实际买的套餐价 → 其余套餐价**。
    /// 第一方永远优先,因为那才是"这个模型多少钱"的答案;套餐价只是"你这条渠道多少钱"。
    /// </summary>
    public bool TryPrice(string modelId, string? providerHint, out ModelPrice price)
    {
        price = null!;
        if (string.IsNullOrWhiteSpace(modelId)) return false;

        string key = Normalize(modelId);

        // 1) 第一方
        if (_firstPartyIndex.TryGetValue(key, out var first))
        {
            price = ToPrice(first.Entry, first.Vendor);
            return true;
        }

        // 2) 这个 provider 自己的套餐表(用户确实按这个买的)
        if (providerHint is not null && _providerAliases.TryGetValue(providerHint, out var vendorHint))
        {
            if (_plan.TryGetValue(vendorHint, out var vendorModels)
                && TryVendorLookup(vendorModels, key, out var entry))
            {
                price = ToPrice(entry, vendorHint);
                return true;
            }
            if (_firstParty.TryGetValue(vendorHint, out var firstModels)
                && TryVendorLookup(firstModels, key, out var firstEntry))
            {
                price = ToPrice(firstEntry, vendorHint);
                return true;
            }
        }

        // 3) 其余套餐厂商。**按固定优先级,不按字典序**——否则同一个模型会因为厂商名的
        // 字母顺序换一个价(kilo 排在 opencode-go 前面时,deepseek-v4.1-flash 会贵一倍),
        // 那是个看起来对的错数字。
        if (_planIndex.TryGetValue(key, out var plan))
        {
            price = ToPrice(plan.Entry, plan.Vendor);
            return true;
        }

        return false;
    }

    /// <summary>在一个厂商表里查:先按规范化键,再按表里原样的键。</summary>
    private static bool TryVendorLookup(
        Dictionary<string, PriceEntry> models, string key, out PriceEntry entry)
    {
        if (models.TryGetValue(key, out entry)) return true;
        foreach (var candidate in NormalizedVariants(key))
        {
            if (models.TryGetValue(candidate, out entry)) return true;
        }
        entry = default;
        return false;
    }

    /// <summary>
    /// 规范化:去 provider 路由前缀、去档位后缀、小写。
    ///
    /// 这不是"模糊匹配",而是把各家的写法归到同一个键上:`deepseek/deepseek-v4.1-flash`
    /// (ZCode)、`deepseek-v4.1-flash`(opencode-go)、`TEE/deepseek-v4.1-flash`(nano-gpt)
    /// 说的是同一个模型。**不许做的事**是把长得像的名字削成一个(grok-build-0.1 是
    /// xAI 的真模型,不能被削成 grok)——这里只动斜杠前缀和已知的档位后缀。
    /// </summary>
    private static string Normalize(string modelId)    {
        string id = modelId.Trim();
        int slash = id.LastIndexOf('/');
        if (slash >= 0 && slash < id.Length - 1) id = id[(slash + 1)..];
        int colon = id.IndexOf(':');
        if (colon > 0) id = id[..colon];
        if (id.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
            id = id[..^"-latest".Length];
        return id.ToLowerInvariant();
    }

    /// <summary>规范化键的其它备选(厂商表里可能存着带前缀的原样)。</summary>
    private static IEnumerable<string> NormalizedVariants(string key)
    {
        yield return key;
    }

    private static ModelPrice ToPrice(PriceEntry entry, string vendor) =>
        new(entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, vendor);

    /// <summary>
    /// 界面上显示的模型名:去掉路由前缀的规范化写法。账本仍按来源记的原始 id
    /// 分别计价(同一个模型的两种写法可能命中不同价目),这里只是让它们显示成一个名字。
    /// </summary>
    public static string DisplayName(string modelId) => Normalize(modelId);

    /// <summary>可供设置的"价目表厂商"列表(诊断用)。</summary>
    public IEnumerable<string> VendorNames => _firstParty.Keys.Concat(_plan.Keys).Distinct().OrderBy(x => x);
}

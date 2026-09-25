using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 模型牌价表的自我更新:过期后从 models.dev 重新拉一份、按厂商白名单裁剪、
/// 原子写进 <c>Data\model-prices.json</c>。
///
/// **为什么需要它**:内置表是打包进程序那一刻的快照(内置那份是 2026-09-18 的),
/// 厂商上新模型之后它就查不到价,统计里那些模型会一直显示"无公开牌价"。
/// 上游 Pulse 同样让价目表自己过期重拉——**失败就继续用旧的、过五分钟再试**,
/// 不让一次网络失败影响任何已有读数,这里照抄那条规矩。
///
/// **不做的事**:不改价、不猜价。裁剪只是把 models.dev 公开发布的数字换个短字段名;
/// 白名单外的厂商(两百多个转售商/云厂)一个都不收,否则转售商会按字母序抢在原厂
/// 前头决定价格(代码里点名过 alibaba-cn 会把 DeepSeek 的模型按阿里的价算)。
/// </summary>
public static class ModelPricesUpdater
{
    private const string Endpoint = "https://models.dev/api.json";

    /// <summary>牌价多久算过期。厂商调价不频繁,一周一次足够;过期只意味着"可以再拉一份"。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>失败之后隔多久才允许再试(上游规格:五分钟)。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = CreateClient();
    private static int _busy;
    private static DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    /// <summary>上次更新的结果(诊断/界面用):成功时的模型数,或失败原因。</summary>
    public static string? LastResult { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 全量约 4.9MB,服务端支持 gzip(压到约 0.5MB);不压缩下载本机实测要 25 秒
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/" + UpdateChecker.CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 牌价表是否过期。<c>FetchedAt</c> 来自表里的 <c>generatedAt</c>,内置表也带
    /// (打包那天的日期),所以正常情况下第一次统计就会判为过期、去拉一份新的。
    /// </summary>
    public static bool IsStale =>
        ModelPrices.Current.FetchedAt is not { } at || DateTimeOffset.Now - at > MaxAge;

    /// <summary>
    /// 过期就在后台拉一份新表,拿到之后回调通知界面重算。**调用方不会被阻塞**:
    /// 统计窗口打开时调它,界面照旧用现有表渲染,拉到新的再重算一次。
    /// </summary>
    public static void RefreshIfStale(Action? onUpdated = null) => Refresh(force: false, onUpdated);

    /// <summary>不管过没过期都拉一份(手动/诊断入口)。</summary>
    public static void ForceRefresh(Action? onDone = null) => Refresh(force: true, onDone);

    private static void Refresh(bool force, Action? onUpdated)
    {
        if (!force && (!IsStale || DateTimeOffset.Now < _retryAfter)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                string json = await DownloadTrimmedAsync().ConfigureAwait(false);
                AtomicFile.WriteText(SnapshotSource.DataPaths.ModelPricesFile, json);
                ModelPrices.Invalidate();          // 下一次读就是新表,不必重启
                _retryAfter = DateTimeOffset.MinValue;
                LastResult = $"已更新({ModelPrices.Current.ModelCount} 个模型)";
                Diagnostics.Note("模型牌价表已更新: " + LastResult);
            }
            catch (Exception ex)
            {
                // 失败**不动旧表**:继续用现在这份,过五分钟再试
                _retryAfter = DateTimeOffset.Now + RetryDelay;
                LastResult = "更新失败:" + ex.Message;
                Diagnostics.Note("更新模型牌价表失败,继续用现有表", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                onUpdated?.Invoke();
            }
        });
    }

    /// <summary>下载全量并裁剪。抛异常 = 这次没成,由调用方决定退避。</summary>
    private static async Task<string> DownloadTrimmedAsync()
    {
        using var response = await Http.GetAsync(Endpoint).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"models.dev 返回 {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        return Trim(doc.RootElement);
    }

    /// <summary>
    /// 把 models.dev 的全量结构裁成 PulseWin 的短字段表:
    /// <c>{"firstParty":{厂商:{模型:{i,o,r,w}}},"plan":{...},"source","generatedAt"}</c>。
    /// 形状与内置表完全一致,所以读侧(<see cref="ModelPrices"/>)一个字都不用改。
    /// </summary>
    private static string Trim(JsonElement root)
    {
        int models;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            models = WriteGroup(writer, root, ModelPrices.FirstPartyOrder, "firstParty")
                + WriteGroup(writer, root, ModelPrices.PlanVendorOrder, "plan");
            writer.WriteString("source", "models.dev");
            writer.WriteString("generatedAt", DateTimeOffset.Now.ToString("yyyy-MM-dd"));
            writer.WriteEndObject();
        }

        // 一份没有任何白名单厂商的表等于废纸——宁可报错继续用旧的,也不要写空表落盘
        if (models == 0)
            throw new InvalidDataException("models.dev 的返回里没有白名单厂商的模型,这份表不能用");
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>写一个分组(第一方 / 套餐),返回收进多少个模型。</summary>
    private static int WriteGroup(Utf8JsonWriter writer, JsonElement root, string[] vendors, string group)
    {
        writer.WritePropertyName(group);
        writer.WriteStartObject();
        int total = 0;

        foreach (string vendor in vendors)
        {
            if (!root.TryGetProperty(vendor, out var provider)
                || provider.ValueKind != JsonValueKind.Object
                || !provider.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
            {
                // 白名单里有、models.dev 上没有的厂商是正常的(名单会漂移),跳过即可
                continue;
            }

            var rows = new List<(string Id, Cost Cost)>();
            foreach (var model in models.EnumerateObject())
            {
                if (!TryCost(model.Value, out var cost)) continue;
                rows.Add((model.Name, cost));
            }
            if (rows.Count == 0) continue;

            writer.WritePropertyName(vendor);
            writer.WriteStartObject();
            foreach (var (id, cost) in rows)
            {
                writer.WritePropertyName(id);
                writer.WriteStartObject();
                writer.WriteNumber("i", cost.Input);
                writer.WriteNumber("o", cost.Output);
                if (cost.CacheRead is { } read) writer.WriteNumber("r", read);
                if (cost.CacheWrite is { } write) writer.WriteNumber("w", write);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            total += rows.Count;
        }

        writer.WriteEndObject();
        return total;
    }

    private readonly record struct Cost(double Input, double Output, double? CacheRead, double? CacheWrite);

    /// <summary>
    /// 取一个模型的四类价。<c>input</c> 缺失就整条丢掉(没有输入价算不出金额)。
    /// 只收基础档:<c>reasoning</c> 并进 output(所有价目表都按输出计费),
    /// 而 <c>input_audio</c>/<c>output_audio</c>/<c>tiers</c>/<c>context_over_200k</c>
    /// 在本程序的结构里没有对应位置,与内置表口径一致——不编。
    /// </summary>
    private static bool TryCost(JsonElement model, out Cost cost)
    {
        cost = default;
        if (model.ValueKind != JsonValueKind.Object) return false;
        if (!model.TryGetProperty("cost", out var c) || c.ValueKind != JsonValueKind.Object) return false;
        if (!TryNumber(c, "input", out double input)) return false;

        TryNumber(c, "output", out double output);
        cost = new Cost(
            input,
            output,
            TryNumber(c, "cache_read", out double read) ? read : null,
            TryNumber(c, "cache_write", out double write) ? write : null);
        return true;
    }

    private static bool TryNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
            return false;
        return element.TryGetDouble(out value) && double.IsFinite(value);
    }
}

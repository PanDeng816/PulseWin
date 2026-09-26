using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// Command Code 的**浏览器会话**通道——用来读只有登录态才开放的逐模型明细。
///
/// **为什么需要它**：Command Code 的 API Key 只够读账户级数字（额度、余额、汇总）。
/// 逐模型、逐请求的明细（含缓存读/写 token）在 <c>/internal/usage/*</c> 下，本机实测
/// **API Key 加 Origin/Referer 伪装也是 401，必须有浏览器登录 cookie**。
/// 参考项目 goat-gauge 正是这么做的（它起一个专用 Chrome、用 DevTools 协议抓 cookie）。
///
/// 本实现**不自己起浏览器、不碰 Chrome DevTools**：用户平时就在浏览器里登录着
/// commandcode.ai，所以这里读**本机浏览器 cookie**（Edge/Chrome 的 Cookies SQLite）来拼
/// Cookie 头。代价是要解密 Chromium 的 cookie 值（DPAPI + AES-GCM，密钥在该 profile 的
/// Local State 里），且浏览器运行时 cookie 库被独占锁——所以读取前要复制一份。
///
/// **整条通道是可选的**：读不到 cookie（没登录过 / profile 结构变了 / 解密失败）就
/// 安静地返回 null，界面用本地数据兜底并说明原因。绝不因为它让主界面出问题。
/// </summary>
public static class CommandCodeWebSession
{
    private const string InternalCharts = "internal/usage/charts";
    private const string InternalUsage = "internal/usage";
    private const string Origin = "https://commandcode.ai";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>上一次失败/成功的原因（界面显示用）。</summary>
    public static string? LastStatus { get; private set; }

    /// <summary>cookie 是否可用（null = 还没试过）。</summary>
    public static bool? Available { get; private set; }

    /// <summary>缓存的 cookie 头（成功后短时间内复用，避免每次都去解浏览器库）。</summary>
    private static string? _cookieHeader;
    private static DateTimeOffset _cookieAt;

    private static HttpClient CreateClient()
    {
        var client = ProxyHttp.Create(TimeSpan.FromSeconds(25));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("Origin", Origin);
        client.DefaultRequestHeaders.Add("Referer", Origin + "/");
        return client;
    }

    /// <summary>
    /// 拉一段窗口内的逐模型缓存明细。<paramref name="from"/>/<paramref name="to"/> 是本地时间。
    /// 返回 null = 这条通道当前不可用（原因见 <see cref="LastStatus"/>）。
    /// </summary>
    public static async Task<List<ModelCacheBucket>?> TryFetchModelCacheAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken token = default)
    {
        // 默认关(见 AppSettings.UseBrowserSessionForModelDetail 的说明:读 cookie 有
        // 被 AV 误判的风险,且浏览器运行时 cookie 库是排他锁的、多半读不到)。
        if (!AppSettings.Current.UseBrowserSessionForModelDetail)
        {
            LastStatus = "未启用(可在本页开启「浏览器会话明细」)";
            return null;
        }

        string? cookie = GetCookieHeader(forceRefresh: false);
        if (cookie is null) return null;

        string query = $"?from={Uri.EscapeDataString(from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}"
            + $"&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, InternalCharts + query);
            req.Headers.Add("Cookie", cookie);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // 登录态过期:清掉缓存,下次重解浏览器库(用户重新登录后即可恢复)
                _cookieHeader = null;
                Available = false;
                LastStatus = "浏览器登录态已过期,请重新登录 commandcode.ai";
                return null;
            }
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var buckets = ParseCharts(doc.RootElement);
            Available = true;
            LastStatus = buckets.Count > 0
                ? $"已从浏览器会话读到 {buckets.Count} 条逐模型明细"
                : "浏览器会话可用,但这个窗口没有逐模型明细";
            return buckets;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Available = false;
            LastStatus = "读取逐模型明细失败:" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>/internal/usage/charts</c> 的回包。**结构按 goat-gauge 的读法归一**：
    /// 它按 时间桶 × 模型 返回 cacheReadInputTokens / cacheCreationInputTokens 等。
    /// 这里对字段名做宽松匹配（服务端字段大小写/命名会变），解析不出就跳过该行。
    /// </summary>
    private static List<ModelCacheBucket> ParseCharts(JsonElement root)
    {
        var result = new List<ModelCacheBucket>();
        // 回包可能是数组，也可能是 { data: [...] } / { charts: [...] } / { rows: [...] }
        JsonElement rows = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "data", "charts", "rows", "buckets", "items" })
            {
                if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    rows = inner;
                    break;
                }
            }
        }
        if (rows.ValueKind != JsonValueKind.Array) return result;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string? model = Str(row, "model", "modelId", "model_id", "name");
            if (string.IsNullOrWhiteSpace(model)) continue;

            long cacheRead = Long(row, "cacheReadInputTokens", "cache_read", "cacheRead", "cacheReadTokens");
            long cacheWrite = Long(row, "cacheCreationInputTokens", "cache_write", "cacheWrite", "cacheCreationTokens");
            long input = Long(row, "inputTokens", "input", "input_tokens");
            long output = Long(row, "outputTokens", "output", "output_tokens");
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0) continue;

            DateTimeOffset? at = Time(row, "timestamp", "time", "bucket", "date");
            result.Add(new ModelCacheBucket(
                ModelPrices.DisplayName(model), at, input, cacheWrite, cacheRead, output));
        }
        return result;
    }

    private static string? Str(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static long Long(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (!o.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n64)) return n64;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s)) return s;
        }
        return 0;
    }

    private static DateTimeOffset? Time(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (!o.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long epoch))
            {
                return epoch < 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : DateTimeOffset.FromUnixTimeMilliseconds(epoch);
            }
            if (v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }

    // ————————— 浏览器 cookie 读取 —————————

    /// <summary>
    /// 从本机浏览器的 cookie 库里拼出 commandcode.ai 的 Cookie 头。
    /// 支持 Edge / Chrome（Chromium 系：cookie 值 AES-GCM 加密，密钥在 profile 的
    /// <c>Local State</c> 里经 DPAPI 保护）。读不到返回 null。
    /// </summary>
    private static string? GetCookieHeader(bool forceRefresh)
    {
        if (!forceRefresh && _cookieHeader is not null
            && DateTimeOffset.Now - _cookieAt < TimeSpan.FromMinutes(10))
            return _cookieHeader;

        try
        {
            foreach (var (name, root) in BrowserRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var profile in Profiles(root))
                {
                    string cookieDb = Path.Combine(profile, "Network", "Cookies");
                    if (!File.Exists(cookieDb)) continue;
                    var header = ReadCookiesFrom(name, root, cookieDb);
                    if (!string.IsNullOrEmpty(header))
                    {
                        _cookieHeader = header;
                        _cookieAt = DateTimeOffset.Now;
                        return header;
                    }
                }
            }
            Available = false;
            LastStatus = "没找到 commandcode.ai 的登录 cookie(先在浏览器里登录一次)";
        }
        catch (Exception ex)
        {
            Available = false;
            LastStatus = "读取浏览器 cookie 失败:" + ex.Message;
        }
        return null;
    }

    private static IEnumerable<(string Name, string Root)> BrowserRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"));
        yield return ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"));
    }

    private static IEnumerable<string> Profiles(string root)
    {
        yield return Path.Combine(root, "Default");
        for (int i = 1; i <= 6; i++) yield return Path.Combine(root, $"Profile {i}");
        // 新版 Chromium 把部分 profile 放这里
        string guest = Path.Combine(root, "Guest Profile");
        if (Directory.Exists(guest)) yield return guest;
    }

    private static string? ReadCookiesFrom(string browser, string root, string cookieDb)
    {
        // 浏览器在跑时 cookie 库被独占锁——先复制一份再读(与 goat-gauge 同一思路)
        string temp = Path.Combine(Path.GetTempPath(), $"pulsewin-cookies-{Environment.ProcessId}-{browser}.db");
        try
        {
            File.Copy(cookieDb, temp, overwrite: true);
        }
        catch (IOException)
        {
            return null;   // 复制不到(占用/权限)就跳过这个 profile
        }

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = temp,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, value, encrypted_value, host_key FROM cookies "
                + "WHERE host_key LIKE '%commandcode%'";
            using var reader = cmd.ExecuteReader();
            var pairs = new List<string>();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                string host = reader.GetString(3);
                if (!host.Contains("commandcode", StringComparison.OrdinalIgnoreCase)) continue;
                string value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (value.Length == 0 && !reader.IsDBNull(2))
                {
                    byte[] encrypted = (byte[])reader.GetValue(2);
                    value = DecryptCookie(browser, root, encrypted) ?? "";
                }
                if (value.Length > 0) pairs.Add($"{name}={value}");
            }
            return pairs.Count > 0 ? string.Join("; ", pairs) : null;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* 清理失败无所谓 */ }
        }
    }

    /// <summary>
    /// 解密 Chromium 的 cookie 值。三种格式：
    /// v10/v11 = AES-256-GCM，密钥 = DPAPI(Local State 里的 encrypted_key)；
    /// 老版 = 直接 DPAPI(值前缀 0x01000000)。
    /// </summary>
    private static string? DecryptCookie(string browser, string root, byte[] encrypted)
    {
        if (encrypted.Length < 4) return null;

        // 老格式：整体就是 DPAPI blob
        if (encrypted[0] == 0x01 && encrypted[1] == 0x00 && encrypted[2] == 0x00 && encrypted[3] == 0x00)
        {
            try
            {
                var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }

        // 新格式：v10/v11 + 12 字节 nonce + 密文 + 16 字节 tag
        string prefix = Encoding.ASCII.GetString(encrypted, 0, 3);
        if (prefix is not ("v10" or "v11")) return null;

        byte[]? key = GetBrowserKey(browser, root);
        if (key is null) return null;
        if (encrypted.Length < 3 + 12 + 16) return null;

        int nonceLen = 12, tagLen = 16;
        var nonce = encrypted.AsSpan(3, nonceLen).ToArray();
        var cipher = encrypted.AsSpan(3 + nonceLen, encrypted.Length - 3 - nonceLen - tagLen).ToArray();
        var tag = encrypted.AsSpan(encrypted.Length - tagLen, tagLen).ToArray();
        var decrypted = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, tagLen);
            aes.Decrypt(nonce, cipher, tag, decrypted);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? GetBrowserKey(string browser, string root)
    {
        try
        {
            string localState = Path.Combine(root, "Local State");
            if (!File.Exists(localState)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)
                || !osCrypt.TryGetProperty("encrypted_key", out var encKey)
                || encKey.ValueKind != JsonValueKind.String)
                return null;

            byte[] blob = Convert.FromBase64String(encKey.GetString()!);
            // 前 5 字节是 "DPAPI" 标记
            if (blob.Length <= 5) return null;
            var dpapi = blob.AsSpan(5).ToArray();
            return ProtectedData.Unprotect(dpapi, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>一条"逐模型 / 时间桶"的缓存明细（来自 Command Code 浏览器会话通道）。</summary>
public sealed record ModelCacheBucket(
    string Model,
    DateTimeOffset? At,
    long Input,
    long CacheWrite,
    long CacheRead,
    long Output)
{
    public long Total => Input + CacheWrite + CacheRead + Output;

    /// <summary>缓存命中率 = 缓存读 / 全部输入。</summary>
    public double? CacheHit
    {
        get
        {
            long inputTotal = Input + CacheWrite + CacheRead;
            return inputTotal > 0 ? (double)CacheRead / inputTotal : null;
        }
    }
}

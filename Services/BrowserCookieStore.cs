using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 本机浏览器 cookie 的**共用读取器**(Command Code 与 OpenCode 两条浏览器会话通道都用它)。
///
/// 支持 Edge / Chrome(Chromium 系:cookie 值 AES-GCM 加密,密钥在 profile 的
/// <c>Local State</c> 里经 DPAPI 保护)。浏览器运行时 cookie 库被独占锁——读取前
/// 复制一份再开。按站点关键字缓存拼好的 Cookie 头(10 分钟),避免每次都解密。
///
/// **读不到是常态而非错误**:没登录过、profile 结构变了、解密失败都安静返回 null,
/// 调用方自己兜底。整条通道有被安全软件启发式误判的前车之鉴(CodexBar-Win),
/// 所以它必须活在「用户显式打开的开关」之下,这里不做任何开关判断。
/// </summary>
internal static class BrowserCookieStore
{
    private static readonly Dictionary<string, (string Header, DateTimeOffset At)> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DpapiSecretStore> ManualStores = new(StringComparer.Ordinal);
    private static AppDataPaths? _paths;

    /// <summary>上次失败原因(界面显示用);成功后清空。</summary>
    public static string? LastError { get; private set; }

    /// <summary>上一次成功读到的来源(缓存 / 手动粘贴 / Edge / Chrome),诊断与界面用。</summary>
    public static string? LastSource { get; private set; }

    /// <summary>数据目录(手动粘贴的 cookie 用 DPAPI 存这儿)。引擎构造时 Attach。</summary>
    public static void Attach(AppDataPaths paths) => _paths = paths;

    /// <summary>
    /// 拼出 host 包含 <paramref name="hostKeyword"/> 的站点的 Cookie 头。
    /// 读不到返回 null(原因见 <see cref="LastError"/>)。
    /// </summary>
    public static string? GetCookieHeader(string hostKeyword, bool forceRefresh)
    {
        // **成功读到就一直在**。浏览器 cookie 本身有效期以天计,而"重新从浏览器库读"
        // 有个硬门槛:浏览器开着时那个库是排他锁定的,根本读不到。所以这里不按分钟
        // 过期——否则用户每 10 分钟就得关一次浏览器。只有服务端判登录态过期
        // (<see cref="Invalidate"/>)时才重新去读。
        if (!forceRefresh && Cache.TryGetValue(hostKeyword, out var hit))
        {
            LastSource = "缓存";
            return hit.Header;
        }

        // 手动粘贴的优先:它是用户显式给的,不受浏览器文件锁影响
        if (ManualStore(hostKeyword)?.Read() is { Length: > 0 } manual)
        {
            Cache[hostKeyword] = (manual, DateTimeOffset.Now);
            LastSource = "手动粘贴";
            LastError = null;
            return manual;
        }

        try
        {
            bool blocked = false;
            foreach (var (name, root) in BrowserRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var profile in Profiles(root))
                {
                    string cookieDb = Path.Combine(profile, "Network", "Cookies");
                    if (!File.Exists(cookieDb)) continue;
                    var header = ReadCookiesFrom(name, root, cookieDb, hostKeyword, out bool locked);
                    blocked |= locked;
                    if (!string.IsNullOrEmpty(header))
                    {
                        Cache[hostKeyword] = (header, DateTimeOffset.Now);
                        LastSource = name;
                        LastError = null;
                        return header;
                    }
                }
            }
            // 两种情况要分清(2026-10-10 更正):"库被浏览器占用"只是表象,真正读不到数据的
            // 原因是 Edge/Chrome 127+ 的 cookie 已由 App-Bound 加密保护,第三方进程解不开。
            // 从前把两者合并成一句"没找到登录 cookie",会把人引去反复关浏览器——没用。
            LastError = blocked
                ? $"{hostKeyword} 的 cookie 库被浏览器占用,这次读不到(关掉浏览器后会自动重试)。"
                    + "但**即便读到也解不开**:Edge/Chrome 127+ 的 cookie 由 App-Bound 加密保护。"
                    + "请在设置页点「登录 opencode.ai」,或手动粘贴会话 cookie。"
                : $"{hostKeyword} 的 cookie 由 App-Bound 加密保护(Edge/Chrome 127+),第三方程序解不开。"
                    + "请在设置页点「登录 opencode.ai」,或手动粘贴会话 cookie。";
        }
        catch (Exception ex)
        {
            LastError = "读取浏览器 cookie 失败:" + ex.Message;
        }
        return null;
    }

    /// <summary>登录态被判过期时调用方清缓存,下次强制重解浏览器库。</summary>
    public static void Invalidate(string hostKeyword) => Cache.Remove(hostKeyword);

    /// <summary>保存用户手动粘贴的 Cookie 头(DPAPI 加密落盘,与 API Key 同一保护强度)。</summary>
    public static void SaveManual(string hostKeyword, string cookieHeader)
    {
        ManualStore(hostKeyword)?.Write(Normalize(cookieHeader));
        Cache.Remove(hostKeyword);
        LastError = null;
    }

    public static void ClearManual(string hostKeyword)
    {
        ManualStore(hostKeyword)?.Clear();
        Cache.Remove(hostKeyword);
    }

    /// <summary>是否已经有手动粘贴的 cookie(设置页显示状态用)。</summary>
    public static bool HasManual(string hostKeyword) => ManualStore(hostKeyword)?.Read() is { Length: > 0 };

    private static DpapiSecretStore? ManualStore(string hostKeyword)
    {
        if (_paths is null) return null;
        if (ManualStores.TryGetValue(hostKeyword, out var existing)) return existing;
        var store = new DpapiSecretStore(
            _paths,
            Path.Combine(_paths.RootDirectory, $"browser-cookie-{hostKeyword}.bin"),
            purpose: "browser-cookie-" + hostKeyword);
        ManualStores[hostKeyword] = store;
        return store;
    }

    /// <summary>
    /// 规范化用户粘贴的内容。三种写法都收(与 GoGauge 的 build_cookie_header 同款):
    /// 完整 Cookie 串、带 <c>Cookie:</c> 前缀的串、光秃秃一个值。
    /// </summary>
    private static string Normalize(string raw)
    {
        string text = raw.Trim();
        if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            text = text[7..].Trim();
        // 只有单个值(没有 name=)时补上会话 cookie 名
        if (text.Length > 0 && !text.Contains('=')) text = "__Host-console_session=" + text;
        return text;
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

    private static string? ReadCookiesFrom(
        string browser, string root, string cookieDb, string hostKeyword, out bool locked)
    {
        locked = false;
        // 浏览器在跑时 cookie 库被独占锁——先复制一份再读(与 goat-gauge 同一思路)
        string temp = Path.Combine(Path.GetTempPath(), $"pulsewin-cookies-{Environment.ProcessId}-{browser}.db");
        try
        {
            File.Copy(cookieDb, temp, overwrite: true);
        }
        catch (IOException)
        {
            locked = true;   // 被占用:这一次跳过,调用方据此给出更准确的原因
            return null;
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
                + "WHERE host_key LIKE '%" + hostKeyword + "%'";
            using var reader = cmd.ExecuteReader();
            var pairs = new List<string>();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                string host = reader.GetString(3);
                if (!host.Contains(hostKeyword, StringComparison.OrdinalIgnoreCase)) continue;
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

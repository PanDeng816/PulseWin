using System.IO;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

/// <summary>
/// OpenCode Go 的一个**附加账号**(附加工作区)。API Key 只认工作区——官方
/// 控制台里每个工作区(go1/go2…)各自生成 Key、各自一个额度池,所以「多账号」
/// 在凭据层就是多 Key。Id 即实例编号(2 起),Key 的 DPAPI 存储按它索引;
/// 标签存设置(settings.json),不进加密文件。
/// </summary>
public sealed record StoredOpenCodeAccount(int Id, string ApiKey);

public sealed class OpenCodeCredentialResolver
{
    public const string ApiKeyEnvironmentVariable = "OPENCODE_GO_API_KEY";

    private readonly ISecretStore _secretStore;
    private readonly ISecretStore? _accountsStore;
    private readonly Func<string, string?> _environmentReader;
    private readonly string _authFilePath;

    public OpenCodeCredentialResolver(
        ISecretStore secretStore,
        Func<string, string?>? environmentReader = null,
        string? authFilePath = null,
        ISecretStore? accountsStore = null)
    {
        _secretStore = secretStore;
        _accountsStore = accountsStore;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _authFilePath = authFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "opencode",
            "auth.json");
    }

    // ————————————————— 附加账号清单 —————————————————
    // 整个清单序列化成一个 JSON、整体 DPAPI 加密落盘(与单 Key 同一保护强度)。

    public IReadOnlyList<StoredOpenCodeAccount> LoadAccounts()
    {
        var json = _accountsStore?.Read();
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var accounts = new List<StoredOpenCodeAccount>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                    !element.TryGetProperty("apiKey", out var apiKey) || apiKey.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (id.TryGetInt32(out int parsedId) && parsedId >= 2)
                    accounts.Add(new StoredOpenCodeAccount(parsedId, apiKey.GetString()!.Trim()));
            }
            return accounts;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void SaveAccounts(IReadOnlyList<StoredOpenCodeAccount> accounts)
    {
        if (_accountsStore is null) return;
        if (accounts.Count == 0)
        {
            _accountsStore.Clear();
            return;
        }

        var json = JsonSerializer.Serialize(
            accounts.Select(a => new { id = a.Id, apiKey = a.ApiKey }).ToArray(),
            new JsonSerializerOptions { WriteIndented = true });
        _accountsStore.Write(json);
    }

    /// <summary>取某附加账号的 Key;不存在返回 null。</summary>
    public string? GetAccountKey(int id) =>
        LoadAccounts().FirstOrDefault(a => a.Id == id)?.ApiKey;

    public IReadOnlyList<CredentialCandidate> DiscoverCandidates()
    {
        var candidates = new List<CredentialCandidate>();
        AddIfPresent(candidates, CredentialSource.Environment, _environmentReader(ApiKeyEnvironmentVariable));
        AddIfPresent(candidates, CredentialSource.OpenCodeAuthFile, ReadOpenCodeGoKey());
        AddIfPresent(candidates, CredentialSource.ProtectedStore, _secretStore.Read());
        return candidates.DistinctBy(candidate => candidate.ApiKey, StringComparer.Ordinal).ToList();
    }

    public void SaveManualKey(string apiKey) => _secretStore.Write(apiKey);
    public void ClearManualKey() => _secretStore.Clear();

    private string? ReadOpenCodeGoKey()
    {
        try
        {
            if (!File.Exists(_authFilePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_authFilePath));
            if (!document.RootElement.TryGetProperty("opencode-go", out var provider) ||
                provider.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var propertyName in new[] { "key", "apiKey", "token" })
            {
                if (provider.TryGetProperty(propertyName, out var key) && key.ValueKind == JsonValueKind.String)
                {
                    return key.GetString()?.Trim();
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static void AddIfPresent(ICollection<CredentialCandidate> candidates, CredentialSource source, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            candidates.Add(new CredentialCandidate(source, apiKey.Trim()));
        }
    }
}

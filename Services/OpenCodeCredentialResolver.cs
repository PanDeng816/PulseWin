using System.IO;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

public sealed class OpenCodeCredentialResolver
{
    public const string ApiKeyEnvironmentVariable = "OPENCODE_GO_API_KEY";

    private readonly ISecretStore _secretStore;
    private readonly Func<string, string?> _environmentReader;
    private readonly string _authFilePath;

    public OpenCodeCredentialResolver(
        ISecretStore secretStore,
        Func<string, string?>? environmentReader = null,
        string? authFilePath = null)
    {
        _secretStore = secretStore;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _authFilePath = authFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "opencode",
            "auth.json");
    }

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

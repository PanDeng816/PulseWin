using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

public interface ISecretStore
{
    string? Read();
    void Write(string apiKey);
    void Clear();
}

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly byte[] _entropy;
    private readonly string _credentialFile;

    public DpapiSecretStore(AppDataPaths paths, string? credentialFile = null, string? purpose = null)
    {
        _credentialFile = credentialFile ?? paths.CredentialFile;
        _entropy = Encoding.UTF8.GetBytes(purpose is null
            ? "CommandCodeGOATMonitor/v1"
            : $"CommandCodeGOATMonitor/v1/{purpose}");
    }

    public string? Read()
    {
        try
        {
            if (!File.Exists(_credentialFile))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(_credentialFile);
            var clearBytes = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(clearBytes).Trim();
            CryptographicOperations.ZeroMemory(clearBytes);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Write(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var clearBytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            var protectedBytes = ProtectedData.Protect(clearBytes, _entropy, DataProtectionScope.CurrentUser);
            AtomicFile.WriteBytes(_credentialFile, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public void Clear()
    {
        if (File.Exists(_credentialFile))
        {
            File.Delete(_credentialFile);
        }
    }
}

public sealed class CredentialResolver
{
    public const string ApiKeyEnvironmentVariable = "COMMAND_CODE_API_KEY";

    private readonly ISecretStore _secretStore;
    private readonly Func<string, string?> _environmentReader;
    private readonly string _authFilePath;

    public CredentialResolver(
        ISecretStore secretStore,
        Func<string, string?>? environmentReader = null,
        string? authFilePath = null)
    {
        _secretStore = secretStore;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        _authFilePath = authFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".commandcode",
            "auth.json");
    }

    public IReadOnlyList<CredentialCandidate> DiscoverCandidates()
    {
        var candidates = new List<CredentialCandidate>();
        AddIfPresent(candidates, CredentialSource.Environment, _environmentReader(ApiKeyEnvironmentVariable));
        AddIfPresent(candidates, CredentialSource.CliAuthFile, ReadCliKey());
        AddIfPresent(candidates, CredentialSource.ProtectedStore, _secretStore.Read());

        return candidates
            .DistinctBy(candidate => candidate.ApiKey, StringComparer.Ordinal)
            .ToList();
    }

    public void SaveManualKey(string apiKey) => _secretStore.Write(apiKey);
    public void ClearManualKey() => _secretStore.Clear();

    private string? ReadCliKey()
    {
        try
        {
            if (!File.Exists(_authFilePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_authFilePath));
            return document.RootElement.TryGetProperty("apiKey", out var apiKey)
                ? apiKey.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void AddIfPresent(
        ICollection<CredentialCandidate> candidates,
        CredentialSource source,
        string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            candidates.Add(new CredentialCandidate(source, apiKey.Trim()));
        }
    }
}

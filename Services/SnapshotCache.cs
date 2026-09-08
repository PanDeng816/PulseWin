using System.IO;
using System.Text.Json;
using PulseWin;

namespace PulseWin;

public sealed class SnapshotCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _snapshotFile;

    public SnapshotCache(AppDataPaths paths, string? snapshotFile = null) =>
        _snapshotFile = snapshotFile ?? paths.SnapshotFile;

    public async Task<UsageSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_snapshotFile))
            {
                return null;
            }

            await using var stream = File.OpenRead(_snapshotFile);
            return await JsonSerializer.DeserializeAsync<UsageSnapshot>(stream, JsonOptions, cancellationToken);
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

    public Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteTextAsync(
            _snapshotFile,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            cancellationToken);
}

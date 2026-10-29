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
        catch (JsonException ex)
        {
            // 静默返回 null 会让故障只表现为"没有数据",排查全靠猜——
            // 与 SnapshotSource.LoadOne 同一条规矩:留一行诊断。
            Diagnostics.Note($"读取快照失败 {Path.GetFileName(_snapshotFile)}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            Diagnostics.Note($"读取快照失败 {Path.GetFileName(_snapshotFile)}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    public Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteTextAsync(
            _snapshotFile,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            cancellationToken);
}

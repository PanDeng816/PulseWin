using System.Net.Http;

namespace PulseWin;

public enum SourceState { Loading, Connected, Stale, AuthenticationRequired, Error }

/// <summary>一个数据源的当前状态(设置窗口显示用)。</summary>
public sealed class SourceStatus
{
    public SourceState State { get; set; } = SourceState.Loading;
    public string StatusText { get; set; } = "正在读取额度";
    public string CredentialSource { get; set; } = "";
    public DateTimeOffset? FetchedAt { get; set; }
}

/// <summary>
/// 内置数据引擎:不依赖 GOAT-Go-Usage-Monitor 运行。凭据发现顺序与 monitor
/// 一致(环境变量 → CLI auth.json → DPAPI 手动存储);快照落盘到便携 Data 目录,
/// 失败保留最后一次成功数据并标记 Stale。
/// </summary>
public sealed class UsageEngine : IDisposable
{
    private readonly AppDataPaths _paths;
    private readonly SnapshotCache _goatCache;
    private readonly SnapshotCache _goCache;
    private readonly CommandCodeApiClient _goatApi;
    private readonly OpenCodeGoApiClient _goApi;
    private readonly CredentialResolver _goatResolver;
    private readonly OpenCodeCredentialResolver _goResolver;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    private static readonly TimeSpan SuccessDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(60);

    public SourceStatus Goat { get; } = new();
    public SourceStatus Go { get; } = new();
    public event Action? SnapshotsChanged;

    public UsageEngine(AppDataPaths? paths = null)
    {
        _paths = paths ?? new AppDataPaths();
        _paths.MigrateFromLegacy();
        _goatCache = new SnapshotCache(_paths);
        _goCache = new SnapshotCache(_paths, _paths.OpenCodeSnapshotFile);
        _goatApi = new CommandCodeApiClient(new HttpClient());
        _goApi = new OpenCodeGoApiClient(new HttpClient());
        _goatResolver = new CredentialResolver(new DpapiSecretStore(_paths));
        _goResolver = new OpenCodeCredentialResolver(new DpapiSecretStore(_paths, _paths.OpenCodeCredentialFile));
    }

    public void Start() => _ = Task.Run(() => Loop(_life.Token));

    /// <summary>立即唤醒一次同步(跳过剩余等待)。</summary>
    public void RequestRefreshNow()
    {
        try { _signal.Wait(0); } catch (ObjectDisposedException) { }
        _signal.Release();
    }

    private async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await SyncOnce(token);
            // 成功 60s、失败 60s 后重试;RequestRefreshNow 可立即唤醒
            try
            {
                var delayed = Task.Delay(SuccessDelay, token);
                if (await Task.WhenAny(delayed, Task.Run(() => _signal.Wait(60000), token)) == delayed)
                    await delayed; // 正常计时结束
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SyncOnce(CancellationToken token)
    {
        await SyncSourceAsync(MonitorSource.CommandCodeGoat, token);
        await SyncSourceAsync(MonitorSource.OpenCodeGo, token);
        SnapshotsChanged?.Invoke();
    }

    private async Task SyncSourceAsync(MonitorSource source, CancellationToken token)
    {
        var status = source == MonitorSource.CommandCodeGoat ? Goat : Go;
        try
        {
            var provider = source == MonitorSource.CommandCodeGoat ? (IUsageProvider)_goatApi : _goApi;
            var candidates = source == MonitorSource.CommandCodeGoat
                ? _goatResolver.DiscoverCandidates()
                : _goResolver.DiscoverCandidates();

            if (candidates.Count == 0)
            {
                status.State = SourceState.AuthenticationRequired;
                status.StatusText = "未配置凭据,请在设置中输入 API Key";
                status.CredentialSource = "";
                return;
            }

            // 逐个候选尝试,直到成功(monitor 同款策略)
            Exception? last = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var snapshot = await provider.GetUsageAsync(candidate, token);
                    if (source == MonitorSource.CommandCodeGoat)
                        await _goatCache.SaveAsync(snapshot, token);
                    else
                        await _goCache.SaveAsync(snapshot, token);
                    status.State = SourceState.Connected;
                    status.StatusText = $"已连接 · {snapshot.AccountLabel}";
                    status.CredentialSource = candidate.SourceLabel;
                    status.FetchedAt = snapshot.FetchedAt;
                    return;
                }
                catch (CommandCodeApiException ex) when (ex.IsAuthenticationError)
                {
                    throw; // 凭据无效,不再尝试其余候选
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw last ?? new Exception("未知错误");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 保留最后一次成功快照,标记过期
            status.State = ex is CommandCodeApiException { IsAuthenticationError: true }
                ? SourceState.AuthenticationRequired
                : SourceState.Stale;
            status.StatusText = ex.Message;
        }
    }

    /// <summary>设置窗口保存手动 Key(DPAPI 加密保存)并立即刷新。</summary>
    public async Task<bool> SaveManualKeyAsync(MonitorSource source, string apiKey, CancellationToken token = default)
    {
        var provider = source == MonitorSource.CommandCodeGoat ? (IUsageProvider)_goatApi : _goApi;
        var validation = await provider.ValidateCredentialAsync(apiKey, token);
        if (!validation.IsValid) return false;
        if (source == MonitorSource.CommandCodeGoat)
            _goatResolver.SaveManualKey(apiKey);
        else
            _goResolver.SaveManualKey(apiKey);
        RequestRefreshNow();
        return true;
    }

    public void ClearManualKey(MonitorSource source)
    {
        if (source == MonitorSource.CommandCodeGoat) _goatResolver.ClearManualKey();
        else _goResolver.ClearManualKey();
        RequestRefreshNow();
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
        _signal.Dispose();
        GC.SuppressFinalize(this);
    }
}

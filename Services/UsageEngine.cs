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

    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(60);

    /// <summary>同步成功后的间隔,来自设置(默认 60s)。</summary>
    private static TimeSpan SuccessDelay =>
        TimeSpan.FromSeconds(AppSettings.Current.SyncIntervalSeconds);

    public SourceStatus Goat { get; } = new();
    public SourceStatus Go { get; } = new();
    public event Action? SnapshotsChanged;

    public UsageEngine(AppDataPaths? paths = null)
    {
        _paths = paths ?? new AppDataPaths();
        _paths.MigrateFromLegacy();
        Diagnostics.Attach(_paths);
        AppSettings.Attach(_paths);
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
        try
        {
            // 只唤醒不排队:连续点击"立即刷新"不该攒出多次同步
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task Loop(CancellationToken token)
    {
        int consecutiveFailures = 0;
        while (!token.IsCancellationRequested)
        {
            bool ok = await SyncOnce(token);

            // 成功按 60s 周期;失败逐步退避到 2 分钟,避免断网时每 60s 撞一次
            consecutiveFailures = ok ? 0 : Math.Min(consecutiveFailures + 1, 3);
            var delay = consecutiveFailures switch
            {
                0 => SuccessDelay,
                1 => FailureDelay,
                2 => TimeSpan.FromMinutes(2),
                _ => TimeSpan.FromMinutes(5),
            };

            try
            {
                // WaitAsync 不占用线程池线程(旧写法 Task.Run + Wait(60000) 会阻塞一个)
                var waiter = _signal.WaitAsync(delay, token);
                try { await waiter; }
                catch (OperationCanceledException) { throw; }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>同步一轮(两个源),返回是否全部成功。</summary>
    private async Task<bool> SyncOnce(CancellationToken token)
    {
        bool goatOk = await SyncSourceAsync(MonitorSource.CommandCodeGoat, token);
        bool goOk = await SyncSourceAsync(MonitorSource.OpenCodeGo, token);
        SnapshotsChanged?.Invoke();
        return goatOk && goOk;
    }

    private async Task<bool> SyncSourceAsync(MonitorSource source, CancellationToken token)
    {
        var status = source == MonitorSource.CommandCodeGoat ? Goat : Go;
        string name = source == MonitorSource.CommandCodeGoat ? "GOAT" : "OpenCode Go";
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
                return false;
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
                    return true;
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
            Diagnostics.Note($"{name} 同步失败", ex);
            return false;
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

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
    private readonly SnapshotCache _deepSeekCache;
    private readonly CommandCodeApiClient _goatApi;
    private readonly OpenCodeGoApiClient _goApi;
    private readonly DeepSeekApiClient _deepSeekApi;
    private readonly CredentialResolver _goatResolver;
    private readonly OpenCodeCredentialResolver _goResolver;
    private readonly CredentialResolver _deepSeekResolver;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>每个源上一次记过的错误:同一个错误不重复刷诊断(见 SyncSourceAsync)。</summary>
    private readonly Dictionary<string, string> _lastError = new(StringComparer.Ordinal);

    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(60);

    /// <summary>同步成功后的间隔,来自设置(默认 60s)。</summary>
    private static TimeSpan SuccessDelay =>
        TimeSpan.FromSeconds(AppSettings.Current.SyncIntervalSeconds);

    public SourceStatus Goat { get; } = new();
    public SourceStatus Go { get; } = new();
    public SourceStatus DeepSeek { get; } = new();
    public event Action? SnapshotsChanged;

    public UsageEngine(AppDataPaths? paths = null)
    {
        _paths = paths ?? new AppDataPaths();
        _paths.MigrateFromLegacy();
        Diagnostics.Attach(_paths);
        AppSettings.Attach(_paths);
        _goatCache = new SnapshotCache(_paths);
        _goCache = new SnapshotCache(_paths, _paths.OpenCodeSnapshotFile);
        _deepSeekCache = new SnapshotCache(_paths, _paths.DeepSeekSnapshotFile);
        _goatApi = new CommandCodeApiClient(new HttpClient());
        _goApi = new OpenCodeGoApiClient(new HttpClient());
        _deepSeekApi = new DeepSeekApiClient(
            new HttpClient(), new DeepSeekBaseline(_paths), new DeepSeekLedger(_paths));
        _goatResolver = new CredentialResolver(new DpapiSecretStore(_paths));
        _goResolver = new OpenCodeCredentialResolver(new DpapiSecretStore(_paths, _paths.OpenCodeCredentialFile));
        // DeepSeek 没有 CLI 登录态可借用(Key 只存在于它的控制台)——
        // 所以这里只有 环境变量 → 手动保存 两条路,没有 auth.json 那一档。
        _deepSeekResolver = new CredentialResolver(
            new DpapiSecretStore(_paths, _paths.DeepSeekCredentialFile, purpose: "deepseek"),
            authFilePath: string.Empty,
            environmentVariable: DeepSeekApiClient.ApiKeyEnvironmentVariable);
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
        // **每个源自己记失败次数**。整轮共用一个计数器是错的:一个源坏了会把
        // 其它源一起拖进退避。实测过这个故障:OpenCode Go 的账户未订阅、每次同步
        // 必然失败,于是整轮永远算失败、连续 3 次后退避到 5 分钟——GOAT 明明每轮
        // 都成功,也只能 5 分钟更新一次,界面上就是"数据 X 分钟前 · 未能刷新"。
        var failures = new Dictionary<MonitorSource, int>();
        while (!token.IsCancellationRequested)
        {
            await SyncOnce(token, failures);

            // 整轮的节奏取**最健康的那个源**:只要还有源在正常工作就按正常周期跑,
            // 全都失败才退避(避免断网时每 60s 撞一次)。
            int healthiest = failures.Count == 0 ? 0 : failures.Values.Min();
            var delay = healthiest switch
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

    /// <summary>同步一轮(全部数据源),并按源更新各自的失败计数。</summary>
    private async Task SyncOnce(CancellationToken token, Dictionary<MonitorSource, int> failures)
    {
        foreach (var source in AllSources)
        {
            bool ok = await SyncSourceAsync(source, token);
            failures[source] = ok ? 0 : Math.Min(failures.GetValueOrDefault(source) + 1, 3);
        }
        SnapshotsChanged?.Invoke();
    }

    private static readonly MonitorSource[] AllSources =
    [
        MonitorSource.CommandCodeGoat, MonitorSource.OpenCodeGo, MonitorSource.DeepSeek
    ];

    private IUsageProvider ProviderFor(MonitorSource source) => source switch
    {
        MonitorSource.CommandCodeGoat => _goatApi,
        MonitorSource.OpenCodeGo => _goApi,
        _ => _deepSeekApi,
    };

    private SnapshotCache CacheFor(MonitorSource source) => source switch
    {
        MonitorSource.CommandCodeGoat => _goatCache,
        MonitorSource.OpenCodeGo => _goCache,
        _ => _deepSeekCache,
    };

    private async Task<bool> SyncSourceAsync(MonitorSource source, CancellationToken token)
    {
        var status = source switch
        {
            MonitorSource.CommandCodeGoat => Goat,
            MonitorSource.OpenCodeGo => Go,
            _ => DeepSeek,
        };
        string name = source switch
        {
            MonitorSource.CommandCodeGoat => "GOAT",
            MonitorSource.OpenCodeGo => "OpenCode Go",
            _ => "DeepSeek",
        };
        var provider = ProviderFor(source);
        var candidates = source switch
        {
            MonitorSource.CommandCodeGoat => _goatResolver.DiscoverCandidates(),
            MonitorSource.OpenCodeGo => _goResolver.DiscoverCandidates(),
            _ => _deepSeekResolver.DiscoverCandidates(),
        };

        try
        {
            if (candidates.Count == 0)
            {
                status.State = SourceState.AuthenticationRequired;
                status.StatusText = "未配置凭据,请在设置中输入 API Key";
                status.CredentialSource = "";
                // 没配凭据不是故障。若算作失败,退避会把已经配好的源一起拖慢,
                // 而 DeepSeek 这种"先加进来、Key 回头再填"的源很常见。
                return true;
            }

            // 逐个候选尝试,直到成功(monitor 同款策略)
            Exception? last = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var snapshot = await provider.GetUsageAsync(candidate, token);
                    await CacheFor(source).SaveAsync(snapshot, token);
                    status.State = SourceState.Connected;
                    status.StatusText = $"已连接 · {snapshot.AccountLabel}";
                    status.CredentialSource = candidate.SourceLabel;
                    status.FetchedAt = snapshot.FetchedAt;
                    _lastError.Remove(name);
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
            // 同一个源反复报同一个错(账户未订阅、Key 失效)不必每次都记:诊断只留
            // 100 条,同一行刷屏会把真正有用的历史挤掉。错误内容变了才重新记一条。
            if (_lastError.GetValueOrDefault(name) != ex.Message)
            {
                Diagnostics.Note($"{name} 同步失败", ex);
                _lastError[name] = ex.Message;
            }
            return false;
        }
    }

    /// <summary>设置窗口保存手动 Key(DPAPI 加密保存)并立即刷新。</summary>
    public async Task<bool> SaveManualKeyAsync(MonitorSource source, string apiKey, CancellationToken token = default)
    {
        var validation = await ProviderFor(source).ValidateCredentialAsync(apiKey, token);
        if (!validation.IsValid) return false;
        switch (source)
        {
            case MonitorSource.CommandCodeGoat: _goatResolver.SaveManualKey(apiKey); break;
            case MonitorSource.OpenCodeGo: _goResolver.SaveManualKey(apiKey); break;
            default: _deepSeekResolver.SaveManualKey(apiKey); break;
        }
        RequestRefreshNow();
        return true;
    }

    public void ClearManualKey(MonitorSource source)
    {
        switch (source)
        {
            case MonitorSource.CommandCodeGoat: _goatResolver.ClearManualKey(); break;
            case MonitorSource.OpenCodeGo: _goResolver.ClearManualKey(); break;
            default: _deepSeekResolver.ClearManualKey(); break;
        }
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

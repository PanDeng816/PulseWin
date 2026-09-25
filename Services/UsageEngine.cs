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

    /// <summary>每个源上一次记过的错误:同一个错误不重复刷诊断(见 SyncSourceAsync)。
    /// 用并发字典是因为各源的同步现在是并行的。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastError =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 服务端明确说"不可用"的源(账户未订阅、Key 无权)下次允许重试的时刻。
    ///
    /// 这类错误**重试再快也不会成功**——每轮都撞一次只是白打请求;但也不能放弃,
    /// 因为订阅续上之后应当自己恢复,不该要求用户记得回来手动打开。所以按一个较长
    /// 周期静默重试;手动刷新(立即刷新 / 点环)会强制立即试一次。
    /// </summary>
    private readonly Dictionary<MonitorSource, DateTimeOffset> _retryAfter = new();
    private static readonly TimeSpan PermissionRetryDelay = TimeSpan.FromMinutes(30);

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

    /// <summary>立即唤醒一次同步(跳过剩余等待与任何静默周期)。</summary>
    public void RequestRefreshNow()
    {
        try
        {
            // 手动刷新要连"正在静默等待"的源也一起试(用户续订后点一下就该看到恢复)
            _forceNextSync = true;
            // 只唤醒不排队:连续点击"立即刷新"不该攒出多次同步
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>把手动刷新标记只交给紧随其后的那一轮。</summary>
    private bool _forceNextSync;

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
        bool force = _forceNextSync;
        _forceNextSync = false;
        var now = DateTimeOffset.Now;

        // 设置里没勾选的源**根本不读**(上游规格:未选择显示的服务不再读取)。
        // 关掉一个源是"我不想再看到它",不是"照旧每 60 秒打一次 API、只是不画环"。
        // 三个全关时 due 为空,整轮就是空转,不会出错。
        var enabled = AllSources.Where(EnabledInSettings).ToList();

        // 被关掉的源要**连上次的失败计数一起清掉**:留着它,"整轮节奏取最健康的
        // 那个源"就会一直算上一个已经不看的源,把还在用的源一起拖进退避——
        // 正是 v1.2.1 修的那类 bug。静默期同理,重新勾选时应当立刻试一次。
        foreach (var source in AllSources.Except(enabled))
        {
            failures.Remove(source);
            _retryAfter.Remove(source);
        }

        // 服务端明确拒绝过的源:在静默期内不再每轮去撞(手动刷新例外)。
        // 续订之后最长等一个静默周期就会自己恢复,不需要用户记得回来手动打开。
        var due = enabled
            .Where(source => force
                || !_retryAfter.TryGetValue(source, out var until)
                || now >= until)
            .ToList();

        // **三个源并行**发请求。串行时一个源卡住,后面的源就得排队等它——
        // 实测见过 GOAT 单次请求超 15s,整轮就被拖长;而"按源独立退避"本来就
        // 意味着它们互不牵连,那么同步也该并着做。
        var results = await Task.WhenAll(due.Select(async source =>
            (Source: source, Outcome: await SyncSourceAsync(source, token))));

        foreach (var (source, (ok, denied)) in results)
        {
            if (denied)
            {
                // 权限类失败**既不记失败次数也不参与整轮节奏**:它已经在静默期里,
                // 再让它拖慢别人就是 v1.2.1 修的那个 bug 的翻版。
                _retryAfter[source] = DateTimeOffset.Now + PermissionRetryDelay;
                continue;
            }
            _retryAfter.Remove(source);
            failures[source] = ok ? 0 : Math.Min(failures.GetValueOrDefault(source) + 1, 3);
        }
        SnapshotsChanged?.Invoke();
    }

    private static readonly MonitorSource[] AllSources =
    [
        MonitorSource.CommandCodeGoat, MonitorSource.OpenCodeGo, MonitorSource.DeepSeek
    ];

    /// <summary>
    /// 这个源在设置里是否被勾选显示。没勾选的源**连 API 都不打**:rail 上不画它,
    /// 就没有理由为它花配额、攒失败计数。设置里改回勾选后由设置窗口触发一次
    /// 立即同步,不必等下一轮(最长 60 秒)才恢复。
    /// </summary>
    private static bool EnabledInSettings(MonitorSource source)
    {
        var settings = AppSettings.Current;
        return source switch
        {
            MonitorSource.CommandCodeGoat => settings.ShowGoat,
            MonitorSource.OpenCodeGo => settings.ShowOpenCode,
            _ => settings.ShowDeepSeek,
        };
    }

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

    /// <summary>
    /// 同步一个源。返回两件事:是否成功,以及是不是**被服务端明确拒绝**。
    /// 后者要单独说出来——它不是"暂时失败",重试节奏完全不同(见 _retryAfter)。
    /// </summary>
    private async Task<(bool Ok, bool PermissionDenied)> SyncSourceAsync(MonitorSource source, CancellationToken token)
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
                return (true, false);
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
                    _lastError.TryRemove(name, out _);
                    return (true, false);
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
            bool denied = ex is CommandCodeApiException { IsAuthenticationError: true };
            status.State = denied ? SourceState.AuthenticationRequired : SourceState.Stale;
            status.StatusText = ex.Message;
            // 同一个源反复报同一个错(账户未订阅、Key 失效)不必每次都记:诊断只留
            // 100 条,同一行刷屏会把真正有用的历史挤掉。错误内容变了才重新记一条。
            if (_lastError.GetValueOrDefault(name) != ex.Message)
            {
                Diagnostics.Note($"{name} 同步失败", ex);
                _lastError[name] = ex.Message;
            }
            return (false, denied);
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

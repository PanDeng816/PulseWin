using System.IO;
using System.Net;
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

/// <summary>账号操作(添加/删除/改名)的结果,消息直接给设置页的提示行。</summary>
public sealed record AccountOperationResult(bool Ok, string Message)
{
    public static AccountOperationResult Success(string message) => new(true, message);
    public static AccountOperationResult Fail(string message) => new(false, message);
}

/// <summary>
/// 内置数据引擎:不依赖 GOAT-Go-Usage-Monitor 运行。凭据发现顺序与 monitor
/// 一致(环境变量 → CLI auth.json → DPAPI 手动存储);快照落盘到便携 Data 目录,
/// 失败保留最后一次成功数据并标记 Stale。
///
/// **同步条目(entry)**:一个源通常就是一个条目(键 = 源键),但 OpenCode Go 的
/// 附加账号各是一个条目(键 = opencode:2…)——每个账号一把 Key、一个快照文件、
/// 一份退避与状态,互不牵连:一个账号未订阅不会拖慢其他账号的同步节奏。
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

    /// <summary>每个条目上一次记过的错误:同一个错误不重复刷诊断(见 SyncEntryAsync)。
    /// 用并发字典是因为各条目的同步现在是并行的。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastError =
        new(StringComparer.Ordinal);

    /// <summary>附加账号各自的状态(主账号与整源状态见 <see cref="Go"/>,实例键 → 状态)。
    /// 用并发字典:条目同步是并行的,两个以上附加账号会同时写它,UI 线程同时在读。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceStatus> _instanceStatus =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 服务端明确说"不可用"的条目(账户未订阅、Key 无权)下次允许重试的时刻。
    ///
    /// 这类错误**重试再快也不会成功**——每轮都撞一次只是白打请求;但也不能放弃,
    /// 因为订阅续上之后应当自己恢复,不该要求用户记得回来手动打开。所以按一个较长
    /// 周期静默重试;手动刷新(立即刷新 / 点环)会强制立即试一次。
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private static readonly TimeSpan PermissionRetryDelay = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(60);

    /// <summary>同步成功后的间隔,来自设置(默认 60s)。</summary>
    private static TimeSpan SuccessDelay =>
        TimeSpan.FromSeconds(AppSettings.Current.SyncIntervalSeconds);

    public SourceStatus Goat { get; } = new();
    public SourceStatus Go { get; } = new();
    public SourceStatus DeepSeek { get; } = new();
    public event Action? SnapshotsChanged;

    /// <summary>便携数据目录(浏览器登录窗的 WebView2 profile 也要落在它下面,
    /// 免得在 exe 旁边多出一个游离目录)。</summary>
    public AppDataPaths Paths => _paths;

    public UsageEngine(AppDataPaths? paths = null)
    {
        _paths = paths ?? new AppDataPaths();
        _paths.MigrateFromLegacy();
        Diagnostics.Attach(_paths);
        AppSettings.Attach(_paths);
        BrowserCookieStore.Attach(_paths);
        OpenCodeUsageStatsClient.Attach(_paths);
        _goatCache = new SnapshotCache(_paths);
        _goCache = new SnapshotCache(_paths, _paths.OpenCodeSnapshotFile);
        _deepSeekCache = new SnapshotCache(_paths, _paths.DeepSeekSnapshotFile);
        _goatApi = new CommandCodeApiClient(ProxyHttp.Create(TimeSpan.FromSeconds(30)));
        _goApi = new OpenCodeGoApiClient(ProxyHttp.Create(TimeSpan.FromSeconds(30)));
        _deepSeekApi = new DeepSeekApiClient(
            ProxyHttp.Create(TimeSpan.FromSeconds(30)), new DeepSeekBaseline(_paths), new DeepSeekLedger(_paths));
        _goatResolver = new CredentialResolver(new DpapiSecretStore(_paths));
        _goResolver = new OpenCodeCredentialResolver(
            new DpapiSecretStore(_paths, _paths.OpenCodeCredentialFile),
            accountsStore: new DpapiSecretStore(_paths, _paths.OpenCodeAccountsFile, purpose: "opencode-accounts"));
        // DeepSeek 没有 CLI 登录态可借用(Key 只存在于它的控制台)——
        // 所以这里只有 环境变量 → 手动保存 两条路,没有 auth.json 那一档。
        _deepSeekResolver = new CredentialResolver(
            new DpapiSecretStore(_paths, _paths.DeepSeekCredentialFile, purpose: "deepseek"),
            authFilePath: string.Empty,
            environmentVariable: DeepSeekApiClient.ApiKeyEnvironmentVariable);
    }

    private readonly object _loopLock = new();
    private Task? _loopTask;

    public void Start()
    {
        lock (_loopLock) _loopTask = Task.Run(() => Loop(_life.Token));
    }

    /// <summary>立即唤醒一次同步(跳过剩余等待与任何静默周期)。</summary>
    public void RequestRefreshNow()
    {
        try
        {
            // 手动刷新要连"正在静默等待"的源也一起试(用户续订后点一下就该看到恢复)
            _forceNextSync = true;
            // 只唤醒不排队:连续点击"立即刷新"不该攒出多次同步。
            // 不再用 CurrentCount 判断——那是 check-then-act,两次并发调用可能都读到 0,
            // 第二次 Release 抛 SemaphoreFullException 并一路抛给 UI 调用方。
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
            // 循环真结束了(未预料的异常)时,手动刷新顺带把它拉起来——
            // 在那之前用户唯一的办法是重启程序,而界面上看不出区别。
            EnsureLoopRunning();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 循环不在跑了就重起一个。守卫是防呆、不是常规路径:一轮同步的任何异常都会在
    /// <see cref="RunRoundAsync"/> 里被吃掉,循环本身不该结束(见那里的说明)。
    /// </summary>
    private void EnsureLoopRunning()
    {
        lock (_loopLock)
        {
            if (_loopTask is { IsCompleted: false } or null) return;
            Diagnostics.Note($"同步循环已结束({_loopTask.Status}),重新启动");
            _loopTask = Task.Run(() => Loop(_life.Token));
        }
    }

    /// <summary>把手动刷新标记只交给紧随其后的那一轮。</summary>
    private bool _forceNextSync;

    /// <summary>
    /// 一轮同步最多给多久。各客户端自己那点超时(10~30s)管不住"响应头到了、正文不来"
    /// 这类停顿(HttpClient.Timeout 不覆盖 ResponseHeadersRead 之后的正文读取),
    /// 那会把整轮永远挂住。这一层是最后的兜底。
    /// </summary>
    private static readonly TimeSpan RoundTimeout = TimeSpan.FromMinutes(2);

    private async Task Loop(CancellationToken token)
    {
        // **每个条目自己记失败次数**。整轮共用一个计数器是错的:一个源坏了会把
        // 其它源一起拖进退避。实测过这个故障:OpenCode Go 的账户未订阅、每次同步
        // 必然失败,于是整轮永远算失败、连续 3 次后退避到 5 分钟——GOAT 明明每轮
        // 都成功,也只能 5 分钟更新一次,界面上就是"数据 X 分钟前 · 未能刷新"。
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        while (!token.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunRoundAsync(token, failures);
            }
            catch (OperationCanceledException)
            {
                return;   // 程序在退出
            }
            catch (Exception ex)
            {
                // RunRoundAsync 本该吃掉所有异常;漏到这里说明它自身出了问题。
                // 绝不能让 Task 因此结束——那正是 v1.20.5 的现场:rail 永远停在
                // "数据 X 分钟前 · 未能刷新",诊断里连一行错都没有,只能重启。
                Diagnostics.Note("同步循环异常,1 秒后重试", ex);
                delay = TimeSpan.FromSeconds(1);
            }

            try
            {
                // WaitAsync 不占用线程池线程(旧写法 Task.Run + Wait(60000) 会阻塞一个)
                await _signal.WaitAsync(delay, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // 同上:等待环节抛非取消异常也会结束循环。退化成固定 1 秒轮询。
                Diagnostics.Note("同步循环等待异常,1 秒后重试", ex);
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// 同步一轮,并算好下一轮该等多久。**任何异常都在这里收口**——这是本类最要紧的
    /// 一条不变量:循环体里漏出任何一个异常,这个后台任务就此结束,而它**没有别处会
    /// 重启**,界面上只是静静停在"数据 X 分钟前 · 未能刷新",连诊断日志都一行错没有,
    /// 只有重启程序才能恢复。
    ///
    /// 本机实测过这一次(2026-10-12 08:36 起):网页额度通道的一次 HTTP 超时抛出
    /// TaskCanceledException(它**就是** OperationCanceledException),被各层
    /// <c>catch (OperationCanceledException) { throw; }</c> 当成"程序正在退出"一路
    /// rethrow 出循环,于是同步在 30 秒内整个停摆——进程活着、活动心跳照写、
    /// 三个源的快照永远停在 08:36,而日志里找不到任何失败记录。
    /// </summary>
    private async Task<TimeSpan> RunRoundAsync(CancellationToken token, Dictionary<string, int> failures)
    {
        // 声明在 try 外面:catch 里要靠它判断这一轮到底是谁取消的
        using var round = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            round.CancelAfter(RoundTimeout);
            await SyncOnce(round.Token, failures);

            // 整轮的节奏取**最健康的那个源**:只要还有源在正常工作就按正常周期跑,
            // 全都失败才退避(避免断网时每 60s 撞一次)。
            int healthiest = failures.Count == 0 ? 0 : failures.Values.Min();
            return healthiest switch
            {
                0 => SuccessDelay,
                1 => FailureDelay,
                2 => TimeSpan.FromMinutes(2),
                _ => TimeSpan.FromMinutes(5),
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;   // 程序在退出,交给 Loop 收尾
        }
        catch (OperationCanceledException ex)
        {
            // 两种来源要分开记:①上面那个 watchdog 到点;②某条通道把**自己的 HTTP
            // 超时**当成取消抛了出来(HttpClient.Timeout 抛的 TaskCanceledException
            // 就是 OperationCanceledException)。②过去会一路穿透整个循环、把同步
            // 永久打死,现在只是这一轮不要了。
            Diagnostics.Note(round.IsCancellationRequested
                ? $"同步一轮超过 {RoundTimeout.TotalSeconds:0} 秒未返回,已放弃本轮"
                : "同步一轮被通道抛出的取消异常打断,已放弃本轮", ex);
            return FailureDelay;
        }
        catch (Exception ex)
        {
            Diagnostics.Note("同步一轮失败,已跳过本轮", ex);
            return FailureDelay;
        }
    }

    /// <summary>同步一轮(全部条目),并按条目更新各自的失败计数。</summary>
    private async Task SyncOnce(CancellationToken token, Dictionary<string, int> failures)
    {
        bool force = _forceNextSync;
        _forceNextSync = false;
        var now = DateTimeOffset.Now;

        // 设置里没勾选的条目**根本不读**(上游规格:未选择显示的服务不再读取)。
        // 关掉一个条目是"我不想再看到它",不是"照旧每 60 秒打一次 API、只是不画环"。
        var all = AllEntryKeys().ToList();
        var enabled = all.Where(EnabledInSettings).ToList();

        // 被关掉的条目要**连上次的失败计数一起清掉**:留着它,"整轮节奏取最健康的
        // 那个源"就会一直算上一个已经不看的源,把还在用的源一起拖进退避——
        // 正是 v1.2.1 修的那类 bug。静默期同理,重新勾选时应当立刻试一次。
        foreach (var entry in all.Except(enabled))
        {
            failures.Remove(entry);
            _retryAfter.Remove(entry);
        }

        // 服务端明确拒绝过的条目:在静默期内不再每轮去撞(手动刷新例外)。
        // 续订之后最长等一个静默周期就会自己恢复,不需要用户记得回来手动打开。
        var due = enabled
            .Where(entry => force
                || !_retryAfter.TryGetValue(entry, out var until)
                || now >= until)
            .ToList();

        // **所有条目并行**发请求。串行时一个源卡住,后面的源就得排队等它——
        // 实测见过 GOAT 单次请求超 15s,整轮就被拖长;而"按源独立退避"本来就
        // 意味着它们互不牵连,那么同步也该并着做。
        var results = await Task.WhenAll(due.Select(async entry =>
            (Entry: entry, Outcome: await SyncEntryAsync(entry, token))));

        foreach (var (entry, (ok, denied)) in results)
        {
            if (denied)
            {
                // 权限类失败**既不记失败次数也不参与整轮节奏**:它已经在静默期里,
                // 再让它拖慢别人就是 v1.2.1 修的那个 bug 的翻版。
                _retryAfter[entry] = DateTimeOffset.Now + PermissionRetryDelay;
                continue;
            }
            _retryAfter.Remove(entry);
            failures[entry] = ok ? 0 : Math.Min(failures.GetValueOrDefault(entry) + 1, 3);
        }
        SnapshotsChanged?.Invoke();
    }

    /// <summary>
    /// 全部同步条目的键:每个源的主账号(键 = 源键)+ OpenCode Go 的附加账号
    /// (键 = opencode:n)。附加账号的清单是**活的**(随时增删),每轮现取。
    /// </summary>
    private static IEnumerable<string> AllEntryKeys()
    {
        foreach (var source in SourceCatalog.All)
        {
            yield return source.Key;
            if (source.Key != SourceKeys.OpenCode) continue;
            // 取快照再枚举:后台同步线程在跑时,UI 线程可能正在增删附加账号;
            // 直接枚举 List 会因版本变化抛 InvalidOperationException,让这一轮同步整轮失败。
            foreach (var account in AppSettings.Current.AccountsSnapshot())
                yield return SourceKeys.InstanceKey(source.Key, account.Id);
        }
    }

    /// <summary>
    /// 这个条目在设置里是否被勾选显示。没勾选的条目**连 API 都不打**:rail 上不画它,
    /// 就没有理由为它花配额、攒失败计数。设置里改回勾选后由设置窗口触发一次
    /// 立即同步,不必等下一轮(最长 60 秒)。
    /// </summary>
    private static bool EnabledInSettings(string entry) => AppSettings.Current.IsSourceVisible(entry);

    /// <summary>
    /// 状态对象、凭据解析器、API 客户端**都带实例**,所以仍在这里按源映射。注册表只管
    /// "静态元数据"(名字、文件名、提示语),不持有这些有状态对象——否则它的构造就会
    /// 牵动网络与磁盘。
    /// </summary>
    private SourceStatus StatusFor(MonitorSource source) => source switch
    {
        MonitorSource.CommandCodeGoat => Goat,
        MonitorSource.OpenCodeGo => Go,
        _ => DeepSeek,
    };

    /// <summary>按键取状态:源键 → 整源状态;实例键(opencode:2) → 附加账号状态。</summary>
    public SourceStatus StatusForSource(string key)
    {
        if (SourceCatalog.ByKey(key) is { } source) return StatusFor(source.Id);
        if (SourceKeys.TryParse(key) is { } parsed && parsed.Instance >= 2)
            return InstanceStatus(key);
        return new SourceStatus();
    }

    private SourceStatus InstanceStatus(string entry) =>
        // GetOrAdd 是原子的:并行同步里两个账号同时进来也只会建一个状态对象
        _instanceStatus.GetOrAdd(entry, _ => new SourceStatus());

    private IUsageProvider ProviderFor(MonitorSource source) => source switch
    {
        MonitorSource.CommandCodeGoat => _goatApi,
        MonitorSource.OpenCodeGo => _goApi,
        _ => _deepSeekApi,
    };

    /// <summary>三个凭据解析器是两种类型(OpenCode 的走另一套 auth.json),但接口同形。</summary>
    private IReadOnlyList<CredentialCandidate> DiscoverCandidatesFor(MonitorSource source) => source switch
    {
        MonitorSource.CommandCodeGoat => _goatResolver.DiscoverCandidates(),
        MonitorSource.OpenCodeGo => _goResolver.DiscoverCandidates(),
        _ => _deepSeekResolver.DiscoverCandidates(),
    };

    private SnapshotCache CacheFor(MonitorSource source) => source switch
    {
        MonitorSource.CommandCodeGoat => _goatCache,
        MonitorSource.OpenCodeGo => _goCache,
        _ => _deepSeekCache,
    };

    /// <summary>按条目键分派:OpenCode 附加账号走账号同步,其余按主账号(整源)同步。</summary>
    private Task<(bool Ok, bool PermissionDenied)> SyncEntryAsync(string entry, CancellationToken token)
    {
        if (SourceKeys.TryParse(entry) is { } parsed && parsed.Instance >= 2 && parsed.BaseKey == SourceKeys.OpenCode)
            return SyncOpenCodeAccountAsync(parsed.Instance, token);

        var source = SourceCatalog.ByKey(entry) is { } descriptor
            ? descriptor.Id
            : MonitorSource.CommandCodeGoat;
        return SyncSourceAsync(source, token);
    }

    /// <summary>
    /// 同步一个源(主账号)。返回两件事:是否成功,以及是不是**被服务端明确拒绝**。
    /// 后者要单独说出来——它不是"暂时失败",重试节奏完全不同(见 _retryAfter)。
    /// </summary>
    private async Task<(bool Ok, bool PermissionDenied)> SyncSourceAsync(MonitorSource source, CancellationToken token)
    {
        var status = StatusFor(source);
        string name = SourceCatalog.ById(source).DisplayName;
        var provider = ProviderFor(source);

        try
        {
            // 凭据发现也放进 try 里:它要读磁盘(auth.json / DPAPI 密文),读失败
            // 不该把整个循环带走——过去它就在 try 外面,是同一种"漏出异常"。
            var candidates = DiscoverCandidatesFor(source);

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
            CommandCodeApiException? authFailure = null;
            int otherFailures = 0;
            foreach (var candidate in candidates)
            {
                try
                {
                    var snapshot = await provider.GetUsageAsync(candidate, token);
                    if (source == MonitorSource.OpenCodeGo)
                        snapshot = await MergeWebMetersAsync(snapshot, token);
                    await CacheFor(source).SaveAsync(snapshot, token);
                    status.State = SourceState.Connected;
                    status.StatusText = $"已连接 · {snapshot.AccountLabel}";
                    status.CredentialSource = candidate.SourceLabel;
                    status.FetchedAt = snapshot.FetchedAt;
                    _lastError.TryRemove(name, out _);
                    SourceStatusHub.Publish(SourceCatalog.ById(source).Key, null, false);
                    return (true, false);
                }
                catch (CommandCodeApiException ex) when (ex.IsAuthenticationError)
                {
                    // **一把 Key 被拒不等于别的 Key 也不行**。候选里可能同时有环境变量、
                    // CLI 的 auth.json、用户在设置里手填的三把**互不相同**的 Key。实测踩过:
                    // auth.json 里那把 OpenCode Key 被服务端标成 api_key_expired(官方口径:
                    // 重新登录不续期,必须去控制台新建),而用户手填的那把是好的——却被
                    // "立即 throw"挡在后面永远试不到,整个源于是报"Key 无效或已过期",
                    // 卡片上就是红字"数据 X 小时前 · 未能刷新"。
                    authFailure = ex;
                    last = ex;
                }
                catch (Exception ex)
                {
                    last = ex;
                    otherFailures++;
                }
            }
            // 只有**所有**候选都因鉴权被拒,才认定这是凭据问题(它会进 30 分钟长静默)。
            // 否则报最后一次的真实错误——一把过期 Key 不该把整源盖成"需要重新登录",
            // 更不该因此静默半小时(实测:第一候选过期 + 手填那把恰好超时,用户会以为要重登)。
            if (authFailure is not null && otherFailures == 0) throw authFailure;
            throw last ?? new Exception("未知错误");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 保留最后一次成功快照,标记过期
            bool denied = ex is CommandCodeApiException { IsAuthenticationError: true };
            status.State = denied ? SourceState.AuthenticationRequired : SourceState.Stale;
            status.StatusText = ex.Message;
            // 交给 rail 卡片:光有"多久没更新"不够,用户要知道"为什么"。
            SourceStatusHub.Publish(SourceCatalog.ById(source).Key, ex.Message, denied);
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

    /// <summary>
    /// 同步一个 OpenCode Go 附加账号:一把 Key、一个快照文件(opencode-go-snapshot-n.json)。
    ///
    /// 与主账号的最大差别:**权限类失败要写一个"不可用"快照**。附加账号是用户显式
    /// 添加的——账号没订阅时它应该作为一个灰环留在 rail 上("我知道它存在,只是还没
    /// 额度"),而不是悄悄消失;普通失败则照旧保留最后一次成功快照。
    /// </summary>
    private async Task<(bool Ok, bool PermissionDenied)> SyncOpenCodeAccountAsync(int instance, CancellationToken token)
    {
        var entry = SourceKeys.InstanceKey(SourceKeys.OpenCode, instance);
        var status = InstanceStatus(entry);
        string label = AppSettings.Current.OpenCodeAccountLabel(instance);
        string display = $"OpenCode Go·{label}";
        status.CredentialSource = "手动添加";

        string? apiKey = _goResolver.GetAccountKey(instance);
        if (apiKey is null)
        {
            // 清单与加密文件脱节(手删文件等):当作未配置,提示重加
            status.State = SourceState.AuthenticationRequired;
            status.StatusText = "Key 丢失,请删除后重新添加该账号";
            return (true, false);
        }

        try
        {
            var snapshot = await _goApi.GetUsageAsync(
                new CredentialCandidate(CredentialSource.ManualEntry, apiKey), token);
            snapshot = await MergeWebMetersAsync(snapshot, token);
            snapshot = snapshot with { AccountLabel = label };
            await new SnapshotCache(_paths, _paths.OpenCodeSnapshotFileFor(instance)).SaveAsync(snapshot, token);
            status.State = SourceState.Connected;
            status.StatusText = $"已连接 · {label}";
            status.FetchedAt = snapshot.FetchedAt;
            _lastError.TryRemove(display, out _);
            return (true, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (CommandCodeApiException ex) when (ex.IsAuthenticationError)
        {
            // 未订阅 / Key 无效:写灰快照让环留在 rail 上,并进入长静默
            await WriteUnavailableSnapshotAsync(instance, label, ex, token);
            status.State = SourceState.AuthenticationRequired;
            status.StatusText = ex.Message;
            status.FetchedAt = DateTimeOffset.Now;
            Diagnostics.Note($"{display} {ex.Message}");
            return (false, true);
        }
        catch (Exception ex)
        {
            // 普通失败:不写快照,保留最后一次成功数据
            status.State = SourceState.Stale;
            status.StatusText = ex.Message;
            if (_lastError.GetValueOrDefault(display) != ex.Message)
            {
                Diagnostics.Note($"{display} 同步失败", ex);
                _lastError[display] = ex.Message;
            }
            return (false, false);
        }
    }

    /// <summary>权限类失败时给附加账号落一个"无读数"的月池快照(灰环 + hover 说明)。</summary>
    private async Task WriteUnavailableSnapshotAsync(int instance, string label, CommandCodeApiException ex, CancellationToken token)
    {
        string note = ex.StatusCode switch
        {
            HttpStatusCode.Forbidden => "未订阅或无权读取;开通订阅后自动开始显示",
            HttpStatusCode.Unauthorized => "Key 无效或已过期,请更新",
            _ => "服务端拒绝了这次查询",
        };
        var snapshot = new UsageSnapshot(
            SourceKeys.OpenCode,
            "GO",
            label,
            CredentialSource.ManualEntry,
            [new QuotaWindow(QuotaKind.Monthly, "总额度", null, null, null, false, note, "%")],
            0, 0, null, null, null,
            DateTimeOffset.UtcNow,
            "https://opencode.ai");
        try
        {
            await new SnapshotCache(_paths, _paths.OpenCodeSnapshotFileFor(instance)).SaveAsync(snapshot, token);
        }
        catch (Exception writeEx)
        {
            Diagnostics.Note("写入不可用快照失败", writeEx);
        }
    }

    // ————————————————— 网页口径的额度读数 —————————————————
    // API Key 那个端点(/zen/go/v1/usage)只回**向下取整**的整数百分比,而网页读的是
    // meters 的微美分原值、自己四舍五入——同一个真实用量两边会差 1 个百分点
    // (实测:网页 11% / API Key 10%)。要跟网页对上,就只能走网页控制台那条通道。

    private readonly SemaphoreSlim _metersGate = new(1, 1);
    private IReadOnlyList<OpenCodeConsoleMeters>? _metersCache;
    private DateTimeOffset _metersCacheAt;

    /// <summary>
    /// 取网页控制台的额度读数。额度是**账号级**的,一轮同步里主账号与各附加账号会问
    /// 同一份数据——60 秒内复用一次拉取结果,免得同一轮里打 N 次同样的请求。
    /// 失败不缓存(下一轮照常重试)。
    /// </summary>
    private async Task<IReadOnlyList<OpenCodeConsoleMeters>?> GetWebMetersAsync(CancellationToken token)
    {
        if (_metersCache is { } cached && DateTimeOffset.Now - _metersCacheAt < TimeSpan.FromSeconds(60))
            return cached;

        await _metersGate.WaitAsync(token);
        try
        {
            if (_metersCache is { } again && DateTimeOffset.Now - _metersCacheAt < TimeSpan.FromSeconds(60))
                return again;
            var fetched = await OpenCodeConsoleClient.TryFetchMetersAsync(token);
            if (fetched is not null)
            {
                _metersCache = fetched;
                _metersCacheAt = DateTimeOffset.Now;
            }
            return fetched;
        }
        finally
        {
            _metersGate.Release();
        }
    }

    /// <summary>
    /// 用网页控制台的读数覆盖 API Key 接口那三个整数百分比。
    ///
    /// **怎么认出"哪个工作区是这个 Key 的"**:API Key 里不带工作区标识(实测 zen 接口
    /// 没有),所以拿 API 通道的值当指纹——同一份数据下网页(四舍五入)只可能等于或比
    /// API(向下取整)多 1,三个窗口全部满足才采用。一个都不满足就放弃:宁可显示 API 的
    /// 整数,也不能把别的工作区的读数安到这个账号头上。
    ///
    /// 整条通道挂在「浏览器会话明细」开关下(读 cookie 需要用户显式同意)。
    /// </summary>
    private async Task<UsageSnapshot> MergeWebMetersAsync(UsageSnapshot snapshot, CancellationToken token)
    {
        if (!AppSettings.Current.UseBrowserSessionForModelDetail) return snapshot;
        try
        {
            var all = await GetWebMetersAsync(token);
            if (all is null || all.Count == 0) return snapshot;

            var match = PickMatchingWorkspace(all, snapshot);
            if (match is null)
            {
                Diagnostics.Note($"网页额度读数与 API Key 的百分比对不上({all.Count} 个工作区),继续用 API Key 口径");
                return snapshot;
            }

            var merged = snapshot with
            {
                Windows = snapshot.Windows.Select(w => MergeMeter(w, match)).ToList(),
            };

            // 顺手把**官方账期 token** 也带上:卡片头部因此从"本月额度已用 17%"变成
            // "5.7亿 · 本月已用 Token"(与 GOAT 卡片同一个位置、同一种读法)。百分比回答不了
            // "一个月到底能用多少 token",官方这个滚动 30 天窗口才回答得了。
            // 数据源是控制台聚合接口,自带 15 分钟缓存,不会每轮同步都去打服务端。
            // 这一步失败(接口慢/超时)不能拖垮整轮同步:给它单独的短超时,拿不到就只是
            // "卡片这次退回显示百分比",额度读数一点不受影响。外层取消照旧往外抛。
            using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            statsCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stats = await OpenCodeUsageStatsClient.TryFetchAsync("30d", statsCts.Token);
                if (stats is { TotalTokens: > 0 })
                {
                    // PeriodCost 不填:GOAT 的同名字段是 credits,这里拿到的是美元,两个单位
                    // 塞进一个字段迟早有人当成同一个东西用。要美元金额的地方直接读官方读数。
                    merged = merged with
                    {
                        PeriodTokens = stats.TotalTokens,
                        PeriodRequests = stats.Requests,
                    };
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Diagnostics.Note("读取官方 30 天 token 超时,悬浮卡这次退回显示百分比");
            }
            return merged;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Note("合并网页额度读数失败", ex);
            return snapshot;
        }
    }

    private static OpenCodeConsoleMeters? PickMatchingWorkspace(
        IReadOnlyList<OpenCodeConsoleMeters> all, UsageSnapshot snapshot)
    {
        foreach (var meters in all)
        {
            if (WorkspaceMatches(meters, snapshot)) return meters;
        }
        return null;
    }

    /// <summary>诊断参数 --go-meters 用的入口(把指纹匹配结果单独打出来核对)。</summary>
    internal static OpenCodeConsoleMeters? PickMatchingWorkspaceForDiagnostics(
        IReadOnlyList<OpenCodeConsoleMeters> all, UsageSnapshot snapshot) =>
        PickMatchingWorkspace(all, snapshot);

    private static bool WorkspaceMatches(OpenCodeConsoleMeters meters, UsageSnapshot snapshot)
    {
        bool comparedAny = false;
        foreach (var (kind, reading) in new (QuotaKind, OpenCodeConsoleWindow?)[]
                 {
                     (QuotaKind.FiveHour, meters.FiveHour),
                     (QuotaKind.Weekly, meters.Weekly),
                     (QuotaKind.Monthly, meters.Monthly),
                 })
        {
            if (reading is null) continue;
            if (snapshot.Find(kind)?.Used is not { } apiUsed) continue;
            comparedAny = true;
            // 网页四舍五入、API 向下取整 → 网页 ≥ API,且最多多 1
            if (reading.Percent < apiUsed || reading.Percent > apiUsed + 1) return false;
        }
        return comparedAny;
    }

    /// <summary>把网页读数盖到对应窗口上。用精确小数(不是网页那个整数)填 Used——
    /// 进度条更准,而显示用的百分比仍会四舍五入成与网页完全一样的整数。</summary>
    private static QuotaWindow MergeMeter(QuotaWindow window, OpenCodeConsoleMeters meters)
    {
        var reading = window.Kind switch
        {
            QuotaKind.FiveHour => meters.FiveHour,
            QuotaKind.Weekly => meters.Weekly,
            QuotaKind.Monthly => meters.Monthly,
            _ => null,
        };
        if (reading is null) return window;
        return window with
        {
            Used = reading.ExactPercent,
            Cap = 100,
            ResetAt = reading.ResetAt ?? window.ResetAt,
            ReadingFrom = "网页控制台",
            UsedAmount = reading.UsedUsd,
            LimitAmount = reading.LimitUsd,
        };
    }

    // ————————————————— OpenCode Go 附加账号管理 —————————————————
    // 设置页「账号」区块的入口。Key 的验证、DPAPI 落盘、清单与可见性的联动都在这,
    // 设置页只负责收输入与展示结果。

    /// <summary>主账号当前命中(优先级最高)的 Key 尾号,设置页显示用;没有凭据返回空。</summary>
    public string OpenCodeMainKeyTail() => KeyTail(_goResolver.DiscoverCandidates().FirstOrDefault()?.ApiKey);

    /// <summary>某附加账号的 Key 尾号;账号不存在或 Key 丢失返回空。</summary>
    public string OpenCodeAccountTail(int id) => KeyTail(_goResolver.GetAccountKey(id));

    private static string KeyTail(string? apiKey) =>
        string.IsNullOrEmpty(apiKey) || apiKey.Length <= 4 ? "" : apiKey[^4..];

    /// <summary>
    /// 添加一个附加账号。**403(未订阅)允许保存**——用户完全可能先加账号、后开订阅
    /// (本机的 go2 就是这个状态);只有 Key 确认无效(401)或连不上才拒绝。
    /// </summary>
    public async Task<AccountOperationResult> AddOpenCodeAccountAsync(string apiKey, string label, CancellationToken token = default)
    {
        apiKey = apiKey.Trim();
        label = label?.Trim() ?? "";
        if (apiKey.Length == 0) return AccountOperationResult.Fail("请先粘贴 API Key。");

        var settings = AppSettings.Current;
        bool AlreadyExists(string key) =>
            _goResolver.DiscoverCandidates().Any(c => string.Equals(c.ApiKey, key, StringComparison.Ordinal))
            || _goResolver.LoadAccounts().Any(a => string.Equals(a.ApiKey, key, StringComparison.Ordinal));
        if (AlreadyExists(apiKey)) return AccountOperationResult.Fail("这个 Key 已经添加过了。");

        bool subscribed;
        try
        {
            await _goApi.GetUsageAsync(new CredentialCandidate(CredentialSource.ManualEntry, apiKey), token);
            subscribed = true;
        }
        catch (CommandCodeApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            subscribed = false;
        }
        catch (CommandCodeApiException ex)
        {
            return AccountOperationResult.Fail(ex.Message);
        }
        catch (Exception)
        {
            return AccountOperationResult.Fail("连接 OpenCode Go 失败,请检查网络后重试。");
        }

        int id = settings.NextOpenCodeAccountId();
        var accounts = _goResolver.LoadAccounts().ToList();
        accounts.Add(new StoredOpenCodeAccount(id, apiKey));
        _goResolver.SaveAccounts(accounts);

        settings.AddOpenCodeAccount(new OpenCodeAccountInfo(id, label));
        string entry = SourceKeys.InstanceKey(SourceKeys.OpenCode, id);
        if (!settings.VisibleSources.Contains(entry)) settings.VisibleSources.Add(entry);
        settings.Save();
        RequestRefreshNow();

        return AccountOperationResult.Success(subscribed
            ? $"已添加并连接成功(账号 {id})。"
            : $"已添加(账号 {id})。这个 Key 当前未订阅,开通订阅后会自动开始显示。");
    }

    /// <summary>删除附加账号:加密清单、设置、可见性、快照四处一起清,不留幽灵环。</summary>
    public void RemoveOpenCodeAccount(int id)
    {
        var settings = AppSettings.Current;
        var accounts = _goResolver.LoadAccounts()
            .Where(a => a.Id != id)
            .ToList();
        _goResolver.SaveAccounts(accounts);

        settings.RemoveOpenCodeAccount(id);
        settings.VisibleSources.Remove(SourceKeys.InstanceKey(SourceKeys.OpenCode, id));
        settings.Save();

        try
        {
            if (File.Exists(_paths.OpenCodeSnapshotFileFor(id))) File.Delete(_paths.OpenCodeSnapshotFileFor(id));
        }
        catch (IOException)
        {
        }
        RequestRefreshNow();
    }

    /// <summary>改附加账号的标签(环标题、额度条、告警气泡里显示的名字)。</summary>
    public void RenameOpenCodeAccount(int id, string label)
    {
        var settings = AppSettings.Current;
        settings.RenameOpenCodeAccount(id, label);
        settings.Save();
        RequestRefreshNow();
    }

    /// <summary>附加账号是否显示在 rail 上(不显示 = 连 API 都不同步,与整源一个语义)。</summary>
    public void SetOpenCodeAccountVisible(int id, bool visible)
    {
        var settings = AppSettings.Current;
        string entry = SourceKeys.InstanceKey(SourceKeys.OpenCode, id);
        if (visible)
        {
            if (!settings.VisibleSources.Contains(entry)) settings.VisibleSources.Add(entry);
        }
        else
        {
            settings.VisibleSources.Remove(entry);
        }
        settings.Save();
        RequestRefreshNow();
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
        _metersGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

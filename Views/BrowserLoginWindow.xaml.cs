using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace PulseWin;

/// <summary>
/// 通用的**内嵌 WebView2 登录窗**:打开某个官方站点的登录页,登录成功后直接从浏览器引擎的
/// cookie 罐里取会话 cookie。目标站由 <see cref="BrowserLoginTarget"/> 决定。
///
/// **为什么必须这么做**(2026-10-10 实测,推翻了 v1.17.0 的结论):从本机 Edge 的 cookie
/// 数据库里读这条路已经**彻底**不通——不只是"浏览器运行时文件被排他锁定"那么简单。
/// 杀掉全部 <c>msedge.exe</c> 之后库确实能复制了(锁是 Edge「启动加速」的后台进程持有的),
/// 但复制出来也没用:Edge 154 里 **702 条 cookie 全部是 <c>v20</c> 前缀**,即 Chromium 的
/// App-Bound Encryption,密钥按浏览器可执行文件路径 + 用户绑定、由浏览器自己的提权服务
/// 解封,第三方进程解不出来(这正是它的设计目的)。opencode 与 commandcode 的会话 cookie
/// 一条都拼不进 Cookie 头。
///
/// <see cref="CoreWebView2.CookieManager"/> 给的是**引擎已经解密**的 cookie,既不碰文件
/// 也不碰 ABE;HttpOnly 也拿得到——这是 <c>document.cookie</c> 做不到的。GoGauge 走的就是
/// 这条路。
///
/// **一份 profile 同时只能被一个进程用**(2026-10-10 实测踩到):WebView2 的 user data
/// folder 是跨进程独占的,第二个窗口(哪怕在另一个 PulseWin 进程里)会**卡在初始化、
/// 浏览器进程永远起不来**,界面就是一块空白、状态停在"正在载入登录页…"。所以这里有两道
/// 防护:①跨进程命名互斥量,已经有登录窗开着就直接说清楚而不是白屏;②整个进程共用一份
/// <see cref="CoreWebView2Environment"/>(同进程内重复 CreateAsync 也一样会出问题),
/// 且初始化带超时,超时就把原因写到状态行与诊断日志,不再无限期静默等待。
/// </summary>
public partial class BrowserLoginWindow : Window
{
    /// <summary>跨进程互斥:同一时刻只允许一个登录窗(WebView2 profile 是跨进程独占的)。</summary>
    private static readonly Mutex LoginGate = new(initiallyOwned: false, @"Local\PulseWin.BrowserLoginWindow");

    /// <summary>整个进程共用一份 WebView2 环境。</summary>
    private static CoreWebView2Environment? _sharedEnvironment;

    /// <summary>初始化超时:WebView2 起不来时不要无限期等下去。</summary>
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(25);

    private readonly BrowserLoginTarget _target;
    private readonly string _userDataFolder;
    private readonly bool _ownsGate;
    private DispatcherTimer? _poll;
    /// <summary>轮询重入守卫:上一次 await 还没回来就不再发一次。</summary>
    /// <summary>捕获与看门狗各用一个标志。共用时 Tick 里先跑的那个会把标志立起来,
    /// 后跑的那个每次都第一行返回——2FA 自动续跑因此从未执行过。</summary>
    private bool _polling;
    private bool _resuming;
    private bool _captured;
    /// <summary>GitHub 已登录、却停在无关页面(2FA/passkey 之后常见的 return_to 丢失)的起点时刻;
    /// 有值就交给看门狗,过了宽限期自动重新发起登录。<c>default</c> = 没卡着。</summary>
    private DateTimeOffset _githubStuckSince;
    /// <summary>本轮已自动重试过几次(防止来回踢成死循环)。</summary>
    private int _autoResumes;
    private const int MaxAutoResumes = 3;
    /// <summary>在 GitHub 无关页面上停多久才判定"卡住"(给正常回跳留时间)。</summary>
    private static readonly TimeSpan StuckGrace = TimeSpan.FromSeconds(4);
    private int _navHelperInjectedFor;

    /// <param name="target">目标站(登录页、会话 cookie 名、落盘键)。</param>
    /// <param name="userDataFolder">WebView2 的 profile 目录(放便携 Data\ 下)。两个站共用同一份。</param>
    public BrowserLoginWindow(BrowserLoginTarget target, string userDataFolder)
    {
        InitializeComponent();
        _target = target;
        _userDataFolder = userDataFolder;
        Title = $"登录 {target.DisplayName}";
        HeadingText.Text = $"用 GitHub 或 {target.DisplayName} 账号登录一次";
        try { _ownsGate = LoginGate.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsGate = true; }   // 上一个进程被强杀:所有权归我们
        Loaded += OnLoadedAsync;
    }

    /// <summary>取到的 Cookie 头(诊断/日志用);未取到为 null。</summary>
    public string? CapturedCookieHeader { get; private set; }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (!_ownsGate)
        {
            StatusText.Text = "已经有一个登录窗开着(同一个 WebView2 profile 一次只能被一个进程使用)。"
                + "请先把它关掉,再点这里的登录。";
            RestartButton.IsEnabled = false;
            Diagnostics.Note($"打开 {_target.DisplayName} 登录窗被拒:已有登录窗在运行");
            return;
        }

        try
        {
            Directory.CreateDirectory(_userDataFolder);
            Diagnostics.Note($"{_target.DisplayName} 登录窗:开始初始化 WebView2(profile {_userDataFolder})");

            var environment = await GetEnvironmentAsync(_userDataFolder);
            await View.EnsureCoreWebView2Async(environment).WaitAsync(InitTimeout);

            var core = View.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationCompleted += OnNavigationCompleted;
            // 浏览器/渲染进程挂掉时界面会变空白——记下来,别静默
            core.ProcessFailed += (_, args) =>
            {
                StatusText.Text = $"WebView2 的 {args.ProcessFailedKind} 进程异常退出,请关掉本窗口重试。";
                Diagnostics.Note($"{_target.DisplayName} 登录窗:{args.ProcessFailedKind} 退出({args.Reason})");
            };
            core.Navigate(_target.LoginUrl);
            Diagnostics.Note($"{_target.DisplayName} 登录窗:WebView2 就绪,已导航到登录页");

            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _poll.Tick += (_, _) =>
            {
                _ = TryCaptureAsync();
                _ = ResumeIfStuckAsync();
            };
            _poll.Start();
        }
        catch (TimeoutException)
        {
            StatusText.Text = "WebView2 初始化超时(浏览器进程没能启动)。"
                + "常见原因是另一个登录窗/另一个 PulseWin 还占着同一份 profile——"
                + "把它们关掉再试;若反复如此,可把 Data\\WebView2 目录删掉重来。";
            Diagnostics.Note($"{_target.DisplayName} 登录窗:WebView2 初始化超时({InitTimeout.TotalSeconds:0} 秒)");
        }
        catch (Exception ex)
        {
            StatusText.Text = "登录窗启动失败:" + ex.Message
                + "(若提示缺少 WebView2 运行时,装一次「Microsoft Edge WebView2 Runtime」即可)";
            Diagnostics.Note($"{_target.DisplayName} 登录窗启动失败", ex);
        }
    }

    /// <summary>
    /// 取(并在首次创建)本进程共用的 WebView2 环境。
    /// **必须共用**:同一个 user data folder 在本进程里第二次 <c>CreateAsync</c> 会失败或
    /// 长时间不就绪——"开一次登录窗、关掉、再开"正好会撞上。
    /// </summary>
    private static async Task<CoreWebView2Environment> GetEnvironmentAsync(string userDataFolder)
    {
        if (_sharedEnvironment is { } existing) return existing;

        var gate = EnvironmentGate;
        await gate.WaitAsync();
        try
        {
            _sharedEnvironment ??= await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder)
                .WaitAsync(InitTimeout);
            return _sharedEnvironment;
        }
        finally
        {
            gate.Release();
        }
    }

    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        string url = View.CoreWebView2?.Source ?? "";

        if (IsTargetHost(url))
        {
            _githubStuckSince = default;
            StatusText.Text = $"已回到 {_target.DisplayName},正在确认会话…";
            return;
        }

        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            // 停在登录表单 / 两步验证 / 设备验证这些"等人操作"的页是正常的;
            // 停在别的 GitHub 页面多半就是 2FA 之后 return_to 丢了,交给看门狗自动续跑。
            _githubStuckSince = IsGitHubFlowPage(url) ? default : DateTimeOffset.Now;
            StatusText.Text = _githubStuckSince == default
                ? "正在 GitHub 登录…"
                : "这页与授权无关;几秒内没往回跳就自动重新发起登录(也可点左下角「继续登录」)。";
            _ = InjectNavHelperAsync();
        }
        else
        {
            _githubStuckSince = default;
            if (!e.IsSuccess)
            {
                StatusText.Text = $"这一步没载入成功({e.WebErrorStatus});可点「重新载入」重试。";
            }
        }
    }

    /// <summary>按 **主机名** 判断是不是目标站(不能拿 URL 子串判断:GitHub 的 OAuth 地址里
    /// 带着目标站域名,子串匹配会把 GitHub 页面误判成"已回到目标站")。</summary>
    private bool IsTargetHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string host = new Uri(_target.Site).Host;
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GitHub 流程里"等用户操作"的页(登录表单 / 会话 / 两步验证)——停在这儿算正常。</summary>
    private static bool IsGitHubFlowPage(string url) =>
        url.Contains("github.com/login", StringComparison.OrdinalIgnoreCase)
        || url.Contains("github.com/sessions", StringComparison.OrdinalIgnoreCase)
        || url.Contains("github.com/two_factor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 看门狗:**只在"GitHub 已登录、却停在无关页面"时才重新发起登录**。
    ///
    /// 这是 GoGauge 踩过的坑:开了两步验证/passkey 的账号走 OAuth,GitHub 有时不再回跳,
    /// 而是把窗口留在 github.com 的某个设置页(本机实测:停在 "Configure passwordless
    /// authentication"),于是登录永远完不成。这里确认 GitHub 侧确实已登录
    /// (<c>meta[name=user-login]</c> 有值)之后,重新导航回官方登录页——此时 GitHub 会话
    /// 已经建立,整条 OAuth 会顺跑完。最多重试 <see cref="MaxAutoResumes"/> 次。
    /// </summary>
    private async Task ResumeIfStuckAsync()
    {
        if (_captured || _resuming) return;
        if (_githubStuckSince == default || _autoResumes >= MaxAutoResumes) return;
        if (DateTimeOffset.Now - _githubStuckSince < StuckGrace) return;

        var core = View.CoreWebView2;
        if (core is null) return;

        _resuming = true;
        try
        {
            // 没登录就别打扰(用户可能正在填密码,只是 URL 还没变成 flow 页)
            string loggedIn = await core.ExecuteScriptAsync(
                "document.querySelector('meta[name=\"user-login\"]')?.getAttribute('content') || ''");
            if (loggedIn.Trim('"', ' ').Length == 0) return;

            _autoResumes++;
            _githubStuckSince = default;
            StatusText.Text = $"GitHub 已登录但这页与授权无关,自动重新发起登录"
                + $"({_autoResumes}/{MaxAutoResumes})…";
            Diagnostics.Note($"{_target.DisplayName} 登录:GitHub 停在无关页面,自动续跑第 {_autoResumes} 次");
            core.Navigate(_target.LoginUrl);
        }
        catch (Exception ex)
        {
            Diagnostics.Note("登录窗自动续跑失败", ex);
        }
        finally
        {
            _resuming = false;
        }
    }

    /// <summary>
    /// 在非目标站的页面上注入一条悬浮导航(WebView2 发布版没有后退按钮,GitHub 侧一旦跳到
    /// 无关页面就回不来了)。看门狗是自动那条路,这条是给用户手动兜底的。
    /// </summary>
    private async Task InjectNavHelperAsync()
    {
        var core = View.CoreWebView2;
        if (core is null) return;
        // 同一个文档只注入一次(导航后 __pulsewinNav 随文档消失,会重新注入)
        int stamp = core.Source?.GetHashCode() ?? 0;
        if (stamp == _navHelperInjectedFor) return;
        _navHelperInjectedFor = stamp;

        string script = NavHelperScript.Replace(
            "__RESUME__", JsonSerializer.Serialize(_target.LoginUrl));
        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Diagnostics.Note("登录窗注入导航条失败", ex);
        }
    }

    private const string NavHelperScript = """
        (function () {
          if (window.__pulsewinNav) { window.__pulsewinResume = __RESUME__; return; }
          window.__pulsewinNav = 1;
          window.__pulsewinResume = __RESUME__;
          var css = 'padding:6px 12px;margin-right:8px;border-radius:6px;'
                  + 'border:1px solid rgba(0,0,0,.25);background:#fff;color:#111;'
                  + 'cursor:pointer;box-shadow:0 1px 4px rgba(0,0,0,.35);'
                  + 'font:12px system-ui,sans-serif;white-space:nowrap;';
          function mk(label, fn) {
            var b = document.createElement('button');
            b.textContent = label; b.style.cssText = css; b.onclick = fn; return b;
          }
          var bar = document.createElement('div');
          bar.style.cssText = 'position:fixed;left:10px;bottom:10px;z-index:2147483647;';
          bar.appendChild(mk('\u2190 \u8fd4\u56de', function () { history.back(); }));
          bar.appendChild(mk('\u7ee7\u7eed\u767b\u5f55 \u2192',
            function () { location.assign(window.__pulsewinResume); }));
          (document.body || document.documentElement).appendChild(bar);
        })();
        """;

    /// <summary>
    /// 每秒问一次引擎的 cookie 罐。拿到会话 cookie 就拼成 Cookie 头交给
    /// <see cref="BrowserCookieStore"/> 落盘,然后自己关窗。
    /// </summary>
    private async Task TryCaptureAsync()
    {
        if (_captured || _polling) return;
        var core = View.CoreWebView2;
        if (core is null) return;

        _polling = true;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(_target.Site);
            var header = BuildCookieHeader(cookies, _target.SessionCookieNames);
            if (header is null) return;

            CapturedCookieHeader = header;
            _captured = true;
            _poll?.Stop();
            BrowserCookieStore.SaveManual(_target.HostKeyword, header);
            StatusText.Text = "登录成功,已保存会话 cookie。";
            Diagnostics.Note($"{_target.DisplayName} 登录成功,已保存会话 cookie");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Diagnostics.Note("登录窗读取 cookie 失败", ex);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// 把引擎给的 cookie 拼成 Cookie 头。整个目标域的都带上(与真实请求一致,也顺带覆盖
    /// 只用旧版名字的历史账号);<paramref name="sessionCookieNames"/> 一条都没命中就返回
    /// null——那说明登录还没完成。
    /// </summary>
    internal static string? BuildCookieHeader(
        IEnumerable<CoreWebView2Cookie> cookies, IReadOnlyList<string> sessionCookieNames)
    {
        var pairs = new List<string>();
        bool hasSession = false;
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Value)) continue;
            pairs.Add($"{cookie.Name}={cookie.Value}");
            if (sessionCookieNames.Contains(cookie.Name, StringComparer.Ordinal)) hasSession = true;
        }
        return hasSession ? string.Join("; ", pairs) : null;
    }

    // ————————————————— 按钮 —————————————————

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        _githubStuckSince = default;
        _autoResumes = 0;
        View.CoreWebView2?.Navigate(_target.LoginUrl);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll?.Stop();
        _poll = null;
        // WebView2 不显式释放的话,它的浏览器进程会跟着本进程活着;关窗时清掉。
        try { View.Dispose(); } catch { /* 已经关了 */ }
        if (_ownsGate)
        {
            try { LoginGate.ReleaseMutex(); } catch (ApplicationException) { /* 不属于本线程 */ }
        }
        base.OnClosed(e);
    }
}

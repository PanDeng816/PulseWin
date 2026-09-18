using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 版本更新检查:**只提示,不自动装**。上游 Pulse 用 Sparkle 也是这个立场——
/// 一个整天待在屏幕边上的程序,不该在用户没同意的时候把二进制换掉。
///
/// 每两小时查一次(上游 1.2.1 把频率从一天一次提到两小时,理由是排错期间
/// 一天一次太慢)。
/// </summary>
public static class UpdateChecker
{
    public sealed record Release(Version Version, string Tag, string Title, string Url, DateTimeOffset? PublishedAt);

    private const string LatestApi = "https://api.github.com/repos/PanDeng816/PulseWin/releases/latest";

    private static readonly HttpClient Http = CreateClient();
    private static Timer? _timer;
    private static int _checking;

    /// <summary>发现的可用更新(已是本机更新的版本);null = 没有或还没查过。</summary>
    public static Release? Available { get; private set; }

    /// <summary>上次检查失败的原因(界面要说出来,而不是静默装没查过)。</summary>
    public static string? LastError { get; private set; }

    public static DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>检查结果有变化(界面菜单要重画)。</summary>
    public static event Action? Changed;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API 不接受空 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseWin/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>启动:30 秒后先查一次,之后每两小时一次。</summary>
    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(2));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>查一次。失败只记原因,不打扰用户——网络不通不是用户此刻要处理的事。</summary>
    public static async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var release = await FetchAsync().ConfigureAwait(false);
            LastError = null;
            LastCheckedAt = DateTimeOffset.Now;
            var newer = release is not null && release.Version > CurrentVersion ? release : null;
            bool changed = newer?.Tag != Available?.Tag;
            Available = newer;
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastCheckedAt = DateTimeOffset.Now;
            Diagnostics.Note("检查更新失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static async Task<Release?> FetchAsync()
    {
        using var response = await Http.GetAsync(LatestApi).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub 返回 {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (tag.Length == 0) return null;
        // tag 形如 v1.2.1;解析不了的 tag(如带后缀的预发布)当作没有更新
        string numeric = tag.TrimStart('v', 'V');
        int dash = numeric.IndexOfAny(['-', '+']);
        if (dash > 0) numeric = numeric[..dash];
        if (!Version.TryParse(numeric, out var version)) return null;

        string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        string title = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        DateTimeOffset? published = root.TryGetProperty("published_at", out var p)
            && DateTimeOffset.TryParse(p.GetString(), out var parsed) ? parsed : null;
        return new Release(version, tag, title, url, published);
    }
}

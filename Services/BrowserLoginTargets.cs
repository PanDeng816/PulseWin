namespace PulseWin;

/// <summary>
/// 一个「浏览器登录窗」的目标站:开哪个登录页、登录完从哪个地址的 cookie 罐里取哪条会话
/// cookie、取到后存进 <see cref="BrowserCookieStore"/> 的哪个键。
///
/// **为什么要有这张表**:两家(Command Code 与 OpenCode)的登录流程形状完全一样——都是
/// 官方站点的 signin 页 → "Continue with GitHub" → GitHub OAuth → 回跳写会话 cookie。
/// 所以登录窗只需按这张表参数化,不必一个站写一个窗口。加第三个站时只加一条记录。
/// </summary>
/// <param name="HostKeyword">交给 <see cref="BrowserCookieStore"/> 的站点键(也是
/// <c>Data\browser-cookie-&lt;键&gt;.bin</c> 的文件名部分)。</param>
/// <param name="DisplayName">界面上的站点名(窗口标题、设置页文案)。</param>
/// <param name="LoginUrl">登录页地址(带回跳参数)。</param>
/// <param name="Site">取 cookie 用的地址——返回的正是"发给它时会带上的那些 cookie"。</param>
/// <param name="SessionCookieNames">按优先级排的会话 cookie 名;至少命中一个才算登录成功
/// (新版名在前,旧版名兜历史账号)。</param>
public sealed record BrowserLoginTarget(
    string HostKeyword,
    string DisplayName,
    string LoginUrl,
    string Site,
    IReadOnlyList<string> SessionCookieNames);

/// <summary>本机支持的全部登录目标(见 <see cref="BrowserLoginTarget"/>)。</summary>
public static class BrowserLoginTargets
{
    /// <summary>OpenCode:控制台登录页,会话 cookie 是 <c>__Host-console_session</c>
    /// (2026-09 前端改版后的新名,旧账号是 <c>auth</c>)。</summary>
    public static BrowserLoginTarget OpenCode { get; } = new(
        HostKeyword: "opencode",
        DisplayName: "opencode.ai",
        LoginUrl: "https://opencode.ai/console/login?next=%2Fconsole%2F",
        Site: "https://opencode.ai",
        SessionCookieNames: ["__Host-console_session", "auth"]);

    /// <summary>Command Code:登录页在 <c>/signin</c>(回跳 studio),走的同样是 GitHub OAuth
    /// (client_id 见 Edge 历史里的 <c>github.com/login/oauth/authorize</c>,回调落在
    /// <c>api.commandcode.ai/auth/callback/github</c>)。会话 cookie 实测为
    /// <c>__Secure-commandcode_prod_.session_token</c>(域 <c>.commandcode.ai</c>)。</summary>
    public static BrowserLoginTarget CommandCode { get; } = new(
        HostKeyword: "commandcode",
        DisplayName: "commandcode.ai",
        LoginUrl: "https://commandcode.ai/signin?returnTo=%2Fstudio",
        Site: "https://commandcode.ai",
        SessionCookieNames: ["__Secure-commandcode_prod_.session_token"]);

    /// <summary>设置页与命令行按站点键查(未知键返回 null)。</summary>
    public static BrowserLoginTarget? ByKey(string key) =>
        key switch
        {
            "opencode" => OpenCode,
            "commandcode" => CommandCode,
            _ => null,
        };
}

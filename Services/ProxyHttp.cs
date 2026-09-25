using System.Net;
using System.Net.Http;

namespace PulseWin;

/// <summary>
/// 统一的 HttpClient 出口:按设置决定代理行为(0=跟随系统、1=直连、2=手动 HTTP 代理)。
/// 价目表更新、检查更新、三个数据源 API 都从这里拿 client。
/// 注意:HttpClient 是长生命周期对象,改了代理设置重启程序才生效。
/// </summary>
public static class ProxyHttp
{
    /// <summary>给"手动代理"输入的占位示例,设置界面复用同一句。</summary>
    public const string AddressHint = "http://127.0.0.1:7890";

    public static HttpClient Create(TimeSpan? timeout = null, Action<HttpClientHandler>? configure = null)
    {
        var handler = new HttpClientHandler();
        var s = AppSettings.Current;
        if (s.ProxyMode == 1)
        {
            handler.UseProxy = false;
        }
        else if (s.ProxyMode == 2 && !string.IsNullOrWhiteSpace(s.ProxyAddress))
        {
            try
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(s.ProxyAddress.Trim());
            }
            catch
            {
                // 地址解析不了就退回系统默认,别让一个坏地址拖死所有请求
                handler.UseProxy = false;
            }
        }
        configure?.Invoke(handler);
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }
}

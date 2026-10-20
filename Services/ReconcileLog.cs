using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 对账历史落盘（v1.22.1）。每次渲染「官方 vs 本机」对照卡追加一条 JSONL，
/// 供一个月后做"重置残留审计"（每个窗口重置时剩多少额度被作废）。
///
/// 只记**这一刻看到的两个数**，不推断、不补数：官方含其他设备/Key，本机是这台电脑，
/// 两者窗口也可能不同（官方只有滚动窗口）。记录里连窗口标签一起存，否则以后对不上。
/// 同渠道、同窗口、同一分钟内重复渲染只留第一条——切 tab/换区间会重画，不去重会把
/// 同一次观察写成几十条。
/// </summary>
internal static class ReconcileLog
{
    private static readonly object Gate = new();
    private static readonly Lazy<string> FilePath = new(() => new AppDataPaths().ReconcileFile);

    /// <summary>JSONL 必须无 BOM：第一行带 BOM 会让所有按行解析的脚本（jq/python）读不出第一条。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 中文窗口标签直出，别写成 \u8FD1 24 小时——这文件是给人看的
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string _lastKey = "";

    public static void Append(string channel, string window, long official, long local, string verdict)
    {
        try
        {
            string key = $"{channel}|{window}|{DateTime.Now:yyyy-MM-dd HH:mm}";
            lock (Gate)
            {
                if (key == _lastKey) return;
                _lastKey = key;
            }

            var record = new
            {
                ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                channel,
                window,
                official,
                local,
                diff = official - local,
                verdict,
            };
            string line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
            lock (Gate)
            {
                System.IO.File.AppendAllText(FilePath.Value, line, Utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            // 诊断用途：落盘失败不打断界面
            Diagnostics.Note("对账历史落盘失败", ex);
        }
    }
}

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// DeepSeek 余额的**按小时采样账本**。
///
/// DeepSeek 的 API 不提供任何用量或消费历史(只有当前余额),所以"今天花了多少"
/// 只能靠本程序自己看:每次同步成功记下当时余额,每小时留一个点,相邻两点的差额
/// 就是那一小时的消耗。
///
/// 它衡量的是"本程序观察到的余额下降",不是 DeepSeek 的账单——程序没运行的那些
/// 小时没有采样,算不出消耗,柱状图上就是空的。这一点必须诚实:宁可缺一根柱子,
/// 也不能把"没看见"画成"没花钱"。
/// </summary>
public sealed class DeepSeekLedger
{
    /// <summary>保留最近多少天(每小时一个点,7 天 = 168 个数字,很小)。</summary>
    private const int KeepDays = 7;

    /// <summary>小时键的格式(本地时间)。</summary>
    private const string HourFormat = "yyyy-MM-ddTHH";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _file;
    private readonly object _gate = new();

    public DeepSeekLedger(AppDataPaths paths) => _file = paths.DeepSeekLedgerFile;

    /// <summary>记下这一时刻的余额,覆盖当前小时的点(一小时内多次同步只留最后一次)。</summary>
    public void Record(string currency, double balance, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(currency)) return;

        lock (_gate)
        {
            var all = LoadAll();
            if (!all.TryGetValue(currency, out var hours))
            {
                hours = new Dictionary<string, double>();
                all[currency] = hours;
            }

            hours[HourKey(now)] = balance;

            // 清掉过期的小时点,文件不会无限长
            string cutoff = now.ToLocalTime().AddDays(-KeepDays)
                .ToString(HourFormat, CultureInfo.InvariantCulture);
            foreach (var stale in hours.Keys
                         .Where(key => string.CompareOrdinal(key, cutoff) < 0)
                         .ToList())
            {
                hours.Remove(stale);
            }

            Save(all);
        }
    }

    /// <summary>
    /// 今天 0~23 点每小时的消耗金额(索引 = 小时)。只有**连续两个相邻小时都有采样**
    /// 且余额下降时才算出值;余额上升(充值)记 0。
    /// </summary>
    public double[] TodayHourlySpend(string currency, DateTimeOffset now)
    {
        var result = new double[24];
        if (string.IsNullOrWhiteSpace(currency)) return result;

        lock (_gate)
        {
            if (!LoadAll().TryGetValue(currency, out var hours)) return result;

            DateTime day = now.ToLocalTime().Date;
            double? previous = null;
            int previousHour = -2;

            for (int hour = 0; hour < 24; hour++)
            {
                string key = day.AddHours(hour).ToString(HourFormat, CultureInfo.InvariantCulture);
                if (!hours.TryGetValue(key, out double balance)) continue;

                // 只有紧邻的上一小时才能相减:中间断了采样,差额就不属于这一小时
                if (previous is { } before && previousHour == hour - 1 && before > balance)
                {
                    result[hour] = before - balance;
                }

                previous = balance;
                previousHour = hour;
            }
        }

        return result;
    }

    private static string HourKey(DateTimeOffset now) =>
        now.ToLocalTime().ToString(HourFormat, CultureInfo.InvariantCulture);

    /// <summary>币种 → 小时键 → 余额。</summary>
    private Dictionary<string, Dictionary<string, double>> LoadAll()
    {
        try
        {
            if (!File.Exists(_file)) return new Dictionary<string, Dictionary<string, double>>();
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(
                File.ReadAllText(_file), JsonOptions)
                ?? new Dictionary<string, Dictionary<string, double>>();
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取 DeepSeek 采样账本失败", ex);
            return new Dictionary<string, Dictionary<string, double>>();
        }
    }

    private void Save(Dictionary<string, Dictionary<string, double>> all)
    {
        try
        {
            AtomicFile.WriteText(_file, JsonSerializer.Serialize(all, JsonOptions));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("保存 DeepSeek 采样账本失败", ex);
        }
    }
}

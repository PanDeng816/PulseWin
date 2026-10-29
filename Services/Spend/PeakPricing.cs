namespace PulseWin;

/// <summary>
/// DeepSeek 系渠道（官方 API，以及按官方牌价计费的 GOAT / OpenCode Go）的**峰谷计价规则**。
///
/// 现行规则（2026-09-10 12:00 起执行，2026-10-10 核对官方价格页）：
///   · 高峰 = **周一至周五** 09:00–12:00、14:00–18:00（北京时间），**不含法定节假日**；
///   · 其余全部时间为谷价：含工作日夜间与午休，以及**周末、法定节假日全天**；
///   · 谷价 = 高峰价 × 50%。
///
/// 2026-08-23 官方另把周六、周日整天统一为谷价（此前周末仍按小时分段），本类一并覆盖。
///
/// **口径前提**：本机时区即北京时间（UTC+8）。库里的小时行与明细时间戳都是本机时间，
/// 机器不在东八区时判断会整体偏移——这是既有假设（旧实现同样按本机小时判峰），不是本类新增的约束。
///
/// 为什么不能只按小时判断：同一小时在周二 10 点按峰价、在周六 10 点按谷价。
/// 任何"只拿 24 个小时桶求和"的算法都会把周末/节假日的用量错算成峰时，本类按**具体时刻**判。
/// </summary>
public static class PeakPricing
{
    /// <summary>一段高峰窗口（当日分钟数，半开区间 [Start, End)）。</summary>
    public readonly record struct Window(int Start, int End);

    /// <summary>工作日高峰窗口：09:00–12:00、14:00–18:00。</summary>
    public static readonly Window[] Windows = [new(9 * 60, 12 * 60), new(14 * 60, 18 * 60)];

    /// <summary>界面文案用的窗口描述（与 <see cref="Windows"/> 同源，别另写一份）。</summary>
    public const string WindowText = "工作日 09–12 / 14–18";

    /// <summary>
    /// 2026 年中国法定节假日中**落在周一至周五**的日期。
    /// 周末本身全天谷价，所以周六日（含调休上班的周末）不必列出。
    /// 数据与官方放假安排一致：元旦 2 天、春节 6 天、清明 1 天、劳动节 3 天、端午 1 天、中秋 1 天、国庆 5 天。
    /// </summary>
    private static readonly string[] Holidays2026 =
    [
        "2026-01-01", "2026-01-02",                                      // 元旦
        "2026-02-16", "2026-02-17", "2026-02-18", "2026-02-19",
        "2026-02-20", "2026-02-23",                                      // 春节
        "2026-04-06",                                                    // 清明
        "2026-05-01", "2026-05-04", "2026-05-05",                        // 劳动节
        "2026-06-19",                                                    // 端午
        "2026-09-25",                                                    // 中秋
        "2026-10-01", "2026-10-02", "2026-10-05", "2026-10-06", "2026-10-07", // 国庆
    ];

    /// <summary>
    /// 节假日表覆盖的年份。未覆盖的年份只能按"工作日 + 窗口"估算（会把节假日误当工作日），
    /// 界面必须如实标注 <see cref="CoverageNote"/>，不能假装精确。
    /// 2027 年的放假安排通常在前一年 11 月公布，届时新增一份 <c>Holidays2027</c> 数组并在此登记年份。
    /// </summary>
    public static readonly int[] CoveredYears = [2026];

    private static readonly HashSet<DateOnly> Holidays = BuildHolidays();

    private static HashSet<DateOnly> BuildHolidays()
    {
        var set = new HashSet<DateOnly>();
        foreach (var text in Holidays2026)
        {
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", out var day)) set.Add(day);
        }
        return set;
    }

    public static bool IsWeekend(DateOnly day) =>
        day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static bool IsHoliday(DateOnly day) => Holidays.Contains(day);

    /// <summary>该日期所在年份的节假日表是否已收录。</summary>
    public static bool IsCovered(DateOnly day) => CoveredYears.Contains(day.Year);

    /// <summary>全天都是谷价的日期：周末或法定节假日。</summary>
    public static bool IsOffPeakDay(DateOnly day) => IsWeekend(day) || IsHoliday(day);

    /// <summary>该时间点是否按高峰价计费（精确到分钟，12:00 与 18:00 整点已属谷价）。</summary>
    public static bool IsPeak(DateTime local)
    {
        var day = DateOnly.FromDateTime(local);
        if (IsOffPeakDay(day)) return false;
        int minute = local.Hour * 60 + local.Minute;
        foreach (var w in Windows)
        {
            if (minute >= w.Start && minute < w.End) return true;
        }
        return false;
    }

    /// <summary>
    /// 整点小时是否落在**工作日**的峰窗口内。给 24 小时图的底纹用——
    /// 底纹表示"工作日的计价峰窗口"，不代表该小时一定是峰价（周末同一小时是谷价）。
    /// </summary>
    public static bool IsPeakHour(int hour) =>
        Windows.Any(w => hour * 60 >= w.Start && hour * 60 < w.End);

    /// <summary>未收录年份的提示文案；已收录返回 null。</summary>
    public static string? CoverageNote(DateOnly day) =>
        IsCovered(day) ? null : $"{day.Year} 年节假日表未收录，该年按工作日估算";
}

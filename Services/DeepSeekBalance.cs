using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// DeepSeek 余额的"观测峰值",每个币种一份。
///
/// 这是 sinceTopUp 模式的分母。逻辑只有一句:余额上涨只可能是充值,所以峰值一旦
/// 被超过就重置回当前余额,环重新从满开始。分母是本程序**看着发生**的数字,不是
/// 猜的定价、不是套餐表、也不是用户填的值(那是 budget 模式的事)。
///
/// 代价是首次运行:从没观察过这个账户时没有峰值,第一次读数本身就成为峰值,
/// 环显示 0% 直到真的花了钱——这是关于"本程序看到了什么"的真实陈述。
/// </summary>
public sealed class DeepSeekBaseline
{
    public sealed class Mark
    {
        /// <summary>自上次上涨以来见过的最高余额。</summary>
        public double Peak { get; set; }

        /// <summary>看到该峰值的时刻(即充值发生的时刻,不是最后一次读取的时刻)。</summary>
        public DateTimeOffset SetAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _file;
    private readonly object _gate = new();

    public DeepSeekBaseline(AppDataPaths paths) => _file = paths.DeepSeekBaselineFile;

    /// <summary>按币种代码存一份。</summary>
    public Dictionary<string, Mark> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_file)) return new Dictionary<string, Mark>();
                return JsonSerializer.Deserialize<Dictionary<string, Mark>>(
                    File.ReadAllText(_file), JsonOptions) ?? new Dictionary<string, Mark>();
            }
            catch (Exception ex)
            {
                Diagnostics.Note("读取 DeepSeek 基准失败", ex);
                return new Dictionary<string, Mark>();
            }
        }
    }

    public void Save(Dictionary<string, Mark> marks)
    {
        lock (_gate)
        {
            try
            {
                AtomicFile.WriteText(_file, JsonSerializer.Serialize(marks, JsonOptions));
            }
            catch (Exception ex)
            {
                Diagnostics.Note("保存 DeepSeek 基准失败", ex);
            }
        }
    }

    /// <summary>
    /// 看到 `balance` 之后标记该变成什么。纯函数,不碰磁盘。
    /// 首次看到、或余额高于既有峰值(充值了)——两种都重置;其余保持不动。
    /// </summary>
    public static Mark Advanced(Mark? mark, double balance, DateTimeOffset now) =>
        mark is null || balance > mark.Peak
            ? new Mark { Peak = Math.Max(balance, 0), SetAt = now }
            : mark;

    /// <summary>
    /// 相对峰值已经用掉的比例。峰值为 0 表示这个账户从来没有过额度,
    /// 与"花光了"不是一回事,所以返回 null(没有分母)。
    /// </summary>
    public static double? UsedFraction(double balance, double peak) =>
        peak > 0 ? Math.Clamp((peak - balance) / peak, 0d, 1d) : null;
}

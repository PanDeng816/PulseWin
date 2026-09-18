using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 值得打扰用户的两件事:**额度到了你选的那一档**、**额度重置了**。
///
/// 两条规矩:
/// ① 档位是固定几档(75/80/90/95),**默认关**——一个装上就开始自己弹通知的程序,
///    在你装它的那天就改变了你同意的东西(上游的原话);
/// ② 同一个池、同一个档位只提一次,掉回阈值以下(用完一轮、重置了)才会再提,
///    否则每次同步都弹一遍,通知会变成噪音被忽略掉。
/// </summary>
public sealed class Notifier
{
    private readonly AppDataPaths _paths;
    private readonly Action<string, string> _show;
    private readonly object _gate = new();
    private AlertState _state;
    private bool _primed;

    public Notifier(AppDataPaths paths, Action<string, string> show)
    {
        _paths = paths;
        _show = show;
        _state = Load();
    }

    private sealed class AlertState
    {
        /// <summary>每个池已经提过的档位(键 = 来源/池类型)。</summary>
        public Dictionary<string, int> NotifiedAt { get; set; } = new();
        /// <summary>每个池上一次看到的重置时刻(判断"这一轮是不是换了")。</summary>
        public Dictionary<string, string> LastResetAt { get; set; } = new();
    }

    private AlertState Load()
    {
        try
        {
            if (File.Exists(_paths.AlertStateFile))
            {
                var loaded = JsonSerializer.Deserialize<AlertState>(File.ReadAllText(_paths.AlertStateFile));
                if (loaded is not null)
                {
                    loaded.NotifiedAt ??= new();
                    loaded.LastResetAt ??= new();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取通知状态失败", ex);
        }
        return new AlertState();
    }

    private void Save()
    {
        try
        {
            AtomicFile.WriteText(_paths.AlertStateFile,
                JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("保存通知状态失败", ex);
        }
    }

    /// <summary>每次拿到新快照后调用。</summary>
    public void Inspect(IReadOnlyList<SubData> subs)
    {
        var settings = AppSettings.Current;
        lock (_gate)
        {
            bool stateChanged = false;

            foreach (var sub in subs)
            {
                foreach (var pool in sub.Pools)
                {
                    if (!pool.IsAvailable) continue;
                    string key = $"{sub.Key}/{pool.PoolKind}";

                    // —— 额度重置 ——
                    if (pool.ResetAt is { } reset)
                    {
                        string text = reset.ToString("O");
                        bool isReset = _state.LastResetAt.TryGetValue(key, out var previous)
                            && DateTimeOffset.TryParse(previous, out var before)
                            // 只认"往后跳"的重置;抖动几秒不算(快照重读会让时间戳微动)
                            && reset > before.AddSeconds(30);
                        // 第一次看到某个池不算重置:那是刚认识它,不是它重置了
                        _state.LastResetAt[key] = text;
                        stateChanged = true;
                        if (isReset)
                        {
                            _state.NotifiedAt.Remove(key);
                            if (settings.NotifyOnReset && _primed)
                            {
                                _show($"{sub.Name} 额度已重置",
                                    $"{pool.Label}开始新一轮" + (pool.Cap is { } cap ? $"(上限 {Money.Short(cap, pool.Unit)})" : ""));
                            }
                        }
                    }

                    // —— 到档位 ——
                    if (settings.AlertPercent > 0 && pool.HasPercent)
                    {
                        double percent = pool.Fraction * 100;
                        int already = _state.NotifiedAt.TryGetValue(key, out var level) ? level : 0;
                        if (percent >= settings.AlertPercent && already < settings.AlertPercent)
                        {
                            _state.NotifiedAt[key] = settings.AlertPercent;
                            stateChanged = true;
                            if (_primed)
                            {
                                _show($"{sub.Name} {pool.Label}已用到 {percent:0}%",
                                    $"超过你设的 {settings.AlertPercent}% 提醒线"
                                    + (pool.ResetAt is { } next ? $";{next.LocalDateTime:MM-dd HH:mm} 重置" : ""));
                            }
                        }
                        else if (percent < settings.AlertPercent - 5 && already > 0)
                        {
                            // 掉回阈值以下(重置了/换了一轮):下一轮才会再提
                            _state.NotifiedAt.Remove(key);
                            stateChanged = true;
                        }
                    }
                }
            }

            // 第一次扫描只记录状态、不发通知:否则刚装上就会为"已经用掉 80%"弹窗,
            // 而那是用户早就知道的事。
            if (!_primed)
            {
                _primed = true;
                stateChanged = true;
            }
            if (stateChanged) Save();
        }
    }
}

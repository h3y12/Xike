using System;
using System.Globalization;
using System.Windows.Media;

namespace TimeGuard;

public enum CountdownMode
{
    /// <summary>到某个钟点（原 timecountdown.c 的语义：填 时 分 秒，到点为止）。</summary>
    ClockTime,
    /// <summary>从此刻起计 N 分钟。</summary>
    Duration
}

public enum CountdownPhase
{
    /// <summary>还没设目标，输入框待命。</summary>
    Idle,
    Running,
    Paused,
    /// <summary>已到点（原程序打印的「时间到！」）。</summary>
    Finished
}

/// <summary>一次倒计时的设定，由输入文本解析得到。</summary>
public sealed class CountdownPlan
{
    public CountdownMode Mode { get; init; }

    /// <summary>ClockTime 模式：目标时刻（当日内的 hh:mm:ss）。</summary>
    public TimeSpan ClockTarget { get; init; }

    /// <summary>Duration 模式：时长。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>界面上那行小字，说明这次在数什么。</summary>
    public string Label { get; init; } = string.Empty;
}

public readonly struct CountdownState
{
    public readonly CountdownPhase Phase;
    public readonly TimeSpan Remaining;

    public bool HasActiveTime => (Phase is CountdownPhase.Running or CountdownPhase.Paused)
        && Remaining > TimeSpan.Zero;

    public CountdownState(CountdownPhase phase, TimeSpan remaining)
    {
        Phase = phase;
        Remaining = remaining;
    }
}

/// <summary>
/// 倒计时（纯逻辑，不依赖界面，可由 --selftest 独立核对）。
///
/// 替代原 timecountdown.c 里「敲 0 结束 / 敲 1 重来」的那套交互：
/// 结束 = 重置、重来 = 再来一次，都改由按钮驱动。原程序的「目标时刻」语义保留，
/// 另加「N 分钟后」模式。
/// </summary>
public sealed class Countdown
{
    public const int MaxDurationMinutes = 24 * 60;

    private CountdownPlan? _plan;
    private DateTime _deadline;
    private TimeSpan _pausedRemaining;

    public CountdownPhase Phase { get; private set; } = CountdownPhase.Idle;

    public CountdownPlan? Plan => _plan;

    /// <summary>
    /// 这次倒计时能不能暂停：只有「N 分钟后」的时长计时可以。
    /// 「到某个钟点」的计时钉死在钟点上，暂停会让它偏离目标，因此不允许。
    /// 界面据此决定要不要显示暂停按钮，托盘菜单与热键也照此判断。
    /// </summary>
    public bool PauseAllowed => _plan?.Mode == CountdownMode.Duration;

    /// <summary>输入框里的原文，落盘后下次启动还在。</summary>
    public string InputText { get; set; } = string.Empty;

    // ---- 输入解析 ----

    /// <summary>
    /// 认这些写法：
    ///  · 23:00 / 23:00:30 / 23   → 到那个钟点（已过则顺延到明天）
    ///  · 45m / 45分钟 / 2h       → 时长模式
    ///  · 纯数字 &gt; 23           → 分钟数（45 就是 45 分钟）
    /// </summary>
    public static bool TryParse(string? text, DateTime now, out CountdownPlan plan, out string error)
    {
        plan = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "先填一个时间：23:00 表示到点，45m 表示 45 分钟后。";
            return false;
        }

        string raw = text.Trim();
        string s = raw.Replace('：', ':')
                     .Replace('．', ':')
                     .Replace('。', ':')
                     .Replace("分钟", "m")
                     .Replace("分", "m")
                     .Replace("小时", "h")
                     .Replace("钟头", "h")
                     .Replace(" ", string.Empty);

        // ---- 时刻模式：带冒号，或 0–23 的纯数字（与原程序「只填小时也认」一致）----
        if (s.Contains(':'))
        {
            if (!SleepRules.TryParseHm(s, out TimeSpan target))
            {
                error = "「" + raw + "」读不出来，按 23:00 或 23:00:30 这样填。";
                return false;
            }

            plan = new CountdownPlan
            {
                Mode = CountdownMode.ClockTime,
                ClockTarget = target,
                Duration = DeadlineFor(target, now) - now,
                Label = "到 " + HmOrHms(target) + " 为止"
            };
            return true;
        }

        string digits = s;
        char unit = '\0';
        if (s.Length > 0 && !char.IsAsciiDigit(s[^1]))
        {
            unit = char.ToLowerInvariant(s[^1]);
            digits = s[..^1];
        }

        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            error = "「" + raw + "」读不出来，按 23:00 或 45m 这样填。";
            return false;
        }

        if (unit == '\0')
        {
            // 纯数字：0–23 当整点，>23 当分钟数。
            //
            // ⚠️ 下界必须写出来。写成 `n <= 23` 的话，负数会掉进 `new TimeSpan(n, 0, 0)`：
            // int.TryParse 认得 "-1"、"-2147483648"，而 TimeSpan 的小时参数只允许 ±23，
            // 于是用户在倒计时框里敲一个 "-1" 就能让程序当场抛 ArgumentOutOfRangeException。
            if (n < 0)
            {
                error = "「" + raw + "」不能是负数，按 23:00 或 45m 这样填。";
                return false;
            }

            if (n <= 23)
            {
                var t = new TimeSpan(n, 0, 0);
                plan = new CountdownPlan
                {
                    Mode = CountdownMode.ClockTime,
                    ClockTarget = t,
                    Duration = DeadlineFor(t, now) - now,
                    Label = "到 " + HmOrHms(t) + " 为止"
                };
                return true;
            }
            return BuildDuration(n, raw, now, out plan, out error);
        }

        if (unit == 'h')
        {
            if (n < 1 || n > 24) { error = "「" + raw + "」的小时数要在 1–24 之间。"; return false; }
            return BuildDuration(n * 60, raw, now, out plan, out error);
        }

        if (unit == 'm') return BuildDuration(n, raw, now, out plan, out error);

        error = "「" + raw + "」的单位只认 m（分钟）和 h（小时）。";
        return false;
    }

    private static bool BuildDuration(int minutes, string raw, DateTime now, out CountdownPlan plan, out string error)
    {
        plan = null!;
        error = string.Empty;

        if (minutes < 1 || minutes > MaxDurationMinutes)
        {
            error = "「" + raw + "」换算成 " + minutes + " 分钟，超出 1 分钟–24 小时的可用范围。";
            return false;
        }

        var d = TimeSpan.FromMinutes(minutes);
        plan = new CountdownPlan
        {
            Mode = CountdownMode.Duration,
            Duration = d,
            Label = "到 " + (now + d).ToString("HH:mm", CultureInfo.InvariantCulture) + " 结束"
        };
        return true;
    }

    /// <summary>目标时刻落在今天还是明天（已过就顺延）。</summary>
    private static DateTime DeadlineFor(TimeSpan clockTarget, DateTime now)
    {
        DateTime today = now.Date + clockTarget;
        return today <= now ? today.AddDays(1) : today;
    }

    // ---- 状态机 ----

    /// <summary>开始一次倒计时。ClockTime 模式按 <paramref name="now"/> 现算截止点，避免解析与启动之间差几秒。</summary>
    public void Start(CountdownPlan plan, DateTime now)
    {
        _plan = plan;
        _deadline = plan.Mode == CountdownMode.ClockTime
            ? DeadlineFor(plan.ClockTarget, now)
            : now + plan.Duration;

        // 时长模式的说明文字里带结束钟点，这里以真实截止点为准再写一次
        if (plan.Mode == CountdownMode.Duration)
        {
            _plan = new CountdownPlan
            {
                Mode = CountdownMode.Duration,
                Duration = plan.Duration,
                Label = "到 " + _deadline.ToString("HH:mm", CultureInfo.InvariantCulture) + " 结束"
            };
        }

        _pausedRemaining = TimeSpan.Zero;
        Phase = CountdownPhase.Running;
    }

    /// <summary>到点后点「再来」：时刻模式顺延到下一个同一时刻，时长模式重新计满一轮。</summary>
    public void Restart(DateTime now)
    {
        if (_plan is null) return;
        Start(_plan, now);
    }

    /// <summary>
    /// 暂停。<b>时刻模式直接拒绝</b>（返回 false，状态一点不动）：那里的截止点是那个钟点本身，
    /// 冻结剩余量再继续只会把截止点往后推，暂停 10 秒就等于把 23:30 变成了 23:30:10。
    /// 要停这种倒计时就「重置」。只有「N 分钟后」的时长计时才允许暂停。
    /// </summary>
    public bool Pause(DateTime now)
    {
        if (Phase != CountdownPhase.Running) return false;
        if (!PauseAllowed) return false;

        TimeSpan left = _deadline - now;
        _pausedRemaining = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        Phase = _pausedRemaining == TimeSpan.Zero ? CountdownPhase.Finished : CountdownPhase.Paused;
        return true;
    }

    public void Resume(DateTime now)
    {
        if (Phase != CountdownPhase.Paused) return;

        if (_pausedRemaining <= TimeSpan.Zero) { Phase = CountdownPhase.Finished; return; }
        _deadline = now + _pausedRemaining;

        // 说明文字里的结束钟点是 Start 里按**当时**的截止点写死的。暂停会把截止点
        // 整体往后推（暂停多久推多久），继续以后必须重写一次，否则小字还写着
        // 「到 20:45 结束」，而真实截止点已经是 20:55。
        if (_plan is not null && _plan.Mode == CountdownMode.Duration)
        {
            _plan = new CountdownPlan
            {
                Mode = CountdownMode.Duration,
                Duration = _plan.Duration,
                Label = "到 " + _deadline.ToString("HH:mm", CultureInfo.InvariantCulture) + " 结束"
            };
        }

        Phase = CountdownPhase.Running;
    }

    /// <summary>「重置」：回到输入态。相当于原程序里敲 0 结束。</summary>
    public void Reset()
    {
        Phase = CountdownPhase.Idle;
        _plan = null;
        _pausedRemaining = TimeSpan.Zero;
    }

    /// <summary>推进到 <paramref name="now"/>。到点会自动落到 Finished，只在这里改状态，调用方不用自己判。</summary>
    public CountdownState Tick(DateTime now)
    {
        switch (Phase)
        {
            case CountdownPhase.Idle:
                return new CountdownState(CountdownPhase.Idle, TimeSpan.Zero);

            case CountdownPhase.Paused:
                return new CountdownState(CountdownPhase.Paused, _pausedRemaining);

            case CountdownPhase.Finished:
                return new CountdownState(CountdownPhase.Finished, TimeSpan.Zero);

            default:
                TimeSpan left = _deadline - now;
                if (left <= TimeSpan.Zero)
                {
                    Phase = CountdownPhase.Finished;
                    _pausedRemaining = TimeSpan.Zero;
                    return new CountdownState(CountdownPhase.Finished, TimeSpan.Zero);
                }
                return new CountdownState(CountdownPhase.Running, left);
        }
    }

    /// <summary>只读地看一眼某个时刻的剩余（不改状态），供自检断言用。</summary>
    public CountdownState Peek(DateTime now)
    {
        if (Phase == CountdownPhase.Running)
        {
            TimeSpan left = _deadline - now;
            return left <= TimeSpan.Zero
                ? new CountdownState(CountdownPhase.Finished, TimeSpan.Zero)
                : new CountdownState(CountdownPhase.Running, left);
        }
        return new CountdownState(Phase, Phase == CountdownPhase.Paused ? _pausedRemaining : TimeSpan.Zero);
    }

    /// <summary>大字下面的小字。</summary>
    public string SubLabel(CountdownPhase phase)
    {
        string basis = _plan?.Label ?? string.Empty;

        return phase switch
        {
            CountdownPhase.Idle => "23:00 到点 / 45m 计时",
            CountdownPhase.Paused => "已暂停 · " + basis,
            CountdownPhase.Finished => basis + " · 已到点",
            // 时刻模式在跑的时候说清「不能暂停」，免得用户找不到暂停按钮还以为是漏了
            CountdownPhase.Running when !PauseAllowed => basis + " · 不可暂停",
            _ => basis
        };
    }

    /// <summary>大字本身。</summary>
    public static string BigText(CountdownPhase phase, TimeSpan remaining) => phase switch
    {
        CountdownPhase.Idle => "--:--:--",
        CountdownPhase.Finished => "时间到！",
        _ => Hms(remaining)
    };

    /// <summary>颜色随剩余时间收紧：>5 分钟白、≤5 分钟琥珀、≤1 分钟红；未开始为暗白。</summary>
    public static Color ColorFor(CountdownPhase phase, TimeSpan remaining)
    {
        if (phase == CountdownPhase.Finished) return Color.FromRgb(0xFF, 0x3B, 0x30);
        if (phase == CountdownPhase.Idle) return Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);
        if (remaining <= TimeSpan.FromMinutes(1)) return Color.FromRgb(0xFF, 0x3B, 0x30);
        if (remaining <= TimeSpan.FromMinutes(5)) return Color.FromRgb(0xEF, 0x9F, 0x27);
        return Color.FromRgb(0xF2, 0xFF, 0xFF);
    }

    /// <summary>
    /// HH:mm:ss，小时两位（跨零点也不会显示成负）。
    /// 剩余时间向上取整到秒：剩 44:59.997 时显示 00:45:00，跨过整秒才跳，
    /// 否则「点了开始」到「第一次刷新」之间那几毫秒会让它一上来就少一秒。
    /// </summary>
    public static string Hms(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        ts = TimeSpan.FromSeconds(Math.Ceiling(ts.TotalSeconds));

        int h = (int)ts.TotalHours;
        return h.ToString("00", CultureInfo.InvariantCulture) + ":"
            + ts.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
            + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    // ---- 落盘 / 还原（关掉程序再打开，倒计时还在走）----

    /// <summary>存成扁平字段，直接塞进 config.json。</summary>
    public (string Mode, long ClockTicks, long DurationTicks, string Deadline, string Phase, long PausedTicks) Snapshot()
    {
        if (_plan is null) return (string.Empty, 0, 0, string.Empty, CountdownPhase.Idle.ToString(), 0);

        return (
            _plan.Mode.ToString(),
            _plan.ClockTarget.Ticks,
            _plan.Duration.Ticks,
            _deadline.ToString("o", CultureInfo.InvariantCulture),
            Phase.ToString(),
            _pausedRemaining.Ticks);
    }

    /// <summary>
    /// 从配置还原。Running 存的是绝对截止时刻，所以程序没开的这段时间也照常算进去
    /// （设了 45 分钟，关掉程序 50 分钟后再打开，看到的是「时间到！」而不是重新数 45 分钟）。
    /// </summary>
    public void Restore(string mode, long clockTicks, long durationTicks, string deadline,
        string phase, long pausedTicks, DateTime now)
    {
        Reset();

        if (!Enum.TryParse(mode, out CountdownMode m)) return;
        if (!Enum.TryParse(phase, out CountdownPhase p) || p == CountdownPhase.Idle) return;

        var clock = TimeSpan.FromTicks(Math.Max(0, clockTicks));
        var span = TimeSpan.FromTicks(Math.Max(0, durationTicks));

        bool hasDeadline = DateTime.TryParse(deadline, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime d);
        _deadline = hasDeadline ? d : now;

        _plan = new CountdownPlan
        {
            Mode = m,
            ClockTarget = m == CountdownMode.ClockTime ? clock : TimeSpan.Zero,
            Duration = m == CountdownMode.Duration ? span : TimeSpan.Zero,
            Label = m == CountdownMode.ClockTime
                ? "到 " + HmOrHms(clock) + " 为止"
                : "到 " + _deadline.ToString("HH:mm", CultureInfo.InvariantCulture) + " 结束"
        };

        // ---- 时刻模式：目标就是那个钟点，落盘的截止点要按钟点校准 ----
        if (m == CountdownMode.ClockTime)
        {
            // 已经到过点了就还是到点。不加这一步的话，第二天早上打开程序会把
            // 昨晚早已结束的计时「顺延到今晚 23:30」，看着像自己重开了一轮。
            if (p == CountdownPhase.Finished)
            {
                Phase = CountdownPhase.Finished;
                _pausedRemaining = TimeSpan.Zero;
                return;
            }

            // 旧版本的暂停会把截止点整体往后推（时刻模式已不再允许暂停，但历史配置里留着）。
            // 取「≤ 该截止点的最后一个钟点」，就是暂停之前的原始目标，
            // 等于顺手把被暂停弄歪的那批倒计时治回原样。
            _deadline = hasDeadline ? DeadlineFor(clock, _deadline.AddDays(-1)) : DeadlineFor(clock, now);
            _pausedRemaining = TimeSpan.Zero;
            Phase = _deadline <= now ? CountdownPhase.Finished : CountdownPhase.Running;
            return;
        }

        if (p == CountdownPhase.Paused)
        {
            _pausedRemaining = TimeSpan.FromTicks(Math.Max(0, pausedTicks));
            Phase = _pausedRemaining > TimeSpan.Zero ? CountdownPhase.Paused : CountdownPhase.Finished;
            if (Phase == CountdownPhase.Paused && _pausedRemaining > TimeSpan.Zero)
                _deadline = now + _pausedRemaining;
            return;
        }

        if (p == CountdownPhase.Finished || _deadline <= now)
        {
            Phase = CountdownPhase.Finished;
            _pausedRemaining = TimeSpan.Zero;
            return;
        }

        Phase = CountdownPhase.Running;
    }

    private static string HmOrHms(TimeSpan ts)
        => ts.Seconds == 0 && ts.Milliseconds == 0
            ? SleepRules.FormatHm(ts)
            : SleepRules.FormatHm(ts) + ":" + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
}

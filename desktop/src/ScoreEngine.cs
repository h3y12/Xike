using System;

namespace TimeGuard;

public enum NightPhase
{
    /// <summary>隐藏时段（默认 06:30 ~ 20:30），窗口不显示。</summary>
    Daytime,
    /// <summary>出现后到扣分起点之间，满分待命。</summary>
    Countdown,
    /// <summary>扣分中。</summary>
    Deducting,
    /// <summary>已扣光，锁定 0 分。</summary>
    Exhausted
}

public readonly struct ScoreState
{
    public readonly int Score;
    public readonly NightPhase Phase;
    public readonly TimeSpan UntilDeadline;

    public ScoreState(int score, NightPhase phase, TimeSpan untilDeadline)
    {
        Score = score;
        Phase = phase;
        UntilDeadline = untilDeadline;
    }
}

/// <summary>
/// 健康分规则（纯函数，不依赖界面，可用 --selftest 独立核对）。
/// 默认口径：20:30 出现 100 分；23:30 前始终 100 分；
/// 23:30 起连续扣分，每 3 分钟 1 分（= 半小时 10 分，00:00 恰好 90 分）；
/// 04:30 归零后锁 0，不出现负数；06:30 窗口隐藏，直到次日 20:30。
/// 以上四个时刻与扣分速率均可在「设置」中调整，通过 <see cref="SleepRules"/> 传入。
/// </summary>
public static class ScoreEngine
{
    public const int MaxScore = 100;

    public static ScoreState Evaluate(DateTime now, SleepRules rules)
    {
        (DateTime show, DateTime deduct, DateTime hide) = rules.Resolve(now);

        if (now < show || now >= hide)
            return new ScoreState(rules.MaxScore, NightPhase.Daytime, TimeSpan.Zero);

        if (now < deduct)
            return new ScoreState(rules.MaxScore, NightPhase.Countdown, deduct - now);

        // 整数运算，避免浮点误差把整点算少一分
        long elapsedSeconds = (now - deduct).Ticks / TimeSpan.TicksPerSecond;
        long stepSeconds = rules.MinutesPerPoint * 60L;
        long dropped = elapsedSeconds / stepSeconds * rules.PointsPerStep;
        long raw = rules.MaxScore - dropped;

        int score = raw <= 0 ? 0 : (int)raw;
        NightPhase phase = score == 0 ? NightPhase.Exhausted : NightPhase.Deducting;
        return new ScoreState(score, phase, TimeSpan.Zero);
    }

    /// <summary>格式化成 h:mm:ss（如 2:14:30）。</summary>
    public static string FormatHms(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
    }
}

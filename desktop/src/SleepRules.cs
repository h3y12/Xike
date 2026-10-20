using System;
using System.Globalization;

namespace TimeGuard;

/// <summary>
/// 一夜的规则参数。全部可在「设置」窗口里调整，落到 config.json。
/// 时间语义：ShowAt（出现/开始记分）→ DeductAt（开始扣分）→ HideAt（隐藏），
/// 跨零点按「一夜」的顺序解释，不按当天先后。
/// </summary>
public sealed class SleepRules
{
    /// <summary>窗口出现、开始记分。</summary>
    public TimeSpan ShowAt { get; init; } = new(20, 30, 0);

    /// <summary>开始扣分。</summary>
    public TimeSpan DeductAt { get; init; } = new(23, 30, 0);

    /// <summary>窗口隐藏。</summary>
    public TimeSpan HideAt { get; init; } = new(6, 30, 0);

    /// <summary>每多少分钟扣一次。</summary>
    public int MinutesPerPoint { get; init; } = 3;

    /// <summary>每次扣多少分。</summary>
    public int PointsPerStep { get; init; } = 1;

    /// <summary>起始分，也是上限。</summary>
    public int MaxScore { get; init; } = 100;

    // ---- 状态文案（显示在分数下方那一行，可在设置窗口改）----
    // 留空是合法的：表示"这个状态下不显示任何文字"，不是错误。

    /// <summary>已出现、还没开始扣分时的文案。</summary>
    public string StatusBefore { get; init; } = "夜深了，注意休息~";

    /// <summary>高分段的文案（≥ <see cref="ScoreVisual.HighLine"/>）。</summary>
    public string StatusHigh { get; init; } = "快去睡吧。";

    /// <summary>中分段的文案（<see cref="ScoreVisual.MidLine"/> – <see cref="ScoreVisual.HighLine"/>-1）。</summary>
    public string StatusMid { get; init; } = "头发要紧，睡觉！";

    /// <summary>低分段的文案（&lt; <see cref="ScoreVisual.MidLine"/>）。</summary>
    public string StatusLow { get; init; } = "不睡等死吧你！！！";

    public static SleepRules Default { get; } = new();

    /// <summary>扣光所需时长（从 DeductAt 起算）。</summary>
    public TimeSpan SpanToZero =>
        TimeSpan.FromMinutes((long)Math.Ceiling(MaxScore / (double)PointsPerStep) * MinutesPerPoint);

    /// <summary>把规则里的三个时刻落到以 <paramref name="anyTimeInNight"/> 所在这一夜为基准的真实时间点上。</summary>
    public (DateTime Show, DateTime Deduct, DateTime Hide) Resolve(DateTime anyTimeInNight)
    {
        DateTime show = anyTimeInNight.Date + ShowAt;
        if (anyTimeInNight < show) show = show.AddDays(-1);

        DateTime day = show.Date;
        DateTime deduct = (DeductAt >= ShowAt ? day : day.AddDays(1)) + DeductAt;
        DateTime hide = (HideAt > ShowAt ? day : day.AddDays(1)) + HideAt;
        return (show, deduct, hide);
    }

    /// <summary>整套参数是否自洽（顺序正确、数值在合理区间）。</summary>
    public bool IsValid(out string error)
    {
        if (HideAt == ShowAt) { error = "「窗口隐藏」不能和「窗口出现」是同一个时刻，否则窗口会一直显示。"; return false; }
        if (MinutesPerPoint < 1 || MinutesPerPoint > 720) { error = "「每几分钟扣」要在 1–720 之间。"; return false; }
        if (PointsPerStep < 1 || PointsPerStep > MaxScore) { error = $"「每次扣几分」要在 1–{MaxScore} 之间。"; return false; }

        (DateTime show, DateTime deduct, DateTime hide) = Resolve(new DateTime(2026, 1, 1) + ShowAt);
        if (deduct < show || deduct >= hide)
        {
            error = $"按当前设置，「开始扣分 {FormatHm(DeductAt)}」落在窗口显示时段（{FormatHm(ShowAt)}–{FormatHm(HideAt)}）之外，永远不会扣分。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>这段时长跨零点也不会算错（用于把扣光时刻显示成 HH:mm）。</summary>
    public string Describe(DateTime nightAnchorDate)
    {
        (DateTime show, DateTime deduct, DateTime hide) = Resolve(nightAnchorDate + ShowAt);
        DateTime zero = deduct + SpanToZero;

        string zeroText = zero >= hide
            ? "在窗口隐藏前扣不到 0"
            : $"约 {zero:HH:mm} 扣光";

        return $"{show:HH:mm} 出现 100 分 → {deduct:HH:mm} 起每 {MinutesPerPoint} 分钟扣 {PointsPerStep} 分（{zeroText}）→ {hide:HH:mm} 隐藏";
    }

    // ---- 时间文本 ----

    public static string FormatHm(TimeSpan ts) => $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";

    public static bool TryParseHm(string? text, out TimeSpan ts)
    {
        ts = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim().Replace('：', ':').Replace('．', '.').Replace('.', ':');
        string[] parts = s.Split(':');
        if (parts.Length is < 1 or > 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return false;
        int m = 0, sec = 0;
        if (parts.Length >= 2 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
        if (parts.Length >= 3 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out sec)) return false;

        if (h is < 0 or > 23 || m is < 0 or > 59 || sec is < 0 or > 59) return false;

        ts = new TimeSpan(h, m, sec);
        return true;
    }

    public static TimeSpan ParseHm(string? text, TimeSpan fallback)
        => TryParseHm(text, out TimeSpan ts) ? ts : fallback;
}

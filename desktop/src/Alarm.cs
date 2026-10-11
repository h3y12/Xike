using System;
using System.Collections.Generic;
using System.Globalization;

namespace TimeGuard;

/// <summary>闹钟的重复方式。</summary>
public enum AlarmRepeat
{
    /// <summary>只响一次，响过就删。</summary>
    Once,

    /// <summary>每天同一时刻。</summary>
    Daily,

    /// <summary>每周指定几天。</summary>
    Weekly
}

/// <summary>
/// 一个闹钟。纯数据 + 纯函数，不碰界面也不碰磁盘，便于 --selftest 独立核对。
///
/// 时间语义：<see cref="TimeOfDay"/> 是「钟点」（0:00–23:59），与日期无关。
/// 它属于**哪一天**由 <see cref="DayCutoverHour"/>（凌晨四点）决定：
/// 04:00 之前的钟点算作「前一天的晚上」，04:00 及之后算「当天」。
/// 这条规则只影响**排序与显示分组**，不改变它实际响起的那一刻 —— 见 <see cref="SortKey"/>。
/// </summary>
public sealed class Alarm
{
    /// <summary>
    /// 一天的起点设在凌晨四点，而不是零点。
    /// 理由：设成 0 点的话，「今晚 01:30 睡」和「明晚 23:00 睡」会被排到列表首尾两端，
    /// 而它们在用户心里是前后脚的两件事。凌晨四点切，01:30 就乖乖排在 23:00 后面。
    /// </summary>
    public const int DayCutoverHour = 4;

    /// <summary>稳定标识，用于「手动关掉整串连锁闹钟」时认出彼此。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>用户给闹钟起的名字，如「起床」「吃药」。空串表示不起名。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>钟点 0:00–23:59。秒对闹钟没有意义，一律截到分。</summary>
    public TimeSpan TimeOfDay { get; set; }

    /// <summary>这个闹钟当前是否生效。不生效的在界面上显示浅灰色。</summary>
    public bool Enabled { get; set; } = true;

    public AlarmRepeat Repeat { get; set; } = AlarmRepeat.Once;

    /// <summary>
    /// Weekly 时生效的星期几。空集合会让 Weekly 退化成一响都不响，
    /// 因此 <see cref="Normalize"/> 会把空集合修正为「每天」。
    /// </summary>
    public HashSet<DayOfWeek> Days { get; set; } = new();

    /// <summary>
    /// 一次性闹钟：响过之后自动删除（用户选的就是「响过就删」）。
    /// 每日/每周的闹钟无视这个标志 —— 它们本来就该反复响。
    /// </summary>
    public bool DeleteAfterRing { get; set; } = true;

    /// <summary>
    /// 属于哪一串连锁起床闹钟。空串 = 独立闹钟。
    /// 「手动关闭时一起关闭」靠这个分组：手动关掉当前这个，同组的剩余闹钟一并关掉。
    /// </summary>
    public string ChainId { get; set; } = string.Empty;

    /// <summary>在连锁串里的序号（从 1 开始）。仅用于显示「第 2/5 个」，不参与判定。</summary>
    public int ChainIndex { get; set; }

    /// <summary>
    /// 排序键：把钟点折算成「从凌晨四点起的分钟数」。
    /// 04:00 → 0，05:00 → 60，23:00 → 1140，01:30(次日) → 1290。
    /// 于是 01:30 排在 23:00 后面，符合「同一个晚上」的直觉。
    /// </summary>
    public int SortKey
    {
        get
        {
            int minutes = (int)TimeOfDay.TotalMinutes;
            int shifted = minutes - DayCutoverHour * 60;
            if (shifted < 0) shifted += 24 * 60;
            return shifted;
        }
    }

    /// <summary>界面上显示的时间文本，如 "06:30"。</summary>
    public string TimeText => $"{(int)TimeOfDay.TotalHours:00}:{TimeOfDay.Minutes:00}";

    /// <summary>重复方式的短说明，如 "每天" / "周一、周三" / "仅一次"。</summary>
    public string RepeatText
    {
        get
        {
            switch (Repeat)
            {
                case AlarmRepeat.Once:
                    return "仅一次";
                case AlarmRepeat.Daily:
                    return "每天";
                default:
                    if (Days.Count == 0) return "每天";
                    if (Days.Count == 7) return "每天";
                    var names = new List<string>();
                    // 按周一到周日的自然顺序排，不用 HashSet 的遍历顺序（那是无序的）
                    foreach (DayOfWeek d in WeekOrder)
                        if (Days.Contains(d)) names.Add(DayName(d));
                    return string.Join("、", names);
            }
        }
    }

    /// <summary>周一…周日的固定顺序，用来把星期几排成用户习惯的次序。</summary>
    public static readonly DayOfWeek[] WeekOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    public static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    /// <summary>把钟点截到分钟（秒和毫秒对闹钟没意义），并做基本的合法性修正。</summary>
    public void Normalize()
    {
        if (TimeOfDay < TimeSpan.Zero) TimeOfDay = TimeSpan.Zero;
        if (TimeOfDay >= TimeSpan.FromDays(1)) TimeOfDay -= TimeSpan.FromDays(1);
        TimeOfDay = new TimeSpan((int)TimeOfDay.TotalHours, TimeOfDay.Minutes, 0);

        if (string.IsNullOrEmpty(Id)) Id = NewId();

        Days ??= new HashSet<DayOfWeek>();
        // Weekly 没勾任何一天会变成"永远不响"，用户多半是误操作 —— 按每天处理，
        // 至少听得见；真要静音应该直接关掉开关。
        if (Repeat == AlarmRepeat.Weekly && Days.Count == 0)
            foreach (DayOfWeek d in WeekOrder) Days.Add(d);
    }

    public Alarm Clone() => new()
    {
        Id = Id,
        Label = Label,
        TimeOfDay = TimeOfDay,
        Enabled = Enabled,
        Repeat = Repeat,
        Days = new HashSet<DayOfWeek>(Days),
        DeleteAfterRing = DeleteAfterRing,
        ChainId = ChainId,
        ChainIndex = ChainIndex
    };

    private static int _seq;

    /// <summary>生成一个进程内唯一的 Id。不追求跨机器唯一，够区分列表项就行。</summary>
    public static string NewId()
    {
        _seq++;
        return "a" + DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture)
                   + "-" + _seq.ToString(CultureInfo.InvariantCulture);
    }

    // ---- 判定：下一次什么时候响 ----

    /// <summary>
    /// 算出 <paramref name="after"/> 之后（不含 <paramref name="after"/> 本身被触发的那一刻）
    /// 这个闹钟下一次该响的真实时刻。关掉的闹钟返回 null（不参与提醒）。
    ///
    /// Once 的处理：只在「今天（按四点分界）」这一夜内有效，过了就没了 —— 见 <see cref="NextRingAfter"/>。
    /// </summary>
    public DateTime? NextRingAfter(DateTime after)
    {
        if (!Enabled) return null;

        // 从"今天"开始试到第 8 天，足够覆盖 Weekly 全部 7 种星期几
        for (int offset = 0; offset <= 7; offset++)
        {
            DateTime candidate = after.Date.AddDays(offset) + TimeOfDay;
            if (candidate <= after) continue;

            if (Repeat == AlarmRepeat.Once) return candidate;
            if (Repeat == AlarmRepeat.Daily) return candidate;
            if (Days.Contains(candidate.DayOfWeek)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 这个钟点落在「哪一夜」—— 用于把闹钟分组显示与排序。
    /// 返回的是该夜所属的日期（凌晨四点前算前一天）。
    /// </summary>
    public static DateTime NightOf(DateTime when)
        => when.Hour < DayCutoverHour ? when.Date.AddDays(-1) : when.Date;

    /// <summary>「从现在起还有多久响」的中文描述，如 "3 小时 20 分后" / "1 天后"。</summary>
    public static string DescribeCountdown(DateTime now, DateTime ringAt)
    {
        TimeSpan d = ringAt - now;
        if (d <= TimeSpan.Zero) return "已到点";
        if (d.TotalMinutes < 1) return "不到 1 分钟";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟后";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时 {d.Minutes} 分后";
        int days = (int)d.TotalDays;
        int hours = d.Hours;
        return hours == 0 ? $"{days} 天后" : $"{days} 天 {hours} 小时后";
    }

    /// <summary>校验一个小时/分钟输入是否可接受，给出中文理由。</summary>
    public static bool TryParseTimeText(string? text, out TimeSpan result, out string error)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "时间不能为空，按 06:30 这样填。";
            return false;
        }
        if (!SleepRules.TryParseHm(text, out TimeSpan ts))
        {
            error = "「" + text.Trim() + "」读不出来，按 06:30 这样填（24 小时制）。";
            return false;
        }
        result = new TimeSpan(ts.Hours, ts.Minutes, 0);
        error = string.Empty;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace TimeGuard;

/// <summary>连锁起床闹钟的生成参数。起始时间 + 间隔 + 次数 → 一串闹钟。</summary>
public sealed class AlarmChainSpec
{
    /// <summary>第一个闹钟的钟点。</summary>
    public TimeSpan StartAt { get; set; } = new(6, 30, 0);

    /// <summary>相邻两个闹钟相隔多少分钟。</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>一共几个。1 表示退化成普通闹钟（不连锁）。</summary>
    public int Count { get; set; } = 3;

    /// <summary>是否每天重复。false = 仅一次（响完整串就删）。</summary>
    public bool Daily { get; set; } = true;

    /// <summary>是否只在工作日（周一到周五）响。</summary>
    public bool WeekdaysOnly { get; set; }

    public const int MaxCount = 12;
    public const int MaxIntervalMinutes = 120;

    public bool IsValid(out string error)
    {
        if (Count < 1 || Count > MaxCount)
        {
            error = $"响铃次数要在 1–{MaxCount} 之间。";
            return false;
        }
        if (IntervalMinutes < 1 || IntervalMinutes > MaxIntervalMinutes)
        {
            error = $"间隔时间要在 1–{MaxIntervalMinutes} 分钟之间。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    /// <summary>最后一个闹钟的钟点（跨零点会自动绕回来）。</summary>
    public TimeSpan LastAt
    {
        get
        {
            int total = (int)StartAt.TotalMinutes + (Count - 1) * IntervalMinutes;
            return TimeSpan.FromMinutes(((total % (24 * 60)) + 24 * 60) % (24 * 60));
        }
    }
}

/// <summary>一次「到点了」的事件。</summary>
public readonly struct AlarmFiring
{
    public readonly Alarm Alarm;

    /// <summary>本该响的时刻。用于判断是否已经错过太久。</summary>
    public readonly DateTime DueAt;

    /// <summary>这是不是一串连锁闹钟里的最后一个（关掉它就整串结束）。</summary>
    public readonly bool IsLastInChain;

    public AlarmFiring(Alarm alarm, DateTime dueAt, bool isLastInChain)
    {
        Alarm = alarm;
        DueAt = dueAt;
        IsLastInChain = isLastInChain;
    }
}

/// <summary>
/// 闹钟集合的纯逻辑：排序、到期判定、连锁生成与整串关闭、响过就删。
/// **刻意不碰界面、不碰磁盘、不读系统时钟** —— 时间一律由调用方传进来，
/// 这样 --selftest 可以在任意时刻上断言，不必等到半夜三点去验。
/// </summary>
public sealed class AlarmBook
{
    private readonly List<Alarm> _items = new();

    /// <summary>
    /// 已报过的响铃：闹钟 Id → 那一次的**时刻**。
    ///
    /// ⚠️ 存时刻而不是存"响过了"这个布尔值，是这段代码最容易写错的地方。
    /// 早期的写法是 `if (_firedAt.ContainsKey(a.Id)) continue;` —— 那等于
    /// 「这个闹钟响过一次就报废」。对一次性闹钟看不出问题，
    /// 但「每天 8:00」的闹钟第二天再也不会响，而且**自检里如果只测一次响铃根本发现不了**。
    /// 现在改成比时刻：只有"同一个时刻已经报过"才跳过，新的时刻照报。
    /// </summary>
    private readonly Dictionary<string, DateTime> _firedAt = new();

    public IReadOnlyList<Alarm> Items => _items;

    public int Count => _items.Count;

    /// <summary>
    /// 错过多久就不补响。用户选的是「错过就不补」，所以这里不是"宽容窗口"，
    /// 而是"程序刚从睡眠/关机里醒来时，多久之内的还认"。默认 90 秒：
    /// 挂件每 200ms 刷一次，正常路径绝不会错过；只有刚唤醒那一下可能差几十秒。
    /// </summary>
    public int MissGraceSeconds { get; set; } = 90;

    public void Add(Alarm a)
    {
        a.Normalize();
        // 同一个 Id 已在列表里就替换，避免连锁生成时手滑加出两份
        int existing = _items.FindIndex(x => x.Id == a.Id);
        if (existing >= 0) _items[existing] = a;
        else _items.Add(a);
    }

    public void AddRange(IEnumerable<Alarm> items)
    {
        foreach (Alarm a in items) Add(a);
    }

    public Alarm? Find(string id) => _items.FirstOrDefault(x => x.Id == id);

    public bool Remove(string id)
    {
        _firedAt.Remove(id);
        return _items.RemoveAll(x => x.Id == id) > 0;
    }

    public void Clear()
    {
        _items.Clear();
        _firedAt.Clear();
    }

    /// <summary>把列表整理好：去重、修正字段、按四点分界排序。</summary>
    public void NormalizeAll()
    {
        foreach (Alarm a in _items) a.Normalize();

        // 按 Id 去重（保留先出现的那个）
        var seen = new HashSet<string>();
        _items.RemoveAll(a => !seen.Add(a.Id));

        Sort();
    }

    /// <summary>
    /// 按「凌晨四点为界」的钟点顺序排。
    /// 同一钟点内，连锁闹钟按序号排，这样一串起床闹钟会连着显示。
    /// </summary>
    public void Sort()
    {
        _items.Sort((x, y) =>
        {
            int c = x.SortKey.CompareTo(y.SortKey);
            if (c != 0) return c;
            c = string.CompareOrdinal(x.ChainId, y.ChainId);
            if (c != 0) return c;
            return x.ChainIndex.CompareTo(y.ChainIndex);
        });
    }

    /// <summary>排序后的快照，供界面渲染（不暴露内部 List，免得界面顺手改乱顺序）。</summary>
    public List<Alarm> Sorted() => _items.OrderBy(a => a.SortKey)
                                         .ThenBy(a => a.ChainId, StringComparer.Ordinal)
                                         .ThenBy(a => a.ChainIndex)
                                         .ToList();

    // ---- 到期判定 ----

    /// <summary>消费一次响铃：一次性立即删除或关闭，响铃窗口保留自己的快照。</summary>
    public void ConsumeOccurrence(Alarm a)
    {
        if (a.Repeat != AlarmRepeat.Once) return;
        if (a.DeleteAfterRing) Remove(a.Id);
        else
        {
            Alarm? stored = _items.FirstOrDefault(x => x.Id == a.Id);
            if (stored is not null) stored.Enabled = false;
        }
    }

    /// <summary>
    /// 在 <paramref name="from"/>（不含）到 <paramref name="to"/>（含）之间，有哪些闹钟该响。
    /// 返回按时刻先后排好的列表。
    ///
    /// 两条规则：
    ///  ① 同一次响铃只报一次 —— 靠 <see cref="_firedAt"/> 记账，200ms 的刷新循环不会重复报。
    ///  ② **错过就不补** —— 只检查当前时刻前 MissGraceSeconds 内的响铃。
    ///     这正是用户要的：早上开机不会被一堆昨天的闹钟轰炸。
    /// </summary>
    public List<AlarmFiring> DueBetween(DateTime from, DateTime to)
    {
        var result = new List<AlarmFiring>();
        if (to < from) return result;
        // 睡眠后 from 可能停留在昨天。以当前时刻限制窗口，避免补响旧闹钟，
        // 也避免昨天已报过的候选遮住今天的响铃。
        DateTime searchFrom = to.AddSeconds(-Math.Max(0, MissGraceSeconds) - 1);

        foreach (Alarm a in _items)
        {
            if (!a.Enabled) continue;

            // 从"宽松窗口"往前找，这样刚落下的响铃也能被算到
            DateTime? next = a.NextRingAfter(searchFrom);
            if (next is not DateTime ringAt) continue;

            if (ringAt > to) continue;

            // 这一次（这个时刻）已经报过了 → 跳过。**不要**因为"这个闹钟响过"就永久跳过，
            // 否则每天重复的闹钟只响一次（见 _firedAt 的注释）。
            if (_firedAt.TryGetValue(a.Id, out DateTime already) && already == ringAt) continue;

            // 错过太久 → 记账吞掉，不报给界面（用户选的"错过就不补"）
            if (ringAt < to.AddSeconds(-Math.Max(0, MissGraceSeconds)))
            {
                _firedAt[a.Id] = ringAt;
                continue;
            }

            _firedAt[a.Id] = ringAt;
            result.Add(new AlarmFiring(a, ringAt, IsLastInChain(a)));
        }

        result.Sort((x, y) => x.DueAt.CompareTo(y.DueAt));
        return result;
    }

    /// <summary>只应用草稿实际修改的字段；保留响铃记账、运行中新增项及后台变化。</summary>
    public void MergeEdits(IReadOnlyList<Alarm> baseline, AlarmBook edited)
    {
        var originals = baseline.ToDictionary(a => a.Id);
        foreach (Alarm original in baseline)
        {
            Alarm? draft = edited.Find(original.Id);
            if (draft is null) { Remove(original.Id); continue; }
            Alarm? live = Find(original.Id);
            // 已消费的一次性闹钟不能被旧草稿复活。
            if (live is null) continue;
            // 同一字段有后台变化时保留实时值。无冲突的编辑仍可保存。
            if (draft.Label != original.Label && live.Label == original.Label) live.Label = draft.Label;
            if (draft.TimeOfDay != original.TimeOfDay && live.TimeOfDay == original.TimeOfDay) live.TimeOfDay = draft.TimeOfDay;
            if (draft.Enabled != original.Enabled && live.Enabled == original.Enabled) live.Enabled = draft.Enabled;
            if (draft.Repeat != original.Repeat && live.Repeat == original.Repeat) live.Repeat = draft.Repeat;
            if (!draft.Days.SetEquals(original.Days) && live.Days.SetEquals(original.Days)) live.Days = new HashSet<DayOfWeek>(draft.Days);
            if (draft.DeleteAfterRing != original.DeleteAfterRing && live.DeleteAfterRing == original.DeleteAfterRing) live.DeleteAfterRing = draft.DeleteAfterRing;
            if (draft.ChainId != original.ChainId && live.ChainId == original.ChainId) live.ChainId = draft.ChainId;
            if (draft.ChainIndex != original.ChainIndex && live.ChainIndex == original.ChainIndex) live.ChainIndex = draft.ChainIndex;
        }
        foreach (Alarm draft in edited.Items)
            if (!originals.ContainsKey(draft.Id) && Find(draft.Id) is null) Add(draft.Clone());
        NormalizeAll();
    }

    /// <summary>这个闹钟离它这一串的末尾还有几个（含自己）。不在串里返回 1。</summary>
    public int RemainingInChain(Alarm a)
    {
        if (string.IsNullOrEmpty(a.ChainId)) return 1;
        return _items.Count(x => x.ChainId == a.ChainId && x.ChainIndex >= a.ChainIndex);
    }

    /// <summary>它是不是这一串里的最后一个。</summary>
    public bool IsLastInChain(Alarm a)
    {
        if (string.IsNullOrEmpty(a.ChainId)) return true;
        int max = _items.Where(x => x.ChainId == a.ChainId)
                        .Select(x => x.ChainIndex)
                        .DefaultIfEmpty(0)
                        .Max();
        return a.ChainIndex >= max;
    }

    /// <summary>
    /// 用户「关掉了这次响铃」，接下来该发生什么。返回这次操作产生的说明文字。
    ///
    /// 语义（对应用户要的「响铃时关闭一个，第二个仍会响起；手动关闭时一起关闭」）：
    ///  · 普通闹钟：一次性 → 删掉；重复的 → 保留（等下一次）。
    ///  · 连锁串里**不是最后一个**：只把当前这个记成"这次响过了"，
    ///    后面的照常按各自时刻继续响 —— 这就是"关掉一个，第二个仍会响起"。
    ///  · 连锁串里**是最后一个**：整串结束，一次性的话把整串删掉。
    /// </summary>
    public string Dismiss(Alarm a, DateTime now)
    {
        bool last = IsLastInChain(a);

        if (string.IsNullOrEmpty(a.ChainId))
        {
            if (a.Repeat == AlarmRepeat.Once && a.DeleteAfterRing)
            {
                Remove(a.Id);
                return "已删除这个一次性闹钟。";
            }
            return "已关闭，下次仍会响。";
        }

        if (!last)
        {
            int left = RemainingInChain(a) - 1;
            return $"已关闭，还有 {left} 个会继续响。";
        }

        // 最后一个：整串收尾
        string chainId = a.ChainId;
        int n = _items.Count(x => x.ChainId == chainId);
        bool once = _items.Where(x => x.ChainId == chainId).All(x => x.Repeat == AlarmRepeat.Once);

        if (once)
        {
            _firedAt.Keys.Where(k => _items.Any(x => x.ChainId == chainId && x.Id == k)).ToList()
                         .ForEach(k => _firedAt.Remove(k));
            int removed = _items.RemoveAll(x => x.ChainId == chainId);
            return $"这是最后一个，整串 {removed} 个已删除。";
        }
        return "这是最后一个，这一串起床闹钟已结束。";
    }

    /// <summary>
    /// 手动关闭整串（用户说的「手动关闭时一起关闭」）。
    /// 与 <see cref="Dismiss"/> 的区别：这个是**主动**把这一串全部停掉，
    /// 界面上点「关闭整串」或把床上那串一次性关掉时用。
    /// </summary>
    public string DismissWholeChain(Alarm a, DateTime now)
    {
        if (string.IsNullOrEmpty(a.ChainId))
            return a.Repeat == AlarmRepeat.Once && a.DeleteAfterRing
                ? Dismiss(a, now)
                : "已关闭这个闹钟。";

        string chainId = a.ChainId;
        int n = _items.Count(x => x.ChainId == chainId);
        bool once = _items.Where(x => x.ChainId == chainId).All(x => x.Repeat == AlarmRepeat.Once);

        if (once)
        {
            _items.RemoveAll(x => x.ChainId == chainId);
            return $"整串 {n} 个起床闹钟已全部关闭并删除。";
        }
        foreach (Alarm x in _items.Where(x => x.ChainId == chainId)) x.Enabled = false;
        return $"整串 {n} 个起床闹钟已全部关闭。";
    }

    /// <summary>「稍后提醒」：把当前这个推迟 <paramref name="minutes"/> 分钟再响，其余不动。</summary>
    public string Snooze(Alarm a, DateTime now, int minutes)
    {
        if (minutes < 1) minutes = 1;

        // 记成"已响过"，然后插一个新的临时闹钟
        _firedAt[a.Id] = now;

        var snoozed = new Alarm
        {
            Id = Alarm.NewId(),
            Label = string.IsNullOrEmpty(a.Label) ? "稍后提醒" : a.Label + "（稍后）",
            TimeOfDay = TimeSpan.FromMinutes((now.Hour * 60 + now.Minute + minutes) % (24 * 60)),
            Enabled = true,
            Repeat = AlarmRepeat.Once,
            DeleteAfterRing = true,
            ChainId = string.Empty   // 脱离原串：稍后提醒不该把整串唤醒逻辑带过来
        };
        snoozed.Normalize();
        Add(snoozed);
        Sort();
        return $"{minutes} 分钟后（{snoozed.TimeText}）再响一次。";
    }

    /// <summary>当前已生效的闹钟里，最近的那一次响铃时刻；没有则 null。</summary>
    public DateTime? NextRing(DateTime now)
    {
        DateTime? best = null;
        foreach (Alarm a in _items)
        {
            if (!a.Enabled) continue;
            DateTime? n = a.NextRingAfter(now);
            if (n is DateTime t && (best is null || t < best)) best = t;
        }
        return best;
    }

    /// <summary>
    /// 半小时内（含）要响的闹钟。挂件只在有这种闹钟时才把闹钟块亮出来；
    /// 手机端据此决定要不要写进通知栏。
    /// </summary>
    public const int ImminentMinutes = 30;

    public List<Alarm> Imminent(DateTime now)
    {
        DateTime limit = now.AddMinutes(ImminentMinutes);
        var list = new List<(Alarm a, DateTime at)>();
        foreach (Alarm a in _items)
        {
            if (!a.Enabled) continue;
            DateTime? n = a.NextRingAfter(now);
            if (n is DateTime t && t <= limit) list.Add((a, t));
        }
        list.Sort((x, y) => x.at.CompareTo(y.at));
        return list.Select(p => p.a).ToList();
    }

    /// <summary>半小时内最早的那次响铃时刻。</summary>
    public DateTime? NextImminentRing(DateTime now)
    {
        List<Alarm> im = Imminent(now);
        if (im.Count == 0) return null;
        DateTime? best = null;
        foreach (Alarm a in im)
        {
            DateTime? n = a.NextRingAfter(now);
            if (n is DateTime t && (best is null || t < best)) best = t;
        }
        return best;
    }

    /// <summary>
    /// 手动添加一个普通闹钟。
    /// </summary>
    public Alarm AddManual(TimeSpan at, string label, AlarmRepeat repeat,
                           IEnumerable<DayOfWeek>? days, bool deleteAfterRing)
    {
        var a = new Alarm
        {
            Id = Alarm.NewId(),
            Label = label ?? string.Empty,
            TimeOfDay = at,
            Enabled = true,
            Repeat = repeat,
            Days = days is null ? new HashSet<DayOfWeek>() : new HashSet<DayOfWeek>(days),
            DeleteAfterRing = deleteAfterRing
        };
        a.Normalize();
        Add(a);
        Sort();
        return a;
    }

    /// <summary>
    /// 按「起始时间 + 间隔 + 次数」自动生成一串连锁起床闹钟。
    /// 同一串共用一个 <see cref="Alarm.ChainId"/>，因而能整串关闭、也能判断"是不是最后一个"。
    /// </summary>
    public List<Alarm> AddChain(AlarmChainSpec spec, string label, string? chainId = null)
    {
        spec.IsValid(out _);

        string cid = string.IsNullOrEmpty(chainId) ? "c" + Alarm.NewId() : chainId;
        var made = new List<Alarm>();

        // 已经有一条同 id 的串就先清掉，避免重复生成时越加越多
        _items.RemoveAll(x => x.ChainId == cid);

        for (int i = 0; i < spec.Count; i++)
        {
            int total = (int)spec.StartAt.TotalMinutes + i * spec.IntervalMinutes;
            var a = new Alarm
            {
                Id = Alarm.NewId(),
                Label = label ?? string.Empty,
                TimeOfDay = TimeSpan.FromMinutes(((total % (24 * 60)) + 24 * 60) % (24 * 60)),
                Enabled = true,
                Repeat = spec.Daily ? AlarmRepeat.Daily : AlarmRepeat.Once,
                Days = new HashSet<DayOfWeek>(),
                DeleteAfterRing = !spec.Daily,
                ChainId = cid,
                ChainIndex = i + 1
            };
            if (spec.Daily && spec.WeekdaysOnly)
            {
                a.Repeat = AlarmRepeat.Weekly;
                foreach (DayOfWeek d in Alarm.WeekOrder)
                    if (d is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) a.Days.Add(d);
            }
            a.Normalize();
            _items.Add(a);
            made.Add(a);
        }

        Sort();
        return made;
    }

    /// <summary>把整串起床闹钟调成"生效/不生效"，不删除。</summary>
    public void SetChainEnabled(string chainId, bool enabled)
    {
        foreach (Alarm a in _items.Where(x => x.ChainId == chainId)) a.Enabled = enabled;
    }

    /// <summary>已生成的连锁串（按 ChainId 分组），供设置窗口回显。</summary>
    public List<(string ChainId, List<Alarm> Items)> Chains()
    {
        return _items.Where(a => !string.IsNullOrEmpty(a.ChainId))
                     .GroupBy(a => a.ChainId)
                     .Select(g => (g.Key, g.OrderBy(x => x.ChainIndex).ToList()))
                     .ToList();
    }

    /// <summary>从当前列表反推连锁参数，供设置窗口回显（取第一串）。</summary>
    public AlarmChainSpec? InferChainSpec(string chainId)
    {
        List<Alarm> items = _items.Where(x => x.ChainId == chainId)
                                  .OrderBy(x => x.ChainIndex).ToList();
        if (items.Count == 0) return null;

        int interval = items.Count >= 2
            ? (int)(items[1].TimeOfDay - items[0].TimeOfDay).TotalMinutes
            : 0;
        if (interval < 0) interval += 24 * 60;

        bool weekdays = items[0].Repeat == AlarmRepeat.Weekly
            && items[0].Days.Count == 5
            && !items[0].Days.Contains(DayOfWeek.Saturday)
            && !items[0].Days.Contains(DayOfWeek.Sunday);

        return new AlarmChainSpec
        {
            StartAt = items[0].TimeOfDay,
            IntervalMinutes = interval <= 0 ? 5 : interval,
            Count = items.Count,
            Daily = items[0].Repeat != AlarmRepeat.Once,
            WeekdaysOnly = weekdays
        };
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace TimeGuard;

/// <summary>
/// 规则自检：每一节都做两件事——① 逐分钟模拟与计分引擎对拍（两边独立实现）；
/// ② 关键边界时刻逐条断言。另附文案与配色的断言。
/// 用法：TimeGuard.exe --selftest [输出文件路径]
/// </summary>
public static class SelfTest
{
    private static readonly DateTime BaseNight = new(2026, 9, 21, 0, 0, 0);

    public static bool Write(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TimeGuard 规则自检");
        sb.AppendLine("# 每节先「逐分钟模拟对拍」（模拟与引擎两边各写一套，互不参考），再逐条断言关键边界。");
        sb.AppendLine();

        AppendSection(sb, "默认规则", SleepRules.Default);

        SleepRules current = AppConfig.Load().Rules;
        if (IsDefault(current))
            sb.AppendLine("（当前 config.json 的设置与默认规则一致，不重复出表）");
        else
            AppendSection(sb, "当前设置（读自 config.json）", current);

        AppendVisualChecks(sb);
        AppendSettingsChecks(sb);
        AppendCountdownChecks(sb);
        AppendCountdownVisibilityChecks(sb);
        AppendPlacementChecks(sb);
        AppendDpiWidthChecks(sb);
        AppendConfigFileChecks(sb);
        AppendAlarmChecks(sb);
        AppendReviewRegressionChecks(sb);

        bool allPass = ReportPassed(sb.ToString());

        sb.AppendLine();
        sb.AppendLine(allPass ? "结论：全部通过" : "结论：存在不通过项，见上方 FAIL");

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return allPass;
    }

    internal static bool ReportPassed(string report)
        => !report.Split('\n').Any(line => line.StartsWith("FAIL", StringComparison.Ordinal));

    private static void AppendReviewRegressionChecks(StringBuilder sb)
    {
        sb.AppendLine("\n## 发布前审查回归");
        DateTime due = new(2026, 10, 11, 8, 0, 0);
        var live = new AlarmBook();
        live.Add(new Alarm { Id = "daily", TimeOfDay = due.TimeOfDay, Repeat = AlarmRepeat.Daily });
        CheckNum(sb, "休眠一小时不补响", live.DueBetween(due.AddSeconds(-1), due.AddHours(1)).Count, 0);
        CheckNum(sb, "当天正常到点响铃", live.DueBetween(due.AddSeconds(-1), due).Count, 1);
        CheckNum(sb, "昨天已响后休眠到今天仍响", live.DueBetween(due, due.AddDays(1)).Count, 1);
        CheckNum(sb, "宽容边界外不响", new AlarmBookWithDaily().Book.DueBetween(due.AddSeconds(-1), due.AddSeconds(91)).Count, 0);
        CheckNum(sb, "宽容边界仍响", new AlarmBookWithDaily().Book.DueBetween(due.AddSeconds(-1), due.AddSeconds(90)).Count, 1);

        var baseline = live.Items.Select(a => a.Clone()).ToList();
        var draft = new AlarmBook();
        draft.AddRange(baseline.Select(a => a.Clone()));
        live.MergeEdits(baseline, draft);
        CheckNum(sb, "保存未修改草稿不重复响", live.DueBetween(due.AddDays(1), due.AddDays(1).AddSeconds(1)).Count, 0);
        live.Add(new Alarm { Id = "once", TimeOfDay = due.TimeOfDay, Repeat = AlarmRepeat.Once });
        baseline = live.Items.Select(a => a.Clone()).ToList();
        draft.Clear(); draft.AddRange(baseline.Select(a => a.Clone()));
        live.ConsumeOccurrence(live.Find("once")!);
        live.Snooze(live.Find("daily")!, due.AddDays(1), 5);
        live.Find("daily")!.Enabled = false;
        draft.Find("daily")!.Label = "新名称";
        live.MergeEdits(baseline, draft);
        sb.AppendLine(Mark(live.Find("once") is null && live.Count == 2 && !live.Find("daily")!.Enabled && live.Find("daily")!.Label == "新名称",
            "草稿保留消费、稍后提醒、后台开关，并应用名称编辑", $"count={live.Count}"));
        draft.Remove("daily");
        live.MergeEdits(baseline, draft);
        CheckNum(sb, "草稿主动删除仍生效且不删除后台新增项", live.Count, 1);
        sb.AppendLine(Mark(!ReportPassed("PASS ok\nFAIL synthetic\n") && ReportPassed("PASS ok\n"), "自检失败结果返回 false", "供退出码使用"));

        int pings = 0;
        using var sound = new AlarmSoundPlayer(() => pings++);
        var playerField = typeof(AlarmSoundPlayer).GetField("_player", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var fallback = typeof(AlarmSoundPlayer).GetMethod("FallbackToSystem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        playerField.SetValue(sound, new MediaPlayer());
        fallback.Invoke(sound, null);
        sound.PingSystemSound();
        sb.AppendLine(Mark(playerField.GetValue(sound) is null && sound.IsPlaying && pings == 2, "失败播放器释放后系统音立即回退且可持续响", $"pings={pings}"));
        sound.Stop(); sound.PingSystemSound();
        CheckNum(sb, "停止后不再发系统音", pings, 2);
    }

    private sealed class AlarmBookWithDaily
    {
        public AlarmBook Book { get; } = new();
        public AlarmBookWithDaily() => Book.Add(new Alarm { Id = "boundary", TimeOfDay = new TimeSpan(8, 0, 0), Repeat = AlarmRepeat.Daily });
    }

    private static void AppendCountdownVisibilityChecks(StringBuilder sb)
    {
        DateTime now = new(2026, 10, 7, 22, 0, 0);
        var c = new Countdown();
        void Check(string name, bool visible) => sb.AppendLine(
            (c.Tick(now).HasActiveTime == visible ? "PASS  " : "FAIL  ") + name);
        Check("未设置倒计时时自动隐藏", false);
        Countdown.TryParse("45m", now, out CountdownPlan plan, out _);
        c.Start(plan, now);
        Check("运行时显示剩余时间", true);
        now = now.AddMinutes(10);
        c.Pause(now);
        Check("暂停时保留剩余时间", true);
        now = now.AddMinutes(10);
        c.Resume(now);
        now = now.AddMinutes(35);
        Check("到点后自动隐藏", false);
        c.Reset();
        Check("重置后自动隐藏", false);
    }

    // ---- 一节 = 一套规则 ----
    private static void AppendSection(StringBuilder sb, string title, SleepRules r)
    {
        (DateTime show, DateTime deduct, DateTime hide) = r.Resolve(BaseNight + r.ShowAt);
        long stepSec = r.MinutesPerPoint * 60L;

        sb.AppendLine("## " + title);
        sb.AppendLine("参数：" + SleepRules.FormatHm(r.ShowAt) + " 出现（开始记分）｜"
            + SleepRules.FormatHm(r.DeductAt) + " 开始扣分｜每 " + r.MinutesPerPoint + " 分钟扣 " + r.PointsPerStep
            + " 分｜满分 " + r.MaxScore + "｜" + SleepRules.FormatHm(r.HideAt) + " 隐藏");
        sb.AppendLine("推导：" + r.Describe(BaseNight));
        sb.AppendLine();

        int mismatch = CrossCheck(sb, r, show, deduct, hide);
        sb.AppendLine(mismatch == 0
            ? "对拍：整夜逐分钟模拟与计分引擎完全一致（0 处不符）"
            : "对拍：共 " + mismatch + " 处不符（上方仅列前 10 条）");
        sb.AppendLine();

        sb.AppendLine("边界断言：");
        AppendCheckpoint(sb, show.AddMinutes(-1), "隐藏时段：不显示", null, NightPhase.Daytime, r);
        AppendCheckpoint(sb, show, "到点出现：满分待命", r.MaxScore, NightPhase.Countdown, r);
        AppendCheckpoint(sb, deduct.AddSeconds(-1), "扣分前最后一秒仍是满分", r.MaxScore, NightPhase.Countdown, r);
        AppendCheckpoint(sb, deduct, "扣分起点：此刻仍满分，只是进入扣分段", r.MaxScore, NightPhase.Deducting, r);
        AppendCheckpoint(sb, deduct.AddSeconds(stepSec - 1), "差一秒满一个周期：不掉分", r.MaxScore, NightPhase.Deducting, r);

        int firstDrop = Math.Max(0, r.MaxScore - r.PointsPerStep);
        AppendCheckpoint(sb, deduct.AddSeconds(stepSec), "满 " + r.MinutesPerPoint + " 分钟：扣 " + r.PointsPerStep + " 分",
            firstDrop, firstDrop == 0 ? NightPhase.Exhausted : NightPhase.Deducting, r);

        int midSteps = r.MaxScore / 2 / r.PointsPerStep;
        if (midSteps > 0)
        {
            DateTime midTime = deduct.AddSeconds(stepSec * midSteps);
            int midScore = r.MaxScore - midSteps * r.PointsPerStep;
            if (midTime < hide)
                AppendCheckpoint(sb, midTime, "扣到一半附近", midScore, NightPhase.Deducting, r);
        }

        DateTime zero = deduct + r.SpanToZero;
        int stepsToZero = (int)Math.Ceiling(r.MaxScore / (double)r.PointsPerStep);
        int lowestPositive = Math.Max(0, r.MaxScore - (stepsToZero - 1) * r.PointsPerStep);

        if (zero < hide)
        {
            AppendCheckpoint(sb, zero.AddSeconds(-1), "扣光前一秒：最低正分", lowestPositive, NightPhase.Deducting, r);
            AppendCheckpoint(sb, zero, "扣光：锁 0，不上负", 0, NightPhase.Exhausted, r);
            AppendCheckpoint(sb, hide.AddSeconds(-1), "隐藏前一直锁 0", 0, NightPhase.Exhausted, r);
        }
        else if (zero == hide)
        {
            AppendCheckpoint(sb, zero.AddSeconds(-1), "扣光前一秒：最低正分", lowestPositive, NightPhase.Deducting, r);
            AppendCheckpoint(sb, zero, "扣光：锁 0，不上负", 0, NightPhase.Exhausted, r);
        }
        else
        {
            sb.AppendLine("    （注：按当前速率，扣光时刻 " + zero.ToString("HH:mm", CultureInfo.InvariantCulture)
                + " 晚于隐藏时刻 " + hide.ToString("HH:mm", CultureInfo.InvariantCulture) + "，本夜扣不到 0，跳过归零断言）");
        }

        AppendCheckpoint(sb, hide, "到隐藏时刻：不再显示", null, NightPhase.Daytime, r);
        AppendCheckpoint(sb, show.AddDays(1), "次日同时刻：重新出现，回到满分", r.MaxScore, NightPhase.Countdown, r);
        sb.AppendLine();

        sb.AppendLine("时间轴（每 10 分钟）：");
        sb.AppendLine("时间\t分数\t阶段");
        for (DateTime t = show.AddMinutes(-10); t <= hide.AddMinutes(10); t = t.AddMinutes(10))
        {
            ScoreState s = ScoreEngine.Evaluate(t, r);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:MM-dd HH:mm}\t{1}\t{2}",
                t,
                s.Phase == NightPhase.Daytime ? "-" : s.Score.ToString(CultureInfo.InvariantCulture),
                Phase(s.Phase)));
        }
        sb.AppendLine();
    }

    /// <summary>独立实现：按分钟自己算一遍，与引擎对拍。</summary>
    private static int CrossCheck(StringBuilder sb, SleepRules r, DateTime show, DateTime deduct, DateTime hide)
    {
        int bad = 0;

        for (DateTime t = show.AddHours(-2); t <= hide.AddHours(2); t = t.AddMinutes(1))
        {
            int expected;
            if (t < show || t >= hide) expected = -1;                    // 隐藏时段
            else if (t < deduct) expected = r.MaxScore;                  // 满分待命
            else
            {
                long minutes = (long)(t - deduct).TotalMinutes;
                long dropped = minutes / r.MinutesPerPoint * r.PointsPerStep;
                expected = (int)Math.Max(0, r.MaxScore - dropped);
            }

            ScoreState s = ScoreEngine.Evaluate(t, r);
            int actual = s.Phase == NightPhase.Daytime ? -1 : s.Score;
            if (actual == expected) continue;

            bad++;
            if (bad <= 10)
                sb.AppendLine("FAIL 对拍不一致 " + t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + "：模拟=" + expected + "，引擎=" + actual);
        }

        return bad;
    }

    private static void AppendVisualChecks(StringBuilder sb)
    {
        sb.AppendLine("## 文案与配色");
        SleepRules byDefault = SleepRules.Default;
        CheckText(sb, "默认·100 分", ScoreVisual.StatusForScore(100, byDefault), "快去睡吧。");
        CheckText(sb, "默认·80 分", ScoreVisual.StatusForScore(80, byDefault), "快去睡吧。");
        CheckText(sb, "默认·79 分", ScoreVisual.StatusForScore(79, byDefault), "头发要紧，睡觉！");
        CheckText(sb, "默认·60 分", ScoreVisual.StatusForScore(60, byDefault), "头发要紧，睡觉！");
        CheckText(sb, "默认·59 分", ScoreVisual.StatusForScore(59, byDefault), "不睡等死吧你！！！");
        CheckText(sb, "默认·0 分", ScoreVisual.StatusForScore(0, byDefault), "不睡等死吧你！！！");
        CheckText(sb, "默认·扣分前文案", byDefault.StatusBefore, "夜深了，注意休息~");
        sb.AppendLine();

        // 自定义文案要真的走到界面上，不能只停在设置窗口的输入框里
        sb.AppendLine("自定义文案（在设置里改成别的字，分数段该用哪句就用哪句）：");
        var custom = new SleepRules
        {
            StatusBefore = "还没到点，先别急",
            StatusHigh = "还行，继续撑",
            StatusMid = "该睡了",
            StatusLow = ""                       // 留空 = 这一档不显示文字
        };
        CheckText(sb, "  自定义·扣分前", custom.StatusBefore, "还没到点，先别急");
        CheckText(sb, $"  自定义·{ScoreVisual.HighLine} 分（分界上沿）", ScoreVisual.StatusForScore(ScoreVisual.HighLine, custom), "还行，继续撑");
        CheckText(sb, $"  自定义·{ScoreVisual.HighLine - 1} 分（刚落到中档）", ScoreVisual.StatusForScore(ScoreVisual.HighLine - 1, custom), "该睡了");
        CheckText(sb, $"  自定义·{ScoreVisual.MidLine} 分（分界上沿）", ScoreVisual.StatusForScore(ScoreVisual.MidLine, custom), "该睡了");
        CheckText(sb, $"  自定义·{ScoreVisual.MidLine - 1} 分（刚落到低档）", ScoreVisual.StatusForScore(ScoreVisual.MidLine - 1, custom), "");
        CheckText(sb, "  自定义·留空档位不显示文字", ScoreVisual.StatusForScore(0, custom), "");
        sb.AppendLine();

        sb.AppendLine("配色支点（分数 → 颜色）：");
        foreach (var stop in ScoreVisual.Ramp)
            sb.AppendLine("    " + stop.Score.ToString(CultureInfo.InvariantCulture).PadLeft(3)
                + " 分 → " + ScoreVisual.Hex(ScoreVisual.ColorForScore(stop.Score)));
        sb.AppendLine();

        int worst = 0, worstAt = 0;
        for (int s = 0; s < ScoreEngine.MaxScore; s++)
        {
            Color a = ScoreVisual.ColorForScore(s);
            Color b = ScoreVisual.ColorForScore(s + 1);
            int d = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
            if (d > worst) { worst = d; worstAt = s; }
        }

        sb.AppendLine(worst <= 12
            ? "PASS  渐变色连续性：相邻分数最大色差 " + worst + "/255（在 " + worstAt + " 分处），平滑无跳变"
            : "FAIL  渐变色不连续：相邻分数最大色差 " + worst + "/255（在 " + worstAt + " 分处）");
        sb.AppendLine();
    }

    /// <summary>设置窗口那五段文本的取值校验：该接受的接受、该拦的拦。</summary>
    private static void AppendSettingsChecks(StringBuilder sb)
    {
        sb.AppendLine("## 设置窗口取值校验");
        sb.AppendLine("（模拟你在设置里输入的内容，看是否被正确接受 / 拦截）");
        sb.AppendLine();

        (string Show, string Deduct, string Hide, string Min, string Pts, bool Valid, string Note)[] cases =
        {
            ("20:30", "23:30", "06:30", "3",   "1",   true,  "默认值"),
            ("22:00", "00:00", "07:00", "10",  "10",  true,  "跨零点扣分：10 分钟扣 10 分"),
            ("20",    "23",    "6",     "3",   "1",   true,  "只填小时也认"),
            ("20：30", "23：30", "06：30", "3", "1",   true,  "全角冒号也认"),
            ("23:30", "23:30", "06:30", "3",   "1",   true,  "出现即开始扣分"),
            ("20:30", "23:30", "06:30", "0",   "1",   false, "每 0 分钟一轮"),
            ("20:30", "23:30", "06:30", "3",   "0",   false, "每轮扣 0 分"),
            ("20:30", "23:30", "20:30", "3",   "1",   false, "隐藏时刻 = 出现时刻"),
            ("20:30", "12:00", "06:30", "3",   "1",   false, "扣分时刻落在显示时段之外"),
            ("20:30", "23:30", "06:30", "3",   "101", false, "每轮扣分超过满分"),
            ("20:30", "23:30", "06:30", "721", "1",   false, "每轮周期超过 720 分钟"),
            ("abc",   "23:30", "06:30", "3",   "1",   false, "时间填了非数字"),
            ("25:00", "23:30", "06:30", "3",   "1",   false, "小时超过 23"),
            ("20:30", "23:70", "06:30", "3",   "1",   false, "分钟超过 59"),
            ("20:30", "23:30", "06:30", "x",   "1",   false, "周期填了非数字")
        };

        foreach (var c in cases)
        {
            bool accepted = SettingsWindow.TryBuildRules(c.Show, c.Deduct, c.Hide, c.Min, c.Pts, out SleepRules r, out string err);
            bool ok = accepted == c.Valid;

            string detail = accepted ? "接受 —— " + r.Describe(BaseNight) : "拦截 —— " + err;
            sb.AppendLine((ok ? "PASS  " : "FAIL  ")
                + c.Show + "/" + c.Deduct + "/" + c.Hide + "，每 " + c.Min + " 分钟扣 " + c.Pts + " 分"
                + "（" + c.Note + "）→ " + detail);
        }

        // ---- 四句状态文案：设置窗口收下 → 写进配置 → 重启读回来 ----
        sb.AppendLine();
        sb.AppendLine("状态文案（四句话经设置窗口 → 配置 → 读回，全程不碰真实配置文件）：");

        const string before = "还没到点，先别急";
        const string high = "还行，继续撑";
        const string mid = "该睡了";

        if (!SettingsWindow.TryBuildRules("20:30", "23:30", "06:30", "3", "1",
                "  " + before + "  ", high, mid, string.Empty,
                out SleepRules typed, out string typeErr))
        {
            sb.AppendLine("FAIL  带文案的输入被挡住了 —— " + typeErr);
        }
        else
        {
            CheckText(sb, "  首尾空白自动去掉", typed.StatusBefore, before);
            CheckText(sb, "  第二句原样收下", typed.StatusHigh, high);
            CheckText(sb, "  留空档位 = 空串（不显示文字）", typed.StatusLow, string.Empty);

            var cfg = new AppConfig();
            cfg.ApplyRules(typed);
            AppConfig back = AppConfig.FromJsonForTest(AppConfig.ToJsonForTest(cfg));

            CheckText(sb, "  写进配置再读回来·扣分前", back.Rules.StatusBefore, before);
            CheckText(sb, "  写进配置再读回来·高档位", back.Rules.StatusHigh, high);
            CheckText(sb, "  写进配置再读回来·留空档位", back.Rules.StatusLow, string.Empty);
            CheckText(sb, "  读回来就能直接用在界面上",
                ScoreVisual.StatusForScore(ScoreVisual.HighLine, back.Rules), high);
        }

        // 老配置文件里根本没有这几个字段 —— 必须落回默认文案，不能变成空白
        AppConfig old = AppConfig.FromJsonForTest("{\"ConfigVersion\":2,\"ShowAtText\":\"20:30\"}");
        CheckText(sb, "  老配置缺字段 → 用默认文案", old.Rules.StatusBefore, SleepRules.Default.StatusBefore);

        // 手工把 json 改成 null 也不能让程序措手不及
        AppConfig nulled = AppConfig.FromJsonForTest("{\"ConfigVersion\":2,\"StatusMidText\":null}");
        CheckText(sb, "  配置里写了 null → 用默认文案", nulled.Rules.StatusMid, SleepRules.Default.StatusMid);

        sb.AppendLine();
    }

    /// <summary>
    /// 倒计时（原 timecountdown.c 的功能）：输入解析表 → 状态机逐步走 → 落盘还原。
    /// 起点固定，与「现在几点」无关，任何时候跑结论都一样。
    /// </summary>
    private static void AppendCountdownChecks(StringBuilder sb)
    {
        DateTime t0 = new(2026, 9, 21, 22, 0, 0);

        sb.AppendLine("## 倒计时");
        sb.AppendLine("起点固定为 " + t0.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "，与当前时间无关。");
        sb.AppendLine();

        sb.AppendLine("输入解析（填什么 → 认不认、算成什么）：");
        (string Text, bool Ok, string Note)[] cases =
        {
            ("23:00",    true,  "标准写法"),
            ("23:00:30", true,  "带秒"),
            ("23：00",   true,  "全角冒号"),
            ("23",       true,  "纯数字 ≤23 当整点"),
            ("21",       true,  "今天 21:00 已过 → 顺延到明天"),
            ("0",        true,  "顺延到明天零点"),
            ("45",       true,  "纯数字 >23 当分钟数"),
            ("24",       true,  "24 分钟"),
            ("45m",      true,  "分钟"),
            ("45分钟",   true,  "中文单位"),
            ("2h",       true,  "小时"),
            ("1439m",    true,  "上限前一刻"),
            ("1441m",    false, "超出 24 小时"),
            ("0m",       false, "0 分钟无意义"),
            ("45x",      false, "单位只认 m / h"),
            ("abc",      false, "非数字"),
            ("",         false, "空输入"),
            ("25:00",    false, "小时超过 23"),
            ("23:70",    false, "分钟超过 59"),
            ("-1",       false, "负数（曾会算出过去时刻的截止点）"),
            ("-24",      false, "负数小时（曾会算出 -1 天的截止点）"),
            ("-0",       true,  "负零 = 0（int 没有负零，与 \"0\" 同义）"),
            ("-2147483648", false, "int 最小值（曾必抛 ArgumentOutOfRangeException）"),
            ("-1m",      false, "负数分钟"),
            ("-2h",      false, "负数小时")
        };

        foreach (var c in cases)
        {
            bool accepted = Countdown.TryParse(c.Text, t0, out CountdownPlan plan, out string err);
            bool ok = accepted == c.Ok;

            string detail;
            if (accepted)
            {
                // 独立算一遍截止点（不复用 Countdown 内部实现），用来对拍
                DateTime deadline = plan.Mode == CountdownMode.ClockTime
                    ? CdDeadline(plan.ClockTarget, t0)
                    : t0 + plan.Duration;

                detail = (plan.Mode == CountdownMode.ClockTime ? "时刻模式" : "时长模式")
                    + " → 截止 " + deadline.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "，待走 " + Countdown.Hms(plan.Duration);
            }
            else
            {
                detail = "拦截 —— " + err;
            }

            sb.AppendLine((ok ? "PASS  " : "FAIL  ") + "「" + c.Text + "」"
                + (ok ? "" : "（期望" + (c.Ok ? "接受" : "拦截") + "）")
                + " → " + detail + "　｜ " + c.Note);
        }
        sb.AppendLine();

        sb.AppendLine("状态机（时刻模式）：");
        var cd = new Countdown();
        if (!Countdown.TryParse("23:00", t0, out CountdownPlan p, out string e0))
        {
            sb.AppendLine("FAIL  解析 23:00 失败：" + e0);
            return;
        }

        cd.Start(p, t0);
        AppendCdPhase(sb, "22:00 起设 23:00", cd.Peek(t0).Phase, CountdownPhase.Running);
        AppendCd(sb, "  起点剩余", cd.Peek(t0).Remaining, "01:00:00");
        AppendCd(sb, "  59 分钟时剩余", cd.Peek(t0.AddMinutes(59)).Remaining, "00:01:00");
        AppendCd(sb, "  到点前一秒", cd.Peek(t0.AddMinutes(59).AddSeconds(59)).Remaining, "00:00:01");
        AppendCdPhase(sb, "  到点前一秒仍在走", cd.Peek(t0.AddMinutes(59).AddSeconds(59)).Phase, CountdownPhase.Running);

        CountdownState at = cd.Tick(t0.AddMinutes(60));
        AppendCdPhase(sb, "到点（+60 分钟）", at.Phase, CountdownPhase.Finished);
        AppendCd(sb, "  到点剩余", at.Remaining, "00:00:00");
        AppendCd(sb, "  到点后 10 分钟仍锁 0（不上负）", cd.Tick(t0.AddMinutes(70)).Remaining, "00:00:00");
        AppendCd(sb, "  到点后大字", Countdown.BigText(cd.Peek(t0.AddMinutes(70)).Phase, cd.Peek(t0.AddMinutes(70)).Remaining), "时间到！");

        cd.Restart(t0.AddMinutes(61));   // 此刻是 23:01，目标 23:00 已过
        AppendCdPhase(sb, "点「再来」", cd.Peek(t0.AddMinutes(61)).Phase, CountdownPhase.Running);
        AppendCd(sb, "  时刻模式的「再来」顺延到明天同一时刻", cd.Peek(t0.AddMinutes(61)).Remaining, "23:59:00");
        sb.AppendLine();

        // 时刻模式不给暂停：目标是个钉死的钟点，冻结剩余量再继续只会把截止点往后推，
        // 暂停 10 秒就等于把 23:00 变成 23:00:10。要停只能「重置」。
        sb.AppendLine("时刻模式不提供暂停：");
        AppendCd(sb, "  填 23:00 时的 PauseAllowed", cd.PauseAllowed ? "可以暂停" : "不能暂停", "不能暂停");
        AppendCd(sb, "  小字里写明", cd.SubLabel(CountdownPhase.Running), "到 23:00 为止 · 不可暂停");

        var cdNP = new Countdown();
        Countdown.TryParse("23:00", t0, out CountdownPlan pNP, out _);
        cdNP.Start(pNP, t0);
        bool pauseRejected = !cdNP.Pause(t0.AddMinutes(10));
        AppendCd(sb, "  22:10 点暂停被拒绝", pauseRejected ? "是" : "否", "是");
        AppendCdPhase(sb, "  被拒后阶段不变", cdNP.Peek(t0.AddMinutes(10)).Phase, CountdownPhase.Running);
        AppendCd(sb, "  22:10 时剩余（没被暂停挪动）", cdNP.Peek(t0.AddMinutes(10)).Remaining, "00:50:00");
        AppendCd(sb, "  再等 10 秒仍是 23:00 减 10 分 10 秒", cdNP.Peek(t0.AddMinutes(10).AddSeconds(10)).Remaining, "00:49:50");
        AppendCdPhase(sb, "  一路走到 23:00 才到点", cdNP.Tick(t0.AddMinutes(60)).Phase, CountdownPhase.Finished);
        sb.AppendLine();

        sb.AppendLine("状态机（时长模式 + 暂停/继续）：");
        var c2 = new Countdown();
        if (!Countdown.TryParse("45m", t0, out CountdownPlan p2, out string e2))
        {
            sb.AppendLine("FAIL  解析 45m 失败：" + e2);
            return;
        }

        c2.Start(p2, t0);
        AppendCd(sb, "22:00 设 45m", c2.Peek(t0).Remaining, "00:45:00");
        // 「点了开始」到「第一次刷新」之间差几毫秒，显示的仍应是完整时长
        AppendCd(sb, "  隔 0.4 秒刷新（不该少一秒）", c2.Peek(t0 + TimeSpan.FromMilliseconds(400)).Remaining, "00:45:00");
        AppendCd(sb, "  隔 1.6 秒刷新", c2.Peek(t0 + TimeSpan.FromMilliseconds(1600)).Remaining, "00:44:59");

        c2.Pause(t0.AddMinutes(10));
        AppendCdPhase(sb, "22:10 暂停", c2.Peek(t0.AddMinutes(10)).Phase, CountdownPhase.Paused);
        AppendCd(sb, "  暂停时剩余冻结在", c2.Peek(t0.AddMinutes(10)).Remaining, "00:35:00");
        AppendCd(sb, "  暂停 20 分钟后再看，剩余仍是", c2.Peek(t0.AddMinutes(30)).Remaining, "00:35:00");

        c2.Resume(t0.AddMinutes(30));
        AppendCdPhase(sb, "22:30 继续", c2.Peek(t0.AddMinutes(30)).Phase, CountdownPhase.Running);
        AppendCd(sb, "  继续后剩余", c2.Peek(t0.AddMinutes(30)).Remaining, "00:35:00");
        AppendCd(sb, "  继续后到点前一秒", c2.Peek(t0.AddMinutes(64).AddSeconds(59)).Remaining, "00:00:01");
        AppendCdPhase(sb, "  继续后到点", c2.Tick(t0.AddMinutes(65)).Phase, CountdownPhase.Finished);

        // 秒级算例：22:00 起 45m → 截止 22:45:00；22:32:41 暂停，此刻正好剩 12 分 19 秒。
        // 继续时从**当前时间**再推 12 分 19 秒（不是接着 22:45:00 走），所以真实结束时刻
        // 由「暂停了多久」决定。桌面版与安卓版用的是同一组数字，两边结论必须一致。
        sb.AppendLine();
        sb.AppendLine("暂停 / 继续的秒级算例（22:00 起 45m）：");
        var cP = new Countdown();
        Countdown.TryParse("45m", t0, out CountdownPlan pP, out _);
        cP.Start(pP, t0);

        DateTime pauseAt = t0.AddMinutes(32).AddSeconds(41);      // 22:32:41
        AppendCdPhase(sb, "  暂停前仍在走", cP.Peek(pauseAt).Phase, CountdownPhase.Running);
        AppendCd(sb, "  22:32:41 暂停时的剩余", cP.Peek(pauseAt).Remaining, "00:12:19");
        cP.Pause(pauseAt);
        AppendCdPhase(sb, "  22:32:41 点暂停", cP.Peek(pauseAt).Phase, CountdownPhase.Paused);
        AppendCd(sb, "  暂停 15 分钟后再看，剩余仍是", cP.Peek(pauseAt.AddMinutes(15)).Remaining, "00:12:19");

        DateTime resumeAt = pauseAt.AddMinutes(15);              // 22:47:41
        cP.Resume(resumeAt);
        AppendCdPhase(sb, "  22:47:41 点继续", cP.Peek(resumeAt).Phase, CountdownPhase.Running);
        AppendCd(sb, "  继续后剩余（从当前时间往后推）", cP.Peek(resumeAt).Remaining, "00:12:19");

        // 暂停 15 分钟把截止点从 22:45 推到了 23:00，小字里的结束钟点必须跟着改。
        // 改之前 label 还写着「到 22:45 结束」，与真实截止点对不上。
        AppendCd(sb, "  继续后小字里的结束钟点同步到 23:00",
            cP.SubLabel(CountdownPhase.Running), "到 23:00 结束");

        AppendCd(sb, "  继续后 5 分钟", cP.Peek(resumeAt.AddMinutes(5)).Remaining, "00:07:19");
        AppendCd(sb, "  继续后到点前一秒仍在走", cP.Peek(resumeAt.AddMinutes(12).AddSeconds(18)).Remaining, "00:00:01");
        AppendCdPhase(sb, "  继续后正好 23:00:00 到点", cP.Tick(resumeAt.AddMinutes(12).AddSeconds(19)).Phase, CountdownPhase.Finished);

        c2.Restart(t0.AddMinutes(65));
        AppendCd(sb, "时长模式的「再来」重新计满", c2.Peek(t0.AddMinutes(65)).Remaining, "00:45:00");

        c2.Reset();
        AppendCdPhase(sb, "点「重置」", c2.Phase, CountdownPhase.Idle);
        AppendCd(sb, "  重置后大字", Countdown.BigText(CountdownPhase.Idle, TimeSpan.Zero), "--:--:--");
        sb.AppendLine();

        sb.AppendLine("大字与配色：");
        AppendCd(sb, "空闲时大字", Countdown.BigText(CountdownPhase.Idle, TimeSpan.Zero), "--:--:--");
        AppendCd(sb, "运行 45 分钟", Countdown.BigText(CountdownPhase.Running, TimeSpan.FromMinutes(45)), "00:45:00");
        AppendCd(sb, "运行 2 小时 3 分 4 秒", Countdown.BigText(CountdownPhase.Running, new TimeSpan(2, 3, 4)), "02:03:04");
        AppendCd(sb, "到点", Countdown.BigText(CountdownPhase.Finished, TimeSpan.Zero), "时间到！");
        AppendCd(sb, "剩余 6 分钟的颜色", Rgb(Countdown.ColorFor(CountdownPhase.Running, TimeSpan.FromMinutes(6))), "#F2FFFF");
        AppendCd(sb, "剩余 5 分 1 秒仍为白", Rgb(Countdown.ColorFor(CountdownPhase.Running, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1))), "#F2FFFF");
        AppendCd(sb, "剩余 5 分钟整转入琥珀", Rgb(Countdown.ColorFor(CountdownPhase.Running, TimeSpan.FromMinutes(5))), "#EF9F27");
        AppendCd(sb, "剩余 1 分 1 秒仍为琥珀", Rgb(Countdown.ColorFor(CountdownPhase.Running, TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1))), "#EF9F27");
        AppendCd(sb, "剩余 1 分钟整转入红", Rgb(Countdown.ColorFor(CountdownPhase.Running, TimeSpan.FromMinutes(1))), "#FF3B30");
        AppendCd(sb, "到点为红", Rgb(Countdown.ColorFor(CountdownPhase.Finished, TimeSpan.Zero)), "#FF3B30");
        sb.AppendLine();

        sb.AppendLine("落盘 / 还原（关掉程序再打开，倒计时接着走）：");
        var c3 = new Countdown();
        Countdown.TryParse("23:30", t0, out CountdownPlan p3, out _);
        c3.Start(p3, t0);
        var snap = c3.Snapshot();

        var c4 = new Countdown();
        c4.Restore(snap.Mode, snap.ClockTicks, snap.DurationTicks, snap.Deadline, snap.Phase, snap.PausedTicks, t0.AddMinutes(5));
        AppendCdPhase(sb, "22:00 设 23:30，关掉程序 5 分钟后再打开", c4.Peek(t0.AddMinutes(5)).Phase, CountdownPhase.Running);
        AppendCd(sb, "  剩余", c4.Peek(t0.AddMinutes(5)).Remaining, "01:25:00");

        var c5 = new Countdown();
        Countdown.TryParse("45m", t0, out CountdownPlan p5, out _);
        c5.Start(p5, t0);
        var snap2 = c5.Snapshot();

        var c6 = new Countdown();
        c6.Restore(snap2.Mode, snap2.ClockTicks, snap2.DurationTicks, snap2.Deadline, snap2.Phase, snap2.PausedTicks, t0.AddMinutes(50));
        AppendCdPhase(sb, "设 45 分钟、关掉程序 50 分钟后再打开", c6.Peek(t0.AddMinutes(50)).Phase, CountdownPhase.Finished);
        AppendCd(sb, "  剩余", c6.Peek(t0.AddMinutes(50)).Remaining, "00:00:00");

        var c7 = new Countdown();
        Countdown.TryParse("45m", t0, out CountdownPlan p7, out _);
        c7.Start(p7, t0);
        c7.Pause(t0.AddMinutes(10));
        var snap3 = c7.Snapshot();

        var c8 = new Countdown();
        c8.Restore(snap3.Mode, snap3.ClockTicks, snap3.DurationTicks, snap3.Deadline, snap3.Phase, snap3.PausedTicks, t0.AddMinutes(40));
        AppendCdPhase(sb, "暂停状态下关掉程序再打开", c8.Peek(t0.AddMinutes(40)).Phase, CountdownPhase.Paused);
        AppendCd(sb, "  剩余仍冻结在", c8.Peek(t0.AddMinutes(40)).Remaining, "00:35:00");

        var c9 = new Countdown();
        c9.Restore(string.Empty, 0, 0, string.Empty, string.Empty, 0, t0);
        AppendCdPhase(sb, "没设过倒计时（配置里是空字段）", c9.Phase, CountdownPhase.Idle);
        sb.AppendLine();

        // 旧版本允许时刻模式暂停，而「继续」是把截止点改成「现在 + 剩余」，
        // 于是暂停 10 分钟就把 23:30 悄悄推成了 23:40。还原时按钟点校准回 23:30，
        // 把这批被弄歪的历史配置顺手治回原样。
        sb.AppendLine("还原时按钟点校准（治旧版本「暂停把钟点推后」留下的配置）：");
        string shifted = (t0.Date + new TimeSpan(23, 40, 0)).ToString("o", CultureInfo.InvariantCulture);

        var cA = new Countdown();
        cA.Restore("ClockTime", new TimeSpan(23, 30, 0).Ticks, 0, shifted, "Running", 0, t0.AddMinutes(85));
        AppendCdPhase(sb, "  存着 23:40、23:25 打开", cA.Peek(t0.AddMinutes(85)).Phase, CountdownPhase.Running);
        AppendCd(sb, "  剩余按 23:30 算（不是 23:40）", cA.Peek(t0.AddMinutes(85)).Remaining, "00:05:00");

        var cB = new Countdown();
        cB.Restore("ClockTime", new TimeSpan(23, 30, 0).Ticks, 0, shifted, "Running", 0, t0.AddMinutes(95));
        AppendCdPhase(sb, "  23:35 打开：过了 23:30 就该到点", cB.Peek(t0.AddMinutes(95)).Phase, CountdownPhase.Finished);

        var cC = new Countdown();
        cC.Restore("ClockTime", new TimeSpan(23, 30, 0).Ticks, 0, shifted, "Finished", 0, t0.AddDays(1).AddHours(8));
        AppendCdPhase(sb, "  昨晚已到点、次早打开仍是到点", cC.Peek(t0.AddDays(1).AddHours(8)).Phase, CountdownPhase.Finished);
        sb.AppendLine();
    }

    /// <summary>独立复算截止点，用于和 Countdown 内部实现对拍。</summary>
    private static DateTime CdDeadline(TimeSpan clockTarget, DateTime now)
    {
        DateTime today = now.Date + clockTarget;
        return today <= now ? today.AddDays(1) : today;
    }

    /// <summary>
    /// 配置文件本身的健壮性：原子写、坏文件恢复、不丢既有内容。
    ///
    /// 这一节**真的读写磁盘**（用的是 --selftest 自己的隔离文件，不碰用户的 config.json）：
    /// 「写坏文件会不会把配置弄丢」这种事只在真实文件系统上才验得出来，
    /// 拿字符串模拟一遍等于没测。
    /// </summary>
    private static void AppendConfigFileChecks(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## 配置文件健壮性（真的读写磁盘，用隔离文件）");
        sb.AppendLine("验的是「写盘失败 / 文件被写坏」时用户配置还在不在。");
        sb.AppendLine();

        string dir = Path.Combine(Path.GetTempPath(), "TimeGuard-selftest-cfg");
        string file = Path.Combine(dir, "config.json");

        // 记住进来时的隔离文件，检查完要还回去——后面 RestorePosition 等还在用它
        string? configFileBefore = AppConfig.OverrideFileForTest;

        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            sb.AppendLine(Mark(false, "准备临时目录", ex.Message));
            return;
        }

        AppConfig.UseOverrideFile(file);

        try
        {
            // ① 首写：目录不存在也要能建出来
            var first = new AppConfig { Left = 100, Top = 200, Opacity = 0.75 };
            first.Save();

            sb.AppendLine(Mark(File.Exists(file),
                "首次保存会自动建目录并落盘",
                $"文件 {file}，存在={File.Exists(file)}"));

            // ② 覆盖写：既有内容必须完整读回
            var second = new AppConfig { Left = 321, Top = 654, Opacity = 0.5 };
            second.Save();
            AppConfig reread = AppConfig.Load();

            sb.AppendLine(Mark(reread.Left == 321 && reread.Top == 654 && Math.Abs(reread.Opacity - 0.5) < 0.001,
                "覆盖写之后读回来的还是最新内容",
                $"left={reread.Left} top={reread.Top} opacity={reread.Opacity:0.##}"));

            // ③ 覆盖写不应留下临时文件
            string tmp = file + ".tmp";
            sb.AppendLine(Mark(!File.Exists(tmp),
                "保存完成后不留 .tmp 临时文件",
                $"存在={File.Exists(tmp)}"));

            // ④ 覆盖写会留一份 .bak 备份（这是「文件写坏还能救回来」的前提）
            sb.AppendLine(Mark(File.Exists(file + ".bak"),
                "覆盖写会留一份 .bak 备份",
                $"存在={File.Exists(file + ".bak")}"));

            // ⑤ 主文件被写坏（模拟断电写了半截）→ 必须从 .bak 救回来，而不是回落默认值
            //    注意 .bak 里装的是**上一次** Save 的内容（second.Save 把 first 挪成了 .bak），
            //    所以期望值是 left=100 / top=200，不是最新的 321 / 654。
            File.WriteAllText(file, "{\"Left\": 321, \"Top\": ");   // 半截 JSON
            AppConfig.ExerciseRecoveryForTest = true;
            AppConfig rescued = AppConfig.Load();
            AppConfig.ExerciseRecoveryForTest = false;
            bool rescuedRight = rescued.Left == 100 && rescued.Top == 200;

            sb.AppendLine(Mark(rescuedRight,
                "主文件写坏时从 .bak 救回（不是回落默认值）",
                $"救回 left={rescued.Left?.ToString(CultureInfo.InvariantCulture) ?? "null"} "
                    + $"top={rescued.Top?.ToString(CultureInfo.InvariantCulture) ?? "null"}（期望 100 / 200）"));

            File.Delete(file);
            AppConfig.ExerciseRecoveryForTest = true;
            AppConfig missingPrimary = AppConfig.Load();
            sb.AppendLine(Mark(missingPrimary.Left == 100 && missingPrimary.Top == 200, "主配置缺失仍恢复有效备份", $"left={missingPrimary.Left}"));
            missingPrimary.Save();
            File.WriteAllText(file, "broken primary");
            missingPrimary.Save();
            File.Delete(file);
            sb.AppendLine(Mark(AppConfig.Load().Left == 100, "恢复后保存不让损坏主文件覆盖有效备份", "备份仍可恢复"));
            missingPrimary.Save();
            string intact = File.ReadAllText(file);
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                new AppConfig { Left = 999 }.Save();
            sb.AppendLine(Mark(File.ReadAllText(file) == intact, "保存被占用时保留原配置内容", "失败路径不先移走主文件"));
            AppConfig.ExerciseRecoveryForTest = false;

            // ⑥ 坏文件被改名留证，下次启动不会再解析失败一次
            //    要点：⑤ 走的是「主文件坏、.bak 好」的路径，不会隔离；
            //    这里必须把 .bak 也除掉，让 Load 走到「两个都读不出 → 隔离坏文件」那一步。
            //    （实测踩过：不删 .bak 的话，.corrupt 永远不出现，用例假失败。）
            File.WriteAllText(file, "not json at all");
            TryDeleteForTest(file + ".bak");
            AppConfig.ExerciseRecoveryForTest = true;
            AppConfig quarantined = AppConfig.Load();
            AppConfig.ExerciseRecoveryForTest = false;

            sb.AppendLine(Mark(File.Exists(file + ".corrupt"),
                "读不出来的文件被改名留证（.corrupt）",
                $"存在={File.Exists(file + ".corrupt")}"));

            // ⑦ 主文件与备份都坏 → 回落默认，且程序不抛
            File.WriteAllText(file, "not json at all");
            File.WriteAllText(file + ".bak", "also not json");
            AppConfig.ExerciseRecoveryForTest = true;
            AppConfig fallback = AppConfig.Load();
            AppConfig.ExerciseRecoveryForTest = false;

            sb.AppendLine(Mark(Math.Abs(fallback.Opacity - 0.88) < 0.001 && fallback.ClickThrough == false,
                "主文件与备份都坏 → 回落默认值且不抛异常",
                $"opacity={fallback.Opacity:0.##} clickThrough={fallback.ClickThrough}"));

            // ⑧ 「写不进去」不能删掉原文件：把目标占成目录，逼 Save 失败
            string blocked = Path.Combine(dir, "blocked.json");
            Directory.CreateDirectory(blocked);          // 同名目录 → File.Move 必失败
            AppConfig.UseOverrideFile(blocked);

            var doomed = new AppConfig { Left = 7, Top = 8 };
            doomed.Save();                                // 不该抛

            sb.AppendLine(Mark(Directory.Exists(blocked),
                "保存失败时不抛异常、也不破坏既有路径",
                $"目标仍是目录={Directory.Exists(blocked)}"));
        }
        catch (Exception ex)
        {
            sb.AppendLine(Mark(false, "配置文件健壮性检查本身出错", ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            // 恢复成调用方（MainWindow/自检启动）原本设的那个隔离文件
            AppConfig.ExerciseRecoveryForTest = false;   // 别忘了关：留着会让后续 Load 都走恢复分支
            if (configFileBefore is not null) AppConfig.UseOverrideFile(configFileBefore);
            try { Directory.Delete(dir, recursive: true); } catch { /* 清不掉不影响结论 */ }
        }
    }

    /// <summary>自检用：删一个测试用文件，删不掉也不影响结论。</summary>
    private static void TryDeleteForTest(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 无所谓 */ }
    }

    // ===================================================================================
    //  闹钟
    // ===================================================================================

    private static void AppendAlarmChecks(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## 闹钟");
        sb.AppendLine("验的是：排序（以凌晨四点为界）、下次响铃时刻、连锁串的咬合、响过就删、错过不补。");
        sb.AppendLine();

        AppendAlarmSortChecks(sb);
        AppendAlarmNextRingChecks(sb);
        AppendAlarmChainChecks(sb);
        AppendAlarmLifecycleChecks(sb);
        AppendAlarmImminentChecks(sb);
        AppendAlarmRepeatedDaysChecks(sb);
        AppendAlarmWindowChecks(sb);
    }

    /// <summary>
    /// 闹钟设置窗的说明行。看着像"只为一行字"写的，但这一行正是 2026-09-24 出的毛病：
    /// XAML 里 <c>TabNormal IsChecked="True"</c> 会在 InitializeComponent 期间触发 Tab_Checked，
    /// 那时 PageNormal/PageWake 还没建好、会被提前 return —— 结果是**打开窗口时说明行是空的**，
    /// 点了页签才冒出来。这类"首屏与点过之后不一样"的问题不抛异常、不看图发现不了，所以钉住它。
    /// </summary>
    private static void AppendAlarmWindowChecks(StringBuilder sb)
    {
        sb.AppendLine("### 闹钟设置窗的说明行（开窗首屏 / 报错还原）");

        const string normalSubtitle = "每个闹钟一个方块，按时间先后排列（以凌晨四点为界）。";
        const string wakeSubtitle = "自动生成一串互相咬合的闹钟，专治赖床。";

        var win = new AlarmWindow(new AlarmBook(), 30);

        AppendCd(sb, "开窗首屏就有说明行（不是空的）", win.SubtitleForTest.Length > 0 ? "有" : "空（错）", "有");
        AppendCd(sb, "首屏说明行 = 普通闹钟那一句", win.SubtitleForTest, normalSubtitle);

        // 普通页的报错是顶在说明行上的（两个页签共用一个提示位置）
        win.AddNormalForTest("25:99", string.Empty, 0, false, Array.Empty<int>());
        AppendCd(sb, "非法时间 → 说明行被顶成错误提示",
            win.SubtitleForTest == normalSubtitle ? "没被顶掉（错）" : "被顶掉（符合预期）", "被顶掉（符合预期）");

        // 再加一个合法的：清错时必须把说明**文字**也还回来（原先只还了颜色，说明行会一直挂着错话）
        bool added = win.AddNormalForTest("07:00", "测试", 0, false, Array.Empty<int>());
        AppendCd(sb, "合法时间被接受", added ? "接受" : "拒绝（错）", "接受");
        AppendCd(sb, "清错后说明行还原成普通闹钟那句", win.SubtitleForTest, normalSubtitle);

        win.SelectWakeTabForTest();
        AppendCd(sb, "切到起床页 → 说明行换成连锁那句", win.SubtitleForTest, wakeSubtitle);

        // 起床页报错后切回普通页，说明行不能还挂着起床页的错话
        win.AddNormalForTest("25:99", string.Empty, 0, false, Array.Empty<int>());
        AppendCd(sb, "起床页里报错也不污染说明行（提示落在连锁预览那行）",
            win.SubtitleForTest, wakeSubtitle);

        // 窗口宽度只能有一个来源（各自的 CardWidth，XAML 里用 x:Static 指过来）。
        // ⚠️ 这一条**截图看不出来**：Snapshot.TakePanel 会把宽度强行设成 CardWidth，
        //    所以就算 XAML 里偷偷写死一个不一样的数字，图照样是对的 —— 只能直接读窗口属性。
        var ring = new RingWindow();
        AppendCd(sb, "响铃弹窗宽度 = RingWindow.CardWidth（XAML 没另写一份）",
            ring.Width.ToString("0.#", CultureInfo.InvariantCulture) + " / "
                + RingWindow.CardWidth.ToString("0.#", CultureInfo.InvariantCulture),
            "320 / 320");
        AppendCd(sb, "闹钟设置窗宽度 = AlarmWindow.CardWidth（XAML 没另写一份）",
            win.Width.ToString("0.#", CultureInfo.InvariantCulture) + " / "
                + AlarmWindow.CardWidth.ToString("0.#", CultureInfo.InvariantCulture),
            "476 / 476");

        sb.AppendLine();
    }

    /// <summary>排序：以凌晨四点为界 —— 1:00/2:00/3:00 排在 23:00 之后。</summary>
    private static void AppendAlarmSortChecks(StringBuilder sb)
    {
        sb.AppendLine("### 排序（凌晨四点为界）");

        CheckNum(sb, "04:00 是最小值（新一天的起点）", new Alarm { TimeOfDay = new TimeSpan(4, 0, 0) }.SortKey, 0);
        CheckNum(sb, "05:00 折算成 60 分钟", new Alarm { TimeOfDay = new TimeSpan(5, 0, 0) }.SortKey, 60);
        CheckNum(sb, "23:00 折算成 1140 分钟", new Alarm { TimeOfDay = new TimeSpan(23, 0, 0) }.SortKey, 1140);
        CheckNum(sb, "01:00 折算成 1260（排在 23:00 之后）", new Alarm { TimeOfDay = new TimeSpan(1, 0, 0) }.SortKey, 1260);
        CheckNum(sb, "03:59 折算成 1439（这一天里最后一个）", new Alarm { TimeOfDay = new TimeSpan(3, 59, 0) }.SortKey, 1439);

        var book = new AlarmBook();
        string[] times = { "03:00", "23:00", "01:00", "04:00", "20:30", "02:00", "22:00" };
        foreach (string t in times)
        {
            SleepRules.TryParseHm(t, out TimeSpan ts);
            book.AddManual(ts, string.Empty, AlarmRepeat.Daily, null, false);
        }

        string order = string.Join(" ", book.Sorted().Select(a => a.TimeText));
        AppendCd(sb, "一串乱序输入排好后的样子", order, "04:00 20:30 22:00 23:00 01:00 02:00 03:00");

        // 四点整本身算新一天的开始，不该跑到末尾去
        var b2 = new AlarmBook();
        SleepRules.TryParseHm("03:59", out TimeSpan t1);
        SleepRules.TryParseHm("04:00", out TimeSpan t2);
        b2.AddManual(t1, string.Empty, AlarmRepeat.Daily, null, false);
        b2.AddManual(t2, string.Empty, AlarmRepeat.Daily, null, false);
        AppendCd(sb, "04:00 排在 03:59 前面（四点即分界）",
            string.Join(" ", b2.Sorted().Select(a => a.TimeText)), "04:00 03:59");

        sb.AppendLine();
    }

    /// <summary>下次响铃时刻：跨零点、重复方式、关掉的不参与。</summary>
    private static void AppendAlarmNextRingChecks(StringBuilder sb)
    {
        sb.AppendLine("### 下次响铃时刻");

        var now = new DateTime(2026, 9, 23, 20, 0, 0);   // 周三晚八点

        var daily = new Alarm { TimeOfDay = new TimeSpan(6, 30, 0), Repeat = AlarmRepeat.Daily, Enabled = true };
        AppendCd(sb, "每晚八点看「每天 06:30」", Format(daily.NextRingAfter(now)), "2026-09-24 06:30");

        var later = new Alarm { TimeOfDay = new TimeSpan(22, 0, 0), Repeat = AlarmRepeat.Daily, Enabled = true };
        AppendCd(sb, "每晚八点看「每天 22:00」（今晚还没到）", Format(later.NextRingAfter(now)), "2026-09-23 22:00");

        // 01:30 在四点分界里属于"这一夜"，从周三晚八点算，下一次是周四 01:30
        var earlyMorning = new Alarm { TimeOfDay = new TimeSpan(1, 30, 0), Repeat = AlarmRepeat.Daily, Enabled = true };
        AppendCd(sb, "每晚八点看「每天 01:30」", Format(earlyMorning.NextRingAfter(now)), "2026-09-24 01:30");

        var once = new Alarm { TimeOfDay = new TimeSpan(7, 0, 0), Repeat = AlarmRepeat.Once, Enabled = true };
        AppendCd(sb, "一次性 07:00 的下一次", Format(once.NextRingAfter(now)), "2026-09-24 07:00");

        var off = new Alarm { TimeOfDay = new TimeSpan(7, 0, 0), Repeat = AlarmRepeat.Daily, Enabled = false };
        AppendCd(sb, "关掉的闹钟没有下一次", off.NextRingAfter(now) is null ? "null" : "有不该有", "null");

        // Weekly：只在周一、周三响，周三晚八点之后的下一次应是下周一
        var weekly = new Alarm
        {
            TimeOfDay = new TimeSpan(7, 0, 0),
            Repeat = AlarmRepeat.Weekly,
            Enabled = true,
            Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday }
        };
        AppendCd(sb, "每周一/周三 07:00，周三晚八点之后", Format(weekly.NextRingAfter(now)), "2026-09-28 07:00");

        AppendCd(sb, "还有多久的说明（3 小时 20 分）",
            Alarm.DescribeCountdown(new DateTime(2026, 9, 23, 20, 0, 0), new DateTime(2026, 9, 23, 23, 20, 0)),
            "3 小时 20 分后");

        sb.AppendLine();
    }

    /// <summary>连锁起床闹钟：生成、咬合、整串关闭。</summary>
    private static void AppendAlarmChainChecks(StringBuilder sb)
    {
        sb.AppendLine("### 连锁起床闹钟");

        var spec = new AlarmChainSpec
        {
            StartAt = new TimeSpan(8, 0, 0),
            IntervalMinutes = 3,
            Count = 3,
            Daily = true
        };
        AppendCd(sb, "起始 08:00 + 间隔 3 分 + 3 次 → 参数合法", spec.IsValid(out _) ? "合法" : "非法", "合法");
        AppendCd(sb, "算出来的最后一个时刻", SleepRules.FormatHm(spec.LastAt), "08:06");

        var book = new AlarmBook();
        List<Alarm> made = book.AddChain(spec, "起床");
        AppendCd(sb, "生成了几个", made.Count.ToString(CultureInfo.InvariantCulture), "3");
        AppendCd(sb, "三个的时刻", string.Join(" ", made.Select(a => a.TimeText)), "08:00 08:03 08:06");
        AppendCd(sb, "三个共用一个 ChainId", made.All(a => a.ChainId == made[0].ChainId) ? "是" : "否", "是");
        AppendCd(sb, "序号 1/2/3", string.Join("/", made.Select(a => a.ChainIndex)), "1/2/3");

        // 关键语义：关掉第一个，第二个照常响，且响的时刻**不因关闭而漂移**
        var t0 = new DateTime(2026, 9, 23, 7, 55, 0);      // 差 5 分钟到 08:00
        List<AlarmFiring> due = book.DueBetween(t0, new DateTime(2026, 9, 23, 8, 0, 5));
        AppendCd(sb, "08:00 到点时该响几个", due.Count.ToString(CultureInfo.InvariantCulture), "1");
        AppendCd(sb, "响的是哪一个", due[0].Alarm.TimeText, "08:00");

        string msg1 = book.Dismiss(due[0].Alarm, new DateTime(2026, 9, 23, 8, 0, 10));
        AppendCd(sb, "关掉 08:00 之后的提示语", msg1, "已关闭，还有 2 个会继续响。");

        // 再往前走到 08:03 —— 必须响，且时刻就是 08:03（不是"关掉时刻 + 3 分"）
        List<AlarmFiring> due2 = book.DueBetween(new DateTime(2026, 9, 23, 8, 0, 5), new DateTime(2026, 9, 23, 8, 3, 5));
        AppendCd(sb, "08:03 该响了（关掉第一个不影响第二个）", due2.Count.ToString(CultureInfo.InvariantCulture), "1");
        AppendCd(sb, "第二个响的时刻仍是 08:03（没漂移）", due2.Count > 0 ? due2[0].Alarm.TimeText : "-", "08:03");
        AppendCd(sb, "这还不是最后一个", due2.Count > 0 && due2[0].IsLastInChain ? "是最后一个（错）" : "不是最后一个", "不是最后一个");

        string msg2 = book.Dismiss(due2[0].Alarm, new DateTime(2026, 9, 23, 8, 3, 10));
        AppendCd(sb, "关掉 08:03 之后的提示语", msg2, "已关闭，还有 1 个会继续响。");

        List<AlarmFiring> due3 = book.DueBetween(new DateTime(2026, 9, 23, 8, 3, 5), new DateTime(2026, 9, 23, 8, 6, 5));
        AppendCd(sb, "08:06 是最后一个", due3.Count > 0 && due3[0].IsLastInChain ? "是" : "不是", "是");

        string msg3 = book.Dismiss(due3[0].Alarm, new DateTime(2026, 9, 23, 8, 6, 10));
        AppendCd(sb, "关掉最后一个之后的提示语", msg3, "这是最后一个，这一串起床闹钟已结束。");
        CheckNum(sb, "重复的串关完后仍在（不会删）", book.Count, 3);

        // 逐个关掉 08:00 与 08:03，但不等 08:06 —— 此刻还在响的是 08:00
        var book2 = new AlarmBook();
        List<Alarm> made2 = book2.AddChain(spec, "起床");
        string whole = book2.DismissWholeChain(made2[0], new DateTime(2026, 9, 23, 8, 0, 10));
        AppendCd(sb, "手动关掉整串的提示语", whole, "整串 3 个起床闹钟已全部关闭。");
        AppendCd(sb, "整串关掉后没有一个还生效",
            book2.Items.Any(a => a.Enabled) ? "还有生效的（错）" : "全部不生效", "全部不生效");
        CheckNum(sb, "整串关掉后不删除（重复的串留着）", book2.Count, 3);

        // 一次性串：关完最后一个就整串删掉
        var book3 = new AlarmBook();
        List<Alarm> made3 = book3.AddChain(new AlarmChainSpec
        {
            StartAt = new TimeSpan(8, 0, 0),
            IntervalMinutes = 3,
            Count = 3,
            Daily = false
        }, "起床");
        book3.Dismiss(made3[0], new DateTime(2026, 9, 23, 8, 0, 10));
        book3.Dismiss(made3[1], new DateTime(2026, 9, 23, 8, 3, 10));
        book3.Dismiss(made3[2], new DateTime(2026, 9, 23, 8, 6, 10));
        CheckNum(sb, "一次性串关完最后一个后整串删除", book3.Count, 0);

        // 工作日串
        var book4 = new AlarmBook();
        List<Alarm> made4 = book4.AddChain(new AlarmChainSpec
        {
            StartAt = new TimeSpan(6, 30, 0),
            IntervalMinutes = 5,
            Count = 3,
            Daily = true,
            WeekdaysOnly = true
        }, "起床");
        AppendCd(sb, "工作日串的重复说明", made4[0].RepeatText, "周一、周二、周三、周四、周五");

        // 跨零点：22:00 起、间隔 90 分、3 次 → 23:30、次日 01:00
        var crossSpec = new AlarmChainSpec
        {
            StartAt = new TimeSpan(22, 0, 0),
            IntervalMinutes = 90,
            Count = 3,
            Daily = true
        };
        var book5 = new AlarmBook();
        List<Alarm> made5 = book5.AddChain(crossSpec, "夜");
        AppendCd(sb, "跨零点一串的时刻", string.Join(" ", made5.Select(a => a.TimeText)), "22:00 23:30 01:00");
        AppendCd(sb, "跨零点后的排序（01:00 在最后）",
            string.Join(" ", book5.Sorted().Select(a => a.TimeText)), "22:00 23:30 01:00");

        // 参数越界的拒绝
        AppendCd(sb, "响铃次数 0 被拒", new AlarmChainSpec { Count = 0 }.IsValid(out _) ? "接受（错）" : "拒绝", "拒绝");
        AppendCd(sb, "响铃次数 13 被拒", new AlarmChainSpec { Count = 13 }.IsValid(out _) ? "接受（错）" : "拒绝", "拒绝");
        AppendCd(sb, "间隔 0 分钟被拒", new AlarmChainSpec { IntervalMinutes = 0 }.IsValid(out _) ? "接受（错）" : "拒绝", "拒绝");
        AppendCd(sb, "间隔 121 分钟被拒", new AlarmChainSpec { IntervalMinutes = 121 }.IsValid(out _) ? "接受（错）" : "拒绝", "拒绝");
        AppendCd(sb, "间隔 1 分钟可接受", new AlarmChainSpec { IntervalMinutes = 1 }.IsValid(out _) ? "接受" : "拒绝（错）", "接受");

        // 参数回显
        AlarmChainSpec? inferred = book5.InferChainSpec(made5[0].ChainId);
        AppendCd(sb, "从已有串反推间隔", inferred is null ? "null" : inferred.IntervalMinutes.ToString(CultureInfo.InvariantCulture), "90");
        AppendCd(sb, "从已有串反推次数", inferred is null ? "null" : inferred.Count.ToString(CultureInfo.InvariantCulture), "3");

        sb.AppendLine();
    }

    /// <summary>生命周期：响过就删、稍后提醒、错过不补。</summary>
    private static void AppendAlarmLifecycleChecks(StringBuilder sb)
    {
        sb.AppendLine("### 响过就删 / 稍后提醒 / 错过不补");

        // 一次性 + 响过即删
        var b1 = new AlarmBook();
        b1.AddManual(new TimeSpan(7, 0, 0), "吃药", AlarmRepeat.Once, null, deleteAfterRing: true);
        List<AlarmFiring> f1 = b1.DueBetween(new DateTime(2026, 9, 23, 6, 59, 0), new DateTime(2026, 9, 23, 7, 0, 5));
        string m1 = f1.Count > 0 ? b1.Dismiss(f1[0].Alarm, new DateTime(2026, 9, 23, 7, 0, 10)) : "（没响）";
        AppendCd(sb, "一次性闹钟关掉后的提示语", m1, "已删除这个一次性闹钟。");
        CheckNum(sb, "删完之后列表为空", b1.Count, 0);

        // 一次性但用户选择保留
        var b2 = new AlarmBook();
        b2.AddManual(new TimeSpan(7, 0, 0), "吃药", AlarmRepeat.Once, null, deleteAfterRing: false);
        List<AlarmFiring> f2 = b2.DueBetween(new DateTime(2026, 9, 23, 6, 59, 0), new DateTime(2026, 9, 23, 7, 0, 5));
        string m2 = f2.Count > 0 ? b2.Dismiss(f2[0].Alarm, new DateTime(2026, 9, 23, 7, 0, 10)) : "（没响）";
        AppendCd(sb, "一次性但选择保留的提示语", m2, "已关闭，下次仍会响。");
        CheckNum(sb, "保留模式不删除", b2.Count, 1);

        // 每天重复的关掉后必须保留
        var b3 = new AlarmBook();
        b3.AddManual(new TimeSpan(7, 0, 0), "起床", AlarmRepeat.Daily, null, deleteAfterRing: true);
        List<AlarmFiring> f3 = b3.DueBetween(new DateTime(2026, 9, 23, 6, 59, 0), new DateTime(2026, 9, 23, 7, 0, 5));
        b3.Dismiss(f3[0].Alarm, new DateTime(2026, 9, 23, 7, 0, 10));
        CheckNum(sb, "每天重复的关掉后仍保留", b3.Count, 1);

        // 稍后提醒
        var b4 = new AlarmBook();
        Alarm baseAlarm = b4.AddManual(new TimeSpan(7, 0, 0), "起床", AlarmRepeat.Once, null, true);
        string m4 = b4.Snooze(baseAlarm, new DateTime(2026, 9, 23, 7, 0, 10), 5);
        AppendCd(sb, "稍后提醒 5 分钟的提示语", m4, "5 分钟后（07:05）再响一次。");
        CheckNum(sb, "稍后提醒后多了一个闹钟", b4.Count, 2);

        // 错过不补：程序关了一夜，早上开机
        var b5 = new AlarmBook();
        b5.AddManual(new TimeSpan(2, 0, 0), "半夜", AlarmRepeat.Daily, null, false);
        List<AlarmFiring> f5 = b5.DueBetween(new DateTime(2026, 9, 23, 8, 0, 0), new DateTime(2026, 9, 23, 8, 0, 1));
        CheckNum(sb, "关机一夜后开机，不补响（返回 0 个）", f5.Count, 0);

        // 但在宽容窗口内（刚唤醒那一下）还算数
        var b6 = new AlarmBook();
        b6.AddManual(new TimeSpan(8, 0, 0), "刚过", AlarmRepeat.Daily, null, false);
        List<AlarmFiring> f6 = b6.DueBetween(new DateTime(2026, 9, 23, 8, 0, 30), new DateTime(2026, 9, 23, 8, 0, 31));
        CheckNum(sb, "错过 30 秒内仍算（宽容窗口 90 秒）", f6.Count, 1);

        // 同一个时刻不会被报两次（200ms 刷新循环）
        var b7 = new AlarmBook();
        b7.AddManual(new TimeSpan(8, 0, 0), "x", AlarmRepeat.Daily, null, false);
        List<AlarmFiring> g1 = b7.DueBetween(new DateTime(2026, 9, 23, 7, 59, 59), new DateTime(2026, 9, 23, 8, 0, 1));
        List<AlarmFiring> g2 = b7.DueBetween(new DateTime(2026, 9, 23, 8, 0, 0), new DateTime(2026, 9, 23, 8, 0, 1));
        CheckNum(sb, "同一时刻第一次报出", g1.Count, 1);
        CheckNum(sb, "紧接着再刷一次不重复报", g2.Count, 0);

        sb.AppendLine();
    }

    /// <summary>「每天」的闹钟第二天必须还响 —— 这是曾经写错的地方，留一条专门的回归。</summary>
    private static void AppendAlarmRepeatedDaysChecks(StringBuilder sb)
    {
        sb.AppendLine("### 重复闹钟跨天（回归：曾经响过就不再响）");

        // 这是早期实现的真实 bug：`_firedAt.ContainsKey(Id)` 让闹钟响过一次就报废。
        // 当时只测"今天响不响"是发现不了的，必须连着跑两天。
        var b = new AlarmBook();
        b.AddManual(new TimeSpan(7, 0, 0), "起床", AlarmRepeat.Daily, null, false);

        List<AlarmFiring> day1 = b.DueBetween(new DateTime(2026, 9, 23, 6, 59, 0), new DateTime(2026, 9, 23, 7, 0, 5));
        CheckNum(sb, "第一天 07:00 响了", day1.Count, 1);
        if (day1.Count > 0) b.Dismiss(day1[0].Alarm, new DateTime(2026, 9, 23, 7, 0, 10));

        List<AlarmFiring> day2 = b.DueBetween(new DateTime(2026, 9, 24, 6, 59, 0), new DateTime(2026, 9, 24, 7, 0, 5));
        CheckNum(sb, "第二天 07:00 还得响（每天重复）", day2.Count, 1);

        if (day2.Count > 0) b.Dismiss(day2[0].Alarm, new DateTime(2026, 9, 24, 7, 0, 10));
        List<AlarmFiring> day3 = b.DueBetween(new DateTime(2026, 9, 25, 6, 59, 0), new DateTime(2026, 9, 25, 7, 0, 5));
        CheckNum(sb, "第三天也响", day3.Count, 1);
        CheckNum(sb, "三天之后这个闹钟还在", b.Count, 1);

        // 每周：只在周一响，周二不该响
        var w = new AlarmBook();
        w.AddManual(new TimeSpan(7, 0, 0), "周一", AlarmRepeat.Weekly,
                    new[] { DayOfWeek.Monday }, false);
        List<AlarmFiring> mon = w.DueBetween(new DateTime(2026, 9, 28, 6, 59, 0), new DateTime(2026, 9, 28, 7, 0, 5));
        CheckNum(sb, "周一 07:00 响了（2026-09-28 是周一）", mon.Count, 1);
        if (mon.Count > 0) w.Dismiss(mon[0].Alarm, new DateTime(2026, 9, 28, 7, 0, 10));
        List<AlarmFiring> tue = w.DueBetween(new DateTime(2026, 9, 29, 6, 59, 0), new DateTime(2026, 9, 29, 7, 0, 5));
        CheckNum(sb, "周二 07:00 不响（只勾了周一）", tue.Count, 0);
        List<AlarmFiring> mon2 = w.DueBetween(new DateTime(2026, 10, 5, 6, 59, 0), new DateTime(2026, 10, 5, 7, 0, 5));
        CheckNum(sb, "下周一还响", mon2.Count, 1);

        sb.AppendLine();
    }

    /// <summary>半小时内要响的闹钟 —— 挂件据此决定要不要显示闹钟块，手机端据此决定要不要进通知栏。</summary>
    private static void AppendAlarmImminentChecks(StringBuilder sb)
    {
        sb.AppendLine("### 半小时内要响的（挂件显示 / 通知栏的判据）");

        var now = new DateTime(2026, 9, 23, 8, 0, 0);
        var b = new AlarmBook();
        b.AddManual(new TimeSpan(8, 10, 0), "十分钟后", AlarmRepeat.Once, null, true);   // 10 分 → 算
        b.AddManual(new TimeSpan(8, 30, 0), "半小时整", AlarmRepeat.Once, null, true);   // 30 分 → 算（含）
        b.AddManual(new TimeSpan(8, 31, 0), "半小时零一分", AlarmRepeat.Once, null, true); // 31 分 → 不算
        b.AddManual(new TimeSpan(9, 0, 0), "一小时后", AlarmRepeat.Once, null, true);    // 60 分 → 不算

        List<Alarm> im = b.Imminent(now);
        AppendCd(sb, "半小时内的闹钟", string.Join(" ", im.Select(a => a.TimeText)), "08:10 08:30");
        AppendCd(sb, "最早的那次", Format(b.NextImminentRing(now)), "2026-09-23 08:10");

        var b2 = new AlarmBook();
        b2.AddManual(new TimeSpan(9, 0, 0), "很久以后", AlarmRepeat.Once, null, true);
        CheckNum(sb, "半小时内没有 → 返回 0 个（挂件不显示闹钟块）", b2.Imminent(now).Count, 0);
        AppendCd(sb, "没有临近闹钟时下一次为 null", b2.NextImminentRing(now) is null ? "null" : "非 null", "null");

        var b3 = new AlarmBook();
        Alarm offAlarm = b3.AddManual(new TimeSpan(8, 10, 0), "关掉的", AlarmRepeat.Once, null, true);
        offAlarm.Enabled = false;
        CheckNum(sb, "关掉的闹钟不算临近", b3.Imminent(now).Count, 0);

        sb.AppendLine();
    }

    /// <summary>自检用：把一个 DateTime 格式成稳定的文本，避免受区域设置影响。</summary>
    private static string Format(DateTime? t)
        => t is DateTime d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "null";

    private static void CheckNum(StringBuilder sb, string label, int actual, int expect)
    {
        bool ok = actual == expect;
        sb.AppendLine((ok ? "PASS  " : "FAIL  ") + label + " → " + actual
                      + (ok ? "" : "（期望：" + expect + "）"));
    }

    private static void AppendCd(StringBuilder sb, string label, string actual, string expect)    {
        bool ok = string.Equals(actual, expect, StringComparison.Ordinal);
        sb.AppendLine((ok ? "PASS  " : "FAIL  ") + label + " → " + actual + (ok ? "" : "（期望：" + expect + "）"));
    }

    private static void AppendCd(StringBuilder sb, string label, TimeSpan actual, string expect)
        => AppendCd(sb, label, Countdown.Hms(actual), expect);

    private static void AppendCdPhase(StringBuilder sb, string label, CountdownPhase actual, CountdownPhase expect)
    {
        bool ok = actual == expect;
        sb.AppendLine((ok ? "PASS  " : "FAIL  ") + label + " → " + actual + (ok ? "" : "（期望：" + expect + "）"));
    }

    private static string Rgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void CheckText(StringBuilder sb, string label, string actual, string expect)
    {
        bool ok = string.Equals(actual, expect, StringComparison.Ordinal);
        sb.AppendLine((ok ? "PASS  " : "FAIL  ") + label + " → " + actual + (ok ? "" : "（期望：" + expect + "）"));
    }

    private static bool IsDefault(SleepRules r) =>
        r.ShowAt == SleepRules.Default.ShowAt
        && r.DeductAt == SleepRules.Default.DeductAt
        && r.HideAt == SleepRules.Default.HideAt
        && r.MinutesPerPoint == SleepRules.Default.MinutesPerPoint
        && r.PointsPerStep == SleepRules.Default.PointsPerStep
        && r.MaxScore == SleepRules.Default.MaxScore;

    private static string Phase(NightPhase p) => p switch
    {
        NightPhase.Daytime => "隐藏",
        NightPhase.Countdown => "满分待命",
        NightPhase.Deducting => "扣分中",
        NightPhase.Exhausted => "已扣光",
        _ => "?"
    };

    private static void AppendCheckpoint(StringBuilder sb, DateTime t, string desc, int? expectScore, NightPhase expectPhase, SleepRules r)
    {
        ScoreState s = ScoreEngine.Evaluate(t, r);
        bool okScore = expectScore is null || s.Score == expectScore.Value;
        bool okPhase = s.Phase == expectPhase;
        bool ok = okScore && okPhase;

        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0}  {1:MM-dd HH:mm:ss}  {2}   →  分数={3}  阶段={4}  （期望 分数={5} 阶段={6}）",
            ok ? "PASS" : "FAIL", t, desc,
            s.Phase == NightPhase.Daytime ? "-" : s.Score.ToString(CultureInfo.InvariantCulture),
            Phase(s.Phase),
            expectScore?.ToString(CultureInfo.InvariantCulture) ?? "-",
            Phase(expectPhase)));
    }

    // ---- 分辨率适配 ----

    /// <summary>
    /// 分辨率 / 缩放适配的用例。
    ///
    /// 全部是**算出来的**规格：系统缩放决定 DIP 工作区，程序只按 DIP 摆放，
    /// 所以要证的不是「4K 上好看」，而是「任何 DIP 工作区下窗口都整窗可见」。
    /// 这样手上没有 4K 屏也能验，不必靠「去借台机器看看」。
    /// </summary>
    private static void AppendPlacementChecks(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## 窗口摆放（分辨率 / 缩放适配）");
        sb.AppendLine("物理分辨率 ÷ 系统缩放 = DIP 工作区；程序按 DIP 摆放，故不为分辨率写任何分支。");
        sb.AppendLine("下面用四组常见规格断言「整窗始终可见」，不需要真实 4K 屏。");
        sb.AppendLine();

        // 宽度只有 MainWindow.CardWidth 一个来源（XAML 里也是 x:Static 指过来）——
        // 以前这里、XAML、Snapshot 各写一份 183，那正是「宽度悄悄漂走」的温床。
        const double WinW = MainWindow.CardWidth;
        const double WinH = WindowPlacement.FallbackHeight;   // 高度未定时的估值
        const double Eps = 0.01;

        (string Name, double W, double H)[] screens =
        {
            WindowPlacement.Fhd100,
            WindowPlacement.Qhd100,
            WindowPlacement.Uhd150,
            WindowPlacement.Uhd200,
        };

        // ① 默认位置：贴右上角，且本身就在界内（不需要被夹）
        foreach ((string name, double w, double h) in screens)
        {
            (double dl, double dt) = WindowPlacement.DefaultPosition(w, 0, WinW);
            (double cl, double ct) = WindowPlacement.Clamp(dl, dt, WinW, WinH, 0, 0, w, h);

            bool marginOk = Math.Abs(w - (dl + WinW) - WindowPlacement.EdgeMargin) < Eps
                            && Math.Abs(dt - WindowPlacement.EdgeMargin) < Eps;
            bool alreadyInside = Math.Abs(cl - dl) < Eps && Math.Abs(ct - dt) < Eps;

            sb.AppendLine(Mark(marginOk && alreadyInside,
                "默认位置贴右上角 · " + name,
                $"left={F(dl)} top={F(dt)}（右边距 {F(w - dl - WinW)}，上边距 {F(dt)}）"));
        }

        // ② 这条是「4K@200% 与 1080P@100% 能共用一份配置」的依据
        {
            (double lFhd, double tFhd) = WindowPlacement.DefaultPosition(WindowPlacement.Fhd100.Width, 0, WinW);
            (double lUhd, double tUhd) = WindowPlacement.DefaultPosition(WindowPlacement.Uhd200.Width, 0, WinW);

            sb.AppendLine(Mark(Math.Abs(lFhd - lUhd) < Eps && Math.Abs(tFhd - tUhd) < Eps,
                "1080P@100% 与 4K@200% 算出同一个默认位置",
                $"1080P → ({F(lFhd)}, {F(tFhd)})；4K@200% → ({F(lUhd)}, {F(tUhd)})"));
        }

        // ③ 越界纠正：把「在更宽屏幕上保存的位置」拿到小屏上用
        {
            double savedLeft = 2500;   // 例：4K@150% 下贴着右边保存下来的坐标
            (double l, double t) = WindowPlacement.Clamp(savedLeft, 40, WinW, WinH,
                0, 0, WindowPlacement.Fhd100.Width, WindowPlacement.Fhd100.Height);

            double maxLeft = WindowPlacement.Fhd100.Width - WinW;
            sb.AppendLine(Mark(l >= -Eps && l <= maxLeft + Eps,
                "换到小屏后位置被夹回界内",
                $"保存 left={F(savedLeft)} → 实际 left={F(l)}（上限 {F(maxLeft)}），top={F(t)}"));
        }

        // ④ 双屏拼接：副屏在主屏左侧时坐标是负的，位置必须原样保留
        {
            (double l, double t) = WindowPlacement.Clamp(-1000, 60, WinW, WinH, -1920, 0, 3840, 1080);

            sb.AppendLine(Mark(Math.Abs(l + 1000) < Eps && Math.Abs(t - 60) < Eps,
                "双屏（副屏在左侧负坐标）位置原样保留",
                $"left=-1000 → {F(l)}；top=60 → {F(t)}"));
        }

        // ⑤ 副屏被拔掉：原先落在副屏上的窗口必须被拉回主屏
        {
            (double l, double t) = WindowPlacement.Clamp(-1000, 60, WinW, WinH, 0, 0, 1920, 1080);

            sb.AppendLine(Mark(l >= -Eps && l <= 1920 - WinW + Eps,
                "副屏拔掉后窗口被拉回主屏",
                $"原 left=-1000 → {F(l)}，top={F(t)}"));
        }

        // ⑥ 极端：屏幕比窗口还窄时退化成贴左上角，而不是算出负坐标
        {
            (double l, double t) = WindowPlacement.Clamp(500, 500, WinW, WinH, 0, 0, 100, 100);

            sb.AppendLine(Mark(Math.Abs(l) < Eps && Math.Abs(t) < Eps,
                "屏幕比窗口还小 → 贴左上角而非负坐标",
                $"left={F(l)} top={F(t)}"));
        }

        // ⑦ SizeToContent="Height" 的 NaN 必须先换成可用值，否则整个夹取链都会算出 NaN
        {
            double h = WindowPlacement.UsableHeight(double.NaN);
            (double l, double t) = WindowPlacement.Clamp(100, 100, WinW, h, 0, 0, 1920, 1080);

            sb.AppendLine(Mark(!double.IsNaN(h) && h > 0 && !double.IsNaN(l) && !double.IsNaN(t),
                "高度为 NaN（SizeToContent）时夹取仍得到有效坐标",
                $"估值高 {F(h)}；left={F(l)} top={F(t)}"));
        }

        // ---- ⑧ 及以后：多屏按「最近那块屏」夹取 ----
        // 这是 P2-7 的回归：老算法把虚拟桌面当成一整块矩形，两块并排的屏中间那条缝
        // 在物理上并不存在，窗口可以停在缝里（跨在两块屏上，哪块上都摆不正）。
        sb.AppendLine();
        sb.AppendLine("多屏：先挑离窗口最近的那块屏，再在那块屏的工作区内夹取（缝不是合法位置）。");

        // 两块 1920×1080 并排：主屏 [0,1920)，副屏 [1920,3840)
        var dual = new[]
        {
            new WindowPlacement.ScreenRect(0, 0, 1920, 1080),
            new WindowPlacement.ScreenRect(1920, 0, 1920, 1080),
        };

        // ⑧ 老算法在这种输入下会把窗口留在缝里，新算法必须把它拉回某一块屏内
        {
            const double SeamLeft = 1850;   // 主屏外 70px、副屏外 70px —— 正落在缝上
            (double l, double t) = WindowPlacement.ClampToNearest(SeamLeft, 40, WinW, WinH, dual);

            bool inMain = l >= -Eps && l + WinW <= 1920 + Eps;
            bool inSecond = l >= 1920 - Eps && l + WinW <= 3840 + Eps;

            sb.AppendLine(Mark((inMain || inSecond) && l > SeamLeft,
                "落在两屏之间缝隙上的窗口被拉回某一块屏内",
                $"保存 left={F(SeamLeft)}（缝）→ 实际 left={F(l)}（{(inMain ? "主屏" : "副屏")}内），top={F(t)}"));
        }

        // ⑨ 副屏上的窗口（坐标 2000+）必须留在副屏，不能被拽回主屏 —— 挑屏不能只认主屏
        {
            (double l, double t) = WindowPlacement.ClampToNearest(2500, 40, WinW, WinH, dual);

            sb.AppendLine(Mark(Math.Abs(l - 2500) < Eps && Math.Abs(t - 40) < Eps,
                "副屏上摆好的窗口原样保留（不被拽回主屏）",
                $"left=2500 → {F(l)}；top=40 → {F(t)}"));
        }

        // ⑩ 副屏在主屏左侧（负坐标）：与 ④ 同口径，按屏夹取后仍要原样保留
        {
            var negative = new[]
            {
                new WindowPlacement.ScreenRect(0, 0, 1920, 1080),
                new WindowPlacement.ScreenRect(-1920, 0, 1920, 1080),
            };
            (double l, double t) = WindowPlacement.ClampToNearest(-1000, 60, WinW, WinH, negative);

            sb.AppendLine(Mark(Math.Abs(l + 1000) < Eps && Math.Abs(t - 60) < Eps,
                "副屏在左侧（负坐标）时位置原样保留",
                $"left=-1000 → {F(l)}；top=60 → {F(t)}"));
        }

        // ⑪ 副屏被拔掉：只剩主屏时，原先落在副屏上的窗口必须被拉回主屏
        {
            var onlyMain = new[] { new WindowPlacement.ScreenRect(0, 0, 1920, 1080) };
            (double l, double t) = WindowPlacement.ClampToNearest(2500, 60, WinW, WinH, onlyMain);

            sb.AppendLine(Mark(l >= -Eps && l + WinW <= 1920 + Eps,
                "副屏拔掉后窗口被拉回主屏",
                $"原 left=2500 → {F(l)}，top={F(t)}"));
        }

        // ⑫ 两块屏缩放不同（主屏 100%，副屏 200% 对应的 DIP 更小）：挑屏只看 DIP 坐标，不做额外换算
        {
            var mixedDpi = new[]
            {
                new WindowPlacement.ScreenRect(0, 0, 1920, 1080),
                new WindowPlacement.ScreenRect(1920, 0, 1280, 720),   // 4K@200% 的 DIP 工作区
            };
            (double l, double t) = WindowPlacement.ClampToNearest(2600, 40, WinW, WinH, mixedDpi);

            // 2600+183=2783 > 1920+1280=3200? 不，3200 > 2783 → 该屏放得下，原样保留
            bool inSecond = l >= 1920 - Eps && l + WinW <= 3200 + Eps;
            sb.AppendLine(Mark(inSecond && Math.Abs(l - 2600) < Eps,
                "两块屏缩放不同时，窗口留在自己那块屏上",
                $"left=2600 → {F(l)}（副屏 DIP 宽 1280），top={F(t)}"));
        }

        // ⑬ 防御：一块屏都没有（理论上不该发生）时不能返回 NaN
        {
            (double l, double t) = WindowPlacement.ClampToNearest(100, 100, WinW, WinH,
                Array.Empty<WindowPlacement.ScreenRect>());

            sb.AppendLine(Mark(!double.IsNaN(l) && !double.IsNaN(t),
                "显示器列表为空时不返回 NaN",
                $"left={F(l)} top={F(t)}"));
        }
    }

    /// <summary>
    /// 窗口宽度与 DPI 的换算。这是 2026-09-28「远程 150% 缩放下 UI 全乱」的防回归。
    ///
    /// 现场：本地 100% 正常；远程控制时 2K@150%，挂件里「时间到！」左右各被切掉约 20px、
    /// 小字折成两行、输入框和两个按钮叠在一起。从截图反推出来的两个数字：
    ///   · 红字高 47px = 33 DIP × 1.5  → **内容是按 150% 画的**
    ///   · 卡片宽 177px = 183 DIP × 1.0 → **窗口还是 100% 时的物理尺寸**
    /// 而 177 ÷ 1.5 = 118，正好是「WPF 把这份物理宽度当成了新 DPI 下的 DIP 宽度」，
    /// 118 × 1.5 又正好回到 177 —— 一旦掉进去就自我锁定，再也长不回来。
    ///
    /// 这毛病只在「另一种缩放」上出现，本机 100% 复现不了，只能把期望值算出来钉住。
    /// </summary>
    private static void AppendDpiWidthChecks(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("## 窗口宽度与 DPI（远程 150% 下 UI 全乱 的防回归）");
        sb.AppendLine("宽度只有一个来源 MainWindow.CardWidth；物理宽度必须 = 它 × 当前缩放。");
        sb.AppendLine();

        (string Name, double Scale, int ExpectPx)[] cases =
        {
            ("100%（本机）",       1.00, 183),
            ("125%（常见笔记本）", 1.25, 229),
            ("150%（远程 2K）",    1.50, 275),   // ← 现场：实测只有 177
            ("175%",               1.75, 320),
            ("200%（4K 默认）",    2.00, 366),
        };

        foreach ((string name, double scale, int expectPx) in cases)
        {
            int got = WindowPlacement.ExpectedPhysicalWidth(MainWindow.CardWidth, scale);
            AppendCd(sb, "物理宽度 · " + name,
                got.ToString(CultureInfo.InvariantCulture),
                expectPx.ToString(CultureInfo.InvariantCulture));
        }

        // 把「漂了」长什么样写进报告：150% 的机器上量到 ≈183px 就是在按 100% 画
        {
            int stale = WindowPlacement.ExpectedPhysicalWidth(MainWindow.CardWidth, 1.0);
            int want = WindowPlacement.ExpectedPhysicalWidth(MainWindow.CardWidth, 1.5);
            sb.AppendLine(Mark(want - stale >= 90,
                "判据：150% 环境下量到 ≈183px 即为漂移（现场实测 177px）",
                $"漂移值 {stale}px vs 正确值 {want}px"));
        }

        // 缩放取不到/传 0 时按 1:1 兜底，绝不能算出 0 宽把挂件弄没
        AppendCd(sb, "缩放非法时按 1:1 兜底",
            WindowPlacement.ExpectedPhysicalWidth(MainWindow.CardWidth, 0).ToString(CultureInfo.InvariantCulture),
            "183");

        // 主窗口的宽度真的来自那个常量：XAML 用 {x:Static} 指过来。
        // 断言的是「从 XAML 读到了这个值」，不是「常量等于自己」。
        // ⚠️ 这个漂移**截图看不出来** —— 离屏渲染会把宽度强行设成常量，只能读窗口属性。
        try
        {
            var mw = new MainWindow();
            AppendCd(sb,
                "主窗口 Width = MainWindow.CardWidth（XAML 里不许再写一份数字）",
                mw.Width.ToString("0.#", CultureInfo.InvariantCulture),
                MainWindow.CardWidth.ToString("0.#", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAIL  实例化主窗口读宽度 → " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Mark(bool ok, string label, string detail)
        => (ok ? "PASS  " : "FAIL  ") + label + " → " + detail;

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

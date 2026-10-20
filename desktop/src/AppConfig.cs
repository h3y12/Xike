using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeGuard;

/// <summary>
/// 闹钟在 config.json 里的存法。单独一个类型是为了让 json 短且好读 ——
/// 直接序列化 <see cref="Alarm"/> 会把 HashSet&lt;DayOfWeek&gt; 写成啰嗦的对象。
/// </summary>
public sealed class AlarmRecord
{
    public string Id { get; set; } = string.Empty;

    /// <summary>钟点，如 "06:30"。</summary>
    public string At { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>是否生效。</summary>
    public bool On { get; set; } = true;

    /// <summary>"once" / "daily" / "weekly"。</summary>
    public string Repeat { get; set; } = "once";

    /// <summary>weekly 时生效的星期几，"1,3,5"（1 = 周一）。</summary>
    public string Days { get; set; } = string.Empty;

    /// <summary>一次性闹钟响过是否删除。</summary>
    public bool DeleteAfterRing { get; set; } = true;

    /// <summary>所属连锁串，空串 = 独立闹钟。</summary>
    public string Chain { get; set; } = string.Empty;

    /// <summary>在串里的序号。</summary>
    public int ChainIndex { get; set; }
}

public sealed class AppConfig
{
    /// <summary>配置格式版本，用于一次性纠正历史默认值。当前 = 2。</summary>
    public int ConfigVersion { get; set; }

    /// <summary>窗口左上角 X（设备无关单位），null 表示尚未记录。</summary>
    public double? Left { get; set; }

    /// <summary>窗口左上角 Y，null 表示尚未记录。</summary>
    public double? Top { get; set; }

    public double Opacity { get; set; } = 0.88;

    /// <summary>
    /// 鼠标穿透。**默认必须是 false**：开着的时候鼠标点不到窗口，倒计时输入框根本没法打字。
    /// 想要穿透的人从托盘/右键菜单自己开，那是"当纯显示器用"的场景，不能当默认。
    /// </summary>
    public bool ClickThrough { get; set; }

    // ---- 两个面板各自的手动开关（从托盘菜单或右键菜单切）----

    /// <summary>手动收起「早睡健康分」块。</summary>
    public bool ScoreHidden { get; set; }

    /// <summary>手动收起「倒计时」块。</summary>
    public bool CountdownHidden { get; set; }

    /// <summary>白天（隐藏时段）里也把倒计时面板留在屏幕上。托盘点「显示倒计时」时置位。</summary>
    public bool CountdownPinned { get; set; }

    // ---- 规则（可在设置窗口调整）----

    /// <summary>窗口出现、开始记分。</summary>
    public string ShowAtText { get; set; } = "20:30";

    /// <summary>开始扣分。</summary>
    public string DeductAtText { get; set; } = "23:30";

    /// <summary>窗口隐藏。</summary>
    public string HideAtText { get; set; } = "06:30";

    /// <summary>每多少分钟扣一次。</summary>
    public int MinutesPerPoint { get; set; } = 3;

    /// <summary>每次扣多少分。</summary>
    public int PointsPerStep { get; set; } = 1;

    // ---- 状态文案（分数下面那一行字，可在设置窗口改）----
    // 空串是合法值：表示"这个状态不显示文字"。null 只在 json 被手工改坏时出现，Sanitize 会兜住。

    /// <summary>已出现、还没开始扣分时的文案。</summary>
    public string StatusBeforeText { get; set; } = SleepRules.Default.StatusBefore;

    /// <summary>高分段的文案。</summary>
    public string StatusHighText { get; set; } = SleepRules.Default.StatusHigh;

    /// <summary>中分段的文案。</summary>
    public string StatusMidText { get; set; } = SleepRules.Default.StatusMid;

    /// <summary>低分段的文案。</summary>
    public string StatusLowText { get; set; } = SleepRules.Default.StatusLow;

    // ---- 倒计时（落盘，重启后接着走）----

    public string CdInput { get; set; } = string.Empty;
    public string CdMode { get; set; } = string.Empty;
    public long CdClockTicks { get; set; }
    public long CdDurationTicks { get; set; }
    public string CdDeadline { get; set; } = string.Empty;
    public string CdPhase { get; set; } = string.Empty;
    public long CdPausedTicks { get; set; }

    // ---- 闹钟（落盘）----

    /// <summary>
    /// 全部闹钟。界面**不要**直接改这个列表，一律经 <see cref="AlarmBook"/> 增删 ——
    /// 它负责排序、去重与连锁分组。
    /// </summary>
    public List<AlarmRecord> Alarms { get; set; } = new();

    /// <summary>响铃持续秒数。默认 30 秒。</summary>
    public int AlarmRingSeconds { get; set; } = 30;

    /// <summary>响铃声音：空串 = 系统提示音；否则是音频文件的完整路径。</summary>
    public string AlarmSoundPath { get; set; } = string.Empty;

    /// <summary>「稍后提醒」推迟几分钟。</summary>
    public int AlarmSnoozeMinutes { get; set; } = 5;

    /// <summary>手动收起「闹钟」块。</summary>
    public bool AlarmPanelHidden { get; set; }

    /// <summary>响铃时是否把窗口顶到最前（默认开，否则闹钟容易被别的窗口盖住）。</summary>
    public bool AlarmBringToFront { get; set; } = true;

    /// <summary>把落盘的闹钟读进一个 AlarmBook。</summary>
    public AlarmBook BuildAlarmBook()
    {
        var book = new AlarmBook();
        if (Alarms is null) return book;

        foreach (AlarmRecord r in Alarms)
        {
            if (r is null) continue;
            if (!SleepRules.TryParseHm(r.At, out TimeSpan ts)) continue;   // 手改坏了就跳过，不让程序倒下

            var a = new Alarm
            {
                Id = string.IsNullOrEmpty(r.Id) ? Alarm.NewId() : r.Id,
                Label = r.Label ?? string.Empty,
                TimeOfDay = ts,
                Enabled = r.On,
                Repeat = ParseRepeat(r.Repeat),
                DeleteAfterRing = r.DeleteAfterRing,
                ChainId = r.Chain ?? string.Empty,
                ChainIndex = r.ChainIndex,
                Days = ParseDays(r.Days)
            };
            a.Normalize();
            book.Add(a);
        }

        book.NormalizeAll();
        return book;
    }

    /// <summary>把 AlarmBook 的状态写回可落盘的记录。</summary>
    public void CaptureAlarms(AlarmBook book)
    {
        var list = new List<AlarmRecord>();
        foreach (Alarm a in book.Sorted())
        {
            list.Add(new AlarmRecord
            {
                Id = a.Id,
                At = a.TimeText,
                Label = a.Label,
                On = a.Enabled,
                Repeat = RepeatText(a.Repeat),
                Days = DaysText(a.Days),
                DeleteAfterRing = a.DeleteAfterRing,
                Chain = a.ChainId,
                ChainIndex = a.ChainIndex
            });
        }
        Alarms = list;
    }

    private static string RepeatText(AlarmRepeat r) => r switch
    {
        AlarmRepeat.Once => "once",
        AlarmRepeat.Daily => "daily",
        _ => "weekly"
    };

    private static AlarmRepeat ParseRepeat(string? s) => s switch
    {
        "daily" => AlarmRepeat.Daily,
        "weekly" => AlarmRepeat.Weekly,
        _ => AlarmRepeat.Once
    };

    /// <summary>星期几存成 "1,3,5"（1 = 周一 … 7 = 周日），比存枚举名短且稳定。</summary>
    private static string DaysText(HashSet<DayOfWeek> days)
    {
        var nums = new List<int>();
        foreach (DayOfWeek d in Alarm.WeekOrder)
            if (days.Contains(d)) nums.Add(((int)d + 6) % 7 + 1);
        nums.Sort();
        return string.Join(",", nums);
    }

    private static HashSet<DayOfWeek> ParseDays(string? text)
    {
        var set = new HashSet<DayOfWeek>();
        if (string.IsNullOrWhiteSpace(text)) return set;

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), out int n)) continue;
            if (n is < 1 or > 7) continue;
            set.Add((DayOfWeek)(n % 7));   // 1 = 周一
        }
        return set;
    }

    /// <summary>由上面九个字段推导，不落盘（避免 config.json 里出现两份同样的规则）。</summary>
    [JsonIgnore]
    public SleepRules Rules => new()
    {
        ShowAt = SleepRules.ParseHm(ShowAtText, SleepRules.Default.ShowAt),
        DeductAt = SleepRules.ParseHm(DeductAtText, SleepRules.Default.DeductAt),
        HideAt = SleepRules.ParseHm(HideAtText, SleepRules.Default.HideAt),
        MinutesPerPoint = MinutesPerPoint,
        PointsPerStep = PointsPerStep,
        MaxScore = ScoreEngine.MaxScore,
        StatusBefore = StatusBeforeText,
        StatusHigh = StatusHighText,
        StatusMid = StatusMidText,
        StatusLow = StatusLowText
    };

    public void ApplyRules(SleepRules r)
    {
        ShowAtText = SleepRules.FormatHm(r.ShowAt);
        DeductAtText = SleepRules.FormatHm(r.DeductAt);
        HideAtText = SleepRules.FormatHm(r.HideAt);
        MinutesPerPoint = r.MinutesPerPoint;
        PointsPerStep = r.PointsPerStep;
        StatusBeforeText = r.StatusBefore;
        StatusHighText = r.StatusHigh;
        StatusMidText = r.StatusMid;
        StatusLowText = r.StatusLow;
    }

    /// <summary>把倒计时当前状态写进配置字段。</summary>
    public void CaptureCountdown(Countdown cd)
    {
        (string mode, long clockTicks, long durationTicks, string deadline, string phase, long pausedTicks) = cd.Snapshot();
        CdInput = cd.InputText;
        CdMode = mode;
        CdClockTicks = clockTicks;
        CdDurationTicks = durationTicks;
        CdDeadline = deadline;
        CdPhase = phase;
        CdPausedTicks = pausedTicks;
    }

    public void RestoreCountdown(Countdown cd, DateTime now)
    {
        cd.InputText = CdInput;
        cd.Restore(CdMode, CdClockTicks, CdDurationTicks, CdDeadline, CdPhase, CdPausedTicks, now);
    }

    // ---- 文件 ----

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string? _overrideFile;

    /// <summary>
    /// 自检模式改用独立配置文件。**必须隔离**：`--uitest ... clickthrough` 会把
    /// `ClickThrough=true` 连同新的 `ConfigVersion` 一起保存进用户的 config.json，
    /// 而版本迁移只在 ConfigVersion 更老时才清零 → 测试跑完，用户的窗口就永久点不动了。
    /// （这不是假设，是实测踩到的。）
    /// </summary>
    internal static void UseOverrideFile(string path) => _overrideFile = path;

    /// <summary>自检用：查当前生效的隔离文件路径（检查完要还原回去）。</summary>
    internal static string? OverrideFileForTest => _overrideFile;

    /// <summary>
    /// 自检用：让隔离文件也走「主文件坏了 → 从 .bak 救回 → 改名 .corrupt」这套恢复逻辑。
    /// 默认关闭——因为恢复逻辑会**改名/删除**磁盘文件，而自检默认只能读写自己那个隔离文件，
    /// 一旦误伤用户真实的 config.json 就麻烦（见 UseOverrideFile 的注释）。
    /// 打开后只影响当前的隔离路径，Load() 里除了跳过短路之外其余分支完全一致。
    /// </summary>
    internal static bool ExerciseRecoveryForTest { get; set; }

    private static string FilePath =>
        _overrideFile ?? Path.Combine(AppData, "TimeGuard", "config.json");

    /// <summary>改名前的旧配置（%APPDATA%\SleepGuard\config.json），首次运行自动搬过来，位置和规则不用重设。</summary>
    private static string LegacyFilePath => Path.Combine(AppData, "SleepGuard", "config.json");

    // ---- 自检钩子：只在内存里做"存盘 / 读回"，不碰磁盘 ----

    /// <summary>自检用：把一段 JSON 按"读配置文件"的方式解析（含 Sanitize）。</summary>
    internal static AppConfig FromJsonForTest(string json)
        => Sanitize(JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig());

    /// <summary>自检用：把配置序列化成 JSON（就是存盘时会写下去的内容）。</summary>
    internal static string ToJsonForTest(AppConfig c)
        => JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });

    public static AppConfig Load()
    {
        AppConfig? fromLegacy = null;

        try
        {
            // 自检模式：只读写自己的隔离文件，绝不碰用户的 config.json。
            // 默认直接读、不做恢复动作（改名/删文件会误伤用户的真实配置）；
            // 只有自检显式打开 ExerciseRecoveryForTest 时才往下走完整逻辑。
            if (_overrideFile is not null && !ExerciseRecoveryForTest)
            {
                return File.Exists(_overrideFile)
                    ? Sanitize(JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_overrideFile)) ?? new AppConfig())
                    : Sanitize(new AppConfig());
            }

            string p = FilePath;
            AppConfig? primary = TryRead(p);
            if (primary is not null) return Sanitize(primary);
            // 主文件缺失也可能是一次中断的保存，仍应优先恢复备份。
            AppConfig? backup = TryRead(p + ".bak");
            if (backup is not null) return Sanitize(backup);
            if (File.Exists(p))
            {
                // 两个都不行：回落默认，并把坏文件改名留证，别让下次启动继续踩它
                TryQuarantine(p);
                return Sanitize(new AppConfig());
            }
            else if (File.Exists(LegacyFilePath))
            {
                AppConfig? c = TryRead(LegacyFilePath);
                if (c is not null) fromLegacy = Sanitize(c);
            }
        }
        catch
        {
            // 配置损坏时静默回落默认值，不打扰使用
        }

        // 新配置也要过 Sanitize：否则 ConfigVersion 留在 0，下次启动会被上面的迁移逻辑
        // 当成"老配置"，把用户自己打开的鼠标穿透又改回 false。
        if (fromLegacy is null) return Sanitize(new AppConfig());

        fromLegacy.Save();   // 搬到新路径，下次不再读旧的
        return fromLegacy;
    }

    /// <summary>
    /// 落盘。**先写临时文件、再原子替换**，并且留一份 .bak。
    ///
    /// 为什么不能直接 `File.WriteAllText(p, json)`：那会先把原文件截断成 0 字节再写。
    /// 如果这时候断电、进程被杀、或者磁盘写满，用户留下的是一份**空文件或半截 JSON**——
    /// 窗口位置、规则、状态文案全丢。而且原来的写法把异常整个吞掉（`catch { }`），
    /// 失败了也不会有人知道。
    ///
    /// 现在的顺序：写 `config.json.tmp` → 备份旧文件为 `.bak` → 用
    /// `File.Replace`/`File.Move` 原子换过去。任何一步失败，原 config.json 都还在。
    /// </summary>
    public void Save()
    {
        string p = FilePath;
        string tmp = p + ".tmp";
        string bak = p + ".bak";

        try
        {
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // ① 先把完整内容写到临时文件（此时原文件还没动）
            File.WriteAllText(tmp, json);

            if (File.Exists(p))
            {
                // ② 有旧文件：先留一份备份，再原子替换
                //    不把损坏的主文件覆盖到有效备份上。
                if (TryRead(p) is null)
                {
                    File.Move(tmp, p, overwrite: true);
                    return;
                }
                try
                {
                    File.Replace(tmp, p, bak, ignoreMetadataErrors: true);
                }
                catch (Exception)
                {
                    // 保留主文件直到新文件完成替换，避免两次 Move 之间的空档。
                    File.Copy(p, bak, overwrite: true);
                    File.Move(tmp, p, overwrite: true);
                }
            }
            else
            {
                // ③ 首次运行：没有旧文件可替换，直接改名
                File.Move(tmp, p);
            }
        }
        catch
        {
            // 写盘失败（磁盘满、被占用、权限）：**不要动原文件**，也不要让程序倒下。
            // 顺手把临时文件清掉，免得下次启动把它当成配置读进去。
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败无所谓 */ }
    }

    /// <summary>读一份配置；文件不存在或内容坏了都返回 null，绝不抛。</summary>
    private static AppConfig? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把读不出来的配置文件改名留证，免得每次启动都拿它去解析失败一次。</summary>
    private static void TryQuarantine(string path)
    {
        try { File.Move(path, path + ".corrupt", overwrite: true); } catch { /* 挪不动就算了 */ }
    }

    /// <summary>当前配置格式版本。2 = 不再把鼠标穿透当默认值。</summary>
    private const int CurrentConfigVersion = 2;

    private static AppConfig Sanitize(AppConfig c)
    {
        // v1 拿鼠标穿透当默认值，后果是整个窗口点不动、倒计时输入框打不进字。
        // 老配置（含用户机器上已经写着的 "ClickThrough": true）一律改回不穿透——不然只改默认值没用。
        // 之后用户自己开穿透会被记成 v2，重启后不会被这段再冲掉。
        if (c.ConfigVersion < CurrentConfigVersion) c.ClickThrough = false;
        c.ConfigVersion = CurrentConfigVersion;

        if (double.IsNaN(c.Opacity) || c.Opacity < 0.2 || c.Opacity > 1.0) c.Opacity = 0.88;
        if (c.Left is double l && (double.IsNaN(l) || double.IsInfinity(l))) c.Left = null;
        if (c.Top is double t && (double.IsNaN(t) || double.IsInfinity(t))) c.Top = null;

        // 手改 config.json 写坏了也不至于让窗口消失或永驻
        if (!SleepRules.TryParseHm(c.ShowAtText, out _)) c.ShowAtText = SleepRules.FormatHm(SleepRules.Default.ShowAt);
        if (!SleepRules.TryParseHm(c.DeductAtText, out _)) c.DeductAtText = SleepRules.FormatHm(SleepRules.Default.DeductAt);
        if (!SleepRules.TryParseHm(c.HideAtText, out _)) c.HideAtText = SleepRules.FormatHm(SleepRules.Default.HideAt);
        if (c.MinutesPerPoint is < 1 or > 720) c.MinutesPerPoint = SleepRules.Default.MinutesPerPoint;
        if (c.PointsPerStep is < 1 or > ScoreEngine.MaxScore) c.PointsPerStep = SleepRules.Default.PointsPerStep;

        // 状态文案：null（json 里显式写了 null）退回默认值；**空串要保留**——那是"不显示文字"的意思。
        // 必须在下面 c.Rules.IsValid 之前处理：Rules 会把这些字段带进去，null 会在拼文字时炸。
        c.StatusBeforeText ??= SleepRules.Default.StatusBefore;
        c.StatusHighText ??= SleepRules.Default.StatusHigh;
        c.StatusMidText ??= SleepRules.Default.StatusMid;
        c.StatusLowText ??= SleepRules.Default.StatusLow;

        if (!c.Rules.IsValid(out _)) c.ApplyRules(SleepRules.Default);

        // 倒计时字段写坏了就当没设过，不要卡在一个没法重置的状态
        if (c.CdClockTicks < 0 || c.CdClockTicks > TimeSpan.TicksPerDay) c.CdClockTicks = 0;
        if (c.CdDurationTicks < 0 || c.CdDurationTicks > TimeSpan.FromMinutes(Countdown.MaxDurationMinutes).Ticks)
            c.CdDurationTicks = 0;
        if (c.CdPausedTicks < 0 || c.CdPausedTicks > TimeSpan.FromDays(2).Ticks) c.CdPausedTicks = 0;

        if (!Enum.TryParse(c.CdMode, out CountdownMode _)) c.CdMode = string.Empty;
        if (!Enum.TryParse(c.CdPhase, out CountdownPhase _)) c.CdPhase = string.Empty;
        if (string.IsNullOrEmpty(c.CdMode) || string.IsNullOrEmpty(c.CdPhase))
        {
            c.CdMode = string.Empty;
            c.CdPhase = string.Empty;
            c.CdDeadline = string.Empty;
        }

        // ---- 闹钟 ----
        // 手改 config.json 常见的几种坏法都要兜住，否则闹钟会"看得见但不响"，
        // 那比直接报错更难查。这里只修数据，不改变用户的意图（比如不会擅自打开关掉的闹钟）。
        c.AlarmRingSeconds = c.AlarmRingSeconds is < 1 or > 600 ? 30 : c.AlarmRingSeconds;
        c.AlarmSnoozeMinutes = c.AlarmSnoozeMinutes is < 1 or > 60 ? 5 : c.AlarmSnoozeMinutes;
        c.AlarmSoundPath ??= string.Empty;
        c.Alarms ??= new List<AlarmRecord>();

        c.Alarms.RemoveAll(r => r is null);

        var seenIds = new HashSet<string>();
        foreach (AlarmRecord r in c.Alarms)
        {
            r.Id = string.IsNullOrEmpty(r.Id) ? Alarm.NewId() : r.Id;
            if (!seenIds.Add(r.Id)) r.Id = Alarm.NewId();     // Id 重复会让"响了没"的记账串到别的闹钟上
            r.Label ??= string.Empty;
            r.Chain ??= string.Empty;
            r.Days ??= string.Empty;

            // 时刻读不出来就当"没设过这一刻"，标成不生效而不是删掉 ——
            // 用户可能只是手滑打错一个字，删掉就找不回来了。
            if (!SleepRules.TryParseHm(r.At, out _))
            {
                r.At = "07:00";
                r.On = false;
            }
            else
            {
                SleepRules.TryParseHm(r.At, out TimeSpan ts);
                r.At = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
            }

            if (r.Repeat is not ("once" or "daily" or "weekly")) r.Repeat = "once";
            if (r.ChainIndex < 0) r.ChainIndex = 0;

            // 孤立的连锁成员（同一 ChainId 只有一个）会让"是不是最后一个"算错，
            // 摘成独立闹钟；用户在界面上看到的是「这不就是一串里的一个吗」，实测过。
            // 真正的成串情况在 BuildAlarmBook 之后再校正。
        }

        // 串的成员校正：ChainId 非空但成员数 < 2 的，一律摘出去当独立闹钟
        var chainCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AlarmRecord r in c.Alarms)
        {
            if (string.IsNullOrEmpty(r.Chain)) continue;
            chainCounts.TryGetValue(r.Chain, out int n);
            chainCounts[r.Chain] = n + 1;
        }
        foreach (AlarmRecord r in c.Alarms)
        {
            if (string.IsNullOrEmpty(r.Chain)) continue;
            if (chainCounts[r.Chain] < 2)
            {
                r.Chain = string.Empty;
                r.ChainIndex = 0;
            }
        }

        return c;
    }
}

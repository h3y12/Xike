using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TimeGuard;

/// <summary>
/// 闹钟设置窗。两个页签：普通闹钟 / 起床闹钟（连锁）。
///
/// 设计取向：**窗口只管编辑一份草稿，不自己去写配置**。
/// 保存时把整理好的 <see cref="AlarmBook"/> 交给调用方（MainWindow），
/// 由它统一落盘。这样"谁负责存盘"只有一处，不会出现两个窗口各存一半的情况。
/// </summary>
public partial class AlarmWindow : Window
{
    /// <summary>窗口宽度。XAML 用 x:Static 引用，只此一处定义。</summary>
    public const double CardWidth = 476;

    private static readonly Brush OnText = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush OffText = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly Brush HintText = new SolidColorBrush(Color.FromArgb(0xC6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ProblemText = new SolidColorBrush(Color.FromRgb(0xFF, 0xB1, 0x74));

    /// <summary>正在编辑的草稿。窗口里所有增删都改它，点保存才交出去。</summary>
    private readonly AlarmBook _book = new();

    /// <summary>当前显示的是"起床闹钟"页吗（决定渲染哪一列）。</summary>
    private bool _wakeTab;

    /// <summary>保存结果；不为 null 表示用户点了保存。</summary>
    public AlarmBook? Result { get; private set; }

    /// <summary>保存时一并带出去的响铃相关设置（窗口里可改）。</summary>
    public int RingSeconds { get; private set; } = 30;

    public AlarmWindow(AlarmBook current, int ringSeconds)
    {
        InitializeComponent();
        RingSeconds = ringSeconds;

        foreach (Alarm a in current.Items) _book.Add(a.Clone());
        _book.NormalizeAll();

        // ⚠️ 必须在这里补一次：XAML 里 TabNormal IsChecked="True" 会在 InitializeComponent 期间
        // 触发 Tab_Checked，那时 PageNormal/PageWake 还没建好，会被提前 return 掉 ——
        // 结果是**打开窗口时标题下那行说明是空的**，点了页签才冒出来。
        ApplyTab();

        FillChainDefaults();
        RenderLists();
        UpdateChainPreview();
    }

    // ---- 初始值 ----

    /// <summary>如果已经有一串起床闹钟，把它的参数回显出来，改起来不用重填。</summary>
    private void FillChainDefaults()
    {
        var chains = _book.Chains();
        if (chains.Count == 0) return;

        AlarmChainSpec? spec = _book.InferChainSpec(chains[0].ChainId);
        if (spec is null) return;

        TxtChainStart.Text = SleepRules.FormatHm(spec.StartAt);
        TxtChainInterval.Text = spec.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        TxtChainCount.Text = spec.Count.ToString(CultureInfo.InvariantCulture);
        ChainDaily.IsChecked = spec.Daily && !spec.WeekdaysOnly;
        ChainWeekdays.IsChecked = spec.Daily && spec.WeekdaysOnly;
        ChainOnce.IsChecked = !spec.Daily;

        string? label = chains[0].Items.FirstOrDefault()?.Label;
        if (!string.IsNullOrWhiteSpace(label)) TxtChainLabel.Text = label;
    }

    // ---- 页签 ----

    private void Tab_Checked(object sender, RoutedEventArgs e) => ApplyTab();

    /// <summary>两个页签下的说明行文字。开窗时要能直接取到，所以抽成静态方法。</summary>
    private static string SubtitleFor(bool wake) => wake
        ? "自动生成一串互相咬合的闹钟，专治赖床。"
        : "每个闹钟一个方块，按时间先后排列（以凌晨四点为界）。";

    /// <summary>
    /// 把当前选中的页签落实下去：页签高亮、正文可见性、说明行。
    /// 构造函数和 Tab_Checked 都走这里 —— 开窗时的首屏和点页签后必须一模一样，
    /// 各写一份迟早漂移。
    /// </summary>
    private void ApplyTab()
    {
        if (PageNormal is null || PageWake is null) return;   // InitializeComponent 期间的提前触发

        _wakeTab = TabWake.IsChecked == true;
        PageNormal.Visibility = _wakeTab ? Visibility.Collapsed : Visibility.Visible;
        PageWake.Visibility = _wakeTab ? Visibility.Visible : Visibility.Collapsed;
        LblSubtitle.Text = SubtitleFor(_wakeTab);
        LblSubtitle.Foreground = HintText;
        RenderLists();
    }

    // ---- 重复方式 ----

    private void Repeat_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDays is null) return;
        bool weekly = RepWeekly?.IsChecked == true;
        PanelDays.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;

        // 「响过就删」只对一次性闹钟有意义
        if (ChkDeleteAfter is not null)
        {
            bool once = RepOnce?.IsChecked == true;
            ChkDeleteAfter.IsEnabled = once;
            ChkDeleteAfter.Opacity = once ? 1.0 : 0.42;
        }
    }

    private HashSet<DayOfWeek> PickedDays()
    {
        var set = new HashSet<DayOfWeek>();
        if (D1?.IsChecked == true) set.Add(DayOfWeek.Monday);
        if (D2?.IsChecked == true) set.Add(DayOfWeek.Tuesday);
        if (D3?.IsChecked == true) set.Add(DayOfWeek.Wednesday);
        if (D4?.IsChecked == true) set.Add(DayOfWeek.Thursday);
        if (D5?.IsChecked == true) set.Add(DayOfWeek.Friday);
        if (D6?.IsChecked == true) set.Add(DayOfWeek.Saturday);
        if (D7?.IsChecked == true) set.Add(DayOfWeek.Sunday);
        return set;
    }

    private void SetPickedDays(HashSet<DayOfWeek> days)
    {
        ToggleButton[] btns = { D1, D2, D3, D4, D5, D6, D7 };
        DayOfWeek[] order = Alarm.WeekOrder;
        for (int i = 0; i < btns.Length && i < order.Length; i++)
            btns[i].IsChecked = days.Contains(order[i]);
    }

    // ---- 列表渲染 ----

    private void RenderLists()
    {
        RenderInto(ListNormal, LblEmptyNormal, wake: false);
        RenderInto(ListWake, LblEmptyWake, wake: true);
    }

    private void RenderInto(StackPanel host, TextBlock emptyHint, bool wake)
    {
        if (host is null) return;
        host.Children.Clear();

        List<Alarm> items = _book.Sorted().Where(a => (a.ChainId.Length > 0) == wake).ToList();
        if (emptyHint is not null) emptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (Alarm a in items) host.Children.Add(BuildRow(a));
    }

    /// <summary>画一行闹钟：时间（大） + 说明（小） + 开关 + 删除。</summary>
    private Border BuildRow(Alarm a)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(a.Enabled ? (byte)0x14 : (byte)0x0A, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 时间：生效黑字（浅底）、不生效浅灰字 —— 用户明确要求的判据
        var time = new TextBlock
        {
            Text = a.TimeText,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = a.Enabled ? OnText : OffText,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 62
        };
        Grid.SetColumn(time, 0);

        var desc = new TextBlock
        {
            FontSize = 11.5,
            Foreground = a.Enabled ? HintText : OffText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 8, 0)
        };
        string chainInfo = a.ChainId.Length > 0
            ? $"第 {a.ChainIndex}/{_book.Items.Count(x => x.ChainId == a.ChainId)} 个 · "
            : string.Empty;
        string name = string.IsNullOrWhiteSpace(a.Label) ? string.Empty : a.Label + " · ";
        desc.Text = chainInfo + name + a.RepeatText + (a.DeleteAfterRing && a.Repeat == AlarmRepeat.Once ? " · 响过即删" : "");
        Grid.SetColumn(desc, 1);

        var toggle = new Button
        {
            Content = a.Enabled ? "已开" : "已关",
            Style = (Style)FindResource("BtnSmall"),
            Background = a.Enabled
                ? new SolidColorBrush(Color.FromArgb(0x4D, 0x5D, 0xCA, 0xA5))
                : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 48
        };
        toggle.Click += (_, _) =>
        {
            a.Enabled = !a.Enabled;   // 草稿里直接翻，保存时一起生效
            RenderLists();
        };
        Grid.SetColumn(toggle, 2);

        var del = new Button
        {
            Content = "删除",
            Style = (Style)FindResource("BtnSmall"),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        del.Click += (_, _) =>
        {
            if (a.ChainId.Length > 0)
            {
                int n = _book.Items.Count(x => x.ChainId == a.ChainId);
                _book.Remove(a.Id);
                // 串删到只剩一个就不算串了，摘成独立闹钟，否则"最后一个"会判断错
                List<Alarm> left = _book.Items.Where(x => x.ChainId == a.ChainId).OrderBy(x => x.ChainIndex).ToList();
                if (left.Count == 1) { left[0].ChainId = string.Empty; left[0].ChainIndex = 0; }
                else for (int i = 0; i < left.Count; i++) left[i].ChainIndex = i + 1;
                _ = n;
            }
            else _book.Remove(a.Id);

            _book.NormalizeAll();
            RenderLists();
            UpdateChainPreview();
        };
        Grid.SetColumn(del, 3);

        g.Children.Add(time);
        g.Children.Add(desc);
        g.Children.Add(toggle);
        g.Children.Add(del);
        row.Child = g;
        return row;
    }

    // ---- 添加普通闹钟 ----

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!Alarm.TryParseTimeText(TxtNewTime.Text, out TimeSpan at, out string err))
        {
            ShowError(err);
            return;
        }

        AlarmRepeat rep = RepDaily.IsChecked == true ? AlarmRepeat.Daily
                        : RepWeekly.IsChecked == true ? AlarmRepeat.Weekly
                        : AlarmRepeat.Once;

        HashSet<DayOfWeek> days = rep == AlarmRepeat.Weekly ? PickedDays() : new HashSet<DayOfWeek>();
        if (rep == AlarmRepeat.Weekly && days.Count == 0)
        {
            ShowError("选了「每周」但一天都没勾。至少要勾一天。");
            return;
        }

        _book.AddManual(at, TxtNewLabel.Text?.Trim() ?? string.Empty, rep, days,
                        rep == AlarmRepeat.Once && ChkDeleteAfter.IsChecked == true);

        HideError();
        TxtNewLabel.Text = string.Empty;
        RenderLists();
    }

    // ---- 连锁起床闹钟 ----

    private void Chain_TextChanged(object sender, TextChangedEventArgs e) => UpdateChainPreview();

    /// <summary>把当前参数算一遍，把结果先说给用户听。</summary>
    private void UpdateChainPreview()
    {
        if (LblChainPreview is null) return;

        if (!TryReadChainSpec(out AlarmChainSpec spec, out string err))
        {
            LblChainPreview.Text = err;
            LblChainPreview.Foreground = ProblemText;
            return;
        }

        LblChainPreview.Foreground = HintText;
        var times = new List<string>();
        for (int i = 0; i < spec.Count; i++)
        {
            int total = (int)spec.StartAt.TotalMinutes + i * spec.IntervalMinutes;
            times.Add($"{(total / 60) % 24:00}:{total % 60:00}");
        }

        string rep = ChainOnce.IsChecked == true ? "仅一次"
                   : ChainWeekdays.IsChecked == true ? "工作日"
                   : "每天";
        LblChainPreview.Text = $"将生成 {spec.Count} 个（{rep}）：{string.Join(" → ", times)}";
    }

    private bool TryReadChainSpec(out AlarmChainSpec spec, out string error)
    {
        spec = new AlarmChainSpec();

        if (!Alarm.TryParseTimeText(TxtChainStart.Text, out TimeSpan start, out string e1))
        {
            error = e1;
            return false;
        }
        if (!int.TryParse(TxtChainInterval.Text?.Trim(), out int interval))
        {
            error = "「间隔时间」要填整数分钟。";
            return false;
        }
        if (!int.TryParse(TxtChainCount.Text?.Trim(), out int count))
        {
            error = "「响铃次数」要填整数。";
            return false;
        }

        spec = new AlarmChainSpec
        {
            StartAt = start,
            IntervalMinutes = interval,
            Count = count,
            Daily = ChainOnce.IsChecked != true,
            WeekdaysOnly = ChainWeekdays.IsChecked == true
        };

        if (!spec.IsValid(out string e2))
        {
            error = e2;
            return false;
        }

        // 跨零点会让一串闹钟分成两段显示，先说清楚，免得用户以为算错了
        int lastTotal = (int)start.TotalMinutes + (count - 1) * interval;
        error = lastTotal >= 24 * 60
            ? $"注意：最后一个落在次日 {(lastTotal / 60) % 24:00}:{lastTotal % 60:00}，会排在列表末尾。"
            : string.Empty;
        return true;
    }

    private void BtnChainGenerate_Click(object sender, RoutedEventArgs e) => GenerateChain(replace: true);

    private void BtnChainAppend_Click(object sender, RoutedEventArgs e) => GenerateChain(replace: false);

    private void GenerateChain(bool replace)
    {
        if (!TryReadChainSpec(out AlarmChainSpec spec, out string err))
        {
            ShowError(err);
            return;
        }

        string label = TxtChainLabel.Text?.Trim() ?? string.Empty;

        if (replace)
        {
            // "替换"= 把已有的起床闹钟全清掉再生成，避免攒出好几串
            List<Alarm> old = _book.Items.Where(x => x.ChainId.Length > 0).ToList();
            foreach (Alarm a in old) _book.Remove(a.Id);
        }

        _book.AddChain(spec, label);
        _book.NormalizeAll();

        HideError();
        RenderLists();
        if (!string.IsNullOrEmpty(err))
        {
            // 跨零点的提醒，用告警色但不算错误
            LblChainPreview.Foreground = ProblemText;
            LblChainPreview.Text = err;
        }
    }

    // ---- 错误提示 ----

    private void ShowError(string message)
    {
        if (LblChainPreview is null) return;
        // 两个页签共用一个提示位置：报错时就近显示，不额外占版面
        if (_wakeTab)
        {
            LblChainPreview.Text = message;
            LblChainPreview.Foreground = ProblemText;
        }
        else
        {
            LblSubtitle.Text = message;
            LblSubtitle.Foreground = ProblemText;
        }
    }

    private void HideError()
    {
        if (LblChainPreview is not null) LblChainPreview.Foreground = HintText;
        if (LblSubtitle is not null)
        {
            LblSubtitle.Foreground = HintText;
            // 普通页报错是顶在说明行上的，清错时得把说明**文字**也还回来（原先只还了颜色）
            LblSubtitle.Text = SubtitleFor(_wakeTab);
        }
    }

    // ---- 保存 / 取消 ----

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _book.NormalizeAll();
        Result = _book;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 未按住左键时 DragMove 会抛，忽略 */ }
    }

    // ---- 自检钩子 ----

    /// <summary>自检用：直接塞一批闹钟进去看渲染与排序（不开真窗口也能查逻辑，开窗口时查得更多）。</summary>
    internal void LoadForTest(IEnumerable<Alarm> alarms)
    {
        _book.Clear();
        foreach (Alarm a in alarms) _book.Add(a);
        _book.NormalizeAll();
        RenderLists();
        UpdateChainPreview();
    }

    /// <summary>自检用：切到起床闹钟页。</summary>
    internal void SelectWakeTabForTest()
    {
        TabWake.IsChecked = true;
        Tab_Checked(TabWake, new RoutedEventArgs());
    }

    /// <summary>自检用：读当前草稿（不保存）。</summary>
    internal AlarmBook DraftForTest => _book;

    /// <summary>自检用：把连锁参数填进去，然后照点"生成"走一遍。</summary>
    internal void FillChainForTest(string start, string interval, string count, bool once)
    {
        TxtChainStart.Text = start;
        TxtChainInterval.Text = interval;
        TxtChainCount.Text = count;
        ChainOnce.IsChecked = once;
        ChainDaily.IsChecked = !once;
        BtnChainGenerate_Click(this, new RoutedEventArgs());
    }

    /// <summary>自检用：读连锁预览那行字（验证用户看到的说明与实际生成一致）。</summary>
    internal string ChainPreviewForTest => LblChainPreview?.Text ?? string.Empty;

    /// <summary>自检用：读标题下那行说明（开窗首屏就该有，不点页签也必须有）。</summary>
    internal string SubtitleForTest => LblSubtitle?.Text ?? string.Empty;

    /// <summary>自检用：走一遍"添加普通闹钟"的按钮逻辑。</summary>
    internal bool AddNormalForTest(string time, string label, int repeatMode, bool deleteAfter, int[] days)
    {
        TxtNewTime.Text = time;
        TxtNewLabel.Text = label;
        RepOnce.IsChecked = repeatMode == 0;
        RepDaily.IsChecked = repeatMode == 1;
        RepWeekly.IsChecked = repeatMode == 2;

        var set = new HashSet<DayOfWeek>();
        foreach (int d in days)
            if (d is >= 1 and <= 7) set.Add((DayOfWeek)(d % 7));
        SetPickedDays(set);

        int before = _book.Count;
        BtnAdd_Click(this, new RoutedEventArgs());
        return _book.Count > before;
    }
}

using System;
using System.Windows;
using System.Windows.Input;

namespace TimeGuard;

/// <summary>
/// 闹钟响铃时的弹窗。独立于挂件：挂件可能被收起、最小化到托盘、或者被别的窗口盖住，
/// 而闹钟响的时候必须有东西在屏幕正中让人看见。
///
/// 它自己**不管业务**（不知道 AlarmBook 是什么）：用户点了按钮就抛事件，
/// 由 MainWindow 决定"关掉这次响铃"还是"稍后提醒"。这样判断逻辑只有一处。
/// </summary>
public partial class RingWindow : Window
{
    /// <summary>窗口宽度。XAML 用 x:Static 引用，只此一处定义（和另外两个窗口一致）。</summary>
    public const double CardWidth = 320;

    /// <summary>用户点了「知道了」。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>用户点了「稍后提醒」。</summary>
    public event EventHandler? SnoozeRequested;

    public RingWindow()
    {
        InitializeComponent();
    }

    /// <summary>把当前响铃的内容刷到界面上。</summary>
    public void Update(RingingState state, int snoozeMinutes)
        => Update(state, snoozeMinutes, DateTime.Now);

    /// <summary>
    /// 同上，但由调用方给定时钟。快照（--snapshot）走这条，好让图上的「还剩 N 秒」是定值，
    /// 两次出图能逐字比对，不会被出图的时刻带偏。
    /// </summary>
    public void Update(RingingState state, int snoozeMinutes, DateTime now)
    {
        Alarm a = state.Alarm;

        TxtWho.Text = AlarmRinger.RingingText(a);
        TxtTime.Text = a.TimeText;

        // 说明这一行：让用户知道这是哪种闹钟
        string note;
        if (a.ChainId.Length > 0)
            note = $"连锁起床闹钟（第 {a.ChainIndex} 个）· {a.RepeatText}";
        else
            note = a.RepeatText + (a.Repeat == AlarmRepeat.Once && a.DeleteAfterRing ? " · 响过即删" : string.Empty);
        TxtNote.Text = note;

        // 连锁串：告诉用户后面还有几个
        bool inChain = a.ChainId.Length > 0;
        BoxChain.Visibility = inChain ? Visibility.Visible : Visibility.Collapsed;
        if (inChain)
        {
            TxtChain.Text = "关掉这一个，后面的还会照常响 —— 必须全部关完才会彻底安静。";
        }

        int left = state.SecondsLeft(now);
        TxtAutoStop.Text = left > 0 ? $"{left} 秒后自动停止" : string.Empty;

        BtnSnooze.Content = snoozeMinutes + " 分钟后再说";
    }

    /// <summary>响铃期间每帧刷新一下剩余秒数（只有这行字在变）。</summary>
    public void UpdateCountdown(RingingState state)
        => UpdateCountdown(state, DateTime.Now);

    /// <inheritdoc cref="Update(RingingState, int, DateTime)"/>
    public void UpdateCountdown(RingingState state, DateTime now)
    {
        int left = state.SecondsLeft(now);
        TxtAutoStop.Text = left > 0 ? $"{left} 秒后自动停止" : string.Empty;
    }

    private void BtnDismiss_Click(object sender, RoutedEventArgs e)
    {
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BtnSnooze_Click(object sender, RoutedEventArgs e)
    {
        SnoozeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 回车 = 知道了，Esc = 稍后提醒。
    /// 两个键都能收掉弹窗，用户不用去找按钮；把常用的「知道了」放回车、
    /// 需要多睡一会儿的放 Esc（不常按的那个）。
    /// </summary>
    private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnDismiss_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Key.Escape) { e.Handled = true; BtnSnooze_Click(this, new RoutedEventArgs()); }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 未按住左键时 DragMove 会抛，忽略 */ }
    }
}

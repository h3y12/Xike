using System;
using System.Collections.Generic;

namespace TimeGuard;

/// <summary>当前正在响的这次闹钟。</summary>
public sealed class RingingState
{
    public Alarm Alarm { get; init; } = null!;

    /// <summary>响铃从什么时候开始的。</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>还能响到什么时候。</summary>
    public DateTime EndsAt { get; init; }

    /// <summary>距离停止还有几秒（给界面倒计时用）。</summary>
    public int SecondsLeft(DateTime now)
    {
        double s = (EndsAt - now).TotalSeconds;
        return s <= 0 ? 0 : (int)Math.Ceiling(s);
    }
}

/// <summary>
/// 响铃的调度：把「到期事件」变成「开始响 / 响够了停 / 用户处理掉」。
///
/// 与 <see cref="AlarmBook"/> 的分工：Book 只管"到点了没"，本类只管"响了之后怎样"。
/// 两边都是纯逻辑，都不读系统时钟（时间由调用方传进来），因此自检里可以任意快进。
/// </summary>
public sealed class AlarmRinger
{
    /// <summary>系统提示音的重复间隔（秒）。太密会像故障，太疏又不像闹钟，2 秒是听感上"连续"的下限。</summary>
    public const int SystemSoundRepeatSeconds = 2;

    /// <summary>当前正在响的（同一时刻最多一个 —— 后到的排队等前一个被处理）。</summary>
    public RingingState? Current { get; private set; }

    /// <summary>等着响的（同一秒有多个闹钟到期时，排队一个个来）。</summary>
    private readonly Queue<AlarmFiring> _queue = new();

    /// <summary>上次敲系统提示音的时刻，用来控制敲击间隔。</summary>
    private DateTime _lastPing = DateTime.MinValue;

    /// <summary>本轮响铃一共响了多少秒（停止时用于回显）。</summary>
    public int LastRingSeconds { get; private set; }

    public bool IsRinging => Current is not null;

    /// <summary>排队等着的还有几个。</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// 收下一批到期事件。如果当前没在响，立刻开始响第一个。
    /// </summary>
    public void Accept(IEnumerable<AlarmFiring> firings, DateTime now, int ringSeconds)
    {
        foreach (AlarmFiring f in firings) _queue.Enqueue(f);
        if (IsRinging) return;
        BeginNext(now, ringSeconds);
    }

    private void BeginNext(DateTime now, int ringSeconds)
    {
        if (_queue.Count == 0) { Current = null; return; }

        AlarmFiring f = _queue.Dequeue();
        if (ringSeconds < 1) ringSeconds = 1;

        Current = new RingingState
        {
            Alarm = f.Alarm,
            StartedAt = now,
            EndsAt = now.AddSeconds(ringSeconds)
        };
        _lastPing = DateTime.MinValue;
    }

    /// <summary>
    /// 响铃期间每帧调一次：到点自动静音、按间隔敲系统音。
    /// 返回 true 表示"这一帧刚刚自动停下来了"（界面据此更新状态）。
    /// </summary>
    public bool Tick(DateTime now, AlarmSoundPlayer sound, int ringSeconds, bool repeatSystemSound = true)
    {
        if (Current is null) return false;

        if (now >= Current.EndsAt)
        {
            LastRingSeconds = (int)(now - Current.StartedAt).TotalSeconds;
            sound.Stop();
            Current = null;
            // 自动停之后立刻接下一个排队的（比如几个闹钟同一分钟）
            BeginNext(now, ringSeconds);
            return true;
        }

        if (repeatSystemSound && (now - _lastPing).TotalSeconds >= SystemSoundRepeatSeconds)
        {
            sound.PingSystemSound();
            _lastPing = now;
        }
        return false;
    }

    /// <summary>用户处理掉了当前这次响铃，开始响下一个排队的。</summary>
    public void DismissCurrent(DateTime now, int ringSeconds)
    {
        Current = null;
        BeginNext(now, ringSeconds);
    }

    public void StopAll()
    {
        _queue.Clear();
        Current = null;
    }

    /// <summary>响铃时显示的那行提示语。</summary>
    public static string RingingText(Alarm a)
    {
        string who = string.IsNullOrWhiteSpace(a.Label) ? "闹钟" : a.Label;
        return who + "时间到";
    }
}

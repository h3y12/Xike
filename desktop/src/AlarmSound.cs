using System;
using System.IO;
using System.Media;
using System.Windows.Media;

namespace TimeGuard;

/// <summary>
/// 响铃发声。
///
/// 两种音源：
///  · 系统提示音（默认）—— 用 <see cref="SystemSounds"/>，零依赖、绝不会"没声音还查不出原因"。
///  · 自定义音频文件 —— 用 WPF 自带的 <see cref="MediaPlayer"/> 播 mp3/wav/wma。
///
/// **为什么不引 NAudio**：这里只需要"播一个文件、到时间停"，
/// WPF 自带的 MediaPlayer 就够；引第三方库会让发布体积变大，还得跟着它升级。
///
/// 三个容易踩的点，都在下面处理了：
///  ① <see cref="MediaPlayer"/> 是**异步**的，`Play()` 立刻返回，
///     文件不存在或格式不认都不会当场抛 —— 所以路径要先自己查、失败要能回退，
///     否则用户以为设了闹钟，结果静悄悄。
///  ② WPF 没有"循环播放"属性，循环只能在 `MediaEnded` 里再 `Play()` 一次。
///  ③ 系统提示音是**一声短音**，不会自己循环；要连续响得由外层定时器反复敲。
/// </summary>
public sealed class AlarmSoundPlayer : IDisposable
{
    private MediaPlayer? _player;
    private bool _looping;
    private bool _disposed;
    private string? _currentPath;
    private readonly Action _playSystemSound;

    public AlarmSoundPlayer() : this(() => SystemSounds.Exclamation.Play()) { }
    internal AlarmSoundPlayer(Action playSystemSound) => _playSystemSound = playSystemSound;

    /// <summary>当前是不是正在出声（自定义音频时=播放器在跑；系统音时=本次响铃未结束）。</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>上次播放失败的原因。设置窗口会显示出来，别让用户对着无声的闹钟猜。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>自定义音频是否可用（路径非空、文件在）。</summary>
    public static bool IsUsableFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// 开始响。<paramref name="soundPath"/> 为空/文件不存在/格式不认时，自动退回系统提示音。
    /// </summary>
    public void Start(string? soundPath, bool loop = true)
    {
        Stop();
        LastError = string.Empty;
        _disposed = false;
        _looping = loop;
        _currentPath = soundPath;

        if (IsUsableFile(soundPath))
        {
            try
            {
                _player = new MediaPlayer();
                _player.MediaEnded += OnMediaEnded;
                _player.MediaFailed += OnMediaFailed;
                _player.Open(new Uri(Path.GetFullPath(soundPath!)));
                _player.Play();
                IsPlaying = true;
                return;
            }
            catch (Exception ex)
            {
                LastError = "音频播放器起不来：" + ex.Message;
                // 掉下去用系统音，至少能叫醒人
            }
        }
        else if (!string.IsNullOrWhiteSpace(soundPath))
        {
            LastError = "找不到音频文件：" + soundPath;
        }

        FallbackToSystem();
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (!_looping || _disposed || _player is null) return;
        try
        {
            // WPF 没有"循环播放"开关，只能在播完那一刻再点一次
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch { /* 播放器已释放，忽略 */ }
    }

    private void FallbackToSystem()
    {
        ReleasePlayer();
        IsPlaying = true;                 // 由外层定时器决定什么时候停
        PingSystemSound();
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _player)) return;
        LastError = "这个音频文件放不出来：" + (e.ErrorException?.Message ?? "格式不支持");
        FallbackToSystem();
    }

    /// <summary>
    /// 敲一声系统提示音。自定义音频在播时是空操作（它在自己循环，不用管）。
    /// 外层响铃控制器每一两秒调一次，就得到"持续响"的效果。
    /// </summary>
    public void PingSystemSound()
    {
        if (!IsPlaying || _player is not null) return;
        try { _playSystemSound(); } catch { /* 没声卡也不能崩 */ }
    }

    public void Stop()
    {
        IsPlaying = false;
        ReleasePlayer();
    }

    private void ReleasePlayer()
    {
        MediaPlayer? p = _player;
        _player = null;
        if (p is not null)
        {
            try
            {
                p.MediaEnded -= OnMediaEnded;
                p.MediaFailed -= OnMediaFailed;
                p.Stop();
                p.Close();
            }
            catch { /* 关不掉也不能崩 */ }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

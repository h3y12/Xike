using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace TimeGuard;

public partial class MainWindow : Window
{
    /// <summary>
    /// 挂件的逻辑宽度（DIP）。**窗口宽度只有这一个来源**：XAML 里用 {x:Static} 指过来，
    /// 运行期由 <see cref="EnsureWindowSize"/> 每帧核对，漂了就钉回来。
    ///
    /// 为什么必须能钉：2026-09-28 报的问题 —— 本地 100%、远程 150% 时 DPI 一切换，
    /// 窗口的物理宽度就停在旧值（实测 177px，而 183×1.5 应该是 275px），
    /// 内容却按 1.5 倍渲染，于是「时间到！」左右各被切掉约 20px、小字折行、
    /// 输入框和两个按钮叠在一起。反推能对得很准：177 ÷ 1.5 = 118 ——
    /// WPF 把当时那份物理宽度按新 DPI 反算回了 Width，而 118 × 1.5 又正好 = 177，
    /// 一旦掉进去就自我锁定、再也长不回来。
    /// </summary>
    public const double CardWidth = 183;

    // ---- Win32 ----
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const int WM_HOTKEY = 0x0312;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;
    private const int HOTKEY_CLICKTHROUGH = 0x5A31;
    private const int HOTKEY_MINIMIZE = 0x5A32;
    private const int HOTKEY_CD_TOGGLE = 0x5A33;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_L = 0x4C;
    private const uint VK_M = 0x4D;
    private const uint VK_H = 0x48;
    private const uint VK_T = 0x54;
    private const uint VK_K = 0x4B;
    private const int VK_CONTROL = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>客户区的**物理像素**尺寸。分层窗口没有非客户区，所以它就等于整窗尺寸。</summary>
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 状态 ----
    private static readonly Brush BrushCdSub = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BrushCdError = new SolidColorBrush(Color.FromRgb(0xFF, 0xB1, 0x74));

    // ---- 闹钟配色 ----
    // 「生效 = 亮字，不生效 = 浅灰字」是用户明确要求的判据。
    // 灰的那档要真的看得出"这是不生效的"，所以明度差拉得比一般的次要文字更大。
    private static readonly Brush BrushAlarmOn = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BrushAlarmOff = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BrushAlarmSub = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RingingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB1, 0x74));
    private static readonly Brush AlarmChipBg = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RingingChipBg = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xB1, 0x74));

    private readonly Dictionary<int, SolidColorBrush> _scoreBrushCache = new();
    private readonly Dictionary<uint, SolidColorBrush> _cdBrushCache = new();

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private AppConfig _cfg = new();
    private SleepRules _rules = SleepRules.Default;
    private readonly Countdown _cd = new();

    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _trayClickThrough;
    private WinForms.ToolStripMenuItem? _trayShowScore;
    private WinForms.ToolStripMenuItem? _trayShowCd;
    private WinForms.ToolStripMenuItem? _trayCdToggle;
    private WinForms.ToolStripMenuItem? _trayShowAlarm;
    private WinForms.ToolStripMenuItem? _trayAlarmList;
    private SettingsWindow? _settings;

    // ---- 闹钟 ----
    private AlarmBook _alarms = new();
    private readonly AlarmRinger _ringer = new();
    private readonly AlarmSoundPlayer _sound = new();
    private AlarmWindow? _alarmWindow;
    private RingWindow? _ringWindow;

    /// <summary>上一次把闹钟块画出来的内容签名。变了才重建 UI —— 每 200ms 重建一次会闪。</summary>
    private string _alarmUiSignature = string.Empty;

    private bool _userMinimized;
    private bool _ready;

    /// <summary>最近一次「单击托盘图标把窗口叫出来」的时刻，用于双击保护带。</summary>
    private DateTime _trayWokeAt = DateTime.MinValue;
    private bool _suppressTrayEvent;
    private bool _minHotkeyOk;
    private bool _cdHotkeyOk;
    private int _minHotkeyError;
    private int _cdHotkeyError;
    private string _minHotkeyDesc = "Ctrl+Alt+M 最小化 / 恢复";
    private string _cdHotkeyDesc = "Ctrl+Alt+T 开始 / 暂停倒计时";
    private bool _appliedTransparent;
    private bool _cdSuppressText;
    private string _cdError = string.Empty;
    private bool _countdownEditing;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <param name="forceClickThrough">
    /// 仅自检用：强制开着鼠标穿透，用来验证 --uitest 确实能甄别出「点不进去」这种情况。
    /// </param>
    public MainWindow(bool forceClickThrough = false)
    {
        InitializeComponent();

        _cfg = AppConfig.Load();
        if (forceClickThrough) _cfg.ClickThrough = true;
        _rules = _cfg.Rules;
        _cfg.RestoreCountdown(_cd, DateTime.Now);

        Opacity = _cfg.Opacity;
        MenuClickThrough.IsChecked = _cfg.ClickThrough;
        MenuHideScore.IsChecked = _cfg.ScoreHidden;
        MenuHideCountdown.IsChecked = _cfg.CountdownHidden;
        MenuHideAlarm.IsChecked = _cfg.AlarmPanelHidden;

        _alarms = _cfg.BuildAlarmBook();

        SetInputText(_cd.InputText);

        // 兜底：点击进了窗口但窗口没被激活时，键盘焦点进不来（光标会闪，字打不进去）。
        // WM_MOUSEACTIVATE 已经在消息层拦了一道，这里再保证一次。
        bool reActivating = false;
        CdInput.GotKeyboardFocus += (_, _) =>
        {
            if (IsActive || reActivating) return;
            reActivating = true;
            try
            {
                Activate();
                CdInput.Focus();
            }
            finally
            {
                reActivating = false;
            }
        };

        Loaded += MainWindow_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyClickThrough();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // 全局快捷键（穿透开着时鼠标点不到窗口，这几个是键盘逃生口）
        // 最小化是唯一的「把窗口收回/叫出来」的键盘口子，被占用时必须退一个组合，不能就这么没了
        _minHotkeyOk = RegisterHotKey(_hwnd, HOTKEY_MINIMIZE, MOD_CONTROL | MOD_ALT, VK_M);
        if (_minHotkeyOk)
        {
            _minHotkeyDesc = "Ctrl+Alt+M 最小化 / 恢复";
        }
        else
        {
            _minHotkeyError = Marshal.GetLastWin32Error();

            _minHotkeyOk = RegisterHotKey(_hwnd, HOTKEY_MINIMIZE, MOD_CONTROL | MOD_ALT, VK_H);
            _minHotkeyDesc = _minHotkeyOk
                ? "Ctrl+Alt+H 最小化 / 恢复（Ctrl+Alt+M 被别的程序占了）"
                : "最小化快捷键注册失败（错误码 " + _minHotkeyError + "），请用托盘菜单";
        }

        // 倒计时同理：Ctrl+Alt+T 在不少机器上被别的程序占着（本机就是），
        // 退到 Ctrl+Alt+K，免得托盘菜单里写着一个按不动的键。
        _cdHotkeyOk = RegisterHotKey(_hwnd, HOTKEY_CD_TOGGLE, MOD_CONTROL | MOD_ALT, VK_T);
        if (_cdHotkeyOk)
        {
            _cdHotkeyDesc = "Ctrl+Alt+T 开始 / 暂停倒计时";
        }
        else
        {
            _cdHotkeyError = Marshal.GetLastWin32Error();

            _cdHotkeyOk = RegisterHotKey(_hwnd, HOTKEY_CD_TOGGLE, MOD_CONTROL | MOD_ALT, VK_K);
            _cdHotkeyDesc = _cdHotkeyOk
                ? "Ctrl+Alt+K 开始 / 暂停倒计时（Ctrl+Alt+T 被别的程序占了）"
                : "倒计时快捷键注册失败（错误码 " + _cdHotkeyError + "），请用托盘菜单";
        }

        RegisterHotKey(_hwnd, HOTKEY_CLICKTHROUGH, MOD_CONTROL | MOD_ALT, VK_L);

        BuildTray();
        RefreshAlarmTrayMenu();     // 托盘里那份"全部闹钟"要按刚读进来的配置填
        HookDisplaySettings();
    }

    // ---- 分辨率 / 缩放变化的应对 ----
    // 桌面版过去对这两件事完全没有处理：启动时夹一次位置就再也不管了。
    // 后果是「把窗口从 1080P 拖到 4K 屏」「改了系统缩放」「拔掉副屏」之后，
    // 窗口可能按旧 DPI 的尺寸绘制（内容被裁）或者停在已经不存在的位置上。

    private bool _displayHooked;

    /// <summary>
    /// 订阅「显示设置改变」（改分辨率、插拔显示器、改缩放）。
    /// 该事件在非 UI 线程触发，必须切回 Dispatcher 再动窗口。
    /// </summary>
    private void HookDisplaySettings()
    {
        if (_displayHooked) return;
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displayHooked = true;
        }
        catch
        {
            // 个别精简环境拿不到 SystemEvents；退化成只靠 OnDpiChanged 纠正即可，不影响主流程
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(ClampToScreen));
    }

    /// <summary>
    /// 跨显示器拖动、或系统缩放变化时触发。三件事都不能省：
    /// ① **把尺寸钉回唯一来源**（<see cref="EnsureWindowSize"/>）—— 这一步以前缺，
    ///    于是 100%→150% 切换后窗口还是旧的物理宽度，而内容按 1.5 倍渲染，字全被裁；
    /// ② 让内容按新 DPI 重新测量（分层窗口不会自己重排，留着会沿用旧尺寸绘制）；
    /// ③ 新显示器的 DIP 工作区可能更小，原有位置可能越界 —— 重新夹一次。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        LogSize($"DPI 变化 {oldDpi.DpiScaleX:0.###} → {newDpi.DpiScaleX:0.###}"
            + $"（Width={Width:0.##}，物理={PhysicalWidth()}px，"
            + $"应为 {WindowPlacement.ExpectedPhysicalWidth(CardWidth, newDpi.DpiScaleX)}px）");

        EnsureWindowSize();
        InvalidateMeasure();
        UpdateLayout();
        ClampToScreen();

        // WPF 内部对这个切换的处理是异步的：给它几帧稳定下来之后再核对一次，
        // 否则可能出现「这一刻看着对了、半秒后又漂回去」。
        var recheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        recheck.Tick += (_, _) =>
        {
            recheck.Stop();
            EnsureWindowSize();
            ClampToScreen();
        };
        recheck.Start();
    }

    /// <summary>
    /// 把窗口尺寸钉回唯一来源：**逻辑宽度 = <see cref="CardWidth"/>，物理宽度 = 它 × 当前 DPI**。
    ///
    /// 为什么是每帧核对而不是只在 OnDpiChanged 里做一次：远程桌面会话建立、改缩放、
    /// 跨屏拖动这些动作，WPF 把「物理尺寸 ↔ DIP 属性」换算错的时机并不固定在某一刻。
    /// 代价是两次比较（一个 double + 一次 GetClientRect），正常运行时一个分支都不会进。
    ///
    /// 两条判断分开做，① 纠正后直接返回 —— 让 WPF 先按新宽度把 HWND 重设一遍，
    /// 下一帧再判物理尺寸，免得同一次里拿着还没更新的值误判。
    /// </summary>
    private void EnsureWindowSize()
    {
        // ① 逻辑宽度：唯一来源是 CardWidth。漂了就钉回去（WPF 会按当前 DPI 同步重设 HWND）。
        //
        // 容差取 1 DIP 而不是 0.5：SizeToContent="Height" 会让 WPF 把 client 宽度反算回 Width，
        // 于是稳定状态下 Width 会是 183.33（= 275px ÷ 1.5）这种带小数的值 —— 实测过。
        // 容差太小就会每帧都判成"漂了"、每帧调一次 Win32，反而抖。
        // 真正的漂移是 65 DIP（183 → 118）这个量级，1 DIP 的容差不会漏。
        if (Math.Abs(Width - CardWidth) > 1.0)
        {
            double bad = Width;
            Width = CardWidth;
            LogSize($"逻辑宽度漂移已纠正：{bad:0.##} DIP → {CardWidth:0.#} DIP"
                + $"（dpi={CurrentDpi():0.###}，纠正后物理应为 {WindowPlacement.ExpectedPhysicalWidth(CardWidth, CurrentDpi())}px）");
            return;
        }

        // ② 物理宽度：无边框 + 分层窗口（WindowStyle=None + AllowsTransparency=True）在 DPI
        //    变化后不一定会自己跟上。这时 Width 属性是对的、HWND 却还是旧尺寸 —— 用 Win32 补。
        if (_hwnd == IntPtr.Zero) return;
        if (!GetClientRect(_hwnd, out RECT r)) return;

        double dpi = CurrentDpi();
        int want = WindowPlacement.ExpectedPhysicalWidth(CardWidth, dpi);   // 取整口径与自检共用一份
        int have = r.Right - r.Left;
        if (Math.Abs(have - want) <= 2) return;       // 2px 容差：圆角与取整

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, want, r.Bottom - r.Top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        LogSize($"物理宽度漂移已纠正：{have}px → {want}px（{CardWidth:0.#} DIP × {dpi:0.###}）");
    }

    /// <summary>当前窗口所在显示器的缩放。取不到就按 1:1 —— 宁可少纠一次也不抛。</summary>
    private double CurrentDpi()
    {
        try { return VisualTreeHelper.GetDpi(this).DpiScaleX; }
        catch { return 1.0; }
    }

    /// <summary>
    /// 自检用：把窗口宽度故意改成一个错的值，模拟「DPI 切换后宽度漂走」。
    /// 现场那个坏值是 118 DIP（= 177px ÷ 1.5），见 <see cref="CardWidth"/> 的注释。
    /// </summary>
    internal void PerturbWidthForTest(double badDipWidth) => Width = badDipWidth;

    /// <summary>自检用：窗口当前的逻辑宽度（DIP）。</summary>
    internal double WindowWidth => Width;

    /// <summary>自检用：当前窗口所在显示器的缩放。</summary>
    internal double CurrentDpiForTest => CurrentDpi();

    /// <summary>自检用：当前 HWND 的**物理**宽度（px）。</summary>
    internal int WindowPhysicalWidthForTest => PhysicalWidth();

    /// <summary>当前 HWND 的物理宽度（px）。只在日志里用，取不到给 0。</summary>
    private int PhysicalWidth()
        => _hwnd != IntPtr.Zero && GetClientRect(_hwnd, out RECT r) ? r.Right - r.Left : 0;

    /// <summary>
    /// 把尺寸相关的事件记一行。只写系统临时目录，不进工程、不碰用户配置 ——
    /// 这个漂移只在「别的机器 / 别的缩放」上出现，本机 100% 复现不了，
    /// 留一份现场记录，下次再出问题不用从头猜。
    /// </summary>
    private static void LogSize(string line)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TimeGuard-dpi.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 64 * 1024) return;    // 别无限长
            File.AppendAllText(path,
                DateTime.Now.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "  " + line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 记不上也不影响使用 */ }
    }

    /// <summary>
    /// 枚举所有显示器的**工作区**（已扣掉任务栏），换算成 DIP。
    ///
    /// 为什么不用 `SystemParameters.VirtualScreen*`：那是所有屏幕的**外接矩形**，
    /// 两块并排的屏拼出来的矩形，中间那条缝在物理上不存在。用它夹取，窗口会停在缝上。
    /// 这里逐块屏给出矩形，交给 <see cref="WindowPlacement.ClampToNearest"/> 先挑屏再夹。
    ///
    /// 主屏放第一个：距离相同时优先留在主屏，不会因为枚举顺序变化而漂到副屏。
    /// </summary>
    private static IReadOnlyList<WindowPlacement.ScreenRect> ScreenWorkAreas()
    {
        var list = new List<WindowPlacement.ScreenRect>();

        try
        {
            WinForms.Screen[] all = WinForms.Screen.AllScreens;
            Array.Sort(all, (a, b) => b.Primary.CompareTo(a.Primary));   // 主屏在前

            foreach (WinForms.Screen s in all)
            {
                System.Windows.Forms.Screen scr = s;
                Drawing.Rectangle r = scr.WorkingArea;

                // System.Windows.Forms.Screen 给的是**物理像素**，而 WPF 的 Left/Top 是 DIP。
                // 按这块屏自己的缩放比例换算；拿不到就按 1:1 兜底（宁可差一点也别抛）。
                double sx = 1, sy = 1;
                try
                {
                    var src = PresentationSource.FromVisual(Application.Current?.MainWindow);
                    if (src?.CompositionTarget is not null)
                    {
                        sx = src.CompositionTarget.TransformFromDevice.M11;
                        sy = src.CompositionTarget.TransformFromDevice.M22;
                    }
                }
                catch { /* 取不到缩放就按 1:1 */ }

                list.Add(new WindowPlacement.ScreenRect(r.Left * sx, r.Top * sy, r.Width * sx, r.Height * sy));
            }
        }
        catch
        {
            // 枚举失败（无桌面、权限异常等）：退化成「一块虚拟桌面」，
            // 行为与改动前一致，绝不因此让窗口摆不出去。
            list.Clear();
            list.Add(new WindowPlacement.ScreenRect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        }

        return list;
    }

    /// <summary>
    /// 按当前虚拟桌面把窗口夹回可视范围。
    /// 只在环境变化时调用（启动、改分辨率、改缩放），不会打扰用户自己摆好的位置。
    /// </summary>
    private void ClampToScreen()
    {
        if (!_ready) return;

        double w = Width;
        double h = WindowPlacement.UsableHeight(Height);

        (double l, double t) = WindowPlacement.ClampToNearest(Left, Top, w, h, ScreenWorkAreas());

        if (Math.Abs(l - Left) > 0.5 || Math.Abs(t - Top) > 0.5)
        {
            Left = l;
            Top = t;
            SaveConfig();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        _ready = true;
        SaveConfig();   // 首次运行即落下配置文件，位置/透明度/穿透状态后续都能对上

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveConfig(); };
        LocationChanged += (_, _) => { _saveDebounce.Stop(); _saveDebounce.Start(); };

        ListenForSecondLaunch();

        // Loaded 在首帧渲染前触发，这里状态判断能与 Show() 落在同一帧，不会闪一下
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        _saveDebounce.Stop();
        PersistCountdown();
        SaveConfig();

        // 闹钟：停声音、把闹钟状态落盘、关掉附属窗口。
        // 顺序要紧：先停声音再关窗，否则可能在关窗那一刻又响一声。
        try { _sound.Stop(); _sound.Dispose(); } catch { /* 退出路径，出错也不能卡住 */ }
        _ringer.StopAll();
        try { _ringWindow?.Close(); } catch { /* 已经关了 */ }
        _ringWindow = null;
        try { _alarmWindow?.Close(); } catch { /* 已经关了 */ }
        _alarmWindow = null;
        SaveAlarms();

        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HOTKEY_CLICKTHROUGH);
            UnregisterHotKey(_hwnd, HOTKEY_MINIMIZE);
            UnregisterHotKey(_hwnd, HOTKEY_CD_TOGGLE);
        }

        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }

        if (_displayHooked)
        {
            try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; }
            catch { /* 退订失败不影响退出 */ }
            _displayHooked = false;
        }

        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 无边框 + ShowInTaskbar=False + ToolWindow 这类窗口，点一下默认不会把自己激活，
        // 键盘焦点进不来 → 表现是「鼠标能点到输入框，但敲键盘没反应」。
        // 直接回 MA_ACTIVATE，把激活拿到手。
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_ACTIVATE);
        }

        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HOTKEY_CLICKTHROUGH:
                    SetClickThrough(!_cfg.ClickThrough);
                    handled = true;
                    break;
                case HOTKEY_MINIMIZE:
                    ToggleMinimize();
                    handled = true;
                    break;
                case HOTKEY_CD_TOGGLE:
                    ToggleCountdown();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>再次双击 exe 时，由第二个实例发信号，把窗口叫出来（而不是再开一个）。</summary>
    private void ListenForSecondLaunch()
    {
        EventWaitHandle? signal = App.ShowSignal;
        if (signal is null) return;

        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!signal.WaitOne()) return;
                }
                catch
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _userMinimized = false;
                    Refresh();

                    // 两个块都被手动收起时，光「恢复窗口」等于什么都没发生，顺手把倒计时叫出来
                    if (!IsVisible) SummonCountdown();
                    else { Topmost = false; Topmost = true; Activate(); }
                }));
            }
        })
        { IsBackground = true, Name = "timeguard-show-signal" };
        t.Start();
    }

    // ---- 主循环 ----
    private void Refresh()
    {
        // 每帧核对一次窗口尺寸（漂了就钉回去），必须放在最前面：
        // 宽度不对的话，下面量出来的每一块高度也都是错的。
        EnsureWindowSize();

        DateTime now = DateTime.Now;
        ScoreState score = ScoreEngine.Evaluate(now, _rules);
        CountdownState cd = _cd.Tick(now);

        bool daytime = score.Phase == NightPhase.Daytime;

        // 分数块：时段（06:30–20:30 自动收起）与手动收起二者取交集
        bool scoreShow = !_cfg.ScoreHidden && !daytime;

        // Empty/finished timers stay hidden; explicitly opening the editor is temporary.
        bool cdShow = !_cfg.CountdownHidden
            && (cd.HasActiveTime || _countdownEditing);

        // 闹钟块：**只在半小时内会有闹钟响时才显示**（用户要求的）。
        // 这样平时挂件不长出这一块；临近了自动浮出来提醒你。
        // 响铃过程中也强制显示 —— 否则响完了用户看不到是哪个闹钟。
        bool alarmShow = !_cfg.AlarmPanelHidden
            && (_ringer.IsRinging || _alarms.Imminent(now).Count > 0);

        bool shouldShow = (scoreShow || cdShow || alarmShow) && !_userMinimized;

        ApplyClickThrough();

        // 闹钟的到期与响铃：每帧都查一遍（200ms），错过不补的逻辑在 AlarmBook 里
        TickAlarms(now);

        if (shouldShow && !IsVisible) Show();
        else if (!shouldShow && IsVisible) Hide();

        SetPanelVisibility(scoreShow, cdShow, alarmShow);

        if (!shouldShow)
        {
            UpdateWindowHeight(scoreShow, cdShow, alarmShow);
            UpdateBtnRow();
            UpdateTrayText(score, cd, daytime);
            return;
        }

        ApplyScoreState(now, score);
        ApplyCdState(cd);

        // ⚠️ 顺序要紧：先把闹钟方块填进 AlarmList，再算窗口高度。
        // 反过来量的是"空的闹钟块"，最后几个闹钟会被窗口裁掉（快照里实测到过）。
        ApplyAlarmState(now);
        UpdateWindowHeight(scoreShow, cdShow, alarmShow);
        UpdateBtnRow();
        UpdateTrayText(score, cd, daytime);
    }

    private void SetPanelVisibility(bool scoreShow, bool cdShow, bool alarmShow)
    {
        ScorePanel.Visibility = scoreShow ? Visibility.Visible : Visibility.Collapsed;
        CdPanel.Visibility = cdShow ? Visibility.Visible : Visibility.Collapsed;
        AlarmPanel.Visibility = alarmShow ? Visibility.Visible : Visibility.Collapsed;

        // 细线只在"上下都有块"时才画，否则会露出一条孤零零的横线
        int shown = (scoreShow ? 1 : 0) + (cdShow ? 1 : 0) + (alarmShow ? 1 : 0);
        Divider.Visibility = shown >= 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前显示的面板把窗口高度算准。
    /// 不用 SizeToContent="Height"：那样窗口会先按一个中间尺寸创建再改，分层窗口会留下残影。
    /// 倒计时那行小字、闹钟列表都可能换行或增减行数，所以高度要按实际测量值算，不能写死。
    /// </summary>
    private void UpdateWindowHeight(bool scoreShow, bool cdShow, bool alarmShow)
    {
        const double border = 1;                       // Root 的 BorderThickness
        // 用常量而不是 Width 属性：万一宽度正在漂，高度也要按「应该的宽度」量，
        // 不能跟着一起错 —— 两个方向错法不同，叠起来更难查。
        double contentWidth = CardWidth - 2 * border;  // StackPanel 拿到的可用宽度

        double h = 2 * border;

        if (scoreShow)
        {
            ScorePanel.Measure(new Size(contentWidth, double.PositiveInfinity));
            h += ScorePanel.DesiredSize.Height;
        }

        if (cdShow)
        {
            CdPanel.Measure(new Size(contentWidth, double.PositiveInfinity));
            h += CdPanel.DesiredSize.Height;           // DesiredSize 已含 Margin
        }

        if (alarmShow)
        {
            AlarmPanel.Measure(new Size(contentWidth, double.PositiveInfinity));
            h += AlarmPanel.DesiredSize.Height;
        }

        // 有几个块就有几条分隔线（块之间各一条）
        int shown = (scoreShow ? 1 : 0) + (cdShow ? 1 : 0) + (alarmShow ? 1 : 0);
        if (shown >= 2) h += shown - 1;

        if (Math.Abs(Height - h) > 0.5) Height = h;    // 只在真的变了才赋值，免得反复触发重排
    }

    /// <summary>
    /// 闹钟的到期检查与响铃推进。每帧调一次。
    ///
    /// 时间窗口取「上一次检查到现在」：这样即使某次刷新被卡了几秒，
    /// 中间那一刻的闹钟也不会被漏掉（但超过 90 秒的仍然按"错过不补"处理）。
    /// </summary>
    private void TickAlarms(DateTime now)
    {
        DateTime from = _lastAlarmCheck == DateTime.MinValue ? now.AddMilliseconds(-250) : _lastAlarmCheck;

        // 先让响铃状态机走一步（到点自动静音、按间隔敲系统音）
        bool justStopped = _ringer.Tick(now, _sound, _cfg.AlarmRingSeconds);
        if (justStopped)
        {
            if (_ringer.IsRinging) StartRinging(now);
            else CloseRingWindowIfIdle();
        }

        // 弹窗上那行"还有几秒"每帧都要变，所以单独刷（其他文字只在换闹钟时才变）
        if (_ringWindow is not null && _ringer.Current is RingingState cur) _ringWindow.UpdateCountdown(cur);

        List<AlarmFiring> due = _alarms.DueBetween(from, now);
        if (due.Count > 0)
        {
            bool wasRinging = _ringer.IsRinging;
            foreach (AlarmFiring firing in due) _alarms.ConsumeOccurrence(firing.Alarm);
            SaveAlarms();
            _ringer.Accept(due, now, _cfg.AlarmRingSeconds);
            if (!wasRinging) StartRinging(now);
        }

        _lastAlarmCheck = now;
    }

    private DateTime _lastAlarmCheck = DateTime.MinValue;

    /// <summary>开始响一个闹钟：发声 + 弹窗。</summary>
    private void StartRinging(DateTime now)
    {
        if (_ringer.Current is null) return;

        _sound.Start(_cfg.AlarmSoundPath, loop: true);

        if (_cfg.AlarmBringToFront)
        {
            try
            {
                if (!IsVisible) Show();
                Topmost = true;
            }
            catch { /* 顶不动也不能崩 */ }
        }

        ShowRingWindow(_ringer.Current);
    }

    /// <summary>把闹钟状态刷到挂件里的闹钟块。</summary>
    private void ApplyAlarmState(DateTime now)
    {
        // ---- 响铃时：这一块变成"正在响"的提示 ----
        if (_ringer.Current is RingingState rs)
        {
            AlarmNext.Text = rs.SecondsLeft(now).ToString(CultureInfo.InvariantCulture) + " 秒后静音";
            AlarmNext.Foreground = RingingBrush;
        }
        else
        {
            DateTime? next = _alarms.NextImminentRing(now);
            AlarmNext.Text = next is DateTime t ? Alarm.DescribeCountdown(now, t) : string.Empty;
            AlarmNext.Foreground = BrushAlarmSub;
        }

        // ---- 列表：签名不变就不重建（每 200ms 重建控件会闪、也费）----
        string sig = BuildAlarmSignature(now);
        if (sig == _alarmUiSignature) return;
        _alarmUiSignature = sig;

        AlarmList.Children.Clear();
        List<Alarm> show = _alarms.Imminent(now);
        if (show.Count == 0) return;

        foreach (Alarm a in show) AlarmList.Children.Add(BuildAlarmChip(a, now));
    }

    /// <summary>
    /// 闹钟块的"内容签名"：决定要不要重建 UI。
    /// 含开关状态与时间 —— 用户在设置窗口改了开关，这里能立刻反映出来。
    /// **不含"还有多久"**：那个每秒钟都在变，含进去会导致每秒重建一次控件。
    /// </summary>
    private string BuildAlarmSignature(DateTime now)
    {
        var parts = new List<string>();
        foreach (Alarm a in _alarms.Imminent(now))
            parts.Add(a.Id + (a.Enabled ? "1" : "0") + a.TimeText);
        return string.Join("|", parts);
    }

    /// <summary>画一个闹钟方块：只显示时间和开关状态。生效 = 亮字，不生效 = 浅灰字。</summary>
    private Border BuildAlarmChip(Alarm a, DateTime now)
    {
        bool ringingThis = _ringer.Current?.Alarm.Id == a.Id;

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ringingThis ? RingingChipBg : AlarmChipBg,
            Padding = new Thickness(9, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            ToolTip = BuildAlarmTooltip(a, now)
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 时间：生效 = 亮字，不生效 = 浅灰字（用户明确要求的判据）
        var time = new TextBlock
        {
            Text = a.TimeText,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = a.Enabled ? BrushAlarmOn : BrushAlarmOff,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 0);

        var name = new TextBlock
        {
            Text = a.Label,
            FontSize = 11,
            Foreground = a.Enabled ? BrushAlarmSub : BrushAlarmOff,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 6, 0)
        };
        Grid.SetColumn(name, 1);

        // 开关：一个小圆点 + 状态字，点一下就切换，不用开设置窗口
        var toggle = new TextBlock
        {
            Text = a.Enabled ? "开" : "关",
            FontSize = 11,
            Foreground = a.Enabled ? BrushAlarmOn : BrushAlarmOff,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(toggle, 2);

        g.Children.Add(time);
        g.Children.Add(name);
        g.Children.Add(toggle);
        chip.Child = g;

        // 点方块 = 切换开关；正在响的那个，点一下是"知道了"
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (ringingThis) { DismissCurrentRing(DateTime.Now); return; }

            a.Enabled = !a.Enabled;
            _alarmUiSignature = string.Empty;   // 逼下一次刷新重建
            SaveAlarmsSoon();
            ApplyAlarmState(DateTime.Now);
        };

        return chip;
    }

    private static string BuildAlarmTooltip(Alarm a, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append(a.TimeText);
        if (!string.IsNullOrWhiteSpace(a.Label)) sb.Append(" · ").Append(a.Label);
        sb.Append('\n').Append(a.RepeatText);
        if (a.ChainId.Length > 0)
            sb.Append("\n第 ").Append(a.ChainIndex).Append(" 个（连锁起床闹钟）");
        if (a.Repeat == AlarmRepeat.Once && a.DeleteAfterRing) sb.Append("\n响过之后自动删除");
        DateTime? next = a.NextRingAfter(now);
        if (next is DateTime t && a.Enabled) sb.Append('\n').Append(Alarm.DescribeCountdown(now, t));
        if (!a.Enabled) sb.Append("\n（已关闭，不会响）");
        sb.Append("\n单击切换开关");
        return sb.ToString();
    }

    /// <summary>用户处理掉当前这次响铃。</summary>
    private void DismissCurrentRing(DateTime now)
    {
        RingingState? rs = _ringer.Current;
        if (rs is null) return;

        _sound.Stop();
        string message = _alarms.Dismiss(rs.Alarm, now);
        _ringer.DismissCurrent(now, _cfg.AlarmRingSeconds);

        SaveAlarmsSoon();
        _alarmUiSignature = string.Empty;

        if (_ringer.IsRinging) StartRinging(now);
        else CloseRingWindowIfIdle();

        SetTrayBalloon("闹钟", message);
    }

    /// <summary>「稍后提醒」。</summary>
    private void SnoozeCurrentRing(DateTime now)
    {
        RingingState? rs = _ringer.Current;
        if (rs is null) return;

        _sound.Stop();
        string message = _alarms.Snooze(rs.Alarm, now, _cfg.AlarmSnoozeMinutes);
        _ringer.DismissCurrent(now, _cfg.AlarmRingSeconds);

        SaveAlarmsSoon();
        _alarmUiSignature = string.Empty;

        if (_ringer.IsRinging) StartRinging(now);
        else CloseRingWindowIfIdle();

        SetTrayBalloon("闹钟", message);
    }

    /// <summary>把某个时刻的分数状态刷到界面上，返回该时刻的分数（快照模式复用）。</summary>
    internal int ApplyScoreState(DateTime now)
        => ApplyScoreState(now, ScoreEngine.Evaluate(now, _rules));

    private int ApplyScoreState(DateTime now, ScoreState st)
    {
        ScoreText.Text = st.Score.ToString();
        ClockText.Text = now.ToString("HH:mm");

        // 状态文案全部来自规则（设置窗口可改）。留空 = 这一行不显示，此时连
        // 上方的 5px 间距一起收掉，免得空出一块。
        string status = st.Phase switch
        {
            NightPhase.Daytime => $"{SleepRules.FormatHm(_rules.HideAt)}–{SleepRules.FormatHm(_rules.ShowAt)} 不显示",
            NightPhase.Countdown => _rules.StatusBefore,
            _ => ScoreVisual.StatusForScore(st.Score, _rules)
        };

        StatusText.Text = status;
        StatusText.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;

        ScoreText.Foreground = BrushForScore(st.Score);
        return st.Score;
    }

    private void ApplyCdState(CountdownState st)
    {
        if (_cdError.Length > 0)
            SetCdVisual(CountdownPhase.Idle, TimeSpan.Zero, _cdError, isError: true);
        else
            SetCdVisual(st.Phase, st.Remaining, _cd.SubLabel(st.Phase));

        SyncPrimaryButton(st.Phase);
        CdReset.IsEnabled = st.Phase != CountdownPhase.Idle;
        SyncCdMenuItems(st.Phase);
    }

    /// <summary>
    /// 主按钮长什么样：空闲「开始」、运行「暂停」、暂停「继续」、到点「再来」。
    /// <b>「到某个钟点」的倒计时不给暂停，按钮直接收起来</b>——摆一个按不动的按钮
    /// 不如不摆，小字那行会写明「不可暂停」，要停就点旁边的「重置」。
    /// </summary>
    private void SyncPrimaryButton(CountdownPhase phase, bool? pauseAllowed = null)
    {
        bool stoppable = pauseAllowed ?? _cd.PauseAllowed;
        bool running = phase == CountdownPhase.Running;

        CdPrimary.Visibility = running && !stoppable ? Visibility.Collapsed : Visibility.Visible;
        CdPrimary.Content = phase switch
        {
            CountdownPhase.Running => "暂停",
            CountdownPhase.Paused => "继续",
            CountdownPhase.Finished => "再来",
            _ => "开始"
        };
    }

    /// <summary>右键菜单和托盘菜单里的那一项，跟着阶段与「能不能暂停」一起变。</summary>
    private void SyncCdMenuItems(CountdownPhase phase)
    {
        bool clockRunning = phase == CountdownPhase.Running && !_cd.PauseAllowed;

        string label = clockRunning
            ? "倒计时进行中（到点式不可暂停）"
            : phase == CountdownPhase.Running ? "暂停倒计时" : "开始倒计时";

        if (!string.Equals(MenuCdToggle.Header as string, label, StringComparison.Ordinal))
            MenuCdToggle.Header = label;
        MenuCdToggle.IsEnabled = !clockRunning;

        if (_trayCdToggle is null) return;
        if (!string.Equals(_trayCdToggle.Text, label, StringComparison.Ordinal))
            _trayCdToggle.Text = label;
        _trayCdToggle.Enabled = !clockRunning;
    }

    /// <summary>倒计时大字与小字的唯一写入口（界面刷新和快照都走这里，避免两套渲染对不上）。</summary>
    private void SetCdVisual(CountdownPhase phase, TimeSpan remaining, string sub, bool isError = false)
    {
        CdTime.Text = Countdown.BigText(phase, remaining);
        CdTime.Foreground = BrushOf(Countdown.ColorFor(phase, remaining));
        CdLabel.Text = sub;
        CdLabel.Foreground = isError ? BrushCdError : BrushCdSub;
    }

    private void UpdateTrayText(ScoreState score, CountdownState cd, bool daytime)
    {
        if (_tray is null) return;

        string head = daytime || _cfg.ScoreHidden
            ? "早睡健康分已收起"
            : $"早睡健康分 {score.Score}";

        string tail = cd.Phase switch
        {
            CountdownPhase.Running => " · 倒计时 " + Countdown.Hms(cd.Remaining),
            CountdownPhase.Paused => " · 倒计时已暂停 " + Countdown.Hms(cd.Remaining),
            CountdownPhase.Finished => " · 倒计时已到点",
            _ => _cfg.CountdownHidden ? " · 倒计时已收起" : string.Empty
        };

        _tray.Text = head + tail;
    }

    private SolidColorBrush BrushForScore(int score)
    {
        if (_scoreBrushCache.TryGetValue(score, out SolidColorBrush? cached)) return cached;

        var b = new SolidColorBrush(ScoreVisual.ColorForScore(score));
        b.Freeze();
        _scoreBrushCache[score] = b;
        return b;
    }

    private SolidColorBrush BrushOf(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (_cdBrushCache.TryGetValue(key, out SolidColorBrush? cached)) return cached;

        var b = new SolidColorBrush(c);
        b.Freeze();
        _cdBrushCache[key] = b;
        return b;
    }

    // ---- 倒计时交互（替代原 C 程序里「敲 0 结束 / 敲 1 重来」）----

    private void CdPrimary_Click(object sender, RoutedEventArgs e) => CdPrimaryAction();

    private void CdReset_Click(object sender, RoutedEventArgs e) => CdResetAction();

    private void MenuCdToggle_Click(object sender, RoutedEventArgs e) => ToggleCountdown();

    private void MenuCdReset_Click(object sender, RoutedEventArgs e) => CdResetAction();

    /// <summary>主按钮：空闲→开始；运行→暂停；暂停→继续；到点→再来一次。</summary>
    internal void CdPrimaryAction()
    {
        DateTime now = DateTime.Now;

        switch (_cd.Phase)
        {
            case CountdownPhase.Running:
                // 到点式不接受暂停（Pause 会拒绝），此时按钮本来就是收起来的，这里再兜一道
                if (!_cd.Pause(now)) return;
                break;
            case CountdownPhase.Paused:
                _cd.Resume(now);
                break;
            case CountdownPhase.Finished:
                _cd.Restart(now);
                break;
            default:
                TryStartFromInput(now);
                break;
        }

        if (_cd.Peek(now).HasActiveTime) _countdownEditing = false;
        PersistCountdown();
        Refresh();
    }

    /// <summary>「重置」：回到输入态（相当于原程序里敲 0 结束）。</summary>
    internal void CdResetAction()
    {
        _countdownEditing = false;
        _cd.Reset();
        _cdError = string.Empty;
        PersistCountdown();
        Refresh();
    }

    /// <summary>从输入框起一次倒计时。失败时把错误留在界面上，不静默吞掉。</summary>
    private bool TryStartFromInput(DateTime now)
    {
        _countdownEditing = true;
        if (!Countdown.TryParse(CdInput.Text, now, out CountdownPlan plan, out string err))
        {
            _cdError = err;
            return false;
        }

        _cdError = string.Empty;
        _cd.InputText = CdInput.Text.Trim();
        _cd.Start(plan, now);
        _countdownEditing = false;

        // 手动起过一次就把面板钉住，免得白天收起来找不着
        _cfg.CountdownHidden = false;
        _cfg.CountdownPinned = true;
        SyncMenuChecks();
        return true;
    }

    private void ToggleCountdown()
    {
        DateTime now = DateTime.Now;

        if (_cd.Phase == CountdownPhase.Running)
        {
            // 到点式不可暂停：热键/托盘也不能偷偷把它停住（暂停会让它偏离那个钟点）
            if (!_cd.Pause(now)) return;
        }
        else if (_cd.Phase == CountdownPhase.Paused)
        {
            _cd.Resume(now);
        }
        else if (_cd.Phase == CountdownPhase.Finished)
        {
            _cd.Restart(now);
        }
        else
        {
            // 空闲时按快捷键：把面板叫出来、光标落到输入框
            SummonCountdown();
            return;
        }

        _countdownEditing = false;
        PersistCountdown();
        Refresh();
    }

    /// <summary>从托盘或二次启动唤起倒计时：确保面板可见并把光标放进输入框。</summary>
    internal void SummonCountdown()
    {
        _countdownEditing = !_cd.Peek(DateTime.Now).HasActiveTime;
        _cfg.CountdownHidden = false;
        _cfg.CountdownPinned = true;
        _userMinimized = false;
        SyncMenuChecks();
        PersistCountdown();
        Refresh();

        if (!IsVisible) return;

        Topmost = false;
        Topmost = true;
        Activate();
        CdInput.Focus();
        CdInput.SelectAll();
    }

    /// <summary>托盘里的「15 / 30 / 60 分钟后」快捷开始。</summary>
    internal void StartQuick(int minutes)
    {
        _countdownEditing = false;
        DateTime now = DateTime.Now;

        if (!Countdown.TryParse(minutes.ToString() + "m", now, out CountdownPlan plan, out string err))
        {
            _cdError = err;
            Refresh();
            return;
        }

        _cdError = string.Empty;
        _cd.InputText = minutes.ToString() + "m";
        SetInputText(_cd.InputText);
        _cd.Start(plan, now);

        _cfg.CountdownHidden = false;
        _cfg.CountdownPinned = true;
        SyncMenuChecks();
        PersistCountdown();
        Refresh();
    }

    private void CdInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DateTime now = DateTime.Now;

            if (_cd.Phase == CountdownPhase.Running)
            {
                // 时长模式：回车 = 暂停。到点式不能暂停，改成「按框里的钟点重设」，
                // 重设同一个钟点算出来的截止点还是那个钟点，回车等于没按。
                if (_cd.PauseAllowed) _cd.Pause(now);
                else if (!string.IsNullOrWhiteSpace(CdInput.Text)) TryStartFromInput(now);
            }
            else TryStartFromInput(now);

            PersistCountdown();
            Refresh();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _countdownEditing = false;
            Keyboard.ClearFocus();
            Refresh();
        }
    }

    private void CdInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_cdSuppressText) return;
        if (_cdError.Length == 0) return;

        _cdError = string.Empty;
        Refresh();
    }

    private void SetInputText(string text)
    {
        _cdSuppressText = true;
        CdInput.Text = text ?? string.Empty;
        _cdSuppressText = false;
    }

    private void PersistCountdown()
    {
        _cfg.CaptureCountdown(_cd);
        SaveConfig();
    }

    // ---- 输入链路自检（--uitest）：程序只写状态，鼠标和键盘由外部脚本真实注入 ----
    private string? _uiTestLog;

    /// <summary>
    /// 每 250ms 追加一行：窗口是否激活 / 输入框是否有键盘焦点 / 输入框里是什么 /
    /// 输入框在窗口内的位置与 DPI / 穿透是否真的生效。
    /// 外部脚本据此算出点击坐标 → 真实点击 → 真实敲键 → 再读日志判定。
    /// **这是唯一能验证「鼠标点得进去、键盘打得进去」的办法**：纯逻辑断言、离屏快照、
    /// 直接构造参数调链路，都测不出「鼠标点不到窗口」这种问题（历史教训）。
    /// </summary>
    internal void StartUiTestLog(string path)
    {
        _uiTestLog = path;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        File.WriteAllText(path,
            "TimeGuard --uitest 输入链路自检\n" +
            "dpi=" + dpi.DpiScaleX.ToString("0.###", CultureInfo.InvariantCulture) + "\n" +
            "clickThrough=" + (_cfg.ClickThrough ? 1 : 0) + "\n");

        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        tick.Tick += (_, _) =>
        {
            try
            {
                Point p = CdInput.TranslatePoint(new Point(0, 0), this);
                File.AppendAllText(_uiTestLog!, string.Format(CultureInfo.InvariantCulture,
                    "[{0:HH:mm:ss.fff}] active={1} kbdFocus={2} wsExTransparent={3} text=\"{4}\" inWindow={5:0.#},{6:0.#},{7:0.#},{8:0.#}\n",
                    DateTime.Now,
                    IsActive ? 1 : 0,
                    CdInput.IsKeyboardFocused ? 1 : 0,
                    _appliedTransparent ? 1 : 0,
                    CdInput.Text,
                    p.X, p.Y, CdInput.ActualWidth, CdInput.ActualHeight));
            }
            catch
            {
                // 写日志失败不影响正常使用
            }
        };
        tick.Start();

        // 把测量起点摆正：窗口先激活、输入框先失焦，再由外部脚本点击。
        // 从后台启动的窗口受 Windows 前台锁影响可能一直不被激活，起点不一致会让
        // 「点击后有没有拿到焦点」这个判定失去意义（时好时坏）。
        var prime = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        prime.Tick += (_, _) =>
        {
            prime.Stop();
            Activate();
            Keyboard.ClearFocus();
        };
        prime.Start();

        // 兜底自关：外部脚本异常没能收尾时，也不给用户留一个幽灵窗口
        var kill = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        kill.Tick += (_, _) =>
        {
            kill.Stop();
            Close();
            Application.Current?.Shutdown();
        };
        kill.Start();
    }

    // ---- 快照模式：不上屏，只把界面状态摆好供离屏渲染 ----
    internal void PrepareSnapshot(DateTime time, bool hoverButtons, bool scoreVisible, bool countdownVisible,
        CountdownPhase cdPhase, bool cdPauseAllowed, TimeSpan cdRemaining, string cdSub, string cdInput)
        => PrepareSnapshot(time, hoverButtons, scoreVisible, countdownVisible, cdPhase, cdPauseAllowed,
                           cdRemaining, cdSub, cdInput, alarmVisible: false);

    internal void PrepareSnapshot(DateTime time, bool hoverButtons, bool scoreVisible, bool countdownVisible,
        CountdownPhase cdPhase, bool cdPauseAllowed, TimeSpan cdRemaining, string cdSub, string cdInput,
        bool alarmVisible)
    {
        Opacity = 1.0;
        SetPanelVisibility(scoreVisible, countdownVisible, alarmVisible);

        // ⚠️ 顺序要紧：必须先 ApplyAlarmState（把闹钟方块填进 AlarmList）、
        // 再 UpdateWindowHeight。反过来的话量的是"空的闹钟块"，
        // 算出的窗口高度不够，最后几个闹钟会被裁掉（快照里实测到过）。
        if (alarmVisible) ApplyAlarmState(time);
        UpdateWindowHeight(scoreVisible, countdownVisible, alarmVisible);

        ApplyScoreState(time);
        SetCdVisual(cdPhase, cdRemaining, cdSub);
        SetInputText(cdInput);

        SyncPrimaryButton(cdPhase, cdPauseAllowed);
        CdReset.IsEnabled = cdPhase != CountdownPhase.Idle;

        BtnRow.Opacity = hoverButtons ? 1 : 0;
        BtnRow.IsHitTestVisible = hoverButtons;
    }

    // ---- 窗口行为 ----
    private void RestorePosition()
    {
        double w = Width;
        double h = WindowPlacement.UsableHeight(Height);

        // 没摆过的窗口贴右上角；摆过的沿用保存的 DIP 坐标
        (double defLeft, double defTop) = WindowPlacement.DefaultPosition(
            SystemParameters.WorkArea.Right, SystemParameters.WorkArea.Top, w);

        // 多屏：逐块屏夹取，保证整窗落在**某一块**屏的工作区内（见 ScreenWorkAreas 的说明）
        (double l, double t) = WindowPlacement.ClampToNearest(
            _cfg.Left ?? defLeft,
            _cfg.Top ?? defTop,
            w, h,
            ScreenWorkAreas());

        Left = l;
        Top = t;
    }

    private void SaveConfig()
    {
        if (!_ready) return;
        _cfg.Left = Left;
        _cfg.Top = Top;
        _cfg.Opacity = Opacity;
        _cfg.CaptureAlarms(_alarms);      // 闹钟也一起落盘：窗口位置与闹钟必须是一致的快照
        _cfg.Save();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 未按住左键时 DragMove 会抛，忽略 */ }
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e) => UpdateBtnRow();

    private void Root_MouseLeave(object sender, MouseEventArgs e) => UpdateBtnRow();

    /// <summary>按钮行：鼠标悬停显示；穿透开着时按住 Ctrl 也显示（否则鼠标根本碰不到窗口）。</summary>
    private void UpdateBtnRow()
    {
        bool show = Root.IsMouseOver || (_cfg.ClickThrough && IsCtrlDown());
        double o = show ? 1 : 0;
        if (Math.Abs(BtnRow.Opacity - o) > 0.001) BtnRow.Opacity = o;
        BtnRow.IsHitTestVisible = show;
    }

    private void BtnMin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MinimizeToTray();
    }

    private void BtnClose_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Shutdown();
    }

    private void MenuMinimize_Click(object sender, RoutedEventArgs e) => MinimizeToTray();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void MenuClickThrough_Click(object sender, RoutedEventArgs e) => SetClickThrough(MenuClickThrough.IsChecked);

    private void MenuHideScore_Click(object sender, RoutedEventArgs e) => SetScoreHidden(MenuHideScore.IsChecked);

    private void MenuHideCountdown_Click(object sender, RoutedEventArgs e) => SetCountdownHidden(MenuHideCountdown.IsChecked);

    private void MenuHideAlarm_Click(object sender, RoutedEventArgs e) => SetAlarmHidden(MenuHideAlarm.IsChecked);

    private void MenuAlarmSettings_Click(object sender, RoutedEventArgs e) => OpenAlarmSettings();

    internal void SetAlarmHidden(bool hidden)
    {
        _cfg.AlarmPanelHidden = hidden;
        if (MenuHideAlarm is not null) MenuHideAlarm.IsChecked = hidden;
        if (_trayShowAlarm is not null)
        {
            _suppressTrayEvent = true;
            _trayShowAlarm.Checked = !hidden;
            _suppressTrayEvent = false;
        }
        _alarmUiSignature = string.Empty;
        SaveConfig();
        Refresh();
    }

    /// <summary>
    /// 打开闹钟设置窗口。改完保存的话，把草稿接过来落盘并立刻刷新挂件。
    ///
    /// 用**非模态**（Show）而不是 ShowDialog：闹钟设置是"边看边调"的东西，
    /// 用户在设置里改一个时间，希望挂件那边立刻有反应，而不是被模态框锁住看不了。
    /// </summary>
    internal void OpenAlarmSettings()
    {
        if (_alarmWindow is not null && _alarmWindow.IsLoaded)
        {
            _alarmWindow.Activate();
            return;
        }

        var baseline = _alarms.Items.Select(a => a.Clone()).ToList();
        int originalRingSeconds = _cfg.AlarmRingSeconds;
        var win = new AlarmWindow(_alarms, originalRingSeconds)
        {
            Owner = IsVisible ? this : null
        };
        _alarmWindow = win;

        win.Closed += (_, _) =>
        {
            _alarmWindow = null;

            if (win.Result is AlarmBook edited)
            {
                _alarms.MergeEdits(baseline, edited);
                if (win.RingSeconds != originalRingSeconds && _cfg.AlarmRingSeconds == originalRingSeconds)
                    _cfg.AlarmRingSeconds = win.RingSeconds;
                SaveAlarms();
            }
            _alarmUiSignature = string.Empty;
            Refresh();
        };

        win.Show();
    }

    /// <summary>把当前闹钟写回配置并立即落盘。</summary>
    private void SaveAlarms()
    {
        _cfg.CaptureAlarms(_alarms);
        _cfg.Save();
        RefreshAlarmTrayMenu();
    }

    /// <summary>改了闹钟之后延迟落盘：连点几下开关不必每次都写盘。</summary>
    private void SaveAlarmsSoon()
    {
        _cfg.CaptureAlarms(_alarms);
        _saveDebounce.Stop();
        _saveDebounce.Start();
        RefreshAlarmTrayMenu();
    }

    /// <summary>托盘里的「全部闹钟」子菜单：列出所有闹钟，点一下就切换开关。</summary>
    private void RefreshAlarmTrayMenu()
    {
        if (_trayAlarmList is null) return;

        _trayAlarmList.DropDownItems.Clear();
        if (_alarms.Count == 0)
        {
            _trayAlarmList.DropDownItems.Add(new WinForms.ToolStripMenuItem("（还没有闹钟）") { Enabled = false });
            return;
        }

        foreach (Alarm a in _alarms.Sorted())
        {
            string text = a.TimeText
                + (string.IsNullOrWhiteSpace(a.Label) ? string.Empty : "  " + a.Label)
                + (a.Enabled ? string.Empty : "（已关）");
            var item = new WinForms.ToolStripMenuItem(text)
            {
                CheckOnClick = true,
                Checked = a.Enabled
            };
            Alarm captured = a;
            item.CheckedChanged += (_, _) =>
            {
                if (_suppressTrayEvent) return;
                captured.Enabled = item.Checked;
                _alarmUiSignature = string.Empty;
                SaveAlarmsSoon();
                Refresh();
            };
            _trayAlarmList.DropDownItems.Add(item);
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Shutdown();

    // ---- 响铃弹窗 ----

    /// <summary>
    /// 把响铃弹窗显示出来（已经在显示就更新内容）。
    /// 弹窗是**独立窗口**而不是挂在挂件里：挂件很小、还可能被收起或最小化到托盘，
    /// 闹钟响的时候必须有一个「看得见、点得到」的东西在屏幕上。
    /// </summary>
    private void ShowRingWindow(RingingState state)
    {
        if (_ringWindow is null || !_ringWindow.IsLoaded)
        {
            var w = new RingWindow();
            w.DismissRequested += (_, _) => DismissCurrentRing(DateTime.Now);
            w.SnoozeRequested += (_, _) => SnoozeCurrentRing(DateTime.Now);
            w.Closed += (_, _) => _ringWindow = null;
            _ringWindow = w;
            w.Show();
        }

        _ringWindow.Update(state, _cfg.AlarmSnoozeMinutes);
        try
        {
            if (_cfg.AlarmBringToFront)
            {
                _ringWindow.Activate();
                _ringWindow.Topmost = true;
            }
        }
        catch { /* 顶不动也不能崩 */ }
    }

    /// <summary>没有在响的闹钟了就把弹窗关掉。</summary>
    private void CloseRingWindowIfIdle()
    {
        if (_ringer.IsRinging) return;
        try { _ringWindow?.Close(); } catch { /* 已经关了 */ }
        _ringWindow = null;
    }

    /// <summary>托盘气球提示。用于「已关闭，还有 2 个会继续响」这类反馈 —— 弹窗关掉之后还看得见。</summary>
    internal void SetTrayBalloon(string title, string message)
    {
        try
        {
            if (_tray is null) return;
            _tray.BalloonTipTitle = title ?? string.Empty;
            _tray.BalloonTipText = message ?? string.Empty;
            _tray.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }
        catch { /* 气球提示失败无所谓，别影响主流程 */ }
    }

    // ---- 自检钩子：闹钟 ----

    /// <summary>自检用：直接塞一批闹钟，跳过设置窗口。</summary>
    internal void LoadAlarmsForTest(IEnumerable<Alarm> alarms)
    {
        _lastAlarmCheck = DateTime.MinValue;
        _alarms.Clear();
        foreach (Alarm a in alarms) _alarms.Add(a);
        _alarms.NormalizeAll();
        _alarmUiSignature = string.Empty;
    }

    internal AlarmBook AlarmsForTest => _alarms;

    internal bool AlarmPanelShown => AlarmPanel.Visibility == Visibility.Visible;

    internal int AlarmChipCount => AlarmList.Children.Count;

    internal string AlarmNextText => AlarmNext.Text;

    /// <summary>自检用：把闹钟模块按给定时刻走一遍（不依赖真实时钟）。</summary>
    internal void TickAlarmsForTest(DateTime now) => TickAlarms(now);

    /// <summary>自检用：走一遍「用户点了知道了」。</summary>
    internal void DismissRingForTest(DateTime now) => DismissCurrentRing(now);

    /// <summary>自检用：走一遍「稍后提醒」。</summary>
    internal void SnoozeRingForTest(DateTime now) => SnoozeCurrentRing(now);

    internal bool IsRingingForTest => _ringer.IsRinging;

    internal void MinimizeToTray()
    {
        _userMinimized = true;
        Hide();
        Refresh();
    }

    internal void RestoreFromTray()
    {
        _userMinimized = false;
        Refresh();
        if (IsVisible)
        {
            Activate();
            Topmost = true;
        }
    }

    /// <summary>
    /// 「刚被单击唤醒」的保护期（毫秒）。这段时间里来的双击不再把窗口收回去。
    /// 取值只要够盖住一次双击的间隔即可：Windows 默认双击间隔上限是 500ms。
    /// </summary>
    private const int TrayDoubleClickGuardMs = 500;

    /// <summary>
    /// 单击托盘图标。要的是「把最小化的叫出来」，所以只在窗口本来不在屏幕上时才动手；
    /// 窗口已经在屏幕上时单击不做事，第二下才是「收起」（见 <see cref="TrayIconDoubleClick"/>）。
    /// </summary>
    internal void TrayIconClick()
    {
        if (_userMinimized || !IsVisible) TrayWake();
    }

    /// <summary>
    /// 双击托盘图标 = 把窗口收起来。
    ///
    /// 两种手势在事件层是互相污染的（双击必然先来一次单击），所以这里不靠 WinForms
    /// 的 MouseDoubleClick 去分辨，而是用一条时间保护带：刚被单击唤醒的那 500ms 内，
    /// 双击无效。否则窗口最小化时双击会「先被第一下唤出、立刻被双击收回去」，
    /// 看着像闪了一下什么都没发生。
    /// </summary>
    internal void TrayIconDoubleClick()
    {
        if ((DateTime.UtcNow - _trayWokeAt).TotalMilliseconds < TrayDoubleClickGuardMs) return;
        MinimizeToTray();
    }

    /// <summary>
    /// 把窗口叫出来。
    ///
    /// 用 RestoreFromTray 而不是直接 SummonCountdown：用户收起窗口前的样子该被保留
    /// （他可能只想看分数块）。但白天、或两个块都被收起时，光「恢复窗口」等于什么都没发生
    /// ——这时才退一步把倒计时叫出来。判据与「再次双击 exe」那条路保持一致。
    /// </summary>
    internal void TrayWake()
    {
        _trayWokeAt = DateTime.UtcNow;
        RestoreFromTray();
        if (!IsVisible) SummonCountdown();
    }

    private void ToggleMinimize()
    {
        if (_userMinimized) RestoreFromTray();
        else MinimizeToTray();
    }

    internal bool UserMinimized => _userMinimized;

    internal NightPhase CurrentPhase => ScoreEngine.Evaluate(DateTime.Now, _rules).Phase;

    internal CountdownPhase CurrentCountdownPhase => _cd.Phase;

    internal string CurrentCountdownText => CdTime.Text;

    /// <summary>Ctrl+Alt+M 是否注册成功（被别的程序占用时会失败）。</summary>
    internal bool MinHotkeyRegistered => _minHotkeyOk;

    /// <summary>倒计时快捷键（Ctrl+Alt+T，占用时回退 Ctrl+Alt+K）是否注册成功。</summary>
    internal bool CountdownHotkeyRegistered => _cdHotkeyOk;

    /// <summary>实际生效的倒计时快捷键说明（含自动回退的结果）。</summary>
    internal string CountdownHotkeyDescription => _cdHotkeyDesc;

    /// <summary>托盘图标从哪来的（正常应是内嵌的 Assets/TimeGuard.ico）。</summary>
    internal static string TrayIconSource => _iconSource;

    /// <summary>热键注册失败时的 Win32 错误码（1409 = 已被别的程序占用）。</summary>
    internal int MinHotkeyError => _minHotkeyError;

    internal int CountdownHotkeyError => _cdHotkeyError;

    /// <summary>实际生效的最小化快捷键说明（含自动回退的结果）。</summary>
    internal string MinHotkeyDescription => _minHotkeyDesc;

    // ---- 面板收起 / 展开 ----
    internal void SetScoreHidden(bool hidden)
    {
        _cfg.ScoreHidden = hidden;
        SyncMenuChecks();
        SaveConfig();
        Refresh();
    }

    internal void SetCountdownHidden(bool hidden)
    {
        _countdownEditing = false;
        _cfg.CountdownHidden = hidden;
        if (!hidden) _cfg.CountdownPinned = true;
        SyncMenuChecks();
        SaveConfig();
        Refresh();
    }

    private void SyncMenuChecks()
    {
        MenuHideScore.IsChecked = _cfg.ScoreHidden;
        MenuHideCountdown.IsChecked = _cfg.CountdownHidden;

        _suppressTrayEvent = true;
        if (_trayShowScore is not null) _trayShowScore.Checked = !_cfg.ScoreHidden;
        if (_trayShowCd is not null) _trayShowCd.Checked = !_cfg.CountdownHidden;
        _suppressTrayEvent = false;
    }

    // ---- 面板开关的当前取值（自检要拍快照再还原，别把用户的设置改掉）----
    internal bool ConfigScoreHidden => _cfg.ScoreHidden;

    internal bool ConfigCountdownHidden => _cfg.CountdownHidden;

    internal bool ConfigCountdownPinned => _cfg.CountdownPinned;

    internal bool ScorePanelVisible => ScorePanel.Visibility == Visibility.Visible;

    internal bool CountdownPanelVisible => CdPanel.Visibility == Visibility.Visible;

    internal void RestorePanelFlags(bool scoreHidden, bool countdownHidden, bool countdownPinned)
    {
        _countdownEditing = false;
        _cfg.ScoreHidden = scoreHidden;
        _cfg.CountdownHidden = countdownHidden;
        _cfg.CountdownPinned = countdownPinned;
        SyncMenuChecks();
        SaveConfig();
        Refresh();
    }

    // ---- 自检钩子（--uicheck 用）----

    /// <summary>
    /// 自检用：窗口按钮行（最小化 / 关闭）现在在哪儿、还点不点得到。
    ///
    /// 判据不能用「按钮还在不在」——父级 Collapsed 时它自己的 Visibility 仍写着 Visible。
    /// 必须量**真实布局之后的坐标与尺寸**：父级一收起，这两个数就归零。
    /// 2026-09-22 的 bug 正是这样漏掉的：按钮原本挂在「早睡健康分」块里，那块一收，
    /// 按钮整枝消失，而当时的自检只看两个内容块的显隐，照样报全绿。
    /// </summary>
    internal string ButtonRowReport()
    {
        Point p = BtnRow.TranslatePoint(new Point(0, 0), this);
        double w = BtnRow.ActualWidth;
        double h = BtnRow.ActualHeight;
        double winW = ActualWidth;
        double winH = ActualHeight;

        bool present = BtnRow.Visibility == Visibility.Visible && w > 1 && h > 1;
        bool inside = p.X >= -0.5 && p.Y >= -0.5 && p.X + w <= winW + 0.5 && p.Y + h <= winH + 0.5;
        bool atCorner = p.X + w >= winW - 12 && p.Y <= 4;
        bool ok = present && inside && atCorner;

        return (ok ? "PASS  " : "FAIL  ")
            + "窗口按钮行（最小化/关闭） → 可见=" + (BtnRow.Visibility == Visibility.Visible)
            + "，实测尺寸=" + Fmt(w) + "×" + Fmt(h)
            + "，位置=(" + Fmt(p.X) + "," + Fmt(p.Y) + ")"
            + "，窗口=" + Fmt(winW) + "×" + Fmt(winH)
            + (ok ? "" : "（期望：钉在窗口右上角且尺寸不为 0，即不随任一内容块收起而消失）");
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// 自检用：走一遍「设置窗口填值 → 保存 → 写回配置 → 立即生效」的完整链路。
    /// 只改内存里的配置对象，不落盘，跑完还原，绝不会污染你现有的设置。
    /// </summary>
    internal string SettingsRoundTripCheck(SleepRules candidate)
    {
        SleepRules before = _cfg.Rules;

        try
        {
            var w = new SettingsWindow(_cfg.Rules);
            w.LoadForTest(candidate);
            SleepRules? got = w.SaveForTest();

            if (got is null) return "FAIL  设置窗口点保存后没有返回规则";

            if (SleepRules.FormatHm(got.ShowAt) != SleepRules.FormatHm(candidate.ShowAt)
                || SleepRules.FormatHm(got.DeductAt) != SleepRules.FormatHm(candidate.DeductAt)
                || SleepRules.FormatHm(got.HideAt) != SleepRules.FormatHm(candidate.HideAt)
                || got.MinutesPerPoint != candidate.MinutesPerPoint
                || got.PointsPerStep != candidate.PointsPerStep)
            {
                return "FAIL  设置窗口返回的规则和输入对不上";
            }

            _cfg.ApplyRules(got);
            // ⚠️ 界面渲染读的是 _rules，不是 _cfg.Rules —— 不同步这一步，
            // 配置里明明改好了，屏幕上的状态文字还是旧的（真实路径里也有这一句，
            // 见 MenuSettings_Click；自检钩子漏掉它就会漏检"文案改了不生效"这类问题）。
            _rules = _cfg.Rules;
            SleepRules back = _cfg.Rules;

            bool same = SleepRules.FormatHm(back.ShowAt) == SleepRules.FormatHm(got.ShowAt)
                && SleepRules.FormatHm(back.DeductAt) == SleepRules.FormatHm(got.DeductAt)
                && SleepRules.FormatHm(back.HideAt) == SleepRules.FormatHm(got.HideAt)
                && back.MinutesPerPoint == got.MinutesPerPoint
                && back.PointsPerStep == got.PointsPerStep
                // 四句状态文案也要在
                && back.StatusBefore == got.StatusBefore
                && back.StatusHigh == got.StatusHigh
                && back.StatusMid == got.StatusMid
                && back.StatusLow == got.StatusLow;

            if (!same) return "FAIL  写回配置对象后规则或状态文案对不上";

            // 光比配置字段不算数：得确认那几句话真的刷到界面的状态文字上了。
            // 取「刚开始扣分」这个时刻 —— 那时是满分，落在高档位。
            string onScreen = StatusTextAt(DateTime.Today + candidate.DeductAt);
            if (onScreen != candidate.StatusHigh)
            {
                return "FAIL  界面状态文字没跟上自定义文案（屏幕上是「" + onScreen
                    + "」，期望「" + candidate.StatusHigh + "」）";
            }

            // 「设置窗口真实宽度 = CardWidth」也在这里断言：XAML 里曾经把宽度写死成 396，
            // 于是所有"按常量算可用宽度"的检查全绿，用户真窗口里「分」字被裁掉。
            // 真开一次窗口读 ActualWidth，是唯一能抓住这种漂移的判据。
            string widthCheck = SettingsWindow.WidthCheck();
            if (!widthCheck.StartsWith("PASS", StringComparison.Ordinal))
                return "FAIL  设置窗口实际宽度不等于 CardWidth（真窗口量和常量对不上）：" + widthCheck;

            return "PASS  设置链路通畅：填值 → 保存 → 写回配置 → 立即生效（" + candidate.Describe(DateTime.Today) + "）"
                + "\n     自定义文案也已生效：刚扣分时屏幕上写的是「" + onScreen + "」"
                + "\n     " + widthCheck;
        }
        finally
        {
            _cfg.ApplyRules(before);
            _rules = before;
            Refresh();          // 把界面从"自检时刻"拨回来，别让挂件停在测试状态
        }
    }

    /// <summary>自检用：把某个时刻的状态刷到界面上，返回界面上真实显示的那行字。</summary>
    internal string StatusTextAt(DateTime now)
    {
        ApplyScoreState(now);
        return StatusText.Text;
    }

    /// <summary>
    /// 自检用：倒计时的界面链路——填输入框 → 点按钮 → 读界面上的大字 → 落盘 → 读回。
    /// 跑完全部还原，不会留下一个正在跑的倒计时。
    /// </summary>
    internal string CountdownUiRoundTripCheck()
    {
        DateTime now = DateTime.Now;
        (string Mode, long ClockTicks, long DurationTicks, string Deadline, string Phase, long PausedTicks) keep = _cd.Snapshot();
        string keepInput = _cd.InputText;

        // 起一次倒计时会把「倒计时块收起/白天钉住」这两个开关翻掉，跑完一并还原
        bool keepHidden = _cfg.CountdownHidden;
        bool keepPinned = _cfg.CountdownPinned;

        var sb = new StringBuilder();

        try
        {
            // 1) 输入 45m → 点「开始」→ 大字应为 00:45:00
            _cd.Reset(); // This check must start idle even when the isolated config restored a running timer.
            SetInputText("45m");
            CdPrimaryAction();
            string shown = CdTime.Text;
            sb.Append(shown == "00:45:00" ? "PASS" : "FAIL")
              .Append("  填 45m → 点开始 → 大字显示 ").Append(shown)
              .Append("，小字「").Append(CdLabel.Text).Append("」");

            // 2) 再点一次 → 暂停，按钮应变成「继续」
            CdPrimaryAction();
            bool paused = _cd.Phase == CountdownPhase.Paused;
            sb.Append('\n').Append(paused && (string)CdPrimary.Content == "继续" ? "PASS" : "FAIL")
              .Append("  运行中点按钮 → 状态=").Append(_cd.Phase)
              .Append("，按钮显示「").Append(CdPrimary.Content).Append("」");

            // 3) 继续 → 重置
            CdPrimaryAction();
            bool resumed = _cd.Phase == CountdownPhase.Running;
            CdResetAction();
            sb.Append('\n').Append(resumed && _cd.Phase == CountdownPhase.Idle ? "PASS" : "FAIL")
              .Append("  继续 → ").Append(resumed ? "Running" : "没恢复")
              .Append("；点重置 → ").Append(_cd.Phase).Append("（期望 Idle），大字回到 ").Append(CdTime.Text);

            // 3.5) 到点式（填钟点）：不该给暂停。按钮收起来、状态不被停住、小字写明「不可暂停」
            string clock = now.AddMinutes(90).ToString("HH:mm", CultureInfo.InvariantCulture);
            SetInputText(clock);
            CdPrimaryAction();
            bool clockRun = _cd.Phase == CountdownPhase.Running && !_cd.PauseAllowed;
            sb.Append('\n').Append(clockRun && CdPrimary.Visibility == Visibility.Collapsed ? "PASS" : "FAIL")
              .Append("  填 ").Append(clock).Append("（到点式）→ 状态=").Append(_cd.Phase)
              .Append("，可暂停=").Append(_cd.PauseAllowed ? "是" : "否")
              .Append("，暂停按钮=").Append(CdPrimary.Visibility == Visibility.Collapsed ? "已收起" : "还在")
              .Append("，小字「").Append(CdLabel.Text).Append("」");

            // 就算有漏网的入口再触发一次，也不该把到点式停住（停住就等于改了目标钟点）
            CdPrimaryAction();
            sb.Append('\n').Append(_cd.Phase == CountdownPhase.Running ? "PASS" : "FAIL")
              .Append("  对到点式再触发一次 → 仍是 ").Append(_cd.Phase)
              .Append("，剩余 ").Append(Countdown.Hms(_cd.Peek(DateTime.Now).Remaining))
              .Append("（没被改成暂停）");
            CdResetAction();

            // 4) 非法输入要拦住并给出提示
            SetInputText("abc");
            bool started = TryStartFromInput(now);
            sb.Append('\n').Append(!started && _cdError.Length > 0 ? "PASS" : "FAIL")
              .Append("  填 abc → ").Append(started ? "居然开始了" : "被拦下：" + _cdError);

            // 5) 写回配置再读出来（关掉程序再打开，倒计时还在走）
            SetInputText("30m");
            TryStartFromInput(now);
            PersistCountdown();

            var probe = new Countdown();
            _cfg.RestoreCountdown(probe, now);
            CountdownState ps = probe.Peek(now);
            sb.Append('\n').Append(ps.Phase == CountdownPhase.Running ? "PASS" : "FAIL")
              .Append("  落盘再读回：").Append(ps.Phase).Append("，剩余 ").Append(Countdown.Hms(ps.Remaining));

            return sb.ToString();
        }
        finally
        {
            _cd.Restore(keep.Mode, keep.ClockTicks, keep.DurationTicks, keep.Deadline, keep.Phase, keep.PausedTicks, now);
            _cd.InputText = keepInput ?? string.Empty;
            SetInputText(_cd.InputText);
            _cdError = string.Empty;
            _cfg.CountdownHidden = keepHidden;
            _cfg.CountdownPinned = keepPinned;
            SyncMenuChecks();
            PersistCountdown();
        }
    }

    private void Shutdown()
    {
        if (_tray is not null) _tray.Visible = false;
        Application.Current.Shutdown();
    }

    // ---- 设置 ----
    private void OpenSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        // 用非模态而不是 ShowDialog：设置改动的过程里挂件会照常刷新，能立刻看到效果
        var w = new SettingsWindow(_cfg.Rules);
        w.Closed += (_, _) =>
        {
            _settings = null;
            if (w.Result is not SleepRules r) return;

            _cfg.ApplyRules(r);
            _rules = _cfg.Rules;
            SaveConfig();

            _userMinimized = false;   // 改完规则把窗口叫回来，方便当场确认
            Refresh();
        };

        _settings = w;
        w.Show();
        w.Activate();
    }

    // ---- 鼠标穿透 ----
    private void SetClickThrough(bool on)
    {
        _cfg.ClickThrough = on;
        ApplyClickThrough();
        MenuClickThrough.IsChecked = on;

        if (_trayClickThrough is not null && _trayClickThrough.Checked != on)
        {
            _suppressTrayEvent = true;
            _trayClickThrough.Checked = on;
            _suppressTrayEvent = false;
        }

        SaveConfig();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;

        // 穿透开着时，按住 Ctrl 临时恢复可交互，方便拖动和点按钮
        bool transparent = _cfg.ClickThrough && !IsCtrlDown();
        if (transparent == _appliedTransparent) return;
        _appliedTransparent = transparent;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = transparent ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    private static bool IsCtrlDown() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

    // ---- 托盘 ----
    private void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示窗口");
        showItem.Click += (_, _) => TrayWake();
        menu.Items.Add(showItem);

        var minItem = new WinForms.ToolStripMenuItem("最小化到托盘");
        minItem.Click += (_, _) => MinimizeToTray();
        menu.Items.Add(minItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // ---- 两个块各自显隐 ----
        _trayShowScore = new WinForms.ToolStripMenuItem("显示「早睡健康分」块")
        {
            CheckOnClick = true,
            Checked = !_cfg.ScoreHidden
        };
        _trayShowScore.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayEvent) return;
            SetScoreHidden(!_trayShowScore.Checked);
        };
        menu.Items.Add(_trayShowScore);

        _trayShowCd = new WinForms.ToolStripMenuItem("显示「倒计时」块")
        {
            CheckOnClick = true,
            Checked = !_cfg.CountdownHidden
        };
        _trayShowCd.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayEvent) return;
            SetCountdownHidden(!_trayShowCd.Checked);
        };
        menu.Items.Add(_trayShowCd);

        // 闹钟块平时自己会隐藏（半小时内没闹钟就不占地方），
        // 这个勾只用于"我不想看到它自动冒出来"
        _trayShowAlarm = new WinForms.ToolStripMenuItem("「闹钟」块到点自动显示")
        {
            CheckOnClick = true,
            Checked = !_cfg.AlarmPanelHidden
        };
        _trayShowAlarm.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayEvent) return;
            SetAlarmHidden(!_trayShowAlarm.Checked);
        };
        menu.Items.Add(_trayShowAlarm);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // ---- 闹钟 ----
        var alarmSet = new WinForms.ToolStripMenuItem("闹钟设置…");
        alarmSet.Click += (_, _) => OpenAlarmSettings();
        menu.Items.Add(alarmSet);

        var alarmList = new WinForms.ToolStripMenuItem("全部闹钟");
        menu.Items.Add(alarmList);

        _trayAlarmList = alarmList;

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // ---- 倒计时 ----
        var summon = new WinForms.ToolStripMenuItem("唤起倒计时");
        summon.Click += (_, _) => SummonCountdown();
        menu.Items.Add(summon);

        _trayCdToggle = new WinForms.ToolStripMenuItem("开始倒计时");
        _trayCdToggle.Click += (_, _) => ToggleCountdown();
        menu.Items.Add(_trayCdToggle);

        var cdReset = new WinForms.ToolStripMenuItem("重置倒计时");
        cdReset.Click += (_, _) => CdResetAction();
        menu.Items.Add(cdReset);

        var quick = new WinForms.ToolStripMenuItem("从现在起…");
        foreach (int m in new[] { 15, 30, 45, 60, 120 })
        {
            int minutes = m;
            var item = new WinForms.ToolStripMenuItem(minutes + " 分钟后");
            item.Click += (_, _) => StartQuick(minutes);
            quick.DropDownItems.Add(item);
        }
        menu.Items.Add(quick);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var setItem = new WinForms.ToolStripMenuItem("设置…");
        setItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(setItem);

        _trayClickThrough = new WinForms.ToolStripMenuItem("鼠标穿透")
        {
            CheckOnClick = true,
            Checked = _cfg.ClickThrough
        };
        _trayClickThrough.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayEvent) return;
            SetClickThrough(_trayClickThrough.Checked);
        };
        menu.Items.Add(_trayClickThrough);

        var opMenu = new WinForms.ToolStripMenuItem("透明度");
        foreach (double val in new[] { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5 })
        {
            var item = new WinForms.ToolStripMenuItem($"{(int)Math.Round(val * 100)}%") { Tag = val };
            item.Click += (_, _) =>
            {
                Opacity = val;
                SyncOpacityChecks(opMenu);
                SaveConfig();
            };
            opMenu.DropDownItems.Add(item);
        }
        SyncOpacityChecks(opMenu);
        menu.Items.Add(opMenu);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 提示行跟着实际行为走：以前写的是「双击 = 唤起倒计时」，
        // 现在单击唤醒、双击收起，写错了用户会照着一个不存在的操作试半天
        menu.Items.Add(new WinForms.ToolStripMenuItem("单击图标 = 唤出窗口，双击 = 收起") { Enabled = false });
        menu.Items.Add(new WinForms.ToolStripMenuItem("穿透时按住 Ctrl 可拖动/点击") { Enabled = false });
        menu.Items.Add(new WinForms.ToolStripMenuItem("Ctrl+Alt+L 切换鼠标穿透") { Enabled = false });
        menu.Items.Add(new WinForms.ToolStripMenuItem(_minHotkeyDesc) { Enabled = false });
        menu.Items.Add(new WinForms.ToolStripMenuItem(_cdHotkeyDesc) { Enabled = false });

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "息刻",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => TrayIconDoubleClick();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) TrayIconClick();
        };

        void SyncOpacityChecks(WinForms.ToolStripMenuItem parent)
        {
            foreach (object o in parent.DropDownItems)
                if (o is WinForms.ToolStripMenuItem mi && mi.Tag is double v)
                    mi.Checked = Math.Abs(v - Opacity) < 0.001;
        }
    }

    /// <summary>图标文件的内容要一直活着：Icon 是引用着这个流用的，提前 Disposing 会让托盘图标变空白。</summary>
    private static MemoryStream? _iconStream;

    /// <summary>托盘图标最后是从哪来的（自检要看这个，确认没悄悄退回系统默认图标）。</summary>
    private static string _iconSource = "还没加载";

    /// <summary>
    /// 托盘图标 = 程序图标。用的是和 exe 图标同一个 Assets/TimeGuard.ico（16–256 九个尺寸），
    /// 系统按当前 DPI 自己挑合适的那张，所以托盘里、桌面快捷方式上、任务栏里是同一个图案。
    /// 早先这里是现画一张 32×32 再 Icon.FromHandle —— 一来那个 HICON 没人释放，二来
    /// 画出来的图案和 exe 图标是两回事，托盘和快捷方式对不上。
    /// </summary>
    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var ms = new MemoryStream();
            using (Stream? res = Application.GetResourceStream(
                       new Uri("pack://application:,,,/Assets/TimeGuard.ico"))?.Stream)
            {
                if (res is null) throw new InvalidOperationException("内嵌资源里没有 Assets/TimeGuard.ico");
                res.CopyTo(ms);
            }

            ms.Position = 0;
            var icon = new Drawing.Icon(ms, WinForms.SystemInformation.SmallIconSize);
            _iconStream = ms;
            _iconSource = "内嵌 Assets/TimeGuard.ico（" + icon.Width + "×" + icon.Height + "）";
            return icon;
        }
        catch (Exception ex)
        {
            // 图标只是装饰，取不到也别把程序拖崩——但要让自检看得见这次退让
            _iconSource = "加载失败，退回系统默认图标：" + ex.Message;
            return Drawing.SystemIcons.Application;
        }
    }

    /// <summary>
    /// 自检用：把「托盘实际用的那张图标」导出成 PNG，同时在图片旁边写一份说明。
    /// 「图标进没进托盘」光看代码看不出来——资源路径写错、ico 条目系统不认，
    /// 都会静悄悄退回系统默认图标，这一手就是为了把这种情况抓出来。
    /// </summary>
    internal static string DumpTrayIcon(string pngPath)
    {
        using Drawing.Icon icon = LoadAppIcon();
        using Drawing.Bitmap bmp = icon.ToBitmap();

        string? dir = Path.GetDirectoryName(pngPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(pngPath, Drawing.Imaging.ImageFormat.Png);

        bool ok = _iconSource.StartsWith("内嵌", StringComparison.Ordinal);
        string info =
            "图标来源\t" + _iconSource + Environment.NewLine +
            "托盘尺寸\t" + icon.Width + "×" + icon.Height + "（系统当前 DPI 下托盘图标的实际尺寸）" + Environment.NewLine +
            "导出图片\t" + pngPath + "（" + bmp.Width + "×" + bmp.Height + "）" + Environment.NewLine +
            "判定\t" + (ok ? "PASS  托盘用的是自带的 TimeGuard.ico" : "FAIL  没读到自带图标");

        File.WriteAllText(Path.ChangeExtension(pngPath, ".txt"), info, new UTF8Encoding(true));
        return info;
    }
}

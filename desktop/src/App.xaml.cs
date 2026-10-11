using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TimeGuard;

public partial class App : Application
{
    private const string MutexName = @"Local\TimeGuard.SingleInstance.v1";
    private const string ShowEventName = @"Local\TimeGuard.ShowWindow.v1";

    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showSignal;

    /// <summary>第二个实例请求「把窗口叫出来」时置位，主实例在后台线程上等它。</summary>
    internal static EventWaitHandle? ShowSignal => _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string mode = e.Args.Length > 0 ? e.Args[0] : string.Empty;
        string outPath = e.Args.Length > 1 ? e.Args[1] : string.Empty;

        if (string.Equals(mode, "--alarmcheck", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppConfig.UseOverrideFile(IsolatedConfigPath(OrDefault(outPath, "alarmcheck.txt")));
            Shutdown(AlarmRegressionCheck.Write(OrDefault(outPath, "alarmcheck.txt")) ? 0 : 1);
            return;
        }

        // 自检模式：--selftest [输出文件]   把整夜各时刻的分数导出成表，便于核对规则
        if (string.Equals(mode, "--selftest", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppConfig.UseOverrideFile(IsolatedConfigPath(OrDefault(outPath, "selftest.txt")));
            Shutdown(SelfTest.Write(OrDefault(outPath, "selftest.txt")) ? 0 : 1);
            return;
        }

        // 快照模式：--snapshot [输出图片路径]   离屏渲染整夜各阶段的界面
        if (string.Equals(mode, "--snapshot", StringComparison.OrdinalIgnoreCase))
        {
            string png = OrDefault(outPath, "snapshot.png");

            // **隔离配置**：快照要给每张卡片 new 一个真的 MainWindow，而 MainWindow
            // 一 Loaded 就 SaveConfig()、一关闭又存一次倒计时状态 —— 不隔离的话，
            // 跑一次快照就会把用户真实的 config.json 洗成「窗口默认位置 + 倒计时重置」。
            // （2026-09-21 实测踩到：跑完快照，用户的 23:30 倒计时状态没了。）
            AppConfig.UseOverrideFile(IsolatedConfigPath(png));

            Snapshot.Write(png);

            // 顺手把设置窗口量一遍：每一行的控件有没有超出可视宽度，并抓一张**真实窗口**的图。
            // 这毛病不抛异常、不崩溃、自检也照样全绿，只有实测数字 + 看那张真窗口图才发现。
            // ⚠️ 量的是真实窗口（Show 在屏幕外），不是"按常量离屏摆一遍"——离屏摆法量的是
            // 一个可能并不存在的宽度：XAML 里 Width 还写着 396 时，离屏照样报"全部 PASS"。
            File.WriteAllText(Path.ChangeExtension(png, ".layout.txt"),
                SettingsWindow.LayoutReport(Path.ChangeExtension(png, ".real.png")));

            Shutdown();
            return;
        }

        // 规矩：**凡是会 new MainWindow 的自检模式，必须先 UseOverrideFile 隔离配置**。
        // MainWindow 一 Loaded 就 SaveConfig()、一 OnClosed 又存一次倒计时状态，
        // 不隔离就会把用户真实的 config.json 洗掉。以后加新模式照着做。

        // 图标自检：--icon [输出图片]   把托盘实际用的那张图标导出来，
        // 确认它真的是自带的 TimeGuard.ico，而不是悄悄退回了系统默认图标
        if (string.Equals(mode, "--icon", StringComparison.OrdinalIgnoreCase))
        {
            // 写全名：App 自己有个 Application.MainWindow 属性会挡住这个类名
            TimeGuard.MainWindow.DumpTrayIcon(OrDefault(outPath, "trayicon.png"));
            Shutdown();
            return;
        }

        // 界面自检：--uicheck [输出文件]   真开一次窗口，验证「最小化后不会被自己弹回来」
        if (string.Equals(mode, "--uicheck", StringComparison.OrdinalIgnoreCase))
        {
            string checkLog = OrDefault(outPath, "uicheck.txt");
            // 隔离配置：自检会翻转面板开关之类的字段，不能让它落到用户的 config.json 上
            AppConfig.UseOverrideFile(IsolatedConfigPath(checkLog));
            _ = RunUiCheckAsync(checkLog);
            return;
        }

        // 输入链路自检：--uitest [输出文件] [clickthrough]
        // 开真窗口，由外部脚本注入真实的鼠标点击与键盘敲字，验证「鼠标点得进去、键盘打得进去」。
        // 加第三个参数 clickthrough 会强制开着鼠标穿透，用来确认这个自检确实能甄别出「点不进去」。
        if (string.Equals(mode, "--uitest", StringComparison.OrdinalIgnoreCase))
        {
            bool forceClickThrough = e.Args.Length > 2
                && string.Equals(e.Args[2], "clickthrough", StringComparison.OrdinalIgnoreCase);

            string log = OrDefault(outPath, "uitest.log");
            // 隔离配置：否则 --uitest clickthrough 会把 ClickThrough=true 存进用户的 config.json
            AppConfig.UseOverrideFile(IsolatedConfigPath(log));

            var w = new MainWindow(forceClickThrough);
            w.StartUiTestLog(log);
            w.Show();
            return;
        }

        // 只允许一个实例：否则再次双击 exe 会多开一个窗口，表现为「最小化了又被弹回来」，
        // 而且分不清屏幕上那个是哪一个实例的。
        _singleInstance = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                using EventWaitHandle? h = EventWaitHandle.OpenExisting(ShowEventName);
                h?.Set();
            }
            catch
            {
                // 老实例没在等信号也不影响：至少不会再开一个窗口
            }
            Shutdown();
            return;
        }

        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch
        {
            _showSignal = null;
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string OrDefault(string path, string fileName)
        => string.IsNullOrWhiteSpace(path) ? Path.Combine(AppContext.BaseDirectory, fileName) : path;

    /// <summary>
    /// 自检专用配置文件路径：放在自检输出文件旁边，方便连同报告一起清理。
    /// 自检绝不能读写用户的 config.json——历史教训：--uitest clickthrough 把
    /// ClickThrough=true 存进了真实配置，测试跑完用户的窗口就点不动了。
    /// </summary>
    private static string IsolatedConfigPath(string outputPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? AppContext.BaseDirectory;
        return Path.Combine(dir, "selftest-config.json");
    }

    /// <summary>热键注册结果。失败时把 Win32 错误码写出来（1409 = 已被别的程序占用）。</summary>
    private static string Hotkey(bool ok, int err)
        => ok ? "PASS" : "FAIL（错误码 " + err + (err == 1409 ? " = 已被其它程序占用" : "") + "）";

    /// <summary>
    /// 真开窗口 → 最小化 → 等 Refresh 跑十几轮 → 看窗口是否自己回来了。
    /// 这是「最小化后又弹上来」这个问题的可验证证据。
    /// </summary>
    private async Task RunUiCheckAsync(string outPath)
    {
        var sb = new StringBuilder();
        var w = new MainWindow();
        w.Show();

        await Task.Delay(1500);
        sb.AppendLine("运行时刻\t" + DateTime.Now.ToString("HH:mm:ss") + "\t阶段\t" + w.CurrentPhase);
        sb.AppendLine("1) 启动后窗口可见\t" + w.IsVisible);

        w.MinimizeToTray();
        sb.AppendLine("2) 点最小化后立刻\t可见=" + w.IsVisible + "\t最小化标记=" + w.UserMinimized);

        await Task.Delay(3000);   // 200ms 一跳，够 Refresh 跑 15 轮
        bool stayedHidden = !w.IsVisible;
        sb.AppendLine("3) 等 3 秒后\t可见=" + w.IsVisible + "\t（关键项，应为 False）");

        w.RestoreFromTray();
        await Task.Delay(1200);
        bool restored = w.IsVisible;
        sb.AppendLine("4) 调用恢复后\t可见=" + w.IsVisible);

        sb.AppendLine();
        bool night = w.CurrentPhase != NightPhase.Daytime;
        if (!night)
        {
            sb.AppendLine("结论：当前处于隐藏时段，本次运行不能判定最小化行为（窗口本来就该不显示）。");
        }
        else
        {
            sb.AppendLine("结论：最小化后窗口保持隐藏 = " + (stayedHidden ? "PASS" : "FAIL"));
            sb.AppendLine("结论：恢复后窗口能回来 = " + (restored ? "PASS" : "FAIL"));
        }

        sb.AppendLine();
        sb.AppendLine("5) 最小化热键\t" + Hotkey(w.MinHotkeyRegistered, w.MinHotkeyError)
            + "\t生效：" + w.MinHotkeyDescription
            + (w.MinHotkeyError != 0 ? "（Ctrl+Alt+M 注册失败，错误码 " + w.MinHotkeyError + "）" : ""));
        sb.AppendLine("6) 倒计时热键注册\t" + Hotkey(w.CountdownHotkeyRegistered, w.CountdownHotkeyError)
            + "\t生效：" + w.CountdownHotkeyDescription);
        sb.AppendLine("7) 设置链路：" + w.SettingsRoundTripCheck(new SleepRules
        {
            ShowAt = new TimeSpan(22, 15, 0),
            DeductAt = new TimeSpan(0, 0, 0),
            HideAt = new TimeSpan(7, 0, 0),
            MinutesPerPoint = 5,
            PointsPerStep = 2,
            // 故意换成自检专用的文案：验证自定义的几句话真的会走到界面状态行上，
            // 而不是只在设置窗口里能填、填完还是显示写死的那几句
            StatusBefore = "自检·扣分前",
            StatusHigh = "自检·高分",
            StatusMid = "自检·中分",
            StatusLow = "自检·低分"
        }));

        sb.AppendLine("8) 托盘图标来源\t" + TimeGuard.MainWindow.TrayIconSource);

        sb.AppendLine();
        sb.AppendLine("9) 倒计时界面链路（填输入框 → 点按钮 → 读界面大字 → 落盘 → 读回）：");
        sb.AppendLine(w.CountdownUiRoundTripCheck());

        // 9) 两个块分别收起 / 展开。跑完把三个开关原样还回去，不污染你自己的设置。
        bool keepScore = w.ConfigScoreHidden;
        bool keepCd = w.ConfigCountdownHidden;
        bool keepPinned = w.ConfigCountdownPinned;

        sb.AppendLine();
        sb.AppendLine("10) 两个块分别收起 / 展开：");

        try
        {
            w.RestorePanelFlags(false, false, true);
            w.StartQuick(30); // The countdown panel now requires real remaining time.
            await Task.Delay(700);
            sb.AppendLine("   展开后：窗口=" + w.IsVisible + " 分数块=" + w.ScorePanelVisible + " 倒计时块=" + w.CountdownPanelVisible
                + "  → " + (w.IsVisible && w.CountdownPanelVisible ? "PASS" : "FAIL"));

            w.SetScoreHidden(true);
            await Task.Delay(700);
            bool scoreGoneCdKept = !w.ScorePanelVisible && w.CountdownPanelVisible && w.IsVisible;
            sb.AppendLine("   收起分数块后：窗口=" + w.IsVisible + " 分数块=" + w.ScorePanelVisible + " 倒计时块=" + w.CountdownPanelVisible
                + "  → " + (scoreGoneCdKept ? "PASS  分数块单独收起，倒计时块照常显示" : "FAIL"));
            // 就是这一刻暴露过 bug：分数块一收，挂在它里面的最小化按钮跟着消失。
            // 光看块显隐抓不到，必须量按钮行的真实坐标与尺寸。
            sb.AppendLine("   " + w.ButtonRowReport());

            w.SetCountdownHidden(true);
            await Task.Delay(700);
            bool bothGone = !w.ScorePanelVisible && !w.CountdownPanelVisible && !w.IsVisible;
            sb.AppendLine("   两块都收起后：窗口=" + w.IsVisible + " 分数块=" + w.ScorePanelVisible + " 倒计时块=" + w.CountdownPanelVisible
                + "  → " + (bothGone ? "PASS  窗口整体隐藏，后台照常计时" : "FAIL"));

            w.SetCountdownHidden(false);
            w.SetScoreHidden(false);
            await Task.Delay(700);
            sb.AppendLine("   从托盘恢复后：窗口=" + w.IsVisible + " 倒计时块=" + w.CountdownPanelVisible
                + "  → " + (w.IsVisible && w.CountdownPanelVisible ? "PASS" : "FAIL"));
        }
        finally
        {
            w.RestorePanelFlags(keepScore, keepCd, keepPinned);
            await Task.Delay(400);
        }

        sb.AppendLine("   （三个开关已还原成运行前的值：分数块收起=" + keepScore
            + "，倒计时块收起=" + keepCd + "，白天钉住=" + keepPinned + "）");

        // 11) 托盘图标的单击 / 双击。
        //     只验到「动作层」：真去点系统托盘区的图标得先算像素坐标，而那图标还可能被折进
        //     溢出区、位置随语言栏和缩放变，这种测试比被测的代码本身还脆。所以这里直接调
        //     单击 / 双击对应的动作，验的是「点完之后窗口该不该回来、该不该收起来」。
        //     真实鼠标事件到动作之间的那两根线（MouseClick / DoubleClick）由 BuildTray 接，
        //     接错会在下面第一、二条上直接显形。
        bool keepScore2 = w.ConfigScoreHidden;
        bool keepCd2 = w.ConfigCountdownHidden;
        bool keepPinned2 = w.ConfigCountdownPinned;

        sb.AppendLine();
        sb.AppendLine("11) 托盘图标：单击唤出 / 双击收起");

        try
        {
            w.MinimizeToTray();
            await Task.Delay(400);
            sb.AppendLine("   ① 先最小化 → 可见=" + w.IsVisible + " 最小化标记=" + w.UserMinimized
                + "  → " + (!w.IsVisible && w.UserMinimized ? "PASS" : "FAIL"));

            w.TrayIconClick();                       // 单击
            await Task.Delay(250);
            bool woke = w.IsVisible && !w.UserMinimized;
            sb.AppendLine("   ② 单击图标 → 可见=" + w.IsVisible + " 最小化标记=" + w.UserMinimized
                + "  → " + (woke ? "PASS  窗口被唤出" : "FAIL  点一下没反应"));

            w.TrayIconDoubleClick();                 // 紧接着的双击，应落在保护期内
            await Task.Delay(300);
            sb.AppendLine("   ③ 唤醒后紧接着双击（保护期内）→ 可见=" + w.IsVisible
                + "  → " + (w.IsVisible ? "PASS  没被误收回去" : "FAIL  唤出后立刻又被双击收回，看着像闪了一下"));

            await Task.Delay(700);                   // 等过 500ms 保护期
            w.TrayIconDoubleClick();
            await Task.Delay(400);
            bool hid = !w.IsVisible && w.UserMinimized;
            sb.AppendLine("   ④ 过了保护期再双击 → 可见=" + w.IsVisible + " 最小化标记=" + w.UserMinimized
                + "  → " + (hid ? "PASS  收起来了" : "FAIL"));

            w.TrayIconClick();                       // 再单击唤出，收尾停在可见
            await Task.Delay(400);
            sb.AppendLine("   ⑤ 再单击唤出 → 可见=" + w.IsVisible
                + "  → " + (w.IsVisible ? "PASS" : "FAIL"));
        }
        finally
        {
            w.RestorePanelFlags(keepScore2, keepCd2, keepPinned2);
            await Task.Delay(400);
        }

        // 12) 宽度漂移自愈。
        //     这是 2026-09-28「远程 150% 下 UI 全乱」那个修复到底兑不兑现的**实测**：
        //     把宽度改成现场那个坏值（118 DIP = 177px ÷ 1.5），200ms 一轮的守护必须纠回 CardWidth。
        //     光读代码推不出「守护有没有被调到」——只有真改一次、真等一轮才算数。
        sb.AppendLine();
        sb.AppendLine("12) 宽度漂移自愈（模拟 DPI 切换后宽度漂走 → 字被裁）");
        w.PerturbWidthForTest(118);
        await Task.Delay(700);
        bool healed = Math.Abs(w.WindowWidth - TimeGuard.MainWindow.CardWidth) < 1.0;
        sb.AppendLine("   故意把 Width 改成 118 DIP → 700ms 后 = " + w.WindowWidth.ToString("0.##")
            + "（应回到 " + TimeGuard.MainWindow.CardWidth.ToString("0.#") + "）"
            + "  → " + (healed ? "PASS  守护纠回来了" : "FAIL  没纠回来，150% 下会继续裁字"));

        // 13) 物理宽度必须 = CardWidth × 当前缩放。
        //     这条就是 2026-09-28 那个 bug 的判据本身：现场那台机器上
        //     窗口 DPI 是 144（150%），物理宽度却只有 183px（该有 274.5px），
        //     于是内容按 1.5 倍画、容器只有 1.0 倍，字被裁、按钮重叠。
        //     **本机跑就有效**：这台机器的缩放是多少，这条就在什么缩放下验。
        {
            double sc = w.CurrentDpiForTest;
            int have = w.WindowPhysicalWidthForTest;
            int want = WindowPlacement.ExpectedPhysicalWidth(TimeGuard.MainWindow.CardWidth, sc);
            sb.AppendLine();
            sb.AppendLine("13) 物理宽度 = CardWidth × 当前缩放（本机 dpi=" + sc.ToString("0.###")
                + "，即 " + (sc * 100).ToString("0") + "% 缩放）");
            sb.AppendLine("   实测 " + have + "px，应为 " + want + "px  → "
                + (Math.Abs(have - want) <= 2
                    ? "PASS  物理尺寸与缩放一致"
                    : "FAIL  差 " + (have - want) + "px —— 这正是「字被裁、按钮重叠」的成因"));
        }

        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));

        Shutdown();
    }
}

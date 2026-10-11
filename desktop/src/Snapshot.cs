using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeGuard;

/// <summary>
/// 界面快照：把各个阶段的窗口离屏渲染成一张对照图，便于核对显示效果。
/// 卡片的高度按内容自然量出来（分数块收起后窗口会变矮），所以图上看到的就是真实比例。
/// 用法：TimeGuard.exe --snapshot [输出图片路径]
/// </summary>
public static class Snapshot
{
    /// <summary>
    /// 窗口宽度。**不留第二份**：指向 MainWindow.CardWidth，XAML 里也用 x:Static 指同一个常量。
    /// （原来这里写着 183，和 XAML 各写一份 —— 那正是「宽度悄悄漂走」这类问题的温床。）
    /// </summary>
    private const double CardW = MainWindow.CardWidth;

    private const double RenderScale = 2;   // 2 倍像素密度，字更清
    private const int Cols = 4;
    private const int Pad = 24;
    private const double CaptionH = 34;
    private const double TitleH = 56;

    private sealed class Sample
    {
        public string Note = string.Empty;
        public DateTime Time;

        /// <summary>分数块是否可见（白天会自动收起）。</summary>
        public bool ScoreVisible = true;

        /// <summary>倒计时块是否可见（被手动收起时为 false）。</summary>
        public bool CountdownVisible = true;

        public bool Hover;

        /// <summary>输入框里显示的文字。</summary>
        public string Input = string.Empty;

        /// <summary>用真实的 Countdown 摆出这个阶段的倒计时状态（不走假数据）。</summary>
        public Action<Countdown, DateTime>? CdSetup;

        /// <summary>闹钟块是否可见（半小时内有闹钟时才显示）。</summary>
        public bool AlarmVisible;

        /// <summary>要摆进闹钟块的闹钟（用真实的 AlarmBook 排序与渲染）。</summary>
        public Action<MainWindow, DateTime>? AlarmSetup;
    }

    private sealed class Card
    {
        public BitmapSource Image = null!;
        public double Height;
        public string Caption = string.Empty;
    }

    /// <summary>底部那张子窗口的图（设置窗 / 闹钟设置窗 / 响铃弹窗）。宽度各不相同。</summary>
    private sealed class Panel
    {
        public string Caption = string.Empty;
        public FrameworkElement Root = null!;

        /// <summary>逻辑宽度，取各自窗口的 CardWidth，不在这里另写一份。</summary>
        public double Width;

        /// <summary>逻辑高度（已按窗口的 MaxHeight 夹过，画出来的就是真窗口的高度）。</summary>
        public double Height;

        // 整张图是 2 倍像素密度画的，排版也走这套像素坐标，别跟逻辑尺寸混着算
        public double PixelWidth => Width * RenderScale;
        public double PixelHeight => Height * RenderScale;
    }

    public static void Write(string path)
    {
        var samples = new List<Sample>
        {
            new()
            {
                Note = "白天：分数块自动收起，倒计时块留着",
                Time = new DateTime(2026, 9, 21, 14, 0, 0),
                ScoreVisible = false
            },
            new()
            {
                // 和上面一张成对看：同样是「没有分数块」，但鼠标悬停 → 窗口按钮该出现在右上角。
                // 这就是 2026-09-22 修的那个 bug（按钮原先挂在分数块里，块一收就跟着消失）。
                Note = "分数块收起、只剩倒计时（悬停时按钮仍在右上角）",
                Time = new DateTime(2026, 9, 21, 23, 36, 15),
                ScoreVisible = false,
                Hover = true,
                Input = "00:30",
                CdSetup = (cd, t) => StartAt(cd, "00:30", t)
            },
            new()
            {
                Note = "夜里刚出现（鼠标悬停露出按钮）",
                Time = new DateTime(2026, 9, 21, 21, 0, 0),
                Hover = true
            },
            new()
            {
                Note = "满分待命 + 倒计时运行中",
                Time = new DateTime(2026, 9, 21, 21, 36, 15),
                Input = "23:00",
                CdSetup = (cd, t) => StartAt(cd, "23:00", t)
            },
            new()
            {
                Note = "倒计时剩 4 分钟（转琥珀）",
                Time = new DateTime(2026, 9, 21, 23, 56, 0),
                Input = "00:00",
                CdSetup = (cd, t) => StartAt(cd, "00:00", t)
            },
            new()
            {
                // 闹钟块只在「半小时内会有生效闹钟响」时才出现，所以这张图看到的就是它的真实长相。
                // ⚠️ 别指望在这块里看到「不生效的浅灰字」：灰字只出现在**闹钟设置窗的列表**里
                //    （挂件这一块只收生效的，不生效的根本进不来）—— 见下方「闹钟设置窗 · 普通闹钟页」那张图。
                //    （更正：本卡原先标着"生效亮字、不生效浅灰字"，但那张图里灰字根本画不出来，标注与事实不符。）
                Note = "闹钟块（半小时内要响才显示）：两个生效闹钟按响铃先后排，不生效的不进来",
                Time = new DateTime(2026, 9, 22, 6, 15, 0),
                ScoreVisible = false,
                CountdownVisible = false,
                AlarmVisible = true,
                AlarmSetup = (w, t) =>
                {
                    var book = new AlarmBook();
                    book.AddManual(new TimeSpan(6, 20, 0), "出门", AlarmRepeat.Daily, null, false);
                    book.AddManual(new TimeSpan(6, 40, 0), "吃药", AlarmRepeat.Daily, null, false);
                    // 故意加一个"不生效"的：它不该出现在这一块（挂件只显示生效的）
                    Alarm off = book.AddManual(new TimeSpan(6, 30, 0), "起床", AlarmRepeat.Daily, null, false);
                    off.Enabled = false;
                    w.LoadAlarmsForTest(book.Items);
                }
            },
            new()
            {
                // 连锁起床闹钟：08:00 / 08:03 / 08:06 全部落在半小时内，会一起出现在挂件里。
                // 重点看排序：按钟点先后排（这里的时刻都在凌晨四点之后，所以就是时间顺序）。
                Note = "连锁起床闹钟：一串三个都在半小时内，一起显示",
                Time = new DateTime(2026, 9, 22, 7, 50, 0),
                ScoreVisible = false,
                CountdownVisible = false,
                AlarmVisible = true,
                AlarmSetup = (w, t) =>
                {
                    var book = new AlarmBook();
                    book.AddChain(new AlarmChainSpec
                    {
                        StartAt = new TimeSpan(8, 0, 0),
                        IntervalMinutes = 3,
                        Count = 3,
                        Daily = true
                    }, "起床");
                    w.LoadAlarmsForTest(book.Items);
                }
            },            new()
            {
                Note = "倒计时剩 1 分钟（转红）",
                Time = new DateTime(2026, 9, 22, 0, 0, 0),
                Input = "00:01",
                CdSetup = (cd, t) => StartAt(cd, "00:01", t)
            },
            new()
            {
                Note = "倒计时已暂停（剩余冻结）",
                Time = new DateTime(2026, 9, 22, 0, 0, 0),
                Input = "00:30",
                CdSetup = (cd, t) =>
                {
                    DateTime started = t.AddMinutes(-10);
                    StartAt(cd, "00:30", started);
                    cd.Pause(started);
                }
            },
            new()
            {
                Note = "倒计时块被收起，只剩分数块",
                Time = new DateTime(2026, 9, 22, 3, 30, 0),
                CountdownVisible = false
            },
            new()
            {
                Note = "扣光锁 0 + 倒计时到点",
                Time = new DateTime(2026, 9, 22, 4, 30, 0),
                Input = "04:30",
                CdSetup = (cd, t) => StartAt(cd, "04:30", t.AddMinutes(-90))
            }
        };

        var cards = new List<Card>();
        foreach (Sample s in samples) cards.Add(BuildCard(s));

        double maxH = 0;
        foreach (Card c in cards) maxH = Math.Max(maxH, c.Height);

        int cw = (int)(CardW * RenderScale);
        int maxCh = (int)(maxH * RenderScale);
        int rows = (int)Math.Ceiling(cards.Count / (double)Cols);

        // 底部面板区：设置窗 + 闹钟设置窗两页 + 响铃弹窗，各按自己的真实宽度离屏渲染。
        // 「生效亮字 / 不生效浅灰字」这类只在子窗口里才有的观感，全靠这几张图来核对。
        var panels = new List<Panel>
        {
            TakePanel(new SettingsWindow(SleepRules.Default),
                      "设置窗口（右键窗口或托盘图标 → 设置…）",
                      SettingsWindow.CardWidth, double.PositiveInfinity),
            BuildAlarmPanel(normalTab: true),
            BuildAlarmPanel(normalTab: false),
            BuildRingPanel()
        };

        // 各面板宽度不一样（456 / 476 / 320），不能按固定列数排 —— 按行贪心摆，放不下就换行
        const double PanelGap = 22;
        int gridW = Cols * cw + (Cols + 1) * Pad;
        double widest = 0;
        foreach (Panel p in panels) widest = Math.Max(widest, p.PixelWidth);
        int sheetW = (int)Math.Max(gridW, widest + 2 * Pad);
        double panelAvail = sheetW - 2 * Pad;

        var panelRows = new List<List<Panel>>();
        var rowAcc = new List<Panel>();
        double rowW = 0;
        foreach (Panel p in panels)
        {
            double need = p.PixelWidth + (rowAcc.Count > 0 ? PanelGap : 0);
            if (rowAcc.Count > 0 && rowW + need > panelAvail)
            {
                panelRows.Add(rowAcc);
                rowAcc = new List<Panel>();
                rowW = 0;
                need = p.PixelWidth;
            }
            rowW += need;
            rowAcc.Add(p);
        }
        if (rowAcc.Count > 0) panelRows.Add(rowAcc);

        double panelsH = 0;
        foreach (List<Panel> r in panelRows)
        {
            double rh = 0;
            foreach (Panel p in r) rh = Math.Max(rh, CaptionH + p.PixelHeight);
            panelsH += rh + PanelGap;
        }

        int gridBottom = (int)(TitleH + rows * (maxCh + CaptionH) + Pad);
        int panelsTop = gridBottom + 2 * Pad;   // 给上一行的图注留出空间，别叠在一起
        int sheetH = (int)(panelsTop + panelsH + Pad);

        var sheet = new DrawingVisual();
        using (DrawingContext dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x31)), null,
                new Rect(0, 0, sheetW, sheetH));

            var titleFace = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var bodyFace = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            var title = new FormattedText(
                "TimeGuard · 各阶段界面（分数块 183×143 = 原 236×184 的 60%；下方细线分隔，倒计时在细线之下）",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, titleFace, 22,
                new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), 96);
            dc.DrawText(title, new Point(Pad, 18));

            for (int i = 0; i < cards.Count; i++)
            {
                Card c = cards[i];
                int col = i % Cols;
                int row = i / Cols;
                double x = Pad + col * (cw + Pad);
                double y = TitleH + Pad + row * (maxCh + CaptionH);

                // 卡片顶对齐：高度不同的卡片（分数块收起的那几张会矮一截）保持同一基线
                dc.DrawImage(c.Image, new Rect(x, y, cw, c.Height * RenderScale));

                var ct = new FormattedText(c.Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    bodyFace, 17, new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)), 96);
                dc.DrawText(ct, new Point(x, y + maxCh - 8));
            }

            var captionBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            double py = panelsTop;
            foreach (List<Panel> r in panelRows)
            {
                double rh = 0;
                foreach (Panel p in r) rh = Math.Max(rh, CaptionH + p.PixelHeight);

                double px = Pad;
                foreach (Panel p in r)
                {
                    var cap = new FormattedText(p.Caption, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, bodyFace, 19, captionBrush, 96);
                    dc.DrawText(cap, new Point(px, py));

                    var bmp = new RenderTargetBitmap((int)p.PixelWidth, (int)p.PixelHeight,
                        96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
                    bmp.Render(p.Root);
                    dc.DrawImage(bmp, new Rect(px, py + CaptionH, p.PixelWidth, p.PixelHeight));

                    px += p.PixelWidth + PanelGap;
                }
                py += rh + PanelGap;
            }
        }

        var target = new RenderTargetBitmap(sheetW, sheetH, 96, 96, PixelFormats.Pbgra32);
        target.Render(sheet);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>按输入文本起一次倒计时（解析走真实代码路径）。</summary>
    private static void StartAt(Countdown cd, string input, DateTime now)
    {
        if (Countdown.TryParse(input, now, out CountdownPlan plan, out string err))
            cd.Start(plan, now);
        else
            throw new InvalidOperationException("快照样本里的输入「" + input + "」解析失败：" + err);
    }

    private static Card BuildCard(Sample s)
    {
        var cd = new Countdown();
        s.CdSetup?.Invoke(cd, s.Time);
        CountdownState cs = cd.Peek(s.Time);

        var win = new MainWindow();
        s.AlarmSetup?.Invoke(win, s.Time);

        win.PrepareSnapshot(s.Time, s.Hover, s.ScoreVisible, s.CountdownVisible,
            cs.Phase, cd.PauseAllowed, cs.Remaining, cd.SubLabel(cs.Phase), s.Input, s.AlarmVisible);

        var root = (FrameworkElement)win.Content;
        win.Content = null;

        var host = new Border
        {
            Width = CardW,
            Background = new SolidColorBrush(Color.FromRgb(0x39, 0x45, 0x52)), // 模拟桌面底色，好看清半透明卡片
            Child = root
        };

        // 先量出内容自然高度（窗口的 SizeToContent="Height" 就是这个高度），再按这个高度排版
        host.Measure(new Size(CardW, double.PositiveInfinity));
        double h = host.DesiredSize.Height;
        host.Arrange(new Rect(0, 0, CardW, h));
        host.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)(CardW * RenderScale), (int)(h * RenderScale),
            96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
        rtb.Render(host);

        ScoreState ss = ScoreEngine.Evaluate(s.Time, SleepRules.Default);

        string scorePart = s.ScoreVisible
            ? "分数 " + ss.Score.ToString(CultureInfo.InvariantCulture)
              + "（" + ScoreVisual.Hex(ScoreVisual.ColorForScore(ss.Score)) + "）"
            : "分数块已收起";

        string cdPart = s.CountdownVisible
            ? "倒计时 " + Countdown.BigText(cs.Phase, cs.Remaining)
            : "倒计时块已收起";

        return new Card
        {
            Image = rtb,
            Height = h,
            Caption = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss}　{1}　{2}\n{3}",
                s.Time, scorePart, cdPart, s.Note)
        };
    }

    /// <summary>
    /// 把一个窗口的内容摘下来、离屏摆好，量出它的真实高度。
    /// 宽度只认各窗口自己的 CardWidth（XAML 里也用 x:Static 指向同一个常量），
    /// 这里不许再写一份数字 —— 写两份必然漂移。
    /// </summary>
    private static Panel TakePanel(Window win, string caption, double width, double maxHeight)
    {
        var root = (FrameworkElement)win.Content;
        win.Content = null;   // 摘下来，免得还挂在窗口上

        root.Width = width;
        root.Measure(new Size(width, double.PositiveInfinity));

        // 真窗口是 SizeToContent="Height" + MaxHeight，所以超过上限时它就只有上限那么高
        double h = Math.Min(root.DesiredSize.Height, maxHeight);
        root.Arrange(new Rect(0, 0, width, h));
        root.UpdateLayout();

        return new Panel { Caption = caption, Root = root, Width = width, Height = h };
    }

    /// <summary>
    /// 闹钟设置窗。**「黑色字生效 / 浅灰色字不生效」这对观感只有这一张图画得出来** ——
    /// 挂件里的闹钟块只收「生效且半小时内会响」的，不生效的根本进不去。
    /// 顺带演示排序：以凌晨四点为界，01:30 属于"前一天的尾巴"，排在 23:50 之后。
    /// </summary>
    private static Panel BuildAlarmPanel(bool normalTab)
    {
        var book = new AlarmBook();
        book.AddManual(new TimeSpan(23, 50, 0), "睡前", AlarmRepeat.Daily, null, false);
        book.AddManual(new TimeSpan(1, 30, 0), "夜班", AlarmRepeat.Daily, null, false);
        book.AddManual(new TimeSpan(4, 10, 0), "早起", AlarmRepeat.Daily, null, false);
        Alarm off = book.AddManual(new TimeSpan(6, 30, 0), "起床", AlarmRepeat.Daily, null, false);
        off.Enabled = false;                                          // 演示浅灰字
        book.AddManual(new TimeSpan(8, 0, 0), "例会", AlarmRepeat.Weekly,
                       new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, false);

        // 一串连锁起床闹钟，进"起床闹钟"页
        book.AddChain(new AlarmChainSpec
        {
            StartAt = new TimeSpan(6, 30, 0),
            IntervalMinutes = 5,
            Count = 3,
            Daily = true
        }, "起床");

        // 一个窗口实例只画一页（页签切换会改同一个对象的状态，两页共用实例会互相干扰）
        var win = new AlarmWindow(new AlarmBook(), 30);
        win.LoadForTest(book.Items);
        if (!normalTab) win.SelectWakeTabForTest();

        return TakePanel(win,
            normalTab
                ? "闹钟设置窗 · 普通闹钟页（亮字=生效、浅灰字=不生效；按凌晨四点分界排序）"
                : "闹钟设置窗 · 起床闹钟页（连锁：关掉第 1 个，第 2 个照常响）",
            AlarmWindow.CardWidth, 760);
    }

    /// <summary>响铃弹窗。重点看串里那句"关掉这一个，后面的还会照常响"。</summary>
    private static Panel BuildRingPanel()
    {
        DateTime now = new DateTime(2026, 9, 22, 6, 35, 0);

        var book = new AlarmBook();
        book.AddChain(new AlarmChainSpec
        {
            StartAt = new TimeSpan(6, 30, 0),
            IntervalMinutes = 5,
            Count = 3,
            Daily = true
        }, "起床");

        Alarm second = book.Items.First(a => a.ChainIndex == 2);

        var win = new RingWindow();
        win.Update(new RingingState
        {
            Alarm = second,
            StartedAt = now,
            EndsAt = now.AddSeconds(30)     // 定值，好让图上的"还剩 30 秒"两次出图一模一样
        }, 5, now);

        return TakePanel(win, "响铃弹窗（连锁里的第 2 个；回车=知道了，Esc=稍后提醒）",
                         RingWindow.CardWidth, double.PositiveInfinity);
    }
}

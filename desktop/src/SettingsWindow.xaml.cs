using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace TimeGuard;

/// <summary>规则设置窗。ShowDialog 后读 <see cref="Result"/>：非 null 表示用户点了保存。</summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// 窗口宽度。XAML 用 x:Static 引用、快照也引用，所以只在这里定义一次，改一处即可。
    /// 宽度不是随便定的：右列的控件总宽必须放得下（见 LayoutReport 里的实测）。
    /// </summary>
    public const double CardWidth = 456;

    /// <summary>Border 的左右内边距，量"内容可用宽度"时要用。</summary>
    internal const double SidePadding = 22;

    private static readonly Brush Normal = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Problem = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB1, 0x74));

    public SleepRules? Result { get; private set; }

    public SettingsWindow(SleepRules current)
    {
        InitializeComponent();
        Fill(current);
        UpdatePreview();
    }

    private void Fill(SleepRules r)
    {
        TxtShow.Text = SleepRules.FormatHm(r.ShowAt);
        TxtDeduct.Text = SleepRules.FormatHm(r.DeductAt);
        TxtHide.Text = SleepRules.FormatHm(r.HideAt);
        TxtMinutes.Text = r.MinutesPerPoint.ToString(CultureInfo.InvariantCulture);
        TxtPoints.Text = r.PointsPerStep.ToString(CultureInfo.InvariantCulture);

        // 分档标签按 ScoreVisual 里的阈值生成：以后改阈值，标签自动跟着变，不会对不上
        LblTagHigh.Text = $"{ScoreVisual.HighLine} 分及以上";
        LblTagMid.Text = $"{ScoreVisual.MidLine}–{ScoreVisual.HighLine - 1} 分";
        LblTagLow.Text = $"低于 {ScoreVisual.MidLine} 分";

        TxtStatusBefore.Text = r.StatusBefore;
        TxtStatusHigh.Text = r.StatusHigh;
        TxtStatusMid.Text = r.StatusMid;
        TxtStatusLow.Text = r.StatusLow;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (LblPreview is null) return;

        if (TryBuild(out SleepRules r, out string err))
        {
            LblPreview.Text = "按此规则：" + r.Describe(DateTime.Today) + "\n状态文案：" + DescribeStatuses(r);
            LblPreview.Foreground = Normal;
        }
        else
        {
            LblPreview.Text = err;
            LblPreview.Foreground = Problem;
        }
    }

    /// <summary>把四句文案拼成一行预览；空串明确写成「（不显示）」，免得以为没生效。</summary>
    private static string DescribeStatuses(SleepRules r)
        => string.Join(" → ", new[]
        {
            $"扣分前「{Show(r.StatusBefore)}」",
            $"{ScoreVisual.HighLine}+「{Show(r.StatusHigh)}」",
            $"{ScoreVisual.MidLine}–{ScoreVisual.HighLine - 1}「{Show(r.StatusMid)}」",
            $"<{ScoreVisual.MidLine}「{Show(r.StatusLow)}」"
        });

    private static string Show(string text) => string.IsNullOrEmpty(text) ? "（不显示）" : text;

    private bool TryBuild(out SleepRules rules, out string error)
        => TryBuildRules(TxtShow.Text, TxtDeduct.Text, TxtHide.Text, TxtMinutes.Text, TxtPoints.Text,
            TxtStatusBefore.Text, TxtStatusHigh.Text, TxtStatusMid.Text, TxtStatusLow.Text,
            out rules, out error);

    /// <summary>把「界面上的九段文本」变成规则对象，并拦截明显写错的组合。抽成静态方法便于自检。</summary>
    internal static bool TryBuildRules(string showText, string deductText, string hideText,
        string minutesText, string pointsText,
        string statusBefore, string statusHigh, string statusMid, string statusLow,
        out SleepRules rules, out string error)
    {
        rules = SleepRules.Default;

        if (!SleepRules.TryParseHm(showText, out TimeSpan show)) { error = "「窗口出现」时间读不出来，按 20:30 这样填。"; return false; }
        if (!SleepRules.TryParseHm(deductText, out TimeSpan deduct)) { error = "「开始扣分」时间读不出来，按 23:30 这样填。"; return false; }
        if (!SleepRules.TryParseHm(hideText, out TimeSpan hide)) { error = "「窗口隐藏」时间读不出来，按 06:30 这样填。"; return false; }

        if (!int.TryParse(minutesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
        { error = "「每几分钟扣」要填整数。"; return false; }
        if (!int.TryParse(pointsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int points))
        { error = "「每次扣几分」要填整数。"; return false; }

        var r = new SleepRules
        {
            ShowAt = show,
            DeductAt = deduct,
            HideAt = hide,
            MinutesPerPoint = minutes,
            PointsPerStep = points,
            MaxScore = ScoreEngine.MaxScore,
            // 文案去首尾空白；**空串合法**，意思是"这个状态不显示文字"
            StatusBefore = Clean(statusBefore),
            StatusHigh = Clean(statusHigh),
            StatusMid = Clean(statusMid),
            StatusLow = Clean(statusLow)
        };

        if (!r.IsValid(out error)) return false;

        rules = r;
        error = string.Empty;
        return true;
    }

    /// <summary>不带文案的旧签名：文案取默认值。自检里"非法输入要拦住"那组用例仍在用。</summary>
    internal static bool TryBuildRules(string showText, string deductText, string hideText,
        string minutesText, string pointsText, out SleepRules rules, out string error)
        => TryBuildRules(showText, deductText, hideText, minutesText, pointsText,
            SleepRules.Default.StatusBefore, SleepRules.Default.StatusHigh,
            SleepRules.Default.StatusMid, SleepRules.Default.StatusLow, out rules, out error);

    private static string Clean(string? text) => (text ?? string.Empty).Trim();

    private void ShowError(string message)
    {
        LblError.Text = message;
        LblError.Visibility = Visibility.Visible;
    }

    private void TrySave()
    {
        if (!TryBuild(out SleepRules r, out string err)) { ShowError(err); return; }
        Result = r;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => TrySave();

    // ---- 自检钩子（--uicheck 用）----
    internal void LoadForTest(SleepRules r) => Fill(r);

    internal SleepRules? SaveForTest() { TrySave(); return Result; }

    /// <summary>
    /// 自检用：**开一个真的设置窗口**（挪到屏幕外，不会闪到用户眼前），量每一行「最右边那个控件」
    /// 相对**窗口右边缘**的余量（负数 = 溢出、最右边的字会被裁掉）；可选把窗口抓成图片。
    ///
    /// **为什么必须是真窗口**：上一版是「把内容摘下来、按常量 CardWidth 离屏 Measure/Arrange」，
    /// 量的是一个**并不存在的宽度**——XAML 里 `Width` 还写着 396（没跟着常量改成 456），
    /// 于是离屏报告写着"每一行都在可视区内、余量 45px"，用户屏幕上「分」字却被整个挤出窗口。
    /// 教训：断言里的宽度必须来自**真实窗口的 ActualWidth**，不能取代码里那个"应该是多少"。
    /// 顺带也断言 ActualWidth == CardWidth，把「XAML 与常量各写一份」这种漂移直接抓出来。
    /// </summary>
    internal static string LayoutReport(string? realShotPath = null)
    {
        var win = new SettingsWindow(SleepRules.Default)
        {
            // 真开窗口才能拿到真实布局；挪到屏幕外，不打扰用户
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000
        };
        win.Show();
        // 等 Loaded / 布局 / 渲染都走完，再开始量
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        double realWidth = win.ActualWidth;
        double contentRight = realWidth - SidePadding;

        var sb = new StringBuilder();
        sb.AppendLine($"设置窗口：实测宽 {realWidth:0.#}，内容区宽 {realWidth - SidePadding * 2:0.#}，"
            + $"右边界在 x={contentRight:0.#}（左右各留 {SidePadding:0.#}）");

        // ⚠️ 判据要用「内容区右边界相对窗口的坐标」= 宽 − 右内边距，不是「内容宽度」。
        // 上一版把这两个搞混，把余量算小了 2×22，白白虚报一批 FAIL。
        bool widthOk = Math.Abs(realWidth - CardWidth) < 0.5;
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  {0}  {1,-28} 应是 {2:0.#}，实测 {3:0.#}{4}",
            widthOk ? "PASS" : "FAIL", "窗口实际宽度 = CardWidth", CardWidth, realWidth,
            widthOk ? "" : "  ← XAML 的 Width 与常量不一致，离屏量出来的余量全是假的"));

        (string Label, FrameworkElement Element)[] rows =
        {
            ("窗口出现（开始记分）行", win.TxtShow),
            ("开始扣分行", win.TxtDeduct),
            ("扣分速率行（最右是「分」字）", win.LblPointsUnit),
            ("窗口隐藏行", win.TxtHide),
            ("文案·扣分前行", win.TxtStatusBefore),
            ("文案·高分行", win.TxtStatusHigh),
            ("文案·中分行", win.TxtStatusMid),
            ("文案·低分行", win.TxtStatusLow)
        };

        bool allOk = widthOk;
        foreach ((string label, FrameworkElement el) in rows)
        {
            double edgeX = el.TransformToAncestor(win).Transform(new Point(el.ActualWidth, 0)).X;
            double slack = contentRight - edgeX;
            bool ok = slack >= 0;
            allOk &= ok;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}  {1,-26} 右边界 x={2,6:0.#}，余量 {3,6:0.#}px",
                ok ? "PASS" : "FAIL", label, edgeX, slack));
        }

        if (!string.IsNullOrEmpty(realShotPath))
        {
            try
            {
                CaptureRealWindow(win, realShotPath!);
                sb.AppendLine("  真实窗口抓图：" + realShotPath);
            }
            catch (Exception ex)
            {
                sb.AppendLine("  真实窗口抓图失败：" + ex.Message);
            }
        }

        sb.AppendLine("  结论：" + (allOk ? "每一行都在可视区内" : "有行溢出或窗口宽度不对，最右边的字会被裁掉"));

        win.Close();
        return sb.ToString();
    }

    /// <summary>
    /// 自检用（--uicheck 里调用）：真开一次设置窗口，确认「实际宽度 = CardWidth」。
    ///
    /// **为什么单独加这一条**：窗口宽度曾经在两处各写一份（XAML 写死 396、代码常量 456），
    /// 于是所有"按常量算余量"的检查都全绿，用户在真窗口里看到的却是「分」字被裁掉。
    /// 只要 XAML 再出现一处硬写的宽度，这条就会红。XAML 现在用 `{x:Static}` 引用常量。
    /// </summary>
    internal static string WidthCheck()
    {
        var win = new SettingsWindow(SleepRules.Default)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000
        };
        win.Show();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        double w = win.ActualWidth;
        win.Close();

        bool ok = Math.Abs(w - CardWidth) < 0.5;
        return string.Format(CultureInfo.InvariantCulture,
            "{0}  设置窗口实际宽度 = CardWidth（应 {1:0.#}，实测 {2:0.#}）",
            ok ? "PASS" : "FAIL", CardWidth, w);
    }

    // ---- 把"真实窗口"抓成图：走 PrintWindow，让窗口自己画到内存 DC 上 ----
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private static void CaptureRealWindow(Window win, string path)
    {
        if (PresentationSource.FromVisual(win) is not System.Windows.Interop.HwndSource src)
            throw new InvalidOperationException("窗口还没有句柄");

        double scale = 1.0;
        if (src.CompositionTarget is not null)
            scale = src.CompositionTarget.TransformToDevice.M11;

        int w = (int)Math.Ceiling(win.ActualWidth * scale);
        int h = (int)Math.Ceiling(win.ActualHeight * scale);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("窗口尺寸量出来是 0");

        bool printed = false;
        using (var bmp = new Drawing.Bitmap(w, h))
        {
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { printed = PrintWindow(src.Handle, hdc, 2); } // PW_RENDERFULLCONTENT
                finally { g.ReleaseHdc(hdc); }
            }

            // 分层窗口（AllowsTransparency）有时 PrintWindow 会给出全黑，退回渲染视觉树
            if (!printed || IsBlank(bmp))
            {
                var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(win);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string? dir0 = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir0)) System.IO.Directory.CreateDirectory(dir0);
                using var fs0 = System.IO.File.Create(path);
                enc.Save(fs0);
                return;
            }

            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            bmp.Save(path, DrawingImaging.ImageFormat.Png);
        }
    }

    /// <summary>整张图是不是全黑/全透明（PrintWindow 对分层窗口失败的典型表现）。</summary>
    private static bool IsBlank(Drawing.Bitmap bmp)
    {
        int lit = 0;
        for (int y = 0; y < bmp.Height; y += 7)
        {
            for (int x = 0; x < bmp.Width; x += 7)
            {
                Drawing.Color c = bmp.GetPixel(x, y);
                if (c.A > 8 && (c.R > 24 || c.G > 24 || c.B > 24)) lit++;
            }
        }
        return lit < 8;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        Fill(SleepRules.Default);
        LblError.Visibility = Visibility.Collapsed;
        UpdatePreview();
    }

    private void Win_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.Enter) { e.Handled = true; TrySave(); }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { /* 未按住左键时 DragMove 会抛，忽略 */ }
    }
}

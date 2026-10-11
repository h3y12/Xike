using System;
using System.Collections.Generic;

namespace TimeGuard;

/// <summary>
/// 窗口摆放的纯计算部分。
///
/// 为什么要单独抽出来：分辨率适配的全部风险都压在这几个减法上，而
/// 「窗口跑到屏幕外」「4K 上位置怪怪的」这类问题靠肉眼是验不出来的——
/// 手上没有 4K 屏就没法试。抽成纯函数后，可以用 1080P / 4K@100% / 4K@150% /
/// 4K@200% / 双屏拼接 / 显示器被拔掉 这些**算出来的**规格在 --selftest 里断言。
///
/// 全部使用 DIP（设备无关像素）。WPF 的 DIP 会随系统缩放自动换算成物理像素，
/// 所以程序不需要为「4K 还是 1080P」写任何分支——真正需要管的只有
/// 「位置还在不在可视范围内」。
/// </summary>
public static class WindowPlacement
{
    /// <summary>新建窗口时离屏幕边缘的留白。</summary>
    public const double EdgeMargin = 28;

    /// <summary>SizeToContent="Height" 时 Height 是 NaN，夹取需要一个够用的估值。</summary>
    public const double FallbackHeight = 260;

    /// <summary>
    /// 贴右上角的默认位置。
    ///
    /// 注意这里全是 DIP：1080P@100% 和 4K@200% 的 DIP 工作区都是 1920×1080，
    /// 于是**两者算出来的位置一模一样**，在各自的屏幕上也都落在同样的视觉位置。
    /// </summary>
    public static (double Left, double Top) DefaultPosition(
        double workAreaRight, double workAreaTop, double width)
        => (workAreaRight - width - EdgeMargin, workAreaTop + EdgeMargin);

    /// <summary>
    /// 把窗口夹进虚拟桌面（所有显示器拼起来的那个矩形）内，保证整窗可见。
    ///
    /// 这是「换了分辨率/缩放以后窗口不见了」的唯一防线：无论保存的位置多离谱，
    /// 结果一定是四个边都在范围内。屏幕比窗口还小的极端情况下退化成贴左上角，
    /// 也不会出现负数坐标把标题栏推到屏幕外。
    ///
    /// ⚠️ **多屏不要用这个重载**：它把虚拟桌面当成一整块矩形，而两块并排的屏幕
    /// 拼出来的矩形**中间那条缝在物理上并不存在**。窗口被夹到缝里就是「看得见，
    /// 但跨在两块屏之间、哪块上都摆不正」。多屏请用
    /// <see cref="ClampToNearest(double, double, double, double, IReadOnlyList{ScreenRect})"/>。
    /// </summary>
    public static (double Left, double Top) Clamp(
        double left, double top, double width, double height,
        double screenLeft, double screenTop, double screenWidth, double screenHeight)
        => ClampToNearest(left, top, width, height,
            new[] { new ScreenRect(screenLeft, screenTop, screenWidth, screenHeight) });

    /// <summary>
    /// 一块显示器的工作区（DIP）。多屏夹取的输入单位。
    /// </summary>
    public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public double CenterX => Left + Width / 2;
        public double CenterY => Top + Height / 2;
    }

    /// <summary>
    /// 多屏版本：先挑一块「离窗口最近」的显示器，再在那块屏的工作区里夹取。
    ///
    /// 为什么要挑屏而不是用外接矩形：两块 1920×1080 并排时，外接矩形是 3840×1080，
    /// 把窗口夹到这个矩形里，窗口完全可以停在 x=1850 —— 那是主屏右边缘外 70px、
    /// 副屏左边缘外 70px，**真的跨在缝上**。按屏夹取的口径是「整窗必须落在某一块屏内」，
    /// 缝里不是合法位置。
    ///
    /// 挑屏用的是「窗口中心离哪块屏的中心最近」：窗口中心是它视觉上的重心，
    /// 比左上角更贴近用户的直觉（把一个跨屏窗口拖动时，人看的是它的中心落在哪块屏上）。
    /// 一块屏都没有（理论上不该发生）时按 (0,0) 兜底，绝不返回 NaN。
    /// </summary>
    public static (double Left, double Top) ClampToNearest(
        double left, double top, double width, double height, IReadOnlyList<ScreenRect> screens)
    {
        if (screens is null || screens.Count == 0) return ClampIn(left, top, width, height, 0, 0, 1, 1);

        double cx = left + width / 2;
        double cy = top + height / 2;

        ScreenRect best = screens[0];
        double bestDistance = double.MaxValue;

        foreach (ScreenRect s in screens)
        {
            // 中心点到窗口中心的距离（欧氏，够用且不会因除法引入 NaN）
            double dx = s.CenterX - cx;
            double dy = s.CenterY - cy;
            double d = dx * dx + dy * dy;

            // 相等时取更靠前的（调用方保证主屏在前），使结果与列表顺序确定
            if (d < bestDistance)
            {
                bestDistance = d;
                best = s;
            }
        }

        return ClampIn(left, top, width, height, best.Left, best.Top, best.Width, best.Height);
    }

    /// <summary>单块矩形内的夹取（原来的算法，逐字保留）。</summary>
    private static (double Left, double Top) ClampIn(
        double left, double top, double width, double height,
        double screenLeft, double screenTop, double screenWidth, double screenHeight)
    {
        double minLeft = screenLeft;
        double maxLeft = screenLeft + screenWidth - width;
        double minTop = screenTop;
        double maxTop = screenTop + screenHeight - height;

        double l = maxLeft >= minLeft ? Math.Clamp(left, minLeft, maxLeft) : minLeft;
        double t = maxTop >= minTop ? Math.Clamp(top, minTop, maxTop) : minTop;
        return (l, t);
    }

    /// <summary>把一个可能为 NaN（SizeToContent）的高度换成可用的估值。</summary>
    public static double UsableHeight(double height)
        => double.IsNaN(height) || height <= 0 ? FallbackHeight : height;

    /// <summary>
    /// 给定逻辑宽度（DIP）和系统缩放，算出窗口**应该**有的物理宽度（px）。
    ///
    /// 为什么要抽成纯函数：2026-09-28 报的问题 —— 本地 100%、远程 150% 时，
    /// DPI 一切换窗口的物理宽度就停在旧值，而内容按新比例渲染，于是字被裁、按钮重叠。
    /// 这个 bug 只在「另一种缩放」上出现，本机复现不了，只能把期望值算出来断言。
    ///
    /// 现场数字（远程 2K@150%，窗口 183 DIP）：实测 **177px**，应该是 **275px** ——
    /// 177 ÷ 1.5 = 118，正好是「WPF 把这份物理宽度当成了新 DPI 下的 DIP 宽度」。
    /// 后面 <see cref="ExpectedPhysicalWidth"/> 把这几个值都钉住了。
    ///
    /// ⚠️ 取整必须用 **AwayFromZero**（四舍五入），不能用 `Math.Round` 的默认值：
    /// 默认是 ToEven（银行家舍入），183 × 1.5 = 274.5 会算成 **274**，
    /// 而 Windows 的 DPI 换算是四舍五入 → 275。差这 1px 正常看不出来，
    /// 但会让「期望值」和系统行为长期对不上（自检当初就是这么把这条抓出来的）。
    /// </summary>
    public static int ExpectedPhysicalWidth(double dipWidth, double dpiScale)
    {
        if (double.IsNaN(dpiScale) || dpiScale <= 0) dpiScale = 1.0;
        if (double.IsNaN(dipWidth) || dipWidth <= 0) return 0;
        return (int)Math.Round(dipWidth * dpiScale, MidpointRounding.AwayFromZero);
    }

    // ---- 常见显示规格（DIP 工作区），供自检与文档引用 ----
    // 这些是「系统缩放后的逻辑尺寸」：物理分辨率 ÷ 缩放比例。

    /// <summary>1920×1080 @ 100%</summary>
    public static readonly (string Name, double Width, double Height) Fhd100 =
        ("1920×1080 @100%", 1920, 1080);

    /// <summary>3840×2160 @ 150%（Windows 在 4K 上的常见默认）</summary>
    public static readonly (string Name, double Width, double Height) Uhd150 =
        ("3840×2160 @150%", 2560, 1440);

    /// <summary>3840×2160 @ 200%</summary>
    public static readonly (string Name, double Width, double Height) Uhd200 =
        ("3840×2160 @200%", 1920, 1080);

    /// <summary>2560×1440 @ 100%（超宽/高分显示器）</summary>
    public static readonly (string Name, double Width, double Height) Qhd100 =
        ("2560×1440 @100%", 2560, 1440);
}

using System;
using System.Windows.Media;

namespace TimeGuard;

/// <summary>
/// 分数 → 界面表现：状态文案与分数颜色。
/// 独立成静态类（不依赖窗口），便于 --selftest 直接断言。
/// </summary>
public static class ScoreVisual
{
    /// <summary>颜色支点：低分→高分，分数在这些点之间线性插值，逐分连续变化。</summary>
    public static readonly (int Score, byte R, byte G, byte B)[] Ramp =
    {
        (0,   0xFF, 0x3B, 0x30),   // 亮红
        (20,  0xE2, 0x4B, 0x4A),
        (40,  0xEF, 0x6B, 0x2B),   // 橙红
        (60,  0xEF, 0x9F, 0x27),   // 琥珀
        (80,  0x9B, 0xD1, 0x4E),   // 黄绿
        (100, 0x5D, 0xCA, 0xA5)    // 青绿
    };

    /// <summary>高/中分界（≥ 此分用 <see cref="SleepRules.StatusHigh"/>）。设置窗口的标签按它生成。</summary>
    public const int HighLine = 80;

    /// <summary>中/低分界（≥ 此分用 <see cref="SleepRules.StatusMid"/>）。</summary>
    public const int MidLine = 60;

    /// <summary>
    /// 扣分段的状态文案。**文案不在代码里写死**，从规则里取（用户能在设置窗口改）。
    /// 留空表示这一档不显示文字。
    /// </summary>
    public static string StatusForScore(int score, SleepRules rules) =>
        score >= HighLine ? rules.StatusHigh
        : score >= MidLine ? rules.StatusMid
        : rules.StatusLow;

    public static Color ColorForScore(int score)
    {
        if (score <= Ramp[0].Score) return Color.FromRgb(Ramp[0].R, Ramp[0].G, Ramp[0].B);

        for (int i = 1; i < Ramp.Length; i++)
        {
            if (score > Ramp[i].Score) continue;

            double t = (score - Ramp[i - 1].Score) / (double)(Ramp[i].Score - Ramp[i - 1].Score);
            return Color.FromRgb(
                Lerp(Ramp[i - 1].R, Ramp[i].R, t),
                Lerp(Ramp[i - 1].G, Ramp[i].G, t),
                Lerp(Ramp[i - 1].B, Ramp[i].B, t));
        }

        return Color.FromRgb(Ramp[^1].R, Ramp[^1].G, Ramp[^1].B);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}

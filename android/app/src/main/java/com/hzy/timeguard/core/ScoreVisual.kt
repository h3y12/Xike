package com.hzy.timeguard.core

import kotlin.math.roundToInt

/**
 * 分数 → 界面表现：状态文案与分数颜色。
 * 与桌面版 `ScoreVisual.cs` 使用同一组颜色支点，分数在这些点之间线性插值，
 * 逐分连续变化，改分时颜色是平滑过渡而不是跳变。
 */
object ScoreVisual {

    /** 高/中分界（≥ 此分用 [SleepRules.statusHigh]）。 */
    const val HIGH_LINE = 80

    /** 中/低分界（≥ 此分用 [SleepRules.statusMid]）。 */
    const val MID_LINE = 60

    /** 颜色支点：分数下标（升序）→ RGB。 */
    private val stops = intArrayOf(0, 20, 40, 60, 80, 100)

    private val ramp = intArrayOf(
        0xFF3B30, // 亮红
        0xE24B4A,
        0xEF6B2B, // 橙红
        0xEF9F27, // 琥珀
        0x9BD14E, // 黄绿
        0x5DCAA5, // 青绿
    )

    /**
     * 扣分段的状态文案。**文案不在代码里写死**，从规则里取（用户能在设置里改）。
     * 留空表示这一档不显示文字。
     */
    fun statusForScore(score: Int, rules: SleepRules): String = when {
        score >= HIGH_LINE -> rules.statusHigh
        score >= MID_LINE -> rules.statusMid
        else -> rules.statusLow
    }

    /** 返回带不透明度的 ARGB 颜色，可直接交给 Android 的 setTextColor。 */
    fun colorForScore(score: Int): Int {
        if (score <= stops[0]) return ARGB or ramp[0]

        for (i in 1 until stops.size) {
            if (score > stops[i]) continue

            val t = (score - stops[i - 1]).toDouble() / (stops[i] - stops[i - 1])
            val r = lerp((ramp[i - 1] shr 16) and 0xFF, (ramp[i] shr 16) and 0xFF, t)
            val g = lerp((ramp[i - 1] shr 8) and 0xFF, (ramp[i] shr 8) and 0xFF, t)
            val b = lerp(ramp[i - 1] and 0xFF, ramp[i] and 0xFF, t)
            return ARGB or (r shl 16) or (g shl 8) or b
        }

        return ARGB or ramp[ramp.size - 1]
    }

    /** 形如 #RRGGBB，便于日志与断言。 */
    fun hex(argb: Int): String = "#%02X%02X%02X".format((argb shr 16) and 0xFF, (argb shr 8) and 0xFF, argb and 0xFF)

    private const val ARGB = -0x1000000 // 0xFF shl 24；const 不接受位运算，写成等价的负数字面量

    private fun lerp(a: Int, b: Int, t: Double): Int = (a + (b - a) * t).roundToInt()
}

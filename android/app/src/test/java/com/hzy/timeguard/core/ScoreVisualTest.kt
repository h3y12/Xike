package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 颜色与文案分档的测试。
 * 颜色支点必须和桌面版 ScoreVisual.cs 完全一致，否则同一个分数在两块屏上会显示不同颜色。
 */
class ScoreVisualTest {

    private val rules = SleepRules.Default

    private fun argb(rgb: Int): Int = (0xFF shl 24) or rgb

    @Test
    fun colorAtZero_isBrightRed() {
        assertEquals(argb(0xFF3B30), ScoreVisual.colorForScore(0))
    }

    @Test
    fun colorAtHundred_isTeal() {
        assertEquals(argb(0x5DCAA5), ScoreVisual.colorForScore(100))
    }

    @Test
    fun colorAtAnchors_matchRampExactly() {
        assertEquals(argb(0xE24B4A), ScoreVisual.colorForScore(20))
        assertEquals(argb(0xEF6B2B), ScoreVisual.colorForScore(40))
        assertEquals(argb(0xEF9F27), ScoreVisual.colorForScore(60))
        assertEquals(argb(0x9BD14E), ScoreVisual.colorForScore(80))
    }

    @Test
    fun colorIsClampedBeyondRamp() {
        assertEquals("低于 0 应取端点色", argb(0xFF3B30), ScoreVisual.colorForScore(-10))
        assertEquals("高于 100 应取端点色", argb(0x5DCAA5), ScoreVisual.colorForScore(130))
    }

    @Test
    fun colorIsFullyOpaqueAlways() {
        for (score in 0..100) {
            val alpha = (ScoreVisual.colorForScore(score) ushr 24) and 0xFF
            assertEquals("分数 $score 的颜色必须不透明", 0xFF, alpha)
        }
    }

    @Test
    fun colorChangesMonotonicallyInRedChannel() {
        // 从亮红到青绿，红色分量整体是递减的（允许个别平台四舍五入波动，只验证趋势）
        val rAt10 = (ScoreVisual.colorForScore(10) shr 16) and 0xFF
        val rAt90 = (ScoreVisual.colorForScore(90) shr 16) and 0xFF
        assertTrue("低分应比高分更红：$rAt10 vs $rAt90", rAt10 > rAt90)
    }

    // ---------------------------------------------------------- 文案分档

    @Test
    fun statusBoundaries_areEightyAndSixty() {
        assertEquals(rules.statusHigh, ScoreVisual.statusForScore(100, rules))
        assertEquals(rules.statusHigh, ScoreVisual.statusForScore(80, rules))
        assertEquals(rules.statusMid, ScoreVisual.statusForScore(79, rules))
        assertEquals(rules.statusMid, ScoreVisual.statusForScore(60, rules))
        assertEquals(rules.statusLow, ScoreVisual.statusForScore(59, rules))
        assertEquals(rules.statusLow, ScoreVisual.statusForScore(0, rules))
    }

    @Test
    fun customStatusText_isHonoured() {
        val custom = rules.copy(statusHigh = "赶紧睡", statusMid = "该睡了", statusLow = "快放下手机")
        assertEquals("赶紧睡", ScoreVisual.statusForScore(95, custom))
        assertEquals("该睡了", ScoreVisual.statusForScore(70, custom))
        assertEquals("快放下手机", ScoreVisual.statusForScore(10, custom))
    }

    @Test
    fun emptyStatusText_isAllowed() {
        // 留空是合法设置：表示这一档不显示文字
        val blank = rules.copy(statusLow = "")
        assertEquals("", ScoreVisual.statusForScore(10, blank))
    }

    @Test
    fun constLinesMatchDesktop() {
        assertEquals(80, ScoreVisual.HIGH_LINE)
        assertEquals(60, ScoreVisual.MID_LINE)
    }

    @Test
    fun hexFormatsWithoutAlpha() {
        assertEquals("#FF3B30", ScoreVisual.hex(ScoreVisual.colorForScore(0)))
        assertEquals("#5DCAA5", ScoreVisual.hex(ScoreVisual.colorForScore(100)))
    }
}

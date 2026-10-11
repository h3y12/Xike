package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.LocalDateTime

/**
 * 算分测试。
 *
 * 这里有一组「金标准」数字，全部来自桌面版 TimeGuard 的默认口径
 * （23:30 起每 3 分钟扣 1 分 ⇒ 00:00 恰好 90 分，04:30 扣光，06:30 隐藏）。
 * 手机版只要有一个数字和它们对不上，就说明移植走样了。
 *
 * **口径只有一种：时间戳。** 函数签名里没有任何「用了多久手机」的输入，
 * 这不是巧合——手机版曾经自作主张加过「亮屏累计」，那是桌面版从来没有的规则，
 * 已废除。见 [untouchedPhoneStillLosesPoints] 与 [everyMinuteAcrossTheNight_matchesHandCalculation]。
 */
class ScoreEngineTest {

    private val rules = SleepRules.Default
    private fun at(h: Int, m: Int, day: Int = 1) = LocalDateTime.of(2026, 1, day, h, m)

    // ---------------------------------------------------------- 时段与阶段

    @Test
    fun daytimeBeforeShow_isDaytime() {
        // 20:00 还没到出现时刻（20:30），窗口不该显示
        val s = ScoreEngine.evaluate(at(20, 0), rules)
        assertEquals(NightPhase.Daytime, s.phase)
        assertEquals(100, s.score)
    }

    @Test
    fun afterHide_isDaytime() {
        val s = ScoreEngine.evaluate(at(6, 30), rules)
        assertEquals(NightPhase.Daytime, s.phase)
    }

    @Test
    fun rightAtShow_startsCountdownWithFullScore() {
        val s = ScoreEngine.evaluate(at(20, 30), rules)
        assertEquals(NightPhase.Countdown, s.phase)
        assertEquals(100, s.score)
        assertEquals(Duration.ofMinutes(180), s.untilDeadline) // 20:30 → 23:30
    }

    @Test
    fun beforeDeduct_staysFullScore() {
        val s = ScoreEngine.evaluate(at(23, 29), rules)
        assertEquals(NightPhase.Countdown, s.phase)
        assertEquals(100, s.score)
        assertEquals(Duration.ofMinutes(1), s.untilDeadline)
    }

    @Test
    fun rightAtDeduct_scoreStillFull_butPhaseSwitches() {
        // 扣分起点这一瞬间刚好还没扣掉任何一分
        val s = ScoreEngine.evaluate(at(23, 30), rules)
        assertEquals(NightPhase.Deducting, s.phase)
        assertEquals(100, s.score)
    }

    // ---------------------------------------------------------- 与桌面版逐字对齐

    /**
     * 用户口述的两个例子，逐字核对：
     *  · 23:33 距 23:30 过了 3 分钟 → 扣 1 分 → 99
     *  · 00:06 距 23:30 过了 36 分钟 → 扣 12 分 → 88
     */
    @Test
    fun userExamples_timestampDriven() {
        assertEquals(99, ScoreEngine.evaluate(at(23, 33), rules).score)
        assertEquals(88, ScoreEngine.evaluate(at(0, 6, day = 2), rules).score)
    }

    @Test
    fun threeMinutesIn_dropsExactlyOnePoint() {
        assertEquals(99, ScoreEngine.evaluate(at(23, 33), rules).score)
    }

    @Test
    fun twoMinutes59SecondsIn_stillNoDeduction() {
        // 差一秒不算——整数运算，不能因为浮点误差把整点算少
        val s = ScoreEngine.evaluate(LocalDateTime.of(2026, 1, 1, 23, 32, 59), rules)
        assertEquals(100, s.score)
    }

    @Test
    fun atMidnight_scoreIs90() {
        // 金标准：桌面版注释明确写了「00:00 恰好 90 分」
        val s = ScoreEngine.evaluate(at(0, 0, day = 2), rules)
        assertEquals(90, s.score)
        assertEquals(NightPhase.Deducting, s.phase)
    }

    @Test
    fun twoAM_scoreIs50() {
        // 23:30 → 02:00 = 150 分钟 ÷ 3 = 50 分
        val s = ScoreEngine.evaluate(at(2, 0, day = 2), rules)
        assertEquals(50, s.score)
    }

    @Test
    fun atFourThirty_scoreIsZeroAndExhausted() {
        // 金标准：04:30 扣光
        val s = ScoreEngine.evaluate(at(4, 30, day = 2), rules)
        assertEquals(0, s.score)
        assertEquals(NightPhase.Exhausted, s.phase)
    }

    @Test
    fun afterExhaustion_scoreNeverGoesNegative() {
        val s = ScoreEngine.evaluate(at(6, 0, day = 2), rules)
        assertEquals(0, s.score)
        assertEquals(NightPhase.Exhausted, s.phase)
    }

    /**
     * **整夜没碰手机也照扣。** 23:30 开始，到 06:00 早该扣光。
     *
     * 这条是本次修改的核心：手机版曾经自作主张改成「亮屏才算在用」，
     * 后果是手机往床头一放睡一整夜、分数纹丝不动，等于白装。
     * 判定「晚睡」的依据是**时间**，不是「你玩了多久手机」。
     */
    @Test
    fun untouchedPhoneStillLosesPoints() {
        assertEquals(0, ScoreEngine.evaluate(at(6, 0, day = 2), rules).score)
    }

    /**
     * 一分钟一格扫过整夜，与**手算公式**逐个对照。
     * 手算公式按桌面版那句 `(now − deduct) ÷ 步长` 独立写一遍，不复用被测代码。
     */
    @Test
    fun everyMinuteAcrossTheNight_matchesHandCalculation() {
        val start = at(23, 30)
        val step = rules.minutesPerPoint.toLong()

        var t = start
        var checked = 0
        while (t < at(6, 30, day = 2)) {
            val elapsedMinutes = Duration.between(start, t).toMinutes()
            val expected = (rules.maxScore - elapsedMinutes / step * rules.pointsPerStep)
                .coerceAtLeast(0L)
                .toInt()

            assertEquals("时刻 $t 的分数与手算不符", expected, ScoreEngine.evaluate(t, rules).score)
            t = t.plusMinutes(1)
            checked++
        }

        assertEquals("应扫过 23:30 → 06:30 的 420 分钟", 420, checked)
    }

    // ---------------------------------------------------------- 格式化

    @Test
    fun formatHms_padsMinutesAndSeconds() {
        assertEquals("2:14:30", ScoreEngine.formatHms(Duration.ofSeconds(2 * 3600 + 14 * 60 + 30)))
        assertEquals("0:00:05", ScoreEngine.formatHms(Duration.ofSeconds(5)))
        assertEquals("0:00:00", ScoreEngine.formatHms(Duration.ofSeconds(-10)))
    }

    @Test
    fun formatShort_omitsHoursWhenZero() {
        assertEquals("3:05", ScoreEngine.formatShort(Duration.ofSeconds(185)))
        assertEquals("1:02:03", ScoreEngine.formatShort(Duration.ofSeconds(3723)))
    }

    // ---------------------------------------------------------- 自定义速率

    @Test
    fun customRate_fiveMinutesTwoPoints_collapsesFaster() {
        val r = rules.copy(minutesPerPoint = 5, pointsPerStep = 2)
        // 23:30 + 50 分钟 = 00:20 ⇒ 10 步 × 2 分 = 20 分
        val s = ScoreEngine.evaluate(at(0, 20, day = 2), r)
        assertEquals(80, s.score)
        assertTrue(ScoreVisual.statusForScore(s.score, r).isNotEmpty())
    }
}

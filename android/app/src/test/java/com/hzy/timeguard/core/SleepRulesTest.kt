package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDateTime
import java.time.LocalTime

/**
 * 规则解析的测试。
 * 重点不是「跑通」，而是把桌面版 SleepRules.cs 的行为逐个钉死，
 * 免得手机版悄悄和电脑版算出不一样的结果。
 */
class SleepRulesTest {

    private val rules = SleepRules.Default

    @Test
    fun defaults_matchDesktop() {
        assertEquals(LocalTime.of(20, 30), rules.showAt)
        assertEquals(LocalTime.of(23, 30), rules.deductAt)
        assertEquals(LocalTime.of(6, 30), rules.hideAt)
        assertEquals(3, rules.minutesPerPoint)
        assertEquals(1, rules.pointsPerStep)
        assertEquals(100, rules.maxScore)
    }

    @Test
    fun resolve_insideNight_usesSameCalendarDay() {
        // 21:00 已在 20:30 之后，三个时刻应落在同一天/次日
        val r = rules.resolve(LocalDateTime.of(2026, 1, 1, 21, 0))
        assertEquals(LocalDateTime.of(2026, 1, 1, 20, 30), r.show)
        assertEquals(LocalDateTime.of(2026, 1, 1, 23, 30), r.deduct)
        assertEquals(LocalDateTime.of(2026, 1, 2, 6, 30), r.hide)
    }

    @Test
    fun resolve_afterMidnight_stillBelongsToPreviousEvening() {
        // 凌晨 2:00 必须归属到「前一天晚上」那一段
        val r = rules.resolve(LocalDateTime.of(2026, 1, 2, 2, 0))
        assertEquals(LocalDateTime.of(2026, 1, 1, 20, 30), r.show)
        assertEquals(LocalDateTime.of(2026, 1, 1, 23, 30), r.deduct)
        assertEquals(LocalDateTime.of(2026, 1, 2, 6, 30), r.hide)
    }

    @Test
    fun resolve_daytime_belongsToPreviousNight() {
        // 中午 12:00 时，最近的一段「夜」是昨晚的；此时已过 hide，属于 Daytime
        val r = rules.resolve(LocalDateTime.of(2026, 1, 2, 12, 0))
        assertEquals(LocalDateTime.of(2026, 1, 1, 20, 30), r.show)
        assertEquals(LocalDateTime.of(2026, 1, 2, 6, 30), r.hide)
    }

    @Test
    fun spanToZero_isFiveHoursByDefault() {
        // 100 分 ÷ 每次 1 分 × 每 3 分钟 = 300 分钟
        assertEquals(300L, rules.spanToZero.toMinutes())
    }

    @Test
    fun describe_mentionsZeroTime() {
        val text = rules.describe()
        assertTrue("应报出扣光时刻 04:30，实际：$text", text.contains("04:30"))
        assertTrue(text.contains("20:30"))
        assertTrue(text.contains("06:30"))
    }

    @Test
    fun validate_acceptsDefaults() {
        assertNull(rules.validate())
    }

    @Test
    fun validate_rejectsSameShowAndHide() {
        val bad = rules.copy(hideAt = rules.showAt)
        assertNotNull(bad.validate())
    }

    @Test
    fun validate_rejectsZeroMinutesPerPoint() {
        assertNotNull(rules.copy(minutesPerPoint = 0).validate())
        assertNotNull(rules.copy(minutesPerPoint = 721).validate())
    }

    @Test
    fun validate_rejectsPointsAboveMax() {
        assertNotNull(rules.copy(pointsPerStep = 0).validate())
        assertNotNull(rules.copy(pointsPerStep = 101).validate())
    }

    @Test
    fun validate_rejectsDeductOutsideWindow() {
        // 扣分起点在隐藏之后 → 永远扣不到分
        val bad = rules.copy(deductAt = LocalTime.of(9, 0))
        val err = bad.validate()
        assertNotNull("开始扣分 09:00 落在 20:30–06:30 之外，应被判非法", err)
    }

    @Test
    fun parseHm_acceptsLooseFormats() {
        assertEquals(LocalTime.of(23, 30), SleepRules.tryParseHm("23:30"))
        assertEquals(LocalTime.of(6, 30), SleepRules.tryParseHm("6:30"))
        assertEquals(LocalTime.of(6, 30), SleepRules.tryParseHm(" 06:30 "))
        assertEquals(LocalTime.of(23, 30), SleepRules.tryParseHm("23：30")) // 全角冒号
        assertEquals(LocalTime.of(23, 30), SleepRules.tryParseHm("23.30")) // 句点
    }

    @Test
    fun parseHm_rejectsGarbage() {
        assertNull(SleepRules.tryParseHm(""))
        assertNull(SleepRules.tryParseHm(null))
        assertNull(SleepRules.tryParseHm("abc"))
        assertNull(SleepRules.tryParseHm("24:00"))
        assertNull(SleepRules.tryParseHm("12:60"))
        assertNull(SleepRules.tryParseHm("1:2:3:4"))
    }

    @Test
    fun parseHm_fallsBackWhenInvalid() {
        assertEquals(rules.hideAt, SleepRules.parseHm("不是时间", rules.hideAt))
    }

    @Test
    fun formatHm_padsToTwoDigits() {
        assertEquals("06:30", SleepRules.formatHm(LocalTime.of(6, 30)))
        assertEquals("23:00", SleepRules.formatHm(LocalTime.of(23, 0)))
    }

    /**
     * 「写之前要拦」的两端口径：非法组合必须能被 `validate()` 认出来。
     *
     * 这是 P2-3 的核心——之前界面拿的是「已经把非法输入换成默认值」的规则去校验，
     * 于是永远合法，非法设置照样落盘生效（比如「每 0 分钟扣 1 分」）。
     * 严格路径（`rawRules` + `validate`）必须真的拦得住这一组。
     */
    @Test
    fun strictValidation_rejectsEveryIllegalCombination() {
        val illegals = listOf(
            rules.copy(hideAt = rules.showAt),                    // 隐藏 = 出现 → 窗口一直显示
            rules.copy(minutesPerPoint = 0),                      // 每 0 分钟一轮
            rules.copy(minutesPerPoint = 721),                    // 超过 12 小时
            rules.copy(pointsPerStep = 0),                        // 每轮扣 0 分
            rules.copy(pointsPerStep = 101),                      // 单轮扣穿满分
            rules.copy(deductAt = LocalTime.of(9, 0)),            // 扣分落在显示时段之外
        )

        illegals.forEach { bad ->
            assertNotNull("这份规则应被判非法：$bad", bad.validate())
        }
    }

    /** 默认规则和几组正常改动必须继续合法——防止校验收紧过头把好配置也拦了。 */
    @Test
    fun strictValidation_keepsLegalCombinations() {
        val legals = listOf(
            rules,
            rules.copy(showAt = LocalTime.of(22, 0), deductAt = LocalTime.MIDNIGHT, hideAt = LocalTime.of(7, 0)),
            rules.copy(deductAt = rules.showAt),                  // 出现即扣分
            rules.copy(minutesPerPoint = 720, pointsPerStep = 100),
            rules.copy(statusBefore = "", statusLow = ""),         // 空文案合法
        )

        legals.forEach { good ->
            assertNull("这份规则应当合法：$good", good.validate())
        }
    }
}

package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.LocalDateTime
import java.time.LocalTime

/**
 * 手动倒计时测试。
 *
 * 这一块与健康分**没有任何关系**，只是个数——所以测试也分成两半：
 * 一半是输入解析（认哪些写法、认不出来时说人话），一半是状态机与落盘还原。
 *
 * 最要紧的两条：
 *  · [clockTimeRefusesPause]：到钟点的计时不能暂停，否则 23:30 会被挪成 23:30:10；
 *  · [restoredRunningCountsRealElapsedTime]：剩余时间按**绝对截止时刻**算，
 *    锁屏 / App 被杀 / 手机重启都不会让它「停住」。
 */
class ManualCountdownTest {

    private val now = LocalDateTime.of(2026, 1, 1, 22, 0)

    @Test
    fun overlayOnlyShowsCountdownWithRemainingTime() {
        val c = ManualCountdown()
        assertFalse(c.tick(now).hasActiveTime)
        c.start(parse("45m"), now)
        assertTrue(c.tick(now).hasActiveTime)
        c.pause(now.plusMinutes(10))
        assertTrue(c.tick(now.plusMinutes(20)).hasActiveTime)
        c.resume(now.plusMinutes(20))
        assertFalse(c.tick(now.plusMinutes(55)).hasActiveTime)
        c.reset()
        assertFalse(c.tick(now.plusMinutes(60)).hasActiveTime)
    }

    /** RGB → 不透明 ARGB。颜色断言一律用它，别直接写 6 位十六进制（那等于 alpha=0）。 */
    private fun argb(rgb: Int): Int = (0xFF shl 24) or rgb

    private fun parse(text: String, at: LocalDateTime = now): CdPlan {
        val r = ManualCountdown.tryParse(text, at)
        assertTrue("「$text」应该解析成功，实际是 $r", r is CdParse.Ok)
        return (r as CdParse.Ok).plan
    }

    private fun parseError(text: String, at: LocalDateTime = now): String {
        val r = ManualCountdown.tryParse(text, at)
        assertTrue("「$text」应该解析失败，实际是 $r", r is CdParse.Err)
        return (r as CdParse.Err).message
    }

    private fun started(text: String, at: LocalDateTime = now): ManualCountdown {
        val c = ManualCountdown()
        c.start(parse(text, at), at)
        return c
    }

    // ---------------------------------------------------------- 输入解析

    @Test
    fun colonInput_meansClockTime() {
        val p = parse("23:00")
        assertEquals(CdMode.ClockTime, p.mode)
        assertEquals(LocalTime.of(23, 0), p.clockTarget)
        assertEquals("到 23:00 为止", p.label)
    }

    @Test
    fun clockTimeWithSeconds_keepsSecondsInLabel() {
        val p = parse("23:00:30")
        assertEquals(CdMode.ClockTime, p.mode)
        assertEquals(LocalTime.of(23, 0, 30), p.clockTarget)
        assertEquals("到 23:00:30 为止", p.label)
    }

    @Test
    fun fullWidthColon_isAccepted() {
        assertEquals(LocalTime.of(23, 0), parse("23：00").clockTarget)
    }

    @Test
    fun minutesSuffix_meansDuration() {
        val p = parse("45m")
        assertEquals(CdMode.Duration, p.mode)
        assertEquals(45, p.durationMinutes)
    }

    @Test
    fun chineseMinutes_sameAsM() {
        assertEquals(45, parse("45分钟").durationMinutes)
    }

    @Test
    fun hoursSuffix_convertsToMinutes() {
        assertEquals(CdMode.Duration, parse("2h").mode)
        assertEquals(120, parse("2h").durationMinutes)
        assertEquals(60, parse("1小时").durationMinutes)
    }

    @Test
    fun plainNumberUpTo23_isAnHour() {
        val p = parse("23")
        assertEquals(CdMode.ClockTime, p.mode)
        assertEquals(LocalTime.of(23, 0), p.clockTarget)
    }

    @Test
    fun plainNumberAbove23_isMinutes() {
        val p = parse("45")
        assertEquals(CdMode.Duration, p.mode)
        assertEquals(45, p.durationMinutes)
    }

    @Test
    fun emptyInput_isRejectedWithHint() {
        assertTrue(parseError("").contains("23:00"))
        assertTrue(parseError("   ").contains("45m"))
    }

    @Test
    fun garbage_isRejectedWithHumanMessage() {
        assertTrue(parseError("abc").isNotEmpty())
        assertTrue(parseError("30x").contains("m"))
        assertTrue(parseError("0m").contains("超出"))
        assertTrue(parseError("25h").contains("1–24"))
        assertTrue(parseError("99:99").isNotEmpty())
        assertTrue(parseError("2000m").contains("超出"))
    }

    /**
     * 负数一律不认。
     *
     * 安卓版这里本来就是对的（`n in 0..23` 有下界），桌面版曾经写成 `n <= 23` 缺下界，
     * 于是 `-2147483648` 会当场抛 ArgumentOutOfRangeException。这条测试是两端口径的凭证：
     * 桌面版对应 `SelfTest.AppendCountdownChecks` 里的负数用例。
     */
    @Test
    fun negativeNumbers_areRejected() {
        assertTrue(parseError("-1").isNotEmpty())
        assertTrue(parseError("-24").isNotEmpty())
        assertTrue(parseError("-1m").isNotEmpty())
        assertTrue(parseError("-2h").isNotEmpty())
        assertTrue(parseError("-2147483648").isNotEmpty())
    }

    /** `-0` 在 int 里就是 0，与 `"0"` 同义（不是负数）。两端行为要一致。 */
    @Test
    fun negativeZero_meansMidnight() {
        val plan = parse("-0")
        assertEquals(CdMode.ClockTime, plan.mode)
        assertEquals(LocalTime.MIDNIGHT, plan.clockTarget)
    }

    // ---------------------------------------------------------- 状态机

    @Test
    fun clockTimeCountsDownToThatHour() {
        val c = started("23:00")                 // 22:00 起
        assertEquals(CdPhase.Running, c.phase)

        val st = c.tick(LocalDateTime.of(2026, 1, 1, 22, 30))
        assertEquals(CdPhase.Running, st.phase)
        assertEquals(Duration.ofMinutes(30), st.remaining)
        assertEquals("00:30:00", ManualCountdown.hms(st.remaining))
    }

    @Test
    fun clockTimeAlreadyPassedToday_rollsToTomorrow() {
        val at = LocalDateTime.of(2026, 1, 1, 23, 30)
        val c = started("23:00", at)
        // 23:00 已过 → 目标是明天 23:00，还有 23.5 小时
        assertEquals(Duration.ofMinutes(23 * 60 + 30), c.tick(at).remaining)
    }

    @Test
    fun tickReachingDeadline_autoFinishes() {
        val c = started("23:00")
        val st = c.tick(LocalDateTime.of(2026, 1, 1, 23, 0))
        assertEquals(CdPhase.Finished, st.phase)
        assertEquals(Duration.ZERO, st.remaining)
        // 状态已经落到 Finished，再 tick 也不会回头
        assertEquals(CdPhase.Finished, c.tick(LocalDateTime.of(2026, 1, 1, 23, 5)).phase)
    }

    @Test
    fun durationCountsFromNow() {
        val c = started("45m")
        assertEquals("00:45:00", ManualCountdown.hms(c.tick(now).remaining))
        assertEquals("00:25:00", ManualCountdown.hms(c.tick(now.plusMinutes(20)).remaining))
    }

    // ---------------------------------------------------------- 暂停规则（关键）

    /**
     * 时刻模式**不允许暂停**：它的截止点就是那个钟点本身，
     * 冻结剩余量再继续等于把 23:30 变成 23:30:10。
     */
    @Test
    fun clockTimeRefusesPause() {
        val c = started("23:00")
        val at = now.plusMinutes(10)

        assertFalse(c.pause(at))
        assertEquals(CdPhase.Running, c.phase)
        assertEquals(Duration.ofMinutes(50), c.tick(at).remaining)
        assertFalse(c.pauseAllowed)
    }

    @Test
    fun overlayClockCaptionKeepsPauseNoticeOnItsOwnLine() {
        val c = started("23:30")
        assertEquals("到 23:30 为止\n不可暂停", c.subLabel(multiline = true))
        assertEquals("到 23:30 为止 · 不可暂停", c.subLabel())
    }

    @Test
    fun overlayCaptionSeparatesPausedAndFinishedStates() {
        val c = started("45m")
        assertTrue(c.pause(now.plusMinutes(10)))
        assertEquals("已暂停\n到 22:45 结束", c.subLabel(multiline = true))
        c.start(parse("23:30"), now)
        c.tick(now.plusMinutes(90))
        assertEquals("到 23:30 为止\n已到点", c.subLabel(multiline = true))
    }

    @Test
    fun durationCanPauseAndFreezesRemaining() {
        val c = started("45m")
        val at = now.plusMinutes(20)          // 22:20，剩 25 分钟
        assertTrue(c.pause(at))
        assertEquals(CdPhase.Paused, c.phase)

        // 暂停期间又过了 20 分钟，剩余**一动不动**
        assertEquals(Duration.ofMinutes(25), c.tick(now.plusMinutes(40)).remaining)
    }

    @Test
    fun resumeShiftsDeadlineByRemaining() {
        val c = started("45m")
        c.pause(now.plusMinutes(20))                 // 22:20，剩 25 分钟
        c.resume(now.plusMinutes(40))                // 22:40 继续 → 截止 23:05

        assertEquals(CdPhase.Running, c.phase)
        assertEquals(Duration.ofMinutes(25), c.tick(now.plusMinutes(40)).remaining)
        assertEquals(Duration.ofMinutes(5), c.tick(now.plusMinutes(60)).remaining)
    }

    /**
     * 秒级算例（用户点名的那个）：22:00 起 45 分钟 → 截止 22:45:00；
     * 22:32:41 按下暂停，此刻正好剩 **12 分 19 秒**；22:47:41 点继续，
     * 新的截止点 = 22:47:41 + 12:19 = 23:00:00 —— 从当前时间往后推，
     * 不是接着原来那个 22:45:00 走。
     */
    @Test
    fun pausingAtTwelveNineteen_resumesFromNowWithExactlyTheSameRemaining() {
        val c = started("45m")
        val pauseAt = now.plusMinutes(32).plusSeconds(41)      // 22:32:41
        val remain = Duration.ofMinutes(12).plusSeconds(19)    // 12:19

        assertEquals(remain, c.tick(pauseAt).remaining)
        assertTrue(c.pause(pauseAt))
        assertEquals(CdPhase.Paused, c.phase)

        // 暂停期间又过了 15 分钟，剩余一点都不动
        assertEquals(remain, c.tick(pauseAt.plusMinutes(15)).remaining)

        val resumeAt = pauseAt.plusMinutes(15)                 // 22:47:41
        c.resume(resumeAt)

        assertEquals(CdPhase.Running, c.phase)
        assertEquals(remain, c.tick(resumeAt).remaining)
        assertEquals("00:12:19", ManualCountdown.hms(c.tick(resumeAt).remaining))

        // 继续之后按真实时间流走，并且正好在 23:00:00 到点
        assertEquals("00:07:19", ManualCountdown.hms(c.tick(resumeAt.plusMinutes(5)).remaining))
        assertEquals("00:00:01", ManualCountdown.hms(c.tick(resumeAt.plusMinutes(12).plusSeconds(18)).remaining))
        assertEquals(CdPhase.Running, c.tick(resumeAt.plusMinutes(12).plusSeconds(18)).phase)
        assertEquals(CdPhase.Finished, c.tick(resumeAt.plusMinutes(12).plusSeconds(19)).phase)
    }

    /** 暂停只是把截止点整体推后，剩余量不变：中途停 15 分钟，真实结束时刻就晚 15 分钟。 */
    @Test
    fun pauseShiftsTheEndMomentByExactlyThePausedSpan() {
        val c = started("45m")                                  // 不暂停的话 22:45 到点
        c.pause(now.plusMinutes(32).plusSeconds(41))
        c.resume(now.plusMinutes(47).plusSeconds(41))            // 停了 15 分钟

        assertEquals(CdPhase.Running, c.tick(now.plusMinutes(45)).phase)
        assertEquals(CdPhase.Running, c.tick(now.plusMinutes(59).plusSeconds(59)).phase)
        assertEquals(CdPhase.Finished, c.tick(now.plusMinutes(60)).phase)
    }

    @Test
    fun resetGoesBackToIdle() {
        val c = started("45m")
        c.reset()
        assertEquals(CdPhase.Idle, c.phase)
        assertEquals(CdPhase.Idle, c.tick(now).phase)
        assertNull(c.plan)
    }

    @Test
    fun restartDuration_countsFullRoundAgain() {
        val c = started("45m")
        c.tick(now.plusMinutes(44))
        c.restart(now.plusMinutes(44))
        assertEquals("00:45:00", ManualCountdown.hms(c.tick(now.plusMinutes(44)).remaining))
    }

    // ---------------------------------------------------------- 落盘还原（锁屏 / 被杀 / 重开）

    /**
     * 存的是**绝对截止时刻**，所以 App 没开的这段时间照常算进去。
     * 这条正是「锁屏再亮起，按结束时间重新算剩余」在代码里的落点。
     */
    @Test
    fun restoredRunningCountsRealElapsedTime() {
        val c = started("45m")                        // 22:00 起，截止 22:45
        val snap = c.snapshot()

        val reopened = ManualCountdown()
        reopened.restore(snap, now.plusMinutes(10))   // 22:10 重开

        assertEquals(CdPhase.Running, reopened.phase)
        assertEquals(Duration.ofMinutes(35), reopened.tick(now.plusMinutes(10)).remaining)
    }

    @Test
    fun restoredAfterDeadline_isFinishedNotRestarted() {
        val c = started("45m")
        val snap = c.snapshot()

        val reopened = ManualCountdown()
        reopened.restore(snap, now.plusMinutes(50))   // 22:50，早过点了

        assertEquals(CdPhase.Finished, reopened.phase)
    }

    /**
     * 昨晚设的「到 23:00」，第二天早上打开 App：应该是「已到点」，
     * 而不是被顺延成今晚 23:00 又重开一轮。
     */
    @Test
    fun restoredClockTimeNextMorning_isFinishedNotRolledToTonight() {
        val c = started("23:00")                      // 22:00 起
        val snap = c.snapshot()

        val reopened = ManualCountdown()
        reopened.restore(snap, LocalDateTime.of(2026, 1, 2, 8, 0))

        assertEquals(CdPhase.Finished, reopened.phase)
    }

    @Test
    fun restoredPausedKeepsRemaining() {
        val c = started("45m")
        c.pause(now.plusMinutes(20))                  // 剩 25 分钟
        val snap = c.snapshot()

        val reopened = ManualCountdown()
        reopened.restore(snap, now.plusMinutes(120))

        assertEquals(CdPhase.Paused, reopened.phase)
        assertEquals(Duration.ofMinutes(25), reopened.tick(now.plusMinutes(120)).remaining)
    }

    @Test
    fun restoreEmptySnapshot_staysIdle() {
        val c = ManualCountdown()
        c.restore(CdSnapshot(), now)
        assertEquals(CdPhase.Idle, c.phase)
        assertNull(c.plan)
    }

    /**
     * 暂停快照必须保住**不足一秒**的剩余量。
     *
     * 修之前快照只存整秒（`pausedRemaining.seconds`，Duration.seconds 是向下取整），
     * 59.5 秒的剩余存进去变成 59，恢复后余量不足 1 秒 → 大字直接跳到「时间到！」。
     * 这里连 0.5 秒都要原样穿过一次落盘还原。
     */
    @Test
    fun pausedSnapshotKeepsSubSecondRemaining() {
        val c = ManualCountdown()
        c.start(parse("45m"), now)
        // 走到只剩 59.5 秒（LocalDateTime 上做减法；Duration 没有 minusMillis）
        c.pause(now.plusMinutes(45).minusSeconds(59).minusNanos(500_000_000L))
        val snap = c.snapshot()

        assertEquals(59L, snap.pausedSeconds)
        assertEquals(500_000_000L, snap.pausedNanos)

        val reopened = ManualCountdown()
        reopened.restore(snap, now.plusHours(3))

        assertEquals(CdPhase.Paused, reopened.phase)
        assertEquals(
            "剩余量要精确到纳秒",
            Duration.ofSeconds(59).plusMillis(500),
            reopened.tick(now.plusHours(3)).remaining,
        )
    }

    /**
     * 旧快照（没有 `pausedNanos` 这一项）要能平滑读回，行为与改动前一致。
     * 这条保证升级不会让用户手上正在暂停的倒计时发生变化。
     */
    @Test
    fun oldSnapshotWithoutNanos_stillRestores() {
        val legacy = CdSnapshot(
            mode = CdMode.Duration.name,
            durationMinutes = 45,
            deadline = now.plusMinutes(45).toString(),
            phase = CdPhase.Paused.name,
            pausedSeconds = 600L,          // 旧版本只存了这一项
            pausedNanos = 0L,
        )

        val c = ManualCountdown()
        c.restore(legacy, now.plusMinutes(5))

        assertEquals(CdPhase.Paused, c.phase)
        assertEquals(Duration.ofMinutes(10), c.tick(now.plusMinutes(5)).remaining)
    }

    /**
     * 继续之后，小字里的结束钟点必须跟着新的截止点走。
     *
     * 关闭的写法里 label 是 `start()` 时按当时的截止点写死的，暂停会把截止点整体
     * 往后推，继续后 label 还指着旧钟点（实测 20:45 vs 实际 20:55）。
     */
    @Test
    fun resumeRewritesTheEndMomentInTheLabel() {
        val c = started("45m")                     // 22:00 起 → 22:45 结束
        assertEquals("到 22:45 结束", c.plan!!.label)
        assertEquals("到 22:45 结束", c.subLabel())

        c.pause(now.plusMinutes(10))               // 22:10 暂停，剩 35 分钟
        c.resume(now.plusMinutes(25))              // 22:25 继续 → 截止点推到 23:00

        assertEquals("到 23:00 结束", c.plan!!.label)
        assertEquals("到 23:00 结束", c.subLabel())
        assertEquals(Duration.ofMinutes(35), c.tick(now.plusMinutes(25)).remaining)
    }

    /** 时刻模式没有暂停，label 也就不会被继续改写——这条防止「顺手把时刻模式也改了」。 */
    @Test
    fun clockTimeLabelIsNotRewrittenByResume() {
        val c = started("23:00")
        c.resume(now.plusMinutes(5))               // 不是 Paused，应当什么都不做
        assertEquals("到 23:00 为止", c.plan!!.label)
    }

    // ---------------------------------------------------------- 显示

    @Test
    fun hmsRoundsUpToWholeSeconds() {
        // 剩 44:59.997 要显示 00:45:00，否则「点了开始」那几毫秒会让它一上来就少一秒
        assertEquals("00:45:00", ManualCountdown.hms(Duration.ofSeconds(45 * 60L).minusMillis(3)))
        assertEquals("00:00:01", ManualCountdown.hms(Duration.ofMillis(1)))
        assertEquals("00:00:00", ManualCountdown.hms(Duration.ofSeconds(-10)))
        assertEquals("01:00:00", ManualCountdown.hms(Duration.ofHours(1)))
    }

    @Test
    fun bigTextShowsPlaceholderOrArrival() {
        assertEquals("--:--:--", ManualCountdown.bigText(CdPhase.Idle, Duration.ZERO))
        assertEquals("时间到！", ManualCountdown.bigText(CdPhase.Finished, Duration.ZERO))
        assertEquals(
            "00:14:59",
            ManualCountdown.bigText(CdPhase.Running, Duration.ofSeconds(14 * 60 + 59)),
        )
    }

    /**
     * 颜色随剩余时间收紧，**而且是看得见的**。
     *
     * 2026-09-22 补：这一条原来只比 RGB（`assertEquals(0xF2FFFF, …)`），
     * 而 `0xF2FFFF` 在 Kotlin 里是 `0x00F2FFFF` —— **alpha = 0，字是全透明的**。
     * 断言全绿，屏幕上却是「一按开始，倒计时数字就不见了」。
     * 所以现在一律比**完整 ARGB**；同目录 ScoreVisualTest 早就是这么写的
     * （它有 [argb] 辅助），偏偏倒计时这边漏了，教训是"颜色断言必须卡住 alpha"。
     */
    @Test
    fun colorTightensAsTimeRunsOut() {
        assertEquals(argb(0xF2FFFF), ManualCountdown.colorFor(CdPhase.Running, Duration.ofMinutes(6)))
        assertEquals(argb(0xEF9F27), ManualCountdown.colorFor(CdPhase.Running, Duration.ofMinutes(5)))
        assertEquals(argb(0xFF3B30), ManualCountdown.colorFor(CdPhase.Running, Duration.ofMinutes(1)))
        assertEquals(argb(0xFF3B30), ManualCountdown.colorFor(CdPhase.Finished, Duration.ZERO))
        // 未开始这一档本来就带 alpha（0xB0 的暗白），保持原样
        assertEquals(0xB0FFFFFF.toInt(), ManualCountdown.colorFor(CdPhase.Idle, Duration.ZERO))
    }

    /** 任何阶段、任何剩余时间，颜色都得看得见（alpha ≥ 0xB0）。 */
    @Test
    fun everyCountdownColorIsVisible() {
        val cases = listOf(
            CdPhase.Idle to Duration.ZERO,
            CdPhase.Running to Duration.ofHours(2),
            CdPhase.Running to Duration.ofMinutes(5).plusSeconds(1),
            CdPhase.Running to Duration.ofSeconds(30),
            CdPhase.Paused to Duration.ofMinutes(12),
            CdPhase.Finished to Duration.ZERO,
        )
        for ((phase, left) in cases) {
            val alpha = (ManualCountdown.colorFor(phase, left) ushr 24) and 0xFF
            assertTrue(
                "$phase 剩 $left 时 alpha=0x%02X，字会看不见".format(alpha),
                alpha >= 0xB0,
            )
        }
    }

    /**
     * App 主页那张**白底**卡片用的配色：正常档、未开始档必须是深色。
     *
     * 悬浮窗的卡片底色是 #CC1B2028（深），所以 [ManualCountdown.colorFor] 的正常档
     * 给的是接近白的 #F2FFFF。同一组颜色搬到白底预览上就是**白字白底**——
     * 2026-09-22 现场实测：数字确实画出来了，却淡得看不见，用户报"数字不显示"。
     * 这里卡住"亮度要压得住白底"，别让谁再把浅色搬回去。
     */
    @Test
    fun lightCardColorsAreReadableOnWhite() {
        assertEquals(0xFF1B2028.toInt(), ManualCountdown.colorForLightCard(CdPhase.Running, Duration.ofMinutes(30)))
        assertEquals(0xFF6B7480.toInt(), ManualCountdown.colorForLightCard(CdPhase.Idle, Duration.ZERO))
        assertEquals(0xFFEF9F27.toInt(), ManualCountdown.colorForLightCard(CdPhase.Running, Duration.ofMinutes(5)))
        assertEquals(0xFFFF3B30.toInt(), ManualCountdown.colorForLightCard(CdPhase.Running, Duration.ofSeconds(30)))
        assertEquals(0xFFFF3B30.toInt(), ManualCountdown.colorForLightCard(CdPhase.Finished, Duration.ZERO))

        for ((phase, left) in listOf(CdPhase.Idle to Duration.ZERO, CdPhase.Running to Duration.ofHours(1))) {
            val c = ManualCountdown.colorForLightCard(phase, left)
            val lum = (((c shr 16) and 0xFF) * 299 + ((c shr 8) and 0xFF) * 587 + (c and 0xFF) * 114) / 1000
            assertTrue("$phase 剩 $left 的颜色亮度 $lum 在白底上太浅（要 < 160）", lum < 160)
        }
    }

    @Test
    fun subLabelExplainsWhyPauseIsMissing() {
        val clock = started("23:00")
        assertTrue("时刻模式要写明不可暂停", clock.subLabel().contains("不可暂停"))

        val duration = started("45m")
        assertFalse(duration.subLabel().contains("不可暂停"))
        assertTrue(duration.subLabel().contains("结束"))
    }
}

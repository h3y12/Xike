package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.DayOfWeek
import java.time.LocalDateTime
import java.time.LocalTime

/**
 * 闹钟核心逻辑单测。
 *
 * 与桌面版 `SelfTest.cs` 的「闹钟」那一节覆盖同一批规则 ——
 * 两端共用同一套闹钟语义，这些测试是让两边不走样的凭证。
 */
class AlarmTest {

    // ===================================================================================
    //  排序：以凌晨四点为界
    // ===================================================================================

    @Test
    fun sortKey_usesFourAmAsDayBoundary() {
        assertEquals(0, alarmAt(4, 0).sortKey)
        assertEquals(60, alarmAt(5, 0).sortKey)
        assertEquals(1140, alarmAt(23, 0).sortKey)
        assertEquals(1260, alarmAt(1, 0).sortKey)
        assertEquals(1439, alarmAt(3, 59).sortKey)
    }

    @Test
    fun sorted_putsEarlyMorningAfterLateNight() {
        val book = AlarmBook()
        // 故意乱序塞进去
        for (t in listOf(
            LocalTime.of(3, 0), LocalTime.of(23, 0), LocalTime.of(1, 0), LocalTime.of(4, 0),
            LocalTime.of(20, 30), LocalTime.of(2, 0), LocalTime.of(22, 0),
        )) {
            book.addManual(t, "", AlarmRepeat.DAILY, null, false)
        }

        val order = book.sorted().joinToString(" ") { it.timeText }
        assertEquals("04:00 20:30 22:00 23:00 01:00 02:00 03:00", order)
    }

    @Test
    fun fourAmComesBeforeFourAmMinusOneMinute() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(3, 59), "", AlarmRepeat.DAILY, null, false)
        book.addManual(LocalTime.of(4, 0), "", AlarmRepeat.DAILY, null, false)
        assertEquals("04:00 03:59", book.sorted().joinToString(" ") { it.timeText })
    }

    @Test
    fun nightOf_treatsBeforeFourAsPreviousDay() {
        val d = java.time.LocalDate.of(2026, 9, 24)
        assertEquals(d.minusDays(1), Alarm.nightOf(d.atTime(3, 59)))
        assertEquals(d, Alarm.nightOf(d.atTime(4, 0)))
        assertEquals(d, Alarm.nightOf(d.atTime(23, 0)))
    }

    // ===================================================================================
    //  下次响铃时刻
    // ===================================================================================

    private val wednesdayNight = LocalDateTime.of(2026, 9, 23, 20, 0)

    @Test
    fun nextRing_dailyLaterToday() {
        val a = Alarm("x", timeOfDay = LocalTime.of(22, 0), repeat = AlarmRepeat.DAILY)
        assertEquals(LocalDateTime.of(2026, 9, 23, 22, 0), a.nextRingAfter(wednesdayNight))
    }

    @Test
    fun nextRing_dailyTomorrowMorning() {
        val a = Alarm("x", timeOfDay = LocalTime.of(6, 30), repeat = AlarmRepeat.DAILY)
        assertEquals(LocalDateTime.of(2026, 9, 24, 6, 30), a.nextRingAfter(wednesdayNight))
    }

    @Test
    fun nextRing_earlyMorningBelongsToThisNight() {
        val a = Alarm("x", timeOfDay = LocalTime.of(1, 30), repeat = AlarmRepeat.DAILY)
        assertEquals(LocalDateTime.of(2026, 9, 24, 1, 30), a.nextRingAfter(wednesdayNight))
    }

    @Test
    fun nextRing_disabledIsNull() {
        val a = Alarm("x", timeOfDay = LocalTime.of(7, 0), enabled = false)
        assertNull(a.nextRingAfter(wednesdayNight))
    }

    @Test
    fun nextRing_weeklyOnlyOnPickedDays() {
        val a = Alarm(
            "x",
            timeOfDay = LocalTime.of(7, 0),
            repeat = AlarmRepeat.WEEKLY,
            days = setOf(DayOfWeek.MONDAY, DayOfWeek.WEDNESDAY),
        )
        // 周三晚八点 → 周三的 07:00 已过，下一个是下周一
        assertEquals(LocalDateTime.of(2026, 9, 28, 7, 0), a.nextRingAfter(wednesdayNight))
    }

    // ===================================================================================
    //  连锁起床闹钟
    // ===================================================================================

    @Test
    fun chain_lastAtIsComputed() {
        val spec = AlarmChainSpec(LocalTime.of(8, 0), 3, 3, daily = true)
        assertNull(spec.validate())
        assertEquals(LocalTime.of(8, 6), spec.lastAt)
    }

    @Test
    fun chain_generatesThreeAlarmsSharingChainId() {
        val book = AlarmBook()
        val made = book.addChain(AlarmChainSpec(LocalTime.of(8, 0), 3, 3, true), "起床")

        assertEquals(3, made.size)
        assertEquals("08:00 08:03 08:06", made.joinToString(" ") { it.timeText })
        assertTrue(made.all { it.chainId == made[0].chainId })
        assertEquals(listOf(1, 2, 3), made.map { it.chainIndex })
    }

    /**
     * 这是最关键的一条语义：**关掉一个，第二个照常响，而且时刻不漂移**。
     * 不是"关掉的时刻 + 间隔"，而是它自己那个固定钟点。
     */
    @Test
    fun chain_dismissingFirstDoesNotShiftTheSecond() {
        val book = AlarmBook()
        val made = book.addChain(AlarmChainSpec(LocalTime.of(8, 0), 3, 3, true), "起床")

        // 08:00 到点
        val first = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 7, 55),
            LocalDateTime.of(2026, 9, 23, 8, 0, 5),
        )
        assertEquals(1, first.size)
        assertEquals("08:00", first[0].alarm.timeText)

        val msg = book.dismiss(first[0].alarm)
        assertEquals("已关闭，还有 2 个会继续响。", msg)

        // 拖到 08:02 才点"知道了"，08:03 仍必须准时响
        val second = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 8, 0, 5),
            LocalDateTime.of(2026, 9, 23, 8, 3, 5),
        )
        assertEquals(1, second.size)
        assertEquals("08:03", second[0].alarm.timeText)
        assertFalse(second[0].isLastInChain)
    }

    @Test
    fun chain_lastOneEndsTheChain() {
        val book = AlarmBook()
        book.addChain(AlarmChainSpec(LocalTime.of(8, 0), 3, 3, true), "起床")

        // ⚠️ 必须走 `dueBetween` 拿到每一次响铃再 dismiss，不能直接 dismiss 没响过的闹钟。
        // 直接 dismiss 不会在 firedAt 里记账，那个闹钟到点时照样会被报出来 ——
        // 于是「只该报最后一个」的断言会变成「报了 2 个」。桌面版同一条用例也是这个写法。
        val first = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 7, 55),
            LocalDateTime.of(2026, 9, 23, 8, 0, 5),
        )
        assertEquals(1, first.size)
        assertEquals("08:00", first[0].alarm.timeText)
        assertFalse(first[0].isLastInChain)
        book.dismiss(first[0].alarm)

        val second = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 8, 0, 5),
            LocalDateTime.of(2026, 9, 23, 8, 3, 5),
        )
        assertEquals(1, second.size)
        assertEquals("08:03", second[0].alarm.timeText)
        assertFalse(second[0].isLastInChain)
        book.dismiss(second[0].alarm)

        val third = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 8, 3, 5),
            LocalDateTime.of(2026, 9, 23, 8, 6, 5),
        )
        assertEquals(1, third.size)
        assertEquals("08:06", third[0].alarm.timeText)
        assertTrue(third[0].isLastInChain)

        val msg = book.dismiss(third[0].alarm)
        assertEquals("这是最后一个，这一串起床闹钟已结束。", msg)
        // 重复的串关完后仍在
        assertEquals(3, book.size)
    }

    @Test
    fun chain_onceIsDeletedWholeAfterLastDismiss() {
        val book = AlarmBook()
        val made = book.addChain(AlarmChainSpec(LocalTime.of(8, 0), 3, 3, daily = false), "起床")
        assertEquals(3, book.size)

        book.dismiss(made[0])
        book.dismiss(made[1])
        book.dismiss(made[2])

        assertEquals(0, book.size)
    }

    @Test
    fun chain_dismissWholeChainTurnsEverythingOff() {
        val book = AlarmBook()
        val made = book.addChain(AlarmChainSpec(LocalTime.of(8, 0), 3, 3, true), "起床")

        val msg = book.dismissWholeChain(made[0])
        assertEquals("整串 3 个起床闹钟已全部关闭。", msg)
        // 重复的串是关掉（不删）
        assertEquals(3, book.size)
        assertTrue(book.all.none { it.enabled })
    }

    @Test
    fun chain_weekdaysOnly() {
        val book = AlarmBook()
        val made = book.addChain(
            AlarmChainSpec(LocalTime.of(6, 30), 5, 3, daily = true, weekdaysOnly = true),
            "起床",
        )
        assertEquals("周一、周二、周三、周四、周五", made[0].repeatText)
    }

    @Test
    fun chain_crossingMidnightWrapsAround() {
        val book = AlarmBook()
        val made = book.addChain(AlarmChainSpec(LocalTime.of(22, 0), 90, 3, true), "夜")
        assertEquals("22:00 23:30 01:00", made.joinToString(" ") { it.timeText })
        // 01:00 在四点分界里属于"这一夜的后半段"，所以排在最后
        assertEquals("22:00 23:30 01:00", book.sorted().joinToString(" ") { it.timeText })
    }

    @Test
    fun chain_rejectsOutOfRangeParams() {
        assertNotNull(AlarmChainSpec(count = 0).validate())
        assertNotNull(AlarmChainSpec(count = 13).validate())
        assertNotNull(AlarmChainSpec(intervalMinutes = 0).validate())
        assertNotNull(AlarmChainSpec(intervalMinutes = 121).validate())
        assertNull(AlarmChainSpec(intervalMinutes = 1).validate())
        assertNull(AlarmChainSpec(count = 1).validate())
    }

    @Test
    fun chain_infersSpecFromExisting() {
        val book = AlarmBook()
        val made = book.addChain(AlarmChainSpec(LocalTime.of(22, 0), 90, 3, true), "夜")
        val spec = book.inferChainSpec(made[0].chainId)
        assertNotNull(spec)
        assertEquals(90, spec!!.intervalMinutes)
        assertEquals(3, spec.count)
        assertEquals(LocalTime.of(22, 0), spec.startAt)
    }

    // ===================================================================================
    //  生命周期
    // ===================================================================================

    @Test
    fun once_deleteAfterRingIsRemoved() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(7, 0), "吃药", AlarmRepeat.ONCE, null, deleteAfterRing = true)

        val due = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 6, 59),
            LocalDateTime.of(2026, 9, 23, 7, 0, 5),
        )
        assertEquals(1, due.size)
        assertEquals("已删除这个一次性闹钟。", book.dismiss(due[0].alarm))
        assertEquals(0, book.size)
    }

    @Test
    fun once_keepWhenDeleteDisabled() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(7, 0), "吃药", AlarmRepeat.ONCE, null, deleteAfterRing = false)

        val due = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 6, 59),
            LocalDateTime.of(2026, 9, 23, 7, 0, 5),
        )
        assertEquals("已关闭，下次仍会响。", book.dismiss(due[0].alarm))
        assertEquals(1, book.size)
    }

    @Test
    fun snooze_addsANewAlarm() {
        val book = AlarmBook()
        val a = book.addManual(LocalTime.of(7, 0), "起床", AlarmRepeat.ONCE, null, true)
        val msg = book.snooze(a, LocalDateTime.of(2026, 9, 23, 7, 0, 10), 5)
        assertEquals("5 分钟后（07:05）再响一次。", msg)
        assertEquals(2, book.size)
    }

    @Test
    fun missedAlarms_areNotCaughtUp() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(2, 0), "半夜", AlarmRepeat.DAILY, null, false)

        // 半夜两点该响，但程序早上八点才醒 —— 不补
        val due = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 8, 0),
            LocalDateTime.of(2026, 9, 23, 8, 0, 1),
        )
        assertEquals(0, due.size)
    }

    @Test
    fun missedWithinGrace_areStillFired() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(8, 0), "刚过", AlarmRepeat.DAILY, null, false)

        // 差 30 秒，宽容窗口 90 秒 → 还算
        val due = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 8, 0, 30),
            LocalDateTime.of(2026, 9, 23, 8, 0, 31),
        )
        assertEquals(1, due.size)
    }

    @Test
    fun sameMomentIsReportedOnlyOnce() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(8, 0), "x", AlarmRepeat.DAILY, null, false)

        val first = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 7, 59, 59),
            LocalDateTime.of(2026, 9, 23, 8, 0, 1),
        )
        val second = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 8, 0, 0),
            LocalDateTime.of(2026, 9, 23, 8, 0, 1),
        )
        assertEquals(1, first.size)
        assertEquals(0, second.size)
    }

    // ===================================================================================
    //  回归：每天重复的闹钟必须跨天还响
    // ===================================================================================

    /**
     * 这是桌面版早期实现里的真 bug（`firedAt.containsKey` 让闹钟响过一次就报废），
     * 当时只测"今天响不响"发现不了。这里连着跑三天把它锁住。
     */
    @Test
    fun dailyAlarm_ringsAgainTheNextDay() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(7, 0), "起床", AlarmRepeat.DAILY, null, false)

        val day1 = book.dueBetween(
            LocalDateTime.of(2026, 9, 23, 6, 59),
            LocalDateTime.of(2026, 9, 23, 7, 0, 5),
        )
        assertEquals(1, day1.size)
        book.dismiss(day1[0].alarm)

        val day2 = book.dueBetween(
            LocalDateTime.of(2026, 9, 24, 6, 59),
            LocalDateTime.of(2026, 9, 24, 7, 0, 5),
        )
        assertEquals(1, day2.size)
        book.dismiss(day2[0].alarm)

        val day3 = book.dueBetween(
            LocalDateTime.of(2026, 9, 25, 6, 59),
            LocalDateTime.of(2026, 9, 25, 7, 0, 5),
        )
        assertEquals(1, day3.size)

        assertEquals(1, book.size)
    }

    @Test
    fun weeklyAlarm_onlyRingsOnPickedWeekdays() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(7, 0), "周一", AlarmRepeat.WEEKLY, setOf(DayOfWeek.MONDAY), false)

        // 2026-09-28 是周一
        val mon = book.dueBetween(
            LocalDateTime.of(2026, 9, 28, 6, 59),
            LocalDateTime.of(2026, 9, 28, 7, 0, 5),
        )
        assertEquals(1, mon.size)
        book.dismiss(mon[0].alarm)

        val tue = book.dueBetween(
            LocalDateTime.of(2026, 9, 29, 6, 59),
            LocalDateTime.of(2026, 9, 29, 7, 0, 5),
        )
        assertEquals(0, tue.size)

        val nextMon = book.dueBetween(
            LocalDateTime.of(2026, 10, 5, 6, 59),
            LocalDateTime.of(2026, 10, 5, 7, 0, 5),
        )
        assertEquals(1, nextMon.size)
    }

    // ===================================================================================
    //  临近判定（通知栏的判据）
    // ===================================================================================

    @Test
    fun imminent_isWithinThirtyMinutesInclusive() {
        val now = LocalDateTime.of(2026, 9, 23, 8, 0)
        val book = AlarmBook()
        book.addManual(LocalTime.of(8, 10), "十分钟后", AlarmRepeat.ONCE, null, true)
        book.addManual(LocalTime.of(8, 30), "半小时整", AlarmRepeat.ONCE, null, true)
        book.addManual(LocalTime.of(8, 31), "半小时零一分", AlarmRepeat.ONCE, null, true)
        book.addManual(LocalTime.of(9, 0), "一小时后", AlarmRepeat.ONCE, null, true)

        assertEquals(
            "08:10 08:30",
            book.imminent(now).joinToString(" ") { it.timeText },
        )
        assertEquals(LocalDateTime.of(2026, 9, 23, 8, 10), book.nextImminentRing(now))
    }

    @Test
    fun imminent_ignoresDisabled() {
        val now = LocalDateTime.of(2026, 9, 23, 8, 0)
        val book = AlarmBook()
        val a = book.addManual(LocalTime.of(8, 10), "关掉的", AlarmRepeat.ONCE, null, true)
        book.add(a.copy(enabled = false))

        assertEquals(0, book.imminent(now).size)
        assertNull(book.nextImminentRing(now))
    }

    // ===================================================================================
    //  响铃状态机
    // ===================================================================================

    @Test
    fun ringer_stopsAfterRingSeconds() {
        val ringer = AlarmRinger()
        val now = LocalDateTime.of(2026, 9, 23, 8, 0)
        val a = Alarm("a1", timeOfDay = LocalTime.of(8, 0))

        ringer.accept(listOf(AlarmFiring(a, now, true)), now, ringSeconds = 30)
        assertTrue(ringer.isRinging)

        // 20 秒还在响
        assertFalse(ringer.tick(now.plusSeconds(20), 30))
        assertTrue(ringer.isRinging)

        // 31 秒该停了
        assertTrue(ringer.tick(now.plusSeconds(31), 30))
        assertFalse(ringer.isRinging)
    }

    @Test
    fun ringer_queuesSeveralAlarms() {
        val ringer = AlarmRinger()
        val now = LocalDateTime.of(2026, 9, 23, 8, 0)
        val a1 = Alarm("a1", timeOfDay = LocalTime.of(8, 0))
        val a2 = Alarm("a2", timeOfDay = LocalTime.of(8, 1))

        ringer.accept(
            listOf(AlarmFiring(a1, now, false), AlarmFiring(a2, now.plusMinutes(1), true)),
            now,
            ringSeconds = 30,
        )
        assertEquals("a1", ringer.current?.alarm?.id)
        assertEquals(1, ringer.pendingCount)

        // 关掉第一个，第二个立刻接上
        ringer.dismissCurrent(now.plusSeconds(5), 30)
        assertEquals("a2", ringer.current?.alarm?.id)
        assertEquals(0, ringer.pendingCount)
    }

    @Test
    fun ringer_timeoutPicksUpNextQueued() {
        val ringer = AlarmRinger()
        val now = LocalDateTime.of(2026, 9, 23, 8, 0)
        val a1 = Alarm("a1", timeOfDay = LocalTime.of(8, 0))
        val a2 = Alarm("a2", timeOfDay = LocalTime.of(8, 1))

        ringer.accept(
            listOf(AlarmFiring(a1, now, false), AlarmFiring(a2, now, true)),
            now,
            ringSeconds = 30,
        )
        // 到点自动停 → 第二个顶上
        assertTrue(ringer.tick(now.plusSeconds(31), 30))
        assertEquals("a2", ringer.current?.alarm?.id)
    }

    // ===================================================================================
    //  存储序列化
    // ===================================================================================

    @Test
    fun store_roundTrips() {
        val book = AlarmBook()
        book.addManual(LocalTime.of(6, 30), "起床", AlarmRepeat.WEEKLY, setOf(DayOfWeek.MONDAY, DayOfWeek.FRIDAY), false)
        book.addChain(AlarmChainSpec(LocalTime.of(8, 0), 3, 2, true), "连锁")

        val text = AlarmStore.serialize(
            book.sorted().map {
                AlarmStore.Record(
                    id = it.id, at = it.timeText, label = it.label, on = it.enabled,
                    repeat = when (it.repeat) {
                        AlarmRepeat.ONCE -> "once"
                        AlarmRepeat.DAILY -> "daily"
                        AlarmRepeat.WEEKLY -> "weekly"
                    },
                    days = Alarm.daysToText(it.days), deleteAfterRing = it.deleteAfterRing,
                    chain = it.chainId, chainIndex = it.chainIndex,
                )
            },
        )

        val back = AlarmStore.parse(text)
        assertEquals(book.size, back.size)
        assertEquals("06:30", back[0].at)
        assertEquals("起床", back[0].label)
        assertEquals("weekly", back[0].repeat)
        assertEquals("1,5", back[0].days)
        assertFalse(back[0].on == false)   // 生效
        assertEquals("08:00", back[1].at)
        assertTrue(back[1].chain.isNotEmpty())
        assertEquals(1, back[1].chainIndex)
    }

    @Test
    fun store_toleratesBadInput() {
        // 空、坏行、字段少了，都不该抛
        assertEquals(0, AlarmStore.parse("").size)
        assertEquals(0, AlarmStore.parse(null).size)
        assertEquals(0, AlarmStore.parse("只有一段").size)

        val partial = AlarmStore.parse("id1\u000107:00")
        assertEquals(1, partial.size)
        assertEquals("07:00", partial[0].at)
        assertTrue(partial[0].on)              // 缺字段取默认
        assertEquals("once", partial[0].repeat)
    }

    @Test
    fun daysText_roundTrips() {
        val days = setOf(DayOfWeek.MONDAY, DayOfWeek.WEDNESDAY, DayOfWeek.SUNDAY)
        val text = Alarm.daysToText(days)
        assertEquals("1,3,7", text)
        assertEquals(days, Alarm.daysFromText(text))
    }

    @Test
    fun daysText_ignoresGarbage() {
        assertEquals(emptySet<DayOfWeek>(), Alarm.daysFromText("0,8,abc,"))
        assertEquals(setOf(DayOfWeek.MONDAY), Alarm.daysFromText("1"))
    }

    // ---- 小工具 ----

    private fun alarmAt(h: Int, m: Int) = Alarm("x", timeOfDay = LocalTime.of(h, m))
}

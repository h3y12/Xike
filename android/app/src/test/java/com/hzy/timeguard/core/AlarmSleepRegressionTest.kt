package com.hzy.timeguard.core

import java.time.LocalDateTime
import java.time.LocalTime
import org.junit.Assert.assertEquals
import org.junit.Test

class AlarmSleepRegressionTest {
    private val due = LocalDateTime.of(2026, 10, 11, 8, 0)
    private fun book() = AlarmBook().apply {
        add(Alarm(id = "daily", timeOfDay = LocalTime.of(8, 0), repeat = AlarmRepeat.DAILY))
    }
    @Test fun wakingAnHourLateDoesNotRing() {
        assertEquals(0, book().dueBetween(due.minusSeconds(1), due.plusHours(1)).size)
    }
    @Test fun yesterdayOccurrenceDoesNotHideToday() {
        val b = book()
        assertEquals(1, b.dueBetween(due.minusSeconds(1), due).size)
        assertEquals(1, b.dueBetween(due, due.plusDays(1)).size)
        assertEquals(0, b.dueBetween(due.plusDays(1), due.plusDays(1).plusSeconds(1)).size)
    }
    @Test fun graceBoundaryIsMeasuredAgainstCurrentTime() {
        assertEquals(1, book().dueBetween(due.minusSeconds(1), due.plusSeconds(90)).size)
        assertEquals(0, book().dueBetween(due.minusSeconds(1), due.plusSeconds(91)).size)
    }
}

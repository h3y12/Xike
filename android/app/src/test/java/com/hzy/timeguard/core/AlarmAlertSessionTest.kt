package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.LocalDateTime

class AlarmAlertSessionTest {
    private val now = LocalDateTime.of(2026, 10, 7, 8, 0)
    private class Output : AlarmAlertOutput {
        val events = mutableListOf<String>()
        override fun startSound() { events += "sound on" }
        override fun stopSound() { events += "sound off" }
        override fun startVibration() { events += "vibration on" }
        override fun stopVibration() { events += "vibration off" }
    }
    private fun firing(id: String) = AlarmFiring(Alarm(id, timeOfDay = now.toLocalTime()), now, true)

    @Test fun outputsContinueUntilConfiguredTimeout() {
        val output = Output()
        val session = AlarmAlertSession(output)
        val ringer = AlarmRinger()
        ringer.accept(listOf(firing("one")), now, 30)
        session.sync(ringer.current, true)
        for (second in 1L..29L) {
            ringer.tick(now.plusSeconds(second), 30)
            session.sync(ringer.current, true)
        }
        assertEquals(listOf("sound on", "vibration on"), output.events)
        ringer.tick(now.plusSeconds(30), 30)
        session.sync(ringer.current, true)
        assertEquals(listOf("sound on", "vibration on", "sound off", "vibration off"), output.events)
    }

    @Test fun dismissStopsBothOutputsAndStartsQueuedAlarm() {
        val output = Output()
        val session = AlarmAlertSession(output)
        val ringer = AlarmRinger()
        ringer.accept(listOf(firing("one"), firing("two")), now, 30)
        session.sync(ringer.current, true)
        ringer.dismissCurrent(now.plusSeconds(1), 30)
        session.sync(ringer.current, true)
        assertEquals(listOf("sound on", "vibration on", "sound off", "vibration off", "sound on", "vibration on"), output.events)
        ringer.dismissCurrent(now.plusSeconds(2), 30)
        session.sync(ringer.current, true)
        assertEquals(listOf("sound off", "vibration off"), output.events.takeLast(2))
    }

    @Test fun vibrationCanBeDisabledWhileSoundContinues() {
        val output = Output()
        val session = AlarmAlertSession(output)
        val state = RingingState(firing("one").alarm, now, now.plusSeconds(30))
        session.sync(state, false)
        assertEquals(listOf("sound on"), output.events)
        session.sync(state, true)
        session.sync(state, false)
        assertEquals(listOf("sound on", "vibration on", "vibration off"), output.events)
        session.stop()
        session.stop()
        assertEquals("sound off", output.events.last())
        assertEquals(1, output.events.count { it == "sound off" })
    }
}

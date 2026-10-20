package com.hzy.timeguard.core

interface AlarmAlertOutput {
    fun startSound()
    fun stopSound()
    fun startVibration()
    fun stopVibration()
}

/** Keeps the outputs alive for the actual ringing state, including queued alarms. */
class AlarmAlertSession(private val output: AlarmAlertOutput) {
    private var current: RingingState? = null
    private var vibrating = false

    fun sync(next: RingingState?, vibrate: Boolean) {
        if (next != current) {
            stop()
            current = next
            if (next != null) output.startSound()
        }
        val shouldVibrate = next != null && vibrate
        if (shouldVibrate != vibrating) {
            if (shouldVibrate) output.startVibration() else output.stopVibration()
            vibrating = shouldVibrate
        }
    }

    fun stop() {
        if (current != null) output.stopSound()
        if (vibrating) output.stopVibration()
        current = null
        vibrating = false
    }
}

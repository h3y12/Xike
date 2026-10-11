package com.hzy.timeguard.core

import java.time.LocalDateTime

/** 当前正在响的这次闹钟。 */
data class RingingState(
    val alarm: Alarm,
    val startedAt: LocalDateTime,
    val endsAt: LocalDateTime,
) {
    /** 距离停止还有几秒。 */
    fun secondsLeft(now: LocalDateTime): Int {
        val s = java.time.Duration.between(now, endsAt).seconds
        return if (s <= 0) 0 else s.toInt()
    }
}

/**
 * 响铃的调度：把「到期事件」变成「开始响 / 响够了停 / 用户处理掉」。
 *
 * 与 [AlarmBook] 的分工：Book 只管"到点了没"，本类只管"响了之后怎样"。
 * 两边都不读系统时钟（时间由调用方传进来），因此单测可以任意快进。
 * 与桌面版 `AlarmRinger.cs` 一一对应。
 */
class AlarmRinger {

    /** 当前正在响的（同一时刻最多一个 —— 后到的排队等前一个被处理）。 */
    var current: RingingState? = null
        private set

    private val queue = ArrayDeque<AlarmFiring>()

    val isRinging: Boolean get() = current != null

    val pendingCount: Int get() = queue.size

    /**
     * 收下一批到期事件。当前没在响的话，立刻开始响第一个。
     */
    fun accept(firings: List<AlarmFiring>, now: LocalDateTime, ringSeconds: Int) {
        firings.forEach { queue.addLast(it) }
        if (isRinging) return
        beginNext(now, ringSeconds)
    }

    private fun beginNext(now: LocalDateTime, ringSeconds: Int) {
        val f = queue.removeFirstOrNull()
        if (f == null) {
            current = null
            return
        }

        val secs = if (ringSeconds < 1) 1 else ringSeconds
        current = RingingState(
            alarm = f.alarm,
            startedAt = now,
            endsAt = now.plusSeconds(secs.toLong()),
        )
    }

    /**
     * 响铃期间每帧调一次：到点自动停，接着响下一个排队的。
     * 返回 true 表示"这一帧刚刚自动停下来了"。
     */
    fun tick(now: LocalDateTime, ringSeconds: Int): Boolean {
        val cur = current ?: return false
        if (now < cur.endsAt) return false

        current = null
        beginNext(now, ringSeconds)
        return true
    }

    /** 用户处理掉了当前这次响铃，开始响下一个排队的。 */
    fun dismissCurrent(now: LocalDateTime, ringSeconds: Int) {
        current = null
        beginNext(now, ringSeconds)
    }

    fun stopAll() {
        queue.clear()
        current = null
    }

    companion object {
        /** 响铃时显示的那行提示语。 */
        fun ringingText(a: Alarm): String {
            val who = if (a.label.isBlank()) "闹钟" else a.label
            return "${who}时间到"
        }
    }
}

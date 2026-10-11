package com.hzy.timeguard.core

import java.time.Duration
import java.time.LocalDateTime
import java.time.LocalTime
import kotlin.math.ceil

/** 倒计时类型。 */
enum class CdMode {
    /** 到某个钟点（原 timecountdown.c 的语义：填时分秒，到点为止）。 */
    ClockTime,

    /** 从此刻起计 N 分钟。 */
    Duration,
}

enum class CdPhase {
    /** 还没设目标，输入框待命。 */
    Idle,
    Running,
    Paused,

    /** 已到点（原程序打印的「时间到！」）。 */
    Finished,
}

/** 一次倒计时的设定，由输入文本解析得到。 */
data class CdPlan(
    val mode: CdMode,
    /** [CdMode.ClockTime] 的目标时刻。 */
    val clockTarget: LocalTime = LocalTime.MIDNIGHT,
    /** [CdMode.Duration] 的分钟数。 */
    val durationMinutes: Int = 0,
    /** 界面上那行小字，说明这次在数什么。 */
    val label: String = "",
)

data class CdState(val phase: CdPhase, val remaining: Duration) {
    val hasActiveTime: Boolean
        get() = (phase == CdPhase.Running || phase == CdPhase.Paused) && remaining > Duration.ZERO
}

/** 解析结果。成功给出方案，失败给出中文原因。 */
sealed interface CdParse {
    data class Ok(val plan: CdPlan) : CdParse
    data class Err(val message: String) : CdParse
}

/** 落盘用的扁平快照。 */
data class CdSnapshot(
    val mode: String = "",
    /** HH:mm:ss */
    val clockTarget: String = "",
    val durationMinutes: Int = 0,
    /** ISO 本地日期时间 */
    val deadline: String = "",
    val phase: String = CdPhase.Idle.name,
    /** 暂停时的剩余量：整秒部分。 */
    val pausedSeconds: Long = 0L,
    /**
     * 暂停时的剩余量：不足一秒的部分（0–999999999）。
     *
     * 单独一个字段是为了**兼容旧快照**——老版本只存了 [pausedSeconds]，
     * 读回来这一项是 0，正好等价于旧行为，不需要写迁移代码。
     */
    val pausedNanos: Long = 0L,
)

/**
 * 手动倒计时（纯逻辑，不依赖界面，可用单元测试独立核对）。
 *
 * 与桌面版 `Countdown.cs` 同源，语义完全一致：
 *  · 输入 `23:00` → 数到那个钟点（今天已过则顺延到明天）
 *  · 输入 `45m` / `2h` / `45` → 数 45 分钟 / 2 小时
 *  · **与健康分没有任何关系**，它只是个数
 *
 * 剩余时间一律用**绝对截止时刻**现算（`deadline - now`），所以锁屏、进程被杀、
 * 手机重启都不会让它「停住」——重新亮屏时就按真实时间接着算。
 * 唯一能把截止点整体往后推的是**暂停**，而暂停只有「N 分钟后」这种时长计时才允许：
 * 「到某个钟点」的目标就是那个钟点本身，冻结剩余量再继续等于把 23:30 变成 23:30:10。
 */
class ManualCountdown {

    var phase: CdPhase = CdPhase.Idle
        private set

    var plan: CdPlan? = null
        private set

    /** 输入框里的原文，落盘后下次启动还在。 */
    var inputText: String = ""

    /** 绝对截止时刻。 */
    private var deadline: LocalDateTime = LocalDateTime.MIN
    private var pausedRemaining: Duration = Duration.ZERO

    /** 这次倒计时能不能暂停，见类注释。界面据此决定按钮要不要置灰。 */
    val pauseAllowed: Boolean get() = plan?.mode == CdMode.Duration

    // ---------------------------------------------------------------- 状态机

    fun start(p: CdPlan, now: LocalDateTime) {
        plan = p
        deadline = if (p.mode == CdMode.ClockTime) {
            deadlineFor(p.clockTarget, now)
        } else {
            now.plusMinutes(p.durationMinutes.toLong())
        }

        // 时长模式的说明文字里带结束钟点，这里以真实截止点为准再写一次
        if (p.mode == CdMode.Duration) {
            plan = p.copy(label = "到 " + hm(deadline) + " 结束")
        }

        pausedRemaining = Duration.ZERO
        phase = CdPhase.Running
    }

    /** 到点后点「再来」：时刻模式顺延到下一个同一时刻，时长模式重新计满一轮。 */
    fun restart(now: LocalDateTime) {
        val p = plan ?: return
        start(p, now)
    }

    /** 暂停。时刻模式直接拒绝（返回 false，状态一点不动）。 */
    fun pause(now: LocalDateTime): Boolean {
        if (phase != CdPhase.Running) return false
        if (!pauseAllowed) return false

        val left = Duration.between(now, deadline)
        pausedRemaining = if (left.isNegative) Duration.ZERO else left
        phase = if (pausedRemaining.isZero) CdPhase.Finished else CdPhase.Paused
        return true
    }

    fun resume(now: LocalDateTime) {
        if (phase != CdPhase.Paused) return
        if (pausedRemaining.isZero || pausedRemaining.isNegative) {
            phase = CdPhase.Finished
            return
        }
        deadline = now.plus(pausedRemaining)

        // 说明文字里的结束钟点是**在 start() 里按当时的截止点写死的**。
        // 暂停会把截止点整体往后推（暂停多久就推多久），继续以后必须重写一次，
        // 否则小字还显示着「到 20:45 结束」，而真实截止点已经变成 20:55。
        plan = plan?.let { p ->
            if (p.mode == CdMode.Duration) p.copy(label = "到 " + hm(deadline) + " 结束") else p
        }

        phase = CdPhase.Running
    }

    /** 「重置」：回到输入态。相当于原程序里敲 0 结束。 */
    fun reset() {
        phase = CdPhase.Idle
        plan = null
        pausedRemaining = Duration.ZERO
    }

    /**
     * 推进到 [now]。到点会自动落到 [CdPhase.Finished]，只在这里改状态，
     * 调用方不用自己判。
     */
    fun tick(now: LocalDateTime): CdState = when (phase) {
        CdPhase.Idle -> CdState(CdPhase.Idle, Duration.ZERO)
        CdPhase.Paused -> CdState(CdPhase.Paused, pausedRemaining)
        CdPhase.Finished -> CdState(CdPhase.Finished, Duration.ZERO)
        CdPhase.Running -> {
            val left = Duration.between(now, deadline)
            if (left.isZero || left.isNegative) {
                pausedRemaining = Duration.ZERO
                phase = CdPhase.Finished
                CdState(CdPhase.Finished, Duration.ZERO)
            } else {
                CdState(CdPhase.Running, left)
            }
        }
    }

    /** 大字下面的小字。 */
    fun subLabel(multiline: Boolean = false): String {
        val basis = plan?.label ?: ""
        val separator = if (multiline) "\n" else " · "
        return when {
            phase == CdPhase.Idle -> "23:00 到点 / 45m 计时"
            phase == CdPhase.Paused -> "已暂停$separator$basis"
            phase == CdPhase.Finished -> "$basis${separator}已到点"
            // 时刻模式在跑的时候说清「不能暂停」，免得用户找不到暂停按钮还以为是漏了
            phase == CdPhase.Running && !pauseAllowed -> "$basis${separator}不可暂停"
            else -> basis
        }
    }

    // ---------------------------------------------------------------- 落盘 / 还原

    fun snapshot(): CdSnapshot {
        val p = plan ?: return CdSnapshot()
        return CdSnapshot(
            mode = p.mode.name,
            clockTarget = hmsFull(p.clockTarget),
            durationMinutes = p.durationMinutes,
            deadline = if (deadline == LocalDateTime.MIN) "" else deadline.toString(),
            phase = phase.name,
            // 整秒与纳秒分开存。只存整秒会把 59.5s 截成 59s，恢复后虽然还剩 0.5s
            // 但 floor 到秒就成了「已到点」（实测：59.5s 暂停 → 恢复变 Finished）。
            pausedSeconds = pausedRemaining.seconds,
            pausedNanos = pausedRemaining.nano.toLong(),
        )
    }

    /**
     * 从配置还原。Running 存的是绝对截止时刻，所以 App 没开的这段时间照常算进去
     * （设了 45 分钟，关掉 App 50 分钟后再打开，看到的是「时间到！」而不是重新数 45 分钟）。
     */
    fun restore(s: CdSnapshot, now: LocalDateTime) {
        reset()

        val m = runCatching { CdMode.valueOf(s.mode) }.getOrNull() ?: return
        val restoredPhase = runCatching { CdPhase.valueOf(s.phase) }.getOrNull() ?: return
        if (restoredPhase == CdPhase.Idle) return

        val target = SleepRules.tryParseHm(s.clockTarget) ?: LocalTime.MIDNIGHT
        val storedDeadline = runCatching { LocalDateTime.parse(s.deadline) }.getOrNull()
        val parsedDeadline = storedDeadline ?: now

        plan = CdPlan(
            mode = m,
            clockTarget = if (m == CdMode.ClockTime) target else LocalTime.MIDNIGHT,
            durationMinutes = if (m == CdMode.Duration) s.durationMinutes else 0,
            label = if (m == CdMode.ClockTime) {
                "到 " + hmOrHms(target) + " 为止"
            } else {
                "到 " + hm(parsedDeadline) + " 结束"
            },
        )

        if (m == CdMode.ClockTime) {
            // 已经到过点了就还是到点。不加这一步的话，第二天早上打开 App 会把
            // 昨晚早已结束的计时「顺延到今晚 23:30」，看着像自己重开了一轮。
            if (restoredPhase == CdPhase.Finished) {
                deadline = parsedDeadline
                pausedRemaining = Duration.ZERO
                phase = CdPhase.Finished
                return
            }

            // 取「≤ 存下来的截止点的最后一个同钟点」，就是这次倒计时原本的目标。
            // 若直接按 now 顺延，昨晚早已结束的计时会在第二天早上被「续」到今晚，
            // 看起来像自己重开了一轮。（桌面版 Restore 里同一个用意。）
            deadline = if (storedDeadline != null) {
                deadlineFor(target, storedDeadline.minusDays(1))
            } else {
                deadlineFor(target, now)
            }
            pausedRemaining = Duration.ZERO
            phase = if (deadline.isAfter(now)) CdPhase.Running else CdPhase.Finished
            return
        }

        deadline = parsedDeadline

        if (restoredPhase == CdPhase.Paused) {
            // 秒 + 纳秒一起还原；只按秒还原会让 0–1 秒之间的剩余量塌成 0（见 snapshot 的说明）。
            // 旧快照没有 pausedNanos 这一项，字段默认 0 → 行为与改动前一致，能平滑升级。
            val nanos = s.pausedSeconds.coerceAtLeast(0L) * 1_000_000_000L +
                s.pausedNanos.coerceIn(0L, 999_999_999L)
            pausedRemaining = Duration.ofNanos(nanos)
            phase = if (pausedRemaining.isZero) CdPhase.Finished else CdPhase.Paused
            if (phase == CdPhase.Paused) deadline = now.plus(pausedRemaining)
            return
        }

        if (restoredPhase == CdPhase.Finished || !deadline.isAfter(now)) {
            phase = CdPhase.Finished
            pausedRemaining = Duration.ZERO
            return
        }

        phase = CdPhase.Running
    }

    companion object {

        const val MAX_DURATION_MINUTES = 24 * 60

        /** 未开始时的暗白；const 不接受位运算，直接写等价的负数字面量。 */
        private val IDLE_COLOR = 0xB0FFFFFF.toInt()

        /**
         * 不透明的 alpha。**这行是 2026-09-22 那次「倒计时数字不显示」的根因**：
         * 颜色写成 6 位十六进制时，Kotlin 把它当 Int 解 → `0xF2FFFF` 其实是
         * `0x00F2FFFF`，**alpha 通道 = 0，字画出来是全透明的**。
         * 未开始那一档因为写的是 8 位 `0xB0FFFFFF` 才有 alpha 0xB0 —— 于是现象特别迷惑：
         * 未开始时看得到 `--:--:--`，一按开始数字反而"消失"。
         * 桌面版 `Countdown.ColorFor` 用的是 `Color.FromRgb(...)`（不透明），
         * 所以移植时必须把这一位补回来。
         */
        private const val OPAQUE = -0x1000000 // 0xFF000000

        /**
         * 认这些写法：
         *  · 23:00 / 23:00:30 / 23   → 到那个钟点（已过则顺延到明天）
         *  · 45m / 45分钟 / 2h       → 时长模式
         *  · 纯数字 &gt; 23           → 分钟数（45 就是 45 分钟）
         */
        fun tryParse(text: String?, now: LocalDateTime): CdParse {
            if (text.isNullOrBlank()) {
                return CdParse.Err("先填一个时间：23:00 表示到点，45m 表示 45 分钟后。")
            }

            val raw = text.trim()
            val s = raw
                .replace('：', ':')
                .replace('．', ':')
                .replace('。', ':')
                .replace("分钟", "m")
                .replace("分", "m")
                .replace("小时", "h")
                .replace("钟头", "h")
                .replace(" ", "")

            // ---- 时刻模式：带冒号 ----
            if (s.contains(':')) {
                val target = SleepRules.tryParseHm(s)
                    ?: return CdParse.Err("「$raw」读不出来，按 23:00 或 23:00:30 这样填。")
                return CdParse.Ok(
                    CdPlan(
                        mode = CdMode.ClockTime,
                        clockTarget = target,
                        label = "到 " + hmOrHms(target) + " 为止",
                    ),
                )
            }

            var digits = s
            var unit = '\u0000'
            val last = s.lastOrNull()
            if (last != null && last.code !in 48..57) {
                unit = last.lowercaseChar()
                digits = s.dropLast(1)
            }

            val n = digits.toIntOrNull()
                ?: return CdParse.Err("「$raw」读不出来，按 23:00 或 45m 这样填。")

            if (unit == '\u0000') {
                // 纯数字：≤23 当整点，>23 当分钟数
                if (n in 0..23) {
                    val t = LocalTime.of(n, 0)
                    return CdParse.Ok(
                        CdPlan(
                            mode = CdMode.ClockTime,
                            clockTarget = t,
                            label = "到 " + hmOrHms(t) + " 为止",
                        ),
                    )
                }
                if (n > 23) return buildDuration(n, raw, now)
                return CdParse.Err("「$raw」读不出来，按 23:00 或 45m 这样填。")
            }

            if (unit == 'h') {
                if (n < 1 || n > 24) return CdParse.Err("「$raw」的小时数要在 1–24 之间。")
                return buildDuration(n * 60, raw, now)
            }

            if (unit == 'm') return buildDuration(n, raw, now)

            return CdParse.Err("「$raw」的单位只认 m（分钟）和 h（小时）。")
        }

        private fun buildDuration(minutes: Int, raw: String, now: LocalDateTime): CdParse {
            if (minutes < 1 || minutes > MAX_DURATION_MINUTES) {
                return CdParse.Err("「$raw」换算成 $minutes 分钟，超出 1 分钟–24 小时的可用范围。")
            }
            return CdParse.Ok(
                CdPlan(
                    mode = CdMode.Duration,
                    durationMinutes = minutes,
                    label = "到 " + hm(now.plusMinutes(minutes.toLong())) + " 结束",
                ),
            )
        }

        /** 目标时刻落在今天还是明天（已过就顺延）。 */
        private fun deadlineFor(target: LocalTime, now: LocalDateTime): LocalDateTime {
            val today = LocalDateTime.of(now.toLocalDate(), target)
            return if (today.isAfter(now)) today else today.plusDays(1)
        }

        /** 大字本身。 */
        fun bigText(phase: CdPhase, remaining: Duration): String = when (phase) {
            CdPhase.Idle -> "--:--:--"
            CdPhase.Finished -> "时间到！"
            else -> hms(remaining)
        }

        /** 颜色随剩余时间收紧：>5 分钟白、≤5 分钟琥珀、≤1 分钟红；未开始为暗白。
         *  每一档都要 [OPAQUE]，否则字是全透明的（见 [OPAQUE] 的说明）。 */
        fun colorFor(phase: CdPhase, remaining: Duration): Int = when {
            phase == CdPhase.Finished -> OPAQUE or 0xFF3B30
            phase == CdPhase.Idle -> IDLE_COLOR
            remaining <= Duration.ofMinutes(1) -> OPAQUE or 0xFF3B30
            remaining <= Duration.ofMinutes(5) -> OPAQUE or 0xEF9F27
            else -> OPAQUE or 0xF2FFFF
        }

        /**
         * **白色卡片上**用的配色（App 主页那张实时预览）。
         *
         * 悬浮窗的卡片是深色的，所以 [colorFor] 的"正常档"给的是接近白的 #F2FFFF——
         * 搬到白底预览上就成了白字白底。2026-09-22 用户报"开始倒计时后数字不显示"，
         * 一半是 alpha=0（见 [OPAQUE]），另一半正是这个：淡得像没画。
         * 这里把两档"白"换成 App 自己的正文色（主要文字 / 次要文字），
         * 琥珀与红两档沿用（在白底上是饱和色，看得清），改一个字都没有多说。
         */
        fun colorForLightCard(phase: CdPhase, remaining: Duration): Int = when {
            phase == CdPhase.Finished -> OPAQUE or 0xFF3B30
            phase == CdPhase.Idle -> OPAQUE or 0x6B7480 // 与 App 的次要文字同色
            remaining <= Duration.ofMinutes(1) -> OPAQUE or 0xFF3B30
            remaining <= Duration.ofMinutes(5) -> OPAQUE or 0xEF9F27
            else -> OPAQUE or 0x1B2028 // 与 App 的主要文字同色
        }

        /**
         * HH:mm:ss，小时两位（跨零点也不会显示成负）。
         * 剩余时间**向上取整到秒**：剩 44:59.997 时显示 00:45:00，跨过整秒才跳，
         * 否则「点了开始」到「第一次刷新」之间那几毫秒会让它一上来就少一秒。
         */
        fun hms(d: Duration): String {
            val secs = if (d.isNegative) 0L else ceil(d.seconds + d.nano / 1e9).toLong()
            val h = secs / 3600
            val m = secs % 3600 / 60
            val s = secs % 60
            return "%02d:%02d:%02d".format(h, m, s)
        }

        fun hm(t: LocalDateTime): String = "%02d:%02d".format(t.hour, t.minute)

        private fun hmsFull(t: LocalTime): String = "%02d:%02d:%02d".format(t.hour, t.minute, t.second)

        private fun hmOrHms(t: LocalTime): String =
            if (t.second == 0) "%02d:%02d".format(t.hour, t.minute) else hmsFull(t)
    }
}

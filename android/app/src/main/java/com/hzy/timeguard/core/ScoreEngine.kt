package com.hzy.timeguard.core

import java.time.Duration
import java.time.LocalDateTime

enum class NightPhase {
    /** 隐藏时段（默认 06:30 ~ 20:30），悬浮窗不显示。 */
    Daytime,

    /** 出现后到扣分起点之间，满分待命。 */
    Countdown,

    /** 扣分中。 */
    Deducting,

    /** 已扣光，锁定 0 分。 */
    Exhausted,
}

data class ScoreState(
    val score: Int,
    val phase: NightPhase,
    /** [NightPhase.Countdown] 时表示「距开始扣分还有多久」；其余阶段为 [Duration.ZERO]。 */
    val untilDeadline: Duration,
)

/**
 * 健康分规则（纯函数，不依赖界面，可用单元测试独立核对）。
 *
 * **口径与桌面版 `ScoreEngine.Evaluate` 完全一致**：扣分的依据是**时钟**——
 * 从「开始扣分」那一刻起算，过了多少时间就扣多少分，与你在不在用手机无关。
 * 例：23:30 开始扣分、每 3 分钟 1 分，则 23:33 是 99 分，00:06 是 88 分。
 *
 * 这**不是**「用了多久手机」的统计：手机放在床头没碰，时间照样在走，分照样扣。
 * 手机版曾经自作主张改成过「亮屏累计」，那是桌面版从来没有的规则，已废除。
 *
 * 默认口径：20:30 出现 100 分；23:30 前始终 100 分；23:30 起每 3 分钟 1 分；
 * 扣到 0 后锁 0，不出现负数；06:30 隐藏。
 */
object ScoreEngine {

    /** 与 [SleepRules.maxScore] 的默认值一致；仅用于测试断言。 */
    const val MAX_SCORE = 100

    fun evaluate(now: LocalDateTime, rules: SleepRules): ScoreState {
        val r = rules.resolve(now)

        if (now < r.show || now >= r.hide) {
            return ScoreState(rules.maxScore, NightPhase.Daytime, Duration.ZERO)
        }
        if (now < r.deduct) {
            return ScoreState(rules.maxScore, NightPhase.Countdown, Duration.between(now, r.deduct))
        }

        // 与桌面版逐字对应：先取整秒，再整除步长，避免浮点误差把整点算少一分
        val elapsedSeconds = Duration.between(r.deduct, now).seconds
        val stepSeconds = rules.minutesPerPoint * 60L
        val dropped = elapsedSeconds / stepSeconds * rules.pointsPerStep
        val raw = rules.maxScore - dropped

        val score = if (raw <= 0) 0 else raw.toInt()
        val phase = if (score == 0) NightPhase.Exhausted else NightPhase.Deducting
        return ScoreState(score, phase, Duration.ZERO)
    }

    /** 格式化成 h:mm:ss（如 2:14:30）。 */
    fun formatHms(d: Duration): String {
        val safe = if (d.isNegative) Duration.ZERO else d
        val totalHours = safe.toHours()
        val minutes = safe.toMinutes() % 60
        val seconds = safe.seconds % 60
        return "$totalHours:%02d:%02d".format(minutes, seconds)
    }

    /** 格式化成 m:ss 或 h:mm:ss —— 悬浮窗窄，能短则短。 */
    fun formatShort(d: Duration): String {
        val safe = if (d.isNegative) Duration.ZERO else d
        val h = safe.toHours()
        val m = safe.toMinutes() % 60
        val s = safe.seconds % 60
        return if (h > 0) "$h:%02d:%02d".format(m, s) else "$m:%02d".format(s)
    }
}

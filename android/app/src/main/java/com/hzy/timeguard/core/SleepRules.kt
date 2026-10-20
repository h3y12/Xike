package com.hzy.timeguard.core

import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import kotlin.math.ceil

/**
 * 一夜的规则参数。
 *
 * 与桌面版 `SleepRules.cs` 一一对应，时间语义完全相同：
 * [showAt]（出现/开始记分）→ [deductAt]（开始扣分）→ [hideAt]（隐藏），
 * 跨零点按「一夜」的顺序解释，不按当天先后。
 */
data class SleepRules(
    /** 悬浮窗出现、开始记分。 */
    val showAt: LocalTime = LocalTime.of(20, 30),

    /** 开始扣分。 */
    val deductAt: LocalTime = LocalTime.of(23, 30),

    /** 悬浮窗隐藏。 */
    val hideAt: LocalTime = LocalTime.of(6, 30),

    /** 每多少分钟扣一次。（手机版里指的是「亮屏累计」的分钟数） */
    val minutesPerPoint: Int = 3,

    /** 每次扣多少分。 */
    val pointsPerStep: Int = 1,

    /** 起始分，也是上限。 */
    val maxScore: Int = 100,

    // ---- 状态文案（显示在分数下方那一行，可在设置里改）----
    // 留空是合法的：表示「这个状态下不显示任何文字」，不是错误。

    /** 已出现、还没开始扣分时的文案。 */
    val statusBefore: String = "夜深了，注意休息~",

    /** 高分段的文案（≥ [ScoreVisual.HIGH_LINE]）。 */
    val statusHigh: String = "快去睡吧。",

    /** 中分段的文案（[ScoreVisual.MID_LINE] – [ScoreVisual.HIGH_LINE]-1）。 */
    val statusMid: String = "头发要紧，睡觉！",

    /** 低分段的文案（< [ScoreVisual.MID_LINE]）。 */
    val statusLow: String = "不睡等死吧你！！！",
) {
    /** 扣光所需时长（从 [deductAt] 起算）。 */
    val spanToZero: java.time.Duration
        get() = java.time.Duration.ofMinutes(ceil(maxScore.toDouble() / pointsPerStep).toLong() * minutesPerPoint)

    /** 把规则里的三个时刻，落到 [anyTimeInNight] 所在这一夜的真实时间点上。 */
    fun resolve(anyTimeInNight: LocalDateTime): Resolved {
        var show = anyTimeInNight.toLocalDate().atTime(showAt)
        if (anyTimeInNight < show) show = show.minusDays(1)

        val day: LocalDate = show.toLocalDate()
        val deduct = (if (deductAt >= showAt) day else day.plusDays(1)).atTime(deductAt)
        val hide = (if (hideAt > showAt) day else day.plusDays(1)).atTime(hideAt)
        return Resolved(show, deduct, hide)
    }

    /** 整套参数是否自洽。合法时返回 null，否则返回中文错误说明。 */
    fun validate(): String? {
        if (hideAt == showAt) {
            return "「悬浮窗隐藏」不能和「悬浮窗出现」是同一个时刻，否则窗口会一直显示。"
        }
        if (minutesPerPoint !in 1..720) {
            return "「每几分钟扣」要在 1–720 之间。"
        }
        if (pointsPerStep < 1 || pointsPerStep > maxScore) {
            return "「每次扣几分」要在 1–$maxScore 之间。"
        }

        val r = resolve(LocalDate.of(2026, 1, 1).atTime(showAt))
        if (r.deduct < r.show || r.deduct >= r.hide) {
            return "按当前设置，「开始扣分 ${formatHm(deductAt)}」落在悬浮窗显示时段" +
                "（${formatHm(showAt)}–${formatHm(hideAt)}）之外，永远不会扣分。"
        }
        return null
    }

    /** 一句话描述这套规则，用于设置界面的预览。跨零点也不会算错。 */
    fun describe(): String {
        val r = resolve(LocalDate.of(2026, 1, 1).atTime(showAt))
        val zero = r.deduct.plus(spanToZero)

        val zeroText = if (zero >= r.hide) "在悬浮窗隐藏前扣不到 0" else "约 ${formatHm(zero.toLocalTime())} 扣光"

        return "${formatHm(showAt)} 出现 ${maxScore} 分 → ${formatHm(deductAt)} 起每 " +
            "$minutesPerPoint 分钟扣 $pointsPerStep 分（$zeroText）→ ${formatHm(hideAt)} 隐藏"
    }

    companion object {
        val Default: SleepRules = SleepRules()

        /** 格式化成 HH:mm。 */
        fun formatHm(t: LocalTime): String = "%02d:%02d".format(t.hour, t.minute)

        /**
         * 宽松解析 HH:mm / H:mm / HH:mm:ss。
         * 全角冒号、句点都能当分隔符，与桌面版行为一致。
         */
        fun tryParseHm(text: String?): LocalTime? {
            if (text.isNullOrBlank()) return null

            val s = text.trim().replace('：', ':').replace('．', '.').replace('.', ':')
            val parts = s.split(':')
            if (parts.isEmpty() || parts.size > 3) return null

            val h = parts[0].toIntOrNull() ?: return null
            val m = if (parts.size >= 2) (parts[1].toIntOrNull() ?: return null) else 0
            val sec = if (parts.size >= 3) (parts[2].toIntOrNull() ?: return null) else 0

            if (h !in 0..23 || m !in 0..59 || sec !in 0..59) return null
            return LocalTime.of(h, m, sec)
        }

        fun parseHm(text: String?, fallback: LocalTime): LocalTime = tryParseHm(text) ?: fallback
    }
}

/** 三个时刻落在真实日历上的结果。 */
data class Resolved(
    val show: LocalDateTime,
    val deduct: LocalDateTime,
    val hide: LocalDateTime,
)

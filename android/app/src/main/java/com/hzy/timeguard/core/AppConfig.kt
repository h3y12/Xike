package com.hzy.timeguard.core

import android.content.Context
import android.content.SharedPreferences

/**
 * 配置持久化。
 *
 * 字段名与桌面版 `config.json` 里的键保持一致，方便两份配置互相对照。
 * Android 侧用 SharedPreferences 而不是 JSON 文件：读写原子、不怕进程被杀。
 */
class AppConfig(context: Context) {

    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    // ---- 三个时刻 ----
    var showAtText: String
        get() = prefs.getString(KEY_SHOW_AT, "20:30")!!
        set(v) = prefs.edit().putString(KEY_SHOW_AT, v).apply()

    var deductAtText: String
        get() = prefs.getString(KEY_DEDUCT_AT, "23:30")!!
        set(v) = prefs.edit().putString(KEY_DEDUCT_AT, v).apply()

    var hideAtText: String
        get() = prefs.getString(KEY_HIDE_AT, "06:30")!!
        set(v) = prefs.edit().putString(KEY_HIDE_AT, v).apply()

    // ---- 扣分速率 ----
    var minutesPerPoint: Int
        get() = prefs.getInt(KEY_MINUTES, 3)
        set(v) = prefs.edit().putInt(KEY_MINUTES, v).apply()

    var pointsPerStep: Int
        get() = prefs.getInt(KEY_POINTS, 1)
        set(v) = prefs.edit().putInt(KEY_POINTS, v).apply()

    // ---- 状态文案 ----
    var statusBeforeText: String
        get() = prefs.getString(KEY_STATUS_BEFORE, SleepRules.Default.statusBefore)!!
        set(v) = prefs.edit().putString(KEY_STATUS_BEFORE, v).apply()

    var statusHighText: String
        get() = prefs.getString(KEY_STATUS_HIGH, SleepRules.Default.statusHigh)!!
        set(v) = prefs.edit().putString(KEY_STATUS_HIGH, v).apply()

    var statusMidText: String
        get() = prefs.getString(KEY_STATUS_MID, SleepRules.Default.statusMid)!!
        set(v) = prefs.edit().putString(KEY_STATUS_MID, v).apply()

    var statusLowText: String
        get() = prefs.getString(KEY_STATUS_LOW, SleepRules.Default.statusLow)!!
        set(v) = prefs.edit().putString(KEY_STATUS_LOW, v).apply()

    // ---- 外观与交互 ----
    /** 悬浮窗整体透明度，0.2–1.0。 */
    var opacity: Float
        get() = prefs.getFloat(KEY_OPACITY, 0.88f).coerceIn(MIN_OPACITY, 1f)
        set(v) = prefs.edit().putFloat(KEY_OPACITY, v.coerceIn(MIN_OPACITY, 1f)).apply()

    /** 穿透：开启后触摸落到下层应用，悬浮窗自己收不到点击。 */
    var clickThrough: Boolean
        get() = prefs.getBoolean(KEY_CLICK_THROUGH, false)
        set(v) = prefs.edit().putBoolean(KEY_CLICK_THROUGH, v).apply()

    /** 悬浮窗上是否显示「早睡健康分」那一块。 */
    var showScore: Boolean
        get() = prefs.getBoolean(KEY_SHOW_SCORE, true)
        set(v) = prefs.edit().putBoolean(KEY_SHOW_SCORE, v).apply()

    /** 悬浮窗上是否显示「手动倒计时」那一块。 */
    var showCountdown: Boolean
        get() = prefs.getBoolean(KEY_SHOW_COUNTDOWN, true)
        set(v) = prefs.edit().putBoolean(KEY_SHOW_COUNTDOWN, v).apply()

    /** 通知栏里是否带一个「切换穿透」按钮（默认关，保持通知清爽）。 */
    var notifyToggle: Boolean
        get() = prefs.getBoolean(KEY_NOTIFY_TOGGLE, false)
        set(v) = prefs.edit().putBoolean(KEY_NOTIFY_TOGGLE, v).apply()

    /** 用户是否希望悬浮窗处于开启状态（App 被杀后用于恢复）。 */
    var wantRunning: Boolean
        get() = prefs.getBoolean(KEY_WANT_RUNNING, false)
        set(v) = prefs.edit().putBoolean(KEY_WANT_RUNNING, v).apply()

    // ---- 悬浮窗位置（像素，-1 表示还没摆过）----
    var overlayX: Int
        get() = prefs.getInt(KEY_X, -1)
        set(v) = prefs.edit().putInt(KEY_X, v).apply()

    var overlayY: Int
        get() = prefs.getInt(KEY_Y, -1)
        set(v) = prefs.edit().putInt(KEY_Y, v).apply()

    // ---- 手动倒计时 ----

    /** 倒计时输入框里的原文，落盘后下次打开 App 还在。 */
    var cdInputText: String
        get() = prefs.getString(KEY_CD_INPUT, "")!!
        set(v) = prefs.edit().putString(KEY_CD_INPUT, v).apply()

    /**
     * 读倒计时状态。Running 存的是**绝对截止时刻**，所以 App 没开的这段时间照常算进去，
     * 和桌面版一致。
     */
    fun loadCountdown(): CdSnapshot = CdSnapshot(
        mode = prefs.getString(KEY_CD_MODE, "")!!,
        clockTarget = prefs.getString(KEY_CD_CLOCK, "")!!,
        durationMinutes = prefs.getInt(KEY_CD_DURATION, 0),
        deadline = prefs.getString(KEY_CD_DEADLINE, "")!!,
        phase = prefs.getString(KEY_CD_PHASE, CdPhase.Idle.name)!!,
        pausedSeconds = prefs.getLong(KEY_CD_PAUSED, 0L),
        // 旧快照没有这个键 → 取到 0，等价于改动前的行为
        pausedNanos = prefs.getLong(KEY_CD_PAUSED_NANOS, 0L),
    )

    fun saveCountdown(s: CdSnapshot) {
        prefs.edit()
            .putString(KEY_CD_MODE, s.mode)
            .putString(KEY_CD_CLOCK, s.clockTarget)
            .putInt(KEY_CD_DURATION, s.durationMinutes)
            .putString(KEY_CD_DEADLINE, s.deadline)
            .putString(KEY_CD_PHASE, s.phase)
            .putLong(KEY_CD_PAUSED, s.pausedSeconds)
            .putLong(KEY_CD_PAUSED_NANOS, s.pausedNanos)
            .apply()
    }

    /**
     * 把散落的字段组装成一份规则。文本解析失败时回退到默认值。
     *
     * ⚠️ 这是「读」用的宽松口径：宁可给出一份能跑的规则，也不让界面起不来。
     * 「写」的时候**不能**用它——那会把用户在输入框里打错的字**静默换成默认值再存下去**，
     * 用户下次打开看到的是一份自己没填过的规则。写之前要用 [rawRules] + [SleepRules.validate]。
     */
    fun rules(): SleepRules = SleepRules(
        showAt = SleepRules.parseHm(showAtText, SleepRules.Default.showAt),
        deductAt = SleepRules.parseHm(deductAtText, SleepRules.Default.deductAt),
        hideAt = SleepRules.parseHm(hideAtText, SleepRules.Default.hideAt),
        minutesPerPoint = minutesPerPoint.coerceIn(1, 720),
        pointsPerStep = pointsPerStep.coerceAtLeast(1),
        maxScore = ScoreEngine.MAX_SCORE,
        statusBefore = statusBeforeText,
        statusHigh = statusHighText,
        statusMid = statusMidText,
        statusLow = statusLowText,
    )

    /**
     * 与 [rules] 的区别：**解析失败直接判非法**，不做静默回退。
     * 返回 null 表示「输入框里的内容根本读不出规则」，此时调用方应当拒绝保存。
     *
     * 用于「用户改了设置 → 要不要落盘」这条路：只有这份规则自洽才允许覆盖既有配置。
     */
    fun rawRules(): SleepRules? {
        val show = SleepRules.tryParseHm(showAtText) ?: return null
        val deduct = SleepRules.tryParseHm(deductAtText) ?: return null
        val hide = SleepRules.tryParseHm(hideAtText) ?: return null

        return SleepRules(
            showAt = show,
            deductAt = deduct,
            hideAt = hide,
            minutesPerPoint = minutesPerPoint,
            pointsPerStep = pointsPerStep,
            maxScore = ScoreEngine.MAX_SCORE,
            statusBefore = statusBeforeText,
            statusHigh = statusHighText,
            statusMid = statusMidText,
            statusLow = statusLowText,
        )
    }

    /**
     * 整套规则**一次事务**写完。
     *
     * 之前是逐字段调 9 个 setter，等于 9 次 `apply()`——每次都是一轮 IPC 到 SharedPreferences
     * 并把整份文件重新序列化。在输入框上每敲一个字符触发一次全量落盘，一次要付 9 份开销。
     * 合并成一个 Editor 后只有一次提交，且天然原子：要么 9 个字段全写进去，要么一个都不写，
     * 不会出现「时间和分数对不上」的中间态。
     */
    fun saveRules(r: SleepRules) {
        prefs.edit()
            .putString(KEY_SHOW_AT, SleepRules.formatHm(r.showAt))
            .putString(KEY_DEDUCT_AT, SleepRules.formatHm(r.deductAt))
            .putString(KEY_HIDE_AT, SleepRules.formatHm(r.hideAt))
            .putInt(KEY_MINUTES, r.minutesPerPoint)
            .putInt(KEY_POINTS, r.pointsPerStep)
            .putString(KEY_STATUS_BEFORE, r.statusBefore)
            .putString(KEY_STATUS_HIGH, r.statusHigh)
            .putString(KEY_STATUS_MID, r.statusMid)
            .putString(KEY_STATUS_LOW, r.statusLow)
            .apply()
    }

    // ---- 闹钟 ----

    /**
     * 全部闹钟，序列化成一行行文本（`key|value` 用换行分隔）。
     *
     * 为什么不用 JSON：Android 侧没有现成的 JSON 依赖（不想为一个闹钟列表引 Gson），
     * 而闹钟的字段都是简单的标量，用 `\u0001` 当分隔符、`\n` 当记录分隔符足够稳，
     * 且**手改也看得懂**——与桌面版把闹钟存成可读 JSON 的取向一致。
     */
    var alarmRecords: String
        get() = prefs.getString(KEY_ALARMS, "")!!
        set(v) = prefs.edit().putString(KEY_ALARMS, v).apply()

    /** 响铃持续秒数。默认 30 秒。 */
    var alarmRingSeconds: Int
        get() = prefs.getInt(KEY_ALARM_RING_SECONDS, 30).coerceIn(1, 600)
        set(v) = prefs.edit().putInt(KEY_ALARM_RING_SECONDS, v.coerceIn(1, 600)).apply()

    /** 「稍后提醒」推迟几分钟。 */
    var alarmSnoozeMinutes: Int
        get() = prefs.getInt(KEY_ALARM_SNOOZE, 5).coerceIn(1, 60)
        set(v) = prefs.edit().putInt(KEY_ALARM_SNOOZE, v.coerceIn(1, 60)).apply()

    /** 响铃时是否震动。 */
    var alarmVibrate: Boolean
        get() = prefs.getBoolean(KEY_ALARM_VIBRATE, true)
        set(v) = prefs.edit().putBoolean(KEY_ALARM_VIBRATE, v).apply()

    /** 把闹钟列表读进一个 AlarmBook。读不出来的记录跳过，不让 App 起不来。 */
    fun loadAlarms(): AlarmBook {
        val book = AlarmBook()
        for (rec in AlarmStore.parse(alarmRecords)) {
            val at = SleepRules.tryParseHm(rec.at) ?: continue
            book.add(
                Alarm(
                    id = rec.id.ifEmpty { Alarm.newId() },
                    label = rec.label,
                    timeOfDay = at,
                    enabled = rec.on,
                    repeat = when (rec.repeat) {
                        "daily" -> AlarmRepeat.DAILY
                        "weekly" -> AlarmRepeat.WEEKLY
                        else -> AlarmRepeat.ONCE
                    },
                    days = Alarm.daysFromText(rec.days),
                    deleteAfterRing = rec.deleteAfterRing,
                    chainId = rec.chain,
                    chainIndex = rec.chainIndex,
                    chainIntervalMinutes = rec.chainIntervalMinutes,
                ),
            )
        }
        book.normalizeAll()
        return book
    }

    /** 把 AlarmBook 写回 prefs。 */
    fun saveAlarms(book: AlarmBook) {
        val records = book.sorted().map {
            AlarmStore.Record(
                id = it.id,
                at = it.timeText,
                label = it.label,
                on = it.enabled,
                repeat = when (it.repeat) {
                    AlarmRepeat.ONCE -> "once"
                    AlarmRepeat.DAILY -> "daily"
                    AlarmRepeat.WEEKLY -> "weekly"
                },
                days = Alarm.daysToText(it.days),
                deleteAfterRing = it.deleteAfterRing,
                chain = it.chainId,
                chainIndex = it.chainIndex,
                chainIntervalMinutes = it.chainIntervalMinutes,
            )
        }
        alarmRecords = AlarmStore.serialize(records)
    }

    companion object {
        const val PREFS_NAME = "timeguard"
        const val MIN_OPACITY = 0.2f
        private const val KEY_SHOW_AT = "showAtText"
        private const val KEY_DEDUCT_AT = "deductAtText"
        private const val KEY_HIDE_AT = "hideAtText"
        private const val KEY_MINUTES = "minutesPerPoint"
        private const val KEY_POINTS = "pointsPerStep"
        private const val KEY_STATUS_BEFORE = "statusBeforeText"
        private const val KEY_STATUS_HIGH = "statusHighText"
        private const val KEY_STATUS_MID = "statusMidText"
        private const val KEY_STATUS_LOW = "statusLowText"
        private const val KEY_OPACITY = "opacity"
        private const val KEY_CLICK_THROUGH = "clickThrough"
        private const val KEY_SHOW_SCORE = "showScore"
        private const val KEY_SHOW_COUNTDOWN = "showCountdown"
        private const val KEY_NOTIFY_TOGGLE = "notifyToggle"
        private const val KEY_WANT_RUNNING = "wantRunning"
        private const val KEY_X = "overlayX"
        private const val KEY_Y = "overlayY"
        private const val KEY_CD_INPUT = "cdInputText"
        private const val KEY_CD_MODE = "cdMode"
        private const val KEY_CD_CLOCK = "cdClockTarget"
        private const val KEY_CD_DURATION = "cdDurationMinutes"
        private const val KEY_CD_DEADLINE = "cdDeadline"
        private const val KEY_CD_PHASE = "cdPhase"
        private const val KEY_CD_PAUSED = "cdPausedSeconds"
        private const val KEY_CD_PAUSED_NANOS = "cdPausedNanos"
        private const val KEY_ALARMS = "alarms"
        private const val KEY_ALARM_RING_SECONDS = "alarmRingSeconds"
        private const val KEY_ALARM_SNOOZE = "alarmSnoozeMinutes"
        private const val KEY_ALARM_VIBRATE = "alarmVibrate"
    }
}

/**
 * 闹钟的文本序列化。与界面、Android 框架都无关，可直接单测。
 *
 * 格式：一条闹钟一行，字段用 `\u0001`（SOH，正文里绝不会出现的控制字符）分隔。
 * 字段顺序固定，见 [Record]。多出来的字段忽略、少了的字段取默认值 ——
 * 这样以后加字段不会让老数据读不出来。
 */
object AlarmStore {

    private const val SEP = '\u0001'

    data class Record(
        val id: String = "",
        /** "HH:mm" */
        val at: String = "",
        val label: String = "",
        val on: Boolean = true,
        /** "once" / "daily" / "weekly" */
        val repeat: String = "once",
        /** "1,3,5"（1 = 周一） */
        val days: String = "",
        val deleteAfterRing: Boolean = true,
        val chain: String = "",
        val chainIndex: Int = 0,
        val chainIntervalMinutes: Int = 0,
    )

    fun serialize(records: List<Record>): String =
        records.joinToString("\n") { r ->
            listOf(
                r.id, r.at, r.label,
                if (r.on) "1" else "0",
                r.repeat, r.days,
                if (r.deleteAfterRing) "1" else "0",
                r.chain, r.chainIndex.toString(), r.chainIntervalMinutes.toString(),
            ).joinToString(SEP.toString())
        }

    fun parse(text: String?): List<Record> {
        if (text.isNullOrBlank()) return emptyList()

        val out = mutableListOf<Record>()
        for (line in text.split('\n')) {
            if (line.isBlank()) continue

            val f = line.split(SEP)
            // 至少要有时刻那一段，否则这行是坏的
            if (f.size < 2) continue

            out += Record(
                id = f.getOrElse(0) { "" },
                at = f.getOrElse(1) { "" },
                label = f.getOrElse(2) { "" },
                on = f.getOrElse(3) { "1" } != "0",
                repeat = f.getOrElse(4) { "once" },
                days = f.getOrElse(5) { "" },
                deleteAfterRing = f.getOrElse(6) { "1" } != "0",
                chain = f.getOrElse(7) { "" },
                chainIndex = f.getOrElse(8) { "0" }.toIntOrNull()?.coerceAtLeast(0) ?: 0,
                chainIntervalMinutes = f.getOrElse(9) { "0" }.toIntOrNull()?.takeIf { it in 1..AlarmChainSpec.MAX_INTERVAL_MINUTES } ?: 0,
            )
        }
        return out
    }
}

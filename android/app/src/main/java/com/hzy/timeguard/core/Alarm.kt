package com.hzy.timeguard.core

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.format.DateTimeFormatter

/** 闹钟的重复方式。与桌面版 `AlarmRepeat` 一一对应。 */
enum class AlarmRepeat {
    /** 只响一次，响过就删。 */
    ONCE,

    /** 每天同一时刻。 */
    DAILY,

    /** 每周指定几天。 */
    WEEKLY,
}

/**
 * 一个闹钟。与桌面版 `Alarm.cs` 语义完全一致，可脱离 Android 框架单测。
 *
 * 时间语义：[timeOfDay] 是「钟点」，属于哪一天由 [DAY_CUTOVER_HOUR]（凌晨四点）决定 ——
 * 04:00 之前的钟点算作「前一天的晚上」，04:00 及之后算「当天」。
 */
data class Alarm(
    /** 稳定标识，用于「手动关掉整串连锁闹钟」时认出彼此。 */
    val id: String,

    /** 用户给闹钟起的名字，空串表示不起名。 */
    val label: String = "",

    /** 钟点。秒对闹钟没有意义，构造时截到分。 */
    val timeOfDay: LocalTime,

    /** 当前是否生效。不生效的在界面上显示浅灰色。 */
    val enabled: Boolean = true,

    val repeat: AlarmRepeat = AlarmRepeat.ONCE,

    /** WEEKLY 时生效的星期几。 */
    val days: Set<DayOfWeek> = emptySet(),

    /** 一次性闹钟响过之后是否自动删除。 */
    val deleteAfterRing: Boolean = true,

    /** 属于哪一串连锁起床闹钟。空串 = 独立闹钟。 */
    val chainId: String = "",

    /** 在连锁串里的序号（从 1 开始）。 */
    val chainIndex: Int = 0,
) {
    /** 截到分的钟点。 */
    val at: LocalTime get() = timeOfDay.withSecond(0).withNano(0)

    /**
     * 排序键：把钟点折算成「从凌晨四点起的分钟数」。
     * 04:00 → 0，05:00 → 60，23:00 → 1140，01:30(次日) → 1290。
     * 于是 01:30 排在 23:00 后面，符合「同一个晚上」的直觉。
     */
    val sortKey: Int
        get() {
            val minutes = at.hour * 60 + at.minute
            var shifted = minutes - DAY_CUTOVER_HOUR * 60
            if (shifted < 0) shifted += 24 * 60
            return shifted
        }

    /** 界面上显示的时间文本，如 "06:30"。 */
    val timeText: String get() = "%02d:%02d".format(at.hour, at.minute)

    /** 重复方式的短说明，如 "每天" / "周一、周三" / "仅一次"。 */
    val repeatText: String
        get() = when (repeat) {
            AlarmRepeat.ONCE -> "仅一次"
            AlarmRepeat.DAILY -> "每天"
            AlarmRepeat.WEEKLY -> {
                if (days.isEmpty() || days.size == 7) "每天"
                else WEEK_ORDER.filter { it in days }.joinToString("、") { dayName(it) }
            }
        }

    /** 规范化的副本：钟点截到分，WEEKLY 空集合修正为「每天」。 */
    fun normalized(): Alarm {
        val fixedDays = if (repeat == AlarmRepeat.WEEKLY && days.isEmpty()) WEEK_ORDER.toSet() else days
        return copy(timeOfDay = at, days = fixedDays)
    }

    /**
     * 算出 [after] 之后这个闹钟下一次该响的真实时刻。关掉的不参与（返回 null）。
     */
    fun nextRingAfter(after: LocalDateTime): LocalDateTime? {
        if (!enabled) return null

        // 从"今天"试到第 8 天，足够覆盖 WEEKLY 全部 7 种星期几
        for (offset in 0..7) {
            val candidate = after.toLocalDate().plusDays(offset.toLong()).atTime(at)
            if (candidate <= after) continue

            when (repeat) {
                AlarmRepeat.ONCE, AlarmRepeat.DAILY -> return candidate
                AlarmRepeat.WEEKLY -> if (candidate.dayOfWeek in days) return candidate
            }
        }
        return null
    }

    companion object {
        /**
         * 一天的起点设在凌晨四点，而不是零点。
         * 理由：设成 0 点的话，「今晚 01:30 睡」和「明晚 23:00 睡」会被排到列表首尾两端，
         * 而它们在用户心里是前后脚的两件事。凌晨四点切，01:30 就乖乖排在 23:00 后面。
         */
        const val DAY_CUTOVER_HOUR = 4

        /** 半小时内要响就算「临近」——挂件据此显示、手机端据此写进通知栏。 */
        const val IMMINENT_MINUTES = 30

        /** 周一…周日的固定顺序，用来把星期几排成用户习惯的次序。 */
        val WEEK_ORDER: List<DayOfWeek> = listOf(
            DayOfWeek.MONDAY, DayOfWeek.TUESDAY, DayOfWeek.WEDNESDAY, DayOfWeek.THURSDAY,
            DayOfWeek.FRIDAY, DayOfWeek.SATURDAY, DayOfWeek.SUNDAY,
        )

        fun dayName(d: DayOfWeek): String = when (d) {
            DayOfWeek.MONDAY -> "周一"
            DayOfWeek.TUESDAY -> "周二"
            DayOfWeek.WEDNESDAY -> "周三"
            DayOfWeek.THURSDAY -> "周四"
            DayOfWeek.FRIDAY -> "周五"
            DayOfWeek.SATURDAY -> "周六"
            else -> "周日"
        }

        private var seq = 0

        /** 生成一个进程内唯一的 Id。 */
        fun newId(): String {
            seq += 1
            return "a" + System.currentTimeMillis().toString(36) + "-" + seq
        }

        /** 这个钟点落在「哪一夜」——用于分组显示与排序。 */
        fun nightOf(when0: LocalDateTime): LocalDate =
            if (when0.hour < DAY_CUTOVER_HOUR) when0.toLocalDate().minusDays(1) else when0.toLocalDate()

        /** 「从现在起还有多久响」的中文描述。 */
        fun describeCountdown(now: LocalDateTime, ringAt: LocalDateTime): String {
            val d = java.time.Duration.between(now, ringAt)
            if (d.isNegative || d.isZero) return "已到点"
            val minutes = d.toMinutes()
            if (minutes < 1) return "不到 1 分钟"
            if (minutes < 60) return "${minutes} 分钟后"
            val hours = d.toHours()
            if (hours < 24) return "${hours} 小时 ${minutes % 60} 分后"
            val days = d.toDays()
            val restHours = hours % 24
            return if (restHours == 0L) "${days} 天后" else "${days} 天 ${restHours} 小时后"
        }

        private val ISO: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm")

        /** 落盘用：把钟点写成 "HH:mm"。 */
        fun timeToText(t: LocalTime): String = "%02d:%02d".format(t.hour, t.minute)

        /** 存真实时刻（用于"哪一次已经响过"的记账）。 */
        fun stampToText(t: LocalDateTime): String = t.format(ISO)

        fun stampFromText(s: String?): LocalDateTime? =
            try {
                if (s.isNullOrBlank()) null else LocalDateTime.parse(s, ISO)
            } catch (_: Throwable) {
                null
            }

        /** 星期几存成 "1,3,5"（1 = 周一 … 7 = 周日）。 */
        fun daysToText(days: Set<DayOfWeek>): String =
            WEEK_ORDER.filter { it in days }
                .map { (it.value + 6) % 7 + 1 }
                .sorted()
                .joinToString(",")

        fun daysFromText(text: String?): Set<DayOfWeek> {
            if (text.isNullOrBlank()) return emptySet()
            val out = mutableSetOf<DayOfWeek>()
            for (part in text.split(',')) {
                val n = part.trim().toIntOrNull() ?: continue
                if (n !in 1..7) continue
                out += DayOfWeek.of(n)   // DayOfWeek.of(1) = MONDAY
            }
            return out
        }
    }
}

/** 连锁起床闹钟的生成参数。起始时间 + 间隔 + 次数 → 一串闹钟。 */
data class AlarmChainSpec(
    val startAt: LocalTime = LocalTime.of(6, 30),
    val intervalMinutes: Int = 5,
    val count: Int = 3,
    val daily: Boolean = true,
    /** 只在工作日（周一到周五）响。仅 [daily] 为 true 时有意义。 */
    val weekdaysOnly: Boolean = false,
) {
    /** 合法时返回 null，否则返回中文说明。 */
    fun validate(): String? {
        if (count !in 1..MAX_COUNT) return "响铃次数要在 1–$MAX_COUNT 之间。"
        if (intervalMinutes !in 1..MAX_INTERVAL_MINUTES) {
            return "间隔时间要在 1–$MAX_INTERVAL_MINUTES 分钟之间。"
        }
        return null
    }

    /** 最后一个闹钟的钟点（跨零点会自动绕回来）。 */
    val lastAt: LocalTime
        get() {
            val total = startAt.hour * 60 + startAt.minute + (count - 1) * intervalMinutes
            val wrapped = ((total % (24 * 60)) + 24 * 60) % (24 * 60)
            return LocalTime.of(wrapped / 60, wrapped % 60)
        }

    companion object {
        const val MAX_COUNT = 12
        const val MAX_INTERVAL_MINUTES = 120
    }
}

/** 一次「到点了」的事件。 */
data class AlarmFiring(
    val alarm: Alarm,
    /** 本该响的时刻。 */
    val dueAt: LocalDateTime,
    /** 这是不是一串连锁闹钟里的最后一个。 */
    val isLastInChain: Boolean,
)

/**
 * 闹钟集合的纯逻辑：排序、到期判定、连锁生成与整串关闭、响过就删。
 *
 * **刻意不读系统时钟** —— 时间一律由调用方传进来，这样单测可以在任意时刻上断言，
 * 不必等到半夜三点去验。与桌面版 `AlarmBook.cs` 一一对应。
 */
class AlarmBook {
    private val items = mutableListOf<Alarm>()

    /**
     * 已报过的响铃：闹钟 id → 那一次的**时刻**。
     *
     * ⚠️ 存时刻而不是存布尔值，这一点极容易写错。
     * 如果写成「这个闹钟响过就跳过」，那「每天 8:00」的闹钟第二天再也不会响 ——
     * 而单测里只测一次响铃根本发现不了。桌面版就是这么踩过一回的，这里沿用正确写法。
     */
    private val firedAt = mutableMapOf<String, LocalDateTime>()

    /**
     * 错过多久就不补响（用户选的就是「错过就不补」）。
     * 这不是"宽容窗口"而是"程序刚从睡眠里醒来时，多久之内的还认"。
     */
    var missGraceSeconds: Int = 90

    val all: List<Alarm> get() = items.toList()

    val size: Int get() = items.size

    fun add(a: Alarm) {
        val n = a.normalized()
        val idx = items.indexOfFirst { it.id == n.id }
        if (idx >= 0) items[idx] = n else items.add(n)
    }

    fun addAll(list: Iterable<Alarm>) = list.forEach { add(it) }

    fun find(id: String): Alarm? = items.firstOrNull { it.id == id }

    fun remove(id: String): Boolean {
        firedAt.remove(id)
        return items.removeAll { it.id == id }
    }

    fun clear() {
        items.clear()
        firedAt.clear()
    }

    /** 去重 + 修正字段 + 按四点分界排序。 */
    fun normalizeAll() {
        val seen = mutableSetOf<String>()
        val kept = mutableListOf<Alarm>()
        for (a in items) {
            val n = a.normalized()
            if (seen.add(n.id)) kept += n
        }
        items.clear()
        items += kept
        sort()
    }

    /** 按「凌晨四点为界」的钟点顺序排。同一钟点内连锁闹钟按序号排。 */
    fun sort() {
        items.sortWith(
            compareBy({ it.sortKey }, { it.chainId }, { it.chainIndex }),
        )
    }

    /** 排序后的快照，供界面渲染。 */
    fun sorted(): List<Alarm> = items.sortedWith(
        compareBy({ it.sortKey }, { it.chainId }, { it.chainIndex }),
    )

    // ---- 到期判定 ----

    /** Consume a one-shot on firing; the ringing service retains its own alarm snapshot. */
    fun consumeOccurrence(a: Alarm) {
        if (a.repeat != AlarmRepeat.ONCE) return
        if (a.deleteAfterRing) remove(a.id)
        else add(a.copy(enabled = false))
    }

    /**
     * 在 (from, to] 之间有哪些闹钟该响，按时刻先后排好。
     *
     * 两条规则：① 同一个时刻只报一次；② 错过太久的不补（用户选的行为）。
     */
    fun dueBetween(from: LocalDateTime, to: LocalDateTime): List<AlarmFiring> {
        val result = mutableListOf<AlarmFiring>()
        if (to < from) return result
        val grace = missGraceSeconds.coerceAtLeast(0).toLong()
        val searchFrom = to.minusSeconds(grace + 1)
        val graceBefore = to.minusSeconds(grace)

        for (a in items) {
            if (!a.enabled) continue

            val ringAt = a.nextRingAfter(searchFrom) ?: continue
            if (ringAt > to) continue

            // 这一次已经报过了 → 跳过。**不要**因为"响过"就永久跳过，
            // 否则每天重复的闹钟只响一次（见 firedAt 的注释）。
            if (firedAt[a.id] == ringAt) continue

            // 错过太久 → 记账吞掉，不报给界面
            if (ringAt < graceBefore) {
                firedAt[a.id] = ringAt
                continue
            }

            firedAt[a.id] = ringAt
            result += AlarmFiring(a, ringAt, isLastInChain(a))
        }

        result.sortBy { it.dueAt }
        return result
    }

    /** 这个闹钟离它这一串的末尾还有几个（含自己）。不在串里返回 1。 */
    fun remainingInChain(a: Alarm): Int {
        if (a.chainId.isEmpty()) return 1
        return items.count { it.chainId == a.chainId && it.chainIndex >= a.chainIndex }
    }

    /** 它是不是这一串里的最后一个。 */
    fun isLastInChain(a: Alarm): Boolean {
        if (a.chainId.isEmpty()) return true
        val max = items.filter { it.chainId == a.chainId }.maxOfOrNull { it.chainIndex } ?: 0
        return a.chainIndex >= max
    }

    /**
     * 用户「关掉了这次响铃」。语义与桌面版一致：
     *  · 普通闹钟：一次性 → 删掉；重复的 → 保留。
     *  · 连锁串里不是最后一个：只记成"这次响过了"，后面的照常响 —— 这就是"关掉一个，第二个仍会响起"。
     *  · 连锁串里是最后一个：整串结束，一次性的整串删掉。
     */
    fun dismiss(a: Alarm): String {
        val last = isLastInChain(a)

        if (a.chainId.isEmpty()) {
            return if (a.repeat == AlarmRepeat.ONCE && a.deleteAfterRing) {
                remove(a.id)
                "已删除这个一次性闹钟。"
            } else {
                "已关闭，下次仍会响。"
            }
        }

        if (!last) {
            val left = remainingInChain(a) - 1
            return "已关闭，还有 $left 个会继续响。"
        }

        val chainId = a.chainId
        val members = items.filter { it.chainId == chainId }
        val allOnce = members.all { it.repeat == AlarmRepeat.ONCE }

        return if (allOnce) {
            val ids = members.map { it.id }
            ids.forEach { firedAt.remove(it) }
            items.removeAll { it.chainId == chainId }
            "这是最后一个，整串 ${members.size} 个已删除。"
        } else {
            "这是最后一个，这一串起床闹钟已结束。"
        }
    }

    /** 手动关闭整串（用户说的「手动关闭时一起关闭」）。 */
    fun dismissWholeChain(a: Alarm): String {
        if (a.chainId.isEmpty()) {
            return if (a.repeat == AlarmRepeat.ONCE && a.deleteAfterRing) dismiss(a) else "已关闭这个闹钟。"
        }

        val chainId = a.chainId
        val members = items.filter { it.chainId == chainId }
        val allOnce = members.all { it.repeat == AlarmRepeat.ONCE }

        return if (allOnce) {
            items.removeAll { it.chainId == chainId }
            "整串 ${members.size} 个起床闹钟已全部关闭并删除。"
        } else {
            for (i in items.indices) {
                if (items[i].chainId == chainId) items[i] = items[i].copy(enabled = false)
            }
            "整串 ${members.size} 个起床闹钟已全部关闭。"
        }
    }

    /** 「稍后提醒」：把当前这个推迟 [minutes] 分钟再响，其余不动。 */
    fun snooze(a: Alarm, now: LocalDateTime, minutes: Int): String {
        val m = if (minutes < 1) 1 else minutes
        firedAt[a.id] = now

        val later = now.plusMinutes(m.toLong()).toLocalTime().withSecond(0).withNano(0)
        val snoozed = Alarm(
            id = Alarm.newId(),
            label = if (a.label.isBlank()) "稍后提醒" else "${a.label}（稍后）",
            timeOfDay = later,
            enabled = true,
            repeat = AlarmRepeat.ONCE,
            deleteAfterRing = true,
            chainId = "",   // 脱离原串：稍后提醒不该把整串唤醒逻辑带过来
        )
        add(snoozed)
        sort()
        return "$m 分钟后（${snoozed.timeText}）再响一次。"
    }

    /** 当前已生效的闹钟里最近的那一次响铃时刻。 */
    fun nextRing(now: LocalDateTime): LocalDateTime? =
        items.filter { it.enabled }
            .mapNotNull { it.nextRingAfter(now) }
            .minOrNull()

    /** 半小时内（含）要响的闹钟。手机端据此决定要不要写进通知栏。 */
    fun imminent(now: LocalDateTime): List<Alarm> {
        val limit = now.plusMinutes(Alarm.IMMINENT_MINUTES.toLong())
        val pairs = mutableListOf<Pair<Alarm, LocalDateTime>>()
        for (a in items) {
            if (!a.enabled) continue
            val n = a.nextRingAfter(now) ?: continue
            if (n <= limit) pairs += a to n
        }
        pairs.sortBy { it.second }
        return pairs.map { it.first }
    }

    /** 半小时内最早的那次响铃时刻。 */
    fun nextImminentRing(now: LocalDateTime): LocalDateTime? =
        imminent(now).mapNotNull { it.nextRingAfter(now) }.minOrNull()

    /** 手动添加一个普通闹钟。 */
    fun addManual(
        at: LocalTime,
        label: String,
        repeat: AlarmRepeat,
        days: Set<DayOfWeek>?,
        deleteAfterRing: Boolean,
    ): Alarm {
        val a = Alarm(
            id = Alarm.newId(),
            label = label,
            timeOfDay = at,
            enabled = true,
            repeat = repeat,
            days = days ?: emptySet(),
            deleteAfterRing = deleteAfterRing,
        ).normalized()
        add(a)
        sort()
        return a
    }

    /**
     * 按「起始时间 + 间隔 + 次数」自动生成一串连锁起床闹钟。
     * 同一串共用一个 [Alarm.chainId]，因而能整串关闭、也能判断"是不是最后一个"。
     */
    fun addChain(spec: AlarmChainSpec, label: String, chainId: String? = null): List<Alarm> {
        val cid = chainId ?: ("c" + Alarm.newId())

        // 已经有一条同 id 的串就先清掉，避免重复生成时越加越多
        items.removeAll { it.chainId == cid }

        val made = mutableListOf<Alarm>()
        for (i in 0 until spec.count) {
            val total = spec.startAt.hour * 60 + spec.startAt.minute + i * spec.intervalMinutes
            val wrapped = ((total % (24 * 60)) + 24 * 60) % (24 * 60)

            var a = Alarm(
                id = Alarm.newId(),
                label = label,
                timeOfDay = LocalTime.of(wrapped / 60, wrapped % 60),
                enabled = true,
                repeat = if (spec.daily) AlarmRepeat.DAILY else AlarmRepeat.ONCE,
                days = emptySet(),
                deleteAfterRing = !spec.daily,
                chainId = cid,
                chainIndex = i + 1,
            )

            if (spec.daily && spec.weekdaysOnly) {
                a = a.copy(
                    repeat = AlarmRepeat.WEEKLY,
                    days = Alarm.WEEK_ORDER
                        .filter { it != DayOfWeek.SATURDAY && it != DayOfWeek.SUNDAY }
                        .toSet(),
                )
            }

            a = a.normalized()
            items += a
            made += a
        }

        sort()
        return made
    }

    /** 把整串起床闹钟调成"生效/不生效"，不删除。 */
    fun setChainEnabled(chainId: String, enabled: Boolean) {
        for (i in items.indices) {
            if (items[i].chainId == chainId) items[i] = items[i].copy(enabled = enabled)
        }
    }

    /** 设置页的开关：普通闹钟改单个，连锁闹钟改整组；响铃消费不走这里。 */
    fun setEnabledFromSettings(id: String, enabled: Boolean) {
        val current = find(id) ?: return
        if (current.chainId.isEmpty()) add(current.copy(enabled = enabled))
        else setChainEnabled(current.chainId, enabled)
    }

    /** 设置页删除：即使编辑时当前成员已被消费，仍可删除其余同组成员。 */
    fun removeFromSettings(alarm: Alarm): Int {
        val ids = if (alarm.chainId.isEmpty()) listOf(alarm.id)
            else items.filter { it.chainId == alarm.chainId }.map { it.id }
        return ids.count { remove(it) }
    }

    /** 已生成的连锁串（按 chainId 分组）。 */
    fun chains(): List<Pair<String, List<Alarm>>> =
        items.filter { it.chainId.isNotEmpty() }
            .groupBy { it.chainId }
            .map { (k, v) -> k to v.sortedBy { it.chainIndex } }

    /** 从当前列表反推连锁参数，供设置界面回显。 */
    fun inferChainSpec(chainId: String): AlarmChainSpec? {
        val list = items.filter { it.chainId == chainId }.sortedBy { it.chainIndex }
        if (list.isEmpty()) return null

        var interval = if (list.size >= 2) {
            (list[1].at.toSecondOfDay() - list[0].at.toSecondOfDay()) / 60
        } else {
            0
        }
        if (interval < 0) interval += 24 * 60

        val weekdays = list[0].repeat == AlarmRepeat.WEEKLY &&
            list[0].days.size == 5 &&
            DayOfWeek.SATURDAY !in list[0].days &&
            DayOfWeek.SUNDAY !in list[0].days

        return AlarmChainSpec(
            startAt = list[0].at,
            intervalMinutes = if (interval <= 0) 5 else interval,
            count = list.size,
            daily = list[0].repeat != AlarmRepeat.ONCE,
            weekdaysOnly = weekdays,
        )
    }
}

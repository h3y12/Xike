package com.hzy.timeguard

import android.app.TimePickerDialog
import android.content.Intent
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.widget.doAfterTextChanged
import com.google.android.material.button.MaterialButton
import com.hzy.timeguard.core.Alarm
import com.hzy.timeguard.core.AlarmBook
import com.hzy.timeguard.core.AlarmChainSpec
import com.hzy.timeguard.core.AlarmRepeat
import com.hzy.timeguard.core.AppConfig
import com.hzy.timeguard.databinding.ActivityMainBinding
import com.hzy.timeguard.databinding.DialogAlarmBinding
import com.hzy.timeguard.databinding.DialogAlarmChainBinding
import com.hzy.timeguard.databinding.ItemAlarmBinding
import java.time.DayOfWeek
import java.time.LocalDateTime
import java.time.LocalTime

/**
 * 「闹钟」那张卡的全部逻辑：列表渲染 + 增删改 + 连锁起床闹钟。
 *
 * 单独拎成一个类，是为了不让 [MainActivity] 再长一截（它已经承担了健康分、倒计时、
 * 权限三件事）。MainActivity 只需要 `attach()` 与 `render()` 两处调用。
 *
 * 闹钟由系统 AlarmManager 调度、独立服务执行，与悬浮窗开关无关。
 */
class AlarmCard(
    private val activity: AppCompatActivity,
    private val b: ActivityMainBinding,
    private val cfg: AppConfig,
    private val onChanged: () -> Unit = {},
) {

    private var book = AlarmBook()

    /** 载入配置到控件时置位，避免「填值」被当成「用户改动」而回写。 */
    private var suppress = false

    /** 上一次画出来的内容签名。变了才重建方块 —— 每秒重建会把开关动画打断。 */
    private var sig = ""

    // ---------------------------------------------------------------- 挂载

    /** 绑定事件 + 载入现有闹钟。在 `onCreate` 里调一次。 */
    fun attach() {
        book = cfg.loadAlarms()

        b.btnAlarmAdd.setOnClickListener { openEditor(null) }
        b.btnAlarmChain.setOnClickListener { openChainEditor() }

        b.etAlarmRingSeconds.doAfterTextChanged {
            if (suppress) return@doAfterTextChanged
            // 读不出整数（刚删空）就什么都不做，别把默认值塞回去打断输入
            val v = it?.toString()?.trim()?.toIntOrNull() ?: return@doAfterTextChanged
            cfg.alarmRingSeconds = v
        }

        b.swAlarmVibrate.setOnCheckedChangeListener { _, checked ->
            if (suppress) return@setOnCheckedChangeListener
            cfg.alarmVibrate = checked
        }

        b.btnAlarmDismiss.setOnClickListener { AlarmRingService.command(activity, AlarmRingService.ACTION_DISMISS) }
        b.btnAlarmSnooze.setOnClickListener { AlarmRingService.command(activity, AlarmRingService.ACTION_SNOOZE) }
        loadToUi()
        sig = ""
        render()
    }

    private fun loadToUi() {
        suppress = true
        b.etAlarmRingSeconds.setText(cfg.alarmRingSeconds.toString())
        b.swAlarmVibrate.isChecked = cfg.alarmVibrate
        suppress = false
    }

    // ---------------------------------------------------------------- 渲染

    /** 画出闹钟列表与状态行。每秒调一次也安全（内容没变就不重建）。 */
    fun render() {
        book = cfg.loadAlarms()
        b.alarmRingControls.visibility = if (AlarmRingService.isRinging) View.VISIBLE else View.GONE
        val s = signature()
        if (s != sig) {
            rebuildList()
            sig = s
        }
        updateStatusLine()
    }

    private fun signature(): String = book.sorted().joinToString("|") {
        "${it.id},${it.timeText},${it.enabled},${it.label},${it.repeatText},${it.days.size}"
    }

    private fun rebuildList() {
        b.llAlarmList.removeAllViews()
        val list = book.sorted()

        b.tvAlarmEmpty.visibility = if (list.isEmpty()) View.VISIBLE else View.GONE

        for (a in list) {
            val ib = ItemAlarmBinding.inflate(activity.layoutInflater, b.llAlarmList, false)

            ib.tvAlarmTime.text = a.timeText
            ib.tvAlarmSub.text = subtitle(a)
            colorize(ib.tvAlarmTime, ib.tvAlarmSub, a.enabled)

            // 先设值、再挂监听：反过来的话「填初值」会被当成用户改动而回写一次
            ib.swAlarmItem.isChecked = a.enabled
            ib.swAlarmItem.setOnCheckedChangeListener { _, checked ->
                if (suppress) return@setOnCheckedChangeListener
                // 设置页的连锁开关作用于整组，所有成员的开关和颜色一起刷新。
                book = cfg.loadAlarms()
                book.setEnabledFromSettings(a.id, checked)
                persist()
                render()
            }

            // 点连锁成员编辑整组；普通闹钟仍编辑单个。
            ib.alarmItemRoot.setOnClickListener { openEditor(a) }

            b.llAlarmList.addView(ib.root)
        }
    }

    /** 方块里那行小字：名字与重复方式。**只显示时间和开关**是主，这行是补充。 */
    private fun subtitle(a: Alarm): String {
        val parts = mutableListOf<String>()
        if (a.chainId.isNotEmpty()) parts += "连锁 ${a.chainIndex} · 整组开关"
        if (a.label.isNotBlank()) parts += a.label
        parts += a.repeatText
        return parts.joinToString(" · ")
    }

    private fun colorize(time: TextView, sub: TextView, enabled: Boolean) {
        // 用户明确要求的判据：生效 = 近黑，不生效 = 浅灰
        time.setTextColor(
            ContextCompat.getColor(
                activity,
                if (enabled) R.color.text_primary else R.color.alarm_off_text,
            ),
        )
        sub.setTextColor(
            ContextCompat.getColor(
                activity,
                if (enabled) R.color.text_secondary else R.color.alarm_off_text,
            ),
        )
    }

    private fun updateStatusLine() {
        val now = LocalDateTime.now()
        val next = book.nextRing(now)
        b.tvAlarmHint.text = if (AlarmRingService.current != null) {
            AlarmRingService.current!!.let { "${it.alarm.label.ifBlank { "闹钟" }}正在响 · ${it.secondsLeft(now)} 秒后停止" }
        } else if (book.all.any { it.enabled } && !AlarmScheduler.canSchedule(activity)) {
            activity.getString(R.string.perm_alarm_missing)
        } else if (next == null) {
            activity.getString(R.string.alarm_hint)
        } else {
            // 距离最近的那次响铃还有多久 —— 不用点进设置页就能看明白
            activity.getString(
                R.string.alarm_next_at,
                hm(next.toLocalTime()),
                Alarm.describeCountdown(now, next),
            )
        }
    }

    private fun hm(t: LocalTime): String = "%02d:%02d".format(t.hour, t.minute)

    private fun persist() {
        cfg.saveAlarms(book)
        AlarmScheduler.schedule(activity)
        onChanged()
        updateStatusLine()
    }

    // ---------------------------------------------------------------- 普通闹钟编辑

    private fun openEditor(existing: Alarm?) {
        val editorBook = cfg.loadAlarms()
        val baseline = editorBook.all.filter {
            if (existing?.chainId.isNullOrEmpty()) it.id == existing?.id else it.chainId == existing?.chainId
        }
        val db = DialogAlarmBinding.inflate(activity.layoutInflater)
        var picked: LocalTime = existing?.at ?: LocalTime.of(7, 0)

        val chain = baseline.sortedBy { it.chainIndex }
        val chainSpec = existing?.chainId?.takeIf { it.isNotEmpty() }?.let { editorBook.inferChainSpec(it) }
        db.editChainSettings.visibility = if (chainSpec != null) View.VISIBLE else View.GONE
        db.etEditChainInterval.setText(chainSpec?.intervalMinutes?.toString().orEmpty())
        db.etEditChainCount.setText(chainSpec?.count?.toString().orEmpty())
        fun updateEditChainPreview() {
            val initial = existing ?: return
            if (chainSpec == null) return
            val interval = db.etEditChainInterval.text.toString().toIntOrNull() ?: 0
            val count = db.etEditChainCount.text.toString().toIntOrNull() ?: 0
            if (interval !in 1..AlarmChainSpec.MAX_INTERVAL_MINUTES || count !in 1..AlarmChainSpec.MAX_COUNT) {
                db.tvEditChainPreview.text = activity.getString(R.string.alarm_chain_preview_bad)
                return
            }
            val shift = java.time.Duration.between(initial.at, picked).toMinutes()
            val start = chain.first().at.plusMinutes(shift)
            val times = (0 until count).joinToString(" / ") { hm(start.plusMinutes(it.toLong() * interval)) }
            db.tvEditChainPreview.text = activity.getString(R.string.alarm_chain_preview_pattern, times, count)
        }
        db.etEditChainInterval.doAfterTextChanged { updateEditChainPreview() }
        db.etEditChainCount.doAfterTextChanged { updateEditChainPreview() }
        updateEditChainPreview()

        db.btnAlarmTime.text = hm(picked)
        db.etAlarmLabel.setText(existing?.label ?: "")

        when (existing?.repeat ?: AlarmRepeat.DAILY) {
            AlarmRepeat.ONCE -> db.rbAlarmOnce.isChecked = true
            AlarmRepeat.DAILY -> db.rbAlarmDaily.isChecked = true
            AlarmRepeat.WEEKLY -> db.rbAlarmWeekly.isChecked = true
        }

        // 周一…周日 与 7 个按钮的固定对应（顺序与 Alarm.WEEK_ORDER 一致）
        val dayButtons = linkedMapOf(
            db.btnDay1.id to DayOfWeek.MONDAY,
            db.btnDay2.id to DayOfWeek.TUESDAY,
            db.btnDay3.id to DayOfWeek.WEDNESDAY,
            db.btnDay4.id to DayOfWeek.THURSDAY,
            db.btnDay5.id to DayOfWeek.FRIDAY,
            db.btnDay6.id to DayOfWeek.SATURDAY,
            db.btnDay7.id to DayOfWeek.SUNDAY,
        )
        val existingDays = existing?.days ?: setOf(
            DayOfWeek.MONDAY, DayOfWeek.TUESDAY, DayOfWeek.WEDNESDAY,
            DayOfWeek.THURSDAY, DayOfWeek.FRIDAY,
        )
        for ((id, day) in dayButtons) {
            db.tgAlarmDays.findViewById<MaterialButton>(id).isChecked = day in existingDays
        }

        db.swDeleteAfterRing.isChecked = existing?.deleteAfterRing ?: true

        fun syncVisibility() {
            db.tgAlarmDays.visibility = if (db.rbAlarmWeekly.isChecked) View.VISIBLE else View.GONE
            // 「响过之后自动删除」只对「仅一次」有意义：每天/每周的闹钟不会因为响过就消失
            db.swDeleteAfterRing.visibility =
                if (db.rbAlarmOnce.isChecked) View.VISIBLE else View.GONE
        }
        db.rgAlarmRepeat.setOnCheckedChangeListener { _, _ -> syncVisibility() }
        syncVisibility()

        db.btnAlarmTime.setOnClickListener {
            TimePickerDialog(
                activity,
                { _, h, m ->
                    picked = LocalTime.of(h, m)
                    db.btnAlarmTime.text = hm(picked)
                    updateEditChainPreview()
                },
                picked.hour,
                picked.minute,
                true,
            ).show()
        }

        val dialog = AlertDialog.Builder(activity)
            .setTitle(when {
                existing == null -> R.string.alarm_dialog_title
                existing.chainId.isNotEmpty() -> R.string.alarm_edit_chain_title
                else -> R.string.alarm_edit_title
            })
            .setView(db.root)
            .setPositiveButton(R.string.btn_ok, null)     // 校验失败要能拦住关闭，所以稍后手动挂
            .setNegativeButton(R.string.btn_cancel, null)
            .apply { if (existing != null) setNeutralButton(if (existing.chainId.isEmpty()) R.string.btn_delete else R.string.btn_delete_chain, null) }
            .create()

        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val days = dayButtons
                    .filterKeys { db.tgAlarmDays.findViewById<MaterialButton>(it).isChecked }
                    .values.toSet()

                val rep = when {
                    db.rbAlarmOnce.isChecked -> AlarmRepeat.ONCE
                    db.rbAlarmWeekly.isChecked -> AlarmRepeat.WEEKLY
                    else -> AlarmRepeat.DAILY
                }

                if (rep == AlarmRepeat.WEEKLY && days.isEmpty()) {
                    // 拦下来，不关窗：关掉的话用户填的东西白填了
                    activity.toast(activity.getString(R.string.alarm_weekly_need_day))
                    return@setOnClickListener
                }

                val label = db.etAlarmLabel.text.toString().trim()
                book = cfg.loadAlarms()

                if (existing == null) {
                    book.addManual(
                        at = picked,
                        label = label,
                        repeat = rep,
                        days = days,
                        deleteAfterRing = db.swDeleteAfterRing.isChecked,
                    )
                } else {
                    val edited = existing.copy(
                        label = label,
                        timeOfDay = picked,
                        repeat = rep,
                        days = days,
                        deleteAfterRing = db.swDeleteAfterRing.isChecked,
                    )
                    val saved = if (chainSpec != null) {
                        val interval = db.etEditChainInterval.text.toString().toIntOrNull() ?: 0
                        val count = db.etEditChainCount.text.toString().toIntOrNull() ?: 0
                        AlarmChainSpec(intervalMinutes = interval, count = count).validate()?.let {
                            activity.toast(activity.getString(R.string.alarm_chain_bad, it))
                            return@setOnClickListener
                        }
                        if (interval != chainSpec.intervalMinutes || count != chainSpec.count)
                            book.resizeChainFromEditor(existing, edited, baseline, interval, count)
                        else book.mergeEditorChanges(existing, edited, baseline)
                    } else book.mergeEditorChanges(existing, edited, baseline)
                    if (!saved) {
                        sig = ""
                        render()
                        activity.toast(activity.getString(R.string.alarm_editor_expired))
                        dialog.dismiss()
                        return@setOnClickListener
                    }
                }

                persist()
                sig = ""
                render()
                activity.toast(activity.getString(R.string.alarm_saved))
                dialog.dismiss()
            }

            if (existing != null) {
                dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener {
                    book = cfg.loadAlarms()
                    val removed = book.removeFromSettings(existing)
                    persist()
                    sig = ""
                    render()
                    activity.toast(if (existing.chainId.isEmpty()) activity.getString(R.string.alarm_deleted) else activity.getString(R.string.alarm_chain_deleted, removed))
                    dialog.dismiss()
                }
            }
        }

        dialog.show()
    }

    // ---------------------------------------------------------------- 连锁起床闹钟

    private fun openChainEditor() {
        val db = DialogAlarmChainBinding.inflate(activity.layoutInflater)
        var start: LocalTime = LocalTime.of(6, 30)

        db.btnChainStart.text = hm(start)
        db.etChainInterval.setText(DEFAULT_INTERVAL.toString())
        db.etChainCount.setText(DEFAULT_COUNT.toString())
        db.swChainWeekdays.isChecked = true

        db.btnChainStart.setOnClickListener {
            TimePickerDialog(
                activity,
                { _, h, m ->
                    start = LocalTime.of(h, m)
                    db.btnChainStart.text = hm(start)
                    refreshChainPreview(db, start)
                },
                start.hour,
                start.minute,
                true,
            ).show()
        }

        for (et in listOf(db.etChainInterval, db.etChainCount)) {
            et.doAfterTextChanged { refreshChainPreview(db, start) }
        }

        refreshChainPreview(db, start)

        val dialog = AlertDialog.Builder(activity)
            .setTitle(R.string.alarm_dialog_chain_title)
            .setView(db.root)
            .setPositiveButton(R.string.btn_ok, null)
            .setNegativeButton(R.string.btn_cancel, null)
            .create()

        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val interval = db.etChainInterval.text.toString().trim().toIntOrNull()
                val count = db.etChainCount.text.toString().trim().toIntOrNull()

                if (interval == null || count == null) {
                    activity.toast(activity.getString(R.string.alarm_chain_bad, "间隔和次数要填整数"))
                    return@setOnClickListener
                }

                val spec = AlarmChainSpec(
                    startAt = start,
                    intervalMinutes = interval,
                    count = count,
                    daily = true,
                    weekdaysOnly = db.swChainWeekdays.isChecked,
                )

                spec.validate()?.let {
                    activity.toast(activity.getString(R.string.alarm_chain_bad, it))
                    return@setOnClickListener
                }

                book = cfg.loadAlarms()
                val made = book.addChain(spec, activity.getString(R.string.alarm_chain_label))
                persist()
                sig = ""
                render()
                activity.toast(activity.getString(R.string.alarm_chain_added, made.size))
                dialog.dismiss()
            }
        }

        dialog.show()
    }

    /** 实时回显「会生成哪几个闹钟」。填错就不回显，免得给出一个假的预览。 */
    private fun refreshChainPreview(db: DialogAlarmChainBinding, start: LocalTime) {
        val interval = db.etChainInterval.text.toString().trim().toIntOrNull()
        val count = db.etChainCount.text.toString().trim().toIntOrNull()

        if (interval == null || count == null ||
            count !in 1..AlarmChainSpec.MAX_COUNT ||
            interval !in 1..AlarmChainSpec.MAX_INTERVAL_MINUTES
        ) {
            db.tvChainPreview.text = activity.getString(R.string.alarm_chain_preview_bad)
            return
        }

        val startMin = start.hour * 60 + start.minute
        val times = (0 until count).map {
            val total = ((startMin + it * interval) % (24 * 60) + 24 * 60) % (24 * 60)
            "%02d:%02d".format(total / 60, total % 60)
        }
        db.tvChainPreview.text =
            activity.getString(R.string.alarm_chain_preview_pattern, times.joinToString(" / "), count)
    }

    companion object {
        private const val DEFAULT_INTERVAL = 5
        private const val DEFAULT_COUNT = 3
    }
}

/** 短提示。统一一个入口，省得每处都写全 `Toast.makeText(...)`。 */
internal fun android.content.Context.toast(msg: String) {
    android.widget.Toast.makeText(this, msg, android.widget.Toast.LENGTH_SHORT).show()
}

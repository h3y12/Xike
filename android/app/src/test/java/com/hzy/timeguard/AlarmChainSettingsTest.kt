package com.hzy.timeguard

import android.content.Context
import android.content.Intent
import android.os.Looper
import android.view.ViewGroup
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.widget.SwitchCompat
import com.hzy.timeguard.core.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowAlarmManager
import org.robolectric.shadows.ShadowDialog
import org.robolectric.shadows.ShadowMediaPlayer
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class AlarmChainSettingsTest {
    private val app get() = RuntimeEnvironment.getApplication()
    private val cfg get() = AppConfig(app)
    @Before fun setup() {
        app.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE).edit().clear().commit()
        shadowOf(app).grantPermissions("com.hzy.timeguard.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION", "android.permission.VIBRATE", "android.permission.WAKE_LOCK", "android.permission.POST_NOTIFICATIONS")
        ShadowAlarmManager.setCanScheduleExactAlarms(true)
        ShadowMediaPlayer.setMediaInfoProvider { ShadowMediaPlayer.MediaInfo(2000, 0) }
    }
    private fun book() = AlarmBook().apply {
        addChain(AlarmChainSpec(startAt = LocalTime.of(6, 30), count = 3, intervalMinutes = 5), "连锁", "group-a")
        addManual(LocalTime.NOON, "普通", AlarmRepeat.DAILY, emptySet(), false)
    }
    @Test fun everyMemberSwitchUpdatesTheWholeGroupAndOtherGroupsRemainEnabled() {
        val initial = book()
        initial.addChain(AlarmChainSpec(startAt = LocalTime.of(10, 0), count = 3), "另一组", "group-b")
        cfg.saveAlarms(initial)
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        try {
            fun list() = activity.findViewById<ViewGroup>(R.id.llAlarmList)
            for (index in 0..2) {
                list().getChildAt(index).findViewById<SwitchCompat>(R.id.swAlarmItem).isChecked = false
                assertTrue(cfg.loadAlarms().all.filter { it.chainId == "group-a" }.all { !it.enabled })
                assertTrue(cfg.loadAlarms().all.filter { it.chainId != "group-a" }.all { it.enabled })
                for (i in 0..2) assertFalse(list().getChildAt(i).findViewById<SwitchCompat>(R.id.swAlarmItem).isChecked)
                list().getChildAt(index).findViewById<SwitchCompat>(R.id.swAlarmItem).isChecked = true
                assertTrue(cfg.loadAlarms().all.all { it.enabled })
                for (i in 0..2) assertTrue(list().getChildAt(i).findViewById<SwitchCompat>(R.id.swAlarmItem).isChecked)
            }
        } finally { controller.pause().stop().destroy() }
    }
    @Test fun deletingAnyMemberThroughTheEditorRemovesAndUnschedulesTheWholeGroup() {
        for (index in 0..2) {
            cfg.saveAlarms(book())
            val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
            try {
                controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(index).performClick()
                shadowOf(Looper.getMainLooper()).idle()
                val dialog = ShadowDialog.getLatestDialog() as AlertDialog
                assertEquals("删除整组", dialog.getButton(AlertDialog.BUTTON_NEUTRAL).text.toString())
                dialog.getButton(AlertDialog.BUTTON_NEUTRAL).performClick()
                val saved = cfg.loadAlarms()
                assertEquals(1, saved.size)
                assertTrue(saved.all.single().chainId.isEmpty())
                val prefs = app.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE)
                assertEquals(setOf(saved.all.single().id), prefs.getStringSet("pendingAlarmIds", emptySet()))
                assertEquals(1, controller.get().findViewById<ViewGroup>(R.id.llAlarmList).childCount)
            } finally { controller.pause().stop().destroy() }
        }
    }
    @Test fun ordinaryAlarmSwitchAndDeleteDoNotAffectTheChain() {
        cfg.saveAlarms(book())
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val list = controller.get().findViewById<ViewGroup>(R.id.llAlarmList)
            list.getChildAt(3).findViewById<SwitchCompat>(R.id.swAlarmItem).isChecked = false
            assertTrue(cfg.loadAlarms().all.filter { it.chainId.isNotEmpty() }.all { it.enabled })
            assertFalse(cfg.loadAlarms().all.single { it.chainId.isEmpty() }.enabled)
            list.getChildAt(3).performClick()
            shadowOf(Looper.getMainLooper()).idle()
            (ShadowDialog.getLatestDialog() as AlertDialog).getButton(AlertDialog.BUTTON_NEUTRAL).performClick()
            assertEquals(3, cfg.loadAlarms().size)
        } finally { controller.pause().stop().destroy() }
    }
    @Test fun deletingAnEditorMemberConsumedWhileTheDialogWasOpenStillDeletesItsGroup() {
        val initial = book()
        val first = initial.all.first()
        cfg.saveAlarms(initial)
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(0).performClick()
            shadowOf(Looper.getMainLooper()).idle()
            initial.remove(first.id)
            cfg.saveAlarms(initial)
            (ShadowDialog.getLatestDialog() as AlertDialog).getButton(AlertDialog.BUTTON_NEUTRAL).performClick()
            assertEquals(1, cfg.loadAlarms().size)
            assertTrue(cfg.loadAlarms().all.single().chainId.isEmpty())
        } finally { controller.pause().stop().destroy() }
    }
    @Test fun savingAnEditorCannotResurrectAConsumedAlarmOrLoseBackgroundAdditions() {
        val original = AlarmBook()
        val once = original.addManual(LocalTime.of(7, 0), "一次性", AlarmRepeat.ONCE, emptySet(), true)
        cfg.saveAlarms(original)
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(0).performClick()
            shadowOf(Looper.getMainLooper()).idle()
            original.consumeOccurrence(once)
            original.addManual(LocalTime.NOON, "后台新增", AlarmRepeat.DAILY, emptySet(), false)
            cfg.saveAlarms(original)
            (ShadowDialog.getLatestDialog() as AlertDialog).getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            assertNull(cfg.loadAlarms().find(once.id))
            assertEquals("后台新增", cfg.loadAlarms().all.single().label)
        } finally { controller.pause().stop().destroy() }
    }
    @Test fun savingAnEditorPreservesRuntimeDisableAndNewAlarms() {
        val original = book()
        val first = original.all.first()
        cfg.saveAlarms(original)
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(0).performClick()
            shadowOf(Looper.getMainLooper()).idle()
            (ShadowDialog.getLatestDialog() as AlertDialog).findViewById<android.widget.EditText>(R.id.etAlarmLabel)!!.setText("修改后的名称")
            original.setChainEnabled(first.chainId, false)
            original.addManual(LocalTime.of(13, 0), "稍后新增", AlarmRepeat.ONCE, emptySet(), true)
            cfg.saveAlarms(original)
            (ShadowDialog.getLatestDialog() as AlertDialog).getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            val saved = cfg.loadAlarms()
            assertEquals(5, saved.size)
            assertEquals("修改后的名称", saved.find(first.id)!!.label)
            assertTrue(saved.all.filter { it.chainId == first.chainId }.all { !it.enabled })
            assertTrue(saved.all.any { it.label == "稍后新增" })
        } finally { controller.pause().stop().destroy() }
    }
    @Test fun addingAnAlarmPreservesRecordsCreatedWhileTheDialogWasOpen() {
        val original = book()
        cfg.saveAlarms(original)
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            controller.get().findViewById<android.view.View>(R.id.btnAlarmAdd).performClick()
            shadowOf(Looper.getMainLooper()).idle()
            original.addManual(LocalTime.of(13, 0), "后台新增", AlarmRepeat.DAILY, emptySet(), false)
            cfg.saveAlarms(original)
            (ShadowDialog.getLatestDialog() as AlertDialog).getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            assertEquals(6, cfg.loadAlarms().size)
            assertTrue(cfg.loadAlarms().all.any { it.label == "后台新增" })
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun editingAnyMemberMovesTheWholeChainAndReschedulesIt() {
        for (index in 0..2) {
            cfg.saveAlarms(book())
            val before = cfg.loadAlarms().all.filter { it.chainId == "group-a" }.sortedBy { it.chainIndex }
            val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
            try {
                controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(index).performClick()
                shadowOf(Looper.getMainLooper()).idle()
                val dialog = ShadowDialog.getLatestDialog() as AlertDialog
                dialog.findViewById<android.view.View>(R.id.btnAlarmTime)!!.performClick()
                val picker = ShadowDialog.getLatestDialog() as android.app.TimePickerDialog
                picker.updateTime(7, index * 5)
                picker.getButton(android.content.DialogInterface.BUTTON_POSITIVE).performClick()
                dialog.findViewById<android.widget.EditText>(R.id.etAlarmLabel)!!.setText("新的起床时间")
                dialog.findViewById<android.widget.RadioButton>(R.id.rbAlarmWeekly)!!.isChecked = true
                dialog.findViewById<com.google.android.material.button.MaterialButton>(R.id.btnDay1)!!.isChecked = true
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
                val saved = cfg.loadAlarms()
                val members = saved.all.filter { it.chainId == "group-a" }.sortedBy { it.chainIndex }
                assertEquals(listOf("07:00", "07:05", "07:10"), members.map { it.timeText })
                assertEquals(before.map { it.id }, members.map { it.id })
                assertTrue(members.all { it.label == "新的起床时间" && it.repeat == AlarmRepeat.WEEKLY && it.days == setOf(java.time.DayOfWeek.MONDAY) })
                assertEquals("12:00", saved.all.single { it.chainId.isEmpty() }.timeText)
                // 系统只注册下一次唤醒；推进到每个成员之前，验证后续调度也使用新时间。
                for (member in members) {
                    val beforeRing = java.time.LocalDate.of(2026, 10, 26).atTime(member.at).minusSeconds(1)
                    AlarmScheduler.schedule(app, beforeRing)
                    val alarm = shadowOf(app.getSystemService(android.app.AlarmManager::class.java)).scheduledAlarms.single()
                    assertEquals(listOf(member.id), shadowOf(alarm.operation).savedIntent.getStringArrayListExtra(AlarmRingService.EXTRA_IDS))
                    val scheduledTime = java.time.Instant.ofEpochMilli(alarm.triggerAtTime).atZone(ZoneId.systemDefault()).toLocalTime()
                    assertEquals(member.at, scheduledTime)
                }
            } finally { controller.pause().stop().destroy() }
        }
    }

    @Test fun existingChainCanGrowShrinkAndRememberItsIntervalAfterReopening() {
        cfg.saveAlarms(book())
        val before = cfg.loadAlarms().chains().single().second
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            fun open(index: Int): AlertDialog {
                activity.findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(index).performClick()
                shadowOf(Looper.getMainLooper()).idle()
                return ShadowDialog.getLatestDialog() as AlertDialog
            }
            var dialog = open(1)
            assertEquals("3", dialog.findViewById<android.widget.EditText>(R.id.etEditChainCount)!!.text.toString())
            assertEquals("5", dialog.findViewById<android.widget.EditText>(R.id.etEditChainInterval)!!.text.toString())
            dialog.findViewById<android.widget.EditText>(R.id.etEditChainCount)!!.setText("5")
            dialog.findViewById<android.widget.EditText>(R.id.etEditChainInterval)!!.setText("7")
            assertTrue(dialog.findViewById<android.widget.TextView>(R.id.tvEditChainPreview)!!.text.contains("06:58"))
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            var members = cfg.loadAlarms().chains().single().second
            assertEquals(listOf("06:30", "06:37", "06:44", "06:51", "06:58"), members.map { it.timeText })
            assertEquals(before.map { it.id }, members.take(3).map { it.id })
            assertEquals("普通", cfg.loadAlarms().all.single { it.chainId.isEmpty() }.label)
            dialog = open(0)
            dialog.findViewById<android.widget.EditText>(R.id.etEditChainCount)!!.setText("1")
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            assertEquals(1, cfg.loadAlarms().chains().single().second.size)
            dialog = open(0)
            assertEquals("7", dialog.findViewById<android.widget.EditText>(R.id.etEditChainInterval)!!.text.toString())
            dialog.findViewById<android.widget.EditText>(R.id.etEditChainCount)!!.setText("3")
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            members = cfg.loadAlarms().chains().single().second
            assertEquals(listOf("06:30", "06:37", "06:44"), members.map { it.timeText })
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun invalidChainSizeOrIntervalDoesNotSaveOrDismissTheEditor() {
        cfg.saveAlarms(book())
        val before = cfg.loadAlarms().all
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(0).performClick()
            val dialog = ShadowDialog.getLatestDialog() as AlertDialog
            for ((count, interval) in listOf("0" to "5", "13" to "5", "3" to "0", "3" to "121", "" to "5")) {
                dialog.findViewById<android.widget.EditText>(R.id.etEditChainCount)!!.setText(count)
                dialog.findViewById<android.widget.EditText>(R.id.etEditChainInterval)!!.setText(interval)
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
                assertTrue(dialog.isShowing)
                assertEquals(before, cfg.loadAlarms().all)
            }
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun resizingAChainDoesNotResurrectMembersConsumedWhileEditing() {
        val initial = book()
        cfg.saveAlarms(initial)
        val first = initial.chains().single().second.first()
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            controller.get().findViewById<ViewGroup>(R.id.llAlarmList).getChildAt(1).performClick()
            val dialog = ShadowDialog.getLatestDialog() as AlertDialog
            dialog.findViewById<android.widget.EditText>(R.id.etEditChainCount)!!.setText("5")
            initial.remove(first.id)
            cfg.saveAlarms(initial)
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            assertNull(cfg.loadAlarms().find(first.id))
            assertEquals(2, cfg.loadAlarms().chains().single().second.size)
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun homeShowsTheInstalledVersion() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val version = controller.get().findViewById<android.widget.TextView>(R.id.tvAppVersion)
            assertEquals(android.view.View.VISIBLE, version.visibility)
            assertEquals("版本 1.2.13（16）", version.text.toString())
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun stoppingTheRingingMemberKeepsLaterMembersEnabledAndScheduled() {
        val at = LocalDateTime.now().withSecond(0).withNano(0)
        val initial = AlarmBook()
        val chain = initial.addChain(AlarmChainSpec(startAt = at.toLocalTime(), count = 3, intervalMinutes = 5), "连锁")
        cfg.saveAlarms(initial)
        val intent = Intent(app, AlarmRingService::class.java).setAction(AlarmRingService.ACTION_FIRE)
            .putExtra(AlarmRingService.EXTRA_DUE_AT, at.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli())
            .putStringArrayListExtra(AlarmRingService.EXTRA_IDS, arrayListOf(chain.first().id))
        val controller = Robolectric.buildService(AlarmRingService::class.java, intent).create()
        try {
            controller.get().onStartCommand(intent, 0, 1)
            assertTrue(AlarmRingService.isRinging)
            controller.get().onStartCommand(Intent(app, AlarmRingService::class.java).setAction(AlarmRingService.ACTION_DISMISS), 0, 2)
            assertFalse(AlarmRingService.isRinging)
            assertEquals(3, cfg.loadAlarms().size)
            assertTrue(cfg.loadAlarms().all.all { it.enabled })
            val pending = app.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE).getStringSet("pendingAlarmIds", emptySet()).orEmpty()
            assertTrue(chain.drop(1).any { it.id in pending })
        } finally { controller.destroy() }
    }
}
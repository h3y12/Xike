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
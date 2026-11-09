package com.hzy.xike

import android.app.AlarmManager
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.os.Looper
import android.view.View
import android.view.ViewGroup
import com.hzy.xike.core.*
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
import org.robolectric.shadows.ShadowSettings
import org.robolectric.shadows.ShadowMediaPlayer
import java.time.LocalDateTime

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class CountdownFeaturesTest {
    private val app get() = RuntimeEnvironment.getApplication()
    private val cfg get() = AppConfig(app)
    private val alarms get() = shadowOf(app.getSystemService(Context.ALARM_SERVICE) as AlarmManager)
    @Before fun setup() {
        app.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE).edit().clear().commit()
        shadowOf(app).grantPermissions(app.packageName + ".DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION", "android.permission.WAKE_LOCK", "android.permission.VIBRATE")
        ShadowAlarmManager.setCanScheduleExactAlarms(true)
        ShadowSettings.setCanDrawOverlays(true)
        ShadowMediaPlayer.setMediaInfoProvider { ShadowMediaPlayer.MediaInfo(2000, 0) }
    }
    private fun countdown(due: LocalDateTime, enabled: Boolean = true) {
        cfg.cdRingEnabled = enabled
        cfg.cdSessionId = "session"
        val timer = ManualCountdown()
        timer.startUntil(due)
        cfg.saveCountdown(timer.snapshot())
    }
    private fun dueIntent(): Intent = Intent(app, AlarmRingService::class.java)
        .setAction(AlarmRingService.ACTION_COUNTDOWN)
        .putExtra(CountdownScheduler.EXTRA_TOKEN, cfg.cdSessionId + ":" + cfg.loadCountdown().deadline)

    @Test fun soundDefaultsOffAndEnablingSchedulesSystemWakeUp() {
        assertFalse(cfg.cdRingEnabled)
        countdown(LocalDateTime.now().plusMinutes(10), false)
        CountdownScheduler.schedule(app)
        assertTrue(alarms.scheduledAlarms.isEmpty())
        cfg.cdRingEnabled = true
        assertTrue(CountdownScheduler.schedule(app))
        val delivery = shadowOf(alarms.scheduledAlarms.single().operation).savedIntent
        assertEquals(AlarmRingService.ACTION_COUNTDOWN, delivery.action)
        cfg.cdRingEnabled = false
        CountdownScheduler.schedule(app)
        assertTrue(alarms.scheduledAlarms.isEmpty())
    }
    @Test fun pauseResetAndLinkedAlarmCancelTheExtraWakeUp() {
        countdown(LocalDateTime.now().plusMinutes(5))
        CountdownScheduler.schedule(app)
        cfg.saveCountdown(cfg.loadCountdown().copy(phase = CdPhase.Paused.name))
        CountdownScheduler.schedule(app)
        assertTrue(alarms.scheduledAlarms.isEmpty())
        countdown(LocalDateTime.now().plusMinutes(5))
        CountdownScheduler.schedule(app)
        cfg.cdSourceAlarmId = "existing"
        CountdownScheduler.schedule(app)
        assertTrue(alarms.scheduledAlarms.isEmpty())
        assertNull(CountdownScheduler.consume(app, dueIntent(), LocalDateTime.now().plusMinutes(5)))
        cfg.cdSourceAlarmId = ""
        cfg.saveCountdown(CdSnapshot())
        CountdownScheduler.schedule(app)
        assertTrue(alarms.scheduledAlarms.isEmpty())
    }
    @Test fun aQueuedFinishIsAcceptedOnceAndStaleOrEarlyDeliveriesAreIgnored() {
        val due = LocalDateTime.now().withNano(0)
        countdown(due)
        val intent = dueIntent()
        assertNull(CountdownScheduler.consume(app, intent, due.minusSeconds(1)))
        assertNull(CountdownScheduler.consume(app, Intent(intent).putExtra(CountdownScheduler.EXTRA_TOKEN, "old"), due))
        cfg.saveCountdown(cfg.loadCountdown().copy(phase = CdPhase.Finished.name))
        assertNotNull(CountdownScheduler.consume(app, intent, due.plusSeconds(1)))
        assertNull(CountdownScheduler.consume(app, intent, due.plusSeconds(2)))
        assertTrue(cfg.loadAlarms().all.isEmpty())
        countdown(due.minusMinutes(5))
        assertNull(CountdownScheduler.consume(app, dueIntent(), due))
    }
    @Test fun packageUpdateRestoresAnUndeliveredCountdownAtItsDeadline() {
        val now = LocalDateTime.now()
        countdown(now.minusSeconds(5))
        cfg.saveCountdown(cfg.loadCountdown().copy(phase = CdPhase.Finished.name))
        val update = Intent(Intent.ACTION_MY_PACKAGE_REPLACED)
        AlarmRescheduleReceiver().onReceive(app, update)
        val delivery = shadowOf(alarms.scheduledAlarms.single().operation).savedIntent
        assertEquals(AlarmRingService.ACTION_COUNTDOWN, delivery.action)
        assertNotNull(CountdownScheduler.consume(app, delivery, now))
        AlarmRescheduleReceiver().onReceive(app, update)
        assertTrue(alarms.scheduledAlarms.isEmpty())
    }

    @Test fun completionRingsInIndependentServiceAndHasOnlyStop() {
        countdown(LocalDateTime.now().minusSeconds(1))
        val intent = dueIntent()
        val service = Robolectric.buildService(AlarmRingService::class.java, intent).create()
        try {
            service.get().onStartCommand(intent, 0, 1)
            assertTrue(AlarmRingService.isRinging)
            assertEquals("倒计时结束", AlarmRingService.current!!.alarm.label)
            assertFalse(AlarmRingService.current!!.alarm.canSnooze)
            assertNotNull(service.get().ringOverlay.view)
            val notification = shadowOf(app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).getNotification(AlarmRingService.NOTIFICATION_ID)
            assertEquals(1, notification.actions.size)
            service.get().onStartCommand(Intent(app, AlarmRingService::class.java).setAction(AlarmRingService.ACTION_DISMISS), 0, 2)
            assertFalse(AlarmRingService.isRinging)
            assertNull(service.get().ringOverlay.view)
        } finally { service.destroy() }
    }
    @Test fun fromAlarmUsesItsExactNextDateWithoutEditingOrDuplicatingTheAlarm() {
        val now = LocalDateTime.now()
        val book = AlarmBook()
        val alarm = book.addManual(now.toLocalTime().withSecond(0), "每周", AlarmRepeat.WEEKLY,
            setOf(now.plusDays(3).dayOfWeek), false)
        cfg.saveAlarms(book)
        cfg.cdRingEnabled = true
        val records = cfg.alarmRecords
        val intent = Intent(app, OverlayService::class.java).setAction(OverlayService.ACTION_CD_FROM_ALARM)
            .putExtra(OverlayService.EXTRA_ALARM_ID, alarm.id)
        val service = Robolectric.buildService(OverlayService::class.java).create()
        try {
            service.get().onStartCommand(intent, 0, 1)
            assertEquals(alarm.nextRingAfter(now), LocalDateTime.parse(cfg.loadCountdown().deadline))
            assertEquals(alarm.id, cfg.cdSourceAlarmId)
            assertEquals(records, cfg.alarmRecords)
            assertTrue(alarms.scheduledAlarms.isEmpty())
        } finally { service.destroy() }
    }
    @Test fun exactTargetSeveralDaysAwaySurvivesRestore() {
        val now = LocalDateTime.of(2026, 10, 27, 15, 0)
        val due = now.plusDays(5).withHour(7).withMinute(30)
        val timer = ManualCountdown()
        timer.startUntil(due)
        val restored = ManualCountdown()
        restored.restore(timer.snapshot(), now.plusHours(1))
        assertEquals(due, LocalDateTime.parse(restored.snapshot().deadline))
        assertFalse(restored.pauseAllowed)
    }
    @Test fun notificationTogglePreservesTimerAndOffersAnOnActionWhileHidden() {
        countdown(LocalDateTime.now().plusMinutes(10))
        val service = Robolectric.buildService(OverlayService::class.java).create()
        try {
            service.get().onStartCommand(Intent(app, OverlayService::class.java).setAction(OverlayService.ACTION_START), 0, 1)
            shadowOf(Looper.getMainLooper()).idle()
            val snapshot = cfg.loadCountdown()
            fun notification() = shadowOf(app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).getNotification(1001)
            assertTrue(notification().actions.any { it.title.toString() == "关闭悬浮窗" })
            val toggle = Intent(app, OverlayService::class.java).setAction(OverlayService.ACTION_TOGGLE_OVERLAY)
            service.get().onStartCommand(toggle, 0, 2)
            shadowOf(Looper.getMainLooper()).idle()
            assertFalse(cfg.overlayVisible)
            assertEquals(snapshot, cfg.loadCountdown())
            assertTrue(OverlayService.isRunning)
            assertTrue(notification().actions.any { it.title.toString() == "开启悬浮窗" })
            service.get().onStartCommand(toggle, 0, 3)
            assertTrue(cfg.overlayVisible)
        } finally { service.destroy() }
    }
    @Test fun addButtonsAppearAboveTheAlarmList() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            val list = activity.findViewById<View>(R.id.llAlarmList)
            val addRow = activity.findViewById<View>(R.id.btnAlarmAdd).parent as View
            val parent = list.parent as ViewGroup
            assertSame(parent, addRow.parent)
            assertTrue(parent.indexOfChild(addRow) < parent.indexOfChild(list))
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun openingAtTheDeadlineDoesNotCancelAQueuedSystemDelivery() {
        val now = LocalDateTime.now().withNano(0)
        val due = now.plusMinutes(1)
        countdown(due)
        CountdownScheduler.schedule(app, now)
        val operation = alarms.scheduledAlarms.single().operation
        CountdownScheduler.schedule(app, due.plusSeconds(1))
        assertEquals(operation, alarms.scheduledAlarms.single().operation)
        cfg.saveCountdown(cfg.loadCountdown().copy(phase = CdPhase.Finished.name))
        CountdownScheduler.schedule(app, due.plusSeconds(2))
        assertEquals(operation, alarms.scheduledAlarms.single().operation)
        assertNotNull(CountdownScheduler.consume(app, dueIntent(), due.plusSeconds(3)))
        assertTrue(alarms.scheduledAlarms.isEmpty())
    }

    @Test fun countdownCommandIsSentBeforeThePageCanLeaveWithoutADelayedStart() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        activity.findViewById<android.widget.EditText>(R.id.etCdInput).setText("45m")
        activity.findViewById<View>(R.id.btnCdStart).performClick()
        assertEquals(OverlayService.ACTION_START, shadowOf(app).nextStartedService.action)
        assertEquals(OverlayService.ACTION_CD_START, shadowOf(app).nextStartedService.action)
        controller.pause().stop().destroy()
        shadowOf(Looper.getMainLooper()).idle()
        assertNull(shadowOf(app).nextStartedService)
    }

    @Test fun alarmAddButtonsHaveEqualMeasuredBoundsWhenALabelUsesTwoLines() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            val add = activity.findViewById<android.widget.Button>(R.id.btnAlarmAdd)
            val chain = activity.findViewById<android.widget.Button>(R.id.btnAlarmChain)
            val row = add.parent as ViewGroup
            add.textSize = 32f
            chain.textSize = 32f
            // Explicit lines avoid depending on the host JVM's CJK font metrics.
            chain.text = "起床闹钟\n（连锁）"
            row.measure(View.MeasureSpec.makeMeasureSpec(400, View.MeasureSpec.EXACTLY),
                View.MeasureSpec.makeMeasureSpec(1000, View.MeasureSpec.AT_MOST))
            row.layout(0, 0, row.measuredWidth, row.measuredHeight)
            assertTrue(chain.lineCount > 1)
            assertTrue(add.height > 0)
            assertEquals(add.height, chain.height)
            assertEquals(add.top, chain.top)
            assertEquals(add.bottom, chain.bottom)
            assertTrue(kotlin.math.abs(add.width - chain.width) <= 1)
        } finally { controller.pause().stop().destroy() }
    }
}

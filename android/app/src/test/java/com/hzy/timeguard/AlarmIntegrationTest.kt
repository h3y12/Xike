package com.hzy.timeguard

import android.app.AlarmManager
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.media.MediaPlayer
import android.os.Looper
import android.os.Vibrator
import android.view.View
import android.widget.Button
import androidx.appcompat.app.AlertDialog
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
import org.robolectric.android.controller.ServiceController
import org.robolectric.shadows.*
import java.time.LocalDateTime
import java.time.ZoneId

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class AlarmIntegrationTest {
    private val app get() = RuntimeEnvironment.getApplication()
    private val players = mutableListOf<ShadowMediaPlayer>()
    @Before fun setup() {
        app.getSharedPreferences("timeguard", Context.MODE_PRIVATE).edit().clear().commit()
        shadowOf(app).grantPermissions("com.hzy.timeguard.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION", "android.permission.VIBRATE", "android.permission.WAKE_LOCK", "android.permission.POST_NOTIFICATIONS")
        ShadowSettings.setCanDrawOverlays(true)
        ShadowAlarmManager.setCanScheduleExactAlarms(true)
        ShadowMediaPlayer.setMediaInfoProvider { ShadowMediaPlayer.MediaInfo(2000, 0) }
        ShadowMediaPlayer.setCreateListener { _, shadow -> players += shadow }
    }

    private fun fire(repeat: AlarmRepeat = AlarmRepeat.ONCE, keep: Boolean = false): ServiceController<AlarmRingService> {
        val at = LocalDateTime.now().withSecond(0).withNano(0)
        val book = AlarmBook()
        val alarm = book.addManual(at.toLocalTime(), "测试", repeat, emptySet(), !keep)
        AppConfig(app).saveAlarms(book)
        val intent = Intent(app, AlarmRingService::class.java).setAction(AlarmRingService.ACTION_FIRE)
            .putExtra(AlarmRingService.EXTRA_DUE_AT, at.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli())
            .putStringArrayListExtra(AlarmRingService.EXTRA_IDS, arrayListOf(alarm.id))
        val controller = Robolectric.buildService(AlarmRingService::class.java, intent).create()
        controller.get().onStartCommand(intent, 0, 1)
        return controller
    }

    private fun assertStopped() {
        assertFalse(AlarmRingService.isRinging)
        assertEquals(ShadowMediaPlayer.State.END, players.last().state)
        assertFalse(shadowOf(app.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator).isVibrating)
    }

    @Test fun savingAlarmSchedulesItWhileOverlayIsOff() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        activity.findViewById<Button>(R.id.btnAlarmAdd).performClick()
        val dialog = ShadowDialog.getLatestDialog() as AlertDialog
        shadowOf(Looper.getMainLooper()).idle()
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
        assertEquals("Actual save button must persist its alarm", 1, AppConfig(app).loadAlarms().size)
        val manager = app.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        assertFalse("Saving must schedule an alarm without the overlay", shadowOf(manager).scheduledAlarms.isEmpty())
        val operation = shadowOf(manager).scheduledAlarms.single().operation
        assertEquals(AlarmRingService::class.java.name, shadowOf(operation).savedIntent.component!!.className)
        assertFalse(OverlayService.isRunning)
        controller.pause().stop().destroy()
    }

    @Test fun oneShotIsRemovedWhenItRingsWithoutDismiss() {
        val controller = fire()
        assertTrue(AlarmRingService.isRinging)
        assertTrue("One-shot must be deleted on firing", AppConfig(app).loadAlarms().all.isEmpty())
        assertEquals(ShadowMediaPlayer.State.STARTED, players.last().state)
        controller.get().tickAt(AlarmRingService.current!!.endsAt)
        assertStopped()
        controller.destroy()
    }

    @Test fun repeatingAlarmRemainsAndSchedulesItsNextOccurrence() {
        val controller = fire(AlarmRepeat.DAILY)
        assertEquals(1, AppConfig(app).loadAlarms().size)
        val manager = app.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        assertEquals(1, shadowOf(manager).scheduledAlarms.size)
        assertTrue(shadowOf(manager).scheduledAlarms.single().triggerAtTime > System.currentTimeMillis())
        controller.destroy()
    }

    @Test fun keptOneShotIsDisabledAfterFiring() {
        val controller = fire(keep = true)
        val once = AppConfig(app).loadAlarms().all.single()
        assertFalse(once.enabled)
        assertNull(once.nextRingAfter(LocalDateTime.now()))
        controller.destroy()
    }

    @Test fun closingOverlayDoesNotStopTheIndependentAlarm() {
        val ring = fire()
        val overlay = Robolectric.buildService(OverlayService::class.java).create()
        overlay.destroy()
        assertTrue(AlarmRingService.isRinging)
        assertEquals(ShadowMediaPlayer.State.STARTED, players.last().state)
        val vibrator = shadowOf(app.getSystemService(Context.VIBRATOR_SERVICE) as Vibrator)
        assertTrue(vibrator.isVibrating)
        assertEquals(0, vibrator.repeat)
        ring.destroy()
    }

    @Test fun applicationStopButtonStopsSoundAndVibrationWithoutOverlay() {
        val ring = fire()
        val activity = Robolectric.buildActivity(MainActivity::class.java).setup()
        assertEquals(View.VISIBLE, activity.get().findViewById<View>(R.id.alarmRingControls).visibility)
        activity.get().findViewById<Button>(R.id.btnAlarmDismiss).performClick()
        val command = shadowOf(app).nextStartedService
        assertEquals(AlarmRingService.ACTION_DISMISS, command.action)
        ring.get().onStartCommand(command, 0, 2)
        assertStopped()
        activity.pause().stop().destroy()
        ring.destroy()
    }

    @Test fun notificationSnoozeStopsAndSchedulesNewOneShot() {
        val ring = fire()
        val notification = shadowOf(app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
            .getNotification(AlarmRingService.NOTIFICATION_ID)
        notification.actions[1].actionIntent.send()
        val command = shadowOf(app).nextStartedService
        assertEquals(AlarmRingService.ACTION_SNOOZE, command.action)
        ring.get().onStartCommand(command, 0, 2)
        assertStopped()
        val snoozed = AppConfig(app).loadAlarms().all.single()
        assertEquals(AlarmRepeat.ONCE, snoozed.repeat)
        val manager = app.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        assertEquals(1, shadowOf(manager).scheduledAlarms.size)
        ring.destroy()
    }

    @Test fun ringingNotificationOpensDedicatedLockScreenControlsAndCanStopDirectly() {
        val ring = fire()
        val notification = shadowOf(app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
            .getNotification(AlarmRingService.NOTIFICATION_ID)
        assertNotNull("Full-screen intent must be on the first posted foreground notification", shadowOf(ring.get()).lastForegroundNotification.fullScreenIntent)
        assertEquals(AlarmRingActivity::class.java.name, shadowOf(notification.fullScreenIntent).savedIntent.component!!.className)
        assertEquals(AlarmRingActivity::class.java.name, shadowOf(notification.contentIntent).savedIntent.component!!.className)
        notification.actions[0].actionIntent.send()
        val command = shadowOf(app).nextStartedService
        assertEquals(AlarmRingService.ACTION_DISMISS, command.action)
        ring.get().onStartCommand(command, 0, 2)
        assertStopped()
        ring.destroy()
    }

    @Test fun lockScreenStopButtonStopsWithoutOpeningSettings() {
        val ring = fire()
        val controller = Robolectric.buildActivity(AlarmRingActivity::class.java).setup()
        controller.get().findViewById<Button>(R.id.btnAlarmDismiss).performClick()
        val command = shadowOf(app).nextStartedService
        ring.get().onStartCommand(command, 0, 2)
        assertStopped()
        controller.pause().stop().destroy()
        ring.destroy()
    }

    @Test fun openingExpiredRingControlsDoesNotShowAnEmptyAlarm() {
        val controller = Robolectric.buildActivity(AlarmRingActivity::class.java).setup()
        assertTrue(controller.get().isFinishing)
        controller.pause().stop().destroy()
    }

    @Test fun deletingAllAlarmsCancelsTheSystemWakeUp() {
        val book = AlarmBook()
        book.addManual(LocalDateTime.now().plusMinutes(5).toLocalTime(), "测试", AlarmRepeat.DAILY, emptySet(), false)
        AppConfig(app).saveAlarms(book)
        AlarmScheduler.schedule(app)
        val manager = app.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        assertEquals(1, shadowOf(manager).scheduledAlarms.size)
        AppConfig(app).saveAlarms(AlarmBook())
        AlarmScheduler.schedule(app)
        assertTrue(shadowOf(manager).scheduledAlarms.isEmpty())
    }

    @Test fun openingAppAtScheduledMinuteDoesNotAdvanceAlarmToTomorrow() {
        val due = LocalDateTime.now().withSecond(0).withNano(0)
        val book = AlarmBook()
        book.addManual(due.toLocalTime(), "测试", AlarmRepeat.DAILY, emptySet(), false)
        AppConfig(app).saveAlarms(book)
        AlarmScheduler.schedule(app, due.minusMinutes(1))
        val manager = app.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val expected = shadowOf(manager).scheduledAlarms.single().triggerAtTime
        AlarmScheduler.schedule(app, due.plusSeconds(2))
        assertEquals("Opening settings during delivery must not postpone this alarm to tomorrow", expected, shadowOf(manager).scheduledAlarms.single().triggerAtTime)
    }

    @Test @Config(sdk = [35]) fun exactAlarmPermissionIsRequiredAndDisplayedAccurately() {
        val book = AlarmBook()
        book.addManual(LocalDateTime.now().plusMinutes(5).toLocalTime(), "测试", AlarmRepeat.DAILY, emptySet(), false)
        AppConfig(app).saveAlarms(book)
        shadowOf(app.getSystemService(Context.POWER_SERVICE) as android.os.PowerManager).setIgnoringBatteryOptimizations(app.packageName, true)
        ShadowAlarmManager.setCanScheduleExactAlarms(false)
        val activity = Robolectric.buildActivity(MainActivity::class.java).setup()
        assertEquals(View.VISIBLE, activity.get().findViewById<View>(R.id.btnGrantAlarm).visibility)
        assertFalse(AlarmScheduler.schedule(app))
        ShadowAlarmManager.setCanScheduleExactAlarms(true)
        activity.pause().resume()
        assertEquals(View.GONE, activity.get().findViewById<View>(R.id.btnGrantAlarm).visibility)
        assertTrue(AlarmScheduler.schedule(app))
        activity.pause().stop().destroy()
    }
}

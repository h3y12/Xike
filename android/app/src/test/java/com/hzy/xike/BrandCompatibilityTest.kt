package com.hzy.xike

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import com.hzy.xike.core.AppConfig
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class BrandCompatibilityTest {
    @Test fun renamedCodeReadsExistingUserPreferencesWithoutReset() {
        val app = RuntimeEnvironment.getApplication()
        val prefs = app.getSharedPreferences(LegacyIdentity.PREFERENCES, Context.MODE_PRIVATE)
        prefs.edit().clear().putString("showAtText", "21:45")
            .putString("alarmSoundUri", "content://example/alarm")
            .putBoolean("cdRingEnabled", true).commit()
        val cfg = AppConfig(app)
        assertEquals("21:45", cfg.showAtText)
        assertEquals("content://example/alarm", cfg.alarmSoundUri)
        assertTrue(cfg.cdRingEnabled)
        assertEquals(LegacyIdentity.OVERLAY_CHANNEL, OverlayService.CHANNEL_ID)
        assertEquals(LegacyIdentity.ALARM_CHANNEL, AlarmRingService.CHANNEL_ID)
    }

    @Test fun updateRemovesObsoleteServiceRegistrationsBeforeRescheduling() {
        val app = RuntimeEnvironment.getApplication()
        val old = Intent().setClassName(app.packageName, app.packageName + ".AlarmRingService")
            .setAction(app.packageName + ".alarm.FIRE")
        PendingIntent.getForegroundService(app, 100, old, PendingIntent.FLAG_IMMUTABLE)
        LegacyIdentity.cancelObsoleteAlarms(app)
        assertNull(PendingIntent.getForegroundService(app, 100, old,
            PendingIntent.FLAG_NO_CREATE or PendingIntent.FLAG_IMMUTABLE))
    }

    @Test fun packageReplacementReceiverAndLauncherResolveAfterNamespaceRename() {
        val app = RuntimeEnvironment.getApplication()
        val receivers = app.packageManager.queryBroadcastReceivers(
            Intent(Intent.ACTION_MY_PACKAGE_REPLACED).setPackage(app.packageName), 0)
        assertTrue(receivers.any { it.activityInfo.name == AlarmRescheduleReceiver::class.java.name })
        val launcher = app.packageManager.getLaunchIntentForPackage(app.packageName)
        assertNotNull(launcher)
        assertEquals(app.packageName + ".MainActivity", launcher!!.component!!.className)
        assertEquals("com.hzy.xike.MainActivity", app.packageManager.getActivityInfo(launcher.component!!, 0).targetActivity)
    }
}

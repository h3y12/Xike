package com.hzy.timeguard

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.media.RingtoneManager
import android.net.Uri
import android.view.View
import androidx.appcompat.app.AlertDialog
import com.hzy.timeguard.core.AppConfig
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowDialog

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class AlarmSoundPickerTest {
    private val app get() = RuntimeEnvironment.getApplication()
    @Before fun setup() {
        app.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE).edit().clear().commit()
        shadowOf(app).grantPermissions("com.hzy.timeguard.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION")
    }

    private fun choose(activity: MainActivity, index: Int) {
        activity.findViewById<View>(R.id.btnAlarmSound).performClick()
        val dialog = ShadowDialog.getLatestDialog() as AlertDialog
        dialog.listView.performItemClick(null, index, index.toLong())
    }

    @Test fun systemPickerSelectionPersistsAndCancellationKeepsIt() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            choose(activity, 0)
            val request = shadowOf(activity).nextStartedActivityForResult.intent
            assertEquals(RingtoneManager.ACTION_RINGTONE_PICKER, request.action)
            assertEquals(RingtoneManager.TYPE_ALARM, request.getIntExtra(RingtoneManager.EXTRA_RINGTONE_TYPE, -1))
            assertFalse(request.getBooleanExtra(RingtoneManager.EXTRA_RINGTONE_SHOW_SILENT, true))
            val selected = Uri.parse("content://media/internal/audio/media/42")
            shadowOf(activity).receiveResult(request, Activity.RESULT_OK,
                Intent().putExtra(RingtoneManager.EXTRA_RINGTONE_PICKED_URI, selected))
            assertEquals(selected.toString(), AppConfig(app).alarmSoundUri)
            choose(activity, 0)
            val cancelled = shadowOf(activity).nextStartedActivityForResult.intent
            shadowOf(activity).receiveResult(cancelled, Activity.RESULT_CANCELED, null)
            assertEquals(selected.toString(), AppConfig(app).alarmSoundUri)
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun documentPickerRequestsAudioAndPersistableAccess() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            choose(activity, 1)
            val request = shadowOf(activity).nextStartedActivityForResult.intent
            assertEquals(Intent.ACTION_OPEN_DOCUMENT, request.action)
            assertArrayEquals(arrayOf("audio/*"), request.getStringArrayExtra(Intent.EXTRA_MIME_TYPES))
            val selected = Uri.parse("content://documents/audio/42")
            shadowOf(activity).receiveResult(request, Activity.RESULT_OK, Intent().setData(selected)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION))
            assertEquals(selected.toString(), AppConfig(app).alarmSoundUri)
            assertTrue(app.contentResolver.persistedUriPermissions.any { it.uri == selected && it.isReadPermission })
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun restoreDefaultOnlyChangesTheSoundPreference() {
        val cfg = AppConfig(app)
        cfg.alarmSoundUri = "content://media/internal/audio/media/42"
        cfg.alarmRingSeconds = 55
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            choose(controller.get(), 2)
            assertEquals("", cfg.alarmSoundUri)
            assertEquals(55, cfg.alarmRingSeconds)
        } finally { controller.pause().stop().destroy() }
    }
}

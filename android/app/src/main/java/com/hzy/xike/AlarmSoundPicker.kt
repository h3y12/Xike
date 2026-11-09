package com.hzy.xike

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.media.RingtoneManager
import android.net.Uri
import android.provider.OpenableColumns
import android.widget.Button
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.hzy.xike.core.AppConfig

/** System tones and document-picked audio share a global, persistent alarm setting. */
class AlarmSoundPicker(
    private val activity: AppCompatActivity,
    private val button: Button,
    private val cfg: AppConfig,
) {
    private val systemPicker = activity.registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            @Suppress("DEPRECATION")
            val uri = result.data?.getParcelableExtra<Uri>(RingtoneManager.EXTRA_RINGTONE_PICKED_URI)
            if (uri != null) save(if (uri == RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)) "" else uri.toString())
        }
    }
    private val filePicker = activity.registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            try {
                activity.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
                save(uri.toString())
            } catch (_: SecurityException) {
                Toast.makeText(activity, R.string.alarm_sound_permission_failed, Toast.LENGTH_LONG).show()
            }
        }
    }

    init {
        refresh()
        button.setOnClickListener {
            AlertDialog.Builder(activity).setTitle(R.string.alarm_sound_title)
                .setItems(arrayOf(activity.getString(R.string.alarm_sound_system),
                    activity.getString(R.string.alarm_sound_file), activity.getString(R.string.alarm_sound_default))) { _, choice ->
                    try {
                        when (choice) {
                            0 -> systemPicker.launch(Intent(RingtoneManager.ACTION_RINGTONE_PICKER)
                                .putExtra(RingtoneManager.EXTRA_RINGTONE_TYPE, RingtoneManager.TYPE_ALARM)
                                .putExtra(RingtoneManager.EXTRA_RINGTONE_SHOW_DEFAULT, true)
                                .putExtra(RingtoneManager.EXTRA_RINGTONE_SHOW_SILENT, false)
                                .putExtra(RingtoneManager.EXTRA_RINGTONE_EXISTING_URI,
                                    cfg.alarmSoundUri.takeIf { it.isNotBlank() }?.let(Uri::parse)
                                        ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)))
                            1 -> filePicker.launch(arrayOf("audio/*"))
                            2 -> save("")
                        }
                    } catch (_: ActivityNotFoundException) {
                        Toast.makeText(activity, R.string.alarm_sound_picker_missing, Toast.LENGTH_LONG).show()
                    }
                }.setNegativeButton(R.string.btn_cancel, null).show()
        }
    }

    private fun save(value: String) {
        val old = cfg.alarmSoundUri
        cfg.alarmSoundUri = value
        if (old.isNotBlank() && old != value) {
            try { activity.contentResolver.releasePersistableUriPermission(Uri.parse(old), Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            catch (_: SecurityException) { /* System tones need no persisted document permission. */ }
        }
        refresh()
    }

    private fun refresh() {
        val value = cfg.alarmSoundUri
        val name = if (value.isBlank()) activity.getString(R.string.alarm_sound_default) else {
            val uri = Uri.parse(value)
            runCatching {
                activity.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                    if (cursor.moveToFirst()) cursor.getString(0) else null
                }
            }.getOrNull()?.takeIf { it.isNotBlank() }
                ?: runCatching { RingtoneManager.getRingtone(activity, uri)?.getTitle(activity) }.getOrNull()
                ?: activity.getString(R.string.alarm_sound_custom)
        }
        button.text = activity.getString(R.string.alarm_sound_current, name)
    }
}

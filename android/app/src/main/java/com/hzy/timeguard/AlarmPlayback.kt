package com.hzy.timeguard

import android.content.Context
import android.media.AudioManager
import android.media.AudioAttributes
import android.media.MediaPlayer
import android.media.RingtoneManager
import android.net.Uri
import android.os.PowerManager
import android.os.VibrationEffect
import android.os.Vibrator
import android.util.Log
import com.hzy.timeguard.core.AlarmAlertOutput

/** Owns the looping alarm outputs independently of the notification channel. */
class AlarmPlayback(private val context: Context) : AlarmAlertOutput {
    private var player: MediaPlayer? = null
    @Suppress("DEPRECATION")
    private val vibrator = context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
    private val attributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_ALARM)
        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()

    override fun startSound() {
        stopSound()
        val default = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
        val fallback = Uri.parse("android.resource://${context.packageName}/${R.raw.alarm_fallback}")
        for (uri in listOfNotNull(default, fallback)) {
            val candidate = MediaPlayer()
            try {
                candidate.setAudioAttributes(attributes)
                // 显式声明传统音量通道，兼容仅设置 USAGE_ALARM 时仍走媒体音量的系统。
                @Suppress("DEPRECATION")
                candidate.setAudioStreamType(AudioManager.STREAM_ALARM)
                candidate.setWakeMode(context, PowerManager.PARTIAL_WAKE_LOCK)
                candidate.setDataSource(context, uri)
                candidate.isLooping = true
                candidate.prepare()
                candidate.start()
                player = candidate
                return
            } catch (e: Exception) {
                candidate.release()
                Log.w("AlarmPlayback", "Unable to play alarm tone", e)
            }
        }
    }

    override fun stopSound() {
        player?.release()
        player = null
    }

    override fun startVibration() {
        try {
            vibrator?.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 600, 400), 0), attributes)
        } catch (e: Exception) {
            Log.e("AlarmPlayback", "Unable to start alarm vibration", e)
        }
    }

    override fun stopVibration() { vibrator?.cancel() }
}

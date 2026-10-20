package com.hzy.timeguard

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.hzy.timeguard.core.*
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId

/** Exists only while an alarm is ringing; closing the overlay never touches it. */
class AlarmRingService : Service() {
    private lateinit var cfg: AppConfig
    private val ringer = AlarmRinger()
    private val output by lazy { AlarmAlertSession(AlarmPlayback(this)) }
    private val handler = Handler(Looper.getMainLooper())
    private val ticker = object : Runnable {
        override fun run() {
            tickAt(LocalDateTime.now())
            if (ringer.isRinging) handler.postDelayed(this, 250)
        }
    }

    override fun onCreate() {
        super.onCreate()
        cfg = AppConfig(this)
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (manager.getNotificationChannel(CHANNEL_ID) == null) {
            val channel = NotificationChannel(CHANNEL_ID, "闹钟", NotificationManager.IMPORTANCE_HIGH)
            channel.description = "到点响铃与停止、稍后提醒操作"
            channel.setSound(null, null)
            channel.enableVibration(false)
            manager.createNotificationChannel(channel)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // System wake-up starts a foreground service; promote it before opening the audio file.
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification(ringer.current, intent?.action == ACTION_FIRE),
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK else 0)
        when (intent?.action) {
            ACTION_FIRE -> acceptDelivery(intent)
            ACTION_DISMISS -> ringer.dismissCurrent(LocalDateTime.now(), cfg.alarmRingSeconds)
            ACTION_SNOOZE -> {
                ringer.current?.let {
                    val book = cfg.loadAlarms()
                    book.snooze(it.alarm, LocalDateTime.now(), cfg.alarmSnoozeMinutes)
                    cfg.saveAlarms(book)
                    AlarmScheduler.schedule(this)
                    ringer.dismissCurrent(LocalDateTime.now(), cfg.alarmRingSeconds)
                }
            }
        }
        syncRing()
        handler.removeCallbacks(ticker)
        if (ringer.isRinging) handler.post(ticker)
        return START_NOT_STICKY
    }

    private fun acceptDelivery(intent: Intent) {
        val millis = intent.getLongExtra(EXTRA_DUE_AT, 0)
        val ids = intent.getStringArrayListExtra(EXTRA_IDS).orEmpty().toSet()
        if (millis <= 0 || ids.isEmpty()) return
        val now = LocalDateTime.now()
        val due = Instant.ofEpochMilli(millis).atZone(ZoneId.systemDefault()).toLocalDateTime()
        val prefs = getSharedPreferences(AppConfig.PREFS_NAME, MODE_PRIVATE)
        val signature = "$millis:${ids.sorted().joinToString(",") }"
        if (prefs.getString("lastAlarmDelivery", "") == signature) return
        val book = cfg.loadAlarms()
        val firings = book.all.filter { it.id in ids && it.enabled && it.nextRingAfter(due.minusSeconds(1)) == due }
            .map { AlarmFiring(it, due, book.isLastInChain(it)) }
        if (firings.isEmpty()) { AlarmScheduler.schedule(this); return }
        prefs.edit().putString("lastAlarmDelivery", signature).commit()
        firings.forEach { book.consumeOccurrence(it.alarm) }
        cfg.saveAlarms(book)
        AlarmScheduler.schedule(this, now)
        // An outdated delivery must not revive a consumed one-shot or ring hours late.
        if (now.isBefore(due) || now.isAfter(due.plusSeconds(90))) return
        ringer.accept(firings, now, cfg.alarmRingSeconds)
    }

    internal fun tickAt(now: LocalDateTime) {
        if (ringer.tick(now, cfg.alarmRingSeconds)) syncRing()
    }

    private fun syncRing() {
        current = ringer.current
        output.sync(current, cfg.alarmVibrate)
        sendBroadcast(Intent(OverlayService.ACTION_STATE_CHANGED).setPackage(packageName))
        if (current == null) {
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelf()
        } else {
            (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
                .notify(NOTIFICATION_ID, notification(current))
        }
    }

    private fun notification(state: RingingState?, incomingAlarm: Boolean = false): Notification {
        val open = PendingIntent.getActivity(this, 102, Intent(this, AlarmRingActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        fun action(action: String, code: Int) = PendingIntent.getService(this, code,
            Intent(this, AlarmRingService::class.java).setAction(action),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val drawable = requireNotNull(ContextCompat.getDrawable(this, R.drawable.ic_notification_xike))
        val icon = Bitmap.createBitmap(128, 128, Bitmap.Config.ARGB_8888)
        drawable.setBounds(0, 0, 128, 128)
        drawable.draw(Canvas(icon))
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_xike).setLargeIcon(icon)
            .setContentTitle(state?.let { AlarmRinger.ringingText(it.alarm) } ?: "闹钟")
            .setContentText(state?.let { "${it.alarm.timeText} · ${it.alarm.repeatText}" } ?: "正在准备响铃")
            .setContentIntent(open).setCategory(NotificationCompat.CATEGORY_ALARM)
            .setPriority(NotificationCompat.PRIORITY_HIGH).setOngoing(true).setOnlyAlertOnce(true)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setStyle(NotificationCompat.BigTextStyle().bigText("响铃中 · 可直接点「停止响铃」或「稍后提醒」"))
            .setFullScreenIntent(if (state != null || incomingAlarm) open else null, state != null || incomingAlarm)
            .addAction(0, getString(R.string.btn_alarm_dismiss), action(ACTION_DISMISS, 103))
            .addAction(0, getString(R.string.btn_alarm_snooze), action(ACTION_SNOOZE, 104))
            .build()
    }

    override fun onDestroy() {
        handler.removeCallbacks(ticker)
        output.stop()
        ringer.stopAll()
        current = null
        sendBroadcast(Intent(OverlayService.ACTION_STATE_CHANGED).setPackage(packageName))
        super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val CHANNEL_ID = "timeguard_alarm"
        const val ACTION_FIRE = "com.hzy.timeguard.alarm.FIRE"
        const val ACTION_DISMISS = "com.hzy.timeguard.alarm.DISMISS"
        const val ACTION_SNOOZE = "com.hzy.timeguard.alarm.SNOOZE"
        const val EXTRA_DUE_AT = "dueAt"
        const val EXTRA_IDS = "alarmIds"
        const val NOTIFICATION_ID = 1002
        var current: RingingState? = null
            private set
        val isRinging: Boolean get() = current != null
        fun command(context: Context, action: String) {
            if (isRinging) context.startService(Intent(context, AlarmRingService::class.java).setAction(action))
        }
    }
}

package com.hzy.timeguard

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import com.hzy.timeguard.core.AppConfig
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId

/** System owns the next wake-up; no overlay or permanent polling service is required. */
object AlarmScheduler {
    fun canSchedule(context: Context): Boolean = Build.VERSION.SDK_INT < 31 ||
        (context.getSystemService(Context.ALARM_SERVICE) as AlarmManager).canScheduleExactAlarms()

    fun schedule(context: Context, now: LocalDateTime = LocalDateTime.now()): Boolean {
        val manager = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val book = AppConfig(context).loadAlarms()
        val prefs = context.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE)
        val pendingMillis = prefs.getLong("pendingAlarmAt", 0)
        val pendingIds = prefs.getStringSet("pendingAlarmIds", emptySet()).orEmpty()
        val pendingAt = Instant.ofEpochMilli(pendingMillis).atZone(ZoneId.systemDefault()).toLocalDateTime()
        val signature = "$pendingMillis:${pendingIds.sorted().joinToString(",") }"
        // Opening settings at the firing minute must not cancel an in-flight delivery
        // and replace it with tomorrow's occurrence.
        val awaitingDelivery = pendingMillis > 0 && !now.isBefore(pendingAt) &&
            !now.isAfter(pendingAt.plusSeconds(90)) &&
            prefs.getString("lastAlarmDelivery", "") != signature &&
            book.all.any { it.id in pendingIds && it.nextRingAfter(pendingAt.minusSeconds(1)) == pendingAt }
        val next = if (awaitingDelivery) pendingAt else book.nextRing(now)
        val intent = Intent(context, AlarmRingService::class.java).setAction(AlarmRingService.ACTION_FIRE)
        if (next != null) {
            val ids = book.all.filter { it.nextRingAfter(next.minusSeconds(1)) == next }.map { it.id }
            prefs.edit().putLong("pendingAlarmAt", next.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli())
                .putStringSet("pendingAlarmIds", ids.toSet()).apply()
            intent.putExtra(AlarmRingService.EXTRA_DUE_AT, next.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli())
                .putStringArrayListExtra(AlarmRingService.EXTRA_IDS, ArrayList(ids))
        }
        val operation = PendingIntent.getForegroundService(context, 100, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        manager.cancel(operation)
        if (next == null) {
            prefs.edit().remove("pendingAlarmAt").remove("pendingAlarmIds").apply()
            return true
        }
        if (!canSchedule(context)) return false
        val show = PendingIntent.getActivity(context, 101, Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return try {
            manager.setAlarmClock(AlarmManager.AlarmClockInfo(next.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli(), show), operation)
            true
        } catch (_: SecurityException) { false }
    }
}

/** Restore system registrations after reboot, clock changes, updates, or permission grant. */
class AlarmRescheduleReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) { AlarmScheduler.schedule(context) }
}

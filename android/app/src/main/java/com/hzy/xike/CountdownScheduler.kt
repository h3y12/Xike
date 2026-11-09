package com.hzy.xike

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import com.hzy.xike.core.*
import java.time.LocalDateTime
import java.time.ZoneId

/** Optional countdown sound has a system wake-up even when the overlay is hidden/asleep. */
object CountdownScheduler {
    private fun token(cfg: AppConfig) = cfg.cdSessionId + ":" + cfg.loadCountdown().deadline
    fun schedule(
        context: Context,
        now: LocalDateTime = LocalDateTime.now(),
        recoveringAfterUpdate: Boolean = false,
    ): Boolean {
        val cfg = AppConfig(context)
        val snapshot = cfg.loadCountdown()
        val due = runCatching { LocalDateTime.parse(snapshot.deadline) }.getOrNull()?.takeIf { it.year in 1..9999 }
        val prefs = context.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE)
        val wanted = cfg.cdRingEnabled && cfg.cdSourceAlarmId.isEmpty() && due != null && cfg.cdSessionId.isNotEmpty()
        val awaitingDelivery = wanted && due != null &&
            snapshot.phase in listOf(CdPhase.Running.name, CdPhase.Finished.name) &&
            !now.isBefore(due) && !now.isAfter(due.plusSeconds(90)) &&
            prefs.getString("lastCountdownDelivery", "") != token(cfg)
        // Normal refreshes preserve the queued delivery. An app update can remove its old operation.
        if (awaitingDelivery && !recoveringAfterUpdate) return true
        val manager = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val intent = Intent(context, AlarmRingService::class.java).setAction(AlarmRingService.ACTION_COUNTDOWN)
            .putExtra(EXTRA_TOKEN, token(cfg))
        val operation = PendingIntent.getForegroundService(context, 201, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        manager.cancel(operation)
        if (!wanted || due == null) return true
        if (!awaitingDelivery && (snapshot.phase != CdPhase.Running.name || !due.isAfter(now))) return true
        if (!AlarmScheduler.canSchedule(context)) return false
        val open = PendingIntent.getActivity(context, 202, Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return try {
            manager.setAlarmClock(AlarmManager.AlarmClockInfo(due.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli(), open), operation)
            true
        } catch (_: SecurityException) { false }
    }

    internal fun consume(context: Context, intent: Intent, now: LocalDateTime): AlarmFiring? {
        val cfg = AppConfig(context)
        val s = cfg.loadCountdown()
        val due = runCatching { LocalDateTime.parse(s.deadline) }.getOrNull()?.takeIf { it.year in 1..9999 } ?: return null
        val key = token(cfg)
        if (!cfg.cdRingEnabled || cfg.cdSourceAlarmId.isNotEmpty() || cfg.cdSessionId.isEmpty() ||
            intent.getStringExtra(EXTRA_TOKEN) != key || s.phase !in listOf(CdPhase.Running.name, CdPhase.Finished.name) ||
            now.isBefore(due) || now.isAfter(due.plusSeconds(90))) return null
        val prefs = context.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE)
        if (prefs.getString("lastCountdownDelivery", "") == key) return null
        if (!prefs.edit().putString("lastCountdownDelivery", key).commit()) return null
        cfg.saveCountdown(s.copy(phase = CdPhase.Finished.name))
        schedule(context, now)
        return AlarmFiring(Alarm("countdown:$key", "倒计时结束", due.toLocalTime(), chainId = "countdown"), due, true)
    }
    const val EXTRA_TOKEN = "countdownToken"
}

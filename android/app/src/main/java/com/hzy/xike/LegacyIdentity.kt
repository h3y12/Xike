package com.hzy.xike

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent

/** Persisted identifiers from released versions. Keep these stable for in-place upgrades. */
object LegacyIdentity {
    const val PREFERENCES = "timeguard"
    const val OVERLAY_CHANNEL = "timeguard_overlay"
    const val ALARM_CHANNEL = "timeguard_alarm"

    /** Replace obsolete component registrations after the source namespace changes. */
    fun cancelObsoleteAlarms(context: Context) {
        val manager = context.getSystemService(AlarmManager::class.java)
        for ((request, action) in listOf(100 to "FIRE", 201 to "COUNTDOWN")) {
            val intent = Intent().setClassName(context.packageName, context.packageName + ".AlarmRingService")
                .setAction(context.packageName + ".alarm." + action)
            val pending = PendingIntent.getForegroundService(context, request, intent,
                PendingIntent.FLAG_NO_CREATE or PendingIntent.FLAG_IMMUTABLE)
            if (pending != null) {
                manager.cancel(pending)
                pending.cancel()
            }
        }
    }
}

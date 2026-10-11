package com.hzy.timeguard

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Bundle
import android.view.Gravity
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.hzy.timeguard.core.AlarmRinger

/** A ringing alarm has its own lock-screen controls, separate from settings and overlay. */
class AlarmRingActivity : AppCompatActivity() {
    private lateinit var title: TextView
    private lateinit var time: TextView
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) { refresh() }
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (android.os.Build.VERSION.SDK_INT >= 27) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        } else {
            @Suppress("DEPRECATION")
            window.addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(48, 48, 48, 48)
            setBackgroundColor(ContextCompat.getColor(context, R.color.card_bg))
        }
        title = TextView(this).apply {
            textSize = 24f
            gravity = Gravity.CENTER
            setTextColor(ContextCompat.getColor(context, R.color.text_primary))
        }
        time = TextView(this).apply {
            textSize = 56f
            gravity = Gravity.CENTER
            setTextColor(ContextCompat.getColor(context, R.color.text_primary))
        }
        layout.addView(title)
        layout.addView(time)
        layout.addView(Button(this).apply {
            id = R.id.btnAlarmDismiss
            setText(R.string.btn_alarm_dismiss)
            setOnClickListener { AlarmRingService.command(this@AlarmRingActivity, AlarmRingService.ACTION_DISMISS) }
        }, LinearLayout.LayoutParams(-1, -2))
        layout.addView(Button(this).apply {
            id = R.id.btnAlarmSnooze
            setText(R.string.btn_alarm_snooze)
            setOnClickListener { AlarmRingService.command(this@AlarmRingActivity, AlarmRingService.ACTION_SNOOZE) }
        }, LinearLayout.LayoutParams(-1, -2))
        setContentView(layout)
        refresh()
    }
    override fun onStart() {
        super.onStart()
        ContextCompat.registerReceiver(this, receiver, IntentFilter(OverlayService.ACTION_STATE_CHANGED), ContextCompat.RECEIVER_NOT_EXPORTED)
        refresh()
    }
    override fun onStop() {
        unregisterReceiver(receiver)
        super.onStop()
    }
    private fun refresh() {
        val state = AlarmRingService.current
        if (state == null) { finish(); return }
        title.text = AlarmRinger.ringingText(state.alarm)
        time.text = state.alarm.timeText
    }
}

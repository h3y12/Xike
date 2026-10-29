package com.hzy.timeguard

import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.graphics.drawable.GradientDrawable
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.content.ContextCompat
import com.hzy.timeguard.core.AlarmRinger
import com.hzy.timeguard.core.RingingState

/** A small, actionable alarm window when the system declines to open the full activity. */
internal class AlarmRingOverlay(private val context: Context) {
    private val wm = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    var view: View? = null
        private set
    private var title: TextView? = null
    private var snooze: Button? = null
    fun sync(state: RingingState?, screenVisible: Boolean) {
        if (state == null || screenVisible || !Settings.canDrawOverlays(context)) { dismiss(); return }
        if (view == null) {
            val pad = (20 * context.resources.displayMetrics.density).toInt()
            val layout = LinearLayout(context).apply {
                tag = "alarm-ring-overlay"
                orientation = LinearLayout.VERTICAL
                setPadding(pad, pad, pad, pad)
                background = GradientDrawable().apply {
                    setColor(ContextCompat.getColor(context, R.color.card_bg))
                    cornerRadius = pad.toFloat()
                    setStroke(2, ContextCompat.getColor(context, R.color.accent))
                }
                elevation = pad.toFloat()
            }
            title = TextView(context).apply { textSize = 22f; setTextColor(ContextCompat.getColor(context, R.color.text_primary)) }
            layout.addView(title)
            layout.addView(Button(context).apply {
                id = R.id.btnAlarmDismiss
                setText(R.string.btn_alarm_dismiss)
                setOnClickListener { AlarmRingService.command(context, AlarmRingService.ACTION_DISMISS) }
            })
            snooze = Button(context).apply {
                id = R.id.btnAlarmSnooze
                setText(R.string.btn_alarm_snooze)
                setOnClickListener { AlarmRingService.command(context, AlarmRingService.ACTION_SNOOZE) }
            }
            layout.addView(snooze)
            layout.addView(Button(context).apply {
                setText(R.string.open_ring_page)
                setOnClickListener {
                    runCatching { context.startActivity(Intent(context, AlarmRingActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)) }
                }
            })
            val params = WindowManager.LayoutParams(
                minOf((360 * context.resources.displayMetrics.density).toInt(), context.resources.displayMetrics.widthPixels - 2 * pad).coerceAtLeast(pad),
                WindowManager.LayoutParams.WRAP_CONTENT, WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, PixelFormat.TRANSLUCENT).apply { gravity = Gravity.CENTER }
            try { wm.addView(layout, params); view = layout }
            catch (_: Exception) { title = null; snooze = null; return }
        }
        title?.text = "${AlarmRinger.ringingText(state.alarm)}\n${state.alarm.timeText}"
        snooze?.visibility = if (state.alarm.canSnooze) View.VISIBLE else View.GONE
    }
    fun dismiss() {
        view?.let { runCatching { wm.removeView(it) } }
        view = null
        title = null
        snooze = null
    }
}

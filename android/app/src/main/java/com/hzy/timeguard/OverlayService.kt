package com.hzy.timeguard

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.content.pm.ServiceInfo
import android.graphics.PixelFormat
import android.graphics.Bitmap
import android.graphics.Canvas
import android.hardware.input.InputManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.view.WindowInsets
import com.hzy.timeguard.core.OverlayPosition
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.hzy.timeguard.core.Alarm
import com.hzy.timeguard.core.AppConfig
import com.hzy.timeguard.core.CdParse
import com.hzy.timeguard.core.CdPhase
import com.hzy.timeguard.core.CdState
import com.hzy.timeguard.core.ManualCountdown
import com.hzy.timeguard.core.NightPhase
import com.hzy.timeguard.core.ScoreEngine
import com.hzy.timeguard.core.ScoreState
import com.hzy.timeguard.core.ScoreVisual
import com.hzy.timeguard.core.SleepRules
import com.hzy.timeguard.databinding.OverlayScoreBinding
import java.time.LocalDateTime

/**
 * 悬浮窗前台服务。
 *
 * 悬浮窗上下两块，与桌面版一一对应：
 *  ① **早睡健康分**：纯时钟口径，每 [SleepRules.minutesPerPoint] 分钟扣 [SleepRules.pointsPerStep] 分；
 *  ② **手动倒计时**：用户自己设的计时，与分数无关。
 *
 * 可见性也照抄桌面版：分数块白天（06:30–20:30）自动收起；倒计时块夜里总在，
 * 白天只在「计时中」才冒出来；两块都不可见就把整个悬浮窗摘掉，免得留个看不见的窗口挡触摸。
 *
 * 为什么必须是前台服务：Android 8.0 起，进程要在后台长期存活就必须挂一个前台通知，
 * 否则系统随时会杀掉它，悬浮窗也就跟着没了。所以通知是躲不掉的，
 * 只能把它做得低调（低优先级、静音），并让「通知栏里是否带穿透按钮」成为可选项。
 */
class OverlayService : Service() {

    private lateinit var wm: WindowManager
    private lateinit var cfg: AppConfig
    private val handler = Handler(Looper.getMainLooper())

    // Supply our own color icon rather than relying on an OEM's cached app badge.
    private val notificationIcon: Bitmap by lazy {
        val drawable = requireNotNull(ContextCompat.getDrawable(this, R.drawable.ic_notification_xike))
        Bitmap.createBitmap(128, 128, Bitmap.Config.ARGB_8888).also { bitmap ->
            drawable.setBounds(0, 0, bitmap.width, bitmap.height)
            drawable.draw(Canvas(bitmap))
        }
    }

    private var binding: OverlayScoreBinding? = null
    private var lp: WindowManager.LayoutParams? = null

    /** 手动倒计时状态机。以服务为唯一拥有者，App 只发命令。 */
    private var cd = ManualCountdown()

    /** 上一次落盘时的倒计时阶段，用来只在阶段变化时写盘（剩余时间每秒都在变）。 */
    private var lastCdPhase: CdPhase? = null

    private var touchDownX = 0f
    private var touchDownY = 0f
    private var touchStartLpX = 0
    private var touchStartLpY = 0
    private var dragging = false

    private var lastNotifSignature = ""

    /** 是否已经进过前台。第一条命令才需要挂「正在启动…」占位通知，之后不能反复覆盖。 */
    private var isForeground = false

    /** Read-only summaries; alarm execution belongs to AlarmRingService. */
    private val alarms get() = cfg.loadAlarms()

    private val ticker = object : Runnable {
        override fun run() {
            tick()
            handler.postDelayed(this, TICK_INTERVAL_MS)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        cfg = AppConfig(this)

        // 进程被杀后重启：把倒计时按绝对截止时刻接着算，不从头开始
        cd = ManualCountdown().also {
            it.inputText = cfg.cdInputText
            it.restore(cfg.loadCountdown(), LocalDateTime.now())
        }
        lastCdPhase = cd.phase


        isRunning = true
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // 先把前台通知挂上，避免超时被系统判定为 ANR 式崩溃。
        //
        // ⚠️ 只在**第一次**（还没进过前台）时用「正在启动…」占位。之前是无条件调用，
        // 于是每收到一条命令（切换穿透、开始/暂停倒计时、刷新外观）都会把通知**打回**
        // 启动文案；而 updateNotification 的去重缓存认为「正文没变」→ 永远不再刷新，
        // 通知就永久卡在「正在启动…」上。
        if (!isForeground) {
            startForegroundWithNotification()
            isForeground = true
        }

        when (intent?.action) {
            ACTION_STOP -> {
                cfg.wantRunning = false
                stopSelf()
                return START_NOT_STICKY
            }

            ACTION_TOGGLE_THROUGH, ACTION_SET_THROUGH -> {
                val target = if (intent.action == ACTION_SET_THROUGH) {
                    intent.getBooleanExtra(EXTRA_THROUGH, false)
                } else {
                    !cfg.clickThrough
                }
                cfg.clickThrough = target
                applyThrough()
                broadcastStateChanged()
            }

            ACTION_CD_START -> {
                val text = intent.getStringExtra(EXTRA_CD_TEXT) ?: cfg.cdInputText
                cfg.cdInputText = text
                when (val r = ManualCountdown.tryParse(text, LocalDateTime.now())) {
                    is CdParse.Ok -> {
                        cd.start(r.plan, LocalDateTime.now())
                        persistCd()
                        broadcastCdResult(null)
                    }

                    is CdParse.Err -> broadcastCdResult(r.message)
                }
            }

            ACTION_CD_TOGGLE_PAUSE -> {
                val now = LocalDateTime.now()
                if (cd.phase == CdPhase.Paused) cd.resume(now) else cd.pause(now)
                persistCd()
            }

            ACTION_CD_RESET -> {
                cd.reset()
                persistCd()
            }

            ACTION_REFRESH -> {
                // 透明度 / 显示项 / 通知按钮这类外观设置改了，立刻套用，不必等下一秒的 tick
                applyAppearance()
            }

        }

        cfg.wantRunning = true
        applyAppearance()

        handler.removeCallbacks(ticker)
        handler.post(ticker)

        return START_STICKY
    }

    override fun onDestroy() {
        handler.removeCallbacks(ticker)

        cfg.saveCountdown(cd.snapshot())

        detachOverlay()
        isRunning = false
        broadcastStateChanged()
        super.onDestroy()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        dragging = false
        val b = binding ?: return
        val p = lp ?: return
        // Recheck after rotation even when the card's own dimensions do not change.
        b.root.post { keepOverlayOnScreen(b, p) }
    }

    // ---------------------------------------------------------------- 每秒结算

    private fun tick() {
        val rules = cfg.rules()
        val now = LocalDateTime.now()

        val state = ScoreEngine.evaluate(now, rules)
        val cdState = cd.tick(now)

        // 只在阶段变化时写盘（剩余时间每秒都在变，没必要跟着写）
        if (cdState.phase != lastCdPhase) persistCd()


        render(state, rules, cdState)
        updateNotification(state, cdState, rules)
    }

    private fun persistCd() {
        lastCdPhase = cd.phase
        cfg.saveCountdown(cd.snapshot())
        broadcastStateChanged()
    }

    private fun render(state: ScoreState, rules: SleepRules, cdState: CdState) {
        val daytime = state.phase == NightPhase.Daytime

        // ---- 可见性：照抄桌面版 MainWindow 的两行判断 ----
        // 分数块：时段（06:30–20:30 自动收起）与手动收起二者取交集
        val scoreShow = cfg.showScore && !daytime

        // Only a running or paused countdown with time remaining occupies the overlay.
        val cdShow = cfg.showCountdown && cdState.hasActiveTime

        if (!scoreShow && !cdShow) {
            detachOverlay()
            return
        }

        val b = ensureOverlay() ?: return

        // ---- 早睡健康分 ----
        b.scoreBlock.visibility = if (scoreShow) View.VISIBLE else View.GONE
        if (scoreShow) {
            val score = state.score
            val status = if (state.phase == NightPhase.Countdown) rules.statusBefore
                else ScoreVisual.statusForScore(score, rules)

            b.tvScore.text = score.toString()
            b.tvScore.setTextColor(ScoreVisual.colorForScore(score))

            b.tvStatus.visibility = if (status.isEmpty()) View.GONE else View.VISIBLE
            b.tvStatus.text = status

        }

        // ---- 手动倒计时 ----
        b.cdBlock.visibility = if (cdShow) View.VISIBLE else View.GONE
        b.cdDivider.visibility = if (scoreShow && cdShow) View.VISIBLE else View.GONE
        if (cdShow) {
            b.tvCdTime.text = ManualCountdown.bigText(cdState.phase, cdState.remaining)
            b.tvCdTime.setTextColor(ManualCountdown.colorFor(cdState.phase, cdState.remaining))
            b.tvCdLabel.text = cd.subLabel(multiline = true)
        }
    }

    // ---------------------------------------------------------------- 悬浮窗

    private fun ensureOverlay(): OverlayScoreBinding? {
        binding?.let { return it }

        if (!canDrawOverlays()) {
            // 没有悬浮窗权限时不要炸掉服务，安静地把通知留着即可
            return null
        }

        val b = OverlayScoreBinding.inflate(LayoutInflater.from(this))

        val params = buildLayoutParams()
        params.x = cfg.overlayX
        params.y = cfg.overlayY

        // 穿透状态下淡一层，提醒「现在点不到我」；和 applyThrough 保持一致
        b.root.alpha = if (cfg.clickThrough) THROUGH_ALPHA else 1.0f
        b.root.setOnTouchListener { v, e -> onOverlayTouch(v, e) }
        b.root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ ->
            keepOverlayOnScreen(b, params)
        }

        try {
            wm.addView(b.root, params)
        } catch (_: Throwable) {
            return null
        }

        binding = b
        lp = params

        // Recover old off-screen coordinates as well as first-launch placement.
        b.root.post { keepOverlayOnScreen(b, params) }
        return b
    }

    private fun buildLayoutParams(): WindowManager.LayoutParams {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        } else {
            @Suppress("DEPRECATION")
            WindowManager.LayoutParams.TYPE_PHONE
        }

        val p = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            type,
            baseFlags(),
            PixelFormat.TRANSLUCENT,
        )
        p.gravity = Gravity.TOP or Gravity.START
        p.alpha = windowOpacity()
        return p
    }

    private fun baseFlags(): Int {
        var f = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
            WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
        if (cfg.clickThrough) f = f or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        return f
    }

    private fun windowOpacity(): Float {
        val maximum = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            getSystemService(InputManager::class.java).maximumObscuringOpacityForTouch
        } else 1.0f
        return com.hzy.timeguard.core.OverlayOpacity.effective(
            cfg.opacity, cfg.clickThrough, maximum,
        )
    }

    /** 透明度、穿透、显示项发生变化后调用。 */
    private fun applyAppearance() {
        val b = binding ?: return
        val p = lp ?: return
        p.alpha = windowOpacity()
        p.flags = baseFlags()
        b.root.alpha = if (cfg.clickThrough) THROUGH_ALPHA else 1.0f
        try {
            wm.updateViewLayout(b.root, p)
        } catch (_: Throwable) {
        }
    }

    private fun applyThrough() {
        applyAppearance()
        val b = binding ?: return
        val p = lp ?: return
        // 穿透状态下把外观调淡一点，提醒「现在点不到我」
        b.root.alpha = if (cfg.clickThrough) THROUGH_ALPHA else 1.0f
        try {
            wm.updateViewLayout(b.root, p)
        } catch (_: Throwable) {
        }
    }

    private fun detachOverlay() {
        val b = binding ?: return
        try {
            wm.removeView(b.root)
        } catch (_: Throwable) {
        }
        binding = null
        lp = null
    }

    private fun onOverlayTouch(v: View, e: MotionEvent): Boolean {
        val p = lp ?: return false

        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                touchDownX = e.rawX
                touchDownY = e.rawY
                touchStartLpX = p.x
                touchStartLpY = p.y
                dragging = false
                return true
            }

            MotionEvent.ACTION_MOVE -> {
                val dx = (e.rawX - touchDownX).toInt()
                val dy = (e.rawY - touchDownY).toInt()
                if (!dragging && (kotlin.math.abs(dx) > TOUCH_SLOP || kotlin.math.abs(dy) > TOUCH_SLOP)) {
                    dragging = true
                }
                if (dragging) {
                    p.x = touchStartLpX + dx
                    p.y = touchStartLpY + dy
                    binding?.let { keepOverlayOnScreen(it, p, updateLayout = false) }
                    try {
                        wm.updateViewLayout(v, p)
                    } catch (_: Throwable) {
                    }
                }
                return true
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (dragging) {
                    cfg.overlayX = p.x
                    cfg.overlayY = p.y
                } else {
                    // 轻点一下 = 打开 App（和点通知一个效果）
                    val i = Intent(this, MainActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
                    try {
                        startActivity(i)
                    } catch (_: Throwable) {
                    }
                }
                dragging = false
                return true
            }
        }
        return false
    }

    private fun keepOverlayOnScreen(
        b: OverlayScoreBinding,
        p: WindowManager.LayoutParams,
        updateLayout: Boolean = true,
    ) {
        if (binding !== b || lp !== p) return
        val w = if (b.root.width > 0) b.root.width else b.root.measuredWidth
        val h = if (b.root.height > 0) b.root.height else b.root.measuredHeight
        if (w <= 0 || h <= 0) return

        val screen = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val metrics = wm.currentWindowMetrics
            val insets = metrics.windowInsets.getInsetsIgnoringVisibility(
                WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout(),
            )
            android.graphics.Rect(
                insets.left, insets.top,
                metrics.bounds.width() - insets.right,
                metrics.bounds.height() - insets.bottom,
            )
        } else {
            @Suppress("DEPRECATION")
            val metrics = android.util.DisplayMetrics().also { wm.defaultDisplay.getMetrics(it) }
            android.graphics.Rect(0, 0, metrics.widthPixels, metrics.heightPixels)
        }
        val position = OverlayPosition.fit(
            p.x, p.y, w, h, screen.left, screen.top, screen.right, screen.bottom, dp(8),
        )
        val changed = p.x != position.x || p.y != position.y
        p.x = position.x
        p.y = position.y

        if (!dragging) {
            if (cfg.overlayX != p.x) cfg.overlayX = p.x
            if (cfg.overlayY != p.y) cfg.overlayY = p.y
        }
        if (changed && updateLayout) {
            try {
                wm.updateViewLayout(b.root, p)
            } catch (_: Throwable) {
            }
        }
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()

    private fun canDrawOverlays(): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) android.provider.Settings.canDrawOverlays(this)
        else true

    // ---------------------------------------------------------------- 通知

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (nm.getNotificationChannel(CHANNEL_ID) != null) return

        val ch = NotificationChannel(CHANNEL_ID, "健康分悬浮窗", NotificationManager.IMPORTANCE_LOW)
        ch.description = "保持悬浮窗运行；可在此切换穿透或关闭悬浮窗"
        ch.setShowBadge(false)
        ch.enableLights(false)
        ch.enableVibration(false)
        nm.createNotificationChannel(ch)
    }

    private fun startForegroundWithNotification() {
        val n = buildNotification("正在启动…")
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        } else {
            0
        }
        ServiceCompat.startForeground(this, NOTIFICATION_ID, n, type)
    }

    private fun updateNotification(state: ScoreState, cdState: CdState, rules: SleepRules) {
        val base = when (state.phase) {
            NightPhase.Daytime -> "白天休息中（${SleepRules.formatHm(rules.showAt)} 再见）"
            NightPhase.Countdown -> "满分 ${state.score} · 距扣分 ${ScoreEngine.formatShort(state.untilDeadline)}"
            NightPhase.Exhausted -> "已扣光 · 早点睡"
            else -> "${state.score} 分 · ${ScoreVisual.statusForScore(state.score, rules)}"
        }

        // 倒计时在跑时，通知栏也带一眼剩余时间
        val withCd = if (cdState.phase == CdPhase.Running) {
            "$base · 倒计时 ${ManualCountdown.hms(cdState.remaining)}"
        } else {
            base
        }

        // 半小时内会有闹钟响时，把最近那个写进通知栏（用户要求的「临近半小时加入通知栏」）。
        // 不显示全部：通知只有一行，写最近的那个最有信息量。
        val imminent = alarms.nextImminentRing(LocalDateTime.now())
        val text = if (imminent != null) {
            "$withCd · 闹钟 ${Alarm.describeCountdown(LocalDateTime.now(), imminent)}"
        } else {
            withCd
        }

        // 去重签名必须包含**正文以外的可变状态**：通知上的动作按钮文案取决于
        // notifyToggle / clickThrough，只比正文的话，用户切换穿透后按钮文字不会更新。
        val signature = text + "|" + cfg.notifyToggle + "|" + cfg.clickThrough

        // 只在内容真的变化时重建通知，避免每秒刷一次引起闪烁
        if (signature == lastNotifSignature) return

        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        try {
            nm.notify(NOTIFICATION_ID, buildNotification(text))
            // 提交成功后才记账。之前是先记账再提交，一旦 notify 抛异常，
            // 这一版内容就再也不会被重试（缓存说「已发过」，实际没发出去）。
            lastNotifSignature = signature
        } catch (_: Throwable) {
        }
    }

    // ---------------------------------------------------------------- 闹钟的通知与震动

    private fun buildNotification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_xike)
            .setLargeIcon(notificationIcon)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setShowWhen(false)

        // 通知栏里的穿透切换按钮：默认关闭，保持通知清爽
        if (cfg.notifyToggle) {
            val toggle = PendingIntent.getService(
                this, 1,
                Intent(this, OverlayService::class.java).setAction(ACTION_TOGGLE_THROUGH),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            builder.addAction(
                0,
                if (cfg.clickThrough) "关闭穿透" else "开启穿透",
                toggle,
            )
        }

        val stop = PendingIntent.getService(
            this, 2,
            Intent(this, OverlayService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        builder.addAction(0, "关闭悬浮窗", stop)

        return builder.build()
    }

    private fun broadcastStateChanged() {
        // 带上闹钟摘要：App 那边的界面直接用这几行字，不必自己再算一遍
        // 这里只显示摘要，独立闹钟服务负责执行。
        val now = LocalDateTime.now()
        val imminent = alarms.imminent(now)
        val summary = when {
            AlarmRingService.isRinging -> "正在响：${AlarmRingService.current?.alarm?.timeText ?: ""}"
            imminent.isEmpty() -> "半小时内没有闹钟"
            else -> "半小时内：" + imminent.joinToString("、") { it.timeText }
        }

        sendBroadcast(
            Intent(ACTION_STATE_CHANGED)
                .setPackage(packageName)
                .putExtra("alarmText", summary),
        )
    }

    /** 把解析失败的原因捎给 App，由它弹提示；成功则传 null。 */
    private fun broadcastCdResult(error: String?) {
        sendBroadcast(
            Intent(ACTION_STATE_CHANGED).setPackage(packageName).putExtra(EXTRA_CD_ERROR, error),
        )
    }

    // ---------------------------------------------------------------- 对外入口

    companion object {
        const val ACTION_START = "com.hzy.timeguard.action.START"
        const val ACTION_STOP = "com.hzy.timeguard.action.STOP"
        const val ACTION_TOGGLE_THROUGH = "com.hzy.timeguard.action.TOGGLE_THROUGH"
        const val ACTION_SET_THROUGH = "com.hzy.timeguard.action.SET_THROUGH"
        const val ACTION_REFRESH = "com.hzy.timeguard.action.REFRESH"
        const val ACTION_STATE_CHANGED = "com.hzy.timeguard.action.STATE_CHANGED"

        const val ACTION_CD_START = "com.hzy.timeguard.action.CD_START"
        const val ACTION_CD_TOGGLE_PAUSE = "com.hzy.timeguard.action.CD_TOGGLE_PAUSE"
        const val ACTION_CD_RESET = "com.hzy.timeguard.action.CD_RESET"

        const val EXTRA_THROUGH = "through"
        const val EXTRA_CD_TEXT = "cdText"
        const val EXTRA_CD_ERROR = "cdError"

        const val CHANNEL_ID = "timeguard_overlay"
        private const val NOTIFICATION_ID = 1001
        private const val TICK_INTERVAL_MS = 1_000L
        private const val TOUCH_SLOP = 8

        /** 穿透状态下悬浮窗淡一层，提示「现在点不到我」。 */
        private const val THROUGH_ALPHA = 0.72f

        /** 服务是否在跑。Activity 用它决定按钮文案。 */
        @Volatile
        var isRunning: Boolean = false
            internal set
    }
}

/** 便捷入口，避免调用方到处判断版本。 */
fun Context.startTimeGuardOverlay() {
    val i = Intent(this, OverlayService::class.java).setAction(OverlayService.ACTION_START)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(i) else startService(i)
}

fun Context.stopTimeGuardOverlay() {
    val i = Intent(this, OverlayService::class.java).setAction(OverlayService.ACTION_STOP)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(i) else startService(i)
}

fun Context.setTimeGuardThrough(through: Boolean) {
    val i = Intent(this, OverlayService::class.java)
        .setAction(OverlayService.ACTION_SET_THROUGH)
        .putExtra(OverlayService.EXTRA_THROUGH, through)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(i) else startService(i)
}

/** 让服务开始一次手动倒计时；文本解析失败时服务会广播回来原因。 */
fun Context.startTimeGuardCountdown(text: String) {
    val i = Intent(this, OverlayService::class.java)
        .setAction(OverlayService.ACTION_CD_START)
        .putExtra(OverlayService.EXTRA_CD_TEXT, text)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(i) else startService(i)
}

fun Context.toggleTimeGuardCountdownPause() {
    val i = Intent(this, OverlayService::class.java).setAction(OverlayService.ACTION_CD_TOGGLE_PAUSE)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(i) else startService(i)
}

fun Context.resetTimeGuardCountdown() {
    val i = Intent(this, OverlayService::class.java).setAction(OverlayService.ACTION_CD_RESET)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(i) else startService(i)
}

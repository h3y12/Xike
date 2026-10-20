package com.hzy.timeguard

import android.Manifest
import android.app.TimePickerDialog
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.media.AudioManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.hzy.timeguard.core.AppConfig
import com.hzy.timeguard.core.CdPhase
import com.hzy.timeguard.core.ManualCountdown
import com.hzy.timeguard.core.NightPhase
import com.hzy.timeguard.core.ScoreEngine
import com.hzy.timeguard.core.ScoreVisual
import com.hzy.timeguard.core.SleepRules
import com.hzy.timeguard.databinding.ActivityMainBinding
import java.time.LocalDateTime
import java.time.LocalTime

/**
 * 设置主界面。两块与桌面版对应：**早睡健康分**与**手动倒计时**。
 *
 * 这里同时承担一个桌面版没有的职责：**切回穿透**。
 * 悬浮窗一旦开了穿透，它自己就收不到任何触摸了，必须由 App 提供一个开关把它关掉。
 * （通知栏也能切，但那是可选功能，默认不开——见 [AppConfig.notifyToggle]。）
 *
 * 倒计时的状态机住在 [OverlayService] 里，这里只发命令、只读结果，
 * 不自己持有第二份状态——否则 App 与悬浮窗会各算各的，迟早对不上。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var b: ActivityMainBinding
    private lateinit var cfg: AppConfig

    private val handler = Handler(Looper.getMainLooper())

    /** 载入配置到控件时置位，避免「填值」被当成「用户改动」而回写。 */
    private var suppress = false

    /** 只用于**回放**倒计时状态的临时副本（每次从配置还原，不参与写）。 */
    private var cdView = ManualCountdown()

    /** 闹钟那张卡的全部逻辑在这个类里（列表、增删改、连锁起床闹钟）。 */
    private lateinit var alarmCard: AlarmCard

    private val ticker = object : Runnable {
        override fun run() {
            updatePreview()
            refreshCdPreview()
            alarmCard.render()
            handler.postDelayed(this, 1_000L)
        }
    }

    private val stateReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            b.swThrough.isChecked = cfg.clickThrough
            updateOverlayButton()

            // 服务解析输入失败时会把原因捎回来，直接弹给用户
            intent?.getStringExtra(OverlayService.EXTRA_CD_ERROR)?.let { toast(it) }

            refreshCdPreview()
            alarmCard.render()
            updatePermissionUi()
        }
    }

    private val notifPermLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) {
            updatePermissionUi()
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        volumeControlStream = AudioManager.STREAM_ALARM
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        setSupportActionBar(b.toolbar)
        @Suppress("DEPRECATION")
        val packageInfo = packageManager.getPackageInfo(packageName, 0)
        val versionCode = androidx.core.content.pm.PackageInfoCompat.getLongVersionCode(packageInfo)
        b.tvAppVersion.text = getString(R.string.app_version, packageInfo.versionName.orEmpty(), versionCode)

        cfg = AppConfig(this)

        alarmCard = AlarmCard(this, b, cfg) { updatePermissionUi() }
        alarmCard.attach()

        bindEvents()
        loadToUi()
    }

    override fun onResume() {
        super.onResume()
        ContextCompat.registerReceiver(
            this,
            stateReceiver,
            IntentFilter(OverlayService.ACTION_STATE_CHANGED),
            ContextCompat.RECEIVER_NOT_EXPORTED,
        )
        AlarmScheduler.schedule(this)
        updatePermissionUi()
        updateOverlayButton()
        handler.removeCallbacks(ticker)
        handler.post(ticker)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(ticker)
        try {
            unregisterReceiver(stateReceiver)
        } catch (_: Throwable) {
        }
    }

    // ---------------------------------------------------------------- 事件绑定

    private fun bindEvents() {
        b.btnShowAt.setOnClickListener {
            pickTime(SleepRules.parseHm(b.btnShowAt.text.toString(), SleepRules.Default.showAt)) { t ->
                b.btnShowAt.text = SleepRules.formatHm(t)
                persistRules()
            }
        }
        b.btnDeductAt.setOnClickListener {
            pickTime(SleepRules.parseHm(b.btnDeductAt.text.toString(), SleepRules.Default.deductAt)) { t ->
                b.btnDeductAt.text = SleepRules.formatHm(t)
                persistRules()
            }
        }
        b.btnHideAt.setOnClickListener {
            pickTime(SleepRules.parseHm(b.btnHideAt.text.toString(), SleepRules.Default.hideAt)) { t ->
                b.btnHideAt.text = SleepRules.formatHm(t)
                persistRules()
            }
        }

        // 数值与文案：输入变化即保存，不做「确认」按钮，省得用户忘了点
        for (et in listOf(b.etMinutes, b.etPoints)) {
            et.addTextChangedListener(SimpleWatcher { persistRules() })
        }
        for (et in listOf(b.etStatusBefore, b.etStatusHigh, b.etStatusMid, b.etStatusLow)) {
            et.addTextChangedListener(SimpleWatcher { persistRules() })
        }

        b.sbOpacity.setOnSeekBarChangeListener(object : android.widget.SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: android.widget.SeekBar?, progress: Int, fromUser: Boolean) {
                b.tvOpacityValue.text = "$progress%"
                if (suppress) return
                cfg.opacity = progress / 100f
                notifyServiceAppearance()
            }

            override fun onStartTrackingTouch(sb: android.widget.SeekBar?) = Unit
            override fun onStopTrackingTouch(sb: android.widget.SeekBar?) = Unit
        })

        b.swThrough.setOnCheckedChangeListener { _, checked ->
            if (suppress) return@setOnCheckedChangeListener
            cfg.clickThrough = checked
            if (OverlayService.isRunning) {
                setTimeGuardThrough(checked)
            } else if (checked) {
                toast("开启悬浮窗后穿透才会生效")
            }
        }

        b.swShowScore.setOnCheckedChangeListener { _, checked ->
            if (suppress) return@setOnCheckedChangeListener
            cfg.showScore = checked
            notifyServiceAppearance()
        }

        b.swShowCountdown.setOnCheckedChangeListener { _, checked ->
            if (suppress) return@setOnCheckedChangeListener
            cfg.showCountdown = checked
            notifyServiceAppearance()
        }

        b.swNotifyToggle.setOnCheckedChangeListener { _, checked ->
            if (suppress) return@setOnCheckedChangeListener
            cfg.notifyToggle = checked
            notifyServiceAppearance()
        }

        b.etCdInput.addTextChangedListener(SimpleWatcher { cfg.cdInputText = b.etCdInput.text.toString() })

        // ---- 手动倒计时三颗按钮：状态机在服务里，这里只发命令 ----
        b.btnCdStart.setOnClickListener {
            val text = b.etCdInput.text.toString()
            cfg.cdInputText = text
            if (!ensureServiceForCd()) return@setOnClickListener
            // 服务刚被拉起时 onStartCommand 要晚一步才跑，延迟发命令免得命令先到、服务还没建好
            b.root.postDelayed({ startTimeGuardCountdown(text) }, 350L)
        }

        b.btnCdToggle.setOnClickListener {
            if (!ensureServiceForCd()) return@setOnClickListener
            b.root.postDelayed({ toggleTimeGuardCountdownPause() }, 120L)
        }

        b.btnCdReset.setOnClickListener {
            if (!ensureServiceForCd()) return@setOnClickListener
            b.root.postDelayed({ resetTimeGuardCountdown() }, 120L)
        }

        b.btnToggleOverlay.setOnClickListener {
            if (OverlayService.isRunning) {
                stopTimeGuardOverlay()
                b.root.postDelayed({ updateOverlayButton() }, 400L)
            } else {
                if (!Settings.canDrawOverlays(this)) {
                    toast(getString(R.string.toast_overlay_permission_needed))
                    requestOverlayPermission()
                    return@setOnClickListener
                }
                askNotificationPermissionIfNeeded()
                startTimeGuardOverlay()
                b.root.postDelayed({ updateOverlayButton() }, 600L)
            }
        }

        b.btnGrantFullscreen.setOnClickListener {
            if (Build.VERSION.SDK_INT >= 34) {
                startActivity(Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT, Uri.parse("package:$packageName")))
            }
        }
        b.btnGrantBattery.setOnClickListener {
            startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }
        b.btnBackgroundHelp.setOnClickListener {
            androidx.appcompat.app.AlertDialog.Builder(this)
                .setTitle(R.string.btn_background_help).setMessage(R.string.background_help)
                .setPositiveButton("打开应用设置") { _, _ ->
                    startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
                }.setNegativeButton("关闭", null).show()
        }
        b.btnGrantOverlay.setOnClickListener { requestOverlayPermission() }
        b.btnGrantNotification.setOnClickListener { requestNotificationAccess() }
        b.btnGrantAlarm.setOnClickListener {
            if (Build.VERSION.SDK_INT >= 31) {
                startActivity(Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:$packageName")))
            }
        }
    }

    // ---------------------------------------------------------------- 载入 / 保存

    private fun loadToUi() {
        suppress = true
        b.btnShowAt.text = cfg.showAtText
        b.btnDeductAt.text = cfg.deductAtText
        b.btnHideAt.text = cfg.hideAtText
        b.etMinutes.setText(cfg.minutesPerPoint.toString())
        b.etPoints.setText(cfg.pointsPerStep.toString())
        b.etStatusBefore.setText(cfg.statusBeforeText)
        b.etStatusHigh.setText(cfg.statusHighText)
        b.etStatusMid.setText(cfg.statusMidText)
        b.etStatusLow.setText(cfg.statusLowText)
        b.etCdInput.setText(cfg.cdInputText)

        val pct = (cfg.opacity * 100).toInt().coerceIn(20, 100)
        b.sbOpacity.progress = pct
        b.tvOpacityValue.text = "$pct%"

        b.swThrough.isChecked = cfg.clickThrough
        b.swShowScore.isChecked = cfg.showScore
        b.swShowCountdown.isChecked = cfg.showCountdown
        b.swNotifyToggle.isChecked = cfg.notifyToggle
        suppress = false

        updateRulesPreview()
    }

    /**
     * 从控件读出规则。非法输入一律回退到默认值，服务端也因此永远拿得到可用规则。
     *
     * ⚠️ 只在**渲染预览**时用它。要落盘请走 [persistRules]——那条路会先校验，
     * 不然用户打错的字会被这份「已经回退过」的规则悄悄覆盖掉。
     */
    private fun collect(): SleepRules = SleepRules(
        showAt = SleepRules.parseHm(b.btnShowAt.text.toString(), SleepRules.Default.showAt),
        deductAt = SleepRules.parseHm(b.btnDeductAt.text.toString(), SleepRules.Default.deductAt),
        hideAt = SleepRules.parseHm(b.btnHideAt.text.toString(), SleepRules.Default.hideAt),
        minutesPerPoint = (b.etMinutes.text.toString().toIntOrNull() ?: 3).coerceIn(1, 720),
        pointsPerStep = (b.etPoints.text.toString().toIntOrNull() ?: 1).coerceIn(1, 100),
        maxScore = ScoreEngine.MAX_SCORE,
        statusBefore = b.etStatusBefore.text.toString(),
        statusHigh = b.etStatusHigh.text.toString(),
        statusMid = b.etStatusMid.text.toString(),
        statusLow = b.etStatusLow.text.toString(),
    )

    /**
     * 校验采用**严格口径**：直接看控件里的原文，读不出来就是错，不做回退。
     *
     * 为什么不能复用 [collect] 的结果：它已经把非法输入换成了默认值，
     * 再拿去 `validate()` 永远是合法的——这正是「校验失败的规则照样存了下去」的成因。
     *
     * @return 合法时返回可保存的规则；否则返回 null，并把原因写进 [b.tvRulesPreview]。
     */
    private fun validatedRules(): SleepRules? {
        val m = b.etMinutes.text.toString().trim().toIntOrNull()
        if (m == null) {
            showRulesError("「每几分钟扣」要填整数。")
            return null
        }
        val p = b.etPoints.text.toString().trim().toIntOrNull()
        if (p == null) {
            showRulesError("「每次扣几分」要填整数。")
            return null
        }

        val r = SleepRules(
            showAt = SleepRules.tryParseHm(b.btnShowAt.text.toString())
                ?: return showRulesError("「窗口出现」时间读不出来，按 20:30 这样填。"),
            deductAt = SleepRules.tryParseHm(b.btnDeductAt.text.toString())
                ?: return showRulesError("「开始扣分」时间读不出来，按 23:30 这样填。"),
            hideAt = SleepRules.tryParseHm(b.btnHideAt.text.toString())
                ?: return showRulesError("「窗口隐藏」时间读不出来，按 06:30 这样填。"),
            minutesPerPoint = m,
            pointsPerStep = p,
            maxScore = ScoreEngine.MAX_SCORE,
            statusBefore = b.etStatusBefore.text.toString(),
            statusHigh = b.etStatusHigh.text.toString(),
            statusMid = b.etStatusMid.text.toString(),
            statusLow = b.etStatusLow.text.toString(),
        )

        r.validate()?.let { return showRulesError(it) }
        return r
    }

    private fun showRulesError(message: String): SleepRules? {
        b.tvRulesPreview.text = "⚠ $message（未保存）"
        return null
    }

    /**
     * 设置改动落盘。**整套校验通过才写**：输入到一半（比如分钟数刚删成空）时
     * 宁可不动既有配置，也不要拿一份回退过的规则覆盖它。
     */
    private fun persistRules() {
        if (suppress) return
        val r = validatedRules() ?: return
        cfg.saveRules(r)
        updateRulesPreview()
    }

    private fun notifyServiceAppearance() {
        if (!OverlayService.isRunning) return
        val i = Intent(this, OverlayService::class.java).setAction(OverlayService.ACTION_REFRESH)
        ContextCompat.startForegroundService(this, i)
    }

    // ---------------------------------------------------------------- 预览

    private fun updateRulesPreview() {
        val rules = collect()
        val err = rules.validate()
        b.tvRulesPreview.text = if (err == null) rules.describe() else "⚠ $err（未保存）"
    }

    /**
     * 实时预览。口径与悬浮窗、与桌面版**完全一致**：按时间戳算，
     * 不掺任何「用没用手机」的因素。
     */
    private fun updatePreview() {
        val rules = collect()
        val now = LocalDateTime.now()
        val state = ScoreEngine.evaluate(now, rules)

        val score = state.score
        val status: String
        val detail: String

        when (state.phase) {
            NightPhase.Daytime -> {
                status = "白天休息中"
                detail = "今天 ${SleepRules.formatHm(rules.showAt)} 出现"
            }

            NightPhase.Countdown -> {
                status = rules.statusBefore
                detail = "距开始扣分 ${ScoreEngine.formatShort(state.untilDeadline)}"
            }

            NightPhase.Exhausted -> {
                status = ScoreVisual.statusForScore(0, rules)
                detail = "已扣光 · ${SleepRules.formatHm(rules.hideAt)} 隐藏"
            }

            else -> {
                status = ScoreVisual.statusForScore(score, rules)
                detail = "扣分中 · 每 ${rules.minutesPerPoint} 分钟 −${rules.pointsPerStep} 分"
            }
        }

        b.tvPreviewScore.text = if (state.phase == NightPhase.Daytime) "—" else score.toString()
        b.tvPreviewScore.setTextColor(
            if (state.phase == NightPhase.Daytime) ContextCompat.getColor(this, R.color.text_secondary)
            else ScoreVisual.colorForScore(score)
        )
        b.tvPreviewStatus.text = status
        b.tvPreviewDetail.text = detail
    }

    /**
     * 倒计时预览。状态由服务落盘在配置里，这里还原一份只读副本回放——
     * 剩余时间是从**绝对截止时刻**算出来的，所以和悬浮窗上显示的一定一致。
     */
    private fun refreshCdPreview() {
        val now = LocalDateTime.now()
        cdView.restore(cfg.loadCountdown(), now)
        val st = cdView.tick(now)

        b.tvCdPreview.text = ManualCountdown.bigText(st.phase, st.remaining)
        // 白底卡片用「浅底版」配色：colorFor 的正常档是接近白的，白底上等于看不见
        b.tvCdPreview.setTextColor(ManualCountdown.colorForLightCard(st.phase, st.remaining))
        b.tvCdStatus.text = cdView.subLabel()

        b.btnCdStart.text = when (st.phase) {
            CdPhase.Idle -> getString(R.string.btn_cd_start)
            CdPhase.Finished -> getString(R.string.btn_cd_again)
            else -> getString(R.string.btn_cd_restart)
        }

        b.btnCdToggle.text = if (st.phase == CdPhase.Paused) {
            getString(R.string.btn_cd_resume)
        } else {
            getString(R.string.btn_cd_pause)
        }
        // 时刻模式的截止点钉死在钟点上，冻结它等于把 23:30 变成 23:30:10，所以不允许暂停
        b.btnCdToggle.isEnabled = st.phase == CdPhase.Paused ||
            (st.phase == CdPhase.Running && cdView.pauseAllowed)
        b.btnCdReset.isEnabled = st.phase != CdPhase.Idle
    }

    // ---------------------------------------------------------------- 权限

    private fun blockedNotificationChannel(): String? {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
        return listOf(AlarmRingService.CHANNEL_ID, OverlayService.CHANNEL_ID).firstOrNull {
            manager.getNotificationChannel(it)?.importance == android.app.NotificationManager.IMPORTANCE_NONE
        }
    }

    private fun updatePermissionUi() {
        val overlayOk = Settings.canDrawOverlays(this)
        val notifOk = androidx.core.app.NotificationManagerCompat.from(this).areNotificationsEnabled() &&
            blockedNotificationChannel() == null
        b.tvPermOverlay.text = getString(R.string.perm_overlay_missing)
        b.tvPermNotification.text = getString(R.string.perm_notification_missing)
        b.tvPermOverlay.visibility = if (overlayOk) android.view.View.GONE else android.view.View.VISIBLE
        b.btnGrantOverlay.visibility = b.tvPermOverlay.visibility
        b.tvPermNotification.visibility = if (notifOk) android.view.View.GONE else android.view.View.VISIBLE
        b.btnGrantNotification.visibility = b.tvPermNotification.visibility
        val hasAlarms = cfg.loadAlarms().all.any { it.enabled }
        val alarmOk = AlarmScheduler.canSchedule(this) || !hasAlarms
        val fullscreenOk = !hasAlarms || Build.VERSION.SDK_INT < 34 ||
            (getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager).canUseFullScreenIntent()
        val batteryOk = !hasAlarms || (getSystemService(Context.POWER_SERVICE) as android.os.PowerManager).isIgnoringBatteryOptimizations(packageName)
        val vivoHelp = hasAlarms && (Build.MANUFACTURER.equals("vivo", true) || Build.MANUFACTURER.equals("iQOO", true))
        b.tvPermFullscreen.visibility = if (fullscreenOk) android.view.View.GONE else android.view.View.VISIBLE
        b.btnGrantFullscreen.visibility = b.tvPermFullscreen.visibility
        b.tvPermBattery.visibility = if (batteryOk) android.view.View.GONE else android.view.View.VISIBLE
        b.btnGrantBattery.visibility = b.tvPermBattery.visibility
        b.btnBackgroundHelp.visibility = if (vivoHelp) android.view.View.VISIBLE else android.view.View.GONE
        b.tvPermAlarm.visibility = if (alarmOk) android.view.View.GONE else android.view.View.VISIBLE
        b.btnGrantAlarm.visibility = b.tvPermAlarm.visibility
        b.permissionCard.visibility = if (overlayOk && notifOk && alarmOk && fullscreenOk && batteryOk) android.view.View.GONE else android.view.View.VISIBLE
    }

    private fun requestNotificationAccess() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED &&
            !notifPermissionRequested
        ) {
            notifPermissionRequested = true
            askNotificationPermissionIfNeeded()
            return
        }
        val channel = blockedNotificationChannel()
        val intent = if (channel != null) {
            Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_CHANNEL_ID, channel)
        } else Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
        intent.putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
        try { startActivity(intent) } catch (_: Exception) {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
        }
    }

    private var notifPermissionRequested = false

    private fun requestOverlayPermission() {
        try {
            startActivity(
                Intent(
                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:$packageName"),
                ),
            )
        } catch (_: Throwable) {
            toast("请在系统设置里手动打开「显示在其他应用上层」")
        }
    }

    private fun askNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        notifPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    // ---------------------------------------------------------------- 杂项

    /**
     * 倒计时的状态机住在服务里，所以服务没跑就得先拉起来。
     * 没有悬浮窗权限时服务起来也画不出窗口，但计时本身照走、通知栏看得到——
     * 所以这里只在完全没权限时提示一声，不硬拦。
     */
    private fun ensureServiceForCd(): Boolean {
        if (OverlayService.isRunning) return true
        if (!Settings.canDrawOverlays(this)) {
            toast(getString(R.string.toast_cd_need_overlay))
            requestOverlayPermission()
            return false
        }
        askNotificationPermissionIfNeeded()
        startTimeGuardOverlay()
        return true
    }

    private fun updateOverlayButton() {
        b.btnToggleOverlay.text =
            if (OverlayService.isRunning) getString(R.string.btn_stop_overlay)
            else getString(R.string.btn_start_overlay)
    }

    private fun pickTime(initial: LocalTime, onPicked: (LocalTime) -> Unit) {
        TimePickerDialog(
            this,
            { _, h, m -> onPicked(LocalTime.of(h, m)) },
            initial.hour,
            initial.minute,
            true,
        ).show()
    }

    private fun toast(msg: String) {
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
    }

    /** 只关心「什么时候变了」，不关心变成什么——统一走 persistRules 从控件读。 */
    private class SimpleWatcher(private val onChange: () -> Unit) : android.text.TextWatcher {
        override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
        override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
        override fun afterTextChanged(s: android.text.Editable?) = onChange()
    }
}

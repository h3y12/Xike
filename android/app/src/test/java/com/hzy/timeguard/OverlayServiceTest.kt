package com.hzy.timeguard

import android.content.Context
import android.content.res.Configuration
import android.os.Looper
import android.view.WindowManager
import com.hzy.timeguard.core.AppConfig
import com.hzy.timeguard.databinding.OverlayScoreBinding
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowSettings
import org.robolectric.shadows.ShadowDisplayManager

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class OverlayServiceTest {
    @Test @Config(sdk = [35])
    fun throughOpacityIsAppliedToTheActualWindowAcrossRefreshAndToggle() {
        val app = RuntimeEnvironment.getApplication()
        app.getSharedPreferences("timeguard", Context.MODE_PRIVATE).edit().clear().commit()
        ShadowSettings.setCanDrawOverlays(true)
        val cfg = AppConfig(app)
        cfg.opacity = 0.95f
        cfg.clickThrough = true
        val controller = Robolectric.buildService(OverlayService::class.java).create()
        val service = controller.get()
        fun invoke(name: String): Any? {
            val method = OverlayService::class.java.getDeclaredMethod(name)
            method.isAccessible = true
            return method.invoke(service)
        }
        try {
            val b = invoke("ensureOverlay") as OverlayScoreBinding
            fun params() = b.root.layoutParams as WindowManager.LayoutParams
            assertEquals(0.8f, params().alpha, 0.001f)
            assertTrue(params().flags and WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE != 0)
            cfg.opacity = 1f
            invoke("applyAppearance")
            assertEquals(0.8f, params().alpha, 0.001f)
            cfg.clickThrough = false
            invoke("applyThrough")
            assertEquals(1f, params().alpha, 0f)
            assertEquals(1f, b.root.alpha, 0f)
            assertEquals(0, params().flags and WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE)
        } finally { controller.destroy() }
    }

    @Test fun restoredOverlayAndRotationRemainVisibleWithoutChangingCardSize() {
        val app = RuntimeEnvironment.getApplication()
        app.getSharedPreferences("timeguard", Context.MODE_PRIVATE).edit().clear().commit()
        ShadowSettings.setCanDrawOverlays(true)
        RuntimeEnvironment.setQualifiers("w640dp-h360dp-land")
        @Suppress("DEPRECATION")
        val displayId = (app.getSystemService(Context.WINDOW_SERVICE) as WindowManager)
            .defaultDisplay.displayId
        ShadowDisplayManager.changeDisplay(displayId, "w640dp-h360dp-land")
        val cfg = AppConfig(app)
        cfg.overlayX = 5000
        cfg.overlayY = 5000
        val controller = Robolectric.buildService(OverlayService::class.java).create()
        val service = controller.get()
        try {
            val method = OverlayService::class.java.getDeclaredMethod("ensureOverlay")
            method.isAccessible = true
            val b = method.invoke(service) as OverlayScoreBinding
            b.root.layout(0, 0, 180, 120)
            shadowOf(Looper.getMainLooper()).idle()
            fun assertVisible() {
                val p = b.root.layoutParams as WindowManager.LayoutParams
                val wm = service.getSystemService(Context.WINDOW_SERVICE) as WindowManager
                val dm = android.util.DisplayMetrics()
                @Suppress("DEPRECATION")
                wm.defaultDisplay.getMetrics(dm)
                assertTrue(p.x >= 0 && p.x + b.root.width <= dm.widthPixels)
                assertTrue(p.y >= 0 && p.y + b.root.height <= dm.heightPixels)
                assertEquals(p.x, cfg.overlayX)
                assertEquals(p.y, cfg.overlayY)
                assertEquals(0, p.flags and WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS)
            }
            assertVisible()
            RuntimeEnvironment.setQualifiers("w360dp-h640dp-port")
            ShadowDisplayManager.changeDisplay(displayId, "w360dp-h640dp-port")
            service.onConfigurationChanged(Configuration(app.resources.configuration))
            shadowOf(Looper.getMainLooper()).idle()
            assertVisible()
            RuntimeEnvironment.setQualifiers("w640dp-h360dp-land")
            ShadowDisplayManager.changeDisplay(displayId, "w640dp-h360dp-land")
            service.onConfigurationChanged(Configuration(app.resources.configuration))
            shadowOf(Looper.getMainLooper()).idle()
            assertVisible()
        } finally {
            controller.destroy()
        }
    }
}

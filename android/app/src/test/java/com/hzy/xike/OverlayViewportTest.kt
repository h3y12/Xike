package com.hzy.xike

import android.graphics.Insets
import android.graphics.Rect
import android.view.WindowInsets
import com.hzy.xike.core.OverlayPosition
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class OverlayViewportTest {
    private fun insets(statusBarVisible: Boolean): WindowInsets = WindowInsets.Builder()
        .setInsets(WindowInsets.Type.statusBars(), if (statusBarVisible) Insets.of(0, 24, 0, 0) else Insets.NONE)
        .setInsetsIgnoringVisibility(WindowInsets.Type.statusBars(), Insets.of(0, 24, 0, 0))
        .setVisible(WindowInsets.Type.statusBars(), statusBarVisible)
        .setInsets(WindowInsets.Type.navigationBars(), Insets.of(0, 0, 48, 0))
        .setVisible(WindowInsets.Type.navigationBars(), true).build()

    @Test fun hiddenStatusBarDoesNotReserveItsOldHeight() {
        val hidden = insets(false)
        assertEquals(24, hidden.getInsetsIgnoringVisibility(WindowInsets.Type.statusBars()).top)
        assertEquals(Rect(0, 0, 1232, 720), OverlayViewport.usableBounds(1280, 720, hidden))
        assertEquals(Rect(0, 24, 1232, 720), OverlayViewport.usableBounds(1280, 720, insets(true)))
    }

    @Test fun physicalCutoutStillRemainsProtected() {
        val withCutout = WindowInsets.Builder(insets(false))
            .setInsets(WindowInsets.Type.displayCutout(), Insets.of(40, 0, 0, 0)).build()
        assertEquals(Rect(40, 0, 1232, 720), OverlayViewport.usableBounds(1280, 720, withCutout))
    }

    @Test fun showingBarsAgainRecoversATopEdgePositionSafely() {
        val bounds = OverlayViewport.usableBounds(1280, 720, insets(true))
        val moved = OverlayPosition.fit(400, 0, 180, 120,
            bounds.left, bounds.top, bounds.right, bounds.bottom, 8, topMargin = 0)
        assertEquals(24, moved.y)
        assertEquals(400, moved.x)
    }
}

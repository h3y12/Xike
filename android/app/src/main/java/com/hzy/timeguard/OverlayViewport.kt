package com.hzy.timeguard

import android.graphics.Rect
import android.view.WindowInsets
import androidx.annotation.RequiresApi

/** Full-display coordinates, avoiding only currently visible bars and physical cutouts. */
internal object OverlayViewport {
    @RequiresApi(30)
    fun usableBounds(width: Int, height: Int, insets: WindowInsets): Rect {
        val visible = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
        return Rect(visible.left, visible.top, width - visible.right, height - visible.bottom)
    }
}

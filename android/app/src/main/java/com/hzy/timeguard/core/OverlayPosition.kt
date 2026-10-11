package com.hzy.timeguard.core

/** Fits saved overlay coordinates into the current display's usable bounds. */
data class OverlayPosition(val x: Int, val y: Int) {
    companion object {
        fun fit(
            x: Int, y: Int, width: Int, height: Int,
            left: Int, top: Int, right: Int, bottom: Int, margin: Int,
        ): OverlayPosition {
            val minX = left + margin
            val minY = top + margin
            val maxX = (right - width - margin).coerceAtLeast(minX)
            val maxY = (bottom - height - margin).coerceAtLeast(minY)
            val initial = x == -1 && y == -1
            return OverlayPosition(
                (if (initial) right - width - 2 * margin else x).coerceIn(minX, maxX),
                (if (initial) top + ((bottom - top) * 0.62f).toInt() - height / 2 else y)
                    .coerceIn(minY, maxY),
            )
        }
    }
}

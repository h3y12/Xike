package com.hzy.timeguard.core

object OverlayOpacity {
    fun effective(configured: Float, clickThrough: Boolean, maximum: Float): Float {
        val opacity = if (configured.isFinite()) configured.coerceIn(0.2f, 1f) else 0.88f
        val limit = if (maximum.isFinite()) maximum.coerceIn(0f, 1f) else 0.8f
        return if (clickThrough) minOf(opacity, limit) else opacity
    }
}

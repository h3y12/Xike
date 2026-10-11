package com.hzy.timeguard.core

import org.junit.Assert.assertEquals
import org.junit.Test

class OverlayOpacityTest {
    @Test fun throughModeHonorsSystemTouchLimit() {
        assertEquals(0.8f, OverlayOpacity.effective(0.88f, true, 0.8f), 0f)
        assertEquals(0.6f, OverlayOpacity.effective(1f, true, 0.6f), 0f)
        assertEquals(0.5f, OverlayOpacity.effective(0.5f, true, 0.8f), 0f)
    }
    @Test fun leavingThroughModeRestoresConfiguredAppearance() {
        assertEquals(0.88f, OverlayOpacity.effective(0.88f, false, 0.8f), 0f)
        assertEquals(1f, OverlayOpacity.effective(1f, false, 0.8f), 0f)
    }
    @Test fun olderSystemsAndMalformedValuesRemainBounded() {
        assertEquals(0.88f, OverlayOpacity.effective(0.88f, true, 1f), 0f)
        assertEquals(0.8f, OverlayOpacity.effective(Float.NaN, true, Float.NaN), 0f)
    }
}

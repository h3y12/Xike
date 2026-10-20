package com.hzy.timeguard.core

import org.junit.Assert.*
import org.junit.Test

class OverlayPositionTest {
    @Test fun landscapePositionIsRecoveredInPortrait() {
        val p = OverlayPosition.fit(1900, 400, 180, 120, 0, 24, 1080, 2300, 8)
        assertEquals(892, p.x)
        assertEquals(400, p.y)
    }

    @Test fun portraitPositionIsRecoveredInLandscapeAndSurvivesReturn() {
        val landscape = OverlayPosition.fit(800, 1800, 180, 120, 40, 24, 2300, 1080, 8)
        assertEquals(952, landscape.y)
        val portrait = OverlayPosition.fit(landscape.x, landscape.y, 180, 120, 0, 24, 1080, 2300, 8)
        assertEquals(landscape, portrait)
    }

    @Test fun dragCannotHideCardPastAnyEdge() {
        assertEquals(OverlayPosition(48, 32),
            OverlayPosition.fit(-500, -300, 180, 120, 40, 24, 1080, 2300, 8))
        assertEquals(OverlayPosition(892, 2172),
            OverlayPosition.fit(5000, 5000, 180, 120, 40, 24, 1080, 2300, 8))
    }

    @Test fun largerCountdownCardStaysInsideBottomEdge() {
        val p = OverlayPosition.fit(800, 2170, 200, 240, 0, 24, 1080, 2300, 8)
        assertEquals(2052, p.y)
        assertEquals(800, p.x)
    }

    @Test fun firstLaunchAndTinyDisplayHaveValidPositions() {
        val p = OverlayPosition.fit(-1, -1, 180, 120, 0, 24, 1080, 2300, 8)
        assertEquals(884, p.x)
        assertTrue(p.y in 32..2172)
        assertEquals(OverlayPosition(8, 8),
            OverlayPosition.fit(500, 500, 180, 240, 0, 0, 100, 100, 8))
    }
}

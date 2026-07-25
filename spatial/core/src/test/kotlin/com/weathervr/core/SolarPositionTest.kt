package com.weathervr.core

import java.time.Instant
import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertTrue

/**
 * Solar position, ported from `tools/verify.py`'s solstice checks.
 *
 * The sun angle is what makes an afternoon snapshot light its cloud tops from the west.
 * Getting it wrong reads as "computer graphics" rather than as weather, and it is easy
 * to get wrong in ways that still look like a plausible arc.
 */
class SolarPositionTest {

    private val londonLat = 51.5136
    private val londonLon = -0.0832

    @Test
    fun `sun is high at midsummer noon and low at midwinter noon in London`() {
        // At 51.5°N: solstice noon elevation is 90 − 51.5 ± 23.44 → about 62° and 15°.
        val summer = SolarPosition.compute(Instant.parse("2026-06-21T12:00:00Z"), londonLat, londonLon)
        val winter = SolarPosition.compute(Instant.parse("2026-12-21T12:00:00Z"), londonLat, londonLon)

        assertTrue(
            abs(summer.elevationDegrees - 62.0) < 2.0,
            "midsummer noon elevation was ${summer.elevationDegrees}°, expected ~62°",
        )
        assertTrue(
            abs(winter.elevationDegrees - 15.0) < 2.0,
            "midwinter noon elevation was ${winter.elevationDegrees}°, expected ~15°",
        )
    }

    @Test
    fun `sun is due south at local noon`() {
        val noon = SolarPosition.compute(Instant.parse("2026-06-21T12:00:00Z"), londonLat, londonLon)
        assertTrue(
            abs(noon.azimuthDegrees - 180.0) < 5.0,
            "at solar noon in the northern hemisphere the sun should bear ~180°, got ${noon.azimuthDegrees}°",
        )
    }

    @Test
    fun `sun rises in the east and sets in the west`() {
        val morning = SolarPosition.compute(Instant.parse("2026-06-21T05:00:00Z"), londonLat, londonLon)
        val evening = SolarPosition.compute(Instant.parse("2026-06-21T19:00:00Z"), londonLat, londonLon)

        assertTrue(morning.azimuthDegrees in 30.0..120.0, "morning sun bore ${morning.azimuthDegrees}°")
        assertTrue(evening.azimuthDegrees in 240.0..330.0, "evening sun bore ${evening.azimuthDegrees}°")
    }

    @Test
    fun `sun is below the horizon at midnight`() {
        val midnight = SolarPosition.compute(Instant.parse("2026-06-21T00:00:00Z"), londonLat, londonLon)
        assertTrue(midnight.elevationDegrees < 0.0, "the sun should be down at midnight in London")
    }

    @Test
    fun `southern hemisphere seasons are inverted`() {
        // Sydney: December is summer. A hemisphere sign error is otherwise invisible from
        // the London tests alone.
        val december = SolarPosition.compute(Instant.parse("2026-12-21T02:00:00Z"), -33.87, 151.21)
        val june = SolarPosition.compute(Instant.parse("2026-06-21T02:00:00Z"), -33.87, 151.21)
        assertTrue(
            december.elevationDegrees > june.elevationDegrees,
            "December should be higher than June in Sydney: ${december.elevationDegrees}° vs ${june.elevationDegrees}°",
        )
    }

    @Test
    fun `azimuth stays in range across a full day`() {
        var t = Instant.parse("2026-03-20T00:00:00Z")
        val end = Instant.parse("2026-03-21T00:00:00Z")
        while (t.isBefore(end)) {
            val angles = SolarPosition.compute(t, londonLat, londonLon)
            assertTrue(angles.azimuthDegrees in 0.0..360.0, "azimuth ${angles.azimuthDegrees}° at $t")
            assertTrue(angles.elevationDegrees in -90.0..90.0, "elevation ${angles.elevationDegrees}° at $t")
            t = t.plusSeconds(1800)
        }
    }
}

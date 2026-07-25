package com.weathervr.core

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The barometric and lightning-proxy checks, ported from `tools/verify.py`.
 *
 * These are worth keeping because the formula is the one place in the data layer where
 * a plausible-looking wrong answer is easy to produce: swap the exponent's sign or drop
 * the hPa→Pa conversion and you still get monotonic altitudes in a believable range.
 * The reference values below come from the standard atmosphere, not from this code.
 */
class AtmosphereTest {

    @Test
    fun `sea level pressure is sea level`() {
        assertEquals(0.0, Atmosphere.altitudeFromPressureHpa(1013.25), 0.5)
    }

    @Test
    fun `standard atmosphere reference points`() {
        // Textbook standard-atmosphere heights for these pressure levels.
        val references = listOf(
            1000.0 to 110.9,
            800.0 to 1949.3,
            500.0 to 5575.2,
            450.0 to 6344.5,
            200.0 to 11776.4,
        )
        for ((hpa, expectedMeters) in references) {
            val actual = Atmosphere.altitudeFromPressureHpa(hpa)
            assertTrue(
                abs(actual - expectedMeters) < 1.0,
                "at $hpa hPa expected ~$expectedMeters m, got $actual m",
            )
        }
    }

    @Test
    fun `pressure and altitude round trip`() {
        for (meters in listOf(0.0, 500.0, 2_000.0, 8_000.0, 12_000.0)) {
            val roundTripped = Atmosphere.altitudeFromPressure(Atmosphere.pressureFromAltitude(meters))
            assertEquals(meters, roundTripped, 0.01, "round trip failed at $meters m")
        }
    }

    @Test
    fun `altitude is monotonic in falling pressure`() {
        var previous = Double.NEGATIVE_INFINITY
        var hpa = 1050.0
        while (hpa >= 100.0) {
            val altitude = Atmosphere.altitudeFromPressureHpa(hpa)
            assertTrue(altitude > previous, "altitude not increasing as pressure falls, at $hpa hPa")
            previous = altitude
            hpa -= 25.0
        }
    }

    @Test
    fun `cloud layers stack without gaps or overlaps`() {
        // Each layer's top must be the next layer's base, or the cloud deck has a seam.
        assertEquals(Atmosphere.Layer.LOW.topPressureHpa, Atmosphere.Layer.MID.basePressureHpa)
        assertEquals(Atmosphere.Layer.MID.topPressureHpa, Atmosphere.Layer.HIGH.basePressureHpa)

        for (layer in Atmosphere.Layer.entries) {
            assertTrue(
                layer.topAltitudeMeters > layer.baseAltitudeMeters,
                "$layer is inverted: base ${layer.baseAltitudeMeters} m, top ${layer.topAltitudeMeters} m",
            )
        }
    }

    @Test
    fun `lightning needs both instability and rain`() {
        // A dry unstable atmosphere is not a thunderstorm — this is the specific
        // physical claim the proxy makes, and the reason it is a product not a sum.
        assertEquals(0f, Atmosphere.lightningPotential(4000f, 0f), 1e-6f)
        // Nor is a wet stable one.
        assertEquals(0f, Atmosphere.lightningPotential(0f, 20f), 1e-6f)
        // Both present: something happens.
        assertTrue(Atmosphere.lightningPotential(2500f, 4f) > 0.8f)
        // And it stays in range at absurd inputs.
        assertEquals(1f, Atmosphere.lightningPotential(99_000f, 500f), 1e-6f)
    }

    @Test
    fun `lightning potential rises with both inputs`() {
        assertTrue(
            Atmosphere.lightningPotential(1500f, 2f) > Atmosphere.lightningPotential(800f, 2f),
            "more CAPE should mean more lightning",
        )
        assertTrue(
            Atmosphere.lightningPotential(1500f, 3f) > Atmosphere.lightningPotential(1500f, 1f),
            "more rain should mean more lightning",
        )
    }
}

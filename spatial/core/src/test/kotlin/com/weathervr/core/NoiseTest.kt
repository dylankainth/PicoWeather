package com.weathervr.core

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The noise field underpins every procedural fallback, so its two load-bearing
 * properties are determinism (the same seed must give the same weather on every device,
 * which is what makes the demo repeatable) and exact tiling for the detail volume.
 *
 * The tiling checks are ported from `tools/verify.py`, which tests the same thing about
 * the C#.
 */
class NoiseTest {

    @Test
    fun `is deterministic for a given seed`() {
        val a = Noise.fbm2(3.7f, -1.2f, seed = 1234)
        val b = Noise.fbm2(3.7f, -1.2f, seed = 1234)
        assertEquals(a, b, 0f, "same input and seed must give the same value")
    }

    @Test
    fun `different seeds give different fields`() {
        val a = Noise.fbm2(3.7f, -1.2f, seed = 1)
        val b = Noise.fbm2(3.7f, -1.2f, seed = 2)
        assertTrue(abs(a - b) > 1e-6f, "seed is not reaching the hash")
    }

    @Test
    fun `stays in range across a wide sample`() {
        // Perlin scaled to 0..1 can be pushed out of range by a bad normalisation
        // constant, and the clamp would then be silently doing the work.
        var atFloor = 0
        var atCeiling = 0
        for (i in 0 until 4000) {
            val x = (i % 97) * 0.37f - 18f
            val y = (i / 97) * 0.53f - 11f
            val p2 = Noise.perlin2(x, y, seed = 7)
            val p3 = Noise.perlin3(x, y, x * 0.3f, seed = 7)
            val w3 = Noise.worley3(x, y, x * 0.3f, seed = 7)
            for (v in listOf(p2, p3, w3)) {
                assertTrue(v in 0f..1f, "noise out of range at ($x,$y): $v")
            }
            if (p2 <= 0f) atFloor++
            if (p2 >= 1f) atCeiling++
        }
        // If the clamp is doing heavy lifting the field is wrong, not merely clipped.
        assertTrue(atFloor < 40 && atCeiling < 40, "clamped $atFloor low, $atCeiling high — check scaling")
    }

    @Test
    fun `is not constant`() {
        val values = (0 until 50).map { Noise.perlin2(it * 0.31f, it * 0.17f, seed = 3) }
        assertTrue(values.distinct().size > 40, "field looks constant")
    }

    @Test
    fun `periodic perlin tiles exactly on all three axes`() {
        val period = 4
        val seed = 99
        for (i in 0 until 40) {
            val x = (i % 7) * 0.31f
            val y = (i / 7) * 0.23f
            val z = i * 0.11f

            val base = Noise.perlin3Periodic(x, y, z, period, seed)
            assertEquals(base, Noise.perlin3Periodic(x + period, y, z, period, seed), 1e-5f, "X seam")
            assertEquals(base, Noise.perlin3Periodic(x, y + period, z, period, seed), 1e-5f, "Y seam")
            assertEquals(base, Noise.perlin3Periodic(x, y, z + period, period, seed), 1e-5f, "Z seam")
        }
    }

    @Test
    fun `periodic worley tiles exactly on all three axes`() {
        val period = 4
        val seed = 51
        for (i in 0 until 40) {
            val x = (i % 7) * 0.31f
            val y = (i / 7) * 0.23f
            val z = i * 0.11f

            val base = Noise.worley3Periodic(x, y, z, period, seed)
            assertEquals(base, Noise.worley3Periodic(x + period, y, z, period, seed), 1e-5f, "X seam")
            assertEquals(base, Noise.worley3Periodic(x, y + period, z, period, seed), 1e-5f, "Y seam")
            assertEquals(base, Noise.worley3Periodic(x, y, z + period, period, seed), 1e-5f, "Z seam")
        }
    }

    @Test
    fun `cloud detail volume tiles over the unit cube`() {
        // This is the property that matters on screen: the detail texture is tiled by the
        // renderer, so a seam here is a visible grid across the sky.
        val baseFrequency = 4
        val seed = 2024
        for (i in 0 until 25) {
            val x = (i % 5) * 0.2f
            val y = (i / 5) * 0.2f
            val z = i * 0.04f

            val base = Noise.cloudDetailPeriodic(x, y, z, baseFrequency, seed)
            assertEquals(base, Noise.cloudDetailPeriodic(x + 1f, y, z, baseFrequency, seed), 1e-5f, "X seam")
            assertEquals(base, Noise.cloudDetailPeriodic(x, y + 1f, z, baseFrequency, seed), 1e-5f, "Y seam")
            assertEquals(base, Noise.cloudDetailPeriodic(x, y, z + 1f, baseFrequency, seed), 1e-5f, "Z seam")
        }
    }

    @Test
    fun `fbm averages toward the middle of the range`() {
        var sum = 0.0
        val n = 2000
        for (i in 0 until n) {
            sum += Noise.fbm2(i * 0.137f, i * 0.081f, seed = 5)
        }
        val mean = sum / n
        assertTrue(mean in 0.35..0.65, "fBm mean $mean is skewed; expected roughly 0.5")
    }
}

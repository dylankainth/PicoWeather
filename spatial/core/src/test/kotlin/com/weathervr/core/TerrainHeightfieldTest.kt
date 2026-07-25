package com.weathervr.core

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * The binary format is the contract between the Python baker and this app. Getting it
 * wrong does not throw — a big-endian read of a little-endian file yields a grid full
 * of plausible-looking noise — so it is checked both ways: synthetic round-trip, and
 * against the real baked file the Unity build ships with.
 */
class TerrainHeightfieldTest {

    /**
     * The actual baked London terrain, read from the Unity project next door. Skipped
     * rather than failed when absent: the repo's data payload is optional by design,
     * and CI without it should not go red.
     */
    private fun bakedTerrain(): File? {
        val candidates = listOf(
            File("../../Assets/StreamingAssets/WeatherData/terrain.bin"),
            File("../Assets/StreamingAssets/WeatherData/terrain.bin"),
            File("Assets/StreamingAssets/WeatherData/terrain.bin"),
        )
        return candidates.firstOrNull { it.isFile }
    }

    @Test
    fun `round trips through bytes`() {
        val bounds = GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0)
        val original = TerrainHeightfield(8, 6, bounds, -5f, 43.7f)
        for (y in 0 until 6) {
            for (x in 0 until 8) {
                original.setElevation(x, y, -5f + (x + y).toFloat())
            }
        }

        val restored = TerrainHeightfield.fromBytes(original.toBytes())

        assertEquals(original.width, restored.width)
        assertEquals(original.height, restored.height)
        assertEquals(original.minElevation, restored.minElevation)
        assertEquals(original.maxElevation, restored.maxElevation)
        assertEquals(bounds.minLatitude, restored.bounds.minLatitude, 1e-12)
        assertEquals(bounds.maxLongitude, restored.bounds.maxLongitude, 1e-12)

        for (y in 0 until 6) {
            for (x in 0 until 8) {
                // 16-bit quantisation over a ~49 m range is ~0.001 m, so this is tight.
                assertEquals(
                    original.elevationAt(x, y),
                    restored.elevationAt(x, y),
                    0.01f,
                    "sample ($x,$y) survived the round trip",
                )
            }
        }
    }

    @Test
    fun `rejects a file that is not terrain`() {
        val notTerrain = "this is not a heightfield, it is a cry for help".toByteArray()
        val failure = assertFailsWith<InvalidTerrainDataException> {
            TerrainHeightfield.fromBytes(notTerrain)
        }
        assertTrue(failure.message!!.contains("magic"), "should complain about the magic")
    }

    @Test
    fun `rejects empty and truncated input`() {
        assertFailsWith<InvalidTerrainDataException> { TerrainHeightfield.fromBytes(null) }
        assertFailsWith<InvalidTerrainDataException> { TerrainHeightfield.fromBytes(ByteArray(0)) }

        val bounds = GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0)
        val full = TerrainHeightfield(8, 6, bounds, 0f, 10f).toBytes()
        val truncated = full.copyOf(full.size - 20)
        val failure = assertFailsWith<InvalidTerrainDataException> {
            TerrainHeightfield.fromBytes(truncated)
        }
        assertTrue(failure.message!!.contains("truncated"), "should say it is truncated")
    }

    @Test
    fun `bilinear sampling interpolates between grid points`() {
        val bounds = GeoBounds.fromCenterSpan(0.0, 0.0, 5.0)
        val field = TerrainHeightfield(2, 2, bounds, 0f, 100f)
        field.setElevation(0, 0, 0f)
        field.setElevation(1, 0, 100f)
        field.setElevation(0, 1, 0f)
        field.setElevation(1, 1, 100f)

        assertEquals(0f, field.sampleElevation(0f, 0f), 0.01f)
        assertEquals(100f, field.sampleElevation(1f, 0f), 0.01f)
        assertEquals(50f, field.sampleElevation(0.5f, 0.5f), 0.01f)

        // Out-of-range coordinates clamp rather than extrapolating off a cliff.
        assertEquals(0f, field.sampleElevation(-3f, 0f), 0.01f)
        assertEquals(100f, field.sampleElevation(9f, 0f), 0.01f)
    }

    @Test
    fun `decodes the real baked London terrain`() {
        val file = bakedTerrain()
        if (file == null) {
            println("SKIP: no baked terrain.bin found; run tools/build_all.py to exercise this test")
            return
        }

        val field = TerrainHeightfield.fromBytes(file.readBytes())

        assertEquals(512, field.width, "the baker writes a 512x512 grid by default")
        assertEquals(512, field.height)

        // London, not Shanghai — the region moved and the bake is expected to have
        // followed. These bounds are wide enough to survive a re-bake, tight enough to
        // catch a stale or wrong-region file.
        assertTrue(
            field.bounds.centerLatitude in 51.4..51.6,
            "expected a London latitude, got ${field.bounds.centerLatitude}",
        )
        assertTrue(
            field.bounds.centerLongitude in -0.2..0.05,
            "expected a London longitude, got ${field.bounds.centerLongitude}",
        )

        // The Thames floodplain: a few metres below sea level at the lowest, a few tens
        // of metres at the highest. Anything outside this is a decode error, not terrain.
        assertTrue(
            field.minElevation > -50f && field.minElevation < 20f,
            "implausible minimum elevation ${field.minElevation} m",
        )
        assertTrue(
            field.maxElevation > 10f && field.maxElevation < 200f,
            "implausible maximum elevation ${field.maxElevation} m",
        )

        // A real heightfield is not constant, and every sample must sit in range.
        var seenDistinct = false
        val first = field.normalizedAt(0, 0)
        for (y in 0 until field.height step 37) {
            for (x in 0 until field.width step 37) {
                val n = field.normalizedAt(x, y)
                assertTrue(n in 0f..1f, "sample ($x,$y) out of range: $n")
                if (!seenDistinct && kotlin.math.abs(n - first) > 1e-4f) seenDistinct = true
            }
        }
        assertTrue(seenDistinct, "every sample identical — the file decoded as flat")

        println("decoded $field")
    }
}

package com.weathervr.core

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The procedural fallbacks are what make the app run with no network and no baked data,
 * so what is tested here is that they always produce something *plausible and
 * deterministic*, not that they produce any particular picture.
 */
class ProceduralTest {

    private val bounds = GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0)
    private val fixedNow: Instant = Instant.parse("2026-07-25T06:00:00Z")

    // ------------------------------------------------------------------ terrain

    @Test
    fun `terrain is deterministic for a seed`() {
        val a = ProceduralTerrain.generate(bounds, 64, seed = 7)
        val b = ProceduralTerrain.generate(bounds, 64, seed = 7)
        for (y in 0 until 64 step 7) {
            for (x in 0 until 64 step 7) {
                assertEquals(a.elevationAt(x, y), b.elevationAt(x, y), 0f, "sample ($x,$y) drifted")
            }
        }
    }

    @Test
    fun `terrain covers sea, plain and hills`() {
        val field = ProceduralTerrain.generate(bounds, 128, seed = 3)
        var min = Float.MAX_VALUE
        var max = -Float.MAX_VALUE
        var below = 0
        for (y in 0 until 128) {
            for (x in 0 until 128) {
                val e = field.elevationAt(x, y)
                if (e < min) min = e
                if (e > max) max = e
                if (e < 0f) below++
            }
        }
        assertTrue(below > 0, "no water at all — the coastline is missing")
        assertTrue(below < 128 * 128, "everything is underwater")
        assertTrue(max > 20f, "no relief above the plain, peak was $max m")
        assertTrue(min >= ProceduralTerrain.SEA_FLOOR_ELEVATION - 5f, "sea floor undercut: $min m")
    }

    @Test
    fun `terrain resolution is clamped`() {
        assertEquals(32, ProceduralTerrain.generate(bounds, 1, seed = 1).width)
        assertEquals(2048, ProceduralTerrain.generate(bounds, 99_999, seed = 1).width)
    }

    // ------------------------------------------------------------------ weather

    @Test
    fun `weather grid is valid and deterministic`() {
        val a = ProceduralWeather.generate(bounds, 12, seed = 11, now = fixedNow)
        val b = ProceduralWeather.generate(bounds, 12, seed = 11, now = fixedNow)

        assertTrue(a.isValid, "generated dataset failed its own validity check")
        assertEquals(144, a.cells.size)
        assertEquals(a, b, "same seed and clock must give the same snapshot")
        assertEquals("procedural", a.source)
        assertEquals(3, a.layers.size)
    }

    @Test
    fun `weather fields stay physical`() {
        val dataset = ProceduralWeather.generate(bounds, 16, seed = 4, now = fixedNow)
        for (cell in dataset.cells) {
            assertTrue(cell.cloudTotal in 0f..1f, "cloudTotal ${cell.cloudTotal}")
            assertTrue(cell.cloudLow in 0f..1f, "cloudLow ${cell.cloudLow}")
            assertTrue(cell.cloudMid in 0f..1f, "cloudMid ${cell.cloudMid}")
            assertTrue(cell.cloudHigh in 0f..1f, "cloudHigh ${cell.cloudHigh}")
            assertTrue(cell.lightningPotential in 0f..1f, "lightning ${cell.lightningPotential}")
            assertTrue(cell.precipitationMmHr >= 0f, "negative rain ${cell.precipitationMmHr}")
            assertTrue(cell.capeJkg >= 0f, "negative CAPE ${cell.capeJkg}")
            assertTrue(cell.temperatureC > -60f && cell.temperatureC < 60f, "temp ${cell.temperatureC}")
        }
    }

    @Test
    fun `total cloud is at least the largest layer`() {
        // Total is the random-overlap combination 1 − Π(1 − c_i), so it can never be less
        // than any single layer. Getting this backwards makes the sky read thinner than
        // the cloud actually present.
        val dataset = ProceduralWeather.generate(bounds, 16, seed = 9, now = fixedNow)
        for (cell in dataset.cells) {
            val largest = maxOf(cell.cloudLow, cell.cloudMid, cell.cloudHigh)
            assertTrue(
                cell.cloudTotal >= largest - 1e-5f,
                "total ${cell.cloudTotal} below largest layer $largest",
            )
        }
    }

    @Test
    fun `the squall line actually crosses the map`() {
        // The whole point of DEFAULT_PHASE: the convective core should sit over the middle
        // of the region, not tucked into a corner. Compare the wettest cell's position.
        val dataset = ProceduralWeather.generate(bounds, 24, seed = 6, now = fixedNow)
        val wettest = dataset.cells.withIndex().maxBy { it.value.precipitationMmHr }
        val x = wettest.index % 24
        val y = wettest.index / 24

        assertTrue(wettest.value.precipitationMmHr > 1f, "no meaningful rain anywhere")
        assertTrue(x in 4..19 && y in 4..19, "heaviest rain at ($x,$y) is against the map edge")
    }

    @Test
    fun `phase marches the system across the region`() {
        val early = ProceduralWeather.generate(bounds, 16, seed = 2, phase = 0.2f, now = fixedNow)
        val late = ProceduralWeather.generate(bounds, 16, seed = 2, phase = 1.1f, now = fixedNow)

        fun centroidX(d: WeatherDataset): Float {
            var sum = 0f
            var weight = 0f
            for (i in d.cells.indices) {
                val w = d.cells[i].precipitationMmHr
                sum += (i % 16) * w
                weight += w
            }
            return if (weight > 0f) sum / weight else -1f
        }

        assertTrue(centroidX(late) > centroidX(early), "increasing phase should move the line east")
    }

    // ---------------------------------------------------------------- buildings

    @Test
    fun `buildings are deterministic, inside the region and sanely sized`() {
        val a = ProceduralBuildings.generate(bounds, seed = 5)
        val b = ProceduralBuildings.generate(bounds, seed = 5)
        assertEquals(a, b, "same seed must give the same city")

        assertTrue(a.isValid)
        assertTrue(a.buildings.isNotEmpty(), "no buildings generated at all")
        assertEquals("procedural", a.source)

        for (building in a.buildings) {
            assertEquals(4, building.pointCount, "footprints are rectangles")
            assertTrue(building.heightMeters in 8f..180f, "height ${building.heightMeters} m")
            for (p in building.points) {
                assertTrue(
                    bounds.contains(p.latitude, p.longitude),
                    "footprint point (${p.latitude}, ${p.longitude}) is outside the region",
                )
            }
        }
    }

    @Test
    fun `there is exactly one landmark tower`() {
        val dataset = ProceduralBuildings.generate(bounds, seed = 5)
        val landmarks = dataset.buildings.count { it.heightMeters == 180f }
        assertEquals(1, landmarks, "expected a single landmark at the centre")
    }

    @Test
    fun `footprints are wound counter-clockwise`() {
        // The mesh builder assumes CCW winding in (u, v); reversed rings extrude inside-out.
        val dataset = ProceduralBuildings.generate(bounds, seed = 8)
        for (building in dataset.buildings) {
            var area = 0.0
            val n = building.pointCount
            for (i in 0 until n) {
                val j = (i + 1) % n
                // Shoelace in (lon, lat) — the same orientation as (u, v), since the
                // projection between them is a positive scale with no reflection.
                area += building.longitudeAt(i) * building.latitudeAt(j) -
                    building.longitudeAt(j) * building.latitudeAt(i)
            }
            assertTrue(area > 0.0, "footprint is wound clockwise (signed area $area)")
        }
    }

    // ---------------------------------------------------------------- satellite

    @Test
    fun `satellite image is deterministic and plausible`() {
        val field = ProceduralTerrain.generate(bounds, 64, seed = 12)
        val a = ProceduralSatellite.generate(field, 128, seed = 12)
        val b = ProceduralSatellite.generate(field, 128, seed = 12)
        assertTrue(a.pixels.contentEquals(b.pixels), "same seed must give the same basemap")

        assertEquals(128, a.width)
        assertEquals(128 * 128 * 3, a.pixels.size)

        // Not a flat fill, and not a black screen.
        val distinct = HashSet<Int>()
        var sum = 0L
        for (y in 0 until 128 step 3) {
            for (x in 0 until 128 step 3) {
                val (r, g, bch) = a.get(x, y)
                distinct.add((r shl 16) or (g shl 8) or bch)
                sum += r + g + bch
            }
        }
        assertTrue(distinct.size > 200, "basemap has only ${distinct.size} distinct colours")
        val meanChannel = sum / (3.0 * (128 / 3 + 1) * (128 / 3 + 1))
        assertTrue(meanChannel in 20.0..200.0, "basemap mean brightness $meanChannel is implausible")
    }

    @Test
    fun `water shades blue-green and land does not`() {
        // Deep water: blue channel should dominate red. Cropland: green should dominate.
        val water = ProceduralSatellite.shade(0.9f, 0.5f, elevation = -15f, seed = 1)
        assertTrue(water.b > water.r, "deep water is not blue-dominant: $water")

        val land = ProceduralSatellite.shade(0.15f, 0.8f, elevation = 6f, seed = 1)
        assertTrue(land.g > land.b, "cropland is not green-dominant: $land")
    }
}

package com.weathervr.core

import java.time.Instant
import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Open-Meteo ingest. No networking happens here — [OpenMeteo] is deliberately a pair of
 * pure functions — so both the request URL and the response mapping are testable, which
 * they were not in the C# where they sat behind `UnityWebRequest`.
 */
class OpenMeteoTest {

    private val bounds = GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0)
    private val fixedNow: Instant = Instant.parse("2026-07-25T06:00:00Z")

    // ------------------------------------------------------------------ request

    @Test
    fun `url carries one coordinate pair per grid point`() {
        val url = OpenMeteo.buildUrl(bounds, 3)
        val lats = url.substringAfter("latitude=").substringBefore("&").split(",")
        val lons = url.substringAfter("longitude=").substringBefore("&").split(",")

        assertEquals(9, lats.size)
        assertEquals(9, lons.size)
        assertTrue(url.startsWith(OpenMeteo.ENDPOINT))
        assertTrue(url.contains("timezone=GMT"), "the Z suffix on the timestamp depends on this")
        assertTrue(url.contains("hourly=cape"), "CAPE only comes back on the hourly series")
    }

    @Test
    fun `url uses a dot decimal separator regardless of locale`() {
        // A comma decimal separator would silently double the coordinate count and the
        // request would come back the wrong shape — a locale-dependent bug that only
        // fires on some users' devices.
        val previous = java.util.Locale.getDefault()
        try {
            java.util.Locale.setDefault(java.util.Locale.GERMANY)
            val url = OpenMeteo.buildUrl(bounds, 2)
            val lats = url.substringAfter("latitude=").substringBefore("&").split(",")
            assertEquals(4, lats.size, "coordinate list split apart under a comma locale")
            assertTrue(lats.all { it.contains('.') }, "expected dot decimals, got $lats")
        } finally {
            java.util.Locale.setDefault(previous)
        }
    }

    @Test
    fun `url spans the whole region`() {
        val url = OpenMeteo.buildUrl(bounds, 2)
        val lats = url.substringAfter("latitude=").substringBefore("&").split(",").map { it.toDouble() }
        assertEquals(bounds.minLatitude, lats.min(), 1e-3)
        assertEquals(bounds.maxLatitude, lats.max(), 1e-3)
    }

    @Test
    fun `grid size is clamped to keep the url sane`() {
        val huge = OpenMeteo.buildUrl(bounds, 64)
        val lats = huge.substringAfter("latitude=").substringBefore("&").split(",")
        assertEquals(144, lats.size, "grid should clamp to 12×12")
    }

    // -------------------------------------------------------------------- parse

    private fun location(
        cloud: Int = 50,
        low: Int = 40,
        mid: Int = 30,
        high: Int = 20,
        precipitation: Double = 1.5,
        temperature: Double = 18.0,
        windSpeed: Double = 36.0,
        windDirection: Double = 270.0,
        cape: String = "[100,200,300,400,500,600,700,800,900,1000,1100,1200]",
    ) = """
        {"latitude":51.5,"longitude":-0.08,
         "current":{"time":"2026-07-25T06:00","temperature_2m":$temperature,
           "precipitation":$precipitation,"cloud_cover":$cloud,"cloud_cover_low":$low,
           "cloud_cover_mid":$mid,"cloud_cover_high":$high,
           "wind_speed_10m":$windSpeed,"wind_direction_10m":$windDirection},
         "hourly":{"cape":$cape}}
    """.trimIndent()

    @Test
    fun `parses a multi location array`() {
        val json = "[" + (0 until 4).joinToString(",") { location() } + "]"
        val dataset = OpenMeteo.parse(json, bounds, 2, fixedNow)

        assertTrue(dataset.isValid)
        assertEquals("open-meteo", dataset.source)
        assertEquals(4, dataset.cells.size)
        assertEquals(2, dataset.gridWidth)
        assertEquals(3, dataset.layers.size)
        assertEquals("2026-07-25T06:00Z", dataset.observationTimeUtc)
    }

    @Test
    fun `parses a single location object`() {
        // A one-coordinate request returns a bare object rather than an array.
        val dataset = OpenMeteo.parse(location(), bounds, 1, fixedNow)
        assertEquals(1, dataset.cells.size)
    }

    @Test
    fun `converts percentages to fractions`() {
        val dataset = OpenMeteo.parse(location(cloud = 75, low = 50, mid = 25, high = 0), bounds, 1, fixedNow)
        val cell = dataset.cells[0]
        assertEquals(0.75f, cell.cloudTotal, 1e-4f)
        assertEquals(0.50f, cell.cloudLow, 1e-4f)
        assertEquals(0.25f, cell.cloudMid, 1e-4f)
        assertEquals(0f, cell.cloudHigh, 1e-4f)
    }

    @Test
    fun `converts wind from meteorological direction to components`() {
        // A "270°" wind blows FROM the west, so it moves air toward the east: +U, ~0 V.
        // Getting this backwards advects the cloud the wrong way across the map.
        val westerly = OpenMeteo.parse(location(windSpeed = 36.0, windDirection = 270.0), bounds, 1, fixedNow)
        val cell = westerly.cells[0]
        assertEquals(10f, cell.windU, 1e-3f, "36 km/h should be 10 m/s eastward")
        assertTrue(abs(cell.windV) < 1e-3f, "a due-westerly should have no northward component")

        // And a "180°" wind blows from the south, moving air north: +V.
        val southerly = OpenMeteo.parse(location(windSpeed = 36.0, windDirection = 180.0), bounds, 1, fixedNow)
        assertTrue(southerly.cells[0].windV > 9f, "southerly should push north")
    }

    @Test
    fun `indexes cape by the current utc hour`() {
        // The series starts at the day's midnight under timezone=GMT, so hour 6 is index 6.
        val dataset = OpenMeteo.parse(location(), bounds, 1, fixedNow)
        assertEquals(700f, dataset.cells[0].capeJkg, 1e-3f)
    }

    @Test
    fun `missing cape means no lightning rather than a broken dataset`() {
        val json = "[" + (0 until 4).joinToString(",") { location(cape = "[]") } + "]"
        val dataset = OpenMeteo.parse(json, bounds, 2, fixedNow)

        assertEquals(0f, dataset.cells[0].capeJkg)
        assertEquals(0f, dataset.cells[0].lightningPotential)
        // The rest of the snapshot still stands: no CAPE means no lightning, not no data.
        assertTrue(dataset.isValid, "absent CAPE must not invalidate the snapshot")
        assertTrue(dataset.cells[0].cloudTotal > 0f, "cloud should survive missing CAPE")
    }

    @Test
    fun `a single location is parsed but is not a samplable grid`() {
        // `isValid` requires gridWidth and gridHeight > 1, because a 1×1 grid cannot be
        // bilinearly sampled — there is nothing to interpolate between. So the
        // single-coordinate response parses fine and is still not usable as a map layer.
        // Worth pinning: it is the one case where a successful parse is not a usable
        // dataset, and a caller that only checks for an exception would render nothing.
        val dataset = OpenMeteo.parse(location(), bounds, 1, fixedNow)
        assertEquals(1, dataset.cells.size)
        assertTrue(!dataset.isValid, "a 1×1 grid should not claim to be a valid map layer")
    }

    @Test
    fun `lightning is derived from the parsed cape and rain`() {
        val dataset = OpenMeteo.parse(location(precipitation = 8.0), bounds, 1, fixedNow)
        val cell = dataset.cells[0]
        assertEquals(
            Atmosphere.lightningPotential(cell.capeJkg, cell.precipitationMmHr),
            cell.lightningPotential,
            1e-6f,
        )
    }

    @Test
    fun `negative precipitation is floored`() {
        val dataset = OpenMeteo.parse(location(precipitation = -3.0), bounds, 1, fixedNow)
        assertEquals(0f, dataset.cells[0].precipitationMmHr)
    }

    @Test
    fun `a missing current block yields an empty cell rather than a crash`() {
        val json = """[{"latitude":51.5,"longitude":-0.08}]"""
        val dataset = OpenMeteo.parse(json, bounds, 1, fixedNow)
        assertEquals(WeatherCell(), dataset.cells[0])
    }

    @Test
    fun `wrong location count is rejected`() {
        val json = "[" + location() + "]"
        val failure = assertFailsWith<OpenMeteoParseException> {
            OpenMeteo.parse(json, bounds, 3, fixedNow)
        }
        assertTrue(failure.message!!.contains("expected 9"), "should say how many it wanted")
    }

    @Test
    fun `garbage is rejected with a parse exception`() {
        assertFailsWith<OpenMeteoParseException> { OpenMeteo.parse("not json at all", bounds, 1, fixedNow) }
        assertFailsWith<OpenMeteoParseException> { OpenMeteo.parse("", bounds, 1, fixedNow) }
        assertFailsWith<OpenMeteoParseException> { OpenMeteo.parse("42", bounds, 1, fixedNow) }
    }
}

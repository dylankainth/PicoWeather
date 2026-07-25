package com.weathervr.core

import java.io.File
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The JSON schema is the contract with `tools/fetch_weather.py` and
 * `tools/fetch_buildings.py`. These tests read the **real baked files** wherever they
 * are present, because a schema test against a fixture you wrote yourself only proves
 * you are self-consistent.
 */
class WeatherJsonTest {

    private fun baked(name: String): File? = listOf(
        File("../../Assets/StreamingAssets/WeatherData/$name"),
        File("../Assets/StreamingAssets/WeatherData/$name"),
        File("Assets/StreamingAssets/WeatherData/$name"),
    ).firstOrNull { it.isFile }

    private val fixedNow: Instant = Instant.parse("2026-07-25T06:00:00Z")

    // ------------------------------------------------------------------ weather

    @Test
    fun `decodes the real baked weather json`() {
        val file = baked("weather.json")
        if (file == null) {
            println("SKIP: no baked weather.json; run tools/build_all.py to exercise this test")
            return
        }

        val dataset = WeatherJson.decodeWeather(file.readText())

        assertTrue(dataset.isValid, "baked weather.json did not decode into a valid grid")
        assertEquals(dataset.gridWidth * dataset.gridHeight, dataset.cells.size)
        assertEquals(3, dataset.layers.size, "expected low/mid/high layers")
        assertTrue(dataset.source.isNotEmpty())

        // Bounds should be the London region the rest of the payload covers.
        assertTrue(dataset.bounds.centerLatitude in 51.4..51.6, "unexpected latitude")
        assertTrue(dataset.bounds.centerLongitude in -0.2..0.05, "unexpected longitude")

        // Physical sanity on real values, not just structural decode.
        for (cell in dataset.cells) {
            assertTrue(cell.cloudTotal in 0f..1f, "cloudTotal out of range: ${cell.cloudTotal}")
            assertTrue(cell.precipitationMmHr >= 0f)
            assertTrue(cell.capeJkg >= 0f)
        }

        println("decoded ${dataset.describe()}")
    }

    @Test
    fun `weather round trips through json`() {
        val original = ProceduralWeather.generate(
            GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0),
            gridSize = 8,
            seed = 21,
            now = fixedNow,
        )
        val restored = WeatherJson.decodeWeather(WeatherJson.encodeWeather(original))
        assertEquals(original, restored, "weather dataset did not survive a JSON round trip")
    }

    @Test
    fun `layer altitudes survive the round trip`() {
        val original = ProceduralWeather.generate(
            GeoBounds.fromCenterSpan(0.0, 0.0, 5.0),
            gridSize = 4,
            seed = 1,
            now = fixedNow,
        )
        val restored = WeatherJson.decodeWeather(WeatherJson.encodeWeather(original))
        for (layer in Atmosphere.Layer.entries) {
            assertEquals(
                original.layerFor(layer).baseAltitudeM,
                restored.layerFor(layer).baseAltitudeM,
                0.01f,
                "$layer base altitude drifted",
            )
        }
    }

    @Test
    fun `unknown keys do not break decoding`() {
        // The bakers may add fields; a new key upstream must not fail on a headset.
        val json = """
            {"schemaVersion":1,"source":"open-meteo","gridWidth":2,"gridHeight":2,
             "minLatitude":51.0,"maxLatitude":52.0,"minLongitude":-1.0,"maxLongitude":0.0,
             "somethingNew":{"nested":true},
             "layers":[],"cells":[{"cloudTotal":0.5,"unexpected":7},{},{},{}]}
        """.trimIndent()
        val dataset = WeatherJson.decodeWeather(json)
        assertTrue(dataset.isValid)
        assertEquals(0.5f, dataset.cells[0].cloudTotal)
    }

    @Test
    fun `missing layers fall back to the standard bands`() {
        val dataset = WeatherDataset(gridWidth = 2, gridHeight = 2, cells = List(4) { WeatherCell() })
        val low = dataset.layerFor(Atmosphere.Layer.LOW)
        assertEquals(1000f, low.basePressureHpa)
        assertEquals("low", low.name)
    }

    @Test
    fun `malformed json returns null rather than throwing`() {
        assertNull(WeatherJson.decodeWeatherOrNull("{ this is not json"))
        assertNull(WeatherJson.decodeWeatherOrNull(null))
        // Structurally fine but not a valid grid: also null, so callers fall back.
        assertNull(WeatherJson.decodeWeatherOrNull("""{"gridWidth":0,"gridHeight":0}"""))
    }

    // ---------------------------------------------------------------- buildings

    @Test
    fun `decodes the real baked buildings json`() {
        val file = baked("buildings.json")
        if (file == null) {
            println("SKIP: no baked buildings.json; run tools/build_all.py to exercise this test")
            return
        }

        val dataset = WeatherJson.decodeBuildings(file.readText())

        assertTrue(dataset.isValid)
        assertTrue(dataset.buildings.isNotEmpty(), "expected real OSM buildings")

        var withHeight = 0
        for (building in dataset.buildings) {
            assertTrue(building.pointCount >= 3, "a footprint with ${building.pointCount} points is not a polygon")
            assertTrue(building.footprintFlat.size % 2 == 0, "footprint is not lat/lon pairs")
            assertTrue(building.heightMeters >= 0f, "negative height ${building.heightMeters}")
            assertTrue(building.heightMeters < 400f, "implausible height ${building.heightMeters} m")
            if (building.heightMeters > 0f) withHeight++
        }
        assertTrue(withHeight > dataset.buildings.size / 2, "most buildings should have a height")

        println("decoded ${dataset.describe()}")
    }

    @Test
    fun `buildings round trip through json`() {
        val original = ProceduralBuildings.generate(GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0), seed = 3)
        val restored = WeatherJson.decodeBuildings(WeatherJson.encodeBuildings(original))
        assertEquals(original, restored, "building dataset did not survive a JSON round trip")
    }

    @Test
    fun `an empty building list is still valid`() {
        // "No buildings here" must be distinguishable from "no data", or a genuinely
        // empty query would silently fall back to procedural blocks.
        val json = """
            {"schemaVersion":1,"source":"openstreetmap-overpass","minLatitude":51.0,
             "maxLatitude":52.0,"minLongitude":-1.0,"maxLongitude":0.0,"buildings":[]}
        """.trimIndent()
        val dataset = WeatherJson.decodeBuildings(json)
        assertTrue(dataset.isValid, "an empty-but-well-formed dataset must stay valid")
        assertEquals(0, dataset.buildings.size)
        assertNotNull(WeatherJson.decodeBuildingsOrNull(json))
    }
}

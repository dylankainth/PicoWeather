package com.weathervr.core

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The fallback policy: live, then baked, then procedural, per layer independently.
 *
 * This is the behaviour that keeps the app from ever being a black screen, and every
 * branch of it is reachable here because the I/O sits behind [AssetSource] and
 * [HttpFetcher] rather than behind a coroutine and a web request.
 */
class WeatherDataServiceTest {

    private val bounds = GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0)
    private val fixedNow: Instant = Instant.parse("2026-07-25T06:00:00Z")

    /** An asset source backed by a map, so a test can supply exactly one file. */
    private class FakeAssets(private val files: Map<String, ByteArray> = emptyMap()) : AssetSource {
        override fun readBytes(name: String): ByteArray? = files[name]
    }

    private fun bakedWeatherJson(): String = WeatherJson.encodeWeather(
        ProceduralWeather.generate(bounds, 8, seed = 1, now = fixedNow).copy(source = "era5"),
    )

    private fun bakedBuildingsJson(): String = WeatherJson.encodeBuildings(
        ProceduralBuildings.generate(bounds, seed = 1).copy(source = "openstreetmap-overpass"),
    )

    private fun openMeteoResponse(count: Int): String = "[" + (0 until count).joinToString(",") {
        """
        {"latitude":51.5,"longitude":-0.08,
         "current":{"time":"2026-07-25T06:00","temperature_2m":18,"precipitation":0.4,
           "cloud_cover":60,"cloud_cover_low":40,"cloud_cover_mid":20,"cloud_cover_high":10,
           "wind_speed_10m":18,"wind_direction_10m":225},
         "hourly":{"cape":[0,0,0,0,0,0,0,120,0,0,0,0]}}
        """.trimIndent()
    } + "]"

    // ---------------------------------------------------------------- everything absent

    @Test
    fun `with nothing available every layer is procedural`() {
        val service = WeatherDataService(FakeAssets())
        val snapshot = service.load(bounds, weatherGridSize = 8, terrainResolution = 64, now = fixedNow)

        assertEquals(DataOrigin.PROCEDURAL, snapshot.terrain.origin)
        assertEquals(DataOrigin.PROCEDURAL, snapshot.weather.origin)
        assertEquals(DataOrigin.PROCEDURAL, snapshot.buildings.origin)

        // And it is still a usable snapshot, which is the entire point.
        assertTrue(snapshot.weather.value.isValid)
        assertTrue(snapshot.buildings.value.buildings.isNotEmpty())
        assertTrue(snapshot.terrain.value.width >= 32)
    }

    // ------------------------------------------------------------------------ baked

    @Test
    fun `baked files are preferred over procedural`() {
        val assets = FakeAssets(
            mapOf(
                WeatherDataService.WEATHER_FILE to bakedWeatherJson().toByteArray(),
                WeatherDataService.BUILDINGS_FILE to bakedBuildingsJson().toByteArray(),
            ),
        )
        val snapshot = WeatherDataService(assets).load(bounds, 8, 64, fixedNow)

        assertEquals(DataOrigin.BAKED, snapshot.weather.origin)
        assertEquals("era5", snapshot.weather.detail)
        assertEquals(DataOrigin.BAKED, snapshot.buildings.origin)
        assertEquals("openstreetmap-overpass", snapshot.buildings.detail)
        // Terrain had no baked file, so it independently falls back.
        assertEquals(DataOrigin.PROCEDURAL, snapshot.terrain.origin)
    }

    @Test
    fun `baked terrain is decoded`() {
        val field = ProceduralTerrain.generate(bounds, 64, seed = 2)
        val assets = FakeAssets(mapOf(WeatherDataService.TERRAIN_FILE to field.toBytes()))
        val snapshot = WeatherDataService(assets).load(bounds, 8, 64, fixedNow)

        assertEquals(DataOrigin.BAKED, snapshot.terrain.origin)
        assertEquals(64, snapshot.terrain.value.width)
    }

    @Test
    fun `a corrupt baked file falls back instead of throwing`() {
        val assets = FakeAssets(
            mapOf(
                WeatherDataService.TERRAIN_FILE to "not a heightfield".toByteArray(),
                WeatherDataService.WEATHER_FILE to "{ truncated".toByteArray(),
                WeatherDataService.BUILDINGS_FILE to "[]".toByteArray(),
            ),
        )
        val snapshot = WeatherDataService(assets).load(bounds, 8, 64, fixedNow)

        assertEquals(DataOrigin.PROCEDURAL, snapshot.terrain.origin)
        assertEquals(DataOrigin.PROCEDURAL, snapshot.weather.origin)
        assertEquals(DataOrigin.PROCEDURAL, snapshot.buildings.origin)
    }

    @Test
    fun `an empty but valid building file is not overridden`() {
        // "No buildings here" is a real answer and must survive; only absent or
        // unparseable data falls back to procedural blocks.
        val emptyButReal = BuildingDataset(
            source = "openstreetmap-overpass",
            minLatitude = bounds.minLatitude, maxLatitude = bounds.maxLatitude,
            minLongitude = bounds.minLongitude, maxLongitude = bounds.maxLongitude,
        )
        val assets = FakeAssets(
            mapOf(WeatherDataService.BUILDINGS_FILE to WeatherJson.encodeBuildings(emptyButReal).toByteArray()),
        )
        val snapshot = WeatherDataService(assets).load(bounds, 8, 64, fixedNow)

        assertEquals(DataOrigin.BAKED, snapshot.buildings.origin)
        assertEquals(0, snapshot.buildings.value.buildings.size)
    }

    // ------------------------------------------------------------------------- live

    @Test
    fun `live data wins over baked`() {
        val assets = FakeAssets(mapOf(WeatherDataService.WEATHER_FILE to bakedWeatherJson().toByteArray()))
        val service = WeatherDataService(assets, http = { openMeteoResponse(64) })
        val snapshot = service.load(bounds, weatherGridSize = 8, terrainResolution = 64, now = fixedNow)

        assertEquals(DataOrigin.LIVE, snapshot.weather.origin)
        assertEquals("Open-Meteo", snapshot.weather.detail)
        assertEquals(0.6f, snapshot.weather.value.cells[0].cloudTotal, 1e-4f)
    }

    @Test
    fun `a failing network falls through to baked`() {
        val assets = FakeAssets(mapOf(WeatherDataService.WEATHER_FILE to bakedWeatherJson().toByteArray()))
        val service = WeatherDataService(assets, http = { null })
        val snapshot = service.load(bounds, 8, 64, fixedNow)
        assertEquals(DataOrigin.BAKED, snapshot.weather.origin)
    }

    @Test
    fun `a throwing fetcher does not take the app down`() {
        // HttpFetcher is documented as "must not throw", but the app must survive one
        // that does — this is the layer whose entire job is to never fail.
        val service = WeatherDataService(FakeAssets(), http = { error("network on fire") })
        val snapshot = service.load(bounds, 8, 64, fixedNow)
        assertEquals(DataOrigin.PROCEDURAL, snapshot.weather.origin)
    }

    @Test
    fun `a garbled live response falls through rather than rendering nonsense`() {
        val assets = FakeAssets(mapOf(WeatherDataService.WEATHER_FILE to bakedWeatherJson().toByteArray()))
        val service = WeatherDataService(assets, http = { "<html>502 Bad Gateway</html>" })
        val snapshot = service.load(bounds, 8, 64, fixedNow)
        assertEquals(DataOrigin.BAKED, snapshot.weather.origin)
    }

    @Test
    fun `a live response of the wrong size is rejected`() {
        // Right shape, wrong count — the most plausible way for a live fetch to be
        // subtly wrong rather than obviously broken.
        val service = WeatherDataService(FakeAssets(), http = { openMeteoResponse(9) })
        val snapshot = service.load(bounds, weatherGridSize = 8, terrainResolution = 64, now = fixedNow)
        assertEquals(DataOrigin.PROCEDURAL, snapshot.weather.origin)
    }

    // ------------------------------------------------------------------ provenance

    @Test
    fun `provenance reads like the label the app shows`() {
        val snapshot = WeatherDataService(FakeAssets()).load(bounds, 8, 64, fixedNow)
        val description = snapshot.describe()

        assertTrue(description.contains("procedural"), "provenance must admit what is synthetic: $description")
        assertTrue(description.contains("terrain:"))
        assertTrue(description.contains("weather:"))
        assertTrue(description.contains("buildings:"))
        println(description)
    }

    @Test
    fun `layers resolve independently`() {
        // The mixed case is the normal one on device: real terrain and buildings from
        // the bake, live weather from the network.
        val assets = FakeAssets(
            mapOf(
                WeatherDataService.TERRAIN_FILE to ProceduralTerrain.generate(bounds, 64, 3).toBytes(),
                WeatherDataService.BUILDINGS_FILE to bakedBuildingsJson().toByteArray(),
            ),
        )
        val service = WeatherDataService(assets, http = { openMeteoResponse(64) })
        val snapshot = service.load(bounds, 8, 64, fixedNow)

        assertEquals(DataOrigin.BAKED, snapshot.terrain.origin)
        assertEquals(DataOrigin.LIVE, snapshot.weather.origin)
        assertEquals(DataOrigin.BAKED, snapshot.buildings.origin)
    }
}

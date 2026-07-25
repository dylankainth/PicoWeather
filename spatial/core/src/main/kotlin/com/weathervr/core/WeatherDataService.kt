package com.weathervr.core

import java.time.Instant

/**
 * Where a layer's data actually came from. Surfaced in the app's provenance label,
 * because roughly half of what the app renders is inferred and it says so on screen.
 */
enum class DataOrigin { LIVE, BAKED, PROCEDURAL }

/** One resolved layer plus its provenance. */
data class SourcedLayer<T>(val value: T, val origin: DataOrigin, val detail: String) {
    /** e.g. "SRTM/AWS terrarium (baked)". */
    fun describe(): String = "$detail (${origin.name.lowercase()})"
}

/** Everything the renderer needs for one snapshot. */
data class WeatherSnapshot(
    val bounds: GeoBounds,
    val terrain: SourcedLayer<TerrainHeightfield>,
    val weather: SourcedLayer<WeatherDataset>,
    val buildings: SourcedLayer<BuildingDataset>,
) {
    /** The one-line provenance string the Unity app logs and shows on the panel. */
    fun describe(): String =
        "terrain: ${terrain.describe()} · weather: ${weather.describe()} · buildings: ${buildings.describe()}"
}

/**
 * Reads the baked payload out of wherever the platform keeps it.
 *
 * An interface rather than a concrete reader because this is the seam between logic and
 * platform: on Android it is `AssetManager`, in these tests it is a map in memory, and
 * in the Unity version it was `StreamingAssets` behind a `jar:file://` URL that only
 * `UnityWebRequest` could open. Returning null for a missing file is the expected case,
 * not an error — every layer has a fallback.
 */
interface AssetSource {
    fun readBytes(name: String): ByteArray?
    fun readText(name: String): String? = readBytes(name)?.toString(Charsets.UTF_8)
}

/** Fetches a URL. Separate from [AssetSource] because the network is allowed to be slow and to fail. */
fun interface HttpFetcher {
    /** Returns the body, or null on any failure. Must not throw. */
    fun get(url: String): String?
}

/**
 * Resolves each layer by trying live data, then the baked payload, then procedural
 * generation.
 *
 * **The policy is the whole point of this class, and it is nine lines.** In the Unity
 * version the same nine lines were spread across 261 lines of coroutines, callbacks and
 * result objects, all of which existed to work around `UnityWebRequest` being
 * asynchronous and non-throwing. None of that machinery is logic, and none of it
 * survived the port: here the sequencing is a plain `?:` chain, and the caller decides
 * what thread it runs on.
 *
 * Every step degrades rather than failing. The app must always render something — that
 * is a hackathon-demo safety net, and it is also why the provenance label exists, since
 * "it drew a city" and "it drew *the* city" have to be distinguishable on screen.
 */
class WeatherDataService(
    private val assets: AssetSource,
    private val http: HttpFetcher? = null,
    private val seed: Int = 20260725,
) {
    fun load(
        bounds: GeoBounds,
        weatherGridSize: Int = 12,
        terrainResolution: Int = 512,
        now: Instant = Instant.now(),
    ): WeatherSnapshot = WeatherSnapshot(
        bounds = bounds,
        terrain = loadTerrain(bounds, terrainResolution),
        weather = loadWeather(bounds, weatherGridSize, now),
        buildings = loadBuildings(bounds),
    )

    // ------------------------------------------------------------------ terrain

    fun loadTerrain(bounds: GeoBounds, resolution: Int): SourcedLayer<TerrainHeightfield> {
        val baked = runCatching { assets.readBytes(TERRAIN_FILE)?.let { TerrainHeightfield.fromBytes(it) } }
            .getOrNull()

        return if (baked != null) {
            SourcedLayer(baked, DataOrigin.BAKED, "SRTM/AWS terrarium")
        } else {
            SourcedLayer(
                ProceduralTerrain.generate(bounds, resolution, seed),
                DataOrigin.PROCEDURAL,
                "synthesised relief",
            )
        }
    }

    // ------------------------------------------------------------------ weather

    fun loadWeather(bounds: GeoBounds, gridSize: Int, now: Instant): SourcedLayer<WeatherDataset> {
        live(bounds, gridSize, now)?.let {
            return SourcedLayer(it, DataOrigin.LIVE, "Open-Meteo")
        }

        WeatherJson.decodeWeatherOrNull(assets.readText(WEATHER_FILE))?.let {
            return SourcedLayer(it, DataOrigin.BAKED, it.source)
        }

        return SourcedLayer(
            ProceduralWeather.generate(bounds, gridSize, seed, now = now),
            DataOrigin.PROCEDURAL,
            "synthetic squall line",
        )
    }

    private fun live(bounds: GeoBounds, gridSize: Int, now: Instant): WeatherDataset? {
        val fetcher = http ?: return null
        val body = runCatching { fetcher.get(OpenMeteo.buildUrl(bounds, gridSize)) }.getOrNull() ?: return null
        return runCatching { OpenMeteo.parse(body, bounds, gridSize.clampTo(2, 12), now) }
            .getOrNull()
            ?.takeIf { it.isValid }
    }

    // ---------------------------------------------------------------- buildings

    fun loadBuildings(bounds: GeoBounds): SourcedLayer<BuildingDataset> {
        WeatherJson.decodeBuildingsOrNull(assets.readText(BUILDINGS_FILE))?.let {
            // An empty list from a real query is a real answer — somewhere with no
            // buildings — and must not be replaced with procedural blocks. Only absent
            // or unparseable data falls back.
            return SourcedLayer(it, DataOrigin.BAKED, it.source)
        }

        return SourcedLayer(
            ProceduralBuildings.generate(bounds, seed),
            DataOrigin.PROCEDURAL,
            "city block layout",
        )
    }

    companion object {
        const val TERRAIN_FILE = "terrain.bin"
        const val WEATHER_FILE = "weather.json"
        const val BUILDINGS_FILE = "buildings.json"
        const val SATELLITE_FILE = "satellite.jpg"
    }
}

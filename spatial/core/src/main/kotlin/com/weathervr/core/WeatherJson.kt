package com.weathervr.core

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/** The timestamp format both the Python baker and the C# runtime write. */
object Iso8601 {
    private val FORMATTER: DateTimeFormatter =
        DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss'Z'").withZone(ZoneOffset.UTC)

    fun format(instant: Instant): String = FORMATTER.format(instant)
}

/**
 * Reading and writing the baked JSON payload.
 *
 * The schema is whatever `tools/fetch_weather.py` and `tools/fetch_buildings.py` emit,
 * which in turn was whatever Unity's `JsonUtility` could round-trip. The domain classes
 * already carry those exact field names, so they deserialise directly with no DTO layer
 * in between — verified against the real baked files in `WeatherJsonTest`.
 *
 * `ignoreUnknownKeys` is on deliberately: the bakers may add fields (they already write
 * a `manifest.json` this module does not model), and a new key upstream should not turn
 * into a hard failure on a headset.
 */
object WeatherJson {

    val format = Json {
        ignoreUnknownKeys = true
        isLenient = true
        encodeDefaults = true
        explicitNulls = false
    }

    fun decodeWeather(json: String): WeatherDataset = format.decodeFromString(json)

    fun encodeWeather(dataset: WeatherDataset): String = format.encodeToString(dataset)

    fun decodeBuildings(json: String): BuildingDataset = format.decodeFromString(json)

    fun encodeBuildings(dataset: BuildingDataset): String = format.encodeToString(dataset)

    /**
     * Decodes, returning null instead of throwing.
     *
     * Every consumer of this data has a procedural fallback, so an unreadable file is a
     * recoverable condition rather than an error — the same reasoning as the C#
     * `StreamingDataReader.Result`, which reported failure without raising.
     */
    fun decodeWeatherOrNull(json: String?): WeatherDataset? = runCatching {
        json?.let { decodeWeather(it) }?.takeIf { it.isValid }
    }.getOrNull()

    fun decodeBuildingsOrNull(json: String?): BuildingDataset? = runCatching {
        json?.let { decodeBuildings(it) }?.takeIf { it.isValid }
    }.getOrNull()
}

package com.weathervr.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import java.time.Instant
import java.time.ZoneOffset
import java.util.Locale
import kotlin.math.cos
import kotlin.math.sin

/**
 * Live weather ingest from Open-Meteo (https://open-meteo.com).
 *
 * The specification calls for ERA5 via the Copernicus CDS. ERA5 needs a registered API
 * key and its requests are queued asynchronously — a fetch can take minutes to hours,
 * which cannot happen inside an app launch. Open-Meteo is key-less, synchronous, and
 * exposes the same physical variables, so it is the live path. The offline baker can
 * still pull genuine ERA5 for anyone with CDS credentials, and both writers emit the
 * identical [WeatherDataset] schema.
 *
 * **This object does no networking.** The C# version wrapped `UnityWebRequest`, which
 * is exactly the kind of engine coupling that made the data layer unportable. Here the
 * URL construction and the response parsing — the parts with logic worth testing — are
 * pure functions, and fetching the bytes is the caller's problem. The app module can
 * use OkHttp, `HttpURLConnection`, or whatever the Spatial SDK prefers.
 */
object OpenMeteo {

    const val ENDPOINT = "https://api.open-meteo.com/v1/forecast"

    const val ATTRIBUTION =
        "Weather data by Open-Meteo.com (CC BY 4.0), ECMWF/DWD/NOAA source models."

    private const val CURRENT_VARIABLES =
        "temperature_2m,precipitation,cloud_cover,cloud_cover_low,cloud_cover_mid," +
            "cloud_cover_high,wind_speed_10m,wind_direction_10m"

    /**
     * One request covers the whole map: Open-Meteo accepts comma-separated coordinate
     * lists and returns an array of per-location results.
     *
     * Grid size is clamped to 12 to keep the URL and the API load sane — the same limit
     * the C# used.
     */
    fun buildUrl(bounds: GeoBounds, gridSize: Int): String {
        val size = gridSize.clampTo(2, 12)

        val lats = StringBuilder()
        val lons = StringBuilder()

        for (y in 0 until size) {
            val lat = bounds.minLatitude + y / (size - 1).toDouble() * bounds.latitudeSpan
            for (x in 0 until size) {
                val lon = bounds.minLongitude + x / (size - 1).toDouble() * bounds.longitudeSpan
                if (lats.isNotEmpty()) {
                    lats.append(',')
                    lons.append(',')
                }
                // Invariant culture, explicitly: a comma decimal separator would turn the
                // coordinate list into twice as many coordinates.
                lats.append(String.format(Locale.ROOT, "%.4f", lat))
                lons.append(String.format(Locale.ROOT, "%.4f", lon))
            }
        }

        return "$ENDPOINT?latitude=$lats&longitude=$lons" +
            "&current=$CURRENT_VARIABLES&hourly=cape&forecast_days=1&timezone=GMT"
    }

    /**
     * Parses a response into a dataset covering [bounds].
     *
     * @throws OpenMeteoParseException if the response is not a grid of the expected size.
     */
    fun parse(
        json: String,
        bounds: GeoBounds,
        gridSize: Int,
        now: Instant = Instant.now(),
    ): WeatherDataset {
        if (json.isBlank()) throw OpenMeteoParseException("empty response")

        val element: JsonElement = try {
            WeatherJson.format.parseToJsonElement(json)
        } catch (e: Exception) {
            throw OpenMeteoParseException("response is not JSON: ${e.message}")
        }

        // A multi-coordinate request returns a bare array; a single-coordinate request
        // returns a plain object. The C# had to wrap the array in an object because
        // JsonUtility cannot deserialise a top-level array; here both shapes decode
        // directly.
        val locations: List<Location> = try {
            when (element) {
                is JsonArray -> WeatherJson.format.decodeFromJsonElement(
                    kotlinx.serialization.builtins.ListSerializer(Location.serializer()),
                    element,
                )
                is JsonObject -> listOf(
                    WeatherJson.format.decodeFromJsonElement(Location.serializer(), element),
                )
                else -> throw OpenMeteoParseException("response is neither an object nor an array")
            }
        } catch (e: OpenMeteoParseException) {
            throw e
        } catch (e: Exception) {
            throw OpenMeteoParseException("could not decode locations: ${e.message}")
        }

        // Not clamped to the 2..12 range [buildUrl] uses: that clamp exists to keep the
        // request URL sane, whereas here the caller is stating what the response is
        // supposed to contain. Clamping would make a legitimate single-coordinate
        // response (Open-Meteo returns a bare object for one location) fail as
        // "expected 4, got 1".
        val size = gridSize.coerceAtLeast(1)
        val expected = size * size
        if (locations.size != expected) {
            throw OpenMeteoParseException("expected $expected locations, got ${locations.size}")
        }

        val observationTime = locations.firstOrNull()?.current?.time
        val timestamp = Iso8601.format(now)

        return WeatherDataset(
            source = "open-meteo",
            attribution = ATTRIBUTION,
            // Open-Meteo returns local-to-the-request time without a zone suffix; the
            // request pins timezone=GMT, so appending Z is correct rather than hopeful.
            observationTimeUtc = if (observationTime.isNullOrEmpty()) timestamp else observationTime + "Z",
            generatedUtc = timestamp,
            minLatitude = bounds.minLatitude,
            maxLatitude = bounds.maxLatitude,
            minLongitude = bounds.minLongitude,
            maxLongitude = bounds.maxLongitude,
            gridWidth = size,
            gridHeight = size,
            layers = WeatherLayerBand.defaults(),
            cells = locations.map { toCell(it, now) },
        )
    }

    private fun toCell(location: Location, now: Instant): WeatherCell {
        val current = location.current ?: return WeatherCell()

        val precipitation = maxOf(0f, current.precipitation)
        val cape = firstCape(location, now)

        // Open-Meteo reports wind as speed (km/h) + meteorological direction, i.e. the
        // direction the wind blows *from*. Convert to the eastward/northward components
        // the rest of the app advects cloud with.
        val speedMs = maxOf(0f, current.windSpeed10m) / 3.6f
        val fromRad = current.windDirection10m * DEG_TO_RAD

        return WeatherCell(
            cloudTotal = (current.cloudCover / 100f).clamp01(),
            cloudLow = (current.cloudCoverLow / 100f).clamp01(),
            cloudMid = (current.cloudCoverMid / 100f).clamp01(),
            cloudHigh = (current.cloudCoverHigh / 100f).clamp01(),
            precipitationMmHr = precipitation,
            capeJkg = cape,
            lightningPotential = Atmosphere.lightningPotential(cape, precipitation),
            temperatureC = current.temperature2m,
            windU = -speedMs * sin(fromRad),
            windV = -speedMs * cos(fromRad),
        )
    }

    /**
     * CAPE only comes back on the hourly series, so take the hour matching "now" — with
     * `forecast_days=1` and a GMT timezone the series starts at the current day's
     * midnight, so the UTC hour indexes it directly. Missing CAPE degrades to zero,
     * which simply means no lightning rather than a broken dataset.
     */
    private fun firstCape(location: Location, now: Instant): Float {
        val cape = location.hourly?.cape ?: return 0f
        if (cape.isEmpty()) return 0f

        val hour = now.atZone(ZoneOffset.UTC).hour
        val index = hour.clampTo(0, cape.size - 1)
        return maxOf(0f, cape[index])
    }

    // ------------------------------------------------------ response schema
    // Only the fields we consume are declared; unknown keys are ignored.

    @Serializable
    data class Location(
        val latitude: Double = 0.0,
        val longitude: Double = 0.0,
        val current: Current? = null,
        val hourly: Hourly? = null,
    )

    @Serializable
    data class Current(
        val time: String? = null,
        @SerialName("temperature_2m") val temperature2m: Float = 0f,
        val precipitation: Float = 0f,
        @SerialName("cloud_cover") val cloudCover: Float = 0f,
        @SerialName("cloud_cover_low") val cloudCoverLow: Float = 0f,
        @SerialName("cloud_cover_mid") val cloudCoverMid: Float = 0f,
        @SerialName("cloud_cover_high") val cloudCoverHigh: Float = 0f,
        @SerialName("wind_speed_10m") val windSpeed10m: Float = 0f,
        @SerialName("wind_direction_10m") val windDirection10m: Float = 0f,
    )

    @Serializable
    data class Hourly(
        val time: List<String>? = null,
        val cape: List<Float>? = null,
    )
}

class OpenMeteoParseException(message: String) : RuntimeException(message)

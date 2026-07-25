package com.weathervr.core

import kotlinx.serialization.Serializable

/**
 * One grid cell of the weather model. Field names mirror the variable names used by
 * both ERA5 and Open-Meteo, and match the JSON keys the Python baker writes, so the
 * same `weather.json` feeds this build and the Unity one.
 */
@Serializable
data class WeatherCell(
    /** Total cloud cover, 0..1. */
    val cloudTotal: Float = 0f,
    /** Cloud cover below ~2 km, 0..1. */
    val cloudLow: Float = 0f,
    /** Cloud cover ~2–6 km, 0..1. */
    val cloudMid: Float = 0f,
    /** Cloud cover above ~6 km, 0..1. */
    val cloudHigh: Float = 0f,
    /** Surface precipitation rate, mm/hour. */
    val precipitationMmHr: Float = 0f,
    /** Convective available potential energy, J/kg. */
    val capeJkg: Float = 0f,
    /** Derived 0..1 lightning likelihood (see [Atmosphere.lightningPotential]). */
    val lightningPotential: Float = 0f,
    /** 2 m air temperature, °C. */
    val temperatureC: Float = 0f,
    /** Eastward wind component at 10 m, m/s. */
    val windU: Float = 0f,
    /** Northward wind component at 10 m, m/s. */
    val windV: Float = 0f,
) {
    fun cloudCoverFor(layer: Atmosphere.Layer): Float = when (layer) {
        Atmosphere.Layer.LOW -> cloudLow
        Atmosphere.Layer.MID -> cloudMid
        Atmosphere.Layer.HIGH -> cloudHigh
    }
}

/**
 * Altitude extent of one cloud layer, resolved at bake time so the runtime does not
 * have to redo the barometric conversion.
 */
@Serializable
data class WeatherLayerBand(
    val name: String,
    val basePressureHpa: Float,
    val topPressureHpa: Float,
    val baseAltitudeM: Float,
    val topAltitudeM: Float,
) {
    companion object {
        fun default(layer: Atmosphere.Layer) = WeatherLayerBand(
            name = layer.name.lowercase(),
            basePressureHpa = layer.basePressureHpa.toFloat(),
            topPressureHpa = layer.topPressureHpa.toFloat(),
            baseAltitudeM = layer.baseAltitudeMeters.toFloat(),
            topAltitudeM = layer.topAltitudeMeters.toFloat(),
        )

        fun defaults(): List<WeatherLayerBand> = Atmosphere.Layer.entries.map { default(it) }
    }
}

/**
 * The full weather snapshot: a regular lat/lon grid of [WeatherCell] covering the map
 * region, plus provenance.
 *
 * The C# original was shaped by `JsonUtility`'s limitations — no dictionaries, no
 * jagged arrays, no nullable value types, and public mutable fields. None of that
 * applies here, but the *wire format* is unchanged, so the shape is kept: cells are
 * stored row-major with `index = y * gridWidth + x`, x running west→east and y
 * south→north.
 */
@Serializable
data class WeatherDataset(
    val schemaVersion: Int = CURRENT_SCHEMA_VERSION,
    /** ISO-8601 UTC timestamp of the observation/analysis time. */
    val observationTimeUtc: String? = null,
    /** ISO-8601 UTC timestamp of when this file was baked. */
    val generatedUtc: String? = null,
    /** "open-meteo", "era5", or "procedural". */
    val source: String = "procedural",
    /** Human-readable attribution shown in the app's about panel. */
    val attribution: String = "",
    val minLatitude: Double = 0.0,
    val maxLatitude: Double = 0.0,
    val minLongitude: Double = 0.0,
    val maxLongitude: Double = 0.0,
    val gridWidth: Int = 0,
    val gridHeight: Int = 0,
    val layers: List<WeatherLayerBand> = emptyList(),
    val cells: List<WeatherCell> = emptyList(),
) {
    val bounds: GeoBounds
        get() = GeoBounds(minLatitude, maxLatitude, minLongitude, maxLongitude)

    val isValid: Boolean
        get() = gridWidth > 1 && gridHeight > 1 &&
            cells.size == gridWidth * gridHeight &&
            maxLatitude > minLatitude && maxLongitude > minLongitude

    fun cellAt(x: Int, y: Int): WeatherCell {
        val cx = x.clampTo(0, gridWidth - 1)
        val cy = y.clampTo(0, gridHeight - 1)
        return cells[cy * gridWidth + cx]
    }

    /**
     * Bilinearly samples a per-cell scalar at normalised map coordinates. The grid is
     * treated as samples at cell centres, so a 12×12 grid spans u,v ∈ [0,1] with the
     * first sample at u = 0.
     */
    fun sampleBilinear(u: Float, v: Float, selector: (WeatherCell) -> Float): Float {
        if (!isValid) return 0f

        val fx = u.clamp01() * (gridWidth - 1)
        val fy = v.clamp01() * (gridHeight - 1)

        val x0 = floorToInt(fx)
        val y0 = floorToInt(fy)
        val x1 = minOf(x0 + 1, gridWidth - 1)
        val y1 = minOf(y0 + 1, gridHeight - 1)

        val tx = fx - x0
        val ty = fy - y0

        val c00 = selector(cellAt(x0, y0))
        val c10 = selector(cellAt(x1, y0))
        val c01 = selector(cellAt(x0, y1))
        val c11 = selector(cellAt(x1, y1))

        return lerp(lerp(c00, c10, tx), lerp(c01, c11, tx), ty)
    }

    fun layerFor(layer: Atmosphere.Layer): WeatherLayerBand =
        layers.getOrNull(layer.ordinal) ?: WeatherLayerBand.default(layer)

    /** Summary line for the in-app provenance label. */
    fun describe(): String {
        val when0 = observationTimeUtc?.takeIf { it.isNotEmpty() } ?: "unknown time"
        return "$source · ${gridWidth}×$gridHeight grid · $when0"
    }

    companion object {
        const val CURRENT_SCHEMA_VERSION = 1
    }
}

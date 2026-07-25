package com.weathervr.core

import kotlinx.serialization.Serializable

/**
 * One building: a flat-roofed extrusion of a ground footprint.
 *
 * The footprint stays flat (`[lat0, lon0, lat1, lon1, ...]`) rather than becoming a
 * list of points. In the C# that was forced — `JsonUtility` cannot deserialise a jagged
 * array at all — and Kotlin has no such limit, but the *file format* is the shared
 * contract with `tools/fetch_buildings.py`, so the wire shape is preserved. [points]
 * gives the ergonomic view without changing what is on disk.
 */
@Serializable
data class BuildingRecord(
    /** True height above its own footprint, metres. Not an altitude. */
    val heightMeters: Float = 0f,
    /** Footprint ring, counter-clockwise, as [lat0, lon0, lat1, lon1, ...]. */
    val footprintFlat: DoubleArray = DoubleArray(0),
) {
    val pointCount: Int get() = footprintFlat.size / 2

    fun latitudeAt(i: Int): Double = footprintFlat[i * 2]
    fun longitudeAt(i: Int): Double = footprintFlat[i * 2 + 1]

    /** The footprint as points, in ring order. */
    val points: List<GeoPoint>
        get() = (0 until pointCount).map { GeoPoint(latitudeAt(it), longitudeAt(it)) }

    // DoubleArray in a data class breaks equals/hashCode by identity; spell them out so
    // two datasets read from the same file compare equal, which the tests rely on.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BuildingRecord) return false
        return heightMeters == other.heightMeters && footprintFlat.contentEquals(other.footprintFlat)
    }

    override fun hashCode(): Int = 31 * heightMeters.hashCode() + footprintFlat.contentHashCode()
}

/** Every building footprint covering the map region, plus provenance. */
@Serializable
data class BuildingDataset(
    val schemaVersion: Int = CURRENT_SCHEMA_VERSION,
    /** "openstreetmap-overpass" or "procedural". */
    val source: String = "procedural",
    val attribution: String = "",
    val generatedUtc: String? = null,
    val minLatitude: Double = 0.0,
    val maxLatitude: Double = 0.0,
    val minLongitude: Double = 0.0,
    val maxLongitude: Double = 0.0,
    val buildings: List<BuildingRecord> = emptyList(),
) {
    val bounds: GeoBounds
        get() = GeoBounds(minLatitude, maxLatitude, minLongitude, maxLongitude)

    /**
     * Valid means parseable, not populated: an empty building list for a genuinely
     * building-free query is still a valid dataset, so the renderer can tell "no data"
     * from "no buildings here" and only the former falls back to procedural generation.
     */
    val isValid: Boolean
        get() = maxLatitude > minLatitude && maxLongitude > minLongitude

    fun describe(): String = "$source · ${buildings.size} building(s)"

    companion object {
        const val CURRENT_SCHEMA_VERSION = 1
    }
}

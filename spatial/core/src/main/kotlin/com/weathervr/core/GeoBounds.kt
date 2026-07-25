package com.weathervr.core

import kotlin.math.abs
import kotlin.math.cos

/**
 * A point in the map's local space.
 *
 * The Unity original returned `Vector3`/`Vector2` here. Nothing about this maths
 * needs an engine type, and depending on one is exactly what made the C# data
 * layer non-portable, so the port carries its own.
 *
 * X runs east, Z runs north, both in [-0.5, 0.5]; Y is up, in the *same* units.
 * See [MapScale] for why that last point matters.
 */
data class MapPoint(val x: Float, val y: Float, val z: Float)

/** Normalised map coordinates: u west→east, v south→north, both nominally in [0,1]. */
data class MapUv(val u: Float, val v: Float)

/** A geographic point. */
data class GeoPoint(val latitude: Double, val longitude: Double)

/**
 * An axis-aligned latitude/longitude rectangle, plus the conversions between
 * geographic coordinates and the map's local space.
 *
 * Local map space is normalised to [-0.5, 0.5] on X (east) and Z (north), with the
 * map plane at Y = 0. Multiplying by the map size in metres gives real metres, so
 * the same local coordinates work regardless of how big the map is drawn.
 */
data class GeoBounds(
    val minLatitude: Double,
    val maxLatitude: Double,
    val minLongitude: Double,
    val maxLongitude: Double,
) {
    val centerLatitude: Double get() = (minLatitude + maxLatitude) * 0.5
    val centerLongitude: Double get() = (minLongitude + maxLongitude) * 0.5
    val latitudeSpan: Double get() = maxLatitude - minLatitude
    val longitudeSpan: Double get() = maxLongitude - minLongitude

    /** Real-world width of the region in metres (east-west, at the centre latitude). */
    val widthMeters: Double
        get() = longitudeSpan * METERS_PER_DEGREE_LATITUDE * cos(Math.toRadians(centerLatitude))

    /** Real-world height of the region in metres (north-south). */
    val heightMeters: Double
        get() = latitudeSpan * METERS_PER_DEGREE_LATITUDE

    /**
     * Geographic point to normalised map coordinates in [0,1]. Values outside [0,1]
     * mean the point is off the map — deliberately not clamped, because callers that
     * cull off-map features need to see that.
     */
    fun toNormalized(latitude: Double, longitude: Double): MapUv = MapUv(
        ((longitude - minLongitude) / longitudeSpan).toFloat(),
        ((latitude - minLatitude) / latitudeSpan).toFloat(),
    )

    /** Geographic point to local map space, X/Z in [-0.5, 0.5]. */
    fun toLocal(latitude: Double, longitude: Double, localY: Float = 0f): MapPoint {
        val n = toNormalized(latitude, longitude)
        return MapPoint(n.u - 0.5f, localY, n.v - 0.5f)
    }

    /** Normalised map coordinates back to a geographic point. */
    fun fromNormalized(u: Float, v: Float): GeoPoint = GeoPoint(
        latitude = minLatitude + v * latitudeSpan,
        longitude = minLongitude + u * longitudeSpan,
    )

    /** Local map space back to a geographic point. */
    fun fromLocal(local: MapPoint): GeoPoint = fromNormalized(local.x + 0.5f, local.z + 0.5f)

    fun contains(latitude: Double, longitude: Double): Boolean =
        latitude in minLatitude..maxLatitude && longitude in minLongitude..maxLongitude

    override fun toString(): String = "GeoBounds[%.4f..%.4f N, %.4f..%.4f E]"
        .format(minLatitude, maxLatitude, minLongitude, maxLongitude)

    companion object {
        const val METERS_PER_DEGREE_LATITUDE = 111_320.0

        /**
         * Builds a square-on-the-ground region of [spanKm] edge length around a centre
         * point. Longitude degrees shrink with the cosine of latitude, so the longitude
         * span is widened to keep the footprint square in metres rather than in degrees.
         */
        fun fromCenterSpan(centerLat: Double, centerLon: Double, spanKm: Double): GeoBounds {
            val halfMeters = spanKm * 1000.0 * 0.5
            val halfLat = halfMeters / METERS_PER_DEGREE_LATITUDE

            var metersPerDegreeLon = METERS_PER_DEGREE_LATITUDE * cos(Math.toRadians(centerLat))
            // Guard against the poles, where a degree of longitude collapses to zero.
            if (abs(metersPerDegreeLon) < 1.0) metersPerDegreeLon = 1.0
            val halfLon = halfMeters / metersPerDegreeLon

            return GeoBounds(
                centerLat - halfLat, centerLat + halfLat,
                centerLon - halfLon, centerLon + halfLon,
            )
        }
    }
}

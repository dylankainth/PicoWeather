package com.weathervr.core

import kotlin.math.max

/**
 * Every conversion between real-world units and map-local units, in one place.
 *
 * **Read this before touching anything that positions content on the map.** Local map
 * space is a normalised unit square: X and Z run [-0.5, 0.5], Y uses the *same* units,
 * and the map root's scale ([mapSizeMeters]) turns all three into real metres.
 *
 * Any value computed in metres must therefore be divided by [mapSizeMeters] before
 * being used as a local coordinate. This shipped wrong once in the Unity version — the
 * terrain mesh built relief in metres while the cloud box and the lightning built
 * theirs in map units, so terrain came out exactly 2× too tall and bolts stopped short
 * of the ground. This class exists specifically so that cannot be got wrong silently;
 * use it for every conversion rather than multiplying by hand.
 */
class MapScale(
    mapSizeMeters: Float,
    regionSpanMeters: Float,
    /**
     * Altitude scale relative to horizontal. 1 is true scale.
     *
     * Named "exaggeration" for continuity with the Unity config, but note it ships at
     * **0.4**, which compresses: a cloud at 2 km renders at 40% of its true height above
     * the map, because at true scale the atmosphere column would leave the table
     * entirely. Both this and [terrainReliefExaggeration] are tuned for a specific
     * region span and scale inversely with it — changing the span without retuning them
     * is how a 43.7 m hill once rendered as an 84 cm spike.
     */
    val verticalExaggeration: Float,
    /** Extra exaggeration applied to terrain relief only, on top of [vertical]. */
    val terrainReliefExaggeration: Float,
    /** Altitude in metres that sits at the base of the rendered column. */
    val atmosphereFloorMeters: Float,
) {
    /** Edge length of the map, in real metres. */
    val mapSizeMeters: Float = max(mapSizeMeters, 1e-4f)

    /** Edge length of the region on the ground, in real metres. */
    val regionSpanMeters: Float = max(regionSpanMeters, 1f)

    /** Map metres per real-world metre, horizontally. */
    val horizontal: Float get() = mapSizeMeters / regionSpanMeters

    /** Map metres per real-world metre, vertically. */
    val vertical: Float get() = horizontal * verticalExaggeration

    /** Real-world altitude in metres to map metres above the map plane. */
    fun altitudeToMeters(altitudeMeters: Float): Float =
        (altitudeMeters - atmosphereFloorMeters) * vertical

    /** Real metres to normalised map units. */
    fun metersToMapUnits(meters: Float): Float = meters / mapSizeMeters

    /** Real-world altitude in metres to map-local Y. */
    fun altitudeToMapUnits(altitudeMeters: Float): Float =
        metersToMapUnits(altitudeToMeters(altitudeMeters))

    /**
     * Terrain elevation in metres to map-local Y. Sea level and below flattens to
     * zero: the sea surface is flat, and bathymetry rides in vertex colour instead.
     */
    fun terrainElevationToMapUnits(elevationMeters: Float): Float =
        metersToMapUnits(max(elevationMeters, 0f) * vertical * terrainReliefExaggeration)

    /**
     * A building's real height in metres to map-local Y, added on top of its
     * footprint's terrain base. Deliberately uses [horizontal], not [vertical]:
     * buildings are already visible at true scale (a 180 m tower on a 5 km/2 m map is
     * 7 cm), unlike terrain relief or cloud altitude, which exist to be exaggerated.
     * Exaggerating buildings too would push a skyscraper through the cloud deck.
     */
    fun buildingHeightToMapUnits(heightMeters: Float): Float =
        metersToMapUnits(max(heightMeters, 0f) * horizontal)

    override fun toString(): String =
        "MapScale[${mapSizeMeters} m map / ${regionSpanMeters} m region, " +
            "1:${(regionSpanMeters / mapSizeMeters).toInt()}, vert ×$verticalExaggeration]"
}

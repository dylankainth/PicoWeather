package com.weathervr.core

import kotlin.math.cos
import kotlin.math.sin

/**
 * Fallback buildings for when no baked `buildings.json` is present.
 *
 * Not an attempt at real geometry — a stylised city block grid: streets left clear at
 * low "presence" noise, one landmark tower at the region's centre, and heights varying
 * by the same seeded noise every other procedural layer uses. It exists so the app is
 * never a bare satellite plate if the bake or the query failed, and it is always
 * labelled "procedural" on screen.
 */
object ProceduralBuildings {

    private const val GRID_SIZE = 7
    private const val MIN_HEIGHT_METERS = 8f
    private const val MAX_HEIGHT_METERS = 140f
    private const val LANDMARK_HEIGHT_METERS = 180f

    fun generate(bounds: GeoBounds, seed: Int): BuildingDataset {
        val records = ArrayList<BuildingRecord>()
        val center = GRID_SIZE / 2

        for (gy in 0 until GRID_SIZE) {
            for (gx in 0 until GRID_SIZE) {
                // Leave the outer ring clear so buildings don't crowd the map edge.
                if (gx == 0 || gy == 0 || gx == GRID_SIZE - 1 || gy == GRID_SIZE - 1) continue

                val cellU = (gx + 0.5f) / GRID_SIZE
                val cellV = (gy + 0.5f) / GRID_SIZE

                // Some cells stay empty — streets and plazas, deterministically.
                val presence = Noise.fbm2(gx * 3.1f, gy * 3.1f, 2, 2f, 0.5f, seed + 401)
                val landmark = gx == center && gy == center
                if (presence < 0.22f && !landmark) continue

                val sizeT = 0.55f + 0.25f * Noise.fbm2(gx * 1.7f, gy * 1.7f, 2, 2f, 0.5f, seed + 71)
                val halfW = sizeT * 0.5f / GRID_SIZE
                val halfH = sizeT * 0.42f / GRID_SIZE
                val yaw = (Noise.fbm2(gx * 5.3f, gy * 5.3f, 2, 2f, 0.5f, seed + 909) - 0.5f) * 40f * DEG_TO_RAD

                val height = if (landmark) {
                    LANDMARK_HEIGHT_METERS
                } else {
                    lerp(
                        MIN_HEIGHT_METERS,
                        MAX_HEIGHT_METERS,
                        Noise.fbm2(gx * 2.3f, gy * 2.3f, 3, 2f, 0.5f, seed + 137),
                    )
                }

                records.add(buildRectangle(bounds, cellU, cellV, halfW, halfH, yaw, height))
            }
        }

        return BuildingDataset(
            source = "procedural",
            attribution = "Procedural city block layout (no real building data).",
            minLatitude = bounds.minLatitude,
            maxLatitude = bounds.maxLatitude,
            minLongitude = bounds.minLongitude,
            maxLongitude = bounds.maxLongitude,
            buildings = records,
        )
    }

    /**
     * An axis-aligned rectangle rotated by [yaw] about its own centre, in normalised map
     * space, then projected to lat/lon. Corners are wound counter-clockwise in (u, v) —
     * and therefore in local (x, z), since that projection is a plain scale-and-translate
     * with no reflection — to match the winding the mesh builder assumes.
     */
    private fun buildRectangle(
        bounds: GeoBounds,
        centerU: Float,
        centerV: Float,
        halfW: Float,
        halfH: Float,
        yaw: Float,
        heightMeters: Float,
    ): BuildingRecord {
        val corners = arrayOf(
            -halfW to -halfH,
            halfW to -halfH,
            halfW to halfH,
            -halfW to halfH,
        )

        val c = cos(yaw)
        val s = sin(yaw)
        val flat = DoubleArray(corners.size * 2)
        for (i in corners.indices) {
            val (px, py) = corners[i]
            val u = centerU + px * c - py * s
            val v = centerV + px * s + py * c
            val geo = bounds.fromNormalized(u.clamp01(), v.clamp01())
            flat[i * 2] = geo.latitude
            flat[i * 2 + 1] = geo.longitude
        }

        return BuildingRecord(heightMeters = heightMeters, footprintFlat = flat)
    }
}

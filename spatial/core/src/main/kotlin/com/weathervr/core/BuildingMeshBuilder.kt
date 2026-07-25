package com.weathervr.core

/** How many buildings were extruded, and how many the cap dropped. */
data class BuildingMeshResult(
    val mesh: MeshData?,
    val builtCount: Int,
    val droppedCount: Int,
)

/**
 * Extrudes OSM building footprints into flat-roofed massing.
 *
 * Winding matters and is the easiest thing to get silently wrong: a footprint ring is
 * counter-clockwise viewed from above, and every triangle here is emitted so its normal
 * — `cross(v1 − v0, v2 − v0)`, the same rule [MeshData] documents — points away from
 * the building's interior. Reverse a ring and the building renders inside-out, which on
 * a headset reads as "the roof is missing" rather than as a winding bug.
 *
 * Vertex UV carries building semantics for the facade shader:
 * - u = the building's true height in metres (constant across its vertices)
 * - v = height in floors from the base (0 at the base, one unit per ~3 m storey) rather
 *   than a plain 0..1 fraction, so a bare `frac(v)` in the shader already bands once per
 *   floor with no further scaling
 */
object BuildingMeshBuilder {

    private const val METRES_PER_FLOOR_UV_HINT = 3f

    fun build(
        dataset: BuildingDataset,
        terrainField: TerrainHeightfield?,
        mapBounds: GeoBounds,
        scale: MapScale,
        maxBuildings: Int,
    ): BuildingMeshResult {
        val builder = MeshBuilder()

        val limit = minOf(dataset.buildings.size, maxOf(0, maxBuildings))
        val dropped = dataset.buildings.size - limit
        var built = 0

        for (b in 0 until limit) {
            val record = dataset.buildings[b]
            if (record.pointCount < 3) continue

            val heightMeters = maxOf(record.heightMeters, 1f)
            val n = record.pointCount

            val bottom = ArrayList<MapPoint>(n)
            val top = ArrayList<MapPoint>(n)
            var centroidX = 0f
            var centroidY = 0f
            var centroidZ = 0f

            for (i in 0 until n) {
                val lat = record.latitudeAt(i)
                val lon = record.longitudeAt(i)

                val elevation = terrainField?.let {
                    val uv = mapBounds.toNormalized(lat, lon)
                    it.sampleElevation(uv.u, uv.v)
                } ?: 0f

                val baseY = scale.terrainElevationToMapUnits(elevation)
                val topY = baseY + scale.buildingHeightToMapUnits(heightMeters)

                val local = mapBounds.toLocal(lat, lon)
                bottom.add(MapPoint(local.x, baseY, local.z))
                top.add(MapPoint(local.x, topY, local.z))

                centroidX += local.x
                centroidY += topY
                centroidZ += local.z
            }
            centroidX /= n
            centroidY /= n
            centroidZ /= n

            // A deterministic tint per building, so a skyline of identical grey boxes
            // still reads as individual buildings rather than one slab.
            val hash = (b * 2654435761u.toInt()).toUInt() xor (heightMeters * 97f).toUInt()
            val shade = 0.46f + ((hash shr 8) and 0xFFu).toFloat() / 255f * 0.16f
            val tintR = shade
            val tintG = shade
            val tintB = shade + 0.02f

            val floors = heightMeters / METRES_PER_FLOOR_UV_HINT

            fun vertex(p: MapPoint, heightFraction: Float): Int = builder.addVertex(
                p.x, p.y, p.z,
                u = heightMeters,
                v = heightFraction * floors,
                r = tintR, g = tintG, b = tintB,
            )

            // Walls: one quad per footprint edge, with its own four vertices so the
            // recalculated normal is flat per face rather than averaged at the corners.
            for (i in 0 until n) {
                val j = (i + 1) % n

                val v0 = vertex(bottom[i], 0f)
                val v1 = vertex(top[j], 1f)
                val v2 = vertex(bottom[j], 0f)
                val v3 = vertex(top[i], 1f)

                builder.addTriangle(v0, v1, v2)
                builder.addTriangle(v0, v3, v1)
            }

            // Roof: a centroid fan. The reversed perimeter order relative to the
            // footprint's own winding is what turns the fan to face +Y.
            val centroidIndex = vertex(MapPoint(centroidX, centroidY, centroidZ), 1f)
            for (i in 0 until n) {
                val j = (i + 1) % n
                val pj = vertex(top[j], 1f)
                val pi = vertex(top[i], 1f)
                builder.addTriangle(centroidIndex, pj, pi)
            }

            built++
        }

        val mesh = if (builder.vertexCount == 0) null else builder.build()
        return BuildingMeshResult(mesh, built, dropped)
    }
}

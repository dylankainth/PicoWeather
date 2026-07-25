package com.weathervr.core

import kotlin.math.abs
import kotlin.math.sqrt

/**
 * Turns a [TerrainHeightfield] into a renderable grid mesh.
 *
 * Vertex colour channels, so the shader and this builder agree:
 * - r = water mask (1 at or below sea level)
 * - g = normalised elevation across the field's own range
 * - b = normalised slope
 * - a = shoreline proximity (1 right at the water's edge, falling off either side)
 */
object TerrainMeshBuilder {

    /** What the vertex colour means, for whoever writes the shader next. */
    const val VERTEX_CHANNEL_DESCRIPTION = "r=water, g=elevation, b=slope, a=shore"

    /**
     * Builds the mesh. [resolution] is vertices per side.
     *
     * Y goes through the same normalised-map-unit conversion as X and Z — see
     * [MapScale] — which the map root's scale then turns into real metres. Building
     * relief in metres while X and Z are in map units is the specific mistake that
     * shipped once in the Unity version and made the terrain exactly 2× too tall.
     */
    fun build(field: TerrainHeightfield, resolution: Int, scale: MapScale): MeshData {
        val res = resolution.clampTo(4, 256)
        val vertexCount = res * res
        val quads = (res - 1) * (res - 1)
        val mesh = MeshData(vertexCount, quads * 2)

        // Sample once into a scratch grid: the slope pass needs neighbours, and
        // re-sampling the heightfield bilinearly four times per vertex is wasteful.
        val elevations = FloatArray(vertexCount)
        for (y in 0 until res) {
            val v = y / (res - 1).toFloat()
            for (x in 0 until res) {
                val u = x / (res - 1).toFloat()
                elevations[y * res + x] = field.sampleElevation(u, v)
            }
        }

        // Horizontal distance between adjacent vertices, in real metres — needed to make
        // the slope channel a true gradient rather than a per-resolution artefact.
        val sampleSpacingMeters = (field.bounds.widthMeters / (res - 1)).toFloat()
        val range = field.elevationRange

        for (y in 0 until res) {
            val v = y / (res - 1).toFloat()
            for (x in 0 until res) {
                val i = y * res + x
                val u = x / (res - 1).toFloat()
                val elevation = elevations[i]

                // Sea surface is flat; anything below it lives only in the colour.
                mesh.setPosition(i, u - 0.5f, scale.terrainElevationToMapUnits(elevation), v - 0.5f)
                mesh.setUv(i, u, v)

                val left = elevations[y * res + maxOf(x - 1, 0)]
                val right = elevations[y * res + minOf(x + 1, res - 1)]
                val down = elevations[maxOf(y - 1, 0) * res + x]
                val up = elevations[minOf(y + 1, res - 1) * res + x]

                val dx = (right - left) / (2f * sampleSpacingMeters)
                val dz = (up - down) / (2f * sampleSpacingMeters)
                val slope = (sqrt(dx * dx + dz * dz) * 8f).clamp01()

                val water = if (elevation <= 0f) 1f else 0f
                val normalizedElevation = ((elevation - field.minElevation) / range).clamp01()
                // Shoreline band: within ±6 m of the waterline.
                val shore = 1f - (abs(elevation) / 6f).clamp01()

                mesh.setColor(i, water, normalizedElevation, slope, shore)
            }
        }

        buildIndices(mesh, res)
        mesh.recalculateNormals()
        return mesh
    }

    private fun buildIndices(mesh: MeshData, resolution: Int) {
        var t = 0
        for (y in 0 until resolution - 1) {
            for (x in 0 until resolution - 1) {
                val bl = y * resolution + x
                val br = bl + 1
                val tl = bl + resolution
                val tr = tl + 1

                // Wound so the surface faces +Y.
                mesh.setTriangle(t++, bl, tl, br)
                mesh.setTriangle(t++, br, tl, tr)
            }
        }
    }

    /**
     * Builds LOD0/1/2 at full, half and quarter vertex density. The map is only 2 m
     * across so LOD switching is subtle, but it earns its keep when the user steps back
     * to take in the whole system.
     */
    fun buildLodChain(field: TerrainHeightfield, scale: MapScale, lod0Resolution: Int): List<MeshData> = listOf(
        build(field, lod0Resolution, scale),
        build(field, maxOf(16, lod0Resolution / 2), scale),
        build(field, maxOf(8, lod0Resolution / 4), scale),
    )
}

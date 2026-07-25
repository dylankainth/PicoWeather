package com.weathervr.core

import kotlin.math.abs
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Fallback terrain for when no baked `terrain.bin` is present.
 *
 * A stylised stand-in for the City of London: the Thames meandering roughly west→east
 * through the middle with its tidal channel cut a few metres below datum, the flat
 * floodplain terraces either side of it, and the low ground rising gently north and
 * south away from the river. It exists so the app is never a black screen, and the app
 * labels the map "procedural" whenever it is used.
 *
 * **The C# original generated the Yangtze delta** — sea to the east, an estuary, an
 * alluvial plain — from when the project targeted Shanghai. The region moved to London
 * and the fallback did not follow, so running without baked data put an ocean over the
 * City. Rewritten here rather than transliterated; this is the one place in the port
 * that is deliberately not a faithful copy, and the reason is that faithfulness would
 * have preserved a bug.
 *
 * The elevation range is matched to the real baked London `terrain.bin` (−5 m to
 * +44 m). That matters beyond looks: `MapScale`'s exaggeration constants are tuned
 * against that range, so a fallback with delta-sized relief would render at a visibly
 * different vertical scale from the real data it stands in for.
 */
object ProceduralTerrain {

    /** Deepest point of the tidal channel, metres. Matches the baked data's minimum. */
    const val RIVER_BED_ELEVATION = -5f

    /** The floodplain the City sits on. */
    const val PLAIN_ELEVATION = 12f

    /** Highest ground in the region. Matches the baked data's 43.7 m peak. */
    const val HILL_PEAK_ELEVATION = 44f

    fun generate(bounds: GeoBounds, resolution: Int, seed: Int): TerrainHeightfield {
        val size = resolution.clampTo(32, 2048)
        val field = TerrainHeightfield(size, size, bounds, RIVER_BED_ELEVATION, HILL_PEAK_ELEVATION)

        for (y in 0 until size) {
            val v = y / (size - 1).toFloat()
            for (x in 0 until size) {
                val u = x / (size - 1).toFloat()
                field.setElevation(x, y, elevationAt(u, v, seed))
            }
        }
        return field
    }

    /** Elevation in metres at normalised map coordinates (u east, v north). */
    fun elevationAt(u: Float, v: Float, seed: Int): Float {
        // --- the ground ----------------------------------------------------------
        // London's relief over 5 km is a few tens of metres: terraces stepping up away
        // from the river, not hills. The rise is gentle and north-weighted, since the
        // ground climbs toward Islington and Hampstead beyond the tile.
        val northward = smoothStep(0f, 1f, inverseLerp(0.55f, 1.0f, v)) * 22f
        val southward = smoothStep(0f, 1f, inverseLerp(0.42f, 0.0f, v)) * 14f
        val terraces = (Noise.fbm2(u * 5f, v * 5f, 4, 2f, 0.5f, seed + 31) - 0.5f) * 9f

        var land = PLAIN_ELEVATION + northward + southward + terraces

        // A couple of low rises so the surface is not a plane: the ground around
        // Clerkenwell and the slight dome the old City stands on.
        land += bump(u, v, 0.34f, 0.78f, 0.20f) * 10f
        land += bump(u, v, 0.62f, 0.30f, 0.16f) * 6f

        // --- the Thames -----------------------------------------------------------
        // A wide meander crossing the tile west to east, swinging south around the Isle
        // of Dogs at the eastern edge. `slope` is small because the river runs across
        // the map rather than up it.
        val river = riverMask(
            u, v, seed + 512,
            amplitude = 0.085f, frequency = 1.15f, baseline = 0.47f, slope = -0.06f, halfWidth = 0.030f,
        )

        // The banks are built up, so the ground dips sharply into the channel rather
        // than shelving gently — the same reason the real minimum is a hard −5 m.
        val channel = smoothStep(0f, 1f, river)
        land = lerp(land, RIVER_BED_ELEVATION, channel)

        // Docks and canal basins: small isolated cuts north of the river.
        val basin = bump(u, v, 0.78f, 0.58f, 0.045f)
        if (basin > 0f) land = lerp(land, -2f, smoothStep(0f, 1f, basin))

        return land.coerceIn(RIVER_BED_ELEVATION, HILL_PEAK_ELEVATION)
    }

    /** A rise: smooth radial falloff, squared so the flanks are convex rather than conical. */
    private fun bump(u: Float, v: Float, cx: Float, cy: Float, radius: Float): Float {
        val d = sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) / radius
        if (d >= 1f) return 0f
        val t = 1f - d
        return t * t
    }

    /**
     * 0..1 mask for a sinuous river channel. The centreline is a sine meander with a
     * linear drift, perturbed by noise so it is not obviously periodic.
     *
     * Note this runs *across* the map: `centre` is a v (northing) for a given u
     * (easting), the transpose of the C# version, because the Thames crosses London
     * west-to-east where the Huangpu ran south-to-north.
     */
    private fun riverMask(
        u: Float,
        v: Float,
        seed: Int,
        amplitude: Float,
        frequency: Float,
        baseline: Float,
        slope: Float,
        halfWidth: Float,
    ): Float {
        val centre = baseline + slope * u +
            amplitude * sin(u * frequency * Math.PI.toFloat() * 2f) +
            amplitude * 0.6f * (Noise.fbm2(u * 3.5f, seed * 0.013f, 3, 2f, 0.5f, seed) - 0.5f) * 2f

        val d = abs(v - centre)
        // The tideway widens downstream, toward the east.
        val width = halfWidth * (0.75f + 0.6f * u)
        return 1f - smoothStep(0f, 1f, inverseLerp(width, width * 2.2f, d))
    }
}

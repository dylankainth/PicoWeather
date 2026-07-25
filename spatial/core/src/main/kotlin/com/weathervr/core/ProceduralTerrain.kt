package com.weathervr.core

import kotlin.math.abs
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Fallback terrain for when no baked `terrain.bin` is present.
 *
 * A stylised stand-in for a river delta rather than a guess at real elevations: sea to
 * the east, an estuary opening to the north-east, a meandering river, an almost
 * dead-flat alluvial plain a few metres above sea level, and low residual hills to the
 * south-west. It exists so the app is never a black screen, and the app labels the map
 * "procedural" whenever it is used.
 *
 * Note the shape is the Yangtze delta the project originally targeted, not London.
 * That mismatch is inherited from the C# and left as-is deliberately: changing the
 * generator would be a content decision, not a port, and doing it silently inside a
 * port is how you end up unable to tell a translation bug from a redesign.
 */
object ProceduralTerrain {

    const val SEA_FLOOR_ELEVATION = -18f
    const val PLAIN_ELEVATION = 4f
    const val HILL_PEAK_ELEVATION = 96f

    fun generate(bounds: GeoBounds, resolution: Int, seed: Int): TerrainHeightfield {
        val size = resolution.clampTo(32, 2048)
        val field = TerrainHeightfield(size, size, bounds, SEA_FLOOR_ELEVATION, HILL_PEAK_ELEVATION)

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
        // --- coastline ----------------------------------------------------------
        // The coast runs roughly NNW-SSE through the eastern third of the tile,
        // wandering with low-frequency noise so it never reads as a straight edge.
        var coastX = 0.72f + 0.05f * (Noise.fbm2(v * 2.4f, 11.7f, 3, 2f, 0.5f, seed) - 0.5f) * 2f
        // The estuary flares open towards the north.
        coastX -= smoothStep(0f, 1f, inverseLerp(0.62f, 1f, v)) * 0.28f

        val landMask = smoothStep(0f, 1f, (coastX - u) / 0.045f)

        // --- alluvial plain ------------------------------------------------------
        // Delta relief is genuinely tiny: a few metres of levees and fill.
        val plainRelief = (Noise.fbm2(u * 7f, v * 7f, 4, 2f, 0.5f, seed + 31) - 0.5f) * 6f
        var land = PLAIN_ELEVATION + plainRelief

        // --- south-western hills --------------------------------------------------
        // A cluster of isolated low hills, not a range: separate Gaussian bumps
        // modulated by noise.
        var hills = 0f
        hills += bump(u, v, 0.17f, 0.21f, 0.075f) * 96f
        hills += bump(u, v, 0.28f, 0.13f, 0.055f) * 61f
        hills += bump(u, v, 0.09f, 0.36f, 0.048f) * 44f
        hills += bump(u, v, 0.34f, 0.30f, 0.040f) * 33f
        hills *= 0.65f + 0.35f * Noise.fbm2(u * 18f, v * 18f, 3, 2f, 0.5f, seed + 77)
        land += hills

        // --- rivers ----------------------------------------------------------------
        // A meander running SW -> NE across the plain into the estuary.
        val trunk = riverMask(
            u, v, seed + 512,
            amplitude = 0.10f, frequency = 2.3f, baseline = 0.36f, slope = 0.42f, halfWidth = 0.016f,
        )
        // A smaller tributary running roughly west -> east.
        val creek = riverMask(
            u, v, seed + 913,
            amplitude = 0.045f, frequency = 4.1f, baseline = 0.60f, slope = 0.04f, halfWidth = 0.008f,
        )
        val river = maxOf(trunk, creek)
        land = lerp(land, -6f, river)

        // --- sea floor ---------------------------------------------------------------
        // Shallow shelf deepening gradually offshore.
        val offshore = ((u - coastX) / 0.30f).clamp01()
        var sea = lerp(-2f, SEA_FLOOR_ELEVATION, smoothStep(0f, 1f, offshore))
        sea += (Noise.fbm2(u * 9f, v * 9f, 3, 2f, 0.5f, seed + 404) - 0.5f) * 3f

        return lerp(sea, land, landMask)
    }

    /** A hill: smooth radial falloff, squared so the flanks are convex rather than conical. */
    private fun bump(u: Float, v: Float, cx: Float, cy: Float, radius: Float): Float {
        val d = sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) / radius
        if (d >= 1f) return 0f
        val t = 1f - d
        return t * t
    }

    /**
     * 0..1 mask for a sinuous river channel. The centreline is a sine meander with a
     * linear drift, perturbed by noise so it is not obviously periodic.
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
        val centre = baseline + slope * v +
            amplitude * sin(v * frequency * Math.PI.toFloat() * 2f) +
            amplitude * 0.6f * (Noise.fbm2(v * 3.5f, seed * 0.013f, 3, 2f, 0.5f, seed) - 0.5f) * 2f

        val d = abs(u - centre)
        // Channels widen downstream.
        val width = halfWidth * (0.6f + 0.8f * v)
        return 1f - smoothStep(0f, 1f, inverseLerp(width, width * 2.4f, d))
    }
}

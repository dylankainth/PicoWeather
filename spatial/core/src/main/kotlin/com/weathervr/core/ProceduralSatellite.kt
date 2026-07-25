package com.weathervr.core

import kotlin.math.abs
import kotlin.math.floor
import kotlin.math.sqrt

/** A linear RGB colour, components nominally 0..1. Stands in for Unity's `Color`. */
data class Rgb(val r: Float, val g: Float, val b: Float) {
    operator fun times(k: Float) = Rgb(r * k, g * k, b * k)

    companion object {
        /** From 8-bit components, as the C# `Color32` → `Color` conversion does. */
        fun bytes(r: Int, g: Int, b: Int) = Rgb(r / 255f, g / 255f, b / 255f)

        /** Unity's `Color.Lerp` clamps `t`. */
        fun lerp(a: Rgb, b: Rgb, t: Float): Rgb {
            val k = t.clamp01()
            return Rgb(
                a.r + (b.r - a.r) * k,
                a.g + (b.g - a.g) * k,
                a.b + (b.b - a.b) * k,
            )
        }
    }
}

/**
 * An 8-bit RGB image, row-major from the south edge up — the same orientation as
 * [TerrainHeightfield] and the baked `satellite.jpg`.
 *
 * Deliberately not a texture object. The Unity version returned a `Texture2D`, which is
 * what made this file unportable despite being pure arithmetic; handing back bytes lets
 * whatever renderer the Spatial SDK provides upload them however it likes.
 */
class RgbImage(val width: Int, val height: Int) {
    /** Tightly packed RGB, 3 bytes per pixel. */
    val pixels = ByteArray(width * height * 3)

    fun set(x: Int, y: Int, color: Rgb) {
        val i = (y * width + x) * 3
        pixels[i] = toByte(color.r)
        pixels[i + 1] = toByte(color.g)
        pixels[i + 2] = toByte(color.b)
    }

    fun get(x: Int, y: Int): Triple<Int, Int, Int> {
        val i = (y * width + x) * 3
        return Triple(
            pixels[i].toInt() and 0xFF,
            pixels[i + 1].toInt() and 0xFF,
            pixels[i + 2].toInt() and 0xFF,
        )
    }

    private fun toByte(v: Float): Byte = (v * 255f).coerceIn(0f, 255f).toInt().toByte()
}

/**
 * Synthesises a true-colour-looking basemap from a heightfield, for when no baked
 * `satellite.jpg` is present.
 *
 * This is landcover painting, not imagery: water tinted by depth and sediment load, a
 * patchwork of field parcels on the plain, a grey urban mass near the river confluence,
 * and wooded hills. It reads correctly at tabletop scale and is clearly labelled
 * procedural in the app.
 *
 * One deliberate deviation: the C# mixed `Color32.Lerp` (which rounds to bytes at every
 * step) with `Color.Lerp` (which does not). This port does every blend in float and
 * quantises once at the end, so individual pixels can differ from the C# by ±1/255. The
 * output is a procedural texture, not a checksum, and the extra precision is strictly
 * better; noting it because "should be identical" is otherwise a reasonable expectation
 * of a port.
 */
object ProceduralSatellite {

    private val DEEP_WATER = Rgb.bytes(24, 46, 68)
    private val SHALLOW_WATER = Rgb.bytes(52, 78, 92)
    private val SEDIMENT_WATER = Rgb.bytes(118, 104, 76)
    private val WETLAND = Rgb.bytes(92, 100, 66)
    private val CROPLAND = Rgb.bytes(104, 122, 66)
    private val CROPLAND_ALT = Rgb.bytes(132, 138, 84)
    private val FOREST = Rgb.bytes(56, 82, 48)
    private val URBAN_CORE = Rgb.bytes(126, 124, 122)
    private val SUBURB = Rgb.bytes(112, 112, 100)

    fun generate(field: TerrainHeightfield?, resolution: Int, seed: Int): RgbImage {
        val size = resolution.clampTo(128, 4096)
        val image = RgbImage(size, size)

        for (y in 0 until size) {
            val v = y / (size - 1).toFloat()
            for (x in 0 until size) {
                val u = x / (size - 1).toFloat()
                val elevation = field?.sampleElevation(u, v) ?: ProceduralTerrain.elevationAt(u, v, seed)
                image.set(x, y, shade(u, v, elevation, seed))
            }
        }
        return image
    }

    fun shade(u: Float, v: Float, elevation: Float, seed: Int): Rgb {
        // ---------------------------------------------------------------- water
        if (elevation < 0f) {
            val depth = (-elevation / 18f).clamp01()
            var water = Rgb.lerp(SHALLOW_WATER, DEEP_WATER, smoothStep(0f, 1f, depth))

            // Sediment plume: strongest close inshore, dispersing offshore.
            var plume = (1f - depth * 1.6f).clamp01()
            plume *= 0.55f + 0.45f * Noise.fbm2(u * 9f, v * 9f, 4, 2f, 0.5f, seed + 211)
            water = Rgb.lerp(water, SEDIMENT_WATER, plume * 0.85f)

            return jitter(water, u, v, seed + 9, 0.03f)
        }

        // -------------------------------------------------------------- wetland
        if (elevation < 1.5f) {
            val t = inverseLerp(0f, 1.5f, elevation)
            return jitter(Rgb.lerp(SEDIMENT_WATER, WETLAND, t), u, v, seed + 17, 0.04f)
        }

        // ---------------------------------------------------------------- hills
        if (elevation > 24f) {
            val t = ((elevation - 24f) / 60f).clamp01()
            var wooded = Rgb.lerp(CROPLAND, FOREST, smoothStep(0f, 1f, t))
            // Fine noise gives the hills relief.
            val texture = Noise.fbm2(u * 60f, v * 60f, 3, 2f, 0.5f, seed + 303)
            wooded *= 0.85f + 0.3f * texture
            return jitter(wooded, u, v, seed + 23, 0.05f)
        }

        // ---------------------------------------------------------------- urban
        // Density falls off from the historic centre, with a secondary cluster at the port.
        var urban = 0f
        urban += radialFalloff(u, v, 0.52f, 0.50f, 0.135f) * 1.0f
        urban += radialFalloff(u, v, 0.63f, 0.63f, 0.075f) * 0.7f
        urban += radialFalloff(u, v, 0.44f, 0.38f, 0.060f) * 0.5f
        // Ribbon development along the transport corridors, as fine filaments.
        urban += maxOf(0f, Noise.fbm2(u * 14f, v * 14f, 3, 2f, 0.5f, seed + 41) - 0.60f) * 2.2f
        urban = urban.clamp01()

        // ---------------------------------------------------------- agriculture
        // Field parcels: quantised noise gives the patchwork of distinct plots that makes
        // farmland recognisable from orbit.
        val parcelsPerEdge = 46f
        val px = floor(u * parcelsPerEdge)
        val py = floor(v * parcelsPerEdge)
        val parcel = Noise.perlin2(px * 0.73f, py * 0.73f, seed + 57)
        var farmland = Rgb.lerp(CROPLAND, CROPLAND_ALT, parcel)
        // Faint hedgerow/bund lines between parcels.
        val edgeU = abs(repeat(u * parcelsPerEdge, 1f) - 0.5f) * 2f
        val edgeV = abs(repeat(v * parcelsPerEdge, 1f) - 0.5f) * 2f
        val bund = maxOf(edgeU, edgeV)
        farmland *= lerp(1f, 0.9f, smoothStep(0f, 1f, inverseLerp(0.86f, 1f, bund)))

        val built = Rgb.lerp(SUBURB, URBAN_CORE, urban)
        val surface = Rgb.lerp(farmland, built, smoothStep(0f, 1f, urban))

        return jitter(surface, u, v, seed + 29, 0.035f)
    }

    private fun radialFalloff(u: Float, v: Float, cx: Float, cy: Float, radius: Float): Float {
        val d = sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) / radius
        return if (d >= 1f) 0f else smoothStep(1f, 0f, d)
    }

    /** Per-pixel grain, so large flat areas do not band. */
    private fun jitter(color: Rgb, u: Float, v: Float, seed: Int, amount: Float): Rgb {
        val n = Noise.perlin2(u * 512f, v * 512f, seed) - 0.5f
        return color * (1f + n * amount * 2f)
    }
}

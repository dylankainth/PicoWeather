package com.weathervr.core

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Deterministic gradient noise used by every procedural fallback.
 *
 * Unity's `Mathf.PerlinNoise` is not guaranteed stable across versions or platforms and
 * only exists in 2D, so the original hashed its own. That decision pays off here: the
 * field is a pure function of (position, seed) with no engine call anywhere in it, so
 * this port is line-for-line and produces bit-identical output to the C#.
 *
 * The one thing to be careful about is the hashing. C# `uint` arithmetic wraps silently;
 * Kotlin has `UInt` with the same wrapping semantics, so the mixing constants and shifts
 * carry over directly — but `Int`/`UInt` conversions must be explicit or the sign
 * extension quietly changes every lattice point.
 */
object Noise {

    // ----------------------------------------------------------------- hashing

    private fun hash(x0: UInt): UInt {
        var x = x0
        x = x xor (x shr 16); x *= 0x7feb352du
        x = x xor (x shr 15); x *= 0x846ca68bu
        x = x xor (x shr 16)
        return x
    }

    private fun hash(x: Int, y: Int, seed: Int): UInt =
        hash(x.toUInt() * 0x9E3779B1u xor (y.toUInt() * 0x85EBCA77u) xor (seed.toUInt() * 0xC2B2AE3Du))

    private fun hash(x: Int, y: Int, z: Int, seed: Int): UInt =
        hash(
            x.toUInt() * 0x9E3779B1u xor (y.toUInt() * 0x85EBCA77u) xor
                (z.toUInt() * 0xC2B2AE3Du) xor (seed.toUInt() * 0x27D4EB2Fu),
        )

    /** Uniform 0..1 from an integer lattice point. */
    private fun unit(h: UInt): Float = (h and 0x00FFFFFFu).toFloat() / 0x01000000u.toFloat()

    private fun gradient2(x: Int, y: Int, seed: Int): Pair<Float, Float> {
        val angle = unit(hash(x, y, seed)) * PI.toFloat() * 2f
        return cos(angle) to sin(angle)
    }

    private class Vec3(val x: Float, val y: Float, val z: Float)

    private fun gradient3(x: Int, y: Int, z: Int, seed: Int): Vec3 {
        val h = hash(x, y, z, seed)
        val theta = unit(h) * PI.toFloat() * 2f
        val cosPhi = unit(hash(h xor 0x5F356495u)) * 2f - 1f
        val sinPhi = sqrt(maxOf(0f, 1f - cosPhi * cosPhi))
        return Vec3(cos(theta) * sinPhi, sin(theta) * sinPhi, cosPhi)
    }

    /** Quintic smoothstep — C2 continuous, so fBm has no visible lattice creases. */
    private fun fade(t: Float): Float = t * t * t * (t * (t * 6f - 15f) + 10f)

    // ------------------------------------------------------------------ perlin

    /** Perlin gradient noise in 2D, returned in 0..1. */
    fun perlin2(x: Float, y: Float, seed: Int = 0): Float {
        val xi = floorToInt(x)
        val yi = floorToInt(y)
        val xf = x - xi
        val yf = y - yi
        val u = fade(xf)
        val v = fade(yf)

        fun dot(cx: Int, cy: Int): Float {
            val g = gradient2(xi + cx, yi + cy, seed)
            return g.first * (xf - cx) + g.second * (yf - cy)
        }

        val a = lerp(dot(0, 0), dot(1, 0), u)
        val b = lerp(dot(0, 1), dot(1, 1), u)
        return (lerp(a, b, v) * 0.7071f + 0.5f).clamp01()
    }

    /** Perlin gradient noise in 3D, returned in 0..1. */
    fun perlin3(x: Float, y: Float, z: Float, seed: Int = 0): Float {
        val xi = floorToInt(x)
        val yi = floorToInt(y)
        val zi = floorToInt(z)
        val xf = x - xi
        val yf = y - yi
        val zf = z - zi
        val u = fade(xf)
        val v = fade(yf)
        val w = fade(zf)

        fun dot(cx: Int, cy: Int, cz: Int): Float {
            val g = gradient3(xi + cx, yi + cy, zi + cz, seed)
            return g.x * (xf - cx) + g.y * (yf - cy) + g.z * (zf - cz)
        }

        val x00 = lerp(dot(0, 0, 0), dot(1, 0, 0), u)
        val x10 = lerp(dot(0, 1, 0), dot(1, 1, 0), u)
        val x01 = lerp(dot(0, 0, 1), dot(1, 0, 1), u)
        val x11 = lerp(dot(0, 1, 1), dot(1, 1, 1), u)

        val y0 = lerp(x00, x10, v)
        val y1 = lerp(x01, x11, v)
        return (lerp(y0, y1, w) * 0.8660f + 0.5f).clamp01()
    }

    // --------------------------------------------------------------------- fBm

    /** Fractional Brownian motion over [perlin2], 0..1. */
    fun fbm2(
        x: Float,
        y: Float,
        octaves: Int = 4,
        lacunarity: Float = 2f,
        gain: Float = 0.5f,
        seed: Int = 0,
    ): Float {
        var sum = 0f
        var amp = 1f
        var norm = 0f
        var freq = 1f
        for (i in 0 until octaves) {
            sum += amp * perlin2(x * freq, y * freq, seed + i * 131)
            norm += amp
            amp *= gain
            freq *= lacunarity
        }
        return if (norm > 0f) sum / norm else 0f
    }

    /** Fractional Brownian motion over [perlin3], 0..1. */
    fun fbm3(
        x: Float,
        y: Float,
        z: Float,
        octaves: Int = 4,
        lacunarity: Float = 2f,
        gain: Float = 0.5f,
        seed: Int = 0,
    ): Float {
        var sum = 0f
        var amp = 1f
        var norm = 0f
        var freq = 1f
        for (i in 0 until octaves) {
            sum += amp * perlin3(x * freq, y * freq, z * freq, seed + i * 131)
            norm += amp
            amp *= gain
            freq *= lacunarity
        }
        return if (norm > 0f) sum / norm else 0f
    }

    /**
     * Worley/cellular noise, returned as 1 − distance-to-nearest-feature so that high
     * values sit at the cell centres. Gives cloud volumes their billowy,
     * cauliflower-edged look rather than the smooth blobs fBm alone gives.
     */
    fun worley3(x: Float, y: Float, z: Float, seed: Int = 0): Float {
        val xi = floorToInt(x)
        val yi = floorToInt(y)
        val zi = floorToInt(z)
        var best = 1e9f

        for (dz in -1..1) for (dy in -1..1) for (dx in -1..1) {
            val cx = xi + dx
            val cy = yi + dy
            val cz = zi + dz
            val h = hash(cx, cy, cz, seed)
            val fx = cx + unit(h)
            val fy = cy + unit(hash(h xor 0x68bc21ebu))
            val fz = cz + unit(hash(h xor 0x02e5be93u))
            val ddx = fx - x
            val ddy = fy - y
            val ddz = fz - z
            val d = ddx * ddx + ddy * ddy + ddz * ddz
            if (d < best) best = d
        }

        return (1f - sqrt(best)).clamp01()
    }

    /** Multi-octave Worley, inverted so it reads as cloud billows. */
    fun worleyFbm3(x: Float, y: Float, z: Float, octaves: Int = 3, seed: Int = 0): Float {
        var sum = 0f
        var amp = 1f
        var norm = 0f
        var freq = 1f
        for (i in 0 until octaves) {
            sum += amp * worley3(x * freq, y * freq, z * freq, seed + i * 977)
            norm += amp
            amp *= 0.5f
            freq *= 2f
        }
        return if (norm > 0f) sum / norm else 0f
    }

    // ---------------------------------------------------------------- periodic
    // The detail-noise volume is tiled by the cloud renderer, so it has to wrap
    // seamlessly in all three axes. These variants wrap the integer lattice by
    // `period`, which makes the field exactly periodic with that period.

    private fun wrap(v: Int, period: Int): Int {
        val m = v % period
        return if (m < 0) m + period else m
    }

    private fun periodicGradient3(x: Int, y: Int, z: Int, period: Int, seed: Int): Vec3 =
        gradient3(wrap(x, period), wrap(y, period), wrap(z, period), seed)

    /** Perlin noise that tiles exactly every [period] units. */
    fun perlin3Periodic(x: Float, y: Float, z: Float, period: Int, seed: Int = 0): Float {
        val xi = floorToInt(x)
        val yi = floorToInt(y)
        val zi = floorToInt(z)
        val xf = x - xi
        val yf = y - yi
        val zf = z - zi
        val u = fade(xf)
        val v = fade(yf)
        val w = fade(zf)

        fun dot(cx: Int, cy: Int, cz: Int): Float {
            val g = periodicGradient3(xi + cx, yi + cy, zi + cz, period, seed)
            return g.x * (xf - cx) + g.y * (yf - cy) + g.z * (zf - cz)
        }

        val x00 = lerp(dot(0, 0, 0), dot(1, 0, 0), u)
        val x10 = lerp(dot(0, 1, 0), dot(1, 1, 0), u)
        val x01 = lerp(dot(0, 0, 1), dot(1, 0, 1), u)
        val x11 = lerp(dot(0, 1, 1), dot(1, 1, 1), u)

        val y0 = lerp(x00, x10, v)
        val y1 = lerp(x01, x11, v)
        return (lerp(y0, y1, w) * 0.8660f + 0.5f).clamp01()
    }

    /** Worley noise that tiles exactly every [period] units. */
    fun worley3Periodic(x: Float, y: Float, z: Float, period: Int, seed: Int = 0): Float {
        val xi = floorToInt(x)
        val yi = floorToInt(y)
        val zi = floorToInt(z)
        var best = 1e9f

        for (dz in -1..1) for (dy in -1..1) for (dx in -1..1) {
            val cx = xi + dx
            val cy = yi + dy
            val cz = zi + dz
            val h = hash(wrap(cx, period), wrap(cy, period), wrap(cz, period), seed)
            // The feature point is offset from the *unwrapped* cell so distances stay
            // continuous across the seam.
            val fx = cx + unit(h)
            val fy = cy + unit(hash(h xor 0x68bc21ebu))
            val fz = cz + unit(hash(h xor 0x02e5be93u))
            val ddx = fx - x
            val ddy = fy - y
            val ddz = fz - z
            val d = ddx * ddx + ddy * ddy + ddz * ddz
            if (d < best) best = d
        }

        return (1f - sqrt(best)).clamp01()
    }

    /**
     * The detail field the cloud renderer erodes with: inverted Worley for the billowy
     * cores, layered with Perlin for the wispy fringes. Tiles exactly over the unit cube
     * when sampled at [baseFrequency] lattice cells per unit.
     */
    fun cloudDetailPeriodic(x: Float, y: Float, z: Float, baseFrequency: Int, seed: Int): Float {
        var worley = 0f
        var amp = 1f
        var norm = 0f
        var freq = baseFrequency
        for (i in 0 until 3) {
            worley += amp * worley3Periodic(x * freq, y * freq, z * freq, freq, seed + i * 977)
            norm += amp
            amp *= 0.5f
            freq *= 2
        }
        worley = if (norm > 0f) worley / norm else 0f

        var perlin = 0f
        amp = 1f
        norm = 0f
        freq = baseFrequency
        for (i in 0 until 3) {
            perlin += amp * perlin3Periodic(x * freq, y * freq, z * freq, freq, seed + i * 131)
            norm += amp
            amp *= 0.5f
            freq *= 2
        }
        perlin = if (norm > 0f) perlin / norm else 0f

        return (worley * 0.65f + perlin * 0.35f).clamp01()
    }
}

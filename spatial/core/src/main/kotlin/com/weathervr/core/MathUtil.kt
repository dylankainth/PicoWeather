package com.weathervr.core

import kotlin.math.floor

/**
 * The handful of `Mathf` helpers the ported data layer actually used.
 *
 * Unity's `Mathf` is not available here, and pulling in a maths library for four
 * one-line functions would be worse than writing them. Semantics match Unity's
 * exactly — in particular [lerp] does *not* clamp `t`, and [roundToIntHalfUp]
 * matches `Mathf.RoundToInt`'s away-from-zero behaviour at .5 rather than Kotlin's
 * banker's rounding, which would quietly shift terrain samples by one unit.
 */

fun Float.clamp01(): Float = when {
    this < 0f -> 0f
    this > 1f -> 1f
    else -> this
}

fun Int.clampTo(min: Int, max: Int): Int = when {
    this < min -> min
    this > max -> max
    else -> this
}

fun lerp(a: Float, b: Float, t: Float): Float = a + (b - a) * t

fun floorToInt(value: Float): Int = floor(value.toDouble()).toInt()

/** `Mathf.RoundToInt` rounds half away from zero; `Float.roundToInt()` agrees, but be explicit. */
fun roundToIntHalfUp(value: Float): Int = floor(value + 0.5f).toInt()

/**
 * `Mathf.SmoothStep(from, to, t)`: hermite interpolation with `t` clamped.
 *
 * Note this is *not* the HLSL `smoothstep(edge0, edge1, x)` everyone reaches for —
 * Unity's takes the interpolant as the third argument and the edges as the range being
 * interpolated *between*, which is the opposite convention. The procedural generators
 * call it both ways round (`smoothStep(0f, 1f, inverseLerp(a, b, x))` is the common
 * idiom), so getting this backwards silently changes every gradient in the app.
 */
fun smoothStep(from: Float, to: Float, t: Float): Float {
    val clamped = t.clamp01()
    val weight = clamped * clamped * (3f - 2f * clamped)
    return to * weight + from * (1f - weight)
}

/** `Mathf.InverseLerp`: where `value` sits between `a` and `b`, clamped. Reversed ranges work. */
fun inverseLerp(a: Float, b: Float, value: Float): Float =
    if (a != b) ((value - a) / (b - a)).clamp01() else 0f

/** `Mathf.Repeat`: loops `t` within [0, length). */
fun repeat(t: Float, length: Float): Float =
    (t - floor(t / length) * length).coerceIn(0f, length)

const val DEG_TO_RAD = (Math.PI / 180.0).toFloat()

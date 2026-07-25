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

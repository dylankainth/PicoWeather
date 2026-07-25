package com.weathervr.core

import kotlin.math.pow
import kotlin.math.sqrt

/**
 * Standard-atmosphere conversions between pressure and altitude, and the definitions
 * of the three cloud layers the weather model reports.
 */
object Atmosphere {

    /** Sea-level standard pressure, pascals. */
    const val SEA_LEVEL_PRESSURE_PA = 101_325.0

    /**
     * Barometric altitude from pressure, per the specification:
     *   h ≈ 44330 × [1 − (P / P₀)^(1/5.255)]
     *
     * @param pressurePa Pressure at the level of interest, in pascals.
     * @return Altitude above sea level, in metres.
     */
    fun altitudeFromPressure(pressurePa: Double): Double {
        if (pressurePa <= 0.0) return 44_330.0
        val ratio = pressurePa / SEA_LEVEL_PRESSURE_PA
        return 44_330.0 * (1.0 - ratio.pow(1.0 / 5.255))
    }

    /** Convenience overload taking hectopascals (millibars). */
    fun altitudeFromPressureHpa(pressureHpa: Double): Double =
        altitudeFromPressure(pressureHpa * 100.0)

    /** Inverse of [altitudeFromPressure]. */
    fun pressureFromAltitude(altitudeMeters: Double): Double {
        val t = 1.0 - altitudeMeters / 44_330.0
        if (t <= 0.0) return 0.0
        return SEA_LEVEL_PRESSURE_PA * t.pow(5.255)
    }

    /**
     * The three layers ECMWF (and therefore ERA5 and Open-Meteo) splits cloud cover
     * into. Boundaries are the conventional sigma levels converted to pressure against
     * a standard sea-level surface.
     */
    enum class Layer(
        /** Pressure at the base (lower altitude, higher pressure) of the layer, hPa. */
        val basePressureHpa: Double,
        /** Pressure at the top (higher altitude, lower pressure) of the layer, hPa. */
        val topPressureHpa: Double,
    ) {
        LOW(1000.0, 800.0),
        MID(800.0, 450.0),
        HIGH(450.0, 200.0);

        val baseAltitudeMeters: Double get() = altitudeFromPressureHpa(basePressureHpa)
        val topAltitudeMeters: Double get() = altitudeFromPressureHpa(topPressureHpa)
    }

    const val LAYER_COUNT = 3

    /**
     * Physically-motivated proxy for lightning activity. Neither ERA5's free hourly
     * single-level set nor Open-Meteo exposes stroke density, so we derive a 0..1
     * potential from convective available potential energy and precipitation rate —
     * the two ingredients that actually drive charge separation in a thunderstorm.
     *
     * CAPE below ~300 J/kg essentially never produces lightning; above ~2500 J/kg the
     * atmosphere is strongly unstable. Precipitation gates the result because a dry
     * unstable atmosphere is not a thunderstorm.
     */
    fun lightningPotential(capeJoulesPerKg: Float, precipitationMmPerHour: Float): Float {
        val instability = ((capeJoulesPerKg - 300f) / 2200f).clamp01()
        // Saturating response: 4 mm/h is already a convincing convective shower.
        val wetness = (precipitationMmPerHour / 4f).clamp01()
        // Both factors are necessary; the sqrt keeps moderate-but-real storms visible.
        return sqrt((instability * wetness).toDouble()).toFloat().clamp01()
    }
}

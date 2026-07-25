package com.weathervr.core

import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset
import kotlin.math.asin
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.tan

/** Solar elevation and azimuth, in degrees. Azimuth is clockwise from north. */
data class SolarAngles(
    /** Altitude above the horizon; negative at night. */
    val elevationDegrees: Double,
    /** Compass bearing, degrees clockwise from north. */
    val azimuthDegrees: Double,
)

/**
 * Where the sun is, given a time and a place.
 *
 * The low-precision algorithm from the Astronomical Almanac, accurate to roughly 0.01°
 * over 1950–2050 — vastly better than this app needs, and about twenty lines. It matters
 * because the sun angle is what makes an afternoon snapshot light its cloud tops from
 * the west; getting it wrong reads as "computer graphics" rather than as weather.
 *
 * Ported from `Core/SolarPosition.cs`. The only substantive change is `DateTime` →
 * `java.time.Instant`, which removes the C# version's `DateTimeKind` ambiguity: an
 * `Instant` is unambiguously UTC, so there is no local-time branch to get wrong.
 */
object SolarPosition {

    private val J2000: Instant =
        LocalDateTime.of(2000, 1, 1, 12, 0, 0).toInstant(ZoneOffset.UTC)

    private const val MILLIS_PER_DAY = 86_400_000.0

    /**
     * @param utc Time of observation.
     * @param latitudeDegrees Positive north.
     * @param longitudeDegrees Positive east.
     */
    fun compute(utc: Instant, latitudeDegrees: Double, longitudeDegrees: Double): SolarAngles {
        // Days since J2000.0 (2000-01-01 12:00 UT), including the fractional day.
        val julianDays = (utc.toEpochMilli() - J2000.toEpochMilli()) / MILLIS_PER_DAY

        // --- the sun's position in ecliptic coordinates -------------------------
        val meanLongitude = normalize360(280.460 + 0.9856474 * julianDays)
        val meanAnomaly = Math.toRadians(normalize360(357.528 + 0.9856003 * julianDays))

        // Equation of centre: the correction for the Earth's elliptical orbit.
        val eclipticLongitude = Math.toRadians(
            meanLongitude + 1.915 * sin(meanAnomaly) + 0.020 * sin(2.0 * meanAnomaly),
        )

        val obliquity = Math.toRadians(23.439 - 0.0000004 * julianDays)

        // --- to equatorial coordinates ------------------------------------------
        val rightAscension = atan2(
            cos(obliquity) * sin(eclipticLongitude),
            cos(eclipticLongitude),
        )
        val declination = asin(sin(obliquity) * sin(eclipticLongitude))

        // --- to horizontal coordinates -------------------------------------------
        // Greenwich mean sidereal time, in hours, then the local hour angle.
        val gmst = 18.697374558 + 24.06570982441908 * julianDays
        val localSiderealTime = Math.toRadians(normalize360((gmst % 24.0) * 15.0 + longitudeDegrees))
        val hourAngle = localSiderealTime - rightAscension

        val latitude = Math.toRadians(latitudeDegrees)
        val sinElevation = (
            sin(latitude) * sin(declination) +
                cos(latitude) * cos(declination) * cos(hourAngle)
            ).coerceIn(-1.0, 1.0)

        val azimuth = atan2(
            -sin(hourAngle),
            tan(declination) * cos(latitude) - sin(latitude) * cos(hourAngle),
        )

        return SolarAngles(
            elevationDegrees = Math.toDegrees(asin(sinElevation)),
            azimuthDegrees = normalize360(Math.toDegrees(azimuth)),
        )
    }

    private fun normalize360(degrees: Double): Double {
        val d = degrees % 360.0
        return if (d < 0.0) d + 360.0 else d
    }
}

using System;

namespace WeatherVR.Core
{
    /// <summary>
    /// Where the sun is, given a time and a place.
    ///
    /// This is the low-precision algorithm from the Astronomical Almanac, accurate
    /// to roughly 0.01° over 1950-2050 — vastly better than this app needs, and
    /// about twenty lines. It matters because the sun angle is what makes an
    /// afternoon snapshot of Shanghai light its cloud tops from the west; getting it
    /// wrong is the kind of error that reads as "computer graphics" rather than as
    /// weather.
    /// </summary>
    public static class SolarPosition
    {
        const double Deg2Rad = Math.PI / 180.0;
        const double Rad2Deg = 180.0 / Math.PI;

        /// <summary>
        /// Solar elevation and azimuth for an instant and a location.
        /// </summary>
        /// <param name="utc">Time of observation, UTC.</param>
        /// <param name="latitudeDegrees">Positive north.</param>
        /// <param name="longitudeDegrees">Positive east.</param>
        /// <param name="elevationDegrees">Altitude above the horizon; negative at night.</param>
        /// <param name="azimuthDegrees">Compass bearing, degrees clockwise from north.</param>
        public static void Compute(DateTime utc, double latitudeDegrees, double longitudeDegrees,
                                   out double elevationDegrees, out double azimuthDegrees)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();

            // Days since J2000.0 (2000-01-01 12:00 UT), including the fractional day.
            double julianDays = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;

            // --- the sun's position in ecliptic coordinates ---------------------
            double meanLongitude = Normalize360(280.460 + 0.9856474 * julianDays);
            double meanAnomaly = Normalize360(357.528 + 0.9856003 * julianDays) * Deg2Rad;

            // Equation of centre: the correction for the Earth's elliptical orbit.
            double eclipticLongitude = (meanLongitude
                                        + 1.915 * Math.Sin(meanAnomaly)
                                        + 0.020 * Math.Sin(2.0 * meanAnomaly)) * Deg2Rad;

            double obliquity = (23.439 - 0.0000004 * julianDays) * Deg2Rad;

            // --- to equatorial coordinates ---------------------------------------
            double rightAscension = Math.Atan2(Math.Cos(obliquity) * Math.Sin(eclipticLongitude),
                                               Math.Cos(eclipticLongitude));
            double declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(eclipticLongitude));

            // --- to horizontal coordinates ----------------------------------------
            // Greenwich mean sidereal time, in hours, then the local hour angle.
            double gmst = 18.697374558 + 24.06570982441908 * julianDays;
            double localSiderealTime = Normalize360((gmst % 24.0) * 15.0 + longitudeDegrees) * Deg2Rad;
            double hourAngle = localSiderealTime - rightAscension;

            double latitude = latitudeDegrees * Deg2Rad;
            double sinElevation = Math.Sin(latitude) * Math.Sin(declination)
                                + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
            sinElevation = Math.Max(-1.0, Math.Min(1.0, sinElevation));
            double elevation = Math.Asin(sinElevation);

            double azimuth = Math.Atan2(
                -Math.Sin(hourAngle),
                Math.Tan(declination) * Math.Cos(latitude) - Math.Sin(latitude) * Math.Cos(hourAngle));

            elevationDegrees = elevation * Rad2Deg;
            azimuthDegrees = Normalize360(azimuth * Rad2Deg);
        }

        static double Normalize360(double degrees)
        {
            degrees %= 360.0;
            return degrees < 0.0 ? degrees + 360.0 : degrees;
        }
    }
}

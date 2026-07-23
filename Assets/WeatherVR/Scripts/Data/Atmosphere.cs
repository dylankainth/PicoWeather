using System;

namespace WeatherVR.Data
{
    /// <summary>
    /// Standard-atmosphere conversions between pressure and altitude, and the
    /// definitions of the three cloud layers the weather model reports.
    /// </summary>
    public static class Atmosphere
    {
        /// <summary>Sea-level standard pressure, pascals.</summary>
        public const double SeaLevelPressurePa = 101325.0;

        /// <summary>
        /// Barometric altitude from pressure, per the specification:
        ///   h ≈ 44330 × [1 − (P / P₀)^(1/5.255)]
        /// </summary>
        /// <param name="pressurePa">Pressure at the level of interest, in pascals.</param>
        /// <returns>Altitude above sea level, in metres.</returns>
        public static double AltitudeFromPressure(double pressurePa)
        {
            if (pressurePa <= 0.0) return 44330.0;
            double ratio = pressurePa / SeaLevelPressurePa;
            return 44330.0 * (1.0 - Math.Pow(ratio, 1.0 / 5.255));
        }

        /// <summary>Convenience overload taking hectopascals (millibars).</summary>
        public static double AltitudeFromPressureHpa(double pressureHpa)
            => AltitudeFromPressure(pressureHpa * 100.0);

        /// <summary>Inverse of <see cref="AltitudeFromPressure"/>.</summary>
        public static double PressureFromAltitude(double altitudeMeters)
        {
            double t = 1.0 - altitudeMeters / 44330.0;
            if (t <= 0.0) return 0.0;
            return SeaLevelPressurePa * Math.Pow(t, 5.255);
        }

        /// <summary>
        /// The three layers ECMWF (and therefore ERA5 and Open-Meteo) splits cloud
        /// cover into. Boundaries are the conventional sigma levels converted to
        /// pressure against a standard sea-level surface.
        /// </summary>
        public enum Layer
        {
            Low = 0,
            Mid = 1,
            High = 2
        }

        public const int LayerCount = 3;

        /// <summary>Pressure at the base (lower altitude, higher pressure) of a layer, hPa.</summary>
        public static double LayerBasePressureHpa(Layer layer) => layer switch
        {
            Layer.Low => 1000.0,
            Layer.Mid => 800.0,
            Layer.High => 450.0,
            _ => 1000.0
        };

        /// <summary>Pressure at the top (higher altitude, lower pressure) of a layer, hPa.</summary>
        public static double LayerTopPressureHpa(Layer layer) => layer switch
        {
            Layer.Low => 800.0,
            Layer.Mid => 450.0,
            Layer.High => 200.0,
            _ => 800.0
        };

        public static double LayerBaseAltitudeMeters(Layer layer)
            => AltitudeFromPressureHpa(LayerBasePressureHpa(layer));

        public static double LayerTopAltitudeMeters(Layer layer)
            => AltitudeFromPressureHpa(LayerTopPressureHpa(layer));

        /// <summary>
        /// Physically-motivated proxy for lightning activity. Neither ERA5's free
        /// hourly single-level set nor Open-Meteo exposes stroke density, so we
        /// derive a 0..1 potential from convective available potential energy and
        /// precipitation rate — the two ingredients that actually drive charge
        /// separation in a thunderstorm.
        ///
        /// CAPE below ~300 J/kg essentially never produces lightning; above
        /// ~2500 J/kg the atmosphere is strongly unstable. Precipitation gates the
        /// result because a dry unstable atmosphere is not a thunderstorm.
        /// </summary>
        /// <param name="capeJoulesPerKg">Convective available potential energy.</param>
        /// <param name="precipitationMmPerHour">Surface precipitation rate.</param>
        public static float LightningPotential(float capeJoulesPerKg, float precipitationMmPerHour)
        {
            float instability = Clamp01((capeJoulesPerKg - 300f) / 2200f);
            // Saturating response: 4 mm/h is already a convincing convective shower.
            float wetness = Clamp01(precipitationMmPerHour / 4f);
            // Both factors are necessary; the sqrt keeps moderate-but-real storms visible.
            return Clamp01((float)Math.Sqrt(instability * wetness));
        }

        static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}

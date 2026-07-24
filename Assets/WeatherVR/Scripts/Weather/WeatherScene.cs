using UnityEngine;

namespace WeatherVR.Weather
{
    /// <summary>
    /// The weather cases the carousel can switch between. This is the full set the
    /// app renders — the carousel shows one card per case.
    /// </summary>
    public enum WeatherSceneKind
    {
        Clear,
        PartlyCloudy,
        Cloudy,
        Overcast,
        Fog,
        Drizzle,
        Rain,
        Thunderstorm,
        Snow
    }

    /// <summary>What falls from the sky in a scene.</summary>
    public enum PrecipKind
    {
        None,
        Rain,
        Snow
    }

    /// <summary>
    /// A self-contained description of one weather look. Two halves:
    ///
    ///   • how the new <c>WeatherVisuals</c> should render it (cloud amount and
    ///     darkness, precipitation kind and intensity, lightning, wind),
    ///   • the glass environment palette (sky gradient, sun, fog) so the surround
    ///     agrees with the carousel UI.
    ///
    /// This is a clean rebuild: nothing here drives the old volumetric cloud / particle
    /// rain / lightning-bolt renderers. Those are no longer used by the app.
    /// </summary>
    public struct WeatherSceneProfile
    {
        public WeatherSceneKind Kind;
        public string DisplayName;

        // ---- how WeatherVisuals renders it -----------------------------------
        public float CloudAmount;    // 0..1 how much cloud cover
        public float CloudDarkness;  // 0 bright white .. 1 storm grey
        public PrecipKind Precip;
        public float PrecipIntensity;// 0..1
        public bool Lightning;
        public float WindMs;

        // ---- glass environment palette ---------------------------------------
        public Color SkyZenith, SkyHorizon, SkyNadir;
        public Color SunGlow;   public float SunGlowStrength;
        public Color SunColor;  public float SunIntensity;
        public float SunElevation, SunAzimuth;
        public Color Ambient;
        public Color FogColor;  public float FogDensity;
    }

    public static class WeatherScene
    {
        /// <summary>Every case, in carousel order.</summary>
        public static readonly WeatherSceneKind[] AllKinds =
        {
            WeatherSceneKind.Clear,
            WeatherSceneKind.PartlyCloudy,
            WeatherSceneKind.Cloudy,
            WeatherSceneKind.Overcast,
            WeatherSceneKind.Fog,
            WeatherSceneKind.Drizzle,
            WeatherSceneKind.Rain,
            WeatherSceneKind.Thunderstorm,
            WeatherSceneKind.Snow
        };

        public static WeatherSceneProfile Default(WeatherSceneKind kind)
        {
            switch (kind)
            {
                case WeatherSceneKind.Clear:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Clear",
                        CloudAmount = 0.04f, CloudDarkness = 0f,
                        Precip = PrecipKind.None, PrecipIntensity = 0f, Lightning = false, WindMs = 2f,
                        SkyZenith = new Color(0.16f, 0.34f, 0.62f),
                        SkyHorizon = new Color(0.62f, 0.76f, 0.90f),
                        SkyNadir = new Color(0.10f, 0.16f, 0.24f),
                        SunGlow = new Color(1.00f, 0.85f, 0.55f), SunGlowStrength = 1.9f,
                        SunColor = new Color(1.00f, 0.97f, 0.90f), SunIntensity = 1.55f,
                        SunElevation = 58f, SunAzimuth = 145f,
                        Ambient = new Color(0.44f, 0.50f, 0.58f),
                        FogColor = new Color(0.55f, 0.68f, 0.82f), FogDensity = 0.015f
                    };

                case WeatherSceneKind.PartlyCloudy:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Partly Cloudy",
                        CloudAmount = 0.35f, CloudDarkness = 0.12f,
                        Precip = PrecipKind.None, PrecipIntensity = 0f, Lightning = false, WindMs = 3.5f,
                        SkyZenith = new Color(0.15f, 0.30f, 0.54f),
                        SkyHorizon = new Color(0.58f, 0.70f, 0.82f),
                        SkyNadir = new Color(0.09f, 0.14f, 0.21f),
                        SunGlow = new Color(0.98f, 0.86f, 0.64f), SunGlowStrength = 1.4f,
                        SunColor = new Color(0.99f, 0.96f, 0.90f), SunIntensity = 1.30f,
                        SunElevation = 52f, SunAzimuth = 150f,
                        Ambient = new Color(0.42f, 0.47f, 0.55f),
                        FogColor = new Color(0.52f, 0.63f, 0.76f), FogDensity = 0.02f
                    };

                case WeatherSceneKind.Cloudy:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Cloudy",
                        CloudAmount = 0.7f, CloudDarkness = 0.35f,
                        Precip = PrecipKind.None, PrecipIntensity = 0f, Lightning = false, WindMs = 4.5f,
                        SkyZenith = new Color(0.28f, 0.34f, 0.42f),
                        SkyHorizon = new Color(0.52f, 0.58f, 0.66f),
                        SkyNadir = new Color(0.12f, 0.14f, 0.18f),
                        SunGlow = new Color(0.78f, 0.80f, 0.84f), SunGlowStrength = 0.8f,
                        SunColor = new Color(0.88f, 0.90f, 0.94f), SunIntensity = 0.95f,
                        SunElevation = 46f, SunAzimuth = 155f,
                        Ambient = new Color(0.40f, 0.43f, 0.48f),
                        FogColor = new Color(0.48f, 0.52f, 0.58f), FogDensity = 0.04f
                    };

                case WeatherSceneKind.Overcast:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Overcast",
                        CloudAmount = 1f, CloudDarkness = 0.55f,
                        Precip = PrecipKind.None, PrecipIntensity = 0f, Lightning = false, WindMs = 5f,
                        SkyZenith = new Color(0.32f, 0.35f, 0.40f),
                        SkyHorizon = new Color(0.46f, 0.49f, 0.54f),
                        SkyNadir = new Color(0.13f, 0.14f, 0.17f),
                        SunGlow = new Color(0.62f, 0.64f, 0.68f), SunGlowStrength = 0.4f,
                        SunColor = new Color(0.78f, 0.80f, 0.84f), SunIntensity = 0.7f,
                        SunElevation = 40f, SunAzimuth = 160f,
                        Ambient = new Color(0.38f, 0.40f, 0.44f),
                        FogColor = new Color(0.46f, 0.48f, 0.52f), FogDensity = 0.06f
                    };

                case WeatherSceneKind.Fog:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Fog",
                        CloudAmount = 0.85f, CloudDarkness = 0.35f,
                        Precip = PrecipKind.None, PrecipIntensity = 0f, Lightning = false, WindMs = 1.5f,
                        SkyZenith = new Color(0.55f, 0.58f, 0.62f),
                        SkyHorizon = new Color(0.66f, 0.68f, 0.71f),
                        SkyNadir = new Color(0.34f, 0.36f, 0.39f),
                        SunGlow = new Color(0.74f, 0.76f, 0.78f), SunGlowStrength = 0.5f,
                        SunColor = new Color(0.82f, 0.84f, 0.86f), SunIntensity = 0.6f,
                        SunElevation = 35f, SunAzimuth = 160f,
                        Ambient = new Color(0.50f, 0.52f, 0.55f),
                        FogColor = new Color(0.72f, 0.74f, 0.77f), FogDensity = 0.30f
                    };

                case WeatherSceneKind.Drizzle:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Drizzle",
                        CloudAmount = 0.9f, CloudDarkness = 0.5f,
                        Precip = PrecipKind.Rain, PrecipIntensity = 0.3f, Lightning = false, WindMs = 4f,
                        SkyZenith = new Color(0.26f, 0.30f, 0.36f),
                        SkyHorizon = new Color(0.42f, 0.47f, 0.54f),
                        SkyNadir = new Color(0.11f, 0.13f, 0.16f),
                        SunGlow = new Color(0.60f, 0.66f, 0.74f), SunGlowStrength = 0.4f,
                        SunColor = new Color(0.76f, 0.80f, 0.86f), SunIntensity = 0.7f,
                        SunElevation = 38f, SunAzimuth = 160f,
                        Ambient = new Color(0.36f, 0.40f, 0.45f),
                        FogColor = new Color(0.42f, 0.47f, 0.54f), FogDensity = 0.09f
                    };

                case WeatherSceneKind.Rain:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Rain",
                        CloudAmount = 0.95f, CloudDarkness = 0.62f,
                        Precip = PrecipKind.Rain, PrecipIntensity = 0.75f, Lightning = false, WindMs = 6f,
                        SkyZenith = new Color(0.18f, 0.22f, 0.30f),
                        SkyHorizon = new Color(0.34f, 0.40f, 0.48f),
                        SkyNadir = new Color(0.09f, 0.11f, 0.14f),
                        SunGlow = new Color(0.50f, 0.58f, 0.70f), SunGlowStrength = 0.35f,
                        SunColor = new Color(0.68f, 0.74f, 0.84f), SunIntensity = 0.55f,
                        SunElevation = 32f, SunAzimuth = 165f,
                        Ambient = new Color(0.30f, 0.34f, 0.40f),
                        FogColor = new Color(0.30f, 0.36f, 0.44f), FogDensity = 0.12f
                    };

                case WeatherSceneKind.Thunderstorm:
                    return new WeatherSceneProfile
                    {
                        Kind = kind, DisplayName = "Thunderstorm",
                        CloudAmount = 1f, CloudDarkness = 0.85f,
                        Precip = PrecipKind.Rain, PrecipIntensity = 1f, Lightning = true, WindMs = 9f,
                        SkyZenith = new Color(0.10f, 0.11f, 0.18f),
                        SkyHorizon = new Color(0.22f, 0.23f, 0.33f),
                        SkyNadir = new Color(0.06f, 0.06f, 0.10f),
                        SunGlow = new Color(0.44f, 0.42f, 0.66f), SunGlowStrength = 0.5f,
                        SunColor = new Color(0.56f, 0.60f, 0.78f), SunIntensity = 0.4f,
                        SunElevation = 26f, SunAzimuth = 170f,
                        Ambient = new Color(0.22f, 0.24f, 0.34f),
                        FogColor = new Color(0.16f, 0.18f, 0.28f), FogDensity = 0.16f
                    };

                default: // Snow
                    return new WeatherSceneProfile
                    {
                        Kind = WeatherSceneKind.Snow, DisplayName = "Snow",
                        CloudAmount = 0.95f, CloudDarkness = 0.3f,
                        Precip = PrecipKind.Snow, PrecipIntensity = 0.7f, Lightning = false, WindMs = 3f,
                        SkyZenith = new Color(0.40f, 0.46f, 0.55f),
                        SkyHorizon = new Color(0.66f, 0.72f, 0.80f),
                        SkyNadir = new Color(0.22f, 0.26f, 0.32f),
                        SunGlow = new Color(0.80f, 0.84f, 0.92f), SunGlowStrength = 0.7f,
                        SunColor = new Color(0.86f, 0.90f, 0.98f), SunIntensity = 0.85f,
                        SunElevation = 34f, SunAzimuth = 160f,
                        Ambient = new Color(0.46f, 0.50f, 0.58f),
                        FogColor = new Color(0.62f, 0.68f, 0.78f), FogDensity = 0.10f
                    };
            }
        }
    }
}
